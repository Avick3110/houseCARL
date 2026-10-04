using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using HousecarlCore;

namespace HousecarlMcp;

/// <summary>housecarl_apply — the field-write surface: the write verbs × the lane × transport, over
/// <see cref="LoadOrderService.ApplyEdits"/>, plus the <c>bundle=</c> × <c>assignments=</c> cross-record copy, whose
/// paths are caller data so the tool stays generic (the second cornerstone).</summary>
[McpServerToolType]
public static class ApplyTools
{
    [McpServerTool(Name = ToolNames.Apply, Title = "Edit record fields"),
     Description(
         "Edit fields on one or many existing records and write the result to a new patch plugin; originals are " +
         "untouched by default.\n\n" +
         "What to change: ops=, or a copy with bundle= and assignments=; both can go in one call. Where it lands: " +
         "a new patch (patch=), an existing houseCARL patch (into=), or a plugin's own file (in_place= with " +
         "acknowledge=). How it reads back: readback=, format=, max_chars=.\n\n" +
         "Every FormID this tool takes (formid, from, target, a field value) is 'XXXXXX:Plugin.esp': 6 hex digits, a " +
         "colon, the defining master's filename. The 8-digit runtime form the game and console print is refused " +
         "here; " + ToolNames.Records + " reads either form. ops= and assignments= also take \"@<absolute path>\" " +
         "naming a JSON file that holds the array, and bundle= takes [\"@<absolute path>\"]; the file is read when " +
         "the call runs.\n\n" +
         "Each edit overrides the record's load-order winner into the patch. All edits land in one .esp whose " +
         "masters cover every plugin the edits reference. If any op is malformed or fails pre-flight, the whole call " +
         "is refused with a reason per op and nothing is written.\n\n" +
         "New records are " + ToolNames.Create + "; dropping whole records is " + ToolNames.Remove + "; copying a " +
         "whole record verbatim is " + ToolNames.Forward + ". Read first with " + ToolNames.Records + ".")]
    public static string Apply(
        LoadOrderService svc,
        [Description("The edits, all into one plugin: [{formid, field_path, op?, value?, values?, key?, entries?, compose?, composes?, from?, from_source?}, …]. For a big job, write the ops to a manifest file, dry-run it, then apply it. The plugin write is atomic, so an interrupted call leaves the old file or the whole new one, though on the default lane it can leave the new mod folder holding only meta.ini, so a re-run is suffixed. If it may have landed, read the target before re-running, because a list Add or InsertAtIndex would apply twice and on the default lane a re-run writes a second, suffixed patch. Which ops a field takes follows its cardinality, so read that off the schema first.")]
            JsonElement? ops = null,
        [Description("Copy, with assignments=: the field paths copied for every pair, e.g. [\"BasicStats.Damage\", \"Keywords\"]. Only these fields are copied; the record's identity and every other field are untouched. There are no preset bundles (such as an appearance set); name the paths.")]
            string[]? bundle = null,
        [Description("Copy, with bundle=: [{target, from, from_source?}, …]. Each target is paired with its own source record, not with every source.")]
            JsonElement? assignments = null,
        [Description(LaneSentences.PatchDefault + LaneSentences.PatchSuffix)]
            string? patch = null,
        [Description(LaneSentences.IntoLead + "A record the patch already carries is edited as it stands in the patch; a record it does not carry is copied in from the load-order winner first. So to build on one plugin's version of a record that another plugin wins, " + ToolNames.Forward + " it from that plugin into the patch, then apply into= the same patch. " + LaneSentences.IntoFound)]
            string? into = null,
        [Description("Opt-in: the filename of an active plugin to edit in its own file instead of writing a patch" + LaneSentences.InPlaceAnyPlugin + LaneSentences.InPlaceRewrite + "The records you edit are verified; the rest is not.")]
            string? in_place = null,
        [Description(LaneSentences.Acknowledge)]
            bool acknowledge = false,
        [Description("Run the whole pipeline (winner resolve, pre-flight, every op applied in memory, the reference check) and stop before anything touches disk. Returns the would-be values and masters, or the refusal the real call would give. " + LaneSentences.DryRunLanes)]
            bool dry_run = false,
        [Description("Widen the read-back to every field of every record this call touched, not just the edited fields. " + LaneSentences.ReadbackIsTheFile)]
            bool readback = false,
        [Description("'text' (default) or 'json' (the same data). " + LaneSentences.Epoch)]
            string? format = null,
        [Description("Character limit on the reply: in json the applied-op rows and the read-back, in text the read-back. " + LaneSentences.MaxCharsCut + "; raise it for a readback=true dump.")]
            int max_chars = 0) => Guard.Tool(ToolNames.Apply, () =>
    {
        // ---- TRANSPORT: format --------------------------------------------------------------------------
        // Ahead of the unconfigured-MO2 prompt; contract in docs/architecture/write-path.md.
        bool json = Wire.WantsJson(format, out var ferr);
        if (ferr is not null) return ferr;   // the format value itself is unparsed — there is no known render to answer in
        if (svc.ConfigPromptOrNull() is { } prompt)
            return json ? JsonWire.RenderError(prompt, null) : prompt;

        string Refuse(string message) => json ? JsonWire.RenderError(message, null) : "error: " + message;

        // ---- LANE: the three destinations are mutually exclusive, and a dropped one is named ------------
        // A lane is honoured or refused BY NAME; contract in docs/architecture/write-path.md.
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

        // An explicitly EMPTY bundle= is a supplied parameter: presence on the ARRAY, emptiness on its own terms.
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
        // A rename over the same engine inputs: op -> verb, from_source -> the source plugin, while from (the source
        // RECORD) rides alongside. Mapping problems are collected all at once.
        var wire = new List<BulkOp>(edits.Count);
        var fromRecords = new List<string?>(edits.Count);
        var origins = new List<string?>(edits.Count);
        var problems = new List<string>();
        for (int i = 0; i < edits.Count; i++)
        {
            var e = edits[i];
            // A refusal names the caller's OWN spelling; contract in docs/architecture/write-path.md.
            var where = e.Origin ?? $"ops[{i}]";
            if (e.From is not null && !string.Equals(e.Op ?? "Set", "CopyFrom", StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"{where}: from= names the SOURCE RECORD of a copy and is only valid with op='CopyFrom' (got op='{e.Op ?? "Set"}').");
                continue;
            }
            // The same gate on the other half of the copy source, which would otherwise write off the winner.
            if (e.FromSource is not null && !string.Equals(e.Op ?? "Set", "CopyFrom", StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"{where}: from_source= names the PLUGIN a copy reads its source from and is only valid with op='CopyFrom' (got op='{e.Op ?? "Set"}').");
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
        // The lane the CALL named; contract in docs/architecture/write-path.md.
        return json
            ? JsonWire.RenderPatchOutcome(outcome, max_chars, readback, hasInPlace ? "in_place" : hasInto ? "into" : "patch")
            : WriteTools.Render(outcome, max_chars, readback);
    });

    // ---- input readers -------------------------------------------------------------------------------

    /// <summary>Read <c>ops=</c>: the inline JSON array, or either @file spelling, all strictly on one lane.</summary>
    static (ApplyOp[]? Items, string? Error) ReadOps(JsonElement el)
        => ListParams.Read<ApplyOp>(el, "ops", "{formid, field_path, op?, value?, values?, key?, entries?, compose?, composes?, from?, from_source?}");

    /// <summary>Read <c>assignments=</c>, the copy zip's per-target source mapping, exactly as <see cref="ReadOps"/> does.</summary>
    static (Assignment[]? Items, string? Error) ReadAssignments(JsonElement el)
        => ListParams.Read<Assignment>(el, "assignments", "{target, from, from_source?}");


    // ---- the copy zip --------------------------------------------------------------------------------

    /// <summary>Resolve <c>bundle=</c> to its field-path list, honoring the <c>["@&lt;path&gt;"]</c> spelling.</summary>
    static (IReadOnlyList<string>? Paths, string? Error) ReadBundlePaths(string[] bundle)
    {
        // A MIXED inline/@file list is refused, in ListParams.Read's own words: an "@path" beside real paths would
        // otherwise become a literal dotted FIELD path.
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

    /// <summary>Expand the copy zip into ops, one CopyFrom per assignment × bundle path; only pair-level shape is
    /// checked here, the rest being the engine's pre-flight. Contract in docs/architecture/write-path.md.</summary>
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
/// <c>verb</c> is <c>op</c> at the op level only, and the source plugin splits into <c>from</c> (the source RECORD)
/// and <c>from_source</c> (the pole it is read at).</summary>
public sealed record ApplyOp
{
    [SchemaRequired, JsonPropertyName("formid"), Description("The record to edit, as 'XXXXXX:Plugin.esp'.")]
    public string? Formid { get; init; }

    [SchemaRequired, JsonPropertyName("field_path"), Description("Dotted field path, e.g. 'BasicStats.Damage', 'Name', 'Keywords' or 'Entries'. Step into a list/dict element mid-path with brackets ('Effects[0].Data.Magnitude'); at the LEAF use op + key, not brackets.")]
    public string? FieldPath { get; init; }

    [SchemaValues(SchemaVocabulary.WriteVerbs), JsonPropertyName("op"), Description(WriteVerbs.AllRecital + ". SetAtIndex overwrites the element at key=; InsertAtIndex inserts a new one at key= and shifts the rest right (key = the list's length appends), e.g. adding an arm to an existing condition OR-group, where Add would put the row at the end as a separate AND-group. On a flags field (SPEL Flags, NPC Configuration.Flags, WEAP Data.Flags...) Add sets one bit and Remove clears one, leaving the other bits alone, while Set replaces them all; Set '0' clears every bit. CopyFrom takes no value: it copies a whole field (scalar, reference, modeled list, sub-struct) from the version named by from_source= (and from= for a different record); it cannot copy owned child records (use " + ToolNames.Forward + " on the whole record).")]
    public string? Op { get; init; }

    [JsonPropertyName("value"), Description("The value, coerced to the field's type: a number, an enum name ('OneHanded'), or a FormID for a reference. Omit for ReplaceAll, Merge, compose and CopyFrom, and for a Remove by key= or one that clears a whole nullable field.")]
    public string? Value { get; init; }

    [JsonPropertyName("key"), Description("Dict key or list index at the leaf.")]
    public string? Key { get; init; }

    [JsonPropertyName("values"), Description("The whole new list for a list ReplaceAll.")]
    public string[]? Values { get; init; }

    [JsonPropertyName("entries"), Description("Key->value pairs for a dict Merge or dict ReplaceAll.")]
    public Dictionary<string, string>? Entries { get; init; }

    [JsonPropertyName("compose"), Description("Build a modeled struct for an Add, InsertAtIndex or SetAtIndex, or a polymorphic Set: a leveled-list entry (e.g. 'LeveledItemEntry'), an effect, a condition row, or a polymorphic list element by its concrete type (e.g. 'ScriptObjectProperty'). A script property: op=Add, field_path='VirtualMachineAdapter.Scripts[0].Properties', compose={type:'ScriptObjectProperty', fields:{Name:'MyProp', Flags:'Edited', Object:'XXXXXX:Plugin.esp', Alias:'-1'}}. Merging a weapon into a leveled list: op=Add, field_path='Entries', compose={type:'LeveledItemEntry', sets:[{path:'Data.Level',value:'1'},{path:'Data.Count',value:'1'},{path:'Data.Reference',value:'<weapon FormID>'}]}.")]
    public StructInput? Compose { get; init; }

    [JsonPropertyName("composes"), Description("Build many modeled list elements in one op. With Add, appends each in order (e.g. a block of condition rows); with ReplaceAll, clears the list then appends each, and composes=[] clears it to empty. Pass only one of compose, composes, value and values.")]
    public StructInput[]? Composes { get; init; }

    [JsonPropertyName("from"), Description("op='CopyFrom' only: a different record to copy the field from, of the same record type as formid.")]
    public string? From { get; init; }

    [JsonPropertyName("from_source"), Description("op='CopyFrom' only: whose version of the source record to copy, an active plugin or a plugin file on disk outside the load order (e.g. a disabled old patch). Use it to base an edit on an authored plugin while a generated one wins the record. With from= it defaults to the source record's load-order winner; without from= it is required.")]
    public string? FromSource { get; init; }

    /// <summary>NOT a wire member — <see cref="JsonIgnoreAttribute"/> keeps it out of the published schema and the
    /// strict reader's member set; it is how a zip-generated op remembers the caller's own spelling.</summary>
    [JsonIgnore]
    public string? Origin { get; init; }
}

/// <summary>One pair of the <c>assignments=</c> zip: the record written, the record its bundle is copied FROM, and
/// optionally the pole that source is read at.</summary>
public sealed record Assignment
{
    [SchemaRequired, JsonPropertyName("target"), Description("The record being written, as 'XXXXXX:Plugin.esp'.")]
    public string? Target { get; init; }

    [SchemaRequired, JsonPropertyName("from"), Description("The record the bundle is copied from, of the same record type as target.")]
    public string? From { get; init; }

    [JsonPropertyName("from_source"), Description("Whose version of the source record to read: a plugin filename, active or a file on disk outside the load order. Defaults to the source record's load-order winner.")]
    public string? FromSource { get; init; }
}
