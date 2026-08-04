using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;
using Mutagen.Bethesda.Plugins;

namespace HousecarlMcp;

/// <summary>
/// housecarl_records — the 2.0 S1 read surface, COMPLETE (tool-surface-2.0 W2; SPEC §2.2/§3/§4/§6.1).
///
/// ONE read tool: SELECT (which records) × SOURCE (whose version) × PROJECT (what shape) × TRANSPORT compose in a
/// single call, over the SAME proven engine lanes the 1.x read tools drive (CrossQuery / ResolveBatch /
/// ResolveRefs / the one-pole batch / the plugin-file overlay) — consolidation of the surface, not a re-implementation
/// of the engine.
///
/// All NINE PROJECT forms are live — identity | summary | fields | everything | aggregate | delta | tree | chain |
/// info_order — each form-scoped, so a sub-parameter exists only inside the form that carries it (§2.2). SOURCE is
/// the §4.2 pole grammar: the winner by default, a named plugin resolved wherever it lives (active or on disk
/// out of the order, with the resolved arm always stated), the SkyPatcher overlay pre/post state, and — as the
/// comparison REFERENCE, `versus=` — `previous_provider`. SELECT carries the full `where=` grammar (including the
/// `winner` provenance term), `references=`, and the §3 `walk=` traversal construct, forward closure and the typed
/// MGEF reverse lane alike.
///
/// The 1.x read tools stay registered and unchanged through the build waves; they retire at 2.0.0 (clean cut,
/// CHARTER_PHASE4 §3.4a), at which point a call naming one is answered with its successor spelling here.
/// </summary>
[McpServerToolType]
public static class RecordsTools
{
    /// <summary>The plugins= SELECT scope: which records are CONSIDERED (records these plugins touch) — a different
    /// question from source=, which decides whose VERSION is read. defined_in lives inside the scope because it has
    /// no meaning without one (SPEC §2.2 form-scoping).</summary>
    public sealed class RecordsScope
    {
        [Description("Plugin filenames to scope the scan to (records those plugins touch), e.g. [\"Requiem.esp\"].")]
        public string[]? names { get; set; }

        [Description("When true, keep only records DEFINED IN (originating from) the named plugins, dropping records they merely override.")]
        public bool defined_in { get; set; }
    }

    /// <summary>PROJECT — the shape of the answer. ONE form; sub-parameters exist only inside the forms that carry
    /// them (SPEC §2.2: form-scoped and structured — the flat spelling for an illegal pairing does not exist).</summary>
    public sealed class RecordsProject
    {
        [Description("The form: 'identity' (FormID -> type/editorid/name/winner — the labeling form; needs formids=) | 'summary' (identity plus winner/override-depth header facts — the default) | 'fields' (named field values; takes fields= and depth=) | 'everything' (the full record body; takes depth=) | 'aggregate' (a counted table; takes group_by=) | 'delta' (subject vs reference, differences only — source= is the subject, versus= the reference; takes fields= to narrow) | 'tree' (every provider of each record in priority order, winner last, each diffed against the reference pole — default the winner; takes fields=) | 'info_order' (DIAL topics only: the effective MERGED INFO sequence across every touching plugin — the order the game walks, with MOVED annotations; the 'why does the wrong line play' diagnostic). The 'chain' traversal form is refused by NAME until it lands.")]
        public string? form { get; set; }

        [Description("fields form only: dotted field paths to read, e.g. [\"BasicStats.Damage\", \"Keywords\", \"Effects\"]. Index a list/dict element with BRACKETS ('Effects[0].Data.Magnitude').")]
        public string[]? fields { get; set; }

        [Description("fields/everything forms: expansion depth for list/dict/substruct CONTENTS (default 1 = a container shown as a count). THIS is the expansion knob: fields=[\"Effects\"], depth=4 reaches every effect's Magnitude/Area/Duration — no hand-written index guessing.")]
        public int? depth { get; set; }

        [Description("aggregate form only: the count key — 'winner' (by winning plugin), 'type' (by record type; needs types= or plugins=), or 'defined_in' (by defining plugin).")]
        public string? group_by { get; set; }

        [Description("fields/everything forms: annotate every FormLink value with its target's identity (-> editorid \"Name\"). Display-only — the token itself still round-trips to a write.")]
        public bool resolve_names { get; set; }
    }

    /// <summary>The §3 traversal construct: follow record-to-record FORM LINKS and select what the walk reaches.
    /// This traverses BETWEEN records; expanding nested fields WITHIN one record is project.depth (the declared
    /// seam). Seeds are this call's own SELECT (formids=, or a scan's matches).</summary>
    public sealed class RecordsWalk
    {
        [Description("Link-bearing field paths that start the walk from each seed, e.g. [\"HeadParts\", \"WornArmor\"]. Omit for every link on the seed.")]
        public string[]? seed_paths { get; set; }

        [Description("The link path followed at every LATER hop. \"*\" (default) walks every link — full closure. A named path restricts to one chain, e.g. \"Template\" for NPC template inheritance.")]
        public string? follow { get; set; }

        [Description("'forward' (default) — what the seeds point AT (cheap: each hop is one link resolve). 'reverse' — what points AT the seeds; depth 1 only (reverse is a bounded scan; transitive reverse is refused naming the reverse-reference index as the future capability). The general reverse spelling on this surface IS references= (same construct); walk.direction='reverse' serves the typed MGEF lane — magic-effect seeds get per-carrier magnitude/area/duration (types= narrows the carrier types; walk.max_nodes bounds each seed's carrier rows; limit=/offset= window the SEEDS).")]
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

    [McpServerTool(Name = "housecarl_records", ReadOnly = true, Title = "Read records (the 2.0 read surface)"),
     Description(
         "Read Bethesda records from the load order — ONE read surface: which records (SELECT) x whose version " +
         "(SOURCE) x what shape of answer (PROJECT) compose in a single call.\n\n" +
         "A FormID is 'XXXXXX:Plugin.esp' — 6 hex digits, a colon, the defining master's filename. Every " +
         "list-valued parameter is set-valued (one item is a set of one), and formids=/references= accept a single " +
         "\"@<absolute path>\" element to read the list from a file — including a spilled result artifact from an " +
         "earlier call (its identity column becomes the list, epoch-checked against the then-current build).\n\n" +
         "SELECT: formids= | types= | plugins= (scope: which records are considered) | conflicts_only= | where= " +
         "(body predicates, ANDed: comparisons 'BasicStats.Damage >= 50', 'editorid contains Iron', 'editorid " +
         "startswith REQ_', flag tests 'BodyTemplate.FirstPersonFlags has Body', presence 'VirtualMachineAdapter " +
         "exists', membership 'formid not in @<file>' / 'Race in [XXXXXX:A.esm, YYYYYY:B.esm]' (list entries " +
         "separate on commas/newlines with brackets and quotes stripped — a value that itself contains a comma " +
         "or bracket is not expressible in a list; test it with '='), ONE '->' link step " +
         "'Perks->editorid startswith REQ_NULL_', and the provenance term 'winner = X.esp' — which records does X " +
         "WIN; that term forces winner resolution over the scanned scope, the same declared cost as any winner " +
         "scan) | references= (reverse, one step — requires a bounding types=/plugins= scope; the reverse-reference " +
         "index that would lift the bound is a known future capability). UNION-ARM tip: when a field is one of " +
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
         "PROJECT is a single form (see project=): identity | summary (default) | fields | everything | aggregate | " +
         "delta | tree | chain | info_order. Sub-parameters live INSIDE the form that uses them (depth belongs to fields/everything, " +
         "group_by to aggregate, fields to fields/delta/tree) — there is no flat spelling for an illegal pairing. " +
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
         "This tool never writes. Authoring goes through the write tools (set_field / bulk_apply / create_record / " +
         "remove_record / forward_record and their successors).")]
    public static string Records(
        LoadOrderService svc,
        [Description("SELECT: records by FormID ('XXXXXX:Plugin.esp'), or [\"@<absolute path>\"] to read the list from a file / spilled artifact. Results return in input order; a bad or absent FormID is a per-item error, never a failed batch.")]
            string[]? formids = null,
        [Description("SELECT: record types — signatures ('WEAP') or catalog names ('Weapon'); the scan streams the UNION. types alone enumerates every record of those types in whatever the SOURCE names.")]
            string[]? types = null,
        [Description("SELECT: the plugin SCOPE — which records are CONSIDERED (records these plugins touch). Not the same question as source= (whose VERSION is read).")]
            RecordsScope? plugins = null,
        [Description("SELECT: keep only records touched by more than one plugin (the contested set).")]
            bool conflicts_only = false,
        [Description("SELECT: body predicates, ANDed — see the tool description for the full grammar (comparisons, contains/startswith, has, exists/missing, in/'not in' membership incl. @file and artifact re-entry, the '->' link step, the 'winner' provenance term, and the 'editorid' term that replaces editorid_contains=). A body scan — must be combined with types= or plugins= to bound the work. A wrong path is reported loud, never a silent '0 matches'.")]
            string[]? where = null,
        [Description("Which BODY the where= predicates decide the MATCH on: 'scoped' (default — the body the scan streams) or 'winner' (the live load-order winner regardless of scan scope; the post-patch audit answer). Match only — fields_source= independently governs display.")]
            string? where_source = null,
        [Description("SELECT: find records that REFERENCE these FormIDs (reverse, one step; OR over the list, each match names which target(s) it hit). Requires a bounding types= or plugins= scope — see the cost declaration in the tool description. Accepts [\"@<path>\"] like formids=.")]
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
            string? to_file = null) => Guard.Tool("housecarl_records", () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;

        // ---- TRANSPORT: format --------------------------------------------------------------------------
        var fmt = Wire.CrossQueryFormat(format, out var ferr);
        if (ferr is not null) return ferr;
        bool json = fmt is Wire.QueryFormat.Json;
        bool dense = fmt is Wire.QueryFormat.Dense;

        // ---- PROJECT: form + form-scoping ---------------------------------------------------------------
        var form = project?.form?.Trim().ToLowerInvariant() ?? "summary";
        switch (form)
        {
            case "identity" or "summary" or "fields" or "everything" or "aggregate" or "delta" or "tree" or "info_order" or "chain": break;
            default:
                return $"error: project.form='{project?.form}' is not a form — use identity | summary | fields | everything | aggregate | delta | tree | chain | info_order.";
        }
        bool comparisonForm = form is "delta" or "tree";
        // Sub-parameters exist only inside their forms (SPEC §2.2) — a stray one is refused by name, so the caller
        // learns the form-scoping rule instead of getting a silently-ignored knob.
        if (project?.fields is { Length: > 0 } && form != "fields" && !comparisonForm)
            return $"error: project.fields belongs to the 'fields'/'delta'/'tree' forms (got form='{form}'). Set project.form, or drop fields.";
        if (form == "fields" && project?.fields is not { Length: > 0 })
            return "error: the 'fields' form names its field paths — pass project.fields=[\"<path>\", …] (or use form='everything' for the full body).";
        if (project?.depth is { } dv)
        {
            // ANY explicit depth is form-scoped (review round 3: `depth: 1` was accepted-and-dropped on other
            // forms while `depth: 2` refused — the rule must not depend on the value), and 0/negative is refused
            // rather than silently becoming 1.
            if (form is not ("fields" or "everything"))
                return comparisonForm
                    ? $"error: project.depth belongs to the 'fields'/'everything' forms — the '{form}' comparison always deep-reads BOTH sides at the diff engine's fixed depth so line sets correspond (narrow with project.fields instead)."
                    : $"error: project.depth expands field contents and belongs to the 'fields'/'everything' forms (got form='{form}').";
            if (dv < 1)
                return $"error: project.depth={dv} — depth must be >= 1 (1 shows a container as a collapsed summary; higher opens it).";
        }
        if (project?.group_by is not null && form != "aggregate")
            return $"error: project.group_by belongs to the 'aggregate' form only (got form='{form}'). Set project.form='aggregate', or drop group_by.";
        if (form == "aggregate")
        {
            // Validated HERE, before any read runs (review round 3: the list lane validated it only inside the
            // render, after the batch had already been paid for).
            var gbv = project?.group_by?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(gbv))
                return "error: the 'aggregate' form names its count key — pass project.group_by='winner' | 'type' | 'defined_in'.";
            if (gbv is not ("winner" or "type" or "defined_in"))
                return $"error: project.group_by='{project!.group_by}' is not a count key — use 'winner', 'type', or 'defined_in'.";
        }
        if (project is { resolve_names: true } && form is not ("fields" or "everything"))
            return $"error: project.resolve_names annotates field values and belongs to the 'fields'/'everything' forms (got form='{form}').";
        int depth = project?.depth is { } d && d > 0 ? d : 1;
        var projFields = form is "fields" or "delta" or "tree" ? project?.fields : null;
        bool resolveNames = project?.resolve_names ?? false;

        // ---- SOURCE: the §4.2 pole grammar (source = the SUBJECT; versus = the comparison REFERENCE) ----
        if (ParsePole(source, "source", subjectRole: true, out var srcSpec) is { } sperr) return sperr;
        srcSpec ??= LoadOrderService.PoleSpec.Winner;
        if (ParsePole(versus, "versus", subjectRole: false, out var versusSpec) is { } vperr) return vperr;

        // versus= belongs to the comparison forms; the delta form REQUIRES it (§4.1 — a delta has two poles).
        if (versusSpec is not null && !comparisonForm)
            return $"error: versus= is the comparison REFERENCE pole and belongs to the 'delta'/'tree' forms (got form='{form}') — set project.form='delta' (subject vs reference) or 'tree' (every provider vs reference), or drop versus=.";
        if (form == "delta" && versusSpec is null)
            return "error: the 'delta' form compares the subject (source=, default the winner) against a REFERENCE — pass versus= (\"winner\" | a plugin filename | \"previous_provider\" | {\"overlay\": …}).";
        if (form == "tree")
        {
            versusSpec ??= LoadOrderService.PoleSpec.Winner;
            if (versusSpec.Kind == LoadOrderService.PoleKind.PreviousProvider)
                return "error: versus='previous_provider' is subject-relative and pairs with the 'delta' form (one subject, one reference below it) — a tree diffs EVERY provider against ONE reference pole. Use form='delta', or a named/winner versus= on the tree.";
        }
        // ---- walk= (the §3 traversal construct) ---------------------------------------------------------
        string walkDirection = "forward";
        int walkDepth = 16, walkMaxNodes = 2000;
        var walkExclusions = new List<(string Match, bool Refuse)>();
        if (walk is not null)
        {
            var dir = walk.direction?.Trim().ToLowerInvariant();
            if (dir is not (null or "" or "forward" or "reverse"))
                return $"error: walk.direction='{walk.direction}' — use 'forward' (what the seeds point at) or 'reverse' (what points at them; depth 1).";
            if (!string.IsNullOrEmpty(dir)) walkDirection = dir!;
            if (walk.depth is { } wd)
            {
                if (wd < 1) return $"error: walk.depth={wd} — depth must be >= 1 (hops from the seed).";
                walkDepth = wd;
            }
            if (walk.max_nodes is { } wn)
            {
                if (wn < 1) return $"error: walk.max_nodes={wn} — the node budget must be >= 1.";
                walkMaxNodes = wn;
            }
            foreach (var x in walk.exclusions ?? Array.Empty<RecordsWalkExclusion>())
            {
                if (string.IsNullOrWhiteSpace(x.match))
                    return "error: a walk.exclusions entry needs match= — the record type name a read reports (e.g. 'Race').";
                var sev = x.severity?.Trim().ToLowerInvariant();
                if (sev is not ("stop" or "refuse"))
                    return $"error: walk.exclusions '{x.match}': severity='{x.severity}' — use 'stop' (prune, record the boundary) or 'refuse' (the whole walk fails loud).";
                walkExclusions.Add((x.match!.Trim(), sev == "refuse"));
            }
            if (walkDirection == "reverse")
            {
                if (walk.depth is > 1)
                    return "error: walk.direction='reverse' with depth>1 is a TRANSITIVE reverse lookup — no index exists for it today, so it is refused rather than run as an unbounded scan-of-scans (the reverse-reference index is the known future capability that lifts this). Depth-1 reverse: references= with a bounding types=/plugins=, or MGEF seeds under form='chain' for the typed carrier lane.";
                if (walk.seed_paths is { Length: > 0 } || walk.exclusions is { Length: > 0 } || walk.follow is not null)
                    return "error: walk.seed_paths/follow/exclusions shape a FORWARD expansion — a reverse walk scans TOWARD the seeds. Drop them.";
                if (form != "chain")
                    return "error: the reverse walk's general spelling on this surface IS references= (the same construct, depth 1, bounded by types=/plugins=) — walk.direction='reverse' serves form='chain' with MGEF seeds (per-carrier magnitude/area/duration).";
            }
            if (references is { Length: > 0 })
                return "error: walk= and references= are the same construct (references= IS the reverse walk at depth 1) — use one spelling per call.";
            if (dense)
                return "error: format='dense' renders positional columnar cells 1:1 with requested field paths, and a walk's outputs (chains; reached-set reads) have no fixed column set — use format='text' or 'json'.";
            if (comparisonForm || form is "info_order" or "identity")
                return $"error: walk= derives a selection (the reached set), and the '{form}' form does not consume one — use form='chain' for the walk's own paths, or summary/fields/everything/aggregate over the reached set. To compare reached records, walk with to_file= and re-enter the artifact via formids=[\"@<file>\"] with form='{form}'.";
            if (where is { Length: > 0 })
                return "error: walk= composed with where= (filtering the reached set by predicate) — walk with to_file=, then re-enter the artifact on a bounded scan via where=[\"formid in @<file>\", …]; the reached set becomes the scan's identity list.";
        }
        if (form == "chain" && walk is null)
            return "error: the 'chain' form renders a walk's paths — pass walk= (e.g. walk={\"follow\": \"Template\"} over NPC seeds; reverse MGEF carriers: walk={\"direction\": \"reverse\"} with MGEF formids=).";

        // The existing single-pole lanes below drive off the named-pole fields; the richer specs dispatch to the
        // comparison/overlay lanes before reaching them.
        string? srcName = srcSpec.Kind == LoadOrderService.PoleKind.Named ? srcSpec.Plugin : null;
        string? srcMod = srcSpec.Kind == LoadOrderService.PoleKind.Named ? srcSpec.Mod : null;
        bool srcOverlay = srcSpec.Kind == LoadOrderService.PoleKind.Overlay;

        // ---- fields_source (display pole) ---------------------------------------------------------------
        // Value first, THEN the lane rules (re-review R3-3): 'scoped'/'scanned' are the documented no-op
        // defaults and stay accepted everywhere; only the actual RETARGET ('winner') is refused on the lanes
        // that cannot honor it, and an unknown value always gets the not-a-known-source refusal.
        bool winnerFields = false;
        if (!string.IsNullOrWhiteSpace(fields_source))
        {
            var fs = fields_source.Trim().ToLowerInvariant();
            if (fs == "winner") winnerFields = true;
            else if (fs is not ("scoped" or "scanned"))
                return $"error: fields_source='{fields_source}' — use 'winner' (display the live winner's values) or omit it (display the matched body). A NAMED display pole is the scope-vs-pole composition: plugins= selects, source= names whose version the body forms read.";
            if (winnerFields && comparisonForm)
                return $"error: fields_source='winner' retargets what a matched row DISPLAYS, and the '{form}' form's display IS its two poles (source=/versus=) — name the version you want as a pole instead.";
            if (winnerFields && form is "chain" or "info_order")
                return $"error: fields_source='winner' retargets FIELD display, and the '{form}' form renders no field values — drop it.";
            if (winnerFields && walk is not null)
                return "error: fields_source='winner' — a walk's reading forms display the source= pole's version of the reached set: name the version via source= instead.";
        }

        // ---- lane decision ------------------------------------------------------------------------------
        bool hasFormids = formids is { Length: > 0 };
        bool hasScan = types is { Length: > 0 } || plugins?.names is { Length: > 0 } || conflicts_only
                       || where is { Length: > 0 } || references is { Length: > 0 };
        if (!hasFormids && !hasScan)
            return "error: select something — formids= (a record list), or a scan scope: types=, plugins=, conflicts_only=true, where=, references=.";
        // formids= composes with the scan terms (the W2 formids×scan composition): the identity set intersects
        // the scan's selection — or IS the universe when it is the only bound. The reverse MGEF walk keeps its
        // own lane (formids are the seeds; types= narrows the typed carrier scan).
        bool reverseWalk = walk is not null && walkDirection == "reverse";
        if (reverseWalk && (plugins?.names is { Length: > 0 } || conflicts_only || where is { Length: > 0 }))
            return "error: the reverse MGEF walk takes formids= (the effects) and optionally types= (narrowing the carrier types) — the general bounded reverse over other scan terms is the references= spelling.";
        if (reverseWalk && !hasFormids)
            return "error: the reverse walk needs its seeds — pass formids= (the MGEF(s) whose carriers to trace).";
        // dense is DEFINED as positional columnar cells 1:1 with the requested field paths (the tool description's
        // own rule) — the forms with no fixed column set refuse by name rather than quietly switching transport
        // (re-review: everything fell back to text, aggregate to the json table, neither saying so).
        if (dense && form == "everything")
            return "error: format='dense' renders positional columnar cells 1:1 with requested field paths, and the 'everything' form has no fixed column set — use format='text' or 'json', or name the paths via form='fields'.";
        if (dense && form == "aggregate")
            return "error: format='dense' is the per-row columnar transport, and the 'aggregate' form is a count table — its json render IS the compact form; use format='json'.";
        if (dense && comparisonForm)
            return $"error: format='dense' renders positional columnar cells 1:1 with requested field paths, and the '{form}' form's rows are variable-length delta lists with no fixed column set — use format='text' or 'json'.";
        if (dense && form == "info_order")
            return "error: format='dense' renders positional columnar cells 1:1 with requested field paths, and the 'info_order' form is an ordered sequence render with no fixed column set — use format='text' or 'json'.";
        if (form == "info_order" && srcSpec.Kind != LoadOrderService.PoleKind.Winner)
            return "error: the info_order form merges EVERY plugin touching each topic — that merge is the answer, so a source= pole has no seat here (each line already names the plugin that placed it). Drop source=.";
        // fields_source= is the SCAN lane's display pole (it retargets what a matched row DISPLAYS); the list
        // lane's read IS its display, so the request would be silently meaningless there — refuse by name
        // (re-review: it was accepted and dropped). The GENERAL display pole is the composition itself:
        // plugins= (scope) x source= (whose version) reads any pole's bodies; fields_source='winner' remains
        // the winner shorthand on a scoped scan.
        if (winnerFields && formids is { Length: > 0 } && !hasScan)
            return "error: fields_source= is the scan lane's display pole — on a formids= read the version you want IS the source: name it via source= (source=\"winner\" is the default).";

        if (offset < 0) return $"error: offset={offset} — offset must be >= 0.";
        if (offset > 0 && form == "aggregate")
            return "error: the aggregate form counts ALL selected records (a count table has no row window), so offset= has nothing to page — drop offset=, or drop the aggregate form for per-record rows.";
        var toFile = to_file?.Trim();
        bool wantFile = !string.IsNullOrEmpty(toFile);
        if (wantFile)
        {
            if (Artifacts.ValidateToFile(toFile!) is { } verr) return verr;
            if (offset > 0) return "error: to_file= captures the COMPLETE result (the artifact is never a window), so offset= has nothing to page — drop offset=.";
            if (form == "aggregate") return "error: to_file= writes row artifacts, and the aggregate form is a count table with no record rows — drop one of the two.";
            if (counts_only) return "error: counts_only= returns the census with no rows, and to_file= writes the rows — the two contradict; drop one (review: this pair used to return the census and silently write nothing).";
        }
        if (where_source is not null && where is not { Length: > 0 })
            return "error: where_source= retargets the where= predicates and needs where= — add predicates, or drop where_source=.";

        // ---- the response envelope (form + resolved source arm) -----------------------------------------
        // Text renders get it as a header line; json renders carry the same pairs as top-level fields.
        var envelope = new List<KeyValuePair<string, string>> { new("form", form) };
        string headerLine = $"records  form={form}";
        void Arm(string statement)
        {
            // ONE source statement per response (round-3 F1: the scan lane pre-armed and the derived forms'
            // pipelines armed again — two "source" properties in one json object). The specific arm is always
            // stated first now; first-wins is the backstop that keeps a future double-call from lying twice.
            if (envelope.Any(kv => kv.Key == "source")) return;
            envelope.Add(new("source", statement));
            headerLine += $"  source={statement}";
        }
        // When a walk (or a scan) derived the selection this call now reads, the two captures meet at a seam —
        // every downstream form's epoch is compared against the deriving step's, and a divergence refuses loud
        // (the mixed-builds rule every two-capture path on this surface follows).
        string? expectEpoch = null;
        string? SeamTear(string? epoch) =>
            expectEpoch is not null && epoch is not null && epoch != expectEpoch
                ? $"the load order changed between deriving the selection (epoch={expectEpoch}) and reading it (epoch={epoch}) — the two halves would mix builds. Retry the call."
                : null;

        // limit=/offset= WINDOW the list lane's render (round-3 review: they were accepted-and-dropped there).
        // The census, the aggregate, and every artifact write still cover the COMPLETE list; the window note
        // rides the header + envelope so a windowed render can never read as the whole list.
        int lim = limit <= 0 ? 500 : limit;
        IReadOnlyList<T> Windowed<T>(IReadOnlyList<T> rows)
        {
            // Under to_file= the rows are the FILE (the render is manifest-only) — a window note over a complete
            // artifact would misdescribe both halves, so the window doesn't apply (round-3 re-review).
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

        return hasScan && !reverseWalk
            ? ScanLane()          // incl. formids×scan: the identity set rides the scan as an intersection
            : ListLane();

        // ================================================================================================
        //  LIST lane — formids= drives; SOURCE picks the read lane.
        // ================================================================================================
        string ListLane()
        {
            if (dense) return "error: format='dense' is the scan lane's columnar form — a formids= read renders text or json.";

            var (toks, demand, echoSrc, xerr) = Artifacts.ExpandListInput(formids!, "formids");
            if (xerr is not null) return xerr;
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

            // ---- walk=: the traversal construct derives the selection (or, form='chain', IS the render). --
            if (walk is not null) return WalkLane(ids, demand, echoSrc);

            // ---- delta / tree: the §4.1 comparison forms ride their own engine batches. ------------------
            if (form is "delta" or "tree") return ListCompare(ids, demand, echoSrc);

            // ---- info_order: the merged effective INFO sequence (§6.1 F1 split). ------------------------
            if (form == "info_order")
            {
                var ioRows = svc.InfoOrderBatch(ids, demand, out var ioRefusal, out var ioEpoch);
                if (ioRefusal is not null)
                    return json ? JsonWire.RenderError(ioRefusal, ioEpoch) : "error: " + ioRefusal + (ioEpoch is not null ? $"\nepoch={ioEpoch}" : "");
                var e = new List<KeyValuePair<string, string>>
                {
                    new("formids", echoSrc ?? $"{ids.Length} inline formid(s)"),
                    new("form", form),
                };
                return InfoOrderResponse(ioRows, ioEpoch, e);
            }

            // ---- identity form: the labeling lane (absorbs housecarl_resolve). Winner frame by contract. --
            if (form == "identity")
            {
                if (srcName is not null || srcOverlay)
                    return "error: the identity form is the load-order labeling frame (type/editorid/name/WINNER per FormID) — " +
                           "it does not take a source= pole. Use form='summary' or 'fields' for a named version's view.";
                var rows = svc.ResolveRefs(ids, demand, out var epoch, out var refusal);
                if (refusal is not null)
                    return json ? JsonWire.RenderError(refusal, epoch) : "error: " + refusal + $"\nepoch={epoch}";
                Arm("winner");
                if (counts_only)
                {
                    // The census honors counts_only on EVERY list form (round-3 review: identity rendered rows anyway).
                    int okI = rows.Count(r => r.Error is null);
                    return json ? JsonWire.RenderCounts(envelope, rows.Count, okI, rows.Count - okI, epoch)
                                : $"{headerLine}\ncount={rows.Count} ok={okI} errors={rows.Count - okI}\nepoch={epoch}";
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
                    var path = ResultsStore.NextPath("housecarl_records", epoch);
                    var (s, aerr) = Artifacts.WriteResolve(rows, epoch, path, "ceiling", Echo());
                    if (aerr is not null) ResultsStore.Release(path);
                    rendered = Render(aerr is null ? SpillState.Spilled(s!, manifestOnly: false) : SpillState.WriteFailed(aerr), out _);
                }
                return rendered;
            }

            // ---- summary / fields / everything / aggregate: batch bodies off the source pole. -----------
            // summary reads ONE cheap leaf (the header facts ride the outcome), fields reads the named paths,
            // everything dumps the modeled fields (paths=null).
            IReadOnlyList<string>? readFields = form switch
            {
                "fields" => projFields,
                "summary" or "aggregate" => new[] { "EditorID" },   // cheapest leaf — headers carry the summary facts
                _ => null,                                          // everything — the full dump
            };
            IReadOnlyList<ReadOutcome> outcomes;
            LoadOrderService.PoleInfo? pole = null;
            if (srcOverlay && !string.Equals(srcSpec.OverlayState ?? "post", "pre", StringComparison.OrdinalIgnoreCase))
            {
                // The overlay POST source: every record's winner replayed through the SkyPatcher INI layer, the
                // replayed body read at the caller's own depth (absorbs skypatcher_read's post-state view).
                outcomes = svc.OverlayPostBatch(ids, readFields, depth, resolveNames, demand, out var ovRefusal, out var ovEpoch, out _);
                if (ovRefusal is not null)
                    return json ? JsonWire.RenderError(ovRefusal, ovEpoch)
                                : "error: " + ovRefusal + (ovEpoch is not null ? $"\nepoch={ovEpoch}" : "");
                Arm("skypatcher overlay (post) — the winner after the SkyPatcher INI layer replays");
                envelope.Add(new("epoch_covers_source", "false"));
                headerLine += "\n(the SkyPatcher INI layer's files are OUTSIDE the epoch fingerprint — an INI edit changes answers " +
                              "without changing the epoch; a record whose type SkyPatcher cannot patch reads as its plain winner)";
            }
            else if (srcName is null)
            {
                if (srcOverlay) Arm("skypatcher overlay (pre) = winner — the body the INI layer starts from");
                outcomes = svc.ResolveBatch(ids, readFields, false, depth, resolveNames, null, demand, out var refusal, out var refusalEpoch);
                if (refusal is not null)
                    return json ? JsonWire.RenderError(refusal, refusalEpoch)
                                : "error: " + refusal + (refusalEpoch is not null ? $"\nepoch={refusalEpoch}" : "");
                if (!srcOverlay) Arm("winner");
            }
            else
            {
                outcomes = svc.ResolveBatchFromPole(ids, srcName, srcMod, readFields, depth, resolveNames, demand,
                                                    out pole, out var refusal, out var refusalEpoch);
                if (refusal is not null)
                    return json ? JsonWire.RenderError(refusal, refusalEpoch)
                                : "error: " + refusal + (refusalEpoch is not null ? $"\nepoch={refusalEpoch}" : "");
                Arm($"{pole!.Plugin} — {pole.Where}");
                if (!pole.EpochCoversPole)
                {
                    envelope.Add(new("epoch_covers_source", "false"));
                    headerLine += "\n(the off-order file's content is OUTSIDE the epoch fingerprint — an edit to it changes answers without changing the epoch)";
                }
            }
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
                    : $"{headerLine}\ncount={outcomes.Count} ok={ok} errors={err}" + (epoch2 is not null ? $"\nepoch={epoch2}" : "");
            }

            var winOutcomes = Windowed(outcomes);   // render window; census/aggregate/artifacts stay complete
            SpillState? spill2 = null;
            if (wantFile)
            {
                var (s, aerr) = Artifacts.WriteBatch(outcomes, toFile!, "to_file", Echo());
                if (aerr is not null) return json ? JsonWire.RenderError(aerr, epoch2) : "error: " + aerr;
                spill2 = SpillState.Spilled(s!, manifestOnly: true);
            }
            string Render2(SpillState? sp, out bool trunc) => form == "summary"
                ? RenderRecordsSummary(winOutcomes, json, headerLine, envelope, max_chars, sp, out trunc)
                : json ? JsonWire.RenderBatch(winOutcomes, max_chars, sp, out trunc, envelope)
                       : headerLine + "\n" + Wire.RenderBatch(svc, winOutcomes, projFields, false, max_chars, sp, out trunc);
            var rendered2 = Render2(spill2, out var truncated2);
            if (spill2 is null && truncated2)
            {
                var path = ResultsStore.NextPath("housecarl_records", epoch2 ?? "none");
                var (s, aerr) = Artifacts.WriteBatch(outcomes, path, "ceiling", Echo());
                if (aerr is not null) ResultsStore.Release(path);
                rendered2 = Render2(aerr is null ? SpillState.Spilled(s!, manifestOnly: false) : SpillState.WriteFailed(aerr), out _);
            }
            return rendered2;
        }

        // ================================================================================================
        //  WALK lane — the §3 traversal construct: forward walks expand the winner link graph (chain form
        //  renders the paths; any other form consumes the reached set as its selection); the reverse walk's
        //  typed MGEF lane traces carriers with per-hit payload (the effect_chain absorption).
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
                // The typed MGEF lane (the effect_chain absorption, §3.3): each seed MUST resolve to a
                // MagicEffect — a non-MGEF seed fails LOUD per item, never a silent '0 carriers'.
                var results = new List<(string Seed, EffectChainResult Result)>(ids.Length);
                foreach (var raw in ids)
                {
                    FormKey fk;
                    try { fk = FormKey.Factory(raw.Trim()); }
                    catch (Exception ex) { results.Add((raw?.Trim() ?? "", EffectChainResult.Fail($"bad FormID '{raw}': {ex.Message}"))); continue; }
                    // The per-seed carrier bound is the WALK's own reach budget (walk.max_nodes, default 2000) —
                    // limit=/offset= stay the SEED window, never a second silent cut on the carrier axis (re-review).
                    results.Add((fk.ToString(), svc.ResolveEffectChain(fk, types, walkMaxNodes)));
                }
                // ONE build for the whole batch (review F3): each seed's resolve captures its own view — the
                // stamps must agree, and an @artifact seed list's epoch demand must match that build.
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
                    return json ? JsonWire.RenderError(dref, epochR) : "error: " + dref + (epochR is not null ? $"\nepoch={epochR}" : "");
                }
                Arm("winner (carriers are the load-order-effective versions)");
                envelope.Add(new("walk", "reverse, depth 1 — the typed MGEF carrier lane"));
                headerLine += "\nwalk=reverse (per seed: every SPEL/ENCH/ALCH/SCRL/INGR applying it, with the MATCHING entry's magnitude/area/duration — reported AS AUTHORED; conditions are not evaluated, so a row means 'defines it at this strength', not 'it will fire')";
                // The census separates WRITTEN rows from the true total, and names capped seeds — so the
                // artifact and its census can never disagree, and a walk.max_nodes cut is declared (re-review).
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
                        : $"{headerLine}\nseeds={results.Count} carrier_rows={carrierRows} carrier_total={carrierTotal} capped_seeds={cappedSeeds} errors={seedErrs2}" + (epochR is not null ? $"\nepoch={epochR}" : "");
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
                    var path = ResultsStore.NextPath("housecarl_records", epochR ?? "none");
                    var (sp, aerr) = Artifacts.WriteEffectChains(results, epochR, path, "ceiling", Echo());
                    if (aerr is not null) ResultsStore.Release(path);
                    revRendered = RenderRev(aerr is null ? SpillState.Spilled(sp!, manifestOnly: false) : SpillState.WriteFailed(aerr), out _);
                }
                return revRendered;
            }

            // FORWARD: one engine batch, one captured build; the chain form renders it, every other form
            // consumes the reached set (seeds ∪ reached — the render says so) through the normal lanes.
            var rows = svc.WalkForwardBatch(ids, walk!.seed_paths, walk.follow, walkDepth, walkMaxNodes,
                                            walkExclusions, demand, out var wRefusal, out var wEpoch);
            if (wRefusal is not null)
                return json ? JsonWire.RenderError(wRefusal, wEpoch) : "error: " + wRefusal + (wEpoch is not null ? $"\nepoch={wEpoch}" : "");
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
                        : $"{headerLine}\nseeds={rows.Count} reached={reached} errors={errs}" + (wEpoch is not null ? $"\nepoch={wEpoch}" : "");
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
                    var path = ResultsStore.NextPath("housecarl_records", wEpoch ?? "none");
                    var (s, aerr) = Artifacts.WriteChain(rows, wEpoch, path, "ceiling", Echo());
                    if (aerr is not null) ResultsStore.Release(path);
                    rendered = Render(aerr is null ? SpillState.Spilled(s!, manifestOnly: false) : SpillState.WriteFailed(aerr), out _);
                }
                return rendered;
            }

            // Selection consumption: seeds ∪ reached, in walk order, deduplicated — then the ordinary form
            // pipelines read it (source= decides whose version), seam-checked against the walk's build.
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
                            : $"error: the walk reached nothing readable ({seedErrs} seed error(s) — run form='chain' to see each seed's outcome)." + (wEpoch is not null ? $"\nepoch={wEpoch}" : "");
            envelope.Add(new("walk", $"forward{(walk.follow is { } f3 ? $" follow={f3}" : " (closure)")} depth={walkDepth} — selection = the {combined.Count} record(s) the walk reached (seeds included{(seedErrs > 0 ? $"; {seedErrs} seed error(s), listed via form='chain'" : "")})"));
            headerLine += $"\nwalk: selection = {combined.Count} reached record(s) (seeds included)";
            expectEpoch = wEpoch;
            formids = combined.ToArray();
            walk = null;
            return ListLane();
        }

        // ================================================================================================
        //  COMPARISON forms on the list lane — delta (subject vs reference) and tree (every provider vs
        //  reference), riding the §4.1 engine batches (one captured build per call).
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
                    return json ? JsonWire.RenderError(refusal, epoch) : "error: " + refusal + (epoch is not null ? $"\nepoch={epoch}" : "");
                return DeltaResponse(rows, sArm, rArm, covers, epoch, Echo());
            }
            else   // tree
            {
                if (srcSpec.Kind != LoadOrderService.PoleKind.Winner)
                    return "error: the tree form has no subject — every provider of each record is on the bench, and the pole each is diffed against is versus=. Drop source= (or use form='delta' for a subject-vs-reference comparison).";
                var rows = svc.TreeBatch(ids, versusSpec!, projFields, demand,
                                         out var rArm, out var covers, out var refusal, out var epoch);
                if (refusal is not null)
                    return json ? JsonWire.RenderError(refusal, epoch) : "error: " + refusal + (epoch is not null ? $"\nepoch={epoch}" : "");
                return TreeResponse(rows, rArm, covers, epoch, Echo());
            }
        }

        // The shared delta response pipeline (envelope, counts_only, window, to_file/ceiling spill, both
        // renders) — one path for the list AND scan lanes, so their behavior cannot drift.
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
                    : $"{headerLine}\ncount={rows.Count} differing={differing} identical={identical} errors={errs}" + (epoch is not null ? $"\nepoch={epoch}" : "");
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
                var path = ResultsStore.NextPath("housecarl_records", epoch ?? "none");
                var (s, aerr) = Artifacts.WriteDelta(rows, epoch, path, "ceiling", echo);
                if (aerr is not null) ResultsStore.Release(path);
                rendered = Render(aerr is null ? SpillState.Spilled(s!, manifestOnly: false) : SpillState.WriteFailed(aerr), out _);
            }
            return rendered;
        }

        // The shared tree response pipeline — the tree form has no subject (every provider is on the bench);
        // the envelope's source slot carries the reference statement instead.
        string TreeResponse(IReadOnlyList<LoadOrderService.TreeRow> rows, string? rArm, bool covers,
                            string? epoch, List<KeyValuePair<string, string>> echo)
        {
            // The tree has no subject: its REFERENCE rides the `versus` envelope key (delta's own convention),
            // and `source` keeps the SELECTION arm — stated by the lane (scan/off-order), or the stack
            // statement here on the list lane (first-wins; re-review R3-1: the reference in the source slot
            // suppressed the selection arm that made epoch_covers_source intelligible).
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
                    : $"{headerLine}\ncount={rows.Count} contested={contested} errors={errs}" + (epoch is not null ? $"\nepoch={epoch}" : "");
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
                ? JsonWire.RenderTree(winRows, max_chars, epoch, envelope, treeCounts, sp, out trunc)
                : RenderRecordsTree(winRows, rows.Count, contested, errs, projFields is { Length: > 0 }, headerLine, epoch, max_chars, sp, out trunc);
            var rendered = Render(spill, out var truncated);
            if (spill is null && truncated)
            {
                var path = ResultsStore.NextPath("housecarl_records", epoch ?? "none");
                var (s, aerr) = Artifacts.WriteTree(rows, epoch, path, "ceiling", echo);
                if (aerr is not null) ResultsStore.Release(path);
                rendered = Render(aerr is null ? SpillState.Spilled(s!, manifestOnly: false) : SpillState.WriteFailed(aerr), out _);
            }
            return rendered;
        }

        // The shared info_order response pipeline (envelope, counts_only, window, to_file/ceiling spill, both
        // renders) — one path for the list AND scan lanes.
        string InfoOrderResponse(IReadOnlyList<LoadOrderService.InfoOrderRow> rows, string? epoch,
                                 List<KeyValuePair<string, string>> echo)
        {
            Arm("the merge of every touching plugin (the effective order the game walks)");
            int contested = rows.Count(x => x.Error is null && x.Order is { Contested: true });
            int errs = rows.Count(x => x.Error is not null);
            if (counts_only)
                return json
                    ? JsonWire.RenderNamedCounts(envelope, new[] { KvI("count", rows.Count), KvI("contested", contested), KvI("errors", errs) }, epoch)
                    : $"{headerLine}\ncount={rows.Count} contested={contested} errors={errs}" + (epoch is not null ? $"\nepoch={epoch}" : "");
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
                var path = ResultsStore.NextPath("housecarl_records", epoch ?? "none");
                var (s, aerr) = Artifacts.WriteInfoOrder(rows, epoch, path, "ceiling", echo);
                if (aerr is not null) ResultsStore.Release(path);
                rendered = Render(aerr is null ? SpillState.Spilled(s!, manifestOnly: false) : SpillState.WriteFailed(aerr), out _);
            }
            return rendered;
        }

        // A pole that reads content outside the epoch fingerprint (an off-order file; the overlay's INIs) is
        // DECLARED, envelope + header alike (the PR #305 coverage rule) — one helper so no form forgets.
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
                return "error: the identity form labels a formids= list; a scan's summary rows already carry each match's identity — use form='summary' (the default).";

            if (srcOverlay || versusSpec?.Kind == LoadOrderService.PoleKind.Overlay)
                return "error: an overlay pole on a SCAN would replay the SkyPatcher INI layer over every match — a per-record replay at scan scale " +
                       "(a scan comparison compares EVERY match, so it is not a bound). Name the records via formids= — the list lane reads and " +
                       "compares their post-state bodies — or read the whole layer via housecarl_skypatcher_layer.";
            bool hasBodyFilter = where is { Length: > 0 } || references is { Length: > 0 };
            bool hasTypes = types is { Length: > 0 };
            bool hasScope = plugins?.names is { Length: > 0 };
            bool scopePlusPole = false;
            // The derived-selection forms (comparisons, info_order, a walk's seeds) consume EVERY match;
            // known up front, used by the scan cap below.
            bool derivedSelection = comparisonForm || form == "info_order" || walk is not null;
            // The arm rule (F1, corrected in the round-4 fold): the scan pre-arm is skipped ONLY for forms
            // whose pipeline states its own SOURCE arm (delta = the subject; info_order = the merge; a walk =
            // the re-entered reading arm). The TREE has no subject — its reference lives in the `versus`
            // envelope key — so the scan's own selection arm stays, which is exactly what discloses an
            // off-order or scoped selection universe (re-review R3-1/R3-2).
            bool pipelineArms = form == "delta" || form == "info_order" || walk is not null;
            if (hasBodyFilter && !hasTypes && !hasScope && !hasFormids)
                return "error: where=/references= is a body scan and must be combined with types=, plugins=, or a formids= set to bound the work " +
                       "(conflicts_only= alone is not enough — an unbounded body scan over the whole order is refused; the " +
                       "reverse-reference index that would lift this is a known future capability).";
            if (plugins is { defined_in: true } && !hasScope)
                return "error: plugins.defined_in=true keeps records DEFINED in the scoped plugins, so plugins.names must name that scope.";

            // formids×scan (W2 composition): the identity set intersects the scan — expanded here (@file /
            // artifact demand honored inside the scan's own capture), parsed once, handed to the engine as the
            // set filter (alone, it IS the scan universe — the set is the bound).
            HousecarlCore.ArtifactDemand? fidDemand = null; string? fidEcho = null;
            IReadOnlyList<FormKey>? formidSet = null;
            if (hasFormids)
            {
                var (ftoks, fdemand, fecho, fxerr) = Artifacts.ExpandListInput(formids!, "formids");
                if (fxerr is not null) return fxerr;
                fidDemand = fdemand; fidEcho = fecho;
                var fkList = new List<FormKey>();
                foreach (var t in ftoks!)
                {
                    if (string.IsNullOrWhiteSpace(t)) continue;
                    try { fkList.Add(FormKey.Factory(t.Trim())); }
                    catch (Exception ex) { return $"error: bad formids entry '{t}': {ex.Message}. Expected 'XXXXXX:Plugin.esp'."; }
                }
                if (fkList.Count == 0) return "error: formids= expanded to an empty list — nothing to intersect the scan with.";
                formidSet = fkList;
            }

            // ---- OFF-ORDER source universe: the file's own records (absorbs read_plugin_file's enumeration).
            string? probeEpoch = null;
            if (srcName is not null)
            {
                // Resolve which arm ONCE via a cheap containment probe. The ACTIVE-arm scan below re-captures;
                // its outcome stamp is compared against the probe's (a mid-call order change refuses loud, never
                // an arm statement about a different world). The off-order lane reads the file directly and
                // consults no further build.
                var probe = svc.ProbeSourceArm(srcName, srcMod, out var probeErr);
                if (probeErr is not null) return "error: " + probeErr;
                srcName = probe!.Plugin;   // a PATH pole resolves back to its plugin name — every consumer below uses the resolved name (round-3 F4)
                probeEpoch = probe.Epoch;
                if (!probe.InOrder)
                    return OffOrderScan(probe);
                if (hasScope)
                {
                    // Scope-vs-pole streams (the W2 composition): the plugins= scope decides WHICH records are
                    // considered; the named source= decides whose VERSION the body forms read. Identity-fact
                    // forms have nothing for the pole to change — accepting-and-ignoring it is the sin.
                    if (form is "summary" or "aggregate")
                        return $"error: a plugins= scope with a named source= reads the POLE's version of each scoped match — and the '{form}' form's rows are identity facts the pole doesn't change. Drop source=, or use form='fields'/'everything' (the pole's bodies) or 'delta'/'tree' (comparisons).";
                    if (winnerFields)
                        return "error: fields_source='winner' and a named source= under a plugins= scope are TWO display poles on one call — the pole's version is what this composition reads. Drop fields_source= (or drop source= and keep fields_source='winner').";
                    scopePlusPole = true;
                    // The scope statement is the truthful arm only for the forms that READ the pole's bodies;
                    // delta's pipeline states its subject itself (R3-2). A scoped TREE reads every provider,
                    // so the scope-selection arm (without the pole clause) is stated below like any scan.
                    if (form == "tree") Arm($"{probe.Plugin} — scope-selected ({string.Join(", ", plugins!.names!)}); the tree reads every provider");
                    else if (!pipelineArms) Arm($"{probe.Plugin} — active in the load order (the plugins= scope selects; this pole's version is read)");
                }
                else if (!pipelineArms)
                    // ACTIVE arm: the pole's records ARE the scan universe — the plugins= stream with the arm stated.
                    Arm($"{probe.Plugin} — active in the load order");
            }
            else if (!pipelineArms) Arm("winner");   // delta/info_order/walk pipelines state their own arm (F1)

            // references= @file expansion + FormKey parse (mirrors cross_plugin_query).
            HousecarlCore.ArtifactDemand? refDemand = null; string? refEcho = null;
            var refs = references;
            if (refs is { Length: > 0 })
            {
                var (toks, demand, echoSrc, xerr) = Artifacts.ExpandListInput(refs, "references");
                if (xerr is not null) return xerr;
                refs = toks!; refDemand = demand; refEcho = echoSrc;
            }
            IReadOnlyList<FormKey>? refFks = null;
            if (refs is { Length: > 0 })
            {
                var list = new List<FormKey>();
                foreach (var r in refs)
                {
                    if (string.IsNullOrWhiteSpace(r)) continue;
                    try { list.Add(FormKey.Factory(r.Trim())); }
                    catch (Exception ex) { return $"error: bad references FormID '{r}': {ex.Message}. Expected 'XXXXXX:Plugin.esp'."; }
                }
                if (list.Count > 0) refFks = list.Distinct().ToList();
            }

            var scanPlugins = scopePlusPole ? plugins!.names : (srcName is not null ? new[] { srcName } : plugins?.names);
            bool definedIn = plugins?.defined_in ?? false;
            // Under walk= the scan only SELECTS the seeds — the aggregate (like every reading form) applies to
            // the REACHED set via the walk lane's re-entry. Grouping the scan itself would return an aggregate
            // of the seeds labeled as the walk's answer, with walk= silently dropped (review F2).
            var groupBy = form == "aggregate" && walk is null ? project!.group_by!.Trim().ToLowerInvariant() : null;
            // The derived-selection forms consume EVERY match — the window applies to their rows, and the
            // counts / artifact must cover the full selection — so the scan itself is uncapped for them
            // (a DECLARED cost, carried in the tool description: the scan terms are the bound).
            int effLimit = wantFile || derivedSelection ? int.MaxValue : counts_only ? 0 : (limit <= 0 ? 500 : limit);

            var demandsList = new List<HousecarlCore.ArtifactDemand>();
            if (refDemand is not null) demandsList.Add(refDemand);
            if (fidDemand is not null) demandsList.Add(fidDemand);
            var outcome = svc.CrossQuery(types, refFks, null, conflicts_only, scanPlugins, where,
                                         effLimit, definedIn, groupBy, offset, where_source,
                                         demandsList.Count > 0 ? demandsList : null, formidSet);
            // The probe→scan seam IS epoch-compared (round-3 F5): the arm statement must describe the same
            // build the rows were scanned from.
            if (probeEpoch is not null && outcome.Error is null && outcome.Epoch is not null && outcome.Epoch != probeEpoch)
            {
                var tear = $"the load order changed between resolving the source arm (epoch={probeEpoch}) and the scan " +
                           $"(epoch={outcome.Epoch}) — the arm statement would describe a different world. Retry the call.";
                // Format-kept like every other refusal on these paths (round-3 re-review minor).
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

            // ---- walk= on a scan: the scan's matches are the SEEDS; the walk lane takes it from there
            //      (chain render, or the reached set through the form pipelines), seam-checked throughout.
            if (walk is not null && outcome.Error is null && outcome.Groups is null)
            {
                var seedKeys = outcome.Keys.Select(k => k.ToString()).ToArray();
                envelope.Add(new("total", outcome.Total.ToString()));
                headerLine += $"\n{outcome.Total} match(es) selected by the scan as walk seeds";
                expectEpoch = outcome.Epoch;
                return WalkLane(seedKeys, null, null);
            }

            // ---- delta / tree on a scan: the scan SELECTS the records, the §4.1 engine batches compare
            //      them. Two captures meet here, so the seam is epoch-compared — the halves can never
            //      silently mix builds (the form=everything discipline).
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
                        return json ? JsonWire.RenderError(refusal, depoch) : "error: " + refusal + (depoch is not null ? $"\nepoch={depoch}" : "");
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
                        return json ? JsonWire.RenderError(refusal, tepoch) : "error: " + refusal + (tepoch is not null ? $"\nepoch={tepoch}" : "");
                    if (outcome.Epoch is not null && tepoch is not null && tepoch != outcome.Epoch)
                    {
                        var tear = $"the load order changed between the scan (epoch={outcome.Epoch}) and the comparison " +
                                   $"(epoch={tepoch}) — the two halves would mix builds. Retry the call.";
                        return json ? JsonWire.RenderError(tear, tepoch) : "error: " + tear;
                    }
                    return TreeResponse(rows, rArm, covers, tepoch, Echo());
                }
            }

            // ---- info_order on a scan: the scan SELECTS the topics (types=["DIAL"] + where= is the quest
            //      fan-out spelling), the merge engine renders — seam epoch-compared like every two-capture form.
            if (form == "info_order" && outcome.Error is null && outcome.Groups is null)
            {
                var ioKeys = outcome.Keys.Select(k => k.ToString()).ToList();
                envelope.Add(new("total", outcome.Total.ToString()));
                headerLine += $"\n{outcome.Total} match(es) selected by the scan";
                var ioRows = svc.InfoOrderBatch(ioKeys, null, out var ioRefusal, out var ioEpoch);
                if (ioRefusal is not null)
                    return json ? JsonWire.RenderError(ioRefusal, ioEpoch) : "error: " + ioRefusal + (ioEpoch is not null ? $"\nepoch={ioEpoch}" : "");
                if (outcome.Epoch is not null && ioEpoch is not null && ioEpoch != outcome.Epoch)
                {
                    var tear = $"the load order changed between the scan (epoch={outcome.Epoch}) and the merge " +
                               $"(epoch={ioEpoch}) — the two halves would mix builds. Retry the call.";
                    return json ? JsonWire.RenderError(tear, ioEpoch) : "error: " + tear;
                }
                return InfoOrderResponse(ioRows, ioEpoch, Echo());
            }

            // ---- form=everything on a scan: selection here, bodies via the batch lane (window-bounded).
            // counts_only skips the body lane entirely — the census is the cross-query render below (round 3).
            if ((form == "everything" || (form == "fields" && scopePlusPole)) && !counts_only && outcome.Error is null && outcome.Groups is null)
            {
                var keys = outcome.Keys.Select(k => k.ToString()).ToList();
                IReadOnlyList<ReadOutcome> bodies;
                if (srcName is not null)
                {
                    bodies = svc.ResolveBatchFromPole(keys, srcName, srcMod, form == "fields" ? projFields : null, depth, resolveNames, null,
                                                      out _, out var bref, out var brefEpoch);
                    // A refusal is judged on the NAMED cause, never on row count (review: a zero-match scan is an
                    // honest EMPTY result, and the pole lane's real refusals were being replaced by a generic one).
                    if (bref is not null)
                        return json ? JsonWire.RenderError(bref, brefEpoch)
                                    : "error: " + bref + (brefEpoch is not null ? $"\nepoch={brefEpoch}" : "");
                }
                else
                {
                    // The scan's per-match SOURCE decides whose body `everything` dumps — the SAME rule the fields
                    // form renders by (review: this branch read the WINNER under a plugins= scope while the fields
                    // form read the scoped body — same SELECT, different pole, nothing said so). Keys group by
                    // their matched source and each group reads off its own plugin; fields_source="winner"
                    // retargets display to the winner exactly as on the fields form.
                    var srcs = outcome.Sources;
                    if (winnerFields || srcs is null || srcs.Take(keys.Count).All(s => s is null))
                        bodies = svc.ResolveBatch(keys, null, false, depth, resolveNames);
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
                            var res = svc.ResolveBatch(winnerIdx.Select(i => keys[i]).ToList(), null, false, depth, resolveNames);
                            for (int i = 0; i < winnerIdx.Count; i++) byIndex[winnerIdx[i]] = res[i];
                        }
                        foreach (var kv in bySource)
                        {
                            var res = svc.ResolveBatch(kv.Value.Select(i => keys[i]).ToList(), null, false, depth, resolveNames, kv.Key);
                            for (int i = 0; i < kv.Value.Count; i++) byIndex[kv.Value[i]] = res[i];
                        }
                        bodies = byIndex;
                    }
                }
                // The §4.2 untouched contract, bulk shape: rows the pole does not touch come back as per-item
                // refusals naming the touchers; the ACCOUNTING carries the explicit count so quiet omission is
                // impossible (the per-item wording below is ResolveBatchFromPole's own).
                if (scopePlusPole)
                {
                    int notTouched = bodies.Count(o => o.Error is not null && (o.Error.Contains("does not touch") || o.Error.Contains("does not define or override")));
                    if (notTouched > 0)
                    {
                        envelope.Add(new("not_touched", notTouched.ToString()));
                        headerLine += $"\nnot_touched={notTouched} — scoped match(es) the source pole has no version of (each row names its actual touchers)";
                    }
                }
                // The selection and every body read must agree on ONE build (grouped reads capture per batch) —
                // any divergence refuses loud rather than mixing builds. An empty selection has no body epochs
                // and passes: it renders as an honest 0-row batch.
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
                string RenderEv(SpillState? sp, out bool trunc) => json
                    ? JsonWire.RenderBatch(bodies, max_chars, sp, out trunc, envelope)
                    : headerLine + "\n" + Wire.RenderBatch(svc, bodies, null, false, max_chars, sp, out trunc);
                SpillState? evSpill = null;
                if (wantFile)
                {
                    var (s, aerr) = Artifacts.WriteBatch(bodies, toFile!, "to_file", Echo());
                    if (aerr is not null) return json ? JsonWire.RenderError(aerr, bodyEpoch) : "error: " + aerr;
                    evSpill = SpillState.Spilled(s!, manifestOnly: true);
                }
                var evRendered = RenderEv(evSpill, out var evTrunc);
                if (evSpill is null && evTrunc)
                {
                    var path = ResultsStore.NextPath("housecarl_records", bodyEpoch ?? "none");
                    var (s, aerr) = Artifacts.WriteBatch(bodies, path, "ceiling", Echo());
                    if (aerr is not null) ResultsStore.Release(path);
                    evRendered = RenderEv(aerr is null ? SpillState.Spilled(s!, manifestOnly: false) : SpillState.WriteFailed(aerr), out _);
                }
                return evRendered;
            }

            // ---- summary / fields / aggregate: the cross-query renders, envelope-stamped. ----------------
            SpillState? spill = null;
            if (wantFile && outcome.Error is null)
            {
                var (s, aerr) = Artifacts.WriteCrossQuery(svc, outcome, projFields, resolveNames, winnerFields, depth, toFile!, "to_file", Echo());
                if (aerr is not null)
                    return fmt is Wire.QueryFormat.Text ? "error: " + aerr : JsonWire.RenderError(aerr, outcome.Epoch);
                spill = SpillState.Spilled(s!, manifestOnly: true);
            }
            string Render(SpillState? sp, out bool trunc) => fmt switch
            {
                Wire.QueryFormat.Dense when groupBy is null => JsonWire.RenderCrossQueryDense(svc, outcome, projFields, max_chars, resolveNames, winnerFields, sp, out trunc, envelope),
                Wire.QueryFormat.Dense or Wire.QueryFormat.Json => JsonWire.RenderCrossQuery(svc, outcome, projFields, max_chars, resolveNames, winnerFields, depth, sp, out trunc, envelope),
                _ => headerLine + "\n" + Wire.RenderCrossQuery(svc, outcome, projFields, false, max_chars, resolveNames, winnerFields, depth, sp, out trunc),
            };
            var rendered = Render(spill, out var truncated);
            if (spill is null && truncated && outcome.Error is null)
            {
                var path = ResultsStore.NextPath("housecarl_records", outcome.Epoch ?? "none");
                var (s, aerr) = Artifacts.WriteCrossQuery(svc, outcome, projFields, resolveNames, winnerFields, depth, path, "ceiling", Echo());
                if (aerr is not null) ResultsStore.Release(path);
                rendered = Render(aerr is null ? SpillState.Spilled(s!, manifestOnly: false) : SpillState.WriteFailed(aerr), out _);
            }
            return rendered;
        }

        // ================================================================================================
        //  OFF-ORDER scan: the file's own records are the universe (the read_plugin_file enumeration).
        // ================================================================================================
        string OffOrderScan(LoadOrderService.PoleInfo pole)
        {
            // The file arm is the SELECTION statement — stated for every form except delta, whose pipeline
            // names the same file as its subject (R3-1: the tree needs this arm; its reference rides `versus`).
            if (form != "delta") Arm($"{pole.Plugin} — {pole.Where}");
            envelope.Add(new("epoch_covers_source", "false"));
            headerLine += "\n(the off-order file's content is OUTSIDE the epoch fingerprint — an edit to it changes answers without changing the epoch)";
            if (conflicts_only)
                return "error: conflicts_only= has no meaning on an out-of-load-order file — it is not in the conflict frame. Drop it, or read the winner (source=\"winner\").";
            if (form == "info_order")
                return "error: the info_order form merges the ACTIVE order's touching plugins — an out-of-load-order file is not in that frame. Read the winner's merge (drop source=), or enumerate the file's DIAL records with form='summary'.";
            if (walk is not null)
                return "error: the walk expands the ACTIVE order's winner link graph — an out-of-load-order file's records are not in that graph. Enumerate the file with form='summary', then walk specific records via formids= (dropping source=).";
            if (dense) return "error: format='dense' is the in-order scan's columnar form — an off-order file scan renders text or json.";
            if (versusSpec?.Kind == LoadOrderService.PoleKind.Overlay)
                return "error: an overlay pole on a SCAN would replay the SkyPatcher INI layer over every match — a per-record replay at scan scale " +
                       "(a scan comparison compares EVERY match, so it is not a bound). Name the records via formids= — the list lane reads and " +
                       "compares their post-state bodies — or read the whole layer via housecarl_skypatcher_layer.";
            if (where_source is not null)
            {
                // Full-vocabulary validation, mirroring the in-order engine (review F9): an unknown spelling must
                // refuse by name, never be accepted-and-ignored.
                var ws = where_source.Trim().ToLowerInvariant();
                if (ws == "winner")
                    return "error: where_source=winner matches on the live load-order winner — but this scan streams an out-of-load-order FILE's bodies, many of which have no winner. Match the winner by scanning the winner (drop source=), or drop where_source=.";
                if (ws is not ("scoped" or "scanned"))
                    return $"error: where_source='{where_source}' is not a known source — over an out-of-load-order file the match reads the FILE's own bodies ('scoped', the default); drop where_source=, or use 'winner' on an in-order scan.";
            }

            // The completed off-order lane (W2 PR 2): the same filter grammar as the in-order scan, run by the
            // engine over the file's own records; provenance terms bind to the ACTIVE view (declared).
            HousecarlCore.ArtifactDemand? refDemand = null; string? refEcho = null;
            var refs = references;
            if (refs is { Length: > 0 })
            {
                var (toks, demand, echoSrc2, xerr) = Artifacts.ExpandListInput(refs, "references");
                if (xerr is not null) return xerr;
                refs = toks!; refDemand = demand; refEcho = echoSrc2;
            }
            IReadOnlyList<FormKey>? refFks = null;
            if (refs is { Length: > 0 })
            {
                var list = new List<FormKey>();
                foreach (var r in refs)
                {
                    if (string.IsNullOrWhiteSpace(r)) continue;
                    try { list.Add(FormKey.Factory(r.Trim())); }
                    catch (Exception ex) { return $"error: bad references FormID '{r}': {ex.Message}. Expected 'XXXXXX:Plugin.esp'."; }
                }
                if (list.Count > 0) refFks = list.Distinct().ToList();
            }
            HousecarlCore.ArtifactDemand? fidDemand = null; string? fidEcho = null;
            IReadOnlyList<FormKey>? formidSet = null;
            if (formids is { Length: > 0 })
            {
                var (ftoks, fdemand, fecho, fxerr) = Artifacts.ExpandListInput(formids!, "formids");
                if (fxerr is not null) return fxerr;
                fidDemand = fdemand; fidEcho = fecho;
                var fkList = new List<FormKey>();
                foreach (var t in ftoks!)
                {
                    if (string.IsNullOrWhiteSpace(t)) continue;
                    try { fkList.Add(FormKey.Factory(t.Trim())); }
                    catch (Exception ex) { return $"error: bad formids entry '{t}': {ex.Message}. Expected 'XXXXXX:Plugin.esp'."; }
                }
                if (fkList.Count == 0) return "error: formids= expanded to an empty list — nothing to intersect the scan with.";
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
                                            formidSet, offDemands.Count > 0 ? offDemands : null);

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
                        return json ? JsonWire.RenderError(refusal, depoch) : "error: " + refusal + (depoch is not null ? $"\nepoch={depoch}" : "");
                    // The file selection's build and the comparison's build must agree (review F4) — the
                    // selection filtered via the ACTIVE view (scope/provenance terms), same as the in-order seam.
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
                        return json ? JsonWire.RenderError(refusal, tepoch) : "error: " + refusal + (tepoch is not null ? $"\nepoch={tepoch}" : "");
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
            if (form is ("fields" or "everything") && !counts_only && outcome.Error is null && outcome.Groups is null)
            {
                var keys = outcome.Keys.Select(k => k.ToString()).ToList();
                var bodies = svc.ResolveBatchFromPole(keys, pole.Plugin, srcMod, form == "fields" ? projFields : null,
                                                      depth, resolveNames, null, out _, out var bref, out var brefEpoch);
                if (bref is not null)
                    return json ? JsonWire.RenderError(bref, brefEpoch)
                                : "error: " + bref + (brefEpoch is not null ? $"\nepoch={brefEpoch}" : "");
                // The selection's build and the body reads' build must agree (review F4) — same rule as the
                // in-order body seam; the bodies re-open the file, but the SELECTION was made on the view.
                var offBodyEpochs = bodies.Where(o => o.Epoch is not null).Select(o => o.Epoch!).Distinct().ToList();
                if (outcome.Epoch is not null && offBodyEpochs.Any(e => e != outcome.Epoch))
                {
                    var tear = $"the load order changed between the file scan (epoch={outcome.Epoch}) and the body read " +
                               $"(epoch={string.Join(", ", offBodyEpochs.Where(e => e != outcome.Epoch))}) — the two halves would mix builds. Retry the call.";
                    return json ? JsonWire.RenderError(tear, outcome.Epoch) : "error: " + tear;
                }
                envelope.Add(new("total", outcome.Total.ToString()));
                headerLine += $"\n{outcome.Total} match(es); bodies for the {keys.Count}-row window below";
                string RenderOff(SpillState? sp, out bool trunc) => json
                    ? JsonWire.RenderBatch(bodies, max_chars, sp, out trunc, envelope)
                    : headerLine + "\n" + Wire.RenderBatch(svc, bodies, form == "fields" ? projFields : null, false, max_chars, sp, out trunc);
                SpillState? offSpill = null;
                var offEpoch = bodies.FirstOrDefault(o => o.Epoch is not null)?.Epoch ?? outcome.Epoch;
                if (wantFile)
                {
                    var (sp, aerr) = Artifacts.WriteBatch(bodies, toFile!, "to_file", Echo());
                    if (aerr is not null) return json ? JsonWire.RenderError(aerr, offEpoch) : "error: " + aerr;
                    offSpill = SpillState.Spilled(sp!, manifestOnly: true);
                }
                var offRendered = RenderOff(offSpill, out var offTrunc);
                if (offSpill is null && offTrunc)
                {
                    var path = ResultsStore.NextPath("housecarl_records", offEpoch ?? "none");
                    var (sp, aerr) = Artifacts.WriteBatch(bodies, path, "ceiling", Echo());
                    if (aerr is not null) ResultsStore.Release(path);
                    offRendered = RenderOff(aerr is null ? SpillState.Spilled(sp!, manifestOnly: false) : SpillState.WriteFailed(aerr), out _);
                }
                return offRendered;
            }

            // summary / aggregate: the shared cross-query renders (Prefilled rows carry the file's identities;
            // winner context rides where a record also lives in the order).
            SpillState? spill = null;
            if (wantFile && outcome.Error is null)
            {
                var (sp, aerr) = Artifacts.WriteCrossQuery(svc, outcome, null, false, false, 1, toFile!, "to_file", Echo());
                if (aerr is not null)
                    return fmt is Wire.QueryFormat.Text ? "error: " + aerr : JsonWire.RenderError(aerr, outcome.Epoch);
                spill = SpillState.Spilled(sp!, manifestOnly: true);
            }
            string Render(SpillState? sp, out bool trunc) => fmt switch
            {
                Wire.QueryFormat.Dense or Wire.QueryFormat.Json => JsonWire.RenderCrossQuery(svc, outcome, null, max_chars, false, false, 1, sp, out trunc, envelope),
                _ => headerLine + "\n" + Wire.RenderCrossQuery(svc, outcome, null, false, max_chars, false, false, 1, sp, out trunc),
            };
            var rendered = Render(spill, out var truncated);
            if (spill is null && truncated && outcome.Error is null)
            {
                var path = ResultsStore.NextPath("housecarl_records", outcome.Epoch ?? "none");
                var (sp, aerr) = Artifacts.WriteCrossQuery(svc, outcome, null, false, false, 1, path, "ceiling", Echo());
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

    /// <summary>Parse a §4.2 pole expression from its wire spelling. Null element ⇒ null spec (the caller applies
    /// its default). Returns the named refusal, or null on success. <paramref name="subjectRole"/> marks source=
    /// (the SUBJECT): previous_provider is measured FROM the subject and so cannot BE it (§4.3).</summary>
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

    /// <summary>The delta form's text render: header counts, then per record the two pole lines, the §4.3
    /// stack-above FACT (neutral — never advice), and diff_record's own delta-line grammar with its truncation
    /// honesty (a truncated deep read is never rendered as 'identical').</summary>
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
        if (epoch is not null) sb.Append("  epoch=").Append(epoch);
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
                    sb.Append("  no differing fields in what was read, but the deep read was TRUNCATED at the cap — NOT a clean 'identical' (Q3). Narrow with project.fields to compare in full.\n");
                else if (d.AgreedCount > 0)
                    sb.Append("  identical across the fields read (").Append(d.AgreedCount).Append(" value leaf/leaves agree).\n");
                else
                    sb.Append("  identical across the fields read (no differing fields).\n");
            }
            else
            {
                sb.Append("  ").Append(d.Deltas.Count).Append(d.Deltas.Count == 1 ? " difference" : " differences")
                  .Append(" — each line: ").Append(s.Plugin).Append("'s value (reference = ").Append(r.Plugin).Append("):\n");
                foreach (var delta in d.Deltas)
                {
                    if (sb.Length >= cap)
                    {
                        truncated = true;
                        sb.Append("    ... [delta lines cut at max_chars=").Append(cap).Append(" — raise max_chars or narrow with project.fields]\n");
                        break;
                    }
                    sb.Append("    - ").Append(delta).Append('\n');
                }
                if (!d.Complete)
                    sb.Append("  note: the deep read was TRUNCATED — list-content and one-sided-presence deltas are SUPPRESSED; narrow with project.fields to compare those in full.\n");
            }
            rendered++;
        }
        if (spill is not null) Artifacts.AppendSpillStateText(sb, spill);
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>The tree form's text render: per record the touching list (load order, winner LAST) and each
    /// provider's delta against the reference — the conflict_tree view as a PROJECT form, same wording rules
    /// (identical-vs-truncated honesty; list contents compared by content, reorders flagged).</summary>
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
        if (epoch is not null) sb.Append("  epoch=").Append(epoch);
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
            sb.Append("  ").Append(row.Type ?? "?").Append("  ").Append(row.EditorId ?? "<no editorid>").Append('\n');
            sb.Append("  ").Append(row.Touchers.Count).Append(" plugin(s) touch this record (load order, winner last):\n");
            for (int i = 0; i < row.Touchers.Count; i++)
                sb.Append("    ").Append(i + 1).Append(". ").Append(row.Touchers[i])
                  .Append(i == row.Touchers.Count - 1 ? "  (winner)" : "").Append('\n');
            if (row.Nodes.Count <= 1) { rendered++; continue; }   // a sole provider has nothing to diff against
            sb.Append("  diff (field deltas vs ").Append(row.ReferencePlugin)
              .Append("; identical fields omitted; list contents compared by content, element reorders flagged):\n");
            foreach (var n in row.Nodes)
            {
                if (n.IsReference) continue;
                if (sb.Length >= cap)
                {
                    truncated = true;
                    sb.Append("    ... [nodes cut at max_chars=").Append(cap).Append(" — raise max_chars or narrow with project.fields]\n");
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

    /// <summary>The chain form's text render: per seed the reached nodes in BFS order with provenance (what
    /// pulled each node in), recorded cycles, the cap-truncation note (read posture — what is listed IS proved),
    /// and the NPC_ TemplateFlags inheritance report where the walk was a Template chain.</summary>
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
        if (epoch is not null) sb.Append("  epoch=").Append(epoch);
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

    /// <summary>The reverse MGEF lane's text render: header census over the COMPLETE seed list, each windowed
    /// seed's carriers via the shared effect-chain render, the standard explicit cut + in-band spill marker.</summary>
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
        if (epoch is not null) sb.Append("  epoch=").Append(epoch);
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
            sb.Append(Wire.RenderEffectChain(result, cap)).Append('\n');
            rendered++;
        }
        if (spill is not null) Artifacts.AppendSpillStateText(sb, spill);
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>The info_order form's text render: per topic its identity, then the SAME merged-order body
    /// housecarl_validate_dialogue renders (shared via <see cref="DialogueWire.AppendInfoOrderView"/> — the §6.1
    /// split carried the MOVED annotations and the MovesComputed×Complete / BaselineTrusted honesty gates across
    /// intact, one render, no drift).</summary>
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
        if (epoch is not null) sb.Append("  epoch=").Append(epoch);
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

    /// <summary>The list-lane summary render: one identity+winner line per outcome (or its per-item error) — the
    /// batch shape of the scan lane's summary rows. Budget-bounded with the standard explicit cut; the spill marker
    /// rides in-band in both formats via the shared emitters.</summary>
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
        if (epoch is not null) sb.Append("  epoch=").Append(epoch);
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
                sb.Append(o.FormKey).Append("  ").Append(o.Record!.Type)
                  .Append("  ").Append(o.Record.EditorId ?? "<no editorid>")
                  .Append("  source=").Append(o.SourcePlugin ?? "?");
                if (o.WinnerPlugin is not null) sb.Append("  winner=").Append(o.WinnerPlugin).Append("  depth=").Append(o.OverrideDepth);
                sb.Append('\n');
            }
            rendered++;
        }
        if (spill is not null) Artifacts.AppendSpillStateText(sb, spill);
        return sb.ToString();
    }

    /// <summary>The list-lane aggregate render: count the resolved rows by winner | type | defined_in — the batch
    /// twin of the scan lane's count table. Per-item errors are counted and named as their own bucket (never
    /// silently dropped from a census — Q3). Carries the SAME response envelope as every other form (review fold:
    /// this render dropped the resolved source arm + the epoch-coverage qualifier — the one statement the tool
    /// description promises unconditionally).</summary>
    static string RenderListAggregate(IReadOnlyList<ReadOutcome> outcomes, string groupBy, bool json, bool dense, string? epoch,
                                      string headerLine, List<KeyValuePair<string, string>> envelope)
    {
        var gb = groupBy.Trim().ToLowerInvariant();
        if (gb is not ("winner" or "type" or "defined_in"))
            return $"error: project.group_by='{groupBy}' is not a count key — use 'winner', 'type', or 'defined_in'.";
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
        if (epoch is not null) sb.Append("  epoch=").Append(epoch);
        sb.Append('\n');
        foreach (var (key, count) in rows.Select(r => (r.Key, r.Value)))
            sb.Append("  ").Append(count.ToString().PadLeft(6)).Append("  ").Append(key).Append('\n');
        return sb.ToString();
    }
}
