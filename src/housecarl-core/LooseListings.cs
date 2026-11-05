using System.Collections.Concurrent;
using System.IO.Enumeration;

namespace HousecarlCore;

// LooseListings — one listing per loose directory per call, so every loose read in a call (resolve, walk, root files,
// the freshness check, an enabled folder's off-order probe) answers off the same read; and the one join of a Data
// path onto a disk folder. Contract in docs/architecture/assets.md.

/// <summary>One call's loose directory listings: each directory is listed at its first touch and kept until the call
/// ends, a directory that will not list keeps its failure for the call, and nothing outlives the object. Outside a
/// call, one <see cref="AssetResolver.Capture"/> pass holds one.</summary>
public sealed class LooseListings
{
    /// <summary>One directory's listing: every name in enumeration order, and the folders among them; the files are
    /// the rest, made into a set only where a folder is resolved from, since most listed folders are only walked.</summary>
    internal sealed class Listing(HashSet<string> names, HashSet<string> dirs)
    {
        HashSet<string>? _files;
        public HashSet<string> Names { get; } = names;
        public bool IsFile(string name) => Names.Contains(name) && !dirs.Contains(name);
        public HashSet<string> Files => LazyInitializer.EnsureInitialized(ref _files,
            () => new HashSet<string>(Names.Where(n => !dirs.Contains(n)), StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>A listing, or why the directory would not list.</summary>
    internal readonly record struct Read(Listing? Listing, Exception? Failure);

    readonly ConcurrentDictionary<string, Lazy<Read>> _reads = new(StringComparer.OrdinalIgnoreCase);
    int _count;

    /// <summary>The loose subtrees this call has checked against its own listings, or warmed from them.</summary>
    internal readonly ConcurrentDictionary<string, AssetResolver.LooseSubtree> Subtrees = new(StringComparer.OrdinalIgnoreCase);

    AssetResolver.Snapshot? _checked;
    int _checkedWarms;

    /// <summary>The freshness check found <paramref name="build"/> fresh, off these listings, for every subtree among
    /// its first <paramref name="warms"/> warms.</summary>
    internal void Checked(AssetResolver.Snapshot build, int warms) { _checkedWarms = warms; Volatile.Write(ref _checked, build); }

    /// <summary>Did this call's freshness check cover warm number <paramref name="warm"/> of <paramref name="build"/>?</summary>
    internal bool CheckedUpTo(AssetResolver.Snapshot build, int warm) => ReferenceEquals(Volatile.Read(ref _checked), build) && warm <= _checkedWarms;

    /// <summary>How many directories this call has listed; a test seam.</summary>
    internal int ListingCount => Volatile.Read(ref _count);

    /// <summary>The one join of a Data-relative path, or a folder name, onto a disk folder. On Windows it is the
    /// plain join; a case-folding match per segment is where a case-sensitive file system would need one.</summary>
    public static string OnDisk(string folder, string logical) => Path.Combine(folder, logical);

    /// <summary>As <see cref="OnDisk(string, string)"/> over two segments.</summary>
    public static string OnDisk(string folder, string logical, string more) => Path.Combine(folder, logical, more);

    /// <summary>This call's listing of <paramref name="dir"/>, taken now if it is the first touch;
    /// <paramref name="fresh"/> runs only when this read took it.</summary>
    internal Read Get(string dir, Action? fresh = null) =>
        _reads.GetOrAdd(dir, d => new Lazy<Read>(() =>
        {
            Interlocked.Increment(ref _count);
            fresh?.Invoke();
            try { return new Read(List(d), null); }
            catch (Exception ex) { return new Read(null, ex); }
        })).Value;

    /// <summary>This call's listing of <paramref name="dir"/>, or null when it would not list.</summary>
    internal Listing? TryGet(string dir, Action? fresh = null) => Get(dir, fresh).Listing;

    /// <summary>The one enumeration every listing rides; throws when the directory will not list.</summary>
    internal static Listing List(string dir)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Only FILES may resolve: a directory reported as an asset is the silently wrong answer. Names only, no
        // FileSystemInfo per entry: a call can list every folder under meshes\.
        foreach (var (name, isDir) in new FileSystemEnumerable<(string, bool)>(dir,
                     (ref FileSystemEntry e) => (e.FileName.ToString(), e.IsDirectory), Everything))
        {
            names.Add(name);
            if (isDir) dirs.Add(name);
        }
        return new Listing(names, dirs);
    }

    /// <summary>What <see cref="DirectoryInfo.EnumerateFileSystemInfos()"/> lists: hidden and system entries too, and
    /// a folder that refuses throws.</summary>
    static readonly EnumerationOptions Everything = new() { AttributesToSkip = 0, IgnoreInaccessible = false };
}
