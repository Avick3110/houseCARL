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
/// MCP server's read tools emit.
///
/// <para><see cref="Display"/> is a DISPLAY-ONLY annotation the render layer appends in parentheses after the
/// value — NOT part of the round-trip token (the write surface reads <see cref="Token"/>, the round-trip oracle
/// drives the internal <c>LeafRead</c>, and <c>FieldsDiff</c> compares Token/Note), so it is invisible to write,
/// read-proof, and diff. Used to decode a value that is correct but opaque — a biped-slot bitmask into its slot
/// numbers — without disturbing the token that must round-trip. Null on every leaf that needs no annotation.</para>
///
/// <para><see cref="Link"/> is the resolve_names annotation: when a leaf's <see cref="Token"/> is a
/// form reference (a token that round-trips to a FormKey), the SERVICE layer resolves its target's identity
/// (editorid/name) against the load order and hangs it here. Like <see cref="Display"/> it is DISPLAY-ONLY — never
/// part of the round-trip <see cref="Token"/>, so it is invisible to write, read-proof, and diff. Populated by the
/// service (which holds the resolver), never by the core read (which reads one record's bytes and cannot resolve a
/// target). Null unless resolve_names was requested and the leaf carries a resolvable FormKey.</para></summary>
/// <param name="Present">Is anything THERE? True for a value and for a present container/substruct (whose own
/// <paramref name="Count"/> says how much); false only when the leaf holds nothing — absent, no such field,
/// unreadable. Carried structurally because the two render alike (both are parenthesised notes), so a consumer
/// deciding presence from the note text would be parsing prose and can report a vanished write as verified.</param>
/// <param name="Count">Element count for a container leaf (0 = present but EMPTY); null for a value or a substruct,
/// neither of which has one. Surfaced from <c>LeafRead.ContainerCount</c>, which already carried it.</param>
/// <param name="Readable">False when the read FAILED (a throw, or no such field) rather than finding nothing — an
/// unreadable leaf looks absent and is not evidence of absence.</param>
public sealed record FieldValue(string Path, bool HasValue, string? Token, string? Note, string? Display = null, ResolvedRef? Link = null,
                                bool Present = true, int? Count = null, bool Readable = true);

/// <summary>The resolved identity of a form reference — the shared contract behind housecarl_resolve (a full row)
/// and the resolve_names field annotation. <see cref="Resolved"/> false ⇒ the FormKey is valid but not present in
/// the active order (a dangling target — named, never dropped or guessed). <see cref="Error"/> carries the REASON:
/// the malformed-input sentence when the string is not a legal FormID at all, otherwise the three-cause sentence
/// saying whether the defining plugin was excluded, is absent from the order, or is present and defines no such
/// record. As a field annotation it is DISPLAY-ONLY: it never replaces the leaf's round-trip
/// <see cref="FieldValue.Token"/>, and the annotation renders from Resolved/Winner, not from Error.</summary>
public sealed record ResolvedRef(
    string Token, bool Resolved, string? Type = null, string? EditorId = null,
    string? Name = null, string? Winner = null, string? Error = null);

/// <summary>A located record read out as structured fields — the result the MCP server's read tools return.
/// Identity (<see cref="Type"/> / <see cref="FormKey"/> / <see cref="EditorId"/>) plus the requested (or all
/// modeled) field reads.</summary>
public sealed record RecordFields(string Type, string FormKey, string? EditorId, IReadOnlyList<FieldValue> Fields);

/// <summary>
/// The reflection-driven READ surface, symmetric partner to <see cref="WriteEngine"/>: it reflects a record's
/// modeled field OUT to a string token that is the faithful <b>inverse of Coerce</b>, so reading a value and
/// writing that exact token straight back is a byte-level no-op. <c>ReadProof</c>'s round-trip oracle drives every
/// coercible leaf the write surface drives and asserts that, so any drift between this emitter and Coerce fails loud.
///
/// Scope is a PER-PLUGIN read — record R exactly as plugin P defines it; winning-record resolution belongs to the
/// load-order layer above. Navigation is REUSED from the write engine (<c>ParseSegment</c> / <c>ResolveProperty</c> /
/// <c>StepIntoElement</c>) so read and write can never disagree on how a path resolves, but the read walk never
/// materialises an absent substruct — reading must not mutate. Per-record/per-leaf fault isolation: a
/// Mutagen-unparseable field names itself loud and never crashes the read.
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
    internal readonly record struct LeafRead(bool HasValue, string Token, string? Note, FlagBits? Flags = null, int? ContainerCount = null,
                                             bool Present = true, bool Readable = true)
    {
        public static LeafRead Value(string token) => new(true, token, null);
        public static LeafRead FlagsValue(string token, FlagBits bits) => new(true, token, null, bits);
        /// <summary>NOTHING is there — an absent optional substruct, a field the type does not have, an unreadable
        /// leaf. The ONE no-value shape that is not <see cref="Container"/>, and the difference matters to any caller
        /// deciding presence: both render as a parenthesised note, so telling them apart from the note text alone
        /// means parsing prose, which can miss a vanished substruct entirely.</summary>
        public static LeafRead None(string note) => new(false, "", note, null, null, Present: false);

        /// <summary>The read FAILED — a throw at navigation, or a field the type does not have. Absent-looking, but
        /// not evidence of ABSENCE: a caller deciding whether content vanished must not read "I could not look" as
        /// "there is nothing there", or it reports a correct write as lost.</summary>
        public static LeafRead Unreadable(string note) => new(false, "", note, null, null, Present: false, Readable: false);
        /// <summary>A no-value CONTAINER/substruct summary carrying its element <paramref name="count"/>: null for a
        /// substruct (present by being non-null — no element count), a number for a list/dict (0 = present-but-EMPTY).
        /// The count is additive metadata for the presence predicate (<c>exists</c>/<c>missing</c>), which must tell
        /// an EMPTY list from a carried one WITHOUT re-parsing the display note. The Token is empty and the oracle
        /// never drives a no-value read, so this is invisible to read/write/diff, exactly like <see cref="Flags"/>.</summary>
        public static LeafRead Container(string note, int? count) => new(false, "", note, null, count);
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
    /// string whose <c>.STRINGS</c> entry for the target language is not in the workspace: genuinely absent, NOT the
    /// cleaned-masters case <see cref="LoadOrderResolver.OpenOverlay"/>'s strings source resolves. Surfaced as a
    /// no-value NOTE — never a blank token — so a value predicate cannot silently treat it as a real non-matching
    /// value (a `where Name contains …` reporting a false "0 matches") and a read renders it loud, not as an empty
    /// Name indistinguishable from a record that truly has none. Like <see cref="AbsentNote"/> /
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
            Console.WriteLine($"  {fv.Path} = {(fv.HasValue ? fv.Token : fv.Note)}{(fv.Display is null ? "" : $"   ({fv.Display})")}");
        return 0;
    }

    /// <summary>The depth-1 container hint: appended to an unexpanded container/substruct summary so an agent turns
    /// the depth= knob instead of inventing a param. It names <c>depth=2</c>, which is only honest on a surface that
    /// HAS a depth= parameter (read_record / batch_record_detail / read_plugin_file / cross_plugin_query text+json /
    /// the CLI). A caller whose surface refuses depth passes its own redirect via <c>containerHint</c>
    /// (cross_plugin_query's DENSE render names the text/json format hop — its positional cells refuse depth&gt;1),
    /// or null to suppress it (write read-backs, where the count IS the confirmation).</summary>
    public const string DepthExpandHint = " — pass depth=2 to expand";

    /// <summary>Read a located record's fields as round-trippable tokens — the structured entry the MCP server
    /// consumes (<c>RunRead</c> is the CLI sibling). With <paramref name="paths"/>: exactly those leaves. Without: a
    /// one-level dump of every modeled field. Wraps the internal <see cref="ReadLeaf"/> the round-trip oracle drives,
    /// so the server's reads inherit the read-proof by construction. Per-leaf fault isolation: an unreadable field
    /// names itself in its <see cref="FieldValue.Note"/>, never throws out of the record read.</summary>
    public static RecordFields ReadFields(IMajorRecordGetter record, IReadOnlyList<string>? paths = null, int depth = 1,
                                          string? containerHint = DepthExpandHint)
    {
        var typeName = RecordNaming.StripGetterInterface(WriteEngine.PrimaryGetter(record.GetType())?.Name ?? "I?Getter");
        var targets = paths is { Count: > 0 } ? (IEnumerable<string>)paths : ModeledFieldNames(typeName, record.GetType());
        var fields = new List<FieldValue>();
        if (depth <= 1)
        {
            // depth 1 (default) — the one-level read the round-trip oracle drives (ReadLeaf). Expansion
            // (depth>=2) is a separate branch below, so the oracle-critical leaf path stays untouched.
            foreach (var p in targets)
            {
                var seg = p.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var r = ReadLeaf(record, seg);
                string? note = r.HasValue ? null : r.Note;
                // An UNEXPANDED container / substruct leaf self-documents the lever that opens it: at the depth-1
                // default it renders as a count/summary ("[list: 2 item(s)]", "[BodyTemplate]"). No-value NOTES are
                // parenthesized ("(absent)", "(null link)"), so the leading-'[' test targets exactly the
                // container/substruct summaries. Depth-1 only — the deep read FieldsDiff runs never sees the hint.
                // The hint text is the caller's (containerHint): depth=2 is only a real knob on some surfaces.
                if (note is { Length: > 0 } && note[0] == '[' && !string.IsNullOrEmpty(containerHint)) note += containerHint;
                fields.Add(new FieldValue(p, r.HasValue, r.HasValue ? r.Token : null, note, FlagDisplay(r),
                                          Present: r.Present, Count: r.ContainerCount, Readable: r.Readable));
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
    /// materialised. Per-leaf fault isolation: any reflection/parse failure names itself and never
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
                if (p is null) return LeafRead.Unreadable(NoFieldNote(current, segName, i > 0 ? WriteEngine.ParseSegment(path[i - 1]).name : null, path[(i + 1)..]));
                current = segKey is null
                    ? p.GetValue(current)                                  // descend a substruct (read-only)
                    : WriteEngine.StepIntoElement(current, p, segName, segKey); // collnav (handles IReadOnly*)
                if (current is null) return LeafRead.None(AbsentNote);     // absent optional substruct
            }

            var (leafName, leafKey) = WriteEngine.ParseSegment(path[^1]);
            var leaf = WriteEngine.ResolveProperty(current!.GetType(), leafName);
            if (leaf is null) return LeafRead.Unreadable(NoFieldNote(current, leafName, path.Length >= 2 ? WriteEngine.ParseSegment(path[^2]).name : null));
            if (leafKey is not null)
            {
                // The leaf brackets a collection element (Keywords[0]) — step in and emit the element.
                var elem = WriteEngine.StepIntoElement(current, leaf, leafName, leafKey);
                return EmitToken(elem, elem.GetType(), current);
            }
            return EmitToken(leaf.GetValue(current), leaf.PropertyType, current);
        }
        catch (Exception ex) { return LeafRead.Unreadable($"(unreadable: {ex.Message})"); }
    }

    /// <summary>The FormKeys on a record's <c>Keywords</c> list — the ONE keyword walk, shared by the SkyPatcher
    /// overlay, the post-state service resolver and the show CLI so their property resolution and non-formlink
    /// handling cannot diverge. Resolves the property the same
    /// way the engines do (<see cref="WriteEngine.ResolveProperty"/>), so explicit-interface getters
    /// can't hide it. An ABSENT (null) list honestly reads as EMPTY — a record with no keyword list
    /// carries no keywords. Null is reserved for "no such property / not a formlink list" (surfaced
    /// loud by callers, never guessed).</summary>
    public static IReadOnlyList<FormKey>? KeywordKeys(object record)
    {
        var p = WriteEngine.ResolveProperty(record.GetType(), "Keywords");
        if (p is null) return null;
        if (p.GetValue(record) is not System.Collections.IEnumerable list) return new List<FormKey>();   // absent list reads as empty
        return FormLinkKeys(list);
    }

    /// <summary>The FormKeys of one formlink-list value — the ONE enumerable→FormKey walk
    /// (<see cref="KeywordKeys"/> and the SkyPatcher overlay's list ops both ride it, so link-reading
    /// can't drift between them). Null the moment an element isn't a formlink (loud upstream).</summary>
    public static List<FormKey>? FormLinkKeys(System.Collections.IEnumerable list)
    {
        var keys = new List<FormKey>();
        foreach (var item in list)
        {
            if (item is IFormLinkGetter link) keys.Add(link.FormKey);
            else return null;
        }
        return keys;
    }

    /// <summary>Collect every FormKey linked UNDER one field path — the `-&gt;` link-step's left side.
    /// Navigation is the same engine walk reads use (<see cref="NavigateValue"/>), then the value
    /// yields its links by shape: a FormLink → its key; a list → each element's own links (a direct link element,
    /// or a link-bearing struct like PerkPlacement via Mutagen's generic <c>EnumerateFormLinks</c>); a link-bearing
    /// substruct → its links. De-duplicated, null links dropped. Returns (null, note) when the path doesn't reach a
    /// link-bearing value — the note reuses the read walk's vocabulary ("(no field …", "(absent)", "(unreadable …")
    /// so callers classify it exactly like a leaf miss; a present-but-empty list returns an EMPTY list (a genuine
    /// "no links here", distinct from "not a link path").</summary>
    public static (List<FormKey>? Links, string? Note) CollectLinksAt(object record, string[] path)
    {
        try
        {
            var nav = NavigateValue(record, path);
            if (!nav.ok) return (null, nav.note);
            if (nav.val is null) return (null, AbsentNote);
            var keys = new List<FormKey>();
            var seen = new HashSet<FormKey>();
            void Add(FormKey fk) { if (!fk.IsNull && seen.Add(fk)) keys.Add(fk); }
            switch (nav.val)
            {
                case IFormLinkGetter link:
                    Add(link.FormKey);
                    break;
                case string:
                    return (null, $"(no links: '{string.Join(".", path)}' is a string, not a link-bearing field)");
                case System.Collections.IEnumerable list:
                    foreach (var item in list)
                    {
                        if (item is IFormLinkGetter il) Add(il.FormKey);
                        else if (item is IFormLinkContainerGetter fc)
                            foreach (var l in fc.EnumerateFormLinks()) Add(l.FormKey);
                    }
                    break;
                case IFormLinkContainerGetter sub:
                    foreach (var l in sub.EnumerateFormLinks()) Add(l.FormKey);
                    break;
                default:
                    return (null, $"(no links: '{string.Join(".", path)}' is not a link-bearing field)");
            }
            return (keys, null);
        }
        catch (Exception ex) { return (null, $"(unreadable: {ex.Message})"); }
    }

    /// <summary>A "no such field" note that, when the owner is a collection, points the caller at bracket
    /// indexing — the common <c>.0</c>-vs-<c>[0]</c> confusion (the read analog of the write pre-flight's
    /// bracket hint in <c>CorpusRulebook</c>). Brackets are how you step into a list/dict element mid-path;
    /// a bare dotted <c>.0</c> is parsed as a field name and dead-ends here.</summary>
    static string NoFieldNote(object owner, string segName, string? precedingField, string[]? trailing = null)
    {
        bool ownerIsCollection = owner is System.Collections.IDictionary
            || (owner is System.Collections.IEnumerable && owner is not string);
        if (ownerIsCollection)
        {
            var pf = precedingField ?? "<field>";
            return $"(no field '{segName}': '{pf}' is a list/dict — {ListHopRemedy(owner, segName, pf, trailing)})";
        }
        return $"(no field {segName})";
    }

    /// <summary>What to actually DO about a path that dotted THROUGH a list/dict — checked against the element
    /// type, never asserted. A missing bracket and a wrong leaf name look identical at the dead-end, and they need
    /// opposite next moves, so the segment is resolved against the collection's element type first: it exists, and
    /// the remedy is the exact bracketed spelling; it does not, and the remedy says so and offers the nearest real
    /// field. Only where the element type cannot be determined does this fall back to the shape advice alone.
    /// <para><paramref name="trailing"/> is the rest of the path after the dead-end segment, so the remedy prints the
    /// WHOLE fixed spelling: a caller pasting it back gets a path that reads, not a prefix that refuses again.</para></summary>
    internal static string ListHopRemedy(object owner, string segName, string pf, string[]? trailing = null)
    {
        var rest = trailing is { Length: > 0 } ? "." + string.Join(".", trailing) : "";

        // A numeric segment is the '.0'-vs-'[0]' confusion, not a field name: it is an INDEX, and no element type
        // check applies.
        if (segName.Length > 0 && segName.All(char.IsDigit))
            return $"index an element with brackets, e.g. '{pf}[{segName}]{rest}', not '{pf}.{segName}{rest}'";

        var et = ElementType(owner);
        if (et is null)
            return $"index an element with brackets, e.g. '{pf}[0].{segName}{rest}', not '{pf}.{segName}{rest}'";

        var etName = RecordNaming.StripOverlay(et.Name);
        if (WriteEngine.ResolveProperty(et, segName) is not null)
            return $"index an element with brackets: '{pf}[0].{segName}{rest}', not '{pf}.{segName}{rest}'";

        var near = PluginNameSuggest.Nearest(segName, ElementFieldNames(et), 1);
        return near.Count > 0
            ? $"'{segName}' is not a field on its element type {etName} — did you mean '{pf}[0].{near[0]}{rest}'?"
            : $"'{segName}' is not a field on its element type {etName}; index an element with brackets " +
              $"('{pf}[0].<field>') and name a field {etName} has";
    }

    /// <summary>The element type of a collection, from the strongly-typed <c>IEnumerable&lt;T&gt;</c> it implements
    /// (a dictionary's values where it is a dictionary), falling back to the runtime type of its first element. Null
    /// when the collection is untyped and empty — nothing can be said about its elements then.</summary>
    static Type? ElementType(object owner)
    {
        foreach (var i in owner.GetType().GetInterfaces())
        {
            if (!i.IsGenericType) continue;
            var d = i.GetGenericTypeDefinition();
            if (d == typeof(IReadOnlyDictionary<,>) || d == typeof(IDictionary<,>)) return i.GetGenericArguments()[1];
        }
        foreach (var i in owner.GetType().GetInterfaces())
            if (i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                var t = i.GetGenericArguments()[0];
                if (t != typeof(object)) return t;
            }
        if (owner is System.Collections.IEnumerable e)
            foreach (var first in e) { if (first is not null) return first.GetType(); break; }
        return null;
    }

    /// <summary>Every public instance property name on an element type, across the interfaces the read walk resolves
    /// through — the candidate set the nearest-name suggestion is drawn from, so a suggestion can only ever name a
    /// field the caller could actually use.</summary>
    static IEnumerable<string> ElementFieldNames(Type et)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in new[] { et }.Concat(et.GetInterfaces()))
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                if (p.GetIndexParameters().Length == 0 && seen.Add(p.Name)) yield return p.Name;
    }

    // ======================================================================
    //  DESCENDABLE READS (depth>=2) — enumerate list/dict/substruct CONTENTS so element indices and
    //  sub-fields are discoverable without hand-probing each [i]. Navigation reuses the same engine walk
    //  (ParseSegment/ResolveProperty/StepIntoElement) and EmitToken as the leaf path, but the depth-1
    //  ReadLeaf is untouched. Bounded by MaxExpandNodes, with an explicit truncation note.
    // ======================================================================

    /// <summary>Max FieldValue lines one descendable read will GENERATE (separate from the renderer's char
    /// cap) — a guard so depth-expanding a huge container can't build a runaway result. Over it, a single
    /// truncation note is emitted: bounded, never silent.</summary>
    internal const int MaxExpandNodes = 2000;

    static readonly string[] IdentityFieldNames = { "Name", "EditorID", "Title" };

    /// <summary>Emit one target path, expanding container/substruct contents up to <paramref name="depth"/>
    /// levels. A miss surfaces the same bracket-aware note the leaf read uses. The body is wrapped in the same
    /// per-field fault isolation depth-1 <see cref="ReadLeaf"/> gives: a throw while navigating OR expanding
    /// this one target (an unparseable nested getter, an enumerator that faults mid-list, an ambiguous identity
    /// reflection) names itself "(unreadable …)" and never escapes the record read — so one bad field can't crash
    /// a whole-record depth dump. Lines already emitted before a mid-expansion fault are real reads and are kept.</summary>
    static void EmitWithDepth(object record, string path, int depth, List<FieldValue> sink, ref int budget)
    {
        try
        {
            var seg = path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var nav = NavigateValue(record, seg);
            if (!nav.ok) { Emit(sink, ref budget, new FieldValue(path, false, null, nav.note, Present: false)); return; }
            Expand(nav.val, nav.type, nav.parent, path, depth, sink, ref budget);
        }
        catch (Exception ex) { Emit(sink, ref budget, new FieldValue(path, false, null, $"(unreadable: {ex.Message})", Present: false)); }
    }

    /// <summary>Recursively emit <paramref name="val"/> at <paramref name="path"/>: a value leaf → its token;
    /// a link → its note (not opened); a container/substruct → an identity-enriched summary line, then (while
    /// depth allows) one child line per element (bracketed) or sub-field (dotted), each recursed at depth-1.</summary>
    static void Expand(object? val, Type declaredType, object parent, string path, int depth, List<FieldValue> sink, ref int budget)
    {
        if (budget < 0) return;
        var leaf = EmitToken(val, declaredType, parent);
        if (leaf.HasValue) { Emit(sink, ref budget, new FieldValue(path, true, leaf.Token, null, FlagDisplay(leaf))); return; }
        if (val is null) { Emit(sink, ref budget, new FieldValue(path, false, null, leaf.Note, Present: false)); return; }
        // a link (incl. a null FormKey, or an FLOI) is a note, not an openable container/substruct.
        if (val is IFormLinkGetter || WriteEngine.IsFormLinkOrIndex(Nullable.GetUnderlyingType(declaredType) ?? declaredType))
        { Emit(sink, ref budget, new FieldValue(path, false, null, leaf.Note, Present: leaf.Present)); return; }

        // Classify dict-vs-list the SAME way the navigation does (StepIntoElement) — by the GENERIC dictionary
        // interfaces via ClosedInterface, not a separate non-generic System.Collections.IDictionary cast — so the
        // browse view and the read/write path can't drift (a getter dict need not expose the non-generic interface).
        // A generic dict enumerates as KeyValuePair<,>; Key/Value come off each pair. Classified BEFORE the
        // summary line so the summary can carry the dict marker ("pair(s)" vs "item(s)") — the in-band signal
        // FieldsDiff uses to keep numeric-KEYED dicts (Package.Data) out of positional-list comparison, where
        // a key rebinding would wrongly compare "identical".
        bool isDict = WriteEngine.ClosedInterface(val.GetType(), typeof(IDictionary<,>)) is not null
                   || WriteEngine.ClosedInterface(val.GetType(), typeof(IReadOnlyDictionary<,>)) is not null;

        // a container or substruct — summarise (with an element identity where we can), then maybe open it.
        // Present/Count are set on the DEEP path too: they exist so a consumer never parses a note to decide
        // presence, and left at their defaults a depth-2 read of an EMPTY list claims content.
        int? deepCount = val is System.Collections.IEnumerable de and not string ? de.Cast<object?>().Count() : null;
        if (!Emit(sink, ref budget, new FieldValue(path, false, null, ElementSummary(val, isDict), Present: true, Count: deepCount))) return;

        // Two POLYMORPHIC-ARM families normally stop here at their identity summary, hiding their VALUE, and both
        // surface it ONE bounded level deeper even at the depth floor so a read reaches parity with the write
        // surface and with a direct per-arm path:
        //   * a VMAD script property (e.g. "[ScriptObjectProperty] Name=DAK_HorseBuyPerk") — its Object FormLink
        //     (incl. a declared-but-null link, the signal the quest-fragment linter keys on), Data scalar, Alias;
        //   * a Conditions[].Data arm (e.g. "[GetFactionRankConditionData]") — its parameter fields (Faction,
        //     Global, Reference, RunOnType…), which otherwise appear ONLY when Data is addressed directly: the arm
        //     would consume a summary-only level, so a Conditions-list dump would need one extra depth to show them.
        // Each family's direct members are leaves/links (a VMAD *ListProperty arm shows as a count at the floor;
        // raise depth= to enumerate it), so this opens exactly one level and never unbounded-descends. EVERY OTHER
        // substruct still stops at the floor — the exception is these two arm families only, matched by their
        // shared getter interface (no per-arm list).
        int childDepth = depth - 1;
        if (depth <= 1)
        {
            if (!IsScriptProperty(val.GetType()) && !IsConditionData(val.GetType())) return;
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
            // conflict-diff comparison is unchanged.
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
            // UnderlyingSystemType — ~50 KB of reflection internals that is never record data.
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
    /// stop (returns false thereafter so callers unwind). The cut is named, never silent.</summary>
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
    /// instead of a token, so the expander can descend into it. Fault-isolated.</summary>
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
                    NoFieldNote(current, segName, i > 0 ? WriteEngine.ParseSegment(path[i - 1]).name : null, path[(i + 1)..]));
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
    /// "what landed" line. For a single list <c>Add</c>, the new last element + the new count
    /// (<c>now 29 (+1), new [28] = …</c>); for a batch <c>composes=</c> Add of N, the whole appended run
    /// (<c>now 34 (+6), new [28..33]</c> — <paramref name="added"/> carries how many the op appended); for a keyed
    /// <c>SetAtIndex</c>/<c>InsertAtIndex</c>/<c>Remove</c>, the touched key + new count;
    /// else the new count.
    /// Names the element as specifically as the model allows — a
    /// formlink element renders its FormKey, an identity-bearing struct its Name/EditorID, an anonymous struct (a
    /// condition) its <c>[Type]</c>. Read-only; NEVER throws (null on any difficulty) — a display nicety on an
    /// ALREADY-succeeded write, never load-bearing. <paramref name="leafPath"/> is the verb's path to the collection
    /// (the engine's <see cref="WriteRequest.Path"/>); <paramref name="key"/> its list index / dict key, if any.</summary>
    internal static string? TouchedElement(object record, string[] leafPath, string verb, string? key, int added = 1)
    {
        try
        {
            var nav = NavigateValue(record, leafPath);
            if (!nav.ok || nav.val is not System.Collections.IEnumerable en || nav.val is string) return null;
            int count = 0; object? last = null;
            foreach (var e in en) { count++; last = e; }
            return verb switch
            {
                "Add"        => AddLanded(record, count, last, added),
                "ReplaceAll" => $"now {count} item(s) (replaced)",
                "SetAtIndex" => key is not null ? $"now {count} item(s), set [{key}]" : $"now {count} item(s)",
                // Insert says INSERTED, not set: the count moved and every element at or after [key] shifted right,
                // which is the whole difference between this verb and its sibling and the thing a caller is checking.
                "InsertAtIndex" => key is not null ? $"now {count} item(s), inserted [{key}]" : $"now {count} item(s)",
                "Remove"     => key is not null ? $"now {count} item(s), removed [{key}]" : $"now {count} item(s) (-1)",
                _            => $"now {count} item(s)",
            };
        }
        catch { return null; }
    }

    /// <summary>The list-<c>Add</c> "what landed" line, honest about the appended count. A SINGLE append names the
    /// new element (<c>now 29 (+1), new [28] = …</c>); a BATCH <c>composes=</c> Add of N names the whole appended run
    /// of indices (<c>now 34 (+6), new [28..33]</c>) instead of reporting only the last element as a "(+1)", which
    /// reads as though a single element landed. <paramref name="added"/> is the op's appended count
    /// (composes.Count, else 1), clamped to
    /// the live count so a display nicety on an already-succeeded write can never throw or under-run the range.</summary>
    static string AddLanded(object record, int count, object? last, int added)
    {
        if (last is null) return $"now {count} item(s)";
        int n = Math.Clamp(added, 1, count);
        return n <= 1
            ? $"now {count} (+1), new [{count - 1}] = {ElementId(last, record)}"
            : $"now {count} (+{n}), new [{count - n}..{count - 1}]";
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
        // An owned child RECORD element (an IMajorRecordGetter — a DIAL's Responses hold DialogResponses/INFO
        // records, a CELL's references hold placed records; each owns its own FormKey) leads with its FormKey the
        // way a top-level read does. Checked BEFORE the Name/EditorID/Title scan: for an owned record the FormKey IS
        // the canonical identity (an INFO has no Name and usually no EditorID, so the lone-FormLink path below can't
        // reach it), and EditorID rides along when present, so a depth=2 owned-record list reads
        // "[DialogResponses 4D9A74:Plugin.esp editorid=…]" rather than a bare opaque [Type].
        if (val is IMajorRecordGetter mr)
            return $"[{typeName} {mr.FormKey}{(string.IsNullOrEmpty(mr.EditorID) ? "" : $" editorid={mr.EditorID}")}]";
        foreach (var idName in IdentityFieldNames)
        {
            var p = t.GetProperty(idName, BindingFlags.Public | BindingFlags.Instance);
            if (p is null || p.GetIndexParameters().Length != 0) continue;
            object? iv; try { iv = p.GetValue(val); } catch { continue; }
            var s = iv switch { null => null, string str => str, IFormLinkGetter fl => fl.FormKey.ToString(), _ => iv.ToString() };
            if (!string.IsNullOrEmpty(s)) return $"[{typeName}] {idName}={s}";
        }
        // No Name/EditorID/Title identity. If the struct carries EXACTLY ONE FormLink field, that link IS its
        // identity (PerkPlacement.Perk, and any other single-link struct) — surface it so a depth=2 element line
        // reveals which record it points at, the way a Name= identity does, instead of a bare [Type] that reads as
        // "the FormID isn't surfaced". Exactly one link only — 2+ are ambiguous and we don't guess which is the
        // identity.
        if (LoneFormLinkIdentity(val, t) is { } linkId) return $"[{typeName}] {linkId}";
        return $"[{typeName}]";
    }

    /// <summary>The <c>Field=FormKey</c> identity of a struct element that has EXACTLY ONE FormLink property and no
    /// Name/EditorID/Title identity — e.g. PerkPlacement → <c>Perk=03AF81:Skyrim.esm</c>. Null when the struct has no
    /// FormLink or MORE THAN ONE (ambiguous — don't guess which is the identity). A present-but-null link still
    /// counts: it names the field and shows the null FormKey, the exact signal a reader chasing a dangling ref wants.
    /// Display-only, best-effort — any reflection fault yields null (falls back to the bare <c>[Type]</c>).</summary>
    static string? LoneFormLinkIdentity(object val, Type t)
    {
        PropertyInfo? only = null;
        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length != 0) continue;
            if (!typeof(IFormLinkGetter).IsAssignableFrom(p.PropertyType)) continue;
            if (only is not null) return null;   // 2+ FormLink fields — ambiguous, don't guess
            only = p;
        }
        if (only is null) return null;
        try { return only.GetValue(val) is IFormLinkGetter fl ? $"{only.Name}={fl.FormKey}" : null; }
        catch { return null; }
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

        // FLOI (condition targets) — emit a token ClassifyFloiValue re-accepts (FormKey, or
        // "alias N" / "packdata N"), inferred from the parent arm's UseAliases/UsePackageData.
        if (WriteEngine.IsFormLinkOrIndex(u)) return EmitFloi(val, parent);

        // primitive (inverse of TryPrimitive)
        if (TryEmitPrimitive(val, out var prim)) return LeafRead.Value(prim);
        // enum (inverse of TryEnum) — ToString gives the name(s); Enum.Parse(ignoreCase) re-accepts,
        // including the comma form for a [Flags] combination. For a [Flags] enum we ALSO carry the underlying
        // bit pattern + type (the display token is unchanged) so the query predicate can bit-test (`has`) and
        // compare numerically without tripping over the name-vs-number rendering split.
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
        // surfaced LOUD as no-value, never a blank token, so a predicate cannot silently count it as a
        // non-match. Must stay ahead of TryEmitValueType, which would fold the null into "".
        if (val.GetType().FullName == "Mutagen.Bethesda.Strings.TranslatedString")
        {
            var s = ReflectString(val, "String");
            return s is null ? LeafRead.None(UnresolvedStringNote) : LeafRead.Value(s);
        }
        // value types (inverse of TryValueType)
        if (TryEmitValueType(val, out var vt)) return LeafRead.Value(vt);

        // Not a single-token VALUE leaf: a substruct / collection / arm container. The oracle never
        // drives these AS leaves — their sub-leaves are driven individually (exactly like write-proof).
        // Summarise for the read display, with the same dict-vs-list marker the depth walk renders, and carry the
        // element count STRUCTURALLY (Container) so the presence predicate tells an empty list from a carried one
        // without re-parsing the display note.
        bool isDict = WriteEngine.ClosedInterface(val.GetType(), typeof(IDictionary<,>)) is not null
                   || WriteEngine.ClosedInterface(val.GetType(), typeof(IReadOnlyDictionary<,>)) is not null;
        var summary = SummariseContainer(val, isDict, out var count);
        return LeafRead.Container(summary, count);
    }

    /// <summary>The unsigned bit pattern of a boxed enum value, robust across every underlying integer type
    /// (signed or unsigned) — the bits a <c>has</c> predicate ANDs against, and the write engine's flags
    /// Add/Remove OR/AND-NOT operand. Read through the declared underlying type so a high-bit-set signed enum
    /// yields its two's-complement pattern rather than overflowing.</summary>
    internal static bool TryEnumBits(object val, Type enumType, out ulong bits)
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

    /// <summary>The DISPLAY-ONLY biped-slot decode for a <c>BodyTemplate.FirstPersonFlags</c> leaf (enum
    /// <c>BipedObjectFlag</c>): the equipped SLOT NUMBERS ("slots 32, 34, 53") derived from the bit pattern
    /// (slot = 30 + bit index). Armor/slot analysis wants the slot numbers, but <c>[Flags].ToString()</c> gives
    /// enum NAMES when every set bit is named ("Body") and falls back to a bare decimal the moment an unnamed
    /// modder slot is set (e.g. <c>8388980</c> for a slot-53 addon) — neither is the slot list the modder reasons
    /// in. This annotation rides <see cref="FieldValue.Display"/>, so the round-trip
    /// <see cref="LeafRead.Token"/> is untouched (write/read-proof/diff never see it). Gated to BipedObjectFlag by
    /// name — the slot=30+bit mapping is meaningless for any other <c>[Flags]</c> enum. Null for every non-biped
    /// leaf, an unset mask, or a non-flags value.</summary>
    internal static string? FlagSlotDisplay(LeafRead leaf)
    {
        if (!leaf.HasValue || leaf.Flags is not { } fb || fb.EnumType.Name != "BipedObjectFlag") return null;
        var slots = new List<int>();
        for (int i = 0; i < 32; i++) if ((fb.Bits & (1UL << i)) != 0) slots.Add(30 + i);
        if (slots.Count == 0) return null;
        return (slots.Count == 1 ? "slot " : "slots ") + string.Join(", ", slots);
    }

    /// <summary>The DISPLAY-ONLY annotation for a <c>[Flags]</c> enum leaf — the human-readable decode that rides
    /// <see cref="FieldValue.Display"/> without touching the round-trip <see cref="LeafRead.Token"/>. A biped-slot
    /// flags leaf gets the slot-number decode (<see cref="FlagSlotDisplay"/>); every OTHER flags enum gets the
    /// unknown-bits decode (<see cref="FlagBitsDisplay"/>), which fires only when unnamed bits are present. The two
    /// are mutually exclusive by construction — a biped leaf with any bit set already yields a slot decode, and one
    /// with no bit set has no unknown bits either — so the <c>??</c> never double-annotates. Null when neither
    /// applies (a non-flags leaf, or a flags leaf whose every set bit is already named).</summary>
    internal static string? FlagDisplay(LeafRead leaf) => FlagSlotDisplay(leaf) ?? FlagBitsDisplay(leaf);

    /// <summary>The DISPLAY-ONLY decode for a <c>[Flags]</c> enum leaf carrying bits the catalog does NOT name — the
    /// case where <c>[Flags].ToString()</c> abandons the name list and renders a bare decimal (e.g. an NPC
    /// <c>Configuration.Flags</c> whose value includes an unnamed modder/game-version bit), silently losing even the
    /// KNOWN bits a consumer needs (gender / uniqueness / ghost state). This surfaces the known bits by NAME
    /// plus the unnamed remainder as an explicit hex mask — <c>&lt;known flag names&gt; (+unknown bits 0x…)</c> — so
    /// the common bits stay directly consumable and the presence of unknown bits is STATED, not hidden. Rides
    /// <see cref="FieldValue.Display"/>, so the round-trip <see cref="LeafRead.Token"/> (the bare decimal, which
    /// <c>Enum.Parse</c> re-accepts) is untouched — write / read-proof / diff never see it, exactly like the
    /// biped-slot decode. Null when the leaf is not a flags enum OR every set bit is already named (ToString gave
    /// the full name list — nothing to recover).</summary>
    internal static string? FlagBitsDisplay(LeafRead leaf)
    {
        if (!leaf.HasValue || leaf.Flags is not { } fb) return null;
        // Peel the NAMEABLE bits exactly the way .NET's [Flags].ToString() does: greedily apply each named member that
        // is FULLY contained (largest value first, so a multi-bit COMBO member wins over its constituent bits), and
        // whatever bits no member can cover are the unknown remainder. Do NOT just OR every member's bits into one
        // "known" mask: a bit that exists ONLY inside a multi-bit combo member (e.g. Package.Flag.WearSleepOutfit)
        // would count as "known" yet ToString can't name it on its own, so the "known names" slot would itself render
        // a bare decimal — the very thing this decode exists to avoid.
        var members = new List<ulong>();
        foreach (var member in Enum.GetValues(fb.EnumType))
            if (TryEnumBits(member, fb.EnumType, out var mb) && mb != 0) members.Add(mb);
        members.Sort((a, b) => b.CompareTo(a));   // descending (unsigned) — a combo before its constituent bits
        ulong remainder = fb.Bits;
        foreach (var mb in members) if ((remainder & mb) == mb) remainder &= ~mb;
        if (remainder == 0) return null;   // every set bit is nameable — ToString already gave the full name list
        // The nameable bits are exactly a union of whole members, so ToString renders them as clean names (never a
        // decimal); state the remainder as an explicit hex mask so nothing is silently dropped.
        ulong nameable = fb.Bits & ~remainder;
        var names = nameable == 0 ? null : Enum.ToObject(fb.EnumType, nameable).ToString();
        return string.IsNullOrEmpty(names) || names == "0"
            ? $"unknown bits 0x{remainder:X}"
            : $"{names} (+unknown bits 0x{remainder:X})";
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

        // (TranslatedString is handled earlier in EmitToken — a null .String must surface as a loud no-value note,
        //  not a blank token; see UnresolvedStringNote.)

        // Noggog.Percent — emit the [0..1] fraction its single-arg ctor takes. Find the underlying
        // numeric member BY TYPE, not a guessed name: Percent stores exactly one double (its ToString
        // is the "33%" display form, which Coerce can't parse).
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
    /// a note, never a guessed four bytes.</summary>
    static LeafRead EmitFloi(object val, object parent)
    {
        bool? useAliases = ReflectBool(parent, "UseAliases");
        bool? usePackData = ReflectBool(parent, "UsePackageData");
        if (useAliases is null || usePackData is null)
            return LeafRead.Unreadable($"(floi: parent {parent.GetType().Name} has no UseAliases/UsePackageData discriminator)");

        if (useAliases == false && usePackData == false)
        {
            // Form mode. The binary overlay's FLOI is NOT itself a link — it carries the link in its
            // .Link property, the same accessor the write side reads (ReadFloiFormKey).
            // A present-but-null link stays a note, matching plain FormLink leaves.
            if (val is IFormLinkGetter fl) return LeafRead.Value(fl.FormKey.ToString());
            if (WriteEngine.ReadFloiFormKey(val) is { } fk) return LeafRead.Value(fk.ToString());
            return LeafRead.Unreadable($"(floi: form mode, null or unreadable FormKey on {val.GetType().Name})");
        }

        // index mode — read the numeric index defensively (FormLinkOrIndex carries it alongside the link).
        var idx = ReflectUInt(val, "Index", "RawIndex", "FormKeyOrIndex");
        if (idx is null) return LeafRead.Unreadable("(floi: index mode, index accessor unresolved — refined in oracle)");
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
    /// the depth floor, for read parity with the write surface. One of TWO type-targeted exceptions to the depth
    /// gate (the other is <see cref="IsConditionData"/>); every other substruct stops at the floor.</summary>
    static bool IsScriptProperty(Type t) => typeof(IScriptPropertyGetter).IsAssignableFrom(t);

    /// <summary>True if <paramref name="t"/> is a polymorphic CONDITION-DATA arm (GetActorValueConditionData,
    /// GetFactionRankConditionData — every <c>ConditionData</c> subtype) — recognised by the shared
    /// <c>IConditionDataGetter</c> interface, so every arm matches by construction with no per-arm list, on the
    /// overlay getter or the mutable type alike. Like <see cref="IsScriptProperty"/> the depth walker opens such an
    /// arm's parameter fields one bounded level past the depth floor so a <c>Conditions</c>-list dump reaches the
    /// arm's params (Faction/Global/Reference/RunOnType…) without an extra depth level or a per-row <c>Data</c> path.
    /// An arm's direct members are leaves/links, so
    /// this opens exactly one level and never unbounded-descends; every non-arm substruct still stops at the floor.</summary>
    static bool IsConditionData(Type t) => typeof(IConditionDataGetter).IsAssignableFrom(t);

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
    /// arm) for the read display. Its sub-leaves are the round-trippable surface. A collection renders as a
    /// clean <c>[list: N item(s)]</c> / <c>[dict: N pair(s)]</c> — the Mutagen overlay class name
    /// (<c>BinaryOverlayListByStartIndex`1</c>) is container plumbing, NOT the element type, so it is pure noise
    /// to the reader and is dropped. The <c>item(s)</c>/<c>pair(s)</c> marker is LOAD-BEARING —
    /// <c>FieldsDiff</c> splits numeric-keyed dicts (Package.Data) out of positional-list comparison on the
    /// exact <c>" pair(s)]"</c> substring — so it is kept verbatim. A substruct keeps its
    /// <c>[TypeName]</c> (e.g. <c>[BodyTemplate]</c>): there the type name IS informative.</summary>
    static string SummariseContainer(object val, bool isDict = false) => SummariseContainer(val, isDict, out _);

    /// <summary>Overload that also yields the element <paramref name="count"/>: a number for a list/dict (0 = empty),
    /// null for a substruct (no element count — present by being non-null). The presence predicate reads this to
    /// tell a carried list from an empty one; every display caller keeps the count-free overload above. One
    /// enumeration, one format source (the <c>item(s)</c>/<c>pair(s)</c> marker stays load-bearing for FieldsDiff).</summary>
    static string SummariseContainer(object val, bool isDict, out int? count)
    {
        count = null;
        if (val is System.Collections.IEnumerable en and not string)
        {
            int n = 0;
            foreach (var _ in en) n++;
            count = n;
            return $"[{(isDict ? "dict" : "list")}: {n} {(isDict ? "pair(s)" : "item(s)")}]";
        }
        // StripOverlay as well as StripGetterInterface, matching ElementSummary: a binary overlay loads
        // WeaponBasicStatsBinaryOverlay for the same type a mutable record calls WeaponBasicStats, and this summary
        // is what the in-place verify prints for a substruct leaf read off the WRITTEN FILE. Without it one response
        // says "-> [WeaponBasicStats]" in its edit list and "[WeaponBasicStatsBinaryOverlay]" two lines below.
        return $"[{RecordNaming.StripGetterInterface(RecordNaming.StripOverlay(val.GetType().Name))}]";
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
