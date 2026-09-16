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

    /// <summary>Rewrite ONE record's entry-point effect to the INTERNALLY INCONSISTENT encoding #301 found in the
    /// wild: EPFT set to 1 (Float) while the effect's DATA function byte still names an actor-value arm, and the
    /// 8-byte EPFD cut to the 4 bytes EPFT 1 declares — the leading actor-value dword is dropped, so what is left is
    /// the float. Both subrecords stay well-formed, so the file is one xEdit renders and Mutagen refuses — unlike
    /// <see cref="CorruptEpftBytes"/>, which writes a parameter type nothing can decode.</summary>
    public static int MakeEpftFunctionMismatch(string espPath, uint rawFormId) =>
        PatchEffectParameter(espPath, rawFormId, epft: 1, dropLeadingDword: true, zeroKept: false);

    /// <summary>Rewrite ONE record's entry-point effect to declare a FormID parameter that is all zeroes: EPFT set to
    /// 4 (a SPEL link, which Mutagen itself writes as a NULLABLE link) and the 8-byte EPFD cut to four zero bytes.
    /// Mutagen still refuses the effect — the function byte names another arm — so the lenient decode reads it, and
    /// an all-zero reference FormID must come back as a declared-but-null link rather than record 000000 of the
    /// plugin's first master.</summary>
    public static int MakeEpftNullFormIdParameter(string espPath, uint rawFormId) =>
        PatchEffectParameter(espPath, rawFormId, epft: 4, dropLeadingDword: false, zeroKept: true);

    /// <summary>Rewrite ONE record's entry-point effect to declare EPFT 7 (a LOCALIZED string) over four bytes. In a
    /// plugin the mod header flags localized — see <see cref="SetLocalizedFlag"/> — those four bytes are a
    /// strings-table key, not characters, so a reader that prints them as text hands back mojibake.</summary>
    public static int MakeEpftLocalizedTextParameter(string espPath, uint rawFormId) =>
        PatchEffectParameter(espPath, rawFormId, epft: 7, dropLeadingDword: true, zeroKept: false);

    /// <summary>Set the mod header's Localized flag (0x80) so Mutagen reads the plugin's translated strings through a
    /// strings table rather than inline. Returns true when the flag was not already set.</summary>
    public static bool SetLocalizedFlag(string espPath)
    {
        var bytes = File.ReadAllBytes(espPath);
        if ((bytes[8] & 0x80) != 0) return false;
        bytes[8] |= 0x80;
        File.WriteAllBytes(espPath, bytes);
        return true;
    }

    /// <summary>Cut ONE of a record's CTDA subrecords short, so reading that condition runs off the end of its own
    /// payload and throws while everything else in the record still reads. <paramref name="skip"/> leaves the first N
    /// CTDAs alone, which is how a condition INSIDE an effect is passed over. The record, its GRUP and the file all
    /// shrink with the cut. Returns how many were cut.</summary>
    public static int TruncateCondition(string espPath, uint rawFormId, int skip = 0)
    {
        const int Keep = 8;
        var bytes = File.ReadAllBytes(espPath);
        int rec = FindRecord(bytes, "PERK", rawFormId);
        if (rec < 0) return 0;
        int end = rec + 24 + (int)BitConverter.ToUInt32(bytes, rec + 4);
        int seen = 0;
        for (int i = rec + 24; i + 6 < end; i++)
        {
            if (bytes[i] != (byte)'C' || bytes[i + 1] != (byte)'T' || bytes[i + 2] != (byte)'D' || bytes[i + 3] != (byte)'A') continue;
            if (seen++ < skip) continue;
            int len = BitConverter.ToUInt16(bytes, i + 4);
            if (len <= Keep) return 0;
            BitConverter.GetBytes((ushort)Keep).CopyTo(bytes, i + 4);
            Shrink(ref bytes, i + 6 + Keep, len - Keep);
            File.WriteAllBytes(espPath, bytes);
            return 1;
        }
        return 0;
    }

    /// <summary>The shared surgery behind the two fixtures above: inside the PERK whose on-disk FormID is
    /// <paramref name="rawFormId"/>, set the EPFT payload and cut its 8-byte EPFD to 4. The record's content length,
    /// its GRUP's length and the file all shrink with the cut. Returns how many EPFD subrecords were cut; callers
    /// write exactly one entry-point effect in that record and assert exactly one.</summary>
    static int PatchEffectParameter(string espPath, uint rawFormId, byte epft, bool dropLeadingDword, bool zeroKept)
    {
        var bytes = File.ReadAllBytes(espPath);
        int hits = 0;
        int rec = FindRecord(bytes, "PERK", rawFormId);
        if (rec < 0) return 0;
        int end = rec + 24 + (int)BitConverter.ToUInt32(bytes, rec + 4);
        for (int i = rec + 24; i + 6 < end; i++)
        {
            if (bytes[i] != (byte)'E' || bytes[i + 1] != (byte)'P' || bytes[i + 2] != (byte)'F' || bytes[i + 3] != (byte)'T') continue;
            int epftLen = BitConverter.ToUInt16(bytes, i + 4);
            int epfd = i + 6 + epftLen;
            if (epfd + 6 > bytes.Length) continue;
            if (bytes[epfd] != (byte)'E' || bytes[epfd + 1] != (byte)'P' || bytes[epfd + 2] != (byte)'F' || bytes[epfd + 3] != (byte)'D') continue;
            int epfdLen = BitConverter.ToUInt16(bytes, epfd + 4);
            if (epfdLen != 8) continue;                       // only the two-dword arms carry the disagreement
            bytes[i + 6] = epft;
            BitConverter.GetBytes((ushort)4).CopyTo(bytes, epfd + 4);
            int cutAt = dropLeadingDword ? epfd + 6 : epfd + 6 + 4;
            Shrink(ref bytes, cutAt, 4);
            if (zeroKept) Array.Clear(bytes, epfd + 6, 4);
            hits++;
            end -= 4;
            i = epfd;                                         // the cut moved everything after it
        }
        if (hits > 0) File.WriteAllBytes(espPath, bytes);
        return hits;
    }

    /// <summary>The offset of the major-record header of signature <paramref name="sig"/> whose on-disk FormID is
    /// <paramref name="rawFormId"/> (see <see cref="SetDeletedFlag"/> for what that dword is), or -1.</summary>
    static int FindRecord(byte[] bytes, string sig, uint rawFormId)
    {
        var s = System.Text.Encoding.ASCII.GetBytes(sig);
        for (int i = 0; i + 24 <= bytes.Length; i++)
        {
            if (bytes[i] != s[0] || bytes[i + 1] != s[1] || bytes[i + 2] != s[2] || bytes[i + 3] != s[3]) continue;
            if (BitConverter.ToUInt32(bytes, i + 12) == rawFormId) return i;
        }
        return -1;
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

    /// <summary>Corrupt every EPFT subrecord's parameter-type flag byte in the written plugin (sig + len(2) + 1-byte
    /// payload → payload at +6), returning how many were hit. This is the suite's canonical "a body whose LAZY parse
    /// throws" fixture: a perk with one entry-point effect, its EPFT byte set to a value that is not a legal parameter
    /// type, so <c>ParseEffect</c> throws the moment anything reaches for the perk's Effects. Callers write exactly one
    /// entry-point effect and assert exactly one hit.</summary>
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
