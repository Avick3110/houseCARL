using System.Collections.Concurrent;

namespace HousecarlCore;

/// <summary>What one service's reads cost, counted where the work happens: never read by product code, never rendered,
/// only held to by the cost tests. The service owns one and its resolver carries it across a rebuild.</summary>
public sealed class CostCounters
{
    /// <summary>Overlay opens the service's sessions have paid.</summary>
    public long SessionOverlayOpens;

    /// <summary>Every per-record seek, typed or not; the flat walks are <c>BodySeeks - TypedSeeks</c>.</summary>
    public long BodySeeks;

    /// <summary>Per-record seeks answered by walking only the record type's groups.</summary>
    public long TypedSeeks;

    /// <summary>Plugins a gather actually walked.</summary>
    public long CollectPasses;

    /// <summary>Gather walks answered by reading only the wanted record types' groups.</summary>
    public long TypedCollectPasses;

    /// <summary>Reads of each plugin's top-level groups by the in-order walks, keyed (plugin, group); a walk with no group
    /// map counts once as <c>*flat</c>.</summary>
    public readonly ConcurrentDictionary<(string Plugin, string Group), int> GroupReads = new();

    /// <summary>Count one walk of the groups <paramref name="seek"/> lives in, or one flat walk when it has none.</summary>
    public void CountGroupRead(string plugin, Type? seek)
    {
        if (seek is not null && MutagenGroups.PropsOf(seek) is { } props)
            foreach (var p in props) CountGroupRead(plugin, p.Name);
        else CountGroupRead(plugin, "*flat");
    }

    /// <summary>Count one read of one named group.</summary>
    public void CountGroupRead(string plugin, string group) => GroupReads.AddOrUpdate((plugin, group), 1, static (_, n) => n + 1);

    /// <summary>Provider bodies the conflict-tree fold read.</summary>
    public long TreeBodiesRead;

    /// <summary>Link caches the service's sessions have built.</summary>
    public long LinkCacheBuilds;

    /// <summary>Record bodies the chunk gather was asked for.</summary>
    public long KeysWanted;

    /// <summary>SkyPatcher layers an overlay read opened for replay.</summary>
    public long ReplayOpens;

    /// <summary>Off-order files a comparison pole swept.</summary>
    public long OffOrderSweeps;

    /// <summary>Identify passes run by compact and merge.</summary>
    public int IdentifyPasses;

    /// <summary>The most record bodies the last forward walk held at once; reset per walk.</summary>
    public int WalkBodyHighWater;

    /// <summary>The record bodies the last forward walk still held when it returned; reset per walk.</summary>
    public int WalkBodiesHeldAtReturn;
}
