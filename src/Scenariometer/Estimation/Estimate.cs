using System;

namespace Scenariometer.Estimation;

/// <summary>
/// A pace, in play-seconds per MSQ quest, plus how spread out the samples were.
/// </summary>
internal sealed record Estimate(int SampleCount, double Median, double LowerQuartile, double UpperQuartile)
{
    public static readonly Estimate None = new(0, 0, 0, 0);

    public bool HasData => SampleCount > 0;

    /// <summary>
    /// Time for <paramref name="quests"/> more quests, with a band.
    ///
    /// The band is NOT quartile * quests. Quest times are spread wide (a 90-second
    /// "talk to the man next to you" and a 40-minute dungeon are both one quest), but
    /// over a long run those cancel out - the uncertainty of a sum of n draws grows
    /// with sqrt(n), not n. Multiplying the per-quest quartiles straight through would
    /// produce a "somewhere between 40 and 400 hours" band that tells nobody anything.
    /// </summary>
    public (TimeSpan Low, TimeSpan Mid, TimeSpan High) For(int quests)
    {
        if (!HasData || quests <= 0)
            return (TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero);

        var mid = Median * quests;
        var halfWidth = (UpperQuartile - LowerQuartile) / 2 * Math.Sqrt(quests);

        return (
            TimeSpan.FromSeconds(Math.Max(0, mid - halfWidth)),
            TimeSpan.FromSeconds(mid),
            TimeSpan.FromSeconds(mid + halfWidth));
    }
}
