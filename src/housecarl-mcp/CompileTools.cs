using System.ComponentModel;
using System.Text;
using HousecarlCore;
using ModelContextProtocol.Server;

namespace HousecarlMcp;

/// <summary>
/// houseCARL compile rider — housecarl_compile_script. Drives the Creation Kit's PapyrusCompiler.exe (via
/// <see cref="HousecarlCore.PapyrusCompile"/>; NOT Mutagen) to turn a .psc into a .pex, and lands the .pex in a reviewable
/// houseCARL patch-mod folder (originals untouched — same folder-per-patch model as every other write). It rides the
/// external-tool bridge: the compiler path comes from <see cref="ToolPathResolver"/> (auto-prompts via the forcing
/// function if unset). The structured pass/fail return — per-line {file,line,col,message} diagnostics — is the point: it
/// feeds the AI fix-loop (compile-fail → look the symbol up with the papyrus-reference skill → fix the .psc → recompile).
/// </summary>
[McpServerToolType]
public static class CompileTools
{
    /// <summary>The compiler's import path plus the PROVENANCE of every entry — assembled once by
    /// <see cref="PlanImports"/> and carried to <see cref="Render"/>, which prints it (issue #200: the old signature
    /// took the dir list and dropped it on the floor, so the caller was never told what was actually searched — the one
    /// fact that explains both a missing dependency and a shadowed script).
    /// <para>The summary counts are DERIVED from <see cref="Entries"/>, never tracked alongside it: dedup and the
    /// vanilla-last re-slot both change what survives, so a count computed from the inputs would quietly disagree with
    /// the list printed beneath it.</para></summary>
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
        /// <summary>Provenance labels — the exact strings <see cref="ImportDetail"/> prints, and the keys the counts group on.</summary>
        public const string OwnFolder = "the script's own folder";
        public const string Vanilla = "vanilla sources";
        public const string CallerDirs = "import_dirs=";
        public const string AutoPrefix = "MO2: ";

        /// <summary>The ordered list handed to the compiler.</summary>
        public IReadOnlyList<string> Dirs { get; } = Entries.Select(e => e.Dir).ToList();

        /// <summary>Dirs that came from the caller (import_dirs= / import_set=) and survived into the final path.</summary>
        public int CallerCount => Entries.Count(e => e.Origin == CallerDirs);

        /// <summary>The providing mod folders behind the entries that occupy an auto-discovered SLOT — i.e. that are
        /// on the path because the scan put them there. A folder the caller also passed takes the caller slot instead
        /// and is absent here by design.</summary>
        public IReadOnlyList<string> AutoProviders =>
            Entries.Where(e => e.Origin.StartsWith(AutoPrefix, StringComparison.Ordinal))
                   .Select(e => e.Origin[AutoPrefix.Length..]).ToList();

        /// <summary>The mods the reference WALK matched — the scan's own conclusion, independent of which slot each
        /// folder ended up in. This is the number the summary and the missing-imports banner quote, because the
        /// question they ask ("what does this script reference?") is not the question the slots answer ("why is this
        /// folder on the path?"). Deriving it from <see cref="Entries"/> instead made a folder the caller had also
        /// passed drop out of the count, so a genuine match rendered as "matched the 0 this script REFERENCES BY NAME".
        /// The one count in this record NOT derived from the entries, and deliberately so.</summary>
        public IReadOnlyList<string> Referenced { get; } = ReferencedProviders ?? Array.Empty<string>();
    }

    [McpServerTool(Name = "housecarl_compile_script", Title = "Compile a Papyrus script (.psc → .pex)"),
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
            string? output_dir = null) => Guard.Tool("housecarl_compile_script", () =>
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

        // 3) the compiler — bridge forcing function if unset (returns the trained prompt to surface). Pass the auto-detect
        // HINTS (the CK installs its compiler under <game>\Papyrus Compiler\): the load order's own game dir first, then the
        // located real Steam SE install — so a normal Steam+CK install AND the common Stock-Game setup (CK lives in the Steam
        // install, not the copy MO2 points at) both resolve with no prompt; a total miss names where houseCARL looked (6.2).
        if (bridge.RequireOrPrompt(ToolDependency.PapyrusCompiler, out var compilerExe, svc.CompilerGameDirHints()) is { } toolPrompt) return toolPrompt;

        // 4) CALLER extras = import_dirs= then the named set (#200). An unknown set is REFUSED and names the saved sets
        // rather than silently compiling with a shorter path — not having to check is the whole point of a named set.
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

        // 5) persist the set BEFORE compiling: a set is a path list, worth keeping whether or not this particular script
        // compiles. A save FAILURE is reported, never swallowed (Q3 — "it worked this session, it won't survive a restart").
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

        // 6) the import path — the script's own folder, the caller's extras, the modlist's own source folders, vanilla last.
        // The scan is skipped when auto_imports=false — which is what that flag PROMISES, and its only real use: it is
        // what you reach for when the scan is slow or the profile is pathological, and an unconditional call would take
        // that away while three render strings still said it hadn't run. The one exception is the rare branch where the
        // compiler has no vanilla sources beside it: then the modlist is read purely to LOCATE them, because the
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
        // the vanilla-only branch it explains a missing vanilla slot, which the VanillaMissing caveat would otherwise
        // report without the cause the code already has in hand.
        var plan = PlanImports(script, scriptDir, compilerExe!, callerExtras, autoRoots, auto_imports, importSetName,
                               autoWarning, gameDataSources, scanFailed);

        // The whole path travels as ONE `-i=` argument and Windows caps a process command line (~32k chars), so a modlist
        // with hundreds of source folders could cross it. Refuse LOUDLY with the way out rather than letting the process
        // start fail with something unreadable (Q3).
        var joinedLength = string.Join(";", plan.Dirs).Length;
        if (joinedLength > 30_000)
            return $"error: the assembled import path is too long for one compiler command line ({plan.Dirs.Count} dirs, " +
                   $"{joinedLength} chars; the limit is about 32000). Re-run with auto_imports=false and pass only the " +
                   "dependencies this script needs via import_dirs= (save_import_set= will keep that list for next time).";

        // 7) output folder — output_dir= names a USER-OWNED location (append Scripts\, never a houseCARL patch folder; 6.3),
        // else the default folder-per-patch with its Scripts\ subdir. output_dir= wins; patch_name=/into= are then ignored
        // (surfaced, not silent — Q3).
        LoadOrderService.RiderFolder rf;
        string? deployWarning = null, outputNote = null;
        if (!string.IsNullOrWhiteSpace(output_dir))
        {
            if (!string.IsNullOrWhiteSpace(patch_name) || !string.IsNullOrWhiteSpace(into))
                outputNote = "note: output_dir= was given, so patch_name=/into= are ignored (the .pex lands in output_dir, not a houseCARL patch folder).";
            try { rf = svc.ResolveExplicitScriptFolder(output_dir, out deployWarning); }
            // …carrying the ignored-lane note onto the REFUSAL, the parity write_seq's own fold argued for and this
            // lane was missing (review round 3): a refusal is exactly when a caller re-reads their parameters, and
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
            // Q3: never report a clean "done" for a .pex that won't deploy from where output_dir= put it.
            if (deployWarning is not null) rendered += "\n" + deployWarning;
        }
        else
        {
            // A failed compile produced no .pex — clean up a genuinely-empty fresh folder, name a partial one, leave an
            // into= reuse alone (hunt H2, Aaron's delete-if-empty). An output_dir= folder is USER-OWNED (CreatedFresh=false),
            // so RemoveOrNameRiderResidue returns null for it by construction — never delete-if-empty a user dir.
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

    /// <summary>Whether the rider must read the MO2 modlist at all. True when the scan was asked for, and — even when
    /// it was declined — when the compiler has no vanilla sources beside it, because the modlist's own
    /// <c>Data\Source\Scripts</c> is then the only place left to find them and a path with no vanilla resolves nothing.
    /// <para>A NAMED rule rather than an inline condition because it is the difference between honouring
    /// <c>auto_imports=false</c> and quietly ignoring it: the scan forces the asset build and probes two layouts under
    /// every enabled mod (~7,200 directory probes on the 3,617-mod order this work measured), which is exactly the cost
    /// that flag exists to let a caller avoid.</para></summary>
    internal static bool NeedsModlistScan(bool autoImports, string compilerExe)
        => autoImports || VanillaSourceDir(compilerExe) is null;

    /// <summary>The vanilla Papyrus sources shipped with the game the COMPILER belongs to
    /// (&lt;game&gt;\Papyrus Compiler\PapyrusCompiler.exe → &lt;game&gt;\Data\Source\Scripts, which also holds the flags
    /// file), or null if that folder isn't there. ONE definition, used both by <see cref="BuildImports"/> (which pins it
    /// last) and by the labelling in <see cref="PlanImports"/> (which must recognise the same folder) — derived twice they
    /// could disagree, and the render would then label the vanilla slot as a mod.</summary>
    public static string? VanillaSourceDir(string compilerExe)
    {
        var gameRoot = Path.GetDirectoryName(Path.GetDirectoryName(compilerExe));
        if (gameRoot is null) return null;
        var vanilla = Path.Combine(gameRoot, "Data", "Source", "Scripts");
        return Directory.Exists(vanilla) ? vanilla : null;
    }

    /// <summary>
    /// Assemble the compiler's import-directory list. The CK compiler resolves each referenced script
    /// to the FIRST matching .psc across these directories in order, so order is semantics: the
    /// script's own folder first, then CALLER extras, then the modlist's own AUTO-DISCOVERED source
    /// folders (#200 — passed in MO2 priority order, so a higher-priority mod's copy wins exactly as it
    /// does for every other file in the VFS), then the vanilla sources LAST.
    /// Vanilla last is load-bearing: mods ship EXTENDED copies of
    /// vanilla sources (SKSE's Actor.psc/Game.psc/Form.psc above all) — ranked above the caller's
    /// dirs, the vanilla copy wins and every call to an extended function fails "not a function or
    /// does not exist" despite the user passing the right folder (the PEX bulk gate hit this twice;
    /// spike findings §5.12). Exposed as the import-order-guard probe's seam.
    /// </summary>
    public static List<string> BuildImports(string scriptDir, string compilerExe, string? import_dirs,
                                            IReadOnlyList<string>? autoDirs = null, string? resolvedVanilla = null)
    {
        var imports = new List<string> { scriptDir };
        imports.AddRange(SplitDirs(import_dirs));
        if (autoDirs is not null) imports.AddRange(autoDirs);
        // resolvedVanilla, when given, IS the vanilla dir — already decided by the caller, not a second opinion to
        // reconcile. PlanImports resolves it once (compiler-relative first, else the modlist's own Data\Source\Scripts,
        // which the scan split out precisely to stand in here) and hands the answer down. Deriving it independently in
        // both places is exactly the drift VanillaSourceDir's own summary warns about: they would disagree, and the
        // plan would then report a vanilla slot the assembled path does not have — or the reverse.
        var vanilla = resolvedVanilla ?? VanillaSourceDir(compilerExe);
        if (vanilla is not null)
        {
            // The auto-added vanilla dir is authoritative-LAST: a caller re-passing it (defensively, not
            // knowing it's auto-added) — or the modlist scan reaching it, since the game's Data folder is
            // itself a VFS loose root and Data\Source\Scripts IS this folder — must not pin it into an
            // earlier slot and resurrect the shadowing; Distinct keeps the FIRST occurrence. (When the
            // script ITSELF lives in the vanilla folder, that slot is the own-folder slot and stays.)
            imports.RemoveAll(d => d.Equals(vanilla, StringComparison.OrdinalIgnoreCase)
                                   && !d.Equals(scriptDir, StringComparison.OrdinalIgnoreCase));
            imports.Add(vanilla);
        }
        return imports.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Drive <see cref="BuildImports"/> and LABEL each surviving dir with where it came from, so the render can
    /// print the searched path with provenance. Labelling reads the FINAL list, not the inputs — dedup and the vanilla
    /// re-slot both drop entries, and labels built from the inputs would describe a path that wasn't used.
    /// Label precedence matters wherever a dir belongs to two origins: the script's own folder wins (it is routinely a
    /// mod's own Source\Scripts, and it holds the first slot), then vanilla (the game's Data folder is also a loose root,
    /// so the scan finds it — that is the vanilla slot, not a mod), then a discovered mod, then the caller.</summary>
    internal static ImportPlan PlanImports(
        string targetScript, string scriptDir, string compilerExe, IReadOnlyList<string> callerExtras,
        IReadOnlyList<PapyrusSourceRoot> autoRoots, bool autoEnabled, string? importSetName, string? warning,
        string? gameDataSources = null, bool scanFailed = false)
    {
        // The compiler's own game dir first — its sources are the ones matching the flags file it will use — then the
        // modlist's Data\Source\Scripts, which the scan split out precisely so it could stand in here.
        var vanilla = VanillaSourceDir(compilerExe) ?? gameDataSources;

        // NARROW the scan to what this script reaches. Handing over every folder a modlist ships is not a heavier
        // version of the same thing — measured on a real 3617-mod order it is 501 folders / ~40,200 chars, past the
        // ~32,767 a Windows command line can carry, so the compile could not run at all. It is also wrong on the
        // merits: those folders are overwhelmingly quest and follower mods shipping their own scripts, which nothing
        // else references. See PapyrusDependencyFilter. Vanilla is held OUT of the candidates (it is appended last
        // unconditionally, so indexing it could only make the closure walk the base game for no gain).
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

        // Providers are indexed from the KEPT folders only. A candidate the filter DROPPED can still reach the final
        // list — but only because the caller passed it — so indexing every discovered root would label it as one the
        // scan contributed.
        var keptSet = new HashSet<string>(autoDirs, StringComparer.OrdinalIgnoreCase);
        var providers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in candidates) if (keptSet.Contains(r.Dir)) providers.TryAdd(r.Dir, r.Provider);
        var callerSet = new HashSet<string>(callerExtras, StringComparer.OrdinalIgnoreCase);

        var entries = new List<(string Dir, string Origin)>(dirs.Count);
        foreach (var d in dirs)
        {
            // CALLER outranks the scan here, mirroring the slot BuildImports gave it. A dir the caller passed keeps
            // its caller identity even when the scan also found it — otherwise the two derived counts both go wrong
            // at once (CallerCount misses it, AutoProviders claims it), and the number the reader is asked to audit
            // ("kept the N this script REFERENCES BY NAME") over-counts by the overlap. That overlap is not exotic: it
            // is the recovery path this tool prints, where a failure tells the user to pass a folder via import_dirs=.
            string origin =
                d.Equals(scriptDir, StringComparison.OrdinalIgnoreCase) ? ImportPlan.OwnFolder
                : vanilla is not null && d.Equals(vanilla, StringComparison.OrdinalIgnoreCase) ? ImportPlan.Vanilla
                : callerSet.Contains(d) ? ImportPlan.CallerDirs
                : providers.TryGetValue(d, out var mod) ? ImportPlan.AutoPrefix + mod
                : ImportPlan.CallerDirs;
            entries.Add((d, origin));
        }

        // The scan's OWN answer, kept separate from the slot labels. A folder can be both matched by the walk and
        // passed explicitly by the caller; it then takes the caller SLOT (it would be on the path with the scan off),
        // but it is still something the script references — deriving the referenced count from the labels made that
        // folder vanish from it, so a genuine match rendered as "matched the 0 this script REFERENCES BY NAME". The
        // two questions are answered by two fields rather than one doing double duty.
        var referenced = scan is null
            ? (IReadOnlyList<string>)Array.Empty<string>()
            : scan.Folders.Select(f => providers.TryGetValue(f, out var m) ? m : Path.GetFileName(f)).ToList();

        return new ImportPlan(entries, autoEnabled, importSetName, warning, candidates.Count, scan, referenced,
                              VanillaMissing: vanilla is null, ScanFailed: scanFailed);
    }

    /// <summary>The one-line "what was actually searched" summary printed on EVERY call (#200 — the old render took the
    /// import list and never showed it, so neither a missing dependency nor a shadowed script could be diagnosed from
    /// the output). Provider names are display-capped at 8 with a "+N more" tail; the TOTAL is always stated, so a long
    /// list reads as abbreviated rather than as everything there was.</summary>
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
            // "matched 0 of 0" is a CONCLUSION, and a read that threw never reached one. An empty root list from a
            // failure looks exactly like a modlist with no source folders, which is why the failure is flagged rather
            // than inferred from the count.
            sb.Append("; the modlist could NOT be read, so none of your installed mods' source folders were scanned");
        else
        {
            // Report the NARROWING, not just the survivors: "12 auto-discovered" reads as "your modlist ships 12
            // source folders", which is wrong by a factor of forty and hides the one decision worth auditing when a
            // dependency turns up missing — that the other 489 were dropped as unreferenced, not overlooked.
            // The WALK's count, not the slot count — a folder the caller also passed is still one the script
            // references. No arithmetic is claimed between this and the import_dirs= clause: a folder can be in both,
            // and the only total stated is the dir count at the front.
            var provs = p.Referenced;
            sb.Append("; the modlist scan matched ").Append(provs.Count).Append(" of ").Append(p.AutoScanned)
              // "referenced by this script" is a claim about the source's CONTENTS. It is only earned when the source
              // was actually read: an unreadable target yields the same empty result, and asserting it there would be
              // a confident wrong answer rather than a degraded one.
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

    /// <summary>The FULL ordered import path with provenance — printed on a FAILURE, where "which folders did it look
    /// in, in what order" is exactly the question the diagnostics raise and the summary line cannot answer.</summary>
    internal static string ImportDetail(ImportPlan p)
    {
        var sb = new StringBuilder("import path searched, in order (the compiler takes the FIRST match):");
        for (int i = 0; i < p.Entries.Count; i++)
            sb.Append("\n  ").Append((i + 1).ToString().PadLeft(2)).Append(". ")
              .Append(p.Entries[i].Dir).Append("   [").Append(p.Entries[i].Origin).Append(']');
        sb.Append(ImportCaveats(p));
        return sb.ToString();
    }

    /// <summary>Everything that makes the printed path LESS than a complete answer. Emitted by BOTH
    /// <see cref="ImportSummary"/> and <see cref="ImportDetail"/> — i.e. on success and on failure — because these
    /// caveats are written for the failing run: the truncation note literally ends "if the compile fails on unresolved
    /// symbols, pass that folder via import_dirs=", and it previously could only print on a compile that succeeded.
    /// Living inside the two import blocks (rather than being appended per branch) is what makes that structural: every
    /// path that prints an import path prints its caveats, including the no-parseable-diagnostics branch that used to
    /// drop the discovery warning entirely.</summary>
    internal static string ImportCaveats(ImportPlan p)
    {
        var sb = new StringBuilder();
        // No vanilla sources ANYWHERE — neither beside the compiler nor under the modlist's data dir. Every vanilla
        // type (Form, Quest, ObjectReference…) will fail to resolve, and the missing-imports banner would otherwise
        // send the reader hunting for a mod. This state was reachable silently for one commit, when the game-Data
        // split dropped the only copy on the machine and nothing replaced it (PR #296 re-review).
        if (p.VanillaMissing)
            sb.Append("\n⚠ NO vanilla Papyrus sources are on the import path — houseCARL found none beside the compiler " +
                      "(<game>\\Data\\Source\\Scripts)")
              // "found none under your MO2 data folder" would be a second claim about a place the failed read never
              // reached. Which of the two it is changes the fix, so it is said rather than smoothed over.
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
            // The destination line must match where the .pex actually went (Q3 — don't claim a houseCARL patch folder for a
            // user-chosen output_dir=, where there may be no "enable in MO2" step at all). Any deployability caveat for an
            // output_dir= target is appended by the caller as deployWarning.
            sb.Append(userChoseOutputDir
                ? "the .pex is in the output folder you chose (path above)."
                : "the .pex is in a houseCARL patch-mod folder — enable it in MO2 to use it.");
            sb.Append('\n').Append(ImportSummary(plan));
            if (r.Diagnostics.Count > 0)   // a .pex WAS produced but the compiler emitted notes → surface them as warnings
            {
                sb.Append('\n').Append(r.Diagnostics.Count).Append(" warning(s) (the .pex compiled anyway):");
                foreach (var d in r.Diagnostics) sb.Append("\n  ").Append(d);
            }
            return sb.ToString();
        }

        // failed — this run wrote no .pex (a previous build, if any, is left untouched for the user to keep or delete)
        sb.Append("compile FAILED: ").Append(r.ObjectName).Append(".psc — no new .pex produced (any previous build is left unchanged).");
        if (r.Diagnostics.Count > 0)
        {
            // MISSING-IMPORTS LEAD (HCBR-2026-06-25): when the failure is DOMINATED by unresolved-symbol/type errors, the
            // overwhelmingly likely cause is an incomplete import path — NOT a bug in the script (a single missing framework
            // header cascades into dozens of "unknown type / is undefined" lines, plus secondary type-mismatch noise, that
            // read like code errors). Surface that FIRST so the AI fixes the import path instead of "fixing" correct code.
            // Gated on a >=2/3 SUPERMAJORITY of the diagnostics AND a real count (>=3): a genuine missing import is
            // overwhelmingly unresolved (~all of the ~360 in the report), so a near-EVEN split — which could be half real
            // syntax bugs — must NOT earn the confident "not a bug" banner (it falls to the generic tail instead). The full
            // diagnostic list prints either way, so a missed banner only costs the lead hint, never the errors themselves.
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
                // The remedy DIFFERS once the modlist has already been scanned: "list every dependency's folder" is the
                // wrong instruction when dozens are already on the path, and it hides the causes the scan cannot fix (#200).
                // When the narrowing was itself INCOMPLETE — the source unreadable, or the walk truncated — the cause
                // is most likely the narrowing, and saying "kept the N this script references" would assert a complete
                // job and then list causes that exclude the real one. The caveats print with the path below.
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
                      "e.g. with housecarl_bsa_extract), or one keeping them in a SUBFOLDER of a listed dir. Pass that folder " +
                      "via import_dirs= (and save_import_set= to keep it for next time).");
            }

            // "diagnostic(s)", not "error(s)": the CK compiler mixes warnings into a failed run's output and the
            // parser doesn't split severities — labelling them all errors over-claims (2026-06-12 hunt render wave).
            sb.Append('\n').Append(r.Diagnostics.Count).Append(" diagnostic(s) (errors and possibly warnings — the CK compiler mixes them):");
            foreach (var d in r.Diagnostics) sb.Append("\n  ").Append(d);
            sb.Append('\n').Append(ImportDetail(plan));   // carries the caveats (ImportCaveats), warning included
            // The generic tail stays for the NON-dominated case (a few resolution errors mixed with real ones); when we
            // already led with the strong missing-imports banner, don't repeat the import line.
            sb.Append("\nfix the .psc and recompile (look unfamiliar functions/types up with the papyrus-reference skill).");
            if (!dominatedByMissingImports)
                sb.Append(" If a dependency type is 'not found', its source folder may be missing from the import path listed above — pass it via import_dirs=.");
        }
        else
        {
            // no parseable diagnostics — surface the raw compiler output rather than a silent empty failure (Q3)
            sb.Append("\nthe compiler reported failure but no per-line diagnostics were parsed. Raw output:");
            if (r.Stderr.Trim().Length > 0) sb.Append("\n[stderr] ").Append(r.Stderr.Trim());
            if (r.Stdout.Trim().Length > 0) sb.Append("\n[stdout] ").Append(r.Stdout.Trim());
            sb.Append('\n').Append(ImportDetail(plan));
        }
        return sb.ToString();
    }
}
