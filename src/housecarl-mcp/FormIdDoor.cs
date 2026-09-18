using Mutagen.Bethesda.Plugins;
using HousecarlCore;

namespace HousecarlMcp;

/// <summary>The FormID door a tool body parses a list of tokens through; it answers the plugin-qualified form without
/// touching the load order, and captures one index build on the first runtime FormID for every remaining token.</summary>
internal sealed class FormIdDoor
{
    readonly LoadOrderService? _svc;
    readonly bool _write;
    LoadOrderResolver.IndexView? _view;

    FormIdDoor(LoadOrderService? svc, LoadOrderResolver.IndexView? view, bool write = false)
    { _svc = svc; _view = view; _write = write; }

    /// <summary>A door pinned to a build the caller already captured.</summary>
    public static FormIdDoor On(LoadOrderResolver.IndexView view) => new(null, view);

    /// <summary>A door that captures a build on the first runtime FormID, and never if none arrives.</summary>
    public static FormIdDoor For(LoadOrderService svc) => new(svc, null);

    /// <summary>The same door in write mode: a runtime FormID is translated and then refused; reads keep taking it.</summary>
    public static FormIdDoor ForWrite(LoadOrderService svc) => new(svc, null, write: true);

    /// <summary>The build this door captured, or null when no runtime FormID arrived; a caller that scans passes it down.</summary>
    public LoadOrderResolver.IndexView? CapturedView => _view;

    /// <summary>Parse one token, throwing one plain sentence on anything it cannot answer.</summary>
    public FormKey Parse(string? raw)
    {
        // A runtime form with the plugin name still on it is neither notation: say which half to drop.
        if (RuntimeFormId.HybridNote(raw) is { } hybrid) throw new FormatException(hybrid);
        if (!RuntimeFormId.TryParse(raw, out _)) return FormKey.Factory((raw ?? "").Trim());
        _view ??= _svc!.CaptureView();
        var fk = _view.Value.ParseFormId(raw);
        // Translate first, so the refusal can hand back the plugin form to paste in place of the token.
        if (_write)
            throw new WriteRefusal(
                $"'{(raw ?? "").Trim()}' is a runtime FormID, which houseCARL accepts for reading but not for " +
                $"writing, because it names a slot in the load order as it stands rather than a record — write to " +
                $"'{FormIdToken.Of(fk)}' instead.");
        return fk;
    }

    /// <summary>Refuse a runtime FormID, or a hybrid of the two notations, in a slot that may hold something other than
    /// a FormID, returning the sentence or null; anything else is left to the caller's own parser.</summary>
    public string? RuntimeRefusal(string? raw)
    {
        // A hybrid carries a colon, so TryParse says no to it; checked first, or it falls through to the caller.
        if (RuntimeFormId.HybridNote(raw) is { } hybrid) return hybrid;
        if (!RuntimeFormId.TryParse(raw, out _)) return null;
        try { Parse(raw); return null; }
        catch (WriteRefusal ex) { return ex.Message; }
        // A well-formed runtime token the index cannot translate is bad input, not an internal fault.
        catch (FormatException ex) { return ex.Message; }
    }

    /// <summary>What to report for a token this door threw on: a write refusal stands as its own sentence under
    /// <paramref name="prefix"/>, anything else keeps the caller's framing.</summary>
    public static string Sentence(Exception ex, string prefix, string ifMalformed)
        => ex is WriteRefusal ? prefix + ex.Message : ifMalformed;

    /// <summary>A runtime FormID at a <see cref="ForWrite"/> door, distinguishable from a malformed token.</summary>
    internal sealed class WriteRefusal(string message) : FormatException(message);
}
