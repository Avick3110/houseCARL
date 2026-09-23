using HousecarlCore;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// Clearing a FormLink with a null synonym on apply (migrated from the <c>formlink-null-guard</c> probe). The four
/// synonyms ("0", "00000000", "Null", "000000:Null") clear a nullable link, the all-zeros form clears a required
/// one, and a real 6-hex FormID is never taken for a clear. Pure in-memory Mutagen: no world, no corpus.
/// </summary>
[Trait("tier", "unit")]
public sealed class FormLinkNullGuardApplyTests
{
    static Npc FreshNpc() =>
        new SkyrimMod(new ModKey("hc_formlink_null", ModType.Plugin), SkyrimRelease.SkyrimSE).Npcs.AddNew();

    static FormKey Fk(object link) => ((IFormLinkGetter)link).FormKey;

    static WriteRequest SetReq(string field, string value) =>
        new() { RecordType = "Npc", Path = new[] { field }, Verb = "Set", Value = value };

    static bool IsNullableFormLink(string prop)
    {
        var pt = typeof(INpc).GetProperty(prop)!.PropertyType;
        return pt.IsGenericType && pt.GetGenericTypeDefinition().Name.StartsWith("IFormLinkNullable", StringComparison.Ordinal);
    }

    // S0: "test fields have the expected nullability (Race required, DeathItem nullable)".
    [Fact]
    public void RaceIsARequiredLinkAndDeathItemANullableOne()
    {
        Assert.False(IsNullableFormLink("Race"));
        Assert.True(IsNullableFormLink("DeathItem"));
    }

    // A: "a real 6-hex FormID round-trips, not swallowed as a null-clear".
    [Fact]
    public void ARealSixHexFormIdIsNotTakenForAClear()
    {
        var npc = FreshNpc();

        WriteEngine.ApplyVerb(npc, SetReq("DeathItem", "012345:Skyrim.esm"));

        Assert.Equal(FormKey.Factory("012345:Skyrim.esm"), Fk(npc.DeathItem));
    }

    // D: "Set nullable FormLink = '<synonym>' clears it (no throw)".
    [Theory]
    [InlineData("00000000")]
    [InlineData("0")]
    [InlineData("Null")]
    [InlineData("000000:Null")]
    public void EachNullSynonymClearsANullableLink(string synonym)
    {
        var npc = FreshNpc();
        npc.DeathItem.SetTo(FormKey.Factory("012345:Skyrim.esm"));

        WriteEngine.ApplyVerb(npc, SetReq("DeathItem", synonym));

        Assert.True(Fk(npc.DeathItem).IsNull);
    }

    // E: "Set Race = 00000000 clears a REQUIRED FormLink (the new all-zeros required-clear)".
    [Fact]
    public void AllZerosClearsARequiredLink()
    {
        var npc = FreshNpc();
        npc.Race.SetTo(FormKey.Factory("012345:Skyrim.esm"));

        WriteEngine.ApplyVerb(npc, SetReq("Race", "00000000"));

        Assert.True(Fk(npc.Race).IsNull);
    }

    // F: "an all-zeros-cleared link serializes end-to-end to a valid patch". Read back, not only "the file exists".
    [Fact]
    public void AnAllZerosClearSurvivesSerialize()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hc-formlink-null-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var outPath = Path.Combine(dir, "hc_formlink_null_f.esp");
            var mod = new SkyrimMod(ModKey.FromFileName("hc_formlink_null_f.esp"), SkyrimRelease.SkyrimSE);
            var npc = mod.Npcs.AddNew();
            npc.Race.SetTo(FormKey.Factory("012345:Skyrim.esm"));
            WriteEngine.ApplyVerb(npc, SetReq("Race", "00000000"));

            WriteEngine.WritePatch(mod, new ISkyrimModGetter[] { mod }, outPath);

            using var back = SkyrimMod.CreateFromBinaryOverlay(outPath, SkyrimRelease.SkyrimSE);
            Assert.True(back.Npcs.Single().Race.IsNull);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}

/// <summary>
/// The pre-flight half of <c>formlink-null-guard</c>: a malformed FormLink value is refused at the gate instead of
/// passing and then throwing at apply. Corpus-only; the world is here for the corpus
/// <c>CorpusRulebook.CorpusPath</c> points at.
/// </summary>
[Trait("tier", "integration")]
[Collection("bulk-records")]
public sealed class FormLinkNullGuardPreflightTests : BulkRecordsTestBase
{
    public FormLinkNullGuardPreflightTests(BulkRecordsFixture f) : base(f) { }

    // C1: "pre-flight REJECTS a malformed FormLink value (was accept-then-throw)".
    [Fact]
    public void AMalformedFormLinkValueIsRefusedBeforeTheWrite()
    {
        var err = CorpusRulebook.Load().Validate(
            new WriteRequest { RecordType = "Npc", Path = new[] { "Race" }, Verb = "Set", Value = "notaformkey" });

        Assert.NotNull(err);
        Assert.Contains("FormLink", err, StringComparison.OrdinalIgnoreCase);
    }
}
