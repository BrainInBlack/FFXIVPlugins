using System;
using System.Collections.Generic;
using System.Text.Json;
using Dalamud.Plugin.Ipc;
using Scenariometer.Contract;
using Scenariometer.Estimation;
using Scenariometer.Tracking;

namespace Scenariometer.Ipc;

/// <summary>
/// Publishes the current progress and estimate over Dalamud IPC, so other plugins -
/// the Umbra widget in this repo, or anything else - can render it without
/// duplicating the MSQ index, the clock and the estimator.
///
/// Pull, not push: consumers call when they need a value (a toolbar widget redraws
/// every frame and only cares about the latest state), which avoids holding
/// subscriber delegates from another load context alive across a reload.
/// </summary>
internal sealed class IpcProvider : IDisposable
{
    private readonly ProgressTracker tracker;
    private readonly ICallGateProvider<int> apiVersion;
    private readonly ICallGateProvider<string> snapshot;
    private readonly ICallGateProvider<bool, bool> setPaused;

    public IpcProvider(ProgressTracker tracker)
    {
        this.tracker = tracker;

        apiVersion = Services.PluginInterface.GetIpcProvider<int>(ScenariometerIpc.ApiVersionGate);
        snapshot = Services.PluginInterface.GetIpcProvider<string>(ScenariometerIpc.SnapshotGate);

        setPaused = Services.PluginInterface.GetIpcProvider<bool, bool>(ScenariometerIpc.SetPausedGate);

        apiVersion.RegisterFunc(() => ScenariometerIpc.ContractVersion);
        snapshot.RegisterFunc(BuildSnapshotJson);
        setPaused.RegisterFunc(SetPaused);
    }

    public void Dispose()
    {
        apiVersion.UnregisterFunc();
        snapshot.UnregisterFunc();
        setPaused.UnregisterFunc();
    }

    /// <summary>
    /// Writes the pause flag on behalf of a consumer. Saving on every call would
    /// rewrite the config file for a no-op click, so it only saves on a real change.
    /// </summary>
    private static bool SetPaused(bool paused)
    {
        if (Plugin.Config.Paused != paused)
        {
            Plugin.Config.Paused = paused;
            Plugin.Config.Save();
        }

        return Plugin.Config.Paused;
    }

    private string BuildSnapshotJson()
    {
        try
        {
            return JsonSerializer.Serialize(BuildSnapshot());
        }
        catch (Exception ex)
        {
            // An exception here would surface inside the *caller's* plugin, which is
            // a confusing place to debug it. Log on our side and hand back nothing.
            Services.Log.Error(ex, "Failed to build the IPC snapshot.");
            return string.Empty;
        }
    }

    private ScenariometerSnapshot BuildSnapshot()
    {
        var progress = tracker.Progress;
        var estimate = Estimator.Compute(tracker.History);
        var expansion = progress.CurrentExpansion;

        var goal = DailyGoal.Compute(
            Plugin.Config.TargetDate,
            tracker.PlanStartDate,
            Plugin.Config.DayStartHour,
            progress.Remaining,
            tracker.History,
            DateTimeOffset.Now);

        var (low, mid, high) = estimate.For(progress.Remaining);
        var (_, expansionMid, _) = estimate.For(expansion?.Remaining ?? 0);
        var hasEstimate = estimate.SampleCount >= Plugin.Config.MinimumSamples;

        // Copied into the contract's own types rather than sent as-is: the internal
        // records carry a Lumina-shaped identity the far side has no business seeing,
        // and only what is listed here is part of the promise.
        var expansions = new List<ScenariometerExpansion>(progress.Expansions.Count);
        foreach (var item in progress.Expansions)
        {
            expansions.Add(new ScenariometerExpansion
            {
                Name = item.Name,
                IconId = item.IconId,
                Completed = item.Completed,
                Total = item.Total,
                IsCurrent = expansion is not null && item.ExpansionId == expansion.ExpansionId,
                Marks = [.. item.Marks],
            });
        }

        return new ScenariometerSnapshot
        {
            ContractVersion = ScenariometerIpc.ContractVersion,
            LoggedIn = Services.ClientState.IsLoggedIn,
            Paused = Plugin.Config.Paused,

            Completed = progress.Completed,
            Total = progress.Total,
            Remaining = progress.Remaining,

            CurrentQuest = progress.Current?.Name ?? string.Empty,
            CurrentChapter = progress.Current?.GenreName ?? string.Empty,

            ExpansionName = expansion?.Name ?? string.Empty,
            ExpansionCompleted = expansion?.Completed ?? 0,
            ExpansionTotal = expansion?.Total ?? 0,
            ExpansionRemaining = expansion?.Remaining ?? 0,

            Expansions = expansions,
            Marks = [.. progress.Marks],

            HasEstimate = hasEstimate,
            SampleCount = estimate.SampleCount,
            PaceSeconds = estimate.Median,
            MinimumSamples = Plugin.Config.MinimumSamples,

            RemainingSeconds = hasEstimate ? mid.TotalSeconds : 0,
            RemainingSecondsLow = hasEstimate ? low.TotalSeconds : 0,
            RemainingSecondsHigh = hasEstimate ? high.TotalSeconds : 0,
            ExpansionRemainingSeconds = hasEstimate ? expansionMid.TotalSeconds : 0,

            CurrentQuestSeconds = tracker.CurrentQuestSeconds ?? 0,

            HasTarget = goal.HasTarget,
            TargetDate = goal.HasTarget ? DailyGoal.Format(goal.Target) : string.Empty,
            DaysRemaining = goal.DaysRemaining,
            CompletedToday = goal.CompletedToday,
            QuotaToday = goal.QuotaToday,
            RemainingToday = goal.RemainingToday,
            AheadBy = goal.AheadBy,
            Overdue = goal.Overdue,
            DoneForToday = goal.DoneForToday,
        };
    }
}
