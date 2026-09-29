namespace HousecarlCore;

/// <summary>What one service's reads cost, counted where the work happens: never read by product code, never rendered,
/// only held to by the cost tests. The service owns one and its resolver carries it across a rebuild.</summary>
public sealed class CostCounters
{
    /// <summary>Overlay opens the service's sessions have paid.</summary>
    public long SessionOverlayOpens;

    /// <summary>Per-record whole-plugin seeks.</summary>
    public long BodySeeks;

    /// <summary>Plugins a gather actually walked.</summary>
    public long CollectPasses;

    /// <summary>Provider bodies the conflict-tree fold read.</summary>
    public long TreeBodiesRead;

    /// <summary>Record bodies the chunk gather was asked for.</summary>
    public long KeysWanted;

    /// <summary>Times an absent plugin was explained from the MO2 profile.</summary>
    public long AbsenceExplains;

    /// <summary>Identify passes run by compact and merge.</summary>
    public int IdentifyPasses;

    /// <summary>The most record bodies the last forward walk held at once; reset per walk.</summary>
    public int WalkBodyHighWater;

    /// <summary>The record bodies the last forward walk still held when it returned; reset per walk.</summary>
    public int WalkBodiesHeldAtReturn;
}
