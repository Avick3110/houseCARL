using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;

namespace HousecarlCore;

/// <summary>
/// The load-order integrity sweep (housecarl_check_errors — audit A1): the data-layer twin of the Creation Kit's
/// "Check For Errors" / xEdit's error check. For each plugin in scope it walks EVERY record's FormLinks (Mutagen's
/// own <see cref="IFormLinkContainerGetter.EnumerateFormLinks"/> — the by-construction link surface, the same one
/// cross_plugin_query references= rides) and reports three error classes:
///   • DANGLING — a non-null FormLink whose target NO plugin in the active order defines (<see cref="LoadOrderResolver.IndexView.ResolveWinner"/>
///     is null): a broken reference in the resolvable order. Engine-implicit forms (PlayerRef 000014, Player 000007 —
///     hardcoded refs the index can't resolve but that are never actually broken) are exempted via <see cref="EngineImplicit"/>,
///     so the standard player-state pattern doesn't false-flag (HCBR: 531/531 dangling all → 000014 before this exemption).
///   • MISSING MASTER — a plugin DECLARES a master that is not present in the active order
///     (<see cref="LoadOrderResolver.IndexView.ContainsPlugin"/> is false): the plugin's dependency is not installed /
///     enabled, the most common load-order break (and the root cause behind a cluster of that master's refs dangling).
///   • PARSE failures — per-record (a body whose link walk THROWS is excluded + accounted, never a silent skip — the
///     fault-isolation twin of the cross_plugin_query scan) and whole-plugin (plugins the index build could not parse,
///     surfaced via <see cref="LoadOrderResolver.IndexView.ExcludedPlugins"/>).
///
/// WHY NOT the master-TABLE diff the CK/xEdit also show (declared-vs-used) — the honest scope boundary, Q3:
///   • "used-but-undeclared master" is STRUCTURALLY UNDETECTABLE through Mutagen: a FormID's master-index byte is
///     decoded against the plugin's declared master list, so every FormKey Mutagen yields has a ModKey that is, by
///     construction, a declared master or the plugin itself — the "references a form from a plugin it does not master"
///     corruption lives in the RAW master-index byte, below what Mutagen models (adjacent to the mesh-byte residual).
///     Its OBSERVABLE effect — refs that no longer resolve — is caught as DANGLING. We do not claim to diagnose it.
///   • "declared-but-unused master" (xEdit's unused-master cleanup) is advisory only and a FormLink scan cannot prove
///     it (a master used solely by scripts or unmodeled refs would read as unused — a false positive). Deferred as a
///     named future item rather than shipped as an unprovable claim.
///
/// HONEST BOUNDARY (Q3 — the sweep claims exactly this, never more): it covers the FormLink-resolution / missing-master
/// / parse class. It does NOT verify navmesh or terrain SPATIAL integrity (the CrcHash / NavmeshGrid recompute — a known
/// Mutagen-delta residual), does NOT flag a REQUIRED field left null (needs per-field requiredness; a null FormLink is a
/// legal optional here), and does NOT list unused-master cleanup (see above). All named in the rendered footer so a
/// clean result is never read as byte-for-byte xEdit parity.
///
/// Composes existing primitives only — no new dependency (audit A1 "Verified 2026-06-25"): the per-plugin record stream
/// (<c>RecordsIn</c>), the O(1) resolution test (<c>ResolveWinner</c>), presence (<c>ContainsPlugin</c>), the declared-
/// master read (<c>DeclaredMasters</c>), and per-record fault isolation. Holds nothing past each yield (Option B).
/// </summary>
public static class ErrorCheck
{
    /// <summary>Sweep <paramref name="scope"/> (plugin filenames; null/empty = the whole active order minus excluded
    /// plugins) over the order <paramref name="resolver"/> holds. One <see cref="LoadOrderResolver.Capture"/> pins the
    /// whole sweep. <paramref name="limit"/> caps the number of dangling refs collected across the sweep (the true
    /// total is always counted); missing-master findings are few and never capped. A bad/excluded scope name fails
    /// LOUD (Q3) with no partial result.</summary>
    public static ErrorCheckResult Run(LoadOrderResolver resolver, IReadOnlyList<string>? scope, int limit)
    {
        var view = resolver.Capture();

        // --- resolve the plugin set to scan (Q3: a bad or excluded explicit scope name fails loud, never a silent skip). ---
        List<string> targets;
        if (scope is { Count: > 0 })
        {
            targets = new List<string>(scope.Count);
            foreach (var name in scope)
            {
                if (!view.ContainsPlugin(name))
                    return ErrorCheckResult.Fail($"plugin not in the load order: {name}");
                if (view.ExcludedPlugins.TryGetValue(name, out var why))
                    return ErrorCheckResult.Fail(
                        $"plugin '{name}' was excluded from this session because it could not be parsed ({why}) — fix or remove it upstream; it cannot be checked.");
                targets.Add(name);
            }
        }
        else
        {
            targets = new List<string>();
            foreach (var n in resolver.PluginNames)
                if (!view.ExcludedPlugins.ContainsKey(n)) targets.Add(n);
        }

        var reports = new List<PluginErrors>();
        int totalDangling = 0, totalMissing = 0, totalUnscannable = 0;
        int danglingBudget = limit;
        bool capped = false;

        foreach (var plugin in targets)
        {
            string? scanError = null;

            // Missing masters: a declared master not present in the active order (the dependency is not installed /
            // enabled). Read independently of the record walk so a record-walk fault still reports the master state.
            var missingMasters = new List<string>();
            try
            {
                foreach (var m in view.DeclaredMasters(plugin))
                    if (!view.ContainsPlugin(m)) missingMasters.Add(m);
            }
            catch (Exception ex) { scanError = $"could not read the master list: {ex.GetType().Name}: {ex.Message}"; }
            missingMasters.Sort(StringComparer.OrdinalIgnoreCase);

            var dangling = new List<DanglingRef>();
            int unscannable = 0;
            var unscannableSamples = new List<string>();

            try
            {
                foreach (var (fk, _, body, _) in view.RecordsIn(new[] { plugin }, null))
                {
                    // PER-RECORD FAULT ISOLATION (twin of the cross_plugin_query scan, HCBR-2026-06-09-03):
                    // EnumerateFormLinks lazily parses subrecord content, so ONE record Mutagen can't parse is excluded
                    // + accounted, never an opaque whole-call abort and never a silent skip (Q3).
                    try
                    {
                        if (body is not IFormLinkContainerGetter flc) continue;
                        foreach (var link in flc.EnumerateFormLinks())
                        {
                            var target = link.FormKey;
                            if (target.IsNull) continue;            // a null FormLink is a legal optional — not an error (see the class-doc boundary)
                            if (view.ResolveWinner(target) is not null) continue;   // resolves → fine
                            if (EngineImplicit.IsImplicit(target)) continue;        // engine-implicit (PlayerRef 000014 / Player 000007): the index can't resolve these hardcoded forms, but they are real, never dangling — same precise exemption the dialogue lints use (HCBR: was 531/531 false dangling → 000014)
                            totalDangling++;
                            if (danglingBudget > 0)
                            {
                                dangling.Add(new DanglingRef(fk, RecordNaming.StripOverlay(body.GetType().Name), body.EditorID, target));
                                danglingBudget--;
                            }
                            else capped = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        unscannable++;
                        if (unscannableSamples.Count < 3) unscannableSamples.Add($"{fk} — {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
            // The plugin enumeration itself faulting (a record that throws on top-level enumeration rather than on a
            // link walk) is NAMED per-plugin and the sweep continues — never the MCP layer's opaque transport error (Q3).
            catch (Exception ex)
            {
                scanError = (scanError is null ? "" : scanError + "; ")
                          + $"record enumeration aborted partway: {ex.GetType().Name}: {ex.Message}";
            }

            totalMissing += missingMasters.Count;
            totalUnscannable += unscannable;

            if (dangling.Count > 0 || missingMasters.Count > 0 || unscannable > 0 || scanError is not null)
                reports.Add(new PluginErrors(plugin, dangling, missingMasters, unscannable, unscannableSamples, scanError));
        }

        return new ErrorCheckResult(reports, targets.Count, totalDangling, totalMissing,
                                    totalUnscannable, capped, view.ExcludedPlugins, null);
    }
}

/// <summary>One broken reference: the SOURCE record (FormKey + catalog type + editorid) and the TARGET FormKey no
/// active plugin defines.</summary>
public sealed record DanglingRef(FormKey Source, string SourceType, string? SourceEditorId, FormKey Target);

/// <summary>Every error found in one plugin: its dangling references (capped across the sweep), the masters it declares
/// that are not present in the active order, the count + samples of records that could not be scanned, and — if the
/// plugin's own enumeration faulted — a <paramref name="ScanError"/>.</summary>
public sealed record PluginErrors(
    string Plugin,
    IReadOnlyList<DanglingRef> Dangling,
    IReadOnlyList<string> MissingMasters,
    int UnscannableRecords,
    IReadOnlyList<string> UnscannableSamples,
    string? ScanError);

/// <summary>The result of <see cref="ErrorCheck.Run"/>: the per-plugin reports (only plugins WITH findings; clean
/// plugins are counted in <paramref name="PluginsScanned"/> but omitted), the sweep totals, whether the dangling list
/// was capped at the caller's limit, the plugins the index build excluded as unparseable, and — on a Q3 scope error —
/// a recoverable <see cref="Error"/> with no reports.</summary>
public sealed record ErrorCheckResult(
    IReadOnlyList<PluginErrors> Reports,
    int PluginsScanned,
    int TotalDangling,
    int TotalMissingMasters,
    int TotalUnscannableRecords,
    bool Capped,
    IReadOnlyDictionary<string, string> ExcludedPlugins,
    string? Error)
{
    public bool Success => Error is null;
    public static ErrorCheckResult Fail(string error) =>
        new(Array.Empty<PluginErrors>(), 0, 0, 0, 0, false,
            new Dictionary<string, string>(), error);
}
