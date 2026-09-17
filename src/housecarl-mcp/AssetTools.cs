using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace HousecarlMcp;

/// <summary>Read-only asset resolution: which mod or BSA provides a Data-relative path, and which copy wins in game —
/// loose files beat BSA-packed, and among BSAs the latest-loaded plugin's wins. Active BSAs are discovered from the
/// same static MO2 profile read the load order uses (per-plugin "X.bsa" / "X - Textures.bsa" plus the Skyrim.ini base
/// archives). No archive handles are held at rest; freshness is a last-write-plus-size check.</summary>
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
        // The read/write surface's one refusal shape, through its one owner: Wire.Refuse strips the prefix for the
        // json document, so this shorthand only saves the two call sites below from repeating the transport flag.
        string Refuse(string message) => Wire.Refuse(json, Wire.RefusalPrefix + message);

        if ((asset_paths is null || asset_paths.Length == 0) && (under is null || under.Length == 0)
            && (formids is null || formids.Length == 0))
            return Refuse("asset_paths, under and formids are all empty. Pass Data-relative asset path(s) in " +
                          "asset_paths (e.g. 'textures/armor/iron/cuirass_1.dds'), a Data-relative directory or glob " +
                          "in under (e.g. 'meshes/actors/character/facegendata/facegeom/Skyrim.esm'), or NPC " +
                          "FormID(s) in formids (e.g. '01A51A:Dawnguard.esm') for their FaceGen pairs.");
        // The window's own refusal, from the window: this tool and housecarl_skse answer the same input class, so the
        // sentence is spelled once rather than reworded in two places.
        if (new RowWindow(offset, limit).Error is { } bad) return Wire.Refuse(json, bad);
        int cap = max_chars > 0 ? max_chars : 80_000;

        var toFile = to_file?.Trim();
        bool wantFile = toFile is { Length: > 0 };
        if (wantFile)
        {
            // The same validator the records surface runs: absolute, .jsonl, and outside the pruned results
            // directory. Unvalidated, a relative path writes under the SERVER's working directory and the response
            // names an artifact the caller cannot find.
            if (Artifacts.ValidateToFile(toFile!) is { } verr) return Wire.Refuse(json, verr);
            // The same pair the records lanes refuse, in the same words: one returns the census with no rows, the
            // other writes the rows.
            if (counts_only) return Wire.Refuse(json, Artifacts.CountsOnlyWithToFile);
            if (offset > 0)
                return Wire.Refuse(json, "error: to_file= captures the COMPLETE result (the artifact is never a " +
                                         "window), so offset= has nothing to page — drop offset=.");
        }
        // Same shape, same reason as the records lanes' aggregate: a census covers the whole selection, so there is
        // no selection window for offset= to move, and limit= is what pages the table it renders instead.
        if (counts_only && offset > 0)
            return Wire.Refuse(json, "error: counts_only= counts the COMPLETE selection, so offset= has nothing to " +
                                     "page — drop offset=, and use limit= to page the census table's rows.");

        // The @file convention on both list inputs: an artifact stands in place of the whole list, and each takes
        // the identity column its own tokens are made of.
        var (pathTokens, pathDemand, pathEcho, perr) =
            Artifacts.ExpandListInput(asset_paths ?? Array.Empty<string>(), "asset_paths", identity: "path");
        if (perr is not null) return Wire.Refuse(json, perr);
        var (idTokens, idDemand, idEcho, ferr2) =
            Artifacts.ExpandListInput(formids ?? Array.Empty<string>(), "formids");
        if (ferr2 is not null) return Wire.Refuse(json, ferr2);

        // The FormID door parses the plugin-qualified form without touching the index, and reaches for one build only
        // if a RUNTIME FormID actually arrives — so an ordinary sweep costs no record read at all.
        var door = FormIdDoor.For(svc);
        var seeds = new List<FaceGenSeed>(idTokens?.Length ?? 0);
        foreach (var raw in idTokens ?? Array.Empty<string>())
        {
            // Every token that is not a FormID answers as ONE error row, whatever the door threw. Narrower than
            // this, a RUNTIME FormID on an order that cannot build reaches CaptureView, whose InvalidOperationException
            // would escape to Guard as "an internal houseCARL failure (the arguments bound fine)" — for input this
            // tool can plainly name. The sibling lanes catch Exception here for the same reason.
            try { seeds.Add(new FaceGenSeed(raw, door.Parse(raw), null)); }
            catch (Exception ex)
            {
                seeds.Add(new FaceGenSeed(raw, null, $"not a FormID: {Guard.Flatten(ex.Message)} Expected 'XXXXXX:Plugin.esp'."));
            }
        }

        // An artifact of FORMIDS was captured at one record build, and consuming it server-side is epoch-checked
        // against the build answering now — a mismatch is a loud refusal naming both, with no stale-override switch.
        // An artifact of PATHS is not checked and deliberately so: a path is a string, every answer about it is read
        // live off the VFS, and nothing in it can go stale against a record build. Gating it would refuse the loop
        // this feature exists for — sweep, fix a mod, re-ask the same path list — over a build the answer never used.
        // Captured only where something needs it, so a plain path sweep still builds no record index. And it is
        // allowed to FAIL: this tool answers "which mod wins this file" off the VFS alone, so an order whose plugins
        // do not resolve must not stop it — the artifact then carries no fingerprint and says why, rather than the
        // call dying on a build its answer never needed.
        OrderStamp? order = null;
        string? noEpochBecause = null;
        if (wantFile || idDemand is not null)
        {
            // What READING the order can throw, and nothing else. The profile files are read unguarded, so MO2
            // rewriting them on a re-sort hands this an IOException while it holds the handle, and an order with no
            // active plugins is the InvalidOperationException — both are honest degrades for a sweep that never
            // needed the record index. Anything else is a bug, and the sentence below names a cause ("the order
            // could not be read … re-run once it reads") that would be false of one.
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

        // counts_only= resolves the WHOLE selection, the way to_file= does: a census of a window would answer about
        // a window while the rows that actually need paging — the table's — had no knob at all. limit= is spent
        // there instead.
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
            // WHICH rows the file holds, in the records lane's spelling: an auto-spilled window's manifest states
            // row_count and total, and without this nothing says which of the total those rows are. A re-read months
            // later has no conversation to recover it from. Never on the to_file= arm, which is never a window.
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
            // SPEC §2.1.1: an over-ceiling read result is written whole to the server-managed results directory and
            // the response names the file — truncation is not a failure mode on a read lane. The stamp is taken only
            // here, so an ordinary sweep still builds no record index; the same two degrades the to_file= lane
            // allows are honest here too, since every row is read off the VFS.
            if (order is null && noEpochBecause is null)
                try { order = svc.CaptureView().Stamp; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                { noEpochBecause = Guard.Flatten(ex.Message); }
            using var reservation = ResultsStore.Reserve(ToolNames.AssetStatus, order?.Epoch ?? "none");
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

/// <summary>Renders <see cref="AssetStatusData"/>: the build-level alarms first (archives that failed to read,
/// discovery warnings), then one block per queried path — winner and every provider in precedence order. Contention is
/// worded neutrally, since more than one source is the common healthy case. The per-path list is bounded by max_chars
/// with an explicit cut notice.</summary>
static class AssetWire
{
    /// <summary>The one header line both renders open with: the profile, and how many paths the SELECTION named.</summary>
    internal static string Header(AssetStatusData d) =>
        new StringBuilder("asset status — profile '")
            .Append(d.ProfileName.Length > 0 ? d.ProfileName : "(unconfigured)")
            .Append("'  (").Append(d.Selected).Append(" path").Append(d.Selected == 1 ? "" : "s")
            .Append(" selected)").ToString();

    public static string Render(AssetStatusData d, int cap) => Render(d, cap, null, out _);

    /// <summary><paramref name="spill"/> is this call's artifact disposition, written after the accounting and
    /// charged before the first path so the block lands inside max_chars. <paramref name="truncated"/> is whether
    /// max_chars cut paths out of the window — what the caller auto-spills on.</summary>
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
                BatchRender.AppendDiscoveryWarnings(sb, d.Warnings, room);
                AppendSelectorNotes(sb, d.SelectorNotes, room);
            },
            (sb, r, _) => AppendPath(sb, r, d.ReadIncomplete, d.Warnings.Count > 0),
            out int rendered,
            // The accounting block is priced INSIDE max_chars, the way the check sweep's footer is: it is written
            // after the body, so room for its longest spelling is held back before the paths render rather than
            // appended past the cap. max_chars then means the same on this tool as on every other.
            reserve: AccountingReserve(d) + spillText.Length);

        var counts = Tally(d, rendered);
        truncated = counts.Truncated > 0;
        return RenderCap.Settle(body + TransportAccounting.Compose(counts, RowNoun, everySentence: false) + spillText, cap);
    }

    /// <summary>What this family's accounting counts.</summary>
    const string RowNoun = "path(s)";

    /// <summary>What each under= selector had to say for itself — a selector that matched nothing, or was rejected.
    /// Above the per-path list, with the other alarms, so a truncated sweep cannot cut it away. Capped like its two
    /// sibling alarm blocks: one note per selector is bounded by the call's own input, but that input can be thousands
    /// of selectors, which would write megabytes before the per-path loop ever checks the budget.</summary>
    internal static void AppendSelectorNotes(StringBuilder sb, IReadOnlyList<string>? notes, RenderCap cap)
    {
        if (notes is not { Count: > 0 }) return;
        // The heading carries the count and is written whatever the budget — a selector that matched nothing must not
        // vanish into a render that then reads as complete. Only the per-selector lines below it are cut.
        sb.Append("\n[!] under (").Append(notes.Count).Append("):\n");
        BatchRender.AppendLines(sb, notes, "selector(s)", cap);
    }

    /// <summary>What this response actually did, in the shared TRANSPORT vocabulary
    /// (<see cref="TransportAccounting"/>): the selection total, the window this response rendered, and the four
    /// distinct omissions.</summary>
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

    static void AppendPath(StringBuilder sb, AssetPathResult r, bool readIncomplete, bool discoveryIncomplete)
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
            // A path taken straight off a record is missing its root folder: a model path is stored relative to
            // meshes\, a texture path to textures\. Each suggestion was verified by re-resolving the prefixed form.
            // Backticks, not single quotes — an asset path can carry the mod author's own apostrophes.
            if (r.PrefixSuggestions is { Count: > 0 } sug)
                sb.Append("  did you mean ").Append(string.Join(" or ", sug.Select(s => "`" + s + "`")))
                  .Append("?  (a path read off a record is relative to its root folder, not to Data)\n");
            // Both incomplete-scan conditions hedge an ABSENT at the point of use, not only in the top-of-output note:
            // an archive that failed to read, and base archives never discovered (no Skyrim.ini found, so the vanilla
            // "Skyrim - Textures*.bsa" went unscanned). Either means the asset could exist where we did not look.
            if (readIncomplete)
                sb.Append("  [!] but an archive failed to read this build (see the read-failure note above), so " +
                          "\"absent\" may be incomplete — the asset could live in the unreadable archive.\n");
            if (discoveryIncomplete)
                sb.Append("  [!] some archives were not scanned this build (see the discovery note above), so " +
                          "\"absent\" may be incomplete — base-game assets live in BSAs that weren't enumerated.\n");
            AppendPair(sb, r);
            return;
        }

        // The provider token is spelled by the one formatter the asset surface uses, so the name printed here is the
        // name place_asset's source_provider= accepts — the third surface of #340. A mod folder can legitimately hold
        // a parenthetical ("SkyUI (SE)"), so the delimiter is what tells a caller where the name ends.
        // The owning mod rides the WINS: line on a formids= row, so the text lane shows the same evidence the pair
        // verdict is taken on — two archive names of one mod read as a split without it. Only there, so a plain path
        // block is byte for byte the block it always was.
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

    /// <summary>The OTHER half of the FaceGen pair, beside this one: its path and its own winner. An NPC's head
    /// renders from BOTH files and a dark face is almost always the two disagreeing, so a row that named only its own
    /// winner would leave the diagnosis a second call away. Written on both a present and an ABSENT row — the absent
    /// half IS the finding on half the classes. Nothing at all on a row no FormID derived.</summary>
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

    /// <summary>The mod folder behind a BSA provider, where the provider token is the archive's own filename and the
    /// mod is what a caller would sort or disable. Nothing for a loose provider, whose token IS the mod.</summary>
    static string Mod(HousecarlCore.AssetProvider p)
        => p.OwningMod is { Length: > 0 } m && !string.Equals(m, p.Source, StringComparison.OrdinalIgnoreCase)
            ? $"  [mod: {m}]" : "";

    static string Kind(HousecarlCore.AssetKind k) => k == HousecarlCore.AssetKind.Bsa ? "BSA" : "loose";

    /// <summary>One provider, spelled by the shared formatter (#340): the name inside double quotes — a character a
    /// Windows folder or file name cannot contain — with the kind outside them, so the printed token is the token a
    /// source selector accepts.</summary>
    static string Provider(HousecarlCore.AssetProvider p)
        => HousecarlCore.AssetSourceSelection.Describe(p.Source, Kind(p.Kind));
}

/// <summary><c>asset_status</c>'s <c>counts_only=</c> census (SPEC §2.1): what the file layer looks like in
/// AGGREGATE over the paths this call resolved — which mods win how many, how the winners split between loose and
/// BSA, and how many are absent. A whole-order FaceGen sweep is over a hundred thousand rows, and the question a
/// caller usually has of it is this histogram rather than the rows.
///
/// <para>The census covers the WHOLE selection — it is the cost <c>counts_only=</c> exists to pay, the one
/// <c>to_file=</c> already pays — and <c>limit=</c> is spent on the table's rows instead, which is the thing here
/// that actually needs paging (SPEC §2.1, closure-proof §G4).</para>
///
/// <para>The table itself is the surface's ONE histogram axis (<see cref="HistogramAxis"/>), the same grammar
/// <c>check</c>'s <c>counts_only=</c> axes take: the head that rides its first row, the cut line naming the knob
/// that stopped it, the reserve taken before the rows render, and the distinct empty-axis sentence. Only the
/// counters above it are this lane's own.</para></summary>
static class AssetCensus
{
    /// <summary>The census over one resolution. <see cref="ByLayer"/> is the winning MO2 LAYERS, count descending
    /// then name ascending — the same value the artifact's <c>winner_mod</c> column carries and the pair verdict is
    /// taken on, which for a loose winner is a mod folder, the game's own Data folder, or overwrite.</summary>
    internal readonly record struct Counts(int Selected, int Present, int Absent, int Errors,
                                           int Loose, int Bsa, IReadOnlyList<SweepCount> ByLayer);

    /// <summary>What the axis is titled, and the note that keeps its two non-mod values honest. The note rides the
    /// axis, so it is written whatever the budget says — the same treatment the sibling by-mod axis gives its
    /// own.</summary>
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

    /// <summary>The axis this census renders, in one place so the text lane and the json lane cannot title or note
    /// it differently.</summary>
    internal static HistogramAxis Axis(Counts c) =>
        new(SweepSubject.AssetWinnerRows, c.ByLayer, AxisTitle, Note: AxisNote);

    /// <summary>The rows one call may render, from its <c>limit=</c>: 0 is no limit, the shape every transport axis
    /// defaults to on this tool.</summary>
    internal static int RowLimit(int limit) => limit > 0 ? limit : int.MaxValue;

    /// <summary>The two counter lines — the census's whole answer, written whatever the budget says. Composed
    /// rather than appended so the reserve and the render read one spelling.</summary>
    static string Counters(Counts c) =>
        $"\ncensus: counted={c.Selected} present={c.Present} absent={c.Absent} errors={c.Errors}\n"
        + $"winners: loose={c.Loose} BSA={c.Bsa}\n";

    /// <summary>The text census: the alarms an ABSENT count depends on, the counters, then the layer axis. The
    /// counters are exact whatever the axis's cut, so a cut table never makes a total wrong.</summary>
    public static string Render(AssetStatusData d, int cap, int limit)
    {
        var c = Tally(d);
        var sb = new StringBuilder(AssetWire.Header(d)).Append('\n');
        // What this response writes whatever the budget says, held back BEFORE the alarms — the counters, and the
        // axis's note, head and cut line. The alarms are the only cuttable thing above the axis, so uncharged they
        // take the room the census's own answer needs and the response lands over the cap on a cut that would have
        // fitted. The path render holds its accounting back the same way.
        var room = RenderCap.For(cap, Counters(c).Length + Axis(c).TextFixed);
        // The alarms first, for the reason the path render puts them first: an ABSENT count is authoritative only
        // where an archive read failed nowhere, and a long table must not be able to cut that away.
        BatchRender.AppendReadFailures(sb, d.BsaFailures, "an asset", room);
        BatchRender.AppendDiscoveryWarnings(sb, d.Warnings, room);
        AssetWire.AppendSelectorNotes(sb, d.SelectorNotes, room);

        sb.Append(Counters(c));

        // The one bounded emission path, as the sweep lanes use it: the budget is the whole cap, because
        // Outstanding reads the live builder and so already charges everything written above.
        var body = new BoundedBody(acct: null, budget: cap, () => sb.Length);
        Wire.AppendHistogramAxes(sb, body, RowLimit(limit), Axis(c));
        return RenderCap.Settle(sb.ToString().TrimEnd('\n'), cap);
    }
}
