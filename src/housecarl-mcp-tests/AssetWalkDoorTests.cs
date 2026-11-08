using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>Two plugins for the asset walk's reads (#1277). A defines a race, a voice type, a quest, three NPCs, a
/// topic of lines, and a cell placing the first NPC. B defines its own voice type, two NPCs (one templated on A's
/// first), a seed list of all five, a topic of lines spoken by A's and B's NPCs, and a chain of five lists.</summary>
public sealed class AssetWalkDoorWorld : IDisposable
{
    readonly ScratchMo2 _mo2 = new("hc-walk-door-");
    public static readonly ModKey A = new("HcDoorA", ModType.Master), B = new("HcDoorB", ModType.Plugin);
    public LoadOrderService Svc { get; }
    public string PathA { get; }
    public string PathB { get; }
    public Dictionary<string, FormKey> Keys { get; } = new(StringComparer.Ordinal);

    public AssetWalkDoorWorld()
    {
        var a = new SkyrimMod(A, SkyrimRelease.SkyrimSE);
        var race = a.Races.AddNew("HcDoorRace");
        race.Flags = Race.Flag.Playable | Race.Flag.FaceGenHead;
        var voiceA = a.VoiceTypes.AddNew("HcDoorVoiceA");
        var quest = a.Quests.AddNew("HcDoorQst");
        Npc NpcIn(SkyrimMod m, string edid, IVoiceTypeGetter? voice)
        {
            var n = m.Npcs.AddNew(edid);
            n.Race.SetTo(race);
            if (voice is not null) n.Voice.SetTo(voice);
            Keys[edid] = n.FormKey;
            return n;
        }
        var a1 = NpcIn(a, "HcDoorA1", voiceA);
        var a2 = NpcIn(a, "HcDoorA2", voiceA);
        var a3 = NpcIn(a, "HcDoorA3", null);
        DialogTopic TopicIn(SkyrimMod m, string edid)
        {
            var t = m.DialogTopics.AddNew(edid);
            t.Quest.SetTo(quest);
            return t;
        }
        void LineIn(SkyrimMod m, DialogTopic t, string key, INpcGetter speaker)
        {
            var info = new DialogResponses(m.GetNextFormKey(), SkyrimRelease.SkyrimSE);
            info.Responses.Add(new DialogResponse { ResponseNumber = 1, Text = key });
            info.Speaker.SetTo(speaker);
            t.Responses.Add(info);
            Keys[key] = info.FormKey;
        }
        var topicA = TopicIn(a, "HcDoorTA");
        LineIn(a, topicA, "HcDoorLineA1", a1);
        LineIn(a, topicA, "HcDoorLineA3", a3);
        var cell = new Cell(a.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcDoorCell", Flags = Cell.Flag.IsInteriorCell };
        var placed = new PlacedNpc(a.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcDoorPlacedA1" };
        placed.Base.SetTo(a1);
        cell.Temporary.Add(placed);
        var block = new CellBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellBlock };
        var sub = new CellSubBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellSubBlock };
        sub.Cells.Add(cell);
        block.SubBlocks.Add(sub);
        a.Cells.Records.Add(block);
        var faction = a.Factions.AddNew("HcDoorFaction");
        var arrow = a.Ammunitions.AddNew("HcDoorArrow");
        PathA = _mo2.InMod("DoorA", A);
        a.BeginWrite.ToPath(PathA).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        var b = new SkyrimMod(B, SkyrimRelease.SkyrimSE);
        var b1 = NpcIn(b, "HcDoorB1", b.VoiceTypes.AddNew("HcDoorVoiceB"));
        var b2 = NpcIn(b, "HcDoorB2", null);
        b2.Template.SetTo(a1);
        b2.Configuration.TemplateFlags = NpcConfiguration.TemplateFlag.Traits;
        var seed = b.FormLists.AddNew("HcDoorSeed");
        foreach (var n in new INpcGetter[] { a1, a2, a3, b1, b2 }) seed.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(n.FormKey));
        Keys["HcDoorSeed"] = seed.FormKey;
        var topicB = TopicIn(b, "HcDoorTB");
        LineIn(b, topicB, "HcDoorLineA2", a2);
        LineIn(b, topicB, "HcDoorLineB1", b1);
        FormList? below = null;
        for (int i = 4; i >= 0; i--)
        {
            var l = b.FormLists.AddNew("HcDoorChain" + i);
            if (below is not null) l.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(below.FormKey));
            Keys[l.EditorID!] = l.FormKey;
            below = l;
        }
        // A list whose entry a faction owns at rank 7: the rank is a number, not a link to walk.
        var owned = b.LeveledItems.AddNew("HcDoorOwned");
        owned.Entries = new()
        {
            new LeveledItemEntry
            {
                Data = new LeveledItemEntryData { Level = 1, Count = 1, Reference = arrow.ToLink<IItemGetter>() },
                ExtraData = new ExtraData { ItemCondition = 1f, Owner = new FactionOwner { Faction = faction.ToLink<IFactionGetter>(), RequiredRank = 7 } },
            },
        };
        Keys["HcDoorOwned"] = owned.FormKey;
        PathB = _mo2.InMod("DoorB", B);
        b.BeginWrite.ToPath(PathB).WithLoadOrder(new ISkyrimModGetter[] { a }).Write();

        var names = new[] { A, B }.Select(k => k.FileName.String).ToList();
        _mo2.Profile(string.Join("\r\n", names) + "\r\n", string.Join("", names.Select(n => "*" + n + "\r\n")), "+DoorB\r\n+DoorA\r\n");
        Svc = _mo2.Open();
    }

    /// <summary>A second service over the same instance, with nothing built yet.</summary>
    public LoadOrderService OpenFresh() => _mo2.Open();

    public void Dispose() { Svc.Dispose(); _mo2.Delete(); }
}

/// <summary>The asset walk reads its record bodies through the read door, a depth at a time (#1277): each (plugin,
/// group) once per depth, no line found off an NPC (#1278), and the same answer.</summary>
[Trait("tier", "unit")]
public sealed class AssetWalkDoorTests : IClassFixture<AssetWalkDoorWorld>
{
    readonly AssetWalkDoorWorld _w;
    public AssetWalkDoorTests(AssetWalkDoorWorld w) => _w = w;

    static readonly string[] BothPlugins = { AssetWalkDoorWorld.A.FileName.String, AssetWalkDoorWorld.B.FileName.String };

    /// <summary>The five NPCs and their four lines, in that order.</summary>
    static readonly string[] NpcsAndLines = { "HcDoorA1", "HcDoorA2", "HcDoorA3", "HcDoorB1", "HcDoorB2", "HcDoorLineA1", "HcDoorLineA3", "HcDoorLineA2", "HcDoorLineB1" };

    /// <summary>The walk's defaults with walk.through naming the seed list's reached types, so its NPCs are entered (#1371).</summary>
    static readonly WalkGrammar.Values EnterNpcs = Walks.EveryLink()
        with { Through = new(StringComparer.OrdinalIgnoreCase) { "Npc", "Race", "VoiceType" } };

    AssetWalkData Walk(string seed, IReadOnlyList<string>? fromSource = null) => Walk(new[] { seed }, fromSource);

    AssetWalkData Walk(IReadOnlyList<string> seeds, IReadOnlyList<string>? fromSource = null, LoadOrderService? svc = null, WalkGrammar.Values? values = null)
        => (svc ?? _w.Svc).AssetWalk(Array.Empty<string>(), seeds.Select(s => _w.Keys[s]).ToList(), fromSource ?? BothPlugins, null, values: values ?? Walks.EveryLink());

    (AssetWalkData Data, Dictionary<(string Plugin, string Group), int> Reads, long Seeks) Measured(string seed, IReadOnlyList<string>? fromSource = null, WalkGrammar.Values? values = null)
        => Measured(new[] { seed }, fromSource, values);

    (AssetWalkData Data, Dictionary<(string Plugin, string Group), int> Reads, long Seeks) Measured(IReadOnlyList<string> seed, IReadOnlyList<string>? fromSource = null, WalkGrammar.Values? values = null)
    {
        var walk = Walk(seed, fromSource, values: values);   // the voice cache's own reads, paid once
        var before = new Dictionary<(string Plugin, string Group), int>(_w.Svc.Counters.GroupReads);
        var seeks = _w.Svc.Counters.BodySeeks;
        walk = Walk(seed, fromSource, values: values);
        var reads = _w.Svc.Counters.GroupReads.Where(kv => kv.Value != before.GetValueOrDefault(kv.Key))
                                              .ToDictionary(kv => kv.Key, kv => kv.Value - before.GetValueOrDefault(kv.Key));
        return (walk, reads, _w.Svc.Counters.BodySeeks - seeks);
    }

    /// <summary>The seed list's five NPCs sit in two plugins' NPC groups and reach a race and two voice types: each
    /// (plugin, group) is read once per depth, the race once, when the walk reaches it, since a reached NPC has no
    /// faces' round (#1278); the same with the plugins named by their paths. walk.through names Npc, so the walk
    /// enters them (#1371).</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EachDepthReadsEachGroupOnce(bool byPath)
    {
        var (_, reads, _) = Measured("HcDoorSeed", byPath ? new[] { _w.PathA, _w.PathB } : null, EnterNpcs);
        string a = BothPlugins[0], b = BothPlugins[1];
        Assert.Equal(1, reads[(b, "FormLists")]);
        Assert.Equal(1, reads[(a, "Npcs")]);
        Assert.Equal(1, reads[(b, "Npcs")]);
        Assert.Equal(1, reads[(a, "VoiceTypes")]);
        Assert.Equal(1, reads[(b, "VoiceTypes")]);
        Assert.Equal(1, reads[(a, "Races")]);
    }

    /// <summary>The default walk reads the seed list's reached NPCs once per plugin's NPC group, at the depth it reaches
    /// them, and keeps them as boundaries, so nothing past them is read (#1371, r1 #14).</summary>
    [Fact]
    public void ADefaultWalkReadsItsBoundaryNpcsOnce()
    {
        var (data, reads, _) = Measured("HcDoorSeed");
        string a = BothPlugins[0], b = BothPlugins[1];
        Assert.Contains(AssetLayers.NpcBoundaryNote(5), data.Notes);
        Assert.Equal(new Dictionary<(string, string), int> { [(b, "FormLists")] = 1, [(a, "Npcs")] = 1, [(b, "Npcs")] = 1 }, reads);
    }

    /// <summary>Read through 'winner', the seed list reaches all five NPCs, its own plugin's and the other's, each NPC
    /// group read in one pass and the NPCs kept as boundaries (#1371).</summary>
    [Fact]
    public void AWinnerWalkReadsTheGroupOnce()
    {
        var (data, reads, _) = Measured("HcDoorSeed", new[] { SourcePoles.Winner });
        Assert.Contains(AssetLayers.NpcBoundaryNote(5), data.Notes);
        Assert.Equal(1, reads[(BothPlugins[0], "Npcs")]);
        Assert.Equal(1, reads[(BothPlugins[1], "Npcs")]);
    }

    /// <summary>A list entry a faction owns at a rank: the walk follows the faction and the item, and reads the rank as
    /// a number, as the record sources settle it, so no note names a record the rank seemed to link.</summary>
    [Fact]
    public void AnOwnerRankIsNotWalkedAsALink()
    {
        Assert.Empty(Walk("HcDoorOwned").Notes);
    }

    /// <summary>No line is found off an NPC (#1278, V4): the seed list's NPCs have four lines in two plugins, and the walk
    /// builds no reverse-reference index, reads no topic and no cell group, and lists no voice node, the NPCs entered as
    /// walk.through names Npc (#1371).</summary>
    [Fact]
    public void NoLineIsFoundOffAnNpc()
    {
        using var svc = _w.OpenFresh();
        var before = new Dictionary<(string Plugin, string Group), int>(svc.Counters.GroupReads);
        var d = Walk(new[] { "HcDoorSeed" }, svc: svc, values: EnterNpcs);
        Assert.Null(svc.CaptureView().ReverseIndex);
        var read = svc.Counters.GroupReads.Where(kv => kv.Value != before.GetValueOrDefault(kv.Key)).Select(kv => kv.Key.Group).ToList();
        Assert.Contains("Npcs", read);
        Assert.DoesNotContain(read, g => g is "DialogTopics" or "Cells" or "Worldspaces" || g.StartsWith('*'));
        Assert.DoesNotContain(d.Nodes, n => n.Edge is "voice" or "lip");
    }

    /// <summary>A template member and a speaker the walk reached resolve, and no body in the whole walk is read by a
    /// single-key seek; which round reads a race is pinned in AssetWalkTemplateDoorTests.</summary>
    [Fact]
    public void NoRecordIsReadOnItsOwn()
    {
        var (data, _, seeks) = Measured(NpcsAndLines);
        Assert.Contains(data.Notes, n => n.Contains(FormIdToken.Of(_w.Keys["HcDoorLineA3"])) && n.Contains("has no Voice type set"));
        Assert.DoesNotContain(data.Notes, n => n.Contains("HcDoorB2"));
        Assert.Equal(0, seeks);
    }

    /// <summary>A reached NPC is a boundary (#1371): the seed is a list, its five NPCs are not entered, so no row, and
    /// one note counts them and says to seed them or name Npc in walk.through.</summary>
    [Fact]
    public void AReachedNpcIsABoundary()
    {
        var d = Walk("HcDoorSeed");
        Assert.Empty(d.Nodes);
        Assert.Contains(AssetLayers.NpcBoundaryNote(5), d.Notes);
    }

    /// <summary>FaceGen is a seed NPC's only (#1278, F2): walk.through naming Npc enters the list's five NPCs with no
    /// FaceGen node and no note counting them (#1371).</summary>
    [Fact]
    public void AnNpcThroughEntersHasNoFaceGen()
    {
        var d = Walk(new[] { "HcDoorSeed" }, values: EnterNpcs);
        Assert.DoesNotContain(d.Nodes, n => n.Edge == "facegen");
        Assert.DoesNotContain(d.Notes, n => n.Contains("NPC(s) the walk reached past a seed"));
    }

    /// <summary>Pins what the door does not change: the nodes in walk order, their edges and who pulled them, and the
    /// notes; the seeds are the five NPCs, whose faces are listed (the templated one's is its root's, already listed),
    /// and their four lines, each voiced as a record the walk entered.</summary>
    [Fact]
    public void TheAnswerIsTheOneAQueueGave()
    {
        var d = Walk(NpcsAndLines);
        string Id(string k) => FormIdToken.Of(_w.Keys[k]);
        string Face(string npc, FaceGenSlot slot) => FaceGenPath.For(_w.Keys[npc], slot);
        string VoiceOf(string line, string voice, string topic) => VoicePath.For(_w.Keys[line], voice, "HcDoorQst", topic, 1, VoiceFile.Fuz);
        var expected = new[]
        {
            (Face("HcDoorA1", FaceGenSlot.Tint), "facegen", $"Npc HcDoorA1 [{Id("HcDoorA1")}]"),
            (Face("HcDoorA1", FaceGenSlot.Mesh), "facegen", $"Npc HcDoorA1 [{Id("HcDoorA1")}]"),
            (Face("HcDoorA2", FaceGenSlot.Tint), "facegen", $"Npc HcDoorA2 [{Id("HcDoorA2")}]"),
            (Face("HcDoorA2", FaceGenSlot.Mesh), "facegen", $"Npc HcDoorA2 [{Id("HcDoorA2")}]"),
            (Face("HcDoorA3", FaceGenSlot.Tint), "facegen", $"Npc HcDoorA3 [{Id("HcDoorA3")}]"),
            (Face("HcDoorA3", FaceGenSlot.Mesh), "facegen", $"Npc HcDoorA3 [{Id("HcDoorA3")}]"),
            (Face("HcDoorB1", FaceGenSlot.Tint), "facegen", $"Npc HcDoorB1 [{Id("HcDoorB1")}]"),
            (Face("HcDoorB1", FaceGenSlot.Mesh), "facegen", $"Npc HcDoorB1 [{Id("HcDoorB1")}]"),
            (VoiceOf("HcDoorLineA1", "HcDoorVoiceA", "HcDoorTA"), "voice", $"DialogResponses [{Id("HcDoorLineA1")}]"),
            (VoiceOf("HcDoorLineA2", "HcDoorVoiceA", "HcDoorTB"), "voice", $"DialogResponses [{Id("HcDoorLineA2")}]"),
            (VoiceOf("HcDoorLineB1", "HcDoorVoiceB", "HcDoorTB"), "voice", $"DialogResponses [{Id("HcDoorLineB1")}]"),
        };
        Assert.Equal(string.Join("\n", expected.Select(e => $"{e.Item1.ToLowerInvariant()} | {e.Item2} | {e.Item3}")),
                     string.Join("\n", d.Nodes.Select(n => $"{n.Path.ToLowerInvariant()} | {n.Edge} | {n.PulledBy}")));
        Assert.Equal(new[] { $"DialogResponses [{Id("HcDoorLineA3")}]: no voice node — its Speaker NPC {Id("HcDoorA3")} has no Voice type set." },
                     d.Notes);
    }

    /// <summary>Pins the depth bound and its note: a chain of five lists enters four, and names the fifth as past it.</summary>
    [Fact]
    public void TheWalkStillStopsThreeHopsOut()
    {
        var d = Walk(new[] { "HcDoorChain0" }, values: Walks.EveryLink(3));
        Assert.Equal(new[] { "1 record(s) sit more than walk.depth=3 hops from a seed and were not entered; raise walk.depth to walk them (an NPC among them is then a boundary unless walk.through names Npc)." },
                     d.Notes);
    }

    /// <summary>A link out of a record at walk.depth to a record never entered closes no loop and is still counted past
    /// depth (#1278, r1 #2); pins behaviour the reviewed head already had.</summary>
    [Fact]
    public void ALinkPastTheDepthCapClosesNoLoop()
    {
        var d = _w.Svc.AssetWalk(Array.Empty<string>(), new[] { _w.Keys["HcDoorChain0"] }, BothPlugins, null,
                                 values: Walks.EveryLink(1));
        Assert.Empty(d.Cycles);
        Assert.Equal(new[] { "1 record(s) sit more than walk.depth=1 hops from a seed and were not entered; raise walk.depth to walk them (an NPC among them is then a boundary unless walk.through names Npc)." },
                     d.Notes);
    }

    /// <summary>Pins the record cap and its note: at two records the first two of five seed NPCs are entered, in queue
    /// order, and the walk says where it stopped, once: nothing past the cap is entered at a later depth.</summary>
    [Fact]
    public void TheCapStopsAtTheSameRecord()
    {
        var d = _w.Svc.AssetWalk(Array.Empty<string>(), NpcsAndLines.Take(5).Select(s => _w.Keys[s]).ToList(), BothPlugins, null,
                                 values: Walks.EveryLink() with { MaxNodes = 2 });
        Assert.True(d.Truncated);
        Assert.Equal(new[] { "HcDoorA1", "HcDoorA2" }, d.Nodes.Where(n => n.Edge == "facegen").Select(n => n.PulledBy.Split(' ')[1]).Distinct());
        Assert.Equal("the walk stopped entering records at walk.max_nodes=2 (every record entered counts, seeds included); raise walk.max_nodes to walk further.", d.Notes[0]);
        Assert.Single(d.Notes, n => n.StartsWith("the walk stopped entering records"));
    }
    /// <summary>The record cap stops part-way through a later depth (#1278, r1 #11): at three records the seed list and
    /// its first two NPCs are entered, walk.through naming Npc (#1371), and the walk says where it stopped; pins
    /// behaviour the reviewed head already had.</summary>
    [Fact]
    public void TheCapStopsPartWayThroughALaterDepth()
    {
        var d = Walk(new[] { "HcDoorSeed" }, values: EnterNpcs with { MaxNodes = 3 });
        Assert.True(d.Truncated);
        Assert.Contains("the walk stopped entering records at walk.max_nodes=3 (every record entered counts, seeds included); raise walk.max_nodes to walk further.", d.Notes);
    }

    /// <summary>A json counts_only walk stopped by the record cap, with no nodes and one note, so max_chars cuts no list
    /// and stops no axis, still says truncated once bounded, and writes it before its overrun notice.</summary>
    [Fact]
    public void ABoundedCountsWalkStoppedByTheRecordCapSaysTruncated()
    {
        string Counts(int max) => AssetTools.AssetStatus(_w.Svc, records: new[] { FormIdToken.Of(_w.Keys["HcDoorChain0"]) }, walk: Walks.Of("{\"follow\": \"*\", \"max_nodes\": 1}"),
                                                         from_source: BothPlugins, counts_only: true, format: "json", max_chars: max);
        var whole = Counts(400_000);
        using var doc = System.Text.Json.JsonDocument.Parse(Counts(whole.Length - 1));
        var r = doc.RootElement;
        Assert.Equal(0, r.GetProperty("nodes").GetInt32());
        Assert.Equal(new[] { "the walk stopped entering records at walk.max_nodes=1 (every record entered counts, seeds included); raise walk.max_nodes to walk further." },
                     r.GetProperty("notes").EnumerateArray().Select(n => n.GetString()));
        foreach (var list in new[] { "bsa_failures", "root_failures", "warnings", "selector_notes", "notes" })
            Assert.Equal(0, r.GetProperty(list + "_omitted").GetInt32());
        foreach (var axis in new[] { "nodes_by_edge", "nodes_by_standing" })
            Assert.Equal(System.Text.Json.JsonValueKind.Null, r.GetProperty(axis).GetProperty("cut_by").ValueKind);
        Assert.True(r.GetProperty("truncated").GetBoolean(), whole);
        // The overrun notice is written last, after the flag, so the length it states counts the flag.
        Assert.Equal(new[] { "truncated", "max_chars_overrun" }, r.EnumerateObject().Select(p => p.Name).TakeLast(2));
    }

    /// <summary>Pins the chain's order and its fault: a key the first arm has but cannot read is that arm's fault even
    /// when a later arm holds it, a key only the later arm holds is its hit, and a key neither holds is a miss — the
    /// same answers one key at a time gives, with each batch read once and the later arm's over only the keys the
    /// first left unanswered.</summary>
    [Fact]
    public void ManyKeysAnswerAsOneKeyAtATime()
    {
        var mod = new SkyrimMod(ModKey.FromFileName("HcDoorChainArms.esp"), SkyrimRelease.SkyrimSE);
        var faulted = mod.Keywords.AddNew("HcDoorFaulted");
        var later = mod.Keywords.AddNew("HcDoorLater");
        var none = mod.GetNextFormKey();
        int batches = 0, secondBatches = 0;
        IReadOnlyCollection<FormKey>? secondAsked = null;
        Func<FormKey, IMajorRecordGetter?> First = fk => fk == faulted.FormKey ? throw new IOException("the file is locked") : null;
        Func<FormKey, IMajorRecordGetter?> Second = fk => fk == faulted.FormKey ? faulted : fk == later.FormKey ? later : null;
        var chain = new SourceChain(new[]
        {
            new SourceArm("first", SourceArmKind.File, "first", _ => { batches++; return First; }),
            new SourceArm("second", SourceArmKind.File, "second", asked => { secondBatches++; secondAsked = asked.ToList(); return Second; }),
        });
        var keys = new[] { faulted.FormKey, later.FormKey, none };
        var many = chain.FetchMany(keys);
        Assert.Equal(1, batches);
        Assert.Equal(1, secondBatches);
        Assert.Equal(new[] { later.FormKey, none }, secondAsked);
        foreach (var k in keys)
        {
            var one = chain.Fetch(k);
            Assert.Equal(one.Hit?.ArmIndex, many[k].Hit?.ArmIndex);
            Assert.Same(one.Hit?.Body, many[k].Hit?.Body);
            Assert.Equal(one.Fault?.Arm.Spelling, many[k].Fault?.Arm.Spelling);
            Assert.Equal(one.Fault?.Cause, many[k].Fault?.Cause);
            Assert.Equal(one.IsMiss, many[k].IsMiss);
        }
        Assert.Equal("first", many[faulted.FormKey].Fault!.Arm.Spelling);
        Assert.Equal(1, many[later.FormKey].Hit!.ArmIndex);
        Assert.True(many[none].IsMiss);
    }
}

/// <summary>A walk into a plugin whose NPC group broke after the index build: the door's walk of that group faults, so
/// the NPC answers the group's fault with no read of its own, the note it always was, naming the arm, not a miss.</summary>
[Trait("tier", "integration")]
public sealed class AssetWalkDoorFaultTests : IDisposable
{
    readonly ScratchMo2 _mo2 = new("hc-walk-door-fault-");
    readonly ModKey _key = new("HcWalkDoorFault", ModType.Master);
    readonly FormKey _seed, _npc;
    readonly string _path;

    public AssetWalkDoorFaultTests()
    {
        var m = new SkyrimMod(_key, SkyrimRelease.SkyrimSE);
        var npc = m.Npcs.AddNew("HcWdfNpc");
        var seed = m.FormLists.AddNew("HcWdfSeed");
        seed.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(npc.FormKey));
        (_seed, _npc) = (seed.FormKey, npc.FormKey);
        _path = _mo2.InMod("WalkDoorFault", _key);
        m.BeginWrite.ToPath(_path).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        _mo2.Profile(_key.FileName + "\r\n", "*" + _key.FileName + "\r\n", "+WalkDoorFault\r\n");
    }

    public void Dispose() => _mo2.Delete();

    /// <summary>0xFF over the first record header inside the NPC_ group, with the file's size and write time kept.</summary>
    void BreakNpcGroup()
    {
        var mtime = File.GetLastWriteTimeUtc(_path);
        var bytes = File.ReadAllBytes(_path);
        int at = -1;
        for (int i = 0; i + 12 <= bytes.Length && at < 0; i++)
            if (bytes.AsSpan(i, 4).SequenceEqual("GRUP"u8) && bytes.AsSpan(i + 8, 4).SequenceEqual("NPC_"u8)) at = i;
        Assert.True(at >= 0, "the plugin has no NPC_ group");
        bytes.AsSpan(at + 24, 24).Fill(0xFF);
        File.WriteAllBytes(_path, bytes);
        File.SetLastWriteTimeUtc(_path, mtime);
    }

    [Fact]
    public void ABrokenGroupIsTheArmsFaultNotAMiss()
    {
        using var svc = _mo2.Open();
        Assert.DoesNotContain(svc.AssetWalk(Array.Empty<string>(), new[] { _seed }, null, null).Notes, n => n.Contains("could not read it"));
        BreakNpcGroup();
        var notes = svc.AssetWalk(Array.Empty<string>(), new[] { _seed }, null, null).Notes;
        Assert.Contains(notes, n => n.StartsWith($"record {FormIdToken.Of(_npc)} (pulled by FormList HcWdfSeed [{FormIdToken.Of(_seed)}]): 'winner' has it but could not read it — "));
        Assert.DoesNotContain(notes, n => n.Contains("in none of the sources consulted"));
    }
}
