using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlMcp;
using Xunit;
using static HousecarlMcpTests.ApplyGuardWorld;

namespace HousecarlMcpTests;

/// <summary>apply-guard arms 6-8 (#308): the in-place verify's per-op clause comes off the written file, an empty
/// compose is refused before the file is touched, multi-op calls on one list are reported truthfully, and the
/// count-neutral keyed exemption covers SetAtIndex but not InsertAtIndex. Every test edits the faction's Ranks in
/// the replacer, in place.</summary>
[Trait("tier", "integration")]
public sealed class ApplyGuardVerifyTests : IClassFixture<ApplyGuardCorpus>, IDisposable
{
    readonly ApplyGuardWorld W = new();
    public void Dispose() => W.Dispose();

    string InPlace(string ops, string? format = null) =>
        ApplyTools.Apply(W.Svc, ops: Je(ops), in_place: W.ReplacerName, acknowledge: true, format: format);

    void SeedRanks(params string[] numbers)
    {
        var seed = InPlace(W.AddRanks(numbers));
        Assert.StartsWith("edited ", seed);
    }

    // probe: "a compose with NO fields whose struct serializes to nothing is REFUSED, not reported as landed"
    // probe: "…and the refusal names the settable fields from the TYPE, so the caller knows what to set"
    // probe: "…and it is a PRE-SERIALIZE refusal: the in-place target was never rewritten (size AND mtime)"
    [Fact]
    public void AnEmptyComposeIsRefusedBeforeTheFileIsTouched()
    {
        var before = (new FileInfo(W.ReplacerPath).Length, File.GetLastWriteTimeUtc(W.ReplacerPath));
        var r = InPlace(W.ComposeRankOp(null));
        Assert.StartsWith("error:", r);
        Assert.Contains("no serializable content", r);
        Assert.Contains("Settable fields on Rank:", r);
        Assert.Contains("Number", r);
        Assert.Equal(before, (new FileInfo(W.ReplacerPath).Length, File.GetLastWriteTimeUtc(W.ReplacerPath)));
    }

    // probe: "the same compose WITH a field lands, and its clause is the FILE's (landed_on_disk present)"
    // probe: "…and it is SOURCED as the file's reading rather than the applied edit's"
    [Fact]
    public void AComposeWithAFieldLandsAndItsClauseComesOffTheFile()
    {
        var r = InPlace(W.ComposeRankOp("\"fields\":{\"Number\":\"0\"}"), format: "json");
        using var doc = JsonDocument.Parse(r);
        var op0 = Assert.Single(doc.RootElement.GetProperty("ops").EnumerateArray());
        Assert.Equal(JsonValueKind.String, op0.GetProperty("landed_on_disk").ValueKind);
        Assert.Equal("written_file", op0.GetProperty("landed_source").GetString());
    }

    // probe: "a SUBSTRUCT leaf read off the written file renders the modelled type, not the overlay class"
    [Fact]
    public void ASubstructLeafReadOffAnOverlayRendersTheModelledType()
    {
        using var ovr = SkyrimMod.CreateFromBinaryOverlay(W.ReplacerPath, SkyrimRelease.SkyrimSE);
        var subj = ovr.Weapons.First(w => w.FormKey == W.SubjectKey);
        var note = ReadEngine.ReadFields(subj, new[] { "BasicStats" }).Fields.First().Note;
        Assert.NotNull(note);
        Assert.StartsWith("[WeaponBasicStats]", note);
        Assert.DoesNotContain("BinaryOverlay", note);
    }

    // probe: "two Adds to ONE list in one call: the superseded op says so — its clause is the applied edit's, not the later op's file reading"
    // probe: "…and the list really carries BOTH ranks on disk (the write the arm is defending was correct)"
    [Fact]
    public void TwoAddsToOneListMarkTheEarlierSupersededAndBothLand()
    {
        var r = InPlace(W.AddRanks("1", "2"));
        Assert.Contains("a later op in this call wrote the same field", r);
        Assert.Equal(2, W.RanksOnDisk());
    }

    string RemoveRank(string key) =>
        "{\"formid\":\"" + W.FactionFid + "\",\"field_path\":\"Ranks\",\"op\":\"Remove\",\"key\":\"" + key + "\"}";

    // probe: "seeded three ranks for the keyed-op arm"
    // probe: "two key-addressed Removes in one call: BOTH land (the count drops by two)"
    // probe: "…and NEITHER is slandered — no op's clause says the re-opened file could not answer for it"
    [Fact]
    public void TwoKeyedRemovesInOneCallBothLandAndNeitherIsDoubted()
    {
        SeedRanks("7", "8", "9");
        Assert.Equal(3, W.RanksOnDisk());
        var r = InPlace("[" + RemoveRank("2") + "," + RemoveRank("0") + "]");
        Assert.Equal(1, W.RanksOnDisk());
        Assert.DoesNotContain("the re-opened file did not answer for this op", r);
    }

    string KeyedRankOp(string verb, string idx, string val) =>
        "{\"formid\":\"" + W.FactionFid + "\",\"field_path\":\"Ranks\",\"op\":\"" + verb + "\",\"key\":\"" + idx
        + "\",\"compose\":{\"type\":\"Rank\",\"fields\":{\"Number\":\"" + val + "\"}}}";

    // probe: "two ops on DIFFERENT elements each keep their own file reading (neither is written off)"
    [Fact]
    public void TwoSetAtIndexOnDifferentElementsEachKeepTheirFileReading()
    {
        SeedRanks("1", "2");
        var r = InPlace("[" + KeyedRankOp("SetAtIndex", "0", "41") + "," + KeyedRankOp("SetAtIndex", "1", "42") + "]", format: "json");
        Assert.Equal(new[] { "written_file", "written_file" }, LandedSources(r));
    }

    // probe: "the verify pass reads the FILE: the op comes back with the file's own count, not the claim's"
    // probe: "...and the claim's own in-memory reading is preserved beside it, unchanged"
    [Fact]
    public void TheVerifyPassReadsTheFileAndKeepsTheClaimBesideIt()
    {
        SeedRanks("1", "2");
        using var back = SkyrimMod.CreateFromBinaryOverlay(W.ReplacerPath, SkyrimRelease.SkyrimSE);
        var fk = FormKey.Factory(W.FactionFid);
        var req = new WriteRequest { RecordType = "Faction", Path = new[] { "Ranks" }, Verb = "Add" };
        var claim = new WritePatchBuilder.OpResult(fk, "Faction", "Add Ranks", true, null, "[list: 5 item(s)]", "now 5 item(s)");
        var verified = WritePatchBuilder.VerifyLandedAgainstFile(back, new[] { (fk, (WriteRequest?)req) }, new[] { claim });
        var op = Assert.Single(verified);
        Assert.NotNull(op.LandedOnDisk);
        Assert.DoesNotContain("5", op.LandedOnDisk);
        Assert.Equal("now 5 item(s)", op.Landed);
    }

    // probe: "two key-addressed InsertAtIndex ops in one call: BOTH land (the count rises by two)"
    // probe: "…and InsertAtIndex is NOT admitted to the count-neutral keyed exemption: the earlier op reads 'superseded'"
    // probe: "…and the change summary says INSERTED at the index, not just a new count"
    [Fact]
    public void TwoInsertAtIndexOpsLandAndTheEarlierIsSuperseded()
    {
        SeedRanks("1", "2");
        var r = InPlace("[" + KeyedRankOp("InsertAtIndex", "0", "51") + "," + KeyedRankOp("InsertAtIndex", "1", "52") + "]", format: "json");
        Assert.Equal(4, W.RanksOnDisk());
        Assert.Equal(new[] { "superseded", "written_file" }, LandedSources(r));
        Assert.Contains("inserted [0]", r);
        Assert.Contains("inserted [1]", r);
    }
}
