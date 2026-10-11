using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlMcp;

/// <summary>The per-row note for a record whose type the game keys by EditorID at runtime, when another record of
/// that type in the active order carries the same EditorID. Built once per call, from the call's own GMST scan when it
/// ran one, else on the first such row a render asks about; held by the pin and dropped with it. Contract in
/// docs/architecture/read-engine.md.</summary>
internal sealed class SharedEditorIds
{
    /// <summary>The record types the game keeps one runtime object per EditorID for: data, one entry each.</summary>
    static readonly Type[] EngineKeyed = { typeof(IGameSettingGetter) };

    /// <summary>One shared name: its members in load order of their winners, the last one the copy the game uses.</summary>
    sealed record Group(string EditorId, List<(FormKey Key, string Winner)> Members);

    readonly LoadOrderResolver.IndexView _view;
    readonly Dictionary<string, Group>[] _byName =
        EngineKeyed.Select(_ => new Dictionary<string, Group>(StringComparer.OrdinalIgnoreCase)).ToArray();
    readonly HashSet<FormKey> _seen = new();
    Dictionary<FormKey, Group>? _byKey;
    List<string>? _unread;

    internal SharedEditorIds(LoadOrderResolver.IndexView view) => _view = view;

    /// <summary>The note a row owes, or null: no pin, a type the game does not key by name, or a name no other record shares.</summary>
    internal static string? Note(LoadOrderService.ViewPin? pin, FormKey fk, string? type) =>
        pin is not null && IsKeyed(type) ? pin.SharedNames.NoteFor(fk) : null;

    /// <summary>Whether a winner scan of these types reads every winner of every keyed type, so it can feed the lookup.</summary>
    internal static bool Covers(IReadOnlyList<Type>? types, TypeLookup lookup) =>
        types is { Count: > 0 } && EngineKeyed.All(k =>
            lookup.ResolveScope(RecordNaming.StripGetterInterface(k.Name)) is { } arms
            && arms.All(a => types.Any(t => t.IsAssignableFrom(a))));

    /// <summary>Is this row's type (its rendered name) one the game keys by EditorID?</summary>
    static bool IsKeyed(string? type)
    {
        if (string.IsNullOrEmpty(type)) return false;
        var getter = typeof(IGameSettingGetter).Assembly.GetType($"Mutagen.Bethesda.Skyrim.I{type}Getter");
        return getter is not null && EngineKeyed.Any(k => k.IsAssignableFrom(getter));
    }

    /// <summary>The lookup built by its own winner walk of the keyed types over the view.</summary>
    internal static SharedEditorIds Walk(LoadOrderResolver.IndexView view)
    {
        var s = new SharedEditorIds(view);
        var unread = new List<PluginUnreadableException>();
        foreach (var (fk, _, body) in view.WinnerRecordsOfType(EngineKeyed, unread)) s.Add(fk, body);
        return s.Finish(unread);
    }

    /// <summary>One winner from a stream walking plugins low to high; a type the game does not key by name is ignored.</summary>
    internal void Add(FormKey fk, IMajorRecordGetter body)
    {
        for (int k = 0; k < EngineKeyed.Length; k++)
        {
            if (!EngineKeyed[k].IsInstanceOfType(body)) continue;
            if (string.IsNullOrEmpty(body.EditorID) || !_seen.Add(fk) || _view.ResolveWinner(fk) is not { } w) return;
            if (!_byName[k].TryGetValue(body.EditorID, out var g)) _byName[k][body.EditorID] = g = new Group(body.EditorID, new());
            g.Members.Add((fk, w.WinnerPlugin));   // the stream walks plugins low to high, so members arrive in load order
            return;
        }
    }

    /// <summary>Keeps only the shared names and the plugins the stream could not read through; the lookup is then ready.</summary>
    internal SharedEditorIds Finish(IEnumerable<PluginUnreadableException> unread)
    {
        _byKey = new Dictionary<FormKey, Group>();
        foreach (var byName in _byName)
            foreach (var g in byName.Values.Where(g => g.Members.Count > 1))
                foreach (var m in g.Members) _byKey[m.Key] = g;
        foreach (var byName in _byName) byName.Clear();
        _seen.Clear();
        _unread = unread.Select(u => u.PluginName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return this;
    }

    string? NoteFor(FormKey fk)
    {
        string? shared = _byKey!.TryGetValue(fk, out var g) ? Sentence(g, fk) : null;
        if (_unread!.Count == 0) return shared;
        var gap = ReadSentences.SharedEditorIdUnread(_unread);
        return shared is null ? gap : shared + "; " + gap;
    }

    static string Sentence(Group g, FormKey self)
    {
        var last = g.Members[^1];
        var others = g.Members.Where(m => m.Key != self).Select(m => FormIdToken.Of(m.Key)).ToList();
        return ReadSentences.SharedEditorIdNote(g.EditorId, others, last.Winner, FormIdToken.Of(last.Key));
    }
}
