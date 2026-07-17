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
         "configs). Pass peek=true WITH filter= to additionally read what the matching DLL's IMAGE statically contains: the " +
         "DLLs it imports (with derived flags — graphics/input hooks, network, and which sibling non-plugin DLL is bundled " +
         "for it), the config paths it embeds (which folder it actually scans), and the plugin names it embeds, each " +
         "cross-checked against your load order. Debug-build plugins are flagged WITHOUT peek — a DLL importing the debug C " +
         "runtime fails with error 126 for anyone without Visual Studio. It does NOT read DLL behavior (that's the ceiling; " +
         "an embedded string is what the image CONTAINS, never what the code DOES, and absence proves nothing), and it does " +
         "NOT cover distributor INIs (SPID *_DISTR, KID *_KID) — those live in Data\\ root and are owned by the " +
         "spid-authoring / kid-authoring skills. Read-only.")]
    public static string SkseInventory(
        LoadOrderService svc,
        [Description("Optional. A plugin name, author, DLL filename, providing-mod, or config-FOLDER substring (case-insensitive). " +
            "Expands the matching config folder to its individual files, and shows full per-plugin detail for a matching DLL. " +
            "Omit for the whole-layer overview.")]
            string? filter = null,
        [Description("Optional. Static peek at the matching DLL's image — its imports, the config paths and plugin names it " +
            "embeds. REQUIRES filter= (a peek is per-DLL: it reads whole images, and a whole-layer dump would be unreadable " +
            "noise). Use it to answer 'what does this unfamiliar DLL touch'.")]
            bool peek = false,
        [Description("Optional. Max characters before lists are cut with an explicit notice. 0 = the server default (~80k).")]
            int max_chars = 0) => Guard.Tool("housecarl_skse_inventory", () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        if (SkseInventoryWire.PeekArgError(peek, filter) is { } err) return err;
        var data = svc.SkseInventory(peek ? filter!.Trim() : null);
        return SkseInventoryWire.Render(data, filter, max_chars > 0 ? max_chars : 80_000);
    });

    [McpServerTool(Name = "housecarl_native_pairing_audit", ReadOnly = true, Title = "Native Papyrus declarations vs the DLLs that implement them (pairing audit)"),
     Description(
         "Cross-check the native Papyrus functions the ACTIVE order's compiled scripts declare against the SKSE DLLs that must " +
         "implement them — the seam where 'a mod's scripts are installed but its DLL is missing, won't load on this game version, " +
         "or is 32-bit/BSA-packed/subfolder-shipped' hides. A native function is ONE thing declared in TWO places (a .pex class " +
         "with a native-flagged function + a DLL registering the implementation at runtime); the halves ship as separate files and " +
         "fail INDEPENDENTLY, and the engine's response is a cryptic 'unable to bind' log + calls that silently no-op. Scans the " +
         "winning copy of EVERY compiled script (loose + BSA), keeps the baseline honest by construction (a class carried by an " +
         "official archive is the ENGINE's — even when SKSE's loose override wins the file; skse64's own script additions are " +
         "SKSE CORE, implemented by the game-root loader), then pairs each remaining class to the DLLs its provider mod — or a mod " +
         "in its conflict chain, the bundling case — ships under SKSE\\Plugins. Leads with the findings: PAIRED-BUT-DEAD (scripts " +
         "installed, and every candidate DLL statically will not load — wrong game runtime for a version-LOCKED plugin, BSA-only, " +
         "subfolder, 32-bit, unreadable) and UNPAIRED (no DLL in sight — a VERIFY flag, typically a declaration copy of a framework " +
         "you don't have; never called 'broken', because registration is runtime behavior — the tier-E ceiling this tool never " +
         "crosses). It answers 'is the declaration↔implementation pairing plausible and healthy', NEVER 'does the DLL register " +
         "exactly these functions'. Pass filter= a class, mod, or DLL substring for full detail — the native function names, the " +
         "pairing evidence, each candidate DLL's manifest and load verdict, the conflict chains. Read-only.")]
    public static string NativePairingAudit(
        LoadOrderService svc,
        [Description("Optional. A script CLASS name, providing-mod, paired-mod, or DLL filename substring (case-insensitive). " +
            "Shows full detail for every matching class: declared native functions, pairing rung, candidate DLL manifests and " +
            "load verdicts, conflict chains. Omit for the whole-order audit (findings first, then the accounted-for baseline).")]
            string? filter = null,
        [Description("Optional. Max characters before lists are cut with an explicit notice. 0 = the server default (~80k).")]
            int max_chars = 0) => Guard.Tool("housecarl_native_pairing_audit", () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        var data = svc.NativePairingAudit();
        return NativePairingWire.Render(data, filter, max_chars > 0 ? max_chars : 80_000);
    });

    [McpServerTool(Name = "housecarl_skse_config_audit", ReadOnly = true, Title = "SKSE config references vs the load order (reference-validity audit)"),
     Description(
         "Cross-check the form references SKSE-plugin CONFIGS declare against the real records of the ACTIVE load order — so a " +
         "BROKEN reference (a FormID pointing at a record that doesn't exist in a plugin you DO have) is caught by houseCARL " +
         "instead of by a silent in-game failure, and kept apart from a merely INERT one (a plugin you don't have installed — " +
         "usually optional support for a mod you aren't running). Scans the full depth of Data\\SKSE\\Plugins for .ini/.toml/.json/" +
         ".yaml/.yml configs, reads the WINNING copy of each (the copy the DLL actually reads), and extracts every form-shaped " +
         "reference — a hex FormID paired with a plugin filename in EITHER order (0xFORM|Plugin.esp as DSD/CDF/po3 write it, " +
         "Plugin.esp|0xFORM as SkyPatcher writes it, the ~ tilde form) plus plugin-named folder gates (DynamicStringDistributor\\" +
         "Plugin.esp\\...) — then resolves each to a verdict: OK, PLUGIN MISSING (plugin not in the order), DANGLING (plugin " +
         "present but no such record), or UNPARSEABLE (a shape-matched token that can't be normalized) — the summary groups " +
         "these as BROKEN (dangling/unparseable, actionable) vs INERT (plugin-missing, usually optional support). It is the generic, " +
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
            "audit (diagnostics — broken & inert references — first, then the accounted-for remainder).")]
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
    /// <summary>The <c>peek=</c> argument check, or null when the call is valid. A peek is per-DLL BY DESIGN (plan §3a):
    /// peeking all ~290 DLLs would read every image and render a wall that invites misreading noise as signal — the tool's
    /// central design risk. So a bare <c>peek=true</c> fails LOUD rather than silently ignoring the flag or quietly
    /// peeking one arbitrary DLL (Q3). Pure + internal so the skse-peek-guard pins it without a live service.</summary>
    internal static string? PeekArgError(bool peek, string? filter) =>
        peek && string.IsNullOrWhiteSpace(filter)
            ? "peek=true needs filter= — a peek is per-DLL, not a whole-layer dump (it reads each matching DLL's whole " +
              "image). Pass filter='<DLL/plugin/mod name>' to name the DLL to peek, e.g. filter='SkyPatcher' peek=true."
            : null;

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

        // Debug-CRT offenders lead: it is the sharpest static verdict in the layer (deterministic breakage, not a
        // mismatch to verify). Surfaced WITHOUT peek= — plan §8.3, decided on the measured data §9 asked for: the import
        // walk this needs rides the PE open every DLL's manifest read already pays for, which is noise beside the
        // inventory's per-file VFS resolve. A user should not have to suspect this to be told about it.
        var debugCrt = loaded.Where(x => x.Plugin is { Imports: not null } pl && pl.DebugCrtImports.Count > 0).ToList();
        if (debugCrt.Count > 0)
        {
            sb.Append("\n[!] DEBUG-BUILD plugins (").Append(debugCrt.Count)
              .Append(") — they import the debug C runtime, which ships only with Visual Studio and is NOT redistributable:\n");
            AppendCapped(sb, debugCrt, cap, x =>
            {
                var crt = x.Plugin!.DebugCrtImports;
                bool loadsHere = crt.All(SksePluginReader.IsSystemDllResolvable);
                string verdict = loadsHere
                    ? "  loads on THIS machine (you have the debug runtime) — but error 126 for anyone without Visual Studio"
                    : "  ≠ this machine — will NOT load (error 126: the debug runtime isn't here)";
                return $"  - {x.FileName} → needs {string.Join(", ", crt)}{verdict}{Provider(x)}";
            });
        }

        if (locked.Count > 0)
        {
            // With the installed runtime resolved this is PASS/FAIL per plugin; without it, the honest degrade is
            // the original "verify each" wording (the native-pairing audit's §4d upgrade, shared here).
            sb.Append("\n[!] version-LOCKED plugins (").Append(locked.Count)
              .Append(") — load ONLY on their listed runtime(s)");
            sb.Append(d.InstalledRuntime is { } rt0
                ? $"; installed game runtime is {rt0}:\n"
                : "; a mismatch with your game version = won't load:\n");
            AppendCapped(sb, locked, cap, e =>
            {
                var v = e.Plugin!.Version!;
                string rt = v.CompatibleVersions.Count > 0 ? string.Join(", ", v.CompatibleVersions) : "(none listed!)";
                string verdict = d.InstalledRuntime is { } inst
                    ? (SksePluginReader.RuntimeCompatible(v, inst) ? "  = your game, loads" : "  ≠ your game — will NOT load")
                    : "";
                return $"  - {e.FileName} → {rt}{verdict}   [\"{v.Name}\"{Provider(e)}]";
            });
            if (d.InstalledRuntime is null)
            {
                var distinctRuntimes = locked.SelectMany(e => e.Plugin!.Version!.CompatibleVersions)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (distinctRuntimes.Count > 1)
                    sb.Append("      ↑ these target DIFFERENT runtimes (").Append(string.Join(", ", distinctRuntimes))
                      .Append(") — verify each matches your game version (the installed version could not be resolved).\n");
            }
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
        bool MatchCfg(SkseFileEntry e) => In(e.FileName) || In(e.WinningProvider) || In(e.Group);

        // SkseFileEntry.MatchesDll is the ONE DLL predicate — the service peeks exactly the entries this view renders.
        var dllHits = d.Dlls.Where(e => e.MatchesDll(filter)).OrderBy(e => e.FileName, StringComparer.OrdinalIgnoreCase).ToList();
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
                // Tier D rides EVERY kind: a bundled dependency or an unreadable-manifest DLL still has an import table,
                // and a debug-CRT build is exactly the sort of thing that shows up as a DLL nobody can classify.
                AppendPeek(sb, e, d);
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
        AppendPeek(sb, e, d);
    }

    /// <summary>The tier-D peek block for one DLL (<c>peek=true</c>): what the IMAGE statically contains — its imports
    /// (with the derived flags), the config paths and plugin names it embeds, and the scan accounting. Renders nothing
    /// unless a peek ran. Every line here is a fact about bytes in a file, and the framing line says so: this is not what
    /// the code DOES (tier E), and absence of a string proves NOTHING — plenty of DLLs build their references at runtime.</summary>
    static void AppendPeek(StringBuilder sb, SkseFileEntry e, SkseInventoryData d)
    {
        if (e.Peek is not { } peek) return;
        sb.Append("  ── peek (what the image contains) ──\n");
        if (peek.Failed) { sb.Append("  [!] ").Append(peek.Note).Append('\n'); return; }

        // ── imports ──
        var imports = e.Plugin?.Imports;
        if (imports is null)
            sb.Append("  imports: UNKNOWN — the import directory could not be walked (corrupt or absent optional header)\n");
        else if (imports.Count == 0)
            sb.Append("  imports: none (walked, genuinely empty)\n");
        else
        {
            sb.Append("  imports (").Append(imports.Count).Append("): ").Append(string.Join(", ", imports)).Append('\n');
            var hooks = imports.Where(i => HookImports.ContainsKey(i)).ToList();
            foreach (var h in hooks) sb.Append("    → ").Append(h).Append(": ").Append(HookImports[h]).Append('\n');
            // Bundled-dependency attribution: an import satisfied by a sibling NON-plugin DLL in the same layer (the
            // msdia140.dll ← CrashLogger case) — names WHY that stray DLL is installed.
            var siblings = d.Dlls.Where(x => x.Plugin is { Kind: SksePluginReader.SksePluginKind.NotSkse })
                .Select(x => x.FileName).Where(f => imports.Contains(f, StringComparer.OrdinalIgnoreCase)).ToList();
            if (siblings.Count > 0)
                sb.Append("    → bundled with this plugin (a non-plugin DLL in this layer satisfies it): ")
                  .Append(string.Join(", ", siblings)).Append('\n');
        }
        AppendDebugCrt(sb, e);

        // ── config surface ──
        if (peek.ConfigPaths.Count > 0)
        {
            sb.Append("  config paths embedded (").Append(peek.ConfigPaths.Count).Append("):\n");
            foreach (var c in peek.ConfigPaths.Take(PeekListCap)) sb.Append("    - ").Append(c).Append('\n');
            if (peek.ConfigPaths.Count > PeekListCap)
                sb.Append("    ... [showing ").Append(PeekListCap).Append(" of ").Append(peek.ConfigPaths.Count).Append("]\n");
        }

        // ── plugin references, cross-checked ──
        if (peek.PluginRefs.Count > 0)
        {
            sb.Append("  plugin names embedded (").Append(peek.PluginRefs.Count).Append("):\n");
            foreach (var r in peek.PluginRefs.Take(PeekListCap))
            {
                string verdict = d.ActivePlugins is null ? ""
                    : d.ActivePlugins.Contains(r) ? "  (in your load order)"
                    : "  [!] NOT in your load order";
                sb.Append("    - ").Append(r).Append(verdict).Append('\n');
            }
            if (peek.PluginRefs.Count > PeekListCap)
                sb.Append("    ... [showing ").Append(PeekListCap).Append(" of ").Append(peek.PluginRefs.Count).Append("]\n");
        }

        sb.Append("  scanned ").Append(peek.RunsScanned).Append(" string run(s) over ")
          .Append(peek.BytesScanned / 1024).Append(" KB → showed ")
          .Append(peek.ConfigPaths.Count + peek.PluginRefs.Count)
          .Append(" (the classes above are a FILTER over the image, not the whole haystack)\n");
        if (peek.Note is { } note) sb.Append("  [!] ").Append(note).Append('\n');
        sb.Append("  (imports/strings are what the image CONTAINS, never what the code DOES — behavior is unreadable by " +
                  "design. Absence proves nothing: many DLLs build their references at runtime or read them from configs.)\n");
    }

    /// <summary>Max entries per peek list before an explicit cut — a peek is per-DLL and readability is the point (§6's
    /// design risk: noise misread as signal). Never a silent truncation.</summary>
    const int PeekListCap = 40;

    /// <summary>Imports whose presence names a capability the DLL reaches for. FACTS about the import table with a plain
    /// gloss — never a behavior claim (it hooks the API; what it does with it is tier E).</summary>
    static readonly Dictionary<string, string> HookImports = new(StringComparer.OrdinalIgnoreCase)
    {
        ["d3d11.dll"] = "Direct3D 11 — touches graphics/rendering",
        ["dxgi.dll"] = "DXGI — touches the swapchain/presentation layer",
        ["d3dcompiler_47.dll"] = "D3D shader compiler — compiles shaders at runtime",
        ["dinput8.dll"] = "DirectInput — touches input handling",
        ["xinput1_3.dll"] = "XInput — touches controller input",
        ["ws2_32.dll"] = "Winsock — opens network sockets",
        ["winhttp.dll"] = "WinHTTP — makes HTTP requests",
        ["wininet.dll"] = "WinINet — makes internet requests",
    };

    /// <summary>The Debug-CRT verdict — tier D's sharpest finding and the ONE peek line allowed "will not load" language,
    /// because it is a static, deterministic loader fact (the version-LOCKED class). The debug CRT is NOT redistributable:
    /// it ships with Visual Studio and is absent from a stock Windows, so a plugin importing it dies with error 126.
    ///
    /// But "will not load" is only unconditionally true where the runtime is ABSENT — and houseCARL runs on the modder's
    /// own machine, where it can CHECK rather than assume. A plugin author with VS installed would otherwise be told a DLL
    /// that loads fine for him "will NOT load": a confident wrong answer, which is worse than no answer (Q3). So the
    /// verdict splits: absent here ⇒ it will not load, stated flatly; present here ⇒ it loads FOR YOU and is broken for
    /// everyone else — which is the more useful finding if you are the one shipping it.</summary>
    static void AppendDebugCrt(StringBuilder sb, SkseFileEntry e)
    {
        if (e.Plugin is not { } p || p.Imports is null) return;   // never walked ⇒ no claim either way
        var crt = p.DebugCrtImports;
        if (crt.Count == 0) return;

        var missing = crt.Where(c => !SksePluginReader.IsSystemDllResolvable(c)).ToList();
        sb.Append("  [!] DEBUG BUILD — imports the debug C runtime: ").Append(string.Join(", ", crt)).Append('\n');
        if (missing.Count > 0)
            sb.Append("      → will NOT load: ").Append(string.Join(", ", missing))
              .Append(missing.Count == 1 ? " is" : " are").Append(" not present on this machine, so the loader fails with " +
                      "error 126 (ERROR_MOD_NOT_FOUND). The debug CRT ships only with Visual Studio and is not redistributable — " +
                      "this DLL was shipped as a Debug build by mistake. Ask its author for a Release build.\n");
        else
            sb.Append("      → it loads on THIS machine (you have the debug runtime installed — Visual Studio), but it will " +
                      "fail with error 126 for anyone who doesn't. If you built this, ship a Release build.\n");
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
/// Default: a health summary that separates BROKEN references (DANGLING + UNPARSEABLE — a reference that should resolve
/// and doesn't) from INERT ones (PLUGIN MISSING — the named plugin simply isn't installed, usually optional support for
/// a mod you don't have), then the DIAGNOSTICS first and in full (PLUGIN MISSING gates + tokens, DANGLING, UNPARSEABLE —
/// and read errors, each with file:line provenance and its winning provider), then the
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
        // Two distinct signals, kept apart in the headline (framing fix). BROKEN = a reference that SHOULD resolve and
        // doesn't (DANGLING: plugin present, record absent; UNPARSEABLE: a token we can't read) — the actionable one.
        // INERT = PLUGIN MISSING (gate or token): the named plugin simply isn't installed, so the entry/file does nothing.
        // For a config shipping optional support for a mod you don't have that's expected, not a fault — lumping it into
        // "DEAD" made a healthy order read as thousands of dead references, which is the whole point of this reframe.
        int broken = dangling.Count + unparseable.Count;
        int inert  = missingGates.Count + missingToks.Count;
        int notOk  = broken + inert;                       // every non-OK ref (kept for the accounted-for reconciliation below)
        int filesWithRefs = d.Files.Count(f => f.Refs.Count > 0);

        var sb = new StringBuilder();
        sb.Append("SKSE config audit — profile '").Append(d.ProfileName).Append("' — ")
          .Append(d.ConfigCount).Append(" config(s) scanned, ").Append(filesWithRefs).Append(" carry references, ")
          .Append(refsChecked).Append(" reference(s) checked\n");
        if (broken == 0 && inert == 0)
            sb.Append("✓ every reference resolves against the active load order — nothing broken, nothing inert.\n");
        else if (broken == 0)
            sb.Append("✓ no broken references — every reference to an installed plugin resolves. (")
              .Append(inert).Append(" reference(s) point at plugins not in your load order — inert, usually optional support for a mod you don't have.)\n");
        else
        {
            sb.Append("[!] ").Append(broken).Append(" BROKEN reference(s): ")
              .Append(dangling.Count).Append(" dangling · ").Append(unparseable.Count).Append(" unparseable");
            if (inert > 0)
                sb.Append("   ·   ").Append(inert).Append(" more inert (plugin not installed — usually optional support)");
            sb.Append('\n');
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
        int okInMixed = (refsChecked - notOk) - healthyRefs;   // OK refs living in a file that ALSO has a non-OK ref — so every ref reconciles: refsChecked = notOk + healthyRefs + okInMixed
        var noRefFiles = d.Files.Where(f => f.ReadError is null && f.Refs.Count == 0).ToList();
        sb.Append("\naccounted for: ").Append(healthyFiles.Count).Append(" file(s) with ").Append(healthyRefs)
          .Append(" reference(s) all OK");
        if (okInMixed > 0) sb.Append(" · ").Append(okInMixed).Append(" more OK ref(s) in files that also carry a non-OK reference");
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
            // The suggestion pool must span EVERY axis Match filters on — else a mistyped plugin/provider filter gets only
            // folder/filename suggestions. Match keys on filename/group/provider/relpath + referenced-plugin, so the pool
            // carries filenames, folders, winning-provider mod names, AND the referenced-plugin names (the axis the tool
            // exists to check). PluginNameSuggest dedups + skips empties, so no Distinct is needed here.
            var suggestPool = d.Files.Select(f => f.FileName)
                .Concat(d.Files.Select(f => f.Group).Where(g => g.Length > 0))
                .Concat(d.Files.Select(f => f.WinningProvider).Where(p => !string.IsNullOrEmpty(p)).Select(p => p!))
                .Concat(d.Files.SelectMany(f => f.Refs.Select(r => r.Ref.Plugin)));
            sb.Append("\nnothing under SKSE\\Plugins matched. ")
              .Append(HousecarlCore.PluginNameSuggest.DidYouMean(filter, suggestPool));
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

/// <summary>Renders <see cref="NativePairingAuditData"/> as compact, scannable text (housecarl_native_pairing_audit).
/// Default: a health summary, then the DIAGNOSTICS first and in full — PAIRED-BUT-DEAD (the high-confidence class:
/// every candidate DLL statically won't load, version-LOCKED mismatches included when the installed runtime is known),
/// locked-but-unverifiable pairings (runtime unknown → honest degrade), UNPAIRED classes (a verify flag, said so), and
/// unreadable-.pex notes — then the accounted-for baseline (engine / skse-core counts, paired-healthy classes grouped
/// by their implementing mod). Bounded by max_chars with explicit cut notices (Q3). filter= shows a class in full:
/// native function names, pairing evidence, per-DLL manifests + load verdicts, conflict chains.</summary>
static class NativePairingWire
{
    /// <summary>One candidate DLL's static load verdict for the render: LOADS / VERIFY (locked, runtime unknown) /
    /// DEAD (a named static blocker or a locked-runtime mismatch).</summary>
    enum DllFate { Loads, Verify, Dead }

    static (DllFate Fate, string Detail) Judge(NativePairedDll d, string? runtime)
    {
        if (d.LoadBlocker is { } b) return (DllFate.Dead, b);
        if (d.Info is not { } info) return (DllFate.Verify, "no static manifest read");   // defensive: blocker-less entries carry Info by construction
        if (info.Kind == SksePluginReader.SksePluginKind.LegacyQuery)
        {
            // The AE loader loads ONLY version-data plugins (SksePluginReader's first bullet) — a query-only SE/VR-era
            // plugin will NOT load on a 1.6+ runtime (review finding: rendering these LOADS buried the tool's headline
            // breakage class, the abandoned SE-era mod on an AE game).
            if (runtime is { } rt2 && SksePluginReader.IsAeRuntime(rt2))
                return (DllFate.Dead, $"query-only SE/VR-era plugin — the AE loader (installed game is {rt2}) loads only version-data plugins, so it will NOT load");
            if (runtime is not null)
                return (DllFate.Loads, "legacy SE/VR plugin — loads on this SE runtime, but its metadata is set at runtime (not statically verifiable)");
            return (DllFate.Verify, "legacy SE/VR-era query-only plugin — loads on SE (1.5.x) but NOT on an AE (1.6+) runtime; installed game version unknown, verify");
        }
        var v = info.Version!;
        if (v.VersionIndependent)
            return (DllFate.Loads, $"version-independent ({(v.UsesAddressLibrary ? "Address Library" : "signature scanning")})");
        string locked = v.CompatibleVersions.Count > 0 ? string.Join(", ", v.CompatibleVersions) : "(none listed!)";
        if (runtime is null)
            return (DllFate.Verify, $"version-LOCKED → {locked} — installed game version unknown, verify it matches");
        return SksePluginReader.RuntimeCompatible(v, runtime)
            ? (DllFate.Loads, $"version-LOCKED → {locked} = installed {runtime}")
            : (DllFate.Dead, $"version-LOCKED → {locked} ≠ installed {runtime} — will NOT load on this game version");
    }

    /// <summary>A paired class's verdict = the BEST fate among its candidate DLLs — the audit can't know WHICH DLL
    /// implements the class (tier E), so one loadable candidate keeps the pairing plausible.</summary>
    static DllFate BestFate(NativeClassEntry c, string? runtime)
    {
        var best = DllFate.Dead;
        foreach (var d in c.PairedDlls)
        {
            var (f, _) = Judge(d, runtime);
            if (f == DllFate.Loads) return DllFate.Loads;
            if (f == DllFate.Verify) best = DllFate.Verify;
        }
        return best;
    }

    public static string Render(NativePairingAuditData d, string? filter, int cap)
    {
        if (filter is { Length: > 0 }) return RenderFiltered(d, filter.Trim(), cap);

        var engine = d.Classes.Where(c => c.Provenance == NativeProvenance.Engine).ToList();
        var skseCore = d.Classes.Where(c => c.Provenance == NativeProvenance.SkseCore).ToList();
        var third = d.Classes.Where(c => c.Provenance == NativeProvenance.ThirdParty).ToList();
        var unpaired = third.Where(c => c.Rung == NativePairingRung.Unpaired).ToList();
        // ONE fate pass per paired class (review finding: three Where partitions each re-judged every DLL) — the
        // section split and the per-DLL tags come from the same Judge, so they can never disagree.
        var byFate = third.Where(c => c.Rung != NativePairingRung.Unpaired)
            .ToLookup(c => BestFate(c, d.InstalledRuntime));
        var dead = byFate[DllFate.Dead].ToList();
        var verify = byFate[DllFate.Verify].ToList();
        var healthy = byFate[DllFate.Loads].ToList();

        var sb = new StringBuilder();
        sb.Append("native pairing audit — profile '").Append(d.ProfileName).Append("' — ")
          .Append(d.PexScanned).Append(" compiled script(s) scanned, ").Append(d.Classes.Count)
          .Append(" class(es) declare native functions\n");
        if (d.InstalledRuntime is { } rt) sb.Append("installed game runtime: ").Append(rt).Append('\n');
        else sb.Append("installed game runtime: could not be resolved — version-LOCKED findings degrade to 'verify'\n");

        if (dead.Count == 0 && unpaired.Count == 0 && verify.Count == 0)
        {
            sb.Append("✓ every third-party native class pairs to a mod whose DLL statically loads — nothing dead, nothing unpaired");
            // The checkmark must not overclaim a universal the scan didn't verify (review finding): unreadable
            // .pex files were never examined, and they're where an unpaired class could hide.
            sb.Append(d.Unreadable.Count > 0 ? $" ({d.Unreadable.Count} unreadable .pex NOT examined — see below).\n" : ".\n");
        }
        else
        {
            sb.Append(dead.Count > 0 ? "[!] " : "✓ no dead pairings. ");
            if (dead.Count > 0) sb.Append(dead.Count).Append(" class(es) PAIRED BUT DEAD — scripts installed, nothing that could implement them loads");
            if (verify.Count > 0) sb.Append(dead.Count > 0 ? "   ·   " : "").Append(verify.Count).Append(" pairing(s) need a version check");
            if (unpaired.Count > 0) sb.Append(dead.Count > 0 || verify.Count > 0 ? "   ·   " : "").Append(unpaired.Count).Append(" class(es) UNPAIRED (verify)");
            sb.Append('\n');
        }

        // ── Diagnostics FIRST, in full (the point of the tool). ──
        if (dead.Count > 0)
        {
            sb.Append("\nPAIRED BUT DEAD — the high-confidence finding: every candidate DLL statically will not load, so every native these scripts declare is a silent no-op in game (").Append(dead.Count).Append("):\n");
            AppendCapped(sb, dead, cap, c => DeadLine(c, d.InstalledRuntime));
        }
        if (verify.Count > 0)
        {
            sb.Append("\npaired, version-LOCKED, runtime unknown — verify the listed runtime matches your game (").Append(verify.Count).Append("):\n");
            AppendCapped(sb, verify, cap, c => DeadLine(c, d.InstalledRuntime));
        }
        if (unpaired.Count > 0)
        {
            sb.Append("\nUNPAIRED — no mod shipping these scripts (winner or chain) ships any SKSE plugin DLL (").Append(unpaired.Count)
              .Append("). A VERIFY flag, not 'broken': most often a declaration copy of a framework that isn't installed — the calls will silently no-op if anything uses them:\n");
            AppendCapped(sb, unpaired, cap, c =>
                $"  - {c.ClassName} ({c.NativeCount} native fn) ← {c.WinningProvider ?? "(no provider)"} ({c.ProviderKind})");
        }
        if (d.Unreadable.Count > 0)
        {
            sb.Append("\nunreadable .pex — could not be parsed, NOT counted as native-free (").Append(d.Unreadable.Count).Append("):\n");
            AppendCapped(sb, d.Unreadable, cap, u => $"  - {u.RelPath}: {u.Reason}{(u.WinningProvider is { } p ? $"  [← {p}]" : "")}");
        }

        // ── Accounted-for baseline — everything that ISN'T a finding, so nothing is silently dropped (Q3). ──
        sb.Append("\naccounted for: ").Append(engine.Count).Append(" engine class(es) (carried by an official archive — implemented by the game executable) · ")
          .Append(skseCore.Count).Append(" SKSE-core class(es) (skse64's script additions — implemented by the game-root loader)");
        // Tri-state (Q3): false = checked, genuinely absent → the definite note; null = the CHECK failed → say that,
        // never render a failed check as a checked-and-absent verdict (review finding).
        if (skseCore.Count > 0 && d.SkseLoaderSeen == false)
            sb.Append("\n  [!] SKSE-core classes are present but no skse64 loader is visible (game root or enabled mods' Root\\ folders) — if SKSE isn't actually installed, every one of these is dead");
        else if (skseCore.Count > 0 && d.SkseLoaderSeen is null)
            sb.Append("\n  (skse64 loader visibility could not be checked)");
        sb.Append("\npaired healthy (").Append(healthy.Count).Append(" class(es)) — implementing mod ← its classes:\n");
        foreach (var g in healthy.GroupBy(c => c.PairedMod ?? "(?)", StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (sb.Length >= cap) { sb.Append("  ... [remaining healthy groups omitted; raise max_chars]\n"); break; }
            sb.Append("  - ").Append(g.Key).Append(": ").Append(string.Join(", ", g.Select(c => c.ClassName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))).Append('\n');
        }

        sb.Append("\n(scope: what the winning compiled scripts DECLARE, statically paired to what their mods ship. 'Paired' means the " +
                  "co-shipment evidence is plausible and a candidate DLL loads — NEVER that the DLL registers exactly these functions " +
                  "(registration is runtime behavior, the honest ceiling). Which mods CALL an unpaired class is not scanned (a possible Wave 2).)\n");
        AppendCaveats(sb, d);
        sb.Append("\n→ filter='<class/mod/DLL>' for full detail: native function names, pairing evidence, per-DLL manifests and load verdicts.");
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>The one-block render of a dead/verify pairing: the class line, then each candidate DLL's fate.</summary>
    static string DeadLine(NativeClassEntry c, string? runtime)
    {
        var sb = new StringBuilder();
        sb.Append("  - ").Append(c.ClassName).Append(" (").Append(c.NativeCount).Append(" native fn) ← ")
          .Append(c.WinningProvider ?? "(no provider)")
          .Append(c.Rung == NativePairingRung.ChainMod ? $" — paired via the conflict chain to {c.PairedMod}" : $" — paired to {c.PairedMod}");
        foreach (var dll in c.PairedDlls)
            sb.Append("\n      ").Append(DllLine(dll, runtime, withVersion: false));
        return sb.ToString();
    }

    /// <summary>ONE per-DLL fate line for BOTH the default and the filter= views (review finding: two hand-kept copies
    /// of the same line drift): "[FATE] group\file ("name" vX)? — detail".</summary>
    static string DllLine(NativePairedDll dll, string? runtime, bool withVersion)
    {
        var (fate, detail) = Judge(dll, runtime);
        var sb = new StringBuilder();
        sb.Append(fate switch { DllFate.Dead => "[DEAD] ", DllFate.Verify => "[VERIFY] ", _ => "[LOADS] " })
          .Append(dll.Group.Length > 0 ? dll.Group + "\\" : "").Append(dll.FileName);
        if (withVersion && dll.Info?.Version is { } v) sb.Append("  \"").Append(v.Name).Append("\" v").Append(v.PluginVersion);
        sb.Append(" — ").Append(detail);
        return sb.ToString();
    }

    /// <summary>filter= : full detail for every matching class (by class name, path, provider, paired mod, or a
    /// candidate DLL's filename) — the declared native functions, the pairing evidence rung, each candidate DLL's
    /// manifest + load verdict, and the conflict chain.</summary>
    static string RenderFiltered(NativePairingAuditData d, string filter, int cap)
    {
        bool In(string? s) => s is not null && s.Contains(filter, StringComparison.OrdinalIgnoreCase);
        bool Match(NativeClassEntry c) => In(c.ClassName) || In(c.RelPath) || In(c.WinningProvider) || In(c.PairedMod)
            || c.PairedDlls.Any(x => In(x.FileName)) || c.Providers.Any(p => In(p.Name));
        var hits = d.Classes.Where(Match).OrderBy(c => c.ClassName, StringComparer.OrdinalIgnoreCase).ToList();

        var sb = new StringBuilder();
        sb.Append("native pairing audit — filter '").Append(filter).Append("' — ")
          .Append(hits.Count).Append(" class(es) match [profile '").Append(d.ProfileName).Append("']\n");
        if (hits.Count == 0)
        {
            // The suggestion pool spans EVERY axis Match filters on (the tier-B lesson): class names, providers,
            // paired mods, and DLL filenames. PluginNameSuggest dedups + skips empties.
            var pool = d.Classes.Select(c => c.ClassName)
                .Concat(d.Classes.Select(c => c.WinningProvider).Where(p => !string.IsNullOrEmpty(p)).Select(p => p!))
                .Concat(d.Classes.Select(c => c.PairedMod).Where(p => !string.IsNullOrEmpty(p)).Select(p => p!))
                .Concat(d.Classes.SelectMany(c => c.PairedDlls.Select(x => x.FileName)));
            sb.Append("\nno native-declaring class matched. ").Append(HousecarlCore.PluginNameSuggest.DidYouMean(filter, pool));
            sb.Append('\n');
            AppendCaveats(sb, d);   // a "no match" over an incompletely-read build must carry the caveat (Q3)
            return sb.ToString().TrimEnd('\n');
        }

        int shown = 0;
        foreach (var c in hits)
        {
            if (sb.Length >= cap) { sb.Append("\n  ... [showing ").Append(shown).Append(" of ").Append(hits.Count).Append(" classes; raise max_chars]\n"); break; }
            sb.Append('\n').Append(c.ClassName).Append("  (").Append(c.RelPath).Append(")\n");
            sb.Append("  provenance: ").Append(c.Provenance switch
            {
                NativeProvenance.Engine => "ENGINE — carried by an official archive; implemented by the game executable (baseline)",
                NativeProvenance.SkseCore => "SKSE CORE — skse64's script additions; implemented by the game-root loader (baseline)",
                _ => c.Rung switch
                {
                    NativePairingRung.SameMod => $"third-party, paired to its own provider ({c.PairedMod})",
                    NativePairingRung.ChainMod => $"third-party, paired via the conflict chain to {c.PairedMod}",
                    _ => "third-party, UNPAIRED — no mod in this file's chain ships any SKSE plugin DLL (verify)",
                },
            }).Append('\n');
            if (c.ProviderCount > 1)
                sb.Append("  [!] contested by ").Append(c.ProviderCount).Append(" sources (winner scanned): ")
                  .Append(string.Join(" › ", c.Providers.Select(p => $"{p.Name} ({p.Kind})"))).Append('\n');
            else
                sb.Append("  provider: ").Append(c.WinningProvider ?? "(none)").Append(" (").Append(c.ProviderKind).Append(")\n");
            foreach (var dll in c.PairedDlls)
                sb.Append("  ").Append(DllLine(dll, d.InstalledRuntime, withVersion: true)).Append('\n');
            sb.Append("  native functions (").Append(c.NativeCount).Append("): ");
            var fns = string.Join(", ", c.NativeFunctions);
            if (sb.Length + fns.Length > cap && c.NativeCount > 8)
                sb.Append(string.Join(", ", c.NativeFunctions.Take(8))).Append(", ... [").Append(c.NativeCount - 8).Append(" more; raise max_chars]");
            else sb.Append(fns);
            sb.Append('\n');
            shown++;
        }
        // The Q3 caveats ride the FILTERED view too (review finding): "no match" or a partial hit over a build whose
        // BSA failed to read must never read as a clean answer.
        sb.Append('\n');
        AppendCaveats(sb, d);
        return sb.ToString().TrimEnd('\n');
    }

    static void AppendCapped<T>(StringBuilder sb, IReadOnlyList<T> items, int cap, Func<T, string> line)
    {
        int shown = 0;
        foreach (var e in items)
        {
            if (sb.Length >= cap) { sb.Append("  ... [showing ").Append(shown).Append(" of ").Append(items.Count).Append("; raise max_chars or use filter= to see all]\n"); break; }
            sb.Append(line(e)).Append('\n'); shown++;
        }
    }

    static void AppendCaveats(StringBuilder sb, NativePairingAuditData d)
    {
        if (d.ReadIncomplete)
            sb.Append("[!] a BSA failed to read this build, so a script present only in it may be missing from this audit (Q3).\n");
        foreach (var w in d.Warnings) sb.Append("[!] ").Append(w).Append('\n');
        foreach (var f in d.BsaFailures) sb.Append("[!] archive read failure: ").Append(f).Append('\n');
    }
}
