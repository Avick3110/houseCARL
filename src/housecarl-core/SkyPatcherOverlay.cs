using System.Globalization;
using System.Reflection;
using Mutagen.Bethesda.Plugins;

namespace HousecarlCore;

/// <summary>
/// The SkyPatcher OVERLAY engine (Wave 1 of the distributor subsystem; plan
/// dev/plans/SKYPATCHER_DISTRIBUTOR_TOOL_PLAN_2026-07-08.md): replay an ordered union of parsed
/// SkyPatcher INI lines onto a MUTABLE COPY of one record and report the true post-patch state.
///
/// <para><b>Apply-order replay, not last-write-wins (plan §5.3).</b> Lines apply in the caller-supplied
/// order (filename sort <c>0</c>→<c>z</c> within the type folder — discovery's job). A same-field
/// <c>set</c> later in the order overwrites an earlier one <i>by construction</i> (each op mutates the
/// running copy); <c>…Mult</c>/<c>…ToAdd</c> read the RUNNING value (which may already be the product
/// of an earlier INI) and so accumulate exactly like the DLL; collection add/remove accumulate on the
/// running list. There is no separate "final write" model to get wrong.</para>
///
/// <para><b>Tiered honesty (plan §2.3, Q3).</b> CLEAN/COLLECTION ops resolve to a post-state value.
/// HARD ops are returned as <see cref="SkyPatcherDirective"/>s — the directive text plus WHY it has no
/// static resolution (runtime math / non-deterministic / copy-from-form) — never a silently-wrong
/// value. Unknown keys, unmapped ops, unevaluable filters, and value failures are all NAMED warnings.</para>
///
/// <para><b>Filter tier (Wave 1).</b> Evaluated here: the primary filter (FormID/EditorID membership,
/// + Excluded), <c>filterByKeywords</c>(/Or/Excluded) against the RUNNING copy's keyword list,
/// <c>filterByEditorIdContains</c>/<c>filterByNameContains</c> families, <c>hasPlugins</c>(/Or), and
/// the explicit <c>noFilter…</c> tokens. Every other filter family (restrictTo…, override-aware,
/// record-specific) is NOT yet evaluated: a line carrying one is SKIPPED LOUD as filter-unresolved
/// (never guessed either way). Wave 2 completes the filter surface.</para>
///
/// <para>Navigation and mutation ride the PROVEN engines — <see cref="WriteEngine.ApplyVerb"/> for
/// sets/adds/removes (same coercion, same materialization) and <see cref="ReadEngine.ReadLeaf"/> for
/// before/after tokens — so field addressing cannot drift from the read/write surface (plan §3.2.3).</para>
/// </summary>
public static class SkyPatcherOverlay
{
    /// <summary>Everything the overlay needs from the load order, kept behind an interface so the
    /// engine itself stays testable off fixtures (the service implements this over the live resolver).</summary>
    public interface IFormResolver
    {
        /// <summary>Resolve a bare EditorID to its winning FormKey, scoped to the Mutagen type when given
        /// (corpus catalog name, e.g. "Keyword"). Null = not found (the caller surfaces it loud).</summary>
        FormKey? ResolveEditorId(string editorId, string? mutagenType);

        /// <summary>Read one leaf token off the load-order WINNER of another record (the modelPath donor
        /// copy). Null = unresolvable (surfaced loud).</summary>
        string? ReadWinnerLeaf(FormKey donor, string path);

        /// <summary>The keyword FormKeys attached to a record's load-order winner (removeByKeyword's
        /// per-entry lookup). Null = unresolvable (surfaced loud, entry NOT removed).</summary>
        IReadOnlyList<FormKey>? KeywordsOf(FormKey record);

        /// <summary>Whether a plugin (filename incl. extension) is in the active load order (hasPlugins).</summary>
        bool PluginPresent(string pluginName);
    }

    /// <summary>One parsed line in its apply-order context: which file (Data-relative), which physical
    /// line, and the parsed form. The caller (discovery) supplies these already ORDERED.</summary>
    public sealed record OrderedLine(string File, int LineNumber, SkyPatcherLine Parsed);

    /// <summary>One resolved field change: op + raw value, the Mutagen field it landed on, and the
    /// before/after leaf tokens (before == after ⇒ a visible no-op, e.g. re-adding a present keyword).</summary>
    public sealed record SkyPatcherAppliedOp(string File, int LineNumber, string Op, string RawValue,
        string FieldPath, string? Before, string? After, string? Note);

    /// <summary>One HARD op that applies to this record but has no static resolution — the directive,
    /// rendered honest (plan §2.3). <see cref="Reason"/> names why (from the catalog note / shape).</summary>
    public sealed record SkyPatcherDirective(string File, int LineNumber, string Op, string RawValue, string Reason);

    /// <summary>The overlay outcome for one record: what applied (in order), what stayed a directive,
    /// and every warning (unknown keys, unmapped ops, filter-unresolved skips, value failures).</summary>
    public sealed record SkyPatcherOverlayResult(
        IReadOnlyList<SkyPatcherAppliedOp> Applied,
        IReadOnlyList<SkyPatcherDirective> Directives,
        IReadOnlyList<string> Warnings,
        int LinesMatched,
        int LinesSkippedUnresolvedFilter);

    // ======================================================================
    //  ENTRY — replay the ordered lines onto one record copy.
    // ======================================================================

    /// <summary>
    /// Replay <paramref name="lines"/> (already in apply order) onto <paramref name="mutableRecord"/> —
    /// a deep mutable copy of the record's load-order winner, identified by <paramref name="fk"/> /
    /// <paramref name="editorId"/>. Never throws for content reasons: every per-line/per-op failure is
    /// captured as a warning and the replay continues (one bad line must not hide the rest — Q3).
    /// </summary>
    public static SkyPatcherOverlayResult Apply(
        object mutableRecord, FormKey fk, string? editorId,
        SkyPatcherCatalog catalog, SkyPatcherRecordCatalog recordCatalog, RecordMap? fieldMap,
        IEnumerable<OrderedLine> lines, IFormResolver resolver)
    {
        var applied = new List<SkyPatcherAppliedOp>();
        var directives = new List<SkyPatcherDirective>();
        var warnings = new List<string>();
        var dedupe = new HashSet<string>(StringComparer.Ordinal);   // layer-fact warnings (e.g. a keyword not in the order) fire once, not per line
        int matched = 0, unresolvedSkips = 0;

        foreach (var line in lines)
        {
            if (line.Parsed.Kind != SkyPatcherLineKind.Patch) continue;
            var where = $"{line.File}:{line.LineNumber}";
            if (line.Parsed.Note is { } parseNote)
                warnings.Add($"{where}: parse note — {parseNote}");

            // ---- split the line into filters and ops, classifying every key (unknowns are loud). ----
            var filters = new List<(SkyPatcherSegment seg, SkyPatcherKeyClass cls)>();
            var ops = new List<(SkyPatcherSegment seg, SkyPatcherKeyClass cls)>();
            bool unknownKey = false;
            foreach (var seg in line.Parsed.Segments)
            {
                var cls = catalog.Classify(recordCatalog, seg.Key);
                switch (cls.Role)
                {
                    case SkyPatcherKeyRole.Filter: filters.Add((seg, cls)); break;
                    case SkyPatcherKeyRole.Operation: ops.Add((seg, cls)); break;
                    default: unknownKey = true; break;
                }
            }
            // An unknown key poisons the WHOLE line (Wave-1 crux finding): if it was a filter we failed to
            // recognize, evaluating the remaining segments would mis-scope the line — worst case an
            // unknown ONLY-filter leaves the filter list empty and the ops apply to EVERY record of the
            // type (a silently-over-applied post-state). Skip the line LOUD instead; never guess (Q3).
            if (unknownKey)
            {
                unresolvedSkips++;
                var bad = string.Join(", ", line.Parsed.Segments
                    .Where(s => catalog.Classify(recordCatalog, s.Key).Role == SkyPatcherKeyRole.Unknown)
                    .Select(s => $"'{s.Key}'"));
                warnings.Add($"{where}: line skipped — key(s) {bad} are not in the SkyPatcher reference for record type '{recordCatalog.RecordType}' (an unrecognized key may be a filter, so whether the line applies is UNRESOLVED; verify the spelling or the reference version).");
                continue;
            }
            if (ops.Count == 0) continue;   // a line with no operation does nothing (grammar §3)

            // ---- evaluate the filters against THIS record (Wave-1 tier; unsupported ⇒ loud skip). ----
            var verdict = EvaluateFilters(mutableRecord, fk, editorId, recordCatalog, fieldMap?.RecordType, filters, resolver, warnings, dedupe);
            if (verdict == FilterVerdict.Unresolved)
            {
                unresolvedSkips++;
                var names = string.Join(", ", filters.Select(f => f.seg.Key));
                warnings.Add($"{where}: line skipped — carries filter(s) this reader does not evaluate yet ({names}); whether it applies to {Ident(fk, editorId)} is UNRESOLVED (Wave-2 filter surface).");
                continue;
            }
            if (verdict == FilterVerdict.NoMatch) continue;
            matched++;

            // ---- apply each op, in segment order, onto the running copy. ----
            foreach (var (seg, cls) in ops)
            {
                var op = cls.Operation!;
                if (op.Tractability == SkyPatcherTractability.Hard)
                {
                    directives.Add(new SkyPatcherDirective(line.File, line.LineNumber, seg.Key, seg.RawValue ?? "",
                        HardReason(op)));
                    continue;
                }
                var map = fieldMap?.Ops.GetValueOrDefault(seg.Key);
                if (fieldMap is null || map is null)
                {
                    warnings.Add($"{where}: op '{seg.Key}' is {op.Tractability} but has no field mapping{(fieldMap is null ? $" (record type '{recordCatalog.RecordType}' has no field map yet)" : "")} — post-state NOT computed for it (named gap, never a guess).");
                    continue;
                }
                if (map.IsUnmapped)
                {
                    warnings.Add($"{where}: op '{seg.Key}' is explicitly unmapped — {map.Unmapped}");
                    continue;
                }
                try { ApplyOp(mutableRecord, fieldMap, seg, map, line, resolver, applied, warnings); }
                catch (Exception ex)
                {
                    warnings.Add($"{where}: op '{seg.Key}={seg.RawValue}' failed to apply — {Concise(ex)} (post-state does not include it).");
                }
            }
        }

        return new SkyPatcherOverlayResult(applied, directives, warnings, matched, unresolvedSkips);
    }

    static string Ident(FormKey fk, string? editorId) => editorId is null ? fk.ToString() : $"{fk} ({editorId})";

    static string HardReason(SkyPatcherOpDef op) => op.Shape switch
    {
        SkyPatcherOpShape.Mirror => $"copy-from-form — the value comes from another record's fields at apply time{NoteSuffix(op)}",
        SkyPatcherOpShape.Compound => $"compound/runtime op — no single static field value{NoteSuffix(op)}",
        _ => $"runtime-derived — the engine computes the result at load{NoteSuffix(op)}",
    };
    static string NoteSuffix(SkyPatcherOpDef op) => op.Note is { Length: > 0 } n ? $" ({n})" : "";

    // ======================================================================
    //  FILTERS (Wave-1 tier)
    // ======================================================================

    enum FilterVerdict { Match, NoMatch, Unresolved }

    static FilterVerdict EvaluateFilters(object record, FormKey fk, string? editorId,
        SkyPatcherRecordCatalog recordCatalog, string? mutagenRecordType,
        IReadOnlyList<(SkyPatcherSegment seg, SkyPatcherKeyClass cls)> filters, IFormResolver resolver,
        List<string> warnings, HashSet<string> dedupe)
    {
        // No filter set → every record of the type is patched (grammar §5).
        if (filters.Count == 0) return FilterVerdict.Match;

        bool any = false;
        foreach (var (seg, cls) in filters)
        {
            var f = cls.Filter!;
            var conn = cls.Connective ?? "";
            bool primary = f.Kind == SkyPatcherFilterKind.Primary;

            if (f.Kind == SkyPatcherFilterKind.NoFilter)
            {
                // The explicit apply-all tokens are RECORD-CLASS scoped in the shared leveledList folder:
                // noFilterLL means "every ITEM list" (LVLI), noFilterLLNPC "every CHARACTER list" (LVLN) —
                // review finding #3: without this, an LLNPC-only line silently patched item lists too.
                var required = cls.BaseKey.EndsWith("LLNPC", StringComparison.OrdinalIgnoreCase) ? "LeveledNpc"
                    : cls.BaseKey.EndsWith("LL", StringComparison.OrdinalIgnoreCase) ? "LeveledItem"
                    : null;
                if (required is not null && !required.Equals(mutagenRecordType, StringComparison.OrdinalIgnoreCase))
                    return FilterVerdict.NoMatch;
                any = true; continue;
            }

            if (f.Kind == SkyPatcherFilterKind.HasPlugins)
            {
                var plugins = seg.Values.Select(v => v.Raw).ToList();
                bool ok = conn == "Or" ? plugins.Any(resolver.PluginPresent) : plugins.All(resolver.PluginPresent);
                if (!ok) return FilterVerdict.NoMatch;
                any = true; continue;
            }

            if (primary)
            {
                bool inSet = seg.Values.Any(v => MatchesIdentity(v, fk, editorId));
                bool ok = conn is "Excluded" or "Exclude" ? !inSet : inSet;
                if (!ok) return FilterVerdict.NoMatch;
                any = true; continue;
            }

            switch (cls.BaseKey)
            {
                case "filterByKeywords":
                {
                    var mine = RecordKeywords(record);
                    if (mine is null) return FilterVerdict.Unresolved;   // type has no keyword list we can read
                    var wanted = new List<FormKey>();
                    int unresolved = 0;
                    foreach (var v in seg.Values)
                    {
                        var k = ResolveFormValue(v, "Keyword", resolver);
                        if (k is null)
                        {
                            // A keyword that resolves to NOTHING in the active order can never be attached
                            // to any record — that's a fact about the order, not a guess: it evaluates as
                            // not-attached, surfaced ONCE per token (Wave-1 crux finding: real INIs list
                            // keywords from frameworks the modlist doesn't run, e.g. SLA_KillerHeels).
                            unresolved++;
                            if (dedupe.Add($"kw:{v.Raw}"))
                                warnings.Add($"keyword '{v.Raw}' (in a {cls.BaseKey}{conn}) resolves to nothing in the active order — treated as attached to no record.");
                        }
                        else wanted.Add(k.Value);
                    }
                    bool ok = conn switch
                    {
                        "Or" => wanted.Any(mine.Contains),
                        "Excluded" or "Exclude" => !wanted.Any(mine.Contains),
                        // bare = AND: EVERY listed keyword must be attached — an in-order-nonexistent one can't be.
                        _ => unresolved == 0 && wanted.All(mine.Contains),
                    };
                    if (!ok) return FilterVerdict.NoMatch;
                    any = true; continue;
                }
                case "filterByEditorIdContains":
                {
                    var eid = editorId ?? "";
                    bool ok = ContainsVerdict(seg, conn, eid);
                    if (!ok) return FilterVerdict.NoMatch;
                    any = true; continue;
                }
                case "filterByNameContains":
                {
                    var name = RecordName(record) ?? "";
                    bool ok = ContainsVerdict(seg, conn, name);
                    if (!ok) return FilterVerdict.NoMatch;
                    any = true; continue;
                }
                default:
                    return FilterVerdict.Unresolved;   // a filter family Wave 1 does not evaluate — loud skip upstream
            }
        }
        return any ? FilterVerdict.Match : FilterVerdict.NoMatch;
    }

    static bool ContainsVerdict(SkyPatcherSegment seg, string conn, string haystack)
    {
        bool Has(string needle) => haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
        var needles = seg.Values.Select(v => v.Raw).ToList();
        return conn switch
        {
            "Or" => needles.Any(Has),
            "Excluded" or "Exclude" => !needles.Any(Has),
            _ => needles.All(Has),
        };
    }

    static bool MatchesIdentity(SkyPatcherValue v, FormKey fk, string? editorId)
    {
        if (v.Address is { IsFormId: true } a)
            return TryFormKey(a, out var key) && key == fk;
        return editorId is not null && string.Equals(v.Raw, editorId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>SkyPatcher <c>Plugin|FormID</c> → a Mutagen FormKey. A full load-indexed ESL FormID
    /// (<c>FExxxYYY</c> — the grammar reference documents the full xEdit copy as always legal) keeps only
    /// its 12-bit local id; anything else keeps the low 24 bits (leading load-order digits trimmable).
    /// Review finding #4: the bare 24-bit mask made every full ESL FormID silently match nothing.
    /// Residual EMPIRICAL item: a bare 6-hex ESL spelling like <c>800123</c> is inherently ambiguous
    /// (documented in the plan's Wave-1 list) — the 24-bit mask applies to it.</summary>
    static bool TryFormKey(FormAddress a, out FormKey fk)
    {
        fk = default;
        if (a.Plugin is null || a.FormId is null) return false;
        if (!ModKey.TryFromNameAndExtension(a.Plugin, out var mk)) return false;
        var id = a.FormId.Value;
        id = (id & 0xFF000000) == 0xFE000000 ? id & 0xFFF : id & 0xFFFFFF;
        fk = new FormKey(mk, id);
        return true;
    }

    // ======================================================================
    //  OPS
    // ======================================================================

    static void ApplyOp(object record, RecordMap fieldMap, SkyPatcherSegment seg, OpMap map,
        OrderedLine line, IFormResolver resolver,
        List<SkyPatcherAppliedOp> applied, List<string> warnings)
    {
        var where = $"{line.File}:{line.LineNumber}";
        var segs = SplitPath(map.Path);

        switch (map.Semantic)
        {
            case SkyPatcherOpSemantic.Set:
            {
                // 'attackDamage=' (empty value) must be LOUD like every sibling semantic — ParseValueList
                // yields zero items for it, and iterating zero times was the one silent no-op path (review
                // finding #8).
                if (seg.Values.Count == 0)
                { warnings.Add($"{where}: '{seg.Key}=' has no value; skipped."); break; }
                foreach (var v in seg.Values)   // most set-ops take one value; tolerate a list by applying in order
                    ApplySetOne(record, fieldMap, seg.Key, map, segs, v, line, resolver, applied, warnings);
                break;
            }
            case SkyPatcherOpSemantic.Mult:
            case SkyPatcherOpSemantic.AddNumeric:
            {
                var raw = seg.Values.Count > 0 ? seg.Values[0].Raw : "";
                if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var operand))
                { warnings.Add($"{where}: '{seg.Key}={raw}' — not a number; skipped."); return; }
                var before = LeafToken(record, segs);
                if (before is null || !double.TryParse(before, NumberStyles.Float, CultureInfo.InvariantCulture, out var current))
                { warnings.Add($"{where}: '{seg.Key}' — current value of '{map.Path}' is not numeric ('{before ?? "<unreadable>"}'); skipped."); return; }
                double result = map.Semantic == SkyPatcherOpSemantic.Mult ? current * operand : current + operand;
                var token = FormatNumericFor(record, segs, result);
                WriteEngine.ApplyVerb(record, new WriteRequest { RecordType = fieldMap.RecordType, Path = segs, Verb = "Set", Value = token });
                applied.Add(new SkyPatcherAppliedOp(line.File, line.LineNumber, seg.Key, raw, map.Path, before, LeafToken(record, segs),
                    map.Semantic == SkyPatcherOpSemantic.Mult ? $"stateful: {before} × {raw}" : $"stateful: {before} + {raw}"));
                break;
            }
            case SkyPatcherOpSemantic.SetFromOwnField:
            {
                var srcSegs = SplitPath(map.SourcePath ?? throw new InvalidOperationException($"'{seg.Key}' mapping has no sourcePath"));
                var src = LeafToken(record, srcSegs);
                if (src is null) { warnings.Add($"{where}: '{seg.Key}' — source field '{map.SourcePath}' unreadable; skipped."); return; }
                var before = LeafToken(record, segs);
                WriteEngine.ApplyVerb(record, new WriteRequest { RecordType = fieldMap.RecordType, Path = segs, Verb = "Set", Value = src });
                applied.Add(new SkyPatcherAppliedOp(line.File, line.LineNumber, seg.Key, seg.RawValue ?? "", map.Path, before, LeafToken(record, segs),
                    $"self-copy from {map.SourcePath} (order-dependent)"));
                break;
            }
            case SkyPatcherOpSemantic.VecComponent:
            {
                // One component of a P3* point. ReadEngine renders a point as its ctor-order components
                // ("x,y,z") and WriteEngine coerces the same form back, so the edit is a token splice +
                // an engine Set of the whole value — no hand-rolled struct rebuild to drift.
                var raw = seg.Values.Count > 0 ? seg.Values[0].Raw : "";
                if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                { warnings.Add($"{where}: '{seg.Key}={raw}' — not a number; skipped."); return; }
                var comp = map.Component ?? throw new InvalidOperationException($"'{seg.Key}' mapping has no component");
                var before = LeafToken(record, segs);
                var parts = before?.Split(',');
                if (parts is null || comp >= parts.Length)
                { warnings.Add($"{where}: '{seg.Key}' — '{map.Path}' is absent or not a {comp + 1}+-component point ('{before ?? "<unreadable>"}'); skipped."); return; }
                parts[comp] = raw.Trim();
                WriteEngine.ApplyVerb(record, new WriteRequest { RecordType = fieldMap.RecordType, Path = segs, Verb = "Set", Value = string.Join(",", parts) });
                applied.Add(new SkyPatcherAppliedOp(line.File, line.LineNumber, seg.Key, raw, $"{map.Path}.{"XYZ"[comp]}", before, LeafToken(record, segs), null));
                break;
            }
            case SkyPatcherOpSemantic.ModelPath:
            {
                var v = seg.Values.Count > 0 ? seg.Values[0] : null;
                if (v is null) { warnings.Add($"{where}: '{seg.Key}' has no value; skipped."); return; }
                string? pathToken;
                string? note = null;
                bool looksLikePath = v.Raw.Contains('.') || v.Raw.Contains('\\') || v.Raw.Contains('/');
                if (v.Address is { IsFormId: true } || !looksLikePath)
                {
                    // A form address, or a bare identifier (EditorID) — either way the value is a DONOR form.
                    // A dot-less token can never be a valid .nif path, so an unresolvable one must fail LOUD
                    // here, never fall through to the literal branch and be written verbatim as a
                    // "successful" model path (review finding #6).
                    var donor = ResolveFormValue(v, map.FormType, resolver);
                    if (donor is null) { warnings.Add($"{where}: '{seg.Key}={v.Raw}' — donor form not resolvable (and '{v.Raw}' is not a model path); skipped."); return; }
                    pathToken = resolver.ReadWinnerLeaf(donor.Value, map.Path);
                    if (pathToken is null) { warnings.Add($"{where}: '{seg.Key}={v.Raw}' — donor {donor.Value}'s '{map.Path}' unreadable; skipped."); return; }
                    note = $"model path copied from donor {donor.Value}";
                }
                else pathToken = v.Raw;   // a literal .nif path
                var before = LeafToken(record, segs);
                WriteEngine.ApplyVerb(record, new WriteRequest { RecordType = fieldMap.RecordType, Path = segs, Verb = "Set", Value = pathToken });
                applied.Add(new SkyPatcherAppliedOp(line.File, line.LineNumber, seg.Key, v.Raw, map.Path, before, LeafToken(record, segs), note));
                break;
            }
            case SkyPatcherOpSemantic.FlagsSet:
            case SkyPatcherOpSemantic.FlagsRemove:
            {
                var (parent, leaf) = Navigate(record, segs);
                if (!leaf.PropertyType.IsEnum) throw new InvalidOperationException($"'{map.Path}' is not a flags enum");
                var before = LeafToken(record, segs);
                ulong bits = Convert.ToUInt64(leaf.GetValue(parent) ?? 0UL, CultureInfo.InvariantCulture);
                foreach (var v in seg.Values)
                {
                    var token = map.ValueMap?.GetValueOrDefault(v.Raw) ?? v.Raw;
                    if (!TryParseEnumMember(leaf.PropertyType, token, out var bit))
                    { warnings.Add($"{where}: '{seg.Key}' — flag '{v.Raw}' is not a {leaf.PropertyType.Name} member (no valueMap match either); that flag skipped."); continue; }
                    bits = map.Semantic == SkyPatcherOpSemantic.FlagsSet ? bits | bit : bits & ~bit;
                }
                SetEnumBits(record, fieldMap, segs, bits);
                applied.Add(new SkyPatcherAppliedOp(line.File, line.LineNumber, seg.Key, seg.RawValue ?? "", map.Path, before, LeafToken(record, segs), null));
                break;
            }
            case SkyPatcherOpSemantic.FlagBool:
            {
                var raw = seg.Values.Count > 0 ? seg.Values[0].Raw : "";
                // 'none' is a LEGAL token on these ops (grammar: setEssential/setProtected/… — "none = leave
                // unchanged"): a visible no-op, not the "not a boolean" warning it used to draw.
                if (raw.Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    var cur = LeafToken(record, segs);
                    applied.Add(new SkyPatcherAppliedOp(line.File, line.LineNumber, seg.Key, raw, map.Path, cur, cur, "none — leave unchanged"));
                    return;
                }
                bool on = raw.Equals("true", StringComparison.OrdinalIgnoreCase) || raw.Equals("yes", StringComparison.OrdinalIgnoreCase) || raw == "1";
                bool off = raw.Equals("false", StringComparison.OrdinalIgnoreCase) || raw.Equals("no", StringComparison.OrdinalIgnoreCase) || raw == "0";
                if (!on && !off) { warnings.Add($"{where}: '{seg.Key}={raw}' — not a boolean; skipped."); return; }
                var (parent, leaf) = Navigate(record, segs);
                var flagToken = map.Flag ?? throw new InvalidOperationException($"'{seg.Key}' mapping has no flag");
                if (!TryParseEnumMember(leaf.PropertyType, flagToken, out var bit))
                    throw new InvalidOperationException($"flag '{flagToken}' is not a {leaf.PropertyType.Name} member");
                var before = LeafToken(record, segs);
                ulong bits = Convert.ToUInt64(leaf.GetValue(parent) ?? 0UL, CultureInfo.InvariantCulture);
                bits = on ? bits | bit : bits & ~bit;
                SetEnumBits(record, fieldMap, segs, bits);
                applied.Add(new SkyPatcherAppliedOp(line.File, line.LineNumber, seg.Key, raw, $"{map.Path} ({flagToken})", before, LeafToken(record, segs), map.Note));
                break;
            }
            case SkyPatcherOpSemantic.AddForm:
            case SkyPatcherOpSemantic.RemoveForm:
            {
                foreach (var v in seg.Values)
                {
                    var key = ResolveFormValue(v, map.FormType, resolver);
                    if (key is null) { warnings.Add($"{where}: '{seg.Key}={v.Raw}' — form not resolvable ({FormHint(v, map)}); that item skipped."); continue; }
                    var list = FormLinkList(record, segs) ?? throw new InvalidOperationException($"'{map.Path}' is not a formlink list");
                    bool present = list.Contains(key.Value);
                    string note;
                    if (map.Semantic == SkyPatcherOpSemantic.AddForm)
                    {
                        if (present) note = "already present — no change";
                        else { WriteEngine.ApplyVerb(record, new WriteRequest { RecordType = fieldMap.RecordType, Path = segs, Verb = "Add", Value = key.Value.ToString() }); note = "added"; }
                    }
                    else
                    {
                        if (!present) note = "not present — no change";
                        else { WriteEngine.ApplyVerb(record, new WriteRequest { RecordType = fieldMap.RecordType, Path = segs, Verb = "Remove", Value = key.Value.ToString() }); note = "removed"; }
                    }
                    bool now = FormLinkList(record, segs)!.Contains(key.Value);
                    applied.Add(new SkyPatcherAppliedOp(line.File, line.LineNumber, seg.Key, v.Raw, map.Path,
                        $"contains={(present ? "true" : "false")}", $"contains={(now ? "true" : "false")}", note));
                }
                break;
            }
            case SkyPatcherOpSemantic.ReplaceForm:
            {
                foreach (var v in seg.Values)
                {
                    var (aTok, bTok) = SplitPair(v, map.EqPacked);
                    if (aTok is null || bTok is null) { warnings.Add($"{where}: '{seg.Key}={v.Raw}' — expected two packed forms; skipped."); continue; }
                    var a = ResolveFormToken(aTok, map.FormType, resolver);
                    var b = ResolveFormToken(bTok, map.FormType, resolver);
                    if (a is null || b is null) { warnings.Add($"{where}: '{seg.Key}={v.Raw}' — form(s) not resolvable; skipped."); continue; }
                    int n = ReplaceInFormLinkList(record, fieldMap.RecordType, segs, a.Value, b.Value);
                    applied.Add(new SkyPatcherAppliedOp(line.File, line.LineNumber, seg.Key, v.Raw, map.Path, null, null,
                        n > 0 ? $"replaced {n} occurrence(s)" : "form A not present — no change"));
                }
                break;
            }
            case SkyPatcherOpSemantic.ClearList:
            {
                var raw = seg.Values.Count > 0 ? seg.Values[0].Raw : "true";
                if (!raw.Equals("true", StringComparison.OrdinalIgnoreCase) && !raw.Equals("yes", StringComparison.OrdinalIgnoreCase))
                { warnings.Add($"{where}: '{seg.Key}={raw}' — clear expects true/yes; skipped."); return; }
                var (parent, leaf) = Navigate(record, segs);
                var coll = leaf.GetValue(parent);
                int had = coll is null ? 0 : CountOf(coll);
                // Clear = the engine's ReplaceAll with no values (same clear the verb surface exposes).
                if (coll is not null)
                    WriteEngine.ApplyVerb(record, new WriteRequest { RecordType = fieldMap.RecordType, Path = segs, Verb = "ReplaceAll" });
                applied.Add(new SkyPatcherAppliedOp(line.File, line.LineNumber, seg.Key, raw, map.Path, $"{had} entr(ies)", "0 entries", "cleared"));
                break;
            }
            case SkyPatcherOpSemantic.AddEntry:
            case SkyPatcherOpSemantic.AddEntryOnce:
            case SkyPatcherOpSemantic.RemoveEntry:
            case SkyPatcherOpSemantic.RemoveEntryByCount:
            case SkyPatcherOpSemantic.ReplaceEntry:
            case SkyPatcherOpSemantic.MultCount:
            case SkyPatcherOpSemantic.RemoveByKeyword:
                ApplyEntryOp(record, fieldMap, seg, map, line, resolver, applied, warnings);
                break;

            default:
                warnings.Add($"{where}: op '{seg.Key}' — semantic {map.Semantic} not implemented; named gap.");
                break;
        }
    }

    static void ApplySetOne(object record, RecordMap fieldMap, string opKey, OpMap map, string[] segs,
        SkyPatcherValue v, OrderedLine line, IFormResolver resolver,
        List<SkyPatcherAppliedOp> applied, List<string> warnings)
    {
        var where = $"{line.File}:{line.LineNumber}";
        var before = LeafToken(record, segs);

        // null → clear the (form) field. Routed through the engine's Remove (FormLink-aware clear);
        // a required link refuses LOUD there and we surface it as a warning.
        if (!v.IsNameLiteral && v.Raw.Equals("null", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                WriteEngine.ApplyVerb(record, new WriteRequest { RecordType = fieldMap.RecordType, Path = segs, Verb = "Remove" });
                applied.Add(new SkyPatcherAppliedOp(line.File, line.LineNumber, opKey, v.Raw, map.Path, before, LeafToken(record, segs), "cleared"));
            }
            catch (Exception ex) { warnings.Add($"{where}: '{opKey}=null' — {Concise(ex)}"); }
            return;
        }

        string token;
        if (v.IsNameLiteral) token = v.NameText!;                       // rename literal, wrapper stripped
        else if (v.Address is { IsFormId: true } a && TryFormKey(a, out var fk1)) token = fk1.ToString();
        else if (map.ValueMap is { } vm && vm.TryGetValue(v.Raw, out var mapped)) token = mapped;
        else if (map.FormType is not null && ResolveFormValue(v, map.FormType, resolver) is { } rk) token = rk.ToString();
        else token = v.Raw;                                             // scalar / enum member (ignore-case coercion downstream)

        WriteEngine.ApplyVerb(record, new WriteRequest { RecordType = fieldMap.RecordType, Path = segs, Verb = "Set", Value = token });
        applied.Add(new SkyPatcherAppliedOp(line.File, line.LineNumber, opKey, v.Raw, map.Path, before, LeafToken(record, segs), map.Note));
    }

    // ---- struct-entry collections (containers, inventories, LLs, factions, cobj items) --------------

    static void ApplyEntryOp(object record, RecordMap fieldMap, SkyPatcherSegment seg, OpMap map,
        OrderedLine line, IFormResolver resolver,
        List<SkyPatcherAppliedOp> applied, List<string> warnings)
    {
        var where = $"{line.File}:{line.LineNumber}";
        var el = map.Element ?? throw new InvalidOperationException($"'{seg.Key}' mapping has no element spec");
        var segs = SplitPath(map.Path);

        foreach (var v in seg.Values)
        {
            var args = UnpackArgs(v, map.EqPacked);

            switch (map.Semantic)
            {
                case SkyPatcherOpSemantic.AddEntry:
                case SkyPatcherOpSemantic.AddEntryOnce:
                {
                    var sets = new List<WriteRequest>();
                    FormKey? keyForm = null;
                    bool bad = false;
                    foreach (var f in el.Fields)
                    {
                        string? raw = f.Arg < args.Count ? args[f.Arg] : f.Default;
                        if (raw is null || raw.Equals("null", StringComparison.OrdinalIgnoreCase)) continue;
                        string valueToken = raw;
                        if (f.Path == el.KeyPath || LooksLikeForm(raw))
                        {
                            var k = ResolveFormToken(raw, f.Path == el.KeyPath ? map.FormType : null, resolver);
                            if (k is null && f.Path == el.KeyPath) { warnings.Add($"{where}: '{seg.Key}={v.Raw}' — entry form '{raw}' not resolvable; entry skipped."); bad = true; break; }
                            if (k is not null) { valueToken = k.Value.ToString(); if (f.Path == el.KeyPath) keyForm = k; }
                        }
                        sets.Add(new WriteRequest { RecordType = el.Type, Path = SplitPath(f.Path), Verb = "Set", Value = valueToken });
                    }
                    if (bad) continue;
                    if (map.Semantic == SkyPatcherOpSemantic.AddEntryOnce && keyForm is not null
                        && EntryIndicesByKey(record, segs, el, keyForm.Value).Count > 0)
                    {
                        applied.Add(new SkyPatcherAppliedOp(line.File, line.LineNumber, seg.Key, v.Raw, map.Path, null, null, "already present — addOnce is a no-op"));
                        continue;
                    }
                    WriteEngine.ApplyVerb(record, new WriteRequest
                    {
                        RecordType = fieldMap.RecordType, Path = segs, Verb = "Add",
                        Struct = new StructSpec { Type = el.Type, Sets = sets },
                    });
                    applied.Add(new SkyPatcherAppliedOp(line.File, line.LineNumber, seg.Key, v.Raw, map.Path, null, null, "entry added"));
                    break;
                }
                case SkyPatcherOpSemantic.RemoveEntry:
                {
                    // A conditional remove (form~level~count, with <,>,<=,>= operators / 'none' slots) is NOT
                    // modeled in Wave 1 — replaying it as an unconditional remove would be a silently-WRONG
                    // post-state (subagent review finding), so the whole item skips LOUD instead (Q3).
                    if (args.Count > 1)
                    {
                        warnings.Add($"{where}: '{seg.Key}={v.Raw}' — conditional/qualified removal (extra ~sub-args) is not modeled in Wave 1; this removal was NOT applied (named gap, never an unconditional guess).");
                        continue;
                    }
                    var k = ResolveFormToken(args[0], map.FormType, resolver);
                    if (k is null) { warnings.Add($"{where}: '{seg.Key}={v.Raw}' — form not resolvable; skipped."); continue; }
                    int n = RemoveEntriesByKey(record, fieldMap.RecordType, segs, el, k.Value);
                    applied.Add(new SkyPatcherAppliedOp(line.File, line.LineNumber, seg.Key, v.Raw, map.Path, null, null,
                        n > 0 ? $"removed {n} entr(ies)" : "no matching entry — no change"));
                    break;
                }
                case SkyPatcherOpSemantic.RemoveEntryByCount:
                {
                    var k = ResolveFormToken(args[0], map.FormType, resolver);
                    if (k is null || args.Count < 2 || !int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec))
                    { warnings.Add($"{where}: '{seg.Key}={v.Raw}' — expected form~count; skipped."); continue; }
                    var countPath = el.CountPath ?? throw new InvalidOperationException($"'{seg.Key}' element has no countPath");
                    int touched = AdjustEntryCounts(record, fieldMap.RecordType, segs, el, k.Value, countPath, c => c - dec);
                    applied.Add(new SkyPatcherAppliedOp(line.File, line.LineNumber, seg.Key, v.Raw, map.Path, null, null,
                        touched > 0 ? $"count reduced by {dec} on {touched} entr(ies) (entries at ≤0 removed)" : "no matching entry — no change"));
                    break;
                }
                case SkyPatcherOpSemantic.ReplaceEntry:
                {
                    var (aTok, bTok) = SplitPair(v, map.EqPacked);
                    var a = aTok is null ? null : ResolveFormToken(aTok, map.FormType, resolver);
                    var b = bTok is null ? null : ResolveFormToken(bTok, map.FormType, resolver);
                    if (a is null || b is null) { warnings.Add($"{where}: '{seg.Key}={v.Raw}' — expected formA{(map.EqPacked ? "=" : "~")}formB; skipped."); continue; }
                    int n = RetargetEntriesByKey(record, segs, el, a.Value, b.Value);
                    applied.Add(new SkyPatcherAppliedOp(line.File, line.LineNumber, seg.Key, v.Raw, map.Path, null, null,
                        n > 0 ? $"retargeted {n} entr(ies)" : "form A not present — no change"));
                    break;
                }
                case SkyPatcherOpSemantic.MultCount:
                {
                    var countPath = el.CountPath ?? throw new InvalidOperationException($"'{seg.Key}' element has no countPath");
                    FormKey? scope = null;
                    double mult;
                    if (args.Count >= 2)
                    {
                        scope = ResolveFormToken(args[0], map.FormType, resolver);
                        if (scope is null || !double.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out mult))
                        { warnings.Add($"{where}: '{seg.Key}={v.Raw}' — expected form~mult or mult; skipped."); continue; }
                    }
                    else if (!double.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out mult))
                    { warnings.Add($"{where}: '{seg.Key}={v.Raw}' — expected form~mult or mult; skipped."); continue; }
                    int touched = MultiplyEntryCounts(record, segs, el, scope, countPath, mult);
                    applied.Add(new SkyPatcherAppliedOp(line.File, line.LineNumber, seg.Key, v.Raw, map.Path, null, null,
                        $"counts ×{args[^1]} on {touched} entr(ies) (stateful)"));
                    break;
                }
                case SkyPatcherOpSemantic.RemoveByKeyword:
                {
                    var kw = ResolveFormToken(args[0], "Keyword", resolver);
                    if (kw is null) { warnings.Add($"{where}: '{seg.Key}={v.Raw}' — keyword not resolvable; skipped."); continue; }
                    var (removed, unresolved) = RemoveEntriesByTargetKeyword(record, fieldMap.RecordType, segs, el, kw.Value, resolver);
                    if (unresolved > 0)
                        warnings.Add($"{where}: '{seg.Key}={v.Raw}' — {unresolved} entr(ies) whose target record could not be resolved were LEFT IN PLACE (never removed on a guess).");
                    applied.Add(new SkyPatcherAppliedOp(line.File, line.LineNumber, seg.Key, v.Raw, map.Path, null, null,
                        removed > 0 ? $"removed {removed} entr(ies) by keyword" : "no entry carries the keyword — no change"));
                    break;
                }
            }
        }
    }

    // ======================================================================
    //  VALUE + NAVIGATION HELPERS
    // ======================================================================

    static string[] SplitPath(string path) => path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    static string? LeafToken(object record, string[] segs)
    {
        var r = ReadEngine.ReadLeaf(record, segs);
        return r.HasValue ? r.Token : null;
    }

    /// <summary>Navigate to the leaf's PARENT + PropertyInfo — READ access only (raw enum bits for the
    /// flag math, live collection handles for entry matching); every MUTATION goes back through
    /// <see cref="WriteEngine.ApplyVerb"/>. Rides the same ParseSegment/ResolveProperty/StepIntoElement
    /// walk as the engines.</summary>
    static (object parent, PropertyInfo leaf) Navigate(object record, string[] segs)
    {
        object current = record;
        for (int i = 0; i < segs.Length - 1; i++)
        {
            var (name, key) = WriteEngine.ParseSegment(segs[i]);
            var p = WriteEngine.ResolveProperty(current.GetType(), name)
                ?? throw new InvalidOperationException($"no property '{name}' on {current.GetType().Name}");
            current = (key is null ? p.GetValue(current) : WriteEngine.StepIntoElement(current, p, name, key))
                ?? throw new InvalidOperationException($"'{name}' is absent");
        }
        var (leafName, _) = WriteEngine.ParseSegment(segs[^1]);
        var leaf = WriteEngine.ResolveProperty(current.GetType(), leafName)
            ?? throw new InvalidOperationException($"no property '{leafName}' on {current.GetType().Name}");
        return (current, leaf);
    }

    /// <summary>Write a computed flag-bit value back through the verb engine: an enum leaf Set with the
    /// numeric token (Enum.Parse accepts it) — the ONE mutation path, same coercion as every other Set.</summary>
    static void SetEnumBits(object record, RecordMap fieldMap, string[] segs, ulong bits)
        => WriteEngine.ApplyVerb(record, new WriteRequest
        { RecordType = fieldMap.RecordType, Path = segs, Verb = "Set", Value = bits.ToString(CultureInfo.InvariantCulture) });

    /// <summary>Format a computed stateful result for the leaf's actual type: integral leaves round to
    /// nearest (how the DLL lands fractional mults on int fields is a Wave-1 EMPIRICAL item — round is
    /// the declared assumption, revisited at the gate), floats keep the fraction.</summary>
    static string FormatNumericFor(object record, string[] segs, double result)
    {
        var (parent, leaf) = Navigate(record, segs);
        var t = Nullable.GetUnderlyingType(leaf.PropertyType) ?? leaf.PropertyType;
        bool integral = t == typeof(byte) || t == typeof(sbyte) || t == typeof(short) || t == typeof(ushort)
                     || t == typeof(int) || t == typeof(uint) || t == typeof(long) || t == typeof(ulong);
        return integral
            ? Math.Round(result, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture)
            : result.ToString("R", CultureInfo.InvariantCulture);
    }

    static bool TryParseEnumMember(Type enumType, string token, out ulong bits)
    {
        bits = 0;
        if (!enumType.IsEnum) return false;
        foreach (var name in Enum.GetNames(enumType))
            if (name.Equals(token, StringComparison.OrdinalIgnoreCase))
            { bits = Convert.ToUInt64(Enum.Parse(enumType, name), CultureInfo.InvariantCulture); return true; }
        return false;
    }

    static FormKey? ResolveFormValue(SkyPatcherValue v, string? formType, IFormResolver resolver)
    {
        if (v.Address is { IsFormId: true } a) return TryFormKey(a, out var fk) ? fk : null;
        return v.IsNameLiteral ? null : resolver.ResolveEditorId(v.Raw, formType);
    }

    static FormKey? ResolveFormToken(string token, string? formType, IFormResolver resolver)
    {
        var a = SkyPatcherParse.TryParseAddress(token);
        if (a is { IsFormId: true }) return TryFormKey(a, out var fk) ? fk : null;
        return resolver.ResolveEditorId(token, formType);
    }

    /// <summary>A sub-arg that IS the unambiguous <c>Plugin|FormID</c> address form — the ONE recognizer
    /// (<see cref="SkyPatcherParse.TryParseAddress"/>), not a '|' sniff that also matched form=count packs.</summary>
    static bool LooksLikeForm(string raw) => SkyPatcherParse.TryParseAddress(raw) is { IsFormId: true };

    static string FormHint(SkyPatcherValue v, OpMap map)
        => v.Address is { IsFormId: true }
            ? "plugin name unparseable"
            : $"EditorID '{v.Raw}' not found{(map.FormType is null ? "" : $" among {map.FormType} winners")}";

    /// <summary>Sub-args of one comma-item: '='-packed ops split on the FIRST '=' (form=count), then each
    /// side contributes; otherwise the tokenizer's ~-split sub-args are used as-is.</summary>
    static IReadOnlyList<string> UnpackArgs(SkyPatcherValue v, bool eqPacked)
    {
        if (!eqPacked) return v.SubArgs;
        int eq = v.Raw.IndexOf('=');
        return eq < 0 ? new[] { v.Raw.Trim() } : new[] { v.Raw[..eq].Trim(), v.Raw[(eq + 1)..].Trim() };
    }

    static (string? a, string? b) SplitPair(SkyPatcherValue v, bool eqPacked)
    {
        var args = UnpackArgs(v, eqPacked);
        return args.Count >= 2 ? (args[0], args[1]) : (null, null);
    }

    // ---- direct list access (reflection over the running copy) --------------------------------------

    static int CountOf(object coll)
    {
        int n = 0;
        foreach (var _ in (System.Collections.IEnumerable)coll) n++;
        return n;
    }

    static System.Collections.IList? ListAt(object record, string[] segs)
    {
        var (parent, leaf) = Navigate(record, segs);
        return leaf.GetValue(parent) as System.Collections.IList;
    }

    /// <summary>A formlink list's FormKeys (null when the path isn't a formlink list / list is absent).</summary>
    static List<FormKey>? FormLinkList(object record, string[] segs)
    {
        var (parent, leaf) = Navigate(record, segs);
        if (leaf.GetValue(parent) is not System.Collections.IEnumerable list) return new List<FormKey>();   // absent list reads as empty
        var keys = new List<FormKey>();
        foreach (var item in list)
        {
            var fkProp = item?.GetType().GetProperty("FormKey");
            if (fkProp?.GetValue(item) is FormKey fk) keys.Add(fk);
            else return null;
        }
        return keys;
    }

    static int ReplaceInFormLinkList(object record, string recordType, string[] segs, FormKey a, FormKey b)
    {
        var keys = FormLinkList(record, segs);
        if (keys is null) return 0;
        int n = 0;
        for (int i = 0; i < keys.Count; i++)
        {
            if (keys[i] != a) continue;
            // Re-point the element through the engine's SetAtIndex — same formlink coercion as every Set.
            WriteEngine.ApplyVerb(record, new WriteRequest { RecordType = recordType, Path = segs, Verb = "SetAtIndex", Key = i.ToString(CultureInfo.InvariantCulture), Value = b.ToString() });
            n++;
        }
        return n;
    }

    /// <summary>Remove one list element by index through the engine (RemoveAt) — the ONE list-surgery path.</summary>
    static void RemoveListElementAt(object record, string recordType, string[] segs, int index)
        => WriteEngine.ApplyVerb(record, new WriteRequest
        { RecordType = recordType, Path = segs, Verb = "Remove", Key = index.ToString(CultureInfo.InvariantCulture) });

    static string? EntryKeyToken(object entry, ElementMap el)
        => el.KeyPath is null ? null : LeafToken(entry, SplitPath(el.KeyPath));

    static List<int> EntryIndicesByKey(object record, string[] segs, ElementMap el, FormKey key)
    {
        var hits = new List<int>();
        var list = ListAt(record, segs);
        if (list is null) return hits;
        var want = key.ToString();
        for (int i = 0; i < list.Count; i++)
            if (list[i] is { } entry && string.Equals(EntryKeyToken(entry, el), want, StringComparison.OrdinalIgnoreCase))
                hits.Add(i);
        return hits;
    }

    static int RemoveEntriesByKey(object record, string recordType, string[] segs, ElementMap el, FormKey key)
    {
        if (ListAt(record, segs) is null) return 0;
        var idx = EntryIndicesByKey(record, segs, el, key);
        for (int i = idx.Count - 1; i >= 0; i--) RemoveListElementAt(record, recordType, segs, idx[i]);
        return idx.Count;
    }

    static int RetargetEntriesByKey(object record, string[] segs, ElementMap el, FormKey a, FormKey b)
    {
        var list = ListAt(record, segs);
        if (list is null || el.KeyPath is null) return 0;
        var idx = EntryIndicesByKey(record, segs, el, a);
        foreach (var i in idx)
            WriteEngine.ApplyVerb(list[i]!, new WriteRequest { RecordType = el.Type, Path = SplitPath(el.KeyPath), Verb = "Set", Value = b.ToString() });
        return idx.Count;
    }

    static int AdjustEntryCounts(object record, string recordType, string[] segs, ElementMap el, FormKey key, string countPath, Func<int, int> f)
    {
        var list = ListAt(record, segs);
        if (list is null) return 0;
        var idx = EntryIndicesByKey(record, segs, el, key);
        var cSegs = SplitPath(countPath);
        foreach (var i in idx.AsEnumerable().Reverse())
        {
            var entry = list[i]!;
            var tok = LeafToken(entry, cSegs);
            if (tok is null || !int.TryParse(tok, NumberStyles.Integer, CultureInfo.InvariantCulture, out var c)) continue;
            int now = f(c);
            if (now <= 0) RemoveListElementAt(record, recordType, segs, i);
            else WriteEngine.ApplyVerb(entry, new WriteRequest { RecordType = el.Type, Path = cSegs, Verb = "Set", Value = now.ToString(CultureInfo.InvariantCulture) });
        }
        return idx.Count;
    }

    static int MultiplyEntryCounts(object record, string[] segs, ElementMap el, FormKey? scope, string countPath, double mult)
    {
        var list = ListAt(record, segs);
        if (list is null) return 0;
        var cSegs = SplitPath(countPath);
        var scopeTok = scope?.ToString();
        int touched = 0;
        foreach (var entry in list.Cast<object?>().Where(e => e is not null))
        {
            if (scopeTok is not null && !string.Equals(EntryKeyToken(entry!, el), scopeTok, StringComparison.OrdinalIgnoreCase)) continue;
            var tok = LeafToken(entry!, cSegs);
            if (tok is null || !double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out var c)) continue;
            var now = (int)Math.Round(c * mult, MidpointRounding.AwayFromZero);
            WriteEngine.ApplyVerb(entry!, new WriteRequest { RecordType = el.Type, Path = cSegs, Verb = "Set", Value = now.ToString(CultureInfo.InvariantCulture) });
            touched++;
        }
        return touched;
    }

    static (int removed, int unresolved) RemoveEntriesByTargetKeyword(object record, string recordType, string[] segs, ElementMap el, FormKey keyword, IFormResolver resolver)
    {
        var list = ListAt(record, segs);
        if (list is null || el.KeyPath is null) return (0, 0);
        int removed = 0, unresolved = 0;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var tok = list[i] is { } e ? EntryKeyToken(e, el) : null;
            if (tok is null || !FormKey.TryFactory(tok, out var target)) { unresolved++; continue; }
            var kws = resolver.KeywordsOf(target);
            if (kws is null) { unresolved++; continue; }
            if (kws.Contains(keyword)) { RemoveListElementAt(record, recordType, segs, i); removed++; }
        }
        return (removed, unresolved);
    }

    // ---- record-local reads for filters --------------------------------------------------------------

    static IReadOnlyList<FormKey>? RecordKeywords(object record) => ReadEngine.KeywordKeys(record);

    static string? RecordName(object record)
    {
        var p = record.GetType().GetProperty("Name");
        var v = p?.GetValue(record);
        return v?.ToString();
    }

    static string Concise(Exception ex)
    {
        var e = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
        return $"{e.GetType().Name}: {e.Message}";
    }
}
