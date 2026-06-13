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

/// <summary>The result of a BSArch unpack/pack. <see cref="RunError"/> non-null ⇒ the operation never ran BSArch
/// (bad path / timeout / a stuck stale scratch refused up front); otherwise <see cref="Success"/> is decided by
/// THIS-RUN artifact provenance (unpack: entries added or changed since the pre-run snapshot; pack: a fresh,
/// non-empty scratch written at/after the run baseline), never by the exit code alone.</summary>
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

    /// <summary>Unpack the WHOLE archive into <paramref name="destFolder"/> (created if absent). Success = THIS RUN
    /// added or changed entries, not "the folder is non-empty afterwards": the managed flow PRE-SEEDS the folder (the
    /// meta.ini ownership marker is written before BSArch runs) and a caller-supplied dest may hold anything, so the
    /// old test was satisfiable with zero extracted files and reported every BSArch failure as a successful extract
    /// (2026-06-12 adversarial hunt, MUST-FIX). New entries are detected by PATH (robust to extractors that restore
    /// archived timestamps); changed ones by size/mtime. Honest residual edge: re-extracting byte-identical content
    /// over an existing dest with restored timestamps can read as "nothing new" — that direction fails LOUD with
    /// BSArch's raw output attached, never falsely succeeds. "Read a file inside" = unpack, then read it.</summary>
    public static BsaResult Unpack(string bsarchExe, string archive, string destFolder, int timeoutMs = 300_000)
    {
        Directory.CreateDirectory(destFolder);
        var before = SnapshotEntries(destFolder);
        var run = Run(bsarchExe, new[] { "unpack", archive, destFolder, "-mt" }, timeoutMs);
        if (run.runError is not null) return new BsaResult(false, (run.stdout + run.stderr).Trim(), run.runError);
        bool ok = AnyNewOrChangedEntries(destFolder, before);
        return new BsaResult(ok, (run.stdout + "\n" + run.stderr).Trim(), null);
    }

    /// <summary>Snapshot a folder's files (recursive): relative path → (size, mtimeUtc). The Unpack provenance
    /// baseline — and the bsa-contract-guard probe's seam.</summary>
    public static Dictionary<string, (long Size, DateTime MtimeUtc)> SnapshotEntries(string folder)
    {
        var map = new Dictionary<string, (long, DateTime)>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(folder)) return map;
        foreach (var f in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            var fi = new FileInfo(f);
            map[Path.GetRelativePath(folder, f)] = (fi.Length, fi.LastWriteTimeUtc);
        }
        return map;
    }

    /// <summary>Did anything appear or change under <paramref name="folder"/> since <paramref name="before"/>?
    /// A path absent from the baseline = new (timestamp-independent); a present path with a different size or
    /// mtime = changed.</summary>
    public static bool AnyNewOrChangedEntries(string folder, Dictionary<string, (long Size, DateTime MtimeUtc)> before)
    {
        if (!Directory.Exists(folder)) return false;
        foreach (var f in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(folder, f);
            if (!before.TryGetValue(rel, out var b)) return true;
            var fi = new FileInfo(f);
            if (fi.Length != b.Size || fi.LastWriteTimeUtc != b.MtimeUtc) return true;
        }
        return false;
    }

    /// <summary>Pack <paramref name="srcFolder"/> into a .bsa at <paramref name="archive"/> with the given format flag
    /// (e.g. "-sse") and optional compression. NON-DESTRUCTIVE (Aaron 2026-06-06): an existing archive at the target is
    /// NEVER overwritten unless this run successfully packs a new one — BSArch writes to a houseCARL-internal temp beside
    /// the target, and only a clean pack THIS RUN (temp exists, non-empty, AND written at/after the run's mtime baseline)
    /// is moved over the target; a stale scratch from a previous run that cannot be removed REFUSES up front (nothing
    /// runs, the prior .bsa untouched), and any failure (BSArch error, timeout, empty output, stale-mtime scratch)
    /// deletes the temp and leaves the prior .bsa untouched. The mtime gate assumes an NTFS-class timestamp resolution —
    /// on a FAT-class target a same-second pack could read as stale and fail LOUD (never falsely succeed). NOTE the
    /// caller must surface BSArch's caveat: a COMPRESSED archive breaks any sounds/voices it contains.</summary>
    public static BsaResult Pack(string bsarchExe, string srcFolder, string archive, string formatFlag, bool compress, int timeoutMs = 600_000)
    {
        // Pack to a scratch sibling (keeps the .bsa extension so BSArch is happy); the real target is touched only on success.
        var dir = Path.GetDirectoryName(archive) ?? Environment.CurrentDirectory;
        var tmp = Path.Combine(dir, Path.GetFileNameWithoutExtension(archive) + ".houseCARL-tmp.bsa");
        try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* checked next — a stuck scratch refuses loud */ }
        if (File.Exists(tmp))
            // A stale scratch from a previous run that we cannot remove: packing over it would let
            // "tmp exists and is non-empty" pass on the PREVIOUS run's bytes when BSArch fails this
            // run — a false success that ships wrong content over the target (2026-06-12 adversarial
            // hunt). Refuse loud instead; nothing is packed, the prior archive is untouched.
            return new BsaResult(false, "",
                $"a stale houseCARL scratch from a previous run is stuck at '{tmp}' and could not be removed " +
                "(another process may hold it). Delete it and retry — this run packed nothing; the existing archive, if any, is untouched.");
        var baselineUtc = DateTime.UtcNow;

        var args = new List<string> { "pack", srcFolder, tmp, formatFlag, "-mt" };
        if (compress) args.Add("-z");
        var run = Run(bsarchExe, args, timeoutMs);

        // Provenance: THIS run must have written the scratch (mtime at/after the pre-run baseline) —
        // existence alone proved nothing about who made it.
        bool packed = run.runError is null && File.Exists(tmp) && new FileInfo(tmp).Length > 0
                      && File.GetLastWriteTimeUtc(tmp) >= baselineUtc;
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

    /// <summary>The legal format tokens, for refusal messages.</summary>
    public const string FormatTokens = "sse (default), tes3/morrowind, tes4/oblivion, fo3, fnv, tes5/le/skyrimle, fo4, fo4dds, sf1/starfield, sf1dds";

    /// <summary>Map a houseCARL format token to a BSArch flag. Null/empty/sse-family = the -sse default (Skyrim SE,
    /// the target). An UNKNOWN token returns null — the caller refuses loud naming <see cref="FormatTokens"/> —
    /// instead of silently packing -sse from a typo (Q3: a silently degraded mode; 2026-06-12 adversarial hunt).</summary>
    public static string? TryFormatFlag(string? format) => (format?.Trim().ToLowerInvariant()) switch
    {
        null or "" or "sse" or "ae" or "skyrimse" => "-sse",
        "tes3" or "morrowind" => "-tes3",
        "tes4" or "oblivion" => "-tes4",
        "fo3" => "-fo3",
        "fnv" => "-fnv",
        "tes5" or "le" or "skyrimle" => "-tes5",
        "fo4" => "-fo4",
        "fo4dds" => "-fo4dds",
        "sf1" or "starfield" => "-sf1",
        "sf1dds" => "-sf1dds",
        _ => null,
    };

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

        const int StreamDrainMs = 5000;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return (false, -1, "", "", $"BSArch did not finish within {timeoutMs / 1000}s (killed).");
        }
        // The PROCESS exited, but a grandchild that inherited the stdout/stderr pipe could keep it open and hang the
        // stream reads forever (WaitForExit(int) does NOT flush async readers, unlike the parameterless overload).
        // Bound the post-exit drain: on a stuck pipe kill the tree to force the inherited handles closed and report
        // what was captured rather than blocking indefinitely (Q3 — a bounded, named degradation, never a hang).
        bool drained; try { drained = Task.WaitAll(new Task[] { o, e }, StreamDrainMs); } catch { drained = false; }
        if (!drained) { try { p.Kill(entireProcessTree: true); } catch { /* already gone */ } }
        var stdout = o.IsCompletedSuccessfully ? o.Result : "";
        var stderr = e.IsCompletedSuccessfully ? e.Result : "";
        return drained
            ? (true, p.ExitCode, stdout, stderr, null)
            : (true, p.ExitCode, stdout, stderr,
               $"BSArch exited but its output did not drain within {StreamDrainMs / 1000}s (a child process may still hold the pipe) — captured output may be truncated.");
    }
}
