using HousecarlCore;
using HousecarlMcp;
using Xunit;
using W = HousecarlMcpTests.InPlaceGuardWorld;

namespace HousecarlMcpTests;

/// <summary>Each in-place lane's service pre-flight refuses a localized target before the write, in that lane's own
/// words, on the dry run as on the real call, and records no consent; a plain target's dry run still reports. Moved
/// from the <c>inplace-guard</c> probe (arms LOC-E to LOC-J). The lane clause is what tells the pre-flight from the
/// write's own refusal, which uses the same lead sentence.</summary>
[Trait("tier", "integration")]
[Collection(InPlaceGuardCollection.Name)]
public sealed class InPlaceGuardLocalizedPreflightTests
{
    const string Lead = "houseCARL did not write";
    readonly W _w;
    public InPlaceGuardLocalizedPreflightTests(W w) { _w = w; }

    LoadOrderService Service(string store, params string[] order)
    {
        var svc = LoadOrderService.ForGuard(LoadOrderResolver.Build(order), new UserConfigStore(store));
        svc.Stats();
        return svc;
    }

    BulkOp[] SetDamage => new[] { new BulkOp { Formid = _w.WeaponId, FieldPath = "BasicStats.Damage", Verb = "Set", Value = "55" } };

    // LOC-E an in-place DRY RUN on a localized target refuses, with the lane's remedy, as the real call does
    [Fact]
    public void AnInPlaceDryRunOnALocalizedTargetRefusesWithTheRemedy()
    {
        var loc = _w.FreshLocalized();
        using var svc = Service(_w.NewStorePath(), _w.MasterPath, loc);
        var dry = svc.ApplyEdits(SetDamage, null, null, fullReadback: false, target: W.LocName, inPlace: true, acknowledge: false, dryRun: true);
        Assert.False(dry.Success);
        Assert.StartsWith(Lead, dry.Error, StringComparison.Ordinal);
        Assert.Contains(LocalizedTargetUnsupportedException.RemedyDefaultLane, dry.Error, StringComparison.Ordinal);
    }

    // LOC-F the refused write does NOT spend consent, and carries the same remedy the dry run gave
    [Fact]
    public void ARefusedLocalizedEditSpendsNoConsentAndCarriesTheRemedy()
    {
        var loc = _w.FreshLocalized();
        var store = _w.NewStorePath();
        using var svc = Service(store, _w.MasterPath, loc);
        var real = svc.ApplyEdits(SetDamage, null, null, fullReadback: false, target: W.LocName, inPlace: true, acknowledge: true);
        Assert.False(real.Success);
        Assert.Contains(LocalizedTargetUnsupportedException.RemedyDefaultLane, real.Error, StringComparison.Ordinal);
        Assert.False(new UserConfigStore(store).IsInPlaceAcknowledged(loc));
    }

    // LOC-G the NON-localized twin's in-place dry run still reports what it would do
    [Fact]
    public void ThePlainTwinsInPlaceDryRunStillReports()
    {
        var user = _w.FreshUser();
        using var svc = Service(_w.NewStorePath(), _w.MasterPath, user);
        var dry = svc.ApplyEdits(SetDamage, null, null, fullReadback: false, target: W.UserName, inPlace: true, acknowledge: false, dryRun: true);
        Assert.True(dry.Success, dry.Error);
        Assert.True(dry.DryRun);
    }

    // LOC-H the REMOVE lane's own pre-flight answers before consent, with THIS lane's clause
    [Fact]
    public void TheRemoveLanesPreflightRefusesALocalizedTargetInItsOwnClause()
    {
        var loc = _w.FreshLocalized();
        var store = _w.NewStorePath();
        using var svc = Service(store, _w.MasterPath, loc);
        var o = svc.RemoveRecords(new[] { _w.WeaponId }, null, target: W.LocName, inPlace: true, acknowledge: true);
        Assert.False(o.Success);
        Assert.StartsWith(Lead, o.Error, StringComparison.Ordinal);
        Assert.Contains(LocalizedTargetUnsupportedException.RemoveNoEquivalent, o.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(LocalizedTargetUnsupportedException.RemedyDefaultLane, o.Error, StringComparison.Ordinal);
        Assert.False(new UserConfigStore(store).IsInPlaceAcknowledged(loc));
    }

    // LOC-I the FORWARD lane's own pre-flight answers before consent, with THIS lane's clause
    [Fact]
    public void TheForwardLanesPreflightRefusesALocalizedTargetInItsOwnClause()
    {
        var loc = _w.FreshLocalized();
        var store = _w.NewStorePath();
        using var svc = Service(store, _w.MasterPath, loc, _w.HighPath);
        var o = svc.ForwardRecords(new[] { _w.WeaponId }, W.HighName, null, null, target: W.LocName, inPlace: true, acknowledge: true);
        Assert.False(o.Success);
        Assert.StartsWith(Lead, o.Error, StringComparison.Ordinal);
        Assert.Contains(LocalizedTargetUnsupportedException.RemedyDefaultLane, o.Error, StringComparison.Ordinal);
        Assert.False(new UserConfigStore(store).IsInPlaceAcknowledged(loc));
    }

    // LOC-J the CREATE lane's own pre-flight answers before consent, with THIS lane's clause
    [Fact]
    public void TheCreateLanesPreflightRefusesALocalizedTargetInItsOwnClause()
    {
        var loc = _w.FreshLocalized();
        var store = _w.NewStorePath();
        using var svc = Service(store, _w.MasterPath, loc);
        var o = svc.InPlaceGuardCreate("Keyword", "HcIP_LocKw", Array.Empty<BulkOp>(), null, null, false, null, null, null, target: W.LocName, inPlace: true, acknowledge: true);
        Assert.False(o.Success);
        Assert.StartsWith(Lead, o.Error, StringComparison.Ordinal);
        Assert.Contains(LocalizedTargetUnsupportedException.RemedyDefaultLane, o.Error, StringComparison.Ordinal);
        Assert.False(new UserConfigStore(store).IsInPlaceAcknowledged(loc));
    }
}
