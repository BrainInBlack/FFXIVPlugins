namespace Scenariometer.Msq;

/// <summary>One Main Scenario quest, flattened out of the Excel sheets at load.</summary>
/// <param name="RowId">Quest sheet row id (0x1xxxx form) - use for Lumina lookups.</param>
/// <param name="ShortId">Quest id as the game's QuestManager knows it.</param>
/// <param name="Order">Index in the story order built by <see cref="MsqIndex"/>.</param>
internal sealed record MsqQuest(
    uint RowId,
    ushort ShortId,
    string Name,
    uint ExpansionId,
    string ExpansionName,
    uint GenreId,
    string GenreName,
    uint ExpansionIcon,
    int Order);
