using System.Collections.Concurrent;
using System.Text.Json;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;

namespace HousecarlCore;

/// <summary>The part fields the asset walk follows by default: per record type (the name a read reports), the field
/// paths whose links hold a part of the record, plus the "*" paths every type is read at. Data, shipped as
/// walk-parts.json; contract in docs/architecture/assets.md, Walking the links.</summary>
public static class WalkParts
{
    /// <summary>The key whose paths every type is read at.</summary>
    public const string AnyType = "*";

    static readonly Lazy<IReadOnlyDictionary<string, string[]>> _byType = new(() =>
        JsonSerializer.Deserialize<Dictionary<string, string[]>>(EmbeddedJson.Read("walk-parts.json", "the asset walk's part-field list"))
        ?? throw new InvalidOperationException("the asset walk's part-field list walk-parts.json is empty."));

    /// <summary>Every entry as the file holds it, the "*" key included.</summary>
    public static IReadOnlyDictionary<string, string[]> ByType => _byType.Value;

    static readonly ConcurrentDictionary<string, IReadOnlyList<string>> _for = new(StringComparer.Ordinal);

    /// <summary>The part paths a record of <paramref name="type"/> is read at: its own, then the "*" paths.</summary>
    public static IReadOnlyList<string> For(string type) => _for.GetOrAdd(type, static t =>
        (ByType.GetValueOrDefault(t) ?? Array.Empty<string>()).Concat(ByType.GetValueOrDefault(AnyType) ?? Array.Empty<string>()).ToList());

    static readonly ConcurrentDictionary<(Type, string), bool> _has = new();

    /// <summary>The links under each of <paramref name="paths"/> on <paramref name="record"/>, each once in path order,
    /// into <paramref name="into"/>. A path whose first field the record's type does not have, or whose value is
    /// absent, gives none; a path that cannot be read is added to <paramref name="unread"/> with why. A deleted record
    /// links to nothing.</summary>
    public static void Links(IMajorRecordGetter record, IReadOnlyList<string> paths, List<FormKey> into, List<string> unread)
    {
        into.Clear();
        if (DeletedRecordRule.HasNoLiveBody(record)) return;
        var seen = new HashSet<FormKey>();
        foreach (var path in paths)
        {
            var segs = path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segs.Length == 0) continue;
            // Asked once per type and path, so a type without the field costs no diagnosis per record.
            if (!_has.GetOrAdd((record.GetType(), segs[0]), static k => WriteEngine.ResolveProperty(k.Item1, k.Item2) is not null)) continue;
            var (links, note) = ReadEngine.CollectLinksAt(record, segs);
            if (links is null)
            {
                // Absent, no such field, or not a link-bearing field: no links, not a fault.
                if (note is not null && !ReadEngine.IsAbsentNote(note) && !ReadEngine.IsNoSuchFieldNote(note)
                    && !note.StartsWith(ReadEngine.NoLinksPrefix, StringComparison.Ordinal))
                    unread.Add($"{path} {note}");
                continue;
            }
            foreach (var k in links)
                if (seen.Add(k)) into.Add(k);
        }
    }
}
