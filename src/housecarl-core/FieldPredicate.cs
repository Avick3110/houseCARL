using System.Globalization;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;

namespace HousecarlCore;

/// <summary>
/// A field-VALUE predicate over a record body — the query-side complement to <see cref="ReadEngine"/>.
///
/// <para>Where <c>cross_plugin_query</c> can already scope and shape results by a record's IDENTITY and LINKS
/// (type / editorid_contains / references), a <see cref="FieldPredicateSet"/> filters by a field's VALUE:
/// <c>"MagicSkill = Destruction"</c>, <c>"BasicStats.Damage &gt;= 50"</c>, <c>"Archetype.ActorValue = Infamy"</c>.
/// Multiple predicates are ANDed (the wire is <c>where: string[]</c>).</para>
///
/// <para><b>By construction (cornerstone §3).</b> The extraction is NOT a per-field table — it is the read
/// engine's own proven path-walk: each predicate pulls its candidate's value through the internal
/// <see cref="ReadEngine.ReadLeaf"/> (the same <c>ParseSegment</c>/<c>ResolveProperty</c>/<c>StepIntoElement</c>
/// navigation the read tools and the round-trip oracle drive), then compares the round-trippable token. So the
/// set of fields you can FILTER on IS the set of fields houseCARL can READ — every type, every depth, no
/// hand-kept list. The comparison vocabulary is fixed by the token forms <see cref="ReadEngine"/> emits (the
/// faithful inverse of the write engine's Coerce): enum NAME, invariant numeric round-trip, <c>True</c>/<c>False</c>,
/// <c>XXXXXX:Plugin.esp</c> FormKey.</para>
///
/// <para><b>Q3 — no silent wrong answer.</b> A value predicate's natural failure mode is a wrong field path that
/// reads no value on every candidate and looks like a true "0 matches." This type ACCOUNTS for why each
/// candidate didn't match (per-predicate value-read vs no-value counts) so the scan can fail LOUD when a path
/// is read-blind everywhere (<see cref="AccountingNote"/>), and a numeric operator pointed at a non-numeric
/// field is a fast, named <see cref="FatalError"/> on the first value-bearing candidate — never a whole-scan
/// silent skip.</para>
///
/// <para>Scope (v1): scalar-leaf paths only (incl. a concrete bracketed element like <c>Keywords[0]</c>). A whole
/// LIST leaf (<c>Keywords</c>, <c>Effects</c>) reads as a no-value container summary, so a list path is surfaced
/// by the Q3 accounting, never silently matched — list→FormID membership is <c>references=</c>'s job. A wildcard
/// over a list (<c>Effects[*].Magnitude &gt; 50</c>) is a deliberate future extension, not v1.</para>
/// </summary>
public sealed class FieldPredicateSet
{
    /// <summary>The seven v1 operators. <see cref="Gt"/>/<see cref="Ge"/>/<see cref="Lt"/>/<see cref="Le"/> are
    /// numeric-only; <see cref="Contains"/> is a case-insensitive substring; <see cref="Eq"/>/<see cref="Ne"/>
    /// compare across the whole token vocabulary (FormKey-canonical, else numeric, else case-insensitive string).</summary>
    enum Op { Eq, Ne, Gt, Ge, Lt, Le, Contains }

    /// <summary>One parsed predicate: the split path segments (fed straight to <see cref="ReadEngine.ReadLeaf"/>),
    /// the operator, the raw operand, and — for a numeric operator — the operand pre-parsed to a double (validated
    /// at parse, so a non-numeric operand under <c>&gt;</c>/<c>&lt;</c> fails the whole call before any scan).</summary>
    sealed record Predicate(string Text, string[] PathSegments, string PathDisplay, Op Op, string Operand, double NumericOperand);

    readonly IReadOnlyList<Predicate> _predicates;
    readonly long[] _valueRead;   // per-predicate: candidates whose path read SOME value
    readonly long[] _noValue;     // per-predicate: candidates whose path read NO value (absent / no-such-field / container / fault)
    long _scanned;
    string? _fatal;

    FieldPredicateSet(IReadOnlyList<Predicate> predicates)
    {
        _predicates = predicates;
        _valueRead = new long[predicates.Count];
        _noValue = new long[predicates.Count];
    }

    /// <summary>Set once when a numeric operator meets a non-numeric field value on the first value-bearing
    /// candidate — a typed predicate error. The scan checks this and aborts, surfacing it as a recoverable error
    /// (never a silent skip). Null while the predicate is well-typed.</summary>
    public string? FatalError => _fatal;

    /// <summary>Candidate bodies tested so far — the denominator the Q3 accounting reports against.</summary>
    public long Scanned => _scanned;

    // ======================================================================
    //  PARSE — "<path> <op> <value>", longest-match the operator.
    //  The path is dotted/bracketed identifiers (no whitespace, no operator
    //  char), so it ends at the first space or operator char; the operand is
    //  the remainder. 'contains' is a whitespace-delimited word operator.
    // ======================================================================

    /// <summary>Parse the wire <c>where</c> list into an evaluable set, or return the FIRST parse error (so a
    /// malformed predicate refuses the whole call before scanning — Q3). An empty list is a parse error: a
    /// caller passing <c>where</c> at all means to filter.</summary>
    public static (FieldPredicateSet? Set, string? Error) Parse(IReadOnlyList<string> where)
    {
        var list = new List<Predicate>(where.Count);
        foreach (var raw in where)
        {
            var (p, err) = ParseOne(raw);
            if (err is not null) return (null, err);
            list.Add(p!);
        }
        if (list.Count == 0) return (null, "where= was empty — give at least one predicate like \"MagicSkill = Destruction\".");
        return (new FieldPredicateSet(list), null);
    }

    static (Predicate?, string?) ParseOne(string raw)
    {
        var text = (raw ?? "").Trim();
        if (text.Length == 0) return (null, "empty predicate in where= (expected \"<path> <op> <value>\").");

        // 1. path — the leading run of non-whitespace, non-operator-char characters.
        int i = 0;
        while (i < text.Length && !char.IsWhiteSpace(text[i]) && !IsOpChar(text[i])) i++;
        var path = text.Substring(0, i);
        if (path.Length == 0)
            return (null, $"predicate '{raw}': no field path before the operator (expected \"<path> <op> <value>\").");

        // 2. skip whitespace to the operator.
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        if (i >= text.Length)
            return (null, $"predicate '{raw}': no operator. Use one of = != > >= < <= contains, e.g. \"{path} = <value>\".");

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
            else return (null, $"predicate '{raw}': unrecognized operator at '{text.Substring(i)}'. Use = != > >= < <= contains.");
        }
        else
        {
            int w = i;
            while (w < text.Length && !char.IsWhiteSpace(text[w])) w++;
            var word = text.Substring(i, w - i);
            if (!word.Equals("contains", StringComparison.OrdinalIgnoreCase))
                return (null, $"predicate '{raw}': unrecognized operator '{word}'. Use = != > >= < <= or contains.");
            op = Op.Contains;
            after = w;
        }

        // 4. operand — the remainder, trimmed. (For 'contains' the operand may contain operator chars; we already
        //    consumed the operator positionally, so that's fine.)
        var operand = text.Substring(after).Trim();
        if (operand.Length == 0)
            return (null, $"predicate '{raw}': no value after '{OpStr(op)}'.");

        // 5. a numeric operator demands a numeric operand — fail fast at parse (before any scan).
        double num = 0;
        if (IsNumericOp(op) && !TryNum(operand, out num))
            return (null, $"predicate '{raw}': operator '{OpStr(op)}' needs a numeric value, got '{operand}'.");

        var segs = path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segs.Length == 0)
            return (null, $"predicate '{raw}': '{path}' is not a usable field path.");
        return (new Predicate(text, segs, path, op, operand, num), null);
    }

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
    /// AND, so the Q3 no-value signal is correct per predicate.</summary>
    public bool Matches(IMajorRecordGetter body)
    {
        if (_fatal is not null) return false;
        _scanned++;
        bool all = true;
        for (int k = 0; k < _predicates.Count; k++)
        {
            var p = _predicates[k];
            var leaf = ReadEngine.ReadLeaf(body, p.PathSegments);   // internal, same assembly — the by-construction read walk
            if (!leaf.HasValue) { _noValue[k]++; all = false; continue; }
            _valueRead[k]++;
            var (satisfied, err) = Compare(p, leaf.Token);
            if (err is not null) { _fatal ??= err; return false; }
            if (!satisfied) all = false;
        }
        return all;
    }

    static (bool satisfied, string? error) Compare(Predicate p, string token)
    {
        switch (p.Op)
        {
            case Op.Gt or Op.Ge or Op.Lt or Op.Le:
                if (!TryNum(token, out var tv))
                    return (false, $"predicate '{p.Text}': operator '{OpStr(p.Op)}' needs a numeric field, but '{p.PathDisplay}' read '{Trunc(token)}', not a number.");
                double ov = p.NumericOperand;
                bool num = p.Op switch { Op.Gt => tv > ov, Op.Ge => tv >= ov, Op.Lt => tv < ov, Op.Le => tv <= ov, _ => false };
                return (num, null);

            case Op.Contains:
                return (token.Contains(p.Operand, StringComparison.OrdinalIgnoreCase), null);

            default: // Eq / Ne
                bool eq = ValueEquals(token, p.Operand);
                return (p.Op == Op.Eq ? eq : !eq, null);
        }
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
    //  Q3 ACCOUNTING — the loud "wrong path ≠ true zero" surface.
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
            var path = _predicates[k].PathDisplay;
            if (_valueRead[k] == 0)
                (notes ??= new()).Add(
                    $"predicate field '{path}' yielded no readable value on any of {_scanned:N0} scanned record(s) — likely a mistyped path, " +
                    $"or a container/list path (use a scalar leaf like 'Archetype.ActorValue', or references= for list→FormID membership). " +
                    $"0 matches on that basis is NOT a confirmed 'nothing matches'.");
            else if (_noValue[k] * 2 > _scanned)
                (notes ??= new()).Add(
                    $"note: '{path}' had no readable value on {_noValue[k]:N0} of {_scanned:N0} scanned record(s) " +
                    $"(absent or not a field on those types) — counted as non-matches there, not errors.");
        }
        return notes is null ? null : string.Join("\n", notes);
    }

    static string OpStr(Op op) => op switch
    {
        Op.Eq => "=", Op.Ne => "!=", Op.Gt => ">", Op.Ge => ">=", Op.Lt => "<", Op.Le => "<=", Op.Contains => "contains", _ => "?",
    };

    static string Trunc(string s) => s.Length > 60 ? s.Substring(0, 60) + "…" : s;
}
