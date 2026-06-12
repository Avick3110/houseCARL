using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;

namespace HousecarlMcp;

/// <summary>
/// houseCARL BSA riders — list / extract / repack Bethesda .bsa archives by driving BSArch (via
/// <see cref="HousecarlCore.BsaArchive"/>; Mutagen has no archive surface). All three ride the external-tool bridge: the
/// BSArch path comes from <see cref="ToolPathResolver"/> (auto-prompts via the forcing function if unset — BSArch ships
/// with xEdit). Reading a file INSIDE an archive = extract it, then read it (BSArch has no per-file extract). Extract +
/// repack land their output in a reviewable houseCARL mod folder (folder-per-patch; originals untouched).
/// </summary>
[McpServerToolType]
public static class BsaTools
{
    [McpServerTool(Name = "housecarl_bsa_list", ReadOnly = true, Title = "List a .bsa archive's contents"),
     Description(
         "List the files inside a Bethesda .bsa archive (via BSArch). Returns the archive format + the contained file " +
         "paths. Read-only — extracts nothing. To read a file's CONTENTS, use housecarl_bsa_extract then read the file. " +
         "Needs the BSArch path; if it isn't set yet houseCARL tells you exactly what to ask for and how to set it " +
         "(BSArch ships with xEdit).")]
    public static string BsaList(
        ToolPathResolver bridge,
        [Description("Full path to the .bsa archive to list.")]
            string archive,
        [Description("Optional. Max characters before the file list is cut with an explicit notice. 0 = the server default (~80k).")]
            int max_chars = 0) => Guard.Tool("housecarl_bsa_list", () =>
    {
        if (string.IsNullOrWhiteSpace(archive)) return "error: no archive given. Pass the full path to the .bsa.";
        // GetFullPath: BSArch resolves relative paths against ITS OWN folder (the child's working dir),
        // not the server's — a relative path that passed File.Exists here could list a DIFFERENT
        // same-named archive sitting beside BSArch (2026-06-12 adversarial hunt).
        try { archive = Path.GetFullPath(archive.Trim().Trim('"')); }
        catch (Exception ex) { return $"error: '{archive}' is not a usable path ({ex.Message})."; }
        if (!File.Exists(archive)) return $"error: no such file: '{archive}'.";
        if (bridge.RequireOrPrompt(ToolDependency.Bsarch, out var bsarch) is { } prompt) return prompt;

        var r = HousecarlCore.BsaArchive.List(bsarch!, archive);
        if (!r.Ran) return "error: " + r.RunError;
        if (!r.Success)
            // Covers BOTH unreadable (no "Files: N" at all) and aborted-mid-list (declared N, listed
            // fewer) — the old guard only caught the first, rendering "N file(s)" over an empty list.
            return r.DeclaredCount == 0
                ? $"error: BSArch could not read '{Path.GetFileName(archive)}' as an archive. Raw output:\n" + r.Raw
                : $"error: BSArch declared {r.DeclaredCount} file(s) in '{Path.GetFileName(archive)}' but listed {r.Files.Count} — the listing aborted or the archive is damaged. Raw output:\n" + r.Raw;

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

    [McpServerTool(Name = "housecarl_bsa_extract", Title = "Extract a .bsa archive to a folder"),
     Description(
         "Extract a Bethesda .bsa archive's contents to a folder (via BSArch), so you can read the files. BSArch unpacks " +
         "the WHOLE archive (it has no per-file extract). Pass dest= a folder to unpack into; OMIT dest to let houseCARL " +
         "unpack into a NEW reviewable mod folder under your mods directory (reported back) — that needs houseCARL pointed " +
         "at your MO2 instance. Needs the BSArch path (auto-prompts if unset). Originals are never modified.")]
    public static string BsaExtract(
        LoadOrderService svc,
        ToolPathResolver bridge,
        [Description("Full path to the .bsa archive to extract.")]
            string archive,
        [Description("Optional. Folder to unpack into. If omitted, houseCARL creates a NEW mod folder under your mods directory and reports its path.")]
            string? dest = null) => Guard.Tool("housecarl_bsa_extract", () =>
    {
        if (string.IsNullOrWhiteSpace(archive)) return "error: no archive given. Pass the full path to the .bsa.";
        // GetFullPath: BSArch resolves relative paths against ITS OWN folder, not the server's (see BsaList).
        try { archive = Path.GetFullPath(archive.Trim().Trim('"')); }
        catch (Exception ex) { return $"error: '{archive}' is not a usable path ({ex.Message})."; }
        if (!File.Exists(archive)) return $"error: no such file: '{archive}'.";
        if (bridge.RequireOrPrompt(ToolDependency.Bsarch, out var bsarch) is { } prompt) return prompt;

        string target;
        bool managed = string.IsNullOrWhiteSpace(dest);
        if (managed)
        {
            if (svc.ConfigPromptOrNull() is { } cfg) return cfg;   // need ModsDir for the default managed folder
            try { target = svc.ResolvePatchModFolder(Path.GetFileNameWithoutExtension(archive) + " (extracted)", into: null, "houseCARL_Extract"); }
            catch (InvalidOperationException ex) { return "error: " + ex.Message; }
        }
        else
        {
            target = Path.GetFullPath(dest!.Trim().Trim('"'));
        }

        var r = HousecarlCore.BsaArchive.Unpack(bsarch!, archive, target);
        if (!r.Ran) return "error: " + r.RunError;
        if (!r.Success)
            return $"extract FAILED: '{Path.GetFileName(archive)}' produced no new or changed files in '{target}' this run." +
                   (managed ? $" The freshly created mod folder (with only houseCARL's meta.ini marker) was left at '{target}' — delete it or retry into it." : "") +
                   "\nRaw BSArch output:\n" + r.Raw;

        var sb = new StringBuilder();
        sb.Append("extracted ").Append(Path.GetFileName(archive)).Append(" → ").Append(target).Append('\n');
        sb.Append(managed
            ? "(a new houseCARL mod folder — read the files you need from it; enable it in MO2 only if you want the loose files in your load order.)"
            : "(read the files you need from that folder.)");
        return sb.ToString();
    });

    [McpServerTool(Name = "housecarl_bsa_repack", Title = "Pack a folder into a .bsa archive"),
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
        [Description("Optional. Base name for the NEW mod folder the .bsa lands in (default 'houseCARL_Archive'); auto-suffixed if taken.")]
            string? patch_name = null,
        [Description("Optional. Filename of an existing houseCARL patch mod to place the .bsa into instead of a fresh folder.")]
            string? into = null) => Guard.Tool("housecarl_bsa_repack", () =>
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

        string folder;
        try { folder = svc.ResolvePatchModFolder(patch_name, into, "houseCARL_Archive"); }
        catch (InvalidOperationException ex) { return "error: " + ex.Message; }

        var archive = Path.Combine(folder, name);
        // Unknown format tokens REFUSE (Q3): a typo like 'fo4dd' must not silently pack -sse.
        var fmtFlag = HousecarlCore.BsaArchive.TryFormatFlag(format);
        if (fmtFlag is null)
            return $"error: unknown format '{format}'. Legal tokens: {HousecarlCore.BsaArchive.FormatTokens}.";
        var r = HousecarlCore.BsaArchive.Pack(bsarch!, source_folder, archive, fmtFlag, compress);
        if (!r.Ran) return "error: " + r.RunError;
        if (!r.Success)
            return $"repack FAILED: no .bsa written at '{archive}'. Raw BSArch output:\n" + r.Raw;

        var sb = new StringBuilder();
        sb.Append("packed ").Append(name).Append(" (").Append(fmtFlag.TrimStart('-')).Append(compress ? ", compressed" : ", uncompressed").Append(") → ").Append(archive).Append('\n');
        sb.Append("(a new houseCARL mod folder — enable it in MO2 to use the archive.)");
        if (compress) sb.Append("\nNOTE: compressed — any sounds/voices in it will not work in-game (BSArch limitation).");
        return sb.ToString();
    });
}
