using System.ComponentModel;
using System.Text;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using HousecarlCore;

namespace HousecarlMcp;

/// <summary>
/// houseCARL write tools (§8.4 Beat C). Both ride the PROVEN public write cleave (<see cref="WritePatchBuilder.Apply"/>)
/// through <see cref="LoadOrderService.ApplyEdits"/>: resolve each record's load-order WINNER, override it into a NEW
/// patch plugin, pre-flight EVERY edit through the corpus rulebook, apply the generic verbs, and serialize ONCE with the
/// full master set (cross-master merges included). Originals are never written. Output model (Aaron-locked): one
/// complete .esp per call; <c>into=</c> extends an existing patch (the multi-session accumulation lever).
/// </summary>
[McpServerToolType]
public static class WriteTools
{
    [McpServerTool(Name = "housecarl_set_field", Title = "Edit one record field"),
     Description(
         "Edit ONE field of one record and write the change to a NEW patch plugin (originals untouched). Resolves the " +
         "record's load-order WINNER and overrides it. field_path is dotted (e.g. 'BasicStats.Damage', 'Name'); value is " +
         "coerced to the field's real type — a number, an enum name, or a FormID 'XXXXXX:Plugin.esp' for a reference. verb " +
         "defaults to Set; for collections use Add / Remove / SetAtIndex / ReplaceAll (key = a dict key or list index; " +
         "values = the whole new list for ReplaceAll). By default writes a fresh patch named patch_name; pass " +
         "into='<an existing patch's filename>' to ADD this edit to that patch instead (accumulate across calls and " +
         "sessions). Pre-flight rejects an illegal edit with the reason and writes nothing (Q3). Returns the patch path, " +
         "its masters, and the value read back. Does NOT compose modeled structs (leveled-list entries, polymorphic " +
         "fields) or edit a dict via Merge — use housecarl_bulk_apply for those, or for many edits in one patch. Read " +
         "first with housecarl_read_record.")]
    public static string SetField(
        LoadOrderService svc,
        [Description("The record's FormID as 'XXXXXX:Plugin.esp' (6 hex digits, the defining master's filename).")]
            string formid,
        [Description("Dotted field path to edit, e.g. 'BasicStats.Damage', 'Name', 'Keywords'. Step into a list/dict element MID-PATH with brackets, e.g. 'Effects[0].Data.Magnitude' or 'VirtualMachineAdapter.Aliases[0].Scripts[0].Properties'. At the LEAF, edit a collection element with verb + key (SetAtIndex/Remove by index, Set/Remove by dict key) — not brackets.")]
            string field_path,
        [Description("The value, coerced to the field's type: a number, an enum name (e.g. 'OneHanded'), or a FormID 'XXXXXX:Plugin.esp' for a reference. Omit only for Remove.")]
            string? value = null,
        [Description("Set (default) | Add | Remove | SetAtIndex | ReplaceAll. Set edits a scalar (or a dict element with key=); Add/Remove/SetAtIndex/ReplaceAll edit a collection.")]
            string verb = "Set",
        [Description("Optional. The dict key or list index at the leaf (for a dict Set, a SetAtIndex/Remove on a list, etc.).")]
            string? key = null,
        [Description("Optional. The whole new list contents for ReplaceAll on a list (each coerced).")]
            string[]? values = null,
        [Description("Optional. Base filename for the new patch (default 'houseCARL_Patch'); auto-suffixed if taken so a prior patch is never overwritten. Ignored if into= is given.")]
            string patch_name = "houseCARL_Patch",
        [Description("Optional. Filename of an existing patch (from a prior call) to EXTEND with this edit instead of writing a fresh one — the way to accumulate edits into one patch across calls/sessions.")]
            string? into = null,
        [Description("When true, the response ALSO returns the ENTIRE edited record read back from the written patch file on disk (every field, deep — not just the edited leaf). The pre-enable verification: confirm the write landed exactly and nothing else in the record was disturbed, WITHOUT enabling the patch in MO2. (The patch wins nothing until enabled + sorted in MO2 — this read-back is the written file's content, not load-order truth.)")]
            bool full_readback = false,
        [Description("Optional. Max characters for the whole response; past it the full read-back section is cut with an explicit notice (never silent). 0 = the server default (~80k). Only matters with full_readback=true.")]
            int max_chars = 0) => Guard.Tool("housecarl_set_field", () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        var op = new BulkOp
        {
            Formid = formid, FieldPath = field_path, Verb = verb, Value = value, Key = key, Values = values,
        };
        return Render(svc.ApplyEdits(new[] { op }, patch_name, into, full_readback), max_chars);
    });

    [McpServerTool(Name = "housecarl_bulk_apply", Title = "Apply many edits in one patch"),
     Description(
         "Apply MANY edits in ONE patch plugin (originals untouched) — the batch form of housecarl_set_field, and the way " +
         "to COMPOSE modeled structs. Each operation is {formid, field_path, verb, value?, key?, values?, entries?, " +
         "compose?}: scalar/collection verbs work as in set_field; entries (a key→value map) drives a dict Merge or " +
         "ReplaceAll; compose builds a modeled struct for an Add (a leveled-list entry, an effect — and a POLYMORPHIC " +
         "list element composes by its concrete arm type, e.g. a VMAD script property: verb=Add, " +
         "field_path='VirtualMachineAdapter.Scripts[0].Properties', compose={type:'ScriptObjectProperty', " +
         "fields:{Name:'MyProp', Flags:'Edited', Object:'XXXXXX:Plugin.esp', Alias:'-1'}}) or a polymorphic Set " +
         "(an arm) — e.g. merge a weapon into a leveled list with verb=Add, field_path='Entries', " +
         "compose={type:'LeveledItemEntry', sets:[{path:'Data.Level',value:'1'},{path:'Data.Count',value:'1'}," +
         "{path:'Data.Reference',value:'<weapon FormID>'}]}. All edits land in ONE reviewable .esp; the patch spans " +
         "masters automatically when edits reference forms across several plugins (cross-master merge). ALL-OR-NOTHING " +
         "(Q3): if ANY operation is malformed or fails pre-flight, the whole call is refused with per-op reasons and " +
         "nothing is written — no partial patches. By default writes a fresh patch named patch_name; pass into= to extend " +
         "an existing one. Returns the patch path, masters, and per-op read-back.")]
    public static string BulkApply(
        LoadOrderService svc,
        [Description("The edits to apply, all into one patch. Each: {formid, field_path, verb, value?, key?, values?, entries?, compose?}.")]
            BulkOp[] operations,
        [Description("Optional. Base filename for the new patch (default 'houseCARL_Patch'); auto-suffixed if taken. Ignored if into= is given.")]
            string patch_name = "houseCARL_Patch",
        [Description("Optional. Filename of an existing patch to EXTEND with these edits instead of writing a fresh one (accumulate across calls/sessions).")]
            string? into = null,
        [Description("When true, the response ALSO returns the ENTIRE record(s) this call touched, read back from the written patch file on disk (every field, deep — not just the edited leaves). The pre-enable verification: confirm composed structures (conditions, container entries) landed exactly and nothing else in each record was disturbed, WITHOUT enabling the patch in MO2. (The patch wins nothing until enabled + sorted in MO2 — this read-back is the written file's content, not load-order truth.)")]
            bool full_readback = false,
        [Description("Optional. Max characters for the whole response; past it the full read-back section is cut with an explicit notice (never silent). 0 = the server default (~80k). Only matters with full_readback=true.")]
            int max_chars = 0) => Guard.Tool("housecarl_bulk_apply", () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        if (operations is null || operations.Length == 0)
            return "error: operations is empty. Pass one or more {formid, field_path, verb, ...} edits.";
        return Render(svc.ApplyEdits(operations, patch_name, into, full_readback), max_chars);
    });

    [McpServerTool(Name = "housecarl_remove_record", Title = "Remove a whole record from a patch"),
     Description(
         "Remove a WHOLE record from a houseCARL patch — a literal drop-from-plugin (NOT a flag-as-deleted stub). The " +
         "companion to the edit tools: where set_field/bulk_apply ADD an override into a patch, this drops one OUT of it. " +
         "Only works on a record the patch ITSELF carries — one houseCARL created, or an override the patch accumulated " +
         "via a prior set_field/bulk_apply into=patch. You CANNOT remove a record that lives in a master/another mod; you " +
         "can only drop THIS patch's override of it, which makes the load-order winner revert (the patch stops touching " +
         "that record). patch is REQUIRED and names an existing houseCARL-owned patch (the same name you pass to into=); " +
         "removal targets a patch that already carries the record. Refuses loud and writes nothing (Q3) if the patch does " +
         "not carry the FormID. Unused masters are pruned automatically — if the removed record held the patch's last " +
         "reference to a master, that master drops from the header on the re-write. Reaches records in ANY group (incl. " +
         "cells, placed references, dialog, navmesh). Returns what was removed, the patch's remaining masters, and how " +
         "many records remain. To remove a list ENTRY (a keyword, an item) rather than a whole record, use set_field with " +
         "verb=Remove instead.")]
    public static string RemoveRecord(
        LoadOrderService svc,
        [Description("The record's FormID as 'XXXXXX:Plugin.esp' — the record to drop from the patch.")]
            string formid,
        [Description("Filename of the houseCARL patch to remove the record from (e.g. 'MyMerge.esp' or 'MyMerge') — must be a patch houseCARL created that carries this record.")]
            string patch) => Guard.Tool("housecarl_remove_record", () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        return RenderRemoval(svc.RemoveRecords(new[] { formid }, patch));
    });

    [McpServerTool(Name = "housecarl_create_record", Title = "Create a brand-new record"),
     Description(
         "Create a BRAND-NEW record (a new FormID) of record_type in a NEW patch plugin (originals untouched) — the " +
         "net-new authoring tool, the companion to set_field/bulk_apply (which edit EXISTING records). Use it to author a " +
         "new keyword, spell, perk, magic effect, faction, armor, weapon, leveled list... — any flat top-level record. " +
         "record_type is a catalog name ('Keyword', 'Spell', 'LeveledItem') or a 4-char signature ('KYWD'). editorid is " +
         "REQUIRED — the EditorID the record is referenced by (in SkyPatcher/SPID, in xEdit); choose a clear, prefixed name. " +
         "operations set the new record's fields, the SAME shape as bulk_apply ops but WITHOUT a formid (the new record's " +
         "FormID is auto-allocated, in the patch's own 0x800+ range, and returned to you) — e.g. " +
         "operations=[{field_path:'Name', value:'My Spell'}, {field_path:'EffectList', verb:'Add', compose:{...}}]. To create a " +
         "NESTED record (a dialogue line under a topic, a placed ref in a cell), pass parent= (the parent record's FormID) and, " +
         "if the parent holds more than one child-list that fits, collection= (e.g. 'Persistent'); for a parent AND its children " +
         "in ONE call (a topic + its lines), use housecarl_bulk_create. The new FormID is reported back; to make ANOTHER record " +
         "reference it, call this or set_field again with into='<this patch>' using that FormID. By default writes a fresh patch " +
         "named patch_name; into= extends an existing houseCARL patch (accumulate across calls/sessions). ALL-OR-NOTHING (Q3): " +
         "the whole call is refused with a reason and nothing is written if the type can't be created (an EXTERIOR cell nests " +
         "under FormKey-less worldspace structs — a separate capability; abstract types like Global need a concrete subtype), if " +
         "a nested type is given no parent, if editorid is missing, or if any field op is illegal. Returns the new record's " +
         "FormID + editorid, the patch path, and its (derived) masters.")]
    public static string CreateRecord(
        LoadOrderService svc,
        [Description("The kind of record to create: a catalog name ('Keyword', 'Spell', 'Weapon', 'LeveledItem', 'DialogResponses', 'PlacedObject') or a 4-char signature ('KYWD'). A flat top-level type, or a nested type (a dialogue line, a placed ref) when parent= is given.")]
            string record_type,
        [Description("REQUIRED. The EditorID for the new record — how it's referenced (in SkyPatcher/SPID/xEdit). Choose a clear, prefixed name.")]
            string editorid,
        [Description("Optional. The new record's fields, same shape as bulk_apply ops but with NO formid: {field_path, verb?, value?, key?, values?, entries?, compose?}. Omit to create a bare record (just type + editorid).")]
            BulkOp[]? operations = null,
        [Description("Optional. For a NESTED record (a dialogue line, a placed ref): the PARENT it nests under, as the parent record's FormID 'XXXXXX:Plugin.esp' (e.g. add a line to an existing topic, a ref to an existing cell). Omit for a flat top-level record. (For a parent + its children in one call — where parent can also be a same-call sibling's editorid — use housecarl_bulk_create.)")]
            string? parent = null,
        [Description("Optional. Which of the parent's child-collections to add into, BY NAME (e.g. a cell's 'Persistent'/'Temporary') — needed only when the parent holds more than one list that accepts this child type. Omit when the collection is unique (e.g. a topic's responses) or when parent is omitted.")]
            string? collection = null,
        [Description("Optional. Base filename for the new patch (default 'houseCARL_Patch'); auto-suffixed if taken. Ignored if into= is given.")]
            string patch_name = "houseCARL_Patch",
        [Description("Optional. Filename of an existing houseCARL patch to add this new record to instead of writing a fresh one (accumulate across calls/sessions).")]
            string? into = null,
        [Description("When true, the response ALSO returns the ENTIRE created record read back from the written patch file on disk (every field, deep — not just the fields you set). The pre-enable verification, WITHOUT enabling the patch in MO2. (The patch wins nothing until enabled + sorted in MO2 — this read-back is the written file's content, not load-order truth.)")]
            bool full_readback = false,
        [Description("Optional. Max characters for the whole response; past it the full read-back section is cut with an explicit notice (never silent). 0 = the server default (~80k). Only matters with full_readback=true.")]
            int max_chars = 0) => Guard.Tool("housecarl_create_record", () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        return RenderCreate(svc.CreateRecords(record_type, editorid, operations ?? Array.Empty<BulkOp>(), patch_name, into, full_readback, parent, collection), max_chars);
    });

    [McpServerTool(Name = "housecarl_bulk_create", Title = "Create many records (incl. a nested one-shot) in one patch"),
     Description(
         "Create MANY brand-new records in ONE patch plugin (originals untouched) — the batch form of housecarl_create_record, " +
         "and the way to author a NESTED unit in a single call: a dialogue topic AND its lines, a cell AND its placed refs. " +
         "records is an array of {record_type, editorid, operations?, parent?, collection?} — each spec is exactly a " +
         "create_record call. A spec's parent= can be the FormID of an EXISTING record OR the editorid of a record declared " +
         "EARLIER in this same records array (a same-call sibling) — which is how the one-shot 'topic + its lines' is expressed: " +
         "records=[{record_type:'DialogTopic', editorid:'MyTopic'}, {record_type:'DialogResponses', editorid:'MyTopic_L1', " +
         "parent:'MyTopic', operations:[{field_path:'Prompt', value:'Hello'}]}] (declare the topic BEFORE the lines). collection= " +
         "names which child-list when the parent holds more than one that fits (e.g. a cell's 'Persistent'). Each new FormID is " +
         "auto-allocated (the patch's own 0x800+ range) and returned. ALL-OR-NOTHING (Q3): if ANY spec is malformed or fails " +
         "pre-flight (unknown/ambiguous type, missing editorid, illegal field op, a nested child with no resolvable parent, an " +
         "ambiguous collection), the whole call is refused with per-record reasons and nothing is written — no partial patches. " +
         "By default writes a fresh patch named patch_name; into= extends an existing houseCARL patch — and a parent created in " +
         "a PRIOR into= call CAN be the parent here too (it's resolved from the patch being extended, not only the load order). " +
         "Returns each new record's FormID + editorid, the patch path, and its (derived) masters.")]
    public static string BulkCreate(
        LoadOrderService svc,
        [Description("The records to create, all into one patch. Each: {record_type, editorid, operations?, parent?, collection?}. For a nested one-shot, declare the parent (e.g. a DialogTopic) BEFORE the children whose parent= names its editorid.")]
            CreateOp[] records,
        [Description("Optional. Base filename for the new patch (default 'houseCARL_Patch'); auto-suffixed if taken. Ignored if into= is given.")]
            string patch_name = "houseCARL_Patch",
        [Description("Optional. Filename of an existing houseCARL patch to add these new records to instead of writing a fresh one (accumulate across calls/sessions).")]
            string? into = null,
        [Description("When true, the response ALSO returns each created record IN FULL, read back from the written patch file on disk (every field, deep). The pre-enable verification, WITHOUT enabling the patch in MO2 (the written file's content, not load-order truth).")]
            bool full_readback = false,
        [Description("Optional. Max characters for the whole response; past it the full read-back section is cut with an explicit notice (never silent). 0 = the server default (~80k). Only matters with full_readback=true.")]
            int max_chars = 0) => Guard.Tool("housecarl_bulk_create", () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        if (records is null || records.Length == 0)
            return "error: records is empty. Pass one or more {record_type, editorid, operations?, parent?, collection?} specs.";
        return RenderCreate(svc.CreateRecordsBatch(records, patch_name, into, full_readback), max_chars);
    });

    /// <summary>Compact, parseable confirmation (rulebook: short mutation confirmation + the IDs needed for follow-up).
    /// On refusal, the full reason (every malformed/rejected op) so the caller can fix and retry.</summary>
    static string Render(WritePatchBuilder.PatchOutcome o, int maxChars = 0)
    {
        if (!o.Success) return "error: " + o.Error;
        var file = Path.GetFileName(o.OutputPath);
        var modFolder = Path.GetFileName(Path.GetDirectoryName(o.OutputPath) ?? "");
        var sb = new StringBuilder();
        sb.Append(o.Extended ? "extended " : "wrote ").Append(file)
          .Append(o.Extended ? " (existing patch grown; " : " (new patch; ").Append(o.Bytes).Append(" bytes)\n");
        sb.Append("mod folder: ").Append(modFolder)
          .Append(o.Extended ? "\n" : "  — enable + sort it in MO2 to use the patch\n");
        sb.Append("masters: ").Append(o.Masters.Count == 0 ? "(none)" : string.Join(", ", o.Masters)).Append('\n');
        sb.Append(o.Ops.Count).Append(o.Ops.Count == 1 ? " edit:\n" : " edits:\n");
        foreach (var op in o.Ops)
            sb.Append("  ").Append(op.RecordType).Append(' ').Append(op.Target).Append("  ").Append(op.Label)
              .Append(op.After is not null ? "  -> " + op.After : "  -> applied").Append('\n');
        if (o.ReadBack is { } rb) AppendFullReadback(sb, rb, maxChars);
        sb.Append("to add more edits to THIS patch, pass into=\"").Append(file).Append("\".");
        return sb.ToString();
    }

    /// <summary>The opt-in full read-back section (HCBR-2026-06-11-02 wave (b)): each touched/created record IN FULL,
    /// re-read from the written file on disk. Labeled as exactly that — the written file's content, NOT load-order
    /// truth (the patch wins nothing until enabled in MO2) — so the caller can't mistake it for a winner read.
    /// Char-budget-bounded with an explicit notice (Q3), same convention as the read tools.</summary>
    static void AppendFullReadback(StringBuilder sb, IReadOnlyList<WritePatchBuilder.FullReadback> rb, int maxChars)
    {
        int cap = maxChars > 0 ? maxChars : Wire.DefaultMaxChars;
        sb.Append("full read-back — the ENTIRE record(s) as written, re-read from the patch file on disk ")
          .Append("(the written file's content, NOT load-order truth; the patch wins nothing until enabled + sorted in MO2):\n");
        for (int i = 0; i < rb.Count; i++)
        {
            if (sb.Length >= cap)
            {
                sb.Append("  ... [truncated: full read-back rendered ").Append(i).Append(" of ").Append(rb.Count)
                  .Append(" record(s) at max_chars=").Append(cap)
                  .Append("; raise max_chars, or enable the patch in MO2 and use housecarl_read_record]\n");
                return;
            }
            var r = rb[i];
            if (r.Error is not null) { sb.Append("  ").Append(r.Target).Append("  error: ").Append(r.Error).Append('\n'); continue; }
            var rec = r.Record!;
            sb.Append("  ").Append(rec.Type).Append(' ').Append(rec.FormKey).Append("  editorid=").Append(rec.EditorId ?? "<none>").Append('\n');
            foreach (var f in rec.Fields)
            {
                if (sb.Length >= cap)
                {
                    sb.Append("    ... [truncated: this record's field lines hit max_chars=").Append(cap)
                      .Append("; ").Append(rb.Count - i - 1).Append(" further record(s) not rendered")
                      .Append("; raise max_chars, or enable the patch in MO2 and use housecarl_read_record]\n");
                    return;
                }
                sb.Append("    ").Append(f.Path).Append(" = ").Append(f.HasValue ? f.Token : f.Note).Append('\n');
            }
        }
    }

    /// <summary>Confirmation for housecarl_remove_record: what was dropped, the patch's now-lean masters, and how many
    /// records remain (0 ⇒ inert). On refusal, the named reason (Q3) so the caller can fix and retry.</summary>
    static string RenderRemoval(WritePatchBuilder.RemovalOutcome o)
    {
        if (!o.Success) return "error: " + o.Error;
        var file = Path.GetFileName(o.OutputPath);
        var modFolder = Path.GetFileName(Path.GetDirectoryName(o.OutputPath) ?? "");
        var sb = new StringBuilder();
        sb.Append("removed ").Append(o.Removed.Count).Append(o.Removed.Count == 1 ? " record from " : " records from ")
          .Append(file).Append(" (").Append(o.Bytes).Append(" bytes; ")
          .Append(o.RemainingRecords).Append(o.RemainingRecords == 1 ? " record remains)\n" : " records remain)\n");
        sb.Append("mod folder: ").Append(modFolder).Append('\n');
        foreach (var r in o.Removed)
            sb.Append("  - ").Append(r.RecordType).Append(' ').Append(r.Target).Append("  ")
              .Append(r.EditorId ?? "<no editorid>").Append('\n');
        sb.Append("masters: ").Append(o.Masters.Count == 0 ? "(none)" : string.Join(", ", o.Masters)).Append('\n');
        sb.Append(o.RemainingRecords == 0
            ? "this patch now carries no records — it's inert; disable or delete the mod folder in MO2 if you don't need it."
            : "re-sort in MO2 if dropping this override changes a conflict winner.");
        return sb.ToString();
    }

    /// <summary>Confirmation for housecarl_create_record: the new record's ALLOCATED FormID + editorid + type (the FormID
    /// is the key output — the caller references the new record by it), the patch path + its (derived) masters, and the
    /// fields applied. On refusal, the named reason (Q3) so the caller can fix and retry.</summary>
    static string RenderCreate(WritePatchBuilder.CreateOutcome o, int maxChars = 0)
    {
        if (!o.Success) return "error: " + o.Error;
        var file = Path.GetFileName(o.OutputPath);
        var modFolder = Path.GetFileName(Path.GetDirectoryName(o.OutputPath) ?? "");
        var sb = new StringBuilder();
        sb.Append(o.Extended ? "extended " : "wrote ").Append(file)
          .Append(o.Extended ? " (existing patch grown; " : " (new patch; ").Append(o.Bytes).Append(" bytes)\n");
        sb.Append("mod folder: ").Append(modFolder)
          .Append(o.Extended ? "\n" : "  — enable + sort it in MO2 to use the patch\n");
        sb.Append("masters: ").Append(o.Masters.Count == 0 ? "(none)" : string.Join(", ", o.Masters)).Append('\n');
        var replacedCount = o.Created.Count(c => c.ReplacedExisting);
        sb.Append("created ").Append(o.Created.Count).Append(o.Created.Count == 1 ? " record" : " records");
        if (replacedCount > 0)
            sb.Append(" (").Append(replacedCount).Append(replacedCount == 1 ? " REPLACED an existing record" : " REPLACED existing records")
              .Append(" — same FormID kept, prior contents discarded)");
        sb.Append(":\n");
        foreach (var c in o.Created)
        {
            sb.Append("  ").Append(c.RecordType).Append(' ').Append(c.FormKey).Append("  ").Append(c.EditorId);
            if (c.ReplacedExisting) sb.Append("  [REPLACED: this patch already defined this editorid — re-created fresh at the same FormID; prior contents, including any set_field edits since, were discarded]");
            sb.Append('\n');
            foreach (var op in c.Ops)
                sb.Append("      ").Append(op.Label).Append(op.After is not null ? "  -> " + op.After : "  -> applied").Append('\n');
        }
        if (o.ReadBack is { } rb) AppendFullReadback(sb, rb, maxChars);
        sb.Append("the new FormID above is how you reference this record (SkyPatcher/SPID, or a follow-up edit). ")
          .Append("To add more to THIS patch, pass into=\"").Append(file).Append("\".");
        return sb.ToString();
    }
}

// ---- wire DTOs (the operation shape for bulk_apply; set_field builds one internally) ----------------------

/// <summary>One edit operation off the wire. RecordType is NOT supplied — the cleave derives it from the resolved
/// winner's runtime type. Mirrors <see cref="WritePatchBuilder.PatchEdit"/> with string FormID + dotted path +
/// optional composition.</summary>
public sealed record BulkOp
{
    [JsonPropertyName("formid"), Description("The record's FormID 'XXXXXX:Plugin.esp'.")]
    public string? Formid { get; init; }

    [JsonPropertyName("field_path"), Description("Dotted field path, e.g. 'BasicStats.Damage' or 'Entries'. Step into a list/dict element mid-path with brackets, e.g. 'Effects[0].Data.Magnitude'; at the LEAF use verb + key, not brackets.")]
    public string? FieldPath { get; init; }

    [JsonPropertyName("verb"), Description("Set (default) | Add | Remove | SetAtIndex | ReplaceAll | Merge.")]
    public string Verb { get; init; } = "Set";

    [JsonPropertyName("value"), Description("The value (coerced to the field's type). Omit for Remove / ReplaceAll / Merge / compose.")]
    public string? Value { get; init; }

    [JsonPropertyName("key"), Description("Dict key or list index at the leaf.")]
    public string? Key { get; init; }

    [JsonPropertyName("values"), Description("The whole new list for a list ReplaceAll.")]
    public string[]? Values { get; init; }

    [JsonPropertyName("entries"), Description("Key→value pairs for a dict Merge or dict ReplaceAll.")]
    public Dictionary<string, string>? Entries { get; init; }

    [JsonPropertyName("compose"), Description("Build a modeled struct: an arm for a polymorphic Set, or the element for a struct-element Add (e.g. a leveled-list entry; for a polymorphic list like VMAD Scripts[i].Properties, the element's CONCRETE arm type, e.g. 'ScriptObjectProperty').")]
    public StructInput? Compose { get; init; }
}

/// <summary>One brand-new record to create off the wire (housecarl_bulk_create) — the batch element matching the scalar
/// args of housecarl_create_record: the DECLARED record_type, its editorid, optional field operations, and the optional
/// nested parent/collection (a child's parent may be an existing FormID or a same-call sibling's editorid).</summary>
public sealed record CreateOp
{
    [JsonPropertyName("record_type"), Description("The kind of record to create: a catalog name ('Keyword', 'Spell', 'DialogTopic', 'DialogResponses', 'PlacedObject') or a 4-char signature.")]
    public string? RecordType { get; init; }

    [JsonPropertyName("editorid"), Description("REQUIRED. The EditorID the new record is referenced by. A nested child's parent= can name this editorid (a same-call sibling parent).")]
    public string? Editorid { get; init; }

    [JsonPropertyName("operations"), Description("Optional. The new record's fields, same shape as bulk_apply ops but with NO formid: {field_path, verb?, value?, key?, values?, entries?, compose?}.")]
    public BulkOp[]? Operations { get; init; }

    [JsonPropertyName("parent"), Description("Optional. For a NESTED record: the parent it nests under — an EXISTING parent's FormID 'XXXXXX:Plugin.esp', OR the editorid of a record declared EARLIER in this same records array (a same-call sibling). Omit for a flat top-level record.")]
    public string? Parent { get; init; }

    [JsonPropertyName("collection"), Description("Optional. Which of the parent's child-collections to add into, BY NAME (e.g. a cell's 'Persistent') — needed only when more than one fits. Omit when unique or when parent is omitted.")]
    public string? Collection { get; init; }
}

/// <summary>A modeled struct built from parts (wire shape of <see cref="StructSpec"/>): the concrete type, optional
/// flat coercible sub-fields, optional positional ctor args, and nested edits applied to the built struct.</summary>
public sealed record StructInput
{
    [JsonPropertyName("type"), Description("The concrete catalog type to build (arm type for a polymorphic Set; the collection's element type for an Add, e.g. 'LeveledItemEntry'; or a polymorphic element's concrete ARM, e.g. 'ScriptObjectProperty' into VMAD Properties).")]
    public string? Type { get; init; }

    [JsonPropertyName("fields"), Description("Flat coercible sub-fields set directly on the struct: name → value.")]
    public Dictionary<string, string>? Fields { get; init; }

    [JsonPropertyName("ctor_args"), Description("Positional constructor args, for struct types that require them.")]
    public string[]? CtorArgs { get; init; }

    [JsonPropertyName("sets"), Description("Nested edits applied to the built struct (paths rooted at it), e.g. {path:'Data.Reference', value:'<FormID>'}.")]
    public NestedSet[]? Sets { get; init; }
}

/// <summary>One nested edit inside a <see cref="StructInput"/> (a path+verb+value rooted at the struct being built).</summary>
public sealed record NestedSet
{
    [JsonPropertyName("path"), Description("Dotted path within the struct, e.g. 'Data.Level'.")]
    public string? Path { get; init; }

    [JsonPropertyName("verb"), Description("Set (default) | Add | Remove | SetAtIndex.")]
    public string Verb { get; init; } = "Set";

    [JsonPropertyName("value"), Description("The value (coerced).")]
    public string? Value { get; init; }

    [JsonPropertyName("key"), Description("Dict key or list index, if the nested target is a collection.")]
    public string? Key { get; init; }

    [JsonPropertyName("compose"), Description("Build a modeled sub-struct for THIS nested target (recursive): the concrete ARM of a polymorphic sub-field (e.g. a Condition's Data → 'GetActorValueConditionData'), or the element for a struct-element Add nested inside the struct. Omit for a coercible scalar (use value=).")]
    public StructInput? Compose { get; init; }
}
