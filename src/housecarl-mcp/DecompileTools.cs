using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Pex;

namespace HousecarlMcp;

/// <summary>
/// houseCARL decompile rider — housecarl_decompile_script. Reconstructs Papyrus source (.psc) from a compiled
/// .pex over Mutagen's PexFile model via <see cref="HousecarlCore.PapyrusDecompiler"/> (gate-proven: 100.0%
/// structuring, 98.80% exact recompile round-trips across 10,189 provable pairs — dev/review bulk-gate doc,
/// 2026-06-12). Needs NO external tool: Mutagen reads the pex natively. The .psc lands in a reviewable
/// houseCARL patch-mod folder (originals untouched), under the SE-canonical Source\Scripts layout so
/// decompile → edit → housecarl_compile_script composes naturally. Q3 discipline end to end: a function the
/// engine can't prove fails LOUD inside the file (named reason + raw bytecode) and in the result; an
/// unreadable pex names the file; an existing target is REFUSED, never overwritten.
/// </summary>
[McpServerToolType]
public static class DecompileTools
{
    [McpServerTool(Name = "housecarl_decompile_script", Title = "Decompile a compiled Papyrus script (.pex → .psc)"),
     Description(
         "Decompile a compiled Papyrus script (.pex) back to source (.psc), landing the .psc in a NEW houseCARL " +
         "patch-mod folder you review (originals untouched; an existing .psc is never overwritten). Pass pex= the full " +
         "path to the .pex — for a script inside a BSA, extract it first with housecarl_bsa_extract and pass the " +
         "extracted path. Names, types, properties, states, events and docstrings survive; control flow is " +
         "reconstructed and proven (98.80% of provable scripts recompile to identical bytecode). KNOWN LOSSES every " +
         "decompiler shares, baked into the PEX format itself: parameter DEFAULTS don't exist in .pex (callers baked " +
         "the literals, so defaulted parameters come back as plain parameters), and comments/blank-line layout are " +
         "gone (docstrings survive). Scripts built by an OPTIMIZING compiler (Caprica) decompile to correct source " +
         "but won't re-produce byte-identical output under the CK compiler — the result says so when optimizer " +
         "patterns are detected, but detection is best-effort: a result WITHOUT the note does not prove the .pex " +
         "came from the CK compiler. Any " +
         "function the engine cannot prove is emitted as a LOUD failure comment with its raw bytecode (the .psc then " +
         "won't compile as-is) — never silently wrong source. Needs houseCARL pointed at your MO2 instance for the " +
         "output folder; no compiler or external tool required.")]
    public static string DecompileScript(
        LoadOrderService svc,
        [Description("Full path to the .pex compiled script to decompile. For a script inside a BSA, run housecarl_bsa_extract first and pass the extracted path.")]
            string pex,
        [Description("Optional. Base name for the NEW patch-mod folder the .psc lands in (default 'houseCARL_Scripts'); auto-suffixed if taken.")]
            string? patch_name = null,
        [Description("Optional. Filename of an existing houseCARL patch mod to add the .psc into instead of creating a fresh folder (accumulate sources; pairs with housecarl_compile_script's into=).")]
            string? into = null) => Guard.Tool("housecarl_decompile_script", () =>
    {
        // 1) MO2 must be configured — the .psc lands under the instance's mods folder.
        if (svc.ConfigPromptOrNull() is { } cfgPrompt) return cfgPrompt;

        // 2) validate the pex path.
        if (string.IsNullOrWhiteSpace(pex))
            return "error: no pex given. Pass pex= the full path to the .pex file to decompile.";
        pex = pex.Trim().Trim('"');
        if (!File.Exists(pex))
            return $"error: no such file: '{pex}'. Pass the full path to the .pex (for a BSA member, housecarl_bsa_extract it first).";
        if (!pex.EndsWith(".pex", StringComparison.OrdinalIgnoreCase))
            return $"error: '{Path.GetFileName(pex)}' is not a .pex compiled script.";
        pex = Path.GetFullPath(pex);

        // 3) read the pex via Mutagen. Unreadable = the known Mutagen-delta class — fail loud naming the file.
        PexFile pexFile;
        try { pexFile = PexFile.CreateFromFile(pex, GameCategory.Skyrim); }
        catch (Exception ex)
        {
            return $"error: Mutagen cannot read '{Path.GetFileName(pex)}' ({ex.GetType().Name}: {ex.Message}). " +
                   "This is the known unreadable-pex class (corrupt, non-standard, or obfuscated) — no output was written.";
        }
        if (pexFile.Objects.Count == 0)
            return $"error: '{Path.GetFileName(pex)}' contains no script objects — no output was written.";

        // 4) class hierarchy: cached vanilla baseline + mods-tree sources, topped up with the input pex itself
        //    and its sibling .pex files (every pex declares its own parent). Soft input — missing pieces mean
        //    explicit casts in the output, never wrong code. A missing/corrupt BASELINE is named in the
        //    result; the runtime top-ups degrade silently to fewer edges (purely cosmetic).
        var (cached, hierarchyNote) = svc.ClassParentsForDecompile();
        var edges = new Dictionary<string, string>(cached, StringComparer.OrdinalIgnoreCase);
        HousecarlCore.PapyrusClassParents.AddFromPex(edges, pexFile);
        try { HousecarlCore.PapyrusClassParents.AddFromPexFolder(edges, Path.GetDirectoryName(pex)!); }
        catch { /* fewer edges, never fatal */ }

        // 5) output folder (folder-per-patch, Source\Scripts subdir) — resolved BEFORE decompiling so a
        //    folder-resolution error costs nothing.
        string outDir;
        try { outDir = svc.ResolveDecompiledSourceFolder(patch_name, into); }
        catch (InvalidOperationException ex) { return "error: " + ex.Message; }

        // 6) decompile + write (the guard-probed seam).
        var o = WriteObjects(pexFile, edges, outDir);
        if (o.UnnamedObject)
            return $"error: '{Path.GetFileName(pex)}' carries an unnamed script object. " +
                   (o.Written.Count > 0 ? $"Already written this call: {string.Join(", ", o.Written)}." : "Nothing was written.");
        if (o.ExistingTarget is not null)
            return $"error: '{o.ExistingTarget}' already exists — houseCARL never overwrites a source file. " +
                   "Move/delete it, or pass a different patch_name= (or into= another patch folder). " +
                   (o.Written.Count > 0 ? $"Already written this call: {string.Join(", ", o.Written)}." : "Nothing was written.");

        // 7) render — Q3: totals, failures, and every degraded mode named.
        var outSb = new StringBuilder();
        outSb.Append("decompile ").Append(o.FunctionsFailed == 0 ? "OK" : "PARTIAL").Append(": ")
             .Append(Path.GetFileName(pex)).Append(" → ").Append(string.Join(", ", o.Written)).Append('\n');
        outSb.Append(o.FunctionsTotal).Append(" function(s)");
        if (o.FunctionsFailed > 0)
        {
            outSb.Append(", ").Append(o.FunctionsFailed).Append(" FAILED — each is a loud comment block with its raw bytecode in the .psc, which will NOT compile as-is:");
            foreach (var f in o.Failures) outSb.Append("\n  FAIL ").Append(f);
        }
        else outSb.Append(", all structured clean.");
        if (o.OptimizerHints > 0)
            outSb.Append("\nnote: optimizer-compiled patterns detected (Caprica class) — source is correct; byte-identity vs the original .pex under the CK compiler is not expected.");
        if (hierarchyNote is not null)
            outSb.Append("\nnote: ").Append(hierarchyNote).Append(" (cosmetic: some implicit casts may render explicitly; the source stays correct).");
        outSb.Append("\nknown format losses (every decompiler): parameter defaults are baked at call sites; comments/layout are gone (docstrings survive).");
        outSb.Append("\nthe .psc is in a houseCARL patch-mod folder — review it, edit it, and recompile with housecarl_compile_script (into= the same patch).");
        return outSb.ToString();
    });

    /// <summary>The decompile-and-write outcome — the seam the decompile-guard probe drives directly.</summary>
    public sealed record DecompileOutcome(
        List<string> Written, string? ExistingTarget, bool UnnamedObject,
        int FunctionsTotal, int FunctionsFailed, int OptimizerHints, List<string> Failures);

    /// <summary>Decompile every object in <paramref name="pexFile"/> and write one .psc per object into
    /// <paramref name="outDir"/>, filename = the OBJECT's name (the compiler's filename==ScriptName rule).
    /// NEVER overwrites: an existing target stops the call with that path reported and the file untouched.
    /// Optimizer-compiled inputs get a loud header note in the .psc itself. Exposed as the decompile-guard
    /// probe's seam (the BuildImports pattern).</summary>
    public static DecompileOutcome WriteObjects(
        PexFile pexFile, IReadOnlyDictionary<string, string>? edges, string outDir)
    {
        var written = new List<string>();
        int totalFns = 0, failedFns = 0, optimizerHints = 0;
        var failures = new List<string>();
        foreach (var obj in pexFile.Objects)
        {
            if (string.IsNullOrWhiteSpace(obj.Name))
                return new(written, null, true, totalFns, failedFns, optimizerHints, failures);

            var target = Path.Combine(outDir, obj.Name + ".psc");
            if (File.Exists(target))   // cheap refusal before any decompile work
                return new(written, target, false, totalFns, failedFns, optimizerHints, failures);

            var r = HousecarlCore.PapyrusDecompiler.DecompileFile(SingleObjectView(pexFile, obj), edges);
            totalFns += r.FunctionsTotal;
            failedFns += r.FunctionsFailed;
            optimizerHints += r.OptimizerHints;
            failures.AddRange(r.Failures);

            var source = r.OptimizerHints > 0
                ? "; NOTE: optimizer-compiled patterns detected (Caprica class) — this source is correct, but the CK\n" +
                  "; compiler will not reproduce the original .pex byte-for-byte (the optimizer's output is not its form).\n"
                  + r.Source
                : r.Source;
            // CreateNew = atomic create-or-fail: "never overwrites" holds even against a file that
            // appeared between the cheap check above and this write. The collision catch wraps the
            // CONSTRUCTOR ONLY: once the create succeeded the file exists BECAUSE WE MADE IT, so a
            // write-phase IOException (disk full, device error) caught here would mis-report as
            // "already exists" over our own partial file — it must propagate with its true name
            // (PR #47 review #2). A CreateNew-constructor IOException with the file present is a
            // collision by API contract.
            FileStream fs;
            try { fs = new FileStream(target, FileMode.CreateNew, FileAccess.Write); }
            catch (IOException) when (File.Exists(target))
            {
                return new(written, target, false, totalFns, failedFns, optimizerHints, failures);
            }
            using (fs)
            using (var w = new StreamWriter(fs))
                w.Write(source);
            written.Add(target);
        }
        return new(written, null, false, totalFns, failedFns, optimizerHints, failures);
    }

    /// <summary>A PexFile view carrying ONE object, so multi-object pex files emit one .psc per object while
    /// reusing <see cref="HousecarlCore.PapyrusDecompiler.DecompileFile"/> unchanged. Single-object files (the
    /// Skyrim norm) pass through as-is. The USER-FLAG TABLE must ride along: it maps bit→name per FILE, and
    /// the engine resolves every Hidden/Conditional through it — a fresh PexFile's table is empty and would
    /// silently drop the flags (PR #47 review must-fix; Conditional is functional, not cosmetic).</summary>
    static PexFile SingleObjectView(PexFile pex, PexObject obj)
    {
        if (pex.Objects.Count == 1) return pex;
        var view = new PexFile(GameCategory.Skyrim)
        {
            MajorVersion = pex.MajorVersion,
            MinorVersion = pex.MinorVersion,
            GameId = pex.GameId,
            CompilationTime = pex.CompilationTime,
            SourceFileName = pex.SourceFileName,
            Username = pex.Username,
            MachineName = pex.MachineName,
            DebugInfo = pex.DebugInfo,
        };
        for (int i = 0; i < pex.UserFlags.Length && i < view.UserFlags.Length; i++)
            view.UserFlags[i] = pex.UserFlags[i];
        view.Objects.Add(obj);
        return view;
    }
}
