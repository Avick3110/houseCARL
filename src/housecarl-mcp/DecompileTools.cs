using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Pex;

namespace HousecarlMcp;

/// <summary>Reconstructs Papyrus source (.psc) from a compiled .pex over Mutagen's PexFile model via
/// <see cref="HousecarlCore.PapyrusDecompiler"/>; no external tool is needed. The .psc lands in a houseCARL patch-mod
/// folder under the SE-canonical Source\Scripts layout, so decompile, edit and recompile compose — or, with
/// <c>out_path=</c>, straight into a folder the caller names, for a look at a declaration no mod folder is wanted for.
/// A function the engine cannot prove is emitted as a loud failure comment with its raw bytecode; an existing target
/// is refused, never overwritten.</summary>
[McpServerToolType]
public static class DecompileTools
{
    [McpServerTool(Name = ToolNames.DecompileScript, Title = "Decompile a compiled Papyrus script (.pex → .psc)"),
     Description(
         "Decompile a compiled Papyrus script (.pex) back to source (.psc), landing the .psc in a NEW houseCARL " +
         "patch-mod folder you review — or pass out_path= an absolute folder to land it there instead, for a read-only " +
         "look at a declaration you don't want a mod folder for (originals untouched; an existing .psc is never " +
         "overwritten). Pass pex= the full " +
         "path to the .pex — for a script inside a BSA, extract it first with " + ToolNames.BsaExtract + " and pass the " +
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
         "output folder, except with out_path=, which runs without one and says when the class hierarchy is the " +
         "vanilla baseline only; no compiler or external tool required.")]
    public static string DecompileScript(
        LoadOrderService svc,
        [Description("Full path to the .pex compiled script to decompile. For a script inside a BSA, run " + ToolNames.BsaExtract + " first and pass the extracted path.")]
            string pex,
        [Description("Optional. Base name for the NEW patch-mod folder the .psc lands in (default 'houseCARL_Scripts'); auto-suffixed if taken.")]
            string? patch = null,
        [Description("Optional. Filename of an existing houseCARL patch mod to add the .psc into instead of creating a fresh folder (accumulate sources; pairs with " + ToolNames.CompileScript + "'s into=). Found by the plugin's filename even if you've renamed its MO2 mod folder; for two patches sharing a filename, pass the mod-folder name here instead (folder & plugin names need not match).")]
            string? into = null,
        [Description("Optional. Land the .psc in a folder of YOUR choosing instead of a houseCARL patch folder — an ABSOLUTE path, created if it doesn't exist. The .psc is written straight into it (nothing appended: a .psc is source a compiler reads, not a file the game loads), so the folder is yours and houseCARL never deletes it. When set, patch=/into= are ignored, and the result says so; this lane also works with no MO2 instance configured.")]
            string? out_path = null) => Guard.Tool(ToolNames.DecompileScript, () =>
    {
        // 1) lane: out_path= supersedes patch=/into= and says so rather than ignoring them silently — the same rule
        //    the compile and .seq lanes carry. The note is APPENDED, so a refusal still opens with "error:".
        bool chosenOutput = !string.IsNullOrWhiteSpace(out_path);
        bool ignoredLane = chosenOutput && (!string.IsNullOrWhiteSpace(patch) || !string.IsNullOrWhiteSpace(into));
        string LaneNoteOk() => ignoredLane
            ? "\nnote: out_path= was given, so patch=/into= are ignored (the .psc lands in out_path, not a houseCARL patch folder)."
            : "";
        // The ignored-lane note rides a refusal too — a refusal is when a caller re-reads their parameters — but a
        // refusal claims no destination for a .psc it did not write, and adds "nothing was written" only where the
        // sentence it follows does not already say so.
        string LaneNoteFail(bool sayNothingWritten = true) => ignoredLane
            ? "\nnote: out_path= was given, so patch=/into= were ignored" + (sayNothingWritten ? "; nothing was written." : ".")
            : "";

        // 2) the instance. The default lane writes into a mod folder under it, so it must be configured. out_path=
        //    writes entirely outside it and runs without one, as bsa_extract's out_path lane does: all the instance
        //    adds to a decompile is the class hierarchy's mods-tree top-up, and a missing top-up costs explicit
        //    casts, never wrong source. What the hierarchy actually is gets stated at the render, off what the
        //    hierarchy build reports rather than off whether an instance is configured — an instance can be
        //    configured and still not resolve.
        if (!chosenOutput && svc.ConfigPromptOrNull() is { } cfgPrompt) return cfgPrompt;

        // 3) validate the pex path.
        if (string.IsNullOrWhiteSpace(pex))
            return "error: no pex given. Pass pex= the full path to the .pex file to decompile." + LaneNoteFail();
        pex = pex.Trim().Trim('"');
        if (!File.Exists(pex))
            return $"error: no such file: '{pex}'. Pass the full path to the .pex (for a BSA member, {ToolNames.BsaExtract} it first)." + LaneNoteFail();
        if (!pex.EndsWith(".pex", StringComparison.OrdinalIgnoreCase))
            return $"error: '{Path.GetFileName(pex)}' is not a .pex compiled script." + LaneNoteFail();
        pex = Path.GetFullPath(pex);

        // 4) read the pex via Mutagen. An unreadable one fails loud naming the file.
        PexFile pexFile;
        try { pexFile = PexFile.CreateFromFile(pex, GameCategory.Skyrim); }
        catch (Exception ex)
        {
            return $"error: Mutagen cannot read '{Path.GetFileName(pex)}' ({ex.GetType().Name}: {ex.Message}). " +
                   "This is the known unreadable-pex class (corrupt, non-standard, or obfuscated) — no output was written."
                   + LaneNoteFail(sayNothingWritten: false);
        }
        if (pexFile.Objects.Count == 0)
            return $"error: '{Path.GetFileName(pex)}' contains no script objects — no output was written."
                   + LaneNoteFail(sayNothingWritten: false);

        // 5) output folder: out_path= a folder the caller owns, else folder-per-patch with the Source\Scripts subdir.
        //    Resolved before the hierarchy build so a folder-resolution error costs nothing and the instance paths
        //    are derived before the cached walk.
        LoadOrderService.RiderFolder rf;
        try
        {
            rf = chosenOutput
                ? LoadOrderService.ResolveExplicitSourceFolder(out_path!)
                : svc.ResolveDecompiledSourceFolder(patch, into);
        }
        catch (InvalidOperationException ex)
        {
            // This refusal is out_path='s own, so the note points at the parameter to fix rather than steering back
            // to the lanes out_path= superseded.
            return "error: " + ex.Message + (ignoredLane
                ? "\nnote: patch=/into= were ignored because out_path= was given, and nothing was written. Fix out_path=, or drop it to write into the patch folder patch=/into= names."
                : "");
        }

        // 6) class hierarchy: the vanilla baseline plus the mods-tree sources, topped up with the input pex itself
        //    and its sibling .pex files (every pex declares its own parent). Soft input — missing pieces mean
        //    explicit casts in the output, never wrong code — and whatever is missing is named at the render.
        var hierarchy = svc.ClassParentsForDecompile();
        var edges = new Dictionary<string, string>(hierarchy.Edges, StringComparer.OrdinalIgnoreCase);
        HousecarlCore.PapyrusClassParents.AddFromPex(edges, pexFile);
        try { HousecarlCore.PapyrusClassParents.AddFromPexFolder(edges, Path.GetDirectoryName(pex)!); }
        catch { /* fewer edges, never fatal */ }

        // A refused decompile leaves no orphan: an empty fresh folder is deleted, one holding partial .psc output is
        // kept and named, and an into= reuse is left alone.
        string Refuse(string msg)
        {
            var left = svc.RemoveOrNameRiderResidue(rf);
            return (left is null ? msg
                : msg + $" The freshly created mod folder at '{left}' still holds partial output — delete it or retry with into=.")
                // Every message reaching here already accounts for what landed, so the note only states the lane.
                + LaneNoteFail(sayNothingWritten: false);
        }

        // 7) decompile + write.
        var o = WriteObjects(pexFile, edges, rf.OutputDir);
        if (o.UnnamedObject)
            return Refuse($"error: '{Path.GetFileName(pex)}' carries an unnamed script object. " +
                   (o.Written.Count > 0 ? $"Already written this call: {string.Join(", ", o.Written)}." : "Nothing was written."));
        if (o.ExistingTarget is not null)
            return Refuse($"error: '{o.ExistingTarget}' already exists — houseCARL never overwrites a source file. " +
                   (chosenOutput
                       ? "Move/delete it, or pass a different out_path=. "
                       : "Move/delete it, or pass a different patch= (or into= another patch folder). ") +
                   (o.Written.Count > 0 ? $"Already written this call: {string.Join(", ", o.Written)}." : "Nothing was written."));

        // 8) render: totals, failures, and every degraded mode named.
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
        outSb.Append(HierarchySentence(hierarchy));
        outSb.Append("\nknown format losses (every decompiler): parameter defaults are baked at call sites; comments/layout are gone (docstrings survive).");
        // The destination line must match where the .psc actually went: an out_path= folder is the caller's, with no
        // patch to recompile back into.
        outSb.Append(chosenOutput
            ? "\nthe .psc is in the folder you named with out_path= (path above) — review it, edit it, and recompile with " + ToolNames.CompileScript + "."
            : "\nthe .psc is in a houseCARL patch-mod folder — review it, edit it, and recompile with " + ToolNames.CompileScript + " (into= the same patch).");
        return outSb.ToString() + LaneNoteOk();
    });

    /// <summary>One sentence saying what the class hierarchy IS whenever a source of it is missing, and nothing when
    /// both are there. One sentence rather than one note per source: the baseline and the mods-tree top-up can be
    /// missing together, and two notes then contradict each other about what was read.</summary>
    internal static string HierarchySentence(ClassParents h)
    {
        const string tail = " (cosmetic: some implicit casts may render explicitly; the source stays correct).";
        if (h.BaselineNote is null && h.TopUpMissing is null) return "";
        if (h.BaselineNote is not null && h.TopUpMissing is not null)
            return $"\nnote: the class hierarchy is only what this .pex and the .pex files beside it declare — {h.BaselineNote}, " +
                   $"and the mods-tree sources were not read ({h.TopUpMissing}){tail}";
        if (h.BaselineNote is not null)
            return $"\nnote: the class hierarchy is the mods-tree sources only — {h.BaselineNote}{tail}";
        return $"\nnote: the class hierarchy is the shipped vanilla baseline only — the mods-tree sources were not read ({h.TopUpMissing}){tail}";
    }

    /// <summary>The decompile-and-write outcome.</summary>
    public sealed record DecompileOutcome(
        List<string> Written, string? ExistingTarget, bool UnnamedObject,
        int FunctionsTotal, int FunctionsFailed, int OptimizerHints, List<string> Failures);

    /// <summary>Decompile every object in <paramref name="pexFile"/> and write one .psc per object into
    /// <paramref name="outDir"/>, named for the object, because the compiler requires filename == ScriptName. Never
    /// overwrites: an existing target stops the call with that path reported and the file untouched.</summary>
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
            // CreateNew is atomic create-or-fail, so "never overwrites" holds even against a file that appeared
            // between the cheap check above and this write. The catch wraps the CONSTRUCTOR ONLY: once the create
            // succeeded the file exists because we made it, so a write-phase IOException (disk full, device error)
            // caught here would mis-report as "already exists" over our own partial file and must propagate instead.
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

    /// <summary>A PexFile view carrying one object, so a multi-object pex emits one .psc per object while reusing
    /// <see cref="HousecarlCore.PapyrusDecompiler.DecompileFile"/> unchanged; a single-object file passes through
    /// as-is. The user-flag table must be copied across: it maps bit to name per file and the engine resolves every
    /// Hidden/Conditional through it, so an empty table would silently drop those flags.</summary>
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
