using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using Mutagen.Bethesda.Plugins;

namespace HousecarlMcp;

/// <summary>
/// houseCARL read tools (§8.4 Beat B). Reads ride the proven read core (<see cref="ReadEngine"/>) + the load-order
/// resolver (<see cref="LoadOrderResolver"/>) through <see cref="LoadOrderService"/>; they never mutate. The
/// compact `path = token` wire format (Q4.8 lever 1) is token-lean and the token IS a value a write can reuse.
/// conflict_tree adds the winner-relative field diff (Q4.8 lever 2). Every bulk view estimates its size and
/// stops with an explicit notice rather than truncating silently (Q3 + Q4.9).
/// </summary>
[McpServerToolType]
public static class ReadTools
{
    [McpServerTool(Name = "housecarl_read_record", ReadOnly = true, Title = "Read a record"),
     Description(
         "Read one record's fields as the load order resolves it. Returns the TRUE load-order winner's values by " +
         "default — or a named plugin's version via `plugin` — as compact `path = token` lines whose tokens a write " +
         "can reuse verbatim. With conflict_tree=true, also lists every plugin that touches the record (load order, " +
         "winner last) AND the winner-relative field diff (each other plugin's only-the-fields-that-differ). A FormID " +
         "is 'XXXXXX:Plugin.esp'. Does NOT modify anything. For many records in one call use " +
         "housecarl_batch_record_detail; to scan the order by type/reference/conflict use housecarl_cross_plugin_query; " +
         "to edit, use housecarl_set_field.")]
    public static string ReadRecord(
        LoadOrderService svc,
        [Description("The record's FormID as 'XXXXXX:Plugin.esp' — 6 hex digits, a colon, then the defining master's filename. Example: '0F1AC1:Skyrim.esm'.")]
            string formid,
        [Description("Optional. Read THIS plugin's version of the record instead of the load-order winner (a filename, e.g. 'Requiem.esp'). Useful to inspect a specific override.")]
            string? plugin = null,
        [Description("Optional. Dotted field paths to read (e.g. 'BasicStats.Damage', 'Name', 'Keywords'). Index into a list/dict element with BRACKETS, e.g. 'Effects[0].Data.Magnitude' or 'VirtualMachineAdapter.Aliases[0].Scripts[0].Properties[5].Name' — a bare '.0' is read as a field name, not an index. Omit to dump every modeled field one level deep (pass depth= to expand list/dict contents).")]
            string[]? fields = null,
        [Description("Optional. Expansion depth for list/dict/substruct CONTENTS (default 1 = one level, a container shown as just a count like '[List: 22 item(s)]'). depth=2 enumerates each element with its index + an identity, e.g. 'VirtualMachineAdapter.Aliases[0].Scripts[0].Properties[5] = [ScriptObjectProperty] Name=DAK_HorseBuyPerk' AND, one level beneath it, that property's VALUE ('...Properties[5].Object = 0F1AC1:Skyrim.esm', a Data scalar, or '(null link)' for a declared-but-unset property) — so you can SEE indices/contents without probing each [i]. Higher depth opens deeper. Pair with a fields= path to expand only that subtree; on a whole-record dump it expands every container (bounded, with an explicit truncation note).")]
            int depth = 1,
        [Description("When true, also return the ordered list of every plugin that touches this record (winner last) and the winner-relative field diff for each.")]
            bool conflict_tree = false,
        [Description("Optional. Max characters before the diff is cut with an explicit notice (never silent). 0 = the server default (~80k). Raise to see a very deep conflict tree in full.")]
            int max_chars = 0) => Guard.Tool("housecarl_read_record", () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        FormKey fk;
        try { fk = FormKey.Factory(formid.Trim()); }
        catch (Exception ex) { return $"error: bad FormID '{formid}': {ex.Message}. Expected 'XXXXXX:Plugin.esp', e.g. '0F1AC1:Skyrim.esm'."; }

        var outcome = svc.ResolveRead(fk, plugin?.Trim(), fields, conflict_tree, depth <= 0 ? 1 : depth);
        return Wire.RenderRecord(svc, outcome, fields, conflict_tree, max_chars);
    });

    [McpServerTool(Name = "housecarl_batch_record_detail", ReadOnly = true, Title = "Read many records"),
     Description(
         "Read many records in ONE call (saves per-record tool-call overhead). Each FormID resolves to its " +
         "load-order winner and renders like housecarl_read_record; a bad or absent FormID yields a per-item error " +
         "without failing the batch. With conflict_tree=true each record also gets its touching-plugin list + " +
         "winner-relative field diff. The combined response is size-estimated: over the cap it stops with an explicit " +
         "'rendered X of N' notice (never silent truncation) — request fewer formids, pass fields= to slim each, or " +
         "raise max_chars. Does NOT modify anything.")]
    public static string BatchRecordDetail(
        LoadOrderService svc,
        [Description("The FormIDs to read, each 'XXXXXX:Plugin.esp'. Resolved in order; results are returned in the same order.")]
            string[] formids,
        [Description("Optional. Dotted field paths to read for EVERY record (e.g. 'Name', 'BasicStats.Damage'); index a list/dict element with BRACKETS, e.g. 'Effects[0].Data.Magnitude'. Omit to dump every modeled field one level deep per record.")]
            string[]? fields = null,
        [Description("Optional. Expansion depth for list/dict/substruct CONTENTS per record (default 1). depth=2 enumerates each container's elements with index + identity (see housecarl_read_record). Bounded per record with an explicit truncation note.")]
            int depth = 1,
        [Description("When true, include each record's touching-plugin list (winner last) + winner-relative field diff.")]
            bool conflict_tree = false,
        [Description("Optional. Max characters before the response stops with an explicit 'rendered X of N' notice. 0 = the server default (~80k).")]
            int max_chars = 0) => Guard.Tool("housecarl_batch_record_detail", () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        if (formids is null || formids.Length == 0) return "error: formids is empty. Pass one or more 'XXXXXX:Plugin.esp' FormIDs.";
        var outcomes = svc.ResolveBatch(formids, fields, conflict_tree, depth <= 0 ? 1 : depth);
        return Wire.RenderBatch(svc, outcomes, fields, conflict_tree, max_chars);
    });

    [McpServerTool(Name = "housecarl_cross_plugin_query", ReadOnly = true, Title = "Query records across the load order"),
     Description(
         "Find records across the whole load order matching a filter — returns matches only, each as a compact " +
         "summary line (FormID, type, editorid, winner, override depth). Filters (combine freely): type= a record " +
         "signature ('WEAP') or catalog name ('Weapon'); conflicts_only=true for records >1 plugin touches; " +
         "editorid_contains= a substring of the EditorID; references= one or more FormIDs the record points at " +
         "(reverse lookup, e.g. 'what uses this keyword' — OR over the list, and each match shows which target(s) it " +
         "hit); where= filters by a field's VALUE (e.g. 'MagicSkill = Destruction', " +
         "'BasicStats.Damage >= 50' — any scalar field, ANDed); plugins= limits the scan to records those plugins " +
         "touch (a bare plugins= is 'everything this plugin touches'); defined_in=true narrows a plugins= scope to " +
         "records DEFINED in those plugins (not overrides they merely touch). At least one filter or plugins= is " +
         "required. editorid_contains/references/where " +
         "are body scans and MUST be combined with type= or plugins= to bound the work (conflicts_only= alone is not " +
         "enough). Pass fields= " +
         "or conflict_tree=true to expand each match from a summary line to full detail; or group_by= " +
         "(winner|type|defined_in) for a count table over ALL matches instead of per-match lines. Results cap at " +
         "limit= matches and max_chars; both overruns are reported explicitly (never silent). Does NOT modify anything.")]
    public static string CrossPluginQuery(
        LoadOrderService svc,
        [Description("Optional. A record signature ('WEAP', 'NPC_') or catalog name ('Weapon', 'Npc'). Cheap — uses typed group enumeration.")]
            string? type = null,
        [Description("Optional. One or more FormIDs 'XXXXXX:Plugin.esp'; matches records that reference ANY of them (OR, deep link scan). Each match line shows matches=<which target(s)> when you pass 2+. Must be combined with type= or plugins= (conflicts_only= alone is not enough).")]
            string[]? references = null,
        [Description("Optional. Case-insensitive substring of the EditorID. Body scan — must be combined with type= or plugins= (conflicts_only= alone is not enough).")]
            string? editorid_contains = null,
        [Description("When true, restrict to records more than one plugin touches (the contested set).")]
            bool conflicts_only = false,
        [Description("Optional. Plugin filenames to scope the scan to (records those plugins touch). A name not in the load order is an error. Omit to scan the whole order.")]
            string[]? plugins = null,
        [Description("When true, narrow a plugins= scope to records DEFINED in those plugins (origin FormID), not overrides they merely touch — the catalogue-scope semantics. Requires plugins= (refused loud otherwise).")]
            bool defined_in = false,
        [Description("Optional. Field-VALUE predicates, each \"<path> <op> <value>\" — e.g. 'MagicSkill = Destruction', 'BasicStats.Damage >= 50', 'Archetype.ActorValue = Infamy'. Operators: = != > >= < <= (>/< numeric) and contains (case-insensitive substring); multiple are ANDed. Filters on ANY scalar field the read tools can read (any type, any depth). A body scan — MUST be combined with type= or plugins=. A wrong or container/list path is reported, never a silent '0 matches'.")]
            string[]? where = null,
        [Description("Optional. Aggregate matches into a count table (sorted desc) instead of listing them: 'winner' (by load-order-winning plugin), 'type' (by record type — needs type= or plugins=), or 'defined_in' (by defining plugin). Counts ALL matches (not capped by limit=). Cannot combine with fields= or conflict_tree=.")]
            string? group_by = null,
        [Description("Optional. Dotted field paths to show for each match (e.g. 'BasicStats.Damage'). Omit for a one-line summary per match.")]
            string[]? fields = null,
        [Description("When true, include each match's touching-plugin list (winner last) + winner-relative field diff.")]
            bool conflict_tree = false,
        [Description("Optional. Max matches to return (default 500). The TRUE total is always reported; over the cap it says 'showing first N'.")]
            int limit = 500,
        [Description("Optional. Max characters before the response stops with an explicit notice. 0 = the server default (~80k).")]
            int max_chars = 0) => Guard.Tool("housecarl_cross_plugin_query", () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        if (group_by is not null && ((fields is { Length: > 0 }) || conflict_tree))
            return "error: group_by aggregates matches into a count table and cannot be combined with fields= or conflict_tree=true (those expand each match to full detail — pick one). Drop fields=/conflict_tree, or drop group_by.";
        IReadOnlyList<FormKey>? refFks = null;
        if (references is { Length: > 0 })
        {
            var list = new List<FormKey>();
            foreach (var r in references)
            {
                if (string.IsNullOrWhiteSpace(r)) continue;
                try { list.Add(FormKey.Factory(r.Trim())); }
                catch (Exception ex) { return $"error: bad references FormID '{r}': {ex.Message}. Expected 'XXXXXX:Plugin.esp'."; }
            }
            if (list.Count > 0) refFks = list.Distinct().ToList();   // preserve input order, drop dupes
        }
        var outcome = svc.CrossQuery(type, refFks, editorid_contains, conflicts_only, plugins, where, limit <= 0 ? 500 : limit, defined_in, group_by);
        return Wire.RenderCrossQuery(svc, outcome, fields, conflict_tree, max_chars);
    });

    [McpServerTool(Name = "housecarl_resolve", ReadOnly = true, Title = "Resolve FormIDs to their identity"),
     Description(
         "Turn a batch of FormIDs into their load-order identity — for EACH: type, editorid, display name, and " +
         "winning plugin — in ONE call. The bulk name-resolution primitive: where housecarl_batch_record_detail frames " +
         "every record (fields, override depth, per-record header), this returns one compact identity line (or JSON " +
         "row) per FormID and nothing else — the cheap way to label a list of material/perk/keyword FormIDs. Resolved " +
         "in order; a bad or absent FormID yields a per-item error without failing the batch (never a silent drop — " +
         "Q3). Winners only (the load-order-effective identity of each target). Deliberately minimal: no fields=, no " +
         "depth, no conflict_tree — for those use housecarl_batch_record_detail. Does NOT modify anything.")]
    public static string Resolve(
        LoadOrderService svc,
        [Description("The FormIDs to resolve, each 'XXXXXX:Plugin.esp'. Resolved in order; results are returned in the same order.")]
            string[] formids,
        [Description("Optional. 'text' (default) — one compact identity line per FormID — or 'json' for a machine-readable document (one {formid,type,editorid,name,winner} row per input; a bad/absent input carries {formid,error}).")]
            string? format = null,
        [Description("Optional. Max characters before the text response stops with an explicit notice. 0 = the server default (~80k). (JSON is bounded by the input count — each row is one small object.)")]
            int max_chars = 0) => Guard.Tool("housecarl_resolve", () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        if (formids is null || formids.Length == 0) return "error: formids is empty. Pass one or more 'XXXXXX:Plugin.esp' FormIDs.";
        bool json = Wire.WantsJson(format, out var ferr);
        if (ferr is not null) return ferr;
        var rows = svc.ResolveRefs(formids);
        return json ? JsonWire.RenderResolve(rows) : Wire.RenderResolve(rows, max_chars);
    });

    [McpServerTool(Name = "housecarl_effect_chain", ReadOnly = true, Title = "Resolve an effect's carriers + magnitudes"),
     Description(
         "Given a MagicEffect (MGEF), return every Spell/Enchantment/Potion/Scroll/Ingredient (SPEL/ENCH/ALCH/SCRL/INGR) " +
         "that APPLIES it, each with the magnitude/area/duration from the MATCHING effect entry — collapsing the " +
         "'cross_plugin_query references=<MGEF> then read each hit's effect' trace (repeated across five record types) " +
         "into one call. The FormID MUST resolve to a MagicEffect: a non-MGEF or absent FormID fails LOUD (never a " +
         "silent '0 carriers') — to find what references an arbitrary record, use housecarl_cross_plugin_query " +
         "references=. A carrier that applies the effect in more than one entry yields one row per entry. Magnitude is " +
         "reported AS AUTHORED: this does NOT evaluate the effect's Conditions, so a row means 'this carrier defines the " +
         "effect at this strength', not 'it will fire'. Winners only (the load-order-effective version). Results cap at " +
         "limit= and max_chars (both overruns explicit, never silent). Does NOT modify anything.")]
    public static string EffectChainTool(
        LoadOrderService svc,
        [Description("The MagicEffect's FormID as 'XXXXXX:Plugin.esp' — 6 hex digits, a colon, then the defining master's filename. Must resolve to an MGEF in the active order.")]
            string mgef_formid,
        [Description("Optional. Narrow the scan to a subset of the effect-bearing types — any of 'SPEL','ENCH','ALCH','SCRL','INGR' (or the catalog names 'Spell','ObjectEffect','Ingestible','Scroll','Ingredient'). A non-effect-bearing type is refused. Omit to scan all five.")]
            string[]? types = null,
        [Description("Optional. Max rows (default 500). The TRUE total is always reported; over the cap it says 'showing first N'.")]
            int limit = 500,
        [Description("Optional. Max characters before the response stops with an explicit notice. 0 = the server default (~80k).")]
            int max_chars = 0) => Guard.Tool("housecarl_effect_chain", () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        FormKey fk;
        try { fk = FormKey.Factory(mgef_formid.Trim()); }
        catch (Exception ex) { return $"error: bad FormID '{mgef_formid}': {ex.Message}. Expected 'XXXXXX:Plugin.esp', e.g. '0F1AC1:Skyrim.esm'."; }

        var result = svc.ResolveEffectChain(fk, types, limit <= 0 ? 500 : limit);
        return Wire.RenderEffectChain(result, max_chars);
    });

    [McpServerTool(Name = "housecarl_check_errors", ReadOnly = true, Title = "Check the load order for record errors"),
     Description(
         "Load-order integrity sweep — the data-layer twin of the Creation Kit's 'Check For Errors' / xEdit's error " +
         "check. For each plugin in scope it walks every record's FormLinks and reports three error classes: (1) DANGLING " +
         "references — a non-null link whose target NO plugin in the ACTIVE order defines (a broken reference); (2) " +
         "MISSING MASTERS — a master a plugin DECLARES that is not present in the active order (its dependency is not " +
         "installed/enabled — the most common load-order break); (3) PARSE failures — records houseCARL/Mutagen could not " +
         "read (per record), plus whole plugins the index excluded as unparseable. A scoped name NOT in the active order " +
         "is resolved on disk (any mod folder — enabled, disabled, or not yet listed in MO2) and swept OFF-ORDER: its own " +
         "records, links resolved against the active order PLUS the file's own definitions — the pre-enable verify sweep " +
         "for a patch houseCARL just wrote. Read-only — writes nothing. BOUNDARY " +
         "(never a silent claim of more — Q3): this covers the FormLink-resolution / missing-master / parse class. It does " +
         "NOT verify navmesh or terrain spatial integrity (CRC/grid — a Mutagen-delta residual), does NOT flag a required " +
         "field left null (a null FormLink is a legal optional, not an error), and does NOT list unused-master cleanup " +
         "(a FormLink scan cannot prove a master is unused). Results cap at limit= and max_chars (both overruns explicit).")]
    public static string CheckErrorsTool(
        LoadOrderService svc,
        [Description("Optional. Plugin filenames to check (e.g. 'MyMod.esp'). A name not in the active order is resolved on disk (a fresh houseCARL patch, a disabled mod) and swept OFF-ORDER; found nowhere (or in several folders) it is an error. Omit to sweep the WHOLE active order (every non-excluded plugin) — thorough but heavier; scope to one plugin for a fast, focused check like the CK's per-plugin 'Check For Errors'.")]
            string[]? plugins = null,
        [Description("Optional. Max dangling references to list across the whole sweep (default 1000). The TRUE total is always reported; over the cap it says so. Master-table findings are always listed in full (they are few).")]
            int limit = 1000,
        [Description("Optional. Max characters before the response stops with an explicit notice. 0 = the server default (~80k).")]
            int max_chars = 0) => Guard.Tool("housecarl_check_errors", () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        var result = svc.CheckErrors(plugins, limit <= 0 ? 1000 : limit);
        return Wire.RenderCheckErrors(result, max_chars);
    });

    [McpServerTool(Name = "housecarl_validate_scripts", ReadOnly = true, Title = "Check scripted records for unbound script properties"),
     Description(
         "Script-property binding sweep — catches the silent-None footgun a byte-valid plugin hides: a record whose " +
         "attached Papyrus script DECLARES a property (e.g. 'Spell Property CallVesyraPower Auto') the record's script " +
         "data (VMAD) never BINDS, so at runtime it is None and the code that uses it no-ops while the log looks clean " +
         "(the maximally-misleading 'the function ran, the effect is absent' class — the same as the Creation Kit's " +
         "auto-add-property bug). For each record carrying a script it reads the attached script's compiled .pex — and " +
         "every script it EXTENDS — from the load order (loose or BSA), and reports: (1) UNBOUND properties declared " +
         "but not bound (an object/form type ⇒ None ⇒ the silent no-op, ranked first; an uninitialized scalar ⇒ a " +
         "0/false/\"\" default that may be wrong); (2) BOUND-BUT-NULL object properties (advisory — sometimes filled at " +
         "runtime). Read-only. BOUNDARY (never a silent claim of more — Q3): it checks Auto (CK-editable) properties " +
         "only, not code-driven full properties; 'unbound may be intentional' (a runtime-filled link), so a finding is " +
         "a flag to VERIFY; and if a script's .pex is not on disk (uncompiled / not in the order) the attachment is " +
         "reported UNVERIFIABLE, never passed clean. Scope to one plugin for a fast focused check, or omit to sweep the " +
         "whole active order. Results cap at limit= and max_chars (both overruns explicit).")]
    public static string ValidateScriptsTool(
        LoadOrderService svc,
        [Description("Optional. Plugin filenames to check (e.g. 'MyMod.esp'). A name not in the load order is an error. Omit to sweep the WHOLE active order (every scripted record in every non-excluded plugin) — thorough but heavier; scope to one plugin for a fast, focused check.")]
            string[]? plugins = null,
        [Description("Optional. Max property findings (unbound + bound-but-null) to list across the whole sweep (default 1000). The TRUE totals are always reported; over the cap it says so. Unverifiable notes are always listed in full (they are few).")]
            int limit = 1000,
        [Description("Optional. Max characters before the response stops with an explicit notice. 0 = the server default (~80k).")]
            int max_chars = 0) => Guard.Tool("housecarl_validate_scripts", () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        var result = svc.ValidateScripts(plugins, limit <= 0 ? 1000 : limit);
        return Wire.RenderScriptCheck(result, max_chars);
    });

    [McpServerTool(Name = "housecarl_read_plugin_file", ReadOnly = true, Title = "Read a plugin file directly (active or not)"),
     Description(
         "Read ONE plugin file straight off disk — INCLUDING a plugin DISABLED in MO2 — returning THAT FILE's own " +
         "version of a record, NOT the load-order winner. Where housecarl_read_record resolves the ACTIVE order, this " +
         "reaches an inactive/arbitrary plugin: give it a filename (located even inside a DISABLED mod folder) or an " +
         "absolute path. Modes: formid= reads one record's fields (compact `path = token`, same format as read_record); " +
         "type= enumerates the records of that type the file defines/overrides; neither returns a record-type summary " +
         "(what's in the file). EVERY result is labeled OUT-OF-LOAD-ORDER — the game does not load this file. It emits " +
         "FormLinks as FormKey tokens (does NOT follow links), so it needs no masters present; a declared master that " +
         "is not installed is flagged. Read-only — writes nothing: read an inactive donor here, then author into a NEW " +
         "active patch with the write tools. Primary use: fork/borrow an existing NPC's appearance records (the " +
         "standalone-copy flow). A missing/ambiguous filename, a bad FormID, or a FormID the file does not define is " +
         "reported explicitly (never a silent wrong answer). To read the load-order WINNER instead, use " +
         "housecarl_read_record.")]
    public static string ReadPluginFile(
        LoadOrderService svc,
        [Description("The plugin to read: a FILENAME (e.g. 'Vivace.esp' — located across all mod folders, ENABLED or DISABLED, the overwrite folder, and game Data) OR an absolute path to a .esp/.esm/.esl. This is the FILE to open, not a load-order lookup.")]
            string plugin,
        [Description("Optional. Read THIS record's fields from the file, as 'XXXXXX:Plugin.esp' (6 hex, a colon, the defining master's filename). Reads the file's OWN version — a record it defines, or an override it carries. Mutually exclusive with type=.")]
            string? formid = null,
        [Description("Optional. Enumerate the records of this type the file defines/overrides — a signature ('NPC_','HDPT') or catalog name ('Npc','HeadPart'). Mutually exclusive with formid=. Omit BOTH formid= and type= for a record-type summary of the whole file.")]
            string? type = null,
        [Description("Optional. When a bare FILENAME is provided by more than one MO2 mod folder, the exact mod folder name to read from — disambiguates instead of guessing. Ignored for an absolute path.")]
            string? mod = null,
        [Description("Optional. With formid=: dotted field paths to read (e.g. 'HeadParts', 'FaceMorph', 'Name'); index a list/dict element with BRACKETS (e.g. 'HeadParts[0]'). Omit to dump every modeled field one level deep.")]
            string[]? fields = null,
        [Description("Optional. With formid=: expansion depth for list/dict/substruct CONTENTS (default 1; higher enumerates elements — see housecarl_read_record).")]
            int depth = 1,
        [Description("Optional. With type=: case-insensitive substring of the EditorID to filter the enumerated records.")]
            string? editorid_contains = null,
        [Description("Optional. With type=: max rows to return (default 500). The TRUE total is always reported; over the cap it says 'showing first N'.")]
            int limit = 500,
        [Description("Optional. Max characters before the response stops with an explicit notice. 0 = the server default (~80k).")]
            int max_chars = 0) => Guard.Tool("housecarl_read_plugin_file", () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        var outcome = svc.ReadPluginFile(plugin, formid, type, mod, fields, depth <= 0 ? 1 : depth, editorid_contains, limit <= 0 ? 500 : limit);
        return Wire.RenderPluginFile(outcome, max_chars);
    });
}

/// <summary>Compact, parseable `key = value` rendering (Q4.8 lever 1) + the winner-relative conflict diff
/// (lever 2) + response-size estimation (Q3 / Q4.9 — explicit cut, never silent). Shared by all three read tools.</summary>
static class Wire
{
    /// <summary>Server default char budget for one tool response (~20k tokens). A caller raises it per-call via max_chars.</summary>
    public const int DefaultMaxChars = 80_000;

    /// <summary>Default char budget for ANY write-tool READ-BACK dump (HCBR-2026-06-28-01). Deliberately well BELOW
    /// <see cref="DefaultMaxChars"/> / the host's per-result token ceiling: the forced in-place verify deep-dumped
    /// every touched record and, at the 80k default, the "gracefully truncated" 80k string STILL exceeded the host
    /// limit and spilled to a file (silent, Q3-breaking). The compact default render is tiny; this bounds the opt-in
    /// full_readback=true dump, whose truncation note now actually reaches the caller. INTENTIONALLY GLOBAL (PR #127
    /// review #3): the host ceiling is a transport property, not an in-place-lane one, so this cap applies to EVERY
    /// AppendFullReadback caller — the in-place verify, forward_record, and the new-file full_readback=true dump all
    /// hit the same spill bug, and all want the same bound. A caller raises it per-call via max_chars.</summary>
    public const int ReadbackMaxChars = 24_000;

    static int Cap(int maxChars) => maxChars > 0 ? maxChars : DefaultMaxChars;

    /// <summary>Parse the shared format= param (Wave 2 / P6): null/"text" ⇒ false (the default text render), "json" ⇒
    /// true, anything else ⇒ false with a NAMED error in <paramref name="error"/> (never a silent fall-through to
    /// text on a typo — Q3). Case/whitespace-insensitive.</summary>
    public static bool WantsJson(string? format, out string? error)
    {
        error = null;
        var f = format?.Trim();
        if (string.IsNullOrEmpty(f) || f.Equals("text", StringComparison.OrdinalIgnoreCase)) return false;
        if (f.Equals("json", StringComparison.OrdinalIgnoreCase)) return true;
        error = $"error: format='{format}' is not recognized — use 'text' (the default) or 'json'.";
        return false;
    }

    // ---- housecarl_resolve --------------------------------------------------------------------------
    /// <summary>Render the bulk name-resolution result (housecarl_resolve — P3): one compact identity line per input
    /// FormID (type/editorid/name/winner), or <c>error=</c> for a bad/absent input (per-item, the batch survives — Q3).
    /// Budget-bounded with the same explicit cut the other reads use.</summary>
    public static string RenderResolve(IReadOnlyList<ResolvedRef> rows, int maxChars)
    {
        int cap = Cap(maxChars);
        var sb = new StringBuilder();
        sb.Append("resolve: ").Append(rows.Count).Append(rows.Count == 1 ? " formid\n" : " formids\n");
        for (int i = 0; i < rows.Count; i++)
        {
            if (sb.Length >= cap)
            {
                sb.Append("... [truncated: rendered ").Append(i).Append(" of ").Append(rows.Count)
                  .Append(" at max_chars=").Append(cap).Append("; request fewer formids or raise max_chars]\n");
                break;
            }
            var r = rows[i];
            sb.Append("  ").Append(r.Token);
            if (r.Resolved)
            {
                sb.Append("  type=").Append(r.Type).Append("  editorid=").Append(r.EditorId ?? "<none>");
                if (!string.IsNullOrEmpty(r.Name)) sb.Append("  name=\"").Append(r.Name).Append('"');
                sb.Append("  winner=").Append(r.Winner);
            }
            else sb.Append("  error=").Append(r.Error ?? "not present in the active order");
            sb.Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }

    // ---- housecarl_read_record ----------------------------------------------------------------------
    public static string RenderRecord(LoadOrderService svc, ReadOutcome o, IReadOnlyList<string>? fields, bool conflictTree, int maxChars)
    {
        if (o.Error is not null) return "error: " + o.Error;
        var sb = new StringBuilder();
        AppendRecord(sb, o, Cap(maxChars));
        if (conflictTree) AppendConflictTree(sb, svc, o, fields, Cap(maxChars));
        return sb.ToString().TrimEnd('\n');
    }

    // ---- housecarl_batch_record_detail --------------------------------------------------------------
    public static string RenderBatch(LoadOrderService svc, IReadOnlyList<ReadOutcome> outcomes, IReadOnlyList<string>? fields, bool conflictTree, int maxChars)
    {
        int cap = Cap(maxChars);
        var sb = new StringBuilder();
        sb.Append("batch: ").Append(outcomes.Count).Append(outcomes.Count == 1 ? " record\n" : " records\n");
        int rendered = 0;
        foreach (var o in outcomes)
        {
            if (sb.Length >= cap)
            {
                sb.Append("... [truncated: rendered ").Append(rendered).Append(" of ").Append(outcomes.Count)
                  .Append(" records before hitting max_chars=").Append(cap)
                  .Append("; request fewer formids, pass fields= to slim each, or raise max_chars]\n");
                break;
            }
            sb.Append('\n');
            if (o.Error is not null) sb.Append("error: ").Append(o.Error).Append('\n');
            else { AppendRecord(sb, o, cap); if (conflictTree) AppendConflictTree(sb, svc, o, fields, cap); }
            rendered++;
        }
        return sb.ToString().TrimEnd('\n');
    }

    // ---- housecarl_cross_plugin_query ---------------------------------------------------------------
    public static string RenderCrossQuery(LoadOrderService svc, CrossQueryOutcome q, IReadOnlyList<string>? fields, bool conflictTree, int maxChars)
    {
        if (q.Error is not null) return "error: " + q.Error;
        int cap = Cap(maxChars);
        if (q.Groups is not null) return RenderCrossQueryGroups(q, cap);   // group_by= → a count table, not per-match lines
        bool detail = (fields is { Count: > 0 }) || conflictTree;          // expand matches, vs. one-line summaries
        var sb = new StringBuilder();
        sb.Append("cross_plugin_query: ").Append(q.Total).Append(q.Total == 1 ? " match" : " matches");
        if (q.ScopeLabel is not null) sb.Append(" DEFINED IN ").Append(q.ScopeLabel);   // P1: explicit scope — NOT the 'touches' default
        if (q.Capped) sb.Append(" (showing first ").Append(q.Keys.Count).Append("; raise limit= or narrow to see more)");
        sb.Append('\n');
        if (q.PredicateNote is not null) sb.Append(q.PredicateNote).Append('\n');   // where= Q3 accounting (wrong-path/no-value surface)
        if (q.ScanNote is not null) sb.Append(q.ScanNote).Append('\n');             // unscannable-record Q3 accounting (Mutagen-unparseable content)

        int rendered = 0;
        for (int i = 0; i < q.Keys.Count; i++)
        {
            if (sb.Length >= cap)
            {
                sb.Append("... [truncated: rendered ").Append(rendered).Append(" of ").Append(q.Keys.Count)
                  .Append(" returned matches before hitting max_chars=").Append(cap)
                  .Append("; lower limit=, drop fields=/conflict_tree, or raise max_chars]\n");
                break;
            }
            var fk = q.Keys[i];
            string? matches = q.MatchedTargets is { } mt && i < mt.Count ? mt[i] : null;   // multi-target references= un-merge
            if (detail)
            {
                var o = svc.ResolveRead(fk, q.Sources is { } src ? src[i] : null, fields, conflictTree);   // display the body the scan filtered: scoped plugin under plugins=, else winner
                sb.Append('\n');
                if (matches is not null) sb.Append("  ").Append(fk).Append("  matches=").Append(matches).Append('\n');
                if (o.Error is not null) sb.Append(fk).Append(": error: ").Append(o.Error).Append('\n');
                else { AppendRecord(sb, o, cap); if (conflictTree) AppendConflictTree(sb, svc, o, fields, cap); }
            }
            else
            {
                var m = q.Prefilled is not null ? q.Prefilled[i] : svc.ResolveSummary(fk);   // lazy fill for conflicts-only
                sb.Append("  ").Append(m.FormKey);
                if (m.Error is not null) sb.Append("  error=").Append(m.Error).Append('\n');
                else
                {
                    sb.Append("  type=").Append(m.Type).Append("  editorid=").Append(m.EditorId ?? "<none>")
                      .Append("  winner=").Append(m.Winner).Append("  override_depth=").Append(m.OverrideDepth);
                    if (matches is not null) sb.Append("  matches=").Append(matches);
                    sb.Append('\n');
                }
            }
            rendered++;
        }
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>Render a cross_plugin_query <c>group_by=</c> aggregation: a header naming the key + true total + group
    /// count, then one "  &lt;key&gt; = &lt;count&gt;" row per group (already sorted desc by the core). Q3 accounting
    /// (where= / unscannable notes) survives the aggregation. Over max_chars it stops with the explicit truncation
    /// notice — the count is exact even when the row LIST is clipped (aggregation isn't limit-capped; only rendering is).</summary>
    static string RenderCrossQueryGroups(CrossQueryOutcome q, int cap)
    {
        var groups = q.Groups!;
        var sb = new StringBuilder();
        sb.Append("cross_plugin_query: grouped by ").Append(q.GroupBy).Append(" — ")
          .Append(q.Total).Append(q.Total == 1 ? " match" : " matches")
          .Append(" across ").Append(groups.Count).Append(groups.Count == 1 ? " group" : " groups");
        if (q.ScopeLabel is not null) sb.Append(" (DEFINED IN ").Append(q.ScopeLabel).Append(')');
        sb.Append('\n');
        if (q.PredicateNote is not null) sb.Append(q.PredicateNote).Append('\n');
        if (q.ScanNote is not null) sb.Append(q.ScanNote).Append('\n');
        for (int i = 0; i < groups.Count; i++)
        {
            if (sb.Length >= cap)
            {
                sb.Append("... [truncated: rendered ").Append(i).Append(" of ").Append(groups.Count)
                  .Append(" groups before hitting max_chars=").Append(cap).Append("; raise max_chars — the total above is exact]\n");
                break;
            }
            sb.Append("  ").Append(groups[i].Key).Append(" = ").Append(groups[i].Count).Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }

    // ---- housecarl_effect_chain ---------------------------------------------------------------------
    /// <summary>Render the effect chain: a header that RESOLVES the MGEF (its editorid + the confirmed MagicEffect
    /// type — the Q3 typed-match proof), then carrier rows grouped by record type. A valid-but-unused MGEF renders a
    /// clean "none" line, NOT an error (the error path is the bad/mistyped FormID, handled in core). Over max_chars it
    /// stops with the same explicit notice the other read renders use (Q3 — never silent).</summary>
    public static string RenderEffectChain(EffectChainResult r, int maxChars)
    {
        if (r.Error is not null) return "error: " + r.Error;
        int cap = Cap(maxChars);
        var sb = new StringBuilder();
        sb.Append("effect_chain for ").Append(r.Mgef).Append(" (").Append(r.MgefEditorId).Append(", MagicEffect): ")
          .Append(r.Total).Append(r.Total == 1 ? " carrier row" : " carrier rows");
        if (r.Capped) sb.Append(" (showing first ").Append(r.Rows.Count).Append("; raise limit= or narrow to see more)");
        sb.Append('\n');
        if (r.Total == 0)
            sb.Append("  none — ").Append(r.MgefEditorId)
              .Append(" is a valid MagicEffect but is applied by no SPEL/ENCH/ALCH/SCRL/INGR in the active order.\n");
        if (r.ScanNote is not null) sb.Append(r.ScanNote).Append('\n');

        int rendered = 0;
        bool truncated = false;
        // Group rows by carrier type (stable, ordinal) so a multi-type result reads grouped — like cross_plugin_query's type=.
        foreach (var grp in r.Rows.GroupBy(x => x.Type).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            if (truncated) break;
            sb.Append(grp.Key).Append(" (").Append(grp.Count()).Append("):\n");
            foreach (var row in grp)
            {
                if (sb.Length >= cap)
                {
                    sb.Append("  ... [truncated: rendered ").Append(rendered).Append(" of ").Append(r.Rows.Count)
                      .Append(" rows before hitting max_chars=").Append(cap).Append("; lower limit= or raise max_chars]\n");
                    truncated = true;
                    break;
                }
                sb.Append("  ").Append(row.Carrier)
                  .Append("  ").Append(row.EditorId ?? "<none>")
                  .Append("  winner=").Append(row.Winner)
                  .Append("  mag=").Append(row.Magnitude.ToString(System.Globalization.CultureInfo.InvariantCulture))
                  .Append("  area=").Append(row.Area)
                  .Append("  dur=").Append(row.Duration)
                  .Append("  [effect ").Append(row.EffectIndex + 1).Append('/').Append(row.EffectCount).Append("]\n");
                rendered++;
            }
        }
        return sb.ToString().TrimEnd('\n');
    }

    // ---- housecarl_check_errors ---------------------------------------------------------------------
    public static string RenderCheckErrors(ErrorCheckResult r, int maxChars)
    {
        if (r.Error is not null) return "error: " + r.Error;
        int cap = Cap(maxChars);
        var sb = new StringBuilder();

        sb.Append("check_errors — load-order integrity sweep\n");
        sb.Append("scanned ").Append(r.PluginsScanned).Append(r.PluginsScanned == 1 ? " plugin · " : " plugins · ")
          .Append(r.TotalDangling).Append(" dangling ref(s) · ")
          .Append(r.TotalMissingMasters).Append(" missing master(s) · ")
          .Append(r.TotalUnscannableRecords).Append(" unscannable record(s)");
        if (r.ExcludedPlugins.Count > 0)
            sb.Append(" · ").Append(r.ExcludedPlugins.Count).Append(" plugin(s) excluded (unparseable)");
        sb.Append('\n');
        if (r.OffOrderScanned is { Count: > 0 } off)
            sb.Append("swept OFF-ORDER (on disk, not in the active load order): ").Append(string.Join(", ", off))
              .Append("   [the file's own records; links resolved against the active order + the file's own definitions]\n");

        if (r.Reports.Count == 0 && r.ExcludedPlugins.Count == 0)
            sb.Append("\nNo errors found in the scanned scope.\n");

        bool truncated = false;
        foreach (var p in r.Reports)
        {
            if (sb.Length >= cap)
            {
                sb.Append("\n... [truncated at max_chars=").Append(cap).Append("; scope plugins= or raise max_chars to see the rest]\n");
                truncated = true;
                break;
            }
            sb.Append("\n[ERROR] ").Append(p.Plugin).Append('\n');
            if (p.ScanError is not null)
                sb.Append("  scan error: ").Append(p.ScanError).Append('\n');
            if (p.MissingMasters.Count > 0)
                sb.Append("  missing master(s): ").Append(string.Join(", ", p.MissingMasters))
                  .Append("   [declared as a dependency but not present in the active order — install/enable it, or this plugin's refs into it dangle]\n");
            if (p.Dangling.Count > 0)
            {
                sb.Append("  dangling reference(s) (").Append(p.Dangling.Count).Append("):\n");
                foreach (var d in p.Dangling)
                {
                    if (sb.Length >= cap) break;
                    sb.Append("    ").Append(d.Source).Append(" (").Append(d.SourceType);
                    if (!string.IsNullOrEmpty(d.SourceEditorId)) sb.Append(" '").Append(d.SourceEditorId).Append('\'');
                    sb.Append(") -> ").Append(d.Target).Append("   [target not defined by any active plugin]\n");
                }
            }
            if (p.UnscannableRecords > 0)
            {
                sb.Append("  ").Append(p.UnscannableRecords).Append(" record(s) could not be scanned (Mutagen could not parse their content)");
                if (p.UnscannableSamples.Count > 0) sb.Append(": ").Append(string.Join("; ", p.UnscannableSamples));
                sb.Append('\n');
            }
        }

        if (r.Capped)
            sb.Append("\n[dangling list capped at limit; true total = ").Append(r.TotalDangling).Append(" — raise limit= to see all]\n");

        if (!truncated && r.ExcludedPlugins.Count > 0)
        {
            sb.Append("\nexcluded plugins (could not be parsed — NOT checked):\n");
            foreach (var kv in r.ExcludedPlugins)
            {
                if (sb.Length >= cap) break;
                sb.Append("  ").Append(kv.Key).Append(": ").Append(kv.Value).Append('\n');
            }
        }

        sb.Append("\nboundary: checks FormLink resolution, missing masters, and parse failures. Does NOT verify navmesh/terrain ")
          .Append("spatial integrity (CRC/grid), flag required-but-null fields, or list unused-master cleanup; a null FormLink is a legal optional.\n");
        return sb.ToString().TrimEnd('\n');
    }

    // ---- housecarl_validate_scripts -----------------------------------------------------------------
    public static string RenderScriptCheck(ScriptCheckResult r, int maxChars)
    {
        if (r.Error is not null) return "error: " + r.Error;
        int cap = Cap(maxChars);
        var sb = new StringBuilder();

        sb.Append("validate_scripts — VMAD script-property binding sweep\n");
        sb.Append("scanned ").Append(r.PluginsScanned).Append(r.PluginsScanned == 1 ? " plugin · " : " plugins · ")
          .Append(r.RecordsWithScripts).Append(" record(s) with scripts · ")
          .Append(r.TotalUnbound).Append(" unbound · ")
          .Append(r.TotalNullObject).Append(" bound-but-null · ")
          .Append(r.TotalUnverifiable).Append(" unverifiable");
        if (r.ExcludedPlugins.Count > 0)
            sb.Append(" · ").Append(r.ExcludedPlugins.Count).Append(" plugin(s) excluded (unparseable)");
        sb.Append('\n');
        if (r.ReadIncomplete)
            sb.Append("note: a BSA failed to read this build — a '.pex not on disk' below may merely be unscanned, not truly absent (Q3).\n");

        if (r.Reports.Count == 0 && r.ExcludedPlugins.Count == 0)
            sb.Append("\nNo unbound script properties found in the scanned scope.\n");

        bool truncated = false;
        foreach (var rec in r.Reports)
        {
            if (sb.Length >= cap)
            {
                sb.Append("\n... [truncated at max_chars=").Append(cap).Append("; scope plugins= or raise max_chars to see the rest]\n");
                truncated = true;
                break;
            }
            if (rec.ScanError is not null) { sb.Append("\n[SCAN ERROR] ").Append(rec.Plugin).Append(": ").Append(rec.ScanError).Append('\n'); continue; }

            sb.Append('\n').Append(rec.Unbound.Count > 0 ? "[UNBOUND] " : "[CHECK] ")
              .Append(rec.Record).Append(" (").Append(rec.RecordType);
            if (!string.IsNullOrEmpty(rec.EditorId)) sb.Append(" '").Append(rec.EditorId).Append('\'');
            sb.Append(") in ").Append(rec.Plugin).Append('\n');

            // Unbound findings, object/form types (silent None) FIRST, then uninitialized scalars.
            foreach (var u in rec.Unbound.OrderByDescending(u => u.IsObjectType))
            {
                if (sb.Length >= cap) break;
                sb.Append("  ").Append(u.IsObjectType ? "! " : "· ")
                  .Append(u.PropertyName).Append(" (").Append(u.PexTypeName).Append(") on script ").Append(u.Script);
                if (!string.Equals(u.DeclaringScript, u.Script, StringComparison.OrdinalIgnoreCase))
                    sb.Append(" [declared in ").Append(u.DeclaringScript).Append(']');
                sb.Append(u.IsObjectType
                    ? " — declared but NOT bound → None at runtime (HIGH: object/form type — the silent no-op)\n"
                    : " — declared but NOT bound → defaults to 0/false/\"\" (scalar, no baked default)\n");
            }
            if (rec.NullObjects.Count > 0 && sb.Length < cap)
                sb.Append("  bound-but-null object propert").Append(rec.NullObjects.Count == 1 ? "y: " : "ies: ")
                  .Append(string.Join(", ", rec.NullObjects.Select(n => $"{n.PropertyName} ({n.Script})")))
                  .Append("   [advisory — a None link; sometimes intentional, filled at runtime]\n");
            foreach (var uv in rec.Unverifiable)
            {
                if (sb.Length >= cap) break;
                sb.Append("  could not verify script ").Append(uv.Script).Append(": ").Append(uv.Reason).Append('\n');
            }
        }

        if (r.Capped)
            sb.Append("\n[finding list capped at limit; true totals = ").Append(r.TotalUnbound).Append(" unbound + ")
              .Append(r.TotalNullObject).Append(" bound-but-null — raise limit= to see all]\n");

        if (!truncated && r.ExcludedPlugins.Count > 0)
        {
            sb.Append("\nexcluded plugins (could not be parsed — NOT checked):\n");
            foreach (var kv in r.ExcludedPlugins)
            {
                if (sb.Length >= cap) break;
                sb.Append("  ").Append(kv.Key).Append(": ").Append(kv.Value).Append('\n');
            }
        }

        sb.Append("\nboundary: checks Auto (CK-editable) properties across the extends chain — not code-driven full ")
          .Append("properties. An unbound object property is the silent-None footgun, but CAN be intentional (filled at runtime) — a ")
          .Append("finding is a flag to VERIFY. A script whose .pex is not on disk is reported unverifiable, never passed clean.\n");
        return sb.ToString().TrimEnd('\n');
    }

    // ---- shared building blocks ---------------------------------------------------------------------

    static void AppendRecord(StringBuilder sb, ReadOutcome o, int cap)
    {
        var r = o.Record!;
        sb.Append("type=").Append(r.Type)
          .Append("  formid=").Append(r.FormKey)
          .Append("  editorid=").Append(r.EditorId ?? "<none>")
          .Append("  winner=").Append(o.WinnerPlugin)
          .Append("  override_depth=").Append(o.OverrideDepth).Append('\n');
        sb.Append("fields (from ").Append(o.SourcePlugin).Append("):\n");
        for (int i = 0; i < r.Fields.Count; i++)
        {
            if (sb.Length >= cap)                                          // depth= can produce many lines — cap them (Q3)
            {
                sb.Append("  ... [truncated: showing ").Append(i).Append(" of ").Append(r.Fields.Count)
                  .Append(" field lines at max_chars=").Append(cap)
                  .Append("; narrow with fields=, lower depth=, or raise max_chars]\n");
                break;
            }
            var f = r.Fields[i];
            sb.Append("  ").Append(f.Path).Append(" = ").Append(f.HasValue ? f.Token : f.Note);
            if (f.Display is not null) sb.Append("   (").Append(f.Display).Append(')');   // display-only annotation (e.g. decoded biped slots) — never the round-trip token
            sb.Append('\n');
        }
    }

    /// <summary>The conflict tree: the ordered touching-plugin list, then (when >1 plugin touches) the
    /// winner-relative field diff — each other plugin's only-the-fields-that-differ, as `path=theirs (winner X)`.
    /// Bodies come from <see cref="LoadOrderService.ResolveTree"/> (on-demand fetch, held by nothing). The diff
    /// is char-budget-bounded: over <paramref name="cap"/> it stops with an explicit notice (Q3).</summary>
    static void AppendConflictTree(StringBuilder sb, LoadOrderService svc, ReadOutcome o, IReadOnlyList<string>? fields, int cap)
    {
        var tp = o.TouchingPlugins;
        if (tp is null) return;
        sb.Append("conflict_tree: ").Append(tp.Count).Append(tp.Count == 1 ? " plugin touches" : " plugins touch")
          .Append(" this record (load order, winner last):\n");
        for (int i = 0; i < tp.Count; i++)
        {
            if (sb.Length >= cap && i < tp.Count - 1)                      // budget gone mid-list — name the rest + the winner, stop
            {
                sb.Append("  ... [").Append(tp.Count - i).Append(" more plugins omitted at max_chars=").Append(cap)
                  .Append("; winner (last in load order) = ").Append(tp[^1]).Append("; raise max_chars or narrow with fields=]\n");
                return;                                                    // and skip the diff (the all-bodies fetch too) — already over budget
            }
            sb.Append("  ").Append(i + 1).Append(". ").Append(tp[i]).Append(i == tp.Count - 1 ? "  (winner)" : "").Append('\n');
        }

        if (tp.Count <= 1) return;                                         // nothing to diff against
        var tree = svc.ResolveTree(o.FormKey, fields);                     // materialised (no live overlay) — Option B
        if (tree is null || tree.Nodes.Count <= 1) return;

        var winnerNode = tree.Winner;                                       // Nodes[^1] = highest priority = the winner

        // CONTENT diff (HCBR-2026-06-09-01): each node's DEEP read vs the winner's, compared by FieldsDiff —
        // scalar leaves exact-path, list contents order-insensitively by whole element. The old depth-1 token
        // comparison called equal-count lists with different contents "identical to winner" — an affirmative
        // false ITM that masked a real override regression. "Identical" is now only claimed when the FULL
        // modeled content compared clean; a truncated comparison says so instead (Q3).
        sb.Append("diff (field deltas vs winner ").Append(winnerNode.Plugin)
          .Append("; identical fields omitted; list contents compared order-insensitively):\n");
        for (int n = 0; n < tree.Nodes.Count - 1; n++)                      // every node except the winner
        {
            if (sb.Length >= cap)
            {
                sb.Append("  ... [truncated: response hit max_chars=").Append(cap)
                  .Append("; pass fields= to narrow the diff or raise max_chars]\n");
                return;
            }
            var node = tree.Nodes[n];
            var diff = FieldsDiff.Compare(node.Record, winnerNode.Record);
            string line = diff.Deltas.Count > 0
                ? string.Join("; ", diff.Deltas)
                  + (diff.Complete ? "" : " [comparison TRUNCATED at the expansion cap — only value mismatches observed on both sides are shown; list contents and one-sided fields were NOT compared; narrow with fields= to fully compare]")
                : diff.Complete
                    // No deltas. Surface HOW MANY modeled fields read identical to the winner (item 4.3) — node-
                    // neutral, because this renders for every touching plugin including the master (Nodes[0]),
                    // which is not an "override". AgreedCount counts only value leaves read identical on both
                    // sides; it distinguishes an ITM-restate (high N) from a fields-narrow compare, without
                    // asserting intent the diff can't know.
                    ? (fields is { Count: > 0 }
                        ? IdenticalAcrossFields(diff)
                        : IdenticalWholeRecord(diff))
                    : "(no differences found, but the comparison was TRUNCATED at the expansion cap — NOT a verified ITM; narrow with fields= to fully compare)";
            // One node's joined deltas are unbounded (two divergent deep reads can carry thousands); slice
            // against the remaining char budget with the same explicit notice the other cuts use (Q3).
            int room = cap - sb.Length;
            if (line.Length > room)
                line = string.Concat(line.AsSpan(0, Math.Max(0, room)),
                    " ... [delta line truncated at max_chars; narrow with fields= or raise max_chars]");
            sb.Append("  ").Append(node.Plugin).Append(": ").Append(line).Append('\n');
        }
    }

    /// <summary>No-delta render for a WHOLE-record compare. Node-NEUTRAL: this renders for every touching plugin,
    /// including the master node (Nodes[0]), which is not an override — so it reports the COUNT of fields read
    /// identical to the winner (the ITM-restate-vs-no-op signal lives in N) without asserting an "override" or a
    /// "not a no-op" intent the diff can't know. A whole-record compare always agrees on housekeeping/identity
    /// leaves, so N is typically >0; that's stated as a plain fact, not a verdict. The sample names a few agreed
    /// paths; presence is claimed only for the value leaves the read engine actually surfaced (Q3 — never claim a
    /// non-nullable subrecord bit the read can't prove).</summary>
    static string IdenticalWholeRecord(FieldsDiff.Result diff) =>
        diff.AgreedCount > 0
            ? $"(no field deltas; {diff.AgreedCount} modeled field(s) read identical to the winner ({SampleOf(diff)}) — list order ignored)"
            : "(identical to winner — full modeled content compared, list order ignored)";

    /// <summary>No-delta render for a fields=-narrowed compare — node-neutral, and the identity claim must not
    /// outrun the compared paths (PR #28 review #2).</summary>
    static string IdenticalAcrossFields(FieldsDiff.Result diff) =>
        diff.AgreedCount > 0
            ? $"(no deltas across the requested fields; {diff.AgreedCount} of them read identical to the winner ({SampleOf(diff)}) — other fields NOT compared, list order ignored)"
            : "(identical to winner across the requested fields, list order ignored — other fields NOT compared)";

    static string SampleOf(FieldsDiff.Result diff)
    {
        var s = string.Join(", ", diff.AgreedSample);
        return diff.AgreedCount > diff.AgreedSample.Count ? $"e.g. {s}, …" : s;
    }

    // ---- housecarl_read_plugin_file -----------------------------------------------------------------
    /// <summary>Render a RAW plugin-file read. THE load-bearing requirement: every result is stamped
    /// OUT-OF-LOAD-ORDER up front, so a raw-file read is never mistaken for load-order truth. The read mode reuses
    /// the SAME `path = token` field format as read_record (round-trip parity). Size-bounded with an explicit cut,
    /// never silent (Q3). An "ambiguous" outcome lists the folders that provide the name and asks for mod=.</summary>
    public static string RenderPluginFile(PluginFileOutcome o, int maxChars)
    {
        if (o.Mode == "error") return "error: " + o.Error;
        int cap = Cap(maxChars);
        var sb = new StringBuilder();

        if (o.Mode == "ambiguous")
        {
            sb.Append("error: '").Append(Path.GetFileName(o.Requested)).Append("' is provided by ").Append(o.Ambiguous.Count)
              .Append(" locations — specify which with mod= (or pass an absolute path):\n");
            foreach (var h in o.Ambiguous)
                sb.Append("  ").Append(h.Where).Append("  ->  ").Append(h.Path).Append('\n');
            return sb.ToString().TrimEnd('\n');
        }

        // The banner — OUT-OF-LOAD-ORDER first, always (the single load-bearing requirement).
        sb.Append("read_plugin_file — OUT-OF-LOAD-ORDER (raw file read; the game does not load this file)\n");
        sb.Append("file: ").Append(o.FilePath);
        if (!string.IsNullOrEmpty(o.Where)) sb.Append("  [").Append(o.Where).Append(o.Enabled ? "" : "; NOT active").Append(']');
        sb.Append('\n');
        sb.Append("masters: ").Append(o.Masters.Count == 0 ? "none" : string.Join(", ", o.Masters)).Append('\n');
        if (o.MissingMasters.Count > 0)
            sb.Append("  ! declared master(s) NOT installed anywhere in the MO2 install: ").Append(string.Join(", ", o.MissingMasters))
              .Append("  (install them — the file will not load in-game without them)\n");
        if (o.InactiveMasters.Count > 0)
            sb.Append("  ! declared master(s) installed but NOT ACTIVE in the load order (in a disabled mod, or unchecked): ")
              .Append(string.Join(", ", o.InactiveMasters))
              .Append("  (enable them — the file will not load until you do)\n");

        if (o.Mode == "read")
        {
            var r = o.Record!;
            sb.Append("type=").Append(r.Type).Append("  formid=").Append(r.FormKey).Append("  editorid=").Append(r.EditorId ?? "<none>").Append('\n');
            sb.Append("fields (raw, from ").Append(Path.GetFileName(o.FilePath)).Append("):\n");
            for (int i = 0; i < r.Fields.Count; i++)
            {
                if (sb.Length >= cap)
                {
                    sb.Append("  ... [truncated: showing ").Append(i).Append(" of ").Append(r.Fields.Count)
                      .Append(" field lines at max_chars=").Append(cap).Append("; narrow with fields=, lower depth=, or raise max_chars]\n");
                    break;
                }
                var f = r.Fields[i];
                sb.Append("  ").Append(f.Path).Append(" = ").Append(f.HasValue ? f.Token : f.Note);
                if (f.Display is not null) sb.Append("   (").Append(f.Display).Append(')');   // display-only annotation (e.g. decoded biped slots) — never the round-trip token
                sb.Append('\n');
            }
        }
        else if (o.Mode == "enumerate")
        {
            sb.Append(o.RowTotal).Append(o.RowTotal == 1 ? " record" : " records");
            if (o.Capped) sb.Append(" (showing first ").Append(o.Rows.Count).Append("; raise limit= to see more)");
            sb.Append('\n');
            for (int i = 0; i < o.Rows.Count; i++)
            {
                if (sb.Length >= cap)
                {
                    sb.Append("  ... [truncated: rendered ").Append(i).Append(" of ").Append(o.Rows.Count)
                      .Append(" rows at max_chars=").Append(cap).Append("; lower limit= or raise max_chars]\n");
                    break;
                }
                var row = o.Rows[i];
                sb.Append("  ").Append(row.FormKey).Append("  type=").Append(row.Type).Append("  editorid=").Append(row.EditorId ?? "<none>").Append('\n');
            }
        }
        else // summary
        {
            sb.Append(o.RecordTotal).Append(o.RecordTotal == 1 ? " record" : " records").Append(" across ")
              .Append(o.TypeCounts.Count).Append(o.TypeCounts.Count == 1 ? " type" : " types").Append(":\n");
            for (int i = 0; i < o.TypeCounts.Count; i++)
            {
                if (sb.Length >= cap)
                {
                    sb.Append("  ... [truncated at max_chars=").Append(cap).Append("; raise max_chars to see all types]\n");
                    break;
                }
                var tc = o.TypeCounts[i];
                sb.Append("  ").Append(tc.Type).Append("  (").Append(tc.Count).Append(")\n");
            }
        }
        return sb.ToString().TrimEnd('\n');
    }
}
