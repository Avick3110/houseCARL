using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlMcp;

/// <summary>The per-row note for a record whose type the game keys by EditorID at runtime, when another record of
/// that type in the active order carries the same EditorID. Built once per call, on the first such row a render asks
/// about, off the call's pinned view; held by the pin and dropped with it. Contract in docs/architecture/read-engine.md.</summary>
internal sealed class SharedEditorIds
{
    /// <summary>The record types the game keeps one runtime object per EditorID for: data, one entry each.</summary>
    static readonly Type[] EngineKeyed = { typeof(IGameSettingGetter) };

    /// <summary>One shared name: its members in load order of their winners, the last one the copy the game uses.</summary>
    sealed record Group(string EditorId, List<(FormKey Key, string Winner)> Members);

    readonly LoadOrderService.ViewPin _pin;
    Dictionary<FormKey, Group>? _byKey;
    List<string>? _unread;

    internal SharedEditorIds(LoadOrderService.ViewPin pin) => _pin = pin;

    /// <summary>The note a row owes, or null: no pin, a type the game does not key by name, or a name no other record shares.</summary>
    internal static string? Note(LoadOrderService.ViewPin? pin, FormKey fk, string? type) =>
        pin is not null && IsKeyed(type) ? pin.SharedNames.NoteFor(fk) : null;

    /// <summary>Is this row's type (its rendered name) one the game keys by EditorID?</summary>
    static bool IsKeyed(string? type)
    {
        if (string.IsNullOrEmpty(type)) return false;
        var getter = typeof(IGameSettingGetter).Assembly.GetType($"Mutagen.Bethesda.Skyrim.I{type}Getter");
        return getter is not null && EngineKeyed.Any(k => k.IsAssignableFrom(getter));
    }

    string? NoteFor(FormKey fk)
    {
        Build();
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

    /// <summary>One winner scan per keyed type over the pinned view, grouped by EditorID; only shared names are kept.</summary>
    void Build()
    {
        if (_byKey is not null) return;
        var view = _pin.View;
        var unread = new List<PluginUnreadableException>();
        var byName = new Dictionary<string, Group>(StringComparer.OrdinalIgnoreCase);
        foreach (var keyed in EngineKeyed)
        {
            byName.Clear();
            foreach (var (fk, _, body) in view.WinnerRecordsOfType(new[] { keyed }, unread))
            {
                if (string.IsNullOrEmpty(body.EditorID) || view.ResolveWinner(fk) is not { } w) continue;
                if (!byName.TryGetValue(body.EditorID, out var g)) byName[body.EditorID] = g = new Group(body.EditorID, new());
                g.Members.Add((fk, w.WinnerPlugin));   // the stream walks plugins low to high, so members arrive in load order
            }
            _byKey ??= new Dictionary<FormKey, Group>();
            foreach (var g in byName.Values.Where(g => g.Members.Count > 1))
                foreach (var m in g.Members) _byKey[m.Key] = g;
        }
        _byKey ??= new Dictionary<FormKey, Group>();
        _unread = unread.Select(u => u.PluginName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
