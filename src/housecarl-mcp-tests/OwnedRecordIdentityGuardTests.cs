using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>At depth=2 an owned INFO in a DIAL's Responses renders its own FormKey, plus its EditorID only when it
/// has one (#252). Migrated from <c>owned-record-identity-guard</c>.</summary>
[Trait("tier", "unit")]
public sealed class OwnedRecordIdentityGuardTests
{
    readonly DialogResponses _withEdid;
    readonly DialogResponses _noEdid;
    readonly IReadOnlyList<FieldValue> _fields;

    public OwnedRecordIdentityGuardTests()
    {
        var mod = new SkyrimMod(new ModKey("hc_owned", ModType.Plugin), SkyrimRelease.SkyrimSE);
        _withEdid = new DialogResponses(mod.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcInfoEdid" };
        _noEdid = new DialogResponses(mod.GetNextFormKey(), SkyrimRelease.SkyrimSE);
        var dial = mod.DialogTopics.AddNew();
        dial.Responses.Add(_withEdid);
        dial.Responses.Add(_noEdid);
        _fields = ReadEngine.ReadFields(dial, new[] { "Responses" }, 2).Fields;
    }

    string Note(int i) => _fields.Single(f => f.Path == $"Responses[{i}]" && !f.HasValue).Note ?? "";

    // FORMKEY + EDITORID + TYPE-KEPT: [DialogResponses <fk> editorid=HcInfoEdid].
    [Fact]
    public void AnInfoWithAnEditorIdShowsItsFormKeyAndEditorId()
    {
        var note = Note(0);
        Assert.Contains("DialogResponses", note);
        Assert.Contains(_withEdid.FormKey.ToString(), note);
        Assert.Contains("editorid=HcInfoEdid", note);
    }

    // NO-EDITORID + TYPE-KEPT: [DialogResponses <fk>], no dangling editorid=.
    [Fact]
    public void AnInfoWithoutAnEditorIdShowsItsFormKeyAndNoEditorIdKey()
    {
        var note = Note(1);
        Assert.Contains("DialogResponses", note);
        Assert.Contains(_noEdid.FormKey.ToString(), note);
        Assert.DoesNotContain("editorid=", note);
    }
}
