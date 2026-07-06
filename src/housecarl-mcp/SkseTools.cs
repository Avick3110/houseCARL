using System.ComponentModel;
using System.Text;
using HousecarlCore;
using ModelContextProtocol.Server;

namespace HousecarlMcp;

/// <summary>
/// houseCARL diagnostic tool (SKSE-plugin-layer visibility, gap 2026-06-08). Read-only. Brings the SKSE-plugin layer —
/// invisible to every record/asset tool before this — into view: the FULL DEPTH of Data\SKSE\Plugins — the <c>.dll</c>
/// plugins, and every <c>.toml</c>/<c>.ini</c>/<c>.json</c> config beneath it grouped by its real subfolder — WHICH MOD
/// wins the VFS for each (tier A), and each plugin's STATICALLY declared manifest — name/author/version, Address Library
/// vs version-LOCKED, target runtimes, XSE floor (tier C). It reads what a DLL DECLARES about itself (the SKSE loader's own
/// <c>SKSEPlugin_Version</c> data blob), never what it DOES at runtime — tier E (DLL behavior) is the honest ceiling.
/// Full visibility, compact by default: everything is accounted for (deep configs rolled into their folder-groups, other
/// content counted); <c>filter=</c> expands any group or plugin. See <see cref="SksePluginReader"/>.
/// </summary>
[McpServerToolType]
public static class SkseTools
{
    [McpServerTool(Name = "housecarl_skse_inventory", ReadOnly = true, Title = "SKSE plugin layer (DLLs, configs, provider & static metadata)"),
     Description(
         "Inventory the SKSE-plugin layer of the ACTIVE load order — the layer houseCARL's record/asset tools are otherwise " +
         "blind to. Covers the FULL depth of Data\\SKSE\\Plugins: the .dll plugins and every .ini/.toml/.json/.yaml config " +
         "beneath, each with the MOD that wins the VFS for it. Configs are grouped by their real subfolder (SkyPatcher, " +
         "DynamicStringDistributor, OStim, … — derived from the actual tree, never a hardcoded list), so the default stays " +
         "compact while accounting for everything; non-config content is counted, never dropped. For every modern plugin it " +
         "also reads the STATIC manifest the SKSE loader itself reads — name, author, version, whether it uses Address Library " +
         "(version-independent) or is LOCKED to specific game runtimes, and the XSE floor — by parsing the DLL's " +
         "SKSEPlugin_Version data export WITHOUT loading or running it. Leads with the diagnostics that matter: version-LOCKED " +
         "plugins (won't load on a mismatched game version), legacy query-only plugins (metadata set at runtime — not statically " +
         "readable), non-plugin DLLs (bundled dependencies), subfolder DLLs (not on SKSE's loader path), and any DLL contested " +
         "by more than one mod. Pass filter= a plugin/mod/DLL name, author, or config FOLDER (e.g. 'SkyPatcher', 'EngineFixes', " +
         "'po3', 'OStim') to expand that group or see a plugin in full (all flags, compatible runtimes, email, providers, " +
         "configs). It does NOT read DLL behavior (that's the ceiling), and it does NOT cover distributor INIs (SPID *_DISTR, " +
         "KID *_KID) — those live in Data\\ root and are owned by the spid-authoring / kid-authoring skills. Read-only.")]
    public static string SkseInventory(
        LoadOrderService svc,
        [Description("Optional. A plugin name, author, DLL filename, providing-mod, or config-FOLDER substring (case-insensitive). " +
            "Expands the matching config folder to its individual files, and shows full per-plugin detail for a matching DLL. " +
            "Omit for the whole-layer overview.")]
            string? filter = null,
        [Description("Optional. Max characters before lists are cut with an explicit notice. 0 = the server default (~80k).")]
            int max_chars = 0) => Guard.Tool("housecarl_skse_inventory", () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        var data = svc.SkseInventory();
        return SkseInventoryWire.Render(data, filter, max_chars > 0 ? max_chars : 80_000);
    });
}

/// <summary>Renders <see cref="SkseInventoryData"/> as compact, scannable text. Default: summary + compat, the diagnostic
/// subsets FULLY (version-locked / legacy / non-plugin / subfolder / contested), the top-level plugin roster (terse), then
/// the config folders GROUPED (count + providers) — everything accounted for, bounded by max_chars with an explicit cut
/// notice (Q3 — never silent truncation). filter= expands a group to its individual configs, or a plugin to full detail.</summary>
static class SkseInventoryWire
{
    public static string Render(SkseInventoryData d, string? filter, int cap)
    {
        if (filter is { Length: > 0 }) return RenderFiltered(d, filter.Trim(), cap);

        var sb = new StringBuilder();
        // DLLs split by SKSE-loader scope: top-level DLLs are what SKSE loads; subfolder DLLs are seen but not loader-scoped.
        var loaded = d.Dlls.Where(e => e.Group.Length == 0).ToList();
        var subfolder = d.Dlls.Where(e => e.Group.Length > 0).ToList();
        var modern = loaded.Where(e => e.Plugin is { Kind: SksePluginReader.SksePluginKind.Modern }).ToList();
        var legacy = loaded.Where(e => e.Plugin is { Kind: SksePluginReader.SksePluginKind.LegacyQuery }).ToList();
        var notPlugin = loaded.Where(e => e.Plugin is { Kind: SksePluginReader.SksePluginKind.NotSkse }).ToList();
        var unreadable = loaded.Where(e => e.Plugin is { Kind: SksePluginReader.SksePluginKind.Unreadable }).ToList();
        var bsaOnly = loaded.Where(e => e.Plugin is null).ToList();

        int addrLib = modern.Count(e => e.Plugin!.Version!.UsesAddressLibrary);
        int sig = modern.Count(e => e.Plugin!.Version!.UsesSignatureScanning);
        var locked = modern.Where(e => !e.Plugin!.Version!.VersionIndependent).ToList();
        int groupCount = d.Configs.Select(e => e.Group).Distinct(StringComparer.OrdinalIgnoreCase).Count();

        sb.Append("SKSE plugin layer — profile '").Append(d.ProfileName).Append("' — ")
          .Append(loaded.Count).Append(" DLL(s), ").Append(d.Configs.Count).Append(" config(s) across ")
          .Append(groupCount).Append(" folder(s)");
        if (d.OtherFileCount > 0) sb.Append(", ").Append(d.OtherFileCount).Append(" other file(s)");
        sb.Append(" (full depth of SKSE\\Plugins)\n");
        sb.Append("plugins: ").Append(modern.Count).Append(" with static metadata");
        if (legacy.Count > 0) sb.Append(" · ").Append(legacy.Count).Append(" legacy query-only");
        if (notPlugin.Count > 0) sb.Append(" · ").Append(notPlugin.Count).Append(" non-plugin (bundled deps)");
        if (bsaOnly.Count > 0) sb.Append(" · ").Append(bsaOnly.Count).Append(" BSA-only/unresolved");
        if (unreadable.Count > 0) sb.Append(" · ").Append(unreadable.Count).Append(" unreadable");
        if (subfolder.Count > 0) sb.Append(" · ").Append(subfolder.Count).Append(" in subfolders (not loader-scoped)");
        sb.Append('\n');
        sb.Append("compat: ").Append(addrLib).Append(" Address Library · ").Append(sig).Append(" signature-scanning · ")
          .Append(locked.Count).Append(" version-LOCKED\n");

        // ── Diagnostic subsets, FIRST and in full (the point of the tool). ──
        if (locked.Count > 0)
        {
            var distinctRuntimes = locked.SelectMany(e => e.Plugin!.Version!.CompatibleVersions)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            sb.Append("\n[!] version-LOCKED plugins (").Append(locked.Count)
              .Append(") — load ONLY on their listed runtime(s); a mismatch with your game version = won't load:\n");
            AppendCapped(sb, locked, cap, e =>
            {
                var v = e.Plugin!.Version!;
                string rt = v.CompatibleVersions.Count > 0 ? string.Join(", ", v.CompatibleVersions) : "(none listed!)";
                return $"  - {e.FileName} → {rt}   [\"{v.Name}\"{Provider(e)}]";
            });
            if (distinctRuntimes.Count > 1)
                sb.Append("      ↑ these target DIFFERENT runtimes (").Append(string.Join(", ", distinctRuntimes))
                  .Append(") — verify each matches your game version.\n");
        }

        AppendSubset(sb, "legacy query-only (SE/VR-era — metadata set at runtime, not statically readable)", legacy, cap,
            e => $"  - {e.FileName}{Provider(e)}");
        AppendSubset(sb, "non-plugin DLLs (no SKSE export — a bundled dependency, not a plugin)", notPlugin, cap,
            e => $"  - {e.FileName}{Provider(e)}");
        AppendSubset(sb, "subfolder DLLs (present but NOT on SKSE's loader path — bundled/parent-loaded, not plugins SKSE loads)", subfolder, cap,
            e => $"  - {e.Group}\\{e.FileName}{Provider(e)}");
        AppendSubset(sb, "BSA-only / unresolved DLLs (SKSE loads loose DLLs only — these will NOT load)", bsaOnly, cap,
            e => $"  - {e.FileName}{Provider(e)}  — {e.Note}");
        AppendSubset(sb, "unreadable DLLs (not a valid PE image)", unreadable, cap,
            e => $"  - {e.FileName}{Provider(e)}  — {e.Plugin?.Note}");

        var contested = loaded.Where(e => e.ProviderCount > 1).ToList();
        AppendSubset(sb, "contested DLLs (shipped by >1 mod — winner-first conflict chain; verify the winner is the one you want)", contested, cap,
            e => $"  - {e.FileName}: {Chain(e)}");

        // ── Plugin roster (the loaded, metadata-bearing plugins) — terse. ──
        sb.Append("\nplugins with metadata (").Append(modern.Count).Append(") — name · version · compat · winning mod:\n");
        AppendCapped(sb, modern.OrderBy(e => e.FileName, StringComparer.OrdinalIgnoreCase).ToList(), cap, e =>
        {
            var v = e.Plugin!.Version!;
            return $"  - {e.FileName}  \"{v.Name}\" v{v.PluginVersion}  {CompatTag(v)}{Provider(e)}";
        });

        // ── Config FOLDERS — grouped by the derived subfolder, sorted by size. Everything accounted for, compactly. ──
        if (d.Configs.Count > 0)
        {
            var groups = d.Configs.GroupBy(e => e.Group, StringComparer.OrdinalIgnoreCase)
                .Select(g => (Name: g.Key.Length == 0 ? "(top level)" : g.Key, Count: g.Count(),
                              Providers: g.Select(e => e.WinningProvider).Where(p => p is not null).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                              Contested: g.Count(e => e.ProviderCount > 1)))
                .OrderByDescending(g => g.Count).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
            sb.Append("\nconfig folders (").Append(groups.Count).Append(") — folder: files ← provider(s):\n");
            int shown = 0;
            foreach (var g in groups)
            {
                if (sb.Length >= cap) { sb.Append("  ... [showing ").Append(shown).Append(" of ").Append(groups.Count).Append(" folders; raise max_chars]\n"); break; }
                string prov = g.Providers.Count switch
                {
                    0 => "(no active provider)",
                    <= 2 => string.Join(", ", g.Providers),
                    _ => $"{g.Providers.Count} mods",
                };
                sb.Append("  - ").Append(g.Name).Append(": ").Append(g.Count).Append(" ← ").Append(prov);
                if (g.Contested > 0) sb.Append("  [").Append(g.Contested).Append(" contested]");
                sb.Append('\n'); shown++;
            }
        }

        sb.Append("(scope: full depth of Data\\SKSE\\Plugins. DLLs are top-level = what SKSE loads; configs at any depth are " +
                  "grouped by folder above. Non-config content (animation/mesh/etc.) is counted in the 'other file(s)' total.)\n");
        AppendCaveats(sb, d);
        sb.Append("\n→ filter='<plugin/mod/DLL name>' for a plugin's full detail, or filter='<folder>' (e.g. SkyPatcher, OStim) to list a config group.");
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>filter= : full detail for every matching DLL, then every matching config (by folder, filename, or provider) —
    /// so a folder name expands its group, a plugin name shows the manifest, a mod name shows everything it provides.</summary>
    static string RenderFiltered(SkseInventoryData d, string filter, int cap)
    {
        bool In(string? s) => s is not null && s.Contains(filter, StringComparison.OrdinalIgnoreCase);
        bool MatchDll(SkseFileEntry e) => In(e.FileName) || In(e.WinningProvider) || In(e.Group)
            || (e.Plugin?.Version is { } v && (In(v.Name) || In(v.Author)));
        bool MatchCfg(SkseFileEntry e) => In(e.FileName) || In(e.WinningProvider) || In(e.Group);

        var dllHits = d.Dlls.Where(MatchDll).OrderBy(e => e.FileName, StringComparer.OrdinalIgnoreCase).ToList();
        var cfgHits = d.Configs.Where(MatchCfg).ToList();

        var sb = new StringBuilder();
        sb.Append("SKSE plugin layer — filter '").Append(filter).Append("' — ")
          .Append(dllHits.Count).Append(" DLL + ").Append(cfgHits.Count).Append(" config match(es) [profile '").Append(d.ProfileName).Append("']\n");

        if (dllHits.Count == 0 && cfgHits.Count == 0)
        {
            sb.Append("\nnothing under SKSE\\Plugins matched. ")
              .Append(HousecarlCore.PluginNameSuggest.DidYouMean(filter,
                  d.Dlls.Select(e => e.FileName).Concat(d.Configs.Select(e => e.Group).Where(g => g.Length > 0)).Distinct()));
            return sb.ToString().TrimEnd('\n');
        }

        var shownCfg = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in dllHits)
        {
            if (sb.Length >= cap) { sb.Append("\n  ... [remaining DLL matches omitted at max_chars=").Append(cap).Append("]\n"); break; }
            AppendDetail(sb, e, d, shownCfg);
        }

        // Remaining matching configs (not already shown as a DLL's paired config), grouped by folder.
        var rest = cfgHits.Where(e => !shownCfg.Contains(e.RelPath))
            .OrderBy(e => e.Group, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.FileName, StringComparer.OrdinalIgnoreCase).ToList();
        if (rest.Count > 0)
        {
            sb.Append("\nmatching configs (").Append(rest.Count).Append("):\n");
            string? curGroup = null;
            int shown = 0;
            foreach (var e in rest)
            {
                if (sb.Length >= cap) { sb.Append("  ... [showing ").Append(shown).Append(" of ").Append(rest.Count).Append("; raise max_chars]\n"); break; }
                string g = e.Group.Length == 0 ? "(top level)" : e.Group;
                if (g != curGroup) { sb.Append("  ").Append(g).Append(":\n"); curGroup = g; }
                sb.Append("    - ").Append(e.FileName);
                if (e.ProviderCount > 1) sb.Append(": ").Append(Chain(e));   // contested config → the full winner→loser chain
                else sb.Append(Provider(e));
                sb.Append('\n'); shown++;
            }
        }
        return sb.ToString().TrimEnd('\n');
    }

    static void AppendDetail(StringBuilder sb, SkseFileEntry e, SkseInventoryData d, HashSet<string> shownCfg)
    {
        sb.Append('\n').Append(e.Group.Length > 0 ? e.Group + "\\" : "").Append(e.FileName).Append("  ← ")
          .Append(e.WinningProvider ?? "(no active provider)").Append(" (").Append(e.ProviderKind).Append(")\n");
        if (e.ProviderCount > 1)
            sb.Append("  [!] contested by ").Append(e.ProviderCount).Append(" mods — full chain (winner first): ").Append(Chain(e)).Append('\n');

        var p = e.Plugin;
        // Service-level note (subfolder-not-loader-scoped / no active provider / BSA-only) — shown for ANY kind, not just
        // Modern (fix: a bundled-dependency or unreadable DLL in a subfolder also deserves the loader-path flag).
        if (e.Note is { } enote) sb.Append("  [!] ").Append(enote).Append('\n');
        if (p is null) { if (e.Note is null) sb.Append("  no static metadata\n"); return; }

        switch (p.Kind)
        {
            case SksePluginReader.SksePluginKind.LegacyQuery:
            case SksePluginReader.SksePluginKind.NotSkse:
            case SksePluginReader.SksePluginKind.Unreadable:
                sb.Append("  ").Append(p.Note).Append('\n');
                if (p.Is64Bit == false) sb.Append("  [!] NOT an x64 image — a 32-bit DLL cannot load in Skyrim SE/AE.\n");
                return;   // Is64Bit == false is EXPLICITLY-determined non-x64; null (unknown) never triggers the claim (finding #1)
        }

        var v = p.Version!;
        sb.Append("  \"").Append(v.Name).Append("\" by ").Append(v.Author.Length > 0 ? v.Author : "(no author)");
        if (v.SupportEmail.Length > 0) sb.Append(" <").Append(v.SupportEmail).Append('>');
        sb.Append("\n  version ").Append(v.PluginVersion).Append('\n');
        if (p.Is64Bit == false) sb.Append("  [!] NOT an x64 image — a 32-bit DLL cannot load in Skyrim SE/AE.\n");

        if (v.VersionIndependent)
        {
            var how = new List<string>();
            if (v.UsesAddressLibrary) how.Add("Address Library");
            if (v.UsesSignatureScanning) how.Add("signature scanning");
            sb.Append("  runtime compat: version-INDEPENDENT via ").Append(string.Join(" + ", how))
              .Append(" — loads on any supported game runtime");
            if (v.UsesAddressLibrary) sb.Append(" (needs the Address Library for SKSE Plugins mod installed)");
            sb.Append('\n');
        }
        else
        {
            string rt = v.CompatibleVersions.Count > 0 ? string.Join(", ", v.CompatibleVersions) : "(none listed — will refuse every runtime!)";
            sb.Append("  runtime compat: version-LOCKED → loads ONLY on ").Append(rt)
              .Append("  [!] a game version outside this list = won't load\n");
        }
        var structs = new List<string>();
        if (v.UsesUpdatedStructs) structs.Add("post-1.6.629 structs");
        if (v.DeclaresNoStructs) structs.Add("no CommonLib structs");
        if (structs.Count > 0) sb.Append("  struct compat: ").Append(string.Join(", ", structs)).Append('\n');
        if (v.MinimumXseVersion is { } xse) sb.Append("  requires SKSE ≥ ").Append(xse).Append('\n');

        // Paired configs: a config anywhere under SKSE\Plugins whose basename stem matches the DLL (best-effort association).
        string stem = System.IO.Path.GetFileNameWithoutExtension(e.FileName);
        var cfgs = d.Configs.Where(c => System.IO.Path.GetFileNameWithoutExtension(c.FileName)
            .StartsWith(stem, StringComparison.OrdinalIgnoreCase)).ToList();
        if (cfgs.Count > 0)
        {
            sb.Append("  configs: ").Append(string.Join(", ", cfgs.Select(c =>
                (c.Group.Length > 0 ? c.Group + "\\" : "") + c.FileName + Provider(c)))).Append('\n');
            foreach (var c in cfgs) shownCfg.Add(c.RelPath);
        }
    }

    /// <summary>The compat one-word tag for the terse roster: "AddrLib", "SigScan", or "LOCKED→[runtimes]".</summary>
    static string CompatTag(SksePluginReader.SkseVersionInfo v)
    {
        if (v.UsesAddressLibrary) return "AddrLib";
        if (v.UsesSignatureScanning) return "SigScan";
        return "LOCKED→" + (v.CompatibleVersions.Count > 0 ? string.Join("/", v.CompatibleVersions) : "?");
    }

    static string Provider(SkseFileEntry e) =>
        e.WinningProvider is null ? "  (no active provider)" : $"  ← {e.WinningProvider}";

    /// <summary>The full VFS conflict chain, winner FIRST then losers in precedence order, each tagged loose/BSA — the
    /// asset-tool conflict transparency (which mod wins this file, and who it overrides). "(no active provider)" when empty.</summary>
    static string Chain(SkseFileEntry e) =>
        e.Providers.Count == 0 ? "(no active provider)" : string.Join(" › ", e.Providers.Select(p => $"{p.Name} ({p.Kind})"));

    static void AppendSubset(StringBuilder sb, string label, IReadOnlyList<SkseFileEntry> items, int cap, Func<SkseFileEntry, string> line)
    {
        if (items.Count == 0) return;
        sb.Append('\n').Append(label).Append(" (").Append(items.Count).Append("):\n");
        AppendCapped(sb, items, cap, line);
    }

    static void AppendCapped(StringBuilder sb, IReadOnlyList<SkseFileEntry> items, int cap, Func<SkseFileEntry, string> line)
    {
        int shown = 0;
        foreach (var e in items)
        {
            if (sb.Length >= cap) { sb.Append("  ... [showing ").Append(shown).Append(" of ").Append(items.Count).Append("; raise max_chars or use filter= to see all]\n"); break; }
            sb.Append(line(e)).Append('\n'); shown++;
        }
    }

    static void AppendCaveats(StringBuilder sb, SkseInventoryData d)
    {
        if (d.ReadIncomplete)
            sb.Append("[!] a BSA failed to read this build, so a file present only in it may be missing from this inventory (Q3).\n");
        foreach (var w in d.Warnings) sb.Append("[!] ").Append(w).Append('\n');
        foreach (var f in d.BsaFailures) sb.Append("[!] archive read failure: ").Append(f).Append('\n');
    }
}
