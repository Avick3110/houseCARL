namespace HousecarlGenerator;

/// <summary>
/// Whole-identifier matching for tool and skill names, shared by every guard that asks "does this text name
/// THIS tool".
///
/// The 2.0 surface renamed tools onto PREFIXES of the 1.x names they absorbed — <c>housecarl_create</c> is a
/// prefix of <c>housecarl_create_record</c>, and the same for remove/forward — so a bare <c>Contains</c> answers
/// "yes" for a text that names only the retired tool. The binding shim also ECHOES the retired name in its
/// refusal, so a <c>Contains</c> check of that response is satisfied by the echo alone. Match at identifier
/// boundaries instead.
/// </summary>
internal static class ToolNameMatch
{
    /// <summary>Does <paramref name="text"/> mention <paramref name="name"/> as a WHOLE identifier — not as part
    /// of a longer name? Both sides must be checked: names collide by suffix as well as by prefix, so
    /// <c>record-jobs</c> must not match a text that mentions only <c>bulk-record-jobs</c>.</summary>
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
    /// kebab-case, so a letter, digit, <c>_</c> or <c>-</c> continues a name rather than ending it. Dropping the
    /// hyphen would make <c>bulk-record-jobs</c> read as three words and re-open the suffix collision.</summary>
    internal static bool IsNamePart(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '-';
}
