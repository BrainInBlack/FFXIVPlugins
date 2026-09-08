using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using Scenariometer.Estimation;

namespace Scenariometer.Windows;

/// <summary>
/// A month grid for picking the target date, as its own small game window.
///
/// Typing a date in game is the worst of both worlds: it needs keyboard focus, and it
/// needs the typist to match a format. A grid needs neither, and answers the question
/// people actually have - "which day is that, and how far off is it?"
///
/// The day buttons are a fixed pool of six weeks' worth, filled and hidden per month
/// rather than created per month: like every other native window here, nodes are built
/// once in OnSetup and only ever have their strings and visibility changed.
/// </summary>
internal sealed class NativeDatePickerWindow : NativeAddon
{
    private const int Columns = 7;

    /// <summary>A month can touch six weeks, so the grid is sized for the worst case.</summary>
    private const int Rows = 6;

    private const float CellWidth = 34f;
    private const float CellHeight = 28f;
    private const float CellGap = 2f;
    private const float HeaderHeight = 30f;
    private const float WeekdayHeight = 18f;
    private const float ButtonHeight = 28f;
    private const float BottomMargin = 14f;

    private static readonly string[] Weekdays = ["Mo", "Tu", "We", "Th", "Fr", "Sa", "Su"];

    /// <summary>The day already chosen, so it can be picked out of the grid.</summary>
    private static readonly Vector3 SelectedTint = new(1.0f, 0.78f, 0.35f);

    /// <summary>Today, when it is not also the selection.</summary>
    private static readonly Vector3 TodayTint = new(0.65f, 0.85f, 0.65f);

    private static readonly Vector3 PlainTint = new(1f, 1f, 1f);

    private readonly Action<DateOnly?> onPicked;

    private readonly List<TextButtonNode> dayButtons = [];
    private readonly List<TextNode> weekdayLabels = [];

    private TextNode monthLabel = null!;
    private CircleButtonNode previousMonth = null!;
    private CircleButtonNode nextMonth = null!;
    private TextButtonNode todayButton = null!;
    private TextButtonNode clearButton = null!;

    /// <summary>The month on display. Changed by the arrows, not by the selection.</summary>
    private DateOnly browsed = DateOnly.FromDateTime(DateTime.Now);

    private DateOnly? selected;

    private bool ready;

    public NativeDatePickerWindow(Action<DateOnly?> onPicked) => this.onPicked = onPicked;

    /// <summary>
    /// Opens on the month of the date already set, or on this one when none is. Safe
    /// to call whether the window is open or closed: the state is set first, and
    /// OnSetup refreshes from it.
    /// </summary>
    public void OpenAt(DateOnly? current)
    {
        selected = current;
        browsed = current ?? DateOnly.FromDateTime(DateTime.Now);

        if (ready)
            Refresh();

        Open();
    }

    protected override unsafe void OnSetup(AtkUnitBase* addon, Span<AtkValue> values)
    {
        // OnSetup runs again on every open against a fresh addon, so the pools have to
        // start empty or the previous open's dead nodes come with them.
        dayButtons.Clear();
        weekdayLabels.Clear();

        var x = ContentStartPosition.X;
        var y = ContentStartPosition.Y;
        var gridWidth = (Columns * CellWidth) + ((Columns - 1) * CellGap);

        previousMonth = new CircleButtonNode
        {
            Position = new Vector2(x, y),
            Size = new Vector2(HeaderHeight - 4f, HeaderHeight - 4f),
            IsVisible = true,
            Icon = CircleButtonIcon.LeftArrow,
            OnClick = () => ShiftMonth(-1),
        };

        monthLabel = new TextNode
        {
            Position = new Vector2(x + HeaderHeight, y + 4f),
            Size = new Vector2(gridWidth - (HeaderHeight * 2f), 20f),
            IsVisible = true,
            String = string.Empty,
            AlignmentType = AlignmentType.Top,
        };

        nextMonth = new CircleButtonNode
        {
            Position = new Vector2(x + gridWidth - (HeaderHeight - 4f), y),
            Size = new Vector2(HeaderHeight - 4f, HeaderHeight - 4f),
            IsVisible = true,
            Icon = CircleButtonIcon.RightArrow,
            OnClick = () => ShiftMonth(1),
        };

        y += HeaderHeight;

        for (var column = 0; column < Columns; column++)
        {
            var label = new TextNode
            {
                Position = new Vector2(x + (column * (CellWidth + CellGap)), y),
                Size = new Vector2(CellWidth, WeekdayHeight),
                IsVisible = true,
                String = Weekdays[column],
                AlignmentType = AlignmentType.Top,
            };

            weekdayLabels.Add(label);
        }

        y += WeekdayHeight;

        for (var row = 0; row < Rows; row++)
        {
            for (var column = 0; column < Columns; column++)
            {
                var index = (row * Columns) + column;
                var button = new TextButtonNode
                {
                    Position = new Vector2(
                        x + (column * (CellWidth + CellGap)),
                        y + (row * (CellHeight + CellGap))),
                    Size = new Vector2(CellWidth, CellHeight),
                    IsVisible = false,
                    String = string.Empty,
                };

                button.OnClick = () => PickIndex(index);
                dayButtons.Add(button);
            }
        }

        y += (Rows * (CellHeight + CellGap)) + 6f;

        var footerWidth = (gridWidth - CellGap) / 2f;

        todayButton = new TextButtonNode
        {
            Position = new Vector2(x, y),
            Size = new Vector2(footerWidth, ButtonHeight),
            IsVisible = true,
            String = "Today",
            OnClick = () => Pick(DateOnly.FromDateTime(DateTime.Now)),
        };

        clearButton = new TextButtonNode
        {
            Position = new Vector2(x + footerWidth + CellGap, y),
            Size = new Vector2(footerWidth, ButtonHeight),
            IsVisible = true,
            String = "No target",
            OnClick = () => Pick(null),
        };

        y += ButtonHeight;

        var nodes = new List<NodeBase> { previousMonth, monthLabel, nextMonth, todayButton, clearButton };
        nodes.AddRange(weekdayLabels);
        nodes.AddRange(dayButtons);

        AddNode(nodes);

        ready = true;
        Refresh();

        var needed = y + BottomMargin;
        if (Math.Abs(needed - Size.Y) > 2f)
        {
            Services.Log.Warning(
                "Date picker is {Actual}px but the content needs {Needed}px - set that height in Plugin.cs.",
                Size.Y,
                needed);
        }
    }

    /// <summary>
    /// Cleared on hide as well as on finalize. Closing does not finalize immediately -
    /// there is a close transition - so a window reopened during it would otherwise
    /// still look ready and get refreshed while its nodes were being torn down.
    /// </summary>
    protected override unsafe void OnHide(AtkUnitBase* addon) => ready = false;

    protected override unsafe void OnFinalize(AtkUnitBase* addon) => ready = false;

    private void ShiftMonth(int months)
    {
        browsed = browsed.AddMonths(months);
        Refresh();
    }

    private void PickIndex(int index)
    {
        if (index < 0 || index >= dayButtons.Count)
            return;

        var first = new DateOnly(browsed.Year, browsed.Month, 1);

        // DayOfWeek starts at Sunday; the grid starts at Monday.
        var lead = (((int)first.DayOfWeek) + 6) % 7;
        var day = index - lead + 1;

        if (day < 1 || day > DateTime.DaysInMonth(browsed.Year, browsed.Month))
            return;

        Pick(new DateOnly(browsed.Year, browsed.Month, day));
    }

    private void Pick(DateOnly? date)
    {
        selected = date;
        onPicked(date);
        Close();
    }

    /// <summary>Refills the grid for the browsed month. Cheap enough to call on any change.</summary>
    private void Refresh()
    {
        monthLabel.String = browsed.ToString("MMMM yyyy", CultureInfo.InvariantCulture);

        var today = DateOnly.FromDateTime(DateTime.Now);
        var first = new DateOnly(browsed.Year, browsed.Month, 1);
        var lead = (((int)first.DayOfWeek) + 6) % 7;
        var daysInMonth = DateTime.DaysInMonth(browsed.Year, browsed.Month);

        for (var i = 0; i < dayButtons.Count; i++)
        {
            var button = dayButtons[i];
            var day = i - lead + 1;

            if (day < 1 || day > daysInMonth)
            {
                button.IsVisible = false;
                continue;
            }

            var date = new DateOnly(browsed.Year, browsed.Month, day);

            button.String = day.ToString(CultureInfo.InvariantCulture);
            button.IsVisible = true;

            // Tinting the button rather than swapping its art: the selection and today
            // both need to stand out without looking like a different kind of control.
            button.MultiplyColor =
                date == selected ? SelectedTint
                : date == today ? TodayTint
                : PlainTint;
        }
    }
}
