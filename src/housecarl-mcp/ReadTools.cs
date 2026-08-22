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
         "exclude= drops named plugins or whole groups (base_masters / implicit) out of the sweep when you want them gone " +
         "entirely rather than merely accounted for. " +
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
        [Description("Optional. Plugins to leave OUT of the sweep entirely — they cost no record walk and no limit= " +
             "budget. Each value is either a plugin filename WITH its extension ('CoolMod.esp') or one of two group " +
             "names: base_masters (the five the game ships with) or implicit (every plugin the order force-loads " +
             "because plugins.txt does not list it — this is where Creation Club plugins and _ResourcePack.esl are, " +
             "and it INCLUDES the base masters). A value that is neither is refused before the sweep runs, and so is " +
             "a FILENAME YOU NAMED that nothing in scope matches — a group member that is not in this order is the " +
             "ordinary case and is simply dropped. This does not change what counts " +
             "as the vanilla BASELINE the response splits out — that is always Mutagen's own base-master set.")]
            string[]? exclude = null,
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
        var result = svc.CheckErrors(plugins, lim, formids, editorid_contains, type, findings, counts_only, exclude);
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
        // ONE accounting for this response, and the body renders inside what it leaves (#361). Everything below
        // emits through `acct`, and every omission claim is computed from those registrations after emission stops
        // — there is no truncation flag to miss, and no path that appends the tail past the cap.
        var acct = new CheckAccounting(r, cap);
        int reserve = acct.TextReserve;
        int budget = acct.BodyBudget(reserve);
        var sb = new StringBuilder();

        sb.Append("check_errors — load-order integrity sweep\n");
        AppendErrorsHead(sb, r, acct);
        // EVERYTHING A CAP CAN REFUSE GOES THROUGH `body`. What the header above, the axes' own lines, the reserved
        // closing disclosures, the accounting and the boundary come to is therefore whatever the finished response
        // is MINUS what `body` let through — which is how the overrun notice is told the fixed part's size
        // (BoundedBody.FixedPart), rather than by a header length captured here and summed with a reserve.
        var body = new BoundedBody(acct, budget, () => sb.Length);
        AppendErrorsSection(sb, r, body, histogramLimit);
        // The excluded-plugin roster is a SCOPE fact, not a family fact — which plugins the INDEX could not parse
        // at all. It is emitted once per RESPONSE, after every section, which is why it sits here rather than
        // inside the section renderer: on the merged surface a roster written per family would be the same rows
        // twice, declared by two accountings and counted by both.
        AppendExcludedPlugins(sb, body, r.ExcludedPlugins);
        return Close(sb, acct, body);
    }

    /// <summary>The errors family's own head: what it swept and what it found. Everything above the first thing a
    /// budget can refuse, and everything below the response's title — which is the caller's, because the merged
    /// surface titles a SECTION where the single-family tool titles the whole response.</summary>
    static void AppendErrorsHead(StringBuilder sb, ErrorCheckResult r, CheckAccounting acct)
    {
        bool didDangling = r.Classes.HasFlag(ErrorFindingClass.Dangling);
        bool didMasters = r.Classes.HasFlag(ErrorFindingClass.MissingMasters);
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
    }

    /// <summary>The errors family's two counts_only axes, built ONCE and read by both the render and the
    /// DEMAND pass — a second construction here would be a second Head, and the demand would be measuring a
    /// row that is not the row written.</summary>
    internal static HistogramAxis[] ErrorsAxes(ErrorCheckResult r) => new[]
    {
        new HistogramAxis(SweepSubject.HistogramByTarget, r.Histogram,
                          "dangling ref(s) by TARGET plugin (the plugin the broken refs point INTO)",
                          "counts_only=true — totals above are exact; no per-plugin listing was built.",
                          "no dangling histogram, by target or by source — the link walk was not run (findings= excluded 'dangling')."),
        new HistogramAxis(SweepSubject.HistogramBySource, r.DanglingBySource,
                          "dangling ref(s) by SOURCE plugin (the plugin the broken refs come FROM)"),
    };

    /// <summary>The scripts family's one counts_only axis. Same reason.</summary>
    internal static HistogramAxis[] ScriptsAxes(ScriptCheckResult r) => new[]
    {
        new HistogramAxis(SweepSubject.HistogramByProperty, r.Histogram, "unbound properties by NAME",
                          "counts_only=true — totals above are exact; no per-record listing was built.",
                          "no unbound histogram — findings= excluded both unbound classes, so nothing was tallied."),
    };

    /// <summary>ONE plugin section's fixed part — everything the section says besides its dangling entries. It
    /// is a UNIT: emitted whole or not at all, because a scan error, the missing masters it declares and its
    /// unscannable-record count are each a finding in their own right, and no accounting subject covers half of
    /// one.
    ///
    /// <para><b>Extracted so the DEMAND pass and the WRITE read ONE source.</b> The allocation water-fills over
    /// MEASURED demand (<see cref="BodyAllocation"/>), and a demand taken from a second spelling of this composer
    /// would be free to drift from what is written — the divergence ALLOCATION-EQUALS-SPEND makes a stop rather
    /// than a slack factor.</para></summary>
    internal static string ComposeErrorSection(PluginErrors p)
    {
        var fixedPart = new StringBuilder("\n[ERROR] ").Append(p.Plugin).Append('\n');
        if (p.ScanError is not null)
            fixedPart.Append("  scan error: ").Append(p.ScanError).Append('\n');
        if (p.MissingMasters.Count > 0)
            fixedPart.Append("  missing master(s): ").Append(string.Join(", ", p.MissingMasters))
                     .Append("   [declared as a dependency but not present in the active order — install/enable it, or this plugin's refs into it dangle]\n");
        if (p.UnscannableRecords > 0)
        {
            fixedPart.Append("  ").Append(p.UnscannableRecords).Append(" record(s) could not be scanned (Mutagen could not parse their content)");
            if (p.UnscannableSamples.Count > 0) fixedPart.Append(": ").Append(string.Join("; ", p.UnscannableSamples));
            fixedPart.Append('\n');
        }
        if (p.Dangling.Count > 0)
            fixedPart.Append("  dangling reference(s) (").Append(p.Dangling.Count).Append("):\n");
        return fixedPart.ToString();
    }

    /// <summary>ONE dangling entry — the one thing this family's accounting states a unit at a time. Shared by the
    /// demand pass and the write; see <see cref="ComposeErrorSection"/>.</summary>
    internal static string ComposeDanglingLine(DanglingRef d)
    {
            return "    " + d.Source + " (" + d.SourceType
                     + (string.IsNullOrEmpty(d.SourceEditorId) ? "" : " '" + d.SourceEditorId + "'")
                     + ") -> " + d.Target + "   [target not defined by any active plugin]\n";
            // Registered where the line LANDS — the accounting counts the response, and the by-source roster is
            // tallied off the same registration, so the count and the roster cannot disagree.
    }

    /// <summary>ONE unread-plugin row, the <c>counts_only</c> lane's honesty layer. Shared, for the same reason.</summary>
    internal static string ComposeUnreadRow(PluginErrors p)
    {
        var line = new StringBuilder("\n[UNREAD] ").Append(p.Plugin).Append(": ");
        if (p.ScanError is not null) line.Append(p.ScanError).Append(' ');
        if (p.UnscannableRecords > 0)
        {
            line.Append(p.UnscannableRecords).Append(" record(s) could not be scanned");
            if (p.UnscannableSamples.Count > 0) line.Append(": ").Append(string.Join("; ", p.UnscannableSamples));
        }
        line.Append('\n');
        return line.ToString();
    }

    /// <summary>ONE histogram row. The FIRST row of an axis carries the axis head, so its width differs from the
    /// rest — the demand pass asks with the same row index the write will use.</summary>
    internal static string ComposeHistogramRow(HistogramAxis axis, SweepCount row, bool first)
    {
        var line = "  " + row.Count.ToString().PadLeft(6) + "  " + row.Key + "\n";
        return first ? axis.Head + line : line;
    }

    /// <summary>The errors family's BODY — everything a cap can refuse, and nothing else. It writes no roster, no
    /// accounting and no boundary: those are the response's, and a section renderer that wrote them could not be
    /// called twice in one response.</summary>
    static void AppendErrorsSection(StringBuilder sb, ErrorCheckResult r, BoundedBody body, int histogramLimit)
    {
        if (r.CountsOnly)
        {
            // Both axes are handed over TOGETHER so both are reserved before either renders. #344's SOURCE axis —
            // which plugin the broken refs come FROM — carries no note and no not-computed line of its own: both
            // would repeat what the TARGET axis directly above has just said.
            AppendHistograms(sb, body, histogramLimit,
                ErrorsAxes(r));
            AppendScanErrorTail(sb, body, r.Reports);
            return;
        }

        if (r.Reports.Count == 0 && r.ExcludedPlugins.Count == 0)
            sb.Append("\nNo errors found in the scanned scope.\n");

        foreach (var p in r.Reports)
        {
            // A SECTION IS EMITTED WHOLE, OR NOT AT ALL — except for its dangling entries, which are the one thing
            // the accounting can account for one at a time. Everything else a section says (the scan error, the
            // missing masters, the unscannable-record count) is a finding in its own right, and a per-line "append
            // if it fits" dropped those silently: they are not entries, so no accounting subject covered them, and
            // the tool's own parameter text promises master-table findings are "always listed in full". Composing
            // the fixed part first makes the only droppable units the two the accounting already states — a whole
            // section, or an entry.
            var section = ComposeErrorSection(p);
            if (!body.Emit(SweepSubject.PluginSections, section.Length, () => sb.Append(section))) break;

            foreach (var d in p.Dangling)
            {
                var line = ComposeDanglingLine(d);
                if (!body.Emit(SweepSubject.DanglingEntries, line.Length, () => sb.Append(line), p.Plugin)) break;
            }
        }
    }

    /// <summary>Close the response: the accounting for what it emitted, then the boundary. Both live inside the
    /// reserve taken before the body rendered, so this is the ONE place either can be appended and it cannot
    /// overrun. The single exception — a <c>max_chars</c> smaller than the accounting itself — is stated in-band
    /// rather than taken silently (#361: appending past the cap without saying so is half of what that issue is).
    /// </summary>
    /// <param name="body">the bounded body, for what the BODY wrote
    /// (<see cref="BoundedBody.FixedPart"/> subtracts it). The rest of the finished response is its fixed part —
    /// the quantity the overrun notice branches on. Given a fixed part that leaves out an unconditional line, a cap
    /// too small for that line gets explained as a body unit overshooting.</param>
    static string Close(StringBuilder sb, CheckAccounting acct, BoundedBody body)
    {
        if (acct.TextLine() is { } line) sb.Append('\n').Append(line).Append('\n');
        sb.Append('\n').Append(ReadSentences.SweepBoundaryLabel).Append(acct.Boundary).Append('\n');
        // The overrun question is asked of the FINISHED response, so the string is built first and measured — and
        // the notice is PART of the response it reports the length of. Measured without itself it understated by its
        // own length: 1,109 stated on a response of 1,281. Composing it changes only the width of one printed
        // number, so a second pass settles it and a third is only ever needed when that width moved.
        var response = sb.ToString().TrimEnd('\n');
        int needed = body.FixedPart(response.Length);
        // How many times this response prints the cap back, COUNTED in the response itself: raising the cap widens
        // every one of those numbers, and the remedy has to name a cap that already covers that.
        int sites = acct.CapPrintsIn(response);
        if (acct.CapTooSmall(response.Length, needed, 0, sites) is not { } notice) return response;
        var settled = acct.CapTooSmall(response.Length + notice.Length, needed, notice.Length, sites)!;
        if (settled.Length != notice.Length)
            settled = acct.CapTooSmall(response.Length + settled.Length, needed, settled.Length, sites)!;
        return response + settled;
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

    /// <summary>Reserve EVERY axis's FIXED PART — its unconditional lines and its closing disclosure — then render
    /// them all. The two passes are the point: an axis that reserved its own room only when its turn came would
    /// find a sibling had already spent the budget, which is the silence the reserve exists to remove. Reserving is
    /// therefore not something each axis does for itself.</summary>
    static void AppendHistograms(StringBuilder sb, BoundedBody body, int rowLimit, params HistogramAxis[] axes)
    {
        foreach (var a in axes) body.Reserve(a.Subject, a.TextFixed);
        foreach (var a in axes) AppendHistogram(sb, body, rowLimit, a);
    }

    /// <summary>Render ONE <c>counts_only=</c> histogram axis, capped at <paramref name="rowLimit"/> with the true
    /// distinct-key count always stated. A null histogram means the mode was not requested; an EMPTY one means the
    /// sweep genuinely found nothing, and the two read differently (Q3).</summary>
    /// <param name="body">the ONE bounded emission path. The axis's ROWS go through it and can be refused; the
    /// axis's own statement about itself cannot — that room was reserved out of <c>max_chars</c> before the body
    /// rendered, and <see cref="BoundedBody.Close"/> spends it without asking. Charging the statement to the same
    /// budget as the rows is what let a whole axis disappear with nothing saying so (#392): the pressure that cut
    /// the rows cut the sentence reporting the cut.</param>
    static void AppendHistogram(StringBuilder sb, BoundedBody body, int rowLimit, HistogramAxis axis)
    {
        // The note and the not-computed line are fixed text that does not grow with the findings, and the second is
        // this axis's whole answer — a "the walk was not run" that a budget could drop would be the silence the
        // sentence exists to break. They are part of the response's fixed part, deliberately — and they go through
        // `body` so that fixed part is MEASURED. Appended straight to the builder they were invisible to the
        // overrun notice, which then explained a cap too small for them as a body unit overshooting (#391 review).
        if (axis.NoteLine.Length > 0) body.Fixed(axis.Subject, () => sb.Append(axis.NoteLine));
        if (axis.Rows is not { } rows)
        {
            if (axis.NotComputedLine.Length > 0) body.Fixed(axis.Subject, () => sb.Append(axis.NotComputedLine));
            body.Release(axis.Subject);
            return;
        }
        // The title rides the empty case too: two axes that both came back empty rendered as two identical untitled
        // sentences, with no way to tell which was which — or that a second axis existed at all (round-1 review).
        // "Nothing to tally" is this axis's entire answer, so it closes with it rather than emitting it: an answer a
        // tight cap can refuse leaves the caller unable to tell "no findings" from "this axis was never computed".
        if (rows.Count == 0) { body.Close(axis.Subject, () => sb.Append(axis.EmptyLine)); return; }
        var head = axis.Head;
        int shown = 0;
        bool cutByBudget = false;
        foreach (var row in rows)
        {
            if (shown >= rowLimit) break;
            var unit = ComposeHistogramRow(axis, row, shown == 0);
            // The row pays for itself only. What the closing line costs is already held back, against this axis's
            // own tests as much as its siblings' — a subject may spend the budget on its rows, never on its own
            // disclosure.
            if (!body.Emit(axis.Subject, unit.Length, () => sb.Append(unit))) { cutByBudget = true; break; }
            shown++;
        }
        // The closing disclosure, from ONE computation the json lane reads too. The remedy names the knob that
        // STOPPED THIS AXIS: "raise limit=" on rows the response had no room for is a knob that moves nothing, and so
        // is "raise max_chars=" on rows the row budget refused. An axis that rendered every row it had says nothing,
        // and gives its reserved room back for the subjects rendering after it.
        //
        // An axis the budget admitted NO rows of still says so, head and count together: an axis that exists and
        // renders nothing at all is the same silent cut, one level down.
        if (HistogramCut.For(rows.Count, shown, cutByBudget) is not { } cut) { body.Release(axis.Subject); return; }
        if (shown == 0) body.Close(axis.Subject, () => sb.Append(head).Append(cut.Line));
        else body.Close(axis.Subject, () => sb.Append(cut.Line));
    }

    /// <summary>The named, reasoned list of plugins the index build could not parse. Shared by the listing and
    /// <c>counts_only=</c> paths — counts_only used to return before it, leaving the header's bare count with no way to
    /// learn WHICH plugin went unchecked without re-running (PR #288 review, finding 5).
    /// <para>How many rows it emitted is no longer returned: the accounting states that fact, from the same
    /// registrations, in both transports. The one caller that counted rows here composed a second spelling of it —
    /// and got it wrong first, claiming every plugin was unnamed on a response that had just named one.</para></summary>
    static void AppendExcludedPlugins(StringBuilder sb, BoundedBody body, IReadOnlyDictionary<string, string> excluded)
    {
        for (int i = 0; i < excluded.Count; i++)
        {
            var unit = ComposeExcludedRow(excluded, i);
            if (!body.Emit(SweepSubject.ExcludedRows, unit.Length, () => sb.Append(unit))) return;
        }
    }

    /// <summary>ONE roster row, composed by the same helper the DEMAND pass measures. The head rides the FIRST row,
    /// so the list is whole or absent: a head with nothing under it says a roster exists and then names none of it.
    /// </summary>
    internal static string ComposeExcludedRow(IReadOnlyDictionary<string, string> excluded, int index)
    {
        const string head = "\nexcluded plugins (could not be parsed — NOT checked):\n";
        var kv = excluded.ElementAt(index);
        return (index == 0 ? head : "") + "  " + kv.Key + ": " + kv.Value + "\n";
    }

    /// <summary>Under <c>counts_only=</c> the reports list carries the honesty layer only (records/plugins houseCARL
    /// could not read). Emit it verbatim so a counts-only answer still names what it could not check (Q3).</summary>
    static void AppendScanErrorTail(StringBuilder sb, BoundedBody body, IReadOnlyList<PluginErrors> reports)
    {
        foreach (var p in reports)
        {
            var row = ComposeUnreadRow(p);
            if (!body.Emit(SweepSubject.UnreadRows, row.Length, () => sb.Append(row))) return;
        }
    }

    // ---- housecarl_check — the merged, multi-family response (SPEC §6.1) ---------------------------
    /// <summary>The merged sweep: ONE header, one section per selected family, each family's own accounting under
    /// its section, one boundary block, and the excluded-plugin roster once for the whole response.
    ///
    /// <para><b>The families' section renderers are the ancestors' own bodies</b> with their title, roster,
    /// accounting and boundary lifted out — so what a family says about itself here is what it said as a whole
    /// response, and the 131 arms that hold those renders hold this one's sections too.</para>
    ///
    /// <para><b>The body budget is DIVIDED, not spent in series</b> (#394, ruling item 1): one
    /// <see cref="BoundedBody"/> for the response, carrying the allocation plan, so a family that renders second
    /// does not inherit whatever the first one left. Measured on the live order at plain defaults, a serial second
    /// family inherited 400 characters of an 80,000 budget — at the no-arguments call, not at a cap anyone
    /// tightened.</para>
    ///
    /// <para><b>Each family's accounting is written under its own section</b>, and its room comes out of the
    /// reserve rather than out of the rows: <see cref="BoundedBody.Reserved"/> discounts what it wrote, so the
    /// family rendering after it is not charged for a sentence the reserve already bought.</para></summary>
    public static string RenderCheck(CheckSweep s, int maxChars, int histogramLimit = 1000)
        => RenderCheck(s, maxChars, histogramLimit, out _);

    /// <summary>The same render, handing back the ALLOCATION it built — for <c>ALLOCATION-EQUALS-SPEND</c>, which
    /// asserts against what each subject was given and what it spent rather than against anything the response
    /// printed. An internal seam: the public render is the one every caller uses.</summary>
    internal static string RenderCheck(CheckSweep s, int maxChars, int histogramLimit, out BoundedBody? measured)
    {
        measured = null;
        if (s.Error is not null) return "error: " + s.Error + (s.Epoch is not null ? $"\nepoch={s.Epoch}" : "");
        int cap = Cap(maxChars);
        var sections = s.Sections;
        var accts = s.Accountings(cap);
        // The reserve: one accounting line PER FAMILY, and ONE boundary line per family, held before anything
        // renders. Summing whole TextReserves would hold the boundary once per family — room for a sentence
        // written once, taken out of the rows that could have used it.
        int reserve = 0;
        for (int i = 0; i < accts.Count; i++)
            reserve += accts[i].TextAccountingReserve
                     + accts[i].Boundary.Length
                     + string.Format(ReadSentences.SweepBoundaryLabelFor,
                                     SweepFamilySelection.Token(sections[i])).Length + BoundaryWrap;
        int budget = Math.Max(0, cap - reserve);

        // WHAT EACH SUBJECT WANTS, measured before anything is written, so the allocation can water-fill
        // over it rather than discover shortfalls at render time (SweepDemand, BodyAllocation).
        var demand = SweepDemand.ForText(s, budget, histogramLimit);
        // AND WHAT THE RESPONSE OWES WHATEVER THE BUDGET SAYS, measured the same way: composed, not assembled.
        // The row budget has to exclude the WHOLE fixed part — the title, the scope sentence, every family's
        // section head and its own head, the "no findings" line — and not only the pieces that happen to call
        // Reserve. Left in, the allocation divides room that does not exist, the global test bites before any
        // subject reaches its share, and render order decides who loses: the order-dependence water-filling is
        // here to remove, re-entering one level up.
        var skeleton = new StringBuilder();
        var skeletonAccts = s.Accountings(cap);
        var skeletonBody = BoundedBody.Skeleton(skeletonAccts, () => skeleton.Length);
        Compose(skeleton, s, sections, skeletonAccts, skeletonBody, histogramLimit);
        int fixedPart = skeleton.Length - skeletonBody.ReservedWritten - skeletonBody.BodyTotal;

        var sb = new StringBuilder();
        var body = BoundedBody.ForFamilies(accts, budget, () => sb.Length, s.Plan(),
                                           demand.Demand, demand.Reserved + fixedPart, s.ResponseSubjects);
        measured = body;
        Compose(sb, s, sections, accts, body, histogramLimit);

        // The overrun question, asked of the FINISHED response exactly as the single-family close asks it. The
        // notice is part of the response whose length it states, so the composition runs to a fixed point.
        var response = sb.ToString().TrimEnd('\n');
        int needed = body.FixedPart(response.Length);
        // Which accounting states it: the FIRST, because the sentence is about the whole response rather than about
        // any family, and every accounting was built with the same cap. Stating it once is the point — a notice per
        // family would tell the caller three times that one response was too long.
        var overrun = accts.Count > 0 ? accts[0] : null;
        if (overrun is null) return response;
        // How many times this response prints the cap back, COUNTED in the response itself: the remedy has to name
        // a cap that already covers the characters those numbers gain when they widen.
        int sites = overrun.CapPrintsIn(response);
        if (overrun.CapTooSmall(response.Length, needed, 0, sites) is not { } notice) return response;
        var settled = overrun.CapTooSmall(response.Length + notice.Length, needed, notice.Length, sites)!;
        if (settled.Length != notice.Length)
            settled = overrun.CapTooSmall(response.Length + settled.Length, needed, settled.Length, sites)!;
        return response + settled;
    }

    /// <summary>THE WHOLE MERGED RESPONSE BAR ITS OVERRUN NOTICE, composed through one <paramref name="body"/>.
    /// Run twice per render: once with a <see cref="BoundedBody.Skeleton"/>, which refuses every unit and so leaves
    /// exactly the fixed part behind to be measured, and once for real. One routine rather than two, because a
    /// second spelling of the fixed part is a number free to drift from the response it is meant to describe.
    /// </summary>
    static void Compose(StringBuilder sb, CheckSweep s, IReadOnlyList<SweepFamily> sections,
                        IReadOnlyList<CheckAccounting> accts, BoundedBody body, int histogramLimit)
    {
        sb.Append(ReadSentences.SweepMergedTitle).Append('\n');
        // The scope sentence, above everything a budget can refuse: which families ran and which registered ones did
        // not, with the spelling that gets them. The default narrows only because the response says so (Q3).
        sb.Append(s.ScopeSentence()).Append('\n');

        // THE EXCLUDED-PLUGIN ROSTER, ABOVE THE FAMILY SECTIONS. It is part of what the scope sentence claims —
        // which plugins this response did NOT check — so it reads where the scope is stated. Its POSITION is also
        // load-bearing: every family's accounting is composed inside the loop below, and an accounting can only
        // report what has already been emitted. Written after the sections, the roster's rows were registered
        // AFTER every accounting had spoken, so each one reported none of N roster rows rendered, claimed a cut
        // that had not happened, and set truncated on a response carrying the whole roster — in both transports,
        // on every merged call with an unparseable plugin (round-1 review, found by two reviewers). Reading FIRST
        // is not the same as spending first: the roster is a response-level participant in the allocation
        // (CheckSweep.ResponseSubjects), so it takes min(its demand, lambda) of the row budget exactly as a family
        // does. Held as a reserve instead — its room subtracted from the rows, its rows spent against the
        // response-wide test, which no plan governed — it took the whole body budget before the first family head
        // was written and the fixed part landed past the cap: 4,494 chars against a 4,000 cap.
        AppendExcludedPlugins(sb, body, s.ExcludedPlugins);

        for (int i = 0; i < sections.Count; i++)
        {
            var f = sections[i];
            sb.Append('\n').Append(string.Format(ReadSentences.SweepFamilySectionHead,
                                                 SweepFamilySelection.Token(f), SweepFamilySelection.Title(f)))
              .Append('\n');
            // A FAMILY THAT REFUSED fills its own section with the refusal — the rule the dialogue family has
            // always followed, now followed by all three. A scripts-family scope refusal used to be raised to
            // response level and discard a completed errors sweep beside it: exclude= is validated against each
            // family's OWN scope, and the scripts family is handed the ACTIVE subset of plugins=, so a call
            // naming an off-order file and excluding the active one refused outright and told the caller to narrow
            // exclude= — while the errors family had swept the off-order file perfectly well (round-1 review).
            if (s.Refusal(f) is { } refusal)
            {
                sb.Append(refusal).Append('\n');
            }
            else if (f == SweepFamily.Errors)
            {
                AppendErrorsHead(sb, s.Errors!, accts[i]);
                AppendErrorsSection(sb, s.Errors!, body, histogramLimit);
            }
            else if (f == SweepFamily.Scripts)
            {
                // The off-order asymmetry sits IN this family's section, above its own counts: a "0 unbound" over a
                // scope this family could not sweep reads as "looked, found none" — the exact Q3 misreading the
                // NOT CHECKED wording exists to prevent — unless the reason is right beside it.
                if (s.OffOrderSentence() is { } skipped) sb.Append(skipped).Append('\n');
                AppendScriptsHead(sb, s.Scripts!);
                AppendScriptsSection(sb, s.Scripts!, body, histogramLimit);
            }
            else
            {
                // The dialogue family's own asymmetry sits in the same place and for the same reason: it is SEEDED,
                // so the scope parameters beside it did not narrow it, and a caller who passed plugins= would
                // otherwise read a seeded answer as a scoped one.
                DialogueSweepRender.AppendHead(sb, s);
                DialogueSweepRender.AppendSection(sb, s, body);
            }
            // This family's accounting, under this family's section, out of the room held for it.
            if (accts[i].TextLine() is { } line)
                body.Reserved(() => sb.Append('\n').Append(line).Append('\n'));
        }

        // ONE boundary block, one line per family that ran — the two families claim different things, so a single
        // sentence for both would be a claim neither of them makes. Written through the reserve, because that is
        // where its room came from: counted as body it would be charged to the rows a second time.
        for (int i = 0; i < sections.Count; i++)
        {
            int at = i;
            body.Reserved(() => sb.Append('\n')
                                  .Append(string.Format(ReadSentences.SweepBoundaryLabelFor,
                                                        SweepFamilySelection.Token(sections[at])))
                                  .Append(accts[at].Boundary).Append('\n'));
        }
    }

    /// <summary>The newlines a boundary line is wrapped in, held back with it. Two in the render; this is the same
    /// headroom the accounting's own wrap uses, per block rather than once for the lot.</summary>
    const int BoundaryWrap = 32;

    // ---- housecarl_validate_scripts -----------------------------------------------------------------
    /// <summary>Render the script-property sweep. <paramref name="histogramLimit"/> (#282) caps the
    /// <c>counts_only=</c> histogram rows.</summary>
    public static string RenderScriptCheck(ScriptCheckResult r, int maxChars, int histogramLimit = 1000)
    {
        if (r.Error is not null) return "error: " + r.Error + (r.Epoch is not null ? $"\nepoch={r.Epoch}" : "");
        int cap = Cap(maxChars);
        // The scripts family now renders through the same accounting and the same bounded body the errors family
        // does. What it replaces: an inline `sb.Length >= cap` test, a truncation marker of its own, and a boundary
        // footer appended unconditionally AFTER that test — which is why validate_scripts returned 80,673 chars
        // against its own 80,000 cap on the live order, undeclared. The footer is now inside the reserve, so the
        // overrun closes by construction rather than by a second length check.
        var acct = new CheckAccounting(r, cap);
        int budget = acct.BodyBudget(acct.TextReserve);
        var sb = new StringBuilder();

        sb.Append("validate_scripts — VMAD script-property binding sweep\n");
        AppendScriptsHead(sb, r);
        var body = new BoundedBody(acct, budget, () => sb.Length);
        AppendScriptsSection(sb, r, body, histogramLimit);
        AppendExcludedPlugins(sb, body, r.ExcludedPlugins);
        return Close(sb, acct, body);
    }

    /// <summary>The scripts family's own head: what it swept and what it found. Every count states its own scope —
    /// a class the caller excluded reads NOT CHECKED and never 0, and the two counts <c>property_contains=</c>
    /// narrows carry their own label — so no number here can be read as a wider claim than it is.</summary>
    static void AppendScriptsHead(StringBuilder sb, ScriptCheckResult r)
    {
        bool didObject = r.Classes.HasFlag(ScriptFindingClass.UnboundObject);
        bool didScalar = r.Classes.HasFlag(ScriptFindingClass.UnboundScalar);
        bool didNull = r.Classes.HasFlag(ScriptFindingClass.BoundNull);

        sb.Append("scanned ").Append(r.PluginsScanned).Append(r.PluginsScanned == 1 ? " plugin · " : " plugins · ")
          .Append(r.RecordsWithScripts).Append(" record(s) with scripts · ")
          // A class the caller excluded reads as NOT CHECKED, never as a 0 — a 0 would say "looked, found none" about
          // the HIGH silent-None class nobody looked for (PR #288 review, finding 1).
          .Append(ReadSentences.ScriptUnboundTotal(r, didObject, didScalar))
          .Append(" · ")
          .Append(ReadSentences.ScriptNullTotal(r, didNull))
          .Append(" · ")
          .Append(r.TotalUnverifiable).Append(" unverifiable");
        if (r.ExcludedPlugins.Count > 0)
            sb.Append(" · ").Append(r.ExcludedPlugins.Count).Append(" plugin(s) excluded (unparseable)");
        if (r.Epoch is not null) sb.Append(" · epoch=").Append(r.Epoch).Append(EpochOffOrderQualifier(r));
        sb.Append('\n');
        if (r.FilterNote is not null) sb.Append(r.FilterNote).Append('\n');
        if (r.ReadIncomplete)
            sb.Append("note: a BSA failed to read this build — a '.pex not on disk' below may merely be unscanned, not truly absent (Q3).\n");
    }

    /// <summary>The scripts family's BODY — everything a cap can refuse. Like the errors family's, it writes no
    /// roster, no accounting and no boundary: those are the response's.</summary>
    static void AppendScriptsSection(StringBuilder sb, ScriptCheckResult r, BoundedBody body, int histogramLimit)
    {
        if (r.CountsOnly)
        {
            AppendHistograms(sb, body, histogramLimit,
                ScriptsAxes(r));
            // The honesty layer: plugins whose record enumeration faulted. Its own subject, so a response that could
            // not carry every row states how many it named instead of stopping with a bare marker.
            foreach (var rec in r.Reports)
            {
                if (rec.ScanError is null) continue;
                var row = ComposeScriptRecordUnit(rec);
                if (!body.Emit(SweepSubject.ScriptScanRows, row.Length, () => sb.Append(row))) break;
            }
            return;
        }

        if (r.Reports.Count == 0 && r.ExcludedPlugins.Count == 0)
            sb.Append("\nNo unbound script properties found in the scanned scope.\n");

        foreach (var rec in r.Reports)
        {
            // A RECORD SECTION IS EMITTED WHOLE, OR NOT AT ALL — the errors family's rule, in this family's units.
            // Everything inside one is a finding in its own right (an unbound property, the bound-but-null advisory,
            // a "could not verify" note), and the per-line "append if it fits" this replaces dropped them with no
            // subject accounting for the loss: half a record's findings under a header claiming the whole record.
            var section = ComposeScriptRecordUnit(rec);
            if (!body.Emit(SweepSubject.ScriptRecords, section.Length, () => sb.Append(section))) break;
        }
    }

    /// <summary>One record's whole section, composed before it is offered to the budget — the same construction the
    /// errors family's plugin sections use, and for the same reason: a unit measured before the write is a unit the
    /// response cannot land over its cap with.</summary>
    internal static string ComposeScriptRecordUnit(RecordScriptFindings rec)
    {
        if (rec.ScanError is not null)
            return "\n[SCAN ERROR] " + rec.Plugin + ": " + rec.ScanError + "\n";

        var sb = new StringBuilder();
        sb.Append('\n').Append(rec.Unbound.Count > 0 ? "[UNBOUND] " : "[CHECK] ")
          .Append(rec.Record).Append(" (").Append(rec.RecordType);
        if (!string.IsNullOrEmpty(rec.EditorId)) sb.Append(" '").Append(rec.EditorId).Append('\'');
        sb.Append(") in ").Append(rec.Plugin).Append('\n');

        // Unbound findings, object/form types (silent None) FIRST, then uninitialized scalars.
        foreach (var u in rec.Unbound.OrderByDescending(u => u.IsObjectType))
        {
            sb.Append("  ").Append(u.IsObjectType ? "! " : "· ")
              .Append(u.PropertyName).Append(" (").Append(u.PexTypeName).Append(") on script ").Append(u.Script);
            if (!string.Equals(u.DeclaringScript, u.Script, StringComparison.OrdinalIgnoreCase))
                sb.Append(" [declared in ").Append(u.DeclaringScript).Append(']');
            sb.Append(u.IsObjectType
                ? " — declared but NOT bound → None at runtime (HIGH: object/form type — the silent no-op)\n"
                : " — declared but NOT bound → defaults to 0/false/\"\" (scalar, no baked default)\n");
        }
        if (rec.NullObjects.Count > 0)
            sb.Append("  bound-but-null object propert").Append(rec.NullObjects.Count == 1 ? "y: " : "ies: ")
              .Append(string.Join(", ", rec.NullObjects.Select(n => $"{n.PropertyName} ({n.Script})")))
              .Append("   [advisory — a None link; sometimes intentional, filled at runtime]\n");
        foreach (var uv in rec.Unverifiable)
            sb.Append("  could not verify script ").Append(uv.Script).Append(": ").Append(uv.Reason).Append('\n');
        return sb.ToString();
    }

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
