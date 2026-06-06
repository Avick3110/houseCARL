using System.Diagnostics;
using System.Text.RegularExpressions;

namespace HousecarlCore;

/// <summary>The parsed result of a BSArch -list. <see cref="RunError"/> non-null ⇒ BSArch couldn't be RUN (bad path /
/// timeout). <see cref="Success"/> means the archive was read and the file list matched its declared count.</summary>
public sealed record BsaListResult(
    bool Success, string? Format, int DeclaredCount, IReadOnlyList<string> Files, string Raw, string? RunError)
{
    public bool Ran => RunError is null;
}

/// <summary>The result of a BSArch unpack/pack. <see cref="RunError"/> non-null ⇒ BSArch couldn't be run; otherwise
/// <see cref="Success"/> is decided by the ARTIFACT (dest has files / the .bsa exists), not the exit code.</summary>
public sealed record BsaResult(bool Success, string Raw, string? RunError)
{
    public bool Ran => RunError is null;
}

/// <summary>
/// Drives BSArch (zilav/ElminsterAU/Sheson; ships with xEdit) to list / unpack / pack Bethesda .bsa archives — the engine
/// behind the housecarl_bsa_* tools. Mutagen has no archive surface, so this wraps the external exe (a bounded
/// ProcessStartInfo + parser). Grounded against the real BSArch v0.9c CLI (measured 2026-06-05):
///   • list  : `BSArch &lt;archive&gt; -list`  → banner + info block (incl. "Files: N"), a blank line, then N file paths.
///   • unpack: `BSArch unpack &lt;archive&gt; &lt;folder&gt; -mt`  (WHOLE archive — BSArch has no per-file extract).
///   • pack  : `BSArch pack &lt;folder&gt; &lt;archive&gt; -sse [-z] -mt`  (-sse = Skyrim SE; -z compresses but BREAKS
///             sounds/voices, so uncompressed is the safe default).
/// Pure (no DI): the build-time probe drives this exact code against real BSArch + real .bsa.
/// </summary>
public static class BsaArchive
{
    static readonly Regex FilesCount = new(@"^\s*Files:\s*(\d+)\s*$", RegexOptions.Multiline | RegexOptions.Compiled);
    static readonly Regex FormatLine = new(@"^\s*Format:\s*(.+?)\s*$", RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>List an archive's contents. Robust parse: BSArch prints "Files: N" in the info block, then the N paths as
    /// the final block — so the file list is the LAST N non-empty lines (immune to banner/info-block variation).</summary>
    public static BsaListResult List(string bsarchExe, string archive, int timeoutMs = 60_000)
    {
        var run = Run(bsarchExe, new[] { archive, "-list" }, timeoutMs);
        if (run.runError is not null)
            return new BsaListResult(false, null, 0, Array.Empty<string>(), run.stdout + run.stderr, run.runError);

        var format = FormatLine.Match(run.stdout) is { Success: true } fm2 ? fm2.Groups[1].Value.Trim() : null;
        var fm = FilesCount.Match(run.stdout);
        if (!fm.Success)   // no "Files: N" ⇒ not a readable archive (BSArch printed an error / usage)
            return new BsaListResult(false, format, 0, Array.Empty<string>(), (run.stdout + "\n" + run.stderr).Trim(), null);

        int declared = int.Parse(fm.Groups[1].Value);
        var nonEmpty = run.stdout.Replace("\r", "").Split('\n').Where(l => l.Trim().Length > 0).ToList();
        var files = declared > 0 && declared <= nonEmpty.Count
            ? nonEmpty.Skip(nonEmpty.Count - declared).ToList()
            : new List<string>();
        return new BsaListResult(files.Count == declared, format, declared, files, run.stdout, null);
    }

    /// <summary>Unpack the WHOLE archive into <paramref name="destFolder"/> (created if absent). Success = the folder has
    /// at least one entry afterwards (BSArch's exit code isn't relied on). "Read a file inside" = unpack, then read it.</summary>
    public static BsaResult Unpack(string bsarchExe, string archive, string destFolder, int timeoutMs = 300_000)
    {
        Directory.CreateDirectory(destFolder);
        var run = Run(bsarchExe, new[] { "unpack", archive, destFolder, "-mt" }, timeoutMs);
        if (run.runError is not null) return new BsaResult(false, (run.stdout + run.stderr).Trim(), run.runError);
        bool ok = Directory.Exists(destFolder) && Directory.EnumerateFileSystemEntries(destFolder).Any();
        return new BsaResult(ok, (run.stdout + "\n" + run.stderr).Trim(), null);
    }

    /// <summary>Pack <paramref name="srcFolder"/> into a .bsa at <paramref name="archive"/> with the given format flag
    /// (e.g. "-sse") and optional compression. NON-DESTRUCTIVE (Aaron 2026-06-06): an existing archive at the target is
    /// NEVER overwritten unless this run successfully packs a new one — BSArch writes to a houseCARL-internal temp beside
    /// the target, and only a clean pack (temp exists, non-empty) is moved over the target; any failure (BSArch error,
    /// timeout, empty output) deletes the temp and leaves the prior .bsa untouched. NOTE the caller must surface BSArch's
    /// caveat: a COMPRESSED archive breaks any sounds/voices it contains.</summary>
    public static BsaResult Pack(string bsarchExe, string srcFolder, string archive, string formatFlag, bool compress, int timeoutMs = 600_000)
    {
        // Pack to a scratch sibling (keeps the .bsa extension so BSArch is happy); the real target is touched only on success.
        var dir = Path.GetDirectoryName(archive) ?? Environment.CurrentDirectory;
        var tmp = Path.Combine(dir, Path.GetFileNameWithoutExtension(archive) + ".houseCARL-tmp.bsa");
        try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* stale internal scratch; best-effort */ }

        var args = new List<string> { "pack", srcFolder, tmp, formatFlag, "-mt" };
        if (compress) args.Add("-z");
        var run = Run(bsarchExe, args, timeoutMs);

        bool packed = run.runError is null && File.Exists(tmp) && new FileInfo(tmp).Length > 0;
        if (!packed)   // BSArch couldn't run, or produced no/empty output — leave any prior archive untouched
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best-effort */ }
            return new BsaResult(false, (run.stdout + "\n" + run.stderr).Trim(), run.runError);
        }

        try { File.Move(tmp, archive, overwrite: true); }   // success → atomically replace the target (same volume = rename)
        catch (Exception ex)
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best-effort */ }
            return new BsaResult(false, (run.stdout + "\n" + run.stderr + $"\ncould not place the packed archive at '{archive}': {ex.Message}").Trim(), null);
        }
        return new BsaResult(File.Exists(archive) && new FileInfo(archive).Length > 0, (run.stdout + "\n" + run.stderr).Trim(), null);
    }

    /// <summary>Map a houseCARL format token to a BSArch flag. Default/unknown → -sse (Skyrim Special Edition, the target).</summary>
    public static string FormatFlag(string? format) => "-" + ((format?.Trim().ToLowerInvariant()) switch
    {
        "tes3" or "morrowind" => "tes3",
        "tes4" or "oblivion" => "tes4",
        "fo3" => "fo3",
        "fnv" => "fnv",
        "tes5" or "le" or "skyrimle" => "tes5",
        "fo4" => "fo4",
        "fo4dds" => "fo4dds",
        "sf1" or "starfield" => "sf1",
        "sf1dds" => "sf1dds",
        _ => "sse",   // sse / ae / skyrimse / null / unknown → Skyrim SE
    });

    static (bool ran, int exit, string stdout, string stderr, string? runError) Run(string exe, IReadOnlyList<string> args, int timeoutMs)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);   // one arg each; .NET quotes spaces/semicolons

        Process p;
        try { p = Process.Start(psi)!; }
        catch (Exception ex) { return (false, -1, "", "", $"could not run BSArch at '{exe}': {ex.Message}"); }

        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return (false, -1, "", "", $"BSArch did not finish within {timeoutMs / 1000}s (killed).");
        }
        return (true, p.ExitCode, o.GetAwaiter().GetResult(), e.GetAwaiter().GetResult(), null);
    }
}
