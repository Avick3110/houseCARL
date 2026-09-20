using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;

namespace HousecarlCore;

/// <summary>Many record bodies, ONE walk per plugin — the one primitive every lane that gathers in bulk reads
/// through. Declare every (plugin, record) the call will need, gather, then read them back. The lanes differ by
/// constructor option, never by a second copy: <paramref name="onDemand"/> defers a plugin's walk to the first
/// <see cref="Body"/> that wants it, <see cref="Absent"/> picks what an undeclared pair answers, and
/// <see cref="Faults"/> names the plugins whose walk faulted. Contract in docs/architecture/read-engine.md.</summary>
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
    readonly IReadOnlyList<Type>? _getterTypes;
    readonly Absent _absent;
    readonly bool _onDemand;
    readonly CancellationToken _ct;
    readonly Dictionary<string, HashSet<FormKey>> _declared = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Dictionary<FormKey, IMajorRecordGetter>> _held = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _attempted = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _walked = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, PluginUnreadableException> _faults = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The plugins whose walk faulted, in declaration order, with the cause; every record declared for one
    /// of them is answered by <see cref="Absent"/> instead. Non-empty means the call ran the slow path.</summary>
    public IReadOnlyDictionary<string, PluginUnreadableException> Faults => _faults;

    /// <summary>The faulted plugins by name alone.</summary>
    public IReadOnlyCollection<string> Faulted => _faults.Keys;

    /// <param name="getterTypes">The caller's own type scope, which narrows each plugin's walk to its GRUPs.</param>
    /// <param name="absent">What <see cref="Body"/> answers for a pair this gather does not hold.</param>
    /// <param name="onDemand">Walk a plugin on the first <see cref="Body"/> that wants it, not in <see cref="Gather"/>.</param>
    /// <param name="ct">Checked before each plugin walk.</param>
    public BodyGather(LoadOrderResolver.IndexView view, LoadOrderResolver.OverlaySession session,
                      IReadOnlyList<Type>? getterTypes = null, Absent absent = Absent.Seek,
                      bool onDemand = false, CancellationToken ct = default)
    {
        _view = view; _session = session; _getterTypes = getterTypes;
        _absent = absent; _onDemand = onDemand; _ct = ct;
    }

    /// <summary>Declare that this call will need <paramref name="fk"/>'s body out of <paramref name="pluginName"/>,
    /// per (plugin, record): the same FormKey out of the winner and out of the source plugin is two bodies.</summary>
    public void Want(string pluginName, FormKey fk)
    {
        // A plugin already attempted is never walked again, so a key declared this late falls to Absent instead.
        if (_attempted.Contains(pluginName)) return;
        if (!_declared.TryGetValue(pluginName, out var set)) _declared[pluginName] = set = new HashSet<FormKey>();
        set.Add(fk);
    }

    /// <summary>Walk each declared plugin ONCE, collecting every body declared for it; a no-op when the gather is
    /// deferred. Once per gather, faults included — declare everything, then gather.</summary>
    public void Gather()
    {
        if (_onDemand) return;
        foreach (var plugin in _declared.Keys) Walk(plugin);
    }

    /// <summary>The gathered body, or <see cref="Absent"/>'s answer when this gather does not hold the pair. Null
    /// means the plugin does not hold the record, the same answer <c>GetRecord</c> gives.</summary>
    public IMajorRecordGetter? Body(string pluginName, FormKey fk)
    {
        if (_onDemand && !_attempted.Contains(pluginName) && _declared.ContainsKey(pluginName)) Walk(pluginName);
        if (_held.TryGetValue(pluginName, out var sink) && sink.TryGetValue(fk, out var body)) return body;
        if (_absent == Absent.Null) return null;
        // Gathered and still absent IS the answer: the walk saw the whole plugin, so a second one cannot find it.
        if (_walked.Contains(pluginName) && _declared.TryGetValue(pluginName, out var keys) && keys.Contains(fk)) return null;
        return _view.GetRecord(_session, pluginName, fk);
    }

    /// <summary>Every body this gather holds, keyed by record alone, for a lane whose wants are one plugin each.</summary>
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
        // A fault reading the PLUGIN leaves it unwalked, so Body answers by Absent and the caller sees the same
        // fault, from the same place in its own loop, that it saw before this existed.
        if (WalkOnce(_view, _session, plugin, keys, _getterTypes, sink) is { } fault) { _faults[plugin] = fault; return; }
        _walked.Add(plugin);
    }

    /// <summary>One plugin's walk, guarded: every declared key into <paramref name="sink"/> in one enumeration,
    /// answering the fault that stopped it or null. Out-of-memory and cancellation are rethrown. Public for the
    /// tree fold, whose wanted set and type scope are settled at walk time.</summary>
    public static PluginUnreadableException? WalkOnce(
        LoadOrderResolver.IndexView view, LoadOrderResolver.OverlaySession session, string plugin,
        IReadOnlyCollection<FormKey> keys, IReadOnlyList<Type>? getterTypes,
        IDictionary<FormKey, IMajorRecordGetter> sink)
    {
        try { view.CollectRecords(session, plugin, keys, getterTypes, sink); return null; }
        catch (OutOfMemoryException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (PluginUnreadableException ex) { return ex; }
        catch (Exception ex) { return new PluginUnscannableException(plugin, ex); }
    }
}
