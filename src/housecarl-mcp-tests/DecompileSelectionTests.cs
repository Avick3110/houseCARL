using HousecarlGenerator;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary><c>housecarl_decompile_script</c> on the shared selection (#1287): Data paths, <c>under=</c> and
/// <c>provider=</c> pick scripts as asset_status picks files, each read loose or out of its archive with no extract
/// step; <c>source_provider=</c> reads a copy that does not win; a file on disk still decompiles beside them; a bare
/// name and the retired names are refused in one sentence. The world: the mod Arch ships Dummy.bsa (bound to its
/// Dummy.esp) holding two scripts, a loose script, and a copy of HcDsShared that the loose copy in Fix wins over;
/// the disabled mod Off ships one script.</summary>
[Trait("tier", "integration")]
public sealed class DecompileSelectionTests : IDisposable
{
    const string Arch1 = @"scripts\hcdsarch1.pex", ArchBase = @"scripts\hcdsarchbase.pex";
    const string LooseRel = @"scripts\hcdsloose.pex", Shared = @"scripts\hcdsshared.pex", OffRel = @"scripts\hcdsoff.pex";

    readonly PlaceInstance _p = new();
    readonly string _out;
    readonly LoadOrderService _svc;

    public DecompileSelectionTests()
    {
        _out = Path.Combine(_p.Root, "out");
        var bsa = Path.Combine(_p.Mod("Arch"), "Dummy.bsa");
        File.WriteAllBytes(bsa, BsaBuilder.Build(105, BsaBuilder.HasFolderNames | BsaBuilder.HasFileNames, new[]
        {
            ("scripts", new[]
            {
                ("hcdsarch1.pex", Pex("HcDsArch1", "HcDsArchBase", "ArchOne")),
                ("hcdsarchbase.pex", Pex("HcDsArchBase", null, "ArchBaseThing")),
                ("hcdsshared.pex", Pex("HcDsShared", null, "ArchCopyThing")),
            }),
        }));
        PlaceInstance.Loose(_p.Mod("Arch"), LooseRel, Pex("HcDsLoose", null, "LooseThing"));
        PlaceInstance.Loose(_p.Mod("Fix"), Shared, Pex("HcDsShared", null, "FixCopyThing"));
        PlaceInstance.Loose(_p.Mod("Off"), OffRel, Pex("HcDsOff", null, "OffThing"));
        _p.ProfileWithDummy("Arch", "+Fix", "+Arch", "-Off");
        _svc = _p.Open();
    }

    public void Dispose() => _p.Dispose();

    /// <summary>A one-object .pex named <paramref name="name"/> declaring one Spell property, as bytes.</summary>
    byte[] Pex(string name, string? parent, string property)
    {
        var dir = Path.Combine(_p.Root, "pex");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name + "-" + property + ".pex");
        PexWriter.WritePex(path, name, parent, PexWriter.AutoObj(property, "Spell"));
        return File.ReadAllBytes(path);
    }

    string Psc(string name) => File.ReadAllText(Path.Combine(_out, name + ".psc"));

    [Fact]
    public void AProviderDecompilesItsLooseAndArchivedScriptsInOneCall()
    {
        var r = DecompileTools.DecompileScript(_svc, provider: "Arch", out_path: _out);

        Assert.Contains("ArchOne", Psc("HcDsArch1"));                       // out of Dummy.bsa
        Assert.Contains("ArchBaseThing", Psc("HcDsArchBase"));
        Assert.Contains("LooseThing", Psc("HcDsLoose"));                    // loose in Arch
        Assert.Contains("FixCopyThing", Psc("HcDsShared"));                 // provider= picks which; the winner is read
        Assert.Contains("1 script(s) provider 'Arch' ships are won by another provider", r);
        Assert.Contains("4 OK", r);
        Assert.Contains(@"OK scripts\hcdsarch1.pex ← ""Dummy.bsa"" (BSA)", r);
        // Dummy.esp and Dummy.bsa are files Arch provides, and not scripts.
        Assert.Contains("2 file(s) the selectors matched are not .pex scripts", r);
        Assert.Contains("[accounting] total=4 rendered=4", r);
    }

    [Fact]
    public void UnderSelectsScriptsAcrossLooseAndArchives()
    {
        var r = DecompileTools.DecompileScript(_svc, under: new[] { "scripts/hcds*.pex" }, out_path: _out);

        Assert.Contains("4 OK", r);
        Assert.True(File.Exists(Path.Combine(_out, "HcDsArch1.psc")), r);
        Assert.True(File.Exists(Path.Combine(_out, "HcDsLoose.psc")), r);
        Assert.False(File.Exists(Path.Combine(_out, "HcDsOff.psc")), r);  // a disabled mod is not in the selection
    }

    [Fact]
    public void SourceProviderReadsTheCopyThatDoesNotWin()
    {
        var r = DecompileTools.DecompileScript(_svc, paths: new[] { "scripts/hcdsshared.pex" }, source_provider: "Arch", out_path: _out);

        Assert.Contains("ArchCopyThing", Psc("HcDsShared"));
        Assert.Contains(@"not the winning copy (""Fix"" (loose) wins)", r);
    }

    [Fact]
    public void SourceProviderReachesADisabledModsScript()
    {
        var r = DecompileTools.DecompileScript(_svc, paths: new[] { OffRel }, source_provider: "Off", out_path: _out);

        Assert.Contains("OffThing", Psc("HcDsOff"));
        Assert.Contains("(not loaded by the game)", r);
    }

    [Fact]
    public void AFileOnDiskDecompilesBesideTheSelection()
    {
        var disk = _p.Scratch("HcDsDisk.pex", Pex("HcDsDisk", null, "DiskThing"));

        var r = DecompileTools.DecompileScript(_svc, paths: new[] { disk, LooseRel }, out_path: _out);

        Assert.Contains("DiskThing", Psc("HcDsDisk"));
        Assert.Contains("LooseThing", Psc("HcDsLoose"));
        Assert.Contains($"OK {disk} ← the file on disk", r);
    }

    [Fact]
    public void ABareScriptNameIsRefusedNamingItsDataPathAndTheSearch()
    {
        var r = DecompileTools.DecompileScript(_svc, paths: new[] { "hcdsloose" }, out_path: _out);

        Assert.StartsWith("error:", r);
        Assert.Contains("paths=[\"scripts/hcdsloose.pex\"]", r);
        Assert.Contains("under=[\"**/hcdsloose*.pex\"]", r);
        Assert.False(Directory.Exists(_out));
    }

    [Fact]
    public void TheRetiredPexParameterIsRefusedInOneSentence()
    {
        var r = DecompileTools.DecompileScript(_svc, pex: Path.Combine(_p.Root, "x.pex"), out_path: _out);

        Assert.StartsWith("error: pex= is now paths=", r);
        Assert.Single(r.Split('\n'));
    }

    [Fact]
    public void TheRetiredAssetPathsParameterIsRefusedInOneSentence()
    {
        var old = System.Text.Json.JsonDocument.Parse("[\"scripts/hcdsloose.pex\"]").RootElement;

        var r = AssetTools.AssetStatus(_svc, asset_paths: old);

        Assert.StartsWith("error: asset_paths= is now paths=", r);
        Assert.Single(r.Split('\n'));
    }

    [Fact]
    public void AssetStatusTakesPaths()
    {
        var r = AssetTools.AssetStatus(_svc, paths: new[] { LooseRel });

        Assert.Contains("hcdsloose.pex", r);
        Assert.DoesNotContain("error:", r);
    }

    [Fact]
    public void AnExistingSourceIsThatScriptsRowAndTheRestLand()
    {
        Directory.CreateDirectory(_out);
        File.WriteAllText(Path.Combine(_out, "HcDsLoose.psc"), "; mine");

        var r = DecompileTools.DecompileScript(_svc, paths: new[] { LooseRel, Arch1 }, out_path: _out);

        Assert.Equal("; mine", Psc("HcDsLoose"));
        Assert.Contains("ArchOne", Psc("HcDsArch1"));
        Assert.Contains(@"ERROR scripts\hcdsloose.pex — '", r);
        Assert.Contains("already exists — houseCARL never overwrites a source file", r);
        Assert.Contains("1 OK, 0 PARTIAL, 1 not decompiled", r);
    }

    [Fact]
    public void LimitAndOffsetChooseWhichScriptsAreDecompiled()
    {
        var r = DecompileTools.DecompileScript(_svc, paths: new[] { LooseRel, Arch1, ArchBase }, limit: 1, offset: 1, out_path: _out);

        Assert.True(File.Exists(Path.Combine(_out, "HcDsArch1.psc")), r);
        Assert.False(File.Exists(Path.Combine(_out, "HcDsLoose.psc")), r);
        Assert.False(File.Exists(Path.Combine(_out, "HcDsArchBase.psc")), r);
        Assert.Contains("in this window of 3 selected", r);
        Assert.Contains("offset=2 for the next page", r);
    }

    [Fact]
    public void AnArchivedScriptsParentIsReadOffTheLoadOrder()
    {
        // Only the child is selected; its parent ships in the same archive, and no .psc of either is anywhere.
        DecompileTools.DecompileScript(_svc, paths: new[] { Arch1 }, out_path: _out);

        Assert.Contains("ScriptName HcDsArch1 extends HcDsArchBase", Psc("HcDsArch1"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ACallReadsEachArchiveOnce()
    {
        var assets = ((ILoadOrderHost)_svc).Assets;
        var before = assets.ArchiveTableReadCount;

        DecompileTools.DecompileScript(_svc, provider: "Arch", out_path: _out);

        Assert.Equal(1, assets.ArchiveTableReadCount - before);
    }

    [Fact]
    public void ToFileIsRefusedNamingTheOutputFolder()
    {
        var r = DecompileTools.DecompileScript(_svc, provider: "Arch", to_file: Path.Combine(_p.Root, "x.jsonl"));

        Assert.StartsWith("error: to_file=", r);
        Assert.Contains("out_path=", r);
    }

    [Fact]
    public void JsonCarriesTheRowsAndTheAccounting()
    {
        var r = DecompileTools.DecompileScript(_svc, paths: new[] { LooseRel }, format: "json", out_path: _out);

        using var doc = System.Text.Json.JsonDocument.Parse(r);
        var row = doc.RootElement.GetProperty("rows")[0];
        Assert.Equal("ok", row.GetProperty("status").GetString());
        Assert.Equal("Arch", row.GetProperty("provider").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("accounting").GetProperty("total").GetInt32());
    }
}
