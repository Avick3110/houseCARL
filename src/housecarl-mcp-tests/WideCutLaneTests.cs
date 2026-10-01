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
    /// <summary>One keyword in each of forty active plugins.</summary>
    public IReadOnlyList<FormKey> PartKeywords { get; }
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
        foreach (var m in new[] { "WideMasterMod", "WideOffMod", "WideMeshMod", "WidePartsMod" }) Directory.CreateDirectory(Path.Combine(mods, m));
        var masterName = masterKey.FileName.String;
        master.BeginWrite.ToPath(Path.Combine(mods, "WideMasterMod", masterName)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        // Forty small active plugins, one keyword each, so a count table grouped by plugin has forty rows.
        var parts = new List<FormKey>();
        var partNames = new List<string>();
        for (int i = 1; i <= 40; i++)
        {
            var part = new SkyrimMod(ModKey.FromNameAndExtension($"HcWideCutPart{i:D2}.esp"), SkyrimRelease.SkyrimSE);
            var kw = part.Keywords.AddNew();
            kw.EditorID = $"HcWideCutPartKeyword{i:D2}";
            parts.Add(kw.FormKey);
            partNames.Add(part.ModKey.FileName.String);
            part.BeginWrite.ToPath(Path.Combine(mods, "WidePartsMod", part.ModKey.FileName.String)).WithLoadOrder(new ISkyrimModGetter[] { master }).Write();
        }
        PartKeywords = parts;
        off.BeginWrite.ToPath(Path.Combine(mods, "WideOffMod", OffName)).WithLoadOrder(new ISkyrimModGetter[] { master }).Write();
        var meshDir = Path.Combine(mods, "WideMeshMod", MeshDir);
        Directory.CreateDirectory(meshDir);
        for (int i = 0; i < Meshes; i++) File.WriteAllText(Path.Combine(meshDir, $"a_rather_long_mesh_file_name_{i:D3}.nif"), "x");

        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");
        var prof = Path.Combine(instance, "profiles", "Default");
        Directory.CreateDirectory(prof);
        File.WriteAllText(Path.Combine(prof, "loadorder.txt"), "# header\r\n" + masterName + "\r\n" + string.Concat(partNames.Select(n => n + "\r\n")));
        File.WriteAllText(Path.Combine(prof, "plugins.txt"), "*" + masterName + "\r\n" + string.Concat(partNames.Select(n => "*" + n + "\r\n")));
        // WideOffMod is switched OFF: its plugin is on disk and out of the active order.
        File.WriteAllText(Path.Combine(prof, "modlist.txt"), "# header\r\n+WidePartsMod\r\n+WideMeshMod\r\n-WideOffMod\r\n+WideMasterMod\r\n");
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

    string[] TopicIds => _w.Topics.Select(t => $"{t.ID:X6}:{t.ModKey.FileName}").ToArray();

    /// <summary>The lanes that gained a whole-first pass (#986), each over this world's thirty topics or sixty meshes.</summary>
    public static TheoryData<string> WholeFirstLanes => new()
    {
        "scan summary", "scan fields", "scan group_by", "list summary", "list fields", "identity", "list aggregate", "asset_status",
    };

    Func<int, string> WholeFirstCall(string lane) => lane switch
    {
        "scan summary" => c => RecordsTools.Records(_w.Svc, types: new[] { "DIAL" }, max_chars: c),
        "scan fields" => c => RecordsTools.Records(_w.Svc, types: new[] { "DIAL" }, max_chars: c,
                                                   project: new RecordsTools.RecordsProject { form = "fields", fields = new[] { "EditorID" } }),
        "scan group_by" => c => RecordsTools.Records(_w.Svc, types: new[] { "DIAL" }, max_chars: c,
                                                     project: new RecordsTools.RecordsProject { form = "aggregate", group_by = "defined_in" }),
        "list summary" => c => RecordsTools.Records(_w.Svc, formids: TopicIds, max_chars: c),
        "list fields" => c => RecordsTools.Records(_w.Svc, formids: TopicIds, max_chars: c,
                                                   project: new RecordsTools.RecordsProject { form = "fields", fields = new[] { "EditorID" } }),
        "identity" => c => RecordsTools.Records(_w.Svc, formids: TopicIds, max_chars: c,
                                                project: new RecordsTools.RecordsProject { form = "identity" }),
        "list aggregate" => c => RecordsTools.Records(_w.Svc, formids: TopicIds, max_chars: c,
                                                      project: new RecordsTools.RecordsProject { form = "aggregate", group_by = "type" }),
        _ => c => AssetTools.AssetStatus(_w.Svc, under: new[] { WideCutWorld.MeshDir }, max_chars: c),
    };

    /// <summary>A complete answer is served at a max_chars as wide as it is: no reserve held back for a cut it does not
    /// make. A lane that prints a read timing gets two chars of slack, for that timing printing wider on the second
    /// call; a lane that prints none is served at exactly its own width.</summary>
    [Theory]
    [MemberData(nameof(WholeFirstLanes))]
    public void AWholeAnswerIsServedAtItsOwnWidth(string lane)
    {
        var call = WholeFirstCall(lane);
        var whole = call(80_000);
        Assert.DoesNotContain("spilled:", whole);
        int cap = whole.Length + (System.Text.RegularExpressions.Regex.IsMatch(whole, @"\d ms(\n|$)") ? 2 : 0);

        var text = call(cap);

        Assert.False(RenderFloorAssert.IsFloorRefusal(text), text);
        Assert.True(text.Length <= cap, $"{text.Length} chars at max_chars={cap}");
        Assert.DoesNotContain("max_chars=" + cap, text);   // no cut notice
        Assert.DoesNotContain("spilled:", text);
    }

    /// <summary>Lanes whose render trims its trailing newline after the loop, so a pass stopped one past a row boundary
    /// comes back no wider than its bound.</summary>
    public static TheoryData<string> RowBoundaryLanes => new() { "scan summary", "scan group_by", "tree", "info_order" };

    Func<int, string> RowBoundaryCall(string lane) => lane switch
    {
        "scan group_by" => c => RecordsTools.Records(_w.Svc, types: new[] { "KYWD" }, max_chars: c,
                                                     project: new RecordsTools.RecordsProject { form = "aggregate", group_by = "defined_in" }),
        "tree" => c => RecordsTools.Records(_w.Svc, formids: TopicIds, max_chars: c,
                                            project: new RecordsTools.RecordsProject { form = "tree" }),
        "info_order" => c => RecordsTools.Records(_w.Svc, formids: TopicIds, max_chars: c,
                                                  project: new RecordsTools.RecordsProject { form = "info_order" }),
        _ => WholeFirstCall(lane),
    };

    /// <summary>A bounded whole pass that stopped is never served as the whole answer (#986): at every cap a row of the
    /// whole answer ends at, the reply is the whole answer, a cut naming that cap, or a refusal; never fewer rows with no
    /// notice.</summary>
    [Theory]
    [MemberData(nameof(RowBoundaryLanes))]
    public void NoCapAtARowBoundaryServesFewerRowsWithoutANotice(string lane)
    {
        SpillFolders.Emptied(_w.Svc);
        var call = RowBoundaryCall(lane);
        var whole = call(80_000);
        var shortened = new List<string>();
        for (int cap = whole.IndexOf('\n'); cap >= 0 && shortened.Count < 6; cap = whole.IndexOf('\n', cap + 1))
        {
            var r = call(cap);
            if (r == whole || RenderFloorAssert.IsFloorRefusal(r) || r.Contains("max_chars=" + cap, StringComparison.Ordinal)) continue;
            shortened.Add($"@{cap}: {r.Length} chars, no cut notice");
        }
        Assert.Empty(shortened);
    }

    Func<int, string> OffOrderEverything =>c => RecordsTools.Records(_w.Svc, types: new[] { "WEAP" }, source: OffSource, max_chars: c,
                                                                      project: new RecordsTools.RecordsProject { form = "everything" });

    /// <summary>A spilling call refused below its floor leaves no file, carries the epoch stamp, and names a cap that was
    /// measured with its spill block in place: the call at that cap is served cut, spilling.</summary>
    [Fact]
    public void ARefusedSpillingCallWritesNoFileAndNamesACapMeasuredWithItsSpillBlock()
    {
        var spills = SpillFolders.Emptied(_w.Svc);

        var refused = OffOrderEverything(200);

        Assert.Empty(Directory.GetFileSystemEntries(spills));
        Assert.Contains("\nepoch=", refused);
        var served = RenderFloorAssert.RefusesAndTheNamedCapFits(refused, 200, OffOrderEverything);
        Assert.Contains("spilled: complete result", served);
        Assert.Contains(Assert.Single(Directory.GetFiles(spills, "*.jsonl")), served);
    }

    /// <summary>A served call whose spill cannot be written says so in the reply, inside its cap; and the cap a refusal
    /// named while the write was failing is one the call fits once it can write, so it was not sized off the warning.</summary>
    [Fact]
    public void AFailedSpillIsStatedAndNeverSizesTheNamedCap()
    {
        var spills = SpillFolders.Emptied(_w.Svc);
        Directory.Delete(spills);
        File.WriteAllText(spills, "a file where the results folder goes");
        try
        {
            int named = RenderFloorAssert.Named(OffOrderEverything(200));
            var failed = OffOrderEverything(named);

            Assert.True(failed.Length <= named, $"{failed.Length} chars at max_chars={named}");
            Assert.Contains("could NOT be written", failed);

            File.Delete(spills);
            Assert.Contains("spilled: complete result", RenderFloorAssert.RefusesAndTheNamedCapFits(OffOrderEverything(200), 200, OffOrderEverything));
            var written = OffOrderEverything(named);
            Assert.False(RenderFloorAssert.IsFloorRefusal(written), written);
            Assert.True(written.Length <= named, $"{written.Length} chars at max_chars={named}");
        }
        finally
        {
            if (File.Exists(spills)) File.Delete(spills);
        }
    }

    /// <summary>The cap a records refusal names holds the same call made again with its read timing three digits wider
    /// and its spill file's name carrying a -99 counter: the identity form's own render, driven through the ceiling
    /// with both widened for real rather than by the allowance's own arithmetic.</summary>
    [Fact]
    public void TheNamedCapHoldsANextCallThatPrintsItsTimingAndSpillNameWider()
    {
        var rows = _w.Svc.ResolveRefs(TopicIds, null, out var epoch, out _);
        var dir = Path.Combine(Path.GetTempPath(), "hc-nextcall-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string Call(int cap, long ms, string file) => Artifacts.CeilingText(cap,
                (int n, SpillState? sp, WholePass? w, out bool t) => Wire.RenderResolve(rows, n, epoch, sp, out t, "records  form=identity", (rows.Count, ms), w),
                () => ArtifactTarget.Named(Path.Combine(dir, file)),
                t => Artifacts.WriteResolve(rows, epoch.Epoch, t, "ceiling", Array.Empty<KeyValuePair<string, string>>()),
                Wire.EpochLine(epoch));
            int named = RenderFloorAssert.Named(Call(200, 5, "records_x.jsonl"));

            var next = Call(named, 5_000, "records_x-99.jsonl");

            Assert.False(RenderFloorAssert.IsFloorRefusal(next), next);
            Assert.True(next.Length <= named, $"{next.Length} chars at max_chars={named}");
            Assert.Contains("records_x-99.jsonl", next);   // it spilled, so the wider name is what it printed
            Assert.Contains("in 5000 ms", next);
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>The list lane's count table, served cut through the tool: forty plugins give forty groups, it names the
    /// cap that cut it, and lays fewer rows than its whole answer (moved from RecordsListLaneTests, whose table is served
    /// cut only in a band a few chars wide).</summary>
    [Fact]
    public void AListAggregateServedCutSaysWhatItCutOff()
    {
        var ids = _w.PartKeywords.Select(k => $"{k.ID:X6}:{k.ModKey.FileName}").ToArray();
        var project = new RecordsTools.RecordsProject { form = "aggregate", group_by = "defined_in" };
        string Call(int c) => RecordsTools.Records(_w.Svc, formids: ids, project: project, max_chars: c);
        var whole = Call(80_000);

        var (cap, cut) = RenderFloorAssert.ServedCut(Call, t => t.Contains("truncated: rendered", StringComparison.Ordinal));

        Assert.Contains("groups before hitting max_chars=" + cap, cut);
        Assert.True(cut.Split("\n  ").Length < whole.Split("\n  ").Length, "the capped table laid as many rows as the whole one");
    }

    /// <summary>A windowed scan served cut on the text lane says its spill holds the WINDOW, never the complete result,
    /// and where the matches outside it are (moved from RecordsArtifactTests, whose two-row window is served whole or
    /// refused).</summary>
    [Fact]
    public void AWindowedTextSpillSaysWindowAndWhereTheRestAre()
    {
        SpillFolders.Emptied(_w.Svc);
        var (_, text) = RenderFloorAssert.ServedCut(c => RecordsTools.Records(_w.Svc, types: new[] { "DIAL" }, limit: 20, max_chars: c),
                                                    t => t.Contains("spilled:", StringComparison.Ordinal));

        Assert.Contains("spilled: the returned WINDOW (20 rows of 30 total matches)", text);
        Assert.DoesNotContain("complete result", text);
        Assert.Contains("outside the returned window are in NO file", text);
    }

    /// <summary>The identity form served cut on the text lane spills every row (moved from RecordsArtifactTests, whose
    /// identity render is narrower than its floor).</summary>
    [Fact]
    public void TheIdentityTextLaneAutoSpillsUnderTheSameContract()
    {
        var spills = SpillFolders.Emptied(_w.Svc);
        var (_, text) = RenderFloorAssert.ServedCut(c => WholeFirstCall("identity")(c),
                                                    t => t.Contains("spilled:", StringComparison.Ordinal));

        Assert.Contains("spilled: complete result (30 rows)", text);
        Assert.Contains(Assert.Single(Directory.GetFiles(spills, "*.jsonl")), text);
    }

    /// <summary>A list summary the server serves cut counts what it held back (moved from RecordsArtifactTests, whose
    /// eight-record selection is served whole wherever it is not refused).</summary>
    [Fact]
    public void AListSummaryServedCutCountsWhatItHeldBack()
    {
        var (_, cut) = RenderFloorAssert.ServedCut(c => WholeFirstCall("list summary")(c),
                                                   t => t.Contains("[rendered ", StringComparison.Ordinal));

        Assert.Matches(@"\[rendered \d+ of 30 at max_chars=\d+\]", cut);
    }

    /// <summary>A list-lane census is one constant render: over its cap it names its own width, which serves.</summary>
    [Fact]
    public void ACensusOverItsCapNamesItsOwnWidth()
    {
        string Call(int c) => RecordsTools.Records(_w.Svc, formids: TopicIds, counts_only: true, max_chars: c);

        var refused = Call(50);

        var served = RenderFloorAssert.RefusesAndTheNamedCapFits(refused, 50, Call);
        Assert.Equal(RenderFloorAssert.Named(refused), served.Length);
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
