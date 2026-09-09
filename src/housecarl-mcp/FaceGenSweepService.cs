using HousecarlCore;

namespace HousecarlMcp;

/// <summary>
/// The facegen family's service entry: the load order, the VFS build and the MO2 composition, handed to
/// <see cref="FaceGenCheck"/> as ONE build each.
///
/// <para>Its own file rather than another domain inside <c>LoadOrderService</c>. What lives here is only what the
/// core sweep cannot know: which plugins a MOD FOLDER ships (the pole a stale-bake comparison runs against), and
/// the plugins=/exclude= resolution the other swept families already share.</para>
/// </summary>
public sealed partial class LoadOrderService
{
    /// <summary>Sweep the facegen join. <paramref name="plugins"/> takes the same active/off-order split the errors
    /// and scripts families take, through the same memo, so one call's families cannot disagree about which names
    /// resolved.</summary>
    public FaceGenCheckResult CheckFaceGen(IReadOnlyList<string>? plugins, int limit,
                                           IReadOnlyList<string>? formids = null, string? editoridContains = null,
                                           IReadOnlyList<string>? types = null, IReadOnlyList<string>? findings = null,
                                           bool countsOnly = false, IReadOnlyList<string>? exclude = null,
                                           SweepOffOrderMemo? offOrderMemo = null)
    {
        var (recordScope, scopeErr) = BuildSweepScope(formids, editoridContains, types);
        if (scopeErr is not null) return FaceGenCheckResult.Fail(scopeErr);
        if (!TryParseFaceGenClasses(findings, out var classes, out var classErr))
            return FaceGenCheckResult.Fail(classErr!);

        var resolver = Resolver;
        var view = resolver.Capture();

        bool wantsImplicit = exclude?.Any(v => (v ?? "").Trim().Equals(SweepExclusion.ImplicitToken, StringComparison.OrdinalIgnoreCase)) == true;
        var (implicitNames, implicitErr) = wantsImplicit ? ImplicitPluginNames() : (Array.Empty<string>(), null);
        if (implicitErr is not null) return FaceGenCheckResult.Fail(implicitErr);
        var (excluded, excludeErr) = SweepExclusion.Resolve(exclude, implicitNames);
        if (excludeErr is not null) return FaceGenCheckResult.Fail(excludeErr);

        string modsDir, dataDir, overwriteDir, profileDir;
        AssetResolver.AssetView assets;
        lock (_gate)
        {
            EnsurePathsDerived();
            modsDir = _modsDir; dataDir = _dataDir; overwriteDir = _overwriteDir; profileDir = _profileDir;
            assets = Assets.Capture();                 // one VFS build for every path this sweep resolves
        }

        List<(string Name, string Path)> offOrder = new();
        if (plugins is { Count: > 0 })
        {
            if (SweepOffOrderScope.Split(view, plugins, modsDir, dataDir, overwriteDir, profileDir,
                                         out _, out offOrder, offOrderMemo) is { } splitErr)
                return splitErr.Stamped
                    ? FaceGenCheckResult.Fail(splitErr.Message) with { Epoch = view.Epoch }
                    : FaceGenCheckResult.Fail(splitErr.Message);
        }

        // Which plugins one provider ships, read lazily and memoized: only a provider that actually WINS a facegen
        // half is ever asked, so a whole-order sweep pays for a handful of directory listings, not one per mod.
        var shipped = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<string> PluginsIn(string provider)
        {
            if (shipped.TryGetValue(provider, out var got)) return got;
            var dir = provider.Equals(AssetResolver.OverwriteLayerName, StringComparison.OrdinalIgnoreCase) ? overwriteDir
                    : provider.Equals(AssetResolver.DataLayerName, StringComparison.OrdinalIgnoreCase) ? dataDir
                    : Path.Combine(modsDir, provider);
            var names = new List<string>();
            try
            {
                if (Directory.Exists(dir))
                    foreach (var f in Directory.EnumerateFiles(dir))
                    {
                        var ext = Path.GetExtension(f);
                        if (ext.Equals(".esp", StringComparison.OrdinalIgnoreCase)
                         || ext.Equals(".esm", StringComparison.OrdinalIgnoreCase)
                         || ext.Equals(".esl", StringComparison.OrdinalIgnoreCase))
                            names.Add(Path.GetFileName(f));
                    }
            }
            catch (Exception) { /* an unreadable folder ships no pole; the row says the test did not run */ }
            return shipped[provider] = names;
        }

        return FaceGenCheck.Run(resolver, view, assets, PluginsIn, plugins, limit,
                                offOrder.Count > 0 ? offOrder : null, recordScope, classes, countsOnly, excluded);
    }

    /// <summary>The facegen family's <c>findings=</c> class tokens. An unrecognized token is a named refusal listing
    /// the whole vocabulary, never a silent widening to every class.</summary>
    static bool TryParseFaceGenClasses(IReadOnlyList<string>? names, out FaceGenFindingClass classes, out string? error)
    {
        classes = FaceGenFindingClass.All; error = null;
        if (names is not { Count: > 0 }) return true;
        var acc = FaceGenFindingClass.None;
        foreach (var raw in names)
        {
            var token = (raw ?? "").Trim().Replace('-', '_').ToLowerInvariant();
            if (FaceGenCheck.ClassFor(token) is { } c) { acc |= c; continue; }
            error = $"findings='{raw}' is not a facegen finding class — use {FaceGenCheck.Vocabulary}.";
            return false;
        }
        classes = acc;
        return true;
    }
}
