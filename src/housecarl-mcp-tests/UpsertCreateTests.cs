using HousecarlCore;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A create re-run into the same patch replaces what the patch itself defines, in place and surfaced; an
/// editorid colliding with a carried override, another type or duplicate residue is refused with the file untouched;
/// and a commit blocked by a foreign handle fails loud with the old bytes intact. Migrated from the upsert-guard probe.</summary>
[Trait("tier", "integration")]
public sealed class UpsertCreateTests : IDisposable
{
    readonly WritePathRig _rig = new();
    readonly string _masterPath;
    readonly FormKey _masterWeapon, _masterKeyword;

    public UpsertCreateTests()
    {
        var m = new SkyrimMod(new ModKey("HcUpsGdMaster", ModType.Master), SkyrimRelease.SkyrimSE);
        var w = m.Weapons.AddNew(); w.EditorID = "HcUpsGdMasterWeap"; w.BasicStats = new WeaponBasicStats { Damage = 10 };
        var k = m.Keywords.AddNew(); k.EditorID = "HcUpsGdMasterKw";
        _masterWeapon = w.FormKey; _masterKeyword = k.FormKey;
        _masterPath = _rig.Write(m);
    }

    WritePatchBuilder.CreateSpec[] KeywordAndWeapon() => new[]
    {
        new WritePatchBuilder.CreateSpec { RecordType = "Keyword", EditorId = "HcUpsGdKw", Edits = Array.Empty<WriteRequest>() },
        new WritePatchBuilder.CreateSpec { RecordType = "Weapon", EditorId = "HcUpsGdWeap", Edits = new[]
        {
            new WriteRequest { RecordType = "Weapon", Path = new[] { "BasicStats", "Damage" }, Verb = "Set", Value = "25" },
            new WriteRequest { RecordType = "Weapon", Path = new[] { "Keywords" }, Verb = "Add", Value = $"{_masterKeyword.ID:X6}:{_masterKeyword.ModKey.FileName}" },
        } },
    };

    static WritePatchBuilder.CreateSpec Bare(string type, string edid) =>
        new() { RecordType = type, EditorId = edid, Edits = Array.Empty<WriteRequest>() };

    WritePatchBuilder.CreateOutcome Create(string path, bool extend, params WritePatchBuilder.CreateSpec[] specs)
        => WritePatchBuilder.CreateRecords(_rig.Order(_masterPath), TestCorpus.Rulebook, specs, path, extend);

    /// <summary>A patch holding the created keyword and weapon, written fresh.</summary>
    string FreshPatch(out WritePatchBuilder.CreateOutcome fresh)
    {
        var path = _rig.Out("HcUpsGuard.esp");
        fresh = Create(path, false, KeywordAndWeapon());
        Assert.True(fresh.Success, fresh.Error);
        return path;
    }

    static uint NextFormId(string path)
    {
        using var ov = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
        return ov.ModHeader.Stats.NextFormID;
    }

    // RERUN: a fresh create replaces nothing
    [Fact]
    public void AFreshCreateReplacesNothing()
    {
        FreshPatch(out var fresh);
        Assert.All(fresh.Created, c => Assert.False(c.ReplacedExisting));
    }

    // RERUN: both flagged REPLACED (a replace is never silent), FormKeys stable
    [Fact]
    public void ARerunFlagsEveryReplaceAndKeepsItsFormKeys()
    {
        var path = FreshPatch(out var fresh);
        var again = Create(path, true, KeywordAndWeapon());
        Assert.True(again.Success, again.Error);
        Assert.Equal(2, again.Created.Count);
        Assert.All(again.Created, c => Assert.True(c.ReplacedExisting));
        Assert.Equal(fresh.Created.Select(c => c.FormKey), again.Created.Select(c => c.FormKey));
    }

    // RERUN: 1/1 copies, list edits applied once, byte-identical, counter unmoved (0x802)
    [Fact]
    public void ARerunLeavesOneCopyOfEachAndTheSameBytes()
    {
        var path = FreshPatch(out _);
        var before = File.ReadAllBytes(path);
        Assert.True(Create(path, true, KeywordAndWeapon()).Success);
        using (var ov = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE))
        {
            Assert.Single(ov.Keywords, k => k.EditorID == "HcUpsGdKw");
            var w = Assert.Single(ov.Weapons, x => x.EditorID == "HcUpsGdWeap");
            Assert.Single(w.Keywords!);
        }
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(0x802u, NextFormId(path));
    }

    // OVERRIDE: a create colliding with a carried override's editorid is refused loud, file untouched, override Damage=20 intact
    [Fact]
    public void ACreateCollidingWithACarriedOverrideIsRefused()
    {
        var path = FreshPatch(out _);
        var edit = new WritePatchBuilder.PatchEdit { Target = _masterWeapon, Path = new[] { "BasicStats", "Damage" }, Verb = "Set", Value = "20" };
        Assert.True(WritePatchBuilder.Apply(_rig.Order(_masterPath), TestCorpus.Rulebook, new[] { edit }, path, extend: true).Success);
        var before = File.ReadAllBytes(path);

        var o = Create(path, true, Bare("Weapon", "HcUpsGdMasterWeap"));
        Assert.False(o.Success);
        Assert.Contains("override", o.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, File.ReadAllBytes(path));
        using var ov = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
        Assert.Equal(20, ov.Weapons.Single(x => x.FormKey == _masterWeapon).BasicStats!.Damage);
    }

    // CROSS-TYPE: a Keyword create colliding with the patch-defined weapon's editorid refuses loud
    [Fact]
    public void AnEditorIdCollisionAcrossTypesIsRefused()
    {
        var path = FreshPatch(out _);
        var before = File.ReadAllBytes(path);
        var o = Create(path, true, Bare("Keyword", "HcUpsGdWeap"));
        Assert.False(o.Success);
        Assert.Contains("not a Keyword", o.Error);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    // DUP: duplicate same-editorid residue refused naming both copies, file untouched
    [Fact]
    public void DuplicateResidueIsRefusedNamingEveryCopy()
    {
        var p = new SkyrimMod(new ModKey("HcUpsGuardDup", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var k1 = p.Keywords.AddNew(); k1.EditorID = "HcUpsGdDupKw";
        var k2 = p.Keywords.AddNew(); k2.EditorID = "HcUpsGdDupKw";
        var dupPath = _rig.Out(p.ModKey.FileName.String);
        p.BeginWrite.ToPath(dupPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).NoNextFormIDProcessing().Write();
        var before = File.ReadAllBytes(dupPath);

        var o = Create(dupPath, true, Bare("Keyword", "HcUpsGdDupKw"));
        Assert.False(o.Success);
        Assert.Contains(FormIdToken.Of(k1.FormKey), o.Error);
        Assert.Contains(FormIdToken.Of(k2.FormKey), o.Error);
        Assert.Equal(before, File.ReadAllBytes(dupPath));
    }

    // LOCKED: a commit blocked by a foreign no-delete-share handle fails loud, old bytes intact, no temp residue
    [Fact]
    public void ABlockedCommitFailsLoudWithTheOldBytesIntact()
    {
        var path = FreshPatch(out _);
        var before = File.ReadAllBytes(path);
        WritePatchBuilder.CreateOutcome o;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            o = Create(path, true, Bare("Keyword", "HcUpsGdKwLocked"));
        Assert.False(o.Success);
        Assert.Contains("writing the patch after create failed", o.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Empty(Directory.EnumerateDirectories(_rig.Root, ".housecarl-tmp", SearchOption.AllDirectories));
    }

    public void Dispose() => _rig.Dispose();
}
