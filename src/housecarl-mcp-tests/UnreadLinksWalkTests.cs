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

    /// <summary>The walk from the plugin's list under walk.follow <paramref name="follow"/>: "*" every link, null the
    /// default part fields, else that one field.</summary>
    AssetWalkData Asset(string plugin, string? follow = "*")
        => _w.Svc.AssetWalk(Array.Empty<string>(), new[] { _w.Keys[plugin]["List"] }, null, null,
                            values: follow is null ? null : WalkGrammar.Values.Defaults(WalkGrammar.AssetDefaultDepth) with { Follow = new[] { follow } });

    static List<string> Paths(AssetWalkData d) => d.Nodes.Select(n => n.Path).ToList();

    /// <summary>Under "*" the perk's recovered static is followed; the default and a named walk.follow follow no perk
    /// field, so they list the two head parts and say the lenient read off the count's read.</summary>
    [Theory]
    [InlineData("*")]
    [InlineData(null)]
    [InlineData("Items")]
    public void TheAssetWalkFollowsALenientPerksRecoveredLinksAndSaysSo(string? follow)
    {
        var d = Asset(L, follow);
        Assert.Null(d.Refusal);
        Assert.Equal(follow == "*" ? new[] { UnreadLinksWorld.BeforeNif, UnreadLinksWorld.AfterNif, UnreadLinksWorld.RecoveredNif }
                               : new[] { UnreadLinksWorld.BeforeNif, UnreadLinksWorld.AfterNif }, Paths(d));
        Assert.Contains(d.Notes, n => n.Contains(_w.Id(L, "Perk")) && n.Contains("its links were read leniently"));
        Assert.Contains(d.Notes, n => n.Contains(_w.Id(L, "Perk")) && n.Contains("part of the paths it lists could not be read"));
    }

    /// <summary>A sentence that names the record before the door's note names its FormID once, not again in the note.</summary>
    static void NamesOnce(string id, string text)
    {
        Assert.Contains(id, text);
        Assert.Equal(text.IndexOf(id, StringComparison.Ordinal), text.LastIndexOf(id, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("*")]
    [InlineData(null)]
    [InlineData("Items")]
    public void TheAssetWalksLenientNoteNamesThePerksFormIdOnce(string? follow)
        => NamesOnce(_w.Id(L, "Perk"), Asset(L, follow).Notes.Single(n => n.Contains("its links were read leniently")));

    /// <summary>Mutagen hands over the master's spell link before the refused effect throws, and the lenient re-read
    /// starts over: the default walk, which follows no perk field, counts the perk's two links (the static and the
    /// spell) once each on the links-not-followed note, not the spell twice.</summary>
    [Fact]
    public void TheAssetWalkCountsALenientPerksLinksOncePerLink()
        => Assert.Contains(_w.Svc.AssetWalk(Array.Empty<string>(), new[] { _w.Keys[L]["List"] }, null, null).Notes,
                           n => n.Contains("sit outside the part fields") && n.Contains(" 2 on Perk"));

    /// <summary>On every follow rule the deleted perk links to nothing; the default and named walks pin unchanged behaviour.</summary>
    [Theory]
    [InlineData("*")]
    [InlineData(null)]
    [InlineData("Items")]
    public void TheAssetWalkGoesPastADeletedThrowingRecordFollowingNothing(string? follow)
    {
        var d = Asset(D, follow);
        Assert.Null(d.Refusal);
        Assert.Equal(new[] { UnreadLinksWorld.BeforeNif, UnreadLinksWorld.AfterNif }, Paths(d));
        Assert.DoesNotContain(d.Nodes, n => n.Standing == WalkStanding.Error);
        Assert.DoesNotContain(d.Notes, n => n.Contains("its links"));
    }

    /// <summary>Under "*" the perk is not followed past; the default and a named walk.follow follow none of its fields
    /// and leave its links uncounted. Each answers, with the note on the perk.</summary>
    [Theory]
    [InlineData("*")]
    [InlineData(null)]
    [InlineData("Items")]
    public void TheAssetWalkNotesARecordWhoseLinksCannotBeReadAndGoesOn(string? follow)
    {
        var d = Asset(C, follow);
        Assert.Null(d.Refusal);
        Assert.Equal(new[] { UnreadLinksWorld.BeforeNif, UnreadLinksWorld.AfterNif }, Paths(d));
        Assert.Contains(d.Notes, n => n.Contains(_w.Id(C, "Perk")) && n.Contains("its links could not be read"));
        Assert.Contains(d.Notes, n => n.Contains(_w.Id(C, "Perk")) && n.Contains("part of the paths it lists could not be read"));
        // Mutagen hands over the master's spell before the corrupt effect throws; a link read that fails follows none.
        Assert.DoesNotContain(d.Notes, n => n.Contains(UnreadLinksWorld.BaseMaster));
    }

    /// <summary>A named walk.follow still reads the field it names on a perk whose whole link read faults, so it enters
    /// the static that field holds, and the note says it followed only the named fields. Pins as-built behaviour
    /// (DECISIONS 2026-11-08 (23), call A2).</summary>
    [Fact]
    public void ANamedFollowReadsItsFieldPastARecordWhoseLinksCannotBeRead()
    {
        var d = _w.Svc.AssetWalk(Array.Empty<string>(), new[] { _w.Keys[C]["List"] }, null, null,
                                 values: WalkGrammar.Values.Defaults(WalkGrammar.AssetDefaultDepth) with { Follow = new[] { "Items", "NextPerk" } });
        Assert.Null(d.Refusal);
        Assert.Equal(new[] { UnreadLinksWorld.BeforeNif, UnreadLinksWorld.AfterNif, UnreadLinksWorld.RecoveredNif }, Paths(d));
        Assert.Contains(d.Notes, n => n.Contains(_w.Id(C, "Perk")) && n.Contains("its links could not be read whole, so the walk follows only its fields walk.follow names"));
    }

    // ---- copy ----

    string Copy(string plugin, string stem, string[]? exclude = null)
        => CopyTools.Copy(_w.Svc, _w.Id(plugin, "List"), new[] { plugin }, new[] { "Items" }, Walks.Excl(exclude), null, stem, stem);

    /// <summary>The refusal says only what the walk decided, the same for a lenient read and a fault: no lenient note,
    /// no fault text, the perk's FormID once (in the chain), and the step that gets past it.</summary>
    void SaysOnlyTheRefusalAndItsStep(string plugin, string text)
    {
        Assert.Contains("its links could not be read whole, so it cannot be copied. To copy past it, prune its type with exclusions=[{\"match\": \"Perk\", \"severity\": \"stop\"}], which prunes every record of that type the walk reaches.", text);
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
        // The pruned perk is the excluded boundary, so the standalone clone strips the list's link to it.
        Assert.Contains($"- Items[1]  (was {_w.Id(plugin, "Perk")})", text);
    }

    /// <summary>Onto a target the pruned perk is kept as a link into the source, which copy refuses naming the prune as
    /// the cause (unchanged behaviour, pinned: the cause comes from the excluded boundary the walk keeps).</summary>
    [Theory]
    [InlineData(L, "HcIsoOntoLenient")]
    [InlineData(C, "HcIsoOntoCorrupt")]
    public void CopyOntoATargetPastThePerkNamesThePruneAsTheCause(string plugin, string stem)
    {
        var text = CopyTools.Copy(_w.Svc, _w.Id(plugin, "List"), new[] { plugin }, new[] { "Items" }, Walks.Excl("Perk:stop"),
                                  FormIdToken.Of(_w.Target), null, stem);
        Assert.StartsWith("error: ", text);
        Assert.Contains($"the record {_w.Id(plugin, "Perk")} was pruned by exclusions", text);
        NothingWritten(stem);
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
        // The walk read no links off it, so it is the duplicate that refuses, not the link read.
        Assert.Contains("could not duplicate", text);
        Assert.DoesNotContain("could not be read whole", text);
        NothingWritten("HcIsoCopyDead");
    }

    /// <summary>The refusal's chain names every record from the seed down, not only the last.</summary>
    [Fact]
    public void CopysUnreadLinksRefusalRendersTheWholeChain()
    {
        var src = ModKey.FromFileName("ChainSrc.esp");
        FormKey list = new(src, 0x800), perk = new(src, 0x801);
        var text = CopyTools.Render(ClosureCopyOutcome.Fail(
            walk: new WalkRefusal(WalkRefusalKind.UnreadLinks, perk, "FormList 000800:ChainSrc.esp", new[] { list, perk },
                                  "effect(s) 0 were refused", TypeName: "Perk", EditorId: "ChainPerk"),
            sources: new[] { new SourceArmRef("ChainSrc.esp", SourceArmKind.File, null) }));
        Assert.Contains($"Chain: {list} -> {perk}.", text);
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
        NamesOnce(_w.Id(L, "Perk"), perkRow);
        Assert.Contains("HcIsoLenientStatic", r);
        Assert.DoesNotContain("could not be scanned", r);
    }

    [Fact]
    public void TheRecordsWalkSaysASeedsLinksWereReadLeniently()
    {
        var r = Walk(_w.Id(L, "Perk"));
        Assert.Contains("note: the seed's links read leniently", r);
        Assert.DoesNotContain(_w.Id(L, "Perk"), r.Split('\n').Single(l => l.Contains("note: the seed's")));
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
        Assert.DoesNotContain(_w.Id(L, "Perk"), Note(_w.Id(L, "Perk")));
        Assert.Null(Note(_w.Id(L, "List")));
    }

    string WalkWith(string seed, RecordsTools.RecordsWalk walk) => RecordsTools.Records(
        _w.Svc, formids: new[] { seed }, walk: walk, project: new RecordsTools.RecordsProject { form = "chain" });

    /// <summary>A field-path walk reads a deleted record the way the plain walk does: it follows none of its links,
    /// though the deleted list's Items still read.</summary>
    [Fact]
    public void TheRecordsWalksFieldPathsFollowNoneOfADeletedRecordsLinks()
    {
        var gone = _w.Id(D, "GoneList");
        Assert.DoesNotContain("HcIsoDeadStatic", WalkWith(gone, new RecordsTools.RecordsWalk { depth = 2 }));
        var seeded = WalkWith(gone, new RecordsTools.RecordsWalk { depth = 2, seed_paths = new[] { "Items" } });
        Answered(seeded);
        Assert.Contains("no links to follow from this seed", seeded);
        Assert.DoesNotContain("HcIsoDeadStatic", seeded);
        var followed = WalkWith(_w.Id(D, "Holder"), new RecordsTools.RecordsWalk { depth = 3, follow = "Items" });
        Answered(followed);
        Assert.Contains("HcIsoDeadGoneList", followed);
        Assert.DoesNotContain("HcIsoDeadStatic", followed);
    }

    /// <summary>A '*parent.X' path from a live record reads X off its container; a deleted container gives no links.</summary>
    [Fact]
    public void AParentPathFromALiveRecordFollowsNoneOfItsDeletedContainersLinks()
    {
        var r = WalkWith(_w.Id(D, "Info"), new RecordsTools.RecordsWalk { depth = 1, seed_paths = new[] { "*parent.Quest" } });
        Answered(r);
        Assert.Contains("no links to follow from this seed", r);
        Assert.DoesNotContain("HcIsoDeadQuest", r);
    }

    static void Answered(string r)
    {
        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), r);
        Assert.DoesNotContain("failed unexpectedly", r);
    }

    /// <summary>A follow= hop's path note is not the lenient note: a node row on a follow= walk carries none.</summary>
    [Fact]
    public void AFollowWalksNodeRowsCarryNoLenientNote()
    {
        var r = WalkWith(_w.Id(L, "List"), new RecordsTools.RecordsWalk { depth = 3, follow = "Items" });
        var rows = r.Split('\n').Where(l => l.TrimStart().StartsWith("d1  ", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, rows.Count);
        Assert.All(rows, row => Assert.Contains("[expanded]  <- ", row));
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
