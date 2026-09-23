using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// Path refusals (Stryker row T12): a malformed segment or an index the list cannot take is refused by the word that
/// says what to fix, and an index past the end is an expected refusal, not an engine fault.
/// </summary>
[Trait("tier", "unit")]
public sealed class PathSegmentRefusalTests
{
    static Weapon WeaponWithOneKeyword()
    {
        var mod = new SkyrimMod(new ModKey("HcPathMod", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var kw = mod.Keywords.AddNew("HcPathKw");
        var w = mod.Weapons.AddNew("HcPathWeap");
        w.Keywords = new Noggog.ExtendedList<IFormLinkGetter<IKeywordGetter>> { kw.ToLink() };
        return w;
    }

    static Exception Refusal(params string[] path) => Assert.ThrowsAny<Exception>(() =>
        WriteEngine.ApplyVerb(WeaponWithOneKeyword(), new WriteRequest
        {
            RecordType = "Weapon", Path = path, Verb = "Set", Value = "000800:HcPathMod.esp",
        }));

    [Fact]
    public void ASegmentThatIsOnlyAnIndexIsRefusedForItsMissingName()
        => Assert.Contains("no field name", Refusal("[0]", "FormKey").Message);

    [Fact]
    public void AnUnclosedBracketIsRefusedNamingTheClose()
        => Assert.Contains("closed by ']'", Refusal("Keywords[0", "FormKey").Message);

    [Fact]
    public void ABracketInsideAKeyIsRefusedAsNested()
        => Assert.Contains("nested", Refusal("Keywords[b[c]", "FormKey").Message);

    [Fact]
    public void ANegativeIndexIsRefusedAsNotNonNegative()
        => Assert.Contains("non-negative", Refusal("Keywords[-1]", "FormKey").Message);

    [Fact]
    public void AQuantifierInAWritePathIsSentToWhere()
        => Assert.Contains("where=", Refusal("Keywords[*any]", "FormKey").Message);

    [Fact]
    public void AnIndexPastTheEndIsAnExpectedRefusal()
    {
        var ex = Refusal("Keywords[5]", "FormKey");
        Assert.IsType<ExpectedApplyRejectionException>(ex);
        Assert.Contains("out of bounds", ex.Message);
    }
}

/// <summary>
/// The gendered [0]/[1] alias (Stryker row T13): mid-path it maps to the Male/Female arm and materializes a missing
/// one on a write, at the leaf it is refused toward the named arm, and a read through a getter overlay navigates it.
/// </summary>
[Trait("tier", "unit")]
public sealed class GenderedIndexTests
{
    static Armor BareArmor(out SkyrimMod mod)
    {
        mod = new SkyrimMod(new ModKey("HcGenderMod", ModType.Plugin), SkyrimRelease.SkyrimSE);
        return mod.Armors.AddNew("HcGenderArmor");
    }

    static void SetModelFile(Armor armor, string index, string file) =>
        WriteEngine.ApplyVerb(armor, new WriteRequest
        {
            RecordType = "Armor", Path = new[] { $"WorldModel[{index}]", "Model", "File" }, Verb = "Set", Value = file,
        });

    [Fact]
    public void IndexZeroOnAnArmorWithNoWorldModelSetsTheMaleArm()
    {
        var armor = BareArmor(out _);
        SetModelFile(armor, "0", @"armor\male.nif");
        Assert.EndsWith("male.nif", armor.WorldModel?.Male?.Model?.File.GivenPath);
    }

    [Fact]
    public void IndexOneOnAnArmorWithNoWorldModelSetsTheFemaleArm()
    {
        var armor = BareArmor(out _);
        SetModelFile(armor, "1", @"armor\female.nif");
        Assert.EndsWith("female.nif", armor.WorldModel?.Female?.Model?.File.GivenPath);
    }

    [Fact]
    public void AGenderedIndexAtTheLeafIsSentToTheNamedArm()
    {
        var armor = BareArmor(out _);
        var ex = Assert.ThrowsAny<Exception>(() => WriteEngine.ApplyVerb(armor, new WriteRequest
        {
            RecordType = "Armor", Path = new[] { "WorldModel[0]" }, Verb = "Set", Value = "x",
        }));
        Assert.Contains("WorldModel.Male", ex.Message);
    }

    [Fact]
    public void AReadOfIndexOneThroughAGetterOverlayNavigatesToTheFemaleArm()
    {
        var armor = BareArmor(out var mod);
        SetModelFile(armor, "1", @"armor\female.nif");
        var dir = Path.Combine(Path.GetTempPath(), "hc-gender-read-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "HcGenderMod.esp");
            mod.BeginWrite.ToPath(path).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
            using var back = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
            var overlay = back.Armors.Single();
            var prop = WriteEngine.ResolveProperty(overlay.GetType(), "WorldModel")!;

            var arm = WriteEngine.StepIntoElement(overlay, prop, "WorldModel", "1");
            Assert.EndsWith("female.nif", ((IArmorModelGetter)arm).Model?.File.GivenPath);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}

/// <summary>
/// A bracket on the leaf of an element-keyed collection (Stryker row T14): the refusal names the remedy, the verb
/// plus a key on the collection field.
/// </summary>
[Trait("tier", "unit")]
public sealed class LeafBracketRefusalTests
{
    [Fact]
    public void AKeyedLeafBracketIsSentToTheVerbPlusKey()
    {
        var mod = new SkyrimMod(new ModKey("HcLeafMod", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var kw = mod.Keywords.AddNew("HcLeafKw");
        var w = mod.Weapons.AddNew("HcLeafWeap");
        w.Keywords = new Noggog.ExtendedList<IFormLinkGetter<IKeywordGetter>> { kw.ToLink() };

        var ex = Assert.ThrowsAny<Exception>(() => WriteEngine.ApplyVerb(w, new WriteRequest
        {
            RecordType = "Weapon", Path = new[] { "Keywords[0]" }, Verb = "Remove",
        }));
        Assert.Contains("use the verb + Key", ex.Message);
    }
}

/// <summary>
/// A read that keys into a dictionary-typed field through a getter overlay (Stryker row T22): the overlay exposes the
/// read-only dictionary interface, and the step navigates it by key.
/// </summary>
[Trait("tier", "unit")]
public sealed class DictionaryReadStepTests
{
    [Fact]
    public void AGetterOverlayReadOfARacesBipedObjectNameByKeyNavigates()
    {
        var mod = new SkyrimMod(new ModKey("HcDictMod", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var race = mod.Races.AddNew("HcDictRace");
        race.BipedObjectNames[BipedObject.Head] = "HcHead";

        var dir = Path.Combine(Path.GetTempPath(), "hc-dict-read-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "HcDictMod.esp");
            mod.BeginWrite.ToPath(path).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
            using var back = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
            var overlay = back.Races.Single();
            var prop = WriteEngine.ResolveProperty(overlay.GetType(), "BipedObjectNames")!;

            Assert.Equal("HcHead", WriteEngine.StepIntoElement(overlay, prop, "BipedObjectNames", "Head"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
