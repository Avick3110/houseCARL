using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using HousecarlCore;

namespace HousecarlMcp;

/// <summary>
/// houseCARL's LOCAL mod-update reader (no network). Reads MO2's OWN Nexus update cache — the modid / version /
/// newestVersion fields MO2 writes into each mod's meta.ini — and reports which installed mods MO2 already believes have
/// a newer version. This is the cheap FIRST pass of update triage: it narrows a big modlist to the handful worth
/// verifying online (housecarl_nexus_check_updates), reading only files MO2 has already populated. Works fully offline;
/// it does NOT touch Nexus and does NOT modify anything. Sits in the MO2-static-read lane beside housecarl_load_order_status.
/// </summary>
[McpServerToolType]
public static class UpdateStatusTools
{
    [McpServerTool(Name = "housecarl_update_status", ReadOnly = true, Title = "MO2's local mod-update cache (no network)"),
     Description(
         "Report which of your installed mods MO2 already thinks have a newer version — read from MO2's OWN local cache " +
         "(each mod's meta.ini), with NO network and NO API key. For every Nexus-linked mod it compares the installed " +
         "version against the newest version MO2 last learned and lists the ones that DIFFER (plus any you told MO2 to " +
         "ignore, and how many were never checked or are current). This is the cheap FIRST pass of update triage — it " +
         "narrows a big modlist to the handful worth checking online — but it is only as fresh as MO2's last Nexus check, " +
         "and a 'never checked' mod is NOT 'up to date' (Q3). To verify live, pass the flagged mod ids to " +
         "housecarl_nexus_check_updates; use housecarl_nexus_mod changelog=true to see what changed. READ-ONLY, works " +
         "OFFLINE, and modifies/updates NOTHING. Needs a configured MO2 instance (housecarl_set_mo2_instance).")]
    public static string UpdateStatus(
        LoadOrderService svc,
        [Description("Optional. Max characters before the mod lists are cut with an explicit notice. 0 = the server default (~40k).")]
            int max_chars = 0) => Guard.Tool("housecarl_update_status", () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        var data = svc.UpdateCache();
        return UpdateStatusWire.Render(data, max_chars > 0 ? max_chars : 40_000);
    });
}

/// <summary>Renders <see cref="UpdateCacheData"/>: a summary line, then the mods MO2 has a newer version cached for
/// (the actionable set), then the ignored set. Current / never-checked mods are counted, not listed (the point is the
/// FILTER). Every render ends with the Q3 honesty note — this is MO2's cached view, not a live Nexus check.</summary>
static class UpdateStatusWire
{
    public static string Render(UpdateCacheData d, int cap)
    {
        var sb = new StringBuilder();
        sb.Append("MO2 update cache (LOCAL, no network) — instance: ")
          .Append(d.InstanceDir ?? "explicit-paths mode").Append('\n');

        var flagged = new List<ModUpdateEntry>();
        var ignored = new List<ModUpdateEntry>();
        int current = 0, neverChecked = 0;
        foreach (var e in d.Entries)
        {
            if (string.IsNullOrWhiteSpace(e.Newest)) { neverChecked++; continue; }   // MO2 never learned a newer version
            bool differs = !string.Equals(e.Newest.Trim(), (e.Installed ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
            if (!differs) { current++; continue; }
            if (e.Ignored is not null && string.Equals(e.Newest.Trim(), e.Ignored.Trim(), StringComparison.OrdinalIgnoreCase))
                ignored.Add(e);
            else flagged.Add(e);
        }

        sb.Append(d.Entries.Count).Append(" Nexus-linked mod(s): ")
          .Append(flagged.Count).Append(" with a newer version cached · ")
          .Append(ignored.Count).Append(" ignored · ")
          .Append(current).Append(" current · ")
          .Append(neverChecked).Append(" never checked");
        if (d.UntrackedCount > 0) sb.Append("  (+").Append(d.UntrackedCount).Append(" non-Nexus mods/separators skipped)");
        sb.Append('\n');
        foreach (var p in d.Problems) sb.Append("[!] ").Append(p).Append('\n');

        AppendEntries(sb, "UPDATE cached — MO2 learned a newer version (verify live before updating)", flagged, cap);
        AppendEntries(sb, "ignored — you told MO2 to skip this newer version", ignored, cap);

        sb.Append("\nnote: this is MO2's OWN cached view — only as fresh as MO2's last Nexus check, and 'never checked' is ")
          .Append("NOT 'up to date'. To verify against Nexus right now, feed the flagged mod ids to ")
          .Append("housecarl_nexus_check_updates.");
        return sb.ToString().TrimEnd('\n');
    }

    static void AppendEntries(StringBuilder sb, string label, IReadOnlyList<ModUpdateEntry> rows, int cap)
    {
        sb.Append('\n').Append(label).Append(" (").Append(rows.Count).Append("):");
        if (rows.Count == 0) { sb.Append(" none\n"); return; }
        sb.Append('\n');
        int shown = 0;
        foreach (var e in rows)
        {
            if (sb.Length >= cap) { sb.Append("  ... [").Append(rows.Count - shown).Append(" more omitted at max_chars=").Append(cap).Append("]\n"); break; }
            sb.Append("  - ").Append(e.Folder);
            if (e.Enabled == false) sb.Append("  [disabled]");
            sb.Append("  [id ").Append(e.ModId).Append("]  v").Append(e.Installed ?? "?").Append(" → v").Append(e.Newest ?? "?");
            var checkedOn = StaleDay(e.LastUpdate);
            if (checkedOn is not null) sb.Append("  (checked ").Append(checkedOn).Append(')');
            sb.Append('\n'); shown++;
        }
    }

    /// <summary>Unix-seconds string → yyyy-MM-dd, or null if it isn't a usable timestamp.</summary>
    static string? StaleDay(string? unix)
    {
        if (long.TryParse(unix, out var s) && s > 0)
            try { return DateTimeOffset.FromUnixTimeSeconds(s).ToString("yyyy-MM-dd"); } catch { }
        return null;
    }
}
