using HousecarlCore;

namespace HousecarlMcp;

/// <summary>The server-managed directory for auto-spilled result artifacts; a caller-named <c>to_file=</c>
/// target never comes here. Files are named <c>&lt;tool&gt;_&lt;utc stamp&gt;_&lt;epoch&gt;.jsonl</c> and are
/// immutable once written — a same-second collision gets a counter, never an overwrite.</summary>
static class ResultsStore
{
    public const int PruneAfterDays = 7;

    /// <summary>The results directory (created on demand). Resolution is HOUSECARL_DATA_DIR, else the server
    /// binary's folder — the same order Program.cs uses for user config, so results sit beside it.</summary>
    public static string Dir
    {
        get
        {
            if (OverrideDirForTests is { } o) return o;
            var dataDir = Environment.GetEnvironmentVariable("HOUSECARL_DATA_DIR");
            var root = string.IsNullOrWhiteSpace(dataDir) ? AppContext.BaseDirectory : dataDir;
            return Path.Combine(root, "results");
        }
    }

    /// <summary>Test seam: point the store at a temp directory. Never set in production code paths.</summary>
    public static string? OverrideDirForTests;

    /// <summary>Reserve a fresh artifact file for an auto-spill from <paramref name="tool"/> at build
    /// <paramref name="epoch"/>, pruning old spills on the way. The reservation IS the file: it is created with
    /// <c>FileMode.CreateNew</c> — rather than probed with File.Exists, because parallel tool calls would otherwise
    /// hand two same-second spills the same path — and the exclusive handle stays OPEN, so nothing can come between
    /// the name claim and the write the spill makes through it. The caller disposes the reservation.</summary>
    public static ArtifactTarget Reserve(string tool, string epoch)
    {
        var dir = Dir;
        // Best-effort: a throw here would surface as a generic tool error and eat the valid truncated response.
        // Letting it through lets Save name the write failure, which reaches the caller as a spill warning.
        try { Directory.CreateDirectory(dir); Prune(dir); } catch (Exception) { }
        var shortTool = tool.StartsWith("housecarl_", StringComparison.Ordinal) ? tool["housecarl_".Length..] : tool;
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var basePath = Path.Combine(dir, $"{shortTool}_{stamp}_{epoch}");
        for (int n = 1; ; n++)
        {
            var path = n == 1 ? basePath + ".jsonl" : $"{basePath}-{n}.jsonl";
            try
            {
                var held = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                return ArtifactTarget.Reserved(path, held);   // reserved: this call owns the name and the handle
            }
            catch (IOException) when (File.Exists(path)) { /* taken — try the next counter */ }
            catch (Exception) { return ArtifactTarget.Named(path); }   // bad dir, permissions — Save names it loud
        }
    }

    /// <summary>Delete spilled artifacts older than <see cref="PruneAfterDays"/> days, plus orphaned Writer temps
    /// (<c>*.jsonl.tmp-*</c>) a crash mid-write can strand: a caller-named target writes through one, and 2.0.1 and
    /// earlier wrote spills that way too, so this directory can still hold them from an older build. Best-effort per
    /// file — pruning is hygiene, not correctness;
    /// epoch-checked re-entry is what protects against stale artifacts.</summary>
    static void Prune(string dir)
    {
        var cutoff = DateTime.UtcNow.AddDays(-PruneAfterDays);
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*.jsonl").Concat(Directory.EnumerateFiles(dir, "*.jsonl.tmp-*")))
                try { if (File.GetLastWriteTimeUtc(f) < cutoff) File.Delete(f); }
                catch (IOException) { /* locked/raced — next write retries */ }
                catch (UnauthorizedAccessException) { /* same */ }
        }
        catch (Exception) { /* enumeration failure — hygiene only, never blocks the spill */ }
    }
}
