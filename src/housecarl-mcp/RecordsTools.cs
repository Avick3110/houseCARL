using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;
using Mutagen.Bethesda.Plugins;

namespace HousecarlMcp;

/// <summary>
/// housecarl_records — the read surface. One tool: SELECT (which records), SOURCE (whose version), PROJECT (what
/// shape) and TRANSPORT compose in a single call, over the same engine lanes the older read tools drove. Ten
/// project forms — identity, summary, fields, rows, everything, aggregate, delta, tree, chain, info_order — each
/// form-scoped, so a sub-parameter exists only inside the form that carries it.
/// </summary>
[McpServerToolType]
public static class RecordsTools
{
    /// <summary>The plugins= SELECT scope: which records are considered — a different question from source=, which
    /// decides whose version is read. defined_in lives inside the scope because it has no meaning without
    /// one.</summary>
    public sealed class RecordsScope
    {
        [Description("Plugin filenames to scope the scan to (records those plugins touch), e.g. [\"Requiem.esp\"].")]
        public string[]? names { get; set; }

        [Description("When true, keep only records DEFINED IN (originating from) the named plugins, dropping records they merely override.")]
        public bool defined_in { get; set; }
    }

    /// <summary>PROJECT — the shape of the answer. One form; sub-parameters exist only inside the forms that carry
    /// them, so there is no flat spelling for an illegal pairing.</summary>
    public sealed class RecordsProject
    {
        [Description("The form: 'identity' (FormID -> type/editorid/name/winner — the labeling form; needs formids=) | 'summary' (identity plus winner/override-depth header facts — the default) | 'fields' (named field values; takes fields= and depth=) | 'rows' (a LIST field folded to ONE LINE PER ELEMENT — the compact per-row view: takes fields= naming the list (index one element, 'Conditions[0]', to fold just that one), and depth= (default 4). Each line is the element's own summary plus every sub-field the read FOUND; only ABSENT optionals are omitted, which is what turns a 40-row condition stack from ~1,000 lines into 40. A declared-but-null link is kept — an empty slot is a fact. A named field that is not a list fails loud) | 'everything' (the full record body; takes depth=) | 'aggregate' (a counted table; takes group_by=) | 'delta' (subject vs reference, differences only — source= is the subject, versus= the reference; takes fields= to narrow) | 'tree' (every provider of each record in priority order, winner last, each diffed against the reference pole — default the winner; takes fields=. On a record type that OWNS child records — a cell's placed references, a topic's INFO lines, a worldspace's cells — it also states, per such field, which providers DECLARE children there (a COLLECTION field) or how many do (a SINGULAR one, e.g. Cell.Landscape), and says so when none do) | 'info_order' (DIAL topics only: the effective MERGED INFO sequence across every touching plugin — the order the game walks, with MOVED annotations; the 'why does the wrong line play' diagnostic) | 'chain' (a walk's own paths, endpoints and cycles rather than the records it reached; needs walk=, and carries the NPC-template inheritance report and the reverse MGEF carrier rows).")]
        public string? form { get; set; }

        [Description("fields/rows forms: dotted field paths to read, e.g. [\"BasicStats.Damage\", \"Keywords\", \"Effects\"]. Index a list/dict element with BRACKETS ('Effects[0].Data.Magnitude'). A path may LEAD with the containment step '*parent' — the record that CONTAINS this one, which group nesting makes invisible to references= ('*parent.EditorID' is an INFO's owning DIAL; '*parent.*parent.EditorID' a placed reference's worldspace) — and it chains. On the rows form these name the LIST(S) to fold, one line per element. where='s quantifier tokens are not read here: [*any]/[*all]/[*none] fold to a boolean, which is not a row, and [*] / [*count] as a PROJECTION are not built yet — both are refused by name.")]
        public string[]? fields { get; set; }

        [Description("fields/rows/everything forms: expansion depth for list/dict/substruct CONTENTS (default 1, or 4 on the rows form, where a shallower read renders every element as a bare type). THIS is the expansion knob: fields=[\"Effects\"], depth=4 reaches every effect's Magnitude/Area/Duration — no hand-written index guessing.")]
        public int? depth { get; set; }

        [Description("aggregate form only: the count key — 'winner' (by winning plugin), 'type' (by record type; needs types= or plugins=), or 'defined_in' (by defining plugin).")]
        public string? group_by { get; set; }

        [Description("fields/rows/everything forms: annotate every FormLink value with its target's identity (-> editorid \"Name\"). Display-only — the token itself still round-trips to a write.")]
        public bool resolve_names { get; set; }
    }

    /// <summary>The traversal construct: follow record-to-record form links and select what the walk reaches. This
    /// traverses BETWEEN records; expanding nested fields within one record is project.depth. Seeds are this
    /// call's own SELECT.</summary>
    public sealed class RecordsWalk
    {
        [Description("Link-bearing field paths that start the walk from each seed, e.g. [\"HeadParts\", \"WornArmor\"]. '*parent' crosses the containment edge instead — the record that CONTAINS the seed (a REFR from a crash log to its CELL; '*parent.*parent' to the worldspace). Omit for every link on the seed.")]
        public string[]? seed_paths { get; set; }

        [Description("The link path followed at every LATER hop. \"*\" (default) walks every link — full closure. A named path restricts to one chain, e.g. \"Template\" for NPC template inheritance, or \"*parent\" to climb containment.")]
        public string? follow { get; set; }

        [Description("'forward' (default) — what the seeds point AT (cheap: each hop is one link resolve). 'reverse' — what points AT the seeds; depth 1 only (transitive reverse is not wired to the reverse-reference index on this lane yet, so depth>1 is still refused). The general reverse spelling on this surface IS references=, which needs no bounding scope; walk.direction='reverse' serves the typed MGEF lane — magic-effect seeds get per-carrier magnitude/area/duration (types= narrows the carrier types; walk.max_nodes bounds each seed's carrier rows; limit=/offset= window the SEEDS).")]
        public string? direction { get; set; }

        [Description("Maximum hops from a seed (default 16). Nodes AT the cap are recorded, not entered, and the response says the cap cut the walk — never a silent stop.")]
        public int? depth { get; set; }

        [Description("Maximum nodes reached per seed (default 2000, the read-expansion budget). A breach keeps what was proved and says so.")]
        public int? max_nodes { get; set; }

        [Description("Node classes the walk must not enter, as data: [{\"match\": \"Race\", \"severity\": \"stop\"|\"refuse\"}] — match is the record type name a read reports; stop prunes there (recorded as a boundary), refuse fails the whole call loud.")]
        public RecordsWalkExclusion[]? exclusions { get; set; }
    }

    public sealed class RecordsWalkExclusion
    {
        [Description("The record type name to match (as reads report it, e.g. 'Race', 'Npc').")]
        public string? match { get; set; }

        [Description("'stop' (prune here, record the boundary) or 'refuse' (the whole walk fails loud).")]
        public string? severity { get; set; }
    }

    [McpServerTool(Name = ToolNames.Records, ReadOnly = true, Title = "Read records (the 2.0 read surface)"),
     Description(
         "Read Bethesda records from the load order — ONE read surface: which records (SELECT) x whose version " +
         "(SOURCE) x what shape of answer (PROJECT) compose in a single call.\n\n" +
         "A FormID is 'XXXXXX:Plugin.esp' — 6 hex digits, a colon, the defining master's filename. The RUNTIME " +
         "form the game, the console, Papyrus logs, SKSE logs and crash logs print is accepted too: eight hex " +
         "digits and no plugin name, 'FExxxYYY' for a light plugin or 'XX######' for a full one, with or without " +
         "a leading 0x, resolved against the CURRENT load order — the response names the plugin it resolved to, " +
         "and prints each record's runtime FormID beside its own. It is taken wherever a parameter holds nothing " +
         "but FormIDs (formids=, references=, walk seeds, and a where= 'formid in [...]' list); a where= operand " +
         "compared against a field also holds numbers and enum names, so it takes the plugin-qualified form only. " +
         "Every " +
         "list-valued parameter is set-valued (one item is a set of one), and formids=/references= accept a single " +
         "\"@<absolute path>\" element to read the list from a file — including a spilled result artifact from an " +
         "earlier call (its identity column becomes the list, epoch-checked against the then-current build).\n\n" +
         "SELECT: formids= | types= | plugins= (scope: which records are considered) | conflicts_only= | where= " +
         "(body predicates, ANDed: comparisons 'BasicStats.Damage >= 50', 'editorid contains Iron', 'editorid " +
         "startswith REQ_', flag tests 'BodyTemplate.FirstPersonFlags has Body' — every operand bit set — with " +
         "'has_any' (at least one set) and 'has_none' (none set) as the other two folds over the same bits, " +
         "QUANTIFIED STEPS over a list field: 'Conditions[*any].Data.Function = IsGuard', " +
         "'Effects[*none].BaseEffect->editorid startswith REQ_' (absence, proved), 'Effects[*count] > 2' — " +
         "[*any]/[*all]/[*none] fold the elements into a boolean, [*count] into their number, and [*all] is " +
         "vacuously true on an empty list; a quantified step COSTS the list's length (per-candidate work times the " +
         "number of elements) and, where its sub-path carries '->', one winner fetch PER ELEMENT — " +
         "'Temporary[*any]->…' on a dense cell is hundreds of fetches per candidate; the CONTAINMENT step " +
         "'*parent' — the record that CONTAINS this one, " +
         "which group nesting makes invisible to references=: '*parent.EditorID = GreetingsTopic' is an INFO's " +
         "owning DIAL, '*parent.*parent.EditorID = Tamriel' a placed reference's worldspace; it LEADS a path and " +
         "chains, presence 'VirtualMachineAdapter " +
         "exists', membership 'formid not in @<file>' / 'Race in [XXXXXX:A.esm, YYYYYY:B.esm]' (list entries " +
         "separate on commas/newlines with brackets and quotes stripped — a value that itself contains a comma " +
         "or bracket is not expressible in a list; test it with '='), ONE '->' link step " +
         "'Perks->editorid startswith REQ_NULL_', and the provenance term 'winner = X.esp' — which records does X " +
         "WIN; that term forces winner resolution over the scanned scope, the same declared cost as any winner " +
         "scan) | references= (reverse, one step; needs no bounding scope — unbounded, it is answered off the " +
         "reverse-reference index, which is built on the first such call, costs one whole-order link-walk, and " +
         "reports that cost and its own per-plugin freshness key in the response. A bounded references= is " +
         "unchanged and still cheaper. A '!' before an entry NEGATES it — " +
         "references=[\"!XXXXXX:A.esm\"] keeps only records that do NOT reference that target, and plain and " +
         "negated entries in one call compose by AND; the sigil takes the @file spelling too — " +
         "references=[\"!@C:/work/targets.jsonl\"] excludes every target the file names. A negated entry ALONE " +
         "with no types=/plugins= scope is the " +
         "ORPHAN sweep: the universe becomes every record nothing in the order references, and the named target " +
         "then excludes any of those that link it — bound the call if you meant the narrower question). " +
         "UNION-ARM tip: when a field is one of " +
         "several shapes (an NPC's Configuration.Level is a fixed level OR a PC-level multiplier), a scalar " +
         "predicate on one arm's sub-field doubles as an ARM-PRESENCE test: where=[\"Configuration.Level.LevelMult " +
         ">= 0\"] returns exactly the NPCs on a multiplier. formids= COMPOSES with the scan terms: the identity set " +
         "intersects the scan — or, alone, IS the scan universe (the set is the bound, so a where= over it needs " +
         "no types=/plugins=). The walk= construct is a SELECT term too: what it reaches is a selection any " +
         "reading form can consume.\n\n" +
         "SOURCE decides WHOSE version you read. Default: the load-order winner. Naming a plugin (source= " +
         "\"OldPatch.esp\", or {\"file\": \"X.esp\", \"mod\": \"<mod folder>\"} when two mods ship the filename) " +
         "reads THAT plugin's version wherever the plugin lives — active in your order, or sitting on disk unticked " +
         "— you do not have to know which, and the response STATES which arm resolved (active, or out-of-load-order " +
         "and from where). A plugin found in neither place is refused naming both places searched. A record the " +
         "named plugin does not touch is refused naming the plugins that DO touch it — never silently absent. " +
         "An off-order file's content sits OUTSIDE the epoch fingerprint and the response says so. " +
         "versus= is the comparison REFERENCE pole (delta/tree); {\"overlay\": \"skypatcher\", \"state\": " +
         "\"pre\"|\"post\"} reads around the SkyPatcher INI layer; \"previous_provider\" (a versus= value) is the " +
         "plugin immediately below the SUBJECT in the record's touching stack.\n\n" +
         "PROJECT is a single form (see project=): identity | summary (default) | fields | rows | everything | aggregate | " +
         "delta | tree | chain | info_order. Sub-parameters live INSIDE the form that uses them (depth belongs to fields/rows/everything, " +
         "group_by to aggregate, fields to fields/rows/delta/tree) — there is no flat spelling for an illegal pairing. " +
         "form='rows' is the compact per-row view of a LIST field: project.fields names the list and every element folds to ONE " +
         "line — the element's own summary plus each sub-field the read found, ABSENT optionals omitted. Auditing a " +
         "40-row condition stack (project.fields=[\"Conditions\"]) is one call, not an index probe per row; an indexed path " +
         "(project.fields=[\"Conditions[0]\"]) folds that one element, and a field that is not a list is refused by name. " +
         "COMPARISONS (form='delta'/'tree'): a delta reads the SUBJECT (source=) and a REFERENCE (versus=) and " +
         "returns only what differs — each delta line shows the subject's value with the reference's labeled by its " +
         "plugin; versus=\"previous_provider\" answers 'what did this plugin change relative to what sat beneath " +
         "it'. A tree lists EVERY plugin touching each record (load order, winner last) with each provider's " +
         "only-the-fields-that-differ against the reference (default the winner) — the conflict-resolution view. " +
         "Both compare by the content-keyed, truncation-honest engine (list reorders flagged; a truncated deep " +
         "read is reported, never claimed 'identical'). form='info_order' (DIAL topics) renders the effective " +
         "MERGED INFO sequence across every plugin touching each topic — the game walks it top to bottom and " +
         "plays the FIRST passing line; re-listing a line appends it to the BOTTOM unless the plugin also carries " +
         "its PNAM, so a reorder changes which line answers while every field stays identical (invisible to a " +
         "diff — this form is how you see it). A quest's topics select by composition: types=[\"DIAL\"] " +
         "where=[\"Quest = <quest formid>\"].\n\n" +
         "TRANSPORT: format= 'text' | 'json' | 'dense' (scan lane: positional columnar cells 1:1 with the requested " +
         "fields — by that definition depth expansion and the everything form are inexpressible in it); limit=/" +
         "offset= page a scan in exact windows WITHIN one epoch (different epochs across pages ⇒ the order changed " +
         "— re-run from offset=0, do not stitch); counts_only= returns the accounting without rows; max_chars= caps " +
         "the RENDER — an over-cap result is not truncated, it SPILLS in full to a server-side JSONL artifact " +
         "(line 1 = manifest with the query echo, row schema, and epoch) and the response names the file; to_file= " +
         "forces that disposition to your own path. Every response carries epoch=<hex> — the identity of the index " +
         "build it was answered from.\n\n" +
         "This tool never writes. Authoring goes through the write tools (" + ToolNames.Apply + " / " + ToolNames.Create + " / " +
         ToolNames.Remove + " / " + ToolNames.Forward + ").")]
    public static string Records(
        LoadOrderService svc,
        [Description("SELECT: records by FormID ('XXXXXX:Plugin.esp', or the runtime form a log or the console prints — 'FExxxYYY' / 'XX######'), or [\"@<absolute path>\"] to read the list from a file / spilled artifact. Results return in input order; a bad or absent FormID is a per-item error, never a failed batch.")]
            string[]? formids = null,
        [Description("SELECT: record types — signatures ('WEAP') or catalog names ('Weapon'); the scan streams the UNION. types alone enumerates every record of those types in whatever the SOURCE names.")]
            string[]? types = null,
        [Description("SELECT: the plugin SCOPE — which records are CONSIDERED (records these plugins touch). Not the same question as source= (whose VERSION is read).")]
            RecordsScope? plugins = null,
        [Description("SELECT: keep only records touched by more than one plugin (the contested set).")]
            bool conflicts_only = false,
        [Description("SELECT: body predicates, ANDed — see the tool description for the full grammar (comparisons, contains/startswith, has, exists/missing, in/'not in' membership incl. @file and artifact re-entry, the '->' link step, the 'winner' provenance term, and the 'editorid' term that replaces editorid_contains=). A body scan — must be combined with types= or plugins= to bound the work. A wrong path is reported loud, never a silent '0 matches'. Paths are scalar leaves: step into a list element with BRACKETS ('Effects[0].Data.Magnitude'), never a dotted hop; a WILDCARD over a list ('Effects[*].Magnitude') is a known future capability and is not built.")]
            string[]? where = null,
        [Description("Which BODY the where= predicates decide the MATCH on: 'scoped' (default — the body the scan streams) or 'winner' (the live load-order winner regardless of scan scope; the post-patch audit answer). Match only — fields_source= independently governs display.")]
            string? where_source = null,
        [Description("SELECT: find records that REFERENCE these FormIDs (reverse, one step; OR over the list, each match names which target(s) it hit). Needs no bounding scope: unbounded it is answered off the reverse-reference index, built on the first such call at the cost of one whole-order link-walk, declared in the response — see the tool description. A bounding types= or plugins= is still cheaper. Accepts [\"@<path>\"] like formids=.")]
            string[]? references = null,
        [Description("SOURCE: whose version to read — the SUBJECT of the call. Omit or \"winner\" for the load-order winner; a plugin filename (e.g. \"OldPatch.esp\") for that plugin's version WHEREVER it lives — active or on disk out of the order (the response states which); {\"file\": \"X.esp\", \"mod\": \"<mod folder>\"} when two mods ship the same filename; {\"overlay\": \"skypatcher\", \"state\": \"pre\"|\"post\"} for the runtime view around the SkyPatcher INI layer (post = after it replays; INI content sits OUTSIDE the epoch fingerprint and the response says so). \"previous_provider\" is a versus= value only — it is measured FROM the subject this parameter names.")]
            JsonElement? source = null,
        [Description("SOURCE (comparison forms): the REFERENCE pole a delta/tree compares against. Same forms as source= — \"winner\" | a plugin filename | {\"file\", \"mod\"} | {\"overlay\", \"state\"} — plus \"previous_provider\": the plugin immediately below the SUBJECT (whatever source= names) in the record's touching stack, measured FROM THE SUBJECT, never from the winner. Its four cases are all declared: subject=winner → next plugin down; subject mid-stack → still the one below the SUBJECT, with what sits above reported as plain fact (a mid-stack patch is ordinary practice, not judged); subject defines the record → refused naming it (never an empty diff that reads as 'no changes'); subject doesn't touch it → refused naming the actual touchers. REQUIRED when project.form='delta'; defaults to \"winner\" on 'tree'; refused on other forms.")]
            JsonElement? versus = null,
        [Description("The pole field VALUES display from, when it differs from the matching pole: \"winner\" shows the live winner's values on a plugins=-scoped scan (the old winner_fields=true). Display only — where_source= governs matching.")]
            string? fields_source = null,
        [Description("PROJECT: the shape of the answer — a single form plus its own sub-parameters. Omit for summary rows.")]
            RecordsProject? project = null,
        [Description("SELECT: the traversal construct — follow record-to-record links from this call's own SELECT (the seeds) and select what the walk reaches; project.form='chain' renders the paths/endpoints/cycles themselves (with, for NPC template chains, the per-category active-vs-masked inheritance report), while any other form reads the reached set like any selection. The walk expands on the WINNER's link graph; source= governs whose version the form then reads.")]
            RecordsWalk? walk = null,
        [Description("TRANSPORT: 'text' (default) | 'json' (machine-readable document; same accounting in-band) | 'dense' (scan lane: columnar positional rows — the compact bulk-enumeration form).")]
            string? format = null,
        [Description("TRANSPORT: max rows to render (default 500). The TRUE total is always reported; page with offset=. DECLARED COST: the derived-selection forms (delta/tree/chain/info_order, and any walk) consume EVERY scan match — their censuses and artifacts cover the full selection, and limit= windows only the rendered rows — so on a big order the SCAN TERMS (types=/plugins=/where=) are the cost bound: narrow them.")]
            int limit = 500,
        [Description("TRANSPORT: skip the first N matches (exact windows: offset=0/500/1000…). Windows tile only within one epoch — if two pages' epochs differ the load order changed mid-pagination; re-run from offset=0.")]
            int offset = 0,
        [Description("TRANSPORT: character ceiling on the RENDER. Never truncates the result — an over-ceiling result spills to a JSONL artifact in full and the response names the file. 0 = the server default (~80k).")]
            int max_chars = 0,
        [Description("TRANSPORT: return the accounting block and counts only, no rows — the cheap census.")]
            bool counts_only = false,
        [Description("TRANSPORT: write the COMPLETE result to this ABSOLUTE .jsonl path as an artifact (line 1 = manifest) and render only the manifest inline. Re-enter it later via formids=[\"@<path>\"] or where=[\"formid in @<path>\"] — epoch-checked. The artifact is never a window: offset= is refused with to_file=.")]
            string? to_file = null) => Guard.Tool(ToolNames.Records, () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;

        // ---- TRANSPORT: format --------------------------------------------------------------------------
        var fmt = Wire.CrossQueryFormat(format, out var ferr);
        if (ferr is not null) return ferr;
        bool json = fmt is Wire.QueryFormat.Json;
        bool dense = fmt is Wire.QueryFormat.Dense;

        // ONE FormID door for the whole call, shared by every lane below: formids=, references= and the walk seeds
        // all resolve against one index build, and the scan then runs on that same build. A door per list would let
        // a freshness rebuild land between two lists, so a call's own tokens could name records in different orders.
        var door = svc.OpenFormIdDoor();

        // ---- PROJECT: form + form-scoping ---------------------------------------------------------------
        var form = project?.form?.Trim().ToLowerInvariant() ?? "summary";
        switch (form)
        {
            case "identity" or "summary" or "fields" or "rows" or "everything" or "aggregate" or "delta" or "tree" or "info_order" or "chain": break;
            default:
                return Wire.Refuse(json, $"error: project.form='{project?.form}' is not a form — use identity | summary | fields | rows | everything | aggregate | delta | tree | chain | info_order.");
        }
        bool comparisonForm = form is "delta" or "tree";
        bool bodyFields = form is "fields" or "rows";   // the two forms that read the caller's own field paths
        // Sub-parameters exist only inside their forms; a stray one is refused by name, so the caller learns the
        // form-scoping rule instead of getting a silently ignored knob.
        if (project?.fields is { Length: > 0 } && !bodyFields && !comparisonForm)
            return Wire.Refuse(json, $"error: project.fields belongs to the 'fields'/'rows'/'delta'/'tree' forms (got form='{form}'). Set project.form, or drop fields.");
        if (form == "fields" && project?.fields is not { Length: > 0 })
            return Wire.Refuse(json, "error: the 'fields' form names its field paths — pass project.fields=[\"<path>\", …] (or use form='everything' for the full body).");
        if (form == "rows" && project?.fields is not { Length: > 0 })
            return Wire.Refuse(json, "error: the 'rows' form folds a LIST field to one line per element and names that field — pass project.fields=[\"Conditions\"] (or any list path).");
        // Every entry has to be a path: the fold reads the roots themselves to decide what a line belongs to, so a
        // null or blank one is bad input and is named as such rather than reaching the fold.
        if (form == "rows" && project?.fields is { } rowFields && Array.FindIndex(rowFields, p => string.IsNullOrWhiteSpace(p)) is var badAt && badAt >= 0)
            return Wire.Refuse(json, $"error: project.fields[{badAt}] is empty — the 'rows' form folds the list each entry names, so every entry must be a field path (e.g. [\"Conditions\"]).");
        // The quantifier tokens are where='s, not a projection's — refused by name here so a caller who tries one
        // gets the rule rather than an unreadable-field note from the read walk.
        if (project?.fields is { Length: > 0 } pf)
            foreach (var path in pf)
                if (QuantifierToken(path) is { } qt)
                {
                    var qtl = qt.ToLowerInvariant();
                    // A token outside the vocabulary is a typo, not a capability that is coming — say so in the
                    // parser's own words, so where= and project.fields judge the same token the same way.
                    if (qtl is not ("[*]" or "[*count]" or "[*any]" or "[*all]" or "[*none]"))
                        return Wire.Refuse(json, $"error: project.fields path '{path}': '{qt}' is not a quantifier — the tokens are [*], [*count], [*any], [*all] and [*none] (the last three fold to a boolean and belong in where=).");
                    return Wire.Refuse(json, qtl is "[*any]" or "[*all]" or "[*none]"
                        ? $"error: project.fields path '{path}' folds the elements into a boolean, and a boolean is not a row — use {qt} in where= to SELECT the records, and name a concrete element ('Effects[0].Data.Magnitude') or the list itself to read one."
                        : $"error: project.fields path '{path}' uses '{qt}', which is a where= step today — the projection half of the quantified step (a row per element, and the element count as a cell) is not built yet. Name a concrete element ('Effects[0].Data.Magnitude') or the list itself, which renders as a summary with project.depth.");
                }
        if (project?.depth is { } dv)
        {
            // Any explicit depth is form-scoped — the rule must not depend on the value, or depth:1 is accepted
            // and dropped where depth:2 refuses — and 0 or negative is refused rather than silently becoming 1.
            if (form is not ("fields" or "rows" or "everything"))
                return Wire.Refuse(json, comparisonForm
                    ? $"error: project.depth belongs to the 'fields'/'rows'/'everything' forms — the '{form}' comparison always deep-reads BOTH sides at the diff engine's fixed depth so line sets correspond (narrow with {LeverNames.Records.Fields} instead)."
                    : $"error: project.depth expands field contents and belongs to the 'fields'/'rows'/'everything' forms (got form='{form}').");
            if (dv < 1)
                return Wire.Refuse(json, $"error: project.depth={dv} — depth must be >= 1 (1 shows a container as a collapsed summary; higher opens it).");
            // depth=1 collapses the list to a count, so the rows form would answer with no rows at all.
            if (dv == 1 && form == "rows")
                return Wire.Refuse(json, "error: project.depth=1 collapses a list to a count, and the 'rows' form renders its elements — pass depth >= 2 (2 shows each element's type, the default 4 reaches its sub-fields), or use form='fields' for the collapsed line.");
        }
        if (project?.group_by is not null && form != "aggregate")
            return Wire.Refuse(json, $"error: project.group_by belongs to the 'aggregate' form only (got form='{form}'). Set project.form='aggregate', or drop group_by.");
        if (form == "aggregate")
        {
            // Validated here, before any read runs — validating inside the render pays for the batch first.
            var gbv = project?.group_by?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(gbv))
                return Wire.Refuse(json, "error: the 'aggregate' form names its count key — pass project.group_by='winner' | 'type' | 'defined_in'.");
            if (gbv is not ("winner" or "type" or "defined_in"))
                return Wire.Refuse(json, $"error: project.group_by='{project!.group_by}' is not a count key — use 'winner', 'type', or 'defined_in'.");
            // The in-order engine refuses this too, but in the 1.x spelling (group_by=/type=), so the pre-check
            // here says the same thing in this tool's levers. It is deliberately NOT applied to the off-order
            // lane: there the file's own records ARE the universe, every match has a body, and OffOrderQuery
            // imposes no such precondition — pre-refusing it would state a false reason. The lane is not known
            // until the source pole is probed, so the check lives with the probe rather than here.
        }
        if (project is { resolve_names: true } && form is not ("fields" or "rows" or "everything"))
            return Wire.Refuse(json, $"error: project.resolve_names annotates field values and belongs to the 'fields'/'rows'/'everything' forms (got form='{form}').");
        // The rows form's default is its own: at depth 1 every element renders as a bare arm type, which is the
        // gap the form exists to close. Its cost is per-row TEXT, not per-row lines — the fold is one line either way.
        int depth = project?.depth is { } d && d > 0 ? d : (form == "rows" ? RowProjection.DefaultDepth : 1);
        var projFields = bodyFields || comparisonForm ? project?.fields : null;
        bool resolveNames = project?.resolve_names ?? false;
        // The lever vocabulary is a function of (tool, FORM), not of the tool alone: the 'everything' form refuses
        // project.fields= by name, so a truncation notice there must not offer it as the way to narrow.
        var formLevers = form == "everything" ? LeverNames.Records.WithoutFieldSelector() : LeverNames.Records;
        // The rows form IS the fields form plus this fold, applied wherever a lane produces bodies — so the
        // render, the artifact and the json document all see the same folded rows.
        IReadOnlyList<ReadOutcome> FoldRows(IReadOnlyList<ReadOutcome> read)
            => form == "rows" ? RowProjection.Apply(read, projFields!, depth) : read;

        // ---- SOURCE: the pole grammar (source = the subject; versus = the comparison reference) ----
        // ParsePole has no transport in scope, so its refusals take their shape here.
        if (ParsePole(source, "source", subjectRole: true, out var srcSpec) is { } sperr) return Wire.Refuse(json, sperr);
        srcSpec ??= LoadOrderService.PoleSpec.Winner;
        if (ParsePole(versus, "versus", subjectRole: false, out var versusSpec) is { } vperr) return Wire.Refuse(json, vperr);

        // versus= belongs to the comparison forms, and the delta form requires it — a delta has two poles.
        if (versusSpec is not null && !comparisonForm)
            return Wire.Refuse(json, $"error: versus= is the comparison REFERENCE pole and belongs to the 'delta'/'tree' forms (got form='{form}') — set project.form='delta' (subject vs reference) or 'tree' (every provider vs reference), or drop versus=.");
        if (form == "delta" && versusSpec is null)
            return Wire.Refuse(json, "error: the 'delta' form compares the subject (source=, default the winner) against a REFERENCE — pass versus= (\"winner\" | a plugin filename | \"previous_provider\" | {\"overlay\": …}).");
        if (form == "tree")
        {
            versusSpec ??= LoadOrderService.PoleSpec.Winner;
            if (versusSpec.Kind == LoadOrderService.PoleKind.PreviousProvider)
                return Wire.Refuse(json, "error: versus='previous_provider' is subject-relative and pairs with the 'delta' form (one subject, one reference below it) — a tree diffs EVERY provider against ONE reference pole. Use form='delta', or a named/winner versus= on the tree.");
        }
        // ---- walk= (the traversal construct) ----
        string walkDirection = "forward";
        int walkDepth = 16, walkMaxNodes = 2000;
        var walkExclusions = new List<(string Match, bool Refuse)>();
        if (walk is not null)
        {
            var dir = walk.direction?.Trim().ToLowerInvariant();
            if (dir is not (null or "" or "forward" or "reverse"))
                return Wire.Refuse(json, $"error: walk.direction='{walk.direction}' — use 'forward' (what the seeds point at) or 'reverse' (what points at them; depth 1).");
            if (!string.IsNullOrEmpty(dir)) walkDirection = dir!;
            if (walk.depth is { } wd)
            {
                if (wd < 1) return Wire.Refuse(json, $"error: walk.depth={wd} — depth must be >= 1 (hops from the seed).");
                walkDepth = wd;
            }
            if (walk.max_nodes is { } wn)
            {
                if (wn < 1) return Wire.Refuse(json, $"error: walk.max_nodes={wn} — the node budget must be >= 1.");
                walkMaxNodes = wn;
            }
            foreach (var x in walk.exclusions ?? Array.Empty<RecordsWalkExclusion>())
            {
                if (string.IsNullOrWhiteSpace(x.match))
                    return Wire.Refuse(json, "error: a walk.exclusions entry needs match= — the record type name a read reports (e.g. 'Race').");
                var sev = x.severity?.Trim().ToLowerInvariant();
                if (sev is not ("stop" or "refuse"))
                    return Wire.Refuse(json, $"error: walk.exclusions '{x.match}': severity='{x.severity}' — use 'stop' (prune, record the boundary) or 'refuse' (the whole walk fails loud).");
                walkExclusions.Add((x.match!.Trim(), sev == "refuse"));
            }
            if (walkDirection == "reverse")
            {
                if (walk.depth is > 1)
                    return Wire.Refuse(json, "error: walk.direction='reverse' with depth>1 is a TRANSITIVE reverse lookup — the reverse-reference index that answers it now ships, but this walk lane is not wired to it yet, so it is refused rather than run as a scan-of-scans. Depth-1 reverse: references= (no bounding scope needed), or MGEF seeds under form='chain' for the typed carrier lane.");
                if (walk.seed_paths is { Length: > 0 } || walk.exclusions is { Length: > 0 } || walk.follow is not null)
                    return Wire.Refuse(json, "error: walk.seed_paths/follow/exclusions shape a FORWARD expansion — a reverse walk scans TOWARD the seeds. Drop them.");
                if (form != "chain")
                    return Wire.Refuse(json, "error: the reverse walk's general spelling on this surface IS references= (the same construct, depth 1, and it needs no bounding scope) — walk.direction='reverse' serves form='chain' with MGEF seeds (per-carrier magnitude/area/duration).");
            }
            if (references is { Length: > 0 })
                return Wire.Refuse(json, "error: walk= and references= are the same construct (references= IS the reverse walk at depth 1) — use one spelling per call.");
            if (dense)
                return Wire.Refuse(json, "error: format='dense' renders positional columnar cells 1:1 with requested field paths, and a walk's outputs (chains; reached-set reads) have no fixed column set — use format='text' or 'json'.");
            if (comparisonForm || form is "info_order" or "identity")
                return Wire.Refuse(json, $"error: walk= derives a selection (the reached set), and the '{form}' form does not consume one — use form='chain' for the walk's own paths, or summary/fields/rows/everything/aggregate over the reached set. To compare reached records, walk with to_file= and re-enter the artifact via formids=[\"@<file>\"] with form='{form}'.");
            if (where is { Length: > 0 })
                return Wire.Refuse(json, "error: walk= composed with where= (filtering the reached set by predicate) — walk with to_file=, then re-enter the artifact on a bounded scan via where=[\"formid in @<file>\", …]; the reached set becomes the scan's identity list.");
        }
        if (form == "chain" && walk is null)
            return Wire.Refuse(json, "error: the 'chain' form renders a walk's paths — pass walk= (e.g. walk={\"follow\": \"Template\"} over NPC seeds; reverse MGEF carriers: walk={\"direction\": \"reverse\"} with MGEF formids=).");

        // The existing single-pole lanes below drive off the named-pole fields; the richer specs dispatch to the
        // comparison/overlay lanes before reaching them.
        string? srcName = srcSpec.Kind == LoadOrderService.PoleKind.Named ? srcSpec.Plugin : null;
        string? srcMod = srcSpec.Kind == LoadOrderService.PoleKind.Named ? srcSpec.Mod : null;
        bool srcOverlay = srcSpec.Kind == LoadOrderService.PoleKind.Overlay;

        // ---- fields_source (display pole) ----
        // Validate the value first, then the lane rules: 'scoped'/'scanned' are no-op defaults accepted
        // everywhere, only the actual retarget ('winner') is refused on lanes that cannot honor it, and an
        // unknown value always gets the not-a-known-source refusal.
        bool winnerFields = false;
        if (!string.IsNullOrWhiteSpace(fields_source))
        {
            var fs = fields_source.Trim().ToLowerInvariant();
            if (fs == "winner") winnerFields = true;
            else if (fs is not ("scoped" or "scanned"))
                return Wire.Refuse(json, $"error: fields_source='{fields_source}' — use 'winner' (display the live winner's values) or omit it (display the matched body). A NAMED display pole is the scope-vs-pole composition: plugins= selects, source= names whose version the body forms read.");
            if (winnerFields && comparisonForm)
                return Wire.Refuse(json, $"error: fields_source='winner' retargets what a matched row DISPLAYS, and the '{form}' form's display IS its two poles (source=/versus=) — name the version you want as a pole instead.");
            if (winnerFields && form is "chain" or "info_order")
                return Wire.Refuse(json, $"error: fields_source='winner' retargets FIELD display, and the '{form}' form renders no field values — drop it.");
            if (winnerFields && walk is not null)
                return Wire.Refuse(json, "error: fields_source='winner' — a walk's reading forms display the source= pole's version of the reached set: name the version via source= instead.");
        }

        // ---- lane decision ------------------------------------------------------------------------------
        bool hasFormids = formids is { Length: > 0 };
        bool hasScan = types is { Length: > 0 } || plugins?.names is { Length: > 0 } || conflicts_only
                       || where is { Length: > 0 } || references is { Length: > 0 };
        if (!hasFormids && !hasScan)
            return Wire.Refuse(json, "error: select something — formids= (a record list), or a scan scope: types=, plugins=, conflicts_only=true, where=, references=.");
        // formids= composes with the scan terms: the identity set intersects the scan's selection, or is the
        // universe when it is the only bound. The reverse MGEF walk keeps its own lane, where formids are seeds.
        bool reverseWalk = walk is not null && walkDirection == "reverse";
        if (reverseWalk && (plugins?.names is { Length: > 0 } || conflicts_only || where is { Length: > 0 }))
            return Wire.Refuse(json, "error: the reverse MGEF walk takes formids= (the effects) and optionally types= (narrowing the carrier types) — the general bounded reverse over other scan terms is the references= spelling.");
        if (reverseWalk && !hasFormids)
            return Wire.Refuse(json, "error: the reverse walk needs its seeds — pass formids= (the MGEF(s) whose carriers to trace).");
        // The lane, decided once and read wherever a remedy sentence depends on which lane will run. The dispatch
        // below reads this same value, so a refusal cannot disagree with the lane it is refusing for.
        bool scanLane = hasScan && !reverseWalk;
        // dense is defined as positional columnar cells 1:1 with the requested field paths, so a form with no
        // fixed column set refuses by name rather than quietly falling back to another transport.
        if (dense && form == "everything")
            return Wire.Refuse(json, "error: format='dense' renders positional columnar cells 1:1 with requested field paths, and the 'everything' form has no fixed column set — use format='text' or 'json', or name the paths via form='fields'.");
        if (dense && form == "rows")
            return Wire.Refuse(json, "error: format='dense' renders positional columnar cells 1:1 with requested field paths, and the 'rows' form folds a list's elements into one variable-length line each — use format='text' or 'json'.");
        if (dense && form == "aggregate")
            return Wire.Refuse(json, "error: format='dense' is the per-row columnar transport, and the 'aggregate' form is a count table — its json render IS the compact form; use format='json'.");
        // The same rule at the depth knob: depth expansion is inexpressible in dense, so an explicit
        // project.depth must be refused rather than accepted and dropped. Scan lane only — the list lane refuses
        // dense outright below, and firing this first would send the caller to fix depth and then hit that.
        if (scanLane && dense && project?.depth is { } denseDepth && denseDepth > 1)
            return Wire.Refuse(json, $"error: format='dense' renders positional columnar cells 1:1 with the requested {LeverNames.Records.Fields} paths, and project.depth={denseDepth} emits extra sub-paths that have no column — use format='text' or 'json' for depth expansion, or drop project.depth for the dense summary cells.");
        if (dense && comparisonForm)
            return Wire.Refuse(json, $"error: format='dense' renders positional columnar cells 1:1 with requested field paths, and the '{form}' form's rows are variable-length delta lists with no fixed column set — use format='text' or 'json'.");
        if (dense && form == "info_order")
            return Wire.Refuse(json, "error: format='dense' renders positional columnar cells 1:1 with requested field paths, and the 'info_order' form is an ordered sequence render with no fixed column set — use format='text' or 'json'.");
        if (form == "info_order" && srcSpec.Kind != LoadOrderService.PoleKind.Winner)
            return Wire.Refuse(json, "error: the info_order form merges EVERY plugin touching each topic — that merge is the answer, so a source= pole has no seat here (each line already names the plugin that placed it). Drop source=.");
        // fields_source= is the scan lane's display pole: it retargets what a matched row displays. The list
        // lane's read IS its display, so it would be meaningless there and is refused by name instead of dropped.
        if (winnerFields && formids is { Length: > 0 } && !hasScan)
            return Wire.Refuse(json, "error: fields_source= is the scan lane's display pole — on a formids= read the version you want IS the source: name it via source= (source=\"winner\" is the default).");

        if (offset < 0) return Wire.Refuse(json, $"error: offset={offset} — offset must be >= 0.");
        if (offset > 0 && form == "aggregate")
            return Wire.Refuse(json, "error: the aggregate form counts ALL selected records (a count table has no row window), so offset= has nothing to page — drop offset=, or drop the aggregate form for per-record rows.");
        var toFile = to_file?.Trim();
        bool wantFile = !string.IsNullOrEmpty(toFile);
        if (wantFile)
        {
            if (Artifacts.ValidateToFile(toFile!) is { } verr) return Wire.Refuse(json, verr);
            if (offset > 0) return Wire.Refuse(json, "error: to_file= captures the COMPLETE result (the artifact is never a window), so offset= has nothing to page — drop offset=.");
            if (form == "aggregate") return Wire.Refuse(json, "error: to_file= writes row artifacts, and the aggregate form is a count table with no record rows — drop one of the two.");
            if (counts_only) return Wire.Refuse(json, "error: counts_only= returns the census with no rows, and to_file= writes the rows — the two contradict; drop one (review: this pair used to return the census and silently write nothing).");
        }
        if (where_source is not null && where is not { Length: > 0 })
            return Wire.Refuse(json, "error: where_source= retargets the where= predicates and needs where= — add predicates, or drop where_source=.");

        // ---- the response envelope (form + resolved source arm) -----------------------------------------
        // Text renders get it as a header line; json renders carry the same pairs as top-level fields.
        var envelope = new List<KeyValuePair<string, string>> { new("form", form) };
        string headerLine = $"records  form={form}";
        void Arm(string statement)
        {
            // One source statement per response: first call wins, so a lane that states the specific source and
            // then falls through to a general pipeline cannot emit two "source" properties in one json object.
            if (envelope.Any(kv => kv.Key == "source")) return;
            envelope.Add(new("source", statement));
            headerLine += $"  source={statement}";
        }
        // When a walk or scan derived the selection this call now reads, the two captures meet at a seam: every
        // downstream form's epoch is compared against the deriving step's, and a divergence refuses loud rather
        // than mixing builds.
        string? expectEpoch = null;
        string? SeamTear(string? epoch) =>
            expectEpoch is not null && epoch is not null && epoch != expectEpoch
                ? $"the load order changed between deriving the selection (epoch={expectEpoch}) and reading it (epoch={epoch}) — the two halves would mix builds. Retry the call."
                : null;

        // limit=/offset= window the list lane's RENDER only: the census, the aggregate and every artifact write
        // still cover the complete list, and the window note rides the header and envelope so a windowed render
        // can never read as the whole list.
        int lim = limit <= 0 ? 500 : limit;
        IReadOnlyList<T> Windowed<T>(IReadOnlyList<T> rows)
        {
            // Under to_file= the rows are the file and the render is manifest-only, so no window applies — a
            // window note over a complete artifact would misdescribe both halves.
            if (wantFile) return rows;
            if (offset == 0 && rows.Count <= lim) return rows;
            var w = rows.Skip(offset).Take(lim).ToList();
            var note = w.Count == 0
                ? $"window: no rows — offset={offset} is past the end of the {rows.Count}-row list"
                : $"window: rows {offset + 1}–{offset + w.Count} of {rows.Count} (limit={lim}, offset={offset})";
            envelope.Add(new("window", note));
            headerLine += "\n" + note;
            return w;
        }

        return scanLane
            ? ScanLane()          // including formids plus scan: the identity set rides the scan as an intersection
            : ListLane();

        // ================================================================================================
        //  LIST lane — formids= drives; SOURCE picks the read lane.
        // ================================================================================================
        string ListLane()
        {
            if (dense) return "error: format='dense' is the scan lane's columnar form — a formids= read renders text or json.";

            var (toks, demand, echoSrc, xerr) = Artifacts.ExpandListInput(formids!, "formids");
            if (xerr is not null) return Wire.Refuse(json, xerr);
            var ids = toks!;

            List<KeyValuePair<string, string>> Echo()
            {
                var e = new List<KeyValuePair<string, string>> { new("formids", echoSrc ?? $"{ids.Length} inline formid(s)") };
                e.Add(new("form", form));
                if (srcName is not null) e.Add(new("source", srcName + (srcMod is not null ? $" (mod '{srcMod}')" : "")));
                if (projFields is { Length: > 0 }) e.Add(new("fields", string.Join(", ", projFields)));
                if (depth > 1) e.Add(new("depth", depth.ToString()));
                return e;
            }

            // ---- walk=: the traversal construct derives the selection, or under form='chain' is the render. ----
            if (walk is not null) return WalkLane(ids, demand, echoSrc);

            // ---- delta / tree: the comparison forms ride their own engine batches. ----
            if (form is "delta" or "tree") return ListCompare(ids, demand, echoSrc);

            // ---- info_order: the merged effective INFO sequence. ----
            if (form == "info_order")
            {
                var ioRows = svc.InfoOrderBatch(ids, demand, out var ioRefusal, out var ioEpoch);
                if (ioRefusal is not null)
                    return json ? JsonWire.RenderError(ioRefusal, ioEpoch) : "error: " + ioRefusal + (ioEpoch is not null ? $"\nepoch={ioEpoch}{OrderHealth.ClauseFor(ioEpoch)}" : "");
                var e = new List<KeyValuePair<string, string>>
                {
                    new("formids", echoSrc ?? $"{ids.Length} inline formid(s)"),
                    new("form", form),
                };
                return InfoOrderResponse(ioRows, ioEpoch, e);
            }

            // ---- identity form: the labeling lane. Winner frame by contract. ----
            if (form == "identity")
            {
                if (srcName is not null || srcOverlay)
                    return Wire.Refuse(json, "error: the identity form is the load-order labeling frame (type/editorid/name/WINNER per FormID) — " +
                           "it does not take a source= pole. Use form='summary' or 'fields' for a named version's view.");
                var rows = svc.ResolveRefs(ids, demand, out var epoch, out var refusal);
                if (refusal is not null)
                    return json ? JsonWire.RenderError(refusal, epoch) : "error: " + refusal + $"\nepoch={epoch}{OrderHealth.ClauseFor(epoch)}";
                Arm("winner");
                if (counts_only)
                {
                    // The census honors counts_only on every list form, this one included.
                    int okI = rows.Count(r => r.Error is null);
                    return json ? JsonWire.RenderCounts(envelope, rows.Count, okI, rows.Count - okI, epoch)
                                : $"{headerLine}\ncount={rows.Count} ok={okI} errors={rows.Count - okI}\nepoch={epoch}{OrderHealth.ClauseFor(epoch)}";
                }
                var winRows = Windowed(rows);
                SpillState? spill = null;
                if (wantFile)
                {
                    var (s, aerr) = Artifacts.WriteResolve(rows, epoch, toFile!, "to_file", Echo());
                    if (aerr is not null) return json ? JsonWire.RenderError(aerr, epoch) : "error: " + aerr;
                    spill = SpillState.Spilled(s!, manifestOnly: true);
                }
                string Render(SpillState? sp, out bool trunc) => json
                    ? JsonWire.RenderResolve(winRows, max_chars, epoch, sp, out trunc, envelope)
                    : headerLine + "\n" + Wire.RenderResolve(winRows, max_chars, epoch, sp, out trunc);
                var rendered = Render(spill, out var truncated);
                if (spill is null && truncated)
                {
                    var path = ResultsStore.NextPath(ToolNames.Records, epoch);
                    var (s, aerr) = Artifacts.WriteResolve(rows, epoch, path, "ceiling", Echo());
                    if (aerr is not null) ResultsStore.Release(path);
                    rendered = Render(aerr is null ? SpillState.Spilled(s!, manifestOnly: false) : SpillState.WriteFailed(aerr), out _);
                }
                return rendered;
            }

            // ---- summary / fields / everything / aggregate: batch bodies off the source pole. ----
            // summary reads one cheap leaf, since the header facts ride the outcome; fields reads the named
            // paths; everything passes null to dump the modeled fields.
            IReadOnlyList<string>? readFields = form switch
            {
                "fields" or "rows" => projFields,
                "summary" or "aggregate" => new[] { "EditorID" },   // cheapest leaf — headers carry the summary facts
                _ => null,                                          // everything — the full dump
            };
            IReadOnlyList<ReadOutcome> outcomes;
            LoadOrderService.PoleInfo? pole = null;
            if (srcOverlay && !string.Equals(srcSpec.OverlayState ?? "post", "pre", StringComparison.OrdinalIgnoreCase))
            {
                // The overlay post source: every record's winner replayed through the SkyPatcher INI layer, the
                // replayed body read at the caller's own depth.
                outcomes = svc.OverlayPostBatch(ids, readFields, depth, resolveNames, demand, out var ovRefusal, out var ovEpoch, out _,
                                                LeverNames.Records.ContainerHint);
                if (ovRefusal is not null)
                    return json ? JsonWire.RenderError(ovRefusal, ovEpoch)
                                : "error: " + ovRefusal + (ovEpoch is not null ? $"\nepoch={ovEpoch}{OrderHealth.ClauseFor(ovEpoch)}" : "");
                Arm("skypatcher overlay (post) — the winner after the SkyPatcher INI layer replays");
                envelope.Add(new("epoch_covers_source", "false"));
                headerLine += "\n(the SkyPatcher INI layer's files are OUTSIDE the epoch fingerprint — an INI edit changes answers " +
                              "without changing the epoch; a record whose type SkyPatcher cannot patch reads as its plain winner)";
            }
            else if (srcName is null)
            {
                if (srcOverlay) Arm("skypatcher overlay (pre) = winner — the body the INI layer starts from");
                outcomes = svc.ResolveBatch(ids, readFields, false, depth, resolveNames, null, demand, out var refusal, out var refusalEpoch, LeverNames.Records.ContainerHint);
                if (refusal is not null)
                    return json ? JsonWire.RenderError(refusal, refusalEpoch)
                                : "error: " + refusal + (refusalEpoch is not null ? $"\nepoch={refusalEpoch}{OrderHealth.ClauseFor(refusalEpoch)}" : "");
                if (!srcOverlay) Arm("winner");
            }
            else
            {
                outcomes = svc.ResolveBatchFromPole(ids, srcName, srcMod, readFields, depth, resolveNames, demand,
                                                    out pole, out var refusal, out var refusalEpoch,
                                                    LeverNames.Records.ContainerHint);
                if (refusal is not null)
                    return json ? JsonWire.RenderError(refusal, refusalEpoch)
                                : "error: " + refusal + (refusalEpoch is not null ? $"\nepoch={refusalEpoch}{OrderHealth.ClauseFor(refusalEpoch)}" : "");
                Arm($"{pole!.Plugin} — {pole.Where}");
                if (!pole.EpochCoversPole)
                {
                    envelope.Add(new("epoch_covers_source", "false"));
                    headerLine += "\n(the off-order file's content is OUTSIDE the epoch fingerprint — an edit to it changes answers without changing the epoch)";
                }
            }
            outcomes = FoldRows(outcomes);
            var epoch2 = outcomes.FirstOrDefault(o => o.Epoch is not null)?.Epoch;
            if (SeamTear(epoch2) is { } seamTear)
                return json ? JsonWire.RenderError(seamTear, epoch2) : "error: " + seamTear;

            if (form == "aggregate")
                return RenderListAggregate(outcomes, project!.group_by!, json, dense, epoch2, headerLine, envelope);

            if (counts_only)
            {
                int ok = outcomes.Count(o => o.Error is null), err = outcomes.Count - outcomes.Count(o => o.Error is null);
                return json
                    ? JsonWire.RenderCounts(envelope, outcomes.Count, ok, err, epoch2)
                    : $"{headerLine}\ncount={outcomes.Count} ok={ok} errors={err}" + (epoch2 is not null ? $"\nepoch={epoch2}{OrderHealth.ClauseFor(epoch2)}" : "");
            }

            var winOutcomes = Windowed(outcomes);   // render window; census/aggregate/artifacts stay complete
            SpillState? spill2 = null;
            if (wantFile)
            {
                var (s, aerr) = Artifacts.WriteBatch(outcomes, toFile!, "to_file", Echo(), formLevers);
                if (aerr is not null) return json ? JsonWire.RenderError(aerr, epoch2) : "error: " + aerr;
                spill2 = SpillState.Spilled(s!, manifestOnly: true);
            }
            string Render2(SpillState? sp, out bool trunc) => form == "summary"
                ? RenderRecordsSummary(winOutcomes, json, headerLine, envelope, max_chars, sp, out trunc)
                : json ? JsonWire.RenderBatch(winOutcomes, max_chars, sp, out trunc, envelope, formLevers)
                       : headerLine + "\n" + Wire.RenderBatch(winOutcomes, max_chars, sp, out trunc, formLevers);
            var rendered2 = Render2(spill2, out var truncated2);
            if (spill2 is null && truncated2)
            {
                var path = ResultsStore.NextPath(ToolNames.Records, epoch2 ?? "none");
                var (s, aerr) = Artifacts.WriteBatch(outcomes, path, "ceiling", Echo(), formLevers);
                if (aerr is not null) ResultsStore.Release(path);
                rendered2 = Render2(aerr is null ? SpillState.Spilled(s!, manifestOnly: false) : SpillState.WriteFailed(aerr), out _);
            }
            return rendered2;
        }

        // ================================================================================================
        //  WALK lane — forward walks expand the winner link graph (the chain form renders the paths; any other
        //  form consumes the reached set as its selection); the reverse walk is the typed MGEF lane, tracing
        //  carriers with their per-hit payload.
        // ================================================================================================
        string WalkLane(string[] ids, HousecarlCore.ArtifactDemand? demand, string? echoSrc)
        {
            List<KeyValuePair<string, string>> Echo()
            {
                var e = new List<KeyValuePair<string, string>>
                {
                    new("formids", echoSrc ?? $"{ids.Length} inline seed(s)"),
                    new("form", form),
                    new("walk", $"{walkDirection}{(walk!.follow is { } f ? $" follow={f}" : "")} depth={walkDepth}"),
                };
                if (walk.seed_paths is { Length: > 0 }) e.Add(new("seed_paths", string.Join(", ", walk.seed_paths)));
                return e;
            }

            if (walkDirection == "reverse")
            {
                // The typed MGEF lane: each seed must resolve to a MagicEffect, and a non-MGEF seed fails loud
                // per item rather than reading as '0 carriers'.
                var results = new List<(string Seed, EffectChainResult Result)>(ids.Length);
                foreach (var raw in ids)
                {
                    FormKey fk;
                    try { fk = door.Parse(raw); }
                    catch (Exception ex) { results.Add((raw?.Trim() ?? "", EffectChainResult.Fail($"bad FormID '{raw}': {ex.Message}"))); continue; }
                    // The per-seed carrier bound is the walk's own reach budget; limit=/offset= stay the SEED
                    // window, never a second silent cut on the carrier axis.
                    results.Add((fk.ToString(), svc.ResolveEffectChain(fk, types, walkMaxNodes)));
                }
                // One build for the whole batch: each seed's resolve captures its own view, so the stamps must
                // agree, and an @artifact seed list's epoch demand must match that build.
                var epochsR = results.Select(r => r.Result.Epoch).Where(e => e is not null).Distinct().ToList();
                if (epochsR.Count > 1)
                {
                    var tear = $"the load order changed while the seeds resolved (epochs {string.Join(", ", epochsR)}) — " +
                               "the carrier sets would mix builds. Retry the call.";
                    return json ? JsonWire.RenderError(tear, epochsR[^1]) : "error: " + tear;
                }
                var epochR = epochsR.FirstOrDefault();
                if (demand is not null && (epochR is null || demand.Epoch != epochR))
                {
                    var dref = epochR is null
                        ? $"artifact '{demand.Path}' carries epoch={demand.Epoch}, but no seed consulted a build to verify it against (every seed failed pre-capture) — fix the seeds and retry."
                        : LoadOrderService.ArtifactEpochMismatch(demand, epochR);
                    return json ? JsonWire.RenderError(dref, epochR) : "error: " + dref + (epochR is not null ? $"\nepoch={epochR}{OrderHealth.ClauseFor(epochR)}" : "");
                }
                Arm("winner (carriers are the load-order-effective versions)");
                envelope.Add(new("walk", "reverse, depth 1 — the typed MGEF carrier lane"));
                headerLine += "\nwalk=reverse (per seed: every SPEL/ENCH/ALCH/SCRL/INGR applying it, with the MATCHING entry's magnitude/area/duration — reported AS AUTHORED; conditions are not evaluated, so a row means 'defines it at this strength', not 'it will fire')";
                // The census separates written rows from the true total and names capped seeds, so the artifact
                // and its census cannot disagree and a walk.max_nodes cut is always declared.
                int carrierRows = results.Sum(r => r.Result.Error is null ? r.Result.Rows.Count : 0);
                int carrierTotal = results.Sum(r => r.Result.Error is null ? r.Result.Total : 0);
                int cappedSeeds = results.Count(r => r.Result.Error is null && r.Result.Capped);
                int seedErrs2 = results.Count(r => r.Result.Error is not null);
                var revCounts = new[] { KvI("seeds", results.Count), KvI("carrier_rows", carrierRows), KvI("carrier_total", carrierTotal), KvI("capped_seeds", cappedSeeds), KvI("errors", seedErrs2) };
                if (cappedSeeds > 0)
                    headerLine += $"\n[!] {cappedSeeds} seed(s) hit the walk.max_nodes carrier bound ({walkMaxNodes}) — their rows are a prefix of carrier_total; raise walk.max_nodes.";
                if (counts_only)
                    return json
                        ? JsonWire.RenderNamedCounts(envelope, revCounts, epochR)
                        : $"{headerLine}\nseeds={results.Count} carrier_rows={carrierRows} carrier_total={carrierTotal} capped_seeds={cappedSeeds} errors={seedErrs2}" + (epochR is not null ? $"\nepoch={epochR}{OrderHealth.ClauseFor(epochR)}" : "");
                var winResults = Windowed(results);
                SpillState? revSpill = null;
                if (wantFile)
                {
                    var (sp, aerr) = Artifacts.WriteEffectChains(results, epochR, toFile!, "to_file", Echo());
                    if (aerr is not null) return json ? JsonWire.RenderError(aerr, epochR) : "error: " + aerr;
                    revSpill = SpillState.Spilled(sp!, manifestOnly: true);
                }
                string RenderRev(SpillState? sp, out bool trunc) => json
                    ? JsonWire.RenderEffectChains(winResults, max_chars, envelope, revCounts, epochR, sp, out trunc)
                    : RenderRecordsEffectChains(winResults, results.Count, carrierRows, carrierTotal, seedErrs2, headerLine, epochR, max_chars, sp, out trunc);
                var revRendered = RenderRev(revSpill, out var revTrunc);
                if (revSpill is null && revTrunc)
                {
                    var path = ResultsStore.NextPath(ToolNames.Records, epochR ?? "none");
                    var (sp, aerr) = Artifacts.WriteEffectChains(results, epochR, path, "ceiling", Echo());
                    if (aerr is not null) ResultsStore.Release(path);
                    revRendered = RenderRev(aerr is null ? SpillState.Spilled(sp!, manifestOnly: false) : SpillState.WriteFailed(aerr), out _);
                }
                return revRendered;
            }

            // Forward: one engine batch, one captured build. The chain form renders it; every other form
            // consumes the reached set — seeds included, and the render says so — through the normal lanes.
            var rows = svc.WalkForwardBatch(ids, walk!.seed_paths, walk.follow, walkDepth, walkMaxNodes,
                                            walkExclusions, demand, out var wRefusal, out var wEpoch);
            if (wRefusal is not null)
                return json ? JsonWire.RenderError(wRefusal, wEpoch) : "error: " + wRefusal + (wEpoch is not null ? $"\nepoch={wEpoch}{OrderHealth.ClauseFor(wEpoch)}" : "");
            if (SeamTear(wEpoch) is { } wTear)
                return json ? JsonWire.RenderError(wTear, wEpoch) : "error: " + wTear;

            if (form == "chain")
            {
                Arm("winner (the walk expands the winner link graph)");
                int reached = rows.Where(r => r.Error is null).Sum(r => r.Nodes.Count(n => !n.Status.StartsWith("no links")));
                int errs = rows.Count(r => r.Error is not null);
                envelope.Add(new("walk", $"forward{(walk.follow is { } f2 ? $" follow={f2}" : " (closure)")} depth={walkDepth}"));
                if (counts_only)
                    return json
                        ? JsonWire.RenderNamedCounts(envelope, new[] { KvI("seeds", rows.Count), KvI("reached", reached), KvI("errors", errs) }, wEpoch)
                        : $"{headerLine}\nseeds={rows.Count} reached={reached} errors={errs}" + (wEpoch is not null ? $"\nepoch={wEpoch}{OrderHealth.ClauseFor(wEpoch)}" : "");
                var winRows = Windowed(rows);
                SpillState? spill = null;
                if (wantFile)
                {
                    var (s, aerr) = Artifacts.WriteChain(rows, wEpoch, toFile!, "to_file", Echo());
                    if (aerr is not null) return json ? JsonWire.RenderError(aerr, wEpoch) : "error: " + aerr;
                    spill = SpillState.Spilled(s!, manifestOnly: true);
                }
                var chainCounts = new[] { KvI("seeds", rows.Count), KvI("reached", reached), KvI("errors", errs) };
                string Render(SpillState? sp, out bool trunc) => json
                    ? JsonWire.RenderChain(winRows, max_chars, wEpoch, envelope, chainCounts, sp, out trunc)
                    : RenderRecordsChain(winRows, rows.Count, reached, errs, headerLine, wEpoch, max_chars, sp, out trunc);
                var rendered = Render(spill, out var truncated);
                if (spill is null && truncated)
                {
                    var path = ResultsStore.NextPath(ToolNames.Records, wEpoch ?? "none");
                    var (s, aerr) = Artifacts.WriteChain(rows, wEpoch, path, "ceiling", Echo());
                    if (aerr is not null) ResultsStore.Release(path);
                    rendered = Render(aerr is null ? SpillState.Spilled(s!, manifestOnly: false) : SpillState.WriteFailed(aerr), out _);
                }
                return rendered;
            }

            // Selection consumption: seeds plus reached, in walk order, deduplicated. The ordinary form pipelines
            // then read it under source=, seam-checked against the walk's build.
            var combined = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in rows)
            {
                if (r.Error is not null) continue;
                if (seen.Add(r.Seed)) combined.Add(r.Seed);
                foreach (var n in r.Nodes)
                    if (n.Type is not null && seen.Add(n.Key)) combined.Add(n.Key);
            }
            int seedErrs = rows.Count(r => r.Error is not null);
            if (combined.Count == 0)
                return json ? JsonWire.RenderError($"the walk reached nothing readable ({seedErrs} seed error(s) — run form='chain' to see each seed's outcome).", wEpoch)
                            : $"error: the walk reached nothing readable ({seedErrs} seed error(s) — run form='chain' to see each seed's outcome)." + (wEpoch is not null ? $"\nepoch={wEpoch}{OrderHealth.ClauseFor(wEpoch)}" : "");
            envelope.Add(new("walk", $"forward{(walk.follow is { } f3 ? $" follow={f3}" : " (closure)")} depth={walkDepth} — selection = the {combined.Count} record(s) the walk reached (seeds included{(seedErrs > 0 ? $"; {seedErrs} seed error(s), listed via form='chain'" : "")})"));
            headerLine += $"\nwalk: selection = {combined.Count} reached record(s) (seeds included)";
            expectEpoch = wEpoch;
            formids = combined.ToArray();
            walk = null;
            return ListLane();
        }

        // ================================================================================================
        //  COMPARISON forms on the list lane — delta (subject vs reference) and tree (every provider vs
        //  reference), riding their engine batches on one captured build per call.
        // ================================================================================================
        string ListCompare(string[] ids, HousecarlCore.ArtifactDemand? demand, string? echoSrc)
        {
            List<KeyValuePair<string, string>> Echo()
            {
                var e = new List<KeyValuePair<string, string>>
                {
                    new("formids", echoSrc ?? $"{ids.Length} inline formid(s)"),
                    new("form", form),
                    new("source", srcSpec.Label),
                };
                if (versusSpec is not null) e.Add(new("versus", versusSpec.Label));
                if (projFields is { Length: > 0 }) e.Add(new("fields", string.Join(", ", projFields)));
                return e;
            }

            if (form == "delta")
            {
                var rows = svc.DeltaBatch(ids, srcSpec, versusSpec!, projFields, demand,
                                          out var sArm, out var rArm, out var covers, out var refusal, out var epoch);
                if (refusal is not null)
                    return json ? JsonWire.RenderError(refusal, epoch) : "error: " + refusal + (epoch is not null ? $"\nepoch={epoch}{OrderHealth.ClauseFor(epoch)}" : "");
                return DeltaResponse(rows, sArm, rArm, covers, epoch, Echo());
            }
            else   // tree
            {
                if (srcSpec.Kind != LoadOrderService.PoleKind.Winner)
                    return Wire.Refuse(json, "error: the tree form has no subject — every provider of each record is on the bench, and the pole each is diffed against is versus=. Drop source= (or use form='delta' for a subject-vs-reference comparison).");
                var rows = svc.TreeBatch(ids, versusSpec!, projFields, demand,
                                         out var rArm, out var covers, out var refusal, out var epoch);
                if (refusal is not null)
                    return json ? JsonWire.RenderError(refusal, epoch) : "error: " + refusal + (epoch is not null ? $"\nepoch={epoch}{OrderHealth.ClauseFor(epoch)}" : "");
                return TreeResponse(rows, rArm, covers, epoch, Echo());
            }
        }

        // The shared delta response pipeline — envelope, counts_only, window, spill, both renders — used by the
        // list and scan lanes alike so their behavior cannot drift.
        string DeltaResponse(IReadOnlyList<LoadOrderService.DeltaRow> rows, string? sArm, string? rArm, bool covers,
                             string? epoch, List<KeyValuePair<string, string>> echo)
        {
            Arm(sArm ?? srcSpec.Label);
            envelope.Add(new("versus", rArm ?? versusSpec!.Label));
            headerLine += $"  versus={rArm ?? versusSpec!.Label}";
            CoverageNote(covers);
            int differing = rows.Count(x => x.Error is null && x.Diff!.Deltas.Count > 0);
            int identical = rows.Count(x => x.Error is null && x.Diff!.Deltas.Count == 0 && x.Diff.Complete);
            int errs = rows.Count(x => x.Error is not null);
            if (counts_only)
                return json
                    ? JsonWire.RenderNamedCounts(envelope, new[] { KvI("count", rows.Count), KvI("differing", differing), KvI("identical", identical), KvI("errors", errs) }, epoch)
                    : $"{headerLine}\ncount={rows.Count} differing={differing} identical={identical} errors={errs}" + (epoch is not null ? $"\nepoch={epoch}{OrderHealth.ClauseFor(epoch)}" : "");
            var winRows = Windowed(rows);
            SpillState? spill = null;
            if (wantFile)
            {
                var (s, aerr) = Artifacts.WriteDelta(rows, epoch, toFile!, "to_file", echo);
                if (aerr is not null) return json ? JsonWire.RenderError(aerr, epoch) : "error: " + aerr;
                spill = SpillState.Spilled(s!, manifestOnly: true);
            }
            var deltaCounts = new[] { KvI("count", rows.Count), KvI("differing", differing), KvI("identical", identical), KvI("errors", errs) };
            string Render(SpillState? sp, out bool trunc) => json
                ? JsonWire.RenderDelta(winRows, max_chars, epoch, envelope, deltaCounts, sp, out trunc)
                : RenderRecordsDelta(winRows, rows.Count, differing, identical, errs, headerLine, epoch, max_chars, sp, out trunc);
            var rendered = Render(spill, out var truncated);
            if (spill is null && truncated)
            {
                var path = ResultsStore.NextPath(ToolNames.Records, epoch ?? "none");
                var (s, aerr) = Artifacts.WriteDelta(rows, epoch, path, "ceiling", echo);
                if (aerr is not null) ResultsStore.Release(path);
                rendered = Render(aerr is null ? SpillState.Spilled(s!, manifestOnly: false) : SpillState.WriteFailed(aerr), out _);
            }
            return rendered;
        }

        // The shared tree response pipeline. The tree form has no subject: every provider is on the bench.
        string TreeResponse(IReadOnlyList<LoadOrderService.TreeRow> rows, string? rArm, bool covers,
                            string? epoch, List<KeyValuePair<string, string>> echo)
        {
            // The tree's reference rides the `versus` envelope key, the same convention delta uses, so `source`
            // is free to keep the SELECTION statement — putting the reference there suppresses the selection
            // statement that makes epoch_covers_source intelligible.
            var refStatement = versusSpec!.Kind == LoadOrderService.PoleKind.Winner ? "winner" : rArm ?? versusSpec.Label;
            envelope.Add(new("versus", refStatement));
            headerLine += $"  versus={refStatement}";
            Arm("every provider of each record (the touching stack, winner last)");
            CoverageNote(covers);
            int contested = rows.Count(x => x.Error is null && x.Touchers.Count > 1);
            int errs = rows.Count(x => x.Error is not null);
            if (counts_only)
                return json
                    ? JsonWire.RenderNamedCounts(envelope, new[] { KvI("count", rows.Count), KvI("contested", contested), KvI("errors", errs) }, epoch)
                    : $"{headerLine}\ncount={rows.Count} contested={contested} errors={errs}" + (epoch is not null ? $"\nepoch={epoch}{OrderHealth.ClauseFor(epoch)}" : "");
            var winRows = Windowed(rows);
            SpillState? spill = null;
            if (wantFile)
            {
                var (s, aerr) = Artifacts.WriteTree(rows, epoch, toFile!, "to_file", echo);
                if (aerr is not null) return json ? JsonWire.RenderError(aerr, epoch) : "error: " + aerr;
                spill = SpillState.Spilled(s!, manifestOnly: true);
            }
            var treeCounts = new[] { KvI("count", rows.Count), KvI("contested", contested), KvI("errors", errs) };
            string Render(SpillState? sp, out bool trunc) => json
                ? JsonWire.RenderTree(winRows, max_chars, epoch, envelope, treeCounts, sp, out trunc, LeverNames.Records)
                : RenderRecordsTree(winRows, rows.Count, contested, errs, projFields is { Length: > 0 }, headerLine, epoch, max_chars, sp, out trunc);
            var rendered = Render(spill, out var truncated);
            if (spill is null && truncated)
            {
                var path = ResultsStore.NextPath(ToolNames.Records, epoch ?? "none");
                var (s, aerr) = Artifacts.WriteTree(rows, epoch, path, "ceiling", echo);
                if (aerr is not null) ResultsStore.Release(path);
                rendered = Render(aerr is null ? SpillState.Spilled(s!, manifestOnly: false) : SpillState.WriteFailed(aerr), out _);
            }
            return rendered;
        }

        // The shared info_order response pipeline — envelope, counts_only, window, spill, both renders — used by
        // the list and scan lanes alike.
        string InfoOrderResponse(IReadOnlyList<LoadOrderService.InfoOrderRow> rows, string? epoch,
                                 List<KeyValuePair<string, string>> echo)
        {
            Arm("the merge of every touching plugin (the effective order the game walks)");
            int contested = rows.Count(x => x.Error is null && x.Order is { Contested: true });
            int errs = rows.Count(x => x.Error is not null);
            if (counts_only)
                return json
                    ? JsonWire.RenderNamedCounts(envelope, new[] { KvI("count", rows.Count), KvI("contested", contested), KvI("errors", errs) }, epoch)
                    : $"{headerLine}\ncount={rows.Count} contested={contested} errors={errs}" + (epoch is not null ? $"\nepoch={epoch}{OrderHealth.ClauseFor(epoch)}" : "");
            var winRows = Windowed(rows);
            SpillState? spill = null;
            if (wantFile)
            {
                var (s, aerr) = Artifacts.WriteInfoOrder(rows, epoch, toFile!, "to_file", echo);
                if (aerr is not null) return json ? JsonWire.RenderError(aerr, epoch) : "error: " + aerr;
                spill = SpillState.Spilled(s!, manifestOnly: true);
            }
            var ioCounts = new[] { KvI("count", rows.Count), KvI("contested", contested), KvI("errors", errs) };
            string Render(SpillState? sp, out bool trunc) => json
                ? JsonWire.RenderInfoOrder(winRows, max_chars, epoch, envelope, ioCounts, sp, out trunc)
                : RenderRecordsInfoOrder(winRows, rows.Count, contested, errs, headerLine, epoch, max_chars, sp, out trunc);
            var rendered = Render(spill, out var truncated);
            if (spill is null && truncated)
            {
                var path = ResultsStore.NextPath(ToolNames.Records, epoch ?? "none");
                var (s, aerr) = Artifacts.WriteInfoOrder(rows, epoch, path, "ceiling", echo);
                if (aerr is not null) ResultsStore.Release(path);
                rendered = Render(aerr is null ? SpillState.Spilled(s!, manifestOnly: false) : SpillState.WriteFailed(aerr), out _);
            }
            return rendered;
        }

        // A pole reading content outside the epoch fingerprint — an off-order file, or the overlay's INIs — must
        // be declared in both the envelope and the header. One helper, so no form forgets.
        void CoverageNote(bool covers)
        {
            if (covers) return;
            envelope.Add(new("epoch_covers_source", "false"));
            headerLine += "\n(a pole reads content OUTSIDE the epoch fingerprint — an off-order file or the INI layer; an edit there changes answers without changing the epoch)";
        }

        // ================================================================================================
        //  SCAN lane — types/plugins/where/references/conflicts_only drive; SOURCE picks the universe.
        // ================================================================================================
        string ScanLane()
        {
            if (form == "identity")
                return Wire.Refuse(json, "error: the identity form labels a formids= list; a scan's summary rows already carry each match's identity — use form='summary' (the default).");

            if (srcOverlay || versusSpec?.Kind == LoadOrderService.PoleKind.Overlay)
                return Wire.Refuse(json, "error: an overlay pole on a SCAN would replay the SkyPatcher INI layer over every match — a per-record replay at scan scale " +
                       "(a scan comparison compares EVERY match, so it is not a bound). Name the records via formids= — the list lane reads and " +
                       "compares their post-state bodies — or read the whole layer via " + ToolNames.SkypatcherLayer + ".");
            bool hasBodyFilter = where is { Length: > 0 } || references is { Length: > 0 };
            bool hasTypes = types is { Length: > 0 };
            bool hasScope = plugins?.names is { Length: > 0 };
            bool scopePlusPole = false;
            // The derived-selection forms (comparisons, info_order, a walk's seeds) consume EVERY match; known
            // up front, used by the scan cap below.
            bool derivedSelection = comparisonForm || form == "info_order" || walk is not null;
            // The scan states the source itself EXCEPT for forms whose own pipeline states one: delta names its
            // subject, info_order the merge, a walk its re-entered read. The tree has no subject, so the scan's
            // selection statement stays — it is what discloses an off-order or scoped selection universe.
            bool pipelineArms = form == "delta" || form == "info_order" || walk is not null;
            // An unbounded references= is answered off the reverse-reference index; every other body filter still
            // needs a bound, because the index knows links, not field values.
            bool onlyReverseFilter = where is not { Length: > 0 };
            if (hasBodyFilter && !hasTypes && !hasScope && !hasFormids && !onlyReverseFilter)
                return Wire.Refuse(json, "error: where= is a body scan and must be combined with types=, plugins=, or a formids= set to bound the work " +
                       "(conflicts_only= alone is not enough — an unbounded body scan over the whole order is refused). " +
                       "Only references= is unbounded, off the reverse-reference index.");
            if (plugins is { defined_in: true } && !hasScope)
                return Wire.Refuse(json, "error: plugins.defined_in=true keeps records DEFINED in the scoped plugins, so plugins.names must name that scope.");

            // The identity set intersects the scan: expanded here so an @file or artifact demand is honored
            // inside the scan's own capture, parsed once, then handed to the engine as the set filter. Alone, it
            // is the scan universe, since the set is itself the bound.
            HousecarlCore.ArtifactDemand? fidDemand = null; string? fidEcho = null;
            IReadOnlyList<FormKey>? formidSet = null;
            if (hasFormids)
            {
                var (ftoks, fdemand, fecho, fxerr) = Artifacts.ExpandListInput(formids!, "formids");
                if (fxerr is not null) return Wire.Refuse(json, fxerr);
                fidDemand = fdemand; fidEcho = fecho;
                var fkList = new List<FormKey>();
                foreach (var t in ftoks!)
                {
                    if (string.IsNullOrWhiteSpace(t)) continue;
                    try { fkList.Add(door.Parse(t)); }
                    catch (Exception ex) { return Wire.Refuse(json, $"error: bad formids entry '{t}': {ex.Message}. Expected 'XXXXXX:Plugin.esp'."); }
                }
                if (fkList.Count == 0) return Wire.Refuse(json, "error: formids= expanded to an empty list — nothing to intersect the scan with.");
                formidSet = fkList;
            }

            // ---- Off-order source universe: the file's own records. ----
            string? probeEpoch = null;
            if (srcName is not null)
            {
                // Resolve which case applies once, with a cheap containment probe. The active-plugin scan below
                // re-captures, so its stamp is compared against the probe's and a mid-call order change refuses
                // loud. The off-order lane reads the file directly and consults no further build.
                var probe = svc.ProbeSourceArm(srcName, srcMod, out var probeErr);
                if (probeErr is not null) return Wire.Refuse(json, "error: " + probeErr);
                srcName = probe!.Plugin;   // a path pole resolves back to its plugin name; every consumer below uses the resolved name
                probeEpoch = probe.Epoch;
                if (!probe.InOrder)
                    return OffOrderScan(probe);
                if (hasScope)
                {
                    // Scope and pole compose: plugins= decides which records are considered, the named source=
                    // decides whose version the body forms read. Identity-fact forms have nothing for the pole
                    // to change, so they refuse it rather than accept and ignore it.
                    if (form is "summary" or "aggregate")
                        return Wire.Refuse(json, $"error: a plugins= scope with a named source= reads the POLE's version of each scoped match — and the '{form}' form's rows are identity facts the pole doesn't change. Drop source=, or use form='fields'/'everything' (the pole's bodies) or 'delta'/'tree' (comparisons).", probeEpoch);
                    if (winnerFields)
                        return Wire.Refuse(json, "error: fields_source='winner' and a named source= under a plugins= scope are TWO display poles on one call — the pole's version is what this composition reads. Drop fields_source= (or drop source= and keep fields_source='winner').", probeEpoch);
                    scopePlusPole = true;
                    // The scope statement is only truthful for forms that READ the pole's bodies; delta states
                    // its own subject. A scoped tree reads every provider, so it states the selection without
                    // the pole clause.
                    if (form == "tree") Arm($"{probe.Plugin} — scope-selected ({string.Join(", ", plugins!.names!)}); the tree reads every provider");
                    else if (!pipelineArms) Arm($"{probe.Plugin} — active in the load order (the plugins= scope selects; this pole's version is read)");
                }
                else if (!pipelineArms)
                    // The pole's records are the scan universe: stream the plugin and say so.
                    Arm($"{probe.Plugin} — active in the load order");
            }
            else if (!pipelineArms) Arm("winner");   // delta/info_order/walk pipelines state their own source

            // references= @file expansion and FormKey parse.
            HousecarlCore.ArtifactDemand? refDemand = null; string? refEcho = null;
            var refs = references;
            if (refs is { Length: > 0 })
            {
                var (toks, demand, echoSrc, xerr) = ExpandReferenceList(refs);
                if (xerr is not null) return Wire.Refuse(json, xerr);
                refs = toks!; refDemand = demand; refEcho = echoSrc;
            }
            IReadOnlyList<FormKey>? refFks = null, refNoneFks = null;
            if (refs is { Length: > 0 })
            {
                var (pos, neg, rerr) = SplitReferenceTargets(refs, door);
                if (rerr is not null) return Wire.Refuse(json, rerr);
                if (pos!.Count > 0) refFks = pos.Distinct().ToList();
                if (neg!.Count > 0) refNoneFks = neg.Distinct().ToList();
            }

            var scanPlugins = scopePlusPole ? plugins!.names : (srcName is not null ? new[] { srcName } : plugins?.names);
            // The negated-only unbounded spelling selects the whole orphan set — millions of records on a real
            // order — and the derived forms consume EVERY match uncapped (effLimit below is int.MaxValue for them),
            // so each match would also be compared, merged or walked. Refused with the bound named, rather than
            // run. A positive references= is not this: its universe is what links the target. Read off scanPlugins,
            // so an in-order source= plugin counts as the scope it is and its one plugin's records are not called
            // the sweep.
            if (refNoneFks is not null && refFks is null && !hasTypes && scanPlugins is not { Length: > 0 } && !hasFormids && derivedSelection)
                return Wire.Refuse(json, $"error: a negated references= with no types=/plugins=/formids= bound is the orphan sweep — every record nothing in the order references — and the '{form}' form compares or merges EVERY match, uncapped. Add types= or plugins= to bound it, or run the sweep as a plain scan with to_file= and re-enter the artifact via formids=[\"@<file>\"].");

            bool definedIn = plugins?.defined_in ?? false;
            // group_by=type names each match's record type, which only a body-bearing scope can supply. The engine
            // refuses it too, in the 1.x spelling (group_by=/type=), so it is pre-checked here in this tool's own
            // levers. Placed on the IN-ORDER lane only — the off-order file scan returned above, where the file's
            // own records ARE the universe and every match has a body — and read off scanPlugins, so an in-order
            // source= plugin counts as the scope it is. An unbounded references= is body-bearing too: the reverse
            // index hands the scan a universe of keys, so each match's body is read and can name its type.
            if (form == "aggregate" && walk is null
                && string.Equals(project!.group_by?.Trim(), "type", StringComparison.OrdinalIgnoreCase)
                && !hasTypes && scanPlugins is not { Length: > 0 } && formidSet is null
                && refFks is null && refNoneFks is null)
                return Wire.Refuse(json, "error: project.group_by='type' counts each match's record TYPE, which only a " +
                    "body-bearing scope can name — add types=, plugins=, formids=, references=, or an in-order source= plugin. " +
                    "(where= reads bodies too but takes one of those as its own bound; references= brings its own universe " +
                    "off the reverse-reference index; 'winner' and 'defined_in' group without reading a body.)", probeEpoch);
            // Under walk= the scan only SELECTS the seeds; the aggregate, like every reading form, applies to
            // the reached set after the walk lane re-enters. Grouping the scan itself would label an aggregate
            // of the seeds as the walk's answer.
            var groupBy = form == "aggregate" && walk is null ? project!.group_by!.Trim().ToLowerInvariant() : null;
            // The derived-selection forms consume EVERY match: the window applies to their rows while the counts
            // and artifact cover the full selection, so the scan itself is uncapped for them. The tool
            // description declares this cost — the scan terms are the bound.
            int effLimit = wantFile || derivedSelection ? int.MaxValue : counts_only ? 0 : (limit <= 0 ? 500 : limit);

            var demandsList = new List<HousecarlCore.ArtifactDemand>();
            if (refDemand is not null) demandsList.Add(refDemand);
            if (fidDemand is not null) demandsList.Add(fidDemand);
            var outcome = svc.CrossQuery(types, refFks, null, conflicts_only, scanPlugins, where,
                                         effLimit, definedIn, groupBy, offset, where_source,
                                         demandsList.Count > 0 ? demandsList : null, formidSet, door.CapturedView,
                                         refNoneFks);
            // The probe-to-scan seam is epoch-compared: the source statement must describe the same build the
            // rows were scanned from.
            if (probeEpoch is not null && outcome.Error is null && outcome.Epoch is not null && outcome.Epoch != probeEpoch)
            {
                var tear = $"the load order changed between resolving the source arm (epoch={probeEpoch}) and the scan " +
                           $"(epoch={outcome.Epoch}) — the arm statement would describe a different world. Retry the call.";
                // Rendered in the caller's format, like every other refusal on these paths.
                return fmt is Wire.QueryFormat.Text ? "error: " + tear : JsonWire.RenderError(tear, outcome.Epoch);
            }

            List<KeyValuePair<string, string>> Echo()
            {
                var e = new List<KeyValuePair<string, string>>();
                void Add(string k, string? v) { if (!string.IsNullOrEmpty(v)) e.Add(new(k, v!)); }
                Add("form", form);
                Add("formids", fidEcho ?? (formidSet is not null ? $"{formidSet.Count} inline formid(s)" : null));
                Add("types", types is { Length: > 0 } ? string.Join(", ", types) : null);
                Add("references", refEcho ?? (refs is { Length: > 0 } ? string.Join(", ", refs) : null));
                if (conflicts_only) Add("conflicts_only", "true");
                Add("plugins", scanPlugins is { Length: > 0 } ? string.Join(", ", scanPlugins) : null);
                if (definedIn) Add("defined_in", "true");
                Add("where", where is { Length: > 0 } ? string.Join(" AND ", where) : null);
                Add("where_source", where_source);
                Add("group_by", groupBy);
                Add("fields", projFields is { Length: > 0 } ? string.Join(", ", projFields) : null);
                if (depth > 1) Add("depth", depth.ToString());
                if (winnerFields) Add("fields_source", "winner");
                Add("source", srcName ?? (srcSpec.Kind != LoadOrderService.PoleKind.Winner ? srcSpec.Label : null));
                if (versusSpec is not null) Add("versus", versusSpec.Label);
                return e;
            }

            // The reverse-reference index's accounting belongs to the response whatever consumes the scan. The two
            // CrossQuery renderers read it off the outcome themselves; every form BELOW renders its own pipeline's
            // response and would drop it, so it rides their header and envelope. One place, so no form forgets.
            if (outcome.ReverseIndexNote is not null && outcome.Error is null && outcome.Groups is null
                && (walk is not null || comparisonForm || form == "info_order"
                    || ((form == "everything" || (form == "fields" && scopePlusPole)) && !counts_only)))
            {
                envelope.Add(new("reverse_index", outcome.ReverseIndexNote));
                headerLine += "\n" + outcome.ReverseIndexNote;
            }

            // ---- walk= on a scan: the scan's matches are the seeds and the walk lane takes it from there,
            //      rendering the chain or reading the reached set, seam-checked throughout. ----
            if (walk is not null && outcome.Error is null && outcome.Groups is null)
            {
                var seedKeys = outcome.Keys.Select(k => k.ToString()).ToArray();
                envelope.Add(new("total", outcome.Total.ToString()));
                headerLine += $"\n{outcome.Total} match(es) selected by the scan as walk seeds";
                expectEpoch = outcome.Epoch;
                return WalkLane(seedKeys, null, null);
            }

            // ---- delta / tree on a scan: the scan selects the records and the engine batches compare them.
            //      Two captures meet here, so the seam is epoch-compared and the halves can never mix builds.
            if (comparisonForm && outcome.Error is null && outcome.Groups is null)
            {
                var cmpKeys = outcome.Keys.Select(k => k.ToString()).ToList();
                envelope.Add(new("total", outcome.Total.ToString()));
                headerLine += $"\n{outcome.Total} match(es) selected by the scan";
                if (form == "delta")
                {
                    var rows = svc.DeltaBatch(cmpKeys, srcSpec, versusSpec!, projFields, null,
                                              out var sArm, out var rArm, out var covers, out var refusal, out var depoch);
                    if (refusal is not null)
                        return json ? JsonWire.RenderError(refusal, depoch) : "error: " + refusal + (depoch is not null ? $"\nepoch={depoch}{OrderHealth.ClauseFor(depoch)}" : "");
                    if (outcome.Epoch is not null && depoch is not null && depoch != outcome.Epoch)
                    {
                        var tear = $"the load order changed between the scan (epoch={outcome.Epoch}) and the comparison " +
                                   $"(epoch={depoch}) — the two halves would mix builds. Retry the call.";
                        return json ? JsonWire.RenderError(tear, depoch) : "error: " + tear;
                    }
                    return DeltaResponse(rows, sArm, rArm, covers, depoch, Echo());
                }
                else
                {
                    var rows = svc.TreeBatch(cmpKeys, versusSpec!, projFields, null,
                                             out var rArm, out var covers, out var refusal, out var tepoch);
                    if (refusal is not null)
                        return json ? JsonWire.RenderError(refusal, tepoch) : "error: " + refusal + (tepoch is not null ? $"\nepoch={tepoch}{OrderHealth.ClauseFor(tepoch)}" : "");
                    if (outcome.Epoch is not null && tepoch is not null && tepoch != outcome.Epoch)
                    {
                        var tear = $"the load order changed between the scan (epoch={outcome.Epoch}) and the comparison " +
                                   $"(epoch={tepoch}) — the two halves would mix builds. Retry the call.";
                        return json ? JsonWire.RenderError(tear, tepoch) : "error: " + tear;
                    }
                    return TreeResponse(rows, rArm, covers, tepoch, Echo());
                }
            }

            // ---- info_order on a scan: the scan selects the topics and the merge engine renders, with the seam
            //      epoch-compared like every two-capture form. ----
            if (form == "info_order" && outcome.Error is null && outcome.Groups is null)
            {
                var ioKeys = outcome.Keys.Select(k => k.ToString()).ToList();
                envelope.Add(new("total", outcome.Total.ToString()));
                headerLine += $"\n{outcome.Total} match(es) selected by the scan";
                var ioRows = svc.InfoOrderBatch(ioKeys, null, out var ioRefusal, out var ioEpoch);
                if (ioRefusal is not null)
                    return json ? JsonWire.RenderError(ioRefusal, ioEpoch) : "error: " + ioRefusal + (ioEpoch is not null ? $"\nepoch={ioEpoch}{OrderHealth.ClauseFor(ioEpoch)}" : "");
                if (outcome.Epoch is not null && ioEpoch is not null && ioEpoch != outcome.Epoch)
                {
                    var tear = $"the load order changed between the scan (epoch={outcome.Epoch}) and the merge " +
                               $"(epoch={ioEpoch}) — the two halves would mix builds. Retry the call.";
                    return json ? JsonWire.RenderError(tear, ioEpoch) : "error: " + tear;
                }
                return InfoOrderResponse(ioRows, ioEpoch, Echo());
            }

            // ---- form=everything on a scan: selection here, bodies via the batch lane, window-bounded.
            // counts_only skips the body lane entirely — its census is the scan render below. ----
            // The rows form always takes this lane: its fold is over a body read, and the scan render fills
            // detail rows of its own that never pass through it.
            if ((form == "everything" || form == "rows" || (form == "fields" && scopePlusPole)) && !counts_only && outcome.Error is null && outcome.Groups is null)
            {
                var keys = outcome.Keys.Select(k => k.ToString()).ToList();
                IReadOnlyList<ReadOutcome> bodies;
                if (srcName is not null)
                {
                    bodies = svc.ResolveBatchFromPole(keys, srcName, srcMod, bodyFields ? projFields : null, depth, resolveNames, null,
                                                      out _, out var bref, out var brefEpoch, LeverNames.Records.ContainerHint);
                    // A refusal is judged on the named cause, never on row count: a zero-match scan is an honest
                    // empty result, not a failure.
                    if (bref is not null)
                        return json ? JsonWire.RenderError(bref, brefEpoch)
                                    : "error: " + bref + (brefEpoch is not null ? $"\nepoch={brefEpoch}{OrderHealth.ClauseFor(brefEpoch)}" : "");
                }
                else
                {
                    // The scan's per-match source decides whose body `everything` dumps, the same rule the fields
                    // form renders by — otherwise the same SELECT reads a different pole per form with nothing
                    // saying so. Keys group by matched source and each group reads off its own plugin;
                    // fields_source="winner" retargets display to the winner as it does on the fields form.
                    var srcs = outcome.Sources;
                    if (winnerFields || srcs is null || srcs.Take(keys.Count).All(s => s is null))
                        bodies = svc.ResolveBatch(keys, bodyFields ? projFields : null, false, depth, resolveNames, containerHint: LeverNames.Records.ContainerHint);
                    else
                    {
                        var byIndex = new ReadOutcome[keys.Count];
                        var bySource = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
                        var winnerIdx = new List<int>();
                        for (int i = 0; i < keys.Count; i++)
                        {
                            var s = i < srcs.Count ? srcs[i] : null;
                            if (s is null) { winnerIdx.Add(i); continue; }
                            if (!bySource.TryGetValue(s, out var l)) bySource[s] = l = new List<int>();
                            l.Add(i);
                        }
                        if (winnerIdx.Count > 0)
                        {
                            var res = svc.ResolveBatch(winnerIdx.Select(i => keys[i]).ToList(), bodyFields ? projFields : null, false, depth, resolveNames, containerHint: LeverNames.Records.ContainerHint);
                            for (int i = 0; i < winnerIdx.Count; i++) byIndex[winnerIdx[i]] = res[i];
                        }
                        foreach (var kv in bySource)
                        {
                            var res = svc.ResolveBatch(kv.Value.Select(i => keys[i]).ToList(), bodyFields ? projFields : null, false, depth, resolveNames, kv.Key, LeverNames.Records.ContainerHint);
                            for (int i = 0; i < kv.Value.Count; i++) byIndex[kv.Value[i]] = res[i];
                        }
                        bodies = byIndex;
                    }
                }
                bodies = FoldRows(bodies);
                // Rows the pole does not touch come back as per-item refusals naming the touchers, and the
                // accounting carries the explicit count so a quiet omission is impossible.
                if (scopePlusPole)
                {
                    int notTouched = bodies.Count(o => o.Error is not null && (o.Error.Contains("does not touch") || o.Error.Contains("does not define or override")));
                    if (notTouched > 0)
                    {
                        envelope.Add(new("not_touched", notTouched.ToString()));
                        headerLine += $"\nnot_touched={notTouched} — scoped match(es) the source pole has no version of (each row names its actual touchers)";
                    }
                }
                // The selection and every body read must agree on one build — grouped reads capture per batch —
                // so any divergence refuses loud. An empty selection has no body epochs and passes, rendering
                // as an honest 0-row batch.
                var bodyEpochs = bodies.Where(o => o.Epoch is not null).Select(o => o.Epoch!).Distinct().ToList();
                if (outcome.Epoch is not null && bodyEpochs.Any(e => e != outcome.Epoch))
                {
                    var tear = $"the load order changed between the scan (epoch={outcome.Epoch}) and the body read " +
                               $"(epoch={string.Join(", ", bodyEpochs.Where(e => e != outcome.Epoch))}) — the two halves would mix builds. Retry the call.";
                    return json ? JsonWire.RenderError(tear, outcome.Epoch) : "error: " + tear;
                }
                var bodyEpoch = bodyEpochs.FirstOrDefault() ?? outcome.Epoch;
                envelope.Add(new("total", outcome.Total.ToString()));
                headerLine += $"\n{outcome.Total} match(es); bodies for the {keys.Count}-row window below";
                // These bodies were selected by a scan, not a formids list, so the batch notice's selection
                // clause must name limit= — the knob that actually windows this response.
                var evLevers = formLevers.OnScanSelection();
                string RenderEv(SpillState? sp, out bool trunc) => json
                    ? JsonWire.RenderBatch(bodies, max_chars, sp, out trunc, envelope, evLevers)
                    : headerLine + "\n" + Wire.RenderBatch(bodies, max_chars, sp, out trunc, evLevers);
                SpillState? evSpill = null;
                if (wantFile)
                {
                    var (s, aerr) = Artifacts.WriteBatch(bodies, toFile!, "to_file", Echo(), evLevers);
                    if (aerr is not null) return json ? JsonWire.RenderError(aerr, bodyEpoch) : "error: " + aerr;
                    evSpill = SpillState.Spilled(s!, manifestOnly: true);
                }
                var evRendered = RenderEv(evSpill, out var evTrunc);
                if (evSpill is null && evTrunc)
                {
                    var path = ResultsStore.NextPath(ToolNames.Records, bodyEpoch ?? "none");
                    var (s, aerr) = Artifacts.WriteBatch(bodies, path, "ceiling", Echo(), evLevers);
                    if (aerr is not null) ResultsStore.Release(path);
                    evRendered = RenderEv(aerr is null ? SpillState.Spilled(s!, manifestOnly: false) : SpillState.WriteFailed(aerr), out _);
                }
                return evRendered;
            }

            // ---- summary / fields / aggregate: the scan renders, envelope-stamped. ----
            SpillState? spill = null;
            if (wantFile && outcome.Error is null)
            {
                var (s, aerr) = Artifacts.WriteCrossQuery(svc, outcome, projFields, resolveNames, winnerFields, depth, toFile!, "to_file", Echo(), LeverNames.Records);
                if (aerr is not null)
                    return fmt is Wire.QueryFormat.Text ? "error: " + aerr : JsonWire.RenderError(aerr, outcome.Epoch);
                spill = SpillState.Spilled(s!, manifestOnly: true);
            }
            // "drop project=" is only actionable when the rows are detail rows: a summary-form scan is already
            // reading the render that clause points at and has no project= to drop.
            var qLevers = projFields is { Length: > 0 } ? LeverNames.Records : LeverNames.Records.WithNothingToDrop();
            string Render(SpillState? sp, out bool trunc) => fmt switch
            {
                Wire.QueryFormat.Dense when groupBy is null => JsonWire.RenderCrossQueryDense(svc, outcome, projFields, max_chars, resolveNames, winnerFields, sp, out trunc, envelope, qLevers),
                Wire.QueryFormat.Dense or Wire.QueryFormat.Json => JsonWire.RenderCrossQuery(svc, outcome, projFields, max_chars, resolveNames, winnerFields, depth, sp, out trunc, envelope, qLevers),
                _ => headerLine + "\n" + Wire.RenderCrossQuery(svc, outcome, projFields, max_chars, resolveNames, winnerFields, depth, sp, out trunc, qLevers),
            };
            var rendered = Render(spill, out var truncated);
            if (spill is null && truncated && outcome.Error is null)
            {
                var path = ResultsStore.NextPath(ToolNames.Records, outcome.Epoch ?? "none");
                var (s, aerr) = Artifacts.WriteCrossQuery(svc, outcome, projFields, resolveNames, winnerFields, depth, path, "ceiling", Echo(), LeverNames.Records);
                if (aerr is not null) ResultsStore.Release(path);
                rendered = Render(aerr is null ? SpillState.Spilled(s!, manifestOnly: false) : SpillState.WriteFailed(aerr), out _);
            }
            return rendered;
        }

        // ================================================================================================
        //  OFF-ORDER scan: the file's own records are the universe.
        // ================================================================================================
        string OffOrderScan(LoadOrderService.PoleInfo pole)
        {
            // The file is the SELECTION statement, stated for every form except delta, whose pipeline names the
            // same file as its subject. The tree needs it, since its reference rides `versus` instead.
            if (form != "delta") Arm($"{pole.Plugin} — {pole.Where}");
            envelope.Add(new("epoch_covers_source", "false"));
            headerLine += "\n(the off-order file's content is OUTSIDE the epoch fingerprint — an edit to it changes answers without changing the epoch)";
            if (conflicts_only)
                return Wire.Refuse(json, "error: conflicts_only= has no meaning on an out-of-load-order file — it is not in the conflict frame. Drop it, or read the winner (source=\"winner\").", pole.Epoch);
            if (form == "info_order")
                return Wire.Refuse(json, "error: the info_order form merges the ACTIVE order's touching plugins — an out-of-load-order file is not in that frame. Read the winner's merge (drop source=), or enumerate the file's DIAL records with form='summary'.", pole.Epoch);
            if (walk is not null)
                return Wire.Refuse(json, "error: the walk expands the ACTIVE order's winner link graph — an out-of-load-order file's records are not in that graph. Enumerate the file with form='summary', then walk specific records via formids= (dropping source=).", pole.Epoch);
            if (dense) return "error: format='dense' is the in-order scan's columnar form — an off-order file scan renders text or json.";
            if (versusSpec?.Kind == LoadOrderService.PoleKind.Overlay)
                return Wire.Refuse(json, "error: an overlay pole on a SCAN would replay the SkyPatcher INI layer over every match — a per-record replay at scan scale " +
                       "(a scan comparison compares EVERY match, so it is not a bound). Name the records via formids= — the list lane reads and " +
                       "compares their post-state bodies — or read the whole layer via " + ToolNames.SkypatcherLayer + ".", pole.Epoch);
            if (where_source is not null)
            {
                // Full-vocabulary validation, mirroring the in-order engine: an unknown spelling must refuse by
                // name rather than be accepted and ignored.
                var ws = where_source.Trim().ToLowerInvariant();
                if (ws == "winner")
                    return Wire.Refuse(json, "error: where_source=winner matches on the live load-order winner — but this scan streams an out-of-load-order FILE's bodies, many of which have no winner. Match the winner by scanning the winner (drop source=), or drop where_source=.", pole.Epoch);
                if (ws is not ("scoped" or "scanned"))
                    return Wire.Refuse(json, $"error: where_source='{where_source}' is not a known source — over an out-of-load-order file the match reads the FILE's own bodies ('scoped', the default); drop where_source=, or use 'winner' on an in-order scan.", pole.Epoch);
            }

            // The off-order lane runs the same filter grammar as the in-order scan over the file's own records;
            // provenance terms still bind to the active view, which the response declares.
            HousecarlCore.ArtifactDemand? refDemand = null; string? refEcho = null;
            var refs = references;
            if (refs is { Length: > 0 })
            {
                var (toks, demand, echoSrc2, xerr) = ExpandReferenceList(refs);
                if (xerr is not null) return Wire.Refuse(json, xerr);
                refs = toks!; refDemand = demand; refEcho = echoSrc2;
            }
            IReadOnlyList<FormKey>? refFks = null, refNoneFks = null;
            if (refs is { Length: > 0 })
            {
                var (pos, neg, rerr) = SplitReferenceTargets(refs, door);
                if (rerr is not null) return Wire.Refuse(json, rerr);
                if (pos!.Count > 0) refFks = pos.Distinct().ToList();
                if (neg!.Count > 0) refNoneFks = neg.Distinct().ToList();
            }
            HousecarlCore.ArtifactDemand? fidDemand = null; string? fidEcho = null;
            IReadOnlyList<FormKey>? formidSet = null;
            if (formids is { Length: > 0 })
            {
                var (ftoks, fdemand, fecho, fxerr) = Artifacts.ExpandListInput(formids!, "formids");
                if (fxerr is not null) return Wire.Refuse(json, fxerr);
                fidDemand = fdemand; fidEcho = fecho;
                var fkList = new List<FormKey>();
                foreach (var t in ftoks!)
                {
                    if (string.IsNullOrWhiteSpace(t)) continue;
                    try { fkList.Add(door.Parse(t)); }
                    catch (Exception ex) { return Wire.Refuse(json, $"error: bad formids entry '{t}': {ex.Message}. Expected 'XXXXXX:Plugin.esp'."); }
                }
                if (fkList.Count == 0) return Wire.Refuse(json, "error: formids= expanded to an empty list — nothing to intersect the scan with.");
                formidSet = fkList;
            }
            var offGroupBy = form == "aggregate" ? project!.group_by!.Trim().ToLowerInvariant() : null;
            bool offDerived = comparisonForm;
            int offLimit = wantFile || offDerived ? int.MaxValue : counts_only ? 0 : (limit <= 0 ? 500 : limit);
            var offDemands = new List<HousecarlCore.ArtifactDemand>();
            if (refDemand is not null) offDemands.Add(refDemand);
            if (fidDemand is not null) offDemands.Add(fidDemand);

            var outcome = svc.OffOrderQuery(pole, types, refFks, null, plugins?.names,
                                            plugins?.defined_in ?? false, where, offLimit, offGroupBy, offset,
                                            formidSet, offDemands.Count > 0 ? offDemands : null, door.CapturedView,
                                            refNoneFks);

            List<KeyValuePair<string, string>> Echo()
            {
                var e = new List<KeyValuePair<string, string>>();
                void Add(string k, string? v) { if (!string.IsNullOrEmpty(v)) e.Add(new(k, v!)); }
                Add("form", form);
                Add("source", $"{pole.Plugin} (out-of-load-order)");
                Add("formids", fidEcho ?? (formidSet is not null ? $"{formidSet.Count} inline formid(s)" : null));
                Add("types", types is { Length: > 0 } ? string.Join(", ", types) : null);
                Add("references", refEcho ?? (refs is { Length: > 0 } ? string.Join(", ", refs) : null));
                Add("plugins", plugins?.names is { Length: > 0 } ? string.Join(", ", plugins.names) : null);
                if (plugins?.defined_in ?? false) Add("defined_in", "true");
                Add("where", where is { Length: > 0 } ? string.Join(" AND ", where) : null);
                Add("where_source", where_source);
                Add("group_by", offGroupBy);
                if (versusSpec is not null) Add("versus", versusSpec.Label);
                return e;
            }

            // Comparisons over the file's matches: the file IS the subject pole (its version of each match).
            if (comparisonForm && outcome.Error is null && outcome.Groups is null)
            {
                var cmpKeys = outcome.Keys.Select(k => k.ToString()).ToList();
                envelope.Add(new("total", outcome.Total.ToString()));
                headerLine += $"\n{outcome.Total} match(es) selected from the file";
                if (form == "delta")
                {
                    var rows = svc.DeltaBatch(cmpKeys, srcSpec, versusSpec!, projFields, null,
                                              out var sArm, out var rArm, out var covers, out var refusal, out var depoch);
                    if (refusal is not null)
                        return json ? JsonWire.RenderError(refusal, depoch) : "error: " + refusal + (depoch is not null ? $"\nepoch={depoch}{OrderHealth.ClauseFor(depoch)}" : "");
                    // The file selection's build and the comparison's must agree: the selection filtered through
                    // the active view, same as the in-order seam.
                    if (outcome.Epoch is not null && depoch is not null && depoch != outcome.Epoch)
                    {
                        var tear = $"the load order changed between the file scan (epoch={outcome.Epoch}) and the comparison " +
                                   $"(epoch={depoch}) — the two halves would mix builds. Retry the call.";
                        return json ? JsonWire.RenderError(tear, depoch) : "error: " + tear;
                    }
                    return DeltaResponse(rows, sArm, rArm, covers, depoch, Echo());
                }
                else
                {
                    var rows = svc.TreeBatch(cmpKeys, versusSpec!, projFields, null,
                                             out var rArm, out var covers, out var refusal, out var tepoch);
                    if (refusal is not null)
                        return json ? JsonWire.RenderError(refusal, tepoch) : "error: " + refusal + (tepoch is not null ? $"\nepoch={tepoch}{OrderHealth.ClauseFor(tepoch)}" : "");
                    if (outcome.Epoch is not null && tepoch is not null && tepoch != outcome.Epoch)
                    {
                        var tear = $"the load order changed between the file scan (epoch={outcome.Epoch}) and the comparison " +
                                   $"(epoch={tepoch}) — the two halves would mix builds. Retry the call.";
                        return json ? JsonWire.RenderError(tear, tepoch) : "error: " + tear;
                    }
                    return TreeResponse(rows, rArm, covers, tepoch, Echo());
                }
            }

            // fields/everything over the file's matches: bodies via the one-pole batch (it reads the FILE).
            if (form is ("fields" or "rows" or "everything") && !counts_only && outcome.Error is null && outcome.Groups is null)
            {
                var keys = outcome.Keys.Select(k => k.ToString()).ToList();
                var bodies = svc.ResolveBatchFromPole(keys, pole.Plugin, srcMod, bodyFields ? projFields : null,
                                                      depth, resolveNames, null, out _, out var bref, out var brefEpoch,
                                                      LeverNames.Records.ContainerHint);
                bodies = FoldRows(bodies);
                if (bref is not null)
                    return json ? JsonWire.RenderError(bref, brefEpoch)
                                : "error: " + bref + (brefEpoch is not null ? $"\nepoch={brefEpoch}{OrderHealth.ClauseFor(brefEpoch)}" : "");
                // The selection's build and the body reads' must agree, the same rule as the in-order body seam:
                // the bodies re-open the file, but the selection was made on the view.
                var offBodyEpochs = bodies.Where(o => o.Epoch is not null).Select(o => o.Epoch!).Distinct().ToList();
                if (outcome.Epoch is not null && offBodyEpochs.Any(e => e != outcome.Epoch))
                {
                    var tear = $"the load order changed between the file scan (epoch={outcome.Epoch}) and the body read " +
                               $"(epoch={string.Join(", ", offBodyEpochs.Where(e => e != outcome.Epoch))}) — the two halves would mix builds. Retry the call.";
                    return json ? JsonWire.RenderError(tear, outcome.Epoch) : "error: " + tear;
                }
                envelope.Add(new("total", outcome.Total.ToString()));
                headerLine += $"\n{outcome.Total} match(es); bodies for the {keys.Count}-row window below";
                // Selected by the off-order file scan, so the remedy vocabulary matches the body lane above.
                var offLevers = formLevers.OnScanSelection();
                string RenderOff(SpillState? sp, out bool trunc) => json
                    ? JsonWire.RenderBatch(bodies, max_chars, sp, out trunc, envelope, offLevers)
                    : headerLine + "\n" + Wire.RenderBatch(bodies, max_chars, sp, out trunc, offLevers);
                SpillState? offSpill = null;
                var offEpoch = bodies.FirstOrDefault(o => o.Epoch is not null)?.Epoch ?? outcome.Epoch;
                if (wantFile)
                {
                    var (sp, aerr) = Artifacts.WriteBatch(bodies, toFile!, "to_file", Echo(), offLevers);
                    if (aerr is not null) return json ? JsonWire.RenderError(aerr, offEpoch) : "error: " + aerr;
                    offSpill = SpillState.Spilled(sp!, manifestOnly: true);
                }
                var offRendered = RenderOff(offSpill, out var offTrunc);
                if (offSpill is null && offTrunc)
                {
                    var path = ResultsStore.NextPath(ToolNames.Records, offEpoch ?? "none");
                    var (sp, aerr) = Artifacts.WriteBatch(bodies, path, "ceiling", Echo(), offLevers);
                    if (aerr is not null) ResultsStore.Release(path);
                    offRendered = RenderOff(aerr is null ? SpillState.Spilled(sp!, manifestOnly: false) : SpillState.WriteFailed(aerr), out _);
                }
                return offRendered;
            }

            // summary / aggregate: the shared scan renders. Prefilled rows carry the file's identities, and
            // winner context rides along where a record also lives in the order.
            SpillState? spill = null;
            if (wantFile && outcome.Error is null)
            {
                var (sp, aerr) = Artifacts.WriteCrossQuery(svc, outcome, null, false, false, 1, toFile!, "to_file", Echo(), LeverNames.Records);
                if (aerr is not null)
                    return fmt is Wire.QueryFormat.Text ? "error: " + aerr : JsonWire.RenderError(aerr, outcome.Epoch);
                spill = SpillState.Spilled(sp!, manifestOnly: true);
            }
            // The off-order scan passes no field paths at all, so it never has a project= to drop; the in-order
            // lane above has to ask.
            var offQLevers = LeverNames.Records.WithNothingToDrop();
            string Render(SpillState? sp, out bool trunc) => fmt switch
            {
                Wire.QueryFormat.Dense or Wire.QueryFormat.Json => JsonWire.RenderCrossQuery(svc, outcome, null, max_chars, false, false, 1, sp, out trunc, envelope, offQLevers),
                _ => headerLine + "\n" + Wire.RenderCrossQuery(svc, outcome, null, max_chars, false, false, 1, sp, out trunc, offQLevers),
            };
            var rendered = Render(spill, out var truncated);
            if (spill is null && truncated && outcome.Error is null)
            {
                var path = ResultsStore.NextPath(ToolNames.Records, outcome.Epoch ?? "none");
                var (sp, aerr) = Artifacts.WriteCrossQuery(svc, outcome, null, false, false, 1, path, "ceiling", Echo(), LeverNames.Records);
                if (aerr is not null) ResultsStore.Release(path);
                rendered = Render(aerr is null ? SpillState.Spilled(sp!, manifestOnly: false) : SpillState.WriteFailed(aerr), out _);
            }
            return rendered;
        }
    });

    /// <summary>Recognize the one off-order-lane where-clause: <c>editorid contains &lt;text&gt;</c>.</summary>
    static bool TryEditorIdContains(string clause, out string? text)
    {
        text = null;
        var c = clause.Trim();
        const string prefix = "editorid contains ";
        if (!c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var t = c.Substring(prefix.Length).Trim();
        if (t.Length == 0) return false;
        text = t;
        return true;
    }

    static KeyValuePair<string, int> KvI(string k, int v) => new(k, v);

    /// <summary>Parse a pole expression from its wire spelling; a null element yields a null spec and the caller
    /// applies its default. Returns the named refusal, or null on success. <paramref name="subjectRole"/> marks
    /// source=, the subject: previous_provider is measured FROM the subject and so cannot be it.</summary>
    static string? ParsePole(JsonElement? el, string param, bool subjectRole, out LoadOrderService.PoleSpec? spec)
    {
        spec = null;
        if (el is not { } e || e.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        if (e.ValueKind == JsonValueKind.String)
        {
            var s = e.GetString()!.Trim();
            if (s.Length == 0 || s.Equals("winner", StringComparison.OrdinalIgnoreCase))
            { spec = LoadOrderService.PoleSpec.Winner; return null; }
            if (s.Equals("previous_provider", StringComparison.OrdinalIgnoreCase))
            {
                if (subjectRole)
                    return "error: source= is the SUBJECT of the call, and 'previous_provider' is measured FROM the subject " +
                           "(it is the plugin immediately below whatever source= names, §4.3) — so it cannot BE the subject. " +
                           "Name the subject via source= and pass versus=\"previous_provider\".";
                spec = new LoadOrderService.PoleSpec(LoadOrderService.PoleKind.PreviousProvider);
                return null;
            }
            spec = new LoadOrderService.PoleSpec(LoadOrderService.PoleKind.Named, s);
            return null;
        }
        if (e.ValueKind == JsonValueKind.Object)
        {
            if (e.TryGetProperty("overlay", out var ov))
            {
                var kind = ov.ValueKind == JsonValueKind.String ? ov.GetString()!.Trim() : null;
                if (!string.Equals(kind, "skypatcher", StringComparison.OrdinalIgnoreCase))
                    return $"error: {param}= names overlay '{kind ?? "<non-string>"}', and the one runtime overlay on this surface is " +
                           "{\"overlay\": \"skypatcher\", \"state\": \"pre\"|\"post\"} (post = after the INI layer replays; the default).";
                string? st = e.TryGetProperty("state", out var stEl) && stEl.ValueKind == JsonValueKind.String ? stEl.GetString()!.Trim() : "post";
                if (!st!.Equals("pre", StringComparison.OrdinalIgnoreCase) && !st.Equals("post", StringComparison.OrdinalIgnoreCase))
                    return $"error: {param}= overlay state '{st}' — use \"pre\" (the winner before the INI layer) or \"post\" (after it; the default).";
                spec = new LoadOrderService.PoleSpec(LoadOrderService.PoleKind.Overlay, OverlayState: st.ToLowerInvariant());
                return null;
            }
            if (!e.TryGetProperty("file", out var fEl) || fEl.ValueKind != JsonValueKind.String)
                return $"error: a structured {param}= names the plugin as {{\"file\": \"X.esp\"[, \"mod\": \"<mod folder>\"]}} or the runtime view as {{\"overlay\": \"skypatcher\", \"state\": \"pre\"|\"post\"}}.";
            string? mod = e.TryGetProperty("mod", out var mEl) && mEl.ValueKind == JsonValueKind.String ? mEl.GetString()!.Trim() : null;
            spec = new LoadOrderService.PoleSpec(LoadOrderService.PoleKind.Named, fEl.GetString()!.Trim(), mod);
            return null;
        }
        return $"error: {param}= is a string (\"winner\" | a plugin filename{(subjectRole ? "" : " | \"previous_provider\"")}) or an object ({{\"file\", \"mod\"}} | {{\"overlay\", \"state\"}}).";
    }

    /// <summary>The delta form's text render: header counts, then per record the two pole lines, the stack-above
    /// fact stated neutrally rather than as advice, and the delta-line grammar — where a truncated deep read is
    /// never rendered as 'identical'.</summary>
    static string RenderRecordsDelta(IReadOnlyList<LoadOrderService.DeltaRow> rows, int total, int differing, int identical, int errors,
                                     string headerLine, string? epoch, int maxChars, SpillState? spill, out bool truncated)
    {
        truncated = false;
        int cap = maxChars > 0 ? maxChars : Wire.DefaultMaxChars;
        bool manifestOnly = spill?.ManifestOnly ?? false;
        var sb = new StringBuilder();
        sb.Append(headerLine).Append('\n');
        sb.Append(total).Append(" record(s): ").Append(differing).Append(" differing, ").Append(identical)
          .Append(" identical, ").Append(errors).Append(" error(s)");
        if (epoch is not null) sb.Append("  epoch=").Append(epoch).Append(OrderHealth.ClauseFor(epoch));
        sb.Append('\n');
        int rendered = 0;
        foreach (var row in rows)
        {
            if (manifestOnly) break;
            if (sb.Length >= cap)
            {
                truncated = true;
                sb.Append("... [rendered ").Append(rendered).Append(" of ").Append(rows.Count)
                  .Append(" rows at max_chars=").Append(cap).Append("]\n");
                break;
            }
            sb.Append('\n').Append(row.Formid);
            if (row.Error is not null)
            {
                sb.Append("  error=").Append(row.Error).Append('\n');
                if (row.StackAbove is { Count: > 0 })
                    sb.Append("  stack above the subject (closer to winning, winner last): ").Append(string.Join(", ", row.StackAbove)).Append('\n');
                rendered++;
                continue;
            }
            var s = row.Subject!; var r = row.Reference!; var d = row.Diff!;
            sb.Append("  ").Append(s.RecordType ?? "?").Append("  ").Append(s.EditorId ?? "<no editorid>").Append('\n');
            sb.Append("  subject:   ").Append(s.Plugin).Append(" [").Append(s.Where).Append("]\n");
            sb.Append("  reference: ").Append(r.Plugin).Append(" [").Append(r.Where).Append("]\n");
            if (row.StackAbove is { Count: > 0 })
                sb.Append("  stack above the subject (closer to winning, winner last): ").Append(string.Join(", ", row.StackAbove)).Append('\n');
            if (row.Note is not null) sb.Append("  note: ").Append(row.Note).Append('\n');
            if (d.Deltas.Count == 0)
            {
                if (!d.Complete)
                    sb.Append("  no differing fields in what was read, but the deep read was TRUNCATED at the cap — NOT a clean 'identical' (Q3). Narrow with ").Append(LeverNames.Records.Fields).Append(" to compare in full.\n");
                else if (d.AgreedCount > 0)
                    sb.Append("  identical across the fields read (").Append(d.AgreedCount).Append(" value leaf/leaves agree).\n");
                else
                    sb.Append("  identical across the fields read (no differing fields).\n");
            }
            else
            {
                sb.Append("  ").Append(d.Deltas.Count).Append(d.Deltas.Count == 1 ? " difference" : " differences")
                  .Append(" — each line: ").Append(s.LabelVersus(r.Plugin)).Append("'s value (reference = ")
                  .Append(r.LabelVersus(s.Plugin)).Append("):\n");
                foreach (var delta in d.Deltas)
                {
                    if (sb.Length >= cap)
                    {
                        truncated = true;
                        AppendCutNotice(sb, "delta lines", cap);
                        break;
                    }
                    sb.Append("    - ").Append(delta).Append('\n');
                }
                if (!d.Complete)
                    sb.Append("  note: the deep read was TRUNCATED — list-content and one-sided-presence deltas are SUPPRESSED; narrow with ").Append(LeverNames.Records.Fields).Append(" to compare those in full.\n");
            }
            rendered++;
        }
        if (spill is not null) Artifacts.AppendSpillStateText(sb, spill);
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>The tree form's text render: per record the touching list in load order with the winner last,
    /// and each provider's delta against the reference. Same wording rules as the delta form — identical is
    /// never claimed over a truncated read, list contents compare by content, and reorders are flagged.</summary>
    static string RenderRecordsTree(IReadOnlyList<LoadOrderService.TreeRow> rows, int total, int contested, int errors,
                                    bool fieldsNarrow, string headerLine, string? epoch, int maxChars,
                                    SpillState? spill, out bool truncated)
    {
        truncated = false;
        int cap = maxChars > 0 ? maxChars : Wire.DefaultMaxChars;
        bool manifestOnly = spill?.ManifestOnly ?? false;
        var sb = new StringBuilder();
        sb.Append(headerLine).Append('\n');
        sb.Append(total).Append(" record(s): ").Append(contested).Append(" contested, ").Append(errors).Append(" error(s)");
        if (epoch is not null) sb.Append("  epoch=").Append(epoch).Append(OrderHealth.ClauseFor(epoch));
        sb.Append('\n');
        int rendered = 0;
        bool declarersLeadWritten = false;
        foreach (var row in rows)
        {
            if (manifestOnly) break;
            if (sb.Length >= cap)
            {
                truncated = true;
                sb.Append("... [rendered ").Append(rendered).Append(" of ").Append(rows.Count)
                  .Append(" rows at max_chars=").Append(cap).Append("]\n");
                break;
            }
            sb.Append('\n').Append(row.Formid);
            if (row.Error is not null) { sb.Append("  error=").Append(row.Error).Append('\n'); rendered++; continue; }
            sb.Append("  ").Append(row.Type ?? "?").Append("  ").Append(row.EditorId ?? "<no editorid>").Append('\n');
            sb.Append("  ").Append(row.Touchers.Count).Append(" plugin(s) touch this record (load order, winner last):\n");
            for (int i = 0; i < row.Touchers.Count; i++)
                sb.Append("    ").Append(i + 1).Append(". ").Append(row.Touchers[i])
                  .Append(i == row.Touchers.Count - 1 ? "  (winner)" : "").Append('\n');
            if (AppendChildDeclarers(sb, row, cap, ref declarersLeadWritten, out bool declarersCut))
            {
                // The row ends here, but `truncated` claims the whole ANSWER is incomplete and drives the spill,
                // so set it only when this row actually lost something: declarer lines dropped, or a diff the row
                // never reached. A sole-provider row whose complete block merely ended past cap lost nothing.
                if (declarersCut || row.Nodes.Count > 1) truncated = true;
                // A multi-provider row loses its diff whether or not declarer lines were also dropped, and each
                // notice claims one thing, so a cut row carries both notices.
                if (row.Nodes.Count > 1) AppendCutNotice(sb, "nodes", cap);
                rendered++; continue;
            }
            if (row.Nodes.Count <= 1) { rendered++; continue; }   // a sole provider has nothing to diff against
            sb.Append("  diff (field deltas vs ").Append(row.ReferencePlugin)
              .Append("; identical fields omitted; list contents compared by content, element reorders flagged):\n");
            foreach (var n in row.Nodes)
            {
                if (n.IsReference) continue;
                if (sb.Length >= cap)
                {
                    truncated = true;
                    AppendCutNotice(sb, "nodes", cap);
                    break;
                }
                sb.Append("    ").Append(n.Plugin).Append(n.IsWinner ? " (winner)" : "").Append(": ");
                if (n.Deltas.Count > 0)
                    sb.Append(string.Join("; ", n.Deltas)).Append('\n');
                else if (!n.Complete)
                    sb.Append("no differing fields in what was read, but the deep read was TRUNCATED — not a clean 'identical'.\n");
                else
                    sb.Append(fieldsNarrow
                        ? $"identical to {row.ReferencePlugin} across the fields read ({n.AgreedCount} leaf/leaves agree)\n"
                        : $"identical to {row.ReferencePlugin} (whole record; {n.AgreedCount} leaf/leaves agree)\n");
            }
            rendered++;
        }
        if (spill is not null) Artifacts.AppendSpillStateText(sb, spill);
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>The records text lane's cut notice, composed in one place so its several call sites cannot
    /// drift.</summary>
    /// <param name="what">What was cut — the notice claims this and nothing else, so a caller that cut something
    /// different names that instead.</param>
    static void AppendCutNotice(StringBuilder sb, string what, int cap) =>
        sb.Append("    ... [").Append(what).Append(" cut at max_chars=").Append(cap)
          .Append(" — raise max_chars or narrow with ").Append(LeverNames.Records.Fields).Append("]\n");

    /// <summary>The tree's precise owned-child block: which providers declare children per child-bearing field,
    /// and the negative sentence when none do. Sits above the diff, not inside it; background in
    /// `docs/architecture/records-owned-child-declarers.md`. Internal so a test can drive it against a hand-built
    /// <see cref="LoadOrderService.TreeRow"/> wider than any fixture cell.</summary>
    /// <param name="leadWritten">Set once the framing line has been stated; every later row gets the short
    /// <see cref="ReadSentences.DeclarersHeader"/> instead of repeating it.</param>
    /// <param name="blockCut">true only when declarer lines were actually dropped. false with a true return means
    /// the block is complete and the row ends at <paramref name="cap"/> — the caller names what it loses.</param>
    /// <returns>true if the row ends here.</returns>
    internal static bool AppendChildDeclarers(StringBuilder sb, LoadOrderService.TreeRow row, int cap,
                                              ref bool leadWritten, out bool blockCut)
    {
        blockCut = false;
        if (row.ChildDeclarers.Count == 0) return false;
        // The framing line has a known length, so reserve it rather than write it and regret it: a plain
        // sb.Length < cap check would put its whole length past cap with no way to take it back.
        // JsonWire.RenderTree reserves the same sentence the same way.
        string framing = leadWritten ? ReadSentences.DeclarersHeader : ReadSentences.DeclarersLead;
        if (sb.Length + framing.Length + 3 >= cap)   // 3: the two-space indent and the newline around it
        {
            blockCut = true;
            AppendCutNotice(sb, "child declarers", cap);
            return true;
        }
        sb.Append("  ").Append(framing).Append('\n');
        leadWritten = true;
        foreach (var cd in row.ChildDeclarers)
        {
            if (sb.Length >= cap)
            {
                blockCut = true;
                AppendCutNotice(sb, "child declarers", cap);
                return true;
            }
            sb.Append("    ").Append(cd.Field).Append(": ")
              .Append(ReadSentences.DeclarersNote(cd.Shape, cd.Declaring, cd.Unreadable));
            // DeclarersNote elides past DeclarerNameCap in two clauses — a collection field's `declaring` names,
            // and `unreadable` on any shape — and both are followable only in json. One remedy per line even
            // when both fired, since it is the same pointer.
            if ((cd.Shape == OwnedChildShape.Collection && cd.Declaring.Count > ReadSentences.DeclarerNameCap)
                || cd.Unreadable.Count > ReadSentences.DeclarerNameCap)
                sb.Append(ReadSentences.DeclarersOverflowRemedy);
            sb.Append('\n');
        }
        // Reached only when every declarer line was written, so the block was not cut — the last line simply
        // ended past cap. Ending the row is right, but the notice is the caller's to write over what it loses.
        return sb.Length >= cap;
    }

    /// <summary>The chain form's text render: per seed the reached nodes in BFS order with what pulled each one
    /// in, recorded cycles, the cap-truncation note — what is listed is proved — and the NPC TemplateFlags
    /// inheritance report where the walk followed a Template chain.</summary>
    static string RenderRecordsChain(IReadOnlyList<LoadOrderService.WalkSeedResult> rows, int total, int reached,
                                     int errors, string headerLine, string? epoch, int maxChars,
                                     SpillState? spill, out bool truncated)
    {
        truncated = false;
        int cap = maxChars > 0 ? maxChars : Wire.DefaultMaxChars;
        bool manifestOnly = spill?.ManifestOnly ?? false;
        var sb = new StringBuilder();
        sb.Append(headerLine).Append('\n');
        sb.Append(total).Append(" seed(s), ").Append(reached).Append(" node(s) reached, ").Append(errors).Append(" error(s)");
        if (epoch is not null) sb.Append("  epoch=").Append(epoch).Append(OrderHealth.ClauseFor(epoch));
        sb.Append('\n');
        int rendered = 0;
        foreach (var row in rows)
        {
            if (manifestOnly) break;
            if (sb.Length >= cap)
            {
                truncated = true;
                sb.Append("... [rendered ").Append(rendered).Append(" of ").Append(rows.Count)
                  .Append(" seeds at max_chars=").Append(cap).Append("]\n");
                break;
            }
            sb.Append('\n').Append(row.Seed);
            if (row.Error is not null) { sb.Append("  error=").Append(row.Error).Append('\n'); rendered++; continue; }
            sb.Append("  ").Append(row.Type ?? "?").Append("  ").Append(row.EditorId ?? "<no editorid>").Append('\n');
            if (row.Nodes.Count == 0)
                sb.Append("  no links to follow from this seed").Append(row.TruncationNote is null ? ".\n" : " before the cap.\n");
            foreach (var n in row.Nodes)
            {
                if (sb.Length >= cap)
                {
                    truncated = true;
                    sb.Append("    ... [nodes cut at max_chars=").Append(cap).Append(" — raise max_chars, or to_file= for the complete walk]\n");
                    break;
                }
                sb.Append("    d").Append(n.Depth).Append("  ").Append(n.Key);
                if (n.Type is not null) sb.Append("  ").Append(n.Type).Append("  ").Append(n.EditorId ?? "<no editorid>");
                sb.Append("  [").Append(n.Status).Append(']');
                if (n.Note is not null) sb.Append("  ").Append(n.Note);
                sb.Append("  <- ").Append(n.PulledBy).Append('\n');
            }
            foreach (var c in row.Cycles)
                sb.Append("  cycle: ").Append(c).Append('\n');
            if (row.TruncationNote is not null)
                sb.Append("  [!] ").Append(row.TruncationNote).Append('\n');
            if (row.TemplateReport is { } tr)
            {
                sb.Append("  template inheritance (TemplateFlags — a SET flag means the category is INHERITED and the seed's own local data for it is MASKED):\n");
                foreach (var c in tr)
                {
                    sb.Append("    ").Append(c.Category).Append(": ");
                    if (!c.InheritedAtSeed) sb.Append("local data ACTIVE");
                    else if (c.ProviderKey is not null)
                        sb.Append("INHERITED from ").Append(c.ProviderKey).Append(" (").Append(c.ProviderEditorId ?? "<no editorid>").Append(')');
                    else sb.Append("INHERITED");
                    if (c.Note is not null && c.InheritedAtSeed) sb.Append("  — ").Append(c.Note);
                    sb.Append('\n');
                }
            }
            rendered++;
        }
        if (spill is not null) Artifacts.AppendSpillStateText(sb, spill);
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>The reverse MGEF lane's text render: a header census over the complete seed list, each windowed
    /// seed's carriers through the shared effect-chain render, the standard explicit cut and spill
    /// marker.</summary>
    static string RenderRecordsEffectChains(IReadOnlyList<(string Seed, EffectChainResult Result)> results,
                                            int totalSeeds, int carrierRows, int carrierTotal, int errors, string headerLine,
                                            string? epoch, int maxChars, SpillState? spill, out bool truncated)
    {
        truncated = false;
        int cap = maxChars > 0 ? maxChars : Wire.DefaultMaxChars;
        bool manifestOnly = spill?.ManifestOnly ?? false;
        var sb = new StringBuilder();
        sb.Append(headerLine).Append('\n');
        sb.Append(totalSeeds).Append(" seed(s), ").Append(carrierRows).Append(" carrier row(s)");
        if (carrierTotal != carrierRows) sb.Append(" of ").Append(carrierTotal).Append(" total");
        sb.Append(", ").Append(errors).Append(" error(s)");
        if (epoch is not null) sb.Append("  epoch=").Append(epoch).Append(OrderHealth.ClauseFor(epoch));
        sb.Append('\n');
        int rendered = 0;
        foreach (var (seed, result) in results)
        {
            if (manifestOnly) break;
            if (sb.Length >= cap)
            {
                truncated = true;
                sb.Append("... [rendered ").Append(rendered).Append(" of ").Append(results.Count)
                  .Append(" seeds at max_chars=").Append(cap).Append("]\n");
                break;
            }
            sb.Append('\n').Append("seed ").Append(seed).Append('\n');
            sb.Append(Wire.RenderEffectChain(result, cap, "walk.max_nodes")).Append('\n');
            rendered++;
        }
        if (spill is not null) Artifacts.AppendSpillStateText(sb, spill);
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>The info_order form's text render: per topic its identity, then the merged-order body from
    /// <see cref="DialogueWire.AppendInfoOrderView"/> — one shared render, so the MOVED annotations and the
    /// confidence gates cannot drift from the dialogue surface's.</summary>
    static string RenderRecordsInfoOrder(IReadOnlyList<LoadOrderService.InfoOrderRow> rows, int total, int contested,
                                         int errors, string headerLine, string? epoch, int maxChars,
                                         SpillState? spill, out bool truncated)
    {
        truncated = false;
        int cap = maxChars > 0 ? maxChars : Wire.DefaultMaxChars;
        bool manifestOnly = spill?.ManifestOnly ?? false;
        var sb = new StringBuilder();
        sb.Append(headerLine).Append('\n');
        sb.Append(total).Append(" topic(s): ").Append(contested).Append(" contested, ").Append(errors).Append(" error(s)");
        if (epoch is not null) sb.Append("  epoch=").Append(epoch).Append(OrderHealth.ClauseFor(epoch));
        sb.Append('\n');
        int rendered = 0;
        foreach (var row in rows)
        {
            if (manifestOnly) break;
            if (sb.Length >= cap)
            {
                truncated = true;
                sb.Append("... [rendered ").Append(rendered).Append(" of ").Append(rows.Count)
                  .Append(" rows at max_chars=").Append(cap).Append("]\n");
                break;
            }
            sb.Append('\n').Append(row.Formid);
            if (row.Error is not null) { sb.Append("  error=").Append(row.Error).Append('\n'); rendered++; continue; }
            sb.Append("  ").Append(row.Type ?? "?").Append("  ").Append(row.EditorId ?? "<no editorid>")
              .Append("  winner=").Append(row.WinnerPlugin ?? "?").Append('\n');
            if (row.Order is null)
                sb.Append("  [!] the merge could not be computed for this topic (its key did not resolve in the touching index).\n");
            else if (row.Order.Order.Count == 0 && row.Order.Complete)
                sb.Append("  no INFO lines — every touching plugin's child list is empty.\n");
            else if (!DialogueWire.AppendInfoOrderView(sb, row.Order, "", cap, indent: false))
            {
                truncated = true;
                break;
            }
            rendered++;
        }
        if (spill is not null) Artifacts.AppendSpillStateText(sb, spill);
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>The list-lane summary render: one identity-and-winner line per outcome, or its per-item error —
    /// the batch shape of the scan lane's summary rows. Budget-bounded with the standard explicit cut, and the
    /// spill marker rides in-band in both formats.</summary>
    static string RenderRecordsSummary(IReadOnlyList<ReadOutcome> outcomes, bool json, string headerLine,
                                       List<KeyValuePair<string, string>> envelope, int maxChars, SpillState? spill, out bool truncated)
    {
        truncated = false;
        int cap = maxChars > 0 ? maxChars : Wire.DefaultMaxChars;
        bool manifestOnly = spill?.ManifestOnly ?? false;
        var epoch = outcomes.FirstOrDefault(o => o.Epoch is not null)?.Epoch;
        if (json) return JsonWire.RenderRecordsSummary(outcomes, cap, envelope, spill, out truncated);

        var sb = new StringBuilder();
        sb.Append(headerLine).Append('\n');
        sb.Append(outcomes.Count).Append(" record(s)");
        if (epoch is not null) sb.Append("  epoch=").Append(epoch).Append(OrderHealth.ClauseFor(epoch));
        sb.Append('\n');
        int rendered = 0;
        foreach (var o in outcomes)
        {
            if (manifestOnly) break;
            if (sb.Length >= cap)
            {
                sb.Append("... [rendered ").Append(rendered).Append(" of ").Append(outcomes.Count)
                  .Append(" at max_chars=").Append(cap).Append("]\n");
                truncated = true;
                break;
            }
            if (o.Error is not null) sb.Append(o.FormKey).Append("  error=").Append(o.Error).Append('\n');
            else
            {
                sb.Append(o.FormKey);
                Wire.AppendRuntime(sb, o.RuntimeFormId, o.RuntimeFormIdNote);
                sb.Append("  ").Append(o.Record!.Type)
                  .Append("  ").Append(o.Record.EditorId ?? "<no editorid>")
                  .Append("  source=").Append(o.SourcePlugin ?? "?");
                if (o.WinnerPlugin is not null) sb.Append("  winner=").Append(o.WinnerPlugin).Append("  override_depth=").Append(o.OverrideDepth);
                sb.Append('\n');
            }
            rendered++;
        }
        if (spill is not null) Artifacts.AppendSpillStateText(sb, spill);
        return sb.ToString();
    }

    /// <summary>The list-lane aggregate render: count the resolved rows by winner, type or defined_in — the batch
    /// twin of the scan lane's count table. Per-item errors get their own named bucket rather than dropping out
    /// of the census, and it carries the same response envelope as every other form, including the resolved
    /// source statement and the epoch-coverage qualifier the tool description promises unconditionally.</summary>
    static string RenderListAggregate(IReadOnlyList<ReadOutcome> outcomes, string groupBy, bool json, bool dense, string? epoch,
                                      string headerLine, List<KeyValuePair<string, string>> envelope)
    {
        var gb = groupBy.Trim().ToLowerInvariant();
        if (gb is not ("winner" or "type" or "defined_in"))
            return Wire.Refuse(json, $"error: project.group_by='{groupBy}' is not a count key — use 'winner', 'type', or 'defined_in'.");
        var groups = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int errors = 0;
        foreach (var o in outcomes)
        {
            if (o.Error is not null) { errors++; continue; }
            var key = gb switch
            {
                "type" => o.Record!.Type,
                "defined_in" => o.FormKey.ModKey.FileName.ToString(),
                _ => o.WinnerPlugin ?? "?",
            };
            groups[key] = groups.GetValueOrDefault(key) + 1;
        }
        var rows = groups.OrderByDescending(g => g.Value).ThenBy(g => g.Key, StringComparer.Ordinal).ToList();
        if (json || dense)
            return JsonWire.RenderListAggregate(gb, rows, outcomes.Count, errors, epoch, envelope);
        var sb = new StringBuilder();
        sb.Append(headerLine).Append("  group_by=").Append(gb).Append('\n');
        sb.Append(outcomes.Count).Append(" record(s)");
        if (errors > 0) sb.Append("  (").Append(errors).Append(" per-item error(s) — counted apart, listed via form='summary')");
        if (epoch is not null) sb.Append("  epoch=").Append(epoch).Append(OrderHealth.ClauseFor(epoch));
        sb.Append('\n');
        foreach (var (key, count) in rows.Select(r => (r.Key, r.Value)))
            sb.Append("  ").Append(count.ToString().PadLeft(6)).Append("  ").Append(key).Append('\n');
        return sb.ToString();
    }

    /// <summary>The first quantifier token in a projection path, or null — a bracket key beginning '*', spelled back
    /// as the caller wrote it so the refusal names their own token.</summary>
    static string? QuantifierToken(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        foreach (var seg in path.Split('.'))
        {
            int open = seg.IndexOf('[');
            if (open < 0 || !seg.EndsWith("]", StringComparison.Ordinal)) continue;
            // A quantifier on the containment step is that grammar's mistake, not a projection one — leave it to
            // the read walk's shared check, so where= and project.fields refuse it in the same sentence.
            if (HousecarlCore.ContainmentIndex.IsParentStep(seg[..open])) continue;
            var key = seg[(open + 1)..^1];
            if (key.Length > 0 && key[0] == '*') return $"[{key}]";
        }
        return null;
    }

    /// <summary>references= @file expansion with the negation sigil carried across it: '!@&lt;path&gt;' excludes every
    /// target the file names, so the negated entry spells a list file the same way the positive one does. The sigil
    /// is stripped before the expander sees it — the expander decides "this is a file" on the first character — and
    /// put back on each expanded token.</summary>
    static (string[]? Tokens, HousecarlCore.ArtifactDemand? Demand, string? EchoSource, string? Error)
        ExpandReferenceList(string[] refs)
    {
        var bare = new string[refs.Length];
        bool anyNegatedFile = false;
        for (int i = 0; i < refs.Length; i++)
        {
            var t = refs[i]?.TrimStart() ?? "";
            if (t.Length > 1 && t[0] == '!' && t[1..].TrimStart().StartsWith("@", StringComparison.Ordinal))
            { anyNegatedFile = true; bare[i] = t[1..].TrimStart(); }
            else bare[i] = refs[i];
        }
        if (!anyNegatedFile) return Artifacts.ExpandListInput(refs, "references");
        var (toks, demand, echo, err) = Artifacts.ExpandListInput(bare, "references");
        if (err is not null) return (null, null, null, err);
        // '@file' stands in place of the whole list, so a negated one negates every token it expanded to.
        return (toks!.Select(t => "!" + t.Trim()).ToArray(), demand, echo is null ? null : "!" + echo, null);
    }

    /// <summary>Split references= into the targets a match must link to and the ones it must NOT: a leading '!'
    /// negates that entry. The two compose by AND — "links to A and to neither B nor C" is one call. A FormID
    /// never begins with '!', so the sigil cannot collide with a target token.</summary>
    static (List<FormKey>? Positive, List<FormKey>? Negative, string? Error)
        SplitReferenceTargets(string[] refs, FormIdDoor door)
    {
        var pos = new List<FormKey>();
        var neg = new List<FormKey>();
        foreach (var r in refs)
        {
            if (string.IsNullOrWhiteSpace(r)) continue;
            var tok = r.Trim();
            bool negated = tok[0] == '!';
            if (negated) tok = tok[1..].Trim();
            if (tok.Length == 0)
                return (null, null, "error: a references= entry is just '!' and names no target — write '!XXXXXX:Plugin.esp' to exclude the records that reference it.");
            try { (negated ? neg : pos).Add(door.Parse(tok)); }
            catch (Exception ex) { return (null, null, $"error: bad references FormID '{r}': {ex.Message}. Expected 'XXXXXX:Plugin.esp', or '!XXXXXX:Plugin.esp' to exclude."); }
        }
        return (pos, neg, null);
    }
}
