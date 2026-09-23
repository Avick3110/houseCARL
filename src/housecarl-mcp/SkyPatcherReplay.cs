using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlMcp;

// The SkyPatcher replay: one record's winner run through the INI layer, and the context a read opens to run it.
public sealed partial class LoadOrderService
{
    /// <summary>Open the replay context for one call over its pinned view and session: loads the catalog and field map
    /// and scans the INI layer. A load or scan failure throws; the caller names it in its own words.</summary>
    internal SkyPatcherReplay OpenSkyPatcherReplay(AssetResolver.AssetView assets, LoadOrderResolver.IndexView view,
                                                   LoadOrderResolver.OverlaySession session)
    {
        var fieldMap = SkyPatcherFieldMap.Load();
        var catalog = SkyPatcherCatalog.Load();
        var scan = SkyPatcherDiscovery.Scan(assets, catalog, view.ContainsPlugin, _skyPatcherParseCache);
        return new SkyPatcherReplay(this, view, session, catalog, fieldMap, scan);
    }

    /// <summary>One call's replay context: catalog, field map, layer scan, scratch mod, form resolver and lines cache.
    /// The scratch mod is shared across the call, so a caller replays each key once.</summary>
    internal sealed class SkyPatcherReplay
    {
        readonly LoadOrderService _svc;
        readonly LoadOrderResolver.IndexView _view;
        readonly LoadOrderResolver.OverlaySession _session;
        readonly SkyPatcherCatalog _catalog;
        readonly SkyPatcherFieldMap _fieldMap;
        SkyPatcherDiscovery.LayerScan _scan;
        readonly SkyrimMod _scratch = new(SkyPatcherScratchKey, SkyrimRelease.SkyrimSE);
        readonly SkyPatcherServiceResolver _formResolver;
        readonly Dictionary<string, IReadOnlyList<SkyPatcherOverlay.OrderedLine>> _linesCache = new(StringComparer.OrdinalIgnoreCase);

        internal SkyPatcherReplay(LoadOrderService svc, LoadOrderResolver.IndexView view, LoadOrderResolver.OverlaySession session,
                                  SkyPatcherCatalog catalog, SkyPatcherFieldMap fieldMap, SkyPatcherDiscovery.LayerScan scan)
        {
            _svc = svc; _view = view; _session = session;
            _catalog = catalog; _fieldMap = fieldMap; _scan = scan;
            _formResolver = new SkyPatcherServiceResolver(svc, view, session);
        }

        /// <summary>Fold a draft INI into the scan; the refusal sentence when it cannot be folded, else null.</summary>
        internal string? FoldDraft(SkyPatcherDraft.Plan draft, SkyPatcherOverlay.WarningSink? warnings)
        {
            _scan = draft.Fold(_scan, _catalog, _view.ContainsPlugin, out var refusal, warnings);
            return refusal;
        }

        /// <summary>Replay one record through the layer; see <see cref="ReplaySkyPatcher"/>.</summary>
        internal (string? TypeName, string? WinnerPlugin, string? EditorId, List<SkyPatcherFolderOutcome> Folders, string? Error, IMajorRecord? Copy)
            Replay(FormKey fk)
            => _svc.ReplaySkyPatcher(_view, _session, _scan, _catalog, _fieldMap, _scratch, _formResolver, fk, _linesCache);
    }

    /// <summary>The per-record SkyPatcher replay core, shared by the post-state read and the layer no-op scan; Error is the named reason a record cannot be replayed.</summary>
    (string? TypeName, string? WinnerPlugin, string? EditorId, List<SkyPatcherFolderOutcome> Folders, string? Error, IMajorRecord? Copy)
        ReplaySkyPatcher(
            LoadOrderResolver.IndexView view, LoadOrderResolver.OverlaySession session,
            SkyPatcherDiscovery.LayerScan scan, SkyPatcherCatalog catalog, SkyPatcherFieldMap fieldMap,
            SkyrimMod scratch, SkyPatcherOverlay.IFormResolver formResolver, FormKey fk,
            Dictionary<string, IReadOnlyList<SkyPatcherOverlay.OrderedLine>>? linesCache)
    {
        var none = new List<SkyPatcherFolderOutcome>();
        var winner = view.ResolveWinner(fk);
        if (winner is null)
            return (null, null, null, none, UnresolvedFormId(view, fk), null);

        var body = view.GetRecord(session, winner.Value.WinnerPlugin, fk);
        if (body is null)
            return (null, winner.Value.WinnerPlugin, null, none, $"Winner '{winner.Value.WinnerPlugin}' did not yield {FormIdToken.Of(fk)} on fetch — a load-order inconsistency.", null);

        var typeName = ReadEngine.ReadFields(body, new[] { "EditorID" }).Type;   // the same type naming every read tool reports
        var maps = fieldMap.ForRecordType(typeName);
        if (maps.Count == 0)
            return (typeName, winner.Value.WinnerPlugin, body.EditorID, none,
                $"Record type '{typeName}' is not a SkyPatcher-patchable type (or has no field map) — the SkyPatcher layer cannot touch {FormIdToken.Of(fk)}.", null);

        // The running copy: the winner overridden into an in-memory scratch mod. Nested-group types need the source link cache, or they throw instead of failing by name.
        IMajorRecord copy;
        try
        {
            Mutagen.Bethesda.Plugins.Cache.ILinkCache? cache =
                WriteEngine.RecordNeedsSourceCache(body) ? session.LinkCacheFor(winner.Value.WinnerPlugin) : null;
            copy = WriteEngine.GenericGetOrAddAsOverride(scratch, body, cache);
        }
        catch (Exception ex)
        {
            return (typeName, winner.Value.WinnerPlugin, body.EditorID, none,
                $"Could not materialize a mutable copy of {FormIdToken.Of(fk)} ({typeName}) for the replay — {ex.GetType().Name}: {ex.Message}", null);
        }

        // Watch this record's own EditorID lookups: a record addressed purely by FormID answers normally.
        var spr = formResolver as SkyPatcherServiceResolver;
        spr?.WatchLookups();

        var folders = new List<SkyPatcherFolderOutcome>();
        foreach (var m in maps)
        {
            var folder = scan.Folders.FirstOrDefault(f => f.Subfolder.Equals(m.Subfolder, StringComparison.OrdinalIgnoreCase));
            if (folder is null || folder.Catalog is null)
            {
                folders.Add(new SkyPatcherFolderOutcome(m.Subfolder, 0, 0, null, true));
                continue;
            }
            IReadOnlyList<SkyPatcherOverlay.OrderedLine> lines;
            if (linesCache is null) lines = SkyPatcherDiscovery.OrderedLines(folder);
            else if (!linesCache.TryGetValue(folder.Subfolder, out lines!))
                linesCache[folder.Subfolder] = lines = SkyPatcherDiscovery.OrderedLines(folder);
            var result = SkyPatcherOverlay.Apply(copy, fk, body.EditorID, catalog, folder.Catalog, m, lines, formResolver);
            // A toggled-off folder contributes nothing: reporting its files as applied would assert INIs the DLL skips.
            folders.Add(new SkyPatcherFolderOutcome(folder.Subfolder,
                folder.PatchingEnabled ? folder.Files.Count(f => f.NotApplied is null) : 0, lines.Count, result,
                folder.PatchingEnabled));
        }

        // A plugin an EditorID sweep could not read leaves its EditorIDs out of the table, so this replay would report a state the layer does not produce. Named, not answered wrong.
        if (spr is { ConsumedIncompleteTable: true })
            return (typeName, winner.Value.WinnerPlugin, body.EditorID, none,
                $"the SkyPatcher replay of {FormIdToken.Of(fk)} resolved an EditorID against the load order, and "
                + string.Join(" ", spr.Unreadable.Select(u => u.Message).Distinct()), null);

        return (typeName, winner.Value.WinnerPlugin, body.EditorID, folders, null, copy);
    }

    /// <summary>The live-load-order lookups the overlay needs, off the ONE pinned view and session the call holds.
    /// EditorID resolution sweeps a type's winners once into a table; a miss is null, reported loudly upstream.</summary>
    sealed class SkyPatcherServiceResolver : SkyPatcherOverlay.IFormResolver
    {
        readonly LoadOrderService _svc;
        readonly LoadOrderResolver.IndexView _view;
        readonly LoadOrderResolver.OverlaySession _session;
        readonly Dictionary<string, Dictionary<string, FormKey>> _eidsByType = new(StringComparer.OrdinalIgnoreCase);
        readonly List<PluginUnreadableException> _unreadable = new();
        readonly HashSet<string> _incompleteTypes = new(StringComparer.OrdinalIgnoreCase);   // types whose sweep missed a plugin
        bool _consumedIncomplete;

        /// <summary>Plugins an EditorID sweep could not open, so a miss here is not proof the name does not exist.</summary>
        public IReadOnlyList<PluginUnreadableException> Unreadable => _unreadable;

        /// <summary>Whether a lookup since the last <see cref="WatchLookups"/> was answered from a table a plugin is missing from — the flag that tells a caller its OWN answer is affected.</summary>
        public bool ConsumedIncompleteTable => _consumedIncomplete;

        public void WatchLookups() => _consumedIncomplete = false;

        public SkyPatcherServiceResolver(LoadOrderService svc, LoadOrderResolver.IndexView view, LoadOrderResolver.OverlaySession session)
        { _svc = svc; _view = view; _session = session; }

        public FormKey? ResolveEditorId(string editorId, string? mutagenType)
        {
            if (mutagenType is null || string.IsNullOrWhiteSpace(editorId)) return null;
            if (!_eidsByType.TryGetValue(mutagenType, out var eids))
            {
                eids = new Dictionary<string, FormKey>(StringComparer.OrdinalIgnoreCase);
                var types = _svc.ResolveFormScope(mutagenType);
                if (types is not null)
                {
                    int before = _unreadable.Count;
                    foreach (var (candidate, _, cBody) in _view.WinnerRecordsOfType(types, _unreadable))
                        if (cBody.EditorID is { Length: > 0 } eid && !eids.ContainsKey(eid))   // first winner keeps the slot
                            eids[eid] = candidate;
                    if (_unreadable.Count > before) _incompleteTypes.Add(mutagenType);
                }
                _eidsByType[mutagenType] = eids;
            }
            if (_incompleteTypes.Contains(mutagenType)) _consumedIncomplete = true;
            return eids.TryGetValue(editorId, out var fk) ? fk : null;
        }

        public string? ReadWinnerLeaf(FormKey donor, string path)
        {
            var w = _view.ResolveWinner(donor);
            if (w is null) return null;
            var rec = _view.GetRecord(_session, w.Value.WinnerPlugin, donor);
            if (rec is null) return null;
            var leaf = ReadEngine.ReadFields(rec, new[] { path }).Fields.FirstOrDefault();
            return leaf is { HasValue: true } ? leaf.Token : null;
        }

        public IReadOnlyList<FormKey>? KeywordsOf(FormKey record)
        {
            var w = _view.ResolveWinner(record);
            if (w is null) return null;
            var rec = _view.GetRecord(_session, w.Value.WinnerPlugin, record);
            return rec is null ? null : ReadEngine.KeywordKeys(rec);   // the ONE keyword walk (shared)
        }

        public bool PluginPresent(string pluginName) => _view.ContainsPlugin(pluginName);

        public string? WinnerPluginOf(FormKey record) => _view.ResolveWinner(record)?.WinnerPlugin;

        public string? EditorIdOf(FormKey record)
        {
            var w = _view.ResolveWinner(record);
            if (w is null) return null;
            return _view.GetRecord(_session, w.Value.WinnerPlugin, record)?.EditorID;
        }
    }
}
