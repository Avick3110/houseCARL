namespace HousecarlCore;

/// <summary>A plugin changed on disk after the call's index was built, so the call refuses whole (ruling F);
/// contract in docs/architecture/load-order-resolver.md.</summary>
public sealed class LoadOrderChangedMidCallException(string plugin) : Exception(
    $"'{plugin}' changed on disk during this call, after houseCARL indexed it, so the answer would mix two states " +
    "of the load order; nothing was answered or written. Rerun the call.")
{
    public string Plugin { get; } = plugin;
}

/// <summary>The call-wide mark of ruling F: the index builds a call relied on, the off-order files it opened, and the
/// first change found, so the tool guard refuses the whole call even where a lane folded the throw into a row.</summary>
public static class MidCallChange
{
    sealed class Mark
    {
        public string? Plugin;
        public readonly Dictionary<string, FileStamp> Pinned = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Written = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<(string[] Names, string[] Paths, FileStamp[] Built)> Relied = new();
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
            bool wrote;
            lock (m) wrote = m.Written.Count > 0;
            return (wrote ? m.Plugin : Moved(m, seam: false)) is { } p ? new LoadOrderChangedMidCallException(p).Message : null;
        }

        public void Dispose() { if (owns) Current.Value = null; }
    }

    static string Key(string path) { try { return Path.GetFullPath(path); } catch { return path; } }

    static string Changed(Mark? m, string plugin)
    {
        if (m is not null) lock (m) m.Plugin ??= plugin;
        return plugin;
    }

    static bool WroteIt(Mark m, string path)
    {
        lock (m) if (m.Written.Count == 0) return false;
        var key = Key(path);
        lock (m) return m.Written.Contains(key);
    }

    /// <summary>The call relies on one index build: its plugins are re-statted against <paramref name="built"/> before it answers or commits.</summary>
    internal static void Relied(string[] names, string[] paths, FileStamp[] built)
    {
        if (Current.Value is not { } m) return;
        lock (m)
        {
            foreach (var r in m.Relied) if (ReferenceEquals(r.Built, built)) return;
            m.Relied.Add((names, paths, built));
        }
    }

    /// <summary>An active plugin about to be opened: its file must still carry <paramref name="built"/>, the stamp its build read.</summary>
    internal static void Check(string plugin, string path, FileStamp built)
    {
        var m = Current.Value;
        if (m is not null && WroteIt(m, path)) return;
        BeforeCheck.Value?.Invoke(plugin);
        if (FileStamp.Of(path) != built) throw new LoadOrderChangedMidCallException(Changed(m, plugin));
    }

    /// <summary>A file outside the index: the call's first sight of it sets its stamp, which every later open and check must find.</summary>
    public static void PinOffOrder(string path)
    {
        if (Current.Value is not { } m || WroteIt(m, path)) return;
        var name = Path.GetFileName(path);
        BeforeCheck.Value?.Invoke(name);
        var now = FileStamp.Of(path);
        var key = Key(path);
        lock (m) if (m.Pinned.TryAdd(key, now) || m.Pinned[key] == now) return;
        throw new LoadOrderChangedMidCallException(Changed(m, name));
    }

    /// <summary>A write's last check before it commits: no read found a change, and every file the call relied on still carries its stamp.</summary>
    public static void BeforeCommit()
    {
        if (Current.Value is { } m && Moved(m, seam: true) is { } p) throw new LoadOrderChangedMidCallException(p);
    }

    /// <summary>The call wrote <paramref name="path"/>; its own change is not a change under it.</summary>
    public static void AfterCommit(string path)
    {
        if (Current.Value is { } m) { var key = Key(path); lock (m) m.Written.Add(key); }
    }

    /// <summary>The first change a read found, else the first pinned or relied-on file whose stamp moved, the call's own writes excepted.</summary>
    static string? Moved(Mark m, bool seam)
    {
        KeyValuePair<string, FileStamp>[] pinned;
        (string[] Names, string[] Paths, FileStamp[] Built)[] relied;
        HashSet<string>? written;
        lock (m)
        {
            if (m.Plugin is { } p) return p;
            written = m.Written.Count > 0 ? new HashSet<string>(m.Written, StringComparer.OrdinalIgnoreCase) : null;
            pinned = m.Pinned.Where(w => written is null || !written.Contains(w.Key)).ToArray();
            relied = m.Relied.ToArray();
        }
        foreach (var (path, stamp) in pinned)
        {
            if (seam) BeforeCheck.Value?.Invoke(Path.GetFileName(path));
            if (FileStamp.Of(path) != stamp) return Changed(m, Path.GetFileName(path));
        }
        foreach (var (names, paths, built) in relied)
            for (int i = 0; i < built.Length; i++)
                if (FileStamp.Of(paths[i]) != built[i] && (written is null || !written.Contains(Key(paths[i]))))
                    return Changed(m, names[i]);
        return null;
    }
}
