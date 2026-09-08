using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Scenariometer.Estimation;
using Scenariometer.Msq;

namespace Scenariometer.Tracking;

/// <summary>
/// Polls the game's completed-quest state, turns newly completed MSQ quests into
/// <see cref="QuestSample"/>s, and keeps the current <see cref="MsqProgress"/>.
///
/// Polling rather than hooking: quest completion has no stable public event, and a
/// full pass over the MSQ list is a few thousand bitfield reads. At one pass every
/// few seconds that is nothing, and the thing being measured takes minutes - the
/// poll interval is far below the noise floor of the estimate.
/// </summary>
internal sealed class ProgressTracker : IDisposable
{
    private const double PollSeconds = 5;

    /// <summary>
    /// What a quest finished while paused is worth. The real interval is unknowable -
    /// the clock was stopped for an unknown part of it - so this is a deliberate
    /// stand-in rather than a measurement, chosen to be a plausible quest rather than
    /// the near-zero the stopped clock would otherwise report.
    /// </summary>
    private const double ResumedQuestSeconds = 5 * 60;

    private readonly MsqIndex index;
    private readonly ActiveTimeClock clock;

    private HashSet<ushort> completed = [];
    private ulong currentContentId;
    private double sincePoll;

    /// <summary>
    /// Clock reading at the last MSQ turn-in. Null means "no baseline" - the state
    /// after a login or a character switch, where the time since the previous turn-in
    /// is unknown and inventing it would poison the average. It is nullable rather
    /// than a -1 sentinel because a baseline restored from a previous session is
    /// legitimately negative: the clock restarts at zero, so an interval that was
    /// already running started before it.
    /// </summary>
    private double? anchorSeconds;

    public QuestHistory? History { get; private set; }

    public MsqProgress Progress { get; private set; } = MsqProgress.Empty;

    /// <summary>Play time on the current quest so far; null while there is no baseline.</summary>
    public double? CurrentQuestSeconds =>
        anchorSeconds is null ? null : clock.ActiveSeconds - anchorSeconds.Value;

    /// <summary>Whether play time is currently accumulating. See <see cref="ActiveTimeClock"/>.</summary>
    public bool ClockRunning => clock.IsRunning;

    /// <summary>
    /// How many MSQ quests the index holds, independent of any character. Lets the UI
    /// tell a genuinely empty index apart from "no character loaded yet", which both
    /// show up as a zero total in <see cref="Progress"/>.
    /// </summary>
    public int IndexedQuestCount => index.Quests.Count;

    /// <summary>The character being tracked, or 0 before one is loaded.</summary>
    public ulong CurrentContentId => currentContentId;

    /// <summary>The day this character's target-date plan started, ISO; empty if none.</summary>
    public string PlanStartDate => Plugin.Config.TargetStartFor(currentContentId);

    public event Action<QuestSample>? SampleRecorded;

    public ProgressTracker(MsqIndex index, ActiveTimeClock clock)
    {
        this.index = index;
        this.clock = clock;
    }

    /// <summary>
    /// Begins polling. Deliberately not done in the constructor: Dalamud runs plugin
    /// constructors on a dedicated long-running thread while the framework thread
    /// keeps ticking, so a poll could otherwise reach SwitchCharacter - and through it
    /// the config, the history and SampleRecorded - while Plugin is still wiring
    /// itself up, and a first turn-in could land before anything was subscribed.
    /// </summary>
    public void Start() => Services.Framework.Update += OnUpdate;

    public void Dispose()
    {
        Services.Framework.Update -= OnUpdate;
        SaveSession();
    }

    /// <summary>
    /// Hands the in-flight interval to disk so a reload does not lose it. Only on
    /// unload: writing it every poll would be a file write every five seconds for a
    /// value read exactly once, and a crash losing it is no worse than before.
    /// </summary>
    private void SaveSession()
    {
        if (currentContentId == 0)
        {
            // Unloaded before the first poll got as far as identifying the character,
            // which a rebuild-on-save loop does routinely. We know nothing here, so
            // leave whatever is on disk alone - clearing it would throw away a good
            // interval saved by the previous unload.
            return;
        }

        if (anchorSeconds is null || Progress.Current is null)
        {
            // Character loaded and genuinely nothing in flight - make sure an older
            // file cannot be applied later.
            SessionState.Clear();
            return;
        }

        new SessionState(
            currentContentId,
            Progress.Current.RowId,
            clock.ActiveSeconds - anchorSeconds.Value).Save();
    }

    private void OnUpdate(IFramework framework)
    {
        sincePoll += framework.UpdateDelta.TotalSeconds;
        if (sincePoll < PollSeconds)
            return;

        sincePoll = 0;

        try
        {
            Poll();
        }
        catch (Exception ex)
        {
            // An exception thrown into the framework tick is a hard client problem;
            // swallow it here and keep the plugin passive instead.
            Services.Log.Error(ex, "MSQ poll failed.");
        }
    }

    private void Poll()
    {
        if (!Services.ClientState.IsLoggedIn)
        {
            anchorSeconds = null;

            // Dropped here rather than left standing until the next character loads.
            // Consumers gate on IsLoggedIn, which flips the instant a character comes
            // in, while this poll runs only every few seconds - so the window and the
            // IPC snapshot would otherwise report the PREVIOUS character's progress,
            // history and plan as the new one's. With "open on login" on, that stale
            // reading is the first thing shown.
            currentContentId = 0;
            completed = [];
            Progress = MsqProgress.Empty;
            History = null;
            return;
        }

        var contentId = Services.PlayerState.ContentId;
        if (contentId == 0)
            return;

        if (contentId != currentContentId)
        {
            SwitchCharacter(contentId);
            return;
        }

        var current = ScanCompleted();
        var newly = index.Quests
            .Where(q => current.Contains(q.ShortId) && !completed.Contains(q.ShortId))
            .OrderBy(q => q.Order)
            .ToList();

        completed = current;

        // Only when something actually changed. The scan above has to run every poll
        // to notice a turn-in at all, but BuildProgress is several more passes over
        // the index with grouping and sorting, and for most polls it recomputes a
        // breakdown identical to the one already on screen. The Total check covers the
        // first poll after a login or a character switch, where nothing is "newly"
        // complete but the breakdown does not exist yet.
        if (newly.Count > 0 || Progress.Total == 0)
            Progress = BuildProgress(current);

        if (newly.Count > 0)
            RecordCompletions(newly);

        // Deliberately does NOT set a baseline here. Doing so on the first poll after
        // a login, a character switch or a plugin reload would make the next turn-in
        // measurable - but timed from that moment rather than from the previous
        // turn-in, which silently records a too-short sample and drags the median
        // down. The baseline is set by the first turn-in instead (see
        // RecordCompletions), which is the only point where the interval is known.
    }

    private void SwitchCharacter(ulong contentId)
    {
        currentContentId = contentId;
        History = QuestHistory.Load(contentId);
        completed = ScanCompleted();
        Progress = BuildProgress(completed);

        // No baseline yet: the next turn-in only establishes one, it is not measured -
        // unless a previous session left one to pick back up.
        anchorSeconds = null;
        RestoreSession(contentId);

        // A target set on another character - or before plan starts were per character
        // - leaves this one with no plan of its own. Start it today rather than
        // measuring it against days this character never played.
        if (DailyGoal.TryParse(Plugin.Config.TargetDate, out _)
            && !DailyGoal.TryParse(Plugin.Config.TargetStartFor(contentId), out _))
        {
            Plugin.Config.SetTargetStart(
                contentId,
                DailyGoal.LogicalDate(DateTimeOffset.Now, Plugin.Config.DayStartHour)
                    .ToString(DailyGoal.IsoFormat));

            Plugin.Config.Save();
        }
    }

    /// <summary>
    /// Picks the in-flight measurement back up after a reload, but only when it is
    /// still about the same character on the same quest. A quest finished while the
    /// plugin was unloaded would otherwise have its interval handed to whatever quest
    /// came next.
    /// </summary>
    private void RestoreSession(ulong contentId)
    {
        var saved = SessionState.Load();

        // One use only, whether or not it applied.
        SessionState.Clear();

        if (saved is null || saved.ContentId != contentId)
            return;

        if (Progress.Current is null || Progress.Current.RowId != saved.CurrentQuestRowId)
            return;

        anchorSeconds = clock.ActiveSeconds - saved.ElapsedSeconds;

        Services.Log.Information(
            "Resumed the measurement on {Quest} at {Seconds:0}s.",
            Progress.Current.Name,
            saved.ElapsedSeconds);
    }

    private void RecordCompletions(List<MsqQuest> newly)
    {
        // The first turn-in after a login has no interval to measure, but it did
        // happen, and the daily-goal count is a count of completions rather than of
        // timings. So it is still recorded - as an outlier, which is exactly what
        // that flag means: kept in the log, kept out of the pace.
        var unmeasured = anchorSeconds is null;

        // Finishing an MSQ quest while paused means the pause was forgotten rather
        // than meant - not measuring the MSQ is the entire purpose of it. Resume, and
        // credit the quest a nominal time: the clock was stopped for an unknown part
        // of it, so what it actually reports is near zero and worse than a guess.
        var wasPaused = Plugin.Config.Paused;
        if (wasPaused)
        {
            Plugin.Config.Paused = false;
            Plugin.Config.Save();

            Services.Chat.Print(
                "[Scenariometer] Tracking resumed - an MSQ quest was completed while paused. "
                + $"Counted as {ResumedQuestSeconds / 60:0} minutes.");
        }

        var elapsed = wasPaused
            ? ResumedQuestSeconds
            : unmeasured ? 0 : clock.ActiveSeconds - anchorSeconds!.Value;
        var threshold = Plugin.Config.OutlierMinutes * 60.0;

        // Several quests completed inside one poll (or while logged out on another
        // client): the interval cannot be attributed to any one of them, so they are
        // all recorded for the log and none of them count toward the pace.
        var ambiguous = newly.Count > 1;

        // A resumed quest does count, unlike a first-of-session one: it carries a
        // stand-in time rather than no time at all.
        var usable = (!unmeasured || wasPaused) && !ambiguous;

        var samples = newly
            .Select(quest => new QuestSample(
                QuestRowId: quest.RowId,
                QuestName: quest.Name,
                ExpansionId: quest.ExpansionId,
                CompletedUnix: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ActiveSeconds: usable ? elapsed : 0,
                Outlier: !usable || elapsed > threshold || elapsed <= 0))
            .ToList();

        // Persist the batch before announcing any of it: subscribers recompute the
        // estimate from the history, and would otherwise read it mid-batch.
        History?.AddRange(samples);

        // Before the callbacks, not after. Poll has already advanced `completed`, so
        // this turn-in can never be seen again - and a subscriber that throws (this
        // one prints to chat) would otherwise leave the baseline pointing at the
        // PREVIOUS turn-in, making the next sample measure two quests' play time and
        // then quietly discarding it as an outlier. Failing costs one sample, not two.
        anchorSeconds = clock.ActiveSeconds;

        foreach (var sample in samples)
            SampleRecorded?.Invoke(sample);
    }

    /// <summary>
    /// Lists MSQ quests still incomplete that sit before the furthest quest this
    /// character has finished - "holes" in what should be a linear run. Behind
    /// "/msq debug missing". Any hole means the index counts a quest this character
    /// can never complete, which caps the percentage below 100 forever.
    /// </summary>
    public string DumpHoles()
    {
        // Every other ScanCompleted caller sits behind this; the command handler is
        // reachable from the title screen, where there is no quest state to read.
        if (!Services.ClientState.IsLoggedIn)
            return "Not logged in - quest state can only be read with a character loaded.";

        var done = ScanCompleted();
        var furthest = index.Quests
            .Where(q => done.Contains(q.ShortId))
            .Select(q => q.Order)
            .DefaultIfEmpty(-1)
            .Max();

        var lines = new List<string>
        {
            $"Completed {done.Count}/{index.Quests.Count}; furthest completed is order {furthest}.",
            "Per genre (completed/total):",
        };

        foreach (var group in index.Quests.GroupBy(q => q.GenreId).OrderBy(g => g.Min(q => q.Order)))
        {
            lines.Add($"  genre {group.Key} \"{group.First().GenreName}\": "
                + $"{group.Count(q => done.Contains(q.ShortId))}/{group.Count()}");
        }

        var holes = index.Quests
            .Where(q => q.Order < furthest && !done.Contains(q.ShortId))
            .OrderBy(q => q.Order)
            .ToList();

        lines.Add($"Holes before the furthest completed quest: {holes.Count}");
        foreach (var quest in holes.Take(80))
            lines.Add($"  [{quest.Order}] {quest.Name} (genre {quest.GenreId})");
        if (holes.Count > 80)
            lines.Add($"  ... and {holes.Count - 80} more");

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Where consecutive groups divide a bar, as fractions of it. The last boundary
    /// is dropped: it always lands at 1.0, which is the end of the bar rather than a
    /// division within it.
    /// </summary>
    private static IReadOnlyList<float> Divisions(IEnumerable<IGrouping<uint, MsqQuest>> groups, int total)
    {
        if (total <= 0)
            return [];

        var ordered = groups.OrderBy(group => group.Min(q => q.Order)).ToList();
        var marks = new List<float>();
        var running = 0;

        foreach (var group in ordered.Take(ordered.Count - 1))
        {
            running += group.Count();
            marks.Add((float)running / total);
        }

        return marks;
    }

    private HashSet<ushort> ScanCompleted()
    {
        var done = new HashSet<ushort>();
        foreach (var quest in index.Quests)
        {
            if (QuestState.IsComplete(quest.ShortId))
                done.Add(quest.ShortId);
        }

        return done;
    }

    private MsqProgress BuildProgress(HashSet<ushort> done)
    {
        // The MSQ is not one linear list. The three starting cities are mutually
        // exclusive branches, so a character completes one opening and can never
        // complete the other two - 55 quests that stay incomplete for good. Counting
        // them caps A Realm Reborn below 100% forever, and inflates "remaining", and
        // so the estimate, by that many quests.
        //
        // Rather than model the branch graph (which is what chain-walking PreviousQuest
        // would be for, and it branches for class starts too), treat a quest as on this
        // character's path if it is either already done or still ahead of them. An
        // incomplete quest sitting behind the furthest one they have finished is a road
        // not taken. That covers the city splits, class-specific starts and story-skip
        // potions alike, without knowing anything about them.
        var furthest = -1;
        foreach (var quest in index.Quests)
        {
            if (done.Contains(quest.ShortId) && quest.Order > furthest)
                furthest = quest.Order;
        }

        var onPath = index.Quests
            .Where(q => done.Contains(q.ShortId) || q.Order > furthest)
            .ToList();

        // Story order, not id order, because the marks below are positions along a
        // bar that runs left to right in story order.
        var byExpansion = onPath
            .GroupBy(q => q.ExpansionId)
            .OrderBy(group => group.Min(q => q.Order))
            .ToList();

        var expansions = byExpansion
            .Select(group => new ExpansionProgress(
                group.Key,
                group.First().ExpansionName,
                group.Count(q => done.Contains(q.ShortId)),
                group.Count(),
                // The expansion's own icon, from ExVersion.
                group.First().ExpansionIcon,
                Divisions(group.GroupBy(q => q.GenreId), group.Count())))
            .ToList();

        // First incomplete quest on the path - not simply the first incomplete quest
        // in the index, which would be a skipped starting-city quest.
        var current = onPath.FirstOrDefault(q => !done.Contains(q.ShortId));

        return new MsqProgress(
            done.Count,
            onPath.Count,
            current,
            expansions,
            Divisions(byExpansion, onPath.Count));
    }
}
