using System.Text.Json;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A game setting whose EditorID another one in the active order shares says so on its row, naming the
/// other FormID(s) and the copy the game uses: the last plugin carrying any setting of that name (#1076).</summary>
[Trait("tier", "integration")]
public sealed class SharedEditorIdNoteTests : IClassFixture<SharedGmstFixture>
{
    readonly SharedGmstWorld _w;
    public SharedEditorIdNoteTests(SharedGmstFixture f) => _w = f.W;

    static string T(Mutagen.Bethesda.Plugins.FormKey fk) => SharedGmstWorld.Token(fk);

    static string GameUses(string plugin, Mutagen.Bethesda.Plugins.FormKey fk) => $"carrying any of them: {plugin} ({T(fk)})";

    string Json(Mutagen.Bethesda.Plugins.FormKey fk) =>
        JsonDocument.Parse(RecordsTools.Records(_w.Svc, formids: new[] { T(fk) }, format: "json"))
            .RootElement.GetProperty("records")[0].TryGetProperty("editorid_note", out var n) ? n.GetString()! : "";

    [Fact]
    public void TwoPluginsSharingANameNameTheOtherAndTheLastProvider()
    {
        var r = RecordsTools.Records(_w.Svc, formids: new[] { T(_w.SharedInMaster) });

        Assert.Contains("shares EditorID iShared with " + T(_w.SharedInMid) + ";", r);
        Assert.Contains(GameUses(SharedGmstWorld.MidName, _w.SharedInMid), r);
    }

    [Fact]
    public void AThreeFormIdNameNamesBothOthersAndTheLastPlugin()
    {
        var note = Json(_w.TripleInMid);

        Assert.Contains($"with {T(_w.TripleInMaster)}, {T(_w.TripleInLast)};", note);
        Assert.Contains(GameUses(SharedGmstWorld.LastName, _w.TripleInLast), note);
    }

    [Fact]
    public void AUniqueNameCarriesNoNoteInAnyFormat()
    {
        var id = new[] { T(_w.Unique) };
        Assert.DoesNotContain("shares EditorID", RecordsTools.Records(_w.Svc, formids: id));
        Assert.DoesNotContain("shares EditorID", RecordsTools.Records(_w.Svc, formids: id, format: "json"));
        Assert.DoesNotContain("shares EditorID", RecordsTools.Records(_w.Svc, formids: id, format: "dense"));
    }

    [Fact]
    public void TextJsonAndDenseCarryTheSameSentence()
    {
        var id = new[] { T(_w.SharedInMaster) };
        var json = Json(_w.SharedInMaster);
        var text = RecordsTools.Records(_w.Svc, formids: id);
        var doc = JsonDocument.Parse(RecordsTools.Records(_w.Svc, formids: id, format: "dense")).RootElement;
        int col = doc.GetProperty("columns").EnumerateArray().Select(c => c.GetString()).ToList().IndexOf("editorid");
        var dense = doc.GetProperty("rows")[0][col].GetString();

        Assert.StartsWith("shares EditorID iShared", json);
        Assert.Contains("note: " + json + "\n", text);
        Assert.Equal($"iShared   ({json})", dense);
    }

    [Fact]
    public void AScanRowCarriesTheNoteToo()
    {
        var r = RecordsTools.Records(_w.Svc, types: new[] { "GMST" }, where: new[] { "editorid = fTriple" });

        Assert.Contains(GameUses(SharedGmstWorld.LastName, _w.TripleInLast), r);
        Assert.Contains($"with {T(_w.TripleInMid)}, {T(_w.TripleInLast)};", r);
    }
}
