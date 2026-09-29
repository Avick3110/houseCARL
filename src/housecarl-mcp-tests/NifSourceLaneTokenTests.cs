using System.Text.RegularExpressions;
using HousecarlMcp;
using Xunit;
using W = HousecarlMcpTests.NifSourceLaneWorld;

namespace HousecarlMcpTests;

/// <summary>#340: every provider name nif_inspect prints, taken back out of the chain by its DELIMITER and fed straight
/// into source_provider=, selects that provider. The old render printed <c>SomeMod (loose)</c>, which contains the
/// accepted <c>SomeMod</c> and refuses when passed back, so a substring check would not catch it.</summary>
[Trait("tier", "integration")]
public sealed class NifSourceLaneTokenTests : IClassFixture<NifSourceLaneWorld>
{
    readonly NifSourceLaneWorld _w;
    public NifSourceLaneTokenTests(NifSourceLaneWorld w) => _w = w;

    static string ProvidersLine(string text) => text.Split('\n').First(l => l.Contains("providers")).Trim();

    static List<string> Printed(string text)
        => Regex.Matches(ProvidersLine(text), "\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();

    // Probe: "both providers are listed".
    [Fact]
    public void BothProvidersAreListed()
        => Assert.Contains("providers (2)", NifTools.NifInspect(_w.Svc, new[] { W.FaceRel }));

    // Probe: "the names come out of the chain by DELIMITER, hostile characters intact".
    [Fact]
    public void TheNamesComeOutOfTheChainByDelimiterWithHostileCharactersIntact()
    {
        var printed = Printed(NifTools.NifInspect(_w.Svc, new[] { W.FaceRel }));

        Assert.Equal(new[] { W.LooseMod, "Test.bsa" }, printed.OrderBy(n => n, StringComparer.Ordinal));
    }

    // Probe: "round trip: the printed '<name>' selects that provider".
    [Fact]
    public void EachPrintedNameSelectsThatProviderWhenFedBack()
    {
        var printed = Printed(NifTools.NifInspect(_w.Svc, new[] { W.FaceRel }));
        Assert.Equal(2, printed.Count);

        foreach (var name in printed)
            Assert.Contains("read from: \"" + name + "\"", NifTools.NifInspect(_w.Svc, new[] { W.FaceRel }, source_provider: name));
    }

    // Probe: "the kind annotation is NOT part of the name — passing it refuses rather than resolving anyway".
    [Fact]
    public void TheKindAnnotationIsNotPartOfTheName()
    {
        var text = NifTools.NifInspect(_w.Svc, new[] { W.FaceRel }, source_provider: W.LooseMod + " (loose)");

        Assert.Contains("does not supply", text);
        Assert.DoesNotContain("read from:", text);
    }

    // Probe: "'*winner' selects the VFS winner, as the refusal's tail says it does".
    [Fact]
    public void TheWinnerPoleSelectsTheVfsWinner()
        => Assert.Contains("read from: \"" + W.LooseMod + "\"",
                           NifTools.NifInspect(_w.Svc, new[] { W.FaceRel }, source_provider: HousecarlCore.AssetSourceChoice.WinnerToken));
}
