using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;

namespace HousecarlMcp;

/// <summary>Read-only view of the active MO2 profile's load-order composition: enabled versus disabled mods, and
/// active versus inactive versus implicit plugins. The enabled/disabled picture is read fresh from the profile each
/// call via <see cref="HousecarlCore.Mo2LoadOrder.ReadComposition"/>; the resolved and record counts reflect the
/// resolver's last build, with a staleness note if the profile changed since.</summary>
[McpServerToolType]
public static class StatusTools
{
    [McpServerTool(Name = ToolNames.LoadOrderStatus, ReadOnly = true, Title = "Load-order status (enabled/disabled mods & plugins)"),
     Description(
         "Report what houseCARL sees in the active MO2 profile: enabled vs DISABLED mods, active vs INACTIVE plugins, " +
         "the implicit force-loaded masters/CC, how many plugins resolved to real files, and any load-order warnings. " +
         "The enabled/disabled picture is read FRESH each call, so a mod/plugin you just toggled in MO2 shows " +
         "immediately; the resolved count reflects the resolver's last build, which houseCARL refreshes AUTOMATICALLY on " +
         "each call when the profile changed — no restart needed (a 'refresh still pending' note appears only in the rare " +
         "case MO2 was mid-write). Pass lookup= a mod folder name (e.g. 'Requiem " +
         "Lite 2') or a plugin filename (e.g. 'Requiem.esp') to ask whether houseCARL sees that one as enabled/disabled " +
         "(mod) or active/inactive/implicit (plugin). Also reports the resolved Papyrus script-log and SKSE crash-log " +
         "FOLDERS — where to Read logs for triage/diagnosis (auto-detected, or as set via " + ToolNames.SetToolPath + "). " +
         "Also reports the RUNNING SERVER's build version (the binary's informational version — the release version, " +
         "then '+' and the full commit sha, e.g. '1.9.5-dev+e942910...'), so an installed-build-vs-source check never " +
         "has to read the exe's file properties. " +
         "Does NOT modify anything.")]
    public static string LoadOrderStatus(
        LoadOrderService svc,
        ToolPathResolver tools,
        [Description("Optional. A mod folder name or plugin filename to look up. Omit for the whole-profile summary.")]
            string? lookup = null,
        [Description("Optional. A profile NAME to INSPECT without switching to it (e.g. 'Default', 'Modded') — reports that " +
            "profile's enabled/disabled mods + active/inactive plugins even if it is not the active one, so you can compare " +
            "load orders across profiles. Omit to describe the ACTIVE profile (which also lists the available profile names). " +
            "MO2-instance mode only (explicit-paths mode has no profiles folder); if both lookup= and profile= are given, " +
            "both render.")]
            string? profile = null,
        [Description("Optional. Max characters before name lists are cut with an explicit notice. 0 = the server default (~80k).")]
            int max_chars = 0) => Guard.Tool(ToolNames.LoadOrderStatus, () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        var data = svc.StatusData();
        var logs = StatusWire.LogFolders(tools);                 // resolved Papyrus/crash log dirs (pure — no persist)
        var profiles = svc.NamedProfileComposition(profile);     // available-profile discovery + inactive-profile inspection: text parse only, no index build, no switch
        return StatusWire.Render(data, logs, profiles, lookup, max_chars > 0 ? max_chars : 80_000);
    });
}

/// <summary>The running server's build version, read once from the informational version the tool assembly embeds
/// (build-plugin.ps1 stamps it from plugin.json, so it carries the release version, '+', and the full commit sha:
/// '1.9.5-dev+e942910...'). The tool assembly,
/// not the entry assembly: they are the same binary for the shipped server, and under a test host the entry assembly
/// is the host rather than houseCARL. An unstamped build says so rather than rendering a blank.</summary>
public static class ServerBuild
{
    /// <summary>The informational version verbatim, metadata suffix and all; null on an unstamped build.</summary>
    public static string? Version { get; } =
        ToolSurface.Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), inherit: false)
            is [System.Reflection.AssemblyInformationalVersionAttribute a, ..] && !string.IsNullOrWhiteSpace(a.InformationalVersion)
            ? a.InformationalVersion : null;

    /// <summary>What the status line prints: the version, or a sentence naming the absent attribute.</summary>
    public static string Line { get; } =
        Version ?? "unstamped build — this binary carries no informational version, so houseCARL cannot name its own build; " +
                   "read the version off housecarl-mcp.exe's file properties instead.";
}

/// <summary>Renders <see cref="LoadOrderStatusData"/>: a header line per category, then the name lists (disabled mods,
/// inactive plugins, implicit masters), each bounded by max_chars with an explicit cut notice. lookup= switches to a
/// single mod/plugin verdict.</summary>
static class StatusWire
{
    public static string Render(LoadOrderStatusData d, IReadOnlyList<LogFolderView> logs, NamedProfileResult profiles, string? lookup, int cap)
    {
        var c = d.Composition;
        int checkedActive = c.ActivePluginNames.Count;
        int impl = c.ImplicitPluginNames.Count;
        int inactive = c.InactivePluginNames.Count;
        int gameLoaded = checkedActive + impl;

        var sb = new StringBuilder();
        sb.Append("load order status — profile '").Append(d.ProfileName).Append("'\n");
        // The running server's own build, so an installed-build-vs-source check never leaves the tool surface.
        sb.Append("server:   ").Append(ServerBuild.Line).Append('\n');
        // The resolved MO2 instance; null means explicit-paths mode, where the three roots are set directly and there
        // is no MO2 instance folder.
        sb.Append("instance: ").Append(d.InstanceDir ?? "explicit-paths mode (no MO2 instance configured)").Append('\n');
        sb.Append("mods:    ").Append(c.EnabledMods.Count).Append(" enabled · ").Append(c.DisabledMods.Count).Append(" disabled\n");
        sb.Append("plugins in load order: ").Append(c.OrderedPluginNames.Count).Append('\n');
        sb.Append("  active:   ").Append(gameLoaded).Append("  (").Append(checkedActive).Append(" checked + ").Append(impl).Append(" implicit masters/CC)\n");
        sb.Append("  inactive: ").Append(inactive).Append("  (present but unchecked — houseCARL excludes these)\n");
        sb.Append("resolver: ").Append(d.ResolvedPluginCount).Append(" plugins resolved to real files");
        if (d.MaxPlugins > 0) sb.Append(" [capped at MaxPlugins=").Append(d.MaxPlugins).Append(']');
        if (d.Epoch is not null) sb.Append("  epoch=").Append(d.Epoch);   // the current build's fingerprint — bulk responses stamp the build they read, matched against this
        sb.Append('\n');
        if (d.ProfileChanged)
            sb.Append("[!] the profile changed mid-call and a refresh is still pending — houseCARL re-reads it " +
                      "automatically on the next tool call (lazy refresh; no restart needed).\n");

        // profile= renders before the lookup branch, which returns early: the two compose, since lookup verdicts the
        // active profile while this inspects another, possibly inactive, one without switching to it.
        if (profiles.RequestedName is not null) AppendNamedProfile(sb, profiles, cap);

        if (lookup is { Length: > 0 })
        {
            AppendLookup(sb, c, d.ExcludedPlugins, lookup.Trim());
            return sb.ToString().TrimEnd('\n');
        }

        AppendExcluded(sb, d.ExcludedPlugins, cap);   // health alarm — before the routine name lists
        AppendLogs(sb, logs);   // two lines only, before the cap-bounded name lists, so a long modlist can't truncate it away
        AppendAvailableProfiles(sb, profiles, cap);   // makes the inactive-profile read discoverable

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

    /// <summary>Where papyrus_logs and crash_logs resolve — saved, auto-detected, or unset — without persisting, since a
    /// read-only status call must mutate nothing. These two are the only tool dependencies surfaced here because they
    /// have no wrapping tool; the compiler and BSArch surface through their own prompts when called.</summary>
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

    /// <summary>The log-folders section: where the Papyrus script-log and SKSE crash-log directories resolve. Logs have
    /// no wrapping tool, so this says where to read them, and an unset one names the call that points at it. Two
    /// entries, rendered before the cap-bounded name lists so they can never be truncated away.</summary>
    static void AppendLogs(StringBuilder sb, IReadOnlyList<LogFolderView> logs)
    {
        sb.Append("\nlog folders (Read the .log files directly — logs have no wrapping tool):\n");
        foreach (var l in logs)
        {
            sb.Append("  ").Append(l.Key).Append(": ").Append(l.Source switch
            {
                ToolPathSource.Saved        => l.Path + "  (configured)",
                ToolPathSource.AutoDetected => l.Path + "  (auto-detected)",
                _                           => $"not set — call {ToolNames.SetToolPath}(tool='{l.Key}', path='<folder>') to point houseCARL at it",
            }).Append('\n');
        }
    }

    /// <summary>Plugins dropped from the index this build: unopenable, or carrying a record Mutagen cannot parse (a
    /// malformed subrecord the game ignores but Mutagen rejects). None of an excluded plugin is read, while every
    /// other plugin still works. Rendered before the routine name lists so a long modlist cannot truncate it away.</summary>
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

    /// <summary>Render the requested named profile's composition, or a refusal. Explicit-paths mode has no profiles
    /// folder, so it refuses rather than enumerating an arbitrary directory; a name matching no profile lists the real
    /// options rather than rendering an empty composition. A match says plainly that the active profile is
    /// unchanged — this is inspection, not a switch.</summary>
    static void AppendNamedProfile(StringBuilder sb, NamedProfileResult p, int cap)
    {
        if (!p.InstanceMode)
        {
            sb.Append("\nprofile '").Append(p.RequestedName)
              .Append("': can't inspect — that needs MO2-instance mode; in explicit-paths mode there is no profiles folder to read from.\n");
            return;
        }
        if (p.Composition is null)                                // not found: name the real options, never an empty composition
        {
            sb.Append("\nprofile '").Append(p.RequestedName).Append("' not found — ");
            AppendProfileNames(sb, "available", p.AvailableProfiles, cap);
            return;
        }
        var c = p.Composition;
        int active = c.ActivePluginNames.Count + c.ImplicitPluginNames.Count;
        sb.Append("\n— inspecting profile '").Append(p.RequestedName).Append("' (read-only; the active profile is unchanged):\n");
        sb.Append("  mods:    ").Append(c.EnabledMods.Count).Append(" enabled · ").Append(c.DisabledMods.Count).Append(" disabled\n");
        sb.Append("  plugins: ").Append(c.OrderedPluginNames.Count).Append(" in order · ").Append(active).Append(" active · ")
          .Append(c.InactivePluginNames.Count).Append(" inactive\n");
        // Any read note, e.g. a missing modlist.txt, so a zero-enabled-mods inspection is not mistaken for a genuinely
        // empty profile. Rendered before the lists, like the active status' own warnings block.
        foreach (var warn in p.Warnings)
            sb.Append("  [!] ").Append(warn).Append('\n');
        AppendList(sb, "  disabled mods", c.DisabledMods, cap);
        AppendList(sb, "  inactive plugins", c.InactivePluginNames, cap);
    }

    /// <summary>The available profile names, so the inactive-profile read is discoverable from the default status.
    /// Suppressed in explicit-paths mode, which has no profiles folder, and when profile= was asked for, since the
    /// named block already lists them on a miss.</summary>
    static void AppendAvailableProfiles(StringBuilder sb, NamedProfileResult p, int cap)
    {
        if (!p.InstanceMode || p.RequestedName is not null) return;
        sb.Append('\n');
        AppendProfileNames(sb, "profiles available", p.AvailableProfiles, cap);
        if (p.AvailableProfiles.Count > 0)
            sb.Append("  → pass profile='<name>' to inspect any of these WITHOUT switching to it.\n");
    }

    /// <summary>One line of comma-joined profile names, cap-bounded with an explicit cut notice.</summary>
    static void AppendProfileNames(StringBuilder sb, string label, IReadOnlyList<string> names, int cap)
    {
        sb.Append(label).Append(" (").Append(names.Count).Append(')');
        if (names.Count == 0) { sb.Append(": none\n"); return; }
        sb.Append(": ");
        int shown = 0;
        foreach (var n in names)
        {
            if (sb.Length >= cap) { sb.Append(" ... [").Append(names.Count - shown).Append(" more omitted at max_chars=").Append(cap).Append(']'); break; }
            if (shown > 0) sb.Append(", ");
            sb.Append(n); shown++;
        }
        sb.Append('\n');
    }

    static void AppendLookup(StringBuilder sb, HousecarlCore.Mo2Composition c,
                             IReadOnlyDictionary<string, string> excluded, string name)
    {
        sb.Append("\nlookup '").Append(name).Append("':\n");

        // modMiss and pluginMiss must stay in sync with the not-found arm of their ternary below — each is the negation
        // of every hit case. They gate the "did you mean", so a desync would surface a suggestion on a non-miss.
        bool modMiss = !Contains(c.EnabledMods, name) && !Contains(c.DisabledMods, name);
        string asMod =
            Contains(c.EnabledMods, name)  ? "ENABLED (mod present + switched on)" :
            Contains(c.DisabledMods, name) ? "DISABLED (mod present but switched OFF — houseCARL excludes it)" :
                                             "not found in modlist.txt (not a managed mod folder name, or a UI separator)";
        sb.Append("  as a mod:    ").Append(asMod);
        // A near-miss is an easy slip — a dropped apostrophe, a stray word — so point at the nearest real mod folders
        // across both the enabled and disabled lists rather than leaving a flat "not found".
        if (modMiss) sb.Append(HousecarlCore.PluginNameSuggest.DidYouMean(name, c.EnabledMods.Concat(c.DisabledMods)));
        sb.Append('\n');

        bool pluginMiss = !c.ActivePluginNames.Contains(name) && !Contains(c.ImplicitPluginNames, name)
                          && !Contains(c.InactivePluginNames, name);
        string asPlugin =
            c.ActivePluginNames.Contains(name)   ? "ACTIVE (checked in plugins.txt — houseCARL reads/writes it)" :
            Contains(c.ImplicitPluginNames, name) ? "ACTIVE (implicit master/CC, force-loaded — houseCARL reads/writes it)" :
            Contains(c.InactivePluginNames, name) ? "INACTIVE (present but unchecked — houseCARL EXCLUDES it)" :
                                                    "not in the load order (no such plugin in loadorder.txt)";
        sb.Append("  as a plugin: ").Append(asPlugin);
        // The common slip is the mod-folder name passed where the .esp filename was wanted. The suggester's
        // extension-difference and prefix rules turn that into the plugin filename. Match across the whole order.
        if (pluginMiss) sb.Append(HousecarlCore.PluginNameSuggest.DidYouMean(name, c.OrderedPluginNames));
        sb.Append('\n');

        // An active plugin can still be excluded from the index if it carries a record Mutagen cannot parse, so the
        // "ACTIVE ... houseCARL reads/writes it" line above must not be taken as the whole truth.
        if (excluded.TryGetValue(name, out var why))
            sb.Append("  [!] EXCLUDED this session: ").Append(why).Append("\n      → houseCARL does NOT read this plugin (every other plugin is unaffected).\n");
    }

    static bool Contains(IReadOnlyList<string> list, string name)
    {
        foreach (var s in list) if (string.Equals(s, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}

/// <summary>One external log folder for the status surface: its wire key (papyrus_logs or crash_logs), where it
/// resolved (null if unset), and how (<see cref="ToolPathSource"/>). The .log files at <see cref="Path"/> are read
/// directly — logs are the one dependency with no wrapping tool.</summary>
public sealed record LogFolderView(string Key, string? Path, ToolPathSource Source);
