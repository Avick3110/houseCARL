namespace HousecarlGenerator;

/// <summary>
/// Whole-identifier matching for tool and skill names, shared by every guard that asks "does this text name
/// THIS tool".
///
/// It exists because the 2.0 surface renamed tools onto PREFIXES of the 1.x names they absorbed —
/// <c>housecarl_create</c> ⊂ <c>housecarl_create_record</c>, and the same for remove/forward — so a bare
/// <c>Contains</c> answers "yes" for a text that names only the retired tool. That is not a hypothetical:
/// the #468 review rounds measured it holding a guard GREEN while the behaviour under it was gutted. The
/// binding shim's retired-name response ECHOES the name it is refusing (<c>housecarl_create_record is not on
/// this surface — …</c>), so a <c>Contains("housecarl_create")</c> test of that response is satisfied by the
/// echo alone, whatever the successor teaching says — and three of the six retired write tools collide that way.
///
/// The matcher was written in <see cref="CodexUmbrellaCoverageProbe"/> for the same collision and lives here so
/// every consumer shares one implementation with one set of teeth: that probe's GUARD-SELF arm derives the
/// colliding pairs from the real name set and asserts the matcher tells each pair apart, so it now vouches for
/// this helper on behalf of all of them rather than for one file's private copy.
/// </summary>
internal static class ToolNameMatch
{
    /// <summary>Does <paramref name="text"/> mention <paramref name="name"/> as a WHOLE identifier — not as part
    /// of a longer name? Both sides are checked (PR #311 round-2 review [low]: checking only the trailing side let
    /// a suffix collision through — a future skill slug <c>record-jobs</c> would have been reported as routed by a
    /// router that mentions only <c>bulk-record-jobs</c>).</summary>
    internal static bool ReferencedAtBoundary(string text, string name)
    {
        for (int i = text.IndexOf(name, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(name, i + 1, StringComparison.Ordinal))
        {
            if (i > 0 && IsNamePart(text[i - 1])) continue;
            int after = i + name.Length;
            if (after >= text.Length || !IsNamePart(text[after])) return true;
        }
        return false;
    }

    /// <summary>The identifier alphabet these names are written in: tool names are snake_case, skill slugs are
    /// kebab-case, so a letter, digit, <c>_</c> or <c>-</c> continues a name rather than ending it. Without the
    /// hyphen the boundary in <c>bulk-record-jobs</c> would read as a word break and re-open that hole.</summary>
    internal static bool IsNamePart(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '-';
}
