using System.Text;

namespace HousecarlCore;

/// <summary>
/// Commits a re-serialized localized plugin together with the string tables its own serialize emitted — the seam #373
/// describes as missing, where <c>CommitStagedPatch</c> committed the plugin alone and discarded the renumbered tables
/// with the staging directory, leaving the committed plugin's new indices resolving against its old, untouched tables.
///
/// <para><b>The crash window, and why the order is what it is.</b> <see cref="AtomicFile.Commit"/> swaps ONE file. A
/// plugin and its tables cannot swap as a set, and both obvious orderings leave a SCRAMBLED pairing: plugin-first
/// leaves the new indices against the old tables, tables-first leaves the old indices against the new tables. That is
/// #373's failure exactly — a weapon reading a book's name — a silent wrong answer, which is the one outcome Q3 rules
/// out. Ordering alone cannot fix it, so the design changes what the reachable intermediate state IS: the live tables
/// are backed up and REMOVED first, so every state a crash can leave is a localized plugin with no tables — BLANK, not
/// scrambled. Blank is the right target rather than merely the lesser evil, because houseCARL's own read surface
/// already renders an unresolved localized string LOUD (<c>ReadEngine.UnresolvedStringNote</c> — a no-value note,
/// never a blank token, so a value predicate's accounting fires on it). A scrambled plugin reads as authored content;
/// a blank one announces itself.</para>
///
/// <para>A manifest is written before the first destructive step and removed after the last, so an interrupted commit
/// is DETECTABLE rather than merely survivable: <see cref="PendingCommit"/> is what the write lanes pre-flight on, and
/// the backups it names are beside the plugin for recovery.</para>
/// </summary>
public static class LocalizedTableCommit
{
    /// <summary>Marker naming an in-flight commit's file set. Present only between the first destructive step and the
    /// last; its survival IS the signal that a commit was interrupted.</summary>
    public const string ManifestName = "localized-commit.manifest";

    const string BackupDirName = "backup";

    /// <summary>Test seam: invoked with a step name at each point between the destructive steps below, so the guard can
    /// throw from it and leave the process in exactly the state a crash there would. The alternative — building each
    /// window state by hand and asserting on it — would test the guard's idea of the sequence rather than this
    /// method's, which is the one thing those arms exist to check. Null in every non-test path.</summary>
    internal static Action<string>? StepHook;

    /// <summary>Step names <see cref="StepHook"/> is called with, in order.</summary>
    internal const string StepAfterManifest = "after-manifest";
    internal const string StepAfterDelete = "after-delete";
    internal const string StepAfterPlugin = "after-plugin";
    internal const string StepMidTables = "mid-tables";

    /// <summary>The <c>Strings\</c> folder Mutagen emitted beside the staged plugin, or null when the serialize
    /// produced none (a non-localized mod).</summary>
    public static string? EmittedTablesDir(string stagedPluginPath)
    {
        var dir = Path.GetDirectoryName(stagedPluginPath);
        if (dir is null) return null;
        var strings = Path.Combine(dir, "Strings");
        return Directory.Exists(strings) ? strings : null;
    }

    /// <summary>Is an interrupted commit still pending for the plugin at <paramref name="pluginPath"/>? Returns the
    /// manifest's path when one is, for the refusal to name. One <c>File.Exists</c> — the write lanes pre-flight on it;
    /// nothing is added to the read path, which stays hot and already renders the blank state loud.</summary>
    public static string? PendingCommit(string pluginPath)
    {
        var dir = Path.GetDirectoryName(pluginPath);
        if (dir is null) return null;
        var manifest = Path.Combine(dir, ".housecarl-tmp", ManifestName);
        return File.Exists(manifest) ? manifest : null;
    }

    /// <summary>Commit the staged plugin and its emitted tables as one set.
    ///
    /// <para>When <paramref name="outputPath"/> names a file that does not exist yet — the compact and merge new-file
    /// lanes — there are no live tables to mispair with, so no backup or manifest is written: the plugin and its
    /// tables are simply committed, and an interruption leaves a partial output nobody has enabled yet.</para></summary>
    /// <param name="stagedPluginPath">The staged plugin, in its <c>.housecarl-tmp</c> staging directory.</param>
    /// <param name="outputPath">Where the plugin is going.</param>
    public static void Commit(string stagedPluginPath, string outputPath)
    {
        var emitted = EmittedTablesDir(stagedPluginPath);
        // No tables emitted — the mod is not localized, and this is the single-file atomic swap it has always been,
        // cleanup contract included.
        if (emitted is null)
        {
            try { AtomicFile.Commit(stagedPluginPath, outputPath); }
            finally { Cleanup(stagedPluginPath); }
            return;
        }

        var emittedFiles = Directory.GetFiles(emitted);
        var liveStrings = Path.Combine(Path.GetDirectoryName(outputPath)!, "Strings");

        if (!File.Exists(outputPath))
        {
            // Fresh output: nothing on disk to mispair with, so the blank-window protocol has nothing to protect.
            try
            {
                AtomicFile.Commit(stagedPluginPath, outputPath);
                Directory.CreateDirectory(liveStrings);
                foreach (var f in emittedFiles) AtomicFile.Commit(f, Path.Combine(liveStrings, Path.GetFileName(f)));
            }
            finally { Cleanup(stagedPluginPath); }
            return;
        }

        var stagingDir = Path.GetDirectoryName(stagedPluginPath)!;
        var backupDir = Path.Combine(stagingDir, BackupDirName);
        var live = LocalizedStrings.OwnTableFiles(outputPath);

        // 1. Back the live tables up, then record the whole set. The manifest is FLUSHED before anything is destroyed,
        //    so a crash between here and the end always leaves a marker naming what was in flight.
        Directory.CreateDirectory(backupDir);
        foreach (var f in live) File.Copy(f, Path.Combine(backupDir, Path.GetFileName(f)), overwrite: true);
        WriteManifest(Path.Combine(stagingDir, ManifestName), outputPath, live, emittedFiles, backupDir);
        StepHook?.Invoke(StepAfterManifest);

        // 2. Remove the live tables. From here until step 4 completes, the plugin resolves nothing — blank, and loud
        //    through every houseCARL read, which is the state this ordering exists to guarantee.
        foreach (var f in live) File.Delete(f);
        StepHook?.Invoke(StepAfterDelete);

        // 3. The plugin.
        AtomicFile.Commit(stagedPluginPath, outputPath);
        StepHook?.Invoke(StepAfterPlugin);

        // 4. The tables its serialize emitted, into the folder the live ones came from.
        Directory.CreateDirectory(liveStrings);
        bool firstTable = true;
        foreach (var f in emittedFiles)
        {
            AtomicFile.Commit(f, Path.Combine(liveStrings, Path.GetFileName(f)));
            if (firstTable) { firstTable = false; StepHook?.Invoke(StepMidTables); }
        }

        // 5. The set is consistent again; drop the marker LAST, so its absence means exactly "nothing in flight", then
        //    the staging directory with it. DELIBERATELY not in a finally: if any step above threw, the manifest and
        //    the backups it names are the only record of what the plugin used to hold, and the write lanes' recovery
        //    gate refuses on that manifest. Cleaning up after a failure would erase the recovery along with the mess.
        try { File.Delete(Path.Combine(stagingDir, ManifestName)); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
        Cleanup(stagedPluginPath);
    }

    /// <summary>Remove the staging directory and everything in it. Best-effort: a cleanup failure never masks the
    /// result of the commit itself (Q3).</summary>
    static void Cleanup(string stagedPluginPath)
    {
        try
        {
            if (File.Exists(stagedPluginPath)) File.Delete(stagedPluginPath);
            var dir = Path.GetDirectoryName(stagedPluginPath);
            if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>The in-flight record: the plugin, the tables removed, the tables going in, and where the backups are.
    /// Plain text on purpose — a modder recovering by hand reads this file, and so does the refusal that names it.</summary>
    static void WriteManifest(string path, string outputPath, IReadOnlyList<string> removed,
                              IReadOnlyList<string> incoming, string backupDir)
    {
        var sb = new StringBuilder();
        sb.AppendLine("houseCARL localized-strings commit in flight.");
        sb.AppendLine("If this file still exists, the commit did not finish and the plugin below reads BLANK.");
        sb.AppendLine();
        sb.AppendLine("plugin: " + outputPath);
        sb.AppendLine("backups: " + backupDir);
        foreach (var f in removed) sb.AppendLine("removed: " + f);
        foreach (var f in incoming) sb.AppendLine("incoming: " + Path.GetFileName(f));
        sb.AppendLine();
        sb.AppendLine("To restore by hand, copy every file from the backups folder back beside the plugin's Strings");
        sb.AppendLine("folder, then delete this file.");

        // Flushed to disk before the caller destroys anything: a manifest still sitting in a write buffer when the
        // power goes is a manifest that does not exist, which is the one failure this whole file is guarding against.
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        fs.Write(bytes, 0, bytes.Length);
        fs.Flush(flushToDisk: true);
    }
}
