using HousecarlCore;

namespace HousecarlMcp;

/// <summary>
/// The 'rows' project form: fold a depth-expanded read of a LIST field into ONE line per element.
///
/// <para>A struct list read the ordinary way costs one line per modeled sub-field per element — a 40-row CTDA
/// stack is ~1,000 lines, most of them absent parameters, and the render truncates before the stack is readable.
/// This folds each element's lines onto the element's own path, so the same stack is 40 lines and one call.</para>
///
/// <para>The rule is one sentence: a row line carries the element's own summary followed by every sub-field the
/// read FOUND, and only an ABSENT optional is omitted. Nothing else is dropped — a declared-but-null link stays
/// (an empty slot is a fact: it is what the None-property diagnostics read), a nested container keeps its count.
/// An unreadable sub-field would keep its note too — a read fault is not an absence — though no read reaching this
/// fold emits one: <c>ReadEngine.Expand</c> skips a nested property-get fault instead of naming it, so the keep is
/// a guarantee about the fold, not a line a caller sees today. The fold is over emitted paths, so
/// it is general to any list of any element type; conditions are the case that motivated it, not a case it knows
/// about.</para>
///
/// <para>A row is carried STRUCTURALLY as well as as text: the row's <see cref="FieldValue.Cells"/> hold the
/// leaves it folded, so the json render emits one object per element with its cells' own values, links and
/// counts rather than a sentence a consumer would have to parse.</para>
/// </summary>
static class RowProjection
{
    /// <summary>The depth a 'rows' read runs at when the caller names none: the element, plus two levels of its
    /// substructs — enough to reach an Effect's Data.Magnitude and a condition arm's parameters, which are the
    /// values that make a row worth reading. Every level still folds onto the one line.</summary>
    internal const int DefaultDepth = 4;

    /// <summary>Separator between a row's cells. Values carry spaces of their own, so the cells need a mark that
    /// a value cannot be mistaken for.</summary>
    const string Sep = " | ";

    /// <summary>Fold every outcome's field list. A failed read has no body and passes through; a root that
    /// resolved to something that is NOT a list fails that record loud, because the form's whole answer is rows
    /// and there are none to give.</summary>
    internal static IReadOnlyList<ReadOutcome> Apply(IReadOnlyList<ReadOutcome> outcomes, IReadOnlyList<string> roots, int depth)
    {
        var order = roots.OrderByDescending(r => r.Length).ToList();   // a root that prefixes another must not claim its rows
        var folded = new List<ReadOutcome>(outcomes.Count);
        foreach (var o in outcomes)
        {
            if (o.Record is null) { folded.Add(o); continue; }
            var bad = NotAList(o.Record, roots);
            folded.Add(bad is null
                ? o with { Record = o.Record with { Fields = Fold(o.Record.Fields, order, depth) } }
                : o with { Record = null, Error = bad });
        }
        return folded;
    }

    /// <summary>The first named root this record answered with something that is not a list, as the sentence that
    /// fails the record. Judged on the root's OWN emitted line: a container carries a count, a value or a
    /// substruct carries none. An absent or unreadable root is left alone — that is the read's answer, not a
    /// misuse of the form — and an already-indexed root names one element, which is a row by construction.</summary>
    static string? NotAList(RecordFields rec, IReadOnlyList<string> roots)
    {
        foreach (var r in roots)
        {
            if (r.Length > 0 && r[^1] == ']') continue;
            var line = rec.Fields.FirstOrDefault(f => f.Path == r);
            if (line is null || !line.Present || line.Count is not null) continue;
            return $"project.form='rows' folds a LIST field to one line per element, and '{r}' on {rec.Type} " +
                   $"{(line.HasValue ? "holds a single value" : "is a substruct")} — read it with project.form='fields', " +
                   "or name a list path (index an element to fold just that one, e.g. 'Effects[0]').";
        }
        return null;
    }

    /// <summary>The fold itself, over the emitted lines in their emission order: lines under one element
    /// accumulate into that element's row, and anything that is not under an element passes through. A row is
    /// keyed by its PATH, not by contiguity, so an element whose lines are split by a nested list's rows (two
    /// overlapping roots) comes back as one row and never as two entries sharing a path. A cell is keyed by its
    /// path too: the read walks each requested path on its own, so overlapping roots emit the nested lines once
    /// per root, and a row keeps the first of each rather than printing the element's sub-fields twice.</summary>
    internal static IReadOnlyList<FieldValue> Fold(IReadOnlyList<FieldValue> fields, IReadOnlyList<string> roots, int depth)
    {
        var outp = new List<FieldValue>(fields.Count);
        var slotOf = new Dictionary<string, int>(StringComparer.Ordinal);       // row path → its place in the output
        var cells = new Dictionary<string, List<FieldValue>>(StringComparer.Ordinal);
        var seen = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);   // row path → the cell paths it holds

        foreach (var f in fields)
        {
            var key = RowKey(f.Path, roots);
            if (key is null) { outp.Add(Passthrough(f, depth)); continue; }
            if (!slotOf.TryGetValue(key, out int slot))
            {
                slot = outp.Count; slotOf[key] = slot; cells[key] = new List<FieldValue>();
                seen[key] = new HashSet<string>(StringComparer.Ordinal);
                outp.Add(f);                                                    // placeholder, rewritten below
            }
            // The element's own line always leads the row — it names the arm type, and dropping it could leave a
            // row with nothing to render at all. A sub-field is dropped only when it is an ABSENT optional.
            bool isElement = f.Path.Length == key.Length;
            if (!seen[key].Add(f.Path)) continue;
            if (isElement || f.Present || !f.Readable || f.Note != ReadEngine.AbsentNote) cells[key].Add(f);
        }
        foreach (var (key, slot) in slotOf) outp[slot] = Row(key, cells[key]);
        return outp;
    }

    /// <summary>One row: the joined text the text render prints, with the folded leaves carried beside it so the
    /// json render can emit them structurally.</summary>
    static FieldValue Row(string key, IReadOnlyList<FieldValue> kept)
    {
        var text = new List<string>(kept.Count);
        foreach (var f in kept)
            // A container summary carries an identity enrichment so a COLLAPSED line still says something
            // ("[Effect] BaseEffect=…"). On a row whose own sub-fields are right there it would just repeat
            // them, so it keeps only its type.
            text.Add(Cell(f, key, trimToType: kept.Any(g => IsUnder(g.Path, f.Path))));
        return new FieldValue(key, false, null, string.Join(Sep, text), Present: true, Cells: kept);
    }

    /// <summary>A line the fold does not own, as it should leave the form. The engine's expansion-truncation note
    /// offers a remedy written for the fields form; on this form it would send a caller to a depth that renders no
    /// rows, so the remedy — and only the remedy — is restated. Lowering the depth is offered only when the call
    /// ran above the floor the form can still render: at depth 3 it is a no-op and at depth 2 it is an increase,
    /// and naming one element is the only remedy left there.</summary>
    static FieldValue Passthrough(FieldValue f, int depth)
    {
        if (f.Note is not { } n || !n.StartsWith("(expansion truncated", StringComparison.Ordinal)) return f;
        int dash = n.IndexOf('—');
        if (dash < 0) return f;
        var lower = depth > 3 ? ", or lower depth to 3 (depth 2 leaves every element a bare type)" : "";
        return f with { Note = n[..(dash + 1)] + " the fold runs AFTER the read, so the elements past the cut are " +
                               "missing from the rows: name one element to fold it alone (e.g. \"Conditions[0]\")" +
                               lower + ")" };
    }

    /// <summary>Is <paramref name="path"/> a strict sub-field or element of <paramref name="owner"/>.</summary>
    internal static bool IsUnder(string path, string owner) =>
        path.Length > owner.Length && path.StartsWith(owner, StringComparison.Ordinal)
        && (path[owner.Length] == '.' || path[owner.Length] == '[');

    /// <summary>The element path a line belongs to — a requested root plus the first bracketed segment after it,
    /// or the root itself when the caller already indexed one element — or null when the line is the list's own
    /// summary, a non-list field, or the truncation note.</summary>
    internal static string? RowKey(string path, IReadOnlyList<string> roots)
    {
        foreach (var r in roots)
        {
            if (!path.StartsWith(r, StringComparison.Ordinal)) continue;
            if (r.Length > 0 && r[^1] == ']')                              // an indexed root IS the element it names
            {
                if (path.Length == r.Length || IsUnder(path, r)) return r;
                continue;
            }
            if (path.Length <= r.Length || path[r.Length] != '[') continue;
            int close = path.IndexOf(']', r.Length + 1);
            if (close < 0) continue;
            return path[..(close + 1)];
        }
        return null;
    }

    /// <summary>One cell: the sub-field's path relative to its element, its value or note, and the display-only
    /// annotations the ordinary render appends — the same text, inline.</summary>
    static string Cell(FieldValue f, string rowKey, bool trimToType)
    {
        var val = f.HasValue ? f.Token : f.Note;
        if (trimToType && !f.HasValue && val is { Length: > 0 } n && n[0] == '['
            && n.IndexOf(']') is var close && close > 0 && close < n.Length - 1)
            val = n[..(close + 1)];
        if (f.Display is not null) val += $" ({f.Display})";
        if (f.Link is not null) val += $" ({Wire.LinkText(f.Link)})";
        if (f.Path.Length == rowKey.Length) return val ?? "";
        return $"{f.Path[rowKey.Length..].TrimStart('.')}={val}";
    }
}
