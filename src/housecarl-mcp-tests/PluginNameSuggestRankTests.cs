using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The near-miss plugin suggester on the report's real names: an apostrophe slip and a mod-folder name both
/// point at the real plugin, a far miss and an exact match get nothing. Migrated from the loadorder-status-guard
/// probe (arm J1).</summary>
[Trait("tier", "unit")]
public class PluginNameSuggestRankTests
{
    const string Real = "Sanguine's Trade - An Economy Mod.esp";
    static readonly string[] Pool = { "Skyrim.esm", Real, "Requiem.esp" };

    // "apostrophe slip (edit-distance 1) → suggests the real .esp"
    [Fact]
    public void AnApostropheSlipSuggestsTheRealPlugin()
        => Assert.Equal(new[] { Real }, PluginNameSuggest.Nearest("Sanguines Trade - An Economy Mod.esp", Pool));

    // "the MOD FOLDER name (no extension) → suggests the matching .esp (extension-difference rule)"
    [Fact]
    public void TheModFolderNameSuggestsTheMatchingPlugin()
        => Assert.Equal(Real, PluginNameSuggest.Nearest("Sanguine's Trade - An Economy Mod", Pool).FirstOrDefault());

    // "a far miss yields NO suggestion (a wrong 'did you mean' is worse than none)"
    [Fact]
    public void AFarMissGetsNoSuggestion()
        => Assert.Empty(PluginNameSuggest.Nearest("Totally Unrelated Content Pack.esp", Pool));

    // "an EXACT match is not a miss → no suggestion"
    [Fact]
    public void AnExactMatchGetsNoSuggestion()
        => Assert.Empty(PluginNameSuggest.Nearest(Real, Pool));

    // "DidYouMean renders the clause naming the real plugin"
    [Fact]
    public void DidYouMeanNamesTheRealPlugin()
        => Assert.Contains("Did you mean `" + Real + "`", PluginNameSuggest.DidYouMean("Sanguines Trade - An Economy Mod.esp", Pool));
}
