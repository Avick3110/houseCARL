using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;

namespace HousecarlCore;

/// <summary>The ONE way a scan or a walk reads a record's links, so a record whose links only read leniently is
/// reachable in all of them or in none, and a deleted record in none; contract in docs/architecture/read-engine.md.</summary>
public static class RecordLinks
{
    public enum Step { Continue, Stop }

    /// <summary>A visitor over a record's links. A STRUCT implementation is passed by reference, so the walk
    /// allocates neither a closure nor a delegate per record.</summary>
    public interface IVisitor
    {
        Step Link(FormKey key);
        /// <summary>Mutagen's walk threw part-way and the lenient re-read starts over; a visitor that keeps order drops what it was handed.</summary>
        void Restart() { }
    }

    /// <summary>Hand every FormKey this record links to <paramref name="visitor"/>, in Mutagen's order, a null link
    /// included. A DELETED record links to nothing. Answers null when Mutagen's own walk finished, else what a LENIENT
    /// re-read could not reach; RETHROWS when nothing can be recovered.</summary>
    public static string? Walk<TVisitor>(IMajorRecordGetter record, ref TVisitor visitor)
        where TVisitor : struct, IVisitor
    {
        if (DeletedRecordRule.HasNoLiveBody(record) || record is not IFormLinkContainerGetter flc) return null;
        try
        {
            foreach (var l in flc.EnumerateFormLinks())
                if (visitor.Link(l.FormKey) == Step.Stop) return null;
            return null;
        }
        catch (Exception)
        {
            if (PerkEffectDecode.ReadLinks(record) is not { } relaxed) throw;
            visitor.Restart();
            foreach (var fk in relaxed.Links)
                if (visitor.Link(fk) == Step.Stop) break;
            return relaxed.Note;
        }
    }

    public struct Collector : IVisitor
    {
        public HashSet<FormKey> Keys;
        public Collector(HashSet<FormKey> keys) => Keys = keys;
        public Step Link(FormKey key) { Keys.Add(key); return Step.Continue; }
    }

    public struct Lister : IVisitor
    {
        public List<FormKey> Keys;
        public Lister(List<FormKey> keys) => Keys = keys;
        public Step Link(FormKey key) { Keys.Add(key); return Step.Continue; }
        public void Restart() => Keys.Clear();
    }

    /// <summary>Every FormKey this record links, in order and repeats kept, into <paramref name="into"/>, which is
    /// cleared first. Answers the lenient note as <see cref="Walk{TVisitor}"/> does; where that would rethrow a
    /// parse fault (<see cref="RecordParseFault"/>), <paramref name="into"/> is left empty and
    /// <paramref name="fault"/> names it, so each walk turns it into its own posture. Any other throw goes on up.</summary>
    public static string? List(IMajorRecordGetter record, List<FormKey> into, out string? fault)
    {
        into.Clear();
        fault = null;
        var v = new Lister(into);
        try { return Walk(record, ref v); }
        catch (Exception ex) when (RecordParseFault.Is(ex))
        {
            into.Clear();
            fault = $"{ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }

    /// <summary>Every FormKey this record links, collected — same contract as <see cref="Walk{TVisitor}"/>.</summary>
    public static string? Collect(IMajorRecordGetter record, HashSet<FormKey> into)
    {
        var v = new Collector(into);
        return Walk(record, ref v);
    }
}
