using System;
using System.Collections.Generic;
using Dalamud.Configuration;

namespace Scenariometer;

/// <summary>
/// User settings. Small and stable on purpose - the measured quest samples are NOT
/// stored here (they grow without bound and would be rewritten on every settings
/// change); they live in their own per-character file, see
/// <see cref="Tracking.QuestHistory"/>.
/// </summary>
[Serializable]
internal sealed class Configuration : IPluginConfiguration
{
    /// <summary>Schema version; bump and migrate in <see cref="Migrate"/> on breaking changes.</summary>
    public int Version { get; set; } = 2;

    // --- Measurement -------------------------------------------------------

    /// <summary>Stop the clock while the character is flagged Away from Keyboard.</summary>
    public bool PauseWhileAfk { get; set; } = true;

    /// <summary>
    /// Manual pause, toggled from the main window. Persisted deliberately: a pause
    /// that quietly lifted itself on the next login would fold whatever the player
    /// stepped away to do into the next sample, which is the exact measurement the
    /// pause exists to prevent. The window says so plainly while it is on.
    /// </summary>
    public bool Paused { get; set; }

    /// <summary>
    /// A quest that took longer than this is treated as an outlier (raid night in the
    /// middle of the MSQ, alt-tabbed for an hour without going AFK) and excluded from
    /// the pace calculation. It is still recorded, just flagged.
    /// </summary>
    public int OutlierMinutes { get; set; } = 90;

    /// <summary>How many recent samples the pace is computed from. 0 = all of them.</summary>
    public int PaceWindow { get; set; } = 30;

    /// <summary>Below this many usable samples the window shows "not enough data yet".</summary>
    public int MinimumSamples { get; set; } = 5;

    // --- Target date -------------------------------------------------------

    /// <summary>
    /// Goal date for finishing the MSQ, as "yyyy-MM-dd". Empty means no target.
    /// Stored as a string rather than a DateTime so the value in the config file
    /// stays readable and timezone-free - it is a calendar date, not an instant.
    /// </summary>
    public string TargetDate { get; set; } = string.Empty;

    /// <summary>
    /// The day the plan started, ISO, per character - keyed by content id the same way
    /// the history files are.
    ///
    /// Per character and not global, unlike the target date itself: the carry-over is
    /// measured against a character's own history, so one plan start shared by
    /// everyone judges every other character against days it never played. An alt
    /// picked up a month after the target was set would read as hundreds behind.
    ///
    /// Reset whenever the target changes - a new target is a new plan.
    /// </summary>
    public Dictionary<string, string> TargetStartDates { get; set; } = [];

    /// <summary>The plan start for one character, or empty when it has none yet.</summary>
    public string TargetStartFor(ulong contentId) =>
        TargetStartDates.TryGetValue(CharacterKey(contentId), out var start) ? start : string.Empty;

    public void SetTargetStart(ulong contentId, string isoDate)
    {
        if (contentId == 0)
            return;

        if (isoDate.Length == 0)
            TargetStartDates.Remove(CharacterKey(contentId));
        else
            TargetStartDates[CharacterKey(contentId)] = isoDate;
    }

    /// <summary>Clears every character's plan. A new target is a new plan for all of them.</summary>
    public void ClearTargetStarts() => TargetStartDates.Clear();

    private static string CharacterKey(ulong contentId) => contentId.ToString("X16");

    /// <summary>
    /// How dates are shown and typed. The stored <see cref="TargetDate"/> stays ISO
    /// whatever this is set to, so the config file is portable and a change here
    /// never invalidates a date already set.
    /// </summary>
    public string DateFormat { get; set; } = Estimation.DailyGoal.GermanFormat;

    /// <summary>
    /// The hour a new day starts, for counting "quests done today". A session that
    /// runs past midnight is one evening, not two days, so the default boundary is
    /// early morning rather than 00:00.
    /// </summary>
    public int DayStartHour { get; set; } = 6;

    // --- UI ----------------------------------------------------------------

    public bool OpenOnLogin { get; set; }
    public bool ShowExpansionBreakdown { get; set; } = true;

    /// <summary>Announce the new estimate in chat after each MSQ quest turn-in.</summary>
    public bool ChatOnQuestComplete { get; set; }

    public void Save() => Services.PluginInterface.SavePluginConfig(this);

    /// <summary>Applied once at load, before the config is handed to anything else.</summary>
    public Configuration Migrate()
    {
        // Version 2 moved the plan start from one shared date to one per character.
        // The old value cannot be attributed to a character, so it is dropped; each
        // character re-establishes its own plan the next time it is loaded.
        if (Version < 2)
        {
            TargetStartDates.Clear();
            Version = 2;
        }

        return this;
    }
}
