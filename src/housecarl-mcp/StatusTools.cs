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
         "(mod) or active/inactive/implicit (plugin). Also reports the resolved Papyrus script-log and SKSE crash-log " +
         "FOLDERS — where to Read logs for triage/diagnosis (auto-detected, or as set via housecarl_set_tool_path). " +
         "Does NOT modify anything.")]
    public static string LoadOrderStatus(
        LoadOrderService svc,
        ToolPathResolver tools,
        [Description("Optional. A mod folder name or plugin filename to look up. Omit for the whole-profile summary.")]
            string? lookup = null,
        [Description("Optional. Max characters before name lists are cut with an explicit notice. 0 = the server default (~80k).")]
            int max_chars = 0)
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        var data = svc.StatusData();
        var logs = StatusWire.LogFolders(tools);                 // resolved Papyrus/crash log dirs (pure — no persist)
        return StatusWire.Render(data, logs, lookup, max_chars > 0 ? max_chars : 80_000);
    }
}

/// <summary>Renders <see cref="LoadOrderStatusData"/> as compact, scannable text: a header line per category, then the
/// small/interesting name lists (disabled mods, inactive plugins, implicit masters), each bounded by max_chars with an
/// explicit cut notice (Q3 — never silent truncation). lookup= switches to a single mod/plugin verdict.</summary>
static class StatusWire
{
    public static string Render(LoadOrderStatusData d, IReadOnlyList<LogFolderView> logs, string? lookup, int cap)
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
            AppendLookup(sb, c, d.ExcludedPlugins, lookup.Trim());
            return sb.ToString().TrimEnd('\n');
        }

        AppendExcluded(sb, d.ExcludedPlugins, cap);   // Q3 health alarm — prominent, before the routine name lists
        AppendLogs(sb, logs);   // fixed-tiny — before the cap-bounded name lists, so a long modlist can't truncate it away

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

    /// <summary>Build the log-folder views for the status surface: where papyrus_logs + crash_logs resolve (saved →
    /// auto-detected → unset), PURE (no persist — a ReadOnly status read mutates nothing). These two are the only tool deps
    /// surfaced here because they have NO wrapping tool — the AI must be TOLD where to Read them; the compiler / BSArch deps
    /// instead surface through their riders' forcing prompts when called.</summary>
    public static IReadOnlyList<LogFolderView> LogFolders(ToolPathResolver tools)
    {
        var views = new List<LogFolderView>(2);
        foreach (var dep in new[] { ToolDependency.PapyrusLogs, ToolDependency.CrashLogs })
        {
            var (path, source) = tools.Inspect(dep);
            views.Add(new LogFolderView(ToolBridge.Info(dep).Key, path, source));
        }
        return views;
    }

    /// <summary>The external LOG FOLDERS section: where houseCARL resolves the Papyrus script-log + SKSE crash-log dirs.
    /// Logs have no wrapping tool, so this tells the AI WHERE to Read them; an unset one names the housecarl_set_tool_path
    /// call to point at it. Fixed-tiny (2 entries) and rendered before the cap-bounded name lists, so a long modlist with a
    /// small max_chars can never truncate the log paths away (Q3).</summary>
    static void AppendLogs(StringBuilder sb, IReadOnlyList<LogFolderView> logs)
    {
        sb.Append("\nlog folders (Read the .log files directly — logs have no wrapping tool):\n");
        foreach (var l in logs)
        {
            sb.Append("  ").Append(l.Key).Append(": ").Append(l.Source switch
            {
                ToolPathSource.Saved        => l.Path + "  (configured)",
                ToolPathSource.AutoDetected => l.Path + "  (auto-detected)",
                _                           => $"not set — call housecarl_set_tool_path(tool='{l.Key}', path='<folder>') to point houseCARL at it",
            }).Append('\n');
        }
    }

    /// <summary>The EXCLUDED-plugins alarm (Q3): plugins dropped from the index this build — unopenable, or carrying a
    /// record Mutagen can't parse (a malformed subrecord the game ignores but Mutagen rejects). houseCARL reads NONE of
    /// an excluded plugin, but EVERY other plugin works — surfaced loud so the user can fix/remove the upstream plugin
    /// (before this fix, ONE such record bricked every call). Rendered before the routine name lists so a long modlist
    /// can't truncate it away.</summary>
    static void AppendExcluded(StringBuilder sb, IReadOnlyDictionary<string, string> excluded, int cap)
    {
        if (excluded.Count == 0) return;
        sb.Append("\n[!] EXCLUDED plugins (").Append(excluded.Count)
          .Append(") — dropped from the index this session; houseCARL does NOT read these, every OTHER plugin works:\n");
        int shown = 0;
        foreach (var kv in excluded)
        {
            if (sb.Length >= cap) { sb.Append("  ... [").Append(excluded.Count - shown).Append(" more omitted at max_chars=").Append(cap).Append("]\n"); break; }
            sb.Append("  - ").Append(kv.Key).Append(": ").Append(kv.Value).Append('\n'); shown++;
        }
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

    static void AppendLookup(StringBuilder sb, HousecarlCore.Mo2Composition c,
                             IReadOnlyDictionary<string, string> excluded, string name)
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

        // An active plugin can still be EXCLUDED from the index (a record Mutagen can't parse) — say so (Q3), so the
        // "ACTIVE … houseCARL reads/writes it" line above isn't taken as the whole truth.
        if (excluded.TryGetValue(name, out var why))
            sb.Append("  [!] EXCLUDED this session: ").Append(why).Append("\n      → houseCARL does NOT read this plugin (every other plugin is unaffected).\n");
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

/// <summary>One external LOG FOLDER for the status surface: its wire key (papyrus_logs / crash_logs — the
/// housecarl_set_tool_path token), where it resolved (null if unset), and HOW (<see cref="ToolPathSource"/>). The AI reads
/// the .log files at <see cref="Path"/> with its normal Read tool — logs are the one bridge dep with no wrapping tool.</summary>
public sealed record LogFolderView(string Key, string? Path, ToolPathSource Source);
