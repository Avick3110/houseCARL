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

    /// <summary>The dialogue family's per-seed heads — one per seed that RESOLVED. Its analogue of
    /// <see cref="PluginSections"/>, in the units this family selects by: a seed, not a plugin.</summary>
    DialogueSeeds,

    /// <summary>The dialogue family's per-topic blocks — the rows under a seed head, and its analogue of
    /// <see cref="ScriptRecords"/>. A block is emitted WHOLE or not at all for that subject's reason: everything
    /// inside one (the graph issues, the silent voice lines, the result scripts that will not fire) is a finding in
    /// its own right, and a per-line "append if it fits" drops them with nothing accounting for the loss.
    ///
    /// <para>MEASURED width-computable before the write, in both transports, before this family was built: 237 units
    /// of a 235-topic quest composed independently and concatenated are byte-identical to the one-pass render, and
    /// every json row's pre-write cost bounds its write (<c>dialogue-width-measure</c>, live ARR 2.0 order,
    /// 2026-08-21). The #394 ruling made the allocation shape conditional on that answer.</para></summary>
    DialogueTopics,

    /// <summary>The dialogue family's honesty layer: seeds that could NOT be validated, one row each. Its own
    /// subject and present in BOTH lanes, unlike the heads and blocks — a seed nobody could reach is the boundary of
    /// the answer, not a finding inside it, so <c>counts_only</c> must not silence it either.</summary>
    DialogueSeedRefusals,
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

    /// <summary>A merged response's body. A static factory rather than a second constructor: a lane passing a bare
    /// <c>null</c> accounting would otherwise be an ambiguous call, and disambiguating it with a cast at every such
    /// site is a worse trade than naming the multi-family case.</summary>
    /// <param name="accts">ONE ACCOUNTING PER FAMILY, all reading the same emissions. 4a's invariant is one source
    /// per sentence across the two TRANSPORTS, not one accounting across all families: the numbers an accounting
    /// states are per-family, and a merged one would have to name which family each number belonged to. Registering
    /// with every accounting is safe by construction because <see cref="CheckAccounting.Emitted"/> ignores a subject
    /// its lane did not declare — and the one subject two families could both declare, the excluded-plugin roster,
    /// is declared by exactly one of them (it is a fact about the SCOPE, emitted once per response).</param>
    /// <param name="demand">each planned subject's MEASURED demand (<see cref="BodyAllocation"/>). Omitted, every
    /// planned subject is unconstrained, which is the honest reading of "nobody measured" and never allocates a
    /// subject zero by accident.</param>
    /// <param name="reservedForRows">what this response will hold back for fixed parts, known BEFORE the render.
    /// The allocation divides the body budget less this, and it is passed rather than read off <see cref="Held"/>
    /// because the reserves are taken DURING the render, one family at a time — an allocation built at the first
    /// write divided room that families rendering later had not yet claimed.</param>
    /// <param name="responseSubjects">the subjects that belong to the RESPONSE rather than to any family — the
    /// excluded-plugin roster. They are a top-level participant in the fill beside the families, so the roster has
    /// a share it can actually spend rather than a reserve the emission test holds standing against it.</param>
    internal static BoundedBody ForFamilies(IReadOnlyList<CheckAccounting> accts, int budget, Func<int> length,
                                            IReadOnlyList<(SweepFamily Family, IReadOnlyList<SweepSubject> Subjects)>? plan = null,
                                            IReadOnlyDictionary<SweepSubject, int>? demand = null,
                                            int reservedForRows = 0,
                                            IReadOnlyList<SweepSubject>? responseSubjects = null)
        => new(accts, budget, length, plan, demand, reservedForRows, responseSubjects);

    /// <summary>A body that admits exactly ONE UNIT OF EACH SUBJECT and refuses the rest — for the pass that
    /// measures a response's FIXED PART.
    ///
    /// <para>The fixed part is everything a response writes whatever the budget says, and the allocation has to
    /// exclude all of it or it divides room that does not exist (measured: the merged sweep's title, scope sentence
    /// and per-family heads came to hundreds of characters the row budget still counted as row room, so the global
    /// test bit before any subject reached its share and render order decided who lost). The number is MEASURED,
    /// not assembled: the caller composes the WHOLE response through this body — the same title, the same heads,
    /// the same one-source composers — and what comes back, less what those units wrote
    /// (<see cref="BodyTotal"/>) and less what came out of the reserve (<see cref="ReservedWritten"/>), is the
    /// fixed part. Nothing enumerates the unconditional write sites, which is the point: every version of that
    /// number that was assembled from a roster of sites came out wrong in a way nothing could see
    /// (<see cref="FixedPart"/> carries that history).</para>
    ///
    /// <para><b>Why ONE unit rather than none.</b> Some of what a response owes regardless is only written in the
    /// shape it takes when a subject has rendered something: a json array closes on the same line when it is
    /// <c>[]</c> and on its own indented line when it is not, so a fixed part measured with every array empty is
    /// short by that on every array the response opens. Measured at eight characters on a three-family listing
    /// document — enough for the response-wide test to bite before the allocation did, which cost the
    /// last-rendering subject a unit AT A WIDER CAP than it had rendered at. Admitting one unit each puts every
    /// frame in its rendered shape; subtracting what those units wrote takes the units themselves back out.</para>
    ///
    /// <para>What the skeleton CANNOT compose is the part that varies with the cut — a closing disclosure says a
    /// different thing at a different length depending on what fit. Those go through <see cref="Reserve"/> as an
    /// upper bound exactly as before, and <see cref="ReservedWritten"/> is what the caller subtracts so the two
    /// mechanisms do not both charge for the same characters.</para></summary>
    internal static BoundedBody Skeleton(IReadOnlyList<CheckAccounting> accts, Func<int> length)
        => new(accts, budget: 0, length, plan: null, demand: null, reservedForRows: 0) { _skeleton = true };

    BoundedBody(IReadOnlyList<CheckAccounting> accts, int budget, Func<int> length,
                IReadOnlyList<(SweepFamily Family, IReadOnlyList<SweepSubject> Subjects)>? plan,
                IReadOnlyDictionary<SweepSubject, int>? demand, int reservedForRows,
                IReadOnlyList<SweepSubject>? responseSubjects = null)
    {
        _accts = accts;
        _budget = budget;
        _length = length;
        _plan = plan;
        _reservedForRows = reservedForRows;
        // BUILT HERE, before anything is written. The old rule built it lazily at the first unit any subject
        // emitted, which made it a function of render order; water-filling over measured demand is a function of
        // the budget and the demands alone, so the only correct moment to build it is before the render.
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

    /// <summary>What one subject was allocated — read by the arms, which assert against the allocation rather than
    /// against a phrase the render printed.</summary>
    internal int AllocationOf(SweepSubject s) => Allocation.AllocationOf(s);

    /// <summary>The chars the whole BODY may occupy — the cap less what the response reserved for its accountings
    /// and boundaries. Read by the arms beside <see cref="RowBudget"/>.</summary>
    internal int Budget => _budget;

    /// <summary>What the caller MEASURED, before the render, that this response owes outside its units. Read by the
    /// arms beside <see cref="OutstandingHigh"/>: the two being equal is what says the up-front measurement was
    /// not exceeded. Not derivable from <see cref="RowBudget"/>, which clamps at zero when the fixed part alone
    /// is wider than the budget.</summary>
    internal int ReservedForRows => _reservedForRows;

    /// <summary>THE ROW BUDGET — the room the allocation divided, and the room the units may spend together. Read
    /// by the arms: <see cref="BodyTotal"/> never exceeding it is what makes the global emission test and the
    /// allocation one budget rather than two.</summary>
    internal int RowBudget => Math.Max(0, _budget - _reservedForRows);

    /// <summary>THE MOST this response ever owed outside its units, across every emission test — a running
    /// high-water mark taken inside <see cref="Emit"/>, because <see cref="Outstanding"/> reads the live response
    /// and the json lane's stream is closed by the time an arm asks.
    ///
    /// <para>Read by <c>MATRIX-ONE-BUDGET</c>, which is the arm that holds the
    /// whole "one budget" property: this equalling <c>reservedForRows</c> is what says the up-front measurement was
    /// not exceeded, and therefore that the response-wide test never bit before the allocation did. When it did
    /// exceed it — by eight characters, on a three-family json document whose arrays the fixed-part pass had
    /// measured empty — the last-rendering subject lost a unit at a WIDER cap than it had rendered at.</para></summary>
    internal int OutstandingHigh { get; private set; }

    /// <summary>What one subject actually SPENT, charged unit by unit as each landed. Read beside
    /// <see cref="AllocationOf"/> by <c>ALLOCATION-EQUALS-SPEND</c>: on a response with nothing cut the two are the
    /// same number, and a measurement that drifted from what the render writes shows up here as the gap.</summary>
    internal int SpentOn(SweepSubject s) => Allocation.SpentOn(s);

    /// <summary>What of the response so far is charged against the BODY's budget: everything written, less what was
    /// written out of the reserve. A merged response writes a family's accounting line before the next family
    /// renders, and that line's room was subtracted from the budget once already — counted again here it would be
    /// charged twice, and the second family would pay for a sentence the first one's reserve had bought.</summary>
    int Spent => _length() - _reservedSpent;
    int _reservedSpent;

    /// <summary>WHAT THIS RESPONSE HAS ALREADY SPENT that is not a unit, plus what it is still holding — the term
    /// the response-wide emission test stands the units against.
    ///
    /// <para>ONE MEASURED quantity: what this render has actually written outside its units so far, plus what is
    /// still <see cref="Held"/>. It is compared against the up-front measurement by <c>MATRIX-ONE-BUDGET</c> rather
    /// than maxed with it here — taking the greater was tried, and sabotaging that arm away left every guard green,
    /// because the fixed-part pass measures the number exactly and the two are equal wherever both exist. A branch
    /// no fixture can tell from its sibling is one that goes (PR #339's rule). If the fixed part is ever
    /// under-measured again, this is a response over its cap, which the overrun notice NAMES — where the maxed
    /// version would have paid for it silently, in one subject's last unit.</para>
    ///
    /// <para><b>Why this is not the old test with a term added.</b> The old test was
    /// <c>Spent + cost + Held &gt; budget</c>, where <c>Spent</c> counts the fixed part AS IT LANDS. The allocation
    /// divides <c>budget − reservedForRows</c>, so the fixed part was subtracted from the rows once and charged to
    /// them again as it was written — two budgets, and whichever subject rendered LAST paid the difference. That is
    /// the order-dependence water-filling exists to remove, and it was still deciding outcomes: measured on a
    /// three-family json response, one more unread row landing at <c>max_chars=5573</c> cost the scan-error rows one
    /// of theirs, so a subject spent 125 chars at a WIDER cap than the 251 it spent at 5,572. It also let the
    /// excluded-plugin roster — the one emitted subject no family's plan governs — spend the whole body budget
    /// before the first family head was written, and the fixed part then went past the cap (4,494 chars against
    /// 4,000). With this term the units answer to <c>budget − reservedForRows</c>, which is exactly what the
    /// allocation divides.</para>
    ///
    /// <para>Read with <see cref="BodyTotal"/> in front of it the test is <c>Spent + cost + Held</c> —
    /// textually what it always was. What changed is not this term but what it is now true OF: every emitted
    /// subject of a merged response is governed by an allocation dividing <c>budget − reservedForRows</c>, so
    /// the response-wide test is a backstop rather than the thing deciding who loses. The single-family
    /// ancestor renders, which measure no fixed part and govern nothing, still lean on it entirely.</para></summary>
    int Outstanding => Spent - BodyTotal + Held;

    /// <summary>Write text whose room was already held back OUT of the body budget — a family's accounting line, in
    /// a merged response where a later family still has to render. It is not a unit, so it is not registered and it
    /// cannot be refused; what it appends is MEASURED and discounted from what the body is charged with, so a
    /// reserve spent where it was meant to be spent does not also come out of the rows.</summary>
    internal void Reserved(Action commit)
    {
        int before = _length();
        commit();
        int wrote = _length() - before;
        _reservedSpent += wrote;
        ReservedWritten += wrote;
    }

    /// <summary>What this response has written OUT OF THE RESERVE — through <see cref="Reserved"/>,
    /// <see cref="Fixed"/> and <see cref="Close"/>. Read by the SKELETON pass, which measures the fixed part as
    /// "everything the composition wrote with no units in it" and has to subtract the reserved writes from it: the
    /// room for those was already held back out of <c>max_chars</c>, so counting them again would take the same
    /// characters out of the rows twice.</summary>
    internal int ReservedWritten { get; private set; }

    /// <summary>Emit one unit of <paramref name="subject"/>, or refuse. Returns false when the unit did not fit —
    /// the caller's loop breaks and the accounting already knows, because the count it will report is the count of
    /// units that came back true.</summary>
    /// <param name="cost">an UPPER BOUND on what <paramref name="commit"/> will append, or 0 where the site has no
    /// cheap way to measure one — see the class summary for why a zero here bounds the damage at one unit.</param>
    /// <param name="source">for <see cref="SweepSubject.DanglingEntries"/>, the plugin the entry came FROM — the
    /// by-source roster is tallied off the same registration as the count, so the two cannot disagree.</param>
    internal bool Emit(SweepSubject subject, int cost, Action commit, string? source = null)
    {
        // THE FIXED-PART PASS admits exactly ONE unit of each subject and refuses the rest, and the caller subtracts
        // what those units wrote (BodyTotal). One unit rather than none, because a json array's own frame is WIDER
        // when it holds something — an empty `"plugins": []` closes on the same line, a populated one closes on its
        // own indented line — and a fixed part measured with every array empty is short by that on every array the
        // response opens. Measured: eight characters short on a three-family listing document, which is enough to
        // make the response-wide test bite before the allocation does and cost the last-rendering subject a unit.
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
        // The subject's OWN share, on top of the response-wide test rather than instead of it (#394). A subject
        // may spend its ceiling and no more even while the response has room to spare — that room belongs to the
        // siblings that have not rendered yet, and giving it away first-come is exactly the serial rule this
        // replaces.
        if (!Allocation.Fits(subject, cost)) { Stop(subject); return false; }
        Write(subject, commit, source);
        return true;
    }

    /// <summary>Commit one admitted unit: write it, MEASURE what it wrote, and charge that. Charged with what it
    /// ACTUALLY wrote, never with the declared cost — the cost is a test before a write, and a ceiling kept in the
    /// units of a test rather than of the writing would drift the moment a site declared 0 (which most of them
    /// do).</summary>
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

    /// <summary>FINISH a unit already admitted — the closing brackets a bounded json section owes once the rows
    /// nested inside it have been written. Unconditional (refusing it would leave a half-written object on the
    /// document) and charged to the SAME subject the opening was, because the two are one unit.
    ///
    /// <para><b>Why it is charged rather than treated as fixed.</b> These closers scale with how many units
    /// rendered, so the skeleton pass — which renders none — cannot see them, and a fixed part that misses them is
    /// a row budget larger than the room that exists. Charged here, the unit's measured demand covers its whole
    /// footprint and <c>ALLOCATION-EQUALS-SPEND</c> holds: what the cost helper measured for the unit is exactly
    /// what the unit wrote, opening and closing together.</para></summary>
    internal void Complete(SweepSubject subject, Action commit)
    {
        int before = _length();
        commit();
        int wrote = _length() - before;
        BodyTotal += wrote;
        Allocation.Charge(subject, wrote);
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
        int wrote = _length() - before;
        ReservedWritten += wrote;
        if (_held.TryGetValue(subject, out var held)) _held[subject] = Math.Max(0, held - wrote);
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
        int before = _length();
        commit();
        ReservedWritten += _length() - before;
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
        // The ROOM is what is given back here, and giving it back is the whole job: held any longer it would be
        // charged against every later subject's emission test. The allocation is told too, but under water-filling
        // that is a no-op by design — a subject's SHARE was never anyone else's to inherit (BodyAllocation.Done).
        _alloc?.Done(subject);
    }

    /// <summary>Did this subject stop short? For the one caller that states the fact in its own words rather than
    /// through the accounting (validate_scripts).</summary>
    internal bool Stopped(SweepSubject subject) => _stopped.Contains(subject);
}
