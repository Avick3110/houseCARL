using System.ComponentModel;
using System.Text;
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
    [McpServerTool(Name = "housecarl_compile_script", Title = "Compile a Papyrus script (.psc → .pex)"),
     Description(
         "Compile a Papyrus script (.psc) to .pex using the Creation Kit's PapyrusCompiler.exe, landing the .pex in a NEW " +
         "houseCARL patch-mod folder you review and enable in MO2 (originals untouched) — or pass output_dir= to land it in a " +
         "folder you choose (houseCARL appends Scripts\\ so MO2 deploys it). Pass script= the full path to the " +
         ".psc to compile; houseCARL adds the script's own folder and the vanilla source folder (derived from the compiler's " +
         "game dir) to the import path automatically — pass import_dirs= (';'-separated) for any extra dependency sources " +
         "(SKSE, other mods); your folders are searched BEFORE the vanilla sources, so mod-extended copies of vanilla " +
         "scripts (SKSE's Actor.psc etc.) win. On a compile FAILURE it returns the per-line errors as 'name(line,col): message' so you can fix " +
         "the .psc and recompile (look unfamiliar functions up with the papyrus-reference skill); on SUCCESS it returns the " +
         ".pex path. Needs houseCARL pointed at your MO2 instance (for the output folder) and the Papyrus compiler path — if " +
         "the compiler isn't set yet, houseCARL tells you exactly what to ask for and how to set it. The CK compiler ships " +
         "with the vanilla Steam game install, NOT a Wabbajack 'Stock Game' copy.")]
    public static string CompileScript(
        LoadOrderService svc,
        ToolPathResolver bridge,
        [Description("Full path to the .psc source file to compile.")]
            string script,
        [Description("Optional. Extra import directories where dependency sources (.psc) live — SKSE, other mods — separated by ';'. The script's own folder and the vanilla source folder are added automatically; your directories are searched before vanilla (first match wins), so extended copies of vanilla scripts take precedence.")]
            string? import_dirs = null,
        [Description("Optional. Base name for the NEW patch-mod folder the .pex lands in (default 'houseCARL_Scripts'); auto-suffixed if taken.")]
            string? patch_name = null,
        [Description("Optional. Filename of an existing houseCARL patch mod to add the .pex into instead of creating a fresh folder (accumulate compiled scripts). Found by the plugin's filename even if you've renamed its MO2 mod folder; for two patches sharing a filename, pass the mod-folder name here instead (folder & plugin names need not match).")]
            string? into = null,
        [Description("Optional. Land the .pex in a folder of YOUR choosing instead of a fresh houseCARL patch folder — pass the mod-folder ROOT; houseCARL appends Scripts\\ (and won't double it if you already point at a ...\\Scripts folder). When set, patch_name=/into= are ignored. If the folder is under neither your MO2 mods folder nor the game's Data, the .pex still compiles but you're warned it won't deploy automatically.")]
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

        // 4) import dirs — assembled by BuildImports (the guard-probed seam).
        var imports = BuildImports(scriptDir, compilerExe!, import_dirs);

        // 5) output folder — output_dir= names a USER-OWNED location (append Scripts\, never a houseCARL patch folder; 6.3),
        // else the default folder-per-patch with its Scripts\ subdir. output_dir= wins; patch_name=/into= are then ignored
        // (surfaced, not silent — Q3).
        LoadOrderService.RiderFolder rf;
        string? deployWarning = null, outputNote = null;
        if (!string.IsNullOrWhiteSpace(output_dir))
        {
            if (!string.IsNullOrWhiteSpace(patch_name) || !string.IsNullOrWhiteSpace(into))
                outputNote = "note: output_dir= was given, so patch_name=/into= are ignored (the .pex lands in output_dir, not a houseCARL patch folder).";
            try { rf = svc.ResolveExplicitScriptFolder(output_dir, out deployWarning); }
            catch (InvalidOperationException ex) { return "error: " + ex.Message; }
        }
        else
        {
            try { rf = svc.ResolveCompiledScriptFolder(patch_name, into); }
            catch (InvalidOperationException ex) { return "error: " + ex.Message; }
        }

        // 6) compile + render.
        var result = HousecarlCore.PapyrusCompile.CompileObject(compilerExe!, objectName, imports, rf.OutputDir);
        var rendered = Render(result, imports, userChoseOutputDir: !string.IsNullOrWhiteSpace(output_dir));
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
        if (outputNote is not null) rendered = outputNote + "\n" + rendered;
        return rendered;
    });

    /// <summary>
    /// Assemble the compiler's import-directory list. The CK compiler resolves each referenced script
    /// to the FIRST matching .psc across these directories in order, so order is semantics: the
    /// script's own folder first, then CALLER extras, then the vanilla sources LAST (derived from the
    /// compiler's game dir: &lt;game&gt;\Papyrus Compiler\PapyrusCompiler.exe → &lt;game&gt;\Data\Source\Scripts,
    /// which also holds the flags file). Vanilla last is load-bearing: mods ship EXTENDED copies of
    /// vanilla sources (SKSE's Actor.psc/Game.psc/Form.psc above all) — ranked above the caller's
    /// dirs, the vanilla copy wins and every call to an extended function fails "not a function or
    /// does not exist" despite the user passing the right folder (the PEX bulk gate hit this twice;
    /// spike findings §5.12). Exposed as the import-order-guard probe's seam.
    /// </summary>
    public static List<string> BuildImports(string scriptDir, string compilerExe, string? import_dirs)
    {
        var imports = new List<string> { scriptDir };
        if (!string.IsNullOrWhiteSpace(import_dirs))
            foreach (var d in import_dirs.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                imports.Add(d.Trim('"'));
        var gameRoot = Path.GetDirectoryName(Path.GetDirectoryName(compilerExe));
        if (gameRoot is not null)
        {
            var vanilla = Path.Combine(gameRoot, "Data", "Source", "Scripts");
            if (Directory.Exists(vanilla))
            {
                // The auto-added vanilla dir is authoritative-LAST: a caller re-passing it (defensively,
                // not knowing it's auto-added) must not pin it into the caller slot and resurrect the
                // shadowing — Distinct keeps the FIRST occurrence. (When the script ITSELF lives in the
                // vanilla folder, that slot is the own-folder slot and stays.)
                imports.RemoveAll(d => d.Equals(vanilla, StringComparison.OrdinalIgnoreCase)
                                       && !d.Equals(scriptDir, StringComparison.OrdinalIgnoreCase));
                imports.Add(vanilla);
            }
        }
        return imports.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    internal static string Render(HousecarlCore.CompileResult r, IReadOnlyList<string> imports, bool userChoseOutputDir)
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
            // "diagnostic(s)", not "error(s)": the CK compiler mixes warnings into a failed run's output and the
            // parser doesn't split severities — labelling them all errors over-claims (2026-06-12 hunt render wave).
            sb.Append('\n').Append(r.Diagnostics.Count).Append(" diagnostic(s) (errors and possibly warnings — the CK compiler mixes them):");
            foreach (var d in r.Diagnostics) sb.Append("\n  ").Append(d);
            sb.Append("\nfix the .psc and recompile (look unfamiliar functions/types up with the papyrus-reference skill). " +
                      "If a dependency type is 'not found', its source folder may be missing from the import path — pass it via import_dirs=.");
        }
        else
        {
            // no parseable diagnostics — surface the raw compiler output rather than a silent empty failure (Q3)
            sb.Append("\nthe compiler reported failure but no per-line diagnostics were parsed. Raw output:");
            if (r.Stderr.Trim().Length > 0) sb.Append("\n[stderr] ").Append(r.Stderr.Trim());
            if (r.Stdout.Trim().Length > 0) sb.Append("\n[stdout] ").Append(r.Stdout.Trim());
        }
        return sb.ToString();
    }
}
