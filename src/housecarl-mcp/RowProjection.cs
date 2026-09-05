using HousecarlCore;

namespace HousecarlMcp;

/// <summary>
/// The 'rows' project form: fold a depth-expanded read of a LIST field into ONE line per element.
///
/// <para>A struct list read the ordinary way costs one line per modeled sub-field per element — a 40-row CTDA
/// stack is ~1,000 lines, most of them absent parameters, and the render truncates before the stack is readable.
/// This folds each element's lines onto the element's own path, so the same stack is 40 lines and one call.</para>
///
/// <para>The rule is one sentence: a row line carries the element's own summary followed by every sub-field that
/// is THERE, and absent optionals and null links are omitted. Nothing else is dropped — an unreadable sub-field
/// keeps its note (a read fault is not an absence), a nested container keeps its count, and the expansion
/// truncation note passes through untouched. The fold is over emitted paths, so it is general to any list of any
/// element type; conditions are the case that motivated it, not a case it knows about.</para>
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

    /// <summary>Fold every outcome's field list. A failed read has no body and passes through.</summary>
    internal static IReadOnlyList<ReadOutcome> Apply(IReadOnlyList<ReadOutcome> outcomes, IReadOnlyList<string> roots)
    {
        var order = roots.OrderByDescending(r => r.Length).ToList();   // a root that prefixes another must not claim its rows
        var folded = new List<ReadOutcome>(outcomes.Count);
        foreach (var o in outcomes)
            folded.Add(o.Record is null ? o : o with { Record = o.Record with { Fields = Fold(o.Record.Fields, order) } });
        return folded;
    }

    /// <summary>The fold itself, over the emitted lines in their emission order: lines under one element
    /// accumulate into that element's row, and anything that is not under an element passes through.</summary>
    internal static IReadOnlyList<FieldValue> Fold(IReadOnlyList<FieldValue> fields, IReadOnlyList<string> roots)
    {
        var outp = new List<FieldValue>(fields.Count);
        string? openRow = null;
        List<FieldValue>? kept = null;

        void Close()
        {
            if (openRow is null) return;
            var cells = new List<string>(kept!.Count);
            foreach (var f in kept)
                // A container summary carries an identity enrichment so a COLLAPSED line still says something
                // ("[Effect] BaseEffect=…"). On a row whose own sub-fields are right there it would just repeat
                // them, so it keeps only its type.
                cells.Add(Cell(f, openRow, trimToType: kept.Any(g => IsUnder(g.Path, f.Path))));
            outp.Add(new FieldValue(openRow, false, null, string.Join(Sep, cells), Present: true));
            openRow = null; kept = null;
        }

        foreach (var f in fields)
        {
            var key = RowKey(f.Path, roots);
            if (key is null) { Close(); outp.Add(f); continue; }
            if (key != openRow) { Close(); openRow = key; kept = new List<FieldValue>(); }
            bool isElement = f.Path.Length == key.Length;
            // The element's own line always leads the row — it names the arm type, and dropping it could leave a
            // row with nothing to render at all. Its sub-fields are kept only when something is there.
            if (isElement || f.Present || !f.Readable) kept!.Add(f);
        }
        Close();
        return outp;
    }

    /// <summary>Is <paramref name="path"/> a strict sub-field or element of <paramref name="owner"/>.</summary>
    static bool IsUnder(string path, string owner) =>
        path.Length > owner.Length && path.StartsWith(owner, StringComparison.Ordinal)
        && (path[owner.Length] == '.' || path[owner.Length] == '[');

    /// <summary>The element path a line belongs to — a requested root plus the first bracketed segment after it —
    /// or null when the line is the list's own summary, a non-list field, or the truncation note.</summary>
    static string? RowKey(string path, IReadOnlyList<string> roots)
    {
        foreach (var r in roots)
        {
            if (path.Length <= r.Length || !path.StartsWith(r, StringComparison.Ordinal)) continue;
            if (path[r.Length] != '[') continue;
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
