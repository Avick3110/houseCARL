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
         "accounting in-band. TRANSPORT — format= | limit= | offset= | max_chars= | to_file=. BOUND: 1,200,000 paths " +
         "RESOLVED a call — the window where limit= takes one, except under to_file=, which always resolves the " +
         "whole selection — and past it the call refuses up front with the count and the estimate. Read-only: " +
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
                     "which covers the WHOLE selection — the artifact is never a window.")]
            int limit = 0,
        [Description("Optional. Where in the selection the rendered window starts, for paging a large under= sweep. 0 = the beginning. Refused with to_file=.")]
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
        [Description("TRANSPORT: 'text' (default) | 'json' (the same data, machine-readable, accounting in-band).")]
            string? format = null,
        [Description("TRANSPORT: character CEILING on the whole response, not just on the per-path list — the path whose block would cross it is not written at all. What the ceiling holds back is NOT lost: the RESOLVED result is written whole to an artifact in the server's results directory and the response names that file — under limit= the resolved result IS the window, and the marker says so. Spilling also FINGERPRINTS the order, so on a path-only sweep, which otherwise reads no record at all, the first spill builds the record index (seconds on a big order). The alarms and the accounting line are charged before the paths render, so both are inside the ceiling. A cap too small for what the response carries whatever the budget says so and names the cap that clears it in one step. 0 = the server default (~80k).")]
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
            if (offset > 0)
                return Wire.Refuse(json, "error: to_file= captures the COMPLETE result (the artifact is never a " +
                                         "window), so offset= has nothing to page — drop offset=.");
        }

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

        var data = svc.AssetStatus(pathTokens ?? Array.Empty<string>(), under, limit, offset, seeds, wholeSelection: wantFile);
        // The declared-cost refusal: the selection was counted and is past the bound, so nothing was resolved.
        if (data.BoundRefusal is { } tooBig) return Wire.Refuse(json, tooBig);

        KeyValuePair<string, string>[] Echo() => new[]
        {
            new KeyValuePair<string, string>("asset_paths", pathEcho ?? $"{pathTokens?.Length ?? 0} inline path(s)"),
            new KeyValuePair<string, string>("under", under is { Length: > 0 } ? string.Join(",", under) : "<none>"),
            new KeyValuePair<string, string>("formids", idEcho ?? $"{seeds.Count} inline formid(s)"),
            new KeyValuePair<string, string>("read_incomplete", data.ReadIncomplete ? "true" : "false"),
            new KeyValuePair<string, string>("discovery_warnings", data.Warnings.Count.ToString()),
        };

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
    public static string Render(AssetStatusData d, int cap) => Render(d, cap, null, out _);

    /// <summary><paramref name="spill"/> is this call's artifact disposition, written after the accounting and
    /// charged before the first path so the block lands inside max_chars. <paramref name="truncated"/> is whether
    /// max_chars cut paths out of the window — what the caller auto-spills on.</summary>
    public static string Render(AssetStatusData d, int cap, SpillState? spill, out bool truncated)
    {
        var header = new StringBuilder("asset status — profile '")
            .Append(d.ProfileName.Length > 0 ? d.ProfileName : "(unconfigured)")
            .Append("'  (").Append(d.Selected).Append(" path").Append(d.Selected == 1 ? "" : "s")
            .Append(" selected)").ToString();

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
    static void AppendSelectorNotes(StringBuilder sb, IReadOnlyList<string>? notes, RenderCap cap)
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
