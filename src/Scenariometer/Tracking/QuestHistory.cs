using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Scenariometer.Tracking;

/// <summary>
/// The measured samples for one character, persisted as its own JSON file.
///
/// Per character, because MSQ progress is: a level 90 alt and a first-time sprout
/// have nothing to say about each other's pace. Kept out of the Dalamud plugin
/// config because it grows to thousands of rows and the config is rewritten
/// whenever a checkbox changes.
/// </summary>
internal sealed class QuestHistory
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly List<QuestSample> samples = [];
    private readonly string path;

    /// <summary>
    /// Guards <see cref="samples"/>. Writes come from the framework tick; reads can
    /// come from anywhere, because the IPC gates run on whichever thread the caller
    /// used - Dalamud documents the handler as running "on the same thread as the
    /// caller". Enumerating this list while the tick appends to it is the textbook
    /// "collection was modified" crash, and it would surface inside the consumer.
    /// </summary>
    private readonly Lock gate = new();

    public ulong ContentId { get; }

    /// <summary>
    /// A snapshot, not a view. Copying is what makes it safe to read off the framework
    /// thread; the samples themselves are immutable records, so the copy is shallow
    /// and cheap.
    /// </summary>
    public IReadOnlyList<QuestSample> Samples
    {
        get
        {
            lock (gate)
                return samples.ToArray();
        }
    }

    /// <summary>Samples that count toward the pace, oldest first. Also a snapshot.</summary>
    public IReadOnlyList<QuestSample> Usable
    {
        get
        {
            lock (gate)
                return samples.Where(s => !s.Outlier).ToArray();
        }
    }

    private QuestHistory(ulong contentId, string path)
    {
        ContentId = contentId;
        this.path = path;
    }

    public static QuestHistory Load(ulong contentId)
    {
        var dir = Services.PluginInterface.ConfigDirectory;
        dir.Create();

        var history = new QuestHistory(contentId, Path.Combine(dir.FullName, $"history-{contentId:X16}.json"));

        try
        {
            if (File.Exists(history.path))
            {
                var loaded = JsonSerializer.Deserialize<List<QuestSample>>(File.ReadAllText(history.path));
                if (loaded is not null)
                    // No lock: nothing else can see this instance until Load returns.
                    history.samples.AddRange(loaded);
            }
        }
        catch (Exception ex)
        {
            // A corrupt history is not worth taking the plugin down for - start clean,
            // but keep the old file so it can be looked at.
            Services.Log.Error(ex, "Could not read {Path}; starting with an empty history.", history.path);
            TryQuarantine(history.path);
        }

        Services.Log.Information("Loaded {Count} samples for character {Id:X16}.", history.samples.Count, contentId);
        return history;
    }

    public void Add(QuestSample sample)
    {
        samples.Add(sample);
        Save();
    }

    /// <summary>
    /// Adds several samples for one write. Several MSQ quests can complete inside a
    /// single poll, and Save rewrites the whole file - doing that once per sample
    /// rewrites a list of thousands once per quest.
    /// </summary>
    public void AddRange(IReadOnlyList<QuestSample> incoming)
    {
        if (incoming.Count == 0)
            return;

        lock (gate)
            samples.AddRange(incoming);

        Save();
    }

    public void Clear()
    {
        lock (gate)
            samples.Clear();

        Save();
    }

    public void Save()
    {
        try
        {
            // Write-then-move: a crash mid-write must not eat the whole history.
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(samples, JsonOptions));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Services.Log.Error(ex, "Could not write {Path}.", path);
        }
    }

    private static void TryQuarantine(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Move(path, $"{path}.broken-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}", overwrite: true);
        }
        catch (Exception ex)
        {
            Services.Log.Warning(ex, "Could not set aside the unreadable history file.");
        }
    }
}
