namespace HousecarlMcp;

/// <summary>The one absolute-path rule for every path a CALLER names — an out_path folder, a to_file artifact, an
/// '@file' list. The server's working directory is not the caller's, so a path that is not absolute resolves
/// somewhere neither of them meant while the response names the path that was typed.</summary>
internal static class PathArguments
{
    /// <summary>The refusal for a path that is not absolute, or null when it is fine. FULLY-QUALIFIED, not merely
    /// rooted: 'C:work' and '\work' are rooted and still resolve against the server's own directory.
    /// <paramref name="label"/> opens the sentence (the parameter, as that lane spells it), <paramref name="what"/>
    /// names what to pass, and <paramref name="example"/> shows one for that lane. Carries no "error:" prefix, so a
    /// caller that throws and a caller that returns a string can both use it.</summary>
    internal static string? NotAbsolute(string given, string label, string what, string example)
        => Path.IsPathFullyQualified(given)
            ? null
            : $"{label} '{given}' is not an absolute path — pass the full path to {what} (e.g. '{example}'), " +
              "because the server resolves anything else against its OWN working directory, not yours.";
}
