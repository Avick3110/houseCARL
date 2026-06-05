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
         "houseCARL patch-mod folder you review and enable in MO2 (originals untouched). Pass script= the full path to the " +
         ".psc to compile; houseCARL adds the script's own folder and the vanilla source folder (derived from the compiler's " +
         "game dir) to the import path automatically — pass import_dirs= (';'-separated) for any extra dependency sources " +
         "(SKSE, other mods). On a compile FAILURE it returns the per-line errors as 'name(line,col): message' so you can fix " +
         "the .psc and recompile (look unfamiliar functions up with the papyrus-reference skill); on SUCCESS it returns the " +
         ".pex path. Needs houseCARL pointed at your MO2 instance (for the output folder) and the Papyrus compiler path — if " +
         "the compiler isn't set yet, houseCARL tells you exactly what to ask for and how to set it. The CK compiler ships " +
         "with the vanilla Steam game install, NOT a Wabbajack 'Stock Game' copy.")]
    public static string CompileScript(
        LoadOrderService svc,
        ToolPathResolver bridge,
        [Description("Full path to the .psc source file to compile.")]
            string script,
        [Description("Optional. Extra import directories where dependency sources (.psc) live — SKSE, other mods — separated by ';'. The script's own folder and the vanilla source folder are added automatically.")]
            string? import_dirs = null,
        [Description("Optional. Base name for the NEW patch-mod folder the .pex lands in (default 'houseCARL_Scripts'); auto-suffixed if taken.")]
            string? patch_name = null,
        [Description("Optional. Filename of an existing houseCARL patch mod to add the .pex into instead of creating a fresh folder (accumulate compiled scripts).")]
            string? into = null)
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

        // 3) the compiler — bridge forcing function if unset (returns the trained prompt to surface).
        if (bridge.RequireOrPrompt(ToolDependency.PapyrusCompiler, out var compilerExe) is { } toolPrompt) return toolPrompt;

        // 4) import dirs: the script's own folder + the vanilla sources (derived from the compiler's game dir:
        //    <game>\Papyrus Compiler\PapyrusCompiler.exe → <game>\Data\Source\Scripts, which also holds the flags file) + caller extras.
        var imports = new List<string> { scriptDir };
        var gameRoot = Path.GetDirectoryName(Path.GetDirectoryName(compilerExe!));
        if (gameRoot is not null)
        {
            var vanilla = Path.Combine(gameRoot, "Data", "Source", "Scripts");
            if (Directory.Exists(vanilla)) imports.Add(vanilla);
        }
        if (!string.IsNullOrWhiteSpace(import_dirs))
            foreach (var d in import_dirs.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                imports.Add(d.Trim('"'));
        imports = imports.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // 5) output folder (folder-per-patch, Scripts\ subdir).
        string outDir;
        try { outDir = svc.ResolveCompiledScriptFolder(patch_name, into); }
        catch (InvalidOperationException ex) { return "error: " + ex.Message; }

        // 6) compile + render.
        var result = HousecarlCore.PapyrusCompile.CompileObject(compilerExe!, objectName, imports, outDir);
        return Render(result, imports);
    }

    static string Render(HousecarlCore.CompileResult r, IReadOnlyList<string> imports)
    {
        var sb = new StringBuilder();
        if (!r.Ran) return "error: " + r.RunError;   // the compiler couldn't be run at all

        if (r.Success)
        {
            sb.Append("compile OK: ").Append(r.ObjectName).Append(".psc → ").Append(r.PexPath).Append('\n');
            sb.Append("the .pex is in a houseCARL patch-mod folder — enable it in MO2 to use it.");
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
            sb.Append('\n').Append(r.Diagnostics.Count).Append(" error(s):");
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
