using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;

namespace HousecarlMcp;

/// <summary>List, extract and repack Bethesda .bsa archives via <see cref="HousecarlCore.BsaArchive"/>. Repack
/// drives BSArch, whose path comes from <see cref="ToolPathResolver"/>; extract and repack write into a new
/// houseCARL mod folder and originals are untouched (docs/architecture/assets.md).</summary>
[McpServerToolType]
public static class BsaTools
{
    [McpServerTool(Name = ToolNames.BsaList, ReadOnly = true, Title = "List a .bsa archive's contents"),
     Description(
         "List the files inside a Bethesda .bsa archive. Returns the archive format + the contained file paths. " +
         "Read-only — extracts nothing. Reads the archive directly (via Mutagen) — no external tool needed. To read a " +
         "file's CONTENTS, use " + ToolNames.BsaExtract + " then read the file.")]
    public static string BsaList(
        [Description("Full path to the .bsa archive to list.")]
            string archive,
        [Description(UnderText)]
            string[]? under = null,
        [Description("Optional. Return only the file count, of the under= matches when given.")]
            bool counts_only = false,
        [Description("Optional. Max characters before the file list is cut with an explicit notice. 0 = the server default (~80k).")]
            int max_chars = 0) => Guard.Tool(ToolNames.BsaList, () =>
    {
        if (string.IsNullOrWhiteSpace(archive)) return "error: no archive given. Pass the full path to the .bsa.";
        try { archive = Path.GetFullPath(archive.Trim().Trim('"')); }
        catch (Exception ex) { return $"error: '{archive}' is not a usable path ({ex.Message})."; }
        if (!File.Exists(archive)) return $"error: no such file: '{archive}'.";
        if (Keep(under, out var keep, out var selectors) is { } bad) return bad;

        var r = HousecarlCore.BsaArchive.List(archive);
        if (!r.Ran) return "error: " + r.RunError;
        if (!r.Success) return "error: " + r.Raw;   // header-vs-reader file-count mismatch (possible corruption)

        var files = keep is null ? r.Files : r.Files.Where(f => keep(f.Replace('/', '\\'))).ToList();
        int cap = max_chars > 0 ? max_chars : 80_000;
        var sb = new StringBuilder();
        sb.Append(Path.GetFileName(archive)).Append("  [").Append(r.Format ?? "unknown format").Append("]  ")
          .Append(r.DeclaredCount).Append(" file(s)");
        if (keep is not null) sb.Append(", ").Append(files.Count).Append(" matching under=");
        AssetWire.AppendSelectorNotes(sb, Dead(selectors, r.Files, archive), RenderCap.For(cap, 0));
        if (counts_only) return sb.ToString().TrimEnd('\n');
        if (sb[^1] != '\n') sb.Append('\n');
        int shown = 0;
        foreach (var f in files)
        {
            if (sb.Length >= cap) { sb.Append("  ... [").Append(files.Count - shown).Append(" more omitted at max_chars=").Append(cap).Append("]\n"); break; }
            sb.Append("  ").Append(f).Append('\n'); shown++;
        }
        return sb.ToString().TrimEnd('\n');
    });

    const string UnderText =
        "Optional. Archive path(s) or glob(s) to keep, as asset_status under=: forward or back slashes, any case; " +
        "'*' within one segment, '?' one character, '**' across separators; a plain path keeps that file or " +
        "everything beneath that folder.";

    /// <summary>The under= selectors as one predicate over archive paths, null when none were given, or the refusal.</summary>
    static string? Keep(string[]? under, out Func<string, bool>? keep, out List<(string Sel, Func<string, bool> Test)> tests)
    {
        keep = null;
        tests = new();
        var given = (under ?? Array.Empty<string>()).Select(u => (u ?? "").Trim()).Where(u => u.Length > 0).ToList();
        if (given.Count == 0) return under is { Length: > 0 } ? "error: under= holds only empty selectors. Pass an archive path or glob, e.g. 'scripts/**/*.pex'." : null;
        foreach (var sel in given)
        {
            try { tests.Add((sel, HousecarlCore.AssetGlob.Matcher(sel))); }
            catch (ArgumentException ex) { return $"error: under '{sel}': {ex.Message}"; }
        }
        var all = tests;
        keep = p => all.Any(t => t.Test(p));
        return null;
    }

    /// <summary>A note per selector that matched no file in the archive, in asset_status's shape, so a typo beside a live selector is not lost.</summary>
    static List<string> Dead(List<(string Sel, Func<string, bool> Test)> tests, IReadOnlyList<string> files, string archive) =>
        tests.Where(t => !files.Any(f => t.Test(f.Replace('/', '\\'))))
             .Select(t => $"under '{t.Sel}' matched no file in '{Path.GetFileName(archive)}' — check the spelling.")
             .ToList();

    [McpServerTool(Name = ToolNames.BsaExtract, Title = "Extract a .bsa archive to a folder"),
     Description(
         "Extract a Bethesda .bsa archive's contents, or with under= only the matching files, to a folder so you can read the files. Reads the archive " +
         "directly, compressed archives included; no external tool needed. Without out_path= it unpacks into a new mod " +
         "folder under your mods directory, which needs houseCARL pointed at your MO2 instance. The archive is never " +
         "modified.")]
    public static string BsaExtract(
        LoadOrderService svc,
        [Description("Full path to the .bsa archive to extract.")]
            string archive,
        [Description("Optional. Absolute path to the folder to unpack into. Omit for a new houseCARL mod folder under your mods directory; its path is reported.")]
            string? out_path = null,
        [Description("Optional. Extract only the files matching these, as " + ToolNames.BsaList + " under=; matching none is refused.")]
            string[]? under = null) => Guard.Tool(ToolNames.BsaExtract, () =>
    {
        if (string.IsNullOrWhiteSpace(archive)) return "error: no archive given. Pass the full path to the .bsa.";
        try { archive = Path.GetFullPath(archive.Trim().Trim('"')); }
        catch (Exception ex) { return $"error: '{archive}' is not a usable path ({ex.Message})."; }
        if (!File.Exists(archive)) return $"error: no such file: '{archive}'.";
        if (Keep(under, out var keep, out var selectors) is { } bad) return bad;
        // Matched before any folder is cut, so an empty match refuses with nothing written.
        var matched = new StringBuilder();
        if (keep is not null)
        {
            var listed = HousecarlCore.BsaArchive.List(archive);
            if (!listed.Ran) return "error: " + listed.RunError;
            if (!listed.Success) return "error: " + listed.Raw;
            int hits = listed.Files.Count(f => keep(f.Replace('/', '\\')));
            if (hits == 0)
                return $"error: under= matched none of the {listed.Files.Count} file(s) in '{Path.GetFileName(archive)}', so nothing was extracted; check the pattern with {ToolNames.BsaList} under=.";
            matched.Append(hits).Append(" of ").Append(listed.Files.Count).Append(" file(s) matched under=.");
            AssetWire.AppendSelectorNotes(matched, Dead(selectors, listed.Files, archive), RenderCap.For(80_000, 0));
            if (matched[^1] != '\n') matched.Append('\n');
        }

        string target;
        bool managed = string.IsNullOrWhiteSpace(out_path);
        if (managed)
        {
            if (svc.ConfigPromptOrNull() is { } cfg) return cfg;   // need ModsDir for the default managed folder
            // Extract names the folder it left behind on failure rather than deleting it, unlike repack below.
            try { target = svc.ResolvePatchModFolder(Path.GetFileNameWithoutExtension(archive) + " (extracted)", into: null, "houseCARL_Extract", naming: null).OutputDir; }
            catch (InvalidOperationException ex) { return "error: " + ex.Message; }
        }
        else
        {
            var given = out_path!.Trim().Trim('"');
            if (PathArguments.NotAbsolute(given, "out_path", "the folder to unpack into", "C:\\work\\extracted") is { } notAbsolute)
                return "error: " + notAbsolute;
            // An absolute path can still be unusable (an embedded NUL, or past the OS length limit); named here rather than thrown at the guard.
            try { target = Path.GetFullPath(given); }
            catch (Exception ex) { return $"error: out_path '{given}' is not a usable path ({ex.Message})."; }
        }

        string residue = managed ? $"\nThe freshly created mod folder was left at '{target}' — delete it or retry into it." : "";
        var r = HousecarlCore.BsaArchive.Unpack(archive, target, keep);
        if (!r.Ran) return "error: " + r.RunError + residue;   // archive couldn't be opened/read
        if (!r.Success)                                          // path-traversal refusal or a mid-extract error
            return "extract FAILED: " + r.Raw + residue;

        var sb = new StringBuilder();
        sb.Append("extracted ").Append(Path.GetFileName(archive)).Append(" → ").Append(target).Append('\n');
        sb.Append(matched).Append(r.Raw).Append('\n');   // e.g. "extracted 5826 file(s)."
        sb.Append(managed
            ? "(a new houseCARL mod folder — read the files you need from it; enable it in MO2 only if you want the loose files in your load order.)"
            : "(read the files you need from that folder.)");
        return sb.ToString();
    });

    /// <summary>How the repack lane names its mod folder, for the into= not-found refusal. That name is load-bearing
    /// — the game auto-loads an archive only as &lt;activePluginBasename&gt;.bsa — so a taken stem the caller passed
    /// refuses here rather than being suffixed; a defaulted stem is still suffixed.</summary>
    internal static readonly OutputLocations.RiderNaming RepackNaming = new(
        "patch",
        new OutputLocations.StemRefusal(
            "the .bsa (the game auto-loads an archive only under its plugin's exact basename)",
            "Remove it in MO2, pass patch= a different name, or pass into= to place the archive in an existing houseCARL patch folder."),
        Noun: ".bsa");

    [McpServerTool(Name = ToolNames.BsaRepack, Title = "Pack a folder into a .bsa archive"),
     Description(
         "Pack a folder of loose files into a Bethesda .bsa archive with BSArch, written into a new houseCARL mod " +
         "folder under your mods directory, or with into= an existing houseCARL patch; enable it in MO2 to use it. The " +
         "source folder is untouched. The .bsa takes its mod folder's name without the 'houseCARL - ' prefix.")]
    public static string BsaRepack(
        LoadOrderService svc,
        ToolPathResolver bridge,
        [Description("Full path to the source folder of loose files to pack (its tree becomes the archive's contents).")]
            string source_folder,
        [Description("Optional. Name for the new mod folder and the .bsa inside it: patch='MyArchive' writes " +
            "'houseCARL - MyArchive\\MyArchive.bsa'. The game loads an archive only under its plugin's exact basename. " +
            "Default: the source folder's name, which gets a _001-style suffix if that name is already taken (a " +
            "'houseCARL - <name>' mod folder exists, or <name>.esp is active); a taken name you pass is refused, not " +
            "suffixed.")]
            string? patch = null,
        [Description("Optional. Archive format: 'sse' (default, Skyrim SE), 'tes5' (Skyrim LE), 'fo4', 'fo4dds', 'sf1', 'sf1dds', 'tes4', 'fo3', 'fnv', 'tes3'.")]
            string? format = null,
        [Description("Optional, default false. Compress the archive. Compression breaks any sounds or voices in it, so leave it false if the folder contains audio.")]
            bool compress = false,
        [Description("Optional. An existing houseCARL patch to place the .bsa into instead of a new folder; the archive takes that folder's name without the 'houseCARL - ' prefix. Pass the patch's plugin filename (found even if you renamed its MO2 folder), or the mod-folder name when two patches share a filename.")]
            string? into = null) => Guard.Tool(ToolNames.BsaRepack, () =>
    {
        if (string.IsNullOrWhiteSpace(source_folder)) return "error: no source_folder given.";
        source_folder = Path.GetFullPath(source_folder.Trim().Trim('"'));
        if (!Directory.Exists(source_folder)) return $"error: no such folder: '{source_folder}'.";
        if (svc.ConfigPromptOrNull() is { } cfg) return cfg;
        if (bridge.RequireOrPrompt(ToolDependency.Bsarch, out var bsarch) is { } prompt) return prompt;

        // patch= names the mod FOLDER and the .bsa inside takes that folder's name. A caller who spells the archive
        // itself means that name, so the extension is stripped rather than doubled on the file.
        OutputLocations.RiderFolder rf;
        var stem = patch?.Trim().Trim('"');
        if (stem is not null && stem.EndsWith(".bsa", StringComparison.OrdinalIgnoreCase)) stem = stem[..^4];
        // The resolver refuses patch= beside into= before any folder is cut.
        try { rf = svc.ResolvePatchModFolder(stem, into, new DirectoryInfo(source_folder).Name, RepackNaming); }
        catch (InvalidOperationException ex) { return "error: " + ex.Message; }
        var folder = rf.OutputDir;
        var name = rf.Stem + ".bsa";

        // On any post-allocation failure: an empty fresh folder is deleted, a partial .bsa is kept and named, and a reused into= folder is left alone.
        string Refuse(string msg)
        {
            var left = svc.RemoveOrNameRiderResidue(rf);
            return left is null ? msg
                : msg + $"\nThe freshly created mod folder at '{left}' still holds a partial archive — delete it or retry with into=.";
        }

        var archive = Path.Combine(folder, name);
        // An archive already at that path is REFUSED, never replaced: on the into= lane every repack resolves to the
        // same filename, and a silent overwrite loses the first archive's contents.
        if (File.Exists(archive))
            return Refuse($"error: '{name}' already exists in that mod folder ('{archive}') — houseCARL won't replace an "
                        + "archive it did not just write. Delete it, or repack into a different folder.");
        // An unknown format token refuses: a typo like 'fo4dd' must not silently pack -sse.
        var fmtFlag = HousecarlCore.BsaArchive.TryFormatFlag(format);
        if (fmtFlag is null)
            return Refuse($"error: unknown format '{format}'. Legal tokens: {HousecarlCore.BsaArchive.FormatTokens}.");
        var r = HousecarlCore.BsaArchive.Pack(bsarch!, source_folder, archive, fmtFlag, compress);
        if (!r.Ran) return Refuse("error: " + r.RunError);
        if (r.CountError is { } mismatch) return Refuse("error: " + mismatch);
        if (!r.Success)
            return Refuse($"repack FAILED: no .bsa written at '{archive}'. Raw BSArch output:\n" + r.Raw + RootSkipNote(r.RootSkipped));

        return PackReport(r, name, archive, fmtFlag, compress);
    });

    /// <summary>The success message for a repack: how many files the archive holds, where it landed, and any root-level files BSArch dropped.</summary>
    internal static string PackReport(HousecarlCore.BsaPackResult r, string name, string archive, string fmtFlag, bool compress)
    {
        var sb = new StringBuilder();
        sb.Append("packed ");
        if (r.Packed is { } packed) sb.Append(packed).Append(" file(s) into ");
        sb.Append(name)
          .Append(" (").Append(fmtFlag.TrimStart('-')).Append(compress ? ", compressed" : ", uncompressed").Append(") → ").Append(archive).Append('\n');
        sb.Append("(a new houseCARL mod folder — enable it in MO2 to use the archive.)");
        // A null count means the format carries no .bsa header, or the header of one that should have been read failed.
        if (r.Packed is null)
            sb.Append(fmtFlag is "-fo4" or "-fo4dds" or "-sf1" or "-sf1dds" or "-tes3"
                ? "\nhouseCARL reads file counts from .bsa headers only, so this archive's contents were not counted or checked against the source."
                : $"\nWARNING: '{name}' was written but houseCARL could not read its .bsa header, so its contents were not counted or checked against the source — list it before relying on it.");
        else if (r.Expected is null)
            sb.Append("\nThe source folder could not be fully scanned, so this archive's contents were not checked against it — list it before relying on it.");
        sb.Append(RootSkipNote(r.RootSkipped));
        if (compress) sb.Append("\nNOTE: compressed — any sounds/voices in it will not work in-game (BSArch limitation).");
        return sb.ToString();
    }

    /// <summary>The sentence naming the source-root files BSArch dropped, or empty when there were none.</summary>
    static string RootSkipNote(IReadOnlyList<string> rootSkipped) =>
        rootSkipped.Count == 0 ? ""
            : $"\n{rootSkipped.Count} file(s) at the source folder's root were NOT archived — BSArch packs only files " +
              "under a subfolder, so move them into one and repack if they belong in the archive.";
}
