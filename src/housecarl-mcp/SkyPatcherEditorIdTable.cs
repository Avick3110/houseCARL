using Mutagen.Bethesda.Plugins;

namespace HousecarlMcp;

/// <summary>One call's EditorID table for the SkyPatcher layer: each name to every record loaded under it, the first loaded
/// keeping it, as po3 Tweaks keeps the game's map. Filled by load-order walks, each type walked at most once per call;
/// held on the call context and dropped with it, so nothing survives the call.</summary>
internal sealed class SkyPatcherEditorIdTable
{
    /// <summary>One record seen under a name: its getter type, its type's name, and the first plugin that loaded it under the name.</summary>
    internal sealed record Holder(FormKey Record, Type Getter, string Type, int Position, string Plugin);

    readonly LoadOrderResolver.IndexView _view;
    readonly string? _tweaksMissing;
    readonly HashSet<Type> _swept = new();
    readonly Dictionary<Type, int> _rank = new();   // each walked type's place: covers in call order, types in scope order
    readonly HashSet<Type> _incomplete = new();
    readonly Dictionary<string, Dictionary<FormKey, Holder>> _names = new(StringComparer.OrdinalIgnoreCase);
    readonly List<PluginUnreadableException> _unreadable = new();

    SkyPatcherEditorIdTable(LoadOrderResolver.IndexView view, string? tweaksMissing) { _view = view; _tweaksMissing = tweaksMissing; }

    /// <summary>The table for this view inside the open call, made on first use; outside a call, a table of its own.</summary>
    internal static SkyPatcherEditorIdTable For(LoadOrderResolver.IndexView view, string? tweaksMissing)
    {
        if (CallContext.Open is not { } call) return new(view, tweaksMissing);
        if (call.SkyPatcherEditorIds is { } held && held._tweaksMissing == tweaksMissing
            && EqualityComparer<LoadOrderResolver.IndexView>.Default.Equals(held._view, view))
            return held;
        return call.SkyPatcherEditorIds = new(view, tweaksMissing);
    }

    /// <summary>Plugins a walk could not open, so a miss is not proof the name does not exist.</summary>
    internal IReadOnlyList<PluginUnreadableException> Unreadable => _unreadable;

    /// <summary>Walk every type in <paramref name="types"/> not yet walked this call, all in one walk.</summary>
    internal void Cover(IEnumerable<Type> types)
    {
        if (types is IReadOnlyList<Type> list && list.All(_swept.Contains)) return;
        var fresh = types.Where(t => !_swept.Contains(t)).Distinct().ToList();
        if (fresh.Count == 0) return;
        _swept.UnionWith(fresh);
        foreach (var t in fresh) _rank[t] = _rank.Count;
        Interlocked.Increment(ref _view.Counters.EditorIdSweeps);
        Interlocked.Add(ref _view.Counters.EditorIdTypesWalked, fresh.Count);
        var kinds = new Dictionary<Type, (Type Getter, string Name)?>();
        int before = _unreadable.Count;
        foreach (var (fk, body, position, plugin) in _view.RecordVersionsOfType(fresh, _unreadable))
        {
            if (body.EditorID is not { Length: > 0 } eid) continue;
            var runtime = body.GetType();
            if (!kinds.TryGetValue(runtime, out var kind))
            {
                var getter = fresh.FirstOrDefault(t => t.IsInstanceOfType(body));
                var name = getter is null ? null : TypeLookup.DisplayNames(new[] { getter })?.FirstOrDefault();
                // Without po3 Tweaks the game keeps no name of this type.
                kinds[runtime] = kind = getter is null || name is null || SkyPatcherEditorIds.Gap(_tweaksMissing, name) is not null
                    ? null : (getter, name);
            }
            if (kind is not { } k) continue;
            if (!_names.TryGetValue(eid, out var holders)) _names[eid] = holders = new();
            // Plugins walk in load order, so a record's first instance under the name is the one that loaded it.
            var holder = new Holder(fk, k.Getter, k.Name, position, plugin);
            if (!holders.TryAdd(fk, holder)) continue;
            // A tie of two types inside one plugin, kept so one walk of that plugin's file places every such record.
            foreach (var o in holders.Values)
                if (o.Position == position && o.Getter != k.Getter)
                {
                    if (!_ties.TryGetValue(position, out var tied)) _ties[position] = tied = new();
                    tied.Add(o.Record);
                    tied.Add(fk);
                }
        }
        if (_unreadable.Count > before) _incomplete.UnionWith(fresh);
    }

    /// <summary>The records loaded under <paramref name="editorId"/> within <paramref name="scope"/> (every walked type when null), first loaded first, after covering the scope; <paramref name="incomplete"/> when a walk that could hold one missed a plugin.</summary>
    internal IReadOnlyList<Holder> Find(string editorId, IReadOnlyList<Type>? scope, out bool incomplete)
    {
        if (scope is not null) Cover(scope);
        incomplete = scope is null ? _incomplete.Count > 0 : scope.Any(_incomplete.Contains);
        if (!_names.TryGetValue(editorId, out var holders)) return Array.Empty<Holder>();
        var found = holders.Values.Where(h => scope is null || scope.Any(t => t.IsAssignableFrom(h.Getter))).ToList();
        // One plugin's records of two types under one name: the game's map keeps the one first in the plugin's file, whatever
        // this call walked first. Records of one type already come in file order, as walked.
        return found.OrderBy(h => h.Position)
            .ThenBy(h => found.Any(o => o.Position == h.Position && o.Getter != h.Getter) ? FilePlace(h) : 0)
            .ThenBy(h => _rank[h.Getter]).ToList();
    }

    readonly Dictionary<int, HashSet<FormKey>> _ties = new();
    readonly Dictionary<int, Dictionary<FormKey, int>> _places = new();

    /// <summary>A record's place in the file of the plugin that loaded it under its name, read once per plugin and key in
    /// this call; a file that cannot be read places nothing, and the tie falls back to the walk's type order.</summary>
    int FilePlace(Holder h)
    {
        if (!_places.TryGetValue(h.Position, out var known)) _places[h.Position] = known = new();
        if (!known.ContainsKey(h.Record))
        {
            // Every two-type tie this plugin holds in the table so far, so one walk of the file places them all.
            var keys = _ties[h.Position].Where(k => !known.ContainsKey(k)).ToHashSet();
            IReadOnlyDictionary<FormKey, int>? read = null;
            try { read = _view.FilePlaces(h.Position, keys); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            foreach (var k in keys) known[k] = read is not null && read.TryGetValue(k, out var p) ? p : int.MaxValue;
        }
        return known[h.Record];
    }
}
