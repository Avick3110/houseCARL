using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A live MO2 order with FLM files in Data: HcFlm.esp defines two form lists (HcList, holding HcOld, and
/// HcList2), a list HcHolder holding HcGem, three misc items, a keyword, a seed and a bush, and the planter lists
/// FLM's Plant key adds to; HcFlmOther.esp a misc item of its own.</summary>
sealed class FlmWorld : IDisposable
{
    public const string Flm = "HcFlm.esp", Other = "HcFlmOther.esp";
    public readonly string Root, IniMod, Loser;
    public readonly LoadOrderService Svc;

    public FlmWorld(IEnumerable<(string Name, string Text)> files, IEnumerable<(string Name, string Text)>? shadowed = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-flm-layer-" + Guid.NewGuid().ToString("N"));
        var instance = Path.Combine(Root, "instance");
        var profileDir = Path.Combine(instance, "profiles", "Default");
        var mods = Path.Combine(instance, "mods");
        IniMod = Path.Combine(mods, "HcFlmIni");
        Loser = Path.Combine(mods, "HcFlmLoser");
        var plugins = Path.Combine(mods, "HcFlmPlugins");
        foreach (var d in new[] { profileDir, Path.Combine(Root, "game", "Data"), plugins, IniMod, Loser })
            Directory.CreateDirectory(d);
        SkyPatcherSwitchesFile.WriteTweaks(IniMod);
        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace('\\', '/') + ")\r\n");

        var mod = new SkyrimMod(ModKey.FromFileName(Flm), SkyrimRelease.SkyrimSE);
        FormKey K(uint id) => new(mod.ModKey, id);
        var list = new FormList(K(0x800), SkyrimRelease.SkyrimSE) { EditorID = "HcList" };
        list.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(K(0x804)));
        mod.FormLists.Add(list);
        mod.FormLists.Add(new FormList(K(0x801), SkyrimRelease.SkyrimSE) { EditorID = "HcList2" });
        mod.MiscItems.Add(new MiscItem(K(0x802), SkyrimRelease.SkyrimSE) { EditorID = "HcCoin" });
        mod.MiscItems.Add(new MiscItem(K(0x803), SkyrimRelease.SkyrimSE) { EditorID = "HcGem" });
        mod.MiscItems.Add(new MiscItem(K(0x804), SkyrimRelease.SkyrimSE) { EditorID = "HcOld" });
        mod.Keywords.Add(new Keyword(K(0x805), SkyrimRelease.SkyrimSE) { EditorID = "HcKw" });
        mod.Ingredients.Add(new Ingredient(K(0x806), SkyrimRelease.SkyrimSE) { EditorID = "HcSeed" });
        mod.Florae.Add(new Flora(K(0x807), SkyrimRelease.SkyrimSE) { EditorID = "HcBush" });
        mod.FormLists.Add(new FormList(K(0x808), SkyrimRelease.SkyrimSE) { EditorID = "flPlanterPlantableItem" });
        mod.FormLists.Add(new FormList(K(0x809), SkyrimRelease.SkyrimSE) { EditorID = "flPlanterPlantedFlora" });
        var holder = new FormList(K(0x80A), SkyrimRelease.SkyrimSE) { EditorID = "HcHolder" };
        holder.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(K(0x803)));
        mod.FormLists.Add(holder);
        Write(mod, Path.Combine(plugins, Flm));

        var other = new SkyrimMod(ModKey.FromFileName(Other), SkyrimRelease.SkyrimSE);
        other.MiscItems.Add(new MiscItem(new FormKey(other.ModKey, 0x800), SkyrimRelease.SkyrimSE) { EditorID = "HcFar" });
        Write(other, Path.Combine(plugins, Other));

        foreach (var (name, text) in files)
        {
            var p = Path.Combine(IniMod, name);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, text);
        }
        foreach (var (name, text) in shadowed ?? []) File.WriteAllText(Path.Combine(Loser, name), text);

        var order = new[] { Flm, Other };
        File.WriteAllText(Path.Combine(profileDir, "Skyrim.ini"), "[Archive]\r\nsResourceArchiveList=\r\n");
        File.WriteAllText(Path.Combine(profileDir, "loadorder.txt"), "# header\r\n" + string.Concat(order.Select(o => o + "\r\n")));
        File.WriteAllText(Path.Combine(profileDir, "plugins.txt"), string.Concat(order.Select(o => "*" + o + "\r\n")));
        File.WriteAllText(Path.Combine(profileDir, "modlist.txt"), "# header\r\n+HcFlmIni\r\n+HcFlmLoser\r\n+HcFlmPlugins\r\n");
        Svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(Root, "houseCARL.user.json")));
    }

    static void Write(SkyrimMod m, string path) => m.BeginWrite.ToPath(path).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

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

/// <summary>housecarl_layer reading FLM: the winning files in FLM's order, both folders, the lines expanded under filter=
/// with what FLM does with each, formids= and plugins= selecting the lines naming a form (through a group, alias or list
/// included), and FLM's part after SkyPatcher's, SPID's, KID's and BOS's.</summary>
[Trait("tier", "integration")]
public sealed class FlmLayerTests
{
    static readonly string[] FlmOnly = { "flm" };
    const string Coin = "000802:HcFlm.esp", Gem = "000803:HcFlm.esp", Far = "000800:HcFlmOther.esp";

    // R42, the plan's proof: plugins= keeps a line by a form it names by EditorID only, labelled, with FLM's add row
    // (dev Skyrim Revoiced_FLM.ini, 64 lines naming SkyrimRevoiced Skypatcher.esp's voice types by EditorID).
    [Fact]
    public void PluginsSelectsALineNamingItsFormByEditorId()
    {
        using var w = new FlmWorld(new[] { ("Hc_FLM.ini", "FormList = HcList|HcFar\nFormList = HcList2|HcCoin\n") });
        var text = w.Layer(FlmOnly, plugins: new[] { FlmWorld.Other });
        Assert.Contains("— 1 line(s) in 1 of 1 file(s); only those files are listed", text);
        Assert.Contains(":1  FormList = HcList|HcFar\n          adds 1 form(s) to HcList (000800:HcFlm.esp)\n" +
                        "          ↳ value items: HcFar → " + Far + "\n", text);
        Assert.DoesNotContain(":2  ", text);
    }

    // Q1 (b): a line naming a group or a '*' list touches what they hold, labelled through the name; the list's own
    // forms are its winning record's.
    [Fact]
    public void FormIdsFindsAFormThroughAGroupOrAList()
    {
        using var w = new FlmWorld(new[] { ("Hc_FLM.ini", "Group = Mine|HcGem\nFormList = HcList|#Mine\nFormList = HcList2|*HcHolder\nFormList = HcList2|HcCoin\n") });
        var text = w.Layer(FlmOnly, formids: new[] { Gem });
        Assert.Contains("↳ value group members: HcGem → " + Gem, text);
        Assert.Contains("↳ value items through group: #Mine → " + Gem, text);
        Assert.Contains("↳ value items through list: *HcHolder → " + Gem, text);
        Assert.DoesNotContain(":4  ", text);
    }

    // Q1 (b): an alias's lists are the line's targets, through it.
    [Fact]
    public void AnAliasesListsAreTargetsThroughIt()
    {
        using var w = new FlmWorld(new[] { ("Hc_FLM.ini", "Alias = Both|HcList,HcList2\nFormList = #Both|HcCoin\n") });
        var text = w.Layer(FlmOnly, formids: new[] { "000801:HcFlm.esp" });
        Assert.Contains(":2  FormList = #Both|HcCoin\n          adds 1 form(s) to HcList (000800:HcFlm.esp)\n          adds 1 form(s) to HcList2 (000801:HcFlm.esp)\n", text);
        Assert.Contains("↳ target list through alias: #Both → 000801:HcFlm.esp", text);
    }

    // Q3 (a): a simplified key's fixed list is the target, marked implied.
    [Fact]
    public void APlantLinesListIsAnImpliedTarget()
    {
        using var w = new FlmWorld(new[] { ("Hc_FLM.ini", "Plant = HcSeed|HcBush\n") });
        Assert.Contains("↳ target implied list: flPlanterPlantableItem → 000808:HcFlm.esp", w.Layer(FlmOnly, formids: new[] { "000808:HcFlm.esp" }));
    }

    // (12)(iii): a filter's plugin gates the line and is no touch, so plugins= naming it selects nothing; the line says its verdict.
    [Fact]
    public void AFiltersPluginIsNoTouch()
    {
        using var w = new FlmWorld(new[] { ("Hc_FLM.ini", "FormList = HcList|HcCoin|+HcFlmOther.esp\nFormList = HcList|HcGem|-HcFlmOther.esp\n") });
        Assert.Contains("nothing matched: no FLM line names such a form", w.Layer(FlmOnly, plugins: new[] { FlmWorld.Other }));
        var text = w.Layer(FlmOnly, filter: "Hc_");
        Assert.Contains("[!] its filter '+HcFlmOther.esp' is true: '+hcflmother.esp' holds\n", text);
        Assert.Contains("[!] its filter '-HcFlmOther.esp' is false: '-hcflmother.esp' (hcflmother.esp is in the order) fails, so FLM counts the line filtered out and adds nothing from it\n", text);
    }

    // R40: the winning copy only; a shadowed copy is named and never read.
    [Fact]
    public void AShadowedCopyIsNamedAndNeverRead()
    {
        using var w = new FlmWorld(new[] { ("Hc_FLM.ini", "FormList = HcList|HcCoin\n") }, shadowed: new[] { ("Hc_FLM.ini", "FormList = HcList|HcFar\n") });
        var text = w.Layer(FlmOnly, filter: "Hc_");
        Assert.Contains("[!] shadows same-path copies from HcFlmLoser, never read", text);
        Assert.DoesNotContain("HcFar", text);
    }

    // R40: Data\FLM's files come after the root's, each saying its place there is not FLM's; their names need no '_FLM'.
    [Fact]
    public void DataFlmsFilesFollowTheRootsAndSayTheirOrder()
    {
        using var w = new FlmWorld(new[] { ("Hc_FLM.ini", "FormList = HcList|HcCoin\n"), (@"FLM\a.ini", "FormList = HcList|HcGem\n") });
        var text = w.Layer(FlmOnly);
        Assert.Contains("  - Hc_FLM.ini  (file 1 of 2, 1 line(s)) ← HcFlmIni\n  - FLM\\a.ini  (file 2 of 2, 1 line(s)) ← HcFlmIni  [!] comes from a folder the DLL appends", text);
    }

    // No files: the sentence names both folders FLM reads.
    [Fact]
    public void NoFilesNamesBothFolders()
    {
        using var w = new FlmWorld(Array.Empty<(string, string)>());
        Assert.Contains("no FLM files in the active order: no file directly in Data has '_FLM' in its name and the '.ini' extension, and no '.ini' file is in Data\\FLM.", w.Layer(FlmOnly));
    }

    // An edited file is read anew at the next call: nothing is held between calls (R15).
    [Fact]
    public void AnEditedFileIsReadAnew()
    {
        using var w = new FlmWorld(new[] { ("Hc_FLM.ini", "FormList = HcList|HcCoin\n") });
        Assert.Contains("1 line(s)", w.Layer(FlmOnly));
        File.WriteAllText(Path.Combine(w.IniMod, "Hc_FLM.ini"), "FormList = HcList|HcCoin\nFormList = HcList|HcGem\n");
        Assert.Contains("2 line(s)", w.Layer(FlmOnly));
    }

    // R41: FLM's lines share the call's one EditorID table: no walk is added after SPID's every-type walk.
    [Fact]
    public void FlmAddsNoEditorIdWalk()
    {
        using var w = new FlmWorld(new[] { ("Hc_FLM.ini", "FormList = HcList|HcCoin\n"), ("Hc_DISTR.ini", "Spell = HcCoin\n") });
        w.Layer(new[] { "spid" });
        var before = w.Svc.Counters.EditorIdSweeps;
        w.Layer(new[] { "spid" }, formids: new[] { Coin });
        var spid = w.Svc.Counters.EditorIdSweeps - before;
        before = w.Svc.Counters.EditorIdSweeps;
        w.Layer(new[] { "spid", "flm" }, formids: new[] { Coin });
        Assert.Equal(spid, w.Svc.Counters.EditorIdSweeps - before);
    }

    // Pick (1) Q2 (a), format order: under a filter= that fills the cap FLM takes what BOS leaves and says its cut.
    [Fact]
    public void FlmTakesWhatBosLeavesAndStatesItsCut()
    {
        using var w = new FlmWorld(new[]
        {
            ("HcShared_FLM.ini", string.Concat(Enumerable.Range(1, 400).Select(i => $"FormList = HcList|HcCoin{i}\n"))),
            ("HcShared_SWAP.ini", "[Forms]\nHcCoin|HcGem\n"),
        });
        var four = w.Layer(new[] { "skypatcher", "spid", "kid", "bos" }, filter: "HcShared");
        var five = w.Layer(filter: "HcShared", maxChars: 6000);
        Assert.Contains("      ... [FLM lines cut at max_chars after SkyPatcher's, SPID's, KID's and BOS's parts — distributor=[\"flm\"] reads FLM alone at the whole max_chars]", five);
        Assert.StartsWith(w.Layer(new[] { "skypatcher", "spid", "kid", "bos" }, filter: "HcShared", maxChars: 6000) + "\n\nFLM layer (", five);
        Assert.Contains("\n\nFLM layer (", w.Layer(filter: "HcShared"));
        Assert.DoesNotContain("FLM layer", four);
    }

    // Pick (1) Q3 (a), #1409 not this lane's: a selector naming a plugin the order does not carry answers as for the
    // other formats (ARR Follower Stats_FLM.ini:5 names Katana.esp).
    [Fact]
    public void ASelectorNamingAnAbsentPluginAnswersAsForTheOthers()
    {
        using var w = new FlmWorld(new[] { ("Hc_FLM.ini", "FormList = HcList|0x800~Katana.esp\n") });
        Assert.StartsWith("error: plugins= names nothing the load order carries", w.Layer(FlmOnly, plugins: new[] { "Katana.esp" }));
        Assert.StartsWith("error: plugins= names nothing the load order carries", w.Layer(new[] { "bos" }, plugins: new[] { "Katana.esp" }));
    }

    // Pick (1) Q1 (b): with distributor omitted, FLM's one summary line follows BOS's, naming the call that lists its files.
    [Fact]
    public void TheOmittedOverviewEndsOnFlmsSummaryLine()
    {
        using var w = new FlmWorld(new[] { ("Hc_FLM.ini", "FormList = HcList|HcCoin\n") });
        var all = w.Layer();
        Assert.EndsWith("\n\n" + w.Layer(FlmOnly).Split('\n')[0] + "; distributor=[\"flm\"] lists its files", all);
        Assert.True(all.IndexOf("\n\nBOS layer (", StringComparison.Ordinal) < all.IndexOf("\n\nFLM layer (", StringComparison.Ordinal));
    }
}
