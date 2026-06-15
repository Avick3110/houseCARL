using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;

namespace HousecarlMcp;

/// <summary>
/// houseCARL asset tool (facegen-diagnostics Phase 2). Read-only. Resolves Data-relative asset paths through MO2's
/// virtual file system — which mod or BSA provides each asset, and which copy actually WINS in game (loose files beat
/// BSA-packed; among BSAs the latest-loaded plugin's wins) — the file-side answer the dark-face ("grey/black face")
/// skill rides. The asset resolver discovers the active BSAs by construction from the same static MO2 profile read the
/// load order uses (per-plugin "X.bsa"/"X - Textures.bsa" + Skyrim.ini base archives), holds ZERO archive handles at
/// rest (arch #3), and refreshes by mtime — no daemon, no live tracking.
/// </summary>
[McpServerToolType]
public static class AssetTools
{
    [McpServerTool(Name = "housecarl_asset_status", ReadOnly = true, Title = "Asset status — which mod/BSA wins for a Data-relative path"),
     Description(
         "Resolve one or more Data-relative asset paths through Mod Organizer 2's virtual file system and report, for " +
         "each, WHICH copy the game actually uses: the winning source, every source that provides it (loose mods, the " +
         "overwrite folder, the game Data folder, and active BSAs), whether more than one source contends, and whether " +
         "the asset is absent. Precedence is the real engine/MO2 rule — loose files beat BSA-packed, among loose the " +
         "higher-priority mod (then overwrite) wins, among BSAs the later-loaded plugin's archive wins. This is the " +
         "file-side half of dark-face / grey-face ('black face', 'ashen face') NPC diagnosis: pass an NPC's FaceGen " +
         "mesh/texture path (meshes\\actors\\character\\facegendata\\... .nif or textures\\actors\\character\\facegendata\\... .dds) " +
         "to see which mod's facegen wins versus which plugin wins the NPC record — a mismatch is the dark-face bug. " +
         "Also answers general 'which mod provides this file / who is this asset coming from / is this texture loose or " +
         "in a BSA / is this asset even present' questions for any mesh, texture, script, sound, or other path. Pass " +
         "asset_paths = one or more paths RELATIVE to the Data folder (forward or back slashes both fine; a drive-rooted " +
         "or '..'-escaping path is rejected per-path). An archive that cannot be read, or a Skyrim.ini base-archive list " +
         "that cannot be found, is reported LOUD — so an 'absent' answer is never silently trusted when the scan was " +
         "incomplete. Read-only: resolves nothing to disk, writes nothing, changes no load order.")]
    public static string AssetStatus(
        LoadOrderService svc,
        [Description("The Data-relative asset path(s) to resolve, e.g. " +
                     "'meshes/actors/character/facegendata/facegeom/Skyrim.esm/000ABCDE.nif'. One or many; resolved in " +
                     "order, results returned in the same order. Paths are relative to the game's Data folder.")]
            string[] asset_paths,
        [Description("Optional. Max characters before the per-path list is cut with an explicit notice. 0 = the server default (~80k).")]
            int max_chars = 0) => Guard.Tool("housecarl_asset_status", () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        if (asset_paths is null || asset_paths.Length == 0)
            return "error: asset_paths is empty. Pass one or more Data-relative asset paths (e.g. 'textures/actors/character/facegendata/facetint/Skyrim.esm/000ABCDE.dds').";
        var data = svc.AssetStatus(asset_paths);
        return AssetWire.Render(data, max_chars > 0 ? max_chars : 80_000);
    });
}

/// <summary>Renders <see cref="AssetStatusData"/> as compact, scannable text: the build-level Q3 alarms first (archives
/// that failed to read; discovery warnings), then one block per queried path — winner + every provider in precedence
/// order, with contention noted NEUTRALLY (more than one source is the common, healthy case at scale — it's a "verify"
/// signal, not a "problem"). The per-path list is bounded by max_chars with an explicit cut notice (Q3 — never silent
/// truncation).</summary>
static class AssetWire
{
    public static string Render(AssetStatusData d, int cap)
    {
        var sb = new StringBuilder();
        sb.Append("asset status — profile '").Append(d.ProfileName.Length > 0 ? d.ProfileName : "(unconfigured)")
          .Append("'  (").Append(d.Results.Count).Append(" path").Append(d.Results.Count == 1 ? "" : "s").Append(" queried)\n");

        // Q3 alarms FIRST, before the per-path list, so a long batch can't truncate them away.
        AppendReadFailures(sb, d.BsaFailures, cap);
        AppendDiscoveryWarnings(sb, d.Warnings, cap);

        int shown = 0;
        foreach (var r in d.Results)
        {
            if (sb.Length >= cap)
            {
                sb.Append("\n  ... [").Append(d.Results.Count - shown)
                  .Append(" more path(s) omitted at max_chars=").Append(cap).Append("; raise max_chars to see all]\n");
                break;
            }
            AppendPath(sb, r, d.ReadIncomplete, d.Warnings.Count > 0);
            shown++;
        }
        return sb.ToString().TrimEnd('\n');
    }

    static void AppendPath(StringBuilder sb, AssetPathResult r, bool readIncomplete, bool discoveryIncomplete)
    {
        sb.Append('\n').Append(r.RelPath).Append('\n');

        if (r.Error is not null)                                  // a rejected path (drive-rooted / '..') — per-path Q3
        {
            sb.Append("  error: ").Append(r.Error).Append('\n');
            return;
        }

        var hit = r.Hit!;
        if (!hit.Exists)
        {
            sb.Append("  ABSENT — no active mod or BSA provides this path\n");
            // Both "the scan was incomplete" conditions hedge an ABSENT at the POINT OF USE (Q3 — symmetric honesty),
            // not just at the top-of-output note: an archive that failed to READ, AND base archives that were never
            // DISCOVERED (a Skyrim.ini we couldn't find → vanilla "Skyrim - Textures*.bsa" unscanned). Either means the
            // asset could exist where we didn't look — the exact over-trust an "absent → the NPC is fine" call must avoid.
            if (readIncomplete)
                sb.Append("  [!] but an archive failed to read this build (see the read-failure note above), so " +
                          "\"absent\" may be incomplete — the asset could live in the unreadable archive.\n");
            if (discoveryIncomplete)
                sb.Append("  [!] some archives were not scanned this build (see the discovery note above), so " +
                          "\"absent\" may be incomplete — base-game assets live in BSAs that weren't enumerated.\n");
            return;
        }

        sb.Append("  WINS: ").Append(hit.Winner!.Source).Append(" (").Append(Kind(hit.Winner.Kind)).Append(")\n");
        sb.Append("  providers (").Append(hit.Providers.Count).Append("): ");
        for (int i = 0; i < hit.Providers.Count; i++)
        {
            if (i > 0) sb.Append(" > ");
            sb.Append(hit.Providers[i].Source).Append(" (").Append(Kind(hit.Providers[i].Kind)).Append(')');
        }
        sb.Append('\n');
        if (hit.Ambiguous)
            sb.Append("  note: more than one source provides this — the winner above is the precedence call (loose " +
                      "beats BSA; among BSAs the latest-loaded plugin wins). Verify only if that's unexpected.\n");
    }

    /// <summary>The archive-read-failure alarm (Q3): BSAs that couldn't be read this build, each named with its owning
    /// plugin + the reason. An asset present ONLY in one of these is indistinguishable from a truly absent one, so this
    /// is surfaced LOUD before the per-path answers — an "ABSENT" below is authoritative only when this list is empty.</summary>
    static void AppendReadFailures(StringBuilder sb, IReadOnlyList<string> failures, int cap)
    {
        if (failures.Count == 0) return;
        sb.Append("\n[!] ").Append(failures.Count).Append(" archive(s) could NOT be read this build — an asset present " +
                  "only in these may read as ABSENT below:\n");
        int shown = 0;
        foreach (var f in failures)
        {
            if (sb.Length >= cap) { sb.Append("  ... [").Append(failures.Count - shown).Append(" more omitted at max_chars=").Append(cap).Append("]\n"); break; }
            sb.Append("  - ").Append(f).Append('\n'); shown++;
        }
    }

    /// <summary>Archive-discovery warnings (Q3): e.g. a Skyrim.ini whose [Archive] base-archive list couldn't be found,
    /// so the vanilla base BSAs aren't in the scan — surfaced so an "ABSENT" for a base-game asset isn't over-trusted.</summary>
    static void AppendDiscoveryWarnings(StringBuilder sb, IReadOnlyList<string> warnings, int cap)
    {
        if (warnings.Count == 0) return;
        sb.Append("\n[!] discovery (").Append(warnings.Count).Append("):\n");
        int shown = 0;
        foreach (var w in warnings)
        {
            if (sb.Length >= cap) { sb.Append("  ... [").Append(warnings.Count - shown).Append(" more omitted at max_chars=").Append(cap).Append("]\n"); break; }
            sb.Append("  - ").Append(w).Append('\n'); shown++;
        }
    }

    static string Kind(HousecarlCore.AssetKind k) => k == HousecarlCore.AssetKind.Bsa ? "BSA" : "loose";
}
