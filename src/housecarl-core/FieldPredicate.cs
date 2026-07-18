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
/// <para>Scope: the value operators take scalar-leaf paths only (incl. a concrete bracketed element like
/// <c>Keywords[0]</c>). A whole LIST leaf (<c>Keywords</c>, <c>Effects</c>) reads as a no-value container summary,
/// so a value predicate on a list path is surfaced by the Q3 accounting, never silently matched — list→FormID
/// membership is <c>references=</c>'s job. The PRESENCE operators (<c>exists</c>/<c>missing</c>) are the exception:
/// they DO match a carried substruct/list leaf (present and non-empty), the "which records carry a VMAD/Effects"
/// query (#197). The MEMBERSHIP operators (<c>formid in</c>/<c>formid not in</c> a supplied list — #226) are the
/// other non-leaf case: they test the record's IDENTITY against a pre-parsed FormKey set, no read walk at all.
/// A wildcard over a list (<c>Effects[*].Magnitude &gt; 50</c>) is a deliberate future extension.</para>
/// </summary>
public sealed class FieldPredicateSet
{
    /// <summary>The operators. <see cref="Gt"/>/<see cref="Ge"/>/<see cref="Lt"/>/<see cref="Le"/> are
    /// numeric-only; <see cref="Contains"/> is a case-insensitive substring; <see cref="Eq"/>/<see cref="Ne"/>
    /// compare across the whole token vocabulary (FormKey-canonical, else numeric, else case-insensitive string).
    /// <see cref="Has"/> is a BITWISE set-test for a <c>[Flags]</c> enum (or plain integer) leaf — true iff every
    /// bit of the operand is set on the field, regardless of other bits — so a multi-slot BodyTemplate still
    /// matches the one slot asked for, which <see cref="Eq"/> (exact value) and the range ops cannot express. Its
    /// operand is a bit value (decimal or <c>0x</c> hex) or a flag NAME.
    /// <see cref="Exists"/>/<see cref="Missing"/> are PRESENCE tests that take NO operand — true iff the path
    /// resolves to a present, NON-EMPTY value (a scalar OR a carried substruct/list) / its complement. They are the
    /// only operators that MATCH a no-value container leaf: the "which records CARRY a VirtualMachineAdapter /
    /// Effects / Conditions" query (#197), which the value operators (needing a scalar leaf) cannot express.
    /// <see cref="In"/>/<see cref="NotIn"/> are IDENTITY-membership tests against a supplied FormID list — the
    /// reconciliation subtraction "every record except these already-claimed ones" (#226). They take the pseudo-path
    /// <c>formid</c> (the record's own identity, not a body leaf — deliberately outside the read walk, matching the
    /// read cleave where identity sits beside Fields) and a list operand: inline comma-separated FormIDs, or
    /// <c>@&lt;absolute path&gt;</c> naming a file of them. Restricted to <c>formid</c> at parse (a named refusal on any
    /// other path) so a future generalization to leaf-value membership is an extension, not a behavior change.</summary>
    enum Op { Eq, Ne, Gt, Ge, Lt, Le, Contains, Has, Exists, Missing, In, NotIn }

    /// <summary>One parsed predicate: the split path segments (fed straight to <see cref="ReadEngine.ReadLeaf"/>),
    /// the operator, the raw operand, and — for a numeric operator — the operand pre-parsed to a double (validated
    /// at parse, so a non-numeric operand under <c>&gt;</c>/<c>&lt;</c> fails the whole call before any scan).
    /// <paramref name="FormIds"/> is the pre-parsed membership set for <see cref="Op.In"/>/<see cref="Op.NotIn"/>
    /// (file already read + every token validated at parse — the scan never does IO), null for every other op.</summary>
    sealed record Predicate(string Text, string[] PathSegments, string PathDisplay, Op Op, string Operand, double NumericOperand,
                            HashSet<FormKey>? FormIds = null);

    readonly IReadOnlyList<Predicate> _predicates;
    readonly long[] _valueRead;   // per-predicate: candidates whose path read SOME value
    readonly long[] _noValue;     // per-predicate: candidates whose path read NO value (any reason below)
    readonly long[] _noField;     // per-predicate SUBSET of _noValue: the path is not a field on the record (mistyped / wrong for this type)
    readonly long[] _container;   // per-predicate SUBSET of _noValue: the path resolves to a container/list, not a scalar leaf
    readonly long[] _unreadable;  // per-predicate SUBSET of _noValue: the path READ FAULTED (Mutagen-unparseable content) — a fault, NOT an unset value
    long _scanned;
    string? _fatal;

    FieldPredicateSet(IReadOnlyList<Predicate> predicates)
    {
        _predicates = predicates;
        _valueRead = new long[predicates.Count];
        _noValue = new long[predicates.Count];
        _noField = new long[predicates.Count];
        _container = new long[predicates.Count];
        _unreadable = new long[predicates.Count];
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
            return (null, $"predicate '{raw}': no operator. Use one of = != > >= < <= contains has exists missing in 'not in', e.g. \"{path} = <value>\" or \"{path} exists\".");

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
            else return (null, $"predicate '{raw}': unrecognized operator at '{text.Substring(i)}'. Use = != > >= < <= contains has exists missing in 'not in'.");
        }
        else
        {
            int w = i;
            while (w < text.Length && !char.IsWhiteSpace(text[w])) w++;
            var word = text.Substring(i, w - i);
            if (word.Equals("contains", StringComparison.OrdinalIgnoreCase)) op = Op.Contains;
            else if (word.Equals("has", StringComparison.OrdinalIgnoreCase)) op = Op.Has;
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
                return (null, $"predicate '{raw}': unrecognized operator '{word}'. Use = != > >= < <= contains has exists missing in or 'not in'.");
            after = w;
        }

        // 4. operand — the remainder, trimmed. (For 'contains' the operand may contain operator chars; we already
        //    consumed the operator positionally, so that's fine.)
        var operand = text.Substring(after).Trim();

        var segs = path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segs.Length == 0)
            return (null, $"predicate '{raw}': '{path}' is not a usable field path.");

        // A presence op (exists/missing) takes NO operand — a trailing value is a mistake, refused loud (Q3, never
        // silently ignored). Every other op REQUIRES an operand.
        if (op is Op.Exists or Op.Missing)
        {
            if (operand.Length != 0)
                return (null, $"predicate '{raw}': '{OpStr(op)}' is a presence test and takes no value (got '{operand}'). Write it as \"{path} {OpStr(op)}\".");
            return (new Predicate(text, segs, path, op, "", 0), null);
        }

        if (operand.Length == 0)
            return (null, $"predicate '{raw}': no value after '{OpStr(op)}'.");

        // The membership ops (in / not in) test the record's IDENTITY, not a body leaf, so their path is the fixed
        // pseudo-path 'formid' — any other path is refused NAMED (never silently read as a leaf), which keeps a
        // future leaf-value generalization an extension rather than a behavior change. The operand (inline list or
        // @file) is fully parsed + validated HERE, so a bad token, an unreadable file, or an empty list refuses the
        // whole call before any scan (Q3) and the per-record test is a pure set lookup (no IO in the scan).
        if (op is Op.In or Op.NotIn)
        {
            if (!path.Equals("formid", StringComparison.OrdinalIgnoreCase))
                return (null, $"predicate '{raw}': '{OpStr(op)}' tests the record's IDENTITY and takes the path 'formid' only " +
                              $"(got '{path}') — write \"formid {OpStr(op)} <list>\". Membership over a field's VALUE is not supported (yet); " +
                              $"use '=' per value, or references= for list→FormID membership.");
            var (set, lerr) = ParseFormIdList(raw, operand);
            if (lerr is not null) return (null, lerr);
            return (new Predicate(text, segs, path, op, operand, 0, set), null);
        }

        // 5. a numeric operator demands a numeric operand — fail fast at parse (before any scan).
        double num = 0;
        if (IsNumericOp(op) && !TryNum(operand, out num))
            return (null, $"predicate '{raw}': operator '{OpStr(op)}' needs a numeric value, got '{operand}'.");

        return (new Predicate(text, segs, path, op, operand, num), null);
    }

    /// <summary>Parse an <c>in</c>/<c>not in</c> operand into its FormKey set. Two forms: <c>@&lt;path&gt;</c> reads a
    /// list FILE (absolute path required — the server's working directory is not the caller's, so a relative path
    /// would resolve somewhere the caller can't predict); anything else is the INLINE list. Both use the same token
    /// grammar: FormIDs separated by commas and/or newlines — NEVER bare spaces, because a plugin filename can
    /// contain them (<c>123456:My Mod.esp</c>) — with optional surrounding brackets and quotes stripped per token,
    /// so a pasted JSON array (<c>["123456:A.esp", "234567:B.esp"]</c>) parses as-is. Every token must be a valid
    /// FormID and the set must be non-empty; any violation names itself and refuses the call (Q3).</summary>
    static (HashSet<FormKey>?, string?) ParseFormIdList(string raw, string operand)
    {
        string content;
        bool fromFile = operand[0] == '@';
        if (fromFile)
        {
            var path = operand.Substring(1).Trim().Trim('"');
            if (path.Length == 0)
                return (null, $"predicate '{raw}': '@' names a formid-list file but no path follows it.");
            if (!Path.IsPathRooted(path))
                return (null, $"predicate '{raw}': formid-list file '{path}' must be an ABSOLUTE path — the server resolves relative paths against its OWN working directory, not yours.");
            try { content = File.ReadAllText(path); }
            catch (Exception ex) { return (null, $"predicate '{raw}': could not read formid-list file '{path}' — {ex.GetType().Name}: {ex.Message}"); }
        }
        else content = operand;

        var set = new HashSet<FormKey>();
        foreach (var t in content.Split(ListSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            var tok = t.Trim().Trim('[', ']', '"', '\'').Trim();
            if (tok.Length == 0) continue;
            try { set.Add(FormKey.Factory(tok)); }
            catch (Exception ex)
            {
                return (null, $"predicate '{raw}': list entry '{tok}'{(fromFile ? $" (in the @file)" : "")} is not a FormID ({ex.Message}). " +
                              "Expected 'XXXXXX:Plugin.esp' entries separated by commas or newlines.");
            }
        }
        if (set.Count == 0)
            return (null, $"predicate '{raw}': the formid list{(fromFile ? " file" : "")} is empty — give at least one 'XXXXXX:Plugin.esp'.");
        return (set, null);
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
    /// AND, so the Q3 no-value signal is correct per predicate.</summary>
    public bool Matches(IMajorRecordGetter body)
    {
        if (_fatal is not null) return false;
        _scanned++;
        bool all = true;
        for (int k = 0; k < _predicates.Count; k++)
        {
            var p = _predicates[k];

            // Identity-membership ops (in / not in): a pure FormKey set test — no body leaf is read (the 'formid'
            // pseudo-path is identity, which the read cleave keeps BESIDE the fields). Identity is always present,
            // so the predicate counts as value-read and the Q3 no-value accounting can never false-alarm on it.
            if (p.Op is Op.In or Op.NotIn)
            {
                _valueRead[k]++;
                bool member = p.FormIds!.Contains(body.FormKey);
                if (p.Op == Op.In ? !member : member) all = false;
                continue;
            }

            var leaf = ReadEngine.ReadLeaf(body, p.PathSegments);   // internal, same assembly — the by-construction read walk

            // Presence ops (exists/missing) are the ONE case where a no-value CONTAINER leaf is a MATCH, not a miss:
            // they test whether the path resolves to a present, non-empty value (a scalar OR a carried
            // substruct/list), the "which records carry a VMAD/Effects/Conditions" query the value ops can't express
            // (#197). Handled BEFORE the value-predicate no-value classification below. Accounting stays Q3-honest: a
            // DEFINITE verdict (Present or Absent) counts as read, so "exists returns 0 because the field is
            // genuinely absent on all" is a true zero (no false alarm); only a no-such-field or a read-fault is a
            // no-value residue, so a mistyped exists= path still fails LOUD. NoField/Unreadable match NEITHER op —
            // an unjudgeable record is asserted neither present nor absent.
            if (p.Op is Op.Exists or Op.Missing)
            {
                bool present;
                switch (ClassifyPresence(leaf))
                {
                    case Presence.Present: _valueRead[k]++; present = true; break;
                    case Presence.Absent:  _valueRead[k]++; present = false; break;
                    case Presence.NoField: _noField[k]++; _noValue[k]++; all = false; continue;    // not a field here — can't judge
                    default:               _unreadable[k]++; _noValue[k]++; all = false; continue;  // read fault — unjudgeable
                }
                if (!(p.Op == Op.Exists ? present : !present)) all = false;
                continue;
            }

            if (!leaf.HasValue)
            {
                // Classify WHY there was no value, so the Q3 accounting can distinguish a MISTYPED path (no such
                // field anywhere) from a VALID-but-unset field (the path reads fine; there simply are no values in
                // this scope) — the two look identical in a bare "0 matches", and conflating them sent a reporter
                // hunting a non-bug (HCBR-2026-07-12: 'Prompt' is a real INFO field, just unset on all 531 scanned).
                // Reason vocabulary is ReadLeaf's own notes: "(no field …" = mistyped/wrong-type; a leading '[' =
                // a container/list summary; "(unreadable …" = a Mutagen-parse FAULT (must NOT read as "unset" — that
                // would confidently assert a valid empty field where the truth is a read fault, Q3); anything else
                // (absent / null link / unresolved string) = a genuinely-unset valid field.
                var note = leaf.Note ?? "";
                if (note.StartsWith("(no field", StringComparison.Ordinal)) _noField[k]++;
                else if (note.StartsWith("(unreadable", StringComparison.Ordinal)) _unreadable[k]++;
                else if (note.Length > 0 && note[0] == '[') _container[k]++;
                _noValue[k]++; all = false; continue;
            }
            _valueRead[k]++;
            var (satisfied, err) = Compare(p, leaf);
            if (err is not null) { _fatal ??= err; return false; }
            if (!satisfied) all = false;
        }
        return all;
    }

    /// <summary>The three-state presence verdict for a leaf under <c>exists</c>/<c>missing</c>: a DEFINITE
    /// Present/Absent, or an unjudgeable NoField (the path is not a field on this record) / Unreadable (a Mutagen
    /// read fault). Only Present/Absent decide a match; NoField/Unreadable match NEITHER op and feed the Q3
    /// accounting, so a mistyped presence path still fails loud (never a silent "0 matches").</summary>
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
            case Op.Has:
                return CompareHas(p, token, flags);

            case Op.Gt or Op.Ge or Op.Lt or Op.Le:
                // A [Flags] enum compares on its underlying numeric value — so `>= 65536` no longer ERRORS on a
                // field that renders as "Body". Every other leaf compares on its numeric token, exactly as before
                // (a non-numeric, non-flags field is still the fast typed FatalError — Q3).
                double tv;
                if (flags is { } fnum) tv = fnum.Bits;
                else if (!TryNum(token, out tv))
                    return (false, $"predicate '{p.Text}': operator '{OpStr(p.Op)}' needs a numeric field, but '{p.PathDisplay}' read '{Trunc(token)}', not a number.");
                double ov = p.NumericOperand;
                bool num = p.Op switch { Op.Gt => tv > ov, Op.Ge => tv >= ov, Op.Lt => tv < ov, Op.Le => tv <= ov, _ => false };
                return (num, null);

            case Op.Contains:
                return (token.Contains(p.Operand, StringComparison.OrdinalIgnoreCase), null);

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
    /// a zero mask is a typed error — never a silent non-match (Q3).</summary>
    static (bool satisfied, string? error) CompareHas(Predicate p, string token, ReadEngine.FlagBits? flags)
    {
        ulong leafBits, opBits;
        if (flags is { } fi)
        {
            leafBits = fi.Bits;
            if (!TryResolveBits(p.Operand, fi.EnumType, out opBits))
                return (false, $"predicate '{p.Text}': 'has' value '{p.Operand}' is not a bit value or a valid {fi.EnumType.Name} flag name.");
        }
        else if (TryBits(token, out leafBits))   // a plain integer leaf — bit-test its numeric value
        {
            if (!TryBits(p.Operand, out opBits))
                return (false, $"predicate '{p.Text}': 'has' value '{p.Operand}' must be a bit value (decimal or 0x hex) for the integer field '{p.PathDisplay}'.");
        }
        else
            return (false, $"predicate '{p.Text}': 'has' needs a flags/bitmask or integer field, but '{p.PathDisplay}' read '{Trunc(token)}', not a number.");

        if (opBits == 0)
            return (false, $"predicate '{p.Text}': 'has 0' tests no bits — give a non-zero bit value or a flag name.");
        return ((leafBits & opBits) == opBits, null);
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
            {
                // No candidate read a value — but the CAUSE decides whether this is a wrong path or a correct path
                // over a value-less scope, and those need opposite next moves (fix the path vs. widen the scope). All
                // four keep the loud marker "yielded no readable value on any" (distinct from the SOFT "had no readable
                // value on" for a >half-but-not-all miss), then diverge on the actionable reason.
                const string loud = "yielded no readable value on any of";
                long unset = _noValue[k] - _noField[k] - _container[k] - _unreadable[k];   // the residue: genuinely-unset valid fields
                string reason;
                if (_noField[k] == _scanned)
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
        Op.Contains => "contains", Op.Has => "has", Op.Exists => "exists", Op.Missing => "missing",
        Op.In => "in", Op.NotIn => "not in", _ => "?",
    };

    static string Trunc(string s) => s.Length > 60 ? s.Substring(0, 60) + "…" : s;
}
