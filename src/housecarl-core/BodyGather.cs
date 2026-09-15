using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;

namespace HousecarlCore;

/// <summary>
/// Many record bodies, ONE walk per plugin — the one primitive every lane that gathers in bulk reads through (#756).
///
/// <para><see cref="LoadOrderResolver.IndexView.GetRecord"/> finds a record by enumerating its plugin from the top,
/// so N records cost N whole-plugin enumerations — and Mutagen constructs an overlay wrapper for every record it
/// steps over, so the cost is (records in that plugin) per fetch, not per hit. A bulk write asking for one record at
/// a time therefore allocates megabytes PER OP: a 2,484-op apply on a real order churned about 2 MB an op, which the
/// GC heap then holds after the call (#723). <see cref="LoadOrderResolver.CollectRecords"/> already answers many keys
/// from one plugin in one pass; this is the bookkeeping that lets a caller who discovers its wants one at a time use
/// it — declare every (plugin, record) the call will need, gather, then read them back.</para>
///
/// <para>The lanes differ in three ways, and each difference is a constructor option rather than a second copy of
/// this class:</para>
/// <list type="bullet">
/// <item><description><paramref name="onDemand"/> — a WRITE reads every body it declared, so it gathers up front.
/// A RENDER stops at max_chars mid-chunk, so gathering its whole chunk would enumerate plugins for rows nobody
/// sees; deferred, a plugin is walked on the first row that actually asks for it and then answers that plugin's
/// whole share.</description></item>
/// <item><description><see cref="Absent"/> — whether a pair this gather does not hold falls back to the per-record
/// fetch or answers null. A write must raise each fault from the edit that owns it, so its fallback keeps the
/// one-at-a-time path's exact answer AND its exact exception; a render's row raises its own fault on its own read,
/// so a second fetch there would only be a second error path.</description></item>
/// <item><description><see cref="Faults"/> — the plugins whose walk faulted, named in declaration order with the
/// cause. A scan REPORTS them (an unreadable winner is a whole-plugin coverage gap the response has to disclose);
/// a write only wants to know it ran the slow path; a render ignores them.</description></item>
/// </list>
///
/// <para>Two faults are never this plugin's: <see cref="OutOfMemoryException"/> and
/// <see cref="OperationCanceledException"/> are rethrown rather than recorded. The fallback costs one whole-plugin
/// walk per declared record, which is the exact load this class exists to remove, and paying it because memory ran
/// out or the client went away makes the failure worse rather than recovering from it.</para>
/// </summary>
public sealed class BodyGather
{
    /// <summary>What <see cref="Body"/> answers for a pair this gather does not hold.</summary>
    public enum Absent
    {
        /// <summary>Fetch it one at a time. The answer and the exception are the one-at-a-time path's, so a caller
        /// that never declared a pair, or declared one whose plugin faulted, cannot tell this gather existed.</summary>
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

    /// <summary>The plugins whose walk faulted, in declaration order, with the cause. Every record declared for one
    /// of them is answered by <see cref="Absent"/> instead — for <see cref="Absent.Seek"/> the answers are the same
    /// and the cost is the one this class exists to remove. Empty in the normal case; non-empty means the call ran
    /// the slow path, and a caller that reports coverage can name the file.</summary>
    public IReadOnlyDictionary<string, PluginUnreadableException> Faults => _faults;

    /// <summary>The faulted plugins by name alone, for a caller that reports that it ran the slow path rather than
    /// why.</summary>
    public IReadOnlyCollection<string> Faulted => _faults.Keys;

    /// <param name="getterTypes">The caller's own type scope when it has one, which narrows each plugin's walk to
    /// the GRUPs those types live in.</param>
    /// <param name="absent">What <see cref="Body"/> answers for a pair this gather does not hold.</param>
    /// <param name="onDemand">Walk a plugin on the first <see cref="Body"/> that wants it, rather than in
    /// <see cref="Gather"/>.</param>
    /// <param name="ct">Checked before each plugin walk, so a client that aborted stops the gather one walk later
    /// rather than at the end of the chunk.</param>
    public BodyGather(LoadOrderResolver.IndexView view, LoadOrderResolver.OverlaySession session,
                      IReadOnlyList<Type>? getterTypes = null, Absent absent = Absent.Seek,
                      bool onDemand = false, CancellationToken ct = default)
    {
        _view = view; _session = session; _getterTypes = getterTypes;
        _absent = absent; _onDemand = onDemand; _ct = ct;
    }

    /// <summary>Declare that this call will need <paramref name="fk"/>'s body out of <paramref name="pluginName"/>.
    /// Declared per (plugin, record), not per record: a forward lane wants the same FormKey out of the winner and
    /// out of the source plugin, and those are two different bodies.</summary>
    public void Want(string pluginName, FormKey fk)
    {
        // A plugin already attempted is never walked again, so a key declared this late would never be looked for —
        // and recording it would make the walked-and-absent arm of Body answer null for a record the walk never
        // asked about. It falls to Absent instead, which is what a pair this gather does not hold is for.
        if (_attempted.Contains(pluginName)) return;
        if (!_declared.TryGetValue(pluginName, out var set)) _declared[pluginName] = set = new HashSet<FormKey>();
        set.Add(fk);
    }

    /// <summary>Walk each declared plugin ONCE, collecting every body declared for it. A no-op when the gather is
    /// deferred — each plugin is then walked by the first <see cref="Body"/> that wants it.
    /// <para>ONCE per gather, faults included: a plugin whose walk faulted is not walked again by a second
    /// <see cref="Gather"/>, and keys declared for an already-attempted plugin are answered by <see cref="Absent"/>
    /// rather than by a second walk. Declare everything, then gather — which is what every lane does.</para></summary>
    public void Gather()
    {
        if (_onDemand) return;
        foreach (var plugin in _declared.Keys) Walk(plugin);
    }

    /// <summary>The gathered body, or <see cref="Absent"/>'s answer when this gather does not hold the pair. Null
    /// means the plugin does not hold the record — the same answer
    /// <see cref="LoadOrderResolver.IndexView.GetRecord"/> gives.</summary>
    public IMajorRecordGetter? Body(string pluginName, FormKey fk)
    {
        if (_onDemand && !_attempted.Contains(pluginName) && _declared.ContainsKey(pluginName)) Walk(pluginName);
        if (_held.TryGetValue(pluginName, out var sink) && sink.TryGetValue(fk, out var body)) return body;
        if (_absent == Absent.Null) return null;
        // Gathered and still absent IS the answer: the walk saw the whole plugin, so a second one cannot find it.
        if (_walked.Contains(pluginName) && _declared.TryGetValue(pluginName, out var keys) && keys.Contains(fk)) return null;
        return _view.GetRecord(_session, pluginName, fk);
    }

    /// <summary>Every body this gather holds, keyed by record alone — for a lane whose wants are one plugin per
    /// record (a winner, a row's source) and which reads them back by FormKey.</summary>
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
        // A fault reading the PLUGIN — one that opened at index time but cannot be opened now, or a record the walk
        // cannot parse — leaves this plugin unwalked, so Body answers by Absent and the caller sees the same fault,
        // from the same place in its own loop, that it saw before this existed. Swallowing it here would move a
        // named per-record refusal to an up-front throw that names no record.
        if (WalkOnce(_view, _session, plugin, keys, _getterTypes, sink) is { } fault) { _faults[plugin] = fault; return; }
        _walked.Add(plugin);
    }

    /// <summary>One plugin's walk, guarded: every declared key of <paramref name="plugin"/> into
    /// <paramref name="sink"/> in one enumeration. Answers the fault that stopped the walk, or null when it
    /// finished — the two causes told apart, because <see cref="LoadOrderResolver.CollectRecords"/> names an OPEN
    /// failure itself, so a file another program is holding open reads as that and a fault from the walk after a
    /// good open reads as the plugin having changed instead. <see cref="OutOfMemoryException"/> and
    /// <see cref="OperationCanceledException"/> are rethrown: neither is a fault of this plugin, and calling a
    /// readable master a coverage gap because the machine ran out of memory misnames the failure.
    /// <para>Public for the one lane whose walk this class cannot own: the tree fold is plugin-major and computes
    /// each plugin's wanted set and type scope AT walk time, from which rows are still live, so there is nothing to
    /// declare up front. It keeps its own loop and shares this rule.</para></summary>
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
