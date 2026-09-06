namespace HousecarlMcp;

/// <summary>The fresh-patch stem's SHADOW test: would a patch minted under this stem land beside a plugin of the same
/// name that the active order is not loading? The mod-folder and active-plugin tests behind <c>UniqueStem</c> never
/// saw that plugin — it sits in someone else's mod folder, disabled or unticked — so houseCARL used to mint
/// "<c>&lt;stem&gt;.esp</c>" beside a foreign "<c>&lt;stem&gt;.esp</c>", two plugins that cannot both be active, and
/// said nothing (#561). A shadowing stem is REFUSED, naming the folder and the file; it is never auto-suffixed around,
/// because renaming the collision away hides the plugin the caller may actually have meant (Aaron, 2026-09-06).
/// <para>An ACTIVE same-named plugin is deliberately NOT this check's business: the load order already sees it, and
/// <c>UniqueStem</c>'s suffix loop dodges it, which is the behaviour a generic default stem depends on.</para>
/// <para>Cost, paid once per FRESH write and never on an <c>into=</c> extend: one
/// <see cref="Mo2LoadOrder.LocatePlugin"/> sweep per plugin extension — one filename stat per candidate mod folder
/// plus one ModsDir listing for the unlisted layer, opening no plugin — over a composition the caller reads once.
/// The <c>meta.ini</c> ownership read runs only on a folder that actually holds the name, so it costs nothing on the
/// ordinary call.</para></summary>
internal static class PatchStemShadow
{
    /// <summary>What the stem would shadow: the layer label <see cref="PluginFileHit.Where"/> already renders (it
    /// names the mod folder and its state), and the plugin filename found there — null for a folder collision, where
    /// there is no file to name.</summary>
    internal readonly record struct Hit(string Where, string? File);

    /// <summary>The first thing <paramref name="stem"/> would shadow, or null when the stem is clear. Searches the
    /// same layers a plugin read reaches — the overwrite folder, every mod folder enabled, disabled or not yet listed
    /// by MO2, and the game Data folder — so a stem is judged against the whole install rather than the active order
    /// alone. houseCARL's own patches are skipped: a same-named plugin in a folder we own is the <c>into=</c> extend
    /// lane's, not a foreign collision.</summary>
    internal static Hit? Find(Mo2Composition comp, string modsDir, string dataDir, string overwriteDir,
                              string stem, string canonicalFolder,
                              IReadOnlySet<string> activePlugins, Func<string, bool> isOwned)
    {
        foreach (var ext in PluginFile.Extensions)
        {
            var file = stem + ext;
            // An active plugin of this name is the suffix loop's case, not a silent shadow — leave it alone.
            if (activePlugins.Contains(file)) continue;
            foreach (var hit in Mo2LoadOrder.LocatePlugin(comp, modsDir, dataDir, overwriteDir, file))
            {
                var dir = Path.GetDirectoryName(hit.Path);
                if (dir is not null && isOwned(dir)) continue;
                return new Hit(hit.Where, file);
            }
        }
        // A folder literally named "houseCARL - <stem>" that carries no marker is a user's, and a fresh patch under
        // this stem would claim its name. It holds no plugin of the name (the sweep above would have found one), so
        // the refusal names the folder alone.
        var canonical = Path.Combine(modsDir, canonicalFolder);
        if (Directory.Exists(canonical) && !isOwned(canonical)) return new Hit($"mod '{canonicalFolder}'", null);
        return null;
    }

    /// <summary>The refusal: one sentence naming what was found and what to try. Both remedies are safe — pick
    /// another name, or enable the plugin already there — because the caller who reached this may have meant either.
    /// <paramref name="patchParam"/> is the calling lane's OWN parameter spelling, since the rider lanes name their
    /// fresh folder with something other than <c>patch=</c>.</summary>
    internal static string Refusal(string stem, Hit hit, string patchParam)
        => hit.File is null
            ? $"cannot create a fresh patch named '{stem}': {hit.Where} already exists and was not created by "
              + $"houseCARL, so a fresh patch would claim its name — pass {patchParam}= a name no mod folder already uses."
            : $"cannot create a fresh patch named '{stem}': {hit.Where} already holds '{hit.File}', which the load "
              + "order is not loading, and two plugins cannot share one filename — pass "
              + $"{patchParam}= a name no mod folder already uses, or enable '{hit.File}' and target that plugin if "
              + "it is the one you meant.";
}
