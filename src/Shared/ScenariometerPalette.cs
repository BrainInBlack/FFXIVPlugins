namespace Scenariometer.Contract;

/// <summary>
/// A colour as plain bytes, because the two renderers disagree about what a colour is
/// and neither type may appear here.
///
/// The plugin window wants <c>Vector4</c> channels in 0..1; the popup wants
/// Una.Drawing's <c>Color</c>, which is AGPL and cannot be referenced from this
/// project at all. Bytes are the one representation both can convert from without
/// either library crossing the licence boundary.
/// </summary>
internal readonly record struct ReportColor(byte R, byte G, byte B, byte A)
{
    /// <summary>The channel as a 0..1 float, for renderers that want it that way.</summary>
    public float Rf => R / 255f;

    public float Gf => G / 255f;

    public float Bf => B / 255f;

    public float Af => A / 255f;
}

/// <summary>
/// The bar colours, defined once.
///
/// The plugin window and the Umbra popup draw the same two bars, and a different gold
/// in each would read as a different measurement. They used to carry separate copies -
/// floats in one file, bytes in the other - with a comment in each insisting the two
/// matched and nothing whatsoever enforcing it. They did match, to within a rounding
/// step; that they still would after the next adjustment was luck.
///
/// Only the bars. Everything that is text or chrome stays with each renderer: the
/// popup uses Umbra's named theme colours so it follows the profile the user picked,
/// and the window uses the game's.
/// </summary>
internal static class ScenariometerPalette
{
    /// <summary>MSQ progress: the game's own warm gold, as on an experience bar.</summary>
    public static readonly ReportColor ProgressFill = new(245, 199, 97, 255);

    /// <summary>The unfilled track behind it - dark, so the fill carries the eye.</summary>
    public static readonly ReportColor ProgressTrack = new(20, 20, 26, 217);

    /// <summary>
    /// Today's goal, deliberately a different hue from MSQ progress: the two bars sit
    /// side by side and measure completely different things. Green rather than
    /// something cooler - it has to sit next to the gold without fighting it.
    /// </summary>
    public static readonly ReportColor TodayFill = new(148, 204, 107, 255);

    /// <summary>
    /// The quota ticks. Light rather than dark: a dark tick reads well on the gold
    /// fill and vanishes against the track, and the boundaries still to come are the
    /// interesting ones.
    ///
    /// The alpha is doing real work. Only the track sits behind a tick, so the
    /// translucency resolves to one mid grey everywhere - softer against the gold than
    /// an opaque divider, which reads as a hard edge.
    /// </summary>
    public static readonly ReportColor Mark = new(255, 255, 255, 140);
}
