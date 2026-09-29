using System.Buffers.Binary;
using System.IO.Abstractions;
using System.Reflection;
using System.Text;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Headers;
using Mutagen.Bethesda.Plugins.Binary.Translations;
using Mutagen.Bethesda.Plugins.Meta;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace HousecarlCore;

/// <summary>The round-trip check (#961): per record, the subrecords (signature and length) a file holds must survive an unedited re-serialize.</summary>
public static class SubrecordInventory
{
    /// <summary>The one (lost, gained) signature pair the ARR round trip rewrites one for one (INPLACE_ROUNDTRIP_MEASURE_2026-09-28).</summary>
    internal static readonly IReadOnlyList<(string Lost, string Gained)> Renames = new[] { ("BODT", "BOD2") };

    /// <summary>The conditions an allowed loss needs, every one set holding: on the record, the lost payload, or its dropped tail.</summary>
    [Flags]
    public enum AllowWhen { FormVersionBelow = 1, RecordDeleted = 2, AllZeroPayload = 4, ZeroTailDropped = 8 }

    /// <summary>A loss the writer makes that carries no information: record type, subrecord, and the condition.</summary>
    public sealed record Allowance(string Record, string Subrecord, AllowWhen When, int FormVersion = 0);

    /// <summary>The measured class of information-free losses, and nothing beyond it (Aaron, 2026-09-29 ~09:10 and ~10:45).</summary>
    internal static readonly IReadOnlyList<Allowance> InformationFree = new[]
    {
        new Allowance("LTEX", "INAM", AllowWhen.FormVersionBelow | AllowWhen.AllZeroPayload, FormVersion: 43),
        new Allowance("REFR", "NAME", AllowWhen.RecordDeleted),
        new Allowance("REFR", "XRMR", AllowWhen.AllZeroPayload),
        new Allowance("RACE", "PHWT", AllowWhen.ZeroTailDropped),
    };

    /// <summary>How many records the refusal names before it falls back to a count.</summary>
    const int RecordsNamed = 5;

    /// <summary>One subrecord shape: signature, length, count, all-zero, and whether the write keeps each such one as its own prefix less a zero tail.</summary>
    public sealed record Sub(string Sig, int Length, int Count, bool Zero, bool ZeroTail = false);

    /// <summary>One record as its bytes hold it: signature, form version, deleted flag, and its subrecords by (signature, length).</summary>
    public sealed class RecordEntry
    {
        public string Signature { get; init; } = "";
        public int FormVersion { get; init; }
        public bool Deleted { get; init; }
        public Dictionary<(string Sig, int Length), (int Count, bool Zero)> Subs { get; } = new();
        public List<(string Sig, int Length)> Order { get; } = new();
        public List<ReadOnlyMemorySlice<byte>> Bodies { get; } = new();

        /// <summary>Count every subrecord of one record body, kept for a later look at the payloads.</summary>
        public void AddBody(ReadOnlyMemorySlice<byte> body)
        {
            Bodies.Add(body);
            foreach (var (sig, data) in Subrecords(body)) Add(sig, data);
        }

        internal void AddRecord(MajorRecordFrame rec) => AddBody(Body(rec));

        void Add(string sig, ReadOnlyMemorySlice<byte> data)
        {
            var key = (sig, data.Length);
            bool zero = !data.Span.ContainsAnyExcept((byte)0);
            if (Subs.TryGetValue(key, out var had)) { Subs[key] = (had.Count + 1, had.Zero && zero); return; }
            Subs[key] = (1, zero);
            Order.Add(key);
        }

        internal int Count((string, int) key) => Subs.TryGetValue(key, out var v) ? v.Count : 0;
    }

    /// <summary>One record's subrecords the round trip lost and gained, before any rename or allowance is paired off.</summary>
    public sealed record RecordDiff(FormKey Key, string Signature, int FormVersion, bool Deleted, IReadOnlyList<Sub> Lost,
                                    IReadOnlyList<Sub> Gained);

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
        ReadOnlyMemorySlice<byte>? written;
        try { written = SerializeToMemory(parsed, masters, path); }
        catch (Exception ex) when (Find<CaptureUnsupportedException>(ex) is { } cu) { return CouldNotRun(fileName, cu.Message); }
        if (written is null) return CouldNotRun(fileName, "the in-memory serialize produced no bytes");
        List<RecordDiff> losses;
        try
        {
            var file = Walk(File.ReadAllBytes(path), parsed.ModKey);
            if (dropped is not null) foreach (var k in dropped) file.Remove(k);
            losses = Losses(Diff(file, Walk(written.Value, parsed.ModKey)), Renames, InformationFree);
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
    public static ReadOnlyMemorySlice<byte>? SerializeToMemory(SkyrimMod parsed, IReadOnlyList<ISkyrimModGetter> masters, string path)
    {
        var ordered = masters as ISkyrimModGetter[] ?? masters.ToArray();
        try { return Capture(parsed, ordered, path); }
        catch (Exception ex) when (Find<Mutagen.Bethesda.Plugins.Exceptions.MissingModException>(ex) is not null)
            { return Capture(parsed, null, path); }
    }

    static ReadOnlyMemorySlice<byte>? Capture(SkyrimMod parsed, ISkyrimModGetter[]? ordered, string path)
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

    /// <summary>Every major record in a plugin's bytes, keyed by FormKey through that file's own master list; compressed records inflated.</summary>
    public static Dictionary<FormKey, RecordEntry> Walk(ReadOnlyMemorySlice<byte> all, ModKey self)
    {
        var meta = GameConstants.SkyrimSE;
        var into = new Dictionary<FormKey, RecordEntry>();
        var header = new MajorRecordFrame(meta, all);
        var masters = new List<ModKey>();
        foreach (var (sig, data) in Subrecords(header.Content))
            if (sig == "MAST") masters.Add(ModKey.FromFileName(Encoding.Latin1.GetString(data.Span).TrimEnd('\0')));
        long pos = header.TotalLength;
        while (pos < all.Length) pos += WalkGroup(meta, all.Slice(checked((int)pos)), masters, self, into);
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
            if (!into.TryGetValue(key, out var entry))
                into[key] = entry = new RecordEntry
                {
                    Signature = rec.RecordType.Type, FormVersion = rec.FormVersion ?? 0,
                    Deleted = (rec.MajorRecordFlags & DeletedFlag) != 0,
                };
            entry.AddRecord(rec);
            p += rec.TotalLength;
        }
        return end;
    }

    /// <summary>A record's subrecords in order through Mutagen's framing: signature and payload, an XXXX carrier folded in.</summary>
    static IEnumerable<(string Sig, ReadOnlyMemorySlice<byte> Data)> Subrecords(ReadOnlyMemorySlice<byte> body) =>
        RecordSpanExtensions.EnumerateSubrecords(body, GameConstants.SkyrimSE, 0).Select(f => (f.RecordType.Type, f.Content));

    /// <summary>A record's body, decompressed through Mutagen when the record is compressed.</summary>
    static ReadOnlyMemorySlice<byte> Body(MajorRecordFrame rec) => rec.IsCompressed ? rec.Decompress(out _).Content : rec.Content;

    static string Sig(ReadOnlySpan<byte> at) => Encoding.Latin1.GetString(at.Slice(0, 4));

    const int DeletedFlag = 0x20;

    /// <summary>Per record of the FILE, what the written side lacks and what it added, by (signature, length).</summary>
    public static List<RecordDiff> Diff(Dictionary<FormKey, RecordEntry> file, Dictionary<FormKey, RecordEntry> written)
    {
        var diffs = new List<RecordDiff>();
        foreach (var (key, f) in file)
        {
            written.TryGetValue(key, out var w);
            var lost = Minus(f, w);
            var gained = w is null ? new List<Sub>() : Minus(w, f);
            if (lost.Count > 0 || gained.Count > 0)
                diffs.Add(new RecordDiff(key, f.Signature, f.FormVersion, f.Deleted, lost, gained));
        }
        return diffs;
    }

    static List<Sub> Minus(RecordEntry a, RecordEntry? b) =>
        a.Order.Select(k => new Sub(k.Sig, k.Length, a.Subs[k].Count - (b?.Count(k) ?? 0), a.Subs[k].Zero, ZeroTailOnly(a, b, k.Sig)))
               .Where(x => x.Count > 0).ToList();

    static List<ReadOnlyMemorySlice<byte>> Payloads(RecordEntry e, string sig) =>
        e.Bodies.SelectMany(b => Subrecords(b)).Where(x => x.Sig == sig).Select(x => x.Data).ToList();

    /// <summary>True when every occurrence of the signature is written back, in order, as its own leading bytes with only a zero tail cut.</summary>
    static bool ZeroTailOnly(RecordEntry file, RecordEntry? written, string sig)
    {
        if (written is null) return false;
        var f = Payloads(file, sig);
        var w = Payloads(written, sig);
        if (f.Count != w.Count) return false;
        for (int i = 0; i < f.Count; i++)
        {
            if (f[i].Length == w[i].Length) continue;
            if (w[i].Length > f[i].Length || !f[i].Span[..w[i].Length].SequenceEqual(w[i].Span)
                || f[i].Span[w[i].Length..].ContainsAnyExcept((byte)0)) return false;
        }
        return true;
    }

    /// <summary>The diffs that still lose a subrecord once each rename is paired with its gain and each allowed loss is set aside.</summary>
    public static List<RecordDiff> Losses(List<RecordDiff> diffs, IReadOnlyList<(string Lost, string Gained)> renames,
                                          IReadOnlyList<Allowance> allowed)
    {
        var losses = new List<RecordDiff>();
        foreach (var d in diffs)
        {
            var lost = d.Lost.ToList();
            foreach (var (l, g) in renames)
            {
                int pair = Math.Min(lost.Where(x => x.Sig == l).Sum(x => x.Count), d.Gained.Where(x => x.Sig == g).Sum(x => x.Count));
                for (int i = 0; i < lost.Count && pair > 0; i++)
                {
                    if (lost[i].Sig != l) continue;
                    int take = Math.Min(pair, lost[i].Count);
                    lost[i] = lost[i] with { Count = lost[i].Count - take };
                    pair -= take;
                }
            }
            var left = lost.Where(x => x.Count > 0 && !allowed.Any(a => Allows(a, d, x))).ToList();
            if (left.Count > 0) losses.Add(d with { Lost = left });
        }
        return losses;
    }

    static bool Allows(Allowance a, RecordDiff d, Sub s) =>
        a.Record == d.Signature && a.Subrecord == s.Sig && a.When != 0
        && (!a.When.HasFlag(AllowWhen.FormVersionBelow) || d.FormVersion < a.FormVersion)
        && (!a.When.HasFlag(AllowWhen.RecordDeleted) || d.Deleted)
        && (!a.When.HasFlag(AllowWhen.AllZeroPayload) || s.Zero)
        && (!a.When.HasFlag(AllowWhen.ZeroTailDropped) || s.ZeroTail);

    /// <summary>The one refusal: which records, which subrecords, why, and what to do instead.</summary>
    static string Refusal(string fileName, IReadOnlyList<RecordDiff> losses, SkyrimMod parsed, Remedy remedy)
    {
        var named = losses.Take(RecordsNamed).Select(d => d.Key).ToHashSet();
        var types = new Dictionary<FormKey, string>();
        foreach (var r in parsed.EnumerateMajorRecords())
            if (named.Contains(r.FormKey)) types.TryAdd(r.FormKey, RecordNaming.StripOverlay(r.GetType().Name));
        string Name(RecordDiff d) => (types.TryGetValue(d.Key, out var t) ? t : d.Signature) + " " + FormIdToken.Of(d.Key);
        string Sigs(RecordDiff d) => string.Join(", ", d.Lost.Select(x => Shape(d, x)));
        var who = $"houseCARL (Mutagen {MutagenVersion})";
        if (losses.Count == 1)
            return $"refused: {who} cannot write {Name(losses[0])} back as the file holds it, so rewriting '{fileName}' " +
                   $"would drop or resize its {Sigs(losses[0])} (#961); '{fileName}' is UNTOUCHED — {remedy.One}.";
        var list = string.Join("; ", losses.Take(RecordsNamed).Select(d => $"{Name(d)} ({Sigs(d)})"));
        var more = losses.Count > RecordsNamed ? $"; and {losses.Count - RecordsNamed} more record(s)" : "";
        return $"refused: {who} cannot write {losses.Count} records back as the file holds them, so rewriting " +
               $"'{fileName}' would drop or resize subrecords in each — {list}{more} (#961); '{fileName}' is UNTOUCHED — " +
               $"{remedy.Many}.";
    }

    /// <summary>A lost subrecord as the refusal names it: the signature, its count, and its length when the write keeps it at another.</summary>
    static string Shape(RecordDiff d, Sub x)
    {
        var name = x.Count > 1 ? $"{x.Sig} x{x.Count}" : x.Sig;
        var other = d.Gained.Where(g => g.Sig == x.Sig).Select(g => g.Length).Distinct().ToList();
        return other.Count == 0 ? name : $"{name} at {x.Length} bytes (written at {string.Join("/", other)})";
    }

    /// <summary>The Mutagen release the parser is, off the assembly houseCARL loaded rather than a copy of the pin.</summary>
    static readonly string MutagenVersion =
        typeof(SkyrimMod).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(SkyrimMod).Assembly.GetName().Version?.ToString(3) ?? "unknown";

    /// <summary>A file system that keeps the serialize's one create in memory and refuses every other call.</summary>
    sealed class CaptureFileSystem : IFileSystem
    {
        readonly CaptureStreams _streams;
        public CaptureFileSystem() { _streams = new CaptureStreams(this); }
        public ReadOnlyMemorySlice<byte>? Bytes =>
            _streams.Captured?.TryGetBuffer(out var seg) == true ? new ReadOnlyMemorySlice<byte>(seg.Array!, seg.Offset, seg.Count) : null;

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
