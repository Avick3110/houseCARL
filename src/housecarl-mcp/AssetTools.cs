using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace HousecarlMcp;

/// <summary>Read-only asset resolution: which mod or BSA provides a Data-relative path, and which copy wins in
/// game. Precedence, archive discovery and the zero-handles rule are in docs/architecture/assets.md.</summary>
[McpServerToolType]
public static class AssetTools
{
    [McpServerTool(Name = ToolNames.AssetStatus, ReadOnly = true, Title = "Asset status — which mod/BSA wins, where a file lives, what a mod loses"),
     Description(
         "Which mod wins a file, where a file lives, and what one mod provides, wins and loses, through MO2's " +
         "virtual file system, for any tree (meshes, textures, scripts, sound, interface, SKSE/Plugins, " +
         "CalienteTools/BodySlide): the winner, every source that provides " +
         "it (loose mods, overwrite, the game Data folder, active BSAs), whether more than one source contends, and whether " +
         "it is absent. Precedence: loose beats BSA; among loose files the higher-priority mod (then overwrite) wins; " +
         "among BSAs the later-loaded plugin's archive wins. The file-layer counterpart to a record's load-order " +
         "winner; answers 'why isn't my override applying'. Use this instead of searching mods/ on disk, which misses " +
         "BSAs and MO2's priority; for SKSE plugins, which load only loose at the top level, use housecarl_skse. " +
         "Select with asset_paths=, under= (a folder, or a glob: 'meshes/**/*fur*.nif'; '**/*goldbrand*' finds a " +
         "name in every folder), formids= (NPC FaceGen pairs) and provider= (alone, every file one mod or archive " +
         "ships; with under=, only its files beneath those; beside asset_paths= or formids= with no under=, only those rows; with under= too, both), each row marked " +
         "wins, shadowed by the winner, holding it hidden by MO2, or not provided. An archive that cannot be read, or a " +
         "missing Skyrim.ini base-archive list, is reported, so an 'absent' answer is never silently trusted. A call " +
         "that would resolve more than 1,200,000 paths is refused before it resolves any; with limit=, the bound " +
         "counts only the window, except under to_file=, counts_only= and walk=true, which resolve the whole selection. A loose " +
         "file MO2 hides (the instance's skip lists, by default a mod's .mohidden name or .git folder; never in the game's Data folder), or x " +
         "when a mod holds only x.mohidden, provides nothing: the row says hidden by MO2 with the rule and the " +
         "holder, absent unless another layer's copy wins. A loose root meta.ini, readme.txt or fomod folder is " +
         "hidden too; below the root, or in an archive, it is a normal file. " +
         "Read-only.")]
    public static string AssetStatus(
        LoadOrderService svc,
        [Description("Data-relative asset path(s), e.g. 'textures/armor/iron/cuirass_1.dds'. Results come back in " +
                     "the order given. Forward or back slashes; a drive-rooted or '..'-escaping path is an error on " +
                     "its own row, not a failed call. Optional when under= or formids= is given. Takes " +
                     "[\"@<absolute path>\"] in place of the list: a plain file, one path per line, or a to_file= " +
                     "artifact from this tool (identity column 'path'). A path list is not epoch-checked, so a saved " +
                     "sweep re-enters after the order changes.")]
            ListArg? asset_paths = null,
        [Description("Optional. Data-relative directory or glob selector(s): every file the load order provides " +
                     "beneath it, loose and BSA, is resolved, leaving out a loose file MO2 hides (a root meta.ini, " +
                     "readme.txt or fomod folder in any root, Data included, or a skip-list match in a mod or " +
                     "overwrite), e.g. " +
                     "'meshes/actors/character/facegendata/facegeom/Skyrim.esm' for one master's facegen set. '*' " +
                     "matches within one path segment, '?' one character, '**' across separators: " +
                     "'textures/actors/character/**/*.dds'. A glob needs no folder in front: '**/*goldbrand*' finds " +
                     "every file whose name holds 'goldbrand', in any folder, while '*goldbrand*' reads the Data root " +
                     "only. Matches come after the asset_paths and formids= rows, sorted, duplicates dropped. A " +
                     "selector that matches nothing says so. With provider=, only that provider's files are walked." + ListText.Paths)]
            ListArg? under = null,
        [Description("Optional. NPC FormIDs ('XXXXXX:Plugin.esp', the defining master's filename). Each contributes " +
                     "both halves of its FaceGen pair, " +
                     "'Meshes\\actors\\character\\facegendata\\facegeom\\<master>\\00<6hex>.nif' and " +
                     "'Textures\\...\\facetint\\<master>\\00<6hex>.dds' (Mutagen's spelling), as two rows, each naming the other half's " +
                     "winner beside its own and flagging when both win from different mods, the usual dark-face cause " +
                     "(housecarl_check findings=[\"facegen\"] separates a real split from one product spread over " +
                     "two folders). That is judged on the winning MO2 layer, so two archives of one mod are not " +
                     "flagged, nor are two files both in the game's Data folder or both in overwrite. The pair is " +
                     "the one every FaceGen tool names: the NPC record is read, a Traits-templated NPC gives its " +
                     "template root's pair (a leveled template each member's, marked one of N), and an NPC with no " +
                     "pair (a race without FaceGenHead, ACBS flag 0x100, the Traits flag with no Template, a FormID " +
                     "no plugin defines) is an error row with the reason, one per reason: a leveled template's " +
                     "pairless member or broken chain entry is its own row, beside the other members' pairs. The folder is the defining master, never " +
                     "the conflict winner. One half winning nowhere is also a dark face: the row shows it ABSENT, but " +
                     "the json's pair.differs and the to_file= pair_differs flag only two present halves with " +
                     "different owners, so a filter on that field misses it. A malformed FormID is one error row, " +
                     "not a failed call. Takes [\"@<absolute path>\"]: a " +
                     "plain list file, or a housecarl_records artifact whose 'formid' column becomes the list " +
                     "(epoch-checked against the current build); records types=[\"NPC_\"] to_file= then " +
                     "formids=[\"@<that file>\"] sweeps the whole order.")]
            ListArg? formids = null,
        [Description("Optional. One provider, as a row's providers line spells it: an enabled mod folder (its loose " +
                     "files and the active archives it ships), 'overwrite', 'Data' (the game's Data folder: its loose " +
                     "files and the base-game archives), or one archive's filename " +
                     "('Foo - Textures.bsa'). Alone, selects every file it provides; with under= every file it provides " +
                     "beneath those selectors; with asset_paths= or formids= those rows too, and with no under= only those rows. Marks every row with the provider's standing: wins, shadowed by " +
                     "the winning copy, holds it hidden by MO2 (so provides nothing), or does not provide this path. Under counts_only=true the census adds its " +
                     "provides / wins / shadowed counts. The standing matches by name, so an archive's copy is shadowed " +
                     "by its own mod's loose copy; the census tables count by MO2 layer, the mod an archive belongs to. A name " +
                     "that is not in the active order is refused.")]
            string? provider = null,
        [Description("Optional. On every .nif row that resolves, list the files the winning copy names, each with the " +
                     "Data path it maps to and that path's winner, each file once at its first site (one line per kind " +
                     "and stored string, matched ignoring case): texture-set slots, effect-shader textures, and the root node's " +
                     "'HDT Skinned Mesh Physics Object' (the HDT-SMP physics XML, Data-relative) and 'BODYTRI' " +
                     "(meshes-relative) string extra data. With provider=, the links are still the winning copy's, " +
                     "and the heading names it. A string that names no Data path says why. " + ToolNames.NifInspect +
                     " sections=paths lists every site. A " +
                     "to_file= or spilled file carries the same links on each row; refused with counts_only=, and " +
                     "with walk=true, which already follows these files as nodes.")]
            bool links = false,
        [Description("Optional. Walk from the selected paths and records= to every file they reach, returning one node " +
                     "per file: the paths a record lists, its implicit files (an NPC's FaceGen head and tint, or its " +
                     "Traits template root's, each member of a leveled template marked one of N; .pex scripts, fragment " +
                     ".pex files, a start-game-enabled quest's Seq\\<plugin>.seq; a dialogue line's voice .fuz, or the " +
                     ".xwm/.wav and .lip it ships as; the lines an NPC speaks, as their Speaker or by a GetIsID on the Subject (not a line tied to it through a quest alias), in the active order or a from_source= plugin outside it, by their voice), then the links each file names (a mesh's textures, SMP XML and " +
                     "BODYTRI, a script's parent .pex), recursively. Each node is marked from-source (source_provider= " +
                     "ships it), elsewhere (another provider wins it, named), present, or absent; keyed nodes sit at a " +
                     "FormID-named path, so a renumbering copy carries them rather than placing them. A formids= " +
                     "seed, or a root, with no FaceGen pair is a note with the reason, not a node. With " +
                     "source_provider= shipping any node, the node list is followed by those nodes as a housecarl_place assets= " +
                     "array (counts_only=: their count only; none ships: no such line, and the count reads from_source=0). " +
                     "Records are entered through form links only inside the from_source= plugins and, for a seed read " +
                     "through 'winner', its own plugin; links into other plugins are counted as boundaries. A walk that " +
                     "enters any NPC builds the active order's reverse-reference index, once its records are walked, to find its lines, which takes " +
                     "time on a large order. The walk always runs whole: limit= and offset= window only the nodes " +
                     "listed (the header, notes, standing counts and place set still cover every node, and a paged " +
                     "answer ends with the [accounting] line); to_file= writes every node to the file and lists " +
                     "none (the header, notes, counts, place set and the file it wrote still print); counts_only= returns counts per edge and per standing, no nodes. Not with links=.")]
            bool walk = false,
        [Description("Optional, with walk=true. Record FormIDs ('XXXXXX:Plugin.esp') the walk starts from." + ListText.FormIds)]
            ListArg? records = null,
        [Description("Optional, with walk=true. The ordered source list each record is read from, first hit wins: " +
                     "'winner' (default) or a plugin filename, active or a file in a disabled mod folder. A records= " +
                     "seed and a formids= seed's NPC, its template and race are read through it; a walk with " +
                     "neither seed, or a call with no walk, reads nothing through it and says so in a note." + ListText.Plugins)]
            ListArg? from_source = null,
        [Description("Optional, with walk=true. The provider the walk reads files from first: a mod folder, enabled or " +
                     "not (the donor), 'overwrite', 'Data' or an active archive's filename. Each node it ships is from-source, " +
                     "except a loose file MO2 hides, which is absent and says so; " +
                     "one it does not is elsewhere or absent. Omitted, or '" + AssetSourceChoice.WinnerToken + "', names no " +
                     "source: each node is present or absent. Without walk=true it is not used, and a note says so.")]
            string? source_provider = null,
        [Description("Optional. Max paths to resolve and render. 0 = no limit. to_file= ignores it and covers the " +
                     "whole selection. Under counts_only=true it caps each census table's rows instead; the census " +
                     "still covers the whole selection. Under walk=true, max nodes to render; the walk still runs whole.")]
            int limit = 0,
        [Description("Optional. Where the rendered window starts in the selection (under walk=true, in the walk's " +
                     "nodes), for paging. 0 = the beginning.")]
            int offset = 0,
        [Description("Optional. Write the complete result to this absolute .jsonl path (line 1 = manifest) and " +
                     "render only the manifest inline, as housecarl_records and housecarl_check do. One row per " +
                     "resolved path: the winner, loose or BSA (and which archive), the whole provider chain, and on " +
                     "a formids= row the formid, the slot, the paired path, its winner, whether the two differ, and " +
                     "face_note (the template root whose pair the row is, null for the NPC's own). The file covers the " +
                     "whole selection: limit= does not narrow it and row_count equals total. Re-enter it via " +
                     "asset_paths=[\"@<path>\"]; its identity column is 'path', so it is not a formids= list. Under " +
                     "walk=true, one row per node in walk order, the same rows a spilled walk writes.")]
            string? to_file = null,
        [Description("Optional. Return a census instead of path rows: which MO2 layers win how many paths, which " +
                     "layers provide a copy that another layer wins over, how the winners split between loose and " +
                     "BSA, and how many are absent. It covers the whole selection; limit= caps its tables' rows. " +
                     "Under walk=true, the walk's header, notes and counts per edge and per standing instead.")]
            bool counts_only = false,
        [Description("Optional. 'text' (default) | 'json' (the same data, machine-readable, accounting in-band).")]
            string? format = null,
        [Description("Optional. Character ceiling on the whole response; the path whose block would cross it is not written. What it holds back is not lost: the resolved result (under limit=, the window; under walk=true, every node, whatever the window) is written whole to a file in the server's results directory, and the response names it. counts_only= spills nothing: a cut census says how many table rows it held back (layer rows; under walk=true, edge and standing rows); raise max_chars or page the table with limit=. On a path-only sweep the first spill also builds the record index, which takes time on a large order. The alarms and the accounting line are always inside the ceiling; a cap too small for them says so and names the cap that clears it. 0 = the server default (~40k).")]
            int max_chars = 0) => Guard.Tool(ToolNames.AssetStatus, call =>
    {
        // format first, so the unconfigured-MO2 prompt answers a json caller as a document.
        bool json = Wire.WantsJson(format, out var ferr);
        if (ferr is not null) return ToolAnswer.Refusal(ferr);
        if (svc.ConfigPromptOrNull() is { } prompt)
            return ToolAnswer.Refusal(json, prompt);
        // The read/write surface's one refusal shape: ToolAnswer.Refusal strips the prefix on the json document.
        ToolAnswer Refuse(string message) => ToolAnswer.Refusal(json, Wire.RefusalPrefix + message);

        // Every list through the one door, each taking what its own entries are made of.
        var pathsIn = asset_paths.Read("asset_paths", ListKind.Path);
        var underIn = under.Read("under", ListKind.Path);
        var idsIn = formids.Read("formids", ListKind.FormId);
        var recordsIn = records.Read("records", ListKind.FormId);
        var sourcesIn = from_source.Read("from_source", ListKind.Plugin);
        foreach (var listErr in new[] { pathsIn.Error, underIn.Error, idsIn.Error, recordsIn.Error, sourcesIn.Error })
            if (listErr is not null) return ToolAnswer.Refusal(json, listErr);
        var underTokens = underIn.Tokens;

        if (recordsIn.Tokens is { Length: > 0 } && !walk)
            return Refuse("records= seeds the walk — pass walk=true with it.");
        if (!walk && pathsIn.Tokens is not { Length: > 0 } && underTokens is not { Length: > 0 }
            && idsIn.Tokens is not { Length: > 0 } && string.IsNullOrWhiteSpace(provider))
            return Refuse("asset_paths, under, formids and provider are all empty. Pass Data-relative asset path(s) in " +
                          "asset_paths (e.g. 'textures/armor/iron/cuirass_1.dds'), a Data-relative directory or glob " +
                          "in under (e.g. 'meshes/actors/character/facegendata/facegeom/Skyrim.esm'), NPC " +
                          "FormID(s) in formids (e.g. '01A51A:Dawnguard.esm') for their FaceGen pairs, or a mod or " +
                          "archive name in provider for every file it ships.");
        // The one set of transport checks, both lanes. The window's own refusal, from the window: this tool and housecarl_skse answer the same input class.
        var window = new RowWindow(offset, limit);
        if (window.Error is { } bad) return ToolAnswer.Refusal(json, bad);
        int cap = Wire.Cap(max_chars);

        var toFile = to_file?.Trim();
        bool wantFile = toFile is { Length: > 0 };
        if (wantFile)
        {
            // The same validator the records surface runs: absolute, .jsonl, and outside the pruned results directory.
            if (Artifacts.ValidateToFile(toFile!, svc.ResultsDir) is { } verr) return ToolAnswer.Refusal(json, verr);
            // The same pair the records lanes refuse, in the same words: one returns the census, the other writes the rows.
            if (counts_only) return ToolAnswer.Refusal(json, Artifacts.CountsOnlyWithToFile);
            if (offset > 0)
                return ToolAnswer.Refusal(json, "error: to_file= captures the COMPLETE result (the artifact is never a " +
                                         "window), so offset= has nothing to page — drop offset=.");
        }
        if (counts_only && links)
            return ToolAnswer.Refusal(json, "error: links= lists each mesh's files on its row, and counts_only= returns a census " +
                                     "with no rows — drop one of them.");
        // A census covers the whole selection, so there is no selection window for offset= to move.
        if (counts_only && offset > 0)
            return ToolAnswer.Refusal(json, "error: " + ReadSentences.NoOffsetOnCountTable("counts_only="));
        if (walk && links) return ToolAnswer.Refusal(json, ReadSentences.WalkWithLinks);
        // to_file= covers the whole walk, so limit= is ignored under it, as on the row lane.
        if (walk) return Walk(svc, pathsIn, underTokens, idsIn, provider, recordsIn, sourcesIn.Tokens, source_provider,
                              wantFile ? RowWindow.All : window, wantFile ? toFile : null, counts_only, json, cap);

        // The walk's own inputs, given without it, change nothing here: said, not dropped.
        var callNotes = new List<string>();
        if (sourcesIn.Tokens is { Length: > 0 }) callNotes.Add(ReadSentences.WalkOnlyInput("from_source="));
        if (!string.IsNullOrWhiteSpace(source_provider)) callNotes.Add(ReadSentences.WalkOnlyInput("source_provider="));

        var (pathTokens, _, pathEcho, _) = pathsIn;
        var (idTokens, idDemand, idEcho, _) = idsIn;

        // The FormID door parses the plugin-qualified form without touching the index; AssetStatus then reads each NPC, its templates and races.
        var door = FormIdDoor.For(svc);
        var seeds = new List<FaceGenSeed>(idTokens?.Length ?? 0);
        foreach (var raw in idTokens ?? Array.Empty<string>())
        {
            // Every token that is not a FormID answers as ONE error row, whatever the door threw: a runtime FormID on an order that cannot build would otherwise escape to Guard.
            try { seeds.Add(new FaceGenSeed(raw, door.Parse(raw), null)); }
            catch (Exception ex)
            {
                seeds.Add(new FaceGenSeed(raw, null, $"not a FormID: {Guard.Flatten(ex.Message)} Expected 'XXXXXX:Plugin.esp'."));
            }
        }

        // A FORMIDS artifact is epoch-checked against the build answering now. A PATHS artifact is not, deliberately: a
        // path is a string answered live off the VFS, and gating it would refuse the sweep-fix-re-ask loop this exists
        // for. The stamp is captured only where something needs it, and it is allowed to FAIL — every row is read off
        // the VFS, so an order whose plugins do not resolve must not stop the call.
        OrderStamp? order = null;
        string? noEpochBecause = null;
        if (wantFile || idDemand is not null) (order, noEpochBecause) = TryStamp(svc);
        if (idDemand is { } demand)
        {
            if (noEpochBecause is not null)
                return ToolAnswer.Refusal(json, $"error: formids= artifact '{demand.Path}' was captured at epoch={demand.Epoch}, " +
                                         $"and this call could not build a load order to check it against — {noEpochBecause} " +
                                         "Fix the order and retry; there is deliberately no unchecked re-entry. A list " +
                                         "of PATHS re-enters through asset_paths= without a build, because a path " +
                                         "answers off the VFS alone.");
            if (demand.Epoch != order!.Epoch)
                return ToolAnswer.Refusal(json, "error: " + RecordReads.ArtifactEpochMismatch(demand, order.Epoch));
        }

        // counts_only= resolves the WHOLE selection, as to_file= does: a census of a window would answer about a window while the rows that need paging had no knob.
        var data = svc.AssetStatus(pathTokens ?? Array.Empty<string>(), underTokens, counts_only ? 0 : limit, offset, seeds,
                                   wholeSelection: wantFile || counts_only, provider: provider, links: links);
        // The declared-cost refusal (the selection was counted and is past the bound) or an unknown provider: nothing was resolved.
        if (data.Refusal is { } refused) return ToolAnswer.Refusal(json, refused);
        if (callNotes.Count > 0) data = data with { Notes = callNotes };

        if (counts_only)
            return ToolAnswer.Served(json ? JsonWire.RenderAssetCensus(data, cap, limit) : AssetCensus.Render(data, cap, limit));

        KeyValuePair<string, string>[] Echo()
        {
            var e = new List<KeyValuePair<string, string>>
            {
                new("asset_paths", pathEcho ?? $"{pathTokens?.Length ?? 0} inline path(s)"),
                new("under", underIn.EchoSource ?? (underTokens is { Length: > 0 } ? string.Join(",", underTokens) : "<none>")),
                new("formids", idEcho ?? $"{seeds.Count} inline formid(s)"),
                new("provider", data.Provider ?? "<none>"),
                new("read_incomplete", data.ReadIncomplete ? "true" : "false"),
                new("discovery_warnings", data.Warnings.Count.ToString()),
            };
            // WHICH rows the file holds, in the records lane's spelling — a re-read months later has no conversation to recover it from. Never on the to_file= arm, which is never a window.
            if (!wantFile && data.Selected != data.Results.Count)
                e.Add(new("window", data.Results.Count == 0
                    ? $"window: no rows — offset={data.Offset} is past the end of the {data.Selected}-path selection"
                    : $"window: rows {data.Offset + 1}–{data.Offset + data.Results.Count} of {data.Selected} (limit={data.Limit}, offset={data.Offset})"));
            return e.ToArray();
        }

        if (!wantFile)
        {
            string Inline(SpillState? sp, out bool cut) => json
                ? JsonWire.RenderAssetStatus(data, cap, sp, out cut)
                : AssetWire.Render(data, cap, sp, out cut);

            var rendered = Inline(null, out var truncated);
            if (!truncated) return ToolAnswer.Served(rendered);
            // SPEC §2.1.1: an over-ceiling read result is written whole to the results directory and the response names the
            // file. The stamp is taken only here, so an ordinary sweep still builds no record index.
            if (order is null && noEpochBecause is null) (order, noEpochBecause) = TryStamp(svc);
            using var reservation = ResultsStore.Reserve(svc.ResultsDir, ToolNames.AssetStatus, order?.Epoch ?? "none");
            var (auto, autoErr) = AssetArtifact.Write(data, reservation, "ceiling", order, Echo(), noEpochBecause);
            return ToolAnswer.Served(Inline(autoErr is null ? SpillState.Spilled(auto!, manifestOnly: false) : SpillState.WriteFailed(autoErr), out _));
        }

        var (spill, artErr) = AssetArtifact.Write(data, ArtifactTarget.Named(toFile!), "to_file", order, Echo(), noEpochBecause);
        if (artErr is not null) return ToolAnswer.Refusal(json, "error: " + artErr);
        var manifestOnly = AssetArtifact.RenderManifestOnly(data, spill!, json, cap);
        // The text lane's ceiling arm; the json document caps itself as it writes.
        return ToolAnswer.Served(json ? manifestOnly : RenderCap.Settle(manifestOnly, cap));
    });

    /// <summary>The order's stamp for an artifact, or why it could not be read. What READING the order can throw, and
    /// nothing else: a held profile file, a denied read, an order with no active plugins; anything else is a bug.</summary>
    static (OrderStamp? Order, string? NoEpochBecause) TryStamp(LoadOrderService svc)
    {
        try { return (svc.CaptureView().Stamp, null); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { return (null, Guard.Flatten(ex.Message)); }
    }

    /// <summary>The walk=true lane: the selection's paths and the records= keys seed <see cref="LoadOrderService.AssetWalk"/>.
    /// The tool's transport checks have run: <paramref name="window"/> is legal, <paramref name="toFile"/> validated.</summary>
    static ToolAnswer Walk(LoadOrderService svc, ListRead pathsIn, string[]? under, ListRead idsIn, string? provider,
                       ListRead recordsIn, string[]? from_source, string? source_provider,
                       RowWindow window, string? toFile, bool countsOnly, bool json, int cap)
    {
        ToolAnswer Refuse(string message) => ToolAnswer.Refusal(json, Wire.RefusalPrefix + message);
        // Each FormID artifact, formids= and records= alike, is epoch-checked the same way the row lane checks one.
        var demands = new[] { idsIn.Demand, recordsIn.Demand }.OfType<HousecarlCore.ArtifactDemand>().ToList();
        if (demands.Count > 0 && svc.CaptureView().Stamp.Epoch is var epoch)
            foreach (var demand in demands)
                if (demand.Epoch != epoch)
                    return ToolAnswer.Refusal(json, "error: " + RecordReads.ArtifactEpochMismatch(demand, epoch));
        var asset_paths = pathsIn.Tokens;
        var formids = idsIn.Tokens;
        var door = FormIdDoor.For(svc);
        var keys = new List<Mutagen.Bethesda.Plugins.FormKey>();
        foreach (var raw in recordsIn.Tokens ?? Array.Empty<string>())
        {
            try { keys.Add(door.Parse(raw)); }
            catch (Exception ex) { return Refuse($"records '{raw}' is not a FormID: {Guard.Flatten(ex.Message)} Expected 'XXXXXX:Plugin.esp'."); }
        }
        var seeds = new List<FaceGenSeed>();
        foreach (var raw in formids ?? Array.Empty<string>())
        {
            try { seeds.Add(new FaceGenSeed(raw, door.Parse(raw), null)); }
            catch (Exception ex) { return Refuse($"formids '{raw}' is not a FormID: {Guard.Flatten(ex.Message)}"); }
        }
        var paths = new List<string>();
        var keyed = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        List<string>? selectorNotes = null;
        if (asset_paths is { Length: > 0 } || under is { Length: > 0 } || seeds.Count > 0 || !string.IsNullOrWhiteSpace(provider))
        {
            // formids= seeds read their face through from_source=, as records= seeds do.
            var sel = svc.AssetStatus(asset_paths ?? Array.Empty<string>(), under, 0, 0, seeds, wholeSelection: true, provider: provider,
                                      faceSources: from_source is { Length: > 0 } ? new FaceSources(from_source, "from_source", AssetLayers.RecordSourcesUnset) : null);
            if (sel.Refusal is { } refused) return ToolAnswer.Refusal(json, refused);
            selectorNotes = sel.SelectorNotes?.ToList() ?? new List<string>();
            foreach (var r in sel.Results)
            {
                // A formids= NPC with no FaceGen pair is a note with its reason, not a seed path.
                if (r.FormId is not null && r.Error is not null) { selectorNotes.Add($"formids '{r.FormId}': {r.Error}"); continue; }
                // A bad explicit path stays a seed, so the walk answers it as its own error node.
                paths.Add(r.RelPath);
                // A formids= row's path is named by the root NPC's id, so a renumbering copy carries it.
                if (r.Slot is not null) keyed[r.RelPath] = r.FaceNote;
            }
        }
        var w = svc.AssetWalk(paths, keys, from_source, source_provider, keyed);
        if (w.Refusal is { } wr) return ToolAnswer.Refusal(json, wr);
        // from_source= is read only by a record seed; with none it is said, not dropped.
        if (from_source is { Length: > 0 } && recordsIn.Tokens is not { Length: > 0 } && formids is not { Length: > 0 })
            w = w with { Notes = w.Notes.Prepend(ReadSentences.FromSourceWithoutRecordSeed).ToList() };
        // counts_only= refuses offset= and to_file= at the front door, so the window is limit= alone, capping the tables.
        if (countsOnly) return ToolAnswer.Served(AssetWalkWire.RenderCounts(w, selectorNotes, cap, json, window.Limit));

        // The seeds given, so a file re-read later says what was walked: asset_paths= and formids= by their list's source, else counted; under= as its tokens; provider= trimmed; records= by its list's source, else its tokens.
        var seedEcho = new List<KeyValuePair<string, string>>();
        if (asset_paths is { Length: > 0 }) seedEcho.Add(new("asset_paths", pathsIn.EchoSource ?? $"{asset_paths.Length} inline path(s)"));
        if (under is { Length: > 0 }) seedEcho.Add(new("under", string.Join(",", under)));
        if (formids is { Length: > 0 }) seedEcho.Add(new("formids", idsIn.EchoSource ?? $"{formids.Length} inline formid(s)"));
        if (!string.IsNullOrWhiteSpace(provider)) seedEcho.Add(new("provider", provider.Trim()));
        if (recordsIn.Tokens is { Length: > 0 } recs) seedEcho.Add(new("records", recordsIn.EchoSource ?? string.Join(",", recs)));

        if (toFile is not null)
        {
            var (fOrder, fNoEpoch) = TryStamp(svc);
            var (file, fileErr) = AssetWalkWire.Write(w, ArtifactTarget.Named(toFile), "to_file", fOrder, fNoEpoch, seedEcho);
            if (fileErr is not null) return ToolAnswer.Refusal(json, "error: " + fileErr);
            return ToolAnswer.Served(AssetWalkWire.Render(w, selectorNotes, cap, json, SpillState.Spilled(file!, manifestOnly: true), window, out _));
        }
        var rendered = AssetWalkWire.Render(w, selectorNotes, cap, json, null, window, out var cut);
        if (!cut) return ToolAnswer.Served(rendered);
        // SPEC §2.1.1, as the row lane does: the whole walk to the results directory, the response naming it.
        var (order, noEpochBecause) = TryStamp(svc);
        using var reservation = ResultsStore.Reserve(svc.ResultsDir, ToolNames.AssetStatus, order?.Epoch ?? "none");
        var (spill, spillErr) = AssetWalkWire.Write(w, reservation, "ceiling", order, noEpochBecause, seedEcho);
        return ToolAnswer.Served(AssetWalkWire.Render(w, selectorNotes, cap, json,
                                    spillErr is null ? SpillState.Spilled(spill!, manifestOnly: false) : SpillState.WriteFailed(spillErr), window, out _));
    }
}

/// <summary>Renders <see cref="AssetStatusData"/>: the build-level alarms first, then one block per queried path —
/// winner and every provider in precedence order. Contention is worded neutrally; the list is bounded by max_chars.</summary>
static class AssetWire
{
    /// <summary>The one header line both renders open with: the profile, and how many paths the SELECTION named.</summary>
    internal static string Header(AssetStatusData d) =>
        new StringBuilder("asset status — profile '")
            .Append(d.ProfileName.Length > 0 ? d.ProfileName : "(unconfigured)")
            .Append("'  (").Append(d.Selected).Append(" path").Append(d.Selected == 1 ? "" : "s")
            .Append(" selected").Append(d.Provider is { } p ? ", provider \"" + p + "\"" : "").Append(')').ToString();

    public static string Render(AssetStatusData d, int cap) => Render(d, cap, null, out _);

    /// <summary><paramref name="spill"/> is this call's artifact disposition, charged before the first path; <paramref name="truncated"/> is what the caller auto-spills on.</summary>
    public static string Render(AssetStatusData d, int cap, SpillState? spill, out bool truncated)
    {
        var header = Header(d);

        var spillText = Wire.SpillText(spill);
        var body = BatchRender.Render(
            header, d.Results, "path(s)", cap,
            // Alarms come before the per-path list so a long batch cannot truncate them away.
            (sb, room) =>
            {
                BatchRender.AppendReadFailures(sb, d.BsaFailures, "an asset", room);
                BatchRender.AppendRootFailures(sb, d.RootFailures, "an asset", room);
                BatchRender.AppendDiscoveryWarnings(sb, d.Warnings, room);
                AppendSelectorNotes(sb, d.SelectorNotes, room);
                AppendNotes(sb, d.Notes, room);
            },
            (sb, r, _) => AppendPath(sb, r, d),
            out int rendered,
            // The accounting block is priced INSIDE max_chars, the way the check sweep's footer is, so max_chars means the same on this tool as on every other.
            reserve: AccountingReserve(d) + spillText.Length);

        var counts = Tally(d, rendered);
        truncated = counts.Truncated > 0;
        return RenderCap.Settle(body + TransportAccounting.Compose(counts, RowNoun, everySentence: false) + spillText, cap);
    }

    /// <summary>What this family's accounting counts.</summary>
    const string RowNoun = "path(s)";

    /// <summary>What each under= selector had to say for itself, above the per-path list so a truncated sweep
    /// cannot cut it away. Capped like its sibling alarm blocks, because the input can be thousands of selectors.</summary>
    internal static void AppendSelectorNotes(StringBuilder sb, IReadOnlyList<string>? notes, RenderCap cap)
    {
        if (notes is not { Count: > 0 }) return;
        // The heading carries the count and is written whatever the budget: a selector that matched nothing must not vanish into a render that then reads as complete.
        sb.Append("\n[!] under (").Append(notes.Count).Append("):\n");
        BatchRender.AppendLines(sb, notes, "selector(s)", cap);
    }

    /// <summary>The call's own notes, beside the selector notes and not under their heading; nothing when there are none.</summary>
    internal static void AppendNotes(StringBuilder sb, IReadOnlyList<string>? notes, RenderCap cap)
    {
        if (notes is not { Count: > 0 }) return;
        sb.Append("\n[!] notes (").Append(notes.Count).Append("):\n");
        BatchRender.AppendLines(sb, notes, "note(s)", cap);
    }

    /// <summary>What this response actually did, in the shared TRANSPORT vocabulary: the selection total, the window rendered, and the four distinct omissions.</summary>
    internal static TransportCounts Tally(AssetStatusData d, int rendered) =>
        TransportAccounting.Tally(d.Selected, d.Results.Count, rendered, new RowWindow(d.Offset, d.Limit), d.NoteCount);

    /// <summary>The widest counts this response could state — what the json lane's tail reserve measures.</summary>
    internal static TransportCounts Widest(AssetStatusData d) =>
        TransportAccounting.Widest(d.Selected, d.Results.Count, new RowWindow(d.Offset, d.Limit), d.NoteCount);

    /// <summary>The chars held back from max_chars so the accounting block is always affordable.</summary>
    internal static int AccountingReserve(AssetStatusData d) =>
        TransportAccounting.Reserve(d.Selected, d.Results.Count, new RowWindow(d.Offset, d.Limit), d.NoteCount, RowNoun);

    static void AppendPath(StringBuilder sb, AssetPathResult r, AssetStatusData d)
    {
        sb.Append('\n').Append(r.RelPath);
        // Only on a row the formids= SELECT derived, so a plain path block is byte-for-byte the block it always was.
        if (r.FormId is not null)
        {
            sb.Append("   (").Append(r.FormId);
            if (r.Slot is { } s) sb.Append(", facegen ").Append(FaceGenPath.Token(s));
            sb.Append(')');
        }
        sb.Append('\n');
        if (r.FaceNote is { } face) sb.Append("  face: ").Append(face).Append('\n');

        if (r.Error is not null)                                // a rejected path: drive-rooted, or escaping with '..'
        {
            sb.Append("  error: ").Append(r.Error).Append('\n');
            return;
        }

        var hit = r.Hit!;
        if (!hit.Exists)
        {
            sb.Append("  ABSENT — ").Append(AbsentWords.Why(hit.Hidden)).Append('\n');
            // Each suggestion was verified by re-resolving its Data form (root added or data\ dropped). Backticks, not single quotes — an asset path can carry the author's own apostrophes.
            if (r.PrefixSuggestions is { Count: > 0 } sug)
                sb.Append("  did you mean ").Append(string.Join(" or ", sug.Select(s => "`" + s + "`")))
                  .Append(sug.All(s => AssetPathHint.AddsRootOnly(r.RelPath, s))
                      ? "?  (a path read off a record is relative to its root folder, not to Data)\n"
                      : "?  (" + AssetPathHint.DataPrefixSentence + ")\n");
            // Both incomplete-scan conditions hedge an ABSENT at the point of use, not only in the top-of-output note: the asset could exist where we did not look.
            if (d.BsaFailures.Count > 0)
                sb.Append("  [!] but an archive failed to read this build (see the read-failure note above), so " +
                          "\"absent\" may be incomplete — the asset could live in the unreadable archive.\n");
            if (d.Warnings.Count > 0)
                sb.Append("  [!] some archives were not scanned this build (see the discovery note above), so " +
                          "\"absent\" may be incomplete — base-game assets live in BSAs that weren't enumerated.\n");
            if (d.RootFailures.Count > 0)
                sb.Append("  [!] but a loose root failed to read this build (see the root note above), so " +
                          "\"absent\" may be incomplete — the asset could live in the root that was not read.\n");
            AppendStanding(sb, d, r);
            AppendPair(sb, r);
            return;
        }
        AppendLinks(sb, r, d.Provider is null ? null : hit.Winner);

        // The provider token is spelled by the one formatter the asset surface uses, so the name printed here is
        // the name a source selector accepts. The owning mod rides the WINS: line only on a formids= row.
        sb.Append("  WINS: ").Append(Provider(hit.Winner!))
          .Append(r.FormId is not null ? Mod(hit.Winner!) : "").Append('\n');
        sb.Append("  providers (").Append(hit.Providers.Count).Append("): ");
        for (int i = 0; i < hit.Providers.Count; i++)
        {
            if (i > 0) sb.Append(" > ");
            sb.Append(Provider(hit.Providers[i]));
        }
        sb.Append('\n');
        if (hit.Ambiguous)
            sb.Append("  note: more than one source provides this — the winner above is the precedence call (loose " +
                      "beats BSA; among BSAs the latest-loaded plugin wins). Verify only if that's unexpected.\n");
        // A loose copy MO2 hides provides nothing, so the winner above stands; said, so the hidden copy is not a surprise.
        if (hit.Hidden is { } hidden)
            sb.Append("  note: another loose copy is ").Append(hidden.Sentence()).Append(", so it provides nothing.\n");
        AppendStanding(sb, d, r);
        AppendPair(sb, r);
    }

    /// <summary>The files a mesh names, on a links= call only: where in the mesh, the string as stored, and the Data path
    /// with its winner. On a provider= call the heading names the copy read, the winner's, whatever provider= names.</summary>
    static void AppendLinks(StringBuilder sb, AssetPathResult r, AssetProvider? copy)
    {
        if (r.LinksError is { } err) { AppendLinksNotRead(sb, err); return; }
        if (r.Links is not { } links) return;
        sb.Append("  links (").Append(links.Count).Append(')');
        if (copy is not null) sb.Append(", of the winning copy ").Append(Provider(copy));
        sb.Append(":\n");
        foreach (var l in links) AppendLink(sb, l, slotName: false);
    }

    /// <summary>Why a mesh's links could not be read, as both surfaces that list them print it.</summary>
    internal static void AppendLinksNotRead(StringBuilder sb, string err) => sb.Append("  links: not read — ").Append(err).Append('\n');

    /// <summary>One file a mesh names, as both asset_status links= and nif_inspect's paths section print it: the kind,
    /// where in the mesh (with the slot's name when asked and known), the string as stored, then the Data path with its
    /// winner, ABSENT with the reason a hidden copy gives, or why the string names no Data path.</summary>
    internal static void AppendLink(StringBuilder sb, ResolvedNifLink l, bool slotName)
    {
        sb.Append("    ").Append(l.Link.Kind.Token()).Append(" — ").Append(l.Link.Where);
        if (slotName && l.Link.SlotName is { } name) sb.Append(" (").Append(name).Append(')');
        sb.Append(": `").Append(l.Link.Raw).Append("`\n");
        if (l.Link.Error is { } le) { sb.Append("      data path: none — ").Append(le).Append('\n'); return; }
        sb.Append("      data path: ").Append(l.Link.DataPath).Append("  ")
          .Append(l.Hit is { Exists: true, Winner: { } w } ? "WINS: " + Provider(w)
                  : l.Hit?.Hidden is { } h ? "ABSENT — " + h.Sentence() : "ABSENT").Append('\n');
    }

    /// <summary>The named provider's standing on this path, on a provider= call only.</summary>
    static void AppendStanding(StringBuilder sb, AssetStatusData d, AssetPathResult r)
    {
        if (r.Standing is not { } st) return;
        var hit = r.Hit!;
        sb.Append("  provider \"").Append(d.Provider).Append("\": ").Append(st switch
        {
            ProviderStanding.Wins => "wins",
            ProviderStanding.Shadowed => "shadowed by " + Provider(hit.Winner!) + Mod(hit.Winner!),
            ProviderStanding.Hidden => "holds it, " + hit.Hidden!.Unnamed() + ", so provides nothing",
            _ => "does not provide this path",
        }).Append('\n');
    }

    /// <summary>The OTHER half of the FaceGen pair, beside this one: its path and its own winner, written on a present and an ABSENT row alike. Nothing on a row no FormID derived.</summary>
    static void AppendPair(StringBuilder sb, AssetPathResult r)
    {
        if (r.PairPath is null) return;
        sb.Append("  pair");
        if (r.Slot is { } s) sb.Append(" (").Append(FaceGenPath.Token(FaceGenPath.Other(s))).Append(')');
        sb.Append(": ").Append(r.PairPath).Append('\n');
        sb.Append("    ").Append(r.PairHit is { Exists: true, Winner: { } w }
                                    ? "WINS: " + Provider(w) + Mod(w)
                                    : "ABSENT — " + AbsentWords.Why(r.PairHit?.Hidden)).Append('\n');
        if (r.PairDiffers)
            sb.Append("    [!] the two halves win from DIFFERENT mods — the head geometry and the face tint come from " +
                      "different products, which is the dark-face split.\n");
    }

    /// <summary>The mod folder behind a BSA provider, whose token is the archive's filename. Nothing for a loose provider, whose token IS the mod.</summary>
    static string Mod(HousecarlCore.AssetProvider p)
        => p.OwningMod is { Length: > 0 } m && !string.Equals(m, p.Source, StringComparison.OrdinalIgnoreCase)
            ? $"  [mod: {m}]" : "";

    static string Kind(HousecarlCore.AssetKind k) => k == HousecarlCore.AssetKind.Bsa ? "BSA" : "loose";

    /// <summary>One provider, spelled by the shared formatter: the name inside double quotes with the kind outside them, so the printed token is the token a selector accepts.</summary>
    internal static string Provider(HousecarlCore.AssetProvider p)
        => HousecarlCore.AssetSourceSelection.Describe(p.Source, Kind(p.Kind));
}

/// <summary><c>asset_status</c>'s <c>counts_only=</c> census (SPEC §2.1): what the file layer looks like in
/// AGGREGATE over the paths this call resolved. It covers the WHOLE selection and <c>limit=</c> is spent on the
/// tables' rows instead, which is the thing here that actually needs paging. The two tables, winning and shadowed
/// layers, are the surface's <see cref="HistogramAxis"/>, so only the counters above them are this lane's own.</summary>
static class AssetCensus
{
    /// <summary>The census over one resolution. <see cref="ByLayer"/> is the winning MO2 LAYERS, count descending
    /// then name ascending — the same value the artifact's <c>winner_mod</c> column carries; <see cref="ByShadowed"/>
    /// the layers that provide a copy another layer wins over, in the same order; <see cref="Named"/> the provider= counts.</summary>
    internal readonly record struct Counts(int Selected, int Present, int Absent, int Errors,
                                           int Loose, int Bsa, IReadOnlyList<SweepCount> ByLayer,
                                           IReadOnlyList<SweepCount> ByShadowed, ProviderCounts? Named);

    /// <summary>One named provider over the selection: the paths it ships a copy of, and how many of those it wins and loses.</summary>
    internal readonly record struct ProviderCounts(string Name, int Wins, int Shadowed)
    {
        public int Provides => Wins + Shadowed;
    }

    /// <summary>What the axes are titled, and the notes that keep them honest. A note rides its axis, so it is written whatever the budget says.</summary>
    const string AxisTitle = "winning layers";
    const string AxisNote = "a winning LAYER is a mod folder, the game's own Data folder, or overwrite — the last "
                          + "two are layers rather than mods, so they cannot be sorted or disabled.";
    const string ShadowedTitle = "shadowed layers";
    const string ShadowedNote = "a shadowed layer ships a copy another layer wins over; what a layer provides is its "
                              + "winning count plus its shadowed count.";

    internal static Counts Tally(AssetStatusData d)
    {
        int present = 0, absent = 0, errors = 0, loose = 0, bsa = 0, wins = 0, shadowed = 0;
        var byLayer = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var byShadowed = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var losers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in d.Results)
        {
            if (r.Error is not null) { errors++; continue; }
            if (r.Hit is not { Exists: true, Winner: { } win } hit) { absent++; continue; }
            present++;
            if (win.Kind == HousecarlCore.AssetKind.Bsa) bsa++; else loose++;
            var owner = AssetPathResult.Owner(win);
            byLayer[owner] = byLayer.GetValueOrDefault(owner) + 1;
            // Each losing layer once per path, the winner's own layer never: a mod's loose copy and its own archive's are one layer's two copies.
            losers.Clear();
            losers.Add(owner);
            foreach (var p in hit.Providers)
                if (AssetPathResult.Owner(p) is var o && losers.Add(o))
                    byShadowed[o] = byShadowed.GetValueOrDefault(o) + 1;
            switch (r.Standing)
            {
                case ProviderStanding.Wins: wins++; break;
                case ProviderStanding.Shadowed: shadowed++; break;
            }
        }
        // The census always covers the whole selection, so its counters are the selection's — never a window's.
        return new Counts(d.Selected, present, absent, errors, loose, bsa,
                          SweepFindings.Histogram(byLayer), SweepFindings.Histogram(byShadowed),
                          d.Provider is { } name ? new ProviderCounts(name, wins, shadowed) : null);
    }

    /// <summary>The axes this census renders, in one place so the two transports cannot title or note them differently.</summary>
    internal static HistogramAxis[] Axes(Counts c) =>
    [
        new(SweepSubject.AssetWinnerRows, c.ByLayer, AxisTitle, Note: AxisNote),
        new(SweepSubject.AssetShadowedRows, c.ByShadowed, ShadowedTitle, Note: ShadowedNote),
    ];

    /// <summary>The rows one call may render, from its <c>limit=</c>: 0 is no limit.</summary>
    internal static int RowLimit(int limit) => limit > 0 ? limit : int.MaxValue;

    /// <summary>The counter lines — the census's whole answer, written whatever the budget says.</summary>
    static string Counters(Counts c) =>
        $"\ncensus: counted={c.Selected} present={c.Present} absent={c.Absent} errors={c.Errors}\n"
        + $"winners: loose={c.Loose} BSA={c.Bsa}\n"
        + (c.Named is { } n ? $"provider \"{n.Name}\": provides={n.Provides} wins={n.Wins} shadowed={n.Shadowed}\n" : "");

    /// <summary>The text census: the alarms an ABSENT count depends on, the counters, then the two layer axes. The counters are exact whatever the axes' cut.</summary>
    public static string Render(AssetStatusData d, int cap, int limit)
    {
        var c = Tally(d);
        var sb = new StringBuilder(AssetWire.Header(d)).Append('\n');
        // What this response writes whatever the budget says, held back BEFORE the alarms: uncharged, they take
        // the room the census's own answer needs and the response lands over the cap on a cut that would have fitted.
        var room = RenderCap.For(cap, Counters(c).Length + Axes(c).Sum(a => a.TextFixed));
        // The alarms first, for the reason the path render puts them first: an ABSENT count is authoritative only where no archive read failed.
        BatchRender.AppendReadFailures(sb, d.BsaFailures, "an asset", room);
        BatchRender.AppendRootFailures(sb, d.RootFailures, "an asset", room);
        BatchRender.AppendDiscoveryWarnings(sb, d.Warnings, room);
        AssetWire.AppendSelectorNotes(sb, d.SelectorNotes, room);
        AssetWire.AppendNotes(sb, d.Notes, room);

        sb.Append(Counters(c));

        // The one bounded emission path: the budget is the whole cap, because Outstanding reads the live builder.
        var body = new BoundedBody(acct: null, budget: cap, () => sb.Length);
        CheckTextRender.AppendHistogramAxes(sb, body, RowLimit(limit), Axes(c));
        return RenderCap.Settle(sb.ToString().TrimEnd('\n'), cap);
    }
}
