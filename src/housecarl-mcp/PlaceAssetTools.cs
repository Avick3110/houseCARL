using System.ComponentModel;
using System.Text;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using Mutagen.Bethesda.Plugins;
using HousecarlCore;

namespace HousecarlMcp;

/// <summary>Places a chosen asset file as a winning override in a new houseCARL-owned MO2 mod folder — the write
/// counterpart to housecarl_asset_status. It writes the source it is handed and auto-resolves only when exactly one
/// copy exists; which copy is correct is the caller's judgement, never the tool's. Source bytes are read in process
/// (a loose file, or one BSA entry through Mutagen — no BSArch), the write is crash-atomic, and a placement never
/// wins on write: the response always states the current winner and the required MO2 enable + sort.</summary>
[McpServerToolType]
public static class PlaceAssetTools
{
    [McpServerTool(Name = ToolNames.PlaceAsset, Title = "Place ONE asset file so a chosen copy can win MO2's VFS"),
     Description(
         "Place ONE asset file — ANY Data-relative file (a mesh, texture, script, sound, interface, etc.) — into a NEW " +
         "houseCARL-owned MO2 mod folder so a CHOSEN copy can win the virtual file system. The WRITE counterpart to " +
         ToolNames.AssetStatus + " (which reports which copy currently wins). Give the DESTINATION as asset_path (a " +
         "Data-relative path); OR, for an NPC's generated FaceGen file, as formid (the NPC's FormID 'XXXXXX:Plugin.esp') " +
         "+ kind ('mesh' = the head .nif, 'tint' = the face .dds), which houseCARL computes the path for. Give the SOURCE " +
         "(the copy to place) as source= a DATA-RELATIVE path resolved through the VFS — with source_provider= naming " +
         "whose copy (a mod folder or BSA filename on its own, or " + AssetSourceChoice.WinnerToken + " for the current " +
         "VFS winner; a NAMED mod folder need not be one MO2 currently loads — see source_provider=) — or as a full loose " +
         "file path, or '<archive.bsa path>|<entry inside>', or just a '.bsa' path (the entry is taken to be the " +
         "destination — a quick way to pull ONE file out of a BSA as a loose override). A source path DIFFERENT from the " +
         "destination is a RENAME: the bytes of one file land under another file's name, which is how a baked FaceGen " +
         "head is carried onto a different NPC's FormID path. If source is OMITTED, houseCARL resolves the DESTINATION " +
         "path instead: the sole provider, or the one source_provider= names, REFUSING (and listing the providers) when " +
         "several contend and none was named — it will not guess which is correct. The write is crash-atomic; " +
         "originals are never touched. IMPORTANT (and reported back): the placed copy does NOT win on write — you must " +
         "ENABLE the new mod in MO2 and SORT it above the current winner. This tool places ONE file; to place several (or " +
         "an NPC's mesh AND tint together) use " + ToolNames.BulkPlaceAsset + ".")]
    public static string PlaceAsset(
        LoadOrderService svc,
        [Description("Optional. For an NPC's generated FaceGen file: the NPC's FormID 'XXXXXX:Plugin.esp' — houseCARL computes the FaceGen path from it. For any other file, use asset_path instead. Provide this OR asset_path; requires kind=.")]
            string? formid = null,
        [Description("Optional (with formid). Which FaceGen file: 'mesh' (the head .nif) or 'tint' (the face .dds). REQUIRED when formid is given — this tool places one file. Use " + ToolNames.BulkPlaceAsset + " to place both at once.")]
            string? kind = null,
        [Description("Optional. The Data-relative destination path to place to (any file — e.g. 'textures/armor/iron/cuirass_1.dds', 'meshes/...', a script, a sound), instead of formid+kind. Provide this OR formid. A drive-rooted or '..'-escaping path is rejected.")]
            string? asset_path = null,
        [Description("Optional. The copy to place: a DATA-RELATIVE path (resolved through the VFS — use source_provider= to say whose copy, and note that a source path DIFFERENT from the destination is a rename); or a full loose file path; or '<archive.bsa path>|<entry inside>'; or just a '.bsa' path (the entry is taken to be the destination path). If omitted, the destination path is resolved through the VFS instead.")]
            string? source = null,
        [Description("Optional (with a Data-relative source=, or with no source=). Whose copy to read. Two forms: "
                     + AssetSourceChoice.WinnerToken + " (the sigil is part of the token) for whichever copy currently wins the VFS; "
                     + "or the provider's NAME ALONE — a mod folder name, 'overwrite', 'Data', or a BSA filename like "
                     + "'X - Textures.bsa' — matched exactly. A bare name ALWAYS means a provider of that name, so a mod whose "
                     + "folder happens to carry the pole's word is still reachable and nothing is reserved out of the name space. "
                     + WriteSentences.PlaceSourceNameReachesUnticked + " "
                     + ToolNames.AssetStatus + " lists the PROVIDER names the active order supplies a path under — mod "
                     + "folders and archive filenames alike — with a kind annotation after them ('SomeMod (loose)', "
                     + "'X - Textures.bsa (BSA)'); pass the name only, without the annotation, and note that a file inside an "
                     + "active mod's archive is listed (and reached) under the ARCHIVE's name, not the mod's — and an "
                     + "archive MO2 loads no plugin for is listed under neither, so it is reachable only as an on-disk "
                     + "source= path. A path only a "
                     + "mod MO2 is not loading provides reads as ABSENT there, while still being placeable by naming that mod here. "
                     + "Omitted = the sole provider, refused if more than one contends. A named "
                     + "provider that doesn't supply the path is refused, never silently replaced by another, and the "
                     + "refusal says which places were looked in. A name the active order already provides files under "
                     + "is answered by the active order, so a mod folder of that same name is not consulted.")]
            string? source_provider = null,
        [Description("Optional. Base name for the NEW houseCARL mod folder the file lands in (default 'houseCARL_Assets'); auto-suffixed if taken.")]
            string? patch_name = null,
        [Description("Optional. Filename of an existing houseCARL patch mod to place into instead of a fresh folder (accumulate across calls). Found by the plugin's filename even if you've renamed its MO2 mod folder; for two patches sharing a filename, pass the mod-folder name here instead (folder & plugin names need not match).")]
            string? into = null) => Guard.Tool(ToolNames.PlaceAsset, () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        var reqs = MapSpec(svc.OpenWriteFormIdDoor().Parse, formid, kind, asset_path, source, source_provider, allowExpand: false, where: "", out var err);
        if (err is not null) return "error: " + err;
        return PlaceWire.Render(svc.PlaceAssets(reqs!, patch_name, into));
    });

    [McpServerTool(Name = ToolNames.BulkPlaceAsset, Title = "Place MANY asset files in one houseCARL mod"),
     Description(
         "Place MANY asset files in ONE houseCARL-owned MO2 mod folder — the batch form of " + ToolNames.PlaceAsset + " (place " +
         "several overrides at once; or, for an NPC, its FaceGen mesh AND tint together). assets is an array of " +
         "{ formid?, kind?, asset_path?, source?, source_provider? }: give EITHER asset_path (any Data-relative path) OR formid (an NPC " +
         "FormID — omit kind to place BOTH the FaceGen mesh and the tint; or set kind='mesh'/'tint' for just one). source " +
         "is the copy to place (a Data-relative path resolved through the VFS — source_provider names whose copy, and a " +
         "NAMED mod folder need not be one MO2 currently loads — a full " +
         "loose file path, '<archive.bsa>|<entry>', or a '.bsa' path); omit it to resolve the destination path instead " +
         "(an ambiguous or absent source becomes a per-asset error — the rest still place). A per-asset source that " +
         "differs from its destination is a RENAME. When you give " +
         "a FormID with no kind (placing both files), an explicit source must be a FULLY-QUALIFIED '.bsa' path (each slot's " +
         "entry is derived; a RELATIVE '.bsa' is a Data-relative asset path, and one path cannot serve two slots) — for a " +
         "single loose/entry/Data-relative source set kind=. All files land in ONE reviewable mod folder. A malformed " +
         "spec (bad FormID, bad kind, neither/both of formid+asset_path) refuses the WHOLE call with per-spec reasons and " +
         "places nothing. As with the single tool, the placed copies do NOT win until you enable + sort the mod in MO2 " +
         "(reported back).")]
    public static string BulkPlaceAsset(
        LoadOrderService svc,
        [Description("The assets to place, all into one mod folder. Each: { formid?: 'XXXXXX:Plugin.esp', kind?: 'mesh'|'tint' (omit with formid to place BOTH), asset_path?: 'meshes/...', source?: '<loose path>' | '<archive.bsa>|<entry>' | '<archive.bsa>' | '<Data-relative path>', source_provider?: 'SomeMod' | 'X - Textures.bsa' | '" + AssetSourceChoice.WinnerToken + "' }. Each member's own description says what it takes.")]
            PlaceAssetSpec[] assets,
        [Description("Optional. Base name for the NEW houseCARL mod folder (default 'houseCARL_Assets'); auto-suffixed if taken.")]
            string? patch_name = null,
        [Description("Optional. Filename of an existing houseCARL patch mod to place into instead of a fresh folder. Found by the plugin's filename even if you've renamed its MO2 mod folder; for two patches sharing a filename, pass the mod-folder name here instead (folder & plugin names need not match).")]
            string? into = null) => Guard.Tool(ToolNames.BulkPlaceAsset, () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        if (assets is null || assets.Length == 0)
            return "error: assets is empty. Pass one or more { formid|asset_path, kind?, source? } specs.";

        // Malformed specs refuse the WHOLE call (all-or-nothing, like bulk_create); placement-time issues
        // (ambiguous/absent/unreadable source) are per-asset (the resolver isn't consulted until the place loop).
        var all = new List<PlaceRequest>();
        var problems = new List<string>();
        var door = svc.OpenWriteFormIdDoor();
        for (int i = 0; i < assets.Length; i++)
        {
            var a = assets[i];
            var reqs = MapSpec(door.Parse, a.Formid, a.Kind, a.AssetPath, a.Source, a.SourceProvider, allowExpand: true, where: $"assets[{i}]: ", out var err);
            if (err is not null) problems.Add(err); else all.AddRange(reqs!);
        }
        if (problems.Count > 0)
            return $"error: refused — {problems.Count} malformed spec(s); nothing placed:\n  - " + string.Join("\n  - ", problems);

        return PlaceWire.Render(svc.PlaceAssets(all, patch_name, into));
    });

    /// <summary>Map one wire spec to its placement request(s): asset_path → one request; formid+kind → one request (the
    /// computed FaceGen path); formid with NO kind → BOTH mesh+tint (only when <paramref name="allowExpand"/> — the bulk
    /// tool). Exactly one of formid/asset_path is required. A both-expansion forbids a single loose/entry source (it can't
    /// serve two different files) — only a FULLY-QUALIFIED '.bsa' source (entry derived per slot) or auto-resolve. Every
    /// bad input is a NAMED error returned via <paramref name="error"/>, never a silent skip.</summary>
    static List<PlaceRequest>? MapSpec(Func<string?, FormKey> parseFormId, string? formid, string? kind, string? assetPath, string? source, string? sourceProvider, bool allowExpand, string where, out string? error)
    {
        error = null;
        bool hasFormid = !string.IsNullOrWhiteSpace(formid);
        bool hasPath = !string.IsNullOrWhiteSpace(assetPath);
        if (hasFormid == hasPath) { error = $"{where}provide exactly one of formid or asset_path."; return null; }

        var src = NullIfBlank(source);
        var prov = NullIfBlank(sourceProvider);

        if (hasPath)
            return new List<PlaceRequest> { new(assetPath!.Trim(), src, prov) };

        FormKey fk;
        try { fk = parseFormId(formid); }
        catch (Exception ex) { error = FormIdDoor.Sentence(ex, where, $"{where}bad formid '{formid}' ({ex.Message}). Expected 'XXXXXX:Plugin.esp'."); return null; }

        var slot = ParseSlot(kind, out var slotErr);
        if (slotErr is not null) { error = $"{where}{slotErr}"; return null; }

        if (slot is { } s)                                            // explicit mesh|tint → one file
            return new List<PlaceRequest> { new(FaceGenPath.For(fk, s), src, prov) };

        // kind omitted → both mesh + tint (bulk only)
        if (!allowExpand)
        {
            error = $"{where}kind is required with formid (mesh or tint — {ToolNames.PlaceAsset} places ONE file). To place both at once, use {ToolNames.BulkPlaceAsset}.";
            return null;
        }
        // Trim quotes exactly as the service does before it routes, or a quoted spaced BSA name ends in '"' not '.bsa'
        // and is wrongly refused. FULLY-QUALIFIED matches that routing: a RELATIVE '.bsa' is a Data-relative asset
        // path, and one such path cannot serve two slots — accepting it would hand both slots the same file.
        var srcProbe = src?.Trim('"');
        bool srcOkForBoth = srcProbe is null
            || (srcProbe.EndsWith(".bsa", StringComparison.OrdinalIgnoreCase)
                && srcProbe.IndexOf('|') < 0
                && Path.IsPathFullyQualified(srcProbe));
        if (!srcOkForBoth)
        {
            error = $"{where}with formid and no kind (placing BOTH mesh and tint), an explicit source= must be a FULL '.bsa' path — each slot's entry is then derived. Any single file path names ONE file and cannot serve both slots, so for a loose file, a BSA entry, or a Data-relative path set kind= mesh or tint. {WriteSentences.PlaceBothSlotsPoleConstraint}.";
            return null;
        }
        var reqs = new List<PlaceRequest>(2);
        foreach (var (_, rel) in FaceGenPath.Both(fk)) reqs.Add(new PlaceRequest(rel, src, prov));
        return reqs;
    }

    /// <summary>Parse the FaceGen slot token. Null/blank ⇒ null (unspecified — the caller decides if that means "both" or
    /// "required"). A bad token is a named error. Lenient synonyms for the two file kinds.</summary>
    static FaceGenSlot? ParseSlot(string? kind, out string? error)
    {
        error = null;
        var k = kind?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(k)) return null;
        switch (k)
        {
            case "mesh": case "nif": case "geom": case "facegeom": return FaceGenSlot.Mesh;
            case "tint": case "dds": case "facetint": case "texture": return FaceGenSlot.Tint;
            default: error = $"kind '{kind}' is not valid — use 'mesh' (the head .nif) or 'tint' (the face .dds)."; return null;
        }
    }

    static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>Renders a <see cref="PlaceOutcome"/> as compact text: the count, the mod folder, the discovery caveats,
/// one line per asset (its source and the current VFS winner to sort above, or a per-asset error), and always the
/// explicit "this does not win until you enable + sort the mod in MO2" instruction when anything was placed.</summary>
static class PlaceWire
{
    public static string Render(PlaceOutcome o)
    {
        if (o.Error is not null) return "error: " + o.Error;

        var sb = new StringBuilder();
        int placed = 0;
        foreach (var r in o.Results) if (r.Placed) placed++;
        int failed = o.Results.Count - placed;
        var modFolder = o.ModFolder is null ? null : Path.GetFileName(o.ModFolder);

        sb.Append("placed ").Append(placed).Append(" of ").Append(o.Results.Count).Append(" asset(s)");
        if (failed > 0) sb.Append(" (").Append(failed).Append(" failed)");
        sb.Append('\n');
        if (modFolder is not null) sb.Append("mod folder: ").Append(modFolder).Append('\n');

        foreach (var w in o.Warnings) sb.Append("[!] discovery: ").Append(w).Append('\n');

        foreach (var r in o.Results)
        {
            if (r.Placed)
            {
                sb.Append("  OK    ").Append(r.AssetPath).Append("  (").Append(r.Bytes).Append(" bytes from ").Append(r.SourceDesc).Append(")\n");
                // Bytes served out of a mod MO2 does not load look like any other placement on the line above, so
                // say so on their own line. This is about the SOURCE; the destination's enable+sort is the block below.
                if (r.SourceOffOrderProvider is { } offOrder)
                    sb.Append("        ").Append(WriteSentences.PlaceSourceOffOrder(offOrder)).Append('\n');
                // Name the destination folder rather than saying "the mod": the off-order line above can put a
                // SECOND mod in scope, and it ends by saying enabling THAT one is not required.
                sb.Append(r.CurrentWinner is not null
                    ? $"        currently wins the VFS: {r.CurrentWinner} — sort the new mod ABOVE it\n"
                    : $"        nothing else provides this path — once '{modFolder ?? "(the new folder)"}' is enabled, the placed copy wins\n");
            }
            else
            {
                sb.Append("  FAIL  ").Append(r.AssetPath).Append("  ").Append(r.Error).Append('\n');
            }
        }

        if (o.LeftoverFolder is not null)
            sb.Append("note: the fresh folder at '").Append(o.LeftoverFolder)
              .Append("' holds a partial result — delete it or retry with into=.\n");

        if (placed > 0)
        {
            bool anyContended = false;
            foreach (var r in o.Results) if (r.Placed && r.CurrentWinner is not null) { anyContended = true; break; }
            sb.Append("\nIMPORTANT — \"wrote it\" is not \"it wins\": the placed file(s) do NOT win the VFS yet. Enable the mod '")
              .Append(modFolder ?? "(the new folder)").Append("' in MO2");
            sb.Append(anyContended
                ? " and SORT it (left pane) ABOVE the current winner(s) listed above. Only then does the placed copy win.\n"
                : ". Nothing else provided these path(s), so once enabled the placed copy wins (sort it above any mod you later add that also provides them).\n");
        }

        return sb.ToString().TrimEnd('\n');
    }
}

/// <summary>One asset to place off the wire (housecarl_bulk_place_asset). Mirrors the scalar args of
/// housecarl_place_asset: a FormID (+ optional slot) or a raw destination path, the optional source, and the
/// optional source-provider pole that says whose copy of a VFS-resolved source to read.</summary>
public sealed record PlaceAssetSpec
{
    [JsonPropertyName("formid"), Description("The NPC's FormID 'XXXXXX:Plugin.esp' — houseCARL computes the FaceGen path. Omit kind to place BOTH the mesh and the tint. Provide this OR asset_path.")]
    public string? Formid { get; init; }

    [JsonPropertyName("kind"), Description("With formid: 'mesh' (head .nif) or 'tint' (face .dds). Omit to place BOTH. Ignored with asset_path.")]
    public string? Kind { get; init; }

    [JsonPropertyName("asset_path"), Description("A Data-relative destination path (e.g. 'meshes/actors/...'), instead of formid. Provide this OR formid.")]
    public string? AssetPath { get; init; }

    [JsonPropertyName("source"), Description("The copy to place: a Data-relative path (resolved through the VFS; different from the destination = a rename), a full loose file path, '<archive.bsa>|<entry>', or a '.bsa' path. Omit to resolve the destination path through the VFS. With formid and no kind, an explicit source must be a FULLY-QUALIFIED '.bsa' path (a relative one is a Data-relative asset path, and one path cannot serve both slots).")]
    public string? Source { get; init; }

    [JsonPropertyName("source_provider"), Description("Whose copy to read for a VFS-resolved source: "
        + AssetSourceChoice.WinnerToken + " for the current VFS winner, or the provider's NAME ALONE (a mod folder, 'overwrite', "
        + "'Data', or a BSA filename) — not asset_status's ' (loose)' / ' (BSA)' annotation. A bare name always means a provider "
        + "of that name. " + WriteSentences.PlaceSourceNameReachesUnticked + " Applies BOTH with a Data-relative source= (whose "
        + "copy to read it FROM) and with NO source= at all "
        + "(whose copy of the DESTINATION path to place) — in the second case it is what resolves the contention an omitted "
        + "source is otherwise refused for. Omit for the sole provider (contention is refused). Not valid with an on-disk source.")]
    public string? SourceProvider { get; init; }
}
