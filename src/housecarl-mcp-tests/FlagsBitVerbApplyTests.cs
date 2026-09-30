using HousecarlCore;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>Apply of Remove on a [Flags] enum clears one bit and keeps the rest, Add and Remove are pure bit math,
/// and the result persists through serialization; a plain scalar still throws on Add. Migrated from the
/// flags-bit-verb-guard probe (A2, A3, APPLY-SCALAR and E2E arms; A1 is <c>FlagsBitVerbTests</c>).</summary>
[Trait("tier", "unit")]
public sealed class FlagsBitVerbApplyTests
{
    const Quest.Flag A = Quest.Flag.StartGameEnabled;
    const Quest.Flag B = Quest.Flag.AllowRepeatedStages;

    static Quest FreshQuest() =>
        new SkyrimMod(new ModKey("hc_flags_bit", ModType.Plugin), SkyrimRelease.SkyrimSE).Quests.AddNew();

    static void Apply(Quest q, string verb, string value) => WriteEngine.ApplyVerb(q,
        new WriteRequest { RecordType = "Quest", Path = new[] { "Flags" }, Verb = verb, Value = value });

    // A2: Remove clears ONLY its bit, other bits preserved (A|B set via a comma-combo Set)
    [Fact]
    public void RemoveClearsOnlyItsBitAfterACommaComboSet()
    {
        var q = FreshQuest();
        Apply(q, "Set", $"{A}, {B}");
        Assert.Equal(A | B, q.Flags);
        Apply(q, "Remove", B.ToString());
        Assert.Equal(A, q.Flags);
    }

    // A3: Add of a set bit / Remove of an unset bit are no-ops (OR/AND-NOT, not toggle)
    [Fact]
    public void AddOfASetBitAndRemoveOfAnUnsetBitAreNoOps()
    {
        var q = FreshQuest();
        Apply(q, "Set", A.ToString());
        Apply(q, "Add", A.ToString());
        Assert.Equal(A, q.Flags);
        Apply(q, "Remove", B.ToString());
        Assert.Equal(A, q.Flags);
    }

    // APPLY-SCALAR: Add on a plain scalar still throws at apply (the [Flags] branch is scoped)
    [Fact]
    public void AddOnAPlainScalarStillThrowsAtApply()
    {
        var w = new SkyrimMod(new ModKey("hc_flags_ctrl", ModType.Plugin), SkyrimRelease.SkyrimSE).Weapons.AddNew();
        w.BasicStats = new WeaponBasicStats { Damage = 7 };
        var ex = Assert.Throws<InvalidOperationException>(() => WriteEngine.ApplyVerb(w,
            new WriteRequest { RecordType = "Weapon", Path = new[] { "BasicStats", "Damage" }, Verb = "Add", Value = "5" }));
        Assert.Contains("not valid", ex.Message, StringComparison.Ordinal);
        Assert.Equal((ushort)7, w.BasicStats.Damage);
    }

    // E2E: Set+Add serializes AND re-reads as the UNION of both flags (persists through serialization)
    [Fact]
    public void SetThenAddReReadsAsTheUnionAfterSerialize()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hc-flags-bit-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var outPath = Path.Combine(dir, "hc_flags_bit_e2e.esp");
            var mod = new SkyrimMod(ModKey.FromFileName("hc_flags_bit_e2e.esp"), SkyrimRelease.SkyrimSE);
            var q = mod.Quests.AddNew();
            Apply(q, "Set", A.ToString());
            Apply(q, "Add", B.ToString());

            WriteEngine.WritePatch(mod, new ISkyrimModGetter[] { mod }, outPath);

            using var back = SkyrimMod.CreateFromBinaryOverlay(outPath, SkyrimRelease.SkyrimSE);
            Assert.Equal(A | B, back.Quests.Single().Flags);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
