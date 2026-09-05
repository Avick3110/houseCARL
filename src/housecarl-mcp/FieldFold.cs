using HousecarlCore;

namespace HousecarlMcp;

/// <summary>One project.fields path's quantified step: the LIST path the read actually runs, the sub-path after
/// the quantifier (empty when the token ends the path), and the fold the caller spelled.</summary>
sealed record FieldFold(string Requested, string Root, string[] Tail, PathFold Fold);

/// <summary>
/// The PROJECT half of the quantified path step. <c>[*count]</c> on a project.fields path yields ONE number per
/// record — how many elements the list holds. <c>[*]</c> yields ONE row per element: the row shape the 'rows'
/// form produces, which is why the fold itself is <see cref="RowProjection"/>'s and not a second one, and why a
/// sub-path after the token (<c>Effects[*].Data.Magnitude</c>) is one line per element instead.
///
/// <para>The tokens are read through <see cref="PathFoldGrammar"/>, the same tokenizer <c>where=</c> parses with,
/// so a token means one thing on both surfaces. What differs is which folds each side accepts: a set is not a
/// boolean and a boolean is not a row, so each surface refuses the other's folds by name.</para>
/// </summary>
sealed record FoldPlan(IReadOnlyList<string> Requested, string[] ReadPaths, FieldFold?[] Folds, int Depth)
{
    /// <summary>Does any path render per-element rows — the reading that needs the list opened.</summary>
    internal bool RendersElements => Folds.Any(f => f is { Fold: PathFold.Set });

    /// <summary>The first quantified path, for a refusal that has to name one.</summary>
    internal FieldFold First => Folds.First(f => f is not null)!;

    /// <summary>One record's lines, grouped per REQUESTED path and in the caller's own order: a quantified path
    /// contributes its count cell or its element rows, an ordinary path the lines the read emitted for it.
    /// Grouped rather than flat because the columnar render needs to know which column varies per element.</summary>
    internal (IReadOnlyList<FieldValue>[]? Columns, string? Error) Columns(RecordFields rec)
    {
        var setRoots = Folds.Where(f => f is { Fold: PathFold.Set }).Select(f => f!.Root).Distinct(StringComparer.Ordinal)
                            .OrderByDescending(r => r.Length).ToList();
        // The element rows come from the 'rows' fold itself, run over the same lines — so a row here and a row
        // under form='rows' are the same row, never two renderings of one idea.
        var rows = setRoots.Count > 0 ? RowProjection.Fold(rec.Fields, setRoots, Depth) : rec.Fields;

        var cols = new IReadOnlyList<FieldValue>[ReadPaths.Length];
        for (int i = 0; i < ReadPaths.Length; i++)
        {
            if (Folds[i] is not { } fold) { cols[i] = Lines(rec.Fields, ReadPaths[i]); continue; }
            var head = rec.Fields.FirstOrDefault(f => f.Path == fold.Root);
            // An absent or unreadable list is the READ's answer, not a misuse of the token: it carries out under
            // the caller's own spelling. A root that resolved to something with no elements at all IS a misuse,
            // and it fails the record by name.
            if (head is null || !head.Present || !head.Readable)
            {
                cols[i] = new[] { (head ?? new FieldValue(fold.Root, false, null, ReadEngine.AbsentNote, Present: false)) with { Path = fold.Requested } };
                continue;
            }
            if (head.Count is null) return (null, NotAList(rec, fold, head));
            cols[i] = fold.Fold == PathFold.Count
                ? new[] { new FieldValue(fold.Requested, true, head.Count.Value.ToString(), null) }
                : Elements(rows, rec.Fields, fold).ToList();
        }
        return (cols, null);
    }

    /// <summary>One outcome with its fields folded — a failed read has no body and passes through.</summary>
    internal ReadOutcome Apply(ReadOutcome o)
    {
        if (o.Record is null) return o;
        var (cols, error) = Columns(o.Record);
        return error is null ? o with { Record = o.Record with { Fields = cols!.SelectMany(c => c).ToList() } }
                             : o with { Record = null, Error = error };
    }

    internal IReadOnlyList<ReadOutcome> Apply(IReadOnlyList<ReadOutcome> outcomes)
        => outcomes.Select(Apply).ToList();

    /// <summary>The rows a <c>[*]</c> path contributes: the folded element line per element when the token ends
    /// the path, else that element's own sub-path line. Emission order IS element order, since the read walks a
    /// list in order.</summary>
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

    /// <summary>The lines an ordinary (unquantified) path contributed: its own, plus whatever the depth read
    /// emitted under it.</summary>
    static IReadOnlyList<FieldValue> Lines(IReadOnlyList<FieldValue> fields, string path)
        => fields.Where(f => f.Path == path || RowProjection.IsUnder(f.Path, path)).ToList();

    /// <summary>The sentence a quantifier on a non-list step fails the record with: the caller's path, what the
    /// step actually holds, and the two ways to read it.</summary>
    static string NotAList(RecordFields rec, FieldFold fold, FieldValue head)
        => $"project.fields path '{fold.Requested}' quantifies a LIST, and '{fold.Root}' on {rec.Type} " +
           $"{(head.HasValue ? "holds a single value" : "is a substruct")} — drop the quantifier to read it, " +
           "or name a list path (index an element, e.g. 'Effects[0]', to read just that one).";
}

/// <summary>The projection-side parser for the quantified step.</summary>
static class FieldFolds
{
    /// <summary>Parse project.fields into the paths the READ runs plus the fold each requested path declares, or
    /// the one-sentence refusal. Null plan = no path carries a quantifier. Every refusal names the caller's own
    /// path and what to write instead, and the parse runs before any read so a bad token refuses the CALL rather
    /// than each record.</summary>
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
                // A quantifier on the containment step is that grammar's mistake, not a projection one — left to
                // the read walk's shared check, so where= and project.fields refuse it in the same sentence.
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
        // The depth the TOKENS require: an element lives one level under its list, and each sub-path step after
        // the token is one more. A [*count]-only plan needs no expansion at all — the list's own line carries the
        // count. The caller's own depth is folded in above this, where it can still be deeper.
        int need = 1;
        foreach (var f in folds)
            if (f is { Fold: PathFold.Set }) need = Math.Max(need, 2 + f.Tail.Length);
        return (new FoldPlan(fields, readPaths, folds, need), null);
    }
}
