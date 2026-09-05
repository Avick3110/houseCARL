using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;

namespace HousecarlMcp;

/// <summary>housecarl_create — the record-authoring surface: <c>records=</c> × the lane (new patch, <c>into=</c> an
/// existing one, or <c>in_place=</c> with consent) × transport, over
/// <see cref="LoadOrderService.CreateRecordsBatch"/>. <c>records=</c> takes the inline array or
/// <c>"@&lt;absolute path&gt;"</c>, both through the same strict <see cref="ListParams"/> reader, which refuses an
/// undeclared member BY NAME at its element where the SDK binder would silently drop it.</summary>
[McpServerToolType]
public static class CreateTools
{
    [McpServerTool(Name = ToolNames.Create, Title = "Create brand-new records (the 2.0 authoring surface)"),
     Description(
         "Create BRAND-NEW records (new FormIDs) and write them to a NEW patch plugin (originals untouched by " +
         "default) — the net-new authoring tool, the companion to " + ToolNames.Apply + " (which edits EXISTING records). " +
         "ONE surface: what to author (records=) x WHERE it lands (the LANE: a new patch | into= an existing one | " +
         "in_place=\"X.esp\") x how it reads back (TRANSPORT).\n\n" +
         "RECORDS. records= is the spec list — {record_type, editorid, ops?, parent?, collection?, grid?} each; ONE " +
         "record is a set of one. record_type is a catalog name ('Keyword', 'Spell', " +
         "'LeveledItem', 'DialogTopic') or a 4-char signature ('KYWD'). editorid is REQUIRED — the EditorID the " +
         "record is referenced by (in SkyPatcher/SPID, in xEdit); choose a clear, prefixed name. Any flat " +
         "top-level record is fair game — a keyword, spell, perk, magic effect, faction, armor, weapon, leveled " +
         "list, global, quest — as is a nested one (see NESTING). For an ABSTRACT " +
         "record group name the CONCRETE subtype directly ('GlobalFloat'/'GlobalInt'/'GlobalShort', " +
         "'GameSettingFloat'/'GameSettingInt'/'GameSettingString') — that is how a global variable or game setting " +
         "is created. Each new FormID is auto-allocated in the patch's own 0x800+ range and REPORTED BACK: it is how " +
         "you reference the record afterwards — to make ANOTHER record point at it, call this tool or " +
         ToolNames.Apply + " again with into='<this patch>' and that FormID as the value.\n\n" +
         "OPS set the new record's fields — the same shape " + ToolNames.Apply + " takes MINUS formid (the record has no id " +
         "yet): {field_path, op?, value?, key?, values?, entries?, compose?, composes?}. e.g. " +
         "ops=[{field_path:'Name', value:'My Spell'}, {field_path:'EffectList', op:'Add', compose:{...}}]. compose " +
         "builds a modeled struct (a leveled-list entry, an effect, a condition row, or a polymorphic element by its " +
         "CONCRETE arm type); composes is its batch sibling. Copying a field from another record is " + ToolNames.Apply + "'s " +
         "CopyFrom — there is no other version of a record that does not exist yet.\n\n" +
         "NESTING. parent= makes the record a CHILD (a dialogue line under a topic, a placed ref in a cell): the " +
         "parent is an EXISTING record's FormID 'XXXXXX:Plugin.esp' OR the editorid of a record declared EARLIER in " +
         "this same records= array (a same-call sibling) — which is how a topic AND its lines are authored in ONE " +
         "call: records=[{record_type:'DialogTopic', editorid:'MyTopic'}, {record_type:'DialogResponses', " +
         "editorid:'MyTopic_L1', parent:'MyTopic', ops:[{field_path:'Prompt', value:'Hello'}]}] (declare the parent " +
         "BEFORE its children). A FormLink field VALUE can reference a same-call sibling too, written '@editorid' — " +
         "so a line's order-chain and its topic back-link are authored in the same call (ops:[{field_path:'Topic', " +
         "value:'@MyTopic'}, {field_path:'PreviousDialog', value:'@MyTopic_L1'}]). '@editorid' must name a record " +
         "declared EARLIER in the array or the record being created ITSELF (self-reference — e.g. a quest's VMAD " +
         "alias fragment whose Property.Object is the quest). Only on FormLink fields, including inside a compose " +
         "spec. collection= names WHICH of the parent's child slots to add into — a child LIST (e.g. a cell's " +
         "'Persistent'/'Temporary') or a SINGLE-child slot (a cell's 'Landscape', a worldspace's 'TopCell', which " +
         "hold exactly one and refuse rather than overwrite when occupied) — needed only when more than one fits. " +
         "grid= is the EXTERIOR cell's \"X,Y\" " +
         "(record_type 'Cell' with parent= a Worldspace; houseCARL files it into the worldspace's block tree). A " +
         "'Cell' with NO parent and NO grid is an INTERIOR cell.\n\n" +
         "LANE — where the write lands. Default: a NEW patch named patch= (auto-suffixed if taken, so a prior patch " +
         "is never overwritten). into='<an existing patch's filename>' ADDS these records to that patch instead — " +
         "the way to accumulate across calls and sessions, and a parent created in a PRIOR into= call can be the " +
         "parent here too (it resolves from the patch being extended, not only from the load order). " +
         "in_place='<plugin filename>' is the opt-in THIRD lane: the records are created straight INTO your ORIGINAL " +
         "file (incl. a mod houseCARL didn't author) — no new patch, and NO houseCARL backup or undo (keep your " +
         "own). Full create parity in place, nesting included: a parent the target already owns hosts the child, a " +
         "parent from another plugin is overridden in. Each new record gets a fresh FormID in THAT plugin's own " +
         "range. The FIRST in-place write to a given plugin returns a confirmation prompt (re-call with " +
         "acknowledge=true); that consent covers touching your original ONLY — it NEVER skips the record verify.\n\n" +
         "ALL-OR-NOTHING (Q3): if ANY spec is malformed or fails pre-flight — an unknown or ambiguous record_type, a " +
         "missing editorid, an illegal field op, a nested child with no resolvable parent, an ambiguous collection, " +
         "a type that cannot be created (the bare abstract base 'Global'/'GameSetting' needs a concrete subtype; the " +
         "refusal names them) — the whole call is refused with per-record reasons and NOTHING is written. No partial " +
         "patches, ever.\n\n" +
         "WHAT ELSE IS REPORTED (never silent, Q3): creating dialogue lines reports VOICE coverage (a response with " +
         "no .fuz plays SILENT in game — the audio is yours to provide) and RESULT-SCRIPT binding (a bound script " +
         "that is unwired or uncompiled runs nothing); creating cells reports the world content houseCARL does NOT " +
         "author (lighting, terrain, water, navmesh — Creation Kit work).\n\n" +
         "TRANSPORT: readback=true expands the read-back to the FULL deep field-by-field dump of every created " +
         "record (in place the verify ALWAYS runs and shows compactly by default; readback widens it) — confirm " +
         "composed structures landed WITHOUT enabling the patch in MO2. The read-back is the WRITTEN FILE's content, " +
         "NOT load-order truth: the patch wins nothing until enabled + sorted in MO2. format='json' returns the same " +
         "data machine-readable; max_chars caps the render with an explicit notice, never a silent cut. Every " +
         "response carries epoch=<hex> — the identity of the index build parents and link values resolved from.\n\n" +
         "records= (like every list-valued input) also accepts \"@<absolute path>\" in place of the inline array: " +
         "the SAME array as a JSON manifest on disk. The path must be ABSOLUTE (the server resolves relative paths " +
         "against its OWN working directory, not yours), and the file is read at CALL time.\n\n" +
         "This tool authors NEW records. Editing an existing record's fields is " + ToolNames.Apply + "; dropping a whole " +
         "record is " + ToolNames.Remove + "; an EMPTY header-only trigger plugin (no records at all) is " +
         ToolNames.CreatePlugin + ". Read first with " + ToolNames.Records + ".")]
    public static string Create(
        LoadOrderService svc,
        [Description("The records to author, all into one artifact: [{record_type, editorid, ops?, parent?, collection?, grid?}, …] — or \"@<absolute path>\" to read that SAME array from a JSON manifest file. One record is a set of one. For a nested one-shot, declare the parent BEFORE the children whose parent= names its editorid. A member the shape does not declare is refused BY NAME at its element, never silently dropped.")]
            JsonElement? records = null,
        [Description("LANE: base filename for the NEW patch this call writes (default 'Patch'); auto-suffixed if taken, so a prior patch is never overwritten. Mutually exclusive with into= and in_place= — naming both lanes is refused, never silently ignored.")]
            string? patch = null,
        [Description("LANE: filename of an EXISTING houseCARL patch to ADD these records to instead of writing a fresh one — the way to accumulate across calls and sessions (a parent created in a prior into= call resolves from it too). Found by the plugin's filename even if you've renamed its MO2 mod folder; for two patches sharing a filename, pass the mod-folder name here instead.")]
            string? into = null,
        [Description("LANE (opt-in): the FILENAME OF THE FILE BEING WRITTEN INTO, e.g. \"CoolWeapons.esp\" — create these records straight into that existing active plugin (incl. one houseCARL didn't author) instead of a patch. Your ORIGINAL file is rewritten; no houseCARL backup or undo. Naming the file is the point: it is what you are about to overwrite. OMIT for the default patch lane, which leaves every original untouched.")]
            string? in_place = null,
        [Description("Confirms the one-time in-place trade-off for the plugin named by in_place= — needed only on the FIRST in-place write to a given plugin (edit, create, remove, OR forward), and not again once one has LANDED — a call that is refused records nothing, so it may be needed again. Waives the consent to touch your original ONLY; it NEVER skips the record verify. Meaningless without in_place=, and refused there rather than ignored.")]
            bool acknowledge = false,
        [Description("TRANSPORT: expand the read-back to the FULL deep field-by-field dump of every record this call created (not just the fields you set). The written file's content, not load-order truth.")]
            bool readback = false,
        [Description("TRANSPORT: 'text' (default) | 'json' (the same data, machine-readable, accounting in-band).")]
            string? format = null,
        [Description("TRANSPORT: character ceiling on the WHOLE render — the created-record rows (each with its allocated FormID), the voice / result-script / cell-shell reports, and the read-back, in that order. Past it, trailing rows are dropped with an explicit notice and a per-block rendered-vs-total census (never silent). The WRITE is unaffected either way. 0 = a safe default kept under the host's per-response limit.")]
            int max_chars = 0) => Guard.Tool(ToolNames.Create, () =>
    {
        // ---- TRANSPORT: format --------------------------------------------------------------------------
        // Resolved BEFORE the unconfigured-MO2 prompt: that prompt is prose, and handing it verbatim to a
        // format="json" caller returns something JsonDocument.Parse throws on — no ok, no error to branch on.
        bool json = Wire.WantsJson(format, out var ferr);
        if (ferr is not null) return ferr;   // the format value itself is unparsed — there is no known render to answer in
        if (svc.ConfigPromptOrNull() is { } prompt)
            return json ? JsonWire.RenderError(prompt, null) : prompt;

        // Every refusal below answers in the caller's requested format — a json caller must never have to parse
        // "error: …" out of a string. Epoch is null on all of them: none has consulted a build yet.
        string Refuse(string message) => json ? JsonWire.RenderError(message, null) : "error: " + message;

        // ---- LANE: the three destinations are mutually exclusive, and a dropped one is named ------------
        // A parameter is honoured or refused BY NAME, never accepted-and-ignored.
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

        // ---- records= -----------------------------------------------------------------------------------
        if (records is not { } recEl || recEl.ValueKind is JsonValueKind.Null)
            return Refuse("nothing to create. Pass records=[{record_type, editorid, ops?, parent?, collection?}, …] " +
                          "(or records=\"@<absolute path>\") — one record is a set of one.");
        var (specs, rerr) = ListParams.Read<CreateRecordSpec>(recEl, "records", "{record_type, editorid, ops?, parent?, collection?, grid?}");
        if (rerr is not null) return Refuse(rerr);

        // ---- Map the wire shapes onto the engine's inputs -----------------------------------------------
        // A rename over the same engine inputs: ops -> operations, op -> verb.
        var wire = new List<CreateOp>(specs!.Length);
        var origins = new List<string?>(specs.Length);
        for (int i = 0; i < specs.Length; i++)
        {
            var s = specs[i];
            // A null ELEMENT inside ops= is legal JSON and STJ hands it straight through. ListParams.Read makes this
            // check only over the TOP-level list — records= here — so the nested ops must be checked by hand.
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
            // The caller's OWN spelling for this spec, carried down: a refusal must never point at a parameter
            // label the caller never wrote.
            origins.Add($"records[{i}]");
        }

        // naming: refusals raised below the tool layer must use THIS surface's words — ops[i], and op="CopyFrom"
        // (there is no from_plugin member here to tell the caller to drop).
        var outcome = svc.CreateRecordsBatch(wire, patchName, into, readback, in_place, hasInPlace, acknowledge, origins,
            naming: new LoadOrderService.CreateOpNaming("ops", "op=\"CopyFrom\""));
        // The lane the CALL named — stated, not derived from the outcome's flags.
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
    [JsonPropertyName("record_type"), Description("The kind of record to create: a catalog name ('Keyword', 'Spell', 'Weapon', 'DialogTopic', 'PlacedObject') or a 4-char signature ('KYWD'). For an abstract group name the concrete subtype ('GlobalFloat', 'GameSettingInt').")]
    public string? RecordType { get; init; }

    [JsonPropertyName("editorid"), Description("REQUIRED. The EditorID the new record is referenced by. A nested child's parent= can name this editorid (a same-call sibling parent), and a FormLink value can reference it as '@<editorid>'.")]
    public string? Editorid { get; init; }

    [JsonPropertyName("ops"), Description("The new record's fields: [{field_path, op?, value?, key?, values?, entries?, compose?, composes?}, …] — the same op shape " + ToolNames.Apply + " takes, minus formid. Omit to create a bare record (type + editorid only).")]
    public CreateFieldOp[]? Ops { get; init; }

    [JsonPropertyName("parent"), Description("For a NESTED record: the parent it nests under — an EXISTING parent's FormID 'XXXXXX:Plugin.esp', OR the editorid of a record declared EARLIER in this same records= array (a same-call sibling). Omit for a flat top-level record.")]
    public string? Parent { get; init; }

    [JsonPropertyName("collection"), Description("Which of the parent's child-collections to add into, BY NAME (e.g. a cell's 'Persistent') — needed only when more than one fits. Omit when unique or when parent is omitted.")]
    public string? Collection { get; init; }

    [JsonPropertyName("grid"), Description("For an EXTERIOR cell only (record_type 'Cell' with parent= a Worldspace): the cell's grid as \"X,Y\" (e.g. \"5,-12\"). A 'Cell' with NO parent and NO grid is an INTERIOR cell. Ignored for non-Cell types.")]
    public string? Grid { get; init; }
}

/// <summary>One field op on a record being CREATED — the <see cref="ApplyOp"/> shape minus what a create cannot
/// mean: no <c>formid</c> (the id is auto-allocated) and no copy pole (copying a field from another version needs a
/// record that already exists). Either one is refused BY NAME by the strict reader.</summary>
public sealed record CreateFieldOp
{
    [JsonPropertyName("field_path"), Description("Dotted field path on the new record, e.g. 'Name' or 'BasicStats.Damage'. Step into a list/dict element mid-path with brackets ('Effects[0].Data.Magnitude'); at the LEAF use op + key, not brackets.")]
    public string? FieldPath { get; init; }

    [JsonPropertyName("op"), Description("Set (default) | Add | Remove | SetAtIndex | InsertAtIndex | ReplaceAll | Merge. SetAtIndex OVERWRITES the element at key=; InsertAtIndex inserts a new one AT key= and shifts the rest right (key = the list's length appends). On a [Flags] enum, Add sets one bit and Remove clears one, leaving the others untouched.")]
    public string? Op { get; init; }

    [JsonPropertyName("value"), Description("The value, coerced to the field's type — a number, an enum name, a FormID 'XXXXXX:Plugin.esp', or '@<editorid>' for a same-call sibling on a FormLink field.")]
    public string? Value { get; init; }

    [JsonPropertyName("key"), Description("Dict key or list index at the leaf.")]
    public string? Key { get; init; }

    [JsonPropertyName("values"), Description("The whole new list for a list ReplaceAll.")]
    public string[]? Values { get; init; }

    [JsonPropertyName("entries"), Description("Key->value pairs for a dict Merge or dict ReplaceAll.")]
    public Dictionary<string, string>? Entries { get; init; }

    [JsonPropertyName("compose"), Description("Build a modeled struct: the arm for a polymorphic Set, or the element for a struct-element Add / InsertAtIndex / SetAtIndex (e.g. 'LeveledItemEntry'; for a polymorphic list, the element's CONCRETE arm type such as 'ScriptObjectProperty').")]
    public StructInput? Compose { get; init; }

    [JsonPropertyName("composes"), Description("Build MANY modeled list elements in ONE op — the batch sibling of compose. With Add, appends each in order; with ReplaceAll, clears the list then appends each. Mutually exclusive with compose/value/values.")]
    public StructInput[]? Composes { get; init; }
}
