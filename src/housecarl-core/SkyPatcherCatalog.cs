using System.Reflection;
using System.Text.Json;

namespace HousecarlCore;

/// <summary>The SkyPatcher grammar catalog — classifies one segment key as a filter, an operation, or Unknown; contract in docs/architecture/skypatcher-layer.md.</summary>
/// <remarks>Transcribed from the bundled skypatcher-authoring reference, never invented, and cross-checked in CI against that skill's SKILL.md router table by SkyPatcherCatalogTests.</remarks>
public sealed class SkyPatcherCatalog
{
    /// <summary>The connective vocabulary derived from the loaded catalog, longest-first with ordinal tie-breaks.</summary>
    readonly string[] _connectiveSuffixes;

    /// <summary>Every record type's catalog, in load order.</summary>
    public IReadOnlyList<SkyPatcherRecordCatalog> Records { get; }

    /// <summary>The SkyPatcher version the catalog is transcribed from.</summary>
    public string Version { get; }

    /// <summary>The subfolders in the order the DLL runs their patchers.</summary>
    public IReadOnlyList<string> PatcherOrder { get; }

    readonly Dictionary<string, SkyPatcherRecordCatalog> _bySubfolder;   // subfolder (case-insensitive) → catalog
    readonly Dictionary<string, RecordLookup> _lookup;                    // subfolder (case-insensitive) → name maps

    sealed record RecordLookup(
        Dictionary<string, SkyPatcherFilterDef> Filters,   // base filter name (case-insensitive) → def
        Dictionary<string, SkyPatcherOpDef> Operations);   // op name (case-insensitive) → def

    SkyPatcherCatalog(string version, IReadOnlyList<string> patcherOrder, IReadOnlyList<SkyPatcherRecordCatalog> records)
    {
        Version = version;
        PatcherOrder = patcherOrder;
        Records = records;
        _bySubfolder = new(StringComparer.OrdinalIgnoreCase);
        _lookup = new(StringComparer.OrdinalIgnoreCase);
        foreach (var r in records)
        {
            // Subfolders and filter/op key names match case-insensitively, as SkyPatcher does.
            _bySubfolder[r.Subfolder] = r;
            _lookup[r.Subfolder] = new RecordLookup(
                r.Filters.ToDictionary(f => f.Name, f => f, StringComparer.OrdinalIgnoreCase),
                r.Operations.ToDictionary(o => o.Name, o => o, StringComparer.OrdinalIgnoreCase));
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
            if (key.Length > c.Length && key.EndsWith(c, StringComparison.OrdinalIgnoreCase))
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
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("SkyPatcher catalog: the root is not an object with 'version', 'patcherOrder' and 'records'.");
        var records = new List<SkyPatcherRecordCatalog>();
        if (root.TryGetProperty("records", out var rs) && RequireArray(rs, "records", "root"))
            foreach (var el in rs.EnumerateArray())
                records.Add(ParseRecord(el));
        return new SkyPatcherCatalog(Str(root, "version"), StrList(root, "patcherOrder", "root"), records);
    }

    /// <summary>A string array property; absent reads as empty, a wrong kind throws loudly.</summary>
    static string[] StrList(JsonElement el, string prop, string ctx)
        => el.TryGetProperty(prop, out var a) && RequireArray(a, prop, ctx)
            ? a.EnumerateArray().Select(x => x.GetString() ?? throw new InvalidOperationException($"SkyPatcher catalog [{ctx}]: '{prop}' holds a non-string.")).ToArray()
            : Array.Empty<string>();

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
                    OptStr(f, "selects"),
                    ParseRole(Str(f, "role"), recordType)));

        if (el.TryGetProperty("operations", out var opsEl) && RequireArray(opsEl, "operations", recordType))
            foreach (var o in opsEl.EnumerateArray())
                ops.Add(new SkyPatcherOpDef(
                    Str(o, "name"),
                    ParseShape(Str(o, "shape"), recordType),
                    ParseTractability(Str(o, "tractability"), recordType),
                    o.TryGetProperty("stateful", out var st) && st.ValueKind == JsonValueKind.True,
                    OptStr(o, "note")));

        var sources = new List<SkyPatcherKeywordSource>();
        if (el.TryGetProperty("keywordsAlsoFrom", out var ks) && RequireArray(ks, "keywordsAlsoFrom", recordType))
            foreach (var k in ks.EnumerateArray())
                sources.Add(new SkyPatcherKeywordSource(Str(k, "path"), OptStr(k, "keyPath")));
        var player = el.TryGetProperty("player", out var pl) ? pl : default;
        return new SkyPatcherRecordCatalog(
            recordType, Str(el, "sig"), Str(el, "subfolder"), Str(el, "primaryFilter"),
            filters, ops, OptStr(el, "note"))
        {
            FilterlessPatchesNothing = OptStr(el, "filterless") switch
            {
                null => false,
                "nothing" => true,
                var x => throw new InvalidOperationException($"SkyPatcher catalog [{recordType}]: unknown filterless value '{x}'."),
            },
            Ignores = StrList(el, "ignores", recordType),
            DirectNarrowedBy = StrList(el, "directNarrowedBy", recordType),
            PlayerFoundBy = player.ValueKind == JsonValueKind.Object ? StrList(player, "foundBy", recordType) : Array.Empty<string>(),
            PlayerNeverRemovedBy = player.ValueKind == JsonValueKind.Object ? StrList(player, "neverRemovedBy", recordType) : Array.Empty<string>(),
            KeywordsAlsoFrom = sources,
            EnabledWithoutSwitch = el.TryGetProperty("enabledWithoutSwitch", out var ew) && ew.ValueKind == JsonValueKind.True,
        };
    }

    static SkyPatcherFilterRole ParseRole(string s, string ctx) => s switch
    {
        "direct" => SkyPatcherFilterRole.Direct,
        "finder" => SkyPatcherFilterRole.Finder,
        "narrower" => SkyPatcherFilterRole.Narrower,
        "gate" => SkyPatcherFilterRole.Gate,
        "all" => SkyPatcherFilterRole.All,
        _ => throw new InvalidOperationException($"SkyPatcher catalog [{ctx}]: unknown filter role '{s}'."),
    };

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

/// <summary>How a filter's values are evaluated: Primary is the record's own filterBy&lt;Type&gt;.</summary>
public enum SkyPatcherFilterKind { Primary, CrossCutting, RecordSpecific, Restrict, HasPlugins, OverrideAware, NoFilter }

/// <summary>What a filter does to a line's reach: Direct always patches the records it names, a Finder finds records on its own, a Narrower only removes, a Gate stops the whole line, All finds every record of its class.</summary>
public enum SkyPatcherFilterRole { Direct, Finder, Narrower, Gate, All }

/// <summary>Another record whose keywords a patcher's keyword filters also read: a link at <see cref="Path"/>, or each entry's <see cref="KeyPath"/> link of the list at <see cref="Path"/>.</summary>
public sealed record SkyPatcherKeywordSource(string Path, string? KeyPath);

/// <summary>The value-grammar shape of an operation.</summary>
public enum SkyPatcherOpShape { Set, Mult, AddNumeric, Collection, Mirror, Flags, Rename, NullClear, Compound }

/// <summary>How faithfully the overlay can resolve this op's post-state.</summary>
public enum SkyPatcherTractability { Clean, Collection, Hard }

/// <summary>What a classified segment key is.</summary>
public enum SkyPatcherKeyRole { Filter, Operation, Unknown }

/// <summary>One documented filter token (base name; connective variants are in <see cref="Connectives"/>); an Excluded or Exclude connective narrows whatever the row's <see cref="Role"/>.</summary>
public sealed record SkyPatcherFilterDef(string Name, SkyPatcherFilterKind Kind, IReadOnlyList<string> Connectives, string? Selects,
    SkyPatcherFilterRole Role = SkyPatcherFilterRole.Finder);

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
    string? Note)
{
    /// <summary>A line without filters patches nothing (Leveled List, Reference) instead of every record.</summary>
    public bool FilterlessPatchesNothing { get; init; }

    /// <summary>Filter names this patcher does not read: a line carrying one applies as if it were absent.</summary>
    public IReadOnlyList<string> Ignores { get; init; } = Array.Empty<string>();

    /// <summary>Keys, as a line spells them with their connective, that also remove records of the direct list.</summary>
    public IReadOnlyList<string> DirectNarrowedBy { get; init; } = Array.Empty<string>();

    /// <summary>The finders that can find the player; every other finder and a filterless line never do.</summary>
    public IReadOnlyList<string> PlayerFoundBy { get; init; } = Array.Empty<string>();

    /// <summary>Keys, with their connective, that never remove the player.</summary>
    public IReadOnlyList<string> PlayerNeverRemovedBy { get; init; } = Array.Empty<string>();

    /// <summary>Other records whose keywords this patcher's keyword filters also read.</summary>
    public IReadOnlyList<SkyPatcherKeywordSource> KeywordsAlsoFrom { get; init; } = Array.Empty<SkyPatcherKeywordSource>();

    /// <summary>The patcher runs when SkyPatcher.ini has no switch for it (Light).</summary>
    public bool EnabledWithoutSwitch { get; init; }
}

/// <summary>One segment key's classification: a filter with its connective-stripped <see cref="BaseKey"/> (the line's own spelling), an <see cref="Operation"/>, or Unknown.</summary>
public sealed record SkyPatcherKeyClass(
    SkyPatcherKeyRole Role,
    string BaseKey,
    string? Connective,
    SkyPatcherFilterDef? Filter,
    SkyPatcherOpDef? Operation)
{
    /// <summary>A filter's role on its line: an Excluded or Exclude connective narrows, otherwise the row's role.</summary>
    public SkyPatcherFilterRole FilterRole => Connective is "Excluded" or "Exclude" ? SkyPatcherFilterRole.Narrower : Filter!.Role;

    /// <summary>The catalog's spelling of the key with its connective, as the per-patcher lists name it.</summary>
    public string CatalogKey => Filter!.Name + (Connective ?? "");
}
