using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes;
using KamiToolKit.Nodes;
using Scenariometer.Estimation;

namespace Scenariometer.Windows;

/// <summary>
/// Settings, as a game window to match <see cref="NativeMainWindow"/>.
///
/// Layout is fixed here rather than recomputed per frame: unlike the main window,
/// nothing in this one appears or disappears, so there are no holes to collapse. It
/// is still built in OnSetup, which runs on every open against a fresh addon.
///
/// Writes are debounced. A native slider reports every step of a drag, and saving
/// there would rewrite the config file on each one - the same trap the ImGui version
/// had with SliderInt.
/// </summary>
internal sealed class NativeConfigWindow : NativeAddon
{
    private const float Line = 22f;
    private const float Gap = 14f;
    private const float LabelWidth = 190f;

    /// <summary>
    /// Every value control is this wide. Three inputs at three different widths is
    /// what made the column look accidental rather than designed.
    /// </summary>
    private const float ControlWidth = 260f;

    /// <summary>
    /// The hour spinner, narrower than the rest. Two digits do not need the width the
    /// date field does, and at full width its +/- buttons end up marooned at the far
    /// right instead of beside the number they change.
    /// </summary>
    private const float SpinnerWidth = 110f;

    /// <summary>
    /// Matches the node's own +/- buttons, which are 28px and sit at the top of it. At
    /// the usual 26 they overhang the field by two pixels and the row looks crooked.
    /// </summary>
    private const float SpinnerHeight = 28f;

    /// <summary>
    /// Optical, not arithmetic: geometric centring still reads a shade low against the
    /// buttons, so the box is lifted one pixel past it.
    /// </summary>
    private const float SpinnerBoxLift = 1f;
    private const float ControlHeight = 26f;
    private const float ButtonHeight = 28f;
    /// <summary>Clears the window's bottom frame without leaving a band of nothing.</summary>
    private const float BottomMargin = 14f;

    /// <summary>Frames of quiet after a change before the config is written.</summary>
    private const int SaveDelayFrames = 30;

    /// <summary>How long the delete button stays armed before giving up on you.</summary>
    private const int ConfirmFrames = 240;

    private readonly Func<bool> onClearHistory;
    private readonly Action openDatePicker;
    private readonly Action<DateOnly?> setTarget;

    private readonly List<NodeBase> owned = [];

    private CheckboxNode pauseWhileAfk = null!;
    private SliderNode outlierSlider = null!;
    private SliderNode paceWindowSlider = null!;

    private TextInputNode targetInput = null!;
    private TextNode targetHint = null!;
    private NumericInputNode dayStartInput = null!;

    private CheckboxNode showBreakdown = null!;
    private CheckboxNode openOnLogin = null!;
    private CheckboxNode chatOnComplete = null!;

    private TextButtonNode clearButton = null!;

    /// <summary>
    /// Height the content came to. Only ever compared against the constructed size -
    /// resizing at runtime does not redraw the window frame, so it cannot fix a
    /// mismatch, it can only hide one until the next open.
    /// </summary>
    private float desiredHeight;

    /// <summary>
    /// False until OnSetup has built the nodes. KamiToolKit documents OnUpdate as
    /// running while the addon exists but before it is opened, so the callback can
    /// arrive with every field still null.
    /// </summary>
    private bool ready;

    /// <summary>Gap between the spinner's box and its value text, measured at setup.</summary>
    private float spinnerValueOffset;

    private bool dirty;
    private int quietFrames;
    private int confirmFrames;

    public NativeConfigWindow(Func<bool> onClearHistory, Action openDatePicker, Action<DateOnly?> setTarget)
    {
        this.onClearHistory = onClearHistory;
        this.openDatePicker = openDatePicker;
        this.setTarget = setTarget;
    }

    /// <summary>
    /// Pulls the target field and its hint back from the config. Called after the
    /// calendar sets a date, since that happens in another window entirely.
    /// </summary>
    public void RefreshTarget()
    {
        if (!ready)
            return;

        targetInput.String = DisplayDate();
        RefreshHint();
    }

    protected override unsafe void OnSetup(AtkUnitBase* addon, Span<AtkValue> values)
    {
        // OnSetup runs again on every open, against a freshly built addon - the node
        // list has to start empty or the previous open's dead nodes come with it.
        owned.Clear();

        var config = Plugin.Config;

        var x = ContentStartPosition.X;
        var width = ContentSize.X;
        var y = ContentStartPosition.Y;
        var controlX = x + LabelWidth;
        var controlWidth = width - LabelWidth;

        y = Heading("Measurement", x, y, width);

        pauseWhileAfk = Checkbox("Pause the clock while flagged AFK", config.PauseWhileAfk, x, ref y, width);
        pauseWhileAfk.OnClick = isChecked =>
        {
            Plugin.Config.PauseWhileAfk = isChecked;
            MarkDirty();
        };

        Label("Outlier threshold (minutes)", x, y, LabelWidth);
        outlierSlider = Slider(config.OutlierMinutes, 15, 240, controlX, y, controlWidth);
        outlierSlider.OnValueChanged = value =>
        {
            Plugin.Config.OutlierMinutes = value;
            MarkDirty();
        };
        y += ControlHeight + 2f;

        Label("Pace window (quests)", x, y, LabelWidth);
        paceWindowSlider = Slider(config.PaceWindow, 0, 200, controlX, y, controlWidth);
        paceWindowSlider.OnValueChanged = value =>
        {
            Plugin.Config.PaceWindow = value;
            MarkDirty();
        };
        y += ControlHeight + Gap;

        y = Heading("Target date", x, y, width);

        Label("Finish the MSQ by", x, y, LabelWidth);
        targetInput = new TextInputNode
        {
            Position = new Vector2(controlX, y),
            Size = new Vector2(ControlWidth, ControlHeight),
            IsVisible = true,
            MaxCharacters = 10,
            // Off: the counter draws on top of the placeholder, and "0 / 10" over
            // "YYYY-MM-DD" is worse than no counter at all.
            ShowLimitText = false,
            PlaceholderString = DailyGoal.Pattern.ToUpperInvariant(),
            String = DisplayDate(),
        };
        targetInput.OnInputComplete = input =>
        {
            // Shown in the chosen format, stored as ISO - and only ever stored when it
            // parses. Keeping junk would leave the config holding something the field
            // cannot display, so the box looks empty while a target is supposedly set.
            var typed = input.ToString();

            if (!DailyGoal.TryParse(typed, out var parsed))
            {
                ShowBadDate(typed);
                return;
            }

            setTarget(parsed);

            // RefreshTarget, not RefreshHint alone: the field is still showing exactly
            // what was typed, which may be a different format from the one selected.
            RefreshTarget();
            MarkDirty();
        };
        owned.Add(targetInput);

        y += ControlHeight + 2f;

        // Two buttons, not a row of offsets: the calendar covers any date, and the
        // offsets could only ever reach the handful they named.
        var pairGap = 4f;
        var pairWidth = (ControlWidth - pairGap) / 2f;

        var pickButton = new TextButtonNode
        {
            Position = new Vector2(controlX, y),
            Size = new Vector2(pairWidth, ButtonHeight),
            IsVisible = true,
            String = "Pick a date...",
            OnClick = () => openDatePicker(),
        };
        owned.Add(pickButton);

        var clearTarget = new TextButtonNode
        {
            Position = new Vector2(controlX + pairWidth + pairGap, y),
            Size = new Vector2(pairWidth, ButtonHeight),
            IsVisible = true,
            String = "Clear",
        };
        clearTarget.OnClick = () =>
        {
            setTarget(null);
            RefreshTarget();
            MarkDirty();
        };
        owned.Add(clearTarget);
        y += ButtonHeight + 2f;

        targetHint = Label(string.Empty, controlX, y, width - LabelWidth);
        y += Line + 6f;

        y = Rule(x, y, width);

        Label("Date format", x, y, LabelWidth);

        var current = Array.FindIndex(DateFormatOptions, option => option.Pattern == DailyGoal.Pattern);
        var formatDropDown = new StringDropDownNode
        {
            Position = new Vector2(controlX, y - 2f),
            Size = new Vector2(ControlWidth, ControlHeight),
            IsVisible = true,
            MaxListOptions = DateFormatOptions.Length,
            Options = DateFormatOptions.Select(option => option.Label).ToList(),
            SelectedOption = DateFormatOptions[Math.Max(0, current)].Label,
        };

        formatDropDown.OnOptionSelected = label =>
        {
            var match = Array.Find(DateFormatOptions, option => option.Label == label);
            if (match.Pattern is null)
                return;

            Plugin.Config.DateFormat = match.Pattern;

            // The field and its placeholder are still showing the old format.
            targetInput.PlaceholderString = DailyGoal.Pattern.ToUpperInvariant();
            targetInput.String = DisplayDate();

            RefreshHint();
            MarkDirty();
        };

        owned.Add(formatDropDown);
        y += ControlHeight + 6f;

        Label("A new day starts at (hour)", x, y, LabelWidth);
        dayStartInput = new NumericInputNode
        {
            Position = new Vector2(controlX, y),
            Size = new Vector2(SpinnerWidth, SpinnerHeight),
            IsVisible = true,
            Min = 0,
            Max = 23,
            Step = 1,
            Value = config.DayStartHour,
        };
        dayStartInput.OnValueUpdate = value =>
        {
            Plugin.Config.DayStartHour = value;
            RefreshHint();
            MarkDirty();
        };
        owned.Add(dayStartInput);

        // How far the value sits inside its box, kept so centring the box below can
        // carry the text with it instead of pulling the two apart.
        spinnerValueOffset =
            dayStartInput.ValueTextNode.Position.Y - dayStartInput.BackgroundNode.Position.Y;

        y += SpinnerHeight + 2f;

        Label("Quests finished before this hour count toward the previous day.", x, y, width);
        y += Line + Gap;

        y = Heading("Display", x, y, width);

        showBreakdown = Checkbox("Show the per-expansion breakdown", config.ShowExpansionBreakdown, x, ref y, width);
        showBreakdown.OnClick = isChecked =>
        {
            Plugin.Config.ShowExpansionBreakdown = isChecked;
            MarkDirty();
        };

        openOnLogin = Checkbox("Open the window on login", config.OpenOnLogin, x, ref y, width);
        openOnLogin.OnClick = isChecked =>
        {
            Plugin.Config.OpenOnLogin = isChecked;
            MarkDirty();
        };

        chatOnComplete = Checkbox(
            "Report the new estimate in chat after each quest",
            config.ChatOnQuestComplete,
            x,
            ref y,
            width);
        chatOnComplete.OnClick = isChecked =>
        {
            Plugin.Config.ChatOnQuestComplete = isChecked;
            MarkDirty();
        };

        y += Gap;
        y = Heading("Data", x, y, width);

        clearButton = new TextButtonNode
        {
            Position = new Vector2(x, y),
            Size = new Vector2(260f, ButtonHeight),
            IsVisible = true,
            String = ClearLabel,
        };
        clearButton.OnClick = ClearClicked;
        owned.Add(clearButton);
        y += ButtonHeight;

        AddNode(owned);

        ready = true;
        RefreshHint();
        desiredHeight = y + BottomMargin;

        // Resizing an open window updates Size but does not redraw the frame, in
        // either direction - too small clips the last control, too large leaves dead
        // space, and both only come right on the next open. So the constructed height
        // has to be exact, and this says so when it is not.
        if (Math.Abs(desiredHeight - Size.Y) > 2f)
        {
            Services.Log.Warning(
                "Settings window is {Actual}px but the content needs {Needed}px - "
                + "set that height in Plugin.cs.",
                Size.Y,
                desiredHeight);
        }
    }

    /// <summary>
    /// One label per pattern. A fixed date rather than today's on purpose: the three
    /// have to stay visually distinct, and 31.12. is unambiguous in a way that, say,
    /// the 5th of June is not.
    /// </summary>
    private static readonly (string Label, string Pattern)[] DateFormatOptions =
    [
        ("31.12.2026", DailyGoal.GermanFormat),
        ("2026-12-31", DailyGoal.IsoFormat),
        ("12/31/2026", DailyGoal.UsFormat),
    ];

    private const string ClearLabel = "Clear this character's measured history";
    private const string ConfirmLabel = "Click again to delete - this cannot be undone";

    /// <summary>
    /// Two-step rather than a confirmation dialog: one click arms it, a second within
    /// a few seconds does it. Deleting a history silently on a misclick is the one
    /// irreversible thing this window can do.
    /// </summary>
    private void ClearClicked()
    {
        if (confirmFrames > 0)
        {
            // Reports what actually happened. There is no history to clear until a
            // character has loaded, and saying "cleared" for a no-op on the one
            // irreversible control in this window is worse than saying nothing.
            var cleared = onClearHistory();

            confirmFrames = 0;
            clearButton.String = ClearLabel;

            Services.Chat.Print(cleared
                ? "[Scenariometer] Measured quest history cleared for this character."
                : "[Scenariometer] No history to clear - log in to a character first.");
            return;
        }

        confirmFrames = ConfirmFrames;
        clearButton.String = ConfirmLabel;
    }

    protected override unsafe void OnHide(AtkUnitBase* addon) => Flush();

    protected override unsafe void OnFinalize(AtkUnitBase* addon)
    {
        Flush();
        ready = false;
    }

    /// <summary>
    /// Writes a pending change immediately. The debounce lives in OnUpdate, which
    /// stops running once the window closes - without this, ticking a checkbox and
    /// closing the window inside half a second threw the change away.
    /// </summary>
    private void Flush()
    {
        if (!dirty)
            return;

        dirty = false;
        quietFrames = 0;
        Plugin.Config.Save();
    }

    protected override unsafe void OnUpdate(AtkUnitBase* addon)
    {
        if (!ready)
            return;

        // Every frame, not once at setup: ShowLimitText = false does not take, and
        // focusing the field puts the counter back. "0 / 10" over the YYYY-MM-DD
        // placeholder is the one thing this window gets wrong if left alone.
        targetInput.TextLimitsNode.IsVisible = false;

        // NumericInputNode pins its +/- buttons to the top of itself but drops the
        // value box three pixels below that, so the box reads as sitting low against
        // them. Centre it - and carry the text with it. Absolute, not relative, so
        // repeating it every frame lands in the same place rather than creeping.
        var box = dayStartInput.BackgroundNode;
        var boxY = ((SpinnerHeight - box.Height) / 2f) - SpinnerBoxLift;

        box.Position = new Vector2(box.Position.X, boxY);
        dayStartInput.ValueTextNode.Position = new Vector2(
            dayStartInput.ValueTextNode.Position.X,
            boxY + spinnerValueOffset);

        if (confirmFrames > 0 && --confirmFrames == 0)
            clearButton.String = ClearLabel;

        if (!dirty)
            return;

        // Wait for the dragging to stop before writing, so one gesture is one save.
        if (++quietFrames < SaveDelayFrames)
            return;

        Flush();
    }

    private void MarkDirty()
    {
        dirty = true;
        quietFrames = 0;
    }

    /// <summary>The stored date rendered the way the user has asked to see it.</summary>
    private static string DisplayDate() =>
        DailyGoal.TryParse(Plugin.Config.TargetDate, out var date) ? DailyGoal.Format(date) : string.Empty;

    /// <summary>
    /// Complains in the format the user actually picked. Telling someone running the
    /// German format to "use YYYY-MM-DD" contradicts the placeholder in the very field
    /// they just typed into.
    /// </summary>
    private void ShowBadDate(string typed)
    {
        var example = DailyGoal.Format(new DateOnly(2026, 12, 31));

        targetHint.String = string.IsNullOrWhiteSpace(typed)
            ? $"Not a date. Use {DailyGoal.Pattern.ToUpperInvariant()}, for example {example}."
            : $"\"{typed}\" is not a date. Use {DailyGoal.Pattern.ToUpperInvariant()}, "
                + $"for example {example}.";
    }

    /// <summary>Says what the typed date actually means, so a typo shows up here.</summary>
    private void RefreshHint()
    {
        var value = Plugin.Config.TargetDate;

        if (string.IsNullOrWhiteSpace(value))
        {
            targetHint.String = "No target set. A date here turns into a per-day goal.";
            return;
        }

        if (!DailyGoal.TryParse(value, out var date))
        {
            ShowBadDate(value);
            return;
        }

        var today = DailyGoal.LogicalDate(DateTimeOffset.Now, Plugin.Config.DayStartHour);
        var days = date.DayNumber - today.DayNumber + 1;

        targetHint.String = days <= 0
            ? $"{DailyGoal.Format(date)} is in the past."
            : $"{DailyGoal.Format(date)} - {days} {(days == 1 ? "day" : "days")} including today.";
    }

    /// <summary>A rule on its own, for splitting a section without a new heading.</summary>
    private float Rule(float x, float y, float width)
    {
        owned.Add(new HorizontalLineNode
        {
            Position = new Vector2(x, y),
            Size = new Vector2(width, 4f),
            IsVisible = true,
        });

        return y + 10f;
    }

    private float Heading(string text, float x, float y, float width)
    {
        var node = new TextNode
        {
            Position = new Vector2(x, y),
            Size = new Vector2(width, Line),
            IsVisible = true,
            String = text,
        };

        owned.Add(node);

        var rule = new HorizontalLineNode
        {
            Position = new Vector2(x, y + Line),
            Size = new Vector2(width, 4f),
            IsVisible = true,
        };

        owned.Add(rule);

        return y + Line + 8f;
    }

    private TextNode Label(string text, float x, float y, float width)
    {
        var node = new TextNode
        {
            Position = new Vector2(x, y + 4f),
            Size = new Vector2(width, Line),
            IsVisible = true,
            String = text,
        };

        owned.Add(node);
        return node;
    }

    private CheckboxNode Checkbox(string label, bool value, float x, ref float y, float width)
    {
        var node = new CheckboxNode
        {
            Position = new Vector2(x, y),
            Size = new Vector2(width, ControlHeight),
            IsVisible = true,
            IsChecked = value,
            String = label,
        };

        owned.Add(node);
        y += ControlHeight + 2f;
        return node;
    }

    private SliderNode Slider(int value, int min, int max, float x, float y, float width)
    {
        var node = new SliderNode
        {
            Position = new Vector2(x, y),
            Size = new Vector2(width, ControlHeight),
            IsVisible = true,
            Min = min,
            Max = max,
            Step = 1,
            Value = value,
        };

        owned.Add(node);
        return node;
    }
}
