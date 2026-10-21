using System.Buffers.Binary;
using System.Text;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Headers;

namespace HousecarlCore;

/// <summary>Partial forms (#1209): a CELL, WRLD or DIAL with record flag 0x4000, xEdit's placeholder override that holds at most an EDID.
/// Mutagen 0.54.4's Skyrim writer has no such flag and writes its default fields into each (Mutagen-Modding/Mutagen#731).</summary>
public static class PartialForms
{
    const uint PartialFlag = 0x4000;
    const uint CompressedFlag = 0x40000;
    const int Header = 24;

    /// <summary>True for a record xEdit's Skyrim definitions call a partial form.</summary>
    public static bool Is(MajorRecordFrame rec) =>
        ((uint)rec.MajorRecordFlags & PartialFlag) != 0 && rec.RecordType.Type is "CELL" or "WRLD" or "DIAL";

    /// <summary>Puts each partial form of the staged write back byte for byte as its source held it, whatever it holds; one the call edits refuses.</summary>
    public static void WriteBack(string staged, IReadOnlyList<string> sources, IReadOnlySet<FormKey> edited)
    {
        var all = File.ReadAllBytes(staged);
        var fileName = Path.GetFileName(staged);
        var partial = SubrecordInventory.Records(all, ModKey.FromFileName(fileName))
            .Where(r => Is(r.Rec)).Select(r => (r.Key, r.At, Sig: r.Rec.RecordType.Type)).ToList();
        if (partial.Count == 0) return;
        var wanted = partial.Select(r => r.Key).ToHashSet();
        // A later source wins, as a later donor does in a merge.
        var source = new Dictionary<FormKey, (byte[] Bytes, string? Edid)>();
        foreach (var path in sources.Where(File.Exists))
        {
            var bytes = File.ReadAllBytes(path);
            foreach (var (key, at, rec) in SubrecordInventory.Records(bytes, ModKey.FromFileName(Path.GetFileName(path))))
            {
                if (!wanted.Contains(key) || !Is(rec)) continue;
                var edid = SubrecordInventory.Contents(rec).Where(s => s.Sig == "EDID")
                    .Select(s => Encoding.Latin1.GetString(s.Data.Span).TrimEnd('\0')).FirstOrDefault();
                source[key] = (bytes[at..(at + checked((int)rec.TotalLength))], edid);
            }
        }
        var put = new Dictionary<int, byte[]>();
        foreach (var (key, at, sig) in partial)
        {
            if (!source.TryGetValue(key, out var was))
                throw new PartialFormException(Refusal(sig, null, key, fileName,
                    "but houseCARL cannot find it in the plugin it came from to write it back unchanged",
                    "clear its Partial Form flag in xEdit, then retry"));
            if (edited.Contains(key))
                throw new PartialFormException(Refusal(sig, was.Edid, key, fileName,
                    "and this call edits it, which houseCARL cannot write as a partial form",
                    "leave it out of the change, or clear its Partial Form flag in xEdit first, then retry"));
            var back = (byte[])was.Bytes.Clone();
            Buffer.BlockCopy(all, at + 12, back, 12, 4);   // the FormID as this write spells it through its own masters
            // A DIAL's TIFC is its INFO count, which the write's own count replaces.
            if (Tifc(back, 0) is { } had && Tifc(all, at) is { } now) Buffer.BlockCopy(all, now, back, had, 4);
            put[at] = back;
        }
        int head = Header + I32(all, 4);
        var o = new MemoryStream(all.Length);
        o.Write(all, 0, head);
        Copy(all, head, all.Length, o, put);
        using var file = File.Create(staged);
        file.Write(o.GetBuffer(), 0, (int)o.Length);
    }

    /// <summary>The records between p and end, each put-back one in place of its written bytes, every group size recomputed.</summary>
    static void Copy(byte[] all, int p, int end, MemoryStream o, Dictionary<int, byte[]> put)
    {
        while (p < end)
        {
            int size = I32(all, p + 4);
            if (all.AsSpan(p, 4).SequenceEqual("GRUP"u8))
            {
                int start = (int)o.Position;
                o.Write(all, p, Header);
                Copy(all, p + Header, p + size, o, put);
                BinaryPrimitives.WriteInt32LittleEndian(o.GetBuffer().AsSpan(start + 4), (int)o.Position - start);
                p += size;
                continue;
            }
            if (put.TryGetValue(p, out var back)) o.Write(back); else o.Write(all, p, Header + size);
            p += Header + size;
        }
    }

    /// <summary>Where the 4-byte TIFC payload of the uncompressed record at <paramref name="at"/> sits, if it has one.</summary>
    static int? Tifc(byte[] all, int at)
    {
        if ((BinaryPrimitives.ReadUInt32LittleEndian(all.AsSpan(at + 8)) & CompressedFlag) != 0) return null;
        for (int q = at + Header, end = at + Header + I32(all, at + 4); q < end; q += 6 + BinaryPrimitives.ReadUInt16LittleEndian(all.AsSpan(q + 4)))
            if (all.AsSpan(q, 4).SequenceEqual("TIFC"u8) && BinaryPrimitives.ReadUInt16LittleEndian(all.AsSpan(q + 4)) == 4) return q + 6;
        return null;
    }

    static string Refusal(string sig, string? edid, FormKey key, string fileName, string why, string remedy) =>
        $"refused: {sig} {(edid is null ? "" : $"'{edid}' ")}{FormIdToken.Of(key)} in '{fileName}' is a partial form " +
        $"(record flag 0x4000, a placeholder override) {why}; nothing was written — {remedy}.";

    static int I32(byte[] all, int at) => BinaryPrimitives.ReadInt32LittleEndian(all.AsSpan(at));
}

/// <summary>A partial form the in-place write cannot bring back as it went in; the message is the whole refusal.</summary>
public sealed class PartialFormException(string message) : InvalidOperationException(message);
