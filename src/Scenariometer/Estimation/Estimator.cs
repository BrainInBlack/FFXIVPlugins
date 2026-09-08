using System;
using System.Collections.Generic;
using System.Linq;
using Scenariometer.Contract;
using Scenariometer.Tracking;

namespace Scenariometer.Estimation;

/// <summary>
/// Turns measured samples into a pace.
///
/// Median, not mean: MSQ quest times are heavily right-skewed (most are short, a few
/// are a dungeon plus a queue), and one 45-minute outlier would drag a mean far off
/// what the next twenty quests will actually feel like.
///
/// Only the most recent N samples (Configuration.PaceWindow): pace changes as the
/// player levels, unlocks flight and teleports, or switches from skipping to watching
/// cutscenes. A lifetime average is slow to reflect any of that.
/// </summary>
internal static class Estimator
{
    public static Estimate Compute(QuestHistory? history)
    {
        if (history is null)
            return Estimate.None;

        var window = Plugin.Config.PaceWindow;

        // A window of zero means the whole history. Built in one branch or the other
        // rather than assigned and then overwritten: the discarded chain was two
        // iterators allocated on every call for nothing.
        var samples = window > 0
            ? history.Usable.TakeLast(window).Select(s => s.ActiveSeconds)
            : history.Usable.Select(s => s.ActiveSeconds);

        var values = samples.Where(v => v > 0).OrderBy(v => v).ToList();
        if (values.Count == 0)
            return Estimate.None;

        return new Estimate(
            values.Count,
            Quantile(values, 0.50),
            Quantile(values, 0.25),
            Quantile(values, 0.75));
    }

    /// <summary>Linear-interpolated quantile over an already sorted list.</summary>
    private static double Quantile(IReadOnlyList<double> sorted, double q)
    {
        if (sorted.Count == 1)
            return sorted[0];

        var position = q * (sorted.Count - 1);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);

        return sorted[lower] + ((sorted[upper] - sorted[lower]) * (position - lower));
    }

    /// <summary>Shared with IPC consumers - see ScenariometerFormat in Shared/.</summary>
    public static string Format(TimeSpan span) => ScenariometerFormat.Duration(span);
}
