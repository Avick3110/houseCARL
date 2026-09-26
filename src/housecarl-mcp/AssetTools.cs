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
    [McpServerTool(Name = ToolNames.AssetStatus, ReadOnly = true, Title = "Asset status — which mod/BSA wins for a Data-relative path"),
     Description(
         "Resolve one or more Data-relative asset paths through Mod Organizer 2's virtual file system and report, for " +
         "each, WHICH copy the game actually uses: the winning source, every source that provides it (loose mods, the " +
         "overwrite folder, the game Data folder, and active BSAs), whether more than one source contends, and whether " +
         "the asset is absent. Precedence is the real engine/MO2 rule — loose files beat BSA-packed, among loose the " +
         "higher-priority mod (then overwrite) wins, among BSAs the later-loaded plugin's archive wins. This is the " +
         "file-layer counterpart to the load-order winner of a record: use it to answer 'which mod provides this file / " +
         "is this texture loose or in a BSA / is this asset even present / why isn't my " +
         "override applying' for ANY Data-relative path — mesh, texture, script, sound, interface. Pass " +
         "asset_paths = one or more paths RELATIVE to the Data folder, and/or under = a Data-relative DIRECTORY or " +
         "glob, which resolves every file the VFS provides beneath it — one call over " +
         "'meshes/actors/character/facegendata/facegeom/Skyrim.esm' answers for every facegen mesh a master defines. " +
         "Or formids = NPC FormIDs: BOTH halves of each one's FaceGen pair are derived and " +
         "resolved (head mesh + face tint), each row naming the OTHER half's winner beside its own — a whole-order " +
         "dark-face pairing sweep in ONE call. SELECT forms compose, and every " +
         "list-valued one takes '@<absolute path>' in place of the inline list. An archive that cannot be read, or a " +
         "missing Skyrim.ini base-archive list, is reported LOUD — so an 'absent' answer is never silently " +
         "trusted. format='json' returns the same data machine-readably, with the same " +
         "accounting in-band. TRANSPORT — format= | limit= | offset= | max_chars= | counts_only= | to_file=. BOUND: 1,200,000 paths " +
         "RESOLVED a call — the window where limit= takes one, except under to_file= and counts_only=, which each " +
         "resolve the whole selection — and past it the call refuses up front with the count and the estimate. Read-only: " +
         "resolves nothing to disk, writes nothing, changes no load order.")]
    public static string AssetStatus(
        LoadOrderService svc,
        [Description("The Data-relative asset path(s) to resolve, e.g. " +
                     "'textures/armor/iron/cuirass_1.dds' or 'meshes/clutter/common/tankard01.nif'. One or many; resolved " +
                     "in order, results returned in the same order. Paths are relative to the game's Data folder; " +
                     "forward or back slashes both fine, and a drive-rooted or '..'-escaping path is rejected " +
                     "per-path rather than failing the call. " +
                     "Optional when under= or formids= is given. Takes [\"@<absolute path>\"] in place of the inline " +
                     "list: a plain list file one path per line, or an artifact this tool wrote with to_file= (whose " +
                     "identity column is 'path'). A path list is NOT epoch-checked — a path is a string and every " +
                     "answer about it is read live off the VFS — so yesterday's sweep re-enters here after you have " +
                     "changed the order, which is the point.")]
            string[]? asset_paths = null,
        [Description("Optional. Data-relative DIRECTORY or glob selector(s): every file the load order provides beneath " +
                     "it (loose and BSA both) is resolved, e.g. " +
                     "'meshes/actors/character/facegendata/facegeom/Skyrim.esm' for one master's whole facegen set. " +
                     "Wildcards: '*' matches within one path segment, '?' one character in a segment, '**' across " +
                     "separators — 'textures/actors/character/**/*.dds'. Matches are added after any asset_paths, " +
                     "sorted, with duplicates dropped. A selector that matches nothing says so rather than passing as " +
                     "an empty sweep.")]
            string[]? under = null,
        [Description("SELECT: NPC FormIDs ('XXXXXX:Plugin.esp' — 6 hex digits, a colon, then the DEFINING master's " +
                     "filename). Each one contributes BOTH halves of that NPC's FaceGen pair — " +
                     "'meshes\\actors\\character\\facegendata\\facegeom\\<master>\\00<6hex>.nif' and " +
                     "'textures\\...\\facetint\\<master>\\00<6hex>.dds' — as two rows, each carrying the OTHER half's " +
                     "winner beside its own, because a dark face is almost always the two halves winning from " +
                     "different mods (or one of them winning nowhere). That verdict is taken on the winning copy's " +
                     "MO2 LAYER, so two archives of one mod are not a split — and neither are two files both " +
                     "installed into the game's own Data folder, or both in overwrite, which are layers rather than " +
                     "mods. The path is a PURE transform of the FormID, so " +
                     "this lane reads no record and costs no per-id winner seek; the folder is the defining master in " +
                     "the FormID, never the conflict winner. A malformed FormID is ONE error row, not a failed call. " +
                     "Takes [\"@<absolute path>\"] in place of the inline list — a plain list file, or a " +
                     "housecarl_records artifact, whose 'formid' column becomes the list (epoch-checked against " +
                     "the current build): records types=[\"NPC_\"] to_file= then formids=[\"@<that file>\"] is the " +
                     "whole-order sweep.")]
            string[]? formids = null,
        [Description("Optional. Max paths to resolve and render from the selection. 0 = no limit. Ignored by to_file=, " +
                     "which covers the WHOLE selection — the artifact is never a window. Under counts_only=true this " +
                     "caps the census TABLE's rows instead: the census covers the whole selection too, and its table " +
                     "is what needs paging.")]
            int limit = 0,
        [Description("Optional. Where in the selection the rendered window starts, for paging a large under= sweep. 0 = the beginning. Refused with to_file= and with counts_only=, neither of which takes a selection window.")]
            int offset = 0,
        [Description("TRANSPORT: write the COMPLETE result to this ABSOLUTE .jsonl path as an artifact (line 1 = " +
                     "manifest) and render only the manifest inline — the same convention housecarl_records and " +
                     "housecarl_check use. One row per resolved path, carrying the winner, the provider kind (loose " +
                     "or BSA, and for a BSA which archive), the whole provider chain, and — on a formids= row — the " +
                     "paired path with its own winner and whether the two differ. The artifact is never a window: " +
                     "offset= is refused with it, limit= does not narrow it, and row_count equals total. Re-enter it " +
                     "via asset_paths=[\"@<path>\"]; its identity column is 'path', so it is NOT a formids= list for " +
                     "housecarl_records.")]
            string? to_file = null,
        [Description("TRANSPORT: return the census and no path rows — what the file layer looks like in aggregate: " +
                     "which MO2 layers win how many paths, how the winners split between loose and BSA, and how " +
                     "many are absent. The question a whole-order sweep usually has of its rows. It covers the " +
                     "WHOLE selection whatever limit= says, so limit= caps the census table's rows instead and " +
                     "offset= is refused. Refused beside to_file=, which writes the rows the census replaces.")]
            bool counts_only = false,
        [Description("TRANSPORT: 'text' (default) | 'json' (the same data, machine-readable, accounting in-band).")]
            string? format = null,
        [Description("TRANSPORT: character CEILING on the whole response, not just on the per-path list — the path whose block would cross it is not written at all. What the ceiling holds back is NOT lost: the RESOLVED result is written whole to an artifact in the server's results directory and the response names that file — under limit= the resolved result IS the window, and the marker says so. The one exception is counts_only=, which spills nothing: a census whose layer table the ceiling cut says how many rows it held back, and those rows are in no file — raise max_chars, or page the table with limit=. Spilling also FINGERPRINTS the order, so on a path-only sweep, which otherwise reads no record at all, the first spill builds the record index (seconds on a big order). The alarms and the accounting line are charged before the paths render, so both are inside the ceiling. A cap too small for what the response carries whatever the budget says so and names the cap that clears it in one step. 0 = the server default (~80k).")]
            int max_chars = 0) => Guard.Tool(ToolNames.AssetStatus, () =>
    {
        // format first, so the unconfigured-MO2 prompt answers a json caller as a document.
        bool json = Wire.WantsJson(format, out var ferr);
        if (ferr is not null) return ferr;
        if (svc.ConfigPromptOrNull() is { } prompt)
            return json ? JsonWire.RenderError(prompt, null) : prompt;
        // The read/write surface's one refusal shape, through its one owner: Wire.Refuse owns the prefix the json document strips.
        string Refuse(string message) => Wire.Refuse(json, Wire.RefusalPrefix + message);

        if ((asset_paths is null || asset_paths.Length == 0) && (under is null || under.Length == 0)
            && (formids is null || formids.Length == 0))
            return Refuse("asset_paths, under and formids are all empty. Pass Data-relative asset path(s) in " +
                          "asset_paths (e.g. 'textures/armor/iron/cuirass_1.dds'), a Data-relative directory or glob " +
                          "in under (e.g. 'meshes/actors/character/facegendata/facegeom/Skyrim.esm'), or NPC " +
                          "FormID(s) in formids (e.g. '01A51A:Dawnguard.esm') for their FaceGen pairs.");
        // The window's own refusal, from the window: this tool and housecarl_skse answer the same input class.
        if (new RowWindow(offset, limit).Error is { } bad) return Wire.Refuse(json, bad);
        int cap = max_chars > 0 ? max_chars : 80_000;

        var toFile = to_file?.Trim();
        bool wantFile = toFile is { Length: > 0 };
        if (wantFile)
        {
            // The same validator the records surface runs: absolute, .jsonl, and outside the pruned results directory.
            if (Artifacts.ValidateToFile(toFile!, svc.ResultsDir) is { } verr) return Wire.Refuse(json, verr);
            // The same pair the records lanes refuse, in the same words: one returns the census, the other writes the rows.
            if (counts_only) return Wire.Refuse(json, Artifacts.CountsOnlyWithToFile);
            if (offset > 0)
                return Wire.Refuse(json, "error: to_file= captures the COMPLETE result (the artifact is never a " +
                                         "window), so offset= has nothing to page — drop offset=.");
        }
        // A census covers the whole selection, so there is no selection window for offset= to move.
        if (counts_only && offset > 0)
            return Wire.Refuse(json, "error: " + ReadSentences.NoOffsetOnCountTable("counts_only="));

        // The @file convention on both list inputs, each taking the identity column its own tokens are made of.
        var (pathTokens, pathDemand, pathEcho, perr) =
            Artifacts.ExpandListInput(asset_paths ?? Array.Empty<string>(), "asset_paths", identity: "path");
        if (perr is not null) return Wire.Refuse(json, perr);
        var (idTokens, idDemand, idEcho, ferr2) =
            Artifacts.ExpandListInput(formids ?? Array.Empty<string>(), "formids");
        if (ferr2 is not null) return Wire.Refuse(json, ferr2);

        // The FormID door parses the plugin-qualified form without touching the index, so an ordinary sweep reads no record.
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
        if (wantFile || idDemand is not null)
        {
            // What READING the order can throw, and nothing else: a held profile file, a denied read, an order with no
            // active plugins. Anything else is a bug, and the sentence below would be false of one.
            try { order = svc.CaptureView().Stamp; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            { noEpochBecause = Guard.Flatten(ex.Message); }
        }
        if (idDemand is { } demand)
        {
            if (noEpochBecause is not null)
                return Wire.Refuse(json, $"error: formids= artifact '{demand.Path}' was captured at epoch={demand.Epoch}, " +
                                         $"and this call could not build a load order to check it against — {noEpochBecause} " +
                                         "Fix the order and retry; there is deliberately no unchecked re-entry. A list " +
                                         "of PATHS re-enters through asset_paths= without a build, because a path " +
                                         "answers off the VFS alone.");
            if (demand.Epoch != order!.Epoch)
                return Wire.Refuse(json, "error: " + LoadOrderService.ArtifactEpochMismatch(demand, order.Epoch));
        }

        // counts_only= resolves the WHOLE selection, as to_file= does: a census of a window would answer about a window while the rows that need paging had no knob.
        var data = svc.AssetStatus(pathTokens ?? Array.Empty<string>(), under, counts_only ? 0 : limit, offset, seeds,
                                   wholeSelection: wantFile || counts_only);
        // The declared-cost refusal: the selection was counted and is past the bound, so nothing was resolved.
        if (data.BoundRefusal is { } tooBig) return Wire.Refuse(json, tooBig);

        if (counts_only)
            return json ? JsonWire.RenderAssetCensus(data, cap, limit) : AssetCensus.Render(data, cap, limit);

        KeyValuePair<string, string>[] Echo()
        {
            var e = new List<KeyValuePair<string, string>>
            {
                new("asset_paths", pathEcho ?? $"{pathTokens?.Length ?? 0} inline path(s)"),
                new("under", under is { Length: > 0 } ? string.Join(",", under) : "<none>"),
                new("formids", idEcho ?? $"{seeds.Count} inline formid(s)"),
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
            if (!truncated) return rendered;
            // SPEC §2.1.1: an over-ceiling read result is written whole to the results directory and the response names the
            // file. The stamp is taken only here, so an ordinary sweep still builds no record index.
            if (order is null && noEpochBecause is null)
                try { order = svc.CaptureView().Stamp; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                { noEpochBecause = Guard.Flatten(ex.Message); }
            using var reservation = ResultsStore.Reserve(svc.ResultsDir, ToolNames.AssetStatus, order?.Epoch ?? "none");
            var (auto, autoErr) = AssetArtifact.Write(data, reservation, "ceiling", order, Echo(), noEpochBecause);
            return Inline(autoErr is null ? SpillState.Spilled(auto!, manifestOnly: false) : SpillState.WriteFailed(autoErr), out _);
        }

        var (spill, artErr) = AssetArtifact.Write(data, ArtifactTarget.Named(toFile!), "to_file", order, Echo(), noEpochBecause);
        if (artErr is not null) return Wire.Refuse(json, "error: " + artErr);
        var manifestOnly = AssetArtifact.RenderManifestOnly(data, spill!, json, cap);
        // The text lane's ceiling arm; the json document caps itself as it writes.
        return json ? manifestOnly : RenderCap.Settle(manifestOnly, cap);
    });
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
            .Append(" selected)").ToString();

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
            },
            (sb, r, _) => AppendPath(sb, r, d.BsaFailures.Count > 0, d.Warnings.Count > 0, d.RootFailures.Count > 0),
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

    /// <summary>What this response actually did, in the shared TRANSPORT vocabulary: the selection total, the window rendered, and the four distinct omissions.</summary>
    internal static TransportCounts Tally(AssetStatusData d, int rendered) =>
        TransportAccounting.Tally(d.Selected, d.Results.Count, rendered, new RowWindow(d.Offset, d.Limit),
                                  d.SelectorNotes?.Count ?? 0);

    /// <summary>The widest counts this response could state — what the json lane's tail reserve measures.</summary>
    internal static TransportCounts Widest(AssetStatusData d) =>
        TransportAccounting.Widest(d.Selected, d.Results.Count, new RowWindow(d.Offset, d.Limit),
                                   d.SelectorNotes?.Count ?? 0);

    /// <summary>The chars held back from max_chars so the accounting block is always affordable.</summary>
    internal static int AccountingReserve(AssetStatusData d) =>
        TransportAccounting.Reserve(d.Selected, d.Results.Count, new RowWindow(d.Offset, d.Limit),
                                    d.SelectorNotes?.Count ?? 0, RowNoun);

    static void AppendPath(StringBuilder sb, AssetPathResult r, bool readIncomplete, bool discoveryIncomplete,
                           bool rootIncomplete)
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

        if (r.Error is not null)                                  // a rejected path: drive-rooted, or escaping with '..'
        {
            sb.Append("  error: ").Append(r.Error).Append('\n');
            return;
        }

        var hit = r.Hit!;
        if (!hit.Exists)
        {
            sb.Append("  ABSENT — no active mod or BSA provides this path\n");
            // Each suggestion was verified by re-resolving the prefixed form. Backticks, not single quotes — an asset path can carry the author's own apostrophes.
            if (r.PrefixSuggestions is { Count: > 0 } sug)
                sb.Append("  did you mean ").Append(string.Join(" or ", sug.Select(s => "`" + s + "`")))
                  .Append("?  (a path read off a record is relative to its root folder, not to Data)\n");
            // Both incomplete-scan conditions hedge an ABSENT at the point of use, not only in the top-of-output note: the asset could exist where we did not look.
            if (readIncomplete)
                sb.Append("  [!] but an archive failed to read this build (see the read-failure note above), so " +
                          "\"absent\" may be incomplete — the asset could live in the unreadable archive.\n");
            if (discoveryIncomplete)
                sb.Append("  [!] some archives were not scanned this build (see the discovery note above), so " +
                          "\"absent\" may be incomplete — base-game assets live in BSAs that weren't enumerated.\n");
            if (rootIncomplete)
                sb.Append("  [!] but a loose root failed to read this build (see the root note above), so " +
                          "\"absent\" may be incomplete — the asset could live in the root that was not read.\n");
            AppendPair(sb, r);
            return;
        }

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
        AppendPair(sb, r);
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
                                    : "ABSENT — no active mod or BSA provides this path").Append('\n');
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
    static string Provider(HousecarlCore.AssetProvider p)
        => HousecarlCore.AssetSourceSelection.Describe(p.Source, Kind(p.Kind));
}

/// <summary><c>asset_status</c>'s <c>counts_only=</c> census (SPEC §2.1): what the file layer looks like in
/// AGGREGATE over the paths this call resolved. It covers the WHOLE selection and <c>limit=</c> is spent on the
/// table's rows instead, which is the thing here that actually needs paging. The table is the surface's one
/// <see cref="HistogramAxis"/>, so only the counters above it are this lane's own.</summary>
static class AssetCensus
{
    /// <summary>The census over one resolution. <see cref="ByLayer"/> is the winning MO2 LAYERS, count descending
    /// then name ascending — the same value the artifact's <c>winner_mod</c> column carries.</summary>
    internal readonly record struct Counts(int Selected, int Present, int Absent, int Errors,
                                           int Loose, int Bsa, IReadOnlyList<SweepCount> ByLayer);

    /// <summary>What the axis is titled, and the note that keeps its two non-mod values honest. The note rides the axis, so it is written whatever the budget says.</summary>
    const string AxisTitle = "winning layers";
    const string AxisNote = "a winning LAYER is a mod folder, the game's own Data folder, or overwrite — the last "
                          + "two are layers rather than mods, so they cannot be sorted or disabled.";

    internal static Counts Tally(AssetStatusData d)
    {
        int present = 0, absent = 0, errors = 0, loose = 0, bsa = 0;
        var byLayer = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in d.Results)
        {
            if (r.Error is not null) { errors++; continue; }
            if (r.Hit is not { Exists: true, Winner: { } win }) { absent++; continue; }
            present++;
            if (win.Kind == HousecarlCore.AssetKind.Bsa) bsa++; else loose++;
            var owner = AssetPathResult.Owner(win);
            byLayer[owner] = byLayer.GetValueOrDefault(owner) + 1;
        }
        var rows = byLayer.OrderByDescending(m => m.Value).ThenBy(m => m.Key, StringComparer.Ordinal)
                          .Select(m => new SweepCount(m.Key, m.Value)).ToList();
        // The census always covers the whole selection, so its counters are the selection's — never a window's.
        return new Counts(d.Selected, present, absent, errors, loose, bsa, rows);
    }

    /// <summary>The axis this census renders, in one place so the two transports cannot title or note it differently.</summary>
    internal static HistogramAxis Axis(Counts c) =>
        new(SweepSubject.AssetWinnerRows, c.ByLayer, AxisTitle, Note: AxisNote);

    /// <summary>The rows one call may render, from its <c>limit=</c>: 0 is no limit.</summary>
    internal static int RowLimit(int limit) => limit > 0 ? limit : int.MaxValue;

    /// <summary>The two counter lines — the census's whole answer, written whatever the budget says.</summary>
    static string Counters(Counts c) =>
        $"\ncensus: counted={c.Selected} present={c.Present} absent={c.Absent} errors={c.Errors}\n"
        + $"winners: loose={c.Loose} BSA={c.Bsa}\n";

    /// <summary>The text census: the alarms an ABSENT count depends on, the counters, then the layer axis. The counters are exact whatever the axis's cut.</summary>
    public static string Render(AssetStatusData d, int cap, int limit)
    {
        var c = Tally(d);
        var sb = new StringBuilder(AssetWire.Header(d)).Append('\n');
        // What this response writes whatever the budget says, held back BEFORE the alarms: uncharged, they take
        // the room the census's own answer needs and the response lands over the cap on a cut that would have fitted.
        var room = RenderCap.For(cap, Counters(c).Length + Axis(c).TextFixed);
        // The alarms first, for the reason the path render puts them first: an ABSENT count is authoritative only where no archive read failed.
        BatchRender.AppendReadFailures(sb, d.BsaFailures, "an asset", room);
        BatchRender.AppendRootFailures(sb, d.RootFailures, "an asset", room);
        BatchRender.AppendDiscoveryWarnings(sb, d.Warnings, room);
        AssetWire.AppendSelectorNotes(sb, d.SelectorNotes, room);

        sb.Append(Counters(c));

        // The one bounded emission path: the budget is the whole cap, because Outstanding reads the live builder.
        var body = new BoundedBody(acct: null, budget: cap, () => sb.Length);
        CheckTextRender.AppendHistogramAxes(sb, body, RowLimit(limit), Axis(c));
        return RenderCap.Settle(sb.ToString().TrimEnd('\n'), cap);
    }
}
