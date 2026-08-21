namespace HousecarlCore;

/// <summary>
/// One SEED's contribution to the dialogue family's result: the validation report it produced, or the named reason
/// it produced none.
///
/// <para>A seed that could not be resolved is carried, never dropped. The dialogue family's scope is the seed list
/// itself (SPEC §6.1 F1.1), so a silently discarded seed is a silently narrowed scope — the caller would read a
/// clean response over topics that were never looked at (Q3). This is the family's own excluded-scope notion, and
/// it is stated in the family's own section rather than merged into the excluded-plugin roster: the roster is about
/// plugins the INDEX could not parse, which is a different fact about a different thing.</para>
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
/// touching-plugin stack, not a findings list, and SPEC §6.1 routes it to <c>records project=info_order</c> — the
/// two surfaces share ONE render (<c>DialogueWire.AppendInfoOrderView</c>) so the split orphaned none of its
/// MOVED-annotation analysis or its honesty gates. This family renders findings and does not call it.</para>
///
/// <para><b>Seeded, and the empty case is a refusal rather than a widening.</b> A call naming this family with no
/// seeds has given it an empty scope. Resolving that to "the whole order" would be a whole-order dialogue sweep,
/// which SPEC §6.1 F1.2 declares a cost-refusal — so <see cref="Error"/> carries the refusal and nothing is swept.
/// That is the same rule the scripts family's <c>noneInScope</c> serves one surface over: an empty scope is empty,
/// never everything.</para>
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
/// <param name="Error">the pre-sweep refusal — no seeds (F1.2), or every seed malformed. Decided before anything
/// is validated, so it is the whole answer when it fires.</param>
/// <param name="Limit">the seed budget this call was given, carried for the reason the sibling families carry
/// theirs: the response names the knob it tells the caller to raise, off the number the call actually used.</param>
/// <param name="SeedsNamed">how many seeds the caller actually NAMED. Kept beside <paramref name="Seeds"/> rather
/// than derived from it, because the budget stops the loop: the difference between the two is seeds this call
/// never looked at, which is a different absence from a topic that did not fit and must not read alike.</param>
/// <param name="CountsOnly">the response carries totals and the unreachable-seed roster, and no topic blocks. The
/// merged tool's own mode applied to this family — a family that ignored it would render a full listing under a
/// parameter documented to suppress one.</param>
///
/// <remarks><b>No EPOCH, and that is the recorded decision rather than an omission.</b> The two sweep families
/// stamp the record build they read (SPEC §2.1.1). A dialogue validation cannot honestly carry that stamp: core
/// pins one view, but half of what it reports — whether a line's <c>.fuz</c> is on disk, whether a result script's
/// <c>.pex</c> exists — comes off the ASSET substrate, which the record fingerprint does not cover.
/// <c>LoadOrderService.ValidateDialogue</c> has carried that note since PR #305's third round and this family keeps
/// it: a stamp covering half the answer would be a claim about freshness the response cannot support (Q3). What is
/// still unowned is an honest stamp spanning both substrates.</remarks>
public sealed record DialogueCheckResult(
    IReadOnlyList<DialogueSeedResult> Seeds,
    int TopicsFound,
    int ProblemsFound,
    bool ReadIncomplete,
    string? Error = null,
    int Limit = 0,
    int SeedsNamed = 0,
    bool CountsOnly = false)
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

    public static DialogueCheckResult Fail(string error) =>
        new(Array.Empty<DialogueSeedResult>(), 0, 0, false, error);
}
