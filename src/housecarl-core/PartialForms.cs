using System.Buffers.Binary;
using System.Text;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlCore;

/// <summary>Partial forms (#1209): a CELL, WRLD or DIAL with record flag 0x4000, xEdit's placeholder override that holds at most an EDID.
/// Mutagen 0.54.4's Skyrim writer has no such flag and writes its default fields into each (Mutagen-Modding/Mutagen#731).</summary>
public static class PartialForms
{
    const uint PartialFlag = 0x4000;
    const uint CompressedFlag = 0x40000;
    const int Header = 24;

    /// <summary>True for a record xEdit's Skyrim definitions call a partial form.</summary>
    public static bool Is(string sig, uint flags) => (flags & PartialFlag) != 0 && Capable(sig);

    static bool Capable(string sig) => sig is "CELL" or "WRLD" or "DIAL";

    /// <summary>Whether a parsed plugin holds a partial form, so a write without one skips the byte pass.</summary>
    public static bool In(ISkyrimModGetter mod) =>
        mod.Cells.EnumerateMajorRecords<ICellGetter>().Any(r => (r.MajorRecordFlagsRaw & PartialFlag) != 0)
        || mod.Worldspaces.Any(r => (r.MajorRecordFlagsRaw & PartialFlag) != 0)
        || mod.DialogTopics.Any(r => (r.MajorRecordFlagsRaw & PartialFlag) != 0);

    /// <summary>Per type, the subrecords Mutagen writes for a fresh record with nothing set: exactly what the writer adds to a partial form.</summary>
    static readonly Lazy<Dictionary<string, HashSet<(string Sig, string Payload)>>> Defaults = new(() =>
    {
        var mod = new SkyrimMod(ModKey.FromFileName("HousecarlPartialDefaults.esp"), SkyrimRelease.SkyrimSE);
        var sub = new CellSubBlock { GroupType = GroupTypeEnum.InteriorCellSubBlock };
        sub.Cells.Add(new Cell(mod.GetNextFormKey(), SkyrimRelease.SkyrimSE));
        mod.Cells.Records.Add(new CellBlock { GroupType = GroupTypeEnum.InteriorCellBlock, SubBlocks = { sub } });
        mod.Worldspaces.Add(new Worldspace(mod.GetNextFormKey(), SkyrimRelease.SkyrimSE));
        mod.DialogTopics.Add(new DialogTopic(mod.GetNextFormKey(), SkyrimRelease.SkyrimSE));
        var all = SubrecordInventory.CaptureWrite(mod);
        var into = new Dictionary<string, HashSet<(string, string)>>();
        Walk(all, Header + I32(all, 4), all.Length, (at, len) =>
            into[Sig(all, at)] = Subrecords(all, at, len).Select(s => (s.Sig, Convert.ToHexString(s.Payload))).ToHashSet());
        return into;
    });

    /// <summary>The written plugin with every partial form cut back to its EDID; anything there beyond Mutagen's defaults is refused. Callers check <see cref="In"/> first.</summary>
    public static byte[] Restore(byte[] all, string fileName)
    {
        var o = new MemoryStream(all.Length);
        o.Write(all, 0, Header + I32(all, 4));
        Copy(all, Header + I32(all, 4), all.Length, o, fileName, top: true);
        return o.ToArray();
    }

    static void Copy(byte[] all, int p, int end, MemoryStream o, string fileName, bool top)
    {
        while (p < end)
        {
            var sig = Sig(all, p);
            if (sig == "GRUP")
            {
                int size = I32(all, p + 4), start = (int)o.Position;
                // A top-level group of any other type cannot hold a partial form, so it goes across whole.
                if (top && !Capable(Sig(all, p + 8))) { o.Write(all, p, size); p += size; continue; }
                o.Write(all, p, Header);
                Copy(all, p + Header, p + size, o, fileName, top: false);
                var grown = o.GetBuffer();
                BinaryPrimitives.WriteInt32LittleEndian(grown.AsSpan(start + 4), (int)o.Position - start);
                p += size;
                continue;
            }
            int len = Header + I32(all, p + 4);
            if (!Is(sig, U32(all, p + 8))) { o.Write(all, p, len); p += len; continue; }
            var body = Kept(all, p, len, sig, fileName);
            var head = all[p..(p + Header)];
            BinaryPrimitives.WriteInt32LittleEndian(head.AsSpan(4), body.Length);
            o.Write(head);
            o.Write(body);
            p += len;
        }
    }

    /// <summary>A partial form's EDID subrecords; every other one must be a Mutagen default (TIFC is the topic's INFO count Mutagen derives).</summary>
    static byte[] Kept(byte[] all, int at, int len, string sig, string fileName)
    {
        var id = $"{sig} {U32(all, at + 12):X8}";
        if ((U32(all, at + 8) & CompressedFlag) != 0) throw new PartialFormException(Refusal(id, "a compressed body", fileName));
        var kept = new MemoryStream();
        foreach (var s in Subrecords(all, at, len))
        {
            if (s.Sig == "EDID") { kept.Write(all, s.At, 6 + s.Payload.Length); continue; }
            if (Defaults.Value[sig].Contains((s.Sig, Convert.ToHexString(s.Payload))) || (sig == "DIAL" && s.Sig == "TIFC")) continue;
            throw new PartialFormException(Refusal(id, s.Sig, fileName));
        }
        return kept.ToArray();
    }

    static string Refusal(string id, string what, string fileName) =>
        $"refused: {id} in '{fileName}' is a partial form (record flag 0x4000, a placeholder that holds at most an EDID) " +
        $"but would be written with {what}, which houseCARL cannot write back as a partial form; nothing was written — " +
        "leave that record out of the change, or in xEdit remove that data or clear its Partial Form flag, then retry.";

    /// <summary>The round-trip check's rule: a partial form the unedited write would add a subrecord to, else null.</summary>
    public static string? AddedTo(IEnumerable<SubrecordInventory.RecordDiff> diffs, string fileName)
    {
        var hit = diffs.FirstOrDefault(d => d.File?.Partial == true && d.Gained.Count > 0);
        if (hit is null) return null;
        return $"refused: rewriting '{fileName}' would add {string.Join(", ", hit.Gained.Select(g => g.Sig))} to " +
               $"{hit.Signature} {FormIdToken.Of(hit.Key)}, a partial form (record flag 0x4000) that may hold only an EDID; " +
               $"'{fileName}' is UNTOUCHED — check that record in xEdit and retry.";
    }

    /// <summary>Each record (never a GRUP) between p and end, by offset and total length.</summary>
    static void Walk(byte[] all, int p, int end, Action<int, int> record)
    {
        while (p < end)
        {
            int size = I32(all, p + 4);
            if (Sig(all, p) == "GRUP") { Walk(all, p + Header, p + size, record); p += size; continue; }
            record(p, Header + size);
            p += Header + size;
        }
    }

    static IEnumerable<(string Sig, int At, byte[] Payload)> Subrecords(byte[] all, int at, int len)
    {
        for (int q = at + Header; q < at + len;)
        {
            int n = BinaryPrimitives.ReadUInt16LittleEndian(all.AsSpan(q + 4));
            yield return (Sig(all, q), q, all[(q + 6)..(q + 6 + n)]);
            q += 6 + n;
        }
    }

    static string Sig(byte[] all, int at) => Encoding.Latin1.GetString(all, at, 4);
    static int I32(byte[] all, int at) => BinaryPrimitives.ReadInt32LittleEndian(all.AsSpan(at));
    static uint U32(byte[] all, int at) => BinaryPrimitives.ReadUInt32LittleEndian(all.AsSpan(at));
}

/// <summary>A partial form the in-place write cannot bring back as it went in; the message is the whole refusal.</summary>
public sealed class PartialFormException(string message) : InvalidOperationException(message);
