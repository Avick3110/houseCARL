using HousecarlCore;
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

    // Not a probe arm: the Remove twin of PRE-KEY, a flags Remove with a stray key is refused.
    [Fact]
    public void RemoveWithAStrayKeyIsRefused()
    {
        var reject = Check(QuestFlags("Remove", nameof(Quest.Flag.StartGameEnabled), key: "0"));
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
        // The field is a nullable [Flags] enum in the corpus, and the gate takes it as one (a flag Add is admitted).
        var field = TestCorpus.Rulebook.Type("Activator")!.Fields.Single(f => f.Name == "Flags");
        Assert.True(field.Nullable);
        Assert.Equal("enum", field.Cardinality);
        Assert.Contains("Activator+Flag", field.MutableTypeAssemblyQualified ?? field.GetterTypeAssemblyQualified, StringComparison.Ordinal);
        Assert.True(typeof(Mutagen.Bethesda.Skyrim.Activator.Flag).IsDefined(typeof(FlagsAttribute), false));
        var aFlag = Enum.GetValues<Mutagen.Bethesda.Skyrim.Activator.Flag>().First(v => v != 0).ToString();
        Assert.Null(Check(new WriteRequest { RecordType = "Activator", Path = new[] { "Flags" }, Verb = "Add", Value = aFlag }));
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
