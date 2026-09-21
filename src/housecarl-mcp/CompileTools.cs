using System.ComponentModel;
using System.Text;
using HousecarlCore;
using ModelContextProtocol.Server;

namespace HousecarlMcp;

/// <summary>housecarl_compile_script — drives the CK's PapyrusCompiler.exe, landing the .pex in a houseCARL patch-mod folder; contracts in docs/architecture/papyrus.md.</summary>
[McpServerToolType]
public static class CompileTools
{
    /// <summary>The import path plus each entry's provenance; the counts are derived from <see cref="Entries"/>, because dedup and the vanilla re-slot change what survives.</summary>
    public sealed record ImportPlan(
        IReadOnlyList<(string Dir, string Origin)> Entries,
        bool AutoEnabled,
        string? ImportSetName,
        string? Warning,
        int AutoScanned = 0,
        PapyrusDependencyScan? Scan = null,
        IReadOnlyList<string>? ReferencedProviders = null,
        bool VanillaMissing = false,
        bool ScanFailed = false)
    {
        /// <summary>Provenance labels: the exact strings the renders print, and the keys the counts group on.</summary>
        public const string OwnFolder = "the script's own folder";
        public const string Vanilla = "vanilla sources";
        public const string CallerDirs = "import_dirs=";
        public const string AutoPrefix = "MO2: ";

        /// <summary>The ordered list handed to the compiler.</summary>
        public IReadOnlyList<string> Dirs { get; } = Entries.Select(e => e.Dir).ToList();

        /// <summary>Dirs that came from the caller (import_dirs= / import_set=) and survived into the final path.</summary>
        public int CallerCount => Entries.Count(e => e.Origin == CallerDirs);

        /// <summary>The mod folders behind entries the scan put on the path; one the caller also passed takes the caller slot.</summary>
        public IReadOnlyList<string> AutoProviders =>
            Entries.Where(e => e.Origin.StartsWith(AutoPrefix, StringComparison.Ordinal))
                   .Select(e => e.Origin[AutoPrefix.Length..]).ToList();

        /// <summary>The mods the walk matched, whichever slot each took — the one count not derived from <see cref="Entries"/>, which a caller-passed folder would drop out of.</summary>
        public IReadOnlyList<string> Referenced { get; } = ReferencedProviders ?? Array.Empty<string>();
    }

    [McpServerTool(Name = ToolNames.CompileScript, Title = "Compile a Papyrus script (.psc → .pex)"),
     Description(
         "Compile a Papyrus script (.psc) to .pex using the Creation Kit's PapyrusCompiler.exe, landing the .pex in a NEW " +
         "houseCARL patch-mod folder you review and enable in MO2 (originals untouched) — or pass out_path= to land it in a " +
         "folder you choose (houseCARL appends Scripts\\ so MO2 deploys it). Pass script= the full path to the " +
         ".psc to compile. IMPORT PATH: houseCARL adds the script's own folder, then AUTO-DISCOVERS the Papyrus source " +
         "folders your enabled MO2 mods already ship (Source\\Scripts / Scripts\\Source, in MO2 priority order — so SKSE, " +
         "PapyrusUtil, PO3, SkyUI, JContainers and friends need no retyping), NARROWED to the folders this script actually " +
         "references (directly or transitively), then the vanilla sources LAST; pass " +
         "auto_imports=false to leave your enabled mods off the import path. Add anything the scan can't reach (local stubs, a dev project tree, " +
         "sources you extracted from a BSA — the CK compiler cannot read archives) via import_dirs= (';'-separated), and " +
         "save_import_set=<name> to persist that list so later calls just pass import_set=<name>. Precedence is your " +
         "import_dirs=/import_set= > the auto-discovered mods > vanilla, so mod-extended copies of vanilla scripts " +
         "(SKSE's Actor.psc etc.) win. The import path searched is REPORTED on every call. On a compile FAILURE it returns " +
         "the per-line errors as 'name(line,col): message' so you can fix " +
         "the .psc and recompile (look unfamiliar functions up with the papyrus-reference skill); on SUCCESS it returns the " +
         ".pex path. Needs houseCARL pointed at your MO2 instance (for the output folder) and the Papyrus compiler path — if " +
         "the compiler isn't set yet, houseCARL tells you exactly what to ask for and how to set it. The CK compiler ships " +
         "with the vanilla Steam game install, NOT a Wabbajack 'Stock Game' copy.")]
    public static string CompileScript(
        LoadOrderService svc,
        ToolPathResolver bridge,
        UserConfigStore store,
        [Description("Full path to the .psc source file to compile.")]
            string script,
        [Description("Optional. Extra import directories where dependency sources (.psc) live — separated by ';'. The script's own folder, your enabled mods' source folders (unless auto_imports=false), and the vanilla source folder are added automatically; these directories outrank all of those (first match wins), so extended copies of vanilla scripts take precedence.")]
            string? import_dirs = null,
        [Description("Optional (default true). Scan the enabled MO2 mods for Papyrus source folders (Source\\Scripts / Scripts\\Source) and put the ones this script references — by name, followed transitively through those scripts — on the import path, in MO2 priority order, so installed frameworks need no retyping. The narrowing is not optional: a big modlist ships hundreds of source folders (measured: 501 on a 3617-mod order), which together exceed what a Windows command line can carry. Pass false to compile against only the script's own folder, your import_dirs=/import_set=, and the vanilla sources.")]
            bool auto_imports = true,
        [Description("Optional. Name of a SAVED import-directory set (see save_import_set=) to add to the import path. Its dirs rank after import_dirs= and before the auto-discovered mods. An unknown name is refused, and the saved names are listed.")]
            string? import_set = null,
        [Description("Optional. Save this call's import_dirs= (plus any import_set= it loaded) under this name for reuse via import_set=. Persisted in houseCARL's user config, so it survives restarts; re-saving an existing name replaces it.")]
            string? save_import_set = null,
        [Description("Optional. Base name for the NEW patch-mod folder the .pex lands in (default 'houseCARL_Scripts'); auto-suffixed if taken.")]
            string? patch = null,
        [Description("Optional. Filename of an existing houseCARL patch mod to add the .pex into instead of creating a fresh folder (accumulate compiled scripts). Found by the plugin's filename even if you've renamed its MO2 mod folder; for two patches sharing a filename, pass the mod-folder name here instead (folder & plugin names need not match).")]
            string? into = null,
        [Description("Optional. Land the .pex in a folder of YOUR choosing instead of a fresh houseCARL patch folder — pass the ABSOLUTE path to the mod-folder ROOT (a relative path is refused, because the server would resolve it against its own working directory); houseCARL appends Scripts\\ (and won't double it if you already point at a ...\\Scripts folder). When set, patch=/into= are ignored. Scripts load from exactly <mods>\\<YourMod>\\Scripts, the MO2 overwrite folder, or <Data>\\Scripts — anywhere else (including a NESTED path under a mod) the .pex still compiles but you're warned it won't deploy automatically.")]
            string? out_path = null) => Guard.Tool(ToolNames.CompileScript, () =>
    {
        // 1) MO2 must be configured — the .pex lands under the instance's mods folder.
        if (svc.ConfigPromptOrNull() is { } cfgPrompt) return cfgPrompt;

        // 2) validate the script path.
        if (string.IsNullOrWhiteSpace(script))
            return "error: no script given. Pass script= the full path to the .psc file to compile.";
        script = script.Trim().Trim('"');
        if (!File.Exists(script))
            return $"error: no such file: '{script}'. Pass the full path to the .psc source.";
        if (!script.EndsWith(".psc", StringComparison.OrdinalIgnoreCase))
            return $"error: '{Path.GetFileName(script)}' is not a .psc source file.";
        script = Path.GetFullPath(script);
        var objectName = Path.GetFileNameWithoutExtension(script);
        var scriptDir = Path.GetDirectoryName(script)!;

        // 3) the compiler, or the prompt if unset; the hints are the game dir, then the Steam SE install a Stock Game setup keeps the CK in.
        if (bridge.RequireOrPrompt(ToolDependency.PapyrusCompiler, out var compilerExe, svc.CompilerGameDirHints()) is { } toolPrompt) return toolPrompt;

        // 4) caller extras: import_dirs= then the named set. An unknown set is refused and names the saved sets.
        var callerExtras = SplitDirs(import_dirs).ToList();
        string? importSetName = null;
        if (!string.IsNullOrWhiteSpace(import_set))
        {
            importSetName = import_set.Trim();
            var saved = store.GetImportSet(importSetName);
            if (saved is null)
            {
                var known = store.ImportSetNames();
                return $"error: no saved import set named '{importSetName}'. " + (known.Count == 0
                    ? "None are saved yet — pass import_dirs= once together with save_import_set= to create one."
                    : "Saved sets: " + string.Join(", ", known) + ".");
            }
            callerExtras.AddRange(saved);
        }
        callerExtras = callerExtras.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // 5) persist the set before compiling, and report a save failure: it then holds for this session only.
        string? saveNote = null;
        if (!string.IsNullOrWhiteSpace(save_import_set))
        {
            var setName = save_import_set.Trim();
            if (callerExtras.Count == 0)
                saveNote = $"note: save_import_set='{setName}' was ignored — there were no import_dirs=/import_set= dirs to save " +
                           "(the auto-discovered and vanilla folders are re-derived every call, so a set of them would be meaningless).";
            else
            {
                var (ok, err) = store.SaveImportSet(setName, callerExtras);
                saveNote = ok
                    ? $"saved import set '{setName}' ({callerExtras.Count} dir(s)) — later calls can pass import_set={setName}."
                    : $"warning: import set '{setName}' could NOT be saved ({err}) — its dirs apply to this compile but will not survive a restart.";
            }
        }

        // 6) the import path, in order; auto_imports=false skips the scan except to locate missing vanilla sources.
        IReadOnlyList<PapyrusSourceRoot> autoRoots = Array.Empty<PapyrusSourceRoot>();
        string? gameDataSources = null, autoWarning = null;
        bool scanFailed = false;
        if (NeedsModlistScan(auto_imports, compilerExe!))
        {
            (autoRoots, gameDataSources, autoWarning, scanFailed) = svc.PapyrusSourceImportDirs();
            if (!auto_imports) autoRoots = Array.Empty<PapyrusSourceRoot>();   // read for the vanilla fallback only
        }
        // The warning rides along whenever the scan ran, on either branch.
        var plan = PlanImports(script, scriptDir, compilerExe!, callerExtras, autoRoots, auto_imports, importSetName,
                               autoWarning, gameDataSources, scanFailed);

        // The whole path travels as one `-i=` argument, so a path past the command-line cap is refused with the way out.
        var joinedLength = string.Join(";", plan.Dirs).Length;
        if (joinedLength > 30_000)
            return $"error: the assembled import path is too long for one compiler command line ({plan.Dirs.Count} dirs, " +
                   $"{joinedLength} chars; the limit is about 32000). Re-run with auto_imports=false and pass only the " +
                   "dependencies this script needs via import_dirs= (save_import_set= will keep that list for next time).";

        // 7) output folder: out_path= is user-owned, takes Scripts\ appended, and supersedes patch=/into= saying so.
        LoadOrderService.RiderFolder rf;
        string? deployWarning = null, outputNote = null;
        if (!string.IsNullOrWhiteSpace(out_path))
        {
            if (!string.IsNullOrWhiteSpace(patch) || !string.IsNullOrWhiteSpace(into))
                outputNote = "note: out_path= was given, so patch=/into= are ignored (the .pex lands in out_path, not a houseCARL patch folder).";
            try { rf = svc.ResolveExplicitScriptFolder(out_path, out deployWarning); }
            // The ignored-lane note rides the refusal too — it is still true of the call about to be retyped.
            catch (InvalidOperationException ex) { return "error: " + ex.Message + (outputNote is null ? "" : "\n" + outputNote); }
        }
        else
        {
            try { rf = svc.ResolveCompiledScriptFolder(patch, into); }
            catch (InvalidOperationException ex) { return "error: " + ex.Message; }
        }

        // 8) compile + render.
        var result = HousecarlCore.PapyrusCompile.CompileObject(compilerExe!, objectName, plan.Dirs, rf.OutputDir);
        var rendered = Render(result, plan, userChoseOutputDir: !string.IsNullOrWhiteSpace(out_path));
        if (result.Success)
        {
            // Never report a clean "done" for a .pex that will not deploy from where out_path= put it.
            if (deployWarning is not null) rendered += "\n" + deployWarning;
        }
        else
        {
            // No .pex: an empty fresh folder is deleted, a partial one named, an into= or out_path= folder left alone.
            var left = svc.RemoveOrNameRiderResidue(rf);
            if (left is not null)
                rendered += $"\nThe freshly created mod folder at '{left}' still holds partial output — delete it or retry with into=.";
        }
        if (saveNote is not null) rendered = saveNote + "\n" + rendered;
        if (outputNote is not null) rendered = outputNote + "\n" + rendered;
        return rendered;
    });

    /// <summary>Split a ';'-separated import-dir spec into trimmed, unquoted directories, dropping blanks.</summary>
    internal static IEnumerable<string> SplitDirs(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) yield break;
        foreach (var raw in spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var d = raw.Trim('"').Trim();
            if (d.Length > 0) yield return d;
        }
    }

    /// <summary>Whether the MO2 modlist must be read at all: when the scan was asked for, and when the compiler has no vanilla sources beside it. Pinned by <c>import-order-guard</c>.</summary>
    internal static bool NeedsModlistScan(bool autoImports, string compilerExe)
        => autoImports || VanillaSourceDir(compilerExe) is null;

    /// <summary>The vanilla sources shipped with the compiler's own game, or null — one definition, because the path and its labelling must recognise the same folder.</summary>
    public static string? VanillaSourceDir(string compilerExe)
    {
        var gameRoot = Path.GetDirectoryName(Path.GetDirectoryName(compilerExe));
        if (gameRoot is null) return null;
        var vanilla = Path.Combine(gameRoot, "Data", "Source", "Scripts");
        return Directory.Exists(vanilla) ? vanilla : null;
    }

    /// <summary>Assemble the import-directory list, whose ORDER is semantics; the order is in docs/architecture/papyrus.md, pinned by <c>import-order-guard</c>.</summary>
    public static List<string> BuildImports(string scriptDir, string compilerExe, string? import_dirs,
                                            IReadOnlyList<string>? autoDirs = null, string? resolvedVanilla = null)
    {
        var imports = new List<string> { scriptDir };
        imports.AddRange(SplitDirs(import_dirs));
        if (autoDirs is not null) imports.AddRange(autoDirs);
        // resolvedVanilla, when given, IS the vanilla dir: the plan resolves it once and hands the answer down.
        var vanilla = resolvedVanilla ?? VanillaSourceDir(compilerExe);
        if (vanilla is not null)
        {
            // The auto-added vanilla dir stays last, re-passed or re-found; the own-folder slot is the one exception.
            imports.RemoveAll(d => d.Equals(vanilla, StringComparison.OrdinalIgnoreCase)
                                   && !d.Equals(scriptDir, StringComparison.OrdinalIgnoreCase));
            imports.Add(vanilla);
        }
        return imports.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Label each surviving dir of <see cref="BuildImports"/> off the FINAL list: own folder, vanilla, a caller dir, then a discovered mod.</summary>
    internal static ImportPlan PlanImports(
        string targetScript, string scriptDir, string compilerExe, IReadOnlyList<string> callerExtras,
        IReadOnlyList<PapyrusSourceRoot> autoRoots, bool autoEnabled, string? importSetName, string? warning,
        string? gameDataSources = null, bool scanFailed = false)
    {
        // The compiler's own game dir first, its sources matching the flags file, then the modlist's as the fallback.
        var vanilla = VanillaSourceDir(compilerExe) ?? gameDataSources;

        // Narrow the scan to what this script reaches, vanilla held out of the candidates as it is appended last anyway.
        var candidates = autoRoots.Where(r => vanilla is null || !r.Dir.Equals(vanilla, StringComparison.OrdinalIgnoreCase)).ToList();
        PapyrusDependencyScan? scan = null;
        IReadOnlyList<string> autoDirs = Array.Empty<string>();
        if (candidates.Count > 0)
        {
            var seeds = new List<string> { scriptDir };
            seeds.AddRange(callerExtras);
            scan = PapyrusDependencyFilter.Relevant(targetScript, seeds, candidates.Select(c => c.Dir).ToList());
            autoDirs = scan.Folders;
        }

        var dirs = BuildImports(scriptDir, compilerExe, string.Join(";", callerExtras), autoDirs, vanilla);

        // Providers are indexed from the KEPT folders only: a dropped candidate reaches the list only as a caller dir.
        var keptSet = new HashSet<string>(autoDirs, StringComparer.OrdinalIgnoreCase);
        var providers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in candidates) if (keptSet.Contains(r.Dir)) providers.TryAdd(r.Dir, r.Provider);
        var callerSet = new HashSet<string>(callerExtras, StringComparer.OrdinalIgnoreCase);

        var entries = new List<(string Dir, string Origin)>(dirs.Count);
        foreach (var d in dirs)
        {
            // The caller outranks the scan here, mirroring the slot BuildImports gave it.
            string origin =
                d.Equals(scriptDir, StringComparison.OrdinalIgnoreCase) ? ImportPlan.OwnFolder
                : vanilla is not null && d.Equals(vanilla, StringComparison.OrdinalIgnoreCase) ? ImportPlan.Vanilla
                : callerSet.Contains(d) ? ImportPlan.CallerDirs
                : providers.TryGetValue(d, out var mod) ? ImportPlan.AutoPrefix + mod
                : ImportPlan.CallerDirs;
            entries.Add((d, origin));
        }

        // The scan's own answer, kept separate from the slot labels, because the two answer different questions.
        var referenced = scan is null
            ? (IReadOnlyList<string>)Array.Empty<string>()
            : scan.Folders.Select(f => providers.TryGetValue(f, out var m) ? m : Path.GetFileName(f)).ToList();

        return new ImportPlan(entries, autoEnabled, importSetName, warning, candidates.Count, scan, referenced,
                              VanillaMissing: vanilla is null, ScanFailed: scanFailed);
    }

    /// <summary>The one-line "what was searched" summary, printed on every call, provider names capped with a "+N more" tail.</summary>
    internal static string ImportSummary(ImportPlan p)
    {
        var sb = new StringBuilder();
        sb.Append("imports: ").Append(p.Dirs.Count).Append(" dir(s) searched — the script's own folder");
        if (p.CallerCount > 0)
        {
            sb.Append("; ").Append(p.CallerCount).Append(" from import_dirs=");
            if (p.ImportSetName is not null) sb.Append("/import_set=").Append(p.ImportSetName);
        }
        if (!p.AutoEnabled)
            sb.Append("; auto_imports=false (your enabled mods are NOT on the import path)");
        else if (p.ScanFailed)
            // A read that threw reached no conclusion, so the failure is flagged rather than rendered as "0 of 0".
            sb.Append("; the modlist could NOT be read, so none of your installed mods' source folders were scanned");
        else
        {
            // Report the narrowing as the WALK's count, with no arithmetic against the import_dirs= clause.
            var provs = p.Referenced;
            sb.Append("; the modlist scan matched ").Append(provs.Count).Append(" of ").Append(p.AutoScanned)
              // "referenced by this script" is a claim about contents, so it is only made when the source was read.
              .Append(p.Scan is { TargetUnreadable: true }
                          ? " scanned mod source folder(s) (the script could NOT be read — see below)"
                          : " scanned mod source folder(s) referenced by this script");
            if (provs.Count > 0)
            {
                const int Show = 8;
                sb.Append(" (").Append(string.Join(", ", provs.Take(Show)));
                if (provs.Count > Show) sb.Append(", +").Append(provs.Count - Show).Append(" more");
                sb.Append(')');
            }
        }
        if (p.Entries.Count > 0 && p.Entries[^1].Origin == ImportPlan.Vanilla)
            sb.Append("; vanilla sources last");
        sb.Append('.');
        sb.Append(ImportCaveats(p));
        return sb.ToString();
    }

    /// <summary>The full ordered import path with provenance, printed on a failure.</summary>
    internal static string ImportDetail(ImportPlan p)
    {
        var sb = new StringBuilder("import path searched, in order (the compiler takes the FIRST match):");
        for (int i = 0; i < p.Entries.Count; i++)
            sb.Append("\n  ").Append((i + 1).ToString().PadLeft(2)).Append(". ")
              .Append(p.Entries[i].Dir).Append("   [").Append(p.Entries[i].Origin).Append(']');
        sb.Append(ImportCaveats(p));
        return sb.ToString();
    }

    /// <summary>Everything that makes the printed path less than a complete answer, emitted from inside both renders.</summary>
    internal static string ImportCaveats(ImportPlan p)
    {
        var sb = new StringBuilder();
        // No vanilla sources anywhere: every vanilla type will fail to resolve, and the banner would blame a mod.
        if (p.VanillaMissing)
            sb.Append("\n⚠ NO vanilla Papyrus sources are on the import path — houseCARL found none beside the compiler " +
                      "(<game>\\Data\\Source\\Scripts)")
              // A failed read never reached the data folder, and which of the two it is changes the fix.
              .Append(p.ScanFailed
                          ? " and could not read your MO2 modlist to look under the data folder (see below)."
                          : " and none under your MO2 data folder.")
              .Append(" Every vanilla type will fail to resolve until you unpack them (the CK ships them as " +
                      "Scripts.zip) or pass their folder via import_dirs=.");
        if (p.Scan is { TargetUnreadable: true })
            sb.Append("\n⚠ the script's own source could not be READ when the import path was assembled (locked or moved " +
                      "after houseCARL first checked it), so NOTHING was resolved from its contents and the modlist scan " +
                      "contributed nothing — this is not a statement that the script references none of your mods.");
        if (p.Scan is { BudgetExhausted: true })
            sb.Append("\n⚠ the reference walk stopped at its ").Append(PapyrusDependencyFilter.MaxFilesRead)
              .Append("-file ceiling, so a dependency reached only through the unread tail may be missing from the path — " +
                      "if the compile fails on unresolved symbols, pass that folder via import_dirs=.");
        if (p.Warning is not null) sb.Append('\n').Append(p.Warning);
        return sb.ToString();
    }

    internal static string Render(HousecarlCore.CompileResult r, ImportPlan plan, bool userChoseOutputDir)
    {
        var sb = new StringBuilder();
        if (!r.Ran) return "error: " + r.RunError;   // the compiler couldn't be run at all

        if (r.Success)
        {
            sb.Append("compile OK: ").Append(r.ObjectName).Append(".psc → ").Append(r.PexPath).Append('\n');
            // The destination line must match where the .pex went; a deployability caveat rides on deployWarning.
            sb.Append(userChoseOutputDir
                ? "the .pex is in the output folder you chose (path above)."
                : "the .pex is in a houseCARL patch-mod folder — enable it in MO2 to use it.");
            sb.Append('\n').Append(ImportSummary(plan));
            if (r.Diagnostics.Count > 0)   // a .pex was produced but the compiler emitted notes — surface them as warnings
            {
                sb.Append('\n').Append(r.Diagnostics.Count).Append(" warning(s) (the .pex compiled anyway):");
                foreach (var d in r.Diagnostics) sb.Append("\n  ").Append(d);
            }
            return sb.ToString();
        }

        // failed: this run wrote no .pex, and any previous build is left untouched
        sb.Append("compile FAILED: ").Append(r.ObjectName).Append(".psc — no new .pex produced (any previous build is left unchanged).");
        if (r.Diagnostics.Count > 0)
        {
            // A failure dominated by unresolved-symbol errors leads with the import-path banner, gated on three diagnostics and a two-thirds supermajority (<c>compile-ergonomics-guard</c>).
            int unresolved = 0;
            foreach (var d in r.Diagnostics) if (HousecarlCore.PapyrusCompile.IsUnresolvedSymbol(d.Message)) unresolved++;
            bool dominatedByMissingImports = unresolved >= 3 && unresolved * 3 >= r.Diagnostics.Count * 2;
            if (dominatedByMissingImports)
            {
                sb.Append("\n⚠ This looks like an INCOMPLETE import path, not a bug in the script: ")
                  .Append(unresolved).Append(" of ").Append(r.Diagnostics.Count)
                  .Append(" diagnostics are unresolved-symbol/type errors (e.g. 'unknown type …', '… is undefined'). The CK " +
                          "compiler resolves every referenced script against the import path, so a dependency whose source " +
                          "folder is missing makes ALL its calls and types fail.");
                // The remedy differs once the modlist has been scanned, and again when the narrowing was incomplete.
                bool narrowingIncomplete = plan.Scan is { TargetUnreadable: true } or { BudgetExhausted: true };
                sb.Append(!plan.AutoEnabled
                    ? " auto_imports=false, so your enabled mods are NOT on the import path — re-run with auto_imports=true, or pass " +
                      "EVERY dependency's source folder via import_dirs= (SKSE, SkyUI, PapyrusUtil, PO3, JContainers, …; " +
                      "';'-separated) — the same set your project's compile .bat passes via -i=."
                    : plan.ScanFailed
                    ? " The modlist could NOT be read (see the ⚠ note below), so NONE of your installed mods' source folders " +
                      "reached the path — that is the most likely cause here, ahead of anything about this script. Fix the " +
                      "modlist read, or pass the dependency's source folder via import_dirs= for now."
                    : narrowingIncomplete
                    ? " START WITH THE ⚠ NOTE BELOW: the modlist scan did not complete, so the " + plan.Referenced.Count +
                      " folder(s) it matched out of " + plan.AutoScanned + " scanned are NOT the full set this script " +
                      "needs — the missing dependency was most likely dropped by that, not by anything wrong with your setup. " +
                      "Pass its source folder via import_dirs= (and save_import_set= to keep it for next time)."
                    : " The modlist scan found " + plan.AutoScanned + " mod source folder(s) and matched the " +
                      plan.Referenced.Count + " this script REFERENCES BY NAME (listed below). So the missing dependency " +
                      "is most likely one the source never names outright, or one that is not installed as an enabled mod, " +
                      "or one shipping its sources inside a BSA (the CK compiler cannot read archives — extract them first, " +
                      "e.g. with " + ToolNames.BsaExtract + "), or one keeping them in a SUBFOLDER of a listed dir. Pass that folder " +
                      "via import_dirs= (and save_import_set= to keep it for next time).");
            }

            // "diagnostic(s)", not "error(s)": the compiler mixes warnings in and the parser splits no severities.
            sb.Append('\n').Append(r.Diagnostics.Count).Append(" diagnostic(s) (errors and possibly warnings — the CK compiler mixes them):");
            foreach (var d in r.Diagnostics) sb.Append("\n  ").Append(d);
            sb.Append('\n').Append(ImportDetail(plan));   // carries the caveats, warning included
            // The generic tail is for the non-dominated case; after the banner it would repeat the import line.
            sb.Append("\nfix the .psc and recompile (look unfamiliar functions/types up with the papyrus-reference skill).");
            if (!dominatedByMissingImports)
                sb.Append(" If a dependency type is 'not found', its source folder may be missing from the import path listed above — pass it via import_dirs=.");
        }
        else
        {
            // no parseable diagnostics — surface the raw compiler output rather than an empty failure
            sb.Append("\nthe compiler reported failure but no per-line diagnostics were parsed. Raw output:");
            if (r.Stderr.Trim().Length > 0) sb.Append("\n[stderr] ").Append(r.Stderr.Trim());
            if (r.Stdout.Trim().Length > 0) sb.Append("\n[stdout] ").Append(r.Stdout.Trim());
            sb.Append('\n').Append(ImportDetail(plan));
        }
        return sb.ToString();
    }
}
