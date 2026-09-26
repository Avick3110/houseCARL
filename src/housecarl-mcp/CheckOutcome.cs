using HousecarlCore;

namespace HousecarlMcp;

/// <summary>What this response actually did — composed once per response, read by every caller-facing sentence and
/// every json field on the merged <c>check</c> surface. Selection is not outcome: a family can be selected and
/// refuse. Which claims live here and which may stay literals at their own site is in
/// docs/architecture/check-families.md.</summary>
internal sealed class CheckOutcome
{
    readonly CheckSweep _s;

    /// <summary>Compose the outcome of one sweep, once per render and handed to everything below it, the skeleton
    /// pass included.</summary>
    internal static CheckOutcome For(CheckSweep s) => new(s);

    CheckOutcome(CheckSweep s)
    {
        _s = s;

        // What each selected family did, decided once; nothing below reads the selection again.
        var ran = new List<SweepFamily>();
        var refused = new List<SweepFamily>();
        foreach (var f in s.Selection.Ran)
        {
            if (s.Ran(f)) ran.Add(f);
            else if (s.Ground(f) is not null) refused.Add(f);
        }
        Ran = ran;
        NotSelected = s.Selection.NotRun;

        // A whole call refuses with one error exactly when the grounds are one; the shared-input and order-seam
        // grounds short-circuit that collapse rather than joining it. Rule in
        // docs/architecture/check-families.md.
        var grounds = refused.Select(f => s.Ground(f)!).Distinct(StringComparer.Ordinal).ToArray();
        Error = s.SharedInputError ?? s.OrderSeamError
             ?? (ran.Count == 0 && refused.Count > 0 && grounds.Length == 1 ? grounds[0] : null);
        Refused = Error is null ? refused : Array.Empty<SweepFamily>();

        Sections = SweepFamilySelection.Registered.Where(f => Ran.Contains(f) || Refused.Contains(f)).ToArray();

        // The roster, decided once: which plugins the index could not parse, and which family's accounting declares
        // them — so exactly one accounting subtracts its rows from a total.
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
                                  // report, so the family's boundary cannot assert a check no seed ran.
                                  ChecksRun: d.Resolved.Aggregate(DialogueChecks.None,
                                      (acc, seed) => acc | DialogueKindChecks.For(seed.Report!.InputKind)))
            : null;
    }

    /// <summary>The raw results, for the rows; what a sentence says about how many there are comes off this
    /// outcome instead.</summary>
    internal CheckSweep Sweep => _s;

    // ---- what each family did -----------------------------------------------------------------------

    /// <summary>The families that produced a result — not the ones selected — in
    /// <see cref="SweepFamilySelection.Registered"/> order.</summary>
    internal IReadOnlyList<SweepFamily> Ran { get; }

    /// <summary>The families that were selected and answered with a refusal, each its own section carrying its own
    /// ground; empty where the whole call collapsed to <see cref="Error"/>.</summary>
    internal IReadOnlyList<SweepFamily> Refused { get; }

    /// <summary>The registered families this call did not select.</summary>
    internal IReadOnlyList<SweepFamily> NotSelected { get; }

    /// <summary>The whole call's refusal, or null. See the one-ground rule in the constructor.</summary>
    internal string? Error { get; }

    /// <summary>The build any family stamped, for a refusal render.</summary>
    internal string? Epoch => _s.Epoch;

    /// <summary>The plugins the order this call answered from had lost to a load failure, stated at the response
    /// root so every lane says it once.</summary>
    internal IReadOnlyList<string> OrderExcluded => _s.OrderExcluded;

    /// <summary>The loose roots this response's asset builds could not read, UNIONED over the families that read
    /// assets — named once at the response root rather than repeated under each family's hedge, where two copies of
    /// one list would take half the answer between them. A union rather than whichever family answered last: the
    /// service refreshes its asset resolver on every access, so two families in one call can answer off two builds,
    /// and the second build's list starts empty and refills only with what that family's own scan touched.</summary>
    internal IReadOnlyList<string> RootFailures =>
        (_s.FaceGen?.RootFailures ?? Array.Empty<string>())
        .Concat(_s.Scripts?.RootFailures ?? Array.Empty<string>())
        .Concat(_s.Dialogue?.RootFailures ?? Array.Empty<string>())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(r => r, StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary><c>findings=</c> was omitted, so <see cref="Ran"/> is the default rather than a caller's choice —
    /// the one selection fact a response still states.</summary>
    internal bool Defaulted => _s.Selection.Defaulted;

    /// <summary>This family's ground for answering with a refusal, or null where it answered; a call that collapsed
    /// to <see cref="Error"/> has none.</summary>
    internal string? Refusal(SweepFamily f) => Refused.Contains(f) ? _s.Ground(f) : null;

    /// <summary>The families this response renders a section for: those that ran, and those that refused locally.</summary>
    internal IReadOnlyList<SweepFamily> Sections { get; }

    /// <summary>The excluded-plugin roster, emitted once however many families ran.</summary>
    internal IReadOnlyDictionary<string, string> ExcludedPlugins { get; }

    /// <summary>Which family's accounting declares the roster's rows; exactly one is what matters, not which.</summary>
    internal SweepFamily? RosterOwner { get; }

    /// <summary>The dialogue family's quantities in one vocabulary, or null where that family did not answer.</summary>
    internal DialogueOutcome? Dialogue { get; }

    // ---- the response-level sentences ---------------------------------------------------------------

    /// <summary>The scope sentence: which families this response answers for, which selected ones refused, and which
    /// registered ones were never asked, with the <c>findings=</c> spelling that adds each absent one. Composed from
    /// the outcome, not the selection, once, and stated whole by both transports.</summary>
    internal string ScopeSentence()
    {
        // A response with no family answering at all is reachable only where several families refused for different
        // grounds, which is also why this branch cannot be a defaulted call.
        string lead =
            Ran.Count == 0 ? CheckSentences.SweepFamiliesNoneAnswered
          : Defaulted ? string.Format(CheckSentences.SweepFamiliesDefaulted, Describe(Ran))
          : Refused.Count == 0 && NotSelected.Count == 0
                ? string.Format(CheckSentences.SweepFamiliesAll, Describe(Ran))
                : string.Format(CheckSentences.SweepFamiliesChosen, Describe(Ran));

        if (Refused.Count > 0) lead += string.Format(CheckSentences.SweepFamiliesRefused, Describe(Refused));
        if (NotSelected.Count > 0)
            lead += string.Format(CheckSentences.SweepFamiliesAbsent,
                string.Join(", ", NotSelected.Select(f => string.Format(CheckSentences.SweepFamilyNotRun,
                    SweepFamilySelection.Describe(f), SweepFamilySelection.Spelling(f)))));
        return lead;
    }

    static string Describe(IReadOnlyList<SweepFamily> fs)
        => string.Join(", ", fs.Select(SweepFamilySelection.Describe));

    // ---- the render's own structure -----------------------------------------------------------------

    /// <summary>The allocation plan: which families this response renders, and which of each family's subjects
    /// actually have rows. A subject with nothing to render is left out, because
    /// <see cref="BodyAllocation"/> cannot tell a listed-but-empty subject from a full one. The excluded roster is a
    /// child of no family and sits in <see cref="ResponseSubjects"/> instead.</summary>
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
            else if (f == SweepFamily.Facegen && _s.FaceGen is { Error: null } fg)
            {
                if (fg.CountsOnly)
                {
                    if (fg.ByClass is { Count: > 0 }) subjects.Add(SweepSubject.FaceGenClassRows);
                    if (fg.ByOwningMod is { Count: > 0 }) subjects.Add(SweepSubject.FaceGenModRows);
                }
                else if (fg.Findings.Count > 0) subjects.Add(SweepSubject.FaceGenRows);
            }
            else if (f == SweepFamily.Dialogue && _s.Dialogue is { Error: null } d)
            {
                // The unreachable-seed rows are in the plan in both lanes; a family that refused outright contributes
                // no subjects and drops out below.
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
    /// roster, which a merged response emits once whichever families ran, above the family sections and as a
    /// participant in the allocation rather than a reserve.</summary>
    internal IReadOnlyList<SweepSubject> ResponseSubjects
        => ExcludedPlugins.Count > 0 ? new[] { SweepSubject.ExcludedRows } : Array.Empty<SweepSubject>();

    /// <summary>One accounting per family, in section order, with the roster declared by exactly one of them — a
    /// fresh set each call, built at the merged json depth whichever transport asked, so the two lanes hold
    /// identical accountings.</summary>
    internal IReadOnlyList<CheckAccounting> Accountings(int cap)
        => Sections.Select(f => f switch
           {
               SweepFamily.Errors => new CheckAccounting(_s.Errors!, cap, JsonWire.FamilySectionDepth,
                                                         declareExcluded: RosterOwner == SweepFamily.Errors),
               SweepFamily.Scripts => new CheckAccounting(_s.Scripts!, cap, JsonWire.FamilySectionDepth,
                                                          declareExcluded: RosterOwner == SweepFamily.Scripts),
               SweepFamily.Facegen => new CheckAccounting(_s.FaceGen ?? FaceGenCheckResult.Fail(""), cap,
                                                          JsonWire.FamilySectionDepth,
                                                          declareExcluded: RosterOwner == SweepFamily.Facegen),
               // A dialogue family that refused still gets one: it declares no subject, but it owns this family's
               // boundary, which is reserved and written whatever the budget says.
               _ => new CheckAccounting(_s.Dialogue ?? DialogueCheckResult.Fail(""), Dialogue, cap,
                                        JsonWire.FamilySectionDepth),
           })
           .ToArray();
}

/// <summary>The dialogue family's quantities in one vocabulary, each measured off the result that produced them. What
/// named, reached, validated and unreachable mean is in docs/architecture/check-scripts-and-dialogue-families.md.</summary>
/// <param name="Limit">the seed budget this call was given, echoed as the caller passed it.</param>
/// <param name="ChecksRun">which checks the reached seeds actually ran, so the boundary asserts no other.</param>
internal readonly record struct DialogueOutcome(int SeedsNamed, int SeedsReached, int SeedsValidated,
                                                int SeedsUnreachable, int TopicsFound, int FindingsFound,
                                                bool CountsOnly, int Limit,
                                                DialogueChecks ChecksRun = DialogueChecks.None)
{
    /// <summary>Seeds the caller named that the budget never let this call try — the one subtraction, taken here
    /// rather than at the sites that state it.</summary>
    internal int SeedsNotReached => Math.Max(0, SeedsNamed - SeedsReached);
}
