using HousecarlGenerator;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The reverse walk judges a winner's copy off the index's verdicts: a deleted winner and a winner whose link
/// walk throws are each dropped with their own named count, never read as live copies.</summary>
[Trait("tier", "integration")]
public sealed class ReverseWalkIndexVerdictTests
{
    static PerkEntryPointModifyActorValue Effect() => new()
    {
        EntryPoint = APerkEntryPointEffect.EntryType.CalculateWeaponDamage,
        ActorValue = ActorValue.OneHanded,
        Value = 1f,
        Modification = PerkEntryPointModifyActorValue.ModificationType.AddAVMult,
    };

    /// <summary>HcZzBase.esm: Target, Dead and Throw, the last two linking Target; HcZzOver.esp deletes Dead and
    /// overrides Throw with one effect whose EPFT byte is corrupted, so its link walk throws.</summary>
    [Fact]
    public void ADeletedWinnerAndAThrowingWinnerAreDroppedWithTheirOwnCounts()
    {
        var mo2 = new ScratchMo2("hc-zz-verdict-");
        try
        {
            var b = new SkyrimMod(new ModKey("HcZzBase", ModType.Master), SkyrimRelease.SkyrimSE);
            var target = b.Perks.AddNew(); target.EditorID = "HcZzTarget";
            var dead = b.Perks.AddNew(); dead.EditorID = "HcZzDead"; dead.NextPerk.SetTo(target.FormKey);
            var thrower = b.Perks.AddNew(); thrower.EditorID = "HcZzThrow"; thrower.NextPerk.SetTo(target.FormKey);
            b.BeginWrite.ToPath(mo2.InMod("ZzBase", b.ModKey)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

            var o = new SkyrimMod(new ModKey("HcZzOver", ModType.Plugin), SkyrimRelease.SkyrimSE);
            o.Perks.GetOrAddAsOverride(dead);
            var overThrow = o.Perks.GetOrAddAsOverride(thrower);
            overThrow.Effects.Add(Effect());
            var overPath = mo2.InMod("ZzOver", o.ModKey);
            o.BeginWrite.ToPath(overPath).WithLoadOrder(new ISkyrimModGetter[] { b }).Write();
            // The master sits at index 0, so an override's raw FormID is its base ID.
            Assert.Equal(1, ProbeBytes.SetDeletedFlag(overPath, "PERK", dead.FormKey.ID));
            Assert.Equal(1, ProbeBytes.CorruptEpftBytes(overPath));
            mo2.Profile("HcZzBase.esm\r\nHcZzOver.esp\r\n", "*HcZzBase.esm\r\n*HcZzOver.esp\r\n", "+ZzOver\r\n+ZzBase\r\n");

            using var svc = mo2.Open();
            var r = RecordsTools.Records(svc, formids: new[] { ScratchMo2.Fid(target.FormKey) },
                                         walk: new RecordsTools.RecordsWalk { direction = "reverse", depth = 1 },
                                         project: new RecordsTools.RecordsProject { form = "summary" });

            Assert.False(r.StartsWith("error:", StringComparison.Ordinal), r);
            Assert.Contains("1 whose winning plugin could not be read", r);
            Assert.Contains("1 whose winner is deleted or carries no links", r);
            Assert.Contains("selection = 1 record(s)", r);
            Assert.DoesNotContain(ScratchMo2.Fid(thrower.FormKey), r);
        }
        finally { mo2.Delete(); }
    }
}
