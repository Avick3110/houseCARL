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
            holders.TryAdd(fk, new Holder(fk, k.Getter, k.Name, position, plugin));
        }
        if (_unreadable.Count > before) _incomplete.UnionWith(fresh);
    }

    /// <summary>The records loaded under <paramref name="editorId"/> within <paramref name="scope"/> (every walked type when null), first loaded first, after covering the scope; <paramref name="incomplete"/> when a walk that could hold one missed a plugin.</summary>
    internal IReadOnlyList<Holder> Find(string editorId, IReadOnlyList<Type>? scope, out bool incomplete)
    {
        if (scope is not null) Cover(scope);
        incomplete = scope is null ? _incomplete.Count > 0 : scope.Any(_incomplete.Contains);
        if (!_names.TryGetValue(editorId, out var holders)) return Array.Empty<Holder>();
        return holders.Values.Where(h => scope is null || scope.Any(t => t.IsAssignableFrom(h.Getter)))
            .OrderBy(h => h.Position).ThenBy(h => _rank[h.Getter]).ToList();   // a same-plugin tie by type, as skypatcher-layer.md says
    }
}
