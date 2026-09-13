using System;
using System.Collections.Generic;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;

namespace Scenariometer.Windows;

/// <summary>
/// A two-button question, as its own small game window.
///
/// The settings window answers "are you sure" with an arm-then-confirm button
/// instead (see NativeConfigWindow.ClearClicked), and that is still the right shape
/// there: one answer is destructive, the other is doing nothing. This question is a
/// different one. Both answers to "restart the plan, or keep it?" are legitimate,
/// neither is obviously the default, and a button that relabels itself cannot offer
/// two answers at once - it can only offer one and hide the other in a rule nobody
/// is told.
///
/// Deliberately no more general than that. It draws the statement it is handed, sets
/// the question against its two buttons, and reports which was pressed; every decision
/// about what to ask and what to do with the answer stays with the caller.
/// </summary>
internal sealed class NativeConfirmWindow : NativeAddon
{
    private const float Line = 22f;
    private const float ButtonHeight = 28f;
    private const float ButtonGap = 12f;

    /// <summary>
    /// Buttons are sized to their labels, not to half the window each. The content box
    /// reaches within ten pixels of the frame, so a split-the-width pair came out
    /// nearly 180px wide apiece for two words and read as two banners rather than two
    /// choices.
    /// </summary>
    private const float ButtonWidth = 120f;

    /// <summary>
    /// Above the question. The question belongs to the buttons that answer it, not to
    /// the statement it follows, so the space goes here rather than being shared out
    /// evenly - evenly spaced, all three lines read as one block and the buttons as an
    /// unrelated footer.
    /// </summary>
    private const float QuestionGap = 16f;

    /// <summary>Below the question, and deliberately small: this is the join.</summary>
    private const float ButtonRowGap = 6f;

    /// <summary>Clears the window's bottom frame without leaving a band of nothing.</summary>
    private const float BottomMargin = 14f;

    /// <summary>
    /// Nothing here wraps text, so the statement is a list of lines and the caller
    /// breaks them. The window is a fixed size - it is built in Plugin.cs like the
    /// others - so the pool is fixed too, and lines past this many would draw over the
    /// question.
    /// </summary>
    public const int MaxLines = 2;

    private readonly List<TextNode> lines = [];

    private TextNode questionText = null!;

    private TextButtonNode confirmButton = null!;
    private TextButtonNode cancelButton = null!;

    private string[] message = [];
    private string question = string.Empty;
    private string confirmLabel = string.Empty;
    private string cancelLabel = string.Empty;

    /// <summary>
    /// Answered by a button, and only by a button. Closing the window with its X is
    /// not an answer: it leaves everything exactly as it was, which is the outcome a
    /// question worth asking should default to when it is dismissed rather than read.
    /// </summary>
    private Action<bool>? onAnswer;

    private bool ready;

    /// <summary>
    /// Poses a question, replacing any still on screen. Safe to call whether the
    /// window is open or closed - the state is set first, and OnSetup draws from it.
    ///
    /// A pending question that is replaced goes unanswered rather than being answered
    /// falsely: its callback is dropped, because nobody pressed anything.
    /// </summary>
    public void Ask(string[] text, string question, string confirm, string cancel, Action<bool> answer)
    {
        // Silently drawing the first two and dropping the rest is how a question ends
        // up on screen missing the half that made it answerable.
        if (text.Length > MaxLines)
        {
            Services.Log.Warning(
                "Confirm window was handed {Count} lines but holds {Max} - the rest will not draw.",
                text.Length,
                MaxLines);
        }

        message = text;
        this.question = question;
        confirmLabel = confirm;
        cancelLabel = cancel;
        onAnswer = answer;

        if (ready)
            Refresh();

        Open();
    }

    // The signature is the game's own addon callback, pointers and all; the pointer is
    // not used, since KamiToolKit hands back the managed nodes.
    protected override unsafe void OnSetup(AtkUnitBase* addon, Span<AtkValue> values)
    {
        // OnSetup runs again on every open, against a freshly built addon - the pool
        // has to start empty or the previous open's dead nodes come with it, and the
        // lines being filled would be the detached ones.
        lines.Clear();

        var x = ContentStartPosition.X;
        var y = ContentStartPosition.Y;
        var width = Size.X - (ContentStartPosition.X * 2f);

        for (var i = 0; i < MaxLines; i++)
        {
            lines.Add(new TextNode
            {
                Position = new Vector2(x, y + (i * Line)),
                Size = new Vector2(width, Line),
                IsVisible = false,
                String = string.Empty,
                AlignmentType = AlignmentType.Top,
            });
        }

        y += (MaxLines * Line) + QuestionGap;

        questionText = new TextNode
        {
            Position = new Vector2(x, y),
            Size = new Vector2(width, Line),
            IsVisible = true,
            String = string.Empty,
            AlignmentType = AlignmentType.Top,
        };

        y += Line + ButtonRowGap;

        // Centred as a pair rather than pushed to the corners, so the two answers read
        // as a set to choose between.
        var buttonsX = x + ((width - ((ButtonWidth * 2f) + ButtonGap)) / 2f);

        // The keeping answer sits left, where the eye lands first, and the one that
        // discards something sits right. Neither is styled as the default: picking one
        // for the user is the exact thing this window exists not to do.
        cancelButton = new TextButtonNode
        {
            Position = new Vector2(buttonsX, y),
            Size = new Vector2(ButtonWidth, ButtonHeight),
            IsVisible = true,
            String = string.Empty,
            OnClick = () => Answer(false),
        };

        confirmButton = new TextButtonNode
        {
            Position = new Vector2(buttonsX + ButtonWidth + ButtonGap, y),
            Size = new Vector2(ButtonWidth, ButtonHeight),
            IsVisible = true,
            String = string.Empty,
            OnClick = () => Answer(true),
        };

        y += ButtonHeight;

        var nodes = new List<NodeBase> { cancelButton, confirmButton, questionText };
        nodes.AddRange(lines);
        AddNode(nodes);

        ready = true;
        Refresh();

        var needed = y + BottomMargin;
        if (Math.Abs(needed - Size.Y) > 2f)
        {
            Services.Log.Warning(
                "Confirm window is {Actual}px but the content needs {Needed}px - set that height in Plugin.cs.",
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

    /// <summary>
    /// Reports the answer, once. The callback is taken and cleared before the window
    /// closes: Close runs OnHide, and a second click landing during the close
    /// transition would otherwise answer the same question twice.
    /// </summary>
    private void Answer(bool confirmed)
    {
        var callback = onAnswer;
        onAnswer = null;

        Close();
        callback?.Invoke(confirmed);
    }

    private void Refresh()
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var has = i < message.Length;

            lines[i].String = has ? message[i] : string.Empty;
            lines[i].IsVisible = has;
        }

        questionText.String = question;
        cancelButton.String = cancelLabel;
        confirmButton.String = confirmLabel;
    }
}
