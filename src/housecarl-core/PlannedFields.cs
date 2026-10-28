using System.Collections;
using System.Collections.Concurrent;

namespace HousecarlCore;

/// <summary>A projection's field paths with the schema's plan over them, made once per call before any read: a path no type in scope carries refuses the call, and a path one record type lacks reads as a short token naming that type.</summary>
public sealed class PlannedFields : IReadOnlyList<string>
{
    readonly IReadOnlyList<string> _paths;
    readonly SchemaPathWalk _walk;
    readonly ConcurrentDictionary<(Type, string), string?> _lacks = new();
    readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _lacking = new(StringComparer.Ordinal);

    PlannedFields(IReadOnlyList<string> paths, SchemaPathWalk walk) { _paths = paths; _walk = walk; }

    /// <summary>The opening of the token a planned-lacking path reads as.</summary>
    public const string NotOnPrefix = "(not on ";

    /// <summary>The cell a path reads on a record whose type has no such field.</summary>
    public static string Token(string typeName) => $"{NotOnPrefix}{typeName})";

    /// <summary>Is this note the planned-lacking token, as opposed to a read's own note?</summary>
    public static bool IsNotOnNote(string? note) => note is not null && note.StartsWith(NotOnPrefix, StringComparison.Ordinal);

    /// <summary>The plan for <paramref name="paths"/> over <paramref name="roots"/> (null: any record type), or the one sentence refusing a path no type in scope carries.</summary>
    public static (PlannedFields? Plan, string? Refusal) Of(SchemaPathWalk walk, IReadOnlyList<string> paths, IReadOnlyList<TypeSchema>? roots)
    {
        var scope = roots ?? walk.Records;
        string where = roots is null ? "on any record type" : roots.Count == 1 ? $"of {roots[0].Name}" : $"of any of {string.Join(", ", roots.Select(r => r.Name))}";
        foreach (var p in paths)
        {
            var (hops, segs) = Split(p);
            if (hops < 0) continue;   // a malformed '*parent' step keeps the read's own note
            if (walk.Walk(scope, hops, segs, null, where).Failure is { } why)
                return (null, $"project.fields '{p}': {why}{(why.EndsWith('.') || why.EndsWith('?') ? "" : ".")}");
        }
        return (new PlannedFields(paths, walk), null);
    }

    /// <summary>The token <paramref name="path"/> reads as on a record of this runtime class when its type lacks the path, decided by the schema once per class; null keeps the read's own answer, as for a '*parent' path or a class the schema does not model.</summary>
    public string? TokenFor(Type runtime, string path) => _lacks.GetOrAdd((runtime, path), key =>
    {
        var (hops, segs) = Split(key.Item2);
        if (hops != 0 || _walk.RecordOf(key.Item1) is not { } ts) return null;
        if (_walk.Walk(new[] { ts }, 0, segs, null, $"of {ts.Name}").Failure is null) return null;
        _lacking.GetOrAdd(key.Item2, _ => new(StringComparer.Ordinal)).TryAdd(ts.Name, 0);
        return Token(ts.Name);
    });

    /// <summary>Decide the plan for the record types a scan's rows are, so a render that reads its rows lazily can state the notes in its header.</summary>
    public void Expect(IEnumerable<string> typeNames)
    {
        foreach (var n in typeNames.Distinct(StringComparer.Ordinal))
            if (_walk.Records.FirstOrDefault(r => r.Name == n) is { } ts && _walk.ClassOf(ts) is { } c)
                foreach (var p in _paths) TokenFor(c, p);
    }

    /// <summary>One note per path some type in scope lacks, with the schema's clause over those types (as where= words it), in the order the paths were asked.</summary>
    public IReadOnlyList<string> Notes() =>
        _paths.Distinct(StringComparer.Ordinal)
              .Where(_lacking.ContainsKey)
              .Select(p =>
              {
                  var types = _walk.Records.Where(r => _lacking[p].ContainsKey(r.Name)).OrderBy(r => r.Name, StringComparer.Ordinal).ToList();
                  string of = types.Count == 1 ? $"of {types[0].Name}" : $"of any of {string.Join(", ", types.Select(t => t.Name))}";
                  var why = _walk.Walk(types, 0, Split(p).Segs, null, of).Failure ?? $"'{p}' is not a field {of}";
                  return $"note: project.fields '{p}': {why}{(why.EndsWith('.') || why.EndsWith('?') ? "" : ".")} " +
                         (types.Count == 1 ? $"Rows of {types[0].Name} read '{Token(types[0].Name)}' for it." : "Rows of those types read '(not on <type>)' for it.");
              })
              .ToList();

    /// <summary>A path as the read splits it: its leading '*parent' hops (-1 when malformed) and the steps after them.</summary>
    static (int Hops, string[] Segs) Split(string path)
    {
        var segs = path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var (hops, err) = ContainmentIndex.SplitHops(segs, path);
        return err is not null ? (-1, segs) : (hops, segs[hops..]);
    }

    public string this[int index] => _paths[index];
    public int Count => _paths.Count;
    public IEnumerator<string> GetEnumerator() => _paths.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
