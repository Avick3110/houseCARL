using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda.Plugins;
using Xunit;

namespace HousecarlMcpTests;

public sealed class UnreadLinksFixture : IDisposable
{
    public UnreadLinksWorld W { get; } = new();
    public void Dispose() => W.Dispose();
}

/// <summary>A walk that reaches a record whose links cannot be read keeps its own posture instead of failing the call
/// (#1316): the one link door gives a deleted record no links; the asset walk notes the record and goes on; copy
/// refuses at it with its pull chain and writes nothing; the records walk follows a perk read leniently and says so.
/// Before, each read Mutagen's links raw and the throw reached the call as an internal failure.</summary>
[Trait("tier", "integration")]
public sealed class UnreadLinksWalkTests : IClassFixture<UnreadLinksFixture>
{
    readonly UnreadLinksWorld _w;
    public UnreadLinksWalkTests(UnreadLinksFixture f) => _w = f.W;

    const string L = UnreadLinksWorld.Lenient, D = UnreadLinksWorld.Dead, C = UnreadLinksWorld.Corrupt;

    // ---- the door ----

    [Fact]
    public void TheDoorGivesADeletedThrowingRecordNoLinksAndNoThrow()
    {
        var (note, links) = _w.ReadPerk(D, p => { var l = new HashSet<FormKey>(); return (RecordLinks.Collect(p, l), l); });
        Assert.Null(note);
        Assert.Empty(links);
    }

    [Fact]
    public void TheDoorReadsALiveRefusedPerkLenientlyWithItsNote()
    {
        var (note, links) = _w.ReadPerk(L, p => { var l = new HashSet<FormKey>(); return (RecordLinks.Collect(p, l), l); });
        Assert.NotNull(note);
        Assert.Contains(_w.Id(L, "Perk"), note);
        Assert.Contains(_w.Keys[L]["Static"], links);
        Assert.Contains(_w.Keys[L]["Spell"], links);
    }

    // ---- the asset walk ----

    AssetWalkData Asset(string plugin) => _w.Svc.AssetWalk(Array.Empty<string>(), new[] { _w.Keys[plugin]["List"] }, null, null);

    static List<string> Paths(AssetWalkData d) => d.Nodes.Select(n => n.Path).ToList();

    [Fact]
    public void TheAssetWalkFollowsALenientPerksRecoveredLinksAndSaysSo()
    {
        var d = Asset(L);
        Assert.Null(d.Refusal);
        Assert.Equal(new[] { UnreadLinksWorld.BeforeNif, UnreadLinksWorld.AfterNif, UnreadLinksWorld.RecoveredNif }, Paths(d));
        Assert.Contains(d.Notes, n => n.Contains(_w.Id(L, "Perk")) && n.Contains("its links were read leniently"));
        Assert.Contains(d.Notes, n => n.Contains(_w.Id(L, "Perk")) && n.Contains("part of the paths it lists could not be read"));
    }

    /// <summary>Mutagen hands over the master's spell link before the refused effect throws, and the lenient re-read
    /// starts over: the boundary counts the one link the perk holds, not that link twice.</summary>
    [Fact]
    public void TheAssetWalkCountsALenientPerksBoundaryOncePerLink()
        => Assert.Contains(Asset(L).Notes, n => n.Contains(UnreadLinksWorld.BaseMaster + " ×1."));

    [Fact]
    public void TheAssetWalkGoesPastADeletedThrowingRecordFollowingNothing()
    {
        var d = Asset(D);
        Assert.Null(d.Refusal);
        Assert.Equal(new[] { UnreadLinksWorld.BeforeNif, UnreadLinksWorld.AfterNif }, Paths(d));
        Assert.DoesNotContain(d.Nodes, n => n.Standing == WalkStanding.Error);
        Assert.DoesNotContain(d.Notes, n => n.Contains("its links"));
    }

    [Fact]
    public void TheAssetWalkNotesARecordWhoseLinksCannotBeReadAndGoesOn()
    {
        var d = Asset(C);
        Assert.Null(d.Refusal);
        Assert.Equal(new[] { UnreadLinksWorld.BeforeNif, UnreadLinksWorld.AfterNif }, Paths(d));
        Assert.Contains(d.Notes, n => n.Contains(_w.Id(C, "Perk")) && n.Contains("its links could not be read"));
        Assert.Contains(d.Notes, n => n.Contains(_w.Id(C, "Perk")) && n.Contains("part of the paths it lists could not be read"));
    }

    // ---- copy ----

    string Copy(string plugin, string stem, string[]? exclude = null)
        => CopyTools.Copy(_w.Svc, _w.Id(plugin, "List"), new[] { plugin }, new[] { "Items" }, exclude, null, stem, stem);

    /// <summary>The refusal says only what the walk decided, the same for a lenient read and a fault: no lenient note,
    /// no fault text, the perk's FormID once (in the chain), and the step that gets past it.</summary>
    void SaysOnlyTheRefusalAndItsStep(string plugin, string text)
    {
        Assert.Contains("its links could not be read whole, so it cannot be copied. To copy past it, prune it with exclude_types=['Perk:stop'].", text);
        Assert.DoesNotContain("effect(s)", text);
        Assert.DoesNotContain("Exception", text);
        var id = _w.Id(plugin, "Perk");
        Assert.Equal(text.IndexOf(id, StringComparison.Ordinal), text.LastIndexOf(id, StringComparison.Ordinal));
    }

    void NothingWritten(string stem)
        => Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(_w.ModsDir, "*" + stem + "*", SearchOption.AllDirectories), _ => true);

    /// <summary>Q1 (A): copy refuses at the walk, naming the perk and the chain that pulled it in, rather than walking
    /// on and failing at the duplicate.</summary>
    [Fact]
    public void CopyRefusesAtALenientPerkWithItsChainAndWritesNothing()
    {
        var text = Copy(L, "HcIsoCopyLenient");
        Assert.StartsWith("error: ", text);
        Assert.Contains("Perk HcIsoLenientPerk", text);
        Assert.Contains("its links could not be read whole, so it cannot be copied", text);
        Assert.Contains($"Chain: {_w.Keys[L]["Perk"]}.", text);
        Assert.Contains("Nothing was written.", text);
        SaysOnlyTheRefusalAndItsStep(L, text);
        NothingWritten("HcIsoCopyLenient");
    }

    [Fact]
    public void CopyRefusesAtARecordWhoseLinksCannotBeReadAndWritesNothing()
    {
        var text = Copy(C, "HcIsoCopyCorrupt");
        Assert.StartsWith("error: ", text);
        Assert.Contains("Perk HcIsoCorruptPerk", text);
        SaysOnlyTheRefusalAndItsStep(C, text);
        NothingWritten("HcIsoCopyCorrupt");
    }

    /// <summary>The step the refusal names is one the walk honours: the exclusion check runs before the link read, so
    /// the copy gets past the perk.</summary>
    [Theory]
    [InlineData(L, "HcIsoStopLenient")]
    [InlineData(C, "HcIsoStopCorrupt")]
    public void CopyGetsPastThePerkWithTheStepTheRefusalNames(string plugin, string stem)
    {
        var text = Copy(plugin, stem, new[] { "Perk:stop" });
        Assert.False(text.StartsWith("error:", StringComparison.Ordinal), text);
        Assert.DoesNotContain("could not be read whole", text);
    }

    /// <summary>A deleted record links to nothing, so the walk enters it; its body cannot be duplicated, and copy
    /// refuses there, naming it.</summary>
    [Fact]
    public void CopyReachingADeletedThrowingRecordRefusesNamingItAndWritesNothing()
    {
        var text = Copy(D, "HcIsoCopyDead");
        Assert.StartsWith("error: ", text);
        Assert.Contains(_w.Id(D, "Perk"), text);
        Assert.DoesNotContain("failed unexpectedly", text);
        NothingWritten("HcIsoCopyDead");
    }

    // ---- the records walk ----

    string Walk(string seed) => RecordsTools.Records(
        _w.Svc, formids: new[] { seed },
        walk: new RecordsTools.RecordsWalk { depth = 3 },
        project: new RecordsTools.RecordsProject { form = "chain" });

    /// <summary>Q2 (A): the records walk reads through the door, so the perk is walked on its recovered links with the
    /// note, the answer the references search gives, not called unscannable.</summary>
    [Fact]
    public void TheRecordsWalkFollowsALenientPerkWithTheNote()
    {
        var r = Walk(_w.Id(L, "List"));
        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), r);
        var perkRow = r.Split('\n').Single(l => l.TrimStart().StartsWith("d1  " + _w.Id(L, "Perk"), StringComparison.Ordinal));
        Assert.Contains("[expanded]", perkRow);
        Assert.Contains("links read leniently", perkRow);
        Assert.Contains("HcIsoLenientStatic", r);
        Assert.DoesNotContain("could not be scanned", r);
    }

    [Fact]
    public void TheRecordsWalkSaysASeedsLinksWereReadLeniently()
    {
        var r = Walk(_w.Id(L, "Perk"));
        Assert.Contains("note: the seed's links read leniently", r);
        Assert.Contains("HcIsoLenientStatic", r);
    }

    [Fact]
    public void TheRecordsWalksJsonRowCarriesTheSeedsNoteOnlyWhenThereIsOne()
    {
        string? Note(string seed)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(RecordsTools.Records(
                _w.Svc, formids: new[] { seed }, walk: new RecordsTools.RecordsWalk { depth = 1 },
                project: new RecordsTools.RecordsProject { form = "chain" }, format: "json"));
            return doc.RootElement.GetProperty("rows")[0].TryGetProperty("note", out var n) ? n.GetString() : null;
        }
        Assert.StartsWith("links read leniently", Note(_w.Id(L, "Perk")));
        Assert.Null(Note(_w.Id(L, "List")));
    }

    [Fact]
    public void TheRecordsWalkFromADeletedThrowingRecordAnswers()
    {
        var r = Walk(_w.Id(D, "Perk"));
        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), r);
        Assert.DoesNotContain("failed unexpectedly", r);
        Assert.Contains("no links to follow from this seed", r);
    }
}
