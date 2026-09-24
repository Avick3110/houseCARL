using System.Text.Json;

namespace HousecarlCore;

/// <summary>The SkyPatcher op to Mutagen-field map — which record field an operation lands on and with which semantic; the coverage contract is in docs/architecture/skypatcher-layer.md.</summary>
/// <remarks>SkyPatcherFieldMapGuardTests walks every path with the real write engine and every valueMap against the real leaf enum, so only a semantically-wrong-but-existing field survives CI.</remarks>
public sealed class SkyPatcherFieldMap
{
    /// <summary>Neither key is unique alone (<c>leveledList</c> serves LVLI and LVLN; RACE is patched from <c>race/</c> and <c>raceHook/</c>), so maps are grouped both ways.</summary>
    readonly Dictionary<string, List<RecordMap>> _bySubfolder;
    readonly Dictionary<string, List<RecordMap>> _byRecordType;

    public IReadOnlyList<RecordMap> Records { get; }

    SkyPatcherFieldMap(IReadOnlyList<RecordMap> records)
    {
        Records = records;
        _bySubfolder = new(StringComparer.OrdinalIgnoreCase);
        _byRecordType = new(StringComparer.OrdinalIgnoreCase);
        foreach (var r in records)
        {
            (_bySubfolder.TryGetValue(r.Subfolder, out var s) ? s : _bySubfolder[r.Subfolder] = new()).Add(r);
            (_byRecordType.TryGetValue(r.RecordType, out var t) ? t : _byRecordType[r.RecordType] = new()).Add(r);
        }
    }

    static readonly IReadOnlyList<RecordMap> None = Array.Empty<RecordMap>();

    /// <summary>The map(s) fed from an INI subfolder; empty when the type has no field map yet, which the overlay surfaces loud.</summary>
    public IReadOnlyList<RecordMap> ForSubfolder(string subfolder)
        => subfolder is not null && _bySubfolder.TryGetValue(subfolder, out var r) ? r : None;

    /// <summary>The map(s) that patch one Mutagen record type — the service's routing key from record type to the INI folders that can touch it.</summary>
    public IReadOnlyList<RecordMap> ForRecordType(string mutagenType)
        => mutagenType is not null && _byRecordType.TryGetValue(mutagenType, out var r) ? r : None;

    public RecordMap? For(string subfolder, string mutagenType)
        => ForSubfolder(subfolder).FirstOrDefault(r => r.RecordType.Equals(mutagenType, StringComparison.OrdinalIgnoreCase));


    static SkyPatcherFieldMap? _cached;

    /// <summary>Load the embedded field map (memoized); throws loudly on a missing or malformed resource.</summary>
    public static SkyPatcherFieldMap Load() => _cached ??= LoadFrom(EmbeddedJson.Read("skypatcher-fieldmap.json", "SkyPatcher field map"));

    public static SkyPatcherFieldMap LoadFrom(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var records = new List<RecordMap>();
        foreach (var el in doc.RootElement.EnumerateArray())
            records.Add(ParseRecord(el));
        return new SkyPatcherFieldMap(records);
    }

    static RecordMap ParseRecord(JsonElement el)
    {
        var subfolder = Str(el, "subfolder");
        var recordType = Str(el, "recordType");
        var ops = new Dictionary<string, OpMap>(StringComparer.Ordinal);
        if (!el.TryGetProperty("ops", out var opsEl) || opsEl.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"SkyPatcher field map [{subfolder}]: 'ops' is missing or not an object.");
        foreach (var p in opsEl.EnumerateObject())
            ops[p.Name] = ParseOp(subfolder, p.Name, p.Value);

        // The per-record filter evaluation specs, under the same wrong-kind-throws-loud contract as 'ops'.
        var filters = new Dictionary<string, FilterSpec>(StringComparer.Ordinal);
        if (el.TryGetProperty("filters", out var fEl))
        {
            if (fEl.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException($"SkyPatcher field map [{subfolder}]: 'filters' is present but not an object.");
            foreach (var p in fEl.EnumerateObject())
                filters[p.Name] = ParseFilter(subfolder, p.Name, p.Value);
        }
        return new RecordMap(subfolder, recordType, ops, filters);
    }

    static FilterSpec ParseFilter(string subfolder, string name, JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"SkyPatcher field map [{subfolder}.filters.{name}]: entry is not an object.");

        var unmapped = OptStr(el, "unmapped");
        if (unmapped is not null)
            return FilterSpec.MakeUnmapped(unmapped);

        var eval = ParseFilterEval(Str(el, "eval"), $"{subfolder}.filters.{name}");

        // 'path' (one) or 'paths' (a filter matching any of several leaves); exactly one must be present.
        string[] paths;
        if (el.TryGetProperty("paths", out var ps))
        {
            if (ps.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException($"SkyPatcher field map [{subfolder}.filters.{name}]: 'paths' is not an array.");
            paths = ps.EnumerateArray().Select(p => p.GetString()
                ?? throw new InvalidOperationException($"SkyPatcher field map [{subfolder}.filters.{name}]: 'paths' entry is not a string.")).ToArray();
            if (el.TryGetProperty("path", out _))
                throw new InvalidOperationException($"SkyPatcher field map [{subfolder}.filters.{name}]: has BOTH 'path' and 'paths'.");
        }
        else if (el.TryGetProperty("path", out _)) paths = new[] { Str(el, "path") };
        else if (eval == SkyPatcherFilterEval.DonorKeywords) paths = Array.Empty<string>();   // reads the donor's keyword list, no own path
        else throw new InvalidOperationException($"SkyPatcher field map [{subfolder}.filters.{name}]: needs 'path' or 'paths'.");

        Dictionary<string, string>? valueMap = null;
        if (el.TryGetProperty("valueMap", out var vm))
        {
            if (vm.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException($"SkyPatcher field map [{subfolder}.filters.{name}]: 'valueMap' is not an object.");
            valueMap = new(StringComparer.OrdinalIgnoreCase);
            foreach (var p in vm.EnumerateObject())
                valueMap[p.Name] = p.Value.GetString()
                    ?? throw new InvalidOperationException($"SkyPatcher field map [{subfolder}.filters.{name}]: valueMap '{p.Name}' is not a string.");
        }

        return new FilterSpec(
            eval, paths,
            KeyPath: OptStr(el, "keyPath"),
            FormType: OptStr(el, "formType"),
            Flag: OptStr(el, "flag"),
            Invert: el.TryGetProperty("invert", out var inv) && inv.ValueKind == JsonValueKind.True,
            LinkPath: OptStr(el, "linkPath"),
            EidSubstring: el.TryGetProperty("eidSubstring", out var es) && es.ValueKind == JsonValueKind.True,
            ValueMap: valueMap,
            Note: OptStr(el, "note"),
            Unmapped: null);
    }

    static SkyPatcherFilterEval ParseFilterEval(string s, string ctx) => s switch
    {
        "formEquals" => SkyPatcherFilterEval.FormEquals,
        "formInList" => SkyPatcherFilterEval.FormInList,
        "enumEquals" => SkyPatcherFilterEval.EnumEquals,
        "flagBool" => SkyPatcherFilterEval.FlagBool,
        "flagAnyOf" => SkyPatcherFilterEval.FlagAnyOf,
        "gender" => SkyPatcherFilterEval.Gender,
        "pcLevelMult" => SkyPatcherFilterEval.PcLevelMult,
        "substringLeaf" => SkyPatcherFilterEval.SubstringLeaf,
        "donorSubstring" => SkyPatcherFilterEval.DonorSubstring,
        "donorKeywords" => SkyPatcherFilterEval.DonorKeywords,
        "numericLess" => SkyPatcherFilterEval.NumericLess,
        "bipedSlots" => SkyPatcherFilterEval.BipedSlots,
        "linkedOriginPlugin" => SkyPatcherFilterEval.LinkedOriginPlugin,
        _ => throw new InvalidOperationException($"SkyPatcher field map [{ctx}]: unknown filter eval '{s}'."),
    };

    static OpMap ParseOp(string subfolder, string opName, JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"SkyPatcher field map [{subfolder}.{opName}]: entry is not an object.");

        var unmapped = OptStr(el, "unmapped");
        if (unmapped is not null)
            return OpMap.MakeUnmapped(unmapped);

        var semantic = ParseSemantic(Str(el, "semantic"), $"{subfolder}.{opName}");
        var path = Str(el, "path");

        Dictionary<string, string>? valueMap = null;
        if (el.TryGetProperty("valueMap", out var vm))
        {
            if (vm.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException($"SkyPatcher field map [{subfolder}.{opName}]: 'valueMap' is not an object.");
            valueMap = new(StringComparer.OrdinalIgnoreCase);
            foreach (var p in vm.EnumerateObject())
                valueMap[p.Name] = p.Value.GetString()
                    ?? throw new InvalidOperationException($"SkyPatcher field map [{subfolder}.{opName}]: valueMap '{p.Name}' is not a string.");
        }

        ElementMap? element = null;
        if (el.TryGetProperty("element", out var em))
        {
            if (em.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException($"SkyPatcher field map [{subfolder}.{opName}]: 'element' is not an object.");
            var fields = new List<ElementField>();
            if (em.TryGetProperty("fields", out var fs))
            {
                if (fs.ValueKind != JsonValueKind.Array)
                    throw new InvalidOperationException($"SkyPatcher field map [{subfolder}.{opName}]: element 'fields' is not an array.");
                foreach (var f in fs.EnumerateArray())
                    fields.Add(new ElementField(
                        Str(f, "path"),
                        f.TryGetProperty("arg", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetInt32()
                            : throw new InvalidOperationException($"SkyPatcher field map [{subfolder}.{opName}]: element field missing numeric 'arg'."),
                        OptStr(f, "default")));
            }
            element = new ElementMap(Str(em, "type"), fields, OptStr(em, "keyPath"), OptStr(em, "countPath"));
        }

        return new OpMap(
            semantic, path,
            Component: el.TryGetProperty("component", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : null,
            SourcePath: OptStr(el, "sourcePath"),
            Flag: OptStr(el, "flag"),
            FormType: OptStr(el, "formType"),
            DonorType: OptStr(el, "donorType"),
            EqPacked: el.TryGetProperty("pack", out var pk) && pk.ValueKind == JsonValueKind.String && pk.GetString() == "eq",
            ValueMap: valueMap,
            Element: element,
            Key: OptStr(el, "key"),
            Note: OptStr(el, "note"),
            Unmapped: null);
    }

    static string Str(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()!
            : throw new InvalidOperationException($"SkyPatcher field map entry missing required string '{prop}'.");

    static string? OptStr(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static SkyPatcherOpSemantic ParseSemantic(string s, string ctx) => s switch
    {
        "set" => SkyPatcherOpSemantic.Set,
        "mult" => SkyPatcherOpSemantic.Mult,
        "addNumeric" => SkyPatcherOpSemantic.AddNumeric,
        "setFromOwnField" => SkyPatcherOpSemantic.SetFromOwnField,
        "vecComponent" => SkyPatcherOpSemantic.VecComponent,
        "modelPath" => SkyPatcherOpSemantic.ModelPath,
        "flagsSet" => SkyPatcherOpSemantic.FlagsSet,
        "flagsRemove" => SkyPatcherOpSemantic.FlagsRemove,
        "flagBool" => SkyPatcherOpSemantic.FlagBool,
        "addForm" => SkyPatcherOpSemantic.AddForm,
        "removeForm" => SkyPatcherOpSemantic.RemoveForm,
        "replaceForm" => SkyPatcherOpSemantic.ReplaceForm,
        "clearList" => SkyPatcherOpSemantic.ClearList,
        "addEntry" => SkyPatcherOpSemantic.AddEntry,
        "addEntryOnce" => SkyPatcherOpSemantic.AddEntryOnce,
        "removeEntry" => SkyPatcherOpSemantic.RemoveEntry,
        "removeEntryByCount" => SkyPatcherOpSemantic.RemoveEntryByCount,
        "replaceEntry" => SkyPatcherOpSemantic.ReplaceEntry,
        "multCount" => SkyPatcherOpSemantic.MultCount,
        "removeByKeyword" => SkyPatcherOpSemantic.RemoveByKeyword,
        "dictSet" => SkyPatcherOpSemantic.DictSet,
        "dictMult" => SkyPatcherOpSemantic.DictMult,
        "colorChannel" => SkyPatcherOpSemantic.ColorChannel,
        "bipedSlotsSet" => SkyPatcherOpSemantic.BipedSlotsSet,
        "bipedSlotsRemove" => SkyPatcherOpSemantic.BipedSlotsRemove,
        "teachSpell" => SkyPatcherOpSemantic.TeachSpell,
        "teachSkill" => SkyPatcherOpSemantic.TeachSkill,
        "setEntryCount" => SkyPatcherOpSemantic.SetEntryCount,
        _ => throw new InvalidOperationException($"SkyPatcher field map [{ctx}]: unknown semantic '{s}'."),
    };
}

/// <summary>How the overlay applies a mapped operation to its target field.</summary>
public enum SkyPatcherOpSemantic
{
    Set,
    /// <summary>Multiply the CURRENT leaf value (stateful — order-dependent).</summary>
    Mult,
    AddNumeric,
    SetFromOwnField,
    /// <summary>Set one component (X/Y/Z) of a whole-value vector leaf (object bounds P3Int16).</summary>
    VecComponent,
    /// <summary>Set a model path: a literal .nif path sets the leaf; a form value copies the donor's model path (resolver read).</summary>
    ModelPath,
    /// <summary>OR the named flag token(s) into a [Flags] enum leaf.</summary>
    FlagsSet,
    FlagsRemove,
    /// <summary>Set/clear ONE fixed flag (<see cref="OpMap.Flag"/>) from a true/false value (setEssential etc.).</summary>
    FlagBool,
    AddForm,
    RemoveForm,
    ReplaceForm,
    ClearList,
    /// <summary>Add a struct entry built from the packed sub-args (addToContainers, objectsToAdd, addToLLs…).</summary>
    AddEntry,
    /// <summary>AddEntry, skipped when an entry with the same key form already exists (addOnceToX).</summary>
    AddEntryOnce,
    /// <summary>Remove entries whose key form matches (removeFromX, objectsToRemove, factionsToRemove…).</summary>
    RemoveEntry,
    /// <summary>Remove a specific count from matching entries (removeFromXByCount / removeInventoryObjectsByCount).</summary>
    RemoveEntryByCount,
    /// <summary>Replace entry key formA with formB, count/rank preserved (replaceInX, objectsToReplace).</summary>
    ReplaceEntry,
    /// <summary>Multiply entry counts (objectMultCount) — collection-scoped stateful multiply.</summary>
    MultCount,
    /// <summary>Remove entries whose TARGET record carries a keyword (removeInventoryObjectsByKeywords…) — needs the resolver.</summary>
    RemoveByKeyword,
    /// <summary>Set one entry of a numeric-valued dict (<see cref="OpMap.Key"/>), riding the engine's dict Set.</summary>
    DictSet,
    DictMult,
    /// <summary>Set ONE channel (<see cref="OpMap.Component"/>: 0=R 1=G 2=B) of a whole-value Color leaf, alpha preserved.</summary>
    ColorChannel,
    /// <summary>OR biped-slot INDEX bits (0–31; slot number − 30) into a BipedObjectFlag leaf.</summary>
    BipedSlotsSet,
    BipedSlotsRemove,
    /// <summary>Set Book.Teaches to the BookSpell arm holding the given spell (compose-Set through the engine).</summary>
    TeachSpell,
    /// <summary>Set Book.Teaches to the BookSkill arm holding the given skill (compose-Set through the engine).</summary>
    TeachSkill,
    /// <summary>Set matching entries' count to N (changeCobjsCount form~count; 'null' as the form = ALL entries).</summary>
    SetEntryCount,
}

/// <summary>One record type's op to field mappings and filter to evaluation specs, keyed by the exact catalog names; a filter absent from <see cref="Filters"/> is either evaluated built-in by the overlay or is a coverage gap that fails CI.</summary>
public sealed record RecordMap(string Subfolder, string RecordType, IReadOnlyDictionary<string, OpMap> Ops,
    IReadOnlyDictionary<string, FilterSpec> Filters);

/// <summary>How the overlay evaluates a per-record mapped filter against the running copy.</summary>
public enum SkyPatcherFilterEval
{
    /// <summary>A single formlink leaf equals ANY listed form — declared assumption: an AND over one slot is unsatisfiable for several distinct values.</summary>
    FormEquals,
    /// <summary>A formlink list (or struct list via <see cref="FilterSpec.KeyPath"/>) contains the listed forms — bare = all present, Or = any, Excluded = none.</summary>
    FormInList,
    /// <summary>An enum leaf equals ANY listed token (valueMap first, then ignore-case member match).</summary>
    EnumEquals,
    /// <summary>ONE fixed flag (<see cref="FilterSpec.Flag"/>) tested against a true/false value, with <see cref="FilterSpec.Invert"/> flipping the sense.</summary>
    FlagBool,
    /// <summary>Listed flag tokens tested against a [Flags] enum leaf — bare = all set, Or = any, Excluded = none.</summary>
    FlagAnyOf,
    /// <summary>NPC gender: token 'female' ⇒ the Female configuration flag set, 'male' ⇒ clear.</summary>
    Gender,
    /// <summary>NPC filterByPCLevelMult: whether the polymorphic Configuration.Level is the PcLevelMult arm or a static NpcLevel.</summary>
    PcLevelMult,
    /// <summary>A string leaf contains the listed substring(s) — the Contains-family connectives.</summary>
    SubstringLeaf,
    /// <summary>Follow <see cref="FilterSpec.LinkPath"/> to a donor record's winner and substring-match a leaf THERE.</summary>
    DonorSubstring,
    /// <summary>Follow <see cref="FilterSpec.LinkPath"/> to a donor record's winner and match the keyword list THERE.</summary>
    DonorKeywords,
    /// <summary>A numeric leaf is strictly less than the listed number (filterByWeightLessThan).</summary>
    NumericLess,
    /// <summary>Biped-slot INDICES (0–31; slot number − 30) tested as bits of a BipedObjectFlag leaf.</summary>
    BipedSlots,
    /// <summary>The origin plugin of the record a formlink leaf points at, tested against the listed plugin names. DECLARED ASSUMPTION: "comes from mod X" is the linked form's defining master, not the plugin that last overrode the link.</summary>
    LinkedOriginPlugin,
}

/// <summary>One filter's evaluation spec; <see cref="Unmapped"/> non-null means the filter is explicitly not statically evaluable, with the reason the overlay surfaces loud.</summary>
public sealed record FilterSpec(
    SkyPatcherFilterEval Eval,
    IReadOnlyList<string> Paths,
    string? KeyPath,
    string? FormType,
    string? Flag,
    bool Invert,
    string? LinkPath,
    bool EidSubstring,
    IReadOnlyDictionary<string, string>? ValueMap,
    string? Note,
    string? Unmapped)
{
    internal static FilterSpec MakeUnmapped(string reason)
        => new(SkyPatcherFilterEval.FormEquals, Array.Empty<string>(), null, null, null, false, null, false, null, null, reason);
    public bool IsUnmapped => Unmapped is not null;
}

/// <summary>One operation's mapping; <see cref="Unmapped"/> non-null means the op is explicitly not modelable, with the reason the overlay surfaces loud. <see cref="DonorType"/> names a second record kind the value may be, which the overlay reports as an unmodeled donor-copy rather than a malformed FormKey.</summary>
public sealed record OpMap(
    SkyPatcherOpSemantic Semantic,
    string Path,
    int? Component,
    string? SourcePath,
    string? Flag,
    string? FormType,
    string? DonorType,
    bool EqPacked,
    IReadOnlyDictionary<string, string>? ValueMap,
    ElementMap? Element,
    string? Key,
    string? Note,
    string? Unmapped)
{
    internal static OpMap MakeUnmapped(string reason)
        => new(SkyPatcherOpSemantic.Set, "", null, null, null, null, null, false, null, null, null, null, reason);
    public bool IsUnmapped => Unmapped is not null;
}

/// <summary>How a struct-entry collection op builds and matches its elements: the Mutagen element type, the packed-sub-arg wiring, the key path, and the count path.</summary>
public sealed record ElementMap(string Type, IReadOnlyList<ElementField> Fields, string? KeyPath, string? CountPath);

/// <summary>One element sub-field fed from packed sub-arg <see cref="Arg"/> (0-based, after '='-unpacking), with an optional default when the sub-arg is absent.</summary>
public sealed record ElementField(string Path, int Arg, string? Default);
