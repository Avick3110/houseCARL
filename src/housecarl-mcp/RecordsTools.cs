using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;
using Mutagen.Bethesda.Plugins;

namespace HousecarlMcp;

/// <summary>housecarl_records — the read surface: SELECT, SOURCE, PROJECT and TRANSPORT compose in one call over ten form-scoped project forms; contracts in docs/architecture/records-tool-front.md.</summary>
[McpServerToolType]
public static partial class RecordsTools
{
    /// <summary>The rows a lane renders when the caller names no limit=, spelled once so the row lanes and the
    /// count TABLE read an unset limit the same way (#810).</summary>
    internal const int DefaultLimit = 500;

    /// <summary>A caller's limit= turned into a count TABLE's row cap, reading an unset limit exactly as every ROW
    /// lane on this tool reads it — <c>limit &lt;= 0</c> is the 500 default, which is what the parameter description
    /// promises the table too. The asset tool's <c>RowLimit</c> maps the same input to "uncapped" because its own
    /// limit= parameter defaults to 0; this one defaults to 500, so one rule per TOOL is the rule, not one
    /// expression across both (#810, Aaron 2026-09-22).</summary>
    internal static int TableRowLimit(int limit) => limit <= 0 ? DefaultLimit : limit;

    /// <summary>The plugins= SELECT scope: which records are considered, as against source=, which decides whose version is read.</summary>
    public sealed class RecordsScope
    {
        [Description("Plugin filenames to scope the scan to (records those plugins touch), e.g. [\"Requiem.esp\"].")]
        public string[]? names { get; set; }

        [Description("When true, keep only records DEFINED IN (originating from) the named plugins, dropping records they merely override.")]
        public bool defined_in { get; set; }
    }

    /// <summary>PROJECT — the shape of the answer: one form, with its sub-parameters inside it, so there is no flat spelling for an illegal pairing.</summary>
    public sealed class RecordsProject
    {
        [Description("The form: 'identity' (FormID -> type/editorid/name/winner — the labeling form; needs formids=) | 'summary' (identity plus winner/override-depth header facts — the default) | 'fields' (named field values; takes fields= and depth=) | 'rows' (a LIST field folded to ONE LINE PER ELEMENT — the compact per-row view: takes fields= naming the list (index one element, 'Conditions[0]', to fold just that one), and depth= (default 4). Each line is the element's own summary plus every sub-field the read FOUND; only ABSENT optionals are omitted, which is what turns a 40-row condition stack from ~1,000 lines into 40. Auditing that stack (project.fields=[\"Conditions\"]) is ONE call, not an index probe per row. A declared-but-null link is kept — an empty slot is a fact. A named field that is not a list is refused by name) | 'everything' (the full record body; takes depth=) | 'aggregate' (a counted table; takes group_by=) | 'delta' (subject vs reference, differences only — source= is the subject, versus= the reference; takes fields= to narrow. Each delta line shows the SUBJECT's value with the reference's beside it, labeled by its plugin; versus=\"previous_provider\" answers 'what did this plugin change relative to what sat beneath it') | 'tree' (the conflict-resolution view: every provider of each record in priority order, winner last, each showing only the fields that DIFFER from the reference pole — default the winner; takes fields=. On a record type that OWNS child records — a cell's placed references, a topic's INFO lines, a worldspace's cells — it also states, per such field, which providers DECLARE children there (a COLLECTION field) or how many do (a SINGULAR one, e.g. Cell.Landscape), and says so when none do) | 'info_order' (DIAL topics only: the effective MERGED INFO sequence across every touching plugin, with MOVED annotations — the 'why does the wrong line play' diagnostic. The game walks the sequence top to bottom and plays the FIRST passing line; re-listing a line appends it to the BOTTOM unless the plugin also carries its PNAM, so a reorder changes which line answers while every field stays identical — invisible to a diff, which is what this form is for. A quest's topics select by composition: types=[\"DIAL\"] where=[\"Quest = <quest formid>\"]. A patch that is NOT yet enabled folds in: source=\"MyPatch.esp\" names ONE off-order file, and the merge answers as it WOULD be with that file enabled — placed where MO2 would put it, which is the END of the order for a regular plugin and the end of the MASTER BLOCK for an ESM-flagged one or a .esm/.esl — with every line it places marked, the placement and the flag behind it stated, and the projection stated) | 'chain' (a walk's own paths, endpoints and cycles rather than the records it reached — a cycle being a record the walk reached again from itself, found over the nodes it actually ENTERED — so one closing past walk.depth or walk.max_nodes is not yet visible — and reported ONE PER CLOSING LINK, which makes the count a lower bound on how many distinct loops are there — the search also stops at 200 loops per seed and says so. Over a walk that FINISHED, no cycles reported means there are none; over one the response says was cut at walk.depth or walk.max_nodes it means only that none was found in what was read; needs walk=, and carries the NPC-template inheritance report and the reverse MGEF carrier rows). The comparison forms 'delta' and 'tree' both compare by the content-keyed, truncation-honest engine: a list reorder is flagged, and a truncated deep read is reported, never claimed 'identical'.")]
        public string? form { get; set; }

        [Description("fields/rows forms: dotted field paths to read, e.g. [\"BasicStats.Damage\", \"Keywords\", \"Effects\"]. Index a list/dict element with BRACKETS ('Effects[0].Data.Magnitude'). A path may LEAD with the containment step '*parent' — the record that CONTAINS this one, which group nesting makes invisible to references= ('*parent.EditorID' is an INFO's owning DIAL; '*parent.*parent.EditorID' a placed reference's worldspace) — and it chains. On the rows form these name the LIST(S) to fold, one line per element. On the fields form a step may be QUANTIFIED: 'Effects[*count]' is one number per record (how many elements), 'Effects[*]' one row per element in the rows form's own row shape, and 'Effects[*].Data.Magnitude' that leaf per element — under format='dense' the extra rows repeat the record's identity columns. [*any]/[*all]/[*none] fold to a boolean, which is not a row: they belong in where= and are refused here by name.")]
        public string[]? fields { get; set; }

        [Description("fields/rows/everything forms: expansion depth for list/dict/substruct CONTENTS (default 1, or 4 on the rows form, where a shallower read renders every element as a bare type). THIS is the expansion knob: fields=[\"Effects\"], depth=4 reaches every effect's Magnitude/Area/Duration — no hand-written index guessing.")]
        public int? depth { get; set; }

        [Description("aggregate form only: the count key — 'winner' (by winning plugin), 'type' (by record type; needs types= or plugins=), or 'defined_in' (by defining plugin).")]
        public string? group_by { get; set; }

        [Description("fields/rows/everything forms: annotate every FormLink value with its target's identity (-> editorid \"Name\"). Display-only — the token itself still round-trips to a write.")]
        public bool resolve_names { get; set; }
    }

    /// <summary>The traversal construct: follow record-to-record form links from this call's own SELECT and select what the walk reaches; expanding fields WITHIN a record is project.depth.</summary>
    public sealed class RecordsWalk
    {
        [Description("Link-bearing field paths that start the walk from each seed, e.g. [\"HeadParts\", \"WornArmor\"]. '*parent' crosses the containment edge instead — the record that CONTAINS the seed (a REFR from a crash log to its CELL; '*parent.*parent' to the worldspace). Omit for every link on the seed.")]
        public string[]? seed_paths { get; set; }

        [Description("The link path followed at every LATER hop — the edges this walk crosses, in either direction. \"*\" (default) walks every link — full closure. A named path restricts to one chain, e.g. \"Template\" for NPC template inheritance, or \"*parent\" to climb containment. On direction='reverse' the two legal values are \"*\" (the default under a reading form; the transitive walk over every link, off the reverse-reference index) and \"Effects[].BaseEffect\" (the typed MGEF carrier walk) — under project.form='chain' there is no default and one of the two is said outright.")]
        public string? follow { get; set; }

        [Description("'forward' (default) — what the seeds point AT (cheap: each hop is one link resolve). 'reverse' — what points AT the seeds, at any depth, needing no bounding scope. walk.follow picks the reverse walk, exactly as it does forward: \"*\" (or, under a reading form, unset) follows EVERY link at every hop off the reverse-reference index, which is built on the first such call at the cost of one whole-order link-walk and reports that cost in the response; follow=\"Effects[].BaseEffect\" is the typed MGEF carrier walk — magic-effect seeds, per-carrier magnitude/area/duration, types= narrowing the carrier types — which reaches nothing past hop 1 because a carrier is not a magic effect. The FORM then only picks the view and never implies a walk: 'chain' renders the walk's own rows, a reading form (summary/fields/rows/everything/aggregate) consumes the same reached set. Under form='chain' walk.follow is REQUIRED — chain can only draw the carrier walk's per-seed paths, and the transitive walk expands one shared frontier with no path per seed, so an unset follow refuses naming both rather than picking one. references= is the same reverse question as one step of SELECT.")]
        public string? direction { get; set; }

        [Description("Maximum hops from a seed (default 16). Nodes AT the cap are recorded, not entered, and the response says the cap cut the walk — never a silent stop.")]
        public int? depth { get; set; }

        [Description("The node budget (default 2000, the read-expansion budget). Per seed on a forward walk and on the reverse carrier walk (each seed's carrier rows), where 250000 is the hard upper bound and a higher value is refused; ONE budget shared across every seed and every hop on the transitive reverse walk, whose hops are one frontier and not a per-seed expansion. A breach keeps what was proved and says which reading it spent. A reading form (summary/fields/rows/everything/aggregate) then renders the whole reached set — seeds times this budget at the worst — and reads a body per row, so it is held to the same render bound a scan is on EVERY walk lane, and refuses up front naming that lane's own levers: its seeds, this budget, and — on the forward and carrier walks, the two chain can draw — project.form='chain', which lists the same set without reading a body per rendered row (the walk reads one per reached node whatever the form).")]
        public int? max_nodes { get; set; }

        /// <summary>The fixed hard upper bound on the PER-SEED reading of <see cref="max_nodes"/>, and not on the transitive reverse walk's one shared budget; contract in docs/architecture/records-tool-front.md.</summary>
        internal const int Ceiling = 250_000;

        [Description("Node classes the walk must not enter, as data: [{\"match\": \"Race\", \"severity\": \"stop\"|\"refuse\"}] — match is the record type name a read reports; stop prunes there (recorded as a boundary), refuse fails the whole call loud.")]
        public RecordsWalkExclusion[]? exclusions { get; set; }
    }

    public sealed class RecordsWalkExclusion
    {
        [SchemaRequired, Description("The record type name to match (as reads report it, e.g. 'Race', 'Npc').")]
        public string? match { get; set; }

        [SchemaRequired, Description("'stop' (prune here, record the boundary) or 'refuse' (the whole walk fails loud).")]
        public string? severity { get; set; }
    }

    [McpServerTool(Name = ToolNames.Records, ReadOnly = true, Title = "Read records"),
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
         "Each axis's grammar is on its own parameters:\n" +
         "SELECT — formids= | types= | plugins= | conflicts_only= | where= | references= | walk=.\n" +
         "SOURCE — source= is the subject, versus= the reference pole; where_source= | fields_source= split " +
         "matching from display.\n" +
         "PROJECT — project=, one form plus that form's own sub-parameters.\n" +
         "TRANSPORT — format= | limit= | offset= | max_chars= | counts_only= | to_file=.\n\n" +
         "COMPOSITION: formids= composes with the scan terms — the identity set intersects the scan, or, alone, " +
         "IS the scan universe (the set is the bound, so a where= over it needs no types=/plugins=). The walk= " +
         "construct is a SELECT term too: what it reaches is a selection any reading form can consume.\n\n" +
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
        [Description("SELECT: body predicates, ANDed. COMPARISONS: 'BasicStats.Damage >= 50', 'editorid contains Iron', 'editorid startswith REQ_' (the 'editorid' term replaces editorid_contains=). NEGATION: a leading 'not' complements a STRING operator — 'Name not contains Dagger', 'editorid not startswith REQ_'; the other operators keep the complement they already have ('!=' for '=', 'missing' for 'exists', 'has_none' for 'has', 'not in' for 'in'), and 'not' in front of one of those is refused by name. On a FIELD path a negated term matches only records the path READS A VALUE on, so it is the complement over value-bearing records, not over every record: 'Name contains X' + 'Name not contains X' + 'Name missing' partitions the scope. The IDENTITY term 'editorid' is the exception and always gives a verdict, so a record with NO EditorID is on the negated side — exactly where '!=' and 'not in' already put it — and there the three counts overlap by those records. FLAG TESTS: 'BodyTemplate.FirstPersonFlags has Body' — every operand bit set — with 'has_any' (at least one set) and 'has_none' (none set) as the other two folds over the same bits. QUANTIFIED STEPS over a list field: 'Conditions[*any].Data.Function = IsGuard', 'Effects[*none].BaseEffect->editorid startswith REQ_' (absence, proved), 'Effects[*count] > 2' — [*any]/[*all]/[*none] fold the elements into a boolean, [*count] into their number, and [*all] is vacuously true on an empty list; a quantified step COSTS the list's length (per-candidate work times the number of elements) and, where its sub-path carries '->', one winner fetch PER ELEMENT — 'Temporary[*any]->…' on a dense cell is hundreds of fetches per candidate. The CONTAINMENT step '*parent' is the record that CONTAINS this one, which group nesting makes invisible to references=: '*parent.EditorID = GreetingsTopic' is an INFO's owning DIAL, '*parent.*parent.EditorID = Tamriel' a placed reference's worldspace; it LEADS a path and chains. PRESENCE: 'VirtualMachineAdapter exists' (and 'missing'). MEMBERSHIP: 'formid not in @<file>' — the @file spelling, which is how a spilled artifact re-enters — or 'Race in [XXXXXX:A.esm, YYYYYY:B.esm]'; list entries separate on commas/newlines with brackets and quotes stripped, so a value that itself contains a comma or bracket is not expressible in a list — test it with '='. ONE '->' LINK STEP: 'Perks->editorid startswith REQ_NULL_'. PROVENANCE: 'winner = X.esp' — which records does X WIN; that term forces winner resolution over the scanned scope, the same declared cost as any winner scan. UNION-ARM tip: when a field is one of several shapes (an NPC's Configuration.Level is a fixed level OR a PC-level multiplier), a scalar predicate on one arm's sub-field doubles as an ARM-PRESENCE test: where=[\"Configuration.Level.LevelMult >= 0\"] returns exactly the NPCs on a multiplier. A body scan — must be combined with types= or plugins= to bound the work. A wrong path is reported loud, never a silent '0 matches'. Paths are scalar leaves: step into a list element with BRACKETS ('Effects[0].Data.Magnitude'), never a dotted hop; a WILDCARD over a list ('Effects[*].Magnitude') is a known future capability and is not built. FormLink equality takes a wire token ('WorkbenchKeyword = 088108:Skyrim.esm'), so a link filter is one predicate rather than a post-filter. A flag test uses has, never '=': '= 4194304' matches only records whose flags are EXACTLY that bit and skips every combination containing it.")]
            string[]? where = null,
        [Description("Which BODY the where= predicates decide the MATCH on: 'scoped' (default — the body the scan streams) or 'winner' (the live load-order winner regardless of scan scope; the post-patch audit answer). Match only — fields_source= independently governs display.")]
            string? where_source = null,
        [Description("SELECT: find records that REFERENCE these FormIDs (reverse, one step; OR over the list, each match names which target(s) it hit). Needs no bounding scope: unbounded it is answered off the reverse-reference index, which is built on the first such call, costs one whole-order link-walk, and reports that cost and its own per-plugin freshness key in the response. A bounded references= — with a types= or plugins= — is unchanged and still cheaper. A '!' before an entry NEGATES it: references=[\"!XXXXXX:A.esm\"] keeps only records that do NOT reference that target, and plain and negated entries in one call compose by AND; the sigil takes the @file spelling too — references=[\"!@C:/work/targets.jsonl\"] excludes every target the file names. A negated entry ALONE with no types=/plugins= scope is the ORPHAN sweep: the universe becomes every record nothing in the order references, and the named target then excludes any of those that link it — bound the call if you meant the narrower question. Accepts [\"@<path>\"] like formids=.")]
            string[]? references = null,
        [Description("SOURCE decides whose version you read; this is the SUBJECT of the call. Omit or \"winner\" for the load-order winner (the default). A plugin filename (e.g. \"OldPatch.esp\") reads THAT plugin's version WHEREVER the plugin lives — active in your order, or sitting on disk unticked — you do not have to know which, and the response STATES which arm resolved (active, or out-of-load-order and from where); use {\"file\": \"X.esp\", \"mod\": \"<mod folder>\"} when two mods ship the same filename. A plugin found in neither place is refused naming both places searched. A record the named plugin does not touch is refused naming the plugins that DO touch it — never silently absent. {\"overlay\": \"skypatcher\", \"state\": \"pre\"|\"post\"} reads around the SkyPatcher INI layer (post = after it replays); add \"ini\": \"<absolute path to a draft .ini>\" (with \"subfolder\": the SkyPatcher type folder it would be placed in, or omit it when the draft's parent directory already IS that folder) to read the post state with a draft INI that is not yet in a mod folded into the layer, so a draft can be checked before it is placed. Content read from outside the load order — an off-order file, or the SkyPatcher INI layer — sits OUTSIDE the epoch fingerprint, and the response says so. On project.form='info_order' this parameter means something narrower: the merge IS the answer there, so the one value it takes is ONE OFF-ORDER plugin, FOLDED into the merge where MO2 would load that file — the END of the order for a regular plugin, after the LAST MASTER for an ESM-flagged one or a .esm/.esl, and the plugin's OWN SLOT when the order already carries that FILENAME (a shadowed copy named by {\"file\", \"mod\"}: enabling its mod folder swaps the bytes at a position the order already has). The response names the placement, the neighbour it landed beside and the flag behind it. A filename whose ACTIVE copy is the one you named is refused — it is already in the merge. This is how a dialogue patch's merged order is read before MO2 enables it. \"previous_provider\" is a versus= value only — it is measured FROM the subject this parameter names.")]
            JsonElement? source = null,
        [Description("SOURCE (comparison forms): the REFERENCE pole a delta/tree compares against. Same forms as source= — \"winner\" | a plugin filename | {\"file\", \"mod\"} | {\"overlay\", \"state\"[, \"ini\", \"subfolder\"]} — plus \"previous_provider\": the plugin immediately below the SUBJECT (whatever source= names) in the record's touching stack, measured FROM THE SUBJECT, never from the winner. Its four cases are all declared: subject=winner → next plugin down; subject mid-stack → still the one below the SUBJECT, with what sits above reported as plain fact (a mid-stack patch is ordinary practice, not judged); subject defines the record → refused naming it (never an empty diff that reads as 'no changes'); subject doesn't touch it → refused naming the actual touchers. REQUIRED when project.form='delta'; defaults to \"winner\" on 'tree'; refused on other forms.")]
            JsonElement? versus = null,
        [Description("The pole field VALUES display from, when it differs from the matching pole: \"winner\" shows the live winner's values on a plugins=-scoped scan (the old winner_fields=true). Display only — where_source= governs matching. Under a plugins= scope the default renders the SCOPED plugin's OWN values — the defining plugin's era, not the number the game uses after later overrides — so a deliverable claiming live stats passes \"winner\" here. Neither pole reaches TEMPLATE inheritance: an NPC whose Configuration.TemplateFlags include Stats takes its level, and the rest of that category, from the record its Template points at, so the level rendered on its own row is not the level the game uses — walk={\"follow\": \"Template\"} under project.form='chain' reports, per category, which rows inherit and from which record, so read the inheritance there rather than by hand.")]
            string? fields_source = null,
        [Description("PROJECT: the shape of the answer — a SINGLE form plus that form's own sub-parameters: identity | summary (default) | fields | rows | everything | aggregate | delta | tree | chain | info_order. Sub-parameters live INSIDE the form that uses them (depth belongs to fields/rows/everything, group_by to aggregate, fields to fields/rows/delta/tree), so there is no flat spelling for an illegal pairing. Omit for summary rows. aggregate COUNTS all matches whatever limit= says — the total and the distinct-group count are exact — so limit= caps the count TABLE's rows instead (default 500, and the marker says how many it held back) and offset= is refused, the same rule housecarl_asset_status's counts_only= census caps on. A count is safe to size a job with on either lane.")]
            RecordsProject? project = null,
        [Description("SELECT: the traversal construct — follow record-to-record links from this call's own SELECT (the seeds) and select what the walk reaches; project.form='chain' renders the paths/endpoints/cycles themselves (with, for NPC template chains, the per-category active-vs-masked inheritance report), while any other form reads the reached set like any selection. The walk expands on the WINNER's link graph; source= governs whose version the form then reads.")]
            RecordsWalk? walk = null,
        [Description("TRANSPORT: 'text' (default) | 'json' (machine-readable document; same accounting in-band) | 'dense' (scan lane: positional columnar cells 1:1 with the requested fields — the compact bulk-enumeration form; by that definition depth expansion and the 'everything' form are inexpressible in it). On 'json' a record's fields are an ORDERED LIST of {path, value} — a field that read NO value carries {path, note} instead, saying why — and the emission order is the answer's own, and a path REPEATS under a quantified step ('Effects[*].Data.Magnitude'), which a map keyed by path could not hold; 'dense' is the column table to join on. Every response carries the epoch stamp — the identity of the index build it was answered from — spelled epoch=<hex> on 'text' and 'dense', and as an 'epoch' member on 'json'.")]
            string? format = null,
        [Description("TRANSPORT: max rows to render (default 500). The TRUE total is always reported; page a scan in exact windows with offset=. DECLARED COST: a delta or tree row reads every PROVIDER of its record, so on a scan limit= and offset= bound the WORK there and not just the render — only the windowed rows are read — and past a 250-row bound (about a minute) the call refuses up front with the count and the estimate instead of going quiet. A census or a to_file= artifact on those forms still covers the WHOLE selection and is held to the same bound, so what narrows those is the scan terms. The other derived-selection forms (chain/info_order, and any walk) consume EVERY scan match — their censuses and artifacts cover the full selection, and limit= windows only the rendered rows — so on a big order the SCAN TERMS (types=/plugins=/where=) are the cost bound: narrow them. RENDER COST: a row that READS a record's body is what costs, and a scan's accounting reports what that cost as render_ms; a render too big to finish REFUSES up front rather than going silent, naming the shapes that fit. The bound holds on every lane that reads bodies — a scan, an off-order source=, a formids= list — and is per form, because the row costs differ by orders of magnitude: 300,000 rows for a named-fields row (fields/rows, and the one cheap leaf summary/aggregate take), 15,000 for form='everything', whose row materialises the WHOLE record — name the fields you need and the same selection fits — and 40,000 for form='identity', whose row is an UNTYPED whole-plugin seek for the winner's body and is the dearest row here rather than a free one: form='summary' answers the same identity question off a gathered read. On a formids= read every one of those six forms reads a body and is bounded on the LIST's length, not this window's: the ids are read before limit= and offset= apply, so pass fewer ids rather than paging. delta and tree are bounded on the list's length the same way, at their own 250-row bound. The accounting beside it counts the bodies READ, which a source= pole holding no version of an id, or a malformed id, leaves short of the list.")]
            int limit = DefaultLimit,
        [Description("TRANSPORT: skip the first N matches (exact windows: offset=0/500/1000…). Windows tile only WITHIN one epoch — if two pages' epochs differ the load order changed mid-pagination; re-run from offset=0, do not stitch the pages. offset= RE-SCANS the selection from the start rather than seeking into it, so every window pays the whole scan again and a deep window costs more than a shallow one — narrowing the scan terms beats paging far into one. Refused with to_file=, with the aggregate form and with counts_only=, none of which renders a selection window: a count table caps with limit= and does not page.")]
            int offset = 0,
        [Description("TRANSPORT: character CEILING on the RENDER, hard on every text render this tool has — the scan, batch, resolve, group_by and summary renders, the comparison forms (delta, tree), the walk lane's chain and effect-chain renders, info_order, and every form's counts_only census. The record block, node or delta line that would cross it is not written, and the truncation notice, the accounting line and the spilled: block are charged before the rows are laid — charged only where the whole render does not fit, so an answer that fits inside the max_chars you passed comes back complete, uncut and unspilled. The one answer that can still come back over it is a max_chars too small for what the response carries whatever the budget — its header, the notices it owes, its spilled: block — which says so and names the number that clears it. Never truncates the RESULT: an over-ceiling result SPILLS in full to a server-side JSONL artifact (line 1 = manifest with the query echo, the row schema, and the epoch) and the response names the file, so what the ceiling held back inline is in the file. 0 = the server default (~80k).")]
            int max_chars = 0,
        [Description("TRANSPORT: return the accounting block and counts only, no rows — the cheap census.")]
            bool counts_only = false,
        [Description("TRANSPORT: write the COMPLETE result to this ABSOLUTE .jsonl path as an artifact (line 1 = manifest) and render only the manifest inline. Re-enter it later via formids=[\"@<path>\"] or where=[\"formid in @<path>\"] — epoch-checked. The artifact is never a window: offset= is refused with to_file=, and because to_file= renders EVERY selected row it pays the same per-row body read an inline render does and is held to the same bound (see limit=). The manifest is the file's own accounting and is what you read back, not the chat summary: it carries the query echo, identity, row_schema, sort, row_count, total, type_counts where the rows carried types, epoch, created, and notes when the result owes a clause — the file holds the COMPLETE result only when row_count equals total, and windows joined across differing epochs are not one result.")]
            string? to_file = null,
        CancellationToken ct = default) => Guard.Tool(ToolNames.Records, () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;

        // ---- TRANSPORT: format --------------------------------------------------------------------------
        var fmt = Wire.CrossQueryFormat(format, out var ferr);
        if (ferr is not null) return ferr;
        bool json = fmt is Wire.QueryFormat.Json;
        bool dense = fmt is Wire.QueryFormat.Dense;

        // ONE FormID door for the whole call; contract in docs/architecture/records-tool-front.md.
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
        // Every reading form reads a body per row, so this is the set a derived selection's render bound is measured over.
        bool bodyForm = bodyFields || form is "summary" or "everything" or "aggregate";
        // Form-scoping: a sub-parameter outside its form is refused by name; docs/architecture/records-tool-front.md.
        if (project?.fields is { Length: > 0 } && !bodyFields && !comparisonForm)
            return Wire.Refuse(json, $"error: project.fields belongs to the 'fields'/'rows'/'delta'/'tree' forms (got form='{form}'). Set project.form, or drop fields.");
        if (form == "fields" && project?.fields is not { Length: > 0 })
            return Wire.Refuse(json, "error: the 'fields' form names its field paths — pass project.fields=[\"<path>\", …] (or use form='everything' for the full body).");
        if (form == "rows" && project?.fields is not { Length: > 0 })
            return Wire.Refuse(json, "error: the 'rows' form folds a LIST field to one line per element and names that field — pass project.fields=[\"Conditions\"] (or any list path).");
        // Every entry has to be a path, since the fold reads the roots to decide what a line belongs to.
        if (form == "rows" && project?.fields is { } rowFields && Array.FindIndex(rowFields, p => string.IsNullOrWhiteSpace(p)) is var badAt && badAt >= 0)
            return Wire.Refuse(json, $"error: project.fields[{badAt}] is empty — the 'rows' form folds the list each entry names, so every entry must be a field path (e.g. [\"Conditions\"]).");
        // The quantified step's PROJECT half, parsed before any read so a bad token refuses the call, not each record.
        FoldPlan? foldPlan = null;
        if (project?.fields is { Length: > 0 } pf)
        {
            var (plan, foldErr) = FieldFolds.Parse(pf);
            if (foldErr is not null) return Wire.Refuse(json, "error: " + foldErr);
            foldPlan = plan;
            // The quantifier belongs to the form that reads a path as a projection; elsewhere it is refused by name.
            if (foldPlan is not null && form != "fields")
                return Wire.Refuse(json, form == "rows"
                    ? $"error: project.fields path '{foldPlan.First.Requested}' quantifies a step, and the 'rows' form already folds the list it names to one line per element — drop the token, or use form='fields' to mix quantified and ordinary paths."
                    : $"error: project.fields path '{foldPlan.First.Requested}' quantifies a step, and the '{form}' form lines its two sides up path for path — drop the token, or read the elements with form='fields'.");
            // dense lays ONE row per element, and two different lists share no element to lay a row on.
            if (foldPlan is not null && dense && foldPlan.SetRoots is { Count: > 1 } roots)
                return Wire.Refuse(json, $"error: format='dense' lays one row per element, and '{roots[0]}' and '{roots[1]}' are different lists — one row cannot be an element of both. Quantify one of them and read the other as an ordinary path, or make one call per list.");
        }
        if (project?.depth is { } dv)
        {
            // Any explicit depth is form-scoped whatever its value, and 0 or negative is refused, not coerced to 1.
            if (form is not ("fields" or "rows" or "everything"))
                return Wire.Refuse(json, comparisonForm
                    ? $"error: project.depth belongs to the 'fields'/'rows'/'everything' forms — the '{form}' comparison always deep-reads BOTH sides at the diff engine's fixed depth so line sets correspond (narrow with {LeverNames.Records.Fields} instead)."
                    : $"error: project.depth expands field contents and belongs to the 'fields'/'rows'/'everything' forms (got form='{form}').");
            if (dv < 1)
                return Wire.Refuse(json, $"error: project.depth={dv} — depth must be >= 1 (1 shows a container as a collapsed summary; higher opens it).");
            // depth=1 collapses the list to a count, so the rows form would answer with no rows at all.
            if (dv == 1 && form == "rows")
                return Wire.Refuse(json, "error: project.depth=1 collapses a list to a count, and the 'rows' form renders its elements — pass depth >= 2 (2 shows each element's type, the default 4 reaches its sub-fields), or use form='fields' for the collapsed line.");
            // The same reading at the same knob: a [*] path renders elements, which depth 1 collapses away.
            if (dv == 1 && foldPlan?.Folds.FirstOrDefault(f => f is { Fold: PathFold.Set }) is { } setAt)
                return Wire.Refuse(json, $"error: project.depth=1 collapses a list to a count, and '{setAt.Requested}' renders its elements — pass depth >= 2 (2 shows each element's type, the default 4 reaches its sub-fields), or use '{setAt.Root}[*count]' for the number of them.");
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
            // The group_by=type pre-check lives with the source probe below, not here: it is an in-order-lane rule,
            // and the lane is not known until the pole is probed.
        }
        if (project is { resolve_names: true } && form is not ("fields" or "rows" or "everything"))
            return Wire.Refuse(json, $"error: project.resolve_names annotates field values and belongs to the 'fields'/'rows'/'everything' forms (got form='{form}').");
        // The rows form's default depth is its own: at depth 1 every element renders as a bare arm type.
        bool foldsElements = foldPlan?.RendersElements ?? false;
        int depth = project?.depth is { } d && d > 0 ? d : (form == "rows" || foldsElements ? RowProjection.DefaultDepth : 1);
        // A [*] sub-path is the token's own depth requirement; CallerDepth is what an unquantified column beside a
        // quantified one renders at, so a sibling does not expand deeper for the token's sake.
        if (foldPlan is not null)
        {
            depth = Math.Max(depth, foldPlan.Depth);
            foldPlan = foldPlan with { Depth = depth, CallerDepth = project?.depth is { } cd && cd > 0 ? cd : 1 };
        }
        var projFields = bodyFields || comparisonForm ? project?.fields : null;
        // The read runs the LIST path a quantifier binds to and the fold puts the caller's spelling back; each path
        // is read once, at the depth that path's column needs.
        string[]? foldReadPaths = null; int[]? readDepths = null;
        if (foldPlan is not null) (foldReadPaths, readDepths) = foldPlan.Read();
        // Paths whose every column is a [*count] carry no list line, so they take the child union's index-only tier.
        var countFields = foldPlan?.CountOnlyPaths;
        var readPaths = foldPlan is null ? projFields : foldReadPaths;
        bool resolveNames = project?.resolve_names ?? false;
        // The lever vocabulary is a function of (tool, FORM); docs/architecture/records-tool-front.md.
        var formLevers = form == "everything" ? LeverNames.Records.WithoutFieldSelector() : LeverNames.Records;
        // The rows form IS the fields form plus this fold, applied wherever a lane produces bodies, so the render,
        // the artifact and the json document see the same folded rows; a quantified path rides the same seam.
        IReadOnlyList<ReadOutcome> FoldRows(IReadOnlyList<ReadOutcome> read)
            => form == "rows" ? RowProjection.Apply(read, projFields!, depth)
             : foldPlan is not null ? foldPlan.Apply(read)
             : read;

        // ---- SOURCE: the pole grammar (source = the subject; versus = the comparison reference) ----
        // The info_order fold takes ONE file: a list of one is unwrapped, and two or more are refused, since only
        // MO2 decides the order between them.
        var sourceEl = source;
        if (form == "info_order" && sourceEl is { ValueKind: JsonValueKind.Array } srcArr)
        {
            if (srcArr.GetArrayLength() == 1) sourceEl = srcArr[0].Clone();
            else
                return Wire.Refuse(json, srcArr.GetArrayLength() == 0
                    ? "error: source= is an empty list — name the ONE off-order file to fold into the merge (e.g. source=\"MyPatch.esp\"), or drop source= for the live order."
                    : $"error: the info_order form folds ONE off-order file into the merge, where MO2 would load that file, and source= names {srcArr.GetArrayLength()} — two files have no order between them until MO2 sorts them, so there is no one merge to project. Fold one file per call.");
        }
        // ParsePole has no transport in scope, so its refusals take their shape here.
        if (ParsePole(sourceEl, "source", subjectRole: true, out var srcSpec) is { } sperr) return Wire.Refuse(json, sperr);
        srcSpec ??= RecordReads.PoleSpec.Winner;
        if (ParsePole(versus, "versus", subjectRole: false, out var versusSpec) is { } vperr) return Wire.Refuse(json, vperr);

        // versus= belongs to the comparison forms, and the delta form requires it — a delta has two poles.
        if (versusSpec is not null && !comparisonForm)
            return Wire.Refuse(json, $"error: versus= is the comparison REFERENCE pole and belongs to the 'delta'/'tree' forms (got form='{form}') — set project.form='delta' (subject vs reference) or 'tree' (every provider vs reference), or drop versus=.");
        if (form == "delta" && versusSpec is null)
            return Wire.Refuse(json, "error: the 'delta' form compares the subject (source=, default the winner) against a REFERENCE — pass versus= (\"winner\" | a plugin filename | \"previous_provider\" | {\"overlay\": …}).");
        if (form == "tree")
        {
            versusSpec ??= RecordReads.PoleSpec.Winner;
            if (versusSpec.Kind == RecordReads.PoleKind.PreviousProvider)
                return Wire.Refuse(json, "error: versus='previous_provider' is subject-relative and pairs with the 'delta' form (one subject, one reference below it) — a tree diffs EVERY provider against ONE reference pole. Use form='delta', or a named/winner versus= on the tree.");
        }
        // ---- walk= (the traversal construct) ----
        string walkDirection = "forward";
        int walkDepth = 16, walkMaxNodes = 2000;
        bool walkDepthAsked = false;
        // The reverse carrier walk's follow, one constant read by the validation, the lane and every remedy sentence.
        const string CarrierFollow = "Effects[].BaseEffect";
        var walkExclusions = new List<(string Match, bool Refuse)>();
        if (walk is not null)
        {
            var dir = walk.direction?.Trim().ToLowerInvariant();
            if (dir is not (null or "" or "forward" or "reverse"))
                return Wire.Refuse(json, $"error: walk.direction='{walk.direction}' — use 'forward' (what the seeds point at) or 'reverse' (what points at them, at any depth; walk.follow picks which reverse edges: \"*\" for every link, \"{CarrierFollow}\" for the typed MGEF carriers).");
            if (!string.IsNullOrEmpty(dir)) walkDirection = dir!;
            if (walk.depth is { } wd)
            {
                if (wd < 1) return Wire.Refuse(json, $"error: walk.depth={wd} — depth must be >= 1 (hops from the seed).");
                walkDepth = wd;
                walkDepthAsked = true;
            }
            if (walk.max_nodes is { } wn)
            {
                if (wn < 1) return Wire.Refuse(json, $"error: walk.max_nodes={wn} — the node budget must be >= 1.");
                // The bound is on the PER-SEED reading of this budget; docs/architecture/records-tool-front.md.
                bool perSeedBudget = walkDirection == "forward"
                                  || string.Equals(walk.follow?.Trim(), CarrierFollow, StringComparison.OrdinalIgnoreCase);
                if (perSeedBudget && wn > RecordsWalk.Ceiling)
                    return Wire.Refuse(json, $"error: walk.max_nodes={wn} — the node budget's hard upper bound is {RecordsWalk.Ceiling}; pass that or less, which is above the whole closure a single seed reaches on a large order.");
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
                // seed_paths and exclusions shape a FORWARD expansion; follow names which edges are crossed, so it
                // stays legal here and is what tells the two reverse walks apart.
                if (walk.seed_paths is { Length: > 0 } || walk.exclusions is { Length: > 0 })
                    return Wire.Refuse(json, "error: walk.seed_paths/exclusions shape a FORWARD expansion — a reverse walk scans TOWARD the seeds. Drop them (walk.follow stays: it names the edges this walk crosses).");
                var revFollow = walk.follow?.Trim();
                if (!string.IsNullOrEmpty(revFollow) && revFollow != "*"
                    && !string.Equals(revFollow, CarrierFollow, StringComparison.OrdinalIgnoreCase))
                    return Wire.Refuse(json, $"error: walk.follow='{walk.follow}' on a reverse walk — the reverse edges come from the reverse-reference index, which holds every link and no per-path breakdown, so the two follows it can serve are \"*\" (every link, the transitive walk) and \"{CarrierFollow}\" (the typed MGEF carriers). To narrow the transitive walk's reach by path, walk with to_file= and re-enter the artifact on a where= over the field.");
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
            return Wire.Refuse(json, $"error: the 'chain' form renders a walk's paths — pass walk= (e.g. walk={{\"follow\": \"Template\"}} over NPC seeds; reverse MGEF carriers: walk={{\"direction\": \"reverse\", \"follow\": \"{CarrierFollow}\"}} with MGEF formids=).");

        // The single-pole lanes below drive off these fields; richer specs dispatch before reaching them.
        string? srcName = srcSpec.Kind == RecordReads.PoleKind.Named ? srcSpec.Plugin : null;
        string? srcMod = srcSpec.Kind == RecordReads.PoleKind.Named ? srcSpec.Mod : null;
        bool srcOverlay = srcSpec.Kind == RecordReads.PoleKind.Overlay;

        // ---- fields_source (display pole) ----
        // The value first, then the lane rules: 'scoped'/'scanned' are no-op defaults accepted everywhere, only
        // 'winner' is refused on lanes that cannot honor it, and an unknown value is refused by value.
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
        // formids= composes with the scan terms: the identity set intersects the scan, or is the universe when it
        // is the only bound; the reverse MGEF walk keeps its own lane, where formids are seeds.
        bool reverseWalk = walk is not null && walkDirection == "reverse";
        // walk.follow tells the two reverse walks apart and the FORM only picks the view; under 'chain' the follow
        // is said outright. Contract in docs/architecture/records-tool-front.md.
        bool followAsked = !string.IsNullOrWhiteSpace(walk?.follow);
        bool reverseCarrier = reverseWalk
            && string.Equals(walk!.follow?.Trim(), CarrierFollow, StringComparison.OrdinalIgnoreCase);
        if (reverseWalk && form == "chain" && !followAsked)
            return Wire.Refuse(json, $"error: no default follow implies a walk, so a reverse 'chain' call names the edges it crosses — walk.follow=\"{CarrierFollow}\" is the typed MGEF carrier walk, which chain renders, and walk.follow=\"*\" is every link, which chain cannot render because the transitive walk expands one shared frontier and has no per-seed path to draw (read that one with summary/fields/rows/everything/aggregate).");
        if (reverseWalk && form == "chain" && !reverseCarrier)
            return Wire.Refuse(json, $"error: the 'chain' form renders a walk's own per-seed paths, and the transitive reverse walk (follow=\"*\") expands ONE shared frontier rather than a path per seed, so it has none to draw — read it with a reading form (summary/fields/rows/everything/aggregate) over the reached set, or ask for the typed MGEF carrier chain with walk.follow=\"{CarrierFollow}\".");
        if (reverseWalk && (plugins?.names is { Length: > 0 } || conflicts_only || where is { Length: > 0 }))
            return Wire.Refuse(json, "error: a reverse walk takes formids= (its seeds) and nothing else as a scope — the bounded reverse over other scan terms is the references= spelling.");
        if (reverseWalk && !reverseCarrier && types is { Length: > 0 })
            return Wire.Refuse(json, $"error: types= narrows the CARRIER types of the typed MGEF walk (walk.follow=\"{CarrierFollow}\"), and this reverse walk follows every link and reaches records of every type — walk with to_file= and re-enter the artifact via formids=[\"@<file>\"] with types= to keep only the types you want.");
        if (reverseWalk && !hasFormids)
            return Wire.Refuse(json, reverseCarrier
                ? "error: the reverse walk needs its seeds — pass formids= (the MGEF(s) whose carriers to trace)."
                : "error: the reverse walk needs its seeds — pass formids= (the record(s) whose referrers to trace).");
        // The lane, decided once and read by the dispatch below and by every remedy sentence that depends on it.
        bool scanLane = hasScan && !reverseWalk;
        // dense is positional cells 1:1 with the requested field paths, so a form with no fixed column set refuses by name.
        if (dense && form == "everything")
            return Wire.Refuse(json, "error: format='dense' renders positional columnar cells 1:1 with requested field paths, and the 'everything' form has no fixed column set — use format='text' or 'json', or name the paths via form='fields'.");
        if (dense && form == "rows")
            return Wire.Refuse(json, "error: format='dense' renders positional columnar cells 1:1 with requested field paths, and the 'rows' form folds a list's elements into one variable-length line each — use format='text' or 'json'.");
        if (dense && form == "aggregate")
            return Wire.Refuse(json, "error: format='dense' is the per-row columnar transport, and the 'aggregate' form is a count table — its json render IS the compact form; use format='json'.");
        // The same rule at the depth knob, on the scan lane only: the list lane refuses dense outright below, and
        // firing this first would send the caller to fix depth and then hit that.
        if (scanLane && dense && project?.depth is { } denseDepth && denseDepth > 1)
            return Wire.Refuse(json, $"error: format='dense' renders positional columnar cells 1:1 with the requested {LeverNames.Records.Fields} paths, and project.depth={denseDepth} emits extra sub-paths that have no column — use format='text' or 'json' for depth expansion, or drop project.depth for the dense summary cells.");
        if (dense && comparisonForm)
            return Wire.Refuse(json, $"error: format='dense' renders positional columnar cells 1:1 with requested field paths, and the '{form}' form's rows are variable-length delta lists with no fixed column set — use format='text' or 'json'.");
        if (dense && form == "info_order")
            return Wire.Refuse(json, "error: format='dense' renders positional columnar cells 1:1 with requested field paths, and the 'info_order' form is an ordered sequence render with no fixed column set — use format='text' or 'json'.");
        // info_order takes exactly ONE thing on source=, an OFF-ORDER file; the merge IS the answer, so no other
        // pole has anything to pick.
        if (form == "info_order" && srcSpec.Kind is RecordReads.PoleKind.Overlay or RecordReads.PoleKind.PreviousProvider)
            return Wire.Refuse(json, "error: the info_order form merges EVERY plugin touching each topic — that merge is the answer, so a runtime-overlay or previous_provider pole has no seat here (each line already names the plugin that placed it). The one source= this form takes is an OFF-ORDER plugin filename, folded into the merge where MO2 would load it.");
        // fields_source= is the scan lane's display pole; the list lane's read IS its display, so it refuses by name.
        if (winnerFields && formids is { Length: > 0 } && !hasScan)
            return Wire.Refuse(json, "error: fields_source= is the scan lane's display pole — on a formids= read the version you want IS the source: name it via source= (source=\"winner\" is the default).");

        if (offset < 0) return Wire.Refuse(json, $"error: offset={offset} — offset must be >= 0.");
        if (offset > 0 && form == "aggregate")
            return Wire.Refuse(json, "error: " + ReadSentences.NoOffsetOnCountTable("the aggregate form"));
        // The census covers the whole selection and renders no rows at all on this tool, so there is neither a
        // window for offset= to move nor a table for limit= to cap — the sentence drops its limit= clause here.
        if (offset > 0 && counts_only)
            return Wire.Refuse(json, "error: " + ReadSentences.NoOffsetOnCountTable("counts_only=", hasTable: false));
        var toFile = to_file?.Trim();
        bool wantFile = !string.IsNullOrEmpty(toFile);
        if (wantFile)
        {
            if (Artifacts.ValidateToFile(toFile!, svc.ResultsDir) is { } verr) return Wire.Refuse(json, verr);
            if (offset > 0) return Wire.Refuse(json, "error: to_file= captures the COMPLETE result (the artifact is never a window), so offset= has nothing to page — drop offset=.");
            if (form == "aggregate") return Wire.Refuse(json, "error: to_file= writes row artifacts, and the aggregate form is a count table with no record rows — drop one of the two.");
            if (counts_only) return Wire.Refuse(json, Artifacts.CountsOnlyWithToFile);
        }
        if (where_source is not null && where is not { Length: > 0 })
            return Wire.Refuse(json, "error: where_source= retargets the where= predicates and needs where= — add predicates, or drop where_source=.");

        // ---- info_order: the off-order fold ------------------------------------------------------------
        // The fold's file is resolved through the same one-pole probe every other source= goes through, and an
        // ACTIVE plugin is refused; contract in docs/architecture/records-tool-front.md.
        RecordReads.PoleInfo? ioFold = null;
        if (form == "info_order" && srcSpec.Kind == RecordReads.PoleKind.Named)
        {
            var probe = svc.ProbeSourceArm(srcSpec.Plugin!, srcSpec.Mod, out var foldProbeErr);
            if (foldProbeErr is not null) return Wire.Refuse(json, "error: " + foldProbeErr);
            if (probe!.InOrder)
                return Wire.Refuse(json, $"error: source='{probe.Plugin}' is ACTIVE in the load order, and the info_order form already merges every active plugin that touches each topic — its lines are in the answer, labelled with its name. Drop source= for the live order; source= here folds an OFF-ORDER file (one not enabled in MO2) into that merge.", probe.Stamp);
            ioFold = probe;
            // The fold is not a "whose version" pole, so the lanes below must not read it as one.
            srcName = null; srcMod = null;
        }
        // The probe knows the name, the row label and where the copy is; the batch that opens the file adds the
        // placement. Filled here so a statement written before the merge names the same file the rows will.
        RecordReads.FoldFacts? ioFoldFacts = null;
        if (ioFold is not null) { ioFoldFacts = new RecordReads.FoldFacts(); ioFoldFacts.FromArm(ioFold); }
        // The probe decided OFF-ORDER against its own build and the merge reads another, so the two are
        // epoch-compared like every two-capture lane here; docs/architecture/records-tool-front.md.
        string? FoldSeam(OrderStamp? mergeEpoch)
            => ioFold?.Epoch is { } probeEpoch && mergeEpoch is not null && mergeEpoch.Epoch != probeEpoch
                ? $"error: the load order changed between resolving '{ioFoldFacts!.Label}' as off-order (epoch={probeEpoch}) and reading the merge (epoch={mergeEpoch.Epoch}) — that copy may now be IN the order, and the fold would describe a different world. Retry the call."
                : null;
        // The fold's statement rides the artifact echo too, where a `source` would mean the rows were read from
        // that plugin — which a projection is not.
        void FoldEcho(List<KeyValuePair<string, string>> e)
        {
            var f = ioFoldFacts!;
            e.Add(new("folded", f.Label));
            e.Add(new("projection", $"{f.Where}; {f.Placement} — the rows below are a PROJECTION of the order with that file enabled, not the live order."));
        }

        // ---- the response envelope (form + resolved source arm) -----------------------------------------
        // Text renders get it as a header line; json renders carry the same pairs as top-level fields.
        var envelope = new List<KeyValuePair<string, string>> { new("form", form) };
        string headerLine = $"records  form={form}";
        void Arm(string statement)
        {
            // One source statement per response, first call wins; docs/architecture/records-tool-front.md.
            if (envelope.Any(kv => kv.Key == "source")) return;
            envelope.Add(new("source", statement));
            headerLine += $"  source={statement}";
        }
        // A census is a text render too, so it is held to the same ceiling and says so when max_chars is smaller
        // than the statements it carries whatever the budget.
        string Census(string body) => RenderCap.Settle(body, max_chars > 0 ? max_chars : Wire.DefaultMaxChars);
        // Every warning the SkyPatcher replay produced, named beside the answer with its own file and line.
        var overlayWarnings = new HousecarlCore.SkyPatcherOverlay.WarningSink();
        void StateOverlayWarnings()
        {
            var shown = overlayWarnings.Kept;
            if (shown.Count == 0) return;
            int over = overlayWarnings.Overflow;
            envelope.Add(new("skypatcher_warnings",
                             string.Join(" | ", shown) + (over > 0 ? $" (+{over} more not listed)" : "")));
            foreach (var w in shown) headerLine += "\n[!] skypatcher: " + w;
            if (over > 0) headerLine += $"\n[!] skypatcher: {over} further warning(s) not listed.";
        }
        // The seam between a deriving step's capture and the read's; docs/architecture/records-tool-front.md.
        string? expectEpoch = null;
        string? SeamTear(OrderStamp? epoch) =>
            expectEpoch is not null && epoch is not null && epoch.Epoch != expectEpoch
                ? $"the load order changed between deriving the selection (epoch={expectEpoch}) and reading it (epoch={epoch.Epoch}) — the two halves would mix builds. Retry the call."
                : null;

        // limit=/offset= window the list lane's RENDER only, and the window note rides the header and envelope;
        // docs/architecture/records-tool-front.md.
        int lim = limit <= 0 ? DefaultLimit : limit;
        // Set when a comparison form's KEYS were windowed before the rows were read (see ComparisonWindow); the
        // note rides along so the counts and any spilled artifact can say what they cover.
        bool cmpPrewindowed = false;
        string? cmpWindowNote = null;
        int cmpSelected = 0;
        IReadOnlyList<T> Windowed<T>(IReadOnlyList<T> rows)
        {
            if (cmpPrewindowed) return rows;
            // Under to_file= the rows are the file and the render is manifest-only, so no window applies.
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

        // The comparison forms' window, applied to the KEYS before any body is read; a census and a to_file=
        // artifact cover the complete selection. Contract in docs/architecture/records-tool-front.md.
        List<string> ComparisonWindow(IReadOnlyList<FormKey> keys, int total)
        {
            if (wantFile || counts_only) return keys.Select(k => k.ToString()).ToList();
            // The query already skipped offset=, so the keys ARRIVE at the window's start and the limit is the
            // whole window.
            cmpPrewindowed = true;                                     // these keys ARE the rendered rows now
            cmpSelected = total;
            var w = keys.Take(lim).Select(k => k.ToString()).ToList();
            if (offset == 0 && w.Count == total) return w;             // the window is the whole selection: no note
            cmpWindowNote = w.Count == 0
                ? $"window: no rows — offset={offset} is past the end of the {total} selected; nothing was read"
                : $"window: rows {offset + 1}–{offset + w.Count} of {total} (limit={lim}, offset={offset}) — only these rows were read";
            envelope.Add(new("window", cmpWindowNote));
            headerLine += "\n" + cmpWindowNote;
            return w;
        }

        /// <summary>Which lever the comparison bound's refusal names, for the lane the call is actually on; the scan lever reads the same whether or not a limit= was passed, since the move is a limit= at or below the bound either way.</summary>
        string ComparisonLever() =>
            wantFile || counts_only ? RenderBudget.ComparisonWholeSelectionLever
            : RenderBudget.ComparisonScanLever;

        // A walk hands its reached set to the list lane as formids=, already bounded by walk.max_nodes, so the list
        // lane's own render bound is over a list the CALLER passed.
        bool walkDerived = false;

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
                var ioRows = svc.InfoOrderBatch(ids, demand, out var ioRefusal, out var ioEpoch, ioFold, ioFoldFacts);
                if (ioRefusal is not null)
                    return json ? JsonWire.RenderError(ioRefusal, ioEpoch) : "error: " + ioRefusal + Wire.EpochLine(ioEpoch);
                if (FoldSeam(ioEpoch) is { } ioTear) return Wire.Refuse(json, ioTear, ioEpoch);
                var e = new List<KeyValuePair<string, string>>
                {
                    new("formids", echoSrc ?? $"{ids.Length} inline formid(s)"),
                    new("form", form),
                };
                if (ioFoldFacts is not null) FoldEcho(e);
                return InfoOrderResponse(ioRows, ioEpoch, e);
            }

            // ---- identity form: the labeling lane. Winner frame by contract. ----
            if (form == "identity")
            {
                if (srcName is not null || srcOverlay)
                    return Wire.Refuse(json, "error: the identity form is the load-order labeling frame (type/editorid/name/WINNER per FormID) — " +
                           "it does not take a source= pole. Use form='summary' or 'fields' for a named version's view.");
                // This form reads a body by the dearest route on the tool, an untyped whole-plugin seek per id, so
                // it has its own tier, checked before the read.
                if (RenderBudget.RefuseIdentity(svc.Bounds, ids.Length, counts_only ? RenderBudget.ListCensusRemedy : RenderBudget.ListRemedy,
                                                counts_only) is { } identityTooBig)
                    return Wire.Refuse(json, identityTooBig);
                var identityClock = System.Diagnostics.Stopwatch.StartNew();
                var rows = svc.ResolveRefs(ids, demand, out var epoch, out var refusal);
                identityClock.Stop();
                // The count is the bodies READ, not the list's length; docs/architecture/records-tool-front.md.
                var identityCost = (rows.Count(r => r.Resolved), identityClock.ElapsedMilliseconds);
                if (refusal is not null)
                    return json ? JsonWire.RenderError(refusal, epoch) : "error: " + refusal + Wire.EpochLine(epoch);
                Arm("winner");
                if (counts_only)
                {
                    // The census honors counts_only on every list form, this one included.
                    int okI = rows.Count(r => r.Error is null);
                    return json ? JsonWire.RenderCounts(envelope, rows.Count, okI, rows.Count - okI, epoch, max_chars)
                                : Census($"{headerLine}\ncount={rows.Count} ok={okI} errors={rows.Count - okI}" + Wire.EpochLine(epoch));
                }
                var winRows = Windowed(rows);
                SpillState? spill = null;
                if (wantFile)
                {
                    var (s, aerr) = Artifacts.WriteResolve(rows, epoch.Epoch, ArtifactTarget.Named(toFile!), "to_file", Echo());
                    if (aerr is not null) return json ? JsonWire.RenderError(aerr, epoch) : "error: " + aerr;
                    spill = SpillState.Spilled(s!, manifestOnly: true);
                }
                string Render(SpillState? sp, out bool trunc) => json
                    ? JsonWire.RenderResolve(winRows, max_chars, epoch, sp, out trunc, envelope, identityCost)
                    : Wire.RenderResolve(winRows, max_chars, epoch, sp, out trunc, headerLine, identityCost);
                var rendered = Render(spill, out var truncated);
                if (spill is null && truncated)
                {
                    using var reservation = ResultsStore.Reserve(svc.ResultsDir, ToolNames.Records, epoch.Epoch);
                    var (s, aerr) = Artifacts.WriteResolve(rows, epoch.Epoch, reservation, "ceiling", Echo());
                    rendered = Render(aerr is null ? SpillState.Spilled(s!, manifestOnly: false) : SpillState.WriteFailed(aerr), out _);
                }
                return rendered;
            }

            // ---- the render's own bound on this lane, over all five reading forms, checked before any body is
            // read; counts_only pays it too, because this lane reads the list whatever it renders. ----
            if (bodyForm && !walkDerived
                && RenderBudget.Refuse(svc.Bounds, ids.Length, form == "everything",
                                       counts_only ? RenderBudget.ListCensusRemedy : RenderBudget.ListRemedy,
                                       counts_only) is { } listTooBig)
                return Wire.Refuse(json, listTooBig);

            // ---- summary / fields / everything / aggregate: batch bodies off the source pole. Summary reads one
            //      cheap leaf, fields the named paths, everything a null that dumps the modeled fields. ----
            IReadOnlyList<string>? readFields = form switch
            {
                "fields" or "rows" => readPaths,
                "summary" or "aggregate" => new[] { "EditorID" },   // cheapest leaf — headers carry the summary facts
                _ => null,                                          // everything — the full dump
            };
            // The per-path depths belong to the paths they were computed for; every other form reads at one depth.
            var readFieldDepths = ReferenceEquals(readFields, readPaths) ? readDepths : null;
            var readFieldCounts = ReferenceEquals(readFields, readPaths) ? countFields : null;
            IReadOnlyList<ReadOutcome> outcomes;
            RecordReads.PoleInfo? pole = null;
            // Clocked like the scan's body lane, over the BODIES READ; docs/architecture/records-tool-front.md.
            var listClock = System.Diagnostics.Stopwatch.StartNew();
            if (srcOverlay && !string.Equals(srcSpec.OverlayState ?? "post", "pre", StringComparison.OrdinalIgnoreCase))
            {
                // The overlay post source: every winner replayed through the SkyPatcher INI layer, read at the
                // caller's own depth.
                outcomes = svc.OverlayPostBatch(ids, readFields, depth, resolveNames, demand, out var ovRefusal, out var ovEpoch, out _,
                                                LeverNames.Records.ContainerHint, readFieldDepths, ct,
                                                draft: srcSpec.Draft, overlayWarnings: overlayWarnings);
                if (ovRefusal is not null)
                    return json ? JsonWire.RenderError(ovRefusal, ovEpoch)
                                : "error: " + ovRefusal + Wire.EpochLine(ovEpoch);
                Arm("skypatcher overlay (post) — the winner after the SkyPatcher INI layer replays"
                    + (srcSpec.Draft is null ? "" : $", with {srcSpec.Draft.Arm}"));
                envelope.Add(new("epoch_covers_source", "false"));
                headerLine += "\n(the SkyPatcher INI layer's files are OUTSIDE the epoch fingerprint — an INI edit changes answers " +
                              "without changing the epoch; a record whose type SkyPatcher cannot patch reads as its plain winner)";
                StateOverlayWarnings();
            }
            else if (srcName is null)
            {
                if (srcOverlay) Arm("skypatcher overlay (pre) = winner — the body the INI layer starts from");
                outcomes = svc.ResolveBatch(ids, readFields, false, depth, resolveNames, null, demand, out var refusal, out var refusalEpoch, LeverNames.Records.ContainerHint, readFieldDepths, ct, countFields: readFieldCounts);
                if (refusal is not null)
                    return json ? JsonWire.RenderError(refusal, refusalEpoch)
                                : "error: " + refusal + Wire.EpochLine(refusalEpoch);
                if (!srcOverlay) Arm("winner");
            }
            else
            {
                outcomes = svc.ResolveBatchFromPole(ids, srcName, srcMod, readFields, depth, resolveNames, demand,
                                                    out pole, out var refusal, out var refusalEpoch,
                                                    LeverNames.Records.ContainerHint, readFieldDepths, ct, countFields: readFieldCounts);
                if (refusal is not null)
                    return json ? JsonWire.RenderError(refusal, refusalEpoch)
                                : "error: " + refusal + Wire.EpochLine(refusalEpoch);
                Arm($"{pole!.Plugin} — {pole.Where}");
                if (!pole.EpochCoversPole)
                {
                    envelope.Add(new("epoch_covers_source", "false"));
                    headerLine += "\n(the off-order file's content is OUTSIDE the epoch fingerprint — an edit to it changes answers without changing the epoch)";
                }
            }
            listClock.Stop();
            // One cost for every form this lane renders, counted over the bodies actually READ.
            var listCost = (outcomes.Count(o => o.Record is not null), listClock.ElapsedMilliseconds);
            outcomes = FoldRows(outcomes);
            var epoch2 = outcomes.FirstOrDefault(o => o.Stamp is not null)?.Stamp;
            if (SeamTear(epoch2) is { } seamTear)
                return json ? JsonWire.RenderError(seamTear, epoch2) : "error: " + seamTear;

            if (form == "aggregate")
                return RenderListAggregate(outcomes, project!.group_by!, json, dense, epoch2, headerLine, envelope, listCost,
                                           max_chars, svc.Types.DisplayNames(types), TableRowLimit(limit));

            if (counts_only)
            {
                int ok = outcomes.Count(o => o.Error is null), err = outcomes.Count - outcomes.Count(o => o.Error is null);
                return json
                    ? JsonWire.RenderCounts(envelope, outcomes.Count, ok, err, epoch2, max_chars)
                    : Census($"{headerLine}\ncount={outcomes.Count} ok={ok} errors={err}" + Wire.EpochLine(epoch2));
            }

            var winOutcomes = Windowed(outcomes);   // render window; census/aggregate/artifacts stay complete
            SpillState? spill2 = null;
            if (wantFile)
            {
                var (s, aerr) = Artifacts.WriteBatch(outcomes, ArtifactTarget.Named(toFile!), "to_file", Echo(), formLevers);
                if (aerr is not null) return json ? JsonWire.RenderError(aerr, epoch2) : "error: " + aerr;
                spill2 = SpillState.Spilled(s!, manifestOnly: true);
            }
            string Render2(SpillState? sp, out bool trunc) => form == "summary"
                ? RenderRecordsSummary(winOutcomes, json, headerLine, envelope, max_chars, sp, listCost, out trunc)
                : json ? JsonWire.RenderBatch(winOutcomes, max_chars, sp, out trunc, envelope, formLevers, listCost)
                       : Wire.RenderBatch(winOutcomes, max_chars, sp, out trunc, formLevers, listCost, headerLine);
            var rendered2 = Render2(spill2, out var truncated2);
            if (spill2 is null && truncated2)
            {
                using var reservation = ResultsStore.Reserve(svc.ResultsDir, ToolNames.Records, epoch2?.Epoch ?? "none");
                var (s, aerr) = Artifacts.WriteBatch(outcomes, reservation, "ceiling", Echo(), formLevers);
                rendered2 = Render2(aerr is null ? SpillState.Spilled(s!, manifestOnly: false) : SpillState.WriteFailed(aerr), out _);
            }
            return rendered2;
        }

        // ================================================================================================
        //  WALK lane — forward walks expand the winner link graph; reverse splits on walk.follow, not on the form,
        //  and each walk hands its reached set to the same two views.
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

            if (reverseWalk && !reverseCarrier)
            {
                // The transitive reverse walk: every link, at every hop, off the reverse-reference index; the
                // per-hop census is the only thing this lane renders of its own.
                var rev = ReverseWalkBatch.Run(svc, ids, walkDepth, walkMaxNodes, demand, ct);
                if (rev.Refusal is not null)
                    return json ? JsonWire.RenderError(rev.Refusal, rev.Stamp) : "error: " + rev.Refusal + Wire.EpochLine(rev.Stamp);
                if (SeamTear(rev.Stamp) is { } rTear)
                    return json ? JsonWire.RenderError(rTear, rev.Stamp) : "error: " + rTear;
                // Every hop is named with its count, empty ones included, and a hop the budget ended says 'cut'.
                var hopLine = string.Join(", ", rev.Hops.Select(h => $"hop {h.Depth}: {h.Reached.Count}{(h.Cut ? " (cut by walk.max_nodes)" : "")}"));
                if (!rev.Capped && rev.Hops.Count < walkDepth)
                    hopLine += $" (nothing left to expand, so hops {rev.Hops.Count + 1}–{walkDepth} were not walked)";
                // The other way a walk ends: walk.depth reached with the last hop still finding records, so what it
                // reached was recorded and not expanded.
                else if (!rev.Capped && rev.Hops.Count > 0 && rev.Hops[^1].Reached.Count > 0)
                    hopLine += $" (walk.depth={walkDepth} was reached with hop {walkDepth} still finding records, so they were recorded and not expanded — raise walk.depth to walk further)";
                int reachedRev = rev.Selection.Count - rev.Seeds;
                envelope.Add(new("walk", $"reverse (every link) depth={walkDepth} — {hopLine}; selection = the {rev.Selection.Count} record(s) the walk reached (seeds included)"));
                if (rev.IndexNote is not null) envelope.Add(new("reverse_index", rev.IndexNote));
                headerLine += $"\nwalk=reverse (every link) depth={walkDepth}: {hopLine} — selection = {rev.Selection.Count} record(s) ({reachedRev} referrer(s), seeds included)";
                if (rev.IndexNote is not null) headerLine += "\n" + rev.IndexNote;
                if (rev.Dropped.Total > 0)
                {
                    // Each cause is named with its own count: an unreadable winner is a coverage gap, a dropped
                    // link a verdict.
                    var causes = new List<string>(4);
                    if (rev.Dropped.NoLink > 0) causes.Add($"{rev.Dropped.NoLink} whose winner does not carry the link");
                    // The plugin is named when the gather knows it, so the coverage gap says which file to close.
                    if (rev.Dropped.Unreadable > 0)
                        causes.Add($"{rev.Dropped.Unreadable} whose winning plugin could not be read — a coverage gap, not a verdict"
                                   + (rev.UnreadableWinners is { Count: > 0 } up ? $" ({string.Join(", ", up)})" : ""));
                    if (rev.Dropped.NoLiveBody > 0) causes.Add($"{rev.Dropped.NoLiveBody} whose winner is deleted or carries no links");
                    if (rev.Dropped.NoWinner > 0) causes.Add($"{rev.Dropped.NoWinner} with no resolvable winner");
                    headerLine += $"\n{rev.Dropped.Total} index candidate(s) were dropped — the index names a plugin copy that carries the link, and this walk judges the load-order winner (the same second step references= takes): {string.Join("; ", causes)}. None of them was reached or expanded.";
                }
                // Records this walk's body check could only read leniently, naming WHICH records it counts, because
                // the index's own line above says the same words about a different universe.
                if (rev.LenientRecords is { Count: > 0 } lenientRev)
                    headerLine += $"\nof the candidates this walk judged, {lenientRev.Count} winner record(s) were read leniently — part of their "
                                + "content is encoded in a way Mutagen refuses, so the walk judged them on what houseCARL could still decode: "
                                + string.Join("; ", lenientRev.Take(3))
                                + (lenientRev.Count > 3 ? $"; and {lenientRev.Count - 3} more" : "")
                                + $". Read one with {ToolNames.Records} formids=[the FormID] to see the marked row.";
                if (rev.Capped)
                    headerLine += $"\n[!] the walk.max_nodes budget ({walkMaxNodes}, one budget shared across every seed and hop on this lane) was reached — what is listed IS reached and proved, and the hop it cut is marked; raise walk.max_nodes to walk further.";
                // The reached set's render bound, with its own remedy because chain and the scan terms are both
                // refused on this walk; counts_only pays it too, since the list lane reads before it counts.
                if (bodyForm
                    && RenderBudget.Refuse(svc.Bounds, rev.Selection.Count, form == "everything",
                                           counts_only ? RenderBudget.ReverseTransitiveCensusRemedy : RenderBudget.ReverseTransitiveRemedy,
                                           counts_only) is { } revTooBig)
                    return Wire.Refuse(json, revTooBig, rev.Stamp);
                expectEpoch = rev.Epoch;
                formids = rev.Selection.ToArray();
                walk = null;
                walkDerived = true;
                return ListLane();
            }

            if (reverseCarrier)
            {
                // The typed MGEF lane: a non-MGEF seed fails loud per item rather than reading as '0 carriers'.
                var results = new List<(string Seed, EffectChainResult Result)>(ids.Length);
                foreach (var raw in ids)
                {
                    FormKey fk;
                    try { fk = door.Parse(raw); }
                    catch (Exception ex) { results.Add((raw?.Trim() ?? "", EffectChainResult.Fail($"bad FormID '{raw}': {ex.Message}"))); continue; }
                    // The per-seed carrier bound is the walk's own reach budget; limit=/offset= stay the SEED window.
                    results.Add((FormIdToken.Of(fk), svc.ResolveEffectChain(fk, types, walkMaxNodes)));
                }
                // One build for the whole batch, so the per-seed stamps must agree and an @artifact seed list's
                // epoch demand must match it.
                var epochsR = results.Select(r => r.Result.Stamp).Where(e => e is not null).Distinct().ToList();
                if (epochsR.Count > 1)
                {
                    var tear = $"the load order changed while the seeds resolved (epochs {string.Join(", ", epochsR.Select(e => e!.Epoch))}) — " +
                               "the carrier sets would mix builds. Retry the call.";
                    return json ? JsonWire.RenderError(tear, epochsR[^1]) : "error: " + tear;
                }
                var epochR = epochsR.FirstOrDefault();
                if (demand is not null && (epochR is null || demand.Epoch != epochR.Epoch))
                {
                    var dref = epochR is null
                        ? $"artifact '{demand.Path}' carries epoch={demand.Epoch}, but no seed consulted a build to verify it against (every seed failed pre-capture) — fix the seeds and retry."
                        : RecordReads.ArtifactEpochMismatch(demand, epochR.Epoch);
                    return json ? JsonWire.RenderError(dref, epochR) : "error: " + dref + Wire.EpochLine(epochR);
                }
                Arm("winner (carriers are the load-order-effective versions)");
                // The census separates written rows from the true total and names capped seeds, so a walk.max_nodes
                // cut is always declared.
                int carrierRows = results.Sum(r => r.Result.Error is null ? r.Result.Rows.Count : 0);
                int carrierTotal = results.Sum(r => r.Result.Error is null ? r.Result.Total : 0);
                int cappedSeeds = results.Count(r => r.Result.Error is null && r.Result.Capped);
                int seedErrs2 = results.Count(r => r.Result.Error is not null);
                // A carrier is not a magic effect, so hop 2 and beyond reach nothing; said only when a depth was
                // asked for, and the census counts what hop 1 REACHED, not what got printed.
                var carrierHops = $"hop 1: {carrierTotal}";
                if (walkDepthAsked && walkDepth > 1)
                    carrierHops += string.Concat(Enumerable.Range(2, walkDepth - 1).Select(d => $", hop {d}: 0"))
                                 + $" — walk.follow=\"{CarrierFollow}\" crosses the effect link at every hop and a carrier is not a magic effect, so nothing is reached past hop 1; the transitive reverse over every link is the same call with follow=\"*\".";
                envelope.Add(new("walk", $"reverse follow={CarrierFollow} depth={walkDepth} — the typed MGEF carrier walk; {carrierHops}"));
                headerLine += "\nwalk=reverse (per seed: every SPEL/ENCH/ALCH/SCRL/INGR applying it, with the MATCHING entry's magnitude/area/duration — reported AS AUTHORED; conditions are not evaluated, so a row means 'defines it at this strength', not 'it will fire')";
                if (walkDepthAsked && walkDepth > 1) headerLine += $"\n{carrierHops}";
                // A reading form consumes this walk's reached set as it does a forward walk's.
                if (form != "chain")
                {
                    var carrierSel = new List<string>(ids.Length + carrierRows);
                    var carrierSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var r in results)
                    {
                        if (r.Result.Error is not null) continue;
                        if (carrierSeen.Add(r.Seed)) carrierSel.Add(r.Seed);
                        foreach (var row in r.Result.Rows)
                        {
                            var key = FormIdToken.Of(row.Carrier);
                            if (carrierSeen.Add(key)) carrierSel.Add(key);
                        }
                    }
                    if (carrierSel.Count == 0)
                        return json ? JsonWire.RenderError($"the reverse carrier walk reached nothing readable ({seedErrs2} seed error(s) — run form='chain' to see each seed's outcome).", epochR)
                                    : $"error: the reverse carrier walk reached nothing readable ({seedErrs2} seed error(s) — run form='chain' to see each seed's outcome)." + Wire.EpochLine(epochR);
                    // A seed that failed contributed no carriers, and the reading form says so.
                    envelope.Add(new("selection", $"the {carrierSel.Count} record(s) the carrier walk reached (seeds included)"
                                                  + (seedErrs2 > 0 ? $"; {seedErrs2} seed error(s), listed via form='chain'" : "")));
                    headerLine += $"\nwalk: selection = {carrierSel.Count} reached record(s) (seeds included)";
                    if (seedErrs2 > 0)
                        headerLine += $"\n[!] {seedErrs2} seed(s) failed and contributed no carriers — run form='chain' to see each seed's outcome.";
                    if (cappedSeeds > 0)
                        headerLine += $"\n[!] {cappedSeeds} seed(s) hit the walk.max_nodes carrier bound ({walkMaxNodes}, per seed on this lane) — the selection is a prefix of the {carrierTotal} carrier(s) reached; raise walk.max_nodes.";
                    // The reached set's render bound, without walk.depth among its levers because this walk reaches
                    // nothing past hop 1; counts_only pays it too, since the census reads bodies.
                    if (bodyForm
                        && RenderBudget.Refuse(svc.Bounds, carrierSel.Count, form == "everything",
                                               counts_only ? RenderBudget.ReverseCarrierCensusRemedy : RenderBudget.ReverseCarrierRemedy,
                                               counts_only) is { } carrierTooBig)
                        return Wire.Refuse(json, carrierTooBig, epochR);
                    expectEpoch = epochR?.Epoch;
                    formids = carrierSel.ToArray();
                    walk = null;
                    walkDerived = true;
                    return ListLane();
                }
                var revCounts = new[] { KvI("seeds", results.Count), KvI("carrier_rows", carrierRows), KvI("carrier_total", carrierTotal), KvI("capped_seeds", cappedSeeds), KvI("errors", seedErrs2) };
                if (cappedSeeds > 0)
                    headerLine += $"\n[!] {cappedSeeds} seed(s) hit the walk.max_nodes carrier bound ({walkMaxNodes}, per seed on this lane) — their rows are a prefix of carrier_total; raise walk.max_nodes.";
                if (counts_only)
                    return json
                        ? JsonWire.RenderNamedCounts(envelope, revCounts, epochR, max_chars)
                        : Census($"{headerLine}\nseeds={results.Count} carrier_rows={carrierRows} carrier_total={carrierTotal} capped_seeds={cappedSeeds} errors={seedErrs2}" + Wire.EpochLine(epochR));
                var winResults = Windowed(results);
                SpillState? revSpill = null;
                if (wantFile)
                {
                    var (sp, aerr) = Artifacts.WriteEffectChains(results, epochR?.Epoch, ArtifactTarget.Named(toFile!), "to_file", Echo());
                    if (aerr is not null) return json ? JsonWire.RenderError(aerr, epochR) : "error: " + aerr;
                    revSpill = SpillState.Spilled(sp!, manifestOnly: true);
                }
                string RenderRev(SpillState? sp, out bool trunc) => json
                    ? JsonWire.RenderEffectChains(winResults, max_chars, envelope, revCounts, epochR, sp, out trunc)
                    : RenderRecordsEffectChains(winResults, results.Count, carrierRows, carrierTotal, seedErrs2, headerLine, epochR, max_chars, sp, out trunc);
                var revRendered = RenderRev(revSpill, out var revTrunc);
                if (revSpill is null && revTrunc)
                {
                    using var reservation = ResultsStore.Reserve(svc.ResultsDir, ToolNames.Records, epochR?.Epoch ?? "none");
                    var (sp, aerr) = Artifacts.WriteEffectChains(results, epochR?.Epoch, reservation, "ceiling", Echo());
                    revRendered = RenderRev(aerr is null ? SpillState.Spilled(sp!, manifestOnly: false) : SpillState.WriteFailed(aerr), out _);
                }
                return revRendered;
            }

            // Forward: one engine batch, one captured build; chain renders it and every other form consumes the
            // reached set, seeds included, through the normal lanes.
            var rows = svc.WalkForwardBatch(ids, walk!.seed_paths, walk.follow, walkDepth, walkMaxNodes,
                                            walkExclusions, demand, out var wRefusal, out var wEpoch, ct,
                                            wantCycles: form == "chain");
            if (wRefusal is not null)
                return json ? JsonWire.RenderError(wRefusal, wEpoch) : "error: " + wRefusal + Wire.EpochLine(wEpoch);
            if (SeamTear(wEpoch) is { } wTear)
                return json ? JsonWire.RenderError(wTear, wEpoch) : "error: " + wTear;

            if (form == "chain")
            {
                Arm("winner (the walk expands the winner link graph)");
                int reached = rows.Where(r => r.Error is null).Sum(r => r.Nodes.Count(n => !n.Status.StartsWith("no links")));
                int errs = rows.Count(r => r.Error is not null);
                // A cycle is a record the walk reached again from itself, a fact about the walked graph, so
                // counts_only states it too.
                int cycles = rows.Sum(r => r.Cycles.Count);
                // "none means none" holds only over a walk that FINISHED, so the cut is said beside the count on
                // every path that prints one.
                int cutSeeds = rows.Count(r => r.TruncationNote is not null);
                int cappedCycles = rows.Count(r => r.CyclesCapped);
                if (cutSeeds > 0)
                    headerLine += $"\n[!] {cutSeeds} seed(s) stopped at walk.depth ({walkDepth}) or walk.max_nodes ({walkMaxNodes}) — a loop closing past the cut is not visible, so this cycle count is not proof of none; raise the cap to finish the walk.";
                if (cappedCycles > 0)
                    headerLine += $"\n[!] {cappedCycles} seed(s) reached the {RecordReads.WalkCycleCap}-cycle search cap — more loops are there than are counted.";
                envelope.Add(new("walk", $"forward{(walk.follow is { } f2 ? $" follow={f2}" : " (closure)")} depth={walkDepth}"));
                if (counts_only)
                    return json
                        ? JsonWire.RenderNamedCounts(envelope, new[] { KvI("seeds", rows.Count), KvI("reached", reached), KvI("errors", errs), KvI("cycles", cycles), KvI("truncated_seeds", cutSeeds) }, wEpoch, max_chars)
                        : Census($"{headerLine}\nseeds={rows.Count} reached={reached} errors={errs} cycles={cycles}" + Wire.EpochLine(wEpoch));
                // Said only where the seeds ARE listed; a counts_only response's own cycles= is the whole answer.
                if (cycles > 0)
                    headerLine += $"\n{cycles} cycle(s) — a record the walk reached again from itself, one per closing link, listed under its seed. A count is a lower bound on the number of distinct loops; none means none.";
                var winRows = Windowed(rows);
                SpillState? spill = null;
                if (wantFile)
                {
                    var (s, aerr) = Artifacts.WriteChain(rows, wEpoch?.Epoch, ArtifactTarget.Named(toFile!), "to_file", Echo());
                    if (aerr is not null) return json ? JsonWire.RenderError(aerr, wEpoch) : "error: " + aerr;
                    spill = SpillState.Spilled(s!, manifestOnly: true);
                }
                var chainCounts = new[] { KvI("seeds", rows.Count), KvI("reached", reached), KvI("errors", errs), KvI("cycles", cycles) };
                string Render(SpillState? sp, out bool trunc) => json
                    ? JsonWire.RenderChain(winRows, max_chars, wEpoch, envelope, chainCounts, sp, out trunc)
                    : RenderRecordsChain(winRows, rows.Count, reached, errs, headerLine, wEpoch, max_chars, sp, out trunc);
                var rendered = Render(spill, out var truncated);
                if (spill is null && truncated)
                {
                    using var reservation = ResultsStore.Reserve(svc.ResultsDir, ToolNames.Records, wEpoch?.Epoch ?? "none");
                    var (s, aerr) = Artifacts.WriteChain(rows, wEpoch?.Epoch, reservation, "ceiling", Echo());
                    rendered = Render(aerr is null ? SpillState.Spilled(s!, manifestOnly: false) : SpillState.WriteFailed(aerr), out _);
                }
                return rendered;
            }

            // Selection consumption: seeds plus reached, in walk order, deduplicated, then read under source= and
            // seam-checked against the walk's build.
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
                            : $"error: the walk reached nothing readable ({seedErrs} seed error(s) — run form='chain' to see each seed's outcome)." + Wire.EpochLine(wEpoch);

            // ---- the reached set's own render bound, measured on the REACHED count rather than the seed scan's,
            // with its own remedy since the scan window is not what moves a walk. form='chain' returned above and
            // pays no SECOND body read; counts_only pays this, because the list lane reads before it counts. ----
            if (bodyForm
                && RenderBudget.Refuse(svc.Bounds, combined.Count, form == "everything",
                                       counts_only ? RenderBudget.WalkCensusRemedy : RenderBudget.WalkRemedy,
                                       counts_only) is { } walkTooBig)
                return Wire.Refuse(json, walkTooBig, wEpoch);

            envelope.Add(new("walk", $"forward{(walk.follow is { } f3 ? $" follow={f3}" : " (closure)")} depth={walkDepth} — selection = the {combined.Count} record(s) the walk reached (seeds included{(seedErrs > 0 ? $"; {seedErrs} seed error(s), listed via form='chain'" : "")})"));
            headerLine += $"\nwalk: selection = {combined.Count} reached record(s) (seeds included)";
            expectEpoch = wEpoch?.Epoch;
            formids = combined.ToArray();
            walk = null;
            walkDerived = true;
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

            // The same bound as the scan lane's, on the list's own length, since limit= windows only the render here;
            // a walk arrives as a list it derived, so its lever is the walk's. Charged AFTER each form's shape checks.
            string? ListCost() =>
                RenderBudget.RefuseComparison(svc.Bounds, ids.Length, form,
                    walkDerived ? RenderBudget.ComparisonWalkLever : RenderBudget.ComparisonListLever);

            if (form == "delta")
            {
                if (ListCost() is { } deltaTooBig) return Wire.Refuse(json, deltaTooBig);
                var rows = svc.DeltaBatch(ids, srcSpec, versusSpec!, projFields, demand,
                                          out var sArm, out var rArm, out var covers, out var refusal, out var epoch,
                                          overlayWarnings);
                if (refusal is not null)
                    return json ? JsonWire.RenderError(refusal, epoch) : "error: " + refusal + Wire.EpochLine(epoch);
                return DeltaResponse(rows, sArm, rArm, covers, epoch, Echo());
            }
            else   // tree
            {
                if (srcSpec.Kind != RecordReads.PoleKind.Winner)
                    return Wire.Refuse(json, "error: the tree form has no subject — every provider of each record is on the bench, and the pole each is diffed against is versus=. Drop source= (or use form='delta' for a subject-vs-reference comparison).");
                if (ListCost() is { } treeTooBig) return Wire.Refuse(json, treeTooBig);
                var rows = svc.TreeBatch(ids, versusSpec!, projFields, demand,
                                         out var rArm, out var covers, out var refusal, out var epoch,
                                         overlayWarnings);
                if (refusal is not null)
                    return json ? JsonWire.RenderError(refusal, epoch) : "error: " + refusal + Wire.EpochLine(epoch);
                return TreeResponse(rows, rArm, covers, epoch, Echo());
            }
        }

        /// <summary>The counts a windowed comparison carries, with `selected` beside them so `count` is never read as the whole selection.</summary>
        KeyValuePair<string, int>[] CmpCounts(params KeyValuePair<string, int>[] counts) =>
            cmpWindowNote is null ? counts : counts.Concat(new[] { KvI("selected", cmpSelected) }).ToArray();

        // The shared delta response pipeline — envelope, counts_only, window, spill, both renders — used by the
        // list and scan lanes alike.
        string DeltaResponse(IReadOnlyList<RecordReads.DeltaRow> rows, string? sArm, string? rArm, bool covers,
                             OrderStamp? epoch, List<KeyValuePair<string, string>> echo)
        {
            Arm(sArm ?? srcSpec.Label);
            envelope.Add(new("versus", rArm ?? versusSpec!.Label));
            headerLine += $"  versus={rArm ?? versusSpec!.Label}";
            CoverageNote(covers);
            StateOverlayWarnings();
            // A no-verdict is a THIRD state, neither a value difference nor identity, so it gets its own count.
            int differing = rows.Count(x => x.Error is null && x.Diff!.Deltas.Count > x.Diff.NoVerdictCount);
            int identical = rows.Count(x => x.Error is null && x.Diff!.Deltas.Count == 0 && x.Diff.Complete);
            int noVerdict = rows.Count(x => x.Error is null && x.Diff!.NoVerdictCount > 0);
            int errs = rows.Count(x => x.Error is not null);
            if (counts_only)
                return json
                    ? JsonWire.RenderNamedCounts(envelope, CmpCounts(KvI("count", rows.Count), KvI("differing", differing), KvI("identical", identical), KvI("no_verdict", noVerdict), KvI("errors", errs)), epoch, max_chars)
                    : Census($"{headerLine}\ncount={rows.Count} differing={differing} identical={identical} no_verdict={noVerdict} errors={errs}" + Wire.EpochLine(epoch));
            var winRows = Windowed(rows);
            SpillState? spill = null;
            if (wantFile)
            {
                var (s, aerr) = Artifacts.WriteDelta(rows, epoch?.Epoch, ArtifactTarget.Named(toFile!), "to_file", echo);
                if (aerr is not null) return json ? JsonWire.RenderError(aerr, epoch) : "error: " + aerr;
                spill = SpillState.Spilled(s!, manifestOnly: true);
            }
            var deltaCounts = CmpCounts(KvI("count", rows.Count), KvI("differing", differing), KvI("identical", identical), KvI("no_verdict", noVerdict), KvI("errors", errs));
            string Render(SpillState? sp, out bool trunc) => json
                ? JsonWire.RenderDelta(winRows, max_chars, epoch, envelope, deltaCounts, sp, out trunc)
                : RenderRecordsDelta(winRows, rows.Count, differing, identical, noVerdict, errs, headerLine, epoch, max_chars, sp, out trunc);
            var rendered = Render(spill, out var truncated);
            if (spill is null && truncated)
            {
                using var reservation = ResultsStore.Reserve(svc.ResultsDir, ToolNames.Records, epoch?.Epoch ?? "none");
                var (s, aerr) = Artifacts.WriteDelta(rows, epoch?.Epoch, reservation, "ceiling", echo);
                rendered = Render(aerr is null ? SpillState.Spilled(s!, manifestOnly: false) : SpillState.WriteFailed(aerr), out _);
            }
            return rendered;
        }

        // The shared tree response pipeline. The tree form has no subject: every provider is on the bench.
        string TreeResponse(IReadOnlyList<RecordReads.TreeRow> rows, string? rArm, bool covers,
                            OrderStamp? epoch, List<KeyValuePair<string, string>> echo)
        {
            // The tree's reference rides the `versus` envelope key, as delta's does, so `source` keeps the
            // SELECTION statement that makes epoch_covers_source intelligible.
            var refStatement = versusSpec!.Kind == RecordReads.PoleKind.Winner ? "winner" : rArm ?? versusSpec.Label;
            envelope.Add(new("versus", refStatement));
            headerLine += $"  versus={refStatement}";
            Arm("every provider of each record (the touching stack, winner last)");
            CoverageNote(covers);
            StateOverlayWarnings();
            int contested = rows.Count(x => x.Error is null && x.Touchers.Count > 1);
            int errs = rows.Count(x => x.Error is not null);
            if (counts_only)
                return json
                    ? JsonWire.RenderNamedCounts(envelope, new[] { KvI("count", rows.Count), KvI("contested", contested), KvI("errors", errs) }, epoch, max_chars)
                    : Census($"{headerLine}\ncount={rows.Count} contested={contested} errors={errs}" + Wire.EpochLine(epoch));
            var winRows = Windowed(rows);
            SpillState? spill = null;
            if (wantFile)
            {
                var (s, aerr) = Artifacts.WriteTree(rows, epoch?.Epoch, ArtifactTarget.Named(toFile!), "to_file", echo);
                if (aerr is not null) return json ? JsonWire.RenderError(aerr, epoch) : "error: " + aerr;
                spill = SpillState.Spilled(s!, manifestOnly: true);
            }
            var treeCounts = CmpCounts(KvI("count", rows.Count), KvI("contested", contested), KvI("errors", errs));
            string Render(SpillState? sp, out bool trunc) => json
                ? JsonWire.RenderTree(winRows, max_chars, epoch, envelope, treeCounts, sp, out trunc, LeverNames.Records)
                : RenderRecordsTree(winRows, rows.Count, contested, errs, projFields is { Length: > 0 }, headerLine, epoch, max_chars, sp, out trunc);
            var rendered = Render(spill, out var truncated);
            if (spill is null && truncated)
            {
                using var reservation = ResultsStore.Reserve(svc.ResultsDir, ToolNames.Records, epoch?.Epoch ?? "none");
                var (s, aerr) = Artifacts.WriteTree(rows, epoch?.Epoch, reservation, "ceiling", echo);
                rendered = Render(aerr is null ? SpillState.Spilled(s!, manifestOnly: false) : SpillState.WriteFailed(aerr), out _);
            }
            return rendered;
        }

        // The shared info_order response pipeline — envelope, counts_only, window, spill, both renders — used by
        // the list and scan lanes alike.
        string InfoOrderResponse(IReadOnlyList<RecordReads.InfoOrderRow> rows, OrderStamp? epoch,
                                 List<KeyValuePair<string, string>> echo)
        {
            if (ioFold is null)
                Arm("the merge of every touching plugin (the effective order the game walks)");
            else
            {
                // A projection, said as one, every statement naming the LABEL the rows carry rather than the bare
                // filename, which for a shadowed copy IS in the order.
                var f = ioFoldFacts!;
                Arm($"the merge of every touching plugin PLUS '{f.Label}' — {f.Where} — {f.Placement}");
                envelope.Add(new("folded", f.Label));
                envelope.Add(new("projection",
                    $"the folded file is the copy at {f.Where}; it is {f.Placement}. "
                    + (f.ShadowsActiveName
                        ? $"The FILENAME '{f.Plugin}' IS in the load order — served from another mod folder — so the folded copy's lines are labelled '{f.Label}' to tell the two apart. "
                        : "")
                    + "This is the order as it WOULD be with that copy enabled. Enable it and re-read for the live order."));
                headerLine += $"\n[projection] the folded file is '{f.Label}' — {f.Where} — and is {f.Placement}. "
                            + (f.ShadowsActiveName
                                ? $"The FILENAME '{f.Plugin}' IS in the order, served from another mod folder; the folded copy's lines carry the label above. "
                                : "")
                            + "Every line it places is marked below. Enable it and re-read for the live order.";
                CoverageNote(false);
            }
            int contested = rows.Count(x => x.Error is null && x.Order is { Contested: true });
            int errs = rows.Count(x => x.Error is not null);
            if (counts_only)
                return json
                    ? JsonWire.RenderNamedCounts(envelope, new[] { KvI("count", rows.Count), KvI("contested", contested), KvI("errors", errs) }, epoch, max_chars)
                    : Census($"{headerLine}\ncount={rows.Count} contested={contested} errors={errs}" + Wire.EpochLine(epoch));
            var winRows = Windowed(rows);
            SpillState? spill = null;
            if (wantFile)
            {
                var (s, aerr) = Artifacts.WriteInfoOrder(rows, epoch?.Epoch, ArtifactTarget.Named(toFile!), "to_file", echo);
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
                using var reservation = ResultsStore.Reserve(svc.ResultsDir, ToolNames.Records, epoch?.Epoch ?? "none");
                var (s, aerr) = Artifacts.WriteInfoOrder(rows, epoch?.Epoch, reservation, "ceiling", echo);
                rendered = Render(aerr is null ? SpillState.Spilled(s!, manifestOnly: false) : SpillState.WriteFailed(aerr), out _);
            }
            return rendered;
        }

        // A pole reading content outside the epoch fingerprint is declared in both the envelope and the header,
        // from one helper so no form forgets.
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

            if (srcOverlay || versusSpec?.Kind == RecordReads.PoleKind.Overlay)
                return Wire.Refuse(json, "error: an overlay pole on a SCAN would replay the SkyPatcher INI layer over every match — a per-record replay at scan scale " +
                       "(a scan comparison compares EVERY match, so it is not a bound). Name the records via formids= — the list lane reads and " +
                       "compares their post-state bodies — or read the whole layer via " + ToolNames.SkypatcherLayer + ".");
            bool hasBodyFilter = where is { Length: > 0 } || references is { Length: > 0 };
            bool hasTypes = types is { Length: > 0 };
            bool hasScope = plugins?.names is { Length: > 0 };
            bool scopePlusPole = false;
            // The derived-selection forms consume EVERY match; known up front, used by the scan cap below.
            bool derivedSelection = comparisonForm || form == "info_order" || walk is not null;
            // The scan states the source itself except for forms whose own pipeline states one; the tree has no
            // subject, so the scan's selection statement stays and discloses the selection universe.
            bool pipelineArms = form == "delta" || form == "info_order" || walk is not null;
            // An unbounded references= is answered off the reverse-reference index, which knows links, not values.
            bool onlyReverseFilter = where is not { Length: > 0 };
            if (hasBodyFilter && !hasTypes && !hasScope && !hasFormids && !onlyReverseFilter)
                return Wire.Refuse(json, "error: where= is a body scan and must be combined with types=, plugins=, or a formids= set to bound the work " +
                       "(conflicts_only= alone is not enough — an unbounded body scan over the whole order is refused). " +
                       "Only references= is unbounded, off the reverse-reference index.");
            if (plugins is { defined_in: true } && !hasScope)
                return Wire.Refuse(json, "error: plugins.defined_in=true keeps records DEFINED in the scoped plugins, so plugins.names must name that scope.");

            // The identity set intersects the scan, expanded inside the scan's own capture and parsed once; alone
            // it is the scan universe, the set being the bound.
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
            OrderStamp? probeEpoch = null;
            if (srcName is not null)
            {
                // One cheap containment probe decides the case; the scan below re-captures and is epoch-compared
                // against it, while the off-order lane reads the file and consults no further build.
                var probe = svc.ProbeSourceArm(srcName, srcMod, out var probeErr);
                if (probeErr is not null) return Wire.Refuse(json, "error: " + probeErr);
                srcName = probe!.Plugin;   // a path pole resolves back to its plugin name; every consumer below uses the resolved name
                probeEpoch = probe.Stamp;
                if (!probe.InOrder)
                    return OffOrderScan(probe);
                if (hasScope)
                {
                    // Scope and pole compose; an identity-fact form has nothing for the pole to change, so it
                    // refuses rather than accepting and ignoring it.
                    if (form is "summary" or "aggregate")
                        return Wire.Refuse(json, $"error: a plugins= scope with a named source= reads the POLE's version of each scoped match — and the '{form}' form's rows are identity facts the pole doesn't change. Drop source=, or use form='fields'/'everything' (the pole's bodies) or 'delta'/'tree' (comparisons).", probeEpoch);
                    if (winnerFields)
                        return Wire.Refuse(json, "error: fields_source='winner' and a named source= under a plugins= scope are TWO display poles on one call — the pole's version is what this composition reads. Drop fields_source= (or drop source= and keep fields_source='winner').", probeEpoch);
                    scopePlusPole = true;
                    // The scope statement is only truthful for forms that READ the pole's bodies; a scoped tree
                    // reads every provider, so it states the selection without the pole clause.
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
            // A negated-only unbounded references= is the whole orphan set, and the derived forms consume EVERY
            // match uncapped, so it is refused with the bound named. Read off scanPlugins, so an in-order source=
            // plugin counts as the scope it is.
            if (refNoneFks is not null && refFks is null && !hasTypes && scanPlugins is not { Length: > 0 } && !hasFormids && derivedSelection)
                return Wire.Refuse(json, $"error: a negated references= with no types=/plugins=/formids= bound is the orphan sweep — every record nothing in the order references — and the '{form}' form compares or merges EVERY match, uncapped. Add types= or plugins= to bound it, or run the sweep as a plain scan with to_file= and re-enter the artifact via formids=[\"@<file>\"].");

            bool definedIn = plugins?.defined_in ?? false;
            // group_by=type names each match's record type, which only a body-bearing scope can supply, pre-checked
            // here in this tool's own levers. IN-ORDER lane only: the off-order scan returned above, where the
            // file's own records ARE the universe. An unbounded references= is body-bearing too.
            if (form == "aggregate" && walk is null
                && string.Equals(project!.group_by?.Trim(), "type", StringComparison.OrdinalIgnoreCase)
                && !hasTypes && scanPlugins is not { Length: > 0 } && formidSet is null
                && refFks is null && refNoneFks is null)
                return Wire.Refuse(json, "error: project.group_by='type' counts each match's record TYPE, which only a " +
                    "body-bearing scope can name — add types=, plugins=, formids=, references=, or an in-order source= plugin. " +
                    "(where= reads bodies too but takes one of those as its own bound; references= brings its own universe " +
                    "off the reverse-reference index; 'winner' and 'defined_in' group without reading a body.)", probeEpoch);
            // Under walk= the scan only SELECTS the seeds; every reading form, the aggregate included, applies to
            // the reached set after the walk lane re-enters.
            var groupBy = form == "aggregate" && walk is null ? project!.group_by!.Trim().ToLowerInvariant() : null;
            // The derived-selection forms consume EVERY match, so the scan itself is uncapped for them and the
            // scan terms are the bound, as the tool description declares.
            int effLimit = wantFile || derivedSelection ? int.MaxValue : counts_only ? 0 : (limit <= 0 ? DefaultLimit : limit);

            var demandsList = new List<HousecarlCore.ArtifactDemand>();
            if (refDemand is not null) demandsList.Add(refDemand);
            if (fidDemand is not null) demandsList.Add(fidDemand);
            var outcome = svc.CrossQuery(types, refFks, null, conflicts_only, scanPlugins, where,
                                         effLimit, definedIn, groupBy, offset, where_source,
                                         demandsList.Count > 0 ? demandsList : null, formidSet, door.CapturedView,
                                         refNoneFks, ct);
            // The probe-to-scan seam is epoch-compared, so the source statement describes the build the rows came from.
            if (probeEpoch is not null && outcome.Error is null && outcome.Epoch is not null && outcome.Epoch != probeEpoch.Epoch)
            {
                var tear = $"the load order changed between resolving the source arm (epoch={probeEpoch.Epoch}) and the scan " +
                           $"(epoch={outcome.Epoch}) — the arm statement would describe a different world. Retry the call.";
                // Rendered in the caller's format, like every other refusal on these paths.
                return fmt is Wire.QueryFormat.Text ? "error: " + tear : JsonWire.RenderError(tear, outcome.Stamp);
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
                Add("source", srcName ?? (srcSpec.Kind != RecordReads.PoleKind.Winner ? srcSpec.Label : null));
                if (versusSpec is not null) Add("versus", versusSpec.Label);
                Add("window", cmpWindowNote);   // a ceiling spill of a windowed comparison holds the window, and says so
                return e;
            }

            // The reverse-reference index's accounting belongs to the response whatever consumes the scan: the two
            // CrossQuery renderers read it off the outcome, and every form below carries it on header and envelope
            // from here. bodyLaneForm is read by both this gate and the lane below, so the two cannot drift.
            bool bodyLaneForm = form == "everything" || form == "rows"
                             || (form == "fields" && (scopePlusPole || (foldPlan is not null && !dense)));
            if (outcome.ReverseIndexNote is not null && outcome.Error is null && outcome.Groups is null
                && (walk is not null || comparisonForm || form == "info_order" || (bodyLaneForm && !counts_only)))
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

            // ---- the render's own bound, checked before any body is read and BELOW the walk lane, which measures
            // its own reached count instead; the named-fields and 'everything' lanes are measured separately. ----
            if ((bodyFields || form == "everything") && !counts_only
                && outcome.Error is null && outcome.Groups is null
                && RenderBudget.Refuse(svc.Bounds, outcome.Keys.Count, form == "everything") is { } tooBig)
                return Wire.Refuse(json, tooBig, outcome.Stamp);

            // ---- delta / tree on a scan: the scan selects the records, the engine batches compare them, and the
            //      two captures' seam is epoch-compared.
            if (comparisonForm && outcome.Error is null && outcome.Groups is null)
            {
                envelope.Add(new("total", outcome.Total.ToString()));
                headerLine += $"\n{outcome.Total} match(es) selected by the scan";
                var cmpKeys = ComparisonWindow(outcome.Keys, outcome.Total);
                if (RenderBudget.RefuseComparison(svc.Bounds, cmpKeys.Count, form, ComparisonLever()) is { } cmpTooBig)
                    return Wire.Refuse(json, cmpTooBig, outcome.Stamp);
                if (form == "delta")
                {
                    var rows = svc.DeltaBatch(cmpKeys, srcSpec, versusSpec!, projFields, null,
                                              out var sArm, out var rArm, out var covers, out var refusal, out var depoch);
                    if (refusal is not null)
                        return json ? JsonWire.RenderError(refusal, depoch) : "error: " + refusal + Wire.EpochLine(depoch);
                    if (outcome.Epoch is not null && depoch is not null && depoch.Epoch != outcome.Epoch)
                    {
                        var tear = $"the load order changed between the scan (epoch={outcome.Epoch}) and the comparison " +
                                   $"(epoch={depoch.Epoch}) — the two halves would mix builds. Retry the call.";
                        return json ? JsonWire.RenderError(tear, depoch) : "error: " + tear;
                    }
                    return DeltaResponse(rows, sArm, rArm, covers, depoch, Echo());
                }
                else
                {
                    var rows = svc.TreeBatch(cmpKeys, versusSpec!, projFields, null,
                                             out var rArm, out var covers, out var refusal, out var tepoch);
                    if (refusal is not null)
                        return json ? JsonWire.RenderError(refusal, tepoch) : "error: " + refusal + Wire.EpochLine(tepoch);
                    if (outcome.Epoch is not null && tepoch is not null && tepoch.Epoch != outcome.Epoch)
                    {
                        var tear = $"the load order changed between the scan (epoch={outcome.Epoch}) and the comparison " +
                                   $"(epoch={tepoch.Epoch}) — the two halves would mix builds. Retry the call.";
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
                // The scan selects out of the ACTIVE order's index, which the folded file is not in, so a topic only
                // that file defines is reached by naming it in formids= — said here rather than left to be found.
                if (ioFoldFacts is not null)
                {
                    envelope.Add(new("fold_selection", $"the scan selected these topics from the ACTIVE order; a topic only '{ioFoldFacts.Label}' defines is not among them — name it in formids= to read its merge."));
                    headerLine += $"\n(the scan selects from the ACTIVE order; a topic only '{ioFoldFacts.Label}' defines is reached by naming it in formids=)";
                }
                var ioRows = svc.InfoOrderBatch(ioKeys, null, out var ioRefusal, out var ioEpoch, ioFold, ioFoldFacts);
                if (ioRefusal is not null)
                    return json ? JsonWire.RenderError(ioRefusal, ioEpoch) : "error: " + ioRefusal + Wire.EpochLine(ioEpoch);
                if (FoldSeam(ioEpoch) is { } ioScanTear) return Wire.Refuse(json, ioScanTear, ioEpoch);
                if (outcome.Epoch is not null && ioEpoch is not null && ioEpoch.Epoch != outcome.Epoch)
                {
                    var tear = $"the load order changed between the scan (epoch={outcome.Epoch}) and the merge " +
                               $"(epoch={ioEpoch?.Epoch}) — the two halves would mix builds. Retry the call.";
                    return json ? JsonWire.RenderError(tear, ioEpoch) : "error: " + tear;
                }
                // The scan's echo labels a named pole as a read source, which a fold is not, so the fold's
                // statement replaces it in the artifact manifest.
                var ioEcho = Echo();
                if (ioFoldFacts is not null)
                {
                    ioEcho.RemoveAll(kv => kv.Key == "source");
                    FoldEcho(ioEcho);
                }
                return InfoOrderResponse(ioRows, ioEpoch, ioEcho);
            }

            // ---- form=everything on a scan: selection here, bodies via the batch lane, window-bounded, while
            // counts_only skips the body lane for the scan render's census below. The rows form always takes this
            // lane, its fold being over a body read, as does a quantified fields path except under dense. ----
            if (bodyLaneForm && !counts_only && outcome.Error is null && outcome.Groups is null)
            {
                var keys = outcome.Keys.Select(k => k.ToString()).ToList();
                IReadOnlyList<ReadOutcome> bodies;
                // Clocked like the scan render, since the bound is one number over both lanes.
                var bodyClock = System.Diagnostics.Stopwatch.StartNew();
                if (srcName is not null)
                {
                    bodies = svc.ResolveBatchFromPole(keys, srcName, srcMod, bodyFields ? readPaths : null, depth, resolveNames, null,
                                                      out _, out var bref, out var brefEpoch, LeverNames.Records.ContainerHint, readDepths,
                                                      ct, outcome.GetterTypes, countFields);
                    // A refusal is judged on the named cause, never on row count: a zero-match scan is honest.
                    if (bref is not null)
                        return json ? JsonWire.RenderError(bref, brefEpoch)
                                    : "error: " + bref + Wire.EpochLine(brefEpoch);
                }
                else
                {
                    // The scan's per-match source decides whose body `everything` dumps, the rule the fields form
                    // renders by: keys group by matched source and each group reads off its own plugin, with
                    // fields_source="winner" retargeting display to the winner.
                    var srcs = outcome.Sources;
                    if (winnerFields || srcs is null || srcs.Take(keys.Count).All(s => s is null))
                        bodies = svc.ResolveBatch(keys, bodyFields ? readPaths : null, false, depth, resolveNames, containerHint: LeverNames.Records.ContainerHint, depths: readDepths, ct: ct, getterTypes: outcome.GetterTypes, countFields: countFields);
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
                            var res = svc.ResolveBatch(winnerIdx.Select(i => keys[i]).ToList(), bodyFields ? readPaths : null, false, depth, resolveNames, containerHint: LeverNames.Records.ContainerHint, depths: readDepths, ct: ct, getterTypes: outcome.GetterTypes, countFields: countFields);
                            for (int i = 0; i < winnerIdx.Count; i++) byIndex[winnerIdx[i]] = res[i];
                        }
                        foreach (var kv in bySource)
                        {
                            var res = svc.ResolveBatch(kv.Value.Select(i => keys[i]).ToList(), bodyFields ? readPaths : null, false, depth, resolveNames, kv.Key, LeverNames.Records.ContainerHint, readDepths, ct, outcome.GetterTypes, countFields);
                            for (int i = 0; i < kv.Value.Count; i++) byIndex[kv.Value[i]] = res[i];
                        }
                        bodies = byIndex;
                    }
                }
                bodyClock.Stop();
                bodies = FoldRows(bodies);
                // Rows the pole does not touch come back as per-item refusals naming the touchers, counted explicitly.
                if (scopePlusPole)
                {
                    int notTouched = bodies.Count(o => o.Error is not null && (o.Error.Contains("does not touch") || o.Error.Contains("does not define or override")));
                    if (notTouched > 0)
                    {
                        envelope.Add(new("not_touched", notTouched.ToString()));
                        headerLine += $"\nnot_touched={notTouched} — scoped match(es) the source pole has no version of (each row names its actual touchers)";
                    }
                }
                // The selection and every body read must agree on one build, since grouped reads capture per batch;
                // an empty selection has no body epochs and renders as an honest 0-row batch.
                var bodyEpochs = bodies.Where(o => o.Stamp is not null).Select(o => o.Stamp!).Distinct().ToList();
                if (outcome.Epoch is not null && bodyEpochs.Any(e => e.Epoch != outcome.Epoch))
                {
                    var tear = $"the load order changed between the scan (epoch={outcome.Epoch}) and the body read " +
                               $"(epoch={string.Join(", ", bodyEpochs.Where(e => e.Epoch != outcome.Epoch).Select(e => e.Epoch))}) — the two halves would mix builds. Retry the call.";
                    return json ? JsonWire.RenderError(tear, outcome.Stamp) : "error: " + tear;
                }
                var bodyEpoch = bodyEpochs.FirstOrDefault() ?? outcome.Stamp;
                envelope.Add(new("total", outcome.Total.ToString()));
                headerLine += $"\n{outcome.Total} match(es); bodies for the {keys.Count}-row window below";
                // What the SCAN owes about its own coverage, carried on both transports here as it is on the scan
                // lane, since a body form reads the same scan.
                if (outcome.ScanNote is not null)
                {
                    headerLine += "\n" + outcome.ScanNote;
                    envelope.Add(new("scan_note", outcome.ScanNote));
                }
                // Selected by a scan, not a formids list, so the batch notice's selection clause names limit=.
                var evLevers = formLevers.OnScanSelection();
                // Selected by the scan, so they carry its multi-target references= un-merge too, one row per key in
                // key order, which is what makes the list parallel to the bodies.
                var evMatches = outcome.MatchedTargets;
                string RenderEv(SpillState? sp, out bool trunc) => json
                    ? JsonWire.RenderBatch(bodies, max_chars, sp, out trunc, envelope, evLevers, (bodies.Count, bodyClock.ElapsedMilliseconds), evMatches)
                    : Wire.RenderBatch(bodies, max_chars, sp, out trunc, evLevers, (bodies.Count, bodyClock.ElapsedMilliseconds), headerLine, evMatches);
                SpillState? evSpill = null;
                if (wantFile)
                {
                    var (s, aerr) = Artifacts.WriteBatch(bodies, ArtifactTarget.Named(toFile!), "to_file", Echo(), evLevers, matches: evMatches);
                    if (aerr is not null) return json ? JsonWire.RenderError(aerr, bodyEpoch) : "error: " + aerr;
                    evSpill = SpillState.Spilled(s!, manifestOnly: true);
                }
                var evRendered = RenderEv(evSpill, out var evTrunc);
                if (evSpill is null && evTrunc)
                {
                    using var reservation = ResultsStore.Reserve(svc.ResultsDir, ToolNames.Records, bodyEpoch?.Epoch ?? "none");
                    var (s, aerr) = Artifacts.WriteBatch(bodies, reservation, "ceiling", Echo(), evLevers, matches: evMatches);
                    evRendered = RenderEv(aerr is null ? SpillState.Spilled(s!, manifestOnly: false) : SpillState.WriteFailed(aerr), out _);
                }
                return evRendered;
            }

            // ---- summary / fields / aggregate: the scan renders, envelope-stamped. ----
            SpillState? spill = null;
            if (wantFile && outcome.Error is null)
            {
                var (s, aerr) = Artifacts.WriteCrossQuery(svc, outcome, readPaths, resolveNames, winnerFields, depth, ArtifactTarget.Named(toFile!), "to_file", Echo(), LeverNames.Records, fold: foldPlan, ct: ct);
                if (aerr is not null)
                    return fmt is Wire.QueryFormat.Text ? "error: " + aerr : JsonWire.RenderError(aerr, outcome.Stamp);
                spill = SpillState.Spilled(s!, manifestOnly: true);
            }
            // "drop project=" is only actionable on detail rows; a summary-form scan has no project= to drop.
            var qLevers = projFields is { Length: > 0 } ? LeverNames.Records : LeverNames.Records.WithNothingToDrop();
            string Render(SpillState? sp, out bool trunc) => fmt switch
            {
                Wire.QueryFormat.Dense when groupBy is null => JsonWire.RenderCrossQueryDense(svc, outcome, readPaths, max_chars, resolveNames, winnerFields, sp, out trunc, envelope, qLevers, foldPlan, ct),
                Wire.QueryFormat.Dense or Wire.QueryFormat.Json => JsonWire.RenderCrossQuery(svc, outcome, projFields, max_chars, resolveNames, winnerFields, depth, sp, out trunc, envelope, qLevers, ct, TableRowLimit(limit)),
                _ => Wire.RenderCrossQuery(svc, outcome, projFields, max_chars, resolveNames, winnerFields, depth, sp, out trunc, qLevers, ct, headerLine, TableRowLimit(limit)),
            };
            var rendered = Render(spill, out var truncated);
            if (spill is null && truncated && outcome.Error is null)
            {
                // Disposing the reservation deletes the file it owns unless the write landed, so a cancel inside
                // the write leaves nothing.
                using var reservation = ResultsStore.Reserve(svc.ResultsDir, ToolNames.Records, outcome.Epoch ?? "none");
                var (s, aerr) = Artifacts.WriteCrossQuery(svc, outcome, readPaths, resolveNames, winnerFields, depth, reservation, "ceiling", Echo(), LeverNames.Records, fold: foldPlan, ct: ct);
                rendered = Render(aerr is null ? SpillState.Spilled(s!, manifestOnly: false) : SpillState.WriteFailed(aerr), out _);
            }
            return rendered;
        }

        // ================================================================================================
        //  OFF-ORDER scan: the file's own records are the universe.
        // ================================================================================================
        string OffOrderScan(RecordReads.PoleInfo pole)
        {
            // The file is the SELECTION statement, stated for every form except delta, whose pipeline names the
            // same file as its subject.
            if (form != "delta") Arm($"{pole.Plugin} — {pole.Where}");
            envelope.Add(new("epoch_covers_source", "false"));
            headerLine += "\n(the off-order file's content is OUTSIDE the epoch fingerprint — an edit to it changes answers without changing the epoch)";
            if (conflicts_only)
                return Wire.Refuse(json, "error: conflicts_only= has no meaning on an out-of-load-order file — it is not in the conflict frame. Drop it, or read the winner (source=\"winner\").", pole.Stamp);
            if (form == "info_order")
                return Wire.Refuse(json, "error: the info_order form merges the ACTIVE order's touching plugins — an out-of-load-order file is not in that frame. Read the winner's merge (drop source=), or enumerate the file's DIAL records with form='summary'.", pole.Stamp);
            if (walk is not null)
                return Wire.Refuse(json, "error: the walk expands the ACTIVE order's winner link graph — an out-of-load-order file's records are not in that graph. Enumerate the file with form='summary', then walk specific records via formids= (dropping source=).", pole.Stamp);
            if (dense) return "error: format='dense' is the in-order scan's columnar form — an off-order file scan renders text or json.";
            if (versusSpec?.Kind == RecordReads.PoleKind.Overlay)
                return Wire.Refuse(json, "error: an overlay pole on a SCAN would replay the SkyPatcher INI layer over every match — a per-record replay at scan scale " +
                       "(a scan comparison compares EVERY match, so it is not a bound). Name the records via formids= — the list lane reads and " +
                       "compares their post-state bodies — or read the whole layer via " + ToolNames.SkypatcherLayer + ".", pole.Stamp);
            if (where_source is not null)
            {
                // Full-vocabulary validation, mirroring the in-order engine, so an unknown spelling refuses by name.
                var ws = where_source.Trim().ToLowerInvariant();
                if (ws == "winner")
                    return Wire.Refuse(json, "error: where_source=winner matches on the live load-order winner — but this scan streams an out-of-load-order FILE's bodies, many of which have no winner. Match the winner by scanning the winner (drop source=), or drop where_source=.", pole.Stamp);
                if (ws is not ("scoped" or "scanned"))
                    return Wire.Refuse(json, $"error: where_source='{where_source}' is not a known source — over an out-of-load-order file the match reads the FILE's own bodies ('scoped', the default); drop where_source=, or use 'winner' on an in-order scan.", pole.Stamp);
            }

            // The off-order lane runs the in-order filter grammar over the file's own records, with provenance
            // terms still bound to the active view the response declares.
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
            int offLimit = wantFile || offDerived ? int.MaxValue : counts_only ? 0 : (limit <= 0 ? DefaultLimit : limit);
            var offDemands = new List<HousecarlCore.ArtifactDemand>();
            if (refDemand is not null) offDemands.Add(refDemand);
            if (fidDemand is not null) offDemands.Add(fidDemand);

            var outcome = svc.OffOrderQuery(pole, types, refFks, null, plugins?.names,
                                            plugins?.defined_in ?? false, where, offLimit, offGroupBy, offset,
                                            formidSet, offDemands.Count > 0 ? offDemands : null, door.CapturedView,
                                            refNoneFks, ct);

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
                Add("window", cmpWindowNote);   // a ceiling spill of a windowed comparison holds the window, and says so
                return e;
            }

            // Comparisons over the file's matches: the file IS the subject pole (its version of each match).
            if (comparisonForm && outcome.Error is null && outcome.Groups is null)
            {
                envelope.Add(new("total", outcome.Total.ToString()));
                headerLine += $"\n{outcome.Total} match(es) selected from the file";
                var cmpKeys = ComparisonWindow(outcome.Keys, outcome.Total);
                if (RenderBudget.RefuseComparison(svc.Bounds, cmpKeys.Count, form, ComparisonLever()) is { } offCmpTooBig)
                    return Wire.Refuse(json, offCmpTooBig, outcome.Stamp);
                if (form == "delta")
                {
                    var rows = svc.DeltaBatch(cmpKeys, srcSpec, versusSpec!, projFields, null,
                                              out var sArm, out var rArm, out var covers, out var refusal, out var depoch);
                    if (refusal is not null)
                        return json ? JsonWire.RenderError(refusal, depoch) : "error: " + refusal + Wire.EpochLine(depoch);
                    // The file selection's build and the comparison's must agree, as on the in-order seam.
                    if (outcome.Epoch is not null && depoch is not null && depoch.Epoch != outcome.Epoch)
                    {
                        var tear = $"the load order changed between the file scan (epoch={outcome.Epoch}) and the comparison " +
                                   $"(epoch={depoch.Epoch}) — the two halves would mix builds. Retry the call.";
                        return json ? JsonWire.RenderError(tear, depoch) : "error: " + tear;
                    }
                    return DeltaResponse(rows, sArm, rArm, covers, depoch, Echo());
                }
                else
                {
                    var rows = svc.TreeBatch(cmpKeys, versusSpec!, projFields, null,
                                             out var rArm, out var covers, out var refusal, out var tepoch);
                    if (refusal is not null)
                        return json ? JsonWire.RenderError(refusal, tepoch) : "error: " + refusal + Wire.EpochLine(tepoch);
                    if (outcome.Epoch is not null && tepoch is not null && tepoch.Epoch != outcome.Epoch)
                    {
                        var tear = $"the load order changed between the file scan (epoch={outcome.Epoch}) and the comparison " +
                                   $"(epoch={tepoch.Epoch}) — the two halves would mix builds. Retry the call.";
                        return json ? JsonWire.RenderError(tear, tepoch) : "error: " + tear;
                    }
                    return TreeResponse(rows, rArm, covers, tepoch, Echo());
                }
            }

            // fields/everything over the file's matches: bodies via the one-pole batch (it reads the FILE).
            if (form is ("fields" or "rows" or "everything") && !counts_only && outcome.Error is null && outcome.Groups is null)
            {
                var keys = outcome.Keys.Select(k => k.ToString()).ToList();
                // The render bound is on the row cost, not on where the row came from, so it refuses on the same
                // numbers before reading a body.
                if (RenderBudget.Refuse(svc.Bounds, keys.Count, form == "everything") is { } offTooBig)
                    return Wire.Refuse(json, offTooBig, outcome.Stamp);
                // And clocked the same way, so the bound's estimate is checkable on this lane too.
                var offClock = System.Diagnostics.Stopwatch.StartNew();
                var bodies = svc.ResolveBatchFromPole(keys, pole.Plugin, srcMod, bodyFields ? readPaths : null,
                                                      depth, resolveNames, null, out _, out var bref, out var brefEpoch,
                                                      LeverNames.Records.ContainerHint, readDepths, ct, countFields: countFields);
                offClock.Stop();
                bodies = FoldRows(bodies);
                if (bref is not null)
                    return json ? JsonWire.RenderError(bref, brefEpoch)
                                : "error: " + bref + Wire.EpochLine(brefEpoch);
                // The selection's build and the body reads' must agree, as on the in-order body seam: the bodies
                // re-open the file, but the selection was made on the view.
                var offBodyEpochs = bodies.Where(o => o.Epoch is not null).Select(o => o.Epoch!).Distinct().ToList();
                if (outcome.Epoch is not null && offBodyEpochs.Any(e => e != outcome.Epoch))
                {
                    var tear = $"the load order changed between the file scan (epoch={outcome.Epoch}) and the body read " +
                               $"(epoch={string.Join(", ", offBodyEpochs.Where(e => e != outcome.Epoch))}) — the two halves would mix builds. Retry the call.";
                    return json ? JsonWire.RenderError(tear, outcome.Stamp) : "error: " + tear;
                }
                envelope.Add(new("total", outcome.Total.ToString()));
                headerLine += $"\n{outcome.Total} match(es); bodies for the {keys.Count}-row window below";
                // Same coverage note the in-order body lane carries, on both transports, for the same reason.
                if (outcome.ScanNote is not null)
                {
                    headerLine += "\n" + outcome.ScanNote;
                    envelope.Add(new("scan_note", outcome.ScanNote));
                }
                // Selected by the off-order file scan, so the remedy vocabulary matches the body lane above.
                var offLevers = formLevers.OnScanSelection();
                // Same rule as the in-order body lane: the file scan's rows carry its references= un-merge too.
                var offMatches = outcome.MatchedTargets;
                string RenderOff(SpillState? sp, out bool trunc) => json
                    ? JsonWire.RenderBatch(bodies, max_chars, sp, out trunc, envelope, offLevers, (bodies.Count, offClock.ElapsedMilliseconds), offMatches)
                    : Wire.RenderBatch(bodies, max_chars, sp, out trunc, offLevers, (bodies.Count, offClock.ElapsedMilliseconds), headerLine, offMatches);
                SpillState? offSpill = null;
                var offEpoch = bodies.FirstOrDefault(o => o.Stamp is not null)?.Stamp ?? outcome.Stamp;
                if (wantFile)
                {
                    var (sp, aerr) = Artifacts.WriteBatch(bodies, ArtifactTarget.Named(toFile!), "to_file", Echo(), offLevers, matches: offMatches);
                    if (aerr is not null) return json ? JsonWire.RenderError(aerr, offEpoch) : "error: " + aerr;
                    offSpill = SpillState.Spilled(sp!, manifestOnly: true);
                }
                var offRendered = RenderOff(offSpill, out var offTrunc);
                if (offSpill is null && offTrunc)
                {
                    using var reservation = ResultsStore.Reserve(svc.ResultsDir, ToolNames.Records, offEpoch?.Epoch ?? "none");
                    var (sp, aerr) = Artifacts.WriteBatch(bodies, reservation, "ceiling", Echo(), offLevers, matches: offMatches);
                    offRendered = RenderOff(aerr is null ? SpillState.Spilled(sp!, manifestOnly: false) : SpillState.WriteFailed(aerr), out _);
                }
                return offRendered;
            }

            // summary / aggregate: the shared scan renders, prefilled rows carrying the file's identities and
            // winner context where a record also lives in the order.
            SpillState? spill = null;
            if (wantFile && outcome.Error is null)
            {
                var (sp, aerr) = Artifacts.WriteCrossQuery(svc, outcome, null, false, false, 1, ArtifactTarget.Named(toFile!), "to_file", Echo(), LeverNames.Records);
                if (aerr is not null)
                    return fmt is Wire.QueryFormat.Text ? "error: " + aerr : JsonWire.RenderError(aerr, outcome.Stamp);
                spill = SpillState.Spilled(sp!, manifestOnly: true);
            }
            // The off-order scan passes no field paths, so it never has a project= to drop.
            var offQLevers = LeverNames.Records.WithNothingToDrop();
            string Render(SpillState? sp, out bool trunc) => fmt switch
            {
                Wire.QueryFormat.Dense or Wire.QueryFormat.Json => JsonWire.RenderCrossQuery(svc, outcome, null, max_chars, false, false, 1, sp, out trunc, envelope, offQLevers, rowLimit: TableRowLimit(limit)),
                _ => Wire.RenderCrossQuery(svc, outcome, null, max_chars, false, false, 1, sp, out trunc, offQLevers, header: headerLine, rowLimit: TableRowLimit(limit)),
            };
            var rendered = Render(spill, out var truncated);
            if (spill is null && truncated && outcome.Error is null)
            {
                using var reservation = ResultsStore.Reserve(svc.ResultsDir, ToolNames.Records, outcome.Epoch ?? "none");
                var (sp, aerr) = Artifacts.WriteCrossQuery(svc, outcome, null, false, false, 1, reservation, "ceiling", Echo(), LeverNames.Records);
                rendered = Render(aerr is null ? SpillState.Spilled(sp!, manifestOnly: false) : SpillState.WriteFailed(aerr), out _);
            }
            return rendered;
        }
    }, ct);

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

    /// <summary>Parse a pole expression from its wire spelling, returning the named refusal or null; <paramref name="subjectRole"/> marks source=, which previous_provider is measured FROM and so cannot be.</summary>
    static string? ParsePole(JsonElement? el, string param, bool subjectRole, out RecordReads.PoleSpec? spec)
    {
        spec = null;
        if (el is not { } e || e.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        if (e.ValueKind == JsonValueKind.String)
        {
            var s = e.GetString()!.Trim();
            if (s.Length == 0 || s.Equals("winner", StringComparison.OrdinalIgnoreCase))
            { spec = RecordReads.PoleSpec.Winner; return null; }
            if (s.Equals("previous_provider", StringComparison.OrdinalIgnoreCase))
            {
                if (subjectRole)
                    return "error: source= is the SUBJECT of the call, and 'previous_provider' is measured FROM the subject " +
                           "(it is the plugin immediately below whatever source= names, §4.3) — so it cannot BE the subject. " +
                           "Name the subject via source= and pass versus=\"previous_provider\".";
                spec = new RecordReads.PoleSpec(RecordReads.PoleKind.PreviousProvider);
                return null;
            }
            spec = new RecordReads.PoleSpec(RecordReads.PoleKind.Named, s);
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
                // The draft INI, a file not yet placed in a mod, is a value on this pole rather than a mode of its own.
                if (e.TryGetProperty("ini", out var iniEl) && iniEl.ValueKind != JsonValueKind.String)
                    return $"error: {param}= overlay \"ini\" is the absolute path to a draft .ini file, as a string.";
                if (e.TryGetProperty("subfolder", out var subEl) && subEl.ValueKind != JsonValueKind.String)
                    return $"error: {param}= overlay \"subfolder\" is the SkyPatcher type folder the draft would be placed in, as a string.";
                string? draftIni = iniEl.ValueKind == JsonValueKind.String ? iniEl.GetString() : null;
                string? draftSub = subEl.ValueKind == JsonValueKind.String ? subEl.GetString() : null;
                SkyPatcherDraft.Plan? plan = null;
                if (draftIni is null && draftSub is not null)
                    return $"error: {param}= overlay names \"subfolder\" with no \"ini\" — the subfolder says where a DRAFT would be placed, so pass the draft's path as \"ini\" too, or drop \"subfolder\".";
                if (draftIni is not null)
                {
                    if (st.Equals("pre", StringComparison.OrdinalIgnoreCase))
                        return $"error: {param}= overlay state \"pre\" IS the plain load-order winner, the body the INI layer starts from, so a draft INI cannot change it — read the draft with state \"post\".";
                    if (SkyPatcherDraft.Prepare(draftIni, draftSub, SkyPatcherCatalog.Load(), out plan) is { } derr)
                        return $"error: {param}= {derr}";
                }
                spec = new RecordReads.PoleSpec(RecordReads.PoleKind.Overlay, OverlayState: st.ToLowerInvariant(), Draft: plan);
                return null;
            }
            // The draft keys ride the overlay pole and have no meaning on a {"file"} pole.
            if (e.TryGetProperty("ini", out _) || e.TryGetProperty("subfolder", out _))
                return $"error: {param}= names \"ini\"/\"subfolder\" without \"overlay\" — a draft INI is a value on the SkyPatcher overlay pole, " +
                       "so pass {\"overlay\": \"skypatcher\", \"state\": \"post\", \"ini\": \"<absolute path>\", \"subfolder\": \"<type folder>\"}.";
            if (!e.TryGetProperty("file", out var fEl) || fEl.ValueKind != JsonValueKind.String)
                return $"error: a structured {param}= names the plugin as {{\"file\": \"X.esp\"[, \"mod\": \"<mod folder>\"]}} or the runtime view as {{\"overlay\": \"skypatcher\", \"state\": \"pre\"|\"post\"[, \"ini\": \"<draft path>\", \"subfolder\": \"<type folder>\"]}}.";
            string? mod = e.TryGetProperty("mod", out var mEl) && mEl.ValueKind == JsonValueKind.String ? mEl.GetString()!.Trim() : null;
            spec = new RecordReads.PoleSpec(RecordReads.PoleKind.Named, fEl.GetString()!.Trim(), mod);
            return null;
        }
        return $"error: {param}= is a string (\"winner\" | a plugin filename{(subjectRole ? "" : " | \"previous_provider\"")}) or an object ({{\"file\", \"mod\"}} | {{\"overlay\", \"state\"}}).";
    }

    /// <summary>references= @file expansion with the negation sigil carried across it: the sigil is stripped before the expander sees it, which decides "this is a file" on the first character, and put back on each expanded token.</summary>
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

    /// <summary>Split references= into the targets a match must link to and the ones it must NOT, a leading '!' negating an entry; the two compose by AND, and a FormID never begins with '!'.</summary>
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
