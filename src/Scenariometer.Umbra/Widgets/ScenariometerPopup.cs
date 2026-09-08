using System;
using System.Collections.Generic;
using System.Globalization;
using Scenariometer.Contract;
using Umbra.Common;
using Umbra.Widgets;
using Umbra.Windows.Components;
using Una.Drawing;

namespace ScenariometerUmbra.Widgets;

/// <summary>
/// The widget's popup: the plugin's own window, redrawn in Umbra's toolbar.
///
/// It mirrors the layout of Scenariometer's main window - header, pace, target,
/// expansion breakdown - because the two are showing the same thing and looking like
/// two different reports of it helps nobody. What it does not do is compute any of
/// it: the MSQ index, the clock and the estimator live on the plugin side, and every
/// figure here arrives over IPC already decided.
///
/// Nodes are built once in the constructor and only ever have their strings, sizes
/// and visibility changed in <see cref="OnUpdate"/>. Una.Drawing is retained mode,
/// so the per-expansion rows and the bar ticks are fixed pools that get filled and
/// hidden, exactly as the native window does it - not lists that grow.
///
/// Widths are fixed rather than derived, because the popup has no reason to be
/// elastic and a bar needs a pixel width before its fill and its tick marks can be
/// placed. Una.Drawing multiplies them by the UI scale, so they are not "fixed" on
/// screen, only fixed relative to each other.
/// </summary>
internal sealed class ScenariometerPopup : WidgetPopup
{
    /// <summary>The Main Scenario quest marker, as the game draws it in the journal.</summary>
    internal const uint MsqIconId = 71001;

    private const float PopupWidth = 360f;
    private const float Pad = 12f;
    private const float ContentWidth = PopupWidth - (Pad * 2f);

    private const float IconSize = 40f;
    private const float IconGap = 10f;

    /// <summary>Everything to the right of an icon: the label line and the bar under it.</summary>
    private const float BarWidth = ContentWidth - IconSize - IconGap;

    private const float BarHeight = 12f;
    private const float Line = 20f;
    private const float CaptionLine = 16f;

    /// <summary>The right-hand column of a label row. The labels are the shorter half.</summary>
    private const float ValueWidth = ContentWidth * 0.55f;

    /// <summary>The counts beside an expansion's name, right-aligned into a column.</summary>
    private const float CountWidth = 90f;

    private const int TextSize = 13;
    private const int SmallTextSize = 11;

    /// <summary>
    /// The air on each side of a rule. Carried by the containers' Gap rather than by
    /// the rule's own Margin, so the rule keeps the height it is given.
    /// </summary>
    private const float SectionGap = 8f;

    /// <summary>
    /// Headroom over the expansions that exist today, so a new one on patch day shows
    /// up without a code change here. Unused rows are hidden and take no space.
    /// </summary>
    private const int MaxExpansionRows = 10;

    /// <summary>
    /// Tick marks per bar: expansion boundaries on the overall one, journal chapters
    /// on a row. Sized with room to spare, and the surplus stays hidden.
    /// </summary>
    private const int MaxMarksOverall = 12;
    private const int MaxMarksPerRow = 6;

    /// <summary>
    /// Today's bar divides one tick per quest owed, so the day reads as a count
    /// rather than a proportion - but only up to this many. A thirty-quest day would
    /// be a hatched block, which says less than a plain bar does.
    /// </summary>
    private const int MaxTodayMarks = 10;

    /// <summary>
    /// Full height, unlike the plugin window's inset ticks. There a tick is drawn over
    /// the bar and can afford to stop short of its edges; here it is a piece OF the
    /// bar, so anything less leaves a notch of bare track above and below it.
    /// </summary>
    private const float MarkWidth = 2f;

    // --- palette -----------------------------------------------------------------
    // The bar colours are the plugin window's, deliberately: the two are showing the
    // same bars and a different gold in each would read as a different measurement.
    // Everything that is text or chrome uses Umbra's named theme colours instead, so
    // the popup follows whatever colour profile the user has picked.

    /// <summary>MSQ progress: the game's own warm gold, as on an experience bar.</summary>
    private static readonly Color ProgressFill = Rgba(245, 199, 97, 255);

    /// <summary>The unfilled track behind it - dark, so the fill carries the eye.</summary>
    private static readonly Color ProgressTrack = Rgba(20, 20, 26, 217);

    /// <summary>
    /// Today's goal, deliberately a different hue from MSQ progress: the two bars sit
    /// in the same popup and measure completely different things.
    /// </summary>
    private static readonly Color TodayFill = Rgba(148, 204, 107, 255);

    /// <summary>
    /// Light rather than dark: a dark tick reads well on the gold fill and vanishes
    /// against the track, and the boundaries still to come are the interesting ones.
    ///
    /// The alpha is doing real work. Only the track sits behind a tick, so the
    /// translucency resolves to one mid grey everywhere - softer against the gold than
    /// an opaque divider, which reads as a hard edge.
    /// </summary>
    private static readonly Color MarkColor = Rgba(255, 255, 255, 140);

    /// <summary>
    /// Una.Drawing's four-byte Color constructor does not take its channels in the
    /// order its parameter names claim: the game's gold, passed as (245, 199, 97),
    /// rendered as light blue - the same colour with red and blue exchanged. Alpha
    /// lands where it should. Everything here goes through this so the call sites
    /// above can stay in plain RGB order.
    /// </summary>
    private static Color Rgba(byte r, byte g, byte b, byte a) => new(b, g, r, a);

    private readonly ScenariometerApi api;
    private readonly Node root;

    private readonly Node messageNode;

    // --- MSQ section ---
    private readonly Node msqSection;
    private readonly Node msqIcon;
    private readonly Node questName;
    private readonly Node chapterName;
    private readonly Bar overallBar;
    private readonly Node overallCaption;

    // --- pace section ---
    private readonly Node paceSection;
    private readonly LabelRow paceRow;
    private readonly LabelRow expansionLeftRow;
    private readonly LabelRow msqLeftRow;

    // --- target section ---
    private readonly Node targetSection;
    private readonly LabelRow targetRow;
    private readonly Bar todayBar;
    private readonly LabelRow todayRow;
    private readonly LabelRow planRow;

    // --- expansion section ---
    private readonly Node expansionSection;
    private readonly List<ExpansionRow> expansionRows = [];

    private readonly Node footer;
    private readonly ButtonNode pauseButton;

    /// <summary>
    /// Every region, with the rule drawn above it and the container holding the two.
    /// Recorded by <see cref="Separated"/> as the tree is built, so the pairing comes
    /// from the same call that creates them rather than from two lists that have to be
    /// kept in the same order by hand. Which rules are drawn depends on what is
    /// switched on, and is decided in <see cref="LayoutSeparators"/> - a rule above a
    /// hidden section is a line to nowhere.
    /// </summary>
    private readonly List<(Node Rule, Node Wrapper, Node Content)> sections = [];

    public ScenariometerPopup()
    {
        api = Framework.Service<ScenariometerApi>();

        messageNode = NewText(SmallTextSize, MutedColor(), Anchor.MiddleCenter, ContentWidth, Line);

        // --- MSQ -------------------------------------------------------------------
        msqIcon = NewIcon(MsqIconId);
        questName = NewText(TextSize, TextColor(), Anchor.MiddleLeft, BarWidth - CountWidth - 8f, Line);
        chapterName = NewText(SmallTextSize, MutedColor(), Anchor.MiddleRight, CountWidth, Line);
        overallBar = new Bar(BarWidth, ProgressFill, MaxMarksOverall);
        overallCaption = NewText(SmallTextSize, MutedColor(), Anchor.MiddleCenter, BarWidth, CaptionLine);

        msqSection = new Node
        {
            Id = "MsqSection",
            Style = SectionStyle(IconGap),
            ChildNodes =
            [
                new Node
                {
                    Style = new Style
                    {
                        Flow = Flow.Horizontal,
                        Gap = IconGap,
                        Size = new Size(ContentWidth, 0),
                        AutoSize = (AutoSize.Fit, AutoSize.Fit),
                    },
                    ChildNodes =
                    [
                        msqIcon,
                        new Node
                        {
                            Style = new Style
                            {
                                Flow = Flow.Vertical,
                                Gap = 2,
                                Size = new Size(BarWidth, 0),
                                AutoSize = (AutoSize.Fit, AutoSize.Fit),
                            },
                            ChildNodes =
                            [
                                new Node
                                {
                                    Style = RowStyle(BarWidth),
                                    ChildNodes = [questName, chapterName],
                                },
                                overallBar.Root,
                                overallCaption,
                            ],
                        },
                    ],
                },
            ],
        };

        // --- pace ------------------------------------------------------------------
        paceRow = new LabelRow();
        expansionLeftRow = new LabelRow();
        msqLeftRow = new LabelRow();

        paceSection = new Node
        {
            Id = "PaceSection",
            Style = SectionStyle(2),
            ChildNodes = [paceRow.Root, expansionLeftRow.Root, msqLeftRow.Root],
        };

        // --- target ----------------------------------------------------------------
        targetRow = new LabelRow();
        todayBar = new Bar(ContentWidth, TodayFill, MaxTodayMarks);
        todayRow = new LabelRow();
        planRow = new LabelRow();

        targetSection = new Node
        {
            Id = "TargetSection",
            Style = SectionStyle(2),
            ChildNodes = [targetRow.Root, todayBar.Root, todayRow.Root, planRow.Root],
        };

        // --- expansions ------------------------------------------------------------
        expansionSection = new Node
        {
            Id = "ExpansionSection",
            Style = SectionStyle(6),
        };

        for (var i = 0; i < MaxExpansionRows; i++)
        {
            var row = new ExpansionRow();
            expansionRows.Add(row);
            expansionSection.ChildNodes.Add(row.Root);
        }

        // --- footer ----------------------------------------------------------------
        pauseButton = new ButtonNode("ScenariometerPause", "Pause tracking");
        pauseButton.OnClick += _ => TogglePause();

        footer = new Node
        {
            Id = "Footer",
            Style = SectionStyle(0),
            ChildNodes = [pauseButton],
        };

        // Each section carries the rule that sits above it, so hiding a section hides
        // its rule with it and no arithmetic is needed to keep the two in step.
        root = new Node
        {
            Id = "ScenariometerPopup",
            Style = new Style
            {
                Flow = Flow.Vertical,
                Gap = SectionGap,
                Padding = new EdgeSize(Pad),
                Size = new Size(PopupWidth, 0),
                AutoSize = (AutoSize.Fit, AutoSize.Fit),
            },
            ChildNodes =
            [
                messageNode,
                Separated(msqSection),
                Separated(paceSection),
                Separated(targetSection),
                Separated(expansionSection),
                Separated(footer),
            ],
        };
    }

    protected override Node Node => root;

    /// <summary>Set by the widget from its own config before each draw.</summary>
    public bool ShowMsq { get; set; } = true;

    public bool ShowPace { get; set; } = true;

    public bool ShowTarget { get; set; } = true;

    public bool ShowExpansions { get; set; } = true;

    /// <summary>
    /// Wraps a section in a container that also holds the rule drawn above it. The
    /// rule is the container's first child, so hiding the container removes both.
    /// </summary>
    private Node Separated(Node section)
    {
        // Umbra's own recipe, lifted from its popup menus: a one-pixel node that grows
        // to the width it is given, spaced by the containers' Gap. A fixed width with
        // the spacing as a Margin draws nothing at all.
        var rule = new Node
        {
            Style = new Style
            {
                Size = new Size(0, 1),
                AutoSize = (AutoSize.Grow, AutoSize.Fit),
                BackgroundColor = new Color("Widget.Border"),
            },
        };

        var wrapper = new Node
        {
            Style = new Style
            {
                Flow = Flow.Vertical,
                Gap = SectionGap,
                Size = new Size(ContentWidth, 0),
                AutoSize = (AutoSize.Fit, AutoSize.Fit),
            },
            ChildNodes = [rule, section],
        };

        sections.Add((rule, wrapper, section));

        return wrapper;
    }

    /// <summary>
    /// What the popup was last built from. Reading the snapshot is what gives the API
    /// the chance to poll, so this is compared after that read, never before it.
    /// </summary>
    private (int Version, bool Msq, bool Pace, bool Target, bool Expansions) drawn = (-1, false, false, false, false);

    /// <summary>Anything already on screen belongs to a previous open; redraw once.</summary>
    protected override void OnOpen() => drawn = (-1, false, false, false, false);

    protected override void OnUpdate()
    {
        var snapshot = api.Snapshot;

        // This runs every frame the popup is open, against figures that move once a
        // second at most. Rebuilding regardless cost around 150 allocations a frame -
        // Una.Drawing's Size is a class, so every node resize is one - to arrive at
        // the same report. The API's version counter covers the data, including a
        // pause it writes into the cached snapshot in place; the flags cover what the
        // widget's settings say to show.
        var state = (api.Version, ShowMsq, ShowPace, ShowTarget, ShowExpansions);
        if (state == drawn)
            return;

        drawn = state;

        if (snapshot is null || !snapshot.LoggedIn || snapshot.Total == 0)
        {
            ShowMessage(
                snapshot is null ? api.Unavailable
                : !snapshot.LoggedIn ? "Not logged in."
                : "Reading Main Scenario progress...");
            return;
        }

        messageNode.Style.IsVisible = false;

        DrawMsq(snapshot);
        DrawPace(snapshot);
        DrawTarget(snapshot);
        DrawExpansions(snapshot);

        pauseButton.Label = snapshot.Paused ? "Resume tracking" : "Pause tracking";
        footer.Style.IsVisible = true;

        LayoutSeparators();
    }

    /// <summary>
    /// One line instead of the whole report - not running, not logged in, or the
    /// plugin has not finished reading the journal yet. None of those are errors.
    /// </summary>
    private void ShowMessage(string message)
    {
        messageNode.NodeValue = message;
        messageNode.Style.IsVisible = true;

        // Through the same list LayoutSeparators uses, rather than naming the sections
        // again here: a hand-written second list is exactly what that list exists to
        // avoid, and a section added later would otherwise keep drawing over the
        // message.
        foreach (var (_, _, content) in sections)
            content.Style.IsVisible = false;

        LayoutSeparators();
    }

    /// <summary>
    /// Shows the rule above a section only when there is something above it to
    /// separate from. Without this the first visible section opens with a stray line
    /// under the popup's own top edge.
    /// </summary>
    private void LayoutSeparators()
    {
        var anythingAbove = messageNode.Style.IsVisible ?? false;

        foreach (var (rule, wrapper, content) in sections)
        {
            var visible = content.Style.IsVisible ?? true;

            // The container has to follow its section, or a hidden section still
            // reserves its gap.
            wrapper.Style.IsVisible = visible;
            rule.Style.IsVisible = visible && anythingAbove;

            anythingAbove |= visible;
        }
    }

    private void DrawMsq(ScenariometerSnapshot snapshot)
    {
        msqSection.Style.IsVisible = ShowMsq;
        if (!ShowMsq)
            return;

        // No label in front of the quest name: the header reads as a heading, and
        // "Current:" only tells you what a quest name obviously is.
        questName.NodeValue = snapshot.CurrentQuest.Length > 0
            ? snapshot.CurrentQuest
            : "Main Scenario complete.";

        chapterName.NodeValue = snapshot.CurrentChapter;

        overallBar.Set(snapshot.Fraction, snapshot.Marks);

        // Invariant, not the OS culture: the client is running in English and a
        // "45,9%" in the middle of English text reads as a bug.
        overallCaption.NodeValue =
            $"{snapshot.Completed} / {snapshot.Total}   "
            + $"({(snapshot.Fraction * 100).ToString("0.0", CultureInfo.InvariantCulture)}%)";
    }

    private void DrawPace(ScenariometerSnapshot snapshot)
    {
        var show = ShowPace && snapshot.Remaining > 0;
        paceSection.Style.IsVisible = show;

        if (!show)
            return;

        if (!snapshot.HasEstimate)
        {
            paceRow.Set("Measuring your pace", $"{snapshot.SampleCount} of {snapshot.MinimumSamples} quests");
            expansionLeftRow.Set("Estimates", "once there is enough to go on");
            msqLeftRow.Hide();
            return;
        }

        // The sample count rides on the label rather than the value: it qualifies what
        // the number means, and the value column is for the number itself.
        paceRow.Set(
            $"Pace (median of {snapshot.SampleCount})",
            $"{ScenariometerFormat.Duration(snapshot.PaceSeconds)} per quest");

        if (snapshot.ExpansionName.Length > 0)
            expansionLeftRow.Set($"Rest of {snapshot.ExpansionName}", ScenariometerFormat.Duration(snapshot.ExpansionRemainingSeconds));
        else
            expansionLeftRow.Hide();

        msqLeftRow.Set("Rest of the MSQ", ScenariometerFormat.Duration(snapshot.RemainingSeconds));
    }

    private void DrawTarget(ScenariometerSnapshot snapshot)
    {
        var show = ShowTarget && snapshot.HasTarget && snapshot.Remaining > 0;
        targetSection.Style.IsVisible = show;

        if (!show)
            return;

        if (snapshot.Overdue)
        {
            targetRow.Set("Target (passed)", snapshot.TargetDate);
            todayRow.Set("Still to go", $"{snapshot.Remaining} {ScenariometerFormat.Quests(snapshot.Remaining)}");
            planRow.Hide();
            todayBar.Root.Style.IsVisible = false;
            return;
        }

        // The days left qualify the date, so they ride on the label; the per-day figure
        // is not repeated here because the Today row below states it as "of N".
        targetRow.Set(
            $"Target ({snapshot.DaysRemaining} {ScenariometerFormat.Days(snapshot.DaysRemaining)} left)",
            snapshot.TargetDate);

        todayBar.Root.Style.IsVisible = true;
        todayBar.Set(snapshot.TodayFraction, TodayMarks(snapshot.QuotaToday));

        // Always shown, not only once today is finished: standing against the plan is
        // the thing a target date is for.
        planRow.Set("Plan", ScenariometerFormat.PlanStanding(snapshot.AheadBy));

        if (snapshot.DoneForToday)
        {
            todayRow.Set("Today", $"{snapshot.CompletedToday} done - finished");
            return;
        }

        todayRow.Set(
            "Today",
            snapshot.HasEstimate
                ? $"{snapshot.CompletedToday} of {snapshot.QuotaToday} - about "
                    + $"{ScenariometerFormat.Duration(snapshot.RemainingToday * snapshot.PaceSeconds)} left"
                : $"{snapshot.CompletedToday} of {snapshot.QuotaToday} done");
    }

    /// <summary>
    /// One division per quest owed today, so the bar can be read as "three of five" at
    /// a glance. Skipped when there are too many to tell apart, and when there is one
    /// or none - a single quest needs no dividing.
    /// </summary>
    private static float[] TodayMarks(int quota)
    {
        if (quota is <= 1 or > MaxTodayMarks)
            return [];

        var marks = new float[quota - 1];
        for (var i = 1; i < quota; i++)
            marks[i - 1] = (float)i / quota;

        return marks;
    }

    private void DrawExpansions(ScenariometerSnapshot snapshot)
    {
        var show = ShowExpansions && snapshot.Expansions.Count > 0;
        expansionSection.Style.IsVisible = show;

        // Like the three sections above. Rows inside a hidden section need no hiding
        // of their own, and filling them is the most expensive thing the popup does.
        if (!show)
            return;

        for (var i = 0; i < expansionRows.Count; i++)
        {
            if (i >= snapshot.Expansions.Count)
            {
                expansionRows[i].Hide();
                continue;
            }

            expansionRows[i].Fill(snapshot.Expansions[i]);
        }
    }

    /// <summary>
    /// Flips the pause. The desired state comes from a fresh read rather than the
    /// cached snapshot: pausing from the plugin window moments earlier would otherwise
    /// make this click send the state it is already in, and it would appear to do
    /// nothing.
    /// </summary>
    private void TogglePause()
    {
        api.Invalidate();

        var snapshot = api.Snapshot;
        if (snapshot is not null)
            api.SetPaused(!snapshot.Paused);
    }

    // --- node factories ------------------------------------------------------------

    private static Color TextColor() => new("Widget.PopupMenuText");

    private static Color MutedColor() => new("Widget.PopupMenuTextMuted");

    private static Color OutlineColor() => new("Widget.PopupMenuTextOutline");

    private static Style SectionStyle(float gap) => new()
    {
        Flow = Flow.Vertical,
        Gap = gap,
        Size = new Size(ContentWidth, 0),
        AutoSize = (AutoSize.Fit, AutoSize.Fit),
    };

    /// <summary>A horizontal line of the given width, for a label/value pair.</summary>
    private static Style RowStyle(float width) => new()
    {
        Flow = Flow.Horizontal,
        Size = new Size(width, Line),
    };

    private static Node NewText(int fontSize, Color color, Anchor align, float width, float height) => new()
    {
        NodeValue = string.Empty,
        Style = new Style
        {
            Font = 0,
            FontSize = fontSize,
            Color = color,
            OutlineColor = OutlineColor(),
            OutlineSize = 1,
            Size = new Size(width, height),
            TextAlign = align,

            // A long quest name has to lose its tail rather than push the chapter off
            // the right edge: the popup is a fixed width by design.
            TextOverflow = false,
            WordWrap = false,
        },
    };

    private static Node NewIcon(uint iconId) => new()
    {
        Style = new Style
        {
            Size = new Size(IconSize, IconSize),
            IconId = iconId,
        },
    };

    /// <summary>
    /// A progress bar with tick marks, hand-rolled rather than Umbra's ProgressBarNode
    /// because that one owns its own children and has nowhere to put the divisions.
    ///
    /// Nothing here overlaps anything. The bar is a single left-to-right run of
    /// alternating segments and ticks that between them add up to exactly its width -
    /// segment, tick, segment, tick, segment - and each segment carries its own gold
    /// part, as wide as the progress that falls inside that segment. So a tick is not
    /// drawn over the fill; it is drawn between two pieces of it.
    ///
    /// Flow rather than an overlay, because Una.Drawing's stacking is not what it
    /// looks like. Anchor decides where a node is drawn but not whether it takes up
    /// room: an anchored node is painted at its anchor while its siblings are still
    /// pushed along as though it sat in the flow. And siblings paint back to front, so
    /// the FIRST child ends up on top. Drawing ticks over a fill needs both of those
    /// right at once; laying them end to end needs neither.
    ///
    /// Margin is unused throughout: on a node in the flow it shoves the siblings after
    /// it along, which would move every tick after the first.
    /// </summary>
    private sealed class Bar
    {
        private readonly float width;
        private readonly List<Segment> segments = [];
        private readonly List<Node> ticks = [];

        public Bar(float width, Color fillColor, int maxTicks)
        {
            this.width = width;

            Root = new Node
            {
                Style = new Style
                {
                    Flow = Flow.Horizontal,
                    Gap = 0,
                    Size = new Size(width, BarHeight),
                    BackgroundColor = ProgressTrack,
                    BorderRadius = 3,
                    RoundedCorners = RoundedCorners.All,
                },
            };

            // One more segment than ticks: the run starts and ends with one.
            for (var i = 0; i <= maxTicks; i++)
            {
                var segment = new Segment(fillColor);
                segments.Add(segment);
                Root.ChildNodes.Add(segment.Root);

                if (i == maxTicks)
                    break;

                var tick = new Node
                {
                    Style = new Style
                    {
                        Size = new Size(MarkWidth, BarHeight),
                        BackgroundColor = MarkColor,
                        IsVisible = false,
                    },
                };

                ticks.Add(tick);
                Root.ChildNodes.Add(tick);
            }
        }

        public Node Root { get; }

        /// <summary>
        /// Marks are fractions of the bar, in ascending order - which every producer
        /// of them is: expansion boundaries, journal chapters and today's quota all
        /// count upwards.
        /// </summary>
        public void Set(float fraction, IReadOnlyList<float> marks)
        {
            var filled = Math.Clamp(fraction, 0f, 1f) * width;

            // The furthest left a tick can start and still fit inside the bar.
            var limit = width - MarkWidth;

            // Where the run has got to. Every segment and tick advances it, and the
            // total has to come to the bar's width exactly or the flow wraps.
            var cursor = 0f;
            var used = 0;

            for (var i = 0; i < ticks.Count; i++)
            {
                // Out of marks, or out of bar. The second is not hypothetical: a tick
                // placed at the limit leaves the cursor past it, and marks packed that
                // close to the end would draw on top of each other anyway. Dropping
                // them is the only thing that keeps the arithmetic below non-negative.
                if (i >= marks.Count || cursor > limit)
                {
                    ticks[i].Style.IsVisible = false;
                    continue;
                }

                // Held between the cursor and the limit rather than clamped between
                // them: Math.Clamp throws outright when its low bound is above its
                // high one, which is exactly the case being guarded here.
                var at = Math.Max(cursor, Math.Min((width * marks[i]) - (MarkWidth / 2f), limit));

                Fill(used++, cursor, at - cursor, filled);
                ticks[i].Style.IsVisible = true;

                cursor = at + MarkWidth;
            }

            Fill(used++, cursor, width - cursor, filled);

            for (; used < segments.Count; used++)
                segments[used].Hide();
        }

        /// <summary>Sizes one segment and the part of it that is filled.</summary>
        private void Fill(int index, float start, float length, float filled)
        {
            // Floored before it is used as a clamp bound, for the same reason as above.
            var span = Math.Max(0f, length);

            segments[index].Set(span, Math.Clamp(filled - start, 0f, span));
        }

        /// <summary>
        /// A stretch of bar between two ticks, holding its own share of the fill. The
        /// gold is a child rather than a background so it can be shorter than the
        /// stretch it sits in - which is what makes the fill end mid-segment.
        /// </summary>
        private sealed class Segment
        {
            private readonly Node fill;

            public Segment(Color fillColor)
            {
                fill = new Node
                {
                    Style = new Style
                    {
                        Size = new Size(0, BarHeight),
                        BackgroundColor = fillColor,
                        IsVisible = false,
                    },
                };

                Root = new Node
                {
                    Style = new Style
                    {
                        Flow = Flow.Horizontal,
                        Gap = 0,
                        Size = new Size(0, BarHeight),
                        IsVisible = false,
                    },
                    ChildNodes = [fill],
                };
            }

            public Node Root { get; }

            /// <summary>Both figures come floored and clamped from <see cref="Bar.Fill"/>.</summary>
            public void Set(float length, float filled)
            {
                Root.Style.Size = new Size(length, BarHeight);
                Root.Style.IsVisible = length > 0f;

                fill.Style.Size = new Size(filled, BarHeight);

                // Below a pixel there is nothing to draw, and a rounding artefact at
                // the segment's left edge reads as progress that is not there.
                fill.Style.IsVisible = filled >= 1f;
            }

            public void Hide() => Root.Style.IsVisible = false;
        }
    }

    /// <summary>
    /// A label on the left and its value right-aligned on the right - the same shape
    /// the plugin window uses, so the middle of the popup does not look like a
    /// different design from the top and bottom of it.
    /// </summary>
    private sealed class LabelRow
    {
        private readonly Node label;
        private readonly Node value;

        public LabelRow()
        {
            label = NewText(TextSize, MutedColor(), Anchor.MiddleLeft, ContentWidth - ValueWidth - 8f, Line);
            value = NewText(TextSize, TextColor(), Anchor.MiddleRight, ValueWidth, Line);

            Root = new Node
            {
                Style = RowStyle(ContentWidth),
                ChildNodes = [label, value],
            };
        }

        public Node Root { get; }

        /// <summary>An empty value hides the whole row, label included.</summary>
        public void Set(string labelText, string valueText)
        {
            label.NodeValue = labelText;
            value.NodeValue = valueText;
            Root.Style.IsVisible = valueText.Length > 0;
        }

        public void Hide() => Set(string.Empty, string.Empty);
    }

    /// <summary>
    /// One expansion: its journal icon, its name, the counts and a bar with the
    /// journal's chapter boundaries marked across it.
    /// </summary>
    private sealed class ExpansionRow
    {
        private readonly Node icon;
        private readonly Node name;
        private readonly Node count;
        private readonly Bar bar;

        public ExpansionRow()
        {
            icon = NewIcon(0);
            name = NewText(TextSize, TextColor(), Anchor.MiddleLeft, BarWidth - CountWidth - 8f, Line);

            // Right against the bar's end, so the counts form a column instead of
            // drifting with the width of each number.
            count = NewText(SmallTextSize, MutedColor(), Anchor.MiddleRight, CountWidth, Line);

            bar = new Bar(BarWidth, ProgressFill, MaxMarksPerRow);

            Root = new Node
            {
                Style = new Style
                {
                    Flow = Flow.Horizontal,
                    Gap = IconGap,
                    Size = new Size(ContentWidth, 0),
                    AutoSize = (AutoSize.Fit, AutoSize.Fit),
                    IsVisible = false,
                },
                ChildNodes =
                [
                    icon,
                    new Node
                    {
                        Style = new Style
                        {
                            Flow = Flow.Vertical,
                            Gap = 2,
                            Size = new Size(BarWidth, 0),
                            AutoSize = (AutoSize.Fit, AutoSize.Fit),
                        },
                        ChildNodes =
                        [
                            new Node
                            {
                                Style = RowStyle(BarWidth),
                                ChildNodes = [name, count],
                            },
                            bar.Root,
                        ],
                    },
                ],
            };
        }

        public Node Root { get; }

        public void Fill(ScenariometerExpansion expansion)
        {
            icon.Style.IconId = expansion.IconId;
            name.NodeValue = expansion.Name;
            count.NodeValue = $"{expansion.Completed} / {expansion.Total}";
            bar.Set(expansion.Fraction, expansion.Marks);

            Root.Style.IsVisible = true;
        }

        public void Hide() => Root.Style.IsVisible = false;
    }
}
