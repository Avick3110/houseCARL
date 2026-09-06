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
/// file another process still holds open strands only itself. A run that is killed — the bridge test's
/// 20-minute cap, Ctrl-C, a cancelled CI job — never reaches that, so <see cref="TryRedirect"/> also drops the
/// roots whose pid is no longer a live process; residue under our own pid that will not go is reported and
/// the run takes a fresh sibling root rather than work in another run's fixtures. Nothing here throws: a file
/// that will not delete is reported and the run's exit code is unaffected — cleanup never turns a run red.</para>
/// </summary>
public static class ProbeTemp
{
    static string? _root;
    static string? _tmpWas, _tempWas, _tmpdirWas;

    /// <summary>Point this process's temp directory at a root only this process uses. False with a one-sentence
    /// reason if that root could not be made — the temp directory is then left exactly where it was.</summary>
    public static bool TryRedirect(out string? error)
    {
        error = null;
        if (_root != null) return true;

        var temp = Path.GetTempPath();
        SweepDeadRoots(temp);

        // A root under this pid can only be residue from a dead process: no live process shares our pid. If any
        // of it will not go, say so and take a fresh sibling — a run inside fixtures it failed to empty is the
        // collision this class exists to remove, and silent.
        var root = Path.Combine(temp, "hc-" + Environment.ProcessId);
        for (int n = 2; Directory.Exists(root); n++)
        {
            var (left, first) = Remove(root);
            if (left == 0) break;
            ReportLeft(root, left, first);
            root = Path.Combine(temp, $"hc-{Environment.ProcessId}-{n}");
        }
        // A full or read-only temp volume, or a plain FILE already named hc-<pid> (which Directory.Exists above
        // does not see), stops the run before any probe: say which path and why, not a stack trace.
        try { Directory.CreateDirectory(root); }
        catch (Exception ex) { error = StartFailure(root, ex); return false; }

        // Set before the redirect below, not after it: a variable that will not take would otherwise leave the
        // process pointed at a root Cleanup no longer knows to remove.
        _root = root;

        _tmpWas = Environment.GetEnvironmentVariable("TMP");
        _tempWas = Environment.GetEnvironmentVariable("TEMP");
        _tmpdirWas = Environment.GetEnvironmentVariable("TMPDIR");
        Environment.SetEnvironmentVariable("TMP", root);
        Environment.SetEnvironmentVariable("TEMP", root);
        Environment.SetEnvironmentVariable("TMPDIR", root);   // what GetTempPath reads off Windows

        return true;
    }

    /// <summary>The one sentence a run prints when its fixture root cannot be made.</summary>
    static string StartFailure(string root, Exception ex) =>
        $"could not create the fixture directory {root} this run works in ({ex.Message.TrimEnd()}) — " +
        "free space on the temp volume, or remove whatever is already at that path, then run again.";

    /// <summary>Put the temp directory back and delete the root. A file still held open is reported, not thrown.</summary>
    public static void Cleanup()
    {
        if (_root is null) return;

        Environment.SetEnvironmentVariable("TMP", _tmpWas);
        Environment.SetEnvironmentVariable("TEMP", _tempWas);
        Environment.SetEnvironmentVariable("TMPDIR", _tmpdirWas);

        var (left, first) = Remove(_root);
        if (left > 0) ReportLeft(_root, left, first);
        _root = null;
    }

    /// <summary>Drop the roots of runs that were killed before their own cleanup ran: their pid is no longer live.</summary>
    static void SweepDeadRoots(string temp)
    {
        int left = 0;
        string? first = null;

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(temp, "hc-*"))
            {
                if (!TryReadPid(Path.GetFileName(dir), out var pid)) continue;
                if (pid == Environment.ProcessId || IsLive(pid)) continue;
                var (l, f) = Remove(dir);
                left += l;
                first ??= f;
            }
        }
        // A temp path that will not list has nothing to sweep and no residue to name: the enumeration is the
        // only thing here that throws, and the start failure right after it is the run's one sentence.
        catch { }

        if (left > 0)
            Console.WriteLine($"  (temp sweep left {left} entr{(left == 1 ? "y" : "ies")} from an earlier run under {temp}: {first})");
    }

    /// <summary>Read the pid out of an <c>hc-&lt;pid&gt;</c> or <c>hc-&lt;pid&gt;-&lt;n&gt;</c> root name.</summary>
    static bool TryReadPid(string name, out int pid)
    {
        pid = 0;
        if (name.Length <= 3) return false;
        var text = name.AsSpan(3);
        var dash = text.IndexOf('-');
        if (dash >= 0) text = text[..dash];
        return int.TryParse(text, out pid);
    }

    /// <summary>Whether a pid still belongs to a process. Only a definite "no such process" counts as dead.</summary>
    static bool IsLive(int pid)
    {
        // A process we cannot inspect — one at a higher integrity level, a transient permission error — counts
        // as live: leaving its root behind is residue the next run picks up, deleting it is a live run's
        // fixtures going mid-run. HasExited is not consulted; reaching it needs a handle we may not get.
        try { System.Diagnostics.Process.GetProcessById(pid).Dispose(); return true; }
        catch (ArgumentException) { return false; }
        catch { return true; }
    }

    static void ReportLeft(string dir, int left, string? first) =>
        Console.WriteLine($"  (temp cleanup left {left} entr{(left == 1 ? "y" : "ies")} in {dir}: {first})");

    /// <summary>Delete a directory's contents and then itself, counting what would not go. Never throws.</summary>
    static (int Left, string? FirstError) Remove(string dir)
    {
        int left = 0;
        string? first = null;

        try
        {
            // The walk itself can fail — a child process recreating a directory under us, or the root going
            // while we read it — so it is inside the try with the deletes, not outside it.
            foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
            {
                try
                {
                    if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
                    else File.Delete(entry);
                }
                // An entry another sweep deleted between our walk and our delete is the outcome we wanted, not
                // residue: counting it makes a run report a root that is in fact gone, and take a fresh sibling.
                catch (Exception ex) when (ex is not (DirectoryNotFoundException or FileNotFoundException))
                {
                    left++;
                    first ??= ex.Message;
                }
            }
        }
        catch (DirectoryNotFoundException) { return (0, null); }   // the root went while we walked it: nothing is left
        catch (Exception ex) { left++; first ??= ex.Message; }

        try { Directory.Delete(dir, recursive: true); }
        catch (DirectoryNotFoundException) { }
        catch (Exception ex) when (left > 0) { first ??= ex.Message; }
        catch (Exception ex) { left++; first ??= ex.Message; }

        return (left, first);
    }
}
