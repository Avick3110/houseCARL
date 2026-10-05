using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using Mutagen.Bethesda.Plugins;
using HousecarlCore;

namespace HousecarlMcp;

/// <summary>housecarl_place — the S2 write tool: one call places any number of chosen file copies as winning
/// overrides in ONE houseCARL-owned MO2 mod folder, the write counterpart to housecarl_asset_status. It writes the
/// source it is handed and auto-resolves only when exactly one copy exists; which copy is correct is the caller's
/// judgement. A placement never wins on write (docs/architecture/assets.md).</summary>
[McpServerToolType]
public static class PlaceTools
{
    [McpServerTool(Name = ToolNames.Place, Title = "Place chosen file copies so they win MO2's VFS"),
     Description(
         "Place chosen copies of any Data-relative files (meshes, textures, scripts, sounds, interface files) into " +
         "one new houseCARL-owned MO2 mod folder, so the copy you pick wins the virtual file system. The write " +
         "counterpart to " + ToolNames.AssetStatus + ", which reports which copy wins now. One file is a set of " +
         "one; the same call shape places forty.\n\n" +
         "Destination — assets=, the set of destinations; kind= sets the FaceGen slot for every formid= member that " +
         "does not name its own.\n" +
         "Source — source_provider= names whose copy to read, once for the whole set or per member; a member's own " +
         "source= names one exact file.\n" +
         "Lane — patch= names the new mod folder | into= adds to an existing houseCARL patch folder.\n" +
         "Transport — format= | max_chars=.\n\n" +
         "The write is atomic and originals are never touched. The placed copies do not win until you enable the " +
         "mod in MO2, and the response says so. A new folder registers at MO2's highest priority, so enabling it is " +
         "enough; an into= folder's priority is already fixed, so it must also be sorted above a loose winner in " +
         "another mod. Neither folder beats a winner in MO2's overwrite folder, which no mod out-ranks: move or delete " +
         "that copy.")]
    public static string Place(
        LoadOrderService svc,
        [Description("The destinations, all placed into one mod folder. Each: { formid?: 'XXXXXX:Plugin.esp', kind?: " +
                     "'mesh'|'tint', path?: 'meshes/...', source?: '<loose path>' | '<archive.bsa>|<entry>' | " +
                     "'<archive.bsa>' | '<Data-relative path>', source_provider?: 'SomeMod' | 'X - Textures.bsa' | '" +
                     AssetSourceChoice.WinnerToken + "' }, or \"@<absolute path>\" to read that same array from a " +
                     "JSON file. One destination is a set of one. A malformed member refuses the whole call and " +
                     "places nothing; a source that is ambiguous, absent or unreadable fails that member only, and " +
                     "the rest still place.")]
            JsonElement? assets = null,
        [Description("Optional. Whose copy to read, for every member that does not name its own; withheld (and said on that member's row) from a member whose own source= is an on-disk file, which already names one exact copy. " + AssetSourceChoice.WinnerToken + " (the sigil is part of the token) for whichever copy wins the VFS now, or the provider's name alone (a mod folder, 'overwrite', 'Data', or a BSA filename like 'X - Textures.bsa'), matched exactly, without " + ToolNames.AssetStatus + "'s ' (loose)' / ' (BSA)' annotation. A bare name always means a provider of that name. " + WriteSentences.PlaceSourceNameReachesUnticked + " An archive MO2 loads no plugin for is listed under neither name, so it is reachable only as an on-disk source= path. A name the active order already provides files under is answered by the active order, and a mod folder of that same name is not consulted. Omitted = the sole provider; a member with more than one contending provider fails.")]
            string? source_provider = null,
        [Description("Optional. Which FaceGen file every formid member places when the member does not say: 'mesh' (the head .nif) or 'tint' (the face .dds). Omit to place both. Ignored by path members. A member's own kind overrides this, but once this is set no member can get back to both, so for a mixed set leave it omitted and set kind per member.")]
            string? kind = null,
        [Description("Optional. Base name for the new houseCARL mod folder the files land in (default 'houseCARL_Assets'); auto-suffixed if taken, so a prior folder is never overwritten. Refused with into=.")]
            string? patch = null,
        [Description("Optional. Filename of an existing houseCARL patch mod to place into instead of a fresh folder, to add to it across calls. Found by the plugin's filename even if its MO2 mod folder was renamed; when two patches share a filename, pass the mod-folder name instead (folder and plugin names need not match).")]
            string? into = null,
        [Description("Optional. 'text' (default) | 'json' (the same data, machine-readable, with the accounting and the enable-and-sort instruction in the document).")]
            string? format = null,
        [Description("Optional. Character ceiling on the whole response; the row that would cross it is not written, and a notice says how many were held back. The write is unaffected. The accounting line and the enable-and-sort instruction are always inside the ceiling. 0 = the server default (about 80k).")]
            int max_chars = 0) => Guard.Tool(ToolNames.Place, () =>
    {
        // format first, so the unconfigured-MO2 prompt answers a json caller as a document.
        bool json = Wire.WantsJson(format, out var ferr);
        if (ferr is not null) return ferr;
        if (svc.ConfigPromptOrNull() is { } prompt)
            return json ? JsonWire.RenderError(prompt, null) : prompt;
        if (assets is not { } el || el.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return Refuse(json, "assets is empty. Pass one or more { path|formid, kind?, source?, source_provider? } destinations.");
        // The strict reader, not the SDK's binder: a member the shape does not declare is refused by name at its element, because a dropped one would place another provider's copy.
        var (items, listErr) = ListParams.Read<PlaceTarget>(el, "assets", "{path|formid, kind?, source?, source_provider?}");
        if (listErr is not null) return Refuse(json, listErr);
        return PlaceTargets(svc, items!, source_provider, kind, patch, into, max_chars, json);
    });

    /// <summary>The same call over destinations already read — the seam the probes and tests drive with typed members, while a real call comes through the strict reader.</summary>
    internal static string Place(LoadOrderService svc, PlaceTarget[] assets, string? source_provider = null,
                                 string? kind = null, string? patch = null, string? into = null,
                                 string? format = null, int max_chars = 0)
        => Guard.Tool(ToolNames.Place, () =>
    {
        bool json = Wire.WantsJson(format, out var ferr);
        if (ferr is not null) return ferr;
        if (svc.ConfigPromptOrNull() is { } prompt)
            return json ? JsonWire.RenderError(prompt, null) : prompt;
        if (assets is null || assets.Length == 0)
            return Refuse(json, "assets is empty. Pass one or more { path|formid, kind?, source?, source_provider? } destinations.");
        return PlaceTargets(svc, assets, source_provider, kind, patch, into, max_chars, json);
    });

    /// <summary>The one refusal shape, through its one owner — <see cref="Wire.Refuse"/>, which owns the prefix the json document strips.</summary>
    static string Refuse(bool json, string message) => Wire.Refuse(json, Wire.RefusalPrefix + message);

    static string PlaceTargets(LoadOrderService svc, PlaceTarget[] assets, string? source_provider,
                               string? kind, string? patch, string? into, int max_chars, bool json)
    {
        if (!string.IsNullOrWhiteSpace(patch) && !string.IsNullOrWhiteSpace(into))
            return Refuse(json, $"patch='{patch}' names a NEW mod folder for the placed files, but into='{into}' writes them into an existing houseCARL "
                              + "patch — the two lanes are exclusive. Drop patch= to write into that patch, or drop into= to make a new folder.");
        // The set-level slot is validated ONCE, under its own name: attributed to a member it would blame input the caller never wrote there.
        if (ParseSlot(NullIfBlank(kind), out var setKindErr) is null && setKindErr is not null)
            return Refuse(json, setKindErr);

        // Malformed members refuse the WHOLE call, like create; placement-time issues stay per-member.
        var all = new List<PlaceRequest>();
        var problems = new List<string>();
        // Destinations the set-level pole was withheld from, said on their row rather than dropped.
        var poleWithheld = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var door = svc.OpenWriteFormIdDoor();
        var modsRoot = svc.ModsRootOrNull;
        for (int i = 0; i < assets.Length; i++)
        {
            var a = assets[i];
            // A raw mods path as the DESTINATION is a malformed member and refuses the whole call; the same path as a SOURCE stays per-member.
            if (ModsPathAddress.Split(a.Path, modsRoot) is { } dest)
            {
                problems.Add(ModsPathAddress.Refusal($"assets[{i}]: ", a.Path!.Trim(),
                    ModsPathAddress.Address(dest.ModFolder, dest.RelPath, "path", "source_provider")));
                continue;
            }
            var reqs = MapTarget(door.Parse, a, source_provider, kind, $"assets[{i}]: ", out var err, out var withheld);
            if (err is not null) problems.Add(err);
            else
            {
                all.AddRange(reqs!);
                if (withheld) foreach (var r in reqs!) poleWithheld.Add(PoleKey(r.AssetPath));
            }
        }
        if (problems.Count > 0)
            return Refuse(json, $"refused — {problems.Count} malformed destination(s); nothing placed:\n  - " + string.Join("\n  - ", problems));

        var outcome = svc.PlaceAssets(all, patch, into);
        int cap = max_chars > 0 ? max_chars : 80_000;
        return json ? JsonWire.RenderPlaceOutcome(outcome, cap, poleWithheld)
                    : PlaceWire.Render(outcome, cap, poleWithheld);
    }

    /// <summary>Map one destination to its placement request(s): path is one, formid+kind is one computed FaceGen
    /// path, formid with NO kind is BOTH halves — which forbids a single loose or entry source, since one file
    /// cannot serve two. Every bad input is a NAMED error; <paramref name="poleWithheld"/> is true when a
    /// set-level pole existed but could not apply to this member.</summary>
    static List<PlaceRequest>? MapTarget(Func<string?, FormKey> parseFormId, PlaceTarget t,
                                         string? setProvider, string? setKind, string where, out string? error,
                                         out bool poleWithheld)
    {
        error = null;
        poleWithheld = false;
        bool hasFormid = !string.IsNullOrWhiteSpace(t.Formid);
        bool hasPath = !string.IsNullOrWhiteSpace(t.Path);
        if (hasFormid == hasPath) { error = $"{where}provide exactly one of formid or path."; return null; }

        var src = NullIfBlank(t.Source);
        // The set-level pole fills in only where it CAN apply: fanning it onto an on-disk source would refuse the member over input the caller never wrote there.
        var ownProv = NullIfBlank(t.SourceProvider);
        bool setApplies = AssetLayers.SourceTakesAProvider(src);
        poleWithheld = ownProv is null && !setApplies && NullIfBlank(setProvider) is not null;
        var prov = ownProv ?? (setApplies ? NullIfBlank(setProvider) : null);

        if (hasPath)
            return new List<PlaceRequest> { new(t.Path!.Trim(), src, prov) };

        FormKey fk;
        try { fk = parseFormId(t.Formid); }
        catch (Exception ex) { error = FormIdDoor.Sentence(ex, where, $"{where}bad formid '{t.Formid}' ({ex.Message}). Expected 'XXXXXX:Plugin.esp'."); return null; }

        var slot = ParseSlot(NullIfBlank(t.Kind) ?? NullIfBlank(setKind), out var slotErr);
        if (slotErr is not null) { error = $"{where}{slotErr}"; return null; }

        if (slot is { } s)                                            // explicit mesh|tint → one file
            return new List<PlaceRequest> { new(FaceGenPath.For(fk, s), src, prov) };

        // kind omitted at both levels means both slots. FULLY-QUALIFIED matches the service's routing: a relative
        // '.bsa' is a Data-relative asset path, and one such path cannot serve two slots.
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
        // Flagged, because a refusal about this member's source= has to hand back a form this member will accept.
        foreach (var (_, rel) in FaceGenPath.Both(fk)) reqs.Add(new PlaceRequest(rel, src, prov) { BothSlots = true });
        return reqs;
    }

    /// <summary>Parse the FaceGen slot token; blank is unspecified (both files) and a bad token is a named error.</summary>
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

    /// <summary>Key a withheld-pole note the way the result row will read back: the placer validates every destination, so a raw key would miss a 'meshes/x.nif' member's row.</summary>
    static string PoleKey(string path)
    {
        try { return AssetResolver.ValidateRelPath(path); }
        catch (ArgumentException) { return path; }
    }

    static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>Renders a <see cref="PlaceOutcome"/> through the shared batch skeleton: the header, the discovery
/// caveats, one capped row per destination, then the accounting and the explicit enable-and-sort instruction.
/// Those last two are always written and charged INSIDE the cap, so a truncated list still says what it dropped.</summary>
static class PlaceWire
{
    public static string Render(PlaceOutcome o, int cap, IReadOnlySet<string>? poleWithheld = null)
    {
        if (o.Error is not null) return "error: " + o.Error;

        int placed = 0;
        foreach (var r in o.Results) if (r.Placed) placed++;
        int failed = o.Results.Count - placed;
        var modFolder = o.ModFolder is null ? null : Path.GetFileName(o.ModFolder);

        var header = new StringBuilder()
            .Append("placed ").Append(placed).Append(" of ").Append(o.Results.Count).Append(" asset(s)")
            .Append(failed > 0 ? $" ({failed} failed)" : "")
            .Append(modFolder is null ? "" : $"\nmod folder: {modFolder}").ToString();

        // Everything below the rows is charged before the first row is laid, so a filled render answers inside max_chars.
        var body = BatchRender.Render(
            header, o.Results, "asset(s)", cap,
            // Whole warnings, in order, and the list STOPS at the first one that does not fit: skipping a long warning for a short one hands back a list that looks complete and is not.
            (sb, room) =>
            {
                // WHICH loose roots would not read, once for the batch and before the rows, so a row's hedge has a folder to point at.
                // Cut against the room handed in, so the oversize remedy's unbounded re-run prices the whole list.
                sb.Append(BatchRender.RootFailureLines(o.RootFailures, room.Cap));
                if (o.Warnings.Count == 0) return;
                var lines = room.Less(WarningsOmitted(o.Warnings.Count, cap).Length);
                int w = 0;
                for (; w < o.Warnings.Count; w++)
                    if (!lines.TryAppend(sb, "[!] discovery: " + o.Warnings[w] + "\n")) break;
                if (w < o.Warnings.Count) sb.Append(WarningsOmitted(o.Warnings.Count - w, cap));
            },
            (sb, r, _) => AppendResult(sb, r, modFolder, o.FreshFolder, poleWithheld?.Contains(r.AssetPath) == true),
            out int rendered,
            reserve: TrailerReserve(o, modFolder, placed, failed));

        var sb2 = new StringBuilder(body).Append('\n');
        sb2.Append("\ntotal=").Append(o.Results.Count).Append(" rendered=").Append(rendered)
           .Append(" placed=").Append(placed).Append(" failed=").Append(failed)
           .Append(" truncated=").Append(rendered < o.Results.Count ? "true" : "false").Append('\n');

        if (o.LeftoverFolder is not null)
            sb2.Append("note: ").Append(LeftoverNote(o.LeftoverFolder)).Append('\n');

        if (placed > 0) sb2.Append('\n').Append(EnableAndSort(o, modFolder, rendered)).Append('\n');

        return RenderCap.Settle(sb2.ToString().TrimEnd('\n'), cap);
    }

    /// <summary>What a cut discovery-warning list cost, named rather than left as a gap in a list that reads whole.</summary>
    static string WarningsOmitted(int omitted, int cap) =>
        "[!] " + omitted + " more discovery warning(s) omitted at max_chars=" + cap + "; raise max_chars to see them\n";

    /// <summary>The chars this render owes below its rows, at their widest spelling.</summary>
    static int TrailerReserve(PlaceOutcome o, string? modFolder, int placed, int failed)
    {
        int n = o.Results.Count;
        int counts = $"\n\ntotal={n} rendered={n} placed={placed} failed={failed} truncated=false\n".Length;
        int leftover = o.LeftoverFolder is null ? 0 : ("note: " + LeftoverNote(o.LeftoverFolder) + "\n").Length;
        // Which sentence it ends on turns on how many rows reached the page, so the reserve takes the longer end.
        int enable = placed > 0
            ? 2 + Math.Max(EnableAndSort(o, modFolder, 0).Length, EnableAndSort(o, modFolder, n).Length)
            : 0;
        return counts + leftover + enable;
    }

    /// <summary>A fresh folder kept because the write half-landed, said once for both transports.</summary>
    internal static string LeftoverNote(string leftoverFolder)
        => $"the fresh folder at '{leftoverFolder}' holds a partial result — delete it or retry with into=.";

    /// <summary>The instruction a placement is incomplete without: written bytes do not win the VFS until the mod
    /// is enabled, and on the into= lane sorted above the current winner. One home, because both transports carry
    /// it verbatim. The five arms, and why the lane decides as much as contention does, are in
    /// docs/architecture/assets.md. <paramref name="rendered"/> is how many rows reached the page, so a contended
    /// row max_chars cut is named as cut rather than pointed at with "listed above".</summary>
    internal static string EnableAndSort(PlaceOutcome o, string? modFolder, int rendered)
    {
        bool anyContended = false, shownContended = false, anyOverwrite = false, shownOverwrite = false, anyLosesOnEnable = false;
        int placedRows = 0, destinationRows = 0;
        for (int i = 0; i < o.Results.Count; i++)
        {
            var r = o.Results[i];
            if (!r.Placed) continue;
            placedRows++;
            if (r.CurrentWinner is null) continue;
            // A row the destination folder itself won owes no instruction: it keeps winning, so counting it as contention would ask for a sort above this folder.
            if (r.WinnerIsDestination) { destinationRows++; continue; }
            // An overwrite winner is not reachable by enabling or sorting, so it is counted apart and answered apart.
            if (r.WinnerIsOverwrite) { anyOverwrite = true; if (i < rendered) shownOverwrite = true; continue; }
            // A BSA or Data winner loses to any enabled mod's loose copy, so it owes no sort on either lane.
            if (r.WinnerLosesOnEnable) { anyLosesOnEnable = true; continue; }
            anyContended = true;
            if (i < rendered) shownContended = true;
        }
        var folder = modFolder ?? "(the new folder)";
        // Every placed row won by the destination folder: it is enabled already, so there is nothing left to do.
        if (placedRows > 0 && destinationRows == placedRows)
            return "the placed file(s) went into '" + folder + "', which already provided these path(s) and already "
                 + "wins the VFS for them — it is enabled, so there is nothing to enable or sort (sort it above any "
                 + "mod you later add that also provides them).";
        var sort = anyContended
            ? (o.FreshFolder
                ? ". MO2 registers a folder it has not seen at the highest priority, so once enabled the placed copy " +
                  "out-ranks the current winner(s) with no sorting (sort it above any mod you later add that also provides these path(s))."
                : shownContended
                    ? " and SORT it (left pane) ABOVE the current winner(s) listed above. Only then does the placed copy win."
                    : " and SORT it (left pane) ABOVE the current winner(s) — max_chars cut the row(s) naming them from " +
                      "this render, so raise max_chars and re-read to see which. Only then does the placed copy win.")
            : anyOverwrite
                ? "."
                : anyLosesOnEnable
                    ? ". Once enabled the placed copy wins — a loose file beats a BSA, and any enabled mod beats the " +
                      "game's Data folder (sort it above any mod you later add that also provides these path(s))."
                    : ". Nothing else provided these path(s), so once enabled the placed copy wins (sort it above any mod you later add that also provides them).";
        if (anyOverwrite)
            sort += " MO2's overwrite folder sits ABOVE every mod in the VFS, so for the path(s) it currently wins "
                  + "neither enabling nor sorting is enough — move or delete the overwrite copy of "
                  + (shownOverwrite
                        ? "each path whose winner reads 'overwrite (loose)' above."
                        : "the path(s) it wins — max_chars cut the row(s) naming them from this render, so raise max_chars and re-read to see which.");
        return "IMPORTANT — \"wrote it\" is not \"it wins\": the placed file(s) do NOT win the VFS yet. Enable the mod '"
             + folder + "' in MO2" + sort;
    }

    static void AppendResult(StringBuilder sb, PlaceResult r, string? modFolder, bool freshFolder, bool poleWithheld)
    {
        // An input the call carried but this destination could not use is SAID, not dropped, or it reads as honoured.
        void Withheld()
        {
            if (poleWithheld)
                sb.Append("        note: set-level source_provider not applied: source is one exact file\n");
        }

        if (!r.Placed) { sb.Append("  FAIL  ").Append(r.AssetPath).Append("  ").Append(r.Error).Append('\n'); Withheld(); return; }

        sb.Append("  OK    ").Append(r.AssetPath).Append("  (").Append(r.Bytes).Append(" bytes from ").Append(r.SourceDesc).Append(")\n");
        Withheld();
        // Bytes served out of a mod MO2 does not load look like any other placement, so say so on their own line.
        if (r.SourceOffOrderProvider is { } offOrder)
            sb.Append("        ").Append(WriteSentences.PlaceSourceOffOrder(offOrder, r.SourceOffOrderOwnerEnabled)).Append('\n');
        // Name the destination folder rather than "the mod": the off-order line above can put a SECOND mod in scope.
        sb.Append("        ").Append(WinnerLine(r, modFolder, freshFolder)).Append('\n');
    }

    /// <summary>What this destination's current VFS winner means for the caller, in five arms
    /// (docs/architecture/assets.md). One home, because the json twin's <c>winner_note</c> says exactly this.</summary>
    internal static string WinnerLine(PlaceResult r, string? modFolder, bool freshFolder)
    {
        var folder = modFolder ?? (freshFolder ? "(the new folder)" : "(the patch folder)");
        if (r.CurrentWinner is null)
            return $"nothing else provides this path — once '{folder}' is enabled, the placed copy wins";
        if (r.WinnerIsDestination)
            return $"'{folder}' already provided this path — the placed copy replaces its own earlier copy and keeps winning";
        if (r.WinnerIsOverwrite)
            return $"currently wins the VFS: {r.CurrentWinner} — MO2's overwrite folder is ABOVE every mod, so no enable "
                 + $"and no sort out-ranks it; move or delete the overwrite copy of this path, then '{folder}' wins";
        if (r.WinnerLosesOnEnable)
            return $"currently wins the VFS: {r.CurrentWinner} — a loose file in an enabled mod beats it at any priority, "
                 + $"so once '{folder}' is enabled the placed copy wins";
        return freshFolder
            ? $"currently wins the VFS: {r.CurrentWinner} — a folder MO2 has not seen registers at the highest priority, so '{folder}' out-ranks it once enabled"
            : $"currently wins the VFS: {r.CurrentWinner} — sort '{folder}' ABOVE it";
    }
}

/// <summary>One destination off the wire: a FormID (+ optional slot) or a Data-relative path, plus the optional per-member source and pole; either pole may be given once for the whole set.</summary>
public sealed record PlaceTarget
{
    [JsonPropertyName("formid"), Description("The NPC's FormID 'XXXXXX:Plugin.esp'; houseCARL computes the FaceGen path. Provide this or path.")]
    public string? Formid { get; init; }

    [JsonPropertyName("kind"), Description("With formid: 'mesh' (head .nif) or 'tint' (face .dds). Omit to take the call's kind=, or both if that is omitted too. Ignored with path.")]
    public string? Kind { get; init; }

    [JsonPropertyName("path"), Description("A Data-relative destination path (e.g. 'meshes/actors/...'). Provide this or formid. A drive-rooted or '..'-escaping path fails this member, except a path inside MO2's mods folder, which refuses the whole call and names the source_provider= address to use.")]
    public string? Path { get; init; }

    [JsonPropertyName("source"), Description("The copy to place for this destination: a Data-relative path resolved through the VFS, a full loose file path, '<archive.bsa path>|<entry inside>', or a full '.bsa' path alone (the entry is taken to be the destination, which pulls one file out of a BSA as a loose override). A source path different from the destination is a rename: one file's bytes land under another's name, which is how a baked FaceGen head is carried onto a different NPC's FormID path. With no source, the destination path itself is resolved: the sole provider, or the one source_provider names; when several contend and none is named, this member fails and lists them. With formid and no kind, a source must be a full '.bsa' path, since one file cannot serve both slots.")]
    public string? Source { get; init; }

    [JsonPropertyName("source_provider"), Description("Whose copy to read for this destination, overriding the call's source_provider=, in the same spelling: "
        + AssetSourceChoice.WinnerToken + " or the provider's name alone. It applies with a Data-relative source (whose copy to read it from) and with no source "
        + "(whose copy of the destination path to place, which settles the contention an omitted source otherwise fails on). Not valid with an on-disk source.")]
    public string? SourceProvider { get; init; }
}
