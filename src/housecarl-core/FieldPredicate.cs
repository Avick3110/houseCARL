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
                            bool Negate = false);

    /// <summary>The identity pseudo-paths a predicate may name instead of a body leaf.</summary>
    enum PseudoPath { None, EditorId, Winner, FormId }

    readonly IReadOnlyList<Predicate> _predicates;
    readonly long[] _valueRead;   // per-predicate: candidates whose path read SOME value
    readonly long[] _noValue;     // per-predicate: candidates whose path read NO value (any reason below)
    readonly long[] _noField;     // per-predicate SUBSET of _noValue: the path is not a field on the record (mistyped / wrong for this type)
    readonly long[] _container;   // per-predicate SUBSET of _noValue: the path resolves to a container/list, not a scalar leaf
    readonly long[] _unreadable;  // per-predicate SUBSET of _noValue: the path READ FAULTED (Mutagen-unparseable content) — a fault, NOT an unset value
    readonly long[] _unresolved;  // per-predicate SUBSET of _noValue: a '->' step's links are PRESENT but no target resolves (disabled plugin / missing master) — not an unset field
    readonly long[] _listHop;     // per-predicate SUBSET of _noField: the path hopped THROUGH a list/dict with a dotted segment (a missing bracket, not a mistyped name)
    readonly string?[] _listHopOwner;  // the collection field the hop dead-ended on, for the remedy sentence
    readonly string?[] _listHopRemedy; // the leaf-checked remedy the read engine composed for that hop, quoted verbatim
    readonly long[] _notList;     // per-predicate SUBSET of _noField: a quantified step landed on a value that is not a list — the fold has nothing to fan out over
    readonly string?[] _notListWhat;   // what that step actually read, for the sentence
    readonly long[] _noParent;    // per-predicate SUBSET of _noField: a '*parent' step found no containing record
    readonly string?[] _noParentWhat;  // the record type it found none for, for the sentence
    long _scanned;
    string? _fatal;

    // Resolution bindings for the `winner` term and the `->` link step; bound by the call site after its own Capture().
    Func<FormKey, string?>? _winnerOf;
    Func<FormKey, IMajorRecordGetter?>? _fetchWinnerBody;
    // The `*parent` containment step's child->parent lookup.
    Func<FormKey, FormKey?>? _parentOf;
    // Link-step targets cached for the set's lifetime; the '*parent' hop deliberately does not share it (#720).
    readonly Dictionary<FormKey, IMajorRecordGetter?> _targetCache = new();

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
    public void BindResolution(Func<FormKey, string?> winnerOf, Func<FormKey, IMajorRecordGetter?>? fetchWinnerBody = null,
                               Func<FormKey, FormKey?>? parentOf = null)
    {
        _winnerOf = winnerOf;
        _fetchWinnerBody = fetchWinnerBody;
        _parentOf = parentOf;
    }

    FieldPredicateSet(IReadOnlyList<Predicate> predicates)
    {
        _predicates = predicates;
        _valueRead = new long[predicates.Count];
        _noValue = new long[predicates.Count];
        _noField = new long[predicates.Count];
        _container = new long[predicates.Count];
        _unreadable = new long[predicates.Count];
        _unresolved = new long[predicates.Count];
        _listHop = new long[predicates.Count];
        _listHopOwner = new string?[predicates.Count];
        _listHopRemedy = new string?[predicates.Count];
        _notList = new long[predicates.Count];
        _notListWhat = new string?[predicates.Count];
        _noParent = new long[predicates.Count];
        _noParentWhat = new string?[predicates.Count];
    }

    /// <summary>Set once when a numeric operator meets a non-numeric field value; null while the predicate is well-typed.</summary>
    public string? FatalError => _fatal;

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

    /// <summary>One quantified step of one predicate, saying which type it is rooted at.</summary>
    public readonly record struct QuantifiedStep(IReadOnlyList<string> Path, int Index, string Token, string Text,
                                                 bool OnScannedType);

    /// <summary>Every quantified step in the set, both sides of a <c>-&gt;</c> included.</summary>
    public IReadOnlyList<QuantifiedStep> QuantifiedSteps
    {
        get
        {
            var steps = new List<QuantifiedStep>();
            foreach (var p in _predicates)
            {
                // A side roots at the scanned type only when nothing has moved off it first.
                Collect(p.LinkPath, p.LinkFolds, p, p.LinkParentHops == 0);
                Collect(p.PathSegments, p.PathFolds, p, p.LinkPath is null && p.ParentHops == 0);
            }
            return steps;

            void Collect(string[]? segs, Fold[]? folds, Predicate p, bool onScanned)
            {
                if (segs is null || folds is null) return;
                for (int i = 0; i < segs.Length && i < folds.Length; i++)
                    if (folds[i] != Fold.None) steps.Add(new QuantifiedStep(segs, i, FoldToken(folds[i]), p.Text, onScanned));
            }
        }
    }

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
                var (set, artifact, lerr) = ParseFormIdList(text, operand, parseFormId);
                if (lerr is not null) return (null, lerr);
                return (new Predicate(text, segs, path, op, operand, 0, set, artifact, LinkPath: linkSegs, LinkPathDisplay: linkDisplay, Pseudo: pseudo, PathFolds: pathFolds, LinkFolds: linkFolds, ParentHops: parentHops, LinkParentHops: linkParentHops), null);
            }
            var (members, mset, martifact, mruntime, merr) = ParseValueList(text, operand, parseFormId, resolveRuntime: pseudo == PseudoPath.None);
            if (merr is not null) return (null, merr);
            return (new Predicate(text, segs, path, op, operand, 0, mset, martifact, LinkPath: linkSegs, LinkPathDisplay: linkDisplay, Pseudo: pseudo, RawMembers: members, PathFolds: pathFolds, LinkFolds: linkFolds, ParentHops: parentHops, LinkParentHops: linkParentHops, RuntimeKeys: mruntime), null);
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
        foreach (var t in content.Split(ListSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            var tok = t.Trim('[', ']', '"', '\'', ' ', '\t');
            if (tok.Length == 0) continue;
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
    static (HashSet<FormKey>?, ArtifactDemand?, string?) ParseFormIdList(string raw, string operand, Func<string?, FormKey>? parseFormId)
    {
        var toKey = parseFormId ?? (t => FormKey.Factory((t ?? "").Trim()));
        string content;
        bool fromFile = operand[0] == '@';
        if (fromFile)
        {
            var path = operand.Substring(1).Trim().Trim('"', '\'');   // both quote kinds, matching the inline token trim
            if (path.Length == 0)
                return (null, null, $"predicate '{raw}': '@' names a formid-list file but no path follows it.");
            if (PathArguments.NotAbsolute(path, $"predicate '{raw}': formid-list file", "the file the FormIDs are in", "C:\\work\\formids.txt") is { } notAbsolute)
                return (null, null, notAbsolute);
            try { content = File.ReadAllText(path); }
            catch (Exception ex) { return (null, null, $"predicate '{raw}': could not read formid-list file '{path}' — {ex.GetType().Name}: {ex.Message}"); }

            if (ResultArtifact.LooksLikeArtifact(content))
            {
                var (manifest, tokens, aerr) = ResultArtifact.ReadIdentity(path, content);
                if (aerr is not null) return (null, null, $"predicate '{raw}': {aerr}");
                if (!manifest!.Identity!.Equals("formid", StringComparison.OrdinalIgnoreCase))
                    return (null, null, $"predicate '{raw}': artifact '{path}' (from {manifest.Tool}) carries '{manifest.Identity}' " +
                                        $"identities, not FormIDs — there is no formid list in it to test membership against.");
                var aset = new HashSet<FormKey>();
                foreach (var tok in tokens!)
                {
                    // ReadIdentity already excludes error rows, so a non-FormID here is a genuine mismatch.
                    try { aset.Add(toKey(tok)); }
                    catch (Exception ex)
                    {
                        return (null, null, $"predicate '{raw}': artifact '{path}' identity value '{tok}' is not a FormID ({ex.Message}) — " +
                                            "the file does not match its own manifest (was it edited?). Regenerate it from the producing query.");
                    }
                }
                return (aset, new ArtifactDemand(path, manifest.Epoch), null);
            }
        }
        else content = operand;

        var set = new HashSet<FormKey>();
        foreach (var t in content.Split(ListSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            // One trim with whitespace in the set, so interleaved wrapping strips clean.
            var tok = t.Trim('[', ']', '"', '\'', ' ', '\t');
            if (tok.Length == 0) continue;
            // Named before the door runs, so the sentence is the same with or without a load order in hand.
            if (HybridRefusal(raw, tok) is { } hybrid) return (null, null, hybrid);
            try { set.Add(toKey(tok)); }
            catch (Exception ex)
            {
                // A plugin filename can legally contain a comma, which this grammar cannot represent; name that cause on that shape.
                bool shearShape = tok.Contains(':') && !tok.EndsWith(".esp", StringComparison.OrdinalIgnoreCase)
                                                    && !tok.EndsWith(".esm", StringComparison.OrdinalIgnoreCase)
                                                    && !tok.EndsWith(".esl", StringComparison.OrdinalIgnoreCase);
                return (null, null, $"predicate '{raw}': list entry '{tok}'{(fromFile ? $" (in the @file)" : "")} is not a FormID ({ex.Message}). " +
                                    "Expected 'XXXXXX:Plugin.esp' entries separated by commas or newlines." +
                                    (shearShape ? " If the plugin's filename itself contains a comma, it cannot be written in this list — commas always separate entries; rename the plugin or filter another way." : ""));
            }
        }
        if (set.Count == 0)
            return (null, null, $"predicate '{raw}': the formid list{(fromFile ? " file" : "")} is empty — give at least one 'XXXXXX:Plugin.esp'.");
        return (set, null, null);
    }

    /// <summary>Commas and newlines ONLY — a bare space is legal inside a plugin filename.</summary>
    static readonly char[] ListSeparators = { ',', '\r', '\n' };

    static bool IsOpChar(char c) => c is '=' or '!' or '<' or '>';
    static bool IsNumericOp(Op op) => op is Op.Gt or Op.Ge or Op.Lt or Op.Le;
    static bool StartsWith(string s, int i, string op)
        => i + op.Length <= s.Length && string.CompareOrdinal(s, i, op, 0, op.Length) == 0;

    // EVALUATE — test one in-hand body against ALL predicates (ANDed).

    /// <summary>Test one candidate body against every predicate (ANDed), updating the per-predicate accounting; every predicate is read for its accounting even after the AND is lost.</summary>
    public bool Matches(IMajorRecordGetter body)
    {
        if (_fatal is not null) return false;
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
                case EvalKind.NoField: _noField[k]++; _noValue[k]++; all = false; break;
                // A list hop is a no-such-field miss; the extra counter tells a missing bracket from a mistyped name.
                case EvalKind.ListHop: _noField[k]++; _listHop[k]++; _noValue[k]++; _listHopOwner[k] ??= _lastListHopOwner; _listHopRemedy[k] ??= _lastListHopRemedy; all = false; break;
                // A quantified step on a non-list is likewise, with the step's real cardinality for the sentence.
                case EvalKind.NotAList: _noField[k]++; _notList[k]++; _noValue[k]++; _notListWhat[k] ??= _lastNotList; all = false; break;
                // A '*parent' step on a record nothing contains is likewise, naming the child-bearing properties.
                case EvalKind.NoParent: _noField[k]++; _noParent[k]++; _noValue[k]++; _noParentWhat[k] ??= _lastNoParent; all = false; break;
                case EvalKind.Container: _container[k]++; _noValue[k]++; all = false; break;
                case EvalKind.Unreadable: _unreadable[k]++; _noValue[k]++; all = false; break;
                // The links are there; their targets are not in this order — its own counter, its own remedy.
                case EvalKind.UnresolvedTarget: _unresolved[k]++; _noValue[k]++; all = false; break;
                default: _noValue[k]++; all = false; break;   // Unset — a valid, value-less path
            }
        }
        return all;
    }

    /// <summary>How one predicate's evaluation on one record resolved: a definite verdict, or one of the no-verdict classes the accounting keys on.</summary>
    enum EvalKind { Definite, NoField, ListHop, NotAList, NoParent, Container, Unreadable, UnresolvedTarget, Unset }

    /// <summary>The type of the record the most recent <c>*parent</c> hop found no containing record for.</summary>
    string? _lastNoParent;

    /// <summary>The collection field named by the most recent list-hop note, stashed for the rollup.</summary>
    string? _lastListHopOwner;

    /// <summary>The remedy that note carried, composed by the read engine and quoted here rather than recomposed.</summary>
    string? _lastListHopRemedy;

    /// <summary>What a quantified step actually read where it was not a list, stashed for the rollup sentence.</summary>
    string? _lastNotList;

    /// <summary>Sentence-case a remedy fragment lifted from a leaf note.</summary>
    static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>Classify a leaf note beginning "(no field": the bracket-aware variant is a missing-bracket miss, not a mistyped name.</summary>
    EvalKind ClassifyNoField(string note)
    {
        // "(no field 'X': 'Owner' is a list/dict — <remedy>)" vs the plain "(no field X)".
        const string marker = "' is a list/dict";
        int at = note.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0) return EvalKind.NoField;
        int open = at > 0 ? note.LastIndexOf('\'', at - 1) : -1;
        _lastListHopOwner = open >= 0 ? note[(open + 1)..at] : null;
        const string sep = " — ";
        int rem = note.IndexOf(sep, at, StringComparison.Ordinal);
        _lastListHopRemedy = rem >= 0 && note.EndsWith(")", StringComparison.Ordinal)
            ? note[(rem + sep.Length)..^1]
            : null;
        return EvalKind.ListHop;
    }

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
            bool ok = p.Op switch
            {
                Op.Eq => eid is not null && string.Equals(eid, p.Operand, StringComparison.OrdinalIgnoreCase),
                Op.Ne => eid is null || !string.Equals(eid, p.Operand, StringComparison.OrdinalIgnoreCase),
                Op.Contains => eid is not null && eid.Contains(p.Operand, StringComparison.OrdinalIgnoreCase),
                Op.StartsWith => eid is not null && eid.StartsWith(p.Operand, StringComparison.OrdinalIgnoreCase),
                Op.In => eid is not null && p.RawMembers!.Any(m => string.Equals(eid, m, StringComparison.OrdinalIgnoreCase)),
                Op.NotIn => eid is null || !p.RawMembers!.Any(m => string.Equals(eid, m, StringComparison.OrdinalIgnoreCase)),
                _ => false,
            };
            // A leading 'not' flips the string op's verdict, putting a record with no EditorID on the matching side.
            if (p.Negate) ok = !ok;
            return (ok, EvalKind.Definite);
        }

        // Identity-membership ops on 'formid': a pure FormKey set test, no body leaf read.
        if (p.Pseudo == PseudoPath.FormId)
        {
            bool member = p.FormIds!.Contains(body.FormKey);
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
            if (note == ReadEngine.AbsentNote) return (null, parent, null);
            return (null, null, ClassifyMiss(note ?? ""));
        }
        if (NotListShape(declared, val) is { } what)
        {
            _lastNotList = $"'{segs[q]}{FoldToken(folds[q])}' reads as {what}, not a list";
            return (null, null, EvalKind.NotAList);
        }
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
        EvalKind.ListHop => 5,
        EvalKind.NotAList => 4, EvalKind.NoField => 4, EvalKind.NoParent => 4,
        EvalKind.Container => 3,
        EvalKind.UnresolvedTarget => 2,
        _ => 1,   // Unset
    };

    /// <summary>Classify a navigation miss into the no-verdict vocabulary the accounting keys on.</summary>
    EvalKind ClassifyMiss(string note)
    {
        if (note.StartsWith("(no field", StringComparison.Ordinal)) return ClassifyNoField(note);
        if (note.StartsWith("(unreadable", StringComparison.Ordinal)) return EvalKind.Unreadable;
        return EvalKind.Unset;
    }

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
                case Presence.NoField: return (false, ClassifyNoField(leaf.Note ?? ""));   // same path, same diagnosis, whatever the operator
                default: return (false, EvalKind.Unreadable);
            }
        }

        if (!leaf.HasValue)
        {
            // Classify WHY there was no value, from ReadLeaf's own notes, so the accounting can tell a mistyped path from a valid-but-unset field.
            var note = leaf.Note ?? "";
            if (note.StartsWith("(no field", StringComparison.Ordinal)) return (false, ClassifyNoField(note));
            if (note.StartsWith("(unreadable", StringComparison.Ordinal)) return (false, EvalKind.Unreadable);
            if (note.Length > 0 && note[0] == '[') return (false, EvalKind.Container);
            return (false, EvalKind.Unset);
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
                member = p.RawMembers!.Any(m => ValueEquals(leaf.Token, m));
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
            var n = note ?? "";
            if (n.StartsWith("(no field", StringComparison.Ordinal)) return (false, ClassifyNoField(n));
            if (n.StartsWith("(unreadable", StringComparison.Ordinal)) return (false, EvalKind.Unreadable);
            if (n.StartsWith("(no links", StringComparison.Ordinal)) return (false, EvalKind.NoField);   // not a link-bearing path — a wrong path for this step
            return (false, EvalKind.Unset);                                                              // absent optional — no links to follow
        }
        if (links.Count == 0) return (false, EvalKind.Unset);   // present but empty — genuinely nothing linked

        // The loudest no-verdict class an actually-reached target produced; a target that does not resolve gets its own class.
        bool anyVerdict = false;
        EvalKind? unjudged = null;
        foreach (var fk in links)
        {
            if (!_targetCache.TryGetValue(fk, out var target))
                _targetCache[fk] = target = _fetchWinnerBody(fk);
            if (target is null) { Louder(ref unjudged, EvalKind.UnresolvedTarget); continue; }
            var (sat, kind) = EvalCore(p, target);
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
                    eq = ValueEquals(token, p.Operand);
                return (p.Op == Op.Eq ? eq : !eq, null);
        }
    }

    /// <summary>The bitwise set-test (<c>has</c>): true iff every bit of the operand is set on the field, other bits free.</summary>
    static (bool satisfied, string? error) CompareHas(Predicate p, string token, ReadEngine.FlagBits? flags)
    {
        ulong leafBits, opBits;
        if (flags is { } fi)
        {
            leafBits = fi.Bits;
            if (!TryResolveBits(p.Operand, fi.EnumType, out opBits))
                return (false, $"predicate '{p.Text}': '{OpStr(p.Op)}' value '{p.Operand}' is not a bit value or a valid {fi.EnumType.Name} flag name.");
        }
        else if (TryBits(token, out leafBits))   // a plain integer leaf — bit-test its numeric value
        {
            if (!TryBits(p.Operand, out opBits))
                return (false, $"predicate '{p.Text}': '{OpStr(p.Op)}' value '{p.Operand}' must be a bit value (decimal or 0x hex) for the integer field '{p.PathDisplay}'.");
        }
        else
            return (false, $"predicate '{p.Text}': '{OpStr(p.Op)}' needs a flags/bitmask or integer field, but '{p.PathDisplay}' read '{Trunc(token)}', not a number.");

        if (opBits == 0)
            return (false, $"predicate '{p.Text}': '{OpStr(p.Op)} 0' tests no bits — give a non-zero bit value or a flag name.");
        return (p.Op switch
        {
            Op.HasAny => (leafBits & opBits) != 0,
            Op.HasNone => (leafBits & opBits) == 0,
            _ => (leafBits & opBits) == opBits,
        }, null);
    }

    /// <summary>Resolve a <c>has</c>/<c>=</c> operand against a [Flags] enum to its bit pattern: a numeric literal, else a flag name or comma-combo.</summary>
    static bool TryResolveBits(string operand, Type enumType, out ulong bits)
        => TryBits(operand, out bits) || ReadEngine.TryEnumBitsFromName(enumType, operand, out bits);

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

    // ACCOUNTING — the loud "a wrong path is not a true zero" surface.

    /// <summary>The line(s) appended to the result header so a wrong path can never read as a confirmed true negative; the thresholds are in docs/architecture/select-and-walk.md.</summary>
    public string? AccountingNote()
    {
        if (_scanned == 0) return null;   // nothing reached the predicate (e.g. an empty type group) — no health signal to give
        List<string>? notes = null;
        for (int k = 0; k < _predicates.Count; k++)
        {
            var pk = _predicates[k];
            var path = pk.LinkPathDisplay is null ? pk.PathDisplay : pk.LinkPathDisplay + "->" + pk.PathDisplay;
            if (_valueRead[k] == 0)
            {
                // No candidate read a value — the CAUSE decides whether this is a wrong path or a correct path over a value-less scope.
                const string loud = "yielded no readable value on any of";
                long unset = UnsetCount(k);   // what is left: genuinely-unset valid fields
                string reason;
                if (_noField[k] == _scanned && _noParent[k] > 0)
                {
                    // Nothing CONTAINS these records, so the hop has nowhere to go; name the properties that own children at all.
                    reason = $"predicate field '{path}' {loud} {_scanned:N0} scanned record(s) — no record CONTAINS " +
                             $"{(_noParentWhat[k] is { } t ? $"a {t}" : "these records")}, on {_noParent[k]:N0} of them" +
                             (_noParent[k] == _scanned ? "" : "; on the rest the path is not a field at all") +
                             $". Containment runs from these properties only: {ContainmentIndex.ChildBearingSurface()}.";
                }
                else if (_noField[k] == _scanned && _notList[k] > 0)
                {
                    // The quantifier is the thing to drop: the step exists, it is just not a list here.
                    reason = $"predicate field '{path}' {loud} {_scanned:N0} scanned record(s) — the quantified step " +
                             $"{_notListWhat[k] ?? "named there"} on {_notList[k]:N0} of them, so a fold has no elements to run over" +
                             (_notList[k] == _scanned ? "" : "; on the rest the path is not a field at all") +
                             ". Drop the quantifier, or point it at a list-valued field.";
                }
                else if (_noField[k] == _scanned && _listHop[k] > 0)
                {
                    // A missing bracket, not a mistyped name, so the schema is the wrong place to send the caller.
                    var owner = _listHopOwner[k];
                    // The remedy is the read engine's: it checked the trailing segment against the collection's element type.
                    var remedy = _listHopRemedy[k]
                        ?? "index an element with BRACKETS (e.g. 'Effects[0].Data.Magnitude'), not a dotted segment";
                    reason = $"predicate field '{path}' {loud} {_scanned:N0} scanned record(s) — the path steps THROUGH " +
                             (owner is not null ? $"'{owner}', which is a list/dict, " : "a list/dict ") +
                             $"with a dotted segment, which dead-ends (on {_listHop[k]:N0} of them" +
                             (_listHop[k] == _scanned ? "" : "; on the rest the path is not a field at all") +
                             $"). {Capitalize(remedy)}; to ask about EVERY element instead, quantify the step " +
                             "('Effects[*any].Data.Magnitude > 50'). For list->FormID membership use references=.";
                }
                else if (_noField[k] == _scanned)
                    reason = $"predicate field '{path}' {loud} {_scanned:N0} scanned record(s) — it is NOT A FIELD on these records " +
                             $"(a mistyped path, or a field that doesn't exist on this record type); check the field name against the record's schema.";
                else if (_container[k] == _scanned)
                    reason = $"predicate field '{path}' {loud} {_scanned:N0} scanned record(s) — it resolves to a container/list here, not a scalar " +
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
                else if (_noField[k] == 0 && _container[k] == 0 && _unreadable[k] == 0 && _unresolved[k] == 0)
                    reason = $"predicate field '{path}' {loud} {_scanned:N0} scanned record(s) — but the field IS VALID; it is simply UNSET " +
                             $"(absent/null) on every one, so the path reads fine and there are just no values in this scope. Widen the scope, or " +
                             $"the value you want may live on a different field (e.g. a dialogue topic's player text is on DIAL 'Name', not INFO 'Prompt').";
                else
                    reason = $"predicate field '{path}' {loud} {_scanned:N0} scanned record(s) — a mix of no-such-field ({_noField[k]:N0}), " +
                             $"container/list ({_container[k]:N0}), read-fault ({_unreadable[k]:N0}), unresolved link target ({_unresolved[k]:N0}), " +
                             $"and unset ({unset:N0}); check it's a scalar leaf that exists on these records.";
                (notes ??= new()).Add(reason + " 0 matches on that basis is NOT a confirmed 'nothing matches'.");
            }
            else if (_noValue[k] * 2 > _scanned || _unreadable[k] > 0)
            {
                // A real read FAULT is always said, whatever the ratio; the tail says what this note's records did.
                var tail = _unreadable[k] == 0
                    ? " — counted as non-matches there, not errors."
                    : _unreadable[k] == _noValue[k]
                        ? " — a coverage/parse limit on this field, so the filter could NOT judge those records; they are absent from the results, which is not the same as their not matching."
                        : $" — the filter could NOT judge the {_unreadable[k]:N0} read-fault record(s) (a coverage/parse limit on this field); the rest counted as non-matches, not errors.";
                (notes ??= new()).Add(
                    $"note: '{path}' had no value on {_noValue[k]:N0} of {_scanned:N0} scanned record(s) — " +
                    NoValueBreakdown(k) + tail);
            }
        }
        return notes is null ? null : string.Join("\n", notes);
    }

    /// <summary>Why a predicate read no value, named per cause from the counters the scan kept — never re-derived from a rendered note.</summary>
    string NoValueBreakdown(int k)
    {
        long unset = UnsetCount(k);
        var parts = new List<string>();
        if (unset > 0) parts.Add($"unset — null or absent ({unset:N0})");
        if (_noField[k] > 0) parts.Add($"not a field on the record read ({_noField[k]:N0})");
        if (_container[k] > 0) parts.Add($"a container/list, not a scalar ({_container[k]:N0})");
        if (_unreadable[k] > 0) parts.Add($"a read fault — Mutagen could not parse the field ({_unreadable[k]:N0})");
        if (_unresolved[k] > 0) parts.Add($"the link is set but its target is not in this load order ({_unresolved[k]:N0})");
        return parts.Count == 0 ? "no value" : string.Join(", ", parts);
    }

    /// <summary>The genuinely-unset remainder: the no-value candidates none of the named causes accounts for.</summary>
    long UnsetCount(int k) => _noValue[k] - _noField[k] - _container[k] - _unreadable[k] - _unresolved[k];

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
