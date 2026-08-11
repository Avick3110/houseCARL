using System.ComponentModel;
using System.Text;
using System.Text.Json;
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
         "sessions). To edit an EXISTING plugin IN PLACE instead — rewriting your ORIGINAL file (incl. a mod houseCARL " +
         "didn't make), not a patch — pass target=<plugin filename> + in_place=true (opt-in; see those params; the default " +
         "patch lane leaves originals untouched). Pre-flight rejects an illegal edit with the reason and writes nothing (Q3). Returns the patch path, " +
         "its masters, and the value read back. Does NOT compose modeled structs (leveled-list entries, polymorphic " +
         "fields), edit a dict via Merge, or copy a field from another plugin's version (CopyFrom) — use " +
         "housecarl_bulk_apply for those, or for many edits in one patch. Read first with housecarl_read_record.")]
    public static string SetField(
        LoadOrderService svc,
        [Description("The record's FormID as 'XXXXXX:Plugin.esp' (6 hex digits, the defining master's filename).")]
            string formid,
        [Description("Dotted field path to edit, e.g. 'BasicStats.Damage', 'Name', 'Keywords'. Step into a list/dict element MID-PATH with brackets, e.g. 'Effects[0].Data.Magnitude' or 'VirtualMachineAdapter.Aliases[0].Scripts[0].Properties'. At the LEAF, edit a collection element with verb + key (SetAtIndex/Remove by index, Set/Remove by dict key) — not brackets.")]
            string field_path,
        [Description("The value, coerced to the field's type: a number, an enum name (e.g. 'OneHanded'), or a FormID 'XXXXXX:Plugin.esp' for a reference. On a [Flags] enum, the flag(s) to Add/Remove (a name or comma-combo, e.g. 'ManualCostCalc'). Omit only for a Remove that whole-clears a NULLABLE field (scalar/link/flags → cleared/absent); a flags Remove WITH a value clears just that bit, and to turn all bits off Set the field to '0'.")]
            string? value = null,
        [Description("Set (default) | Add | Remove | SetAtIndex | ReplaceAll. Set edits a scalar (or a dict element with key=); Add/Remove/SetAtIndex/ReplaceAll edit a collection. On a [Flags] enum (SPEL Flags, NPC Configuration.Flags, WEAP Data.Flags, …), Add SETS a bit and Remove CLEARS one, leaving the OTHER bits untouched — the way to flip one flag WITHOUT a Set re-listing (and silently dropping) every bit you didn't mention.")]
            string verb = "Set",
        [Description("Optional. The dict key or list index at the leaf (for a dict Set, a SetAtIndex/Remove on a list, etc.).")]
            string? key = null,
        [Description("Optional. The whole new list contents for ReplaceAll on a list (each coerced).")]
            string[]? values = null,
        [Description("Optional. Base filename for the new patch (default 'Patch'); auto-suffixed if taken so a prior patch is never overwritten. Ignored if into= is given.")]
            string patch_name = "Patch",
        [Description("Optional. Filename of an existing patch (from a prior call) to EXTEND with this edit instead of writing a fresh one — the way to accumulate edits into one patch across calls/sessions. Found by the plugin's filename even if you've renamed its MO2 mod folder; for two patches sharing a filename, pass the mod-folder name here instead (folder & plugin names need not match).")]
            string? into = null,
        [Description("Optional. IN-PLACE LANE (opt-in): the filename of an EXISTING active plugin to edit IN PLACE — including one houseCARL didn't author — instead of writing a new patch (e.g. 'CoolWeapons.esp'). Requires in_place=true; mutually exclusive with into=. OMIT this (the default) to write a NEW patch and leave every original untouched — the recommended lane.")]
            string? target = null,
        [Description("Optional, default false. With target=, edit that plugin IN PLACE: houseCARL rewrites your ORIGINAL file — no new patch, and NO houseCARL backup or undo (keep your own). It re-lays-out the whole plugin the way xEdit/CK do on save, VERIFIES the records you edit, and trusts Mutagen for the untouched rest; it refuses a file it can't parse or that holds engine-reserved (sub-0x800) records. The FIRST in-place edit of a given plugin returns a one-time confirmation prompt (re-call with acknowledge=true).")]
            bool in_place = false,
        [Description("Optional, default false. Confirms the one-time in-place trade-off for target (see in_place) — needed only on the FIRST in-place edit of a given plugin, never again for it. Waives the consent to touch your original ONLY; it NEVER skips the record verify.")]
            bool acknowledge = false,
        [Description("When true, the read-back is the FULL deep field-by-field dump of the touched record (every field, not just the edited leaf) — confirm the write landed and nothing else was disturbed, WITHOUT enabling the patch in MO2. For an IN-PLACE edit the touched-record verify ALWAYS runs and is shown COMPACTLY by default (re-read-clean + what landed, every record); true expands it to the deep dump. (The read-back is the written file's content, NOT load-order truth — the patch/edit wins nothing until enabled + sorted in MO2.)")]
            bool full_readback = false,
        [Description("Optional. Max characters for the whole response; past it the read-back is cut with an explicit notice (never silent). 0 = a safe default kept under the host's per-response token limit; raise it to widen a full_readback=true dump.")]
            int max_chars = 0,
        [Description("Optional, default false. DRY RUN: run the FULL write pipeline — winner resolve, schema pre-flight, the edit applied in memory, the reference-resolution check — and STOP before anything touches disk (no patch file, no mod folder, no in-place rewrite). Returns what WOULD change (the would-be value, the expected masters), or EXACTLY the refusal the real call would give. Works on every lane (fresh patch / into= / in_place; an in-place dry run needs no acknowledge and never records consent). Not a disk guarantee: a serialize/commit fault still surfaces only on the real write.")]
            bool dry_run = false) => Guard.Tool("housecarl_set_field", () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        var op = new BulkOp
        {
            Formid = formid, FieldPath = field_path, Verb = verb, Value = value, Key = key, Values = values,
        };
        return Render(svc.ApplyEdits(new[] { op }, patch_name, into, full_readback, target, in_place, acknowledge, dry_run), max_chars, full_readback);
    });

    [McpServerTool(Name = "housecarl_bulk_apply", Title = "Apply many edits in one patch"),
     Description(
         "Apply MANY edits in ONE patch plugin (originals untouched) — the batch form of housecarl_set_field, and the way " +
         "to COMPOSE modeled structs. Each operation is {formid, field_path, verb, value?, key?, values?, entries?, " +
         "compose?, composes?}: scalar/collection verbs work as in set_field; entries (a key→value map) drives a dict Merge or " +
         "ReplaceAll; compose builds a modeled struct for an Add (a leveled-list entry, an effect — and a POLYMORPHIC " +
         "list element composes by its concrete arm type, e.g. a VMAD script property: verb=Add, " +
         "field_path='VirtualMachineAdapter.Scripts[0].Properties', compose={type:'ScriptObjectProperty', " +
         "fields:{Name:'MyProp', Flags:'Edited', Object:'XXXXXX:Plugin.esp', Alias:'-1'}}) or a polymorphic Set " +
         "(an arm) — e.g. merge a weapon into a leveled list with verb=Add, field_path='Entries', " +
         "compose={type:'LeveledItemEntry', sets:[{path:'Data.Level',value:'1'},{path:'Data.Count',value:'1'}," +
         "{path:'Data.Reference',value:'<weapon FormID>'}]}. composes is the BATCH sibling of compose — a LIST of " +
         "elements built in ONE op: verb=Add APPENDS each in order (author many leveled-list entries / condition rows " +
         "at once, e.g. field_path='Conditions', composes=[{type:'Condition',...},{type:'Condition',...}]), verb=ReplaceAll " +
         "CLEARS the list then appends each (the way to replace a whole modeled list — conditions, effects, entries). " +
         "compose and composes are mutually exclusive; a bad element refuses the whole call with per-element " +
         "(composes[i]) reasons. All edits land in ONE reviewable .esp; the patch spans " +
         "masters automatically when edits reference forms across several plugins (cross-master merge). ALL-OR-NOTHING " +
         "(Q3): if ANY operation is malformed or fails pre-flight, the whole call is refused with per-op reasons and " +
         "nothing is written — no partial patches. By default writes a fresh patch named patch_name; pass into= to extend " +
         "an existing one. PRECEDENCE with into= (pinned): a FormKey the patch ALREADY CARRIES (e.g. from a prior " +
         "housecarl_forward_record) is edited AS-IS in the patch — the op lands on the patch's own copy; only a FormKey " +
         "the patch does NOT yet carry copies the load-order winner in first. So forward_record from a source + " +
         "bulk_apply into= is THE recipe to build on a specific plugin's version while a stale winner sits above it. " +
         "To edit an EXISTING plugin IN PLACE instead — rewriting your ORIGINAL file (incl. a mod houseCARL " +
         "didn't make), not a patch — pass target=<plugin filename> + in_place=true (opt-in; the default lane leaves originals " +
         "untouched). dry_run=true validates the WHOLE batch through the real pipeline and reports what WOULD change " +
         "without writing anything — the way to catch a bad field path before the first write of a big batch, not after " +
         "the last. The ops can come from a FILE instead of inline: from_file=<absolute path> reads the SAME operations " +
         "array as a JSON manifest on disk — write a big job's ops once, dry-run the file, then apply it, and re-run the " +
         "same manifest to recover an interrupted write (overrides are idempotent). Returns the patch path, masters, and " +
         "per-op read-back.")]
    public static string BulkApply(
        LoadOrderService svc,
        [Description("The edits to apply, all into one patch. Each: {formid, field_path, verb, value?, key?, values?, entries?, compose?}. Pass this INLINE array or from_file= — exactly one of the two.")]
            BulkOp[]? operations = null,
        [Description("Optional. ABSOLUTE path to a JSON manifest FILE holding the operations array — the SAME shape as operations=, just read from disk: [{formid, field_path, verb, ...}, ...]. The big-batch lane: generate the manifest once, validate the WHOLE file with dry_run=true before the first write, then apply it in one call (and re-run the SAME file to recover an interrupted write). Mutually exclusive with operations= (pass exactly one). The path must be ABSOLUTE (the server resolves relative paths against its OWN working directory, not yours). The file is read at CALL time — a dry run validates the manifest as it is NOW, so if you edit the file afterwards, dry-run it again before the real write. Refused NAMED with nothing written (Q3) if the file is unreadable, not valid JSON (the refusal carries the line + position), not a top-level array, an empty array, or any op carries a member the shape doesn't declare (a misspelled 'field_path' is named at its element, never silently dropped).")]
            string? from_file = null,
        [Description("Optional. Base filename for the new patch (default 'Patch'); auto-suffixed if taken. Ignored if into= is given.")]
            string patch_name = "Patch",
        [Description("Optional. Filename of an existing patch to EXTEND with these edits instead of writing a fresh one (accumulate across calls/sessions). Found by the plugin's filename even if you've renamed its MO2 mod folder; for two patches sharing a filename, pass the mod-folder name here instead (folder & plugin names need not match).")]
            string? into = null,
        [Description("Optional. IN-PLACE LANE (opt-in): the filename of an EXISTING active plugin to edit IN PLACE — including one houseCARL didn't author — instead of writing a new patch (e.g. 'CoolWeapons.esp'). Requires in_place=true; mutually exclusive with into=. OMIT this (the default) to write a NEW patch and leave every original untouched — the recommended lane.")]
            string? target = null,
        [Description("Optional, default false. With target=, edit that plugin IN PLACE: houseCARL rewrites your ORIGINAL file — no new patch, and NO houseCARL backup or undo (keep your own). It re-lays-out the whole plugin the way xEdit/CK do on save, VERIFIES the records you edit, and trusts Mutagen for the untouched rest; it refuses a file it can't parse or that holds engine-reserved (sub-0x800) records. The FIRST in-place edit of a given plugin returns a one-time confirmation prompt (re-call with acknowledge=true).")]
            bool in_place = false,
        [Description("Optional, default false. Confirms the one-time in-place trade-off for target (see in_place) — needed only on the FIRST in-place edit of a given plugin, never again for it. Waives the consent to touch your original ONLY; it NEVER skips the record verify.")]
            bool acknowledge = false,
        [Description("When true, the read-back is the FULL deep field-by-field dump of every record this call touched (not just the edited leaves) — confirm composed structures (conditions, container entries) landed and nothing else was disturbed, WITHOUT enabling the patch in MO2. For an IN-PLACE edit the touched-record verify ALWAYS runs and is shown COMPACTLY by default (per record: re-read-clean + what landed, covering ALL of them); true expands it to the deep dump. (The read-back is the written file's content, NOT load-order truth — the patch/edit wins nothing until enabled + sorted in MO2.)")]
            bool full_readback = false,
        [Description("Optional. Max characters for the whole response; past it the read-back is cut with an explicit notice (never silent). 0 = a safe default kept under the host's per-response token limit; raise it to widen a full_readback=true dump.")]
            int max_chars = 0,
        [Description("Optional, default false. DRY RUN: run the FULL write pipeline over the WHOLE batch — winner resolve, schema pre-flight, every op applied in memory, the reference-resolution check — and STOP before anything touches disk (no patch file, no mod folder, no in-place rewrite). Returns per-op what WOULD change + the expected masters, or EXACTLY the all-or-nothing refusal the real call would give (every bad op named) — validate a big batch's field paths and values BEFORE the first write, not after the last. Works on every lane (fresh patch / into= / in_place; an in-place dry run needs no acknowledge and never records consent). Not a disk guarantee: a serialize/commit fault still surfaces only on the real write.")]
            bool dry_run = false) => Guard.Tool("housecarl_bulk_apply", () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        bool hasInline = operations is not null;
        // Presence, not content: an explicit blank from_file must reach ReadOpsManifest's named "from_file is
        // empty" refusal, never be silently reinterpreted as absent (PR #241 review LOW 2 — a silently-dropped
        // argument is the Q3 class the binding shim's UnknownParameters exists to name).
        bool hasFile = from_file is not null;
        if (hasInline && hasFile)
            return "error: operations and from_file are mutually exclusive — pass the ops INLINE or name a manifest FILE, not both.";
        if (!hasInline && !hasFile)
            return "error: no operations given. Pass operations=[{formid, field_path, verb, ...}, ...] inline, or from_file='<absolute path>' naming a JSON manifest file holding that same array.";
        if (hasFile)
        {
            var (fileOps, err) = ReadOpsManifest(from_file!);
            if (err is not null) return "error: " + err;
            operations = fileOps;
        }
        else if (operations!.Length == 0)
            return "error: operations is empty. Pass one or more {formid, field_path, verb, ...} edits.";
        return Render(svc.ApplyEdits(operations!, patch_name, into, full_readback, target, in_place, acknowledge, dry_run), max_chars, full_readback);
    });

    /// <summary>Read + parse a <c>from_file=</c> ops manifest (#224): an ABSOLUTE path to a JSON array of the SAME
    /// <see cref="BulkOp"/> wire shape as inline <c>operations=</c> — the parsed ops then ride the identical
    /// <see cref="LoadOrderService.ApplyEdits"/> call, so every lane (fresh patch / into= / in_place) and
    /// <c>dry_run=</c> compose with a manifest by construction. The file contract mirrors #226's <c>@file</c>
    /// formid list (FieldPredicate.ParseFormIdList): absolute path required (the server's working directory is not
    /// the caller's), and every failure names itself (Q3) — unreadable, invalid JSON (with line+column), a
    /// non-array root, an empty array, a null element, or an op member the wire shape does not declare. That last
    /// one is DELIBERATELY stricter than inline binding (the SDK binder silently DROPS an unknown member): in a
    /// hand-generated 700-op manifest a misspelled <c>field_path</c> would otherwise become a null-field op whose
    /// downstream refusal points away from the typo — the file lane refuses it BY NAME at its element instead.</summary>
    static (BulkOp[]?, string?) ReadOpsManifest(string fromFile)
    {
        var path = fromFile.Trim().Trim('"', '\'');
        if (path.Length == 0)
            return (null, "from_file is empty — give the ops manifest's absolute path.");
        if (!Path.IsPathRooted(path))
            return (null, $"from_file '{path}' must be an ABSOLUTE path — the server resolves relative paths against its OWN working directory, not yours.");
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex) { return (null, $"could not read ops manifest '{path}' — {ex.GetType().Name}: {ex.Message}"); }
        if (string.IsNullOrWhiteSpace(text))
            return (null, $"ops manifest '{path}' is empty. Expected a JSON array of operations, each the same shape as an inline operations= element ({{formid, field_path, verb, ...}}).");

        BulkOp[]? ops;
        try { ops = JsonSerializer.Deserialize<BulkOp[]>(text, ManifestJson); }
        catch (JsonException ex)
        {
            // "byte N in the line", not "column": BytePositionInLine is a UTF-8 byte offset, which skews from the
            // visual column on a non-ASCII line (a plugin filename with é) — name what it really is (review LOW 1).
            string at = ex.LineNumber is { } ln ? $" at line {ln + 1}, byte {(ex.BytePositionInLine ?? 0) + 1} in the line" : "";
            string element = ex.Path is { Length: > 2 } p ? $" (element {p})" : "";   // "$[12].foo" — pins the offending op
            return (null, $"ops manifest '{path}' could not be parsed{at}{element}: {ShearStjPosition(Guard.Flatten(ex.Message))} " +
                          "Expected a top-level JSON ARRAY of operations, each the same shape as an inline operations= element " +
                          "({formid, field_path, verb, value?, key?, values?, entries?, compose?, composes?, from_plugin?}).");
        }
        if (ops is null)
            return (null, $"ops manifest '{path}' parsed to JSON null — expected a JSON array of operations.");
        if (ops.Length == 0)
            return (null, $"ops manifest '{path}' is an empty array — give at least one {{formid, field_path, verb, ...}} edit.");
        for (int i = 0; i < ops.Length; i++)
            if (ops[i] is null)
                return (null, $"ops manifest '{path}': element [{i}] is null — every element must be an operation object.");
        return (ops, null);
    }

    /// <summary>STJ appends its own position block (" Path: $[0].x | LineNumber: 0 | BytePositionInLine: 89.") to a
    /// JsonException message — 0-based, so it CONTRADICTS the 1-based position the refusal already leads with (PR
    /// #241 review LOW 1). Shear it; the element path it carried is surfaced separately via <c>ex.Path</c>.</summary>
    static string ShearStjPosition(string msg)
    {
        int i = msg.IndexOf(" Path: ", StringComparison.Ordinal);
        return i < 0 ? msg : msg[..i].TrimEnd();
    }

    /// <summary>Manifest parse options: property names case-insensitive like the SDK's inline binder (Web defaults),
    /// comments + trailing commas tolerated (hand-generated files), and an unknown member REFUSED by name — the
    /// deliberate strictness documented on <see cref="ReadOpsManifest"/>.</summary>
    static readonly JsonSerializerOptions ManifestJson = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    [McpServerTool(Name = "housecarl_remove_record", Title = "Remove a whole record from a patch (or a plugin in place)"),
     Description(
         "Remove a WHOLE record from a houseCARL patch — a literal drop-from-plugin (NOT a flag-as-deleted stub). The " +
         "companion to the edit tools: where set_field/bulk_apply ADD an override into a patch, this drops one OUT of it. " +
         "Only works on a record the patch ITSELF carries — one houseCARL created, or an override the patch accumulated " +
         "via a prior set_field/bulk_apply into=patch. You CANNOT remove a record that lives in a master/another mod; you " +
         "can only drop THIS patch's override of it, which makes the load-order winner revert (the patch stops touching " +
         "that record). patch names an existing houseCARL-owned patch (the same name you pass to into=) and is REQUIRED in " +
         "this default lane; removal targets a patch that already carries the record. Refuses loud and writes nothing (Q3) " +
         "if the patch does not carry the FormID. Unused masters are pruned automatically — if the removed record held the " +
         "patch's last reference to a master, that master drops from the header on the re-write. Reaches records in ANY " +
         "group (incl. cells, placed references, dialog, navmesh). Returns what was removed, the patch's remaining masters, " +
         "and how many records remain. To remove the record straight from an EXISTING plugin IN PLACE instead — dropping it " +
         "from your ORIGINAL file (incl. a mod houseCARL didn't make), not a patch — pass target=<plugin filename> + " +
         "in_place=true (opt-in; the default patch lane leaves originals untouched). In place the rule is the same: you can " +
         "only drop a record the TARGET file itself defines or OVERRIDES (dropping an override it holds reverts that record " +
         "to the load-order winner underneath); a FormID the target doesn't carry is refused. To remove a list ENTRY (a " +
         "keyword, an item) rather than a whole record, use set_field with verb=Remove instead.")]
    public static string RemoveRecord(
        LoadOrderService svc,
        [Description("The record's FormID as 'XXXXXX:Plugin.esp' — the record to drop.")]
            string formid,
        [Description("DEFAULT LANE: filename of the houseCARL patch to remove the record from (e.g. 'MyMerge.esp' or 'MyMerge') — must be a patch houseCARL created that carries this record. Found by the plugin's filename even if you've renamed its MO2 mod folder; for two patches sharing a filename, pass the mod-folder name here instead (folder & plugin names need not match). REQUIRED unless you use the in-place lane (target + in_place); omit it then.")]
            string? patch = null,
        [Description("Optional. IN-PLACE LANE (opt-in): the filename of an EXISTING active plugin to remove the record from IN PLACE — including one houseCARL didn't author — instead of from a houseCARL patch (e.g. 'CoolWeapons.esp'). Requires in_place=true; mutually exclusive with patch. Drops only a record the TARGET itself defines or overrides; a FormID it doesn't carry is refused. OMIT this (the default) to drop the record from a houseCARL patch and leave every original untouched.")]
            string? target = null,
        [Description("Optional, default false. With target=, remove the record straight from that plugin IN PLACE: houseCARL rewrites your ORIGINAL file — no patch, and NO houseCARL backup or undo (keep your own). houseCARL re-lays-out the whole plugin the way xEdit/CK do on save, VERIFIES the record is gone, and trusts Mutagen for the untouched rest; it refuses a file it can't parse or that holds engine-reserved (sub-0x800) records. Any master the removal orphans is pruned from the header. The FIRST in-place write to a given plugin returns a one-time confirmation prompt (re-call with acknowledge=true).")]
            bool in_place = false,
        [Description("Optional, default false. Confirms the one-time in-place trade-off for target (see in_place) — needed only on the FIRST in-place write to a given plugin (edit, create, OR remove), never again for it. Waives the consent to touch your original ONLY; it NEVER skips the record verify.")]
            bool acknowledge = false) => Guard.Tool("housecarl_remove_record", () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        return RenderRemoval(svc.RemoveRecords(new[] { formid }, patch, target, in_place, acknowledge));
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
         "in ONE call (a topic + its lines), use housecarl_bulk_create. For an abstract record group (Global, GameSetting), name " +
         "the CONCRETE subtype directly ('GlobalFloat'/'GlobalInt'/'GlobalShort', 'GameSettingFloat'/'GameSettingInt'/'GameSettingString') " +
         "— that's how a global variable or game setting is created. The new FormID is reported back; to make ANOTHER record " +
         "reference it, call this or set_field again with into='<this patch>' using that FormID. By default writes a fresh patch " +
         "named patch_name; into= extends an existing houseCARL patch (accumulate across calls/sessions). To create the new " +
         "record straight INTO an EXISTING plugin IN PLACE instead — rewriting your ORIGINAL file (incl. a mod houseCARL didn't " +
         "make), not a patch — pass target=<plugin filename> + in_place=true (opt-in; the " +
         "default patch lane leaves originals untouched). ALL-OR-NOTHING (Q3): " +
         "the whole call is refused with a reason and nothing is written if the type can't be created (an EXTERIOR cell nests " +
         "under FormKey-less worldspace structs — a separate capability; the bare abstract base 'Global'/'GameSetting' needs a concrete subtype — the refusal names them), if " +
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
        [Description("Optional. For an EXTERIOR cell (record_type 'Cell' with parent= a Worldspace FormID): the cell's grid as \"X,Y\" (e.g. \"5,-12\") — houseCARL files it into the worldspace's block tree (block=floor(grid/32), subblock=floor(grid/8)). A 'Cell' with NO parent and NO grid is an INTERIOR cell (self-files by FormID). Ignored for non-Cell types.")]
            string? grid = null,
        [Description("Optional. Base filename for the new patch (default 'Patch'); auto-suffixed if taken. Ignored if into= is given.")]
            string patch_name = "Patch",
        [Description("Optional. Filename of an existing houseCARL patch to add this new record to instead of writing a fresh one (accumulate across calls/sessions). Found by the plugin's filename even if you've renamed its MO2 mod folder; for two patches sharing a filename, pass the mod-folder name here instead (folder & plugin names need not match).")]
            string? into = null,
        [Description("Optional. IN-PLACE LANE (opt-in): the filename of an EXISTING active plugin to create the new record straight INTO, IN PLACE — including one houseCARL didn't author — instead of writing a new patch (e.g. 'CoolWeapons.esp'). Requires in_place=true; mutually exclusive with into=. Full create parity in place — incl. a nested record (parent=): a parent the target already owns is edited to host the child, a parent from another plugin is overridden in (exactly as the patch lane does). OMIT this (the default) to write a NEW patch and leave every original untouched — the recommended lane.")]
            string? target = null,
        [Description("Optional, default false. With target=, create the new record straight INTO that plugin IN PLACE: houseCARL rewrites your ORIGINAL file — no new patch, and NO houseCARL backup or undo (keep your own). The new record gets a fresh FormID in that plugin's OWN range; houseCARL re-lays-out the whole plugin the way xEdit/CK do on save, VERIFIES the record it created, and trusts Mutagen for the untouched rest; it refuses a file it can't parse or that holds engine-reserved (sub-0x800) records. The FIRST in-place write to a given plugin returns a one-time confirmation prompt (re-call with acknowledge=true).")]
            bool in_place = false,
        [Description("Optional, default false. Confirms the one-time in-place trade-off for target (see in_place) — needed only on the FIRST in-place write to a given plugin (edit OR create), never again for it. Waives the consent to touch your original ONLY; it NEVER skips the record verify.")]
            bool acknowledge = false,
        [Description("When true, the read-back is the FULL deep field-by-field dump of the created record (every field, not just the fields you set). For an IN-PLACE create the touched-record verify ALWAYS runs and is shown COMPACTLY by default (re-read-clean + field count per record); true expands it to the deep dump. (The read-back is the written file's content, NOT load-order truth — the patch/edit wins nothing until enabled + sorted in MO2.)")]
            bool full_readback = false,
        [Description("Optional. Max characters for the whole response; past it the read-back is cut with an explicit notice (never silent). 0 = a safe default kept under the host's per-response token limit; raise it to widen a full_readback=true dump.")]
            int max_chars = 0) => Guard.Tool("housecarl_create_record", () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        return RenderCreate(svc.CreateRecords(record_type, editorid, operations ?? Array.Empty<BulkOp>(), patch_name, into, full_readback, parent, collection, grid, target, in_place, acknowledge), max_chars, full_readback);
    });

    [McpServerTool(Name = "housecarl_bulk_create", Title = "Create many records (incl. a nested one-shot) in one patch"),
     Description(
         "Create MANY brand-new records in ONE patch plugin (originals untouched) — the batch form of housecarl_create_record, " +
         "and the way to author a NESTED unit in a single call: a dialogue topic AND its lines, a cell AND its placed refs. " +
         "records is an array of {record_type, editorid, operations?, parent?, collection?} — each spec is exactly a " +
         "create_record call. A spec's parent= can be the FormID of an EXISTING record OR the editorid of a record declared " +
         "EARLIER in this same records array (a same-call sibling) — which is how the one-shot 'topic + its lines' is expressed: " +
         "records=[{record_type:'DialogTopic', editorid:'MyTopic'}, {record_type:'DialogResponses', editorid:'MyTopic_L1', " +
         "parent:'MyTopic', operations:[{field_path:'Prompt', value:'Hello'}]}] (declare the topic BEFORE the lines). A FormLink " +
         "field VALUE can ALSO reference a same-call sibling, written '@editorid' — so a dialogue line's order-chain and its " +
         "topic back-link are authored in the SAME call: e.g. on a line, operations:[{field_path:'Topic', value:'@MyTopic'}, " +
         "{field_path:'PreviousDialog', value:'@MyTopic_L1'}] points it at the same-call topic and the prior line. Each '@editorid' " +
         "must name a record declared EARLIER in this array OR the record being created ITSELF (self-reference — e.g. a quest's " +
         "VMAD alias fragment whose Property.Object is the quest: value:'@MyQuest' on MyQuest's own operation; works in single " +
         "create_record too); it resolves to that record's auto-allocated FormID. (Only on FormLink fields — including formlink " +
         "fields/sets inside a compose spec.) collection= " +
         "names which child-list when the parent holds more than one that fits (e.g. a cell's 'Persistent'). Each new FormID is " +
         "auto-allocated (the patch's own 0x800+ range) and returned. ALL-OR-NOTHING (Q3): if ANY spec is malformed or fails " +
         "pre-flight (unknown/ambiguous type, missing editorid, illegal field op, a nested child with no resolvable parent, an " +
         "ambiguous collection), the whole call is refused with per-record reasons and nothing is written — no partial patches. " +
         "By default writes a fresh patch named patch_name; into= extends an existing houseCARL patch — and a parent created in " +
         "a PRIOR into= call CAN be the parent here too (it's resolved from the patch being extended, not only the load order). " +
         "To create the new records straight INTO an EXISTING plugin IN PLACE instead — rewriting your ORIGINAL file (incl. a mod " +
         "houseCARL didn't make), not a patch — pass target=<plugin filename> + in_place=true (opt-in; the default patch lane " +
         "leaves originals untouched). " +
         "Returns each new record's FormID + editorid, the patch path, and its (derived) masters.")]
    public static string BulkCreate(
        LoadOrderService svc,
        [Description("The records to create, all into one patch. Each: {record_type, editorid, operations?, parent?, collection?}. For a nested one-shot, declare the parent (e.g. a DialogTopic) BEFORE the children whose parent= names its editorid.")]
            CreateOp[] records,
        [Description("Optional. Base filename for the new patch (default 'Patch'); auto-suffixed if taken. Ignored if into= is given.")]
            string patch_name = "Patch",
        [Description("Optional. Filename of an existing houseCARL patch to add these new records to instead of writing a fresh one (accumulate across calls/sessions). Found by the plugin's filename even if you've renamed its MO2 mod folder; for two patches sharing a filename, pass the mod-folder name here instead (folder & plugin names need not match).")]
            string? into = null,
        [Description("Optional. IN-PLACE LANE (opt-in): the filename of an EXISTING active plugin to create the new records straight INTO, IN PLACE — including one houseCARL didn't author — instead of writing a new patch (e.g. 'CoolWeapons.esp'). Requires in_place=true; mutually exclusive with into=. Full create parity in place — incl. a nested one-shot (a topic AND its lines, a cell AND its refs): a same-call or target-owned parent hosts the child, a parent from another plugin is overridden in (exactly as the patch lane does). OMIT this (the default) to write a NEW patch and leave every original untouched — the recommended lane.")]
            string? target = null,
        [Description("Optional, default false. With target=, create the new records straight INTO that plugin IN PLACE: houseCARL rewrites your ORIGINAL file — no new patch, and NO houseCARL backup or undo (keep your own). Each new record gets a fresh FormID in that plugin's OWN range; houseCARL re-lays-out the whole plugin the way xEdit/CK do on save, VERIFIES the records it created, and trusts Mutagen for the untouched rest; it refuses a file it can't parse or that holds engine-reserved (sub-0x800) records. The FIRST in-place write to a given plugin returns a one-time confirmation prompt (re-call with acknowledge=true).")]
            bool in_place = false,
        [Description("Optional, default false. Confirms the one-time in-place trade-off for target (see in_place) — needed only on the FIRST in-place write to a given plugin (edit OR create), never again for it. Waives the consent to touch your original ONLY; it NEVER skips the record verify.")]
            bool acknowledge = false,
        [Description("When true, the read-back is the FULL deep field-by-field dump of each created record. For an IN-PLACE create the touched-record verify ALWAYS runs and is shown COMPACTLY by default (re-read-clean + field count per record); true expands it to the deep dump. (The read-back is the written file's content, NOT load-order truth — the patch/edit wins nothing until enabled + sorted in MO2.)")]
            bool full_readback = false,
        [Description("Optional. Max characters for the whole response; past it the read-back is cut with an explicit notice (never silent). 0 = a safe default kept under the host's per-response token limit; raise it to widen a full_readback=true dump.")]
            int max_chars = 0) => Guard.Tool("housecarl_bulk_create", () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        if (records is null || records.Length == 0)
            return "error: records is empty. Pass one or more {record_type, editorid, operations?, parent?, collection?} specs.";
        return RenderCreate(svc.CreateRecordsBatch(records, patch_name, into, full_readback, target, in_place, acknowledge), max_chars, full_readback);
    });

    [McpServerTool(Name = "housecarl_forward_record", Title = "Forward a plugin's version of a record as an override"),
     Description(
         "Forward a SPECIFIC plugin's version of one-or-more records into a NEW patch as an override (originals untouched) " +
         "— xEdit's \"copy as override into\", the INVERSE of set_field/bulk_apply. Those edit the load-order WINNER; this " +
         "copies from_plugin's whole record VERBATIM, so from_plugin's version (NOT the winner) becomes the patch's content " +
         "— the way to RE-ASSERT an earlier mod's version over a later override (e.g. a late total-overhaul re-architected a " +
         "record an earlier list patch had balanced — forward the earlier plugin's version back on top), or to REVERT a " +
         "record to vanilla (name a master — Skyrim.esm/Update.esm/… — as from_plugin). formids are 'XXXXXX:Plugin.esp' " +
         "(one or more, ALL copied from the SAME from_plugin); call again with into= to forward from a different source into " +
         "the same patch. This copies the record WHOLE — it does NOT edit fields (use set_field/bulk_apply for that) and " +
         "needs no field pre-flight (a complete source record is legal by construction). Forwarding does NOT add from_plugin " +
         "as a master: the patch overrides the record's ORIGIN FormKey with the copied body, so the header carries the origin " +
         "master + whatever the body references (exactly xEdit's copy-as-override-into-a-new-patch). ALL-OR-NOTHING (Q3): the " +
         "whole call is refused with a named reason and NOTHING is written if from_plugin is found in NEITHER the load order " +
         "NOR on disk, matches several mod folders, was " +
         "excluded (unparseable), is the artifact being written itself, names a target twice, simply doesn't DEFINE/override a given " +
         "record (nothing there to forward), or names a record whose ORIGIN plugin isn't active. By default writes a fresh patch named patch_name; into= EXTENDS an existing " +
         "houseCARL patch (accumulate across calls/sessions). If the extended patch ALREADY carries a forwarded FormKey, its " +
         "existing override is REPLACED by from_plugin's body (xEdit's copy-as-override overwrite — flagged per record in the " +
         "response). target= + in_place=true is the opt-in THIRD route: forward INTO an existing plugin's OWN file (incl. one " +
         "houseCARL didn't author) — same replace-on-collision semantics, same one-time acknowledge= consent as the sibling " +
         "write tools, master header grown from the copied bodies. THE STALE-WINNER BYPASS RECIPE (pinned): forward from " +
         "the source you want, then bulk_apply into= the same patch — the ops edit the patch's FORWARDED copy, never " +
         "re-resolve the (stale) load-order winner, so you build on the forwarded body directly. Returns the patch path, " +
         "masters, and per-record what was copied + the current winner it will out-rank (a forward whose version is " +
         "ALREADY winning is flagged redundant).")]
    public static string ForwardRecord(
        LoadOrderService svc,
        [Description("The record(s) to forward, each as 'XXXXXX:Plugin.esp' (6 hex digits, the defining master's filename). All are copied from the SAME from_plugin.")]
            string[] formids,
        [Description("The plugin whose version of the record(s) to copy (e.g. 'Authoria - ATweaks.esp', or a master like 'Skyrim.esm' to revert to vanilla). Active OR only on disk — a DISABLED mod's plugin, an unticked one, or a full path to any copy; it must DEFINE or override each formid. An off-order read names the exact file it opened.")]
            string from_plugin,
        [Description("Optional. Base filename for the new patch (default 'Patch'); auto-suffixed if taken so a prior patch is never overwritten. Ignored if into= is given.")]
            string patch_name = "Patch",
        [Description("Optional. Filename of an existing houseCARL patch to ADD these forwards to instead of writing a fresh one (accumulate across calls — e.g. forward from a different source plugin into the same patch). Found by the plugin's filename even if you've renamed its MO2 mod folder; for two patches sharing a filename, pass the mod-folder name here instead (folder & plugin names need not match). If the patch already carries a forwarded FormKey, its existing override is REPLACED by from_plugin's body.")]
            string? into = null,
        [Description("When true, the response ALSO returns each forwarded record IN FULL, read back from the written patch file on disk (every field, deep). The pre-enable verification: confirm the copied version is exactly the source's, WITHOUT enabling the patch in MO2 (the written file's content, not load-order truth).")]
            bool full_readback = false,
        [Description("Optional. IN-PLACE LANE (opt-in): the filename of an EXISTING active plugin to forward INTO in place — including one houseCARL didn't author — instead of writing a new patch (e.g. 'MyHandmadePatch.esp'). Requires in_place=true; mutually exclusive with into=. A FormKey the target already carries is REPLACED by from_plugin's body (xEdit's copy-as-override overwrite). OMIT this (the default) to write a NEW patch and leave every original untouched — the recommended lane.")]
            string? target = null,
        [Description("Optional, default false. Confirms the IN-PLACE lane together with target= (see target). Your ORIGINAL plugin file is rewritten — no houseCARL backup or undo. Defaults OFF: omitting it always writes a new patch.")]
            bool in_place = false,
        [Description("Optional, default false. Confirms the one-time in-place trade-off for target (see in_place) — needed only on the FIRST in-place write to a given plugin (edit, create, remove, OR forward), never again for it. Waives the consent to touch your original ONLY; it NEVER skips the record verify.")]
            bool acknowledge = false,
        [Description("Optional. Max characters for the whole response; past it the read-back is cut with an explicit notice (never silent). 0 = a safe default kept under the host's per-response token limit; raise it to widen a full_readback=true dump.")]
            int max_chars = 0,
        [Description("Optional, default false. DRY RUN: resolve every record from from_plugin, copy each into the in-memory would-be patch, and STOP before anything touches disk (no patch file, no mod folder, no in-place rewrite). Returns what WOULD be forwarded (per record: source, the winner it would out-rank, replace/redundant flags) + the expected masters, or EXACTLY the refusal the real call would give. Works on every lane (fresh patch / into= / in_place; an in-place dry run needs no acknowledge and never records consent). Not a disk guarantee: a serialize/commit fault still surfaces only on the real write.")]
            bool dry_run = false) => Guard.Tool("housecarl_forward_record", () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        if (formids is null || formids.Length == 0)
            return "error: formids is empty. Pass one or more 'XXXXXX:Plugin.esp' FormIDs to forward from from_plugin.";
        if (string.IsNullOrWhiteSpace(from_plugin))
            return "error: from_plugin is empty. Name the plugin whose version of the record(s) to forward.";
        return RenderForward(svc.ForwardRecords(formids, from_plugin, patch_name, into, full_readback, target, in_place, acknowledge, dry_run), max_chars);
    });

    [McpServerTool(Name = "housecarl_create_plugin", Title = "Create an empty header-only (trigger) plugin"),
     Description(
         "Create an EMPTY, HEADER-ONLY plugin — a valid TES4 header with ZERO records and no masters, in a NEW mod " +
         "folder (originals untouched). Its only job is to EXIST so its basename resolves: the artifact that SKSE configs " +
         "binding by plugin basename need (e.g. a CraftingCategories-style trigger that must ship 'Foo.esp' so 'Foo.json' " +
         "loads), a placeholder ESL for FormID reservation, a dummy plugin for another mod to list as a master, or any " +
         "'I just need plugin Foo to be present' case. UNLIKE housecarl_create_record, it authors NO record — so it adds no conflict-tree footprint " +
         "(no filler override needed to make the plugin non-empty). plugin_name is used EXACTLY (the basename is " +
         "load-bearing — houseCARL will NOT auto-suffix it): if a plugin of that name is already active in the load order, " +
         "or a houseCARL folder of that name already exists, it REFUSES loud rather than rename or overwrite (Q3). Pass " +
         "esl=true for the lightest trigger (a header-only light plugin consumes no consequential load-order slot; with " +
         "zero records the ESL FormID-range rule is trivially satisfied). author/description are optional TES4 header " +
         "text. Returns the plugin path + mod folder — enable + sort it in MO2 to use it. To author actual records, use " +
         "housecarl_create_record / housecarl_bulk_create instead.")]
    public static string CreatePlugin(
        LoadOrderService svc,
        [Description("The EXACT plugin name (with or without a trailing .esp/.esm/.esl; e.g. 'Authoria - CraftingCategories'). Used VERBATIM as the basename — houseCARL will not auto-suffix it, because a trigger plugin's whole job is that its basename matches the config bound to it. The written file is '<name>.esp'.")]
            string plugin_name,
        [Description("When true, flag the plugin as a light master (ESL) — the lightest possible trigger: a header-only ESL consumes no consequential load-order slot. Default false (a normal full plugin).")]
            bool esl = false,
        [Description("Optional. Author text for the TES4 header (the CNAM field). Purely informational.")]
            string? author = null,
        [Description("Optional. Description text for the TES4 header (the SNAM field). Purely informational.")]
            string? description = null) => Guard.Tool("housecarl_create_plugin", () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        if (string.IsNullOrWhiteSpace(plugin_name))
            return "error: plugin_name is empty. Name the plugin to create (a header-only plugin has no record to derive a name from).";
        return RenderCreatePlugin(svc.CreatePlugin(plugin_name, esl, author, description));
    });

    [McpServerTool(Name = "housecarl_compact_plugin", Title = "Compact / ESL-renumber a plugin's FormIDs"),
     Description(
         "COMPACT a plugin's FormIDs — the data-layer twin of xEdit's \"Compact FormIDs for ESL\". Renumbers EVERY record " +
         "the plugin DEFINES (its originating records — flat AND nested: cells, placed references, dialogue lines, navmesh, " +
         "landscape) into the light/ESL range 0x800–0xFFF (the 2048-ID window), repoints every reference WITHIN the plugin, " +
         "leaves its overrides of other mods at their master FormIDs, and flags the result a light master (ESPFE) so it " +
         "frees a load-order slot. esl=false instead renumbers contiguously from 0x800 with NO light flag/ceiling (to close " +
         "FormID gaps). OUTPUT (default): a NEW plugin keeping the SOURCE'S EXACT basename (so other mods that list it as a " +
         "master still resolve) in a fresh houseCARL mod folder — your ORIGINAL is untouched; review the new one in xEdit, " +
         "then in MO2 enable its folder and DISABLE the original mod (same basename — MO2 serves one). in_place=true instead " +
         "OVERWRITES the original (xEdit's norm; rides the in-place consent, NO backup; needs acknowledge=true). " +
         "THE SAFETY (Q3): renumbering breaks any reference from OUTSIDE this plugin (they'd point at FormIDs that vanish). " +
         "houseCARL scans the WHOLE load order for such external referencers (a one-pass walk — can take ~25s on a big order): " +
         "if NONE, it's a clean compaction; if SOME, the call is REFUSED and lists them, UNLESS repoint_externals=true, which " +
         "ALSO rewrites each of them in place to follow the renumber (needs acknowledge=true; no backup of them either). " +
         "The target need NOT be active: a plugin on disk but not (yet) in the load order — e.g. the patch houseCARL just " +
         "wrote, before the MO2 refresh — is resolved by filename across ALL mod folders and compacted OFF-ORDER (its " +
         "declared masters must still be active). An override-only plugin with esl=true takes the FLAG-ONLY lane: nothing " +
         "to renumber, every record copies verbatim, the ESL flag is set (always valid — the light window only constrains " +
         "originating records). Refuses loud + writes nothing on: the plugin found nowhere on disk / ambiguous across " +
         "folders / unparseable; an override-only plugin with esl=false (nothing to do); MORE records than the light " +
         "range holds (the hard 2048 ESL ceiling — named, never truncated); a declared master not active; a serialize fault. " +
         "Note: references compiled into Papyrus scripts (.pex hardcoded FormIDs / GetFormFromFile) are NOT remappable — " +
         "verify scripted records after compacting.")]
    public static string CompactPlugin(
        LoadOrderService svc,
        [Description("The plugin's filename to compact (e.g. 'CoolMod.esp'). Usually active in your load order; a plugin on disk but not in the order (a fresh houseCARL patch, a disabled mod) is resolved by filename and compacted OFF-ORDER — its declared masters must still be active. The compacted output keeps this EXACT basename.")]
            string plugin,
        [Description("When true (default), renumber into the light/ESL range (0x800–0xFFF, 2048 IDs) and flag the result a light master (ESPFE) — the canonical 'compact for ESL'. false = renumber contiguously from 0x800 with no light flag or 2048 ceiling (just closes FormID gaps).")]
            bool esl = true,
        [Description("Optional, default false. IN-PLACE LANE (opt-in): OVERWRITE the original plugin with its compacted form (xEdit's norm) instead of writing a new file — NO houseCARL backup or undo (keep your own). Requires acknowledge=true. OMIT (the default) to write a NEW plugin (same basename, fresh mod folder) and leave the original untouched for review.")]
            bool in_place = false,
        [Description("Optional, default false. If OTHER plugins reference records being renumbered, compaction would break them and the call REFUSES (listing them) by default. Set true to ALSO rewrite those external referencers IN PLACE to follow the renumber (requires acknowledge=true; no backup of them either).")]
            bool repoint_externals = false,
        [Description("Optional, default false. Confirms the in-place trade-off when in_place=true OR repoint_externals=true (your original file(s) get rewritten, no backup). The FIRST such call without it returns a CONFIRM prompt listing exactly what will be overwritten — re-call with acknowledge=true to proceed.")]
            bool acknowledge = false,
        [Description("Optional. Base name for the NEW mod folder (new-file lane only; auto-suffixed if taken). Ignored with in_place=true. The PLUGIN inside ALWAYS keeps the source's exact basename so external masters still resolve.")]
            string? patch_name = null) => Guard.Tool("housecarl_compact_plugin", () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        if (string.IsNullOrWhiteSpace(plugin))
            return "error: plugin is empty. Name the plugin filename to compact (e.g. 'CoolMod.esp').";
        return RenderCompact(svc.CompactPlugin(plugin, esl, in_place, repoint_externals, acknowledge, patch_name));
    });

    [McpServerTool(Name = "housecarl_merge_plugins", Title = "Merge plugins into one new plugin"),
     Description(
         "MERGE two or more ACTIVE plugins into ONE NEW plugin — a RECORDS operation (the zMerge/'Merge Plugins' job): the " +
         "donors' records combine under a new filename; the donor FILES and their mods are NEVER touched (new-file lane only, " +
         "no in-place). RENUMBER is collision-only (zMerge's default): the donor EARLIEST in the load order keeps its FormID " +
         "object ids; later donors renumber only ids already taken (all records necessarily move to the new plugin's identity). " +
         "Cross-donor conflicts on the SAME record resolve to the LOAD-ORDER WINNER and are each REPORTED; a losing donor's " +
         "nested children the winner doesn't re-list (a base mod's dialogue lines under a patched topic; placed refs under a " +
         "patched cell) are GRAFTED into the winner's copy — so merging a mod WITH its patches is the intended use. ASSETS " +
         "follow the renumber: every donor NPC's facegen and every voiced line are carried into the new plugin-name folders " +
         "(those paths embed the plugin NAME, so ALL donor facegen/voice moves, not just collisions), and a .seq is refreshed " +
         "when any donor shipped one. THE SAFETY (Q3): plugins OUTSIDE the merge that reference or override donor records are " +
         "WARNED and NAMED, never refused — the donors stay active until you swap in MO2, so nothing breaks at write time; the " +
         "remedy is to include those patches in the merge set or re-point them before disabling the donors. Refuses loud + " +
         "writes nothing on: a donor not active / unparseable / not on disk; an output name already in the load order; a " +
         "dangling donor-internal reference (a donor referencing a FormID no donor defines); a declared master not active. " +
         "AFTER: review the merged plugin in xEdit, enable its mod folder in MO2, then deactivate the donor PLUGINS (right " +
         "pane) but KEEP the donor MOD FOLDERS enabled (left pane) — merge carries only the FormID-keyed files the rename " +
         "breaks (facegen/voice/seq); every other donor asset (meshes, textures, scripts, BSA contents) is still referenced " +
         "BY PATH from the merged records and loads from the donor folders. Caveat: a donor .bsa stops auto-loading once its " +
         "same-named plugin is inactive — extract it into the mod folder (housecarl_bsa_extract) or load it via a same-named " +
         "dummy plugin (housecarl_create_plugin). Existing SAVES that depend on the donors will NOT survive (new plugin name " +
         "+ renumbered FormIDs) — best for a new game. Want it light/ESL? Run housecarl_compact_plugin on the merged plugin " +
         "afterward (the tools compose).")]
    public static string MergePlugins(
        LoadOrderService svc,
        [Description("The donor plugin filenames to merge (at least two, e.g. [\"CoolMod.esp\", \"CoolMod Patch.esp\"]) — each must be active in your load order. Argument order does not matter: houseCARL uses LOAD order for id priority and conflict resolution.")]
            string[] plugins,
        [Description("The NEW merged plugin's filename to create (e.g. 'MyMerge.esp') — must NOT already exist in the load order. The donors keep their names and files untouched.")]
            string output,
        [Description("Optional. Base name for the NEW mod folder (auto-suffixed if taken). Defaults to '<output> merged'.")]
            string? patch_name = null) => Guard.Tool("housecarl_merge_plugins", () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        return RenderMerge(svc.MergePlugins(plugins, output, patch_name));
    });

    /// <summary>Compact, parseable confirmation (rulebook: short mutation confirmation + the IDs needed for follow-up).
    /// On refusal, the full reason (every malformed/rejected op) so the caller can fix and retry.</summary>
    internal static string Render(WritePatchBuilder.PatchOutcome o, int maxChars = 0, bool fullDump = false, bool laneAsName = false)   // internal: the compact-readback guard renders one outcome three ways
    {
        if (o.NeedsAcknowledge) return o.Error! + Epoch(o);  // the first-touch in-place CONSENT prompt — a required confirmation, NOT an error (Q3)
        if (!o.Success) return "error: " + o.Error + Epoch(o);
        if (o.DryRun) return RenderDryRun(o, maxChars, fullDump);
        var file = Path.GetFileName(o.OutputPath);
        var modFolder = Path.GetFileName(Path.GetDirectoryName(o.OutputPath) ?? "");
        var sb = new StringBuilder();
        if (o.InPlace)
            sb.Append("edited ").Append(file).Append(" IN PLACE (").Append(o.Bytes)
              .Append(" bytes — your ORIGINAL file was rewritten; no houseCARL backup or undo)\n")
              .Append("mod folder: ").Append(modFolder).Append("  — already active in your load order; re-sort only if a winner changed\n");
        else
        {
            sb.Append(o.Extended ? "extended " : "wrote ").Append(file)
              .Append(o.Extended ? " (existing patch grown; " : " (new patch; ").Append(o.Bytes).Append(" bytes)\n");
            sb.Append("mod folder: ").Append(modFolder)
              .Append(o.Extended ? "\n" : "  — enable + sort it in MO2 to use the patch\n");
        }
        sb.Append("masters: ").Append(o.Masters.Count == 0 ? "(none)" : string.Join(", ", o.Masters)).Append('\n');
        sb.Append(o.Ops.Count).Append(o.Ops.Count == 1 ? " edit:\n" : " edits:\n");
        foreach (var op in o.Ops)
            sb.Append("  ").Append(op.RecordType).Append(' ').Append(op.Target).Append("  ").Append(op.Label)
              .Append(op.After is not null ? "  -> " + op.After : "  -> applied").Append('\n');
        // Make the dialogue-coverage scope boundary VISIBLE, not silent (Q3): the .fuz/.lip presence check (unit B) AND
        // the result-script binding check (unit C1) both run on CREATE of dialogue lines, not on EDITS to existing ones.
        // An edit that adds a spoken response or a result script to an existing INFO produces the same silent-line /
        // dead-script hazard with no note here, so flag it — and point at the on-demand validator (Unit C2 shipped:
        // housecarl_validate_dialogue), which audits voice + result-script coverage AND the topic graph over the
        // edited line and every other line in the topic.
        if (o.Ops.Any(op => string.Equals(op.RecordType, VoiceCheck.InfoCatalogName, StringComparison.Ordinal)))
            sb.Append("note: this edit touched a dialogue line (INFO). Voice (.fuz) and result-script coverage are checked on CREATE, not on edits — ")
              .Append("run housecarl_validate_dialogue on the topic (or its owning quest) to audit voice + result-script coverage and the topic graph over the edited line and every other line in the topic.\n");
        // The touched-record verify (forced ON for in-place — the model-C floor substitute — and opt-in for the new-file
        // lane) renders COMPACT by default and the full field-by-field dump only on full_readback=true (HCBR-2026-06-28-01):
        // the deep dump of N records with large list fields blew past the host token cap and spilled to a file, reading as
        // "only some ops applied". The verify itself is unchanged — this is its OUTPUT, not its detection.
        if (o.ReadBack is { } rb)
        {
            if (fullDump) AppendFullReadback(sb, rb, maxChars);
            else AppendCompactReadback(sb, o.Ops, rb, maxChars);
        }
        if (o.Note is { } note) sb.Append("note: ").Append(note).Append('\n');
        sb.Append(o.InPlace
            ? InPlaceAgainHint("to make more in-place edits to this plugin", file, laneAsName)
            : $"to add more edits to THIS patch, pass into=\"{file}\".");
        sb.Append(Epoch(o));
        return sb.ToString();
    }

    /// <summary>SPEC §2.1.1 — the index build this write resolved winners against, appended to EVERY write render
    /// (success, refusal, dry run, consent prompt) exactly as the read surfaces stamp theirs. It is what lets a
    /// caller tell whether the winner it edited is the winner a read reported a moment earlier: the read-back proves
    /// what landed in the FILE, never what wins in the ORDER. Empty when the outcome consulted no build.</summary>
    static string Epoch(WritePatchBuilder.PatchOutcome o) => o.Epoch is null ? "" : $"\nepoch={o.Epoch}";

    /// <summary>The same §2.1.1 stamp for the other three write outcomes (W3 PR 2 — create / remove / forward
    /// carry the fingerprint on exactly the contract <see cref="WritePatchBuilder.PatchOutcome.Epoch"/> defines).
    /// Separate overloads rather than one interface: the outcome records are deliberately independent shapes, and
    /// a shared marker interface would be more machinery than one property read per lane.</summary>
    static string Epoch(WritePatchBuilder.CreateOutcome o) => o.Epoch is null ? "" : $"\nepoch={o.Epoch}";
    static string Epoch(WritePatchBuilder.RemovalOutcome o) => o.Epoch is null ? "" : $"\nepoch={o.Epoch}";
    static string Epoch(WritePatchBuilder.ForwardOutcome o) => o.Epoch is null ? "" : $"\nepoch={o.Epoch}";

    /// <summary>The "how to keep going on this plugin" line for a completed IN-PLACE write. The spelling differs
    /// by surface and MUST match what the CALLING tool declares (PR #311 round-2 review [medium]): the 1.x tools
    /// take the <c>target=</c> + <c>in_place=true</c> PAIR, while the 2.0 tools (apply / create / remove /
    /// forward) declare a single STRING <c>in_place="X.esp"</c> and no <c>target=</c> at all — so the old text
    /// told a 2.0 caller to send an undeclared parameter plus a BOOLEAN into a string parameter, which fails to
    /// bind or goes looking for a plugin named "true". Same rule the locate contract's <c>offerModParam</c>
    /// encodes on the refusal side, now applied to the success path: a response must never send someone to a
    /// parameter their tool does not expose.</summary>
    static string InPlaceAgainHint(string verb, string file, bool laneAsName) =>
        laneAsName
            ? $"{verb}, pass in_place=\"{file}\" again (no further confirmation needed for it)."
            : $"{verb}, pass target=\"{file}\" in_place=true (no further confirmation needed for it).";

    /// <summary>#225 — the dry_run=true confirmation: the SAME pipeline ran (winner resolve, pre-flight, every verb
    /// applied in memory, the reference-resolution check) and stopped AT the point of no return, so this reports what
    /// WOULD change with NOTHING on disk. The header says so first (Q3 — a dry run must never read like a write);
    /// masters are the labeled link-derived PREVIEW; per-op lines carry the would-be values; the optional deep dump is
    /// the in-memory would-be content. A refusal never reaches here — a dry run refuses EXACTLY like the real call.</summary>
    static string RenderDryRun(WritePatchBuilder.PatchOutcome o, int maxChars, bool fullDump)
    {
        var file = Path.GetFileName(o.OutputPath);
        var sb = new StringBuilder();
        sb.Append("DRY RUN — validated only; NOTHING was written (no patch file, no mod folder, originals untouched).\n");
        if (o.InPlace)
            sb.Append("the real call would edit ").Append(file).Append(" IN PLACE — your ORIGINAL file rewritten; no houseCARL backup or undo.\n");
        else if (o.Extended)
            sb.Append("the real call would EXTEND the existing patch ").Append(file).Append(".\n");
        else
            sb.Append("the real call would write a NEW patch ").Append(file)
              .Append(" (name preview — the real write re-picks a free name, so the auto-suffix can shift if another patch lands first).\n");
        sb.Append("expected masters: ").Append(o.Masters.Count == 0 ? "(none)" : string.Join(", ", o.Masters))
          .Append("  [derived from the would-be content; the real write derives its own lean header]\n");
        sb.Append(o.Ops.Count).Append(o.Ops.Count == 1 ? " edit would apply:\n" : " edits would apply:\n");
        foreach (var op in o.Ops)
            sb.Append("  ").Append(op.RecordType).Append(' ').Append(op.Target).Append("  ").Append(op.Label)
              .Append(op.After is not null ? "  -> would become " + op.After : "  -> would apply").Append('\n');
        if (fullDump && o.ReadBack is { } rb) AppendFullReadback(sb, rb, maxChars, dryRun: true);
        if (o.Note is { } note) sb.Append("note: ").Append(note).Append('\n');
        sb.Append("every op passed resolve + pre-flight; to apply for real, repeat the call without dry_run. ")
          .Append("(A real write can still fail at serialize/commit — disk faults and data Mutagen refuses to serialize surface only there.)")
          .Append(Epoch(o));
        return sb.ToString();
    }

    /// <summary>The full_readback=true read-back section (HCBR-2026-06-11-02 wave (b)): each touched/created record IN FULL,
    /// re-read from the written file on disk. Labeled as exactly that — the written file's content, NOT load-order
    /// truth (the patch wins nothing until enabled in MO2) — so the caller can't mistake it for a winner read.
    /// Char-budget-bounded with an explicit notice (Q3), same convention as the read tools — now at the LOWER
    /// <see cref="Wire.ReadbackMaxChars"/> default so the cut-off output stays under the host token ceiling and the
    /// truncation note actually reaches the caller (HCBR-2026-06-28-01).</summary>
    static void AppendFullReadback(StringBuilder sb, IReadOnlyList<WritePatchBuilder.FullReadback> rb, int maxChars,
        bool dryRun = false)
    {
        int cap = maxChars > 0 ? maxChars : Wire.ReadbackMaxChars;
        // #225: a dry run's records are read from the IN-MEMORY would-be content — say so, never imply a file exists.
        sb.Append(dryRun
            ? "full preview — the ENTIRE record(s) as they WOULD be written, read from the in-memory would-be content (nothing is on disk):\n"
            : "full read-back — the ENTIRE record(s) as written, re-read from the patch file on disk " +
              "(the written file's content, NOT load-order truth; the patch wins nothing until enabled + sorted in MO2):\n");
        string hint = dryRun ? "; raise max_chars" : "; raise max_chars, or enable the patch in MO2 and use housecarl_read_record";
        for (int i = 0; i < rb.Count; i++)
        {
            if (sb.Length >= cap)
            {
                sb.Append("  ... [truncated: full read-back rendered ").Append(i).Append(" of ").Append(rb.Count)
                  .Append(" record(s) at max_chars=").Append(cap).Append(hint).Append("]\n");
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
                      .Append(hint).Append("]\n");
                    return;
                }
                sb.Append("    ").Append(f.Path).Append(" = ").Append(f.HasValue ? f.Token : f.Note).Append('\n');
            }
        }
    }

    /// <summary>The DEFAULT (full_readback=false) render of the touched-record verify (HCBR-2026-06-28-01). The forced
    /// in-place re-read still RAN — corruption DETECTION is unchanged; this only reports it COMPACTLY so it can't overflow
    /// the host token cap the way the deep dump did (which spilled to a file and read as "only some ops applied"). One line
    /// per record: a re-read-CLEAN marker + field count, OR the NAMED re-read failure (Q3); then the "what landed" identity
    /// (the new scalar value, or the touched list element + new count) for each op that touched that record. Covers ALL N
    /// records — bounded by the same char cap with an explicit truncation note, never the silent spill the forced deep dump
    /// produced. The full field-by-field dump is one full_readback=true away.</summary>
    static void AppendCompactReadback(StringBuilder sb, IReadOnlyList<WritePatchBuilder.OpResult> ops,
        IReadOnlyList<WritePatchBuilder.FullReadback> rb, int maxChars)
    {
        int cap = maxChars > 0 ? maxChars : Wire.ReadbackMaxChars;
        sb.Append("verified — every edited record re-read off the written file (compact; pass full_readback=true for the ")
          .Append("full field-by-field dump):\n");
        for (int i = 0; i < rb.Count; i++)
        {
            if (sb.Length >= cap)
            {
                sb.Append("  ... [truncated: ").Append(i).Append(" of ").Append(rb.Count)
                  .Append(" record(s) shown at max_chars=").Append(cap).Append("; raise max_chars]\n");
                return;
            }
            var r = rb[i];
            // A re-read that failed is a real inconsistency — surface it LOUD and NAMED (the whole reason the in-place
            // verify is forced on), never folded into the clean count.
            if (r.Error is not null) { sb.Append("  ✗ ").Append(r.Target).Append(" — ").Append(r.Error).Append('\n'); continue; }
            var rec = r.Record!;
            sb.Append("  ✓ ").Append(rec.Type).Append(' ').Append(rec.FormKey)
              .Append(" — re-read clean (").Append(rec.Fields.Count).Append(" field(s))");
            // #308: the per-op clause is the FILE's answer when the file gave one (LandedOnDisk), and is marked as the
            // applied edit's claim when it did not — the banner above says "re-read off the written file", and this
            // line used to carry a memory-derived descriptor under it without saying so.
            var mine = ops.Where(op => op.Target == r.Target).ToList();
            var landed = mine.Where(op => (op.LandedOnDisk ?? op.Landed) is not null)
                             .Select(op => $"{op.Label}: {op.LandedOnDisk ?? op.Landed}"
                                         + (op.LandedOnDisk is null ? " [as applied — the re-opened file did not answer for this op]" : ""))
                             .ToList();
            if (landed.Count > 0) sb.Append("; ").Append(string.Join("; ", landed));
            sb.Append('\n');
            // #308 — LOUD, on its own line, and never folded into the ✓: the write succeeded and the file does NOT
            // corroborate what an op claims to have put there (canonically a composed struct with nothing to
            // serialize). This is the inconsistency the forced verify exists to catch, so it is stated, not implied by
            // a count the caller would have to notice.
            foreach (var op in mine)
                if (op.Divergence is { } why)
                    sb.Append("  ✗ ").Append(op.Label).Append(" — the edit was APPLIED but the written file does not carry it: ")
                      .Append(why).Append(". The file is what the game loads: treat this op as NOT landed.\n");
        }
    }

    /// <summary>Confirmation for housecarl_remove_record: what was dropped, the patch's now-lean masters, and how many
    /// records remain (0 ⇒ inert). On refusal, the named reason (Q3) so the caller can fix and retry.</summary>
    internal static string RenderRemoval(WritePatchBuilder.RemovalOutcome o, int maxChars = 0, bool laneAsName = false)   // internal: housecarl_remove renders the same outcome
    {
        if (o.NeedsAcknowledge) return o.Error! + Epoch(o);  // the first-touch in-place CONSENT prompt — a required confirmation, NOT an error (Q3)
        if (!o.Success) return "error: " + o.Error + Epoch(o);
        var file = Path.GetFileName(o.OutputPath);
        var modFolder = Path.GetFileName(Path.GetDirectoryName(o.OutputPath) ?? "");
        var sb = new StringBuilder();
        sb.Append("removed ").Append(o.Removed.Count).Append(o.Removed.Count == 1 ? " record from " : " records from ")
          .Append(file);
        if (o.InPlace)
            sb.Append(" IN PLACE (").Append(o.Bytes).Append(" bytes; ")
              .Append(o.RemainingRecords).Append(o.RemainingRecords == 1 ? " record remains" : " records remain")
              .Append(" — your ORIGINAL file was rewritten; no houseCARL backup or undo)\n")
              .Append("mod folder: ").Append(modFolder).Append("  — already active in your load order; re-sort only if a winner changed\n");
        else
        {
            sb.Append(" (").Append(o.Bytes).Append(" bytes; ")
              .Append(o.RemainingRecords).Append(o.RemainingRecords == 1 ? " record remains)\n" : " records remain)\n");
            sb.Append("mod folder: ").Append(modFolder).Append('\n');
        }
        // Budgeted like every other write render (PR #311 review [medium]): removal is SET-VALUED now, so the
        // unbounded case — a few hundred dropped overrides — is the expected one, not an edge, and max_chars=
        // promises "past it trailing rows are dropped with an explicit notice (never silent)". Without this the
        // rows all rendered and the HOST cut the oversized response out-of-band: exactly the silent cut the
        // parameter's own description says never happens. The masters line and the closing guidance below are
        // outside the budget deliberately — they are the accounting a truncated report still needs.
        int cap = maxChars > 0 ? maxChars : Wire.DefaultMaxChars;
        for (int i = 0; i < o.Removed.Count; i++)
        {
            if (sb.Length >= cap)
            {
                // NOT "raise max_chars to see the rest" (PR #311 review 6 [medium]). RenderCreate's comment cited
                // "a repeated remove is refused" as the reason that wording was SAFE here — which is also exactly
                // why it is a dead end: re-issuing the call answers "not carried by patch '<file>'; NOTHING
                // removed", because the records are already gone. Nothing is actually lost, though, and saying so
                // is the honest remedy: removal is all-or-nothing, so the rows ARE the formids= the caller passed.
                sb.Append("  ... [truncated: ").Append(i).Append(" of ").Append(o.Removed.Count)
                  .Append(" removed record(s) listed at max_chars=").Append(cap)
                  .Append("; every one WAS removed — ").Append(RemovedRowsRemedy).Append("]\n");
                break;
            }
            var r = o.Removed[i];
            sb.Append("  - ").Append(r.RecordType).Append(' ').Append(r.Target).Append("  ")
              .Append(r.EditorId ?? "<no editorid>").Append('\n');
        }
        sb.Append("masters: ").Append(o.Masters.Count == 0 ? "(none)" : string.Join(", ", o.Masters)).Append('\n');
        if (o.Note is { } note) sb.Append("note: ").Append(note).Append('\n');
        if (o.InPlace)
            sb.Append(o.RemainingRecords == 0
                ? "this plugin now carries no records — it's an inert shell; disable or delete the mod in MO2 if you don't need it."
                : InPlaceAgainHint("to remove more records from this plugin in place", file, laneAsName));
        else
            sb.Append(o.RemainingRecords == 0
                ? "this patch now carries no records — it's inert; disable or delete the mod folder in MO2 if you don't need it."
                : "re-sort in MO2 if dropping this override changes a conflict winner.");
        sb.Append(Epoch(o));
        return sb.ToString();
    }

    /// <summary>Confirmation for housecarl_forward_record: per record, WHAT was copied (type + FormID + editorid), the
    /// source it was copied FROM, and the current winner it out-ranks once enabled — with a redundant-forward NOTE when
    /// the copied version was already winning (Q3 — never silently a no-op). On refusal, the named reason so the caller
    /// can fix and retry. Optional full read-back rides along (the pre-enable verify that the copy is the source's).</summary>
    internal static string RenderForward(WritePatchBuilder.ForwardOutcome o, int maxChars = 0, bool laneAsName = false)   // internal: the dry-run guard asserts the would-be phrasing
    {
        if (o.NeedsAcknowledge) return o.Error! + Epoch(o);  // the first-touch in-place CONSENT prompt — a required confirmation, NOT an error (Q3)
        if (!o.Success) return "error: " + o.Error + Epoch(o);
        var file = Path.GetFileName(o.OutputPath);
        var modFolder = Path.GetFileName(Path.GetDirectoryName(o.OutputPath) ?? "");
        var sb = new StringBuilder();
        if (o.DryRun)
        {
            // #225 — mirrors RenderDryRun: say NOTHING was written first, then what the real call would do.
            sb.Append("DRY RUN — validated only; NOTHING was written (no patch file, no mod folder, originals untouched).\n");
            if (o.InPlace)
                sb.Append("the real call would forward into ").Append(file).Append(" IN PLACE — your ORIGINAL file rewritten; no houseCARL backup or undo.\n");
            else if (o.Extended)
                sb.Append("the real call would EXTEND the existing patch ").Append(file).Append(".\n");
            else
                sb.Append("the real call would write a NEW patch ").Append(file)
                  .Append(" (name preview — the real write re-picks a free name, so the auto-suffix can shift if another patch lands first).\n");
            sb.Append("expected masters: ").Append(o.Masters.Count == 0 ? "(none)" : string.Join(", ", o.Masters))
              .Append("  [derived from the would-be content; the real write derives its own lean header]\n");
        }
        else if (o.InPlace)
            sb.Append("forwarded into ").Append(file).Append(" IN PLACE (").Append(o.Bytes)
              .Append(" bytes — your ORIGINAL file was rewritten; no houseCARL backup or undo)\n")
              .Append("mod folder: ").Append(modFolder).Append("  — already active in your load order; re-sort only if a winner changed\n");
        else
        {
            sb.Append(o.Extended ? "extended " : "wrote ").Append(file)
              .Append(o.Extended ? " (existing patch grown; " : " (new patch; ").Append(o.Bytes).Append(" bytes)\n");
            sb.Append("mod folder: ").Append(modFolder)
              .Append(o.Extended ? "\n" : "  — enable + sort it in MO2 to use the patch\n");
        }
        if (!o.DryRun)
            sb.Append("masters: ").Append(o.Masters.Count == 0 ? "(none)" : string.Join(", ", o.Masters)).Append('\n');
        // WHICH copy an off-order source read — a fact, not derivable from the name (several install layers can provide
        // one filename, and only one of them was opened). Stated once for the call, because one source= serves it all.
        if (o.OffOrderSource is { } oo)
        {
            sb.Append("source: '").Append(oo.Plugin).Append("' is NOT in the active load order — the bodies were read OFF-ORDER from ")
              .Append(oo.Path).Append(" (").Append(oo.Where).Append("). The epoch below fingerprints the ACTIVE order, which that file is outside of.\n");
            if (oo.ExcludedReason is { } exWhy)
                sb.Append("  NOTE: that file is this session's copy of a plugin EXCLUDED from the index (").Append(exWhy)
                  .Append(") — addressing it by PATH reads it directly, which is why this resolved at all. Copying one record out is not the ")
                  .Append("whole-file re-serialize the exclusion refusal guards, but the body is only what Mutagen could parse: verify it (readback=true) before relying on it.\n");
        }
        sb.Append(o.DryRun ? "would forward " : "forwarded ").Append(o.Forwarded.Count)
          .Append(o.Forwarded.Count == 1 ? " record:\n" : " records:\n");
        // Budgeted for the same reason as the created-records block: formids= is set-valued, each row is long (type +
        // FormKey + editorid + the source clause + a REPLACED / redundant / out-ranks bracket), and the json twin
        // already truncates the identical array (PR #311 review 3 [medium]).
        int fwdCap = maxChars > 0 ? maxChars : Wire.DefaultMaxChars;
        for (int fi = 0; fi < o.Forwarded.Count; fi++)
        {
            if (sb.Length >= fwdCap)
            {
                sb.Append("  ... [truncated: ").Append(fi).Append(" of ").Append(o.Forwarded.Count)
                  .Append(" record(s) listed at max_chars=").Append(fwdCap)
                  .Append(o.DryRun ? "; the dry run covered every one — " : "; every one WAS forwarded — ")
                  .Append(ForwardAgainRemedy(o, file)).Append(']')
                  .Append('\n');
                break;
            }
            var f = o.Forwarded[fi];
            sb.Append("  ").Append(f.RecordType).Append(' ').Append(f.Target).Append("  ").Append(f.EditorId ?? "<no editorid>")
              .Append(o.DryRun ? "  — would be copied from " : "  — copied from ").Append(f.FromPlugin);
            if (f.ReplacedExisting)
                sb.Append(o.DryRun
                    ? "  [would REPLACE the patch's own existing override of this record — the old body would be gone (xEdit's copy-as-override-into overwrite)]"
                    : "  [REPLACED the patch's own existing override of this record — the old body is gone (xEdit's copy-as-override-into overwrite)]");
            if (f.WasAlreadyWinner)
                sb.Append("  [NOTE: this source IS already the load-order winner — the override just re-asserts the content that already wins (a no-op in effect)]");
            else if (f.PriorWinner is null)
                // No active plugin defines this record — ordinary on the self-origin path (a record originating in a
                // patch not enabled yet). The old sentinel rendered "out-ranks the current winner (none)", a ranking
                // against a winner that does not exist (PR #313 review 3 [low]).
                sb.Append("  (no active plugin currently defines this record — nothing to out-rank; it takes effect once this patch is enabled)");
            else
                sb.Append("  (out-ranks the current winner ").Append(f.PriorWinner).Append(" once this patch is enabled + sorted above it)");
            sb.Append('\n');
        }
        if (o.ReadBack is { } rb) AppendFullReadback(sb, rb, maxChars, dryRun: o.DryRun);
        if (o.Note is { } note) sb.Append("note: ").Append(note).Append('\n');
        sb.Append(o.DryRun
            ? "every record resolved from its source; to forward for real, repeat the call without dry_run. " +
              "(A real write can still fail at serialize/commit — disk faults surface only there.)"
            : o.InPlace
                ? InPlaceAgainHint("to forward more into this plugin", file, laneAsName)
                : $"to forward more into THIS patch (incl. from a different source plugin), pass into=\"{file}\".");
        sb.Append(Epoch(o));
        return sb.ToString();
    }

    /// <summary>Confirmation for housecarl_create_plugin: the empty plugin's path + mod folder, its ESL flag, master
    /// header (none), record count (0) and byte size, plus the MO2 enable reminder and what the trigger does. On
    /// refusal, the named reason (Q3) so the caller can fix and retry.</summary>
    static string RenderCreatePlugin(WritePatchBuilder.CreatePluginOutcome o)
    {
        if (!o.Success) return "error: " + o.Error;
        var file = Path.GetFileName(o.OutputPath);
        var modFolder = Path.GetFileName(Path.GetDirectoryName(o.OutputPath) ?? "");
        var sb = new StringBuilder();
        sb.Append("wrote ").Append(file).Append(o.Esl ? " (header-only, ESL-flagged; " : " (header-only; ")
          .Append(o.Bytes).Append(" bytes, ").Append(o.RecordCount).Append(o.RecordCount == 1 ? " record)\n" : " records)\n");
        sb.Append("mod folder: ").Append(modFolder).Append("  — enable + sort it in MO2 to use it\n");
        sb.Append("masters: ").Append(o.Masters.Count == 0 ? "(none)" : string.Join(", ", o.Masters)).Append('\n');
        sb.Append("this is a trigger/placeholder plugin: it carries no records, so it changes nothing in game by itself — ")
          .Append("its only job is to make the basename '").Append(Path.GetFileNameWithoutExtension(file))
          .Append("' present in the load order (so a basename-bound SKSE config resolves, a FormID range is reserved, etc.).");
        return sb.ToString();
    }

    /// <summary>Confirmation for housecarl_compact_plugin: where the compacted P′ landed (new file vs in place), the
    /// record accounting (originating renumbered / overrides kept), masters, the external-referencer verdict (clean,
    /// or the per-plugin repoint results), the identify-pass coverage, and the un-remappable-script reminder (Q3). The
    /// NeedsAcknowledge prompt (a required in-place consent) is returned verbatim, not as an error; on refusal the named
    /// reason so the caller can fix and retry.</summary>
    internal static string RenderCompact(WritePatchBuilder.CompactOutcome o)   // internal: the seq-regen-guard renders a failure outcome to prove the SEQ WARN reaches user output
    {
        if (o.NeedsAcknowledge) return o.Error!;            // the in-place CONSENT prompt — a required confirmation, NOT an error (Q3)
        if (!o.Success) return "error: " + o.Error;
        var file = Path.GetFileName(o.OutputPath);
        var modFolder = Path.GetFileName(Path.GetDirectoryName(o.OutputPath) ?? "");
        var sb = new StringBuilder();
        if (o.InPlace)
            sb.Append("compacted ").Append(file).Append(" IN PLACE (").Append(o.Bytes)
              .Append(" bytes — your ORIGINAL file was rewritten; no houseCARL backup or undo)\n")
              .Append("mod folder: ").Append(modFolder).Append("  — already active; re-sort only if a winner changed\n");
        else
            sb.Append("wrote compacted ").Append(file).Append(" (new plugin; ").Append(o.Bytes).Append(" bytes)\n")
              .Append("mod folder: ").Append(modFolder).Append("  — enable it and DISABLE the original '").Append(file)
              .Append("' mod in MO2 (same basename — MO2 serves one). Review in xEdit first.\n");

        int overrides = o.RecordsCopied - o.RecordsRenumbered;
        sb.Append(o.Esl ? "light master (ESPFE): yes — " : "renumbered (not light-flagged): ");
        sb.Append(o.RecordsRenumbered).Append(o.RecordsRenumbered == 1 ? " originating record renumbered " : " originating records renumbered ");
        sb.Append(o.Esl ? "into the light range 0x800–0xFFF" : "contiguously from 0x800");
        if (overrides > 0) sb.Append("; ").Append(overrides).Append(overrides == 1 ? " override kept at its master FormID" : " overrides kept at their master FormIDs");
        sb.Append(".\n");
        sb.Append("masters: ").Append(o.Masters.Count == 0 ? "(none)" : string.Join(", ", o.Masters)).Append('\n');

        if (o.ExternalPlugins.Count == 0)
            sb.Append("external referencers: none — clean compaction (nothing outside this plugin pointed at a renumbered record).\n");
        else if (o.Repointed.Count > 0)
        {
            int ok = o.Repointed.Count(r => r.Success);
            sb.Append("external referencers repointed in place: ").Append(ok).Append('/').Append(o.Repointed.Count).Append(" succeeded\n");
            foreach (var rep in o.Repointed)
                sb.Append("  ").Append(rep.Success ? "OK   " : "FAIL ").Append(rep.Plugin)
                  .Append(rep.Success ? "" : "  — " + rep.Error).Append('\n');
        }
        else
        {
            sb.Append("external referencers (").Append(o.ExternalPlugins.Count).Append(", NOT repointed):\n");
            foreach (var pl in o.ExternalPlugins.Take(25)) sb.Append("  - ").Append(pl).Append('\n');
            if (o.ExternalPlugins.Count > 25) sb.Append("  - … (+").Append(o.ExternalPlugins.Count - 25).Append(" more)\n");
        }

        // External OVERRIDERS (gap #2) — plugins that OVERRIDE a renumbered record (not just reference it). They orphan
        // after the renumber (the override points at a base FormID that no longer exists), and houseCARL CANNOT auto-repoint
        // an override — that's an identity change, not a link rewrite — so this is a WARN (xEdit parity), not the referencer
        // refuse/repoint path. Named per-plugin so the user can re-point or rebuild them (better than xEdit's blanket warning).
        if (o.ExternalOverriders is { Count: > 0 } overriders)
        {
            sb.Append("external OVERRIDERS (").Append(overriders.Count).Append("): these plugins OVERRIDE a renumbered record and will ")
              .Append("ORPHAN after the renumber — houseCARL can't auto-repoint an override (identity change, not a link). ")
              .Append("Re-point or rebuild them against the new FormIDs, or don't enable the compacted plugin over them:\n");
            foreach (var pl in overriders.Take(25)) sb.Append("  ! ").Append(pl).Append('\n');
            if (overriders.Count > 25) sb.Append("  ! … (+").Append(overriders.Count - 25).Append(" more)\n");
        }

        if (o.UnscannableRecords > 0)
        {
            sb.Append("note: ").Append(o.UnscannableRecords).Append(" record(s) couldn't be scanned in the external-reference pass, so an ")
              .Append("'external referencers: none' may be incomplete — verify in xEdit. Samples: ").Append(string.Join("; ", o.UnscannableSamples)).Append('\n');
        }
        sb.Append("identify-pass scanned ").Append(o.PluginsScanned).Append(" plugin(s) for external references.\n");

        AppendFacegenCarry(sb, o.AssetRename, o.InPlace);
        AppendVoiceCarry(sb, o.VoiceRename, o.InPlace);
        AppendSeqRegen(sb, o.SeqRegen, o.InPlace);

        if (o.Note is { } note) sb.Append("note: ").Append(note).Append('\n');
        sb.Append("reminder: FormIDs compiled into Papyrus (.pex hardcoded / GetFormFromFile) and any Mutagen-delta ")
          .Append("residual are NOT remappable — verify scripted records after compacting.");
        return sb.ToString();
    }

    // FormID-keyed assets carried WITH the renumber (Waves A1–A3, shared by compact AND merge — one render home, no
    // drift). The renumber moved records to new FormIDs (a merge additionally to a new plugin NAME), so the engine looks
    // facegen/voice up under NEW paths and a shipped .seq goes stale; carrying/refreshing them is what stops a renumbered
    // NPC mod silently dark-facing, a voiced mod going mute, and SGE quests never starting. Reported, not silent (Q3).
    // inPlace is always false for merge (it has no in-place lane).

    static void AppendFacegenCarry(StringBuilder sb, AssetRenameOutcome? outcome, bool inPlace)
    {
        if (outcome is not { } ar) return;
        if (ar.FacegenFilesCarried > 0)
            sb.Append("facegen: carried ").Append(ar.FacegenFilesCarried).Append(ar.FacegenFilesCarried == 1 ? " file for " : " files for ")
              .Append(ar.FacegenNpcsCarried).Append(ar.FacegenNpcsCarried == 1 ? " NPC to the new FormIDs" : " NPCs to the new FormIDs")
              .Append(inPlace ? " (old-FormID facegen left as harmless orphans).\n" : " (in the new mod folder — enabling it carries the faces).\n");
        else if (ar.NpcCount > 0 && ar.Failures.Count == 0)
            sb.Append("facegen: none found for ").Append(ar.NpcCount).Append(ar.NpcCount == 1 ? " NPC — nothing to carry.\n" : " NPCs — nothing to carry.\n");
        foreach (var f in ar.Failures.Take(25)) sb.Append("  facegen WARN: ").Append(f).Append('\n');
        if (ar.Failures.Count > 25) sb.Append("  facegen WARN: … (+").Append(ar.Failures.Count - 25).Append(" more)\n");
        if (ar.ReadIncomplete)
            sb.Append("  note: a BSA failed to read this scan, so a 'no facegen' result may be incomplete — verify NPC faces in-game.\n");
    }

    static void AppendVoiceCarry(StringBuilder sb, VoiceCarryOutcome? outcome, bool inPlace)
    {
        if (outcome is not { } vr) return;
        if (vr.FilesCarried > 0)
            sb.Append("voice: carried ").Append(vr.FilesCarried).Append(vr.FilesCarried == 1 ? " file for " : " files for ")
              .Append(vr.LinesCarried).Append(vr.LinesCarried == 1 ? " dialogue line to the new FormIDs" : " dialogue lines to the new FormIDs")
              .Append(inPlace ? " (old-FormID voice left as harmless orphans).\n" : " (in the new mod folder — enabling it carries the voice).\n");
        else if (vr.FilesScanned > 0 && vr.Failures.Count == 0)
            sb.Append("voice: ").Append(vr.FilesScanned).Append(vr.FilesScanned == 1 ? " voice file found, none keyed to a renumbered line" : " voice files found, none keyed to a renumbered line")
              .Append(" — nothing to carry.\n");
        foreach (var f in vr.Failures.Take(25)) sb.Append("  voice WARN: ").Append(f).Append('\n');
        if (vr.Failures.Count > 25) sb.Append("  voice WARN: … (+").Append(vr.Failures.Count - 25).Append(" more)\n");
        if (vr.ReadIncomplete)
            sb.Append("  note: a BSA failed to read this scan, so a 'no voice' result may be incomplete — verify voiced lines in-game.\n");
    }

    static void AppendSeqRegen(StringBuilder sb, SeqRegenOutcome? outcome, bool inPlace)
    {
        if (outcome is not { } sr) return;
        if (sr.Written)
            sb.Append("SEQ: regenerated — ").Append(sr.SgeQuestCount).Append(sr.SgeQuestCount == 1 ? " start-game-enabled quest" : " start-game-enabled quests")
              .Append(inPlace ? " (.seq rewritten in place).\n" : " (.seq in the new mod folder's SEQ\\ — enabling it starts the quests).\n");
        foreach (var f in sr.Failures.Take(25)) sb.Append("  SEQ WARN: ").Append(f).Append('\n');
        if (sr.Failures.Count > 25) sb.Append("  SEQ WARN: … (+").Append(sr.Failures.Count - 25).Append(" more)\n");
    }

    /// <summary>Merge confirmation (A4): the merged plugin's identity + the MO2 swap instruction, per-donor id
    /// accounting, cross-donor conflict resolutions (load-order winner — reported, never silent), the WARN surfaces
    /// (external referencers/overriders with the remedy), the asset-carry accounting, and the saves/ESL pointers.
    /// On refusal, the named reason (Q3). internal: the merge guard asserts warnings reach user output.</summary>
    internal static string RenderMerge(WritePatchBuilder.MergeOutcome o)
    {
        if (!o.Success) return "error: " + o.Error;
        var file = Path.GetFileName(o.OutputPath);
        var modFolder = Path.GetFileName(Path.GetDirectoryName(o.OutputPath) ?? "");
        var sb = new StringBuilder();
        sb.Append("wrote merged ").Append(file).Append(" (new plugin; ").Append(o.Bytes).Append(" bytes) from ")
          .Append(o.Donors.Count).Append(" donors: ").Append(string.Join(", ", o.Donors)).Append('\n');
        sb.Append("mod folder: ").Append(modFolder).Append("  — review in xEdit, then enable + sort it in MO2.\n");
        // The swap is PLUGIN-level, not mod-level (merge is a RECORDS op): the merged records still reference the donors'
        // meshes/textures/scripts/BSA contents BY PATH, and those files live in the donor mod folders — only the
        // FormID-keyed facegen/voice/seq were carried. "Disable the donor mods" (compact's instruction, where the output
        // shares the source's basename) would yank all of that out of the VFS with every warning light green.
        sb.Append("the swap: deactivate the donor PLUGINS (right pane) — their files are untouched — but KEEP the donor mod ")
          .Append("folders enabled (left pane): the merged records still load the donors' meshes/textures/scripts by path; ")
          .Append("only facegen/voice/seq were carried. If a donor ships a .bsa, it stops auto-loading once its plugin is ")
          .Append("deactivated — extract it into the mod folder (housecarl_bsa_extract) or load it via a same-named dummy ")
          .Append("plugin (housecarl_create_plugin).\n");

        int overrides = o.RecordsCopied - o.RecordsRenumbered;
        sb.Append(o.RecordsRenumbered).Append(o.RecordsRenumbered == 1 ? " originating record" : " originating records")
          .Append(" merged under ").Append(o.OutputName);
        if (overrides > 0) sb.Append("; ").Append(overrides).Append(overrides == 1 ? " override kept at its master FormID" : " overrides kept at their master FormIDs");
        sb.Append(".\n");
        foreach (var d in o.DonorRemaps)
            sb.Append("  ").Append(d.Donor).Append(": ").Append(d.Kept).Append(" object id(s) kept, ")
              .Append(d.Renumbered).Append(" renumbered (id collisions / below-floor)\n");
        sb.Append("masters: ").Append(o.Masters.Count == 0 ? "(none)" : string.Join(", ", o.Masters)).Append('\n');

        if (o.Conflicts.Count == 0)
            sb.Append("cross-donor conflicts: none — no record was carried by more than one donor.\n");
        else
        {
            sb.Append("cross-donor conflicts (").Append(o.Conflicts.Count).Append(") — each resolved to the LOAD-ORDER WINNER (the losing version is NOT in the merge; any un-relisted nested children were grafted):\n");
            foreach (var c in o.Conflicts.Take(25))
                sb.Append("  ").Append(c.RecordType).Append(' ').Append(c.Key).Append("  ").Append(c.WinnerDonor).Append(" won over ").Append(c.LoserDonor).Append('\n');
            if (o.Conflicts.Count > 25) sb.Append("  … (+").Append(o.Conflicts.Count - 25).Append(" more)\n");
        }

        // The A4 posture: WARN loud + proceed — the donors stay installed and ACTIVE until the user swaps in MO2, so
        // nothing is broken at write time; the report names every affected plugin and the remedy. (Unlike compact, which
        // refuses on referencers: a compact's renumber takes effect under the SAME plugin name, a merge's only when the
        // user disables the donors — the user holds the switch here.)
        if (o.ExternalPlugins.Count > 0)
        {
            sb.Append("WARNING — ").Append(o.ExternalPlugins.Count).Append(" plugin(s) OUTSIDE the merge REFERENCE donor records. Their references break ")
              .Append("the moment you deactivate the donor plugins: include them in the merge set (re-run with them added), or re-point them at '")
              .Append(o.OutputName).Append("' before the swap:\n");
            foreach (var pl in o.ExternalPlugins.Take(25)) sb.Append("  ! ").Append(pl).Append('\n');
            if (o.ExternalPlugins.Count > 25) sb.Append("  ! … (+").Append(o.ExternalPlugins.Count - 25).Append(" more)\n");
        }
        else sb.Append("external referencers: none — nothing outside the merge points at a donor record.\n");
        if (o.ExternalOverriders.Count > 0)
        {
            sb.Append("WARNING — ").Append(o.ExternalOverriders.Count).Append(" plugin(s) OUTSIDE the merge OVERRIDE a donor record; those overrides ")
              .Append("orphan once you deactivate the donor plugins (an override can't be auto-repointed — identity, not a link). Include them in the merge set, or rebuild them against '")
              .Append(o.OutputName).Append("':\n");
            foreach (var pl in o.ExternalOverriders.Take(25)) sb.Append("  ! ").Append(pl).Append('\n');
            if (o.ExternalOverriders.Count > 25) sb.Append("  ! … (+").Append(o.ExternalOverriders.Count - 25).Append(" more)\n");
        }
        if (o.UnscannableRecords > 0)
            sb.Append("note: ").Append(o.UnscannableRecords).Append(" record(s) couldn't be scanned in the external-reference pass, so a ")
              .Append("'none' above may be incomplete — verify in xEdit. Samples: ").Append(string.Join("; ", o.UnscannableSamples)).Append('\n');
        sb.Append("identify-pass scanned ").Append(o.PluginsScanned).Append(" plugin(s) for external references.\n");

        AppendFacegenCarry(sb, o.AssetRename, inPlace: false);
        AppendVoiceCarry(sb, o.VoiceRename, inPlace: false);
        AppendSeqRegen(sb, o.SeqRegen, inPlace: false);

        if (o.Note is { } note) sb.Append("note: ").Append(note).Append('\n');
        sb.Append("reminders: existing SAVES that depend on the donors will not survive the swap (records moved to a new ")
          .Append("plugin name + new FormIDs) — best for a new game. FormIDs compiled into Papyrus (.pex hardcoded / ")
          .Append("GetFormFromFile) and any Mutagen-delta residual are NOT remappable — verify scripted records. Want it ")
          .Append("light? Run housecarl_compact_plugin on '").Append(o.OutputName).Append("' (the tools compose).");
        return sb.ToString();
    }

    /// <summary>Confirmation for housecarl_create_record: the new record's ALLOCATED FormID + editorid + type (the FormID
    /// is the key output — the caller references the new record by it), the patch path + its (derived) masters, and the
    /// fields applied. On refusal, the named reason (Q3) so the caller can fix and retry.</summary>
    internal static string RenderCreate(WritePatchBuilder.CreateOutcome o, int maxChars = 0, bool fullDump = false, bool laneAsName = false)   // internal: housecarl_create renders the same outcome
    {
        if (o.NeedsAcknowledge) return o.Error! + Epoch(o);  // the first-touch in-place CONSENT prompt — a required confirmation, NOT an error (Q3)
        if (!o.Success) return "error: " + o.Error + Epoch(o);
        var file = Path.GetFileName(o.OutputPath);
        var modFolder = Path.GetFileName(Path.GetDirectoryName(o.OutputPath) ?? "");
        var sb = new StringBuilder();
        if (o.InPlace)
            sb.Append(file).Append(" rewritten IN PLACE (").Append(o.Bytes)
              .Append(" bytes — your ORIGINAL file; no houseCARL backup or undo)\n")
              .Append("mod folder: ").Append(modFolder).Append("  — already active in your load order; re-sort only if a winner changed\n");
        else
        {
            sb.Append(o.Extended ? "extended " : "wrote ").Append(file)
              .Append(o.Extended ? " (existing patch grown; " : " (new patch; ").Append(o.Bytes).Append(" bytes)\n");
            sb.Append("mod folder: ").Append(modFolder)
              .Append(o.Extended ? "\n" : "  — enable + sort it in MO2 to use the patch\n");
        }
        sb.Append("masters: ").Append(o.Masters.Count == 0 ? "(none)" : string.Join(", ", o.Masters)).Append('\n');
        var replacedCount = o.Created.Count(c => c.ReplacedExisting);
        sb.Append("created ").Append(o.Created.Count).Append(o.Created.Count == 1 ? " record" : " records");
        if (replacedCount > 0)
            sb.Append(" (").Append(replacedCount).Append(replacedCount == 1 ? " REPLACED an existing record" : " REPLACED existing records")
              .Append(" — same FormID kept, prior contents discarded)");
        sb.Append(":\n");
        // Budgeted (PR #311 review 3 [medium]): this is the render's LARGEST block and it is SET-VALUED — a 500-record
        // authoring job from records="@<manifest>" is the case this tool exists for, not an edge. The json twin
        // already budgets the same array and closes truncated:true, so leaving this one unbounded made text and json
        // disagree about the same call, with the text lane taking a silent host-side cut. (The round-1 fold filed this
        // as a follow-up on the grounds that max_chars' description scoped the ceiling to the read-back; the D2
        // divergence is the stronger argument and it wins.)
        int createCap = maxChars > 0 ? maxChars : Wire.DefaultMaxChars;
        int listed = 0;
        for (int ci = 0; ci < o.Created.Count; ci++)
        {
            if (sb.Length >= createCap)
            {
                // The remedy points at a READ, never at re-issuing this call (PR #311 review 3 round-2 [medium]).
                // The sibling renders' "raise max_chars to see the rest" is safe on THEIR lanes — a repeated remove
                // is refused, a repeated forward re-copies identical bodies — but a repeated CREATE allocates the
                // records AGAIN: on the default lane patch= auto-suffixes into a second full patch, and under into=
                // each record is re-created at its old FormID with its prior contents discarded. That is the
                // duplicate-write trap ReadBackInFull's own doc names, and an agent following the notice literally
                // walks into it.
                sb.Append("  ... [truncated: ").Append(ci).Append(" of ").Append(o.Created.Count)
                  .Append(" created record(s) listed at max_chars=").Append(createCap)
                  .Append("; every one WAS created — read them back with ").Append(ReadBackCall(o, file))
                  .Append(" (re-calling this tool would create them AGAIN)]").Append('\n');
                break;
            }
            var c = o.Created[ci];
            listed++;
            sb.Append("  ").Append(c.RecordType).Append(' ').Append(c.FormKey).Append("  ").Append(c.EditorId);
            if (c.ReplacedExisting) sb.Append("  [REPLACED: this patch already defined this editorid — re-created fresh at the same FormID; prior contents, including any set_field edits since, were discarded]");
            sb.Append('\n');
            foreach (var op in c.Ops)
                sb.Append("      ").Append(op.Label).Append(op.After is not null ? "  -> " + op.After : "  -> applied").Append('\n');
        }
        AppendVoiceReport(sb, o.Voice, maxChars);
        AppendScriptBindingReport(sb, o.ScriptBinding, maxChars);
        AppendCellShellReport(sb, o.CellShell, maxChars);
        // Same compact-by-default verify as the edit lane (HCBR-2026-06-28-01): the forced create-in-place re-read still
        // runs; full_readback=true gives the deep dump, the default reports it compactly (per created record: re-read clean
        // + field count, or a named failure). The created records' set fields are already listed above.
        if (o.ReadBack is { } rb)
        {
            if (fullDump) AppendFullReadback(sb, rb, maxChars);
            else AppendCompactReadback(sb, Array.Empty<WritePatchBuilder.OpResult>(), rb, maxChars);
        }
        if (o.Note is { } note) sb.Append("note: ").Append(note).Append('\n');
        // Gated on rows having actually rendered (same review finding): with a small max_chars the header alone can
        // exceed the cap and drop EVERY row, and "the new FormID above" then asserts a referent this render never
        // printed. Say where to get them instead.
        sb.Append(listed > 0
            ? "the new FormID above is how you reference this record (SkyPatcher/SPID, or a follow-up edit). "
            : $"no records are listed above — the char budget cut the whole list, though all {o.Created.Count} WERE created. Read them back with {ReadBackCall(o, file)} to get their FormIDs. ");
        sb.Append(o.InPlace
            ? InPlaceAgainHint("To create more records in this plugin", file, laneAsName)
            : $"To add more to THIS patch, pass into=\"{file}\".");
        sb.Append(Epoch(o));
        return sb.ToString();
    }

    /// <summary>What a truncated REMOVE render tells the caller (PR #311 review 6 [medium]). The one lane where the
    /// dropped rows carry no information the caller lacks: removal is ALL-OR-NOTHING over the <c>formids=</c> they
    /// passed, so the listed set and the passed set are the same set. Re-issuing to widen the render is not merely
    /// wasteful here — it is REFUSED, because the records no longer exist to be found, so the old
    /// "raise max_chars to see the rest" pointed at the one call guaranteed to fail.</summary>
    internal const string RemovedRowsRemedy =                       // internal: the json twin says the same thing (D2)
        "removal is all-or-nothing, so these rows are exactly the formids= you passed — nothing here is unrecoverable. "
      + "Do NOT re-issue to widen this: the records are gone, so a repeat is refused as 'not carried by' the file";

    /// <summary>What a truncated FORWARD render tells the caller to do — and it depends on the LANE (PR #311
    /// review 5 [low]). "raise max_chars and re-issue" was justified here on the grounds that a repeated forward
    /// re-copies identical bodies; that holds on <c>in_place=</c> and on <c>into=</c> (replace-on-collision, so the
    /// second call lands on the same FormKeys), and on a dry run, which writes nothing at all. It does NOT hold on
    /// the DEFAULT lane — the one a caller reaches by naming no lane — where <c>ResolveOutputPath</c>'s
    /// <c>UniqueStem</c> allocates a fresh stem, so the re-issue is a SECOND full patch mod carrying the same
    /// overrides. Quieter than create's duplicate (no new FormIDs), not absent. The remedy names the lane that
    /// makes the re-issue safe rather than leaving the caller to discover the second folder.
    /// <para>IN_PLACE is its own case (PR #311 review 6 [low]) and the first pass got it wrong by lumping it with
    /// <c>into=</c>. A re-issue there is content-idempotent, but it re-serializes the caller's OWN plugin end to
    /// end — the file this same render just said has "no houseCARL backup or undo" — purely to widen a display.
    /// It is the only lane of the four where the re-run touches something houseCARL does not own, so it gets the
    /// read-back remedy instead: the target is active by definition of the lane, so <c>housecarl_records</c>
    /// reaches it, and the FormIDs to name are the ones the caller passed.</para></summary>
    internal static string ForwardAgainRemedy(WritePatchBuilder.ForwardOutcome o, string file)   // internal: the json twin says the same thing (D2)
        => WriteAgainRemedy(o.DryRun, o.InPlace, o.Extended, file, "patch mod carrying the same overrides");

    /// <summary>The lane rule generalized (PR #311 review 6): every WRITE render's row budget faces the same
    /// question — is re-issuing this call to widen a display safe? — and the answer is a property of the LANE, not
    /// of the verb. A dry run wrote nothing; <c>into=</c> lands on the same artifact; <c>in_place=</c> re-serializes
    /// the caller's own file; the DEFAULT lane auto-suffixes a second patch. `apply` shares this with `forward`
    /// because it shares the lane axis — the alternative was fixing three of four write tools and shipping the
    /// fourth on wording the reviewer has now flagged in four consecutive rounds.</summary>
    static string WriteAgainRemedy(bool dryRun, bool inPlace, bool extended, string file, string duplicateNoun)
        => dryRun || extended
            ? "raise max_chars to see the rest"
            : inPlace
                ? $"to see the rest, read the rows back with housecarl_records source=\"{file}\" formids=[the ids you passed] — re-issuing would re-serialize your ORIGINAL file a second time just to widen this render"
                : $"to see the rest, raise max_chars AND pass into=\"{file}\" — a bare re-issue on the default patch= lane writes a SECOND {duplicateNoun}";

    /// <summary>The <c>apply</c> lane's wording of <see cref="WriteAgainRemedy"/> (PR #311 review 6, declared as an
    /// unrequested sibling): apply's json render budgets its <c>ops</c> array, and its notice carried the same
    /// "raise max_chars" the forward/remove/create/write_seq notices were all moved off.</summary>
    internal static string ApplyAgainRemedy(WritePatchBuilder.PatchOutcome o, string file)
        => WriteAgainRemedy(o.DryRun, o.InPlace, o.Extended, file, "patch mod carrying the same edits");

    /// <summary>The read-back call a truncated create render points the caller at — a call that actually RESOLVES,
    /// which is the whole point of pointing away from re-issuing the create (PR #311 review 4 [medium]).
    /// <para>Two ways the shorter spellings fail. <c>source=</c> is records' SOURCE pole (WHOSE version), not a SELECT
    /// term, so a source-only call dies on "select something — formids= …, or a scan scope". And a <c>plugins=</c>
    /// scope names ACTIVE plugins, so it cannot select the headline case at all: a patch this very call just wrote is
    /// not enabled in MO2 yet (the render's own next line says to enable it), and over an off-order file the scope is
    /// refused by name. <c>types=</c> is the SELECT term that carries on BOTH arms — active, where the named pole's
    /// records are the scan universe; off-order, where the pole is enumerated from the file directly — and the created
    /// records' own <c>RecordType</c>s are catalog names, exactly what <c>types=</c> resolves.</para></summary>
    internal static string ReadBackCall(WritePatchBuilder.CreateOutcome o, string file)   // internal: the json twin points at the SAME call (D2)
    {
        var types = o.Created.Select(c => c.RecordType).Where(t => !string.IsNullOrWhiteSpace(t))
                             .Distinct(StringComparer.OrdinalIgnoreCase)
                             .OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();
        // Every distinct type, never a sampled head: a partial types= would select a partial answer while reading like
        // the whole one. (Unreachable-empty — a successful create has at least one created record — but a bare
        // source= call is the refused shape, so the clause is not silently dropped either.)
        return types.Count > 0
            ? $"housecarl_records source=\"{file}\" types=[{string.Join(", ", types.Select(t => $"\"{t}\""))}]"
            : $"housecarl_records source=\"{file}\" types=[<the record types you created>]";
    }

    /// <summary>Render the Layer B unit B voice-coverage report (a dialogue-line create). The enforced Q3 teeth against a
    /// byte-valid-but-SILENT line: a LOUD "WILL BE SILENT" per created voiced response with no .fuz on disk (naming the
    /// path to put the audio at), a brief "voice present" for ones already covered, and a NAMED reason per line whose
    /// path couldn't even be computed (no Speaker, unresolvable voice type, …). Voice ACTING stays out of scope — this
    /// reports the on-disk DATA-layer boundary, never generates audio. No-op unless the call created dialogue lines.</summary>
    static void AppendVoiceReport(StringBuilder sb, VoiceReport? report, int maxChars)
    {
        if (report is null || report.IsEmpty) return;
        // Budget-bounded like the full read-back (same maxChars contract): a bulk_create authoring hundreds of voiced
        // lines must NOT silently blow the response size or starve a requested read-back — past the cap the voice
        // section stops with an explicit notice (Q3), never a silent cut.
        int cap = maxChars > 0 ? maxChars : Wire.DefaultMaxChars;
        int total = report.Lines.Count + report.Undetermined.Count, rendered = 0;
        sb.Append("voice coverage — created dialogue lines (a response with no .fuz plays SILENT in game; the audio is yours to provide):\n");

        bool anyReadIncomplete = false;
        foreach (var l in report.Lines)
        {
            if (sb.Length >= cap) { AppendVoiceTrunc(sb, rendered, total, cap); return; }
            var who = string.IsNullOrEmpty(l.TopicEditorId) ? l.Info.ToString() : $"{l.TopicEditorId} ({l.Info})";
            if (l.FuzPresent)
            {
                sb.Append("  OK   ").Append(who).Append(" resp ").Append(l.ResponseNumber)
                  .Append("  — voice present (").Append(l.FuzWinner ?? "?").Append(')');
                if (l.FuzAmbiguous) sb.Append(" [more than one source provides it — contended]");
                if (!l.LipPresent) sb.Append("; no .lip (no lip-sync)");
                sb.Append('\n');
            }
            else
            {
                sb.Append("  [!] WILL BE SILENT  ").Append(who).Append(" resp ").Append(l.ResponseNumber)
                  .Append("  — no .fuz at ").Append(l.FuzPath).Append("  (place the audio here)");
                if (!l.LipPresent) sb.Append("; .lip also absent (").Append(l.LipPath).Append(')');
                sb.Append('\n');
            }
            if (l.ReadIncomplete) anyReadIncomplete = true;
            rendered++;
        }
        foreach (var u in report.Undetermined)
        {
            if (sb.Length >= cap) { AppendVoiceTrunc(sb, rendered, total, cap); return; }
            var who = string.IsNullOrEmpty(u.TopicEditorId) ? u.Info.ToString() : $"{u.TopicEditorId} ({u.Info})";
            sb.Append("  [?] ").Append(who).Append("  — ").Append(u.Reason).Append('\n');
            rendered++;
        }
        if (anyReadIncomplete)
            sb.Append("  note: a BSA failed to read this scan, so an \"absent\" above may merely be unscanned — verify in MO2.\n");
        if (report.CheckError is not null)
            sb.Append("  voice check could not run: ").Append(report.CheckError).Append(" — the records WERE created; verify voice files manually.\n");
    }

    /// <summary>The explicit voice-coverage truncation notice (Q3 — the same convention as the read-back's): how many
    /// of the total voice entries were rendered before the char budget was hit, and what that does and does not mean.
    /// <para>Shares its closing clause with the result-script notice and with the json <c>WriteBlockCensus</c>
    /// (PR #311 review 7 [medium]): these blocks ride the CREATE render, so "raise max_chars to see the rest" meant
    /// re-issuing a create — a second auto-suffixed patch on the default lane, or re-creation at the same FormID
    /// with prior contents discarded under <c>into=</c>. The json twin added this round already said "Do NOT
    /// re-issue the write to widen this", so the two transports were giving opposite advice about one call.</para></summary>
    static void AppendVoiceTrunc(StringBuilder sb, int rendered, int total, int cap)
        => sb.Append("  ... [voice coverage truncated: rendered ").Append(rendered).Append(" of ").Append(total)
             .Append(" line(s) at max_chars=").Append(cap).Append(ReportBlockCutClause).Append("]\n");

    /// <summary>The closing clause every post-write REPORT truncation notice shares, text and json alike. Names the
    /// stakes (a cut list is not a clean bill of health) and refuses to prescribe the one action that would widen it
    /// at the cost of a duplicate write.</summary>
    internal const string ReportBlockCutClause =
        "; the records WERE created and this block is only a render of them — compare rendered vs total above rather than "
      + "reading the list as the whole answer. Do NOT re-issue the create to widen it: that allocates the records again";

    /// <summary>Render the coordinate-keyed §4-(b) structural-shell report (a cell create). The enforced Q3 teeth against
    /// a created-but-EMPTY cell: a created cell is a valid, correctly-placed RECORD, but houseCARL does NOT author world
    /// content — so per created cell this lists, by kind, what the author must still provide in the Creation Kit
    /// (lighting / terrain / water / navmesh). "Created" must never read as "looks right in game". No-op unless the call
    /// created cells.</summary>
    static void AppendCellShellReport(StringBuilder sb, CellShellReport? report, int maxChars)
    {
        if (report is null || report.IsEmpty) return;
        // Budgeted like its two siblings (PR #311 review 7 [low-medium], Aaron-go). This was the last unbudgeted
        // block on the write renders: the json twin already stopped at `cap` and closed with a census, so one
        // create authoring a batch of cells rendered every row in TEXT and took the SILENT host-side cut — the
        // divergence the created-rows / forwarded-rows / removed-rows folds each closed in turn. The inner
        // MustProvide loop is bounded too: one cell with a long work list could blow the budget by itself.
        int cap = maxChars > 0 ? maxChars : Wire.DefaultMaxChars;
        int total = report.Cells.Count, rendered = 0;
        bool cut = false;
        sb.Append("cell shell — created cells are valid, correctly-placed records but EMPTY; houseCARL does not author world content (provide these in the Creation Kit):\n");
        foreach (var c in report.Cells)
        {
            if (sb.Length >= cap) { cut = true; break; }
            sb.Append("  ").Append(c.Interior ? "INTERIOR " : "EXTERIOR ").Append(c.EditorId).Append(" (").Append(c.Cell).Append("):\n");
            foreach (var m in c.MustProvide)
            {
                if (sb.Length >= cap) { cut = true; break; }
                sb.Append("      - ").Append(m).Append('\n');
            }
            if (cut) break;
            rendered++;
        }
        // The notice, then the two Q3 notes below it — which stay OUTSIDE the budget on purpose, the same rule the
        // removal render states: they are the accounting a truncated report still needs, and the grid-occupancy
        // seam in particular must not be the thing a cut swallows.
        if (cut)
            sb.Append("  ... [cell shell truncated: rendered ").Append(rendered).Append(" of ").Append(total)
              .Append(" cell(s) at max_chars=").Append(cap).Append(ReportBlockCutClause).Append("]\n");
        // Q3 — declare the un-checked grid-occupancy seam (full load-order occupancy detection is a follow-up; never a
        // silent omission). Only an EXTERIOR cell collides on a grid; an interior cell has no grid identity.
        if (report.Cells.Any(c => !c.Interior))
            sb.Append("  note: houseCARL does NOT check grid-occupancy — a NEW exterior cell at a grid your load order already fills collides (engine behavior undefined). To change an existing cell, OVERRIDE it instead of creating a new one.\n");
        if (report.CheckError is not null)
            sb.Append("  cell-shell check could not run: ").Append(report.CheckError).Append(" — the cell(s) WERE created; review world content manually.\n");
    }

    /// <summary>Render the Layer B unit C result-script coverage report (a dialogue-line create). The enforced Q3 teeth
    /// against a byte-valid-but-INERT result script: a LOUD "WILL NOT FIRE" per created line whose VMAD binds nothing
    /// usable (incomplete) or names a script with no compiled `.pex` on disk (naming the missing path), a brief "OK" for
    /// ones fully wired + compiled, and a NAMED reason for any created INFO that couldn't be located. Script compilation
    /// itself is housecarl_compile_script's job — this reports the on-disk DATA-layer boundary. No-op unless the call
    /// created scripted dialogue lines. Budget-bounded like the voice + read-back sections (same max_chars contract).</summary>
    static void AppendScriptBindingReport(StringBuilder sb, ScriptBindingReport? report, int maxChars)
    {
        if (report is null || report.IsEmpty) return;
        int cap = maxChars > 0 ? maxChars : Wire.DefaultMaxChars;
        int total = report.Findings.Count, rendered = 0;
        sb.Append("result-script coverage — created dialogue lines (a bound script that's unwired or uncompiled runs NOTHING in game):\n");

        bool anyReadIncomplete = false;
        foreach (var f in report.Findings)
        {
            if (sb.Length >= cap)
            {
                sb.Append("  ... [result-script coverage truncated: rendered ").Append(rendered).Append(" of ").Append(total)
                  .Append(" line(s) at max_chars=").Append(cap).Append(ReportBlockCutClause).Append("]\n");
                return;
            }
            var who = string.IsNullOrEmpty(f.TopicEditorId) ? f.Info.ToString() : $"{f.TopicEditorId} ({f.Info})";
            switch (f.Status)
            {
                case ScriptBindingStatus.BoundAndCompiled:
                    sb.Append("  OK   ").Append(who).Append("  — ").Append(f.Detail).Append('\n');
                    break;
                case ScriptBindingStatus.ScriptNotCompiled:
                    sb.Append("  [!] WILL NOT FIRE  ").Append(who).Append("  — ").Append(f.Detail);
                    if (f.MissingPex.Count > 0) sb.Append("  (missing: ").Append(string.Join(", ", f.MissingPex)).Append(')');
                    sb.Append('\n');
                    break;
                case ScriptBindingStatus.BindingIncomplete:
                    sb.Append("  [!] WILL NOT FIRE  ").Append(who).Append("  — ").Append(f.Detail).Append('\n');
                    break;
                default: // Undetermined
                    sb.Append("  [?] ").Append(who).Append("  — ").Append(f.Detail).Append('\n');
                    break;
            }
            if (f.ReadIncomplete) anyReadIncomplete = true;
            rendered++;
        }
        if (anyReadIncomplete)
            sb.Append("  note: a BSA failed to read this scan, so a \"missing .pex\" above may merely be unscanned — verify in MO2.\n");
        if (report.CheckError is not null)
            sb.Append("  result-script check could not run: ").Append(report.CheckError).Append(" — the records WERE created; verify the script binding manually.\n");
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

    [JsonPropertyName("verb"), Description("Set (default) | Add | Remove | SetAtIndex | ReplaceAll | Merge | CopyFrom (deep-copy the field at field_path from from_plugin's version — see from_plugin).")]
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

    [JsonPropertyName("composes"), Description("Build MANY modeled list elements in ONE op — the batch sibling of compose (each entry the same {type, fields?, ctor_args?, sets?} shape). With verb=Add, APPENDS each element in order (e.g. 10 leveled-list entries, a whole block of condition rows in one op instead of ten Adds). With verb=ReplaceAll, CLEARS the list then appends each — the way to replace a whole modeled list (conditions, effects, entries); pass composes=[] with ReplaceAll to CLEAR the list to empty (the modeled twin of values=[]). LIST elements only; mutually exclusive with compose/value/values. All-or-nothing: a bad element refuses the whole call with per-element (composes[i]) reasons.")]
    public StructInput[]? Composes { get; init; }

    [JsonPropertyName("from_plugin"), Description("For verb=\"CopyFrom\" ONLY: the plugin whose version of THIS record to deep-copy the field at field_path from — an ACTIVE plugin, OR a plugin FILE on disk that isn't in the load order (e.g. a disabled OLD patch you want to re-assert a field from). CopyFrom takes no value/values/entries/compose/composes — the source IS from_plugin's version of the field. Honors forward-then-edit precedence: into= a patch that already carries the record copies onto the patch's own version. Copies a WHOLE field's value (scalar, formlink, modeled list, sub-struct); it can't copy owned child records (forward the whole record with housecarl_forward_record instead).")]
    public string? FromPlugin { get; init; }
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

    [JsonPropertyName("grid"), Description("Optional. For an EXTERIOR cell only (record_type 'Cell' with parent= a Worldspace): the cell's grid as \"X,Y\" (e.g. \"5,-12\"). houseCARL files it into the worldspace's block tree by block=floor(grid/32), subblock=floor(grid/8). A 'Cell' with NO parent and NO grid is an INTERIOR cell (self-files by FormID). Ignored for non-Cell types.")]
    public string? Grid { get; init; }
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
