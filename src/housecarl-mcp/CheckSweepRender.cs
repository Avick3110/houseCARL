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
    IReadOnlyList<string>? ScriptsSkippedOffOrder = null)
{
    /// <summary>The refusal, if any family refused. A sweep that could not run at all answers with one error rather
    /// than a partly-rendered response — the pre-sweep refusals (a malformed FormID, an unknown type, a plugin
    /// nothing provides) are decided before anything is swept, so they are the whole answer when they fire.</summary>
    internal string? Error => Errors?.Error ?? Scripts?.Error;

    /// <summary>The epoch any family stamped, for a refusal render. Both families capture the same build.</summary>
    internal string? Epoch => Errors?.Epoch ?? Scripts?.Epoch;

    /// <summary>Does this family have a result to render?</summary>
    internal bool Ran(SweepFamily f) => f switch
    {
        SweepFamily.Errors => Errors is { Error: null },
        SweepFamily.Scripts => Scripts is { Error: null },
        _ => false,
    };

    /// <summary>The families this response actually renders, in <see cref="SweepFamilySelection.Registered"/> order
    /// — never the order the caller named them, so two calls selecting the same families render alike.</summary>
    internal IReadOnlyList<SweepFamily> Sections => Selection.Ran.Where(Ran).ToArray();

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
                var ex = f == SweepFamily.Errors ? Errors!.ExcludedPlugins : Scripts!.ExcludedPlugins;
                if (ex.Count > 0) return ex;
            }
            return new Dictionary<string, string>();
        }
    }

    /// <summary>WHICH family's accounting owns the roster: the first section that has one to declare. "Exactly one"
    /// is what matters, not which — every rendering family read the same roster off the same build.</summary>
    internal SweepFamily? RosterOwner
    {
        get
        {
            foreach (var f in Sections)
                if ((f == SweepFamily.Errors ? Errors!.ExcludedPlugins : Scripts!.ExcludedPlugins).Count > 0) return f;
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
    /// <para>The excluded roster is deliberately NOT in the plan. It is a RESPONSE-level subject, not a child of any
    /// family, and it is emitted last — after every family has taken its share — so what it answers to is the
    /// global budget alone, which is what <see cref="BodyAllocation.Governs"/> returning false already means.</para></summary>
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
            else
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
            if (subjects.Count > 0) plan.Add((f, subjects));
        }
        return plan;
    }

    /// <summary>One accounting PER FAMILY, in section order, with the roster declared by exactly one of them.</summary>
    internal IReadOnlyList<CheckAccounting> Accountings(int cap)
        => Sections.Select(f => f == SweepFamily.Errors
                ? new CheckAccounting(Errors!, cap, declareExcluded: RosterOwner == SweepFamily.Errors)
                : new CheckAccounting(Scripts!, cap, declareExcluded: RosterOwner == SweepFamily.Scripts))
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
