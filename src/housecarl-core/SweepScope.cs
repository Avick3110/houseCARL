using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;

namespace HousecarlCore;

/// <summary>
/// The RECORD-level narrowing the two sweep tools share (housecarl_check_errors, housecarl_validate_scripts — #282).
/// Both had exactly one scope knob, <c>plugins=</c>, and one script-heavy plugin (~183 scripted records, 711 unbound
/// findings) renders past the tool-result token cap — so the common follow-up question ("did the unbound count for
/// THESE few records change?") had no call that could ask it. This is that missing scope, in the vocabulary the sibling
/// read tools already speak: <see cref="Types"/> (cross_plugin_query's <c>type=</c>), <see cref="Formids"/>
/// (batch_record_detail's <c>formids=</c>), and <see cref="EditorIdContains"/> (cross_plugin_query's
/// <c>editorid_contains=</c>).
///
/// <para><b>Narrowing narrows the NUMBERS.</b> A scoped sweep's totals are the totals FOR THE SCOPE, exactly as
/// <c>plugins=</c> already behaves (scoping to one plugin reports that plugin's counts, not the order's). The
/// render therefore carries <see cref="Label"/> on its own line whenever anything is applied, so a count can never be
/// read as a wider claim than it is (Q3).</para>
///
/// <para><see cref="Types"/> is handed to the record STREAM (<c>RecordsIn</c>'s getter-type filter), so a type scope
/// costs nothing per skipped record; <see cref="Formids"/> and <see cref="EditorIdContains"/> are per-record tests
/// applied before any expensive work (the link walk / the .pex chain read).</para>
/// </summary>
public sealed class SweepScope
{
    /// <summary>The exact records to sweep, or null for "any". Set ⇒ every other record is skipped.</summary>
    public IReadOnlySet<FormKey>? Formids { get; }

    /// <summary>A case-insensitive substring the record's EditorID must contain, or null for "any". A record with NO
    /// EditorID never matches a non-null filter (an absent id cannot contain a substring — the honest answer, not a
    /// free pass).</summary>
    public string? EditorIdContains { get; }

    /// <summary>The getter Type(s) to stream, or null for every record type. Resolved by the caller from the user's
    /// <c>type=</c> string (the shared TypeLookup), so an unknown type fails loud before the sweep starts.</summary>
    public IReadOnlyList<Type>? Types { get; }

    /// <summary>The user-facing spelling of <see cref="Types"/> (the raw <c>type=</c> string), for <see cref="Label"/>.</summary>
    public string? TypeLabel { get; }

    public SweepScope(IReadOnlySet<FormKey>? formids, string? editorIdContains,
                      IReadOnlyList<Type>? types, string? typeLabel)
    {
        Formids = formids is { Count: > 0 } ? formids : null;
        EditorIdContains = string.IsNullOrWhiteSpace(editorIdContains) ? null : editorIdContains.Trim();
        Types = types is { Count: > 0 } ? types : null;
        TypeLabel = Types is null ? null : typeLabel;
    }

    /// <summary>True when nothing is actually narrowed (every knob absent) — the caller can then pass null and keep the
    /// unscoped path byte-identical.</summary>
    public bool IsEmpty => Formids is null && EditorIdContains is null && Types is null;

    /// <summary>Does this record fall inside the scope? <see cref="Types"/> is NOT re-tested here — it is applied at the
    /// stream, so a body reaching this call is already of a scoped type.</summary>
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
            if (Types is not null) parts.Add($"type={TypeLabel}");
            if (Formids is not null) parts.Add($"{Formids.Count} formid(s)");
            if (EditorIdContains is not null) parts.Add($"editorid_contains='{EditorIdContains}'");
            return string.Join(", ", parts);
        }
    }
}

/// <summary>One row of a <c>counts_only=true</c> histogram: a key (a property name, a target plugin) and how many
/// findings in the swept scope carry it.</summary>
public sealed record SweepCount(string Key, int Count);

/// <summary>The error classes housecarl_check_errors can be filtered to (<c>findings=</c>). Excluding a class SKIPS the
/// work that finds it — <see cref="Dangling"/> off skips the per-record link walk entirely, which is what turns "is any
/// master missing anywhere in my order" from a full sweep into a master-table read. Parse failures are deliberately NOT
/// a member: "houseCARL could not read this" is the honesty layer, not a finding class, and must never be filterable
/// away (Q3).</summary>
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

/// <summary>The finding classes housecarl_validate_scripts can be filtered to (<c>findings=</c>), in severity order:
/// an unbound OBJECT property is the silent-<c>None</c> footgun (HIGH), an unbound uninitialized SCALAR silently
/// defaults (MEDIUM), a bound-but-null object property is advisory. Unverifiable attachments are deliberately NOT a
/// member — same reason as the parse class above: an attachment houseCARL could not read must stay visible under every
/// filter, or the filter manufactures a false clean (Q3).</summary>
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

/// <summary>Parsers for the sweep tools' <c>findings=</c> vocabularies. A name outside the vocabulary is a NAMED
/// refusal listing every legal value (never a silent drop to "all", which would answer a different question than the
/// one asked — Q3). An empty/omitted list means "every class", the unfiltered default.</summary>
public static class SweepFindings
{
    /// <summary>housecarl_check_errors' <c>findings=</c>: <c>dangling</c> / <c>missing_masters</c>.</summary>
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

    /// <summary>housecarl_validate_scripts' <c>findings=</c>: <c>unbound_object</c> / <c>unbound_scalar</c> /
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
    /// <paramref name="claim"/> — the sentence saying the counts above are a SUBSET of what their labels name. Null when
    /// nothing was narrowed at all.
    ///
    /// <para>The claim is the caller's call, not automatic, because narrowing a sweep and narrowing its COUNTS are
    /// different things (PR #288 re-review, finding 3). A <c>findings=</c> class filter narrows which findings are
    /// counted but leaves every reported number COMPLETE for what it names — an excluded class already renders
    /// "NOT CHECKED" — so <c>check_errors findings=["dangling"]</c> with no record scope reports the true whole-order
    /// dangling total, and appending "not the whole plugin(s)" to it would be false. A record scope, or
    /// <c>property_contains=</c>, genuinely does make each count a subset of its own label, and there the claim belongs.
    /// Pass <see cref="ScopedCountsClaim"/> for the default wording, a bespoke string where some count is exempt
    /// (check_errors' plugin-level master count — round-1 finding 3), or null for no claim.</para></summary>
    public static string? FilterNote(string? claim, params string?[] clauses)
    {
        var kept = clauses.Where(c => !string.IsNullOrEmpty(c)).ToList();
        if (kept.Count == 0) return null;
        var prefix = "NARROWED to " + string.Join(" · ", kept);
        return claim is null ? prefix + "." : prefix + " — " + claim;
    }

    static string Normalize(string? s) => (s ?? "").Trim().Replace('-', '_').ToLowerInvariant();

    /// <summary>Order a <c>counts_only=true</c> histogram for display: commonest first, ties broken by key so the output
    /// is stable across runs (a before/after diff of two histograms is the motivating use — an unstable order would
    /// make every run diff).</summary>
    public static List<SweepCount> Histogram(Dictionary<string, int> acc)
        => acc.Select(kv => new SweepCount(kv.Key, kv.Value))
              .OrderByDescending(c => c.Count)
              .ThenBy(c => c.Key, StringComparer.OrdinalIgnoreCase)
              .ToList();
}
