namespace HousecarlCore;

/// <summary>
/// The sweep's EXCLUSION axis (<c>exclude=</c>): plugins the caller does not want swept at all.
///
/// <para><b>An axis, not a classification.</b> The sweep itself has no "skip the boring ones" rule: the baseline the
/// response splits out stays Mutagen's <see cref="ErrorCheck.BaseMasters"/> exactly, and what counts as noise is the
/// caller's judgement. Creation Club and <c>_ResourcePack.esl</c> live here, under <see cref="ImplicitToken"/>, and
/// nowhere in the sweep's own definitions.</para>
///
/// <para><b>Names and tokens share one namespace without a sigil.</b> These values are Bethesda plugin filenames,
/// which always carry an extension, so the extension is the discriminator: a value carrying one is a NAME, a value
/// without one is a TOKEN, and an unknown token refuses by name rather than being matched loosely against a plugin
/// whose stem happens to look like it.</para>
/// </summary>
public static class SweepExclusion
{
    /// <summary>Mutagen's base masters — the five the game ships with. Excluding them is the "I know vanilla has
    /// permanent dangling refs, do not spend my sweep on them" request.</summary>
    public const string BaseMastersToken = "base_masters";

    /// <summary>Every plugin the order loads that <c>plugins.txt</c> does not list — the force-loaded set. This is
    /// where Creation Club plugins and <c>_ResourcePack.esl</c> are, and it is a SUPERSET of
    /// <see cref="BaseMastersToken"/>: the base masters are force-loaded too. The token is named for what the set
    /// actually is rather than for Creation Club, because "not listed in plugins.txt" is what houseCARL can observe
    /// and "is a Creation Club plugin" is not.</summary>
    public const string ImplicitToken = "implicit";

    /// <summary>The accepted tokens, in the order a refusal lists them. The ONE place they are enumerated — the
    /// <c>exclude=</c> parameter's own description must name this same set, so the caller-facing spelling and the
    /// accepted spelling cannot drift.</summary>
    public static readonly string[] Tokens = { BaseMastersToken, ImplicitToken };

    static readonly string[] PluginExtensions = { ".esp", ".esm", ".esl" };

    /// <summary>Is this value a plugin NAME (it carries a plugin extension) rather than a token? The whole
    /// discriminator, and the reason no sigil is needed.</summary>
    public static bool IsPluginName(string value) =>
        PluginExtensions.Any(e => value.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>A resolved <c>exclude=</c>, keeping the two kinds of value APART.
    ///
    /// <para>The two must be validated separately. A NAME is a claim that a specific plugin is in scope, so one that
    /// matches nothing is a typo and must refuse. A GROUP is a filter — "whichever of these are here, drop them" — so
    /// a member that is not in scope is the ordinary case, not an error. Validated as one merged list instead, a
    /// narrowed scope refuses every group token, naming a plugin the caller never wrote.</para></summary>
    /// <param name="Names">every filename to leave out, groups expanded.</param>
    /// <param name="TypedNames">only the filenames the CALLER wrote — the ones a scope must actually contain.</param>
    public sealed record Resolved(IReadOnlyCollection<string> Names, IReadOnlyList<string> TypedNames);

    /// <summary>Resolve <paramref name="exclude"/> into the plugin filenames to leave out of the sweep.
    /// <paramref name="implicitNames"/> comes from the layer that reads the MO2 composition, because that is where
    /// the fact lives; passing it in keeps this parsable without a live order.
    ///
    /// <para>Every malformed value refuses BEFORE the sweep runs and names what was expected: a typo'd
    /// exclusion that silently excluded nothing would hand back a wall of findings the caller asked not to see,
    /// with no hint that their spelling missed.</para>
    ///
    /// <para>Returns null only when the caller passed no exclusion at all. A caller who DID pass one gets a
    /// Resolved back even when it expands to nothing, so the sweep can still say the scope was narrowed rather
    /// than proceeding as though exclude= had never been written.</para></summary>
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
