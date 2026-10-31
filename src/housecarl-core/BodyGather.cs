using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;

namespace HousecarlCore;

/// <summary>Many record bodies, each (plugin, group) read once — the one primitive every lane that gathers in bulk reads
/// through; contract in docs/architecture/read-engine.md.</summary>
public sealed class BodyGather
{
    /// <summary>What <see cref="Body"/> answers for a pair this gather does not hold.</summary>
    public enum Absent
    {
        /// <summary>Fetch it one at a time — the answer and the exception are the one-at-a-time path's.</summary>
        Seek,
        /// <summary>Answer null and let the caller's own read raise whatever it raises.</summary>
        Null,
    }

    readonly LoadOrderResolver.IndexView _view;
    readonly LoadOrderResolver.OverlaySession _session;
    readonly Absent _absent;
    readonly bool _onDemand;
    readonly CancellationToken _ct;
    readonly Dictionary<string, HashSet<FormKey>> _declared = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Dictionary<FormKey, IMajorRecordGetter>> _held = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _attempted = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _walked = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, PluginUnreadableException> _faults = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The plugins whose walk faulted, in declaration order, with the cause.</summary>
    public IReadOnlyDictionary<string, PluginUnreadableException> Faults => _faults;

    public IReadOnlyCollection<string> Faulted => _faults.Keys;

    /// <param name="absent">What <see cref="Body"/> answers for a pair this gather does not hold.</param>
    /// <param name="onDemand">Walk a plugin on the first <see cref="Body"/> that wants it, not in <see cref="Gather"/>.</param>
    public BodyGather(LoadOrderResolver.IndexView view, LoadOrderResolver.OverlaySession session,
                      Absent absent = Absent.Seek,
                      bool onDemand = false, CancellationToken ct = default)
    {
        _view = view; _session = session;
        _absent = absent; _onDemand = onDemand; _ct = ct;
    }

    /// <summary>Each key's copy in <paramref name="pluginOf"/>'s plugin, gathered at once and answered by <see cref="Body"/>
    /// under <see cref="Absent.Seek"/>, so a faulted plugin's key raises the per-record fault it always did.</summary>
    public static Func<FormKey, IMajorRecordGetter?> Copies(LoadOrderResolver.IndexView view, LoadOrderResolver.OverlaySession session,
                                                            IEnumerable<FormKey> keys, Func<FormKey, string?> pluginOf)
    {
        var gather = new BodyGather(view, session);
        var plugins = new Dictionary<FormKey, string>();
        foreach (var fk in keys)
            if (pluginOf(fk) is { } p) { plugins[fk] = p; gather.Want(p, fk); }
        gather.Gather();
        return fk => plugins.TryGetValue(fk, out var p) ? gather.Body(p, fk) : null;
    }

    /// <summary><see cref="Copies"/> of each key's winning copy.</summary>
    public static Func<FormKey, IMajorRecordGetter?> Winners(LoadOrderResolver.IndexView view, LoadOrderResolver.OverlaySession session, IEnumerable<FormKey> keys)
        => Copies(view, session, keys, fk => view.ResolveWinner(fk)?.WinnerPlugin);

    /// <summary>Declare a body this call will need, per (plugin, record), not per record.</summary>
    public void Want(string pluginName, FormKey fk)
    {
        // A plugin already attempted is never walked again, so a key declared this late falls to Absent instead.
        if (_attempted.Contains(pluginName)) return;
        if (!_declared.TryGetValue(pluginName, out var set)) _declared[pluginName] = set = new HashSet<FormKey>();
        set.Add(fk);
    }

    /// <summary>Read the declared pairs through the door, each (plugin, group) once — a no-op when deferred, and once per gather, faults included.
    /// With <paramref name="perGroup"/>, each group's asked keys are handed over while only that group's bodies are held.</summary>
    public void Gather(Action<IReadOnlyCollection<FormKey>>? perGroup = null)
    {
        if (_onDemand) return;
        var plugins = _declared.Keys.Where(_attempted.Add).ToList();
        var pairs = new List<(string, FormKey)>();
        foreach (var plugin in plugins)
        {
            _held[plugin] = new Dictionary<FormKey, IMajorRecordGetter>(_declared[plugin].Count);
            foreach (var fk in _declared[plugin]) pairs.Add((plugin, fk));
        }
        var faults = new Dictionary<string, PluginUnreadableException>(StringComparer.OrdinalIgnoreCase);
        OrderedGather.ForEachGroup(_view, _session, pairs, (plugin, bodies, asked) =>
        {
            foreach (var (fk, body) in bodies) _held[plugin][fk] = body;
            if (perGroup is null)
            {
                // A group walked to the end settles its keys even in a plugin another group faulted in.
                foreach (var fk in asked) if (!_faultOf.ContainsKey((plugin, fk))) _settled.Add((plugin, fk));
                return;
            }
            perGroup(asked);
            _held[plugin].Clear();
        }, faults, _ct, faulted: OnFaulted);
        // A faulted plugin keeps what it yielded and stays unwalked; its faulted groups' keys answer as the per-record seek would.
        // A per-group gather dropped what it held, so its plugins stay unwalked too and a later Body seeks rather than answering null.
        foreach (var plugin in plugins)
            if (faults.TryGetValue(plugin, out var fault)) _faults[plugin] = fault;
            else if (perGroup is null) _walked.Add(plugin);
    }

    /// <summary>The gathered body, or <see cref="Absent"/>'s answer when this gather does not hold the pair.</summary>
    public IMajorRecordGetter? Body(string pluginName, FormKey fk)
    {
        if (_onDemand && !_attempted.Contains(pluginName) && _declared.ContainsKey(pluginName)) Walk(pluginName);
        if (_held.TryGetValue(pluginName, out var sink) && sink.TryGetValue(fk, out var body)) return body;
        if (_absent == Absent.Null) return null;
        // Gathered and still absent IS the answer: the walk saw the whole plugin, so a second one cannot find it.
        if (_walked.Contains(pluginName) && _declared.TryGetValue(pluginName, out var keys) && keys.Contains(fk)) return null;
        if (_settled.Contains((pluginName, fk))) return null;
        if (_faultOf.TryGetValue((pluginName, fk), out var group)) return group.Answer(this, pluginName, fk);
        return _view.GetRecord(_session, pluginName, fk);
    }

    // Each key a faulted group did not yield, with that group: its answer is the per-record seek's, worked out once per group.
    readonly Dictionary<(string, FormKey), FaultedGroup> _faultOf = new(PairComparer.Instance);
    readonly HashSet<(string, FormKey)> _settled = new(PairComparer.Instance);

    sealed class PairComparer : IEqualityComparer<(string Plugin, FormKey Key)>
    {
        public static readonly PairComparer Instance = new();
        public bool Equals((string Plugin, FormKey Key) a, (string Plugin, FormKey Key) b)
            => a.Key == b.Key && string.Equals(a.Plugin, b.Plugin, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string Plugin, FormKey Key) p)
            => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(p.Plugin), p.Key);
    }

    void OnFaulted(string plugin, IReadOnlyCollection<FormKey> asked, PluginUnreadableException fault)
    {
        var group = new FaultedGroup(asked, fault);
        foreach (var fk in asked) _faultOf.TryAdd((plugin, fk), group);
    }

    /// <summary>A group the door's walk faulted on. A seek for one of its keys walks only that key's seek type, so a group
    /// joined from several types is walked again once per type to learn which keys each type's own walk reaches; a group
    /// of one type, or a plugin that will not open, already raised what each seek would.</summary>
    sealed class FaultedGroup(IReadOnlyCollection<FormKey> asked, PluginUnreadableException fault)
    {
        Dictionary<FormKey, IMajorRecordGetter>? _found;
        Dictionary<FormKey, Exception>? _raised;

        public IMajorRecordGetter? Answer(BodyGather g, string plugin, FormKey fk)
        {
            if (g._view.LacksCopy(plugin, fk)) return null;
            if (fault is PluginUnscannableException)
            {
                if (_found is null) Split(g, plugin);
                if (_found!.TryGetValue(fk, out var body)) return body;
                if (_raised!.TryGetValue(fk, out var ex)) Rethrow(ex);
                return null;
            }
            Rethrow(fault.InnerException ?? fault);
            return null;
        }

        void Split(BodyGather g, string plugin)
        {
            _found = new(); _raised = new();
            var types = asked.GroupBy(fk => g._view.GroupOf(plugin, fk).Seek).ToList();
            if (types.Count == 1) { foreach (var fk in asked) _raised[fk] = fault.InnerException ?? fault; return; }
            foreach (var type in types)
            {
                var keys = type.ToHashSet();
                var sink = new Dictionary<FormKey, IMajorRecordGetter>(keys.Count);
                var walk = WalkOnce(g._view, g._session, plugin, keys, sink);
                foreach (var fk in keys)
                    if (sink.TryGetValue(fk, out var body)) _found[fk] = body;
                    else if (walk is not null) _raised[fk] = walk.InnerException ?? walk;
            }
        }

        static void Rethrow(Exception ex) => System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex).Throw();
    }

    public void CopyInto(IDictionary<FormKey, IMajorRecordGetter> sink)
    {
        foreach (var held in _held.Values)
            foreach (var (fk, body) in held) sink[fk] = body;
    }

    void Walk(string plugin)
    {
        if (!_attempted.Add(plugin)) return;
        if (!_declared.TryGetValue(plugin, out var keys)) return;
        if (!_held.TryGetValue(plugin, out var sink)) _held[plugin] = sink = new Dictionary<FormKey, IMajorRecordGetter>(keys.Count);
        _ct.ThrowIfCancellationRequested();   // a client that aborted stops the gather between plugin walks
        // A fault reading the PLUGIN leaves it unwalked, so Body answers by Absent with the same fault as before.
        if (WalkOnce(_view, _session, plugin, keys, sink) is { } fault) { _faults[plugin] = fault; return; }
        _walked.Add(plugin);
    }

    /// <summary>One plugin's walk, guarded: every declared key into <paramref name="sink"/> in one enumeration,
    /// answering the fault that stopped it or null. Out-of-memory and cancellation are rethrown — neither is this
    /// plugin's fault. Public for the tree fold alone, which settles its wanted set at walk time.</summary>
    public static PluginUnreadableException? WalkOnce(
        LoadOrderResolver.IndexView view, LoadOrderResolver.OverlaySession session, string plugin,
        IReadOnlyCollection<FormKey> keys, IDictionary<FormKey, IMajorRecordGetter> sink)
    {
        try { view.CollectRecords(session, plugin, keys, sink); return null; }
        catch (OutOfMemoryException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (LoadOrderChangedMidCallException) { throw; }
        catch (PluginUnreadableException ex) { return ex; }
        catch (Exception ex) { return new PluginUnscannableException(plugin, ex); }
    }
}
