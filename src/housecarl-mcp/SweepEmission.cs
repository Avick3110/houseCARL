using HousecarlCore;

namespace HousecarlMcp;

/// <summary>
/// The countable things a sweep response can carry. A lane DECLARES the subjects it has
/// (<see cref="CheckAccounting"/>), and every sentence about what is missing is computed from a declared subject
/// — so a lane without sections cannot claim about sections, and a lane with them cannot fail to.
///
/// <para>These are LANE FACTS, not a findings taxonomy: "how many plugin sections did this response render" is a
/// question about the render, and it stays the same question whatever classes of finding the sections carry. The
/// merged <c>check</c> surface's families are a separate design (SPEC §6.1) and no enum here anticipates them.</para>
/// </summary>
internal enum SweepSubject
{
    /// <summary>Dangling references listed one line at a time. The only subject the listing budget (<c>limit=</c>)
    /// can also drop, which is why it is the only one whose omission is decomposed into two causes.</summary>
    DanglingEntries,

    /// <summary>Per-plugin report sections. Present in every LISTING lane, including one where <c>findings=</c>
    /// excluded 'dangling' and there are no entries at all — the render still cuts sections there.</summary>
    PluginSections,

    /// <summary>Rows of the excluded-plugin roster: the plugins the index could not parse.</summary>
    ExcludedRows,

    /// <summary>Rows of the <c>counts_only</c> honesty layer: the plugins whose records could not be read.</summary>
    UnreadRows,

    /// <summary>Rows of the <c>counts_only</c> dangling histogram, by TARGET plugin — the plugin the broken refs
    /// point INTO.</summary>
    HistogramByTarget,

    /// <summary>Rows of the <c>counts_only</c> dangling histogram, by SOURCE plugin — the plugin the broken refs
    /// come FROM (#344's axis).
    ///
    /// <para><b>Its own subject, and that is the point.</b> Both axes shared one for a while, and a subject is what
    /// <see cref="BoundedBody"/> stops: when the TARGET axis closed on <c>limit=</c> it marked the shared subject
    /// stopped, and every row of the axis below was then refused — at <c>limit=3</c> over two 200-row axes, the
    /// SOURCE axis rendered no rows at all, at a cap 79,000 chars short of biting, under a remedy naming
    /// <c>max_chars=</c>, a knob that would have moved nothing. One subject standing for two lane facts is the
    /// <c>_listing</c> boolean <see cref="CheckAccounting"/> was built to retire, one level down.</para>
    ///
    /// <para><b>Observable in the TEXT lane only, and said here rather than left to be assumed.</b> What made the
    /// shared subject bite is <see cref="BoundedBody.Close"/>, which stops a subject after a row budget cut the axis
    /// short. The json lane writes no closing line, so nothing there stops a subject except the budget itself —
    /// and because the response's length only grows, a budget that refuses the first axis refuses the second anyway.
    /// Sabotaged to share, the json lane stayed green at every arm: the split is unobservable there TODAY. It is
    /// threaded through both transports regardless, because the alternative is a hard-coded coupling that comes back
    /// the moment json gains any per-axis close, and because a parameter has to carry some value — the honest one
    /// costs nothing. No arm claims to pin it in json; HISTOGRAM-AXES-CUT-INDEPENDENTLY pins the text lane, and
    /// HISTOGRAM-JSON-STATES-THE-SAME-CUT pins what json does say about a cut.</para></summary>
    HistogramBySource,

    /// <summary>Per-record sections of the scripts family's listing — its analogue of
    /// <see cref="PluginSections"/>. A section is emitted WHOLE or not at all, for the reason the errors family's
    /// sections are: everything inside one (the unbound findings, the bound-but-null advisory, the "could not
    /// verify" notes) is a finding in its own right, and a per-line "append if it fits" drops them with no subject
    /// accounting for the loss. Before this subject existed the scripts listing tested <c>sb.Length &gt;= cap</c>
    /// inline and kept no accounting at all, which is how <c>validate_scripts</c> returned 80,673 chars against its
    /// own 80,000 cap on the live order, undeclared.</summary>
    ScriptRecords,

    /// <summary>Rows of the scripts family's <c>counts_only</c> honesty layer: plugins whose record enumeration
    /// faulted. Its own subject rather than <see cref="UnreadRows"/>, because a merged response can run both
    /// families in that mode and one subject standing for two families' rows is a double count — the same rule that
    /// split <see cref="HistogramBySource"/> off its sibling, one level up.</summary>
    ScriptScanRows,

    /// <summary>Rows of validate_scripts' <c>counts_only</c> histogram, by property NAME.</summary>
    HistogramByProperty,
}

/// <summary>
/// Which subjects are histogram axes. Deliberately NOT declared accounting subjects: an axis discloses its own cut
/// in both transports (<see cref="HistogramCut"/>), and a second statement of one fact is how a twin starts. They are
/// subjects at all so that an axis's framing lines and its rows go through the same bound as everything else.
/// </summary>
internal static class SweepSubjects
{
    internal static bool IsHistogram(this SweepSubject s)
        => s is SweepSubject.HistogramByTarget or SweepSubject.HistogramBySource or SweepSubject.HistogramByProperty;
}

/// <summary>
/// ONE histogram axis's closing fact: how many of its rows this response does not carry, and WHICH knob moves them.
///
/// <para>Computed once, from the emitting loop's own three facts, and consumed by BOTH transports — the text lane
/// renders <see cref="Line"/>, the json lane writes the same two facts as fields. They used to reach it separately:
/// text composed a sentence and json wrote nothing at all, so one cut read as a stated remedy in one transport and as
/// a bare <c>rendered &lt; distinct</c> in the other, with no way to tell which knob had done it.</para>
///
/// <para>The knob is the one that STOPPED the axis. "raise limit=" over rows the response had no room for names
/// something that moves nothing, and so does "raise max_chars=" over rows the row budget refused.</para>
/// </summary>
internal readonly record struct HistogramCut(int Remaining, bool ByBudget)
{
    /// <summary>This axis's cut, or null where it rendered every row it had.</summary>
    internal static HistogramCut? For(int distinct, int shown, bool byBudget)
        => shown >= distinct ? null : new HistogramCut(distinct - shown, byBudget);

    /// <summary>The knob a caller raises to see these rows, spelled as the parameter is.</summary>
    internal string Knob => ByBudget ? "max_chars" : "limit";

    /// <summary>The text lane's spelling, in ONE place: it is composed twice, once to measure the room to hold back
    /// for it and once to write it, and two spellings of one sentence is how a reserve stops covering what it
    /// reserves for.</summary>
    internal string Line => "  ... [" + Remaining + " more row(s) — raise " + Knob + "= to see them]\n";
}

/// <summary>
/// ONE counts_only histogram axis, as both the RESERVE and the RENDER need it.
///
/// <para>They read the same object for the same reason the cut line has one spelling: a reserve is a promise about
/// a specific sentence, and room measured off a different title or a different row count is not a reserve for the
/// sentence that gets written. Handing the two a single value makes them the same sentence by construction rather
/// than by two call sites agreeing.</para>
/// </summary>
/// <param name="Rows">the axis's tally, or null where the mode was not requested — an ABSENT axis and an EMPTY one
/// are different answers and must not render alike (Q3).</param>
/// <param name="NotComputed">what to say instead when <paramref name="Rows"/> is null: the axis's whole answer, and
/// fixed text that does not grow with the findings.</param>
internal readonly record struct HistogramAxis(SweepSubject Subject, IReadOnlyList<SweepCount>? Rows, string Title,
                                              string? Note = null, string? NotComputed = null)
{
    /// <summary>The axis's section head. Ridden by its first row, so a title never stands over nothing.</summary>
    internal string Head => "\n" + Title + " (" + (Rows?.Count ?? 0) + " distinct):\n";

    /// <summary>What an axis with nothing to tally says. Two axes that both came back empty rendered as two
    /// identical untitled sentences before the title rode this too, with no way to tell which was which — or that a
    /// second axis existed at all.</summary>
    internal string EmptyLine => "\n" + Title + ": nothing to tally — no findings in the swept scope.\n";

    /// <summary>The axis's note, spelled ONCE. It is written whatever the budget says, so the room for it is held
    /// back with the closing disclosure rather than taken out of the body's — and the render and the reserve read
    /// this one string, for the same reason <see cref="HistogramCut.Line"/> has one spelling.</summary>
    internal string NoteLine => Note is null ? "" : "\n" + Note + "\n";

    /// <summary>What this axis says INSTEAD of a tally when the mode was not requested. Also unconditional, and so
    /// also reserved: "the walk was not run" is the axis's whole answer, and an answer a budget can drop leaves the
    /// caller unable to tell it from a tally that came back empty (Q3).</summary>
    internal string NotComputedLine => Rows is null && NotComputed is not null ? NotComputed + "\n" : "";

    /// <summary>The axis's IRREDUCIBLE DISCLOSURE, in text-lane characters: the widest thing
    /// <see cref="BoundedBody.Close"/> can be asked to write for it, which is its head plus a cut line naming
    /// every row — the case where the budget admitted no rows at all. An axis that renders some of its rows
    /// discloses in less than this, and an axis that renders all of them gives the room back
    /// (<see cref="BoundedBody.Release"/>).</summary>
    internal int TextDisclosure
        => Rows is null ? 0
         : Rows.Count == 0 ? EmptyLine.Length
         : Head.Length + new HistogramCut(Rows.Count, ByBudget: true).Line.Length;

    /// <summary>Everything this axis puts in the response's FIXED PART, in text-lane characters: its unconditional
    /// lines plus its closing disclosure. ONE reserve for the lot, because they are one thing — what this axis
    /// writes whatever the budget says.
    ///
    /// <para>The notes used to sit outside every reserve, appended straight to the builder after the header had
    /// been measured. That is what made the overrun notice's fixed part up to ~184 chars smaller than the one the
    /// response carried, and in the band between the two it explained a cap too small for the fixed part as a body
    /// unit overshooting — over a <c>counts_only</c> response that emitted no body unit at all, at 96 caps of a
    /// 100–6000 sweep on the null-axes lane.</para>
    ///
    /// <para><b>What fixed THAT is the subtraction (<see cref="BoundedBody.FixedPart"/>), not this — and no arm
    /// pins this, which is said here rather than left to be discovered.</b> Sabotaged to reserve only the closing
    /// disclosure and write the notes straight to the builder, every arm in both guards stays green. It cannot
    /// bite while the only noted axis is the FIRST one: its note is written before any axis has spent anything, so
    /// holding the room and writing it early are the same thing. It is reserved anyway, because that equality is a
    /// property of today's axis ORDER rather than of the design — a second axis carrying a note would write it
    /// after the first axis had emptied the budget, and land past the cap. The mechanism itself is pinned at the
    /// unit level (EMISSION-THE-FIXED-PART-IS-WHAT-THE-BODY-DID-NOT-WRITE, and EMISSION-A-RESERVE-IS-ROOM-THE-ROWS-
    /// CANNOT-HAVE goes red when <see cref="BoundedBody.Reserve"/> holds nothing); what no fixture reaches is a
    /// response where this term changes the answer.</para></summary>
    internal int TextFixed => NoteLine.Length + NotComputedLine.Length + TextDisclosure;
}

/// <summary>
/// THE ONE PLACE either sweep transport appends anything the caller's <c>max_chars</c> can refuse.
///
/// <para><b>Why one helper rather than a test per write site.</b> Boundedness used to be asserted at each site, so
/// the unbounded ones were found one at a time and each fix bred a sibling: the json plugin head (7,296 chars
/// against a 5,270 cap), then its exact twin the <c>counts_only</c> unread roster (9,823 against 8,000), then the
/// histogram framing lines, then the json excluded roster (1,188 chars past, written above the point the header is
/// even measured at). Four instances of one class. Every body write now goes through <see cref="Emit"/>, and the
/// bound is enforced by this class rather than promised by the caller.</para>
///
/// <para><b>Why a site that under-states its cost still cannot run away.</b> A caller passes a cost only where one
/// unit can be LARGE — a plugin object carrying three exception messages, an unread row carrying a scan error —
/// because there the test before the write is what keeps the response inside its cap rather than one unit over it.
/// Everywhere else the cost is zero and the test degenerates to "is there room at all". That is enough on its own,
/// because the response's length only ever GROWS: the first unit whose declared cost was too small takes it past
/// the budget, and from that moment every test — in every subject, whether or not that subject has been tried —
/// is a comparison against a length already over. So the damage of a forgotten cost is ONE unit.</para>
///
/// <para><b>Why that damage is never SILENT, stated as it actually holds.</b> A response has exactly two ways of
/// telling a caller what it left out, and the guarantee is that neither of them is emitted through this helper:
/// <list type="bullet">
/// <item>the ACCOUNTING — a subtraction against the sweep's own totals, taken after emission stops, computed from
/// the subjects the lane DECLARED (<see cref="CheckAccounting"/>) and written inside a reserve; and</item>
/// <item>a subject's OWN CLOSING DISCLOSURE — the line that says how much of that subject did not fit, written by
/// <see cref="Close"/> out of room <see cref="Reserve"/> held back before the body rendered.</item>
/// </list>
/// This paragraph used to name only the first, and read it as covering everything: "the accounting states what was
/// left out either way". It does not. The histogram axes are deliberately NOT declared accounting subjects (see
/// <see cref="SweepSubjects"/>), so for them the first route does not exist — and the second was budget-gated like
/// a row, which means the pressure that cut the rows also cut the line reporting the cut. A whole
/// <c>counts_only</c> axis therefore left the response with nothing anywhere saying so, at every cap in a wide band
/// (#392). A disclosure that the budget can refuse is not a disclosure, so it is now part of the response's fixed
/// part like the header and the accounting: reserved first, written unconditionally.</para>
///
/// <para>An explicit "the response has gone over, stop everything" flag was written here first and then deleted:
/// monotonic length already makes it true, so the flag could be removed with every arm still green. A conditional
/// that cannot be fixtured honestly is the signal to delete it, not a testing gap to work around (CLAUDE.md §5 #11,
/// PR #339's precedent).</para>
///
/// <para>The FIXED PART is outside the emission gate entirely: the header, every axis's unconditional lines, every
/// reserved closing disclosure, the accounting and the boundary. Those are the things a response is never allowed
/// to drop, which is exactly why none of them goes through <see cref="Emit"/>. What a cap too small for the fixed
/// part gets is an overrun that SAYS SO (<see cref="CheckAccounting.CapTooSmall"/>), never a shorter response with
/// less in it. The unconditional writes still come through this class — <see cref="Fixed"/> and
/// <see cref="Close"/> — so that <see cref="FixedBeyondHeader"/> is MEASURED at the write rather than assembled
/// from what each site remembered to declare.</para>
/// </summary>
internal sealed class BoundedBody
{
    readonly int _budget;
    readonly Func<int> _length;
    readonly CheckAccounting? _acct;
    readonly HashSet<SweepSubject> _stopped = new();
    readonly Dictionary<SweepSubject, int> _held = new();
    readonly IReadOnlyList<(SweepFamily Family, IReadOnlyList<SweepSubject> Subjects)>? _plan;
    BodyAllocation? _alloc;

    /// <param name="acct">the accounting to register emissions with, or null for a lane that keeps no accounting
    /// (validate_scripts, whose response layer is not this branch's — it still gets the same bound).</param>
    /// <param name="budget">the chars the BODY may occupy: the caller's max_chars less the accounting's reserve.</param>
    /// <param name="length">what the response has emitted so far, in the transport's own unit.</param>
    /// <param name="plan">the families this response renders and which of each family's subjects have rows, or null
    /// for a lane that divides nothing (a single-family response has no siblings to be fair to, and the global
    /// budget alone is then the whole rule). See <see cref="BodyAllocation"/>.</param>
    internal BoundedBody(CheckAccounting? acct, int budget, Func<int> length,
                         IReadOnlyList<(SweepFamily Family, IReadOnlyList<SweepSubject> Subjects)>? plan = null)
    {
        _acct = acct;
        _budget = budget;
        _length = length;
        _plan = plan;
    }

    /// <summary>The allocation, built on the FIRST unit any subject emits — which is the only moment it can be
    /// built correctly. Every <see cref="Reserve"/> has happened by then (the class already requires reserving
    /// before the first unit of any subject), so the room left for ROWS is the body budget less everything held,
    /// and that is what gets divided. Built earlier it would divide room the reserves had not yet claimed;
    /// built later it would divide what an earlier subject had already spent.</summary>
    BodyAllocation Allocation => _alloc ??= new BodyAllocation(_budget - _length() - Held, _plan ?? Array.Empty<(SweepFamily, IReadOnlyList<SweepSubject>)>());

    /// <summary>Emit one unit of <paramref name="subject"/>, or refuse. Returns false when the unit did not fit —
    /// the caller's loop breaks and the accounting already knows, because the count it will report is the count of
    /// units that came back true.</summary>
    /// <param name="cost">an UPPER BOUND on what <paramref name="commit"/> will append, or 0 where the site has no
    /// cheap way to measure one — see the class summary for why a zero here bounds the damage at one unit.</param>
    /// <param name="source">for <see cref="SweepSubject.DanglingEntries"/>, the plugin the entry came FROM — the
    /// by-source roster is tallied off the same registration as the count, so the two cannot disagree.</param>
    internal bool Emit(SweepSubject subject, int cost, Action commit, string? source = null)
    {
        if (_stopped.Contains(subject)) return false;
        if (_length() + cost + Held > _budget) { Stop(subject); return false; }
        // The subject's OWN share, on top of the response-wide test rather than instead of it (#394). A subject
        // may spend its ceiling and no more even while the response has room to spare — that room belongs to the
        // siblings that have not rendered yet, and giving it away first-come is exactly the serial rule this
        // replaces.
        if (!Allocation.Fits(subject, cost)) { Stop(subject); return false; }
        int before = _length();
        commit();
        int wrote = _length() - before;
        BodyTotal += wrote;
        // Charged with what it ACTUALLY wrote, never with the declared cost — the cost is a test before a write,
        // and a ceiling kept in the units of a test rather than of the writing would drift the moment a site
        // declared 0 (which most of them do).
        Allocation.Charge(subject, wrote);
        _acct?.Emitted(subject, source);
        return true;
    }

    /// <summary>This subject emits nothing further. Told to the allocation as well as recorded here, so the room
    /// it did not spend is back in the arithmetic for the siblings after it.</summary>
    void Stop(SweepSubject subject)
    {
        _stopped.Add(subject);
        _alloc?.Done(subject);
    }

    /// <summary>What the BODY actually appended, measured at each unit as it landed — the declared cost is a budget
    /// test, never this number. It is the only quantity that separates a response's body from its fixed part, which
    /// is why it is taken here rather than reconstructed from the counts the accounting keeps.</summary>
    internal int BodyTotal { get; private set; }

    /// <summary>Hold back the room one subject writes WHATEVER THE BUDGET SAYS — its unconditional lines and its
    /// closing disclosure — BEFORE any unit is emitted. Every emission test then has to leave that room standing,
    /// so by the time the subject writes, what it writes is already paid for.
    ///
    /// <para>Reserving is what makes those writes unrefusable, and it has to happen before the FIRST unit of ANY
    /// subject: an axis that reserved its own room after a sibling had already spent the budget would be the exact
    /// silence this exists to remove.</para></summary>
    internal void Reserve(SweepSubject subject, int cost) => _held[subject] = cost;

    /// <summary>This response's FIXED PART: every char it carries whatever the budget says — the header, each
    /// axis's unconditional lines, each closing disclosure, the accounting and the boundary.
    ///
    /// <para><b>It is SUBTRACTED, not assembled.</b> Everything a cap can refuse goes through <see cref="Emit"/>
    /// and is measured there (<see cref="BodyTotal"/>); the fixed part is therefore the finished response minus
    /// that, plus room still held that no unit was allowed to touch. Nothing here enumerates the unconditional
    /// write sites, which is the point — the number this feeds is what the overrun notice branches on, and every
    /// version of it that was ASSEMBLED came out wrong in a way nothing could see. Summed from a header measured
    /// before the axes wrote their notes, a reserve total that still counted room <see cref="Release"/> had given
    /// back, and a lane's whole json slack, it understated the text lane's fixed part by up to ~184 chars and
    /// overstated the json raise-to by 85%. Assembled from the write sites instead, it still missed json's empty
    /// <c>plugins</c> array — a site nobody thinks of as a write.</para>
    ///
    /// <para>The one thing it deliberately excludes is the overrun notice, which is why the caller passes the
    /// length it measured: the notice is gone the moment the response fits, so a fixed part counting it would tell
    /// a caller to buy room for a sentence they are paying to remove.</para></summary>
    /// <param name="contentLength">the finished response as the transport measured it, WITHOUT the notice.</param>
    internal int FixedPart(int contentLength) => contentLength - BodyTotal + Held;

    /// <summary>Room reserved and not yet spent. Held against every emission test, including tests of the subject
    /// that reserved it — a subject may spend the budget on its rows, never on what it writes regardless.</summary>
    int Held
    {
        get { int n = 0; foreach (var v in _held.Values) n += v; return n; }
    }

    /// <summary>Write part of a subject's reserved, UNCONDITIONAL text now — an axis's note, a json axis object's
    /// own frame. It is not a unit, so it is not registered and it cannot be refused; what it appends is measured
    /// and charged against the room already held for this subject, so a write and the room held for it are not
    /// held against the body twice over.</summary>
    internal void Fixed(SweepSubject subject, Action commit)
    {
        int before = _length();
        commit();
        if (_held.TryGetValue(subject, out var held)) _held[subject] = Math.Max(0, held - (_length() - before));
    }

    /// <summary>Write a subject's own CLOSING DISCLOSURE — the line that says how much of it did not fit. It is not
    /// a unit, so it is not registered, and it is <b>NEVER REFUSED</b>: not because the subject stopped (a subject
    /// that stopped is exactly when this has to be said), and not on the budget either. The room came out of
    /// <see cref="Reserve"/> before the body rendered, which is what makes writing it unconditionally safe rather
    /// than merely hopeful.
    ///
    /// <para>It used to take a budget, and a subject whose rows the budget refused had its disclosure refused by
    /// the same pressure — the whole axis then left the response with nothing saying it had ever existed. A
    /// disclosure a budget can refuse is not a disclosure (#392).</para></summary>
    internal void Close(SweepSubject subject, Action commit)
    {
        _held.Remove(subject);   // spent here, and only here
        _alloc?.Done(subject);   // and its share is finished with too, whatever it did not spend
        commit();
        _stopped.Add(subject);   // nothing follows a subject's closing disclosure
    }

    /// <summary>Give a subject's remaining reserved room back UNSPENT — for the subject that turned out to have
    /// nothing left to say, because it rendered everything it had. Without this the room stays held against every
    /// later subject's emission test, and the subjects after a complete histogram would pay for a sentence nobody
    /// wrote. What the subject DID write is already in the response, and <see cref="FixedPart"/> measures it there;
    /// what is given back here is room, and room nobody wrote is not part of anything's fixed part.</summary>
    internal void Release(SweepSubject subject)
    {
        _held.Remove(subject);
        // A subject that rendered everything it had is the case the recount exists for: its unspent SHARE is what
        // the siblings after it divide. Telling the allocation here is what makes the redistribution happen at all
        // — without it a short subject's room stays reserved against a sibling that could have used it.
        _alloc?.Done(subject);
    }

    /// <summary>Did this subject stop short? For the one caller that states the fact in its own words rather than
    /// through the accounting (validate_scripts).</summary>
    internal bool Stopped(SweepSubject subject) => _stopped.Contains(subject);
}
