using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;

namespace HousecarlCore;

/// <summary>
/// The ONE way a scan reads a record's links. Mutagen's own whole-record walk is a lazy parse, so a single
/// unparseable part throws and the record drops out of the answer entirely — an unproved negative dressed as a
/// clean one. Every lane that walks links for a scan goes through here, so a record is reachable in all of them or
/// in none of them (#301).
/// </summary>
public static class RecordLinks
{
    /// <summary>Hand every FormKey this record links to <paramref name="onLink"/>. Returns null when Mutagen's own
    /// walk finished, or the sentence naming what a LENIENT re-read could not reach when it did not. The links the
    /// plain walk had already yielded before it threw are kept — they are proved — and the re-read's own links
    /// follow, so <paramref name="onLink"/> can see a key twice and callers collect into a set.
    ///
    /// <para>RETHROWS when nothing can be recovered, which is every case outside the one lenient read
    /// (<see cref="PerkEffectDecode"/>) — so a caller's existing unscannable accounting is untouched.</para></summary>
    public static string? Walk(IMajorRecordGetter record, Action<FormKey> onLink)
    {
        if (record is not IFormLinkContainerGetter flc) return null;
        try
        {
            foreach (var l in flc.EnumerateFormLinks()) onLink(l.FormKey);
            return null;
        }
        catch (Exception)
        {
            if (PerkEffectDecode.ReadLinks(record) is not { } relaxed) throw;
            foreach (var fk in relaxed.Links) onLink(fk);
            return relaxed.Note;
        }
    }
}
