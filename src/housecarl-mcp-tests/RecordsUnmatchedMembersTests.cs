using System.Text.Json;
using HousecarlCore;
using HousecarlGenerator;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
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

    [Fact]
    public void AFormidMissIsNamedAsTheCallerTypedIt()
    {
        var lower = $"ffffF0:{W.MasterName.ToLowerInvariant()}";
        const string runtime = "0x00FFFFE0";

        var doc = Je(RecordsTools.Records(Svc, types: new[] { "WEAP" }, format: "json",
                                          where: new[] { $"formid in [{Fid(W.Weapons[0])}, {lower}, {runtime}]" }));

        Assert.Equal(new[] { lower, runtime }, Unmatched(doc));
    }

    [Fact]
    public void ACappedNoteOnAToFileCallPointsAtTheManifest()
    {
        var names = Absent(FieldPredicateSet.UnmatchedShown + 5);
        var art = W.Scratch("unmatched", Guid.NewGuid().ToString("N") + ".jsonl");

        var text = RecordsTools.Records(Svc, types: new[] { "WEAP" }, to_file: art,
                                        where: new[] { $"editorid in [HcRecW0, {string.Join(", ", names)}]" });

        var note = NoteLine(text);
        Assert.Contains("and 5 more (the manifest's notes name them all)", note);
        Assert.DoesNotContain("to_file=", note);
    }
}

/// <summary>Records the scan could not judge, and records only another where= term drops, never read as absent members.</summary>
[Trait("tier", "integration")]
public sealed class UnmatchedMembersJudgementTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "hc-unmatched-judgement-" + Guid.NewGuid().ToString("N"));

    public UnmatchedMembersJudgementTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* temp cleanup best-effort */ } }

    string Write(SkyrimMod mod)
    {
        var path = Path.Combine(_dir, mod.ModKey.FileName.String);
        mod.BeginWrite.ToPath(path).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        return path;
    }

    [Fact]
    public void ADeletedRecordAnotherTermDropsStillCountsAsMatched()
    {
        var mod = new SkyrimMod(new ModKey("HcUnmatchedDeleted", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var live = mod.Perks.AddNew(); live.EditorID = "HcUnmLive";
        var gone = mod.Perks.AddNew(); gone.EditorID = "HcUnmGone";
        var path = Write(mod);
        Assert.Equal(1, ProbeBytes.SetDeletedFlag(path, "PERK", gone.FormKey.ID));

        using var resolver = LoadOrderResolver.Build(new[] { path });
        var svc = LoadOrderService.ForGuard(resolver, new UserConfigStore(Path.Combine(_dir, "houseCARL.user.json")));
        var q = svc.ReadArea.CrossQuery(type: "PERK", references: null, editoridContains: null, conflictsOnly: false, plugins: null,
                                        where: new[] { "editorid in [HcUnmLive, HcUnmGone, HcUnmNone]", "Level > 200" }, limit: 500);

        Assert.Null(q.Error);
        Assert.Equal(new[] { "HcUnmNone" }, q.Unmatched);
        Assert.Null(q.UnmatchedGap);
    }

    [Fact]
    public void AnUnscannableRecordQualifiesTheNoteInsteadOfClaimingAbsence()
    {
        var mod = new SkyrimMod(new ModKey("HcUnmatchedUnscan", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var target = mod.Perks.AddNew(); target.EditorID = "HcUnmTarget";
        var bad = mod.Perks.AddNew(); bad.EditorID = "HcUnmBad";
        bad.Effects.Add(new PerkEntryPointModifyActorValue
        {
            EntryPoint = APerkEntryPointEffect.EntryType.CalculateWeaponDamage,
            ActorValue = ActorValue.OneHanded,
            Value = 1f,
            Modification = PerkEntryPointModifyActorValue.ModificationType.AddAVMult,
        });
        var good = mod.Perks.AddNew(); good.EditorID = "HcUnmGood";
        good.NextPerk.SetTo(target.FormKey);
        var path = Write(mod);
        Assert.Equal(1, ProbeBytes.CorruptEpftBytes(path));

        using var resolver = LoadOrderResolver.Build(new[] { path });
        var svc = LoadOrderService.ForGuard(resolver, new UserConfigStore(Path.Combine(_dir, "houseCARL.user.json")));
        var q = svc.ReadArea.CrossQuery(type: "PERK", references: new[] { target.FormKey }, editoridContains: null, conflictsOnly: false,
                                        plugins: null, where: new[] { "editorid in [HcUnmGood, HcUnmBad]" }, limit: 500);

        Assert.Null(q.Error);
        Assert.Equal(new[] { good.FormKey }, q.Keys);
        Assert.Equal(new[] { "HcUnmBad" }, q.Unmatched);
        var note = q.UnmatchedNote(inManifest: false)!;
        Assert.Contains("but 1 record(s) could not be scanned, so a record may exist for them: HcUnmBad", note);
        Assert.DoesNotContain("have no record", note);
    }
}

/// <summary>A member whose winner sits in a plugin the scan could not open is not called absent, on the winner scan and under where_source=winner.</summary>
[Trait("tier", "integration")]
public sealed class UnmatchedMembersUnreadableTests : IClassFixture<WinnerSourceFixture>
{
    readonly WinnerSourceWorld _w;
    public UnmatchedMembersUnreadableTests(WinnerSourceFixture f) => _w = f.W;

    // Weapon 7 wins in the Mid plugin.
    const string Where = "editorid in [HcWsrcWeap7, HcWsrcWeap20, HcUnmNoSuch]";

    JsonElement Scan(bool whereWinner) => JsonDocument.Parse(RecordsTools.Records(_w.Svc,
        types: whereWinner ? null : new[] { "WEAP" },
        plugins: whereWinner ? new RecordsTools.RecordsScope { names = new[] { _w.MasterName } } : null,
        where: new[] { Where }, where_source: whereWinner ? "winner" : null, format: "json")).RootElement.Clone();

    static List<string> Unmatched(JsonElement doc) =>
        doc.GetProperty("unmatched").EnumerateArray().Select(e => e.GetString()!).ToList();

    static string UnmatchedNote(JsonElement doc) =>
        doc.GetProperty("notes").EnumerateArray().Select(n => n.GetString()!).Single(n => n.Contains("'in' list member(s)"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AReadableOrderNamesOnlyTheTrueMiss(bool whereWinner)
    {
        var doc = Scan(whereWinner);

        Assert.Equal(new[] { "HcUnmNoSuch" }, Unmatched(doc));
        Assert.Contains("have no record in this selection", UnmatchedNote(doc));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AWinnerInAnUnreadablePluginQualifiesTheNote(bool whereWinner)
    {
        JsonElement doc;
        using (new FileStream(_w.MidPath, FileMode.Open, FileAccess.Read, FileShare.None)) doc = Scan(whereWinner);

        Assert.Contains("HcWsrcWeap7", Unmatched(doc));
        var note = UnmatchedNote(doc);
        Assert.Contains("could not", note);
        Assert.Contains("so a record may exist for them", note);
        Assert.DoesNotContain("have no record", note);
    }
}

/// <summary>An inactive plugin's scan, the off-order lane, names its misses too.</summary>
[Collection("render-cost")]
[Trait("tier", "integration")]
public sealed class UnmatchedMembersOffOrderTests
{
    readonly RenderCostWorld _w;
    public UnmatchedMembersOffOrderTests(RenderCostFixture f) => _w = f.W;

    [Fact]
    public void AnOffOrderScanNamesItsMisses()
    {
        var doc = JsonDocument.Parse(RecordsTools.Records(_w.Svc, types: new[] { "WEAP" }, format: "json",
            source: JsonDocument.Parse("\"" + _w.OffOrderName + "\"").RootElement.Clone(),
            where: new[] { "editorid in [HcOffSword0, HcUnmNoSuch]" })).RootElement;

        Assert.Equal(new[] { "HcUnmNoSuch" }, doc.GetProperty("unmatched").EnumerateArray().Select(e => e.GetString()!));
    }
}
