using System;
using System.Collections.Generic;

namespace Scenariometer.Contract;

/// <summary>One label/value pair as it will be painted, or nothing at all.</summary>
internal readonly record struct ReportRow(string Label, string Value)
{
    /// <summary>Nothing to say. Both renderers hide the row they would have used.</summary>
    public static readonly ReportRow None = new(string.Empty, string.Empty);

    public bool IsNone => Label.Length == 0 && Value.Length == 0;
}

/// <summary>The pace section, in the order it is drawn.</summary>
internal readonly record struct PaceReport(ReportRow Pace, ReportRow Expansion, ReportRow Msq);

/// <summary>
/// The target section, in the order it is drawn, plus everything its progress bar
/// needs. The fraction and the tick positions are here rather than left to the
/// renderers for the same reason the strings are: both were worked out twice.
/// </summary>
internal readonly record struct TargetReport(
    ReportRow Target,
    ReportRow Today,
    ReportRow Plan,
    bool ShowTodayBar,
    float TodayFraction,
    IReadOnlyList<float> TodayMarks);

/// <summary>
/// Everything the report needs, and nothing else.
///
/// Deliberately not <see cref="ScenariometerSnapshot"/>, which carries all of this
/// already: the plugin window draws every frame, and building a snapshot for it would
/// allocate the expansion list and two mark arrays every time, for six rows that need
/// none of them. The widget builds one of these from the snapshot it already has; the
/// plugin builds one from its live state.
/// </summary>
internal sealed class ReportInput
{
    public bool HasEstimate { get; init; }
    public int SampleCount { get; init; }
    public int MinimumSamples { get; init; }

    /// <summary>Median seconds per quest. Also what scales the day's remainder.</summary>
    public double PaceSeconds { get; init; }

    public int Remaining { get; init; }
    public double RemainingSeconds { get; init; }
    public string ExpansionName { get; init; } = string.Empty;
    public double ExpansionRemainingSeconds { get; init; }

    public bool Overdue { get; init; }
    public bool DoneForToday { get; init; }

    /// <summary>Already formatted: the date format is a plugin setting, not a UI one.</summary>
    public string TargetDate { get; init; } = string.Empty;

    public int DaysRemaining { get; init; }
    public int CompletedToday { get; init; }
    public int QuotaToday { get; init; }
    public int RemainingToday { get; init; }
    public int AheadBy { get; init; }

    /// <summary>The widget's side of the join; the plugin fills its own from live state.</summary>
    public static ReportInput From(ScenariometerSnapshot snapshot) => new()
    {
        HasEstimate = snapshot.HasEstimate,
        SampleCount = snapshot.SampleCount,
        MinimumSamples = snapshot.MinimumSamples,
        PaceSeconds = snapshot.PaceSeconds,

        Remaining = snapshot.Remaining,
        RemainingSeconds = snapshot.RemainingSeconds,
        ExpansionName = snapshot.ExpansionName,
        ExpansionRemainingSeconds = snapshot.ExpansionRemainingSeconds,

        Overdue = snapshot.Overdue,
        DoneForToday = snapshot.DoneForToday,
        TargetDate = snapshot.TargetDate,
        DaysRemaining = snapshot.DaysRemaining,
        CompletedToday = snapshot.CompletedToday,
        QuotaToday = snapshot.QuotaToday,
        RemainingToday = snapshot.RemainingToday,
        AheadBy = snapshot.AheadBy,
    };
}

/// <summary>
/// The pace and target sections, worded once for both renderers.
///
/// The plugin window and the Umbra popup show the same two sections and used to build
/// every string in them twice, in two files no compiler relates. The drift was not
/// hypothetical - one side had already said "1 quests" where the other said "1 quest"
/// for the same state.
///
/// This is deliberately NOT part of the wire contract, though it is linked into both
/// projects beside it. What crosses the IPC gate is measurements; how they are worded
/// is a UI decision, and putting user-facing copy on the wire would turn every
/// re-phrasing into a contract change and a lock-step release of both halves.
///
/// Each renderer keeps its own nodes, layout and theme and only paints what it is
/// handed. Rows come back in a fixed order, with <see cref="ReportRow.None"/> standing
/// for "hide this one", so neither side has to know which branch produced it.
/// </summary>
internal static class ScenariometerReport
{
    /// <summary>
    /// Past this many the ticks sit closer together than they are wide, and the bar
    /// reads as hatched rather than divided.
    /// </summary>
    public const int MaxTodayMarks = 10;

    public static PaceReport Pace(ReportInput data)
    {
        if (!data.HasEstimate)
        {
            return new PaceReport(
                new ReportRow("Measuring your pace", $"{data.SampleCount} of {data.MinimumSamples} quests"),
                new ReportRow("Estimates", "once there is enough to go on"),
                ReportRow.None);
        }

        return new PaceReport(
            // The sample count rides on the label rather than the value: it qualifies
            // what the number means, and the value column is for the number itself.
            new ReportRow(
                $"Pace (median of {data.SampleCount})",
                $"{ScenariometerFormat.Duration(data.PaceSeconds)} per quest"),

            data.ExpansionName.Length > 0
                ? new ReportRow(
                    $"Rest of {data.ExpansionName}",
                    ScenariometerFormat.Duration(data.ExpansionRemainingSeconds))
                : ReportRow.None,

            new ReportRow("Rest of the MSQ", ScenariometerFormat.Duration(data.RemainingSeconds)));
    }

    /// <summary>
    /// Whether the section appears at all stays with the caller - the plugin window
    /// hides it when no target is set, and the popup has a user setting for it as
    /// well. This only says what goes in it once it is shown.
    /// </summary>
    public static TargetReport Target(ReportInput data)
    {
        if (data.Overdue)
        {
            return new TargetReport(
                new ReportRow("Target (passed)", data.TargetDate),
                new ReportRow("Still to go", $"{data.Remaining} {ScenariometerFormat.Quests(data.Remaining)}"),
                ReportRow.None,
                ShowTodayBar: false,
                TodayFraction: 0f,
                TodayMarks: []);
        }

        return new TargetReport(
            // The days left qualify the date, so they ride on the label; the per-day
            // figure is not repeated here because the Today row states it as "of N".
            new ReportRow(
                $"Target ({data.DaysRemaining} {ScenariometerFormat.Days(data.DaysRemaining)} left)",
                data.TargetDate),

            Today(data),

            // Always shown, not only once today is finished: standing against the plan
            // is the thing a target date is for.
            new ReportRow("Plan", ScenariometerFormat.PlanStanding(data.AheadBy)),

            ShowTodayBar: true,
            TodayFraction: Fraction(data),
            TodayMarks: Marks(data.QuotaToday));
    }

    private static ReportRow Today(ReportInput data)
    {
        if (data.DoneForToday)
            return new ReportRow("Today", $"{data.CompletedToday} done - finished");

        return new ReportRow(
            "Today",
            data.HasEstimate
                ? $"{data.CompletedToday} of {data.QuotaToday} - about "
                    + $"{ScenariometerFormat.Duration(data.RemainingToday * data.PaceSeconds)} left"
                : $"{data.CompletedToday} of {data.QuotaToday} done");
    }

    /// <summary>
    /// A quota of zero means yesterday's surplus already covers today, which is a full
    /// bar rather than an empty one. There is no "no target" case to confuse that with
    /// here: the section is only drawn when a target is set.
    /// </summary>
    private static float Fraction(ReportInput data) =>
        data.QuotaToday == 0 ? 1f : Math.Min(1f, (float)data.CompletedToday / data.QuotaToday);

    /// <summary>
    /// One division per quest owed today, so the bar reads as "three of five" at a
    /// glance. Skipped when there are too many to tell apart, and when there is one or
    /// none - a single quest needs no dividing.
    /// </summary>
    private static float[] Marks(int quota)
    {
        if (quota is <= 1 or > MaxTodayMarks)
            return [];

        var marks = new float[quota - 1];
        for (var i = 1; i < quota; i++)
            marks[i - 1] = (float)i / quota;

        return marks;
    }
}
