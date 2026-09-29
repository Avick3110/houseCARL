using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A plugin outside the merge that lists the donor as a master and references none of its records reaches
/// the outcome and the rendered report (the former merge-service-guard arm DECLARER: the service-to-render join).</summary>
[Trait("tier", "integration")]
public sealed class MergeServiceDeclarerTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "hc-merge-service-decl-" + Guid.NewGuid().ToString("N"));
    static readonly ModKey DonorKey = new("HcMgDeclDonor", ModType.Plugin);
    static readonly ModKey DeclKey = new("HcMgDeclOnly", ModType.Plugin);

    LoadOrderService Build()
    {
        var instance = Path.Combine(_root, "instance");
        var profiles = Path.Combine(instance, "profiles", "Default");
        var mods = Path.Combine(instance, "mods");
        foreach (var d in new[] { profiles, mods, Path.Combine(_root, "game", "Data") }) Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(_root, "game").Replace(@"\", @"\\") + ")\r\n");

        var donorDir = Path.Combine(mods, "DeclDonorMod"); Directory.CreateDirectory(donorDir);
        var donorPath = Path.Combine(donorDir, DonorKey.FileName.String);
        var donor = new SkyrimMod(DonorKey, SkyrimRelease.SkyrimSE);
        donor.Weapons.Add(new Weapon(new FormKey(DonorKey, 0xA01), SkyrimRelease.SkyrimSE) { EditorID = "HcMgDeclDonorWeap" });
        donor.BeginWrite.ToPath(donorPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        // Its own record, no link into the donor, the donor in its master table anyway.
        var declDir = Path.Combine(mods, "DeclOnlyMod"); Directory.CreateDirectory(declDir);
        using (var donorOv = SkyrimMod.CreateFromBinaryOverlay(donorPath, SkyrimRelease.SkyrimSE))
        {
            var m = new SkyrimMod(DeclKey, SkyrimRelease.SkyrimSE);
            m.Weapons.Add(new Weapon(new FormKey(DeclKey, 0xB01), SkyrimRelease.SkyrimSE) { EditorID = "HcMgDeclOwnWeap" });
            m.BeginWrite.ToPath(Path.Combine(declDir, DeclKey.FileName.String))
                .WithLoadOrder(new ISkyrimModGetter[] { donorOv }).WithExtraIncludedMasters(DonorKey).Write();
        }

        File.WriteAllText(Path.Combine(profiles, "loadorder.txt"), $"# header\r\n{DonorKey.FileName}\r\n{DeclKey.FileName}\r\n");
        File.WriteAllText(Path.Combine(profiles, "plugins.txt"), $"*{DonorKey.FileName}\r\n*{DeclKey.FileName}\r\n");
        File.WriteAllText(Path.Combine(profiles, "modlist.txt"), "# header\r\n+DeclOnlyMod\r\n+DeclDonorMod\r\n");

        var svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(_root, "houseCARL.user.json")));
        svc.Stats();
        return svc;
    }

    // DECLARER a real declarer-only dependent reaches the rendered merge report, named with what it declares
    [Fact]
    public void ADeclarerOnlyDependentReachesTheOutcomeAndTheRenderedReport()
    {
        using var svc = Build();
        var o = svc.MergePlugins(new[] { DonorKey.FileName.String }, "HcMgDeclOut.esp");
        Assert.True(o.Success, o.Error);

        var declarer = Assert.Single(o.MasterDeclarers);
        Assert.Equal(DeclKey.FileName.String, declarer.Plugin, ignoreCase: true);
        Assert.DoesNotContain(DeclKey.FileName.String, o.ExternalPlugins, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(DeclKey.FileName.String, o.ExternalOverriders, StringComparer.OrdinalIgnoreCase);

        var r = WriteTools.RenderMerge(o);
        Assert.Contains("DECLARE a donor as a MASTER", r);
        Assert.Contains(DeclKey.FileName.String, r);
        Assert.Contains("declares " + DonorKey.FileName.String, r);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
