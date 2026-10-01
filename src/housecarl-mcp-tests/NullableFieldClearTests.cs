using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// Remove clears a nullable substruct or polymorphic field: the generator reads the field's nullability, pre-flight
/// permits Remove on a nullable one and refuses it on a non-nullable one, and the engine's Remove sets the property
/// null. World-free: pre-flight against the test corpus, the engine against a record in memory.
/// </summary>
[Trait("tier", "unit")]
public sealed class NullableFieldClearTests
{
    static WriteRequest Remove(string type, string field) => new() { RecordType = type, Path = new[] { field }, Verb = "Remove" };

    // Probe arm PREFLIGHT-VMAD.
    [Fact]
    public void RemoveOnTheNullableVmadSubstructPassesPreflight() =>
        Assert.Null(TestCorpus.Rulebook.Validate(Remove("DialogResponses", "VirtualMachineAdapter")));

    // Probe arm PREFLIGHT-PROMPT: a second nullable substruct, so the rule is general.
    [Fact]
    public void RemoveOnAnotherNullableSubstructPassesPreflight() =>
        Assert.Null(TestCorpus.Rulebook.Validate(Remove("DialogResponses", "Prompt")));

    // Probe arm CONTROL-OBJBOUNDS: a non-nullable substruct still refuses, naming 'non-nullable'.
    [Fact]
    public void RemoveOnANonNullableSubstructIsRefused() =>
        Assert.Contains("non-nullable", TestCorpus.Rulebook.Validate(Remove("Armor", "ObjectBounds")), StringComparison.OrdinalIgnoreCase);

    // Probe arm CONTROL-SET: a plain-value Set on the VMAD substruct is still refused, pointed at composing.
    [Fact]
    public void APlainValueSetOnTheVmadSubstructIsRefusedAsNeedingACompose() =>
        Assert.Contains("composing", TestCorpus.Rulebook.Validate(new WriteRequest
        {
            RecordType = "DialogResponses", Path = new[] { "VirtualMachineAdapter" }, Verb = "Set", Value = "0",
        }), StringComparison.Ordinal);

    // Probe arm E2E-UNFRAGMENT: Remove through the engine clears an INFO's VMAD.
    [Fact]
    public void TheEnginesRemoveClearsAnInfosVmad()
    {
        var info = new DialogResponses(new FormKey(new ModKey("HcSncClear", ModType.Plugin), 0x800), SkyrimRelease.SkyrimSE)
        {
            VirtualMachineAdapter = new DialogResponsesAdapter(),
        };

        WriteEngine.ApplyVerb(info, Remove("DialogResponses", "VirtualMachineAdapter"));

        Assert.Null(info.VirtualMachineAdapter);
    }

    // Probe arms PREFLIGHT-POLY and PREFLIGHT-POLY2: nullable polymorphic fields pass pre-flight.
    [Theory]
    [InlineData("Book", "Teaches")]
    [InlineData("Npc", "Sound")]
    public void RemoveOnANullablePolymorphicFieldPassesPreflight(string type, string field) =>
        Assert.Null(TestCorpus.Rulebook.Validate(Remove(type, field)));

    // Probe arm CONTROL-POLY-REQ: a non-nullable polymorphic field still refuses, naming 'non-nullable'.
    [Fact]
    public void RemoveOnANonNullablePolymorphicFieldIsRefused() =>
        Assert.Contains("non-nullable", TestCorpus.Rulebook.Validate(Remove("MagicEffect", "Archetype")), StringComparison.OrdinalIgnoreCase);

    // Probe arm E2E-POLY: Remove through the engine clears a Book's Teaches arm.
    [Fact]
    public void TheEnginesRemoveClearsABooksTeachesArm()
    {
        var book = new Book(new FormKey(new ModKey("HcSncClear", ModType.Plugin), 0x801), SkyrimRelease.SkyrimSE)
        {
            Teaches = new BookSkill(),
        };

        WriteEngine.ApplyVerb(book, Remove("Book", "Teaches"));

        Assert.Null(book.Teaches);
    }
}
