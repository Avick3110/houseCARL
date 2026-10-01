using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlGenerator;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A DELETED record links to nothing (#279), in the two link walkers the records scan does not cover:
/// <c>ErrorCheck.Run</c>'s dangling-ref sweep and <c>RemapEngine.IdentifyExternalReferencers</c>. Mutagen cannot author
/// either shape, so the fixtures are written normally and patched on disk through <c>ProbeBytes</c>: the Deleted flag
/// OR-ed on, and one EPFT byte corrupted so a deleted perk's lazy parse throws. Each fixture is checked to still show
/// the pre-fix hazard (the intact body still yields its link, the corrupt one still throws), so a pass means the rule
/// held. Each test gets its own temp folder.</summary>
[Trait("tier", "integration")]
public sealed class DeletedLinkWalkTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "hc-deleted-link-walk-" + Guid.NewGuid().ToString("N"));

    public DeletedLinkWalkTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* temp cleanup best-effort */ } }

    static PerkEntryPointModifyActorValue Effect() => new()
    {
        EntryPoint = APerkEntryPointEffect.EntryType.CalculateWeaponDamage,
        ActorValue = ActorValue.OneHanded,
        Value = 1f,
        Modification = PerkEntryPointModifyActorValue.ModificationType.AddAVMult,
    };

    static bool Throws(IMajorRecordGetter r)
    {
        try { _ = ((IFormLinkContainerGetter)r).EnumerateFormLinks().Count(); return false; }
        catch (Exception) { return true; }
    }

    // ---- the errors family: HcDlwGhost.esm on disk but not loaded, HcDlwErr.esp linking into it ----

    sealed record ErrFixture(string Path, FormKey GhostRace, FormKey LiveNpc, FormKey DeadNpc, FormKey DeadPerk);

    ErrFixture BuildErr()
    {
        var ghostPath = Path.Combine(_dir, "HcDlwGhost.esm");
        var errPath = Path.Combine(_dir, "HcDlwErr.esp");
        var ghost = new SkyrimMod(new ModKey("HcDlwGhost", ModType.Master), SkyrimRelease.SkyrimSE);
        var race = ghost.Races.AddNew(); race.EditorID = "HcDlwGhostRace";
        ghost.BeginWrite.ToPath(ghostPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        var err = new SkyrimMod(new ModKey("HcDlwErr", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var live = err.Npcs.AddNew(); live.EditorID = "HcDlwLiveDangler"; live.Race.SetTo(race.FormKey);
        var dead = err.Npcs.AddNew(); dead.EditorID = "HcDlwDeadDangler"; dead.Race.SetTo(race.FormKey);
        var thrower = err.Perks.AddNew(); thrower.EditorID = "HcDlwDeadThrower";
        thrower.Effects.Add(Effect());
        err.BeginWrite.ToPath(errPath).WithLoadOrder(new ISkyrimModGetter[] { ghost }).Write();

        const uint ownIndex = 1u << 24;   // one declared master, so the plugin's own records sit at index 1
        Assert.Equal(1, ProbeBytes.CorruptEpftBytes(errPath));
        Assert.Equal(1, ProbeBytes.SetDeletedFlag(errPath, "NPC_", ownIndex | dead.FormKey.ID));
        Assert.Equal(1, ProbeBytes.SetDeletedFlag(errPath, "PERK", ownIndex | thrower.FormKey.ID));
        return new ErrFixture(errPath, race.FormKey, live.FormKey, dead.FormKey, thrower.FormKey);
    }

    static ErrorCheckResult Sweep(ErrFixture f)
    {
        using var resolver = LoadOrderResolver.Build(new[] { f.Path });
        var r = ErrorCheck.Run(resolver, null, limit: 100);
        Assert.True(r.Success, r.Error);
        return r;
    }

    // CONTROL: the deleted NPC reads as Deleted and its intact body STILL yields the ghost link; the deleted perk STILL throws
    [Fact]
    public void TheErrorsFixtureStillShowsThePreFixHazard()
    {
        var f = BuildErr();
        using var ov = SkyrimMod.CreateFromBinaryOverlay(f.Path, SkyrimRelease.SkyrimSE);
        var npc = ov.Npcs.First(n => n.FormKey == f.DeadNpc);
        Assert.True(npc.IsDeleted);
        Assert.Contains(((IFormLinkContainerGetter)npc).EnumerateFormLinks(), l => l.FormKey == f.GhostRace);
        var perk = ov.Perks.First(p => p.FormKey == f.DeadPerk);
        Assert.True(perk.IsDeleted);
        Assert.True(Throws(perk));
    }

    // CONTROL: the LIVE dangling ref is still reported (no false clean)
    [Fact]
    public void TheLiveDanglingRefIsStillReported()
    {
        var f = BuildErr();
        Assert.Contains(Sweep(f).Reports.SelectMany(p => p.Dangling), d => d.Source == f.LiveNpc && d.Target == f.GhostRace);
    }

    // SEMANTIC: the DELETED record's link is NOT reported dangling (#279)
    [Fact]
    public void ADeletedRecordsLinkIsNotReportedDangling()
    {
        var f = BuildErr();
        var r = Sweep(f);
        Assert.Equal(1, r.TotalDangling);
        Assert.DoesNotContain(r.Reports.SelectMany(p => p.Dangling), d => d.Source == f.DeadNpc);
    }

    // CRASH-CLASS: the DELETED throwing record is NOT accounted unscannable (#279)
    [Fact]
    public void ADeletedThrowingRecordIsNotUnscannableToTheSweep()
    {
        var f = BuildErr();
        var r = Sweep(f);
        Assert.Equal(0, r.TotalUnscannableRecords);
        Assert.DoesNotContain(r.Reports.SelectMany(p => p.UnscannableSamples),
            s => s.Contains(f.DeadPerk.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    // ---- compact/merge scan: HcDlwTarget.esp's weapon, HcDlwDep.esp outside the transform set ----

    static readonly ModKey TargetKey = new("HcDlwTarget", ModType.Plugin);
    static readonly ModKey DepKey = new("HcDlwDep", ModType.Plugin);
    static readonly FormKey TargetWeap = new(TargetKey, 0xA01);
    static readonly FormKey LiveRef = new(DepKey, 0xB01);
    static readonly FormKey DeadRef = new(DepKey, 0xB02);
    static readonly FormKey DeadPerk = new(DepKey, 0xB03);

    (string target, string dep) BuildRemap()
    {
        var targetPath = Path.Combine(_dir, TargetKey.FileName.String);
        var depPath = Path.Combine(_dir, DepKey.FileName.String);
        var t = new SkyrimMod(TargetKey, SkyrimRelease.SkyrimSE);
        t.Weapons.Add(new Weapon(TargetWeap, SkyrimRelease.SkyrimSE) { EditorID = "HcDlwTargetWeap", BasicStats = new WeaponBasicStats { Damage = 7 } });
        t.BeginWrite.ToPath(targetPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).NoNextFormIDProcessing().Write();

        using (var tOv = SkyrimMod.CreateFromBinaryOverlay(targetPath, SkyrimRelease.SkyrimSE))
        {
            var d = new SkyrimMod(DepKey, SkyrimRelease.SkyrimSE);
            var liveList = new FormList(LiveRef, SkyrimRelease.SkyrimSE) { EditorID = "HcDlwLiveRef" };
            liveList.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(TargetWeap));
            d.FormLists.Add(liveList);
            var deadList = new FormList(DeadRef, SkyrimRelease.SkyrimSE) { EditorID = "HcDlwDeadRef" };
            deadList.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(TargetWeap));
            d.FormLists.Add(deadList);
            d.Weapons.Add(new Weapon(TargetWeap, SkyrimRelease.SkyrimSE) { EditorID = "HcDlwDeadOverride", BasicStats = new WeaponBasicStats { Damage = 9 } });
            var thrower = new Perk(DeadPerk, SkyrimRelease.SkyrimSE) { EditorID = "HcDlwDeadThrower" };
            thrower.Effects.Add(Effect());
            d.Perks.Add(thrower);
            d.ModHeader.Stats.NextFormID = 0xB04;
            d.BeginWrite.ToPath(depPath).WithLoadOrder(new[] { tOv }).NoNextFormIDProcessing().Write();
        }
        // the dep's own records sit at index 1 on disk; the override keeps its master's index 0
        Assert.Equal(1, ProbeBytes.CorruptEpftBytes(depPath));
        Assert.Equal(1, ProbeBytes.SetDeletedFlag(depPath, "FLST", (1u << 24) | DeadRef.ID));
        Assert.Equal(1, ProbeBytes.SetDeletedFlag(depPath, "PERK", (1u << 24) | DeadPerk.ID));
        Assert.Equal(1, ProbeBytes.SetDeletedFlag(depPath, "WEAP", TargetWeap.ID));
        return (targetPath, depPath);
    }

    static RemapEngine.IdentifyResult Identify((string target, string dep) f)
    {
        using var resolver = LoadOrderResolver.Build(new[] { f.target, f.dep });
        return RemapEngine.IdentifyExternalReferencers(resolver, new HashSet<FormKey> { TargetWeap },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { TargetKey.FileName.String });
    }

    // CONTROL: the deleted FormList still yields the target link, the override reads as Deleted, the deleted perk still throws
    [Fact]
    public void TheRemapFixtureStillShowsThePreFixHazard()
    {
        var f = BuildRemap();
        using var ov = SkyrimMod.CreateFromBinaryOverlay(f.dep, SkyrimRelease.SkyrimSE);
        var list = ov.FormLists.First(x => x.FormKey == DeadRef);
        Assert.True(list.IsDeleted);
        Assert.Contains(((IFormLinkContainerGetter)list).EnumerateFormLinks(), l => l.FormKey == TargetWeap);
        Assert.True(ov.Weapons.First(w => w.FormKey == TargetWeap).IsDeleted);
        var perk = ov.Perks.First(p => p.FormKey == DeadPerk);
        Assert.True(perk.IsDeleted);
        Assert.True(Throws(perk));
    }

    // CONTROL: the LIVE referencer is still detected (no false clean)
    [Fact]
    public void TheLiveReferencerIsStillDetected()
    {
        var r = Identify(BuildRemap());
        Assert.True(r.HasExternalReferencers);
        Assert.Contains(r.Refs, x => x.Source == LiveRef);
    }

    // SEMANTIC: the DELETED record is NOT listed as an external referencer (#279)
    [Fact]
    public void ADeletedRecordIsNotAnExternalReferencer()
        => Assert.Equal(LiveRef, Assert.Single(Identify(BuildRemap()).Refs).Source);

    // SCOPE: a DELETED external OVERRIDE is still warned (identity test unaffected by the link-walk guard)
    [Fact]
    public void ADeletedExternalOverrideIsStillAnOverrider()
    {
        var r = Identify(BuildRemap());
        Assert.True(r.HasExternalOverriders);
        Assert.Contains(r.Overrides, x => x.Record == TargetWeap);
    }

    // CRASH-CLASS: the DELETED throwing record is NOT accounted unscannable (#279)
    [Fact]
    public void ADeletedThrowingRecordIsNotUnscannableToTheIdentifyPass()
        => Assert.Equal(0, Identify(BuildRemap()).UnscannableRecords);
}
