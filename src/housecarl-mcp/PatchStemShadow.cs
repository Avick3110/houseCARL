namespace HousecarlMcp;

/// <summary>The fresh-write SHADOW test: would the plugin FILE this call is about to write land beside a file of the
/// SAME NAME that the active order is not loading? The mod-folder and active-plugin tests behind <c>UniqueStem</c>
/// never saw that file — it sits in someone else's mod folder, disabled or unticked, in the overwrite folder, or in
/// game Data — so houseCARL used to write "<c>&lt;stem&gt;.esp</c>" beside a foreign "<c>&lt;stem&gt;.esp</c>", two
/// plugins that cannot both be active, and said nothing (#561). A shadowing name the CALLER chose is REFUSED, naming
/// where it was found and the file; it is never auto-suffixed around, because renaming the collision away hides the
/// plugin the caller may actually have meant (Aaron, 2026-09-06).
/// <para>The test is on the FILENAME, not the folder stem: a lane whose folder name and plugin name differ is judged
/// by the plugin it really writes, and a lane that writes no plugin at all takes no plugin-shadow refusal.</para>
/// <para>An ACTIVE same-named plugin is deliberately NOT this check's business: the load order already sees it, and
/// <c>UniqueStem</c>'s suffix loop dodges it, which is the behaviour a generic default stem depends on.</para>
/// <para>Cost, paid once per FRESH write and never on an <c>into=</c> extend: one
/// <see cref="Mo2LoadOrder.LocatePlugin"/> sweep for one filename — one stat per candidate mod folder plus one
/// ModsDir listing for the unlisted layer, opening no plugin — over a composition the caller reads once.</para></summary>
internal static class PatchStemShadow
{
    /// <summary>The plugin file ONE fresh-write lane will emit, and the parameter that lane's caller changes to move
    /// it. <paramref name="PluginFor"/> maps the stem being tried to that filename; <paramref name="FollowsStem"/>
    /// says whether the filename actually varies with the stem — true on the record lane, which writes
    /// "<c>&lt;stem&gt;.esp</c>", and false where the caller named the file outright, where stepping the suffix could
    /// never clear a shadow. A lane that writes no plugin has no target and takes no shadow refusal.</summary>
    internal readonly record struct Target(Func<string, string> PluginFor, bool FollowsStem, string Param);

    /// <summary>Where <paramref name="file"/> would be shadowed, or null when nothing on the install holds that name.
    /// Searches the same layers a plugin read reaches — the overwrite folder, every mod folder enabled, disabled or
    /// not yet listed by MO2, and the game Data folder — so a name is judged against the whole install rather than
    /// the active order alone. Ownership is not an axis: what makes two files a collision is the filename, so a
    /// houseCARL folder holding it counts exactly as a stranger's does. The folder this call writes into is brand new
    /// and holds nothing, so it can never be its own shadow.</summary>
    internal static PluginFileHit? Find(Mo2Composition comp, string modsDir, string dataDir, string overwriteDir,
                                        string file, IReadOnlySet<string> activePlugins)
    {
        // An active plugin of this name is the suffix loop's case, not a silent shadow — leave it alone.
        if (activePlugins.Contains(file)) return null;
        return Mo2LoadOrder.LocatePlugin(comp, modsDir, dataDir, overwriteDir, file).FirstOrDefault();
    }

    /// <summary>The refusal: one sentence naming what was found and what to try. Both remedies are safe — pick
    /// another name, or enable the plugin already there — because the caller who reached this may have meant either.
    /// <paramref name="param"/> is the calling lane's OWN parameter for the name, since the file is spelled by
    /// <c>patch=</c> on the record lanes, <c>output=</c> on a merge and <c>plugin_name=</c> on a header-only create.
    /// The remedy says "your install" rather than "a mod folder" because the sweep also reaches the overwrite folder
    /// and game Data, and <see cref="PluginFileHit.Where"/> has already named which of the three it was.
    /// <paramref name="found"/> is the file actually sitting there when it is NOT the one being written — the
    /// header-only lane sweeps all three plugin extensions, because what binds to a trigger is its BASENAME, so
    /// "<c>MyTrigger.esm</c>" is a collision for the "<c>MyTrigger.esp</c>" that call emits. Naming both keeps the
    /// sentence true, and <paramref name="clash"/> lets that lane state WHY the two collide, since the reason is the
    /// lane's own: the default reason is the one every filename-for-filename hit has.</summary>
    internal static string Refusal(string file, PluginFileHit hit, string param, string? found = null,
                                   string clash = "two plugins cannot share one filename")
    {
        var other = found ?? file;
        return $"cannot write '{file}': {hit.Where} already holds '{other}', which the load order is not loading, and "
             + $"{clash} — pass {param}= a name nothing on your install already uses, or enable "
             + $"'{other}' and target that plugin if it is the one you meant.";
    }
}
