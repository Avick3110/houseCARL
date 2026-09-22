using System.Text.Json;
using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The four lanes #827 did not reach, each of which hedged that "a BSA or a loose mod folder failed to read"
/// and named no folder: the facegen sweep, the scripts sweep, the per-property ".pex not on disk" reason, and the
/// write lane's carry and coverage notes. Each now names the mod folder it could not read (#850), in both transports
/// where the lane has one, and ONCE per document — every lane in one response reads one asset build. The dialogue
/// family and the place refusal, left out of both lists, do the same (#863).</summary>
[Trait("tier", "integration")]
public sealed class UnreadableRootNamedLanesTests : IDisposable
{
    readonly BlockedSweepWorld _w = new();

    public void Dispose() => _w.Dispose();

    /// <summary>The named-root line, as a lane writes it — asserting on the shared lead rather than the mod name
    /// alone, so a name that reached the response some other way cannot pass this test.</summary>
    static string Named(string mod) => BatchRender.RootFailureLead + mod;

    string Sweep(params string[] findings)
        => CheckTools.CheckTool(_w.Svc, findings: findings, max_chars: 60000);

    [Fact]
    public void TheFacegenSweepNamesTheRootItCouldNotRead()
    {
        Assert.True(_w.Blocked, BlockedSweepWorld.NotStaged);

        var text = Sweep("facegen");

        Assert.Contains("failed to read this build", text, StringComparison.Ordinal);
        Assert.Contains(Named(BlockedSweepWorld.BlockedMod), text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheScriptsSweepNamesTheRootItCouldNotRead()
    {
        Assert.True(_w.Blocked, BlockedSweepWorld.NotStaged);

        var text = Sweep("scripts");

        Assert.Contains(".pex not on disk", text, StringComparison.Ordinal);
        Assert.Contains(Named(BlockedSweepWorld.BlockedMod), text, StringComparison.Ordinal);
    }

    /// <summary>The dialogue family hedges an "absent" voice file or .pex on the same build, so the response names the
    /// root at its top in both transports (#863).</summary>
    [Fact]
    public void TheDialogueFamilyNamesTheRootItCouldNotRead()
    {
        Assert.True(_w.Blocked, BlockedSweepWorld.NotStaged);
        var seeds = new[] { _w.TopicSeed };

        var text = CheckTools.CheckTool(_w.Svc, findings: new[] { "dialogue" }, seeds: seeds, max_chars: 60000);
        var json = CheckTools.CheckTool(_w.Svc, findings: new[] { "dialogue" }, seeds: seeds, format: "json",
                                        max_chars: 60000);

        Assert.Contains("may merely be unscanned", text, StringComparison.Ordinal);
        Assert.Contains(Named(BlockedSweepWorld.BlockedMod), text, StringComparison.Ordinal);
        Assert.Contains(RootArrayOf(json), r => r.StartsWith(BlockedSweepWorld.BlockedMod, StringComparison.Ordinal));
    }

    /// <summary>The place refusal for a path nothing provides hedges on the build, on both arms — no source, and a named
    /// source nothing provides — so the response names the root above its rows in both transports (#863).</summary>
    [Fact]
    public void ThePlaceRefusalNamesTheRootItCouldNotRead()
    {
        Assert.True(_w.Blocked, BlockedSweepWorld.NotStaged);

        var outcome = _w.Svc.PlaceAssets(new[]
        {
            new PlaceRequest(@"meshes\hcrootnowhere\absent.nif", null),
            new PlaceRequest(@"meshes\hcrootnowhere\dest.nif", @"meshes\hcrootnowhere\src.nif"),
        }, null, null);
        var text = PlaceWire.Render(outcome, 60000);
        var json = JsonWire.RenderPlaceOutcome(outcome, 60000);

        Assert.All(outcome.Results, r => Assert.Contains(WriteSentences.PlaceSourceScanIncomplete, r.Error));
        Assert.Contains(Named(BlockedSweepWorld.BlockedMod), text, StringComparison.Ordinal);
        Assert.Contains(RootArrayOf(json), r => r.StartsWith(BlockedSweepWorld.BlockedMod, StringComparison.Ordinal));
    }

    /// <summary>One response, two families that hedge, one asset build: the list is the response's, so it is named at
    /// its root once rather than under each head — two copies of it would take half the answer between them.</summary>
    [Fact]
    public void AMergedSweepNamesTheRootOnceForTheWholeResponse()
    {
        Assert.True(_w.Blocked, BlockedSweepWorld.NotStaged);

        AssertEachRootNamedOnce(Sweep("facegen", "scripts"));
    }

    /// <summary>The json twin of the response-level block: the root is an ELEMENT of the array, and nothing was cut.</summary>
    [Fact]
    public void TheSweepsJsonDocumentCarriesTheRootAsAnArrayElement()
    {
        Assert.True(_w.Blocked, BlockedSweepWorld.NotStaged);

        var json = CheckTools.CheckTool(_w.Svc, findings: new[] { "facegen", "scripts" }, format: "json",
                                        max_chars: 60000);

        var named = RootArrayOf(json);
        Assert.Contains(named, r => r.StartsWith(BlockedSweepWorld.BlockedMod, StringComparison.Ordinal));
        Assert.Equal(0, JsonDocument.Parse(json).RootElement
                                    .GetProperty("root_read_failures_omitted").GetInt32());
    }

    /// <summary>The per-property reason inside the scripts listing has no caveat block to point at, so it carries the
    /// root in its own sentence.</summary>
    [Fact]
    public void ThePerPropertyReasonNamesTheRootItCouldNotRead()
    {
        Assert.True(_w.Blocked, BlockedSweepWorld.NotStaged);

        var text = Sweep("scripts");

        Assert.Contains("is not on disk", text, StringComparison.Ordinal);
        Assert.Contains("the loose root that would not read: " + BlockedSweepWorld.BlockedMod, text,
                        StringComparison.Ordinal);
    }

    /// <summary>The write lane's carry notes, off a real compact over a blocked tree. Both passes read one build, so
    /// the root is named once — under the facegen note, with the voice note's hedge above it.</summary>
    [Fact]
    public void TheCarryNotesNameTheRootTheyCouldNotRead()
    {
        using var w = new BlockedCarryWorld();
        Assert.True(w.Blocked, BlockedSweepWorld.NotStaged);

        var outcome = w.Svc.CompactPlugin(BlockedCarryWorld.PluginName);
        var text = WriteTools.RenderCompact(outcome);

        Assert.Contains("'no facegen' result may be incomplete", text, StringComparison.Ordinal);
        Assert.Contains("'no voice' result may be incomplete", text, StringComparison.Ordinal);
        // Both passes read one build, so the voice note repeats no root the facegen note above already named.
        AssertEachRootNamedOnce(text);
    }

    /// <summary>The create lane's two coverage reports. The checks run over a mods tree with one denied folder, so the
    /// root each carries is one they really could not read, not a hand-built list.</summary>
    [Fact]
    public void TheCreateCoverageNotesNameTheRootTheyCouldNotRead()
    {
        using var f = new BlockedReportFixture();
        Assert.True(f.Blocked, BlockedSweepWorld.NotStaged);

        // In production order the voice check runs first and its list is the shorter one — empty here, since a line
        // with no speaker resolves no path. The response names the union, so the binding check's root still lands.
        Assert.Empty(f.Voice.RootFailures);
        Assert.NotEmpty(f.ScriptBinding.RootFailures);
        var text = WriteTools.RenderCreate(f.Outcome, maxChars: 60000);

        Assert.Contains("may merely be unscanned", text, StringComparison.Ordinal);
        // One build behind both reports, so the response carries ONE block of it, not one per report.
        AssertEachRootNamedOnce(text);
    }

    /// <summary>A create whose coverage rows the cap CUT still says a folder went unread and which: the tail is
    /// charged before the rows, so the one case that needs the name most cannot be the case that drops it.</summary>
    [Fact]
    public void ATruncatedCreateStillNamesTheRoot()
    {
        using var f = new BlockedReportFixture();
        Assert.True(f.Blocked, BlockedSweepWorld.NotStaged);

        // A report whose own ROWS the cap cuts, which is the loop that used to return: many lines, small cap.
        var rows = WriteTools.RenderCreate(WithLines(f, 40), maxChars: 1200);
        Assert.Contains("voice coverage truncated", rows, StringComparison.Ordinal);
        Assert.Contains("may merely be unscanned", rows, StringComparison.Ordinal);
        Assert.Contains(Named(BlockedReportFixture.BlockedMod), rows, StringComparison.Ordinal);

        // And the checks' own reports, cut in their undetermined and finding loops.
        var real = WriteTools.RenderCreate(f.Outcome, maxChars: 800);
        Assert.Contains("coverage truncated", real, StringComparison.Ordinal);
        Assert.Contains(Named(BlockedReportFixture.BlockedMod), real, StringComparison.Ordinal);
    }

    /// <summary>The staged fixture's outcome with hand-built voice LINES, as the write-surface probe builds them: the
    /// claim under test is the renderer's budget, not the check's, and the root list stays the staged one.</summary>
    static WritePatchBuilder.CreateOutcome WithLines(BlockedReportFixture f, int count)
    {
        var lines = Enumerable.Range(0, count).Select(i => new VoiceLine(
            default, "HcRootTopic", i, $"line_{i:D4}.fuz", false, null, false,
            $"line_{i:D4}.lip", false, true)).ToList();
        return f.Outcome with
        {
            Voice = new VoiceReport(lines, Array.Empty<VoiceUndetermined>())
                    { RootFailures = WriteTools.CreateRootFailures(f.Outcome) },
            ScriptBinding = ScriptBindingReport.Empty,
        };
    }

    /// <summary>And the block is CHARGED before the rows rather than appended once the budget is spent: at one cap, a
    /// report whose roots are named renders fewer rows than the same report with none.</summary>
    [Fact]
    public void TheNamedRootsBlockIsChargedBeforeTheCoverageRows()
    {
        using var f = new BlockedReportFixture();
        Assert.True(f.Blocked, BlockedSweepWorld.NotStaged);

        var withRoots = WithLines(f, 40);
        var without = withRoots with
            { Voice = new VoiceReport(withRoots.Voice!.Lines, Array.Empty<VoiceUndetermined>()) };

        int rowsWithRoots = CountOf(WriteTools.RenderCreate(withRoots, maxChars: 2000), " resp ");
        int rowsWithout = CountOf(WriteTools.RenderCreate(without, maxChars: 2000), " resp ");

        Assert.True(rowsWithout > rowsWithRoots,
                    $"{rowsWithout} rows without the block, {rowsWithRoots} with it — the block was not charged");
    }

    /// <summary>The json twin of that charge. The array sits above the created rows, like <c>verify_ran</c>, so their
    /// own budget check pays for it: written under them it was the one member nothing charged, and the document grew by
    /// the block's width at every cap. Built outright, with a literal output path — the claim is the renderer's budget,
    /// and a staged fixture's temp path would put its own host-dependent length into the document. A blocked tree names
    /// many roots, so the case is 200 of them.</summary>
    [Fact]
    public void TheJsonCreateDocumentChargesTheRootsToItsRows()
    {
        var many = Enumerable.Range(0, 200)
                             .Select(i => $"BlockedMod{i:D3}: could not read 'meshes' — Access to the path is denied.")
                             .ToList();
        // One line, so the report renders and its roots are the response's; the rows under test are the CREATED ones.
        var line = new[] { new VoiceLine(default, "HcRootTopic", 1, "line.fuz", false, null, false, "line.lip", false, true) };
        var withRoots = Created(60) with
            { Voice = new VoiceReport(line, Array.Empty<VoiceUndetermined>()) { RootFailures = many } };
        var without = withRoots with { Voice = new VoiceReport(line, Array.Empty<VoiceUndetermined>()) };

        int rowsWithRoots = RenderedCreated(JsonWire.RenderCreateOutcome(withRoots, 4000, false, "patch"));
        int rowsWithout = RenderedCreated(JsonWire.RenderCreateOutcome(without, 4000, false, "patch"));

        Assert.True(rowsWithout > rowsWithRoots,
                    $"{rowsWithout} rows without the block, {rowsWithRoots} with it — the block was not charged");
        // And it cannot be cut away by the rows it now costs: one root is named whatever the budget.
        Assert.NotEmpty(RootArrayOf(JsonWire.RenderCreateOutcome(withRoots, 4000, false, "patch")));
    }

    static int RenderedCreated(string json)
        => JsonDocument.Parse(json).RootElement.GetProperty("rendered_created").GetInt32();

    /// <summary>A create of <paramref name="count"/> records, every byte of it fixed, so one host's temp paths cannot
    /// move where a cap falls.</summary>
    static WritePatchBuilder.CreateOutcome Created(int count)
    {
        var key = ModKey.FromFileName("HcRootJson.esp");
        return new WritePatchBuilder.CreateOutcome(
            true, null, @"C:\mods\HcRootJson\HcRootJson.esp", false,
            Enumerable.Range(0, count)
                      .Select(i => new WritePatchBuilder.CreatedRecord(
                          new FormKey(key, (uint)(0x800 + i)), "DialogResponses", $"HcRootInfo{i:D3}",
                          Array.Empty<WritePatchBuilder.OpResult>()))
                      .ToList(),
            Array.Empty<string>(), 512);
    }

    /// <summary>The json twin of the create lane's block, at the document root and as an array element.</summary>
    [Fact]
    public void TheCreatesJsonDocumentCarriesTheRootAsAnArrayElement()
    {
        using var f = new BlockedReportFixture();
        Assert.True(f.Blocked, BlockedSweepWorld.NotStaged);

        var json = JsonWire.RenderCreateOutcome(f.Outcome, 60000, false, "patch");

        Assert.Contains(RootArrayOf(json), r => r.StartsWith(BlockedReportFixture.BlockedMod, StringComparison.Ordinal));
    }

    /// <summary>A merged sweep names the UNION of its families' roots. The service refreshes its asset resolver on
    /// every access and each family takes it separately, so two families in one call can answer off two builds and the
    /// second build's list starts empty: whichever family answered last is not a superset of the other. Staged as the
    /// two results a mid-call rebuild produces — disjoint lists, one per family.</summary>
    [Fact]
    public void AMergedSweepNamesEveryFamilysRootsNotJustOneFamilys()
    {
        var facegenRoot = "ModA: could not read 'facegeom' — Access to the path is denied.";
        var scriptsRoot = "ModB: could not read 'Scripts' — Access to the path is denied.";
        var sweep = new CheckSweep(
            CheckErrorsFixtures.Sel("facegen", "scripts"),
            Scripts: ScriptsFixtures.Result(rootFailures: new[] { scriptsRoot }),
            FaceGen: FaceGenResult(new[] { facegenRoot }));

        var text = Wire.RenderCheck(sweep, 60000);
        var json = JsonWire.RenderCheck(sweep, 60000);

        Assert.Contains(Named("ModA"), text, StringComparison.Ordinal);
        Assert.Contains(Named("ModB"), text, StringComparison.Ordinal);
        Assert.Equal(new[] { facegenRoot, scriptsRoot }, RootArrayOf(json));
    }

    /// <summary>A facegen result that found nothing but could not read one root — the shape a family hands the render
    /// when its scan hit a blocked folder.</summary>
    static FaceGenCheckResult FaceGenResult(IReadOnlyList<string> roots) =>
        new(Array.Empty<FaceGenFinding>(), 0, 0, 0, 0, 0, null, null, false,
            new Dictionary<string, string>(), null, null, null, FaceGenFindingClass.All, "deadbeefdeadbeef", 0, null,
            ReadIncomplete: true, WholeOrder: true, NpcsNoFaceGenRace: 0, NpcsRaceUnresolved: 0, WithheldBenign: null,
            RootFailures: roots);

    /// <summary>The create lane names the UNION of its two checks' roots, in either order. They scan different
    /// subtrees and each takes its list at its own return, so the one that ran first carries the shorter one: taking
    /// either alone names the folder that hid a voice file and not the one that hid the .pex.</summary>
    [Fact]
    public void TheCreateLaneNamesBothChecksRootsWhicheverRanFirst()
    {
        var a = "ModA: could not read 'Sound' — Access to the path is denied.";
        var b = "ModB: could not read 'Scripts' — Access to the path is denied.";
        var line = new[] { new VoiceLine(default, "T", 1, "l.fuz", false, null, false, "l.lip", false, true) };
        var finding = new[] { new ScriptBindingFinding(default, "T", ScriptBindingStatus.ScriptNotCompiled,
                                                       new[] { "S" }, new[] { "S.pex" }, true, "no .pex") };

        // The voice check ran first, so it holds the shorter list — production's order.
        var voiceFirst = Created(1) with
            {
                Voice = new VoiceReport(line, Array.Empty<VoiceUndetermined>()) { RootFailures = new[] { a } },
                ScriptBinding = new ScriptBindingReport(finding) { RootFailures = new[] { a, b } },
            };
        // And the reverse, so the answer cannot depend on which check happened to run first.
        var bindingFirst = voiceFirst with
            {
                Voice = new VoiceReport(line, Array.Empty<VoiceUndetermined>()) { RootFailures = new[] { a, b } },
                ScriptBinding = new ScriptBindingReport(finding) { RootFailures = new[] { a } },
            };

        Assert.Equal(new[] { a, b }, WriteTools.CreateRootFailures(voiceFirst));
        Assert.Equal(new[] { a, b }, WriteTools.CreateRootFailures(bindingFirst));
        // And both reach the render, not just the data.
        var text = WriteTools.RenderCreate(voiceFirst, maxChars: 60000);
        Assert.Contains(Named("ModA"), text, StringComparison.Ordinal);
        Assert.Contains(Named("ModB"), text, StringComparison.Ordinal);
    }

    /// <summary>The cut is ONE rule with no transport in it, driven through the two REAL renders of one staged sweep:
    /// comparing the two helpers would compare a function with itself, and a lane that stopped using the shared rule
    /// would stay green. A handful of caps, because the rule is not cap-shaped.</summary>
    [Theory]
    [InlineData(1500)]
    [InlineData(3000)]
    [InlineData(8000)]
    [InlineData(60000)]
    public void TheTwoRendersOfOneSweepNameTheSameRoots(int cap)
    {
        Assert.True(_w.Blocked, BlockedSweepWorld.NotStaged);
        var families = new[] { "facegen", "scripts" };

        int text = CountOf(CheckTools.CheckTool(_w.Svc, findings: families, max_chars: cap),
                           BatchRender.RootFailureLead);
        var json = CheckTools.CheckTool(_w.Svc, findings: families, format: "json", max_chars: cap);

        Assert.True(text > 0, $"the text render named no root at max_chars={cap}");
        Assert.Equal(text, RootArrayOf(json).Count);
    }

    static IReadOnlyList<string> RootArrayOf(string json)
        => JsonDocument.Parse(json).RootElement.GetProperty("root_read_failures")
                       .EnumerateArray().Select(e => e.GetString() ?? "").ToList();

    /// <summary>Every root this response names, it names ONCE: a second block over one asset build would repeat a
    /// line verbatim. Robust to the list growing as later reads ask about more folders, which a count is not.</summary>
    static void AssertEachRootNamedOnce(string text)
    {
        var named = text.Split('\n').Where(l => l.Contains(BatchRender.RootFailureLead, StringComparison.Ordinal))
                        .Select(l => l.Trim()).ToList();
        Assert.NotEmpty(named);
        Assert.Equal(named.Count, named.Distinct(StringComparer.Ordinal).Count());
    }

    static int CountOf(string haystack, string needle)
    {
        int n = 0;
        for (int at = haystack.IndexOf(needle, StringComparison.Ordinal); at >= 0;
             at = haystack.IndexOf(needle, at + needle.Length, StringComparison.Ordinal)) n++;
        return n;
    }
}

/// <summary>Its own instance, never a shared fixture: it denies the current account one whole mod folder. The order
/// carries an NPC, so the facegen sweep scans, a weapon bound to a script class no mod compiles, so the scripts
/// sweep reaches the per-property ".pex not on disk" reason, and a topic for the dialogue family to seed.</summary>
sealed class BlockedSweepWorld : IDisposable
{
    public const string BlockedMod = "RootBlockedSweepMod";
    public const string NotStaged = "the deny ACE did not bite on this host, so the unreadable root was never staged";

    public string Root { get; }
    public LoadOrderService Svc { get; }
    public bool Blocked { get; }

    /// <summary>The staged topic, as a dialogue seed.</summary>
    public string TopicSeed { get; }

    readonly string _blockedDir;

    public BlockedSweepWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-rootsweep-" + Guid.NewGuid().ToString("N"));
        var instance = Path.Combine(Root, "instance");
        var profile = Path.Combine(instance, "profiles", "Default");
        var mods = Path.Combine(instance, "mods");
        var pluginMod = Path.Combine(mods, "SweepPluginMod");
        var assetMod = Path.Combine(mods, "SweepAssetMod");
        _blockedDir = Path.Combine(mods, BlockedMod);
        foreach (var d in new[] { profile, Path.Combine(Root, "game", "Data"), pluginMod, assetMod })
            Directory.CreateDirectory(d);

        // One master with the two records the sweeps need: an NPC for the facegen join, and a weapon whose attached
        // script class is compiled nowhere, which is what makes the .pex read fail and carry its reason.
        var key = new ModKey("HcRootSweep", ModType.Master);
        var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        var race = mod.Races.AddNew();
        race.EditorID = "HcRootSweepRace";
        race.Flags |= Race.Flag.FaceGenHead;
        var npc = mod.Npcs.AddNew();
        npc.EditorID = "HcRootSweepNpc";
        npc.Race.SetTo(race);
        var weapon = mod.Weapons.AddNew();
        weapon.EditorID = "HcRootSweepWeapon";
        var vmad = new VirtualMachineAdapter();
        vmad.Scripts.Add(new ScriptEntry { Name = "HcRootSweepUncompiled" });
        weapon.VirtualMachineAdapter = vmad;
        // A topic whose one line carries an uncompiled result script, so the dialogue family reads the Scripts subtree.
        var topic = mod.DialogTopics.AddNew();
        topic.EditorID = "HcRootSweepTopic";
        var info = new DialogResponses(mod.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcRootSweepInfo" };
        var infoVmad = new DialogResponsesAdapter();
        infoVmad.Scripts.Add(new ScriptEntry { Name = "HcRootSweepLineUncompiled" });
        info.VirtualMachineAdapter = infoVmad;
        info.Responses.Add(new DialogResponse { ResponseNumber = 1 });
        topic.Responses.Add(info);
        TopicSeed = topic.FormKey.ToString();
        mod.BeginWrite.ToPath(Path.Combine(pluginMod, key.FileName.String))
           .WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        // A readable mod provides each layer too, so no lane answers "nothing here" instead of scanning.
        Write(assetMod, @"Scripts\readable.pex", "not a real pex");
        Write(assetMod, FaceGenPath.For(npc.FormKey, FaceGenSlot.Mesh), "mesh");

        // The blocked mod's content is written FIRST: from the deny on, the folder cannot be touched.
        Write(_blockedDir, @"Scripts\blocked.pex", "x");
        Write(_blockedDir, FaceGenPath.For(npc.FormKey, FaceGenSlot.Tint), "tint");

        // From the deny on, anything that throws would leave the ACE behind and block the temp tree's own cleanup.
        try
        {
            Blocked = DenyAce.TryDeny(_blockedDir);
            Svc = Stage(instance, profile, key.FileName.String);
        }
        catch
        {
            DenyAce.Undeny(_blockedDir);
            throw;
        }
    }

    static void Write(string modDir, string rel, string text)
    {
        var path = Path.Combine(modDir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    LoadOrderService Stage(string instance, string profile, string pluginFile)
    {
        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");
        File.WriteAllText(Path.Combine(profile, "loadorder.txt"), "# header\r\n" + pluginFile + "\r\n");
        File.WriteAllText(Path.Combine(profile, "plugins.txt"), "*" + pluginFile + "\r\n");
        File.WriteAllText(Path.Combine(profile, "modlist.txt"),
            "# header\r\n+" + BlockedMod + "\r\n+SweepAssetMod\r\n+SweepPluginMod\r\n");
        File.WriteAllText(Path.Combine(profile, "Skyrim.ini"), "[Archive]\r\nsResourceArchiveList=\r\n");

        return LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(Root, "houseCARL.user.json")));
    }

    public void Dispose()
    {
        Svc.Dispose();
        DenyAce.Undeny(_blockedDir);
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}

/// <summary>The compact lane's world: one ESP with an NPC to renumber, so the facegen and voice carry passes both run
/// and both hedge, plus one denied mod folder for them to name.</summary>
sealed class BlockedCarryWorld : IDisposable
{
    public const string BlockedMod = "RootBlockedCarryMod";
    public const string PluginName = "HcRootCarry.esp";

    public string Root { get; }
    public LoadOrderService Svc { get; }
    public bool Blocked { get; }

    readonly string _blockedDir;

    public BlockedCarryWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-rootcarry-" + Guid.NewGuid().ToString("N"));
        var instance = Path.Combine(Root, "instance");
        var profile = Path.Combine(instance, "profiles", "Default");
        var mods = Path.Combine(instance, "mods");
        var faceMod = Path.Combine(mods, "CarryFaceMod");
        _blockedDir = Path.Combine(mods, BlockedMod);
        foreach (var d in new[] { profile, Path.Combine(Root, "game", "Data"), faceMod })
            Directory.CreateDirectory(d);

        // An id above the ESL ceiling, so the compact really renumbers it and the carry passes have work to consider.
        var key = ModKey.FromFileName(PluginName);
        var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        mod.Npcs.Add(new Npc(new FormKey(key, 0xA10), SkyrimRelease.SkyrimSE) { EditorID = "HcRootCarryNpc" });
        mod.BeginWrite.ToPath(Path.Combine(faceMod, PluginName))
           .WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        var blockedFile = Path.Combine(_blockedDir, "Scripts", "blocked.pex");
        Directory.CreateDirectory(Path.GetDirectoryName(blockedFile)!);
        File.WriteAllText(blockedFile, "x");

        try
        {
            Blocked = DenyAce.TryDeny(_blockedDir);
            File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
                "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
                + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");
            File.WriteAllText(Path.Combine(profile, "loadorder.txt"), "# header\r\n" + PluginName + "\r\n");
            File.WriteAllText(Path.Combine(profile, "plugins.txt"), "*" + PluginName + "\r\n");
            File.WriteAllText(Path.Combine(profile, "modlist.txt"),
                "# header\r\n+" + BlockedMod + "\r\n+CarryFaceMod\r\n");
            File.WriteAllText(Path.Combine(profile, "Skyrim.ini"), "[Archive]\r\nsResourceArchiveList=\r\n");
            Svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(Root, "houseCARL.user.json")));
            Svc.Stats();
        }
        catch
        {
            DenyAce.Undeny(_blockedDir);
            throw;
        }
    }

    public void Dispose()
    {
        Svc.Dispose();
        DenyAce.Undeny(_blockedDir);
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}

/// <summary>The create lane's two coverage checks, run for real over a mods tree with one denied folder. No MO2
/// instance: both checks take a resolver and an asset resolver, which is all a created dialogue line needs checking.</summary>
sealed class BlockedReportFixture : IDisposable
{
    public const string BlockedMod = "RootBlockedReportMod";

    public string Root { get; }
    public string PatchPath { get; }
    public bool Blocked { get; }
    public VoiceReport Voice { get; }
    public ScriptBindingReport ScriptBinding { get; }
    public WritePatchBuilder.CreateOutcome Outcome { get; }

    readonly string _blockedDir;

    public BlockedReportFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-rootreport-" + Guid.NewGuid().ToString("N"));
        var mods = Path.Combine(Root, "mods");
        var dataDir = Path.Combine(Root, "Data");
        _blockedDir = Path.Combine(mods, BlockedMod);
        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(Path.Combine(_blockedDir, "Scripts"));
        File.WriteAllText(Path.Combine(_blockedDir, "Scripts", "blocked.pex"), "x");

        // A patch holding one topic and one scripted response, written and re-opened the way each check sees a real one.
        var key = new ModKey("HcRootReport", ModType.Plugin);
        PatchPath = Path.Combine(Root, key.FileName.String);
        var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        var topic = mod.DialogTopics.AddNew();
        topic.EditorID = "HcRootTopic";
        var info = new DialogResponses(mod.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcRootInfo" };
        var vmad = new DialogResponsesAdapter();
        vmad.Scripts.Add(new ScriptEntry { Name = "HcRootUncompiled" });
        info.VirtualMachineAdapter = vmad;
        // Spoken responses, or the voice check has no line to verdict and writes no block at all.
        info.Responses.Add(new DialogResponse { ResponseNumber = 1 });
        topic.Responses.Add(info);
        mod.BeginWrite.ToPath(PatchPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        try
        {
            Blocked = DenyAce.TryDeny(_blockedDir);
            var created = new[] { new WritePatchBuilder.CreatedRecord(info.FormKey, "DialogResponses", "HcRootInfo",
                                                                     Array.Empty<WritePatchBuilder.OpResult>()) };
            using var resolver = LoadOrderResolver.Build(new[] { PatchPath });
            using var assets = AssetResolver.Build("", mods, dataDir, new[] { BlockedMod },
                                                   Array.Empty<ActiveArchive>());
            // PRODUCTION ORDER — voice then binding, as RecordWrites enriches the outcome. It matters: each check
            // materialises the root list at its own return off a dictionary that fills lazily, so the first one carries
            // the shorter list, and a render that took either alone would name the wrong folder.
            Voice = VoiceCheck.Run(PatchPath, created, resolver, assets);
            ScriptBinding = DialogueScriptCheck.Run(PatchPath, created, assets);
            Outcome = new WritePatchBuilder.CreateOutcome(true, null, PatchPath, false, created,
                                                         Array.Empty<string>(), 512)
                      { Voice = Voice, ScriptBinding = ScriptBinding };
        }
        catch
        {
            DenyAce.Undeny(_blockedDir);
            throw;
        }
    }

    public void Dispose()
    {
        DenyAce.Undeny(_blockedDir);
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}
