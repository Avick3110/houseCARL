using HousecarlCore;

namespace HousecarlMcp;

/// <summary>
/// ONE SWEEP, SEVERAL FAMILIES — what the merged <c>check</c> surface hands a render (SPEC §6.1).
///
/// <para>A family's result is present exactly where that family RAN. A null is "this family was not selected", and
/// it is never a stand-in for "this family found nothing": the two are different answers, and the response says
/// which it is (<see cref="ReadSentences.SweepFamiliesDefaulted"/> and its siblings).</para>
/// </summary>
/// <param name="Selection">which families ran, which registered ones did not, and whether that was the caller's
/// choice or the default.</param>
/// <param name="ScriptsSkippedOffOrder">plugin names the caller asked for that resolved OFF-ORDER — on disk, not in
/// the active load order. The errors family sweeps those; the scripts family has no off-order lane and did not.
/// Empty on every call that named no such file. The response states the asymmetry per family rather than widening
/// one family silently or refusing the whole call: extending the off-order lane is capability growth, not merge
/// work.</param>
internal sealed record CheckSweep(
    SweepFamilySelection Selection,
    ErrorCheckResult? Errors = null,
    ScriptCheckResult? Scripts = null,
    IReadOnlyList<string>? ScriptsSkippedOffOrder = null,
    DialogueCheckResult? Dialogue = null)
{
    /// <summary>The refusal, if any family refused. A sweep that could not run at all answers with one error rather
    /// than a partly-rendered response — the pre-sweep refusals (a malformed FormID, an unknown type, a plugin
    /// nothing provides) are decided before anything is swept, so they are the whole answer when they fire.</summary>
    internal string? Error
    {
        get
        {
            string? first = null;
            foreach (var f in Selection.Ran)
            {
                if (Own(f) is not { } refusal) return null;   // a family answered ⇒ this call was answerable
                first ??= refusal;
            }
            return first;
        }
    }

    /// <summary>The error THIS family's own result carries, whatever family it is.</summary>
    string? Own(SweepFamily f) => f switch
    {
        SweepFamily.Errors => Errors?.Error,
        SweepFamily.Scripts => Scripts?.Error,
        SweepFamily.Dialogue => Dialogue?.Error,
        _ => null,
    };

    /// <summary>A FAMILY-LOCAL refusal: this family could not run, and the others are unaffected. Only the dialogue
    /// family has one, because only it has a scope of its own — its seeds (SPEC §6.1 F1.1). The sweep families'
    /// refusals come off the SHARED scope trio, so a malformed FormID or an unknown type is malformed input for the
    /// whole call and answers as <see cref="Error"/> above.
    ///
    /// <para>A family-local refusal is rendered in that family's OWN SECTION, never at response level, for the
    /// reason F1.3 gives: a call naming several families runs each over its own declared selection. Raised to
    /// response level it would refuse a call the errors family answered perfectly well; dropped, the response would
    /// name the dialogue family in its scope sentence and then render nothing under it, which is silence where a
    /// refusal belongs (Q3).</para></summary>
    internal string? Refusal(SweepFamily f) => Error is null ? Own(f) : null;

    /// <summary>The epoch any family stamped, for a refusal render. Both families capture the same build.</summary>
    internal string? Epoch => Errors?.Epoch ?? Scripts?.Epoch;

    /// <summary>Does this family have a result to render?</summary>
    internal bool Ran(SweepFamily f) => f switch
    {
        SweepFamily.Errors => Errors is { Error: null },
        SweepFamily.Scripts => Scripts is { Error: null },
        SweepFamily.Dialogue => Dialogue is { Error: null },
        _ => false,
    };

    /// <summary>The families this response actually renders, in <see cref="SweepFamilySelection.Registered"/> order
    /// — never the order the caller named them, so two calls selecting the same families render alike.</summary>
    /// <summary>The families this response actually renders, in <see cref="SweepFamilySelection.Registered"/> order
    /// — never the order the caller named them, so two calls selecting the same families render alike. A family that
    /// refused LOCALLY is still a section: its refusal is what that section says.</summary>
    internal IReadOnlyList<SweepFamily> Sections
        => Selection.Ran.Where(f => Ran(f) || Refusal(f) is not null).ToArray();

    /// <summary>The excluded-plugin roster: which plugins the INDEX could not parse. A SCOPE fact, identical
    /// whichever family reports it (both read it off the same captured build), so the response emits it ONCE and
    /// exactly one family's accounting declares its rows. Emitting it per family would put the same rows in the
    /// response twice and have two accountings each subtract them from the same total — the double count that
    /// keeping one subject per lane fact exists to make unrepresentable.</summary>
    internal IReadOnlyDictionary<string, string> ExcludedPlugins
    {
        get
        {
            foreach (var f in Sections)
            {
                var ex = Roster(f);
                if (ex is { Count: > 0 }) return ex;
            }
            return new Dictionary<string, string>();
        }
    }

    /// <summary>The excluded-plugin roster THIS family carries, or null where it has none to carry. The dialogue
    /// family has none and that is its answer rather than an omission: the roster is which plugins the INDEX could
    /// not parse, and a seeded validation produces no such list — what IT could not reach is a SEED, a different
    /// fact about a different thing, stated in its own section (<c>DialogueScopeNote</c> and the unreachable-seed
    /// rows) rather than merged into a roster about plugins.
    ///
    /// <para><b>Which half of that an arm can see, said rather than left to be discovered.</b> The OWNERSHIP half is
    /// unobservable through a multi-family response: <see cref="RosterOwner"/> takes the first section holding a
    /// roster, and the errors family is always ahead of this one in <see cref="SweepFamilySelection.Registered"/>,
    /// so sabotaging this arm to hand back a sibling's roster changes nothing the render prints. What IS observable
    /// is a DIALOGUE-ONLY response, where a non-null answer here would put an unparseable-plugin roster under a
    /// family that never looked at one — and ROSTER-STILL-ONE-WITH-THREE-FAMILIES asks that question too, which is
    /// what makes the arm worth its name rather than a claim that happens to hold.</para></summary>
    IReadOnlyDictionary<string, string>? Roster(SweepFamily f) => f switch
    {
        SweepFamily.Errors => Errors?.ExcludedPlugins,
        SweepFamily.Scripts => Scripts?.ExcludedPlugins,
        _ => null,
    };

    /// <summary>WHICH family's accounting owns the roster: the first section that has one to declare. "Exactly one"
    /// is what matters, not which — every rendering family read the same roster off the same build.</summary>
    internal SweepFamily? RosterOwner
    {
        get
        {
            foreach (var f in Sections)
                if (Roster(f) is { Count: > 0 }) return f;
            return null;
        }
    }

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
            if (f == SweepFamily.Errors)
            {
                var r = Errors!;
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
            else if (f == SweepFamily.Scripts)
            {
                var r = Scripts!;
                if (r.CountsOnly)
                {
                    if (r.Histogram is { Count: > 0 }) subjects.Add(SweepSubject.HistogramByProperty);
                    if (r.Reports.Any(x => x.ScanError is not null)) subjects.Add(SweepSubject.ScriptScanRows);
                }
                else
                {
                    if (r.Reports.Count > 0) subjects.Add(SweepSubject.ScriptRecords);
                }
            }
            else if (Dialogue is { Error: null } d)
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

    /// <summary>One accounting PER FAMILY, in section order, with the roster declared by exactly one of them.</summary>
    internal IReadOnlyList<CheckAccounting> Accountings(int cap)
        => Sections.Select(f => f switch
           {
               SweepFamily.Errors => new CheckAccounting(Errors!, cap, declareExcluded: RosterOwner == SweepFamily.Errors),
               SweepFamily.Scripts => new CheckAccounting(Scripts!, cap, declareExcluded: RosterOwner == SweepFamily.Scripts),
               // A dialogue family that REFUSED still gets one: it declares no subject and states no accounting line,
               // but it owns this family's boundary — the standing-limits sentence — and that is reserved and written
               // whatever the budget says.
               _ => new CheckAccounting(Dialogue ?? DialogueCheckResult.Fail(""), cap),
           })
           .ToArray();

    /// <summary>The SCOPE SENTENCE (ruling item 2): which families ran, which registered ones did not, and the exact
    /// <c>findings=</c> spelling that adds each absent one. Composed ONCE, here, and stated whole by both
    /// transports — a lead each transport finished its own way is the shape that has failed twice.</summary>
    internal string ScopeSentence()
    {
        var ran = string.Join(", ", Selection.Ran.Select(SweepFamilySelection.Describe));
        if (Selection.NotRun.Count == 0)
            return string.Format(ReadSentences.SweepFamiliesAll, ran);
        var absent = string.Join(", ", Selection.NotRun.Select(f =>
            string.Format(ReadSentences.SweepFamilyNotRun, SweepFamilySelection.Describe(f),
                          SweepFamilySelection.Spelling(f))));
        return string.Format(Selection.Defaulted ? ReadSentences.SweepFamiliesDefaulted
                                                 : ReadSentences.SweepFamiliesChosen, ran, absent);
    }

    /// <summary>The off-order asymmetry, stated per family, or null where no such file was named. One sentence per
    /// family that did not sweep them — today only the scripts family can be that family.</summary>
    internal string? OffOrderSentence()
        => ScriptsSkippedOffOrder is { Count: > 0 } off && Ran(SweepFamily.Scripts)
            ? string.Format(ReadSentences.SweepFamilyOffOrderSkipped,
                            SweepFamilySelection.Token(SweepFamily.Scripts), string.Join(", ", off))
            : null;
}
