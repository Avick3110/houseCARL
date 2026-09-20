using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;

namespace HousecarlCore;

/// <summary>The ONE way a scan reads a record's links, so a record whose links only read leniently is reachable in
/// all of them or in none; contract in docs/architecture/read-engine.md.</summary>
public static class RecordLinks
{
    /// <summary>What one link does to the walk: keep going, or stop here.</summary>
    public enum Step { Continue, Stop }

    /// <summary>A visitor over a record's links. A STRUCT implementation is passed by reference, so the walk
    /// allocates neither a closure nor a delegate per record.</summary>
    public interface IVisitor
    {
        Step Link(FormKey key);
    }

    /// <summary>Hand every FormKey this record links to <paramref name="visitor"/>. Answers null when Mutagen's own
    /// walk finished, else the sentence naming what a LENIENT re-read could not reach; RETHROWS when nothing can be
    /// recovered. Links the plain walk yielded before it threw are kept, so a visitor can see a key twice.</summary>
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

    /// <summary>Every FormKey this record links, collected — same contract as <see cref="Walk{TVisitor}"/>.</summary>
    public static string? Collect(IMajorRecordGetter record, HashSet<FormKey> into)
    {
        var v = new Collector(into);
        return Walk(record, ref v);
    }
}
