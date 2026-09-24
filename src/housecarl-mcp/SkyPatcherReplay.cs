using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlMcp;

// The SkyPatcher replay: one record's winner run through the INI layer, and the one door that opens its context.
public sealed partial class LoadOrderService
{
    /// <summary>Cross-call INI parse cache, FileStamp keyed: repeat calls over an untouched layer skip every read and parse.</summary>
    readonly SkyPatcherDiscovery.ParseCache _skyPatcherParseCache = new();

    /// <summary>The in-memory scratch mod the replay copy is overridden into — never written to disk.</summary>
    static readonly ModKey SkyPatcherScratchKey = new("HousecarlSkyPatcherScratch", ModType.Plugin);

    /// <summary>An asset build with the warnings and profile name taken in the same capture hold.</summary>
    internal readonly record struct SkyPatcherAssets(AssetResolver.AssetView View, IReadOnlyList<string> Warnings, string ProfileName);

    /// <summary>Open one call's replay context, folding in the draft if given; null with the refusal when the draft cannot be folded.</summary>
    internal SkyPatcherReplay? OpenSkyPatcherReplay(LoadOrderResolver.IndexView view, LoadOrderResolver.OverlaySession session,
                                                    out string? draftRefusal, SkyPatcherDraft.Plan? draft = null,
                                                    SkyPatcherOverlay.WarningSink? draftWarnings = null)
    {
        var c = Host.CaptureAssets();
        var captured = new SkyPatcherAssets(c.View, c.Warnings, c.ProfileName);
        return OpenSkyPatcherReplay(captured, view, session, out draftRefusal, draft, draftWarnings);
    }

    /// <summary>The same door over assets the caller took in its own capture hold, for a lane that pins the index in that hold.</summary>
    internal SkyPatcherReplay? OpenSkyPatcherReplay(SkyPatcherAssets captured, LoadOrderResolver.IndexView view,
                                                    LoadOrderResolver.OverlaySession session, out string? draftRefusal,
                                                    SkyPatcherDraft.Plan? draft = null, SkyPatcherOverlay.WarningSink? draftWarnings = null)
    {
        var (assets, assetWarnings, profileName) = captured;
        var fieldMap = SkyPatcherFieldMap.Load();
        var catalog = SkyPatcherCatalog.Load();
        var scan = SkyPatcherDiscovery.Scan(assets, catalog, view.ContainsPlugin, _skyPatcherParseCache);
        draftRefusal = null;
        if (draft is not null)
        {
            scan = draft.Fold(scan, catalog, view.ContainsPlugin, out draftRefusal, draftWarnings);
            if (draftRefusal is not null) return null;
        }
        return new SkyPatcherReplay(this, view, session, assets, assetWarnings, profileName, catalog, fieldMap, scan);
    }

    /// <summary>One call's replay context; its scratch mod is shared across the call, so a caller replays each key once.</summary>
    internal sealed class SkyPatcherReplay
    {
        readonly IAssetHost _host;
        readonly LoadOrderResolver.IndexView _view;
        readonly LoadOrderResolver.OverlaySession _session;
        readonly SkyrimMod _scratch = new(SkyPatcherScratchKey, SkyrimRelease.SkyrimSE);
        readonly SkyPatcherServiceResolver _formResolver;
        readonly Dictionary<string, IReadOnlyList<SkyPatcherOverlay.OrderedLine>> _linesCache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The asset build the layer was scanned from.</summary>
        internal AssetResolver.AssetView Assets { get; }

        /// <summary>The asset warnings taken in the same hold as <see cref="Assets"/>.</summary>
        internal IReadOnlyList<string> AssetWarnings { get; }

        /// <summary>The profile name taken in the same hold as <see cref="Assets"/>.</summary>
        internal string ProfileName { get; }

        internal SkyPatcherCatalog Catalog { get; }
        internal SkyPatcherFieldMap FieldMap { get; }
        internal SkyPatcherDiscovery.LayerScan Scan { get; }

        internal SkyPatcherReplay(LoadOrderService svc, LoadOrderResolver.IndexView view, LoadOrderResolver.OverlaySession session,
                                  AssetResolver.AssetView assets, IReadOnlyList<string> assetWarnings, string profileName,
                                  SkyPatcherCatalog catalog, SkyPatcherFieldMap fieldMap, SkyPatcherDiscovery.LayerScan scan)
        {
            _host = svc.Host; _view = view; _session = session;
            Assets = assets; AssetWarnings = assetWarnings; ProfileName = profileName;
            Catalog = catalog; FieldMap = fieldMap; Scan = scan;
            _formResolver = new SkyPatcherServiceResolver(svc, view, session);
        }

        /// <summary>An EditorID of one type to its winning FormKey; null on a miss.</summary>
        internal FormKey? ResolveEditorId(string editorId, string? mutagenType) => _formResolver.ResolveEditorId(editorId, mutagenType);

        /// <summary>Plugins an EditorID sweep could not open during this call.</summary>
        internal IReadOnlyList<PluginUnreadableException> Unreadable => _formResolver.Unreadable;

        /// <summary>Replay one record's winner through the layer; Error is the named reason it cannot be replayed.</summary>
        internal (string? TypeName, string? WinnerPlugin, string? EditorId, List<SkyPatcherFolderOutcome> Folders, string? Error, IMajorRecord? Copy)
            Replay(FormKey fk)
        {
            var none = new List<SkyPatcherFolderOutcome>();
            var winner = _view.ResolveWinner(fk);
            if (winner is null)
                return (null, null, null, none, _host.UnresolvedFormId(_view, fk), null);

            var body = _view.GetRecord(_session, winner.Value.WinnerPlugin, fk);
            if (body is null)
                return (null, winner.Value.WinnerPlugin, null, none, $"Winner '{winner.Value.WinnerPlugin}' did not yield {FormIdToken.Of(fk)} on fetch — a load-order inconsistency.", null);

            var typeName = ReadEngine.ReadFields(body, new[] { "EditorID" }).Type;   // the same type naming every read tool reports
            var maps = FieldMap.ForRecordType(typeName);
            if (maps.Count == 0)
                return (typeName, winner.Value.WinnerPlugin, body.EditorID, none,
                    $"Record type '{typeName}' is not a SkyPatcher-patchable type (or has no field map) — the SkyPatcher layer cannot touch {FormIdToken.Of(fk)}.", null);

            // The running copy: the winner overridden into an in-memory scratch mod. Nested-group types need the source link cache, or they throw instead of failing by name.
            IMajorRecord copy;
            try
            {
                Mutagen.Bethesda.Plugins.Cache.ILinkCache? cache =
                    WriteEngine.RecordNeedsSourceCache(body) ? _session.LinkCacheFor(winner.Value.WinnerPlugin) : null;
                copy = WriteEngine.GenericGetOrAddAsOverride(_scratch, body, cache);
            }
            catch (Exception ex)
            {
                return (typeName, winner.Value.WinnerPlugin, body.EditorID, none,
                    $"Could not materialize a mutable copy of {FormIdToken.Of(fk)} ({typeName}) for the replay — {ex.GetType().Name}: {ex.Message}", null);
            }

            // Watch this record's own EditorID lookups: a record addressed purely by FormID answers normally.
            _formResolver.WatchLookups();

            var folders = new List<SkyPatcherFolderOutcome>();
            foreach (var m in maps)
            {
                var folder = Scan.Folders.FirstOrDefault(f => f.Subfolder.Equals(m.Subfolder, StringComparison.OrdinalIgnoreCase));
                if (folder is null || folder.Catalog is null)
                {
                    folders.Add(new SkyPatcherFolderOutcome(m.Subfolder, 0, 0, null, true));
                    continue;
                }
                if (!_linesCache.TryGetValue(folder.Subfolder, out var lines))
                    _linesCache[folder.Subfolder] = lines = SkyPatcherDiscovery.OrderedLines(folder);
                var result = SkyPatcherOverlay.Apply(copy, fk, body.EditorID, Catalog, folder.Catalog, m, lines, _formResolver);
                // A toggled-off folder contributes nothing: reporting its files as applied would assert INIs the DLL skips.
                folders.Add(new SkyPatcherFolderOutcome(folder.Subfolder,
                    folder.PatchingEnabled ? folder.Files.Count(f => f.NotApplied is null) : 0, lines.Count, result,
                    folder.PatchingEnabled));
            }

            // A plugin an EditorID sweep could not read leaves its EditorIDs out of the table, so this replay would report a state the layer does not produce. Named, not answered wrong.
            if (_formResolver.ConsumedIncompleteTable)
                return (typeName, winner.Value.WinnerPlugin, body.EditorID, none,
                    $"the SkyPatcher replay of {FormIdToken.Of(fk)} resolved an EditorID against the load order, and "
                    + string.Join(" ", _formResolver.Unreadable.Select(u => u.Message).Distinct()), null);

            return (typeName, winner.Value.WinnerPlugin, body.EditorID, folders, null, copy);
        }
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
