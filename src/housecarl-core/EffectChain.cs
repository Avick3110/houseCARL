using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlCore;

/// <summary>
/// The effect-chain resolver: given a MagicEffect (MGEF), find every record that APPLIES it and the
/// magnitude/area/duration of the matching effect entry. It collapses the hand-trace
/// "cross_plugin_query references=&lt;MGEF&gt; type=SPEL → read each hit's Effects[].Data → keep the entry whose
/// BaseEffect is the MGEF, then repeat for ENCH/ALCH/SCRL/INGR" into one call. The inverse-by-magnitude of
/// references=: that says WHICH carriers reference the effect; this says which entry, and at what strength.
///
/// Two seams, mirroring the where= predicate (<see cref="FieldPredicate"/>):
///   • <see cref="Match"/> — PURE over an in-hand body (no resolver, no I/O), so a test can drive the real matcher
///     directly against a known-magnitude fixture.
///   • <see cref="Resolve"/> — the end-to-end service path (typed-match gate → winner-body scan → assemble),
///     drivable from a <see cref="LoadOrderResolver"/> built over synthetic plugins, so the gate is covered too.
///
/// Scope is the five records the library models with an <c>Effects</c> list — Spell/ObjectEffect/Ingestible/
/// Scroll/Ingredient (SPEL/ENCH/ALCH/SCRL/INGR). That is EVERY effect-bearing record by construction, not a chosen
/// subset: the element type is the SAME <see cref="IEffectGetter"/> across all five, so once
/// <see cref="EffectsOf"/> hands back the list, extraction is uniform and the only per-type code is the switch that
/// reaches the list.
/// </summary>
public static class EffectChain
{
    /// <summary>One matching effect entry on a carrier: its index in the carrier's Effects list and the list size
    /// (so a caller can render "[effect i/n]"), and the magnitude/area/duration of THAT entry — the trace's payload.</summary>
    public readonly record struct EffectHit(int Index, int Count, float Magnitude, int Area, int Duration);

    /// <summary>The effect-bearing getter Types — the scan scope. Every Skyrim record the library models with an
    /// Effects list; a query type-narrow is validated against this exact set (a non-member is refused, never a
    /// silent empty scan). Listed once here so both the scan default and the narrow-validation share one source.</summary>
    public static readonly IReadOnlyList<Type> CarrierTypes = new[]
    {
        typeof(ISpellGetter), typeof(IObjectEffectGetter), typeof(IIngestibleGetter),
        typeof(IScrollGetter), typeof(IIngredientGetter),
    };

    /// <summary>The carrier's Effects list if <paramref name="body"/> is one of the five effect-bearing records, else
    /// null. An explicit switch over the known set — NOT reflection: type-safe, and a sixth effect-bearing record in a
    /// future library version is then a deliberate, surfaced boundary a CI check catches, rather than a silent
    /// reflective guess that might mis-read an unrelated "Effects" member.</summary>
    public static IReadOnlyList<IEffectGetter>? EffectsOf(IMajorRecordGetter body) => body switch
    {
        ISpellGetter s => s.Effects,
        IObjectEffectGetter e => e.Effects,
        IIngestibleGetter i => i.Effects,
        IScrollGetter c => c.Effects,
        IIngredientGetter g => g.Effects,
        _ => null,
    };

    /// <summary>The effect entries of <paramref name="body"/> whose BaseEffect is <paramref name="mgef"/>, each with
    /// its magnitude/area/duration. Empty if the body carries no effects or none reference the MGEF. An effect with an
    /// unset BaseEffect (FormKey.Null) is skipped — never matched, never throws. Pure.</summary>
    public static IReadOnlyList<EffectHit> Match(IMajorRecordGetter body, FormKey mgef)
    {
        var effects = EffectsOf(body);
        if (effects is null || effects.Count == 0) return Array.Empty<EffectHit>();
        List<EffectHit>? hits = null;
        for (int i = 0; i < effects.Count; i++)
        {
            var eff = effects[i];
            if (eff.BaseEffect.FormKey != mgef) continue;   // FormKey.Null (an unset base) never equals a real MGEF
            // Data (the EFIT magnitude/area/duration) is required in practice but modeled nullable; a matching effect
            // with absent data is still a carrier — report it at zero (honest "no magnitude data"), never an NRE that
            // the scan's fault-isolation would then mis-count as an unparseable record.
            var d = eff.Data;
            (hits ??= new List<EffectHit>()).Add(new EffectHit(i, effects.Count, d?.Magnitude ?? 0f, d?.Area ?? 0, d?.Duration ?? 0));
        }
        return hits ?? (IReadOnlyList<EffectHit>)Array.Empty<EffectHit>();
    }

    /// <summary>Resolve the effect chain for <paramref name="mgef"/> over the order <paramref name="resolver"/> holds,
    /// scanning the winner bodies of <paramref name="scope"/> (a subset of <see cref="CarrierTypes"/>). One
    /// <see cref="LoadOrderResolver.Capture"/>; per-record fault isolation (one Mutagen-unparseable body is excluded +
    /// accounted, never aborts the call, never a silent skip); holds nothing.
    ///
    /// The typed-match gate runs FIRST, so a bad target never reads as a silent "0 carriers": the FormID must resolve
    /// to a MagicEffect. An absent FormID or a non-MGEF (a WEAP, an NPC_, …) fails loud with the actual type named; a
    /// VALID-but-unused MGEF returns a clean zero result (Error null) so the caller can tell "no carriers" from "bad
    /// id" by the absence of an error and the resolved <see cref="EffectChainResult.MgefEditorId"/> in the header.</summary>
    public static EffectChainResult Resolve(LoadOrderResolver resolver, FormKey mgef, IReadOnlyList<Type> scope, int limit)
    {
        var view = resolver.Capture();
        // Post-capture refusals are STAMPED: "not in the load order" / "not an MGEF" are answers ABOUT this build,
        // the same refusal contract read_record's stamped refusals follow. Only the service's pre-capture
        // type-narrow gate refuses without an epoch.
        EffectChainResult FailStamped(string msg) => EffectChainResult.Fail(msg) with { Stamp = view.Stamp };

        // --- typed-match gate: the target must be an MGEF. Cheap — one winner-body fetch off this view. ---
        var w = view.ResolveWinner(mgef);
        if (w is null)
            return FailStamped(
                $"no record with FormID {mgef} in the load order. The chain form needs a MagicEffect (MGEF) the active order defines.");
        string mgefEid;
        using (var session = resolver.OpenSession())
        {
            var mbody = view.GetRecord(session, w.Value.WinnerPlugin, mgef);
            if (mbody is null)
                return FailStamped(
                    $"winner '{w.Value.WinnerPlugin}' did not yield {mgef} on fetch — cannot confirm it is a MagicEffect.");
            if (mbody is not IMagicEffectGetter mr)
                return FailStamped(
                    $"{mgef} resolves to a {RecordNaming.StripOverlay(mbody.GetType().Name)}, not a MagicEffect — the chain form " +
                    $"needs an MGEF. (To find what references an arbitrary record, use {ToolNames.Records} references=[the FormID] " +
                    "— unbounded off the reverse-reference index, or with types= or plugins= for a cheaper bounded scan.)");
            mgefEid = mr.EditorID ?? "<none>";
        }

        // --- scan the winner bodies of the scope; collect every matching effect entry. ---
        var rows = new List<EffectChainRow>();
        int total = 0, unscannable = 0;
        var samples = new List<string>();
        // A plugin that cannot be opened now wins carriers this scan cannot see. Collected so the scan covers the
        // rest of the order and the note names the gap, rather than the whole call ending at the bad plugin.
        var unreadable = new List<PluginUnreadableException>();
        try
        {
            foreach (var (fk, _, body) in view.WinnerRecordsOfType(scope, unreadable))
            {
                // PER-RECORD FAULT ISOLATION (twin of the cross_plugin_query scan): Match lazily parses subrecord
                // content, so ONE record Mutagen can't parse is excluded + accounted, not an opaque whole-call abort.
                try
                {
                    var hits = Match(body, mgef);
                    if (hits.Count == 0) continue;
                    string type = RecordNaming.StripOverlay(body.GetType().Name);
                    string winner = view.ResolveWinner(fk)?.WinnerPlugin ?? "?";
                    foreach (var h in hits)
                    {
                        total++;
                        if (rows.Count < limit)
                            rows.Add(new EffectChainRow(fk, type, body.EditorID, winner, h.Index, h.Count, h.Magnitude, h.Area, h.Duration));
                    }
                }
                catch (Exception ex)
                {
                    unscannable++;
                    if (samples.Count < 3) samples.Add($"{fk} — {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        // Anything escaping the stream itself (a bad scope type, a build fault) gets a NAMED failure — never the
        // MCP layer's generic "An error occurred invoking …" as the terminal diagnostic for a data failure.
        catch (Exception ex) { return FailStamped($"scan aborted: {ex.GetType().Name}: {ex.Message}"); }

        string? scanNote = unscannable == 0 ? null
            : $"note: {unscannable} record instance(s) could not be scanned (Mutagen could not parse their content) and were skipped: "
              + string.Join("; ", samples)
              + (unscannable > samples.Count ? $"; and {unscannable - samples.Count} more" : "")
              + $". Inspect one with {ToolNames.Records} formids=[the FormID] (per-field fault isolation applies).";

        string? coverageNote = unreadable.Count == 0 ? null
            : $"coverage gap: {unreadable.Count} plugin(s) could not be read, so any carrier they win is missing from this chain: "
              + string.Join("; ", unreadable.Select(u => u.Message));
        scanNote = scanNote is null ? coverageNote
                 : coverageNote is null ? scanNote
                 : scanNote + " " + coverageNote;

        return new EffectChainResult(mgef, mgefEid, rows, total, total > rows.Count, null, scanNote, view.Stamp)
            { UnreadPlugins = unreadable.Select(u => u.PluginName).ToList() };
    }
}

/// <summary>One carrier-row of an effect chain: the carrier record, its catalog type + editorid + load-order winner,
/// and the matching effect entry's position (<paramref name="EffectIndex"/>/<paramref name="EffectCount"/>) and
/// magnitude/area/duration. A carrier that applies the MGEF in more than one entry yields one row per entry.</summary>
public sealed record EffectChainRow(
    FormKey Carrier, string Type, string? EditorId, string Winner,
    int EffectIndex, int EffectCount, float Magnitude, int Area, int Duration);

/// <summary>The result of <see cref="EffectChain.Resolve"/>: the resolved MGEF (+ its editorid, the typed-match proof
/// for the header), the carrier rows (capped at the caller's limit), the TRUE total, the capped flag, an optional
/// scan note, and — on the typed-match gate — a recoverable <see cref="Error"/> with no rows.</summary>
public sealed record EffectChainResult(
    FormKey Mgef, string MgefEditorId, IReadOnlyList<EffectChainRow> Rows,
    int Total, bool Capped, string? Error, string? ScanNote,
    OrderStamp? Stamp = null)   // the build this answered from: its fingerprint and the plugins it lost to a load failure — set on success and on every post-capture refusal; null only on the service's pre-capture type-narrow gate
{
    /// <summary>That build's fingerprint. Reads through the stamp, so the result cannot carry an epoch without the
    /// health of the build it names.</summary>
    public string? Epoch => Stamp?.Epoch;

    /// <summary>Plugins the carrier scan could not open, by filename. The scan covered the rest of the order, so a
    /// zero here is a real "nothing carries it" and a non-empty one bounds the answer — the render may not state a
    /// whole-order negative over it.</summary>
    public IReadOnlyList<string> UnreadPlugins { get; init; } = Array.Empty<string>();

    public bool Success => Error is null;
    public static EffectChainResult Fail(string error) =>
        new(default, "", Array.Empty<EffectChainRow>(), 0, false, error, null);
}
