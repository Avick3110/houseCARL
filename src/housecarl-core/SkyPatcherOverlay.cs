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
/// <para><b>Filter tier (Wave 2 — the COMPLETE surface).</b> Two lanes: (1) BUILT-IN families that
/// need no per-record path — the primary filter, <c>noFilter…</c>, <c>hasPlugins</c>, keyword /
/// EditorID-contains / name-contains, <c>filterByModNames</c> (the record's DEFINING master — a
/// declared assumption, empirical-gate item), the override-aware skip tokens
/// (<c>modNamesLastOverriddenExcluded</c> = the record's WINNING override plugin;
/// <c>skipRecordByModNameContains</c>), attached-mgef, and alternate-texture matching; (2) MAP-DRIVEN
/// per-record filters from the field map's <c>filters</c> specs (<see cref="FilterSpec"/> — form
/// equality/membership, enum/flag tests, substrings, biped slots, donor reads). A filter that is
/// neither built-in nor mapped, or that is explicitly unmapped-with-reason (e.g. NPC
/// <c>restrictToSkill</c> — autocalc NPCs have no static skill value), keeps the Wave-1 behavior:
/// the line SKIPS LOUD as filter-unresolved, never guessed either way. Per grammar §4 the PLAYER
/// matches only a lone bare primary filter that names it — every other line excludes the player.</para>
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

        /// <summary>The plugin (filename incl. extension) whose override of a record WINS the load
        /// order — the "last override" the override-aware filters yield to. Null = record not present
        /// (surfaced loud, filter unresolved).</summary>
        string? WinnerPluginOf(FormKey record);

        /// <summary>The EditorID of a record's load-order winner (filterByArmorAddons' documented
        /// EditorID-substring match). Null = unresolvable.</summary>
        string? EditorIdOf(FormKey record);
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
            var verdict = EvaluateFilters(mutableRecord, fk, editorId, recordCatalog, fieldMap, filters, resolver, warnings, dedupe);
            if (verdict == FilterVerdict.Unresolved)
            {
                unresolvedSkips++;
                var names = string.Join(", ", filters.Select(f => f.seg.Key));
                warnings.Add($"{where}: line skipped — carries filter(s) with no static evaluation ({names}); whether it applies to {Ident(fk, editorId)} is UNRESOLVED (see the per-filter warning for why).");
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

    /// <summary>The player actor — per grammar §4 ALWAYS excluded from filtered and unfiltered lines
    /// alike; only <c>filterByNpcs=Skyrim.esm|7</c> ALONE patches it.</summary>
    static readonly FormKey PlayerFormKey = new(new ModKey("Skyrim", ModType.Master), 0x7);

    /// <summary>The filter base names the overlay evaluates WITHOUT a field-map spec (no per-record
    /// path needed). Shared with the filtermap coverage guard so "built-in" can't silently drift.</summary>
    public static readonly IReadOnlySet<string> BuiltInFilterBases = new HashSet<string>(StringComparer.Ordinal)
    {
        "filterByKeywords", "restrictToKeywords", "filterByEditorIdContains", "filterByNameContains",
        "filterByModNames", "skipRecordByModNameContains", "modNamesLastOverridden",
        "filterByMgefs", "filterByAlternateTextures",
    };

    static FilterVerdict EvaluateFilters(object record, FormKey fk, string? editorId,
        SkyPatcherRecordCatalog recordCatalog, RecordMap? fieldMap,
        IReadOnlyList<(SkyPatcherSegment seg, SkyPatcherKeyClass cls)> filters, IFormResolver resolver,
        List<string> warnings, HashSet<string> dedupe)
    {
        var mutagenRecordType = fieldMap?.RecordType;

        // The player rule (grammar §4): the player is excluded from EVERY line — filtered, restricted,
        // or unfiltered — except a lone bare primary filter that names it. Strict reading of the
        // documented "use filterByNpcs=Skyrim.esm|7 ALONE"; declared assumption for the empirical gate.
        // "Alone" means no other RECORD filter — hasPlugins gates the LINE on the load order, not the
        // record, so a plugin-gated player line still applies when its gate passes (review finding:
        // counting the gate silently excluded every conditional player patch).
        if (fk == PlayerFormKey)
        {
            var gates = filters.Where(f2 => f2.cls.Filter!.Kind == SkyPatcherFilterKind.HasPlugins).ToList();
            var recordFilters = filters.Where(f2 => f2.cls.Filter!.Kind != SkyPatcherFilterKind.HasPlugins).ToList();
            bool lonePrimary = recordFilters.Count == 1
                && recordFilters[0].cls.Filter!.Kind == SkyPatcherFilterKind.Primary
                && (recordFilters[0].cls.Connective ?? "") == ""
                && recordFilters[0].seg.Values.Any(v => MatchesIdentity(v, fk, editorId));
            if (!lonePrimary) return FilterVerdict.NoMatch;
            foreach (var (gSeg, gCls) in gates)
            {
                var plugins = gSeg.Values.Select(v => v.Raw).ToList();
                bool okGate = (gCls.Connective ?? "") == "Or" ? plugins.Any(resolver.PluginPresent) : plugins.All(resolver.PluginPresent);
                if (!okGate) return FilterVerdict.NoMatch;
            }
            return FilterVerdict.Match;
        }

        // No filter set → every record of the type is patched (grammar §5).
        if (filters.Count == 0) return FilterVerdict.Match;

        bool any = false;
        foreach (var (seg, cls) in filters)
        {
            var f = cls.Filter!;
            var conn = cls.Connective ?? "";

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

            if (f.Kind == SkyPatcherFilterKind.Primary)
            {
                bool inSet = seg.Values.Any(v => MatchesIdentity(v, fk, editorId));
                bool ok = conn is "Excluded" or "Exclude" ? !inSet : inSet;
                if (!ok) return FilterVerdict.NoMatch;
                any = true; continue;
            }

            var verdict = EvaluateOneFilter(record, fk, editorId, cls, seg, conn, fieldMap, resolver, warnings, dedupe);
            if (verdict != FilterVerdict.Match) return verdict;
            any = true;
        }
        return any ? FilterVerdict.Match : FilterVerdict.NoMatch;
    }

    /// <summary>One non-primary, non-gate filter segment against this record: the built-in families
    /// first (keyed by base name), then the field map's per-record <see cref="FilterSpec"/>. A map
    /// entry for a built-in name OVERRIDES the built-in (a recipe's filterByKeywords matches the
    /// CREATED object's keywords, not its own). Anything else is Unresolved — loud skip upstream.</summary>
    static FilterVerdict EvaluateOneFilter(object record, FormKey fk, string? editorId,
        SkyPatcherKeyClass cls, SkyPatcherSegment seg, string conn, RecordMap? fieldMap,
        IFormResolver resolver, List<string> warnings, HashSet<string> dedupe)
    {
        var spec = fieldMap?.Filters.GetValueOrDefault(cls.BaseKey);
        if (spec is { IsUnmapped: true })
        {
            if (dedupe.Add($"fu:{cls.BaseKey}"))
                warnings.Add($"filter '{cls.BaseKey}' has no static evaluation — {spec.Unmapped}");
            return FilterVerdict.Unresolved;
        }
        if (spec is not null)
            return EvaluateSpec(record, cls, seg, conn, spec, fieldMap!, resolver, warnings, dedupe);

        switch (cls.BaseKey)
        {
            case "filterByKeywords":
            case "restrictToKeywords":   // post-match narrowing; for ONE record that's the same verdict
                return KeywordVerdict(ReadEngine.KeywordKeys(record), seg, cls.BaseKey, conn, resolver, warnings, dedupe);

            case "filterByEditorIdContains":
                return ContainsVerdict(seg, conn, editorId ?? "") ? FilterVerdict.Match : FilterVerdict.NoMatch;

            case "filterByNameContains":
                return ContainsVerdict(seg, conn, RecordName(record) ?? "") ? FilterVerdict.Match : FilterVerdict.NoMatch;

            case "filterByModNames":
            {
                // "Records that come from the named plugin(s)" — the record's DEFINING master
                // (FormKey.ModKey). DECLARED ASSUMPTION (empirical-gate item): the DLL could
                // conceivably test the WINNING override's provider instead. When the two readings
                // AGREE for this record (the overwhelmingly common case) the answer is safe under
                // either; when they DISAGREE the verdict is honestly UNRESOLVED, never a guess
                // (review finding — a definite verdict here silently picked a side).
                var origin = fk.ModKey.FileName.String;
                bool inSet = seg.Values.Any(v => v.Raw.Equals(origin, StringComparison.OrdinalIgnoreCase));
                if (resolver.WinnerPluginOf(fk) is { } winner
                    && seg.Values.Any(v => v.Raw.Equals(winner, StringComparison.OrdinalIgnoreCase)) != inSet)
                {
                    if (dedupe.Add($"mn:{fk}"))
                        warnings.Add($"filterByModNames{conn}: the record's defining master ('{origin}') and winning override ('{winner}') disagree on membership — which one the DLL tests is unverified, so whether the line applies is UNRESOLVED.");
                    return FilterVerdict.Unresolved;
                }
                bool ok = conn is "Excluded" or "Exclude" ? !inSet : inSet;
                return ok ? FilterVerdict.Match : FilterVerdict.NoMatch;
            }
            case "skipRecordByModNameContains":
            {
                // Skip when the record's source mod name CONTAINS a listed substring (cell.md) —
                // an inverted filter: match ⇒ the line does NOT apply.
                var origin = fk.ModKey.FileName.String;
                bool hit = seg.Values.Any(v => origin.Contains(v.Raw, StringComparison.OrdinalIgnoreCase));
                return hit ? FilterVerdict.NoMatch : FilterVerdict.Match;
            }
            case "modNamesLastOverridden":
            {
                // "Skip records whose LAST OVERRIDE is from a named mod" (magic-effect.md) — the
                // load-order WINNER's plugin, i.e. the record view's conflict winner. Documented only
                // in the Excluded spelling; any other connective is unresolved, never guessed.
                if (conn is not ("Excluded" or "Exclude"))
                {
                    if (dedupe.Add($"ovc:{cls.BaseKey}{conn}"))
                        warnings.Add($"filter '{cls.BaseKey}{conn}' — only the Excluded spelling is documented; whether this connective yields or selects is UNRESOLVED.");
                    return FilterVerdict.Unresolved;
                }
                var winner = resolver.WinnerPluginOf(fk);
                if (winner is null)
                {
                    if (dedupe.Add($"ov:{fk}"))
                        warnings.Add($"modNamesLastOverridden{conn}: could not resolve the winning override plugin of {fk} — whether the line yields is UNRESOLVED.");
                    return FilterVerdict.Unresolved;
                }
                bool hit = seg.Values.Any(v => v.Raw.Equals(winner, StringComparison.OrdinalIgnoreCase));
                return hit ? FilterVerdict.NoMatch : FilterVerdict.Match;
            }
            case "filterByMgefs":
            {
                // Crosscutting attached-effect match (spell/scroll/ench/ingestible/ingredient): the
                // Effects array's BaseEffect links. (On the magicEffect folder filterByMgefs is the
                // PRIMARY filter and never reaches here.)
                var mine = EntryKeys(record, new[] { "Effects" }, "BaseEffect");
                return FormSetVerdict(mine, seg, cls.BaseKey, conn, "MagicEffect", resolver, warnings, dedupe);
            }
            case "filterByAlternateTextures":
            {
                // Items carrying a given texture set: the model's alternate-texture entries' NewTexture.
                var mine = EntryKeys(record, new[] { "Model", "AlternateTextures" }, "NewTexture");
                return FormSetVerdict(mine, seg, cls.BaseKey, conn, "TextureSet", resolver, warnings, dedupe);
            }
            default:
                // Neither built-in nor mapped — a coverage gap the filtermap guard should have caught;
                // named here too so a stale plugin build can't skip silently.
                if (dedupe.Add($"nf:{cls.BaseKey}"))
                    warnings.Add($"filter '{cls.BaseKey}' has no evaluation (neither built-in nor in the filter map) — whether lines carrying it apply is UNRESOLVED (a coverage gap; report it).");
                return FilterVerdict.Unresolved;
        }
    }

    // ---- the map-driven evaluations -----------------------------------------------------------------

    static FilterVerdict EvaluateSpec(object record, SkyPatcherKeyClass cls, SkyPatcherSegment seg,
        string conn, FilterSpec spec, RecordMap fieldMap, IFormResolver resolver,
        List<string> warnings, HashSet<string> dedupe)
    {
        bool excluded = conn is "Excluded" or "Exclude";
        switch (spec.Eval)
        {
            case SkyPatcherFilterEval.FormEquals:
            {
                // Single-valued form field vs a value list: any-of (an AND over one slot is
                // unsatisfiable for >1 distinct values — see the enum's declared assumption).
                // No early break on a match — every unresolvable token still earns its once-per-token
                // warning (review polish).
                var current = spec.Paths.Select(p => TryLeafToken(record, p)).Where(t => t is not null).ToList();
                bool matched = false;
                foreach (var v in seg.Values)
                {
                    var k = ResolveFormValue(v, spec.FormType, resolver);
                    if (k is null) { WarnUnresolvableForm(v, cls.BaseKey, conn, spec, warnings, dedupe); continue; }
                    matched |= current.Any(t => string.Equals(t, k.Value.ToString(), StringComparison.OrdinalIgnoreCase));
                }
                return (excluded ? !matched : matched) ? FilterVerdict.Match : FilterVerdict.NoMatch;
            }
            case SkyPatcherFilterEval.FormInList:
            {
                var segs = SplitPath(spec.Paths[0]);
                var mine = spec.KeyPath is null ? TryFormLinkKeys(record, segs) : EntryKeys(record, segs, spec.KeyPath);
                if (spec.EidSubstring)
                    return EidAwareListVerdict(mine, seg, cls.BaseKey, conn, spec, resolver, warnings, dedupe);
                return FormSetVerdict(mine, seg, cls.BaseKey, conn, spec.FormType, resolver, warnings, dedupe);
            }
            case SkyPatcherFilterEval.EnumEquals:
            {
                // A token that names NO member of the leaf enum (after valueMap) can never match — but
                // "never matches" is a silently-WRONG verdict under Excluded (it would flip to Match on
                // the records the author meant to exclude). Unknown token ⇒ warn + Unresolved, the same
                // treatment every flag evaluator gives an unknown flag (review finding; the fieldmap's
                // 'scroll'/'darkness'/'nighteye' notes promise exactly this warning).
                var current = spec.Paths.Select(p => TryLeafToken(record, p)).FirstOrDefault(t => t is not null);
                Type? enumType = null;
                foreach (var p in spec.Paths)
                    if (TryLeafEnumType(record, p) is { } et) { enumType = et; break; }
                bool matched = false;
                foreach (var v in seg.Values)
                {
                    var member = spec.ValueMap?.GetValueOrDefault(v.Raw) ?? v.Raw;
                    if (enumType is not null && !TryParseEnumMember(enumType, member, out _))
                    {
                        if (dedupe.Add($"ee:{cls.BaseKey}:{v.Raw}"))
                            warnings.Add($"filter '{cls.BaseKey}' — '{v.Raw}' is not a {enumType.Name} member (no valueMap match either); whether the line applies is UNRESOLVED.");
                        return FilterVerdict.Unresolved;
                    }
                    if (current is not null && string.Equals(current, member, StringComparison.OrdinalIgnoreCase)) matched = true;
                }
                return (excluded ? !matched : matched) ? FilterVerdict.Match : FilterVerdict.NoMatch;
            }
            case SkyPatcherFilterEval.FlagBool:
            {
                var raw = seg.Values.Count > 0 ? seg.Values[0].Raw : "";
                bool? want = ParseBoolToken(raw);
                if (want is null)
                {
                    if (dedupe.Add($"fb:{cls.BaseKey}:{raw}"))
                        warnings.Add($"filter '{cls.BaseKey}={raw}' — not a boolean; whether the line applies is UNRESOLVED.");
                    return FilterVerdict.Unresolved;
                }
                var (bits, enumType) = FlagLeaf(record, spec.Paths[0]);
                if (enumType is null || !TryParseEnumMember(enumType, spec.Flag!, out var bit))
                    return UnresolvedLeaf(cls.BaseKey, spec.Paths[0], warnings, dedupe);
                bool set = (bits & bit) != 0;
                if (spec.Invert) set = !set;
                return set == want.Value ? FilterVerdict.Match : FilterVerdict.NoMatch;
            }
            case SkyPatcherFilterEval.FlagAnyOf:
            {
                var (bits, enumType) = FlagLeaf(record, spec.Paths[0]);
                if (enumType is null) return UnresolvedLeaf(cls.BaseKey, spec.Paths[0], warnings, dedupe);
                var hits = new List<bool>();
                foreach (var v in seg.Values)
                {
                    var member = spec.ValueMap?.GetValueOrDefault(v.Raw) ?? v.Raw;
                    if (!TryParseEnumMember(enumType, member, out var bit))
                    {
                        if (dedupe.Add($"fa:{cls.BaseKey}:{v.Raw}"))
                            warnings.Add($"filter '{cls.BaseKey}' — flag '{v.Raw}' is not a member of the {spec.Paths[0]} enum (no valueMap match either); whether the line applies is UNRESOLVED.");
                        return FilterVerdict.Unresolved;
                    }
                    hits.Add((bits & bit) != 0);
                }
                return ConnectiveVerdict(conn, hits) ? FilterVerdict.Match : FilterVerdict.NoMatch;
            }
            case SkyPatcherFilterEval.Gender:
            {
                var raw = seg.Values.Count > 0 ? seg.Values[0].Raw : "";
                bool female;
                if (raw.Equals("female", StringComparison.OrdinalIgnoreCase)) female = true;
                else if (raw.Equals("male", StringComparison.OrdinalIgnoreCase)) female = false;
                else
                {
                    if (dedupe.Add($"g:{raw}"))
                        warnings.Add($"filter '{cls.BaseKey}={raw}' — expected male|female; whether the line applies is UNRESOLVED.");
                    return FilterVerdict.Unresolved;
                }
                // A TRAITS-templated NPC takes its gender from the TEMPLATE actor — the own-record
                // Female bit is not authoritative, so a definite verdict would be silently wrong for
                // exactly those NPCs (review finding; the same derived-from-template class that keeps
                // restrictToSkill unmapped). The gendered eval is NPC-only, so the sibling path is fixed.
                var (tBits, tType) = FlagLeaf(record, "Configuration.TemplateFlags");
                if (tType is not null && TryParseEnumMember(tType, "Traits", out var traitsBit) && (tBits & traitsBit) != 0)
                {
                    if (dedupe.Add($"gt:{cls.BaseKey}"))
                        warnings.Add($"filter '{cls.BaseKey}' — this NPC templates its TRAITS (gender comes from the template actor, not this record); whether the line applies is UNRESOLVED.");
                    return FilterVerdict.Unresolved;
                }
                var (bits, enumType) = FlagLeaf(record, spec.Paths[0]);
                if (enumType is null || !TryParseEnumMember(enumType, "Female", out var bit))
                    return UnresolvedLeaf(cls.BaseKey, spec.Paths[0], warnings, dedupe);
                return ((bits & bit) != 0) == female ? FilterVerdict.Match : FilterVerdict.NoMatch;
            }
            case SkyPatcherFilterEval.PcLevelMult:
            {
                var raw = seg.Values.Count > 0 ? seg.Values[0].Raw : "";
                bool? want = ParseBoolToken(raw);
                if (want is null)
                {
                    if (dedupe.Add($"pl:{cls.BaseKey}:{raw}"))
                        warnings.Add($"filter '{cls.BaseKey}={raw}' — not a boolean; whether the line applies is UNRESOLVED.");
                    return FilterVerdict.Unresolved;
                }
                var (parent, leaf) = Navigate(record, SplitPath(spec.Paths[0]));
                bool isMult = leaf.GetValue(parent)?.GetType().Name.Equals("PcLevelMult", StringComparison.Ordinal) == true;
                return isMult == want.Value ? FilterVerdict.Match : FilterVerdict.NoMatch;
            }
            case SkyPatcherFilterEval.SubstringLeaf:
            {
                var hay = spec.Paths.Select(p => TryLeafToken(record, p)).FirstOrDefault(t => t is not null) ?? "";
                return ContainsVerdict(seg, conn, hay) ? FilterVerdict.Match : FilterVerdict.NoMatch;
            }
            case SkyPatcherFilterEval.DonorSubstring:
            {
                var donor = LinkedFormKey(record, spec.LinkPath!);
                if (donor is null) return FilterVerdict.NoMatch;   // no link ⇒ nothing to match
                var hay = resolver.ReadWinnerLeaf(donor.Value, spec.Paths[0]);
                if (hay is null)
                {
                    if (dedupe.Add($"ds:{cls.BaseKey}:{donor}"))
                        warnings.Add($"filter '{cls.BaseKey}' — could not read '{spec.Paths[0]}' off {donor}'s winner; whether the line applies is UNRESOLVED.");
                    return FilterVerdict.Unresolved;
                }
                return ContainsVerdict(seg, conn, hay) ? FilterVerdict.Match : FilterVerdict.NoMatch;
            }
            case SkyPatcherFilterEval.DonorKeywords:
            {
                var donor = LinkedFormKey(record, spec.LinkPath!);
                if (donor is null) return FilterVerdict.NoMatch;
                var mine = resolver.KeywordsOf(donor.Value);
                if (mine is null)
                {
                    if (dedupe.Add($"dk:{cls.BaseKey}:{donor}"))
                        warnings.Add($"filter '{cls.BaseKey}' — could not read the keywords of {donor}'s winner; whether the line applies is UNRESOLVED.");
                    return FilterVerdict.Unresolved;
                }
                return KeywordVerdict(mine, seg, cls.BaseKey, conn, resolver, warnings, dedupe);
            }
            case SkyPatcherFilterEval.NumericLess:
            {
                var tok = TryLeafToken(record, spec.Paths[0]);
                if (tok is null || !double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out var current))
                    return FilterVerdict.NoMatch;   // absent/non-numeric leaf can't be "less than N"
                var raw = seg.Values.Count > 0 ? seg.Values[0].Raw : "";
                if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
                {
                    if (dedupe.Add($"nl:{cls.BaseKey}:{raw}"))
                        warnings.Add($"filter '{cls.BaseKey}={raw}' — not a number; whether the line applies is UNRESOLVED.");
                    return FilterVerdict.Unresolved;
                }
                return current < n ? FilterVerdict.Match : FilterVerdict.NoMatch;
            }
            case SkyPatcherFilterEval.BipedSlots:
            {
                // Absent BodyTemplate reads as "occupies no slots" — a fact about the record, not a
                // failure — so bits ride even when the enum leaf can't be reached.
                var (bits, _) = FlagLeaf(record, spec.Paths[0]);
                var hits = new List<bool>();
                foreach (var v in seg.Values)
                {
                    if (!int.TryParse(v.Raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx) || idx is < 0 or > 31)
                    {
                        if (dedupe.Add($"bs:{cls.BaseKey}:{v.Raw}"))
                            warnings.Add($"filter '{cls.BaseKey}' — '{v.Raw}' is not a biped slot INDEX (0–31; slot number − 30); whether the line applies is UNRESOLVED.");
                        return FilterVerdict.Unresolved;
                    }
                    hits.Add((bits & (1UL << idx)) != 0);
                }
                return ConnectiveVerdict(conn, hits) ? FilterVerdict.Match : FilterVerdict.NoMatch;
            }
            case SkyPatcherFilterEval.LinkedOriginPlugin:
            {
                var linked = LinkedFormKey(record, spec.Paths[0]);
                if (linked is null) return spec.Invert ? FilterVerdict.Match : FilterVerdict.NoMatch;   // no link ⇒ nothing to yield to
                var linkedOrigin = linked.Value.ModKey.FileName.String;
                bool hit = seg.Values.Any(v => v.Raw.Equals(linkedOrigin, StringComparison.OrdinalIgnoreCase));
                if (spec.Invert) return hit ? FilterVerdict.NoMatch : FilterVerdict.Match;   // skip semantics
                return hit ? FilterVerdict.Match : FilterVerdict.NoMatch;
            }
            default:
                // Unreachable while the FilterSpec parser and this switch agree on the eval kinds —
                // named anyway so a drift can't skip silently (the same contract every arm honors).
                if (dedupe.Add($"ue:{cls.BaseKey}"))
                    warnings.Add($"filter '{cls.BaseKey}' — eval kind '{spec.Eval}' has no evaluator; whether the line applies is UNRESOLVED (report it).");
                return FilterVerdict.Unresolved;
        }
    }

    // ---- filter helpers ------------------------------------------------------------------------------

    /// <summary>THE connective fold (grammar §5) — bare = AND (every hit true, gated by
    /// <paramref name="bareGuard"/>), Or = any, Excluded/Exclude = none. The ONE copy every
    /// list-membership evaluator rides (review fold — five hand-kept copies of one grammar rule).</summary>
    static bool ConnectiveVerdict(string conn, IReadOnlyList<bool> hits, bool bareGuard = true) => conn switch
    {
        "Or" => hits.Any(h => h),
        "Excluded" or "Exclude" => !hits.Any(h => h),
        _ => bareGuard && hits.All(h => h),
    };

    /// <summary>The keyword-family verdict — <see cref="FormSetVerdict"/> scoped to Keyword. Null set ⇒
    /// the type has no keyword list we can read (unresolved).</summary>
    static FilterVerdict KeywordVerdict(IReadOnlyList<FormKey>? mine, SkyPatcherSegment seg,
        string baseKey, string conn, IFormResolver resolver, List<string> warnings, HashSet<string> dedupe)
        => mine is null ? FilterVerdict.Unresolved
            : FormSetVerdict(mine, seg, baseKey, conn, "Keyword", resolver, warnings, dedupe, noun: "keyword");

    /// <summary>List-membership verdict for a set of the record's own attached forms (keywords, mgefs,
    /// alternate textures, factions, recipe ingredients…): bare = ALL listed present, Or = any,
    /// Excluded = none. A listed form that resolves to NOTHING in the active order can never be attached
    /// to any record — that's a fact about the order, not a guess: it evaluates as not-attached,
    /// surfaced ONCE per token (Wave-1 crux finding: real INIs list keywords from frameworks the
    /// modlist doesn't run, e.g. SLA_KillerHeels) — and makes the bare-AND unsatisfiable.</summary>
    static FilterVerdict FormSetVerdict(IReadOnlyList<FormKey> mine, SkyPatcherSegment seg,
        string baseKey, string conn, string? formType, IFormResolver resolver,
        List<string> warnings, HashSet<string> dedupe, string noun = "form")
    {
        var wanted = new List<FormKey>();
        int unresolved = 0;
        foreach (var v in seg.Values)
        {
            var k = ResolveFormValue(v, formType, resolver);
            if (k is null)
            {
                unresolved++;
                if (dedupe.Add($"fs:{baseKey}:{v.Raw}"))
                    warnings.Add($"{noun} '{v.Raw}' (in a {baseKey}{conn}) resolves to nothing in the active order — treated as attached to no record.");
            }
            else wanted.Add(k.Value);
        }
        return ConnectiveVerdict(conn, wanted.Select(mine.Contains).ToList(), bareGuard: unresolved == 0)
            ? FilterVerdict.Match : FilterVerdict.NoMatch;
    }

    /// <summary>filterByArmorAddons' documented "EditorID substring ok": a listed value that resolves
    /// to a form matches by key; one that doesn't is a SUBSTRING against each attached form's winner
    /// EditorID (resolver lookup).</summary>
    static FilterVerdict EidAwareListVerdict(IReadOnlyList<FormKey> mine, SkyPatcherSegment seg,
        string baseKey, string conn, FilterSpec spec, IFormResolver resolver,
        List<string> warnings, HashSet<string> dedupe)
    {
        var eids = new Lazy<List<string>>(() => mine
            .Select(k => resolver.EditorIdOf(k) ?? "")
            .Where(e => e.Length > 0).ToList());
        var hits = new List<bool>();
        foreach (var v in seg.Values)
        {
            var k = ResolveFormValue(v, spec.FormType, resolver);
            hits.Add(k is not null
                ? mine.Contains(k.Value)
                : eids.Value.Any(e => e.Contains(v.Raw, StringComparison.OrdinalIgnoreCase)));
        }
        return ConnectiveVerdict(conn, hits) ? FilterVerdict.Match : FilterVerdict.NoMatch;
    }

    /// <summary>A leaf token that treats ANY navigation/absence failure as null — filters read
    /// optional structure (an absent Model, a null BodyTemplate) as "not there", never a throw.</summary>
    static string? TryLeafToken(object record, string path)
    {
        try { return LeafToken(record, SplitPath(path)); }
        catch { return null; }
    }

    /// <summary>The FormKeys of a struct list's key sub-field (Effects→BaseEffect, Factions→Faction…).
    /// Absent list reads as empty.</summary>
    static IReadOnlyList<FormKey> EntryKeys(object record, string[] segs, string keyPath)
    {
        var keys = new List<FormKey>();
        System.Collections.IList? list;
        try { list = ListAt(record, segs); }
        catch { return keys; }
        if (list is null) return keys;
        var kSegs = SplitPath(keyPath);
        foreach (var entry in list)
        {
            if (entry is null) continue;
            string? tok;
            try { tok = LeafToken(entry, kSegs); }
            catch { continue; }
            if (tok is not null && FormKey.TryFactory(tok, out var k)) keys.Add(k);
        }
        return keys;
    }

    /// <summary>A plain formlink list's keys with absence-as-empty and failure-as-empty (filter read).</summary>
    static IReadOnlyList<FormKey> TryFormLinkKeys(object record, string[] segs)
    {
        try { return FormLinkList(record, segs) ?? new List<FormKey>(); }
        catch { return new List<FormKey>(); }
    }

    /// <summary>The FormKey a formlink leaf points at (null = absent/unset/unnavigable).</summary>
    static FormKey? LinkedFormKey(object record, string path)
    {
        var tok = TryLeafToken(record, path);
        return tok is not null && FormKey.TryFactory(tok, out var k) ? k : null;
    }

    /// <summary>ONE navigation for the flag-family evaluators: the raw bits AND the enum type of the
    /// leaf at <paramref name="path"/> (review fold — the old bits+member pair walked the same leaf
    /// twice, or 1+N times for a value list). Absent structure reads as (0, null): bits honestly say
    /// "no flags set", and the null type tells member-resolving callers to go Unresolved.</summary>
    static (ulong bits, Type? enumType) FlagLeaf(object record, string path)
    {
        try
        {
            var (parent, leaf) = Navigate(record, SplitPath(path));
            var et = Nullable.GetUnderlyingType(leaf.PropertyType) ?? leaf.PropertyType;
            if (!et.IsEnum) return (0, null);
            return (Convert.ToUInt64(leaf.GetValue(parent) ?? 0UL, CultureInfo.InvariantCulture), et);
        }
        catch { return (0, null); }
    }

    /// <summary>The enum type of a leaf (null = not navigable / not an enum) — EnumEquals' unknown-token
    /// recognizer.</summary>
    static Type? TryLeafEnumType(object record, string path)
    {
        try
        {
            var (parent, leaf) = Navigate(record, SplitPath(path));
            var et = Nullable.GetUnderlyingType(leaf.PropertyType) ?? leaf.PropertyType;
            return et.IsEnum ? et : null;
        }
        catch { return null; }
    }

    /// <summary>The named-warning Unresolved for a flag/enum leaf that can't be resolved on this record
    /// — every Unresolved return owes a per-filter warning (the line-level skip message points at it).</summary>
    static FilterVerdict UnresolvedLeaf(string baseKey, string path, List<string> warnings, HashSet<string> dedupe)
    {
        if (dedupe.Add($"ul:{baseKey}:{path}"))
            warnings.Add($"filter '{baseKey}' — could not resolve '{path}' (or its member) on this record; whether the line applies is UNRESOLVED.");
        return FilterVerdict.Unresolved;
    }

    static bool? ParseBoolToken(string raw)
        => raw.Equals("true", StringComparison.OrdinalIgnoreCase) || raw.Equals("yes", StringComparison.OrdinalIgnoreCase) || raw == "1" ? true
         : raw.Equals("false", StringComparison.OrdinalIgnoreCase) || raw.Equals("no", StringComparison.OrdinalIgnoreCase) || raw == "0" ? false
         : null;

    static void WarnUnresolvableForm(SkyPatcherValue v, string baseKey, string conn, FilterSpec spec,
        List<string> warnings, HashSet<string> dedupe)
    {
        if (dedupe.Add($"fe:{baseKey}:{v.Raw}"))
            warnings.Add($"form '{v.Raw}' (in a {baseKey}{conn}) resolves to nothing in the active order{(spec.FormType is null ? "" : $" among {spec.FormType} winners")} — treated as matching no record.");
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
    public static bool TryFormKey(FormAddress a, out FormKey fk)
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
                if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var num))
                { warnings.Add($"{where}: '{seg.Key}={raw}' — not a number; skipped."); return; }
                var comp = map.Component ?? throw new InvalidOperationException($"'{seg.Key}' mapping has no component");
                var before = LeafToken(record, segs);
                var parts = before?.Split(',');
                if (parts is null || comp >= parts.Length)
                { warnings.Add($"{where}: '{seg.Key}' — '{map.Path}' is absent or not a {comp + 1}+-component point ('{before ?? "<unreadable>"}'); skipped."); return; }
                // An INTEGRAL component (P3Int16 — ObjectBounds) rounds fractional input away-from-zero
                // — the same declared assumption FormatNumericFor carries (Wave-1 empirical item); the
                // engine's per-component Parse would otherwise reject '3.5' where the old rebuild rounded
                // it (review fold). The component's ctor-parameter type is the recognizer, not a type list.
                var leafType = Navigate(record, segs).leaf.PropertyType;
                var ptType = Nullable.GetUnderlyingType(leafType) ?? leafType;
                var ctorPs = ptType.GetConstructors().FirstOrDefault(c => c.GetParameters().Length == parts.Length)?.GetParameters();
                bool integral = ctorPs is not null && comp < ctorPs.Length && IsIntegral(ctorPs[comp].ParameterType);
                parts[comp] = integral
                    ? Math.Round(num, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture)
                    : raw.Trim();
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
            case SkyPatcherOpSemantic.DictSet:
            case SkyPatcherOpSemantic.DictMult:
            {
                var raw = seg.Values.Count > 0 ? seg.Values[0].Raw : "";
                if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var operand))
                { warnings.Add($"{where}: '{seg.Key}={raw}' — not a number; skipped."); return; }
                var key = map.Key ?? throw new InvalidOperationException($"'{seg.Key}' mapping has no dict key");
                var current = DictNumericValue(record, segs, key);
                double result;
                string? note = null;
                if (map.Semantic == SkyPatcherOpSemantic.DictMult)
                {
                    if (current is null)
                    { warnings.Add($"{where}: '{seg.Key}' — '{map.Path}[{key}]' has no current value to multiply; skipped."); return; }
                    result = current.Value * operand;
                    note = $"stateful: {Num(current.Value)} × {raw}";
                }
                else result = operand;
                // The mutation rides the engine's dict Set (Key = the enum entry) — same coercion as the verb surface.
                WriteEngine.ApplyVerb(record, new WriteRequest
                { RecordType = fieldMap.RecordType, Path = segs, Verb = "Set", Key = key, Value = result.ToString("R", CultureInfo.InvariantCulture) });
                applied.Add(new SkyPatcherAppliedOp(line.File, line.LineNumber, seg.Key, raw, $"{map.Path}[{key}]",
                    current is null ? null : Num(current.Value), Num(DictNumericValue(record, segs, key) ?? result), note));
                break;
            }
            case SkyPatcherOpSemantic.ColorChannel:
            {
                // One channel of a whole-value Color: ReadEngine renders "R,G,B,A" and the engine coerces
                // the same form back — the P3 vector-component token splice, alpha preserved.
                var raw = seg.Values.Count > 0 ? seg.Values[0].Raw : "";
                if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var num))
                { warnings.Add($"{where}: '{seg.Key}={raw}' — not a number; skipped."); return; }
                var comp = map.Component ?? throw new InvalidOperationException($"'{seg.Key}' mapping has no component");
                var before = LeafToken(record, segs);
                var parts = before?.Split(',');
                if (parts is null || parts.Length < 3 || comp > 2)
                { warnings.Add($"{where}: '{seg.Key}' — '{map.Path}' is absent or not an R,G,B[,A] colour ('{before ?? "<unreadable>"}'); skipped."); return; }
                parts[comp] = Math.Round(num, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture);
                WriteEngine.ApplyVerb(record, new WriteRequest { RecordType = fieldMap.RecordType, Path = segs, Verb = "Set", Value = string.Join(",", parts) });
                applied.Add(new SkyPatcherAppliedOp(line.File, line.LineNumber, seg.Key, raw, $"{map.Path}.{"RGB"[comp]}", before, LeafToken(record, segs), null));
                break;
            }
            case SkyPatcherOpSemantic.BipedSlotsSet:
            case SkyPatcherOpSemantic.BipedSlotsRemove:
            {
                var (parent, leaf) = Navigate(record, segs);
                if (!(Nullable.GetUnderlyingType(leaf.PropertyType) ?? leaf.PropertyType).IsEnum)
                    throw new InvalidOperationException($"'{map.Path}' is not a flags enum");
                var before = LeafToken(record, segs);
                ulong bits = Convert.ToUInt64(leaf.GetValue(parent) ?? 0UL, CultureInfo.InvariantCulture);
                foreach (var v in seg.Values)
                {
                    if (!int.TryParse(v.Raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx) || idx is < 0 or > 31)
                    { warnings.Add($"{where}: '{seg.Key}' — '{v.Raw}' is not a biped slot INDEX (0–31; slot number − 30); that slot skipped."); continue; }
                    bits = map.Semantic == SkyPatcherOpSemantic.BipedSlotsSet ? bits | (1UL << idx) : bits & ~(1UL << idx);
                }
                SetEnumBits(record, fieldMap, segs, bits);
                applied.Add(new SkyPatcherAppliedOp(line.File, line.LineNumber, seg.Key, seg.RawValue ?? "", map.Path, before, LeafToken(record, segs), null));
                break;
            }
            case SkyPatcherOpSemantic.TeachSpell:
            case SkyPatcherOpSemantic.TeachSkill:
            {
                var v = seg.Values.Count > 0 ? seg.Values[0] : null;
                if (v is null) { warnings.Add($"{where}: '{seg.Key}' has no value; skipped."); return; }
                var before = LeafToken(record, segs);
                WriteRequest armSet;
                string armType;
                if (map.Semantic == SkyPatcherOpSemantic.TeachSpell)
                {
                    var spell = ResolveFormValue(v, map.FormType, resolver);
                    if (spell is null) { warnings.Add($"{where}: '{seg.Key}={v.Raw}' — spell not resolvable; skipped."); return; }
                    armType = "BookSpell";
                    armSet = new WriteRequest { RecordType = armType, Path = new[] { "Spell" }, Verb = "Set", Value = spell.Value.ToString() };
                }
                else
                {
                    armType = "BookSkill";
                    var token = map.ValueMap?.GetValueOrDefault(v.Raw) ?? v.Raw;
                    armSet = new WriteRequest { RecordType = armType, Path = new[] { "Skill" }, Verb = "Set", Value = token };
                }
                // The polymorphic Teaches swap is the engine's compose-Set: build the concrete arm from
                // parts and Set the leaf wholesale — the same path the record-authoring surface uses.
                WriteEngine.ApplyVerb(record, new WriteRequest
                {
                    RecordType = fieldMap.RecordType, Path = segs, Verb = "Set",
                    Struct = new StructSpec { Type = armType, Sets = new List<WriteRequest> { armSet } },
                });
                applied.Add(new SkyPatcherAppliedOp(line.File, line.LineNumber, seg.Key, v.Raw, map.Path, before, LeafToken(record, segs),
                    $"Teaches → {armType}"));
                break;
            }
            case SkyPatcherOpSemantic.AddEntry:
            case SkyPatcherOpSemantic.AddEntryOnce:
            case SkyPatcherOpSemantic.RemoveEntry:
            case SkyPatcherOpSemantic.RemoveEntryByCount:
            case SkyPatcherOpSemantic.ReplaceEntry:
            case SkyPatcherOpSemantic.MultCount:
            case SkyPatcherOpSemantic.RemoveByKeyword:
            case SkyPatcherOpSemantic.SetEntryCount:
                ApplyEntryOp(record, fieldMap, seg, map, line, resolver, applied, warnings);
                break;

            default:
                warnings.Add($"{where}: op '{seg.Key}' — semantic {map.Semantic} not implemented; named gap.");
                break;
        }
    }

    static string Num(double d) => d.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>The numeric value of one dict entry (null = absent key / absent dict / non-numeric),
    /// read via the non-generic IDictionary view with the key parsed into the dict's enum key type.</summary>
    static double? DictNumericValue(object record, string[] segs, string key)
    {
        var (parent, leaf) = Navigate(record, segs);
        if (leaf.GetValue(parent) is not System.Collections.IDictionary dict) return null;
        var kType = (Nullable.GetUnderlyingType(leaf.PropertyType) ?? leaf.PropertyType).GetGenericArguments()[0];
        object keyObj;
        try { keyObj = kType.IsEnum ? Enum.Parse(kType, key, ignoreCase: true) : Convert.ChangeType(key, kType, CultureInfo.InvariantCulture); }
        catch { return null; }
        var val = dict.Contains(keyObj) ? dict[keyObj] : null;
        return val is null ? null : Convert.ToDouble(val, CultureInfo.InvariantCulture);
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
                case SkyPatcherOpSemantic.SetEntryCount:
                {
                    // changeCobjsCount=form~count sets a matching entry's count to N; the documented
                    // 'null' form means EVERY entry (null~0 = all ingredients to 0).
                    if (args.Count < 2 || !int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var setTo))
                    { warnings.Add($"{where}: '{seg.Key}={v.Raw}' — expected form~count; skipped."); continue; }
                    var countPath = el.CountPath ?? throw new InvalidOperationException($"'{seg.Key}' element has no countPath");
                    List<int> idx;
                    if (args[0].Equals("null", StringComparison.OrdinalIgnoreCase))
                        idx = Enumerable.Range(0, ListAt(record, segs)?.Count ?? 0).ToList();
                    else
                    {
                        var k = ResolveFormToken(args[0], map.FormType, resolver);
                        if (k is null) { warnings.Add($"{where}: '{seg.Key}={v.Raw}' — form not resolvable; skipped."); continue; }
                        idx = EntryIndicesByKey(record, segs, el, k.Value);
                    }
                    var list = ListAt(record, segs);
                    var cSegs = SplitPath(countPath);
                    foreach (var i in idx)
                        WriteEngine.ApplyVerb(list![i]!, new WriteRequest { RecordType = el.Type, Path = cSegs, Verb = "Set", Value = setTo.ToString(CultureInfo.InvariantCulture) });
                    applied.Add(new SkyPatcherAppliedOp(line.File, line.LineNumber, seg.Key, v.Raw, map.Path, null, null,
                        idx.Count > 0 ? $"count set to {setTo} on {idx.Count} entr(ies)" : "no matching entry — no change"));
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
        return IsIntegral(leaf.PropertyType)
            ? Math.Round(result, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture)
            : result.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>An integral numeric leaf/component (nullable unwrapped) — the ONE recognizer behind the
    /// declared fractional-rounding assumption (FormatNumericFor + the VecComponent splice).</summary>
    static bool IsIntegral(Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        return t == typeof(byte) || t == typeof(sbyte) || t == typeof(short) || t == typeof(ushort)
            || t == typeof(int) || t == typeof(uint) || t == typeof(long) || t == typeof(ulong);
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

    /// <summary>A formlink list's FormKeys (null when the path isn't a formlink list; absent reads as
    /// empty). The walk itself is the shared <see cref="ReadEngine.FormLinkKeys"/> (review fold — two
    /// link-reading strategies in one assembly was the drift this cleanup exists to kill).</summary>
    static List<FormKey>? FormLinkList(object record, string[] segs)
    {
        var (parent, leaf) = Navigate(record, segs);
        if (leaf.GetValue(parent) is not System.Collections.IEnumerable list) return new List<FormKey>();   // absent list reads as empty
        return ReadEngine.FormLinkKeys(list);
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
        var idx = EntryIndicesByKey(record, segs, el, key);   // absent/null list yields no indices — no separate guard walk
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
