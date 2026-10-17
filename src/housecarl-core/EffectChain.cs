using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlCore;

/// <summary>The effect-chain resolver: given a MagicEffect (MGEF), find every record that APPLIES it and the
/// magnitude/area/duration of the matching effect entry. Two seams, mirroring the where= predicate:
/// <see cref="Match"/> is pure over an in-hand body, <see cref="Resolve"/> is the end-to-end service path. Scope is
/// the five records the library models with an <c>Effects</c> list — every effect-bearing record by construction,
/// since the element type is the same <see cref="IEffectGetter"/> across all five.</summary>
public static class EffectChain
{
    /// <summary>One matching effect entry on a carrier: its index in the carrier's Effects list, the list size, and
    /// that entry's magnitude/area/duration.</summary>
    public readonly record struct EffectHit(int Index, int Count, float Magnitude, int Area, int Duration);

    /// <summary>The effect-bearing getter Types — the scan scope, listed once so the scan default and the
    /// narrow-validation share one source. A non-member type-narrow is refused, never a silent empty scan.</summary>
    public static readonly IReadOnlyList<Type> CarrierTypes = new[]
    {
        typeof(ISpellGetter), typeof(IObjectEffectGetter), typeof(IIngestibleGetter),
        typeof(IScrollGetter), typeof(IIngredientGetter),
    };

    /// <summary>The carrier's Effects list if <paramref name="body"/> is one of the five effect-bearing records, else
    /// null. An explicit switch over the known set, not reflection, so a sixth in a future library version is a
    /// surfaced boundary rather than a silent guess.</summary>
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
    /// its magnitude/area/duration. An effect with an unset BaseEffect is skipped. Pure.</summary>
    public static IReadOnlyList<EffectHit> Match(IMajorRecordGetter body, FormKey mgef)
    {
        var effects = EffectsOf(body);
        if (effects is null || effects.Count == 0) return Array.Empty<EffectHit>();
        List<EffectHit>? hits = null;
        for (int i = 0; i < effects.Count; i++)
        {
            var eff = effects[i];
            if (eff.BaseEffect.FormKey != mgef) continue;   // FormKey.Null (an unset base) never equals a real MGEF
            // Data is modeled nullable: a matching effect with absent data is reported at zero rather than throwing.
            var d = eff.Data;
            (hits ??= new List<EffectHit>()).Add(new EffectHit(i, effects.Count, d?.Magnitude ?? 0f, d?.Area ?? 0, d?.Duration ?? 0));
        }
        return hits ?? (IReadOnlyList<EffectHit>)Array.Empty<EffectHit>();
    }

    /// <summary>Resolve the effect chain for <paramref name="mgef"/> over the order <paramref name="resolver"/> holds,
    /// scanning the winner bodies of <paramref name="scope"/>. One capture, per-record fault isolation, holds nothing.
    /// The typed-match gate runs first, so a bad target never reads as a silent "0 carriers"; a valid but unused MGEF
    /// returns a clean zero result with no error.</summary>
    public static EffectChainResult Resolve(LoadOrderResolver resolver, FormKey mgef, IReadOnlyList<Type> scope, int limit)
        => ResolveMany(resolver, new[] { mgef }, scope, limit)[0];

    /// <summary>The chains of several MGEFs off one capture: the seeds read through the door, then one pass over the carriers.</summary>
    public static EffectChainResult[] ResolveMany(LoadOrderResolver resolver, IReadOnlyList<FormKey> mgefs, IReadOnlyList<Type> scope,
                                                  int limit, CancellationToken ct = default)
    {
        var view = resolver.Capture();
        // Post-capture refusals are STAMPED: they are answers about this build. Only the service's pre-capture
        // type-narrow gate refuses without an epoch.
        EffectChainResult FailStamped(string msg) => EffectChainResult.Fail(msg) with { Stamp = view.Stamp };
        var results = new EffectChainResult[mgefs.Count];

        // --- typed-match gate: each target must be an MGEF; only its type and EditorID outlive its group. ---
        var seeds = new Dictionary<FormKey, (bool Mgef, string Name)>();
        var gateUnread = new Dictionary<string, PluginUnreadableException>(StringComparer.OrdinalIgnoreCase);
        using (var session = resolver.OpenSession())
            OrderedGather.ForEachGroup(view, session, mgefs, (bodies, _) =>
            {
                foreach (var (fk, b) in bodies)
                    seeds[fk] = b is IMagicEffectGetter mr ? (true, mr.EditorID ?? "<none>") : (false, RecordNaming.StripOverlay(b.GetType().Name));
            }, gateUnread, ct);
        // Each live seed's FormKey to its indices, so a carrier's effect entry is tested against every seed at once.
        var seedAt = new Dictionary<FormKey, List<int>>();
        for (int i = 0; i < mgefs.Count; i++)
        {
            var mgef = mgefs[i];
            var w = view.ResolveWinner(mgef);
            if (w is null)
                results[i] = FailStamped(
                    $"no record with FormID {mgef} in the load order. The chain form needs a MagicEffect (MGEF) the active order defines.");
            else if (!seeds.TryGetValue(mgef, out var seed))
                results[i] = FailStamped(gateUnread.TryGetValue(w.Value.WinnerPlugin, out var fault)
                    ? $"{mgef} could not be read: {fault.Message}"
                    : $"winner '{w.Value.WinnerPlugin}' did not yield {mgef} on fetch — cannot confirm it is a MagicEffect.");
            else if (!seed.Mgef)
                results[i] = FailStamped(
                    $"{mgef} resolves to a {seed.Name}, not a MagicEffect — the chain form " +
                    $"needs an MGEF. (To find what references an arbitrary record, use {ToolNames.Records} references=[the FormID] " +
                    "— unbounded off the reverse-reference index, or with types= or plugins= for a cheaper bounded scan.)");
            else if (seedAt.TryGetValue(mgef, out var at)) at.Add(i);
            else seedAt[mgef] = new List<int> { i };
        }
        var live = seedAt.Values.SelectMany(a => a).ToList();
        if (live.Count == 0) return results;

        // --- scan the winner bodies of the scope once; collect every matching effect entry of every seed. ---
        var rows = mgefs.Select(_ => new List<EffectChainRow>()).ToArray();
        var total = new int[mgefs.Count];
        var unscannable = new int[mgefs.Count];
        var samples = mgefs.Select(_ => new List<string>()).ToArray();
        void Skip(int i, FormKey fk, Exception ex)
        {
            unscannable[i]++;
            if (samples[i].Count < 3) samples[i].Add($"{FormIdToken.Of(fk)} — {ex.GetType().Name}: {ex.Message}");
        }
        // A plugin that cannot be opened now wins carriers this scan cannot see; collected so the note names the gap.
        var unreadable = new List<PluginUnreadableException>();
        try
        {
            foreach (var (fk, _, body) in view.WinnerRecordsOfType(scope, unreadable))
            {
                ct.ThrowIfCancellationRequested();
                // One walk of the carrier's effects; each seed's hits are held until the walk ends, so a fault drops
                // that seed's hits on this carrier and counts it unscannable, as a per-seed Match did.
                Dictionary<FormKey, List<EffectHit>>? hits = null;
                Dictionary<FormKey, Exception>? faults = null;
                Exception? whole = null;
                try
                {
                    var effects = EffectsOf(body) ?? Array.Empty<IEffectGetter>();
                    for (int e = 0; e < effects.Count; e++)
                    {
                        var eff = effects[e];
                        var mgef = eff.BaseEffect.FormKey;
                        if (!seedAt.ContainsKey(mgef) || faults?.ContainsKey(mgef) == true) continue;
                        try
                        {
                            var d = eff.Data;   // absent data is reported at zero; a Data fault marks only this seed
                            hits ??= new();
                            if (!hits.TryGetValue(mgef, out var l)) hits[mgef] = l = new List<EffectHit>();
                            l.Add(new EffectHit(e, effects.Count, d?.Magnitude ?? 0f, d?.Area ?? 0, d?.Duration ?? 0));
                        }
                        catch (Exception ex) { (faults ??= new())[mgef] = ex; hits?.Remove(mgef); }
                    }
                }
                catch (Exception ex) { whole = ex; }   // the list itself faulted: every seed not already faulted fails here
                if (faults is not null)
                    foreach (var (mgef, ex) in faults)
                        foreach (var i in seedAt[mgef]) Skip(i, fk, ex);
                if (whole is not null)
                {
                    foreach (var i in live)
                        if (faults is null || !faults.ContainsKey(mgefs[i])) Skip(i, fk, whole);
                    continue;
                }
                if (hits is null) continue;
                string type, winner; string? eid;
                try { type = RecordNaming.StripOverlay(body.GetType().Name); winner = view.ResolveWinner(fk)?.WinnerPlugin ?? "?"; eid = body.EditorID; }
                catch (Exception ex) { foreach (var mgef in hits.Keys) foreach (var i in seedAt[mgef]) Skip(i, fk, ex); continue; }
                foreach (var (mgef, list) in hits)
                    foreach (var i in seedAt[mgef])
                        foreach (var h in list)
                        {
                            total[i]++;
                            if (rows[i].Count < limit)
                                rows[i].Add(new EffectChainRow(fk, type, eid, winner, h.Index, h.Count, h.Magnitude, h.Area, h.Duration));
                        }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        // Anything escaping the stream itself gets a NAMED failure, never the MCP layer's generic message.
        catch (Exception ex)
        {
            foreach (var i in live) results[i] = FailStamped($"scan aborted: {ex.GetType().Name}: {ex.Message}");
            return results;
        }

        foreach (var i in live)
        {
            string? scanNote = unscannable[i] == 0 ? null
                : $"note: {unscannable[i]} record instance(s) could not be scanned (Mutagen could not parse their content) and were skipped: "
                  + string.Join("; ", samples[i])
                  + (unscannable[i] > samples[i].Count ? $"; and {unscannable[i] - samples[i].Count} more" : "")
                  + $". Inspect one with {ToolNames.Records} formids=[the FormID] (per-field fault isolation applies).";

            string? coverageNote = unreadable.Count == 0 ? null
                : $"coverage gap: {unreadable.Count} plugin(s) could not be read, so any carrier they win is missing from this chain: "
                  + string.Join("; ", unreadable.Select(u => u.Message));
            scanNote = scanNote is null ? coverageNote
                     : coverageNote is null ? scanNote
                     : scanNote + " " + coverageNote;

            results[i] = new EffectChainResult(mgefs[i], seeds[mgefs[i]].Name, rows[i], total[i], total[i] > rows[i].Count, null, scanNote, view.Stamp)
                { UnreadPlugins = unreadable.Select(u => u.PluginName).ToList() };
        }
        return results;
    }
}

/// <summary>One carrier-row of an effect chain: the carrier record, its catalog type, editorid and load-order winner,
/// and the matching effect entry's position and magnitude/area/duration. One row per matching entry.</summary>
public sealed record EffectChainRow(
    FormKey Carrier, string Type, string? EditorId, string Winner,
    int EffectIndex, int EffectCount, float Magnitude, int Area, int Duration);

/// <summary>The result of <see cref="EffectChain.Resolve"/>: the resolved MGEF and its editorid, the carrier rows
/// capped at the caller's limit, the true total, the capped flag, an optional scan note, and — on the typed-match gate
/// — a recoverable <see cref="Error"/> with no rows.</summary>
public sealed record EffectChainResult(
    FormKey Mgef, string MgefEditorId, IReadOnlyList<EffectChainRow> Rows,
    int Total, bool Capped, string? Error, string? ScanNote,
    OrderStamp? Stamp = null)   // the build this answered from: its fingerprint and the plugins it lost to a load failure — set on success and on every post-capture refusal; null only on the service's pre-capture type-narrow gate
{
    /// <summary>That build's fingerprint, read through the stamp, so the result cannot carry an epoch without the
    /// health of the build it names.</summary>
    public string? Epoch => Stamp?.Epoch;

    /// <summary>Plugins the carrier scan could not open, by filename. A non-empty one bounds the answer — the render
    /// may not state a whole-order negative over it.</summary>
    public IReadOnlyList<string> UnreadPlugins { get; init; } = Array.Empty<string>();

    public bool Success => Error is null;
    public static EffectChainResult Fail(string error) =>
        new(default, "", Array.Empty<EffectChainRow>(), 0, false, error, null);
}
