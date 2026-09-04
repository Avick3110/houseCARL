using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The synthetic MO2 instance the missing-master remedy tests are driven over. Three declared masters on one
/// active plugin, one per standing:
/// <list type="bullet">
/// <item><c>Skyrim.esm</c> — in <c>loadorder.txt</c>, absent from <c>plugins.txt</c>, so force-loaded and
///   SATISFIED. It is the control that keeps the two remedy lines from being a blanket claim about every
///   declared master.</item>
/// <item><c>HcMmGhost.esm</c> — the file sits in a mod folder MO2 has switched off (<c>-GhostMod</c>), so it
///   is installed and NOT active. Its remedy is enable.</item>
/// <item><c>HcMmAbsent.esm</c> — written outside the instance entirely (no mod folder, not the overwrite
///   layer, not game Data), so no copy exists in the install. Its remedy is install.</item>
/// </list>
/// A second active plugin, <c>HcMmClean.esp</c>, masters only <c>Skyrim.esm</c> — the all-satisfied control.
/// </summary>
public sealed class CheckMasterRemedyWorld : IDisposable
{
    public string Root { get; }
    public string Instance { get; }

    /// <summary>The plugin declaring one master of each standing.</summary>
    public string PatchName => "HcMmPatch.esp";
    /// <summary>The plugin whose every declared master is satisfied.</summary>
    public string CleanName => "HcMmClean.esp";
    /// <summary>Installed, in a DISABLED mod — the enable case.</summary>
    public string GhostName => "HcMmGhost.esm";
    /// <summary>Installed nowhere in the instance — the install case.</summary>
    public string AbsentName => "HcMmAbsent.esm";

    public LoadOrderService Svc { get; }

    public CheckMasterRemedyWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-check-master-remedy-" + Guid.NewGuid().ToString("N"));
        Instance = Path.Combine(Root, "instance");
        var profileDir = Path.Combine(Instance, "profiles", "Default");
        var mods = Path.Combine(Instance, "mods");
        var outside = Path.Combine(Root, "not-installed");   // deliberately NOT under mods/, Data, or overwrite
        Directory.CreateDirectory(profileDir);
        Directory.CreateDirectory(mods);
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(Path.Combine(Root, "game", "Data"));
        File.WriteAllText(Path.Combine(Instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");

        var vanillaDir = Path.Combine(mods, "VanillaStub");
        var ghostDir = Path.Combine(mods, "GhostMod");
        var patchDir = Path.Combine(mods, "PatchMod");
        var cleanDir = Path.Combine(mods, "CleanMod");
        foreach (var d in new[] { vanillaDir, ghostDir, patchDir, cleanDir }) Directory.CreateDirectory(d);

        var sky = new SkyrimMod(new ModKey("Skyrim", ModType.Master), SkyrimRelease.SkyrimSE);
        var skyRace = sky.Races.AddNew(); skyRace.EditorID = "HcMmVanillaRace";
        var skyPath = Path.Combine(vanillaDir, "Skyrim.esm");
        sky.BeginWrite.ToPath(skyPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        var ghost = new SkyrimMod(new ModKey("HcMmGhost", ModType.Master), SkyrimRelease.SkyrimSE);
        var ghostRace = ghost.Races.AddNew(); ghostRace.EditorID = "HcMmGhostRace";
        ghost.BeginWrite.ToPath(Path.Combine(ghostDir, GhostName)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        var absent = new SkyrimMod(new ModKey("HcMmAbsent", ModType.Master), SkyrimRelease.SkyrimSE);
        var absentRace = absent.Races.AddNew(); absentRace.EditorID = "HcMmAbsentRace";
        absent.BeginWrite.ToPath(Path.Combine(outside, AbsentName)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        // One NPC per master, so Mutagen writes all three into the patch's master table.
        var patch = new SkyrimMod(new ModKey("HcMmPatch", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var pSky = patch.Npcs.AddNew(); pSky.EditorID = "HcMmPatchSkyNpc"; pSky.Race.SetTo(skyRace.FormKey);
        var pGhost = patch.Npcs.AddNew(); pGhost.EditorID = "HcMmPatchGhostNpc"; pGhost.Race.SetTo(ghostRace.FormKey);
        var pAbsent = patch.Npcs.AddNew(); pAbsent.EditorID = "HcMmPatchAbsentNpc"; pAbsent.Race.SetTo(absentRace.FormKey);
        patch.BeginWrite.ToPath(Path.Combine(patchDir, PatchName))
             .WithLoadOrder(new ISkyrimModGetter[] { sky, ghost, absent }).Write();

        var clean = new SkyrimMod(new ModKey("HcMmClean", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var cNpc = clean.Npcs.AddNew(); cNpc.EditorID = "HcMmCleanNpc"; cNpc.Race.SetTo(skyRace.FormKey);
        clean.BeginWrite.ToPath(Path.Combine(cleanDir, CleanName)).WithLoadOrder(new ISkyrimModGetter[] { sky }).Write();

        File.WriteAllText(Path.Combine(profileDir, "loadorder.txt"),
            "# header\r\nSkyrim.esm\r\n" + PatchName + "\r\n" + CleanName + "\r\n");
        File.WriteAllText(Path.Combine(profileDir, "plugins.txt"), "*" + PatchName + "\r\n*" + CleanName + "\r\n");
        // GhostMod is switched OFF in MO2's left pane — the file is installed, the order does not load it.
        File.WriteAllText(Path.Combine(profileDir, "modlist.txt"),
            "# header\r\n+CleanMod\r\n+PatchMod\r\n-GhostMod\r\n+VanillaStub\r\n");

        var store = new UserConfigStore(Path.Combine(Root, "houseCARL.user.json"));
        Svc = LoadOrderService.WithInstance(Instance, 0, store);
    }

    public void Dispose()
    {
        Svc.Dispose();
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}

/// <summary>The world, built once for the class. Every test below is read-only over it.</summary>
public sealed class CheckMasterRemedyFixture : IDisposable
{
    public CheckMasterRemedyWorld W { get; } = new();
    public void Dispose() => W.Dispose();
}

/// <summary>
/// <c>housecarl_check findings=["missing_masters"]</c> names, per unsatisfied master, WHICH shortfall it is —
/// so the remedy the caller reads is the one that will work.
///
/// <para>The finding class carries one class, one count and one union list; the split is a render fact. A
/// master present only in a disabled mod wants ENABLE, and one that is not in the install at all wants
/// INSTALL.</para>
///
/// <para>Driven through <see cref="CheckTools.CheckTool"/> — the method the MCP server publishes and binds
/// arguments into — over a synthetic MO2 instance, so the render is reached the way a caller reaches it. The
/// stdio fixture cannot carry this: <c>ServerFixture</c> runs an UNCONFIGURED server, so no wire-driven call
/// reaches a sweep at all, and pointing that shared fixture at an instance would change it for everything
/// else.</para>
///
/// <para>Every expected sentence is a fixture-known value — the fixture decides which master is in which
/// standing, and each test names that master in the line it must appear on, so a remedy printed against the
/// wrong master fails on the name rather than merely on the wording.</para>
/// </summary>
[Trait("tier", "integration")]
public sealed class CheckMasterRemedyTests : IClassFixture<CheckMasterRemedyFixture>
{
    readonly CheckMasterRemedyWorld W;
    public CheckMasterRemedyTests(CheckMasterRemedyFixture f) => W = f.W;

    LoadOrderService Svc => W.Svc;

    const string InstallRemedy = "missing master(s) NOT installed anywhere in the MO2 install: ";
    const string EnableRemedy = "missing master(s) installed but NOT ACTIVE in the load order (in a disabled mod, or unchecked): ";
    const string UnionRemedy = "install/enable it";

    /// <summary>The masters sweep over the plugin declaring one master of each standing.</summary>
    string PatchSweep() => CheckTools.CheckTool(Svc, plugins: new[] { W.PatchName }, findings: new[] { "missing_masters" });

    /// <summary>The line a remedy was printed on, or null — read as a LINE so a name matched anywhere in the
    /// response cannot stand in for a name matched on the sentence that carries the remedy.</summary>
    static string? LineWith(string response, string prefix) =>
        response.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal));

    [Fact]
    public void AMasterInstalledNowhereIsNamedOnTheInstallLine_NotOnTheEnableLine()
    {
        var text = PatchSweep();

        var install = LineWith(text, InstallRemedy);
        Assert.NotNull(install);
        Assert.Contains(W.AbsentName, install);
        Assert.Contains("[install them", install);
        Assert.DoesNotContain(W.GhostName, install);
    }

    [Fact]
    public void AMasterSittingInADisabledModIsNamedOnTheEnableLine_NotOnTheInstallLine()
    {
        var text = PatchSweep();

        var enable = LineWith(text, EnableRemedy);
        Assert.NotNull(enable);
        Assert.Contains(W.GhostName, enable);
        Assert.Contains("[enable them", enable);
        Assert.DoesNotContain(W.AbsentName, enable);
    }

    /// <summary>The union remedy is what this replaces; a response still carrying it is a response the split
    /// did not reach.</summary>
    [Fact]
    public void TheOldUnionRemedyIsNotPrintedWhereTheSplitWasMade()
    {
        Assert.DoesNotContain(UnionRemedy, PatchSweep());
    }

    /// <summary>The satisfied master is in neither line. Without this the two tests above would pass over a
    /// render that listed every declared master under both remedies.</summary>
    [Fact]
    public void ASatisfiedMasterIsNamedOnNeitherRemedyLine()
    {
        var text = PatchSweep();

        Assert.DoesNotContain("Skyrim.esm", LineWith(text, InstallRemedy)!);
        Assert.DoesNotContain("Skyrim.esm", LineWith(text, EnableRemedy)!);
    }

    /// <summary>The control: a plugin whose declared masters are all satisfied reports no shortfall at all,
    /// in either line. A split that fired unconditionally would show up here.</summary>
    [Fact]
    public void APluginWhoseMastersAreAllSatisfiedGetsNoRemedyLineAtAll()
    {
        var clean = CheckTools.CheckTool(Svc, plugins: new[] { W.CleanName }, findings: new[] { "missing_masters" });

        Assert.Null(LineWith(clean, InstallRemedy));
        Assert.Null(LineWith(clean, EnableRemedy));
        // The head still REPORTS the class — it looked, and found none. "0 missing master(s)" there is the
        // proof this sweep ran the master read at all, so the two nulls above are an answer, not a skip.
        Assert.Contains("0 missing master(s)", clean);
    }

    /// <summary>The finding CLASS did not change: one class, and its count is still the union of the two
    /// standings. The remedy split is a render fact, not a second finding.</summary>
    [Fact]
    public void TheFindingClassStillReportsBothShortfallsAsOneCount()
    {
        var r = Svc.CheckErrors(new[] { W.PatchName }, 1000, findings: new[] { "missing_masters" });

        Assert.Null(r.Error);
        Assert.Equal(2, r.TotalMissingMasters);
        var p = Assert.Single(r.Reports);
        Assert.Equal(new[] { W.AbsentName, W.GhostName }.OrderBy(x => x, StringComparer.OrdinalIgnoreCase),
                     p.MissingMasters.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>The split is filled in where the MO2 composition lives, and it is the SUBSET the render reads
    /// — not a second list that could disagree with the count above.</summary>
    [Fact]
    public void TheServiceClassifiesTheUnsatisfiedMastersAndTheSubsetIsExactlyTheInstalledOne()
    {
        var r = Svc.CheckErrors(new[] { W.PatchName }, 1000, findings: new[] { "missing_masters" });
        var p = Assert.Single(r.Reports);

        Assert.NotNull(p.InstalledButInactiveMasters);
        Assert.Equal(new[] { W.GhostName }, p.InstalledButInactiveMasters);
    }

    /// <summary>An unclassified report — what a caller of the core sweep alone produces — still gets a remedy,
    /// and it is the union one. Null there means "the split was not made", and claiming every master is
    /// uninstalled would be a false remedy rather than a missing one.</summary>
    [Fact]
    public void AnUnclassifiedReportKeepsTheUnionRemedy_NullIsNotAnEmptySubset()
    {
        var unclassified = new PluginErrors(W.PatchName, Array.Empty<DanglingRef>(),
                                            new[] { W.AbsentName, W.GhostName }, 0, Array.Empty<string>(), null);

        var section = Wire.ComposeErrorSection(unclassified);

        Assert.Contains(UnionRemedy, section);
        Assert.DoesNotContain(InstallRemedy, section);
        Assert.DoesNotContain(EnableRemedy, section);
    }
}
