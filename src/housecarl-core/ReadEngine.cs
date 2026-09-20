using System.Globalization;
using System.Reflection;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlCore;

/// <summary>One field read off a record: a round-trippable <see cref="Token"/> when <see cref="HasValue"/> is
/// true, else a <see cref="Note"/> saying why there is no value. <see cref="Display"/> and <see cref="Link"/>
/// are DISPLAY-ONLY; <see cref="Present"/>, <see cref="Count"/>, <see cref="Readable"/>, <see cref="Cells"/>,
/// <see cref="NoteRef"/> and <see cref="Bytes"/> are carried structurally so a consumer never parses a note's
/// prose; contract in docs/architecture/read-engine.md.</summary>
public sealed record FieldValue(string Path, bool HasValue, string? Token, string? Note, string? Display = null, ResolvedRef? Link = null,
                                bool Present = true, int? Count = null, bool Readable = true,
                                IReadOnlyList<FieldValue>? Cells = null, string? NoteRef = null, int? Bytes = null,
                                ushort? BytesFormVersion = null);

/// <summary>The resolved identity of a form reference, behind housecarl_resolve and the resolve_names annotation.
/// <see cref="Resolved"/> false means the FormKey is valid but not in the active order, and <see cref="Error"/>
/// carries which of the three causes it is.</summary>
public sealed record ResolvedRef(
    string Token, bool Resolved, string? Type = null, string? EditorId = null,
    string? Name = null, string? Winner = null, string? Error = null);

/// <summary>A located record read out as structured fields: identity plus the requested (or all modeled) reads.</summary>
public sealed record RecordFields(string Type, string FormKey, string? EditorId, IReadOnlyList<FieldValue> Fields);

/// <summary>The reflection-driven READ surface, symmetric partner to <see cref="WriteEngine"/>: a record's modeled
/// field OUT to a token that is the faithful inverse of Coerce, per PLUGIN, navigating the write engine's own
/// walk. Contracts and pins in docs/architecture/read-engine.md.</summary>
public static class ReadEngine
{
    /// <summary>The outcome of reading one leaf: a round-trippable <see cref="Token"/>, else the
    /// <see cref="Note"/> saying why there is none. <see cref="Flags"/> is additive metadata for a
    /// <c>[Flags]</c> enum leaf, so <c>where=</c> can bit-test; the token is unchanged.</summary>
    internal readonly record struct LeafRead(bool HasValue, string Token, string? Note, FlagBits? Flags = null, int? ContainerCount = null,
                                             bool Present = true, bool Readable = true, int? ByteLength = null)
    {
        public static LeafRead Value(string token) => new(true, token, null);
        public static LeafRead FlagsValue(string token, FlagBits bits) => new(true, token, null, bits);
        /// <summary>An opaque BLOB leaf: the hex token plus its byte length — the one family houseCARL renders
        /// without ever parsing, which a verify must not call clean.</summary>
        public static LeafRead Bytes(string token, int length) => new(true, token, null, null, null, ByteLength: length);
        /// <summary>NOTHING is there. The ONE no-value shape that is not <see cref="Container"/>.</summary>
        public static LeafRead None(string note) => new(false, "", note, null, null, Present: false);

        /// <summary>The read FAILED. Absent-looking, but not evidence of ABSENCE.</summary>
        public static LeafRead Unreadable(string note) => new(false, "", note, null, null, Present: false, Readable: false);
    /// <summary>A no-value CONTAINER/substruct summary carrying its element <paramref name="count"/>: null for a
    /// substruct, a number for a list/dict (0 = present-but-EMPTY), for the presence predicate.</summary>
        public static LeafRead Container(string note, int? count) => new(false, "", note, null, count);
        public override string ToString() => HasValue ? Token : Note ?? "(none)";
    }

    /// <summary>The bit-test view of a <c>[Flags]</c> enum leaf — the bit pattern plus the enum type.</summary>
    internal readonly record struct FlagBits(ulong Bits, Type EnumType);

    /// <summary>A modeled leaf that exists but holds no value. Public because a render must tell an ABSENT optional
    /// from every other no-value leaf without matching prose.</summary>
    public const string AbsentNote = "(absent)";

    /// <summary>A FormLink carrying no target: a NON-nullable link holding FormID zero, or a NULLABLE link whose
    /// subrecord is ABSENT. Not round-trippable, so a note the conflict diff reads as "no value here".</summary>
    internal const string NullLinkNote = "(null link)";

    /// <summary>A NULLABLE FormLink whose subrecord is PRESENT and carries FormID zero — an INFO's PNAM "I am
    /// first" marker, told apart from an ABSENT nullable link by <c>FormKeyNullable</c>.</summary>
    internal const string PresentNullLinkNote = "(null link, subrecord present)";

    /// <summary>A present <c>TranslatedString</c> whose <c>.String</c> resolves to null. A no-value NOTE, never a
    /// blank token a value predicate would count as a non-match.</summary>
    internal const string UnresolvedStringNote = "(unresolved localized string)";

    // `read` MODE — resolve a record in one plugin and emit its fields; with no --path, a one-level dump.
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

        var sourceMod = SkyrimMod.CreateFromBinaryOverlay(source, SkyrimRelease.SkyrimSE, PluginTextEncoding.ReadFor(source));
        Type? iface = type is null ? null : typeof(SkyrimMod).Assembly.GetType("Mutagen.Bethesda.Skyrim.I" + type + "Getter");
        if (type is not null && iface is null) { Console.Error.WriteLine($"error: unknown record type '{type}'"); return 1; }
        FormKey? wantFk = null;
        if (formkeyRaw is not null) { try { wantFk = FormKey.Factory(formkeyRaw); } catch (Exception ex) { Console.Error.WriteLine($"error: bad --formkey '{formkeyRaw}': {ex.Message}"); return 1; } }

        var target = sourceMod.EnumerateMajorRecords()
            .FirstOrDefault(r => (iface is null || iface.IsInstanceOfType(r))
                && (wantFk is { } fk ? r.FormKey == fk : string.Equals(r.EditorID, editorid, StringComparison.OrdinalIgnoreCase)));
        if (target is null) { Console.Error.WriteLine($"error: not found in {Path.GetFileName(source)}"); return 1; }

        var typeName = RecordNaming.StripGetterInterface(WriteEngine.PrimaryGetter(target.GetType())?.Name ?? "I?Getter");
        Console.WriteLine($"{typeName}  {FormIdToken.Of(target.FormKey)}  ({target.EditorID ?? "<no editorid>"})");

        // --depth N (default 1) routes through the SAME ReadFields the MCP read tools call.
        var depth = int.TryParse(f.GetValueOrDefault("depth"), out var dN) && dN > 0 ? dN : 1;
        var rf = ReadFields(target, paths.Count > 0 ? paths : null, depth);
        foreach (var fv in rf.Fields)
            Console.WriteLine($"  {fv.Path} = {(fv.HasValue ? fv.Token : fv.Note)}{(fv.Display is null ? "" : $"   ({fv.Display})")}");
        return 0;
    }

    /// <summary>The depth-1 container hint. It names <c>depth=2</c>, so a surface that refuses depth passes its
    /// own <c>containerHint</c>, or null to suppress it.</summary>
    public const string DepthExpandHint = " — pass depth=2 to expand";

    /// <summary>Read a located record's fields as round-trippable tokens — the structured entry the MCP server
    /// consumes, per-leaf fault isolated.</summary>
    /// <param name="depths">One depth per entry of <paramref name="paths"/> when they must differ; every path
    /// spends the same expansion budget.</param>
    public static RecordFields ReadFields(IMajorRecordGetter record, IReadOnlyList<string>? paths = null, int depth = 1,
                                          string? containerHint = DepthExpandHint,
                                          Func<IMajorRecordGetter, (IMajorRecordGetter? Parent, string? Why)>? parentOf = null,
                                          IReadOnlyList<int>? depths = null)
    {
        var typeName = RecordNaming.StripGetterInterface(WriteEngine.PrimaryGetter(record.GetType())?.Name ?? "I?Getter");
        var targets = paths is { Count: > 0 } ? (IEnumerable<string>)paths : ModeledFieldNames(typeName, record.GetType());
        var fields = new List<FieldValue>();
        if (depth <= 1)
        {
            // depth 1 (default) — the one-level read the round-trip oracle drives. Expansion is a separate branch.
            foreach (var p in targets)
            {
                var seg = p.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var (on, tail, hopNote) = HopToParent(record, seg, parentOf);
                if (hopNote is not null) { fields.Add(new FieldValue(p, false, null, hopNote, null, Present: false, Count: null, Readable: false)); continue; }
                var r = ReadLeaf(on, tail);
                string? note = r.HasValue ? null : r.Note;
                // An UNEXPANDED container leaf self-documents the lever that opens it; the leading-'[' test targets
                // exactly the container/substruct summaries, since no-value NOTES are parenthesized.
                if (note is { Length: > 0 } && note[0] == '[' && !string.IsNullOrEmpty(containerHint)) note += containerHint;
                fields.Add(new FieldValue(p, r.HasValue, r.HasValue ? r.Token : null, note, FlagDisplay(r),
                                          Present: r.Present, Count: r.ContainerCount, Readable: r.Readable,
                                          Bytes: r.ByteLength));
                // Annotated off ON, the record the leaf was actually read on — a '*parent' hop rebinds it.
                AnnotateOpaqueBytes(fields, fields.Count - 1, on.FormVersion);
            }
        }
        else
        {
            int budget = MaxExpandNodes;
            // Per-path depths apply only when they line up with the paths the caller named.
            var perPath = paths is { Count: > 0 } && depths is not null && depths.Count == paths.Count ? depths : null;
            int at = 0;
            foreach (var p in targets)
            {
                int d = perPath is not null ? perPath[at] : depth;
                at++;
                var seg = p.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var (on, tail, hopNote) = HopToParent(record, seg, parentOf);
                if (hopNote is not null) { fields.Add(new FieldValue(p, false, null, hopNote, null, Present: false, Count: null, Readable: false)); continue; }
                int from = fields.Count;
                EmitWithDepth(on, string.Join(".", tail), d, fields, ref budget, p);
                // Same rule as the depth-1 branch, over the lines THIS path emitted, on the record the walk ran on.
                AnnotateOpaqueBytes(fields, from, on.FormVersion);
            }
        }
        return new RecordFields(typeName, FormIdToken.Of(record.FormKey), record.EditorID, fields);
    }

    /// <summary>Hang the opaque-blob annotation on every byte-slice leaf from <paramref name="from"/> onward,
    /// per PATH with that path's own owning record, because '*parent' rebinds it.</summary>
    static void AnnotateOpaqueBytes(List<FieldValue> fields, int from, ushort? formVersion)
    {
        for (int i = from; i < fields.Count; i++)
            if (fields[i] is { Bytes: int n, Display: null })
                fields[i] = fields[i] with { Display = BytesDisplay(n, formVersion), BytesFormVersion = formVersion };
    }

    /// <summary>The blob annotation in its SHORT form, for a render with no room for the sentence.</summary>
    public static string BytesShortDisplay(int length, ushort? formVersion) =>
        "[opaque " + length + "B" + (formVersion is { } fv ? " @FV" + fv : "") + "]";

    /// <summary>The DISPLAY-ONLY annotation on an opaque blob leaf: how many bytes, and the FormVersion of the
    /// record they were read off — what decides whether the bytes suit the record carrying them.</summary>
    public static string BytesDisplay(int length, ushort? formVersion) =>
        "opaque bytes, " + length + " byte(s) — layout follows this record's "
        + (formVersion is { } fv ? "FormVersion " + fv : "FormVersion, which this record does not carry")
        + "; not parsed";

    /// <summary>The <c>*parent</c> containment step on a read path, off Mutagen's own containment walk captured at
    /// index build; a note instead when the step is misspelled, has no parent, or this read carries no index.</summary>
    static (IMajorRecordGetter On, string[] Tail, string? Note) HopToParent(
        IMajorRecordGetter record, string[] segs,
        Func<IMajorRecordGetter, (IMajorRecordGetter? Parent, string? Why)>? parentOf)
    {
        var (hops, gerr) = ContainmentIndex.SplitHops(segs, string.Join(".", segs));
        if (gerr is not null) return (record, segs, $"({gerr})");
        if (hops == 0) return (record, segs, null);
        if (parentOf is null)
            return (record, segs, $"('{ContainmentIndex.ParentToken}' needs the load-order index, which this read does not carry — read the record through the load order instead)");
        for (int i = 0; i < hops; i++)
        {
            var (parent, why) = parentOf(record);
            if (parent is null) return (record, segs, $"({why ?? "no record contains this one"})");
            record = parent;
        }
        return (record, segs[hops..], null);
    }


    // THE READ PRIMITIVE — navigate a path read-only, emit the leaf token.

    /// <summary>The opening of the note a getter throw emits; separating faults tests <see cref="IsNoSuchFieldNote"/>.</summary>
    public const string UnreadablePrefix = "(unreadable: ";

    /// <summary>The ONE spelling of a read-fault note, so the sentence cannot drift between the walks that emit it.</summary>
    static string UnreadableNote(string reason) => $"{UnreadablePrefix}{reason})";

    /// <summary>The opening of the NO-SUCH-FIELD note — the one <c>Readable=false</c> answer that is knowledge
    /// about the record rather than a fault, so the conflict diff still compares it.</summary>
    public const string NoFieldPrefix = "(no field ";

    /// <summary>Is this note the no-such-field answer, as opposed to a read fault?</summary>
    public static bool IsNoSuchFieldNote(string? note) =>
        note is not null && note.StartsWith(NoFieldPrefix, StringComparison.Ordinal);

    /// <summary>The reason an unreadable note reports — the INNER exception's message, since reflection wraps a
    /// getter's throw in a <see cref="TargetInvocationException"/> naming nothing a caller can act on.</summary>
    static string Reason(Exception ex) => (ex as TargetInvocationException)?.InnerException?.Message ?? ex.Message;

    /// <summary>Read one leaf path off a located record and return its token, or a sentinel — the write engine's
    /// path walk, READ-ONLY and per-leaf fault isolated.</summary>
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
        catch (Exception ex) { return LeafRead.Unreadable(UnreadableNote(Reason(ex))); }
    }

    /// <summary>The FormKeys on a record's <c>Keywords</c> list — the ONE keyword walk. An ABSENT (null) list
    /// honestly reads as EMPTY; null is reserved for "no such property / not a formlink list".</summary>
    public static IReadOnlyList<FormKey>? KeywordKeys(object record)
    {
        var p = WriteEngine.ResolveProperty(record.GetType(), "Keywords");
        if (p is null) return null;
        if (p.GetValue(record) is not System.Collections.IEnumerable list) return new List<FormKey>();   // absent list reads as empty
        return FormLinkKeys(list);
    }

    /// <summary>The FormKeys of one formlink-list value; null the moment an element is not a formlink.</summary>
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

    /// <summary>Collect every FormKey linked UNDER one field path — the <c>-&gt;</c> link-step's left side.
    /// Answers (null, note) when the path reaches no link-bearing value; a present-but-empty list answers EMPTY.</summary>
    public static (List<FormKey>? Links, string? Note) CollectLinksAt(object record, string[] path)
    {
        try
        {
            var nav = NavigateValue(record, path);
            if (!nav.ok) return (null, nav.note);
            if (nav.val is null) return (null, AbsentNote);
            return LinksIn(nav.val, string.Join(".", path));
        }
        catch (Exception ex) { return (null, UnreadableNote(Reason(ex))); }
    }

    /// <summary>The link-shape half of <see cref="CollectLinksAt"/>, over a value already navigated to.</summary>
    internal static (List<FormKey>? Links, string? Note) LinksIn(object value, string display)
    {
        try
        {
            var keys = new List<FormKey>();
            var seen = new HashSet<FormKey>();
            void Add(FormKey fk) { if (!fk.IsNull && seen.Add(fk)) keys.Add(fk); }
            switch (value)
            {
                case IFormLinkGetter link:
                    Add(link.FormKey);
                    break;
                case string:
                    return (null, $"(no links: '{display}' is a string, not a link-bearing field)");
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
                    return (null, $"(no links: '{display}' is not a link-bearing field)");
            }
            return (keys, null);
        }
        catch (Exception ex) { return (null, UnreadableNote(Reason(ex))); }
    }

    /// <summary>Navigate a path READ-ONLY to its live value — the quantified step's fan-out source.</summary>
    internal static (bool Ok, object? Value, Type Declared, object Parent, string? Note) NavigateTo(object record, string[] path)
    {
        var nav = NavigateValue(record, path);
        return (nav.ok, nav.val, nav.type, nav.parent, nav.note);
    }

    /// <summary>A "no such field" note that, off a collection, points the caller at bracket indexing and says WHICH
    /// of the two dead-end causes this is, off the generated schema.</summary>
    static string NoFieldNote(object owner, string segName, string? precedingField, string[]? trailing = null)
    {
        bool ownerIsCollection = owner is System.Collections.IDictionary
            || (owner is System.Collections.IEnumerable && owner is not string);
        if (ownerIsCollection)
        {
            var pf = precedingField ?? "<field>";
            return $"{NoFieldPrefix}'{segName}': '{pf}' is a list/dict — {ListHopRemedy(owner, segName, pf, trailing)})";
        }

        var typeName = RecordNaming.StripGetterInterface(RecordNaming.StripOverlay(owner.GetType().Name));
        // No corpus (not built / unparseable) ⇒ the bare note. Saying less is not saying something wrong.
        if (ModeledFieldIndex.Diagnose(typeName, segName) is not { } v) return $"{NoFieldPrefix}{segName})";

        if (v.OnOwner)
            return $"{NoFieldPrefix}{segName}: {typeName} declares '{segName}' but the read walk cannot resolve it)";

        // The owner's own spelling settles it: DATA is real on DialogResponses and still a typo at a Weapon.
        if (v.NearIsCaseSlip)
            return $"{NoFieldPrefix}{segName}: a mistyped name — field names are case-sensitive; " +
                   $"did you mean '{v.Near}'?)";

        if (v.ModeledOn.Count > 0)
        {
            var shown = string.Join(", ", v.ModeledOn.Take(3)) + (v.ModeledOn.Count > 3 ? ", …" : "");
            // A name modeled elsewhere can still be THIS record's typo, so the owner's own near field outranks it.
            var lead = v.Near is null ? "not a mistyped name — Mutagen models" : "Mutagen models";
            var alt = v.Near is { } nm ? $"; did you mean '{nm}'?" : "";
            return $"{NoFieldPrefix}{segName}: {lead} '{segName}' on " +
                   $"{v.ModeledOn.Count:N0} other type(s) ({shown}), just not on {typeName}{alt})";
        }

        var near = v.Near is { } n ? $"; did you mean '{n}'?" : $"; check the name against {typeName}'s schema";
        return $"{NoFieldPrefix}{segName}: a mistyped name — Mutagen models no field '{segName}' on any type{near})";
    }

    /// <summary>What to actually DO about a path that dotted THROUGH a list/dict — checked against the element
    /// type, never asserted, because a missing bracket and a wrong leaf name need opposite next moves.</summary>
    internal static string ListHopRemedy(object owner, string segName, string pf, string[]? trailing = null)
    {
        var rest = trailing is { Length: > 0 } ? "." + string.Join(".", trailing) : "";

        // A numeric segment is the '.0'-vs-'[0]' confusion: it is an INDEX, and no element type check applies.
        if (segName.Length > 0 && segName.All(char.IsDigit))
            return $"index an element with brackets, e.g. '{pf}[{segName}]{rest}', not '{pf}.{segName}{rest}'";

        var et = ElementType(owner);
        if (et is null)
            return $"index an element with brackets, e.g. '{pf}[0].{segName}{rest}', not '{pf}.{segName}{rest}'";

        var v = HopVerdictOf(et, segName);
        if (v.OnElement)
            return $"index an element with brackets: '{pf}[0].{segName}{rest}', not '{pf}.{segName}{rest}'";

        return v.Near is { } near
            ? $"'{segName}' is not a field on its element type {v.TypeName} — did you mean '{pf}[0].{near}{rest}'?"
            : $"'{segName}' is not a field on its element type {v.TypeName}; index an element with brackets " +
              $"('{pf}[0].<field>') and name a field {v.TypeName} has";
    }

    /// <summary>The element-type half of a list-hop diagnosis.</summary>
    readonly record struct HopVerdict(bool OnElement, string TypeName, string? Near);

    /// <summary>Memoised on (element type, segment): a scan hits the same dead-end once per scanned record.</summary>
    static readonly System.Collections.Concurrent.ConcurrentDictionary<(Type, string), HopVerdict> HopVerdicts = new();

    /// <summary>How many verdicts have been computed — pinned by
    /// <c>RecordsRemedyRepairTests.AScanComputesOneListHopRemedyForTheWholeScan</c>.</summary>
    internal static int ListHopVerdictComputations;

    static HopVerdict HopVerdictOf(Type et, string segName) =>
        HopVerdicts.GetOrAdd((et, segName), key =>
        {
            Interlocked.Increment(ref ListHopVerdictComputations);
            var (t, seg) = key;
            var etName = RecordNaming.StripOverlay(t.Name);
            if (WriteEngine.ResolveProperty(t, seg) is not null) return new HopVerdict(true, etName, null);
            var near = PluginNameSuggest.Nearest(seg, ElementFieldNames(t), 1);
            return new HopVerdict(false, etName, near.Count > 0 ? near[0] : null);
        });

    /// <summary>The element type of a collection; null when the collection is untyped and empty.</summary>
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

    /// <summary>Every public instance property name on an element type — the nearest-name candidate set.</summary>
    static IEnumerable<string> ElementFieldNames(Type et)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in new[] { et }.Concat(et.GetInterfaces()))
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                if (p.GetIndexParameters().Length == 0 && seen.Add(p.Name)) yield return p.Name;
    }

    // DESCENDABLE READS (depth>=2) — enumerate container CONTENTS; the depth-1 ReadLeaf is untouched.

    /// <summary>Max FieldValue lines one descendable read will GENERATE; over it, ONE truncation note is emitted.</summary>
    internal const int MaxExpandNodes = 2000;

    static readonly string[] IdentityFieldNames = { "Name", "EditorID", "Title" };

    /// <summary>Emit one target path, expanding contents up to <paramref name="depth"/> levels under the same
    /// per-field fault isolation depth-1 gives. <paramref name="display"/> is how the rows spell the path.</summary>
    static void EmitWithDepth(object record, string path, int depth, List<FieldValue> sink, ref int budget, string? display = null)
    {
        var shown = display ?? path;
        try
        {
            var seg = path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var nav = NavigateValue(record, seg);
            if (!nav.ok) { Emit(sink, ref budget, new FieldValue(shown, false, null, nav.note, Present: false, Readable: nav.readable)); return; }
            Expand(nav.val, nav.type, nav.parent, shown, depth, sink, ref budget);
        }
        catch (Exception ex) { Emit(sink, ref budget, Fault(shown, ex)); }
    }

    /// <summary>The ONE unreadable line the deep walk emits — the sentence and flags the depth-1 read carries.</summary>
    static FieldValue Fault(string path, string reason) =>
        new(path, false, null, UnreadableNote(reason), Present: false, Readable: false);

    static FieldValue Fault(string path, Exception ex) => Fault(path, Reason(ex));

    /// <summary>Recurse into ONE child under the same isolation its getter has: a throw beneath it names THAT
    /// child's path and the sibling walk carries on.</summary>
    static void ExpandChild(object? val, Type declaredType, object parent, string childPath, int depth,
                            List<FieldValue> sink, ref int budget)
    {
        try { Expand(val, declaredType, parent, childPath, depth, sink, ref budget); }
        catch (Exception ex) { Emit(sink, ref budget, Fault(childPath, ex)); }
    }

    /// <summary>Recursively emit <paramref name="val"/>: a value leaf to its token, a link to its note, a
    /// container to a summary and one child line per element. A child that cannot be read is never skipped.</summary>
    static void Expand(object? val, Type declaredType, object parent, string path, int depth, List<FieldValue> sink, ref int budget)
    {
        if (budget < 0) return;
        var leaf = EmitToken(val, declaredType, parent);
        if (leaf.HasValue) { Emit(sink, ref budget, new FieldValue(path, true, leaf.Token, null, FlagDisplay(leaf), Bytes: leaf.ByteLength)); return; }
        if (val is null) { Emit(sink, ref budget, new FieldValue(path, false, null, leaf.Note, Present: false)); return; }
        // a link (incl. a null FormKey, or an FLOI) is a note, not an openable container; both flags travel with it.
        if (val is IFormLinkGetter || WriteEngine.IsFormLinkOrIndex(Nullable.GetUnderlyingType(declaredType) ?? declaredType))
        { Emit(sink, ref budget, new FieldValue(path, false, null, leaf.Note, Present: leaf.Present, Readable: leaf.Readable)); return; }

        // Classify dict-vs-list the SAME way navigation does, and BEFORE the summary line, so the summary can carry
        // the dict marker FieldsDiff keeps numeric-KEYED dicts out of positional comparison by.
        bool isDict = WriteEngine.ClosedInterface(val.GetType(), typeof(IDictionary<,>)) is not null
                   || WriteEngine.ClosedInterface(val.GetType(), typeof(IReadOnlyDictionary<,>)) is not null;

        // a container or substruct — summarise, then maybe open it. NoteRef carries the FormID the summary spelled.
        int? deepCount = val is System.Collections.IEnumerable de and not string ? CountOf(de) : null;
        var summary = ElementSummary(val, isDict, out var summaryRef);
        if (!Emit(sink, ref budget, new FieldValue(path, false, null, summary, Present: true, Count: deepCount, NoteRef: summaryRef))) return;

        // Two POLYMORPHIC-ARM families — a VMAD script property and a Conditions[].Data arm — surface their VALUE
        // ONE bounded level deeper even at the depth floor, for parity with the write surface.
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
                ExpandChild(ev, ev?.GetType() ?? typeof(object), val, $"{path}[{key}]", childDepth, sink, ref budget);
            }
        }
        else if (WriteEngine.GenderedInterface(val.GetType()) is not null)
        {
                // Gendered pair ([0]=male, [1]=female), via the SAME index-to-arm mapping navigation uses.
            for (int g = 0; g < WriteEngine.GenderedArmNames.Length; g++)
            {
                if (budget < 0) return;
                var armName = WriteEngine.GenderedArmNames[g];
                var armProp = WriteEngine.ResolveProperty(val.GetType(), armName);
                if (armProp is null)
                {
                    Emit(sink, ref budget, Fault($"{path}[{g}]", $"gendered type {RecordNaming.StripOverlay(val.GetType().Name)} has no '{armName}' arm"));
                    continue;
                }
                object? arm;
                try { arm = armProp.GetValue(val); }
                catch (Exception ex) { Emit(sink, ref budget, Fault($"{path}[{g}]", ex)); continue; }
                ExpandChild(arm, armProp.PropertyType, val, $"{path}[{g}]", childDepth, sink, ref budget);
            }
        }
        else if (val is System.Collections.IEnumerable seq and not string)
        {
                // Plain enumeration. A binary overlay builds an element only when reached, so an element Mutagen
                // cannot parse throws out of the ENUMERATOR; on that throw the REST is stepped by index instead.
            var en = seq.GetEnumerator();
            try
            {
                int i = 0;
                while (true)
                {
                    if (budget < 0) return;
                    object? item;
                    try { if (!en.MoveNext()) return; item = en.Current; }
                    catch (Exception ex)
                    {
                        Emit(sink, ref budget, ElementFault(parent, path, i, ex));
                        ExpandRestByIndex(val, parent, path, i + 1, childDepth, sink, ref budget);
                        return;
                    }
                    ExpandChild(item, item?.GetType() ?? typeof(object), val, $"{path}[{i}]", childDepth, sink, ref budget);
                    i++;
                }
            }
            finally { (en as IDisposable)?.Dispose(); }
        }
        else
        {
                // substruct — open its modeled (Loqui-filtered) fields by reflection.
                // GATE — the expansion boundary is the modeled corpus (cornerstone): a value reaching here that
                // is NOT modeled is .NET plumbing, and a naive recurse walks the whole assembly's metadata.
            if (!IsModeledContent(val.GetType())) return;
            foreach (var fname in ReflectedFieldNames(val.GetType()))
            {
                if (budget < 0) return;
                var prop = WriteEngine.ResolveProperty(val.GetType(), fname);
                // The name came off this type's own reflection, so a resolve miss or a getter throw is a FAULT.
                if (prop is null)
                {
                    Emit(sink, ref budget, Fault($"{path}.{fname}",
                        $"{RecordNaming.StripOverlay(val.GetType().Name)} declares '{fname}' but the read walk cannot resolve it"));
                    continue;
                }
                object? fv;
                try { fv = prop.GetValue(val); }
                catch (Exception ex) { Emit(sink, ref budget, Fault($"{path}.{fname}", ex)); continue; }
                ExpandChild(fv, prop.PropertyType, val, $"{path}.{fname}", childDepth, sink, ref budget);
            }
        }
    }

    /// <summary>Emit the elements from <paramref name="from"/> onward one at a time, each isolated — taken only
    /// after an enumeration already threw.</summary>
    static void ExpandRestByIndex(object val, object parent, string listPath, int from, int childDepth,
                                  List<FieldValue> sink, ref int budget)
    {
        if (IndexedElements(val) is not { } indexed)
        {
            Emit(sink, ref budget, new FieldValue($"{listPath}[{from}…]", false, null,
                UnreadableNote($"the element(s) from {from} on were not read: {RecordNaming.StripOverlay(val.GetType().Name)} "
                               + "can only be enumerated, and the enumeration stopped at the fault above"),
                Present: true, Readable: false));
            return;
        }
        for (int i = from; i < indexed.Count; i++)
        {
            if (budget < 0) return;
            object? item;
            try { item = indexed.At(i); }
            catch (Exception ex) { Emit(sink, ref budget, ElementFault(parent, listPath, i, ex)); continue; }
            ExpandChild(item, item?.GetType() ?? typeof(object), val, $"{listPath}[{i}]", childDepth, sink, ref budget);
        }
    }

    /// <summary>The line for a list element whose own getter threw: the read fault note, or for an element of a
    /// PERK's EFFECTS list the lenient decode's marker.</summary>
    static FieldValue ElementFault(object parent, string listPath, int index, Exception ex)
    {
        var elementPath = $"{listPath}[{index}]";
        if (!IsEffectsList(listPath)) return Fault(elementPath, ex);
        return PerkEffectDecode.EffectNote(parent, index, Reason(ex)) is { } marker
            ? new FieldValue(elementPath, false, null, marker, Present: true, Readable: false)
            : Fault(elementPath, ex);
    }

    /// <summary>Is this read path the record's own <c>Effects</c> list — the last dotted step, with no bracket?</summary>
    static bool IsEffectsList(string listPath)
    {
        int dot = listPath.LastIndexOf('.');
        var last = dot < 0 ? listPath : listPath[(dot + 1)..];
        return string.Equals(last, "Effects", StringComparison.Ordinal);
    }

    /// <summary>A collection's element count and its indexer when it has one, cached per runtime type.</summary>
    static (int Count, Func<int, object?> At)? IndexedElements(object val)
    {
        if (_itemAccessors.GetOrAdd(val.GetType(), ItemAccessor) is not { } item) return null;
        if (_countAccessors.GetOrAdd(val.GetType(), CountAccessor) is not { } count) return null;
        int n;
        try { n = count(val); }
        catch { return null; }                                   // a length we cannot read is not a list we can step
        return (n, i => item(val, i));
    }

    static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, Func<object, int, object?>?> _itemAccessors = new();

    /// <summary>The <c>this[int]</c> accessor of a list type, or null when it has none (a set, a dictionary).</summary>
    static Func<object, int, object?>? ItemAccessor(Type t)
    {
        foreach (var iface in t.GetInterfaces())
        {
            if (!iface.IsGenericType || iface.GetGenericTypeDefinition() != typeof(IReadOnlyList<>)) continue;
            if (iface.GetProperty("Item")?.GetGetMethod() is { } getter)
                return (o, i) => getter.Invoke(o, new object[] { i });
        }
        if (typeof(System.Collections.IList).IsAssignableFrom(t)) return (o, i) => ((System.Collections.IList)o)[i];
        return null;
    }

    /// <summary>Append a line, decrementing the generation budget; at exhaustion emit ONE truncation note and stop.</summary>
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

    /// <summary>Navigate a path READ-ONLY to its target, yielding the live value object (+ declared type + owning
    /// parent) or a miss note; the same walk as <see cref="ReadLeaf"/>, fault-isolated.</summary>
    static (bool ok, object? val, Type type, object parent, string? note, bool readable) NavigateValue(object record, string[] path)
    {
        try
        {
            object current = record;
            for (int i = 0; i < path.Length - 1; i++)
            {
                var (segName, segKey) = WriteEngine.ParseSegment(path[i]);
                var p = WriteEngine.ResolveProperty(current.GetType(), segName);
                if (p is null) return (false, null, typeof(object), current,
                    NoFieldNote(current, segName, i > 0 ? WriteEngine.ParseSegment(path[i - 1]).name : null, path[(i + 1)..]), false);
                var next = segKey is null ? p.GetValue(current) : WriteEngine.StepIntoElement(current, p, segName, segKey);
                if (next is null) return (false, null, typeof(object), record, AbsentNote, true);
                current = next;
            }
            var (leafName, leafKey) = WriteEngine.ParseSegment(path[^1]);
            var leaf = WriteEngine.ResolveProperty(current.GetType(), leafName);
            if (leaf is null) return (false, null, typeof(object), current,
                NoFieldNote(current, leafName, path.Length >= 2 ? WriteEngine.ParseSegment(path[^2]).name : null), false);
            if (leafKey is not null)
            {
                var elem = WriteEngine.StepIntoElement(current, leaf, leafName, leafKey);
                return (true, elem, elem.GetType(), current, null, true);
            }
            return (true, leaf.GetValue(current), leaf.PropertyType, current, null, true);
        }
        catch (Exception ex) { return (false, null, typeof(object), record, UnreadableNote(Reason(ex)), false); }
    }

    /// <summary>Best-effort COMPACT identity of the element a list/dict verb just acted on. NEVER throws.</summary>
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
                // Insert says INSERTED, not set: the count moved and every element at or after [key] shifted.
                "InsertAtIndex" => key is not null ? $"now {count} item(s), inserted [{key}]" : $"now {count} item(s)",
                "Remove"     => key is not null ? $"now {count} item(s), removed [{key}]" : $"now {count} item(s) (-1)",
                _            => $"now {count} item(s)",
            };
        }
        catch { return null; }
    }

    /// <summary>The list-<c>Add</c> "what landed" line: the new element for a single append, the whole appended
    /// run for a batch, with <paramref name="added"/> clamped to the live count.</summary>
    static string AddLanded(object record, int count, object? last, int added)
    {
        if (last is null) return $"now {count} item(s)";
        int n = Math.Clamp(added, 1, count);
        return n <= 1
            ? $"now {count} (+1), new [{count - 1}] = {ElementId(last, record)}"
            : $"now {count} (+{n}), new [{count - n}..{count - 1}]";
    }

    /// <summary>The compact identity of ONE element: its own round-trip token, else <see cref="ElementSummary"/>.</summary>
    static string ElementId(object elem, object parent)
    {
        var lr = EmitToken(elem, elem.GetType(), parent);
        return lr.HasValue ? lr.Token : ElementSummary(elem);
    }

    /// <summary>A compact summary for a container/struct value: the count form, else <c>[TypeName]</c> plus a
    /// representative identity field where present.</summary>
    static string ElementSummary(object val, bool isDict = false) => ElementSummary(val, isDict, out _);

    /// <summary>Overload also yielding the form reference the summary RENDERED — the identity FIELD's target.</summary>
    static string ElementSummary(object val, bool isDict, out string? refToken)
    {
        refToken = null;
        if (val is System.Collections.IEnumerable && val is not string) return SummariseContainer(val, isDict);
        var t = val.GetType();
        var typeName = RecordNaming.StripGetterInterface(RecordNaming.StripOverlay(t.Name));
        // An owned child RECORD element leads with its FormKey, checked BEFORE the Name/EditorID/Title scan.
        if (val is IMajorRecordGetter mr)
            return $"[{typeName} {FormIdToken.Of(mr.FormKey)}{(string.IsNullOrEmpty(mr.EditorID) ? "" : $" editorid={mr.EditorID}")}]";
        foreach (var idName in IdentityFieldNames)
        {
            var p = t.GetProperty(idName, BindingFlags.Public | BindingFlags.Instance);
            if (p is null || p.GetIndexParameters().Length != 0) continue;
            object? iv; try { iv = p.GetValue(val); } catch { continue; }
            var s = iv switch { null => null, string str => str, IFormLinkGetter fl => FormIdToken.Of(fl.FormKey), _ => iv.ToString() };
            if (string.IsNullOrEmpty(s)) continue;
            if (iv is IFormLinkGetter) refToken = s;
            return $"[{typeName}] {idName}={s}";
        }
        // A struct carrying EXACTLY ONE FormLink field has that link as its identity; 2+ are ambiguous.
        if (LoneFormLinkIdentity(val, t, out refToken) is { } linkId) return $"[{typeName}] {linkId}";
        return $"[{typeName}]";
    }

    /// <summary>The <c>Field=FormKey</c> identity of a struct element with EXACTLY ONE FormLink property and no
    /// Name/EditorID/Title identity; null when it has none or MORE THAN ONE.</summary>
    static string? LoneFormLinkIdentity(object val, Type t, out string? refToken)
    {
        refToken = null;
        PropertyInfo? only = null;
        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length != 0) continue;
            if (!typeof(IFormLinkGetter).IsAssignableFrom(p.PropertyType)) continue;
            if (only is not null) return null;   // 2+ FormLink fields — ambiguous, don't guess
            only = p;
        }
        if (only is null) return null;
        try
        {
            if (only.GetValue(val) is not IFormLinkGetter fl) return null;
            refToken = FormIdToken.Of(fl.FormKey);
            return $"{only.Name}={refToken}";
        }
        catch { return null; }
    }

    /// <summary>Modeled field names off a runtime type, for substructs. Best-effort, display-only.</summary>
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

    /// <summary>Drop Loqui/Mutagen plumbing members that are not real data fields.</summary>
    static bool IsInfrastructure(PropertyInfo p)
    {
        if (p.Name is "BinaryWriteTranslator" or "Registration" or "StaticRegistration"
            or "CommonInstance" or "CommonSetterInstance" or "CommonSetterTranslationInstance") return true;
        var tn = p.PropertyType.Name;
        return tn.Contains("BinaryWriteTranslat", StringComparison.Ordinal)
            || tn.EndsWith("BinaryTranslation", StringComparison.Ordinal);
    }

    /// <summary>True if <paramref name="t"/> is MODELED record content the depth walker may descend into — a
    /// Mutagen or Noggog type. Everything else is .NET plumbing: the modeled corpus IS the boundary.</summary>
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

    // EMIT — the inverse of WriteEngine.Coerce, in Coerce's own Try* branch order; FLOI is checked first.

    /// <param name="parent">The leaf's owning object — needed only for a FormLinkOrIndex.</param>
    internal static LeafRead EmitToken(object? val, Type declaredType, object parent)
    {
        if (val is null) return LeafRead.None(AbsentNote);
        var u = Nullable.GetUnderlyingType(declaredType) ?? declaredType;

        // FLOI (condition targets) — a token ClassifyFloiValue re-accepts, inferred from the parent arm.
        if (WriteEngine.IsFormLinkOrIndex(u)) return EmitFloi(val, parent);

        // primitive (inverse of TryPrimitive)
        if (TryEmitPrimitive(val, out var prim)) return LeafRead.Value(prim);
        // enum (inverse of TryEnum). A [Flags] enum ALSO carries the bit pattern and type, for the predicate.
        if (u.IsEnum || val.GetType().IsEnum)
        {
            var token = val.ToString() ?? "";
            var enumType = u.IsEnum ? u : val.GetType();
            if (enumType.IsDefined(typeof(FlagsAttribute), false) && TryEnumBits(val, enumType, out var bits))
                return LeafRead.FlagsValue(token, new FlagBits(bits, enumType));
            return LeafRead.Value(token);
        }
        // formlink (inverse of TryFormLink). A present-but-null link is not round-trippable, so it is a note.
        if (val is IFormLinkGetter fl)
        {
            if (!fl.FormKey.IsNull) return LeafRead.Value(FormIdToken.Of(fl.FormKey));
            // FormKeyNullable is null only when the subrecord is ABSENT; PRESENT with zero means the opposite.
            bool nullable = WriteEngine.ClosedInterface(val.GetType(), typeof(IFormLinkNullableGetter<>)) is not null;
            return LeafRead.None(nullable && fl.FormKeyNullable is not null ? PresentNullLinkNote : NullLinkNote);
        }
        // TranslatedString — the resolved .String. A NULL .String is an UNRESOLVED localized string, surfaced LOUD
        // as no-value, so it must stay ahead of TryEmitValueType.
        if (val.GetType().FullName == "Mutagen.Bethesda.Strings.TranslatedString")
        {
            var s = ReflectString(val, "String");
            return s is null ? LeafRead.None(UnresolvedStringNote) : LeafRead.Value(s);
        }
        // value types (inverse of TryValueType). A byte-slice blob emits the same hex token, marked as opaque.
        if (TryEmitValueType(val, out var vt))
            return IsByteMemorySlice(val.GetType()) ? LeafRead.Bytes(vt, vt.Length / 2) : LeafRead.Value(vt);

        // Not a single-token VALUE leaf: summarise for the display, and carry the element count STRUCTURALLY.
        bool isDict = WriteEngine.ClosedInterface(val.GetType(), typeof(IDictionary<,>)) is not null
                   || WriteEngine.ClosedInterface(val.GetType(), typeof(IReadOnlyDictionary<,>)) is not null;
        var summary = SummariseContainer(val, isDict, out var count);
        return LeafRead.Container(summary, count);
    }

    /// <summary>The unsigned bit pattern of a boxed enum value, read through the declared underlying type.</summary>
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

    /// <summary>Resolve a flag NAME, or a comma-combo, against a <c>[Flags]</c> enum type to its bit pattern.</summary>
    internal static bool TryEnumBitsFromName(Type enumType, string name, out ulong bits)
    {
        bits = 0;
        try { return TryEnumBits(Enum.Parse(enumType, name.Trim(), ignoreCase: true), enumType, out bits); }
        catch { return false; }
    }

    /// <summary>The DISPLAY-ONLY biped-slot decode for a <c>BodyTemplate.FirstPersonFlags</c> leaf (slot = 30 +
    /// bit index), gated to BipedObjectFlag by name.</summary>
    internal static string? FlagSlotDisplay(LeafRead leaf)
    {
        if (!leaf.HasValue || leaf.Flags is not { } fb || fb.EnumType.Name != "BipedObjectFlag") return null;
        var slots = new List<int>();
        for (int i = 0; i < 32; i++) if ((fb.Bits & (1UL << i)) != 0) slots.Add(30 + i);
        if (slots.Count == 0) return null;
        return (slots.Count == 1 ? "slot " : "slots ") + string.Join(", ", slots);
    }

    /// <summary>The DISPLAY-ONLY annotation for a <c>[Flags]</c> enum leaf; the two decodes are exclusive.</summary>
    internal static string? FlagDisplay(LeafRead leaf) => FlagSlotDisplay(leaf) ?? FlagBitsDisplay(leaf);

    /// <summary>The DISPLAY-ONLY decode for a <c>[Flags]</c> enum leaf carrying bits the catalog does NOT name:
    /// the known bits by NAME plus the unnamed remainder as an explicit hex mask.</summary>
    internal static string? FlagBitsDisplay(LeafRead leaf)
    {
        if (!leaf.HasValue || leaf.Flags is not { } fb) return null;
        // Peel the NAMEABLE bits the way .NET's [Flags].ToString() does: greedily apply each named member that is
        // FULLY contained, largest first. ORing every member's bits into one mask would call a bit that exists
        // only inside a combo nameable.
        var members = new List<ulong>();
        foreach (var member in Enum.GetValues(fb.EnumType))
            if (TryEnumBits(member, fb.EnumType, out var mb) && mb != 0) members.Add(mb);
        members.Sort((a, b) => b.CompareTo(a));   // descending (unsigned) — a combo before its constituent bits
        ulong remainder = fb.Bits;
        foreach (var mb in members) if ((remainder & mb) == mb) remainder &= ~mb;
        if (remainder == 0) return null;   // every set bit is nameable — ToString already gave the full name list
        // The nameable bits are a union of whole members, so the remainder is stated as an explicit hex mask.
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
            case FormKey fk: token = FormIdToken.Of(fk); return true;
            case ModKey mk: token = mk.ToString(); return true;
            case RecordType rt: token = rt.ToString(); return true;
        }

        var rt2 = val.GetType();
        var fn = rt2.FullName;


        if (fn == "Noggog.Percent")
        { token = NumericComponentInvariant(val) ?? val.ToString() ?? ""; return true; }

        // Noggog point structs P2*/P3* — components in constructor order ("x,y,z").
        if (rt2.Namespace == "Noggog" && (rt2.Name.StartsWith("P2") || rt2.Name.StartsWith("P3")))
        { token = PointComponents(val); return true; }

        // (ReadOnly)MemorySlice<byte> — raw blob as a hex string.
        if (IsByteMemorySlice(rt2)) { token = Convert.ToHexString(MemorySliceBytes(val)); return true; }

        // AssetLink<T> family — the stored path string, off the ONE predicate write coercion shares.
        if (WriteEngine.IsAssetLinkFamily(rt2))
        { token = ReflectString(val, "GivenPath", "RawPath", "DataRelativePath") ?? val.ToString() ?? ""; return true; }

        return false;
    }

    // -- FLOI (mirror SetFloi / ClassifyFloiValue) -----------------------------
    /// <summary>Emit a condition-target FormLinkOrIndex as the token that re-creates it, never a guessed four bytes.</summary>
    static LeafRead EmitFloi(object val, object parent)
    {
        bool? useAliases = ReflectBool(parent, "UseAliases");
        bool? usePackData = ReflectBool(parent, "UsePackageData");
        if (useAliases is null || usePackData is null)
            return LeafRead.Unreadable($"(floi: parent {parent.GetType().Name} has no UseAliases/UsePackageData discriminator)");

        if (useAliases == false && usePackData == false)
        {
            // Form mode: the overlay's FLOI carries the link in .Link, the accessor the write side reads.
            if (val is IFormLinkGetter fl) return LeafRead.Value(FormIdToken.Of(fl.FormKey));
            if (WriteEngine.ReadFloiFormKey(val) is { } fk) return LeafRead.Value(FormIdToken.Of(fk));
            return LeafRead.Unreadable($"(floi: form mode, null or unreadable FormKey on {val.GetType().Name})");
        }

        // index mode — read the numeric index defensively (FormLinkOrIndex carries it alongside the link).
        var idx = ReflectUInt(val, "Index", "RawIndex", "FormKeyOrIndex");
        if (idx is null) return LeafRead.Unreadable("(floi: index mode, index accessor unresolved — refined in oracle)");
        return LeafRead.Value(useAliases == true ? $"alias {idx}" : $"packdata {idx}");
    }

    // Reflection helpers, read defensively; the round-trip oracle validates which accessor is the faithful inverse.

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

    /// <summary>Emit a single-component numeric value object's underlying number, found BY TYPE.</summary>
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

    /// <summary>Emit a Noggog P2*/P3* point's components in CONSTRUCTOR-PARAMETER order, so the token splits back
    /// into the same ctor args ConstructByCtor consumes.</summary>
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

    // AssetLink-family recognition lives in WriteEngine.IsAssetLinkFamily, shared with write coercion.

    /// <summary>True if <paramref name="t"/> is a VMAD script-property arm, whose direct value members the depth
    /// walker opens one bounded level past the floor — one of TWO exceptions to the depth gate.</summary>
    static bool IsScriptProperty(Type t) => typeof(IScriptPropertyGetter).IsAssignableFrom(t);

    /// <summary>True if <paramref name="t"/> is a polymorphic CONDITION-DATA arm — the other depth-gate exception.</summary>
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

    /// <summary>How many elements a collection holds, WITHOUT building any of them; the accessor is cached.</summary>
    internal static int CountOf(System.Collections.IEnumerable en)
    {
        if (_countAccessors.GetOrAdd(en.GetType(), CountAccessor) is { } count) return count(en);
        int n = 0;
        foreach (var _ in en) n++;
        return n;
    }

    static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, Func<object, int>?> _countAccessors = new();

    /// <summary>The Count property of a collection type, or null when it carries none.</summary>
    static Func<object, int>? CountAccessor(Type t)
    {
        if (typeof(System.Collections.ICollection).IsAssignableFrom(t))
            return o => ((System.Collections.ICollection)o).Count;
        foreach (var iface in t.GetInterfaces())
        {
            if (!iface.IsGenericType) continue;
            var def = iface.GetGenericTypeDefinition();
            if (def != typeof(IReadOnlyCollection<>) && def != typeof(ICollection<>)) continue;
            if (iface.GetProperty("Count")?.GetGetMethod() is { } getter)
                return o => (int)getter.Invoke(o, null)!;
        }
        return null;
    }

    /// <summary>A short, non-round-trippable description of a container leaf. The <c>item(s)</c>/<c>pair(s)</c>
    /// marker is LOAD-BEARING — <c>FieldsDiff</c> splits numeric-keyed dicts out of positional comparison on it.</summary>
    static string SummariseContainer(object val, bool isDict = false) => SummariseContainer(val, isDict, out _);

    /// <summary>Overload that also yields the element <paramref name="count"/>: a number for a list/dict, null for a substruct.</summary>
    static string SummariseContainer(object val, bool isDict, out int? count)
    {
        count = null;
        if (val is System.Collections.IEnumerable en and not string)
        {
            int n = CountOf(en);
            count = n;
            return $"[{(isDict ? "dict" : "list")}: {n} {(isDict ? "pair(s)" : "item(s)")}]";
        }
        // StripOverlay too, matching ElementSummary: an overlay loads WeaponBasicStatsBinaryOverlay for WeaponBasicStats.
        return $"[{RecordNaming.StripGetterInterface(RecordNaming.StripOverlay(val.GetType().Name))}]";
    }

    /// <summary>The modeled field names of one record, for a caller walking it field by field.</summary>
    public static IEnumerable<string> ModeledFieldsOf(IMajorRecordGetter record) =>
        ModeledFieldNames(RecordNaming.StripGetterInterface(WriteEngine.PrimaryGetter(record.GetType())?.Name ?? "I?Getter"),
                          record.GetType());

    /// <summary>The modeled field names for the whole-record dump — the CORPUS, falling back to reflection.</summary>
    static IEnumerable<string> ModeledFieldNames(string typeName, Type recordRuntimeType)
    {
        Corpus? corpus = null;
        try { corpus = CorpusRulebook.LoadCorpus(); } catch { /* corpus not built / unparseable → reflection fallback */ }
        if (corpus is not null && corpus.Types.TryGetValue(typeName, out var schema))
        {
            foreach (var f in schema.Fields) yield return f.Name;
            yield break;
        }

        // Fallback (no corpus): the record's getter interfaces, Loqui-infrastructure filtered.
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
