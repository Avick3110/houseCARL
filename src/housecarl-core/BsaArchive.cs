using System.Diagnostics;
using System.IO.Abstractions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;

namespace HousecarlCore;

/// <summary>The result of listing an archive. <see cref="RunError"/> non-null means it could not be opened or read.</summary>
public sealed record BsaListResult(
    bool Success, string? Format, int DeclaredCount, IReadOnlyList<string> Files, string Raw, string? RunError)
{
    public bool Ran => RunError is null;
}

/// <summary>The result of an unpack. <see cref="RunError"/> non-null means the operation never really ran.</summary>
public sealed record BsaResult(bool Success, string Raw, string? RunError)
{
    public bool Ran => RunError is null;
}

/// <summary>What a pack source folder holds: the files BSArch will archive, and the FULL root listing it drops.</summary>
public sealed record BsaSourceScan(int Archivable, IReadOnlyList<string> RootFiles);

/// <summary>The result of a pack. <see cref="RunError"/> means nothing usable was produced; <see cref="CountError"/>
/// means it ran but the archive's header count disagreed with the source, so nothing was placed. A null
/// <see cref="Packed"/> or <see cref="Expected"/> means that side could not be read, so nothing was cross-checked.</summary>
public sealed record BsaPackResult(
    bool Success, int? Packed, int? Expected, IReadOnlyList<string> RootSkipped, string Raw, string? RunError, string? CountError)
{
    public bool Ran => RunError is null;
}

/// <summary>How a pack writes the archive, so a test can substitute a packer and exercise the checks around the
/// write without BSArch. <c>exit</c> 0 is clean; anything else is a failed pack whatever it left on disk.</summary>
public delegate (int exit, string stdout, string stderr, string? runError) BsaPacker(
    string bsarchExe, string srcFolder, string tmpArchive, string formatFlag, bool compress, int timeoutMs);

/// <summary>The engine behind the housecarl_bsa_* tools: READS go through Mutagen's own in-process reader, and only
/// repack drives BSArch, which is the only half Mutagen 0.53.1 cannot do. The reader matches BSArch byte-for-byte on
/// a conformant archive and reads the archives BSArch's unpacker rejects — pinned by the opt-in <c>bsa-probe</c>.
/// The header cross-check, traversal guard and pack provenance are in docs/architecture/assets.md.</summary>
public static class BsaArchive
{
    static readonly IFileSystem Fs = new FileSystem();

    // One archived entry is read into memory in-process, so bound that allocation: a corrupt or hostile header
    // declaring a multi-GB entry must fail loud rather than OOM the single-process server.
    const long MaxEntryBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>List an archive's contents via Mutagen, cross-checked against the header's OWN declared file count.
    /// An unreadable file surfaces in <see cref="BsaListResult.RunError"/>; a mismatch is never a silent short list.</summary>
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

    /// <summary>Unpack the WHOLE archive into <paramref name="destFolder"/> via Mutagen, writing each file's
    /// decompressed bytes. Path-traversal-guarded and content-aware: a byte-identical file is skipped, which is why
    /// the managed flow's pre-seeded meta.ini marker is left untouched.</summary>
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
        // Cross-check against the header's own count: a reader mis-parse down to zero must not report as success.
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

    /// <summary>Read the version + folder/file counts straight from the 24-byte BSA header — an oracle INDEPENDENT of Mutagen's reader, which exposes neither.</summary>
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

    /// <summary>Is <paramref name="candidate"/> strictly inside <paramref name="root"/>? Both are already full paths, so this catches relative and rooted traversal alike.</summary>
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

    /// <summary>Pack <paramref name="srcFolder"/> into a .bsa at <paramref name="archive"/> via BSArch. NON-DESTRUCTIVE:
    /// the pack goes to a houseCARL scratch and the target is touched only after a clean run whose header count
    /// agrees with the source scan — the provenance rule in docs/architecture/assets.md. The caller must surface
    /// BSArch's caveat that a COMPRESSED archive breaks any sounds or voices in it.</summary>
    public static BsaPackResult Pack(string bsarchExe, string srcFolder, string archive, string formatFlag, bool compress, int timeoutMs = 600_000, BsaPacker? packer = null)
    {
        var nothing = Array.Empty<string>();
        // Pack to a scratch sibling (keeps the .bsa extension so BSArch is happy); the real target is touched only on success.
        var dir = Path.GetDirectoryName(archive) ?? Environment.CurrentDirectory;
        var tmp = Path.Combine(dir, Path.GetFileNameWithoutExtension(archive) + ".houseCARL-tmp.bsa");
        try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* checked next — a stuck scratch refuses loud */ }
        if (File.Exists(tmp))
            // A stale scratch we cannot remove would let "tmp exists and is non-empty" pass on the PREVIOUS run's
            // bytes when BSArch fails this run — a false success that ships wrong content over the target.
            return new BsaPackResult(false, null, null, nothing, "",
                $"a stale houseCARL scratch from a previous run is stuck at '{tmp}' and could not be removed " +
                "(another process may hold it). Delete it and retry — this run packed nothing; the existing archive, if any, is untouched.", null);

        // Count what the source offers as a CROSS-CHECK, not a precondition: a folder that cannot be fully listed
        // packs unverified and the caller says so, rather than refusing a pack that used to work.
        BsaSourceScan? scan;
        try { scan = ScanPackSource(srcFolder); }
        catch { scan = null; }

        var run = (packer ?? ShellBsArch)(bsarchExe, srcFolder, tmp, formatFlag, compress, timeoutMs);

        // A non-zero exit is a failed pack whatever it left behind, including a scratch whose count happens to agree.
        if (run.runError is null && run.exit != 0)
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best-effort */ }
            var said = string.IsNullOrWhiteSpace(run.stderr) ? "it printed no error output" : run.stderr.Trim();
            return new BsaPackResult(false, null, scan?.Archivable, scan?.RootFiles ?? nothing,
                (run.stdout + "\n" + run.stderr).Trim(),
                $"BSArch exited with code {run.exit} — {said}. Nothing was packed; the existing archive, if any, is untouched.", null);
        }

        // Provenance: the scratch was cleared before the run, so a non-empty one after a zero-exit run is this run's.
        // No mtime compare — an NTFS write stamp can read older than a precise-clock baseline taken microseconds earlier (#522).
        bool packed = run.runError is null && File.Exists(tmp) && new FileInfo(tmp).Length > 0;
        if (!packed)   // BSArch couldn't run, or produced no/empty output — leave any prior archive untouched
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best-effort */ }
            return new BsaPackResult(false, null, scan?.Archivable, scan?.RootFiles ?? nothing, (run.stdout + "\n" + run.stderr).Trim(), run.runError, null);
        }

        // Cross-check the produced archive's header count against the source, the same oracle List/Unpack use. The
        // oracle reads .bsa only, so a BA2 or Morrowind archive carries no count and the caller says so.
        var hdr = ReadBsaHeader(tmp);
        int? packedCount = hdr is { fileCount: var fc } ? (int)fc : null;
        if (packedCount is { } read && scan is { } src && PackCountError(read, src.Archivable, Path.GetFileName(archive)) is { } countError)
        {
            try { File.Delete(tmp); } catch { /* best-effort */ }
            return new BsaPackResult(false, packedCount, scan?.Archivable, scan?.RootFiles ?? nothing,
                (run.stdout + "\n" + run.stderr).Trim(), null, countError);
        }

        try { AtomicFile.Commit(tmp, archive); }   // success → crash-atomically swap the target (File.Replace / rename, same volume)
        catch (Exception ex)
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best-effort */ }
            return new BsaPackResult(false, packedCount, scan?.Archivable, scan?.RootFiles ?? nothing,
                (run.stdout + "\n" + run.stderr + $"\ncould not place the packed archive at '{archive}': {ex.Message}").Trim(), null, null);
        }
        return new BsaPackResult(File.Exists(archive) && new FileInfo(archive).Length > 0, packedCount, scan?.Archivable,
            scan?.RootFiles ?? nothing, (run.stdout + "\n" + run.stderr).Trim(), null, null);
    }

    /// <summary>Count what a pack will archive and what it will drop: BSArch packs only files under a subfolder, and
    /// drops every *.db and every extensionless file wherever it sits.</summary>
    public static BsaSourceScan ScanPackSource(string srcFolder)
    {
        if (!Directory.Exists(srcFolder)) return new BsaSourceScan(0, Array.Empty<string>());
        // BSArch packs nothing with a .db extension and nothing without one (checked against BSArch v0.9c).
        static bool Packs(string p) => Path.GetExtension(p) is { Length: > 0 } e && !e.Equals(".db", StringComparison.OrdinalIgnoreCase);
        var root = Directory.GetFiles(srcFolder, "*", SearchOption.TopDirectoryOnly).Select(p => Path.GetFileName(p)).ToList();
        int all = Directory.GetFiles(srcFolder, "*", SearchOption.AllDirectories).Count(Packs);
        return new BsaSourceScan(all - root.Count(Packs), root);
    }

    /// <summary>The one-sentence refusal for a packed archive whose file count disagrees with its source.</summary>
    public static string? PackCountError(int packed, int expected, string archiveName) =>
        packed == expected ? null
            : $"'{archiveName}' packed {packed} file(s) but the source folder offers {expected} — refusing to place it; check the source folder for unreadable or locked files and repack.";

    /// <summary>The legal format tokens, for refusal messages.</summary>
    public const string FormatTokens = "sse (default), tes3/morrowind, tes4/oblivion, fo3, fnv, tes5/le/skyrimle, fo4, fo4dds, sf1/starfield, sf1dds";

    /// <summary>Map a houseCARL format token to a BSArch flag. An UNKNOWN token returns null so the caller refuses loud, never packs -sse from a typo.</summary>
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

    static (int exit, string stdout, string stderr, string? runError) ShellBsArch(
        string bsarchExe, string srcFolder, string tmpArchive, string formatFlag, bool compress, int timeoutMs)
    {
        var args = new List<string> { "pack", srcFolder, tmpArchive, formatFlag, "-mt" };
        if (compress) args.Add("-z");
        var run = Run(bsarchExe, args, timeoutMs);
        return (run.exit, run.stdout, run.stderr, run.runError);
    }

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
        // The PROCESS exited, but a grandchild holding the stdout/stderr pipe can hang the stream reads forever
        // (WaitForExit(int) does not flush async readers). Bounded: on a stuck pipe kill the tree and report what
        // was captured — a named degradation, never a hang.
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
