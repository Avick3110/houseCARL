using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using Xunit;
using static HousecarlMcpTests.NestedCreateRig;

namespace HousecarlMcpTests;

/// <summary>A same-call '@editorid' as a FormLink value: on a singular link, as a link-list Add or ReplaceAll entry, as a
/// record's reference to itself, and inside a compose; and the refusals when the sibling is later, the field is not a
/// link, the verb is not Add, the value sits in a dict, the call has no siblings, or a stray compose rides along.
/// Migrated from the nested-create-guard probe (SIBREF and REJ-SIB* arms).</summary>
[Trait("tier", "integration")]
public sealed class SiblingReferenceCreateTests : IDisposable
{
    NestedCreateRig? _rig;
    NestedCreateRig W => _rig ??= new NestedCreateRig();

    static WriteRequest Req(string type, string path, string verb, string? value = null, string? key = null)
        => WritePathRig.Req(type, path, verb, value, key);

    static WriteRequest AliasAdd(params WriteRequest[] sets) => new()
    {
        RecordType = "Quest", Path = new[] { "VirtualMachineAdapter", "Aliases" }, Verb = "Add",
        Struct = new StructSpec { Type = "QuestFragmentAlias", Sets = sets.ToList() },
    };

    static FormKey? AliasObject(ISkyrimModGetter mod, FormKey quest)
        => mod.Quests.Single(q => q.FormKey == quest).VirtualMachineAdapter?.Aliases.FirstOrDefault()?.Property?.Object.FormKey;

    // SIBREF: line 1 Topic=@topic and line 2 PreviousDialog=@line1 land the same-call FormKeys.
    [Fact]
    public void ASiblingReferenceOnALinkLandsTheSiblingsFormKey()
    {
        var (o, path) = W.Create("HcNcSibRef.esp",
            Spec("DialogTopic", "HcNcSrTopic"),
            Under("HcNcSrTopic", "DialogResponses", "HcNcSrL1", Req("DialogResponses", "Topic", "Set", "@HcNcSrTopic")),
            Under("HcNcSrTopic", "DialogResponses", "HcNcSrL2",
                Req("DialogResponses", "Topic", "Set", "@HcNcSrTopic"),
                Req("DialogResponses", "PreviousDialog", "Set", "@HcNcSrL1")));
        Assert.True(o.Success, o.Error);
        Assert.Equal(3, o.Created.Count);
        var mod = W.Open(path);
        var (topic, l1, l2) = (o.Created[0].FormKey, o.Created[1].FormKey, o.Created[2].FormKey);
        Assert.Equal(topic, Info(mod, l1)!.Topic.FormKey);
        Assert.Equal(topic, Info(mod, l2)!.Topic.FormKey);
        Assert.Equal(l1, Info(mod, l2)!.PreviousDialog.FormKey);
    }

    // SIBREF-LISTADD: a line's LinkTo Add=@sub-topic lands the sub-topic's FormKey.
    [Fact]
    public void ASiblingReferenceAddedToALinkListLandsTheSiblingsFormKey()
    {
        var (o, path) = W.Create("HcNcSlAdd.esp",
            Spec("DialogTopic", "HcNcSlaHub"),
            Spec("DialogTopic", "HcNcSlaSub"),
            Under("HcNcSlaHub", "DialogResponses", "HcNcSlaL1", Req("DialogResponses", "LinkTo", "Add", "@HcNcSlaSub")));
        Assert.True(o.Success, o.Error);
        Assert.Contains(o.Created[1].FormKey, Info(W.Open(path), o.Created[2].FormKey)!.LinkTo.Select(l => l.FormKey));
    }

    // SIBREF-LISTREPLACE: LinkTo ReplaceAll=[@subA,@subB] lands both FormKeys.
    [Fact]
    public void SiblingReferencesInALinkListReplaceAllLandBothFormKeys()
    {
        var (o, path) = W.Create("HcNcSlRep.esp",
            Spec("DialogTopic", "HcNcSlrHub"),
            Spec("DialogTopic", "HcNcSlrSubA"),
            Spec("DialogTopic", "HcNcSlrSubB"),
            Under("HcNcSlrHub", "DialogResponses", "HcNcSlrL1", new WriteRequest
            {
                RecordType = "DialogResponses", Path = new[] { "LinkTo" }, Verb = "ReplaceAll", Values = new[] { "@HcNcSlrSubA", "@HcNcSlrSubB" },
            }));
        Assert.True(o.Success, o.Error);
        var links = Info(W.Open(path), o.Created[3].FormKey)!.LinkTo.Select(l => l.FormKey).ToList();
        Assert.Contains(o.Created[1].FormKey, links);
        Assert.Contains(o.Created[2].FormKey, links);
    }

    // REJ-SIBFWD: a field @ref to a sibling declared later refuses 'EARLIER in this call'.
    [Fact]
    public void ASiblingReferenceToALaterRecordIsRefused()
    {
        var err = W.Refused("HcNcRejSibFwd.esp",
            Spec("DialogTopic", "HcNcSfTopic"),
            Under("HcNcSfTopic", "DialogResponses", "HcNcSfL1", Req("DialogResponses", "PreviousDialog", "Set", "@HcNcSfL2")),
            Under("HcNcSfTopic", "DialogResponses", "HcNcSfL2"));
        Assert.Contains("EARLIER", err, StringComparison.OrdinalIgnoreCase);
    }

    // REJ-SIBNONFL: an @ref on FavorLevel refuses 'only valid on a FormLink field'.
    [Fact]
    public void ASiblingReferenceOnANonLinkFieldIsRefused()
    {
        var err = W.Refused("HcNcRejSibNonFl.esp",
            Spec("DialogTopic", "HcNcSnTopic"),
            Under("HcNcSnTopic", "DialogResponses", "HcNcSnL1", Req("DialogResponses", "FavorLevel", "Set", "@HcNcSnTopic")));
        Assert.Contains("only valid on a FormLink field", err, StringComparison.OrdinalIgnoreCase);
    }

    // REJ-SIBLIST-SETIDX: an @ref via SetAtIndex on a link list refuses; the gate opens only for Add.
    [Fact]
    public void ASiblingReferenceThroughSetAtIndexIsRefused()
    {
        var err = W.Refused("HcNcRejSibListSetIdx.esp",
            Spec("DialogTopic", "HcNcSlsTopic"),
            Under("HcNcSlsTopic", "DialogResponses", "HcNcSlsL1", Req("DialogResponses", "LinkTo", "SetAtIndex", "@HcNcSlsTopic", "0")));
        Assert.Contains("Add value on a FormLink list", err, StringComparison.OrdinalIgnoreCase);
    }

    // REJ-SIBFWD-LIST: a LinkTo Add=@laterSibling refuses 'EARLIER in this call'.
    [Fact]
    public void ASiblingReferenceAddedToALinkListForALaterRecordIsRefused()
    {
        var err = W.Refused("HcNcRejSibFwdList.esp",
            Spec("DialogTopic", "HcNcSflTopic"),
            Under("HcNcSflTopic", "DialogResponses", "HcNcSflL1", Req("DialogResponses", "LinkTo", "Add", "@HcNcSflLater")),
            Spec("DialogTopic", "HcNcSflLater"));
        Assert.Contains("EARLIER", err, StringComparison.OrdinalIgnoreCase);
    }

    // REJ-SIBDICT: an @ref in a dict Merge's Entries values refuses 'not inside a dict value'.
    [Fact]
    public void ASiblingReferenceInADictValueIsRefused()
    {
        var err = W.Refused("HcNcRejSibDict.esp",
            Spec("Class", "HcNcSdClass", new WriteRequest
            {
                RecordType = "Class", Path = new[] { "SkillWeights" }, Verb = "Merge",
                Entries = new Dictionary<string, string> { ["OneHanded"] = "@HcNcSdClass" },
            }));
        Assert.Contains("not inside a dict value", err, StringComparison.OrdinalIgnoreCase);
    }

    // REJ-SIBAPPLY: an @ref with no sibling set (the edit path) refuses with the edit-context copy naming the FormID route.
    [Fact]
    public void ASiblingReferenceOnTheEditPathIsRefused()
    {
        var reject = TestCorpus.Rulebook.Validate(Req("DialogResponses", "PreviousDialog", "Set", "@AnySibling"));
        Assert.NotNull(reject);
        Assert.Contains("no same-call creations to point at", reject, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FormID", reject, StringComparison.OrdinalIgnoreCase);
    }

    // SIBREF-SELF: a line's PreviousDialog=@itself lands its own FormKey.
    [Fact]
    public void ARecordsReferenceToItselfLandsItsOwnFormKey()
    {
        var (o, path) = W.Create("HcNcSelf.esp",
            Spec("DialogTopic", "HcNcSelfTopic"),
            Under("HcNcSelfTopic", "DialogResponses", "HcNcSelfL1", Req("DialogResponses", "PreviousDialog", "Set", "@HcNcSelfL1")));
        Assert.True(o.Success, o.Error);
        var l1 = o.Created[1].FormKey;
        Assert.Equal(l1, Info(W.Open(path), l1)!.PreviousDialog.FormKey);
    }

    // SIBREF-STRUCT: @self inside a composed QuestFragmentAlias' Sets (Property.Object) lands the quest's own FormKey.
    [Fact]
    public void ASelfReferenceInsideAComposesSetsLandsTheRecordsFormKey()
    {
        var (o, path) = W.Create("HcNcVmadSelf.esp",
            Spec("Quest", "HcNcVmadQuest", AliasAdd(
                Req("QuestFragmentAlias", "Property.Object", "Set", "@HcNcVmadQuest"),
                Req("QuestFragmentAlias", "Property.Alias", "Set", "0"))));
        Assert.True(o.Success, o.Error);
        var q = Assert.Single(o.Created).FormKey;
        Assert.Equal(q, AliasObject(W.Open(path), q));
    }

    // SIBREF-STRUCT-FIELDS: Object='@self' in a compose's flat Fields lands the quest's own FormKey.
    [Fact]
    public void ASelfReferenceInAComposeFieldLandsTheRecordsFormKey()
    {
        var (o, path) = W.Create("HcNcVmadSelfF.esp",
            Spec("Quest", "HcNcVmadQuestF", AliasAdd(new WriteRequest
            {
                RecordType = "QuestFragmentAlias", Path = new[] { "Property" }, Verb = "Set",
                Struct = new StructSpec { Type = "ScriptObjectProperty", Fields = new() { ["Object"] = "@HcNcVmadQuestF", ["Alias"] = "0" } },
            })));
        Assert.True(o.Success, o.Error);
        var q = Assert.Single(o.Created).FormKey;
        Assert.Equal(q, AliasObject(W.Open(path), q));
    }

    // REJ-SIBSTRUCT-FWD: a compose-Sets @ref to a later sibling refuses 'EARLIER in this call'.
    [Fact]
    public void ASiblingReferenceInsideAComposeToALaterRecordIsRefused()
    {
        var err = W.Refused("HcNcRejSibStructFwd.esp",
            Spec("Quest", "HcNcSfwQuest", AliasAdd(Req("QuestFragmentAlias", "Property.Object", "Set", "@HcNcSfwLater"))),
            Spec("Keyword", "HcNcSfwLater"));
        Assert.Contains("EARLIER", err, StringComparison.OrdinalIgnoreCase);
    }

    // REJ-SIBSTRUCT-NONFL: a compose-Sets @ref on Property.Name refuses 'only valid on a FormLink field'.
    [Fact]
    public void ASiblingReferenceInsideAComposeOnANonLinkFieldIsRefused()
    {
        var err = W.Refused("HcNcRejSibStructNonFl.esp",
            Spec("Quest", "HcNcSnfQuest", AliasAdd(Req("QuestFragmentAlias", "Property.Name", "Set", "@HcNcSnfQuest"))));
        Assert.Contains("only valid on a FormLink field", err, StringComparison.OrdinalIgnoreCase);
    }

    // REJ-SIBSTRAYSTRUCT: a struct= beside an admitted '@' value refuses 'remove struct='.
    [Fact]
    public void AStrayComposeBesideASiblingReferenceIsRefused()
    {
        var req = new WriteRequest
        {
            RecordType = "DialogResponses", Path = new[] { "Topic" }, Verb = "Set", Value = "@HcNcStrayTopic",
            Struct = new StructSpec { Type = "QuestFragmentAlias", Fields = new() { ["Version"] = "@Typo" } },
        };
        var reject = TestCorpus.Rulebook.Validate(req, new[] { "HcNcStrayTopic" });
        Assert.NotNull(reject);
        Assert.Contains("remove struct=", reject, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() => _rig?.Dispose();
}
