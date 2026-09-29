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
        /// <summary>The in-place record lanes: apply, create, forward.</summary>
        public static readonly Remedy RecordLane = new(
            "drop in_place= and write the change into a new plugin, leaving that record out of it (an override of it loses " +
            "the same subrecords), or fix that record in xEdit first",
            "drop in_place= and write the change into a new plugin, leaving those records out of it (an override of one " +
            "loses the same subrecords), or fix those records in xEdit first");

        /// <summary>The into= lanes, which rewrite an existing patch: apply, create, forward, copy.</summary>
        public static readonly Remedy Extend = new(
            "drop into= and write the change into a new patch, leaving that record out of it (an override of it loses the " +
            "same subrecords), or fix that record in xEdit first",
            "drop into= and write the change into a new patch, leaving those records out of it (an override of one loses " +
            "the same subrecords), or fix those records in xEdit first");

        /// <summary>Both remove lanes; a record being removed is never counted, so fixing the other one is the way on.</summary>
        public static readonly Remedy Remove = new(
            "fix that record in xEdit first, or remove it in the same call if it should go too",
            "fix those records in xEdit first, or remove them in the same call if they should go too");

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

    /// <summary>Null when the unedited round trip over the write's masters loses nothing outside the records the op drops whole, else one refusal.</summary>
    public static string? RoundTripRefusal(SkyrimMod parsed, string path, IReadOnlyList<ISkyrimModGetter> masters, Remedy remedy,
                                           IReadOnlySet<FormKey>? dropped = null)
    {
        if (parsed.UsingLocalization) return null;   // WriteInPlace and WritePatch refuse a localized file before staging
        var fileName = Path.GetFileName(path);
        byte[]? written;
        try { written = SerializeToMemory(parsed, masters, path); }
        catch (Exception ex) when (Find<CaptureUnsupportedException>(ex) is { } cu) { return CouldNotRun(fileName, cu.Message); }
        if (written is null) return CouldNotRun(fileName, "the in-memory serialize produced no bytes");
        List<RecordDiff> losses;
        try
        {
            var file = Walk(File.ReadAllBytes(path), parsed.ModKey);
            if (dropped is not null) foreach (var k in dropped) file.Remove(k);
            losses = Losses(Diff(file, Walk(written, parsed.ModKey)), Renames);
        }
        catch (Exception ex) { return CouldNotRun(fileName, WriteEngine.Describe(ex)); }
        return losses.Count == 0 ? null : Refusal(fileName, losses, parsed, remedy);
    }

    /// <summary>The refusal when the check itself cannot run on a file, so nothing is written unchecked.</summary>
    public static string CouldNotRun(string fileName, string why) =>
        $"refused: houseCARL's round-trip check could not run on '{fileName}' ({why}), so it cannot tell whether rewriting " +
        $"that file would drop subrecords; '{fileName}' is UNTOUCHED — check the file in xEdit, close anything that holds " +
        "it open, and retry.";

    /// <summary>The bytes the write would stage, held in memory; a missing master retries with no load order, which only the header feels.</summary>
    public static byte[]? SerializeToMemory(SkyrimMod parsed, IReadOnlyList<ISkyrimModGetter> masters, string path)
    {
        var ordered = masters as ISkyrimModGetter[] ?? masters.ToArray();
        try { return Capture(parsed, ordered, path); }
        catch (Exception ex) when (Find<Mutagen.Bethesda.Plugins.Exceptions.MissingModException>(ex) is not null)
            { return Capture(parsed, null, path); }
    }

    static byte[]? Capture(SkyrimMod parsed, ISkyrimModGetter[]? ordered, string path)
    {
        var capture = new CaptureFileSystem();
        WriteEngine.SerializeInPlace(parsed, ordered, path, path, capture);
        return capture.Bytes;
    }

    static T? Find<T>(Exception? ex) where T : Exception
    {
        for (; ex is not null; ex = ex.InnerException)
            if (ex is T hit) return hit;
        return null;
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
                   $"would drop its {Sigs(losses[0])} (#961); '{fileName}' is UNTOUCHED — {remedy.One}.";
        var list = string.Join("; ", losses.Take(RecordsNamed).Select(d => $"{Name(d)} ({Sigs(d)})"));
        var more = losses.Count > RecordsNamed ? $"; and {losses.Count - RecordsNamed} more record(s)" : "";
        return $"refused: {who} cannot write {losses.Count} records back as the file holds them, so rewriting " +
               $"'{fileName}' would drop subrecords from each — {list}{more} (#961); '{fileName}' is UNTOUCHED — " +
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

        internal static CaptureUnsupportedException Unsupported(string what) =>
            new($"the in-memory serialize supports only a FileStream create, and the serializer asked for {what}");
    }

    /// <summary>The capture refusing a file-system call the serialize made: a fault of the check, not of the file.</summary>
    sealed class CaptureUnsupportedException(string message) : NotSupportedException(message);

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
