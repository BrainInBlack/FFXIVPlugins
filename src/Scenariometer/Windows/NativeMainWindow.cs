using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using Scenariometer.Contract;
using Scenariometer.Estimation;
using Scenariometer.Tracking;

namespace Scenariometer.Windows;

/// <summary>
/// The main window, as a real game window rather than an ImGui one.
///
/// This is an AtkUnitBase built out of Atk nodes through KamiToolKit, so it gets the
/// game frame, fonts, close button and UI scaling for free, and sits in the game
/// window stacking rather than floating above it.
///
/// Nodes are created once in OnSetup; OnUpdate only ever changes their strings,
/// visibility and position. Native nodes are not immediate mode, and rebuilding them
/// per frame would leak and flicker - that is the one habit to unlearn coming from
/// the ImGui windows next door. The per-expansion rows are therefore a fixed pool
/// that is filled and hidden, not a list that grows.
///
/// Layout is recomputed every update rather than fixed at setup, because most of the
/// window is conditional - no target date, no target block; six expansions today and
/// seven after the next one ships. Positioning at setup left holes where the hidden
/// parts used to be.
/// </summary>
internal sealed class NativeMainWindow : NativeAddon
{
    /// <summary>The Main Scenario quest marker, as the game draws it in the journal.</summary>
    private const uint MsqIconId = 71001;

    /// <summary>
    /// Headroom over the six expansions that exist, so a new one on patch day shows
    /// up without a code change. Unused rows are hidden and take no vertical space.
    /// </summary>
    private const int MaxExpansionRows = 10;

    /// <summary>Header, current quest, estimates, target block - four joins at most.</summary>
    private const int MaxSeparators = 4;

    /// <summary>
    /// Tick marks per bar. The overall bar divides at expansion boundaries (five
    /// today), a row at the journal's chapter boundaries within one expansion (two
    /// so far). Sized with room to spare, and the surplus stays hidden.
    /// </summary>
    private const int MaxMarksOverall = 12;
    private const int MaxMarksPerRow = 6;

    /// <summary>
    /// Today's bar is divided one tick per quest, so the day reads as a count rather
    /// than a proportion. Only up to this many: a thirty-quest day would be a hatched
    /// block, which says less than a plain bar does.
    /// </summary>
    private const int MaxTodayMarks = 10;

    private const float MarkWidth = 2f;

    /// <summary>Inset from the bar rather than overhanging it - a tick, not a fence.</summary>
    private const float MarkInset = 1f;

    // --- palette -----------------------------------------------------------------
    // Grouped so the whole look can be retuned from one place. All are RGBA 0-1.
    //
    // These are set through BarColor and BackgroundColor, which replace the bar's
    // nine-grid art with flat fills - so the rounded caps at each end go away. That is
    // the intended look here, and deliberate: the alternative, tinting the art with
    // MultiplyColor, keeps the rounding but muddies the colour against the shading the
    // art is drawn with, which read worse than the flat bar. Do not "fix" the square
    // ends by switching to MultiplyColor; that trade was made on purpose.

    /// <summary>MSQ progress: the game's own warm gold, as on an experience bar.</summary>
    private static readonly Vector4 ProgressFill = new(0.96f, 0.78f, 0.38f, 1f);

    /// <summary>The unfilled track behind it - dark, so the fill carries the eye.</summary>
    private static readonly Vector4 ProgressTrack = new(0.08f, 0.08f, 0.10f, 0.85f);

    /// <summary>
    /// Today's goal, deliberately a different hue from MSQ progress: the two bars sit
    /// in the same window and measure completely different things. Green rather than
    /// something cooler - it has to sit next to the gold without fighting it.
    /// </summary>
    private static readonly Vector4 TodayFill = new(0.58f, 0.80f, 0.42f, 1f);

    /// <summary>
    /// Ticks are plain coloured rectangles. VerticalLineNode looked like the obvious
    /// choice, but it is a horizontal line rotated 90 degrees, so its Width, Height
    /// and Size mean different things than they appear to and it drew nothing.
    ///
    /// Light rather than dark: a dark tick reads well on the gold fill and vanishes
    /// against the track, and the boundaries still to come are the interesting ones.
    /// </summary>
    private static readonly Vector4 MarkColor = new(1f, 1f, 1f, 0.55f);

    private const float Line = 20f;
    private const float Gap = 12f;
    // 12, not more: ProgressBarNode's art does not scale past this - at 16 the fill
    // rendered as a full bar regardless of Progress.
    private const float BarHeight = 12f;
    /// <summary>The header icon, matching the expansion rows below it.</summary>
    private const float IconSize = 40f;

    /// <summary>The count under the header bar, set smaller than a normal line.</summary>
    private const float SmallLine = 16f;
    private const uint SmallFontSize = 11;

    /// <summary>Quest name and chapter, the bar, then the count.</summary>
    private const float HeaderStackHeight = Line + 2f + BarHeight + 2f + SmallLine;

    /// <summary>How much of the header line the chapter takes, right-aligned.</summary>
    private const float ChapterWidth = 130f;
    private const float IconGap = 10f;
    /// <summary>Name and count on one line, the bar on the next.</summary>
    private const float RowStackHeight = Line + 2f + BarHeight;

    /// <summary>
    /// Larger than the two-line stack beside it, so the icon carries the row. The
    /// stack is centred against it rather than the other way round.
    /// </summary>
    private const float RowIconSize = 40f;

    private const float RowHeight = RowIconSize + 10f;
    private const float ButtonHeight = 28f;
    private const float BottomMargin = 16f;

    /// <summary>
    /// Keeps bars and their labels clear of the right edge, so they are inset on that
    /// side by about as much as an icon's own transparent border insets it on the
    /// left. Without it a bar runs flush to the separator above while the icon
    /// visually does not, and the two sides look mismatched.
    /// </summary>
    private const float ContentRightPad = 6f;

    private readonly ProgressTracker tracker;
    private readonly Action openSettings;

    private IconImageNode msqIcon = null!;
    private ProgressBarNode overallBar = null!;
    private TextNode overallText = null!;

    private TextNode currentQuestText = null!;
    private TextNode chapterText = null!;

    private LabelRow paceRow = null!;
    private LabelRow expansionLeftRow = null!;
    private LabelRow msqLeftRow = null!;

    private LabelRow targetRow = null!;
    private ProgressBarNode todayBar = null!;
    private LabelRow todayRow = null!;
    private LabelRow planRow = null!;

    private readonly List<ExpansionRow> expansionRows = [];

    /// <summary>
    /// Drawn between regions. A pool, because which regions exist depends on whether
    /// a target date is set and whether the breakdown is on; Layout takes them in
    /// order and hides whatever is left over.
    /// </summary>
    private readonly List<HorizontalLineNode> separators = [];

    /// <summary>Expansion boundaries drawn across the overall bar.</summary>
    private readonly List<ColorImageNode> overallMarks = [];

    /// <summary>One division per quest owed today, drawn across the goal bar.</summary>
    private readonly List<ColorImageNode> todayMarks = [];

    /// <summary>Where those divisions fall, worked out in DrawGoal.</summary>
    private readonly List<float> todayMarkFractions = [];

    private int separatorCursor;

    /// <summary>
    /// False until OnSetup has built the nodes. KamiToolKit documents OnUpdate as
    /// running while the addon exists but before it is opened, so the callback can
    /// arrive with every field still null.
    /// </summary>
    private bool ready;

    /// <summary>The font size overallText starts life with, before the caption styling.</summary>
    private uint defaultFontSize;

    // Progress is applied in Layout, immediately after each bar's Size is set.
    // ProgressBarNode recalculates its fill in OnSizeChanged, so setting Progress
    // first and Size second leaves every bar rendered full regardless of value.
    private float overallFraction;
    private float todayFraction;

    private TextButtonNode pauseButton = null!;
    private CircleButtonNode settingsButton = null!;

    public NativeMainWindow(ProgressTracker tracker, Action openSettings)
    {
        this.tracker = tracker;
        this.openSettings = openSettings;
    }

    // The signature is the game's own addon callback, pointers and all. This file and
    // QuestState.cs are the only two that see FFXIVClientStructs; the pointer is not
    // used here, since KamiToolKit hands back the managed nodes.
    protected override unsafe void OnSetup(AtkUnitBase* addon, Span<AtkValue> values)
    {
        // OnSetup runs again every time the window is reopened, against a freshly
        // built addon. The node pools have to be emptied first: appending would leave
        // the previous open's dead nodes in the lists, and the rows being filled would
        // be the detached ones while the live ones stayed blank.
        expansionRows.Clear();
        separators.Clear();
        overallMarks.Clear();
        todayMarks.Clear();

        // Positions here are placeholders - Layout() sets the real ones every update.
        msqIcon = new IconImageNode
        {
            Size = new Vector2(IconSize, IconSize),
            IconId = MsqIconId,

            // Without this the texture draws at its own native size and Size is
            // ignored, so the icon comes out whatever the game happens to store.
            FitTexture = true,
        };
        overallBar = NewBar(ProgressFill);
        overallText = NewText();

        currentQuestText = NewText();

        // Right against the bar's end, the way the expansion counts sit.
        chapterText = NewText();
        chapterText.AlignmentType = AlignmentType.TopRight;

        // Remembered before the caption styling goes on, so the placeholder path can
        // put it back - that node does double duty as a plain line of text.
        defaultFontSize = overallText.FontSize;
        SetCaptionStyle(true);

        paceRow = LabelRow.Create();
        expansionLeftRow = LabelRow.Create();
        msqLeftRow = LabelRow.Create();

        targetRow = LabelRow.Create();
        todayBar = NewBar(TodayFill);
        todayRow = LabelRow.Create();
        planRow = LabelRow.Create();

        for (var i = 0; i < MaxExpansionRows; i++)
            expansionRows.Add(ExpansionRow.Create());

        for (var i = 0; i < MaxSeparators; i++)
            separators.Add(new HorizontalLineNode { IsVisible = false });

        for (var i = 0; i < MaxMarksOverall; i++)
            overallMarks.Add(NewMark());

        for (var i = 0; i < MaxTodayMarks; i++)
            todayMarks.Add(NewMark());

        pauseButton = new TextButtonNode
        {
            Size = new Vector2(150f, ButtonHeight),
            IsVisible = true,
            String = "Pause tracking",
            OnClick = TogglePause,
        };

        // The game's own round cog, the same control its windows use for options.
        settingsButton = new CircleButtonNode
        {
            Size = new Vector2(ButtonHeight, ButtonHeight),
            IsVisible = true,
            Icon = CircleButtonIcon.GearCog,
            OnClick = () => openSettings(),
        };

        var nodes = new List<NodeBase>
        {
            msqIcon, overallBar, overallText,
            currentQuestText, chapterText,
            todayBar,
            pauseButton, settingsButton,
        };

        foreach (var row in new[] { paceRow, expansionLeftRow, msqLeftRow, targetRow, todayRow, planRow })
            row.AddTo(nodes);

        foreach (var row in expansionRows)
            row.AddTo(nodes);

        nodes.AddRange(separators);
        nodes.AddRange(overallMarks);
        nodes.AddRange(todayMarks);

        AddNode(nodes);
        ready = true;
    }

    protected override unsafe void OnFinalize(AtkUnitBase* addon) => ready = false;

    /// <summary>
    /// Hidden by default: every bar is switched on explicitly by whatever decides it
    /// has something to show, and an unclaimed row should not flash one frame of bar.
    /// </summary>
    private static ProgressBarNode NewBar(Vector4 fill) => new()
    {
        Size = new Vector2(100f, BarHeight),
        BarColor = fill,
        BackgroundColor = ProgressTrack,
        IsVisible = false,
    };

    private static ColorImageNode NewMark() => new()
    {
        Size = new Vector2(MarkWidth, BarHeight - (MarkInset * 2f)),
        Color = MarkColor,
        IsVisible = false,
    };

    private static TextNode NewText() => new()
    {
        Size = new Vector2(100f, Line),
        IsVisible = false,
        String = string.Empty,
    };

    /// <summary>Sets a line's text, hiding it when there is nothing to say.</summary>
    private static void SetText(TextNode node, string text)
    {
        node.String = text;
        node.IsVisible = text.Length > 0;
    }

    private static void TogglePause()
    {
        Plugin.Config.Paused = !Plugin.Config.Paused;
        Plugin.Config.Save();
    }

    protected override unsafe void OnUpdate(AtkUnitBase* addon)
    {
        if (!ready)
            return;

        var progress = tracker.Progress;

        if (tracker.IndexedQuestCount == 0)
        {
            ShowPlaceholder("No Main Scenario data loaded. Run /msq debug sections.");
            return;
        }

        if (!Services.ClientState.IsLoggedIn)
        {
            ShowPlaceholder("Not logged in.");
            return;
        }

        if (progress.Total == 0)
        {
            ShowPlaceholder("Reading Main Scenario progress...");
            return;
        }

        SetCaptionStyle(true);

        msqIcon.IsVisible = true;
        overallFraction = progress.Fraction;
        overallBar.IsVisible = true;

        // Invariant, not the OS culture: the client is running in English and a
        // "45,9%" in the middle of English text reads as a bug.
        SetText(
            overallText,
            $"{progress.Completed} / {progress.Total}   "
            + $"({(progress.Fraction * 100).ToString("0.0", CultureInfo.InvariantCulture)}%)");

        DrawCurrent(progress);

        var estimate = Estimator.Compute(tracker.History);
        DrawEstimate(progress, estimate);
        DrawGoal(progress, estimate);
        DrawExpansions(progress);

        pauseButton.String = Plugin.Config.Paused ? "Resume tracking" : "Pause tracking";
        pauseButton.IsVisible = true;

        Layout();
    }

    /// <summary>
    /// overallText is the bar's caption in the normal case and a plain message in the
    /// placeholder one. Without switching back, "Not logged in." rendered centred at
    /// caption size.
    /// </summary>
    private void SetCaptionStyle(bool caption)
    {
        overallText.AlignmentType = caption ? AlignmentType.Top : AlignmentType.TopLeft;
        overallText.FontSize = caption ? SmallFontSize : defaultFontSize;
    }

    private void ShowPlaceholder(string message)
    {
        SetCaptionStyle(false);

        msqIcon.IsVisible = false;
        overallBar.IsVisible = false;
        todayBar.IsVisible = false;
        pauseButton.IsVisible = false;

        SetText(overallText, message);
        SetText(currentQuestText, string.Empty);
        SetText(chapterText, string.Empty);
        paceRow.Hide();
        expansionLeftRow.Hide();
        msqLeftRow.Hide();
        targetRow.Hide();
        todayRow.Hide();
        planRow.Hide();

        foreach (var row in expansionRows)
            row.IsVisible = false;

        foreach (var mark in overallMarks)
            mark.IsVisible = false;

        foreach (var mark in todayMarks)
            mark.IsVisible = false;

        Layout();
    }

    /// <summary>
    /// Stacks whatever is visible from the top down, then shrinks the window to fit.
    /// Hidden nodes contribute nothing, so an absent target date or a not-yet-released
    /// expansion leaves no hole.
    /// </summary>
    private void Layout()
    {
        var x = ContentStartPosition.X;
        var width = ContentSize.X;
        var y = ContentStartPosition.Y;

        // Reopening runs an update or two before the window node has real geometry.
        // Laying out against a zero width piles everything into the top-left corner,
        // and resizing off the back of it makes that state stick.
        if (width <= 1f)
            return;

        separatorCursor = 0;

        if (msqIcon.IsVisible)
        {
            // Same shape as an expansion row, one line taller: the current quest and
            // its chapter above the bar, the totals centred underneath.
            var barX = x + IconSize + IconGap;
            var barWidth = width - IconSize - IconGap - ContentRightPad;
            var headerHeight = Math.Max(HeaderStackHeight, IconSize);
            var stackY = y + ((headerHeight - HeaderStackHeight) / 2f);
            var barY = stackY + Line + 2f;

            msqIcon.Position = new Vector2(x, y + ((headerHeight - IconSize) / 2f));

            currentQuestText.Position = new Vector2(barX, stackY);
            currentQuestText.Size = new Vector2(Math.Max(20f, barWidth - ChapterWidth - 8f), Line);

            chapterText.Position = new Vector2(barX + barWidth - ChapterWidth, stackY);
            chapterText.Size = new Vector2(ChapterWidth, Line);

            overallBar.Position = new Vector2(barX, barY);
            overallBar.Size = new Vector2(barWidth, BarHeight);
            overallBar.Progress = overallFraction;
            PlaceMarks(overallMarks, tracker.Progress.Marks, barX, barY, barWidth);

            overallText.Position = new Vector2(barX, barY + BarHeight + 2f);
            overallText.Size = new Vector2(barWidth, SmallLine);

            y += headerHeight;
        }
        else
        {
            y = Place(overallText, x, y, width);
        }

        y = Separator(x, y + 6f, width);

        var rowWidth = width - ContentRightPad;

        var before = y;
        y = paceRow.Place(x, y, rowWidth);
        y = expansionLeftRow.Place(x, y, rowWidth);
        y = msqLeftRow.Place(x, y, rowWidth);
        y = SeparatorIfGrew(x, y, before, width);

        before = y;
        y = targetRow.Place(x, y, rowWidth);
        if (todayBar.IsVisible)
        {
            todayBar.Position = new Vector2(x, y + 2f);
            todayBar.Size = new Vector2(width - ContentRightPad, BarHeight);
            todayBar.Progress = todayFraction;
            PlaceMarks(todayMarks, todayMarkFractions, x, y + 2f, width - ContentRightPad);
            y += BarHeight + 4f;
        }
        else
        {
            foreach (var mark in todayMarks)
                mark.IsVisible = false;
        }

        y = todayRow.Place(x, y, rowWidth);
        y = planRow.Place(x, y, rowWidth);
        y = SeparatorIfGrew(x, y, before, width);

        before = y;
        foreach (var row in expansionRows)
            y = row.Place(x, y, width);

        // Not the usual gap: RowHeight already carries ten pixels of space past the
        // icon, so adding a full one on top leaves the footer floating away from the
        // list it follows.
        if (y > before)
            y += 2f;

        if (pauseButton.IsVisible)
        {
            // Action on the left, options on the right - the game's usual footer.
            pauseButton.Position = new Vector2(x, y);
            settingsButton.Position = new Vector2(x + width - ButtonHeight, y);
            settingsButton.IsVisible = true;
            y += ButtonHeight;
        }
        else
        {
            settingsButton.IsVisible = false;
        }

        for (var i = separatorCursor; i < separators.Count; i++)
            separators[i].IsVisible = false;

        Resize(y);
    }

    /// <summary>
    /// Lays tick marks across a bar at the given fractions. Anything the pool cannot
    /// cover, and anything left over, stays hidden - the marks are an orientation aid,
    /// so running out is worth nothing more than drawing fewer of them.
    /// </summary>
    private static void PlaceMarks(
        List<ColorImageNode> pool,
        IReadOnlyList<float> fractions,
        float barX,
        float barY,
        float barWidth)
    {
        for (var i = 0; i < pool.Count; i++)
        {
            if (i >= fractions.Count)
            {
                pool[i].IsVisible = false;
                continue;
            }

            var mark = pool[i];
            mark.Size = new Vector2(MarkWidth, BarHeight - (MarkInset * 2f));
            mark.Color = MarkColor;
            mark.Position = new Vector2(
                barX + (barWidth * fractions[i]) - (MarkWidth / 2f),
                barY + MarkInset);
            mark.IsVisible = true;
        }
    }

    private static float Place(TextNode node, float x, float y, float width)
    {
        if (!node.IsVisible)
            return y;

        node.Position = new Vector2(x, y);
        node.Size = new Vector2(width, Line);
        return y + Line;
    }

    /// <summary>A rule at the current position, from the pool. No-op once it is empty.</summary>
    private float Separator(float x, float y, float width)
    {
        if (separatorCursor >= separators.Count)
            return y + Gap;

        var line = separators[separatorCursor++];
        line.Position = new Vector2(x, y);
        line.Size = new Vector2(width, 4f);
        line.IsVisible = true;

        return y + 10f;
    }

    /// <summary>Separates the region that just ended from the next, if it drew anything.</summary>
    private float SeparatorIfGrew(float x, float y, float before, float width) =>
        y > before ? Separator(x, y + 4f, width) : y;

    /// <summary>
    /// Shrinks or grows the window to whatever the content came to. Only on a real
    /// change, so it is not fighting itself every frame.
    /// </summary>
    private void Resize(float contentBottom)
    {
        var height = contentBottom + BottomMargin;

        // Known limitation: resizing an open addon updates Size but does not redraw
        // the window frame, so a height change only takes visual effect on the next
        // open. The settings window sidesteps this by being a fixed size; this one
        // cannot, because its content genuinely varies - the target block appears and
        // disappears, and the expansion list can be turned off.
        if (Math.Abs(Size.Y - height) > 1f)
            SetWindowSize(new Vector2(Size.X, height));
    }

    private void DrawCurrent(MsqProgress progress)
    {
        if (progress.Current is null)
        {
            SetText(currentQuestText, "Main Scenario complete.");
            SetText(chapterText, string.Empty);
            return;
        }

        // No labels: the header reads as a heading, and "Current:" in front of a quest
        // name is only telling you what a quest name obviously is.
        SetText(currentQuestText, progress.Current.Name);
        SetText(chapterText, progress.Current.GenreName);
    }

    private void DrawEstimate(MsqProgress progress, Estimate estimate)
    {
        if (progress.Current is null)
        {
            paceRow.Hide();
            expansionLeftRow.Hide();
            msqLeftRow.Hide();
            return;
        }

        if (estimate.SampleCount < Plugin.Config.MinimumSamples)
        {
            paceRow.Set("Measuring your pace", $"{estimate.SampleCount} of {Plugin.Config.MinimumSamples} quests");
            expansionLeftRow.Set("Estimates", "once there is enough to go on");
            msqLeftRow.Hide();
            return;
        }

        // The sample count rides on the label rather than the value: it qualifies what
        // the number means, and the value column is for the number itself.
        paceRow.Set(
            $"Pace (median of {estimate.SampleCount})",
            $"{Estimator.Format(TimeSpan.FromSeconds(estimate.Median))} per quest");

        var expansion = progress.CurrentExpansion;
        if (expansion is null)
            expansionLeftRow.Hide();
        else
            expansionLeftRow.Set($"Rest of {expansion.Name}", Estimator.Format(estimate.For(expansion.Remaining).Mid));

        msqLeftRow.Set("Rest of the MSQ", Estimator.Format(estimate.For(progress.Remaining).Mid));
    }

    private void DrawGoal(MsqProgress progress, Estimate estimate)
    {
        var goal = DailyGoal.Compute(
            Plugin.Config.TargetDate,
            tracker.PlanStartDate,
            Plugin.Config.DayStartHour,
            progress.Remaining,
            tracker.History,
            DateTimeOffset.Now);

        if (!goal.HasTarget || progress.Current is null)
        {
            targetRow.Hide();
            todayRow.Hide();
            planRow.Hide();
            todayBar.IsVisible = false;
            todayMarkFractions.Clear();
            return;
        }

        if (goal.Overdue)
        {
            targetRow.Set("Target (passed)", DailyGoal.Format(goal.Target));
            todayRow.Set(
                "Still to go",
                $"{goal.QuestsRemaining} {ScenariometerFormat.Quests(goal.QuestsRemaining)}");
            planRow.Hide();
            todayBar.IsVisible = false;
            todayMarkFractions.Clear();
            return;
        }

        // Always shown, not only once today is finished: standing against the plan is
        // the thing a target date is for, and it was previously buried in the one
        // branch that reported the day as done.
        planRow.Set("Plan", ScenariometerFormat.PlanStanding(goal.AheadBy));

        // The days left qualify the date, so they ride on the label; the per-day figure
        // is not repeated here because the Today row below states it as "of N".
        targetRow.Set(
            $"Target ({goal.DaysRemaining} {ScenariometerFormat.Days(goal.DaysRemaining)} left)",
            DailyGoal.Format(goal.Target));

        todayFraction = goal.QuotaToday == 0
            ? 1f
            : Math.Min(1f, (float)goal.CompletedToday / goal.QuotaToday);
        todayBar.IsVisible = true;

        // One division per quest owed, so the bar can be read as "three of five" at a
        // glance. Skipped when there are too many to tell apart, and when there is one
        // or none - a single quest needs no dividing.
        todayMarkFractions.Clear();
        if (goal.QuotaToday is > 1 and <= MaxTodayMarks)
        {
            for (var i = 1; i < goal.QuotaToday; i++)
                todayMarkFractions.Add((float)i / goal.QuotaToday);
        }

        if (goal.DoneForToday)
        {
            todayRow.Set("Today", $"{goal.CompletedToday} done - finished");
            return;
        }

        todayRow.Set(
            "Today",
            estimate.SampleCount < Plugin.Config.MinimumSamples
                ? $"{goal.CompletedToday} of {goal.QuotaToday} done"
                : $"{goal.CompletedToday} of {goal.QuotaToday} - about "
                    + $"{Estimator.Format(estimate.For(goal.RemainingToday).Mid)} left");
    }

    private void DrawExpansions(MsqProgress progress)
    {
        var show = Plugin.Config.ShowExpansionBreakdown;

        for (var i = 0; i < expansionRows.Count; i++)
        {
            if (!show || i >= progress.Expansions.Count)
            {
                expansionRows[i].IsVisible = false;
                continue;
            }

            expansionRows[i].Fill(progress.Expansions[i]);
        }
    }

    /// <summary>
    /// A label on the left and its value right-aligned on the right - the same shape
    /// as the header and the expansion rows, so the middle of the window stops looking
    /// like a different design from the top and bottom of it.
    /// </summary>
    private sealed class LabelRow
    {
        /// <summary>How much of the row the value gets. The labels are the shorter half.</summary>
        private const float ValueShare = 0.55f;

        private TextNode label = null!;
        private TextNode value = null!;

        public bool IsVisible => label.IsVisible;

        public static LabelRow Create() => new()
        {
            label = new TextNode { Size = new Vector2(100f, Line), IsVisible = false, String = string.Empty },
            value = new TextNode
            {
                Size = new Vector2(100f, Line),
                IsVisible = false,
                String = string.Empty,
                AlignmentType = AlignmentType.TopRight,
            },
        };

        public void AddTo(List<NodeBase> nodes)
        {
            nodes.Add(label);
            nodes.Add(value);
        }

        /// <summary>An empty value hides the whole row, label included.</summary>
        public void Set(string labelText, string valueText)
        {
            label.String = labelText;
            value.String = valueText;

            var show = valueText.Length > 0;
            label.IsVisible = show;
            value.IsVisible = show;
        }

        public void Hide() => Set(string.Empty, string.Empty);

        public float Place(float x, float y, float width)
        {
            if (!IsVisible)
                return y;

            var valueWidth = width * ValueShare;

            label.Position = new Vector2(x, y);
            label.Size = new Vector2(Math.Max(20f, width - valueWidth - 8f), Line);

            value.Position = new Vector2(x + width - valueWidth, y);
            value.Size = new Vector2(valueWidth, Line);

            return y + Line;
        }
    }

    /// <summary>
    /// One expansion: its journal icon, its name, a bar and the counts. Built once and
    /// reused, so the set of nodes never changes after setup.
    /// </summary>
    private sealed class ExpansionRow
    {
        private const float CountWidth = 90f;

        private IconImageNode icon = null!;
        private TextNode name = null!;
        private ProgressBarNode bar = null!;
        private TextNode count = null!;

        /// <summary>Chapter boundaries drawn across this expansion's bar.</summary>
        private readonly List<ColorImageNode> marks = [];

        private IReadOnlyList<float> fractions = [];

        /// <summary>Applied in Place, after the bar is sized. See the fields above.</summary>
        private float fraction;

        public bool IsVisible
        {
            get => icon.IsVisible;
            set
            {
                icon.IsVisible = value;
                name.IsVisible = value;
                bar.IsVisible = value;
                count.IsVisible = value;

                if (!value)
                {
                    foreach (var mark in marks)
                        mark.IsVisible = false;
                }
            }
        }

        public static ExpansionRow Create()
        {
            var row = new ExpansionRow
            {
                icon = new IconImageNode
                {
                    Size = new Vector2(RowIconSize, RowIconSize),
                    IsVisible = false,
                    FitTexture = true,
                },
                name = new TextNode { Size = new Vector2(100f, Line), IsVisible = false, String = string.Empty },
                bar = NewBar(ProgressFill),
                count = new TextNode
                {
                    Size = new Vector2(CountWidth, Line),
                    IsVisible = false,
                    String = string.Empty,

                    // Right against the bar's end, so the counts form a column instead
                    // of drifting with the width of each number.
                    AlignmentType = AlignmentType.TopRight,
                },
            };

            for (var i = 0; i < MaxMarksPerRow; i++)
                row.marks.Add(NewMark());

            return row;
        }

        public void AddTo(List<NodeBase> nodes)
        {
            nodes.Add(icon);
            nodes.Add(name);
            nodes.Add(bar);
            nodes.Add(count);
            nodes.AddRange(marks);
        }

        public void Fill(ExpansionProgress expansion)
        {
            icon.IconId = expansion.IconId;
            name.String = expansion.Name;
            fraction = expansion.Fraction;
            fractions = expansion.Marks;
            count.String = $"{expansion.Completed} / {expansion.Total}";

            IsVisible = true;
        }

        /// <summary>Positions the row and returns the next free y, or y unchanged when hidden.</summary>
        public float Place(float x, float y, float width)
        {
            if (!IsVisible)
                return y;

            // Everything but the icon shares one column: the label line on top, the
            // bar filling the same width underneath.
            var contentX = x + RowIconSize + IconGap;
            var contentWidth = Math.Max(60f, width - RowIconSize - IconGap - ContentRightPad);

            // The icon is the taller of the two, so the text and bar sit centred
            // against it instead of hanging from the top of the row.
            var stackY = y + ((RowIconSize - RowStackHeight) / 2f);
            var barY = stackY + Line + 2f;

            icon.Position = new Vector2(x, y);
            icon.Size = new Vector2(RowIconSize, RowIconSize);

            name.Position = new Vector2(contentX, stackY);
            name.Size = new Vector2(Math.Max(20f, contentWidth - CountWidth - 8f), Line);

            count.Position = new Vector2(contentX + contentWidth - CountWidth, stackY);
            count.Size = new Vector2(CountWidth, Line);

            bar.Position = new Vector2(contentX, barY);
            bar.Size = new Vector2(contentWidth, BarHeight);
            bar.Progress = fraction;
            PlaceMarks(marks, fractions, contentX, barY, contentWidth);

            return y + RowHeight;
        }
    }
}
