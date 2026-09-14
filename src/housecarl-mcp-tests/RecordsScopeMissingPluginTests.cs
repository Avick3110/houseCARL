using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A plugins= scope naming one plugin the order does not carry answers for the ones it does, and names the
/// missing one beside the result (#666). Failing every named plugin's read over one bad name threw away an answer
/// that was valid for the rest.</summary>
[Collection("records")]
[Trait("tier", "integration")]
public sealed class RecordsScopeMissingPluginTests : RecordsTestBase
{
    const string Missing = "HcNotInstalled.esp";

    [Fact]
    public void AScopeWithOneMissingPluginStillAnswersForTheLoadedOnes()
    {
        var text = RecordsTools.Records(Svc, plugins: Scope(W.MasterName, Missing), types: new[] { "WEAP" });

        // The loaded plugin's rows are there…
        Served(text, Fid(W.Weapons[0]));
        // …and the missing one is named beside them, not silently dropped.
        Assert.Contains(Missing, text);
        Assert.Contains("the load order does not carry", text);
    }

    [Fact]
    public void AScopeWhoseEveryNameIsMissingIsRefusedNamingThem()
    {
        var text = RecordsTools.Records(Svc, plugins: Scope(Missing), types: new[] { "WEAP" });

        Assert.Contains("error:", text);
        Assert.Contains(Missing, text);
        Assert.Contains("load order does not carry", text);
    }

    public RecordsScopeMissingPluginTests(RecordsFixture f) : base(f) { }
}
