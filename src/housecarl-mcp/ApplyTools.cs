using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;

namespace HousecarlMcp;

/// <summary>
/// housecarl_apply — the 2.0 S1 field-write surface (tool-surface-2.0 W3; SPEC §2.2 ACT, §4.5, §5.1/§5.2, §6.1).
///
/// ONE field-edit tool: the ACT verbs (Set / Add / Remove / SetAtIndex / ReplaceAll / Merge / CopyFrom) × the LANE
/// (new patch | <c>into</c> an existing one | <c>in_place</c> + consent | <c>dry_run</c>) × TRANSPORT, over the SAME
/// proven write cleave the 1.x tools drive (<see cref="LoadOrderService.ApplyEdits"/> → WritePatchBuilder.Apply) —
/// consolidation of the surface, not a re-implementation of the engine. Absorbs <c>housecarl_set_field</c> (the
/// degenerate one-op call) and <c>housecarl_bulk_apply</c>.
///
/// Three things are new rather than renamed:
/// <list type="bullet">
/// <item><b>The §4.5 zip</b> — <c>bundle=</c> (field paths, uniform across pairs) × <c>assignments=</c>
/// ([{target, from, from_source}]) is a cross-RECORD field-bundle copy, the generic core under
/// "give this record that record's Keywords/stats/appearance frame". Which paths form a bundle is skill-carried
/// data; the tool stays generic (CLAUDE.md §3, second cornerstone).</item>
/// <item><b>One list spelling</b> — <c>ops=</c> takes the inline array OR <c>"@&lt;absolute path&gt;"</c>
/// (SPEC §5.1's @file convention), retiring <c>from_file=</c> and its inline-vs-file mutual exclusion. Both lanes
/// now parse through the SAME strict reader, so an unknown member is refused BY NAME inline too — the SDK binder
/// silently DROPS one, which in a 700-op job points every downstream refusal away from the typo.</item>
/// <item><b>in_place is the file's NAME</b> (§5.2) — the <c>target=</c>+<c>in_place=true</c> pair collapses into one
/// string naming the file being overwritten. Restating it is deliberate consent-adjacent explicitness.</item>
/// </list>
/// The 1.x write tools stay registered and unchanged through the build waves; they retire at 2.0.0 (clean cut,
/// CHARTER_PHASE4 §3.4a).
/// </summary>
[McpServerToolType]
public static class ApplyTools
{
    [McpServerTool(Name = "housecarl_apply", Title = "Edit record fields (the 2.0 write surface)"),
     Description(
         "Edit fields on one or many records and write the result to a NEW patch plugin (originals untouched by " +
         "default). ONE surface: what to change (ops=, or the bundle=/assignments= copy zip) x WHERE it lands (the " +
         "LANE: a new patch | into= an existing one | in_place=\"X.esp\" | dry_run) x how it reads back (TRANSPORT).\n\n" +
         "A FormID is 'XXXXXX:Plugin.esp' — 6 hex digits, a colon, the defining master's filename. Every edit " +
         "resolves the record's load-order WINNER and overrides it into the patch; all edits land in ONE reviewable " +
         ".esp, whose master header spans every plugin the edits reference (cross-master merge, derived not declared).\n\n" +
         "OPS. ops= is the edit list — {formid, field_path, op?, value?, values?, key?, entries?, compose?, " +
         "composes?, from?, from_source?} each; one op is a set of one (the old set_field call). field_path is " +
         "dotted ('BasicStats.Damage', 'Name', 'Keywords'); step INTO a list/dict element MID-path with brackets " +
         "('Effects[0].Data.Magnitude'), but at the LEAF use op + key instead of brackets. value is coerced to the " +
         "field's real type — a number, an enum name ('OneHanded'), or a FormID for a reference.\n" +
         "op is Set (default) | Add | Remove | SetAtIndex | ReplaceAll | Merge | CopyFrom. On a [Flags] enum (SPEL " +
         "Flags, NPC Configuration.Flags, WEAP Data.Flags...) Add SETS a bit and Remove CLEARS one, leaving the " +
         "OTHER bits untouched — the way to flip one flag WITHOUT a Set silently dropping every bit you didn't " +
         "mention; to turn all bits off, Set the field to '0'. Omit value only for a Remove that whole-clears a " +
         "NULLABLE field. values= is the whole new list for a list ReplaceAll; entries= (key->value) drives a dict " +
         "Merge or dict ReplaceAll; key= is the dict key or list index at the leaf.\n" +
         "compose= builds a MODELED struct for an Add or a polymorphic Set — a leveled-list entry, an effect, a " +
         "condition row, or a polymorphic list element by its CONCRETE arm type (e.g. a VMAD script property: " +
         "op=Add, field_path='VirtualMachineAdapter.Scripts[0].Properties', compose={type:'ScriptObjectProperty', " +
         "fields:{Name:'MyProp', Flags:'Edited', Object:'XXXXXX:Plugin.esp', Alias:'-1'}}). Merging a weapon into a " +
         "leveled list: op=Add, field_path='Entries', compose={type:'LeveledItemEntry', sets:[{path:'Data.Level'," +
         "value:'1'},{path:'Data.Count',value:'1'},{path:'Data.Reference',value:'<weapon FormID>'}]}. composes= is " +
         "the BATCH sibling — a LIST built in ONE op: with Add it APPENDS each in order (a whole block of condition " +
         "rows at once), with ReplaceAll it CLEARS the list then appends each (the way to replace a whole modeled " +
         "list); composes=[] with ReplaceAll clears the list to empty. compose and composes are mutually exclusive.\n" +
         "COPYING A FIELD: op=CopyFrom takes no value — the source IS another record's version. from_source= names " +
         "the plugin to copy from (an ACTIVE plugin, or a plugin FILE on disk that isn't in the load order — a " +
         "disabled old patch you want to re-assert a field from). from= names a DIFFERENT source RECORD (defaulting " +
         "to from_source's version of the record being edited); with from= and no from_source= the source is that " +
         "record's load-order winner. Cross-record pairs must be the SAME record type — refused by name otherwise. " +
         "CopyFrom copies a WHOLE field (scalar, formlink, modeled list, sub-struct); it cannot copy owned child " +
         "records (forward the whole record with housecarl_forward instead).\n\n" +
         "THE COPY ZIP (bundle= x assignments=). To copy the SAME set of fields from one record to another — many " +
         "pairs in one call — name the paths once and pair them explicitly: bundle=[\"BasicStats.Damage\"," +
         "\"Keywords\"], assignments=[{target:'AAA:Mod.esp', from:'BBB:Other.esp'}, {target:..., from:..., " +
         "from_source:'OldPatch.esp'}]. It is a ZIP, never a product: each target takes its OWN source. Identity and " +
         "everything outside the bundle are untouched BY CONSTRUCTION — a bundle only names what it copies. Which " +
         "paths form an appearance set or a balance frame is knowledge a skill carries, not a verb this tool owns. " +
         "The zip composes with ops= in one call.\n\n" +
         "LANE — where the write lands. Default: a NEW patch named patch= (auto-suffixed if taken, so a prior patch " +
         "is never overwritten). into='<an existing patch's filename>' EXTENDS that patch instead — the way to " +
         "accumulate across calls and sessions. PRECEDENCE with into= (pinned): a FormKey the patch ALREADY CARRIES " +
         "is edited AS-IS in the patch; only a FormKey it does NOT yet carry copies the load-order winner in first. " +
         "So housecarl_forward from a source + apply into= the same patch is THE recipe to build on a " +
         "specific plugin's version while a stale winner sits above it. in_place='<plugin filename>' is the opt-in " +
         "THIRD lane: houseCARL rewrites your ORIGINAL file (incl. a mod it didn't author) — no new patch, and NO " +
         "houseCARL backup or undo (keep your own). It re-lays-out the whole plugin the way xEdit/CK do on save, " +
         "VERIFIES the records you edit, trusts Mutagen for the untouched rest, and refuses a file it can't parse or " +
         "that holds engine-reserved (sub-0x800) records. The FIRST in-place edit of a given plugin returns a " +
         "one-time confirmation prompt (re-call with acknowledge=true); that consent covers touching your original " +
         "ONLY — it NEVER skips the record verify.\n" +
         "dry_run=true runs the FULL pipeline — winner resolve, schema pre-flight, every op applied in memory, the " +
         "reference-resolution check — and STOPS before anything touches disk. It returns what WOULD change and the " +
         "expected masters, or EXACTLY the refusal the real call would give: catch a bad field path before the first " +
         "write of a big batch, not after the last. Works on every lane (an in-place dry run needs no acknowledge " +
         "and never records consent). Not a disk guarantee — a serialize/commit fault still surfaces only for real.\n\n" +
         "ALL-OR-NOTHING (Q3): if ANY op is malformed or fails pre-flight, the whole call is refused with per-op " +
         "reasons and NOTHING is written. No partial patches, ever.\n\n" +
         "TRANSPORT: readback=true expands the read-back to the FULL deep field-by-field dump of every touched " +
         "record (in place, the verify ALWAYS runs and shows compactly by default; readback widens it) — confirm " +
         "composed structures landed and nothing else was disturbed WITHOUT enabling the patch in MO2. The read-back " +
         "is the WRITTEN FILE's content, NOT load-order truth: the patch wins nothing until enabled + sorted in MO2. " +
         "format='json' returns the same data machine-readable; max_chars caps the render with an explicit notice, " +
         "never a silent cut. Every response carries epoch=<hex> — the identity of the index build the winners were " +
         "resolved from.\n\n" +
         "ops= (like every list-valued input) also accepts \"@<absolute path>\" in place of the inline array: the " +
         "SAME array as a JSON manifest on disk. Write a big job's ops once, dry-run the file, then apply it — and " +
         "re-run the same manifest to recover an interrupted write (overrides are idempotent). The path must be " +
         "ABSOLUTE (the server resolves relative paths against its OWN working directory, not yours), and the file " +
         "is read at CALL time, so re-dry-run it after editing.\n\n" +
         "This tool edits EXISTING records' fields. New records are housecarl_create; dropping whole records is " +
         "housecarl_remove; copying a whole record verbatim is housecarl_forward. Read first with " +
         "housecarl_records.")]
    public static string Apply(
        LoadOrderService svc,
        [Description("The edits, all into one artifact: [{formid, field_path, op?, value?, values?, key?, entries?, compose?, composes?, from?, from_source?}, …] — or \"@<absolute path>\" to read that SAME array from a JSON manifest file. One op is a set of one. An op member the shape does not declare is refused BY NAME at its element, never silently dropped.")]
            JsonElement? ops = null,
        [Description("THE COPY ZIP (with assignments=): the field paths copied for EVERY pair, e.g. [\"BasicStats.Damage\", \"Keywords\"]. Accepts [\"@<absolute path>\"] to read the path list from a file. Only what this names is copied — identity and every other field are untouched by construction.")]
            string[]? bundle = null,
        [Description("THE COPY ZIP (with bundle=): the per-target source mapping — [{target: 'XXXXXX:Plugin.esp', from: 'YYYYYY:Other.esp', from_source?: 'SomePlugin.esp'}, …], or \"@<absolute path>\". A ZIP, never a product: each target takes its OWN source record. from_source defaults to the source record's load-order winner; target and from must be the SAME record type.")]
            JsonElement? assignments = null,
        [Description("LANE: base filename for the NEW patch this call writes (default 'Patch'); auto-suffixed if taken, so a prior patch is never overwritten. Mutually exclusive with into= and in_place= — naming both lanes is refused, never silently ignored.")]
            string? patch = null,
        [Description("LANE: filename of an EXISTING houseCARL patch to EXTEND with these edits instead of writing a fresh one — the way to accumulate across calls and sessions. Found by the plugin's filename even if you've renamed its MO2 mod folder; for two patches sharing a filename, pass the mod-folder name here instead.")]
            string? into = null,
        [Description("LANE (opt-in): the FILENAME OF THE FILE BEING OVERWRITTEN, e.g. \"CoolWeapons.esp\" — edit that existing active plugin IN PLACE (incl. one houseCARL didn't author) instead of writing a patch. Your ORIGINAL file is rewritten; no houseCARL backup or undo. Naming the file is the point: it is what you are about to overwrite. OMIT for the default patch lane, which leaves every original untouched.")]
            string? in_place = null,
        [Description("Confirms the one-time in-place trade-off for the plugin named by in_place= — needed only on the FIRST in-place write to a given plugin, never again for it. Waives the consent to touch your original ONLY; it NEVER skips the record verify. Meaningless without in_place=, and refused there rather than ignored.")]
            bool acknowledge = false,
        [Description("DRY RUN: run the whole real pipeline and STOP before anything touches disk. Returns what WOULD change (the would-be values, the expected masters), or EXACTLY the refusal the real call would give. Works on every lane.")]
            bool dry_run = false,
        [Description("TRANSPORT: expand the read-back to the FULL deep field-by-field dump of every record this call touched (not just the edited leaves). The written file's content, not load-order truth.")]
            bool readback = false,
        [Description("TRANSPORT: 'text' (default) | 'json' (the same data, machine-readable, accounting in-band).")]
            string? format = null,
        [Description("TRANSPORT: character ceiling on the WHOLE render — in format=\"json\" the applied-op rows as well as the read-back; in text, the read-back. Past it, trailing rows are dropped with an explicit notice (never silent); the WRITE is unaffected. 0 = a safe default kept under the host's per-response limit; raise it to widen a readback=true dump.")]
            int max_chars = 0) => Guard.Tool("housecarl_apply", () =>
    {
        // ---- TRANSPORT: format --------------------------------------------------------------------------
        // Ahead of the unconfigured-MO2 prompt (PR #311 review 5 [low]): that prompt is prose, and a json caller
        // got it verbatim — unparseable, with no ok/error to branch on. Inherited from PR #310, fixed here with
        // its three siblings rather than left as the one tool of the four that still does it.
        bool json = Wire.WantsJson(format, out var ferr);
        if (ferr is not null) return ferr;   // the format value itself is unparsed — there is no known render to answer in
        if (svc.ConfigPromptOrNull() is { } prompt)
            return json ? JsonWire.RenderError(prompt, null) : prompt;

        // EVERY refusal below this point answers in the caller's requested format. RenderPatchOutcome's contract
        // says a json caller must never have to parse "error: …" out of a string, and the sites a caller hits most
        // (a mixed inline/@file list, an undeclared op member, a LANE conflict, half a zip) are all decided HERE,
        // before any engine outcome exists to render. Epoch is null on all of them: none has consulted a build yet.
        string Refuse(string message) => json ? JsonWire.RenderError(message, null) : "error: " + message;

        // ---- LANE: the three destinations are mutually exclusive, and a dropped one is named ------------
        // (SPEC §2.1 LANE; the W2 review's recurring theme carried to the write side — a parameter is honored
        //  or refused BY NAME, never accepted-and-ignored. 1.x silently ignored patch_name= under into=.)
        // Emptiness is judged ONE way for a lane string: whitespace-only is absent, and the forwarded value uses
        // the same rule (`patch` is normalized below), so the exclusivity checks and what actually gets written
        // can never disagree about whether a lane was named.
        var patchName = string.IsNullOrWhiteSpace(patch) ? null : patch.Trim();
        bool hasPatch = patchName is not null;
        bool hasInto = !string.IsNullOrWhiteSpace(into);
        bool hasInPlace = !string.IsNullOrWhiteSpace(in_place);
        if (hasInto && hasInPlace)
            return Refuse("into= and in_place= are different lanes — into= EXTENDS a houseCARL patch, in_place= rewrites an existing plugin's own file. Name one.");
        if (hasPatch && hasInto)
            return Refuse($"patch='{patch}' names a NEW patch to write, but into='{into}' extends an existing one — the two lanes are exclusive. Drop patch= to extend, or drop into= to write fresh.");
        if (hasPatch && hasInPlace)
            return Refuse($"patch='{patch}' names a NEW patch to write, but in_place='{in_place}' rewrites that plugin's own file — the two lanes are exclusive. Drop patch= to edit in place, or drop in_place= to write a patch.");
        if (acknowledge && !hasInPlace)
            return Refuse("acknowledge= confirms the in-place trade-off and is meaningless without in_place=<plugin filename>. Drop it, or name the file to overwrite.");

        // ---- The edit sources: ops= and/or the §4.5 zip -------------------------------------------------
        var edits = new List<ApplyOp>();
        if (ops is { } opsEl && opsEl.ValueKind is not JsonValueKind.Null)
        {
            var (parsed, err) = ReadOps(opsEl);
            if (err is not null) return Refuse(err);
            edits.AddRange(parsed!);
        }

        // An explicitly EMPTY bundle= is a supplied parameter, not an absent one — reading it as absent is the
        // accepted-and-silently-dropped class this tool exists to close, and it is exactly what `ops=[]` already
        // refuses by name. Judge presence on the ARRAY, then refuse emptiness on its own terms.
        bool hasBundle = bundle is not null;
        bool hasAssignments = assignments is { } aEl && aEl.ValueKind is not JsonValueKind.Null;
        if (hasBundle && bundle!.Length == 0)
            return Refuse("bundle= is an empty array — give at least one dotted field path to copy (e.g. bundle=[\"BasicStats.Damage\"]), or drop bundle= and assignments= entirely.");
        if (hasBundle != hasAssignments)
            return Refuse(hasBundle
                ? "bundle= names the field paths to copy but assignments= names the target/source PAIRS — the zip needs both. Add assignments=[{target, from}, …], or use ops= for edits that aren't a copy."
                : "assignments= names the target/source PAIRS but bundle= names the field paths to copy — the zip needs both. Add bundle=[\"<field path>\", …].");
        if (hasBundle)
        {
            var (paths, perr) = ReadBundlePaths(bundle!);
            if (perr is not null) return Refuse(perr);
            var (zipped, zerr) = ExpandZip(paths!, assignments!.Value);
            if (zerr is not null) return Refuse(zerr);
            edits.AddRange(zipped!);
        }

        if (edits.Count == 0)
            return Refuse("nothing to apply. Pass ops=[{formid, field_path, …}, …] (or ops=\"@<absolute path>\"), " +
                          "and/or the copy zip bundle=[\"<field path>\", …] + assignments=[{target, from}, …].");

        // ---- Map the 2.0 op shape onto the proven cleave ------------------------------------------------
        // The 2.0 spellings are a RENAME over the same engine inputs: op -> verb, from_source -> the source
        // plugin, from -> the source RECORD (§4.5's cross-record copy, carried alongside since it has no 1.x
        // wire member). Mapping problems are collected per element, all at once, like every other refusal.
        var wire = new List<BulkOp>(edits.Count);
        var fromRecords = new List<string?>(edits.Count);
        var origins = new List<string?>(edits.Count);
        var problems = new List<string>();
        for (int i = 0; i < edits.Count; i++)
        {
            var e = edits[i];
            // Zip-generated edits carry the caller's OWN spelling (assignments[i] x bundle[j]); inline ops fall
            // back to their real index. Both this loop and the service's mapper use it, so a refusal never points
            // at an op index the caller did not write.
            var where = e.Origin ?? $"op[{i}]";
            if (e.From is not null && !string.Equals(e.Op ?? "Set", "CopyFrom", StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"{where}: from= names the SOURCE RECORD of a copy and is only valid with op='CopyFrom' (got op='{e.Op ?? "Set"}').");
                continue;
            }
            wire.Add(new BulkOp
            {
                Formid = e.Formid, FieldPath = e.FieldPath, Verb = e.Op ?? "Set", Value = e.Value, Key = e.Key,
                Values = e.Values, Entries = e.Entries, Compose = e.Compose, Composes = e.Composes,
                FromPlugin = e.FromSource,
            });
            fromRecords.Add(e.From);
            origins.Add(e.Origin);
        }
        if (problems.Count > 0)
            return Refuse($"refused — {problems.Count} of {edits.Count} operation(s) malformed; NOTHING written:\n  - "
                        + string.Join("\n  - ", problems));

        var outcome = svc.ApplyEdits(wire, patchName ?? "Patch", into, readback, in_place, hasInPlace, acknowledge, dry_run, fromRecords, origins);
        // The lane the CALL named — stated, not derived from the outcome's flags, which are at their defaults on a
        // refusal and on the consent prompt (PR #311 review [medium]).
        return json
            ? JsonWire.RenderPatchOutcome(outcome, max_chars, readback, hasInPlace ? "in_place" : hasInto ? "into" : "patch")
            : WriteTools.Render(outcome, max_chars, readback, laneAsName: true);
    });

    // ---- input readers -------------------------------------------------------------------------------

    /// <summary>Read <c>ops=</c>: either the inline JSON array of op objects, or the SPEC §5.1 <c>@file</c> spelling
    /// — a bare <c>"@&lt;path&gt;"</c> string, or the one-element <c>["@&lt;path&gt;"]</c> form that matches how
    /// <c>formids=</c> spells it (both accepted so the convention reads the same on a typed list and a string list).
    /// BOTH lanes deserialize through the same strict options, so an unknown member is refused by name inline too —
    /// the SDK's own binder silently drops one, and in a large generated batch a misspelled <c>field_path</c> would
    /// then surface as a downstream refusal pointing away from the typo.</summary>
    static (ApplyOp[]? Items, string? Error) ReadOps(JsonElement el)
        => ListParams.Read<ApplyOp>(el, "ops", "{formid, field_path, op?, value?, values?, key?, entries?, compose?, composes?, from?, from_source?}");

    /// <summary>Read <c>assignments=</c> — the §4.5 zip's per-target source mapping. Same two spellings and the same
    /// strict element contract as <see cref="ReadOps"/>.</summary>
    static (Assignment[]? Items, string? Error) ReadAssignments(JsonElement el)
        => ListParams.Read<Assignment>(el, "assignments", "{target, from, from_source?}");


    // ---- the §4.5 zip --------------------------------------------------------------------------------

    /// <summary>Resolve <c>bundle=</c> to its field-path list, honoring the <c>["@&lt;path&gt;"]</c> spelling (a
    /// newline/comma-free JSON string array on disk) so a long, reused bundle can live in a file like any other
    /// list input.</summary>
    static (IReadOnlyList<string>? Paths, string? Error) ReadBundlePaths(string[] bundle)
    {
        // A MIXED inline/@file list has no meaning here either — and it used to slip through, because the @file
        // branch only fired at Length == 1: an "@path" sitting beside real paths became a literal dotted FIELD
        // path, and the caller got an engine "no such field" refusal naming something they never meant as a field
        // (review [low]). Named here with ListParams.Read's wording, so all three list inputs answer alike.
        int atCount = bundle.Count(b => b?.TrimStart().StartsWith('@') == true);
        if (atCount > 0 && bundle.Length != 1)
            return (null, $"bundle: \"@<path>\" reads the WHOLE list from a file, so it cannot be mixed with inline elements " +
                          $"(found {atCount} @-element(s) among {bundle.Length}). Pass either the inline array of field paths or a single \"@<absolute path>\".");
        if (bundle.Length == 1 && bundle[0]?.TrimStart().StartsWith('@') == true)
        {
            var (text, err) = ListParams.ReadAtFile(bundle[0], "bundle");
            if (err is not null) return (null, err);
            string[]? paths;
            try { paths = JsonSerializer.Deserialize<string[]>(text!, ListParams.Strict); }
            catch (JsonException ex) { return (null, $"the file named by bundle could not be parsed: {ListParams.ShearStjPosition(Guard.Flatten(ex.Message))} Expected a JSON array of field-path strings."); }
            if (paths is null || paths.Length == 0) return (null, "the file named by bundle holds no field paths — expected a JSON array of dotted field paths.");
            bundle = paths;
        }
        var clean = new List<string>(bundle.Length);
        for (int i = 0; i < bundle.Length; i++)
        {
            var p = bundle[i]?.Trim();
            if (string.IsNullOrEmpty(p))
                return (null, $"bundle[{i}] is empty — every entry is a dotted field path to copy (e.g. \"BasicStats.Damage\").");
            clean.Add(p);
        }
        return (clean, null);
    }

    /// <summary>Expand the §4.5 zip into ops: for each assignment, ONE CopyFrom op per bundle path. A zip, never a
    /// product — each target reads its OWN paired source record, so N targets x M paths is N*M ops over N sources,
    /// not N*N. Pair-level shape is validated here (both halves present, and not the same record); FormID SYNTAX,
    /// the same-record-type gate, and the per-path legality rulebook are the engine's pre-flight, which reports
    /// EVERY failure at once before anything is written (§4.5: "legality does not shrink"). Each generated op
    /// carries the caller's own spelling as its <see cref="ApplyOp.Origin"/>, so a downstream refusal names
    /// <c>assignments[i] x bundle[j]</c> rather than an op index that exists only after this expansion.</summary>
    static (IReadOnlyList<ApplyOp>? Ops, string? Error) ExpandZip(IReadOnlyList<string> paths, JsonElement assignments)
    {
        var (pairs, err) = ReadAssignments(assignments);
        if (err is not null) return (null, err);

        var problems = new List<string>();
        var ops = new List<ApplyOp>(pairs!.Length * paths.Count);
        for (int i = 0; i < pairs.Length; i++)
        {
            var a = pairs[i];
            if (string.IsNullOrWhiteSpace(a.Target))
                { problems.Add($"assignments[{i}]: target is required — the FormID of the record being written."); continue; }
            if (string.IsNullOrWhiteSpace(a.From))
                { problems.Add($"assignments[{i}] ({a.Target}): from is required — the FormID of the record to copy the bundle FROM."); continue; }
            if (string.Equals(a.Target!.Trim(), a.From!.Trim(), StringComparison.OrdinalIgnoreCase))
                { problems.Add($"assignments[{i}]: target and from are the same record ({a.Target}) — copying a record's fields onto itself is a no-op. To re-assert an EARLIER PLUGIN's version of this record's fields, keep from= off and name that plugin in from_source=."); continue; }
            for (int b = 0; b < paths.Count; b++)
                ops.Add(new ApplyOp
                {
                    Formid = a.Target, FieldPath = paths[b], Op = "CopyFrom",
                    From = a.From, FromSource = a.FromSource,
                    Origin = $"assignments[{i}] x bundle[{b}] ('{paths[b]}')",
                });
        }
        return problems.Count > 0
            ? (null, $"refused — {problems.Count} of {pairs.Length} assignment(s) malformed; NOTHING written:\n  - " + string.Join("\n  - ", problems))
            : (ops, null);
    }
}

// ---- wire DTOs (the 2.0 op + zip shapes) ---------------------------------------------------------------

/// <summary>One field edit off the 2.0 wire (housecarl_apply). The 1.x <see cref="BulkOp"/> with the SPEC §5.1
/// vocabulary — <c>verb</c> is <c>op</c> (§5.1's one verb-name, AT THE OP LEVEL: a nested set inside
/// <c>compose=</c> is a <see cref="NestedSet"/>, shared verbatim with the 1.x wire shape, and still spells
/// <c>verb</c>; the §5.3 row renames the op member, not every verb-shaped member beneath it), <c>from_plugin</c> splits into the
/// §4.5 pair <c>from</c> (the source RECORD) + <c>from_source</c> (the pole it is read from). Its own record rather
/// than a reshaped BulkOp so the 1.x tools' published schemas stay untouched through the build waves.</summary>
public sealed record ApplyOp
{
    [JsonPropertyName("formid"), Description("The record to edit, as 'XXXXXX:Plugin.esp'.")]
    public string? Formid { get; init; }

    [JsonPropertyName("field_path"), Description("Dotted field path, e.g. 'BasicStats.Damage' or 'Entries'. Step into a list/dict element mid-path with brackets ('Effects[0].Data.Magnitude'); at the LEAF use op + key, not brackets.")]
    public string? FieldPath { get; init; }

    [JsonPropertyName("op"), Description("Set (default) | Add | Remove | SetAtIndex | ReplaceAll | Merge | CopyFrom. On a [Flags] enum, Add sets one bit and Remove clears one, leaving the others untouched.")]
    public string? Op { get; init; }

    [JsonPropertyName("value"), Description("The value, coerced to the field's type. Omit for Remove / ReplaceAll / Merge / compose / CopyFrom.")]
    public string? Value { get; init; }

    [JsonPropertyName("key"), Description("Dict key or list index at the leaf.")]
    public string? Key { get; init; }

    [JsonPropertyName("values"), Description("The whole new list for a list ReplaceAll.")]
    public string[]? Values { get; init; }

    [JsonPropertyName("entries"), Description("Key->value pairs for a dict Merge or dict ReplaceAll.")]
    public Dictionary<string, string>? Entries { get; init; }

    [JsonPropertyName("compose"), Description("Build a modeled struct: the arm for a polymorphic Set, or the element for a struct-element Add (e.g. 'LeveledItemEntry'; for a polymorphic list, the element's CONCRETE arm type such as 'ScriptObjectProperty').")]
    public StructInput? Compose { get; init; }

    [JsonPropertyName("composes"), Description("Build MANY modeled list elements in ONE op — the batch sibling of compose. With Add, appends each in order; with ReplaceAll, clears the list then appends each (composes=[] with ReplaceAll clears it to empty). Mutually exclusive with compose/value/values.")]
    public StructInput[]? Composes { get; init; }

    [JsonPropertyName("from"), Description("op='CopyFrom' only: the SOURCE RECORD to copy the field from, as 'XXXXXX:Plugin.esp' — a DIFFERENT record from formid (SPEC §4.5's cross-record copy). Omit to copy this same record's version from another plugin (name it in from_source). Source and target must be the same record type.")]
    public string? From { get; init; }

    [JsonPropertyName("from_source"), Description("op='CopyFrom' only: WHOSE version of the source record to copy — an ACTIVE plugin, or a plugin FILE on disk that isn't in the load order (a disabled old patch). With from= it defaults to the source record's load-order winner; without from= it is required (there is no other source to name).")]
    public string? FromSource { get; init; }

    /// <summary>NOT a wire member — never deserialized from a caller (<see cref="JsonIgnoreAttribute"/> keeps it out
    /// of the published schema and out of the strict reader's member set). It is how an op the §4.5 zip GENERATED
    /// remembers the caller's own spelling, so a refusal reads "assignments[0] x bundle[1]" instead of an op index
    /// that only exists after expansion.</summary>
    [JsonIgnore]
    public string? Origin { get; init; }
}

/// <summary>One pair of the SPEC §4.5 <c>assignments=</c> zip: the record being written, the record its bundle is
/// copied FROM, and optionally the pole that source is read at. Explicit pairing is the whole point — a join here
/// is a ZIP, never a product, so N targets never silently fan out to N*N copies.</summary>
public sealed record Assignment
{
    [JsonPropertyName("target"), Description("The record being WRITTEN, as 'XXXXXX:Plugin.esp' — the §5.2 meaning of the bare word 'target': a copy's destination record.")]
    public string? Target { get; init; }

    [JsonPropertyName("from"), Description("The record the bundle is copied FROM, as 'XXXXXX:Plugin.esp'. Must be the same record type as target.")]
    public string? From { get; init; }

    [JsonPropertyName("from_source"), Description("Optional. WHOSE version of the source record to read — a plugin filename (active, or a file on disk out of the load order). Defaults to the source record's load-order winner.")]
    public string? FromSource { get; init; }
}
