using System.Buffers.Binary;
using System.Text;
using Mutagen.Bethesda.Plugins;

namespace HousecarlCore;

/// <summary>Partial forms (#1209): a CELL, WRLD or DIAL with record flag 0x4000, xEdit's placeholder override that holds at most an EDID.
/// Mutagen 0.54.4's Skyrim writer has no such flag and writes its default fields into each (Mutagen-Modding/Mutagen#731).</summary>
public static class PartialForms
{
    const uint PartialFlag = 0x4000;
    const int Header = 24;

    /// <summary>True for a record xEdit's Skyrim definitions call a partial form.</summary>
    public static bool Is(string sig, uint flags) => (flags & PartialFlag) != 0 && sig is "CELL" or "WRLD" or "DIAL";

    /// <summary>Puts each partial form of the staged write back byte for byte as its source held it; one the call edits, or whose source holds data, refuses.</summary>
    public static void WriteBack(string staged, IReadOnlyList<string> sources, IReadOnlySet<FormKey> edited)
    {
        var all = File.ReadAllBytes(staged);
        var fileName = Path.GetFileName(staged);
        var partial = SubrecordInventory.Records(all, ModKey.FromFileName(fileName))
            .Where(r => Is(r.Rec.RecordType.Type, (uint)r.Rec.MajorRecordFlags)).Select(r => (r.Key, r.At)).ToList();
        if (partial.Count == 0) return;
        // A later source wins, as a later donor does in a merge.
        var source = new Dictionary<FormKey, (byte[] Bytes, string? Edid, List<string> Data)>();
        foreach (var path in sources.Where(File.Exists))
        {
            var bytes = File.ReadAllBytes(path);
            foreach (var (key, at, rec) in SubrecordInventory.Records(bytes, ModKey.FromFileName(Path.GetFileName(path))))
            {
                if (!Is(rec.RecordType.Type, (uint)rec.MajorRecordFlags)) continue;
                var subs = SubrecordInventory.Contents(rec).ToList();
                var edid = subs.Where(s => s.Sig == "EDID").Select(s => Encoding.Latin1.GetString(s.Data.Span).TrimEnd('\0')).FirstOrDefault();
                // TIFC is a topic's INFO count, which the CK writes on a partial DIAL too.
                var data = subs.Select(s => s.Sig).Where(s => s != "EDID" && !(s == "TIFC" && rec.RecordType.Type == "DIAL")).Distinct().ToList();
                source[key] = (bytes[at..(at + checked((int)rec.TotalLength))], edid, data);
            }
        }
        var put = new Dictionary<int, byte[]>();
        foreach (var (key, at) in partial)
        {
            var sig = Encoding.Latin1.GetString(all, at, 4);
            if (!source.TryGetValue(key, out var was))
                throw new PartialFormException(Refusal(sig, null, key, fileName,
                    "but houseCARL cannot find it in the plugin it came from to write it back unchanged",
                    "clear its Partial Form flag in xEdit, then retry"));
            if (edited.Contains(key))
                throw new PartialFormException(Refusal(sig, was.Edid, key, fileName,
                    "and this call edits it, which houseCARL cannot write as a partial form",
                    "leave it out of the change, or clear its Partial Form flag in xEdit first, then retry"));
            if (was.Data.Count > 0)
                throw new PartialFormException(Refusal(sig, was.Edid, key, fileName,
                    $"but its file already holds {string.Join(", ", was.Data)} on it, which houseCARL cannot write back as a partial form",
                    "in xEdit remove that data or clear its Partial Form flag, then retry"));
            var back = (byte[])was.Bytes.Clone();
            Buffer.BlockCopy(all, at + 12, back, 12, 4);   // the FormID as this write spells it through its own masters
            put[at] = back;
        }
        var o = new MemoryStream(all.Length);
        o.Write(all, 0, Header + I32(all, 4));
        Copy(all, Header + I32(all, 4), all.Length, o, put);
        File.WriteAllBytes(staged, o.ToArray());
    }

    /// <summary>The records between p and end, each put-back one in place of its written bytes, every group size recomputed.</summary>
    static void Copy(byte[] all, int p, int end, MemoryStream o, Dictionary<int, byte[]> put)
    {
        while (p < end)
        {
            int size = I32(all, p + 4);
            if (Encoding.Latin1.GetString(all, p, 4) == "GRUP")
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

    static string Refusal(string sig, string? edid, FormKey key, string fileName, string why, string remedy) =>
        $"refused: {sig} {(edid is null ? "" : $"'{edid}' ")}{FormIdToken.Of(key)} in '{fileName}' is a partial form " +
        $"(record flag 0x4000, a placeholder that holds at most an EDID) {why}; nothing was written — {remedy}.";

    static int I32(byte[] all, int at) => BinaryPrimitives.ReadInt32LittleEndian(all.AsSpan(at));
}

/// <summary>A partial form the in-place write cannot bring back as it went in; the message is the whole refusal.</summary>
public sealed class PartialFormException(string message) : InvalidOperationException(message);
