using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlGenerator;

/// <summary>
/// Shared fixture for the localized-strings arms of the merge and compact service guards (#362). Builds a synthetic MO2
/// instance in the shape the bug needs and nothing else:
///
///   • a real (empty) <c>Skyrim.esm</c> in the game-Data folder — WITHOUT it <c>LoadOrderResolver.ComputeDataDir</c>
///     returns null, every <c>OpenOverlay</c> silently degrades to the folder-adjacent default, and an arm built on a
///     Skyrim.esm-less order would pass whether or not the fix is present. It is the load-bearing part of the fixture.
///   • one localized plugin per spec, each in its OWN MO2 mod folder, carrying a weapon with a localized
///     <c>Name</c>/<c>Description</c>. Mutagen writes the <c>.STRINGS</c>/<c>.DLSTRINGS</c>/<c>.ILSTRINGS</c> beside the
///     plugin; this fixture then MOVES them into the game-Data <c>Strings\</c> folder, which is what makes each plugin a
///     localized plugin whose own folder carries no strings source — the "Cleaned Base Game Masters" / translation-mod
///     pattern, and exactly the read the bare overlay gets wrong.
///
/// A spec with <see cref="Spec.StringsNowhere"/> has its strings DELETED instead of relocated: the residual case, where
/// the strings exist in neither the plugin's own folder nor game-Data, so no <c>dataDir</c> can resolve them.
/// </summary>
internal static class LocalizedStringsFixture
{
    /// <param name="ModFolder">The MO2 mod folder to create (also the plugin's mod-folder name in modlist.txt).</param>
    /// <param name="Key">The plugin's ModKey.</param>
    /// <param name="Name">The localized FULL the weapon carries.</param>
    /// <param name="Desc">The localized DESC the weapon carries.</param>
    /// <param name="StringsNowhere">Delete the strings rather than relocating them to game-Data (the residual case).</param>
    internal sealed record Spec(string ModFolder, ModKey Key, string Name, string Desc, bool StringsNowhere = false);

    /// <param name="Instance">The MO2 instance dir to hand <c>LoadOrderService.WithInstance</c>.</param>
    /// <param name="Mods">The instance's mods dir.</param>
    /// <param name="Data">The game-Data dir — what the resolver's DataDir must resolve to.</param>
    internal sealed record Built(string Instance, string Mods, string Data);

    /// <summary>The EditorID of the localized weapon in the plugin built from <paramref name="spec"/> — the handle every
    /// arm reads back by, since a merge/compact renumbers the FormKey but never the EditorID.</summary>
    internal static string WeaponEdid(Spec spec) => spec.Key.Name + "Weap";

    /// <summary>Build the instance under <paramref name="root"/>. The specs are listed in load order, after Skyrim.esm.</summary>
    internal static Built Build(string root, IReadOnlyList<Spec> specs)
    {
        string instance = Path.Combine(root, "instance");
        string profiles = Path.Combine(instance, "profiles", "Default");
        string mods = Path.Combine(instance, "mods");
        string data = Path.Combine(root, "game", "Data");
        Directory.CreateDirectory(profiles); Directory.CreateDirectory(mods); Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(root, "game").Replace(@"\", @"\\") + ")\r\n");

        // Skyrim.esm in the game-Data folder — the anchor ComputeDataDir derives DataDir from. Real (an empty SkyrimMod),
        // not a stub text file: the index would class an unopenable plugin as excluded, which is a different fixture.
        var skyrimKey = new ModKey("Skyrim", ModType.Master);
        new SkyrimMod(skyrimKey, SkyrimRelease.SkyrimSE)
            .BeginWrite.ToPath(Path.Combine(data, skyrimKey.FileName.String))
            .WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        foreach (var spec in specs)
        {
            var modDir = Path.Combine(mods, spec.ModFolder);
            Directory.CreateDirectory(modDir);

            var m = new SkyrimMod(spec.Key, SkyrimRelease.SkyrimSE) { UsingLocalization = true };
            m.Weapons.Add(new Weapon(new FormKey(spec.Key, 0xA01), SkyrimRelease.SkyrimSE)
            {
                EditorID = WeaponEdid(spec),
                Name = spec.Name,
                Description = spec.Desc,
                BasicStats = new WeaponBasicStats { Damage = 7 },
            });
            m.ModHeader.Stats.NextFormID = 0xA02;
            m.BeginWrite.ToPath(Path.Combine(modDir, spec.Key.FileName.String))
                .WithLoadOrder(Array.Empty<ISkyrimModGetter>()).NoNextFormIDProcessing().Write();

            // The plugin now has its strings beside it, which is the state the bare overlay reads CORRECTLY. Move them
            // out (or drop them) so the plugin's own folder carries no strings source — the state under test.
            var own = Path.Combine(modDir, "Strings");
            if (!Directory.Exists(own))
                throw new InvalidOperationException(
                    $"fixture: '{spec.Key.FileName}' was written with UsingLocalization but produced no Strings folder — " +
                    "the fixture would then be a NON-localized plugin and every arm below would pass vacuously.");
            if (spec.StringsNowhere) Directory.Delete(own, true);
            else
            {
                var target = Path.Combine(data, "Strings");
                Directory.CreateDirectory(target);
                foreach (var f in Directory.EnumerateFiles(own)) File.Move(f, Path.Combine(target, Path.GetFileName(f)));
                Directory.Delete(own, true);
            }
        }

        var order = new[] { skyrimKey.FileName.String }.Concat(specs.Select(s => s.Key.FileName.String)).ToArray();
        File.WriteAllText(Path.Combine(profiles, "loadorder.txt"), "# header\r\n" + string.Join("\r\n", order) + "\r\n");
        File.WriteAllText(Path.Combine(profiles, "plugins.txt"), string.Join("\r\n", order.Select(o => "*" + o)) + "\r\n");
        File.WriteAllText(Path.Combine(profiles, "modlist.txt"),
            "# header\r\n" + string.Join("\r\n", specs.Reverse().Select(s => "+" + s.ModFolder)) + "\r\n");

        return new Built(instance, mods, data);
    }

    /// <summary>Read a written plugin's weapon FULL/DESC back with the BARE overlay — deliberately bare, and deliberately
    /// from the OUTPUT's own folder. Merge and compact both build a fresh, NON-localized <c>SkyrimMod</c>, so a correct
    /// output carries its strings INLINE and this read must see them with no dataDir and no strings folder anywhere near
    /// it. A read that comes back empty here is the blanking #362 describes, whichever end produced it.</summary>
    internal static (string? Name, string? Desc) ReadBackBare(string pluginPath, string weaponEdid)
    {
        using var ov = SkyrimMod.CreateFromBinaryOverlay(pluginPath, SkyrimRelease.SkyrimSE);
        var w = ov.Weapons.FirstOrDefault(x => x.EditorID == weaponEdid);
        return (w?.Name?.String, w?.Description?.String);
    }
}
