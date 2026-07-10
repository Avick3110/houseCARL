using System.Text.Json;

namespace HousecarlCore;

/// <summary>
/// The SkyPatcher op → Mutagen field MAP — Wave 1 of the SkyPatcher distributor subsystem (plan
/// dev/plans/SKYPATCHER_DISTRIBUTOR_TOOL_PLAN_2026-07-08.md). Where the catalog (Wave 0b) answers
/// "this key is an operation of shape X / tractability Y", the field map answers "and it lands on
/// THIS record field, applied with THIS semantic" — the data the overlay engine
/// (<see cref="SkyPatcherOverlay"/>) executes.
///
/// <para><b>Coverage contract (guard-enforced).</b> Every CLEAN and COLLECTION operation in the
/// catalog has exactly one entry here: either a mapping, or an explicit <see cref="OpMap.Unmapped"/>
/// with a named reason (a genuinely un-modelable op — e.g. weapon <c>bashDamage</c>, which no static
/// WEAP field carries — fails LOUD in the overlay, never silently absent; Q3). HARD ops have NO
/// entry — the overlay renders them as unresolved directives by design (tiered honesty, plan §2.3),
/// and the guard rejects a mapping for one (a mapped HARD op would silently claim a fidelity the
/// layer doesn't have).</para>
///
/// <para><b>Validation is by construction where it can be:</b> the map itself is hand-modeled (like
/// the catalog — provenance is the skypatcher-authoring reference + the Mutagen corpus), but the
/// fieldmap guard walks every <see cref="OpMap.Path"/> with the REAL write engine
/// (<c>WriteEngine.ResolveProperty</c> over the actual Mutagen types) and parses every
/// <see cref="OpMap.ValueMap"/> target against the REAL leaf enum — so a typo'd path or enum member
/// cannot survive CI, only a semantically-wrong-but-existing field can (that's what the Wave-1
/// empirical gate is for).</para>
/// </summary>
public sealed class SkyPatcherFieldMap
{
    /// <summary>Neither key is unique alone — <c>leveledList</c> serves BOTH LVLI and LVLN, and a RACE
    /// record is patched from BOTH <c>race/</c> and <c>raceHook/</c> — so maps are grouped both ways.</summary>
    readonly Dictionary<string, List<RecordMap>> _bySubfolder;
    readonly Dictionary<string, List<RecordMap>> _byRecordType;

    /// <summary>Every record map, in file order.</summary>
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

    /// <summary>The map(s) fed from an INI subfolder (1 for most; 2 for <c>leveledList</c>). Empty when
    /// the type has no field map yet (surfaced loud by the overlay — never a silent no-op).</summary>
    public IReadOnlyList<RecordMap> ForSubfolder(string subfolder)
        => subfolder is not null && _bySubfolder.TryGetValue(subfolder, out var r) ? r : None;

    /// <summary>The map(s) that patch one Mutagen record type (1 for most; 2 for Race — <c>race/</c> +
    /// <c>raceHook/</c>). The service's routing key: record type → which INI folders can touch it.</summary>
    public IReadOnlyList<RecordMap> ForRecordType(string mutagenType)
        => mutagenType is not null && _byRecordType.TryGetValue(mutagenType, out var r) ? r : None;

    /// <summary>The one map for (subfolder, Mutagen type), or null.</summary>
    public RecordMap? For(string subfolder, string mutagenType)
        => ForSubfolder(subfolder).FirstOrDefault(r => r.RecordType.Equals(mutagenType, StringComparison.OrdinalIgnoreCase));

    // ---- loading -----------------------------------------------------------------------------------

    static SkyPatcherFieldMap? _cached;

    /// <summary>Load the embedded field map (memoized). Throws loudly on a missing/malformed resource —
    /// a map that silently loaded empty would flag every op unmapped (a Q3 silent-degrade).</summary>
    public static SkyPatcherFieldMap Load() => _cached ??= LoadFrom(EmbeddedJson.Read("skypatcher-fieldmap.json", "SkyPatcher field map"));

    /// <summary>Parse a field map from JSON text (also the guard's entry point for a fixture).</summary>
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

        // Wave 2: the per-record FILTER evaluation specs (base filter name → how the overlay evaluates
        // it against the record). Same wrong-kind-throws-loud contract as 'ops'.
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

        // 'path' (one) or 'paths' (several — a filter that matches ANY of a few leaves, e.g. a race's
        // male+female voice). Exactly one of the two must be present.
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
    /// <summary>Literal set of one leaf (scalar / enum / formlink / rename / null-clear mode).</summary>
    Set,
    /// <summary>Multiply the CURRENT leaf value (stateful — order-dependent).</summary>
    Mult,
    /// <summary>Add to the CURRENT leaf value (stateful — order-dependent).</summary>
    AddNumeric,
    /// <summary>Set the leaf from ANOTHER field of the SAME record (e.g. critDamageSetToBase) — self-copy, order-dependent.</summary>
    SetFromOwnField,
    /// <summary>Set one component (X/Y/Z) of a whole-value vector leaf (object bounds P3Int16).</summary>
    VecComponent,
    /// <summary>Set a model path: a literal .nif path sets the leaf; a form value copies the donor's model path (resolver read).</summary>
    ModelPath,
    /// <summary>OR the named flag token(s) into a [Flags] enum leaf.</summary>
    FlagsSet,
    /// <summary>Clear the named flag token(s) from a [Flags] enum leaf.</summary>
    FlagsRemove,
    /// <summary>Set/clear ONE fixed flag (<see cref="OpMap.Flag"/>) from a true/false value (setEssential etc.).</summary>
    FlagBool,
    /// <summary>Add a plain form to a formlink list (keywordsToAdd, formsToAdd).</summary>
    AddForm,
    /// <summary>Remove a plain form from a formlink list.</summary>
    RemoveForm,
    /// <summary>Replace formA with formB in a formlink list (formsToReplace).</summary>
    ReplaceForm,
    /// <summary>Empty the collection (clear=true / clearInventory etc.).</summary>
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
    /// <summary>Set one entry of a numeric-valued dict (<see cref="OpMap.Key"/> — race startingHealth on
    /// Race.Starting[Health]); rides the engine's dict Set.</summary>
    DictSet,
    /// <summary>Multiply one entry of a numeric-valued dict (stateful — order-dependent).</summary>
    DictMult,
    /// <summary>Set ONE channel (<see cref="OpMap.Component"/>: 0=R 1=G 2=B) of a whole-value Color leaf —
    /// the token splice the P3 vector components use, alpha preserved.</summary>
    ColorChannel,
    /// <summary>OR biped-slot INDEX bits (0–31; slot number − 30) into a BipedObjectFlag leaf.</summary>
    BipedSlotsSet,
    /// <summary>Clear biped-slot INDEX bits from a BipedObjectFlag leaf.</summary>
    BipedSlotsRemove,
    /// <summary>Set Book.Teaches to the BookSpell arm holding the given spell (compose-Set through the engine).</summary>
    TeachSpell,
    /// <summary>Set Book.Teaches to the BookSkill arm holding the given skill (compose-Set through the engine).</summary>
    TeachSkill,
    /// <summary>Set matching entries' count to N (changeCobjsCount form~count; 'null' as the form = ALL entries).</summary>
    SetEntryCount,
}

/// <summary>One record type's op → field mappings (keyed by the exact catalog op name) and filter →
/// evaluation specs (keyed by the exact catalog BASE filter name; connectives are the overlay's job).
/// A filter absent from <see cref="Filters"/> is either evaluated built-in by the overlay (the
/// name-keyed families that need no per-record path — primary, keywords, editorid/name contains,
/// hasPlugins, modNames, the skip/override tokens, alternate textures, attached mgefs) or is a
/// COVERAGE GAP the filtermap guard fails on — never a silent skip.</summary>
public sealed record RecordMap(string Subfolder, string RecordType, IReadOnlyDictionary<string, OpMap> Ops,
    IReadOnlyDictionary<string, FilterSpec> Filters);

/// <summary>How the overlay evaluates a per-record mapped filter against the running copy.</summary>
public enum SkyPatcherFilterEval
{
    /// <summary>A single formlink leaf equals ANY listed form. (A single-valued field can't AND a
    /// multi-value list — any-of is the only satisfiable reading; declared assumption, noted loud.)</summary>
    FormEquals,
    /// <summary>A formlink list (or struct list via <see cref="FilterSpec.KeyPath"/>) contains the listed
    /// forms — bare = ALL present, Or = any, Excluded = none (the keyword-filter connective semantics).</summary>
    FormInList,
    /// <summary>An enum leaf equals ANY listed token (valueMap first, then ignore-case member match).</summary>
    EnumEquals,
    /// <summary>ONE fixed flag (<see cref="FilterSpec.Flag"/>) tested against a true/false value;
    /// <see cref="FilterSpec.Invert"/> flips the sense (restrictToBolts=true ⇒ NonBolt NOT set).</summary>
    FlagBool,
    /// <summary>Listed flag tokens tested against a [Flags] enum leaf — bare = all set, Or = any,
    /// Excluded = none.</summary>
    FlagAnyOf,
    /// <summary>NPC gender: token 'female' ⇒ the Female configuration flag set, 'male' ⇒ clear.</summary>
    Gender,
    /// <summary>NPC filterByPCLevelMult: whether the polymorphic Configuration.Level is the
    /// PcLevelMult arm (true) or a static NpcLevel (false).</summary>
    PcLevelMult,
    /// <summary>A string leaf contains the listed substring(s) — the Contains-family connectives.</summary>
    SubstringLeaf,
    /// <summary>Follow <see cref="FilterSpec.LinkPath"/> to a donor record's winner and substring-match
    /// a leaf THERE (an NPC's skeleton path lives on its race).</summary>
    DonorSubstring,
    /// <summary>Follow <see cref="FilterSpec.LinkPath"/> to a donor record's winner and match the
    /// keyword list THERE (a recipe's filterByKeywords matches the CREATED OBJECT's keywords).</summary>
    DonorKeywords,
    /// <summary>A numeric leaf is strictly less than the listed number (filterByWeightLessThan).</summary>
    NumericLess,
    /// <summary>Biped-slot INDICES (0–31; slot number − 30) tested as bits of a BipedObjectFlag leaf.</summary>
    BipedSlots,
    /// <summary>The ORIGIN plugin (FormKey master) of the record a formlink leaf points at, tested
    /// against the listed plugin names (skipRecordByLightingTemplateFromMod — skip semantics ride
    /// <see cref="FilterSpec.Invert"/>). DECLARED ASSUMPTION: "comes from mod X" = the linked form's
    /// defining master, not the plugin that last overrode the link.</summary>
    LinkedOriginPlugin,
}

/// <summary>One filter's evaluation spec. <see cref="Unmapped"/> non-null ⇒ the filter is EXPLICITLY
/// not statically evaluable, with the reason the overlay surfaces loud (line skips filter-unresolved).</summary>
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
    /// <summary>True when this filter is explicitly declared un-evaluable (loud in the overlay).</summary>
    public bool IsUnmapped => Unmapped is not null;
}

/// <summary>One operation's mapping. <see cref="Unmapped"/> non-null ⇒ the op is EXPLICITLY not
/// modelable, with the reason the overlay surfaces loud (all other members are then unset).</summary>
public sealed record OpMap(
    SkyPatcherOpSemantic Semantic,
    string Path,
    int? Component,
    string? SourcePath,
    string? Flag,
    string? FormType,
    bool EqPacked,
    IReadOnlyDictionary<string, string>? ValueMap,
    ElementMap? Element,
    string? Key,
    string? Note,
    string? Unmapped)
{
    internal static OpMap MakeUnmapped(string reason)
        => new(SkyPatcherOpSemantic.Set, "", null, null, null, null, false, null, null, null, null, reason);
    /// <summary>True when this op is explicitly declared un-modelable (loud in the overlay).</summary>
    public bool IsUnmapped => Unmapped is not null;
}

/// <summary>How a struct-entry collection op builds/matches its elements: the Mutagen element type
/// (corpus catalog name, e.g. "ContainerEntry"), the packed-sub-arg → element-sub-field wiring, the
/// key path (the form sub-field remove/replace/match operate on), and the count path (the numeric
/// sub-field by-count removal / objectMultCount operate on).</summary>
public sealed record ElementMap(string Type, IReadOnlyList<ElementField> Fields, string? KeyPath, string? CountPath);

/// <summary>One element sub-field fed from packed sub-arg <see cref="Arg"/> (0-based; after '='-unpacking
/// when the op is eq-packed), with an optional default when the sub-arg is absent.</summary>
public sealed record ElementField(string Path, int Arg, string? Default);
