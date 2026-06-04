using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;

namespace HousecarlMcp;

/// <summary>
/// houseCARL diagnostic tool (post-§8). Read-only. Surfaces the active MO2 profile's load-order COMPOSITION — what
/// houseCARL sees as enabled vs DISABLED (mods) and active vs INACTIVE vs implicit (plugins) — so the user can confirm
/// houseCARL resolves reads/writes against the right active set (and SEE what it excludes, not just trust that it does).
/// The enabled/disabled picture is read FRESH from the profile each call (cheap text-file parse via
/// <see cref="HousecarlCore.Mo2LoadOrder.ReadComposition"/>); resolved/record counts reflect the resolver's last build,
/// with a staleness note (Q3) if the profile changed since.
/// </summary>
[McpServerToolType]
public static class StatusTools
{
    [McpServerTool(Name = "housecarl_load_order_status", ReadOnly = true, Title = "Load-order status (enabled/disabled mods & plugins)"),
     Description(
         "Report what houseCARL sees in the active MO2 profile: enabled vs DISABLED mods, active vs INACTIVE plugins, " +
         "the implicit force-loaded masters/CC, how many plugins resolved to real files, and any load-order warnings. " +
         "The enabled/disabled picture is read FRESH each call, so a mod/plugin you just toggled in MO2 shows " +
         "immediately; the resolved count reflects the resolver's last build, which houseCARL refreshes AUTOMATICALLY on " +
         "each call when the profile changed — no restart needed (a 'refresh still pending' note appears only in the rare " +
         "case MO2 was mid-write). Pass lookup= a mod folder name (e.g. 'Requiem " +
         "Lite 2') or a plugin filename (e.g. 'Requiem.esp') to ask whether houseCARL sees that one as enabled/disabled " +
         "(mod) or active/inactive/implicit (plugin). Does NOT modify anything.")]
    public static string LoadOrderStatus(
        LoadOrderService svc,
        [Description("Optional. A mod folder name or plugin filename to look up. Omit for the whole-profile summary.")]
            string? lookup = null,
        [Description("Optional. Max characters before name lists are cut with an explicit notice. 0 = the server default (~80k).")]
            int max_chars = 0)
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        var data = svc.StatusData();
        return StatusWire.Render(data, lookup, max_chars > 0 ? max_chars : 80_000);
    }
}

/// <summary>Renders <see cref="LoadOrderStatusData"/> as compact, scannable text: a header line per category, then the
/// small/interesting name lists (disabled mods, inactive plugins, implicit masters), each bounded by max_chars with an
/// explicit cut notice (Q3 — never silent truncation). lookup= switches to a single mod/plugin verdict.</summary>
static class StatusWire
{
    public static string Render(LoadOrderStatusData d, string? lookup, int cap)
    {
        var c = d.Composition;
        int checkedActive = c.ActivePluginNames.Count;
        int impl = c.ImplicitPluginNames.Count;
        int inactive = c.InactivePluginNames.Count;
        int gameLoaded = checkedActive + impl;

        var sb = new StringBuilder();
        sb.Append("load order status — profile '").Append(ProfileName(d.ProfileDir)).Append("'\n");
        sb.Append("mods:    ").Append(c.EnabledMods.Count).Append(" enabled · ").Append(c.DisabledMods.Count).Append(" disabled\n");
        sb.Append("plugins in load order: ").Append(c.OrderedPluginNames.Count).Append('\n');
        sb.Append("  active:   ").Append(gameLoaded).Append("  (").Append(checkedActive).Append(" checked + ").Append(impl).Append(" implicit masters/CC)\n");
        sb.Append("  inactive: ").Append(inactive).Append("  (present but unchecked — houseCARL excludes these)\n");
        sb.Append("resolver: ").Append(d.ResolvedPluginCount).Append(" plugins resolved to real files");
        if (d.MaxPlugins > 0) sb.Append(" [capped at MaxPlugins=").Append(d.MaxPlugins).Append(']');
        sb.Append('\n');
        if (d.ProfileChanged)
            sb.Append("[!] the profile changed mid-call and a refresh is still pending — houseCARL re-reads it " +
                      "automatically on the next tool call (lazy refresh; no restart needed).\n");

        if (lookup is { Length: > 0 })
        {
            AppendLookup(sb, c, lookup.Trim());
            return sb.ToString().TrimEnd('\n');
        }

        AppendList(sb, "disabled mods", c.DisabledMods, cap);
        AppendList(sb, "inactive plugins", c.InactivePluginNames, cap);
        AppendList(sb, "implicit masters / CC", c.ImplicitPluginNames, cap);

        if (d.Warnings.Count > 0)
        {
            sb.Append("\nwarnings (").Append(d.Warnings.Count).Append("):\n");
            int shown = 0;
            foreach (var w in d.Warnings)
            {
                if (sb.Length >= cap) { sb.Append("  ... [").Append(d.Warnings.Count - shown).Append(" more omitted at max_chars=").Append(cap).Append("]\n"); break; }
                sb.Append("  - ").Append(w).Append('\n'); shown++;
            }
        }
        return sb.ToString().TrimEnd('\n');
    }

    static void AppendList(StringBuilder sb, string label, IReadOnlyList<string> names, int cap)
    {
        sb.Append('\n').Append(label).Append(" (").Append(names.Count).Append("):");
        if (names.Count == 0) { sb.Append(" none\n"); return; }
        sb.Append('\n');
        int shown = 0;
        foreach (var n in names)
        {
            if (sb.Length >= cap) { sb.Append("  ... [showing ").Append(shown).Append(" of ").Append(names.Count).Append("; raise max_chars to see all]\n"); break; }
            sb.Append("  - ").Append(n).Append('\n'); shown++;
        }
    }

    static void AppendLookup(StringBuilder sb, HousecarlCore.Mo2Composition c, string name)
    {
        sb.Append("\nlookup '").Append(name).Append("':\n");

        string asMod =
            Contains(c.EnabledMods, name)  ? "ENABLED (mod present + switched on)" :
            Contains(c.DisabledMods, name) ? "DISABLED (mod present but switched OFF — houseCARL excludes it)" :
                                             "not found in modlist.txt (not a managed mod folder name, or a UI separator)";
        sb.Append("  as a mod:    ").Append(asMod).Append('\n');

        string asPlugin =
            c.ActivePluginNames.Contains(name)   ? "ACTIVE (checked in plugins.txt — houseCARL reads/writes it)" :
            Contains(c.ImplicitPluginNames, name) ? "ACTIVE (implicit master/CC, force-loaded — houseCARL reads/writes it)" :
            Contains(c.InactivePluginNames, name) ? "INACTIVE (present but unchecked — houseCARL EXCLUDES it)" :
                                                    "not in the load order (no such plugin in loadorder.txt)";
        sb.Append("  as a plugin: ").Append(asPlugin).Append('\n');
    }

    static string ProfileName(string profileDir)
    {
        var trimmed = profileDir.TrimEnd('\\', '/');
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? profileDir : name;
    }

    static bool Contains(IReadOnlyList<string> list, string name)
    {
        foreach (var s in list) if (string.Equals(s, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
