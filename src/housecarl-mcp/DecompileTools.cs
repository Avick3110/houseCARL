using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Pex;

namespace HousecarlMcp;

/// <summary>housecarl_decompile_script — reconstructs Papyrus source from a .pex into a patch folder's Source\Scripts or the <c>out_path=</c> folder; contracts in docs/architecture/papyrus.md.</summary>
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
         "the literals), so a `= None` default comes back only where a call in the SAME script omitted that " +
         "argument, and a parameter with no such call comes back plain, and comments/blank-line layout are " +
         "gone (docstrings survive). Scripts built by an OPTIMIZING compiler (Caprica) decompile to correct source " +
         "but won't re-produce byte-identical output under the CK compiler — the result says so when optimizer " +
         "patterns are detected, but detection is best-effort: a result WITHOUT the note does not prove the .pex " +
         "came from the CK compiler. Any " +
         "function the engine cannot prove is emitted as a LOUD failure comment with its raw bytecode (the .psc then " +
         "won't compile as-is) — never silently wrong source. Needs houseCARL pointed at your MO2 instance for the " +
         "output folder, except with out_path=, which runs without one; when a piece of the class hierarchy could not " +
         "be read the result says what the hierarchy is instead; no compiler or external tool required.")]
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
        // 1) lane: out_path= supersedes patch=/into= saying so; the note is APPENDED, so a refusal opens with "error:".
        bool chosenOutput = !string.IsNullOrWhiteSpace(out_path);
        bool ignoredLane = chosenOutput && (!string.IsNullOrWhiteSpace(patch) || !string.IsNullOrWhiteSpace(into));
        string LaneNoteOk() => ignoredLane
            ? "\nnote: out_path= was given, so patch=/into= are ignored (the .psc lands in out_path, not a houseCARL patch folder)."
            : "";
        // On a refusal the note claims no destination, and says "nothing was written" only where the sentence does not.
        string LaneNoteFail(bool sayNothingWritten = true) => ignoredLane
            ? "\nnote: out_path= was given, so patch=/into= were ignored" + (sayNothingWritten ? "; nothing was written." : ".")
            : "";

        // 2) the instance, which the default lane needs and out_path= runs without, all it adds being the top-up.
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

        // 4) read the pex via Mutagen; an unreadable one fails loud naming the file.
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

        // 5) output folder, resolved before the hierarchy build so a folder error costs nothing.
        LoadOrderService.RiderFolder rf;
        try
        {
            rf = chosenOutput
                ? LoadOrderService.ResolveExplicitSourceFolder(out_path!)
                : svc.ResolveDecompiledSourceFolder(patch, into);
        }
        catch (InvalidOperationException ex)
        {
            // This refusal is out_path='s own, so the note points at the parameter to fix.
            return "error: " + ex.Message + (ignoredLane
                ? "\nnote: patch=/into= were ignored because out_path= was given, and nothing was written. Fix out_path=, or drop it to write into the patch folder patch=/into= names."
                : "");
        }

        // 6) class hierarchy: the baseline, the mods-tree sources, the input pex and its siblings; every source that
        //    read less than all of itself names the reason at the render (docs/architecture/papyrus.md).
        var hierarchy = svc.ClassParentsForDecompile();
        var edges = new Dictionary<string, string>(hierarchy.Edges, StringComparer.OrdinalIgnoreCase);
        HousecarlCore.PapyrusClassParents.AddFromPex(edges, pexFile);
        var siblingDir = Path.GetDirectoryName(pex)!;
        var pexScan = HousecarlCore.PapyrusClassParents.AddFromPexFolder(edges, siblingDir);
        // The walk sees the input .pex too and it was read at step 4, so it is not one of the siblings counted here.
        var siblingsSeen = Math.Max(pexScan.FilesSeen - 1, pexScan.FilesFailed);
        // Both facts are kept: a count of unreadable files, and a listing that ended early, which can happen together.
        var failedClause = pexScan.FilesFailed > 0
            ? $"{pexScan.FilesFailed} of {siblingsSeen} sibling .pex file(s) in '{siblingDir}' could not be read"
            : null;
        var listingClause =
            pexScan.FolderMissing ? $"the folder '{siblingDir}' is no longer there"
            : pexScan.ListingFailed ? $"the folder '{siblingDir}' could not be listed to the end"
            : null;
        hierarchy = hierarchy with
        {
            SiblingPexMissing = (failedClause, listingClause) switch
            {
                (not null, not null) => $"{failedClause}, and {listingClause}",
                (not null, null) => failedClause,
                (null, not null) => listingClause,
                _ => null,
            },
        };

        // A refused decompile leaves no orphan: an empty fresh folder is deleted, a partial one named, into= alone.
        string Refuse(string msg)
        {
            var left = svc.RemoveOrNameRiderResidue(rf);
            return (left is null ? msg
                : msg + $" The freshly created mod folder at '{left}' still holds partial output — delete it or retry with into=.")
                // Every message reaching here accounts for what landed, so the note only states the lane.
                + LaneNoteFail(sayNothingWritten: false);
        }

        // 7) decompile + write.
        var o = WriteObjects(pexFile, edges, rf.OutputDir);
        if (o.UnnamedObject)
            return Refuse($"error: '{Path.GetFileName(pex)}' carries an unnamed script object. " +
                   (o.Written.Count > 0 ? $"Already written this call: {string.Join(", ", o.Written)}." : "Nothing was written."));
        if (o.EscapingObject is not null)
            return Refuse($"error: '{Path.GetFileName(pex)}' names a script object '{o.EscapingObject}', which is not a plain script name, " +
                   $"so its .psc would not land in '{rf.OutputDir}' — rename the object in the .pex, or decompile a copy that carries a plain name. " +
                   "Nothing was written.");
        if (o.ExistingTarget is not null)
            return Refuse($"error: '{o.ExistingTarget}' already exists — houseCARL never overwrites a source file. " +
                   (chosenOutput
                       ? "Move/delete it, or pass a different out_path=. "
                       : "Move/delete it, or pass a different patch= (or into= another patch folder). ") +
                   (o.Written.Count > 0 ? $"Already written this call: {string.Join(", ", o.Written)}." : "Nothing was written."));

        // 8) render: totals, failures, and the degraded modes the outcome carries.
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
        outSb.Append("\nknown format losses (every decompiler): a parameter default is not in the .pex, so it survives only where a call in the same script omitted the argument — a parameter with no such call comes back with its default missing; comments/layout are gone (docstrings survive).");
        // The destination line must match where the .psc went; an out_path= folder is no patch to recompile into.
        outSb.Append(chosenOutput
            ? "\nthe .psc is in the folder you named with out_path= (path above) — review it, edit it, and recompile with " + ToolNames.CompileScript + "."
            : "\nthe .psc is in a houseCARL patch-mod folder — review it, edit it, and recompile with " + ToolNames.CompileScript + " (into= the same patch).");
        return outSb.ToString() + LaneNoteOk();
    });

    /// <summary>One sentence saying what the class hierarchy IS when a source is missing; two could contradict each other.</summary>
    internal static string HierarchySentence(ClassParents h)
    {
        if (h.BaselineNote is null && h.TopUpMissing is null && h.SiblingPexMissing is null) return "";
        // What a thinner hierarchy costs, said once however many sources are thin.
        const string cost = ", so the source keeps an explicit cast wherever an edge is missing (cosmetic; the source stays correct).";
        // One clause per source: why it is thin, or what it still contributes. The input .pex is always read, so the
        // "is" half is never empty.
        var thin = new List<string>();
        var kept = new List<string>();
        if (h.BaselineNote is not null) thin.Add(h.BaselineNote);
        else kept.Add("the shipped vanilla baseline");
        if (h.TopUpMissing is not null)
        {
            thin.Add($"the mods-tree sources were not all read ({h.TopUpMissing})");
            // TopUpMissing covers a tree that was not read at all AND one whose walk lost some files, so the credit is
            // worded for both: a partial .psc failure never disowns the edges the walk did add.
            kept.Add("the MO2 mods-tree sources that could be read");
        }
        else kept.Add("the MO2 mods-tree sources");
        if (h.SiblingPexMissing is not null)
        {
            thin.Add($"the .pex files beside this one were not all read ({h.SiblingPexMissing})");
            // True whether some siblings were read or none were, so a partial failure never disowns the edges it added.
            kept.Add("what this .pex and the sibling .pex files that could be read declare");
        }
        else kept.Add("what this .pex and the .pex files beside it declare");
        return $"\nnote: {string.Join(", and ", thin)} — the class hierarchy is {string.Join(" plus ", kept)}{cost}";
    }

    /// <summary>The decompile-and-write outcome.</summary>
    public sealed record DecompileOutcome(
        List<string> Written, string? ExistingTarget, bool UnnamedObject,
        int FunctionsTotal, int FunctionsFailed, int OptimizerHints, List<string> Failures,
        string? EscapingObject = null);

    /// <summary>True when a .pex object name is a plain script name, so its .psc lands directly in the output folder;
    /// both separators are checked on every platform. The refusal is pinned by
    /// <c>DecompileOutPathTests.AnObjectNameThatWouldLeaveTheOutputFolderIsRefusedAndNothingIsWritten</c> (#783).</summary>
    static bool IsPlainObjectName(string name) =>
        name.IndexOf('/') < 0 && name.IndexOf('\\') < 0 && name.IndexOf(':') < 0 &&
        name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
        name != "." && name != "..";

    /// <summary>Decompile every object in <paramref name="pexFile"/> and write one .psc per object into
    /// <paramref name="outDir"/>, named for the object as the compiler requires; an existing target stops the call
    /// with that path reported and the file untouched.</summary>
    public static DecompileOutcome WriteObjects(
        PexFile pexFile, IReadOnlyDictionary<string, string>? edges, string outDir)
    {
        var written = new List<string>();
        int totalFns = 0, failedFns = 0, optimizerHints = 0;
        var failures = new List<string>();
        // Every name is checked before the FIRST write, so a bad one last does not refuse with its neighbours on disk.
        foreach (var obj in pexFile.Objects)
            if (!string.IsNullOrWhiteSpace(obj.Name) && !IsPlainObjectName(obj.Name))
                return new(written, null, false, totalFns, failedFns, optimizerHints, failures, obj.Name);

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
            // CreateNew is atomic create-or-fail; the catch wraps the CONSTRUCTOR only, a write fault having to propagate.
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

    /// <summary>A PexFile view carrying one object; the user-flag table is copied across, every flag resolving through it.</summary>
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
