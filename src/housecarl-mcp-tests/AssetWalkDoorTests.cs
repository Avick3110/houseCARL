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

    public void Dispose() { Svc.Dispose(); _mo2.Delete(); }
}

/// <summary>The asset walk reads its record bodies through the read door, a depth at a time, and its NPCs' lines in one
/// pass (#1277): each (plugin, group) once per depth, no group of a referencer that is not a line, and the same answer.</summary>
[Trait("tier", "unit")]
public sealed class AssetWalkDoorTests : IClassFixture<AssetWalkDoorWorld>
{
    readonly AssetWalkDoorWorld _w;
    public AssetWalkDoorTests(AssetWalkDoorWorld w) => _w = w;

    static readonly string[] BothPlugins = { AssetWalkDoorWorld.A.FileName.String, AssetWalkDoorWorld.B.FileName.String };

    AssetWalkData Walk(string seed, IReadOnlyList<string>? fromSource = null)
        => _w.Svc.AssetWalk(Array.Empty<string>(), new[] { _w.Keys[seed] }, fromSource ?? BothPlugins, null);

    (AssetWalkData Data, Dictionary<(string Plugin, string Group), int> Reads, long Seeks) Measured(string seed, IReadOnlyList<string>? fromSource = null)
    {
        var walk = Walk(seed, fromSource);   // the reverse index and the voice cache's own reads, paid once
        var before = new Dictionary<(string Plugin, string Group), int>(_w.Svc.Counters.GroupReads);
        var seeks = _w.Svc.Counters.BodySeeks;
        walk = Walk(seed, fromSource);
        var reads = _w.Svc.Counters.GroupReads.Where(kv => kv.Value != before.GetValueOrDefault(kv.Key))
                                              .ToDictionary(kv => kv.Key, kv => kv.Value - before.GetValueOrDefault(kv.Key));
        return (walk, reads, _w.Svc.Counters.BodySeeks - seeks);
    }

    /// <summary>The seed list's five NPCs sit in two plugins' NPC groups and reach a race and two voice types: each
    /// (plugin, group) is read once per depth, the race once more for the faces, before the walk reaches it; the same
    /// with the plugins named by their paths.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EachDepthReadsEachGroupOnce(bool byPath)
    {
        var (_, reads, _) = Measured("HcDoorSeed", byPath ? new[] { _w.PathA, _w.PathB } : null);
        string a = BothPlugins[0], b = BothPlugins[1];
        Assert.Equal(1, reads[(b, "FormLists")]);
        Assert.Equal(1, reads[(a, "Npcs")]);
        Assert.Equal(1, reads[(b, "Npcs")]);
        Assert.Equal(1, reads[(a, "VoiceTypes")]);
        Assert.Equal(1, reads[(b, "VoiceTypes")]);
        Assert.Equal(2, reads[(a, "Races")]);
    }

    /// <summary>Read through 'winner', the seed list enters its own plugin's two NPCs, read in one pass of its NPC group.</summary>
    [Fact]
    public void AWinnerWalkReadsTheGroupOnce()
    {
        var (data, reads, _) = Measured("HcDoorSeed", new[] { SourcePoles.Winner });
        Assert.Contains(data.Nodes, n => n.PulledBy.StartsWith("Npc HcDoorB1 "));
        Assert.Equal(1, reads[(BothPlugins[1], "Npcs")]);
    }

    /// <summary>A list entry a faction owns at a rank: the walk follows the faction and the item, and reads the rank as
    /// a number, as the record sources settle it, so no note names a record the rank seemed to link.</summary>
    [Fact]
    public void AnOwnerRankIsNotWalkedAsALink()
    {
        Assert.Empty(Walk("HcDoorOwned").Notes);
    }

    /// <summary>The NPCs' referencers are a placed reference in a cell, the seed list and four lines in two plugins:
    /// the lines are read in one pass of each plugin's topics, and no cell group is read.</summary>
    [Fact]
    public void TheLinesAreReadOncePerPluginAndNothingElse()
    {
        var (_, reads, _) = Measured("HcDoorSeed");
        Assert.Equal(1, reads[(BothPlugins[0], "DialogTopics")]);
        Assert.Equal(1, reads[(BothPlugins[1], "DialogTopics")]);
        Assert.DoesNotContain(reads.Keys, k => k.Group is "Cells" or "Worldspaces" || k.Group.StartsWith('*'));
    }

    /// <summary>A template member and a speaker the walk reached come from its own read, and the race its NPCs share
    /// is read once: one single-key read in the whole walk.</summary>
    [Fact]
    public void OnlyTheRaceIsReadOnItsOwn()
    {
        var (data, _, seeks) = Measured("HcDoorSeed");
        Assert.Contains(data.Notes, n => n.Contains(FormIdToken.Of(_w.Keys["HcDoorLineA3"])) && n.Contains("has no Voice type set"));
        Assert.DoesNotContain(data.Notes, n => n.Contains("HcDoorB2"));
        Assert.Equal(1, seeks);
    }

    /// <summary>The reverse step voices the lines of every NPC the walk enters, not the seeds alone (DECISIONS
    /// 2026-10-31 (5)): the seed is a list, and each of its NPCs' lines is a voice node pulled by that line.</summary>
    [Fact]
    public void TheLinesOfEveryEnteredNpcAreVoiced()
    {
        var d = Walk("HcDoorSeed");
        foreach (var (line, npc) in new[] { ("HcDoorLineA1", "HcDoorA1"), ("HcDoorLineA2", "HcDoorA2"), ("HcDoorLineB1", "HcDoorB1") })
            Assert.Contains(d.Nodes, n => n.Edge == "voice" && n.PulledBy.Contains(FormIdToken.Of(_w.Keys[line])) && n.PulledBy.EndsWith("a line spoken by Npc " + npc + " [" + FormIdToken.Of(_w.Keys[npc]) + "]"));
    }

    /// <summary>Pins what the door does not change: the nodes in walk order, their edges and who pulled them, and the notes.</summary>
    [Fact]
    public void TheAnswerIsTheOneAQueueGave()
    {
        var d = Walk("HcDoorSeed");
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
            (VoiceOf("HcDoorLineA1", "HcDoorVoiceA", "HcDoorTA"), "voice", $"DialogResponses [{Id("HcDoorLineA1")}], a line spoken by Npc HcDoorA1 [{Id("HcDoorA1")}]"),
            (VoiceOf("HcDoorLineA2", "HcDoorVoiceA", "HcDoorTB"), "voice", $"DialogResponses [{Id("HcDoorLineA2")}], a line spoken by Npc HcDoorA2 [{Id("HcDoorA2")}]"),
            (VoiceOf("HcDoorLineB1", "HcDoorVoiceB", "HcDoorTB"), "voice", $"DialogResponses [{Id("HcDoorLineB1")}], a line spoken by Npc HcDoorB1 [{Id("HcDoorB1")}]"),
        };
        Assert.Equal(string.Join("\n", expected.Select(e => $"{e.Item1.ToLowerInvariant()} | {e.Item2} | {e.Item3}")),
                     string.Join("\n", d.Nodes.Select(n => $"{n.Path.ToLowerInvariant()} | {n.Edge} | {n.PulledBy}")));
        Assert.Equal(new[] { $"DialogResponses [{Id("HcDoorLineA3")}], a line spoken by Npc HcDoorA3 [{Id("HcDoorA3")}]: no voice node — its Speaker NPC {Id("HcDoorA3")} has no Voice type set." },
                     d.Notes);
    }

    /// <summary>Pins the depth bound and its note: a chain of five lists enters four, and names the fifth as past it.</summary>
    [Fact]
    public void TheWalkStillStopsThreeHopsOut()
    {
        var d = Walk("HcDoorChain0");
        Assert.Equal(new[] { $"1 record(s) of a from_source plugin, or of a seed's own plugin when 'winner' read it, sit more than {AssetLayers.RecordDepth} form-link hops from a seed and were not entered; seed them with records= to walk them." },
                     d.Notes);
    }

    /// <summary>Pins the record cap and its note: at three records the seed list and the first two NPCs are entered,
    /// in queue order, and the walk says where it stopped, once: nothing past the cap is entered at a later depth.</summary>
    [Fact]
    public void TheCapStopsAtTheSameRecord()
    {
        var area = _w.Svc.AssetArea;
        var prior = area.MaxWalkRecords;
        area.MaxWalkRecords = 3;
        try
        {
            var d = Walk("HcDoorSeed");
            Assert.True(d.Truncated);
            Assert.Equal(new[] { "HcDoorA1", "HcDoorA2" }, d.Nodes.Where(n => n.Edge == "facegen").Select(n => n.PulledBy.Split(' ')[1]).Distinct());
            Assert.Equal("the walk stopped entering records at 3.", d.Notes[0]);
            Assert.Single(d.Notes, n => n.StartsWith("the walk stopped entering records"));
        }
        finally { area.MaxWalkRecords = prior; }
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
            new SourceArm("first", SourceArmKind.File, "first", First, Batch: _ => { batches++; return First; }),
            new SourceArm("second", SourceArmKind.File, "second", Second, Batch: asked => { secondBatches++; secondAsked = asked.ToList(); return Second; }),
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
/// the NPC is read on its own and its fault is the note it always was, naming the arm, not a miss.</summary>
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
