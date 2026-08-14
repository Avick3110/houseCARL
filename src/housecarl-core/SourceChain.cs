using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;

namespace HousecarlCore;

// ======================================================================
//  SourceChain — the ORDERED source universe (SPEC §3.1 AMENDMENT 2026-08-14).
//
//  A walk resolves each link against a SOURCE universe. That universe used to be one
//  pole; it is now an ordered LIST of poles, tried in order, FIRST HIT WINS.
//
//  THE BOUNDARY, and the whole reason this type is small: the list is FALLBACK
//  semantics, never MERGE. A record present in several sources resolves to the FIRST
//  and the readback says which; a record present in NONE refuses naming EVERY source
//  consulted. Assembling one logical thing out of what several sources each contribute
//  is a different operation (#342 stage-2, W5) and deliberately has no expression here —
//  there is no combine step to reach for, because there is no combine step at all.
//
//  NOT §4.1's SOURCE-as-list. That one is set-valued for COMPARISON: every pole is
//  resolved and the poles are then diffed against each other. This is set-valued for
//  RESOLUTION: poles are tried in order and later ones are never consulted once one
//  hits. Same shape on the wire, opposite semantics, and they never share a parameter.
//
//  A LENGTH-1 CHAIN IS TODAY'S GRAMMAR. That is a contract, not an implementation
//  note: each arm's fetch is one pole resolved exactly as a lone pole is resolved, and
//  this type adds ordering and provenance on top without touching how any single arm
//  answers. An arm that resolved differently inside a chain than it does alone would
//  make the amendment text false rather than reveal a bug here.
//
//  SENTENCE-FREE BY DESIGN: a miss comes back as DATA (the key, what pulled it, the
//  arms consulted) and the render composes the refusal from the shared sentence source.
//  Core owning the prose is what the response-layer migration (#337) exists to undo.
// ======================================================================

/// <summary>The §4.2 pole tokens, spelled once so the parser, the refusals and the probes cannot disagree about
/// what a token is.
/// <para><b>Bare, not sigiled, and that follows §5.5 rather than excepting it.</b> §5.5 sigils a pole token only
/// where the co-resident name space can legally contain the bare token. These tokens sit beside PLUGIN names, and a
/// Bethesda plugin name is extension-mandatory (<c>.esp</c>/<c>.esm</c>/<c>.esl</c>), so no plugin can ever be
/// spelled <c>winner</c> — the exact ground of §5.5's standing S1 exemption. The exemption is pinned by a probe
/// arm over this parser, not asserted: an extensionless spelling must refuse by name and never fuzzy-match, with a
/// with-extension control beside it.</para></summary>
public static class SourcePoles
{
    /// <summary>The active load order as ONE universe — each key's winning version.</summary>
    public const string Winner = "winner";

    /// <summary>Subject-relative by §4.3, and therefore refused as a walk's source element: a walk reaches records
    /// through links and has no per-key subject plugin for "the provider below the subject" to mean anything
    /// against. Named here so the refusal can quote the token the caller actually typed.</summary>
    public const string PreviousProvider = "previous_provider";
}

/// <summary>How one arm of a chain resolves — the two arms §4.2 one-pole resolution already declares.</summary>
public enum SourceArmKind
{
    /// <summary>The plugin is in the ACTIVE load order; bodies come off the shared captured build.</summary>
    ActiveOrder,
    /// <summary>The plugin is a FILE on disk outside the active order (a disabled mod, an unticked plugin, a
    /// direct path); bodies come off an overlay opened over that file.</summary>
    File,
}

/// <summary>One element of an ordered source universe: the caller's own spelling, how it resolved, a human
/// description of WHERE it resolved to, and the fetch itself.
/// <para><paramref name="Spelling"/> is kept verbatim because every sentence about this arm must name the source
/// the way the CALLER wrote it — a refusal that renames the caller's input is a refusal they cannot act on.
/// <paramref name="Where"/> is the resolution's own account of itself (the located path, the winner's plugin),
/// which is a different claim and is rendered beside it, never instead of it.</para></summary>
public sealed record SourceArm(
    string Spelling,
    SourceArmKind Kind,
    string Where,
    Func<FormKey, IMajorRecordGetter?> Fetch);

/// <summary>A hit: the body, and WHICH arm produced it. The arm index is the provenance the readback is required
/// to state — "first hit wins" is only honest if the caller is told which hit that was.</summary>
public sealed record SourceHit(IMajorRecordGetter Body, int ArmIndex, SourceArm Arm);

/// <summary>A miss, as data. Carries everything a refusal must name: the key, the chain that pulled it (so the
/// caller can see WHY this record was wanted), and every arm consulted — all of them, in order, not just the last
/// one tried. Naming only the last is the failure mode this record exists to make impossible: it reads as though
/// one source was checked, and sends the caller to fix the wrong file.</summary>
public sealed record SourceMiss(FormKey Key, string PulledBy, IReadOnlyList<SourceArm> Consulted);

/// <summary>An arm that HAS the record but could not read it — a record Mutagen cannot parse in that particular
/// source. Distinct from a miss on purpose (Q3), and it stops the chain rather than falling through.
/// <para><b>Why a fault is not a fallthrough.</b> Falling through would answer "arm 0's version, please" with
/// arm 1's bytes whenever arm 0's copy is unparseable — a different record, returned silently, under a chain
/// whose entire contract is "first hit wins". That is the silent-degraded-mode class, and it is worse here than
/// a plain miss would be: the caller ordered the arms precisely because the earlier one is the version they
/// mean. A fault therefore ends the resolution and is reported by arm and by cause, exactly as the single-pole
/// lane already reports an unparseable donor record.</para></summary>
public sealed record SourceFault(FormKey Key, string PulledBy, int ArmIndex, SourceArm Arm, string Cause);

/// <summary>One resolution's outcome: a hit, a fault, or neither (a miss). Exactly one of <see cref="Hit"/> and
/// <see cref="Fault"/> is ever non-null; both null is the miss, which the caller turns into a
/// <see cref="SourceMiss"/> when it wants to refuse.</summary>
public sealed record SourceFetch(SourceHit? Hit, SourceFault? Fault)
{
    /// <summary>True when no arm had the record and none faulted.</summary>
    public bool IsMiss => Hit is null && Fault is null;
}

/// <summary>The ordered source universe. Immutable, cheap, and deliberately without a merge operation.</summary>
public sealed class SourceChain
{
    /// <summary>The arms, in the order the caller declared them. Order IS the semantics here.</summary>
    public IReadOnlyList<SourceArm> Arms { get; }

    /// <summary>True when this chain is the degenerate single-pole case — the shape every caller had before the
    /// amendment. Rendering leans on it: a one-arm chain has no ordering to explain, so the readback says which
    /// source a body came from without the "first of N" framing that would be noise.</summary>
    public bool IsSinglePole => Arms.Count == 1;

    /// <summary>Build a chain. An EMPTY chain is rejected here rather than fetched-against and reported as a
    /// universal miss: "no source produced this record" when no source was ever named is a true sentence with a
    /// useless cause, and every caller that can produce an empty list has a better refusal to give at its own
    /// layer (Q3 — the failure is named where it is understood).</summary>
    public SourceChain(IReadOnlyList<SourceArm> arms)
    {
        if (arms is null || arms.Count == 0)
            throw new ArgumentException("a source chain needs at least one arm — an empty universe is a caller-layer refusal, not a fetch result.", nameof(arms));
        Arms = arms;
    }

    /// <summary>The degenerate chain, spelled out: one pole, today's grammar.</summary>
    public static SourceChain Single(SourceArm arm) => new(new[] { arm });

    /// <summary>Resolve one key: try each arm IN ORDER and return the FIRST that produces a body, naming which arm
    /// produced it. An arm that HAS the record but cannot parse it returns a <see cref="SourceFault"/> and STOPS
    /// the chain — see that type for why substituting a later arm's bytes there is the silent-wrong-answer class.
    /// Neither = a miss, which the caller turns into a refusal naming every arm.
    /// <para><paramref name="pulledBy"/> is carried into the fault/miss rather than looked up afterwards: by the
    /// time a caller renders the refusal, the chain step that wanted this key is gone.</para></summary>
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

    /// <summary>The miss, as data, for the caller's render.</summary>
    public SourceMiss Miss(FormKey key, string pulledBy) => new(key, pulledBy, Arms);
}
