using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;

namespace HousecarlCore;

/// <summary>
/// Many record bodies, ONE walk per plugin.
///
/// <para><see cref="LoadOrderResolver.IndexView.GetRecord"/> finds a record by enumerating its plugin from the top,
/// so N records cost N whole-plugin enumerations — and Mutagen constructs an overlay wrapper for every record it
/// steps over, so the cost is (records in that plugin) per fetch, not per hit. A bulk write asking for one record at
/// a time therefore allocates megabytes PER OP: a 2,484-op apply on a real order churned about 2 MB an op, which the
/// GC heap then holds after the call (#723). <see cref="LoadOrderResolver.CollectRecords"/> already answers many keys
/// from one plugin in one pass; this is the bookkeeping that lets a caller who discovers its wants one at a time use
/// it — declare every (plugin, record) the call will need, gather once, then read them back.</para>
///
/// <para>Answers cannot differ from the one-at-a-time path: <see cref="Body"/> falls back to the single fetch for a
/// pair that was never declared, and for a plugin <see cref="Gather"/> could not walk — so the exception a caller
/// used to see is the one it still sees.</para>
///
/// <para>The third gather in the tree, and the one the WRITE lanes need. <see cref="WinnerBodies"/> (#251) derives
/// the winner itself and REPORTS an unreadable plugin to its caller, because a scan has to say which plugins it could
/// not read; <c>BodyPrefetch.Chunk</c> (#582, in the server assembly) walks a plugin only when a rendered row asks
/// for it and answers null for anything it did not gather, because a render stops mid-chunk and must not pay for
/// rows nobody sees. A write reads every body it declared, all-or-nothing, and must raise each fault from the edit
/// that owns it — hence eager gather plus per-record fallback. Folding the three into one primitive is #756.</para>
/// </summary>
public sealed class BodyGather
{
    readonly LoadOrderResolver.IndexView _view;
    readonly LoadOrderResolver.OverlaySession _session;
    readonly Dictionary<string, HashSet<FormKey>> _declared = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Dictionary<FormKey, IMajorRecordGetter>> _held = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _walked = new(StringComparer.OrdinalIgnoreCase);

    public BodyGather(LoadOrderResolver.IndexView view, LoadOrderResolver.OverlaySession session)
    { _view = view; _session = session; }

    /// <summary>Declare that this call will need <paramref name="fk"/>'s body out of <paramref name="pluginName"/>.</summary>
    public void Want(string pluginName, FormKey fk)
    {
        if (!_declared.TryGetValue(pluginName, out var set)) _declared[pluginName] = set = new HashSet<FormKey>();
        set.Add(fk);
    }

    /// <summary>Walk each declared plugin ONCE, collecting every body declared for it.</summary>
    public void Gather()
    {
        foreach (var (plugin, keys) in _declared)
        {
            if (_walked.Contains(plugin)) continue;
            if (!_held.TryGetValue(plugin, out var sink)) _held[plugin] = sink = new Dictionary<FormKey, IMajorRecordGetter>();
            // Any fault — a plugin that opened at index time but cannot be opened now, or a record the walk cannot
            // parse — leaves this plugin unwalked, so Body falls back to the per-record fetch and the caller sees the
            // same fault, from the same place in its own loop, that it saw before this existed. Swallowing it here
            // would move a named per-record refusal to an up-front throw that names no record.
            try { _view.CollectRecords(_session, plugin, keys, null, sink); }
            catch (Exception) { continue; }
            _walked.Add(plugin);
        }
    }

    /// <summary>The gathered body, or a single fetch when this pair was never gathered. Null means the plugin does
    /// not hold the record — the same answer <see cref="LoadOrderResolver.IndexView.GetRecord"/> gives.</summary>
    public IMajorRecordGetter? Body(string pluginName, FormKey fk)
    {
        if (_held.TryGetValue(pluginName, out var sink) && sink.TryGetValue(fk, out var body)) return body;
        // Gathered and still absent IS the answer: the walk saw the whole plugin, so a second one cannot find it.
        if (_walked.Contains(pluginName) && _declared.TryGetValue(pluginName, out var keys) && keys.Contains(fk)) return null;
        return _view.GetRecord(_session, pluginName, fk);
    }
}
