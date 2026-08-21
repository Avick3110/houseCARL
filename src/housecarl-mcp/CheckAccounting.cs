using System.Text;
using System.Text.Json;
using HousecarlCore;

namespace HousecarlMcp;

/// <summary>
/// ONE ACCOUNTING for the integrity sweep's response, consumed by BOTH transports (#337's one-source-per-sentence,
/// applied to the numbers underneath the prose rather than only to the prose).
///
/// <para><b>What it replaces, and why the replacement is structural.</b> The response used to describe its own
/// omissions from TWO independent counters in two different units: core's listing budget (<c>limit=</c>, counted
/// while the sweep ran) and the render's own cut (<c>max_chars</c>, counted at a break point). Neither could see
/// the other, so neither could answer the question a caller actually has — how many of these findings can I see —
/// and on the live ARR order at plain defaults the two together said "3996 not listed" and "554 of the 1000
/// budget-listed appear above" while the answer was 554 of 4996. Every omission claim here is a SUBTRACTION against
/// the sweep's own totals, taken after emission stops, so there is one number and one source for it. The 2026-08-18
/// layer rule survives as this class's internal discipline: the causes are still named separately, but they are
/// decomposed out of one computation instead of raced by two.</para>
///
/// <para><b>The DECLARED SUBJECT set, and the boolean it replaces.</b> The first cut of this class carried one
/// <c>_listing</c> flag standing for two different lane facts — "did the dangling walk run" and "does this mode
/// build a per-plugin listing" — and a section count read unconditionally off <c>Reports</c>, which means different
/// things per lane. Six distinct wrong sentences came out of that in two review rounds, three of them created by
/// the folds for the other three: a <c>findings=["missing_masters"]</c> json response that reported no sections at
/// all, a <c>counts_only</c> response claiming "0 of 3 plugin section(s) were rendered" over a response that
/// dropped nothing, an opener inside the gate and its closer outside it. So the accounting is now CONSTRUCTED with
/// the subjects the lane actually has (<see cref="SweepSubject"/>), each carrying what the sweep FOUND and what the
/// render EMITTED. Every sentence, every json field, the remedy and the reserve derive from that set. A lane
/// without sections cannot claim about them, and a lane with them cannot fail to — by construction rather than by
/// each clause remembering its own gate.</para>
///
/// <para><b>How #361 dies here rather than being patched.</b> Two invariants, neither of them a code path that has
/// to remember to run:
/// <list type="number">
/// <item>Being missing is MEASURED AT THE TOTAL, never reported at an exit. There is no flag set where a loop
/// breaks, so there is no "the cut landed somewhere that never set the flag" — the silent last-section cut, in both
/// transports, is unrepresentable rather than fixed.</item>
/// <item>The accounting and the boundary footer are RESERVED out of the caller's <c>max_chars</c> before the body
/// renders, never appended past it. This is <see cref="ReadSentences.ClauseReserve"/>'s construction, which #342's
/// review arrived at over the same overshoot one tool over: a 2000-char batch returning ~3100, with the overrun
/// invisible to the <c>truncated</c> flag the auto-spill trigger reads.</item>
/// </list></para>
///
/// <para><b>Deliberately findings-family-agnostic, and deliberately no further.</b> Its subjects are lane facts —
/// entries, sections, roster rows — which carry to any findings family without knowing what a family is. It takes
/// no taxonomy parameter and holds no family enum: the merged <c>check</c> surface (SPEC §6.1) is a separate PR
/// with its own design, and building its machinery here on speculation is what CLAUDE.md §8 names.</para>
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
    // The scripts family's own listing budget, decomposed exactly as the dangling subject's is: the population the
    // sweep found, and the subset the budget admitted into the reports. Both zero on the errors lane, whose
    // findings are the dangling entries above.
    readonly int _scriptFindingsFound;
    readonly int _scriptFindingsListed;
    readonly string _scriptTotals = "";   // the class-aware true totals, restated where the cut is reported
    // The dialogue family's own three quantities: what the validation FOUND (never capped), how many seeds the
    // caller named, and how many the seed budget let this call actually try. The last two differ exactly where the
    // budget stopped the loop, and that difference is an absence no other subject accounts for.
    readonly int _dialogueProblems;
    readonly int _dialogueSeedsNamed;
    readonly int _dialogueSeedsTried;
    // What THIS lane closes with. The two families state different boundaries, and TextReserve holds room for the
    // one that will actually be written — a reserve sized off the other family's sentence is room for a sentence
    // this lane cannot write, which is the defect CanStateAccounting exists to name.
    readonly string _boundary;

    /// <summary>Build the accounting for one response, declaring the subjects this lane has.
    ///
    /// <para>Each declaration is a lane fact stated once, here, instead of a gate repeated at every clause:</para>
    /// <list type="bullet">
    /// <item><b>Dangling entries</b> — only where a per-plugin listing is built AND the walk that fills it ran.
    /// Under <c>counts_only</c> nothing is listed BY DESIGN, and "the budget omitted everything" would report a
    /// mode working correctly as a failure.</item>
    /// <item><b>Plugin sections</b> — in EVERY listing lane, entries or not. With <c>findings=["missing_masters"]</c>
    /// the sweep lists no dangling refs at all, and the section loop still cuts; gated on the listing flag those
    /// responses said nothing in text and wrote no <c>rendered</c>/<c>truncated</c> in json.</item>
    /// <item><b>Unread rows</b> — only under <c>counts_only</c>, where <c>Reports</c> carries the honesty layer
    /// rather than findings. In the listing lane those same plugins ARE the sections, so the two subjects exist in
    /// different lanes and can never double-count a row.</item>
    /// <item><b>Excluded rows</b> — wherever the index actually excluded something.</item>
    /// </list></summary>
    /// <param name="declareExcluded">whether THIS accounting owns the excluded-plugin roster. The roster is a
    /// SCOPE fact — which plugins the index could not parse — emitted once per response however many families ran,
    /// so exactly one accounting may declare its rows. Two that declared it would each subtract the same rows from
    /// the same total and the response would state the cut twice, which is the double count that keeping one
    /// subject per lane fact exists to make unrepresentable.</param>
    internal CheckAccounting(ErrorCheckResult r, int cap, bool declareExcluded = true)
    {
        _cap = cap;
        _limit = r.Limit;
        _boundary = ReadSentences.SweepBoundary;
        _bySource = r.DanglingBySource ?? Array.Empty<SweepCount>();
        _budgetListed = r.Reports.Sum(p => p.Dangling.Count);

        if (!r.CountsOnly && r.Classes.HasFlag(ErrorFindingClass.Dangling)) Declare(SweepSubject.DanglingEntries, r.TotalDangling);
        if (!r.CountsOnly) Declare(SweepSubject.PluginSections, r.Reports.Count);
        if (r.CountsOnly) Declare(SweepSubject.UnreadRows, r.Reports.Count);
        if (declareExcluded && r.ExcludedPlugins.Count > 0) Declare(SweepSubject.ExcludedRows, r.ExcludedPlugins.Count);
    }

    /// <summary>The scripts family's accounting — the same class, declaring that family's own subjects.
    ///
    /// <para><b>Why the scripts family gets one at all.</b> Its listing used to test <c>sb.Length &gt;= cap</c>
    /// inline, write its own truncation marker and keep no accounting, so the two things a response owes about
    /// what it dropped were neither computed nor bounded: on the live ARR 2.0 order at plain defaults
    /// <c>validate_scripts</c> returned <b>80,673 chars against its own 80,000 cap</b>, with the marker and the
    /// boundary footer appended unconditionally AFTER the test. Rendering through the reserved fixed part is what
    /// closes that by construction rather than by a second length check.</para>
    ///
    /// <para>The subjects, and why each is declared where it is:</para>
    /// <list type="bullet">
    /// <item><b>Record sections</b> — in the LISTING lane, findings or not. Under <c>counts_only</c> nothing is
    /// listed by design.</item>
    /// <item><b>Script scan rows</b> — only under <c>counts_only</c>, where <c>Reports</c> carries the honesty
    /// layer alone. In the listing lane those same entries ARE sections, so the two subjects live in different
    /// lanes and cannot double-count a row.</item>
    /// <item><b>Excluded rows</b> — wherever the index excluded something AND this accounting owns the roster.</item>
    /// </list></summary>
    internal CheckAccounting(ScriptCheckResult r, int cap, bool declareExcluded = true)
    {
        _cap = cap;
        _limit = r.Limit;
        _boundary = ReadSentences.SweepScriptBoundary;
        _bySource = Array.Empty<SweepCount>();
        // The dangling subject's decomposition, in the scripts family's own population: what the sweep found, and
        // what its finding budget admitted into the reports. Both MEASURED off the result — the totals the sweep
        // counted regardless of the cap, and the findings the reports actually carry.
        _scriptFindingsFound = r.CountsOnly ? 0 : r.TotalUnbound + r.TotalNullObject;
        _scriptFindingsListed = r.CountsOnly ? 0 : r.Reports.Sum(x => x.Unbound.Count + x.NullObjects.Count);
        _scriptTotals = ReadSentences.ScriptTotals(r);

        if (!r.CountsOnly) Declare(SweepSubject.ScriptRecords, r.Reports.Count);
        if (r.CountsOnly) Declare(SweepSubject.ScriptScanRows, r.Reports.Count(x => x.ScanError is not null));
        if (declareExcluded && r.ExcludedPlugins.Count > 0) Declare(SweepSubject.ExcludedRows, r.ExcludedPlugins.Count);
    }

    /// <summary>The DIALOGUE family's accounting — the same class again, declaring the subjects a SEEDED family has.
    ///
    /// <para><b>It declares no excluded-plugin roster, and that is the answer rather than an omission.</b> The
    /// roster is which plugins the INDEX could not parse; a dialogue validation does not produce one, and its
    /// ancestor never reported one either. What this family cannot reach is a SEED, which is a different fact about
    /// a different thing — so it gets its own subject (<see cref="SweepSubject.DialogueSeedRefusals"/>) inside this
    /// family's own section, and the response-level roster stays declared by exactly one accounting.</para>
    ///
    /// <para>The BOUNDARY carries the standing-limits footer <c>validate_dialogue</c> always printed. Making it the
    /// boundary is what makes it unrefusable: it is reserved out of <c>max_chars</c> before the body renders, so the
    /// sentence saying a clean structural pass is not "this will play" cannot be dropped by the pressure that cut
    /// the findings it qualifies.</para></summary>
    internal CheckAccounting(DialogueCheckResult r, int cap)
    {
        _cap = cap;
        _limit = r.Limit;
        _bySource = Array.Empty<SweepCount>();
        _boundary = string.Format(ReadSentences.DialogueBoundary,
            r.ConditionedInfos > 0 ? string.Format(ReadSentences.DialogueConditioned, r.ConditionedInfos) : "",
            r.ReadIncomplete ? ReadSentences.DialogueReadIncomplete : "");
        _dialogueProblems = r.ProblemsFound;
        _dialogueSeedsNamed = r.SeedsNamed;
        _dialogueSeedsTried = r.Seeds.Count;

        // A REFUSED family declares NOTHING. Declaring its subjects anyway made the accounting state "every one of
        // the 0 topic(s) these seeds own is listed" under a section whose whole content is a cost-refusal — a
        // completeness claim over a validation that never ran, which reads exactly like "looked, found none" (Q3).
        // The refusal is that section's whole answer; a lane with nothing declared has no completeness to assert.
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
    /// <see cref="BoundedBody.Emit"/> where the unit LANDED, never where a section is entered: a section total would
    /// claim entries for a section the cut left half-written, which is the class of false number this construction
    /// exists to make unrepresentable.
    ///
    /// <para>A subject this lane did not declare is IGNORED rather than counted. That is what lets the histogram
    /// rows share the one bounded-emission path without acquiring an accounting sentence of their own — the
    /// histogram already discloses its own cut in both transports.</para></summary>
    internal void Emitted(SweepSubject s, string? source = null)
    {
        if (!_found.ContainsKey(s)) return;
        _emitted[s]++;
        if (s == SweepSubject.DanglingEntries && source is not null)
            _bySourceEmitted[source] = (_bySourceEmitted.TryGetValue(source, out var had) ? had : 0) + 1;
    }

    // ---- derived ------------------------------------------------------------------------------------

    /// <summary>Refs the listing budget never admitted. A pure SWEEP fact, so it is readable before the body renders
    /// — which is what lets the baseline block's phase-order sentence consult it without waiting for emission.
    /// </summary>
    internal int OmittedByBudget => Has(SweepSubject.DanglingEntries) ? Found(SweepSubject.DanglingEntries) - _budgetListed : 0;

    /// <summary>Refs the budget admitted and this response then could not fit. The other half of the dangling
    /// subject's omission BY CONSTRUCTION: both are subtractions off the same total, so the two causes sum to it
    /// exactly rather than by two counters happening to agree.</summary>
    internal int OmittedByCut => Has(SweepSubject.DanglingEntries) ? _budgetListed - Emitted(SweepSubject.DanglingEntries) : 0;

    /// <summary>Property findings the scripts family's listing budget never admitted into the reports. A pure SWEEP
    /// fact, like <see cref="OmittedByBudget"/> — both terms are counted while the sweep runs, so it is readable
    /// before the body renders and is the same number in the worst case as in the real one.</summary>
    int ScriptOmittedByBudget => Has(SweepSubject.ScriptRecords) ? _scriptFindingsFound - _scriptFindingsListed : 0;

    /// <summary>Seeds the caller named that the seed budget never let this call try. A pure SWEEP fact like the two
    /// above — counted before anything renders, and the same number in the worst case as in the real one.</summary>
    int DialogueSeedsUnreached => Math.Max(0, _dialogueSeedsNamed - _dialogueSeedsTried);

    /// <summary>WHICH source plugins are missing entries from THIS response, largest first. Computed against what was
    /// emitted, so it covers both causes at once: under the two-layer split, a plugin whose entries the budget listed
    /// and the cut then dropped appeared in neither sentence.</summary>
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

    /// <summary>The chars held back from <c>max_chars</c> so the text accounting and the boundary footer are always
    /// affordable. Two terms, and only the boundary's is unconditional: the boundary is written on every response,
    /// the accounting line only where this lane can write one.
    ///
    /// <para><b>Reserved per LANE, off the same predicate the line itself uses.</b> <see cref="TextLine"/> returns
    /// null unless the lane declared the dangling subject or something it declared is short, so in the plain
    /// <c>counts_only</c> lane — no listing, nothing unparseable, nothing unread — no value of anything can make it
    /// non-null. Reserved unconditionally, that lane held ~154 chars of worst-case accounting plus its wrap for a
    /// sentence it is structurally unable to write: dead body budget in exactly the lane #392 is about, where every
    /// held char is a histogram row the caller does not see. Measured on a 74/180-row fixture at
    /// <c>max_chars=1200</c>, the response came back 315 chars under its own cap with both axes at zero rows.</para></summary>
    internal int TextReserve => TextAccountingReserve + TextBoundaryReserve;
    int? _textReserve;

    /// <summary>The accounting LINE's own room, without the boundary's. Separate because a merged response has one
    /// boundary block and one accounting PER FAMILY: summing whole TextReserves would reserve the boundary once per
    /// family, and room held for a sentence written once is a subtraction from the answer.</summary>
    internal int TextAccountingReserve => _textReserve ??= CanStateAccounting ? Compose(Worst(escaped: false)).Length + TextWrap : 0;

    /// <summary>The room this lane's boundary needs, label and wrap included.</summary>
    internal int TextBoundaryReserve => _boundary.Length + ReadSentences.SweepBoundaryLabel.Length + TextWrap;

    /// <summary>Can this lane write an accounting line at ALL? Literally <see cref="TextLine"/>'s own test, asked
    /// of the WORST case instead of the real one — so it is true wherever any rendering of this lane could produce
    /// a line, and false only where none could. Written as the same expression rather than as a second list of
    /// subjects, because the two disagreeing is the whole defect: a reserve is room for a specific sentence, and
    /// room held for a sentence this lane cannot write is not a reserve, it is a subtraction from the
    /// answer.</summary>
    bool CanStateAccounting => Has(SweepSubject.DanglingEntries) || Has(SweepSubject.ScriptRecords)
                               || Has(SweepSubject.DialogueTopics)
                               || Missing(Worst(escaped: false));

    /// <summary>The json lane's own reserve. Measured by SERIALIZING the worst case, not by estimating it off the
    /// text line: the two encodings differ in escaping and syntax, and a reserve that is an estimate is a reserve
    /// that is occasionally wrong in the direction that matters.</summary>
    internal int JsonReserve => JsonAccountingReserve + JsonGlue;

    /// <summary>This lane's accounting + boundary, in json bytes, WITHOUT the entry slack. Separate for the same
    /// reason <see cref="TextAccountingReserve"/> is: a merged document holds one accounting per family but only
    /// ever lands ONE unit over its budget, because the body stops the moment a unit crosses — so the slack is the
    /// response's, not each family's.</summary>
    internal int JsonAccountingReserve => _jsonReserve ??= MeasureJson(Worst(escaped: true));
    int? _jsonReserve;

    /// <summary>The slack a json response holds for the one unit that can land past the budget. A
    /// <c>Utf8JsonWriter</c> cannot measure an object without writing it, so a unit whose cost the site left at
    /// zero is tested before the write and lands over; this covers one whole entry.</summary>
    internal const int JsonEntrySlack = JsonGlue;

    /// <summary>Slack over the measured worst case, and the two lanes need very different amounts of it.
    ///
    /// <para>The TEXT lane composes each unit and tests <c>length + cost</c> before appending, so nothing it writes
    /// can exceed the budget it was tested against. All it needs is the handful of newlines the accounting and
    /// boundary are wrapped in. The JSON lane cannot do that for every unit — a <c>Utf8JsonWriter</c> has no way to
    /// measure an object without writing it — so its per-entry test is taken before the write and the last entry
    /// lands over. That slack has to cover one whole entry: two FormID strings, a type name and an EditorID. It is
    /// also what absorbs <see cref="BoundedBody"/>'s post-check, which stops the body the moment a unit crosses the
    /// budget rather than pretending it cannot happen.</para>
    ///
    /// <para>One number for both cost the text lane a kilobyte of listing at every cap, which at small caps was the
    /// whole listing. A reserve slightly too large costs characters; one slightly too small is #361. The cap sweep
    /// in check-errors-guard is what holds both honest — not either number's plausibility.</para>
    ///
    /// <para><see cref="TextWrap"/> is the text lane's glue charged PER WRAPPED BLOCK rather than once for both,
    /// because the two blocks are not both always written. Each is two newlines in the render; 32 is headroom, and
    /// twice 32 is the 64 this was before the split, so a lane writing both reserves exactly what it always
    /// did.</para></summary>
    const int TextWrap = 32;
    const int JsonGlue = 1024;

    /// <summary>The values that make the longest line this sweep could produce. Every substitution is at or above
    /// what a real render can reach: the counts are the totals themselves, so their digit widths bound every real
    /// count; every optional clause is present; and the roster holds the LONGEST source names rather than the
    /// largest, because a partly-listed response can promote a long-named small source into the roster that a
    /// fully-omitted one would have pushed out — "largest" is not a bound and "longest" is.
    ///
    /// <para>Length is measured in the LANE'S OWN encoding, which is why <paramref name="escaped"/> exists. Escaping
    /// only ever adds characters, so a "longest by either spelling" rule is just the escaped one wearing a hat: it
    /// ranks a short non-ASCII name above a much longer ASCII one, and a text reserve sized from that sample is
    /// short by the difference. The two lanes disagree about which names are longest, so each asks its own
    /// question.</para>
    ///
    /// <para><b>Which lane an arm can see this in, said rather than left to be discovered.</b> The TEXT half is
    /// pinned: ranked by the escaped spelling instead, the escaped-names fixture overruns the text cap by 300-plus
    /// characters at eight caps in the sweep. The JSON half is not — sabotaged to rank by the plain spelling, every
    /// arm in both guards stays green, because the json reserve carries a kilobyte of slack sized for one whole
    /// entry and it absorbs the difference at every cap a fixture can reach. Asking each lane its own question is
    /// kept anyway: a reserve that happens to be covered by slack meant for something else is not a reserve, and
    /// that posture is what produced four unbounded write sites in a row.</para></summary>
    Values Worst(bool escaped)
    {
        int danglingFound = Found(SweepSubject.DanglingEntries);
        // The roster is the dangling subject's, so a lane without that subject reserves nothing for it. The
        // by-source tally is collected on EVERY sweep, so a worst case built off it unconditionally held ~700 chars
        // back from a counts_only response for a roster that lane can never emit — at max_chars=1500 that was the
        // whole histogram.
        var longest = Has(SweepSubject.DanglingEntries)
            ? _bySource.OrderByDescending(c => escaped ? JsonEncodedText.Encode(c.Key).Value.Length : c.Key.Length)
                       .Take(ReadSentences.SweepRosterRows)
                       .Select(c => new SweepCount(c.Key, danglingFound))
                       .ToList()
            : new List<SweepCount>();
        // Every slot at its widest, not at zero. The counts are digits in the rendered line, and a real response's
        // emitted counts are wider than 0 — so a worst case that passed 0 for them was bounded by the slack rather
        // than by construction, which is not what its own summary claimed.
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
    /// real case and the worst case go through ONE composer per transport — a second formatter would be a second
    /// spelling, and a reserve computed off a spelling that has since drifted is a reserve that silently stops
    /// bounding anything.</summary>
    readonly record struct Values(int Visible, int ByBudget, int ByCut, IReadOnlyList<SweepCount> Roster,
                                  int RosterTotal, IReadOnlyDictionary<SweepSubject, int> Emitted, bool Worst);

    /// <summary>Is this subject short in this rendering? ONE test, used by the clause that states it, by
    /// <see cref="Missing"/>, and by the remedy — so the three cannot disagree about the same subject.
    ///
    /// <para><c>Found(s) &gt; 0</c> is not a shortcut for the real case, where it is already implied — nothing can
    /// be emitted short of zero. It is what makes the WORST case honest: a lane declares
    /// <see cref="SweepSubject.UnreadRows"/> on every <c>counts_only</c> sweep, empty or not, and with
    /// <c>v.Worst</c> alone a declared-but-empty subject reserved room for a clause no rendering of it can
    /// write.</para></summary>
    bool Short(Values v, SweepSubject s)
        => Has(s) && Found(s) > 0 && (v.Worst || (v.Emitted.TryGetValue(s, out var e) ? e : 0) < Found(s));

    int Shown(Values v, SweepSubject s) => v.Emitted.TryGetValue(s, out var e) ? e : 0;

    // ---- the text lane ------------------------------------------------------------------------------

    /// <summary>The accounting as the text transport states it, or null where there is nothing at all to account
    /// for. Present on EVERY response that has a listing subject, complete or not: silence used to mean both "this
    /// response carries everything" and "#361", and those two must never read alike. A lane with no listing has no
    /// completeness to assert, so it states an accounting only when something is actually short.</summary>
    internal string? TextLine()
    {
        var v = Real();
        return Has(SweepSubject.DanglingEntries) || Has(SweepSubject.ScriptRecords)
               || Has(SweepSubject.DialogueTopics) || Missing(v) ? Compose(v) : null;
    }

    string Compose(Values v)
    {
        // The opener and the closer sit OUTSIDE every subject gate, because they are not about any subject. The
        // opener used to be baked into the two listing leads, so a lane with no listing emitted a bare clause and
        // an orphan "]" — the accounting's own framing had the shape it exists to forbid.
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

        // The scripts family's own lead + budget clause, the same two-part shape the dangling subject has one
        // family over: a completeness assertion off what the response emitted, then the listing budget's share of
        // what is absent, decomposed against the sweep's own totals rather than reported as a bare "capped" flag.
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
        // The dialogue family's own two-part shape, in the units a SEEDED family has: a completeness assertion over
        // the topics its seeds own, then the seed budget's own share of what is absent. They are separate clauses
        // because they are separate absences — a topic that did not fit is a rendering fact, a seed the budget never
        // reached is a scope fact, and one sentence for both would tell a caller to raise the wrong knob.
        if (Has(SweepSubject.DialogueTopics))
        {
            sb.Append(Short(v, SweepSubject.DialogueTopics)
                ? string.Format(ReadSentences.SweepDialogueVisible, Shown(v, SweepSubject.DialogueTopics),
                                Found(SweepSubject.DialogueTopics))
                : string.Format(ReadSentences.SweepDialogueAllVisible, Found(SweepSubject.DialogueTopics)));
        }
        if (DialogueSeedsUnreached > 0 || (v.Worst && Has(SweepSubject.DialogueSeedRefusals)))
            sb.Append(string.Format(ReadSentences.SweepDialogueSeedsCut, _dialogueSeedsTried, _dialogueSeedsNamed,
                                    DialogueSeedsUnreached, _limit));
        // The totals, restated wherever this family's listing is short — they are never capped, and a short listing
        // with no total beside it reads as the whole answer.
        if (Has(SweepSubject.DialogueTopics) && (Short(v, SweepSubject.DialogueTopics) || DialogueSeedsUnreached > 0 || v.Worst))
            sb.Append(string.Format(ReadSentences.SweepDialogueProblems, _dialogueProblems,
                                    Found(SweepSubject.DialogueTopics)));
        if (Short(v, SweepSubject.DialogueSeeds))
            sb.Append(string.Format(ReadSentences.SweepSections, Shown(v, SweepSubject.DialogueSeeds),
                                    Found(SweepSubject.DialogueSeeds)));
        if (Short(v, SweepSubject.DialogueSeedRefusals))
            sb.Append(string.Format(ReadSentences.SweepDialogueRefusalsCut, Shown(v, SweepSubject.DialogueSeedRefusals),
                                    Found(SweepSubject.DialogueSeedRefusals)));

        // The scripts family's counts_only honesty layer. Same rule as the errors family's unread rows, in that
        // family's own subject — its rows are the plugins whose record enumeration faulted.
        if (Short(v, SweepSubject.ScriptScanRows))
            sb.Append(string.Format(ReadSentences.SweepUnreadCut, Shown(v, SweepSubject.ScriptScanRows),
                                    Found(SweepSubject.ScriptScanRows)));

        // One clause per short subject, each computed from the subject it names. A section is dropped by the RENDER,
        // and the render runs whether or not the dangling walk did — with findings=["missing_masters"] the sweep
        // lists nothing and the section loop still cuts, which is #361's own shape in the lane the tool's own
        // description sells as the cheap "is any master missing anywhere" sweep.
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

    /// <summary>Is anything at all absent from this response? ONE test over EVERY DECLARED subject, so the remedy and
    /// the clauses above it cannot disagree, and a subject the lane does not have cannot make it true. Dropped
    /// sections belong here: a sweep whose findings are all missing masters omits no dangling ref, so without this
    /// term a response missing 34 of 41 sections closed as complete — with no remedy, no roster, and a json twin that
    /// said truncated.</summary>
    /// <para>It does NOT take <c>v.Worst</c> as an answer on its own. The worst case is every DECLARED subject at
    /// its widest, not every subject declared: read as "assume something is missing", it wrote the remedy clauses
    /// into a reserve for a lane where nothing can be. The worst case still dominates the real one term by term —
    /// its counts are the totals and <see cref="Short"/> is true of every subject that can ever be short — so the
    /// reserve stays an upper bound while dropping what no rendering could reach.</para></summary>
    bool Missing(Values v)
        => v.ByBudget + v.ByCut > 0 || ScriptOmittedByBudget > 0 || DialogueSeedsUnreached > 0
           || Short(v, SweepSubject.PluginSections) || Short(v, SweepSubject.ExcludedRows)
           || Short(v, SweepSubject.UnreadRows)
           || Short(v, SweepSubject.ScriptRecords) || Short(v, SweepSubject.ScriptScanRows)
           || Short(v, SweepSubject.DialogueSeeds) || Short(v, SweepSubject.DialogueTopics)
           || Short(v, SweepSubject.DialogueSeedRefusals);

    // ---- the json lane ------------------------------------------------------------------------------

    /// <summary>The accounting as json states it — the same numbers, in the transport's own terms rather than as a
    /// prose sentence a machine consumer would have to parse.
    ///
    /// <para>It writes §2.1's four required in-band fields too, flat, rather than leaving them at the call site. Not
    /// tidiness: <see cref="JsonReserve"/> measures this method, so a field written anywhere else is a field outside
    /// the reserve — which is how the first cut of this class under-reserved and let a 5000-char cap return 5673.
    /// Everything the close emits is written here, and therefore measured.</para></summary>
    internal void WriteJson(Utf8JsonWriter w) => WriteJson(w, Real());

    void WriteJson(Utf8JsonWriter w, Values v)
    {
        // A field named for a subject is present exactly where that subject is, and absent otherwise — never a zero
        // standing in for "this lane has no such thing". Written unconditionally, a counts_only sweep that found 240
        // dangling refs reported "plugins_with_findings": 0; gated on the LISTING flag instead, a
        // findings=["missing_masters"] sweep wrote no sections, no rendered and no truncated at all, so the json
        // twin of a text response that stated its cut said nothing about it.
        bool sections = Has(SweepSubject.PluginSections);
        bool dangling = Has(SweepSubject.DanglingEntries);
        // The scripts family's own listing subject. Its `capped` / `rendered` / `truncated` used to be written at
        // the render's call site — outside what JsonReserve measures, which is exactly how that lane's roster ended
        // up unbounded. Everything the close emits is written here, and therefore measured.
        bool scriptSections = Has(SweepSubject.ScriptRecords);
        // The dialogue family's listing subject. Its "capped" is the SEED budget — the knob that decides how many
        // seeds a call expands — which is a different quantity from the sibling families' finding budgets even
        // though all three are spelled limit=.
        bool dialogueTopics = Has(SweepSubject.DialogueTopics);
        if (dangling) w.WriteBoolean("capped", v.ByBudget > 0);
        else if (scriptSections) w.WriteBoolean("capped", ScriptOmittedByBudget > 0);
        else if (dialogueTopics) w.WriteBoolean("capped", DialogueSeedsUnreached > 0);
        if (sections)
        {
            w.WriteNumber("plugins_with_findings", Found(SweepSubject.PluginSections));
            w.WriteNumber("rendered", Shown(v, SweepSubject.PluginSections));
            // truncated is THIS RESPONSE's fact over every subject it has, which is what a consumer deciding
            // whether to re-ask actually needs; capped above is the listing budget's separate one.
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
            w.WriteNumber("topics_validated", Found(SweepSubject.DialogueTopics));
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
            // The scripts family's decomposition, in its own units: the findings the sweep counted, the subset the
            // listing budget admitted, and the record sections this response actually carried.
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
        if (dialogueTopics)
        {
            // The dialogue family's decomposition, in a SEEDED family's units: the seeds named, the seeds the budget
            // let this call try, the topics those seeds own, what the response carried, and the findings the
            // validation counted — which no budget caps.
            w.WriteNumber("seeds_named", _dialogueSeedsNamed);
            w.WriteNumber("seeds_validated", _dialogueSeedsTried);
            w.WriteNumber("seeds_not_reached_by_budget", DialogueSeedsUnreached);
            w.WriteNumber("dialogue_topics_found", Found(SweepSubject.DialogueTopics));
            w.WriteNumber("dialogue_topics_rendered", Shown(v, SweepSubject.DialogueTopics));
            w.WriteNumber("dialogue_findings_found", _dialogueProblems);
            w.WriteNumber("limit", _limit);
        }
        // In BOTH lanes, for the reason the subject is declared in both: a seed nobody could reach bounds the answer
        // rather than sitting inside it, so counts_only states it too.
        if (Has(SweepSubject.DialogueSeedRefusals))
        {
            w.WriteNumber("seeds_unreachable_total", Found(SweepSubject.DialogueSeedRefusals));
            w.WriteNumber("seeds_unreachable_named", Shown(v, SweepSubject.DialogueSeedRefusals));
        }
        // Facts about the CALL rather than about any subject, so they are true of every lane and written by every
        // lane: what this section listed at all, and the cap it was given.
        w.WriteNumber("max_chars", _cap);
        // THE SAME RULE THE HEAD OF THIS METHOD STATES, applied to the three remaining unconditional blocks: a field
        // named for a subject is present exactly where that subject is. Written unconditionally they were zeros
        // standing in for "this lane has no such thing" — the very reading the rule exists to make unrepresentable.
        // The shape that surfaced it is one this merge INVENTED: before it, a refusal early-returned {error, epoch}
        // and never reached this writer, so no response could carry a section for a family that did not run beside
        // sections for families that did. A dialogue family refused on cost then stated `excluded_plugins_total: 0`
        // and `unread_plugins_total: 0` — about a plugin scope it does not have (it is seeded, not swept) — and an
        // empty `dangling_missing_by_source`, which is the ERRORS family's roster, inside the dialogue family's
        // object. Meanwhile the TEXT lane wrote no accounting line at all for that family (CanStateAccounting), so
        // the two transports said different things about one family, which is the failure this branch's own
        // one-source rule is about.
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
        // The roster is the DANGLING subject's — Worst() already reserves for it on exactly that test, and a lane
        // without that subject can never fill it. On findings=["missing_masters"] and under counts_only it was an
        // empty array claiming no source plugin lost dangling findings, over a sweep that listed none.
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
            // The roster's own bound, disclosed rather than implied — the same rule the text line follows, so a
            // machine consumer and a reading one learn the same thing about how complete the roster is.
            w.WriteNumber("dangling_missing_by_source_total", v.RosterTotal);
        }
        w.WriteEndObject();
    }

    /// <summary>Serialize one accounting into a scratch buffer and measure it. Used for the worst case only — the
    /// real one is written straight into the response.
    ///
    /// <para>Under the RESPONSE's writer options, not the default ones. Measuring unindented what is then written
    /// indented is a reserve that is wrong by the whole indentation, and it was: the first cut of this measured with
    /// a bare writer and a 5000-char cap returned 5673.</para></summary>
    int MeasureJson(Values v)
    {
        // A standalone writer needs an enclosing object for a named property, and the wrapper's own two braces are
        // covered by Glue.
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, JsonWire.WriterOptions))
        {
            w.WriteStartObject();
            new CheckAccounting(v, this).WriteJson(w, v);
            // The boundary rides the measurement rather than being added as a raw char count: json escapes the
            // apostrophes in it, so its encoded length is not its string length.
            w.WriteString("boundary", _boundary);
            w.WriteEndObject();
        }
        return (int)ms.Length;
    }

    /// <summary>The measuring constructor: every subject declared at full width, so
    /// <see cref="WriteJson(Utf8JsonWriter, Values)"/> writes the worst case with no field missing. It is never
    /// registered against and never rendered into a response.</summary>
    /// <param name="real">the accounting being measured FOR. It used to declare every non-histogram subject there
    /// is, which made the worst case wider than any rendering of this lane could be — and once the scripts family's
    /// subjects joined the enum, every errors-lane response would have reserved room for two numbers it can never
    /// write. A field a lane cannot write is not a reserve, it is a subtraction from the answer (the text lane's
    /// <see cref="CanStateAccounting"/>, in this transport). The scripts family's two finding counts are copied
    /// rather than substituted, because they are SWEEP facts: the widest value they can print is the value they
    /// will print, and a stand-in derived from the dangling roster is 0 on the very lane that writes them.</param>
    CheckAccounting(Values v, CheckAccounting real)
    {
        _boundary = "";
        _bySource = v.Roster;
        _cap = int.MaxValue;
        _limit = int.MaxValue;
        _budgetListed = v.ByBudget;
        _scriptFindingsFound = real._scriptFindingsFound;
        _scriptFindingsListed = real._scriptFindingsListed;
        _scriptTotals = real._scriptTotals;
        // Each subject at the WIDEST number this lane can print for it: the dangling-derived worst case, or the
        // subject's own found count where that is larger. A subject whose population is neither the dangling total
        // nor the roster — the scripts family's record sections — printed 0 in the worst case and its real count in
        // the response, which is a reserve short by the difference in digits.
        foreach (var s in real._found.Keys)
            if (!s.IsHistogram()) Declare(s, Math.Max(real.Found(s), Math.Max(v.ByBudget, v.RosterTotal)));
    }

    // ---- the cap floor ------------------------------------------------------------------------------

    /// <summary>The overrun notice, or null. Non-null on every response longer than the cap it was given, and it
    /// names WHICH of the two overruns happened: a <c>max_chars</c> too small to hold the response's fixed part
    /// (<see cref="ReadSentences.SweepCapTooSmall"/>), or a body unit that ran past what the budget had left after
    /// that fixed part fit (<see cref="ReadSentences.SweepCapOvershot"/>). Both are arms where the response is
    /// longer than it was asked to be: dropping the accounting would restore exactly the silence #361 is, and
    /// refusing would turn a call that answers today into one that does not, so the accounting ships and the overrun
    /// is named with the number that fixes it.
    ///
    /// <para>ONE sentence for both was tried and rejected: the fixed-part explanation is FALSE of the second, and
    /// there is nothing true of both that is also a cause. The discriminator adds no state —
    /// <paramref name="needed"/> is the fixed part's own size, which the caller of this method already has.</para>
    ///
    /// <para>It is asked of the FINISHED response's length, and answers only about that. Predicted from
    /// <c>headerLength + reserve</c> it was a statement about the worst case the reserve is sized for, and it fired
    /// over a thousand times in a 200–6000 cap sweep on responses that were well inside their cap.</para>
    ///
    /// <para><b>THE RULE THIS ARM IS BUILT ON: every quantity it reads is MEASURED, none derived.</b>
    /// <paramref name="contentLength"/> is the finished response's own length; <paramref name="noticeLength"/> is
    /// how much of that this notice is; <paramref name="needed"/> is that same length less what the emitter let
    /// through, which <see cref="BoundedBody.FixedPart"/> measures at each unit as it lands. Not one of them is a
    /// hand-kept running total or a worst case standing in for a real one.</para>
    ///
    /// <para>That is not a style preference. This branch fixed exactly this disease at the json writer's framing —
    /// two constants kept by hand until <see cref="MeasureFraming"/> derived both from one measurement — and left
    /// it alive one level up, where this arm's inputs were SUMMED from components captured at different moments: a
    /// header measured before the histogram axes wrote their notes, a reserve total that still counted room
    /// <see cref="BoundedBody.Release"/> had given back, and a lane's whole json entry slack. Each error was
    /// invisible on its own, they did not cancel, and the first attempt to fix them by ADDING UP the write sites
    /// instead missed json's empty <c>plugins</c> array. Subtracting the body is the form that cannot miss one.</para>
    ///
    /// <para><paramref name="needed"/> is what it takes to carry this response's fixed part plus the accounting —
    /// the number a caller can set max_chars to and stop seeing this. It is not the current length: raising the cap
    /// widens the printed max_chars by a digit or two, which the slack below absorbs, and a probe follows the
    /// remedy at a sweep of caps rather than trusting that reasoning. It is also never BELOW the cap the caller
    /// already passed: a remedy telling them to lower max_chars is one they cannot act on, and the fixed part that
    /// does not fit is by definition bigger than the budget it did not fit in.</para></summary>
    /// <param name="contentLength">the WHOLE response, this notice included — it is part of what the caller
    /// receives, so the cap test is asked of it.</param>
    /// <param name="needed">this response's fixed part, every term measured (see the summary).</param>
    /// <param name="noticeLength">how many of <paramref name="contentLength"/>'s chars are this notice. It
    /// disappears the moment the response fits, so a remedy that counts it tells the caller to buy room for a
    /// sentence they are paying to remove — measured at 234 chars over the true smallest fitting cap in the text
    /// lane and 1,278 (85%) in json, the other half of that being the json slack the fixed part never needed.</param>
    internal string? CapTooSmall(int contentLength, int needed, int noticeLength = 0)
    {
        if (contentLength <= _cap) return null;
        // WHICH overrun this is, from what the arm was already handed. A cap that cannot hold the fixed part is one
        // story; a body unit that ran past the budget after the fixed part fit is a different one, and the first
        // sentence told that caller their header and accounting did not fit in a cap holding them with room to
        // spare. needed IS the fixed part's size, so no state is added to tell them apart.
        var sentence = needed > _cap ? ReadSentences.SweepCapTooSmall : ReadSentences.SweepCapOvershot;
        int raiseTo = Math.Max(needed, contentLength - noticeLength) + RaiseSlack;
        return string.Format(sentence, _cap, raiseTo, contentLength);
    }

    /// <summary>Slack on the number the overrun notice tells a caller to raise to. Setting max_chars to exactly the
    /// length measured under the OLD cap gets the notice back with the number one higher, because max_chars is
    /// printed inside the accounting and gained a digit — measured, at every 3→4 and 4→5 digit crossing. Two digits
    /// of headroom per place it is printed clears it, and the follow-the-remedy arm is what holds that.</summary>
    const int RaiseSlack = 8;

    /// <summary>The chars the body may occupy. Never negative — a cap too small for the accounting yields a body
    /// budget of zero and the notice above, not a negative bound that every emission test passes.</summary>
    internal int BodyBudget(int reserve) => Math.Max(0, _cap - reserve);
}
