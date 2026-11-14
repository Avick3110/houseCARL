namespace HousecarlCore;

// The plugins whose header names a plugin as a master, read off every plugin header on disk per call; nothing is held.

/// <summary>A plugin file on disk outside the loaded order whose header names the master: its filename, the layer label it sits in, and why the game does not load it when the label does not say.</summary>
public sealed record UnloadedDependent(string Plugin, string Where, string? Why);

/// <summary>The plugins naming one master: <see cref="InOrder"/> in load order, then copies on disk the game does not load, then the folders and files that could not be read, so a missing name is never taken for none.</summary>
public sealed record MasterDependentsResult(
    IReadOnlyList<string> InOrder, IReadOnlyList<UnloadedDependent> OnDisk, IReadOnlyList<string> UnopenedFolders, IReadOnlyList<string> UnreadHeaders);

public static class MasterDependents
{
    /// <summary>Every plugin naming <paramref name="master"/> in its header: a loaded plugin (in loadorder.txt and not unchecked) is judged by the copy the game loads, every other copy on disk by itself.</summary>
    public static MasterDependentsResult Find(Mo2Composition comp, string modsDir, string dataDir, string overwriteDir, string master)
    {
        var inactive = new HashSet<string>(comp.InactivePluginNames, StringComparer.OrdinalIgnoreCase);
        var position = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in comp.OrderedPluginNames)
            if (!inactive.Contains(n)) position.TryAdd(n, position.Count);   // what Build loads: the order less the unchecked

        var served = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inOrder = new List<string>();
        var onDisk = new List<UnloadedDependent>();
        var unopened = new List<string>();
        var unread = new List<string>();
        foreach (var hit in Mo2LoadOrder.AllPluginFiles(comp, modsDir, dataDir, overwriteDir, unopened))
        {
            var fn = Path.GetFileName(hit.Path);
            if (fn.Equals(master, StringComparison.OrdinalIgnoreCase)) continue;
            bool loaded = position.ContainsKey(fn);
            if (loaded && (!hit.Enabled || !served.Add(fn))) continue;    // a loaded name's other copies are not what the game reads
            if (PluginFile.ReadHeader(hit.Path) is not { } header) { unread.Add($"{fn} in {hit.Where}"); continue; }
            if (!header.Masters.Contains(master, StringComparer.OrdinalIgnoreCase)) continue;
            if (loaded) inOrder.Add(fn);
            else onDisk.Add(new UnloadedDependent(fn, hit.Where, !hit.Enabled ? null
                : inactive.Contains(fn) ? "unchecked in plugins.txt" : "not in loadorder.txt"));
        }
        inOrder.Sort((a, b) => position[a].CompareTo(position[b]));
        return new MasterDependentsResult(inOrder, onDisk, unopened, unread);
    }
}
