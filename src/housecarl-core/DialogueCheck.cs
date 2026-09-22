namespace HousecarlCore;

/// <summary>One SEED's contribution: its report, or the named reason it produced none. Carried, never dropped.</summary>
public sealed record DialogueSeedResult(string Seed, DialogueValidationReport? Report, string? Refusal);

/// <summary>The DIALOGUE family's result over a seed list; contract in docs/architecture/dialogue-validation.md.</summary>
/// <param name="Seeds">one entry per seed the caller named, in that order — reports and refusals together.</param>
/// <param name="ReadIncomplete">a BSA failed to read, so an "absent" voice file or .pex may merely be unscanned.</param>
/// <param name="SeedsNamed">seeds the caller NAMED; the difference from <paramref name="Seeds"/> is the budget's cut.</param>
/// <param name="Epoch">the RECORD build every seed was validated against; it covers no asset verdict a graph check produced.</param>
public sealed record DialogueCheckResult(
    IReadOnlyList<DialogueSeedResult> Seeds,
    int TopicsFound,
    int ProblemsFound,
    bool ReadIncomplete,
    string? Error = null,
    int Limit = 0,
    int SeedsNamed = 0,
    bool CountsOnly = false,
    string? Epoch = null)
{
    public bool Success => Error is null;

    /// <summary>The sentence a folded call leads with; the section's frame, which a render may not drop.</summary>
    public string? Folded { get; init; }

    /// <summary>Seeds that produced a report — the ones with rows to render.</summary>
    public IEnumerable<DialogueSeedResult> Resolved => Seeds.Where(s => s.Report is not null);

    /// <summary>Seeds that produced a named refusal instead of a report. The family's own excluded scope.</summary>
    public IReadOnlyList<DialogueSeedResult> Unresolved => Seeds.Where(s => s.Refusal is not null).ToArray();

    /// <summary>Every live topic across every resolved seed, flattened once so both transports walk one sequence.</summary>
    public IEnumerable<(DialogueSeedResult Seed, TopicValidation Topic)> Topics
        => Resolved.SelectMany(s => s.Report!.Topics.Select(t => (s, t)));

    /// <summary>Conditioned INFOs across ALL topics, not just the rendered ones: what could not be evaluated.</summary>
    public int ConditionedInfos => Topics.Sum(x => x.Topic.ConditionedInfoCount);

    /// <summary>The family's refusal, stamped only where it was decided AFTER a build was read.</summary>
    public static DialogueCheckResult Fail(string error, string? epoch = null) =>
        new(Array.Empty<DialogueSeedResult>(), 0, 0, false, error) { Epoch = epoch };
}
