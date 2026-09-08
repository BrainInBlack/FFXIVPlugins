using System;
using Dalamud.Plugin.Services;

namespace Scenariometer.Tracking;

/// <summary>
/// A monotonic "time you were actually playing" counter.
///
/// Wall-clock time between two quest turn-ins is useless as a pace input: it counts
/// the eight hours the client sat at the login screen. This ticks only while logged
/// in (and, optionally, only while not flagged AFK), so a sample means "seconds of
/// play", not "seconds of calendar".
///
/// It never resets on its own - consumers remember a start value and subtract.
/// </summary>
internal sealed class ActiveTimeClock : IDisposable
{
    /// <summary>
    /// The OnlineStatus row for "Away from Keyboard". The row id is confirmed against
    /// the sheet.
    ///
    /// TODO(verify): what is NOT confirmed is the runtime half - whether the game sets
    /// your OWN character's OnlineStatus to this when you go AFK, as opposed to only
    /// showing it to other players. A sheet cannot answer that; it needs someone to
    /// idle until the AFK flag appears and check that the clock stops.
    /// </summary>
    private const uint AfkOnlineStatus = 17;

    private double activeSeconds;

    public double ActiveSeconds => activeSeconds;

    /// <summary>False while the clock is paused (logged out, or AFK when configured).</summary>
    public bool IsRunning { get; private set; }

    /// <summary>
    /// Starts the clock. Separate from the constructor because Dalamud runs a plugin
    /// constructor on its own long-running thread while the framework thread keeps
    /// ticking - so a tick subscribed here would run concurrently with the rest of
    /// Plugin's construction, against fields that are not all assigned yet.
    /// </summary>
    public void Start() => Services.Framework.Update += OnUpdate;

    public void Dispose() => Services.Framework.Update -= OnUpdate;

    private void OnUpdate(IFramework framework)
    {
        IsRunning = ShouldRun();
        if (IsRunning)
            activeSeconds += framework.UpdateDelta.TotalSeconds;
    }

    private static bool ShouldRun()
    {
        if (Plugin.Config.Paused)
            return false;

        if (!Services.ClientState.IsLoggedIn)
            return false;

        if (Plugin.Config.PauseWhileAfk && Services.Objects.LocalPlayer?.OnlineStatus.RowId == AfkOnlineStatus)
            return false;

        return true;
    }
}
