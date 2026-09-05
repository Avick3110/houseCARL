using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;

namespace HousecarlCore;

// ======================================================================
//  SourceChain — the ordered source universe a walk resolves each link against: a list of
//  poles tried in order, FIRST HIT WINS.
//
//  The list is FALLBACK semantics, never MERGE. A record present in several sources resolves
//  to the FIRST and the readback says which; a record present in NONE refuses naming EVERY
//  source consulted. There is no combine step; assembling one logical record out of what
//  several sources each contribute is a different operation (#342).
//
//  Not the set-valued SOURCE used for COMPARISON, where every pole is resolved and the poles
//  are then diffed. Same shape on the wire, opposite semantics; they never share a parameter.
//
//  Each arm's fetch resolves one pole exactly as a lone pole is resolved. This type adds
//  ordering and provenance on top and must not change how any single arm answers.
//
//  A miss comes back as DATA (the key, what pulled it, the arms consulted); the render owns
//  the refusal sentence.
// ======================================================================

/// <summary>The pole tokens, spelled once so the parser and the refusals cannot disagree about what a token is.
/// <para><b>Bare, not sigiled.</b> These tokens sit beside PLUGIN names, and a Bethesda plugin name is
/// extension-mandatory (<c>.esp</c>/<c>.esm</c>/<c>.esl</c>), so no plugin can ever be spelled <c>winner</c>. An
/// extensionless spelling must refuse by name and never fuzzy-match.</para></summary>
public static class SourcePoles
{
    /// <summary>The active load order as ONE universe — each key's winning version.</summary>
    public const string Winner = "winner";

    /// <summary>Subject-relative, and therefore refused as a walk's source element: a walk reaches records
    /// through links and has no per-key subject plugin for "the provider below the subject" to mean anything
    /// against. Named here so the refusal can quote the token the caller actually typed.</summary>
    public const string PreviousProvider = "previous_provider";
}

/// <summary>How one arm of a chain resolves — the two arms one-pole resolution declares.</summary>
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
/// which is a different claim and is rendered beside it, never instead of it.</para>
/// <para><paramref name="Provider"/> is the MO2 mod FOLDER the arm's file was read from, null when the arm resolved
/// out of the ACTIVE order (no single folder stands behind it). It is carried as data rather than left inside
/// <paramref name="Where"/>'s prose because it is the name a following asset placement passes as its provider: a
/// caller that has to parse it back out of a sentence is a caller guessing.</para></summary>
public sealed record SourceArm(
    string Spelling,
    SourceArmKind Kind,
    string Where,
    Func<FormKey, IMajorRecordGetter?> Fetch,
    string? Provider = null);

/// <summary>One arm as a READBACK names it: the caller's own spelling, how it resolved, and the layer behind it,
/// with the fetch left off. Carried into an outcome so a response can name where each source resolved from after the
/// chain and its overlays are gone.
/// <para><paramref name="Kind"/> travels because a null <paramref name="Provider"/> means two different things: an
/// ACTIVE arm has no single folder behind it, while a FILE arm whose path is outside mods/overwrite/Data has a
/// folder that simply cannot be named. Saying "from the active load order" about the second is a claim that the
/// plugin is in the order when by construction it is not.</para></summary>
public sealed record SourceArmRef(string Spelling, SourceArmKind Kind, string? Provider)
{
    /// <summary>The arm, minus its fetch.</summary>
    public static SourceArmRef Of(SourceArm arm) => new(arm.Spelling, arm.Kind, arm.Provider);
}

/// <summary>A hit: the body, and WHICH arm produced it. The arm index is the provenance the readback is required
/// to state — "first hit wins" is only honest if the caller is told which hit that was.</summary>
public sealed record SourceHit(IMajorRecordGetter Body, int ArmIndex, SourceArm Arm);

/// <summary>A miss, as data. Carries everything a refusal must name: the key, the chain that pulled it (so the
/// caller can see WHY this record was wanted), and every arm consulted — all of them, in order, not just the last
/// one tried. Naming only the last is the failure mode this record exists to make impossible: it reads as though
/// one source was checked, and sends the caller to fix the wrong file.</summary>
public sealed record SourceMiss(FormKey Key, string PulledBy, IReadOnlyList<SourceArm> Consulted);

/// <summary>An arm that HAS the record but could not read it — a record Mutagen cannot parse in that particular
/// source. Distinct from a miss on purpose, and it stops the chain rather than falling through.
/// <para><b>Why a fault is not a fallthrough.</b> Falling through would answer "arm 0's version, please" with
/// arm 1's bytes whenever arm 0's copy is unparseable — a different record, returned silently, under a chain
/// whose contract is "first hit wins". A fault therefore ends the resolution and is reported by arm and by
/// cause, as the single-pole lane reports an unparseable donor record.</para></summary>
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

    /// <summary>True when this chain is the degenerate single-pole case. Rendering leans on it: a one-arm chain has
    /// no ordering to explain, so the readback names the source without the "first of N" framing.</summary>
    public bool IsSinglePole => Arms.Count == 1;

    /// <summary>Build a chain. An EMPTY chain is rejected here rather than fetched-against and reported as a
    /// universal miss: "no source produced this record" when no source was ever named is a true sentence with a
    /// useless cause, and every caller that can produce an empty list has a better refusal at its own layer.</summary>
    public SourceChain(IReadOnlyList<SourceArm> arms)
    {
        if (arms is null || arms.Count == 0)
            throw new ArgumentException("a source chain needs at least one arm — an empty universe is a caller-layer refusal, not a fetch result.", nameof(arms));
        Arms = arms;
    }

    /// <summary>The degenerate chain: one pole.</summary>
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
