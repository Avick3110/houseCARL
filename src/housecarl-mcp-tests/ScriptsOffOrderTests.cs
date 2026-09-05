using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlMcp;
using Xunit;
using static HousecarlMcpTests.ScriptsFixtures;

namespace HousecarlMcpTests;

/// <summary>The scripts family's OFF-ORDER lane: a plugin that is on disk but not in the active load order is
/// swept from its own file, so a fresh patch's script bindings can be checked before it is enabled (#395).
///
/// <para>Its own world, because the shared <see cref="ScriptsWorld"/> is frozen and has no plugin outside the
/// order. The pending mod is DISABLED in MO2, which is the pre-enable shape: its plugin is locatable on disk and
/// its own loose scripts are outside the VFS, so both halves of the lane's contract are reachable here.</para></summary>
[Trait("tier", "integration")]
public sealed class ScriptsOffOrderTests : IDisposable
{
    // ---- fixture-known names and totals ---------------------------------------------------------------

    /// <summary>The script both plugins attach. Its .pex ships in the ENABLED mod, so it is in the VFS.</summary>
    const string SharedScript = "HcOoShared";

    /// <summary>A script whose .pex ships only inside the DISABLED mod — outside the VFS, so the attachment that
    /// carries it is UNVERIFIABLE rather than clean.</summary>
    const string PendingOnlyScript = "HcOoPendingOnly";

    /// <summary>The object property <see cref="SharedScript"/> declares and no record here binds.</summary>
    const string ObjectProperty = "OoSpell";

    const string ActiveName = "HcOoActive.esp";
    const string PendingName = "HcOoPending.esp";

    /// <summary>Records in the off-order plugin that attach <see cref="SharedScript"/> and bind nothing.</summary>
    const int PendingUnbound = 2;

    readonly string _root;
    readonly LoadOrderService _svc;

    public ScriptsOffOrderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "hc-scripts-offorder-" + Guid.NewGuid().ToString("N"));
        var game = Path.Combine(_root, "game");
        Directory.CreateDirectory(Path.Combine(game, "Data"));

        var instance = Path.Combine(_root, "inst");
        var mods = Path.Combine(instance, "mods");
        var activeDir = Path.Combine(mods, "ActiveMod");
        var pendingDir = Path.Combine(mods, "PendingMod");
        Directory.CreateDirectory(Path.Combine(activeDir, "Scripts"));
        Directory.CreateDirectory(Path.Combine(pendingDir, "Scripts"));

        PexWriter.WritePex(Path.Combine(activeDir, "Scripts", SharedScript + ".pex"), SharedScript, parent: null,
            PexWriter.AutoObj(ObjectProperty, "Spell"));
        PexWriter.WritePex(Path.Combine(pendingDir, "Scripts", PendingOnlyScript + ".pex"), PendingOnlyScript,
            parent: null, PexWriter.AutoObj(ObjectProperty, "Spell"));

        WritePlugin(Path.Combine(activeDir, ActiveName), "HcOoActive",
                    new[] { SharedScript });
        WritePlugin(Path.Combine(pendingDir, PendingName), "HcOoPending",
                    new[] { SharedScript, SharedScript, PendingOnlyScript });

        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + game.Replace(@"\", @"\\") + ")\r\n");
        var prof = Path.Combine(instance, "profiles", "Default");
        Directory.CreateDirectory(prof);
        File.WriteAllText(Path.Combine(prof, "loadorder.txt"), "# header\r\n" + ActiveName + "\r\n");
        File.WriteAllText(Path.Combine(prof, "plugins.txt"), "*" + ActiveName + "\r\n");
        // PendingMod is switched OFF: its plugin is on disk and locatable, its loose scripts are not in the VFS.
        File.WriteAllText(Path.Combine(prof, "modlist.txt"), "# header\r\n-PendingMod\r\n+ActiveMod\r\n");

        _svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(_root, "user.json")));
    }

    static void WritePlugin(string path, string name, IReadOnlyList<string> scriptPerWeapon)
    {
        var mod = new SkyrimMod(new ModKey(name, ModType.Plugin), SkyrimRelease.SkyrimSE);
        for (int i = 0; i < scriptPerWeapon.Count; i++)
        {
            var w = mod.Weapons.AddNew();
            w.EditorID = $"{name}Weap{i:D2}";
            var vmad = new VirtualMachineAdapter();
            vmad.Scripts.Add(new ScriptEntry { Name = scriptPerWeapon[i] });
            w.VirtualMachineAdapter = vmad;
        }
        mod.BeginWrite.ToPath(path).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
    }

    public void Dispose()
    {
        _svc.Dispose();
        try { Directory.Delete(_root, true); } catch { /* temp cleanup best-effort */ }
    }

    // ---- the lane -------------------------------------------------------------------------------------

    [Fact]
    public void APluginOnDiskButNotInTheOrderIsSweptFromItsOwnFile()
    {
        var r = _svc.ValidateScripts(new[] { PendingName }, 1000);

        Assert.Null(r.Error);
        Assert.Equal(new[] { PendingName }, r.OffOrderScanned);
        Assert.Equal(1, r.PluginsScanned);
        Assert.Equal(PendingUnbound, r.TotalUnbound);
        Assert.All(r.Reports, rep => Assert.Equal(PendingName, rep.Plugin));
    }

    [Fact]
    public void AnEntirelyOffOrderScopeIsNotWidenedToTheWholeOrder()
    {
        var scoped = _svc.ValidateScripts(new[] { PendingName }, 1000);
        var whole = _svc.ValidateScripts(null, 1000);

        // The active plugin's own unbound record is in the unscoped sweep and NOT in the off-order one: an empty
        // active subset passed as "no scope" would sweep the whole order instead.
        Assert.Contains(whole.Reports, rep => rep.Plugin == ActiveName);
        Assert.DoesNotContain(scoped.Reports, rep => rep.Plugin == ActiveName);
    }

    [Fact]
    public void AScriptShippedOnlyInsideTheOffOrderModIsUnverifiableRatherThanClean()
    {
        var r = _svc.ValidateScripts(new[] { PendingName }, 1000);

        var unver = r.Reports.SelectMany(rep => rep.Unverifiable).ToList();
        Assert.Contains(unver, u => u.Script == PendingOnlyScript);
        // …and its declared property is not counted as bound or unbound off the back of a .pex nobody read.
        Assert.Equal(PendingUnbound, r.TotalUnbound);
    }

    [Fact]
    public void TheRendersNameTheOffOrderFileAndSayTheStampDoesNotCoverIt()
    {
        var r = _svc.ValidateScripts(new[] { PendingName }, 1000);

        var text = Wire.RenderCheck(new CheckSweep(Sel("scripts"), Scripts: r), 20000);
        Assert.Contains("swept OFF-ORDER (on disk, not in the active load order): " + PendingName, text);
        Assert.Contains("(indexed plugins only — off-order file content is outside the fingerprint)", text);

        var fam = ScriptsFamily(JsonWire.RenderCheck(new CheckSweep(Sel("scripts"), Scripts: r), 20000));
        Assert.Equal(PendingName, fam.GetProperty("off_order_scanned").EnumerateArray().Single().GetString());
        Assert.False(fam.GetProperty("epoch_covers_all_inputs").GetBoolean());
    }

    [Fact]
    public void AnAllIndexedSweepClaimsFullCoverageAndNamesNoOffOrderFile()
    {
        var r = _svc.ValidateScripts(new[] { ActiveName }, 1000);

        Assert.Empty(r.OffOrderScanned ?? Array.Empty<string>());
        var text = Wire.RenderCheck(new CheckSweep(Sel("scripts"), Scripts: r), 20000);
        Assert.DoesNotContain("(indexed plugins only", text);
        var fam = ScriptsFamily(JsonWire.RenderCheck(new CheckSweep(Sel("scripts"), Scripts: r), 20000));
        Assert.True(fam.GetProperty("epoch_covers_all_inputs").GetBoolean());
    }

    [Fact]
    public void ANameThatIsNeitherInTheOrderNorOnDiskRefusesSayingBoth()
    {
        var r = _svc.ValidateScripts(new[] { "HcOoNoSuch.esp" }, 1000);

        Assert.NotNull(r.Error);
        Assert.Contains("plugin not in the load order: HcOoNoSuch.esp", r.Error);
        Assert.Contains("no on-disk copy was found either", r.Error);
    }

    [Fact]
    public void ExcludeJudgesTheOffOrderFileAsPartOfTheScope()
    {
        var emptied = _svc.ValidateScripts(new[] { PendingName }, 1000, exclude: new[] { PendingName });
        Assert.Contains("exclude= removed every plugin this sweep would have covered (1 in scope, all excluded)",
                        emptied.Error);

        var unmatched = _svc.ValidateScripts(new[] { PendingName }, 1000, exclude: new[] { ActiveName });
        Assert.Contains($"exclude= names '{ActiveName}'", unmatched.Error);
    }

    [Fact]
    public void TheMergedSurfaceHandsBothSweptFamiliesTheSameOffOrderScope()
    {
        var text = CheckTools.CheckTool(_svc, plugins: new[] { PendingName },
                                        findings: new[] { "errors", "scripts" });

        Assert.False(text.StartsWith("error", StringComparison.OrdinalIgnoreCase));
        // Both sections name the file they swept off-order — the asymmetry the merged surface used to state.
        Assert.Equal(2, text.Split("swept OFF-ORDER (on disk, not in the active load order): " + PendingName).Length - 1);
        Assert.DoesNotContain("did NOT sweep", text);
    }
}
