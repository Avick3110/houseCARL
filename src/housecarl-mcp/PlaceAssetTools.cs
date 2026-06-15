using System.ComponentModel;
using System.Text;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using Mutagen.Bethesda.Plugins;
using HousecarlCore;

namespace HousecarlMcp;

/// <summary>
/// houseCARL place-asset tools — place a chosen asset file (any Data-relative file) as a winning override in a NEW
/// houseCARL-owned MO2 mod folder, the WRITE counterpart to housecarl_asset_status (which reports which copy currently
/// wins the VFS). A precise placer: it writes a source it is handed and auto-resolves only when exactly ONE copy exists
/// (the "which copy is correct" judgment is the caller's / a skill's, not the tool's). Source bytes are read IN PROCESS —
/// a loose file, or a single entry out of a BSA via native Mutagen (no BSArch). The write is crash-atomic and
/// non-destructive (originals untouched; a failed fresh placement leaves no orphan). Honest (Q3): "wrote it" is NOT "it
/// wins" — the tool reports the current winner and the required MO2 enable + sort, and never claims the fix took effect on
/// write. General asset-layer; the FaceGen / dark-face case is the headline use case, driven by the facegen skill (the
/// formid+kind input is its convenience — for any other file use asset_path).
/// </summary>
[McpServerToolType]
public static class PlaceAssetTools
{
    [McpServerTool(Name = "housecarl_place_asset", Title = "Place ONE asset file so a chosen copy can win MO2's VFS"),
     Description(
         "Place ONE asset file — ANY Data-relative file (a mesh, texture, script, sound, interface, etc.) — into a NEW " +
         "houseCARL-owned MO2 mod folder so a CHOSEN copy can win the virtual file system. The WRITE counterpart to " +
         "housecarl_asset_status (which reports which copy currently wins). Give the DESTINATION as asset_path (a " +
         "Data-relative path); OR, for an NPC's generated FaceGen file, as formid (the NPC's FormID 'XXXXXX:Plugin.esp') " +
         "+ kind ('mesh' = the head .nif, 'tint' = the face .dds), which houseCARL computes the path for. Give the SOURCE " +
         "(the copy to place) as source= a loose file path, or '<archive.bsa path>|<entry inside>', or just a '.bsa' path " +
         "(the entry is taken to be the destination — a quick way to pull ONE file out of a BSA as a loose override). If " +
         "source is OMITTED, houseCARL auto-resolves: it uses the SOLE provider in your load order, and REFUSES (telling " +
         "you the candidates) if more than one provides it — it will not guess which is correct. The write is crash-atomic; " +
         "originals are never touched. IMPORTANT (and reported back): the placed copy does NOT win on write — you must " +
         "ENABLE the new mod in MO2 and SORT it above the current winner. This tool places ONE file; to place several (or " +
         "an NPC's mesh AND tint together) use housecarl_bulk_place_asset.")]
    public static string PlaceAsset(
        LoadOrderService svc,
        [Description("Optional. For an NPC's generated FaceGen file: the NPC's FormID 'XXXXXX:Plugin.esp' — houseCARL computes the FaceGen path from it. For any other file, use asset_path instead. Provide this OR asset_path; requires kind=.")]
            string? formid = null,
        [Description("Optional (with formid). Which FaceGen file: 'mesh' (the head .nif) or 'tint' (the face .dds). REQUIRED when formid is given — this tool places one file. Use housecarl_bulk_place_asset to place both at once.")]
            string? kind = null,
        [Description("Optional. The Data-relative destination path to place to (any file — e.g. 'textures/armor/iron/cuirass_1.dds', 'meshes/...', a script, a sound), instead of formid+kind. Provide this OR formid. A drive-rooted or '..'-escaping path is rejected.")]
            string? asset_path = null,
        [Description("Optional. The correct copy to place: a loose file path; or '<archive.bsa path>|<entry inside>'; or just a '.bsa' path (the entry is taken to be the destination path). If omitted, houseCARL uses the SOLE provider in your load order and refuses if more than one provides it (you choose).")]
            string? source = null,
        [Description("Optional. Base name for the NEW houseCARL mod folder the file lands in (default 'houseCARL_Assets'); auto-suffixed if taken.")]
            string? patch_name = null,
        [Description("Optional. Filename of an existing houseCARL patch mod to place into instead of a fresh folder (accumulate across calls).")]
            string? into = null) => Guard.Tool("housecarl_place_asset", () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        var reqs = MapSpec(formid, kind, asset_path, source, allowExpand: false, where: "", out var err);
        if (err is not null) return "error: " + err;
        return PlaceWire.Render(svc.PlaceAssets(reqs!, patch_name, into));
    });

    [McpServerTool(Name = "housecarl_bulk_place_asset", Title = "Place MANY asset files in one houseCARL mod"),
     Description(
         "Place MANY asset files in ONE houseCARL-owned MO2 mod folder — the batch form of housecarl_place_asset (place " +
         "several overrides at once; or, for an NPC, its FaceGen mesh AND tint together). assets is an array of " +
         "{ formid?, kind?, asset_path?, source? }: give EITHER asset_path (any Data-relative path) OR formid (an NPC " +
         "FormID — omit kind to place BOTH the FaceGen mesh and the tint; or set kind='mesh'/'tint' for just one). source " +
         "is the copy to place (a loose file path, '<archive.bsa>|<entry>', or a '.bsa' path); omit it to auto-resolve the " +
         "sole VFS provider (an ambiguous or absent source becomes a per-asset error — the rest still place). When you give " +
         "a FormID with no kind (placing both files), an explicit source must be a '.bsa' path (each slot's entry is " +
         "derived) — for a single loose/entry source set kind=. All files land in ONE reviewable mod folder. A malformed " +
         "spec (bad FormID, bad kind, neither/both of formid+asset_path) refuses the WHOLE call with per-spec reasons and " +
         "places nothing. As with the single tool, the placed copies do NOT win until you enable + sort the mod in MO2 " +
         "(reported back).")]
    public static string BulkPlaceAsset(
        LoadOrderService svc,
        [Description("The assets to place, all into one mod folder. Each: { formid?: 'XXXXXX:Plugin.esp', kind?: 'mesh'|'tint' (omit with formid to place BOTH), asset_path?: 'meshes/...', source?: '<loose path>' | '<archive.bsa>|<entry>' | '<archive.bsa>' }.")]
            PlaceAssetSpec[] assets,
        [Description("Optional. Base name for the NEW houseCARL mod folder (default 'houseCARL_Assets'); auto-suffixed if taken.")]
            string? patch_name = null,
        [Description("Optional. Filename of an existing houseCARL patch mod to place into instead of a fresh folder.")]
            string? into = null) => Guard.Tool("housecarl_bulk_place_asset", () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        if (assets is null || assets.Length == 0)
            return "error: assets is empty. Pass one or more { formid|asset_path, kind?, source? } specs.";

        // Malformed specs refuse the WHOLE call (all-or-nothing, like bulk_create); placement-time issues
        // (ambiguous/absent/unreadable source) are per-asset (the resolver isn't consulted until the place loop).
        var all = new List<PlaceRequest>();
        var problems = new List<string>();
        for (int i = 0; i < assets.Length; i++)
        {
            var a = assets[i];
            var reqs = MapSpec(a.Formid, a.Kind, a.AssetPath, a.Source, allowExpand: true, where: $"assets[{i}]: ", out var err);
            if (err is not null) problems.Add(err); else all.AddRange(reqs!);
        }
        if (problems.Count > 0)
            return $"error: refused — {problems.Count} malformed spec(s); nothing placed:\n  - " + string.Join("\n  - ", problems);

        return PlaceWire.Render(svc.PlaceAssets(all, patch_name, into));
    });

    /// <summary>Map one wire spec to its placement request(s): asset_path → one request; formid+kind → one request (the
    /// computed FaceGen path); formid with NO kind → BOTH mesh+tint (only when <paramref name="allowExpand"/> — the bulk
    /// tool). Exactly one of formid/asset_path is required. A both-expansion forbids a single loose/entry source (it can't
    /// serve two different files) — only a bare '.bsa' source (entry derived per slot) or auto-resolve. Q3: every bad
    /// input is a NAMED error (returned via <paramref name="error"/>), never a silent skip.</summary>
    static List<PlaceRequest>? MapSpec(string? formid, string? kind, string? assetPath, string? source, bool allowExpand, string where, out string? error)
    {
        error = null;
        bool hasFormid = !string.IsNullOrWhiteSpace(formid);
        bool hasPath = !string.IsNullOrWhiteSpace(assetPath);
        if (hasFormid == hasPath) { error = $"{where}provide exactly one of formid or asset_path."; return null; }

        var src = NullIfBlank(source);

        if (hasPath)
            return new List<PlaceRequest> { new(assetPath!.Trim(), src) };

        FormKey fk;
        try { fk = FormKey.Factory(formid!.Trim()); }
        catch (Exception ex) { error = $"{where}bad formid '{formid}' ({ex.Message}). Expected 'XXXXXX:Plugin.esp'."; return null; }

        var slot = ParseSlot(kind, out var slotErr);
        if (slotErr is not null) { error = $"{where}{slotErr}"; return null; }

        if (slot is { } s)                                            // explicit mesh|tint → one file
            return new List<PlaceRequest> { new(FaceGenPath.For(fk, s), src) };

        // kind omitted → both mesh + tint (bulk only)
        if (!allowExpand)
        {
            error = $"{where}kind is required with formid (mesh or tint — housecarl_place_asset places ONE file). To place both at once, use housecarl_bulk_place_asset.";
            return null;
        }
        // Trim quotes for the both-expansion test the same way ReadExplicitSource trims before it routes — else a quoted
        // spaced BSA name (the natural form, "C:\...\X - Textures.bsa") ends in '"' not '.bsa' and would be wrongly
        // refused here on the marquee mesh+tint path. (The actual read also trims-before-routing, so the two agree.)
        var srcProbe = src?.Trim('"');
        bool srcOkForBoth = srcProbe is null || (srcProbe.EndsWith(".bsa", StringComparison.OrdinalIgnoreCase) && srcProbe.IndexOf('|') < 0);
        if (!srcOkForBoth)
        {
            error = $"{where}with formid and no kind (placing BOTH mesh and tint), an explicit source= must be a bare '.bsa' path — each slot's entry is then derived. For a single loose file or BSA entry, set kind= mesh or tint.";
            return null;
        }
        var reqs = new List<PlaceRequest>(2);
        foreach (var (_, rel) in FaceGenPath.Both(fk)) reqs.Add(new PlaceRequest(rel, src));
        return reqs;
    }

    /// <summary>Parse the FaceGen slot token. Null/blank ⇒ null (unspecified — the caller decides if that means "both" or
    /// "required"). A bad token is a named error (Q3). Lenient synonyms for the two file kinds.</summary>
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

/// <summary>Renders a <see cref="PlaceOutcome"/> as compact, scannable text: the count, the mod folder, the discovery
/// caveats (Q3), one line per asset (placed with its source + the current VFS winner to sort above, or a per-asset
/// error), and — the load-bearing honesty — the EXPLICIT "this does not win until you enable + sort the mod in MO2"
/// instruction whenever anything was placed.</summary>
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
                sb.Append(r.CurrentWinner is not null
                    ? $"        currently wins the VFS: {r.CurrentWinner} — sort the new mod ABOVE it\n"
                    : "        nothing else provides this path — once the mod is enabled, the placed copy wins\n");
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
/// housecarl_place_asset: a FormID (+ optional slot) or a raw destination path, and the optional source.</summary>
public sealed record PlaceAssetSpec
{
    [JsonPropertyName("formid"), Description("The NPC's FormID 'XXXXXX:Plugin.esp' — houseCARL computes the FaceGen path. Omit kind to place BOTH the mesh and the tint. Provide this OR asset_path.")]
    public string? Formid { get; init; }

    [JsonPropertyName("kind"), Description("With formid: 'mesh' (head .nif) or 'tint' (face .dds). Omit to place BOTH. Ignored with asset_path.")]
    public string? Kind { get; init; }

    [JsonPropertyName("asset_path"), Description("A Data-relative destination path (e.g. 'meshes/actors/...'), instead of formid. Provide this OR formid.")]
    public string? AssetPath { get; init; }

    [JsonPropertyName("source"), Description("The correct copy to place: a loose file path, '<archive.bsa>|<entry>', or a '.bsa' path. Omit to auto-resolve the sole VFS provider. With formid and no kind, an explicit source must be a bare '.bsa' path.")]
    public string? Source { get; init; }
}
