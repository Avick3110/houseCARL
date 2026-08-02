using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;
using Mutagen.Bethesda.Plugins;

namespace HousecarlMcp;

/// <summary>
/// housecarl_records — the 2.0 S1 read surface (tool-surface-2.0 W2 PR 1; SPEC §2.2/§4.2/§6.1).
///
/// ONE read tool: SELECT (which records) × SOURCE (whose version) × PROJECT (what shape) × TRANSPORT compose in a
/// single call, over the SAME proven engine lanes the 1.x read tools drive (CrossQuery / ResolveBatch /
/// ResolveRefs / the one-pole batch / the plugin-file overlay) — consolidation of the surface, not a re-implementation
/// of the engine. This PR ships the CORE forms (identity | summary | fields | everything | aggregate) and the
/// one-pole named SOURCE; the comparison/traversal forms (delta | tree | chain | info_order, walk=, versus=,
/// previous_provider, the SkyPatcher overlay source) arrive in W2 PR 2 — until then those spellings get a NAMED
/// staging refusal pointing at the 1.x tool that does the job today (never a silent gap). The 1.x read tools stay
/// registered and unchanged through the build waves; they retire at 2.0.0 (clean cut, CHARTER_PHASE4 §3.4a).
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
        [Description("The form: 'identity' (FormID -> type/editorid/name/winner — the labeling form; needs formids=) | 'summary' (identity plus winner/override-depth header facts — the default) | 'fields' (named field values; takes fields= and depth=) | 'everything' (the full record body; takes depth=) | 'aggregate' (a counted table; takes group_by=). The comparison forms (delta | tree | chain | info_order) arrive with the W2 comparison wave and are refused by NAME until then.")]
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
         "exists', membership 'formid not in @<file>' / 'Race in [XXXXXX:A.esm, YYYYYY:B.esm]', ONE '->' link step " +
         "'Perks->editorid startswith REQ_NULL_', and the provenance term 'winner = X.esp' — which records does X " +
         "WIN; that term forces winner resolution over the scanned scope, the same declared cost as any winner " +
         "scan) | references= (reverse, one step — requires a bounding types=/plugins= scope; the reverse-reference " +
         "index that would lift the bound is a known future capability). UNION-ARM tip: when a field is one of " +
         "several shapes (an NPC's Configuration.Level is a fixed level OR a PC-level multiplier), a scalar " +
         "predicate on one arm's sub-field doubles as an ARM-PRESENCE test: where=[\"Configuration.Level.LevelMult " +
         ">= 0\"] returns exactly the NPCs on a multiplier. In this wave formids= composes with the OTHER select " +
         "terms in W2 PR 2 — a combined call is refused by name until then.\n\n" +
         "SOURCE decides WHOSE version you read. Default: the load-order winner. Naming a plugin (source= " +
         "\"OldPatch.esp\", or {\"file\": \"X.esp\", \"mod\": \"<mod folder>\"} when two mods ship the filename) " +
         "reads THAT plugin's version wherever the plugin lives — active in your order, or sitting on disk unticked " +
         "— you do not have to know which, and the response STATES which arm resolved (active, or out-of-load-order " +
         "and from where). A plugin found in neither place is refused naming both places searched. A record the " +
         "named plugin does not touch is refused naming the plugins that DO touch it — never silently absent. " +
         "An off-order file's content sits OUTSIDE the epoch fingerprint and the response says so. " +
         "('previous_provider' and the SkyPatcher overlay source arrive in W2 PR 2.)\n\n" +
         "PROJECT is a single form (see project=): identity | summary (default) | fields | everything | aggregate. " +
         "Sub-parameters live INSIDE the form that uses them (depth belongs to fields/everything, group_by to " +
         "aggregate) — there is no flat spelling for an illegal pairing.\n\n" +
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
        [Description("SOURCE: whose version to read. Omit or \"winner\" for the load-order winner; a plugin filename (e.g. \"OldPatch.esp\") for that plugin's version WHEREVER it lives — active or on disk out of the order (the response states which); {\"file\": \"X.esp\", \"mod\": \"<mod folder>\"} when two mods ship the same filename. \"previous_provider\" and the runtime overlay arrive in W2 PR 2.")]
            JsonElement? source = null,
        [Description("The pole field VALUES display from, when it differs from the matching pole: \"winner\" shows the live winner's values on a plugins=-scoped scan (the old winner_fields=true). Display only — where_source= governs matching.")]
            string? fields_source = null,
        [Description("PROJECT: the shape of the answer — a single form plus its own sub-parameters. Omit for summary rows.")]
            RecordsProject? project = null,
        [Description("TRANSPORT: 'text' (default) | 'json' (machine-readable document; same accounting in-band) | 'dense' (scan lane: columnar positional rows — the compact bulk-enumeration form).")]
            string? format = null,
        [Description("TRANSPORT: max rows to render (default 500). The TRUE total is always reported; page with offset=.")]
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
            case "identity" or "summary" or "fields" or "everything" or "aggregate": break;
            case "delta" or "tree" or "chain" or "info_order":
                return $"error: project.form='{form}' is a W2 PR 2 form (comparison/traversal) and is not on this surface yet — " +
                       "meanwhile: delta = housecarl_diff_record; tree = housecarl_read_record conflict_tree=true; chain = " +
                       "housecarl_effect_chain / references=; info_order = housecarl_validate_dialogue.";
            default:
                return $"error: project.form='{project?.form}' is not a form — use identity | summary | fields | everything | aggregate.";
        }
        // Sub-parameters exist only inside their forms (SPEC §2.2) — a stray one is refused by name, so the caller
        // learns the form-scoping rule instead of getting a silently-ignored knob.
        if (project?.fields is { Length: > 0 } && form != "fields")
            return $"error: project.fields belongs to the 'fields' form only (got form='{form}'). Set project.form='fields', or drop fields.";
        if (form == "fields" && project?.fields is not { Length: > 0 })
            return "error: the 'fields' form names its field paths — pass project.fields=[\"<path>\", …] (or use form='everything' for the full body).";
        if (project?.depth is { } dv && dv > 1 && form is not ("fields" or "everything"))
            return $"error: project.depth expands field contents and belongs to the 'fields'/'everything' forms (got form='{form}').";
        if (project?.group_by is not null && form != "aggregate")
            return $"error: project.group_by belongs to the 'aggregate' form only (got form='{form}'). Set project.form='aggregate', or drop group_by.";
        if (form == "aggregate" && string.IsNullOrWhiteSpace(project?.group_by))
            return "error: the 'aggregate' form names its count key — pass project.group_by='winner' | 'type' | 'defined_in'.";
        if (project is { resolve_names: true } && form is not ("fields" or "everything"))
            return $"error: project.resolve_names annotates field values and belongs to the 'fields'/'everything' forms (got form='{form}').";
        int depth = project?.depth is { } d && d > 0 ? d : 1;
        var projFields = form == "fields" ? project!.fields : null;
        bool resolveNames = project?.resolve_names ?? false;

        // ---- SOURCE: the one-pole spelling --------------------------------------------------------------
        string? srcName = null; string? srcMod = null;
        if (source is { } se && se.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            if (se.ValueKind == JsonValueKind.String)
            {
                var s = se.GetString()!.Trim();
                if (s.Equals("previous_provider", StringComparison.OrdinalIgnoreCase))
                    return "error: source='previous_provider' is a W2 PR 2 pole (it pairs with the delta/tree comparison forms) — " +
                           "meanwhile housecarl_read_record conflict_tree=true shows the full provider stack.";
                if (s.Length > 0 && !s.Equals("winner", StringComparison.OrdinalIgnoreCase)) srcName = s;
            }
            else if (se.ValueKind == JsonValueKind.Object)
            {
                if (se.TryGetProperty("overlay", out _))
                    return "error: the runtime-overlay source (SkyPatcher post-state) is a W2 PR 2 pole — meanwhile use housecarl_skypatcher_read.";
                if (!se.TryGetProperty("file", out var fEl) || fEl.ValueKind != JsonValueKind.String)
                    return "error: a structured source= names the plugin as {\"file\": \"X.esp\"[, \"mod\": \"<mod folder>\"]} — 'file' is required.";
                srcName = fEl.GetString()!.Trim();
                if (se.TryGetProperty("mod", out var mEl) && mEl.ValueKind == JsonValueKind.String) srcMod = mEl.GetString()!.Trim();
            }
            else return "error: source= is a string (\"winner\" | a plugin filename) or {\"file\": …, \"mod\": …}.";
        }

        // ---- fields_source (display pole) ---------------------------------------------------------------
        bool winnerFields = false;
        if (!string.IsNullOrWhiteSpace(fields_source))
        {
            var fs = fields_source.Trim().ToLowerInvariant();
            if (fs == "winner") winnerFields = true;
            else if (fs is not ("scoped" or "scanned"))
                return $"error: fields_source='{fields_source}' — use 'winner' (display the live winner's values) or omit it (display the matched body). Further poles arrive with the W2 comparison wave.";
        }

        // ---- lane decision ------------------------------------------------------------------------------
        bool hasFormids = formids is { Length: > 0 };
        bool hasScan = types is { Length: > 0 } || plugins?.names is { Length: > 0 } || conflicts_only
                       || where is { Length: > 0 } || references is { Length: > 0 };
        if (!hasFormids && !hasScan)
            return "error: select something — formids= (a record list), or a scan scope: types=, plugins=, conflicts_only=true, where=, references=.";
        if (hasFormids && hasScan)
            return "error: formids= composes with the scan terms (types=/plugins=/where=/references=/conflicts_only=) in W2 PR 2 — " +
                   "not on this surface yet. Meanwhile: run the scan, spill/to_file the result, and re-enter it via where=[\"formid in @<file>\"]; " +
                   "or filter the formids list client-side.";
        // dense is DEFINED as positional columnar cells 1:1 with the requested field paths (the tool description's
        // own rule) — the forms with no fixed column set refuse by name rather than quietly switching transport
        // (re-review: everything fell back to text, aggregate to the json table, neither saying so).
        if (dense && form == "everything")
            return "error: format='dense' renders positional columnar cells 1:1 with requested field paths, and the 'everything' form has no fixed column set — use format='text' or 'json', or name the paths via form='fields'.";
        if (dense && form == "aggregate")
            return "error: format='dense' is the per-row columnar transport, and the 'aggregate' form is a count table — its json render IS the compact form; use format='json'.";
        // fields_source= is the SCAN lane's display pole in this wave (it retargets what a matched row DISPLAYS);
        // the list lane's read is its display, so the request would be silently meaningless there — refuse by
        // name (re-review: it was accepted and dropped). The generalized poles land with W2 PR 2's comparison forms.
        if (winnerFields && formids is { Length: > 0 })
            return "error: fields_source= is the scan lane's display pole in this wave — on a formids= read the version you want IS the source: name it via source= (source=\"winner\" is the default). The generalized display poles land with the W2 comparison forms.";

        if (offset < 0) return $"error: offset={offset} — offset must be >= 0.";
        var toFile = to_file?.Trim();
        bool wantFile = !string.IsNullOrEmpty(toFile);
        if (wantFile)
        {
            if (Artifacts.ValidateToFile(toFile!) is { } verr) return verr;
            if (offset > 0) return "error: to_file= captures the COMPLETE result (the artifact is never a window), so offset= has nothing to page — drop offset=.";
            if (form == "aggregate") return "error: to_file= writes row artifacts, and the aggregate form is a count table with no record rows — drop one of the two.";
        }
        if (where_source is not null && where is not { Length: > 0 })
            return "error: where_source= retargets the where= predicates and needs where= — add predicates, or drop where_source=.";

        // ---- the response envelope (form + resolved source arm) -----------------------------------------
        // Text renders get it as a header line; json renders carry the same pairs as top-level fields.
        var envelope = new List<KeyValuePair<string, string>> { new("form", form) };
        string headerLine = $"records  form={form}";
        void Arm(string statement) { envelope.Add(new("source", statement)); headerLine += $"  source={statement}"; }

        return hasFormids
            ? ListLane()
            : ScanLane();

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

            // ---- identity form: the labeling lane (absorbs housecarl_resolve). Winner frame by contract. --
            if (form == "identity")
            {
                if (srcName is not null)
                    return "error: the identity form is the load-order labeling frame (type/editorid/name/WINNER per FormID) — " +
                           "it does not take a source= pole. Use form='summary' or 'fields' for a named version's view.";
                var rows = svc.ResolveRefs(ids, demand, out var epoch, out var refusal);
                if (refusal is not null)
                    return json ? JsonWire.RenderError(refusal, epoch) : "error: " + refusal + $"\nepoch={epoch}";
                Arm("winner");
                SpillState? spill = null;
                if (wantFile)
                {
                    var (s, aerr) = Artifacts.WriteResolve(rows, epoch, toFile!, "to_file", Echo());
                    if (aerr is not null) return json ? JsonWire.RenderError(aerr, epoch) : "error: " + aerr;
                    spill = SpillState.Spilled(s!, manifestOnly: true);
                }
                string Render(SpillState? sp, out bool trunc) => json
                    ? JsonWire.RenderResolve(rows, max_chars, epoch, sp, out trunc, envelope)
                    : headerLine + "\n" + Wire.RenderResolve(rows, max_chars, epoch, sp, out trunc);
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
            if (srcName is null)
            {
                outcomes = svc.ResolveBatch(ids, readFields, false, depth, resolveNames, null, demand, out var refusal, out var refusalEpoch);
                if (refusal is not null)
                    return json ? JsonWire.RenderError(refusal, refusalEpoch)
                                : "error: " + refusal + (refusalEpoch is not null ? $"\nepoch={refusalEpoch}" : "");
                Arm("winner");
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

            if (form == "aggregate")
                return RenderListAggregate(outcomes, project!.group_by!, json, dense, epoch2, headerLine, envelope);

            if (counts_only)
            {
                int ok = outcomes.Count(o => o.Error is null), err = outcomes.Count - outcomes.Count(o => o.Error is null);
                return json
                    ? JsonWire.RenderCounts(envelope, outcomes.Count, ok, err, epoch2)
                    : $"{headerLine}\ncount={outcomes.Count} ok={ok} errors={err}" + (epoch2 is not null ? $"\nepoch={epoch2}" : "");
            }

            SpillState? spill2 = null;
            if (wantFile)
            {
                var (s, aerr) = Artifacts.WriteBatch(outcomes, toFile!, "to_file", Echo());
                if (aerr is not null) return json ? JsonWire.RenderError(aerr, epoch2) : "error: " + aerr;
                spill2 = SpillState.Spilled(s!, manifestOnly: true);
            }
            string Render2(SpillState? sp, out bool trunc) => form == "summary"
                ? RenderRecordsSummary(outcomes, json, headerLine, envelope, max_chars, sp, out trunc)
                : json ? JsonWire.RenderBatch(outcomes, max_chars, sp, out trunc, envelope)
                       : headerLine + "\n" + Wire.RenderBatch(svc, outcomes, projFields, false, max_chars, sp, out trunc);
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
        //  SCAN lane — types/plugins/where/references/conflicts_only drive; SOURCE picks the universe.
        // ================================================================================================
        string ScanLane()
        {
            if (form == "identity")
                return "error: the identity form labels a formids= list; a scan's summary rows already carry each match's identity — use form='summary' (the default).";

            bool hasBodyFilter = where is { Length: > 0 } || references is { Length: > 0 };
            bool hasTypes = types is { Length: > 0 };
            bool hasScope = plugins?.names is { Length: > 0 };
            if (hasBodyFilter && !hasTypes && !hasScope)
                return "error: where=/references= is a body scan and must be combined with types= or plugins= to bound the work " +
                       "(conflicts_only= alone is not enough — an unbounded body scan over the whole order is refused; the " +
                       "reverse-reference index that would lift this is a known future capability).";
            if (plugins is { defined_in: true } && !hasScope)
                return "error: plugins.defined_in=true keeps records DEFINED in the scoped plugins, so plugins.names must name that scope.";

            // ---- OFF-ORDER source universe: the file's own records (absorbs read_plugin_file's enumeration).
            if (srcName is not null)
            {
                // Resolve which arm ONCE via a cheap containment probe: the scan below re-captures, and the epoch
                // stamp on its outcome is compared by the caller reading both — a mid-call order change surfaces
                // as differing epochs, never a silently mixed answer.
                var probe = svc.ProbeSourceArm(srcName, srcMod, out var probeErr);
                if (probeErr is not null) return "error: " + probeErr;
                if (!probe!.InOrder)
                    return OffOrderScan(probe);
                if (hasScope)
                    return "error: a plugins= scope combined with an ACTIVE named source= (scope-vs-pole streams) lands in W2 PR 2 — " +
                           "meanwhile: scope to the source plugin itself (plugins.names=[source]) reads its records, or use " +
                           "fields_source='winner' for winner-value display.";
                // ACTIVE arm: the pole's records ARE the scan universe — the plugins= stream with the arm stated.
                Arm($"{probe.Plugin} — active in the load order");
            }
            else Arm("winner");

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

            var scanPlugins = srcName is not null ? new[] { srcName } : plugins?.names;
            bool definedIn = plugins?.defined_in ?? false;
            var groupBy = form == "aggregate" ? project!.group_by!.Trim().ToLowerInvariant() : null;
            int effLimit = wantFile ? int.MaxValue : counts_only ? 0 : (limit <= 0 ? 500 : limit);

            var outcome = svc.CrossQuery(types, refFks, null, conflicts_only, scanPlugins, where,
                                         effLimit, definedIn, groupBy, offset, where_source,
                                         refDemand is null ? null : new[] { refDemand });

            List<KeyValuePair<string, string>> Echo()
            {
                var e = new List<KeyValuePair<string, string>>();
                void Add(string k, string? v) { if (!string.IsNullOrEmpty(v)) e.Add(new(k, v!)); }
                Add("form", form);
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
                Add("source", srcName);
                return e;
            }

            // ---- form=everything on a scan: selection here, bodies via the batch lane (window-bounded). --
            if (form == "everything" && outcome.Error is null && outcome.Groups is null)
            {
                var keys = outcome.Keys.Select(k => k.ToString()).ToList();
                IReadOnlyList<ReadOutcome> bodies;
                if (srcName is not null)
                {
                    bodies = svc.ResolveBatchFromPole(keys, srcName, srcMod, null, depth, resolveNames, null,
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
                // The selection and every body read must agree on ONE build (grouped reads capture per batch) —
                // any divergence refuses loud rather than mixing builds. An empty selection has no body epochs
                // and passes: it renders as an honest 0-row batch.
                var bodyEpochs = bodies.Where(o => o.Epoch is not null).Select(o => o.Epoch!).Distinct().ToList();
                if (outcome.Epoch is not null && bodyEpochs.Any(e => e != outcome.Epoch))
                    return $"error: the load order changed between the scan (epoch={outcome.Epoch}) and the body read " +
                           $"(epoch={string.Join(", ", bodyEpochs.Where(e => e != outcome.Epoch))}) — the two halves would mix builds. Retry the call.";
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
            Arm($"{pole.Plugin} — {pole.Where}");
            envelope.Add(new("epoch_covers_source", "false"));
            if (conflicts_only)
                return "error: conflicts_only= has no meaning on an out-of-load-order file — it is not in the conflict frame. Drop it, or read the winner (source=\"winner\").";
            if (references is { Length: > 0 } || plugins?.names is { Length: > 0 } || (plugins?.defined_in ?? false))
                return "error: references=/plugins= over an out-of-load-order file land in W2 PR 2 — this wave reads the file by types= (and an 'editorid contains' where-clause).";
            if (form is "fields" or "everything")
                return "error: the fields/everything forms over an out-of-load-order file SCAN land in W2 PR 2 — meanwhile enumerate with form='summary', then read the specific records via formids= (the one-pole batch reads any of the file's records in full).";
            if (dense) return "error: format='dense' is the in-order scan's columnar form — an off-order file scan renders text or json.";
            if (wantFile) return "error: to_file= over an out-of-load-order file scan lands in W2 PR 2 — enumerate inline, or batch-read via formids= with to_file=.";
            if (offset > 0) return "error: offset= paging over an out-of-load-order file scan lands in W2 PR 2 — this wave renders the file's first limit= rows (narrow with types= or an 'editorid contains' clause).";
            if (counts_only) return "error: counts_only= over an out-of-load-order file scan lands in W2 PR 2 — meanwhile form='aggregate' group_by='type' is the whole-file census.";

            // The one where-clause this lane carries natively: a single 'editorid contains <text>'.
            string? eidContains = null;
            if (where is { Length: > 0 })
            {
                if (where.Length == 1 && TryEditorIdContains(where[0], out eidContains)) { }
                else return "error: over an out-of-load-order file this wave carries exactly one where-clause form: [\"editorid contains <text>\"] — " +
                            "the full predicate grammar over off-order bodies lands in W2 PR 2.";
            }

            string? typeArg = null;
            if (types is { Length: > 1 })
                return "error: an out-of-load-order file scan reads ONE types= entry at a time in this wave — call per type, or use form='aggregate' group_by='type' for the whole file's census.";
            if (types is { Length: 1 }) typeArg = types[0];
            if (form == "aggregate")
            {
                var gb = project!.group_by!.Trim().ToLowerInvariant();
                if (gb != "type")
                    return $"error: over an out-of-load-order file the aggregate form counts by 'type' (the file's own record census) — group_by='{gb}' has no frame there.";
                typeArg = null;   // no type filter ⇒ the whole-file record-type summary
            }

            var o = svc.ReadPluginFile(pole.Plugin, null, typeArg, srcMod, null, 1, eidContains, limit <= 0 ? 500 : limit, false);
            return json ? JsonWire.RenderPluginFile(o, max_chars, envelope)
                        : headerLine + "\n" + Wire.RenderPluginFile(o, max_chars);
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
