using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;

namespace HousecarlMcp;

/// <summary>housecarl_create — the record-authoring surface: <c>records=</c> × the lane × transport, over
/// <see cref="LoadOrderService.CreateRecordsBatch"/>; <c>records=</c> takes the inline array or the @file spelling,
/// both through the strict <see cref="ListParams"/> reader, which refuses an undeclared member BY NAME.</summary>
[McpServerToolType]
public static class CreateTools
{
    [McpServerTool(Name = ToolNames.Create, Title = "Create brand-new records"),
     Description(
         "Create new records (new FormIDs) and write them to a new patch plugin, leaving originals untouched by " +
         "default; the companion to " + ToolNames.Apply + ", which edits existing records. One call takes what to " +
         "author (records=), where it lands (a new patch with patch=, an existing houseCARL patch with into=, or an " +
         "existing plugin's own file with in_place=, acknowledge= and replace=) and how it reads back (readback=, " +
         "format=, max_chars=).\n\n" +
         "All or nothing: if any spec is malformed or fails pre-flight, the whole call is refused with a reason per " +
         "record and nothing is written.\n\n" +
         "Editing an existing record's fields is " + ToolNames.Apply + "; dropping a whole record is " + ToolNames.Remove +
         "; an empty trigger plugin with no records is " + ToolNames.CreatePlugin + ". Read first with " +
         ToolNames.Records + ".")]
    public static string Create(
        LoadOrderService svc,
        [Description("The records to author, all into one plugin: an array of {record_type, editorid, ops?, parent?, collection?, grid?}, or \"@<absolute path>\" naming a JSON file that holds the same array, read when the call runs. Each new record's FormID is allocated in the receiving plugin from 0x800 up and reported back; to make another record point at it later, pass that FormID as a value to this tool or " + ToolNames.Apply + " on the same lane (into='<this patch>', or in_place='<that plugin>'). The reply also reports: for dialogue lines, voice coverage (a response with no .fuz plays silent in game) and result-script binding (an unwired or uncompiled script runs nothing); for cells, the world content houseCARL does not author (lighting, terrain, water, navmesh). Creating dialogue and quest records auto-fills the Creation Kit's bookkeeping and reports each fill: the INFO's FavorLevel (CNAM) and Flags (ENAM), a DLVW's DNAM and ENAM, a DLBR's Category (TNAM), the DIAL's Priority (PNAM) and SubtypeName (SNAM, derived from Subtype), and the QUST's NextAliasID (ANAM), each objective's and each alias's Flags (FNAM), and each reference alias's VoiceTypes (VTCK). A value you pass always wins over a fill. What no fill decides (which topic a generic greeting reaches, the PNAM a re-listed INFO needs to keep its place, the branch and INFO flags left to you) is in " + ReadSentences.DialogueDocUrl + ".")]
            JsonElement? records = null,
        [Description("LANE: base filename for the new patch this call writes (default 'Patch'); auto-suffixed if taken, so a prior patch is never overwritten.")]
            string? patch = null,
        [Description("LANE: filename of an existing houseCARL patch to add these records to instead of writing a fresh one, to build one patch across calls. A parent created in an earlier into= call can be the parent here. A flat record whose editorid this patch already defines is re-created at the FormID it already has. Found by the plugin's filename even if its MO2 mod folder was renamed; when two patches share a filename, pass the mod-folder name instead.")]
            string? into = null,
        [Description("LANE (opt-in): the filename of an existing active plugin to create these records straight into, e.g. \"CoolWeapons.esp\", including one houseCARL did not author. Your original file is rewritten, with no houseCARL backup or undo; keep your own. Nesting works here too: a parent the target owns hosts the child, and a parent from another plugin is overridden in.")]
            string? in_place = null,
        [Description("Confirms the in-place trade-off for the plugin in_place= names. Needed only on the first in-place write to that plugin (edit, create, remove or forward), until one has landed; a refused call records nothing. Without it, that first call returns a confirmation prompt instead of writing. It waives consent only; the record verify still runs.")]
            bool acknowledge = false,
        [Description("Overwrite a record the in_place= target already defines under an editorid in records=: it is re-created fresh at its own FormID from this call's spec, and everything else it held is discarded. Without it, such a collision refuses the whole call before anything is written.")]
            bool replace = false,
        [Description("TRANSPORT: show the full field-by-field dump of every record this call created, not just the fields you set, to confirm composed structures landed without enabling the patch. In place, the verify always runs and shows compactly; this widens it. The read-back is the written file's content, not load-order truth: a new patch wins nothing until enabled in MO2, and a write into an existing mod keeps that mod's priority and may still need sorting above the current winner.")]
            bool readback = false,
        [Description("TRANSPORT: 'text' (default) | 'json' (the same data, machine-readable). A reply answered from an index build carries its epoch, the identity of the build parents and links resolved from: epoch=<hex> on 'text', an 'epoch' member on 'json'. A refusal that consulted no build carries none.")]
            string? format = null,
        [Description("TRANSPORT: character ceiling on the whole reply: the created-record rows with their FormIDs, the voice, result-script and cell reports, then the read-back, in that order. Past it, trailing rows are dropped with a notice and a rendered-versus-total count per block. The write is unaffected. 0 = a default kept under the host's per-response limit.")]
            int max_chars = 0) => Guard.Tool(ToolNames.Create, () =>
    {
        // ---- TRANSPORT: format --------------------------------------------------------------------------
        // Resolved BEFORE the unconfigured-MO2 prompt; contract in docs/architecture/write-path.md.
        bool json = Wire.WantsJson(format, out var ferr);
        if (ferr is not null) return ferr;   // the format value itself is unparsed — there is no known render to answer in
        if (svc.ConfigPromptOrNull() is { } prompt)
            return json ? JsonWire.RenderError(prompt, null) : prompt;

        string Refuse(string message) => json ? JsonWire.RenderError(message, null) : "error: " + message;

        // ---- LANE: the three destinations are mutually exclusive, and a dropped one is named ------------
        // A lane is honoured or refused BY NAME; contract in docs/architecture/write-path.md.
        var patchName = string.IsNullOrWhiteSpace(patch) ? null : patch.Trim();
        bool hasPatch = patchName is not null;
        bool hasInto = !string.IsNullOrWhiteSpace(into);
        bool hasInPlace = !string.IsNullOrWhiteSpace(in_place);
        if (hasInto && hasInPlace)
            return Refuse("into= and in_place= are different lanes — into= ADDS to a houseCARL patch, in_place= writes the records into an existing plugin's own file. Name one.");
        if (hasPatch && hasInto)
            return Refuse($"patch='{patch}' names a NEW patch to write, but into='{into}' extends an existing one — the two lanes are exclusive. Drop patch= to extend, or drop into= to write fresh.");
        if (hasPatch && hasInPlace)
            return Refuse($"patch='{patch}' names a NEW patch to write, but in_place='{in_place}' writes into that plugin's own file — the two lanes are exclusive. Drop patch= to create in place, or drop in_place= to write a patch.");
        if (acknowledge && !hasInPlace)
            return Refuse("acknowledge= confirms the in-place trade-off and is meaningless without in_place=<plugin filename>. Drop it, or name the file to write into.");
        if (replace && !hasInPlace)
            return Refuse("replace= overwrites a record the in-place target already defines under the same editorid and is meaningless without in_place=<plugin filename>. Drop it, or name the file to write into.");

        // ---- records= -----------------------------------------------------------------------------------
        if (records is not { } recEl || recEl.ValueKind is JsonValueKind.Null)
            return Refuse("nothing to create. Pass records=[{record_type, editorid, ops?, parent?, collection?}, …] " +
                          "(or records=\"@<absolute path>\") — one record is a set of one.");
        var (specs, rerr) = ListParams.Read<CreateRecordSpec>(recEl, "records", "{record_type, editorid, ops?, parent?, collection?, grid?}");
        if (rerr is not null) return Refuse(rerr);

        // ---- Map the wire shapes onto the engine's inputs -----------------------------------------------
        // A rename over the same engine inputs: ops -> operations, op -> verb.
        var wire = new List<CreateOp>(specs!.Length);
        for (int i = 0; i < specs.Length; i++)
        {
            var s = specs[i];
            // A null ELEMENT inside ops= is legal JSON, and the strict reader checks the TOP-level list only.
            BulkOp[]? ops = null;
            if (s.Ops is { } opsIn)
            {
                ops = new BulkOp[opsIn.Length];
                for (int j = 0; j < opsIn.Length; j++)
                {
                    if (opsIn[j] is not { } o)
                        return Refuse($"records[{i}]: ops[{j}] is null — every op must be an object, e.g. {{\"field_path\": \"Name\", \"value\": \"…\"}}. (A JSON null in the array is not an empty op; drop the element.)");
                    ops[j] = new BulkOp
                    {
                        FieldPath = o.FieldPath, Verb = o.Op ?? "Set", Value = o.Value, Key = o.Key,
                        Values = o.Values, Entries = o.Entries, Compose = o.Compose, Composes = o.Composes,
                    };
                }
            }
            wire.Add(new CreateOp
            {
                RecordType = s.RecordType, Editorid = s.Editorid, Parent = s.Parent,
                Collection = s.Collection, Grid = s.Grid,
                Operations = ops,
            });
        }

        var outcome = svc.CreateRecordsBatch(wire, patchName, into, readback, in_place, hasInPlace, acknowledge, replace);
        // The lane the CALL named; contract in docs/architecture/write-path.md.
        return json
            ? JsonWire.RenderCreateOutcome(outcome, max_chars, readback, hasInPlace ? "in_place" : hasInto ? "into" : "patch")
            : WriteTools.RenderCreate(outcome, max_chars, readback);
    });
}

// ---- wire DTOs (the create shapes) ---------------------------------------------------------------------

/// <summary>One brand-new record off housecarl_create's wire — <see cref="CreateOp"/> with this surface's
/// vocabulary: <c>operations</c> is <c>ops</c>, and each op is a <see cref="CreateFieldOp"/> whose verb member is
/// <c>op</c>.</summary>
public sealed record CreateRecordSpec
{
    [SchemaRequired, JsonPropertyName("record_type"), Description("The kind of record to create: a catalog name ('Keyword', 'Spell', 'Weapon', 'LeveledItem', 'DialogTopic', 'PlacedObject') or a 4-char signature ('KYWD'). Flat top-level records and nested ones (see parent=) both work. For a global or game setting, name the concrete subtype: 'GlobalFloat'/'GlobalInt'/'GlobalShort' or 'GameSettingFloat'/'GameSettingInt'/'GameSettingString'.")]
    public string? RecordType { get; init; }

    [SchemaRequired, JsonPropertyName("editorid"), Description("The EditorID the new record is referenced by (in SkyPatcher, SPID, xEdit); choose a clear, prefixed name.")]
    public string? Editorid { get; init; }

    [JsonPropertyName("ops"), Description("The new record's fields, in the same op shape " + ToolNames.Apply + " takes, minus formid. e.g. ops=[{field_path:'Name', value:'My Spell'}, {field_path:'Effects', op:'Add', compose:{...}}]. Omit to create a bare record (type and editorid only).")]
    public CreateFieldOp[]? Ops { get; init; }

    [JsonPropertyName("parent"), Description("For a nested record: the parent it nests under (a dialogue line under a topic, a placed ref in a cell), either an existing parent's FormID 'XXXXXX:Plugin.esp' or the editorid of a record declared earlier in this records= array. That is how a topic and its lines are authored in one call: records=[{record_type:'DialogTopic', editorid:'MyTopic'}, {record_type:'DialogResponses', editorid:'MyTopic_L1', parent:'MyTopic', ops:[{field_path:'Prompt', value:'Hello'}]}]. Omit for a flat top-level record.")]
    public string? Parent { get; init; }

    [JsonPropertyName("collection"), Description("Which of the parent's child slots to add into, by name: a child list (a cell's 'Persistent' or 'Temporary') or a single-child slot (a cell's 'Landscape', a worldspace's 'TopCell'). Needed only when more than one fits; omit when unique or with no parent.")]
    public string? Collection { get; init; }

    [JsonPropertyName("grid"), Description("For an exterior cell only (record_type 'Cell' with parent= a Worldspace): the cell's grid as \"X,Y\" (e.g. \"5,-12\"); houseCARL files it into the worldspace's block tree. A 'Cell' with no parent and no grid is an interior cell. Ignored for other types.")]
    public string? Grid { get; init; }
}

/// <summary>One field op on a record being CREATED — the <see cref="ApplyOp"/> shape minus what a create cannot mean,
/// no <c>formid</c> and no copy pole, either of which the strict reader refuses BY NAME.</summary>
public sealed record CreateFieldOp
{
    [SchemaRequired, JsonPropertyName("field_path"), Description("Dotted field path on the new record, e.g. 'Name' or 'BasicStats.Damage'. Step into a list or dict element mid-path with brackets ('Effects[0].Data.Magnitude'); at the leaf use op and key, not brackets.")]
    public string? FieldPath { get; init; }

    [SchemaValues(SchemaVocabulary.CreateVerbs), JsonPropertyName("op"), Description(WriteVerbs.OnCreateRecital + ". SetAtIndex overwrites the element at key=; InsertAtIndex inserts a new one at key= and shifts the rest right (key = the list's length appends). On a [Flags] enum, Add sets one bit and Remove clears one, leaving the others untouched. Copying a field from another record is " + ToolNames.Apply + "'s CopyFrom.")]
    public string? Op { get; init; }

    [JsonPropertyName("value"), Description("The value, coerced to the field's type: a number, an enum name, a FormID 'XXXXXX:Plugin.esp', or, on a FormLink field (inside a compose spec too), '@<editorid>' naming a record declared earlier in records= or the record being created itself (e.g. a quest's VMAD alias fragment whose Property.Object is the quest). So a dialogue line's topic link and order chain go in the same call: ops:[{field_path:'Topic', value:'@MyTopic'}, {field_path:'PreviousDialog', value:'@MyTopic_L1'}].")]
    public string? Value { get; init; }

    [JsonPropertyName("key"), Description("Dict key or list index at the leaf.")]
    public string? Key { get; init; }

    [JsonPropertyName("values"), Description("The whole new list for a list ReplaceAll.")]
    public string[]? Values { get; init; }

    [JsonPropertyName("entries"), Description("Key->value pairs for a dict Merge or dict ReplaceAll.")]
    public Dictionary<string, string>? Entries { get; init; }

    [JsonPropertyName("compose"), Description("Build a modeled struct (a leveled-list entry, an effect, a condition row): the arm for a polymorphic Set, or the element for a struct-element Add, InsertAtIndex or SetAtIndex.")]
    public StructInput? Compose { get; init; }

    [JsonPropertyName("composes"), Description("Build many modeled list elements in one op. With Add, appends each in order; with ReplaceAll, clears the list then appends each. Mutually exclusive with compose, value and values.")]
    public StructInput[]? Composes { get; init; }
}
