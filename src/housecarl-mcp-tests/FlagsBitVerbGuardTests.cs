using HousecarlCore;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>Pre-flight on Add and Remove against a [Flags] enum (HCBR-2026-07-15): a flag name is admitted as a bit
/// set or clear, a bogus flag or a stray key is refused, a plain scalar still refuses Add, and a valueless Remove keeps
/// its whole-clear meaning on a nullable flags field. Migrated from the flags-bit-verb-guard probe (PRE-* and
/// *-VALUELESS arms).</summary>
[Trait("tier", "integration")]
public sealed class FlagsBitVerbGateTests
{
    static string? Check(WriteRequest req) => TestCorpus.Rulebook.Validate(req);

    static WriteRequest QuestFlags(string verb, string? value, string? key = null) =>
        new() { RecordType = "Quest", Path = new[] { "Flags" }, Verb = verb, Value = value, Key = key };

    // PRE-ACCEPT: pre-flight ACCEPTS Add of a flag on Quest.Flags
    [Fact]
    public void AddOfAFlagIsAccepted() => Assert.Null(Check(QuestFlags("Add", nameof(Quest.Flag.StartGameEnabled))));

    // PRE-ACCEPT: pre-flight ACCEPTS Remove of a flag on Quest.Flags
    [Fact]
    public void RemoveOfAFlagIsAccepted() => Assert.Null(Check(QuestFlags("Remove", nameof(Quest.Flag.StartGameEnabled))));

    // PRE-BADFLAG: pre-flight REFUSES Add of a bogus flag value (no accept-then-throw)
    [Fact]
    public void AddOfABogusFlagIsRefusedNamingIt()
    {
        var reject = Check(QuestFlags("Add", "NotARealQuestFlag"));
        Assert.NotNull(reject);
        Assert.Contains("NotARealQuestFlag", reject);
    }

    // PRE-KEY: pre-flight REFUSES a flags Add with a stray key
    [Fact]
    public void AddWithAStrayKeyIsRefused()
    {
        var reject = Check(QuestFlags("Add", nameof(Quest.Flag.StartGameEnabled), key: "0"));
        Assert.NotNull(reject);
        Assert.Contains("no key", reject, StringComparison.OrdinalIgnoreCase);
    }

    // PRE-SCALAR: pre-flight still REFUSES Add on a plain scalar (Weapon.BasicStats.Damage), names '[Flags]'
    [Fact]
    public void AddOnAPlainScalarIsStillRefused()
    {
        var reject = Check(new WriteRequest { RecordType = "Weapon", Path = new[] { "BasicStats", "Damage" }, Verb = "Add", Value = "5" });
        Assert.NotNull(reject);
        Assert.Contains("[Flags]", reject, StringComparison.Ordinal);
    }

    // NONNULL-VALUELESS: valueless Remove on non-nullable Quest.Flags is REFUSED, names the Set '0' redirect
    [Fact]
    public void ValuelessRemoveOnANonNullableFlagsFieldRedirectsToSetZero()
    {
        var reject = Check(QuestFlags("Remove", null));
        Assert.NotNull(reject);
        Assert.Contains("'0'", reject, StringComparison.Ordinal);
    }

    // NULLABLE-VALUELESS: valueless Remove on a nullable flags field (Activator.Flags) is ACCEPTED (whole-clear preserved)
    [Fact]
    public void ValuelessRemoveOnANullableFlagsFieldIsAccepted()
    {
        var field = TestCorpus.Rulebook.Type("Activator")!.Fields.Single(f => f.Name == "Flags");
        Assert.True(field.Nullable);
        Assert.Null(Check(new WriteRequest { RecordType = "Activator", Path = new[] { "Flags" }, Verb = "Remove", Value = null }));
    }

    // Not a probe arm: a valueless Add on a flags field is refused, naming the missing value.
    [Fact]
    public void ValuelessAddIsRefused()
    {
        var reject = Check(QuestFlags("Add", null));
        Assert.NotNull(reject);
        Assert.Contains("requires a flag value", reject, StringComparison.Ordinal);
    }
}

/// <summary>Apply of Add and Remove on a [Flags] enum ORs or clears one bit and keeps the rest, persisting through
/// serialization; a plain scalar still throws on Add. Migrated from the flags-bit-verb-guard probe (A1, A2, A3,
/// APPLY-SCALAR and E2E arms).</summary>
[Trait("tier", "unit")]
public sealed class FlagsBitVerbApplyTests
{
    const Quest.Flag A = Quest.Flag.StartGameEnabled;
    const Quest.Flag B = Quest.Flag.AllowRepeatedStages;

    static Quest FreshQuest() =>
        new SkyrimMod(new ModKey("hc_flags_bit", ModType.Plugin), SkyrimRelease.SkyrimSE).Quests.AddNew();

    static void Apply(Quest q, string verb, string value) => WriteEngine.ApplyVerb(q,
        new WriteRequest { RecordType = "Quest", Path = new[] { "Flags" }, Verb = verb, Value = value });

    // A1: Add ORs a bit in and PRESERVES the pre-existing bit (no silent clobber)
    [Fact]
    public void AddOrsABitInAndKeepsTheOneAlreadySet()
    {
        var q = FreshQuest();
        Apply(q, "Set", A.ToString());
        Assert.Equal(A, q.Flags);
        Apply(q, "Add", B.ToString());
        Assert.Equal(A | B, q.Flags);
    }

    // A2: Remove clears ONLY its bit, other bits preserved (A|B set via a comma-combo Set)
    [Fact]
    public void RemoveClearsOnlyItsBit()
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
