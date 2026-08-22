using HousecarlCore;

namespace HousecarlMcp;

/// <summary>
/// WHAT EVERY FINDINGS FAMILY AGREES IS MALFORMED — validated ONCE, before the merged <c>check</c> surface
/// dispatches to any of them.
///
/// <para><b>Why it is here rather than inside each family.</b> <c>type=</c>, <c>formids=</c>, <c>plugins=</c> and
/// <c>exclude=</c> are parsed inside <see cref="LoadOrderService.CheckErrors"/> and
/// <see cref="LoadOrderService.ValidateScripts"/>, and the merged tool calls each of those only where its family was
/// selected. So <c>findings=["dialogue"]</c> — a family none of those parameters scope — ran with NOTHING ever
/// looking at them: <c>type="NOTATYPE"</c>, a malformed FormID token and an unknown <c>exclude=</c> value all came
/// back as an ordinary dialogue answer, while the tool's own parameter text promises each is refused before the
/// sweep runs (Aaron's review of PR #399, finding 3). A caller who typo'd a narrowing read the answer as one that
/// had been accepted, which is Q3's silent-wrong-answer shape.</para>
///
/// <para><b>The blank <c>plugins=</c> entry is the same seam and the sharpest instance.</b> The merged tool filters
/// empty names out of <c>plugins=</c> before deciding whether the caller's scope resolved to nothing, so
/// <c>findings=["scripts"] plugins=["  "]</c> swept the WHOLE order — ~468 s the caller did not ask for, with
/// <c>plugins=</c> silently discarded and nothing saying so (round-3 finding C1). The errors family and both
/// ancestors refuse that input; this is what makes every lane refuse it at the same point.</para>
///
/// <para><b>THE SPLIT, and it is load-bearing.</b> Only what is malformed as INPUT moves up front. Whether a value
/// MATCHES anything in a particular family's scope is a family-local question and stays one:
/// <c>exclude=</c>'s "a filename you named that nothing in scope matches" is decided against the scope that family
/// would have swept, and each family's scope differs, so raising it here would refuse a call another family can
/// answer — the grounds-are-one design this branch settled. What moves is the SYNTAX half: a value that is neither
/// a plugin filename (extension-bearing) nor a known group token is not a value any family could have used.</para>
/// </summary>
internal static class SweepSharedInput
{
    /// <summary>The blank-plugin-name refusal, in one place so the up-front check and the errors family's own
    /// cannot drift into two spellings of one rule.</summary>
    internal const string BlankPluginName =
        "a blank plugin name in the scope — pass plugin filenames (e.g. 'CoolMod.esp').";

    /// <summary>The whole call's refusal where a SHARED input is malformed, or null where every one of them parses.
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

        // SYNTAX ONLY — see the split above. Resolve is handed an EMPTY implicit set deliberately: the two values
        // it can refuse (a blank entry, and a value that is neither a filename nor a group token) do not depend on
        // what `implicit` expands to, and expanding it here would read the MO2 composition for a call that may be
        // about to refuse for an unrelated reason. The resolved set is discarded; only the ground is taken.
        return SweepExclusion.Resolve(exclude, Array.Empty<string>()).Error;
    }
}
