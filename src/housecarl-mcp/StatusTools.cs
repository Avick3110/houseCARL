using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;

namespace HousecarlMcp;

/// <summary>Read-only view of the active MO2 profile's load-order composition; the enabled/disabled picture and the counts are the call's build, re-read at its start when the profile changed.</summary>
[McpServerToolType]
public static class StatusTools
{
    [McpServerTool(Name = ToolNames.LoadOrderStatus, ReadOnly = true, Title = "Load-order status (enabled/disabled mods & plugins)"),
     Description(
         "Report what houseCARL sees in the active MO2 profile: enabled and disabled mods, active and inactive plugins, " +
         "the implicit force-loaded masters and CC, how many plugins resolved to real files, and any load-order " +
         "warnings; filter= swaps the name lists and warnings for a verdict on one mod, plugin or Nexus id (enabled? " +
         "localized? which folders carry Nexus mod N? which plugins name this one as a master?). " +
         "The enabled/disabled picture and the resolved count are re-read at the start of a call when the profile " +
         "changed, so a toggle in MO2 shows with no restart; while another call is writing, or when the re-read came back " +
         "empty, status answers at once off the build from before the change; a 'refresh is still pending' note says so when " +
         "the current profile's files changed, and a switch to another profile shows no note. Without filter=, also reports the resolved Papyrus script-log and SKSE crash-log folders, " +
         "where to read logs for triage (auto-detected, or set with " + ToolNames.SetToolPath + "). Every call " +
         "reports the running server's build version (the release version, then '+' and the full commit sha). " +
         "Changes nothing.")]
    public static string LoadOrderStatus(
        LoadOrderService svc,
        ToolPathResolver tools,
        [Description("Optional. A mod folder (e.g. 'Requiem Lite 2') or plugin filename (e.g. 'Requiem.esp'): is that " +
            "mod enabled and its Nexus id, is that plugin active, inactive or implicit, and the plugin's serving folder, other copies, " +
            "header flags (localized refuses in-place writes), masters, record count, and the plugins naming it as a " +
            "master: those in the load order, then copies on disk the game does not load (in a disabled or unlisted mod " +
            "folder, unchecked in plugins.txt, or not in loadorder.txt), marked. A positive whole number (e.g. '131131') is also read as a Nexus mod id: every mod folder whose " +
            "meta.ini carries it, enabled or not; 0 and below are no id, as in MO2. Always reads the active profile. " +
            "Omit for the whole-profile summary.")]
            string? filter = null,
        [Description("Optional. A profile name to inspect without switching to it (e.g. 'Default', 'Modded'): reports " +
            "its enabled and disabled mods and active and inactive plugins, to compare load orders across profiles. Omit " +
            "for the active profile, whose report lists the available profile names. MO2-instance mode only. Given with " +
            "filter=, both render.")]
            string? profile = null,
        [Description("Optional. Max characters before name lists are cut with an explicit notice. 0 = the server default (~40k).")]
            int max_chars = 0) => Guard.Tool(ToolNames.LoadOrderStatus, call =>
    {
        // The server line goes ahead of the config prompt: an unconfigured server is exactly the stale staged install this line exists to catch.
        if (svc.ConfigPromptOrNull() is { } prompt) return ToolAnswer.Refusal(StatusWire.ServerLine + prompt);
        var data = svc.StatusData();
        var logs = StatusWire.LogFolders(tools);                 // resolved Papyrus/crash log dirs (pure — no persist)
        var profiles = svc.NamedProfileComposition(profile);     // available-profile discovery + inactive-profile inspection: text parse only, no index build, no switch
        // Read only for a filter: the facts are a per-plugin locate and header read, and the whole-profile summary asks about none.
        var name = filter is { Length: > 0 } ? filter.Trim() : null;
        var facts = name is not null && data.ModsFolderUnreadable is null ? svc.PluginFactsFor(name, data) : null;
        var lookups = name is null ? null : FilterLookups.Read(name, data);
        return ToolAnswer.Served(StatusWire.Render(data, logs, profiles, filter, facts, Wire.Cap(max_chars), lookups));
    });
}

/// <summary>Renders <see cref="LoadOrderStatusData"/>: a header line per category, then the name lists, each bounded by max_chars with an explicit cut notice; filter= switches to a single mod/plugin verdict.</summary>
static class StatusWire
{
    /// <summary>The running server's own build; public because the unconfigured answer returns before <see cref="Render"/> runs and prints this itself.</summary>
    public static string ServerLine => "server:   " + ServerBuild.Line + "\n";

    public static string Render(LoadOrderStatusData d, IReadOnlyList<LogFolderView> logs, NamedProfileResult profiles,
                                string? filter, PluginFacts? facts, int cap, FilterLookups? lookups = null)
    {
        var c = d.Composition;
        int checkedActive = c.ActivePluginNames.Count;
        int impl = c.ImplicitPluginNames.Count;
        int inactive = c.InactivePluginNames.Count;
        int gameLoaded = checkedActive + impl;

        var sb = new StringBuilder();
        sb.Append("load order status — profile '").Append(d.ProfileName).Append("'\n");
        sb.Append(ServerLine);
        // The resolved MO2 instance; null means explicit-paths mode, which has no MO2 instance folder. That mode names
        // its configured overwrite root here — the only answer that reads it back, where instance mode has set_mo2_instance's.
        sb.Append("instance: ");
        if (d.InstanceDir is { } instanceDir) sb.Append(instanceDir);
        else
        {
            sb.Append("explicit-paths mode (no MO2 instance configured)");
            if (d.OverwriteDir.Length > 0) sb.Append(" · overwrite: ").Append(d.OverwriteDir);
        }
        sb.Append('\n');
        if (d.ModsFolderUnreadable is { } unread)
            sb.Append("[!] ").Append(unread).Append(" Every record, check, asset and write call refuses until it can be opened.\n");
        sb.Append("mods:    ").Append(c.EnabledMods.Count).Append(" enabled · ").Append(c.DisabledMods.Count).Append(" disabled\n");
        sb.Append("plugins in load order: ").Append(c.OrderedPluginNames.Count).Append('\n');
        sb.Append("  active:   ").Append(gameLoaded).Append("  (").Append(checkedActive).Append(" checked + ").Append(impl).Append(" implicit masters/CC)\n");
        sb.Append("  inactive: ").Append(inactive).Append("  (present but unchecked — houseCARL excludes these)\n");
        sb.Append("resolver: ").Append(d.ResolvedPluginCount).Append(" plugins resolved to real files");
        if (d.MaxPlugins > 0) sb.Append(" [capped at MaxPlugins=").Append(d.MaxPlugins).Append(']');
        if (d.Epoch is not null) sb.Append("  epoch=").Append(d.Epoch);   // the current build's fingerprint — bulk responses stamp the build they read, matched against this
        sb.Append('\n');
        // The containment map's size, in band: it is built on every index build, so its cost is stated rather than discovered as memory.
        if (d.ModsFolderUnreadable is null) sb.Append("containment: ").Append(d.ContainedRecordCount.ToString("N0", System.Globalization.CultureInfo.InvariantCulture))
          .Append(" child record(s) mapped to a containing record (the '*parent' step reads this)\n");
        if (d.ProfileChanged)
            sb.Append("[!] the profile changed mid-call and a refresh is still pending — houseCARL re-reads it " +
                      "automatically on the next tool call (lazy refresh; no restart needed).\n");

        // profile= renders before the filter branch, which returns early: the two compose, since filter verdicts the active profile while this inspects another.
        if (profiles.RequestedName is not null) AppendNamedProfile(sb, profiles, cap);

        if (filter is { Length: > 0 })
        {
            AppendLookup(sb, c, d.ExcludedPlugins, filter.Trim(), facts, d.ModsFolderUnreadable is not null, lookups, cap);
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

    /// <summary>Where papyrus_logs and crash_logs resolve — saved, auto-detected, or unset — without persisting, since a read-only status call must mutate nothing.</summary>
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

    /// <summary>The log-folders section: where the Papyrus script-log and SKSE crash-log directories resolve, rendered before the cap-bounded name lists so they can never be truncated away.</summary>
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

    /// <summary>Plugins dropped from the index this build — unopenable, or carrying a record Mutagen cannot parse; rendered before the routine name lists so a long modlist cannot truncate it away.</summary>
    static void AppendExcluded(StringBuilder sb, IReadOnlyDictionary<string, string> excluded, int cap)
    {
        if (excluded.Count == 0) return;
        sb.Append("\n[!] EXCLUDED plugins (").Append(excluded.Count)
          .Append(") — dropped from the index this session; houseCARL does NOT read these, so any record they override is answered without their version:\n");
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
            if (sb.Length >= cap) { sb.Append("  ").Append(CutNotice(shown, names.Count)).Append('\n'); break; }
            sb.Append("  - ").Append(n).Append('\n'); shown++;
        }
    }

    /// <summary>The notice a name list cut at max_chars ends with.</summary>
    static string CutNotice(int shown, int total) => $"... [showing {shown} of {total}; raise max_chars to see all]";

    /// <summary>Render the requested named profile's composition, or a refusal: explicit-paths mode has no profiles folder, and a name matching none lists the real options. A match says plainly that the active profile is unchanged.</summary>
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
        // Any read note, e.g. a missing modlist.txt, so a zero-enabled-mods inspection is not mistaken for an empty profile.
        foreach (var warn in p.Warnings)
            sb.Append("  [!] ").Append(warn).Append('\n');
        AppendList(sb, "  disabled mods", c.DisabledMods, cap);
        AppendList(sb, "  inactive plugins", c.InactivePluginNames, cap);
    }

    /// <summary>The available profile names, so the inactive-profile read is discoverable; suppressed in explicit-paths mode and when profile= was asked for, which lists them on a miss.</summary>
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
                             IReadOnlyDictionary<string, string> excluded, string name,
                             PluginFacts? facts, bool modsUnreadable, FilterLookups? lookups, int cap)
    {
        sb.Append("\nfilter '").Append(name).Append("':\n");

        // modMiss and pluginMiss must stay in sync with the not-found arm of their ternary below — each is the negation of every hit case, and they gate the "did you mean".
        bool modMiss = !Contains(c.EnabledMods, name) && !Contains(c.DisabledMods, name);
        string asMod =
            Contains(c.EnabledMods, name)  ? "ENABLED (mod present + switched on)" :
            Contains(c.DisabledMods, name) ? "DISABLED (mod present but switched OFF — houseCARL excludes it)" :
                                             "not found in modlist.txt (not a managed mod folder name, or a UI separator)";
        sb.Append("  as a mod:    ").Append(asMod);
        if (lookups?.ModNexusId > 0) sb.Append(" · Nexus id ").Append(lookups.ModNexusId);
        else if (lookups is { ModNexusId: null }) sb.Append(" · Nexus id unknown — its meta.ini could not be read");
        // A near-miss is an easy slip, so point at the nearest real mod folders across both the enabled and disabled lists.
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
        // The common slip is the mod-folder name passed where the .esp filename was wanted; the suggester turns that into the plugin filename.
        if (pluginMiss) sb.Append(HousecarlCore.PluginNameSuggest.DidYouMean(name, c.OrderedPluginNames));
        sb.Append('\n');
        if (lookups?.NexusId > 0) AppendNexusFolders(sb, c, lookups, cap);

        // An active plugin can still be excluded from the index, so the ACTIVE line above must not be taken as the whole truth.
        if (excluded.TryGetValue(name, out var why))
            sb.Append("  [!] EXCLUDED this session: ").Append(why).Append("\n      → houseCARL does NOT read this plugin, so any record it overrides is answered without its version.\n");

        // No mod folder was read, so no copy can be placed: say so rather than "no folder holds it".
        if (modsUnreadable && !pluginMiss)
            sb.Append("  served from: unknown — houseCARL could not open the mods folder, so which folder serves this plugin cannot be read\n");

        // A name the profile lists as a plugin: its copies, then the header facts and the LOCALIZED flag the in-place write lanes refuse on.
        // A plugin's dependents line: here, ahead of the copies, when no header was read, else under its masters line (docs/architecture/mo2-instance.md).
        bool isPlugin = HousecarlCore.PluginFile.IsPlugin(name);
        if (facts?.Header is null && isPlugin) AppendDependents(sb, lookups?.Dependents, cap);
        if (facts is null) return;
        AppendCopies(sb, facts.Copies, facts.Unopened);
        if (facts.Moved is { } m)
            sb.Append("  index:       had the copy in ").Append(m.Indexed)
              .Append(m.NotRefreshed is { } held ? $"; not refreshed: {held}\n" : "; refreshed now, so later calls read the served copy\n");
        if (facts.Header is { } h)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            // A .esm or .esl loads as a master, and a .esl loads light, whatever the header bits say.
            var ext = System.IO.Path.GetExtension(name).ToLowerInvariant();
            string Bit(bool set, bool byExt, string what) => set ? "YES" : byExt ? "no, " + what + " by " + ext + " extension" : "no";
            sb.Append("  header:      ");
            // Nothing serves the name, so the lone copy read is named: its facts are that file's, not a loaded plugin's.
            if (facts.HeaderFrom is { Served: not OutputLocations.ServedStanding.Serves } from)
                sb.Append("from ").Append(from.Where).Append(", not served · ");
            sb.Append("master flag ").Append(Bit(h.Master, ext is ".esm" or ".esl", "master"))
              .Append(" · ESL flag ").Append(Bit(h.Light, ext == ".esl", "light"))
              .Append(" · ").Append(h.RecordCount.ToString("N0", inv)).Append(" records (HEDR)\n");
            sb.Append("  masters (").Append(h.Masters.Count).Append("): ").Append(h.Masters.Count == 0 ? "none" : string.Join(", ", h.Masters)).Append('\n');
            if (isPlugin) AppendDependents(sb, lookups?.Dependents, cap);
        }
        sb.Append("  localized:   ").Append(facts.Localized switch
        {
            HousecarlCore.LocalizedFlagRead.Localized =>
                "YES (header flag set) — its text lives in separate .STRINGS files, not in the plugin. An IN-PLACE " +
                "write to it is refused; write to a new plugin instead.",
            HousecarlCore.LocalizedFlagRead.NotLocalized =>
                "no (header flag clear) — its text is inside the plugin.",
            _ when facts.HeaderFrom is not null =>
                "UNKNOWN — houseCARL could not read this plugin's header, so neither answer is established.",
            _ => "UNKNOWN — " + (facts.Unopened.Count > 0 ? "no folder houseCARL could open holds it"
                                : facts.Copies.Count == 0 ? "no file of this name" : "several copies and none served") +
                 ", so no header was read.",
        }).Append('\n');
    }

    /// <summary>The mod folders whose meta.ini carries the Nexus id the filter reads as, each with its state in the profile.</summary>
    static void AppendNexusFolders(StringBuilder sb, HousecarlCore.Mo2Composition c, FilterLookups l, int cap)
    {
        sb.Append("  as a Nexus id: ");
        if (l.NexusFolders is not { } folders)
        { sb.Append("unknown — houseCARL could not open the mods folder, so no meta.ini was read\n"); return; }
        if (folders.Count == 0) sb.Append("no mod folder's meta.ini carries it");
        else
        {
            string State(string f) => Contains(c.EnabledMods, f) ? "enabled" : Contains(c.DisabledMods, f) ? "disabled" : "not in modlist.txt";
            sb.Append(folders.Count).Append(folders.Count == 1 ? " mod folder: " : " mod folders: ");
            AppendJoined(sb, folders.Select(f => f + " (" + State(f) + ")").ToList(), cap);
        }
        sb.Append('\n');
        if (l.UnreadMeta.Count > 0)
            sb.Append("  [!] ").Append(l.UnreadMeta.Count).Append(" folder(s) not read (a meta.ini that would not read, or a folder that would not open), so a folder carrying this id may be missing: ")
              .Append(HousecarlCore.Mo2LoadOrder.Named(l.UnreadMeta)).Append('\n');
    }

    /// <summary>The plugins whose header names this one as a master: the loaded ones in load order, then copies on disk the game does not load, marked.</summary>
    static void AppendDependents(StringBuilder sb, HousecarlCore.MasterDependentsResult? d, int cap)
    {
        sb.Append("  named as a master by");
        if (d is null) { sb.Append(": unknown — houseCARL could not open the mods folder, so no plugin header was read\n"); return; }
        if (d.InOrder.Count + d.OnDisk.Count == 0) sb.Append(": none, in the order or on disk");
        else
        {
            sb.Append(" (").Append(d.InOrder.Count).Append(" in the order");
            if (d.OnDisk.Count > 0) sb.Append(" · ").Append(d.OnDisk.Count).Append(" on disk, not loaded");
            sb.Append("): ");
            AppendJoined(sb, d.InOrder.Concat(d.OnDisk.Select(x => x.Plugin + " [not loaded: " + x.Where + (x.Why is null ? "" : ", " + x.Why) + "]")).ToList(), cap);
        }
        sb.Append('\n');
        if (d.UnopenedFolders.Count + d.UnreadHeaders.Count > 0)
        {
            sb.Append("  [!] not read, so a plugin naming it there is missing above:");
            if (d.UnopenedFolders.Count > 0) sb.Append(' ').Append(d.UnopenedFolders.Count).Append(" folder(s) would not open (").Append(HousecarlCore.Mo2LoadOrder.Named(d.UnopenedFolders)).Append(')');
            if (d.UnreadHeaders.Count > 0) sb.Append(d.UnopenedFolders.Count > 0 ? ";" : "").Append(' ').Append(d.UnreadHeaders.Count).Append(" header(s) would not read (").Append(string.Join("; ", d.UnreadHeaders)).Append(')');
            sb.Append('\n');
        }
    }

    /// <summary>Comma-joined names, cut at <paramref name="cap"/> with an explicit notice.</summary>
    static void AppendJoined(StringBuilder sb, IReadOnlyList<string> names, int cap)
    {
        for (int i = 0; i < names.Count; i++)
        {
            if (sb.Length >= cap) { sb.Append(' ').Append(CutNotice(i, names.Count)); return; }
            if (i > 0) sb.Append(", ");
            sb.Append(names[i]);
        }
    }

    /// <summary>Which folder serves the plugin, then every other same-named copy with its layer state and size.</summary>
    static void AppendCopies(StringBuilder sb, IReadOnlyList<PluginCopy> copies, IReadOnlyList<string> unopened)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string Line(PluginCopy x) => x.Where + " · " + (x.Bytes is { } b ? b.ToString("N0", inv) + " bytes" : "size unreadable")
                                     + (x.Served == OutputLocations.ServedStanding.Shadowed ? " · shadowed" : "");
        var served = copies.FirstOrDefault(x => x.Served == OutputLocations.ServedStanding.Serves);
        sb.Append("  served from: ").Append(served is null
            ? (unopened.Count > 0 ? $"unknown — {unopened.Count} mod folder(s) could not be opened ({HousecarlCore.Mo2LoadOrder.Named(unopened)}), and it may be in one of them"
               : copies.Count == 0 ? "none — no folder holds a file of this name" : "none — no enabled folder holds this file")
            : Line(served)).Append('\n');
        foreach (var x in copies)
            if (x != served) sb.Append("  also in:     ").Append(Line(x)).Append('\n');
    }

    internal static bool Contains(IReadOnlyList<string> list, string name)
    {
        foreach (var s in list) if (string.Equals(s, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}

/// <summary>What a filter value reads as beyond a mod and a plugin, read fresh per call: the named mod folder's Nexus id (0 for none, null when its meta.ini will not read), the value as a Nexus id (0 when it is not a positive whole number, MO2's rule) and the folders carrying it (null when the mods folder will not list), and, for a plugin filename, the plugins naming it as a master (null when the mods folder will not open).</summary>
public sealed record FilterLookups(int? ModNexusId, int NexusId, IReadOnlyList<string>? NexusFolders, IReadOnlyList<string> UnreadMeta,
                                   HousecarlCore.MasterDependentsResult? Dependents)
{
    public static FilterLookups Read(string name, LoadOrderStatusData d)
    {
        var c = d.Composition;
        bool readable = d.ModsFolderUnreadable is null;
        bool isMod = StatusWire.Contains(c.EnabledMods, name) || StatusWire.Contains(c.DisabledMods, name);
        int? modId = readable && isMod && d.ModsDir.Length > 0 ? HousecarlCore.Mo2ModMeta.ModIdOf(HousecarlCore.LooseListings.OnDisk(d.ModsDir, name)) : 0;
        int nexusId = int.TryParse(name, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) && id > 0 ? id : 0;
        var unread = new List<string>();
        var folders = nexusId > 0 && readable ? HousecarlCore.Mo2ModMeta.FoldersWithModId(d.ModsDir, nexusId, unread) : null;
        var dependents = readable && HousecarlCore.PluginFile.IsPlugin(name)
            ? HousecarlCore.MasterDependents.Find(c, d.ModsDir, d.DataDir, d.OverwriteDir, name) : null;
        return new FilterLookups(modId, nexusId, folders, unread, dependents);
    }
}

/// <summary>One external log folder for the status surface: its wire key, where it resolved, and how; the .log files at <see cref="Path"/> are read directly, logs being the one dependency with no wrapping tool.</summary>
public sealed record LogFolderView(string Key, string? Path, ToolPathSource Source);
