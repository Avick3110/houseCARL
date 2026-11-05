using HousecarlCore;
using HousecarlGenerator;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A scripts sweep reads each archive's file table once and each .pex once: two child scripts and the parent
/// they share, all packed in one BSA, cost one table read and three .pex loads. A parent the two children spell
/// differently is loaded once per spelling, so each finding keeps its own child's spelling.</summary>
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

    string WriteBsa(params (string Name, byte[] Bytes)[] scripts)
    {
        Directory.CreateDirectory(_root);
        var bsa = Path.Combine(_root, "HcPo.bsa");
        File.WriteAllBytes(bsa, BsaBuilder.Build(105, BsaBuilder.HasFolderNames | BsaBuilder.HasFileNames, new[]
        {
            ("scripts", scripts.Select(s => (s.Name.ToLowerInvariant() + ".pex", s.Bytes)).ToArray()),
        }));
        return bsa;
    }

    /// <summary>A script whose .pex a mod holds only as .mohidden: its property is unverifiable, and the reason says MO2 hides the .pex (#1281).</summary>
    [Fact]
    public void APexAModHidesIsSaidHiddenInTheUnverifiableReason()
    {
        var mods = Path.Combine(_root, "mods");
        Directory.CreateDirectory(Path.Combine(mods, "HidMod", "Scripts"));
        File.WriteAllBytes(Path.Combine(mods, "HidMod", "Scripts", "HcHid.pex.mohidden"), Pex("HcHid", null, "HidThing"));
        var mod = new SkyrimMod(new ModKey("HcHid", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var vmad = CheckMergeFixture.Vmad("HcHid");
        vmad.Scripts[0].Properties.Add(new ScriptObjectProperty { Name = "HidThing" });
        mod.Weapons.AddNew().VirtualMachineAdapter = vmad;
        var plugin = Path.Combine(_root, "HcHid.esp");
        mod.BeginWrite.ToPath(plugin).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        using var resolver = LoadOrderResolver.Build(new[] { plugin });
        using var assets = AssetResolver.Build("", mods, _root, new[] { "HidMod" }, Array.Empty<ActiveArchive>());
        var result = ScriptPropertyCheck.Run(resolver, assets, null, 1000);

        var json = System.Text.Json.JsonSerializer.Serialize(result.Reports, new System.Text.Json.JsonSerializerOptions
            { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        Assert.True(json.Contains("HcHid.pex' is hidden by MO2 (skipped by MO2's VFS, by the '.mohidden' suffix) in 'HidMod', as"), json);
    }

    ScriptPropertyCheck.PexReads Sweep(out ScriptCheckResult result, string parentOfB = Base)
    {
        var bsa = WriteBsa((Base, Pex(Base, null, "BaseThing")), (ChildA, Pex(ChildA, Base, "AThing")),
                           (ChildB, Pex(ChildB, parentOfB, "BThing")));

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
        result = ScriptPropertyCheck.Run(reads, resolver, resolver.Capture(), av, null, 1000);
        return reads;
    }

    [Fact]
    public void SeveralScriptsInOneArchiveReadItsTableOnce()
    {
        var reads = Sweep(out var result);
        Assert.Equal(6, result.TotalUnbound);   // each of 3 records leaves its own and its parent's property unbound
        Assert.Equal(1, reads.Av.ArchiveTablesRead);
    }

    [Fact]
    public void SeveralScriptsInOneFolderWalkItsFileListOnce()
    {
        var reads = Sweep(out _);
        Assert.Equal(1, reads.Av.FolderListsRead);
    }

    [Fact]
    public void ANamespacedScriptWalksItsOwnFolderOnce()
    {
        Directory.CreateDirectory(_root);
        var bsa = Path.Combine(_root, "HcPo.bsa");
        File.WriteAllBytes(bsa, BsaBuilder.Build(105, BsaBuilder.HasFolderNames | BsaBuilder.HasFileNames, new[]
        {
            ("scripts", new[] { (Base.ToLowerInvariant() + ".pex", Pex(Base, null, "BaseThing")) }),
            (@"scripts\ns", new[] { ("hcpons.pex", Pex("HcPoNs", null, "NsThing")) }),
        }));
        using var assets = AssetResolver.Build("", "", _root, Array.Empty<string>(),
            new[] { new ActiveArchive(bsa, "HcPo.esp", 0) });
        var reads = new ScriptPropertyCheck.PexReads(assets.Capture());
        Assert.Null(reads.Load(Base).Reason);
        Assert.Null(reads.Load("Ns:HcPoNs").Reason);
        Assert.Equal(2, reads.Av.FolderListsRead);
    }

    [Fact]
    public void FoldersTheArchiveLacksShareOneWholeArchiveTable()
    {
        var bsa = WriteBsa((Base, Pex(Base, null, "BaseThing")));
        using var assets = AssetResolver.Build("", "", _root, Array.Empty<string>(),
            new[] { new ActiveArchive(bsa, "HcPo.esp", 0) });
        var reads = new ScriptPropertyCheck.PexReads(assets.Capture());
        Assert.Null(reads.Av.ReadEntry(bsa, @"Scripts\Gone\HcPoGone.pex"));
        Assert.Null(reads.Av.ReadEntry(bsa, @"Meshes\HcPoGone.nif"));
        Assert.Equal(1, reads.Av.FolderListsRead);
    }

    [Fact]
    public void AParentSharedByTwoChildrenLoadsOnce()
    {
        var reads = Sweep(out var result);
        Assert.Contains(result.Reports, r => r.Unbound.Any(u => u.DeclaringScript == Base && u.Script == ChildB));
        Assert.Equal(3, reads.PexLoads);
    }

    [Fact]
    public void AParentSpelledTwoWaysKeepsEachSpelling()
    {
        var upper = Base.ToUpperInvariant();
        var reads = Sweep(out var result, upper);
        var declaring = result.Reports.SelectMany(r => r.Unbound)
            .Where(u => u.PropertyName == "BaseThing").Select(u => (u.Script, u.DeclaringScript)).Distinct().ToList();
        Assert.Contains((ChildA, Base), declaring);
        Assert.Contains((ChildB, upper), declaring);
        Assert.Equal(4, reads.PexLoads);
    }
}
