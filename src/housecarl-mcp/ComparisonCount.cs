using HousecarlCore;
using Mutagen.Bethesda.Plugins;

namespace HousecarlMcp;

/// <summary>What a delta or tree batch reads, counted off the index before any body is: the versions each row reads,
/// how many of those belong to contained records, and the megabytes of plugin each chunk walks (#932).</summary>
internal static class ComparisonCount
{
    internal readonly record struct Counted(long Reads, long ContainedReads, int ContainedRows, double MegabytesWalked,
                                            double MegabytesMissWalked);

    /// <summary>A pole the batch reads from an off-order file: swept once for the whole batch, so charged its size once.</summary>
    internal sealed record OffOrderPole(string? Path);

    /// <summary>Count <paramref name="keys"/> as the batch reads them, on the batch's own chunk boundaries; a null key is
    /// an id that did not parse, which holds a delta chunk's slot and reads nothing. <paramref name="subjectOffOrder"/>
    /// and <paramref name="referenceOffOrder"/> are the poles read from off-order files; <paramref name="containedHere"/>
    /// is the off-order lane's own containment.</summary>
    internal static Counted Count(IReadOnlyList<FormKey?> keys, LoadOrderResolver.IndexView view, bool tree,
                                  RecordReads.PoleSpec subject, RecordReads.PoleSpec reference,
                                  OffOrderPole? subjectOffOrder, OffOrderPole? referenceOffOrder,
                                  IReadOnlySet<FormKey>? containedHere)
    {
        var sizes = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        double Size(string? path) =>
            path is null ? 0
            : sizes.TryGetValue(path, out var known) ? known
            : sizes[path] = File.Exists(path) ? new FileInfo(path).Length / 1_000_000.0 : 0;

        long reads = 0, containedReads = 0;
        int containedRows = 0, slot = 0;
        double mb = 0, missMb = 0;
        bool subjectSwept = false, referenceSwept = false;
        var chunk = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var misses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // asked for a key it lacks, so walked to its end
        void Flush()
        {
            foreach (var p in chunk)
                if (misses.Contains(p)) missMb += Size(view.PluginPath(p));
                else mb += Size(view.PluginPath(p));
            chunk.Clear();
            misses.Clear();
        }
        foreach (var key in keys)
        {
            // TreeBatch chunks only the rows that read a body; DeltaBatch chunks every id it was handed.
            bool takesSlot = tree ? key is { } k0 && view.TouchingPlugins(k0) is { Count: > 0 } : true;
            if (!takesSlot) continue;
            if (slot++ % RecordReads.ComparisonChunkRows == 0) Flush();
            if (key is not { } fk) continue;
            int n = tree ? TreeVersions(fk, view, reference, referenceOffOrder, chunk, misses, ref referenceSwept)
                         : DeltaVersions(fk, view, subject, reference, subjectOffOrder, referenceOffOrder, chunk, misses,
                                         ref subjectSwept, ref referenceSwept);
            reads += n;
            if (n > 0 && (containedHere?.Contains(fk) == true || view.ParentOf(fk) is not null))
            {
                containedReads += n;
                containedRows++;
            }
        }
        Flush();
        if (subjectSwept) mb += Size(subjectOffOrder?.Path);
        if (referenceSwept) mb += Size(referenceOffOrder?.Path);
        return new Counted(reads, containedReads, containedRows, mb, missMb);
    }

    /// <summary>A tree row reads every in-order provider, plus the versus= pole when it is not the winner. A named
    /// in-order versus= that holds no version stops the row at the winner.</summary>
    static int TreeVersions(FormKey fk, LoadOrderResolver.IndexView view, RecordReads.PoleSpec reference,
                            OffOrderPole? referenceOffOrder, HashSet<string> chunk, HashSet<string> misses, ref bool referenceSwept)
    {
        var providers = view.TouchingPlugins(fk)!;
        if (reference.Kind == RecordReads.PoleKind.Named && referenceOffOrder is null)
        {
            chunk.Add(reference.Plugin!);
            if (!RecordReads.Holds(view, fk, reference.Plugin!))
            {
                misses.Add(reference.Plugin!);
                chunk.Add(providers[^1]);   // the winner, read before the refused versus= stops the row
                return 1;
            }
        }
        foreach (var p in providers) chunk.Add(p);
        if (reference.Kind == RecordReads.PoleKind.Winner) return providers.Count;
        if (referenceOffOrder is not null) referenceSwept = true;
        return providers.Count + 1;
    }

    /// <summary>A delta row reads its subject, then its reference only when the subject read; each pole reads one
    /// version when it resolves and none when it does not.</summary>
    static int DeltaVersions(FormKey fk, LoadOrderResolver.IndexView view, RecordReads.PoleSpec subject,
                             RecordReads.PoleSpec reference, OffOrderPole? subjectOffOrder, OffOrderPole? referenceOffOrder,
                             HashSet<string> chunk, HashSet<string> misses, ref bool subjectSwept, ref bool referenceSwept)
    {
        string? subjectPlugin = null;
        if (subjectOffOrder is not null) subjectSwept = true;
        else if (Pole(fk, view, subject, null, null, chunk, misses, ref subjectSwept, out subjectPlugin) == 0) return 0;
        return 1 + Pole(fk, view, reference, subjectPlugin, referenceOffOrder, chunk, misses, ref referenceSwept, out _);
    }

    /// <summary>One pole of one row: the versions it reads (0 or 1), with its plugin added to the chunk's walk.</summary>
    static int Pole(FormKey fk, LoadOrderResolver.IndexView view, RecordReads.PoleSpec pole, string? subjectPlugin,
                    OffOrderPole? offOrder, HashSet<string> chunk, HashSet<string> misses, ref bool swept, out string? plugin)
    {
        plugin = null;
        if (offOrder is not null) { swept = true; return 1; }
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
                plugin = pole.Plugin!;
                chunk.Add(plugin);   // the chunk declares the key to this plugin whether or not it holds it
                if (RecordReads.Holds(view, fk, plugin)) return 1;
                misses.Add(plugin);
                return 0;
            default:   // the winner, and both overlay states, which read the winner body
                plugin = view.ResolveWinner(fk)?.WinnerPlugin;
                if (plugin is null) return 0;
                chunk.Add(plugin);
                return 1;
        }
    }
}
