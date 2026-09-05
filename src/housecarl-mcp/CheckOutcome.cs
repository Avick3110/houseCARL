using HousecarlCore;

namespace HousecarlMcp;

/// <summary>
/// What this response actually did — composed once per response, read by every caller-facing sentence and every
/// json field on the merged <c>check</c> surface.
///
/// <para>Every quantity here is measured, or read from the artifact that did the work, in one hop. Nothing is
/// re-derived at a call site, and nothing is arithmetic over two collections at the place that prints it.</para>
///
/// <para><b>Selection is not outcome.</b> <see cref="SweepFamilySelection"/> says what the caller asked for; this
/// says what came back, and a family can be selected and refuse. Every response-level claim reads
/// <see cref="Ran"/> / <see cref="Refused"/> / <see cref="NotSelected"/> here, never <c>Selection.Ran</c> or
/// <c>Selection.NotRun</c>.</para>
///
/// <para>A claim may stay a literal at its own site only where all three hold: it reads one field of the artifact
/// that did the work (the family result, or the per-family accounting those results feed), it does no arithmetic at
/// the site that prints it, and it composes across no second family and no second moment. Otherwise it belongs on
/// this value. What that leaves is each family's own head and rows, each family's boundary, the accounting's
/// emitted counts (registered where each unit landed), the <c>limit=</c> and <c>max_chars=</c> echoes,
/// <c>findings_defaulted</c>, and the overrun notice, which is measured off the finished response.</para>
/// </summary>
internal sealed class CheckOutcome
{
    readonly CheckSweep _s;

    /// <summary>Compose the outcome of one sweep. Called once per render, at the top, and handed to everything below
    /// it — including the skeleton pass, so the fixed part is measured over the same claims the response writes.
    /// </summary>
    internal static CheckOutcome For(CheckSweep s) => new(s);

    CheckOutcome(CheckSweep s)
    {
        _s = s;

        // What each selected family did, decided once. A family either produced a result or carries a ground for
        // producing none; nothing below reads the selection again.
        var ran = new List<SweepFamily>();
        var refused = new List<SweepFamily>();
        foreach (var f in s.Selection.Ran)
        {
            if (s.Ran(f)) ran.Add(f);
            else if (s.Ground(f) is not null) refused.Add(f);
        }
        Ran = ran;
        NotSelected = s.Selection.NotRun;

        // A whole call refuses with one error exactly when the grounds are one: distinct grounds are distinct
        // answers, and collapsing them would return one and hide the other, so the caller would fix it, retry and
        // meet the next. The rule needs no special case for a single selected family — one family has one ground,
        // so it collapses anyway. A shared-input ground short-circuits the collapse rather than joining it: it was
        // decided before any family was dispatched, so there is no second ground it could be discarding.
        var grounds = refused.Select(f => s.Ground(f)!).Distinct(StringComparer.Ordinal).ToArray();
        // An order-seam ground short-circuits it for the same reason from the other side: the families disagreed
        // about which build they read, so no section is an answer to keep.
        Error = s.SharedInputError ?? s.OrderSeamError
             ?? (ran.Count == 0 && refused.Count > 0 && grounds.Length == 1 ? grounds[0] : null);
        Refused = Error is null ? refused : Array.Empty<SweepFamily>();

        Sections = SweepFamilySelection.Registered.Where(f => Ran.Contains(f) || Refused.Contains(f)).ToArray();

        // The roster, decided once: which plugins the index could not parse, and which family's accounting declares
        // them. Identical whichever family reports it, so the response emits it once and exactly one accounting
        // subtracts its rows from a total.
        IReadOnlyDictionary<string, string>? roster = null;
        foreach (var f in Sections)
            if (s.Roster(f) is { Count: > 0 } ex) { roster = ex; RosterOwner = f; break; }
        ExcludedPlugins = roster ?? new Dictionary<string, string>();

        Dialogue = s.Dialogue is { Error: null } d
            ? new DialogueOutcome(SeedsNamed: d.SeedsNamed, SeedsReached: d.Seeds.Count,
                                  SeedsValidated: d.Resolved.Count(), SeedsUnreachable: d.Unresolved.Count,
                                  TopicsFound: d.TopicsFound, FindingsFound: d.ProblemsFound,
                                  CountsOnly: d.CountsOnly, Limit: d.Limit,
                                  // Which checks this call actually ran, unioned over the seeds that produced a
                                  // report; a seed that refused ran nothing and contributes nothing. Read by the
                                  // family's boundary, so it cannot assert checks no seed here could have run.
                                  ChecksRun: d.Resolved.Aggregate(DialogueChecks.None,
                                      (acc, seed) => acc | DialogueKindChecks.For(seed.Report!.InputKind)))
            : null;
    }

    /// <summary>The raw results, for the rows. A row renders one artifact; what a sentence says about how many of
    /// them there are comes off this value instead.</summary>
    internal CheckSweep Sweep => _s;

    // ---- what each family did -----------------------------------------------------------------------

    /// <summary>The families that produced a result — not the ones selected. In
    /// <see cref="SweepFamilySelection.Registered"/> order.</summary>
    internal IReadOnlyList<SweepFamily> Ran { get; }

    /// <summary>The families that were selected and answered with a refusal, each rendering as its own section
    /// carrying its own ground. Empty where the whole call collapsed to <see cref="Error"/>.</summary>
    internal IReadOnlyList<SweepFamily> Refused { get; }

    /// <summary>The registered families this call did not select. A caller cannot otherwise tell a family that
    /// found nothing from one that was never asked.</summary>
    internal IReadOnlyList<SweepFamily> NotSelected { get; }

    /// <summary>The whole call's refusal, or null. See the one-ground rule in the constructor.</summary>
    internal string? Error { get; }

    /// <summary>The build any family stamped, for a refusal render.</summary>
    internal string? Epoch => _s.Epoch;

    /// <summary>The plugins the order this call answered from had lost to a load failure — see
    /// <see cref="CheckSweep.OrderExcluded"/>. Stated at the response root, so every lane of a check says it,
    /// including a dialogue-only one that carries no epoch.</summary>
    internal IReadOnlyList<string> OrderExcluded => _s.OrderExcluded;

    /// <summary><c>findings=</c> was omitted, so <see cref="Ran"/> is the default rather than a caller's choice.
    /// The one selection fact a response still states, read through here so it has one spelling.</summary>
    internal bool Defaulted => _s.Selection.Defaulted;

    /// <summary>This family's ground for answering with a refusal, or null where it answered. Reads
    /// <see cref="Refused"/>, so a call that collapsed to <see cref="Error"/> has none — the ground is the whole
    /// answer there, not a section.</summary>
    internal string? Refusal(SweepFamily f) => Refused.Contains(f) ? _s.Ground(f) : null;

    /// <summary>The families this response renders a section for: those that ran, and those that refused
    /// locally.</summary>
    internal IReadOnlyList<SweepFamily> Sections { get; }

    /// <summary>The excluded-plugin roster, emitted once however many families ran.</summary>
    internal IReadOnlyDictionary<string, string> ExcludedPlugins { get; }

    /// <summary>Which family's accounting declares the roster's rows. Exactly one is what matters, not which.
    /// </summary>
    internal SweepFamily? RosterOwner { get; }

    /// <summary>The dialogue family's quantities in one vocabulary, or null where that family did not answer.
    /// </summary>
    internal DialogueOutcome? Dialogue { get; }

    // ---- the response-level sentences ---------------------------------------------------------------

    /// <summary>The scope sentence: which families this response answers for, which selected ones refused, and which
    /// registered ones were never asked — with the exact <c>findings=</c> spelling that adds each absent one.
    ///
    /// <para>Composed from the outcome, not the selection: a call whose one section is a refusal must not lead with
    /// having run every family. Composed once and stated whole by both transports, rather than as a lead each
    /// transport finishes its own way.</para></summary>
    internal string ScopeSentence()
    {
        // A response with no family answering at all is reachable only where several families refused for different
        // grounds — one ground collapses to Error and never reaches a render. That is also why this branch cannot
        // be a defaulted call: the default selects one family, and one family has one ground.
        string lead =
            Ran.Count == 0 ? ReadSentences.SweepFamiliesNoneAnswered
          : Defaulted ? string.Format(ReadSentences.SweepFamiliesDefaulted, Describe(Ran))
          : Refused.Count == 0 && NotSelected.Count == 0
                ? string.Format(ReadSentences.SweepFamiliesAll, Describe(Ran))
                : string.Format(ReadSentences.SweepFamiliesChosen, Describe(Ran));

        if (Refused.Count > 0) lead += string.Format(ReadSentences.SweepFamiliesRefused, Describe(Refused));
        if (NotSelected.Count > 0)
            lead += string.Format(ReadSentences.SweepFamiliesAbsent,
                string.Join(", ", NotSelected.Select(f => string.Format(ReadSentences.SweepFamilyNotRun,
                    SweepFamilySelection.Describe(f), SweepFamilySelection.Spelling(f)))));
        return lead;
    }

    static string Describe(IReadOnlyList<SweepFamily> fs)
        => string.Join(", ", fs.Select(SweepFamilySelection.Describe));

    // ---- the render's own structure -----------------------------------------------------------------

    /// <summary>The allocation plan: which families this response renders, and which of each family's subjects
    /// actually have rows.
    ///
    /// <para>A subject with nothing to render is left out — a share held for rows that do not exist is wasted, and
    /// <see cref="BodyAllocation"/> cannot tell a listed-but-empty subject from a full one; it skips an empty
    /// subject LIST, never an empty subject inside one.</para>
    ///
    /// <para>The excluded roster is not in this plan, because it is a child of no family — it is
    /// <see cref="ResponseSubjects"/>, a top-level participant in the same fill.</para></summary>
    internal IReadOnlyList<(SweepFamily Family, IReadOnlyList<SweepSubject> Subjects)> Plan()
    {
        var plan = new List<(SweepFamily, IReadOnlyList<SweepSubject>)>();
        foreach (var f in Sections)
        {
            var subjects = new List<SweepSubject>();
            if (f == SweepFamily.Errors && _s.Errors is { Error: null } r)
            {
                if (r.CountsOnly)
                {
                    if (r.Histogram is { Count: > 0 }) subjects.Add(SweepSubject.HistogramByTarget);
                    if (r.DanglingBySource is { Count: > 0 }) subjects.Add(SweepSubject.HistogramBySource);
                    if (r.Reports.Count > 0) subjects.Add(SweepSubject.UnreadRows);
                }
                else
                {
                    if (r.Reports.Count > 0) subjects.Add(SweepSubject.PluginSections);
                    if (r.Reports.Any(p => p.Dangling.Count > 0)) subjects.Add(SweepSubject.DanglingEntries);
                }
            }
            else if (f == SweepFamily.Scripts && _s.Scripts is { Error: null } sr)
            {
                if (sr.CountsOnly)
                {
                    if (sr.Histogram is { Count: > 0 }) subjects.Add(SweepSubject.HistogramByProperty);
                    if (sr.Reports.Any(x => x.ScanError is not null)) subjects.Add(SweepSubject.ScriptScanRows);
                }
                else
                {
                    if (sr.Reports.Count > 0) subjects.Add(SweepSubject.ScriptRecords);
                }
            }
            else if (f == SweepFamily.Dialogue && _s.Dialogue is { Error: null } d)
            {
                // The unreachable-seed rows are in the plan in both lanes: they are rendered rows like any other, and
                // a family whose every seed failed still has rows to fit. A family that refused outright contributes
                // no subjects and drops out of the plan below — its section is one sentence.
                if (!d.CountsOnly)
                {
                    if (d.Resolved.Any()) subjects.Add(SweepSubject.DialogueSeeds);
                    if (d.TopicsFound > 0) subjects.Add(SweepSubject.DialogueTopics);
                }
                if (d.Unresolved.Count > 0) subjects.Add(SweepSubject.DialogueSeedRefusals);
            }
            if (subjects.Count > 0) plan.Add((f, subjects));
        }
        return plan;
    }

    /// <summary>The response's own subjects — those that belong to no family. Today that is the excluded-plugin
    /// roster, which a merged response emits once whichever families ran.
    ///
    /// <para>It renders above the family sections, because it is part of what the scope sentence claims and because
    /// every family's accounting is composed inside the section loop and can only report rows already emitted. It
    /// is a participant in the allocation rather than a reserve: ungoverned, its rows would spend against the whole
    /// body budget before the first family head was written; given a reserve of its own, they could not spend it at
    /// all, since a reserve is room every emission test holds standing, including the reserving subject's
    /// own.</para></summary>
    internal IReadOnlyList<SweepSubject> ResponseSubjects
        => ExcludedPlugins.Count > 0 ? new[] { SweepSubject.ExcludedRows } : Array.Empty<SweepSubject>();

    /// <summary>One accounting per family, in section order, with the roster declared by exactly one of them. A
    /// fresh set each call: the render builds one set for the skeleton pass and one for the response, and they are
    /// registered against differently.
    ///
    /// <para>Every one is built at the merged json depth — root, <c>families</c>, the family — whichever transport
    /// asked for it, so the two lanes hold identical accountings and the json reserve is measured where its object
    /// actually lands.</para></summary>
    internal IReadOnlyList<CheckAccounting> Accountings(int cap)
        => Sections.Select(f => f switch
           {
               SweepFamily.Errors => new CheckAccounting(_s.Errors!, cap, JsonWire.FamilySectionDepth,
                                                         declareExcluded: RosterOwner == SweepFamily.Errors),
               SweepFamily.Scripts => new CheckAccounting(_s.Scripts!, cap, JsonWire.FamilySectionDepth,
                                                          declareExcluded: RosterOwner == SweepFamily.Scripts),
               // A dialogue family that refused still gets one: it declares no subject and states no accounting line,
               // but it owns this family's boundary — the standing-limits sentence — which is reserved and written
               // whatever the budget says.
               _ => new CheckAccounting(_s.Dialogue ?? DialogueCheckResult.Fail(""), Dialogue, cap,
                                        JsonWire.FamilySectionDepth),
           })
           .ToArray();
}

/// <summary>
/// The dialogue family's quantities in one vocabulary — the four seed numbers, the topics and the findings, each
/// measured off the result that produced them and each with exactly one meaning wherever the response says it.
///
/// <para>The words are fixed here and nowhere else:</para>
/// <list type="bullet">
/// <item><b>named</b> — how many seeds the caller wrote in <c>seeds=</c>.</item>
/// <item><b>reached</b> — how many of those the seed budget let this call actually try.</item>
/// <item><b>validated</b> — how many reached seeds produced a validation report.</item>
/// <item><b>unreachable</b> — how many reached seeds produced a named refusal instead of a report. These are the
/// <c>[X]</c> rows.</item>
/// </list>
/// <para><c>named ≥ reached</c>, and the difference is the seed budget's cut. <c>validated</c> and
/// <c>unreachable</c> are each counted off the reached seeds independently rather than asserted to sum to it.</para>
/// </summary>
/// <param name="Limit">the seed budget this call was given — the knob the response names, echoed as the caller
/// passed it.</param>
/// <param name="ChecksRun">which checks the seeds this call reached actually ran, unioned over their kinds
/// (<see cref="DialogueKindChecks"/>). The family's boundary is composed from it, so it cannot assert a check no
/// seed here could have run.</param>
internal readonly record struct DialogueOutcome(int SeedsNamed, int SeedsReached, int SeedsValidated,
                                                int SeedsUnreachable, int TopicsFound, int FindingsFound,
                                                bool CountsOnly, int Limit,
                                                DialogueChecks ChecksRun = DialogueChecks.None)
{
    /// <summary>Seeds the caller named that the budget never let this call try. The one subtraction, taken here
    /// rather than at the three sites that state it.</summary>
    internal int SeedsNotReached => Math.Max(0, SeedsNamed - SeedsReached);
}
