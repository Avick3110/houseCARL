using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The errors and dialogue sweeps take their view and their roots in one hold, the off-order memo answers only
/// for its roots, and one errors call reads the profile's composition once.</summary>
[Trait("tier", "integration")]
public sealed class ErrorsCheckPinTests : IDisposable
{
    const string MasterName = "HcEpMaster.esm";
    const string PatchName = "HcEpPatch.esp";
    const string OffName = "HcEpOff.esp";
    const string CleanName = "HcEpClean.esp";
    const string OtherProfile = "Other";
    const string ScriptName = "HcEpScript";

    readonly string _root;
    readonly string _ini;
    readonly string _topicSeed;
    readonly string _gameA;
    readonly string _gameB;
    readonly LoadOrderService _svc;
    Thread? _mover;

    public ErrorsCheckPinTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "hc-errors-pin-" + Guid.NewGuid().ToString("N"));
        var instance = Path.Combine(_root, "instance");
        var profileDir = Path.Combine(instance, "profiles", "Default");
        var patchDir = Path.Combine(instance, "mods", "PatchMod");
        var gameA = _gameA = Path.Combine(_root, "gameA");
        _gameB = Path.Combine(_root, "gameB");
        Directory.CreateDirectory(profileDir);
        Directory.CreateDirectory(patchDir);
        Directory.CreateDirectory(Path.Combine(gameA, "Data"));
        Directory.CreateDirectory(Path.Combine(_gameB, "Data"));   // the second game folder: no master, no off-order plugin
        _ini = Path.Combine(instance, "ModOrganizer.ini");
        File.WriteAllText(_ini, IniFor(gameA));

        // Game A's Data holds the unchecked master and a plugin in no list; the order holds only mod-folder plugins,
        // so moving the game folder moves the roots and leaves the order and its epoch unchanged.
        var master = new SkyrimMod(new ModKey("HcEpMaster", ModType.Master), SkyrimRelease.SkyrimSE);
        var race = master.Races.AddNew(); race.EditorID = "HcEpMasterRace";
        master.BeginWrite.ToPath(Path.Combine(gameA, "Data", MasterName)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        var off = new SkyrimMod(new ModKey("HcEpOff", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var offRace = off.Races.AddNew(); offRace.EditorID = "HcEpOffRace";
        off.BeginWrite.ToPath(Path.Combine(gameA, "Data", OffName)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        var patch = new SkyrimMod(new ModKey("HcEpPatch", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var npc = patch.Npcs.AddNew(); npc.EditorID = "HcEpPatchNpc"; npc.Race.SetTo(race.FormKey);
        // A topic whose unmodeled SNAM marker is warned about only where the patch is not force-loaded.
        var topic = patch.DialogTopics.AddNew(); topic.EditorID = "HcEpTopic"; topic.SubtypeName = new RecordType("ZZZZ");
        _topicSeed = $"{topic.FormKey.ID:X6}:{PatchName}";
        // A line binding a script whose .pex only the first profile's mods provide.
        var vmad = new DialogResponsesAdapter();
        vmad.Scripts.Add(new ScriptEntry { Name = ScriptName });
        topic.Responses.Add(new DialogResponses(patch.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcEpLine", VirtualMachineAdapter = vmad });
        var scriptsDir = Path.Combine(instance, "mods", "ScriptMod", "Scripts");
        Directory.CreateDirectory(scriptsDir);
        File.WriteAllBytes(Path.Combine(scriptsDir, ScriptName + ".pex"), new byte[] { 0xFA, 0x57, 0xC0, 0xDE });
        patch.BeginWrite.ToPath(Path.Combine(patchDir, PatchName)).WithLoadOrder(new ISkyrimModGetter[] { master }).Write();

        // A plugin with no masters and nothing to report: a sweep of it alone needs no composition.
        var clean = new SkyrimMod(new ModKey("HcEpClean", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var cleanRace = clean.Races.AddNew(); cleanRace.EditorID = "HcEpCleanRace";
        clean.BeginWrite.ToPath(Path.Combine(patchDir, CleanName)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        File.WriteAllText(Path.Combine(profileDir, "loadorder.txt"), "# header\r\n" + MasterName + "\r\n" + CleanName + "\r\n" + PatchName + "\r\n");
        File.WriteAllText(Path.Combine(profileDir, "plugins.txt"), MasterName + "\r\n*" + CleanName + "\r\n*" + PatchName + "\r\n");
        File.WriteAllText(Path.Combine(profileDir, "modlist.txt"), "# header\r\n+ScriptMod\r\n+PatchMod\r\n");

        // The second profile: the same order, but plugins.txt does not list the patch, so there it is force-loaded,
        // and the script mod is disabled, so there the line's .pex is missing.
        var otherDir = Path.Combine(instance, "profiles", OtherProfile);
        Directory.CreateDirectory(otherDir);
        File.WriteAllText(Path.Combine(otherDir, "loadorder.txt"), "# header\r\n" + MasterName + "\r\n" + CleanName + "\r\n" + PatchName + "\r\n");
        File.WriteAllText(Path.Combine(otherDir, "plugins.txt"), MasterName + "\r\n*" + CleanName + "\r\n");
        File.WriteAllText(Path.Combine(otherDir, "modlist.txt"), "# header\r\n-ScriptMod\r\n+PatchMod\r\n");

        _svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(_root, "houseCARL.user.json")));
    }

    public void Dispose()
    {
        _mover?.Join();
        _svc.Dispose();
        try { Directory.Delete(_root, true); } catch { /* temp cleanup best-effort */ }
    }

    static string IniFor(string game, string profile = "Default")
        => "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(" + profile + ")\r\ngamePath=@ByteArray("
           + game.Replace(@"\", @"\\") + ")\r\n";

    /// <summary>Rewrites the ini and has another call re-derive the roots. Run inside the sweep's hold, that call
    /// cannot re-derive until the hold ends, so it is not waited for; run outside it, it is joined, so the re-derive
    /// has landed before the sweep takes anything else. No timing either way.</summary>
    void RederiveFromAnotherCall(string ini)
    {
        File.WriteAllText(_ini, ini);
        _mover = new Thread(() => _svc.CaptureView());
        _mover.Start();
        if (!_svc.GateHeldByThisThread) _mover.Join();
    }

    ErrorCheckResult SweepWithMoveAfterPin(IReadOnlyList<string>? plugins)
    {
        _svc.CheckArea.AfterCheckPinForGuard = () => RederiveFromAnotherCall(IniFor(_gameB));
        var r = _svc.CheckErrors(plugins, 1000);
        _svc.CheckArea.AfterCheckPinForGuard = null;
        _mover!.Join();
        return r;
    }

    ErrorCheckResult SweepImplicitWithProfileSwitchAfterPin()
    {
        _svc.CheckArea.AfterCheckPinForGuard = () => RederiveFromAnotherCall(IniFor(_gameA, OtherProfile));
        var r = _svc.CheckErrors(null, 1000, exclude: new[] { SweepExclusion.ImplicitToken });
        _svc.CheckArea.AfterCheckPinForGuard = null;
        _mover!.Join();
        return r;
    }

    static bool IsPatch(PluginErrors p) => p.Plugin.Equals(PatchName, StringComparison.OrdinalIgnoreCase);

    static IReadOnlyList<string>? InstalledButInactive(ErrorCheckResult r)
        => r.Reports.Single(p => p.Plugin.Equals(PatchName, StringComparison.OrdinalIgnoreCase)).InstalledButInactiveMasters;

    [Fact]
    public void TheOffOrderLocateUsesTheRootsPinnedWithTheView()
    {
        var before = _svc.CheckErrors(new[] { OffName }, 1000);                 // warms the index
        Assert.Null(before.Error);

        var during = SweepWithMoveAfterPin(new[] { OffName });
        Assert.Null(during.Error);
        Assert.Equal(new[] { OffName }, during.OffOrderScanned);
        Assert.Equal(before.Epoch, during.Epoch);

        // The move landed for the next call: game B's Data has no copy.
        Assert.Contains("no on-disk copy", _svc.CheckErrors(new[] { OffName }, 1000).Error);
    }

    [Fact]
    public void TheMissingMasterSplitUsesTheRootsPinnedWithTheView()
    {
        Assert.Equal(new[] { MasterName }, InstalledButInactive(_svc.CheckErrors(null, 1000)));   // warms the index

        var during = SweepWithMoveAfterPin(null);
        Assert.Null(during.Error);
        Assert.Equal(new[] { MasterName }, InstalledButInactive(during));

        // The move landed for the next call: the master is no longer installed.
        Assert.Empty(InstalledButInactive(_svc.CheckErrors(null, 1000))!);
    }

    [Fact]
    public void TheImplicitGroupUsesTheProfilePinnedWithTheView()
    {
        var exclude = new[] { SweepExclusion.ImplicitToken };
        Assert.Contains(_svc.CheckErrors(null, 1000, exclude: exclude).Reports, IsPatch);   // warms; not force-loaded here

        var during = SweepImplicitWithProfileSwitchAfterPin();
        Assert.Null(during.Error);
        Assert.Contains(during.Reports, IsPatch);

        // The switch landed for the next call: the other profile force-loads the patch, so it is excluded.
        Assert.DoesNotContain(_svc.CheckErrors(null, 1000, exclude: exclude).Reports, IsPatch);
    }

    static bool WarnsOfTheMarker(DialogueCheckResult r)
        => r.Topics.Any(t => t.Topic.Issues.Any(i => i.Message.Contains("not a marker houseCARL models")));

    static ScriptBindingStatus LineScript(DialogueCheckResult r)
        => r.Topics.SelectMany(t => t.Topic.ScriptFindings).Single().Status;

    [Fact]
    public void TheDialogueForceLoadedSetAndAssetsUseTheProfilePinnedWithTheView()
    {
        var seeds = new[] { _topicSeed };
        var before = _svc.CheckDialogue(seeds, 1000);                         // warms; not force-loaded here
        Assert.True(WarnsOfTheMarker(before));
        Assert.Equal(ScriptBindingStatus.BoundAndCompiled, LineScript(before));

        _svc.CheckArea.AfterCheckPinForGuard = () => RederiveFromAnotherCall(IniFor(_gameA, OtherProfile));
        var during = _svc.CheckDialogue(seeds, 1000);
        _svc.CheckArea.AfterCheckPinForGuard = null;
        _mover!.Join();
        Assert.Null(during.Error);
        Assert.True(WarnsOfTheMarker(during));
        Assert.Equal(ScriptBindingStatus.BoundAndCompiled, LineScript(during));

        // The switch landed for the next call: the other profile force-loads the patch, so the warning is not its
        // author's, and disables the script mod, so the .pex is missing.
        var after = _svc.CheckDialogue(seeds, 1000);
        Assert.False(WarnsOfTheMarker(after));
        Assert.Equal(ScriptBindingStatus.ScriptNotCompiled, LineScript(after));
    }

    [Fact]
    public void TheOffOrderMemoRecomputesUnderOtherRoots()
    {
        var view = _svc.CaptureView();
        var rootsA = ((ILoadOrderHost)_svc).CaptureRoots();
        var rootsB = rootsA with { DataDir = Path.Combine(_gameB, "Data") };
        var memo = new SweepOffOrderMemo();
        var plugins = new[] { OffName };

        Assert.Null(SweepOffOrderScope.Split(view, plugins, rootsA, out _, out var offA, memo));
        Assert.Single(offA);

        // Same epoch, same list: only the roots differ, and game B's Data has no copy.
        var refusal = SweepOffOrderScope.Split(view, plugins, rootsB, out _, out _, memo);
        Assert.Contains("no on-disk copy", refusal?.Message);
    }

    /// <summary>This world's composition parses during <paramref name="act"/>, on any thread. Counted at the parse, so
    /// a consumer that parses for itself counts too; an index build's own parse does not.</summary>
    int CompositionReadsDuring(Action act)
    {
        int n = 0;
        void Count(string profileDir)
        {
            if (profileDir.StartsWith(_root, StringComparison.OrdinalIgnoreCase)) Interlocked.Increment(ref n);
        }
        Mo2LoadOrder.CompositionRead += Count;
        try { act(); }
        finally { Mo2LoadOrder.CompositionRead -= Count; }
        return n;
    }

    [Fact]
    public void OneErrorsCallReadsTheCompositionOnce()
    {
        _svc.CheckErrors(null, 1000);                                           // warms the index
        ErrorCheckResult? r = null;

        // An off-order name, the implicit group and a missing master: three consumers of the composition.
        var reads = CompositionReadsDuring(() =>
            r = _svc.CheckErrors(new[] { PatchName, OffName }, 1000, exclude: new[] { SweepExclusion.ImplicitToken }));
        Assert.Null(r!.Error);
        Assert.Equal(new[] { OffName }, r.OffOrderScanned);
        Assert.Equal(new[] { MasterName }, InstalledButInactive(r));

        Assert.Equal(1, reads);
    }

    [Fact]
    public void AnErrorsCallThatNeedsNoCompositionParsesNone()
    {
        _svc.CheckErrors(null, 1000);                                           // warms the index
        ErrorCheckResult? r = null;

        // No off-order name, no implicit group, no missing master.
        var reads = CompositionReadsDuring(() => r = _svc.CheckErrors(new[] { CleanName }, 1000));
        Assert.Null(r!.Error);
        Assert.Empty(r.OffOrderScanned);
        Assert.DoesNotContain(r.Reports, p => p.MissingMasters.Count > 0);

        Assert.Equal(0, reads);
    }
}
