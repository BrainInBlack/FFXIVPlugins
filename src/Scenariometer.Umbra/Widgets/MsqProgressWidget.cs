using System;
using System.Collections.Generic;
using System.Globalization;
using Dalamud.Plugin.Services;
using Scenariometer.Contract;
using Umbra.Common;
using Umbra.Widgets;
using Una.Drawing;

namespace ScenariometerUmbra.Widgets;

/// <summary>
/// Toolbar widget: MSQ progress and time remaining, fed by Scenariometer over IPC.
///
/// It renders whatever the plugin reports and computes nothing of its own - the MSQ
/// index, the play clock and the estimator all live on the plugin side, which is the
/// only side that can measure anything.
/// </summary>
[ToolbarWidget(
    // Never change this id: Umbra keys saved widget settings on it.
    "ScenariometerMsq",
    "MSQ Progress",
    "Shows Main Scenario progress and the estimated time left. Requires the Scenariometer plugin."
)]
internal sealed class MsqProgressWidget(
    WidgetInfo info,
    string? guid = null,
    Dictionary<string, object>? configValues = null
) : StandardToolbarWidget(info, guid, configValues)
{
    private const string ModeProgress = "Progress";
    private const string ModePercent = "Percent";
    private const string ModeTimeLeft = "TimeLeft";
    private const string ModeExpansion = "Expansion";
    private const string ModeQuest = "Quest";
    private const string ModeTodayGoal = "TodayGoal";
    private const string ModeDaysLeft = "DaysLeft";
    private const string ModeAheadBehind = "AheadBehind";
    private const string ModeNone = "None";

    private static readonly Dictionary<string, string> LabelOptions = new()
    {
        { ModeProgress, "Quests done / total" },
        { ModePercent, "Percent complete" },
        { ModeTimeLeft, "Time left in the MSQ" },
        { ModeExpansion, "Time left in this expansion" },
        { ModeQuest, "Current quest name" },
        { ModeTodayGoal, "Today's goal (done / target)" },
        { ModeDaysLeft, "Days until the target date" },
        { ModeAheadBehind, "Ahead of / behind the plan" },
        { ModeNone, "Nothing" },
    };

    protected override StandardWidgetFeatures Features =>
        StandardWidgetFeatures.Text
        | StandardWidgetFeatures.SubText
        | StandardWidgetFeatures.Icon
        | StandardWidgetFeatures.CustomizableIcon
        | StandardWidgetFeatures.ProgressBar;

    /// <summary>
    /// Left click opens this, which is Umbra's own convention for a widget with more
    /// to say than fits on a toolbar. Pausing moved into the popup's footer: it used
    /// to be the left click itself, which meant the widget had no way to show you
    /// anything and made a misclick silently stop the measuring.
    /// </summary>
    public override ScenariometerPopup Popup { get; } = new();

    private ScenariometerApi Api { get; } = Framework.Service<ScenariometerApi>();

    // Held so OnUnload can detach it. Subscribing without unsubscribing means a second
    // OnLoad against the same node attaches a second handler, and the window then
    // opens twice per click.
    private Action<Node>? onRightClick;

    /// <summary>
    /// The Main Scenario icon the game itself uses. Set as the default rather than
    /// applied in OnLoad: the widget offers CustomizableIcon, and pushing an icon on
    /// every load would overwrite whatever the user picked.
    /// </summary>
    protected override uint DefaultGameIconId => ScenariometerPopup.MsqIconId;

    protected override void OnLoad()
    {
        // The bar is driven in percent so the constraint never has to move when the
        // MSQ grows by a patch.
        SetProgressBarConstraint(0, 100);

        onRightClick = _ => OpenPluginWindow();
        Node.OnRightClick += onRightClick;
    }

    /// <summary>
    /// Right click opens the plugin's own window, by running its command.
    ///
    /// ProcessCommand returns false when nothing has registered "/msq" - which is
    /// exactly the case where the plugin is missing or has not finished loading. Left
    /// unchecked, right-clicking then did nothing whatsoever and said nothing about
    /// why, which is the worst possible answer for the one question a new user of this
    /// widget is most likely to have: that it is only half of the thing.
    /// </summary>
    private static void OpenPluginWindow()
    {
        if (Framework.Service<ICommandManager>().ProcessCommand("/msq"))
            return;

        Framework.Service<IChatGui>().PrintError(
            "[Scenariometer] The Scenariometer plugin is not installed or not loaded, "
            + "so there is no window to open. This widget only displays what the "
            + "plugin measures - both halves have to be installed.");
    }

    protected override void OnUnload()
    {
        if (onRightClick is not null)
            Node.OnRightClick -= onRightClick;

        onRightClick = null;
    }

    /// <summary>
    /// What the labels were last built from. OnDraw runs every frame, while the
    /// figures it formats move once a second at most, and building a string costs an
    /// allocation whether or not it turns out to be the one already on screen. The
    /// version counter covers the data - a pause included, which the API writes into
    /// its cached snapshot in place - and the rest covers the settings that decide
    /// what is shown.
    /// </summary>
    private (int Version, string? Primary, string? Secondary, bool SubText) labels = Stale;

    /// <summary>A state no real one can equal, so the next draw always rebuilds.</summary>
    private static (int, string?, string?, bool) Stale => (-1, null, null, false);

    protected override void OnDraw()
    {
        var snapshot = Api.Snapshot;

        // Not running, or logged out - in which case the plugin keeps serving the last
        // figures it saw, which are not wrong so much as no longer about anything
        // happening. Both say the same thing, and neither allocates: the literal is
        // interned and Una.Drawing drops an assignment of equal text.
        if (snapshot is null || !snapshot.LoggedIn)
        {
            SetText("MSQ");
            SetSubText(null);
            SetProgressBarValue(0);
            SetDisabled(true);

            labels = Stale;
            return;
        }

        SetDisabled(false);

        var subTextShown = CvarShowSubText();
        var state = (Api.Version, GetConfigValue<string>("PrimaryLabel"), GetConfigValue<string>("SecondaryLabel"), subTextShown);

        if (state != labels)
        {
            labels = state;

            // A paused tracker that looks exactly like a running one is how you get
            // back from a raid night and find nothing was measured, so saying so
            // outranks whatever was configured. The sub-label carries it when there is
            // one, and the main label when there is not - with sub-text switched off,
            // "Paused" would otherwise go somewhere nothing is drawn. Icon-only
            // display can still hide it; nothing but the popup can help there.
            SetText(snapshot.Paused && !subTextShown
                ? "Paused"
                : Label(snapshot, state.Item2));

            // Nothing draws the sub-label with sub-text switched off, so do not spend
            // a format working out what it would have said.
            SetSubText(!subTextShown ? null
                : snapshot.Paused ? "Paused"
                : Label(snapshot, state.Item3));
        }

        Popup.ShowMsq = GetConfigValue<bool>("PopupShowMsq");
        Popup.ShowPace = GetConfigValue<bool>("PopupShowPace");
        Popup.ShowTarget = GetConfigValue<bool>("PopupShowTarget");
        Popup.ShowExpansions = GetConfigValue<bool>("PopupShowExpansions");

        var fraction = GetConfigValue<string>("ProgressBarScope") switch
        {
            "Expansion" => snapshot.ExpansionFraction,
            "Today" => snapshot.TodayFraction,
            _ => snapshot.Fraction,
        };

        SetProgressBarValue((int)(fraction * 100));
    }

    /// <summary>
    /// A figure that exists but is not known yet - the estimator has too few samples.
    /// Deliberately not the same as <see cref="NoTarget"/>: one of these resolves
    /// itself after a few more quests and the other never will until you set a date.
    /// </summary>
    private const string Unknown = "--";

    private const string NoTarget = "No target";

    private static string? Label(ScenariometerSnapshot snapshot, string mode) => mode switch
    {
        ModeProgress => $"{snapshot.Completed} / {snapshot.Total}",

        // Invariant, not the OS culture: the client is running in English, and a
        // "49,9%" next to the popup's "49.9%" reads as two different numbers.
        ModePercent => $"{(snapshot.Fraction * 100).ToString("0.0", CultureInfo.InvariantCulture)}%",

        ModeTimeLeft => snapshot.HasEstimate ? ScenariometerFormat.Duration(snapshot.RemainingSeconds) : Unknown,
        ModeExpansion => snapshot.HasEstimate ? ScenariometerFormat.Duration(snapshot.ExpansionRemainingSeconds) : Unknown,
        ModeQuest => snapshot.CurrentQuest.Length > 0 ? snapshot.CurrentQuest : "Complete",

        // Never a bare 0 with no target set: a zero here would look like a goal that
        // had been met rather than one that was never asked for.
        ModeTodayGoal => snapshot.HasTarget ? $"{snapshot.CompletedToday} / {snapshot.QuotaToday}" : NoTarget,
        ModeDaysLeft => snapshot.HasTarget ? DaysLeft(snapshot) : NoTarget,
        ModeAheadBehind => snapshot.HasTarget ? AheadBehind(snapshot) : NoTarget,

        // "Quests left today" used to be its own mode and said very nearly what
        // today's goal says. Anyone who had it selected keeps a working widget.
        "TodayLeft" => Label(snapshot, ModeTodayGoal),

        _ => null,
    };

    /// <summary>
    /// "116 days left" rather than "116", which on a toolbar beside an unrelated
    /// number is not obviously a count of days.
    /// </summary>
    private static string DaysLeft(ScenariometerSnapshot snapshot)
    {
        if (snapshot.Overdue)
            return "Overdue";

        var days = snapshot.DaysRemaining;
        return $"{days} {ScenariometerFormat.Days(days)} left";
    }

    /// <summary>
    /// "3 Ahead" / "2 Behind" / "On track" - the toolbar's own short form, measured
    /// against the whole plan rather than today. The popup spells it out in full; a
    /// label on a toolbar has no room to.
    /// </summary>
    /// <summary>
    /// "Overdue" outranks the standing once the date has passed, the same way it does
    /// for the days-left label. Both the plugin window and this widget's own popup
    /// hide the Plan row there, and the figure behind it is no longer a day or two of
    /// drift but the whole unfinished MSQ - a toolbar reading "465 Behind" states a
    /// number nobody can act on.
    /// </summary>
    private static string AheadBehind(ScenariometerSnapshot snapshot) =>
        snapshot.Overdue ? "Overdue" : AheadBehind(snapshot.AheadBy);

    private static string AheadBehind(int delta) => delta switch
    {
        > 0 => $"{delta} Ahead",
        < 0 => $"{-delta} Behind",
        _ => "On track",
    };

    protected override IEnumerable<IWidgetConfigVariable> GetConfigVariables()
    {
        return [
            ..base.GetConfigVariables(),

            new SelectWidgetConfigVariable(
                "PrimaryLabel",
                "Main label",
                "What the widget shows on its first line.",
                ModeQuest,
                LabelOptions),

            new SelectWidgetConfigVariable(
                "SecondaryLabel",
                "Sub label",
                "What the widget shows on its second line. Requires the sub-text option above.",
                ModeTimeLeft,
                LabelOptions),

            new SelectWidgetConfigVariable(
                "ProgressBarScope",
                "Progress bar shows",
                "What the widget's own bar tracks. The popup draws all three regardless.",
                "Overall",
                new Dictionary<string, string>
                {
                    { "Overall", "The whole Main Scenario" },
                    { "Expansion", "The current expansion" },
                    { "Today", "Today's goal, if a target date is set" },
                }),

            // The popup mirrors the plugin window, which is more than most people want
            // hanging off a toolbar button - so every section of it can be switched
            // off. A section with nothing to report hides itself regardless.
            new BooleanWidgetConfigVariable(
                "PopupShowMsq",
                "Popup: current quest and overall progress",
                "The header: the quest you are on, its chapter and the Main Scenario bar.",
                true),

            new BooleanWidgetConfigVariable(
                "PopupShowPace",
                "Popup: pace and time remaining",
                "Your measured pace, and what is left of this expansion and of the Main Scenario.",
                true),

            new BooleanWidgetConfigVariable(
                "PopupShowTarget",
                "Popup: target date and today's goal",
                "Hidden anyway when no target date is set in the plugin.",
                true),

            new BooleanWidgetConfigVariable(
                "PopupShowExpansions",
                "Popup: expansion breakdown",
                "A bar per expansion, the way the plugin window lists them.",
                true),
        ];
    }
}
