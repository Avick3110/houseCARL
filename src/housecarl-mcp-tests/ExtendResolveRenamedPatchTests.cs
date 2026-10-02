using System.Text.Json;
using HousecarlMcp;
using Xunit;
using static HousecarlMcpTests.ExtendResolveRig;

namespace HousecarlMcpTests;

/// <summary>into= finds houseCARL's own patch after the user renamed its MO2 mod folder (HCBR-2026-06-23): by the
/// .esp it holds, by the folder's new name, on both the record lane and the rider lane, without relaxing the
/// ownership marker. Migrated from the extend-resolve-guard probe (arms CANONICAL to MULTI-PLUGIN, FOREIGN,
/// ORIGINALS).</summary>
[Trait("tier", "integration")]
public sealed class ExtendResolveRenamedPatchTests
{
    // seed patch created at houseCARL - SeedA\SeedA.esp; into="SeedA" resolves the canonical folder
    [Fact]
    public void IntoResolvesTheCanonicalFolderBeforeAnyRename()
    {
        using var w = new ExtendResolveRig();
        Assert.Equal(("houseCARL - SeedA", "SeedA.esp"), Tail(w.Seed()));

        var r = w.Into("SeedA", w.Wgt(5));

        Assert.True(r.Success, r.Error);
        Assert.Equal(("houseCARL - SeedA", "SeedA.esp"), Tail(r.OutputPath));
    }

    // into="SeedA" finds the RENAMED folder by the .esp it holds
    [Fact]
    public void IntoTheEspBasenameFindsTheRenamedFolder()
    {
        using var w = new ExtendResolveRig();
        w.SeedRenamed();

        var r = w.Into("SeedA", w.Wgt(7));

        Assert.True(r.Success, r.Error);
        Assert.Equal(("houseCARL - SeedA Renamed", "SeedA.esp"), Tail(r.OutputPath));
    }

    // into="SeedA.esp" (with extension) strips the ext and resolves the same renamed patch
    [Fact]
    public void IntoTheEspFilenameWithExtensionFindsTheRenamedFolder()
    {
        using var w = new ExtendResolveRig();
        w.SeedRenamed();

        var r = w.Into("SeedA.esp", w.Wgt(7));

        Assert.True(r.Success, r.Error);
        Assert.Equal(("houseCARL - SeedA Renamed", "SeedA.esp"), Tail(r.OutputPath));
    }

    // into="SeedA Renamed" (folder name) edits the single .esp inside it;
    // the renamed patch carries BOTH extends (by-folder Damage, by-esp Weight), not a fresh patch
    [Fact]
    public void IntoTheRenamedFolderNameEditsTheSameEspAndTheEditsAccumulate()
    {
        using var w = new ExtendResolveRig();
        var renamed = w.SeedRenamed();

        var byEsp = w.Into("SeedA", w.Wgt(7));
        var byFolder = w.Into("SeedA Renamed", w.Dmg(88));

        Assert.True(byEsp.Success, byEsp.Error);
        Assert.True(byFolder.Success, byFolder.Error);
        Assert.Equal(("houseCARL - SeedA Renamed", "SeedA.esp"), Tail(byFolder.OutputPath));
        Assert.Equal(((ushort?)88, (float?)7), w.ReadWeapon(Path.Combine(renamed, "SeedA.esp")));
    }

    // rider into="SeedA" finds the renamed folder by the .esp it holds (reused);
    // rider into="SeedA Renamed" (folder name) resolves the same reused folder
    [Fact]
    public void TheRiderLaneResolvesTheRenamedFolderByEspAndByFolderName()
    {
        using var w = new ExtendResolveRig();
        w.SeedRenamed();

        var byEsp = w.Svc.ResolvePatchModFolder(null, "SeedA", "HcRiderDefault", BsaTools.RepackNaming);
        var byFolder = w.Svc.ResolvePatchModFolder(null, "SeedA Renamed", "HcRiderDefault", BsaTools.RepackNaming);

        Assert.False(byEsp.CreatedFresh);
        Assert.Equal("houseCARL - SeedA Renamed", Path.GetFileName(byEsp.ModFolder));
        Assert.False(byFolder.CreatedFresh);
        Assert.Equal("houseCARL - SeedA Renamed", Path.GetFileName(byFolder.ModFolder));
    }

    // into="SeedA" refuses (ambiguous), naming BOTH folders + the into=<folder> disambiguator;
    // into="DupHome" (folder name) picks ONE despite the shared .esp basename
    [Fact]
    public void TwoOwnedFoldersHoldingTheSameEspAreAmbiguousAndAFolderNamePicksOne()
    {
        using var w = new ExtendResolveRig();
        var renamed = w.SeedRenamed();
        var dup = w.MarkOwned("houseCARL - DupHome", "SeedA.esp");
        File.Copy(Path.Combine(renamed, "SeedA.esp"), Path.Combine(dup, "SeedA.esp"));

        var r = w.Into("SeedA", w.Wgt(1));

        Assert.False(r.Success);
        Assert.Contains("ambiguous", r.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("houseCARL - SeedA Renamed", r.Error);
        Assert.Contains("houseCARL - DupHome", r.Error);
        Assert.Contains("into=", r.Error);

        var pick = w.Into("DupHome", w.Wgt(3));
        Assert.True(pick.Success, pick.Error);
        Assert.Equal(("houseCARL - DupHome", "SeedA.esp"), Tail(pick.OutputPath));
    }

    // into="TwoEsp" (folder holds Alpha.esp + Beta.esp) refuses, naming both plugins
    [Fact]
    public void IntoAFolderHoldingTwoPluginsRefusesNamingBoth()
    {
        using var w = new ExtendResolveRig();
        w.OwnedWithPlugins("houseCARL - TwoEsp", "Alpha", "Beta");

        var r = w.Into("TwoEsp", w.Wgt(1));

        Assert.False(r.Success);
        Assert.Contains("2 plugins", r.Error);
        Assert.Contains("Alpha.esp", r.Error);
        Assert.Contains("Beta.esp", r.Error);
    }

    // into="Foreign" (un-owned folder) is REFUSED; the un-owned plugin is byte-untouched;
    // the RIDER lane also refuses the un-owned folder (same ownership gate)
    [Fact]
    public void AnUnownedHousecarlFolderIsRefusedOnBothLanesAndLeftByteIntact()
    {
        using var w = new ExtendResolveRig();
        var foreign = Path.Combine(w.ModsDir, "houseCARL - Foreign");
        Directory.CreateDirectory(foreign);
        var esp = Path.Combine(foreign, "Foreign.esp");
        File.WriteAllText(esp, "not a real plugin — a user file houseCARL must never touch");
        var before = File.ReadAllBytes(esp);

        var r = w.Into("Foreign", w.Wgt(1));

        Assert.False(r.Success);
        Assert.Contains("NOT created by houseCARL", r.Error);
        Assert.Contains("originals untouched", r.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, File.ReadAllBytes(esp));

        var rider = RiderRefusal(() => w.Svc.ResolvePatchModFolder(null, "Foreign", "HcRiderDefault", BsaTools.RepackNaming));
        Assert.Contains("NOT created by houseCARL", rider);
        Assert.Equal(before, File.ReadAllBytes(esp));
    }

    // the master plugin is byte-identical to its pre-write state after EVERY arm (every extend wrote only the patch;
    // no refusal touched it): extends, rider, ambiguous, multi-plugin, foreign, not-found, every tool's un-owned refusal
    [Fact]
    public void ExtendsWriteOnlyThePatchAndNeverTheMaster()
    {
        using var w = new ExtendResolveRig();
        var masterBefore = File.ReadAllBytes(w.MasterPath);
        void Arm(string name, Action run)
        {
            run();
            Assert.True(masterBefore.AsSpan().SequenceEqual(File.ReadAllBytes(w.MasterPath)), $"{name} changed the master");
        }
        void Rider(string token) => w.Svc.ResolvePatchModFolder(null, token, "HcRiderDefault", BsaTools.RepackNaming);
        static JsonElement Doc(string s) => JsonDocument.Parse(s).RootElement;
        var fid = w.Fid;

        Arm("seed", () => w.Seed());
        Arm("canonical", () => Assert.True(w.Into("SeedA", w.Wgt(5)).Success));
        Directory.Move(Path.Combine(w.ModsDir, "houseCARL - SeedA"), Path.Combine(w.ModsDir, "houseCARL - SeedA Renamed"));
        Arm("by-esp", () => Assert.True(w.Into("SeedA", w.Wgt(7)).Success));
        Arm("by-esp-ext", () => Assert.True(w.Into("SeedA.esp", w.Wgt(8)).Success));
        Arm("by-folder", () => Assert.True(w.Into("SeedA Renamed", w.Dmg(88)).Success));
        Arm("rider", () => { Rider("SeedA"); Rider("SeedA Renamed"); });

        var dup = w.MarkOwned("houseCARL - DupHome", "SeedA.esp");
        File.Copy(Path.Combine(w.ModsDir, "houseCARL - SeedA Renamed", "SeedA.esp"), Path.Combine(dup, "SeedA.esp"));
        Arm("ambiguous", () => Assert.False(w.Into("SeedA", w.Wgt(1)).Success));
        Arm("ambiguous pick", () => Assert.True(w.Into("DupHome", w.Wgt(3)).Success));

        w.OwnedWithPlugins("houseCARL - TwoEsp", "Alpha", "Beta");
        Arm("multi-plugin", () => Assert.False(w.Into("TwoEsp", w.Wgt(1)).Success));

        var foreign = Path.Combine(w.ModsDir, "houseCARL - Foreign");
        Directory.CreateDirectory(foreign);
        File.WriteAllText(Path.Combine(foreign, "Foreign.esp"), "not a real plugin");
        Arm("foreign", () => Assert.False(w.Into("Foreign", w.Wgt(1)).Success));
        Arm("foreign rider", () => RiderRefusal(() => Rider("Foreign")));

        Arm("not-found apply", () => Assert.False(w.Into("GhostPatch", w.Wgt(1)).Success));
        Arm("not-found rider", () => RiderRefusal(() => Rider("GhostRider")));
        Arm("not-found forward", () => Assert.False(w.Svc.ForwardRecords(new[] { fid }, w.MasterKey.FileName, null, "GhostFwd").Success));
        Arm("not-found create", () => Assert.False(
            w.Svc.CreateRecordsBatch(new[] { new CreateOp { RecordType = "Keyword", Editorid = "HcExtKw" } }, null, "GhostCre").Success));
        Arm("not-found remove", () => Assert.False(w.Svc.RemoveRecords(new[] { fid }, "GhostRemove").Success));

        Arm("un-owned apply tool", () => Assert.Contains("; try into=\"", ApplyTools.Apply(w.Svc,
            ops: Doc($"[{{\"formid\":\"{fid}\",\"field_path\":\"BasicStats.Weight\",\"value\":\"2\"}}]"), into: "Foreign")));
        Arm("un-owned create tool", () => Assert.Contains("; try into=\"", CreateTools.Create(w.Svc,
            records: Doc("[{\"record_type\":\"Keyword\",\"editorid\":\"HcExtUnowned\"}]"), into: "Foreign")));
        Arm("un-owned forward tool", () => Assert.Contains("; try into=\"",
            ForwardTools.Forward(w.Svc, formids: new[] { fid }, source: w.MasterKey.FileName.String, into: "Foreign")));
        Arm("un-owned remove tool", () => Assert.Contains("; try into=\"", RemoveTools.Remove(w.Svc, new[] { fid }, into: "Foreign")));
    }
}
