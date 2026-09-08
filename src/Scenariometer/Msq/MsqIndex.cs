using System;
using System.Collections.Generic;
using System.Linq;
using Lumina.Excel.Sheets;

namespace Scenariometer.Msq;

/// <summary>
/// The Main Scenario, in story order, built once from the Excel sheets.
///
/// How MSQ quests are identified: Quest -> JournalGenre -> JournalCategory ->
/// JournalSection. The Main Scenario sections are top-level tabs in the in-game
/// journal, and nothing but MSQ hangs off them. That beats matching quest names or
/// hardcoding id ranges, both of which break on patch day.
///
/// How they are ordered: JournalGenre rows are laid out in release order (each
/// expansion's MSQ is a run of genres), and Quest.SortKey is the journal's own
/// ordering inside a genre. Sorting by (genre, sort key) reproduces the journal.
/// Chain-walking PreviousQuest instead would have to cope with branch points
/// (class-specific starts, the pre-Heavensward city splits) and is not worth it -
/// we only need a stable count of "quests before / after here", not a strict DAG.
/// </summary>
internal sealed class MsqIndex
{
    // The journal splits the MSQ across two top-level sections: "A Realm Reborn
    // through Endwalker" (row 0) and "Dawntrail" (row 1). Verified in game on
    // 2026-08-31 with "/msq debug sections". Treating this as a single row silently
    // drops a whole expansion - the index still builds, it is just short - so this
    // stays a set, and a new expansion likely adds a row to it.
    private static readonly HashSet<uint> MainScenarioSections = [0, 1];

    public IReadOnlyList<MsqQuest> Quests { get; }

    // No short-id dictionary here, deliberately. There was one, for a Find(ushort)
    // nothing called - and building it with ToDictionary threw straight out of the
    // plugin constructor if two MSQ rows ever shared the low 16 bits of their RowId,
    // failing the load with nothing pointing at the cause. Today's Quest rows cannot
    // collide, but that is a property of the sheet, not of this code.
    private MsqIndex(List<MsqQuest> quests) => Quests = quests;

    public static MsqIndex Build()
    {
        var sheet = Services.Data.GetExcelSheet<Quest>();
        var raw = new List<(uint Genre, ushort SortKey, MsqQuest Quest)>();

        foreach (var quest in sheet)
        {
            if (quest.RowId == 0)
                continue;

            if (quest.JournalGenre.ValueNullable is not { } genre)
                continue;
            if (genre.JournalCategory.ValueNullable is not { } category)
                continue;
            if (!MainScenarioSections.Contains(category.JournalSection.RowId))
                continue;

            var name = quest.Name.ExtractText();
            if (string.IsNullOrWhiteSpace(name))
                continue; // Unused / placeholder rows have empty names.

            var expansion = quest.Expansion.ValueNullable;

            raw.Add((
                genre.RowId,
                quest.SortKey,
                new MsqQuest(
                    RowId: quest.RowId,
                    ShortId: QuestState.ToShortId(quest.RowId),
                    Name: name,
                    ExpansionId: quest.Expansion.RowId,
                    ExpansionName: expansion?.Name.ExtractText() ?? "A Realm Reborn",
                    GenreId: genre.RowId,
                    GenreName: genre.Name.ExtractText(),
                    // From ExVersion, not JournalGenre: every Main Scenario genre
                    // shares one generic journal icon (61412), so that route drew the
                    // same picture against all six expansions.
                    ExpansionIcon: expansion?.Icon ?? 0,
                    Order: 0)));
        }

        var ordered = raw
            .OrderBy(x => x.Genre)
            .ThenBy(x => x.SortKey)
            .Select((x, i) => x.Quest with { Order = i })
            .ToList();

        Services.Log.Information(
            "MSQ index built: {Count} quests across {Expansions} expansions.",
            ordered.Count,
            ordered.Select(q => q.ExpansionId).Distinct().Count());

        if (ordered.Count == 0)
        {
            Services.Log.Error(
                "MSQ index is empty - JournalSections {Sections} matched nothing. "
                + "Check MainScenarioSections against /msq debug sections.",
                string.Join(", ", MainScenarioSections));
        }

        return new MsqIndex(ordered);
    }

    /// <summary>
    /// Dumps the index grouped by expansion and by journal genre, with the story-order
    /// span of each. Behind "/msq debug expansions" - this is how to tell a wrong
    /// Quest.Expansion value apart from a wrong sort order, since both show up as
    /// implausible per-expansion totals. Genres listed in story order.
    /// </summary>
    public string DumpBreakdown()
    {
        var lines = new List<string>();

        lines.Add("By expansion:");
        foreach (var group in Quests.GroupBy(q => q.ExpansionId).OrderBy(g => g.Key))
        {
            var inOrder = group.OrderBy(q => q.Order).ToList();
            lines.Add($"  {group.Key} \"{inOrder[0].ExpansionName}\": {inOrder.Count} quests, "
                + $"order {inOrder[0].Order}-{inOrder[^1].Order}, icon {inOrder[0].ExpansionIcon}");
            lines.Add($"      first: {inOrder[0].Name}");
            lines.Add($"      last:  {inOrder[^1].Name}");
        }

        lines.Add("By genre (story order):");
        foreach (var group in Quests.GroupBy(q => q.GenreId).OrderBy(g => g.Min(q => q.Order)))
        {
            var inOrder = group.OrderBy(q => q.Order).ToList();
            lines.Add($"  genre {group.Key} \"{inOrder[0].GenreName}\" [exp {inOrder[0].ExpansionId}]: {inOrder.Count} quests, order {inOrder[0].Order}-{inOrder[^1].Order}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Dumps every JournalSection to the log, so MainScenarioSections can be checked
    /// against a live client instead of guessed. Behind "/msq debug sections".
    /// </summary>
    public static string DumpSections()
    {
        var sections = Services.Data.GetExcelSheet<JournalSection>();
        return string.Join(
            Environment.NewLine,
            sections.Select(s => $"  {s.RowId}: {s.Name.ExtractText()}"));
    }
}
