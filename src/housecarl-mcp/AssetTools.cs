using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;

namespace HousecarlMcp;

/// <summary>Read-only asset resolution: which mod or BSA provides a Data-relative path, and which copy wins in game —
/// loose files beat BSA-packed, and among BSAs the latest-loaded plugin's wins. Active BSAs are discovered from the
/// same static MO2 profile read the load order uses (per-plugin "X.bsa" / "X - Textures.bsa" plus the Skyrim.ini base
/// archives). No archive handles are held at rest; freshness is an mtime check.</summary>
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
         "who is this asset coming from / is this texture loose or in a BSA / is this asset even present / why isn't my " +
         "override applying' for ANY mesh, texture, script, sound, interface, or other Data-relative path. Pass " +
         "asset_paths = one or more paths RELATIVE to the Data folder (forward or back slashes both fine; a drive-rooted " +
         "or '..'-escaping path is rejected per-path), and/or under = a Data-relative DIRECTORY or glob, which resolves " +
         "every file the VFS provides beneath it — one call over " +
         "'meshes/actors/character/facegendata/facegeom/Skyrim.esm' answers for every facegen mesh a master defines, " +
         "with no path list at all. The two select forms compose in one call. An archive that cannot be read, or a " +
         "Skyrim.ini base-archive list that cannot be found, is reported LOUD — so an 'absent' answer is never silently " +
         "trusted when the scan was incomplete. Read-only: resolves nothing to disk, writes nothing, changes no load order.")]
    public static string AssetStatus(
        LoadOrderService svc,
        [Description("The Data-relative asset path(s) to resolve, e.g. " +
                     "'textures/armor/iron/cuirass_1.dds' or 'meshes/clutter/common/tankard01.nif'. One or many; resolved " +
                     "in order, results returned in the same order. Paths are relative to the game's Data folder. " +
                     "Optional when under= is given.")]
            string[]? asset_paths = null,
        [Description("Optional. Data-relative DIRECTORY or glob selector(s): every file the load order provides beneath " +
                     "it (loose and BSA both) is resolved, e.g. " +
                     "'meshes/actors/character/facegendata/facegeom/Skyrim.esm' for one master's whole facegen set. " +
                     "Wildcards: '*' matches within one path segment, '?' one character in a segment, '**' across " +
                     "separators — 'textures/actors/character/**/*.dds'. Matches are added after any asset_paths, " +
                     "sorted, with duplicates dropped. A selector that matches nothing says so rather than passing as " +
                     "an empty sweep.")]
            string[]? under = null,
        [Description("Optional. Max paths to resolve and render from the selection. 0 = no limit.")]
            int limit = 0,
        [Description("Optional. Where in the selection the rendered window starts, for paging a large under= sweep. 0 = the beginning.")]
            int offset = 0,
        [Description("Optional. Max characters before the per-path list is cut with an explicit notice. 0 = the server default (~80k).")]
            int max_chars = 0) => Guard.Tool(ToolNames.AssetStatus, () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        if ((asset_paths is null || asset_paths.Length == 0) && (under is null || under.Length == 0))
            return "error: asset_paths and under are both empty. Pass Data-relative asset path(s) in asset_paths " +
                   "(e.g. 'textures/armor/iron/cuirass_1.dds'), or a Data-relative directory or glob in under " +
                   "(e.g. 'meshes/actors/character/facegendata/facegeom/Skyrim.esm').";
        if (limit < 0 || offset < 0)
            return $"error: limit={limit} offset={offset} — neither can be negative. Pass limit=0 for no limit and " +
                   "offset=0 to start at the beginning of the selection.";
        var data = svc.AssetStatus(asset_paths ?? Array.Empty<string>(), under, limit, offset);
        return AssetWire.Render(data, max_chars > 0 ? max_chars : 80_000);
    });
}

/// <summary>Renders <see cref="AssetStatusData"/>: the build-level alarms first (archives that failed to read,
/// discovery warnings), then one block per queried path — winner and every provider in precedence order. Contention is
/// worded neutrally, since more than one source is the common healthy case. The per-path list is bounded by max_chars
/// with an explicit cut notice.</summary>
static class AssetWire
{
    public static string Render(AssetStatusData d, int cap)
    {
        var header = new StringBuilder("asset status — profile '")
            .Append(d.ProfileName.Length > 0 ? d.ProfileName : "(unconfigured)")
            .Append("'  (").Append(d.Selected).Append(" path").Append(d.Selected == 1 ? "" : "s")
            .Append(" selected)").ToString();

        int rendered = 0;
        var body = BatchRender.Render(
            header, d.Results, "path(s)", cap,
            // Alarms come before the per-path list so a long batch cannot truncate them away.
            sb =>
            {
                BatchRender.AppendReadFailures(sb, d.BsaFailures, "an asset", cap);
                BatchRender.AppendDiscoveryWarnings(sb, d.Warnings, cap);
                AppendSelectorNotes(sb, d.SelectorNotes);
            },
            (sb, r) => { rendered++; AppendPath(sb, r, d.ReadIncomplete, d.Warnings.Count > 0); });

        return body + Accounting(d, rendered);
    }

    /// <summary>What each under= selector had to say for itself — a selector that matched nothing, or was rejected.
    /// Above the per-path list, with the other alarms, so a truncated sweep cannot cut it away. Uncapped, and bounded
    /// by the call's own input: there is at most one note per selector the caller wrote.</summary>
    static void AppendSelectorNotes(StringBuilder sb, IReadOnlyList<string>? notes)
    {
        if (notes is not { Count: > 0 }) return;
        sb.Append("\n[!] under (").Append(notes.Count).Append("):\n");
        foreach (var n in notes) sb.Append("  - ").Append(n).Append('\n');
    }

    /// <summary>The one machine-readable accounting line, always last and never subject to the cap: how many paths the
    /// selection named, how many rendered, how many the paging window left out, and how many max_chars cut. A bulk
    /// consumer keying results by path checks these numbers instead of counting prose it might miss (#246).</summary>
    static string Accounting(AssetStatusData d, int rendered)
    {
        int capped = Math.Max(d.Selected - d.Results.Count, 0);       // left out of this window by limit/offset
        int truncated = Math.Max(d.Results.Count - rendered, 0);      // left out of the render by max_chars
        int remaining = d.Selected - (d.Offset + d.Results.Count);    // still AHEAD of the window — what pages on
        var sb = new StringBuilder("\n\n[accounting] total=").Append(d.Selected)
            .Append(" rendered=").Append(rendered)
            .Append(" capped=").Append(capped)
            .Append(" truncated=").Append(truncated)
            .Append(" offset=").Append(d.Offset)
            .Append(" remaining=").Append(Math.Max(remaining, 0))
            .Append(" notes=").Append(d.SelectorNotes?.Count ?? 0);
        // Only what is still AHEAD earns a next page: on the last page there is nothing to advance to, and an offset
        // past the end would otherwise be told to re-call at the offset it already used.
        if (remaining > 0)
            sb.Append("\nthe selection is longer than this window: re-call with offset=")
              .Append(d.Offset + d.Results.Count).Append(" for the next page.");
        else if (d.Selected > 0 && d.Offset >= d.Selected)
            sb.Append("\noffset=").Append(d.Offset).Append(" is past the end of the selection (")
              .Append(d.Selected).Append(" path(s)) — the last page starts before it.");
        if (truncated > 0)
            sb.Append("\nmax_chars cut ").Append(truncated).Append(" resolved path(s) from the render: raise max_chars, or page with limit=/offset=.");
        return sb.ToString();
    }

    static void AppendPath(StringBuilder sb, AssetPathResult r, bool readIncomplete, bool discoveryIncomplete)
    {
        sb.Append('\n').Append(r.RelPath).Append('\n');

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

    static string Kind(HousecarlCore.AssetKind k) => k == HousecarlCore.AssetKind.Bsa ? "BSA" : "loose";
}
