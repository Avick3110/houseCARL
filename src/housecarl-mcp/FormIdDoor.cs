using Mutagen.Bethesda.Plugins;
using HousecarlCore;

namespace HousecarlMcp;

/// <summary>
/// The FormID door a tool body parses a LIST of tokens through. It answers the plugin-qualified
/// <c>XXXXXX:Plugin.esp</c> form without touching the load order at all, and reaches for the index only when a
/// RUNTIME FormID actually arrives — then once, holding that one build for every remaining token.
///
/// <para>Both halves of that matter. Reaching for the resolver per token re-stats every plugin in the order on
/// every one, and lets the index rebuild mid-list so two tokens in one call resolve against different builds.
/// Reaching for it at all would break the lanes that read a plugin from disk with no active order.</para>
/// </summary>
internal sealed class FormIdDoor
{
    readonly LoadOrderService? _svc;
    readonly bool _write;
    LoadOrderResolver.IndexView? _view;

    FormIdDoor(LoadOrderService? svc, LoadOrderResolver.IndexView? view, bool write = false)
    { _svc = svc; _view = view; _write = write; }

    /// <summary>A door pinned to a build the caller already captured, so its FormIDs and its records describe the
    /// same index.</summary>
    public static FormIdDoor On(LoadOrderResolver.IndexView view) => new(null, view);

    /// <summary>A door that captures a build on the first runtime FormID, and never if none arrives.</summary>
    public static FormIdDoor For(LoadOrderService svc) => new(svc, null);

    /// <summary>The same door in WRITE mode: a runtime FormID is translated and then refused, because it names a
    /// slot in the order as it stands rather than a record, and a re-sort between the parse and the write would
    /// point the same eight digits at a different record. Reads keep taking it.</summary>
    public static FormIdDoor ForWrite(LoadOrderService svc) => new(svc, null, write: true);

    /// <summary>The build this door captured, or null when no runtime FormID arrived and it never reached for one.
    /// A caller that goes on to scan passes it down, so the tokens it parsed and the records it matches come from
    /// the same index build rather than two adjacent ones.</summary>
    public LoadOrderResolver.IndexView? CapturedView => _view;

    /// <summary>Parse one token — see <see cref="LoadOrderResolver.IndexView.ParseFormId"/>. Throws one plain
    /// sentence on anything it cannot answer, and on a runtime FormID at a <see cref="ForWrite"/> door.</summary>
    public FormKey Parse(string? raw)
    {
        if (!RuntimeFormId.TryParse(raw, out _)) return FormKey.Factory((raw ?? "").Trim());
        _view ??= _svc!.CaptureView();
        var fk = _view.Value.ParseFormId(raw);
        // Translate first, so the refusal can hand back the plugin form to paste in place of the token.
        if (_write)
            throw new WriteRefusal(
                $"'{(raw ?? "").Trim()}' is a runtime FormID, which houseCARL accepts for reading but not for " +
                $"writing, because it names a slot in the load order as it stands rather than a record — write to " +
                $"'{fk.ID:X6}:{fk.ModKey.FileName}' instead.");
        return fk;
    }

    /// <summary>Refuse a runtime FormID in a slot that may hold something OTHER than a FormID (a create's
    /// <c>parent=</c> takes an EditorID too), returning the sentence or null. Anything the door cannot recognise as
    /// a runtime FormID is left to the caller's own parser.</summary>
    public string? RuntimeRefusal(string? raw)
    {
        if (!RuntimeFormId.TryParse(raw, out _)) return null;
        try { Parse(raw); return null; }
        catch (WriteRefusal ex) { return ex.Message; }
        // A well-formed runtime token the index cannot translate (an FF dynamic form, an unoccupied index) is bad
        // input, not an internal fault — hand its sentence back as this record's problem.
        catch (FormatException ex) { return ex.Message; }
    }

    /// <summary>What to report for a token this door threw on. A write refusal is a WELL-FORMED token being
    /// declined, with the form to use already in it, so it stands as its own sentence under
    /// <paramref name="prefix"/>; anything else keeps the caller's own "bad FormID, expected …" framing.</summary>
    public static string Sentence(Exception ex, string prefix, string ifMalformed)
        => ex is WriteRefusal ? prefix + ex.Message : ifMalformed;

    /// <summary>A runtime FormID at a <see cref="ForWrite"/> door — distinguishable so a caller does not wrap it in
    /// its malformed-token sentence.</summary>
    internal sealed class WriteRefusal(string message) : FormatException(message);
}
