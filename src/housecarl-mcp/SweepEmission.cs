using HousecarlCore;

namespace HousecarlMcp;

/// <summary>
/// The countable things a sweep response can carry. A lane declares the subjects it has
/// (<see cref="CheckAccounting"/>), and every sentence about what is missing is computed from a declared subject,
/// so a lane without sections cannot claim about sections and a lane with them cannot fail to.
///
/// <para>These are lane facts, not a findings taxonomy: how many sections a response rendered is a question about
/// the render, whatever classes of finding the sections carry.</para>
/// </summary>
internal enum SweepSubject
{
    /// <summary>Dangling references listed one line at a time. The only subject the listing budget (<c>limit=</c>)
    /// can also drop, which is why it is the only one whose omission is decomposed into two causes.</summary>
    DanglingEntries,

    /// <summary>Per-plugin report sections. Present in every listing lane, including one where <c>findings=</c>
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
    /// come FROM.
    ///
    /// <para>Its own subject rather than one shared with the target axis: a subject is what
    /// <see cref="BoundedBody"/> stops, so a shared one would let the first axis closing on <c>limit=</c> refuse
    /// every row of the second, under a remedy naming a knob that moves nothing.</para></summary>
    HistogramBySource,

    /// <summary>Per-record sections of the scripts family's listing — its analogue of
    /// <see cref="PluginSections"/>. A section is emitted whole or not at all: everything inside one is a finding in
    /// its own right, and a per-line "append if it fits" drops them with no subject accounting for the loss.
    /// </summary>
    ScriptRecords,

    /// <summary>Rows of the scripts family's <c>counts_only</c> honesty layer: plugins whose record enumeration
    /// faulted. Its own subject rather than <see cref="UnreadRows"/>, because a merged response can run both
    /// families in that mode and one subject standing for two families' rows is a double count.</summary>
    ScriptScanRows,

    /// <summary>Rows of validate_scripts' <c>counts_only</c> histogram, by property NAME.</summary>
    HistogramByProperty,

    /// <summary>The dialogue family's per-seed heads — one per seed that RESOLVED. Its analogue of
    /// <see cref="PluginSections"/>, in the units this family selects by: a seed, not a plugin.</summary>
    DialogueSeeds,

    /// <summary>The dialogue family's per-topic blocks — the rows under a seed head, and its analogue of
    /// <see cref="ScriptRecords"/>. A block is emitted whole or not at all for that subject's reason: everything
    /// inside one is a finding in its own right, and a per-line "append if it fits" drops them with nothing
    /// accounting for the loss.
    ///
    /// <para>A block's width is computable before it is written, in both transports: composing the blocks
    /// independently and concatenating them is byte-identical to a one-pass render, and every json row's pre-write
    /// cost bounds its write. The allocation depends on that holding.</para></summary>
    DialogueTopics,

    /// <summary>The dialogue family's honesty layer: seeds that could NOT be validated, one row each. Its own
    /// subject and present in BOTH lanes, unlike the heads and blocks — a seed nobody could reach is the boundary of
    /// the answer, not a finding inside it, so <c>counts_only</c> must not silence it either.</summary>
    DialogueSeedRefusals,
}

/// <summary>
/// Which subjects are histogram axes. Deliberately not declared accounting subjects: an axis discloses its own cut
/// in both transports (<see cref="HistogramCut"/>), and stating one fact twice lets the two disagree. They are
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
/// <para>Computed once from the emitting loop's own facts and consumed by both transports — the text lane renders
/// <see cref="Line"/>, the json lane writes the same two facts as fields — so one cut cannot read differently in
/// the two.</para>
///
/// <para>The knob named is the one that stopped the axis: "raise limit=" over rows the response had no room for
/// moves nothing, and so does "raise max_chars=" over rows the row budget refused.</para>
/// </summary>
internal readonly record struct HistogramCut(int Remaining, bool ByBudget)
{
    /// <summary>This axis's cut, or null where it rendered every row it had.</summary>
    internal static HistogramCut? For(int distinct, int shown, bool byBudget)
        => shown >= distinct ? null : new HistogramCut(distinct - shown, byBudget);

    /// <summary>The knob a caller raises to see these rows, spelled as the parameter is.</summary>
    internal string Knob => ByBudget ? "max_chars" : "limit";

    /// <summary>The text lane's spelling, in one place: it is composed twice, once to measure the room held back
    /// for it and once to write it, and two spellings would leave the reserve covering a different sentence.
    /// </summary>
    internal string Line => "  ... [" + Remaining + " more row(s) — raise " + Knob + "= to see them]\n";
}

/// <summary>
/// ONE counts_only histogram axis, as both the RESERVE and the RENDER need it.
///
/// <para>They read the same object for the same reason the cut line has one spelling: room measured off a
/// different title or row count is not a reserve for the sentence that gets written.</para>
/// </summary>
/// <param name="Rows">the axis's tally, or null where the mode was not requested — an absent axis and an empty one
/// are different answers and must not render alike.</param>
/// <param name="NotComputed">what to say instead when <paramref name="Rows"/> is null: the axis's whole answer, and
/// fixed text that does not grow with the findings.</param>
internal readonly record struct HistogramAxis(SweepSubject Subject, IReadOnlyList<SweepCount>? Rows, string Title,
                                              string? Note = null, string? NotComputed = null)
{
    /// <summary>The axis's section head. Ridden by its first row, so a title never stands over nothing.</summary>
    internal string Head => "\n" + Title + " (" + (Rows?.Count ?? 0) + " distinct):\n";

    /// <summary>What an axis with nothing to tally says. It carries the title, so two empty axes do not render as
    /// two identical untitled sentences.</summary>
    internal string EmptyLine => "\n" + Title + ": nothing to tally — no findings in the swept scope.\n";

    /// <summary>The axis's note, spelled once. It is written whatever the budget says, so its room is held back
    /// with the closing disclosure rather than taken out of the body's, and the render and the reserve read this one
    /// string.</summary>
    internal string NoteLine => Note is null ? "" : "\n" + Note + "\n";

    /// <summary>What this axis says instead of a tally when the mode was not requested. Unconditional, and so also
    /// reserved: it is the axis's whole answer, and an answer a budget can drop leaves the caller unable to tell it
    /// from a tally that came back empty.</summary>
    internal string NotComputedLine => Rows is null && NotComputed is not null ? NotComputed + "\n" : "";

    /// <summary>The axis's irreducible disclosure, in text-lane characters: the widest thing
    /// <see cref="BoundedBody.Close"/> can be asked to write for it, which is its head plus a cut line naming every
    /// row — the case where the budget admitted no rows at all. An axis that renders all of its rows gives the room
    /// back (<see cref="BoundedBody.Release"/>).</summary>
    internal int TextDisclosure
        => Rows is null ? 0
         : Rows.Count == 0 ? EmptyLine.Length
         : Head.Length + new HistogramCut(Rows.Count, ByBudget: true).Line.Length;

    /// <summary>Everything this axis puts in the response's fixed part, in text-lane characters: its unconditional
    /// lines plus its closing disclosure. One reserve for the lot, because they are one thing — what this axis
    /// writes whatever the budget says.
    ///
    /// <para>The notes are reserved even though the only noted axis today is the first one, whose note is written
    /// before any axis has spent anything: that equality is a property of the current axis order, and a later axis
    /// carrying a note would write it after the budget was empty and land past the cap.</para></summary>
    internal int TextFixed => NoteLine.Length + NotComputedLine.Length + TextDisclosure;
}

/// <summary>
/// The one place either sweep transport appends anything the caller's <c>max_chars</c> can refuse. Every body write
/// goes through <see cref="Emit"/>, so the bound is enforced here rather than promised by each caller.
///
/// <para>A caller passes a cost only where one unit can be large; everywhere else the cost is zero and the test
/// degenerates to "is there room at all". That is enough, because the response's length only ever grows: the first
/// unit whose declared cost was too small takes it past the budget, and from that moment every test in every
/// subject compares against a length already over. So the damage of a forgotten cost is one unit.</para>
///
/// <para>A response has exactly two ways of telling a caller what it left out, and neither is emitted through this
/// helper: the accounting (<see cref="CheckAccounting"/>), written inside a reserve, and a subject's own closing
/// disclosure, written by <see cref="Close"/> out of room <see cref="Reserve"/> held back before the body rendered.
/// The histogram axes are not declared accounting subjects (see <see cref="SweepSubjects"/>), so only the second
/// route covers them — and a disclosure the budget can refuse is no disclosure, since the pressure that cut the
/// rows would cut the line reporting the cut.</para>
///
/// <para>The fixed part is outside the emission gate entirely: the header, every axis's unconditional lines, every
/// reserved closing disclosure, the accounting and the boundary. A cap too small for it gets an overrun notice that
/// says so (<see cref="CheckAccounting.CapTooSmall"/>), never a shorter response. Those writes still come through
/// this class — <see cref="Fixed"/> and <see cref="Close"/> — so the fixed part is measured at the write rather
/// than assembled from what each site remembered to declare.</para>
/// </summary>
internal sealed class BoundedBody
{
    readonly int _budget;
    readonly Func<int> _length;
    readonly IReadOnlyList<CheckAccounting> _accts;
    readonly HashSet<SweepSubject> _stopped = new();
    readonly Dictionary<SweepSubject, int> _held = new();
    readonly IReadOnlyList<(SweepFamily Family, IReadOnlyList<SweepSubject> Subjects)>? _plan;
    readonly BodyAllocation _alloc;
    bool _skeleton;

    /// <param name="acct">the accounting to register emissions with, or null for a lane that keeps none. The
    /// single-family shape, which is every lane but the merged sweep.</param>
    /// <param name="budget">the chars the BODY may occupy: the caller's max_chars less the accounting's reserve.</param>
    /// <param name="length">what the response has emitted so far, in the transport's own unit.</param>
    /// <param name="plan">the families this response renders and which of each family's subjects have rows, or null
    /// for a lane that divides nothing (a single-family response has no siblings to be fair to, and the global
    /// budget alone is then the whole rule). See <see cref="BodyAllocation"/>.</param>
    internal BoundedBody(CheckAccounting? acct, int budget, Func<int> length,
                         IReadOnlyList<(SweepFamily Family, IReadOnlyList<SweepSubject> Subjects)>? plan = null,
                         IReadOnlyDictionary<SweepSubject, int>? demand = null, int reservedForRows = 0)
        : this(acct is null ? Array.Empty<CheckAccounting>() : new[] { acct }, budget, length, plan,
               demand, reservedForRows) { }

    /// <summary>A merged response's body. A static factory rather than a second constructor, so a lane passing a
    /// bare <c>null</c> accounting is not an ambiguous call.</summary>
    /// <param name="accts">one accounting per family, all reading the same emissions — the numbers an accounting
    /// states are per-family. Registering with every accounting is safe because
    /// <see cref="CheckAccounting.Emitted"/> ignores a subject its lane did not declare, and the one subject two
    /// families could both declare, the excluded-plugin roster, is declared by exactly one of them.</param>
    /// <param name="demand">each planned subject's measured demand (<see cref="BodyAllocation"/>). Omitted, every
    /// planned subject is unconstrained, so nothing is allocated zero by accident.</param>
    /// <param name="reservedForRows">what this response will hold back for fixed parts, known before the render.
    /// The allocation divides the body budget less this, and it is passed rather than read off <see cref="Held"/>
    /// because the reserves are taken during the render, one family at a time.</param>
    /// <param name="responseSubjects">the subjects that belong to the response rather than to any family — the
    /// excluded-plugin roster. They take part in the fill beside the families, so the roster has a share it can
    /// spend rather than a reserve standing against it.</param>
    internal static BoundedBody ForFamilies(IReadOnlyList<CheckAccounting> accts, int budget, Func<int> length,
                                            IReadOnlyList<(SweepFamily Family, IReadOnlyList<SweepSubject> Subjects)>? plan = null,
                                            IReadOnlyDictionary<SweepSubject, int>? demand = null,
                                            int reservedForRows = 0,
                                            IReadOnlyList<SweepSubject>? responseSubjects = null,
                                            int reserveDemanded = 0)
        => new(accts, budget, length, plan, demand, reservedForRows, responseSubjects, reserveDemanded);

    /// <summary>A body that admits exactly one unit of each subject and refuses the rest — for the pass that
    /// measures a response's fixed part.
    ///
    /// <para>The allocation has to exclude the fixed part or it divides room that does not exist. The number is
    /// measured, not assembled: the caller composes the whole response through this body, and what comes back, less
    /// what those units wrote (<see cref="BodyTotal"/>) and less what came out of the reserve
    /// (<see cref="ReservedWritten"/>), is the fixed part. Nothing enumerates the unconditional write sites, which
    /// is the point.</para>
    ///
    /// <para>One unit rather than none, because some frames are only written in their rendered shape once a subject
    /// has emitted something — a json array closes on the same line when it is <c>[]</c> and on its own indented
    /// line when it is not. Subtracting what those units wrote takes the units themselves back out. More than one
    /// would change the number not at all, and one keeps this pass O(subjects) rather than O(all rows) on a sweep
    /// carrying six figures of findings.</para>
    ///
    /// <para>What the skeleton cannot compose is the part that varies with the cut — a closing disclosure says a
    /// different thing at a different length depending on what fit. Those go through <see cref="Reserve"/> as an
    /// upper bound, and the caller subtracts <see cref="ReservedWritten"/> so the two mechanisms do not both charge
    /// for the same characters.</para></summary>
    internal static BoundedBody Skeleton(IReadOnlyList<CheckAccounting> accts, Func<int> length)
        => new(accts, budget: 0, length, plan: null, demand: null, reservedForRows: 0) { _skeleton = true };

    BoundedBody(IReadOnlyList<CheckAccounting> accts, int budget, Func<int> length,
                IReadOnlyList<(SweepFamily Family, IReadOnlyList<SweepSubject> Subjects)>? plan,
                IReadOnlyDictionary<SweepSubject, int>? demand, int reservedForRows,
                IReadOnlyList<SweepSubject>? responseSubjects = null, int reserveDemanded = 0)
    {
        _accts = accts;
        _budget = budget;
        _length = length;
        _plan = plan;
        _reservedForRows = reservedForRows;
        ReserveDemanded = reserveDemanded;
        // Built here, before anything is written: water-filling over measured demand is a function of the budget
        // and the demands alone, so building it at the first unit would make it a function of render order.
        _alloc = new BodyAllocation(budget - reservedForRows,
                                    plan ?? Array.Empty<(SweepFamily, IReadOnlyList<SweepSubject>)>(), demand,
                                    responseSubjects);
    }

    /// <summary>What the caller measured, before the render, that this response owes outside its units: the fixed
    /// part and the reserves. The allocation divides the budget less this; <see cref="Outstanding"/> holds the same
    /// number against the global emission test, so the two are one budget.</summary>
    readonly int _reservedForRows;

    /// <summary>The allocation, built in the constructor from measured demand — see <see cref="BodyAllocation"/>
    /// for why it cannot be built at the first write.</summary>
    BodyAllocation Allocation => _alloc!;

    /// <summary>What one subject was allocated.</summary>
    internal int AllocationOf(SweepSubject s) => Allocation.AllocationOf(s);

    /// <summary>The chars the whole body may occupy — the cap less what the response reserved for its accountings
    /// and boundaries.</summary>
    internal int Budget => _budget;

    /// <summary>What the caller measured, before the render, that this response owes outside its units. Equal to
    /// <see cref="OutstandingHigh"/> exactly when that measurement was not exceeded. Not derivable from
    /// <see cref="RowBudget"/>, which clamps at zero when the fixed part alone is wider than the budget.</summary>
    internal int ReservedForRows => _reservedForRows;

    /// <summary>What the demand pass said this render would reserve — the reserve half of
    /// <see cref="_reservedForRows"/>, kept separately because that field adds the fixed part to it and the fixed
    /// part is measured by a different mechanism. Compared against <see cref="ReserveDeclared"/>.</summary>
    internal int ReserveDemanded { get; }

    /// <summary>The row budget — the room the allocation divided, and the room the units may spend together.
    /// <see cref="BodyTotal"/> never exceeding it is what makes the response-wide emission test and the allocation
    /// one budget rather than two.</summary>
    internal int RowBudget => Math.Max(0, _budget - _reservedForRows);

    /// <summary>The most this response ever owed outside its units, across every emission test — a running
    /// high-water mark taken inside <see cref="Emit"/>, because <see cref="Outstanding"/> reads the live response
    /// and the json lane's stream is closed by the time anything can ask afterwards.
    ///
    /// <para>Equalling <c>reservedForRows</c> is what says the up-front measurement was not exceeded, and therefore
    /// that the response-wide test never bit before the allocation did.</para></summary>
    internal int OutstandingHigh { get; private set; }

    /// <summary>What one subject actually spent, charged unit by unit as each landed. On a response with nothing
    /// cut this equals <see cref="AllocationOf"/>; a demand measurement that drifted from what the render writes
    /// shows up as the gap.</summary>
    internal int SpentOn(SweepSubject s) => Allocation.SpentOn(s);

    /// <summary>What of the response so far is charged against the BODY's budget: everything written, less what was
    /// written out of the reserve. A merged response writes a family's accounting line before the next family
    /// renders, and that line's room was subtracted from the budget once already — counted again here it would be
    /// charged twice, and the second family would pay for a sentence the first one's reserve had bought.</summary>
    int Spent => _length() - _reservedSpent;
    int _reservedSpent;

    /// <summary>What this response has already spent that is not a unit, plus what it is still holding — the term
    /// the response-wide emission test stands the units against.
    ///
    /// <para>One measured quantity, never maxed with the up-front measurement: if the fixed part is ever
    /// under-measured, this leaves a response over its cap, which the overrun notice names, rather than paying for
    /// it silently out of one subject's last unit.</para>
    ///
    /// <para>Subtracting <see cref="BodyTotal"/> is what keeps the units answering to
    /// <c>budget − reservedForRows</c>, which is exactly what the allocation divides. Counting the fixed part here
    /// as it lands would charge it twice — once to the allocation and again to the rows — and whichever subject
    /// rendered last would pay the difference, which is the order-dependence water-filling exists to remove.</para>
    /// </summary>
    int Outstanding => Spent - BodyTotal + Held;

    /// <summary>Write text whose room was already held back out of the body budget — a family's accounting line, in
    /// a merged response where a later family still has to render. It is not a unit, so it is not registered and it
    /// cannot be refused; what it appends is measured and discounted from what the body is charged with, so a
    /// reserve spent where it was meant to be spent does not also come out of the rows.</summary>
    internal void Reserved(Action commit)
    {
        int before = _length();
        commit();
        int wrote = _length() - before;
        _reservedSpent += wrote;
        ReservedWritten += wrote;
        ReservedWrittenByAccountings += wrote;
    }

    /// <summary>What this response has written out of the reserve — through <see cref="Reserved"/>,
    /// <see cref="Fixed"/> and <see cref="Close"/>. The skeleton pass subtracts it: the room for those writes was
    /// already held back out of <c>max_chars</c>, so counting them again would take the same characters out of the
    /// rows twice.</summary>
    internal int ReservedWritten { get; private set; }

    /// <summary>The same total, split by the path that wrote it — accountings and boundaries through
    /// <see cref="Reserved"/>, the axes' unconditional frames through <see cref="Fixed"/>, closing disclosures
    /// through <see cref="Close"/>. A reserve is an upper bound, so the difference between what was held and what
    /// was written is room no row can use, and this says which holder is sitting on it. Diagnostics only — nothing
    /// in the response branches on them.</summary>
    internal int ReservedWrittenByAccountings { get; private set; }
    internal int ReservedWrittenByAxisFrames { get; private set; }
    internal int ReservedWrittenByDisclosures { get; private set; }

    /// <summary>Emit one unit of <paramref name="subject"/>, or refuse. Returns false when the unit did not fit —
    /// the caller's loop breaks and the accounting already knows, because the count it will report is the count of
    /// units that came back true.</summary>
    /// <param name="cost">an upper bound on what <paramref name="commit"/> will append, or 0 where the site has no
    /// cheap way to measure one — see the class summary for why a zero here bounds the damage at one unit.</param>
    /// <param name="source">for <see cref="SweepSubject.DanglingEntries"/>, the plugin the entry came from — the
    /// by-source roster is tallied off the same registration as the count, so the two cannot disagree.</param>
    internal bool Emit(SweepSubject subject, int cost, Action commit, string? source = null)
    {
        // The fixed-part pass admits one unit of each subject and refuses the rest; the caller subtracts what those
        // units wrote (BodyTotal). One rather than none, because a json array's frame is wider when it holds
        // something — an empty `"plugins": []` closes on the same line, a populated one on its own indented line.
        if (_skeleton)
        {
            if (!_skeletonFirst.Add(subject)) { Stop(subject); return false; }
            Write(subject, commit, source);
            return true;
        }
        if (_stopped.Contains(subject)) return false;
        int outstanding = Outstanding;
        OutstandingHigh = Math.Max(OutstandingHigh, outstanding);
        if (BodyTotal + cost + outstanding > _budget) { Stop(subject); return false; }
        // The subject's own share, on top of the response-wide test rather than instead of it. A subject may spend
        // its ceiling and no more even while the response has room to spare: that room belongs to the siblings that
        // have not rendered yet.
        if (!Allocation.Fits(subject, cost)) { Stop(subject); return false; }
        Write(subject, commit, source);
        return true;
    }

    /// <summary>Commit one admitted unit: write it, measure what it wrote, and charge that. Charged with what it
    /// actually wrote, never with the declared cost — the cost is only a test before a write, and most sites
    /// declare 0.</summary>
    void Write(SweepSubject subject, Action commit, string? source)
    {
        int before = _length();
        commit();
        int wrote = _length() - before;
        BodyTotal += wrote;
        Allocation.Charge(subject, wrote);
        foreach (var a in _accts) a.Emitted(subject, source);
    }

    /// <summary>Which subjects the fixed-part pass has already let one unit through for.</summary>
    readonly HashSet<SweepSubject> _skeletonFirst = new();

    /// <summary>This subject emits nothing further. Told to the allocation as well as recorded here, so the room
    /// it did not spend is back in the arithmetic for the siblings after it.</summary>
    void Stop(SweepSubject subject)
    {
        _stopped.Add(subject);
        _alloc?.Done(subject);
    }

    /// <summary>Finish a unit already admitted — the closing brackets a bounded json section owes once the rows
    /// nested inside it have been written. Unconditional (refusing it would leave a half-written object) and
    /// charged to the same subject the opening was, because the two are one unit.
    ///
    /// <para>Charged rather than treated as fixed: these closers scale with how many units rendered, so the
    /// skeleton pass cannot see them, and a fixed part missing them is a row budget larger than the room that
    /// exists. Charged here, the unit's measured demand covers its whole footprint.</para></summary>
    internal void Complete(SweepSubject subject, Action commit)
    {
        int before = _length();
        commit();
        int wrote = _length() - before;
        BodyTotal += wrote;
        Allocation.Charge(subject, wrote);
    }

    /// <summary>What the body actually appended, measured at each unit as it landed — the declared cost is a budget
    /// test, never this number. It is the only quantity that separates a response's body from its fixed part, so it
    /// is taken here rather than reconstructed from the counts the accounting keeps.</summary>
    internal int BodyTotal { get; private set; }

    /// <summary>Hold back the room one subject writes whatever the budget says — its unconditional lines and its
    /// closing disclosure — before any unit is emitted. Every emission test then leaves that room standing, so by
    /// the time the subject writes, what it writes is already paid for.
    ///
    /// <para>It must happen before the first unit of ANY subject: an axis that reserved its own room after a
    /// sibling had already spent the budget would be refused the very lines this makes unrefusable.</para></summary>
    internal void Reserve(SweepSubject subject, int cost)
    {
        _held.TryGetValue(subject, out int prior);
        ReserveDeclared += cost - prior;   // the DELTA, so this stays the sum of what is held whatever a caller re-reserves
        _held[subject] = cost;
    }

    /// <summary>The room this render actually held back through <see cref="Reserve"/>, accumulated as it was
    /// declared and unaffected by <see cref="Release"/> or <see cref="Close"/> giving it back afterwards.
    ///
    /// <para>It exists to be compared with <see cref="ReserveDemanded"/>: the demand pass subtracts a reserve from
    /// the row budget before the render and the render then holds one, so the two are the same promise measured
    /// twice and have to be one number. A demand pass that charges for an axis the render does not reserve for
    /// takes row room for objects the response never opens.</para></summary>
    internal int ReserveDeclared { get; private set; }

    /// <summary>This response's fixed part: every char it carries whatever the budget says — the header, each
    /// axis's unconditional lines, each closing disclosure, the accounting and the boundary.
    ///
    /// <para>It is subtracted, not assembled. Everything a cap can refuse goes through <see cref="Emit"/> and is
    /// measured there (<see cref="BodyTotal"/>), so the fixed part is the finished response minus that, plus room
    /// still held that no unit was allowed to touch. Nothing here enumerates the unconditional write sites, which
    /// is the point: the overrun notice branches on this number, and a roster of sites always misses one.</para>
    ///
    /// <para>It deliberately excludes the overrun notice, which is why the caller passes the length it measured:
    /// the notice is gone the moment the response fits, so counting it would tell a caller to buy room for a
    /// sentence they are paying to remove.</para></summary>
    /// <param name="contentLength">the finished response as the transport measured it, without the notice.</param>
    internal int FixedPart(int contentLength) => contentLength - BodyTotal + Held;

    /// <summary>Room reserved and not yet spent. Held against every emission test, including tests of the subject
    /// that reserved it — a subject may spend the budget on its rows, never on what it writes regardless.</summary>
    int Held
    {
        get { int n = 0; foreach (var v in _held.Values) n += v; return n; }
    }

    /// <summary>Write part of a subject's reserved, unconditional text now — an axis's note, a json axis object's
    /// own frame. It is not a unit, so it is not registered and it cannot be refused; what it appends is measured
    /// and charged against the room already held for this subject, so the write and the room held for it are not
    /// charged twice.</summary>
    internal void Fixed(SweepSubject subject, Action commit)
    {
        int before = _length();
        commit();
        int wrote = _length() - before;
        ReservedWritten += wrote;
        ReservedWrittenByAxisFrames += wrote;
        if (_held.TryGetValue(subject, out var held)) _held[subject] = Math.Max(0, held - wrote);
    }

    /// <summary>Write a subject's own closing disclosure — the line that says how much of it did not fit. It is not
    /// a unit, so it is not registered, and it is never refused: not because the subject stopped (a subject that
    /// stopped is exactly when this has to be said), and not on the budget either, since the pressure that cut the
    /// rows would cut the line reporting the cut. The room came out of <see cref="Reserve"/> before the body
    /// rendered, which is what makes writing it unconditionally safe.</summary>
    internal void Close(SweepSubject subject, Action commit)
    {
        _held.Remove(subject);   // spent here, and only here
        _alloc?.Done(subject);   // and its share is finished with too, whatever it did not spend
        int before = _length();
        commit();
        int wroteClose = _length() - before;
        ReservedWritten += wroteClose;
        ReservedWrittenByDisclosures += wroteClose;
        _stopped.Add(subject);   // nothing follows a subject's closing disclosure
    }

    /// <summary>Give a subject's remaining reserved room back unspent — for a subject that rendered everything it
    /// had and has nothing left to say. Without this the room stays held against every later subject's emission
    /// test, so subjects after a complete histogram would pay for a sentence nobody wrote.</summary>
    internal void Release(SweepSubject subject)
    {
        _held.Remove(subject);
        // The allocation is told too, but under water-filling that is a no-op by design: a subject's share was
        // never anyone else's to inherit (BodyAllocation.Done).
        _alloc?.Done(subject);
    }

    /// <summary>Did this subject stop short? For the one caller that states the fact in its own words rather than
    /// through the accounting (validate_scripts).</summary>
    internal bool Stopped(SweepSubject subject) => _stopped.Contains(subject);
}
