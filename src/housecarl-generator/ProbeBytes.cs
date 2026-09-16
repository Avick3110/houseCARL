namespace HousecarlGenerator;

/// <summary>
/// On-disk byte surgery for probe FIXTURES — the small set of edits that produce record shapes Mutagen's writer
/// cannot author, shared so the guards that need them can't drift apart on the technique (#279).
///
/// Both of these exist for the same reason: a probe that needs "a record Mutagen chokes on" or "a deleted record that
/// still carries a body" cannot get there through the write API. Mutagen writes only well-formed records, and it
/// serialises a model-deleted record with an EMPTY body — which is exactly the clean case, not the wild one. So the
/// fixture is written normally and then patched on disk.
/// </summary>
public static class ProbeBytes
{
    /// <summary>Set the record-header Deleted flag (0x20) on the record of signature <paramref name="sig"/> whose
    /// on-disk FormID equals <paramref name="rawFormId"/>, by locating its 24-byte header (sig at +0, flags word at
    /// +8, FormID at +12) and OR-ing the flags' low byte. Returns how many headers matched — the caller asserts the
    /// expected count, so a fixture whose layout assumption is wrong fails LOUD at setup rather than passing vacuously.
    ///
    /// <para><paramref name="rawFormId"/> is the ON-DISK dword, NOT <c>FormKey.ID</c>: the high byte is the record's
    /// index into its plugin's declared master list, so a plugin's OWN record is <c>(masterCount &lt;&lt; 24) | id</c>
    /// (masterCount 0 for a master-less plugin) and an OVERRIDE carries the index of the master it overrides. Passing
    /// the object ID alone works only in the master-less case. The FormID match also skips the top-GRUP label of the
    /// same 4 chars, which carries the signature but not a matching FormID.</para></summary>
    public static int SetDeletedFlag(string espPath, string sig, uint rawFormId)
    {
        var bytes = File.ReadAllBytes(espPath);
        var s = System.Text.Encoding.ASCII.GetBytes(sig);
        int hits = 0;
        for (int i = 0; i + 24 <= bytes.Length; i++)
        {
            if (bytes[i] != s[0] || bytes[i + 1] != s[1] || bytes[i + 2] != s[2] || bytes[i + 3] != s[3]) continue;
            uint formId = (uint)(bytes[i + 12] | (bytes[i + 13] << 8) | (bytes[i + 14] << 16) | (bytes[i + 15] << 24));
            if (formId != rawFormId) continue;
            bytes[i + 8] |= 0x20;   // SkyrimMajorRecordFlag.Deleted, the flags word's low byte
            hits++;
        }
        if (hits > 0) File.WriteAllBytes(espPath, bytes);
        return hits;
    }

    /// <summary>Corrupt every EPFT subrecord's parameter-type flag byte in the written plugin (sig + len(2) + 1-byte
    /// payload → payload at +6), returning how many were hit. This is the suite's canonical "a body whose LAZY parse
    /// throws" fixture: a perk with one entry-point effect, its EPFT byte set to a value that is not a legal parameter
    /// type, so <c>ParseEffect</c> throws the moment anything reaches for the perk's Effects. Callers write exactly one
    /// entry-point effect and assert exactly one hit.</summary>
    /// <summary>Rewrite every entry-point effect in the plugin to the INTERNALLY INCONSISTENT encoding #301 found in
    /// the wild: EPFT set to 1 (Float) while the effect's DATA function byte still names an actor-value arm, and the
    /// 8-byte EPFD cut to the 4 bytes EPFT 1 declares. Both subrecords stay well-formed, so the file is one xEdit
    /// renders and Mutagen refuses — unlike <see cref="CorruptEpftBytes"/>, which writes a parameter type nothing can
    /// decode. The record's content length, its GRUP's length and the file all shrink with the cut. Returns how many
    /// EPFD subrecords were cut; callers write exactly one entry-point effect and assert exactly one.</summary>
    public static int MakeEpftFunctionMismatch(string espPath)
    {
        var bytes = File.ReadAllBytes(espPath);
        int hits = 0;
        for (int i = 0; i + 6 < bytes.Length; i++)
        {
            if (bytes[i] != (byte)'E' || bytes[i + 1] != (byte)'P' || bytes[i + 2] != (byte)'F' || bytes[i + 3] != (byte)'T') continue;
            int epftLen = BitConverter.ToUInt16(bytes, i + 4);
            int epfd = i + 6 + epftLen;
            if (epfd + 6 > bytes.Length) continue;
            if (bytes[epfd] != (byte)'E' || bytes[epfd + 1] != (byte)'P' || bytes[epfd + 2] != (byte)'F' || bytes[epfd + 3] != (byte)'D') continue;
            int epfdLen = BitConverter.ToUInt16(bytes, epfd + 4);
            if (epfdLen != 8) continue;                       // only the two-dword arms carry the disagreement
            bytes[i + 6] = 1;                                 // EPFT = Float
            BitConverter.GetBytes((ushort)4).CopyTo(bytes, epfd + 4);
            Shrink(ref bytes, epfd + 6, 4);                   // drop the leading actor-value dword, leaving the float EPFT 1 declares
            hits++;
            i = epfd;                                         // the cut moved everything after it
        }
        if (hits > 0) File.WriteAllBytes(espPath, bytes);
        return hits;
    }

    /// <summary>Drop <paramref name="count"/> bytes at <paramref name="at"/> and shrink every record and GRUP header
    /// that CONTAINS that offset, so the file stays internally consistent. A major record's length word is at +4 of
    /// its 24-byte header and covers its content; a GRUP's is at +4 of its own 24-byte header and covers the header
    /// too. Both are found by walking the file from the mod header, the only walk that cannot mistake record data
    /// for a header.</summary>
    static void Shrink(ref byte[] bytes, int at, int count)
    {
        foreach (int header in ContainingHeaders(bytes, at))
        {
            uint len = BitConverter.ToUInt32(bytes, header + 4);
            BitConverter.GetBytes(len - (uint)count).CopyTo(bytes, header + 4);
        }
        bytes = bytes[..at].Concat(bytes[(at + count)..]).ToArray();
    }

    /// <summary>The offsets of every GRUP and major-record header whose content spans <paramref name="at"/>, walking
    /// the file structurally from the mod header.</summary>
    static List<int> ContainingHeaders(byte[] bytes, int at)
    {
        var hits = new List<int>();
        void Walk(int pos, int end)
        {
            while (pos + 24 <= end)
            {
                bool grup = bytes[pos] == 'G' && bytes[pos + 1] == 'R' && bytes[pos + 2] == 'U' && bytes[pos + 3] == 'P';
                uint len = BitConverter.ToUInt32(bytes, pos + 4);
                int total = grup ? (int)len : 24 + (int)len;
                int contentStart = pos + 24;
                int contentEnd = pos + total;
                bool contains = at >= contentStart && at <= contentEnd;
                if (contains) hits.Add(pos);
                if (grup && contains) Walk(contentStart, contentEnd);
                pos += total;
            }
        }
        // The mod header (TES4) is a plain record; the groups follow it.
        uint headerLen = BitConverter.ToUInt32(bytes, 4);
        Walk(24 + (int)headerLen, bytes.Length);
        return hits;
    }

    public static int CorruptEpftBytes(string espPath)
    {
        var bytes = File.ReadAllBytes(espPath);
        int hits = 0;
        for (int i = 0; i + 6 < bytes.Length; i++)
        {
            if (bytes[i] != (byte)'E' || bytes[i + 1] != (byte)'P' || bytes[i + 2] != (byte)'F' || bytes[i + 3] != (byte)'T') continue;
            bytes[i + 6] = 0x63;   // not a legal parameter-type flag → ParseEffect throws on lazy Effects parse
            hits++;
        }
        if (hits > 0) File.WriteAllBytes(espPath, bytes);
        return hits;
    }
}
