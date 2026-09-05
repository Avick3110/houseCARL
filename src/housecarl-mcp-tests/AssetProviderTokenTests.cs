using System.Text.RegularExpressions;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>asset_status prints a provider the way every other asset surface does (#340): the name inside double
/// quotes — a character a Windows file or folder name cannot contain — with the kind outside them. The old render
/// appended the kind bare, so a caller could not tell a mod called "Face Extras (SE)" from a mod called "Face Extras"
/// read out of a BSA, and the printed token was not the token a source selector takes.</summary>
[Trait("tier", "integration")]
public sealed class AssetProviderTokenTests : IClassFixture<AssetSelectWorld>
{
    readonly AssetSelectWorld _w;
    public AssetProviderTokenTests(AssetSelectWorld w) => _w = w;

    /// <summary>Every name the render delimits, in order of appearance.</summary>
    static string[] Quoted(string text) => Regex.Matches(text, "\"([^\"]*)\"").Select(m => m.Groups[1].Value).ToArray();

    /// <summary>A provider name with its own parenthetical still reads back whole: the delimiter says where the name
    /// ends, and the kind is outside it. Stripping "the parenthetical" here would take part of the real name.</summary>
    [Fact]
    public void AProviderNameCarryingItsOwnParentheticalIsStillDelimitedWhole()
    {
        var text = AssetTools.AssetStatus(_w.Svc, new[] { AssetSelectWorld.ParenPath });

        Assert.Contains(AssetSourceSelection.Describe(AssetSelectWorld.ParenMod, "loose"), text);
        Assert.Contains(AssetSelectWorld.ParenMod, Quoted(text));
    }

    /// <summary>The whole provider chain goes through the one formatter, not just the winner line — a caller reading
    /// a loser out of the chain gets the same token the winner line gives.</summary>
    [Fact]
    public void EveryNameInTheProviderChainIsPrintedByTheSharedFormatter()
    {
        var contested = _w.Rel("0002.nif");
        var text = AssetTools.AssetStatus(_w.Svc, new[] { contested });

        var data = _w.Svc.AssetStatus(new[] { contested });
        var hit = Assert.Single(data.Results).Hit!;
        foreach (var p in hit.Providers)
            Assert.Contains(AssetSourceSelection.Describe(p.Source, p.Kind == AssetKind.Bsa ? "BSA" : "loose"), text);

        // The bug's own signature: the bare "Name (kind)" form must be gone, or a caller copying the printed token
        // back into a selector keeps handing it a name with decoration attached.
        Assert.DoesNotContain("FaceHigher (loose)", text);
    }

    /// <summary>An archive provider is spelled the same way, so the two kinds cannot drift apart again.</summary>
    [Fact]
    public void AnArchiveProviderIsDelimitedLikeALooseOne()
    {
        var text = AssetTools.AssetStatus(_w.Svc, new[] { _w.Rel("0005.nif") });

        Assert.Contains(AssetSourceSelection.Describe("HcArch.bsa", "BSA"), text);
    }
}
