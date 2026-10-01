using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>Remove on a nullable FormLink clears it to the empty link instead of throwing; Remove on a required one is
/// refused by pre-flight and, for a caller that bypasses pre-flight, fails loud at apply. Migrated from the
/// formlink-remove-guard probe.</summary>
[Trait("tier", "unit")]
public sealed class FormLinkRemoveTests
{
    static Npc FreshNpc() => new SkyrimMod(new ModKey("HcFormLinkRemove", ModType.Plugin), SkyrimRelease.SkyrimSE).Npcs.AddNew();
    static FormKey Fk(object link) => ((IFormLinkGetter)link).FormKey;
    static WriteRequest SetReq(string field, string value) => new() { RecordType = "Npc", Path = new[] { field }, Verb = "Set", Value = value };
    static WriteRequest RemReq(string field) => new() { RecordType = "Npc", Path = new[] { field }, Verb = "Remove" };

    static bool IsNullableFormLink(string prop)
    {
        var pt = typeof(INpc).GetProperty(prop)?.PropertyType;
        return pt is { IsGenericType: true } && pt.GetGenericTypeDefinition().Name.StartsWith("IFormLinkNullable", StringComparison.Ordinal);
    }

    // S0: test fields have the expected nullability (HeadTexture + DeathItem nullable, Race required)
    [Fact]
    public void TheFieldsUnderTestHaveTheNullabilityTheTestsAssume()
    {
        Assert.True(IsNullableFormLink("HeadTexture"));
        Assert.True(IsNullableFormLink("DeathItem"));
        Assert.False(IsNullableFormLink("Race"));
    }

    // PRE-NULLABLE: pre-flight ACCEPTS Remove on a nullable FormLink (HeadTexture, DeathItem)
    [Theory]
    [InlineData("HeadTexture")]
    [InlineData("DeathItem")]
    public void PreflightAcceptsRemoveOnANullableLink(string field) => Assert.Null(TestCorpus.Rulebook.Validate(RemReq(field)));

    // PRE-REQUIRED: pre-flight REFUSES Remove on a required FormLink (Race), names 'non-nullable'
    [Fact]
    public void PreflightRefusesRemoveOnARequiredLinkNamingNonNullable()
        => Assert.Contains("non-nullable", TestCorpus.Rulebook.Validate(RemReq("Race")), StringComparison.OrdinalIgnoreCase);

    // R1: Remove clears a populated nullable FormLink with no throw
    [Theory]
    [InlineData("HeadTexture")]
    [InlineData("DeathItem")]
    public void RemoveClearsAPopulatedNullableLink(string field)
    {
        var npc = FreshNpc();
        var prop = typeof(Npc).GetProperty(field)!;
        WriteEngine.ApplyVerb(npc, SetReq(field, "012345:Skyrim.esm"));
        Assert.False(Fk(prop.GetValue(npc)!).IsNull);

        WriteEngine.ApplyVerb(npc, RemReq(field));

        Assert.True(Fk(prop.GetValue(npc)!).IsNull);
    }

    // GUARD-REQUIRED: Remove on a required FormLink (Race) throws loud, does NOT silently blank
    [Fact]
    public void RemoveOnARequiredLinkAtApplyThrowsAndLeavesTheLink()
    {
        var npc = FreshNpc();
        WriteEngine.ApplyVerb(npc, SetReq("Race", "012345:Skyrim.esm"));

        var ex = Assert.Throws<InvalidOperationException>(() => WriteEngine.ApplyVerb(npc, RemReq("Race")));

        Assert.Contains("required", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(npc.Race.FormKey.IsNull);
    }

    // R2: a Remove-cleared nullable FormLink serializes AND re-reads as FormKey.Null
    [Fact]
    public void ARemoveClearedLinkReReadsFromDiskAsNull()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hc-formlink-remove-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "HcFormLinkRemoveE2e.esp");
            var mod = new SkyrimMod(ModKey.FromFileName("HcFormLinkRemoveE2e.esp"), SkyrimRelease.SkyrimSE);
            var npc = mod.Npcs.AddNew();
            WriteEngine.ApplyVerb(npc, SetReq("HeadTexture", "012345:Skyrim.esm"));
            WriteEngine.ApplyVerb(npc, RemReq("HeadTexture"));
            WriteEngine.WritePatch(mod, new ISkyrimModGetter[] { mod }, path);

            using var back = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
            Assert.True(back.Npcs.First().HeadTexture.FormKey.IsNull);
        }
        finally { try { Directory.Delete(dir, true); } catch { /* temp cleanup best-effort */ } }
    }
}
