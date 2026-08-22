using HousecarlCore;

namespace HousecarlMcp;

/// <summary>
/// ONE SWEEP, SEVERAL FAMILIES — the RAW results the merged <c>check</c> surface hands a render (SPEC §6.1).
///
/// <para>A family's result is present exactly where that family RAN. A null is "this family was not selected", and
/// it is never a stand-in for "this family found nothing": the two are different answers, and the response says
/// which it is.</para>
///
/// <para><b>What this type is NOT.</b> It carries results and answers questions about ONE family at a time. It
/// composes no caller-facing claim and holds no response-level fact — those are <see cref="CheckOutcome"/>, which
/// is composed once per response and which every sentence and json field reads. Both lived here for a while, and a
/// property recomputed at each call site is exactly how a claim ends up reading state from the wrong moment.</para>
/// </summary>
/// <param name="Selection">which families the CALLER asked for, which registered ones they did not, and whether
/// that was a choice or the default. What each family then DID is <see cref="CheckOutcome"/>.</param>
/// <param name="ScriptsSkippedOffOrder">plugin names the caller asked for that resolved OFF-ORDER — on disk, not in
/// the active load order. The errors family sweeps those; the scripts family has no off-order lane and did not.
/// Empty on every call that named no such file. The response states the asymmetry per family rather than widening
/// one family silently or refusing the whole call: extending the off-order lane is capability growth, not merge
/// work.</param>
/// <param name="SharedInputError">the ground for refusing BEFORE any family was dispatched — a value that is
/// malformed as input to every family that could have used it (<see cref="SweepSharedInput"/>). It is a whole-call
/// answer by construction rather than by the grounds-are-one collapse: no family ran, so there is no section for it
/// to sit in and no sibling answer it could be throwing away. Null on every call whose shared inputs parsed.</param>
internal sealed record CheckSweep(
    SweepFamilySelection Selection,
    ErrorCheckResult? Errors = null,
    ScriptCheckResult? Scripts = null,
    IReadOnlyList<string>? ScriptsSkippedOffOrder = null,
    DialogueCheckResult? Dialogue = null,
    string? SharedInputError = null)
{
    /// <summary>THIS family's own ground for producing no result, whatever family it is — the refusal its result
    /// carries. Whether that ground is the whole call's answer or one section's is
    /// <see cref="CheckOutcome"/>'s question, not this one's.</summary>
    internal string? Ground(SweepFamily f) => f switch
    {
        SweepFamily.Errors => Errors?.Error,
        SweepFamily.Scripts => Scripts?.Error,
        SweepFamily.Dialogue => Dialogue?.Error,
        _ => null,
    };

    /// <summary>The epoch any family stamped, for a refusal render. Both sweep families capture the same build.
    /// </summary>
    internal string? Epoch => Errors?.Epoch ?? Scripts?.Epoch;

    /// <summary>Does this family have a result to render?</summary>
    internal bool Ran(SweepFamily f) => f switch
    {
        SweepFamily.Errors => Errors is { Error: null },
        SweepFamily.Scripts => Scripts is { Error: null },
        SweepFamily.Dialogue => Dialogue is { Error: null },
        _ => false,
    };

    /// <summary>The excluded-plugin roster THIS family carries, or null where it has none to carry. The dialogue
    /// family has none and that is its answer rather than an omission: the roster is which plugins the INDEX could
    /// not parse, and a seeded validation produces no such list — what IT could not reach is a SEED, a different
    /// fact about a different thing, stated in its own section (<c>DialogueScopeNote</c> and the unreachable-seed
    /// rows) rather than merged into a roster about plugins.
    ///
    /// <para><b>Which half of that an arm can see, said rather than left to be discovered.</b> The OWNERSHIP half is
    /// unobservable through a multi-family response: <see cref="CheckOutcome.RosterOwner"/> takes the first section
    /// holding a roster, and the errors family is always ahead of this one in
    /// <see cref="SweepFamilySelection.Registered"/>, so sabotaging this arm to hand back a sibling's roster changes
    /// nothing the render prints. What IS observable is a DIALOGUE-ONLY response, where a non-null answer here would
    /// put an unparseable-plugin roster under a family that never looked at one — and
    /// ROSTER-STILL-ONE-WITH-THREE-FAMILIES asks that question too, which is what makes the arm worth its name
    /// rather than a claim that happens to hold.</para></summary>
    internal IReadOnlyDictionary<string, string>? Roster(SweepFamily f) => f switch
    {
        SweepFamily.Errors => Errors?.ExcludedPlugins,
        SweepFamily.Scripts => Scripts?.ExcludedPlugins,
        _ => null,
    };
}
