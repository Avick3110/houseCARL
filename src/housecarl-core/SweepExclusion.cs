namespace HousecarlCore;

/// <summary>The sweep's exclusion axis (<c>exclude=</c>): plugins the caller does not want swept at all. Contracts in
/// docs/architecture/check-families.md.</summary>
public static class SweepExclusion
{
    /// <summary>Mutagen's base masters — the five the game ships with.</summary>
    public const string BaseMastersToken = "base_masters";

    /// <summary>Every plugin the order loads that <c>plugins.txt</c> does not list — the force-loaded set, a superset
    /// of <see cref="BaseMastersToken"/>.</summary>
    public const string ImplicitToken = "implicit";

    /// <summary>The accepted tokens, in the order a refusal lists them — the ONE place they are enumerated.</summary>
    public static readonly string[] Tokens = { BaseMastersToken, ImplicitToken };

    static readonly string[] PluginExtensions = { ".esp", ".esm", ".esl" };

    /// <summary>Is this value a plugin NAME (it carries a plugin extension) rather than a token?</summary>
    public static bool IsPluginName(string value) =>
        PluginExtensions.Any(e => value.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>A resolved <c>exclude=</c>, keeping names and groups apart so each is validated on its own rule.</summary>
    /// <param name="Names">every filename to leave out, groups expanded.</param>
    /// <param name="TypedNames">only the filenames the CALLER wrote — the ones a scope must actually contain.</param>
    public sealed record Resolved(IReadOnlyCollection<string> Names, IReadOnlyList<string> TypedNames);

    /// <summary>Resolve <paramref name="exclude"/> into the plugin filenames to leave out of the sweep; every
    /// malformed value refuses before the sweep runs. Returns null only when the caller passed no exclusion at
    /// all.</summary>
    public static (Resolved? Set, string? Error) Resolve(
        IReadOnlyList<string>? exclude, IReadOnlyList<string> implicitNames)
    {
        if (exclude is null || exclude.Count == 0) return (null, null);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var typed = new List<string>();

        foreach (var raw in exclude)
        {
            var v = raw?.Trim() ?? "";
            if (v.Length == 0)
                return (null, $"a blank value in exclude= — pass a plugin filename (e.g. 'CoolMod.esp') or one of: {string.Join(", ", Tokens)}.");
            if (IsPluginName(v)) { names.Add(v); typed.Add(v); continue; }

            if (v.Equals(BaseMastersToken, StringComparison.OrdinalIgnoreCase))
                foreach (var m in ErrorCheck.BaseMasters) names.Add(m);
            else if (v.Equals(ImplicitToken, StringComparison.OrdinalIgnoreCase))
                foreach (var m in implicitNames) names.Add(m);
            else
                return (null,
                    $"exclude= value '{v}' is neither a plugin filename nor a known group. A plugin filename carries " +
                    $"its extension ('CoolMod.esp'); the groups are: {string.Join(", ", Tokens)}. " +
                    "Nothing was swept — fix the value and re-run.");
        }
        return (new Resolved(names, typed), null);
    }
}
