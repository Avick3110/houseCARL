using HousecarlCore;

namespace HousecarlMcp;

/// <summary>One sweep, several families — the raw results the merged <c>check</c> surface hands a render. A
/// family's result is present exactly where that family ran; a null means "not selected" and never "found
/// nothing". It composes no caller-facing claim and holds no response-level fact.</summary>
/// <param name="SharedInputError">the ground for refusing before any family was dispatched.</param>
/// <param name="OrderSeamError">the ground for refusing after they ran: they did not all answer off one build.</param>
internal sealed record CheckSweep(
    SweepFamilySelection Selection,
    ErrorCheckResult? Errors = null,
    ScriptCheckResult? Scripts = null,
    DialogueCheckResult? Dialogue = null,
    FaceGenCheckResult? FaceGen = null,
    string? SharedInputError = null,
    OrderStamp? Order = null,
    string? OrderSeamError = null)
{
    /// <summary>This family's own ground for producing no result — the refusal its result carries.</summary>
    internal string? Ground(SweepFamily f) => f switch
    {
        SweepFamily.Errors => Errors?.Error,
        SweepFamily.Scripts => Scripts?.Error,
        SweepFamily.Dialogue => Dialogue?.Error,
        SweepFamily.Facegen => FaceGen?.Error,
        _ => null,
    };

    /// <summary>The epoch any family stamped, for a refusal render; a call whose families stamped different builds
    /// refuses through <see cref="OrderSeamError"/> and never reaches here.</summary>
    internal string? Epoch => Errors?.Epoch ?? Scripts?.Epoch ?? Dialogue?.Epoch ?? FaceGen?.Epoch;

    /// <summary>The plugins the order this call answered from had LOST to a load failure, a response-level fact,
    /// stated once at the root rather than assembled from whichever families ran (#353).</summary>
    internal IReadOnlyList<string> OrderExcluded => Order?.ExcludedPlugins ?? Array.Empty<string>();

    internal bool Ran(SweepFamily f) => f switch
    {
        SweepFamily.Errors => Errors is { Error: null },
        SweepFamily.Scripts => Scripts is { Error: null },
        SweepFamily.Dialogue => Dialogue is { Error: null },
        SweepFamily.Facegen => FaceGen is { Error: null },
        _ => false,
    };

    /// <summary>The excluded-plugin roster this family carries, or null where it has none. The dialogue family has
    /// none by nature: what it could not reach is a SEED, stated in its own section.</summary>
    internal IReadOnlyDictionary<string, string>? Roster(SweepFamily f) => f switch
    {
        SweepFamily.Errors => Errors?.ExcludedPlugins,
        SweepFamily.Scripts => Scripts?.ExcludedPlugins,
        SweepFamily.Facegen => FaceGen?.ExcludedPlugins,
        _ => null,
    };
}
