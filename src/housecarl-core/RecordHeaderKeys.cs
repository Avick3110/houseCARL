using System.Text;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Headers;
using Mutagen.Bethesda.Plugins.Binary.Translations;
using Mutagen.Bethesda.Plugins.Meta;
using Noggog;

namespace HousecarlCore;

/// <summary>The FormKeys a plugin defines or overrides, read off its record headers alone: no subrecord is parsed, so a record Mutagen cannot read still names its key.</summary>
public static class RecordHeaderKeys
{
    /// <summary>Every major record's FormKey in the file at <paramref name="path"/>, mapped through the file's own master list; throws when the framing itself is broken.</summary>
    public static List<FormKey> Read(string path)
    {
        var meta = GameConstants.SkyrimSE;
        ReadOnlyMemorySlice<byte> all = File.ReadAllBytes(path);
        var self = ModKey.FromFileName(Path.GetFileName(path));
        var header = new MajorRecordFrame(meta, all);
        var masters = Masters(header, meta);
        var keys = new List<FormKey>();
        for (long pos = header.TotalLength; pos < all.Length;)
            pos += Group(meta, all.Slice(checked((int)pos)), masters, self, keys);
        return keys;
    }

    /// <summary>The place of each of <paramref name="wanted"/> among the major records of the file at <paramref name="path"/>,
    /// counted in file order through every group, read header by header with each record's body skipped; a key the file
    /// does not hold is absent. Stops once every key is placed.</summary>
    public static Dictionary<FormKey, int> Places(string path, IReadOnlySet<FormKey> wanted)
    {
        var meta = GameConstants.SkyrimSE;
        var places = new Dictionary<FormKey, int>();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        int len = meta.MajorConstants.HeaderLength;
        var head = new byte[len];
        if (fs.Read(head, 0, len) < len) return places;
        var tes4 = new byte[len + BitConverter.ToUInt32(head, 4)];
        head.CopyTo(tes4, 0);
        fs.ReadExactly(tes4, len, tes4.Length - len);
        var self = ModKey.FromFileName(Path.GetFileName(path));
        var masters = Masters(new MajorRecordFrame(meta, tes4), meta);
        int place = 0;
        // A group's header is followed by its contents, so the walk steps into it; a record's body is skipped.
        while (places.Count < wanted.Count && fs.Read(head, 0, len) == len)
        {
            if (head.AsSpan(0, 4).SequenceEqual("GRUP"u8)) continue;
            uint raw = BitConverter.ToUInt32(head, 12);
            int index = (int)(raw >> 24);
            var fk = new FormKey(index < masters.Count ? masters[index] : self, raw & 0xFFFFFF);
            if (wanted.Contains(fk)) places.TryAdd(fk, place);
            place++;
            fs.Seek(BitConverter.ToUInt32(head, 4), SeekOrigin.Current);
        }
        return places;
    }

    /// <summary>The master list a plugin's header declares, in order, as its FormIDs' load indexes number them.</summary>
    static List<ModKey> Masters(MajorRecordFrame header, GameConstants meta)
    {
        var masters = new List<ModKey>();
        foreach (var sub in RecordSpanExtensions.EnumerateSubrecords(header.Content, meta, 0))
            if (sub.RecordType.Type == "MAST")
                masters.Add(ModKey.FromFileName(Encoding.Latin1.GetString(sub.Content.Span).TrimEnd('\0')));
        return masters;
    }

    static long Group(GameConstants meta, ReadOnlyMemorySlice<byte> at, List<ModKey> masters, ModKey self, List<FormKey> keys)
    {
        var group = new GroupFrame(meta, at);
        for (long p = group.HeaderLength; p < group.TotalLength;)
        {
            var here = at.Slice(checked((int)p));
            if (here.Span.Slice(0, 4).SequenceEqual("GRUP"u8)) { p += Group(meta, here, masters, self, keys); continue; }
            var rec = new MajorRecordFrame(meta, here);
            uint raw = rec.FormID.Raw;
            int index = (int)(raw >> 24);
            keys.Add(new FormKey(index < masters.Count ? masters[index] : self, raw & 0xFFFFFF));
            p += rec.TotalLength;
        }
        return group.TotalLength;
    }
}
