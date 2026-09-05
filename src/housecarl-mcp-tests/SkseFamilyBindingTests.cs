using System.Text.Json;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The BIND-a-family half of <c>housecarl_skse</c>: each <c>findings=</c> value reaching its own service call and its
/// own wire class, driven through the published tool method against a configured instance.
///
/// <para><see cref="SkseFamilySelectionTests"/> pins the switch behind a recording seam, which cannot see two arms of
/// <c>ServiceRenders</c> swapped: the seam would still report the right family while the caller read another family's
/// answer. So these go through the real renders on the shared <see cref="RecordsWorld"/> — a synthetic MO2 instance
/// with no SKSE layer at all, which is enough, because what is asserted is WHICH render answered, not what it
/// found.</para>
/// </summary>
[Collection("records")]
[Trait("tier", "integration")]
public sealed class SkseFamilyBindingTests
{
    readonly RecordsWorld W;
    public SkseFamilyBindingTests(RecordsFixture f) => W = f.W;

    /// <summary>Each family's own header — the first line its wire class writes, and no other one does.</summary>
    const string InventoryHeader = "SKSE plugin layer — profile '";
    const string PairingHeader = "native pairing audit — profile '";
    const string ConfigHeader = "SKSE config audit — profile '";

    static readonly string[] AllHeaders = { InventoryHeader, PairingHeader, ConfigHeader };

    static JsonElement Str(string s) => JsonSerializer.SerializeToElement(s);

    [Theory]
    [InlineData("inventory", InventoryHeader)]
    [InlineData("pairing", PairingHeader)]
    [InlineData("config", ConfigHeader)]
    public void EachFamilyAnswersWithItsOwnWireClassHeaderAndItsOwnFooter(string findings, string header)
    {
        Assert.True(SkseTools.TryParseFamily(findings, out var family, out var parseError), parseError);

        var text = SkseTools.Skse(W.Svc, findings: Str(findings));

        Assert.DoesNotContain("error: ", text, StringComparison.Ordinal);
        Assert.StartsWith(header, text, StringComparison.Ordinal);
        Assert.EndsWith(SkseTools.FamilyFooter(family), text, StringComparison.Ordinal);

        // The two families that did NOT run must not have written a line into this answer.
        foreach (var other in AllHeaders)
            if (other != header) Assert.DoesNotContain(other, text, StringComparison.Ordinal);
    }

    /// <summary>The omitted default binds to the inventory RENDER, not merely to the inventory enum value.</summary>
    [Fact]
    public void FindingsOmittedAnswersWithTheInventoryRender()
    {
        var text = SkseTools.Skse(W.Svc);

        Assert.StartsWith(InventoryHeader, text, StringComparison.Ordinal);
        Assert.EndsWith(SkseTools.FamilyFooter(SkseTools.SkseFamily.Inventory), text, StringComparison.Ordinal);
    }

}
