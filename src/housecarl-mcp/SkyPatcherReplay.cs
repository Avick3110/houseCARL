using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlMcp;

// The SkyPatcher replay: one record's winner run through the INI layer, and the one door that opens its context.
internal sealed partial class AssetLayers
{
    /// <summary>Cross-call INI parse cache, FileStamp keyed: repeat calls over an untouched layer skip every read and parse.</summary>
    readonly SkyPatcherDiscovery.ParseCache _skyPatcherParseCache = new();

    /// <summary>The in-memory scratch mod the replay copy is overridden into — never written to disk.</summary>
    static readonly ModKey SkyPatcherScratchKey = new("HousecarlSkyPatcherScratch", ModType.Plugin);

    /// <summary>Open one call's replay context over assets the caller took in its index pin's hold, folding in the draft if given; null with the refusal when the draft cannot be folded.</summary>
    internal SkyPatcherReplay? OpenSkyPatcherReplay(AssetCapture captured, LoadOrderResolver.IndexView view,
                                                    LoadOrderResolver.OverlaySession session, out string? draftRefusal,
                                                    SkyPatcherDraft.Plan? draft = null, SkyPatcherOverlay.WarningSink? warnings = null)
    {
        var assets = captured.View;
        var fieldMap = SkyPatcherFieldMap.Load();
        var catalog = SkyPatcherCatalog.Load();
        var scan = SkyPatcherDiscovery.Scan(assets, catalog, view.ContainsPlugin, _skyPatcherParseCache);
        draftRefusal = null;
        if (draft is not null)
        {
            scan = draft.Fold(scan, catalog, view.ContainsPlugin, out draftRefusal, warnings);
            if (draftRefusal is not null) return null;
        }
        // An unreadable po3_Tweaks.ini is replayed as the DLL's default and said on the read, records and layer alike.
        var tweaksMissing = SkyPatcherEditorIds.Read(assets, out var tweaksUnknown);
        if (tweaksUnknown is not null) warnings?.Add(tweaksUnknown);
        return new SkyPatcherReplay(this, view, session, assets,
                                    tweaksUnknown is null ? captured.Warnings : captured.Warnings.Append(tweaksUnknown).ToList(),
                                    captured.ProfileName, catalog, fieldMap, scan, tweaksMissing);
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

        internal SkyPatcherReplay(AssetLayers layers, LoadOrderResolver.IndexView view, LoadOrderResolver.OverlaySession session,
                                  AssetResolver.AssetView assets, IReadOnlyList<string> assetWarnings, string profileName,
                                  SkyPatcherCatalog catalog, SkyPatcherFieldMap fieldMap, SkyPatcherDiscovery.LayerScan scan,
                                  string? tweaksMissing)
        {
            _host = layers._host; _view = view; _session = session;
            Assets = assets; AssetWarnings = assetWarnings; ProfileName = profileName;
            Catalog = catalog; FieldMap = fieldMap; Scan = scan;
            _formResolver = new SkyPatcherServiceResolver(layers, view, session, tweaksMissing, CallScopes);
        }

        /// <summary>Every scope an EditorID in this layer is looked up among, across its folders and their record types.</summary>
        IEnumerable<string?> CallScopes()
            => Scan.Folders.Where(f => f.Catalog is not null).SelectMany(f =>
            {
                var maps = FieldMap.ForSubfolder(f.Subfolder);
                return Lines(f).SelectMany(l => (maps.Count == 0 ? new RecordMap?[] { null } : maps.Select(m => (RecordMap?)m))
                    .SelectMany(m => SkyPatcherOverlay.EditorIdScopes(l.Parsed, Catalog, f.Catalog!, m)));
            });

        /// <summary>One folder's lines in apply order, built once per context.</summary>
        IReadOnlyList<SkyPatcherOverlay.OrderedLine> Lines(SkyPatcherDiscovery.FolderScan folder)
        {
            if (!_linesCache.TryGetValue(folder.Subfolder, out var lines))
                _linesCache[folder.Subfolder] = lines = SkyPatcherDiscovery.OrderedLines(folder);
            return lines;
        }

        /// <summary>The call's form lookups, for a site outside the replay that resolves a target the same way.</summary>
        internal SkyPatcherOverlay.IFormResolver Forms => _formResolver;

        /// <summary>Per folder, the lines checked against every record and the lines that can touch only the records they name.</summary>
        readonly Dictionary<string, (List<int> Always, IReadOnlyList<SkyPatcherOverlay.OrderedLine> AlwaysLines, Dictionary<FormKey, List<int>> ByKey)> _lineIndex = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The lines of <paramref name="folder"/> that can touch <paramref name="fk"/>, in apply order.</summary>
        IReadOnlyList<SkyPatcherOverlay.OrderedLine> LinesFor(SkyPatcherDiscovery.FolderScan folder, IReadOnlyList<SkyPatcherOverlay.OrderedLine> lines, FormKey fk)
        {
            if (!_lineIndex.TryGetValue(folder.Subfolder, out var ix))
            {
                var always = new List<int>();
                var byKey = new Dictionary<FormKey, List<int>>();
                for (int i = 0; i < lines.Count; i++)
                {
                    if (NamedTargets(lines[i].Parsed, folder.Catalog!) is not { } keys) { always.Add(i); continue; }
                    foreach (var k in keys) { if (!byKey.TryGetValue(k, out var li)) byKey[k] = li = new(); li.Add(i); }
                }
                ix = (always, always.Count == lines.Count ? lines : always.ConvertAll(i => lines[i]), byKey);
                _lineIndex[folder.Subfolder] = ix;
            }
            if (!ix.ByKey.TryGetValue(fk, out var mine)) return ix.AlwaysLines;
            // Both lists ascend and share no line, so a two-way merge keeps apply order.
            var merged = new List<SkyPatcherOverlay.OrderedLine>(ix.Always.Count + mine.Count);
            int a = 0, m = 0;
            while (a < ix.Always.Count || m < mine.Count)
                merged.Add(lines[m == mine.Count || (a < ix.Always.Count && ix.Always[a] < mine[m]) ? ix.Always[a++] : mine[m++]]);
            return merged;
        }

        /// <summary>The records a patch line can touch when its only filter is one direct list naming FormIDs; null for any other line, or one carrying a parse note, which the overlay must see on every record.</summary>
        HashSet<FormKey>? NamedTargets(SkyPatcherLine line, SkyPatcherRecordCatalog recordCatalog)
        {
            if (line.Kind != SkyPatcherLineKind.Patch || line.Note is not null) return null;
            var classified = SkyPatcherClassifiedLine.Of(line, Catalog, recordCatalog);
            var shape = classified.Shape;
            if (classified.UnknownKey || !shape.OnlyDirect || shape.Direct[0].seg.Values.Count == 0) return null;
            var keys = new HashSet<FormKey>();
            foreach (var v in shape.Direct[0].seg.Values)
                if (v.Address is { } a && SkyPatcherTargets.FormKeyOf(a) is { } k) keys.Add(k);
                else return null;
            return keys;
        }

        /// <summary>One line's lint against the load order, across every record type its folder holds, and whether a lookup read an EditorID table missing a plugin; empty for an undocumented folder.</summary>
        internal (IReadOnlyList<string> Lint, bool Incomplete) LineLint(SkyPatcherDiscovery.FolderScan folder, SkyPatcherOverlay.OrderedLine line)
        {
            if (folder.Catalog is null) return (Array.Empty<string>(), false);
            var maps = FieldMap.ForSubfolder(folder.Subfolder);
            _formResolver.WatchLookups();
            var lint = (maps.Count == 0 ? new RecordMap?[] { null } : maps.Select(m => (RecordMap?)m))
                .SelectMany(m => SkyPatcherOverlay.LineLint(line, Catalog, folder.Catalog, m, _formResolver))
                .Distinct(StringComparer.Ordinal).ToList();
            return (lint, lint.Count > 0 && _formResolver.ConsumedIncompleteTable);
        }

        /// <summary>Plugins an EditorID sweep could not open during this call.</summary>
        internal IReadOnlyList<PluginUnreadableException> Unreadable => _formResolver.Unreadable;

        /// <summary>Replay one record's winner through the layer: the patched copy, the winner itself for a type the layer cannot touch, or the named reason it cannot be replayed.
        /// <paramref name="everyTypeLookups"/> false leaves a value looked up among every record type (a form-list item) unresolved, for a caller that never reads the field it lands on.</summary>
        internal SkyPatcherReplayResult Replay(FormKey fk, IMajorRecordGetter? read = null, bool everyTypeLookups = true)
        {
            _formResolver.EveryTypeLookups = everyTypeLookups;
            try { return ReplayCore(fk, read); }
            finally { _formResolver.EveryTypeLookups = true; }
        }

        SkyPatcherReplayResult ReplayCore(FormKey fk, IMajorRecordGetter? read)
        {
            var winner = _view.ResolveWinner(fk);
            if (winner is null)
                return SkyPatcherReplayResult.Fail(RecordReads.UnresolvedFormId(_view, fk));

            var body = read ?? BodyGather.Winners(_view, _session, new[] { fk })(fk);   // read: the winner's body, already read through the door
            if (body is null)
                return SkyPatcherReplayResult.Fail($"Winner '{winner.Value.WinnerPlugin}' did not yield {FormIdToken.Of(fk)} on fetch — a load-order inconsistency.",
                                                   winnerPlugin: winner.Value.WinnerPlugin);

            var typeName = ReadEngine.ReadFields(body, new[] { "EditorID" }).Type;   // the same type naming every read tool reports
            var maps = FieldMap.ForRecordType(typeName);
            // The layer cannot touch this type, so its post state is the winner itself.
            if (maps.Count == 0)
                return SkyPatcherReplayResult.Unpatchable(typeName, winner.Value.WinnerPlugin, body);

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
                return SkyPatcherReplayResult.Fail(
                    $"Could not materialize a mutable copy of {FormIdToken.Of(fk)} ({typeName}) for the replay — {ex.GetType().Name}: {ex.Message}",
                    typeName, winner.Value.WinnerPlugin, body.EditorID);
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
                var lines = Lines(folder);
                var result = SkyPatcherOverlay.Apply(copy, fk, body.EditorID, Catalog, folder.Catalog, m, LinesFor(folder, lines, fk), _formResolver);
                // A toggled-off folder contributes nothing: reporting its files as applied would assert INIs the DLL skips.
                folders.Add(new SkyPatcherFolderOutcome(folder.Subfolder,
                    folder.PatchingEnabled ? folder.Files.Count(f => f.NotApplied is null) : 0, lines.Count, result,
                    folder.PatchingEnabled));
            }

            // A plugin an EditorID sweep could not read leaves its EditorIDs out of the table, so this replay would report a state the layer does not produce. Named, not answered wrong.
            if (_formResolver.ConsumedIncompleteTable)
                return SkyPatcherReplayResult.Fail(
                    $"the SkyPatcher replay of {FormIdToken.Of(fk)} resolved an EditorID against the load order, and "
                    + string.Join(" ", _formResolver.Unreadable.Select(u => u.Message).Distinct()),
                    typeName, winner.Value.WinnerPlugin, body.EditorID);

            return SkyPatcherReplayResult.Ok(typeName, winner.Value.WinnerPlugin, body.EditorID, folders, copy);
        }
    }

    /// <summary>One replay's outcome, built only through its factories so a failure cannot be marked unpatchable by mistake.</summary>
    internal sealed record SkyPatcherReplayResult
    {
        internal string? TypeName { get; private init; }
        internal string? WinnerPlugin { get; private init; }
        internal string? EditorId { get; private init; }
        internal IReadOnlyList<SkyPatcherFolderOutcome> Folders { get; private init; } = Array.Empty<SkyPatcherFolderOutcome>();

        /// <summary>The post-state body: the patched copy, or the winner itself for an unpatchable type; null exactly when Error is set.</summary>
        internal IMajorRecordGetter? Copy { get; private init; }

        /// <summary>The named reason the record cannot be replayed; null when Copy is set.</summary>
        internal string? Error { get; private init; }

        /// <summary>The type has no SkyPatcher field map, so Copy is the untouched winner.</summary>
        internal bool IsUnpatchable { get; private init; }

        SkyPatcherReplayResult() { }

        internal static SkyPatcherReplayResult Ok(string typeName, string winnerPlugin, string? editorId,
                                                  IReadOnlyList<SkyPatcherFolderOutcome> folders, IMajorRecord copy)
            => new() { TypeName = typeName, WinnerPlugin = winnerPlugin, EditorId = editorId, Folders = folders, Copy = copy };

        internal static SkyPatcherReplayResult Unpatchable(string typeName, string winnerPlugin, IMajorRecordGetter body)
            => new() { TypeName = typeName, WinnerPlugin = winnerPlugin, EditorId = body.EditorID, Copy = body, IsUnpatchable = true };

        internal static SkyPatcherReplayResult Fail(string error, string? typeName = null, string? winnerPlugin = null, string? editorId = null)
            => new() { Error = error, TypeName = typeName, WinnerPlugin = winnerPlugin, EditorId = editorId };
    }

    /// <summary>The live-load-order lookups the overlay needs, off the ONE pinned view and session the call holds.
    /// EditorIDs resolve through the call's one table, which its first lookup fills for every scope the call's lines name; a miss is null, reported on its line.</summary>
    sealed class SkyPatcherServiceResolver : SkyPatcherOverlay.IFormResolver
    {
        readonly AssetLayers _layers;
        readonly LoadOrderResolver.IndexView _view;
        readonly LoadOrderResolver.OverlaySession _session;
        readonly SkyPatcherEditorIdTable _table;
        readonly Func<IEnumerable<string?>> _callScopes;
        bool _covered;
        bool _consumedIncomplete;

        /// <summary>Plugins an EditorID sweep could not open, so a miss here is not proof the name does not exist.</summary>
        public IReadOnlyList<PluginUnreadableException> Unreadable => _table.Unreadable;

        /// <summary>Whether a lookup since the last <see cref="WatchLookups"/> was answered from a table a plugin is missing from — the flag that tells a caller its OWN answer is affected.</summary>
        public bool ConsumedIncompleteTable => _consumedIncomplete;

        public void WatchLookups() => _consumedIncomplete = false;

        /// <summary>Whether a lookup among every record type walks; false makes it miss unwalked.</summary>
        internal bool EveryTypeLookups { get; set; } = true;

        readonly string? _tweaksMissing;

        public SkyPatcherServiceResolver(AssetLayers layers, LoadOrderResolver.IndexView view, LoadOrderResolver.OverlaySession session,
                                         string? tweaksMissing, Func<IEnumerable<string?>> callScopes)
        {
            _layers = layers; _view = view; _session = session; _tweaksMissing = tweaksMissing; _callScopes = callScopes;
            _table = SkyPatcherEditorIdTable.For(view, tweaksMissing);
        }

        readonly Dictionary<string, string?> _gaps = new(StringComparer.OrdinalIgnoreCase);

        public string? EditorIdGap(string? mutagenType)
        {
            if (_tweaksMissing is null) return null;
            var key = mutagenType ?? "";
            if (!_gaps.TryGetValue(key, out var gap)) _gaps[key] = gap = GroupGap(mutagenType) ?? SkyPatcherEditorIds.Gap(_tweaksMissing, mutagenType);
            return gap;
        }

        /// <summary>The gap of a group scope holding a type the game keeps names of without Tweaks, naming those types; null for any other scope.</summary>
        string? GroupGap(string? mutagenType)
        {
            if (mutagenType is null || SkyPatcherEditorIds.Gap(_tweaksMissing, mutagenType) is null || Kept(mutagenType) is not { Count: > 0 } kept) return null;
            return $"{_tweaksMissing}, so of {mutagenType} records the game keeps EditorIDs only of {string.Join(", ", kept)} records";
        }

        /// <summary>The display names in a scope whose EditorIDs the game keeps here.</summary>
        IReadOnlyList<string>? Kept(string mutagenType)
            => Scope(mutagenType) is { } types && TypeLookup.DisplayNames(types) is { } names
                ? names.Where(n => SkyPatcherEditorIds.Gap(_tweaksMissing, n) is null).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                : null;

        public FormKey? ResolveEditorId(string editorId, string? mutagenType)
            => Find(editorId, mutagenType) is { Count: > 0 } holders ? holders[0].Record : null;

        public string? EditorIdNote(string editorId, string? mutagenType)
            => Find(editorId, mutagenType) is { Count: > 1 } holders
                ? $"names {holders.Count} {mutagenType} records; the replay takes the first loaded, {FormIdToken.Of(holders[0].Record)} from {holders[0].Plugin}"
                : null;

        /// <summary>The records loaded under a name within the scope, first loaded first, covering the call's scopes at the first lookup; empty for a blank name, no scope, a type the game keeps no names of here, or a scope that names no type.</summary>
        IReadOnlyList<SkyPatcherEditorIdTable.Holder> Find(string editorId, string? mutagenType)
        {
            IReadOnlyList<Type>? scope;
            // An untyped lookup misses: contract in docs/architecture/skypatcher-layer.md.
            if (mutagenType is null || string.IsNullOrWhiteSpace(editorId)) return Array.Empty<SkyPatcherEditorIdTable.Holder>();
            if ((scope = Scope(mutagenType)) is null || (!EveryTypeLookups && EveryType(scope))) return Array.Empty<SkyPatcherEditorIdTable.Holder>();
            // The game cannot see this name, so neither does the replay; a group with a kept type is gated per type by the table.
            if (EditorIdGap(mutagenType) is not null && GroupGap(mutagenType) is null) return Array.Empty<SkyPatcherEditorIdTable.Holder>();
            if (!_covered)
            {
                // The first lookup walks once for every scope the call's lines name, so later lookups walk nothing;
                // a scope of every type is left to its own first lookup, so a call that never looks one up never walks it.
                _covered = true;
                _table.Cover(_callScopes().OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(Scope).OfType<IReadOnlyList<Type>>().Where(s => !EveryType(s)).SelectMany(s => s));
            }
            var e = _table.Find(editorId, scope, out var incomplete);
            if (incomplete) _consumedIncomplete = true;
            return e;
        }

        readonly Dictionary<string, IReadOnlyList<Type>?> _scopes = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>A scope string's getter types, resolved once per context.</summary>
        IReadOnlyList<Type>? Scope(string scope)
        {
            if (!_scopes.TryGetValue(scope, out var types)) _scopes[scope] = types = _layers.ResolveFormScope(scope);
            return types;
        }

        /// <summary>Whether a scope holds every record type (form-list items).</summary>
        bool EveryType(IReadOnlyList<Type> scope) => Scope(nameof(SkyrimMajorRecord)) is { } all && scope.Count >= all.Count;

        // The winners the lines name (donors, keyword sources, EditorID targets), each read once per replay context
        // through the door, so the same donor named for every record and line is read once.
        readonly Dictionary<FormKey, (IMajorRecordGetter? Body, Exception? Error)> _named = new();

        IMajorRecordGetter? Named(FormKey fk)
        {
            if (!_named.TryGetValue(fk, out var got))
            {
                try { got = (BodyGather.Winners(_view, _session, new[] { fk })(fk), null); }
                catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException or LoadOrderChangedMidCallException)) { got = (null, ex); }
                _named[fk] = got;
            }
            if (got.Error is { } error) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
            return got.Body;
        }

        public string? ReadWinnerLeaf(FormKey donor, string path)
        {
            var rec = Named(donor);
            if (rec is null) return null;
            var leaf = ReadEngine.ReadFields(rec, new[] { path }).Fields.FirstOrDefault();
            return leaf is { HasValue: true } ? leaf.Token : null;
        }

        public IReadOnlyList<FormKey>? KeywordsOf(FormKey record)
            => Named(record) is { } rec ? ReadEngine.KeywordKeys(rec) : null;   // the ONE keyword walk (shared)

        public bool PluginPresent(string pluginName) => _view.ContainsPlugin(pluginName);

        public string? WinnerPluginOf(FormKey record) => _view.ResolveWinner(record)?.WinnerPlugin;

        public string? EditorIdOf(FormKey record) => Named(record)?.EditorID;
    }
}
