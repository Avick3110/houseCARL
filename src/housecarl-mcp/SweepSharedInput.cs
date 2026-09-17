using HousecarlCore;

namespace HousecarlMcp;

/// <summary>What every findings family agrees is malformed as INPUT, checked once before the merged <c>check</c>
/// surface dispatches. Whether a value MATCHES anything is family-local and stays there.</summary>
internal static class SweepSharedInput
{
    internal const string BlankPluginName =
        "a blank plugin name in the scope — pass plugin filenames (e.g. 'CoolMod.esp').";

    /// <summary>The call's refusal where a shared input is malformed, in the order a caller reads the parameters.</summary>
    internal static string? Error(LoadOrderService svc, IReadOnlyList<string>? plugins, IReadOnlyList<string>? types,
                                  IReadOnlyList<string>? formids, string? editoridContains,
                                  IReadOnlyList<string>? exclude)
    {
        if (plugins is not null)
            foreach (var name in plugins)
                if ((name ?? "").Trim().Length == 0)
                    return BlankPluginName;

        if (svc.SweepScopeError(formids, editoridContains, types) is { } scopeErr) return scopeErr;

        // Syntax only, against an empty implicit set: neither value it can refuse depends on what `implicit` expands to.
        return SweepExclusion.Resolve(exclude, Array.Empty<string>()).Error;
    }
}
