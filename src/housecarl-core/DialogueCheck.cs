namespace HousecarlCore;

/// <summary>
/// One SEED's contribution to the dialogue family's result: the validation report it produced, or the named reason
/// it produced none.
///
/// <para>A seed that could not be resolved is carried, never dropped. The dialogue family's scope IS the seed list,
/// so a silently discarded seed is a silently narrowed scope — the caller would read a clean response over topics
/// that were never looked at. This is the family's own excluded-scope notion, kept apart from the excluded-plugin
/// roster: that roster is about plugins the INDEX could not parse, a different fact about a different thing.</para>
/// </summary>
/// <param name="Seed">the FormID as the caller spelled it.</param>
/// <param name="Report">the validation, or null where the seed did not resolve.</param>
/// <param name="Refusal">why this seed produced nothing, or null where it produced a report.</param>
public sealed record DialogueSeedResult(string Seed, DialogueValidationReport? Report, string? Refusal);

/// <summary>
/// The DIALOGUE family's result on the merged <c>check</c> surface: <c>housecarl_validate_dialogue</c>'s findings
/// (its classes 1-7) over a seed list, aggregated so one response can carry several seeds' worth.
///
/// <para><b>Class 8 is deliberately absent.</b> The effective merged INFO order is an ordered sequence over the
/// touching-plugin stack, not a findings list, so it lives on <c>records project=info_order</c>. Both surfaces share
/// ONE render (<c>DialogueWire.AppendInfoOrderView</c>). This family renders findings and does not call it.</para>
///
/// <para><b>Seeded, and the empty case is a refusal rather than a widening.</b> A call naming this family with no
/// seeds has given it an empty scope. Resolving that to "the whole order" would be a whole-order dialogue sweep,
/// which is refused on cost — so <see cref="Error"/> carries the refusal and nothing is swept. An empty scope is
/// empty, never everything.</para>
/// </summary>
/// <param name="Seeds">one entry per seed the caller named, in the order they named them — reports and refusals
/// together, so a response cannot state a count over a scope it did not actually cover.</param>
/// <param name="TopicsFound">live topics across every seed that resolved. The population the topic rows come
/// from, so the accounting's subtraction is against what the validation actually found.</param>
/// <param name="ProblemsFound">findings across every seed: graph and input issues, silent voiced lines, result
/// scripts that will not fire. NOT capped by anything — it is what the validation found, and the response reports
/// how much of it the budget carried separately.</param>
/// <param name="ReadIncomplete">a BSA failed to read this build, so an "absent" voice file or .pex above may
/// merely be unscanned. Rides into the family's boundary, where it is unrefusable.</param>
/// <param name="Error">the pre-sweep refusal — no seeds, or every seed malformed. Decided before anything
/// is validated, so it is the whole answer when it fires.</param>
/// <param name="Limit">the seed budget this call was given, carried for the reason the sibling families carry
/// theirs: the response names the knob it tells the caller to raise, off the number the call actually used.</param>
/// <param name="SeedsNamed">how many seeds the caller actually NAMED. Kept beside <paramref name="Seeds"/> rather
/// than derived from it, because the budget stops the loop: the difference between the two is seeds this call
/// never looked at, which is a different absence from a topic that did not fit and must not read alike.</param>
/// <param name="CountsOnly">the response carries totals and the unreachable-seed roster, and no topic blocks. The
/// merged tool's own mode applied to this family — a family that ignored it would render a full listing under a
/// parameter documented to suppress one.</param>
/// <param name="Epoch">the RECORD build every seed was validated against — the one pinned view the whole call reads,
/// stamped like the sibling families stamp theirs. It names the record build and nothing else: the verdicts taken off
/// the ASSET substrate (a line's <c>.fuz</c>, a result script's <c>.pex</c> chain, <c>.seq</c> coverage and staleness)
/// are outside the fingerprint, so both renders write it with <c>epoch_covers_all_inputs: false</c> and name those
/// classes rather than omitting the stamp. Null on a refusal decided before any build was read, which is where the
/// sibling families leave theirs null too.</param>
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

    /// <summary>Seeds that produced a report — the ones with rows to render.</summary>
    public IEnumerable<DialogueSeedResult> Resolved => Seeds.Where(s => s.Report is not null);

    /// <summary>Seeds that produced a named refusal instead of a report. The family's own excluded scope.</summary>
    public IReadOnlyList<DialogueSeedResult> Unresolved => Seeds.Where(s => s.Refusal is not null).ToArray();

    /// <summary>Every live topic across every resolved seed, paired with the seed that produced it — the topic rows,
    /// flattened once here so both transports walk the same sequence in the same order.</summary>
    public IEnumerable<(DialogueSeedResult Seed, TopicValidation Topic)> Topics
        => Resolved.SelectMany(s => s.Report!.Topics.Select(t => (s, t)));

    /// <summary>Conditioned INFOs across every resolved seed — the number the standing-limits boundary states. Summed
    /// over ALL topics rather than the rendered ones, because it is a global honesty note about what the validation
    /// could not evaluate, not a description of the listing.</summary>
    public int ConditionedInfos => Topics.Sum(x => x.Topic.ConditionedInfoCount);

    /// <summary>The family's refusal. It carries a stamp only when it was decided AFTER a build was read — the split
    /// the sibling families keep: a refusal decided on the arguments alone has no build to name, and reading one to
    /// stamp it would build the index for a call that is about to refuse.</summary>
    public static DialogueCheckResult Fail(string error, string? epoch = null) =>
        new(Array.Empty<DialogueSeedResult>(), 0, 0, false, error) { Epoch = epoch };
}
