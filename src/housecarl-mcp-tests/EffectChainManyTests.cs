using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda.Plugins;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>Several MGEF seeds share one carrier pass.</summary>
[Trait("tier", "integration")]
public sealed class EffectChainManyTests : IClassFixture<SmallDoorWorld>
{
    readonly SmallDoorWorld _w;
    public EffectChainManyTests(SmallDoorWorld w) => _w = w;

    static string Show(EffectChainResult r)
        => $"{r.Mgef} {r.MgefEditorId} total={r.Total} capped={r.Capped} error={r.Error} note={r.ScanNote} epoch={r.Epoch} | "
           + string.Join("; ", r.Rows.Select(x => $"{x.Carrier} {x.Type} {x.EditorId} {x.Winner} #{x.EffectIndex}/{x.EffectCount} {x.Magnitude} {x.Area} {x.Duration}"));

    /// <summary>Two effects, a repeated seed, a weapon and an absent FormID in one call answer as each does alone, at a cap and without one.</summary>
    [Theory]
    [InlineData(50)]
    [InlineData(1)]
    public void ManySeedsInOneCallEqualTheSameSeedsOneAtATime(int limit)
    {
        var seeds = new[] { _w.EffectA, _w.EffectB, _w.EffectA, _w.Weapon1, FormKey.Factory("0ABC12:" + _w.Base.FileName) };

        var many = _w.Svc.ResolveEffectChains(seeds, null, limit).Select(Show).ToList();
        var one = seeds.Select(s => Show(_w.Svc.ResolveEffectChain(s, null, limit))).ToList();

        Assert.Equal(one, many);
        Assert.Contains("total=4", many[0]);   // A: base spell, held spell, and Top's spell twice
        Assert.Contains("total=2", many[1]);   // B: Top's spell and enchantment
    }
    /// <summary>Five seeds through the Records tool read each carrier group and the MGEF group once, not once per seed.</summary>
    [Fact]
    public void FiveSeedsReadEachCarrierGroupOnce()
    {
        var d = GroupReadDelta.Of(_w.Svc, () => RecordsTools.Records(_w.Svc, formids: _w.Effects.Select(ScratchMo2.Fid).ToArray(),
                                                                      walk: new RecordsTools.RecordsWalk { direction = "reverse", follow = "Effects[].BaseEffect" },
                                                                      project: new RecordsTools.RecordsProject { form = "chain" }),
                                  "HcSdTopEnch", "HcSdHeldSpell");

        Assert.Equal(new Dictionary<(string, string), int>
        {
            [(_w.Base.FileName.String, "MagicEffects")] = 1,
            [(_w.Base.FileName.String, "Spells")] = 1,
            [(_w.Top.FileName.String, "Spells")] = 1,
            [(_w.Top.FileName.String, "ObjectEffects")] = 1,
            [(_w.Held.FileName.String, "Spells")] = 1,
        }, d);
    }
}
