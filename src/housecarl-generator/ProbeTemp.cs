namespace HousecarlGenerator;

/// <summary>
/// The fixture root a probe run works under: one directory per process, removed when the run ends.
///
/// <para>Probes name their own fixture directories — <c>hc-inplace-guard</c>, <c>hc-mo2instance-probe</c> and
/// ~50 more — off <see cref="Path.GetTempPath"/>. Those names are fixed, so two runs at once used to open the
/// same directory and fail each other on file-in-use. Rather than rewrite every call site (and leave the next
/// probe free to add another fixed path), this points the PROCESS at its own temp directory before any probe
/// runs: <c>Path.GetTempPath()</c> then answers <c>%TEMP%\hc-&lt;pid&gt;\</c>, every probe's path lands under
/// it, and two processes cannot name the same directory because their pids differ. Child processes a probe
/// spawns inherit the same root.</para>
///
/// <para>The run also stops leaving fixtures behind: the whole root goes at the end, entry by entry so one
/// file another process still holds open strands only itself. A file that will not delete is reported and the
/// run's exit code is unaffected — cleanup never turns a green run red.</para>
/// </summary>
public static class ProbeTemp
{
    static string? _root;
    static string? _tmpWas, _tempWas, _tmpdirWas;

    /// <summary>Point this process's temp directory at a root only this process uses, and return it.</summary>
    public static string Redirect()
    {
        if (_root != null) return _root;

        var root = Path.Combine(Path.GetTempPath(), "hc-" + Environment.ProcessId);
        // A root under this pid can only be residue from a dead process: no live process shares our pid.
        if (Directory.Exists(root)) Remove(root);
        Directory.CreateDirectory(root);

        _tmpWas = Environment.GetEnvironmentVariable("TMP");
        _tempWas = Environment.GetEnvironmentVariable("TEMP");
        _tmpdirWas = Environment.GetEnvironmentVariable("TMPDIR");
        Environment.SetEnvironmentVariable("TMP", root);
        Environment.SetEnvironmentVariable("TEMP", root);
        Environment.SetEnvironmentVariable("TMPDIR", root);   // what GetTempPath reads off Windows

        _root = root;
        return root;
    }

    /// <summary>Put the temp directory back and delete the root. A file still held open is reported, not thrown.</summary>
    public static void Cleanup()
    {
        if (_root is null) return;

        Environment.SetEnvironmentVariable("TMP", _tmpWas);
        Environment.SetEnvironmentVariable("TEMP", _tempWas);
        Environment.SetEnvironmentVariable("TMPDIR", _tmpdirWas);

        var (left, first) = Remove(_root);
        if (left > 0)
            Console.WriteLine($"  (temp cleanup left {left} entr{(left == 1 ? "y" : "ies")} in {_root}: {first})");
        _root = null;
    }

    /// <summary>Delete a directory's contents and then itself, counting what would not go.</summary>
    static (int Left, string? FirstError) Remove(string dir)
    {
        int left = 0;
        string? first = null;

        foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
        {
            try
            {
                if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
                else File.Delete(entry);
            }
            catch (Exception ex)
            {
                left++;
                first ??= ex.Message;
            }
        }

        try { Directory.Delete(dir, recursive: true); }
        catch (Exception ex) when (left > 0) { first ??= ex.Message; }
        catch (Exception ex) { left++; first ??= ex.Message; }

        return (left, first);
    }
}
