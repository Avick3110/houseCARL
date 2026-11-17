using System.Text;
using System.Text.Json;
using HousecarlGenerator;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The config audit leaves SkyPatcher's INIs to the layer tool and keeps every other config, and a config
/// only inside a BSA says so (spec R44; DECISIONS 2026-11-17 (1), Q5 (c)).</summary>
[Trait("tier", "integration")]
public sealed class SkseConfigSkyPatcherSplitTests : IDisposable
{
    const string Upper = @"SKSE\Plugins\SkyPatcher\weapon\upper.ini";
    const string Lower = @"SKSE\Plugins\skypatcher\armor\lower.ini";
    const string Switches = @"SKSE\Plugins\skypatcher.ini";   // not the source's spelling, so its match is case-blind
    const string PackedSp = @"skse\plugins\skypatcher\npc\packed.ini";
    const string NotIni = @"SKSE\Plugins\SkyPatcher\notes.json";
    const string IniInName = @"SKSE\Plugins\SkyPatcher\weapon\named.ini.json";   // '.ini' in the name, not at its end
    const string RootLevel = @"SKSE\Plugins\SkyPatcher\rootlevel.ini";            // in the root, no type subfolder
    const string LookAlike = @"SKSE\Plugins\SkyPatcherExtra\keep.ini";
    const string LooseKeep = @"SKSE\Plugins\Foo\keep.ini";
    const string PackedKeep = @"skse\plugins\inventoryinjector\packed.json";
    const string BothKeep = @"skse\plugins\bar\both.ini";

    static readonly byte[] Token = Encoding.UTF8.GetBytes("form=Dummy.esp|800\r\n");

    readonly PlaceInstance _p = new();
    readonly SkseConfigAuditData _audit;

    public SkseConfigSkyPatcherSplitTests()
    {
        foreach (var rel in new[] { Upper, Switches, NotIni, IniInName, RootLevel, LookAlike, LooseKeep, BothKeep })
            PlaceInstance.Loose(_p.Mod("Other"), rel, Token);
        PlaceInstance.Loose(_p.Mod("Lower"), Lower, Token);
        var folders = new[] { PackedSp, PackedKeep, BothKeep }
            .GroupBy(p => Path.GetDirectoryName(p)!, StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.Key, g.Select(p => (Path.GetFileName(p), Token)).ToArray())).ToArray();
        File.WriteAllBytes(Path.Combine(_p.Mod("Arch"), "Dummy.bsa"),
            BsaBuilder.Build(105, BsaBuilder.HasFolderNames | BsaBuilder.HasFileNames, folders));
        _p.ProfileWithDummy("Arch", "+Other", "+Lower", "+Arch");
        _audit = _p.Open().SkseConfigAudit();
    }

    public void Dispose() => _p.Dispose();

    bool Audited(string rel) => _audit.Files.Any(f => f.RelPath.Equals(rel, StringComparison.OrdinalIgnoreCase));

    SkseConfigFileAudit File1(string rel) => _audit.Files.Single(f => f.RelPath.Equals(rel, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void SkyPatcherFolderInisLeaveUnderEitherSpelling()
    {
        Assert.False(Audited(Upper));
        Assert.False(Audited(Lower));
    }

    [Fact]
    public void SkyPatcherIniLeaves() => Assert.False(Audited(Switches));

    [Fact]
    public void ASkyPatcherIniInsideABsaLeaves() => Assert.False(Audited(PackedSp));

    [Fact]
    public void AnIniDirectlyInTheSkyPatcherRootLeaves() => Assert.False(Audited(RootLevel));

    [Fact]
    public void ASkyPatcherIniOnlyInsideABsaLeaves()
    {
        using var p = new PlaceInstance();
        File.WriteAllBytes(Path.Combine(p.Mod("Arch"), "Dummy.bsa"), BsaBuilder.Build(105,
            BsaBuilder.HasFolderNames | BsaBuilder.HasFileNames,
            new[] { (@"skse\plugins", new[] { ("skypatcher.ini", Token), ("other.ini", Token) }) }));
        p.ProfileWithDummy("Arch", "+Arch");
        var audit = p.Open().SkseConfigAudit();
        var only = Assert.Single(audit.Files);   // the packed neighbour stays, so the packed switches file is what left
        Assert.Equal(@"skse\plugins\other.ini", only.RelPath, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ANonIniUnderTheSkyPatcherFolderStays() => Assert.True(Audited(NotIni));

    [Fact]
    public void ANameHoldingIniAnywhereUnderTheSkyPatcherFolderLeaves() => Assert.False(Audited(IniInName));

    [Fact]
    public void AFolderOnlyNamedLikeSkyPatchersStays() => Assert.True(Audited(LookAlike));

    [Fact]
    public void EveryOtherConfigStaysWithItsReferences()
    {
        Assert.Equal(5, _audit.ConfigCount);
        Assert.All(new[] { NotIni, LookAlike, LooseKeep, PackedKeep, BothKeep },
            rel => Assert.Equal(SkseRefVerdict.Ok, Assert.Single(File1(rel).Refs).Verdict));
    }

    [Fact]
    public void AConfigOnlyInsideABsaIsMarkedSoAndALooseOneIsNot()
    {
        Assert.True(File1(PackedKeep).ArchiveOnly);
        Assert.False(File1(LooseKeep).ArchiveOnly);
        Assert.False(File1(BothKeep).ArchiveOnly);   // a loose copy beside the archive one
    }

    [Fact]
    public void TheFilteredAuditSaysABsaOnlyConfigMayNeverBeReadAndListsItsReferences()
    {
        var txt = SkseConfigAuditWire.Render(_audit, "packed.json", 40_000);
        Assert.Contains("\n  [!] only inside a BSA: most SKSE plugins read loose files only, so it may never be read\n", txt);
        Assert.Contains("[OK] 'Dummy.esp|800'", txt);
        Assert.DoesNotContain(SkseConfigAuditWire.ArchiveOnlyNote, SkseConfigAuditWire.Render(_audit, "keep.ini", 40_000));
    }

    [Fact]
    public void TheWholeAuditNamesEachBsaOnlyConfig()
    {
        var txt = SkseConfigAuditWire.Render(_audit, null, 40_000);
        // The section whole: its heading, the one row once, then the accounted-for block.
        Assert.Contains("\nconfigs only inside a BSA (1): most SKSE plugins read loose files only, so they may never be read:\n" +
                        "  - " + PackedKeep + "  [← Dummy.bsa]\n\naccounted for:", txt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheJsonRowOfABsaOnlyConfigCarriesTheNoteAndNoOtherRowDoes()
    {
        using var doc = JsonDocument.Parse(SkseConfigAuditWire.RenderJson(_audit, null, 1_000_000));
        var notes = doc.RootElement.GetProperty("files").EnumerateArray()
            .Where(r => r.TryGetProperty("note", out _))
            .Select(r => (Path: r.GetProperty("rel_path").GetString(), Note: r.GetProperty("note").GetString())).ToList();
        var only = Assert.Single(notes);
        Assert.Equal(PackedKeep, only.Path, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("only inside a BSA: most SKSE plugins read loose files only, so it may never be read", only.Note);
    }
}
