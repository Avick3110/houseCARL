using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>#1160 review fold: paths the read walk can read plan clean and match as on main; a leftover runtime miss keeps its own cause.</summary>
[Trait("tier", "unit")]
public sealed class SchemaFirstPathFoldTests
{
    readonly SkyrimMod _mod = new(new ModKey("HcFold1168", ModType.Master), SkyrimRelease.SkyrimSE);
    readonly Dictionary<FormKey, FormKey> _parents = new();
    readonly Dictionary<FormKey, IMajorRecordGetter> _bodies = new();

    DialogResponses NewInfo(DialogTopic topic, byte unknown2)
    {
        var i = new DialogResponses(_mod.GetNextFormKey(), SkyrimRelease.SkyrimSE);
        i.Responses.Add(new DialogResponse { Unknown2 = new byte[] { unknown2, 7 } });
        topic.Responses.Add(i);
        _bodies[i.FormKey] = i;
        _parents[i.FormKey] = topic.FormKey;
        return i;
    }

    DialogTopic NewTopic(string eid)
    {
        var t = _mod.DialogTopics.AddNew(eid);
        _bodies[t.FormKey] = t;
        return t;
    }

    FieldPredicateSet Bound(string clause, params string[] types)
    {
        Assert.Null(SchemaPlan.Set(clause, out var set, types));
        set.BindResolution(fk => _bodies.ContainsKey(fk) ? _mod.ModKey.FileName.String : null,
                           fk => _bodies.GetValueOrDefault(fk),
                           fk => _parents.TryGetValue(fk, out var p) ? p : null);
        return set;
    }

    static List<FormKey> Hits(FieldPredicateSet set, IEnumerable<IMajorRecordGetter> records) =>
        records.Where(r => set.Matches(r)).Select(r => r.FormKey).ToList();

    // A pseudo right side behind a containment hop walks the hop only: the linked INFO's topic EditorID is read, as on main.
    [Fact]
    public void APseudoRightSideBehindAParentHopPlansAndMatches()
    {
        var pc = NewTopic("PcTopic");
        var other = NewTopic("OtherTopic");
        var target = NewInfo(pc, 0);
        var hit = NewInfo(other, 0); hit.PreviousDialog.SetTo(target.FormKey);
        var miss = NewInfo(other, 0); miss.PreviousDialog.SetTo(hit.FormKey);
        var set = Bound("PreviousDialog->*parent.editorid = PcTopic", "INFO");
        Assert.Equal(new[] { hit.FormKey }, Hits(set, new IMajorRecordGetter[] { target, hit, miss }));
        Assert.Null(SchemaPlan.Of("PreviousDialog->*parent.formid in [000800:HcFold1168.esm]", "INFO"));
    }

    // The review's repro: 'editorid' is no longer judged as a schema field (no case slip); no record contains a Quest, which main's note said after the scan.
    [Fact]
    public void TheReproIsJudgedOnItsHopNotAsACaseSlip()
    {
        var refusal = SchemaPlan.Of("*parent.Quest->*parent.editorid = PcQuest", "INFO");
        Assert.Contains("no record contains a Quest", refusal);
        Assert.DoesNotContain("case-sensitive", refusal);
    }

    // A raw byte value indexed by an integer key passes the plan; on a plugin read back the answer is main's: a read fault on every record, no match.
    [Fact]
    public void AnIntegerKeyOnRawBytesPassesThePlanAndAnswersAsMain()
    {
        var topic = NewTopic("BytesTopic");
        NewInfo(topic, 0); NewInfo(topic, 1);
        var dir = Path.Combine(Path.GetTempPath(), "hc-fold1168-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, _mod.ModKey.FileName);
            _mod.BeginWrite.ToPath(path).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
            using var back = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
            var infos = back.EnumerateMajorRecords().Where(r => r is IDialogResponsesGetter).ToList();
            var set = Bound("Responses[*any].Unknown2[0] = 0", "INFO");
            Assert.Empty(Hits(set, infos));
            Assert.Contains("could not be READ on any of 2 scanned record(s)", set.AccountingNote());
        }
        finally { Directory.Delete(dir, true); }
    }

    // A member below a scalar is judged on its CLR type: String.Length reads, as on main; a made-up one is still refused.
    [Fact]
    public void AMemberBelowAScalarIsJudgedOnItsClrType()
    {
        var longOne = _mod.Armors.AddNew(new string('L', 41));
        var shortOne = _mod.Armors.AddNew("Short");
        Assert.Equal(new[] { longOne.FormKey }, Hits(Bound("EditorID.Length > 40"), new IMajorRecordGetter[] { longOne, shortOne }));
        Assert.Null(SchemaPlan.Of("EditorID.Length > 40", "ARMO"));
        Assert.Contains("'Lenght' is not a member", SchemaPlan.Of("EditorID.Lenght > 40", "ARMO"));
        Assert.NotNull(SchemaPlan.Of("Value.Nope > 1", "ARMO"));
    }

    // A member the plan passed but the read walk did not find is said as not found, never as valid but unset.
    [Fact]
    public void ALeftoverRuntimeMissIsNotFoundNotUnset()
    {
        var armors = new IMajorRecordGetter[] { _mod.Armors.AddNew("MissA"), _mod.Armors.AddNew("MissB") };
        // The plan cannot tell a quantifier below a CLR member; the read finds an Int32, not a list.
        var set = Bound("EditorID.Length[*any] = 1", "ARMO");
        Assert.Empty(Hits(set, armors));
        var note = set.AccountingNote();
        Assert.Contains("was not found on any of 2 scanned record(s)", note);
        Assert.DoesNotContain("UNSET", note);
    }

    // A member that resolved and held no value is still valid but unset.
    [Fact]
    public void AResolvedMemberWithNoValueIsValidButUnset()
    {
        var armors = new IMajorRecordGetter[] { _mod.Armors.AddNew("NoDescA"), _mod.Armors.AddNew("NoDescB") };
        var set = Bound("Description contains x", "ARMO");
        Assert.Empty(Hits(set, armors));
        Assert.Contains("the field IS VALID; it is simply UNSET", set.AccountingNote());
    }

    // A class the plan rules out fetches no containing record.
    [Fact]
    public void ARuledOutClassFetchesNoParent()
    {
        var cell = new Cell(_mod.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "FoldCell" };
        _bodies[cell.FormKey] = cell;
        var refs = Enumerable.Range(0, 3).Select(_ =>
        {
            var r = new PlacedObject(_mod.GetNextFormKey(), SkyrimRelease.SkyrimSE);
            _parents[r.FormKey] = cell.FormKey;
            return (IMajorRecordGetter)r;
        }).ToList();
        var (set, err) = FieldPredicateSet.Parse(new[] { "*parent.EditorID exists", "BasicStats.Damage > 0" });
        Assert.Null(err);
        Assert.Null(set!.Plan(TestCorpus.Rulebook.PathWalk, null));
        set.BindResolution(_ => "x", fk => _bodies.GetValueOrDefault(fk), fk => _parents.TryGetValue(fk, out var p) ? p : null,
                           want => want.Where(_bodies.ContainsKey).ToDictionary(k => k, k => _bodies[k]));
        set.HoldParents(refs);
        Assert.Empty(Hits(set, refs));
        Assert.Equal(0, set.ParentBodyFetches);
    }
}

/// <summary>#1160 review fold end to end: an untyped AND no type in scope carries whole says so on every form.</summary>
[Trait("tier", "integration")]
public sealed class SchemaFirstPathFoldRecordsTests : ArtifactTestBase, IClassFixture<ArtifactFixture>
{
    public SchemaFirstPathFoldRecordsTests(ArtifactFixture f) : base(f) { }

    const string Sentence = "no record type in scope carries all of the predicates ('ArmorRating > 0', 'BasicStats.Damage > 0')";

    static readonly string[] Where = { "ArmorRating > 0", "BasicStats.Damage > 0" };

    RecordsTools.RecordsScope Master => new() { names = new[] { W.MasterName } };

    [Fact]
    public void AnUntypedAndNoTypeCarriesWholeSaysSoOnText() =>
        Assert.Equal(1, CountOf(RecordsTools.Records(Svc, plugins: Master, where: Where), Sentence));

    [Fact]
    public void AnUntypedAndNoTypeCarriesWholeSaysSoOnJson()
    {
        var j = Je(RecordsTools.Records(Svc, plugins: Master, where: Where, format: "json"));
        Assert.Equal(0, j.GetProperty("total").GetInt32());
        Assert.Single(j.GetProperty("notes").EnumerateArray(), n => n.GetString()!.Contains(Sentence, StringComparison.Ordinal));
    }

    static int CountOf(string haystack, string needle)
    {
        int n = 0;
        for (int i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = haystack.IndexOf(needle, i + 1, StringComparison.Ordinal)) n++;
        return n;
    }

    [Fact]
    public void AnUntypedAndNoTypeCarriesWholeSaysSoInTheManifest()
    {
        var art = Art("fold-1168-and.jsonl");
        RecordsTools.Records(Svc, plugins: Master, where: Where, to_file: art);
        // The manifest line, read raw so a 0-row artifact is judged too.
        var manifest = Je(File.ReadLines(art).First());
        Assert.Single(manifest.GetProperty("notes").EnumerateArray(), n => n.GetString()!.Contains(Sentence, StringComparison.Ordinal));
    }
}
