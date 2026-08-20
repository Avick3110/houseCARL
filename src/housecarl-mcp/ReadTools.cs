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
        [Description("When true, annotate every FormLink field value with its target's identity (→ editorid \"Name\"), resolved against the load order — so a Keywords/Template/DeathItem token reads as what it points AT, not just a FormID. Display-only: the token itself is unchanged (a write can still reuse it). A target no active plugin defines is marked 'unresolved' — except the engine-implicit forms (PlayerRef 000014 / Player 000007), which annotate their hardcoded identity.")]
            bool resolve_names = false,
        [Description("Optional. 'text' (default) or 'json' — a machine-readable {formid,type,editorid,winner,override_depth,source,fields[]} document (field values are the SAME tokens as text). conflict_tree is a text-only diff view.")]
            string? format = null,
        [Description("Optional. Max characters before the diff is cut with an explicit notice (never silent). 0 = the server default (~80k). Raise to see a very deep conflict tree in full.")]
            int max_chars = 0) => Guard.Tool("housecarl_read_record", () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        bool json = Wire.WantsJson(format, out var ferr);
        if (ferr is not null) return ferr;
        if (json && conflict_tree) return "error: conflict_tree=true is a text-only diff view and is not carried in json mode — use format=text for the conflict tree, or drop conflict_tree for the json field data.";
        FormKey fk;
        try { fk = FormKey.Factory(formid.Trim()); }
        catch (Exception ex) { return $"error: bad FormID '{formid}': {ex.Message}. Expected 'XXXXXX:Plugin.esp', e.g. '0F1AC1:Skyrim.esm'."; }

        var outcome = svc.ResolveRead(fk, plugin?.Trim(), fields, conflict_tree, depth <= 0 ? 1 : depth, resolve_names);
        return json ? JsonWire.RenderRecord(outcome, max_chars) : Wire.RenderRecord(svc, outcome, fields, conflict_tree, max_chars);
    });

    [McpServerTool(Name = "housecarl_batch_record_detail", ReadOnly = true, Title = "Read many records"),
     Description(
         "Read many records in ONE call (saves per-record tool-call overhead). Each FormID resolves to its " +
         "load-order winner (or a named plugin's version via plugin=) and renders like housecarl_read_record; a bad " +
         "or absent FormID yields a per-item error without failing the batch. With conflict_tree=true each record " +
         "also gets its touching-plugin list + winner-relative field diff. The combined response is size-estimated: " +
         "over the cap it stops with an explicit 'rendered X of N' notice (never silent truncation) — request fewer " +
         "formids, pass fields= to slim each, or raise max_chars. The header carries epoch=<hex> — the load-order " +
         "build identity the WHOLE batch was answered from; a different epoch on a sibling call means the order " +
         "changed between them. Does NOT modify anything.")]
    public static string BatchRecordDetail(
        LoadOrderService svc,
        [Description("The FormIDs to read, each 'XXXXXX:Plugin.esp'. Resolved in order; results are returned in the same order.")]
            string[] formids,
        [Description("Optional. Read THIS plugin's version of EVERY record instead of the load-order winner (a filename, e.g. 'Gray Fox Cowl.esm') — the batch twin of housecarl_read_record's plugin=. Use to bulk-read a specific override's version (e.g. a mod's OWN records when something else currently wins). A formid that plugin doesn't touch gets its own per-item error; the rest still read.")]
            string? plugin = null,
        [Description("Optional. Dotted field paths to read for EVERY record (e.g. 'Name', 'BasicStats.Damage'); index a list/dict element with BRACKETS, e.g. 'Effects[0].Data.Magnitude'. Omit to dump every modeled field one level deep per record.")]
            string[]? fields = null,
        [Description("Optional. Expansion depth for list/dict/substruct CONTENTS per record (default 1). depth=2 enumerates each container's elements with index + identity (see housecarl_read_record). Bounded per record with an explicit truncation note.")]
            int depth = 1,
        [Description("When true, include each record's touching-plugin list (winner last) + winner-relative field diff.")]
            bool conflict_tree = false,
        [Description("When true, annotate every FormLink field value across every record with its target's identity (→ editorid \"Name\"), resolved against the load order and cached across the whole batch. Display-only: the token itself is unchanged. Unresolvable targets are marked.")]
            bool resolve_names = false,
        [Description("Optional. 'text' (default) or 'json' — a machine-readable {count, records:[…], rendered, truncated} document (each record like read_record's json; field values are the SAME tokens as text). conflict_tree is a text-only diff view.")]
            string? format = null,
        [Description("Optional. Max characters before the response stops with an explicit 'rendered X of N' notice. 0 = the server default (~80k).")]
            int max_chars = 0,
        [Description("Optional. Write the COMPLETE batch to this ABSOLUTE .jsonl path as a §2.1.1 artifact (line 1 = manifest with the epoch fingerprint; then one JSON record-row per input, per-item errors included) and render only the manifest inline. Re-enter it later via formids=[\"@<path>\"] — epoch-checked against the then-current build. Not combinable with conflict_tree (a text-only view with no row form).")]
            string? to_file = null) => Guard.Tool("housecarl_batch_record_detail", () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        if (formids is null || formids.Length == 0) return "error: formids is empty. Pass one or more 'XXXXXX:Plugin.esp' FormIDs.";
        bool json = Wire.WantsJson(format, out var ferr);
        if (ferr is not null) return ferr;
        if (json && conflict_tree) return "error: conflict_tree=true is a text-only diff view and is not carried in json mode — use format=text for the conflict tree, or drop conflict_tree for the json field data.";

        // formids= under the @file convention (§5.1): a single "@<path>" element stands for the whole list — a
        // plain file's tokens, or a §2.1.1 artifact's identity column + its epoch demand (checked in the batch's
        // own capture: scan once, project forever, never against a build the artifact didn't come from).
        var (toks, demand, echoSrc, xerr) = Artifacts.ExpandListInput(formids, "formids");
        if (xerr is not null) return xerr;
        formids = toks!;

        var toFile = to_file?.Trim();
        bool wantFile = !string.IsNullOrEmpty(toFile);
        if (wantFile)
        {
            if (Artifacts.ValidateToFile(toFile!) is { } verr) return verr;
            if (conflict_tree) return "error: to_file= writes the result as JSONL rows, and conflict_tree=true is a text-only diff view with no row form — drop one of the two.";
        }

        var outcomes = svc.ResolveBatch(formids, fields, conflict_tree, depth <= 0 ? 1 : depth, resolve_names, plugin?.Trim(),
                                        demand, out var artifactRefusal, out var refusalEpoch);
        if (artifactRefusal is not null)
            return json ? JsonWire.RenderError(artifactRefusal, refusalEpoch)
                        : "error: " + artifactRefusal + (refusalEpoch is not null ? $"\nepoch={refusalEpoch}" : "");

        List<KeyValuePair<string, string>> Echo()
        {
            var e = new List<KeyValuePair<string, string>> { new("formids", echoSrc ?? $"{formids.Length} inline formid(s)") };
            void Add(string k, string? v) { if (!string.IsNullOrEmpty(v)) e.Add(new(k, v!)); }
            Add("plugin", plugin?.Trim());
            Add("fields", fields is { Length: > 0 } ? string.Join(", ", fields) : null);
            if (depth > 1) Add("depth", depth.ToString());
            return e;
        }

        SpillState? spill = null;
        if (wantFile)
        {
            var (s, aerr) = Artifacts.WriteBatch(outcomes, toFile!, "to_file", Echo());
            if (aerr is not null)
                // Post-scan failure keeps the format contract (review finding 4) — never bare text under json.
                return json ? JsonWire.RenderError(aerr, outcomes.FirstOrDefault(o => o.Epoch is not null)?.Epoch) : "error: " + aerr;
            spill = SpillState.Spilled(s!, manifestOnly: true);
        }

        string Render(SpillState? sp, out bool trunc) => json
            ? JsonWire.RenderBatch(outcomes, max_chars, sp, out trunc)
            : Wire.RenderBatch(svc, outcomes, fields, conflict_tree, max_chars, sp, out trunc);
        var rendered = Render(spill, out var truncated);
        if (spill is null && truncated)
        {
            // AUTO-SPILL (§2.1.1): the complete batch goes to the server results dir; the response re-renders
            // with the spilled marker in-band. See cross_plugin_query's twin for the contract notes.
            if (conflict_tree)
                // WriteBatch rows carry no conflict tree — same no-row-form honesty as the cross-query twin
                // (review finding 1).
                rendered = Render(SpillState.NoRowForm(), out _);
            else
            {
                var path = ResultsStore.NextPath("housecarl_batch_record_detail", outcomes.FirstOrDefault(o => o.Epoch is not null)?.Epoch ?? "none");
                var (s, aerr) = Artifacts.WriteBatch(outcomes, path, "ceiling", Echo());
                if (aerr is not null) ResultsStore.Release(path);
                rendered = Render(aerr is null ? SpillState.Spilled(s!, manifestOnly: false) : SpillState.WriteFailed(aerr), out _);
            }
        }
        return rendered;
    });

    [McpServerTool(Name = "housecarl_diff_record", ReadOnly = true, Title = "Diff two plugins' versions of a record"),
     Description(
         "Field-level diff between TWO plugins' versions of ONE record — plugin_a vs plugin_b. Each plugin may be an " +
         "ACTIVE plugin OR a plugin FILE on disk that isn't in the load order (e.g. a DISABLED old patch): the classic " +
         "use is diffing a disabled OLD patch against the mod that supersedes it, to see exactly what changed. Both " +
         "sides are deep-read and compared by the SAME content-keyed (list reorders flagged), truncation-honest engine the conflict tree " +
         "uses; each delta line shows plugin_a's value with plugin_b's (the reference) value labeled by plugin_b's " +
         "filename. A FormID is 'XXXXXX:Plugin.esp'. Read-only. Unlike housecarl_read_record conflict_tree (which diffs " +
         "every toucher against the load-order WINNER), this compares TWO explicit plugins with no winner pole — use it " +
         "when neither side is the winner (an off-order file), or to compare two specific overrides directly. On a " +
         "TRUNCATED deep read it reports the truncation rather than claiming 'identical' (Q3).")]
    public static string DiffRecord(
        LoadOrderService svc,
        [Description("The record's FormID as 'XXXXXX:Plugin.esp' — the record whose two versions to compare.")]
            string formid,
        [Description("The FIRST plugin whose version to compare — a filename (e.g. 'OldPatch.esp'); an ACTIVE plugin OR a file on disk not in the load order.")]
            string plugin_a,
        [Description("The SECOND plugin whose version to compare — the REFERENCE side (each delta labels this plugin's value by its filename). A filename, active OR on disk.")]
            string plugin_b,
        [Description("Optional. Dotted field paths to compare (e.g. 'BasicStats.Damage', 'Keywords'); omit to diff every modeled field (deep). BOTH sides read the SAME paths so the comparison is apples-to-apples.")]
            string[]? fields = null,
        [Description("Optional. 'text' (default) or 'json' — a machine-readable {formid, a:{plugin,where,type,editorid}, b:{…}, complete, deltas[], delta_count, agreed_count} document. Deltas are the SAME strings text emits.")]
            string? format = null,
        [Description("Optional. Disambiguate plugin_a when its filename lives in more than one mod folder on disk (the mod-folder name) — or omit and pass an exact path in plugin_a.")]
            string? mod_a = null,
        [Description("Optional. Disambiguate plugin_b (see mod_a).")]
            string? mod_b = null,
        [Description("Optional. Max characters before the delta list is cut with an explicit notice (never silent). 0 = the server default.")]
            int max_chars = 0) => Guard.Tool("housecarl_diff_record", () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        bool json = Wire.WantsJson(format, out var ferr);
        if (ferr is not null) return ferr;
        var outcome = svc.DiffRecord(formid, plugin_a, plugin_b, fields, mod_a?.Trim(), mod_b?.Trim());
        return json ? JsonWire.RenderDiffRecord(outcome, max_chars) : Wire.RenderDiffRecord(outcome, max_chars);
    });

    [McpServerTool(Name = "housecarl_cross_plugin_query", ReadOnly = true, Title = "Query records across the load order"),
     Description(
         "Find records across the whole load order matching a filter — returns matches only, each as a compact " +
         "summary line (FormID, type, editorid, winner, override depth). Filters (combine freely): type= a record " +
         "signature ('WEAP') or catalog name ('Weapon'); conflicts_only=true for records >1 plugin touches; " +
         "editorid_contains= a substring of the EditorID; references= one or more FormIDs the record points at " +
         "(reverse lookup, e.g. 'what uses this keyword' — OR over the list, and each match shows which target(s) it " +
         "hit); where= filters by a field's VALUE (e.g. 'MagicSkill = Destruction', " +
         "'BasicStats.Damage >= 50' — any scalar field, ANDed; under a plugins= scope pass where_source=winner to " +
         "decide the match on the live load-order WINNER instead of the scoped plugin's own body); plugins= limits the scan to records those plugins " +
         "touch (a bare plugins= is 'everything this plugin touches'); defined_in=true narrows a plugins= scope to " +
         "records DEFINED in those plugins (not overrides they merely touch). At least one filter or plugins= is " +
         "required. editorid_contains/references/where " +
         "are body scans and MUST be combined with type= or plugins= to bound the work (conflicts_only= alone is not " +
         "enough). Pass fields= " +
         "or conflict_tree=true to expand each match from a summary line to full detail; or group_by= " +
         "(winner|type|defined_in) for a count table over ALL matches instead of per-match lines. Results cap at " +
         "limit= matches and max_chars; both overruns are reported explicitly (never silent), and offset= pages a big " +
         "enumeration in exact windows (offset=0/500/1000… with format='dense' for the compact columnar rows). " +
         "Does NOT modify anything.")]
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
        [Description("Optional. Field-VALUE predicates, each \"<path> <op> <value>\" — e.g. 'MagicSkill = Destruction', 'BasicStats.Damage >= 50', 'Archetype.ActorValue = Infamy'. Operators: = != > >= < <= (>/< numeric), contains (case-insensitive substring), has (bitwise flag/bit set-test, e.g. 'BodyTemplate.FirstPersonFlags has Body'), and the no-value PRESENCE tests exists / missing ('VirtualMachineAdapter exists' lists records that CARRY a script/substruct/non-empty list; missing is its complement); multiple are ANDed. IDENTITY membership: 'formid in <list>' / 'formid not in <list>' keep/drop records BY FormID against a supplied list — the list is inline comma-separated ('formid not in [XXXXXX:A.esp, YYYYYY:B.esp]' — commas separate, spaces in plugin names are fine, brackets/quotes optional so a pasted JSON array works) or a file via '@' + ABSOLUTE path ('formid not in @C:\\work\\claimed.txt', FormIDs comma- or newline-separated). The reconciliation subtraction: 'every record of these types in plugin X minus the ~1,200 already claimed' is type= + plugins= + where=[\"formid not in @file\"]. The value ops filter on ANY scalar field the read tools can read (any type, any depth); exists/missing also match a carried substruct/list. A body scan — MUST be combined with type= or plugins=. A wrong or container/list path is reported, never a silent '0 matches'. UNION-ARM tip: when a field can be one of several shapes (e.g. an NPC's Configuration.Level is EITHER a fixed level OR a PC-level multiplier), a scalar predicate on one arm's sub-field doubles as an ARM-PRESENCE test — only records whose live arm actually carries that sub-field can match; records on a different arm report no value and drop out. So where=[\"Configuration.Level.LevelMult >= 0\"] returns exactly the NPCs still on a PC-level multiplier (a one-call way to list which records are on a given arm). SOURCE: under a plugins= scope this predicate reads each match's SCOPED body by default (the ORIGINAL arm) — pass where_source=winner to test the LIVE load-order winner's arm instead (the post-patch 'which winners are STILL on the multiplier' answer, #233).")]
            string[]? where = null,
        [Description("Optional. Aggregate matches into a count table (sorted desc) instead of listing them: 'winner' (by load-order-winning plugin), 'type' (by record type — needs type= or plugins=), or 'defined_in' (by defining plugin). Counts ALL matches (not capped by limit=). Cannot combine with fields= or conflict_tree=.")]
            string? group_by = null,
        [Description("Optional. Dotted field paths to show for each match (e.g. 'BasicStats.Damage'). Omit for a one-line summary per match. Pair with depth= to expand list/dict contents (fields=['Effects'], depth=4 shows every effect's Data in the scan — no hand-written 'Effects[0].Data.Magnitude' index guessing).")]
            string[]? fields = null,
        [Description("Optional. With fields= (or conflict_tree=true's whole-record dump): expansion depth for list/dict/substruct CONTENTS (#231; default 1 = a container shown as just a count like '[list: 3 item(s)]'), same semantics as housecarl_read_record / housecarl_batch_record_detail — depth=2 enumerates each element with index + identity, higher opens deeper (fields=['Effects'], depth=4 reaches every effect's Magnitude/Area/Duration). Applies to EVERY match in the scan. Refused loud on a surface with nothing to expand: bare summary lines (no fields=/conflict_tree) and group_by= (a count table has no field values). Not carried in format='dense' (columnar cells align 1:1 with the requested paths) — use format=text/json for depth expansion.")]
            int depth = 1,
        [Description("When true, include each match's touching-plugin list (winner last) + winner-relative field diff.")]
            bool conflict_tree = false,
        [Description("When true (with fields=), annotate every FormLink field value with its target's identity (→ editorid \"Name\"), resolved against the load order and cached across all matches. Display-only; the token is unchanged. No effect on summary lines or group_by (there are no field tokens to annotate).")]
            bool resolve_names = false,
        [Description("DISPLAY control (with fields= under a plugins= scope): when true, expand each match's fields from the load-order WINNER's body instead of the scoped plugin's OWN version. WITHOUT this, plugins=-scoped fields are that plugin's values (e.g. a defining esp's AR 38), NOT the live winner (AR 200) — a note names the source either way. No effect under type= scope (already the winner). This governs what is SHOWN, not what MATCHES — to filter on the winner, use where_source=winner (the two compose: where_source=winner + winner_fields=false matches on the winner but shows the scoped origin body).")]
            bool winner_fields = false,
        [Description("Optional. Which BODY the body filters (where=, references=, editorid_contains=) decide the MATCH on: 'scoped' (default) = the body the scan streams (the scoped plugin's OWN under plugins=, else the winner); 'winner' = the live load-order WINNER regardless of scan scope. THE FIX for #233: under a plugins= scope, where=['Configuration.Level.LevelMult >= 0'] with the default source matches records whose SCOPED body ever had a PC-level multiplier (259), while where_source=winner matches only those whose LIVE winner still does (82) — the post-patch audit answer. It retargets the MATCH; winner_fields= independently governs DISPLAY. Requires a body filter (refused loud otherwise). Redundant under a type=-only scope (that scan already reads the winner) — accepted with a note, not refused.")]
            string? where_source = null,
        [Description("Optional. 'text' (default), 'json' (a machine-readable document — group_by count table, detail record objects with fields, or summary rows), or 'dense' (#223 — COLUMNAR json: a columns array once, then ONE positional row array per match [formid, editorid, field values…] — plus a source column under a plugins= scope naming the body each row read — no per-field envelopes or repeated keys — the compact form for bulk enumeration; ~same data at a fraction of the characters; under group_by it renders the same count table as 'json'). All formats carry total/capped/notes/truncated accounting in-band. conflict_tree is a text-only diff view.")]
            string? format = null,
        [Description("Optional. Max matches to return (default 500). The TRUE total is always reported; over the cap it says 'showing first N'. Page with offset=.")]
            int limit = 500,
        [Description("Optional. Skip the first N post-filter matches before returning rows (#223 pagination) — combine with limit= to page a big enumeration in windows (offset=0/500/1000…). Scan order is deterministic while the load order is unchanged, so windows tile exactly. The true total always counts ALL matches. Not valid with group_by= (a count table has no window). Every response carries epoch=<hex> in-band — the identity of the load-order build it was answered from: windows tile ONLY within one epoch, so if two pages' epochs differ, the load order changed mid-pagination and the pages must not be stitched (re-run from offset=0).")]
            int offset = 0,
        [Description("Optional. Max characters before the response stops with an explicit notice. 0 = the server default (~80k).")]
            int max_chars = 0,
        [Description("Optional. Write the COMPLETE result to this ABSOLUTE .jsonl path as a §2.1.1 artifact (line 1 = a manifest carrying the query echo, row count/schema, and the epoch fingerprint; then one JSON row per match) and render only the manifest inline. The artifact is never windowed: limit=/offset= do not apply to it (offset= is refused with to_file=). Re-enter it later via where=[\"formid in @<path>\"] — epoch-checked against the then-current build. Not combinable with conflict_tree (a text-only view with no row form).")]
            string? to_file = null) => Guard.Tool("housecarl_cross_plugin_query", () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        var fmt = Wire.CrossQueryFormat(format, out var ferr);
        if (ferr is not null) return ferr;
        if (fmt is not Wire.QueryFormat.Text && conflict_tree) return $"error: conflict_tree=true is a text-only diff view and is not carried in {(fmt is Wire.QueryFormat.Json ? "json" : "dense")} mode — use format=text for the conflict tree, or drop conflict_tree for the field data.";
        if (group_by is not null && ((fields is { Length: > 0 }) || conflict_tree))
            return "error: group_by aggregates matches into a count table and cannot be combined with fields= or conflict_tree=true (those expand each match to full detail — pick one). Drop fields=/conflict_tree, or drop group_by.";
        if (depth <= 0) depth = 1;
        if (depth > 1 && group_by is not null)
            return "error: depth= expands per-match field contents, and group_by= renders a count table with no field values — depth= never applies there. Drop depth= (or drop group_by= and pass fields= for per-match detail).";
        if (depth > 1 && fields is not { Length: > 0 } && !conflict_tree)
            return "error: depth= expands the list/dict contents of fields= paths, and no fields= was passed — summary lines have nothing to expand. Pass fields= (e.g. fields=['Effects'], depth=4) or conflict_tree=true (the whole-record dump), or drop depth=.";
        if (depth > 1 && fmt is Wire.QueryFormat.Dense)
            return "error: depth>1 is not carried in format='dense' — dense rows are positional (one cell per requested fields= path), and depth expansion emits extra sub-paths that would break the column alignment. Use format=text or format=json for depth expansion, or drop depth= for the dense summary cells.";
        // references= may itself be an @file / @artifact list (the §5.1 @file convention) — expanded BEFORE the
        // FormKey parse; an artifact target contributes its epoch demand, checked inside the scan's own capture.
        HousecarlCore.ArtifactDemand? refDemand = null;
        string? refEcho = null;
        if (references is { Length: > 0 })
        {
            var (toks, demand, echoSrc, xerr) = Artifacts.ExpandListInput(references, "references");
            if (xerr is not null) return xerr;
            references = toks!; refDemand = demand; refEcho = echoSrc;
        }
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

        // to_file= (§2.1.1): validated BEFORE the scan — a doomed disposition must not pay a scan first.
        var toFile = to_file?.Trim();
        bool wantFile = !string.IsNullOrEmpty(toFile);
        if (wantFile)
        {
            if (Artifacts.ValidateToFile(toFile!) is { } verr) return verr;
            if (conflict_tree) return "error: to_file= writes the result as JSONL rows, and conflict_tree=true is a text-only diff view with no row form — drop one of the two.";
            if (offset > 0) return "error: to_file= captures the COMPLETE result (the artifact is never a window), so offset= has nothing to page — drop offset=.";
        }

        var outcome = svc.CrossQuery(type, refFks, editorid_contains, conflicts_only, plugins, where,
                                     wantFile ? int.MaxValue : (limit <= 0 ? 500 : limit),   // to_file: the artifact is the FULL result, never limit-windowed
                                     defined_in, group_by, offset, where_source,
                                     refDemand is null ? null : new[] { refDemand });

        // The query echo the manifest carries — what produced this artifact, readable without this conversation.
        List<KeyValuePair<string, string>> Echo()
        {
            var e = new List<KeyValuePair<string, string>>();
            void Add(string k, string? v) { if (!string.IsNullOrEmpty(v)) e.Add(new(k, v!)); }
            Add("type", type);
            Add("references", refEcho ?? (references is { Length: > 0 } ? string.Join(", ", references) : null));
            Add("editorid_contains", editorid_contains);
            if (conflicts_only) Add("conflicts_only", "true");
            Add("plugins", plugins is { Length: > 0 } ? string.Join(", ", plugins) : null);
            if (defined_in) Add("defined_in", "true");
            Add("where", where is { Length: > 0 } ? string.Join(" AND ", where) : null);
            Add("group_by", group_by);
            Add("fields", fields is { Length: > 0 } ? string.Join(", ", fields) : null);
            if (depth > 1) Add("depth", depth.ToString());
            if (winner_fields) Add("winner_fields", "true");
            Add("where_source", where_source);
            return e;
        }

        SpillState? spill = null;
        if (wantFile && outcome.Error is null)
        {
            var (s, aerr) = Artifacts.WriteCrossQuery(svc, outcome, fields, resolve_names, winner_fields, depth, toFile!, "to_file", Echo());
            if (aerr is not null)
                // A POST-scan failure must keep the format contract — a bare text "error:" under format=json/dense
                // hands a json consumer a non-document (review finding 4).
                return fmt is Wire.QueryFormat.Text ? "error: " + aerr : JsonWire.RenderError(aerr, outcome.Epoch);
            spill = SpillState.Spilled(s!, manifestOnly: true);
        }

        // dense + group_by: the count table is already columnar — render it exactly as json (documented on format=),
        // so dense is never a refusal there and the two renders can't drift.
        string Render(SpillState? sp, out bool trunc) => fmt switch
        {
            Wire.QueryFormat.Dense when group_by is null => JsonWire.RenderCrossQueryDense(svc, outcome, fields, max_chars, resolve_names, winner_fields, sp, out trunc),
            Wire.QueryFormat.Dense or Wire.QueryFormat.Json => JsonWire.RenderCrossQuery(svc, outcome, fields, max_chars, resolve_names, winner_fields, depth, sp, out trunc),
            _ => Wire.RenderCrossQuery(svc, outcome, fields, conflict_tree, max_chars, resolve_names, winner_fields, depth, sp, out trunc),
        };
        var rendered = Render(spill, out var truncated);
        if (spill is null && truncated && outcome.Error is null)
        {
            // AUTO-SPILL (§2.1.1, decided over refuse-and-redirect): the inline render hit max_chars, so the
            // complete requested window goes to the server results dir and the response re-renders with the
            // spilled marker IN-BAND (both formats). The rows are re-filled off the SAME pinned view the scan
            // stamped, so the file cannot mix builds the header didn't claim. A failed write re-renders with the
            // failure named — a truncated response silently missing its promised artifact is the Q3 case.
            if (conflict_tree)
                // No row form for the trees (to_file refuses on the same ground) — spilling thinner tree-less rows
                // under a completeness claim would silently substitute the shape (review finding 1). Say so instead.
                rendered = Render(SpillState.NoRowForm(), out _);
            else
            {
                var path = ResultsStore.NextPath("housecarl_cross_plugin_query", outcome.Epoch ?? "none");
                var (s, aerr) = Artifacts.WriteCrossQuery(svc, outcome, fields, resolve_names, winner_fields, depth, path, "ceiling", Echo());
                if (aerr is not null) ResultsStore.Release(path);   // don't leave the empty reservation behind
                rendered = Render(aerr is null ? SpillState.Spilled(s!, manifestOnly: false) : SpillState.WriteFailed(aerr), out _);
            }
        }
        return rendered;
    });

    [McpServerTool(Name = "housecarl_resolve", ReadOnly = true, Title = "Resolve FormIDs to their identity"),
     Description(
         "Turn a batch of FormIDs into their load-order identity — for EACH: type, editorid, display name, and " +
         "winning plugin — in ONE call. The bulk name-resolution primitive: where housecarl_batch_record_detail frames " +
         "every record (fields, override depth, per-record header), this returns one compact identity line (or JSON " +
         "row) per FormID and nothing else — the cheap way to label a list of material/perk/keyword FormIDs. Resolved " +
         "in order; a bad or absent FormID yields a per-item error without failing the batch (never a silent drop — " +
         "Q3). Winners only (the load-order-effective identity of each target). The engine-implicit forms (PlayerRef " +
         "000014:Skyrim.esm / Player 000007:Skyrim.esm) resolve to their hardcoded identity with winner '<engine>' — " +
         "no plugin defines them, but they are real, never dangling. Deliberately minimal: no fields=, no " +
         "depth, no conflict_tree — for those use housecarl_batch_record_detail. The header carries epoch=<hex> — " +
         "the load-order build identity the whole batch resolved against (differs across calls only when the order " +
         "changed between them). Does NOT modify anything.")]
    public static string Resolve(
        LoadOrderService svc,
        [Description("The FormIDs to resolve, each 'XXXXXX:Plugin.esp'. Resolved in order; results are returned in the same order.")]
            string[] formids,
        [Description("Optional. 'text' (default) — one compact identity line per FormID — or 'json' for a machine-readable document (one {formid,type,editorid,name,winner} row per input; a bad/absent input carries {formid,error}).")]
            string? format = null,
        [Description("Optional. Max characters before the response stops with an explicit notice (text) or drops trailing rows with truncated=true (json). 0 = the server default (~80k).")]
            int max_chars = 0,
        [Description("Optional. Write the COMPLETE identity table to this ABSOLUTE .jsonl path as a §2.1.1 artifact (line 1 = manifest with the epoch fingerprint; then one identity row per input, per-item errors included) and render only the manifest inline. Re-enter it later via formids=[\"@<path>\"] — epoch-checked against the then-current build.")]
            string? to_file = null) => Guard.Tool("housecarl_resolve", () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        if (formids is null || formids.Length == 0) return "error: formids is empty. Pass one or more 'XXXXXX:Plugin.esp' FormIDs.";
        bool json = Wire.WantsJson(format, out var ferr);
        if (ferr is not null) return ferr;

        // formids= under the @file convention — see batch_record_detail's twin.
        var (toks, demand, echoSrc, xerr) = Artifacts.ExpandListInput(formids, "formids");
        if (xerr is not null) return xerr;
        formids = toks!;

        var toFile = to_file?.Trim();
        bool wantFile = !string.IsNullOrEmpty(toFile);
        if (wantFile && Artifacts.ValidateToFile(toFile!) is { } verr) return verr;

        var rows = svc.ResolveRefs(formids, demand, out var epoch, out var artifactRefusal);
        if (artifactRefusal is not null)
            return json ? JsonWire.RenderError(artifactRefusal, epoch)
                        : "error: " + artifactRefusal + $"\nepoch={epoch}";

        var echo = new List<KeyValuePair<string, string>> { new("formids", echoSrc ?? $"{formids.Length} inline formid(s)") };
        SpillState? spill = null;
        if (wantFile)
        {
            var (s, aerr) = Artifacts.WriteResolve(rows, epoch, toFile!, "to_file", echo);
            if (aerr is not null)
                // Post-scan failure keeps the format contract (review finding 4).
                return json ? JsonWire.RenderError(aerr, epoch) : "error: " + aerr;
            spill = SpillState.Spilled(s!, manifestOnly: true);
        }

        string Render(SpillState? sp, out bool trunc) => json
            ? JsonWire.RenderResolve(rows, max_chars, epoch, sp, out trunc)
            : Wire.RenderResolve(rows, max_chars, epoch, sp, out trunc);
        var rendered = Render(spill, out var truncated);
        if (spill is null && truncated)
        {
            // AUTO-SPILL (§2.1.1) — see cross_plugin_query's twin for the contract notes.
            var path = ResultsStore.NextPath("housecarl_resolve", epoch);
            var (s, aerr) = Artifacts.WriteResolve(rows, epoch, path, "ceiling", echo);
            if (aerr is not null) ResultsStore.Release(path);
            rendered = Render(aerr is null ? SpillState.Spilled(s!, manifestOnly: false) : SpillState.WriteFailed(aerr), out _);
        }
        return rendered;
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
         "field left null (a null FormLink is a legal optional, not an error), does NOT list unused-master cleanup " +
         "(a FormLink scan cannot prove a master is unused), and does NOT link-check an owned item's ownership 'variable' " +
         "word (a rank/global Mutagen cannot type on an override without a link cache). NARROWING: beyond plugins= it takes " +
         "a record scope (type= / formids= / editorid_contains=), a findings= class filter, counts_only=true for just the " +
         "totals plus dangling-by-TARGET-plugin and dangling-by-SOURCE-plugin histograms, and format='json'. Narrowing narrows " +
         "the COUNTS too — they are always the counts for the scope actually swept, and the response says so. BASELINE: the " +
         "base-game masters carry permanent vanilla dangling refs no load order can fix, so the response splits them out of the " +
         "total and spends limit= on every other plugin FIRST — vanilla can no longer crowd mod findings out of the listing. " +
         "Results cap at limit= and max_chars (both overruns explicit); a capped listing NAMES which source plugins lost entries.")]
    public static string CheckErrorsTool(
        LoadOrderService svc,
        [Description("Optional. Plugin filenames to check (e.g. 'MyMod.esp'). A name not in the active order is resolved on disk (a fresh houseCARL patch, a disabled mod) and swept OFF-ORDER; found nowhere (or in several folders) it is an error. Omit to sweep the WHOLE active order (every non-excluded plugin) — thorough but heavier; scope to one plugin for a fast, focused check like the CK's per-plugin 'Check For Errors'.")]
            string[]? plugins = null,
        [Description("Optional. Record type to sweep — a 4-char signature ('WEAP') or catalog name ('Weapon'). Applied at the record STREAM, so it is the CHEAPEST scope: skipped records cost nothing. An unknown type is refused, naming what is expected.")]
            string? type = null,
        [Description("Optional. Sweep ONLY these records ('0BCC84:Skyrim.esm', …) — the re-check-these-few pass after a fix. A malformed token refuses the call before the sweep runs.")]
            string[]? formids = null,
        [Description("Optional. Sweep only records whose EditorID contains this substring (case-insensitive). A record with no EditorID never matches.")]
            string? editorid_contains = null,
        [Description("Optional. Which error classes to look for: 'dangling' and/or 'missing_masters' (default both). Excluding 'dangling' SKIPS the per-record link walk entirely — that is how you ask 'is any master missing anywhere in my order' without paying for a full sweep. An excluded class renders as 'not checked', never as 0. Unscannable records and scan errors are ALWAYS reported and cannot be filtered out (a suppressed 'could not read' would read as clean).")]
            string[]? findings = null,
        [Description("Optional. true = return ONLY the header totals plus two histograms, with no per-plugin listing: dangling-by-TARGET-plugin (which plugin the broken refs point INTO — the one absent dependency behind a wall of findings) and dangling-by-SOURCE-plugin (which plugin they come FROM — how much of the total is vanilla baseline and how much your mods introduced). The cheap before/after-a-fix comparison; totals stay exact (never limit-capped) and limit= caps the histogram ROWS instead.")]
            bool counts_only = false,
        [Description("Optional. 'text' (default) or 'json' — the machine-readable twin carrying the same data, with the totals/capped/truncated accounting in-band.")]
            string? format = null,
        [Description("Optional. Max dangling references to list across the whole sweep (default 1000). The TRUE total is always reported; over the cap it says so, says how many plugins lost entries, names the ones that lost the most (a count each), and states how many it did not name — and the omissions list is never itself capped by limit=. Spent on every other plugin before the base-game masters, so a large order's vanilla baseline cannot consume it before your mods are reached. Master-table findings are always listed in full (they are few). Under counts_only=true this caps the histogram ROWS instead.")]
            int limit = 1000,
        [Description("Optional. Max characters before the response stops with an explicit notice. 0 = the server default (~80k).")]
            int max_chars = 0) => Guard.Tool("housecarl_check_errors", () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        bool json = Wire.WantsJson(format, out var fmtErr);
        if (fmtErr is not null) return fmtErr;
        int lim = limit <= 0 ? 1000 : limit;
        var result = svc.CheckErrors(plugins, lim, formids, editorid_contains, type, findings, counts_only);
        return json ? JsonWire.RenderCheckErrors(result, max_chars, lim)
                    : Wire.RenderCheckErrors(result, max_chars, lim);
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
         "reported UNVERIFIABLE, never passed clean. NARROWING: beyond plugins= it takes a record scope (type= / formids= / " +
         "editorid_contains=), property_contains=, a findings= class filter, counts_only=true for just the totals plus an " +
         "unbound-by-PROPERTY-NAME histogram, and format='json' — a script-heavy plugin (~180 scripted records) does not " +
         "fit a tool result unnarrowed, and limit= alone will not help because it caps FINDINGS, not the record roster. " +
         "Narrowing narrows the COUNTS too — they are always the counts for the scope actually swept, and the response " +
         "says so. Results cap at limit= and max_chars (both overruns explicit).")]
    public static string ValidateScriptsTool(
        LoadOrderService svc,
        [Description("Optional. Plugin filenames to check (e.g. 'MyMod.esp'). A name not in the load order is an error. Omit to sweep the WHOLE active order (every scripted record in every non-excluded plugin) — thorough but heavier; scope to one plugin for a fast, focused check.")]
            string[]? plugins = null,
        [Description("Optional. Record type to sweep — a 4-char signature ('QUST') or catalog name ('Quest'). Applied at the record STREAM, so it is the CHEAPEST scope: skipped records never have their .pex chain read. An unknown type is refused, naming what is expected.")]
            string? type = null,
        [Description("Optional. Sweep ONLY these records ('0BCC84:Skyrim.esm', …) — the re-check-these-few pass after editing a script's properties, which is what limit= cannot do. A malformed token refuses the call before the sweep runs.")]
            string[]? formids = null,
        [Description("Optional. Sweep only records whose EditorID contains this substring (case-insensitive). A record with no EditorID never matches.")]
            string? editorid_contains = null,
        [Description("Optional. Report only findings whose PROPERTY NAME contains this substring (case-insensitive) — chasing one property across a plugin. A record left with no matching finding drops out of the listing entirely.")]
            string? property_contains = null,
        [Description("Optional. Which finding classes to report, in severity order: 'unbound_object' (HIGH — the silent-None footgun), 'unbound_scalar' (MEDIUM), 'unbound' (both), 'bound_null' (advisory). Default all. Filtering to 'unbound_object' is the way to cut the record roster down to the records that actually matter. UNVERIFIABLE attachments are ALWAYS reported and cannot be filtered out — a script whose .pex could not be read might be the one declaring the property you are filtering for, so suppressing it would manufacture a clean answer.")]
            string[]? findings = null,
        [Description("Optional. true = return ONLY the header totals plus an unbound-by-PROPERTY-NAME histogram, with no per-record listing — the cheap before/after comparison for a multi-pass script edit ('did the unbound count for this property drop, and did anything new appear'). Totals stay exact (never limit-capped) and limit= caps the histogram ROWS instead.")]
            bool counts_only = false,
        [Description("Optional. 'text' (default) or 'json' — the machine-readable twin carrying the same data, with the totals/capped/truncated accounting in-band.")]
            string? format = null,
        [Description("Optional. Max property findings (unbound + bound-but-null) to list across the whole sweep (default 1000). The TRUE totals are always reported; over the cap it says so. Unverifiable notes are always listed in full (they are few). Under counts_only=true this caps the histogram ROWS instead.")]
            int limit = 1000,
        [Description("Optional. Max characters before the response stops with an explicit notice. 0 = the server default (~80k).")]
            int max_chars = 0) => Guard.Tool("housecarl_validate_scripts", () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        bool json = Wire.WantsJson(format, out var fmtErr);
        if (fmtErr is not null) return fmtErr;
        int lim = limit <= 0 ? 1000 : limit;
        var result = svc.ValidateScripts(plugins, lim, formids, editorid_contains, type, property_contains, findings, counts_only);
        return json ? JsonWire.RenderScriptCheck(result, max_chars, lim)
                    : Wire.RenderScriptCheck(result, max_chars, lim);
    });

    [McpServerTool(Name = "housecarl_read_plugin_file", ReadOnly = true, Title = "Read a plugin file directly (active or not)"),
     Description(
         "Read ONE plugin file straight off disk — INCLUDING a plugin DISABLED in MO2 — returning THAT FILE's own " +
         "version of a record, NOT the load-order winner. Where housecarl_read_record resolves the ACTIVE order, this " +
         "reaches an inactive/arbitrary plugin: give it a filename (located even inside a DISABLED mod folder) or an " +
         "absolute path. Modes: formid= reads one record's fields (compact `path = token`, same format as read_record); " +
         "type= enumerates the records of that type the file defines/overrides; neither returns a record-type summary " +
         "(what's in the file). EVERY result is labeled OUT-OF-LOAD-ORDER — the read did not go through load-order " +
         "resolution; whether the game loads the file is reported separately, per file, with its reason. It emits " +
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
        [Description("Optional. With formid=: annotate every FormLink field value with its target's identity (→ editorid \"Name\"), resolved against the ACTIVE load order (the only identity frame — this file may itself be inactive). Display-only; the token is unchanged. A target the active order doesn't define is marked 'unresolved' — except the engine-implicit forms (PlayerRef 000014 / Player 000007), which annotate their hardcoded identity. Forces the load-order build (opt-in), unlike the default cheap raw read.")]
            bool resolve_names = false,
        [Description("Optional. 'text' (default) or 'json' — a machine-readable document (always stamped out_of_load_order:true; the file's masters context, then the record/records/type_counts payload). Field values are the SAME tokens as text.")]
            string? format = null,
        [Description("Optional. Max characters before the response stops with an explicit notice. 0 = the server default (~80k).")]
            int max_chars = 0) => Guard.Tool("housecarl_read_plugin_file", () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        bool json = Wire.WantsJson(format, out var ferr);
        if (ferr is not null) return ferr;
        var outcome = svc.ReadPluginFile(plugin, formid, type, mod, fields, depth <= 0 ? 1 : depth, editorid_contains, limit <= 0 ? 500 : limit, resolve_names);
        return json ? JsonWire.RenderPluginFile(outcome, max_chars) : Wire.RenderPluginFile(outcome, max_chars);
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

    /// <summary>How many distinct contested parent HOSTS a create render names before it says "and N further"
    /// (#300). Lives here, shared by the text render and its json twin, because the two ARE the same bound and a
    /// second literal is how they drift: the text side was capped on review [medium] and the json side was not,
    /// so a bulk_create fanning children into many contested cells wrote ~700 bytes per host AHEAD of the budgeted
    /// `created` array and truncated it at "0 of N" — the HCBR-2026-06-28-01 shape the text cap exists to prevent
    /// (PR #323 review [medium]). Both lanes still publish the FULL distinct count beside the capped list, so a
    /// cut caller knows how many it is not seeing.</summary>
    public const int ContestedHostsShown = 10;

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

    /// <summary>The cross_plugin_query format vocabulary — the one tool with a third format (<c>dense</c>, the #223
    /// columnar render). Every other tool stays on the two-value <see cref="WantsJson"/>.</summary>
    internal enum QueryFormat { Text, Json, Dense }

    /// <summary>Parse cross_plugin_query's <c>format=</c>: text (default) / json / dense; anything else is a named
    /// refusal (Q3) listing all three.</summary>
    internal static QueryFormat CrossQueryFormat(string? format, out string? error)
    {
        error = null;
        var f = format?.Trim();
        if (string.IsNullOrEmpty(f) || f.Equals("text", StringComparison.OrdinalIgnoreCase)) return QueryFormat.Text;
        if (f.Equals("json", StringComparison.OrdinalIgnoreCase)) return QueryFormat.Json;
        if (f.Equals("dense", StringComparison.OrdinalIgnoreCase)) return QueryFormat.Dense;
        error = $"error: format='{format}' is not recognized — use 'text' (the default), 'json', or 'dense'.";
        return QueryFormat.Text;
    }

    // ---- housecarl_diff_record (P8c) ----------------------------------------------------------------
    /// <summary>Render a pairwise record diff (housecarl_diff_record — P8c): a header naming both poles (plugin + where
    /// found + record identity), then one line per delta (plugin_a's value, reference = plugin_b), budget-bounded with an
    /// explicit cut. No deltas ⇒ "identical across the fields read" WITH the agreed-leaf count — UNLESS the deep read was
    /// TRUNCATED, in which case it says so instead of claiming identical (Q3). On refusal, a single error: line.</summary>
    public static string RenderDiffRecord(LoadOrderService.DiffRecordOutcome o, int maxChars)
    {
        if (o.Error is not null) return $"error: {o.Error}" + (o.Epoch is not null ? $"\nepoch={o.Epoch}" : "");
        int cap = Cap(maxChars);
        var a = o.A!; var b = o.B!; var d = o.Diff!;
        var sb = new StringBuilder();
        sb.Append("diff ").Append(o.Formid);
        if (o.Epoch is not null)
        {
            sb.Append("  epoch=").Append(o.Epoch);
            // The fingerprint covers the ACTIVE ORDER only — an off-order pole's file content sits outside it, so
            // equal epochs must not be read as "same inputs" across such calls (PR #305 review; the fact itself is
            // per-pole data: InOrder/Where, rendered on each pole line).
            if (!a.InOrder || !b.InOrder) sb.Append(" (active-order inputs only — the off-order pole's file is outside the fingerprint)");
        }
        sb.Append('\n')
          .Append("  a: ").Append(PoleLine(a)).Append('\n')
          .Append("  b: ").Append(PoleLine(b)).Append('\n');

        if (d.Deltas.Count == 0)
        {
            if (!d.Complete)
                sb.Append("no differing fields in what was read, but the deep read was TRUNCATED at the cap — NOT a clean 'identical' (Q3). Narrow with fields= to compare in full.\n");
            else if (d.AgreedCount > 0)
                sb.Append("identical across the fields read (").Append(d.AgreedCount).Append(" value leaf/leaves agree")
                  .Append(d.AgreedSample.Count > 0 ? ": " + string.Join(", ", d.AgreedSample) + (d.AgreedCount > d.AgreedSample.Count ? ", …" : "") : "")
                  .Append(").\n");
            else
                sb.Append("identical across the fields read (no differing fields).\n");
            return sb.ToString();
        }

        sb.Append(d.Deltas.Count).Append(d.Deltas.Count == 1 ? " difference" : " differences")
          .Append(" — each line: ").Append(a.Plugin).Append("'s value (reference = ").Append(b.Plugin).Append("):\n");
        int shown = 0;
        foreach (var delta in d.Deltas)
        {
            if (sb.Length >= cap)
            {
                sb.Append("  ... [truncated: rendered ").Append(shown).Append(" of ").Append(d.Deltas.Count)
                  .Append(" at max_chars=").Append(cap).Append("; pass fields= to narrow, or raise max_chars]\n");
                break;
            }
            sb.Append("  - ").Append(delta).Append('\n');
            shown++;
        }
        if (!d.Complete)
            sb.Append("note: the deep read was TRUNCATED — list-content and one-sided-presence deltas are SUPPRESSED (only both-sides value mismatches shown); narrow with fields= to compare those in full.\n");
        return sb.ToString();
    }

    static string PoleLine(LoadOrderService.DiffPole p) =>
        $"{p.Plugin} [{p.Where}{(p.RecordType is not null ? ", " + p.RecordType : "")}{(p.EditorId is not null ? " " + p.EditorId : "")}]";

    // ---- housecarl_resolve --------------------------------------------------------------------------
    /// <summary>Render the bulk name-resolution result (housecarl_resolve — P3): one compact identity line per input
    /// FormID (type/editorid/name/winner), or <c>error=</c> for a bad/absent input (per-item, the batch survives — Q3).
    /// Budget-bounded with the same explicit cut the other reads use.</summary>
    public static string RenderResolve(IReadOnlyList<ResolvedRef> rows, int maxChars, string epoch)
        => RenderResolve(rows, maxChars, epoch, null, out _);

    public static string RenderResolve(IReadOnlyList<ResolvedRef> rows, int maxChars, string epoch, SpillState? spill, out bool truncated)
    {
        truncated = false;
        int cap = Cap(maxChars);
        var sb = new StringBuilder();
        sb.Append("resolve: ").Append(rows.Count).Append(rows.Count == 1 ? " formid" : " formids")
          .Append("  epoch=").Append(epoch).Append('\n');
        for (int i = 0; i < rows.Count && !(spill?.ManifestOnly ?? false); i++)
        {
            if (sb.Length >= cap)
            {
                truncated = true;
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
        if (spill is not null) Artifacts.AppendSpillStateText(sb, spill);
        return sb.ToString().TrimEnd('\n');
    }

    // ---- housecarl_read_record ----------------------------------------------------------------------
    public static string RenderRecord(LoadOrderService svc, ReadOutcome o, IReadOnlyList<string>? fields, bool conflictTree, int maxChars)
    {
        // A stamped refusal renders its stamp (PR #305 review): "not present" is an answer about a build, and the
        // wire must say WHICH — the DTO carrying it while the render dropped it was the unmet half of the contract.
        if (o.Error is not null) return "error: " + o.Error + (o.Epoch is not null ? $"\nepoch={o.Epoch}" : "");
        var sb = new StringBuilder();
        // Header line, like every other text lane — and BEFORE the cap-bounded body, so a capped record never
        // overruns its budget to fit the stamp (third-round note).
        if (o.Epoch is not null) sb.Append("epoch=").Append(o.Epoch).Append('\n');   // §2.1.1: the build this read answered from
        var notes = new ChildNotes();
        AppendRecordBlock(sb, svc, o, fields, conflictTree, Cap(maxChars), notes);
        AppendOwnedChildNotes(sb, notes);
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>Which #342 clause a rendered field earns. The tier decides it: the cheap tier can only say "not
    /// read"; the precise tier says what is true of the field's SHAPE.</summary>
    internal enum ChildClause { NotRead, Collection, Singular }

    /// <summary>Which #342 clauses this response has earned, and over WHICH fields — accumulated at EMISSION, as
    /// each annotated field line is written, never where the annotation was decided.
    ///
    /// <para>That distinction is the whole fix. A response can annotate a field and then not show it: the field
    /// loop hits <c>max_chars</c> before reaching it, the json fields array truncates, a <c>to_file</c> spill sends
    /// the rows to a file. A clause committed when the annotation was DECIDED then describes a field the caller
    /// cannot see. Registering at emission makes that unrepresentable — no field line, no clause.</para>
    ///
    /// <para><see cref="May"/> is the other half, and it runs the other way: it is set BEFORE a record's fields
    /// render, so the clauses that record could still earn are RESERVED out of the caller's budget rather than
    /// appended past it. Reserve decides a clause FITS; emission decides it is STATED.</para></summary>
    internal sealed class ChildNotes
    {
        readonly SortedSet<string> _notRead = new(StringComparer.Ordinal);
        readonly SortedSet<string> _collection = new(StringComparer.Ordinal);
        readonly SortedSet<string> _singular = new(StringComparer.Ordinal);
        bool _mayNotRead, _mayCollection, _maySingular;

        /// <summary>A clause kind this response may still state — reserved from here on.</summary>
        public void May(ChildClause c)
        {
            if (c == ChildClause.NotRead) _mayNotRead = true;
            else if (c == ChildClause.Collection) _mayCollection = true;
            else _maySingular = true;
        }

        /// <summary>An annotated field line just went into the medium: this clause is now STATED, over this
        /// field.</summary>
        public void Emitted(ChildClause c, string field)
        {
            May(c);
            (c == ChildClause.NotRead ? _notRead : c == ChildClause.Collection ? _collection : _singular).Add(field);
        }

        /// <summary>The chars to hold back from <c>max_chars</c> for the clauses this response may still state.</summary>
        public int Reserve => ReadSentences.ClauseReserve(_mayNotRead, _mayCollection, _maySingular);

        internal IReadOnlyCollection<string> Fields(ChildClause c) =>
            c == ChildClause.NotRead ? _notRead : c == ChildClause.Collection ? _collection : _singular;
    }

    /// <summary>The #342 clauses, stated ONCE per response after the body — never per field, which cost ~275
    /// identical chars on every annotated row and pushed real rows out of a bulk response's budget. Each clause
    /// NAMES the fields it was earned over, so it claims nothing about where in the response they are.</summary>
    internal static void AppendOwnedChildNotes(StringBuilder sb, ChildNotes n)
    {
        foreach (var c in new[] { ChildClause.NotRead, ChildClause.Collection, ChildClause.Singular })
        {
            var fields = n.Fields(c);
            if (fields.Count == 0) continue;
            sb.Append('\n').Append(c switch
            {
                ChildClause.NotRead => ReadSentences.NotReadClause(fields),
                ChildClause.Collection => ReadSentences.MergeCollection(fields),
                _ => ReadSentences.SingleResolved(fields),
            }).Append('\n');
        }
    }

    /// <summary>Render one record, and its conflict tree when asked — fetching the tree ONCE and using it for both
    /// the diff view and the #342 precise tier, so the annotation costs no record fetch of its own. Without
    /// conflict_tree the outcome keeps the cheap index-only annotation the service already put on it.</summary>
    static void AppendRecordBlock(StringBuilder sb, LoadOrderService svc, ReadOutcome o, IReadOnlyList<string>? fields,
                                  bool conflictTree, int cap, ChildNotes notes)
    {
        var outcome = o;
        LoadOrderService.TreeFill? fill = null;
        bool precise = false;
        // A tree fetch is one whole-overlay enumeration per touching plugin, so the prefetch takes the tree render's
        // own skips with it. SOLE TOUCHER is the one that mattered: measured on a real order, hoisting the fetch
        // above `tp.Count <= 1` took a conflict_tree read of an uncontested record from ~133 ms to ~273 ms, on a
        // record type where there is nothing to diff and nothing to name. ALREADY over budget is free to check and
        // does fire on the bulk lanes, where earlier rows have filled the buffer.
        //
        // The limit, stated because it is real: this cannot foresee a cap hit DURING this record's own field
        // render. A single read with a cap so tight that the fields themselves exhaust it still pays for the tree
        // and then truncates the diff — base skipped that fetch. Closing it would mean rendering the record twice
        // or guessing its rendered size. (What it no longer costs is a WRONG SENTENCE: the clause is stated off the
        // field lines this render emitted, so a truncated annotation states nothing — Aaron's finding 1.)
        if (conflictTree && o.Pin is { } pin && o.Record is { } rec
            && o.TouchingPlugins is { Count: > 1 } && sb.Length < cap - notes.Reserve)
        {
            fill = svc.ResolveTreeFill(pin, o.FormKey, fields, o.SourcePlugin, rec.Fields.Select(f => f.Path).ToList());
            if (fill is { ByField.Count: > 0 }) { outcome = ApplyPreciseChildNotes(o, rec, fill); precise = true; }
        }
        // Hold back the clauses this record could earn BEFORE its fields render, so an annotated response answers
        // inside the caller's max_chars instead of overrunning it by up to three clauses (finding 6). Only a record
        // that CAN annotate pays: one with no child-bearing field reserves nothing.
        if (outcome.OwnedChildFields is { } annotated)
            foreach (var shape in annotated.Values) notes.May(ClauseOf(shape, precise));
        // The RESERVE is held back from the budget, never from the number the render QUOTES: a cut that told the
        // caller "at max_chars=1124" when they passed 2000 would be a wrong sentence of its own.
        AppendRecord(sb, outcome, cap, notes.Reserve, precise, notes);
        if (conflictTree) AppendConflictTree(sb, svc, outcome, fields, cap, fill?.View, notes.Reserve);
    }

    /// <summary>Which clause an annotated field earns: the cheap tier knows only that other plugins were not read,
    /// whatever the field's shape; the precise tier knows what is true of the SHAPE.</summary>
    static ChildClause ClauseOf(HousecarlCore.OwnedChildShape shape, bool precise) =>
        !precise ? ChildClause.NotRead
        : shape == HousecarlCore.OwnedChildShape.Singular ? ChildClause.Singular : ChildClause.Collection;

    /// <summary>Replace the cheap "not read" note on every child-bearing field with what the tree's bodies
    /// actually say — including replacing it with NOTHING when no other plugin declares content there, which the
    /// cheap tier could not know. The returned outcome's <see cref="ReadOutcome.OwnedChildFields"/> is rebuilt to
    /// the fields that still carry an annotation, so the render states a clause only over one it emits.</summary>
    static ReadOutcome ApplyPreciseChildNotes(ReadOutcome o, RecordFields rec, LoadOrderService.TreeFill fill)
    {
        var rebuilt = new List<FieldValue>(rec.Fields);
        var annotated = new Dictionary<string, HousecarlCore.OwnedChildShape>(StringComparer.Ordinal);
        for (int i = 0; i < rebuilt.Count; i++)
        {
            if (!fill.ByField.TryGetValue(rebuilt[i].Path, out var d)) continue;
            var note = ReadSentences.DeclarersNote(d.Shape, d.Declaring, d.Unreadable);
            rebuilt[i] = rebuilt[i] with { Display = note };
            if (note is null) continue;
            annotated[rebuilt[i].Path] = d.Shape;
        }
        return o with { Record = rec with { Fields = rebuilt }, OwnedChildFields = annotated };
    }

    // ---- housecarl_batch_record_detail --------------------------------------------------------------
    public static string RenderBatch(LoadOrderService svc, IReadOnlyList<ReadOutcome> outcomes, IReadOnlyList<string>? fields, bool conflictTree, int maxChars)
        => RenderBatch(svc, outcomes, fields, conflictTree, maxChars, null, out _);

    public static string RenderBatch(LoadOrderService svc, IReadOnlyList<ReadOutcome> outcomes, IReadOnlyList<string>? fields, bool conflictTree, int maxChars,
                                     SpillState? spill, out bool truncated)
    {
        truncated = false;
        int cap = Cap(maxChars);
        var notes = new ChildNotes();   // #342: accumulated over the rows actually rendered, not over the input list
        var sb = new StringBuilder();
        sb.Append("batch: ").Append(outcomes.Count).Append(outcomes.Count == 1 ? " record" : " records");
        // The whole batch reads ONE captured build (ResolveBatch), so its epoch is response-level accounting —
        // first non-null (a malformed-FormID row never consulted a view and carries none).
        if (outcomes.FirstOrDefault(o => o.Epoch is not null)?.Epoch is { } epoch) sb.Append("  epoch=").Append(epoch);
        sb.Append('\n');
        int rendered = 0;
        foreach (var o in outcomes)
        {
            if (spill?.ManifestOnly ?? false) break;   // to_file: only the manifest renders — the rows are the FILE
            if (sb.Length >= cap - notes.Reserve)      // the clauses this response has already earned are SPOKEN FOR
            {
                truncated = true;
                sb.Append("... [truncated: rendered ").Append(rendered).Append(" of ").Append(outcomes.Count)
                  .Append(" records before hitting max_chars=").Append(cap)
                  .Append("; request fewer formids, pass fields= to slim each, or raise max_chars]\n");
                break;
            }
            sb.Append('\n');
            if (o.Error is not null) sb.Append("error: ").Append(o.Error).Append('\n');
            else AppendRecordBlock(sb, svc, o, fields, conflictTree, cap, notes);
            rendered++;
        }
        AppendOwnedChildNotes(sb, notes);
        if (spill is not null) Artifacts.AppendSpillStateText(sb, spill);
        return sb.ToString().TrimEnd('\n');
    }

    // ---- housecarl_cross_plugin_query ---------------------------------------------------------------

    /// <summary>The container hint for the DENSE render's field cells: dense refuses depth&gt;1 (positional cells align
    /// 1:1 with the requested paths — #231), so the generic " — pass depth=2 to expand" alone would send the caller
    /// into that refusal blind. Name the format hop with the knob. Used only by
    /// <see cref="JsonWire.RenderCrossQueryDense"/>; the text/json renders take depth= directly and use the generic
    /// <see cref="HousecarlCore.ReadEngine.DepthExpandHint"/>.</summary>
    internal const string DenseContainerHint = " — pass depth=2 with format=text/json to expand (dense cells are positional)";

    public static string RenderCrossQuery(LoadOrderService svc, CrossQueryOutcome q, IReadOnlyList<string>? fields, bool conflictTree, int maxChars,
                                          bool resolveNames = false, bool winnerFields = false, int depth = 1)
        => RenderCrossQuery(svc, q, fields, conflictTree, maxChars, resolveNames, winnerFields, depth, null, out _);

    /// <summary>The §2.1.1-aware render: <paramref name="spill"/> carries the call's artifact disposition (the
    /// spilled marker / to_file manifest-only mode / failed-spill warning), and <paramref name="truncated"/> hands
    /// the row-level max_chars cut back to the tool layer — the auto-spill trigger.</summary>
    public static string RenderCrossQuery(LoadOrderService svc, CrossQueryOutcome q, IReadOnlyList<string>? fields, bool conflictTree, int maxChars,
                                          bool resolveNames, bool winnerFields, int depth, SpillState? spill, out bool truncated)
    {
        truncated = false;
        // A post-capture refusal is stamped (PR #305 contract) — e.g. the artifact epoch-mismatch refusal, which
        // consulted the build to compare against it. Pre-capture validation refusals carry null and render bare.
        if (q.Error is not null) return "error: " + q.Error + (q.Epoch is not null ? $"\nepoch={q.Epoch}" : "");
        int cap = Cap(maxChars);
        if (q.Groups is not null) return RenderCrossQueryGroups(q, cap, spill, out truncated);   // group_by= → a count table, not per-match lines
        bool detail = (fields is { Count: > 0 }) || conflictTree;          // expand matches, vs. one-line summaries
        var linkMemo = resolveNames && detail ? new Dictionary<FormKey, ResolvedRef>() : null;   // P7: one link cache across all rendered matches
        bool anyScoped = detail && q.Sources is { } ss && ss.Take(q.Keys.Count).Any(s => s is not null);   // P5: plugins= scope shows a plugin's OWN body
        var sb = new StringBuilder();
        sb.Append("cross_plugin_query: ").Append(q.Total).Append(q.Total == 1 ? " match" : " matches");
        if (q.ScopeLabel is not null) sb.Append(" DEFINED IN ").Append(q.ScopeLabel);   // P1: explicit scope — NOT the 'touches' default
        if (q.Offset > 0)                                                              // #223 pagination — name the window, and the next offset while paging
        {
            if (q.Total == 0) sb.Append(" (offset=").Append(q.Offset).Append(" had nothing to skip — NO records match at any offset; check the filter, not the paging)");
            else if (q.Keys.Count == 0) sb.Append(" (offset=").Append(q.Offset).Append(" skipped past the last match — nothing to show; lower offset=)");
            else
            {
                sb.Append(" (showing matches ").Append(q.Offset + 1).Append('–').Append(q.Offset + q.Keys.Count);
                if (q.Capped) sb.Append("; continue with offset=").Append(q.Offset + q.Keys.Count);
                sb.Append(')');
            }
        }
        else if (q.Capped) sb.Append(" (showing first ").Append(q.Keys.Count).Append("; raise limit=, page with offset=, or narrow to see more)");
        if (q.Epoch is not null) sb.Append("  epoch=").Append(q.Epoch);   // §2.1.1: offset= windows tile ONLY within one epoch
        sb.Append('\n');
        if (q.PredicateNote is not null) sb.Append(q.PredicateNote).Append('\n');   // where= Q3 accounting (wrong-path/no-value surface)
        if (q.ScanNote is not null) sb.Append(q.ScanNote).Append('\n');             // unscannable-record Q3 accounting (Mutagen-unparseable content)
        if (q.WhereSourceNote is not null) sb.Append(q.WhereSourceNote).Append('\n');   // #233: where_source=winner redundancy under a type=-only scope
        // P5: under a plugins= scope the per-match fields are the SCOPED plugin's OWN values, not the live winner's —
        // the silent-wrong trap (a defining esp's AR 38 vs the winner's live AR 200). Name it loud, once (Q3). The
        // helper is 4-way over (winner_fields=, where_source=) so the note never claims a scoped-body MATCH the
        // where_source=winner scan didn't make (D2 no-drift). Shared with the json/dense renders — one source of truth.
        if (anyScoped) sb.Append("note: ").Append(JsonWire.ScopedFieldsNote(winnerFields, q.WhereWinner)).Append('\n');

        int rendered = 0;
        var notes = new ChildNotes();   // #342: accumulated over the rows actually rendered
        for (int i = 0; i < q.Keys.Count && !(spill?.ManifestOnly ?? false); i++)   // to_file: only the manifest renders — the rows are the FILE
        {
            if (sb.Length >= cap - notes.Reserve)      // the clauses this response has already earned are SPOKEN FOR
            {
                truncated = true;
                sb.Append("... [truncated: rendered ").Append(rendered).Append(" of ").Append(q.Keys.Count)
                  .Append(" returned matches before hitting max_chars=").Append(cap)
                  .Append("; lower limit=, drop fields=/conflict_tree, or raise max_chars]\n");
                break;
            }
            var fk = q.Keys[i];
            string? matches = q.MatchedTargets is { } mt && i < mt.Count ? mt[i] : null;   // multi-target references= un-merge
            if (detail)
            {
                // winner_fields=: read the load-order WINNER's body (source=null) regardless of scan scope; else the
                // body the scan filtered (scoped plugin under plugins=, else winner) — so display never contradicts filter.
                // PINNED to the scan's build (ResolveReadOn / the q-carrying AppendConflictTree): the header's epoch
                // names ONE build, so every fill must read it (PR #305 review).
                var o = svc.ResolveReadOn(q, fk, winnerFields ? null : (q.Sources is { } src ? src[i] : null), fields, conflictTree, depth, resolveNames: resolveNames, linkMemo: linkMemo);
                sb.Append('\n');
                if (matches is not null) sb.Append("  ").Append(fk).Append("  matches=").Append(matches).Append('\n');
                if (o.Error is not null) sb.Append(fk).Append(": error: ").Append(o.Error).Append('\n');
                else AppendRecordBlock(sb, svc, o, fields, conflictTree, cap, notes);   // o carries the scan's pin
            }
            else
            {
                var m = q.Prefilled is not null ? q.Prefilled[i] : svc.ResolveSummaryOn(q, fk);   // lazy fill for conflicts-only, pinned to the scan's build
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
        AppendOwnedChildNotes(sb, notes);
        if (spill is not null) Artifacts.AppendSpillStateText(sb, spill);
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>Render a cross_plugin_query <c>group_by=</c> aggregation: a header naming the key + true total + group
    /// count, then one "  &lt;key&gt; = &lt;count&gt;" row per group (already sorted desc by the core). Q3 accounting
    /// (where= / unscannable notes) survives the aggregation. Over max_chars it stops with the explicit truncation
    /// notice — the count is exact even when the row LIST is clipped (aggregation isn't limit-capped; only rendering is).</summary>
    static string RenderCrossQueryGroups(CrossQueryOutcome q, int cap, SpillState? spill, out bool truncated)
    {
        truncated = false;
        var groups = q.Groups!;
        var sb = new StringBuilder();
        sb.Append("cross_plugin_query: grouped by ").Append(q.GroupBy).Append(" — ")
          .Append(q.Total).Append(q.Total == 1 ? " match" : " matches")
          .Append(" across ").Append(groups.Count).Append(groups.Count == 1 ? " group" : " groups");
        if (q.ScopeLabel is not null) sb.Append(" (DEFINED IN ").Append(q.ScopeLabel).Append(')');
        if (q.Epoch is not null) sb.Append("  epoch=").Append(q.Epoch);
        sb.Append('\n');
        if (q.PredicateNote is not null) sb.Append(q.PredicateNote).Append('\n');
        if (q.ScanNote is not null) sb.Append(q.ScanNote).Append('\n');
        for (int i = 0; i < groups.Count && !(spill?.ManifestOnly ?? false); i++)   // to_file: rows live in the file
        {
            if (sb.Length >= cap)
            {
                truncated = true;
                sb.Append("... [truncated: rendered ").Append(i).Append(" of ").Append(groups.Count)
                  .Append(" groups before hitting max_chars=").Append(cap).Append("; raise max_chars — the total above is exact]\n");
                break;
            }
            sb.Append("  ").Append(groups[i].Key).Append(" = ").Append(groups[i].Count).Append('\n');
        }
        if (spill is not null) Artifacts.AppendSpillStateText(sb, spill);
        return sb.ToString().TrimEnd('\n');
    }

    // ---- housecarl_effect_chain ---------------------------------------------------------------------
    /// <summary>Render the effect chain: a header that RESOLVES the MGEF (its editorid + the confirmed MagicEffect
    /// type — the Q3 typed-match proof), then carrier rows grouped by record type. A valid-but-unused MGEF renders a
    /// clean "none" line, NOT an error (the error path is the bad/mistyped FormID, handled in core). Over max_chars it
    /// stops with the same explicit notice the other read renders use (Q3 — never silent).</summary>
    public static string RenderEffectChain(EffectChainResult r, int maxChars)
    {
        if (r.Error is not null) return "error: " + r.Error + (r.Epoch is not null ? $"\nepoch={r.Epoch}" : "");
        int cap = Cap(maxChars);
        var sb = new StringBuilder();
        sb.Append("effect_chain for ").Append(r.Mgef).Append(" (").Append(r.MgefEditorId).Append(", MagicEffect): ")
          .Append(r.Total).Append(r.Total == 1 ? " carrier row" : " carrier rows");
        if (r.Capped) sb.Append(" (showing first ").Append(r.Rows.Count).Append("; raise limit= or narrow to see more)");
        if (r.Epoch is not null) sb.Append("  epoch=").Append(r.Epoch);
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
    /// <summary>Render the integrity sweep. <paramref name="histogramLimit"/> (#282) caps the <c>counts_only=</c>
    /// histogram rows — the one thing <c>limit=</c> means in that mode, since nothing else is listed. An error class the
    /// caller EXCLUDED renders as "NOT CHECKED", never as a 0, so a skipped check can never be read as a clean one (Q3).</summary>
    public static string RenderCheckErrors(ErrorCheckResult r, int maxChars, int histogramLimit = 1000)
    {
        if (r.Error is not null) return "error: " + r.Error + (r.Epoch is not null ? $"\nepoch={r.Epoch}" : "");
        int cap = Cap(maxChars);
        bool didDangling = r.Classes.HasFlag(ErrorFindingClass.Dangling);
        bool didMasters = r.Classes.HasFlag(ErrorFindingClass.MissingMasters);
        // ONE accounting for this response, and the body renders inside what it leaves (#361). Everything below
        // emits through `acct`, and every omission claim is computed from those registrations after emission stops
        // — there is no truncation flag to miss, and no path that appends the tail past the cap.
        var acct = new CheckAccounting(r, cap);
        int reserve = acct.TextReserve;
        int budget = acct.BodyBudget(reserve);
        var sb = new StringBuilder();

        sb.Append("check_errors — load-order integrity sweep\n");
        sb.Append("scanned ").Append(r.PluginsScanned).Append(r.PluginsScanned == 1 ? " plugin · " : " plugins · ")
          .Append(didDangling ? $"{r.TotalDangling} dangling ref(s)" : "dangling refs NOT CHECKED (findings= excluded 'dangling')").Append(" · ")
          .Append(didMasters ? $"{r.TotalMissingMasters} missing master(s)" : "missing masters NOT CHECKED (findings= excluded 'missing_masters')").Append(" · ")
          .Append(didDangling ? $"{r.TotalUnscannableRecords} unscannable record(s)" : "unscannable records NOT COUNTED (the record walk was skipped)");
        if (r.ExcludedPlugins.Count > 0)
            sb.Append(" · ").Append(r.ExcludedPlugins.Count).Append(" plugin(s) excluded (unparseable)");
        if (r.Epoch is not null) sb.Append(" · epoch=").Append(r.Epoch).Append(EpochOffOrderQualifier(r));
        sb.Append('\n');
        if (r.FilterNote is not null) sb.Append(r.FilterNote).Append('\n');
        if (r.OffOrderScanned is { Count: > 0 } off)
            sb.Append("swept OFF-ORDER (on disk, not in the active load order): ").Append(string.Join(", ", off))
              .Append("   [the file's own records; links resolved against the active order + the file's own definitions]\n");
        AppendBaselineSplit(sb, r, acct);   // #344 — how much of the dangling total is vanilla baseline
        // Where the BODY begins. The overrun arm is a question about the fixed part of the response, so it is asked
        // here rather than at the end, where the answer would be the body's own length.
        int headerLength = sb.Length;

        if (r.CountsOnly)
        {
            AppendHistogram(sb, r.Histogram, histogramLimit, "dangling ref(s) by TARGET plugin (the plugin the broken refs point INTO)",
                            "counts_only=true — totals above are exact; no per-plugin listing was built.",
                            notComputed: "no dangling histogram, by target or by source — the link walk was not run (findings= excluded 'dangling').");
            // #344 — the SOURCE axis: which plugin the broken refs come FROM. The target axis names the absent
            // dependency behind a wall of findings; only this one answers "how much of this is vanilla, and how much
            // did the mods introduce". No note and no not-computed line of its own — both would repeat what the
            // target histogram directly above has just said.
            AppendHistogram(sb, r.DanglingBySource, histogramLimit,
                            "dangling ref(s) by SOURCE plugin (the plugin the broken refs come FROM)", note: null);
            acct.UnreadRows(AppendScanErrorTail(sb, r.Reports, budget));
            acct.ExcludedRows(AppendExcludedPlugins(sb, r.ExcludedPlugins, budget).Appended);   // #288 review finding 5: the NAMES, not just the header count
            return Close(sb, acct, reserve, headerLength);
        }

        if (r.Reports.Count == 0 && r.ExcludedPlugins.Count == 0)
            sb.Append("\nNo errors found in the scanned scope.\n");

        foreach (var p in r.Reports)
        {
            // Composed then measured, never appended then regretted: the section header decides whether this section
            // starts at all, and a section that does not start is counted as not rendered rather than half-written.
            var head = "\n[ERROR] " + p.Plugin + "\n";
            if (sb.Length + head.Length > budget) break;
            sb.Append(head);
            acct.Section();
            if (p.ScanError is not null)
                Fit(sb, "  scan error: " + p.ScanError + "\n", budget);
            if (p.MissingMasters.Count > 0)
                Fit(sb, "  missing master(s): " + string.Join(", ", p.MissingMasters)
                        + "   [declared as a dependency but not present in the active order — install/enable it, or this plugin's refs into it dangle]\n", budget);
            if (p.Dangling.Count > 0)
            {
                Fit(sb, "  dangling reference(s) (" + p.Dangling.Count + "):\n", budget);
                foreach (var d in p.Dangling)
                {
                    var line = "    " + d.Source + " (" + d.SourceType
                             + (string.IsNullOrEmpty(d.SourceEditorId) ? "" : " '" + d.SourceEditorId + "'")
                             + ") -> " + d.Target + "   [target not defined by any active plugin]\n";
                    if (sb.Length + line.Length > budget) break;
                    sb.Append(line);
                    acct.Entry(p.Plugin);   // registered where the line LANDED — the accounting counts the response
                }
            }
            if (p.UnscannableRecords > 0)
                Fit(sb, "  " + p.UnscannableRecords + " record(s) could not be scanned (Mutagen could not parse their content)"
                        + (p.UnscannableSamples.Count > 0 ? ": " + string.Join("; ", p.UnscannableSamples) : "") + "\n", budget);
        }

        acct.ExcludedRows(AppendExcludedPlugins(sb, r.ExcludedPlugins, budget).Appended);
        return Close(sb, acct, reserve, headerLength);
    }

    /// <summary>Append <paramref name="line"/> only if it fits the body budget. A line that does not fit is DROPPED,
    /// not clipped: half a finding is a finding a caller cannot act on, and what was dropped is accounted for below
    /// either way.</summary>
    static void Fit(StringBuilder sb, string line, int budget)
    {
        if (sb.Length + line.Length <= budget) sb.Append(line);
    }

    /// <summary>Close the response: the accounting for what it emitted, then the boundary. Both live inside the
    /// reserve taken before the body rendered, so this is the ONE place either can be appended and it cannot
    /// overrun. The single exception — a <c>max_chars</c> smaller than the accounting itself — is stated in-band
    /// rather than taken silently (#361: appending past the cap without saying so is half of what that issue is).
    /// </summary>
    static string Close(StringBuilder sb, CheckAccounting acct, int reserve, int headerLength)
    {
        if (acct.TextLine() is { } line) sb.Append('\n').Append(line).Append('\n');
        sb.Append('\n').Append(ReadSentences.SweepBoundaryLabel).Append(ReadSentences.SweepBoundary).Append('\n');
        if (acct.CapTooSmall(headerLength, reserve) is { } notice) sb.Append(notice).Append('\n');
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>#344 — the baseline split: how much of the dangling total came from the base-game masters, and how
    /// much from everything else. The sweep's one number ("4996 dangling refs") cannot be acted on without it: the
    /// vanilla leftovers are permanent, present in every install, and nothing a load order can fix. The line NAMES the
    /// plugins it counted as baseline rather than saying "base-game", because that word is the whole claim — houseCARL's
    /// own load-order status groups Creation Club plugins WITH the base masters as "implicit", and the set here is
    /// Mutagen's <c>BaseMasters</c>, which does not contain them.
    /// <para>Creation Club and <c>_ResourcePack.esl</c> are DELIBERATELY not baseline (ruled 2026-08-18). Baseline is
    /// Mutagen's set exactly, which is what keeps it by-construction rather than a list kept here; "keep CC out of my
    /// listing" is a caller's choice, and it routes to the <c>exclude=</c> pole on the successor <c>check</c> surface
    /// (#344's second stage), not to a classification baked into the sweep.</para>
    /// <para>Printed only when a base master was actually SWEPT, and it names THAT subset
    /// (<see cref="ErrorCheckResult.BaseMastersSwept"/>) rather than the whole definition: "0 of 12 are vanilla" over
    /// a scope that never included vanilla is a true sentence that teaches something false — and so is a "3 of 3"
    /// naming five plugins when the sweep opened one (round-1 review, found independently by two reviewers).</para></summary>
    static void AppendBaselineSplit(StringBuilder sb, ErrorCheckResult r, CheckAccounting acct)
    {
        if (!r.Classes.HasFlag(ErrorFindingClass.Dangling) || r.BaseMastersSwept is not { Count: > 0 } swept) return;
        sb.Append("baseline: ").Append(r.BaselineDangling).Append(" of ").Append(r.TotalDangling)
          .Append(" dangling ref(s) come from the base-game master(s) this sweep covered (").Append(string.Join(", ", swept))
          .Append(") — vanilla leftovers rather than anything this load order introduced; ")
          .Append(r.TotalDangling - r.BaselineDangling).Append(" come from the rest of the swept scope.").Append('\n');
        // The phase-order sentence only where the phase order DECIDED something. Nothing was crowded out of a sweep
        // that listed everything it found, and saying so there explains a mechanism the reader did not just hit.
        // (Capped is never set under counts_only — that mode lists nothing — so it carries the mode gate too.)
        // "spent on every other plugin BEFORE those" is a statement about an ordering between two groups, so it needs
        // both groups to exist: on a sweep scoped to base masters alone there is no "every other plugin", and what the
        // budget dropped is the caller's own requested findings (round-2 review). NonBaseInScope is that fact, computed
        // in the sweep from the resolved targets — comparing PluginsScanned against the swept-base count subtracts two
        // numbers that measure different things and prints this sentence over a base-only scope whenever they diverge
        // (Aaron's PR #360 review: a record scope that filters a base master out, or a repeated plugins= name).
        if (acct.OmittedByBudget > 0 && r.BaselineDangling > 0 && r.NonBaseInScope)
            sb.Append("  the listing budget (limit=) is spent on every other plugin BEFORE those, so baseline findings ")
              .Append("cannot crowd the rest out of the list; the sections below stay in load order.").Append('\n');
    }

    // ---- shared sweep-render pieces (#282) ----------------------------------------------------------
    /// <summary>The truncation/overflow hint the two sweep tools share. The old wording told the caller to "scope
    /// plugins=" when plugins= was already the narrowest scope either tool had — advice that could not be taken. It now
    /// names the knobs that actually exist.</summary>
    const string SweepNarrowHint =
        "narrow with type= / formids= / editorid_contains= / findings=, ask counts_only=true for just the totals, or raise max_chars";

    /// <summary>The epoch stamp's coverage qualifier (PR #305 review): when off-order files were swept beside the
    /// index, the fingerprint does NOT cover their content (they are located on disk, outside the fingerprinted
    /// order) — say so next to the stamp, so equal epochs are never read as "same inputs" across such sweeps. The
    /// fact itself is data (the OffOrderScanned list / the json <c>off_order_scanned</c> array); this qualifier is
    /// its header-line rendering.</summary>
    static string EpochOffOrderQualifier(ErrorCheckResult r) =>
        r.OffOrderScanned is { Count: > 0 } ? " (indexed plugins only — off-order file content is outside the fingerprint)" : "";

    /// <summary>validate_scripts has no off-order lane — its stamp always covers everything it swept. The overload
    /// exists so the two sweep headers stay textually identical (one shape, no drift).</summary>
    static string EpochOffOrderQualifier(ScriptCheckResult r) => "";

    /// <summary>Render a <c>counts_only=</c> histogram, capped at <paramref name="rowLimit"/> with the true distinct-key
    /// count always stated. A null histogram means the mode was not requested; an EMPTY one means the sweep genuinely
    /// found nothing, and the two read differently (Q3).</summary>
    static void AppendHistogram(StringBuilder sb, IReadOnlyList<SweepCount>? rows, int rowLimit, string title, string? note,
                                string? notComputed = null)
    {
        if (note is not null) sb.Append('\n').Append(note).Append('\n');
        if (rows is null) { if (notComputed is not null) sb.Append(notComputed).Append('\n'); return; }
        // The title rides the empty case too: two axes that both came back empty rendered as two identical untitled
        // sentences, with no way to tell which was which — or that a second axis existed at all (round-1 review).
        if (rows.Count == 0) { sb.Append("\n").Append(title).Append(": nothing to tally — no findings in the swept scope.\n"); return; }
        sb.Append('\n').Append(title).Append(" (").Append(rows.Count).Append(" distinct):\n");
        int shown = 0;
        foreach (var row in rows)
        {
            if (shown >= rowLimit) break;
            sb.Append("  ").Append(row.Count.ToString().PadLeft(6)).Append("  ").Append(row.Key).Append('\n');
            shown++;
        }
        if (shown < rows.Count)
            sb.Append("  ... [").Append(rows.Count - shown).Append(" more row(s) — raise limit= to see them]\n");
    }

    /// <summary>The named, reasoned list of plugins the index build could not parse. Shared by the listing and
    /// <c>counts_only=</c> paths — counts_only used to return before it, leaving the header's bare count with no way to
    /// learn WHICH plugin went unchecked without re-running (PR #288 review, finding 5).</summary>
    static (int Appended, bool Stopped) AppendExcludedPlugins(StringBuilder sb, IReadOnlyDictionary<string, string> excluded,
                                                             int budget)
    {
        if (excluded.Count == 0) return (0, false);
        var head = "\nexcluded plugins (could not be parsed — NOT checked):\n";
        if (sb.Length + head.Length > budget) return (0, true);
        sb.Append(head);
        int n = 0;
        foreach (var kv in excluded)
        {
            var line = "  " + kv.Key + ": " + kv.Value + "\n";
            if (sb.Length + line.Length > budget) return (n, true);
            sb.Append(line);
            n++;
        }
        return (n, false);
    }

    /// <summary>Under <c>counts_only=</c> the reports list carries the honesty layer only (records/plugins houseCARL
    /// could not read). Emit it verbatim so a counts-only answer still names what it could not check (Q3).</summary>
    static int AppendScanErrorTail(StringBuilder sb, IReadOnlyList<PluginErrors> reports, int budget)
    {
        int n = 0;
        foreach (var p in reports)
        {
            var line = new StringBuilder("\n[UNREAD] ").Append(p.Plugin).Append(": ");
            if (p.ScanError is not null) line.Append(p.ScanError).Append(' ');
            if (p.UnscannableRecords > 0)
            {
                line.Append(p.UnscannableRecords).Append(" record(s) could not be scanned");
                if (p.UnscannableSamples.Count > 0) line.Append(": ").Append(string.Join("; ", p.UnscannableSamples));
            }
            line.Append('\n');
            if (sb.Length + line.Length > budget) break;
            sb.Append(line);
            n++;
        }
        return n;
    }

    // ---- housecarl_validate_scripts -----------------------------------------------------------------
    /// <summary>Render the script-property sweep. <paramref name="histogramLimit"/> (#282) caps the
    /// <c>counts_only=</c> histogram rows.</summary>
    public static string RenderScriptCheck(ScriptCheckResult r, int maxChars, int histogramLimit = 1000)
    {
        if (r.Error is not null) return "error: " + r.Error + (r.Epoch is not null ? $"\nepoch={r.Epoch}" : "");
        int cap = Cap(maxChars);
        var sb = new StringBuilder();

        bool didObject = r.Classes.HasFlag(ScriptFindingClass.UnboundObject);
        bool didScalar = r.Classes.HasFlag(ScriptFindingClass.UnboundScalar);
        bool didNull = r.Classes.HasFlag(ScriptFindingClass.BoundNull);

        sb.Append("validate_scripts — VMAD script-property binding sweep\n");
        sb.Append("scanned ").Append(r.PluginsScanned).Append(r.PluginsScanned == 1 ? " plugin · " : " plugins · ")
          .Append(r.RecordsWithScripts).Append(" record(s) with scripts · ")
          // A class the caller excluded reads as NOT CHECKED, never as a 0 — a 0 would say "looked, found none" about
          // the HIGH silent-None class nobody looked for (PR #288 review, finding 1).
          .Append(UnboundTotalText(r, didObject, didScalar))
          .Append(" · ")
          .Append(NullTotalText(r, didNull))
          .Append(" · ")
          .Append(r.TotalUnverifiable).Append(" unverifiable");
        if (r.ExcludedPlugins.Count > 0)
            sb.Append(" · ").Append(r.ExcludedPlugins.Count).Append(" plugin(s) excluded (unparseable)");
        if (r.Epoch is not null) sb.Append(" · epoch=").Append(r.Epoch).Append(EpochOffOrderQualifier(r));
        sb.Append('\n');
        if (r.FilterNote is not null) sb.Append(r.FilterNote).Append('\n');
        if (r.ReadIncomplete)
            sb.Append("note: a BSA failed to read this build — a '.pex not on disk' below may merely be unscanned, not truly absent (Q3).\n");

        if (r.CountsOnly)
        {
            AppendHistogram(sb, r.Histogram, histogramLimit, "unbound properties by NAME",
                            "counts_only=true — totals above are exact; no per-record listing was built.",
                            notComputed: "no unbound histogram — findings= excluded both unbound classes, so nothing was tallied.");
            foreach (var rec in r.Reports)   // the honesty layer: plugins whose record enumeration faulted
            {
                if (sb.Length >= cap) { sb.Append("\n... [truncated at max_chars]\n"); break; }
                if (rec.ScanError is not null) sb.Append("\n[SCAN ERROR] ").Append(rec.Plugin).Append(": ").Append(rec.ScanError).Append('\n');
            }
            // The helper reports rather than annotates (it is shared with check_errors, whose accounting states the
            // same fact in its own terms), so this lane keeps its own marker at its own call site.
            if (AppendExcludedPlugins(sb, r.ExcludedPlugins, cap).Stopped) sb.Append("  ... [truncated at max_chars]\n");
            AppendScriptCheckBoundary(sb);
            return sb.ToString().TrimEnd('\n');
        }

        if (r.Reports.Count == 0 && r.ExcludedPlugins.Count == 0)
            sb.Append("\nNo unbound script properties found in the scanned scope.\n");

        bool truncated = false;
        foreach (var rec in r.Reports)
        {
            if (sb.Length >= cap)
            {
                sb.Append("\n... [truncated at max_chars=").Append(cap).Append("; ").Append(SweepNarrowHint)
                  .Append(", or property_contains= to chase one property]\n");
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

        // The capped tail restates the totals, so it needs the SAME class-awareness as the header — otherwise a small
        // limit= reintroduces the literal "0 unbound" that the header no longer prints (re-review finding 2).
        if (r.Capped)
            sb.Append("\n[finding list capped at limit; true totals = ").Append(UnboundTotalText(r, didObject, didScalar))
              .Append(" + ").Append(NullTotalText(r, didNull)).Append(" — raise limit= to see all]\n");

        if (!truncated && AppendExcludedPlugins(sb, r.ExcludedPlugins, cap).Stopped)
            sb.Append("  ... [truncated at max_chars]\n");

        AppendScriptCheckBoundary(sb);
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>The unbound total, spelled so it can never claim a class nobody checked. ONE definition, used by both
    /// the header and the capped tail — the tail restating the numbers in its own words is how "0 unbound" survived the
    /// header fix (re-review finding 2).
    /// <para>It also carries the <c>property_contains=</c> label when one is in force, so this number states its own
    /// scope rather than leaning on a blanket claim the sweep's other counts would not satisfy (round-3 review).</para></summary>
    static string UnboundTotalText(ScriptCheckResult r, bool didObject, bool didScalar)
        => !didObject && !didScalar ? "unbound NOT CHECKED (findings= excluded both unbound classes)"
         : didObject && didScalar   ? $"{r.TotalUnbound} unbound{PropLabel(r)}"
         : didObject                ? $"{r.TotalUnboundObject} unbound{PropLabel(r)} (object only — unbound_scalar NOT CHECKED)"
                                    : $"{r.TotalUnboundScalar} unbound{PropLabel(r)} (scalar only — unbound_object NOT CHECKED)";

    /// <summary>The bound-but-null total, same contract as <see cref="UnboundTotalText"/>.</summary>
    static string NullTotalText(ScriptCheckResult r, bool didNull)
        => didNull ? $"{r.TotalNullObject} bound-but-null{PropLabel(r)}"
                   : "bound-but-null NOT CHECKED (findings= excluded 'bound_null')";

    /// <summary>The per-number <c>property_contains=</c> label, on exactly the two counts that filter narrows. Absent
    /// from records-with-scripts and unverifiable, which it does not narrow — that asymmetry is the whole point.</summary>
    static string PropLabel(ScriptCheckResult r)
        => r.PropertyContains is null ? "" : $" matching '{r.PropertyContains}'";

    static void AppendScriptCheckBoundary(StringBuilder sb)
        => sb.Append("\nboundary: checks Auto (CK-editable) properties across the extends chain — not code-driven full ")
             .Append("properties. An unbound object property is the silent-None footgun, but CAN be intentional (filled at runtime) — a ")
             .Append("finding is a flag to VERIFY. A script whose .pex is not on disk is reported unverifiable, never passed clean.\n");

    // ---- shared building blocks ---------------------------------------------------------------------

    /// <summary><paramref name="notes"/> (with <paramref name="precise"/> naming the #342 tier) registers a clause
    /// as each ANNOTATED field line is written — so a field the cap truncates away earns nothing. Both are null on
    /// the lanes that render a record outside a #342-annotated response (readback, verify).</summary>
    /// <param name="reserve">Chars held back for the response-level clauses this render may still state: the field
    /// loop stops that much earlier, while the notice still quotes the caller's own <paramref name="cap"/>.</param>
    static void AppendRecord(StringBuilder sb, ReadOutcome o, int cap, int reserve = 0, bool precise = false, ChildNotes? notes = null)
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
            if (sb.Length >= cap - reserve)                                 // depth= can produce many lines — cap them (Q3)
            {
                sb.Append("  ... [truncated: showing ").Append(i).Append(" of ").Append(r.Fields.Count)
                  .Append(" field lines at max_chars=").Append(cap)
                  .Append("; narrow with fields=, lower depth=, or raise max_chars]\n");
                break;
            }
            var f = r.Fields[i];
            sb.Append("  ").Append(f.Path).Append(" = ").Append(f.HasValue ? f.Token : f.Note);
            if (f.Display is not null) sb.Append("   (").Append(f.Display).Append(')');   // display-only annotation (e.g. decoded biped slots) — never the round-trip token
            if (f.Link is not null) sb.Append("   (").Append(LinkText(f.Link)).Append(')');   // resolve_names: target identity, DISPLAY-ONLY — never the round-trip token
            sb.Append('\n');
            // The #342 clause is earned HERE, by a line that reached the caller — not where the annotation was decided.
            if (notes is not null && o.OwnedChildFields is { } ann && ann.TryGetValue(f.Path, out var shape))
                notes.Emitted(ClauseOf(shape, precise), f.Path);
        }
    }

    /// <summary>The resolve_names parenthetical (P7): a FormLink token's target identity as "→ editorid "Name"", or
    /// "unresolved: not in the active order" for a dangling target (named, never dropped — Q3). DISPLAY-ONLY: this
    /// is appended AFTER the round-trip token, never in place of it. Internal: the dense render's cells reuse the
    /// SAME display vocabulary (D2 — renders must not drift).</summary>
    internal static string LinkText(ResolvedRef r) =>
        !r.Resolved ? "unresolved: target not in the active order"
        : string.IsNullOrEmpty(r.Name) ? $"→ {r.EditorId ?? "<no editorid>"}"
        : $"→ {r.EditorId ?? "<no editorid>"} \"{r.Name}\"";

    /// <summary>The conflict tree: the ordered touching-plugin list, then (when >1 plugin touches) the
    /// winner-relative field diff — each other plugin's only-the-fields-that-differ, as `path=theirs (winner X)`.
    /// Bodies come from <see cref="LoadOrderService.ResolveTree"/> (on-demand fetch, held by nothing). The diff
    /// is char-budget-bounded: over <paramref name="cap"/> it stops with an explicit notice (Q3).</summary>
    /// <param name="reserve">Chars held back for the #342 response-level clauses — the same split
    /// <see cref="AppendRecord"/> makes: budget against <c>cap - reserve</c>, quote <c>cap</c>.</param>
    static void AppendConflictTree(StringBuilder sb, LoadOrderService svc, ReadOutcome o, IReadOnlyList<string>? fields, int cap,
                                   ConflictTreeView? prefetched = null, int reserve = 0)
    {
        var tp = o.TouchingPlugins;
        if (tp is null) return;
        sb.Append("conflict_tree: ").Append(tp.Count).Append(tp.Count == 1 ? " plugin touches" : " plugins touch")
          .Append(" this record (load order, winner last):\n");
        for (int i = 0; i < tp.Count; i++)
        {
            if (sb.Length >= cap - reserve && i < tp.Count - 1)            // budget gone mid-list — name the rest + the winner, stop
            {
                sb.Append("  ... [").Append(tp.Count - i).Append(" more plugins omitted at max_chars=").Append(cap)
                  .Append("; winner (last in load order) = ").Append(tp[^1]).Append("; raise max_chars or narrow with fields=]\n");
                return;                                                    // and skip the diff (the all-bodies fetch too) — already over budget
            }
            sb.Append("  ").Append(i + 1).Append(". ").Append(tp[i]).Append(i == tp.Count - 1 ? "  (winner)" : "").Append('\n');
        }

        if (tp.Count <= 1) return;                                         // nothing to diff against
        // Pinned to the OUTCOME's own build (PR #305 review + re-review): every ReadOutcome — single read, batch
        // item, cross-query detail row — carries the (resolver, view) it was answered from, so the tree fill and
        // the response's epoch stamp name the same build. The unpinned fallback exists only for a hand-built
        // outcome (guards).
        // The block helper already fetched this tree to answer #342's precise tier off the same bodies; re-fetching
        // would double every touching plugin's overlay enumeration, which is the cost that tier exists to avoid.
        var tree = prefetched
                   ?? (o.Pin is { } p ? svc.ResolveTreePinned(p, o.FormKey, fields)
                                      : svc.ResolveTree(o.FormKey, fields));   // materialised (no live overlay) — Option B
        if (tree is null || tree.Nodes.Count <= 1) return;

        var winnerNode = tree.Winner;                                       // Nodes[^1] = highest priority = the winner

        // CONTENT diff (HCBR-2026-06-09-01): each node's DEEP read vs the winner's, compared by FieldsDiff —
        // scalar leaves exact-path, list contents order-insensitively by whole element (a reorder of equal
        // contents surfaces as an explicit ORDER-DIFFERS note, #275). The old depth-1 token
        // comparison called equal-count lists with different contents "identical to winner" — an affirmative
        // false ITM that masked a real override regression. "Identical" is now only claimed when the FULL
        // modeled content compared clean; a truncated comparison says so instead (Q3).
        sb.Append("diff (field deltas vs winner ").Append(winnerNode.Plugin)
          .Append("; identical fields omitted; list contents compared by content, element reorders flagged):\n");
        for (int n = 0; n < tree.Nodes.Count - 1; n++)                      // every node except the winner
        {
            if (sb.Length >= cap - reserve)
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
            int room = cap - reserve - sb.Length;
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
            ? $"(no field deltas; {diff.AgreedCount} modeled field(s) read identical to the winner ({SampleOf(diff)}))"
            : "(identical to winner — full modeled content compared)";

    /// <summary>No-delta render for a fields=-narrowed compare — node-neutral, and the identity claim must not
    /// outrun the compared paths (PR #28 review #2).</summary>
    static string IdenticalAcrossFields(FieldsDiff.Result diff) =>
        diff.AgreedCount > 0
            ? $"(no deltas across the requested fields; {diff.AgreedCount} of them read identical to the winner ({SampleOf(diff)}) — other fields NOT compared)"
            : "(identical to winner across the requested fields — other fields NOT compared)";

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

        // The banner — OUT-OF-LOAD-ORDER first, always (the single load-bearing requirement). The stamp describes THIS
        // READ (it bypassed load-order resolution), which is true of every call; the old parenthetical went further and
        // asserted "the game does not load this file", which is FALSE whenever the file passed is the live, winning
        // plugin — exactly #269's case. The wording below says only what the stamp actually knows (#271); what the game
        // does with the file is the separate, per-file question the bracket on the next line answers.
        sb.Append("read_plugin_file — OUT-OF-LOAD-ORDER (raw file read — not resolved through the load order)\n");
        sb.Append("file: ").Append(o.FilePath);
        // Where reports the LAYER the file was found in (a mod folder's switch); the standing reports the FILE. Read
        // together, "mod 'X' (enabled)" beside a bare "NOT active" looked self-contradictory, so the two subjects are
        // now named explicitly and the not-loaded case carries its CAUSE — the reader should never have to infer which
        // of the two facts changed, nor go searching for a remedy the tool already knows (#271).
        if (!string.IsNullOrEmpty(o.Where))
            sb.Append("  [").Append(o.Where)
              .Append(o.WhyNotActive is { } why ? $" — but the game does NOT load this file: {why}" : " — the game loads this file")
              .Append(']');
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
                if (f.Link is not null) sb.Append("   (").Append(LinkText(f.Link)).Append(')');   // resolve_names: target identity (resolved against the ACTIVE order), DISPLAY-ONLY
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
