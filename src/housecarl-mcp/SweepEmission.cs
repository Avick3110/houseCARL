using HousecarlCore;

namespace HousecarlMcp;

/// <summary>The countable things a sweep response can carry; every claim about what is missing reads one.</summary>
internal enum SweepSubject
{
    /// <summary>Dangling references, one line each — the only subject <c>limit=</c> can also drop.</summary>
    DanglingEntries,

    /// <summary>Per-plugin report sections, present in every listing lane even with no entries.</summary>
    PluginSections,

    /// <summary>Rows of the excluded-plugin roster: the plugins the index could not parse.</summary>
    ExcludedRows,

    /// <summary>Rows of the <c>counts_only</c> honesty layer: the plugins whose records could not be read.</summary>
    UnreadRows,

    /// <summary>Rows of the <c>counts_only</c> dangling histogram, by TARGET plugin, the one pointed INTO.</summary>
    HistogramByTarget,

    /// <summary>The same histogram by SOURCE plugin, its own subject so one axis cannot refuse the other's rows.</summary>
    HistogramBySource,

    /// <summary>Per-record sections of the scripts family's listing, emitted whole or not at all.</summary>
    ScriptRecords,

    /// <summary>Rows of the scripts family's <c>counts_only</c> honesty layer: plugins whose enumeration faulted.</summary>
    ScriptScanRows,

    HistogramByProperty,

    /// <summary>The dialogue family's per-seed heads, one per seed that RESOLVED.</summary>
    DialogueSeeds,

    /// <summary>The dialogue family's per-topic blocks, whole or not at all and measurable before the write.</summary>
    DialogueTopics,

    /// <summary>Seeds that could NOT be validated, one row each, in BOTH lanes: the boundary of the answer.</summary>
    DialogueSeedRefusals,

    /// <summary>The facegen family's finding rows, one per NPC or per orphaned bake, emitted whole or not at all.</summary>
    FaceGenRows,

    FaceGenClassRows,

    /// <summary>Rows of the facegen <c>counts_only</c> histogram, by the mod that owns the winning bake.</summary>
    FaceGenModRows,

    /// <summary>Rows of <c>asset_status</c>'s <c>counts_only</c> histogram, by the MO2 layer that wins the path.</summary>
    AssetWinnerRows,
}

/// <summary>Which subjects are histogram axes. Not accounting subjects: an axis discloses its own cut.</summary>
internal static class SweepSubjects
{
    internal static bool IsHistogram(this SweepSubject s)
        => s is SweepSubject.HistogramByTarget or SweepSubject.HistogramBySource or SweepSubject.HistogramByProperty
             or SweepSubject.FaceGenClassRows or SweepSubject.FaceGenModRows or SweepSubject.AssetWinnerRows;
}

/// <summary>ONE histogram axis's closing fact: how many rows are missing, and which knob moves them.</summary>
internal readonly record struct HistogramCut(int Remaining, bool ByBudget)
{
    internal static HistogramCut? For(int distinct, int shown, bool byBudget)
        => shown >= distinct ? null : new HistogramCut(distinct - shown, byBudget);

    internal string Knob => ByBudget ? "max_chars" : "limit";

    /// <summary>The text lane's spelling, in one place: it is composed once to measure its reserve, once to write.</summary>
    internal string Line => "  ... [" + Remaining + " more row(s) — raise " + Knob + "= to see them]\n";
}

/// <summary>ONE counts_only histogram axis, as both the RESERVE and the RENDER need it; null <c>Rows</c> is not
/// the same answer as an empty tally.</summary>
internal readonly record struct HistogramAxis(SweepSubject Subject, IReadOnlyList<SweepCount>? Rows, string Title,
                                              string? Note = null, string? NotComputed = null)
{
    internal string Head => "\n" + Title + " (" + (Rows?.Count ?? 0) + " distinct):\n";

    internal string EmptyLine => "\n" + Title + ": nothing to tally — no findings in the swept scope.\n";

    /// <summary>The axis's note, spelled once; its room is held back with the closing disclosure.</summary>
    internal string NoteLine => Note is null ? "" : "\n" + Note + "\n";

    /// <summary>What this axis says instead of a tally when the mode was not requested. Reserved too.</summary>
    internal string NotComputedLine => Rows is null && NotComputed is not null ? NotComputed + "\n" : "";

    /// <summary>The axis's irreducible disclosure, in text-lane characters: its widest closing line.</summary>
    internal int TextDisclosure
        => Rows is null ? 0
         : Rows.Count == 0 ? EmptyLine.Length
         : Head.Length + new HistogramCut(Rows.Count, ByBudget: true).Line.Length;

    /// <summary>Everything this axis puts in the response's fixed part, one reserve for the lot.</summary>
    internal int TextFixed => NoteLine.Length + NotComputedLine.Length + TextDisclosure;
}

/// <summary>The one place either sweep transport appends anything <c>max_chars</c> can refuse. How the budget is
/// divided, what stays outside it, and the row-shape contract: docs/architecture/render-budget.md.</summary>
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

    /// <param name="budget">the chars the BODY may occupy: the caller's max_chars less the accounting's reserve.</param>
    /// <param name="plan">the families this response renders and which of each family's subjects have rows, or null
    /// for a lane that divides nothing.</param>
    internal BoundedBody(CheckAccounting? acct, int budget, Func<int> length,
                         IReadOnlyList<(SweepFamily Family, IReadOnlyList<SweepSubject> Subjects)>? plan = null,
                         IReadOnlyDictionary<SweepSubject, int>? demand = null, int reservedForRows = 0)
        : this(acct is null ? Array.Empty<CheckAccounting>() : new[] { acct }, budget, length, plan,
               demand, reservedForRows) { }

    /// <summary>A merged response's body, as a factory so a bare <c>null</c> accounting is not an ambiguous call.</summary>
    /// <param name="demand">each planned subject's measured demand; omitted, every planned subject is unconstrained.</param>
    /// <param name="reservedForRows">what this response will hold back for fixed parts, known before the render.</param>
    /// <param name="responseSubjects">the subjects that belong to the response rather than to any family.</param>
    internal static BoundedBody ForFamilies(IReadOnlyList<CheckAccounting> accts, int budget, Func<int> length,
                                            IReadOnlyList<(SweepFamily Family, IReadOnlyList<SweepSubject> Subjects)>? plan = null,
                                            IReadOnlyDictionary<SweepSubject, int>? demand = null,
                                            int reservedForRows = 0,
                                            IReadOnlyList<SweepSubject>? responseSubjects = null,
                                            int reserveDemanded = 0)
        => new(accts, budget, length, plan, demand, reservedForRows, responseSubjects, reserveDemanded);

    /// <summary>A body that admits one unit of each subject and refuses the rest, so the fixed part is MEASURED.</summary>
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
        _alloc = new BodyAllocation(budget - reservedForRows,
                                    plan ?? Array.Empty<(SweepFamily, IReadOnlyList<SweepSubject>)>(), demand,
                                    responseSubjects);
    }

    readonly int _reservedForRows;

    BodyAllocation Allocation => _alloc!;

    internal int AllocationOf(SweepSubject s) => Allocation.AllocationOf(s);

    /// <summary>The chars the whole body may occupy: the cap less what was reserved for accountings and boundaries.</summary>
    internal int Budget => _budget;

    /// <summary>What was measured before the render as owed outside the units, and re-read here.</summary>
    internal int ReservedForRows => _reservedForRows;

    /// <summary>The reserve half of that, held to <see cref="ReserveDeclared"/> by RESERVE-DECLARED-IS-RESERVE-DEMANDED.</summary>
    internal int ReserveDemanded { get; }

    /// <summary>The row budget: the room the allocation divided, and the room the units may spend together.</summary>
    internal int RowBudget => Math.Max(0, _budget - _reservedForRows);

    /// <summary>The high-water mark of what was owed outside the units, held to <see cref="ReservedForRows"/> by
    /// CheckShapeMatrix's one-budget arm.</summary>
    internal int OutstandingHigh { get; private set; }

    /// <summary>What one subject spent, held to <see cref="AllocationOf"/> by ALLOCATION-EQUALS-SPEND.</summary>
    internal int SpentOn(SweepSubject s) => Allocation.SpentOn(s);

    /// <summary>What of the response is charged against the BODY's budget: all of it, less what the reserve paid.</summary>
    int Spent => _length() - _reservedSpent;
    int _reservedSpent;

    /// <summary>What is spent outside the units plus what is still held — the term the response-wide test uses.</summary>
    int Outstanding => Spent - BodyTotal + Held;

    /// <summary>Write text whose room was already held back out of the body budget, so it cannot be refused.</summary>
    internal void Reserved(Action commit)
    {
        int before = _length();
        commit();
        int wrote = _length() - before;
        _reservedSpent += wrote;
        ReservedWritten += wrote;
        ReservedWrittenByAccountings += wrote;
    }

    /// <summary>What this response has written out of the reserve, which the skeleton pass subtracts.</summary>
    internal int ReservedWritten { get; private set; }

    /// <summary>The same total by the path that wrote it, so unwritten reserve has a holder (#398). Diagnostics only.</summary>
    internal int ReservedWrittenByAccountings { get; private set; }
    internal int ReservedWrittenByAxisFrames { get; private set; }
    internal int ReservedWrittenByDisclosures { get; private set; }

    /// <summary>Emit one unit of <paramref name="subject"/>, or refuse; false means it did not fit.</summary>
    /// <param name="cost">an upper bound on what <paramref name="commit"/> will append, or 0 where the site has no
    /// cheap way to measure one, which costs at most one unit of overshoot.</param>
    /// <param name="source">for <see cref="SweepSubject.DanglingEntries"/>, the plugin the entry came from.</param>
    internal bool Emit(SweepSubject subject, int cost, Action commit, string? source = null)
    {
        // One unit rather than none: a json array's frame is wider when it holds something.
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
        // The subject's own share, on top of the response-wide test rather than instead of it.
        if (!Allocation.Fits(subject, cost)) { Stop(subject); return false; }
        Write(subject, commit, source);
        return true;
    }

    /// <summary>Commit one admitted unit: write it, measure what it wrote, and charge that — never the declared cost.</summary>
    void Write(SweepSubject subject, Action commit, string? source)
    {
        int before = _length();
        commit();
        int wrote = _length() - before;
        BodyTotal += wrote;
        Allocation.Charge(subject, wrote);
        foreach (var a in _accts) a.Emitted(subject, source);
    }

    readonly HashSet<SweepSubject> _skeletonFirst = new();

    void Stop(SweepSubject subject)
    {
        _stopped.Add(subject);
        _alloc?.Done(subject);
    }

    /// <summary>Finish a unit already admitted, charged to the subject its opening was.</summary>
    internal void Complete(SweepSubject subject, Action commit)
    {
        int before = _length();
        commit();
        int wrote = _length() - before;
        BodyTotal += wrote;
        Allocation.Charge(subject, wrote);
    }

    /// <summary>What the body actually appended — the one quantity separating the body from the fixed part.</summary>
    internal int BodyTotal { get; private set; }

    /// <summary>Hold back what one subject writes whatever the budget says, before the first unit of ANY subject.</summary>
    internal void Reserve(SweepSubject subject, int cost)
    {
        _held.TryGetValue(subject, out int prior);
        ReserveDeclared += cost - prior;   // the DELTA, so this stays the sum of what is held whatever a caller re-reserves
        _held[subject] = cost;
    }

    /// <summary>The room this render actually held back through <see cref="Reserve"/>, unaffected by a later release.</summary>
    internal int ReserveDeclared { get; private set; }

    /// <summary>This response's fixed part, subtracted rather than assembled, and excluding the overrun notice.</summary>
    /// <param name="contentLength">the finished response as the transport measured it, without the notice.</param>
    internal int FixedPart(int contentLength) => contentLength - BodyTotal + Held;

    /// <summary>Room reserved and not yet spent, held against every emission test including the reserver's own.</summary>
    int Held
    {
        get { int n = 0; foreach (var v in _held.Values) n += v; return n; }
    }

    /// <summary>Write part of a subject's reserved text now, charged against the room already held for it.</summary>
    internal void Fixed(SweepSubject subject, Action commit)
    {
        int before = _length();
        commit();
        int wrote = _length() - before;
        ReservedWritten += wrote;
        ReservedWrittenByAxisFrames += wrote;
        if (_held.TryGetValue(subject, out var held)) _held[subject] = Math.Max(0, held - wrote);
    }

    /// <summary>Write a subject's closing disclosure out of reserved room — never refused, whatever the budget.</summary>
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

    /// <summary>Give a subject's remaining reserved room back unspent.</summary>
    internal void Release(SweepSubject subject)
    {
        _held.Remove(subject);
        // Under water-filling the allocation's own Done is a no-op by design.
        _alloc?.Done(subject);
    }

    /// <summary>Did this subject stop short? For validate_scripts, which states the fact in its own words.</summary>
    internal bool Stopped(SweepSubject subject) => _stopped.Contains(subject);
}
