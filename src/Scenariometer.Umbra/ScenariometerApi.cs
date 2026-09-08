using System;
using System.Text.Json;
using Dalamud.Plugin.Ipc;
using Scenariometer.Contract;
using Umbra.Common;

namespace ScenariometerUmbra;

/// <summary>
/// Reads Scenariometer's state over Dalamud IPC.
///
/// Registered as an Umbra service, so widgets get the same instance and the plugin
/// is polled once per interval no matter how many widgets are on the toolbar.
/// </summary>
[Service]
internal sealed class ScenariometerApi
{
    /// <summary>
    /// A widget's OnDraw runs every frame; the snapshot costs a serialize on the
    /// other side plus a parse on this one, and describes something that changes
    /// over minutes. Once a second is far more resolution than the display needs.
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly ICallGateSubscriber<int> apiVersion;
    private readonly ICallGateSubscriber<string> snapshot;
    private readonly ICallGateSubscriber<bool, bool> setPaused;

    private ScenariometerSnapshot? cached;
    private DateTime nextPoll = DateTime.MinValue;

    public ScenariometerApi()
    {
        var plugin = Framework.DalamudPlugin;

        apiVersion = plugin.GetIpcSubscriber<int>(ScenariometerIpc.ApiVersionGate);
        snapshot = plugin.GetIpcSubscriber<string>(ScenariometerIpc.SnapshotGate);
        setPaused = plugin.GetIpcSubscriber<bool, bool>(ScenariometerIpc.SetPausedGate);
    }

    /// <summary>
    /// Why there is nothing to show, for a consumer to display verbatim. Empty when
    /// there is nothing wrong - never null, so callers do not have to decide what a
    /// null means on top of deciding what the text says.
    /// </summary>
    public string Unavailable { get; private set; } = "Scenariometer is not running.";

    /// <summary>
    /// Bumped whenever what a consumer can see changes: a fresh poll, or a pause
    /// written straight into the cached snapshot. Consumers redraw on a change of
    /// this rather than every frame.
    ///
    /// A counter rather than comparing snapshot references, because <see cref="SetPaused"/>
    /// mutates the cached object in place - reference equality would miss exactly the
    /// change a click needs to show immediately.
    /// </summary>
    public int Version { get; private set; }

    /// <summary>The latest snapshot, or null when the plugin is unavailable.</summary>
    public ScenariometerSnapshot? Snapshot
    {
        get
        {
            var now = DateTime.UtcNow;
            if (now < nextPoll)
                return cached;

            nextPoll = now + PollInterval;
            Poll();
            return cached;
        }
    }

    /// <summary>
    /// Drops the cached snapshot so the next read hits the plugin. Needed before
    /// acting on state rather than merely displaying it: the cache is up to a poll
    /// interval stale, and toggling from a stale value sends back the state the
    /// plugin is already in.
    /// </summary>
    public void Invalidate() => nextPoll = DateTime.MinValue;

    /// <summary>
    /// Asks the plugin to pause or resume. The reply is written straight into the
    /// cached snapshot rather than waiting for the next poll, so a click reads as
    /// instant instead of taking up to a second to show.
    /// </summary>
    public void SetPaused(bool paused)
    {
        try
        {
            var now = setPaused.InvokeFunc(paused);
            if (cached is not null && cached.Paused != now)
            {
                cached.Paused = now;
                Version++;
            }
        }
        catch (Exception)
        {
            // Same set of failure modes as Poll - the plugin went away mid-click.
            Fail("Scenariometer is not running.");
        }
    }

    private void Poll()
    {
        try
        {
            // The two DLLs are installed and updated independently, so a version the
            // contract does not cover is a normal state, not a bug - say so plainly
            // rather than misreporting numbers from a payload we cannot read.
            var version = apiVersion.InvokeFunc();
            if (version != ScenariometerIpc.ContractVersion)
            {
                Fail($"Scenariometer speaks API v{version}, this widget speaks v{ScenariometerIpc.ContractVersion}. Update both.");
                return;
            }

            var json = snapshot.InvokeFunc();
            if (string.IsNullOrEmpty(json))
            {
                Fail("Scenariometer returned no data.");
                return;
            }

            cached = JsonSerializer.Deserialize<ScenariometerSnapshot>(json);
            Unavailable = cached is null ? "Scenariometer returned unreadable data." : string.Empty;
            Version++;
        }
        catch (Exception)
        {
            // Every failure mode here - plugin not installed, not loaded yet, mid
            // reload, gate removed - arrives as an exception from the call gate.
            // None of them are worth a log line every second.
            Fail("Scenariometer is not running.");
        }
    }

    private void Fail(string reason)
    {
        // Only when something actually changed. Every poll fails while the plugin is
        // absent, and bumping the version each time told consumers to redraw once a
        // second - re-rendering the same message, in precisely the state where nothing
        // is happening. Going from working to broken is a change; staying broken is not.
        var changed = cached is not null || Unavailable != reason;

        cached = null;
        Unavailable = reason;

        if (changed)
            Version++;
    }
}
