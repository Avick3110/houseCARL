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
        return new RecordMap(subfolder, recordType, ops);
    }

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
}

/// <summary>One record type's op → field mappings, keyed by the exact catalog op name.</summary>
public sealed record RecordMap(string Subfolder, string RecordType, IReadOnlyDictionary<string, OpMap> Ops);

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
    string? Note,
    string? Unmapped)
{
    internal static OpMap MakeUnmapped(string reason)
        => new(SkyPatcherOpSemantic.Set, "", null, null, null, null, false, null, null, null, reason);
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
