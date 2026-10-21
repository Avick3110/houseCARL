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
        var masters = new List<ModKey>();
        foreach (var sub in RecordSpanExtensions.EnumerateSubrecords(header.Content, meta, 0))
            if (sub.RecordType.Type == "MAST")
                masters.Add(ModKey.FromFileName(Encoding.Latin1.GetString(sub.Content.Span).TrimEnd('\0')));
        var keys = new List<FormKey>();
        for (long pos = header.TotalLength; pos < all.Length;)
            pos += Group(meta, all.Slice(checked((int)pos)), masters, self, keys);
        return keys;
    }

    static long Group(GameConstants meta, ReadOnlyMemorySlice<byte> at, List<ModKey> masters, ModKey self, List<FormKey> keys)
    {
        var group = new GroupFrame(meta, at);
        for (long p = group.HeaderLength; p < group.TotalLength;)
        {
            var here = at.Slice(checked((int)p));
            if (Encoding.Latin1.GetString(here.Span.Slice(0, 4)) == "GRUP") { p += Group(meta, here, masters, self, keys); continue; }
            var rec = new MajorRecordFrame(meta, here);
            uint raw = rec.FormID.Raw;
            int index = (int)(raw >> 24);
            keys.Add(new FormKey(index < masters.Count ? masters[index] : self, raw & 0xFFFFFF));
            p += rec.TotalLength;
        }
        return group.TotalLength;
    }
}
