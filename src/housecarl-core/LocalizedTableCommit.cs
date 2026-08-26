namespace HousecarlCore;

/// <summary>
/// Commits a plugin together with the <c>.STRINGS</c> / <c>.DLSTRINGS</c> / <c>.ILSTRINGS</c> tables its own serialize
/// emitted — for an output houseCARL OWNS, and only there.
///
/// <para><b>Owned output means the compact lane's P′</b>: a plugin written into a houseCARL mod folder the modder has
/// not enabled yet, which they review and swap in themselves. Nothing on disk at that location is authored content, so
/// the plugin and its tables can simply be written: an interruption leaves a half-written output nobody is running,
/// which is the same contract that location already had before strings entered the picture.</para>
///
/// <para><b>What this deliberately no longer does.</b> An earlier form of this file also committed emitted tables OVER
/// an existing localized plugin — the user's own file — and carried a blank-window ordering, backups, a manifest and a
/// recovery gate to make that survivable. That machinery was cut (2026-08-26, advisor ruling on the round-1 §4 stop)
/// after a review round found it destroyed its own recovery set when any other plugin in the same mod folder was
/// written, and that the recovery it instructed reconstructed the very corruption it existed to prevent. In-place
/// writes of a localized plugin are refused outright at <see cref="WriteEngine.WriteInPlace"/>'s choke point, so no
/// live table set is ever replaced and none of that protection has anything left to protect.</para>
/// </summary>
public static class LocalizedTableCommit
{
    /// <summary>The <c>Strings\</c> folder Mutagen emitted beside the staged plugin, or null when the serialize
    /// produced none (a mod that is not flagged localized).</summary>
    public static string? EmittedTablesDir(string stagedPluginPath)
    {
        var dir = Path.GetDirectoryName(stagedPluginPath);
        if (dir is null) return null;
        var strings = Path.Combine(dir, "Strings");
        return Directory.Exists(strings) ? strings : null;
    }

    /// <summary>Commit the staged plugin and the tables its serialize emitted into a houseCARL-owned output location.
    ///
    /// <para>The emitted set and any stale set already there are both filtered to THIS plugin's stem. The staging
    /// directory is a per-FOLDER path shared by every plugin in it (<see cref="WriteEngine"/>'s
    /// <c>.housecarl-tmp</c>), so a commit that took "whatever Strings folder is in staging" would adopt a different
    /// plugin's tables — measured, and the reason both halves are filtered rather than globbed.</para></summary>
    /// <param name="stagedPluginPath">The staged plugin, in its <c>.housecarl-tmp</c> staging directory.</param>
    /// <param name="outputPath">Where the plugin is going — houseCARL's own output, never the caller's own file.</param>
    public static void CommitOwnedOutput(string stagedPluginPath, string outputPath)
    {
        var stem = Path.GetFileNameWithoutExtension(outputPath);
        var emittedDir = EmittedTablesDir(stagedPluginPath);
        var emitted = emittedDir is null
            ? Array.Empty<string>()
            : LocalizedStrings.TableFilesIn(emittedDir, stem);

        try
        {
            // No tables of our own emitted — the mod is not localized, and this is the single-file atomic swap the
            // in-place lane has always used.
            if (emitted.Count == 0)
            {
                AtomicFile.Commit(stagedPluginPath, outputPath);
                return;
            }

            AtomicFile.Commit(stagedPluginPath, outputPath);

            // A re-run into the same output folder can find the PREVIOUS run's tables here. They describe the plugin
            // that was just replaced, so a language that run carried and this one does not would otherwise be left
            // behind claiming to describe the new plugin. Only this plugin's own tables are touched.
            var liveStrings = Path.Combine(Path.GetDirectoryName(outputPath)!, "Strings");
            foreach (var stale in LocalizedStrings.TableFilesIn(liveStrings, stem))
            {
                try { File.Delete(stale); }
                catch (IOException) { } catch (UnauthorizedAccessException) { }
            }

            Directory.CreateDirectory(liveStrings);
            foreach (var f in emitted) AtomicFile.Commit(f, Path.Combine(liveStrings, Path.GetFileName(f)));
        }
        finally { Cleanup(stagedPluginPath, stem); }
    }

    /// <summary>Remove what THIS write staged, and the staging directory only if nothing else is using it.
    ///
    /// <para>Scoped rather than recursive, deliberately: the staging directory is shared by every plugin in the mod
    /// folder, so a recursive delete here removes files a concurrent or interrupted write of a DIFFERENT plugin is
    /// relying on. Best-effort throughout — a cleanup failure never masks the result of the commit itself (Q3).</para></summary>
    static void Cleanup(string stagedPluginPath, string stem)
    {
        try
        {
            if (File.Exists(stagedPluginPath)) File.Delete(stagedPluginPath);

            var dir = Path.GetDirectoryName(stagedPluginPath);
            if (dir is null || !Directory.Exists(dir)) return;

            var stagedStrings = Path.Combine(dir, "Strings");
            foreach (var f in LocalizedStrings.TableFilesIn(stagedStrings, stem))
            {
                try { File.Delete(f); }
                catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            if (Directory.Exists(stagedStrings) && !Directory.EnumerateFileSystemEntries(stagedStrings).Any())
                Directory.Delete(stagedStrings, recursive: false);

            if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir, recursive: false);
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
