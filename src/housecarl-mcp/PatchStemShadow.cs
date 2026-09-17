namespace HousecarlMcp;

/// <summary>The fresh-write SHADOW test: would the plugin FILE this call is about to write land beside a file of
/// the SAME NAME the active order is not loading? A shadowing name the CALLER chose is REFUSED, naming where it
/// was found, and never auto-suffixed around, which would hide the plugin the caller may have meant. The test is
/// on the FILENAME, not the folder stem; an ACTIVE same-named plugin is <c>UniqueStem</c>'s loop, not this.</summary>
internal static class PatchStemShadow
{
    /// <summary>The plugin file ONE fresh-write lane will emit, and the parameter its caller changes to move it. A lane that writes no plugin takes no shadow refusal.</summary>
    internal readonly record struct Target(Func<string, string> PluginFor, string Param);

    /// <summary>Where <paramref name="file"/> would be shadowed, or null. Searches every layer a plugin read reaches,
    /// and ownership is not an axis, because what makes two files a collision is the filename.</summary>
    internal static PluginFileHit? Find(Mo2Composition comp, string modsDir, string dataDir, string overwriteDir,
                                        string file, IReadOnlySet<string> activePlugins)
    {
        // An active plugin of this name is the suffix loop's case, not a silent shadow — leave it alone.
        if (activePlugins.Contains(file)) return null;
        return Mo2LoadOrder.LocatePlugin(comp, modsDir, dataDir, overwriteDir, file).FirstOrDefault();
    }

    /// <summary>The refusal: one sentence naming what was found and what to try, with both remedies, because the
    /// caller may have meant either. <paramref name="clash"/> lets a lane state why its own two files collide.</summary>
    internal static string Refusal(string file, PluginFileHit hit, string param, string? found = null,
                                   string clash = "two plugins cannot share one filename")
    {
        var other = found ?? file;
        return $"cannot write '{file}': {hit.Where} already holds '{other}', which the load order is not loading, and "
             + $"{clash} — pass {param}= a name nothing on your install already uses, or enable "
             + $"'{other}' and target that plugin if it is the one you meant.";
    }
}
