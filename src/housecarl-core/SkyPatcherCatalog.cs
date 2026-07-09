using System.Reflection;
using System.Text.Json;

namespace HousecarlCore;

/// <summary>
/// The SkyPatcher grammar CATALOG — the semantic layer over <see cref="SkyPatcherParse"/> (Wave 0b of
/// the SkyPatcher distributor subsystem; plan dev/plans/SKYPATCHER_DISTRIBUTOR_TOOL_PLAN_2026-07-08.md).
/// Where the tokenizer says "here is a <c>key=value</c> segment", the catalog answers "that key is a
/// FILTER of this kind / an OPERATION of this shape and CLEAN/COLLECTION/HARD tractability — or it is
/// NOT in the SkyPatcher reference at all."
///
/// <para><b>Closed set, warn-on-unknown (Aaron's Wave-0b call).</b> The catalog is the FULL enumeration
/// of every documented filter and operation per record type, transcribed faithfully from the bundled
/// <c>skypatcher-authoring</c> reference (its provenance — never invented). A key that resolves to no
/// entry is reported as <see cref="SkyPatcherKeyRole.Unknown"/>, never silently assumed — the same
/// bundled-or-warn discipline mutagen-reference / papyrus-reference / the skill itself use (Q3).</para>
///
/// <para>Loaded once from the embedded <c>skypatcher-catalog.json</c> (built from the reference; the
/// record dimension — subfolder / sig / primaryFilter — is cross-checked against the reference
/// <c>index.jsonl</c> by the catalog guard). Field-path mapping onto Mutagen records is NOT here — that
/// is the Wave-1 overlay engine; the catalog classifies, it does not resolve values.</para>
/// </summary>
public sealed class SkyPatcherCatalog
{
    /// <summary>Longest-first so 'Excluded' is tried before 'Exclude' before 'Or' when stripping a filter connective.</summary>
    static readonly string[] ConnectiveSuffixes = { "Excluded", "Exclude", "Or" };

    /// <summary>Every record type's catalog, in load order.</summary>
    public IReadOnlyList<SkyPatcherRecordCatalog> Records { get; }

    readonly Dictionary<string, SkyPatcherRecordCatalog> _bySubfolder;   // subfolder (case-insensitive) → catalog
    readonly Dictionary<string, RecordLookup> _lookup;                    // subfolder (case-insensitive) → name maps

    sealed record RecordLookup(
        Dictionary<string, SkyPatcherFilterDef> Filters,   // base filter name (ordinal) → def
        Dictionary<string, SkyPatcherOpDef> Operations);   // op name (ordinal) → def

    SkyPatcherCatalog(IReadOnlyList<SkyPatcherRecordCatalog> records)
    {
        Records = records;
        _bySubfolder = new(StringComparer.OrdinalIgnoreCase);
        _lookup = new(StringComparer.OrdinalIgnoreCase);
        foreach (var r in records)
        {
            // Deliberate case asymmetry: subfolders match case-INSENSITIVELY (Windows paths), but filter/op
            // key names match case-SENSITIVELY as the reference documents them. Whether the real SkyPatcher
            // DLL accepts e.g. 'attackdamage' is a Wave-1 empirical item; until verified, a wrong-cased key
            // classifies as Unknown (a loud warn, never a silent guess).
            _bySubfolder[r.Subfolder] = r;
            _lookup[r.Subfolder] = new RecordLookup(
                r.Filters.ToDictionary(f => f.Name, f => f, StringComparer.Ordinal),
                r.Operations.ToDictionary(o => o.Name, o => o, StringComparer.Ordinal));
        }
    }

    /// <summary>The record catalog for an INI subfolder (e.g. "weapon", "constructibleObject"), or null if unknown.</summary>
    public SkyPatcherRecordCatalog? ForSubfolder(string subfolder)
        => subfolder is not null && _bySubfolder.TryGetValue(subfolder, out var r) ? r : null;

    /// <summary>
    /// Classify a raw segment key against a record type's catalog. Order: exact operation (ops take no
    /// connective) → bare filter → filter + a documented connective suffix → Unknown. A connective is
    /// only stripped when the remaining base is a real filter that documents that connective, so an
    /// operation that merely ends in "Or" isn't mis-split.
    /// </summary>
    public SkyPatcherKeyClass Classify(SkyPatcherRecordCatalog record, string key)
    {
        var lk = _lookup[record.Subfolder];

        if (lk.Operations.TryGetValue(key, out var op))
            return new SkyPatcherKeyClass(SkyPatcherKeyRole.Operation, key, null, null, op);

        if (lk.Filters.TryGetValue(key, out var bare))
            return new SkyPatcherKeyClass(SkyPatcherKeyRole.Filter, key, "", bare, null);

        foreach (var c in ConnectiveSuffixes)
            if (key.Length > c.Length && key.EndsWith(c, StringComparison.Ordinal))
            {
                var baseKey = key[..^c.Length];
                if (lk.Filters.TryGetValue(baseKey, out var f) && f.Connectives.Contains(c))
                    return new SkyPatcherKeyClass(SkyPatcherKeyRole.Filter, baseKey, c, f, null);
            }

        return new SkyPatcherKeyClass(SkyPatcherKeyRole.Unknown, key, null, null, null);
    }

    // ---- loading -----------------------------------------------------------------------------------

    static SkyPatcherCatalog? _cached;

    /// <summary>Load the embedded catalog (memoized). Throws loudly on a missing/malformed resource — a
    /// catalog that silently loaded empty would make every op read as Unknown (a Q3 silent-degrade).</summary>
    public static SkyPatcherCatalog Load() => _cached ??= LoadFrom(ReadEmbeddedJson());

    /// <summary>Parse a catalog from JSON text (also the guard's entry point for a fixture).</summary>
    public static SkyPatcherCatalog LoadFrom(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var records = new List<SkyPatcherRecordCatalog>();
        foreach (var el in doc.RootElement.EnumerateArray())
            records.Add(ParseRecord(el));
        return new SkyPatcherCatalog(records);
    }

    static string ReadEmbeddedJson()
    {
        var asm = typeof(SkyPatcherCatalog).Assembly;
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("skypatcher-catalog.json", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("SkyPatcher catalog resource 'skypatcher-catalog.json' is not embedded in housecarl-core.");
        using var s = asm.GetManifestResourceStream(name)!;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    static SkyPatcherRecordCatalog ParseRecord(JsonElement el)
    {
        var recordType = Str(el, "recordType");
        var filters = new List<SkyPatcherFilterDef>();
        var ops = new List<SkyPatcherOpDef>();

        if (el.TryGetProperty("filters", out var fs) && fs.ValueKind == JsonValueKind.Array)
            foreach (var f in fs.EnumerateArray())
                filters.Add(new SkyPatcherFilterDef(
                    Str(f, "name"),
                    ParseFilterKind(Str(f, "kind"), recordType),
                    f.TryGetProperty("connectives", out var cs) && cs.ValueKind == JsonValueKind.Array
                        ? cs.EnumerateArray().Select(c => c.GetString() ?? "").ToArray()
                        : new[] { "" },
                    OptStr(f, "selects")));

        if (el.TryGetProperty("operations", out var opsEl) && opsEl.ValueKind == JsonValueKind.Array)
            foreach (var o in opsEl.EnumerateArray())
                ops.Add(new SkyPatcherOpDef(
                    Str(o, "name"),
                    ParseShape(Str(o, "shape"), recordType),
                    ParseTractability(Str(o, "tractability"), recordType),
                    o.TryGetProperty("stateful", out var st) && st.ValueKind == JsonValueKind.True,
                    OptStr(o, "note")));

        return new SkyPatcherRecordCatalog(
            recordType, Str(el, "sig"), Str(el, "subfolder"), Str(el, "primaryFilter"),
            filters, ops, OptStr(el, "note"));
    }

    static string Str(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()!
            : throw new InvalidOperationException($"SkyPatcher catalog entry missing required string '{prop}'.");

    static string? OptStr(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static SkyPatcherFilterKind ParseFilterKind(string s, string ctx) => s switch
    {
        "primary" => SkyPatcherFilterKind.Primary,
        "crosscutting" => SkyPatcherFilterKind.CrossCutting,
        "recordSpecific" => SkyPatcherFilterKind.RecordSpecific,
        "restrict" => SkyPatcherFilterKind.Restrict,
        "hasPlugins" => SkyPatcherFilterKind.HasPlugins,
        "overrideAware" => SkyPatcherFilterKind.OverrideAware,
        "noFilter" => SkyPatcherFilterKind.NoFilter,
        _ => throw new InvalidOperationException($"SkyPatcher catalog [{ctx}]: unknown filter kind '{s}'."),
    };

    static SkyPatcherOpShape ParseShape(string s, string ctx) => s switch
    {
        "set" => SkyPatcherOpShape.Set,
        "mult" => SkyPatcherOpShape.Mult,
        "add_numeric" => SkyPatcherOpShape.AddNumeric,
        "collection" => SkyPatcherOpShape.Collection,
        "mirror" => SkyPatcherOpShape.Mirror,
        "flags" => SkyPatcherOpShape.Flags,
        "rename" => SkyPatcherOpShape.Rename,
        "null_clear" => SkyPatcherOpShape.NullClear,
        "compound" => SkyPatcherOpShape.Compound,
        _ => throw new InvalidOperationException($"SkyPatcher catalog [{ctx}]: unknown op shape '{s}'."),
    };

    static SkyPatcherTractability ParseTractability(string s, string ctx) => s.ToUpperInvariant() switch
    {
        "CLEAN" => SkyPatcherTractability.Clean,
        "COLLECTION" => SkyPatcherTractability.Collection,
        "HARD" => SkyPatcherTractability.Hard,
        _ => throw new InvalidOperationException($"SkyPatcher catalog [{ctx}]: unknown tractability '{s}'."),
    };
}

/// <summary>How a filter combines its values (by suffix): Primary is the record's own filterBy&lt;Type&gt;.</summary>
public enum SkyPatcherFilterKind { Primary, CrossCutting, RecordSpecific, Restrict, HasPlugins, OverrideAware, NoFilter }

/// <summary>The value-grammar shape of an operation (inventory categories a–i).</summary>
public enum SkyPatcherOpShape { Set, Mult, AddNumeric, Collection, Mirror, Flags, Rename, NullClear, Compound }

/// <summary>How faithfully the overlay can resolve this op's post-state (Wave-1 tiered honesty).</summary>
public enum SkyPatcherTractability { Clean, Collection, Hard }

/// <summary>What a classified segment key is.</summary>
public enum SkyPatcherKeyRole { Filter, Operation, Unknown }

/// <summary>One documented filter token (base name; connective variants are in <see cref="Connectives"/>).</summary>
public sealed record SkyPatcherFilterDef(string Name, SkyPatcherFilterKind Kind, IReadOnlyList<string> Connectives, string? Selects);

/// <summary>One documented operation token, with its shape, tractability, and stateful-value flag.</summary>
public sealed record SkyPatcherOpDef(string Name, SkyPatcherOpShape Shape, SkyPatcherTractability Tractability, bool Stateful, string? Note);

/// <summary>One record type's full closed filter+operation catalog. <see cref="Note"/> carries e.g. the OMOD gap.</summary>
public sealed record SkyPatcherRecordCatalog(
    string RecordType,
    string Sig,
    string Subfolder,
    string PrimaryFilter,
    IReadOnlyList<SkyPatcherFilterDef> Filters,
    IReadOnlyList<SkyPatcherOpDef> Operations,
    string? Note);

/// <summary>
/// The classification of one segment key against a record type. For a filter, <see cref="Connective"/>
/// is "" (bare) / "Or" / "Excluded" / "Exclude" and <see cref="BaseKey"/> is the connective-stripped
/// name. For an operation, <see cref="Operation"/> is set. <see cref="SkyPatcherKeyRole.Unknown"/> means
/// the key is in no reference entry for this type — surface it, don't assume.
/// </summary>
public sealed record SkyPatcherKeyClass(
    SkyPatcherKeyRole Role,
    string BaseKey,
    string? Connective,
    SkyPatcherFilterDef? Filter,
    SkyPatcherOpDef? Operation);
