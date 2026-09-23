using Mutagen.Bethesda.Plugins;
using HousecarlCore;
using HousecarlMcp;
using Xunit;
using W = HousecarlMcpTests.InPlaceGuardWorld;

namespace HousecarlMcpTests;

/// <summary>The in-place consent handshake at the service: the first touch refuses and writes nothing, acknowledge
/// writes, the next call does not re-prompt, the record persists and is shared across lanes, and only a write that
/// LANDS records it. Moved from the <c>inplace-guard</c> probe (arms G, K, W, CO-A to CO-G).</summary>
[Trait("tier", "integration")]
[Collection(InPlaceGuardCollection.Name)]
public sealed class InPlaceGuardConsentTests
{
    readonly W _w;
    public InPlaceGuardConsentTests(W w) { _w = w; _w.UseCorpus(); }

    LoadOrderService Service(string store, params string[] order) =>
        LoadOrderService.ForGuard(LoadOrderResolver.Build(order), new UserConfigStore(store));

    BulkOp[] SetDamage(string formId, string value) =>
        new[] { new BulkOp { Formid = formId, FieldPath = "BasicStats.Damage", Verb = "Set", Value = value } };

    static bool Acknowledged(string store, string plugin) => new UserConfigStore(store).IsInPlaceAcknowledged(plugin);

    // G handshake RED→GREEN→no-reprompt→persists
    [Fact]
    public void TheEditHandshakeRefusesFirstWritesOnAcknowledgeThenNeverReprompts()
    {
        var user = _w.FreshUser();
        var store = _w.NewStorePath();
        var before = File.ReadAllBytes(user);
        using (var svc = Service(store, _w.MasterPath, user))
        {
            var first = svc.ApplyEdits(SetDamage(_w.WeaponId, "31"), null, null, fullReadback: false, target: W.UserName, inPlace: true, acknowledge: false);
            Assert.True(first.NeedsAcknowledge);
            Assert.False(first.Success);
            Assert.Equal(before, File.ReadAllBytes(user));

            var ack = svc.ApplyEdits(SetDamage(_w.WeaponId, "31"), null, null, fullReadback: false, target: W.UserName, inPlace: true, acknowledge: true);
            Assert.True(ack.Success && ack.InPlace, ack.Error);
            Assert.Equal(31, W.Damage(user, _w.Weapon));

            var again = svc.ApplyEdits(SetDamage(_w.WeaponId, "32"), null, null, fullReadback: false, target: W.UserName, inPlace: true, acknowledge: false);
            Assert.False(again.NeedsAcknowledge);
            Assert.True(again.Success, again.Error);
            Assert.Equal(32, W.Damage(user, _w.Weapon));
        }
        Assert.True(Acknowledged(store, user));   // a fresh store on the same file already knows
    }

    // K create handshake RED->GREEN->no-reprompt
    [Fact]
    public void TheCreateHandshakeRefusesFirstWritesOnAcknowledgeThenNeverReprompts()
    {
        var user = _w.FreshUser();
        var before = File.ReadAllBytes(user);
        using var svc = Service(_w.NewStorePath(), _w.MasterPath, user);
        var first = svc.InPlaceGuardCreate("Keyword", "HcIP_KwA", Array.Empty<BulkOp>(), null, null, false, null, null, null, target: W.UserName, inPlace: true, acknowledge: false);
        Assert.True(first.NeedsAcknowledge);
        Assert.False(first.Success);
        Assert.Equal(before, File.ReadAllBytes(user));
        var ack = svc.InPlaceGuardCreate("Keyword", "HcIP_KwA", Array.Empty<BulkOp>(), null, null, false, null, null, null, target: W.UserName, inPlace: true, acknowledge: true);
        Assert.True(ack.Success && ack.InPlace, ack.Error);
        var again = svc.InPlaceGuardCreate("Keyword", "HcIP_KwB", Array.Empty<BulkOp>(), null, null, false, null, null, null, target: W.UserName, inPlace: true, acknowledge: false);
        Assert.False(again.NeedsAcknowledge);
        Assert.True(again.Success, again.Error);
    }

    // W remove handshake RED->GREEN (part 1)
    [Fact]
    public void TheRemoveHandshakeRefusesFirstThenRemovesOnAcknowledge()
    {
        var user = _w.FreshUser();
        var before = File.ReadAllBytes(user);
        using var svc = Service(_w.NewStorePath(), _w.MasterPath, user);
        var first = svc.RemoveRecords(new[] { _w.WeaponId }, null, target: W.UserName, inPlace: true, acknowledge: false);
        Assert.True(first.NeedsAcknowledge);
        Assert.False(first.Success);
        Assert.Equal(before, File.ReadAllBytes(user));
        var ack = svc.RemoveRecords(new[] { _w.WeaponId }, null, target: W.UserName, inPlace: true, acknowledge: true);
        Assert.True(ack.Success && ack.InPlace, ack.Error);
        Assert.False(W.Present(user, _w.Weapon));
    }

    // W SHARED with edit lane (one ack covers both) (part 2)
    [Fact]
    public void AnEditsAcknowledgementCoversALaterInPlaceRemove()
    {
        var user = _w.FreshUser();
        using var svc = Service(_w.NewStorePath(), _w.MasterPath, user);
        var e = svc.ApplyEdits(SetDamage(_w.WeaponId, "33"), null, null, fullReadback: false, target: W.UserName, inPlace: true, acknowledge: true);
        Assert.True(e.Success && e.InPlace, e.Error);
        var rm = svc.RemoveRecords(new[] { _w.WeaponId }, null, target: W.UserName, inPlace: true, acknowledge: false);
        Assert.False(rm.NeedsAcknowledge);
        Assert.True(rm.Success, rm.Error);
        Assert.False(W.Present(user, _w.Weapon));
    }

    // ---- CO: a REFUSED in-place call must not spend the one-time consent (#378) ------------------------------
    // Each first asks the same call without acknowledge and requires the prompt, so the refusal is known to sit
    // behind the consent gate; a refusal ahead of the gate could never spend consent and would prove nothing.

    // CO-A a REFUSED in-place remove (not carried) spends no consent — the next write still prompts
    [Fact]
    public void ARefusedInPlaceRemoveSpendsNoConsent()
    {
        var user = _w.FreshUser();
        var store = _w.NewStorePath();
        var before = File.ReadAllBytes(user);
        using var svc = Service(store, _w.MasterPath, user, _w.HighPath);
        Assert.True(svc.RemoveRecords(new[] { _w.Weapon2Id }, null, target: W.UserName, inPlace: true, acknowledge: false).NeedsAcknowledge);
        var o = svc.RemoveRecords(new[] { _w.Weapon2Id }, null, target: W.UserName, inPlace: true, acknowledge: true);
        Assert.False(o.Success);
        Assert.Contains("not carried by", o.Error);
        Assert.False(Acknowledged(store, user));
        var next = svc.RemoveRecords(new[] { _w.WeaponId }, null, target: W.UserName, inPlace: true, acknowledge: false);
        Assert.True(next.NeedsAcknowledge && !next.Success);
        Assert.Equal(before, File.ReadAllBytes(user));
    }

    // CO-B a REFUSED in-place edit (record not defined by the target) spends no consent
    [Fact]
    public void ARefusedInPlaceEditSpendsNoConsent()
    {
        var user = _w.FreshUser();
        var store = _w.NewStorePath();
        var before = File.ReadAllBytes(user);
        using var svc = Service(store, _w.MasterPath, user, _w.HighPath);
        var op = SetDamage(_w.Weapon2Id, "44");
        Assert.True(svc.ApplyEdits(op, null, null, fullReadback: false, target: W.UserName, inPlace: true, acknowledge: false).NeedsAcknowledge);
        var o = svc.ApplyEdits(op, null, null, fullReadback: false, target: W.UserName, inPlace: true, acknowledge: true);
        Assert.False(o.Success);
        Assert.Contains("does not define or override", o.Error);
        Assert.False(Acknowledged(store, user));
        var next = svc.ApplyEdits(SetDamage(_w.WeaponId, "44"), null, null, fullReadback: false, target: W.UserName, inPlace: true, acknowledge: false);
        Assert.True(next.NeedsAcknowledge && !next.Success);
        Assert.Equal(before, File.ReadAllBytes(user));
    }

    // CO-C a REFUSED in-place forward (source does not carry it) spends no consent
    [Fact]
    public void ARefusedInPlaceForwardSpendsNoConsent()
    {
        var user = _w.FreshUser();
        var store = _w.NewStorePath();
        var before = File.ReadAllBytes(user);
        using var svc = Service(store, _w.MasterPath, user, _w.HighPath);
        Assert.True(svc.ForwardRecords(new[] { _w.Weapon2Id }, W.HighName, null, null, target: W.UserName, inPlace: true, acknowledge: false).NeedsAcknowledge);
        var o = svc.ForwardRecords(new[] { _w.Weapon2Id }, W.HighName, null, null, target: W.UserName, inPlace: true, acknowledge: true);
        Assert.False(o.Success);
        Assert.Contains("forward(s) rejected", o.Error);   // the builder's own rejection, which lives at the write
        Assert.False(Acknowledged(store, user));
        var next = svc.ForwardRecords(new[] { _w.WeaponId }, W.HighName, null, null, target: W.UserName, inPlace: true, acknowledge: false);
        Assert.True(next.NeedsAcknowledge && !next.Success);
        Assert.Equal(before, File.ReadAllBytes(user));
    }

    // CO-D a REFUSED in-place create (link to a non-load-order plugin) spends no consent
    [Fact]
    public void ARefusedInPlaceCreateSpendsNoConsent()
    {
        var user = _w.FreshUser();
        var store = _w.NewStorePath();
        var before = File.ReadAllBytes(user);
        using var svc = Service(store, _w.MasterPath, user, _w.HighPath);
        var ops = new[] { new BulkOp { FieldPath = "Keywords", Verb = "Add", Value = "000ABC:NotInOrder.esp" } };
        Assert.True(svc.InPlaceGuardCreate("Weapon", "HcIP_CoDWeap", ops, null, null, false, null, null, null, target: W.UserName, inPlace: true, acknowledge: false).NeedsAcknowledge);
        var o = svc.InPlaceGuardCreate("Weapon", "HcIP_CoDWeap", ops, null, null, false, null, null, null, target: W.UserName, inPlace: true, acknowledge: true);
        Assert.False(o.Success);
        Assert.Contains("in place after create failed", o.Error);   // the serialize's refusal
        Assert.False(Acknowledged(store, user));
        var next = svc.InPlaceGuardCreate("Keyword", "HcIP_CoDKw", Array.Empty<BulkOp>(), null, null, false, null, null, null, target: W.UserName, inPlace: true, acknowledge: false);
        Assert.True(next.NeedsAcknowledge && !next.Success);
        Assert.Equal(before, File.ReadAllBytes(user));
    }

    // CO-E an acknowledged forward that LANDS records the consent — the next one does not re-prompt
    [Fact]
    public void AnAcknowledgedForwardThatLandsRecordsConsent()
    {
        var user = _w.FreshUser();
        var store = _w.NewStorePath();
        using var svc = Service(store, _w.MasterPath, user, _w.HighPath);
        var first = svc.ForwardRecords(new[] { _w.WeaponId }, W.HighName, null, null, target: W.UserName, inPlace: true, acknowledge: true);
        Assert.True(first.Success && first.InPlace, first.Error);
        Assert.True(Acknowledged(store, user));
        var second = svc.ForwardRecords(new[] { _w.WeaponId }, W.MasterName, null, null, target: W.UserName, inPlace: true, acknowledge: false);
        Assert.False(second.NeedsAcknowledge);
        Assert.True(second.Success, second.Error);
    }

    // CO-F an acknowledged remove that LANDS records the consent — the next removal does not re-prompt
    [Fact]
    public void AnAcknowledgedRemoveThatLandsRecordsConsent()
    {
        var user = _w.FreshUser();
        var store = _w.NewStorePath();
        FormKey kw;
        using (var rc = LoadOrderResolver.Build(new[] { _w.MasterPath, user, _w.HighPath }))
        {
            // The builder directly: the service lane would record the consent this test measures.
            var c = WritePatchBuilder.CreateRecordsInPlace(rc, _w.Rulebook,
                new[] { new WritePatchBuilder.CreateSpec { RecordType = "Keyword", EditorId = "HcIP_CoFKw", Edits = Array.Empty<WriteRequest>() } },
                user, W.UserName);
            kw = Assert.Single(c.Created).FormKey;
        }
        using var svc = Service(store, _w.MasterPath, user, _w.HighPath);
        var first = svc.RemoveRecords(new[] { $"{kw.ID:X6}:{W.UserName}" }, null, target: W.UserName, inPlace: true, acknowledge: true);
        Assert.True(first.Success && first.InPlace, first.Error);
        Assert.True(Acknowledged(store, user));
        var second = svc.RemoveRecords(new[] { _w.WeaponId }, null, target: W.UserName, inPlace: true, acknowledge: false);
        Assert.False(second.NeedsAcknowledge);
        Assert.True(second.Success, second.Error);
    }

    // CO-G the first-touch prompt states WHEN it stops (a landed write) and a direction-neutral file claim
    [Fact]
    public void TheFirstTouchPromptSaysItStopsOnALandedWriteAndMakesANeutralFileClaim()
    {
        var user = _w.FreshUser();
        using var svc = Service(_w.NewStorePath(), _w.MasterPath, user, _w.HighPath);
        var prompt = svc.RemoveRecords(new[] { _w.WeaponId }, null, target: W.UserName, inPlace: true, acknowledge: false).Error;
        Assert.Contains("LANDS", prompt, StringComparison.Ordinal);
        Assert.Contains("refused records nothing", prompt, StringComparison.Ordinal);
        Assert.Contains("not a copy", prompt, StringComparison.Ordinal);
        Assert.Contains("cannot restore what it overwrites", prompt, StringComparison.Ordinal);
    }
}
