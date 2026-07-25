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
