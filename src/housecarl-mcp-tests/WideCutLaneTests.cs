using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A world wide enough that its text calls are served cut well above their floor, spill block included
/// (#986): forty weapons in a switched-OFF mod, thirty topics, and a folder of sixty loose meshes. The shared
/// worlds' answers are a few records wide, narrower than a spill block, so a cut there is only ever refused; and the
/// band between a floor and the whole answer must be wide enough that a longer temp path or a slower machine cannot
/// close it.</summary>
public sealed class WideCutWorld : IDisposable
{
    public string Root { get; }
    public string OffName { get; }
    public IReadOnlyList<FormKey> Topics { get; }
    public const string MeshDir = @"meshes\hcwidecut";
    public const int Meshes = 60;
    public LoadOrderService Svc { get; }

    public WideCutWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-widecut-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(Root, "game", "Data"));
        var masterKey = new ModKey("HcWideCutMaster", ModType.Master);
        var master = new SkyrimMod(masterKey, SkyrimRelease.SkyrimSE);
        master.Keywords.AddNew().EditorID = "HcWideCutKw";
        var topics = new List<FormKey>();
        for (int t = 1; t <= 30; t++)
        {
            var topic = master.DialogTopics.AddNew();
            topic.EditorID = $"HcWideCutTopicNumber{t:D2}";
            topic.Responses.Add(new DialogResponses(master.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = $"HcWideCutInfoNumber{t:D2}" });
            topics.Add(topic.FormKey);
        }
        Topics = topics;

        var offKey = ModKey.FromNameAndExtension("HcWideCutOff.esp");
        OffName = offKey.FileName.String;
        var off = new SkyrimMod(offKey, SkyrimRelease.SkyrimSE);
        for (int i = 1; i <= 40; i++)
        {
            var w = off.Weapons.AddNew();
            w.EditorID = $"HcWideCutLongSwordNumber{i:D2}";
            w.BasicStats = new WeaponBasicStats { Damage = (ushort)i, Weight = 1 };
        }

        var instance = Path.Combine(Root, "inst");
        var mods = Path.Combine(instance, "mods");
        foreach (var m in new[] { "WideMasterMod", "WideOffMod", "WideMeshMod" }) Directory.CreateDirectory(Path.Combine(mods, m));
        var masterName = masterKey.FileName.String;
        master.BeginWrite.ToPath(Path.Combine(mods, "WideMasterMod", masterName)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        off.BeginWrite.ToPath(Path.Combine(mods, "WideOffMod", OffName)).WithLoadOrder(new ISkyrimModGetter[] { master }).Write();
        var meshDir = Path.Combine(mods, "WideMeshMod", MeshDir);
        Directory.CreateDirectory(meshDir);
        for (int i = 0; i < Meshes; i++) File.WriteAllText(Path.Combine(meshDir, $"a_rather_long_mesh_file_name_{i:D3}.nif"), "x");

        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");
        var prof = Path.Combine(instance, "profiles", "Default");
        Directory.CreateDirectory(prof);
        File.WriteAllText(Path.Combine(prof, "loadorder.txt"), "# header\r\n" + masterName + "\r\n");
        File.WriteAllText(Path.Combine(prof, "plugins.txt"), "*" + masterName + "\r\n");
        // WideOffMod is switched OFF: its plugin is on disk and out of the active order.
        File.WriteAllText(Path.Combine(prof, "modlist.txt"), "# header\r\n+WideMeshMod\r\n-WideOffMod\r\n+WideMasterMod\r\n");
        File.WriteAllText(Path.Combine(prof, "Skyrim.ini"), "[Archive]\r\nsResourceArchiveList=\r\n");

        Svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(Root, "user.json")));
    }

    public void Dispose()
    {
        Svc.Dispose();
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}

/// <summary>What a text call carries when the server serves it cut: the lane's own cut notice and, on a spilling lane,
/// its spill block naming the file. Read at the first cap the call is served cut in <see cref="WideCutWorld"/>.</summary>
[Trait("tier", "integration")]
public sealed class WideCutLaneTests : IClassFixture<WideCutWorld>
{
    readonly WideCutWorld _w;
    public WideCutLaneTests(WideCutWorld w) => _w = w;

    JsonElement OffSource => JsonDocument.Parse("\"" + _w.OffName + "\"").RootElement.Clone();

    static string? Line(string text, string a, string b) =>
        text.Split('\n').FirstOrDefault(l => l.Contains("... [truncated: rendered") && l.Contains(a) && l.Contains(b));

    // Moved from RecordsRemedyGrammarTests, whose world's off-order file is too small to cut above its floor.
    [Fact]
    public void TheOffOrderScanNamesNoneEither_ItPassesNoFieldPathsAtAll()
    {
        var (_, text) = RenderFloorAssert.ServedCut(
            c => RecordsTools.Records(_w.Svc, types: new[] { "WEAP" }, source: OffSource, max_chars: c),
            t => Line(t, " returned matches before hitting", "") is not null);
        var cut = Line(text, " returned matches before hitting", "")!;

        Assert.Contains("lower limit= or raise max_chars", cut);
        Assert.DoesNotContain("drop ", cut);
    }

    // Moved from RecordsRemedyGrammarTests, where the band this call is served cut in was too thin to hold on CI.
    [Fact]
    public void TheOffOrderScansBatchNoticeNamesLimitToo_ItsOwnLaneItsOwnArm()
    {
        var (_, text) = RenderFloorAssert.ServedCut(
            c => RecordsTools.Records(_w.Svc, types: new[] { "WEAP" }, source: OffSource, max_chars: c,
                                      project: new RecordsTools.RecordsProject { form = "everything" }),
            t => Line(t, " records before hitting", "") is not null);
        var cut = Line(text, " records before hitting", "")!;

        Assert.Contains("lower limit=", cut);
        Assert.DoesNotContain("formids", cut);
    }

    // Moved from RecordsRemedyRepairTests, for the same reason.
    [Fact]
    public void TheOffOrderScansEverythingLaneDropsItToo_ThreeLanesOneRule()
    {
        var (_, r) = RenderFloorAssert.ServedCut(
            c => RecordsTools.Records(_w.Svc, types: new[] { "WEAP" }, source: OffSource, max_chars: c,
                                      project: new RecordsTools.RecordsProject { form = "everything" }),
            t => t.Contains(" records before hitting", StringComparison.Ordinal));

        Assert.Contains("max_chars", r);
        Assert.DoesNotContain("project.fields=", r);
    }

    /// <summary>An info_order text reply the server serves cut carries its spill block, naming the file that holds the
    /// complete result.</summary>
    [Fact]
    public void AnInfoOrderTextReplyServedCutCarriesItsSpillBlock()
    {
        var spills = SpillFolders.Emptied(_w.Svc);
        var (cap, text) = RenderFloorAssert.ServedCut(
            c => RecordsTools.Records(_w.Svc, formids: _w.Topics.Select(t => $"{t.ID:X6}:{t.ModKey.FileName}").ToArray(),
                                      project: new RecordsTools.RecordsProject { form = "info_order" }, max_chars: c),
            t => t.Contains("at max_chars=", StringComparison.Ordinal));

        Assert.True(text.Length <= cap);
        Assert.Contains("spilled: complete result", text);
        Assert.Contains(Assert.Single(Directory.GetFiles(spills, "*.jsonl")), text);
    }

    /// <summary>An asset_status text reply the server serves cut carries its spill block, naming the file.</summary>
    [Fact]
    public void AnAssetStatusTextReplyServedCutCarriesItsSpillBlock()
    {
        var spills = SpillFolders.Emptied(_w.Svc);
        var (cap, text) = RenderFloorAssert.ServedCut(
            c => AssetTools.AssetStatus(_w.Svc, under: new[] { WideCutWorld.MeshDir }, max_chars: c),
            t => t.Contains("omitted at max_chars=", StringComparison.Ordinal), step: 25);

        Assert.True(text.Length <= cap);
        Assert.Contains("spilled: complete result", text);
        Assert.Contains(Assert.Single(Directory.GetFiles(spills, "*.jsonl")), text);
    }
}
