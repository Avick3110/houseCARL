using HousecarlCore;

namespace HousecarlMcp;

/// <summary>
/// What every findings family agrees is malformed, validated once before the merged <c>check</c> surface dispatches
/// to any of them.
///
/// <para>The families parse <c>type=</c>, <c>formids=</c>, <c>plugins=</c> and <c>exclude=</c> themselves, but the
/// merged tool calls a family only where it was selected — so a selection none of those parameters scope would run
/// with nothing ever looking at them, and a typo'd narrowing would come back as an ordinary answer.</para>
///
/// <para>Only what is malformed as INPUT belongs here. Whether a value MATCHES anything is family-local and stays
/// there: <c>exclude=</c>'s "you named a file nothing in scope matches" is decided against the scope that family
/// would have swept, and the scopes differ, so refusing it here would refuse a call another family can answer. What
/// moves up front is the syntax half — a value that is neither a plugin filename (extension-bearing) nor a known
/// group token is one no family could have used.</para>
/// </summary>
internal static class SweepSharedInput
{
    /// <summary>The blank-plugin-name refusal, in one place so the up-front check and the errors family's own
    /// cannot drift into two spellings of one rule.</summary>
    internal const string BlankPluginName =
        "a blank plugin name in the scope — pass plugin filenames (e.g. 'CoolMod.esp').";

    /// <summary>The whole call's refusal where a shared input is malformed, or null where every one of them parses.
    /// Checked in the order a caller reads the parameters, so a call with two bad values names the first.</summary>
    /// <param name="svc">the service, for the record-scope parse — <c>type=</c> resolves through the same
    /// TypeLookup <c>cross_plugin_query</c> uses, which lives there.</param>
    internal static string? Error(LoadOrderService svc, IReadOnlyList<string>? plugins, string? type,
                                  IReadOnlyList<string>? formids, string? editoridContains,
                                  IReadOnlyList<string>? exclude)
    {
        if (plugins is not null)
            foreach (var name in plugins)
                if ((name ?? "").Trim().Length == 0)
                    return BlankPluginName;

        if (svc.SweepScopeError(formids, editoridContains, type) is { } scopeErr) return scopeErr;

        // Syntax only — see the split above. Resolve is handed an empty implicit set deliberately: neither value it
        // can refuse depends on what `implicit` expands to, and expanding it would read the MO2 composition for a
        // call that may be about to refuse anyway. The resolved set is discarded; only the ground is taken.
        return SweepExclusion.Resolve(exclude, Array.Empty<string>()).Error;
    }
}
