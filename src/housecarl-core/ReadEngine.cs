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
    /// isolated read fault). The round-trip oracle drives ONLY <see cref="HasValue"/> reads.</summary>
    internal readonly record struct LeafRead(bool HasValue, string Token, string? Note)
    {
        public static LeafRead Value(string token) => new(true, token, null);
        public static LeafRead None(string note) => new(false, "", note);
        public override string ToString() => HasValue ? Token : Note ?? "(none)";
    }

    /// <summary>A modeled leaf that exists but holds no value on this record (absent optional substruct,
    /// empty optional). Distinct from a real token; the oracle skips it — write-proof owns the absent
    /// surface.</summary>
    internal const string AbsentNote = "(absent)";

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

        if (paths.Count > 0)
        {
            foreach (var p in paths)
            {
                var seg = p.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                Console.WriteLine($"  {p} = {ReadLeaf(target, seg)}");
            }
            return 0;
        }

        // Whole-record dump (one level): every modeled field, fault-isolated per field. The CORPUS is the
        // authoritative modeled-field set (infra-free by construction — it's what the read-proof drives); fall
        // back to reflection (Loqui-filtered) only when the corpus isn't built.
        foreach (var name in ModeledFieldNames(typeName, target.GetType()))
            Console.WriteLine($"  {name} = {ReadLeaf(target, new[] { name })}");
        return 0;
    }

    /// <summary>Read a located record's fields as round-trippable tokens — the public, structured entry the MCP
    /// server consumes (the §8.4 read cleave; <c>RunRead</c> is the CLI sibling). With <paramref name="paths"/>:
    /// exactly those leaves (the get_field primitive). Without: a one-level dump of every modeled field. Wraps the
    /// proven internal <see cref="ReadLeaf"/> (the round-trip oracle drives it), so the server's reads inherit the
    /// read-proof by construction. Per-leaf fault isolation (Q3): an unreadable field names itself in its
    /// <see cref="FieldValue.Note"/>, never throws out of the record read.</summary>
    public static RecordFields ReadFields(IMajorRecordGetter record, IReadOnlyList<string>? paths = null)
    {
        var typeName = RecordNaming.StripGetterInterface(WriteEngine.PrimaryGetter(record.GetType())?.Name ?? "I?Getter");
        var targets = paths is { Count: > 0 } ? (IEnumerable<string>)paths : ModeledFieldNames(typeName, record.GetType());
        var fields = new List<FieldValue>();
        foreach (var p in targets)
        {
            var seg = p.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var r = ReadLeaf(record, seg);
            fields.Add(new FieldValue(p, r.HasValue, r.HasValue ? r.Token : null, r.HasValue ? null : r.Note));
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
                if (p is null) return LeafRead.None($"(no field {segName})");
                current = segKey is null
                    ? p.GetValue(current)                                  // descend a substruct (read-only)
                    : WriteEngine.StepIntoElement(current, p, segName, segKey); // collnav (handles IReadOnly*)
                if (current is null) return LeafRead.None(AbsentNote);     // absent optional substruct
            }

            var (leafName, leafKey) = WriteEngine.ParseSegment(path[^1]);
            var leaf = WriteEngine.ResolveProperty(current!.GetType(), leafName);
            if (leaf is null) return LeafRead.None($"(no field {leafName})");
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
        // including the comma form for a [Flags] combination.
        if (u.IsEnum || val.GetType().IsEnum) return LeafRead.Value(val.ToString() ?? "");
        // formlink (inverse of TryFormLink) — FormKey.ToString() ↔ FormKey.Factory. A present-but-null
        // link (FormKey.Null) is NOT a round-trippable token (the write surface sets links to a real
        // FormKey, never "Null"), so surface it as no-value — consistent with what Coerce accepts.
        if (val is IFormLinkGetter fl)
            return fl.FormKey.IsNull ? LeafRead.None("(null link)") : LeafRead.Value(fl.FormKey.ToString());
        // value types (inverse of TryValueType)
        if (TryEmitValueType(val, out var vt)) return LeafRead.Value(vt);

        // Not a single-token VALUE leaf: a substruct / collection / arm container. The oracle never
        // drives these AS leaves — their sub-leaves are driven individually (exactly like write-proof).
        // Summarise for the read display.
        return LeafRead.None(SummariseContainer(val));
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

        // TranslatedString — emit the plain .String the implicit `record.Name = "x"` conversion stored.
        if (fn == "Mutagen.Bethesda.Strings.TranslatedString")
        { token = ReflectString(val, "String") ?? ""; return true; }

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
        // by-name recognition the engine uses for the FLOI family.
        if (IsAssetLinkFamily(rt2))
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
            if (val is IFormLinkGetter fl) return LeafRead.Value(fl.FormKey.ToString());   // form mode
            return LeafRead.None("(floi: form mode but no readable FormKey)");
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

    static bool IsAssetLinkFamily(Type t)
    {
        if (!t.IsGenericType) return false;
        var n = t.GetGenericTypeDefinition().Name;
        return n.StartsWith("AssetLink", StringComparison.Ordinal) || n.StartsWith("IAssetLink", StringComparison.Ordinal);
    }

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
    /// arm) for the read display. Its sub-leaves are the round-trippable surface.</summary>
    static string SummariseContainer(object val)
    {
        if (val is System.Collections.IEnumerable en and not string)
        {
            int n = 0;
            foreach (var _ in en) n++;
            return $"[{RecordNaming.StripGetterInterface(val.GetType().Name)}: {n} item(s)]";
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
