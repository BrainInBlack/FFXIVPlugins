using System;
using System.IO;
using System.Text.Json;

namespace Scenariometer.Tracking;

/// <summary>
/// The one measurement that is in flight when the plugin unloads: how much play time
/// has accumulated on the quest the character is currently on.
///
/// Nothing else needs saving. Completed quests are read back out of the game, and
/// finished samples are already in the per-character history. Only the running
/// interval lives purely in memory, and losing it means the next turn-in is either
/// unmeasured or - before the baseline fix - measured from the reload instead.
///
/// It stores elapsed play time, never a wall-clock timestamp: time while the plugin
/// is not loaded was not measured and must not be counted. A long disable therefore
/// under-counts rather than over-counts, which is the safe direction for a median.
/// </summary>
internal sealed record SessionState(ulong ContentId, uint CurrentQuestRowId, double ElapsedSeconds)
{
    private static string FilePath =>
        Path.Combine(Services.PluginInterface.ConfigDirectory.FullName, "session.json");

    public static SessionState? Load()
    {
        try
        {
            var path = FilePath;
            return File.Exists(path)
                ? JsonSerializer.Deserialize<SessionState>(File.ReadAllText(path))
                : null;
        }
        catch (Exception ex)
        {
            // Worth a line, but never worth failing the load over - the cost of
            // getting this wrong is one unmeasured quest.
            Services.Log.Warning(ex, "Could not read the saved session; carrying on without it.");
            return null;
        }
    }

    public void Save()
    {
        try
        {
            var dir = Services.PluginInterface.ConfigDirectory;
            dir.Create();

            // Write-then-move, same as the history: a crash mid-write must not leave
            // a half-parsed file behind for the next load to trip over.
            var path = FilePath;
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Services.Log.Error(ex, "Could not write the session file.");
        }
    }

    /// <summary>
    /// Deletes the file. Called immediately after a restore attempt, successful or
    /// not: the interval describes one specific moment, and applying it twice - after
    /// a character switch and back, say - would invent play time that never happened.
    /// </summary>
    public static void Clear()
    {
        try
        {
            var path = FilePath;
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            Services.Log.Warning(ex, "Could not remove the session file.");
        }
    }
}
