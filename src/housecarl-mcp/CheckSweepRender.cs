using HousecarlCore;

namespace HousecarlMcp;

/// <summary>
/// One sweep, several families — the raw results the merged <c>check</c> surface hands a render.
///
/// <para>A family's result is present exactly where that family ran. A null means "not selected" and is never a
/// stand-in for "found nothing": the two are different answers and the response says which it is.</para>
///
/// <para>It carries results and answers questions about one family at a time. It composes no caller-facing claim
/// and holds no response-level fact — those are <see cref="CheckOutcome"/>, composed once per response, so that no
/// claim is recomputed at a call site from state of the wrong moment.</para>
/// </summary>
/// <param name="Selection">which families the caller asked for, which registered ones they did not, and whether
/// that was a choice or the default. What each family then did is <see cref="CheckOutcome"/>.</param>
/// <param name="ScriptsSkippedOffOrder">plugin names the caller asked for that resolved off-order — on disk, not in
/// the active load order. The errors family sweeps those; the scripts family has no off-order lane and did not.
/// Empty on every call that named no such file, and the response states the asymmetry per family.</param>
/// <param name="SharedInputError">the ground for refusing before any family was dispatched — a value malformed as
/// input to every family that could have used it (<see cref="SweepSharedInput"/>). A whole-call answer by
/// construction: no family ran, so there is no section for it to sit in and no sibling answer it could be throwing
/// away. Null on every call whose shared inputs parsed.</param>
internal sealed record CheckSweep(
    SweepFamilySelection Selection,
    ErrorCheckResult? Errors = null,
    ScriptCheckResult? Scripts = null,
    IReadOnlyList<string>? ScriptsSkippedOffOrder = null,
    DialogueCheckResult? Dialogue = null,
    string? SharedInputError = null)
{
    /// <summary>This family's own ground for producing no result — the refusal its result carries. Whether that
    /// ground is the whole call's answer or one section's is <see cref="CheckOutcome"/>'s question.</summary>
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

    /// <summary>The excluded-plugin roster this family carries, or null where it has none. The dialogue family has
    /// none by nature: the roster is which plugins the index could not parse, and a seeded validation produces no
    /// such list. What it could not reach is a SEED, stated in its own section rather than merged into a roster
    /// about plugins — a non-null answer here would put an unparseable-plugin roster under a family that never
    /// looked at one.</summary>
    internal IReadOnlyDictionary<string, string>? Roster(SweepFamily f) => f switch
    {
        SweepFamily.Errors => Errors?.ExcludedPlugins,
        SweepFamily.Scripts => Scripts?.ExcludedPlugins,
        _ => null,
    };
}
