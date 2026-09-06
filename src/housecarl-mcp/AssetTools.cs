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
         "who is this asset coming from / is this texture loose or in a BSA / is this asset even present / why isn't my " +
         "override applying' for ANY mesh, texture, script, sound, interface, or other Data-relative path. Pass " +
         "asset_paths = one or more paths RELATIVE to the Data folder (forward or back slashes both fine; a drive-rooted " +
         "or '..'-escaping path is rejected per-path), and/or under = a Data-relative DIRECTORY or glob, which resolves " +
         "every file the VFS provides beneath it — one call over " +
         "'meshes/actors/character/facegendata/facegeom/Skyrim.esm' answers for every facegen mesh a master defines, " +
         "with no path list at all. The two select forms compose in one call. An archive that cannot be read, or a " +
         "Skyrim.ini base-archive list that cannot be found, is reported LOUD — so an 'absent' answer is never silently " +
         "trusted when the scan was incomplete. format='json' returns the same data machine-readably, with the same " +
         "accounting in-band. Read-only: resolves nothing to disk, writes nothing, changes no load order.")]
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
        [Description("TRANSPORT: 'text' (default) | 'json' (the same data, machine-readable, accounting in-band).")]
            string? format = null,
        [Description("TRANSPORT: character CEILING on the whole response, not just on the per-path list — the path whose block would cross it is not written at all, and the notice says how many were held back. The alarms and the accounting line are charged before the paths render, so both are inside the ceiling. A cap too small for what the response carries whatever the budget says so and names the cap that clears it in one step. 0 = the server default (~80k).")]
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

        if ((asset_paths is null || asset_paths.Length == 0) && (under is null || under.Length == 0))
            return Refuse("asset_paths and under are both empty. Pass Data-relative asset path(s) in asset_paths " +
                          "(e.g. 'textures/armor/iron/cuirass_1.dds'), or a Data-relative directory or glob in under " +
                          "(e.g. 'meshes/actors/character/facegendata/facegeom/Skyrim.esm').");
        // The window's own refusal, from the window: this tool and housecarl_skse answer the same input class, so the
        // sentence is spelled once rather than reworded in two places.
        if (new RowWindow(offset, limit).Error is { } bad) return Wire.Refuse(json, bad);
        var data = svc.AssetStatus(asset_paths ?? Array.Empty<string>(), under, limit, offset);
        int cap = max_chars > 0 ? max_chars : 80_000;
        return json ? JsonWire.RenderAssetStatus(data, cap) : AssetWire.Render(data, cap);
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
            reserve: AccountingReserve(d));

        return RenderCap.Settle(body + TransportAccounting.Compose(Tally(d, rendered), RowNoun, everySentence: false), cap);
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
        if (!cap.TryAppend(sb, "\n[!] under (" + notes.Count + "):\n")) return;
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

        // The provider token is spelled by the one formatter the asset surface uses, so the name printed here is the
        // name place_asset's source_provider= accepts — the third surface of #340. A mod folder can legitimately hold
        // a parenthetical ("SkyUI (SE)"), so the delimiter is what tells a caller where the name ends.
        sb.Append("  WINS: ").Append(Provider(hit.Winner!)).Append('\n');
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
    }

    static string Kind(HousecarlCore.AssetKind k) => k == HousecarlCore.AssetKind.Bsa ? "BSA" : "loose";

    /// <summary>One provider, spelled by the shared formatter (#340): the name inside double quotes — a character a
    /// Windows folder or file name cannot contain — with the kind outside them, so the printed token is the token a
    /// source selector accepts.</summary>
    static string Provider(HousecarlCore.AssetProvider p)
        => HousecarlCore.AssetSourceSelection.Describe(p.Source, Kind(p.Kind));
}
