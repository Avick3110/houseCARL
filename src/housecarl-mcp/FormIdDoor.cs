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
    LoadOrderResolver.IndexView? _view;

    FormIdDoor(LoadOrderService? svc, LoadOrderResolver.IndexView? view) { _svc = svc; _view = view; }

    /// <summary>A door pinned to a build the caller already captured, so its FormIDs and its records describe the
    /// same index.</summary>
    public static FormIdDoor On(LoadOrderResolver.IndexView view) => new(null, view);

    /// <summary>A door that captures a build on the first runtime FormID, and never if none arrives.</summary>
    public static FormIdDoor For(LoadOrderService svc) => new(svc, null);

    /// <summary>Parse one token — see <see cref="LoadOrderResolver.IndexView.ParseFormId"/>. Throws one plain
    /// sentence on anything it cannot answer.</summary>
    public FormKey Parse(string? raw)
    {
        if (!RuntimeFormId.TryParse(raw, out _)) return FormKey.Factory((raw ?? "").Trim());
        _view ??= _svc!.CaptureView();
        return _view.Value.ParseFormId(raw);
    }
}
