using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using HousecarlCore;

namespace HousecarlMcp;

/// <summary>housecarl_apply — the field-write surface: the write verbs × the lane (new patch, <c>into=</c> an
/// existing one, <c>in_place=</c> with consent, or <c>dry_run</c>) × transport, over
/// <see cref="LoadOrderService.ApplyEdits"/>. <c>bundle=</c> × <c>assignments=</c> is the cross-record field-bundle
/// copy; which paths form a bundle is caller data, so the tool stays generic (the second cornerstone). Every list
/// input takes the inline array or an @file naming the same array — <c>"@&lt;absolute path&gt;"</c> on the
/// <c>JsonElement</c> inputs, <c>["@&lt;absolute path&gt;"]</c> on <c>bundle=</c>, which is typed
/// <c>string[]</c> — through the same strict reader, which refuses an unknown member BY NAME where the SDK binder
/// would silently drop it.</summary>
[McpServerToolType]
public static class ApplyTools
{
    [McpServerTool(Name = ToolNames.Apply, Title = "Edit record fields (the 2.0 write surface)"),
     Description(
         "Edit fields on one or many records and write the result to a NEW patch plugin (originals untouched by " +
         "default). ONE surface: what to change (ops=, or the bundle=/assignments= copy zip) x WHERE it lands (the " +
         "LANE: a new patch | into= an existing one | in_place=\"X.esp\" | dry_run) x how it reads back (TRANSPORT).\n\n" +
         "A FormID is 'XXXXXX:Plugin.esp' — 6 hex digits, a colon, the defining master's filename, and every FormID " +
         "this tool takes (ops[].formid, from=, a field VALUE) is that form. The RUNTIME form the game, the console " +
         "and the logs print — eight hex digits and no plugin name, 'FExxxYYY' / 'XX######' — is READ-ONLY, because " +
         "it names a slot in the load order as it stands rather than a record: pass one here and it is refused with " +
         "the 'XXXXXX:Plugin.esp' form to use in its place. " + ToolNames.Records + " reads by either form. The " +
         "list inputs also take a JSON manifest on disk holding the SAME array: ops= and assignments= as " +
         "\"@<absolute path>\", bundle= as [\"@<absolute path>\"]. The path must be ABSOLUTE (the server resolves " +
         "relative paths against its OWN working directory, not yours), and the file is read at CALL time, so " +
         "re-dry-run it after editing.\n\n" +
         "Every edit " +
         "resolves the record's load-order WINNER and overrides it into the patch; all edits land in ONE reviewable " +
         ".esp, whose master header spans every plugin the edits reference (cross-master merge, derived not " +
         "declared). ALL-OR-NOTHING (Q3): if ANY op is malformed or fails pre-flight, the whole call is refused " +
         "with per-op reasons and NOTHING is written. No partial patches, ever.\n\n" +
         "Each axis's grammar is on its own parameters:\n" +
         "WHAT — ops= (the edit list), or the copy zip bundle= x assignments=.\n" +
         "LANE — patch= | into= | in_place= with acknowledge= | dry_run=.\n" +
         "TRANSPORT — readback= | format= | max_chars=.\n\n" +
         "COMPOSITION: the zip composes with ops= in one call.\n\n" +
         "This tool edits EXISTING records' fields. New records are " + ToolNames.Create + "; dropping whole records is " +
         ToolNames.Remove + "; copying a whole record verbatim is " + ToolNames.Forward + ". Read first with " +
         ToolNames.Records + ".")]
    public static string Apply(
        LoadOrderService svc,
        [Description("The edits, all into one artifact: [{formid, field_path, op?, value?, values?, key?, entries?, compose?, composes?, from?, from_source?}, …] — or \"@<absolute path>\" to read that SAME array from a JSON manifest file. One op is a set of one. An op member the shape does not declare is refused BY NAME at its element, never silently dropped. The manifest is how a big job is run: write the ops once, dry-run the file, then apply it — and re-run the same manifest to recover an interrupted write (overrides are idempotent).")]
            JsonElement? ops = null,
        [Description("THE COPY ZIP (with assignments=): the field paths copied for EVERY pair, e.g. [\"BasicStats.Damage\", \"Keywords\"] — to copy the SAME set of fields from one record to another, many pairs in one call, name the paths once here and pair them explicitly in assignments=. Accepts [\"@<absolute path>\"] to read the path list from a file. Only what this names is copied — identity and every other field are untouched BY CONSTRUCTION: a bundle only names what it copies. Which paths form an appearance set or a balance frame is knowledge a skill carries, not a verb this tool owns.")]
            string[]? bundle = null,
        [Description("THE COPY ZIP (with bundle=): the per-target source mapping — [{target: 'XXXXXX:Plugin.esp', from: 'YYYYYY:Other.esp', from_source?: 'SomePlugin.esp'}, …], or \"@<absolute path>\". A ZIP, never a product: each target takes its OWN source record. from_source defaults to the source record's load-order winner; target and from must be the SAME record type.")]
            JsonElement? assignments = null,
        [Description("LANE: base filename for the NEW patch this call writes (default 'Patch'); auto-suffixed if taken, so a prior patch is never overwritten. Mutually exclusive with into= and in_place= — naming both lanes is refused, never silently ignored.")]
            string? patch = null,
        [Description("LANE: filename of an EXISTING houseCARL patch to EXTEND with these edits instead of writing a fresh one — the way to accumulate across calls and sessions. PRECEDENCE (pinned): a FormKey the patch ALREADY CARRIES is edited AS-IS in the patch; only a FormKey it does NOT yet carry copies the load-order winner in first. So " + ToolNames.Forward + " from a source + apply into= the same patch is THE recipe to build on a specific plugin's version while a stale winner sits above it. Found by the plugin's filename even if you've renamed its MO2 mod folder; for two patches sharing a filename, pass the mod-folder name here instead.")]
            string? into = null,
        [Description("LANE (opt-in): the FILENAME OF THE FILE BEING OVERWRITTEN, e.g. \"CoolWeapons.esp\" — edit that existing active plugin IN PLACE (incl. one houseCARL didn't author) instead of writing a patch. Your ORIGINAL file is rewritten; no houseCARL backup or undo (keep your own). It re-lays-out the whole plugin the way xEdit/CK do on save, VERIFIES the records you edit, trusts Mutagen for the untouched rest, and refuses a file it can't parse or that holds engine-reserved (sub-0x800) records. Naming the file is the point: it is what you are about to overwrite. OMIT for the default patch lane, which leaves every original untouched.")]
            string? in_place = null,
        [Description("Confirms the one-time in-place trade-off for the plugin named by in_place= — needed only on the FIRST in-place write to a given plugin (edit, create, remove, OR forward), and not again once one has LANDED — a call that is refused records nothing, so it may be needed again. Without it that first call returns a confirmation prompt instead of writing; re-call with acknowledge=true. Waives the consent to touch your original ONLY; it NEVER skips the record verify. Meaningless without in_place=, and refused there rather than ignored.")]
            bool acknowledge = false,
        [Description("DRY RUN: run the FULL real pipeline — winner resolve, schema pre-flight, every op applied in memory, the reference-resolution check — and STOP before anything touches disk. Returns what WOULD change (the would-be values, the expected masters), or EXACTLY the refusal the real call would give: catch a bad field path before the first write of a big batch, not after the last. Works on every lane (an in-place dry run needs no acknowledge and never records consent). Not a disk guarantee — a serialize/commit fault still surfaces only for real.")]
            bool dry_run = false,
        [Description("TRANSPORT: expand the read-back to the FULL deep field-by-field dump of every record this call touched (not just the edited leaves) — confirm composed structures landed and nothing else was disturbed WITHOUT enabling the patch in MO2. In place, the verify ALWAYS runs and shows compactly by default; this widens it. The read-back is the WRITTEN FILE's content, NOT load-order truth: the patch wins nothing until enabled + sorted in MO2.")]
            bool readback = false,
        [Description("TRANSPORT: 'text' (default) | 'json' (the same data, machine-readable, accounting in-band). Every response carries the epoch stamp — the identity of the index build the winners were resolved from — spelled epoch=<hex> on 'text', and as an 'epoch' member on 'json'.")]
            string? format = null,
        [Description("TRANSPORT: character ceiling on the WHOLE render — in format=\"json\" the applied-op rows as well as the read-back; in text, the read-back. Past it, trailing rows are dropped with an explicit notice (never silent); the WRITE is unaffected. 0 = a safe default kept under the host's per-response limit; raise it to widen a readback=true dump.")]
            int max_chars = 0) => Guard.Tool(ToolNames.Apply, () =>
    {
        // ---- TRANSPORT: format --------------------------------------------------------------------------
        // Ahead of the unconfigured-MO2 prompt: that prompt is prose, and a json caller handed it verbatim gets
        // something unparseable, with no ok/error to branch on.
        bool json = Wire.WantsJson(format, out var ferr);
        if (ferr is not null) return ferr;   // the format value itself is unparsed — there is no known render to answer in
        if (svc.ConfigPromptOrNull() is { } prompt)
            return json ? JsonWire.RenderError(prompt, null) : prompt;

        // EVERY refusal below this point answers in the caller's requested format — a json caller must never have to
        // parse "error: …" out of a string. Epoch is null on all of them: none has consulted a build yet.
        string Refuse(string message) => json ? JsonWire.RenderError(message, null) : "error: " + message;

        // ---- LANE: the three destinations are mutually exclusive, and a dropped one is named ------------
        // A parameter is honoured or refused BY NAME, never accepted-and-ignored. Emptiness is judged ONE way for a
        // lane string — whitespace-only is absent, and the forwarded value uses the same rule — so the exclusivity
        // checks and what actually gets written can never disagree about whether a lane was named.
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

        // ---- The edit sources: ops= and/or the copy zip -------------------------------------------------
        var edits = new List<ApplyOp>();
        if (ops is { } opsEl && opsEl.ValueKind is not JsonValueKind.Null)
        {
            var (parsed, err) = ReadOps(opsEl);
            if (err is not null) return Refuse(err);
            edits.AddRange(parsed!);
        }

        // An explicitly EMPTY bundle= is a supplied parameter, not an absent one: judge presence on the ARRAY, then
        // refuse emptiness on its own terms, or the parameter is accepted and silently dropped.
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

        // ---- Map the op shape onto the engine's inputs --------------------------------------------------
        // A rename over the same engine inputs: op -> verb, from_source -> the source plugin; from (the source
        // RECORD) has no engine wire member and rides alongside. Mapping problems are collected all at once.
        var wire = new List<BulkOp>(edits.Count);
        var fromRecords = new List<string?>(edits.Count);
        var origins = new List<string?>(edits.Count);
        var problems = new List<string>();
        for (int i = 0; i < edits.Count; i++)
        {
            var e = edits[i];
            // Zip-generated edits carry the caller's OWN spelling (assignments[i] x bundle[j]); inline ops fall back
            // to their real index. The service's mapper uses it too, so no refusal names an index nobody wrote.
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
        // refusal and on the consent prompt.
        return json
            ? JsonWire.RenderPatchOutcome(outcome, max_chars, readback, hasInPlace ? "in_place" : hasInto ? "into" : "patch")
            : WriteTools.Render(outcome, max_chars, readback);
    });

    // ---- input readers -------------------------------------------------------------------------------

    /// <summary>Read <c>ops=</c>: the inline JSON array of op objects, or the <c>@file</c> spelling — a bare
    /// <c>"@&lt;path&gt;"</c> string or the one-element <c>["@&lt;path&gt;"]</c> form, both accepted so the convention
    /// reads the same on a typed list and a string list. Both lanes deserialize through the same strict options, so an
    /// unknown member is refused by name inline too rather than being dropped by the SDK binder.</summary>
    static (ApplyOp[]? Items, string? Error) ReadOps(JsonElement el)
        => ListParams.Read<ApplyOp>(el, "ops", "{formid, field_path, op?, value?, values?, key?, entries?, compose?, composes?, from?, from_source?}");

    /// <summary>Read <c>assignments=</c> — the copy zip's per-target source mapping. Same two spellings and the same
    /// strict element contract as <see cref="ReadOps"/>.</summary>
    static (Assignment[]? Items, string? Error) ReadAssignments(JsonElement el)
        => ListParams.Read<Assignment>(el, "assignments", "{target, from, from_source?}");


    // ---- the copy zip --------------------------------------------------------------------------------

    /// <summary>Resolve <c>bundle=</c> to its field-path list, honoring the <c>["@&lt;path&gt;"]</c> spelling (a
    /// newline/comma-free JSON string array on disk) so a long, reused bundle can live in a file like any other
    /// list input.</summary>
    static (IReadOnlyList<string>? Paths, string? Error) ReadBundlePaths(string[] bundle)
    {
        // A MIXED inline/@file list has no meaning: the @file branch only fires at Length == 1, so an "@path" beside
        // real paths would become a literal dotted FIELD path. Worded as ListParams.Read does, so all three agree.
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

    /// <summary>Expand the copy zip into ops: for each assignment, ONE CopyFrom op per bundle path. A zip, never a
    /// product — each target reads its OWN paired source record, so N targets x M paths is N*M ops over N sources.
    /// Only pair-level shape is checked here (both halves present, and not the same record); FormID syntax, the
    /// same-record-type gate and the per-path legality rulebook are the engine's pre-flight. Each generated op carries
    /// the caller's own spelling as its <see cref="ApplyOp.Origin"/>, so a downstream refusal names
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

// ---- wire DTOs (the op + zip shapes) -------------------------------------------------------------------

/// <summary>One field edit off housecarl_apply's wire — <see cref="BulkOp"/> with this surface's vocabulary:
/// <c>verb</c> is <c>op</c> AT THE OP LEVEL only (a nested set inside <c>compose=</c> is a <see cref="NestedSet"/>
/// and still spells <c>verb</c>), and the source plugin splits into <c>from</c> (the source RECORD) plus
/// <c>from_source</c> (the pole it is read at).</summary>
public sealed record ApplyOp
{
    [JsonPropertyName("formid"), Description("The record to edit, as 'XXXXXX:Plugin.esp'.")]
    public string? Formid { get; init; }

    [JsonPropertyName("field_path"), Description("Dotted field path, e.g. 'BasicStats.Damage', 'Name', 'Keywords' or 'Entries'. Step into a list/dict element mid-path with brackets ('Effects[0].Data.Magnitude'); at the LEAF use op + key, not brackets.")]
    public string? FieldPath { get; init; }

    [JsonPropertyName("op"), Description(WriteVerbs.AllRecital + ". SetAtIndex OVERWRITES the element at key=; InsertAtIndex inserts a NEW one AT key= and shifts the rest right (key = the list's length appends) — use it to grow a POSITION-CONTIGUOUS run in place, e.g. adding an arm to an existing CTDA OR-group, where Add would land the row at the end as a separate AND-group. On a [Flags] enum (SPEL Flags, NPC Configuration.Flags, WEAP Data.Flags...) Add SETS a bit and Remove CLEARS one, leaving the OTHER bits untouched — the way to flip one flag WITHOUT a Set silently dropping every bit you didn't mention; to turn all bits off, Set the field to '0'. CopyFrom takes no value — the source IS another record's version, named by from_source= (and from= for a DIFFERENT record) — and it copies a WHOLE field (scalar, formlink, modeled list, sub-struct); it cannot copy owned child records (forward the whole record with " + ToolNames.Forward + " instead).")]
    public string? Op { get; init; }

    [JsonPropertyName("value"), Description("The value, coerced to the field's real type — a number, an enum name ('OneHanded'), or a FormID for a reference. Omit for Remove / ReplaceAll / Merge / compose / CopyFrom; on a Remove, omitting it whole-clears a NULLABLE field.")]
    public string? Value { get; init; }

    [JsonPropertyName("key"), Description("Dict key or list index at the leaf.")]
    public string? Key { get; init; }

    [JsonPropertyName("values"), Description("The whole new list for a list ReplaceAll.")]
    public string[]? Values { get; init; }

    [JsonPropertyName("entries"), Description("Key->value pairs for a dict Merge or dict ReplaceAll.")]
    public Dictionary<string, string>? Entries { get; init; }

    [JsonPropertyName("compose"), Description("Build a MODELED struct for an Add / InsertAtIndex / SetAtIndex, or a polymorphic Set — a leveled-list entry (e.g. 'LeveledItemEntry'), an effect, a condition row, or a polymorphic list element by its CONCRETE arm type (e.g. 'ScriptObjectProperty'). A VMAD script property: op=Add, field_path='VirtualMachineAdapter.Scripts[0].Properties', compose={type:'ScriptObjectProperty', fields:{Name:'MyProp', Flags:'Edited', Object:'XXXXXX:Plugin.esp', Alias:'-1'}}. Merging a weapon into a leveled list: op=Add, field_path='Entries', compose={type:'LeveledItemEntry', sets:[{path:'Data.Level',value:'1'},{path:'Data.Count',value:'1'},{path:'Data.Reference',value:'<weapon FormID>'}]}.")]
    public StructInput? Compose { get; init; }

    [JsonPropertyName("composes"), Description("Build MANY modeled list elements in ONE op — the batch sibling of compose, a LIST built in one op. With Add, appends each in order (a whole block of condition rows at once); with ReplaceAll, clears the list then appends each — the way to replace a whole modeled list (composes=[] with ReplaceAll clears it to empty). Mutually exclusive with compose/value/values.")]
    public StructInput[]? Composes { get; init; }

    [JsonPropertyName("from"), Description("op='CopyFrom' only: the SOURCE RECORD to copy the field from, as 'XXXXXX:Plugin.esp' — a DIFFERENT record from formid (SPEC §4.5's cross-record copy). Omit to copy this same record's version from another plugin (name it in from_source). Source and target must be the SAME record type — refused by name otherwise. What CopyFrom does and does not copy is on op=.")]
    public string? From { get; init; }

    [JsonPropertyName("from_source"), Description("op='CopyFrom' only: WHOSE version of the source record to copy — an ACTIVE plugin, or a plugin FILE on disk that isn't in the load order (a disabled old patch you want to re-assert a field from). With from= it defaults to the source record's load-order winner; without from= it is required (there is no other source to name).")]
    public string? FromSource { get; init; }

    /// <summary>NOT a wire member — <see cref="JsonIgnoreAttribute"/> keeps it out of the published schema and out of
    /// the strict reader's member set. It is how a zip-generated op remembers the caller's own spelling, so a refusal
    /// reads "assignments[0] x bundle[1]" instead of an op index that only exists after expansion.</summary>
    [JsonIgnore]
    public string? Origin { get; init; }
}

/// <summary>One pair of the <c>assignments=</c> zip: the record being written, the record its bundle is copied FROM,
/// and optionally the pole that source is read at. The join is a ZIP, never a product, so N targets never silently
/// fan out to N*N copies.</summary>
public sealed record Assignment
{
    [JsonPropertyName("target"), Description("The record being WRITTEN, as 'XXXXXX:Plugin.esp' — the §5.2 meaning of the bare word 'target': a copy's destination record.")]
    public string? Target { get; init; }

    [JsonPropertyName("from"), Description("The record the bundle is copied FROM, as 'XXXXXX:Plugin.esp'. Must be the same record type as target.")]
    public string? From { get; init; }

    [JsonPropertyName("from_source"), Description("Optional. WHOSE version of the source record to read — a plugin filename (active, or a file on disk out of the load order). Defaults to the source record's load-order winner.")]
    public string? FromSource { get; init; }
}
