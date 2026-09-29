using System.ComponentModel;
using System.Text.Json;
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
         "Create an EMPTY, HEADER-ONLY plugin — a valid TES4 header with ZERO records and no masters, in a NEW mod " +
         "folder (originals untouched). Its only job is to EXIST so its basename resolves: the artifact that SKSE configs " +
         "binding by plugin basename need (e.g. a CraftingCategories-style trigger that must ship 'Foo.esp' so 'Foo.json' " +
         "loads), a placeholder ESL for FormID reservation, a dummy plugin for another mod to list as a master, or any " +
         "'I just need plugin Foo to be present' case. UNLIKE " + ToolNames.Create + ", it authors NO record — so it adds no conflict-tree footprint " +
         "(no filler override needed to make the plugin non-empty). patch is used EXACTLY (the basename is " +
         "load-bearing — houseCARL will NOT auto-suffix it): if a plugin of that name is already active in the load order, " +
         "or a houseCARL folder of that name already exists, it REFUSES loud rather than rename or overwrite (Q3). Pass " +
         "esl=true for the lightest trigger (a header-only light plugin consumes no consequential load-order slot; with " +
         "zero records the ESL FormID-range rule is trivially satisfied). author/description are optional TES4 header " +
         "text. Returns the plugin path + mod folder — enable it in MO2 to make the basename exist; if it is there to load a " +
         "<stem>.bsa or to reserve a FormID range, its POSITION in the load order still decides that archive's precedence and " +
         "that range. To author actual records, use " +
         ToolNames.Create + " instead.")]
    public static string CreatePlugin(
        LoadOrderService svc,
        [Description("The EXACT plugin name (with or without a trailing .esp/.esm/.esl; e.g. 'Authoria - CraftingCategories'). Used VERBATIM as the basename — houseCARL will not auto-suffix it, because a trigger plugin's whole job is that its basename matches the config bound to it. The written file is '<name>.esp', and a name that already exists as a plugin — .esp, .esm or .esl — anywhere on your install, active or somewhere your order is not loading it, is refused, naming that place and the file.")]
            string patch,
        [Description("When true, flag the plugin as a light master (ESL) — the lightest possible trigger: a header-only ESL consumes no consequential load-order slot. Default false (a normal full plugin).")]
            bool esl = false,
        [Description("Optional. Author text for the TES4 header (the CNAM field). Purely informational.")]
            string? author = null,
        [Description("Optional. Description text for the TES4 header (the SNAM field). Purely informational.")]
            string? description = null) => Guard.Tool(ToolNames.CreatePlugin, () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        if (string.IsNullOrWhiteSpace(patch))
            return "error: patch is empty. Name the plugin to create (a header-only plugin has no record to derive a name from).";
        return RenderCreatePlugin(svc.CreatePlugin(patch, esl, author, description));
    });

    [McpServerTool(Name = ToolNames.CompactPlugin, Title = "Compact / ESL-renumber a plugin's FormIDs"),
     Description(
         "COMPACT a plugin's FormIDs — the data-layer twin of xEdit's \"Compact FormIDs for ESL\". Renumbers EVERY record " +
         "the plugin DEFINES (its originating records — flat AND nested: cells, placed references, dialogue lines, navmesh, " +
         "landscape), repoints every reference WITHIN the plugin, and leaves its overrides of other mods at their master " +
         "FormIDs.\n\n" +
         "The grammar is on the parameters: source= names the target — where it is resolved from, and its refusals; esl= " +
         "the window and the light flag — the ESL ceiling and the override-only lanes; in_place= which file is written — " +
         "the new-plugin default with its MO2 swap, the overwrite lane, and what each does to a LOCALIZED plugin; " +
         "repoint_externals= the external-referencer safety; acknowledge= the consent any in-place rewrite needs; " +
         "patch= the new mod folder.\n\n" +
         "Note: references compiled into Papyrus scripts (.pex hardcoded FormIDs / GetFormFromFile) are NOT remappable — " +
         "verify scripted records after compacting.")]
    public static string CompactPlugin(
        LoadOrderService svc,
        [Description("The plugin's filename to compact (e.g. 'CoolMod.esp'). The target need NOT be active: usually it is in your load order, but a plugin on disk and not (yet) in it — the patch houseCARL just wrote, before the MO2 refresh; a plugin inside a disabled mod — is resolved by filename across ALL mod folders and compacted OFF-ORDER. Whichever lane the target came from, active or not, its declared masters must still be active: a declared master not active is refused loud and nothing is written. The compacted output keeps this EXACT basename. Refuses loud + writes nothing on: this plugin found nowhere on disk / ambiguous across folders / unparseable; a serialize fault.")]
            string source,
        [Description("When true (default), renumber into the light/ESL range (0x800–0xFFF, 2048 IDs) and flag the result a light master (ESPFE) — the canonical 'compact for ESL', which frees a load-order slot. false = renumber contiguously from 0x800 with no light flag or 2048 ceiling (just closes FormID gaps). An override-only plugin with esl=true takes the FLAG-ONLY lane: nothing to renumber, every record copies verbatim, the ESL flag is set (always valid — the light window only constrains originating records); with esl=false there is nothing to do, and that is refused loud with nothing written. Refused loud with nothing written too: with esl=true, MORE records than the light range holds (the hard 2048 ESL ceiling — named, never truncated).")]
            bool esl = true,
        [Description("Optional, default false. IN-PLACE LANE (opt-in): OVERWRITE the original plugin with its compacted form (xEdit's norm) instead of writing a new file — NO houseCARL backup or undo (keep your own). Rides the in-place consent: requires acknowledge=true. OMIT (the default) to write a NEW plugin instead, keeping the SOURCE'S EXACT basename (so other mods that list it as a master still resolve) in a fresh houseCARL mod folder, leaving the original untouched: review the new plugin in xEdit, then in MO2 enable its folder and DISABLE the original mod (same basename — MO2 serves one). LOCALIZED PLUGINS: houseCARL does not rewrite one in place — its text lives in separate .STRINGS files it cannot swap together with the plugin — so a LOCALIZED plugin is REFUSED in this lane whatever arrangement its .STRINGS files are in. The new-plugin lane still compacts it — UNLESS its .STRINGS resolve NOWHERE (houseCARL can see them nowhere, or the folder holding them cannot be read), which that lane refuses too: every name, description and message would read back EMPTY (or, from a folder nothing could open, unknowable) in a plugin with nothing left in it to tell that text from one that never had any. Otherwise the output is DE-LOCALIZED — the text this read resolved is written into the plugin itself and the source's .STRINGS files no longer describe it — and the report says so. The review step above is where you catch that: read the output's TEXT before swapping it in. ONE MORE REFUSAL on this lane, before the consent gate and whatever acknowledge says: if the external-reference pass could not READ some plugin, houseCARL cannot tell whether it references records about to be renumbered, so any in-place rewrite (the target, its referencers, or both) is refused and nothing is written — the new-plugin lane carries that as a note instead.")]
            bool in_place = false,
        [Description("Optional, default false. THE SAFETY (Q3): renumbering breaks any reference from OUTSIDE this plugin (they'd point at FormIDs that vanish), so whenever there is anything to renumber houseCARL scans the WHOLE load order for such external referencers (a one-pass walk — can take ~25s on a big order): if NONE, it's a clean compaction; if SOME, the call REFUSES (listing them) by default. Set true to ALSO rewrite those external referencers IN PLACE to follow the renumber (requires in_place=true, so the target and its referrers move together, AND acknowledge=true; no backup of them either). Refused when any referencer is LOCALIZED or is one houseCARL CANNOT READ — it rewrites neither a localized plugin nor one it cannot read in place — and the default refusal above says which referencers are in which state, and where a localized one's text is, so you learn that BEFORE choosing this flag rather than being sent here.")]
            bool repoint_externals = false,
        [Description("Optional, default false. Confirms the in-place trade-off when in_place=true OR repoint_externals=true (your original file(s) get rewritten, no backup). The FIRST such call without it returns a CONFIRM prompt listing exactly what will be overwritten — re-call with acknowledge=true to proceed.")]
            bool acknowledge = false,
        [Description("Optional. Base name for the NEW mod folder (new-file lane only; auto-suffixed if taken). Ignored with in_place=true. The PLUGIN inside ALWAYS keeps the source's exact basename so external masters still resolve.")]
            string? patch = null) => Guard.Tool(ToolNames.CompactPlugin, () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        if (string.IsNullOrWhiteSpace(source))
            return "error: source is empty. Name the plugin filename to compact (e.g. 'CoolMod.esp').";
        return RenderCompact(svc.CompactPlugin(source, esl, in_place, repoint_externals, acknowledge, patch));
    });

    [McpServerTool(Name = ToolNames.MergePlugins, Title = "Merge plugins into one new plugin"),
     Description(
         "MERGE one or more ACTIVE plugins into ONE NEW plugin — a RECORDS operation (the zMerge/'Merge Plugins' job): the " +
         "donors' records combine under a new filename; the donor FILES and their mods are NEVER touched (new-file lane only, " +
         "no in-place).\n\n" +
         "THE NORMAL JOB: merge a FAMILY OF PATCHES into one plugin and leave the mods they patch alone and active — that is " +
         "what reclaims load-order slots without changing what any mod does. Merging a base mod TOGETHER WITH its own patches " +
         "is a narrow case, not the shape to reach for: it moves that mod's records to a new plugin identity, so everything " +
         "else that patches or references it must be merged or repointed too.\n\n" +
         "The grammar is on the parameters: plugins= holds the donor set — the single-donor rename lane, the renumber and " +
         "cross-donor conflict rules, the outside-referencer warning, and the donor refusals; patch= the new mod folder, whose " +
         "name the merged plugin inside it takes — the asset carry and what it costs existing saves.\n\n" +
         "AFTER: review the merged plugin in xEdit, enable its mod folder in MO2, then deactivate the donor PLUGINS (right " +
         "pane) but KEEP the donor MOD FOLDERS enabled (left pane) — merge carries only the FormID-keyed files the rename " +
         "breaks (facegen/voice/seq); every other donor asset (meshes, textures, scripts, BSA contents) is still referenced " +
         "BY PATH from the merged records and loads from the donor folders. Caveat: a donor .bsa stops auto-loading once its " +
         "same-named plugin is inactive — extract it into the mod folder (" + ToolNames.BsaExtract + ") or load it via a same-named " +
         "dummy plugin (" + ToolNames.CreatePlugin + ").\n\n" +
         "A donor's HEADER mostly does not come along: master (ESM) status and Author/Description are always dropped, and the " +
         "report names each drop. Light (ESL) status is carried only when every donor was light and every merged object id fits " +
         "the light window 0x800–0xFFF; otherwise the report says which of the two reasons it was, and whether " +
         ToolNames.CompactPlugin + " can still make it light.")]
    public static string MergePlugins(
        LoadOrderService svc,
        [Description("The donor plugin filenames to merge (at least one, e.g. [\"CoolMod.esp\", \"CoolMod Patch.esp\"]) — each must be active in your load order: a donor not active is refused loud and nothing is written. This is a SET: a name repeated is still one donor. A SINGLE donor renames it into patch=: with nothing to combine the merge IS a rename — the same records under a new plugin name, keeping every object id already inside the writable range (nothing can collide; an id BELOW the 0x800 floor still renumbers, and the per-donor line reports it), facegen/voice/seq carried to the new name. Argument order does not matter: houseCARL uses LOAD order for id priority and conflict resolution. RENUMBER is collision-first (zMerge's default): the donor EARLIEST in the load order keeps its FormID object ids; later donors renumber ids already taken, and ANY donor's ids below the 0x800 floor renumber too (all records necessarily move to the new plugin's identity). Cross-donor conflicts on the SAME record resolve to the LOAD-ORDER WINNER and are each REPORTED; a losing donor's nested children the winner doesn't re-list (a base mod's dialogue lines under a patched topic; placed refs under a patched cell) are GRAFTED into the winner's copy, which is what lets a patch family merge cleanly even where one patch relists a topic another one fills. THE SAFETY (Q3): plugins OUTSIDE the merge that reference or override donor records are WARNED and NAMED, never refused — the donors stay active until you swap in MO2, so nothing breaks at write time; the remedy is to include those patches in the merge set or re-point them before disabling the donors. Refuses loud + writes nothing on: a donor unparseable / not on disk; a dangling donor-internal reference (a donor referencing a FormID in donor space that no donor holds); a declared master not active. Each of those is refused before anything is written. A record INJECTED into one donor's FormID space and carried by another is merged like any other record, renumbered into the output; a plugin outside the merge carrying it too is WARNED about by name, as every external overrider is.")]
            string[] plugins,
        [Description("Base name for the NEW mod folder this merge creates (e.g. 'MyMerge'); the merged plugin inside it takes that name too, so patch='MyMerge' writes 'houseCARL - MyMerge\\MyMerge.esp'. A name already taken is REFUSED by name and nothing is written — never auto-suffixed, because the merged plugin's exact basename is what a _DISTR.ini, a _KID.ini, a config or a dependent's master entry binds to. That covers a mod folder 'houseCARL - MyMerge' already under your mods directory, an output plugin already in your load order, and a name that exists somewhere your order is not loading it (another mod folder, the overwrite folder, or game Data) — each refusal naming what is in the way. The donors keep their names and files untouched. ASSETS follow the renumber into this name: every donor NPC's facegen and every voiced line are carried into the new plugin-name folders (those paths embed the plugin NAME, so ALL donor facegen/voice moves, not just collisions), and a .seq is refreshed when any donor shipped one. Existing SAVES that depend on the donors will NOT survive (the records now live under this plugin name, and any id that had to be renumbered moved with it) — best for a new game.")]
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
