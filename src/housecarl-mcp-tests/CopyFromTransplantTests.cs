using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>CopyFrom transplants a field from a named plugin's version of the record, across every field kind, from
/// an active or an off-order source; and its refusals name the fix. Migrated from the bulk-primitives-wave3 probe's
/// P8b arm.</summary>
[Trait("tier", "integration")]
public sealed class CopyFromTransplantTests : IClassFixture<CopyFromWorld>
{
    readonly CopyFromWorld _w;
    public CopyFromTransplantTests(CopyFromWorld w) => _w = w;

    WritePatchBuilder.PatchOutcome Copy(string path, string patch, string? formid = null, string? from = null)
        => _w.Svc.ApplyEdits(new[]
        {
            new BulkOp { Formid = formid ?? _w.WFid, FieldPath = path, Verb = "CopyFrom", FromPlugin = from ?? _w.MasterName },
        }, patch, null);

    WritePatchBuilder.PatchOutcome One(BulkOp op, string patch) => _w.Svc.ApplyEdits(new[] { op }, patch, null);

    static IWeaponGetter Weapon(ISkyrimModGetter ov, FormKey fk) => ov.Weapons.First(x => x.FormKey == fk);

    static T Read<T>(WritePatchBuilder.PatchOutcome o, Func<ISkyrimModGetter, T> read)
    {
        Assert.True(o.Success, o.Error);
        using var ov = SkyrimMod.CreateFromBinaryOverlay(o.OutputPath, SkyrimRelease.SkyrimSE);
        return read(ov);
    }

    static string Refused(WritePatchBuilder.PatchOutcome o)
    {
        Assert.False(o.Success);
        Assert.NotNull(o.Error);
        return o.Error!;
    }

    // probe: fixture: the replacer WINS W
    [Fact]
    public void TheReplacerWinsTheSubject()
        => Assert.Equal(_w.ReplacerName, System.Text.Json.JsonDocument.Parse(RecordsTools.Records(_w.Svc, formids: new[] { _w.WFid }, format: "json"))
                                             .RootElement.GetProperty("records")[0].GetProperty("winner").GetString());

    // probe: CopyFrom scalar BasicStats.Damage: winner 99 → source 10
    [Fact]
    public void AScalarInASubStructTakesTheSourceValue()
        => Assert.Equal((ushort)10, Read(Copy("BasicStats.Damage", "CfDmg"), ov => Weapon(ov, _w.WKey).BasicStats!.Damage));

    // probe: CopyFrom sub-struct BasicStats (whole): Damage 10
    [Fact]
    public void AWholeSubStructIsDeepCopied()
        => Assert.Equal((ushort)10, Read(Copy("BasicStats", "CfBs"), ov => Weapon(ov, _w.WKey).BasicStats!.Damage));

    // probe: CopyFrom TranslatedString Name: → "Base Sword"
    [Fact]
    public void ATranslatedStringTakesTheSourceText()
        => Assert.Equal("Base Sword", Read(Copy("Name", "CfName"), ov => Weapon(ov, _w.WKey).Name?.String));

    // probe: CopyFrom formlink-list Keywords: winner 0 → source 2
    [Fact]
    public void AFormLinkListTakesEverySourceLink()
        => Assert.Equal(new[] { _w.Kw1, _w.Kw2 },
            Read(Copy("Keywords", "CfKw"), ov => Weapon(ov, _w.WKey).Keywords!.Select(k => k.FormKey).ToArray()));

    // probe: CopyFrom modeled-list Effects (element DeepCopy): winner 0 → source 1
    [Fact]
    public void AModeledListCopiesEachElement()
    {
        var effects = Read(Copy("Effects", "CfEff", formid: _w.PotionFid),
            ov => ov.Ingestibles.First(x => x.FormKey == _w.PotionKey).Effects.Select(e => (e.BaseEffect.FormKey, e.Data!.Magnitude)).ToList());
        Assert.Equal(new[] { (_w.MgefKey, 5f) }, effects);
    }

    // probe: CopyFrom single formlink Template (SetTo): winner null → source w2
    [Fact]
    public void ASingleFormLinkTakesTheSourceKey()
        => Assert.Equal(_w.W2Key, Read(Copy("Template", "CfTmpl"), ov => Weapon(ov, _w.WKey).Template.FormKey));

    // probe: CopyFrom OFF-ORDER source (disabled DonorOld.esp): winner 99 → off-order 77
    [Fact]
    public void AnOffOrderSourceInADisabledModIsCopiedFrom()
        => Assert.Equal((ushort)77, Read(Copy("BasicStats.Damage", "CfOff", from: "DonorOld.esp"), ov => Weapon(ov, _w.WKey).BasicStats!.Damage));

    // probe: refusal: CopyFrom without the source pole → refused ('requires from_source')
    [Fact]
    public void CopyFromWithoutASourceIsRefused()
        => Assert.Contains("requires from_source",
            Refused(One(new BulkOp { Formid = _w.WFid, FieldPath = "BasicStats.Damage", Verb = "CopyFrom" }, "CfNoFrom")));

    // probe: refusal: the source pole on a non-CopyFrom op → refused ('only valid with op=CopyFrom')
    [Fact]
    public void ASourceOnASetIsRefused()
        => Assert.Contains("only valid with op=CopyFrom",
            Refused(One(new BulkOp { Formid = _w.WFid, FieldPath = "BasicStats.Damage", Verb = "Set", Value = "5", FromPlugin = _w.MasterName }, "CfStray")));

    // probe: refusal: mis-cased op 'copyfrom' + the source pole → refused at the mapper ('only valid with op=CopyFrom')
    [Fact]
    public void AMisCasedCopyFromIsNotCopyFrom()
    {
        var e = Refused(One(new BulkOp { Formid = _w.WFid, FieldPath = "BasicStats.Damage", Verb = "copyfrom", FromPlugin = _w.MasterName }, "CfCase"));
        Assert.Contains("only valid with op=CopyFrom", e);
        Assert.Contains("got op=copyfrom", e);
    }

    // probe: refusal: CopyFrom + value → refused ('takes no value')
    [Fact]
    public void CopyFromWithAValueIsRefused()
        => Assert.Contains("takes no value",
            Refused(One(new BulkOp { Formid = _w.WFid, FieldPath = "BasicStats.Damage", Verb = "CopyFrom", FromPlugin = _w.MasterName, Value = "5" }, "CfVal")));

    // probe: refusal: from_plugin not in the load order → refused ('not in the load order')
    [Fact]
    public void ASourceFoundNowhereIsRefusedAndLeavesNoFolder()
    {
        Assert.Contains("not in the load order", Refused(Copy("BasicStats.Damage", "CfNope", from: "Nope.esp")));
        Assert.DoesNotContain(Directory.EnumerateDirectories(_w.ModsDir), d => Path.GetFileName(d).Contains("CfNope"));
    }

    // probe: refusal: from_plugin doesn't define/override the record → refused ('does NOT define or override')
    [Fact]
    public void ASourceThatDoesNotTouchTheRecordIsRefused()
        => Assert.Contains("does NOT define or override",
            Refused(Copy("BasicStats.Damage", "CfNoDef", formid: _w.W2Fid, from: _w.ReplacerName)));

    // probe: refusal: source field unset (W3 has no BasicStats) → refused ('nothing to copy')
    [Fact]
    public void AnUnsetSourceFieldIsRefused()
        => Assert.Contains("nothing to copy", Refused(Copy("BasicStats", "CfAbsent", formid: _w.W3Fid)));

    // probe: refusal: owned-child collection Cell.Persistent + CopyFrom → refused by name
    [Fact]
    public void AnOwnedChildCollectionIsRefusedAtPreFlight()
        => Assert.Contains("owned child records",
            TestCorpus.Rulebook.Validate(new WriteRequest { RecordType = "Cell", Path = new[] { "Persistent" }, Verb = "CopyFrom" }));

    // probe: refusal: CopyFrom in a CREATE op → refused (isn't valid when creating)
    [Fact]
    public void CopyFromInACreateIsRefused()
    {
        var o = _w.Svc.CreateRecordsBatch(new[]
        {
            new CreateOp
            {
                RecordType = "Weapon", Editorid = "CfCreated",
                Operations = new[] { new BulkOp { FieldPath = "BasicStats.Damage", Verb = "CopyFrom", FromPlugin = _w.MasterName } },
            },
        }, "CfCreate", null, false, null, false, false);
        Assert.False(o.Success);
        Assert.Contains("isn't valid when CREATING", o.Error);
    }
}
