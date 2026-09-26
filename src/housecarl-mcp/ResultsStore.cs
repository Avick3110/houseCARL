using HousecarlCore;

namespace HousecarlMcp;

/// <summary>The server-managed directory for auto-spilled result artifacts, named <c>&lt;tool&gt;_&lt;utc stamp&gt;_&lt;epoch&gt;.jsonl</c>; contract in docs/architecture/output-and-artifacts.md.</summary>
static class ResultsStore
{
    public const int PruneAfterDays = 7;

    /// <summary>Reserve a fresh artifact file in <paramref name="dir"/> (created on demand) for an auto-spill from <paramref name="tool"/> at build <paramref name="epoch"/>, pruning old spills on the way; the reservation IS the file, and the caller disposes it.</summary>
    public static ArtifactTarget Reserve(string dir, string tool, string epoch)
    {
        // Best-effort: Save names a write failure as a spill warning, rather than a generic tool error here.
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

    /// <summary>Delete spilled artifacts older than <see cref="PruneAfterDays"/> days, plus orphaned <c>*.jsonl.tmp-*</c> Writer temps; best-effort hygiene, since epoch-checked re-entry is what catches a stale artifact.</summary>
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
