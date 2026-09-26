using System.Text;
using System.Text.Json;
using HousecarlCore;

namespace HousecarlMcp;

/// <summary>One accounting of what a sweep response left out, shared by both transports so the text and json answers
/// cannot disagree. Every omission is a subtraction against the sweep's own totals, taken after emission stops, and a
/// lane declares the subjects it actually has, from which every sentence, field, remedy and reserve derives.
/// Contracts in docs/architecture/render-budget.md.</summary>
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
    // The scripts family's listing budget, decomposed as the dangling subject's is; both zero on the errors lane.
    readonly int _scriptFindingsFound;
    readonly int _scriptFindingsListed;
    readonly string _scriptTotals = "";   // the class-aware true totals, restated where the cut is reported
    // The dialogue family's quantities, as one value rather than loose ints; null exactly where it did not answer.
    readonly DialogueOutcome? _dialogue;
    // What this lane closes with, read from here rather than chosen at the render, so the sentence reserved and the
    // sentence written are the same one.
    readonly string _boundary;
    // The scope's types where it covered more than one, spelled with any expanded arms; null otherwise. Stated as a
    // rule wherever the listing came out short, because the sweep never tallies by type.
    readonly string? _typeScope;
    // The facegen findings ELIGIBLE for listing, which its listing budget can cut below — the classes the family
    // withholds on purpose are not in it. Zero on every other lane.
    readonly int _faceGenFound;

    /// <summary>Build the accounting for one response, declaring the errors family's own subjects: dangling entries
    /// in the LISTING lane where the walk that fills them ran, plugin sections in every listing lane, unread rows
    /// only under <c>counts_only</c>, excluded rows wherever the index excluded something.</summary>
    /// <param name="declareExcluded">whether this accounting owns the excluded-plugin roster; exactly one may.</param>
    /// <param name="jsonDepth">the depth this accounting's json lands at — 1 at a root, 3 inside a merged one.</param>
    internal CheckAccounting(ErrorCheckResult r, int cap, int jsonDepth = 1, bool declareExcluded = true)
    {
        _cap = cap;
        _jsonDepth = jsonDepth;
        _limit = r.Limit;
        _boundary = CheckSentences.SweepBoundary;
        _bySource = r.DanglingBySource ?? Array.Empty<SweepCount>();
        _typeScope = r.TypeScopeLabel;
        _budgetListed = r.Reports.Sum(p => p.Dangling.Count);
        // A refused family declares nothing: declaring subjects would assert completeness over a sweep that never ran.
        if (!r.Success) return;

        if (!r.CountsOnly && r.Classes.HasFlag(ErrorFindingClass.Dangling)) Declare(SweepSubject.DanglingEntries, r.TotalDangling);
        if (!r.CountsOnly) Declare(SweepSubject.PluginSections, r.Reports.Count);
        if (r.CountsOnly) Declare(SweepSubject.UnreadRows, r.Reports.Count);
        if (declareExcluded && r.ExcludedPlugins.Count > 0) Declare(SweepSubject.ExcludedRows, r.ExcludedPlugins.Count);
    }

    /// <summary>The scripts family's accounting — the same class, declaring that family's own subjects: record
    /// sections in the listing lane, script scan rows only under <c>counts_only</c>, and excluded rows where this
    /// accounting owns the roster.</summary>
    internal CheckAccounting(ScriptCheckResult r, int cap, int jsonDepth = 1, bool declareExcluded = true)
    {
        _cap = cap;
        _jsonDepth = jsonDepth;
        _limit = r.Limit;
        _boundary = CheckSentences.SweepScriptBoundary;
        _bySource = Array.Empty<SweepCount>();
        _typeScope = r.TypeScopeLabel;
        // Both measured off the result: the totals the sweep counted, and the findings the reports carry.
        _scriptFindingsFound = r.CountsOnly ? 0 : r.TotalUnbound + r.TotalNullObject;
        _scriptFindingsListed = r.CountsOnly ? 0 : r.Reports.Sum(x => x.Unbound.Count + x.NullObjects.Count);
        _scriptTotals = CheckSentences.ScriptTotals(r);
        if (!r.Success) return;   // see the errors ctor: a refused family declares nothing

        if (!r.CountsOnly) Declare(SweepSubject.ScriptRecords, r.Reports.Count);
        if (r.CountsOnly) Declare(SweepSubject.ScriptScanRows, r.Reports.Count(x => x.ScanError is not null));
        if (declareExcluded && r.ExcludedPlugins.Count > 0) Declare(SweepSubject.ExcludedRows, r.ExcludedPlugins.Count);
    }

    /// <summary>The facegen family's accounting — the same class again, in this family's units: one row per NPC. It
    /// declares the excluded-plugin roster like its swept siblings, reading the same index build.</summary>
    internal CheckAccounting(FaceGenCheckResult r, int cap, int jsonDepth = 1, bool declareExcluded = true)
    {
        _cap = cap;
        _jsonDepth = jsonDepth;
        _limit = r.Limit;
        _boundary = CheckSentences.SweepFaceGenBoundary;
        _bySource = Array.Empty<SweepCount>();
        _faceGenFound = r.CountsOnly ? 0 : r.ListableFound;
        if (!r.Success) return;   // see the errors ctor: a refused family declares nothing

        if (!r.CountsOnly) Declare(SweepSubject.FaceGenRows, r.Findings.Count);
        if (declareExcluded && r.ExcludedPlugins.Count > 0) Declare(SweepSubject.ExcludedRows, r.ExcludedPlugins.Count);
    }

    /// <summary>The dialogue family's accounting — the same class again, declaring the subjects a seeded family has.
    /// It declares no excluded-plugin roster; a seed it could not reach gets
    /// <see cref="SweepSubject.DialogueSeedRefusals"/> instead. Its boundary carries the standing-limits footer, so
    /// it is reserved out of <c>max_chars</c>.</summary>
    /// <param name="outcome">this family's quantities, null exactly where the family did not answer.</param>
    internal CheckAccounting(DialogueCheckResult r, DialogueOutcome? outcome, int cap, int jsonDepth = 1)
    {
        _cap = cap;
        _jsonDepth = jsonDepth;
        _limit = r.Limit;
        _bySource = Array.Empty<SweepCount>();
        // The boundary states what the seeds actually ran, not what this family can do; with nothing reached at all
        // the wide sentence is the family's standing claim and stays.
        var ran = outcome?.ChecksRun ?? DialogueChecks.None;
        bool recordLevelOnly = ran.HasFlag(DialogueChecks.RecordParity) && !ran.HasFlag(DialogueChecks.TopicGraph);
        _boundary = string.Format(
            recordLevelOnly ? CheckSentences.DialogueBoundaryRecordLevel : CheckSentences.DialogueBoundary,
            r.ConditionedInfos > 0 ? string.Format(CheckSentences.DialogueConditioned, r.ConditionedInfos) : "",
            r.ReadIncomplete ? CheckSentences.DialogueReadIncomplete : "");
        // Held whether or not this lane lists topics: seeds named and seeds reached are facts of the call.
        _dialogue = outcome;

        // A refused family declares nothing: a completeness claim over a validation that never ran would read as
        // "looked, found none".
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

    /// <summary>One unit of <paramref name="s"/> just went into the response, registered where the unit landed rather
    /// than where a section is entered. A subject this lane did not declare is ignored rather than counted.</summary>
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

    /// <summary>Property findings the scripts family's listing budget never admitted into the reports — a pure sweep
    /// fact like <see cref="OmittedByBudget"/>.</summary>
    int ScriptOmittedByBudget => Has(SweepSubject.ScriptRecords) ? _scriptFindingsFound - _scriptFindingsListed : 0;

    /// <summary>Seeds the caller named that the seed budget never let this call try; the subtraction is taken once for
    /// the whole response on <see cref="DialogueOutcome"/>.</summary>
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

    /// <summary>The chars held back from <c>max_chars</c> so this lane's text accounting line is always affordable,
    /// reserved off the same predicate the line itself uses. The boundary's room is reserved separately, because a
    /// merged response has one boundary block but one accounting per family.</summary>
    internal int TextAccountingReserve => _textReserve ??= CanStateAccounting ? Compose(Worst(escaped: false)).Length + TextWrap : 0;
    int? _textReserve;

    /// <summary>Can this lane write an accounting line at all? <see cref="TextLine"/>'s own test asked of the worst
    /// case, kept as the same expression rather than a second list of subjects.</summary>
    bool CanStateAccounting => Has(SweepSubject.DanglingEntries) || Has(SweepSubject.ScriptRecords)
                               || Has(SweepSubject.DialogueTopics) || Has(SweepSubject.FaceGenRows)
                               || Missing(Worst(escaped: false));

    /// <summary>This lane's accounting and boundary, in json characters, without the entry slack — measured by
    /// serializing the worst case, because the two encodings differ. The slack is separate, because a merged document
    /// holds one accounting per family but only ever lands one unit over its budget.</summary>
    internal int JsonAccountingReserve => _jsonReserve ??= MeasureJson(Worst(escaped: true));
    int? _jsonReserve;

    /// <summary>The slack a json response holds for the one unit that can land past the budget, since a
    /// <c>Utf8JsonWriter</c> cannot measure an object without writing it.</summary>
    internal const int JsonEntrySlack = JsonGlue;

    /// <summary>Slack over the measured worst case: the text lane needs only the newlines its blocks are wrapped in,
    /// the json lane one whole entry plus <see cref="BoundedBody"/>'s post-check. <see cref="TextWrap"/> is charged
    /// per wrapped block, because the accounting and the boundary are not both always written.</summary>
    const int TextWrap = 32;
    const int JsonGlue = 1024;

    /// <summary>The values that make the longest line this sweep could produce: every substitution at or above what a
    /// real render can reach, every optional clause present, and the roster holding the LONGEST source names rather
    /// than the largest, because only "longest" is a bound. Length is measured in the lane's own encoding, which
    /// <paramref name="escaped"/> selects.</summary>
    Values Worst(bool escaped)
    {
        int danglingFound = Found(SweepSubject.DanglingEntries);
        // The roster is the dangling subject's, so a lane without that subject reserves nothing for it.
        var longest = Has(SweepSubject.DanglingEntries)
            ? _bySource.OrderByDescending(c => escaped ? JsonEncodedText.Encode(c.Key, JsonWire.WriterOptions.Encoder).Value.Length : c.Key.Length)
                       .Take(CheckSentences.SweepRosterRows)
                       .Select(c => new SweepCount(c.Key, danglingFound))
                       .ToList()
            : new List<SweepCount>();
        // Every slot at its widest, not at zero: the emitted counts are digits in the rendered line.
        var emitted = new Dictionary<SweepSubject, int>();
        foreach (var kv in _found) emitted[kv.Key] = kv.Value;
        emitted[SweepSubject.DanglingEntries] = danglingFound;
        return new Values(Visible: danglingFound, ByBudget: danglingFound, ByCut: danglingFound, Roster: longest,
                          RosterTotal: Math.Max(_bySource.Count, longest.Count), Emitted: emitted, Worst: true);
    }

    /// <summary>What this response actually did.</summary>
    Values Real() => new(Emitted(SweepSubject.DanglingEntries), OmittedByBudget, OmittedByCut,
                         MissingBySource, MissingBySource.Count, _emitted, Worst: false);

    /// <summary>The numbers one rendering of the accounting states — a record rather than a parameter list, so the
    /// real case and the worst case go through one composer per transport.</summary>
    readonly record struct Values(int Visible, int ByBudget, int ByCut, IReadOnlyList<SweepCount> Roster,
                                  int RosterTotal, IReadOnlyDictionary<SweepSubject, int> Emitted, bool Worst);

    /// <summary>Is this subject short in this rendering? One test, used by the clause that states it, by
    /// <see cref="Missing"/> and by the remedy. <c>Found(s) &gt; 0</c> is there for the worst case, which would
    /// otherwise reserve room for a clause no rendering can write.</summary>
    bool Short(Values v, SweepSubject s)
        => Has(s) && Found(s) > 0 && (v.Worst || (v.Emitted.TryGetValue(s, out var e) ? e : 0) < Found(s));

    int Shown(Values v, SweepSubject s) => v.Emitted.TryGetValue(s, out var e) ? e : 0;

    /// <summary>Findings this family's listing BUDGET never admitted, in either family.</summary>
    bool ShortByBudget(Values v) => v.ByBudget > 0 || ScriptOmittedByBudget > 0;

    /// <summary>Findings the budget admitted and this response's max_chars then could not fit, in either family — the
    /// render cuts the same stream in the same order, so the rule below takes it and not the budget alone.</summary>
    bool ShortByCut(Values v) => v.ByCut > 0 || Short(v, SweepSubject.ScriptRecords);

    /// <summary>Does this rendering have to state the type-scope rule? Only where a multi-type scope was in force AND
    /// this family's listing came out short. The worst case takes it whenever the scope had one, so the reserve bounds
    /// the sentence it can write.</summary>
    bool TypeScopeShort(Values v)
        => _typeScope is not null && (v.Worst || ShortByBudget(v) || ShortByCut(v));

    /// <summary>The knob the type-scope rule tells the caller to raise: the one that actually cut this listing, or
    /// both where both did. The worst case takes the both-spelling, which is the longest.</summary>
    string ShortKnob(Values v)
        => v.Worst || (ShortByBudget(v) && ShortByCut(v)) ? CheckSentences.SweepKnobBoth
           : ShortByBudget(v) ? CheckSentences.SweepKnobLimit : CheckSentences.SweepKnobMaxChars;

    // ---- the text lane ------------------------------------------------------------------------------

    /// <summary>The accounting as the text transport states it, or null where there is nothing to account for:
    /// present on every response that has a listing subject, complete or not, so silence never has to mean two
    /// things. A lane with no listing states an accounting only when something is actually short.</summary>
    internal string? TextLine()
    {
        var v = Real();
        return Has(SweepSubject.DanglingEntries) || Has(SweepSubject.ScriptRecords)
               || Has(SweepSubject.DialogueTopics) || Has(SweepSubject.FaceGenRows)
               || Missing(v) ? Compose(v) : null;
    }

    string Compose(Values v)
    {
        // The opener and the closer sit outside every subject gate: they are not about any subject.
        var sb = new StringBuilder(CheckSentences.SweepAccountingLead);

        if (Has(SweepSubject.DanglingEntries))
        {
            int found = Found(SweepSubject.DanglingEntries);
            int omitted = v.ByBudget + v.ByCut;
            sb.Append(omitted > 0 || v.Worst
                ? string.Format(CheckSentences.SweepVisible, v.Visible, found)
                : string.Format(CheckSentences.SweepAllVisible, found));

            var causes = new List<string>();
            if (v.ByBudget > 0 || v.Worst) causes.Add(string.Format(CheckSentences.SweepOmittedByBudget, v.ByBudget, _limit));
            if (v.ByCut > 0 || v.Worst) causes.Add(string.Format(CheckSentences.SweepOmittedByCut, v.ByCut, _cap));
            if (causes.Count > 0) sb.Append(string.Join(",", causes)).Append('.');
        }

        // The scripts family's lead and budget clause, both against the sweep's own totals.
        if (Has(SweepSubject.ScriptRecords))
        {
            sb.Append(Short(v, SweepSubject.ScriptRecords)
                ? string.Format(CheckSentences.SweepScriptVisible, Shown(v, SweepSubject.ScriptRecords),
                                Found(SweepSubject.ScriptRecords))
                : string.Format(CheckSentences.SweepScriptAllVisible, Found(SweepSubject.ScriptRecords)));
            if (ScriptOmittedByBudget > 0 || v.Worst)
                sb.Append(string.Format(CheckSentences.SweepScriptFindings, _scriptFindingsListed,
                                        _scriptFindingsFound, _limit, _scriptTotals));
        }
        // The same two-part shape in a seeded family's units, as separate clauses: a topic that did not fit is a
        // rendering fact, a seed the budget never reached is a scope fact, and they name different knobs.
        if (Has(SweepSubject.DialogueTopics))
        {
            sb.Append(Short(v, SweepSubject.DialogueTopics)
                ? string.Format(CheckSentences.SweepDialogueVisible, Shown(v, SweepSubject.DialogueTopics),
                                Found(SweepSubject.DialogueTopics))
                : string.Format(CheckSentences.SweepDialogueAllVisible, Found(SweepSubject.DialogueTopics)));
        }
        // Reached against named, in the outcome's own words — the same two quantities the scope sentence states.
        if (_dialogue is { } dlg && (DialogueSeedsUnreached > 0 || (v.Worst && Has(SweepSubject.DialogueSeedRefusals))))
            sb.Append(string.Format(CheckSentences.SweepDialogueSeedsCut, dlg.SeedsReached, dlg.SeedsNamed,
                                    dlg.SeedsNotReached, _limit));
        // The totals, restated wherever this family's listing is short, because they are never capped.
        if (_dialogue is { } dlgT && Has(SweepSubject.DialogueTopics)
            && (Short(v, SweepSubject.DialogueTopics) || DialogueSeedsUnreached > 0 || v.Worst))
            sb.Append(string.Format(CheckSentences.SweepDialogueProblems, dlgT.FindingsFound,
                                    Found(SweepSubject.DialogueTopics)));
        if (Short(v, SweepSubject.DialogueSeeds))
            sb.Append(string.Format(CheckSentences.SweepDialogueSeedSections, Shown(v, SweepSubject.DialogueSeeds),
                                    Found(SweepSubject.DialogueSeeds)));
        if (Short(v, SweepSubject.DialogueSeedRefusals))
            sb.Append(string.Format(CheckSentences.SweepDialogueRefusalsCut, Shown(v, SweepSubject.DialogueSeedRefusals),
                                    Found(SweepSubject.DialogueSeedRefusals)));

        // The facegen family's own two-part shape: what this response carries against what the sweep found, then the
        // listing budget's share of what is absent. The found total is never capped.
        if (Has(SweepSubject.FaceGenRows))
        {
            sb.Append(Short(v, SweepSubject.FaceGenRows)
                ? string.Format(CheckSentences.SweepFaceGenVisible, Shown(v, SweepSubject.FaceGenRows),
                                Found(SweepSubject.FaceGenRows))
                : string.Format(CheckSentences.SweepFaceGenAllVisible, Found(SweepSubject.FaceGenRows)));
            if (_faceGenFound > Found(SweepSubject.FaceGenRows) || v.Worst)
                sb.Append(string.Format(CheckSentences.SweepFaceGenFindings,
                                        Found(SweepSubject.FaceGenRows), _faceGenFound, _limit));
        }

        // The scripts family's counts_only honesty layer: the plugins whose record enumeration faulted.
        if (Short(v, SweepSubject.ScriptScanRows))
            sb.Append(string.Format(CheckSentences.SweepUnreadCut, Shown(v, SweepSubject.ScriptScanRows),
                                    Found(SweepSubject.ScriptScanRows)));

        // One clause per short subject, computed from the subject it names.
        if (Short(v, SweepSubject.PluginSections))
            sb.Append(string.Format(CheckSentences.SweepSections, Shown(v, SweepSubject.PluginSections),
                                    Found(SweepSubject.PluginSections)));
        // The two honesty-layer rosters: their rows are what houseCARL could NOT read, so a silent cut there hides
        // the boundary of the answer.
        if (Short(v, SweepSubject.ExcludedRows))
            sb.Append(string.Format(CheckSentences.SweepExcludedCut, Shown(v, SweepSubject.ExcludedRows),
                                    Found(SweepSubject.ExcludedRows)));
        if (Short(v, SweepSubject.UnreadRows))
            sb.Append(string.Format(CheckSentences.SweepUnreadCut, Shown(v, SweepSubject.UnreadRows),
                                    Found(SweepSubject.UnreadRows)));

        // The type-scope rule, wherever this listing came out short under a scope covering more than one type, naming
        // which knob cut it. Stated as a rule, not a count, because the sweep never tallies by type.
        if (TypeScopeShort(v)) sb.Append(string.Format(CheckSentences.SweepTypeScopeRule, _typeScope, ShortKnob(v)));

        if (v.Roster.Count > 0)
        {
            sb.Append(CheckSentences.SweepRosterLead);
            for (int i = 0; i < v.Roster.Count && i < CheckSentences.SweepRosterRows; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(v.Roster[i].Key).Append(" (").Append(v.Roster[i].Count).Append(')');
            }
            if (v.RosterTotal > CheckSentences.SweepRosterRows || v.Worst)
                sb.Append(string.Format(CheckSentences.SweepRosterCut, CheckSentences.SweepRosterRows, v.RosterTotal));
            sb.Append('.');
            // The rule belongs to the roster — it explains what the roster is for, so it is stated where one exists.
            sb.Append(CheckSentences.SweepNoSectionRule);
        }

        if (Missing(v))
        {
            if (v.ByBudget > 0 || ScriptOmittedByBudget > 0 || DialogueSeedsUnreached > 0 || v.Worst)
                sb.Append(CheckSentences.SweepRemedyLimit);
            // max_chars is the knob for every subject except the listing budget's own share.
            if (v.ByCut > 0 || v.Worst || Short(v, SweepSubject.PluginSections)
                || Short(v, SweepSubject.ExcludedRows) || Short(v, SweepSubject.UnreadRows)
                || Short(v, SweepSubject.ScriptRecords) || Short(v, SweepSubject.ScriptScanRows)
                || Short(v, SweepSubject.DialogueSeeds) || Short(v, SweepSubject.DialogueTopics)
                || Short(v, SweepSubject.DialogueSeedRefusals))
                sb.Append(CheckSentences.SweepRemedyMaxChars);
            if (v.Roster.Count > 0) sb.Append(CheckSentences.SweepRemedyScope).Append(CheckSentences.SweepRemedyCountsOnly);
        }
        sb.Append(CheckSentences.SweepClose);
        return sb.ToString();
    }

    /// <summary>Is anything at all absent from this response? One test over every declared subject, dropped sections
    /// included, so the remedy and the clauses above it cannot disagree. It does not take <c>v.Worst</c> as an answer
    /// on its own, and the worst case still dominates the real one term by term.</summary>
    bool Missing(Values v)
        => v.ByBudget + v.ByCut > 0 || ScriptOmittedByBudget > 0 || DialogueSeedsUnreached > 0
           || Short(v, SweepSubject.PluginSections) || Short(v, SweepSubject.ExcludedRows)
           || Short(v, SweepSubject.UnreadRows)
           || Short(v, SweepSubject.ScriptRecords) || Short(v, SweepSubject.ScriptScanRows)
           || Short(v, SweepSubject.DialogueSeeds) || Short(v, SweepSubject.DialogueTopics)
           || Short(v, SweepSubject.DialogueSeedRefusals);

    // ---- the json lane ------------------------------------------------------------------------------

    /// <summary>The accounting as json states it — the same numbers in the transport's own terms, with the required
    /// in-band fields written here too, because <see cref="JsonAccountingReserve"/> measures this method.</summary>
    internal void WriteJson(Utf8JsonWriter w) => WriteJson(w, Real());

    void WriteJson(Utf8JsonWriter w, Values v)
    {
        // A field named for a subject is present exactly where that subject is, never a zero standing in for it.
        bool sections = Has(SweepSubject.PluginSections);
        bool dangling = Has(SweepSubject.DanglingEntries);
        // The scripts family's listing subject.
        bool scriptSections = Has(SweepSubject.ScriptRecords);
        // The dialogue family's listing subject; its "capped" is the SEED budget, a different quantity from the
        // sibling families' finding budgets even though all three are spelled limit=.
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
            // No topic total here: the family head already writes it as `topics_found`. The sibling families' totals
            // are not in their heads, so theirs stay.
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
        // What this response DID with the seeds, not how many there were, which the family head already states.
        if (_dialogue is not null)
        {
            w.WriteNumber("seeds_not_reached_by_budget", DialogueSeedsUnreached);
            w.WriteNumber("limit", _limit);
        }
        if (dialogueTopics) w.WriteNumber("dialogue_topics_rendered", Shown(v, SweepSubject.DialogueTopics));
        // In both lanes: a seed nobody could reach bounds the answer rather than sitting inside it.
        if (Has(SweepSubject.DialogueSeedRefusals))
            w.WriteNumber("seeds_unreachable_named", Shown(v, SweepSubject.DialogueSeedRefusals));
        // The text lane's type-scope rule, in this transport's terms and under the same test.
        if (TypeScopeShort(v))
        {
            w.WriteString("listing_short_across_types", _typeScope);
            w.WriteString("listing_short_by", ShortKnob(v));
        }
        // A fact about the CALL rather than about any subject, so every lane writes it: the cap it was given.
        w.WriteNumber("max_chars", _cap);
        // The same rule as at the head of this method, applied to the three blocks below: a seeded family has no
        // plugin scope and no dangling roster.
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
        // The roster is the dangling subject's — Worst() reserves for it on exactly this test.
        if (dangling)
        {
            w.WriteStartArray("dangling_missing_by_source");
            for (int i = 0; i < v.Roster.Count && i < CheckSentences.SweepRosterRows; i++)
            {
                w.WriteStartObject();
                w.WriteString("plugin", v.Roster[i].Key);
                w.WriteNumber("count", v.Roster[i].Count);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            // The roster's own bound, disclosed rather than implied — the same rule the text line follows.
            w.WriteNumber("dangling_missing_by_source_total", v.RosterTotal);
        }
        w.WriteEndObject();
    }

    /// <summary>Serialize one accounting into a scratch buffer and measure it, under the response's own writer
    /// options. Used for the worst case only — the real one is written straight into the response.</summary>
    int MeasureJson(Values v)
    {
        // At the depth it will be written at, and as a delta: the writer is indented, so measuring at the wrong
        // depth is short by two spaces per level on every line.
        using var ms = new CharCountedStream();
        int before = 0;
        using (var w = new Utf8JsonWriter(ms, JsonWire.WriterOptions))
        {
            w.WriteStartObject();
            for (int i = 1; i < _jsonDepth; i++) w.WriteStartObject("n");
            // The accounting is never the first member of a family object, so it pays a later property's separator.
            w.WriteString("before", "");
            w.Flush();
            before = ms.Chars;
            new CheckAccounting(v, this).WriteJson(w, v);
            // The boundary rides the measurement rather than a raw char count, because json escapes its apostrophes.
            w.WriteString("boundary", _boundary);
            w.Flush();
            return ms.Chars - before;
        }
    }

    /// <summary>The measuring constructor: every subject declared at full width, so
    /// <see cref="WriteJson(Utf8JsonWriter, Values)"/> writes the worst case with no field missing. It is never
    /// registered against and never rendered into a response.</summary>
    /// <param name="real">the accounting being measured for; only the subjects it declared are declared here. The
    /// scripts family's two finding counts are copied rather than substituted, because they are sweep facts.</param>
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
        // Carried across for the reason the scripts counts are, and it gates fields the worst case must write.
        _dialogue = real._dialogue;
        // Each subject at the widest number this lane can print for it: the dangling-derived worst case, or the
        // subject's own found count where that is larger.
        foreach (var s in real._found.Keys)
            if (!s.IsHistogram()) Declare(s, Math.Max(real.Found(s), Math.Max(v.ByBudget, v.RosterTotal)));
    }

    // ---- the cap floor ------------------------------------------------------------------------------

    /// <summary>The overrun notice, or null. Non-null on every response longer than the cap it was given, naming
    /// which of the two overruns happened — a <c>max_chars</c> too small for the fixed part, or a body unit past what
    /// the budget had left. Every quantity it reads is measured off the finished response, the notice included, and
    /// <paramref name="capPrintSites"/> is counted there by <see cref="CapPrintsIn"/> rather than assumed. Contract
    /// in docs/architecture/render-budget.md.</summary>
    internal string? CapTooSmall(int contentLength, int needed, int noticeLength, int capPrintSites)
    {
        if (contentLength <= _cap) return null;
        // Which overrun this is, told apart with no added state: needed IS the fixed part's size.
        var sentence = needed > _cap ? CheckSentences.SweepCapTooSmall : CheckSentences.SweepCapOvershot;
        // The cap this response would need to stop seeing this: its length without the notice, plus what the raise
        // itself adds back, taken at one more digit than the floor needs rather than iterated.
        int floor = Math.Max(needed, contentLength - noticeLength);
        int raiseTo = floor + capPrintSites * Math.Max(0, Digits(floor) + 1 - Digits(_cap));
        return string.Format(sentence, _cap, raiseTo, contentLength);
    }

    /// <summary>How many times this response prints back the cap it was given, measured in the finished response
    /// rather than derived from how many accountings it has. Both spellings are searched here, so "a place this
    /// response prints the cap" has one definition.</summary>
    /// <param name="content">the finished response, without the overrun notice.</param>
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
