using System.Collections.Concurrent;
using System.Globalization;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Fold = HousecarlCore.PathFold;   // the fold vocabulary is shared with project.fields — one word list, one meaning

namespace HousecarlCore;

/// <summary>A field-VALUE predicate over a record body — the <c>where=</c> grammar; contracts in docs/architecture/select-and-walk.md.</summary>
public sealed class FieldPredicateSet
{
    /// <summary>The operators; what each one means and what operand it takes are in docs/architecture/select-and-walk.md.</summary>
    enum Op { Eq, Ne, Gt, Ge, Lt, Le, Contains, StartsWith, Has, HasAny, HasNone, Exists, Missing, In, NotIn }

    /// <summary>One parsed predicate: the split path segments, the operator, the operand, and the per-side folds, hops and pre-parsed sets a scan reads without doing IO.</summary>
    sealed record Predicate(string Text, string[] PathSegments, string PathDisplay, Op Op, string Operand, double NumericOperand,
                            HashSet<FormKey>? FormIds = null, ArtifactDemand? Artifact = null,
                            string[]? LinkPath = null, string? LinkPathDisplay = null,
                            PseudoPath Pseudo = PseudoPath.None, IReadOnlyList<string>? RawMembers = null,
                            Fold[]? PathFolds = null, Fold[]? LinkFolds = null,
                            int ParentHops = 0, int LinkParentHops = 0,
                            FormKey? RuntimeKey = null, HashSet<FormKey>? RuntimeKeys = null,
                            bool Negate = false, IReadOnlyList<(FormKey Key, string Raw)>? FormIdOrder = null,
                            IReadOnlyDictionary<string, int>? EidIndex = null)
    {
        /// <summary>The path as the caller wrote it, with its '->' link side when it has one.</summary>
        public string FullPath => LinkPathDisplay is null ? PathDisplay : LinkPathDisplay + "->" + PathDisplay;
    }

    /// <summary>The identity pseudo-paths a predicate may name instead of a body leaf.</summary>
    enum PseudoPath { None, EditorId, Winner, FormId }

    readonly IReadOnlyList<Predicate> _predicates;
    readonly long[] _valueRead;   // per-predicate: candidates whose path read SOME value
    readonly long[] _noValue;     // per-predicate: candidates whose path read NO value (any reason below)
    readonly long[] _container;   // per-predicate SUBSET of _noValue: the path resolves to a container/list, not a scalar leaf
    readonly long[] _unreadable;  // per-predicate SUBSET of _noValue: the path READ FAULTED (Mutagen-unparseable content) — a fault, NOT an unset value
    readonly long[] _unresolved;  // per-predicate SUBSET of _noValue: a '->' step's links are PRESENT but no target resolves (disabled plugin / missing master) — not an unset field
    readonly long[] _noParent;    // per-predicate SUBSET of _noValue: a '*parent' step found no containing record
    readonly long[] _noField;     // per-predicate SUBSET of _noValue: the plan passed the path, but the read walk found no such member on the record
    readonly string?[] _noParentWhat;  // the record type it found none for, for the sentence
    readonly bool[]?[] _eidHits;   // per-predicate: which 'editorid in' members (by EidIndex position) matched
    readonly HashSet<FormKey>?[] _fkHits;   // per-predicate: the FormIDs a 'formid in' list matched
    long _scanned;
    string? _fatal;

    // Resolution bindings for the `winner` term and the `->` link step; bound by the call site after its own Capture().
    Func<FormKey, string?>? _winnerOf;
    Func<FormKey, IMajorRecordGetter?>? _fetchWinnerBody;
    // The `*parent` containment step's child->parent lookup.
    Func<FormKey, FormKey?>? _parentOf;
    // The containing records' bodies, gathered many at once.
    Func<IReadOnlyCollection<FormKey>, IReadOnlyDictionary<FormKey, IMajorRecordGetter>>? _fetchParents;
    // One chunk's containing records, held only until the caller releases the chunk (#1147, #720).
    IReadOnlyDictionary<FormKey, IMajorRecordGetter>? _heldParents;
    // The keys that chunk asked for: one absent from the gather is the answer, not a reason to seek it again.
    HashSet<FormKey>? _heldAsked;
    // The link step's verdict per predicate and target, kept for the set's lifetime in place of the target bodies (#1158).
    readonly Dictionary<(int Index, FormKey Target), (bool Satisfied, EvalKind Kind)> _targetVerdicts = new();
    // The type a memoised no-parent verdict found no container for, so the note names it on a memo hit.
    readonly Dictionary<(int Index, FormKey Target), string?> _targetNoParent = new();

    // The '*parent' hop's verdict memo, keyed by the predicate and by which side hopped.
    readonly Dictionary<(int Index, bool LinkSide, FormKey Parent), (bool Satisfied, EvalKind Kind)> _parentVerdicts = new();

    // The type name of a record the containment map gives no parent for, read once per such record per call.
    readonly Dictionary<FormKey, string?> _noParentTypes = new();

    // Which predicate Matches is evaluating right now — the containment memo's key.
    int _evalIndex;

    // Parent bodies alive on the evaluation stack right now: a depth, not a slot.
    int _parentsInFlight;

    /// <summary>Parent bodies this set holds a reference to right now; zero between candidates is the #720 invariant.</summary>
    internal int ParentBodiesHeld => _parentsInFlight;

    /// <summary>The most parent bodies this set ever held at once.</summary>
    internal int ParentBodyHighWater { get; private set; }

    /// <summary>Bodies the hop has read, on the match path and the miss path alike.</summary>
    internal int ParentBodyFetches { get; private set; }

    /// <summary>Whether any predicate needs the scan's resolution context.</summary>
    public bool NeedsResolution => _predicates.Any(p => p.Pseudo == PseudoPath.Winner || p.LinkPath is not null || Hops(p) > 0);

    /// <summary>Whether any predicate needs winner BODY fetches, not just the winner name.</summary>
    public bool NeedsBodyResolution => _predicates.Any(p => p.LinkPath is not null || Hops(p) > 0);

    /// <summary>Whether any predicate takes a <c>*parent</c> containment step.</summary>
    public bool NeedsContainment => _predicates.Any(p => Hops(p) > 0);

    static int Hops(Predicate p) => p.ParentHops + p.LinkParentHops;

    /// <summary>Whether any predicate reads the candidate record's own body content; false when every term is header-only, which is what keeps DELETED records in a header-only scan; contract in docs/architecture/select-and-walk.md.</summary>
    public bool NeedsLiveBody => _predicates.Any(p => p.LinkPath is not null
        ? p.LinkParentHops == 0                                        // the link's LEFT path is read on the candidate
        : p.ParentHops == 0 && p.Pseudo == PseudoPath.None);           // the own path's leaf walk is read on the candidate

    /// <summary>Bind the scan's resolution context, from the same captured view the scan answers from.</summary>
    /// <param name="fetchParents">The bulk fetch of a chunk's containing records; any other parent is read through <paramref name="fetchWinnerBody"/>.</param>
    public void BindResolution(Func<FormKey, string?> winnerOf, Func<FormKey, IMajorRecordGetter?>? fetchWinnerBody = null,
                               Func<FormKey, FormKey?>? parentOf = null,
                               Func<IReadOnlyCollection<FormKey>, IReadOnlyDictionary<FormKey, IMajorRecordGetter>>? fetchParents = null)
    {
        _winnerOf = winnerOf;
        _fetchWinnerBody = fetchWinnerBody;
        _parentOf = parentOf;
        _fetchParents = fetchParents;
    }

    /// <summary>Gather in one bulk fetch the containing records of <paramref name="chunk"/> with no memoized verdict, held until <see cref="ReleaseParents"/>.</summary>
    public void HoldParents(IEnumerable<IMajorRecordGetter> chunk)
    {
        ReleaseParents();
        if (_fetchParents is null || _parentOf is null || !NeedsContainment) return;
        var want = new HashSet<FormKey>();
        foreach (var child in chunk)
            for (int k = 0; k < _predicates.Count && (_walk is null || CanMatch(child.GetType())); k++)
            {
                var p = _predicates[k];
                // The candidate's own side only: a hop on a link TARGET is read when that target is reached.
                bool linkSide = p.LinkPath is not null;
                int hops = linkSide ? p.LinkParentHops : p.ParentHops;
                if (hops == 0) continue;
                var at = child.FormKey;
                int climbed = 0;
                while (climbed < hops && _parentOf(at) is { } up) { at = up; climbed++; }
                if (climbed == 0 || want.Contains(at)) continue;
                // A chain that dead-ends above its first hop reads the record it stopped on, for the rollup sentence.
                if (climbed == hops ? _parentVerdicts.ContainsKey((k, linkSide, at)) : _noParentTypes.ContainsKey(at)) continue;
                want.Add(at);
            }
        if (want.Count == 0) return;
        ParentBodyFetches += want.Count;
        _heldAsked = want;
        _heldParents = _fetchParents(want);
    }

    /// <summary>Drop the chunk's containing records; the verdicts they decided stay memoized.</summary>
    public void ReleaseParents() { _heldParents = null; _heldAsked = null; }

    // The read door's collect pass records the '->' targets it meets here instead of judging them.
    HashSet<(int K, FormKey Fk)>? _collectPairs;
    // One door group's target bodies, and the keys it yielded, while that group is judged.
    IReadOnlyDictionary<FormKey, IMajorRecordGetter>? _heldTargets;
    HashSet<FormKey>? _heldTargetsAsked;

    /// <summary>Whether any term follows a '->' link, so a candidate's body must be read to learn its targets.</summary>
    public bool NeedsLinkStep => _predicates.Any(p => p.LinkPath is not null);

    /// <summary>Collect every target and container the scan will ask for, then judge them a group at a time, keeping verdicts.</summary>
    public void PrimeOrdered(IEnumerable<IMajorRecordGetter>? candidates, IEnumerable<FormKey>? candidateKeys,
                             Action<IEnumerable<FormKey>, Action<IReadOnlyDictionary<FormKey, IMajorRecordGetter>, IReadOnlyCollection<FormKey>>> forEachGroup,
                             CancellationToken ct = default)
    {
        if (_parentOf is null && _predicates.All(p => p.LinkPath is null)) return;
        var pairs = new HashSet<(int K, FormKey Fk)>();
        var parents = new HashSet<(int K, bool LinkSide, FormKey At)>();
        var deadEnds = new HashSet<FormKey>();
        void Climb(FormKey from, int k, bool linkSide, int hops)
        {
            var at = from; int climbed = 0;
            while (climbed < hops && _parentOf!(at) is { } up) { at = up; climbed++; }
            if (climbed == hops) { if (!_parentVerdicts.ContainsKey((k, linkSide, at))) parents.Add((k, linkSide, at)); }
            else if (climbed > 0 && !_noParentTypes.ContainsKey(at)) deadEnds.Add(at);   // the rollup names the record a chain stops on
        }
        var fatal = _fatal; int evalIndex = _evalIndex;
        _collectPairs = pairs;
        try
        {
            if (candidateKeys is not null && _parentOf is not null)
                foreach (var key in candidateKeys)
                {
                    ct.ThrowIfCancellationRequested();
                    for (int k = 0; k < _predicates.Count; k++)
                        if (_predicates[k] is { LinkPath: null, ParentHops: > 0 } p) Climb(key, k, false, p.ParentHops);
                }
            foreach (var child in candidates ?? Enumerable.Empty<IMajorRecordGetter>())
            {
                ct.ThrowIfCancellationRequested();
                if (_walk is not null && !CanMatch(child.GetType())) continue;
                for (int k = 0; k < _predicates.Count; k++)
                {
                    var p = _predicates[k];
                    bool linkSide = p.LinkPath is not null;
                    int hops = linkSide ? p.LinkParentHops : p.ParentHops;
                    if (hops > 0) { if (_parentOf is not null) Climb(child.FormKey, k, linkSide, hops); continue; }
                    if (!linkSide) continue;
                    _evalIndex = k;
                    // Collecting judges nothing; a fault here is the scan's to raise on the same record.
                    try { EvalLinkPath(p, child, 0); }
                    catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException) { }
                }
            }
        }
        finally { _collectPairs = null; _fatal = fatal; _evalIndex = evalIndex; }
        var byKey = new Dictionary<FormKey, List<(int K, bool Parent, bool LinkSide)>>();
        void Add(FormKey fk, (int, bool, bool) v) { if (!byKey.TryGetValue(fk, out var l)) byKey[fk] = l = new(); l.Add(v); }
        foreach (var (k, fk) in pairs) Add(fk, (k, false, false));
        foreach (var (k, ls, at) in parents) Add(at, (k, true, ls));
        foreach (var at in deadEnds) Add(at, (-1, true, false));
        if (byKey.Count == 0) return;
        forEachGroup(byKey.Keys, (bodies, asked) =>
        {
            var present = new HashSet<FormKey>(asked.Where(bodies.ContainsKey));
            var heldParents = _heldParents; var heldAsked = _heldAsked;
            _heldTargets = bodies; _heldTargetsAsked = present;
            _heldParents = bodies; _heldAsked = present;
            try
            {
                foreach (var fk in present)
                {
                    ct.ThrowIfCancellationRequested();
                    foreach (var (k, isParent, linkSide) in byKey[fk])
                    {
                        if (_fatal is not null) return;
                        if (k < 0) { NoParentTypeOf(fk); continue; }
                        _evalIndex = k;
                        var p = _predicates[k];
                        if (!isParent) { TargetVerdict(p, fk); continue; }
                        if (_parentVerdicts.ContainsKey((k, linkSide, fk))) continue;
                        var parent = bodies[fk];
                        ParentBodyFetches++;
                        if (++_parentsInFlight > ParentBodyHighWater) ParentBodyHighWater = _parentsInFlight;
                        (bool, EvalKind) verdict;
                        try { verdict = linkSide ? EvalLinkPath(p, parent, 0) : EvalTerms(p, parent); }
                        finally { _parentsInFlight--; }
                        if (_fatal is not null) return;                 // never memoized: the scan meets it again
                        _parentVerdicts[(k, linkSide, fk)] = verdict;
                    }
                }
            }
            finally { _heldTargets = null; _heldTargetsAsked = null; _heldParents = heldParents; _heldAsked = heldAsked; _evalIndex = evalIndex; }
        });
        _fatal = fatal;   // a typed error is the scan's to raise, in candidate order
    }

    FieldPredicateSet(IReadOnlyList<Predicate> predicates)
    {
        _predicates = predicates;
        _valueRead = new long[predicates.Count];
        _noValue = new long[predicates.Count];
        _container = new long[predicates.Count];
        _unreadable = new long[predicates.Count];
        _unresolved = new long[predicates.Count];
        _noParent = new long[predicates.Count];
        _noField = new long[predicates.Count];
        _noParentWhat = new string?[predicates.Count];
        _eidHits = new bool[]?[predicates.Count];
        _fkHits = new HashSet<FormKey>?[predicates.Count];
        for (int k = 0; k < predicates.Count; k++)
            if (TracksMembers(predicates[k]))
            {
                if (predicates[k].Pseudo == PseudoPath.EditorId) _eidHits[k] = new bool[predicates[k].RawMembers!.Count];
                else _fkHits[k] = new();
            }
    }

    /// <summary>Whether a predicate is an identity 'in' list on the candidate itself, whose unmatched members are named.</summary>
    static bool TracksMembers(Predicate p) =>
        p is { Op: Op.In, Pseudo: PseudoPath.EditorId or PseudoPath.FormId, LinkPath: null, ParentHops: 0 };

    /// <summary>How many unmatched members the response names before it counts the rest.</summary>
    public const int UnmatchedShown = 20;

    IReadOnlyList<string>? _unmatched;

    /// <summary>The members of the identity 'in' lists that matched no record this set judged, in list order, each once as typed; built on the first read, after the scan.</summary>
    public IReadOnlyList<string> Unmatched => _unmatched ??= BuildUnmatched();

    List<string> BuildUnmatched()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var misses = new List<string>();
        for (int k = 0; k < _predicates.Count; k++)
        {
            var p = _predicates[k];
            if (_eidHits[k] is { } eh)
                foreach (var m in p.RawMembers!)
                    if (!eh[p.EidIndex![m]] && seen.Add(m)) misses.Add(m);
            if (_fkHits[k] is { } fh)
                foreach (var (key, raw) in p.FormIdOrder!)
                    if (!fh.Contains(key) && seen.Add(raw)) misses.Add(raw);
        }
        return misses;
    }

    /// <summary>Record which identity 'in' members a record carries without judging it: for a record only another where= term would drop before <see cref="Matches"/>.</summary>
    public void NoteMembers(IMajorRecordGetter body)
    {
        for (int k = 0; k < _predicates.Count; k++)
        {
            if (_eidHits[k] is { } eh && body.EditorID is { } eid && _predicates[k].EidIndex!.TryGetValue(eid, out var at)) eh[at] = true;
            if (_fkHits[k] is { } fh && _predicates[k].FormIds!.Contains(body.FormKey)) fh.Add(body.FormKey);
        }
    }

    /// <summary>The sentence naming the unmatched members, the first <paramref name="cap"/> by name and the rest by count; <paramref name="gap"/> names why some records went unjudged.</summary>
    public static string UnmatchedSentence(IReadOnlyList<string> unmatched, int cap, string? gap, bool inManifest) =>
        $"note: {unmatched.Count:N0} 'in' list member(s) " +
        (gap is null ? "have no record in this selection: " : $"matched no record in this selection, but {gap}, so a record may exist for them: ") +
        OrderDegraded.Shown(unmatched, cap, n => $", and {n:N0} more ({(inManifest ? "the manifest's notes name them all" : "to_file= keeps the whole list")})") + ".";

    /// <summary>Set once when a numeric operator meets a non-numeric field value; null while the predicate is well-typed.</summary>
    public string? FatalError => _fatal;

    /// <summary>The where= hint for a word key that names a quantifier: the starred step it meant.</summary>
    public static string StarHint(string name, string key) =>
        key.Length > 0 && key[0] != '*' && PathFoldGrammar.Read($"{name}[*{key}]").Fold != PathFold.None
            ? $" A quantifier takes a star: '{name}[*{key}]'." : "";

    /// <summary>The epoch obligations this set carries, one per membership list that came from a result artifact.</summary>
    public IReadOnlyList<ArtifactDemand> ArtifactDemands =>
        _predicates.Where(p => p.Artifact is not null).Select(p => p.Artifact!).ToList();

    /// <summary>Candidate bodies tested so far — the denominator the accounting reports against.</summary>
    public long Scanned => _scanned;

    /// <summary>The EditorID this set asks for when it is nothing but an exact, un-negated <c>editorid = &lt;name&gt;</c> term on the candidate itself.</summary>
    public string? ExactEditorId =>
        _predicates.Count == 1
        && _predicates[0] is { Pseudo: PseudoPath.EditorId, Op: Op.Eq, Negate: false, LinkPath: null, ParentHops: 0 } only
            ? only.Operand : null;

    // PARSE — "<path> <op> <value>", longest-match the operator.

    /// <summary>Parse the wire <c>where</c> list into an evaluable set, or return the first parse error. An empty list is a parse error.</summary>
    /// <param name="parseFormId">The load order's own FormID door, so the runtime notation is accepted here too; null leaves only the plugin-qualified form.</param>
    public static (FieldPredicateSet? Set, string? Error) Parse(IReadOnlyList<string> where, Func<string?, FormKey>? parseFormId = null)
    {
        var list = new List<Predicate>(where.Count);
        foreach (var raw in where)
        {
            var (p, err) = ParseOne(raw, parseFormId);
            if (err is not null) return (null, err);
            list.Add(p!);
        }
        if (list.Count == 0) return (null, "where= was empty — give at least one predicate like \"MagicSkill = Destruction\".");
        return (new FieldPredicateSet(list), null);
    }

    static (Predicate?, string?) ParseOne(string raw, Func<string?, FormKey>? parseFormId)
    {
        var text = (raw ?? "").Trim();
        if (text.Length == 0) return (null, "empty predicate in where= (expected \"<path> <op> <value>\").");

        // 1. path — the leading run of non-operator characters; a '>' right after '-' is the link arrow, not an operator.
        int i = 0;
        while (i < text.Length && !char.IsWhiteSpace(text[i])
               && (!IsOpChar(text[i]) || (text[i] == '>' && i > 0 && text[i - 1] == '-'))) i++;
        var path = text.Substring(0, i);
        if (path.Length == 0)
            return (null, $"predicate '{raw}': no field path before the operator (expected \"<path> <op> <value>\").");

        // 2. skip whitespace to the operator.
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        if (i >= text.Length)
            return (null, $"predicate '{raw}': no operator. Use one of = != > >= < <= contains startswith has has_any has_none exists missing in, 'not in', or 'not' before contains/startswith, e.g. \"{path} = <value>\" or \"{path} exists\".");

        // 3. operator — symbolic (longest match) or the 'contains' word, optionally led by 'not'.
        Op op;
        int after;
        bool negate = false;   // the leading 'not' on a string operator
        if (IsOpChar(text[i]))
        {
            if (StartsWith(text, i, "!=")) { op = Op.Ne; after = i + 2; }
            else if (StartsWith(text, i, ">=")) { op = Op.Ge; after = i + 2; }
            else if (StartsWith(text, i, "<=")) { op = Op.Le; after = i + 2; }
            else if (text[i] == '=') { op = Op.Eq; after = i + 1; }
            else if (text[i] == '>') { op = Op.Gt; after = i + 1; }
            else if (text[i] == '<') { op = Op.Lt; after = i + 1; }
            else return (null, $"predicate '{raw}': unrecognized operator at '{text.Substring(i)}'. Use = != > >= < <= contains startswith has has_any has_none exists missing in, 'not in', or 'not' before contains/startswith.");
        }
        else
        {
            int w = i;
            while (w < text.Length && !char.IsWhiteSpace(text[w])) w++;
            var word = text.Substring(i, w - i);
            if (word.Equals("not", StringComparison.OrdinalIgnoreCase))
            {
                // 'not' leads an operator: the membership complement and the negation of a string operator, one rule over the word table.
                while (w < text.Length && char.IsWhiteSpace(text[w])) w++;
                int w2 = w;
                while (w2 < text.Length && !char.IsWhiteSpace(text[w2])) w2++;
                var second = text.Substring(w, w2 - w);
                if (second.Equals("in", StringComparison.OrdinalIgnoreCase)) op = Op.NotIn;
                else if (TryWordOp(second, out var inner) && inner is Op.Contains or Op.StartsWith) { op = inner; negate = true; }
                else
                    return (null, $"predicate '{raw}': 'not' negates a string operator or leads the membership complement — write \"{path} not contains <text>\", " +
                                  $"\"{path} not startswith <text>\", or \"{path} not in <formid list>\". The other operators have their own complement: '!=' for '=', 'missing' for 'exists', 'has_none' for 'has'.");
                w = w2;
            }
            else if (TryWordOp(word, out var wop)) op = wop;
            else
                return (null, $"predicate '{raw}': unrecognized operator '{word}'. Use = != > >= < <= contains startswith has has_any has_none exists missing in, 'not in', or 'not' before contains/startswith.");
            after = w;
        }

        // 4. operand — the remainder, trimmed.
        var operand = text.Substring(after).Trim();

        // LINK STEP: 'Left->Right' reads Right on the winner bodies the candidate's Left path points at; exactly one step.
        string[]? linkSegs = null;
        string? linkDisplay = null;
        var arrow = path.IndexOf("->", StringComparison.Ordinal);
        if (arrow >= 0)
        {
            var left = path.Substring(0, arrow);
            var right = path.Substring(arrow + 2);
            if (left.Length == 0 || right.Length == 0)
                return (null, $"predicate '{raw}': a link step is '<link path>-><target field>' (e.g. \"Perks->editorid startswith REQ_\") — one side of '->' is empty.");
            if (right.Contains("->", StringComparison.Ordinal))
                return (null, $"predicate '{raw}': only ONE '->' link step is supported in a predicate — a longer chain is the walk construct's job (walk= / references=), not a where= term.");
            linkSegs = left.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (linkSegs.Length == 0)
                return (null, $"predicate '{raw}': '{left}' is not a usable link path.");
            linkDisplay = left;
            path = right;   // the right side is the predicate's own path, evaluated on each reached target
        }

        var segs = path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segs.Length == 0)
            return (null, $"predicate '{raw}': '{path}' is not a usable field path.");

        // The containment step: a leading run of '*parent' hops, stripped first because a hop leads a path by definition.
        int linkParentHops = 0, parentHops;
        if (linkSegs is not null)
        {
            var (lrest, lhops, lherr) = SplitParentHops(raw, linkSegs, linkDisplay!, isLinkLeft: true);
            if (lherr is not null) return (null, lherr);
            linkSegs = lrest; linkParentHops = lhops;
        }
        {
            var (rest, hops, herr) = SplitParentHops(raw, segs, path, isLinkLeft: false);
            if (herr is not null) return (null, herr);
            segs = rest; parentHops = hops;
        }

        // The quantified step: each side's segments split into bare field names plus their fold tokens.
        Fold[]? linkFolds = null;
        if (linkSegs is not null)
        {
            var (lsegs, lfolds, lferr) = SplitFolds(raw, linkSegs, linkSide: true);
            if (lferr is not null) return (null, lferr);
            linkSegs = lsegs; linkFolds = lfolds;
        }
        var (psegs, pathFolds, pferr) = SplitFolds(raw, segs, linkSide: false);
        if (pferr is not null) return (null, pferr);
        segs = psegs;
        if (pathFolds is not null && pathFolds[^1] == Fold.Count
            && op is not (Op.Eq or Op.Ne or Op.Gt or Op.Ge or Op.Lt or Op.Le or Op.In or Op.NotIn))
            return (null, $"predicate '{raw}': '[*count]' yields the number of elements — compare it with = != > >= < <= or in / 'not in' (got '{OpStr(op, negate)}').");

        // Pseudo-path classification, off the segment left after the '*parent' hops; a step that carried a quantifier is not an identity term.
        var term = segs.Length == 1 && !segs[0].Contains('[') && (pathFolds is null || pathFolds[0] == Fold.None)
                   ? segs[0] : "";
        var pseudo = term.Equals("editorid", StringComparison.OrdinalIgnoreCase) ? PseudoPath.EditorId
                   : term.Equals("winner", StringComparison.OrdinalIgnoreCase) ? PseudoPath.Winner
                   : term.Equals("formid", StringComparison.OrdinalIgnoreCase) ? PseudoPath.FormId
                   : PseudoPath.None;

        // An identity term is one value per record, so a quantifier on it has nothing to fold over.
        if (segs.Length == 1 && pathFolds is not null && pathFolds[0] != Fold.None
            && (segs[0].Equals("editorid", StringComparison.OrdinalIgnoreCase)
                || segs[0].Equals("winner", StringComparison.OrdinalIgnoreCase)
                || segs[0].Equals("formid", StringComparison.OrdinalIgnoreCase)))
            return (null, $"predicate '{raw}': '{segs[0]}' is the record's own {(segs[0].Equals("winner", StringComparison.OrdinalIgnoreCase) ? "winning plugin" : "identity")} — one value per record, not a list, so it takes no '{FoldToken(pathFolds[0])}'. Write '{segs[0]}' on its own.");

        // Op-compatibility, validated at parse so an unusable pairing refuses the CALL, never a silent all-miss.
        if (pseudo == PseudoPath.Winner)
        {
            if (linkSegs is not null)
                return (null, $"predicate '{raw}': 'winner' is not usable behind a '->' link step — it names the CANDIDATE record's winning plugin. Test the target another way (e.g. '{linkDisplay}->editorid …').");
            if (op is not (Op.Eq or Op.Ne))
                return (null, $"predicate '{raw}': 'winner' is the provenance term (which plugin WINS the record) and takes '=' or '!=' with a plugin filename — e.g. \"winner = Requiem.esp\".");
        }
        if (pseudo == PseudoPath.EditorId && op is Op.Gt or Op.Ge or Op.Lt or Op.Le or Op.Has or Op.HasAny or Op.HasNone)
            return (null, $"predicate '{raw}': 'editorid' is a text term — use = != contains startswith 'not contains' 'not startswith' exists missing in 'not in' (got '{OpStr(op, negate)}').");

        // A presence op takes NO operand; every other op requires one.
        if (op is Op.Exists or Op.Missing)
        {
            if (pseudo is PseudoPath.Winner or PseudoPath.FormId)
                return (null, $"predicate '{raw}': '{path}' always exists (every record has an identity and a winner) — a presence test on it can never filter. Use it with its own operators instead.");
            if (operand.Length != 0)
                return (null, $"predicate '{raw}': '{OpStr(op)}' is a presence test and takes no value (got '{operand}'). Write it as \"{path} {OpStr(op)}\".");
            return (new Predicate(text, segs, path, op, "", 0, LinkPath: linkSegs, LinkPathDisplay: linkDisplay, Pseudo: pseudo, PathFolds: pathFolds, LinkFolds: linkFolds, ParentHops: parentHops, LinkParentHops: linkParentHops), null);
        }

        if (operand.Length == 0)
            return (null, $"predicate '{raw}': no value after '{OpStr(op, negate)}'.");

        // The membership ops: the operand (inline list or @file) is fully parsed and validated here, so the per-record test does no IO.
        if (op is Op.In or Op.NotIn)
        {
            if (pseudo == PseudoPath.Winner)
                return (null, $"predicate '{raw}': 'winner {OpStr(op)} <list>' is not supported (yet) — AND/OR the '=' form per plugin, e.g. \"winner = A.esp\".");
            if (pseudo == PseudoPath.FormId)
            {
                var (set, order, artifact, lerr) = ParseFormIdList(text, operand, parseFormId);
                if (lerr is not null) return (null, lerr);
                return (new Predicate(text, segs, path, op, operand, 0, set, artifact, LinkPath: linkSegs, LinkPathDisplay: linkDisplay, Pseudo: pseudo, PathFolds: pathFolds, LinkFolds: linkFolds, ParentHops: parentHops, LinkParentHops: linkParentHops, FormIdOrder: order), null);
            }
            var (members, mset, martifact, mruntime, merr) = ParseValueList(text, operand, parseFormId, resolveRuntime: pseudo == PseudoPath.None);
            if (merr is not null) return (null, merr);
            // An EditorID list's one case-blind lookup: member to its first position, for the test and the hit record.
            Dictionary<string, int>? eidIndex = null;
            if (pseudo == PseudoPath.EditorId)
            {
                eidIndex = new(StringComparer.OrdinalIgnoreCase);
                for (int m = 0; m < members!.Count; m++) eidIndex.TryAdd(members[m], m);
            }
            return (new Predicate(text, segs, path, op, operand, 0, mset, martifact, LinkPath: linkSegs, LinkPathDisplay: linkDisplay, Pseudo: pseudo, RawMembers: members, PathFolds: pathFolds, LinkFolds: linkFolds, ParentHops: parentHops, LinkParentHops: linkParentHops, RuntimeKeys: mruntime, EidIndex: eidIndex), null);
        }
        if (pseudo == PseudoPath.FormId)
            return (null, $"predicate '{raw}': 'formid' takes the membership ops only — \"formid in <list>\" / \"formid not in <list>\" (a single record is \"formid in [XXXXXX:Plugin.esp]\").");

        // A scalar operand mixing the two FormID notations is refused at the same door the formid list parses through.
        if (HybridRefusal(raw, operand) is { } hybrid) return (null, hybrid);

        // A bare runtime FormID operand is resolved here through the call's FormID door and carried beside the operand.
        FormKey? runtimeKey = null;
        if ((op is Op.Eq or Op.Ne) && pseudo == PseudoPath.None && RuntimeFormId.TryParse(operand, out _))
        {
            if (parseFormId is null)
                return (null, $"predicate '{raw}': '{operand}' is a RUNTIME FormID (the eight-digit form the game, the console and the logs " +
                              $"print), and this call has no load order to resolve it against. Write the plugin form 'XXXXXX:Plugin.esp' instead.");
            try { runtimeKey = parseFormId(operand); }
            catch (Exception ex)
            {
                return (null, $"predicate '{raw}': '{operand}' is a RUNTIME FormID this load order cannot resolve — {ex.Message}");
            }
        }

        // 5. a numeric operator demands a numeric operand — fail fast at parse (before any scan).
        double num = 0;
        if (IsNumericOp(op) && !TryNum(operand, out num))
            return (null, $"predicate '{raw}': operator '{OpStr(op)}' needs a numeric value, got '{operand}'.");

        return (new Predicate(text, segs, path, op, operand, num, LinkPath: linkSegs, LinkPathDisplay: linkDisplay, Pseudo: pseudo, PathFolds: pathFolds, LinkFolds: linkFolds, ParentHops: parentHops, LinkParentHops: linkParentHops, RuntimeKey: runtimeKey, Negate: negate), null);
    }

    /// <summary>Split one side's segments into bare field names plus their fold tokens, or return the first refusal.</summary>
    static (string[] Segs, Fold[]? Folds, string? Error) SplitFolds(string raw, string[] segs, bool linkSide)
    {
        Fold[]? folds = null;
        var outSegs = segs;
        for (int i = 0; i < segs.Length; i++)
        {
            var s = segs[i];
            var (bare, f, key) = PathFoldGrammar.Read(s);
            if (key is null) continue;
            if (bare.Length == 0)
                return (segs, null, $"predicate '{raw}': '{s}' has no field name before '[' — a quantifier binds to a list field, e.g. 'Conditions{s}'.");
            if (f == Fold.None)
                return (segs, null, $"predicate '{raw}': '[{key}]' is not a quantifier — the tokens are [*any], [*all], [*none] and [*count].");
            if (f == Fold.Set)
                return (segs, null, $"predicate '{raw}': '[*]' yields the element SET, and a set is not a boolean — name the fold in the step: [*any], [*all] or [*none] (or [*count] for the number of elements).");
            if (f == Fold.Count && linkSide)
                return (segs, null, $"predicate '{raw}': '[*count]' yields a NUMBER, which carries no '->' link step — count on the predicate's own path instead.");
            if (f == Fold.Count && i != segs.Length - 1)
                return (segs, null, $"predicate '{raw}': nothing can follow '[*count]' — it yields how MANY elements there are, not an element to step into.");
            if (folds is null) { folds = new Fold[segs.Length]; outSegs = (string[])segs.Clone(); }
            folds[i] = f;
            outSegs[i] = bare;
        }
        return (outSegs, folds, null);
    }


    /// <summary>Strip a side's leading <c>*parent</c> hops; the grammar itself is <see cref="ContainmentIndex.SplitHops"/>, shared with the read walk.</summary>
    static (string[] Tail, int Hops, string? Error) SplitParentHops(string raw, string[] segs, string display, bool isLinkLeft)
    {
        var (hops, err) = ContainmentIndex.SplitHops(segs, display, isLinkLeft);
        if (err is not null) return (segs, 0, $"predicate '{raw}': {err}");
        return (hops == 0 ? segs : segs[hops..], hops, null);
    }

    /// <summary>The token a fold is spelled with, for a message.</summary>
    static string FoldToken(Fold f) => PathFoldGrammar.Token(f);

    /// <summary>Parse a generalized (non-formid) membership list; an all-FormKey list rides along pre-parsed, and a bare runtime FormID entry is resolved here.</summary>
    static (IReadOnlyList<string>? Members, HashSet<FormKey>? Keys, ArtifactDemand? Artifact, HashSet<FormKey>? Runtime, string? Error) ParseValueList(string raw, string operand, Func<string?, FormKey>? parseFormId, bool resolveRuntime)
    {
        string content;
        ArtifactDemand? artifact = null;
        if (operand[0] == '@')
        {
            var path = operand.Substring(1).Trim().Trim('"', '\'');
            if (path.Length == 0)
                return (null, null, null, null, $"predicate '{raw}': '@' names a value-list file but no path follows it.");
            if (PathArguments.NotAbsolute(path, $"predicate '{raw}': value-list file", "the file the values are in", "C:\\work\\values.txt") is { } notAbsolute)
                return (null, null, null, null, notAbsolute);
            try { content = File.ReadAllText(path); }
            catch (Exception ex) { return (null, null, null, null, $"predicate '{raw}': could not read value-list file '{path}' — {ex.GetType().Name}: {ex.Message}"); }
            if (ResultArtifact.LooksLikeArtifact(content))
            {
                var (manifest, tokens, aerr) = ResultArtifact.ReadIdentity(path, content);
                if (aerr is not null) return (null, null, null, null, $"predicate '{raw}': {aerr}");
                if (!manifest!.Identity!.Equals("formid", StringComparison.OrdinalIgnoreCase))
                    return (null, null, null, null, $"predicate '{raw}': artifact '{path}' (from {manifest.Tool}) carries '{manifest.Identity}' identities, not FormIDs — nothing in it to test a value against.");
                artifact = new ArtifactDemand(path, manifest.Epoch);
                content = string.Join("\n", tokens!);
            }
        }
        else content = operand;

        var members = new List<string>();
        HashSet<FormKey>? runtime = null;
        foreach (var tok in ListMembers(content))
        {
            // Same door as the scalar operand.
            if (HybridRefusal(raw, tok) is { } hybrid) return (null, null, null, null, hybrid);
            // And the same door for a bare runtime FormID entry, resolved through the order the call already holds.
            if (resolveRuntime && RuntimeFormId.TryParse(tok, out _))
            {
                if (parseFormId is null)
                    return (null, null, null, null, $"predicate '{raw}': list entry '{tok}' is a RUNTIME FormID (the eight-digit form the game, the console and the logs " +
                                                    $"print), and this call has no load order to resolve it against. Write the plugin form 'XXXXXX:Plugin.esp' instead.");
                try { (runtime ??= new HashSet<FormKey>()).Add(parseFormId(tok)); }
                catch (Exception ex)
                {
                    return (null, null, null, null, $"predicate '{raw}': list entry '{tok}' is a RUNTIME FormID this load order cannot resolve — {ex.Message}");
                }
            }
            members.Add(tok);
        }
        if (members.Count == 0)
            return (null, null, null, null, $"predicate '{raw}': the value list is empty — give at least one entry.");

        HashSet<FormKey>? keys = null;
        var all = new HashSet<FormKey>();
        foreach (var m in members)
        {
            if (!TryFormKey(m, out var fk)) { all = null!; break; }
            all.Add(fk);
        }
        if (all is { Count: > 0 }) keys = all;
        return (members, keys, artifact, runtime, null);
    }

    /// <summary>Parse an <c>in</c>/<c>not in</c> operand into its FormKey set: <c>@&lt;absolute path&gt;</c> or the inline list; separators and the artifact form in docs/architecture/select-and-walk.md.</summary>
    static (HashSet<FormKey>?, List<(FormKey Key, string Raw)>?, ArtifactDemand?, string?) ParseFormIdList(string raw, string operand, Func<string?, FormKey>? parseFormId)
    {
        var toKey = parseFormId ?? (t => FormKey.Factory((t ?? "").Trim()));
        string content;
        bool fromFile = operand[0] == '@';
        if (fromFile)
        {
            var path = operand.Substring(1).Trim().Trim('"', '\'');   // both quote kinds, matching the inline token trim
            if (path.Length == 0)
                return (null, null, null, $"predicate '{raw}': '@' names a formid-list file but no path follows it.");
            if (PathArguments.NotAbsolute(path, $"predicate '{raw}': formid-list file", "the file the FormIDs are in", "C:\\work\\formids.txt") is { } notAbsolute)
                return (null, null, null, notAbsolute);
            try { content = File.ReadAllText(path); }
            catch (Exception ex) { return (null, null, null, $"predicate '{raw}': could not read formid-list file '{path}' — {ex.GetType().Name}: {ex.Message}"); }

            if (ResultArtifact.LooksLikeArtifact(content))
            {
                var (manifest, tokens, aerr) = ResultArtifact.ReadIdentity(path, content);
                if (aerr is not null) return (null, null, null, $"predicate '{raw}': {aerr}");
                if (!manifest!.Identity!.Equals("formid", StringComparison.OrdinalIgnoreCase))
                    return (null, null, null, $"predicate '{raw}': artifact '{path}' (from {manifest.Tool}) carries '{manifest.Identity}' " +
                                        $"identities, not FormIDs — there is no formid list in it to test membership against.");
                var aset = new HashSet<FormKey>();
                var aorder = new List<(FormKey Key, string Raw)>();
                foreach (var tok in tokens!)
                {
                    // ReadIdentity already excludes error rows, so a non-FormID here is a genuine mismatch.
                    try { var ak = toKey(tok); if (aset.Add(ak)) aorder.Add((ak, tok)); }
                    catch (Exception ex)
                    {
                        return (null, null, null, $"predicate '{raw}': artifact '{path}' identity value '{tok}' is not a FormID ({ex.Message}) — " +
                                            "the file does not match its own manifest (was it edited?). Regenerate it from the producing query.");
                    }
                }
                return (aset, aorder, new ArtifactDemand(path, manifest.Epoch), null);
            }
        }
        else content = operand;

        var set = new HashSet<FormKey>();
        var order = new List<(FormKey Key, string Raw)>();   // the caller's order and spelling, for naming the unmatched members
        foreach (var tok in ListMembers(content))
        {
            // Named before the door runs, so the sentence is the same with or without a load order in hand.
            if (HybridRefusal(raw, tok) is { } hybrid) return (null, null, null, hybrid);
            try { var key = toKey(tok); if (set.Add(key)) order.Add((key, tok)); }
            catch (Exception ex)
            {
                // A plugin filename can legally contain a comma, which this grammar cannot represent; name that cause on that shape.
                bool shearShape = tok.Contains(':') && !tok.EndsWith(".esp", StringComparison.OrdinalIgnoreCase)
                                                    && !tok.EndsWith(".esm", StringComparison.OrdinalIgnoreCase)
                                                    && !tok.EndsWith(".esl", StringComparison.OrdinalIgnoreCase);
                return (null, null, null, $"predicate '{raw}': list entry '{tok}'{(fromFile ? $" (in the @file)" : "")} is not a FormID ({ex.Message}). " +
                                    "Expected 'XXXXXX:Plugin.esp' entries separated by commas or newlines." +
                                    (shearShape ? " If the plugin's filename itself contains a comma, it cannot be written in this list — commas always separate entries; rename the plugin or filter another way." : ""));
            }
        }
        if (set.Count == 0)
            return (null, null, null, $"predicate '{raw}': the formid list{(fromFile ? " file" : "")} is empty — give at least one 'XXXXXX:Plugin.esp'.");
        return (set, order, null, null);
    }

    /// <summary>Commas and newlines ONLY — a bare space is legal inside a plugin filename.</summary>
    static readonly char[] ListSeparators = { ',', '\r', '\n' };

    /// <summary>A list operand's entries: split on <see cref="ListSeparators"/>, one trim of brackets, quotes and whitespace, empties dropped.</summary>
    static IEnumerable<string> ListMembers(string content)
    {
        foreach (var t in content.Split(ListSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            var tok = t.Trim('[', ']', '"', '\'', ' ', '\t');
            if (tok.Length > 0) yield return tok;
        }
    }

    static bool IsOpChar(char c) => c is '=' or '!' or '<' or '>';
    static bool IsNumericOp(Op op) => op is Op.Gt or Op.Ge or Op.Lt or Op.Le;
    static bool StartsWith(string s, int i, string op)
        => i + op.Length <= s.Length && string.CompareOrdinal(s, i, op, 0, op.Length) == 0;

    // EVALUATE — test one in-hand body against ALL predicates (ANDed).

    /// <summary>Test one candidate body against every predicate (ANDed), updating the per-predicate accounting; every predicate is read for its accounting even after the AND is lost.</summary>
    public bool Matches(IMajorRecordGetter body)
    {
        if (_fatal is not null) return false;
        // A record whose type cannot carry a path is no match, and was said once for its type.
        if (_walk is not null && !CanMatch(body.GetType())) { NoteMembers(body); return false; }
        _scanned++;
        bool all = true;
        for (int k = 0; k < _predicates.Count; k++)
        {
            var p = _predicates[k];
            _evalIndex = k;   // the containment memo's key: a verdict belongs to the predicate that reached it

            EvalKind kind;
            bool sat;
            if (p.LinkPath is not null)
            {
                // LINK STEP: collect the candidate's links, resolve each target's winner body, evaluate the right side on each — ANY-match.
                (sat, kind) = EvalLinkStep(p, body);
                if (_fatal is not null) return false;
            }
            else
            {
                (sat, kind) = EvalCore(p, body);
                if (_fatal is not null) return false;
            }

            switch (kind)
            {
                case EvalKind.Definite: _valueRead[k]++; if (!sat) all = false; break;
                // A '*parent' step on a record nothing contains, naming the child-bearing properties.
                case EvalKind.NoParent: _noParent[k]++; _noValue[k]++; _noParentWhat[k] ??= _lastNoParent; all = false; break;
                case EvalKind.Container: _container[k]++; _noValue[k]++; all = false; break;
                case EvalKind.Unreadable: _unreadable[k]++; _noValue[k]++; all = false; break;
                // The links are there; their targets are not in this order — its own counter, its own remedy.
                case EvalKind.UnresolvedTarget: _unresolved[k]++; _noValue[k]++; all = false; break;
                case EvalKind.NoField: _noField[k]++; _noValue[k]++; all = false; break;
                default: _noValue[k]++; all = false; break;   // Unset — a valid, value-less path
            }
        }
        return all;
    }

    /// <summary>How one predicate's evaluation on one record resolved: a definite verdict, or one of the no-verdict classes the accounting keys on.</summary>
    enum EvalKind { Definite, NoParent, Container, Unreadable, UnresolvedTarget, NoField, Unset }

    /// <summary>The type of the record the most recent <c>*parent</c> hop found no containing record for.</summary>
    string? _lastNoParent;

    /// <summary>Evaluate one predicate's own (non-link) side against one record; sets <see cref="_fatal"/> on a typed predicate error.</summary>
    (bool Satisfied, EvalKind Kind) EvalCore(Predicate p, IMajorRecordGetter body)
    {
        // The '*parent' containment step: climb to the containing record first, then run every term below on it.
        if (p.ParentHops > 0)
            return EvalAtParent(p, body, p.ParentHops, linkSide: false, parent => EvalTerms(p, parent));
        return EvalTerms(p, body);
    }

    /// <summary>One predicate's own terms against one in-hand body: the provenance term, the identity terms, or the body-leaf walk.</summary>
    (bool Satisfied, EvalKind Kind) EvalTerms(Predicate p, IMajorRecordGetter body)
    {
        if (p.Pseudo == PseudoPath.Winner)
        {
            if (_winnerOf is null)
            {
                _fatal = "internal: a 'winner' provenance predicate was evaluated without a bound resolution context — this scan surface does not support it.";
                return (false, EvalKind.Definite);
            }
            var w = _winnerOf(body.FormKey);
            if (w is null) return (false, EvalKind.Unset);   // off-order body — no winner in the frame (accounted, never guessed)
            bool eq = string.Equals(w, p.Operand, StringComparison.OrdinalIgnoreCase);
            return (p.Op == Op.Eq ? eq : !eq, EvalKind.Definite);
        }

        if (p.Pseudo == PseudoPath.EditorId)
        {
            var eid = body.EditorID;
            if (p.Op is Op.Exists or Op.Missing)
            {
                bool present = !string.IsNullOrEmpty(eid);
                return (p.Op == Op.Exists ? present : !present, EvalKind.Definite);
            }
            // A null EditorID is a definite verdict either way, but the polarity must be right per op.
            int at = 0;
            bool ok = p.Op switch
            {
                Op.Eq => eid is not null && string.Equals(eid, p.Operand, StringComparison.OrdinalIgnoreCase),
                Op.Ne => eid is null || !string.Equals(eid, p.Operand, StringComparison.OrdinalIgnoreCase),
                Op.Contains => eid is not null && eid.Contains(p.Operand, StringComparison.OrdinalIgnoreCase),
                Op.StartsWith => eid is not null && eid.StartsWith(p.Operand, StringComparison.OrdinalIgnoreCase),
                Op.In => eid is not null && p.EidIndex!.TryGetValue(eid, out at),
                Op.NotIn => eid is null || !p.EidIndex!.ContainsKey(eid),
                _ => false,
            };
            // A leading 'not' flips the string op's verdict, putting a record with no EditorID on the matching side.
            if (p.Negate) ok = !ok;
            if (ok && _eidHits[_evalIndex] is { } eidHits) eidHits[at] = true;
            return (ok, EvalKind.Definite);
        }

        // Identity-membership ops on 'formid': a pure FormKey set test, no body leaf read.
        if (p.Pseudo == PseudoPath.FormId)
        {
            bool member = p.FormIds!.Contains(body.FormKey);
            if (member && _fkHits[_evalIndex] is { } fkHits) fkHits.Add(body.FormKey);
            return (p.Op == Op.In ? member : !member, EvalKind.Definite);
        }

        return EvalOwnPath(p, body, 0);
    }

    /// <summary>The predicate's own path from segment <paramref name="from"/> down; recurses, so a second quantified step composes.</summary>
    (bool Satisfied, EvalKind Kind) EvalOwnPath(Predicate p, object obj, int from)
    {
        var segs = p.PathSegments;
        int q = FirstFold(p.PathFolds, from, segs.Length);
        if (q < 0)
            return DecideLeaf(p, ReadEngine.ReadLeaf(obj, from == 0 ? segs : segs[from..]));   // internal, same assembly — the by-construction read walk

        var (coll, parent, miss) = CollectionAt(obj, segs, p.PathFolds!, from, q);
        if (miss is { } m) return (false, m);
        var fold = p.PathFolds![q];
        // A count asks how MANY, so it never builds an element.
        if (fold == Fold.Count)
            return DecideLeaf(p, ReadEngine.LeafRead.Value(Count(coll).ToString(CultureInfo.InvariantCulture)));
        var elems = Materialise(coll);
        return FoldOver(p, elems, fold,
                        e => q + 1 >= segs.Length ? DecideLeaf(p, ReadEngine.EmitToken(e, e.GetType(), parent!))
                                                  : EvalOwnPath(p, e, q + 1));
    }

    /// <summary>The first quantified step at or after <paramref name="from"/>, or -1.</summary>
    static int FirstFold(Fold[]? folds, int from, int len)
    {
        if (folds is null) return -1;
        for (int i = from; i < len; i++) if (folds[i] != Fold.None) return i;
        return -1;
    }

    /// <summary>Navigate to a quantified step's collection and hand back its elements; an absent collection reads as empty, and a step that is not a list is a named no-verdict.</summary>
    (List<object>? Elements, object? Parent, EvalKind? Miss) ElementsAt(object obj, string[] segs, Fold[] folds, int from, int q)
    {
        var (coll, parent, miss) = CollectionAt(obj, segs, folds, from, q);
        return miss is null ? (Materialise(coll), parent, null) : (null, null, miss);
    }

    /// <summary>The same navigation and validation, stopping at the collection itself.</summary>
    (object? Collection, object? Parent, EvalKind? Miss) CollectionAt(object obj, string[] segs, Fold[] folds, int from, int q)
    {
        var (ok, val, declared, parent, note) = ReadEngine.NavigateTo(obj, segs[from..(q + 1)]);
        if (!ok)
        {
            // An absent mid-path substruct makes the collection absent, which reads as empty; every other miss keeps its class.
            if (ReadEngine.IsAbsentNote(note)) return (null, parent, null);
            return (null, null, ClassifyMiss(note ?? ""));
        }
        // Below a value the schema could not judge, a step that is not a list is not carried here.
        if (NotListShape(declared, val) is not null) return (null, null, EvalKind.NoField);
        return (val, parent, null);
    }

    /// <summary>A navigated collection's elements — an absent one is empty and a null element is dropped; <see cref="Count"/> counts what the collection holds, nulls included.</summary>
    static List<object> Materialise(object? coll)
    {
        var list = new List<object>();
        if (coll is null) return list;
        foreach (var e in (System.Collections.IEnumerable)coll) if (e is not null) list.Add(e);
        return list;
    }

    /// <summary>How many elements a navigated collection holds, from the same engine helper the project.fields column reads.</summary>
    static int Count(object? coll)
        => coll is null ? 0 : ReadEngine.CountOf((System.Collections.IEnumerable)coll);

    /// <summary>What a quantified step actually reads where it is not a list — null when it is one.</summary>
    static string? NotListShape(Type declared, object? val)
    {
        var t = Nullable.GetUnderlyingType(declared) ?? declared;
        if (t == typeof(object) && val is not null) t = val.GetType();   // a bracketed hop yields the element's own type
        var name = t.Name;
        if (name.StartsWith("MemorySlice", StringComparison.Ordinal) || name.StartsWith("ReadOnlyMemorySlice", StringComparison.Ordinal))
            return "a raw block of bytes";
        if (WriteEngine.ClosedInterface(t, typeof(IDictionary<,>)) is not null
            || WriteEngine.ClosedInterface(t, typeof(IReadOnlyDictionary<,>)) is not null)
            return "a dict of keyed entries";
        if (WriteEngine.ClosedInterface(t, typeof(IList<>)) is not null
            || WriteEngine.ClosedInterface(t, typeof(IReadOnlyList<>)) is not null)
            return null;
        // Both strips, in that order: a carried value reflects as an overlay type and a null one as the declared getter.
        return $"a single {RecordNaming.StripGetterInterface(RecordNaming.StripOverlay((val?.GetType() ?? t).Name))} value";
    }

    /// <summary>Fold one step's element verdicts into the record's; an empty list is a definite verdict, and one unjudged element sinks a fold the judged ones have not already decided.</summary>
    (bool Satisfied, EvalKind Kind) FoldOver(Predicate p, List<object> elems, Fold fold, Func<object, (bool, EvalKind)> eval)
    {
        if (elems.Count == 0) return (fold != Fold.Any, EvalKind.Definite);
        bool anyVerdict = false, anyTrue = false, anyFalse = false;
        EvalKind? unjudged = null;
        foreach (var e in elems)
        {
            var (sat, kind) = eval(e);
            if (_fatal is not null) return (false, EvalKind.Definite);
            if (kind == EvalKind.Definite) { anyVerdict = true; if (sat) anyTrue = true; else anyFalse = true; }
            else if (unjudged is null || NoVerdictRank(kind) > NoVerdictRank(unjudged.Value)) unjudged = kind;
        }
        if (fold == Fold.Any && anyTrue) return (true, EvalKind.Definite);
        if (fold == Fold.NoneOf && anyTrue) return (false, EvalKind.Definite);
        if (fold == Fold.All && anyFalse) return (false, EvalKind.Definite);
        if (unjudged is { } u) return (false, u);
        if (anyVerdict) return (fold != Fold.Any, EvalKind.Definite);
        return (false, EvalKind.Unreadable);
    }

    /// <summary>Which no-verdict class wins when a fold saw more than one.</summary>
    static int NoVerdictRank(EvalKind k) => k switch
    {
        EvalKind.Unreadable => 6,
        EvalKind.NoParent => 4,
        EvalKind.Container => 3,
        EvalKind.UnresolvedTarget => 2,
        EvalKind.NoField => 2,
        _ => 1,   // Unset
    };

    /// <summary>Classify a navigation miss: a read fault, a member the read walk did not find, or unset.</summary>
    static EvalKind ClassifyMiss(string note) =>
        note.StartsWith("(unreadable", StringComparison.Ordinal) ? EvalKind.Unreadable
        : note.StartsWith("(no field", StringComparison.Ordinal) || note.StartsWith("(no links", StringComparison.Ordinal) ? EvalKind.NoField
        : EvalKind.Unset;

    /// <summary>Decide one predicate against one leaf read — the shared tail of the plain path, a quantified step's element, and a <c>[*count]</c>.</summary>
    (bool Satisfied, EvalKind Kind) DecideLeaf(Predicate p, ReadEngine.LeafRead leaf)
    {
        // Presence ops are the one case where a no-value container leaf is a MATCH; a no-such-field or a read fault still counts as no-value.
        if (p.Op is Op.Exists or Op.Missing)
        {
            switch (ClassifyPresence(leaf))
            {
                case Presence.Present: return (p.Op == Op.Exists, EvalKind.Definite);
                case Presence.Absent: return (p.Op == Op.Missing, EvalKind.Definite);
                case Presence.NoField: return (false, EvalKind.NoField);   // not carried by this record or arm
                default: return (false, EvalKind.Unreadable);
            }
        }

        if (!leaf.HasValue)
        {
            // Classify WHY there was no value, from ReadLeaf's own notes, so the accounting can tell a mistyped path from a valid-but-unset field.
            var note = leaf.Note ?? "";
            if (note.StartsWith("(unreadable", StringComparison.Ordinal)) return (false, EvalKind.Unreadable);
            if (note.Length > 0 && note[0] == '[')
            {
                // The has-family tests a flags leaf's bits; on a list it is refused with the quantified rewrite.
                if (p.Op is Op.Has or Op.HasAny or Op.HasNone && leaf.ContainerCount is not null && leaf.ContainerType is { } ct)
                {
                    var (dict, links) = ReadEngine.ContainerShape(ct);
                    _fatal ??= HasOnContainerRefusal(p, dict, links);
                    return (false, EvalKind.Definite);
                }
                return (false, EvalKind.Container);
            }
            return (false, ClassifyMiss(note));
        }

        // Generalized membership on a leaf path: the leaf's token against the member list, '='-vocabulary equality per entry.
        if (p.Op is Op.In or Op.NotIn)
        {
            bool member;
            // A bare runtime FormID entry resolved at parse, tested first so a list mixing the two forms keeps both.
            if (p.RuntimeKeys is { } rks && TryFormKey(leaf.Token, out var rfk) && rks.Contains(rfk))
                member = true;
            else if (p.FormIds is not null && TryFormKey(leaf.Token, out var lfk))
                member = p.FormIds.Contains(lfk);
            else
                member = p.RawMembers!.Any(m => LeafEquals(leaf, m));
            return (p.Op == Op.In ? member : !member, EvalKind.Definite);
        }

        var (satisfied, err) = Compare(p, leaf);
        if (err is not null) { _fatal ??= err; return (false, EvalKind.Definite); }
        // The leading 'not' flips a DEFINITE verdict only.
        return (p.Negate ? !satisfied : satisfied, EvalKind.Definite);
    }

    /// <summary>The <c>-&gt;</c> link step on one candidate — satisfied iff any target satisfies; an unjudgeable candidate is said, never dressed as a definite non-match.</summary>
    (bool Satisfied, EvalKind Kind) EvalLinkStep(Predicate p, IMajorRecordGetter body)
    {
        if (_fetchWinnerBody is null)
        {
            _fatal = "internal: a '->' link-step predicate was evaluated without a bound resolution context — this scan surface does not support it.";
            return (false, EvalKind.Definite);
        }
        if (p.LinkParentHops > 0)
            return EvalAtParent(p, body, p.LinkParentHops, linkSide: true, parent => EvalLinkPath(p, parent, 0));
        return EvalLinkPath(p, body, 0);
    }

    /// <summary>Run <paramref name="below"/> on the record that contains <paramref name="child"/>; what carries across candidates is the verdict, not the body (#720).</summary>
    (bool Satisfied, EvalKind Kind) EvalAtParent(Predicate p, IMajorRecordGetter child, int hops, bool linkSide,
                                                 Func<IMajorRecordGetter, (bool Satisfied, EvalKind Kind)> below)
    {
        var (key, miss) = ClimbToParentKey(child, hops);
        if (miss is { } m) return (false, m);
        if (_parentVerdicts.TryGetValue((_evalIndex, linkSide, key!.Value), out var memo)) return memo;

        var parent = FetchParentBody(key.Value);
        (bool Satisfied, EvalKind Kind) verdict;
        if (parent is null)
            verdict = (false, EvalKind.Unreadable);              // the parent is indexed but its body would not fetch
        else
        {
            if (++_parentsInFlight > ParentBodyHighWater) ParentBodyHighWater = _parentsInFlight;
            try { verdict = below(parent); }
            finally { _parentsInFlight--; }
            if (_fatal is not null) return (false, EvalKind.Definite);   // a typed predicate error is the call's, not this parent's verdict
        }
        _parentVerdicts[(_evalIndex, linkSide, key.Value)] = verdict;
        return verdict;
    }

    /// <summary>Climb <paramref name="hops"/> containment steps and hand back the key of the record it lands on; a record with no containing record is a named no-verdict.</summary>
    (FormKey? Key, EvalKind? Miss) ClimbToParentKey(IMajorRecordGetter body, int hops)
    {
        if (_parentOf is null || _fetchWinnerBody is null)
        {
            _fatal = $"internal: a '{ContainmentIndex.ParentToken}' containment predicate was evaluated without a bound resolution context — this scan surface does not support it.";
            return (null, EvalKind.Definite);
        }
        var at = body.FormKey;
        for (int i = 0; i < hops; i++)
        {
            var pk = _parentOf(at);
            if (pk is null)
            {
                _lastNoParent = i == 0 ? RecordNaming.StripOverlay(body.GetType().Name) : NoParentTypeOf(at);
                return (null, EvalKind.NoParent);
            }
            at = pk.Value;
        }
        return (at, null);
    }

    /// <summary>The type name the rollup wants for an intermediate record a chain dead-ends on, read once per such record per call.</summary>
    string? NoParentTypeOf(FormKey at)
    {
        if (_noParentTypes.TryGetValue(at, out var name)) return name;
        var body = FetchParentBody(at);
        return _noParentTypes[at] = body is null ? null : RecordNaming.StripOverlay(body.GetType().Name);
    }

    /// <summary>The hop's one body read, counted where it happens so every path pays into the same counter.</summary>
    IMajorRecordGetter? FetchParentBody(FormKey key)
    {
        if (_heldAsked is not null && _heldAsked.Contains(key)) return _heldParents!.GetValueOrDefault(key);
        ParentBodyFetches++;
        return _fetchWinnerBody!(key);
    }

    /// <summary>The link step's left path from segment <paramref name="from"/> down.</summary>
    (bool Satisfied, EvalKind Kind) EvalLinkPath(Predicate p, object obj, int from)
    {
        var segs = p.LinkPath!;
        int q = FirstFold(p.LinkFolds, from, segs.Length);
        if (q >= 0)
        {
            var (elems, _, miss) = ElementsAt(obj, segs, p.LinkFolds!, from, q);
            if (miss is { } m) return (false, m);
            return FoldOver(p, elems!, p.LinkFolds![q],
                            e => q + 1 >= segs.Length
                                 ? JudgeTargets(p, ReadEngine.LinksIn(e, p.LinkPathDisplay ?? ""))
                                 : EvalLinkPath(p, e, q + 1));
        }
        return JudgeTargets(p, ReadEngine.CollectLinksAt(obj, from == 0 ? segs : segs[from..]));
    }

    /// <summary>Resolve one collected link set to its winner bodies and judge the predicate's own side on them — satisfied iff any target satisfies.</summary>
    (bool Satisfied, EvalKind Kind) JudgeTargets(Predicate p, (List<FormKey>? Links, string? Note) collected)
    {
        var (links, note) = collected;
        if (links is null)
        {
            // A read fault, a member the read walk did not find, or no links to follow on this record.
            return (false, ClassifyMiss(note ?? ""));
        }
        if (links.Count == 0) return (false, EvalKind.Unset);   // present but empty — genuinely nothing linked

        // The loudest no-verdict class an actually-reached target produced; a target that does not resolve gets its own class.
        bool anyVerdict = false;
        EvalKind? unjudged = null;
        foreach (var fk in links)
        {
            var (sat, kind) = TargetVerdict(p, fk);
            if (_fatal is not null) return (false, EvalKind.Definite);
            if (kind == EvalKind.Definite)
            {
                anyVerdict = true;
                if (sat) return (true, EvalKind.Definite);
            }
            else Louder(ref unjudged, kind);
        }
        if (anyVerdict) return (false, EvalKind.Definite);
        return (false, unjudged ?? EvalKind.Unset);
    }

    /// <summary>One target's verdict on the predicate's own side, read once per call; the body is dropped as soon as it is judged.</summary>
    (bool Satisfied, EvalKind Kind) TargetVerdict(Predicate p, FormKey fk)
    {
        if (_targetVerdicts.TryGetValue((_evalIndex, fk), out var memo))
        {
            if (memo.Kind == EvalKind.NoParent) _lastNoParent = _targetNoParent.GetValueOrDefault((_evalIndex, fk));
            return memo;
        }
        if (_collectPairs is not null) { _collectPairs.Add((_evalIndex, fk)); return (false, EvalKind.Unset); }
        var target = _heldTargetsAsked is not null && _heldTargetsAsked.Contains(fk) ? _heldTargets![fk] : _fetchWinnerBody!(fk);
        var verdict = target is null ? (false, EvalKind.UnresolvedTarget) : EvalCore(p, target);
        if (_fatal is null)
        {
            _targetVerdicts[(_evalIndex, fk)] = verdict;
            if (verdict.Item2 == EvalKind.NoParent) _targetNoParent[(_evalIndex, fk)] = _lastNoParent;
        }
        return verdict;
    }

    /// <summary>Keep the loudest no-verdict class seen so far.</summary>
    static void Louder(ref EvalKind? held, EvalKind seen)
    {
        if (held is null || NoVerdictRank(seen) > NoVerdictRank(held.Value)) held = seen;
    }

    /// <summary>The three-state presence verdict for a leaf under <c>exists</c>/<c>missing</c>; only Present/Absent decide a match.</summary>
    enum Presence { Present, Absent, NoField, Unreadable }

    /// <summary>Map a leaf read to its presence verdict; the empty-vs-carried and present-null-link splits are in docs/architecture/select-and-walk.md.</summary>
    static Presence ClassifyPresence(ReadEngine.LeafRead leaf)
    {
        if (leaf.HasValue) return Presence.Present;
        var note = leaf.Note ?? "";
        if (note.StartsWith("(no field", StringComparison.Ordinal)) return Presence.NoField;
        if (note.StartsWith("(unreadable", StringComparison.Ordinal)) return Presence.Unreadable;
        if (note == ReadEngine.PresentNullLinkNote) return Presence.Present;   // subrecord present, carrying zero
        if (note.Length > 0 && note[0] == '[')
            return leaf.ContainerCount is 0 ? Presence.Absent : Presence.Present;   // empty list/dict → absent; substruct (null count) → present
        return Presence.Absent;   // (absent) / (null link) / (unresolved localized string) — a valid, unset field
    }

    static (bool satisfied, string? error) Compare(Predicate p, ReadEngine.LeafRead leaf)
    {
        var token = leaf.Token;
        var flags = leaf.Flags;   // non-null iff the leaf is a [Flags] enum — carries (bit pattern, enum type)
        switch (p.Op)
        {
            case Op.Has or Op.HasAny or Op.HasNone:
                return CompareHas(p, token, flags);

            case Op.Gt or Op.Ge or Op.Lt or Op.Le:
                // A [Flags] enum compares on its underlying numeric value; a non-numeric, non-flags field is the typed FatalError.
                double tv;
                if (flags is { } fnum) tv = fnum.Bits;
                else if (!TryNum(token, out tv))
                    return (false, $"predicate '{p.Text}': operator '{OpStr(p.Op)}' needs a numeric field, but '{p.PathDisplay}' read '{Trunc(token)}', not a number.");
                double ov = p.NumericOperand;
                bool num = p.Op switch { Op.Gt => tv > ov, Op.Ge => tv >= ov, Op.Lt => tv < ov, Op.Le => tv <= ov, _ => false };
                return (num, null);

            case Op.Contains:
                return (token.Contains(p.Operand, StringComparison.OrdinalIgnoreCase), null);

            case Op.StartsWith:
                return (token.StartsWith(p.Operand, StringComparison.OrdinalIgnoreCase), null);

            default: // Eq / Ne
                // On a [Flags] enum, equate by resolved bit pattern; every other leaf keeps the token-vocabulary equality.
                bool eq;
                // A bare runtime FormID operand resolved at parse compares as the FormKey it names.
                if (p.RuntimeKey is { } rk && TryFormKey(token, out var tk))
                    eq = tk == rk;
                else if (flags is { } feq && TryResolveBits(p.Operand, feq.EnumType, out var opBits))
                    eq = feq.Bits == opBits;
                else
                    eq = LeafEquals(leaf, p.Operand);
                return (p.Op == Op.Eq ? eq : !eq, null);
        }
    }

    /// <summary>The bitwise set-test (<c>has</c>): true iff every bit of the operand is set on the field, other bits free.</summary>
    static (bool satisfied, string? error) CompareHas(Predicate p, string token, ReadEngine.FlagBits? flags)
    {
        ulong leafBits, opBits = 0;
        if (flags is { } fi)
        {
            leafBits = fi.Bits;
            var r = ResolveBits(p.Operand, fi.EnumType);
            if (r.Bad is { } name)
                return (false, $"predicate '{p.Text}': '{OpStr(p.Op)}' value '{name}' " +
                               (r.Refusal ?? $"is not a bit value or a valid {fi.EnumType.Name} flag name") + ".");
            opBits = r.Bits;
        }
        else if (TryBits(token, out leafBits))   // a plain integer leaf — bit-test its numeric value
        {
            foreach (var value in HasOperandMembers(p.Operand))
            {
                if (!TryBits(value, out var b))
                    return (false, $"predicate '{p.Text}': '{OpStr(p.Op)}' value '{value}' must be a bit value (decimal or 0x hex) for the integer field '{p.PathDisplay}'.");
                opBits |= b;
            }
        }
        else
            return (false, $"predicate '{p.Text}': '{OpStr(p.Op)}' needs a flags/bitmask or integer field, but '{p.PathDisplay}' read '{Trunc(token)}', not a number.");

        if (opBits == 0)
            return (false, $"predicate '{p.Text}': '{OpStr(p.Op)} {p.Operand}' tests no bits — give a non-zero bit value or a flag name.");
        return (p.Op switch
        {
            Op.HasAny => (leafBits & opBits) != 0,
            Op.HasNone => (leafBits & opBits) == 0,
            _ => (leafBits & opBits) == opBits,
        }, null);
    }

    /// <summary>The refusal for a has-family op on a list or dict path, with the rewrite that fits the path.</summary>
    static string HasOnContainerRefusal(Predicate p, bool dict, bool linkElements)
    {
        var path = p.FullPath;
        var list = HasOperandMembers(p.Operand).ToList();
        var head = $"predicate '{p.Text}': '{path}' is a {(dict ? "dict" : "list")} here, not a scalar leaf, and '{OpStr(p.Op)}' tests a flags leaf's bits. ";
        if (dict)
            return head + $"Filter on one entry instead (e.g. '{path}[<key>] {OpStr(p.Op)} {p.Operand}').";
        bool formOperands = list.Count > 0 && list.All(m => TryFormKey(m, out _) || RuntimeFormId.TryParse(m, out _));
        if (linkElements && !formOperands)
            return head + $"Step through the links instead (e.g. '{path}-><field> {OpStr(p.Op)} {p.Operand}', or " +
                   $"'{path}->editorid = {(list.Count > 0 ? list[0] : "<EditorID>")}' to match a linked record by EditorID), or use references= for list→FormID membership.";
        if (!linkElements || !formOperands)
            return head + $"Filter on a scalar sub-path instead (e.g. '{path}[*any].<field> {OpStr(p.Op)} {p.Operand}' or '{path}[0]'), " +
                   "or use references= for list→FormID membership.";
        var members = string.Join(", ", list);
        var rewrite = p.Op switch
        {
            Op.HasNone => $"'{path}[*none] in [{members}]'",
            Op.HasAny => $"'{path}[*any] in [{members}]'",
            _ => $"one '{path}[*any] = <form>' per member",
        };
        return head + $"For list members write {rewrite} ([*any] / [*all] / [*none] fold the same way), or use references= for list→FormID membership.";
    }

    /// <summary>Resolve a <c>has</c>/<c>=</c> operand against a [Flags] enum to its bit pattern.</summary>
    static bool TryResolveBits(string operand, Type enumType, out ulong bits)
    {
        var r = ResolveBits(operand, enumType);
        bits = r.Bits;
        return r.Bad is null;
    }

    /// <summary>One operand's resolution on one enum, once per pair: its bits, or the member that fails and why.</summary>
    static (ulong Bits, string? Bad, string? Refusal) ResolveBits(string operand, Type enumType) =>
        FlagOperands.GetOrAdd((enumType, operand), static k =>
        {
            ulong bits = 0;
            foreach (var m in HasOperandMembers(k.Operand))
            {
                string? refusal = null;
                if (!TryBits(m, out var b) && !ReadEngine.TryEnumBitsFromName(k.Enum, m, out b) && !TryDecodeToken(m, k.Enum, out b, out refusal))
                    return (0, m, refusal);
                bits |= b;
            }
            return (bits, null, null);
        });

    static readonly ConcurrentDictionary<(Type Enum, string Operand), (ulong Bits, string? Bad, string? Refusal)> FlagOperands = new();

    /// <summary>A has-family operand's members: the <c>in</c> list split, with the flags decode's <c>|</c> read as a separator too.</summary>
    static IEnumerable<string> HasOperandMembers(string operand) => ListMembers(operand.Replace('|', ','));

    /// <summary>The flags decode's unnamed-bit token (<c>slotNN</c> on a biped field, <c>bitN</c> elsewhere) as its bit, or the refusal naming the range.</summary>
    static bool TryDecodeToken(string name, Type enumType, out ulong bits, out string? refusal)
    {
        bits = 0; refusal = null;
        bool biped = ReadEngine.IsBipedSlots(enumType);
        var prefix = biped ? "slot" : "bit";
        int first = biped ? 30 : 0;
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        if (!int.TryParse(name.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var n))
        {
            // The read's display spelling ('slot 32', 'slots 30 31'): refused, naming the operand spelling.
            var rest = name[prefix.Length..];
            var nums = (rest.StartsWith('s') || rest.StartsWith('S') ? rest[1..] : rest).Split(' ', '\t', StringSplitOptions.RemoveEmptyEntries);
            if (nums.Length > 0 && nums.All(x => x.All(char.IsAsciiDigit)))
                refusal = $"is the read's display spelling — write {string.Join(", ", nums.Select(x => prefix + x))}";
            return false;
        }
        int width = System.Runtime.InteropServices.Marshal.SizeOf(Enum.GetUnderlyingType(enumType)) * 8;
        if (n < first || n >= first + width)
        {
            refusal = $"is not a {prefix} of {enumType.Name} — write a flag name, a bit value, or {prefix}{first} to {prefix}{first + width - 1}";
            return false;
        }
        bits = 1UL << (n - first);
        return true;
    }

    /// <summary>Parse a bit value — decimal, or <c>0x</c>-prefixed hex; unsigned, and a sign or a non-integer is rejected.</summary>
    static bool TryBits(string s, out ulong bits)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return ulong.TryParse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bits);
        return ulong.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out bits);
    }

    /// <summary>Equality across the token vocabulary: FormKey-canonical, else numeric, else case-insensitive string.</summary>
    static bool ValueEquals(string token, string operand)
    {
        if (TryFormKey(token, out var a) && TryFormKey(operand, out var b)) return a == b;
        if (TryNum(token, out var x) && TryNum(operand, out var y)) return x.Equals(y);
        return string.Equals(token, operand, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>One leaf against one operand: an enum leaf by its underlying integer when the operand names one, else <see cref="ValueEquals"/>.</summary>
    static bool LeafEquals(ReadEngine.LeafRead leaf, string operand)
        => leaf.EnumBox is { } box && EnumOperand(operand, box.GetType()) is { } v ? ReadEngine.EnumNumber(box) == v : ValueEquals(leaf.Token, operand);

    static readonly ConcurrentDictionary<(Type Enum, string Operand), Int128?> EnumOperands = new();

    /// <summary>The integer an operand names on an enum (whole number, 0x hex, or one defined name), resolved once per pair.</summary>
    static Int128? EnumOperand(string operand, Type enumType) => EnumOperands.GetOrAdd((enumType, operand), static k =>
    {
        var t = k.Operand.Trim();
        if (long.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole)) return whole;
        if (TryBits(t, out var bits)) return bits;
        var name = Enum.GetNames(k.Enum).FirstOrDefault(n => n.Equals(t, StringComparison.OrdinalIgnoreCase));
        return name is null ? null : ReadEngine.EnumNumber((Enum)Enum.Parse(k.Enum, name));
    });

    /// <summary>Whether an '=', '!=', 'in' or 'not in' literal can ever equal a leaf of this enum, by the comparison's own rules.</summary>
    public static bool EnumLiteralFits(string literal, Type enumType)
        => EnumOperand(literal, enumType) is not null
           || (enumType.IsDefined(typeof(FlagsAttribute), false) && TryResolveBits(literal, enumType, out _));

    static bool TryNum(string s, out double d)
        => double.TryParse(s, NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out d);

    /// <summary>The refusal for an operand token that MIXES the two FormID notations, or null for anything else.</summary>
    static string? HybridRefusal(string raw, string token)
        => RuntimeFormId.HybridNote(token) is { } note ? $"predicate '{raw}': {note}" : null;

    /// <summary>True only for a real FormKey string (<c>XXXXXX:Plugin.esp</c>).</summary>
    static bool TryFormKey(string s, out FormKey fk)
    {
        try { fk = FormKey.Factory(s.Trim()); return true; }
        catch { fk = default; return false; }
    }

    // PLAN — the schema decides whether each path names a field before any record is read.

    SchemaPathWalk? _walk;
    readonly List<string> _planNotes = new();
    readonly Dictionary<Type, bool> _classVerdicts = new();
    readonly HashSet<string> _notedTypes = new(StringComparer.Ordinal);
    bool _typed;
    bool[] _carried = [];
    List<string>?[] _lacking = [];

    /// <summary>The schema's refusal for this set over <paramref name="roots"/> (null: any record type), or null with a note per listed type that cannot match; binds the per-class check the scan makes.</summary>
    public string? Plan(SchemaPathWalk walk, IReadOnlyList<TypeSchema>? roots)
    {
        _walk = walk;
        _typed = roots is not null;
        _carried = new bool[_predicates.Count];
        _lacking = new List<string>?[_predicates.Count];
        var scope = roots ?? walk.Records;
        string where = roots is null ? "on any record type" : roots.Count == 1 ? $"of {roots[0].Name}" : $"of any of {string.Join(", ", roots.Select(r => r.Name))}";
        foreach (var p in _predicates)
        {
            var (segs, folds, hops) = CandidateSide(p);
            var own = walk.Walk(scope, hops, segs, folds, where);
            if (own.Failure is { } why) return Refuse(p, why);
            if (roots is { Count: > 1 })
                foreach (var r in roots)
                    if (walk.Walk(new[] { r }, hops, segs, folds, $"of {r.Name}").Failure is { } lack && _notedTypes.Add(r.Name))
                        _planNotes.Add(LackingNote(p, lack, r));
            var leaves = own.Leaves;
            if (p.LinkPath is not null)
            {
                if (leaves.All(SchemaPathWalk.BearsNoLink))
                    return $"predicate '{p.Text}': '{p.LinkPathDisplay}' holds no link, so '->' has no record to step to — name a link field on the left.";
                var targets = walk.LinkTargets(leaves);
                // A pseudo right side (editorid, formid, winner) walks its containment hops only, as the own side does.
                bool pseudo = p.Pseudo != PseudoPath.None;
                var right = walk.Walk(targets, p.ParentHops, pseudo ? Array.Empty<string>() : p.PathSegments, pseudo ? null : p.PathFolds,
                    targets.Count == 1 ? $"of {targets[0].Name}, the record type '{p.LinkPathDisplay}' links to" : $"on any record type '{p.LinkPathDisplay}' can link to");
                if (right.Failure is { } rwhy) return Refuse(p, rwhy);
                leaves = right.Leaves;
            }
            if (p.Pseudo != PseudoPath.None) continue;
            if (LeafRefusal(p, leaves) is { } leafWhy) return leafWhy;
        }
        return null;
    }

    /// <summary>The candidate's own side of a predicate: its link's left path, or its own path.</summary>
    static (string[] Segs, Fold[]? Folds, int Hops) CandidateSide(Predicate p) =>
        p.LinkPath is not null ? (p.LinkPath, p.LinkFolds, p.LinkParentHops)
        : p.Pseudo != PseudoPath.None ? (Array.Empty<string>(), null, p.ParentHops)
        : (p.PathSegments, p.PathFolds, p.ParentHops);

    static string LackingNote(Predicate p, string why, TypeSchema type) => $"note: predicate '{p.Text}': {(why.EndsWith('?') ? why + " So" : why.TrimEnd('.') + ";")} no {type.Name} can match.";

    static string Refuse(Predicate p, string why) => $"predicate '{p.Text}': {why}{(why.EndsWith('.') || why.EndsWith('?') ? "" : ".")}";

    /// <summary>Whether a record of this runtime class can match every predicate; with types= a class that cannot is said once, without them only a path no class carries is.</summary>
    bool CanMatch(Type runtime)
    {
        if (_classVerdicts.TryGetValue(runtime, out var ok)) return ok;
        ok = true;
        if (_walk!.RecordOf(runtime) is { } ts)
            for (int k = 0; k < _predicates.Count; k++)
            {
                var (segs, folds, hops) = CandidateSide(_predicates[k]);
                if (_walk.Walk(new[] { ts }, hops, segs, folds, $"of {ts.Name}").Failure is not { } why) { _carried[k] = true; continue; }
                ok = false;
                if (!_typed) { (_lacking[k] ??= new()).Add(ts.Name); continue; }
                if (_notedTypes.Add(ts.Name)) _planNotes.Add(LackingNote(_predicates[k], why, ts));
                break;
            }
        return _classVerdicts[runtime] = ok;
    }

    /// <summary>A refusal the leaves decide: a has-family or value op on a list or dict, or an enum literal no leaf's enum names.</summary>
    string? LeafRefusal(Predicate p, IReadOnlyList<SchemaPathWalk.Leaf> leaves)
    {
        bool count = p.PathFolds is { } pf && pf[^1] == Fold.Count;
        if (!count && leaves.Count > 0 && leaves.All(l => l.Container))
        {
            bool dict = leaves[0].Field!.Cardinality == "dict";
            if (p.Op is Op.Has or Op.HasAny or Op.HasNone)
                return HasOnContainerRefusal(p, dict, leaves.All(l => l.Field!.FormLinkTarget is not null));
            if (p.Op is not (Op.Exists or Op.Missing))
                return $"predicate '{p.Text}': '{p.FullPath}' is a {(dict ? "dict" : "list")} here, not a scalar leaf; filter on its elements " +
                       $"('{p.FullPath}[*any] {OpStr(p.Op, p.Negate)} …') or one of them ('{p.FullPath}[0]'), or use references= for list→FormID membership.";
        }
        if (count || p.Op is not (Op.Eq or Op.Ne or Op.In or Op.NotIn)) return null;
        var enums = new List<Type>();
        foreach (var l in leaves)
        {
            var aq = l.Field is not { } f ? null : l.Element ? f.ElementTypeAssemblyQualified
                   : f.Cardinality == "enum" ? f.MutableTypeAssemblyQualified ?? f.GetterTypeAssemblyQualified : null;
            if (aq is null || WriteEngine.ResolveType(aq) is not { } rt || (Nullable.GetUnderlyingType(rt) ?? rt) is not { IsEnum: true } e) return null;
            if (!enums.Contains(e)) enums.Add(e);
        }
        return enums.Count == 0 ? null : EnumLiteralRefusal(p, enums);
    }

    /// <summary>A value literal no enum the leaves hold can equal, with the hint that fits how it went wrong.</summary>
    static string? EnumLiteralRefusal(Predicate p, IReadOnlyList<Type> enums)
    {
        foreach (var literal in p.RawMembers ?? (IReadOnlyList<string>)new[] { p.Operand })
        {
            if (enums.Any(e => EnumLiteralFits(literal, e))) continue;
            var enumNames = string.Join(" or ", enums.Select(e => e.DeclaringType is { } d ? $"{d.Name}.{e.Name}" : e.Name));
            var flags = enums.Where(e => e.IsDefined(typeof(FlagsAttribute), false)).ToList();
            var bare = literal.Trim('"', '\'');
            string hint;
            if (bare.Length < literal.Length && enums.Any(e => EnumLiteralFits(bare, e)))
                hint = $" The quotes are read as part of the value; write it bare: '{p.Text.Replace(literal, bare)}'.";
            else if (literal.Contains(',') && flags.Count > 0)
            {
                var bad = literal.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                                 .FirstOrDefault(m => !flags.Any(e => EnumLiteralFits(m, e))) ?? literal;
                hint = $" '{bad}' is no flag of it.{PluginNameSuggest.DidYouMean(bad, flags.SelectMany(Enum.GetNames).Distinct())}";
            }
            else if (literal.Contains(','))
                hint = $" {enumNames} is not a [Flags] enum, so a field holds one name and a comma list never matches; test several with 'in [a, b]'.";
            else
                hint = PluginNameSuggest.DidYouMean(literal, enums.SelectMany(Enum.GetNames).Distinct());
            return $"predicate '{p.Text}': '{literal}' names no value of the {enumNames} enum this field holds, so the test means nothing.{hint}";
        }
        return null;
    }

    // ACCOUNTING — what the data said: unset, read faults, missing link targets, no container, no parent.

    /// <summary>The line(s) appended to the result header: the plan's per-type notes, then any predicate that read no value or few; the thresholds are in docs/architecture/select-and-walk.md.</summary>
    public string? AccountingNote()
    {
        var notes = new List<string>(_planNotes);
        for (int k = 0; k < _lacking.Length; k++)
            if (_lacking[k] is { } lack && !_carried[k])
                notes.Add($"predicate '{_predicates[k].Text}': '{_predicates[k].FullPath}' names no field on any record type in scope ({string.Join(", ", lack.Take(3))}" +
                          $"{(lack.Count > 3 ? $", and {lack.Count - 3} more" : "")}), so no record here can match. 0 matches on that basis is NOT a confirmed 'nothing matches'.");
        // Each predicate is carried somewhere, but no type met carries them all, so nothing was scanned.
        if (!_typed && _scanned == 0 && notes.Count == 0 && _classVerdicts.ContainsValue(false))
            notes.Add($"no record type in scope carries all of the predicates ({string.Join(", ", _predicates.Select(p => $"'{p.Text}'"))}), so no record here can match. " +
                      "0 matches on that basis is NOT a confirmed 'nothing matches'.");
        for (int k = 0; k < _predicates.Count && _scanned > 0; k++)
        {
            var path = _predicates[k].FullPath;
            if (_valueRead[k] == 0)
            {
                const string loud = "yielded no readable value on any of";
                string reason;
                if (_noParent[k] == _scanned)
                    reason = $"predicate field '{path}' {loud} {_scanned:N0} scanned record(s) — no record CONTAINS " +
                             $"{(_noParentWhat[k] is { } t ? $"a {t}" : "these records")}. Containment runs from these properties only: {ContainmentIndex.ChildBearingSurface()}.";
                else if (_container[k] == _scanned)
                    reason = $"predicate field '{path}' {loud} {_scanned:N0} scanned record(s) — it resolves to a container here, not a scalar " +
                             $"leaf; filter on a scalar sub-path (e.g. '{path}[0]' or a nested field), or use references= for list→FormID membership.";
                else if (_unreadable[k] == _scanned)
                    reason = $"predicate field '{path}' could not be READ on any of {_scanned:N0} scanned record(s) — a read FAULT (Mutagen could not " +
                             $"parse the field's content), NOT an unset value. This is a coverage/parse limit on this field, not a filter miss; the " +
                             $"filter can't judge these records.";
                else if (_unresolved[k] == _scanned)
                    reason = $"predicate field '{path}' {loud} {_scanned:N0} scanned record(s) — the link IS SET on every one, but NONE of its " +
                             $"targets are in this load order (a disabled plugin, or a master that isn't installed), so there was nothing to read " +
                             $"on the other side. Check the plugin holding the target records is enabled and its masters are present; widening the " +
                             $"scope will not help.";
                else if (_noField[k] == _scanned)
                    reason = $"predicate field '{path}' was not found on any of {_scanned:N0} scanned record(s) — the read found no such member on " +
                             $"these records, so the filter could NOT judge them; this is not an unset value.";
                else if (UnsetCount(k) == _scanned)
                    reason = $"predicate field '{path}' {loud} {_scanned:N0} scanned record(s) — the field IS VALID; it is simply UNSET " +
                             $"(absent/null) on every one, so the path reads fine and there are just no values in this scope. Widen the scope, or " +
                             $"the value you want may live on a different field (e.g. a dialogue topic's player text is on DIAL 'Name', not INFO 'Prompt').";
                else
                    reason = $"predicate field '{path}' {loud} {_scanned:N0} scanned record(s) — {NoValueBreakdown(k)}.";
                notes.Add(reason + " 0 matches on that basis is NOT a confirmed 'nothing matches'.");
            }
            else if (_noValue[k] * 2 > _scanned || _unreadable[k] > 0)
            {
                // A real read FAULT is always said, whatever the ratio; the tail says what this note's records did.
                var tail = _unreadable[k] == 0
                    ? " — counted as non-matches there, not errors."
                    : _unreadable[k] == _noValue[k]
                        ? " — a coverage/parse limit on this field, so the filter could NOT judge those records; they are absent from the results, which is not the same as their not matching."
                        : $" — the filter could NOT judge the {_unreadable[k]:N0} read-fault record(s) (a coverage/parse limit on this field); the rest counted as non-matches, not errors.";
                notes.Add($"note: '{path}' had no value on {_noValue[k]:N0} of {_scanned:N0} scanned record(s) — " + NoValueBreakdown(k) + tail);
            }
        }
        return notes.Count == 0 ? null : string.Join("\n", notes);
    }

    /// <summary>Why a predicate read no value, named per cause from the counters the scan kept.</summary>
    string NoValueBreakdown(int k)
    {
        long unset = UnsetCount(k);
        var parts = new List<string>();
        if (unset > 0) parts.Add($"unset — null or absent ({unset:N0})");
        if (_noParent[k] > 0) parts.Add($"no containing record ({_noParent[k]:N0})");
        if (_container[k] > 0) parts.Add($"a container/list, not a scalar ({_container[k]:N0})");
        if (_unreadable[k] > 0) parts.Add($"a read fault — Mutagen could not parse the field ({_unreadable[k]:N0})");
        if (_unresolved[k] > 0) parts.Add($"the link is set but its target is not in this load order ({_unresolved[k]:N0})");
        if (_noField[k] > 0) parts.Add($"not found on the record ({_noField[k]:N0})");
        return parts.Count == 0 ? "no value" : string.Join(", ", parts);
    }

    /// <summary>The genuinely-unset remainder: the no-value candidates none of the named causes accounts for.</summary>
    long UnsetCount(int k) => _noValue[k] - _noParent[k] - _container[k] - _unreadable[k] - _unresolved[k] - _noField[k];

    /// <summary>How an operator is spelled back to the caller, with its leading <c>not</c> when it carries one.</summary>
    static string OpStr(Op op, bool negate) => negate ? "not " + OpStr(op) : OpStr(op);

    static string OpStr(Op op) => op switch
    {
        Op.Eq => "=", Op.Ne => "!=", Op.Gt => ">", Op.Ge => ">=", Op.Lt => "<", Op.Le => "<=",
        Op.Contains => "contains", Op.StartsWith => "startswith", Op.Has => "has", Op.HasAny => "has_any", Op.HasNone => "has_none",
        Op.Exists => "exists", Op.Missing => "missing",
        Op.In => "in", Op.NotIn => "not in", _ => "?",
    };

    /// <summary>The word-spelled operators as one table, so the plain form and the <c>not</c>-led form cannot drift.</summary>
    static bool TryWordOp(string word, out Op op)
    {
        op = word.ToLowerInvariant() switch
        {
            "contains" => Op.Contains, "startswith" => Op.StartsWith,
            "has" => Op.Has, "has_any" => Op.HasAny, "has_none" => Op.HasNone,
            "exists" => Op.Exists, "missing" => Op.Missing, "in" => Op.In,
            _ => (Op)(-1),
        };
        return op != (Op)(-1);
    }

    static string Trunc(string s) => s.Length > 60 ? s.Substring(0, 60) + "…" : s;
}
