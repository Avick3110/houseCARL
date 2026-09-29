using HousecarlCore;
using Mutagen.Bethesda.Plugins;

namespace HousecarlMcp;

/// <summary>What a delta or tree batch reads, counted off the index before any body is: the versions each row reads,
/// how many of those belong to contained records, and the megabytes of plugin each chunk walks (#932).</summary>
internal static class ComparisonCount
{
    internal readonly record struct Counted(long Reads, long ContainedReads, int ContainedRows, double MegabytesWalked);

    /// <summary>Count <paramref name="keys"/> as the batch reads them. <paramref name="offOrderPath"/> is the file an
    /// off-order lane selected from, which only a delta's subject reads; <paramref name="containedHere"/> is that file's
    /// own containment.</summary>
    internal static Counted Count(IReadOnlyList<FormKey> keys, LoadOrderResolver.IndexView view, bool tree,
                                  RecordReads.PoleSpec subject, RecordReads.PoleSpec reference,
                                  IReadOnlySet<FormKey>? containedHere, string? offOrderPath)
    {
        var sizes = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        double Size(string? path) =>
            path is null ? 0
            : sizes.TryGetValue(path, out var known) ? known
            : sizes[path] = File.Exists(path) ? new FileInfo(path).Length / 1_000_000.0 : 0;
        double PluginSize(string plugin) => Size(view.PluginPath(plugin));

        long reads = 0, containedReads = 0;
        int containedRows = 0;
        double mb = 0;
        bool offOrderRead = false;
        var chunk = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < keys.Count; i++)
        {
            if (i % RecordReads.ComparisonChunkRows == 0)
            {
                foreach (var p in chunk) mb += PluginSize(p);
                chunk.Clear();
            }
            var fk = keys[i];
            int n = tree ? TreeVersions(fk, view, reference, chunk, ref offOrderRead)
                         : DeltaVersions(fk, view, subject, reference, offOrderPath is not null, chunk, ref offOrderRead);
            reads += n;
            if (n > 0 && (containedHere?.Contains(fk) == true || view.ParentOf(fk) is not null))
            {
                containedReads += n;
                containedRows++;
            }
        }
        foreach (var p in chunk) mb += PluginSize(p);
        // An off-order arm is swept once for the whole batch, not per chunk.
        if (offOrderRead) mb += Size(offOrderPath);
        return new Counted(reads, containedReads, containedRows, mb);
    }

    /// <summary>A tree row reads every in-order provider, plus the versus= pole when it is not the winner; a key the
    /// index does not hold is settled from the index and reads nothing.</summary>
    static int TreeVersions(FormKey fk, LoadOrderResolver.IndexView view, RecordReads.PoleSpec reference,
                            HashSet<string> chunk, ref bool offOrderRead)
    {
        if (view.TouchingPlugins(fk) is not { Count: > 0 } providers) return 0;
        foreach (var p in providers) chunk.Add(p);
        if (reference.Kind == RecordReads.PoleKind.Winner) return providers.Count;
        if (reference.Kind == RecordReads.PoleKind.Named)
        {
            if (IsInOrder(view, reference)) chunk.Add(reference.Plugin!);
            else offOrderRead = true;
        }
        return providers.Count + 1;
    }

    /// <summary>A delta row reads its subject, then its reference only when the subject read; each pole reads one
    /// version when it resolves and none when it does not.</summary>
    static int DeltaVersions(FormKey fk, LoadOrderResolver.IndexView view, RecordReads.PoleSpec subject,
                             RecordReads.PoleSpec reference, bool subjectOffOrder, HashSet<string> chunk, ref bool offOrderRead)
    {
        string? subjectPlugin = null;
        if (subjectOffOrder) offOrderRead = true;
        else if (Pole(fk, view, subject, null, chunk, ref offOrderRead, out subjectPlugin) == 0) return 0;
        return 1 + Pole(fk, view, reference, subjectPlugin, chunk, ref offOrderRead, out _);
    }

    /// <summary>One pole of one row: the versions it reads (0 or 1), with its plugin added to the chunk's walk.</summary>
    static int Pole(FormKey fk, LoadOrderResolver.IndexView view, RecordReads.PoleSpec pole, string? subjectPlugin,
                    HashSet<string> chunk, ref bool offOrderRead, out string? plugin)
    {
        plugin = null;
        switch (pole.Kind)
        {
            case RecordReads.PoleKind.PreviousProvider:
                if (subjectPlugin is null || view.TouchingPlugins(fk) is not { } below) return 0;
                for (int i = 1; i < below.Count; i++)
                    if (string.Equals(below[i], subjectPlugin, StringComparison.OrdinalIgnoreCase)) { plugin = below[i - 1]; break; }
                if (plugin is null) return 0;
                chunk.Add(plugin);
                return 1;
            case RecordReads.PoleKind.Named:
                if (!IsInOrder(view, pole)) { offOrderRead = true; return 1; }
                plugin = pole.Plugin!;
                chunk.Add(plugin);   // the chunk declares the key to this plugin whether or not it holds it
                var touching = view.TouchingPlugins(fk);
                return touching is not null && touching.Contains(plugin, StringComparer.OrdinalIgnoreCase) ? 1 : 0;
            default:   // the winner, and both overlay states, which read the winner body
                plugin = view.ResolveWinner(fk)?.WinnerPlugin;
                if (plugin is null) return 0;
                chunk.Add(plugin);
                return 1;
        }
    }

    static bool IsInOrder(LoadOrderResolver.IndexView view, RecordReads.PoleSpec pole) =>
        pole.Mod is null && pole.Plugin is not null && view.ContainsPlugin(pole.Plugin);
}
