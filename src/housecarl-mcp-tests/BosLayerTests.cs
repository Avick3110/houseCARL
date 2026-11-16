using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A live MO2 order with BOS files in Data: HcBos.esp defines two Statics (HcRock, HcRockMossy), a Static and a
/// MoveableStatic both named HcLantern (as dev's Dovahnique's Diverse Dark Elf Lanterns - BOS.esp holds 48 pairs), a
/// location and a keyword; HcBosOther.esp a Static of its own.</summary>
sealed class BosWorld : IDisposable
{
    public const string Bos = "HcBos.esp", Other = "HcBosOther.esp";
    public readonly string Root, IniMod, Loser;
    public readonly LoadOrderService Svc;

    /// <param name="moveableFirst">Write HcBos.esp with its MoveableStatic group before its Static group.</param>
    public BosWorld(IEnumerable<(string Name, string Text)> files, IEnumerable<(string Name, string Text)>? shadowed = null, bool moveableFirst = false)
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-bos-layer-" + Guid.NewGuid().ToString("N"));
        var instance = Path.Combine(Root, "instance");
        var profileDir = Path.Combine(instance, "profiles", "Default");
        var mods = Path.Combine(instance, "mods");
        IniMod = Path.Combine(mods, "HcBosIni");
        Loser = Path.Combine(mods, "HcBosLoser");
        var plugins = Path.Combine(mods, "HcBosPlugins");
        foreach (var d in new[] { profileDir, Path.Combine(Root, "game", "Data"), plugins, IniMod, Loser })
            Directory.CreateDirectory(d);
        SkyPatcherSwitchesFile.WriteTweaks(IniMod);
        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace('\\', '/') + ")\r\n");

        var mod = new SkyrimMod(ModKey.FromFileName(Bos), SkyrimRelease.SkyrimSE);
        mod.Statics.Add(new Static(new FormKey(mod.ModKey, 0x800), SkyrimRelease.SkyrimSE) { EditorID = "HcRock" });
        mod.Statics.Add(new Static(new FormKey(mod.ModKey, 0x801), SkyrimRelease.SkyrimSE) { EditorID = "HcRockMossy" });
        mod.Statics.Add(new Static(new FormKey(mod.ModKey, 0x802), SkyrimRelease.SkyrimSE) { EditorID = "HcLantern" });
        mod.MoveableStatics.Add(new MoveableStatic(new FormKey(mod.ModKey, 0x803), SkyrimRelease.SkyrimSE) { EditorID = "HcLantern" });
        mod.Locations.Add(new Location(new FormKey(mod.ModKey, 0x804), SkyrimRelease.SkyrimSE) { EditorID = "HcTownLocation" });
        mod.Keywords.Add(new Keyword(new FormKey(mod.ModKey, 0x805), SkyrimRelease.SkyrimSE) { EditorID = "HcTownKw" });
        var path = Path.Combine(plugins, Bos);
        Write(mod, path);
        if (moveableFirst) MoveGroupFirst(path, "MSTT", "STAT");

        var other = new SkyrimMod(ModKey.FromFileName(Other), SkyrimRelease.SkyrimSE);
        other.Statics.Add(new Static(new FormKey(other.ModKey, 0x800), SkyrimRelease.SkyrimSE) { EditorID = "HcFarRock" });
        Write(other, Path.Combine(plugins, Other));

        foreach (var (name, text) in files)
        {
            var p = Path.Combine(IniMod, name);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, text);
        }
        foreach (var (name, text) in shadowed ?? []) File.WriteAllText(Path.Combine(Loser, name), text);

        var order = new[] { Bos, Other };
        File.WriteAllText(Path.Combine(profileDir, "Skyrim.ini"), "[Archive]\r\nsResourceArchiveList=\r\n");
        File.WriteAllText(Path.Combine(profileDir, "loadorder.txt"), "# header\r\n" + string.Concat(order.Select(o => o + "\r\n")));
        File.WriteAllText(Path.Combine(profileDir, "plugins.txt"), string.Concat(order.Select(o => "*" + o + "\r\n")));
        File.WriteAllText(Path.Combine(profileDir, "modlist.txt"), "# header\r\n+HcBosIni\r\n+HcBosLoser\r\n+HcBosPlugins\r\n");
        Svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(Root, "houseCARL.user.json")));
    }

    static void Write(SkyrimMod m, string path) => m.BeginWrite.ToPath(path).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

    /// <summary>Rewrite a plugin so its top-level group <paramref name="first"/> sits just before <paramref name="before"/>.</summary>
    static void MoveGroupFirst(string path, string first, string before)
    {
        var bytes = File.ReadAllBytes(path);
        int pos = 24 + BitConverter.ToInt32(bytes, 4);
        var groups = new List<(string Label, byte[] Bytes)>();
        var head = bytes[..pos];
        while (pos < bytes.Length)
        {
            int size = BitConverter.ToInt32(bytes, pos + 4);
            groups.Add((System.Text.Encoding.ASCII.GetString(bytes, pos + 8, 4), bytes[pos..(pos + size)]));
            pos += size;
        }
        var moved = groups.Single(g => g.Label == first);
        groups.Remove(moved);
        groups.Insert(groups.FindIndex(g => g.Label == before), moved);
        File.WriteAllBytes(path, head.Concat(groups.SelectMany(g => g.Bytes)).ToArray());
    }

    public string Layer(string[]? distributor = null, string[]? formids = null, string[]? plugins = null, string? filter = null, int maxChars = 0)
        => SkyPatcherTools.Layer(Svc, distributor: distributor, filter: filter, formids: formids, max_chars: maxChars,
               plugins: plugins is null ? null : new SkyPatcherTools.LayerScope { names = plugins })
           .Replace(Root, "<root>", StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        Svc.Dispose();
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}

/// <summary>housecarl_layer reading BOS: the winning files in BOS's order, the lines expanded under filter= with what BOS
/// does with each, formids= and plugins= selecting the lines naming a form, and BOS's part after SkyPatcher's, SPID's and KID's.</summary>
[Trait("tier", "integration")]
public sealed class BosLayerTests
{
    static readonly string[] BosOnly = { "bos" };
    const string Lantern = "000802:HcBos.esp", MoveableLantern = "000803:HcBos.esp";

    // research.md Q1 (a), DECISIONS 2026-11-15 (13): a name one plugin holds as a Static and a MoveableStatic resolves to
    // the one first in the plugin's file, the record the game's map holds (dev 0DVDiverseDarkElfLanterns_SWAP.ini:4).
    [Fact]
    public void ANameOnePluginHoldsTwiceIsTheFirstInItsFile()
    {
        using var w = new BosWorld(new[] { ("Hc_SWAP.ini", "[Forms]\r\nHcRock|HcLantern\r\n") });
        var text = w.Layer(BosOnly, plugins: new[] { BosWorld.Bos });
        Assert.Contains("↳ value swap: HcLantern → " + Lantern + "\n", text);
        Assert.Contains(":2  HcRock|HcLantern", w.Layer(BosOnly, formids: new[] { Lantern }));
        Assert.Contains("nothing matched: no BOS line", w.Layer(BosOnly, formids: new[] { MoveableLantern }));
    }

    // The same tie read off the file, not the record type: a plugin whose MoveableStatic group comes first in the file
    // keeps that record under the name.
    [Fact]
    public void TheTieFollowsTheFilesOwnGroupOrder()
    {
        using var w = new BosWorld(new[] { ("Hc_SWAP.ini", "[Forms]\r\nHcRock|HcLantern\r\n") }, moveableFirst: true);
        Assert.Contains("↳ value swap: HcLantern → " + MoveableLantern + "\n", w.Layer(BosOnly, plugins: new[] { BosWorld.Bos }));
    }

    // Whatever else the call reads: a SPID line looking the name up first leaves BOS's answer as it is alone.
    [Fact]
    public void TheTieIsTheSameWhateverElseTheCallReads()
    {
        using var w = new BosWorld(new[] { ("Hc_SWAP.ini", "[Forms]\r\nHcRock|HcLantern\r\n"), ("Hc_DISTR.ini", "Spell = HcLantern\r\n") });
        const string Swap = "↳ value swap: HcLantern → " + Lantern;
        Assert.Contains(Swap, w.Layer(BosOnly, formids: new[] { Lantern }));
        var all = w.Layer(formids: new[] { Lantern });
        Assert.Contains("↳ value Spell: HcLantern → " + Lantern, all);
        Assert.Contains(Swap, all[all.IndexOf("BOS layer (", StringComparison.Ordinal)..]);
    }

    // R42: plugins= keeps a line by a form it names by EditorID only, labelled, with BOS's order row.
    [Fact]
    public void PluginsSelectsALineNamingItsFormByEditorId()
    {
        using var w = new BosWorld(new[] { ("Hc_SWAP.ini", "[Forms]\r\nHcRock|HcFarRock\r\nHcRockMossy|HcRock\r\n") });
        var text = w.Layer(BosOnly, plugins: new[] { BosWorld.Other });
        Assert.Contains("— 1 line(s) in 1 of 1 file(s); only those files are listed", text);
        Assert.Contains(":2  HcRock|HcFarRock\n          last wins: line 1 of 1 for base HcRock among its form swaps, tried from the last back\n" +
                        "          ↳ value swap: HcFarRock → 000800:HcBosOther.esp\n", text);
        Assert.DoesNotContain(":3  ", text);
    }

    // The conditions of a section's header select every line under it, as filters.
    [Fact]
    public void FormIdsSelectsALineByItsSectionsCondition()
    {
        using var w = new BosWorld(new[] { ("Hc_SWAP.ini", "[Forms|HcTownLocation]\r\nHcRock|HcRockMossy\r\n[Forms]\r\nHcRockMossy|HcRock\r\n") });
        var text = w.Layer(BosOnly, formids: new[] { "000804:HcBos.esp" });
        Assert.Contains("↳ filter conditions: HcTownLocation → 000804:HcBos.esp", text);
        Assert.DoesNotContain(":5  ", text);
    }

    // A condition naming a Static is said never to match, off the record's class in the order (ARR BlendedShrooms_SWAP.ini:4).
    [Fact]
    public void AConditionNamingAStaticIsSaidNeverToMatch()
    {
        using var w = new BosWorld(new[] { ("Hc_SWAP.ini", "[Forms|HcRock,HcTownLocation]\r\nHcRockMossy|HcRock\r\n") });
        var text = w.Layer(BosOnly, filter: "Hc_");
        Assert.Contains("conditions 'HcRock' names 000800:HcBos.esp, a Static record, and BOS matches a condition only against", text);
        Assert.DoesNotContain("'HcTownLocation' names", text);
    }

    // R40: the winning copy only; a shadowed copy is named and never read.
    [Fact]
    public void AShadowedCopyIsNamedAndNeverRead()
    {
        using var w = new BosWorld(new[] { ("Hc_SWAP.ini", "[Forms]\r\nHcRock|HcRockMossy\r\n") }, shadowed: new[] { ("Hc_SWAP.ini", "[Forms]\r\nHcRock|HcFarRock\r\n") });
        var text = w.Layer(BosOnly, filter: "Hc_");
        Assert.Contains("[!] shadows same-path copies from HcBosLoser, never read", text);
        Assert.DoesNotContain("HcFarRock", text);
    }

    // An edited file is read anew at the next call: nothing is held between calls (R15).
    [Fact]
    public void AnEditedFileIsReadAnew()
    {
        using var w = new BosWorld(new[] { ("Hc_SWAP.ini", "[Forms]\r\nHcRock|HcRockMossy\r\n") });
        Assert.Contains("1 line(s)", w.Layer(BosOnly));
        File.WriteAllText(Path.Combine(w.IniMod, "Hc_SWAP.ini"), "[Forms]\r\nHcRock|HcRockMossy\r\nHcRockMossy|HcRock\r\n");
        Assert.Contains("2 line(s)", w.Layer(BosOnly));
    }

    // R41: BOS's lines share the call's one EditorID table: no walk is added after SPID's and KID's every-type walk.
    [Fact]
    public void BosAddsNoEditorIdWalk()
    {
        using var w = new BosWorld(new[] { ("Hc_SWAP.ini", "[Forms]\r\nHcRock|HcLantern\r\n"), ("Hc_DISTR.ini", "Spell = HcLantern\r\n") });
        w.Layer(new[] { "spid" });
        var before = w.Svc.Counters.EditorIdSweeps;
        w.Layer(new[] { "spid" }, formids: new[] { Lantern });
        var spid = w.Svc.Counters.EditorIdSweeps - before;
        before = w.Svc.Counters.EditorIdSweeps;
        w.Layer(new[] { "spid", "bos" }, formids: new[] { Lantern });
        Assert.Equal(spid, w.Svc.Counters.EditorIdSweeps - before);
    }

    // Pick (1) Q2 (a), format order: under a filter= that fills the cap BOS takes what KID leaves and says its cut.
    [Fact]
    public void BosTakesWhatKidLeavesAndStatesItsCut()
    {
        using var w = new BosWorld(new[]
        {
            ("HcShared_SWAP.ini", "[Forms]\r\n" + string.Concat(Enumerable.Range(1, 400).Select(i => $"HcRock|HcRockMossy{i}\r\n"))),
            ("HcShared_KID.ini", "Keyword = HcTownKw|Armor\r\n"),
        });
        var three = w.Layer(new[] { "skypatcher", "spid", "kid" }, filter: "HcShared");
        var four = w.Layer(filter: "HcShared", maxChars: 6000);
        Assert.Contains("      ... [BOS lines cut at max_chars after SkyPatcher's, SPID's and KID's parts — distributor=[\"bos\"] reads BOS alone at the whole max_chars]", four);
        Assert.StartsWith(w.Layer(new[] { "skypatcher", "spid", "kid" }, filter: "HcShared", maxChars: 6000) + "\n\nBOS layer (", four);
        Assert.Contains("\n\nBOS layer (", w.Layer(filter: "HcShared"));
        Assert.DoesNotContain("BOS layer", three);
    }

    // Pick (1) Q3 (a), #1409 not this lane's: a selector naming a plugin the order does not carry answers as for SPID and KID.
    [Fact]
    public void ASelectorNamingAnAbsentPluginAnswersAsForSpidAndKid()
    {
        using var w = new BosWorld(new[] { ("Hc_SWAP.ini", "[References]\r\n0x14EC94~Midwood Isle.esp|HcRock\r\n") });
        Assert.StartsWith("error: plugins= names nothing the load order carries", w.Layer(BosOnly, plugins: new[] { "Midwood Isle.esp" }));
        Assert.StartsWith("error: plugins= names nothing the load order carries", w.Layer(new[] { "kid" }, plugins: new[] { "Midwood Isle.esp" }));
    }

    // The flm refusal names BOS among the formats read (DECISIONS 2026-11-14 (17)).
    [Fact]
    public void FlmIsStillRefusedNamingBos()
    {
        using var w = new BosWorld(Array.Empty<(string, string)>());
        Assert.Contains("this build reads SkyPatcher, Spell Perk Item Distributor, Keyword Item Distributor and Base Object Swapper lines", w.Layer(new[] { "flm" }));
    }
}
