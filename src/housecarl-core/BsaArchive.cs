using System.Diagnostics;
using System.IO.Abstractions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;

namespace HousecarlCore;

/// <summary>The result of listing an archive. <see cref="RunError"/> non-null ⇒ the archive couldn't be opened/read
/// (bad path, not a Bethesda archive, corrupt header); otherwise <see cref="Success"/> and <see cref="Files"/> hold the
/// contained paths.</summary>
public sealed record BsaListResult(
    bool Success, string? Format, int DeclaredCount, IReadOnlyList<string> Files, string Raw, string? RunError)
{
    public bool Ran => RunError is null;
}

/// <summary>The result of an unpack (Mutagen) or pack (BSArch). <see cref="RunError"/> non-null ⇒ the operation never
/// really ran (archive couldn't be opened / BSArch couldn't be launched / a stuck stale scratch refused up front);
/// otherwise <see cref="Success"/> reflects what was actually written this run.</summary>
public sealed record BsaResult(bool Success, string Raw, string? RunError)
{
    public bool Ran => RunError is null;
}

/// <summary>
/// The engine behind the housecarl_bsa_* tools. READS (list + extract) go through <b>Mutagen's own BSA reader</b>
/// (<see cref="Archive.CreateReader"/> in Mutagen.Bethesda.Core — a maintained, in-process parser that handles every
/// version, compression, and embedded-name layout, and returns each file's decompressed bytes). No external tool is
/// needed to list or extract. WRITES (repack) still drive <b>BSArch</b> (zilav/ElminsterAU/Sheson; ships with xEdit),
/// because Mutagen 0.53.1 exposes a reader but no BSA writer.
///
/// Reads deliberately do NOT shell BSArch: its unpacker is stricter than its own lister and than the game engine, so
/// an archive written by a non-BSArch tool can list and load in-game yet unpack to nothing. Mutagen's reader matches
/// BSArch byte-for-byte on conformant archives and reads the archives BSArch's unpacker rejects.
///   • list  : Archive.CreateReader(SkyrimSE, path).Files → the contained paths + count.
///   • unpack: same, writing each IArchiveFile's bytes to the dest (path-traversal-guarded, content-aware/idempotent).
///   • pack  : `BSArch pack &lt;folder&gt; &lt;archive&gt; -sse [-z] -mt` (-sse = Skyrim SE; -z compresses but BREAKS
///             sounds/voices, so uncompressed is the safe default).
/// Pure (no DI): the build-time probes drive this exact code.
/// </summary>
public static class BsaArchive
{
    // houseCARL is Skyrim SE; the reader keys off the header version, so this also reads v103/v104 archives.
    static readonly IFileSystem Fs = new FileSystem();

    // A single archived entry is read into memory in-process (f.GetBytes). Bound that allocation: a corrupt/hostile
    // header declaring a multi-GB entry must fail LOUD, not OOM the whole single-process server (the out-of-process
    // BSArch path used to be killed on a timeout; this is the in-process equivalent). No real Skyrim asset approaches
    // this, and it sits at the ~2GB .NET single-array ceiling GetBytes would throw at anyway.
    const long MaxEntryBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>List an archive's contents via Mutagen, cross-checked against the header's OWN declared file count so a
    /// reader mis-parse can't render a quietly-short list. On an unreadable/corrupt/non-archive file the failure is
    /// surfaced in <see cref="BsaListResult.RunError"/> (Ran=false); a count mismatch surfaces as Ran-but-not-Success
    /// with the discrepancy in <see cref="BsaListResult.Raw"/> — never a silent empty list.</summary>
    public static BsaListResult List(string archive)
    {
        var hdr = ReadBsaHeader(archive);   // independent of Mutagen — the public IArchiveReader doesn't expose the count
        IArchiveReader reader;
        try { reader = Archive.CreateReader(GameRelease.SkyrimSE, archive, Fs); }
        catch (Exception ex) { return new BsaListResult(false, null, 0, Array.Empty<string>(), "", OpenError(archive, ex)); }
        try
        {
            var files = reader.Files.Select(f => f.Path).ToList();
            if (hdr is { fileCount: var declared } && declared != (uint)files.Count)
                return new BsaListResult(false, VersionLabel(hdr), (int)declared, files,
                    $"'{Path.GetFileName(archive)}': header declares {declared} file(s) but the reader enumerated {files.Count} — the archive may be corrupt or unsupported.", null);
            return new BsaListResult(true, VersionLabel(hdr), hdr is { fileCount: var c } ? (int)c : files.Count, files, "", null);
        }
        catch (Exception ex)
        {
            return new BsaListResult(false, null, 0, Array.Empty<string>(), "",
                $"could not read the file list of '{Path.GetFileName(archive)}' ({ex.GetType().Name}: {ex.Message}).");
        }
        finally { (reader as IDisposable)?.Dispose(); }
    }

    /// <summary>Unpack the WHOLE archive into <paramref name="destFolder"/> (created if absent) via Mutagen. Each file's
    /// DECOMPRESSED bytes are written to dest/{file path}. Writes are:
    ///   • path-traversal-guarded — an entry resolving outside the dest refuses loud, never writes out-of-tree;
    ///   • content-aware/idempotent — a file already present byte-identical is skipped, so re-extracting into a
    ///     populated dest reports "already present" rather than a spurious rewrite (this is why the managed flow's
    ///     pre-seeded meta.ini marker is left untouched).
    /// An archive that can't be opened/read fails with a named reason. "Read a file inside" = unpack, then read it.</summary>
    public static BsaResult Unpack(string archive, string destFolder)
    {
        Directory.CreateDirectory(destFolder);
        var hdr = ReadBsaHeader(archive);
        IArchiveReader reader;
        try { reader = Archive.CreateReader(GameRelease.SkyrimSE, archive, Fs); }
        catch (Exception ex) { return new BsaResult(false, "", OpenError(archive, ex)); }

        string destFull = Path.GetFullPath(destFolder);
        int written = 0, already = 0;
        try
        {
            foreach (var f in reader.Files)
            {
                if (f.Size > MaxEntryBytes)   // corrupt/hostile header — refuse loud rather than OOM the server
                    return new BsaResult(false,
                        $"archive entry '{f.Path}' declares {f.Size:N0} bytes, over the {MaxEntryBytes:N0}-byte safety ceiling — refusing to read it in-process (the archive header may be corrupt).", null);
                string outPath = Path.GetFullPath(Path.Combine(destFull, f.Path));
                if (!IsUnder(destFull, outPath))
                    return new BsaResult(false,
                        $"archive entry '{f.Path}' resolves outside the destination folder (path traversal) — refusing after {written} file(s).", null);
                byte[] body = f.GetBytes();
                if (SameOnDisk(outPath, body)) { already++; continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                File.WriteAllBytes(outPath, body);
                written++;
            }
        }
        catch (Exception ex)
        {
            return new BsaResult(false,
                $"failed while extracting '{Path.GetFileName(archive)}' ({ex.GetType().Name}: {ex.Message}); {written} file(s) written before the error.", null);
        }
        finally { (reader as IDisposable)?.Dispose(); }

        int total = written + already;
        // Cross-check against the header's own count. If the reader enumerated FEWER files than the archive declares
        // (a mis-parse down to zero being the worst case), fail loud rather than report a partial or empty extract
        // as success.
        if (hdr is { fileCount: var declared } && declared != (uint)total)
            return new BsaResult(false,
                $"extracted {total} file(s) from '{Path.GetFileName(archive)}' but its header declares {declared} — the archive may be corrupt or unsupported; refusing to report it as success.", null);

        string note = written > 0
            ? $"extracted {written} file(s)" + (already > 0 ? $" ({already} already present byte-identical)" : "") + "."
            : already > 0 ? $"all {already} file(s) were already present byte-identical — nothing to extract."
                          : "the archive contained no files.";
        return new BsaResult(true, note, null);
    }

    static string OpenError(string archive, Exception ex) =>
        $"could not open '{Path.GetFileName(archive)}' as a Bethesda archive ({ex.GetType().Name}: {ex.Message}). " +
        "Is it a real .bsa (not a .ba2 / renamed file), and not truncated?";

    /// <summary>Read the version + folder/file counts straight from the 24-byte BSA header — an oracle INDEPENDENT of
    /// Mutagen's reader (whose public IArchiveReader exposes neither), used to cross-check the enumerated file count and
    /// to label the format. Null if the file can't be read or isn't a "BSA\0" archive.</summary>
    static (uint version, uint folderCount, uint fileCount)? ReadBsaHeader(string archive)
    {
        try
        {
            using var fs = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read);
            var h = new byte[24];
            if (fs.Read(h, 0, 24) < 24) return null;
            if (h[0] != 0x42 || h[1] != 0x53 || h[2] != 0x41 || h[3] != 0x00) return null;   // not "BSA\0"
            static uint U(byte[] b, int o) => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
            return (U(h, 4), U(h, 16), U(h, 20));   // version @4, folderCount @16, fileCount @20
        }
        catch { return null; }
    }

    /// <summary>A cosmetic format label for the list output, derived from the already-read header. Null if unread.</summary>
    static string? VersionLabel((uint version, uint folderCount, uint fileCount)? hdr) => hdr?.version switch
    {
        null => null,
        103 => "BSA v103 (Oblivion)",
        104 => "BSA v104 (Skyrim LE / Fallout 3 / NV)",
        105 => "BSA v105 (Skyrim SE/AE)",
        var v => $"BSA v{v}",
    };

    /// <summary>Is <paramref name="candidate"/> strictly inside <paramref name="root"/>? Both are already full paths.
    /// Because <c>Path.GetFullPath</c> resolves any <c>..</c> before this check, it catches both relative traversal and
    /// absolute/rooted entry paths.</summary>
    static bool IsUnder(string root, string candidate)
    {
        root = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True iff <paramref name="path"/> already holds exactly <paramref name="body"/> (content-aware skip).</summary>
    static bool SameOnDisk(string path, byte[] body)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length != body.Length) return false;
            return File.ReadAllBytes(path).AsSpan().SequenceEqual(body);
        }
        catch { return false; }
    }

    /// <summary>Pack <paramref name="srcFolder"/> into a .bsa at <paramref name="archive"/> with the given format flag
    /// (e.g. "-sse") and optional compression, via BSArch (Mutagen has no BSA writer). NON-DESTRUCTIVE:
    /// an existing archive at the target is NEVER overwritten unless this run successfully packs a new one — BSArch writes
    /// to a houseCARL-internal temp beside the target, and only a clean pack THIS RUN (temp exists, non-empty, AND written
    /// at/after the run's mtime baseline) is moved over the target; a stale scratch from a previous run that cannot be
    /// removed REFUSES up front (nothing runs, the prior .bsa untouched), and any failure (BSArch error, timeout, empty
    /// output, stale-mtime scratch) deletes the temp and leaves the prior .bsa untouched. The mtime gate assumes an
    /// NTFS-class timestamp resolution — on a FAT-class target a same-second pack could read as stale and fail LOUD (never
    /// falsely succeed). NOTE the caller must surface BSArch's caveat: a COMPRESSED archive breaks any sounds/voices it
    /// contains.</summary>
    public static BsaResult Pack(string bsarchExe, string srcFolder, string archive, string formatFlag, bool compress, int timeoutMs = 600_000)
    {
        // Pack to a scratch sibling (keeps the .bsa extension so BSArch is happy); the real target is touched only on success.
        var dir = Path.GetDirectoryName(archive) ?? Environment.CurrentDirectory;
        var tmp = Path.Combine(dir, Path.GetFileNameWithoutExtension(archive) + ".houseCARL-tmp.bsa");
        try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* checked next — a stuck scratch refuses loud */ }
        if (File.Exists(tmp))
            // A stale scratch from a previous run that we cannot remove: packing over it would let
            // "tmp exists and is non-empty" pass on the PREVIOUS run's bytes when BSArch fails this
            // run — a false success that ships wrong content over the target. Refuse loud instead;
            // nothing is packed, the prior archive is untouched.
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

        try { AtomicFile.Commit(tmp, archive); }   // success → crash-atomically swap the target (File.Replace / rename, same volume)
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
    /// instead of silently packing -sse from a typo.</summary>
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
        // what was captured rather than blocking indefinitely — a bounded, named degradation, never a hang.
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
