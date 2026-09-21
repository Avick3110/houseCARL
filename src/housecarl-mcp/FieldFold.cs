using HousecarlCore;

namespace HousecarlMcp;

/// <summary>One project.fields path's quantified step: the LIST path the read runs, the sub-path after the
/// quantifier, and the fold the caller spelled.</summary>
sealed record FieldFold(string Requested, string Root, string[] Tail, PathFold Fold)
{
    /// <summary>How many expansion levels the sub-path adds below one element: a dotted step is one, and a
    /// bracketed index inside a step another, because <c>ReadEngine.Expand</c> spends one level on each.</summary>
    internal int TailLevels => Tail.Sum(s => 1 + s.Count(c => c == '['));
}

/// <summary>The PROJECT half of the quantified path step: <c>[*count]</c> yields ONE number per record, <c>[*]</c>
/// ONE row per element — the row shape the 'rows' form produces, which is why the fold itself is <see
/// cref="RowProjection"/>'s.</summary>
sealed record FoldPlan(IReadOnlyList<string> Requested, string[] Paths, FieldFold?[] Folds, int Depth, int CallerDepth = 1)
{
    /// <summary>What the READ is asked for: each distinct path once, with the depth that path's own column
    /// needs.</summary>
    internal (string[] Paths, int[] Depths) Read()
    {
        var at = new Dictionary<string, int>(StringComparer.Ordinal);
        var paths = new List<string>(Paths.Length);
        var depths = new List<int>(Paths.Length);
        for (int i = 0; i < Paths.Length; i++)
        {
            // A [*count] column renders the list's own count line and nothing under it, so it reads at depth 1.
            int d = Folds[i] switch { { Fold: PathFold.Set } => Depth, { Fold: PathFold.Count } => 1, _ => CallerDepth };
            if (at.TryGetValue(Paths[i], out int j)) { depths[j] = Math.Max(depths[j], d); continue; }
            at[Paths[i]] = paths.Count; paths.Add(Paths[i]); depths.Add(d);
        }
        return (paths.ToArray(), depths.ToArray());
    }

    /// <summary>Does any path render per-element rows — the reading that needs the list opened.</summary>
    internal bool RendersElements => Folds.Any(f => f is { Fold: PathFold.Set });

    /// <summary>The read paths whose every column is a <c>[*count]</c>, which take the child union's INDEX-ONLY
    /// tier (docs/architecture/records-owned-child-declarers.md); both tiers say the value is this body's own list.</summary>
    internal IReadOnlyList<string> CountOnlyPaths
    {
        get
        {
            var all = new HashSet<string>(StringComparer.Ordinal);
            var other = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < Paths.Length; i++)
                (Folds[i] is { Fold: PathFold.Count } ? all : other).Add(Paths[i]);
            all.ExceptWith(other);
            return all.ToList();
        }
    }

    /// <summary>The first quantified path, for a refusal that has to name one.</summary>
    internal FieldFold First => Folds.First(f => f is not null)!;

    /// <summary>The distinct LIST paths a <c>[*]</c> binds to, so a flattening render knows when a call names two.</summary>
    internal IReadOnlyList<string> SetRoots =>
        Folds.Where(f => f is { Fold: PathFold.Set }).Select(f => f!.Root).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>One record's lines, grouped per REQUESTED path and in the caller's own order, because the columnar
    /// render needs to know which column varies per element.</summary>
    internal (IReadOnlyList<FieldValue>[]? Columns, IReadOnlyList<FieldValue> Carried, string? Error) Columns(RecordFields rec)
    {
        var setRoots = SetRoots.OrderByDescending(r => r.Length).ToList();
        // The element rows come from the 'rows' fold itself, run over the same lines.
        var rows = setRoots.Count > 0 ? RowProjection.Fold(rec.Fields, setRoots, Depth) : rec.Fields;

        var cols = new IReadOnlyList<FieldValue>[Paths.Length];
        for (int i = 0; i < Paths.Length; i++)
        {
            if (Folds[i] is not { } fold) { cols[i] = Lines(rec.Fields, Paths[i], CallerDepth); continue; }
            var head = rec.Fields.FirstOrDefault(f => f.Path == fold.Root);
            // An absent or unreadable list is the READ's answer, not a misuse of the token; only a root that is
            // not a list at all fails the record by name.
            if (head is null || !head.Present || !head.Readable)
            {
                cols[i] = new[] { (head ?? new FieldValue(fold.Root, false, null, ReadEngine.AbsentNote, Present: false)) with { Path = fold.Requested } };
                continue;
            }
            if (head.Count is null) return (null, Array.Empty<FieldValue>(), NotAList(rec, fold, head));
            if (fold.Fold == PathFold.Count)
            {
                // The head line's annotation travels with the number; on a child-bearing field it says the count
                // is this body's own list, not the whole set the game assembles.
                cols[i] = new[] { new FieldValue(fold.Requested, true, head.Count.Value.ToString(), null) with { Display = head.Display } };
                continue;
            }
            var elems = Elements(rows, rec.Fields, fold).ToList();
            // No element row is still an ANSWER and never an empty column: an empty list carries its own summary
            // line out under the caller's spelling, and a sub-path no element carries says that in one note.
            cols[i] = elems.Count > 0 ? elems
                    : head.Count.Value == 0 ? new[] { head with { Path = fold.Requested } }
                    : new[] { new FieldValue(fold.Requested, false, null, NoElement(fold, head.Count.Value)) };
        }
        var claimed = new HashSet<string>(cols.SelectMany(c => c).Select(f => f.Path), StringComparer.Ordinal);
        // What no column claims and no requested path covers — the read's own truncation note — rides out beside
        // the columns rather than being dropped with them.
        var carried = (setRoots.Count > 0 ? rows : rec.Fields)
            .Where(f => !claimed.Contains(f.Path) && !Paths.Any(p => f.Path == p || RowProjection.IsUnder(f.Path, p)))
            .ToList();
        return (cols, carried, null);
    }

    /// <summary>One outcome with its fields folded — a failed read has no body and passes through.</summary>
    internal ReadOutcome Apply(ReadOutcome o)
    {
        if (o.Record is null) return o;
        var (cols, carried, error) = Columns(o.Record);
        // The carried note leads: it is what the read did to the columns below it.
        return error is null ? o with { Record = o.Record with { Fields = carried.Concat(cols!.SelectMany(c => c)).ToList() },
                                        OwnedChildFields = WithQuantifiedSpellings(o.OwnedChildFields) }
                             : o with { Record = null, Error = error };
    }

    /// <summary>The child-union annotation keyed by the spelling each quantified column RENDERS under, beside the
    /// list path it was read at, which the dense and manifest lanes still look up by root.</summary>
    IReadOnlyDictionary<string, ChildUnion?>? WithQuantifiedSpellings(IReadOnlyDictionary<string, ChildUnion?>? annotated)
    {
        if (annotated is not { Count: > 0 }) return annotated;
        Dictionary<string, ChildUnion?>? both = null;
        foreach (var f in Folds)
            if (f is not null && annotated.TryGetValue(f.Root, out var u) && !annotated.ContainsKey(f.Requested))
                (both ??= new Dictionary<string, ChildUnion?>(annotated, StringComparer.Ordinal))[f.Requested] = u;
        return both ?? annotated;
    }

    internal IReadOnlyList<ReadOutcome> Apply(IReadOnlyList<ReadOutcome> outcomes)
        => outcomes.Select(Apply).ToList();

    /// <summary>The rows a <c>[*]</c> path contributes; emission order IS element order.</summary>
    static IEnumerable<FieldValue> Elements(IReadOnlyList<FieldValue> rows, IReadOnlyList<FieldValue> raw, FieldFold fold)
    {
        var roots = new[] { fold.Root };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (fold.Tail.Length == 0)
        {
            foreach (var f in rows)
                if (RowProjection.RowKey(f.Path, roots) == f.Path && seen.Add(f.Path))
                    yield return f;
            yield break;
        }
        var tail = "." + string.Join(".", fold.Tail);
        foreach (var f in raw)
            if (RowProjection.RowKey(f.Path, roots) is { } key && f.Path == key + tail && seen.Add(f.Path))
                yield return f;
    }

    /// <summary>The lines an ordinary (unquantified) path contributed, at the depth the CALLER asked for.</summary>
    static IReadOnlyList<FieldValue> Lines(IReadOnlyList<FieldValue> fields, string path, int depth)
        => fields.Where(f => f.Path == path || (RowProjection.IsUnder(f.Path, path) && Levels(f.Path, path) < depth)).ToList();

    /// <summary>How many expansion levels below <paramref name="owner"/> a path sits — one per '.' or '[' step.</summary>
    static int Levels(string path, string owner)
    {
        int n = 0;
        for (int i = owner.Length; i < path.Length; i++) if (path[i] == '.' || path[i] == '[') n++;
        return n;
    }

    /// <summary>The element KEY a folded line carries, as TEXT, because the read brackets a DICT by its own key
    /// and a number would invent the elements between two keys; null when the path does not index the root.</summary>
    internal static string? ElementKey(string path, string root)
    {
        if (path.Length <= root.Length || path[root.Length] != '[') return null;
        int close = path.IndexOf(']', root.Length + 1);
        return close > 0 ? path[(root.Length + 1)..close] : null;
    }

    /// <summary>The note a sub-path no element carries answers with.</summary>
    static string NoElement(FieldFold fold, int count)
        => $"(no element of '{fold.Root}' carries '{string.Join(".", fold.Tail)}' — the list holds {count})";

    /// <summary>The sentence a quantifier on a non-list step fails the record with.</summary>
    static string NotAList(RecordFields rec, FieldFold fold, FieldValue head)
        => $"project.fields path '{fold.Requested}' quantifies a LIST, and '{fold.Root}' on {rec.Type} " +
           $"{(head.HasValue ? "holds a single value" : "is a substruct")} — drop the quantifier to read it, " +
           "or name a list path (index an element, e.g. 'Effects[0]', to read just that one).";
}

/// <summary>The projection-side parser for the quantified step.</summary>
static class FieldFolds
{
    /// <summary>Parse project.fields into the paths the READ runs plus the fold each path declares, or the
    /// one-sentence refusal; the parse runs before any read, so a bad token refuses the CALL, not each record.</summary>
    internal static (FoldPlan? Plan, string? Error) Parse(IReadOnlyList<string> fields)
    {
        var readPaths = new string[fields.Count];
        var folds = new FieldFold?[fields.Count];
        bool any = false;
        for (int i = 0; i < fields.Count; i++)
        {
            var path = fields[i] ?? "";
            readPaths[i] = path;
            var segs = path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            for (int s = 0; s < segs.Length; s++)
            {
                // A quantifier on the containment step is that grammar's mistake, left to the read walk's check.
                var (bare, fold, key) = PathFoldGrammar.Read(segs[s]);
                if (key is null || ContainmentIndex.IsParentStep(bare)) continue;
                if (folds[i] is not null)
                    return (null, $"project.fields path '{path}' quantifies two steps, and one row per element of one list is the shape this form renders — quantify the outer step and read the inner list on the row, or name a concrete element of one of them.");
                if (bare.Length == 0)
                    return (null, $"project.fields path '{path}': '{segs[s]}' has no field name before '[' — a quantifier binds to a list field, e.g. 'Conditions[*]'.");
                if (fold == PathFold.None)
                    return (null, $"project.fields path '{path}': '[{key}]' is not a quantifier — the tokens are [*], [*count], [*any], [*all] and [*none] (the last three fold to a boolean and belong in where=).");
                if (fold is PathFold.Any or PathFold.All or PathFold.NoneOf)
                    return (null, $"project.fields path '{path}' folds the elements into a boolean, and a boolean is not a row — use {PathFoldGrammar.Token(fold)} in where= to SELECT the records, and [*] here to read one row per element.");
                if (fold == PathFold.Count && s != segs.Length - 1)
                    return (null, $"project.fields path '{path}': nothing can follow '[*count]' — it yields how MANY elements there are, not an element to step into.");
                folds[i] = new FieldFold(path, string.Join(".", segs[..s].Append(bare)), segs[(s + 1)..], fold);
                readPaths[i] = folds[i]!.Root;
                any = true;
            }
        }
        if (!any) return (null, null);
        // The depth the TOKENS require: an element lives one level under its list, each sub-path step one more.
        int need = 1;
        foreach (var f in folds)
            if (f is { Fold: PathFold.Set }) need = Math.Max(need, 2 + f.TailLevels);
        return (new FoldPlan(fields, readPaths, folds, need), null);
    }
}
