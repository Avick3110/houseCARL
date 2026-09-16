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
    /// <summary>What one link does to the walk: keep going, or stop here. A filter that has its answer after the
    /// first matching link says so rather than paying the rest of the record — the early exit the separate
    /// <c>references_none=</c> walk used to have.</summary>
    public enum Step { Continue, Stop }

    /// <summary>A visitor over a record's links. A STRUCT implementation is passed by reference, so a caller's state
    /// lives on its own stack frame and the walk allocates neither a closure nor a delegate per record — which
    /// matters on the index build, where this runs for every record of every plugin.</summary>
    public interface IVisitor
    {
        Step Link(FormKey key);
    }

    /// <summary>Hand every FormKey this record links to <paramref name="visitor"/>. Returns null when Mutagen's own
    /// walk finished, or the sentence naming what a LENIENT re-read could not reach when it did not. The links the
    /// plain walk had already yielded before it threw are kept — they are proved — and the re-read's own links
    /// follow, so a visitor can see a key twice and callers collect into a set.
    ///
    /// <para>RETHROWS when nothing can be recovered, which is every case outside the one lenient read
    /// (<see cref="PerkEffectDecode"/>) — so a caller's existing unscannable accounting is untouched.</para></summary>
    public static string? Walk<TVisitor>(IMajorRecordGetter record, ref TVisitor visitor)
        where TVisitor : struct, IVisitor
    {
        if (record is not IFormLinkContainerGetter flc) return null;
        try
        {
            foreach (var l in flc.EnumerateFormLinks())
                if (visitor.Link(l.FormKey) == Step.Stop) return null;
            return null;
        }
        catch (Exception)
        {
            if (PerkEffectDecode.ReadLinks(record) is not { } relaxed) throw;
            foreach (var fk in relaxed.Links)
                if (visitor.Link(fk) == Step.Stop) break;
            return relaxed.Note;
        }
    }

    /// <summary>The collecting visitor every caller that just wants the keys uses.</summary>
    public struct Collector : IVisitor
    {
        public HashSet<FormKey> Keys;
        public Collector(HashSet<FormKey> keys) => Keys = keys;
        public Step Link(FormKey key) { Keys.Add(key); return Step.Continue; }
    }

    /// <summary>Every FormKey this record links, collected — the shape for a caller with no filter to short-circuit
    /// on. Same contract as <see cref="Walk{TVisitor}"/>: the note, or null, and a rethrow when nothing is
    /// recovered.</summary>
    public static string? Collect(IMajorRecordGetter record, HashSet<FormKey> into)
    {
        var v = new Collector(into);
        return Walk(record, ref v);
    }
}
