using System.Text.RegularExpressions;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Assets;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlGenerator;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// Migrated from the arm-classification-guard probe (#397, #424): <see cref="CorpusGenerator.ClassifyArm"/> tells a
/// read-only projection from a writable type with no own-named getter interface, the anomaly line for the second
/// says only what it measured, the record-path emit prints that line, and <see cref="CorpusGenerator.GetterInterfaceFor"/>
/// is arity-aware. The exhibits are named on purpose: a Mutagen bump that changes their shape should break these.
/// </summary>
[Trait("tier", "unit")]
public sealed class ArmClassificationGuardTests
{
    static readonly System.Reflection.Assembly Skyrim = typeof(IArmorGetter).Assembly;

    // FullName lookup: many nested types share a simple name.
    static Type Exhibit(string fullName) =>
        Skyrim.GetTypes().FirstOrDefault(t => t.FullName == fullName)
        ?? throw new InvalidOperationException($"{fullName} is gone from Mutagen; pick a new exhibit of the same shape.");

    const string Unextractable = "Mutagen.Bethesda.Skyrim.ArmorAddonWeightSliderContainer";
    const string Projection = "Mutagen.Bethesda.Skyrim.SkyrimMultiModOverlay";

    // B1-B3. named live types reach Authorable and ReadOnlyProjection
    [Theory]
    [InlineData("Mutagen.Bethesda.Skyrim.Armor", "Authorable")]
    [InlineData(Projection, "ReadOnlyProjection")]
    [InlineData("Mutagen.Bethesda.Skyrim.MergedCellBlock", "ReadOnlyProjection")]
    public void ALiveExhibitClassifiesAsItsArmClass(string fullName, string expected) =>
        Assert.Equal(expected, CorpusGenerator.ClassifyArm(Exhibit(fullName)).ToString());

    // B4/B5. the writable-but-unextractable branch is entered by a live type, and is a distinct verdict from a projection
    [Fact]
    public void AWritableTypeWithNoOwnGetterClassifiesWritableButUnextractable()
    {
        Assert.Equal(CorpusGenerator.ArmClass.WritableButUnextractable, CorpusGenerator.ClassifyArm(Exhibit(Unextractable)));
        Assert.NotEqual(CorpusGenerator.ClassifyArm(Exhibit(Projection)), CorpusGenerator.ClassifyArm(Exhibit(Unextractable)));
    }

    // C1-C3, C6. the unextractable line is marked, names the full type, carries a labelled non-zero count and the settling check
    [Fact]
    public void TheUnextractableLineStatesItsMeasurement()
    {
        var line = CorpusGenerator.UnextractableWarning("union arm", Exhibit(Unextractable));
        Assert.Contains("UNEXTRACTABLE BY NAME", line);
        Assert.Contains(Unextractable, line);
        Assert.Matches(new Regex(@"and [1-9][0-9]* of [0-9]+ properties are authorable \(public settable, or a mutable collection\)"), line);
        Assert.Contains(CorpusGenerator.ReachabilityCheck, line);
    }

    // C4/C5. the line does not diagnose a coverage gap or assign the fix upstream
    [Fact]
    public void TheUnextractableLineDoesNotDiagnoseMoreThanItMeasured()
    {
        var line = CorpusGenerator.UnextractableWarning("union arm", Exhibit(Unextractable));
        Assert.DoesNotContain("coverage gap, not a read-only projection", line);
        Assert.DoesNotContain("is a real coverage gap", line);
        Assert.DoesNotContain("belongs upstream", line);
    }

    // C7/C8. the zero-authorable line is not marked unextractable, and both arms close on the same reachability check
    [Fact]
    public void TheZeroAuthorableLineIsUnmarkedAndClosesOnTheSameCheck()
    {
        var line = CorpusGenerator.UnextractableWarning("union arm", Exhibit(Projection));
        Assert.DoesNotContain("UNEXTRACTABLE BY NAME", line);
        Assert.Contains(CorpusGenerator.ReachabilityCheck, line);
    }

    // E1. a generic modeled type resolves its arity-matched getter interface
    [Fact]
    public void AnOpenGenericResolvesItsArityMatchedGetter()
    {
        var gi = CorpusGenerator.GetterInterfaceFor(typeof(GenderedItem<>));
        Assert.NotNull(gi);
        Assert.Equal(typeof(IGenderedItemGetter<>), gi!.GetGenericTypeDefinition());
    }

    // E2. the record-group container resolves the definition IsList names, by full name
    [Fact]
    public void TheGroupContainerResolvesTheDefinitionIsListNames()
    {
        var gi = CorpusGenerator.GetterInterfaceFor(typeof(SkyrimGroup<>));
        Assert.Equal(typeof(ISkyrimGroupGetter<>), gi);
        Assert.Equal("Mutagen.Bethesda.Skyrim.ISkyrimGroupGetter`1", gi!.FullName);
    }

    // E3. a closed generic resolves the closed interface, not the open definition
    [Fact]
    public void AClosedGenericResolvesTheClosedInterface() =>
        Assert.Equal(typeof(IGenderedItemGetter<bool>), CorpusGenerator.GetterInterfaceFor(typeof(GenderedItem<bool>)));

    // E4. the normalized fallback prefers the arity match whatever order GetInterfaces() lists
    [Theory]
    [InlineData(typeof(FormLinkGetter<>))]
    [InlineData(typeof(AssetLinkGetter<>))]
    public void TheFallbackPrefersTheArityMatchInEitherOrder(Type t)
    {
        var forward = CorpusGenerator.OwnNamedGetterAmong(t, t.GetInterfaces());
        var reversed = CorpusGenerator.OwnNamedGetterAmong(t, Enumerable.Reverse(t.GetInterfaces()));
        Assert.True(forward is { IsGenericType: true }, $"got {forward?.Name ?? "null"}");
        Assert.Equal(forward, reversed);
    }
}

/// <summary>
/// Migrated from arm-classification-guard's D arms: the record-path coverage anomaly is actually printed. Reads
/// console output, so it runs outside the parallel pool; another test swapping Console.Out would steal the capture.
/// </summary>
[Collection("arm-classification-emit")]
[Trait("tier", "unit")]
public sealed class ArmClassificationEmitTests
{
    // D0, D0b, D1. every no-getter-interface record class the seed loop enumerates is named in the untruncated anomaly report
    [Fact]
    public void EveryNoGetterRecordClassIsNamedInTheCoverageAnomalyReport()
    {
        var expected = typeof(IArmorGetter).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && !CorpusGenerator.IsOverlayTwin(t))
            .Where(t => typeof(IMajorRecordGetter).IsAssignableFrom(t))
            .Where(t => CorpusGenerator.GetterInterfaceFor(t) == null)
            .Select(t => t.FullName ?? t.Name)
            .ToList();
        Assert.NotEmpty(expected);

        var root = Path.Combine(Path.GetTempPath(), "hc-armclass-emit-" + Guid.NewGuid().ToString("N"));
        var captured = new StringWriter();
        var realOut = Console.Out;
        try
        {
            Console.SetOut(captured);
            CorpusGenerator.GenerateAll(Path.Combine(root, "generated"), Path.Combine(root, "refs"));
        }
        finally
        {
            Console.SetOut(realOut);
            try { Directory.Delete(root, recursive: true); } catch { }
        }

        var text = captured.ToString();
        var start = text.IndexOf("COVERAGE ANOMALIES", StringComparison.Ordinal);
        Assert.True(start >= 0, "the emit printed no COVERAGE ANOMALIES section");
        var section = text[start..];
        var end = section.IndexOf("ANOMALIES / things to inspect", StringComparison.Ordinal);
        if (end >= 0) section = section[..end];

        var missing = expected.Where(n => !section.Contains(n, StringComparison.Ordinal)).ToList();
        Assert.True(missing.Count == 0, "not named in the anomaly report: " + string.Join(", ", missing));
    }
}

[CollectionDefinition("arm-classification-emit", DisableParallelization = true)]
public sealed class ArmClassificationEmitCollection { }
