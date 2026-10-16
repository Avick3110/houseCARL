using HousecarlCore;
using HousecarlGenerator;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A scripts sweep reads each archive's file table once and each .pex once: two child scripts and the parent
/// they share, all packed in one BSA, cost one table read and three .pex loads.</summary>
[Trait("tier", "integration")]
public sealed class ScriptsReadOnceTests : IDisposable
{
    const string Base = "HcPoBase", ChildA = "HcPoChildA", ChildB = "HcPoChildB";
    readonly string _root = Path.Combine(Path.GetTempPath(), "hc-scripts-read-once-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { /* temp scratch */ } }

    byte[] Pex(string name, string? parent, string prop)
    {
        var path = Path.Combine(_root, name + ".pex");
        PexWriter.WritePex(path, name, parent, PexWriter.AutoObj(prop, "Spell"));
        return File.ReadAllBytes(path);
    }

    ScriptPropertyCheck.PexReads Sweep(out ScriptCheckResult result)
    {
        Directory.CreateDirectory(_root);
        var bsa = Path.Combine(_root, "HcPo.bsa");
        File.WriteAllBytes(bsa, BsaBuilder.Build(105, BsaBuilder.HasFolderNames | BsaBuilder.HasFileNames, new[]
        {
            ("scripts", new[]
            {
                (Base.ToLowerInvariant() + ".pex", Pex(Base, null, "BaseThing")),
                (ChildA.ToLowerInvariant() + ".pex", Pex(ChildA, Base, "AThing")),
                (ChildB.ToLowerInvariant() + ".pex", Pex(ChildB, Base, "BThing")),
            }),
        }));

        var mod = new SkyrimMod(new ModKey("HcPo", ModType.Plugin), SkyrimRelease.SkyrimSE);
        foreach (var script in new[] { ChildA, ChildA, ChildB })
            mod.Weapons.AddNew().VirtualMachineAdapter = CheckMergeFixture.Vmad(script);
        var plugin = Path.Combine(_root, "HcPo.esp");
        mod.BeginWrite.ToPath(plugin).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        using var resolver = LoadOrderResolver.Build(new[] { plugin });
        using var assets = AssetResolver.Build("", "", _root, Array.Empty<string>(),
            new[] { new ActiveArchive(bsa, "HcPo.esp", 0) });
        var av = assets.Capture();
        var reads = new ScriptPropertyCheck.PexReads(av);
        result = ScriptPropertyCheck.Run(resolver, resolver.Capture(), av, null, 1000, reads: reads);
        return reads;
    }

    [Fact]
    public void SeveralScriptsInOneArchiveReadItsTableOnce()
    {
        var reads = Sweep(out var result);
        Assert.Equal(6, result.TotalUnbound);   // each of 3 records leaves its own and its parent's property unbound
        Assert.Equal(1, reads.ArchiveTablesRead);
    }

    [Fact]
    public void AParentSharedByTwoChildrenLoadsOnce()
    {
        var reads = Sweep(out var result);
        Assert.Contains(result.Reports, r => r.Unbound.Any(u => u.DeclaringScript == Base && u.Script == ChildB));
        Assert.Equal(3, reads.PexLoads);
    }
}
