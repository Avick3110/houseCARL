using System.Text;
using System.Text.Json;
using HousecarlCore;

namespace HousecarlMcp;

/// <summary>
/// One accounting of what a sweep response left out, shared by both transports so the text and json answers cannot
/// disagree.
///
/// <para>Every omission it states is a subtraction against the sweep's own totals, taken after emission stops, so
/// the separate causes sum to the total exactly rather than by two counters happening to agree. The accounting
/// line and the boundary footer are reserved out of the caller's <c>max_chars</c> before the body renders, never
/// appended past it.</para>
///
/// <para>A lane declares the subjects it actually has (<see cref="SweepSubject"/>); every sentence, json field,
/// remedy and reserve derives from that set, so a lane without sections cannot claim about them and a lane with
/// them cannot fail to. Subjects are lane facts, never a findings taxonomy.</para>
/// </summary>
internal sealed class CheckAccounting
{
    // ---- the declared subjects: what this lane HAS, and how much of each the sweep found -------------
    readonly Dictionary<SweepSubject, int> _found = new();
    readonly Dictionary<SweepSubject, int> _emitted = new();

    // ---- the dangling subject's own extras ----------------------------------------------------------
    readonly IReadOnlyList<SweepCount> _bySource;   // true dangling count per source plugin, never limit-capped
    readonly Dictionary<string, int> _bySourceEmitted = new(StringComparer.OrdinalIgnoreCase);
    readonly int _budgetListed;                     // the subset the listing budget admitted into the reports
    readonly int _cap;
    readonly int _limit;
    readonly int _jsonDepth = 1;   // where this accounting's json lands; see MeasureJson
    // The scripts family's listing budget, decomposed as the dangling subject's is: what the sweep found, and the
    // subset the budget admitted into the reports. Both zero on the errors lane.
    readonly int _scriptFindingsFound;
    readonly int _scriptFindingsListed;
    readonly string _scriptTotals = "";   // the class-aware true totals, restated where the cut is reported
    // The dialogue family's quantities, as one value rather than loose ints: the measuring constructor below has to
    // copy them, and one field cannot be half-copied. Null exactly where that family did not answer.
    readonly DialogueOutcome? _dialogue;
    // What this lane closes with. Families state different boundaries and the render reserves room per family for
    // the one that will actually be written, so the boundary is read from here rather than chosen at the render.
    readonly string _boundary;

    /// <summary>Build the accounting for one response, declaring the subjects this lane has.
    ///
    /// <para>Dangling entries only where a per-plugin listing is built and the walk that fills it ran; plugin
    /// sections in every listing lane, entries or not, because the section loop still cuts; unread rows only under
    /// <c>counts_only</c>, where those same plugins are the sections in the listing lane, so the two subjects live
    /// in different lanes and cannot double-count a row; excluded rows wherever the index excluded something.</para>
    /// </summary>
    /// <param name="declareExcluded">whether this accounting owns the excluded-plugin roster. The roster is a scope
    /// fact emitted once per response however many families ran, so exactly one accounting may declare its rows or
    /// the response states the same cut twice.</param>
    /// <param name="jsonDepth">the depth this accounting's json is written at — 1 in a root document, 3 inside a
    /// merged one's <c>families.&lt;token&gt;</c>. The document is indented, so <see cref="MeasureJson"/> must size
    /// the reserve at the depth the object actually lands at.</param>
    internal CheckAccounting(ErrorCheckResult r, int cap, int jsonDepth = 1, bool declareExcluded = true)
    {
        _cap = cap;
        _jsonDepth = jsonDepth;
        _limit = r.Limit;
        _boundary = ReadSentences.SweepBoundary;
        _bySource = r.DanglingBySource ?? Array.Empty<SweepCount>();
        _budgetListed = r.Reports.Sum(p => p.Dangling.Count);
        // A refused family declares nothing: a family-local refusal renders as its own section, so this writer is
        // reachable with a failed result, and declaring subjects anyway asserts completeness over a sweep that
        // never ran.
        if (!r.Success) return;

        if (!r.CountsOnly && r.Classes.HasFlag(ErrorFindingClass.Dangling)) Declare(SweepSubject.DanglingEntries, r.TotalDangling);
        if (!r.CountsOnly) Declare(SweepSubject.PluginSections, r.Reports.Count);
        if (r.CountsOnly) Declare(SweepSubject.UnreadRows, r.Reports.Count);
        if (declareExcluded && r.ExcludedPlugins.Count > 0) Declare(SweepSubject.ExcludedRows, r.ExcludedPlugins.Count);
    }

    /// <summary>The scripts family's accounting — the same class, declaring that family's own subjects: record
    /// sections in the listing lane, script scan rows only under <c>counts_only</c> (in the listing lane those same
    /// entries are the sections, so the two cannot double-count a row), and excluded rows where this accounting
    /// owns the roster.</summary>
    internal CheckAccounting(ScriptCheckResult r, int cap, int jsonDepth = 1, bool declareExcluded = true)
    {
        _cap = cap;
        _jsonDepth = jsonDepth;
        _limit = r.Limit;
        _boundary = ReadSentences.SweepScriptBoundary;
        _bySource = Array.Empty<SweepCount>();
        // Both measured off the result: the totals the sweep counted regardless of the cap, and the findings the
        // reports actually carry.
        _scriptFindingsFound = r.CountsOnly ? 0 : r.TotalUnbound + r.TotalNullObject;
        _scriptFindingsListed = r.CountsOnly ? 0 : r.Reports.Sum(x => x.Unbound.Count + x.NullObjects.Count);
        _scriptTotals = ReadSentences.ScriptTotals(r);
        if (!r.Success) return;   // see the errors ctor: a refused family declares nothing

        if (!r.CountsOnly) Declare(SweepSubject.ScriptRecords, r.Reports.Count);
        if (r.CountsOnly) Declare(SweepSubject.ScriptScanRows, r.Reports.Count(x => x.ScanError is not null));
        if (declareExcluded && r.ExcludedPlugins.Count > 0) Declare(SweepSubject.ExcludedRows, r.ExcludedPlugins.Count);
    }

    /// <summary>The dialogue family's accounting — the same class again, declaring the subjects a seeded family has.
    ///
    /// <para>It declares no excluded-plugin roster: that roster is which plugins the index could not parse, and a
    /// seeded validation does not produce one. A seed it could not reach gets its own subject
    /// (<see cref="SweepSubject.DialogueSeedRefusals"/>) instead.</para>
    ///
    /// <para>The boundary carries the standing-limits footer, so it is reserved out of <c>max_chars</c> and cannot
    /// be dropped by the pressure that cut the findings it qualifies.</para></summary>
    /// <param name="outcome">this family's quantities, null exactly where the family did not answer. The accounting
    /// derives none of them itself; every total it names in prose comes from here.</param>
    internal CheckAccounting(DialogueCheckResult r, DialogueOutcome? outcome, int cap, int jsonDepth = 1)
    {
        _cap = cap;
        _jsonDepth = jsonDepth;
        _limit = r.Limit;
        _bySource = Array.Empty<SweepCount>();
        // The boundary states what the seeds actually ran, not what this family can do: DLVW and DLBR seeds own no
        // INFO list, so the wide sentence would assert graph checks that had nothing to run against. The narrow arm
        // is taken only where a record-level check ran and no seed ran the graph checks; with nothing reached at
        // all the wide sentence is the family's standing claim and stays.
        var ran = outcome?.ChecksRun ?? DialogueChecks.None;
        bool recordLevelOnly = ran.HasFlag(DialogueChecks.RecordParity) && !ran.HasFlag(DialogueChecks.TopicGraph);
        _boundary = string.Format(
            recordLevelOnly ? ReadSentences.DialogueBoundaryRecordLevel : ReadSentences.DialogueBoundary,
            r.ConditionedInfos > 0 ? string.Format(ReadSentences.DialogueConditioned, r.ConditionedInfos) : "",
            r.ReadIncomplete ? ReadSentences.DialogueReadIncomplete : "");
        // Held whether or not this lane lists topics: how many seeds were named and how many the budget let it
        // reach are facts of the call, not of the listing, and both transports must state them alike.
        _dialogue = outcome;

        // A refused family declares nothing: a completeness claim over a validation that never ran reads exactly
        // like "looked, found none".
        if (!r.Success) return;

        if (!r.CountsOnly) Declare(SweepSubject.DialogueSeeds, r.Resolved.Count());
        if (!r.CountsOnly) Declare(SweepSubject.DialogueTopics, r.TopicsFound);
        // In BOTH lanes: a seed nobody could reach bounds the answer, so counts_only must not silence it either.
        Declare(SweepSubject.DialogueSeedRefusals, r.Unresolved.Count);
    }

    /// <summary>What this lane's response closes with — read by the render rather than chosen there, so the
    /// sentence reserved and the sentence written are the same one.</summary>
    internal string Boundary => _boundary;

    void Declare(SweepSubject s, int found) { _found[s] = found; _emitted[s] = 0; }

    internal bool Has(SweepSubject s) => _found.ContainsKey(s);
    int Found(SweepSubject s) => _found.TryGetValue(s, out var n) ? n : 0;
    int Emitted(SweepSubject s) => _emitted.TryGetValue(s, out var n) ? n : 0;

    // ---- registration: the emission helper tells the accounting what it emitted ---------------------

    /// <summary>One unit of <paramref name="s"/> just went into the response. Called from
    /// <see cref="BoundedBody.Emit"/> where the unit landed, never where a section is entered — a section total
    /// would claim entries for a section the cut left half-written.
    ///
    /// <para>A subject this lane did not declare is ignored rather than counted, which lets the histogram rows share
    /// the one bounded-emission path without acquiring an accounting sentence of their own.</para></summary>
    internal void Emitted(SweepSubject s, string? source = null)
    {
        if (!_found.ContainsKey(s)) return;
        _emitted[s]++;
        if (s == SweepSubject.DanglingEntries && source is not null)
            _bySourceEmitted[source] = (_bySourceEmitted.TryGetValue(source, out var had) ? had : 0) + 1;
    }

    // ---- derived ------------------------------------------------------------------------------------

    /// <summary>Refs the listing budget never admitted. A pure sweep fact, so it is readable before the body
    /// renders.</summary>
    internal int OmittedByBudget => Has(SweepSubject.DanglingEntries) ? Found(SweepSubject.DanglingEntries) - _budgetListed : 0;

    /// <summary>Refs the budget admitted and this response then could not fit. Both halves are subtractions off the
    /// same total, so the two causes sum to it exactly.</summary>
    internal int OmittedByCut => Has(SweepSubject.DanglingEntries) ? _budgetListed - Emitted(SweepSubject.DanglingEntries) : 0;

    /// <summary>Property findings the scripts family's listing budget never admitted into the reports. A pure sweep
    /// fact like <see cref="OmittedByBudget"/>: readable before the body renders, and the same number in the worst
    /// case as in the real one.</summary>
    int ScriptOmittedByBudget => Has(SweepSubject.ScriptRecords) ? _scriptFindingsFound - _scriptFindingsListed : 0;

    /// <summary>Seeds the caller named that the seed budget never let this call try. A pure sweep fact like the two
    /// above; the subtraction is taken once for the whole response on <see cref="DialogueOutcome"/>.</summary>
    int DialogueSeedsUnreached => _dialogue?.SeedsNotReached ?? 0;

    /// <summary>Which source plugins are missing entries from this response, largest first. Computed against what
    /// was emitted, so it covers both causes of omission at once.</summary>
    internal IReadOnlyList<SweepCount> MissingBySource
    {
        get
        {
            if (!Has(SweepSubject.DanglingEntries)) return Array.Empty<SweepCount>();
            var acc = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in _bySource)
            {
                int shown = _bySourceEmitted.TryGetValue(row.Key, out var c) ? c : 0;
                if (row.Count > shown) acc[row.Key] = row.Count - shown;
            }
            return SweepFindings.Histogram(acc);
        }
    }

    // ---- the reserve --------------------------------------------------------------------------------

    /// <summary>The chars held back from <c>max_chars</c> so this lane's text accounting line is always affordable.
    /// The boundary's room is reserved separately, because a merged response has one boundary block but one
    /// accounting per family.
    ///
    /// <para>Reserved off the same predicate the line itself uses, so a lane that cannot write the line holds
    /// nothing back for it — room held for an unwritable sentence is a subtraction from the answer.</para></summary>
    internal int TextAccountingReserve => _textReserve ??= CanStateAccounting ? Compose(Worst(escaped: false)).Length + TextWrap : 0;
    int? _textReserve;

    /// <summary>Can this lane write an accounting line at all? <see cref="TextLine"/>'s own test asked of the worst
    /// case, so it is true wherever any rendering of this lane could produce a line. Kept as the same expression
    /// rather than a second list of subjects: the two disagreeing is what makes a reserve stop matching the
    /// sentence it reserves for.</summary>
    bool CanStateAccounting => Has(SweepSubject.DanglingEntries) || Has(SweepSubject.ScriptRecords)
                               || Has(SweepSubject.DialogueTopics)
                               || Missing(Worst(escaped: false));

    /// <summary>This lane's accounting + boundary, in json bytes, without the entry slack. Measured by serializing
    /// the worst case rather than estimating it off the text line — the two encodings differ in escaping and
    /// syntax. The slack is separate because a merged document holds one accounting per family but only ever lands
    /// one unit over its budget, so the slack belongs to the response, not to each family.</summary>
    internal int JsonAccountingReserve => _jsonReserve ??= MeasureJson(Worst(escaped: true));
    int? _jsonReserve;

    /// <summary>The slack a json response holds for the one unit that can land past the budget. A
    /// <c>Utf8JsonWriter</c> cannot measure an object without writing it, so a unit whose cost the site left at
    /// zero is tested before the write and lands over; this covers one whole entry.</summary>
    internal const int JsonEntrySlack = JsonGlue;

    /// <summary>Slack over the measured worst case; the two lanes need very different amounts.
    ///
    /// <para>The text lane composes each unit and tests <c>length + cost</c> before appending, so it needs only the
    /// newlines the accounting and boundary are wrapped in. The json lane cannot do that — a
    /// <c>Utf8JsonWriter</c> cannot measure an object without writing it — so its per-entry test is taken before the
    /// write and the last entry lands over; the slack has to cover one whole entry, and it also absorbs
    /// <see cref="BoundedBody"/>'s post-check.</para>
    ///
    /// <para><see cref="TextWrap"/> is charged per wrapped block rather than once for both, because the accounting
    /// and the boundary are not both always written.</para></summary>
    const int TextWrap = 32;
    const int JsonGlue = 1024;

    /// <summary>The values that make the longest line this sweep could produce. Every substitution is at or above
    /// what a real render can reach: the counts are the totals, so their digit widths bound every real count; every
    /// optional clause is present; and the roster holds the longest source names rather than the largest, because a
    /// partly-listed response can promote a long-named small source into the roster — "largest" is not a bound and
    /// "longest" is.
    ///
    /// <para>Length is measured in the lane's own encoding, which is what <paramref name="escaped"/> selects. The
    /// two lanes disagree about which names are longest, so each asks its own question rather than sharing one
    /// ranking.</para></summary>
    Values Worst(bool escaped)
    {
        int danglingFound = Found(SweepSubject.DanglingEntries);
        // The roster is the dangling subject's, so a lane without that subject reserves nothing for it — the
        // by-source tally is collected on every sweep, including lanes that can never emit the roster.
        var longest = Has(SweepSubject.DanglingEntries)
            ? _bySource.OrderByDescending(c => escaped ? JsonEncodedText.Encode(c.Key).Value.Length : c.Key.Length)
                       .Take(ReadSentences.SweepRosterRows)
                       .Select(c => new SweepCount(c.Key, danglingFound))
                       .ToList()
            : new List<SweepCount>();
        // Every slot at its widest, not at zero: the emitted counts are digits in the rendered line, and a real
        // response's are wider than 0.
        var emitted = new Dictionary<SweepSubject, int>();
        foreach (var kv in _found) emitted[kv.Key] = kv.Value;
        emitted[SweepSubject.DanglingEntries] = danglingFound;
        return new Values(Visible: danglingFound, ByBudget: danglingFound, ByCut: danglingFound, Roster: longest,
                          RosterTotal: Math.Max(_bySource.Count, longest.Count), Emitted: emitted, Worst: true);
    }

    /// <summary>What this response actually did.</summary>
    Values Real() => new(Emitted(SweepSubject.DanglingEntries), OmittedByBudget, OmittedByCut,
                         MissingBySource, MissingBySource.Count, _emitted, Worst: false);

    /// <summary>The numbers one rendering of the accounting states. A record rather than a parameter list so the
    /// real case and the worst case go through one composer per transport — a second formatter would be a second
    /// spelling, and the reserve would stop bounding what is written.</summary>
    readonly record struct Values(int Visible, int ByBudget, int ByCut, IReadOnlyList<SweepCount> Roster,
                                  int RosterTotal, IReadOnlyDictionary<SweepSubject, int> Emitted, bool Worst);

    /// <summary>Is this subject short in this rendering? One test, used by the clause that states it, by
    /// <see cref="Missing"/> and by the remedy, so the three cannot disagree.
    ///
    /// <para><c>Found(s) &gt; 0</c> is there for the worst case: a subject can be declared but empty, and without
    /// this term it would reserve room for a clause no rendering of it can write.</para></summary>
    bool Short(Values v, SweepSubject s)
        => Has(s) && Found(s) > 0 && (v.Worst || (v.Emitted.TryGetValue(s, out var e) ? e : 0) < Found(s));

    int Shown(Values v, SweepSubject s) => v.Emitted.TryGetValue(s, out var e) ? e : 0;

    // ---- the text lane ------------------------------------------------------------------------------

    /// <summary>The accounting as the text transport states it, or null where there is nothing to account for.
    /// Present on every response that has a listing subject, complete or not, so that silence never has to mean
    /// both "everything is here" and "something was dropped". A lane with no listing has no completeness to assert,
    /// so it states an accounting only when something is actually short.</summary>
    internal string? TextLine()
    {
        var v = Real();
        return Has(SweepSubject.DanglingEntries) || Has(SweepSubject.ScriptRecords)
               || Has(SweepSubject.DialogueTopics) || Missing(v) ? Compose(v) : null;
    }

    string Compose(Values v)
    {
        // The opener and the closer sit outside every subject gate — they are not about any subject, and a lane
        // with no listing would otherwise emit a bare clause and an orphan closer.
        var sb = new StringBuilder(ReadSentences.SweepAccountingLead);

        if (Has(SweepSubject.DanglingEntries))
        {
            int found = Found(SweepSubject.DanglingEntries);
            int omitted = v.ByBudget + v.ByCut;
            sb.Append(omitted > 0 || v.Worst
                ? string.Format(ReadSentences.SweepVisible, v.Visible, found)
                : string.Format(ReadSentences.SweepAllVisible, found));

            var causes = new List<string>();
            if (v.ByBudget > 0 || v.Worst) causes.Add(string.Format(ReadSentences.SweepOmittedByBudget, v.ByBudget, _limit));
            if (v.ByCut > 0 || v.Worst) causes.Add(string.Format(ReadSentences.SweepOmittedByCut, v.ByCut, _cap));
            if (causes.Count > 0) sb.Append(string.Join(",", causes)).Append('.');
        }

        // The scripts family's lead + budget clause: a completeness assertion off what the response emitted, then
        // the listing budget's share of what is absent, against the sweep's own totals.
        if (Has(SweepSubject.ScriptRecords))
        {
            sb.Append(Short(v, SweepSubject.ScriptRecords)
                ? string.Format(ReadSentences.SweepScriptVisible, Shown(v, SweepSubject.ScriptRecords),
                                Found(SweepSubject.ScriptRecords))
                : string.Format(ReadSentences.SweepScriptAllVisible, Found(SweepSubject.ScriptRecords)));
            if (ScriptOmittedByBudget > 0 || v.Worst)
                sb.Append(string.Format(ReadSentences.SweepScriptFindings, _scriptFindingsListed,
                                        _scriptFindingsFound, _limit, _scriptTotals));
        }
        // The same two-part shape in a seeded family's units. Separate clauses because they are separate absences:
        // a topic that did not fit is a rendering fact, a seed the budget never reached is a scope fact, and one
        // sentence for both would name the wrong knob.
        if (Has(SweepSubject.DialogueTopics))
        {
            sb.Append(Short(v, SweepSubject.DialogueTopics)
                ? string.Format(ReadSentences.SweepDialogueVisible, Shown(v, SweepSubject.DialogueTopics),
                                Found(SweepSubject.DialogueTopics))
                : string.Format(ReadSentences.SweepDialogueAllVisible, Found(SweepSubject.DialogueTopics)));
        }
        // Reached against named, in the outcome's own words — the same two quantities the scope sentence states, so
        // the two cannot call different numbers by one name.
        if (_dialogue is { } dlg && (DialogueSeedsUnreached > 0 || (v.Worst && Has(SweepSubject.DialogueSeedRefusals))))
            sb.Append(string.Format(ReadSentences.SweepDialogueSeedsCut, dlg.SeedsReached, dlg.SeedsNamed,
                                    dlg.SeedsNotReached, _limit));
        // The totals, restated wherever this family's listing is short — they are never capped, and a short listing
        // with no total beside it reads as the whole answer.
        if (_dialogue is { } dlgT && Has(SweepSubject.DialogueTopics)
            && (Short(v, SweepSubject.DialogueTopics) || DialogueSeedsUnreached > 0 || v.Worst))
            sb.Append(string.Format(ReadSentences.SweepDialogueProblems, dlgT.FindingsFound,
                                    Found(SweepSubject.DialogueTopics)));
        if (Short(v, SweepSubject.DialogueSeeds))
            sb.Append(string.Format(ReadSentences.SweepDialogueSeedSections, Shown(v, SweepSubject.DialogueSeeds),
                                    Found(SweepSubject.DialogueSeeds)));
        if (Short(v, SweepSubject.DialogueSeedRefusals))
            sb.Append(string.Format(ReadSentences.SweepDialogueRefusalsCut, Shown(v, SweepSubject.DialogueSeedRefusals),
                                    Found(SweepSubject.DialogueSeedRefusals)));

        // The scripts family's counts_only honesty layer, in its own subject: the plugins whose record enumeration
        // faulted.
        if (Short(v, SweepSubject.ScriptScanRows))
            sb.Append(string.Format(ReadSentences.SweepUnreadCut, Shown(v, SweepSubject.ScriptScanRows),
                                    Found(SweepSubject.ScriptScanRows)));

        // One clause per short subject, computed from the subject it names. A section is dropped by the render, and
        // the render runs whether or not the dangling walk did.
        if (Short(v, SweepSubject.PluginSections))
            sb.Append(string.Format(ReadSentences.SweepSections, Shown(v, SweepSubject.PluginSections),
                                    Found(SweepSubject.PluginSections)));
        // The two honesty-layer rosters. Their rows are what houseCARL could NOT read, so a silent cut there hides
        // the boundary of the answer rather than a finding inside it.
        if (Short(v, SweepSubject.ExcludedRows))
            sb.Append(string.Format(ReadSentences.SweepExcludedCut, Shown(v, SweepSubject.ExcludedRows),
                                    Found(SweepSubject.ExcludedRows)));
        if (Short(v, SweepSubject.UnreadRows))
            sb.Append(string.Format(ReadSentences.SweepUnreadCut, Shown(v, SweepSubject.UnreadRows),
                                    Found(SweepSubject.UnreadRows)));

        if (v.Roster.Count > 0)
        {
            sb.Append(ReadSentences.SweepRosterLead);
            for (int i = 0; i < v.Roster.Count && i < ReadSentences.SweepRosterRows; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(v.Roster[i].Key).Append(" (").Append(v.Roster[i].Count).Append(')');
            }
            if (v.RosterTotal > ReadSentences.SweepRosterRows || v.Worst)
                sb.Append(string.Format(ReadSentences.SweepRosterCut, ReadSentences.SweepRosterRows, v.RosterTotal));
            sb.Append('.');
            // The rule belongs to the roster — it explains what the roster is for, so it is stated where one exists.
            sb.Append(ReadSentences.SweepNoSectionRule);
        }

        if (Missing(v))
        {
            if (v.ByBudget > 0 || ScriptOmittedByBudget > 0 || DialogueSeedsUnreached > 0 || v.Worst)
                sb.Append(ReadSentences.SweepRemedyLimit);
            // max_chars is the knob for every subject except the listing budget's own share.
            if (v.ByCut > 0 || v.Worst || Short(v, SweepSubject.PluginSections)
                || Short(v, SweepSubject.ExcludedRows) || Short(v, SweepSubject.UnreadRows)
                || Short(v, SweepSubject.ScriptRecords) || Short(v, SweepSubject.ScriptScanRows)
                || Short(v, SweepSubject.DialogueSeeds) || Short(v, SweepSubject.DialogueTopics)
                || Short(v, SweepSubject.DialogueSeedRefusals))
                sb.Append(ReadSentences.SweepRemedyMaxChars);
            if (v.Roster.Count > 0) sb.Append(ReadSentences.SweepRemedyScope).Append(ReadSentences.SweepRemedyCountsOnly);
        }
        sb.Append(ReadSentences.SweepClose);
        return sb.ToString();
    }

    /// <summary>Is anything at all absent from this response? One test over every declared subject, so the remedy
    /// and the clauses above it cannot disagree, and a subject the lane does not have cannot make it true. Dropped
    /// sections belong here: a sweep that omits no dangling ref can still have cut sections.
    /// <para>It does not take <c>v.Worst</c> as an answer on its own — the worst case is every declared subject at
    /// its widest, not every subject declared, so reading it as "assume something is missing" reserves remedy
    /// clauses for a lane that cannot write them. The worst case still dominates the real one term by term, so the
    /// reserve stays an upper bound.</para></summary>
    bool Missing(Values v)
        => v.ByBudget + v.ByCut > 0 || ScriptOmittedByBudget > 0 || DialogueSeedsUnreached > 0
           || Short(v, SweepSubject.PluginSections) || Short(v, SweepSubject.ExcludedRows)
           || Short(v, SweepSubject.UnreadRows)
           || Short(v, SweepSubject.ScriptRecords) || Short(v, SweepSubject.ScriptScanRows)
           || Short(v, SweepSubject.DialogueSeeds) || Short(v, SweepSubject.DialogueTopics)
           || Short(v, SweepSubject.DialogueSeedRefusals);

    // ---- the json lane ------------------------------------------------------------------------------

    /// <summary>The accounting as json states it — the same numbers, in the transport's own terms.
    ///
    /// <para>It writes the required in-band fields here too rather than at the call site:
    /// <see cref="JsonAccountingReserve"/> measures this method, so a field written anywhere else is a field outside
    /// the reserve.</para></summary>
    internal void WriteJson(Utf8JsonWriter w) => WriteJson(w, Real());

    void WriteJson(Utf8JsonWriter w, Values v)
    {
        // A field named for a subject is present exactly where that subject is, and absent otherwise — never a zero
        // standing in for "this lane has no such thing".
        bool sections = Has(SweepSubject.PluginSections);
        bool dangling = Has(SweepSubject.DanglingEntries);
        // The scripts family's listing subject.
        bool scriptSections = Has(SweepSubject.ScriptRecords);
        // The dialogue family's listing subject. Its "capped" is the SEED budget — how many seeds a call expands —
        // which is a different quantity from the sibling families' finding budgets even though all three are
        // spelled limit=.
        bool dialogueTopics = Has(SweepSubject.DialogueTopics);
        if (dangling) w.WriteBoolean("capped", v.ByBudget > 0);
        else if (scriptSections) w.WriteBoolean("capped", ScriptOmittedByBudget > 0);
        else if (dialogueTopics) w.WriteBoolean("capped", DialogueSeedsUnreached > 0);
        if (sections)
        {
            w.WriteNumber("plugins_with_findings", Found(SweepSubject.PluginSections));
            w.WriteNumber("rendered", Shown(v, SweepSubject.PluginSections));
            // truncated is this response's fact over every subject it has; capped above is the listing budget's
            // separate one.
            w.WriteBoolean("truncated", v.ByCut > 0 || Short(v, SweepSubject.PluginSections)
                                        || Short(v, SweepSubject.ExcludedRows) || Short(v, SweepSubject.UnreadRows));
        }
        if (scriptSections)
        {
            w.WriteNumber("records_with_findings", Found(SweepSubject.ScriptRecords));
            w.WriteNumber("rendered", Shown(v, SweepSubject.ScriptRecords));
            w.WriteBoolean("truncated", Short(v, SweepSubject.ScriptRecords)
                                        || Short(v, SweepSubject.ExcludedRows) || Short(v, SweepSubject.ScriptScanRows));
        }
        if (dialogueTopics)
        {
            // No topic total here: the family head already writes it as `topics_found`, and writing it here too
            // would be the same key twice in one object. The sibling families' totals are not in their heads, so
            // theirs stay.
            w.WriteNumber("rendered", Shown(v, SweepSubject.DialogueTopics));
            w.WriteBoolean("truncated", Short(v, SweepSubject.DialogueTopics) || Short(v, SweepSubject.DialogueSeeds)
                                        || Short(v, SweepSubject.DialogueSeedRefusals));
        }
        w.WriteStartObject("accounting");
        w.WriteBoolean("listing", dangling || scriptSections || dialogueTopics);
        if (dangling)
        {
            w.WriteNumber("dangling_found", Found(SweepSubject.DanglingEntries));
            w.WriteNumber("dangling_visible", v.Visible);
            w.WriteNumber("dangling_missing", v.ByBudget + v.ByCut);
            w.WriteNumber("dangling_missing_by_budget", v.ByBudget);
            w.WriteNumber("dangling_missing_by_response_cut", v.ByCut);
            w.WriteNumber("limit", _limit);
        }
        if (sections)
        {
            w.WriteNumber("sections_with_findings", Found(SweepSubject.PluginSections));
            w.WriteNumber("sections_rendered", Shown(v, SweepSubject.PluginSections));
        }
        if (scriptSections)
        {
            // In this family's own units: the findings the sweep counted, the subset the listing budget admitted,
            // and the record sections this response carried.
            w.WriteNumber("script_findings_found", _scriptFindingsFound);
            w.WriteNumber("script_findings_listed", _scriptFindingsListed);
            w.WriteNumber("script_findings_missing_by_budget", ScriptOmittedByBudget);
            w.WriteNumber("record_sections_with_findings", Found(SweepSubject.ScriptRecords));
            w.WriteNumber("record_sections_rendered", Shown(v, SweepSubject.ScriptRecords));
            w.WriteNumber("limit", _limit);
        }
        if (Has(SweepSubject.ScriptScanRows))
        {
            w.WriteNumber("script_scan_errors_total", Found(SweepSubject.ScriptScanRows));
            w.WriteNumber("script_scan_errors_named", Shown(v, SweepSubject.ScriptScanRows));
        }
        // What this response DID with the seeds, not how many there were: the family head states every quantity the
        // outcome holds, so restating them here would be a duplicate key in the same object.
        if (_dialogue is not null)
        {
            w.WriteNumber("seeds_not_reached_by_budget", DialogueSeedsUnreached);
            w.WriteNumber("limit", _limit);
        }
        if (dialogueTopics) w.WriteNumber("dialogue_topics_rendered", Shown(v, SweepSubject.DialogueTopics));
        // In both lanes: a seed nobody could reach bounds the answer rather than sitting inside it, so counts_only
        // states how many of them this response named too.
        if (Has(SweepSubject.DialogueSeedRefusals))
            w.WriteNumber("seeds_unreachable_named", Shown(v, SweepSubject.DialogueSeedRefusals));
        // A fact about the CALL rather than about any subject, so every lane writes it: the cap it was given.
        w.WriteNumber("max_chars", _cap);
        // The same rule as at the head of this method, applied to the three blocks below: a field named for a
        // subject is present exactly where that subject is. A seeded family has no plugin scope and no dangling
        // roster, so writing these unconditionally puts another family's zeros inside its object.
        if (Has(SweepSubject.ExcludedRows))
        {
            w.WriteNumber("excluded_plugins_total", Found(SweepSubject.ExcludedRows));
            w.WriteNumber("excluded_plugins_named", Shown(v, SweepSubject.ExcludedRows));
        }
        if (Has(SweepSubject.UnreadRows))
        {
            w.WriteNumber("unread_plugins_total", Found(SweepSubject.UnreadRows));
            w.WriteNumber("unread_plugins_named", Shown(v, SweepSubject.UnreadRows));
        }
        // The roster is the dangling subject's — Worst() reserves for it on exactly this test, and a lane without
        // that subject can never fill it, so an empty array here would claim no source plugin lost findings.
        if (dangling)
        {
            w.WriteStartArray("dangling_missing_by_source");
            for (int i = 0; i < v.Roster.Count && i < ReadSentences.SweepRosterRows; i++)
            {
                w.WriteStartObject();
                w.WriteString("plugin", v.Roster[i].Key);
                w.WriteNumber("count", v.Roster[i].Count);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            // The roster's own bound, disclosed rather than implied — the same rule the text line follows, so both
            // transports say the same thing about how complete the roster is.
            w.WriteNumber("dangling_missing_by_source_total", v.RosterTotal);
        }
        w.WriteEndObject();
    }

    /// <summary>Serialize one accounting into a scratch buffer and measure it. Used for the worst case only — the
    /// real one is written straight into the response.
    ///
    /// <para>Under the response's own writer options: measuring unindented what is then written indented gives a
    /// reserve short by the whole indentation.</para></summary>
    int MeasureJson(Values v)
    {
        // At the depth it will be written at, and as a delta. A named property needs an enclosing object; in a
        // merged document that object is `families.<token>`, two levels further in, and the writer is indented — so
        // measuring at the wrong depth is short by two spaces per level on every line.
        using var ms = new MemoryStream();
        int before = 0;
        using (var w = new Utf8JsonWriter(ms, JsonWire.WriterOptions))
        {
            w.WriteStartObject();
            for (int i = 1; i < _jsonDepth; i++) w.WriteStartObject("n");
            // The accounting is never the first member of a family object, so it pays the separator a later
            // property owes.
            w.WriteString("before", "");
            before = (int)(ms.Length + w.BytesPending);
            new CheckAccounting(v, this).WriteJson(w, v);
            // The boundary rides the measurement rather than being added as a raw char count: json escapes the
            // apostrophes in it, so its encoded length is not its string length.
            w.WriteString("boundary", _boundary);
            w.Flush();
            return (int)ms.Length - before;
        }
    }

    /// <summary>The measuring constructor: every subject declared at full width, so
    /// <see cref="WriteJson(Utf8JsonWriter, Values)"/> writes the worst case with no field missing. It is never
    /// registered against and never rendered into a response.</summary>
    /// <param name="real">the accounting being measured for. Only the subjects that accounting declared are
    /// declared here: a field a lane cannot write is not a reserve, it is a subtraction from the answer. The
    /// scripts family's two finding counts are copied rather than substituted, because they are sweep facts — the
    /// widest value they can print is the value they will print.</param>
    CheckAccounting(Values v, CheckAccounting real)
    {
        _boundary = "";
        _bySource = v.Roster;
        _cap = int.MaxValue;
        _limit = int.MaxValue;
        _jsonDepth = real._jsonDepth;
        _budgetListed = v.ByBudget;
        _scriptFindingsFound = real._scriptFindingsFound;
        _scriptFindingsListed = real._scriptFindingsListed;
        _scriptTotals = real._scriptTotals;
        // Carried across for the reason the scripts counts are: sweep facts, so the widest value they can print is
        // the value they will print. It gates fields the worst case must write, so it cannot be left out.
        _dialogue = real._dialogue;
        // Each subject at the widest number this lane can print for it: the dangling-derived worst case, or the
        // subject's own found count where that is larger. A subject whose population is neither the dangling total
        // nor the roster would otherwise print 0 here and its real count in the response.
        foreach (var s in real._found.Keys)
            if (!s.IsHistogram()) Declare(s, Math.Max(real.Found(s), Math.Max(v.ByBudget, v.RosterTotal)));
    }

    // ---- the cap floor ------------------------------------------------------------------------------

    /// <summary>The overrun notice, or null. Non-null on every response longer than the cap it was given, and it
    /// names which of the two overruns happened: a <c>max_chars</c> too small to hold the response's fixed part
    /// (<see cref="ReadSentences.SweepCapTooSmall"/>), or a body unit that ran past what the budget had left after
    /// that fixed part fit (<see cref="ReadSentences.SweepCapOvershot"/>). One sentence cannot cover both, because
    /// the fixed-part explanation is false of the second. The accounting ships either way and the overrun is named
    /// with the number that fixes it — dropping it would leave the caller with silence.
    ///
    /// <para>It is asked of the finished response's length and answers only about that; predicted from a header
    /// length plus the reserve it would be a statement about the worst case instead. Every quantity it reads is
    /// measured, none derived or kept as a running total.</para>
    ///
    /// <para><paramref name="needed"/> is what it takes to carry this response's fixed part plus the accounting.
    /// The remedy is not that length: raising the cap widens every <c>max_chars</c> this response prints back, so
    /// the growth is added in from two measured terms — how many places print it and how many digits the number
    /// gains. It is also never below the cap the caller already passed.</para></summary>
    /// <param name="contentLength">the whole response, this notice included — it is part of what the caller
    /// receives, so the cap test is asked of it.</param>
    /// <param name="needed">this response's fixed part, every term measured.</param>
    /// <param name="noticeLength">how many of <paramref name="contentLength"/>'s chars are this notice. It
    /// disappears the moment the response fits, so a remedy that counted it would tell the caller to buy room for a
    /// sentence they are paying to remove.</param>
    /// <param name="capPrintSites">how many times this response prints back the cap it was given — counted in the
    /// finished response by <see cref="CapPrintsIn"/>, never assumed from the number of accountings. Raising the cap
    /// across a digit boundary makes the response longer by one character per site, and the remedy has to name a cap
    /// that already includes that.</param>
    /// <returns>the notice, or null when the response is inside its cap.</returns>
    internal string? CapTooSmall(int contentLength, int needed, int noticeLength, int capPrintSites)
    {
        if (contentLength <= _cap) return null;
        // Which overrun this is, told apart with no added state: needed IS the fixed part's size, so a cap smaller
        // than it is one story and a body unit running past the rest is another.
        var sentence = needed > _cap ? ReadSentences.SweepCapTooSmall : ReadSentences.SweepCapOvershot;
        // The cap this response would need to stop seeing this: its length without the notice, plus what the raise
        // itself adds back. The raise widens every number this response prints the cap in, and the answer can gain
        // a digit from that widening, so the growth is taken at one more digit than the floor needs rather than
        // iterated. That bound is always sufficient — the answer is at most a few characters above the floor, so it
        // can cross at most one power of ten — and it overshoots by at most one character per printing site.
        int floor = Math.Max(needed, contentLength - noticeLength);
        int raiseTo = floor + capPrintSites * Math.Max(0, Digits(floor) + 1 - Digits(_cap));
        return string.Format(sentence, _cap, raiseTo, contentLength);
    }

    /// <summary>How many times this response prints back the cap it was given, measured in the finished response
    /// rather than derived from how many accountings it has: the text lane prints it once per subject its
    /// accounting reports as cut, so the count varies. Both spellings are searched here so "a place this response
    /// prints the cap" has one definition.</summary>
    /// <param name="content">the finished response, without the overrun notice — the notice disappears the moment
    /// the response fits, so a site inside it is not a site the raise has to pay for.</param>
    internal int CapPrintsIn(string content)
    {
        int n = 0;
        foreach (var marker in new[] { "max_chars=" + _cap, "\"max_chars\": " + _cap })
            for (int i = content.IndexOf(marker, StringComparison.Ordinal); i >= 0;
                 i = content.IndexOf(marker, i + marker.Length, StringComparison.Ordinal))
            {
                // A longer number starting with the same digits is a DIFFERENT cap, not this one printed again.
                int after = i + marker.Length;
                if (after >= content.Length || !char.IsDigit(content[after])) n++;
            }
        return n;
    }

    static int Digits(int n) => n <= 0 ? 1 : n.ToString().Length;

}
