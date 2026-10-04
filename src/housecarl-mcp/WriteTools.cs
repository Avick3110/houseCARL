using System.ComponentModel;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using HousecarlCore;

namespace HousecarlMcp;

/// <summary>The plugin-level write tools — create a header-only plugin, compact a plugin's FormIDs, merge plugins into
/// one — each taking a whole plugin FILE as its subject, riding its own core builder rather than the record-edit path,
/// and writing a NEW plugin except on compact's <c>in_place=</c> lane.
/// <para>The RENDER helpers every write tool calls are the other half of this class, in WriteTextRender.cs.</para></summary>
[McpServerToolType]
public static partial class WriteTools
{
    [McpServerTool(Name = ToolNames.CreatePlugin, Title = "Create an empty header-only (trigger) plugin"),
     Description(
         "Create an empty, header-only plugin (no records, no masters) in a new mod folder, so that a plugin of that name " +
         "exists: a trigger an SKSE config binds to by basename (e.g. 'Foo.esp' so 'Foo.json' loads), a dummy plugin " +
         "another mod lists as a master, a same-named plugin to load a <name>.bsa, or a placeholder ESL reserving a FormID " +
         "range. It authors no record, so it adds nothing to any conflict tree; to author records use " + ToolNames.Create +
         ". Returns the plugin path and mod folder; enable it in MO2. Its load-order position still decides a same-named " +
         ".bsa's precedence and a reserved range.")]
    public static string CreatePlugin(
        LoadOrderService svc,
        [Description("The plugin name, used exactly, with or without .esp/.esm/.esl (e.g. 'Authoria - CraftingCategories'); the file written is always '<name>.esp'. Never auto-suffixed: a name already used by a plugin (.esp, .esm or .esl) anywhere on your install, active or not, or by a houseCARL mod folder, is refused.")]
            string patch,
        [Description("When true, flag the plugin light (ESL), so it takes no full load-order slot. Default false.")]
            bool esl = false,
        [Description("Optional. Author text for the TES4 header (CNAM).")]
            string? author = null,
        [Description("Optional. Description text for the TES4 header (SNAM).")]
            string? description = null) => Guard.Tool(ToolNames.CreatePlugin, () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        if (string.IsNullOrWhiteSpace(patch))
            return "error: patch is empty. Name the plugin to create (a header-only plugin has no record to derive a name from).";
        return RenderCreatePlugin(svc.CreatePlugin(patch, esl, author, description));
    });

    [McpServerTool(Name = ToolNames.CompactPlugin, Title = "Compact / ESL-renumber a plugin's FormIDs"),
     Description(
         "Compact a plugin's FormIDs, the data-layer twin of xEdit's \"Compact FormIDs for ESL\". Renumbers every record " +
         "the plugin defines (flat and nested: cells, placed references, dialogue lines, navmesh, landscape), repoints every " +
         "reference within the plugin, and leaves its overrides of other mods at their masters' FormIDs. When anything is renumbered " +
         "it first walks the whole load order for plugins that reference those records (see repoint_externals=). FaceGen and " +
         "voice files keyed to a renumbered record are carried beside the output, and a .seq is rebuilt from the output when " +
         "the source shipped one; the report says what was carried.\n\n" +
         "References compiled into Papyrus scripts (hardcoded FormIDs, GetFormFromFile) are not remapped: verify scripted " +
         "records after compacting.")]
    public static string CompactPlugin(
        LoadOrderService svc,
        [Description("The plugin filename to compact (e.g. 'CoolMod.esp'). It need not be active: a plugin not in the load order (a patch not yet enabled, one in a disabled mod) is found on disk by filename and compacted off-order. Either way its declared masters must be active. The output keeps this exact basename.")]
            string source,
        [Description("Default true: renumber into the light range 0x800–0xFFF (2048 ids) and flag the result light (ESL), freeing a load-order slot. false: renumber contiguously from 0x800 with no light flag or ceiling, just closing gaps. An override-only plugin with esl=true has nothing to renumber: every record is copied as is and the light flag is set.")]
            bool esl = true,
        [Description("Default false: write a new plugin with the source's exact basename (so plugins that list it as a master still resolve) in a fresh houseCARL mod folder, leaving the original untouched. Review it, then in MO2 enable its folder and disable the original mod. true: overwrite the original with no houseCARL backup or undo; needs acknowledge=true. A localized plugin is refused in place; the new-plugin lane compacts it but writes its text into the plugin itself (no longer localized), and the report says so. If the external-reference scan could not read some plugin, any in-place rewrite is refused; the new-plugin lane reports it as a note.")]
            bool in_place = false,
        [Description("Default false: if any plugin outside the target references a record being renumbered, the call is refused and lists them. true: also rewrite those referencers in place to follow the renumber, with no backup; needs in_place=true and acknowledge=true. Refused when a referencer is localized or could not be read; the default refusal already says which ones are.")]
            bool repoint_externals = false,
        [Description("Default false. Consent for an in-place rewrite (in_place=true or repoint_externals=true). Without it the call returns a prompt listing every file that would be overwritten; call again with acknowledge=true to proceed.")]
            bool acknowledge = false,
        [Description("Optional. Base name for the new mod folder (auto-suffixed if taken); ignored with in_place=true. The plugin inside always keeps the source's basename.")]
            string? patch = null) => Guard.Tool(ToolNames.CompactPlugin, () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        if (string.IsNullOrWhiteSpace(source))
            return "error: source is empty. Name the plugin filename to compact (e.g. 'CoolMod.esp').";
        return RenderCompact(svc.CompactPlugin(source, esl, in_place, repoint_externals, acknowledge, patch));
    });

    [McpServerTool(Name = ToolNames.MergePlugins, Title = "Merge plugins into one new plugin"),
     Description(
         "Merge one or more active plugins into one new plugin (the zMerge / Merge Plugins job): the donors' records combine " +
         "under a new filename in a new mod folder. The donor files and their mods are never touched; there is no in-place " +
         "lane.\n\n" +
         "The normal job is merging a family of patches and leaving the mods they patch active, which frees load-order slots " +
         "without changing what any mod does. Merging a base mod together with its patches moves its records to a new plugin " +
         "identity, so everything else that patches or references it must be merged or repointed too.\n\n" +
         "After: review the merged plugin, enable its mod folder in MO2, then deactivate the donor plugins but keep the donor " +
         "mod folders enabled. Merge carries only the files keyed to the plugin name (facegen, voice, .seq); every other " +
         "donor asset (meshes, textures, scripts, BSA contents) still loads from the donor folders. A donor .bsa stops loading " +
         "once its same-named plugin is inactive: extract it into the mod folder (" + ToolNames.BsaExtract + ") or load it with " +
         "a same-named dummy plugin (" + ToolNames.CreatePlugin + ").\n\n" +
         "Master (ESM) status and Author/Description are never carried from a donor header; the report names each drop. " +
         "Light (ESL) status is carried only when every donor was light and every merged object id fits 0x800–0xFFF; " +
         "otherwise the report says which reason applied and whether " + ToolNames.CompactPlugin + " can still make it light.")]
    public static string MergePlugins(
        LoadOrderService svc,
        [Description("The donor plugin filenames, at least one, each active in your load order (e.g. [\"CoolMod.esp\", \"CoolMod Patch.esp\"]). A set: order and repeats do not matter; load order decides id priority and conflicts. One donor is a rename: the same records under the new name, keeping every object id at or above 0x800. With several, the donor earliest in the load order keeps its object ids, later donors renumber only ids already taken, and any id below 0x800 renumbers. A record several donors carry resolves to the load-order winner, and each such conflict is reported; nested children the winner does not list (dialogue lines under a topic, references under a cell) are grafted into the winner's copy. A record injected into one donor's FormID space and carried by another is merged and renumbered like any other. Plugins outside the merge that depend on a donor are named as warnings, never refused, before you disable the donors: one that references donor records, include it in the merge or repoint it; one that overrides them, include it or rebuild it against the output; one that only lists a donor as a master, remove that master.")]
            string[] plugins,
        [Description("Name of the new mod folder; the merged plugin takes it too, so patch='MyMerge' writes 'houseCARL - MyMerge\\MyMerge.esp'. Never auto-suffixed, since configs, INIs and dependents bind to the exact basename: a name already used by a mod folder, an active plugin, or a plugin anywhere your order is not loading it is refused. Every donor NPC's facegen and every voiced line move to folders under this name, and a .seq is refreshed when any donor shipped one. Saves that depend on the donors will not survive the switch; best for a new game.")]
            string patch) => Guard.Tool(ToolNames.MergePlugins, () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        return RenderMerge(svc.MergePlugins(plugins, patch));
    });
}

// ---- the retired 1.x wire DTOs: parked in WireNamesScan.NonInputWireTypes, reachable from no tool's input schema ----

/// <summary>One edit operation off the wire, mirroring <see cref="WritePatchBuilder.PatchEdit"/>; RecordType is derived
/// from the resolved winner's runtime type rather than supplied.</summary>
public sealed record BulkOp
{
    [JsonPropertyName("formid"), Description("The record's FormID 'XXXXXX:Plugin.esp'.")]
    public string? Formid { get; init; }

    [JsonPropertyName("field_path"), Description("Dotted field path, e.g. 'BasicStats.Damage' or 'Entries'. Step into a list/dict element mid-path with brackets, e.g. 'Effects[0].Data.Magnitude'; at the LEAF use verb + key, not brackets.")]
    public string? FieldPath { get; init; }

    [JsonPropertyName("verb"), Description(WriteVerbs.AllRecital + " (deep-copy the field at field_path from from_plugin's version — see from_plugin). SetAtIndex OVERWRITES the element at key=; InsertAtIndex inserts a new one AT key= and shifts the rest right (key = the list's length appends).")]
    public string Verb { get; init; } = "Set";

    [JsonPropertyName("value"), Description("The value (coerced to the field's type). Omit for Remove / ReplaceAll / Merge / compose.")]
    public string? Value { get; init; }

    [JsonPropertyName("key"), Description("Dict key or list index at the leaf.")]
    public string? Key { get; init; }

    [JsonPropertyName("values"), Description("The whole new list for a list ReplaceAll.")]
    public string[]? Values { get; init; }

    [JsonPropertyName("entries"), Description("Key→value pairs for a dict Merge or dict ReplaceAll.")]
    public Dictionary<string, string>? Entries { get; init; }

    [JsonPropertyName("compose"), Description("Build a modeled struct: an arm for a polymorphic Set, or the element for a struct-element Add / InsertAtIndex / SetAtIndex (e.g. a leveled-list entry; for a polymorphic list like VMAD Scripts[i].Properties, the element's CONCRETE arm type, e.g. 'ScriptObjectProperty').")]
    public StructInput? Compose { get; init; }

    [JsonPropertyName("composes"), Description("Build MANY modeled list elements in ONE op — the batch sibling of compose (each entry the same {type, fields?, ctor_args?, sets?} shape). With verb=Add, APPENDS each element in order (e.g. 10 leveled-list entries, a whole block of condition rows in one op instead of ten Adds). With verb=ReplaceAll, CLEARS the list then appends each — the way to replace a whole modeled list (conditions, effects, entries); pass composes=[] with ReplaceAll to CLEAR the list to empty (the modeled twin of values=[]). LIST elements only; mutually exclusive with compose/value/values. All-or-nothing: a bad element refuses the whole call with per-element (composes[i]) reasons.")]
    public StructInput[]? Composes { get; init; }

    [JsonPropertyName("from_plugin"), Description("For verb=\"CopyFrom\" ONLY: the plugin whose version of THIS record to deep-copy the field at field_path from — an ACTIVE plugin, OR a plugin FILE on disk that isn't in the load order (e.g. a disabled OLD patch you want to re-assert a field from). CopyFrom takes no value/values/entries/compose/composes — the source IS from_plugin's version of the field. Honors forward-then-edit precedence: into= a patch that already carries the record copies onto the patch's own version. Copies a WHOLE field's value (scalar, formlink, modeled list, sub-struct); it can't copy owned child records (forward the whole record with " + ToolNames.Forward + " instead).")]
    public string? FromPlugin { get; init; }
}

/// <summary>One brand-new record to create off the wire — the retired 1.x batch element: the declared record_type, its
/// editorid, optional field operations, and the optional nested parent and collection.</summary>
public sealed record CreateOp
{
    [JsonPropertyName("record_type"), Description("The kind of record to create: a catalog name ('Keyword', 'Spell', 'DialogTopic', 'DialogResponses', 'PlacedObject') or a 4-char signature.")]
    public string? RecordType { get; init; }

    [JsonPropertyName("editorid"), Description("REQUIRED. The EditorID the new record is referenced by. A nested child's parent= can name this editorid (a same-call sibling parent).")]
    public string? Editorid { get; init; }

    [JsonPropertyName("operations"), Description("Optional. The new record's fields, same shape as " + ToolNames.Apply + " ops but with NO formid (and no from_plugin — there is no other version to copy from yet): {field_path, verb?, value?, key?, values?, entries?, compose?, composes?}.")]
    public BulkOp[]? Operations { get; init; }

    [JsonPropertyName("parent"), Description("Optional. For a NESTED record: the parent it nests under — an EXISTING parent's FormID 'XXXXXX:Plugin.esp', OR the editorid of a record declared EARLIER in this same records array (a same-call sibling). Omit for a flat top-level record.")]
    public string? Parent { get; init; }

    [JsonPropertyName("collection"), Description("Optional. Which of the parent's child slots to add into, BY NAME — a child list (e.g. a cell's 'Persistent') or a single-child slot (a cell's 'Landscape', a worldspace's 'TopCell') — needed only when more than one fits. Omit when unique or when parent is omitted.")]
    public string? Collection { get; init; }

    [JsonPropertyName("grid"), Description("Optional. For an EXTERIOR cell only (record_type 'Cell' with parent= a Worldspace): the cell's grid as \"X,Y\" (e.g. \"5,-12\"). houseCARL files it into the worldspace's block tree by block=floor(grid/32), subblock=floor(grid/8). A 'Cell' with NO parent and NO grid is an INTERIOR cell (self-files by FormID). Ignored for non-Cell types.")]
    public string? Grid { get; init; }
}

/// <summary>A modeled struct built from parts (wire shape of <see cref="StructSpec"/>): the concrete type, flat
/// coercible sub-fields, positional ctor args, and nested edits applied to the built struct.</summary>
public sealed record StructInput
{
    [SchemaRequired, JsonPropertyName("type"), Description("The concrete catalog type to build (arm type for a polymorphic Set; the collection's element type for an Add, e.g. 'LeveledItemEntry'; or a polymorphic element's concrete ARM, e.g. 'ScriptObjectProperty' into VMAD Properties).")]
    public string? Type { get; init; }

    [JsonPropertyName("fields"), Description("Flat coercible sub-fields set directly on the struct: name → value.")]
    public Dictionary<string, string>? Fields { get; init; }

    [JsonPropertyName("ctor_args"), Description("Positional constructor args, for struct types that require them.")]
    public string[]? CtorArgs { get; init; }

    [JsonPropertyName("sets"), Description("Nested edits applied to the built struct (paths rooted at it), each {path, verb?, value?, key?, compose?} — e.g. {path:'Data.Reference', value:'<FormID>'}.")]
    public NestedSet[]? Sets { get; init; }
}

/// <summary>One nested edit inside a <see cref="StructInput"/> (a path+verb+value rooted at the struct being built).</summary>
public sealed record NestedSet
{
    [SchemaRequired, JsonPropertyName("path"), Description("Dotted path within the struct, e.g. 'Data.Level'.")]
    public string? Path { get; init; }

    [SchemaValues(SchemaVocabulary.ComposeVerbs), JsonPropertyName("verb"), Description(WriteVerbs.InComposeRecital + ". The nested write runs through the same verb engine an op does, so the verb is chosen by the nested target's own cardinality. The three verbs the op surface has and this one does not each read an input slot a nested set has no member for — ReplaceAll's values=, Merge's entries=, CopyFrom's source record — and each is refused here by name rather than consuming nothing: set a collection's elements or a dict's entries one at a time, and copy a field from another record with " + ToolNames.Apply + "'s CopyFrom op.")]
    // Nullable so the generator types it ["string","null"]; the gate reads an absent or null verb as Set.
    public string? Verb { get; init; } = "Set";

    [JsonPropertyName("value"), Description("The value (coerced).")]
    public string? Value { get; init; }

    [JsonPropertyName("key"), Description("Dict key or list index, if the nested target is a collection.")]
    public string? Key { get; init; }

    [JsonPropertyName("compose"), Description("Build a modeled sub-struct for THIS nested target (recursive): the concrete ARM of a polymorphic sub-field (e.g. a Condition's Data → 'GetActorValueConditionData'), or the element for a struct-element Add / InsertAtIndex / SetAtIndex nested inside the struct. Omit for a coercible scalar (use value=).")]
    public StructInput? Compose { get; init; }
}
