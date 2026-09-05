using System.Globalization;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Fold = HousecarlCore.PathFold;   // the fold vocabulary is shared with project.fields — one word list, one meaning

namespace HousecarlCore;

/// <summary>
/// A field-VALUE predicate over a record body — the query-side complement to <see cref="ReadEngine"/>.
///
/// <para>Where <c>cross_plugin_query</c> can already scope and shape results by a record's IDENTITY and LINKS
/// (type / editorid_contains / references), a <see cref="FieldPredicateSet"/> filters by a field's VALUE:
/// <c>"MagicSkill = Destruction"</c>, <c>"BasicStats.Damage &gt;= 50"</c>, <c>"Archetype.ActorValue = Infamy"</c>.
/// Multiple predicates are ANDed (the wire is <c>where: string[]</c>).</para>
///
/// <para><b>By construction (cornerstone).</b> The extraction is NOT a per-field table — it is the read engine's
/// own path-walk: each predicate pulls its candidate's value through the internal
/// <see cref="ReadEngine.ReadLeaf"/> (the same navigation the read tools drive), then compares the
/// round-trippable token. So the set of fields you can FILTER on IS the set of fields houseCARL can READ — every
/// type, every depth, no hand-kept list. The comparison vocabulary is fixed by the token forms
/// <see cref="ReadEngine"/> emits (the inverse of the write engine's Coerce): enum NAME, invariant numeric
/// round-trip, <c>True</c>/<c>False</c>, <c>XXXXXX:Plugin.esp</c> FormKey.</para>
///
/// <para><b>No silent wrong answer.</b> A value predicate's natural failure mode is a wrong field path that
/// reads no value on every candidate and looks like a true "0 matches." This type ACCOUNTS for why each
/// candidate didn't match (per-predicate value-read vs no-value counts) so the scan can fail LOUD when a path
/// is read-blind everywhere (<see cref="AccountingNote"/>), and a numeric operator pointed at a non-numeric
/// field is a fast, named <see cref="FatalError"/> on the first value-bearing candidate — never a whole-scan
/// silent skip.</para>
///
/// <para>Scope: the value operators take scalar-leaf paths only (incl. a concrete bracketed element like
/// <c>Keywords[0]</c>). A whole LIST leaf (<c>Keywords</c>, <c>Effects</c>) reads as a no-value container summary,
/// so a value predicate on a list path is surfaced by the accounting, never silently matched — list→FormID
/// membership is <c>references=</c>'s job. The PRESENCE operators (<c>exists</c>/<c>missing</c>) are the exception:
/// they DO match a carried substruct/list leaf (present and non-empty), the "which records carry a VMAD/Effects"
/// query. The MEMBERSHIP operators (<c>formid in</c>/<c>formid not in</c> a supplied list) are the other non-leaf
/// case: they test the record's IDENTITY against a pre-parsed FormKey set, no read walk at all.</para>
///
/// <para><b>The quantified step.</b> A path step may declare its multiplicity and its fold where it binds:
/// <c>Conditions[*any].Data.Function = IsGuard</c>, <c>Effects[*none].BaseEffect-&gt;editorid startswith REQ_</c>,
/// <c>Effects[*count] &gt; 2</c>. <c>[*any]</c>/<c>[*all]</c>/<c>[*none]</c> fold the elements into a boolean and
/// <c>[*count]</c> into their number; the bare <c>[*]</c> is the element SET and is refused here (a set is not a
/// boolean). The fold is the same one <see cref="EvalLinkStep"/> already runs over link targets, with the fan-out
/// source swapped to the step's elements — so it composes with the <c>-&gt;</c> link step and with itself.</para>
///
/// <para><b>The containment step.</b> A path may LEAD with <c>*parent</c>, the second edge kind: the record that
/// CONTAINS this one — a DIAL over its INFO, a CELL over its placed references, a WRLD over its cells
/// (<c>*parent.EditorID = GreetingsTopic</c>, <c>*parent.*parent.EditorID = Tamriel</c>). It is a step, so it
/// chains and everything below it — the winner term, <c>editorid</c>, <c>formid</c> membership, leaves, folds —
/// reads the parent with no second rule. It leads a path by definition: the containing record is a property of the
/// RECORD, not of a field value, so a <c>*parent</c> anywhere else refuses by name.</para>
/// </summary>
public sealed class FieldPredicateSet
{
    /// <summary>The operators. <see cref="Gt"/>/<see cref="Ge"/>/<see cref="Lt"/>/<see cref="Le"/> are
    /// numeric-only; <see cref="Contains"/> is a case-insensitive substring; <see cref="Eq"/>/<see cref="Ne"/>
    /// compare across the whole token vocabulary (FormKey-canonical, else numeric, else case-insensitive string).
    /// <see cref="Has"/> is a BITWISE set-test for a <c>[Flags]</c> enum (or plain integer) leaf — true iff every
    /// bit of the operand is set on the field, regardless of other bits — so a multi-slot BodyTemplate still
    /// matches the one slot asked for, which <see cref="Eq"/> (exact value) and the range ops cannot express. Its
    /// operand is a bit value (decimal or <c>0x</c> hex) or a flag NAME. <see cref="HasAny"/> and
    /// <see cref="HasNone"/> are the other two folds over the SAME bits — any bit of the operand set, and none of
    /// them set — the exclusion terms a slot sweep needs, spelled to match the path step's any/all/none.
    /// <see cref="Exists"/>/<see cref="Missing"/> are PRESENCE tests that take NO operand — true iff the path
    /// resolves to a present, NON-EMPTY value (a scalar OR a carried substruct/list) / its complement. They are the
    /// only operators that MATCH a no-value container leaf: the "which records CARRY a VirtualMachineAdapter /
    /// Effects / Conditions" query, which the value operators (needing a scalar leaf) cannot express.
    /// <see cref="In"/>/<see cref="NotIn"/> are IDENTITY-membership tests against a supplied FormID list — the
    /// reconciliation subtraction "every record except these already-claimed ones". They take the pseudo-path
    /// <c>formid</c> (the record's own identity, not a body leaf — deliberately outside the read walk, matching the
    /// read cleave where identity sits beside Fields) and a list operand: inline comma-separated FormIDs, or
    /// <c>@&lt;absolute path&gt;</c> naming a file of them. Restricted to <c>formid</c> at parse (a named refusal on any
    /// other path) so a future generalization to leaf-value membership is an extension, not a behavior change.</summary>
    enum Op { Eq, Ne, Gt, Ge, Lt, Le, Contains, StartsWith, Has, HasAny, HasNone, Exists, Missing, In, NotIn }

    /// <summary>One parsed predicate: the split path segments (fed straight to <see cref="ReadEngine.ReadLeaf"/>),
    /// the operator, the raw operand, and — for a numeric operator — the operand pre-parsed to a double (validated
    /// at parse, so a non-numeric operand under <c>&gt;</c>/<c>&lt;</c> fails the whole call before any scan).
    /// <paramref name="FormIds"/> is the pre-parsed membership set for <see cref="Op.In"/>/<see cref="Op.NotIn"/>
    /// (file already read + every token validated at parse — the scan never does IO), null for every other op.
    /// <paramref name="Artifact"/> is non-null when the list came from a result ARTIFACT: the epoch obligation the
    /// consuming scan must check against the build it captures (see <see cref="ArtifactDemands"/>).
    /// <paramref name="PathFolds"/> / <paramref name="LinkFolds"/> are parallel to the segments of their side and
    /// carry each step's quantifier, null when that side has none — the segments themselves are stored bare, so
    /// the read walk sees an ordinary field name.
    /// <paramref name="ParentHops"/> / <paramref name="LinkParentHops"/> are how many leading <c>*parent</c>
    /// containment steps that side opens with; the segments after them are stored bare, so once the hop lands on
    /// the containing record everything downstream reads an ordinary path.</summary>
    sealed record Predicate(string Text, string[] PathSegments, string PathDisplay, Op Op, string Operand, double NumericOperand,
                            HashSet<FormKey>? FormIds = null, ArtifactDemand? Artifact = null,
                            string[]? LinkPath = null, string? LinkPathDisplay = null,
                            PseudoPath Pseudo = PseudoPath.None, IReadOnlyList<string>? RawMembers = null,
                            Fold[]? PathFolds = null, Fold[]? LinkFolds = null,
                            int ParentHops = 0, int LinkParentHops = 0);

    /// <summary>The identity pseudo-paths a predicate may name instead of a body leaf. <c>editorid</c> reads the
    /// record's EditorID (always available off the early EDID subrecord — never a reflection walk, and live even on
    /// records whose deep body Mutagen can't parse). <c>winner</c> is the PROVENANCE term: it reads the record's
    /// load-order RESOLUTION (which plugin wins it), not its content — evaluated via the resolution the consuming
    /// scan binds (<see cref="BindResolution"/>), so it forces winner resolution over the whole scanned scope.
    /// <c>formid</c> is the membership ops' identity path.</summary>
    enum PseudoPath { None, EditorId, Winner, FormId }

    readonly IReadOnlyList<Predicate> _predicates;
    readonly long[] _valueRead;   // per-predicate: candidates whose path read SOME value
    readonly long[] _noValue;     // per-predicate: candidates whose path read NO value (any reason below)
    readonly long[] _noField;     // per-predicate SUBSET of _noValue: the path is not a field on the record (mistyped / wrong for this type)
    readonly long[] _container;   // per-predicate SUBSET of _noValue: the path resolves to a container/list, not a scalar leaf
    readonly long[] _unreadable;  // per-predicate SUBSET of _noValue: the path READ FAULTED (Mutagen-unparseable content) — a fault, NOT an unset value
    readonly long[] _listHop;     // per-predicate SUBSET of _noField: the path hopped THROUGH a list/dict with a dotted segment (a missing bracket, not a mistyped name)
    readonly string?[] _listHopOwner;  // the collection field the hop dead-ended on, for the remedy sentence
    readonly string?[] _listHopRemedy; // the leaf-checked remedy the read engine composed for that hop, quoted verbatim
    readonly long[] _notList;     // per-predicate SUBSET of _noField: a quantified step landed on a value that is not a list — the fold has nothing to fan out over
    readonly string?[] _notListWhat;   // what that step actually read, for the sentence
    readonly long[] _noParent;    // per-predicate SUBSET of _noField: a '*parent' step found no containing record
    readonly string?[] _noParentWhat;  // the record type it found none for, for the sentence
    long _scanned;
    string? _fatal;

    // Resolution bindings: the `winner` provenance term and the `->` link step read the record's RESOLUTION
    // (winner plugin; a linked target's winner body), which only the consuming scan's captured view can supply.
    // Must be bound by the call site AFTER its own Capture(), so the predicate and the answer read the same build;
    // evaluating an unbound term is a FatalError, never a silent non-match.
    Func<FormKey, string?>? _winnerOf;
    Func<FormKey, IMajorRecordGetter?>? _fetchWinnerBody;
    // The `*parent` containment step reads the index's child→parent map, then fetches that parent's winner body
    // through _fetchWinnerBody like any other cross-record hop.
    Func<FormKey, FormKey?>? _parentOf;
    // Link-step targets recur across candidates — fetch once per scan. Unbounded by design for the set's lifetime
    // (one call): magnitude is the DISTINCT link-target population of the scanned scope, which for realistic link
    // paths (Perks, Effects, Keywords) is hundreds-to-low-thousands of record getters, small beside the scan
    // itself. A pathological whole-order high-fan-out path is bounded by the scope the grammar already requires
    // (types= / plugins=).
    //
    // The '*parent' hop shares this cache and does NOT share that bound, which is stated here rather than left
    // for a reader to infer: types= bounds the CHILD type, not the parent population, so
    // types=["PlacedObject"] where=["*parent.EditorID startswith Whiterun"] retains one getter per distinct CELL —
    // five figures on vanilla Skyrim before any mod — and a '*parent.*parent' chain adds every worldspace on top.
    // Still one call's lifetime and still bodies the scan would have fetched anyway, so it is retention, not
    // repeated work; the declared cost of the step, not a hidden one.
    readonly Dictionary<FormKey, IMajorRecordGetter?> _targetCache = new();

    /// <summary>Whether any predicate needs the scan's resolution context (<c>winner</c> term or a <c>-&gt;</c>
    /// link step) — the call site checks this to bind <see cref="BindResolution"/> (and open the body-fetch
    /// session the link step needs) before the first <see cref="Matches"/>.</summary>
    public bool NeedsResolution => _predicates.Any(p => p.Pseudo == PseudoPath.Winner || p.LinkPath is not null || Hops(p) > 0);

    /// <summary>Whether any predicate follows a <c>-&gt;</c> link step or a <c>*parent</c> containment step (needs
    /// winner BODY fetches, not just the winner name) — the call site opens an overlay session for the fetch when
    /// true.</summary>
    public bool NeedsBodyResolution => _predicates.Any(p => p.LinkPath is not null || Hops(p) > 0);

    /// <summary>Whether any predicate takes a <c>*parent</c> containment step — the call site binds the index's
    /// child→parent lookup when true.</summary>
    public bool NeedsContainment => _predicates.Any(p => Hops(p) > 0);

    static int Hops(Predicate p) => p.ParentHops + p.LinkParentHops;

    /// <summary>Whether any predicate reads the CANDIDATE record's own body content (a leaf walk or a link step on
    /// it) — false when every term is header/resolution-only (`editorid`, `winner`, `formid` membership). The
    /// scan's deleted-record check keys on this: a deleted record has no live body for the CONTENT filters, but its
    /// EditorID and its winner resolution are real facts, so a header-only predicate set must still see it.
    ///
    /// <para>A <c>*parent</c> hop is header-only for the CHILD: it reads <c>body.FormKey</c> and nothing else, and
    /// every term below the hop reads the PARENT's body, which is live. So a hop leading a side makes that side
    /// header-only on the candidate — which is what keeps a patch-deleted placed reference in the results of
    /// <c>where=["*parent.EditorID = SomeCell"]</c>, the crash-log lookup this step exists for.</para></summary>
    public bool NeedsLiveBody => _predicates.Any(p => p.LinkPath is not null
        ? p.LinkParentHops == 0                                        // the link's LEFT path is read on the candidate
        : p.ParentHops == 0 && p.Pseudo == PseudoPath.None);           // the own path's leaf walk is read on the candidate

    /// <summary>Bind the scan's resolution context: <paramref name="winnerOf"/> answers "which plugin wins this
    /// FormKey" (the `winner` term), <paramref name="fetchWinnerBody"/> produces a linked target's winner body
    /// (the `-&gt;` link step; null when the target doesn't resolve). Both must come from the SAME captured view
    /// the scan answers from.</summary>
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
        _listHop = new long[predicates.Count];
        _listHopOwner = new string?[predicates.Count];
        _listHopRemedy = new string?[predicates.Count];
        _notList = new long[predicates.Count];
        _notListWhat = new string?[predicates.Count];
        _noParent = new long[predicates.Count];
        _noParentWhat = new string?[predicates.Count];
    }

    /// <summary>Set once when a numeric operator meets a non-numeric field value on the first value-bearing
    /// candidate — a typed predicate error. The scan checks this and aborts, surfacing it as a recoverable error
    /// (never a silent skip). Null while the predicate is well-typed.</summary>
    public string? FatalError => _fatal;

    /// <summary>The epoch obligations this predicate set carries: one per <c>in</c>/<c>not in</c> list that came
    /// from a result ARTIFACT (vs a plain formid-list file, which claims nothing). The consuming scan compares each
    /// against the build it captures — AFTER its own Capture(), so the check and the answer read the same build —
    /// and refuses loud on mismatch, naming both epochs. Empty for plain-list predicates.</summary>
    public IReadOnlyList<ArtifactDemand> ArtifactDemands =>
        _predicates.Where(p => p.Artifact is not null).Select(p => p.Artifact!).ToList();

    /// <summary>Candidate bodies tested so far — the denominator the accounting reports against.</summary>
    public long Scanned => _scanned;

    /// <summary>One quantified step of one predicate: the segments of the side it sits on, which one carries the
    /// fold, how it is spelled, and the predicate's own text for the message. A scan with a NAMED type scope walks
    /// these against the schema, so "that step is not a list on this type" refuses the call rather than becoming a
    /// whole-scan accounting note. <paramref name="OnScannedType"/> says whether the step is rooted at the SCANNED
    /// record type: the right side of a <c>-&gt;</c> is rooted at the link TARGET's type instead, and a side that
    /// opens with a <c>*parent</c> hop at the CONTAINING record's — asking the scanned type's schema about either
    /// would judge a field the step was never on.</summary>
    public readonly record struct QuantifiedStep(IReadOnlyList<string> Path, int Index, string Token, string Text,
                                                 bool OnScannedType);

    /// <summary>Every quantified step in the set, both sides of a <c>-&gt;</c> included — each saying which type it
    /// is rooted at.</summary>
    public IReadOnlyList<QuantifiedStep> QuantifiedSteps
    {
        get
        {
            var steps = new List<QuantifiedStep>();
            foreach (var p in _predicates)
            {
                // A side roots at the scanned type only when nothing has moved off it first: a '*parent' hop roots
                // that side at the CONTAINING record's type, and the right side of a '->' at the link target's.
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

    // ======================================================================
    //  PARSE — "<path> <op> <value>", longest-match the operator.
    //  The path is dotted/bracketed identifiers (no whitespace, no operator
    //  char), so it ends at the first space or operator char; the operand is
    //  the remainder. 'contains' is a whitespace-delimited word operator.
    // ======================================================================

    /// <summary>Parse the wire <c>where</c> list into an evaluable set, or return the FIRST parse error, so a
    /// malformed predicate refuses the whole call before scanning. An empty list is a parse error: a caller
    /// passing <c>where</c> at all means to filter.</summary>
    /// <param name="parseFormId">How a <c>formid in [...]</c> entry becomes a FormKey — pass the load order's own
    /// door (<c>IndexView.ParseFormId</c>) so the runtime notation is accepted here too. Null where no load order is
    /// in hand, which leaves only the plugin-qualified form.</param>
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

        // 1. path — the leading run of non-whitespace, non-operator-char characters. The ONE exception: a '>'
        //    immediately after '-' is the LINK-STEP arrow ('Perks->editorid'), part of the path, not an operator.
        int i = 0;
        while (i < text.Length && !char.IsWhiteSpace(text[i])
               && (!IsOpChar(text[i]) || (text[i] == '>' && i > 0 && text[i - 1] == '-'))) i++;
        var path = text.Substring(0, i);
        if (path.Length == 0)
            return (null, $"predicate '{raw}': no field path before the operator (expected \"<path> <op> <value>\").");

        // 2. skip whitespace to the operator.
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        if (i >= text.Length)
            return (null, $"predicate '{raw}': no operator. Use one of = != > >= < <= contains startswith has has_any has_none exists missing in 'not in', e.g. \"{path} = <value>\" or \"{path} exists\".");

        // 3. operator — symbolic (longest match) or the 'contains' word.
        Op op;
        int after;
        if (IsOpChar(text[i]))
        {
            if (StartsWith(text, i, "!=")) { op = Op.Ne; after = i + 2; }
            else if (StartsWith(text, i, ">=")) { op = Op.Ge; after = i + 2; }
            else if (StartsWith(text, i, "<=")) { op = Op.Le; after = i + 2; }
            else if (text[i] == '=') { op = Op.Eq; after = i + 1; }
            else if (text[i] == '>') { op = Op.Gt; after = i + 1; }
            else if (text[i] == '<') { op = Op.Lt; after = i + 1; }
            else return (null, $"predicate '{raw}': unrecognized operator at '{text.Substring(i)}'. Use = != > >= < <= contains startswith has has_any has_none exists missing in 'not in'.");
        }
        else
        {
            int w = i;
            while (w < text.Length && !char.IsWhiteSpace(text[w])) w++;
            var word = text.Substring(i, w - i);
            if (word.Equals("contains", StringComparison.OrdinalIgnoreCase)) op = Op.Contains;
            else if (word.Equals("startswith", StringComparison.OrdinalIgnoreCase)) op = Op.StartsWith;
            else if (word.Equals("has", StringComparison.OrdinalIgnoreCase)) op = Op.Has;
            else if (word.Equals("has_any", StringComparison.OrdinalIgnoreCase)) op = Op.HasAny;
            else if (word.Equals("has_none", StringComparison.OrdinalIgnoreCase)) op = Op.HasNone;
            else if (word.Equals("exists", StringComparison.OrdinalIgnoreCase)) op = Op.Exists;
            else if (word.Equals("missing", StringComparison.OrdinalIgnoreCase)) op = Op.Missing;
            else if (word.Equals("in", StringComparison.OrdinalIgnoreCase)) op = Op.In;
            else if (word.Equals("not", StringComparison.OrdinalIgnoreCase))
            {
                // 'not' is only the first half of 'not in' — consume the second word or refuse loud.
                while (w < text.Length && char.IsWhiteSpace(text[w])) w++;
                int w2 = w;
                while (w2 < text.Length && !char.IsWhiteSpace(text[w2])) w2++;
                if (!text.AsSpan(w, w2 - w).Equals("in", StringComparison.OrdinalIgnoreCase))
                    return (null, $"predicate '{raw}': 'not' must be followed by 'in' (the membership complement) — write \"{path} not in <formid list>\".");
                op = Op.NotIn; w = w2;
            }
            else
                return (null, $"predicate '{raw}': unrecognized operator '{word}'. Use = != > >= < <= contains startswith has has_any has_none exists missing in or 'not in'.");
            after = w;
        }

        // 4. operand — the remainder, trimmed. (For 'contains' the operand may contain operator chars; we already
        //    consumed the operator positionally, so that's fine.)
        var operand = text.Substring(after).Trim();

        // LINK STEP: 'Left->Right' reads Right on the record(s) the candidate's Left path points AT (their
        // load-order-winner bodies, from the same captured view the scan answers from), ANY-match over the reached
        // targets. Exactly ONE step: chaining arrows is refused — a longer chain is the walk construct's job.
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

        // The containment step: a leading run of '*parent' hops from the record to the record that CONTAINS it,
        // and the rest of the side is read on that. Stripped first, because a hop leads a path by definition.
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

        // The quantified step: each side's segments are split into bare field names plus their fold tokens, so the
        // read walk below sees an ordinary path and the fold rides beside it.
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
            return (null, $"predicate '{raw}': '[*count]' yields the number of elements — compare it with = != > >= < <= or in / 'not in' (got '{OpStr(op)}').");

        // Pseudo-path classification: 'editorid' (the record's EditorID), 'winner' (the provenance term — which
        // plugin WINS the record, resolution not content), 'formid' (the membership ops' identity path).
        // Classified off the segment left AFTER the '*parent' hops, not the raw path, so '*parent.editorid' reads
        // the containing record's identity rather than falling through to a case-sensitive field walk. A step that
        // carried a quantifier is NOT an identity term, and the fold must be read off pathFolds rather than off the
        // segment: SplitFolds has already stripped the bracket, so 'editorid[*any]' reaches here spelled 'editorid'
        // and would otherwise classify as the pseudo term with its quantifier silently dropped.
        var term = segs.Length == 1 && !segs[0].Contains('[') && (pathFolds is null || pathFolds[0] == Fold.None)
                   ? segs[0] : "";
        var pseudo = term.Equals("editorid", StringComparison.OrdinalIgnoreCase) ? PseudoPath.EditorId
                   : term.Equals("winner", StringComparison.OrdinalIgnoreCase) ? PseudoPath.Winner
                   : term.Equals("formid", StringComparison.OrdinalIgnoreCase) ? PseudoPath.FormId
                   : PseudoPath.None;

        // An identity term is one value per record, so a quantifier on it has nothing to fold over. Named here
        // rather than left to the walk, which would look for a lowercase field of that name and report a typo.
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
            return (null, $"predicate '{raw}': 'editorid' is a text term — use = != contains startswith exists missing in 'not in' (got '{OpStr(op)}').");

        // A presence op (exists/missing) takes NO operand — a trailing value is a mistake, refused loud rather than
        // silently ignored. Every other op REQUIRES an operand.
        if (op is Op.Exists or Op.Missing)
        {
            if (pseudo is PseudoPath.Winner or PseudoPath.FormId)
                return (null, $"predicate '{raw}': '{path}' always exists (every record has an identity and a winner) — a presence test on it can never filter. Use it with its own operators instead.");
            if (operand.Length != 0)
                return (null, $"predicate '{raw}': '{OpStr(op)}' is a presence test and takes no value (got '{operand}'). Write it as \"{path} {OpStr(op)}\".");
            return (new Predicate(text, segs, path, op, "", 0, LinkPath: linkSegs, LinkPathDisplay: linkDisplay, Pseudo: pseudo, PathFolds: pathFolds, LinkFolds: linkFolds, ParentHops: parentHops, LinkParentHops: linkParentHops), null);
        }

        if (operand.Length == 0)
            return (null, $"predicate '{raw}': no value after '{OpStr(op)}'.");

        // The membership ops (in / not in). On the identity path 'formid' the list must be FormIDs and the test is
        // the record's own identity (or, behind a link step, each reached target's identity) — the artifact @file
        // re-entry lane rides this form. On any OTHER path the list entries are compared against the LEAF's token
        // with the same equality vocabulary '=' uses (FormKey-canonical / numeric / case-insensitive string), so
        // \"Race in [XXXXXX:A.esm, YYYYYY:B.esm]\" keeps exactly the listed races. Either way the operand (inline
        // list or @file) is fully parsed and validated HERE, so a bad token, an unreadable file, or an empty list
        // refuses the whole call before any scan and the per-record test does no IO.
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
            var (members, mset, martifact, merr) = ParseValueList(text, operand);
            if (merr is not null) return (null, merr);
            return (new Predicate(text, segs, path, op, operand, 0, mset, martifact, LinkPath: linkSegs, LinkPathDisplay: linkDisplay, Pseudo: pseudo, RawMembers: members, PathFolds: pathFolds, LinkFolds: linkFolds, ParentHops: parentHops, LinkParentHops: linkParentHops), null);
        }
        if (pseudo == PseudoPath.FormId)
            return (null, $"predicate '{raw}': 'formid' takes the membership ops only — \"formid in <list>\" / \"formid not in <list>\" (a single record is \"formid in [XXXXXX:Plugin.esp]\").");

        // 5. a numeric operator demands a numeric operand — fail fast at parse (before any scan).
        double num = 0;
        if (IsNumericOp(op) && !TryNum(operand, out num))
            return (null, $"predicate '{raw}': operator '{OpStr(op)}' needs a numeric value, got '{operand}'.");

        return (new Predicate(text, segs, path, op, operand, num, LinkPath: linkSegs, LinkPathDisplay: linkDisplay, Pseudo: pseudo, PathFolds: pathFolds, LinkFolds: linkFolds, ParentHops: parentHops, LinkParentHops: linkParentHops), null);
    }

    /// <summary>Split one side's segments into bare field names plus their fold tokens: a bracket key beginning
    /// <c>*</c> is a quantifier, and every other bracket key stays an ordinary index/dict key. Returns the first
    /// refusal instead — an unknown quantifier word, the bare <c>[*]</c> set token (which is not a boolean), a
    /// <c>[*count]</c> that is not the end of the path, or one on the link side (a number carries no link).</summary>
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


    /// <summary>Strip a side's leading <c>*parent</c> hops and hand back the rest of the path. The grammar itself
    /// is <see cref="ContainmentIndex.SplitHops"/>, shared with the read walk, so the two surfaces cannot drift on
    /// the same mistake; only the <c>predicate '…':</c> voice is added here.</summary>
    static (string[] Tail, int Hops, string? Error) SplitParentHops(string raw, string[] segs, string display, bool isLinkLeft)
    {
        var (hops, err) = ContainmentIndex.SplitHops(segs, display, isLinkLeft);
        if (err is not null) return (segs, 0, $"predicate '{raw}': {err}");
        return (hops == 0 ? segs : segs[hops..], hops, null);
    }

    /// <summary>The token a fold is spelled with, for a message.</summary>
    static string FoldToken(Fold f) => PathFoldGrammar.Token(f);

    /// <summary>Parse a generalized (non-formid) membership list: same separators/wrapping as the formid grammar,
    /// but entries are arbitrary VALUE tokens (enum names, numbers, FormKeys) — validated only for non-emptiness.
    /// When every entry parses as a FormKey the pre-parsed set rides along for the fast identity-canonical test
    /// (a FormLink leaf against a big artifact list must not be O(n) per record). An @file target may be a plain
    /// token list or a result artifact (identity column = formids — useful against a FormLink leaf), with the
    /// artifact's epoch demand carried exactly like the formid form.</summary>
    static (IReadOnlyList<string>? Members, HashSet<FormKey>? Keys, ArtifactDemand? Artifact, string? Error) ParseValueList(string raw, string operand)
    {
        string content;
        ArtifactDemand? artifact = null;
        if (operand[0] == '@')
        {
            var path = operand.Substring(1).Trim().Trim('"', '\'');
            if (path.Length == 0)
                return (null, null, null, $"predicate '{raw}': '@' names a value-list file but no path follows it.");
            if (!Path.IsPathRooted(path))
                return (null, null, null, $"predicate '{raw}': value-list file '{path}' must be an ABSOLUTE path — the server resolves relative paths against its OWN working directory, not yours.");
            try { content = File.ReadAllText(path); }
            catch (Exception ex) { return (null, null, null, $"predicate '{raw}': could not read value-list file '{path}' — {ex.GetType().Name}: {ex.Message}"); }
            if (ResultArtifact.LooksLikeArtifact(content))
            {
                var (manifest, tokens, aerr) = ResultArtifact.ReadIdentity(path, content);
                if (aerr is not null) return (null, null, null, $"predicate '{raw}': {aerr}");
                if (!manifest!.Identity!.Equals("formid", StringComparison.OrdinalIgnoreCase))
                    return (null, null, null, $"predicate '{raw}': artifact '{path}' (from {manifest.Tool}) carries '{manifest.Identity}' identities, not FormIDs — nothing in it to test a value against.");
                artifact = new ArtifactDemand(path, manifest.Epoch);
                content = string.Join("\n", tokens!);
            }
        }
        else content = operand;

        var members = new List<string>();
        foreach (var t in content.Split(ListSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            var tok = t.Trim('[', ']', '"', '\'', ' ', '\t');
            if (tok.Length > 0) members.Add(tok);
        }
        if (members.Count == 0)
            return (null, null, null, $"predicate '{raw}': the value list is empty — give at least one entry.");

        HashSet<FormKey>? keys = null;
        var all = new HashSet<FormKey>();
        foreach (var m in members)
        {
            if (!TryFormKey(m, out var fk)) { all = null!; break; }
            all.Add(fk);
        }
        if (all is { Count: > 0 }) keys = all;
        return (members, keys, artifact, null);
    }

    /// <summary>Parse an <c>in</c>/<c>not in</c> operand into its FormKey set. Two forms: <c>@&lt;path&gt;</c> reads a
    /// list FILE (absolute path required — the server's working directory is not the caller's, so a relative path
    /// would resolve somewhere the caller can't predict); anything else is the INLINE list. Both use the same token
    /// grammar: FormIDs separated by commas and/or newlines — NEVER bare spaces, because a plugin filename can
    /// contain them (<c>123456:My Mod.esp</c>) — with optional surrounding brackets and quotes stripped per token,
    /// so a pasted JSON array (<c>["123456:A.esp", "234567:B.esp"]</c>) parses as-is. Every token must be a valid
    /// FormID and the set must be non-empty; any violation names itself and refuses the call.
    /// <para>An <c>@file</c> whose target is a result ARTIFACT (line 1 = manifest) yields its IDENTITY column as
    /// the list instead of raw tokens, and hands back the artifact's epoch obligation. A plain list file carries
    /// no manifest and no epoch claim.</para></summary>
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
            if (!Path.IsPathRooted(path))
                return (null, null, $"predicate '{raw}': formid-list file '{path}' must be an ABSOLUTE path — the server resolves relative paths against its OWN working directory, not yours.");
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
                    // ReadIdentity already excludes error rows (they carry raw failed inputs, not record
                    // identities), so a non-FormID here is a genuine mismatch: server-written success rows always
                    // carry valid formids.
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
            // ONE trim with whitespace IN the set, so interleaved wrapping ('[ "…" ]' — the spaced JSON-array
            // style) strips clean; a chained Trim().Trim('[',…) stops at the inner space and leaves a quote behind.
            var tok = t.Trim('[', ']', '"', '\'', ' ', '\t');
            if (tok.Length == 0) continue;
            try { set.Add(toKey(tok)); }
            catch (Exception ex)
            {
                // A plugin filename can legally CONTAIN a comma ('Foo, Bar.esp') — unrepresentable in this grammar
                // (commas always separate entries), and the shear leaves a token with a ':' but no plugin extension.
                // Name that cause on exactly that shape, so the refusal points at the comma, not a mystery token.
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

    /// <summary>Commas and newlines ONLY — a bare space is a legal character inside a plugin filename
    /// (<c>123456:My Mod.esp</c>), so it can never be a list separator.</summary>
    static readonly char[] ListSeparators = { ',', '\r', '\n' };

    static bool IsOpChar(char c) => c is '=' or '!' or '<' or '>';
    static bool IsNumericOp(Op op) => op is Op.Gt or Op.Ge or Op.Lt or Op.Le;
    static bool StartsWith(string s, int i, string op)
        => i + op.Length <= s.Length && string.CompareOrdinal(s, i, op, 0, op.Length) == 0;

    // ======================================================================
    //  EVALUATE — test one in-hand body against ALL predicates (ANDed).
    // ======================================================================

    /// <summary>Test one candidate body against every predicate (ANDed), updating the per-predicate accounting.
    /// Returns true iff all predicates are satisfied. Reuses <see cref="ReadEngine.ReadLeaf"/> for the value —
    /// so a filterable path is exactly a readable path. On a numeric-operator-vs-non-numeric-field mismatch sets
    /// <see cref="FatalError"/> and returns false (the scan aborts and surfaces it on the first value-bearing
    /// candidate). All predicates are read for their accounting even when an earlier one already disqualifies the
    /// AND, so the no-value signal is correct per predicate.</summary>
    public bool Matches(IMajorRecordGetter body)
    {
        if (_fatal is not null) return false;
        _scanned++;
        bool all = true;
        for (int k = 0; k < _predicates.Count; k++)
        {
            var p = _predicates[k];

            EvalKind kind;
            bool sat;
            if (p.LinkPath is not null)
            {
                // LINK STEP: collect the candidate's links under the LEFT path, resolve each target's winner
                // body from the bound view, evaluate the predicate's own (right) side on each — ANY-match. Targets
                // are cached across candidates (the same perk/spell recurs), and a per-target fault feeds the
                // accounting, never a throw out of the scan.
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
                // A list hop IS a no-such-field miss, so it keeps that bucket; the extra counter is what lets the
                // rollup tell a missing bracket from a mistyped name.
                case EvalKind.ListHop: _noField[k]++; _listHop[k]++; _noValue[k]++; _listHopOwner[k] ??= _lastListHopOwner; _listHopRemedy[k] ??= _lastListHopRemedy; all = false; break;
                // A quantified step on a non-list IS a no-such-field miss for the rollup; the extra counter is what
                // lets the sentence say the step's real cardinality rather than "mistyped path".
                case EvalKind.NotAList: _noField[k]++; _notList[k]++; _noValue[k]++; _notListWhat[k] ??= _lastNotList; all = false; break;
                // A '*parent' step on a record nothing contains is likewise a no-such-field miss for the rollup; the
                // extra counter is what lets the sentence name the child-bearing properties instead of a typo hint.
                case EvalKind.NoParent: _noField[k]++; _noParent[k]++; _noValue[k]++; _noParentWhat[k] ??= _lastNoParent; all = false; break;
                case EvalKind.Container: _container[k]++; _noValue[k]++; all = false; break;
                case EvalKind.Unreadable: _unreadable[k]++; _noValue[k]++; all = false; break;
                default: _noValue[k]++; all = false; break;   // Unset — a valid, value-less path
            }
        }
        return all;
    }

    /// <summary>How one predicate's evaluation on one record resolved: a DEFINITE verdict (the value was read and
    /// compared, or an identity/presence test decided), or one of the no-verdict classes the accounting keys on.
    /// Mirrors the leaf-note vocabulary: no-such-field / container / read-fault / genuinely-unset.</summary>
    enum EvalKind { Definite, NoField, ListHop, NotAList, NoParent, Container, Unreadable, Unset }

    /// <summary>The type of the record the most recent <c>*parent</c> hop found no containing record for, stashed
    /// for the rollup sentence.</summary>
    string? _lastNoParent;

    /// <summary>The collection field named by the most recent list-hop note, stashed for the rollup.</summary>
    string? _lastListHopOwner;

    /// <summary>And the remedy that note carried — composed by the read engine, which had the collection's element
    /// TYPE in hand and checked the trailing segment against it. The rollup quotes this rather than composing its
    /// own, so the per-record leaf note and the whole-scan sentence cannot disagree about the same path.</summary>
    string? _lastListHopRemedy;

    /// <summary>What a quantified step actually read where it was not a list, stashed for the rollup sentence.</summary>
    string? _lastNotList;

    /// <summary>Sentence-case a remedy fragment lifted from a leaf note, which is composed lowercase to read
    /// mid-sentence there. Leading punctuation (a quoted field name) passes through unchanged.</summary>
    static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>Classify a leaf note beginning "(no field": the read engine emits a bracket-aware variant when the
    /// path stepped THROUGH a list/dict, and that is a missing-bracket miss, not a mistyped name.</summary>
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

    /// <summary>Evaluate one predicate's own (non-link) side against one record. Shared by the top-level test and
    /// the link step's per-target test — so 'Perks-&gt;editorid' and a plain 'editorid' term can't drift. Sets
    /// <see cref="_fatal"/> on a typed predicate error (numeric op vs non-numeric field, unbound winner term).</summary>
    (bool Satisfied, EvalKind Kind) EvalCore(Predicate p, IMajorRecordGetter body)
    {
        // The `*parent` containment step: climb to the containing record FIRST, then run every term below on it.
        // That is what makes it a step — the winner term, editorid, formid membership, leaves and folds all read
        // the parent without a second rule each.
        if (p.ParentHops > 0)
        {
            var (hopped, miss) = HopToParent(body, p.ParentHops);
            if (miss is { } m) return (false, m);
            body = hopped!;
        }

        // The `winner` provenance term: reads the record's RESOLUTION off the bound view — never its body.
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

        // The editorid term: always available off the record header (live even where the deep body can't parse).
        if (p.Pseudo == PseudoPath.EditorId)
        {
            var eid = body.EditorID;
            if (p.Op is Op.Exists or Op.Missing)
            {
                bool present = !string.IsNullOrEmpty(eid);
                return (p.Op == Op.Exists ? present : !present, EvalKind.Definite);
            }
            // A null EditorID is a DEFINITE verdict either way, but the polarity must be right per op: a record
            // with no EditorID is unambiguously NOT EQUAL to any operand and NOT IN any list, so a blanket
            // non-match would silently drop every such record from '!='. The positive ops (=, contains,
            // startswith, in) keep the older editorid_contains= semantics: no EditorID never matches.
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
            return (ok, EvalKind.Definite);
        }

        // Identity-membership ops (in / not in) on 'formid': a pure FormKey set test — no body leaf is read.
        // Identity is always present, so the no-value accounting can never false-alarm on it.
        if (p.Pseudo == PseudoPath.FormId)
        {
            bool member = p.FormIds!.Contains(body.FormKey);
            return (p.Op == Op.In ? member : !member, EvalKind.Definite);
        }

        // The body-leaf side, quantified steps and all.
        return EvalOwnPath(p, body, 0);
    }

    /// <summary>The predicate's own (non-link) path from segment <paramref name="from"/> down: an ordinary tail
    /// reads its leaf, a quantified step navigates to the collection and folds the elements. Recurses, so a second
    /// quantified step inside the first composes without a second rule.</summary>
    (bool Satisfied, EvalKind Kind) EvalOwnPath(Predicate p, object obj, int from)
    {
        var segs = p.PathSegments;
        int q = FirstFold(p.PathFolds, from, segs.Length);
        if (q < 0)
            return DecideLeaf(p, ReadEngine.ReadLeaf(obj, from == 0 ? segs : segs[from..]));   // internal, same assembly — the by-construction read walk

        var (elems, parent, miss) = ElementsAt(obj, segs, p.PathFolds!, from, q);
        if (miss is { } m) return (false, m);
        var fold = p.PathFolds![q];
        if (fold == Fold.Count) return DecideLeaf(p, ReadEngine.LeafRead.Value(elems!.Count.ToString(CultureInfo.InvariantCulture)));
        return FoldOver(p, elems!, fold,
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

    /// <summary>Navigate to a quantified step's collection and hand back its elements (with the collection's owning
    /// parent, which the element's token emit needs). An ABSENT collection reads as EMPTY — the same reading
    /// <see cref="ReadEngine.KeywordKeys"/> already gives a record with no list, and it holds whether the LEAF is
    /// null or a substruct ABOVE it is (a record with no VirtualMachineAdapter carries no scripts either). A step
    /// that is not a list at all is a named no-verdict, never a silent non-match — judged on the step's DECLARED
    /// type, so a null non-list field is refused exactly as a carried one is.</summary>
    (List<object>? Elements, object? Parent, EvalKind? Miss) ElementsAt(object obj, string[] segs, Fold[] folds, int from, int q)
    {
        var (ok, val, declared, parent, note) = ReadEngine.NavigateTo(obj, segs[from..(q + 1)]);
        if (!ok)
        {
            // A mid-path substruct that is absent makes the collection absent, which reads as empty like any other
            // absent collection. Every other miss (no such field, a read fault) keeps its own no-verdict class.
            if (note == ReadEngine.AbsentNote) return (new List<object>(), parent, null);
            return (null, null, ClassifyMiss(note ?? ""));
        }
        if (NotListShape(declared, val) is { } what)
        {
            _lastNotList = $"'{segs[q]}{FoldToken(folds[q])}' reads as {what}, not a list";
            return (null, null, EvalKind.NotAList);
        }
        if (val is null) return (new List<object>(), parent, null);
        var list = new List<object>();
        foreach (var e in (System.Collections.IEnumerable)val) if (e is not null) list.Add(e);
        return (list, parent, null);
    }

    /// <summary>What a quantified step actually reads where it is not a list — null when it IS one. Keyed on the
    /// DECLARED type and on the same closed-interface test the read engine's emit side uses, so the two shapes that
    /// merely happen to enumerate (a raw byte slice, a keyed dict) are named rather than folded over.</summary>
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
        // Both strips, in that order: a CARRIED value reflects as BodyTemplateBinaryOverlay and a null one falls back
        // to the declared IBodyTemplateGetter, so without both the same field is named two ways on two records.
        return $"a single {RecordNaming.StripGetterInterface(RecordNaming.StripOverlay((val?.GetType() ?? t).Name))} value";
    }

    /// <summary>Fold one step's element verdicts into the record's. Same accounting shape
    /// <see cref="EvalLinkStep"/> uses over link targets: a definite verdict decides, and where an element could
    /// NOT be judged the no-verdict class carries out rather than a silent non-match. An EMPTY list is a definite
    /// verdict — <c>[*all]</c> and <c>[*none]</c> are vacuously true on it, <c>[*any]</c> false.
    /// <para>One unjudged element is enough to sink a fold that the judged ones have not already decided: an
    /// existential is decided by its first true, a universal (<c>[*all]</c>/<c>[*none]</c>) by its first
    /// counterexample, and anything short of that is a claim over elements one of which was never read. The class
    /// that carries out is the loudest one seen, so the rollup names a read fault as a read fault and a genuinely
    /// value-less element as unset — never the other way round.</para></summary>
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
        // The decided cases: a true settles any/none whatever else the list held, a false settles all.
        if (fold == Fold.Any && anyTrue) return (true, EvalKind.Definite);
        if (fold == Fold.NoneOf && anyTrue) return (false, EvalKind.Definite);
        if (fold == Fold.All && anyFalse) return (false, EvalKind.Definite);
        if (unjudged is { } u) return (false, u);
        if (anyVerdict) return (fold != Fold.Any, EvalKind.Definite);
        return (false, EvalKind.Unreadable);
    }

    /// <summary>Which no-verdict class wins when a fold saw more than one: a read fault outranks a schema miss,
    /// which outranks a container or a genuinely-unset element.</summary>
    static int NoVerdictRank(EvalKind k) => k switch
    {
        EvalKind.Unreadable => 4,
        EvalKind.ListHop => 3, EvalKind.NotAList => 3, EvalKind.NoField => 3, EvalKind.NoParent => 3,
        EvalKind.Container => 2,
        _ => 1,   // Unset
    };

    /// <summary>Classify a navigation miss into the no-verdict vocabulary the accounting keys on.</summary>
    EvalKind ClassifyMiss(string note)
    {
        if (note.StartsWith("(no field", StringComparison.Ordinal)) return ClassifyNoField(note);
        if (note.StartsWith("(unreadable", StringComparison.Ordinal)) return EvalKind.Unreadable;
        return EvalKind.Unset;
    }

    /// <summary>Decide one predicate against one leaf read — the shared tail of the plain path, a quantified step's
    /// element, and a <c>[*count]</c>'s number, so the three cannot drift on what an operator means.</summary>
    (bool Satisfied, EvalKind Kind) DecideLeaf(Predicate p, ReadEngine.LeafRead leaf)
    {
        // Presence ops (exists/missing) are the ONE case where a no-value CONTAINER leaf is a MATCH, not a miss:
        // they test whether the path resolves to a present, non-empty value (a scalar OR a carried
        // substruct/list), the "which records carry a VMAD/Effects/Conditions" query the value ops can't express.
        // The accounting stays honest: a DEFINITE verdict (Present or Absent) counts as read, so "exists returns 0
        // because the field is genuinely absent on all" is a true zero; only a no-such-field or a read-fault counts
        // as no-value, so a mistyped exists= path still fails LOUD. NoField/Unreadable match NEITHER op — an
        // unjudgeable record is asserted neither present nor absent.
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
            // Classify WHY there was no value, so the accounting can distinguish a MISTYPED path (no such field
            // anywhere) from a VALID-but-unset field (the path reads fine; there simply are no values in this
            // scope). The two look identical in a bare "0 matches" and conflating them sends a user hunting a
            // non-bug. Reason vocabulary is ReadLeaf's own notes: "(no field …" = mistyped/wrong-type; a leading
            // '[' = a container/list summary; "(unreadable …" = a Mutagen-parse FAULT, which must NOT read as
            // "unset" (that would assert a valid empty field where the truth is a read fault); anything else
            // (absent / null link / unresolved string) = a genuinely-unset valid field.
            var note = leaf.Note ?? "";
            if (note.StartsWith("(no field", StringComparison.Ordinal)) return (false, ClassifyNoField(note));
            if (note.StartsWith("(unreadable", StringComparison.Ordinal)) return (false, EvalKind.Unreadable);
            if (note.Length > 0 && note[0] == '[') return (false, EvalKind.Container);
            return (false, EvalKind.Unset);
        }

        // Generalized membership (in / not in on a LEAF path): the leaf's token against the member list,
        // '='-vocabulary equality per entry. A FormKey leaf against an all-FormKey list uses the pre-parsed set
        // (O(1) — the artifact-list case must not be linear per record).
        if (p.Op is Op.In or Op.NotIn)
        {
            bool member;
            if (p.FormIds is not null && TryFormKey(leaf.Token, out var lfk))
                member = p.FormIds.Contains(lfk);
            else
                member = p.RawMembers!.Any(m => ValueEquals(leaf.Token, m));
            return (p.Op == Op.In ? member : !member, EvalKind.Definite);
        }

        var (satisfied, err) = Compare(p, leaf);
        if (err is not null) { _fatal ??= err; return (false, EvalKind.Definite); }
        return (satisfied, EvalKind.Definite);
    }

    /// <summary>The <c>-&gt;</c> link step on one candidate: links under the LEFT path → each target's winner body (from
    /// the bound view, cached across candidates) → <see cref="EvalCore"/> on each — satisfied iff ANY target
    /// satisfies. No-verdict classification: a left-path miss reuses the leaf-note vocabulary; links that all fail
    /// to resolve/judge report Unreadable (the filter cannot judge this candidate — never a silent non-match
    /// dressed as a definite one).</summary>
    (bool Satisfied, EvalKind Kind) EvalLinkStep(Predicate p, IMajorRecordGetter body)
    {
        if (_fetchWinnerBody is null)
        {
            _fatal = "internal: a '->' link-step predicate was evaluated without a bound resolution context — this scan surface does not support it.";
            return (false, EvalKind.Definite);
        }
        if (p.LinkParentHops > 0)
        {
            var (hopped, miss) = HopToParent(body, p.LinkParentHops);
            if (miss is { } m) return (false, m);
            body = hopped!;
        }
        return EvalLinkPath(p, body, 0);
    }

    /// <summary>Climb <paramref name="hops"/> containment steps from one record to the record that contains it,
    /// fetching each parent's winner body through the same bound view the <c>-&gt;</c> step resolves through. A
    /// record with no containing record is a NAMED no-verdict, never a silent non-match: the rollup says which
    /// properties own children at all.</summary>
    (IMajorRecordGetter? Body, EvalKind? Miss) HopToParent(IMajorRecordGetter body, int hops)
    {
        if (_parentOf is null || _fetchWinnerBody is null)
        {
            _fatal = $"internal: a '{ContainmentIndex.ParentToken}' containment predicate was evaluated without a bound resolution context — this scan surface does not support it.";
            return (null, EvalKind.Definite);
        }
        for (int i = 0; i < hops; i++)
        {
            var pk = _parentOf(body.FormKey);
            if (pk is null) { _lastNoParent = RecordNaming.StripOverlay(body.GetType().Name); return (null, EvalKind.NoParent); }
            if (!_targetCache.TryGetValue(pk.Value, out var parent))
                _targetCache[pk.Value] = parent = _fetchWinnerBody(pk.Value);
            if (parent is null) return (null, EvalKind.Unreadable);   // the parent is indexed but its body would not fetch
            body = parent;
        }
        return (body, null);
    }

    /// <summary>The link step's left path from segment <paramref name="from"/> down. A quantified step there folds
    /// the per-element link steps ("no effect whose BaseEffect is a REQ_ one"), which is one winner fetch per
    /// element — the declared cost.</summary>
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

    /// <summary>Resolve one collected link set to its winner bodies and judge the predicate's own side on them —
    /// satisfied iff ANY target satisfies.</summary>
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

        bool anyVerdict = false, anyNoField = false, anyListHop = false;
        foreach (var fk in links)
        {
            if (!_targetCache.TryGetValue(fk, out var target))
                _targetCache[fk] = target = _fetchWinnerBody(fk);
            if (target is null) continue;                       // unresolvable target — can't judge through it
            var (sat, kind) = EvalCore(p, target);
            if (_fatal is not null) return (false, EvalKind.Definite);
            if (kind == EvalKind.Definite)
            {
                anyVerdict = true;
                if (sat) return (true, EvalKind.Definite);
            }
            else if (kind is EvalKind.NoField or EvalKind.ListHop or EvalKind.NoParent) { anyNoField = true; anyListHop |= kind == EvalKind.ListHop; }
        }
        if (anyVerdict) return (false, EvalKind.Definite);
        return (false, anyNoField ? (anyListHop ? EvalKind.ListHop : EvalKind.NoField) : EvalKind.Unreadable);
    }

    /// <summary>The three-state presence verdict for a leaf under <c>exists</c>/<c>missing</c>: a DEFINITE
    /// Present/Absent, or an unjudgeable NoField (the path is not a field on this record) / Unreadable (a Mutagen
    /// read fault). Only Present/Absent decide a match; NoField/Unreadable match NEITHER op and feed the
    /// accounting, so a mistyped presence path still fails loud rather than reading as a silent "0 matches".</summary>
    enum Presence { Present, Absent, NoField, Unreadable }

    /// <summary>Map a leaf read to its presence verdict. A round-trippable scalar is Present. A container/substruct
    /// summary (note starts with '[') is Present UNLESS it is an EMPTY list/dict (<see cref="ReadEngine.LeafRead.ContainerCount"/>
    /// == 0) — a modeled-but-empty field carries nothing, so it is Absent (the crucial empty-vs-carried split the
    /// display note alone can't give). A "(no field…" note is NoField, "(unreadable…" is Unreadable, and every other
    /// no-value note ((absent)/(null link)/(unresolved…)) is a valid-but-unset Absent.</summary>
    static Presence ClassifyPresence(ReadEngine.LeafRead leaf)
    {
        if (leaf.HasValue) return Presence.Present;
        var note = leaf.Note ?? "";
        if (note.StartsWith("(no field", StringComparison.Ordinal)) return Presence.NoField;
        if (note.StartsWith("(unreadable", StringComparison.Ordinal)) return Presence.Unreadable;
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
                // A [Flags] enum compares on its underlying numeric value, so `>= 65536` works on a field that
                // renders as "Body". Every other leaf compares on its numeric token; a non-numeric, non-flags
                // field is the fast typed FatalError.
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
                // On a [Flags] enum, equate by RESOLVED bit pattern so a numeric operand matches a name-rendered
                // field (`= 16` matches "Forearms"), a name matches a number-rendered one, and order/spacing of a
                // comma-combo stops mattering. Every other leaf keeps the token-vocabulary equality unchanged.
                bool eq;
                if (flags is { } feq && TryResolveBits(p.Operand, feq.EnumType, out var opBits))
                    eq = feq.Bits == opBits;
                else
                    eq = ValueEquals(token, p.Operand);
                return (p.Op == Op.Eq ? eq : !eq, null);
        }
    }

    /// <summary>The bitwise set-test (<c>has</c>): true iff EVERY bit of the operand is set on the field, other
    /// bits free — so a multi-slot BodyTemplate still matches the one slot asked for, the case <c>=</c> (exact
    /// value) and the range ops miss. On a [Flags] enum the operand is a bit value (decimal or <c>0x</c> hex) or a
    /// flag NAME; on a plain integer leaf it must be a bit value. A non-bitmask field, an unresolvable operand, or
    /// a zero mask is a typed error — never a silent non-match.</summary>
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
        // The three folds over the same bits: every bit of the operand set, at least one set, none set.
        return (p.Op switch
        {
            Op.HasAny => (leafBits & opBits) != 0,
            Op.HasNone => (leafBits & opBits) == 0,
            _ => (leafBits & opBits) == opBits,
        }, null);
    }

    /// <summary>Resolve a <c>has</c>/<c>=</c> operand against a [Flags] enum to its bit pattern: a numeric literal
    /// (decimal or <c>0x</c> hex) is the bits directly; otherwise it is parsed as a flag NAME (or comma-combo)
    /// against that enum. False if it is neither — the caller turns that into a typed error.</summary>
    static bool TryResolveBits(string operand, Type enumType, out ulong bits)
        => TryBits(operand, out bits) || ReadEngine.TryEnumBitsFromName(enumType, operand, out bits);

    /// <summary>Parse a bit value — decimal, or <c>0x</c>-prefixed hex (bitmasks read naturally in hex). Unsigned;
    /// a sign or a non-integer is rejected (those fall through to the flag-name path).</summary>
    static bool TryBits(string s, out ulong bits)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return ulong.TryParse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bits);
        return ulong.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out bits);
    }

    /// <summary>Equality across the token vocabulary: FormKey-canonical if BOTH sides are FormKeys (so a link
    /// compares as a FormKey, not a string), else numeric if both parse as numbers (so <c>0.50</c> matches a
    /// stored <c>0.5</c>), else case-insensitive string (enum names, <c>True</c>/<c>False</c>, plain strings).</summary>
    static bool ValueEquals(string token, string operand)
    {
        if (TryFormKey(token, out var a) && TryFormKey(operand, out var b)) return a == b;
        if (TryNum(token, out var x) && TryNum(operand, out var y)) return x.Equals(y);
        return string.Equals(token, operand, StringComparison.OrdinalIgnoreCase);
    }

    static bool TryNum(string s, out double d)
        => double.TryParse(s, NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out d);

    /// <summary>True only for a real FormKey string (<c>XXXXXX:Plugin.esp</c>). A plain number or enum name has no
    /// <c>:Plugin</c> tail, so it never parses here — the FormKey branch can't swallow a numeric/string compare.</summary>
    static bool TryFormKey(string s, out FormKey fk)
    {
        try { fk = FormKey.Factory(s.Trim()); return true; }
        catch { fk = default; return false; }
    }

    // ======================================================================
    //  ACCOUNTING — the loud "wrong path ≠ true zero" surface.
    // ======================================================================

    /// <summary>The line(s) appended to the result header so a value predicate's natural failure mode (a wrong
    /// path that reads nothing everywhere → false "0 matches") can never read as a confirmed true negative.
    /// Null when every predicate read values on a healthy fraction of candidates.
    /// <list type="bullet">
    /// <item>A predicate that read NO value on ANY candidate ⇒ a LOUD line (it necessarily produced 0 matches —
    /// likely a mistyped or container/list path).</item>
    /// <item>A predicate that read no value on MORE THAN HALF the candidates ⇒ a SOFT note (a path wrong for some
    /// scanned types in a mixed scan reads as a non-match there, not an error).</item>
    /// </list></summary>
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
                // No candidate read a value — but the CAUSE decides whether this is a wrong path or a correct path
                // over a value-less scope, and those need opposite next moves (fix the path vs. widen the scope). All
                // four keep the loud marker "yielded no readable value on any" (distinct from the SOFT "had no readable
                // value on" for a >half-but-not-all miss), then diverge on the actionable reason.
                const string loud = "yielded no readable value on any of";
                long unset = _noValue[k] - _noField[k] - _container[k] - _unreadable[k];   // what is left: genuinely-unset valid fields
                string reason;
                if (_noField[k] == _scanned && _noParent[k] > 0)
                {
                    // Nothing CONTAINS these records, so the hop has nowhere to go. Name the properties that own
                    // children at all — that is the whole reason, and it is derived from Mutagen, not a hand list.
                    reason = $"predicate field '{path}' {loud} {_scanned:N0} scanned record(s) — no record CONTAINS " +
                             $"{(_noParentWhat[k] is { } t ? $"a {t}" : "these records")}, on {_noParent[k]:N0} of them" +
                             (_noParent[k] == _scanned ? "" : "; on the rest the path is not a field at all") +
                             $". Containment runs from these properties only: {ContainmentIndex.ChildBearingSurface()}.";
                }
                else if (_noField[k] == _scanned && _notList[k] > 0)
                {
                    // The quantifier is the thing to drop, and no schema advice fits: the step exists, it is just
                    // not a list here, and the sentence names what it read instead.
                    reason = $"predicate field '{path}' {loud} {_scanned:N0} scanned record(s) — the quantified step " +
                             $"{_notListWhat[k] ?? "named there"} on {_notList[k]:N0} of them, so a fold has no elements to run over" +
                             (_notList[k] == _scanned ? "" : "; on the rest the path is not a field at all") +
                             ". Drop the quantifier, or point it at a list-valued field.";
                }
                else if (_noField[k] == _scanned && _listHop[k] > 0)
                {
                    // The deeper, more specific path must not get the vaguer advice: this is a missing bracket, not
                    // a mistyped name, and the schema is the wrong place to send the caller. One list hop is enough
                    // to say so — on a mixed scan the other types simply have no such field, which is stated too.
                    var owner = _listHopOwner[k];
                    // The remedy is the READ ENGINE's, not this rollup's: it checked the trailing segment against
                    // the collection's element type, so it knows whether this is a missing bracket or a leaf that
                    // is not a field on the element at all — a distinction no count here can make, and one the old
                    // sentence papered over by asserting a bracket and printing a placeholder path.
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
                else if (_noField[k] == 0 && _container[k] == 0 && _unreadable[k] == 0)
                    reason = $"predicate field '{path}' {loud} {_scanned:N0} scanned record(s) — but the field IS VALID; it is simply UNSET " +
                             $"(absent/null) on every one, so the path reads fine and there are just no values in this scope. Widen the scope, or " +
                             $"the value you want may live on a different field (e.g. a dialogue topic's player text is on DIAL 'Name', not INFO 'Prompt').";
                else
                    reason = $"predicate field '{path}' {loud} {_scanned:N0} scanned record(s) — a mix of no-such-field ({_noField[k]:N0}), " +
                             $"container/list ({_container[k]:N0}), read-fault ({_unreadable[k]:N0}), and unset ({unset:N0}); check it's a scalar " +
                             $"leaf that exists on these records.";
                (notes ??= new()).Add(reason + " 0 matches on that basis is NOT a confirmed 'nothing matches'.");
            }
            else if (_noValue[k] * 2 > _scanned)
                (notes ??= new()).Add(
                    $"note: '{path}' had no readable value on {_noValue[k]:N0} of {_scanned:N0} scanned record(s) " +
                    $"(absent or not a field on those types) — counted as non-matches there, not errors.");
        }
        return notes is null ? null : string.Join("\n", notes);
    }

    static string OpStr(Op op) => op switch
    {
        Op.Eq => "=", Op.Ne => "!=", Op.Gt => ">", Op.Ge => ">=", Op.Lt => "<", Op.Le => "<=",
        Op.Contains => "contains", Op.StartsWith => "startswith", Op.Has => "has", Op.HasAny => "has_any", Op.HasNone => "has_none",
        Op.Exists => "exists", Op.Missing => "missing",
        Op.In => "in", Op.NotIn => "not in", _ => "?",
    };

    static string Trunc(string s) => s.Length > 60 ? s.Substring(0, 60) + "…" : s;
}
