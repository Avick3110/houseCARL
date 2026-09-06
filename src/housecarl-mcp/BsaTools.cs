using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;

namespace HousecarlMcp;

/// <summary>List, extract and repack Bethesda .bsa archives via <see cref="HousecarlCore.BsaArchive"/>. Repack drives
/// BSArch, whose path comes from <see cref="ToolPathResolver"/> and prompts if unset. BSArch has no per-file extract,
/// so reading one file inside an archive means extracting the archive first. Extract and repack write into a new
/// houseCARL mod folder; originals are untouched.</summary>
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
        [Description("Optional. Max characters before the file list is cut with an explicit notice. 0 = the server default (~80k).")]
            int max_chars = 0) => Guard.Tool(ToolNames.BsaList, () =>
    {
        if (string.IsNullOrWhiteSpace(archive)) return "error: no archive given. Pass the full path to the .bsa.";
        try { archive = Path.GetFullPath(archive.Trim().Trim('"')); }
        catch (Exception ex) { return $"error: '{archive}' is not a usable path ({ex.Message})."; }
        if (!File.Exists(archive)) return $"error: no such file: '{archive}'.";

        var r = HousecarlCore.BsaArchive.List(archive);
        if (!r.Ran) return "error: " + r.RunError;
        if (!r.Success) return "error: " + r.Raw;   // header-vs-reader file-count mismatch (possible corruption)

        int cap = max_chars > 0 ? max_chars : 80_000;
        var sb = new StringBuilder();
        sb.Append(Path.GetFileName(archive)).Append("  [").Append(r.Format ?? "unknown format").Append("]  ")
          .Append(r.DeclaredCount).Append(" file(s)\n");
        int shown = 0;
        foreach (var f in r.Files)
        {
            if (sb.Length >= cap) { sb.Append("  ... [").Append(r.Files.Count - shown).Append(" more omitted at max_chars=").Append(cap).Append("]\n"); break; }
            sb.Append("  ").Append(f).Append('\n'); shown++;
        }
        return sb.ToString().TrimEnd('\n');
    });

    [McpServerTool(Name = ToolNames.BsaExtract, Title = "Extract a .bsa archive to a folder"),
     Description(
         "Extract a Bethesda .bsa archive's contents to a folder so you can read the files. Reads the archive directly " +
         "(via Mutagen — handles compressed archives too) — no external tool needed. Unpacks the WHOLE archive. Pass " +
         "dest= a folder to unpack into; OMIT dest to let houseCARL unpack into a NEW reviewable mod folder under your " +
         "mods directory (reported back) — that needs houseCARL pointed at your MO2 instance. Originals are never modified.")]
    public static string BsaExtract(
        LoadOrderService svc,
        [Description("Full path to the .bsa archive to extract.")]
            string archive,
        [Description("Optional. Folder to unpack into. If omitted, houseCARL creates a NEW mod folder under your mods directory and reports its path.")]
            string? dest = null) => Guard.Tool(ToolNames.BsaExtract, () =>
    {
        if (string.IsNullOrWhiteSpace(archive)) return "error: no archive given. Pass the full path to the .bsa.";
        try { archive = Path.GetFullPath(archive.Trim().Trim('"')); }
        catch (Exception ex) { return $"error: '{archive}' is not a usable path ({ex.Message})."; }
        if (!File.Exists(archive)) return $"error: no such file: '{archive}'.";

        string target;
        bool managed = string.IsNullOrWhiteSpace(dest);
        if (managed)
        {
            if (svc.ConfigPromptOrNull() is { } cfg) return cfg;   // need ModsDir for the default managed folder
            // Extract names the folder it left behind on failure rather than deleting it, unlike repack below.
            try { target = svc.ResolvePatchModFolder(Path.GetFileNameWithoutExtension(archive) + " (extracted)", into: null, "houseCARL_Extract", naming: null).OutputDir; }
            catch (InvalidOperationException ex) { return "error: " + ex.Message; }
        }
        else
        {
            target = Path.GetFullPath(dest!.Trim().Trim('"'));
        }

        string residue = managed ? $"\nThe freshly created mod folder was left at '{target}' — delete it or retry into it." : "";
        var r = HousecarlCore.BsaArchive.Unpack(archive, target);
        if (!r.Ran) return "error: " + r.RunError + residue;   // archive couldn't be opened/read
        if (!r.Success)                                          // path-traversal refusal or a mid-extract error
            return "extract FAILED: " + r.Raw + residue;

        var sb = new StringBuilder();
        sb.Append("extracted ").Append(Path.GetFileName(archive)).Append(" → ").Append(target).Append('\n');
        sb.Append(r.Raw).Append('\n');   // e.g. "extracted 5826 file(s)."
        sb.Append(managed
            ? "(a new houseCARL mod folder — read the files you need from it; enable it in MO2 only if you want the loose files in your load order.)"
            : "(read the files you need from that folder.)");
        return sb.ToString();
    });

    /// <summary>How the repack lane names its mod folder, for the into= not-found refusal (#357). It names
    /// patch_name= and says why: this tool declares archive_name= as well, and the §5.3 candidate order routes a
    /// bare patch= there — to the .bsa filename, not the folder — so the sibling lanes' patch= sentence would
    /// rename the caller's archive and leave the folder defaulted.</summary>
    public static readonly LoadOrderService.RiderNaming RepackNaming = new(
        "patch_name", "On this tool a bare patch= names the ARCHIVE (archive_name=), not the folder.");

    [McpServerTool(Name = ToolNames.BsaRepack, Title = "Pack a folder into a .bsa archive"),
     Description(
         "Pack a folder of loose files into a Bethesda .bsa archive (via BSArch), placed in a NEW reviewable houseCARL mod " +
         "folder under your mods directory (originals untouched; enable it in MO2 to use). format defaults to 'sse' (Skyrim " +
         "Special Edition). compress defaults to FALSE — a compressed archive is smaller but BREAKS any sounds/voices it " +
         "contains (a BSArch limitation), so only compress archives with no audio. Needs the BSArch path (auto-prompts if " +
         "unset) and houseCARL pointed at your MO2 instance (for the output folder).")]
    public static string BsaRepack(
        LoadOrderService svc,
        ToolPathResolver bridge,
        [Description("Full path to the source folder of loose files to pack (its tree becomes the archive's contents).")]
            string source_folder,
        [Description("Optional. The .bsa filename to create (default: the source folder's name + '.bsa').")]
            string? archive_name = null,
        [Description("Optional. Archive format: 'sse' (default, Skyrim SE), 'tes5' (Skyrim LE), 'fo4', 'fo4dds', 'sf1', 'sf1dds', 'tes4', 'fo3', 'fnv', 'tes3'.")]
            string? format = null,
        [Description("Optional. Compress the archive (default false). WARNING: compression breaks sounds/voices — leave false if the folder contains any audio.")]
            bool compress = false,
        [Description("Optional. Base name for the NEW mod folder the .bsa lands in (default 'houseCARL_Archive'); auto-suffixed if taken. A name another mod folder already holds as a plugin your order is not loading is refused rather than suffixed, naming that folder and file.")]
            string? patch_name = null,
        [Description("Optional. Filename of an existing houseCARL patch mod to place the .bsa into instead of a fresh folder. Found by the plugin's filename even if you've renamed its MO2 mod folder; for two patches sharing a filename, pass the mod-folder name here instead (folder & plugin names need not match).")]
            string? into = null) => Guard.Tool(ToolNames.BsaRepack, () =>
    {
        if (string.IsNullOrWhiteSpace(source_folder)) return "error: no source_folder given.";
        source_folder = Path.GetFullPath(source_folder.Trim().Trim('"'));
        if (!Directory.Exists(source_folder)) return $"error: no such folder: '{source_folder}'.";
        if (svc.ConfigPromptOrNull() is { } cfg) return cfg;
        if (bridge.RequireOrPrompt(ToolDependency.Bsarch, out var bsarch) is { } prompt) return prompt;

        var name = string.IsNullOrWhiteSpace(archive_name)
            ? new DirectoryInfo(source_folder).Name + ".bsa"
            : Path.GetFileName(archive_name!.Trim().Trim('"'));
        if (!name.EndsWith(".bsa", StringComparison.OrdinalIgnoreCase)) name += ".bsa";

        LoadOrderService.RiderFolder rf;
        try { rf = svc.ResolvePatchModFolder(patch_name, into, "houseCARL_Archive", RepackNaming); }
        catch (InvalidOperationException ex) { return "error: " + ex.Message; }
        var folder = rf.OutputDir;

        // On any post-allocation failure: an empty fresh folder is deleted, a partial .bsa is kept and named, and a
        // reused into= folder is left alone.
        string Refuse(string msg)
        {
            var left = svc.RemoveOrNameRiderResidue(rf);
            return left is null ? msg
                : msg + $"\nThe freshly created mod folder at '{left}' still holds a partial archive — delete it or retry with into=.";
        }

        var archive = Path.Combine(folder, name);
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

    /// <summary>The success message for a repack: how many files the archive holds, where it landed, and any root-level
    /// files BSArch dropped.</summary>
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
