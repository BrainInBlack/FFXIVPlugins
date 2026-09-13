using System;
using System.Numerics;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using KamiToolKit;
using Scenariometer.Contract;
using Scenariometer.Estimation;
using Scenariometer.Ipc;
using Scenariometer.Msq;
using Scenariometer.Tracking;
using Scenariometer.Windows;

namespace Scenariometer;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/msq";

    /// <summary>Both windows are real game windows now; no ImGui is left.</summary>
    private readonly NativeMainWindow nativeWindow;
    private readonly NativeConfigWindow nativeConfigWindow;
    private readonly NativeDatePickerWindow datePicker;
    private readonly NativeConfirmWindow confirmWindow;

    private readonly MsqIndex index;
    private readonly ActiveTimeClock clock;
    private readonly ProgressTracker tracker;
    private readonly IpcProvider ipc;

    internal static Configuration Config { get; private set; } = null!;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        pluginInterface.Create<Services>();

        Config = (Services.PluginInterface.GetPluginConfig() as Configuration ?? new Configuration()).Migrate();


        // Built synchronously: it is one pass over the Quest sheet, and every other
        // component needs it. If this ever shows up in load times, move it to a Task
        // and gate the windows on it - nothing here is needed before the first tick.
        index = MsqIndex.Build();

        clock = new ActiveTimeClock();
        tracker = new ProgressTracker(index, clock);
        tracker.SampleRecorded += OnSampleRecorded;

        // Published before the UI exists: a consumer that is already loaded (Umbra
        // reloading its plugins) should find the gates as soon as we are alive.
        ipc = new IpcProvider(tracker);

        // Must come before any node is built; KamiToolKit warns about nothing if it
        // is skipped, it simply misbehaves.
        //
        // Blocked on deliberately, in a constructor, which normally invites a deadlock.
        // InitializeAsync really does suspend - it awaits a config file load and then
        // IFramework.Run(NativeAddon.InitializeCloseCallback), which completes on the
        // framework thread.
        //
        // It is safe because this constructor does NOT run on the framework thread.
        // Dalamud invokes plugin constructors through
        // ServiceContainer.CreateAsync -> Task.Factory.StartNew(..., LongRunning), on
        // a dedicated thread, and its own comment there sanctions the pattern:
        // "Legacy (IDalamudPlugin) plugin ctors can block to wait for Tasks, as the
        // ctor is their only init." The framework thread stays free to tick, so the
        // awaited continuation can run and this call returns.
        //
        // THE INVARIANT THAT KEEPS IT SAFE: this plugin must not declare LoadSync.
        // With LoadSync set and LoadRequiredState 0 or 1, Dalamud runs the constructor
        // ON the framework thread instead - and then waiting here for a framework tick
        // deadlocks the game on load. Do not add it.
        //
        // The v15-sanctioned alternative is IAsyncDalamudPlugin.LoadAsync, which exists
        // precisely to avoid blocking like this. Worth migrating to; not required, and
        // deliberately not done in the same change as the library bump.
        KamiToolKitLibrary.InitializeAsync(Services.PluginInterface, "Scenariometer")
            .GetAwaiter()
            .GetResult();

        // Title is the window, Subtitle is the plugin - the game's own convention, and
        // KamiToolKit defaults the subtitle to the name passed to Initialize. Setting
        // both to "Scenariometer" printed it twice in the title bar.
        nativeWindow = new NativeMainWindow(tracker, ToggleConfigWindow)
        {
            InternalName = "Scenariometer",
            Title = "Main Scenario",
            Size = new Vector2(400f, 560f),
        };

        datePicker = new NativeDatePickerWindow(OnDatePicked)
        {
            InternalName = "ScenariometerDate",
            Title = "Target date",
            Size = new Vector2(290f, 318f),
        };

        confirmWindow = new NativeConfirmWindow
        {
            InternalName = "ScenariometerAsk",
            // The question itself, so the frame says what is being decided even before
            // the lines inside it are read.
            Title = "Change the plan start?",
            // Sized to the question, not to the frame it could fill: at 380 the
            // longest line covered under half the content box and the whole thing
            // read as mostly empty window. The height is exact, like the others -
            // NativeConfirmWindow.OnSetup warns with the right number if the
            // statement, the question, the button row and the margin stop adding up.
            Size = new Vector2(310f, 172f),
        };

        nativeConfigWindow = new NativeConfigWindow(ClearHistory, OpenDatePicker, SetTarget)
        {
            // Distinct from the main window's, and inside the 31 character limit.
            InternalName = "ScenariometerCfg",
            // "Settings" alone: the subtitle beside it is already the plugin name, and
            // "Scenariometer Settings   Scenariometer" said it twice.
            Title = "Settings",
            // Exact, not approximate: resizing an open window does not redraw its
            // frame, so a mismatch either clips the last control or leaves dead space
            // until the next open. NativeConfigWindow.OnSetup warns with the right
            // number whenever the content stops matching this.
            Size = new Vector2(480f, 590f),
        };

        Services.Commands.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the MSQ progress window. Also: /msq config, /msq reset, "
                + "/msq debug sections, /msq debug expansions, /msq debug missing.",
        });

        Services.PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigWindow;
        Services.PluginInterface.UiBuilder.OpenMainUi += ToggleMainWindow;

        Services.ClientState.Login += OnLogin;

        // Last, on purpose. Both of these subscribe to the framework tick, which runs
        // on a different thread from this constructor - see the note on the blocking
        // call above. Starting them here means no tick can observe a half-built
        // plugin, and no turn-in can land before SampleRecorded is attached.
        clock.Start();
        tracker.Start();
    }

    public void Dispose()
    {
        Services.ClientState.Login -= OnLogin;
        Services.PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigWindow;
        Services.PluginInterface.UiBuilder.OpenMainUi -= ToggleMainWindow;
        Services.Commands.RemoveHandler(CommandName);

        // Before KamiToolKitLibrary.Dispose: the addons own native nodes that the
        // library tears the rest of down for.
        nativeWindow.Dispose();
        nativeConfigWindow.Dispose();
        datePicker.Dispose();
        confirmWindow.Dispose();
        KamiToolKitLibrary.Dispose();

        tracker.SampleRecorded -= OnSampleRecorded;
        ipc.Dispose();
        tracker.Dispose();
        clock.Dispose();
    }

    private void ToggleMainWindow() => nativeWindow.Toggle();

    private void ToggleConfigWindow() => nativeConfigWindow.Toggle();

    /// <summary>
    /// Sets the target, and asks what should become of the plan already running.
    ///
    /// A new date means one of two things and which one is not guessable from the
    /// date. Moving the deadline out on a plan under way keeps its carry, so a deficit
    /// shrinks instead of disappearing - that is the point of moving it. Starting over
    /// measures from today and throws the carry away. This used to assume the second
    /// one silently, including when the "new" date was the one already set, which made
    /// retyping a target a way to lose its history without touching anything labelled
    /// as doing that.
    ///
    /// The date itself is never held up by the question: it is applied at once, and
    /// only the plan start waits for an answer.
    /// </summary>
    private void SetTarget(DateOnly? date)
    {
        if (date is null)
        {
            // No target is no plan, so there is no start worth keeping and nothing to
            // ask. Clearing is already deliberate - it is a button that says so.
            Config.ClearTargetStarts();
            Config.TargetDate = string.Empty;
            return;
        }

        var iso = date.Value.ToString(DailyGoal.IsoFormat);

        // The date already set is not a new plan, and re-entering it is not a request
        // to restart one. Typing into the field re-fires this on every commit.
        if (Config.TargetDate == iso)
            return;

        Config.TargetDate = iso;

        var today = DailyGoal.LogicalDate(DateTimeOffset.Now, Config.DayStartHour);

        // Nothing to lose: no plan has run on this character yet, or the one that has
        // began today, so restarting it lands on the same day it already starts. Both
        // would be a question with one meaningful answer.
        if (!DailyGoal.TryParse(Config.TargetStartFor(tracker.CurrentContentId), out var start)
            || start >= today)
        {
            RestartPlan(today);
            return;
        }

        // Captured, not read back at answer time. The window can outlive the character
        // it is asking about.
        var character = tracker.CurrentContentId;

        var banked = DailyGoal.CompletedSince(
            tracker.History,
            start,
            Config.DayStartHour,
            DateTimeOffset.Now);

        confirmWindow.Ask(
            [
                $"This plan has run since {DailyGoal.Format(start)}.",
                // No verb, so it survives the singular: "1 quest are measured" is the
                // same agreement bug the report strings have already produced once.
                $"{banked} {ScenariometerFormat.Quests(banked)} counted against it so far.",
            ],
            question: "Keep it, or start over from today?",
            confirm: "Restart today",
            cancel: "Keep the plan",
            answer =>
            {
                // A logout or a character switch while the question sat open. It was
                // asked about that character's plan, and every answer to it - keeping
                // included - would land on whoever is loaded now. ProgressTracker has
                // already established a start for them; leave it alone.
                if (tracker.CurrentContentId != character)
                    return;

                // Recomputed rather than captured. The window can sit unanswered
                // across the day-start hour, and the plan would then restart on a day
                // that is no longer today.
                if (answer)
                    RestartPlan(DailyGoal.LogicalDate(DateTimeOffset.Now, Config.DayStartHour));

                // The caller saved the date long ago; this is a second, later change.
                Config.Save();
                nativeConfigWindow.RefreshTarget();
            });
    }

    /// <summary>
    /// Starts the plan over on the given day.
    ///
    /// Every character's start goes, not only this one's: surplus banked against the
    /// old plan must not follow it across, and the others re-establish theirs when
    /// they are next loaded.
    /// </summary>
    private void RestartPlan(DateOnly today)
    {
        Config.ClearTargetStarts();
        Config.SetTargetStart(tracker.CurrentContentId, today.ToString(DailyGoal.IsoFormat));
    }

    /// <summary>Opens the calendar on whatever date is already set.</summary>
    private void OpenDatePicker() =>
        datePicker.OpenAt(DailyGoal.TryParse(Config.TargetDate, out var current) ? current : null);

    /// <summary>
    /// The calendar lives in its own window, so the settings window has to be told to
    /// re-read the value it is displaying.
    /// </summary>
    private void OnDatePicked(DateOnly? date)
    {
        SetTarget(date);
        Config.Save();
        nativeConfigWindow.RefreshTarget();
    }

    private void OnLogin()
    {
        if (Config.OpenOnLogin)
            nativeWindow.Open();
    }

    private void OnCommand(string command, string arguments)
    {
        switch (arguments.Trim().ToLowerInvariant())
        {
            case "":
                ToggleMainWindow();
                break;

            case "config":
            case "settings":
                ToggleConfigWindow();
                break;

            case "reset":
                Services.Chat.Print(ClearHistory()
                    ? "[Scenariometer] Measured quest history cleared for this character."
                    : "[Scenariometer] No history to clear - log in to a character first.");
                break;

            case "debug sections":
                Services.Log.Information("JournalSections:{NewLine}{Sections}", Environment.NewLine, MsqIndex.DumpSections());
                Services.Chat.Print("[Scenariometer] Journal sections written to the Dalamud log.");
                break;

            case "debug expansions":
                Services.Log.Information("MSQ breakdown:{NewLine}{Breakdown}", Environment.NewLine, index.DumpBreakdown());
                Services.Chat.Print("[Scenariometer] MSQ breakdown written to the Dalamud log.");
                break;

            case "debug missing":
                Services.Log.Information("MSQ holes:{NewLine}{Holes}", Environment.NewLine, tracker.DumpHoles());
                Services.Chat.Print("[Scenariometer] Incomplete-quest report written to the Dalamud log.");
                break;

            default:
                Services.Chat.Print($"[Scenariometer] Unknown argument '{arguments}'. Try: config, reset, debug sections, debug expansions, debug missing.");
                break;
        }
    }

    /// <summary>
    /// Wipes this character's measured history. False when there is none to wipe -
    /// at the title screen, or in the seconds after login before the first poll has
    /// loaded it. Callers report the outcome, so this must not claim a success that
    /// did not happen: the button and the command both offer it as irreversible.
    /// </summary>
    private bool ClearHistory()
    {
        if (tracker.History is null)
            return false;

        tracker.History.Clear();
        return true;
    }

    private void OnSampleRecorded(QuestSample sample)
    {
        Services.Log.Debug(
            "Recorded {Quest} in {Seconds:0}s (outlier: {Outlier}).",
            sample.QuestName,
            sample.ActiveSeconds,
            sample.Outlier);

        if (!Config.ChatOnQuestComplete || sample.Outlier)
            return;

        var progress = tracker.Progress;
        var estimate = Estimator.Compute(tracker.History);

        if (estimate.SampleCount < Config.MinimumSamples)
            return;

        var (_, mid, _) = estimate.For(progress.Remaining);
        Services.Chat.Print(
            $"[Scenariometer] {progress.Completed}/{progress.Total} MSQ - about {Estimator.Format(mid)} left.");
    }
}
