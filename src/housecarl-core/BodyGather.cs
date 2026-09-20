using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;

namespace HousecarlCore;

/// <summary>Many record bodies, ONE walk per plugin — the one primitive every lane that gathers in bulk reads
/// through; contract in docs/architecture/read-engine.md.</summary>
public sealed class BodyGather
{
    public enum Absent
    {
        Seek,
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

    /// <summary>The plugins whose walk faulted, in declaration order, with the cause.</summary>
    public IReadOnlyDictionary<string, PluginUnreadableException> Faults => _faults;

    public IReadOnlyCollection<string> Faulted => _faults.Keys;

    /// <param name="absent">What <see cref="Body"/> answers for a pair this gather does not hold.</param>
    /// <param name="onDemand">Walk a plugin on the first <see cref="Body"/> that wants it, not in <see cref="Gather"/>.</param>
    public BodyGather(LoadOrderResolver.IndexView view, LoadOrderResolver.OverlaySession session,
                      IReadOnlyList<Type>? getterTypes = null, Absent absent = Absent.Seek,
                      bool onDemand = false, CancellationToken ct = default)
    {
        _view = view; _session = session; _getterTypes = getterTypes;
        _absent = absent; _onDemand = onDemand; _ct = ct;
    }

    /// <summary>Declare a body this call will need, per (plugin, record), not per record.</summary>
    public void Want(string pluginName, FormKey fk)
    {
        // A plugin already attempted is never walked again, so a key declared this late falls to Absent instead.
        if (_attempted.Contains(pluginName)) return;
        if (!_declared.TryGetValue(pluginName, out var set)) _declared[pluginName] = set = new HashSet<FormKey>();
        set.Add(fk);
    }

    /// <summary>Walk each declared plugin ONCE — a no-op when deferred, and once per gather, faults included.</summary>
    public void Gather()
    {
        if (_onDemand) return;
        foreach (var plugin in _declared.Keys) Walk(plugin);
    }

    /// <summary>The gathered body, or <see cref="Absent"/>'s answer when this gather does not hold the pair.</summary>
    public IMajorRecordGetter? Body(string pluginName, FormKey fk)
    {
        if (_onDemand && !_attempted.Contains(pluginName) && _declared.ContainsKey(pluginName)) Walk(pluginName);
        if (_held.TryGetValue(pluginName, out var sink) && sink.TryGetValue(fk, out var body)) return body;
        if (_absent == Absent.Null) return null;
        // Gathered and still absent IS the answer: the walk saw the whole plugin, so a second one cannot find it.
        if (_walked.Contains(pluginName) && _declared.TryGetValue(pluginName, out var keys) && keys.Contains(fk)) return null;
        return _view.GetRecord(_session, pluginName, fk);
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
        if (WalkOnce(_view, _session, plugin, keys, _getterTypes, sink) is { } fault) { _faults[plugin] = fault; return; }
        _walked.Add(plugin);
    }

    /// <summary>One plugin's walk, guarded: every declared key into <paramref name="sink"/> in one enumeration,
    /// answering the fault that stopped it or null.</summary>
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
