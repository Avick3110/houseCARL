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
/// <para><b>Deliberately findings-family-agnostic, and deliberately no further.</b> Its interface speaks in
/// entries-emitted vs entries-found, which carries to any findings family without knowing what a family is. It
/// takes no taxonomy parameter and holds no family enum: the merged <c>check</c> surface (SPEC §6.1) is a separate
/// PR with its own design, and building its machinery here on speculation is what CLAUDE.md §8 names.</para>
/// </summary>
internal sealed class CheckAccounting
{
    // ---- what the SWEEP found (fixed before the render starts) --------------------------------------
    readonly IReadOnlyList<SweepCount> _bySource;   // true dangling count per source plugin, never limit-capped
    readonly int _found;                            // every dangling ref the sweep counted, in scope
    readonly int _budgetListed;                     // the subset the listing budget admitted into the reports
    readonly int _sectionsWithFindings;
    readonly int _excludedTotal;
    readonly int _unreadTotal;
    readonly bool _listing;                         // is a per-plugin listing being built at all?
    readonly int _cap;
    readonly int _limit;

    // ---- what the RENDER put in (filled as it emits) ------------------------------------------------
    readonly Dictionary<string, int> _emitted = new(StringComparer.OrdinalIgnoreCase);
    int _visible, _sections, _excluded, _unread;

    /// <summary>Build the accounting for one response. Under <c>counts_only=true</c> the listing clauses are ABSENT
    /// rather than zero: nothing is listed there BY DESIGN, and "the budget omitted everything" would report a mode
    /// working correctly as a failure.</summary>
    internal CheckAccounting(ErrorCheckResult r, int cap)
    {
        _cap = cap;
        _limit = r.Limit;
        _listing = !r.CountsOnly && r.Classes.HasFlag(ErrorFindingClass.Dangling);
        _bySource = r.DanglingBySource ?? Array.Empty<SweepCount>();
        _found = r.TotalDangling;
        _budgetListed = r.Reports.Sum(p => p.Dangling.Count);
        _sectionsWithFindings = r.Reports.Count;
        _excludedTotal = r.ExcludedPlugins.Count;
        // counts_only's reports list carries the honesty layer only — plugins whose records could not be read. In
        // the listing lane those same plugins carry findings and are counted as SECTIONS, so this subject exists
        // exactly where the other one does not, and the two can never double-count the same row.
        _unreadTotal = r.CountsOnly ? r.Reports.Count : 0;
    }

    // ---- registration: the render tells the accounting what it emitted ------------------------------

    /// <summary>One dangling entry just went into the response, sourced from <paramref name="plugin"/>. Called where
    /// the line is APPENDED, never where a section is entered: a section total would claim entries for a section the
    /// cut left half-written, which is the class of false number this construction exists to make unrepresentable.
    /// </summary>
    internal void Entry(string plugin)
    {
        _visible++;
        _emitted[plugin] = (_emitted.TryGetValue(plugin, out var had) ? had : 0) + 1;
    }

    internal void Section() => _sections++;

    /// <summary>Rows the two honesty-layer rosters actually appended. Taken as a COUNT because those helpers are
    /// shared with validate_scripts, whose response layer is not this PR's — they report what they emitted and each
    /// caller states it in its own terms, so neither lane's prose leaks into the other's.</summary>
    internal void ExcludedRows(int n) => _excluded += n;
    internal void UnreadRows(int n) => _unread += n;

    // ---- derived ------------------------------------------------------------------------------------

    /// <summary>Refs the listing budget never admitted. A pure SWEEP fact, so it is readable before the body renders
    /// — which is what lets the baseline block's phase-order sentence consult it without waiting for emission.
    /// </summary>
    internal int OmittedByBudget => _listing ? _found - _budgetListed : 0;

    internal int Visible => _visible;
    internal int Found => _found;
    internal int Omitted => _listing ? _found - _visible : 0;

    /// <summary>Refs the budget admitted and this response then could not fit. The other half of
    /// <see cref="Omitted"/> BY CONSTRUCTION: both are subtractions off the same total, so the two causes sum to it
    /// exactly rather than by two counters happening to agree.</summary>
    internal int OmittedByCut => _listing ? _budgetListed - _visible : 0;

    internal int SectionsRendered => _sections;
    internal int SectionsWithFindings => _sectionsWithFindings;
    internal bool Listing => _listing;

    /// <summary>WHICH source plugins are missing entries from THIS response, largest first. Computed against what was
    /// emitted, so it covers both causes at once: under the two-layer split, a plugin whose entries the budget listed
    /// and the cut then dropped appeared in neither sentence.</summary>
    internal IReadOnlyList<SweepCount> MissingBySource
    {
        get
        {
            if (!_listing) return Array.Empty<SweepCount>();
            var acc = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in _bySource)
            {
                int shown = _emitted.TryGetValue(row.Key, out var c) ? c : 0;
                if (row.Count > shown) acc[row.Key] = row.Count - shown;
            }
            return SweepFindings.Histogram(acc);
        }
    }

    // ---- the reserve --------------------------------------------------------------------------------

    /// <summary>The chars held back from <c>max_chars</c> so the text accounting and the boundary footer are always
    /// affordable.</summary>
    internal int TextReserve => _textReserve ??= Compose(Worst()).Length + ReadSentences.SweepBoundary.Length + ReadSentences.SweepBoundaryLabel.Length + Glue;
    int? _textReserve;

    /// <summary>The json lane's own reserve. Measured by SERIALIZING the worst case, not by estimating it off the
    /// text line: the two encodings differ in escaping and syntax, and a reserve that is an estimate is a reserve
    /// that is occasionally wrong in the direction that matters.</summary>
    internal int JsonReserve => _jsonReserve ??= MeasureJson(Worst()) + Glue;
    int? _jsonReserve;

    /// <summary>Slack over the measured worst case, and it is load-bearing rather than cosmetic. A body's budget
    /// test is taken BEFORE a line or entry is written, because neither transport can measure one without writing
    /// it — so the last one always lands slightly over the budget it was tested against, and the reserve absorbs
    /// exactly that. One dangling entry is two FormID strings, a type name and an EditorID; a kilobyte covers one
    /// comfortably, plus the brackets that close the document and the newlines the text lane wraps its line in.
    ///
    /// <para>A reserve slightly too large costs a few characters of listing. One slightly too small is #361. The cap
    /// sweep in check-errors-guard is what holds this honest — not the number's plausibility.</para></summary>
    const int Glue = 1024;

    /// <summary>The values that make the longest line this sweep could produce. Every substitution is at or above
    /// what a real render can reach: the counts are the totals themselves, so their digit widths bound every real
    /// count; every optional clause is present; and the roster holds the LONGEST source names rather than the
    /// largest, because a partly-listed response can promote a long-named small source into the roster that a
    /// fully-omitted one would have pushed out — "largest" is not a bound and "longest" is.</summary>
    Values Worst()
    {
        var longest = _bySource.OrderByDescending(c => JsonEncodedText.Encode(c.Key).Value.Length)
                               .Take(ReadSentences.SweepRosterRows)
                               .Select(c => new SweepCount(c.Key, _found))
                               .ToList();
        return new Values(Visible: 0, ByBudget: _found, ByCut: _found, Roster: longest,
                          RosterTotal: Math.Max(_bySource.Count, longest.Count),
                          Sections: 0, Excluded: 0, Unread: 0, Worst: true);
    }

    /// <summary>What this response actually did.</summary>
    Values Real() => new(_visible, OmittedByBudget, OmittedByCut, MissingBySource, MissingBySource.Count,
                         _sections, _excluded, _unread, Worst: false);

    /// <summary>The numbers one rendering of the accounting states. A record rather than eight parameters so the
    /// real case and the worst case go through ONE composer per transport — a second formatter would be a second
    /// spelling, and a reserve computed off a spelling that has since drifted is a reserve that silently stops
    /// bounding anything.</summary>
    readonly record struct Values(int Visible, int ByBudget, int ByCut, IReadOnlyList<SweepCount> Roster,
                                  int RosterTotal, int Sections, int Excluded, int Unread, bool Worst);

    // ---- the text lane ------------------------------------------------------------------------------

    /// <summary>The accounting as the text transport states it, or null where there is nothing at all to account
    /// for. Present on EVERY listing response, complete or not: silence used to mean both "this response carries
    /// everything" and "#361", and those two must never read alike.</summary>
    internal string? TextLine()
    {
        if (!_listing && _excluded >= _excludedTotal && _unread >= _unreadTotal) return null;
        return Compose(Real());
    }

    string Compose(Values v)
    {
        var sb = new StringBuilder();
        int omitted = v.ByBudget + v.ByCut;
        bool missing = Missing(v);

        if (_listing)
        {
            sb.Append(omitted > 0 || v.Worst
                ? string.Format(ReadSentences.SweepVisible, v.Visible, _found)
                : string.Format(ReadSentences.SweepAllVisible, _found));

            var causes = new List<string>();
            if (v.ByBudget > 0 || v.Worst) causes.Add(string.Format(ReadSentences.SweepOmittedByBudget, v.ByBudget, _limit));
            if (v.ByCut > 0 || v.Worst) causes.Add(string.Format(ReadSentences.SweepOmittedByCut, v.ByCut, _cap));
            if (causes.Count > 0) sb.Append(string.Join(",", causes)).Append('.');

            // Stated whenever a section did not make it: the entry count cannot answer "is a whole plugin missing",
            // and a plugin with no section at all is exactly what the roster below exists to recover.
            if (v.Sections < _sectionsWithFindings || v.Worst)
                sb.Append(string.Format(ReadSentences.SweepSections, v.Sections, _sectionsWithFindings));
        }

        // The two honesty-layer rosters. Their rows are what houseCARL could NOT read, so a silent cut there hides
        // the boundary of the answer rather than a finding inside it.
        if (v.Excluded < _excludedTotal || v.Worst)
            sb.Append(string.Format(ReadSentences.SweepExcludedCut, v.Excluded, _excludedTotal));
        if (v.Unread < _unreadTotal || v.Worst)
            sb.Append(string.Format(ReadSentences.SweepUnreadCut, v.Unread, _unreadTotal));

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
        }

        if (missing && _listing) sb.Append(ReadSentences.SweepNoSectionRule);
        sb.Append(missing ? ReadSentences.SweepRemedy : ReadSentences.SweepComplete);
        return sb.ToString();
    }

    /// <summary>Is anything at all absent from this response? One test, so the remedy and the roster rule can never
    /// disagree about whether there is something to remedy.</summary>
    bool Missing(Values v) => v.Worst || v.ByBudget + v.ByCut > 0 || v.Excluded < _excludedTotal || v.Unread < _unreadTotal;

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
        // capped is the listing budget's fact, truncated is this response's — both off the ONE computation, so a
        // consumer no longer has to know which layer measured which.
        w.WriteBoolean("capped", v.ByBudget > 0);
        w.WriteNumber("plugins_with_findings", _sectionsWithFindings);
        w.WriteNumber("rendered", v.Sections);
        w.WriteBoolean("truncated", v.ByCut > 0 || v.Sections < _sectionsWithFindings);
        w.WriteStartObject("accounting");
        w.WriteBoolean("listing", _listing);
        if (_listing)
        {
            w.WriteNumber("dangling_found", _found);
            w.WriteNumber("dangling_visible", v.Visible);
            w.WriteNumber("dangling_missing", v.ByBudget + v.ByCut);
            w.WriteNumber("dangling_missing_by_budget", v.ByBudget);
            w.WriteNumber("dangling_missing_by_response_cut", v.ByCut);
            w.WriteNumber("limit", _limit);
            w.WriteNumber("sections_with_findings", _sectionsWithFindings);
            w.WriteNumber("sections_rendered", v.Sections);
        }
        w.WriteNumber("max_chars", _cap);
        w.WriteNumber("excluded_plugins_total", _excludedTotal);
        w.WriteNumber("excluded_plugins_named", v.Excluded);
        w.WriteNumber("unread_plugins_total", _unreadTotal);
        w.WriteNumber("unread_plugins_named", v.Unread);
        w.WriteStartArray("dangling_missing_by_source");
        for (int i = 0; i < v.Roster.Count && i < ReadSentences.SweepRosterRows; i++)
        {
            w.WriteStartObject();
            w.WriteString("plugin", v.Roster[i].Key);
            w.WriteNumber("count", v.Roster[i].Count);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        // The roster's own bound, disclosed rather than implied — the same rule the text line follows, so a machine
        // consumer and a reading one learn the same thing about how complete the roster is.
        w.WriteNumber("dangling_missing_by_source_total", v.RosterTotal);
        w.WriteEndObject();
    }

    /// <summary>Serialize one accounting into a scratch buffer and measure it. Used for the worst case only — the
    /// real one is written straight into the response.
    ///
    /// <para>Under the RESPONSE's writer options, not the default ones. Measuring unindented what is then written
    /// indented is a reserve that is wrong by the whole indentation, and it was: the first cut of this measured with
    /// a bare writer and a 5000-char cap returned 5673.</para></summary>
    static int MeasureJson(Values v)
    {
        // A standalone writer needs an enclosing object for a named property, and the wrapper's own two braces are
        // covered by Glue.
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, JsonWire.WriterOptions))
        {
            w.WriteStartObject();
            new CheckAccounting(v).WriteJson(w, v);
            // The boundary rides the measurement rather than being added as a raw char count: json escapes the
            // apostrophes in it, so its encoded length is not its string length.
            w.WriteString("boundary", ReadSentences.SweepBoundary);
            w.WriteEndObject();
        }
        return (int)ms.Length;
    }

    /// <summary>The measuring constructor: enough state for <see cref="WriteJson(Utf8JsonWriter, Values)"/> to write
    /// the worst case at full width. It is never registered against and never rendered into a response.</summary>
    CheckAccounting(Values v)
    {
        _bySource = v.Roster;
        _found = v.ByBudget;
        _cap = int.MaxValue;
        _limit = int.MaxValue;
        _listing = true;
        _sectionsWithFindings = v.RosterTotal;
        _excludedTotal = v.RosterTotal;
        _unreadTotal = v.RosterTotal;
    }

    // ---- the cap floor ------------------------------------------------------------------------------

    /// <summary>The overrun notice, or null. Non-null exactly where the caller's <c>max_chars</c> cannot hold the
    /// response's HEADER plus the accounting — the one arm where the response is longer than it was asked to be:
    /// dropping the accounting would restore exactly the silence #361 is, and refusing would turn a call that
    /// answers today into one that does not, so the accounting ships and the overrun is named with the number that
    /// fixes it.
    ///
    /// <para><paramref name="headerLength"/> is the response's length where the BODY BEGINS, not where it ends.
    /// Measured at the end it is the body's own length, which at any cut is about the whole budget — so the notice
    /// fired on every truncated response and added its own unbudgeted couple of hundred chars, which is how the
    /// first cut of this class overran a 5000-char cap by 106. The condition is about the fixed part of the
    /// response, so it is asked before the variable part exists.</para></summary>
    internal string? CapTooSmall(int headerLength, int reserve) =>
        headerLength + reserve > _cap ? string.Format(ReadSentences.SweepCapTooSmall, _cap, headerLength + reserve) : null;

    /// <summary>The chars the body may occupy. Never negative — a cap too small for the accounting yields a body
    /// budget of zero and the notice above, not a negative bound that every emission test passes.</summary>
    internal int BodyBudget(int reserve) => Math.Max(0, _cap - reserve);
}
