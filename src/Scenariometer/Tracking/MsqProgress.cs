using System.Collections.Generic;
using Scenariometer.Msq;

namespace Scenariometer.Tracking;

/// <param name="Marks">
/// Where the journal's chapters divide this expansion, as fractions of its bar.
/// The Quest sheet carries no patch number, so the journal genres are the finest
/// real division there is - "Heavensward", "Dragonsong War", "Post-Dragonsong War"
/// rather than 3.0, 3.1, 3.2.
/// </param>
internal sealed record ExpansionProgress(
    uint ExpansionId,
    string Name,
    int Completed,
    int Total,
    uint IconId,
    IReadOnlyList<float> Marks)
{
    public int Remaining => Total - Completed;
    public float Fraction => Total == 0 ? 0f : (float)Completed / Total;
}

/// <summary>Where the current character stands in the MSQ, recomputed on each poll.</summary>
/// <param name="Marks">
/// Where one expansion ends and the next begins, as fractions of the overall bar.
/// </param>
internal sealed record MsqProgress(
    int Completed,
    int Total,
    MsqQuest? Current,
    IReadOnlyList<ExpansionProgress> Expansions,
    IReadOnlyList<float> Marks)
{
    public static readonly MsqProgress Empty = new(0, 0, null, [], []);

    public int Remaining => Total - Completed;

    public float Fraction => Total == 0 ? 0f : (float)Completed / Total;

    /// <summary>Progress inside the expansion the current quest belongs to.</summary>
    public ExpansionProgress? CurrentExpansion
    {
        get
        {
            if (Current is null)
                return null;

            foreach (var expansion in Expansions)
            {
                if (expansion.ExpansionId == Current.ExpansionId)
                    return expansion;
            }

            return null;
        }
    }
}
