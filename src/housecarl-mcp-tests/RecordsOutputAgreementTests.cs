using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A master and a patch over it: an armor the patch restates (same armature list, no alternate block
/// material on either), a weapon the patch changes, GLOBs and a STAT carrying their own header flags, a keyword with
/// a raw header bit, and an unticked plugin of GLOBs off the order.</summary>
public sealed class OutputAgreementWorld : IDisposable
{
    public const string MasterName = "HcOcMaster.esm";
    public const string PatchName = "HcOcPatch.esp";
    public const string OffName = "HcOcOff.esp";
    public string Root { get; }
    public LoadOrderService Svc { get; }
    public FormKey Armor { get; }
    public FormKey Weapon { get; }
    public FormKey ConstantGlobal { get; }
    /// <summary>Constant plus 0x80000, a bit the GLOB's own flags do not name and the generic header enum calls CantWait.</summary>
    public FormKey StrayGlobal { get; }
    public FormKey NeverFadesStatic { get; }
    /// <summary>A KYWD (no type-specific header flags) carrying raw 0x80000.</summary>
    public FormKey RawKeyword { get; }
    /// <summary>A GLOB only the unticked plugin defines.</summary>
    public FormKey OffGlobal { get; }
    /// <summary>An NPC_ the patch restates with no packages on either side.</summary>
    public FormKey Npc { get; }
    /// <summary>A STAT with no header flags that the patch marks HasDistantLOD.</summary>
    public FormKey FlagFlipStatic { get; }

    public OutputAgreementWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-out-agree-" + Guid.NewGuid().ToString("N"));
        var instance = SyntheticInstance.Create(Root);
        var sky = new SkyrimMod(new ModKey("Skyrim", ModType.Master), SkyrimRelease.SkyrimSE);
        SyntheticInstance.WriteMod(instance, "VanillaStub", sky);

        var master = new SkyrimMod(ModKey.FromNameAndExtension(MasterName), SkyrimRelease.SkyrimSE);
        var arma = master.ArmorAddons.AddNew(); arma.EditorID = "HcOcArma";
        var armo = master.Armors.AddNew(); armo.EditorID = "HcOcArmo";
        armo.Armature.Add(arma.ToLink());
        var weap = master.Weapons.AddNew(); weap.EditorID = "HcOcWeap";
        weap.BasicStats = new WeaponBasicStats { Value = 10 };
        var constant = new GlobalFloat(master.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcOcConstant", Data = 1f };
        constant.MajorFlags = Global.MajorFlag.Constant;
        master.Globals.Add(constant);
        var stray = new GlobalFloat(master.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcOcStray", Data = 2f };
        stray.MajorRecordFlagsRaw = (int)Global.MajorFlag.Constant | 0x80000;
        master.Globals.Add(stray);
        var stat = master.Statics.AddNew(); stat.EditorID = "HcOcNeverFades";
        stat.MajorFlags = Static.MajorFlag.NeverFades;
        var flip = master.Statics.AddNew(); flip.EditorID = "HcOcFlagFlip";
        var kywd = master.Keywords.AddNew(); kywd.EditorID = "HcOcRawKeyword";
        kywd.MajorRecordFlagsRaw = 0x80000;
        var npc = master.Npcs.AddNew(); npc.EditorID = "HcOcNpc";
        var topic = master.DialogTopics.AddNew(); topic.EditorID = "HcOcTopic";
        topic.Responses.Add(new DialogResponses(master.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcOcLine" });
        SyntheticInstance.WriteMod(instance, "OcMaster", master, sky);

        var patch = new SkyrimMod(ModKey.FromNameAndExtension(PatchName), SkyrimRelease.SkyrimSE);
        patch.Armors.GetOrAddAsOverride(armo);
        patch.Weapons.GetOrAddAsOverride(weap).BasicStats!.Value = 20;
        patch.Npcs.GetOrAddAsOverride(npc);
        patch.Statics.GetOrAddAsOverride(flip).MajorFlags = Static.MajorFlag.HasDistantLOD;
        SyntheticInstance.WriteMod(instance, "OcPatch", patch, master);

        var off = new SkyrimMod(ModKey.FromNameAndExtension(OffName), SkyrimRelease.SkyrimSE);
        var offA = new GlobalFloat(off.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcOcOffA", Data = 3f };
        off.Globals.Add(offA);
        off.Globals.Add(new GlobalFloat(off.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcOcOffB", Data = 4f });
        off.Armors.GetOrAddAsOverride(armo);
        SyntheticInstance.WriteMod(instance, "OcOff", off, master);
        (OffGlobal, Npc, FlagFlipStatic) = (offA.FormKey, npc.FormKey, flip.FormKey);

        SyntheticInstance.WriteProfile(instance,
            new[] { "# header", "-OcOff", "+OcPatch", "+OcMaster", "+VanillaStub" },
            new[] { "# header", "Skyrim.esm", MasterName, PatchName },
            new[] { "*" + MasterName, "*" + PatchName });

        (Armor, Weapon, ConstantGlobal, StrayGlobal, NeverFadesStatic, RawKeyword) =
            (armo.FormKey, weap.FormKey, constant.FormKey, stray.FormKey, stat.FormKey, kywd.FormKey);
        Svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(Root, "user.json")));
    }

    public string Scratch(string name) => Path.Combine(Root, "scratch", name);

    public void Dispose()
    {
        Svc.Dispose();
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}

/// <summary>#1079: what agreed is named on an identical comparison, a record's header flags read in its own
/// vocabulary, counts_only states a count with no paging clause, and envelope counts and flags are json numbers and
/// booleans.</summary>
[Trait("tier", "integration")]
public sealed class RecordsOutputAgreementTests : IClassFixture<OutputAgreementWorld>
{
    readonly OutputAgreementWorld W;
    public RecordsOutputAgreementTests(OutputAgreementWorld w) => W = w;
    LoadOrderService Svc => W.Svc;

    static string Fid(FormKey fk) => RecordsWorld.Fid(fk);
    static JsonElement Pole(string name) => JsonDocument.Parse("\"" + name + "\"").RootElement.Clone();
    static RecordsTools.RecordsProject Project(string form, params string[] fields) =>
        new() { form = form, fields = fields.Length > 0 ? fields : null };
    static JsonElement Json(string response) => JsonDocument.Parse(response).RootElement.Clone();

    string Tree(string field, string? format = null) =>
        RecordsTools.Records(Svc, formids: new[] { Fid(W.Armor) }, project: Project("tree", field), format: format);

    string Delta(string field, string? format = null) =>
        RecordsTools.Records(Svc, formids: new[] { Fid(W.Armor) }, project: Project("delta", field),
                             versus: Pole(OutputAgreementWorld.MasterName), format: format);

    // ---- A: an identical comparison names what agreed --------------------------------------------------

    [Fact]
    public void Tree_AMatchingListIsNamed_NeverZeroLeavesAgree()
    {
        var r = Tree("Armature");
        Assert.Contains("identical to " + OutputAgreementWorld.PatchName + " across the fields read (1 list with the same 1 item(s))", r);
        Assert.DoesNotContain("0 leaf/leaves agree", r);
    }

    [Fact]
    public void Tree_AFieldAbsentOnBothSidesIsSaidSo_NeverZeroLeavesAgree()
    {
        var r = Tree("AlternateBlockMaterial");
        Assert.Contains("across the fields read (1 field absent on both)", r);
        Assert.DoesNotContain("0 leaf/leaves agree", r);
    }

    [Fact]
    public void Delta_TheTextNamesWhatAgreed()
    {
        Assert.Contains("identical across the fields read (1 list with the same 1 item(s)).", Delta("Armature"));
        Assert.Contains("identical across the fields read (1 field absent on both).", Delta("AlternateBlockMaterial"));
    }

    [Fact]
    public void Json_DeltaAndTreeRowsCarryTheListMatchAndBothAbsentCountsBesideAgreedCount()
    {
        var delta = Json(Delta("Armature", "json")).GetProperty("rows")[0];
        Assert.Equal(0, delta.GetProperty("agreed_count").GetInt32());
        Assert.Equal(1, delta.GetProperty("matched_list_count").GetInt32());
        Assert.Equal(1, delta.GetProperty("matched_item_count").GetInt32());
        Assert.Equal(0, delta.GetProperty("both_absent_count").GetInt32());
        var node = Json(Tree("AlternateBlockMaterial", "json")).GetProperty("rows")[0].GetProperty("nodes")
                   .EnumerateArray().Single(n => !n.GetProperty("is_reference").GetBoolean());
        Assert.Equal(1, node.GetProperty("both_absent_count").GetInt32());
        Assert.Equal(0, node.GetProperty("matched_list_count").GetInt32());
    }

    [Fact]
    public void ToFile_DeltaRowsCarryTheListMatchCount()
    {
        var art = W.Scratch("delta-armature.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(art)!);
        RecordsTools.Records(Svc, formids: new[] { Fid(W.Armor) }, project: Project("delta", "Armature"),
                             versus: Pole(OutputAgreementWorld.MasterName), to_file: art);
        var row = File.ReadLines(art).Select(l => JsonDocument.Parse(l).RootElement)
                      .Single(e => e.TryGetProperty("formid", out _));
        Assert.Equal(1, row.GetProperty("matched_list_count").GetInt32());
        Assert.Equal(0, row.GetProperty("both_absent_count").GetInt32());
        var schema = JsonDocument.Parse(File.ReadLines(art).First()).RootElement.GetProperty("row_schema");
        Assert.Contains(schema.EnumerateArray(), e => e.GetString() == "matched_list_count");
        Assert.Contains(schema.EnumerateArray(), e => e.GetString() == "both_absent_count");
    }

    // A capped comparison counts no agreement: where the cap fell decides which lines were there to agree.
    [Fact]
    public void Compare_ACappedReadCountsNoBothAbsentField()
    {
        static RecordFields Read(params FieldValue[] f) => new("ARMO", "000800:X.esm", null, f);
        var absent = new FieldValue("AlternateBlockMaterial", false, null, ReadEngine.AbsentNote, Present: false);
        var cap = new FieldValue("…", false, null, "[cap]");
        var d = FieldsDiff.Compare(Read(absent, cap), Read(absent));
        Assert.False(d.Complete);
        Assert.Equal(0, d.BothAbsentCount);
        Assert.Equal(1, FieldsDiff.Compare(Read(absent), Read(absent)).BothAbsentCount);
    }

    // Aaron's pick (c): a localized string unresolved on either side is not compared and never counted absent.
    [Fact]
    public void Compare_AnUnresolvedStringIsCountedApartFromAbsence()
    {
        static RecordFields Read(params FieldValue[] f) => new("ARMO", "000800:X.esm", null, f);
        var unresolved = new FieldValue("Name", false, null, ReadEngine.UnresolvedStringNote);
        var absent = new FieldValue("Name", false, null, ReadEngine.AbsentNote, Present: false);
        foreach (var d in new[] { FieldsDiff.Compare(Read(unresolved), Read(unresolved)), FieldsDiff.Compare(Read(unresolved), Read(absent)) })
        {
            Assert.Equal(0, d.BothAbsentCount);
            Assert.Equal(1, d.UnresolvedStringCount);
            Assert.Equal("1 localized string unresolved, not compared", d.Agreement("leaf/leaves"));
        }
        Assert.Equal("2 localized strings unresolved, not compared",
            FieldsDiff.Compare(Read(unresolved, unresolved with { Path = "Description" }), Read(unresolved, unresolved with { Path = "Description" })).Agreement("leaf/leaves"));
    }

    [Fact]
    public void Json_RowsCarryTheUnresolvedStringCount()
    {
        Assert.Equal(0, Json(Delta("Armature", "json")).GetProperty("rows")[0].GetProperty("unresolved_string_count").GetInt32());
        var art = W.Scratch("delta-unresolved.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(art)!);
        RecordsTools.Records(Svc, formids: new[] { Fid(W.Armor) }, project: Project("delta", "Armature"),
                             versus: Pole(OutputAgreementWorld.MasterName), to_file: art);
        var schema = JsonDocument.Parse(File.ReadLines(art).First()).RootElement.GetProperty("row_schema");
        Assert.Contains(schema.EnumerateArray(), e => e.GetString() == "unresolved_string_count");
    }

    // Several of each kind: the plural phrases, joined by ", ".
    [Fact]
    public void Compare_SeveralOfEachKindReadAsOnePhrase()
    {
        static RecordFields Read(params FieldValue[] f) => new("ARMO", "000800:X.esm", null, f);
        FieldValue[] lines =
        {
            new("Value", true, "5", null),
            new("Keywords", false, null, "[list: 1 item(s)]"), new("Keywords[0]", true, "000801:X.esm", null),
            new("Armature", false, null, "[list: 2 item(s)]"), new("Armature[0]", true, "000802:X.esm", null), new("Armature[1]", true, "000803:X.esm", null),
            new("AlternateBlockMaterial", false, null, ReadEngine.AbsentNote, Present: false),
            new("ObjectEffect", false, null, ReadEngine.AbsentNote, Present: false),
        };
        Assert.Equal("1 leaf/leaves agree, 2 lists with the same items (3 in all), 2 fields absent on both",
                     FieldsDiff.Compare(Read(lines), Read(lines)).Agreement("leaf/leaves"));
    }

    // A header-flags difference shows each side's decode beside its token.
    [Fact]
    public void Delta_AHeaderFlagsDifferenceCarriesTheDecode()
    {
        var r = RecordsTools.Records(Svc, formids: new[] { Fid(W.FlagFlipStatic) }, project: Project("delta", "SkyrimMajorRecordFlags"),
                                     versus: Pole(OutputAgreementWorld.MasterName));
        Assert.Contains("SkyrimMajorRecordFlags=32768 [HasDistantLOD]", r);
    }

    // Pins unchanged behaviour: the delta census still counts a matched list and a both-absent field as identical.
    [Theory]
    [InlineData("Armature")]
    [InlineData("AlternateBlockMaterial")]
    public void DeltaCensus_StillCountsBothKindsAsIdentical(string field)
    {
        var r = RecordsTools.Records(Svc, types: new[] { "ARMO" }, project: Project("delta", field),
                                     versus: Pole(OutputAgreementWorld.MasterName), counts_only: true);
        Assert.Contains("identical=1", r);
    }

    // Pins unchanged behaviour: a real value difference renders as a difference, not as identical.
    [Fact]
    public void Delta_AValueDifferenceStillRendersAsADifference()
    {
        var r = RecordsTools.Records(Svc, formids: new[] { Fid(W.Weapon) }, project: Project("delta", "BasicStats.Value"),
                                     versus: Pole(OutputAgreementWorld.MasterName));
        Assert.Contains("1 difference", r);
        Assert.DoesNotContain("identical across", r);
    }

    // A list empty on both sides is named, never "no differing fields" with every count 0.
    [Fact]
    public void TreeAndDelta_AListEmptyOnBothSidesIsNamed()
    {
        var tree = RecordsTools.Records(Svc, formids: new[] { Fid(W.Npc) }, project: Project("tree", "Packages"));
        Assert.Contains("across the fields read (1 list empty on both)", tree);
        Assert.DoesNotContain("no differing fields", tree);
        var delta = RecordsTools.Records(Svc, formids: new[] { Fid(W.Npc) }, project: Project("delta", "Packages"),
                                         versus: Pole(OutputAgreementWorld.MasterName));
        Assert.Contains("identical across the fields read (1 list empty on both).", delta);
        var node = Json(RecordsTools.Records(Svc, formids: new[] { Fid(W.Npc) }, project: Project("tree", "Packages"), format: "json"))
                   .GetProperty("rows")[0].GetProperty("nodes").EnumerateArray().Single(n => !n.GetProperty("is_reference").GetBoolean());
        Assert.Equal(1, node.GetProperty("matched_list_count").GetInt32());
        Assert.Equal(0, node.GetProperty("matched_item_count").GetInt32());
        var row = Json(RecordsTools.Records(Svc, formids: new[] { Fid(W.Npc) }, project: Project("delta", "Packages"),
                                            versus: Pole(OutputAgreementWorld.MasterName), format: "json")).GetProperty("rows")[0];
        Assert.Equal(1, row.GetProperty("matched_list_count").GetInt32());
    }

    // Pins unchanged behaviour: the delta census counts a list empty on both sides as identical.
    [Fact]
    public void DeltaCensus_CountsAListEmptyOnBothSidesAsIdentical()
    {
        var r = RecordsTools.Records(Svc, types: new[] { "NPC_" }, project: Project("delta", "Packages"),
                                     versus: Pole(OutputAgreementWorld.MasterName), counts_only: true);
        Assert.Contains("identical=1", r);
    }

    // ---- B: the header-flags leaf reads in the record's own vocabulary ----------------------------------

    string HeaderFlags(FormKey fk) =>
        RecordsTools.Records(Svc, formids: new[] { Fid(fk) }, project: Project("fields", "MajorFlags", "SkyrimMajorRecordFlags"));

    [Fact]
    public void Glob_TheHeaderFlagsLeafReadsConstantAgreeingWithMajorFlags()
    {
        var r = HeaderFlags(W.ConstantGlobal);
        Assert.Contains("MajorFlags = Constant", r);
        Assert.Contains("SkyrimMajorRecordFlags = 64   (Constant)", r);
    }

    [Fact]
    public void Stat_NeverFadesReadsNeverFadesOnBothLeaves_NeverNotPlayable()
    {
        var r = HeaderFlags(W.NeverFadesStatic);
        Assert.Contains("SkyrimMajorRecordFlags = 4   (NeverFades)", r);
        Assert.DoesNotContain("NotPlayable", r);
    }

    // Aaron's pick (typed first, generic for the remainder): a bit the GLOB's own flags lack keeps its generic name.
    [Fact]
    public void Glob_ABitItsOwnFlagsLackReadsByTheGenericName()
    {
        Assert.Contains("SkyrimMajorRecordFlags = 524352   (Constant | CantWait)", HeaderFlags(W.StrayGlobal));
    }

    [Fact]
    public void Has_ResolvesTheRecordsOwnFlagName()
    {
        var r = RecordsTools.Records(Svc, types: new[] { "STAT" }, where: new[] { "SkyrimMajorRecordFlags has NeverFades" });
        Assert.Contains("HcOcNeverFades", r);
    }

    [Fact]
    public void Has_AGenericNameResolvesOnlyForABitTheOwnFlagsLeaveUnnamed()
    {
        Assert.Contains("HcOcStray", RecordsTools.Records(Svc, types: new[] { "GLOB" }, where: new[] { "SkyrimMajorRecordFlags has CantWait" }));
        Assert.Contains("error: predicate", RecordsTools.Records(Svc, types: new[] { "STAT" }, where: new[] { "SkyrimMajorRecordFlags has NotPlayable" }));
    }

    string Where(string type, string predicate) => RecordsTools.Records(Svc, types: new[] { type }, where: new[] { predicate });

    [Theory]
    [InlineData("SkyrimMajorRecordFlags = NeverFades")]
    [InlineData("SkyrimMajorRecordFlags in [NeverFades]")]
    public void EqAndIn_ResolveTheRecordsOwnFlagName(string predicate) => Assert.Contains("HcOcNeverFades", Where("STAT", predicate));

    [Theory]
    [InlineData("SkyrimMajorRecordFlags = Constant")]
    [InlineData("SkyrimMajorRecordFlags in [Constant]")]
    public void EqAndIn_MatchAConstantGlob_NotRefused(string predicate)
    {
        var r = Where("GLOB", predicate);
        Assert.DoesNotContain("error:", r);
        Assert.Contains("HcOcConstant", r);
        Assert.DoesNotContain("HcOcStray", r);
    }

    // A generic name the record's own flags rename is refused, as 'has' refuses it, never matched by another type's meaning.
    [Theory]
    [InlineData("SkyrimMajorRecordFlags = NotPlayable")]
    [InlineData("SkyrimMajorRecordFlags in [NotPlayable]")]
    public void EqAndIn_RefuseAGenericNameTheRecordsOwnFlagsRename(string predicate)
    {
        var r = Where("STAT", predicate);
        Assert.Contains("error: predicate", r);
        Assert.DoesNotContain("HcOcNeverFades", r);
        Assert.Contains("NeverFades", r);
        Assert.Contains("MajorFlags", r);
    }

    // A multi-type predicate keeps a literal one of its types holds; the type that renames the bit does not match it.
    [Fact]
    public void In_AMultiTypeScanMatchesTheGenericNameOnlyWhereItIsTheRecordsOwn()
    {
        var r = RecordsTools.Records(Svc, types: new[] { "STAT", "KYWD" }, where: new[] { "SkyrimMajorRecordFlags in [NotPlayable]" });
        Assert.DoesNotContain("error:", r);
        Assert.DoesNotContain("HcOcNeverFades", r);
    }

    [Fact]
    public void NeAndNotIn_AreTheComplements()
    {
        foreach (var r in new[] { Where("GLOB", "SkyrimMajorRecordFlags != Constant"), Where("GLOB", "SkyrimMajorRecordFlags not in [Constant]") })
        {
            Assert.DoesNotContain("HcOcConstant", r);
            Assert.Contains("HcOcStray", r);
        }
    }

    // Aaron's pick: the generic name serves the bit the GLOB's own flags leave unnamed.
    [Fact]
    public void Eq_TheGenericRemainderNameMatches()
    {
        var r = Where("GLOB", "SkyrimMajorRecordFlags = Constant | CantWait");
        Assert.Contains("HcOcStray", r);
        Assert.DoesNotContain("HcOcConstant", r);
    }

    // Pins unchanged behaviour: integer operands, and a type with no own flags taking the generic names.
    [Fact]
    public void Eq_IntegerOperandsAndAKeywordsGenericNamesAsBefore()
    {
        Assert.Contains("HcOcConstant", Where("GLOB", "SkyrimMajorRecordFlags = 64"));
        Assert.Contains("HcOcConstant", Where("GLOB", "SkyrimMajorRecordFlags = 0x40"));
        Assert.Contains("HcOcRawKeyword", Where("KYWD", "SkyrimMajorRecordFlags = CantWait"));
        Assert.Contains("HcOcRawKeyword", Where("KYWD", "SkyrimMajorRecordFlags in [CantWait]"));
    }

    // Pins unchanged behaviour: a type with no flags of its own decodes with the generic header enum, as before.
    [Fact]
    public void Kywd_ATypeWithNoOwnFlagsDecodesAsBefore()
    {
        var r = RecordsTools.Records(Svc, formids: new[] { Fid(W.RawKeyword) }, project: Project("fields", "SkyrimMajorRecordFlags"));
        Assert.Contains("SkyrimMajorRecordFlags = CantWait", r);
    }

    // ---- C: counts_only is a count, not a cut window ----------------------------------------------------

    [Fact]
    public void CountsOnly_TextCarriesNoPagingClause()
    {
        var r = RecordsTools.Records(Svc, types: new[] { "GLOB" }, counts_only: true);
        Assert.Contains("scan: 2 matches", r);
        Assert.DoesNotContain("showing first", r);
        Assert.DoesNotContain("raise limit=", r);
        Assert.DoesNotContain("offset=", r);
    }

    [Fact]
    public void CountsOnly_AnOffOrderScanCarriesNoPagingClause()
    {
        var r = RecordsTools.Records(Svc, types: new[] { "GLOB" }, source: Pole(OutputAgreementWorld.OffName), counts_only: true);
        Assert.Contains("2 matches", r);
        Assert.DoesNotContain("showing first", r);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("dense")]
    public void CountsOnly_JsonAndDenseAreNotCapped(string format)
    {
        var doc = Json(RecordsTools.Records(Svc, types: new[] { "GLOB" }, counts_only: true, format: format));
        Assert.False(doc.TryGetProperty("capped", out var c) && c.GetBoolean());
        Assert.Equal(2, doc.GetProperty("total").GetInt32());
    }

    // Pins unchanged behaviour: a real limit= cut still offers paging.
    [Fact]
    public void ALimitCutStillOffersPaging() =>
        Assert.Contains("showing first 1; raise limit=, page with offset=", RecordsTools.Records(Svc, types: new[] { "GLOB" }, limit: 1));

    // ---- D: envelope counts are numbers, flags booleans --------------------------------------------------

    [Theory]
    [InlineData("everything", null)]
    [InlineData("tree", null)]
    [InlineData("delta", OutputAgreementWorld.MasterName)]
    public void Envelope_TotalIsAJsonNumber(string form, string? versus)
    {
        var doc = Json(RecordsTools.Records(Svc, types: new[] { "ARMO" }, project: Project(form),
                                            versus: versus is null ? null : Pole(versus), format: "json"));
        Assert.Equal(JsonValueKind.Number, doc.GetProperty("total").ValueKind);
        Assert.Equal(1, doc.GetProperty("total").GetInt32());
    }

    [Fact]
    public void Envelope_OffOrderTotalIsANumberAndEpochCoversSourceABoolean()
    {
        var doc = Json(RecordsTools.Records(Svc, types: new[] { "GLOB" }, source: Pole(OutputAgreementWorld.OffName),
                                            project: Project("everything"), format: "json"));
        Assert.Equal(2, doc.GetProperty("total").GetInt32());
        Assert.Equal(JsonValueKind.False, doc.GetProperty("epoch_covers_source").ValueKind);
    }

    [Fact]
    public void Envelope_InfoOrderTotalIsAJsonNumber()
    {
        var doc = Json(RecordsTools.Records(Svc, types: new[] { "DIAL" }, project: Project("info_order"), format: "json"));
        Assert.Equal(1, doc.GetProperty("total").GetInt32());
    }

    [Fact]
    public void Envelope_WalkTotalIsAJsonNumber()
    {
        var doc = Json(RecordsTools.Records(Svc, types: new[] { "ARMO" }, walk: new RecordsTools.RecordsWalk { depth = 1 }, format: "json"));
        Assert.Equal(JsonValueKind.Number, doc.GetProperty("total").ValueKind);
    }

    [Fact]
    public void Envelope_OffOrderComparisonTotalIsAJsonNumber()
    {
        var doc = Json(RecordsTools.Records(Svc, types: new[] { "ARMO" }, source: Pole(OutputAgreementWorld.OffName),
                                            project: Project("delta"), versus: Pole("winner"), format: "json"));
        Assert.Equal(1, doc.GetProperty("total").GetInt32());
    }

    [Fact]
    public void Envelope_EpochCoversSourceIsABoolean_OnAListAndAComparisonPole()
    {
        var list = Json(RecordsTools.Records(Svc, formids: new[] { Fid(W.OffGlobal) }, source: Pole(OutputAgreementWorld.OffName), format: "json"));
        Assert.Equal(JsonValueKind.False, list.GetProperty("epoch_covers_source").ValueKind);
        var delta = Json(RecordsTools.Records(Svc, formids: new[] { Fid(W.Armor) }, project: Project("delta"),
                                              versus: Pole(OutputAgreementWorld.OffName), format: "json"));
        Assert.Equal(JsonValueKind.False, delta.GetProperty("epoch_covers_source").ValueKind);
    }

    [Fact]
    public void Envelope_NotTouchedIsAJsonNumber()
    {
        var doc = Json(RecordsTools.Records(Svc, types: new[] { "GLOB" }, plugins: new RecordsTools.RecordsScope { names = new[] { OutputAgreementWorld.MasterName } },
                                            source: Pole(OutputAgreementWorld.PatchName), project: Project("everything"), format: "json"));
        Assert.Equal(2, doc.GetProperty("not_touched").GetInt32());
    }

    // Pins unchanged behaviour: the artifact's query echo stays strings, as the caller passed them.
    [Fact]
    public void ToFile_TheQueryEchoStaysStrings()
    {
        var art = W.Scratch("glob-echo.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(art)!);
        RecordsTools.Records(Svc, types: new[] { "GLOB" }, project: Project("everything"), to_file: art);
        var manifest = JsonDocument.Parse(File.ReadLines(art).First()).RootElement;
        var query = manifest.GetProperty("query");
        Assert.NotEmpty(query.EnumerateObject());
        Assert.All(query.EnumerateObject(), p => Assert.Equal(JsonValueKind.String, p.Value.ValueKind));
    }
}

/// <summary>#1079 on the shared records world: the SkyPatcher post-state pole's coverage flag is a json boolean.</summary>
[Collection("records")]
[Trait("tier", "integration")]
public sealed class RecordsOverlayEnvelopeKindTests : RecordsTestBase
{
    public RecordsOverlayEnvelopeKindTests(RecordsFixture f) : base(f) { }

    [Fact]
    public void Envelope_OverlayPostEpochCoversSourceIsABoolean()
    {
        var doc = Je(RecordsTools.Records(Svc, formids: new[] { Fid(W.Weapons[0]) }, source: Overlay("post"), format: "json"));
        Assert.Equal(JsonValueKind.False, doc.GetProperty("epoch_covers_source").ValueKind);
    }
}
