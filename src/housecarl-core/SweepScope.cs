using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlCore;

/// <summary>The record-level narrowing the sweep families (errors and scripts) share, reusing the read tools'
/// vocabulary. Narrowing narrows the numbers; contracts in docs/architecture/check-families.md.</summary>
public sealed class SweepScope
{
    /// <summary>The exact records to sweep, or null for "any".</summary>
    public IReadOnlySet<FormKey>? Formids { get; }

    /// <summary>A case-insensitive substring the record's EditorID must contain, or null for "any"; a record with no
    /// EditorID never matches a non-null filter.</summary>
    public string? EditorIdContains { get; }

    /// <summary>The getter Type(s) to stream, or null for every record type — the union the caller resolved from
    /// <c>types=</c>.</summary>
    public IReadOnlyList<Type>? Types { get; }

    /// <summary>The user-facing spelling of <see cref="Types"/> (the raw <c>types=</c> entries), for <see cref="Label"/>.</summary>
    public string? TypeLabel { get; }

    // The same entries with every one that EXPANDED spelling its arms, for TypeScopeLabel; null where the caller
    // built no such spelling, and TypeLabel then stands in.
    readonly string? _armLabel;

    public SweepScope(IReadOnlySet<FormKey>? formids, string? editorIdContains,
                      IReadOnlyList<Type>? types, string? typeLabel, string? armLabel = null)
    {
        Formids = formids is { Count: > 0 } ? formids : null;
        EditorIdContains = string.IsNullOrWhiteSpace(editorIdContains) ? null : editorIdContains.Trim();
        Types = types is { Count: > 0 } ? types : null;
        TypeLabel = Types is null ? null : typeLabel;
        _armLabel = Types is null ? null : armLabel;
    }

    /// <summary>The scope's types spelled for the short-listing rule, but only where the scope covers more than one
    /// TYPE (one entry can expand to several arms, and the listing is shared across them). Null otherwise.</summary>
    public string? TypeScopeLabel => Types is { Count: > 1 } ? (_armLabel ?? TypeLabel) : null;

    /// <summary>One <c>types=</c> entry spelled with the arms it resolved to, where it resolved to more than one; a
    /// concrete entry is spelled as itself.</summary>
    public static string SpellTypeEntry(string entry, IReadOnlyList<Type> arms)
        => arms.Count > 1 ? $"{entry} → {string.Join(", ", arms.Select(GetterName))}" : entry;

    /// <summary>A getter interface's user-facing type name — the catalog spelling <c>types=</c> itself takes.</summary>
    static string GetterName(Type t)
    {
        var n = t.Name;
        if (n.Length > 1 && n[0] == 'I' && char.IsUpper(n[1])) n = n[1..];
        if (n.EndsWith("Getter", StringComparison.Ordinal)) n = n[..^"Getter".Length];
        return n;
    }

    /// <summary>True when nothing is actually narrowed, so the caller can pass null and keep the unscoped path
    /// byte-identical.</summary>
    public bool IsEmpty => Formids is null && EditorIdContains is null && Types is null;

    /// <summary>Does this record fall inside the scope? <see cref="Types"/> is not re-tested here — it is applied at
    /// the stream, through <see cref="RecordArms"/>, on every lane a sweep reads.</summary>
    public bool Matches(FormKey fk, IMajorRecordGetter body)
    {
        if (Formids is not null && !Formids.Contains(fk)) return false;
        if (EditorIdContains is not null)
        {
            var eid = body.EditorID;
            if (eid is null || !eid.Contains(EditorIdContains, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    /// <summary>The applied narrowing, spelled for the render, or null when nothing is applied.</summary>
    public string? Label
    {
        get
        {
            if (IsEmpty) return null;
            var parts = new List<string>(3);
            if (Types is not null) parts.Add($"types=[{TypeLabel}]");
            if (Formids is not null) parts.Add($"{Formids.Count} formid(s)");
            if (EditorIdContains is not null) parts.Add($"editorid_contains='{EditorIdContains}'");
            return string.Join(", ", parts);
        }
    }

    /// <summary>An OFF-ORDER file's record stream, type-scoped when the caller asked for one — the one home both
    /// sweep families take, through the same <see cref="RecordArms"/> re-check the in-order lanes use.</summary>
    public static IEnumerable<IMajorRecordGetter> RecordsFrom(ISkyrimModGetter ov, SweepScope? scope)
        => scope?.Types is { Count: > 0 } types
            ? RecordArms.OfTypes(ov, types)
            : ov.EnumerateMajorRecords();
}

/// <summary>One row of a <c>counts_only=true</c> histogram: a key and how many findings in the swept scope carry it.</summary>
public sealed record SweepCount(string Key, int Count);

/// <summary>The error classes the errors family can be filtered to (<c>findings=</c>); excluding a class skips the work
/// that finds it. Parse failures are deliberately not a member.</summary>
[Flags]
public enum ErrorFindingClass
{
    None = 0,
    /// <summary>A non-null FormLink no active plugin defines.</summary>
    Dangling = 1,
    /// <summary>A master a plugin declares that is not present in the active order.</summary>
    MissingMasters = 2,
    All = Dangling | MissingMasters,
}

/// <summary>The finding classes the scripts family can be filtered to (<c>findings=</c>), in severity order.
/// Unverifiable attachments are deliberately not a member.</summary>
[Flags]
public enum ScriptFindingClass
{
    None = 0,
    /// <summary>Declared but not bound, of a form/object type ⇒ None at runtime. HIGH.</summary>
    UnboundObject = 1,
    /// <summary>Declared but not bound, an uninitialized scalar ⇒ 0/false/"". MEDIUM.</summary>
    UnboundScalar = 2,
    /// <summary>Present in the VMAD with a null Object link. Advisory.</summary>
    BoundNull = 4,
    All = UnboundObject | UnboundScalar | BoundNull,
}

/// <summary>Parsers for the sweep tools' <c>findings=</c> vocabularies. A name outside the vocabulary is a named
/// refusal listing every legal value; an empty or omitted list means every class.</summary>
public static class SweepFindings
{
    /// <summary>The errors family's <c>findings=</c>: <c>dangling</c> / <c>missing_masters</c>.</summary>
    public static bool TryParseErrorClasses(IReadOnlyList<string>? names, out ErrorFindingClass classes, out string? error)
    {
        classes = ErrorFindingClass.All; error = null;
        if (names is not { Count: > 0 }) return true;
        var acc = ErrorFindingClass.None;
        foreach (var raw in names)
        {
            switch (Normalize(raw))
            {
                case "dangling": acc |= ErrorFindingClass.Dangling; break;
                case "missing_masters": acc |= ErrorFindingClass.MissingMasters; break;
                default:
                    error = $"findings='{raw}' is not a check_errors finding class — use 'dangling' and/or 'missing_masters'. " +
                            "Unscannable records and scan errors are ALWAYS reported and cannot be filtered out (a suppressed " +
                            "'could not read' would read as a clean result).";
                    classes = ErrorFindingClass.All;
                    return false;
            }
        }
        classes = acc;
        return true;
    }

    /// <summary>The scripts family's <c>findings=</c>: <c>unbound_object</c> / <c>unbound_scalar</c> /
    /// <c>bound_null</c>, plus the convenience alias <c>unbound</c> (both unbound classes).</summary>
    public static bool TryParseScriptClasses(IReadOnlyList<string>? names, out ScriptFindingClass classes, out string? error)
    {
        classes = ScriptFindingClass.All; error = null;
        if (names is not { Count: > 0 }) return true;
        var acc = ScriptFindingClass.None;
        foreach (var raw in names)
        {
            switch (Normalize(raw))
            {
                case "unbound_object": acc |= ScriptFindingClass.UnboundObject; break;
                case "unbound_scalar": acc |= ScriptFindingClass.UnboundScalar; break;
                case "unbound": acc |= ScriptFindingClass.UnboundObject | ScriptFindingClass.UnboundScalar; break;
                case "bound_null": acc |= ScriptFindingClass.BoundNull; break;
                default:
                    error = $"findings='{raw}' is not a validate_scripts finding class — use 'unbound_object' (the HIGH " +
                            "silent-None class), 'unbound_scalar', 'unbound' (both), and/or 'bound_null'. Unverifiable " +
                            "attachments are ALWAYS reported and cannot be filtered out (a suppressed 'could not check' " +
                            "would read as a clean result).";
                    classes = ScriptFindingClass.All;
                    return false;
            }
        }
        classes = acc;
        return true;
    }

    /// <summary>The applied class filter spelled for the render, or null when every class is included.</summary>
    public static string? Describe(ErrorFindingClass c)
        => c == ErrorFindingClass.All ? null : $"findings=[{string.Join(", ", Names(c))}]";

    /// <summary>The applied class filter spelled for the render, or null when every class is included.</summary>
    public static string? Describe(ScriptFindingClass c)
        => c == ScriptFindingClass.All ? null : $"findings=[{string.Join(", ", Names(c))}]";

    /// <summary>The class tokens a flag set spells, for a caller handing one family's classes to that family's own
    /// sweep; the round trip through the family parsers is pinned by CLASS-TOKEN-ROUND-TRIP in CheckMergeProbe.</summary>
    public static IReadOnlyList<string> Tokens(ErrorFindingClass c) => Names(c).ToList();

    /// <summary>The same, for the scripts family's classes.</summary>
    public static IReadOnlyList<string> Tokens(ScriptFindingClass c) => Names(c).ToList();

    static IEnumerable<string> Names(ErrorFindingClass c)
    {
        if (c.HasFlag(ErrorFindingClass.Dangling)) yield return "dangling";
        if (c.HasFlag(ErrorFindingClass.MissingMasters)) yield return "missing_masters";
        if (c == ErrorFindingClass.None) yield return "none";
    }

    static IEnumerable<string> Names(ScriptFindingClass c)
    {
        if (c.HasFlag(ScriptFindingClass.UnboundObject)) yield return "unbound_object";
        if (c.HasFlag(ScriptFindingClass.UnboundScalar)) yield return "unbound_scalar";
        if (c.HasFlag(ScriptFindingClass.BoundNull)) yield return "bound_null";
        if (c == ScriptFindingClass.None) yield return "none";
    }

    /// <summary>The default subset-claim sentence, shared so the two sweeps cannot word it differently.</summary>
    public const string ScopedCountsClaim =
        "every count below is for THIS narrowed scope, not the whole plugin(s).";

    /// <summary>Join the applied-narrowing clauses into the ONE render line, optionally followed by
    /// <paramref name="claim"/>; null when nothing was narrowed at all. Which narrowings carry a claim is the caller's
    /// call — contract in docs/architecture/check-families.md.</summary>
    public static string? FilterNote(string? claim, params string?[] clauses)
    {
        var kept = clauses.Where(c => !string.IsNullOrEmpty(c)).ToList();
        if (kept.Count == 0) return null;
        var prefix = "NARROWED to " + string.Join(" · ", kept);
        return claim is null ? prefix + "." : prefix + " — " + claim;
    }

    static string Normalize(string? s) => (s ?? "").Trim().Replace('-', '_').ToLowerInvariant();

    /// <summary>Order a <c>counts_only=true</c> histogram for display: commonest first, ties broken by key so the
    /// output is stable across runs.</summary>
    public static List<SweepCount> Histogram(Dictionary<string, int> acc)
        => acc.Select(kv => new SweepCount(kv.Key, kv.Value))
              .OrderByDescending(c => c.Count)
              .ThenBy(c => c.Key, StringComparer.OrdinalIgnoreCase)
              .ToList();
}
