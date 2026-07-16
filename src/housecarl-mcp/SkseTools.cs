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

    [McpServerTool(Name = "housecarl_skse_config_audit", ReadOnly = true, Title = "SKSE config references vs the load order (dead-reference audit)"),
     Description(
         "Cross-check the form references SKSE-plugin CONFIGS declare against the real records of the ACTIVE load order — so a " +
         "DEAD reference (a FormID pointing at a plugin that isn't installed, or at a record that doesn't exist in it) is caught " +
         "by houseCARL instead of by a silent in-game failure. Scans the full depth of Data\\SKSE\\Plugins for .ini/.toml/.json/" +
         ".yaml/.yml configs, reads the WINNING copy of each (the copy the DLL actually reads), and extracts every form-shaped " +
         "reference — a hex FormID paired with a plugin filename in EITHER order (0xFORM|Plugin.esp as DSD/CDF/po3 write it, " +
         "Plugin.esp|0xFORM as SkyPatcher writes it, the ~ tilde form) plus plugin-named folder gates (DynamicStringDistributor\\" +
         "Plugin.esp\\...) — then resolves each to a verdict: OK, PLUGIN MISSING (plugin not in the order), DANGLING (plugin " +
         "present but no such record), or UNPARSEABLE (a shape-matched token that can't be normalized). It is the generic, " +
         "framework-AGNOSTIC twin of the SkyPatcher reader's first half: it checks reference VALIDITY, never what a reference is " +
         "FOR (that's per-framework skill territory) and never what the DLL DOES with it (the honest ceiling). Extraction is a " +
         "heuristic over token SHAPES, so a token in a comment or disabled block still surfaces — the framing is 'references this " +
         "file DECLARES', not 'references the DLL will use'. 'No references found' is the most common per-file outcome and is " +
         "accounted for, never a warning. Bare EditorID / name strings are NOT validated (Wave 2). Pass filter= a folder, mod, " +
         "filename, or referenced-plugin substring to audit just that group and list EVERY reference with its verdict (the OKs " +
         "included — positive confirmation of a patch you just authored). Distributor INIs in Data\\ root (SPID *_DISTR, KID " +
         "*_KID) are out of scope — owned by their authoring skills. Read-only.")]
    public static string SkseConfigAudit(
        LoadOrderService svc,
        [Description("Optional. A config FOLDER, providing-mod, filename, or REFERENCED-plugin substring (case-insensitive). " +
            "Audits just the matching configs and lists every reference with its verdict, OKs included. Omit for the whole-layer " +
            "audit (diagnostics — dead references — first, then the accounted-for remainder).")]
            string? filter = null,
        [Description("Optional. Max characters before lists are cut with an explicit notice. 0 = the server default (~80k).")]
            int max_chars = 0) => Guard.Tool("housecarl_skse_config_audit", () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        var data = svc.SkseConfigAudit();
        return SkseConfigAuditWire.Render(data, filter, max_chars > 0 ? max_chars : 80_000);
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

/// <summary>Renders <see cref="SkseConfigAuditData"/> as compact, scannable text (housecarl_skse_config_audit, tier B).
/// Default: a health summary, then the DIAGNOSTICS first and in full (dead references — PLUGIN MISSING gates + tokens,
/// DANGLING, UNPARSEABLE — and read errors, each with file:line provenance and its winning provider), then the
/// accounted-for remainder (healthy files counted; no-reference files grouped by folder — the OStim bulk). Bounded by
/// max_chars with an explicit cut notice (Q3 — never silent truncation). filter= audits one group and lists EVERY
/// reference with its verdict, OKs included (the positive-confirmation / verifier role).</summary>
static class SkseConfigAuditWire
{
    // (file, ref) pair — a dead reference and where it was declared.
    readonly record struct Hit(SkseConfigFileAudit File, SkseAuditedRef Audited)
    {
        public HousecarlCore.SkseConfigRef Ref => Audited.Ref;
    }

    public static string Render(SkseConfigAuditData d, string? filter, int cap)
    {
        if (filter is { Length: > 0 }) return RenderFiltered(d, filter.Trim(), cap);

        var flat = d.Files.SelectMany(f => f.Refs.Select(r => new Hit(f, r))).ToList();
        var missingGates = flat.Where(h => h.Audited.Verdict == SkseRefVerdict.PluginMissing && h.Ref.Shape == HousecarlCore.SkseRefShape.PathSegmentGate).ToList();
        var missingToks  = flat.Where(h => h.Audited.Verdict == SkseRefVerdict.PluginMissing && h.Ref.Shape == HousecarlCore.SkseRefShape.FormToken).ToList();
        var dangling     = flat.Where(h => h.Audited.Verdict == SkseRefVerdict.Dangling).ToList();
        var unparseable  = flat.Where(h => h.Audited.Verdict == SkseRefVerdict.Unparseable).ToList();
        var readErrors   = d.Files.Where(f => f.ReadError is not null).ToList();

        int refsChecked = flat.Count;
        int dead = missingGates.Count + missingToks.Count + dangling.Count + unparseable.Count;
        int filesWithRefs = d.Files.Count(f => f.Refs.Count > 0);

        var sb = new StringBuilder();
        sb.Append("SKSE config audit — profile '").Append(d.ProfileName).Append("' — ")
          .Append(d.ConfigCount).Append(" config(s) scanned, ").Append(filesWithRefs).Append(" carry references, ")
          .Append(refsChecked).Append(" reference(s) checked\n");
        if (dead == 0)
            sb.Append("✓ every reference resolves against the active load order — no dead references found.\n");
        else
        {
            sb.Append("[!] ").Append(dead).Append(" DEAD reference(s): ")
              .Append(missingGates.Count + missingToks.Count).Append(" plugin-missing · ")
              .Append(dangling.Count).Append(" dangling · ").Append(unparseable.Count).Append(" unparseable\n");
        }

        // ── Diagnostics FIRST, in full (the point of the tool). ──
        AppendHits(sb, "PLUGIN MISSING — folder gates (the plugin isn't installed, so the WHOLE file is inert)", missingGates, cap,
            h => $"  - {h.File.RelPath}: folder '{h.Ref.Plugin}' not in the load order{Prov(h.File)}");
        // Token-level plugin-missing is GROUPED by the target plugin — a whole-layer scan yields tens of thousands of
        // individual inert refs (a config shipping optional support for a mod you don't have contributes hundreds each),
        // so a per-ref list is an unreadable wall; the count-per-plugin table is the actionable shape. filter= a plugin to
        // see its individual refs.
        if (missingToks.Count > 0)
        {
            var byPlugin = missingToks.GroupBy(h => h.Ref.Plugin, StringComparer.OrdinalIgnoreCase)
                .Select(g => (Plugin: g.Key, Refs: g.Count(),
                              Files: g.Select(h => h.File.RelPath).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                              Example: g.First().File.RelPath))
                .OrderByDescending(g => g.Refs).ThenBy(g => g.Plugin, StringComparer.OrdinalIgnoreCase).ToList();
            sb.Append("\nPLUGIN MISSING — target plugin not in the load order (inert; often a config shipping optional support for a mod you don't have) — by plugin (")
              .Append(byPlugin.Count).Append(" plugins, ").Append(missingToks.Count).Append(" refs):\n");
            int shown = 0;
            foreach (var g in byPlugin)
            {
                if (sb.Length >= cap) { sb.Append("  ... [showing ").Append(shown).Append(" of ").Append(byPlugin.Count).Append(" plugins; raise max_chars or use filter=]\n"); break; }
                sb.Append("  - ").Append(g.Plugin).Append(": ").Append(g.Refs).Append(" ref(s)");
                if (g.Files > 1) sb.Append(" across ").Append(g.Files).Append(" file(s)");
                sb.Append("  (e.g. ").Append(g.Example).Append(")\n"); shown++;
            }
        }
        AppendHits(sb, "DANGLING — plugin present but no such record (a dead reference)", dangling, cap,
            h => $"  - {Loc(h)}: '{h.Ref.Raw}' → {h.Audited.Detail}{Prov(h.File)}");
        AppendHits(sb, "UNPARSEABLE — shape-matched tokens that can't be normalized (flagged, never guessed)", unparseable, cap,
            h => $"  - {Loc(h)}: '{h.Ref.Raw}' → {h.Audited.Detail}{Prov(h.File)}");
        if (readErrors.Count > 0)
        {
            sb.Append("\nread errors — configs that could not be read/decoded (NOT counted as clean) (").Append(readErrors.Count).Append("):\n");
            int shown = 0;
            foreach (var f in readErrors)
            {
                if (sb.Length >= cap) { sb.Append("  ... [").Append(shown).Append(" of ").Append(readErrors.Count).Append("; raise max_chars]\n"); break; }
                sb.Append("  - ").Append(f.RelPath).Append(": ").Append(f.ReadError).Append(Prov(f)).Append('\n'); shown++;
            }
        }

        // ── Accounted-for remainder — everything that ISN'T a diagnostic, so nothing is silently dropped (Q3). ──
        var healthyFiles = d.Files.Where(f => f.ReadError is null && f.Refs.Count > 0 && f.Refs.All(r => r.Verdict == SkseRefVerdict.Ok)).ToList();
        int healthyRefs = healthyFiles.Sum(f => f.Refs.Count);
        int okInMixed = (refsChecked - dead) - healthyRefs;   // OK refs living in a file that ALSO has a dead ref — so every ref reconciles: refsChecked = dead + healthyRefs + okInMixed
        var noRefFiles = d.Files.Where(f => f.ReadError is null && f.Refs.Count == 0).ToList();
        sb.Append("\naccounted for: ").Append(healthyFiles.Count).Append(" file(s) with ").Append(healthyRefs)
          .Append(" reference(s) all OK");
        if (okInMixed > 0) sb.Append(" · ").Append(okInMixed).Append(" more OK ref(s) in files that also have dead references");
        sb.Append(" · ").Append(noRefFiles.Count).Append(" file(s) declare no form-shaped references\n");
        if (noRefFiles.Count > 0)
        {
            var groups = noRefFiles.GroupBy(f => f.Group, StringComparer.OrdinalIgnoreCase)
                .Select(g => (Name: g.Key.Length == 0 ? "(top level)" : g.Key, Count: g.Count()))
                .OrderByDescending(g => g.Count).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
            sb.Append("  no-reference configs by folder (").Append(groups.Count).Append("):\n");
            int shown = 0;
            foreach (var g in groups)
            {
                if (sb.Length >= cap) { sb.Append("    ... [").Append(shown).Append(" of ").Append(groups.Count).Append(" folders; raise max_chars]\n"); break; }
                sb.Append("    - ").Append(g.Name).Append(": ").Append(g.Count).Append('\n'); shown++;
            }
        }

        sb.Append("\n(scope: form-shaped references only — a hex FormID + plugin filename, or a plugin-named folder gate. Bare " +
                  "EditorID/name strings are not validated (Wave 2). Extraction is heuristic over token shapes: a token in a comment " +
                  "or disabled block still counts — 'references this file declares', not 'the DLL will use'. A folder that SHOULD carry " +
                  "references but shows none may use a reference shape not yet recognized.)\n");
        AppendCaveats(sb, d);
        sb.Append("\n→ filter='<folder/mod/filename/plugin>' to audit one group and see every reference (OKs included).");
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>filter= : audit just the matching configs (by folder, provider, filename, or a REFERENCED plugin) and list
    /// every reference with its verdict — OKs included, for positive confirmation (the SkyPatcher-reader verifier role).</summary>
    static string RenderFiltered(SkseConfigAuditData d, string filter, int cap)
    {
        bool In(string? s) => s is not null && s.Contains(filter, StringComparison.OrdinalIgnoreCase);
        bool Match(SkseConfigFileAudit f) =>
            In(f.FileName) || In(f.Group) || In(f.WinningProvider) || In(f.RelPath)
            || f.Refs.Any(r => In(r.Ref.Plugin));
        var hits = d.Files.Where(Match)
            .OrderBy(f => f.Group, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.RelPath, StringComparer.OrdinalIgnoreCase).ToList();

        var sb = new StringBuilder();
        sb.Append("SKSE config audit — filter '").Append(filter).Append("' — ")
          .Append(hits.Count).Append(" config(s) match [profile '").Append(d.ProfileName).Append("']\n");
        if (hits.Count == 0)
        {
            sb.Append("\nnothing under SKSE\\Plugins matched. ")
              .Append(HousecarlCore.PluginNameSuggest.DidYouMean(filter,
                  d.Files.Select(f => f.Group).Where(g => g.Length > 0).Concat(d.Files.Select(f => f.FileName)).Distinct()));
            return sb.ToString().TrimEnd('\n');
        }

        int shownFiles = 0;
        foreach (var f in hits)
        {
            if (sb.Length >= cap) { sb.Append("\n  ... [showing ").Append(shownFiles).Append(" of ").Append(hits.Count).Append(" files; raise max_chars]\n"); break; }
            sb.Append('\n').Append(f.RelPath).Append("  ← ").Append(f.WinningProvider ?? "(no active provider)").Append('\n');
            if (f.ProviderCount > 1)
                sb.Append("  [!] contested by ").Append(f.ProviderCount).Append(" mods (winner audited): ")
                  .Append(string.Join(" › ", f.Providers.Select(p => $"{p.Name} ({p.Kind})"))).Append('\n');
            if (f.ReadError is not null) { sb.Append("  [!] ").Append(f.ReadError).Append('\n'); continue; }
            if (f.Refs.Count == 0) { sb.Append("  (no form-shaped references)\n"); continue; }
            foreach (var r in f.Refs)
                sb.Append("  ").Append(Tag(r.Verdict)).Append(' ')
                  .Append(r.Ref.Shape == HousecarlCore.SkseRefShape.PathSegmentGate ? $"folder gate '{r.Ref.Plugin}'" : $"'{r.Ref.Raw}'")
                  .Append(r.Ref.Line > 0 ? $" (line {r.Ref.Line})" : "")
                  .Append(r.Detail is null ? "" : " → " + r.Detail).Append('\n');
            shownFiles++;
        }
        return sb.ToString().TrimEnd('\n');
    }

    static string Tag(SkseRefVerdict v) => v switch
    {
        SkseRefVerdict.Ok => "[OK]",
        SkseRefVerdict.PluginMissing => "[MISSING]",
        SkseRefVerdict.Dangling => "[DANGLING]",
        SkseRefVerdict.Unparseable => "[UNPARSEABLE]",
        _ => "[?]",
    };

    static string Loc(Hit h) => h.Ref.Line > 0 ? $"{h.File.RelPath}:{h.Ref.Line}" : h.File.RelPath;
    static string Prov(SkseConfigFileAudit f) => f.WinningProvider is null ? "" : $"  [← {f.WinningProvider}]";

    static void AppendHits(StringBuilder sb, string label, IReadOnlyList<Hit> items, int cap, Func<Hit, string> line)
    {
        if (items.Count == 0) return;
        sb.Append('\n').Append(label).Append(" (").Append(items.Count).Append("):\n");
        int shown = 0;
        foreach (var h in items)
        {
            if (sb.Length >= cap) { sb.Append("  ... [showing ").Append(shown).Append(" of ").Append(items.Count).Append("; raise max_chars or use filter=]\n"); break; }
            sb.Append(line(h)).Append('\n'); shown++;
        }
    }

    static void AppendCaveats(StringBuilder sb, SkseConfigAuditData d)
    {
        if (d.ReadIncomplete)
            sb.Append("[!] a BSA failed to read this build, so a config present only in it may be missing from this audit (Q3).\n");
        foreach (var w in d.Warnings) sb.Append("[!] ").Append(w).Append('\n');
        foreach (var f in d.BsaFailures) sb.Append("[!] archive read failure: ").Append(f).Append('\n');
    }
}
