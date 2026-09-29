using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>Shared arrangement for the gendered [0]/[1] alias tests (migrated from the gendered-nav-guard probe).</summary>
static class GenderedNav
{
    public const string MaleNif = "hcgnav_male.nif";
    public const string FemaleNif = "hcgnav_female.nif";

    public static Armor FreshArmor() =>
        new SkyrimMod(new ModKey("hc_gendered_nav", ModType.Plugin), SkyrimRelease.SkyrimSE).Armors.AddNew();

    public static ArmorAddon FreshArmorAddon() =>
        new SkyrimMod(new ModKey("hc_gendered_nav", ModType.Plugin), SkyrimRelease.SkyrimSE).ArmorAddons.AddNew();

    public static WriteRequest Set(string recordType, string dotted, string value) => new()
    {
        RecordType = recordType,
        Path = dotted.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        Verb = "Set",
        Value = value,
    };

    public static FieldValue Read(IMajorRecordGetter rec, string path) =>
        ReadEngine.ReadFields(rec, new[] { path }, depth: 1).Fields[0];

    public static string? Tok(FieldValue? f) => f is { HasValue: true } ? f.Token : null;

    public static Armor ArmorWithBothArmsByName()
    {
        var a = FreshArmor();
        WriteEngine.ApplyVerb(a, Set("Armor", "WorldModel.Male.Model.File", MaleNif));
        WriteEngine.ApplyVerb(a, Set("Armor", "WorldModel.Female.Model.File", FemaleNif));
        return a;
    }
}

/// <summary>
/// A bracket read of a gendered field: [0]/[1] resolve to the named arms, the depth render emits paths that re-feed
/// to the shown value, and a real list element still navigates.
/// </summary>
[Trait("tier", "unit")]
public sealed class GenderedNavReadTests
{
    // A1: bracket READ WorldModel[0]/[1] == the named .Male/.Female read
    [Fact]
    public void ABracketReadOfIndexZeroMatchesTheNamedMaleRead()
    {
        var a = GenderedNav.ArmorWithBothArmsByName();
        var bracket = GenderedNav.Read(a, "WorldModel[0].Model.File");
        Assert.True(bracket.HasValue);
        Assert.Equal(GenderedNav.Tok(GenderedNav.Read(a, "WorldModel.Male.Model.File")), GenderedNav.Tok(bracket));
    }

    // A1: bracket READ WorldModel[0]/[1] == the named .Male/.Female read
    [Fact]
    public void ABracketReadOfIndexOneMatchesTheNamedFemaleRead()
    {
        var a = GenderedNav.ArmorWithBothArmsByName();
        var bracket = GenderedNav.Read(a, "WorldModel[1].Model.File");
        Assert.True(bracket.HasValue);
        Assert.Equal(GenderedNav.Tok(GenderedNav.Read(a, "WorldModel.Female.Model.File")), GenderedNav.Tok(bracket));
    }

    // A3: the rendered [0]/[1] path re-feeds to the shown value; [0]=Male, [1]=Female (render<->nav agree)
    [Fact]
    public void TheRenderedPairShowsMaleAtIndexZeroAndFemaleAtIndexOne()
    {
        var a = GenderedNav.ArmorWithBothArmsByName();
        var rendered = ReadEngine.ReadFields(a, new[] { "WorldModel" }, depth: 6).Fields;
        var r0 = rendered.FirstOrDefault(f => f.Path == "WorldModel[0].Model.File");
        var r1 = rendered.FirstOrDefault(f => f.Path == "WorldModel[1].Model.File");
        Assert.Contains("_male", GenderedNav.Tok(r0));
        Assert.Contains("female", GenderedNav.Tok(r1));
    }

    // A3: the rendered [0]/[1] path re-feeds to the shown value
    [Fact]
    public void TheRenderedIndexZeroPathReFeedsToTheShownValue()
    {
        var a = GenderedNav.ArmorWithBothArmsByName();
        var r0 = ReadEngine.ReadFields(a, new[] { "WorldModel" }, depth: 6).Fields
            .FirstOrDefault(f => f.Path == "WorldModel[0].Model.File");
        Assert.NotNull(GenderedNav.Tok(r0));
        Assert.Equal(GenderedNav.Tok(r0), GenderedNav.Tok(GenderedNav.Read(a, "WorldModel[0].Model.File")));
    }

    // C-LIST: a list element (Keywords[0]) still navigates after the refactor
    [Fact]
    public void AListElementStillNavigatesBesideTheGenderedBranch()
    {
        var a = GenderedNav.FreshArmor();
        WriteEngine.ApplyVerb(a, new WriteRequest { RecordType = "Armor", Path = new[] { "Keywords" }, Verb = "Add", Value = "012345:Skyrim.esm" });
        Assert.True(GenderedNav.Read(a, "Keywords[0]").HasValue);
    }
}

/// <summary>
/// A bracket write of a gendered arm materializes an absent pair and arm and writes it back to the record, so the
/// named arm reads it and it survives a serialize and reopen.
/// </summary>
[Trait("tier", "unit")]
public sealed class GenderedNavWriteTests
{
    // SET: GenderedItem<T>.Male is settable; named-from-null materializes + writes back
    [Fact]
    public void TheNamedMalePathFromNullMaterializesASettableArm()
    {
        var a = GenderedNav.FreshArmor();
        WriteEngine.ApplyVerb(a, GenderedNav.Set("Armor", "WorldModel.Male.Model.File", GenderedNav.MaleNif));
        Assert.NotNull(a.WorldModel?.Male);
        Assert.True(a.WorldModel!.GetType().GetProperty("Male")?.CanWrite);
    }

    // A2: bracket WRITE [1] lands AND is read back via the named arm (write-back, not an orphan)
    [Fact]
    public void ABracketWriteToAnAbsentArmIsReadBackThroughTheNamedArm()
    {
        var a = GenderedNav.FreshArmor();
        WriteEngine.ApplyVerb(a, GenderedNav.Set("Armor", "WorldModel[1].Model.File", GenderedNav.FemaleNif));
        var named = GenderedNav.Read(a, "WorldModel.Female.Model.File");
        Assert.Contains("female", GenderedNav.Tok(named));
        Assert.Equal(GenderedNav.Tok(named), GenderedNav.Tok(GenderedNav.Read(a, "WorldModel[1].Model.File")));
    }

    // A2: we only touched [1]; Male stays absent
    [Fact]
    public void ABracketWriteToIndexOneLeavesTheMaleArmAbsent()
    {
        var a = GenderedNav.FreshArmor();
        WriteEngine.ApplyVerb(a, GenderedNav.Set("Armor", "WorldModel[1].Model.File", GenderedNav.FemaleNif));
        Assert.NotNull(a.WorldModel);
        Assert.Null(a.WorldModel!.Male);
    }

    // SER: a bracket-written gendered arm persists through serialize->reopen (named-readable)
    [Fact]
    public void ABracketWrittenArmPersistsThroughSerializeAndReopen()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hc-gendered-nav-ser-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var outPath = Path.Combine(dir, "hc_gendered_nav_ser.esp");
            var mod = new SkyrimMod(new ModKey("hc_gendered_nav_ser", ModType.Plugin), SkyrimRelease.SkyrimSE);
            WriteEngine.ApplyVerb(mod.Armors.AddNew(), GenderedNav.Set("Armor", "WorldModel[1].Model.File", GenderedNav.FemaleNif));
            WriteEngine.WritePatch(mod, new ISkyrimModGetter[] { mod }, outPath);

            using var back = SkyrimMod.CreateFromBinaryOverlay(outPath, SkyrimRelease.SkyrimSE);
            Assert.Contains("female", GenderedNav.Tok(GenderedNav.Read(back.Armors.Single(), "WorldModel.Female.Model.File")));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ } }
    }
}

/// <summary>
/// Pre-flight and the engine agree on the gendered bracket: accepted as a hop, refused naming [0]/[1] for a bad
/// index, refused naming the by-name halves for a scalar arm or a leaf, and the branch does not leak to a plain
/// substruct or a list leaf.
/// </summary>
[Trait("tier", "integration")]
public sealed class GenderedNavRefusalTests
{
    readonly CorpusRulebook _rb = TestCorpus.Rulebook;

    // A2-PF: pre-flight ACCEPTS a gendered bracket write (was rejected as 'not a collection')
    [Fact]
    public void PreflightAcceptsAGenderedBracketWrite()
        => Assert.Null(_rb.Validate(GenderedNav.Set("Armor", "WorldModel[1].Model.File", GenderedNav.FemaleNif)));

    // A4-SUB: a non-gendered substruct bracket (BodyTemplate[0]) is refused at pre-flight
    [Fact]
    public void PreflightRefusesABracketOnANonGenderedSubstruct()
        => Assert.Contains("not a collection", _rb.Validate(GenderedNav.Set("Armor", "BodyTemplate[0].ArmorType", "LightArmor")));

    // A4-SUB: ... AND the engine
    [Fact]
    public void TheEngineRefusesABracketOnANonGenderedSubstruct()
        => Assert.ThrowsAny<Exception>(() =>
            WriteEngine.ApplyVerb(GenderedNav.FreshArmor(), GenderedNav.Set("Armor", "BodyTemplate[0].ArmorType", "LightArmor")));

    // A4-IDX: gendered index [2] is refused naming [0] male / [1] female, at pre-flight
    [Fact]
    public void PreflightRefusesGenderedIndexTwoNamingMale()
        => Assert.Contains("male", _rb.Validate(GenderedNav.Set("Armor", "WorldModel[2].Model.File", GenderedNav.MaleNif)));

    // A4-IDX: ... AND the engine
    [Fact]
    public void TheEngineRefusesGenderedIndexTwo()
        => Assert.ThrowsAny<Exception>(() =>
            WriteEngine.ApplyVerb(GenderedNav.FreshArmor(), GenderedNav.Set("Armor", "WorldModel[2].Model.File", GenderedNav.MaleNif)));

    // A4-SCALAR: stepping into a gendered scalar arm (Priority[0]) is refused, naming the by-name fix
    [Fact]
    public void PreflightRefusesSteppingIntoAGenderedScalarArmNamingByName()
        => Assert.Contains("by name", _rb.Validate(GenderedNav.Set("ArmorAddon", "Priority[0].Anything", "1")));

    // A4-SCALAR: stepping into a gendered formlink arm (SkinTexture[0]) is refused, naming the by-name fix
    [Fact]
    public void PreflightRefusesSteppingIntoAGenderedFormLinkArmNamingByName()
        => Assert.Contains("by name", _rb.Validate(GenderedNav.Set("ArmorAddon", "SkinTexture[0].Anything", "1")));

    // A4-LEAF: a gendered field at the LEAF (Priority[0]) is refused naming .Male/.Female, at pre-flight
    [Fact]
    public void PreflightRefusesAGenderedLeafBracketNamingTheMaleHalf()
        => Assert.Contains(".Male", _rb.Validate(GenderedNav.Set("ArmorAddon", "Priority[0]", "1")));

    // A4-LEAF: ... AND the engine
    [Fact]
    public void TheEngineRefusesAGenderedLeafBracketNamingTheMaleHalf()
    {
        var ex = Assert.ThrowsAny<Exception>(() =>
            WriteEngine.ApplyVerb(GenderedNav.FreshArmorAddon(), GenderedNav.Set("ArmorAddon", "Priority[0]", "1")));
        Assert.Contains("Priority.Male", ex.Message);
    }

    // C-LEAF: a list field at the leaf (Keywords[0]) keeps the generic leaf message at pre-flight (gendered branch gated)
    [Fact]
    public void PreflightKeepsTheGenericLeafMessageForAListLeafBracket()
    {
        var msg = _rb.Validate(GenderedNav.Set("Armor", "Keywords[0]", "012345:Skyrim.esm"));
        Assert.Contains("LEAF", msg);
        Assert.DoesNotContain("by name", msg);
    }

    // C-LEAF: ... AND the engine
    [Fact]
    public void TheEngineKeepsTheGenericLeafMessageForAListLeafBracket()
    {
        var ex = Assert.ThrowsAny<Exception>(() =>
            WriteEngine.ApplyVerb(GenderedNav.FreshArmor(), GenderedNav.Set("Armor", "Keywords[0]", "012345:Skyrim.esm")));
        Assert.Contains("LEAF", ex.Message);
        Assert.DoesNotContain("by name", ex.Message);
    }
}

/// <summary>
/// The shapes the alias rests on: Armor.WorldModel is a runtime gendered field with its arm type in the corpus,
/// ArmorAddon carries scalar gendered arms, and every descendable gendered arm in the corpus is a reference type, so a
/// sub-field write through the arm cannot land on a boxed copy.
/// </summary>
[Trait("tier", "integration")]
public sealed class GenderedArmShapeTests
{
    readonly CorpusRulebook _rb = TestCorpus.Rulebook;

    // S0: corpus has Armor + ArmorModel arm type; WorldModel is a runtime gendered field
    [Fact]
    public void WorldModelIsARuntimeGenderedFieldWithItsArmTypeInTheCorpus()
    {
        Assert.NotNull(_rb.Type("Armor"));
        Assert.NotNull(_rb.Type("ArmorModel"));
        var wm = typeof(IArmorGetter).GetProperty("WorldModel")?.PropertyType;
        Assert.NotNull(wm);
        Assert.True(IsGenderedType(wm!), wm!.Name);
    }

    // S0b: corpus has ArmorAddon with scalar gendered arms (Priority / SkinTexture)
    [Fact]
    public void TheCorpusCarriesArmorAddon()
        => Assert.NotNull(_rb.Type("ArmorAddon"));

    // VTYPE: all descendable gendered arms are reference types (write-back can't orphan)
    [Fact]
    public void EveryDescendableGenderedArmIsAReferenceType()
    {
        var offenders = new List<string>();
        int descendable = 0;
        foreach (var (tname, tschema) in CorpusRulebook.LoadCorpus(TestCorpus.Path).Types)
            foreach (var fld in tschema.Fields)
            {
                if (fld.Cardinality != "substruct" || fld.TypeRef is not { } tr
                    || !tr.StartsWith("GenderedItem<", StringComparison.Ordinal)) continue;
                var armRef = tr["GenderedItem<".Length..^1].Trim();
                if (armRef.Length == 0 || _rb.Type(armRef) is null) continue;   // scalar/value/formlink arm: descent is refused
                descendable++;
                var gt = Type.GetType(fld.MutableTypeAssemblyQualified ?? fld.GetterTypeAssemblyQualified ?? "");
                var arm = gt is { IsGenericType: true } ? gt.GetGenericArguments()[0] : null;
                if (arm is null) offenders.Add($"{tname}.{fld.Name} ({armRef}: arm runtime type unresolved)");
                else if (arm.IsValueType) offenders.Add($"{tname}.{fld.Name} ({armRef}: value type)");
            }
        Assert.True(descendable > 0, "no descendable gendered arm found in the corpus");
        Assert.Empty(offenders);
    }

    /// <summary>A local mirror of the engine recogniser, so this shape check does not rest on the code it guards.</summary>
    static bool IsGenderedType(Type t)
    {
        static bool IsGen(Type x) => x.IsGenericType
            && (x.GetGenericTypeDefinition().Name.StartsWith("GenderedItem", StringComparison.Ordinal)
                || x.GetGenericTypeDefinition().Name.StartsWith("IGenderedItem", StringComparison.Ordinal));
        return IsGen(t) || t.GetInterfaces().Any(IsGen);
    }
}
