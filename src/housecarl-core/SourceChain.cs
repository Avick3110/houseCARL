using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;

namespace HousecarlCore;

// SourceChain — the ordered source universe a walk resolves each link against, first hit wins, never a merge. Contracts in docs/architecture/walk-and-reverse.md.

public static class SourcePoles
{
    public const string Winner = "winner";

    /// <summary>Subject-relative, and therefore refused as a walk's source element; named here so a refusal can quote the token the caller typed.</summary>
    public const string PreviousProvider = "previous_provider";
}

public enum SourceArmKind
{
    ActiveOrder,
    /// <summary>The plugin is a FILE on disk outside the active order; bodies come off an overlay opened over that file.</summary>
    File,
}

public enum SourceLayerKind
{
    /// <summary>A folder under MO2's <c>mods\</c> — the only kind whose name a placement can be handed.</summary>
    ModFolder,
    Overwrite,
    GameData,
}

public enum ModFolderStanding
{
    Live = 0,
    /// <summary>modlist.txt lists the folder with the profile's switch OFF. Remedy: switch it on, re-sort.</summary>
    SwitchedOff,
    /// <summary>modlist.txt does not mention the folder at all, so MO2 has not registered it. Remedy: refresh MO2.</summary>
    Unregistered,
}

public sealed record SourceLayer(
    SourceLayerKind Kind, string Name, ModFolderStanding Folder = ModFolderStanding.Live);

/// <summary>One element of an ordered source universe: the caller's verbatim spelling, how it resolved, where it resolved to, the fetch, and the MO2 layer behind it.</summary>
public sealed record SourceArm(
    string Spelling,
    SourceArmKind Kind,
    string Where,
    Func<FormKey, IMajorRecordGetter?> Fetch,
    SourceLayer? Layer = null);

/// <summary>One arm as a READBACK names it, with the fetch left off; <paramref name="Kind"/> travels because a null <paramref name="Layer"/> means two different things.</summary>
public sealed record SourceArmRef(string Spelling, SourceArmKind Kind, SourceLayer? Layer)
{
    public static SourceArmRef Of(SourceArm arm) => new(arm.Spelling, arm.Kind, arm.Layer);
}

/// <summary>A hit: the body, and WHICH arm produced it — the provenance the readback is required to state.</summary>
public sealed record SourceHit(IMajorRecordGetter Body, int ArmIndex, SourceArm Arm);

/// <summary>A miss, as data: the key, the chain that pulled it, and EVERY arm consulted, in order.</summary>
public sealed record SourceMiss(FormKey Key, string PulledBy, IReadOnlyList<SourceArm> Consulted);

/// <summary>An arm that HAS the record but could not read it; it STOPS the chain rather than answering with a later arm's bytes.</summary>
public sealed record SourceFault(FormKey Key, string PulledBy, int ArmIndex, SourceArm Arm, string Cause);

/// <summary>One resolution's outcome: a hit, a fault, or neither (a miss). Exactly one of <see cref="Hit"/> and <see cref="Fault"/> is ever non-null.</summary>
public sealed record SourceFetch(SourceHit? Hit, SourceFault? Fault)
{
    public bool IsMiss => Hit is null && Fault is null;
}

public sealed class SourceChain
{
    /// <summary>The arms, in the order the caller declared them. Order IS the semantics here.</summary>
    public IReadOnlyList<SourceArm> Arms { get; }

    public bool IsSinglePole => Arms.Count == 1;

    /// <summary>Build a chain; an EMPTY chain is rejected here rather than fetched against and reported as a universal miss.</summary>
    public SourceChain(IReadOnlyList<SourceArm> arms)
    {
        if (arms is null || arms.Count == 0)
            throw new ArgumentException("a source chain needs at least one arm — an empty universe is a caller-layer refusal, not a fetch result.", nameof(arms));
        Arms = arms;
    }

    public static SourceChain Single(SourceArm arm) => new(new[] { arm });

    /// <summary>Resolve one key: try each arm IN ORDER and return the FIRST that produces a body, naming which arm produced it; a parse fault stops the chain, and neither is a miss.</summary>
    public SourceFetch Fetch(FormKey key, string pulledBy = "")
    {
        for (int i = 0; i < Arms.Count; i++)
        {
            IMajorRecordGetter? body;
            try { body = Arms[i].Fetch(key); }
            catch (Exception ex)
            {
                return new SourceFetch(null, new SourceFault(key, pulledBy, i, Arms[i], ex.Message));
            }
            if (body is not null) return new SourceFetch(new SourceHit(body, i, Arms[i]), null);
        }
        return new SourceFetch(null, null);
    }

    public SourceMiss Miss(FormKey key, string pulledBy) => new(key, pulledBy, Arms);
}
