using FFXIVClientStructs.FFXIV.Client.Game;

namespace Scenariometer.Msq;

/// <summary>
/// The only place that touches FFXIVClientStructs. Everything else in the plugin
/// works with plain quest ids, so an API break in ClientStructs is a one-file fix.
/// </summary>
internal static class QuestState
{
    /// <summary>
    /// Quest sheet RowIds are offset by 0x10000 from the ids the game's QuestManager
    /// uses (sheet row 65717 == quest id 181). Every ClientStructs call needs the
    /// short form; every Lumina lookup needs the RowId. Mixing them up silently
    /// reports "nothing completed", so the conversion lives here and nowhere else.
    /// </summary>
    internal static ushort ToShortId(uint questRowId) => (ushort)(questRowId & 0xFFFF);

    internal static bool IsComplete(ushort questId) => QuestManager.IsQuestComplete(questId);
}
