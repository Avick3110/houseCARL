using System.Globalization;
using System.Reflection;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlCore;

/// <summary>One field read off a record: a round-trippable <see cref="Token"/> when <see cref="HasValue"/> is
/// true, else a <see cref="Note"/> explaining why there is no value (absent optional, no such field, a non-leaf
/// container, or an isolated read fault). The public, structured form of the internal <c>LeafRead</c> — what the
/// MCP server's read tools emit.</summary>
public sealed record FieldValue(string Path, bool HasValue, string? Token, string? Note);

/// <summary>A located record read out as structured fields — the public (params)->(result) result the MCP
/// server's read tools return (the §8.4 read cleave). Identity (<see cref="Type"/> / <see cref="FormKey"/> /
/// <see cref="EditorId"/>) plus the requested (or all modeled) field reads.</summary>
public sealed record RecordFields(string Type, string FormKey, string? EditorId, IReadOnlyList<FieldValue> Fields);

/// <summary>
/// Step 6 — the reflection-driven READ surface. The symmetric partner to <see cref="WriteEngine"/>.
///
/// Where the write engine coerces a string token INTO a typed value and Sets it
/// (<c>WriteEngine.Coerce</c>), the read engine reflects a record's modeled field OUT to a string
/// token that is the faithful <b>inverse of Coerce</b> — so reading a value and writing that exact
/// token straight back is a byte-level no-op. That inverse is enforced by construction, not by
/// inspection: <c>ReadProof</c>'s round-trip oracle drives every coercible leaf the write surface
/// drives and asserts read→write-back is byte-identical, so any drift between this emitter and
/// Coerce fails loud (the read analog of write-proof).
///
/// Scope (Aaron 2026-06-01): PER-PLUGIN read — "read record R exactly as plugin P defines it", the
/// mirror of <c>set_field</c>. Load-order winning-record resolution waits for the load-order
/// simulation layer (a later wave; the conflict tree builds on it).
///
/// Navigation is REUSED from the write engine (<c>ParseSegment</c> / <c>ResolveProperty</c> /
/// <c>StepIntoElement</c>) so read and write can never disagree on how a path resolves — but the
/// read walk never materialises an absent substruct (reading must not mutate; an absent optional
/// is surfaced, not created). Per-record/per-leaf fault isolation: a Mutagen-unparseable field
/// names itself loud and never crashes the read (Q3; the product-read-path requirement logged at
/// the write-surface completion sweep).
///
/// Modes: <c>read</c> (resolve a record + emit its fields as round-trippable tokens).
/// </summary>
public static class ReadEngine
{
    /// <summary>The outcome of reading one leaf. <see cref="HasValue"/> ⇒ <see cref="Token"/> is a
    /// round-trippable value (the inverse of Coerce). Otherwise <see cref="Note"/> explains why there
    /// is no value to round-trip (absent optional, no such field, a non-leaf container, or an
    /// isolated read fault). The round-trip oracle drives ONLY <see cref="HasValue"/> reads.
    ///
    /// <para><see cref="Flags"/> is additive METADATA carried only for a <c>[Flags]</c> enum leaf: the underlying
    /// bit pattern + enum type, so the query predicate (<c>where=</c>) can bit-test (<c>has</c>) and compare
    /// numerically WITHOUT tripping over the name/number rendering split that <c>[Flags].ToString()</c> produces
    /// (named bits → "Body"; an unnamed modder slot → "8388608"). The <see cref="Token"/> is unchanged — the
    /// round-trip oracle still drives the same display token — so this is invisible to read/write/diff.</para></summary>
    internal readonly record struct LeafRead(bool HasValue, string Token, string? Note, FlagBits? Flags = null)
    {
        public static LeafRead Value(string token) => new(true, token, null);
        public static LeafRead FlagsValue(string token, FlagBits bits) => new(true, token, null, bits);
        public static LeafRead None(string note) => new(false, "", note);
        public override string ToString() => HasValue ? Token : Note ?? "(none)";
    }

    /// <summary>The bit-test view of a <c>[Flags]</c> enum leaf — the unsigned bit pattern plus the enum
    /// <see cref="Type"/> (so a predicate's operand given as a flag NAME, e.g. <c>has Body</c>, resolves against
    /// the same enum). Populated by <see cref="EmitToken"/> for flags enums only; null for every other leaf.</summary>
    internal readonly record struct FlagBits(ulong Bits, Type EnumType);

    /// <summary>A modeled leaf that exists but holds no value on this record (absent optional substruct,
    /// empty optional). Distinct from a real token; the oracle skips it — write-proof owns the absent
    /// surface.</summary>
    internal const string AbsentNote = "(absent)";

    /// <summary>A present-but-null FormLink (FormKey.Null) — modeled, but carrying no target. Not a
    /// round-trippable token (the write surface sets links to a real FormKey, never "Null"), so surfaced as
    /// a note. Distinct from <see cref="AbsentNote"/> (a wholly absent optional); the conflict diff treats
    /// both as "no value here" (see <c>FieldsDiff.IsAbsentSentinel</c>).</summary>
    internal const string NullLinkNote = "(null link)";

    /// <summary>A present <c>TranslatedString</c> (FULL/DESC/…) whose <c>.String</c> resolves to null — a localized
    /// string whose <c>.STRINGS</c> entry for the target language is not in the workspace. After the
    /// <see cref="LoadOrderResolver.OpenOverlay"/> strings-source fix this is the genuinely-absent residue (no
    /// strings anywhere for it), NOT the cleaned-masters case that fix resolves. Surfaced as a no-value NOTE — never
    /// a blank token — so a value predicate's Q3 accounting fires on it (a `where Name contains …` can't silently
    /// treat it as a real non-matching value → false "0 matches") and a read renders it loud, not as an empty Name
    /// indistinguishable from a record that truly has none (HCBR-2026-06-24). Like <see cref="AbsentNote"/> /
    /// <see cref="NullLinkNote"/>, the conflict diff treats it as "no value here" (<c>FieldsDiff.IsAbsentSentinel</c>).</summary>
    internal const string UnresolvedStringNote = "(unresolved localized string)";

    // ======================================================================
    //  `read` MODE — resolve a record in one plugin and emit its fields.
    //    dotnet run --project src/housecarl-generator read \
    //        --source "<plugin>" [--type Weapon] (--formkey 0F1AC1:Skyrim.esm | --editorid X) \
    //        [--path BasicStats.Damage]...
    //  With one or more --path: emit exactly those leaves (the get_field primitive + the oracle's
    //  read). With no --path: a one-level dump of every modeled field on the record.
    // ======================================================================
    public static int RunRead(string[] args)
    {
        var f = WriteEngine.ParseFlags(args);
        var source = f.GetValueOrDefault("source");
        if (source is null) { Console.Error.WriteLine("error: --source is required"); return 1; }
        if (!File.Exists(source)) { Console.Error.WriteLine($"error: source plugin not found: {source}"); return 1; }
        var type = f.GetValueOrDefault("type");
        var editorid = f.GetValueOrDefault("editorid");
        var formkeyRaw = f.GetValueOrDefault("formkey");
        if (editorid is null && formkeyRaw is null) { Console.Error.WriteLine("error: locate the record with --editorid or --formkey"); return 1; }

        // --path repeats (ParseFlags keeps only the last of a repeated flag) — scan the raw args.
        var paths = new List<string>();
        for (int i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], "--path", StringComparison.OrdinalIgnoreCase)) paths.Add(args[i + 1]);

        var sourceMod = SkyrimMod.CreateFromBinaryOverlay(source, SkyrimRelease.SkyrimSE);
        Type? iface = type is null ? null : typeof(SkyrimMod).Assembly.GetType("Mutagen.Bethesda.Skyrim.I" + type + "Getter");
        if (type is not null && iface is null) { Console.Error.WriteLine($"error: unknown record type '{type}'"); return 1; }
        FormKey? wantFk = null;
        if (formkeyRaw is not null) { try { wantFk = FormKey.Factory(formkeyRaw); } catch (Exception ex) { Console.Error.WriteLine($"error: bad --formkey '{formkeyRaw}': {ex.Message}"); return 1; } }

        var target = sourceMod.EnumerateMajorRecords()
            .FirstOrDefault(r => (iface is null || iface.IsInstanceOfType(r))
                && (wantFk is { } fk ? r.FormKey == fk : string.Equals(r.EditorID, editorid, StringComparison.OrdinalIgnoreCase)));
        if (target is null) { Console.Error.WriteLine($"error: not found in {Path.GetFileName(source)}"); return 1; }

        var typeName = RecordNaming.StripGetterInterface(WriteEngine.PrimaryGetter(target.GetType())?.Name ?? "I?Getter");
        Console.WriteLine($"{typeName}  {target.FormKey}  ({target.EditorID ?? "<no editorid>"})");

        // --depth N (default 1): depth>=2 expands list/dict/substruct contents (descendable reads). With
        // --path it expands those targets; without, the whole-record dump. Routes through the SAME ReadFields
        // the MCP read tools call, so the harness and the product stay in lockstep.
        var depth = int.TryParse(f.GetValueOrDefault("depth"), out var dN) && dN > 0 ? dN : 1;
        var rf = ReadFields(target, paths.Count > 0 ? paths : null, depth);
        foreach (var fv in rf.Fields)
            Console.WriteLine($"  {fv.Path} = {(fv.HasValue ? fv.Token : fv.Note)}");
        return 0;
    }

    /// <summary>Read a located record's fields as round-trippable tokens — the public, structured entry the MCP
    /// server consumes (the §8.4 read cleave; <c>RunRead</c> is the CLI sibling). With <paramref name="paths"/>:
    /// exactly those leaves (the get_field primitive). Without: a one-level dump of every modeled field. Wraps the
    /// proven internal <see cref="ReadLeaf"/> (the round-trip oracle drives it), so the server's reads inherit the
    /// read-proof by construction. Per-leaf fault isolation (Q3): an unreadable field names itself in its
    /// <see cref="FieldValue.Note"/>, never throws out of the record read.</summary>
    public static RecordFields ReadFields(IMajorRecordGetter record, IReadOnlyList<string>? paths = null, int depth = 1)
    {
        var typeName = RecordNaming.StripGetterInterface(WriteEngine.PrimaryGetter(record.GetType())?.Name ?? "I?Getter");
        var targets = paths is { Count: > 0 } ? (IEnumerable<string>)paths : ModeledFieldNames(typeName, record.GetType());
        var fields = new List<FieldValue>();
        if (depth <= 1)
        {
            // depth 1 (default) — UNCHANGED one-level read: the proven get_field/dump path the round-trip
            // oracle drives (ReadLeaf). Descendable expansion (depth>=2) is an additive sibling below, so
            // the oracle-critical leaf path is never touched.
            foreach (var p in targets)
            {
                var seg = p.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var r = ReadLeaf(record, seg);
                fields.Add(new FieldValue(p, r.HasValue, r.HasValue ? r.Token : null, r.HasValue ? null : r.Note));
            }
        }
        else
        {
            int budget = MaxExpandNodes;
            foreach (var p in targets) EmitWithDepth(record, p, depth, fields, ref budget);
        }
        return new RecordFields(typeName, record.FormKey.ToString(), record.EditorID, fields);
    }

    // ======================================================================
    //  THE READ PRIMITIVE — navigate a path read-only, emit the leaf token.
    // ======================================================================

    /// <summary>Read one leaf path off a located record and return its round-trippable token (or a
    /// sentinel). Navigation mirrors the write engine's path walk (same <c>ResolveProperty</c> /
    /// <c>StepIntoElement</c>) but is READ-ONLY — an absent optional substruct is surfaced, never
    /// materialised. Per-leaf fault isolation (Q3): any refl/parse failure names itself and never
    /// throws out, so one Mutagen-unparseable field can't crash a record read.</summary>
    internal static LeafRead ReadLeaf(object record, string[] path)
    {
        try
        {
            object? current = record;
            for (int i = 0; i < path.Length - 1; i++)
            {
                var (segName, segKey) = WriteEngine.ParseSegment(path[i]);
                var p = WriteEngine.ResolveProperty(current!.GetType(), segName);
                if (p is null) return LeafRead.None(NoFieldNote(current, segName, i > 0 ? WriteEngine.ParseSegment(path[i - 1]).name : null));
                current = segKey is null
                    ? p.GetValue(current)                                  // descend a substruct (read-only)
                    : WriteEngine.StepIntoElement(current, p, segName, segKey); // collnav (handles IReadOnly*)
                if (current is null) return LeafRead.None(AbsentNote);     // absent optional substruct
            }

            var (leafName, leafKey) = WriteEngine.ParseSegment(path[^1]);
            var leaf = WriteEngine.ResolveProperty(current!.GetType(), leafName);
            if (leaf is null) return LeafRead.None(NoFieldNote(current, leafName, path.Length >= 2 ? WriteEngine.ParseSegment(path[^2]).name : null));
            if (leafKey is not null)
            {
                // The leaf brackets a collection element (Keywords[0]) — step in and emit the element.
                var elem = WriteEngine.StepIntoElement(current, leaf, leafName, leafKey);
                return EmitToken(elem, elem.GetType(), current);
            }
            return EmitToken(leaf.GetValue(current), leaf.PropertyType, current);
        }
        catch (Exception ex) { return LeafRead.None($"(unreadable: {ex.Message})"); }
    }

    /// <summary>A "no such field" note that, when the owner is a collection, points the caller at bracket
    /// indexing — the common <c>.0</c>-vs-<c>[0]</c> confusion (the read analog of the write pre-flight's
    /// bracket hint in <c>CorpusRulebook</c>). Brackets are how you step into a list/dict element mid-path;
    /// a bare dotted <c>.0</c> is parsed as a field name and dead-ends here.</summary>
    static string NoFieldNote(object owner, string segName, string? precedingField)
    {
        bool ownerIsCollection = owner is System.Collections.IDictionary
            || (owner is System.Collections.IEnumerable && owner is not string);
        if (ownerIsCollection)
        {
            var pf = precedingField ?? "<field>";
            return $"(no field '{segName}': '{pf}' is a list/dict — index an element with brackets, " +
                   $"e.g. '{pf}[{segName}]', not '{pf}.{segName}')";
        }
        return $"(no field {segName})";
    }

    // ======================================================================
    //  DESCENDABLE READS (depth>=2) — enumerate list/dict/substruct CONTENTS so element indices and
    //  sub-fields are discoverable without hand-probing each [i]. Additive: navigation reuses the same
    //  engine walk (ParseSegment/ResolveProperty/StepIntoElement) and EmitToken as the leaf path, but the
    //  proven depth-1 ReadLeaf is untouched. Bounded by MaxExpandNodes (Q3 — explicit truncation note).
    // ======================================================================

    /// <summary>Max FieldValue lines one descendable read will GENERATE (separate from the renderer's char
    /// cap) — a guard so depth-expanding a huge container can't build a runaway result. Over it, a single
    /// truncation note is emitted (Q3 — bounded, never silent).</summary>
    internal const int MaxExpandNodes = 2000;

    static readonly string[] IdentityFieldNames = { "Name", "EditorID", "Title" };

    /// <summary>Emit one target path, expanding container/substruct contents up to <paramref name="depth"/>
    /// levels. A miss surfaces the same bracket-aware note the leaf read uses. The body is wrapped in the same
    /// per-field fault isolation depth-1 <see cref="ReadLeaf"/> gives (Q3): a throw while navigating OR expanding
    /// this one target (an unparseable nested getter, an enumerator that faults mid-list, an ambiguous identity
    /// reflection) names itself "(unreadable …)" and never escapes the record read — so one bad field can't crash
    /// a whole-record depth dump. Lines already emitted before a mid-expansion fault are real reads and are kept.</summary>
    static void EmitWithDepth(object record, string path, int depth, List<FieldValue> sink, ref int budget)
    {
        try
        {
            var seg = path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var nav = NavigateValue(record, seg);
            if (!nav.ok) { Emit(sink, ref budget, new FieldValue(path, false, null, nav.note)); return; }
            Expand(nav.val, nav.type, nav.parent, path, depth, sink, ref budget);
        }
        catch (Exception ex) { Emit(sink, ref budget, new FieldValue(path, false, null, $"(unreadable: {ex.Message})")); }
    }

    /// <summary>Recursively emit <paramref name="val"/> at <paramref name="path"/>: a value leaf → its token;
    /// a link → its note (not opened); a container/substruct → an identity-enriched summary line, then (while
    /// depth allows) one child line per element (bracketed) or sub-field (dotted), each recursed at depth-1.</summary>
    static void Expand(object? val, Type declaredType, object parent, string path, int depth, List<FieldValue> sink, ref int budget)
    {
        if (budget < 0) return;
        var leaf = EmitToken(val, declaredType, parent);
        if (leaf.HasValue) { Emit(sink, ref budget, new FieldValue(path, true, leaf.Token, null)); return; }
        if (val is null) { Emit(sink, ref budget, new FieldValue(path, false, null, leaf.Note)); return; }
        // a link (incl. a null FormKey, or an FLOI) is a note, not an openable container/substruct.
        if (val is IFormLinkGetter || WriteEngine.IsFormLinkOrIndex(Nullable.GetUnderlyingType(declaredType) ?? declaredType))
        { Emit(sink, ref budget, new FieldValue(path, false, null, leaf.Note)); return; }

        // Classify dict-vs-list the SAME way the navigation does (StepIntoElement) — by the GENERIC dictionary
        // interfaces via ClosedInterface, not a separate non-generic System.Collections.IDictionary cast — so the
        // browse view and the read/write path can't drift (a getter dict need not expose the non-generic interface).
        // A generic dict enumerates as KeyValuePair<,>; Key/Value come off each pair. Classified BEFORE the
        // summary line so the summary can carry the dict marker ("pair(s)" vs "item(s)") — the in-band signal
        // FieldsDiff uses to keep numeric-KEYED dicts (Package.Data) out of positional-list comparison, where
        // a key rebinding would wrongly compare "identical" (PR #28 review).
        bool isDict = WriteEngine.ClosedInterface(val.GetType(), typeof(IDictionary<,>)) is not null
                   || WriteEngine.ClosedInterface(val.GetType(), typeof(IReadOnlyDictionary<,>)) is not null;

        // a container or substruct — summarise (with an element identity where we can), then maybe open it.
        if (!Emit(sink, ref budget, new FieldValue(path, false, null, ElementSummary(val, isDict)))) return;

        // A VMAD script property normally stops here at its identity summary (e.g. "[ScriptObjectProperty]
        // Name=DAK_HorseBuyPerk"), hiding its VALUE. Surface that value ONE bounded level deeper even at the
        // depth floor — the Object FormLink (incl. a declared-but-null link, the signal the quest-fragment
        // linter keys on), the Data scalar, the Alias — so a read reaches parity with the write surface. A
        // property's direct members are leaves (a *ListProperty arm shows as a count at the floor; raise
        // depth= to enumerate it), so this opens exactly one level and never unbounded-descends a fat VMAD
        // (1.3.1 item 2). EVERY OTHER substruct still stops at the floor, byte-for-byte unchanged.
        int childDepth = depth - 1;
        if (depth <= 1)
        {
            if (!IsScriptProperty(val.GetType())) return;
            childDepth = 1;
        }

        if (isDict)
        {
            foreach (var entry in (System.Collections.IEnumerable)val)
            {
                if (budget < 0) return;
                if (entry is null) continue;
                var et = entry.GetType();
                var key = et.GetProperty("Key", BindingFlags.Public | BindingFlags.Instance)?.GetValue(entry);
                var ev = et.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance)?.GetValue(entry);
                Expand(ev, ev?.GetType() ?? typeof(object), val, $"{path}[{key}]", childDepth, sink, ref budget);
            }
        }
        else if (WriteEngine.GenderedInterface(val.GetType()) is not null)
        {
            // Gendered pair ([0]=male, [1]=female): render via the SAME index→arm mapping navigation uses
            // (WriteEngine.GenderedArmNames), NOT raw enumeration order — so the [0]/[1] paths a depth-read SHOWS are
            // exactly the ones a write/read ACCEPTS, by construction (no drift if Mutagen's enumerator order shifts).
            // Numeric [0]/[1] keeps this root a positional list to FieldsDiff (gendered halves never reorder), so the
            // conflict-diff comparison is unchanged (the HCBR PR-H render decision: keep numeric indices).
            for (int g = 0; g < WriteEngine.GenderedArmNames.Length; g++)
            {
                if (budget < 0) return;
                var armProp = WriteEngine.ResolveProperty(val.GetType(), WriteEngine.GenderedArmNames[g]);
                object? arm; try { arm = armProp?.GetValue(val); } catch { continue; }
                Expand(arm, armProp?.PropertyType ?? typeof(object), val, $"{path}[{g}]", childDepth, sink, ref budget);
            }
        }
        else if (val is System.Collections.IEnumerable seq and not string)
        {
            int i = 0;
            foreach (var item in seq)
            {
                if (budget < 0) return;
                Expand(item, item?.GetType() ?? typeof(object), val, $"{path}[{i}]", childDepth, sink, ref budget);
                i++;
            }
        }
        else
        {
            // substruct — open its modeled (Loqui-filtered) fields. Reflection, not the corpus: display-only,
            // and substructs aren't corpus-keyed by a name we hold here.
            //
            // GATE — the expansion boundary is the modeled corpus (cornerstone). Only DESCEND into Mutagen/
            // Noggog record content; a value that reaches here but is NOT modeled is .NET plumbing — in
            // practice a System.Type (RuntimeType): a ConditionData arm's Parameter1Type/Parameter2Type are
            // typed System.Type, and a naive recurse walks .Assembly.DefinedTypes (the whole ~17,900-type
            // Mutagen assembly), the BaseType→Enum→ValueType chain, StructLayoutAttribute, Module, the cyclic
            // UnderlyingSystemType — ~50 KB of reflection internals that is never record data (HCBR-2026-06-08-01).
            // The summary token was already emitted above (e.g. `Parameter1Type = [RuntimeType] Name=ActorValue`,
            // exactly the clean depth<=4 rendering); we keep it and STOP, regardless of remaining depth budget.
            if (!IsModeledContent(val.GetType())) return;
            foreach (var fname in ReflectedFieldNames(val.GetType()))
            {
                if (budget < 0) return;
                var prop = WriteEngine.ResolveProperty(val.GetType(), fname);
                object? fv; try { fv = prop?.GetValue(val); } catch { continue; }
                Expand(fv, prop?.PropertyType ?? typeof(object), val, $"{path}.{fname}", childDepth, sink, ref budget);
            }
        }
    }

    /// <summary>Append a line, decrementing the generation budget; at exhaustion emit ONE truncation note and
    /// stop (returns false thereafter so callers unwind). Q3: the cut is named, never silent.</summary>
    static bool Emit(List<FieldValue> sink, ref int budget, FieldValue fv)
    {
        if (budget < 0) return false;
        if (budget == 0)
        {
            sink.Add(new FieldValue("…", false, null,
                $"(expansion truncated at {MaxExpandNodes} lines — narrow with a field path or a lower depth)"));
            budget = -1;
            return false;
        }
        sink.Add(fv); budget--; return true;
    }

    /// <summary>Navigate a path READ-ONLY to its target, returning the live value object (+ declared type +
    /// owning parent) for recursion, or a miss note. Same walk as <see cref="ReadLeaf"/> but yields the object
    /// instead of a token, so the expander can descend into it. Fault-isolated (Q3).</summary>
    static (bool ok, object? val, Type type, object parent, string? note) NavigateValue(object record, string[] path)
    {
        try
        {
            object current = record;
            for (int i = 0; i < path.Length - 1; i++)
            {
                var (segName, segKey) = WriteEngine.ParseSegment(path[i]);
                var p = WriteEngine.ResolveProperty(current.GetType(), segName);
                if (p is null) return (false, null, typeof(object), current,
                    NoFieldNote(current, segName, i > 0 ? WriteEngine.ParseSegment(path[i - 1]).name : null));
                var next = segKey is null ? p.GetValue(current) : WriteEngine.StepIntoElement(current, p, segName, segKey);
                if (next is null) return (false, null, typeof(object), record, AbsentNote);
                current = next;
            }
            var (leafName, leafKey) = WriteEngine.ParseSegment(path[^1]);
            var leaf = WriteEngine.ResolveProperty(current.GetType(), leafName);
            if (leaf is null) return (false, null, typeof(object), current,
                NoFieldNote(current, leafName, path.Length >= 2 ? WriteEngine.ParseSegment(path[^2]).name : null));
            if (leafKey is not null)
            {
                var elem = WriteEngine.StepIntoElement(current, leaf, leafName, leafKey);
                return (true, elem, elem.GetType(), current, null);
            }
            return (true, leaf.GetValue(current), leaf.PropertyType, current, null);
        }
        catch (Exception ex) { return (false, null, typeof(object), record, $"(unreadable: {ex.Message})"); }
    }

    /// <summary>Best-effort COMPACT identity of the element a list/dict verb just acted on — the write-verify's
    /// "what landed" line (HCBR-2026-06-28-01, the compact in-place readback). For a list <c>Add</c>, the new last
    /// element + the new count (<c>now 29 (+1), new [28] = …</c>); for a keyed <c>SetAtIndex</c>/<c>Remove</c>, the
    /// touched key + new count; else the new count. Names the element as specifically as the model allows — a
    /// formlink element renders its FormKey, an identity-bearing struct its Name/EditorID, an anonymous struct (a
    /// condition) its <c>[Type]</c>. Read-only; NEVER throws (null on any difficulty) — a display nicety on an
    /// ALREADY-succeeded write, never load-bearing. <paramref name="leafPath"/> is the verb's path to the collection
    /// (the engine's <see cref="WriteRequest.Path"/>); <paramref name="key"/> its list index / dict key, if any.</summary>
    internal static string? TouchedElement(object record, string[] leafPath, string verb, string? key)
    {
        try
        {
            var nav = NavigateValue(record, leafPath);
            if (!nav.ok || nav.val is not System.Collections.IEnumerable en || nav.val is string) return null;
            int count = 0; object? last = null;
            foreach (var e in en) { count++; last = e; }
            return verb switch
            {
                "Add"        => last is null ? $"now {count} item(s)" : $"now {count} (+1), new [{count - 1}] = {ElementId(last, record)}",
                "ReplaceAll" => $"now {count} item(s) (replaced)",
                "SetAtIndex" => key is not null ? $"now {count} item(s), set [{key}]" : $"now {count} item(s)",
                "Remove"     => key is not null ? $"now {count} item(s), removed [{key}]" : $"now {count} item(s) (-1)",
                _            => $"now {count} item(s)",
            };
        }
        catch { return null; }
    }

    /// <summary>The compact identity of ONE element for <see cref="TouchedElement"/>: a value/formlink element via its
    /// own round-trip token (a keyword → its FormKey), an identity-bearing or anonymous struct via
    /// <see cref="ElementSummary"/> (<c>[Type] Name=…</c> / <c>[Type]</c>).</summary>
    static string ElementId(object elem, object parent)
    {
        var lr = EmitToken(elem, elem.GetType(), parent);
        return lr.HasValue ? lr.Token : ElementSummary(elem);
    }

    /// <summary>A compact summary for a container/struct value: for a collection, the count form
    /// (<see cref="SummariseContainer"/>); for a struct, <c>[TypeName]</c> plus a representative identity
    /// field (Name/EditorID/Title) where present — so a list line like
    /// <c>Properties[5] = [ScriptObjectProperty] Name=DAK_HorseBuyPerk</c> reveals which element is which.</summary>
    static string ElementSummary(object val, bool isDict = false)
    {
        if (val is System.Collections.IEnumerable && val is not string) return SummariseContainer(val, isDict);
        var t = val.GetType();
        var typeName = RecordNaming.StripGetterInterface(RecordNaming.StripOverlay(t.Name));
        foreach (var idName in IdentityFieldNames)
        {
            var p = t.GetProperty(idName, BindingFlags.Public | BindingFlags.Instance);
            if (p is null || p.GetIndexParameters().Length != 0) continue;
            object? iv; try { iv = p.GetValue(val); } catch { continue; }
            var s = iv switch { null => null, string str => str, IFormLinkGetter fl => fl.FormKey.ToString(), _ => iv.ToString() };
            if (!string.IsNullOrEmpty(s)) return $"[{typeName}] {idName}={s}";
        }
        return $"[{typeName}]";
    }

    /// <summary>Public-instance modeled field names off a runtime type (Loqui infra filtered) — the reflection
    /// sibling of <see cref="ModeledFieldNames"/> for substructs (which aren't corpus-keyed by a name we hold
    /// here). Best-effort, display-only.</summary>
    static IEnumerable<string> ReflectedFieldNames(Type runtimeType)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var primary = WriteEngine.PrimaryGetter(runtimeType);
        var ifaces = new List<Type>();
        if (primary is not null) { ifaces.Add(primary); ifaces.AddRange(primary.GetInterfaces()); }
        else { ifaces.Add(runtimeType); ifaces.AddRange(runtimeType.GetInterfaces()); }
        foreach (var iface in ifaces)
            foreach (var p in iface.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                if (p.GetIndexParameters().Length == 0
                    && p.DeclaringType?.Namespace?.StartsWith("Loqui", StringComparison.Ordinal) != true
                    && !IsInfrastructure(p)
                    && seen.Add(p.Name))
                    yield return p.Name;
    }

    /// <summary>Drop Loqui/Mutagen plumbing members that aren't real data fields — the translation/registration
    /// properties the corpus omits but raw reflection surfaces (e.g. a substruct's <c>BinaryWriteTranslator</c>).
    /// Keeps reflective substruct expansion as clean as the corpus-driven whole-record dump.</summary>
    static bool IsInfrastructure(PropertyInfo p)
    {
        if (p.Name is "BinaryWriteTranslator" or "Registration" or "StaticRegistration"
            or "CommonInstance" or "CommonSetterInstance" or "CommonSetterTranslationInstance") return true;
        var tn = p.PropertyType.Name;
        return tn.Contains("BinaryWriteTranslat", StringComparison.Ordinal)
            || tn.EndsWith("BinaryTranslation", StringComparison.Ordinal);
    }

    /// <summary>True if <paramref name="t"/> is MODELED record content the depth walker may descend into — a
    /// Mutagen or Noggog type. Everything else that reaches the substruct branch is .NET plumbing: in practice a
    /// <see cref="System.Type"/>/RuntimeType (a ConditionData arm's <c>Parameter1Type</c>/<c>Parameter2Type</c>),
    /// or an <see cref="Assembly"/>/<see cref="System.Reflection.Module"/>/<see cref="MemberInfo"/>/<see
    /// cref="Attribute"/> reached through one. Those carry no record data — descending them leaks the assembly's
    /// whole type metadata — so the expander renders only their one-line summary token and stops. The modeled
    /// corpus IS the boundary (cornerstone): a type outside Mutagen's own universe is not ours to expand.
    /// Reflection objects are also blocked explicitly so the intent reads clearly and stays robust if a non-
    /// modeled support namespace ever appears.</summary>
    static bool IsModeledContent(Type t)
    {
        if (typeof(MemberInfo).IsAssignableFrom(t)            // Type : MemberInfo — covers Type/RuntimeType, MethodInfo, …
            || typeof(Assembly).IsAssignableFrom(t)
            || typeof(System.Reflection.Module).IsAssignableFrom(t)
            || typeof(Attribute).IsAssignableFrom(t)) return false;
        var ns = t.Namespace;
        return ns is not null
            && (ns.StartsWith("Mutagen.Bethesda", StringComparison.Ordinal)
                || ns.StartsWith("Noggog", StringComparison.Ordinal));
    }

    // ======================================================================
    //  EMIT — the inverse of WriteEngine.Coerce. The branch order mirrors Coerce's Try* family order
    //  (primitive → enum → formlink → value-type), so the two surfaces cannot drift on which token a
    //  type round-trips through. FLOI is checked first (parent-aware, NOT coercible) exactly as the
    //  write side special-cases it.
    // ======================================================================

    /// <param name="parent">The leaf's owning object — needed only for a FormLinkOrIndex (condition
    /// target), whose form-vs-index mode is carried by the parent arm's discriminator bools.</param>
    internal static LeafRead EmitToken(object? val, Type declaredType, object parent)
    {
        if (val is null) return LeafRead.None(AbsentNote);
        var u = Nullable.GetUnderlyingType(declaredType) ?? declaredType;

        // FLOI (wave-4 condition targets) — emit a token ClassifyFloiValue re-accepts (FormKey, or
        // "alias N" / "packdata N"), inferred from the parent arm's UseAliases/UsePackageData.
        if (WriteEngine.IsFormLinkOrIndex(u)) return EmitFloi(val, parent);

        // primitive (inverse of TryPrimitive)
        if (TryEmitPrimitive(val, out var prim)) return LeafRead.Value(prim);
        // enum (inverse of TryEnum) — ToString gives the name(s); Enum.Parse(ignoreCase) re-accepts,
        // including the comma form for a [Flags] combination. For a [Flags] enum we ALSO carry the underlying
        // bit pattern + type (the display token is unchanged) so the query predicate can bit-test (`has`) and
        // compare numerically without tripping over the name-vs-number rendering split (HCBR 2026-06-24).
        if (u.IsEnum || val.GetType().IsEnum)
        {
            var token = val.ToString() ?? "";
            var enumType = u.IsEnum ? u : val.GetType();
            if (enumType.IsDefined(typeof(FlagsAttribute), false) && TryEnumBits(val, enumType, out var bits))
                return LeafRead.FlagsValue(token, new FlagBits(bits, enumType));
            return LeafRead.Value(token);
        }
        // formlink (inverse of TryFormLink) — FormKey.ToString() ↔ FormKey.Factory. A present-but-null
        // link (FormKey.Null) is NOT a round-trippable token (the write surface sets links to a real
        // FormKey, never "Null"), so surface it as no-value — consistent with what Coerce accepts.
        if (val is IFormLinkGetter fl)
            return fl.FormKey.IsNull ? LeafRead.None(NullLinkNote) : LeafRead.Value(fl.FormKey.ToString());
        // TranslatedString (FULL/DESC) — emit the resolved .String (the inverse of Coerce's implicit
        // `record.Name = "x"`). A genuinely-empty "" still round-trips as a value; a NULL .String is an
        // UNRESOLVED localized string (no .STRINGS entry for the target language in the workspace) and is
        // surfaced LOUD as no-value, never a blank token — so the Q3 accounting fires instead of a silent
        // non-match (HCBR-2026-06-24). Checked before TryEmitValueType, which previously folded the null
        // into "" here.
        if (val.GetType().FullName == "Mutagen.Bethesda.Strings.TranslatedString")
        {
            var s = ReflectString(val, "String");
            return s is null ? LeafRead.None(UnresolvedStringNote) : LeafRead.Value(s);
        }
        // value types (inverse of TryValueType)
        if (TryEmitValueType(val, out var vt)) return LeafRead.Value(vt);

        // Not a single-token VALUE leaf: a substruct / collection / arm container. The oracle never
        // drives these AS leaves — their sub-leaves are driven individually (exactly like write-proof).
        // Summarise for the read display, with the same dict-vs-list marker the depth walk renders.
        return LeafRead.None(SummariseContainer(val,
            WriteEngine.ClosedInterface(val.GetType(), typeof(IDictionary<,>)) is not null
            || WriteEngine.ClosedInterface(val.GetType(), typeof(IReadOnlyDictionary<,>)) is not null));
    }

    /// <summary>The unsigned bit pattern of a boxed enum value, robust across every underlying integer type
    /// (signed or unsigned) — the bits a <c>has</c> predicate ANDs against. Read through the declared underlying
    /// type so a high-bit-set signed enum yields its two's-complement pattern rather than overflowing.</summary>
    static bool TryEnumBits(object val, Type enumType, out ulong bits)
    {
        bits = 0;
        try
        {
            var prim = Convert.ChangeType(val, Enum.GetUnderlyingType(enumType), CultureInfo.InvariantCulture);
            bits = prim switch
            {
                ulong ul => ul,
                long l => unchecked((ulong)l),
                uint ui => ui,
                int i => unchecked((ulong)(long)i),
                ushort us => us,
                short s => unchecked((ulong)(long)s),
                byte b => b,
                sbyte sb => unchecked((ulong)(long)sb),
                _ => Convert.ToUInt64(prim, CultureInfo.InvariantCulture),
            };
            return true;
        }
        catch { return false; }
    }

    /// <summary>Resolve a flag NAME (or a comma-combo, case-insensitive) against a <c>[Flags]</c> enum type to its
    /// bit pattern — the name-operand path for the query predicate's <c>has</c>/<c>=</c> (e.g. <c>has Body</c>).
    /// False if the text is not a member (or combo of members) of the enum. A numeric string also parses here, but
    /// the caller resolves numerics first, so this only ever sees names.</summary>
    internal static bool TryEnumBitsFromName(Type enumType, string name, out ulong bits)
    {
        bits = 0;
        try { return TryEnumBits(Enum.Parse(enumType, name.Trim(), ignoreCase: true), enumType, out bits); }
        catch { return false; }
    }

    // -- primitive family (mirror TryPrimitive) --------------------------------
    static bool TryEmitPrimitive(object val, out string token)
    {
        switch (val)
        {
            case string s: token = s; return true;
            case bool b: token = b ? "True" : "False"; return true;
            // round-trippable "R" so float.Parse/double.Parse reproduce the exact IEEE bits.
            case float fl: token = fl.ToString("R", CultureInfo.InvariantCulture); return true;
            case double d: token = d.ToString("R", CultureInfo.InvariantCulture); return true;
            case byte or sbyte or short or ushort or int or uint or long or ulong:
                token = Convert.ToString(val, CultureInfo.InvariantCulture)!; return true;
        }
        token = "";
        return false;
    }

    // -- value-type family (mirror TryValueType) -------------------------------
    static bool TryEmitValueType(object val, out string token)
    {
        token = "";
        switch (val)
        {
            case System.Drawing.Color c: token = $"{c.R},{c.G},{c.B},{c.A}"; return true;       // "R,G,B,A"
            case DateTime dt: token = dt.ToString("O", CultureInfo.InvariantCulture); return true;
            case TimeOnly t: token = t.ToString("O", CultureInfo.InvariantCulture); return true;
            case char ch: token = ch.ToString(); return true;
            case string[] arr: token = string.Join(",", arr); return true;
            case FormKey fk: token = fk.ToString(); return true;
            case ModKey mk: token = mk.ToString(); return true;
            case RecordType rt: token = rt.ToString(); return true;
        }

        var rt2 = val.GetType();
        var fn = rt2.FullName;

        // (TranslatedString is handled earlier in EmitToken — a null .String surfaces as a loud no-value note,
        //  not the blank token this branch used to fold it into; see UnresolvedStringNote.)

        // Noggog.Percent — emit the [0..1] fraction its single-arg ctor takes. Find the underlying
        // numeric member BY TYPE, not a guessed name: Percent stores exactly one double (its ToString
        // is the "33%" display form, which Coerce can't parse — the round-trip oracle caught that).
        if (fn == "Noggog.Percent")
        { token = NumericComponentInvariant(val) ?? val.ToString() ?? ""; return true; }

        // Noggog point structs P2*/P3* — components in constructor order ("x,y,z").
        if (rt2.Namespace == "Noggog" && (rt2.Name.StartsWith("P2") || rt2.Name.StartsWith("P3")))
        { token = PointComponents(val); return true; }

        // (ReadOnly)MemorySlice<byte> — raw blob as a hex string.
        if (IsByteMemorySlice(rt2)) { token = Convert.ToHexString(MemorySliceBytes(val)); return true; }

        // AssetLink<T> family — the stored path string. Recognised by generic-definition NAME (the mutable
        // AssetLink<T> Coerce builds, the getter overlay's AssetLinkGetter<T>, or the IAssetLink(Getter)<T>
        // interfaces) so the value READ off a getter is handled, not only the mutable type — the same
        // by-name recognition the engine uses for the FLOI family. Shares ONE predicate with the write
        // coercion (WriteEngine.IsAssetLinkFamily) so read and write can't drift on what an asset link is.
        if (WriteEngine.IsAssetLinkFamily(rt2))
        { token = ReflectString(val, "GivenPath", "RawPath", "DataRelativePath") ?? val.ToString() ?? ""; return true; }

        return false;
    }

    // -- FLOI (mirror SetFloi / ClassifyFloiValue) -----------------------------
    /// <summary>Emit a condition-target FormLinkOrIndex as the token that re-creates it: a FormKey in
    /// form mode, else "alias N" / "packdata N" per the owning arm's discriminator. The index payload
    /// accessor is read defensively; if the mode or index can't be read cleanly the leaf is surfaced as
    /// a note (never a guessed four bytes — Q3). The precise index round-trip is exercised by the
    /// oracle's FLOI phase.</summary>
    static LeafRead EmitFloi(object val, object parent)
    {
        bool? useAliases = ReflectBool(parent, "UseAliases");
        bool? usePackData = ReflectBool(parent, "UsePackageData");
        if (useAliases is null || usePackData is null)
            return LeafRead.None($"(floi: parent {parent.GetType().Name} has no UseAliases/UsePackageData discriminator)");

        if (useAliases == false && usePackData == false)
        {
            // Form mode. The binary overlay's FLOI is NOT itself a link — it carries the link in its
            // .Link property, the same accessor the write side reads (ReadFloiFormKey, oracle-proven).
            // A present-but-null link stays a note, matching plain FormLink leaves (HCBR-2026-06-09-02).
            if (val is IFormLinkGetter fl) return LeafRead.Value(fl.FormKey.ToString());
            if (WriteEngine.ReadFloiFormKey(val) is { } fk) return LeafRead.Value(fk.ToString());
            return LeafRead.None($"(floi: form mode, null or unreadable FormKey on {val.GetType().Name})");
        }

        // index mode — read the numeric index defensively (FormLinkOrIndex carries it alongside the link).
        var idx = ReflectUInt(val, "Index", "RawIndex", "FormKeyOrIndex");
        if (idx is null) return LeafRead.None("(floi: index mode, index accessor unresolved — refined in oracle)");
        return LeafRead.Value(useAliases == true ? $"alias {idx}" : $"packdata {idx}");
    }

    // ======================================================================
    //  Reflection helpers — read an accessor by candidate names, defensively.
    //  The round-trip oracle validates which accessor is the faithful inverse;
    //  a wrong guess fails the proof loud + precise rather than silently.
    // ======================================================================

    static string? ReflectString(object obj, params string[] names)
    {
        foreach (var n in names)
        {
            var p = obj.GetType().GetProperty(n, BindingFlags.Public | BindingFlags.Instance);
            if (p?.GetValue(obj) is string s) return s;
        }
        return null;
    }

    static bool? ReflectBool(object obj, string name)
        => WriteEngine.ResolveProperty(obj.GetType(), name)?.GetValue(obj) as bool?;

    static uint? ReflectUInt(object obj, params string[] names)
    {
        foreach (var n in names)
        {
            var v = obj.GetType().GetProperty(n, BindingFlags.Public | BindingFlags.Instance)?.GetValue(obj);
            if (v is uint u) return u;
            if (v is int i && i >= 0) return (uint)i;
        }
        return null;
    }

    /// <summary>Emit a single-component numeric value object's underlying number, found BY TYPE (the
    /// first public double/float property, else field) — robust to the member's name. Used for
    /// Noggog.Percent, whose ToString is a display form Coerce can't re-parse.</summary>
    static string? NumericComponentInvariant(object val)
    {
        var t = val.GetType();
        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            if (p.GetIndexParameters().Length == 0 && (p.PropertyType == typeof(double) || p.PropertyType == typeof(float)))
                return Convert.ToDouble(p.GetValue(val), CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture);
        foreach (var fld in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
            if (fld.FieldType == typeof(double) || fld.FieldType == typeof(float))
                return Convert.ToDouble(fld.GetValue(val), CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture);
        return null;
    }

    /// <summary>Emit a Noggog P2*/P3* point's components in CONSTRUCTOR-PARAMETER order ("x,y,z"),
    /// reading each via the matching public property — so the token splits back into the same ctor
    /// args TryValueType's ConstructByCtor consumes.</summary>
    static string PointComponents(object val)
    {
        var t = val.GetType();
        var ctor = t.GetConstructors().OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
        var parms = ctor?.GetParameters();
        if (parms is null || parms.Length == 0) return val.ToString() ?? "";
        var parts = new List<string>(parms.Length);
        foreach (var pp in parms)
        {
            var prop = t.GetProperty(pp.Name!, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            var comp = prop?.GetValue(val);
            parts.Add(comp switch
            {
                float f => f.ToString("R", CultureInfo.InvariantCulture),
                double d => d.ToString("R", CultureInfo.InvariantCulture),
                null => "0",
                _ => Convert.ToString(comp, CultureInfo.InvariantCulture) ?? "0",
            });
        }
        return string.Join(",", parts);
    }

    static bool IsByteMemorySlice(Type t)
        => t.IsGenericType
           && (t.GetGenericTypeDefinition() == typeof(Noggog.MemorySlice<>) || t.GetGenericTypeDefinition() == typeof(Noggog.ReadOnlyMemorySlice<>))
           && t.GetGenericArguments()[0] == typeof(byte);

    // AssetLink-family recognition lives in WriteEngine.IsAssetLinkFamily (ONE predicate, shared with write
    // coercion — read emits the path, write builds the link from it; they must agree on the family).

    /// <summary>True if <paramref name="t"/> is a VMAD script-property arm (ScriptObjectProperty, the scalar
    /// ScriptInt/Float/Bool/StringProperty arms, and the *ListProperty arms) — recognised by the shared getter
    /// interface, so every arm matches by construction with no per-arm list, on the overlay getter or the
    /// mutable type alike. The depth walker opens such a property's direct value members one bounded level past
    /// the depth floor (1.3.1 item 2 — read parity with the write surface); every other substruct stops at the
    /// floor, so this is the one type-targeted exception to the depth gate.</summary>
    static bool IsScriptProperty(Type t) => typeof(IScriptPropertyGetter).IsAssignableFrom(t);

    static byte[] MemorySliceBytes(object slice)
    {
        // Noggog slices expose ToArray() (and a Length + indexer fallback). Reflection-robust either way.
        var toArray = slice.GetType().GetMethod("ToArray", Type.EmptyTypes);
        if (toArray?.Invoke(slice, null) is byte[] arr) return arr;
        var lenProp = slice.GetType().GetProperty("Length", BindingFlags.Public | BindingFlags.Instance);
        var idxer = slice.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(p => p.GetIndexParameters().Length == 1 && p.PropertyType == typeof(byte));
        if (lenProp?.GetValue(slice) is int len && idxer is not null)
        {
            var bytes = new byte[len];
            for (int i = 0; i < len; i++) bytes[i] = (byte)idxer.GetValue(slice, new object[] { i })!;
            return bytes;
        }
        throw new InvalidOperationException($"Cannot extract bytes from MemorySlice {slice.GetType().Name}.");
    }

    /// <summary>A short, non-round-trippable description of a container leaf (substruct / list / dict /
    /// arm) for the read display. Its sub-leaves are the round-trippable surface. A dict renders
    /// "N pair(s)" (vs a list's "N item(s)") — display-informative, and the in-band marker FieldsDiff
    /// uses to keep numeric-keyed dicts out of positional-list comparison (PR #28 review).</summary>
    static string SummariseContainer(object val, bool isDict = false)
    {
        if (val is System.Collections.IEnumerable en and not string)
        {
            int n = 0;
            foreach (var _ in en) n++;
            return $"[{RecordNaming.StripGetterInterface(val.GetType().Name)}: {n} {(isDict ? "pair(s)" : "item(s)")}]";
        }
        return $"[{RecordNaming.StripGetterInterface(val.GetType().Name)}]";
    }

    /// <summary>The modeled field names for the whole-record dump. Prefer the CORPUS — the authoritative
    /// by-construction modeled-field set (exactly what the read-proof drives, infra-free) — and fall back
    /// to the record's getter interfaces (Loqui-namespace filtered) only when the corpus isn't built.</summary>
    static IEnumerable<string> ModeledFieldNames(string typeName, Type recordRuntimeType)
    {
        Corpus? corpus = null;
        try { corpus = CorpusRulebook.LoadCorpus(); } catch { /* corpus not built / unparseable → reflection fallback */ }
        if (corpus is not null && corpus.Types.TryGetValue(typeName, out var schema))
        {
            foreach (var f in schema.Fields) yield return f.Name;
            yield break;
        }

        // Fallback (no corpus): the record's getter interfaces, with the Loqui infrastructure filter
        // (drops plumbing like Registration / BinaryWriteTranslator / Type declared on Loqui base types).
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var primary = WriteEngine.PrimaryGetter(recordRuntimeType);
        if (primary is null) yield break;
        var ifaces = new List<Type> { primary };
        ifaces.AddRange(primary.GetInterfaces());
        foreach (var iface in ifaces)
            foreach (var p in iface.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                if (p.GetIndexParameters().Length == 0
                    && p.DeclaringType?.Namespace?.StartsWith("Loqui", StringComparison.Ordinal) != true
                    && seen.Add(p.Name))
                    yield return p.Name;
    }
}
