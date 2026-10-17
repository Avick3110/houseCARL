namespace HousecarlCore;

/// <summary>A plugin changed on disk after the call's index was built, so the call refuses whole (ruling F);
/// contract in docs/architecture/load-order-resolver.md.</summary>
public sealed class LoadOrderChangedMidCallException(string plugin) : Exception(
    $"'{plugin}' changed on disk during this call, after houseCARL indexed it, so the answer would mix two states " +
    "of the load order; nothing was answered or written. Rerun the call.")
{
    public string Plugin { get; } = plugin;
}

/// <summary>The call-wide mark of ruling F: per index build, the plugins the call answered from and opened; the
/// off-order files it opened; and the first change found, so the tool guard refuses the whole call even where a lane
/// folded the throw into a row.</summary>
public static class MidCallChange
{
    /// <summary>One index build a call relied on; Answered and Opened are per plugin index and only ever set to true, so they need no lock.</summary>
    internal sealed class Build(string[] names, string[] paths, FileStamp[] stamps)
    {
        public readonly string[] Names = names, Paths = paths;
        public readonly FileStamp[] Stamps = stamps;
        public readonly bool[] Answered = new bool[stamps.Length], Opened = new bool[stamps.Length];
    }

    sealed class Mark
    {
        public string? Plugin;
        public readonly Dictionary<string, FileStamp> Pinned = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Written = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<Build> Relied = new();
    }

    static readonly AsyncLocal<Mark?> Current = new();

    /// <summary>Test seam: runs with the file's name just before each open's stamp check and each pinned file's commit check.</summary>
    internal static readonly AsyncLocal<Action<string>?> BeforeCheck = new();

    /// <summary>Start one call's mark; a nested tool body shares the outer call's. Dispose ends it.</summary>
    public static Scope Begin()
    {
        var outer = Current.Value;
        if (outer is null) Current.Value = new Mark();
        return new Scope(outer is null);
    }

    public readonly struct Scope(bool owns) : IDisposable
    {
        /// <summary>The call's refusal or null. A call that committed a write was re-checked at its commit and answers what it wrote.</summary>
        public string? Settle()
        {
            if (Current.Value is not { } m) return null;
            lock (m)
                return (m.Written.Count > 0 ? m.Plugin : Moved(m, commit: false)) is not { } p ? null
                    : m.Written.Count == 0 ? new LoadOrderChangedMidCallException(p).Message
                    : $"'{p}' changed on disk during this call, after houseCARL had written {string.Join(", ", m.Written.Select(Path.GetFileName))}; " +
                      "the rest was not done. Check those files before rerunning.";
        }

        public void Dispose() { if (owns) Current.Value = null; }
    }

    static string Key(string path) { try { return Path.GetFullPath(path); } catch { return path; } }

    static string Changed(Mark? m, string plugin)
    {
        if (m is not null) lock (m) m.Plugin ??= plugin;
        return plugin;
    }

    static bool WroteIt(Mark m, string path) { lock (m) return m.Written.Count > 0 && m.Written.Contains(Key(path)); }

    /// <summary>The call relies on one index build; null outside a call.</summary>
    internal static Build? Relied(string[] names, string[] paths, FileStamp[] stamps)
    {
        if (Current.Value is not { } m) return null;
        lock (m)
        {
            foreach (var b in m.Relied) if (ReferenceEquals(b.Stamps, stamps)) return b;
            m.Relied.Add(new Build(names, paths, stamps));
            return m.Relied[^1];
        }
    }

    /// <summary>Plugin <paramref name="idx"/> is about to be opened: its file must still carry the stamp its build read.</summary>
    internal static void Check(Build? build, string[] names, string[] paths, FileStamp[] stamps, int idx)
    {
        var m = Current.Value;
        if (m is not null && WroteIt(m, paths[idx])) return;
        if (build is not null) build.Opened[idx] = true;
        BeforeCheck.Value?.Invoke(names[idx]);
        if (FileStamp.Of(paths[idx]) != stamps[idx]) throw new LoadOrderChangedMidCallException(Changed(m, names[idx]));
    }

    /// <summary>A file opened by path: an active plugin of the call's build is checked against its build stamp; a file
    /// outside the index is pinned at first sight, and every later open and check must find that stamp.</summary>
    public static void PinOffOrder(string path)
    {
        if (Current.Value is not { } m || WroteIt(m, path)) return;
        var (name, full) = (Path.GetFileName(path), Key(path));
        Build? b = null; int i = -1;
        lock (m) for (int k = m.Relied.Count - 1; k >= 0 && b is null; k--)
            if ((i = Array.FindIndex(m.Relied[k].Paths, p => p.EndsWith(name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Key(p), full, StringComparison.OrdinalIgnoreCase))) >= 0) b = m.Relied[k];
        if (b is not null) { Check(b, b.Names, b.Paths, b.Stamps, i); return; }
        BeforeCheck.Value?.Invoke(name);
        var now = FileStamp.Of(path);
        lock (m) if (m.Pinned.TryAdd(full, now) || m.Pinned[full] == now) return;
        throw new LoadOrderChangedMidCallException(Changed(m, name));
    }

    /// <summary>A write's last check before it commits: no read found a change, and every file the call read or answered from still carries its stamp.</summary>
    public static void BeforeCommit()
    {
        if (Current.Value is not { } m) return;
        string? p;
        lock (m) p = Moved(m, commit: true);
        if (p is not null) throw new LoadOrderChangedMidCallException(p);
    }

    /// <summary>The call wrote <paramref name="path"/>; its own change is not a change under it.</summary>
    public static void AfterCommit(string path)
    {
        if (Current.Value is { } m) lock (m) m.Written.Add(Key(path));
    }

    /// <summary>Under the mark's lock: the first change a read found, else the first pinned file, or plugin the call
    /// answered from (and at a commit, opened), whose stamp moved; the call's own writes excepted.</summary>
    static string? Moved(Mark m, bool commit)
    {
        if (m.Plugin is not null) return m.Plugin;
        foreach (var (path, stamp) in m.Pinned)
        {
            if (m.Written.Contains(path)) continue;
            if (commit) BeforeCheck.Value?.Invoke(Path.GetFileName(path));
            if (FileStamp.Of(path) != stamp) return m.Plugin = Path.GetFileName(path);
        }
        foreach (var b in m.Relied)
            for (int i = 0; i < b.Stamps.Length; i++)
                if ((b.Answered[i] || commit && b.Opened[i]) && FileStamp.Of(b.Paths[i]) != b.Stamps[i]
                    && (m.Written.Count == 0 || !m.Written.Contains(Key(b.Paths[i]))))
                    return m.Plugin = b.Names[i];
        return null;
    }
}
