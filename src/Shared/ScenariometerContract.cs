using System;
using System.Collections.Generic;

// This file is MIT, like the plugin it belongs to - but it is <Compile Include>-linked
// into the Umbra widget as well, and that assembly as a whole is AGPL-3.0-or-later
// because Umbra is. MIT permits that direction. See LICENSING.md at the repo root.

namespace Scenariometer.Contract;

/// <summary>
/// The IPC contract between the Dalamud plugin and any consumer - currently the
/// Umbra widget in src/Scenariometer.Umbra.
///
/// This file is not a shared assembly; it is <c>&lt;Compile Include&gt;</c>-linked into
/// both projects, so each side compiles its own copy. That is deliberate: the two
/// run in different assembly load contexts, where a type from one is NOT the same
/// type to the other. Only primitives and strings cross the gate, and the payload
/// travels as JSON that both sides parse into their own copy of
/// <see cref="ScenariometerSnapshot"/>.
///
/// Changing the shape of the snapshot in a way older consumers cannot read means
/// bumping <see cref="ContractVersion"/>; consumers check it before trusting a
/// payload, since the two DLLs are installed and updated independently.
/// </summary>
internal static class ScenariometerIpc
{
    // v2 added SetPausedGate; v3 added the daily-goal fields; v4 made ahead/behind a
    // sent value rather than one derived from today; v5 added the expansion breakdown
    // and the bar divisions, so a consumer can draw the whole picture rather than a
    // line of it. Consumers match on this exactly, so the widget and the plugin have
    // to be updated together - the point of it.
    public const int ContractVersion = 5;

    /// <summary>Returns <see cref="ContractVersion"/>. Also doubles as "is it running".</summary>
    public const string ApiVersionGate = "Scenariometer.ApiVersion";

    /// <summary>Returns a JSON <see cref="ScenariometerSnapshot"/>.</summary>
    public const string SnapshotGate = "Scenariometer.GetSnapshot";

    /// <summary>
    /// Sets the manual pause. Takes the desired state, returns the state afterwards,
    /// so a caller does not have to wait for the next snapshot to know it took.
    /// </summary>
    public const string SetPausedGate = "Scenariometer.SetPaused";
}

/// <summary>
/// One expansion's slice of the Main Scenario, so a consumer can draw the same
/// breakdown the plugin window does without owning an MSQ index of its own.
/// </summary>
internal sealed class ScenariometerExpansion
{
    public string Name { get; set; } = string.Empty;

    /// <summary>The expansion's own journal icon, from the ExVersion sheet.</summary>
    public uint IconId { get; set; }

    public int Completed { get; set; }
    public int Total { get; set; }

    /// <summary>The expansion the current quest belongs to. At most one is set.</summary>
    public bool IsCurrent { get; set; }

    /// <summary>Chapter boundaries within this expansion, as fractions of its bar.</summary>
    public float[] Marks { get; set; } = [];

    public int Remaining => Total - Completed;

    public float Fraction => Total == 0 ? 0f : (float)Completed / Total;
}

/// <summary>
/// A plain property bag with defaults rather than a positional record, because that is
/// what System.Text.Json can deserialise into.
///
/// The defaults are not a compatibility mechanism, whatever they look like. Consumers
/// check <see cref="ScenariometerIpc.ApiVersionGate"/> for an exact match before
/// deserialising, so a payload written against a different contract never reaches
/// them and a missing member never gets the chance to degrade. Turning that into real
/// tolerance means gating on a minimum version instead of an exact one - a change to
/// how the two DLLs are shipped, not to this class.
/// </summary>
internal sealed class ScenariometerSnapshot
{
    public int ContractVersion { get; set; }

    public bool LoggedIn { get; set; }

    /// <summary>The user has paused tracking by hand; the clock and samples are frozen.</summary>
    public bool Paused { get; set; }

    // --- progress ---
    public int Completed { get; set; }
    public int Total { get; set; }
    public int Remaining { get; set; }

    public string CurrentQuest { get; set; } = string.Empty;
    public string CurrentChapter { get; set; } = string.Empty;

    public string ExpansionName { get; set; } = string.Empty;
    public int ExpansionCompleted { get; set; }
    public int ExpansionTotal { get; set; }
    public int ExpansionRemaining { get; set; }

    /// <summary>
    /// Every expansion in story order, whether or not the consumer chooses to draw
    /// them. Sent unconditionally rather than gated on the plugin's own breakdown
    /// setting: that setting is about the plugin window, and a consumer showing or
    /// hiding its own list is a separate decision.
    /// </summary>
    public List<ScenariometerExpansion> Expansions { get; set; } = [];

    /// <summary>Where one expansion ends and the next begins, as fractions of the overall bar.</summary>
    public float[] Marks { get; set; } = [];

    // --- estimate ---
    /// <summary>False while there are too few samples; the seconds below are then 0.</summary>
    public bool HasEstimate { get; set; }
    public int SampleCount { get; set; }
    public double PaceSeconds { get; set; }

    /// <summary>
    /// How many samples the plugin wants before it will estimate. Sent rather than
    /// assumed, because it is a user setting - a consumer counting "3 of 5" against
    /// its own hardcoded 5 would be wrong for anyone who changed it.
    /// </summary>
    public int MinimumSamples { get; set; }

    public double RemainingSeconds { get; set; }
    public double RemainingSecondsLow { get; set; }
    public double RemainingSecondsHigh { get; set; }
    public double ExpansionRemainingSeconds { get; set; }

    /// <summary>Play time on the current quest so far; 0 when there is no baseline yet.</summary>
    public double CurrentQuestSeconds { get; set; }

    // --- daily goal ---
    /// <summary>False when no target date is set; every field below is then 0.</summary>
    public bool HasTarget { get; set; }

    /// <summary>The target, as "yyyy-MM-dd". A date, so it crosses the gate as text.</summary>
    public string TargetDate { get; set; } = string.Empty;

    /// <summary>Days left including today; 0 once the date has passed.</summary>
    public int DaysRemaining { get; set; }

    public int CompletedToday { get; set; }
    public int QuotaToday { get; set; }
    public int RemainingToday { get; set; }

    /// <summary>The target date has passed with MSQ still to do.</summary>
    public bool Overdue { get; set; }

    /// <summary>Today's share is done. Not the same as the MSQ being finished.</summary>
    public bool DoneForToday { get; set; }

    /// <summary>
    /// How much of today's share is done, for a progress bar.
    ///
    /// Zero with no target date set. A quota of zero otherwise means "yesterday's
    /// surplus covers today", which is genuinely complete - but with no target at all
    /// the quota is also zero, and returning a full bar for that had the widget
    /// sitting at 100% forever beside a label correctly reading "No target".
    /// </summary>
    public float TodayFraction =>
        !HasTarget ? 0f
        : QuotaToday == 0 ? 1f
        : Math.Min(1f, (float)CompletedToday / QuotaToday);

    /// <summary>
    /// How far ahead of the plan the character is, in quests; negative is behind.
    /// Sent rather than derived: it is cumulative across the days since the target
    /// was set, which the per-day fields cannot express.
    /// </summary>
    public int AheadBy { get; set; }

    public float Fraction => Total == 0 ? 0f : (float)Completed / Total;

    public float ExpansionFraction => ExpansionTotal == 0 ? 0f : (float)ExpansionCompleted / ExpansionTotal;
}

/// <summary>
/// Duration formatting, shared with consumers so the plugin window, the chat line
/// and the Umbra widget cannot disagree about how long "4h 12m" is.
/// </summary>
internal static class ScenariometerFormat
{
    /// <summary>"3d 4h", "4h 12m", "12m", "48s" - compact, never more than two units.</summary>
    public static string Duration(TimeSpan span)
    {
        if (span <= TimeSpan.Zero)
            return "0m";

        if (span.TotalDays >= 1)
            return $"{(int)span.TotalDays}d {span.Hours}h";

        if (span.TotalHours >= 1)
            return $"{(int)span.TotalHours}h {span.Minutes}m";

        if (span.TotalMinutes >= 1)
            return $"{(int)span.TotalMinutes}m";

        return $"{(int)span.TotalSeconds}s";
    }

    public static string Duration(double seconds) => Duration(TimeSpan.FromSeconds(seconds));

    /// <summary>
    /// "3 quests ahead" / "2 quests behind" / "on schedule", measured against the whole
    /// plan rather than today - see AheadBy on the snapshot.
    /// </summary>
    public static string PlanStanding(int aheadBy) => aheadBy switch
    {
        > 0 => $"{aheadBy} {Quests(aheadBy)} ahead",
        < 0 => $"{-aheadBy} {Quests(-aheadBy)} behind",
        _ => "on schedule",
    };

    public static string Quests(int count) => count == 1 ? "quest" : "quests";

    public static string Days(int count) => count == 1 ? "day" : "days";
}
