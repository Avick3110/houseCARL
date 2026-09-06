using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using Mutagen.Bethesda.Plugins;
using HousecarlCore;

namespace HousecarlMcp;

/// <summary>housecarl_place — the S2 write tool. One call places any number of chosen file copies as winning
/// overrides in ONE houseCARL-owned MO2 mod folder, the write counterpart to housecarl_asset_status. One destination
/// is a set of one: there is no single-vs-bulk split in the schema. It writes the source it is handed and
/// auto-resolves only when exactly one copy exists; which copy is correct is the caller's judgement, never the
/// tool's. Source bytes are read in process (a loose file, or one BSA entry through Mutagen — no BSArch), the write
/// is crash-atomic, and a placement never wins on write: the response always states the current winner and the
/// required MO2 enable + sort.</summary>
[McpServerToolType]
public static class PlaceTools
{
    [McpServerTool(Name = ToolNames.Place, Title = "Place chosen file copies so they win MO2's VFS"),
     Description(
         "Place chosen copies of files — ANY Data-relative file (a mesh, texture, script, sound, interface, etc.) — into " +
         "ONE NEW houseCARL-owned MO2 mod folder, so the copy YOU pick wins the virtual file system. The WRITE " +
         "counterpart to " + ToolNames.AssetStatus + " (which reports which copy currently wins). ONE surface: WHERE the " +
         "bytes land (assets=) x WHOSE copy to read (the SOURCE pole) x WHICH folder it goes in (the LANE) x how it " +
         "reads back (TRANSPORT). One file is a set of one — the same call shape places forty.\n\n" +
         "Each axis's grammar is on its own parameters:\n" +
         "DESTINATION — assets=, the set of destinations; kind= sets the FaceGen slot for every formid= member that " +
         "does not name its own.\n" +
         "SOURCE — source_provider= names whose copy to read, once for the whole set or per member; a member's own " +
         "source= names one exact file.\n" +
         "LANE — patch= names the NEW mod folder | into= adds to an EXISTING houseCARL patch folder.\n" +
         "TRANSPORT — format= | max_chars=.\n\n" +
         "The write is crash-atomic and originals are never touched. IMPORTANT (and reported back): the placed copies " +
         "do NOT win on write — you must ENABLE the new mod in MO2 and SORT it above the current winner.")]
    public static string Place(
        LoadOrderService svc,
        [Description("SELECT: the destinations, all placed into ONE reviewable mod folder. Each: { formid?: 'XXXXXX:Plugin.esp', kind?: 'mesh'|'tint' (omit with formid to place BOTH FaceGen files), path?: 'meshes/...', source?: '<loose path>' | '<archive.bsa>|<entry>' | '<archive.bsa>' | '<Data-relative path>', source_provider?: 'SomeMod' | 'X - Textures.bsa' | '" + AssetSourceChoice.WinnerToken + "' } — or \"@<absolute path>\" to read that SAME array from a JSON file. Set-valued at every size — one destination is a set of one. A member the shape does not declare is refused BY NAME at its element, never silently dropped. A malformed member — a bad FormID, a bad kind, neither or both of formid and path, or a formid member with no kind whose source= is not a FULL '.bsa' path — refuses the WHOLE call with per-member reasons and places nothing; a source that is ambiguous, absent or unreadable is a PER-MEMBER error and the rest still place. Each member's own description says what it takes.")]
            JsonElement? assets = null,
        [Description("SOURCE: whose copy to read, for EVERY member that does not name its own — withheld (and said on that member's row) from one whose own source= is an on-disk file, which already names one exact copy. " + AssetSourceChoice.WinnerToken + " (the sigil is part of the token) for whichever copy currently wins the VFS, or the provider's NAME ALONE — a mod folder, 'overwrite', 'Data', or a BSA filename like 'X - Textures.bsa' — matched exactly, without " + ToolNames.AssetStatus + "'s ' (loose)' / ' (BSA)' annotation. A bare name ALWAYS means a provider of that name. " + WriteSentences.PlaceSourceNameReachesUnticked + " Note that a file inside an active mod's archive is listed (and reached) under the ARCHIVE's name, not the mod's — and an archive MO2 loads no plugin for is listed under neither, so it is reachable only as an on-disk source= path. A name the active order already provides files under is answered by the active order, so a mod folder of that same name is not consulted. Omitted = the sole provider, refused if more than one contends.")]
            string? source_provider = null,
        [Description("Which FaceGen file every formid= member places, when the member does not say: 'mesh' (the head .nif) or 'tint' (the face .dds). Omit to place BOTH. Ignored by path= members. A member's own kind= only NARROWS this to the other slot — once set here, no member can widen back to both, so leave it omitted and set kind= per member when the set is mixed.")]
            string? kind = null,
        [Description("LANE: base name for the NEW houseCARL mod folder the files land in (default 'houseCARL_Assets'); auto-suffixed if taken, so a prior folder is never clobbered.")]
            string? patch = null,
        [Description("LANE: filename of an EXISTING houseCARL patch mod to place into instead of a fresh folder (accumulate across calls). Found by the plugin's filename even if you've renamed its MO2 mod folder; for two patches sharing a filename, pass the mod-folder name here instead (folder & plugin names need not match).")]
            string? into = null,
        [Description("TRANSPORT: 'text' (default) | 'json' (the same data, machine-readable, the accounting and the enable+sort instruction in-band).")]
            string? format = null,
        [Description("TRANSPORT: character ceiling on the per-destination list. Past it, trailing rows are dropped with an explicit notice (never silent); the WRITE is unaffected, and the accounting line and the enable+sort instruction always render. 0 = the server default (~80k).")]
            int max_chars = 0) => Guard.Tool(ToolNames.Place, () =>
    {
        // format first, so the unconfigured-MO2 prompt answers a json caller as a document.
        bool json = Wire.WantsJson(format, out var ferr);
        if (ferr is not null) return ferr;
        if (svc.ConfigPromptOrNull() is { } prompt)
            return json ? JsonWire.RenderError(prompt, null) : prompt;
        if (assets is not { } el || el.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return Refuse(json, "assets is empty. Pass one or more { path|formid, kind?, source?, source_provider? } destinations.");
        // The strict reader, not the SDK's binder: a member the shape does not declare is refused by name at its
        // element. Dropped silently, a mistyped source_provider would auto-resolve some other provider's copy and
        // read back as a successful place.
        var (items, listErr) = ListParams.Read<PlaceTarget>(el, "assets", "{path|formid, kind?, source?, source_provider?}");
        if (listErr is not null) return Refuse(json, listErr);
        return PlaceTargets(svc, items!, source_provider, kind, patch, into, max_chars, json);
    });

    /// <summary>The same call over destinations already read — the seam the probes and tests drive with typed
    /// members, while a real call comes through the strict reader above.</summary>
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

    /// <summary>The one refusal shape, through its one owner — <see cref="Wire.Refuse"/>, which owns the
    /// <see cref="Wire.RefusalPrefix"/> the json document strips. A shorthand so the call sites below need not repeat
    /// the prefix and the transport flag both, never a second definition of the shape.</summary>
    static string Refuse(bool json, string message) => Wire.Refuse(json, Wire.RefusalPrefix + message);

    static string PlaceTargets(LoadOrderService svc, PlaceTarget[] assets, string? source_provider,
                               string? kind, string? patch, string? into, int max_chars, bool json)
    {
        // The set-level slot is validated ONCE, under its own name: attributed to a member it would blame input the
        // caller never wrote there and repeat it per member, and on a set of nothing but path= members, which ignore
        // it, a bad token would never be noticed at all.
        if (ParseSlot(NullIfBlank(kind), out var setKindErr) is null && setKindErr is not null)
            return Refuse(json, setKindErr);

        // Malformed members refuse the WHOLE call (all-or-nothing, like create); placement-time issues
        // (ambiguous/absent/unreadable source) are per-member (the resolver isn't consulted until the place loop).
        var all = new List<PlaceRequest>();
        var problems = new List<string>();
        // Destinations the set-level pole was withheld from. Said on their row rather than dropped: the pole's own
        // refusal sentence is that a pole which cannot apply is stated, never silently ignored.
        var poleWithheld = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var door = svc.OpenWriteFormIdDoor();
        for (int i = 0; i < assets.Length; i++)
        {
            var a = assets[i];
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

    /// <summary>Map one destination to its placement request(s): path → one request; formid+kind → one request (the
    /// computed FaceGen path); formid with NO kind → BOTH mesh+tint. Exactly one of formid/path is required. The
    /// set-level pole and slot fill in for a member that names neither. A both-expansion forbids a single
    /// loose/entry source (it can't serve two different files) — only a FULLY-QUALIFIED '.bsa' source (entry derived
    /// per slot) or auto-resolve. Every bad input is a NAMED error returned via <paramref name="error"/>, never a
    /// silent skip. <paramref name="poleWithheld"/> is true when a set-level pole existed but could not apply to this
    /// member — the caller states it on the member's row rather than dropping it.</summary>
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
        // The set-level pole fills in only where it CAN apply. An on-disk source already names one exact copy and the
        // placer refuses a pole against it, so fanning the call's pole onto such a member would refuse it over input
        // the caller never wrote there. A member's OWN pole still reaches that refusal: the caller did write it.
        var ownProv = NullIfBlank(t.SourceProvider);
        bool setApplies = LoadOrderService.SourceTakesAProvider(src);
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

        // kind omitted at both levels → both mesh + tint.
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

    /// <summary>Parse the FaceGen slot token. Null/blank ⇒ null (unspecified — both files). A bad token is a named
    /// error. Lenient synonyms for the two file kinds.</summary>
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

    /// <summary>Key a withheld-pole note the way the result row will read back: the placer validates every
    /// destination through <see cref="AssetResolver.ValidateRelPath"/> (forward slashes folded to backslashes), so a
    /// raw key would miss a 'meshes/x.nif' member's row and drop the note. A path the validator rejects is reported
    /// raw on its failure row, so it is keyed raw too.</summary>
    static string PoleKey(string path)
    {
        try { return AssetResolver.ValidateRelPath(path); }
        catch (ArgumentException) { return path; }
    }

    static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>Renders a <see cref="PlaceOutcome"/> through the shared batch skeleton: the count and mod folder as the
/// header, the discovery caveats as the alarms block, one capped row per destination (its source and the current VFS
/// winner to sort above, or a per-destination error), then the §2.1 accounting and the explicit "this does not win
/// until you enable + sort the mod in MO2" instruction. Those last two sit OUTSIDE the cap on purpose: a truncated
/// list must still say how much it dropped and what the caller has to do next.</summary>
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

        int rendered = 0;
        var body = BatchRender.Render(
            header, o.Results, "asset(s)", cap,
            sb => { foreach (var w in o.Warnings) sb.Append("[!] discovery: ").Append(w).Append('\n'); },
            (sb, r) => { rendered++; AppendResult(sb, r, modFolder, poleWithheld?.Contains(r.AssetPath) == true); });

        var sb2 = new StringBuilder(body).Append('\n');
        sb2.Append("\ntotal=").Append(o.Results.Count).Append(" rendered=").Append(rendered)
           .Append(" placed=").Append(placed).Append(" failed=").Append(failed)
           .Append(" truncated=").Append(rendered < o.Results.Count ? "true" : "false").Append('\n');

        if (o.LeftoverFolder is not null)
            sb2.Append("note: ").Append(LeftoverNote(o.LeftoverFolder)).Append('\n');

        if (placed > 0) sb2.Append('\n').Append(EnableAndSort(o, modFolder, rendered)).Append('\n');

        return sb2.ToString().TrimEnd('\n');
    }

    /// <summary>A fresh folder kept because the write half-landed, said once for both transports.</summary>
    internal static string LeftoverNote(string leftoverFolder)
        => $"the fresh folder at '{leftoverFolder}' holds a partial result — delete it or retry with into=.";

    /// <summary>The instruction a placement is incomplete without: written bytes do not win the VFS until the mod is
    /// enabled, and sorted above the current winner when one exists. One home, because both transports have to carry
    /// it verbatim — a json caller told only that the write succeeded would enable nothing.
    /// <para><paramref name="rendered"/> is how many rows the render actually got onto the page. Rows come out in
    /// order, so those are the first <paramref name="rendered"/> results — and a contended row max_chars cut cannot
    /// be pointed at with "listed above", so the sentence says the row was cut instead of naming a winner the
    /// document never shows.</para></summary>
    internal static string EnableAndSort(PlaceOutcome o, string? modFolder, int rendered)
    {
        bool anyContended = false, shownContended = false;
        for (int i = 0; i < o.Results.Count; i++)
        {
            if (!(o.Results[i].Placed && o.Results[i].CurrentWinner is not null)) continue;
            anyContended = true;
            if (i < rendered) { shownContended = true; break; }
        }
        var sort = shownContended
            ? " and SORT it (left pane) ABOVE the current winner(s) listed above. Only then does the placed copy win."
            : anyContended
                ? " and SORT it (left pane) ABOVE the current winner(s) — max_chars cut the row(s) naming them from " +
                  "this render, so raise max_chars and re-read to see which. Only then does the placed copy win."
                : ". Nothing else provided these path(s), so once enabled the placed copy wins (sort it above any mod you later add that also provides them).";
        return "IMPORTANT — \"wrote it\" is not \"it wins\": the placed file(s) do NOT win the VFS yet. Enable the mod '"
             + (modFolder ?? "(the new folder)") + "' in MO2" + sort;
    }

    static void AppendResult(StringBuilder sb, PlaceResult r, string? modFolder, bool poleWithheld)
    {
        // An input the call carried but this destination could not use is SAID, not dropped: the pole's own refusal
        // sentence is that a provider which cannot apply is stated, so withholding it silently would read as honoured.
        void Withheld()
        {
            if (poleWithheld)
                sb.Append("        note: set-level source_provider not applied: source is one exact file\n");
        }

        if (!r.Placed) { sb.Append("  FAIL  ").Append(r.AssetPath).Append("  ").Append(r.Error).Append('\n'); Withheld(); return; }

        sb.Append("  OK    ").Append(r.AssetPath).Append("  (").Append(r.Bytes).Append(" bytes from ").Append(r.SourceDesc).Append(")\n");
        Withheld();
        // Bytes served out of a mod MO2 does not load look like any other placement on the line above, so say so on
        // their own line. This is about the SOURCE; the destination's enable+sort is the block below the list.
        if (r.SourceOffOrderProvider is { } offOrder)
            sb.Append("        ").Append(WriteSentences.PlaceSourceOffOrder(offOrder, r.SourceOffOrderOwnerEnabled)).Append('\n');
        // Name the destination folder rather than saying "the mod": the off-order line above can put a SECOND mod in
        // scope, and it ends by saying enabling THAT one is not required.
        sb.Append(r.CurrentWinner is not null
            ? $"        currently wins the VFS: {r.CurrentWinner} — sort the new mod ABOVE it\n"
            : $"        nothing else provides this path — once '{modFolder ?? "(the new folder)"}' is enabled, the placed copy wins\n");
    }
}

/// <summary>One destination off the wire. A FormID (+ optional slot) or a Data-relative path, plus the optional
/// per-member source and source-provider pole; either pole may instead be given once for the whole set, and the
/// member's own value wins.</summary>
public sealed record PlaceTarget
{
    [JsonPropertyName("formid"), Description("The NPC's FormID 'XXXXXX:Plugin.esp' — houseCARL computes the FaceGen path. Omit kind to place BOTH the mesh and the tint. Provide this OR path.")]
    public string? Formid { get; init; }

    [JsonPropertyName("kind"), Description("With formid: 'mesh' (head .nif) or 'tint' (face .dds). Omit to take the call's kind=, or BOTH if that is omitted too. Ignored with path.")]
    public string? Kind { get; init; }

    [JsonPropertyName("path"), Description("A Data-relative destination path (e.g. 'meshes/actors/...'), instead of formid. Provide this OR formid. A drive-rooted or '..'-escaping path is rejected.")]
    public string? Path { get; init; }

    [JsonPropertyName("source"), Description("The copy to place, for THIS destination — a source names ONE file and a set of destinations is many, so it is PER MEMBER: a DATA-RELATIVE path resolved through the VFS, a full loose file path, '<archive.bsa path>|<entry inside>', or just a '.bsa' path (the entry is taken to be the destination — a quick way to pull ONE file out of a BSA as a loose override). A source path DIFFERENT from the destination is a RENAME: the bytes of one file land under another file's name, which is how a baked FaceGen head is carried onto a different NPC's FormID path. With no source=, the DESTINATION path is resolved instead: the sole provider the VFS offers, or the one source_provider= names, REFUSING (and listing the providers) when several contend and none was named — it will not guess which is correct. With formid and no kind, an explicit source must be a FULLY-QUALIFIED '.bsa' path (a relative one is a Data-relative asset path, and one path cannot serve both slots).")]
    public string? Source { get; init; }

    [JsonPropertyName("source_provider"), Description("Whose copy to read for a VFS-resolved source, for THIS destination — overriding the call's source_provider=: "
        + AssetSourceChoice.WinnerToken + " for the current VFS winner, or the provider's NAME ALONE (a mod folder, 'overwrite', "
        + "'Data', or a BSA filename) — not asset_status's ' (loose)' / ' (BSA)' annotation. A bare name always means a provider "
        + "of that name. " + WriteSentences.PlaceSourceNameReachesUnticked + " Applies BOTH with a Data-relative source= (whose copy to read it FROM) and with NO source= at all "
        + "(whose copy of the DESTINATION path to place) — in the second case it is what resolves the contention an omitted "
        + "source is otherwise refused for. Not valid with an on-disk source.")]
    public string? SourceProvider { get; init; }
}
