using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Xunit;
using static HousecarlMcpTests.WriteSurfaceReads;

namespace HousecarlMcpTests;

/// <summary>The CopyFrom lane decides in-order vs off-order from the engine's own view and from file identity, never
/// from a pre-fetched body or a filename. Migrated from write-surface-guard's #317 and #321 arms.</summary>
[Trait("tier", "integration")]
public sealed class WriteSurfaceCopyFromTests : IClassFixture<WriteSurfaceWorld>
{
    readonly WriteSurfaceWorld _w;
    public WriteSurfaceCopyFromTests(WriteSurfaceWorld w) => _w = w;

    // probe: CopyFrom: a pre-fetched body keyed to an ACTIVE source is IGNORED — the in-order arm resolves it (#317)
    // 10 = the named source; 20 = no copy happened; 77 = the stale pre-fetched body won.
    [Fact]
    public void PrefetchedBodyForAnActiveSourceIsIgnored()
    {
        var dir = Path.Combine(_w.Root, "cfview");
        Directory.CreateDirectory(dir);
        var mKey = new ModKey("HcCfMaster", ModType.Master);
        var rKey = new ModKey("HcCfRepl", ModType.Plugin);
        var mPath = Path.Combine(dir, mKey.FileName.String);
        var rPath = Path.Combine(dir, rKey.FileName.String);

        var m = new SkyrimMod(mKey, SkyrimRelease.SkyrimSE);
        var subject = m.Weapons.AddNew();
        subject.EditorID = "CfSubject";
        subject.BasicStats = new WeaponBasicStats { Damage = 10 };
        m.BeginWrite.ToPath(mPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        var r = new SkyrimMod(rKey, SkyrimRelease.SkyrimSE);
        ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(r, subject)).BasicStats = new WeaponBasicStats { Damage = 20 };
        r.BeginWrite.ToPath(rPath).WithLoadOrder(new ISkyrimModGetter[] { m }).Write();

        var decoy = new SkyrimMod(mKey, SkyrimRelease.SkyrimSE);
        ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(decoy, subject)).BasicStats = new WeaponBasicStats { Damage = 77 };
        IMajorRecordGetter decoyBody = decoy.Weapons.First();

        using var resolver = LoadOrderResolver.Build(new[] { mPath, rPath });
        var edit = new WritePatchBuilder.PatchEdit
        {
            Target = subject.FormKey,
            Path = new[] { "BasicStats", "Damage" },
            Verb = "CopyFrom",
            FromPlugin = mKey.FileName.String,
        };
        var outPath = Path.Combine(dir, "HcCfPatch.esp");
        var o = WritePatchBuilder.Apply(resolver, TestCorpus.Rulebook, new[] { edit }, outPath, extend: false, fullReadback: false,
            copyFromSources: new Dictionary<WritePatchBuilder.PatchEdit, IMajorRecordGetter> { [edit] = decoyBody });
        Assert.True(o.Success, o.Error);
        Assert.Equal((ushort)10, DamageIn(outPath, subject.FormKey));
    }

    // probe: #321: a from_source= PATH to an ACTIVE plugin is refused as IN-ORDER (not as an off-order file read)
    // probe: …and the refusal speaks the order's vocabulary — the plugin NAME, not the path it was addressed by
    [Fact]
    public void PathToAnActivePluginIsReadInOrder()
    {
        var r = ApplyTools.Apply(_w.Svc, patch: "W2Cf321Miss",
            ops: Json(CopyOps(_w.MasterOnlyFid, "BasicStats.Damage", _w.ReplacerPath)));
        Assert.Contains("is in the load order but does NOT define or override", r);
        Assert.Contains(_w.ReplacerName, r, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_w.ReplacerPath, r, StringComparison.OrdinalIgnoreCase);
    }

    // probe: …and the copy itself still lands from the named plugin's own version (10, not the winner's 99)
    [Fact]
    public void CopyByActivePathLandsTheNamedPluginsValue()
    {
        var r = ApplyTools.Apply(_w.Svc, patch: "W2Cf321Live",
            ops: Json(CopyOps(_w.SubjectFid, "BasicStats.Damage", _w.MasterPath)));
        var path = _w.ArtifactPathFrom(r);
        Assert.NotNull(path);
        Assert.Equal((ushort)10, DamageIn(path!, _w.SubjectKey));
    }

    // probe: a path to a DIFFERENT file sharing the active plugin's NAME still copies OFF-ORDER (77, its own body)
    [Fact]
    public void SameNamedDifferentFileCopiesOffOrder()
    {
        var shadowDir = Path.Combine(_w.Root, "cf321-shadow");
        Directory.CreateDirectory(shadowDir);
        var shadow = Path.Combine(shadowDir, _w.MasterName);
        File.Copy(_w.MasterPath, shadow, overwrite: true);
        BumpDamage(shadow, 77);
        var r = ApplyTools.Apply(_w.Svc, patch: "W2Cf321Shadow",
            ops: Json(CopyOps(_w.SubjectFid, "BasicStats.Damage", shadow)));
        var path = _w.ArtifactPathFrom(r);
        Assert.NotNull(path);
        Assert.Equal((ushort)77, DamageIn(path!, _w.SubjectKey));
    }
}
