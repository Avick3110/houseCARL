using System.ComponentModel;
using System.Text;
using HousecarlCore;
using ModelContextProtocol.Server;

namespace HousecarlMcp;

/// <summary>Drives the Creation Kit's PapyrusCompiler.exe through <see cref="HousecarlCore.PapyrusCompile"/> to turn a
/// .psc into a .pex, landing the .pex in a houseCARL patch-mod folder. The compiler path comes from
/// <see cref="ToolPathResolver"/> and prompts if unset. The return is structured per-line diagnostics so a failed
/// compile can be read, fixed and retried.</summary>
[McpServerToolType]
public static class CompileTools
{
    /// <summary>The compiler's import path plus the provenance of every entry, assembled by <see cref="PlanImports"/>
    /// and printed by <see cref="Render"/> — what was actually searched is the one fact that explains both a missing
    /// dependency and a shadowed script. The summary counts are derived from <see cref="Entries"/> rather than tracked
    /// alongside it: dedup and the vanilla-last re-slot both change what survives, so a count taken off the inputs
    /// would disagree with the list printed beneath it.</summary>
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
        /// <summary>Provenance labels: the exact strings <see cref="ImportDetail"/> prints, and the keys the counts
        /// group on.</summary>
        public const string OwnFolder = "the script's own folder";
        public const string Vanilla = "vanilla sources";
        public const string CallerDirs = "import_dirs=";
        public const string AutoPrefix = "MO2: ";

        /// <summary>The ordered list handed to the compiler.</summary>
        public IReadOnlyList<string> Dirs { get; } = Entries.Select(e => e.Dir).ToList();

        /// <summary>Dirs that came from the caller (import_dirs= / import_set=) and survived into the final path.</summary>
        public int CallerCount => Entries.Count(e => e.Origin == CallerDirs);

        /// <summary>The providing mod folders behind entries that are on the path because the scan put them there. A
        /// folder the caller also passed takes the caller slot instead and is absent here by design.</summary>
        public IReadOnlyList<string> AutoProviders =>
            Entries.Where(e => e.Origin.StartsWith(AutoPrefix, StringComparison.Ordinal))
                   .Select(e => e.Origin[AutoPrefix.Length..]).ToList();

        /// <summary>The mods the reference walk matched — the scan's own conclusion, independent of which slot each
        /// folder ended up in. The summary and the missing-imports banner quote this, because "what does this script
        /// reference?" is not the question the slots answer. The one count here deliberately not derived from
        /// <see cref="Entries"/>: a folder the caller also passed would drop out of it.</summary>
        public IReadOnlyList<string> Referenced { get; } = ReferencedProviders ?? Array.Empty<string>();
    }

    [McpServerTool(Name = ToolNames.CompileScript, Title = "Compile a Papyrus script (.psc → .pex)"),
     Description(
         "Compile a Papyrus script (.psc) to .pex using the Creation Kit's PapyrusCompiler.exe, landing the .pex in a NEW " +
         "houseCARL patch-mod folder you review and enable in MO2 (originals untouched) — or pass output_dir= to land it in a " +
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
            string? patch_name = null,
        [Description("Optional. Filename of an existing houseCARL patch mod to add the .pex into instead of creating a fresh folder (accumulate compiled scripts). Found by the plugin's filename even if you've renamed its MO2 mod folder; for two patches sharing a filename, pass the mod-folder name here instead (folder & plugin names need not match).")]
            string? into = null,
        [Description("Optional. Land the .pex in a folder of YOUR choosing instead of a fresh houseCARL patch folder — pass the mod-folder ROOT; houseCARL appends Scripts\\ (and won't double it if you already point at a ...\\Scripts folder). When set, patch_name=/into= are ignored. Scripts load from exactly <mods>\\<YourMod>\\Scripts, the MO2 overwrite folder, or <Data>\\Scripts — anywhere else (including a NESTED path under a mod) the .pex still compiles but you're warned it won't deploy automatically.")]
            string? output_dir = null) => Guard.Tool(ToolNames.CompileScript, () =>
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

        // 3) the compiler, or the prompt to surface if unset. The CK installs it under <game>\Papyrus Compiler\, so the
        // hints are the load order's own game dir first, then the located real Steam SE install: that resolves both a
        // normal Steam+CK install and a Stock-Game setup, where the CK lives in the Steam install rather than the copy
        // MO2 points at.
        if (bridge.RequireOrPrompt(ToolDependency.PapyrusCompiler, out var compilerExe, svc.CompilerGameDirHints()) is { } toolPrompt) return toolPrompt;

        // 4) caller extras: import_dirs= then the named set. An unknown set is refused and names the saved sets rather
        // than silently compiling with a shorter path.
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

        // 5) persist the set before compiling: a set is a path list, worth keeping whether or not this script compiles.
        // A save failure is reported — it works this session but will not survive a restart.
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

        // 6) the import path: the script's own folder, the caller's extras, the modlist's own source folders, vanilla
        // last. auto_imports=false skips the scan, which is the whole point of that flag. The one exception is when
        // the compiler has no vanilla sources beside it: then the modlist is read purely to locate them, because the
        // alternative is a path with no vanilla at all.
        IReadOnlyList<PapyrusSourceRoot> autoRoots = Array.Empty<PapyrusSourceRoot>();
        string? gameDataSources = null, autoWarning = null;
        bool scanFailed = false;
        if (NeedsModlistScan(auto_imports, compilerExe!))
        {
            (autoRoots, gameDataSources, autoWarning, scanFailed) = svc.PapyrusSourceImportDirs();
            if (!auto_imports) autoRoots = Array.Empty<PapyrusSourceRoot>();   // read for the vanilla fallback only
        }
        // The warning rides along whenever the scan actually ran: with auto_imports on it explains missing mods, and on
        // the vanilla-only branch it gives the cause behind a missing vanilla slot.
        var plan = PlanImports(script, scriptDir, compilerExe!, callerExtras, autoRoots, auto_imports, importSetName,
                               autoWarning, gameDataSources, scanFailed);

        // The whole path travels as one `-i=` argument and Windows caps a command line at about 32k characters, which a
        // modlist with hundreds of source folders can cross. Refuse with the way out rather than letting the process
        // start fail unreadably.
        var joinedLength = string.Join(";", plan.Dirs).Length;
        if (joinedLength > 30_000)
            return $"error: the assembled import path is too long for one compiler command line ({plan.Dirs.Count} dirs, " +
                   $"{joinedLength} chars; the limit is about 32000). Re-run with auto_imports=false and pass only the " +
                   "dependencies this script needs via import_dirs= (save_import_set= will keep that list for next time).";

        // 7) output folder: output_dir= names a user-owned location, so append Scripts\ rather than making a houseCARL
        // patch folder. It supersedes patch_name=/into=, and says so rather than ignoring them silently.
        LoadOrderService.RiderFolder rf;
        string? deployWarning = null, outputNote = null;
        if (!string.IsNullOrWhiteSpace(output_dir))
        {
            if (!string.IsNullOrWhiteSpace(patch_name) || !string.IsNullOrWhiteSpace(into))
                outputNote = "note: output_dir= was given, so patch_name=/into= are ignored (the .pex lands in output_dir, not a houseCARL patch folder).";
            try { rf = svc.ResolveExplicitScriptFolder(output_dir, out deployWarning); }
            // The ignored-lane note rides the refusal too: a refusal is when a caller re-reads their parameters, and
            // "patch_name= was ignored" is still true of the call they are about to retype.
            catch (InvalidOperationException ex) { return "error: " + ex.Message + (outputNote is null ? "" : "\n" + outputNote); }
        }
        else
        {
            try { rf = svc.ResolveCompiledScriptFolder(patch_name, into); }
            catch (InvalidOperationException ex) { return "error: " + ex.Message; }
        }

        // 8) compile + render.
        var result = HousecarlCore.PapyrusCompile.CompileObject(compilerExe!, objectName, plan.Dirs, rf.OutputDir);
        var rendered = Render(result, plan, userChoseOutputDir: !string.IsNullOrWhiteSpace(output_dir));
        if (result.Success)
        {
            // Never report a clean "done" for a .pex that will not deploy from where output_dir= put it.
            if (deployWarning is not null) rendered += "\n" + deployWarning;
        }
        else
        {
            // A failed compile produced no .pex: delete an empty fresh folder, name a partial one, leave an into= reuse
            // alone. An output_dir= folder is user-owned (CreatedFresh=false), so RemoveOrNameRiderResidue returns null
            // for it by construction — a user directory is never deleted.
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

    /// <summary>Whether the MO2 modlist must be read at all: when the scan was asked for, and — even when it was
    /// declined — when the compiler has no vanilla sources beside it, since the modlist's own
    /// <c>Data\Source\Scripts</c> is then the only place left to find them and a path with no vanilla resolves
    /// nothing. The scan forces the asset build and probes two layouts under every enabled mod, which is the cost
    /// <c>auto_imports=false</c> exists to avoid.</summary>
    internal static bool NeedsModlistScan(bool autoImports, string compilerExe)
        => autoImports || VanillaSourceDir(compilerExe) is null;

    /// <summary>The vanilla Papyrus sources shipped with the game the compiler belongs to
    /// (&lt;game&gt;\Papyrus Compiler\PapyrusCompiler.exe maps to &lt;game&gt;\Data\Source\Scripts, which also holds
    /// the flags file), or null if that folder is not there. One definition, used both by <see cref="BuildImports"/>,
    /// which pins it last, and by the labelling in <see cref="PlanImports"/>, which must recognise the same folder —
    /// derived twice they could disagree and the render would label the vanilla slot as a mod.</summary>
    public static string? VanillaSourceDir(string compilerExe)
    {
        var gameRoot = Path.GetDirectoryName(Path.GetDirectoryName(compilerExe));
        if (gameRoot is null) return null;
        var vanilla = Path.Combine(gameRoot, "Data", "Source", "Scripts");
        return Directory.Exists(vanilla) ? vanilla : null;
    }

    /// <summary>Assemble the compiler's import-directory list. The CK compiler resolves each referenced script to the
    /// FIRST matching .psc across these directories in order, so the order is semantics: the script's own folder,
    /// then caller extras, then the auto-discovered mod source folders in MO2 priority order, then the vanilla
    /// sources last. Vanilla last is load-bearing — mods ship extended copies of vanilla sources (SKSE's Actor.psc,
    /// Game.psc, Form.psc), and ranked any earlier the vanilla copy wins and every call to an extended function fails
    /// as undefined.</summary>
    public static List<string> BuildImports(string scriptDir, string compilerExe, string? import_dirs,
                                            IReadOnlyList<string>? autoDirs = null, string? resolvedVanilla = null)
    {
        var imports = new List<string> { scriptDir };
        imports.AddRange(SplitDirs(import_dirs));
        if (autoDirs is not null) imports.AddRange(autoDirs);
        // resolvedVanilla, when given, IS the vanilla dir: PlanImports resolves it once — compiler-relative first,
        // else the modlist's own Data\Source\Scripts — and hands the answer down. Deriving it independently here too
        // would let the plan report a vanilla slot the assembled path does not have, or the reverse.
        var vanilla = resolvedVanilla ?? VanillaSourceDir(compilerExe);
        if (vanilla is not null)
        {
            // The auto-added vanilla dir must stay last. A caller re-passing it, or the modlist scan reaching it
            // (the game's Data folder is itself a VFS loose root, and Data\Source\Scripts is this folder), would
            // otherwise pin it into an earlier slot and resurrect the shadowing, since Distinct keeps the FIRST
            // occurrence. When the script itself lives in the vanilla folder, that slot is the own-folder slot
            // and stays.
            imports.RemoveAll(d => d.Equals(vanilla, StringComparison.OrdinalIgnoreCase)
                                   && !d.Equals(scriptDir, StringComparison.OrdinalIgnoreCase));
            imports.Add(vanilla);
        }
        return imports.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Drive <see cref="BuildImports"/> and label each surviving dir with where it came from, so the render
    /// can print the searched path with provenance. Labelling reads the FINAL list, not the inputs: dedup and the
    /// vanilla re-slot both drop entries. Where a dir belongs to two origins the precedence is the script's own
    /// folder (routinely a mod's own Source\Scripts, and it holds the first slot), then vanilla (the game's Data
    /// folder is also a loose root, so the scan finds it), then a discovered mod, then the caller.</summary>
    internal static ImportPlan PlanImports(
        string targetScript, string scriptDir, string compilerExe, IReadOnlyList<string> callerExtras,
        IReadOnlyList<PapyrusSourceRoot> autoRoots, bool autoEnabled, string? importSetName, string? warning,
        string? gameDataSources = null, bool scanFailed = false)
    {
        // The compiler's own game dir first, since its sources match the flags file it will use, then the modlist's
        // Data\Source\Scripts as the fallback.
        var vanilla = VanillaSourceDir(compilerExe) ?? gameDataSources;

        // Narrow the scan to what this script reaches: a large modlist ships hundreds of source folders, which
        // together exceed what a Windows command line can carry, and they are overwhelmingly quest and follower mods
        // nothing else references. Vanilla is held out of the candidates — it is appended last unconditionally, so
        // indexing it would only make the closure walk the base game for no gain.
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

        // Providers are indexed from the kept folders only. A candidate the filter dropped can still reach the final
        // list, but only because the caller passed it, so indexing every discovered root would label it as one the
        // scan contributed.
        var keptSet = new HashSet<string>(autoDirs, StringComparer.OrdinalIgnoreCase);
        var providers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in candidates) if (keptSet.Contains(r.Dir)) providers.TryAdd(r.Dir, r.Provider);
        var callerSet = new HashSet<string>(callerExtras, StringComparer.OrdinalIgnoreCase);

        var entries = new List<(string Dir, string Origin)>(dirs.Count);
        foreach (var d in dirs)
        {
            // The caller outranks the scan here, mirroring the slot BuildImports gave it: a dir the caller passed
            // keeps its caller identity even when the scan also found it, or CallerCount would miss it while
            // AutoProviders claims it. That overlap is common — it is the recovery this tool prints on a failure.
            string origin =
                d.Equals(scriptDir, StringComparison.OrdinalIgnoreCase) ? ImportPlan.OwnFolder
                : vanilla is not null && d.Equals(vanilla, StringComparison.OrdinalIgnoreCase) ? ImportPlan.Vanilla
                : callerSet.Contains(d) ? ImportPlan.CallerDirs
                : providers.TryGetValue(d, out var mod) ? ImportPlan.AutoPrefix + mod
                : ImportPlan.CallerDirs;
            entries.Add((d, origin));
        }

        // The scan's own answer, kept separate from the slot labels: a folder can be both matched by the walk and
        // passed explicitly by the caller, in which case it takes the caller slot but is still something the script
        // references. Two fields, because they answer two questions.
        var referenced = scan is null
            ? (IReadOnlyList<string>)Array.Empty<string>()
            : scan.Folders.Select(f => providers.TryGetValue(f, out var m) ? m : Path.GetFileName(f)).ToList();

        return new ImportPlan(entries, autoEnabled, importSetName, warning, candidates.Count, scan, referenced,
                              VanillaMissing: vanilla is null, ScanFailed: scanFailed);
    }

    /// <summary>The one-line "what was actually searched" summary, printed on every call. Provider names are capped at
    /// eight with a "+N more" tail, and the total is always stated so a long list reads as abbreviated rather than as
    /// everything there was.</summary>
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
            // "matched 0 of 0" is a conclusion, and a read that threw never reached one: an empty root list from a
            // failure looks exactly like a modlist with no source folders, so the failure is flagged, not inferred.
            sb.Append("; the modlist could NOT be read, so none of your installed mods' source folders were scanned");
        else
        {
            // Report the narrowing, not just the survivors: a bare survivor count reads as the modlist's whole supply
            // of source folders and hides the decision worth auditing when a dependency turns up missing — that the
            // rest were dropped as unreferenced rather than overlooked. This is the walk's count, not the slot count,
            // and no arithmetic is claimed against the import_dirs= clause, since a folder can be in both.
            var provs = p.Referenced;
            sb.Append("; the modlist scan matched ").Append(provs.Count).Append(" of ").Append(p.AutoScanned)
              // "referenced by this script" is a claim about the source's contents, so it is only made when the source
              // was actually read: an unreadable target yields the same empty result.
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

    /// <summary>The full ordered import path with provenance, printed on a failure, where which folders were searched
    /// and in what order is the question the diagnostics raise and the summary line cannot answer.</summary>
    internal static string ImportDetail(ImportPlan p)
    {
        var sb = new StringBuilder("import path searched, in order (the compiler takes the FIRST match):");
        for (int i = 0; i < p.Entries.Count; i++)
            sb.Append("\n  ").Append((i + 1).ToString().PadLeft(2)).Append(". ")
              .Append(p.Entries[i].Dir).Append("   [").Append(p.Entries[i].Origin).Append(']');
        sb.Append(ImportCaveats(p));
        return sb.ToString();
    }

    /// <summary>Everything that makes the printed path less than a complete answer. Emitted from inside both
    /// <see cref="ImportSummary"/> and <see cref="ImportDetail"/> rather than appended per branch, so every render
    /// that prints an import path prints its caveats — on success and on failure alike, since the caveats are written
    /// for the failing run.</summary>
    internal static string ImportCaveats(ImportPlan p)
    {
        var sb = new StringBuilder();
        // No vanilla sources anywhere, neither beside the compiler nor under the modlist's data dir. Every vanilla
        // type (Form, Quest, ObjectReference) will fail to resolve, and without this the missing-imports banner would
        // send the reader hunting for a mod.
        if (p.VanillaMissing)
            sb.Append("\n⚠ NO vanilla Papyrus sources are on the import path — houseCARL found none beside the compiler " +
                      "(<game>\\Data\\Source\\Scripts)")
              // "found none under your MO2 data folder" would be a claim about a place a failed read never reached,
              // and which of the two it is changes the fix.
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
            // The destination line must match where the .pex actually went: a user-chosen output_dir= is not a
            // houseCARL patch folder and may have no "enable in MO2" step at all. Any deployability caveat for such a
            // target is appended by the caller as deployWarning.
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
            // When the failure is dominated by unresolved-symbol/type errors the likely cause is an incomplete import
            // path, not a bug in the script: one missing framework header cascades into dozens of "unknown type" and
            // "is undefined" lines plus secondary type-mismatch noise that read like code errors. Gated on a
            // two-thirds supermajority and at least three diagnostics, so a near-even split — which could be half real
            // syntax bugs — falls to the generic tail instead. The full diagnostic list prints either way.
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
                // The remedy differs once the modlist has been scanned: "list every dependency's folder" is wrong when
                // dozens are already on the path, and hides the causes the scan cannot fix. When the narrowing was
                // itself incomplete — an unreadable source, or a truncated walk — that is the likely cause, and
                // claiming a complete match would list causes that exclude the real one.
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

            // "diagnostic(s)", not "error(s)": the CK compiler mixes warnings into a failed run's output and the
            // parser does not split severities, so calling them all errors would over-claim.
            sb.Append('\n').Append(r.Diagnostics.Count).Append(" diagnostic(s) (errors and possibly warnings — the CK compiler mixes them):");
            foreach (var d in r.Diagnostics) sb.Append("\n  ").Append(d);
            sb.Append('\n').Append(ImportDetail(plan));   // carries the caveats, warning included
            // The generic tail is for the non-dominated case, a few resolution errors mixed with real ones; after the
            // missing-imports banner it would repeat the import line.
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
