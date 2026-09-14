using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A plugins= scope naming one plugin the order does not carry answers for the ones it does, and names the
/// missing one — with the cause the index can give for it — beside the result (#666). Failing every named plugin's
/// read over one bad name threw away an answer that was valid for the rest.</summary>
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
        Assert.Contains("nothing from the 1 it does not", text);
    }

    /// <summary>The served note carries each missing name's own absence clause, so a caller is not left to re-derive
    /// a fact the tool already had — here the did-you-mean for a near-miss spelling.</summary>
    [Fact]
    public void TheServedNoteCarriesTheMissingNamesCause()
    {
        var typo = W.MasterName + "X";

        var text = RecordsTools.Records(Svc, plugins: Scope(W.MasterName, typo), types: new[] { "WEAP" });

        Served(text, Fid(W.Weapons[0]));
        Assert.Contains("Did you mean", text);
        Assert.Contains(W.MasterName, text);
    }

    [Fact]
    public void AScopeWhoseEveryNameIsMissingIsRefusedNamingThem()
    {
        var text = RecordsTools.Records(Svc, plugins: Scope(Missing), types: new[] { "WEAP" });

        Assert.Contains("error:", text);
        Assert.Contains(Missing, text);
        Assert.Contains("nothing to scan", text);
    }

    /// <summary>Two missing names both carry a cause: the refusal used to attach one only when exactly one name was
    /// missing, so a two-name scope explained neither.</summary>
    [Fact]
    public void AnAllMissingRefusalCarriesACausePerName()
    {
        var text = RecordsTools.Records(Svc, plugins: Scope(W.MasterName + "X", W.OverrideName + "X"),
                                        types: new[] { "WEAP" });

        Assert.Contains("error:", text);
        Assert.Equal(2, CountOf(text, "Did you mean"));
    }

    /// <summary>A null entry binds, and used to reach the resolver as a null key — reported back as an internal
    /// failure rather than bad input. It is refused by its index instead.</summary>
    [Fact]
    public void ANullScopeEntryIsRefusedByItsIndex()
    {
        var text = RecordsTools.Records(Svc, plugins: new RecordsTools.RecordsScope { names = new[] { W.MasterName, null } },
                                        types: new[] { "WEAP" });

        Assert.Contains("plugins.names[1] is empty", text);
        Assert.DoesNotContain("internal houseCARL failure", text);
    }

    /// <summary>A body form reads the same scan, so it carries the same coverage note — the scan lane printed it and
    /// this one did not, which answered one question two ways.</summary>
    [Fact]
    public void ABodyFormCarriesTheScopeNoteToo()
    {
        var text = RecordsTools.Records(Svc, plugins: Scope(W.MasterName, Missing), types: new[] { "WEAP" },
                                        project: Fields("EditorID"), limit: 2);

        Served(text);
        Assert.Contains(Missing, text);
        Assert.Contains("nothing from the 1 it does not", text);
    }

    /// <summary>The off-order lane takes the same shape: a scope with one absent name used to fail the whole call
    /// there while the identical scope answered on the in-order scan, so the same input behaved two ways depending
    /// on an unrelated axis.</summary>
    [Fact]
    public void TheOffOrderLaneAnswersPastAMissingScopeNameToo()
    {
        var text = RecordsTools.Records(Svc, plugins: Scope(W.MasterName, Missing), types: new[] { "WEAP" },
                                        source: Plugin(W.OldName));

        Served(text);
        Assert.Contains(Missing, text);
        Assert.Contains("nothing from the 1 it does not", text);
        // The scope means something different over a file, and the note still says so.
        Assert.Contains("ACTIVE plugins also touch", text);
    }

    public RecordsScopeMissingPluginTests(RecordsFixture f) : base(f) { }
}
