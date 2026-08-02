namespace HousecarlMcp;

/// <summary>The server-managed results directory for AUTO-SPILLED §2.1.1 artifacts (a caller-named
/// <c>to_file=</c> target never comes here). Decisions this class pins (Phase-4 detail the SPEC delegated):
///
/// <list type="bullet">
/// <item><b>Location:</b> <c>&lt;HOUSECARL_DATA_DIR&gt;\results</c> — the plugin's own persistent data folder,
/// falling back to the server binary's folder when the env var is absent (the same resolution Program.cs uses for
/// user config, so results live beside the config that produced them, never inside the MO2 instance).</item>
/// <item><b>Naming:</b> <c>&lt;tool&gt;_&lt;utc yyyyMMdd-HHmmss&gt;_&lt;epoch&gt;.jsonl</c> (tool minus the
/// <c>housecarl_</c> prefix; epoch = the build fingerprint the result was read from) — sortable by time, and the
/// epoch is visible in a directory listing before a single file is opened. A same-second collision appends a
/// counter rather than overwriting: artifacts are immutable once written.</item>
/// <item><b>Prune-by-age at write:</b> spilled artifacts older than <see cref="PruneAfterDays"/> days are deleted
/// (best-effort, per-file) each time a new spill is written — no daemon, no timer, no state; write time is the one
/// moment the server is already touching the directory. 7 days: an artifact outlives any working session that
/// produced it, while a stale one is already refusing epoch-checked re-entry long before it's pruned. Only
/// <c>*.jsonl</c> in THIS directory is ever touched.</item>
/// </list></summary>
static class ResultsStore
{
    public const int PruneAfterDays = 7;

    /// <summary>The results directory (created on demand). Overridable for tests via
    /// <see cref="OverrideDirForTests"/> — production resolution is env-var → binary folder.</summary>
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

    /// <summary>Reserve a fresh artifact path for an auto-spill from <paramref name="tool"/> at build
    /// <paramref name="epoch"/>, pruning old spills on the way (the write-time prune). The reservation is ATOMIC —
    /// the file is CREATED (empty) here with <c>FileMode.CreateNew</c>, not merely probed with File.Exists, because
    /// parallel tool calls are a normal client pattern and a check-then-write race would hand two same-second
    /// spills the same path, one silently overwriting the other (PR #306 review). The Writer's temp-then-move
    /// replaces the empty reservation wholesale; a spill that later FAILS deletes its reservation via
    /// <see cref="Release"/>. A same-second collision gets a counter suffix.</summary>
    public static string NextPath(string tool, string epoch)
    {
        var dir = Dir;
        // Best-effort here: if the directory cannot be created, the Writer's Save on the returned path produces
        // the NAMED write failure, which flows into the response as the failed-spill warning — a throw here would
        // instead surface as a generic tool error and eat the (valid, truncated) response entirely.
        try { Directory.CreateDirectory(dir); Prune(dir); } catch (Exception) { }
        var shortTool = tool.StartsWith("housecarl_", StringComparison.Ordinal) ? tool["housecarl_".Length..] : tool;
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var basePath = Path.Combine(dir, $"{shortTool}_{stamp}_{epoch}");
        for (int n = 1; ; n++)
        {
            var path = n == 1 ? basePath + ".jsonl" : $"{basePath}-{n}.jsonl";
            try
            {
                using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
                return path;   // reserved: this call owns the name
            }
            catch (IOException) when (File.Exists(path)) { /* taken — try the next counter */ }
            catch (Exception) { return path; }   // non-collision failure (bad dir, permissions) — Save names it loud
        }
    }

    /// <summary>Delete a reservation whose spill FAILED — best-effort (the failure is already named in the
    /// response; an empty leftover would otherwise linger until the age prune).</summary>
    public static void Release(string path)
    {
        try { File.Delete(path); } catch (Exception) { }
    }

    /// <summary>Delete spilled artifacts older than <see cref="PruneAfterDays"/> days — and orphaned Writer temps
    /// (<c>*.jsonl.tmp-*</c>) on the same clock: a failed Save deletes its own temp best-effort, but a crash
    /// mid-write can still strand one, and full-size strays in the server's own data dir are exactly what this
    /// hygiene pass exists for (PR #306 review). Best-effort per file — a locked or vanished file is skipped,
    /// never fatal (pruning is hygiene, not correctness; epoch-checked re-entry is what protects against
    /// staleness).</summary>
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
