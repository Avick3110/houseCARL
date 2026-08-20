namespace HousecarlCore;

/// <summary>
/// The sweep's EXCLUSION axis (<c>exclude=</c>, #344): plugins the caller does not want swept at all.
///
/// <para><b>Why an axis rather than a classification.</b> #344's complaint is that the dangling-ref budget is spent
/// on the vanilla baseline before mod findings are reached. The budget half of that was fixed at the sweep layer;
/// this is the other half — the caller saying which plugins are not their problem today. It is deliberately NOT a
/// "skip the boring ones" rule baked into the sweep: the baseline the response splits out stays Mutagen's
/// <see cref="ErrorCheck.BaseMasters"/> exactly, by construction, and what counts as noise is the caller's judgement
/// (ruled 2026-08-18). Creation Club and <c>_ResourcePack.esl</c> live here, under <see cref="ImplicitToken"/>, and
/// nowhere in the sweep's own definitions.</para>
///
/// <para><b>Names and tokens share one namespace, without a sigil, and that follows §5.5 rather than excepting it.</b>
/// The rule sigils a pole token only where the co-resident name space can legally contain the bare token. These
/// values are Bethesda plugin filenames, which are extension-mandatory — the exact ground of §5.5's standing S1
/// exemption, stated again in SPEC's §3.1 amendment. So the discriminator is the extension itself: a value carrying
/// one is a NAME, a value without one is a TOKEN, and an unknown token refuses by name rather than being matched
/// loosely against a plugin whose stem happens to look like it.</para>
/// </summary>
public static class SweepExclusion
{
    /// <summary>Mutagen's base masters — the five the game ships with. Excluding them is the "I know vanilla has
    /// permanent dangling refs, do not spend my sweep on them" request that #344 is about.</summary>
    public const string BaseMastersToken = "base_masters";

    /// <summary>Every plugin the order loads that <c>plugins.txt</c> does not list — the force-loaded set. This is
    /// where Creation Club plugins and <c>_ResourcePack.esl</c> are, and it is a SUPERSET of
    /// <see cref="BaseMastersToken"/>: the base masters are force-loaded too. The token is named for what the set
    /// actually is rather than for Creation Club, because "not listed in plugins.txt" is what houseCARL can observe
    /// and "is a Creation Club plugin" is not.</summary>
    public const string ImplicitToken = "implicit";

    /// <summary>The accepted tokens, in the order a refusal lists them. The ONE place they are enumerated — the
    /// parameter's own description is held against this list by a guard arm, so the caller-facing spelling and the
    /// accepted spelling cannot drift (the second-spelling problem #341 is about, on a surface no wire-name guard
    /// reaches — see #386).</summary>
    public static readonly string[] Tokens = { BaseMastersToken, ImplicitToken };

    static readonly string[] PluginExtensions = { ".esp", ".esm", ".esl" };

    /// <summary>Is this value a plugin NAME (it carries a plugin extension) rather than a token? The whole
    /// discriminator, and the reason no sigil is needed.</summary>
    public static bool IsPluginName(string value) =>
        PluginExtensions.Any(e => value.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>Resolve <paramref name="exclude"/> into the set of plugin filenames to leave out of the sweep.
    /// <paramref name="implicitNames"/> comes from the layer that reads the MO2 composition, because that is where
    /// the fact lives; passing it in keeps this parsable without a live order.
    ///
    /// <para>Every malformed value refuses BEFORE the sweep runs and names what was expected (Q3): a typo'd
    /// exclusion that silently excluded nothing would hand back a wall of findings the caller asked not to see,
    /// with no hint that their spelling missed.</para></summary>
    public static (HashSet<string> Names, string? Error) Resolve(
        IReadOnlyList<string>? exclude, IReadOnlyList<string> implicitNames)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (exclude is null || exclude.Count == 0) return (names, null);

        foreach (var raw in exclude)
        {
            var v = raw?.Trim() ?? "";
            if (v.Length == 0)
                return (names, $"a blank value in exclude= — pass a plugin filename (e.g. 'CoolMod.esp') or one of: {string.Join(", ", Tokens)}.");
            if (IsPluginName(v)) { names.Add(v); continue; }

            if (v.Equals(BaseMastersToken, StringComparison.OrdinalIgnoreCase))
                foreach (var m in ErrorCheck.BaseMasters) names.Add(m);
            else if (v.Equals(ImplicitToken, StringComparison.OrdinalIgnoreCase))
                foreach (var m in implicitNames) names.Add(m);
            else
                return (names,
                    $"exclude= value '{v}' is neither a plugin filename nor a known group. A plugin filename carries " +
                    $"its extension ('CoolMod.esp'); the groups are: {string.Join(", ", Tokens)}. " +
                    "Nothing was swept — fix the value and re-run.");
        }
        return (names, null);
    }
}
