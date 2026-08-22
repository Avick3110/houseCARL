using HousecarlCore;

namespace HousecarlMcp;

/// <summary>
/// WHAT THIS RESPONSE ACTUALLY DID — composed ONCE per response, read by every caller-facing sentence and every
/// json field on the merged <c>check</c> surface.
///
/// <para><b>Why it exists.</b> The merged response composes some forty caller-facing claims from state captured at
/// three different moments — selection time, sweep time, render time — across three families. Every claim used to
/// be composed at its own site from whatever happened to be in scope there, so each site could read the wrong
/// quantity, the wrong moment or the wrong family's units, and fixing one taught the next site nothing. Two review
/// rounds found the same class five times, and the folds for it generated it again: a scope sentence counting
/// refused seeds as validated ones, a response-level "ran every family" chosen off the SELECTION while one family's
/// whole section was a refusal, a duplicate key in one family object, a reserve that did not measure four fields
/// the render writes. CLAUDE.md §5 #11's class rule fired, and the ruling (Aaron-go 2026-08-22) is this value: the
/// rule this branch already applies to individual sentences — one computation, two transports — raised one level.
/// </para>
///
/// <para><b>The discipline, which is what makes it worth its name.</b> Every quantity here is MEASURED, or read
/// from the artifact that did the work, in one hop. Nothing is re-derived at a call site and nothing is arithmetic
/// over two collections at the place that prints it — that spelling is exactly what produced
/// <c>Resolved.Count() + Unresolved.Count</c> under the word "validated".</para>
///
/// <para><b>SELECTION IS NOT OUTCOME.</b> <see cref="SweepFamilySelection"/> says what the caller asked for.
/// This says what came back. A family can be selected and refuse, and a response-level sentence composed off the
/// selection then claims an answer the response does not carry. Every response-level claim reads
/// <see cref="Ran"/> / <see cref="Refused"/> / <see cref="NotSelected"/> here, never <c>Selection.Ran</c> or
/// <c>Selection.NotRun</c>.</para>
///
/// <para><b>THE REMAINING-LITERAL INVENTORY — this list IS the completeness claim.</b> Every caller-facing claim in
/// a merged response reads this value, EXCEPT the ones below. The list exists so a reviewer can tell an INVENTORIED
/// literal from a FORGOTTEN one: an unlisted claim that does not read this value is a migration gap, and the way to
/// find one is to apply the membership test rather than to trust the list's length.</para>
///
/// <para><b>The membership test, stated so it can be applied rather than believed.</b> A claim may stay a literal
/// only where all three hold. It reads ONE field of the artifact that did the work — the family result
/// (<see cref="ErrorCheckResult"/>, <see cref="ScriptCheckResult"/>, <see cref="DialogueSeedResult"/>) or the
/// per-family accounting those results feed, both of which are 4a's, produced by the pass that measured them. It
/// does NO arithmetic at the site that prints it. And it composes across NO family and NO second moment. Fail any
/// one and the claim belongs on this value instead: <c>Resolved.Count() + Unresolved.Count</c> failed the second,
/// the response-level "ran every family" failed the third, and both are why this type exists.</para>
///
/// <para>What that leaves, each listed by name with the artifact it reads:
/// <list type="bullet">
/// <item><b>The errors family's head and rows</b> (<c>ReadTools.AppendErrorsHead</c> / <c>AppendErrorsSection</c>,
/// <c>JsonWire.WriteErrorsHead</c> / <c>WriteErrorsSection</c>) — every count reads its own field of
/// <see cref="ErrorCheckResult"/>, the artifact the errors sweep produced.</item>
/// <item><b>The scripts family's head and rows</b> (<c>AppendScriptsHead</c> / <c>AppendScriptsSection</c>,
/// <c>WriteScriptsHead</c> / <c>WriteScriptsSection</c>) — the same, off <see cref="ScriptCheckResult"/>.</item>
/// <item><b>The dialogue family's seed heads, topic blocks and unreachable rows</b>
/// (<see cref="DialogueSweepRender"/>'s composers) — each renders ONE <see cref="DialogueSeedResult"/>'s own
/// report. The family's COUNTS are not here: they are <see cref="Dialogue"/>.</item>
/// <item><b>Each family's boundary</b> (<c>CheckAccounting.Boundary</c>) — a standing claim about what that family
/// can and cannot tell you. It is a constant per family, not a quantity.</item>
/// <item><b>The accounting's EMITTED counts</b> (<c>CheckAccounting</c>'s <c>_emitted</c> tallies) — registered by
/// <see cref="BoundedBody.Emit"/> where each unit LANDED. That is a measurement taken at the only moment it can be
/// taken, and moving it here would be re-deriving it.</item>
/// <item><b>The knob echoes</b> — <c>limit=</c> and <c>max_chars=</c>, printed back as the caller passed
/// them.</item>
/// <item><b><c>findings_defaulted</c></b> — a fact about the CALL, not about the outcome: whether
/// <c>findings=</c> was given at all. It is the one selection fact a response still states, and it states it as
/// one (<see cref="Defaulted"/> reads it through here so there is one spelling).</item>
/// <item><b>The overrun notice</b> (<c>CheckAccounting.CapTooSmall</c>) — measured off the FINISHED response's own
/// length, which is later than this value can be composed.</item>
/// </list></para>
/// </summary>
internal sealed class CheckOutcome
{
    readonly CheckSweep _s;

    /// <summary>Compose the outcome of one sweep. Called ONCE per render, at the top, and handed to everything
    /// below it — including the skeleton pass, so the fixed part is measured over the same claims the response
    /// writes.</summary>
    internal static CheckOutcome For(CheckSweep s) => new(s);

    CheckOutcome(CheckSweep s)
    {
        _s = s;

        // WHAT EACH SELECTED FAMILY DID, decided once. A family either produced a result or carries a ground for
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

        // THE GROUNDS-ARE-ONE COLLAPSE (advisor's standing probe, ruled 2026-08-22). A whole call refuses with ONE
        // error exactly when the grounds are ONE. Distinct grounds are distinct answers, and collapsing them threw
        // one away: findings=['errors','dialogue'] with an exclude= that empties the errors scope and no seeds=
        // refused twice for different reasons and returned only the errors ground, so the caller fixed exclude=,
        // retried, and met the dialogue ground — each answer true, the response one ground short of what it held.
        // The rule is uniform and takes no special case for a single selected family: one family has one ground,
        // so it collapses, which is what its ancestor tool did and what the differential records as unchanged.
        // A SHARED-INPUT ground short-circuits the collapse rather than taking part in it: it was decided before
        // any family was dispatched, so nothing ran, nothing refused, and there is no second ground it could be
        // discarding. `findings=["dialogue"] type="NOTATYPE"` reaches this, and before the shared check existed it
        // reached an ordinary dialogue answer with the typo silently accepted.
        var grounds = refused.Select(f => s.Ground(f)!).Distinct(StringComparer.Ordinal).ToArray();
        Error = s.SharedInputError
             ?? (ran.Count == 0 && refused.Count > 0 && grounds.Length == 1 ? grounds[0] : null);
        Refused = Error is null ? refused : Array.Empty<SweepFamily>();

        Sections = SweepFamilySelection.Registered.Where(f => Ran.Contains(f) || Refused.Contains(f)).ToArray();

        // THE ROSTER, decided once: which plugins the INDEX could not parse, and which family's accounting declares
        // them. Identical whichever family reports it — every rendering family read it off the same build — so the
        // response emits it once and exactly one accounting subtracts its rows from a total.
        IReadOnlyDictionary<string, string>? roster = null;
        foreach (var f in Sections)
            if (s.Roster(f) is { Count: > 0 } ex) { roster = ex; RosterOwner = f; break; }
        ExcludedPlugins = roster ?? new Dictionary<string, string>();

        Dialogue = s.Dialogue is { Error: null } d
            ? new DialogueOutcome(SeedsNamed: d.SeedsNamed, SeedsReached: d.Seeds.Count,
                                  SeedsValidated: d.Resolved.Count(), SeedsUnreachable: d.Unresolved.Count,
                                  TopicsFound: d.TopicsFound, FindingsFound: d.ProblemsFound,
                                  CountsOnly: d.CountsOnly, Limit: d.Limit,
                                  // WHICH CHECKS THIS CALL ACTUALLY RAN, unioned over the seeds that produced a
                                  // report. A seed that produced a refusal ran nothing, so it contributes nothing.
                                  // Read by the family's boundary, which asserted LinkTo, .fuz, result-script and
                                  // condition checks on calls whose every seed was a DLVW or DLBR — records that
                                  // own no INFO list for any of them to run against (round-3 finding A1).
                                  ChecksRun: d.Resolved.Aggregate(DialogueChecks.None,
                                      (acc, seed) => acc | DialogueKindChecks.For(seed.Report!.InputKind)))
            : null;
    }

    /// <summary>The raw results, for the rows. A row renders one artifact and is in the inventory above; what a
    /// SENTENCE says about how many of them there are comes off this value.</summary>
    internal CheckSweep Sweep => _s;

    // ---- what each family did -----------------------------------------------------------------------

    /// <summary>The families that produced a RESULT — not the ones selected. In
    /// <see cref="SweepFamilySelection.Registered"/> order.</summary>
    internal IReadOnlyList<SweepFamily> Ran { get; }

    /// <summary>The families that were selected and answered with a REFUSAL, each rendering as its own section
    /// carrying its own ground. Empty where the whole call collapsed to <see cref="Error"/>.</summary>
    internal IReadOnlyList<SweepFamily> Refused { get; }

    /// <summary>The registered families this call did not select. A caller cannot otherwise tell a family that
    /// found nothing from one that was never asked.</summary>
    internal IReadOnlyList<SweepFamily> NotSelected { get; }

    /// <summary>The WHOLE CALL's refusal, or null. See the grounds-are-one rule in the constructor.</summary>
    internal string? Error { get; }

    /// <summary>The build any family stamped, for a refusal render.</summary>
    internal string? Epoch => _s.Epoch;

    /// <summary><c>findings=</c> was omitted, so <see cref="Ran"/> is the default rather than a caller's choice.
    /// The one SELECTION fact a response still states, read through here so it has one spelling.</summary>
    internal bool Defaulted => _s.Selection.Defaulted;

    /// <summary>This family's ground for answering with a refusal, or null where it answered. Reads
    /// <see cref="Refused"/>, so a call that collapsed to <see cref="Error"/> has none — the ground is the whole
    /// answer there, not a section.</summary>
    internal string? Refusal(SweepFamily f) => Refused.Contains(f) ? _s.Ground(f) : null;

    /// <summary>The families this response renders a section for: those that ran, and those that refused
    /// locally.</summary>
    internal IReadOnlyList<SweepFamily> Sections { get; }

    /// <summary>The excluded-plugin roster, emitted ONCE however many families ran.</summary>
    internal IReadOnlyDictionary<string, string> ExcludedPlugins { get; }

    /// <summary>WHICH family's accounting declares the roster's rows. "Exactly one" is what matters, not which.
    /// </summary>
    internal SweepFamily? RosterOwner { get; }

    /// <summary>The DIALOGUE family's quantities, in ONE vocabulary — or null where that family did not answer.
    /// </summary>
    internal DialogueOutcome? Dialogue { get; }

    // ---- the response-level sentences ---------------------------------------------------------------

    /// <summary>THE SCOPE SENTENCE: which families this response ANSWERS for, which selected ones refused, and
    /// which registered ones were never asked — with the exact <c>findings=</c> spelling that adds each absent one.
    ///
    /// <para><b>It is composed from the OUTCOME, not the selection</b> (round-2 finding B2). Chosen off
    /// <c>Selection.NotRun.Count == 0</c>, a three-family call whose dialogue section was nothing but a cost
    /// refusal led with "findings= ran every findings family this surface registers" — true of what was selected
    /// and false of what came back, in both transports.</para>
    ///
    /// <para>Composed ONCE and stated whole by both transports: a lead each transport finishes its own way is the
    /// shape that has failed twice.</para></summary>
    internal string ScopeSentence()
    {
        // A response with no family answering at all is reachable only where several families refused for DIFFERENT
        // grounds — one ground collapses to Error and never reaches a render. That is also why this arm cannot be a
        // defaulted call: the default selects one family, and one family has one ground.
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

    /// <summary>The off-order asymmetry for ONE family, or null where it does not apply — a plugin the caller named
    /// that is on disk but not in the active load order, which the errors family sweeps and this one does not.
    ///
    /// <para><b>It is asked of the family's SECTION, not of whether that family ran</b> (round-2 finding B4). Gated
    /// on <c>Ran(Scripts)</c> and written only in the non-refusal branch, the sentence vanished from exactly the
    /// call that needs it most: the caller named two plugins, the scripts family refused over the other one, and
    /// nothing in the response said why the first was never in its scope either — while the tool's own parameter
    /// text promises the response says so per family.</para></summary>
    internal string? OffOrder(SweepFamily f)
        => f == SweepFamily.Scripts && _s.ScriptsSkippedOffOrder is { Count: > 0 } off && Sections.Contains(f)
            ? string.Format(ReadSentences.SweepFamilyOffOrderSkipped,
                            SweepFamilySelection.Token(f), string.Join(", ", off))
            : null;

    // ---- the render's own structure -----------------------------------------------------------------

    /// <summary>THE ALLOCATION PLAN (#394, ruling item 1): which families this response renders, and which of each
    /// family's subjects actually HAVE rows.
    ///
    /// <para><b>A subject with nothing to render is left OUT.</b> A share held for rows that do not exist is the
    /// equal-split waste the ruled rule was chosen over, and <see cref="BodyAllocation"/> cannot tell a listed-but-
    /// empty subject from a full one — it skips an empty subject LIST, never an empty subject inside one.</para>
    ///
    /// <para>The excluded roster is not in this plan, because it is not a child of any family — it is a fact about
    /// the SCOPE, emitted once however many families ran. It is not ungoverned either: it is
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
                // The unreachable-seed rows are in the plan in BOTH lanes: they are rendered rows like any other, and
                // a family whose every seed failed still has rows to fit. A family that refused outright contributes
                // no subjects at all and drops out of the plan below — its section is one sentence.
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

    /// <summary>THE RESPONSE'S OWN SUBJECTS — the ones that belong to no family. Today that is the excluded-plugin
    /// roster, and it is exactly the subject a merged response emits once whichever families ran.
    ///
    /// <para><b>Why it is a participant in the allocation rather than a reserve.</b> The roster reads above the
    /// family sections, because it is part of what the scope sentence claims and because every family's accounting
    /// is composed inside the section loop and can only report rows already emitted. Held as a RESERVE up there it
    /// was governed by nothing: <see cref="BodyAllocation.Governs"/> returned false, so its rows were admitted
    /// against the whole body budget before the first family head was written, and the fixed part then went past
    /// the cap — measured at 4,494 chars against a 4,000 cap, with a printed remedy that never converged. Given a
    /// reserve of its own instead, the rows could not have spent it: a reserve is room every emission test holds
    /// STANDING, including the reserving subject's own. A top-level participant is the shape that is both bounded
    /// and spendable, and the roster then inherits the three properties the families have — monotone in
    /// <c>max_chars</c>, no stranding, allocation equals spend.</para></summary>
    internal IReadOnlyList<SweepSubject> ResponseSubjects
        => ExcludedPlugins.Count > 0 ? new[] { SweepSubject.ExcludedRows } : Array.Empty<SweepSubject>();

    /// <summary>One accounting PER FAMILY, in section order, with the roster declared by exactly one of them. A
    /// FRESH set each call: the render builds one set for the skeleton pass and one for the response, and they are
    /// registered against differently.
    ///
    /// <para>Every one is built at the MERGED json depth — root, <c>families</c>, the family — whichever transport
    /// asked for it, so the two lanes hold identical accountings and the json reserve is measured where its object
    /// actually lands. Measured at the root depth an ancestor writes at, it was short by two levels of indentation
    /// on every line (round-2 finding A3).</para></summary>
    internal IReadOnlyList<CheckAccounting> Accountings(int cap)
        => Sections.Select(f => f switch
           {
               SweepFamily.Errors => new CheckAccounting(_s.Errors!, cap, JsonWire.FamilySectionDepth,
                                                         declareExcluded: RosterOwner == SweepFamily.Errors),
               SweepFamily.Scripts => new CheckAccounting(_s.Scripts!, cap, JsonWire.FamilySectionDepth,
                                                          declareExcluded: RosterOwner == SweepFamily.Scripts),
               // A dialogue family that REFUSED still gets one: it declares no subject and states no accounting line,
               // but it owns this family's boundary — the standing-limits sentence — and that is reserved and written
               // whatever the budget says.
               _ => new CheckAccounting(_s.Dialogue ?? DialogueCheckResult.Fail(""), Dialogue, cap,
                                        JsonWire.FamilySectionDepth),
           })
           .ToArray();
}

/// <summary>
/// THE DIALOGUE FAMILY'S QUANTITIES, in ONE vocabulary — the four seed numbers, the topics and the findings, each
/// measured off the result that produced them and each with exactly one meaning wherever the response says it.
///
/// <para><b>The vocabulary, because the words are the defect.</b> The scope sentence, the counts line, the json head
/// and the accounting clause each spelled their own "N of M" and two of them called different quantities by the same
/// word. The scope sentence said it "validated" <c>Resolved + Unresolved</c> seeds — a sum taken at the call site,
/// under a word the <c>[X] … NOT validated</c> rows three lines below it deny. Round 1 found that sentence, the fold
/// for it reproduced it, and round 2 found it again, which is one of the two recurrences that stopped the branch.
/// So the words are fixed here and nowhere else:</para>
/// <list type="bullet">
/// <item><b>named</b> — how many seeds the caller wrote in <c>seeds=</c>.</item>
/// <item><b>reached</b> — how many of those the seed budget let this call actually try.</item>
/// <item><b>validated</b> — how many reached seeds produced a validation REPORT.</item>
/// <item><b>unreachable</b> — how many reached seeds produced a named refusal instead of a report. These are the
/// <c>[X]</c> rows.</item>
/// </list>
/// <para><c>named ≥ reached</c>, and the difference is the seed budget's cut. <c>validated</c> and
/// <c>unreachable</c> are counted off the reached seeds independently rather than asserted to sum to it: each is a
/// measurement of the seed list, and a response that stated their sum as "reached" would be doing arithmetic at the
/// place that prints it, which is the thing this value exists to stop.</para>
/// </summary>
/// <param name="Limit">the seed budget this call was given — the knob the response names, echoed as the caller
/// passed it.</param>
/// <param name="ChecksRun">which checks the seeds this call REACHED actually ran, unioned over their kinds
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
