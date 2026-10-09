using System.Text.Json;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>An 'editorid in' or 'formid in' list names the members that matched no record the scan judged (#1092).</summary>
[Collection("records")]
[Trait("tier", "integration")]
public sealed class RecordsUnmatchedMembersTests : RecordsTestBase
{
    public RecordsUnmatchedMembersTests(RecordsFixture f) : base(f) { }

    const string Lead = "have no record in this selection";

    static string NoteLine(string text) => text.Split('\n').Single(l => l.Contains(Lead));

    static List<string> Unmatched(JsonElement doc) =>
        doc.GetProperty("unmatched").EnumerateArray().Select(e => e.GetString()!).ToList();

    [Fact]
    public void AnEditoridListNamesExactlyItsMisses_Text()
    {
        var text = RecordsTools.Records(Svc, types: new[] { "WEAP" }, where: new[] { "editorid in [HcRecW0, NoSuchA, HcRecW1, NoSuchB]" });

        Served(text, Fid(W.Weapons[0]), Fid(W.Weapons[1]));
        var note = NoteLine(text);
        Assert.Contains("2 'in' list member(s)", note);
        Assert.Contains("NoSuchA, NoSuchB", note);
        Assert.DoesNotContain("HcRecW", note);
    }

    [Fact]
    public void AnEditoridListNamesExactlyItsMisses_Json()
    {
        var doc = Je(RecordsTools.Records(Svc, types: new[] { "WEAP" }, format: "json",
                                          where: new[] { "editorid in [HcRecW0, NoSuchA, hcrecw1, NoSuchB]" }));

        Assert.Equal(new[] { "NoSuchA", "NoSuchB" }, Unmatched(doc));
        Assert.Contains(doc.GetProperty("notes").EnumerateArray(), n => n.GetString()!.Contains(Lead));
    }

    [Fact]
    public void AFormidListNamesExactlyItsMisses_TextAndJson()
    {
        var missing = $"FFFFF0:{W.MasterName}";
        var where = new[] { $"formid in [{Fid(W.Weapons[0])}, {missing}, {Fid(W.Weapons[1])}]" };

        var note = NoteLine(RecordsTools.Records(Svc, types: new[] { "WEAP" }, where: where));
        var doc = Je(RecordsTools.Records(Svc, types: new[] { "WEAP" }, format: "json", where: where));

        Assert.Contains("1 'in' list member(s)", note);
        Assert.Contains(missing, note);
        Assert.Equal(new[] { missing }, Unmatched(doc));
    }

    [Fact]
    public void AListWithNoMissesSaysNothing()
    {
        var text = RecordsTools.Records(Svc, types: new[] { "WEAP" }, where: new[] { "editorid in [HcRecW0, HcRecW1]" });
        var doc = Je(RecordsTools.Records(Svc, types: new[] { "WEAP" }, format: "json", where: new[] { "editorid in [HcRecW0, HcRecW1]" }));

        Assert.DoesNotContain(Lead, text);
        Assert.False(doc.TryGetProperty("unmatched", out _));
    }

    static string[] Absent(int n) => Enumerable.Range(0, n).Select(i => $"NoSuch{i:D2}").ToArray();

    [Fact]
    public void ALongMissListNamesWhatFitsAndCountsTheRest()
    {
        var names = Absent(FieldPredicateSet.UnmatchedShown + 5);
        var where = new[] { $"editorid in [HcRecW0, {string.Join(", ", names)}]" };

        var note = NoteLine(RecordsTools.Records(Svc, types: new[] { "WEAP" }, where: where));
        var doc = Je(RecordsTools.Records(Svc, types: new[] { "WEAP" }, format: "json", where: where));

        Assert.Contains($"{names.Length} 'in' list member(s)", note);
        Assert.Contains(names[FieldPredicateSet.UnmatchedShown - 1], note);
        Assert.DoesNotContain(names[FieldPredicateSet.UnmatchedShown], note);
        Assert.Contains("and 5 more", note);
        Assert.Equal(names.Take(FieldPredicateSet.UnmatchedShown), Unmatched(doc));
        Assert.False(doc.TryGetProperty("unmatched_total", out _));
        Assert.Contains(doc.GetProperty("notes").EnumerateArray(), n => n.GetString()!.Contains("and 5 more"));
    }

    [Fact]
    public void AToFileManifestCarriesTheWholeMissList()
    {
        var names = Absent(FieldPredicateSet.UnmatchedShown + 5);
        var art = W.Scratch("unmatched", Guid.NewGuid().ToString("N") + ".jsonl");

        RecordsTools.Records(Svc, types: new[] { "WEAP" }, to_file: art,
                             where: new[] { $"editorid in [HcRecW0, {string.Join(", ", names)}]" });

        var (manifest, _, err) = ResultArtifact.ReadIdentity(art, File.ReadAllText(art));
        Assert.Null(err);
        var note = Assert.Single(manifest!.Notes!, n => n.Contains(Lead));
        Assert.All(names, n => Assert.Contains(n, note));
        Assert.DoesNotContain("more", note);
    }

    [Fact]
    public void AnEditoridAtFileNamesItsMisses()
    {
        var list = W.Scratch("unmatched", Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(list, "NoSuchB\nHcRecW0\nNoSuchA\n");

        var doc = Je(RecordsTools.Records(Svc, types: new[] { "WEAP" }, format: "json", where: new[] { $"editorid in @{list}" }));

        Assert.Equal(new[] { "NoSuchB", "NoSuchA" }, Unmatched(doc));
    }

    [Fact]
    public void AFormidArtifactNamesItsMissesInTheArtifactsOrder()
    {
        var art = W.Scratch("unmatched", Guid.NewGuid().ToString("N") + ".jsonl");
        RecordsTools.Records(Svc, types: new[] { "WEAP" }, to_file: art);
        var (_, tokens, err) = ResultArtifact.ReadIdentity(art, File.ReadAllText(art));
        Assert.Null(err);
        Assert.True(tokens!.Count >= 2);

        // An ARMO scan judges no weapon, so every artifact member is unmatched, named in the file's order.
        var doc = Je(RecordsTools.Records(Svc, types: new[] { "ARMO" }, format: "json", where: new[] { $"formid in @{art}" }));

        Assert.Equal(tokens.Take(FieldPredicateSet.UnmatchedShown), Unmatched(doc));
    }

    [Fact]
    public void AListBesideAnotherTermStillSaysInThisSelection()
    {
        var text = RecordsTools.Records(Svc, types: new[] { "WEAP" },
                                        where: new[] { "editorid in [HcRecW0, NoSuchA]", "BasicStats.Damage > 1000" });

        Assert.Contains("scan: 0 matches", text);
        var note = NoteLine(text);
        Assert.Contains("NoSuchA", note);
        Assert.DoesNotContain("HcRecW0", note);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AMemberOnlyALowerScopedCopyCarriesIsUnmatched(bool swap)
    {
        var names = swap ? new[] { W.OverrideName, W.MasterName } : new[] { W.MasterName, W.OverrideName };

        var doc = Je(RecordsTools.Records(Svc, types: new[] { "ARMO" }, plugins: Scope(names), format: "json",
                                          where: new[] { $"editorid in [{RecordsWorld.RenamedArmorOldEid}, {RecordsWorld.RenamedArmorNewEid}]" }));

        Assert.Equal(new[] { RecordsWorld.RenamedArmorOldEid }, Unmatched(doc));
    }
}
