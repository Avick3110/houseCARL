using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The SELECT-a-family half of <c>housecarl_skse</c>: which family <c>findings=</c> picks, what a call that names
/// none of them ran, and the two refusals. All four are pure over their inputs, so they are driven directly rather
/// than through a live MO2 instance — the three family renders themselves are covered by the generator's
/// skse/native-pairing/config probes against synthetic data.
/// </summary>
[Trait("tier", "unit")]
public sealed class SkseFamilySelectionTests
{
    static SkseTools.SkseFamily Parse(string? findings)
    {
        Assert.True(SkseTools.TryParseFamily(findings, out var family, out var error), error);
        Assert.Null(error);
        return family;
    }

    [Fact]
    public void FindingsOmitted_RunsTheInventoryFamily()
    {
        Assert.Equal(SkseTools.SkseFamily.Inventory, Parse(null));
        Assert.Equal(SkseTools.SkseFamily.Inventory, Parse(""));
        Assert.Equal(SkseTools.SkseFamily.Inventory, Parse("   "));
    }

    [Theory]
    [InlineData("inventory", "Inventory")]
    [InlineData("pairing", "Pairing")]
    [InlineData("config", "Config")]
    [InlineData("  PAIRING  ", "Pairing")]
    [InlineData("Config", "Config")]
    public void EachFamilyTokenSelectsItsFamily_CaseAndPaddingInsensitive(string token, string expected)
        => Assert.Equal(expected, Parse(token).ToString());

    /// <summary>An unknown value is refused naming all three spellings — never quietly defaulted to inventory, which
    /// would answer a question the caller did not ask and call it the answer.</summary>
    [Theory]
    [InlineData("inventories")]
    [InlineData("skse_inventory")]
    [InlineData("errors")]
    [InlineData("inventory,pairing")]
    public void AnUnknownFindingsValueIsRefused_NamingTheThreeFamilies(string token)
    {
        Assert.False(SkseTools.TryParseFamily(token, out _, out var error));
        Assert.NotNull(error);
        Assert.StartsWith("error: ", error);
        Assert.Contains(token.Trim(), error);
        foreach (var family in new[] { "inventory", "pairing", "config" })
            Assert.Contains($"'{family}'", error);
        Assert.Contains("One family per call", error);
    }

    /// <summary>peek= reads one DLL image, which only the inventory family looks at. Passing it with another family is
    /// refused rather than ignored: a silently dropped flag reads as a peek that found nothing.</summary>
    [Fact]
    public void PeekOnANonInventoryFamilyIsRefused_AndOnInventoryIsNot()
    {
        foreach (var family in new[] { SkseTools.SkseFamily.Pairing, SkseTools.SkseFamily.Config })
        {
            var error = SkseTools.PeekFamilyError(peek: true, family);
            Assert.NotNull(error);
            Assert.StartsWith("error: ", error);
            Assert.Contains(family.ToString().ToLowerInvariant(), error);
            Assert.Contains("findings='inventory'", error);
            Assert.Null(SkseTools.PeekFamilyError(peek: false, family));
        }
        Assert.Null(SkseTools.PeekFamilyError(peek: true, SkseTools.SkseFamily.Inventory));
    }

    /// <summary>Every response says which family ran and how to ask for the two it did not — the only thing that keeps
    /// the omitted-findings default from being a silent narrowing.</summary>
    [Theory]
    [InlineData("inventory", "pairing", "config")]
    [InlineData("pairing", "inventory", "config")]
    [InlineData("config", "inventory", "pairing")]
    public void TheFooterNamesTheFamilyThatRanAndTheSpellingOfBothThatDidNot(string mine, string first, string second)
    {
        var footer = SkseTools.FamilyFooter(Parse(mine));

        Assert.Contains($"this call ran findings='{mine}'", footer);
        Assert.Contains("NOT run:", footer);
        Assert.Contains($"findings='{first}'", footer);
        Assert.Contains($"findings='{second}'", footer);

        // The three retired spellings are gone from the surface, so no response may teach one of them.
        foreach (var retired in new[] { "skse_inventory", "native_pairing_audit", "skse_config_audit" })
            Assert.DoesNotContain(retired, footer, StringComparison.Ordinal);
    }
}
