using System.Globalization;
using System.Reflection;
using Mutagen.Bethesda.Plugins;

namespace HousecarlCore;

/// <summary>The SkyPatcher overlay engine — replay an ordered union of parsed INI lines onto a mutable copy of one record and report the true post-patch state; contract in docs/architecture/skypatcher-layer.md.</summary>
public static class SkyPatcherOverlay
{
    /// <summary>One call's collector for the replay's warnings: it keeps <see cref="Cap"/> distinct warnings and counts the rest, so neither the kept list nor the seen set grows with the batch.</summary>
    public sealed class WarningSink
    {
        /// <summary>How many warnings are kept for rendering; the rest are counted.</summary>
        public const int Cap = 20;

        readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        readonly List<string> _kept = new();

        /// <summary>The warnings a render lists, in the order they were first raised.</summary>
        public IReadOnlyList<string> Kept => _kept;

        /// <summary>How many further warnings were raised beyond <see cref="Kept"/>.</summary>
        public int Overflow { get; private set; }

        public void Add(string warning)
        {
            if (_kept.Count < Cap) { if (_seen.Add(warning)) _kept.Add(warning); return; }
            if (!_seen.Contains(warning)) Overflow++;
        }
    }

    /// <summary>Everything the overlay needs from the load order, behind an interface so the engine stays testable off fixtures.</summary>
    public interface IFormResolver
    {
        /// <summary>Resolve a bare EditorID to its winning FormKey, scoped to the Mutagen type when given; null means not found.</summary>
        FormKey? ResolveEditorId(string editorId, string? mutagenType);

        /// <summary>Read one leaf token off the load-order winner of another record; null means unresolvable.</summary>
        string? ReadWinnerLeaf(FormKey donor, string path);

        /// <summary>The keyword FormKeys attached to a record's load-order winner; null means unresolvable and the entry is NOT removed.</summary>
        IReadOnlyList<FormKey>? KeywordsOf(FormKey record);

        /// <summary>Whether a plugin (filename incl. extension) is in the active load order (hasPlugins).</summary>
        bool PluginPresent(string pluginName);

        /// <summary>The plugin whose override of a record wins the load order; null means the record is not present, surfaced loud.</summary>
        string? WinnerPluginOf(FormKey record);

        /// <summary>The EditorID of a record's load-order winner, for filterByArmorAddons' EditorID-substring match; null means unresolvable.</summary>
        string? EditorIdOf(FormKey record);
    }

    /// <summary>One parsed line in its apply-order context: the Data-relative file, the physical line, and the parsed form.</summary>
    public sealed record OrderedLine(string File, int LineNumber, SkyPatcherLine Parsed);

    /// <summary>One resolved field change: op, raw value, the Mutagen field it landed on, and the before/after leaf tokens (equal means a visible no-op).</summary>
    public sealed record SkyPatcherAppliedOp(string File, int LineNumber, string Op, string RawValue,
        string FieldPath, string? Before, string? After, string? Note);

    /// <summary>One HARD op that applies to this record but has no static resolution; <see cref="Reason"/> names why.</summary>
    public sealed record SkyPatcherDirective(string File, int LineNumber, string Op, string RawValue, string Reason);

    /// <summary>The overlay outcome for one record: what applied in order, what stayed a directive, and every warning.</summary>
    public sealed record SkyPatcherOverlayResult(
        IReadOnlyList<SkyPatcherAppliedOp> Applied,
        IReadOnlyList<SkyPatcherDirective> Directives,
        IReadOnlyList<string> Warnings,
        int LinesMatched,
        int LinesSkippedUnresolvedFilter);

    // ---- entry: replay the ordered lines onto one record copy ----

    /// <summary>Replay <paramref name="lines"/> (already in apply order) onto <paramref name="mutableRecord"/>, a deep mutable copy of the record's winner; never throws for content reasons, since every per-line failure becomes a warning and the replay continues.</summary>
    public static SkyPatcherOverlayResult Apply(
        object mutableRecord, FormKey fk, string? editorId,
        SkyPatcherCatalog catalog, SkyPatcherRecordCatalog recordCatalog, RecordMap? fieldMap,
        IEnumerable<OrderedLine> lines, IFormResolver resolver)
    {
        var applied = new List<SkyPatcherAppliedOp>();
        var directives = new List<SkyPatcherDirective>();
        var warnings = new List<string>();
        var warn = new FilterWarnings(warnings);
        int matched = 0, unresolvedSkips = 0;

        foreach (var line in lines)
        {
            if (line.Parsed.Kind != SkyPatcherLineKind.Patch) continue;
            var where = $"{line.File}:{line.LineNumber}";
            warn.At(line.File, where);
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
            // An unknown key poisons the WHOLE line: an unrecognized filter would mis-scope it, so the line skips loud.
            if (unknownKey)
            {
                unresolvedSkips++;
                var bad = string.Join(", ", line.Parsed.Segments
                    .Where(s => catalog.Classify(recordCatalog, s.Key).Role == SkyPatcherKeyRole.Unknown)
                    .Select(s => $"'{s.Key}'"));
                warnings.Add($"{where}: line skipped — key(s) {bad} are not in the SkyPatcher reference for record type '{recordCatalog.RecordType}' (an unrecognized key may be a filter, so whether the line applies is UNRESOLVED; verify the spelling or the reference version).");
                continue;
            }
            if (ops.Count == 0) continue;   // a line with no operation does nothing

            // ---- evaluate the filters against THIS record (unsupported ⇒ loud skip). ----
            var verdict = EvaluateFilters(mutableRecord, fk, editorId, recordCatalog, fieldMap, filters, resolver, warn);
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

    static string Ident(FormKey fk, string? editorId) => editorId is null ? FormIdToken.Of(fk) : $"{FormIdToken.Of(fk)} ({editorId})";

    static string HardReason(SkyPatcherOpDef op) => op.Shape switch
    {
        SkyPatcherOpShape.Mirror => $"copy-from-form — the value comes from another record's fields at apply time{NoteSuffix(op)}",
        SkyPatcherOpShape.Compound => $"compound/runtime op — no single static field value{NoteSuffix(op)}",
        _ => $"runtime-derived — the engine computes the result at load{NoteSuffix(op)}",
    };
    static string NoteSuffix(SkyPatcherOpDef op) => op.Note is { Length: > 0 } n ? $" ({n})" : "";

    // ---- filters ----

    enum FilterVerdict { Match, NoMatch, Unresolved }

    /// <summary>The player actor — always excluded except from a lone bare primary filter naming it.</summary>
    static readonly FormKey PlayerFormKey = FaceGenCheck.PlayerFormKey;

    /// <summary>The filter base names the overlay evaluates without a field-map spec; shared with the filtermap coverage guard.</summary>
    public static readonly IReadOnlySet<string> BuiltInFilterBases = new HashSet<string>(StringComparer.Ordinal)
    {
        "filterByKeywords", "restrictToKeywords", "filterByEditorIdContains", "filterByNameContains",
        "filterByModNames", "skipRecordByModNameContains", "modNamesLastOverridden",
        "filterByMgefs", "filterByAlternateTextures",
    };

    /// <summary>Where the filter evaluators put their warnings, each prefixed with the file and line being evaluated; the dedupe key is scoped to the file.</summary>
    sealed class FilterWarnings
    {
        readonly List<string> _out;
        readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        string _file = "", _where = "";
        public FilterWarnings(List<string> sink) => _out = sink;
        public void At(string file, string where) { _file = file; _where = where; }
        public void Add(string key, string text) { if (_seen.Add(_file + "|" + key)) _out.Add($"{_where}: {text}"); }
    }

    static FilterVerdict EvaluateFilters(object record, FormKey fk, string? editorId,
        SkyPatcherRecordCatalog recordCatalog, RecordMap? fieldMap,
        IReadOnlyList<(SkyPatcherSegment seg, SkyPatcherKeyClass cls)> filters, IFormResolver resolver,
        FilterWarnings warn)
    {
        var mutagenRecordType = fieldMap?.RecordType;

        // The player is excluded from every line except a lone bare primary filter naming it; hasPlugins gates the LINE, not the record, so it does not count as a record filter.
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

        // No filter set → every record of the type is patched.
        if (filters.Count == 0) return FilterVerdict.Match;

        bool any = false;
        foreach (var (seg, cls) in filters)
        {
            var f = cls.Filter!;
            var conn = cls.Connective ?? "";

            if (f.Kind == SkyPatcherFilterKind.NoFilter)
            {
                // The apply-all tokens are record-class scoped in the shared leveledList folder: noFilterLL means every item list, noFilterLLNPC every character list.
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

            var verdict = EvaluateOneFilter(record, fk, editorId, cls, seg, conn, fieldMap, resolver, warn);
            if (verdict != FilterVerdict.Match) return verdict;
            any = true;
        }
        return any ? FilterVerdict.Match : FilterVerdict.NoMatch;
    }

    /// <summary>One non-primary, non-gate filter segment: the built-in families first, then the field map's per-record <see cref="FilterSpec"/>, which overrides a built-in of the same name; anything else is Unresolved.</summary>
    static FilterVerdict EvaluateOneFilter(object record, FormKey fk, string? editorId,
        SkyPatcherKeyClass cls, SkyPatcherSegment seg, string conn, RecordMap? fieldMap,
        IFormResolver resolver, FilterWarnings warn)
    {
        var spec = fieldMap?.Filters.GetValueOrDefault(cls.BaseKey);
        if (spec is { IsUnmapped: true })
        {
            warn.Add($"fu:{cls.BaseKey}", $"filter '{cls.BaseKey}' has no static evaluation — {spec.Unmapped}");
            return FilterVerdict.Unresolved;
        }
        if (spec is not null)
            return EvaluateSpec(record, cls, seg, conn, spec, fieldMap!, resolver, warn);

        switch (cls.BaseKey)
        {
            case "filterByKeywords":
            case "restrictToKeywords":   // post-match narrowing; for ONE record that's the same verdict
                return KeywordVerdict(ReadEngine.KeywordKeys(record), seg, cls.BaseKey, conn, resolver, warn);

            case "filterByEditorIdContains":
                return ContainsVerdict(seg, conn, editorId ?? "") ? FilterVerdict.Match : FilterVerdict.NoMatch;

            case "filterByNameContains":
                return ContainsVerdict(seg, conn, RecordName(record) ?? "") ? FilterVerdict.Match : FilterVerdict.NoMatch;

            case "filterByModNames":
            {
                // Read as the record's DEFINING master (FormKey.ModKey) — an assumption; the verdict is Unresolved when the winning-override reading would disagree.
                var origin = fk.ModKey.FileName.String;
                bool inSet = seg.Values.Any(v => v.Raw.Equals(origin, StringComparison.OrdinalIgnoreCase));
                if (resolver.WinnerPluginOf(fk) is { } winner
                    && seg.Values.Any(v => v.Raw.Equals(winner, StringComparison.OrdinalIgnoreCase)) != inSet)
                {
                    warn.Add($"mn:{FormIdToken.Of(fk)}", $"filterByModNames{conn}: the record's defining master ('{origin}') and winning override ('{winner}') disagree on membership — which one the DLL tests is unverified, so whether the line applies is UNRESOLVED.");
                    return FilterVerdict.Unresolved;
                }
                bool ok = conn is "Excluded" or "Exclude" ? !inSet : inSet;
                return ok ? FilterVerdict.Match : FilterVerdict.NoMatch;
            }
            case "skipRecordByModNameContains":
            {
                // Skip when the record's source mod name contains a listed substring — an inverted filter.
                var origin = fk.ModKey.FileName.String;
                bool hit = seg.Values.Any(v => origin.Contains(v.Raw, StringComparison.OrdinalIgnoreCase));
                return hit ? FilterVerdict.NoMatch : FilterVerdict.Match;
            }
            case "modNamesLastOverridden":
            {
                // "Skip records whose LAST OVERRIDE is from a named mod" — the load-order winner's plugin; documented only in the Excluded spelling.
                if (conn is not ("Excluded" or "Exclude"))
                {
                    warn.Add($"ovc:{cls.BaseKey}{conn}", $"filter '{cls.BaseKey}{conn}' — only the Excluded spelling is documented; whether this connective yields or selects is UNRESOLVED.");
                    return FilterVerdict.Unresolved;
                }
                var winner = resolver.WinnerPluginOf(fk);
                if (winner is null)
                {
                    warn.Add($"ov:{FormIdToken.Of(fk)}", $"modNamesLastOverridden{conn}: could not resolve the winning override plugin of {FormIdToken.Of(fk)} — whether the line yields is UNRESOLVED.");
                    return FilterVerdict.Unresolved;
                }
                bool hit = seg.Values.Any(v => v.Raw.Equals(winner, StringComparison.OrdinalIgnoreCase));
                return hit ? FilterVerdict.NoMatch : FilterVerdict.Match;
            }
            case "filterByMgefs":
            {
                // Crosscutting attached-effect match: the Effects array's BaseEffect links.
                var mine = EntryKeys(record, new[] { "Effects" }, "BaseEffect");
                return FormSetVerdict(mine, seg, cls.BaseKey, conn, "MagicEffect", resolver, warn);
            }
            case "filterByAlternateTextures":
            {
                // Items carrying a given texture set: the model's alternate-texture entries' NewTexture.
                var mine = EntryKeys(record, new[] { "Model", "AlternateTextures" }, "NewTexture");
                return FormSetVerdict(mine, seg, cls.BaseKey, conn, "TextureSet", resolver, warn);
            }
            default:
                // Neither built-in nor mapped — a coverage gap the filtermap guard should have caught, named here too.
                warn.Add($"nf:{cls.BaseKey}", $"filter '{cls.BaseKey}' has no evaluation (neither built-in nor in the filter map) — whether lines carrying it apply is UNRESOLVED (a coverage gap; report it).");
                return FilterVerdict.Unresolved;
        }
    }

    // ---- the map-driven evaluations -----------------------------------------------------------------

    static FilterVerdict EvaluateSpec(object record, SkyPatcherKeyClass cls, SkyPatcherSegment seg,
        string conn, FilterSpec spec, RecordMap fieldMap, IFormResolver resolver,
        FilterWarnings warn)
    {
        bool excluded = conn is "Excluded" or "Exclude";
        switch (spec.Eval)
        {
            case SkyPatcherFilterEval.FormEquals:
            {
                // Single-valued form field against a value list: any-of, with no early break so every unresolvable token still warns once.
                var current = spec.Paths.Select(p => TryLeafToken(record, p)).Where(t => t is not null).ToList();
                bool matched = false;
                foreach (var v in seg.Values)
                {
                    var k = ResolveFormValue(v, spec.FormType, resolver);
                    if (k is null) { WarnUnresolvableForm(v, cls.BaseKey, conn, spec, warn); continue; }
                    matched |= current.Any(t => string.Equals(t, k.Value.ToString(), StringComparison.OrdinalIgnoreCase));
                }
                return (excluded ? !matched : matched) ? FilterVerdict.Match : FilterVerdict.NoMatch;
            }
            case SkyPatcherFilterEval.FormInList:
            {
                var segs = SplitPath(spec.Paths[0]);
                var mine = spec.KeyPath is null ? TryFormLinkKeys(record, segs) : EntryKeys(record, segs, spec.KeyPath);
                if (spec.EidSubstring)
                    return EidAwareListVerdict(mine, seg, cls.BaseKey, conn, spec, resolver, warn);
                return FormSetVerdict(mine, seg, cls.BaseKey, conn, spec.FormType, resolver, warn);
            }
            case SkyPatcherFilterEval.EnumEquals:
            {
                // A token naming no member of the leaf enum can never match, which is silently WRONG under Excluded, so it warns and goes Unresolved.
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
                        warn.Add($"ee:{cls.BaseKey}:{v.Raw}", $"filter '{cls.BaseKey}' — '{v.Raw}' is not a {enumType.Name} member (no valueMap match either); whether the line applies is UNRESOLVED.");
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
                    warn.Add($"fb:{cls.BaseKey}:{raw}", $"filter '{cls.BaseKey}={raw}' — not a boolean; whether the line applies is UNRESOLVED.");
                    return FilterVerdict.Unresolved;
                }
                var (bits, enumType) = FlagLeaf(record, spec.Paths[0]);
                if (enumType is null || !TryParseEnumMember(enumType, spec.Flag!, out var bit))
                    return UnresolvedLeaf(cls.BaseKey, spec.Paths[0], warn);
                bool set = (bits & bit) != 0;
                if (spec.Invert) set = !set;
                return set == want.Value ? FilterVerdict.Match : FilterVerdict.NoMatch;
            }
            case SkyPatcherFilterEval.FlagAnyOf:
            {
                var (bits, enumType) = FlagLeaf(record, spec.Paths[0]);
                if (enumType is null) return UnresolvedLeaf(cls.BaseKey, spec.Paths[0], warn);
                var hits = new List<bool>();
                foreach (var v in seg.Values)
                {
                    var member = spec.ValueMap?.GetValueOrDefault(v.Raw) ?? v.Raw;
                    if (!TryParseEnumMember(enumType, member, out var bit))
                    {
                        warn.Add($"fa:{cls.BaseKey}:{v.Raw}", $"filter '{cls.BaseKey}' — flag '{v.Raw}' is not a member of the {spec.Paths[0]} enum (no valueMap match either); whether the line applies is UNRESOLVED.");
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
                    warn.Add($"g:{raw}", $"filter '{cls.BaseKey}={raw}' — expected male|female; whether the line applies is UNRESOLVED.");
                    return FilterVerdict.Unresolved;
                }
                // A TRAITS-templated NPC takes its gender from the template actor, so the own-record Female bit is not authoritative.
                var (tBits, tType) = FlagLeaf(record, "Configuration.TemplateFlags");
                if (tType is not null && TryParseEnumMember(tType, "Traits", out var traitsBit) && (tBits & traitsBit) != 0)
                {
                    warn.Add($"gt:{cls.BaseKey}", $"filter '{cls.BaseKey}' — this NPC templates its TRAITS (gender comes from the template actor, not this record); whether the line applies is UNRESOLVED.");
                    return FilterVerdict.Unresolved;
                }
                var (bits, enumType) = FlagLeaf(record, spec.Paths[0]);
                if (enumType is null || !TryParseEnumMember(enumType, "Female", out var bit))
                    return UnresolvedLeaf(cls.BaseKey, spec.Paths[0], warn);
                return ((bits & bit) != 0) == female ? FilterVerdict.Match : FilterVerdict.NoMatch;
            }
            case SkyPatcherFilterEval.PcLevelMult:
            {
                var raw = seg.Values.Count > 0 ? seg.Values[0].Raw : "";
                bool? want = ParseBoolToken(raw);
                if (want is null)
                {
                    warn.Add($"pl:{cls.BaseKey}:{raw}", $"filter '{cls.BaseKey}={raw}' — not a boolean; whether the line applies is UNRESOLVED.");
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
                    warn.Add($"ds:{cls.BaseKey}:{donor}", $"filter '{cls.BaseKey}' — could not read '{spec.Paths[0]}' off {FormIdToken.Of(donor.Value)}'s winner; whether the line applies is UNRESOLVED.");
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
                    warn.Add($"dk:{cls.BaseKey}:{donor}", $"filter '{cls.BaseKey}' — could not read the keywords of {FormIdToken.Of(donor.Value)}'s winner; whether the line applies is UNRESOLVED.");
                    return FilterVerdict.Unresolved;
                }
                return KeywordVerdict(mine, seg, cls.BaseKey, conn, resolver, warn);
            }
            case SkyPatcherFilterEval.NumericLess:
            {
                var tok = TryLeafToken(record, spec.Paths[0]);
                if (tok is null || !double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out var current))
                    return FilterVerdict.NoMatch;   // absent/non-numeric leaf can't be "less than N"
                var raw = seg.Values.Count > 0 ? seg.Values[0].Raw : "";
                if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
                {
                    warn.Add($"nl:{cls.BaseKey}:{raw}", $"filter '{cls.BaseKey}={raw}' — not a number; whether the line applies is UNRESOLVED.");
                    return FilterVerdict.Unresolved;
                }
                return current < n ? FilterVerdict.Match : FilterVerdict.NoMatch;
            }
            case SkyPatcherFilterEval.BipedSlots:
            {
                // Absent BodyTemplate reads as "occupies no slots" — a fact about the record, not a failure.
                var (bits, _) = FlagLeaf(record, spec.Paths[0]);
                var hits = new List<bool>();
                foreach (var v in seg.Values)
                {
                    if (!int.TryParse(v.Raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx) || idx is < 0 or > 31)
                    {
                        warn.Add($"bs:{cls.BaseKey}:{v.Raw}", $"filter '{cls.BaseKey}' — '{v.Raw}' is not a biped slot INDEX (0–31; slot number − 30); whether the line applies is UNRESOLVED.");
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
                // Unreachable while the FilterSpec parser and this switch agree on the eval kinds; named so a drift cannot skip silently.
                warn.Add($"ue:{cls.BaseKey}", $"filter '{cls.BaseKey}' — eval kind '{spec.Eval}' has no evaluator; whether the line applies is UNRESOLVED (report it).");
                return FilterVerdict.Unresolved;
        }
    }

    // ---- filter helpers ------------------------------------------------------------------------------

    /// <summary>THE connective rule — bare = AND (gated by <paramref name="bareGuard"/>), Or = any, Excluded/Exclude = none; the one copy every list-membership evaluator rides.</summary>
    static bool ConnectiveVerdict(string conn, IReadOnlyList<bool> hits, bool bareGuard = true) => conn switch
    {
        "Or" => hits.Any(h => h),
        "Excluded" or "Exclude" => !hits.Any(h => h),
        _ => bareGuard && hits.All(h => h),
    };

    /// <summary>The keyword-family verdict — <see cref="FormSetVerdict"/> scoped to Keyword; a null set means the type has no readable keyword list.</summary>
    static FilterVerdict KeywordVerdict(IReadOnlyList<FormKey>? mine, SkyPatcherSegment seg,
        string baseKey, string conn, IFormResolver resolver, FilterWarnings warn)
        => mine is null ? FilterVerdict.Unresolved
            : FormSetVerdict(mine, seg, baseKey, conn, "Keyword", resolver, warn, noun: "keyword");

    /// <summary>List-membership verdict over the record's own attached forms — bare = all listed present, Or = any, Excluded = none; a listed form resolving to nothing in the active order counts as not-attached and is surfaced once per token.</summary>
    static FilterVerdict FormSetVerdict(IReadOnlyList<FormKey> mine, SkyPatcherSegment seg,
        string baseKey, string conn, string? formType, IFormResolver resolver,
        FilterWarnings warn, string noun = "form")
    {
        var wanted = new List<FormKey>();
        int unresolved = 0;
        foreach (var v in seg.Values)
        {
            var k = ResolveFormValue(v, formType, resolver);
            if (k is null)
            {
                unresolved++;
                warn.Add($"fs:{baseKey}:{v.Raw}", $"{noun} '{v.Raw}' (in a {baseKey}{conn}) resolves to nothing in the active order — treated as attached to no record.");
            }
            else wanted.Add(k.Value);
        }
        return ConnectiveVerdict(conn, wanted.Select(mine.Contains).ToList(), bareGuard: unresolved == 0)
            ? FilterVerdict.Match : FilterVerdict.NoMatch;
    }

    /// <summary>filterByArmorAddons' documented "EditorID substring ok": a value resolving to a form matches by key, one that does not is a substring against each attached form's winner EditorID.</summary>
    static FilterVerdict EidAwareListVerdict(IReadOnlyList<FormKey> mine, SkyPatcherSegment seg,
        string baseKey, string conn, FilterSpec spec, IFormResolver resolver,
        FilterWarnings warn)
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

    /// <summary>A leaf token treating any navigation or absence failure as null — filters read optional structure as "not there", never a throw.</summary>
    static string? TryLeafToken(object record, string path)
    {
        try { return LeafToken(record, SplitPath(path)); }
        catch { return null; }
    }

    /// <summary>The FormKeys of a struct list's key sub-field; an absent list reads as empty.</summary>
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

    /// <summary>ONE navigation for the flag-family evaluators: the leaf's raw bits and its enum type, so a value list walks the leaf once; absent structure reads as (0, null).</summary>
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

    /// <summary>The enum type of a leaf (null = not navigable or not an enum) — EnumEquals' unknown-token recognizer.</summary>
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

    /// <summary>The named-warning Unresolved for a flag or enum leaf that cannot be resolved on this record.</summary>
    static FilterVerdict UnresolvedLeaf(string baseKey, string path, FilterWarnings warn)
    {
        warn.Add($"ul:{baseKey}:{path}", $"filter '{baseKey}' — could not resolve '{path}' (or its member) on this record; whether the line applies is UNRESOLVED.");
        return FilterVerdict.Unresolved;
    }

    static bool? ParseBoolToken(string raw)
        => raw.Equals("true", StringComparison.OrdinalIgnoreCase) || raw.Equals("yes", StringComparison.OrdinalIgnoreCase) || raw == "1" ? true
         : raw.Equals("false", StringComparison.OrdinalIgnoreCase) || raw.Equals("no", StringComparison.OrdinalIgnoreCase) || raw == "0" ? false
         : null;

    static void WarnUnresolvableForm(SkyPatcherValue v, string baseKey, string conn, FilterSpec spec,
        FilterWarnings warn)
    {
        warn.Add($"fe:{baseKey}:{v.Raw}", $"form '{v.Raw}' (in a {baseKey}{conn}) resolves to nothing in the active order{(spec.FormType is null ? "" : $" among {spec.FormType} winners")} — treated as matching no record.");
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

    /// <summary>SkyPatcher <c>Plugin|FormID</c> to a Mutagen FormKey: a full load-indexed ESL FormID keeps only its 12-bit local id, anything else the low 24 bits.</summary>
    public static bool TryFormKey(FormAddress a, out FormKey fk)
    {
        fk = default;
        if (a.Plugin is null || a.FormId is null) return false;
        if (!ModKey.TryFromNameAndExtension(a.Plugin, out var mk)) return false;
        // The normalization lives in one tested home (FormIdRange), shared with the SKSE config audit.
        fk = new FormKey(mk, FormIdRange.LocalObjectId(a.FormId.Value));
        return true;
    }

    // ---- ops ----

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
                // An empty value must be LOUD like every sibling semantic — zero items would otherwise iterate zero times.
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
                // One component of a P3* point: a token splice plus an engine Set of the whole value, no hand-rolled struct rebuild.
                var raw = seg.Values.Count > 0 ? seg.Values[0].Raw : "";
                if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var num))
                { warnings.Add($"{where}: '{seg.Key}={raw}' — not a number; skipped."); return; }
                var comp = map.Component ?? throw new InvalidOperationException($"'{seg.Key}' mapping has no component");
                var before = LeafToken(record, segs);
                var parts = before?.Split(',');
                if (parts is null || comp >= parts.Length)
                { warnings.Add($"{where}: '{seg.Key}' — '{map.Path}' is absent or not a {comp + 1}+-component point ('{before ?? "<unreadable>"}'); skipped."); return; }
                // An integral component rounds fractional input away-from-zero, the assumption FormatNumericFor carries; the ctor-parameter type is the recognizer.
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
                    // A dot-less token can never be a valid .nif path, so an unresolvable donor fails LOUD rather than being written verbatim as a model path.
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
                // 'none' is a LEGAL token on these ops, meaning leave unchanged — a visible no-op, not a warning.
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
                // One channel of a whole-value Color: the P3 vector-component token splice, alpha preserved.
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
                // The polymorphic Teaches swap is the engine's compose-Set, the same path the record-authoring surface uses.
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

    /// <summary>The numeric value of one dict entry (null = absent key, absent dict, or non-numeric), read through the non-generic IDictionary view.</summary>
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

        // null clears the field through the engine's Remove; a required link refuses LOUD there and is surfaced as a warning.
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
        else if (map.FormType is not null)
        {
            // A form-valued target must resolve to a form: a bare non-form string reaching the engine throws "Malformed FormKey" and mislabels a VALID INI as broken.
            if (ResolveFormValue(v, map.FormType, resolver) is { } rk) token = rk.ToString();
            else
            {
                // A value that fails as FormType but resolves as the op's donorType is a runtime donor-copy the static model does not cover — named honestly, never a thrown malformed-FormKey.
                if (map.DonorType is { } dt && v.Address is not { IsFormId: true }
                    && resolver.ResolveEditorId(v.Raw, dt) is not null)
                    warnings.Add($"{where}: '{opKey}={v.Raw}' — '{v.Raw}' is a {dt}, not a {map.FormType}. " +
                        $"'{opKey}=<donor {dt}>' copies the donor's {map.Path} at runtime — a donor-copy houseCARL does not " +
                        $"statically model (like copyVisualStyle); the INI line is valid, but its post-state is NOT computed here.");
                else
                    warnings.Add($"{where}: '{opKey}={v.Raw}' — does not resolve to a {map.FormType} in the active order; " +
                        $"not applied (named gap, never a malformed-form guess).");
                return;
            }
        }
        else token = v.Raw;                                             // scalar / enum member on a non-form field (ignore-case coercion downstream)

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
                    // A conditional remove is NOT modeled — replaying it unconditionally would be a silently-wrong post-state, so the item skips LOUD.
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
                    // changeCobjsCount=form~count sets a matching entry's count to N; the documented 'null' form means EVERY entry.
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

    // ---- value + navigation helpers ----

    static string[] SplitPath(string path) => path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    static string? LeafToken(object record, string[] segs)
    {
        var r = ReadEngine.ReadLeaf(record, segs);
        return r.HasValue ? r.Token : null;
    }

    /// <summary>Navigate to the leaf's parent and PropertyInfo for READ access only; every mutation goes back through <see cref="WriteEngine.ApplyVerb"/>.</summary>
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

    /// <summary>Write a computed flag-bit value back through the verb engine as an enum leaf Set — the one mutation path.</summary>
    static void SetEnumBits(object record, RecordMap fieldMap, string[] segs, ulong bits)
        => WriteEngine.ApplyVerb(record, new WriteRequest
        { RecordType = fieldMap.RecordType, Path = segs, Verb = "Set", Value = bits.ToString(CultureInfo.InvariantCulture) });

    /// <summary>Format a computed stateful result for the leaf's actual type: integral leaves round to nearest (a declared assumption), floats keep the fraction.</summary>
    static string FormatNumericFor(object record, string[] segs, double result)
    {
        var (parent, leaf) = Navigate(record, segs);
        return IsIntegral(leaf.PropertyType)
            ? Math.Round(result, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture)
            : result.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>An integral numeric leaf or component (nullable unwrapped) — the one recognizer behind the declared fractional-rounding assumption.</summary>
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

    /// <summary>A sub-arg that IS the unambiguous <c>Plugin|FormID</c> form, via <see cref="SkyPatcherParse.TryParseAddress"/> rather than a '|' sniff.</summary>
    static bool LooksLikeForm(string raw) => SkyPatcherParse.TryParseAddress(raw) is { IsFormId: true };

    static string FormHint(SkyPatcherValue v, OpMap map)
        => v.Address is { IsFormId: true }
            ? "plugin name unparseable"
            : $"EditorID '{v.Raw}' not found{(map.FormType is null ? "" : $" among {map.FormType} winners")}";

    /// <summary>Sub-args of one comma-item: an '='-packed op splits on the FIRST '=' and both sides contribute; otherwise the tokenizer's ~-split sub-args are used as-is.</summary>
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

    /// <summary>A formlink list's FormKeys (null when the path is not a formlink list; absent reads as empty), via the shared <see cref="ReadEngine.FormLinkKeys"/>.</summary>
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
