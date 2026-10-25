using Mutagen.Bethesda.Plugins.Records;

namespace HousecarlCore;

/// <summary>The loud line for a subrecord Mutagen writes at 0 bytes on a new record; which ones is the generator's probe (<see cref="TypeSchema.EmptyOnCreate"/>). houseCARL fills nothing.</summary>
public static class EmptyOnCreate
{
    /// <summary>One sentence per probed subrecord of <paramref name="rec"/> that no edit of this create set; an author's value, empty included, is silent.</summary>
    public static IReadOnlyList<string> Warnings(CorpusRulebook rulebook, IMajorRecord rec, IEnumerable<WriteRequest> edits)
    {
        if (rulebook.Type(rec.GetType().Name) is not { EmptyOnCreate: { Count: > 0 } empties } ts) return Array.Empty<string>();
        var touched = edits.Where(e => e.Path.Length >= 1).Select(e => e.Path[0]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return empties.Where(e => !e.Fields.Any(touched.Contains)).Select(e => Sentence(ts, e)).ToList();
    }

    static string Sentence(TypeSchema ts, EmptySubrecord e) =>
        $"Mutagen writes {ts.Signature} {e.Subrecord} at 0 bytes on a new {ts.Name} and houseCARL fills nothing; vanilla never " +
        $"carries it empty. Any value set on {(e.Fields.Count == 1 ? e.Fields[0] : "one of " + string.Join(", ", e.Fields))} " +
        "silences this line, and houseCARL does not check that value's size or contents.";
}
