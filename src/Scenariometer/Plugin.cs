using System;
using System.Numerics;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using KamiToolKit;
using Scenariometer.Estimation;
using Scenariometer.Ipc;
using Scenariometer.Msq;
using Scenariometer.Tracking;
using Scenariometer.Windows;

namespace Scenariometer;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/msq";

    /// <summary>Both windows are real game windows now; no ImGui is left.</summary>
    private readonly NativeMainWindow nativeWindow;
    private readonly NativeConfigWindow nativeConfigWindow;
    private readonly NativeDatePickerWindow datePicker;

    private readonly MsqIndex index;
    private readonly ActiveTimeClock clock;
    private readonly ProgressTracker tracker;
    private readonly IpcProvider ipc;

    internal static Configuration Config { get; private set; } = null!;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        pluginInterface.Create<Services>();

        Config = (Services.PluginInterface.GetPluginConfig() as Configuration ?? new Configuration()).Migrate();


        // Built synchronously: it is one pass over the Quest sheet, and every other
        // component needs it. If this ever shows up in load times, move it to a Task
        // and gate the windows on it - nothing here is needed before the first tick.
        index = MsqIndex.Build();

        clock = new ActiveTimeClock();
        tracker = new ProgressTracker(index, clock);
        tracker.SampleRecorded += OnSampleRecorded;

        // Published before the UI exists: a consumer that is already loaded (Umbra
        // reloading its plugins) should find the gates as soon as we are alive.
        ipc = new IpcProvider(tracker);

        // Must come before any node is built; KamiToolKit warns about nothing if it
        // is skipped, it simply misbehaves.
        //
        // Blocked on deliberately, in a constructor, which normally invites a deadlock.
        // InitializeAsync really does suspend - it awaits a config file load and then
        // IFramework.Run(NativeAddon.InitializeCloseCallback), which completes on the
        // framework thread.
        //
        // It is safe because this constructor does NOT run on the framework thread.
        // Dalamud invokes plugin constructors through
        // ServiceContainer.CreateAsync -> Task.Factory.StartNew(..., LongRunning), on
        // a dedicated thread, and its own comment there sanctions the pattern:
        // "Legacy (IDalamudPlugin) plugin ctors can block to wait for Tasks, as the
        // ctor is their only init." The framework thread stays free to tick, so the
        // awaited continuation can run and this call returns.
        //
        // THE INVARIANT THAT KEEPS IT SAFE: this plugin must not declare LoadSync.
        // With LoadSync set and LoadRequiredState 0 or 1, Dalamud runs the constructor
        // ON the framework thread instead - and then waiting here for a framework tick
        // deadlocks the game on load. Do not add it.
        //
        // The v15-sanctioned alternative is IAsyncDalamudPlugin.LoadAsync, which exists
        // precisely to avoid blocking like this. Worth migrating to; not required, and
        // deliberately not done in the same change as the library bump.
        KamiToolKitLibrary.InitializeAsync(Services.PluginInterface, "Scenariometer")
            .GetAwaiter()
            .GetResult();

        // Title is the window, Subtitle is the plugin - the game's own convention, and
        // KamiToolKit defaults the subtitle to the name passed to Initialize. Setting
        // both to "Scenariometer" printed it twice in the title bar.
        nativeWindow = new NativeMainWindow(tracker, ToggleConfigWindow)
        {
            InternalName = "Scenariometer",
            Title = "Main Scenario",
            Size = new Vector2(400f, 560f),
        };

        datePicker = new NativeDatePickerWindow(OnDatePicked)
        {
            InternalName = "ScenariometerDate",
            Title = "Target date",
            Size = new Vector2(290f, 318f),
        };

        nativeConfigWindow = new NativeConfigWindow(ClearHistory, OpenDatePicker, SetTarget)
        {
            // Distinct from the main window's, and inside the 31 character limit.
            InternalName = "ScenariometerCfg",
            // "Settings" alone: the subtitle beside it is already the plugin name, and
            // "Scenariometer Settings   Scenariometer" said it twice.
            Title = "Settings",
            // Exact, not approximate: resizing an open window does not redraw its
            // frame, so a mismatch either clips the last control or leaves dead space
            // until the next open. NativeConfigWindow.OnSetup warns with the right
            // number whenever the content stops matching this.
            Size = new Vector2(480f, 590f),
        };

        Services.Commands.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the MSQ progress window. Also: /msq config, /msq reset, "
                + "/msq debug sections, /msq debug expansions, /msq debug missing.",
        });

        Services.PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigWindow;
        Services.PluginInterface.UiBuilder.OpenMainUi += ToggleMainWindow;

        Services.ClientState.Login += OnLogin;

        // Last, on purpose. Both of these subscribe to the framework tick, which runs
        // on a different thread from this constructor - see the note on the blocking
        // call above. Starting them here means no tick can observe a half-built
        // plugin, and no turn-in can land before SampleRecorded is attached.
        clock.Start();
        tracker.Start();
    }

    public void Dispose()
    {
        Services.ClientState.Login -= OnLogin;
        Services.PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigWindow;
        Services.PluginInterface.UiBuilder.OpenMainUi -= ToggleMainWindow;
        Services.Commands.RemoveHandler(CommandName);

        // Before KamiToolKitLibrary.Dispose: the addons own native nodes that the
        // library tears the rest of down for.
        nativeWindow.Dispose();
        nativeConfigWindow.Dispose();
        datePicker.Dispose();
        KamiToolKitLibrary.Dispose();

        tracker.SampleRecorded -= OnSampleRecorded;
        ipc.Dispose();
        tracker.Dispose();
        clock.Dispose();
    }

    private void ToggleMainWindow() => nativeWindow.Toggle();

    private void ToggleConfigWindow() => nativeConfigWindow.Toggle();

    /// <summary>
    /// Sets the target and restarts every character's plan. A new target is a new
    /// plan, so surplus banked against the old one must not follow it across - and the
    /// current character's starts today, while the rest re-establish theirs when they
    /// are next loaded.
    /// </summary>
    private void SetTarget(DateOnly? date)
    {
        Config.ClearTargetStarts();

        if (date is null)
        {
            Config.TargetDate = string.Empty;
            return;
        }

        Config.TargetDate = date.Value.ToString(DailyGoal.IsoFormat);
        Config.SetTargetStart(
            tracker.CurrentContentId,
            DailyGoal.LogicalDate(DateTimeOffset.Now, Config.DayStartHour).ToString(DailyGoal.IsoFormat));
    }

    /// <summary>Opens the calendar on whatever date is already set.</summary>
    private void OpenDatePicker() =>
        datePicker.OpenAt(DailyGoal.TryParse(Config.TargetDate, out var current) ? current : null);

    /// <summary>
    /// The calendar lives in its own window, so the settings window has to be told to
    /// re-read the value it is displaying.
    /// </summary>
    private void OnDatePicked(DateOnly? date)
    {
        SetTarget(date);
        Config.Save();
        nativeConfigWindow.RefreshTarget();
    }

    private void OnLogin()
    {
        if (Config.OpenOnLogin)
            nativeWindow.Open();
    }

    private void OnCommand(string command, string arguments)
    {
        switch (arguments.Trim().ToLowerInvariant())
        {
            case "":
                ToggleMainWindow();
                break;

            case "config":
            case "settings":
                ToggleConfigWindow();
                break;

            case "reset":
                Services.Chat.Print(ClearHistory()
                    ? "[Scenariometer] Measured quest history cleared for this character."
                    : "[Scenariometer] No history to clear - log in to a character first.");
                break;

            case "debug sections":
                Services.Log.Information("JournalSections:{NewLine}{Sections}", Environment.NewLine, MsqIndex.DumpSections());
                Services.Chat.Print("[Scenariometer] Journal sections written to the Dalamud log.");
                break;

            case "debug expansions":
                Services.Log.Information("MSQ breakdown:{NewLine}{Breakdown}", Environment.NewLine, index.DumpBreakdown());
                Services.Chat.Print("[Scenariometer] MSQ breakdown written to the Dalamud log.");
                break;

            case "debug missing":
                Services.Log.Information("MSQ holes:{NewLine}{Holes}", Environment.NewLine, tracker.DumpHoles());
                Services.Chat.Print("[Scenariometer] Incomplete-quest report written to the Dalamud log.");
                break;

            default:
                Services.Chat.Print($"[Scenariometer] Unknown argument '{arguments}'. Try: config, reset, debug sections, debug expansions, debug missing.");
                break;
        }
    }

    /// <summary>
    /// Wipes this character's measured history. False when there is none to wipe -
    /// at the title screen, or in the seconds after login before the first poll has
    /// loaded it. Callers report the outcome, so this must not claim a success that
    /// did not happen: the button and the command both offer it as irreversible.
    /// </summary>
    private bool ClearHistory()
    {
        if (tracker.History is null)
            return false;

        tracker.History.Clear();
        return true;
    }

    private void OnSampleRecorded(QuestSample sample)
    {
        Services.Log.Debug(
            "Recorded {Quest} in {Seconds:0}s (outlier: {Outlier}).",
            sample.QuestName,
            sample.ActiveSeconds,
            sample.Outlier);

        if (!Config.ChatOnQuestComplete || sample.Outlier)
            return;

        var progress = tracker.Progress;
        var estimate = Estimator.Compute(tracker.History);

        if (estimate.SampleCount < Config.MinimumSamples)
            return;

        var (_, mid, _) = estimate.For(progress.Remaining);
        Services.Chat.Print(
            $"[Scenariometer] {progress.Completed}/{progress.Total} MSQ - about {Estimator.Format(mid)} left.");
    }
}
