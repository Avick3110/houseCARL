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

        Assert.StartsWith("shares EditorID iShared", json);
        Assert.Contains("note: " + json + "\n", text);
        Assert.Equal("iShared", DenseEditorId(doc));
        Assert.Equal(json, DenseNote(doc, _w.SharedInMaster));
    }

    // A mutant naming the defining plugin instead of the winner, or ordering members by defining plugin, names the
    // master here: the last plugin overrides the master's iOver, so that FormID is the copy the game uses.
    [Fact]
    public void AnOverriddenMemberIsNamedByItsWinner()
    {
        var fromMid = Json(_w.OverInMid);
        var fromMaster = Json(_w.OverInMaster);

        Assert.Contains($"with {T(_w.OverInMaster)};", fromMid);
        Assert.Contains(GameUses(SharedGmstWorld.LastName, _w.OverInMaster), fromMid);
        Assert.Contains($"with {T(_w.OverInMid)};", fromMaster);
        Assert.Contains(GameUses(SharedGmstWorld.LastName, _w.OverInMaster), fromMaster);
    }

    [Fact]
    public void TheFieldsFormCarriesTheNoteInJsonAndDense()
    {
        var id = new[] { T(_w.SharedInMaster) };
        var fields = new RecordsTools.RecordsProject { form = "fields", fields = new[] { "Data" } };
        var summary = Json(_w.SharedInMaster);

        var json = JsonDocument.Parse(RecordsTools.Records(_w.Svc, formids: id, format: "json", project: fields)).RootElement;
        Assert.Equal(summary, json.GetProperty("records")[0].GetProperty("editorid_note").GetString());

        var dense = JsonDocument.Parse(RecordsTools.Records(_w.Svc, formids: id, format: "dense", project: fields)).RootElement;
        Assert.Equal("iShared", DenseEditorId(dense));
        Assert.Equal(summary, DenseNote(dense, _w.SharedInMaster));
    }

    [Fact]
    public void TreeAndDeltaRowsCarryTheNote()
    {
        var id = new[] { T(_w.OverInMaster) };
        var note = Json(_w.OverInMaster);

        var tree = RecordsTools.Records(_w.Svc, formids: id, project: new RecordsTools.RecordsProject { form = "tree" });
        Assert.Contains("note: " + note + "\n", tree);
        var treeJson = RecordsTools.Records(_w.Svc, formids: id, format: "json", project: new RecordsTools.RecordsProject { form = "tree" });
        Assert.Contains(JsonSerializer.Serialize(note), treeJson);

        var delta = RecordsTools.Records(_w.Svc, formids: id, source: Pole(SharedGmstWorld.LastName),
                                         versus: Pole(SharedGmstWorld.MasterName), project: new RecordsTools.RecordsProject { form = "delta" });
        Assert.Contains("note: " + note + "\n", delta);
    }

    [Fact]
    public void AParseFaultPartwayThroughAPluginIsNamedNotThrown()
    {
        using var w = new SharedGmstWorld();
        var pin = w.Svc.CapturePin();
        // The middle plugin's second setting gets a name prefix no setting type has, so its walk stops partway.
        var path = w.PluginPath(SharedGmstWorld.MidName);
        var bytes = File.ReadAllBytes(path);
        int at = IndexOf(bytes, System.Text.Encoding.ASCII.GetBytes("fTriple\0"));
        Assert.True(at > 0);
        bytes[at] = (byte)'q';
        File.WriteAllBytes(path, bytes);

        var note = SharedEditorIds.Note(pin, w.SharedInMaster, "GameSettingInt");

        Assert.NotNull(note);
        Assert.Contains($"shares EditorID iShared with {T(w.SharedInMid)};", note);
        Assert.Contains($"could NOT be read ({SharedGmstWorld.MidName})", note);
    }

    static System.Text.Json.JsonElement Pole(string plugin) => JsonDocument.Parse(JsonSerializer.Serialize(plugin)).RootElement.Clone();

    static int IndexOf(byte[] hay, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= hay.Length; i++)
            if (hay.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
        return -1;
    }

    static string? DenseEditorId(JsonElement doc)
    {
        int col = doc.GetProperty("columns").EnumerateArray().Select(c => c.GetString()).ToList().IndexOf("editorid");
        return doc.GetProperty("rows")[0][col].GetString();
    }

    static string? DenseNote(JsonElement doc, Mutagen.Bethesda.Plugins.FormKey fk) =>
        doc.GetProperty("editorid_notes").EnumerateArray()
           .Single(n => n.GetProperty("formid").GetString() == T(fk)).GetProperty("editorid_note").GetString();

    [Fact]
    public void AScanRowCarriesTheNoteToo()
    {
        var r = RecordsTools.Records(_w.Svc, types: new[] { "GMST" }, where: new[] { "editorid = fTriple" });

        Assert.Contains(GameUses(SharedGmstWorld.LastName, _w.TripleInLast), r);
        Assert.Contains($"with {T(_w.TripleInMid)}, {T(_w.TripleInLast)};", r);

        // The scan builds the lookup from its own walk; its notes are the ones a single read builds by walking.
        var scan = JsonDocument.Parse(RecordsTools.Records(_w.Svc, types: new[] { "GMST" }, format: "json")).RootElement;
        foreach (var fk in new[] { _w.SharedInMid, _w.TripleInMaster, _w.OverInMid, _w.OverInMaster })
            Assert.Equal(Json(fk), scan.GetProperty("matches").EnumerateArray()
                .Single(x => x.GetProperty("formid").GetString() == T(fk)).GetProperty("editorid_note").GetString());
    }
}
