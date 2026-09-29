using System.Buffers.Binary;
using System.IO.Abstractions;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Headers;
using Mutagen.Bethesda.Plugins.Meta;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace HousecarlCore;

/// <summary>Per record, the multiset of subrecord signatures a plugin's bytes carry, and the in-place round-trip check
/// built on it (#961): the unedited target serialized in memory must keep every signature its file holds. Contract in
/// docs/architecture/write-path.md.</summary>
public static class SubrecordInventory
{
    /// <summary>The (lost, gained) signature pairs the unedited round trip rewrites one for one, and nothing else; measured
    /// over every plugin the ARR instance serves in dev/plans/INPLACE_ROUNDTRIP_MEASURE_2026-09-28.md.</summary>
    internal static readonly IReadOnlyList<(string Lost, string Gained)> Renames = new[] { ("BODT", "BOD2") };

    /// <summary>How many records the refusal names before it falls back to a count.</summary>
    const int RecordsNamed = 5;

    /// <summary>One record's signature, and its subrecord signatures counted in first-seen order.</summary>
    public sealed class RecordEntry
    {
        public string Signature { get; init; } = "";
        public Dictionary<string, int> Counts { get; } = new(StringComparer.Ordinal);
        public List<string> Order { get; } = new();

        internal void Add(string sig)
        {
            if (Counts.TryGetValue(sig, out var n)) { Counts[sig] = n + 1; return; }
            Counts[sig] = 1;
            Order.Add(sig);
        }
    }

    /// <summary>One record's subrecords the round trip lost and gained, before any rename is paired off.</summary>
    public sealed record RecordDiff(FormKey Key, string Signature, IReadOnlyList<(string Sig, int Count)> Lost,
                                    IReadOnlyList<(string Sig, int Count)> Gained);

    /// <summary>What the refusal tells the caller to do instead, per lane, for one record and for several.</summary>
    public sealed record Remedy(string One, string Many)
    {
        /// <summary>The record lanes: apply, create, remove, forward.</summary>
        public static readonly Remedy RecordLane = new(
            "drop in_place= and write the change into a new plugin, leaving that record out of it (an override of it loses " +
            "the same subrecords), or fix that record in xEdit first",
            "drop in_place= and write the change into a new plugin, leaving those records out of it (an override of one " +
            "loses the same subrecords), or fix those records in xEdit first");

        /// <summary>An in-place compact of the target itself.</summary>
        public static readonly Remedy CompactTarget = new(
            "fix that record in xEdit first; compacting into a new plugin (in_place=false) leaves the original alone, but " +
            "its copy of that record loses the same subrecords",
            "fix those records in xEdit first; compacting into a new plugin (in_place=false) leaves the original alone, but " +
            "its copies of those records lose the same subrecords");

        /// <summary>An external referencer the compact would repoint in place.</summary>
        public static readonly Remedy Referencer = new(
            "fix that record in xEdit first, or compact without repoint_externals and handle that plugin's references yourself",
            "fix those records in xEdit first, or compact without repoint_externals and handle that plugin's references yourself");
    }

    /// <summary>Null when the unedited round trip keeps every subrecord signature the file holds, else the one refusal
    /// sentence. The serialize is handed the target's own declared masters, opened through <paramref name="masterPath"/>
    /// (null for one that is absent or unopenable). A localized target returns null: <see cref="WriteEngine.WriteInPlace"/>
    /// refuses it before staging, so no write this check could have stopped ever lands. Any serialize fault other than a
    /// missing master propagates for the caller's own serialize-failure arm.</summary>
    public static string? RoundTripRefusal(SkyrimMod parsed, string path, Func<string, string?> masterPath, Remedy remedy)
    {
        if (parsed.UsingLocalization) return null;
        var overlays = new List<ISkyrimModDisposableGetter>();
        try
        {
            foreach (var mr in parsed.ModHeader.MasterReferences)
                if (masterPath(mr.Master.FileName.String) is { } mp && File.Exists(mp))
                    overlays.Add(SkyrimMod.CreateFromBinaryOverlay(mp, SkyrimRelease.SkyrimSE, PluginTextEncoding.ReadFor(mp)));
            var fileBytes = File.ReadAllBytes(path);
            var written = SerializeToMemory(parsed, overlays, path);
            var losses = Losses(Diff(Walk(fileBytes, parsed.ModKey), Walk(written, parsed.ModKey)), Renames);
            return losses.Count == 0 ? null : Refusal(Path.GetFileName(path), losses, parsed, remedy);
        }
        finally { foreach (var o in overlays) { try { o.Dispose(); } catch { /* best-effort; never mask the check */ } } }
    }

    /// <summary>The same check over a plugin this call has not opened yet. A file that does not parse returns null,
    /// because the lane that would rewrite it refuses an unparseable plugin itself before writing.</summary>
    public static string? RoundTripRefusalAt(string path, Func<string, string?> masterPath, Remedy remedy)
    {
        SkyrimMod parsed;
        try { parsed = SkyrimMod.CreateFromBinary(path, SkyrimRelease.SkyrimSE, PluginTextEncoding.ReadFor(path)); }
        catch { return null; }
        return RoundTripRefusal(parsed, path, masterPath, remedy);
    }

    /// <summary>The bytes the in-place write would stage for this mod, through the same serialize, held in memory only.
    /// A master the load order lacks is re-tried with no load order at all: the order only sorts the header's master
    /// list, which the walk maps away, and the lane's own write still meets that missing master in its own words.</summary>
    public static byte[] SerializeToMemory(SkyrimMod parsed, IReadOnlyList<ISkyrimModGetter> masters, string path)
    {
        var ordered = masters as ISkyrimModGetter[] ?? masters.ToArray();
        try { return Capture(parsed, ordered, path); }
        catch (Exception ex) when (IsMissingMod(ex)) { return Capture(parsed, null, path); }
    }

    static byte[] Capture(SkyrimMod parsed, ISkyrimModGetter[]? ordered, string path)
    {
        var capture = new CaptureFileSystem();
        WriteEngine.SerializeInPlace(parsed, ordered, path, path, capture);
        return capture.Bytes ?? throw new InvalidOperationException(
            $"the in-memory round trip of '{Path.GetFileName(path)}' produced no bytes, so its subrecords could not be compared.");
    }

    static bool IsMissingMod(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
            if (ex is Mutagen.Bethesda.Plugins.Exceptions.MissingModException) return true;
        return false;
    }

    /// <summary>Every major record in a plugin's bytes, keyed by FormKey through that file's own master list, with its
    /// subrecord signatures counted; compressed records are inflated, and an XXXX length carrier is framing, not counted.</summary>
    public static Dictionary<FormKey, RecordEntry> Walk(byte[] bytes, ModKey self)
    {
        var meta = GameConstants.SkyrimSE;
        var all = new ReadOnlyMemorySlice<byte>(bytes);
        var into = new Dictionary<FormKey, RecordEntry>();
        var header = new MajorRecordFrame(meta, all);
        var masters = new List<ModKey>();
        foreach (var (sig, data) in Subrecords(header.Content))
            if (sig == "MAST") masters.Add(ModKey.FromFileName(Encoding.Latin1.GetString(data.Span).TrimEnd('\0')));
        long pos = header.TotalLength;
        while (pos < bytes.Length) pos += WalkGroup(meta, all.Slice(checked((int)pos)), masters, self, into);
        return into;
    }

    static long WalkGroup(GameConstants meta, ReadOnlyMemorySlice<byte> at, List<ModKey> masters, ModKey self,
                          Dictionary<FormKey, RecordEntry> into)
    {
        var group = new GroupFrame(meta, at);
        long end = group.TotalLength;
        long p = group.HeaderLength;
        while (p < end)
        {
            var here = at.Slice(checked((int)p));
            if (Sig(here.Span) == "GRUP") { p += WalkGroup(meta, here, masters, self, into); continue; }
            var rec = new MajorRecordFrame(meta, here);
            var raw = rec.FormID.Raw;
            int index = (int)(raw >> 24);
            var key = new FormKey(index < masters.Count ? masters[index] : self, raw & 0xFFFFFF);
            if (!into.TryGetValue(key, out var entry)) into[key] = entry = new RecordEntry { Signature = rec.RecordType.Type };
            foreach (var (sig, _) in Subrecords(rec.IsCompressed ? Inflate(rec.Content) : rec.Content)) entry.Add(sig);
            p += rec.TotalLength;
        }
        return end;
    }

    static ReadOnlyMemorySlice<byte> Inflate(ReadOnlyMemorySlice<byte> content)
    {
        var size = BinaryPrimitives.ReadUInt32LittleEndian(content.Span);
        var outBuf = new byte[size];
        using var z = new ZLibStream(new MemoryStream(content.Slice(4).ToArray()), CompressionMode.Decompress);
        z.ReadExactly(outBuf);
        return new ReadOnlyMemorySlice<byte>(outBuf);
    }

    /// <summary>A record body's subrecords in order: signature plus payload, an XXXX carrier folded into the length it sets.</summary>
    static IEnumerable<(string Sig, ReadOnlyMemorySlice<byte> Data)> Subrecords(ReadOnlyMemorySlice<byte> body)
    {
        int p = 0;
        int? overflow = null;
        while (p + 6 <= body.Length)
        {
            var sig = Sig(body.Span.Slice(p));
            int len = BinaryPrimitives.ReadUInt16LittleEndian(body.Span.Slice(p + 4));
            p += 6;
            if (overflow is { } big) { len = big; overflow = null; }
            if (sig == "XXXX") { overflow = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(body.Span.Slice(p))); p += len; continue; }
            yield return (sig, body.Slice(p, len));
            p += len;
        }
    }

    static string Sig(ReadOnlySpan<byte> at) => Encoding.Latin1.GetString(at.Slice(0, 4));

    /// <summary>Per record of the FILE, what the written side lacks and what it added, signature by signature.</summary>
    public static List<RecordDiff> Diff(Dictionary<FormKey, RecordEntry> file, Dictionary<FormKey, RecordEntry> written)
    {
        var diffs = new List<RecordDiff>();
        foreach (var (key, f) in file)
        {
            written.TryGetValue(key, out var w);
            var lost = f.Order.Select(s => (s, f.Counts[s] - Count(w, s))).Where(x => x.Item2 > 0).ToList();
            var gained = w is null ? new List<(string, int)>()
                : w.Order.Select(s => (s, w.Counts[s] - Count(f, s))).Where(x => x.Item2 > 0).ToList();
            if (lost.Count > 0 || gained.Count > 0) diffs.Add(new RecordDiff(key, f.Signature, lost, gained));
        }
        return diffs;
    }

    static int Count(RecordEntry? e, string sig) => e is not null && e.Counts.TryGetValue(sig, out var n) ? n : 0;

    /// <summary>The diffs that still lose a signature once each listed rename's loss is paired with its gain in the same record.</summary>
    public static List<RecordDiff> Losses(List<RecordDiff> diffs, IReadOnlyList<(string Lost, string Gained)> renames)
    {
        var losses = new List<RecordDiff>();
        foreach (var d in diffs)
        {
            var lost = d.Lost.ToDictionary(x => x.Sig, x => x.Count, StringComparer.Ordinal);
            var gained = d.Gained.ToDictionary(x => x.Sig, x => x.Count, StringComparer.Ordinal);
            foreach (var (l, g) in renames)
                if (lost.TryGetValue(l, out var n) && gained.TryGetValue(g, out var m))
                {
                    var paired = Math.Min(n, m);
                    lost[l] = n - paired;
                    gained[g] = m - paired;
                }
            var left = d.Lost.Where(x => lost[x.Sig] > 0).Select(x => (x.Sig, lost[x.Sig])).ToList();
            if (left.Count > 0) losses.Add(d with { Lost = left });
        }
        return losses;
    }

    /// <summary>The one refusal: which records, which subrecords, why, and what to do instead.</summary>
    static string Refusal(string fileName, IReadOnlyList<RecordDiff> losses, SkyrimMod parsed, Remedy remedy)
    {
        var named = losses.Take(RecordsNamed).Select(d => d.Key).ToHashSet();
        var types = new Dictionary<FormKey, string>();
        foreach (var r in parsed.EnumerateMajorRecords())
            if (named.Contains(r.FormKey)) types.TryAdd(r.FormKey, RecordNaming.StripOverlay(r.GetType().Name));
        string Name(RecordDiff d) => (types.TryGetValue(d.Key, out var t) ? t : d.Signature) + " " + FormIdToken.Of(d.Key);
        string Sigs(RecordDiff d) => string.Join(", ", d.Lost.Select(x => x.Count > 1 ? $"{x.Sig} x{x.Count}" : x.Sig));
        var who = $"houseCARL (Mutagen {MutagenVersion})";
        if (losses.Count == 1)
            return $"refused: {who} cannot write {Name(losses[0])} back as the file holds it, so rewriting '{fileName}' " +
                   $"in place would drop its {Sigs(losses[0])} (#961); '{fileName}' is UNTOUCHED — {remedy.One}.";
        var list = string.Join("; ", losses.Take(RecordsNamed).Select(d => $"{Name(d)} ({Sigs(d)})"));
        var more = losses.Count > RecordsNamed ? $"; and {losses.Count - RecordsNamed} more record(s)" : "";
        return $"refused: {who} cannot write {losses.Count} records back as the file holds them, so rewriting " +
               $"'{fileName}' in place would drop subrecords from each — {list}{more} (#961); '{fileName}' is UNTOUCHED — " +
               $"{remedy.Many}.";
    }

    /// <summary>The Mutagen release the parser is, off the assembly houseCARL loaded rather than a copy of the pin.</summary>
    static readonly string MutagenVersion =
        typeof(SkyrimMod).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(SkyrimMod).Assembly.GetName().Version?.ToString(3) ?? "unknown";

    /// <summary>A file system that accepts the one create the serialize makes and keeps it in memory; anything else throws,
    /// so a Mutagen change that touched another path fails loud instead of reaching the disk.</summary>
    sealed class CaptureFileSystem : IFileSystem
    {
        readonly CaptureStreams _streams;
        public CaptureFileSystem() { _streams = new CaptureStreams(this); }
        public byte[]? Bytes => _streams.Captured?.ToArray();

        public IFileStreamFactory FileStream => _streams;
        public IDirectory Directory => throw Unsupported(nameof(Directory));
        public IDirectoryInfoFactory DirectoryInfo => throw Unsupported(nameof(DirectoryInfo));
        public IDriveInfoFactory DriveInfo => throw Unsupported(nameof(DriveInfo));
        public IFile File => throw Unsupported(nameof(File));
        public IFileInfoFactory FileInfo => throw Unsupported(nameof(FileInfo));
        public IFileSystemWatcherFactory FileSystemWatcher => throw Unsupported(nameof(FileSystemWatcher));
        public IFileVersionInfoFactory FileVersionInfo => throw Unsupported(nameof(FileVersionInfo));
        public System.IO.Abstractions.IPath Path => throw Unsupported(nameof(Path));

        internal static NotSupportedException Unsupported(string what) =>
            new($"the in-memory round trip supports only a FileStream create, and the serializer asked for {what}.");
    }

    sealed class CaptureStreams : IFileStreamFactory
    {
        public CaptureStreams(IFileSystem fs) { FileSystem = fs; }
        public IFileSystem FileSystem { get; }
        public MemoryStream? Captured { get; private set; }

        public FileSystemStream New(string path, FileMode mode, FileAccess access)
        {
            if (Captured is not null) throw CaptureFileSystem.Unsupported("a second stream");
            Captured = new MemoryStream();
            return new CaptureStream(Captured, path);
        }

        public FileSystemStream New(Microsoft.Win32.SafeHandles.SafeFileHandle handle, FileAccess access) => throw CaptureFileSystem.Unsupported("a handle stream");
        public FileSystemStream New(Microsoft.Win32.SafeHandles.SafeFileHandle handle, FileAccess access, int bufferSize) => throw CaptureFileSystem.Unsupported("a handle stream");
        public FileSystemStream New(Microsoft.Win32.SafeHandles.SafeFileHandle handle, FileAccess access, int bufferSize, bool isAsync) => throw CaptureFileSystem.Unsupported("a handle stream");
        public FileSystemStream New(string path, FileMode mode) => throw CaptureFileSystem.Unsupported("another FileStream shape");
        public FileSystemStream New(string path, FileMode mode, FileAccess access, FileShare share) => throw CaptureFileSystem.Unsupported("another FileStream shape");
        public FileSystemStream New(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize) => throw CaptureFileSystem.Unsupported("another FileStream shape");
        public FileSystemStream New(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize, bool useAsync) => throw CaptureFileSystem.Unsupported("another FileStream shape");
        public FileSystemStream New(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize, FileOptions options) => throw CaptureFileSystem.Unsupported("another FileStream shape");
        public FileSystemStream New(string path, FileStreamOptions options) => throw CaptureFileSystem.Unsupported("another FileStream shape");
        public FileSystemStream Wrap(FileStream fileStream) => throw CaptureFileSystem.Unsupported("a wrapped stream");
    }

    sealed class CaptureStream : FileSystemStream
    {
        public CaptureStream(MemoryStream inner, string path) : base(inner, path, isAsync: false) { }
    }
}
