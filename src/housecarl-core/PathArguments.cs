namespace HousecarlCore;

/// <summary>The one absolute-path rule for every path a CALLER names; contract in docs/architecture/output-and-artifacts.md.</summary>
public static class PathArguments
{
    /// <summary>The refusal for a path that is not FULLY QUALIFIED, or null when it is fine, worded from the calling lane's own label, target and example.</summary>
    public static string? NotAbsolute(string given, string label, string what, string example)
        => Path.IsPathFullyQualified(given)
            ? null
            : $"{label} '{given}' is not an absolute path — pass the full path to {what} (e.g. '{example}'), " +
              "because the server resolves anything else against its OWN working directory, not yours.";
}
