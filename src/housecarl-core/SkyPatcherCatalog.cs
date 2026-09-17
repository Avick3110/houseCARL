using System.Reflection;
using System.Text.Json;

namespace HousecarlCore;
/// <summary>The SkyPatcher grammar catalog — classifies one segment key as a filter, an operation, or Unknown; contract in docs/architecture/skypatcher-layer.md.</summary>
/// <remarks>Transcribed from the bundled skypatcher-authoring reference, never invented, and cross-checked in CI against that skill's SKILL.md router table by skypatcher-catalog-guard.</remarks>
/// <summary>The SkyPatcher grammar catalog — classifies one segment key as a filter, an operation, or Unknown; contract in docs/architecture/skypatcher-layer.md.</summary>
public sealed class SkyPatcherCatalog
{
    /// <summary>The connective vocabulary derived from the loaded catalog, longest-first with ordinal tie-breaks.</summary>
    readonly string[] _connectiveSuffixes;

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
            // Subfolders match case-insensitively; filter/op key names match case-sensitively as documented.
            _bySubfolder[r.Subfolder] = r;
            _lookup[r.Subfolder] = new RecordLookup(
                r.Filters.ToDictionary(f => f.Name, f => f, StringComparer.Ordinal),
                r.Operations.ToDictionary(o => o.Name, o => o, StringComparer.Ordinal));
        }
        _connectiveSuffixes = records.SelectMany(r => r.Filters).SelectMany(f => f.Connectives)
            .Where(c => c.Length > 0).Distinct(StringComparer.Ordinal)
            .OrderByDescending(c => c.Length).ThenBy(c => c, StringComparer.Ordinal).ToArray();
    }

    /// <summary>The record catalog for an INI subfolder (e.g. "weapon", "constructibleObject"), or null if unknown.</summary>
    public SkyPatcherRecordCatalog? ForSubfolder(string subfolder)
        => subfolder is not null && _bySubfolder.TryGetValue(subfolder, out var r) ? r : null;

    /// <summary>Classify a segment key: exact operation, then bare filter, then filter + a documented connective suffix, then Unknown.</summary>
    public SkyPatcherKeyClass Classify(SkyPatcherRecordCatalog record, string key)
    {
        var lk = _lookup[record.Subfolder];

        if (lk.Operations.TryGetValue(key, out var op))
            return new SkyPatcherKeyClass(SkyPatcherKeyRole.Operation, key, null, null, op);

        if (lk.Filters.TryGetValue(key, out var bare) && bare.Connectives.Contains(""))
            return new SkyPatcherKeyClass(SkyPatcherKeyRole.Filter, key, "", bare, null);

        foreach (var c in _connectiveSuffixes)
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

    /// <summary>Load the embedded catalog (memoized); throws loudly on a missing or malformed resource.</summary>
    public static SkyPatcherCatalog Load() => _cached ??= LoadFrom(EmbeddedJson.Read("skypatcher-catalog.json", "SkyPatcher catalog"));

    /// <summary>Parse a catalog from JSON text; also the entry point tests use for a fixture.</summary>
    public static SkyPatcherCatalog LoadFrom(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var records = new List<SkyPatcherRecordCatalog>();
        foreach (var el in doc.RootElement.EnumerateArray())
            records.Add(ParseRecord(el));
        return new SkyPatcherCatalog(records);
    }

    static SkyPatcherRecordCatalog ParseRecord(JsonElement el)
    {
        var recordType = Str(el, "recordType");
        var filters = new List<SkyPatcherFilterDef>();
        var ops = new List<SkyPatcherOpDef>();

        // A present-but-wrong-kind node throws loudly, like every other malformed field.
        if (el.TryGetProperty("filters", out var fs) && RequireArray(fs, "filters", recordType))
            foreach (var f in fs.EnumerateArray())
                filters.Add(new SkyPatcherFilterDef(
                    Str(f, "name"),
                    ParseFilterKind(Str(f, "kind"), recordType),
                    f.TryGetProperty("connectives", out var cs) && RequireArray(cs, "connectives", recordType)
                        ? cs.EnumerateArray().Select(c => c.GetString() ?? "").ToArray()
                        : new[] { "" },
                    OptStr(f, "selects")));

        if (el.TryGetProperty("operations", out var opsEl) && RequireArray(opsEl, "operations", recordType))
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

    /// <summary>True when the (present) node is an array; throws loudly on any other kind.</summary>
    static bool RequireArray(JsonElement el, string prop, string ctx)
        => el.ValueKind == JsonValueKind.Array ? true
            : throw new InvalidOperationException($"SkyPatcher catalog [{ctx}]: '{prop}' is present but not an array.");

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

/// <summary>The value-grammar shape of an operation.</summary>
public enum SkyPatcherOpShape { Set, Mult, AddNumeric, Collection, Mirror, Flags, Rename, NullClear, Compound }

/// <summary>How faithfully the overlay can resolve this op's post-state.</summary>
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

/// <summary>One segment key's classification: a filter with its connective-stripped <see cref="BaseKey"/>, an <see cref="Operation"/>, or Unknown.</summary>
public sealed record SkyPatcherKeyClass(
    SkyPatcherKeyRole Role,
    string BaseKey,
    string? Connective,
    SkyPatcherFilterDef? Filter,
    SkyPatcherOpDef? Operation);
