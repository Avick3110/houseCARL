using Mutagen.Bethesda.Plugins;

namespace HousecarlCore;

/// <summary>The one SkyPatcher target resolver: a token holding '|' is <c>Plugin|FormID</c> and never an EditorID, anything
/// else an EditorID looked up through the call's table; every site that turns a token into a record goes through here.
/// Rules and sources: docs/architecture/skypatcher-layer.md.</summary>
public static class SkyPatcherTargets
{
    /// <summary>The record a value names, scoped to a Mutagen type or type group for an EditorID; null for a name literal or a miss.</summary>
    public static FormKey? Resolve(SkyPatcherValue v, string? scope, SkyPatcherOverlay.IFormResolver resolver)
        => v.IsNameLiteral ? null : v.Address is { } a ? FormKeyOf(a) : resolver.ResolveEditorId(v.Raw, scope);

    /// <summary>The record one sub-arg token names, by the same rule.</summary>
    public static FormKey? Resolve(string token, string? scope, SkyPatcherOverlay.IFormResolver resolver)
        => SkyPatcherParse.TryParseAddress(token) is { } a ? FormKeyOf(a) : resolver.ResolveEditorId(token, scope);

    /// <summary>A '|' address to its FormKey; null for one whose FormID side or plugin name cannot be read.</summary>
    public static FormKey? FormKeyOf(FormAddress a) => SkyPatcherOverlay.TryFormKey(a, out var fk) ? fk : null;

    /// <summary>True when the value is looked up by EditorID: no '|' and not a <c>~name~</c> literal.</summary>
    public static bool ByEditorId(SkyPatcherValue v) => !v.IsNameLiteral && v.Address is null;

    /// <summary>What one target says on its line: why it names nothing, or the duplicate rule when its EditorID names more
    /// than one record; null for a target that names one record. Worded after <c>target 'X' (in a K) </c>.</summary>
    public static string? Report(SkyPatcherValue v, string? scope, SkyPatcherOverlay.IFormResolver resolver)
    {
        if (v.IsNameLiteral) return null;
        if (v.Address is { } a)
        {
            if (a.IsMalformed)
                return "is not a FormID SkyPatcher can read (no hex digit after '|', or more than 32 bits), and SkyPatcher never reads a '|' token as an EditorID, so it names nothing";
            if (!resolver.PluginPresent(a.Plugin!))
                return a.Plugin!.Trim() != a.Plugin && resolver.PluginPresent(a.Plugin!.Trim())
                    ? $"names plugin '{a.Plugin}' with a space SkyPatcher keeps (it does not trim around '|'), so it names nothing"
                    : $"names plugin '{a.Plugin}', which is not in the active order, so it names nothing";
            return FormKeyOf(a) is { } fk && resolver.WinnerPluginOf(fk) is null
                ? $"names {FormIdToken.Of(fk)}, which no plugin in the active order defines, so it names nothing"
                : null;
        }
        if (resolver.EditorIdGap(scope) is { } gap) return $"finds nothing: {gap}";
        return resolver.EditorIdNote(v.Raw, scope)
            ?? (resolver.ResolveEditorId(v.Raw, scope) is null
                ? $"finds no record by that EditorID{(scope is null ? "" : $" among {scope} records")} in the active order"
                : null);
    }
}
