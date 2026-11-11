using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;

namespace HousecarlMcp;

/// <summary>One call's EditorID table for the SkyPatcher layer, kept as the game keeps its map: one case-insensitive
/// name space across every type, the first record loaded under a name keeping it. Filled by load-order walks, each type
/// walked at most once per call; held on the call context and dropped with it, so nothing survives the call.</summary>
internal sealed class SkyPatcherEditorIdTable
{
    /// <summary>The record the game finds by one name, its getter type and the plugin that first loaded it, and every other record seen under it.</summary>
    internal sealed class Entry(FormKey first, Type getter, string type, int position, string plugin)
    {
        internal FormKey First { get; private set; } = first;
        internal Type Getter { get; private set; } = getter;
        internal string Type { get; private set; } = type;
        internal string Plugin { get; private set; } = plugin;
        internal HashSet<FormKey>? Others;
        int _position = position;

        /// <summary>One more record seen under the name; one loaded earlier, from a later walk of another type, takes the name.</summary>
        internal void See(FormKey fk, Type getter, string type, int position, string plugin)
        {
            if (fk == First) return;
            if (position >= _position) { (Others ??= new()).Add(fk); return; }
            (Others ??= new()).Add(First);
            Others.Remove(fk);
            (First, Getter, Type, _position, Plugin) = (fk, getter, type, position, plugin);
        }

        /// <summary>Whether a lookup scoped to these types finds the record: the game's map is untyped, so a record of another type holding the name first hides it.</summary>
        internal bool In(IReadOnlyList<Type>? scope) => scope is null || scope.Any(t => t.IsAssignableFrom(Getter));
    }

    readonly LoadOrderResolver.IndexView _view;
    readonly string? _tweaksMissing;
    readonly HashSet<Type> _swept = new();
    readonly HashSet<Type> _incomplete = new();
    readonly Dictionary<string, Entry> _names = new(StringComparer.OrdinalIgnoreCase);
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
        var fresh = types.Where(t => !_swept.Contains(t)).Distinct().ToList();
        if (fresh.Count == 0) return;
        _swept.UnionWith(fresh);
        Interlocked.Increment(ref _view.Counters.EditorIdSweeps);
        var names = new Dictionary<Type, (Type Getter, string Name)?>();
        int before = _unreadable.Count;
        foreach (var (fk, body, position, plugin) in _view.RecordVersionsOfType(fresh, _unreadable))
        {
            if (body.EditorID is not { Length: > 0 } eid) continue;
            var runtime = body.GetType();
            if (!names.TryGetValue(runtime, out var kind))
            {
                var getter = fresh.FirstOrDefault(t => t.IsInstanceOfType(body));
                var name = getter is null ? null : TypeLookup.DisplayNames(new[] { getter })?.FirstOrDefault();
                // Without po3 Tweaks the game keeps no name of this type, so it neither finds nor hides one.
                names[runtime] = kind = getter is null || name is null || SkyPatcherEditorIds.Gap(_tweaksMissing, name) is not null
                    ? null : (getter, name);
            }
            if (kind is not { } k) continue;
            if (!_names.TryGetValue(eid, out var e)) _names[eid] = new Entry(fk, k.Getter, k.Name, position, plugin);
            else e.See(fk, k.Getter, k.Name, position, plugin);
        }
        if (_unreadable.Count > before) _incomplete.UnionWith(fresh);
    }

    /// <summary>The entry the game holds for <paramref name="editorId"/>, after covering <paramref name="scope"/>; null when none was loaded, and <paramref name="incomplete"/> when a walk that could hold it missed a plugin.</summary>
    internal Entry? Find(string editorId, IReadOnlyList<Type>? scope, out bool incomplete)
    {
        if (scope is not null) Cover(scope);
        incomplete = scope is null ? _incomplete.Count > 0 : scope.Any(_incomplete.Contains);
        return _names.GetValueOrDefault(editorId);
    }
}
