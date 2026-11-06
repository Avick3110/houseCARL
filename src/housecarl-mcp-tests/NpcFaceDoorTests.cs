using System.Text.Json;
using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Assets;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>One NPC of each face shape (#1312), every own pair baked on disk so a lane that guessed from the FormKey
/// would find a file: a plain NPC, a Traits-templated one, one on a leveled template of two, one with ACBS 0x100, one
/// with the Traits flag and no Template, one on a race with no FaceGenHead flag, a keyword, and a key nothing defines.</summary>
public sealed class NpcFaceDoorWorld : IDisposable
{
    internal readonly FaceGenPoleWorld W = new("HcFaceDoor");
    public const string Mod = "DoorMod";
    public LoadOrderService Svc { get; }
    public Npc Plain, Root, Root2, Templated, Leveled, UseTemplate, TraitsAlone, NoHead;
    public Race NoHeadRace;
    public FormKey NotNpc, Unset;

    public NpcFaceDoorWorld()
    {
        var mod = new SkyrimMod(ModKey.FromFileName("HcFaceDoor.esp"), SkyrimRelease.SkyrimSE);
        NoHeadRace = mod.Races.AddNew("HcNoHeadRace");
        NoHeadRace.Flags = Race.Flag.Playable;
        Npc N(string edid, IRaceGetter race) { var n = mod.Npcs.AddNew(edid); n.Race.SetTo(race); return n; }
        Plain = N("HcPlain", W.Race);
        Root = N("HcRoot", W.Race);
        Root2 = N("HcRoot2", W.Race);
        Templated = N("HcTemplated", W.Race);
        Templated.Template.SetTo(Root);
        Templated.Configuration.TemplateFlags = NpcConfiguration.TemplateFlag.Traits;
        var list = mod.LeveledNpcs.AddNew("HcFaceList");
        list.Entries = new();
        foreach (var m in new[] { Root, Root2 })
            list.Entries.Add(new LeveledNpcEntry { Data = new LeveledNpcEntryData { Level = 1, Count = 1, Reference = m.ToLink<INpcSpawnGetter>() } });
        Leveled = N("HcLeveled", W.Race);
        Leveled.Template.SetTo(list);
        Leveled.Configuration.TemplateFlags = NpcConfiguration.TemplateFlag.Traits;
        UseTemplate = N("HcUseTemplate", W.Race);
        UseTemplate.Configuration.Flags |= NpcConfiguration.Flag.UseTemplate;
        TraitsAlone = N("HcTraitsAlone", W.Race);
        TraitsAlone.Configuration.TemplateFlags = NpcConfiguration.TemplateFlag.Traits;
        NoHead = N("HcNoHead", NoHeadRace);
        var mixed = mod.LeveledNpcs.AddNew("HcMixedList");
        mixed.Entries = new();
        foreach (var m in new[] { Root, UseTemplate })
            mixed.Entries.Add(new LeveledNpcEntry { Data = new LeveledNpcEntryData { Level = 1, Count = 1, Reference = m.ToLink<INpcSpawnGetter>() } });
        LeveledMixed = N("HcLeveledMixed", W.Race);
        LeveledMixed.Template.SetTo(mixed);
        LeveledMixed.Configuration.TemplateFlags = NpcConfiguration.TemplateFlag.Traits;
        BothGaps = N("HcBothGaps", W.Race);
        BothGaps.Configuration.TemplateFlags = NpcConfiguration.TemplateFlag.Traits;
        BothGaps.Configuration.Flags |= NpcConfiguration.Flag.UseTemplate;
        NotNpc = mod.Keywords.AddNew("HcNotNpc").FormKey;
        Unset = new FormKey(mod.ModKey, 0xABCDE);
        W.Write(mod, Mod, true, W.RaceMod);
        foreach (var n in Npcs) W.Bake(Mod, n.FormKey);
        var donor = new SkyrimMod(ModKey.FromFileName("HcFaceDonor.esp"), SkyrimRelease.SkyrimSE);
        Donor = donor.Npcs.AddNew("HcDonorOnly");
        Donor.Race.SetTo(W.Race);
        W.Write(donor, DonorMod, false, W.RaceMod);
        W.Bake(DonorMod, Donor.FormKey);
        Svc = W.Open();
    }

    /// <summary>An NPC only an inactive plugin defines, its pair baked beside it, for the lanes that read off the order.</summary>
    public Npc Donor;

    /// <summary>A leveled template of a root with a pair and one with ACBS 0x100; an NPC with both the Traits flag
    /// and no Template and ACBS 0x100. Neither is in <see cref="Npcs"/>, so the check's counts there stay one each.</summary>
    public Npc LeveledMixed, BothGaps;
    public const string DonorMod = "DoorDonor";

    public IEnumerable<Npc> Npcs => new[] { Plain, Root, Root2, Templated, Leveled, UseTemplate, TraitsAlone, NoHead };

    public static string Id(FormKey fk) => FormIdToken.Of(fk);

    public void Dispose() => W.Dispose();
}

/// <summary>Every lane gives the same one FaceGen answer for an NPC (docs/facegen.md, One answer per NPC): formids=,
/// npc=, place formid=, the facegen check and the walk, each case one fixture through every lane.</summary>
public sealed class NpcFaceDoorTests : IClassFixture<NpcFaceDoorWorld>
{
    readonly NpcFaceDoorWorld _w;
    public NpcFaceDoorTests(NpcFaceDoorWorld w) => _w = w;

    static string Mesh(FormKey fk) => FaceGenPath.For(fk, FaceGenSlot.Mesh);
    static string Id(FormKey fk) => NpcFaceDoorWorld.Id(fk);

    List<JsonElement> FormidsRows(FormKey npc)
        => JsonDocument.Parse(AssetTools.AssetStatus(_w.Svc, formids: new[] { Id(npc) }, format: "json"))
                       .RootElement.GetProperty("results").EnumerateArray().ToList();

    string Npc(FormKey npc) => NifTools.NifInspect(_w.Svc, null, npc: new[] { Id(npc) });

    string Check(FormKey npc) => CheckTools.CheckTool(_w.Svc, formids: new[] { Id(npc) }, findings: new[] { "facegen" });

    AssetWalkData Walk(FormKey npc) => _w.Svc.AssetWalk(Array.Empty<string>(), new[] { npc }, null, null);

    string Place(FormKey npc, string patch) => PlaceTools.Place(_w.Svc, new[] { new PlaceTarget { Formid = Id(npc), SourceProvider = NpcFaceDoorWorld.Mod } }, patch: patch);

    /// <summary>A plain NPC: its own pair on every lane, and the check judges it.</summary>
    [Fact]
    public void APlainNpcIsItsOwnPairOnEveryLane()
    {
        var npc = _w.Plain.FormKey;
        var rows = FormidsRows(npc);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.False(r.TryGetProperty("face_note", out _)));
        Assert.Contains(rows, r => string.Equals(r.GetProperty("path").GetString(), Mesh(npc), StringComparison.OrdinalIgnoreCase));
        Assert.Contains(Mesh(npc), Npc(npc), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("0 excluded", Check(npc));
        Assert.Contains(Walk(npc).Nodes, n => n.Edge == "facegen" && string.Equals(n.Path, Mesh(npc), StringComparison.OrdinalIgnoreCase) && n.Note is null);
    }

    /// <summary>The pair is Mutagen's resolved link (DECISIONS 2026-11-05 (19)), so its path is spelled as Mutagen
    /// spells it, 'Meshes\' and 'Textures\', on formids= and npc= as on the walk.</summary>
    [Fact]
    public void ThePairIsSpelledAsMutagensLink()
    {
        var npc = _w.Plain.FormKey;
        var rows = FormidsRows(npc);
        Assert.Contains(rows, r => r.GetProperty("path").GetString()!.StartsWith(@"Meshes\actors\", StringComparison.Ordinal));
        Assert.Contains(rows, r => r.GetProperty("path").GetString()!.StartsWith(@"Textures\actors\", StringComparison.Ordinal));
        Assert.Contains(@"Meshes\actors\character\facegendata\facegeom\HcFaceDoor.esp\", Npc(npc), StringComparison.Ordinal);
    }

    /// <summary>A Traits-templated NPC is its root's pair with the root named, on every lane; its own baked pair is
    /// named nowhere; the check leaves it out as templated; place puts the root's pair and says so.</summary>
    [Fact]
    public void ATemplatedNpcIsItsRootsPairOnEveryLane()
    {
        var npc = _w.Templated.FormKey;
        var root = _w.Root.FormKey;
        var line = $"the face of template root {Id(root)}";
        var rows = FormidsRows(npc);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(line, r.GetProperty("face_note").GetString()));
        Assert.Contains(rows, r => string.Equals(r.GetProperty("path").GetString(), Mesh(root), StringComparison.OrdinalIgnoreCase));
        Assert.Contains("face: " + line, AssetTools.AssetStatus(_w.Svc, formids: new[] { Id(npc) }));

        var nif = Npc(npc);
        Assert.Contains(Mesh(root), nif, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("face: " + line, nif);
        Assert.DoesNotContain(Mesh(npc), nif, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("1 excluded (1 Template+Traits", Check(npc));

        var walk = Walk(npc).Nodes.Where(n => n.Edge == "facegen" && n.PulledBy.Contains("HcTemplated")).ToList();
        Assert.Equal(2, walk.Count);
        Assert.All(walk, n => Assert.Equal(line, n.Note));

        var placed = Place(npc, "HcDoorPlaceT");
        Assert.Contains("placed 2 of 2", placed);
        Assert.Contains(Mesh(root), placed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("face: " + line, placed);
        Assert.DoesNotContain(Mesh(npc), placed, StringComparison.OrdinalIgnoreCase);
    }

    static string Tint(FormKey fk) => FaceGenPath.For(fk, FaceGenSlot.Tint);

    /// <summary>A templated NPC's formids= rows are both halves of its root's pair, and the walk's and place's paths
    /// are spelled as Mutagen's link, compared ordinally.</summary>
    [Fact]
    public void ATemplatedNpcsPathsAreItsRootsPairAsMutagenSpellsIt()
    {
        var npc = _w.Templated.FormKey;
        var root = _w.Root.FormKey;
        var paths = FormidsRows(npc).Select(r => r.GetProperty("path").GetString()!).ToList();
        Assert.Equal(new[] { Mesh(root), Tint(root) }.Order(StringComparer.OrdinalIgnoreCase),
                     paths.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

        var spelled = $@"Meshes\actors\character\facegendata\facegeom\HcFaceDoor.esp\00{root.ID:X6}.nif";
        Assert.Contains(Walk(npc).Nodes, n => n.Edge == "facegen" && n.Path == spelled);
        Assert.Contains(spelled, Place(npc, "HcDoorPlaceSpelled"), StringComparison.Ordinal);
    }

    /// <summary>place formid= with kind= puts that one half of the root's pair for a templated NPC, never its own path.</summary>
    [Fact]
    public void PlaceWithKindPutsTheRootsHalf()
    {
        var npc = _w.Templated.FormKey;
        var placed = PlaceTools.Place(_w.Svc, new[] { new PlaceTarget { Formid = Id(npc), Kind = "mesh", SourceProvider = NpcFaceDoorWorld.Mod } }, patch: "HcDoorPlaceKind");
        Assert.Contains("placed 1 of 1", placed);
        Assert.Contains(Mesh(_w.Root.FormKey), placed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Mesh(npc), placed, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>walk=true formids= through the tool: a templated seed's nodes carry its root's line, and a seed with no
    /// pair is a note with the reason.</summary>
    [Fact]
    public void AFormidsWalkSeedCarriesTheFaceLineOrANote()
    {
        var line = $"the face of template root {Id(_w.Root.FormKey)}";
        var text = AssetTools.AssetStatus(_w.Svc, formids: new[] { Id(_w.Templated.FormKey), Id(_w.UseTemplate.FormKey) }, walk: true);
        Assert.Contains(line, text);
        Assert.Contains($"formids '{Id(_w.UseTemplate.FormKey)}': no FaceGen pair — {NpcFace.UseTemplateFlag}", text);
    }

    /// <summary>A leveled template with one pairless member: its "template root X: reason" row beside the other
    /// member's pair, which is one of 2, the pairless member counted.</summary>
    [Fact]
    public void ALeveledTemplateWithAPairlessMemberGivesItsReasonBesideThePair()
    {
        var rows = FormidsRows(_w.LeveledMixed.FormKey);
        Assert.Equal(3, rows.Count);
        Assert.Contains(rows, r => r.TryGetProperty("error", out var e) && e.GetString() == NpcFaceAnswer.NoPair($"template root {Id(_w.UseTemplate.FormKey)}: {NpcFace.UseTemplateFlag}"));
        Assert.Equal(2, rows.Count(r => r.TryGetProperty("face_note", out var n) && n.GetString() == $"the face of template root {Id(_w.Root.FormKey)}, one of 2"));
    }

    /// <summary>An NPC with the Traits flag and no Template and ACBS 0x100 gets the Traits reason, first in houseCARL's order.</summary>
    [Fact]
    public void TwoGapsGiveTheTraitsReasonFirst()
    {
        Assert.Contains(NpcFace.TraitsNoTemplate, Assert.Single(FormidsRows(_w.BothGaps.FormKey)).GetProperty("error").GetString());
        Assert.Contains("1 with the Traits flag and no Template", Check(_w.BothGaps.FormKey));
    }

    /// <summary>A leveled template names each member's pair, one of N, on formids= (text, json and the artifact), npc=
    /// and the walk; place refuses, naming the roots.</summary>
    [Fact]
    public void ALeveledTemplateNamesEveryMemberOneOfN()
    {
        var npc = _w.Leveled.FormKey;
        var rows = FormidsRows(npc);
        Assert.Equal(4, rows.Count);
        foreach (var root in new[] { _w.Root.FormKey, _w.Root2.FormKey })
        {
            var line = $"the face of template root {Id(root)}, one of 2";
            Assert.Equal(2, rows.Count(r => r.GetProperty("face_note").GetString() == line));
            Assert.Contains("face: " + line, Npc(npc));
            Assert.Contains(Walk(npc).Nodes, n => n.Edge == "facegen" && n.Note == line);
        }

        var file = Path.Combine(_w.W.Root, "leveled-" + Guid.NewGuid().ToString("N") + ".jsonl");
        AssetTools.AssetStatus(_w.Svc, formids: new[] { Id(npc) }, to_file: file);
        var art = File.ReadAllLines(file).Skip(1).Where(l => l.Length > 0).Select(l => JsonDocument.Parse(l).RootElement).ToList();
        Assert.Equal(4, art.Count);
        Assert.All(art, r => Assert.EndsWith("one of 2", r.GetProperty("face_note").GetString()));

        var placed = Place(npc, "HcDoorPlaceL");
        Assert.StartsWith("error:", placed);
        Assert.Contains("2 template roots", placed);
        Assert.Contains(Id(_w.Root.FormKey), placed);
        Assert.Contains(Id(_w.Root2.FormKey), placed);
    }

    /// <summary>Each NPC Mutagen names no pair for gives one reason on every lane, and the check counts it out by that
    /// reason, though its own pair ships.</summary>
    [Theory]
    [InlineData("UseTemplate")]
    [InlineData("TraitsAlone")]
    [InlineData("NoHead")]
    public void AnNpcWithNoPairGivesOneReasonOnEveryLane(string which)
    {
        var (npc, reason, counted) = which switch
        {
            "UseTemplate" => (_w.UseTemplate.FormKey, NpcFace.UseTemplateFlag, "1 with ACBS flag 0x100"),
            "TraitsAlone" => (_w.TraitsAlone.FormKey, NpcFace.TraitsNoTemplate, "1 with the Traits flag and no Template"),
            _ => (_w.NoHead.FormKey, $"race {Id(_w.NoHeadRace.FormKey)} has no FaceGenHead flag", "1 on a race with no FaceGenHead flag"),
        };
        var row = Assert.Single(FormidsRows(npc));
        Assert.Contains(reason, row.GetProperty("error").GetString());

        var nif = Npc(npc);
        Assert.Contains("no FaceGen pair — " + reason, nif);
        Assert.DoesNotContain(Mesh(npc), nif, StringComparison.OrdinalIgnoreCase);

        var check = Check(npc);
        Assert.Contains(counted, check);
        Assert.Contains("0 finding(s)", check);

        var walk = Walk(npc);
        Assert.DoesNotContain(walk.Nodes, n => n.Edge == "facegen");
        Assert.Contains(walk.Notes, n => n.Contains("no FaceGen node") && n.Contains(reason));

        var placed = Place(npc, "HcDoorPlace" + which);
        Assert.StartsWith("error:", placed);
        Assert.Contains("no FaceGen pair — " + reason, placed);
    }

    /// <summary>A key nothing defines, and a key that is not an NPC, are a reason on formids=, npc= and place, not a path.</summary>
    [Fact]
    public void AKeyThatIsNoNpcIsAReasonNotAPath()
    {
        foreach (var (key, reason) in new[] { (_w.Unset, NpcFace.Unset), (_w.NotNpc, "it is a Keyword, not an NPC") })
        {
            Assert.Contains(reason, Assert.Single(FormidsRows(key)).GetProperty("error").GetString());
            Assert.Contains("no FaceGen pair — " + reason, Npc(key));
            Assert.Contains("no FaceGen pair — " + reason, Place(key, "HcDoorPlaceNone"));
        }
        Assert.Contains("place the file by path=", Place(_w.Unset, "HcDoorPlaceNone"));
    }

    /// <summary>An NPC only an inactive plugin defines gives its pair where the lane reads that plugin: the walk's
    /// formids= with from_source= (as its records= seed in the same call) and npc= with source_provider= its folder;
    /// with neither, the active order alone says nothing defines it.</summary>
    [Fact]
    public void ADonorOnlyNpcGivesItsPairThroughTheLanesOwnSources()
    {
        var npc = _w.Donor.FormKey;
        var walk = AssetTools.AssetStatus(_w.Svc, formids: new[] { Id(npc) }, walk: true, from_source: new[] { "HcFaceDonor.esp" });
        Assert.DoesNotContain("no FaceGen pair", walk);
        Assert.Contains(Mesh(npc), walk, StringComparison.OrdinalIgnoreCase);

        var nif = NifTools.NifInspect(_w.Svc, null, npc: new[] { Id(npc) }, source_provider: NpcFaceDoorWorld.DonorMod);
        Assert.DoesNotContain("no FaceGen pair", nif);
        Assert.Contains(Mesh(npc), nif, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("no FaceGen pair — " + NpcFace.Unset, Npc(npc));
        Assert.Contains(NpcFace.Unset, Assert.Single(FormidsRows(npc)).GetProperty("error").GetString());
    }

    /// <summary>Each reason's fixture is one Mutagen names no FaceGen link for, and the plain one is one it does: a
    /// Mutagen bump that moves its gate fails here instead of leaving a reason that is no longer true.</summary>
    [Fact]
    public void MutagenNamesNoFaceGenLinkForEachReasonsFixture()
    {
        static bool Linked(INpcGetter n, IRaceGetter race)
            => n.EnumerateAssetLinks(AssetLinkQuery.Resolved, NpcFace.RaceCache(race))
                .Any(l => l.DataRelativePath.Path.Contains(@"\facegendata\", StringComparison.OrdinalIgnoreCase));
        Assert.True(Linked(_w.Plain, _w.W.Race));
        Assert.False(Linked(_w.UseTemplate, _w.W.Race));
        Assert.False(Linked(_w.TraitsAlone, _w.W.Race));
        Assert.False(Linked(_w.NoHead, _w.NoHeadRace));
    }

    /// <summary>No reason calls Mutagen's gate a bug (DECISIONS 2026-11-05 (22)): each says what Mutagen does.</summary>
    [Fact]
    public void NoReasonCallsMutagensGateABug()
    {
        var reasons = new[] { _w.UseTemplate, _w.TraitsAlone, _w.NoHead }
            .SelectMany(n => FormidsRows(n.FormKey)).Select(r => r.GetProperty("error").GetString()!).ToList();
        Assert.Equal(3, reasons.Count);
        foreach (var r in reasons.Append(NpcFace.UseTemplateFlag).Append(NpcFace.TraitsNoTemplate))
            foreach (var word in new[] { "bug", "defect", "wrong", "broken" })
                Assert.DoesNotContain(word, r, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The json check counts the two new exclusions apart.</summary>
    [Fact]
    public void TheJsonCheckCountsEachExclusionByReason()
    {
        var json = CheckTools.CheckTool(_w.Svc, formids: _w.Npcs.Select(n => Id(n.FormKey)).ToArray(), findings: new[] { "facegen" }, format: "json");
        Assert.Matches(@"""npcs_excluded_use_template_flag"":\s*1[,\s]", json);
        Assert.Matches(@"""npcs_excluded_traits_no_template"":\s*1[,\s]", json);
        Assert.Matches(@"""npcs_excluded_no_facegen_race"":\s*1[,\s]", json);
        Assert.Matches(@"""npcs_excluded_templated"":\s*2[,\s]", json);
    }
}

/// <summary>formids= where the record cannot be read: an NPC in a plugin the index could not read says so with the
/// plugin's reason, not that nothing defines it; in an order that cannot be built at all, every NPC row says that, the
/// asset rows still answer, and a to_file= artifact over paths says it has no fingerprint.</summary>
public sealed class NpcFaceUnreadOrderTests : IDisposable
{
    readonly PlaceInstance _p = new();
    public void Dispose() => _p.Dispose();

    static string Row(LoadOrderService svc, string formid)
        => Assert.Single(JsonDocument.Parse(AssetTools.AssetStatus(svc, formids: new[] { formid }, format: "json"))
                                     .RootElement.GetProperty("results").EnumerateArray()).GetProperty("error").GetString()!;

    [Fact]
    public void AnNpcInAPluginTheIndexCouldNotReadSaysSo()
    {
        FaceNpcPlugin.Write(_p.Mod("Host"), "Good.esp", 0x801);
        File.WriteAllText(Path.Combine(_p.Mod("Host"), "Broken.esp"), "x");
        _p.Profile(new[] { "Good.esp", "Broken.esp" }, new[] { "*Good.esp", "*Broken.esp" }, new[] { "+Host" });
        Assert.Contains("no FaceGen pair — its plugin Broken.esp was not read", Row(_p.Open(), "000801:Broken.esp"));
    }

    [Fact]
    public void AnOrderThatCannotBeReadIsEachNpcRowsReason()
    {
        _p.Profile(new[] { "Missing.esp" }, new[] { "*Missing.esp" }, new[] { "+Host" });
        _p.Mod("Host");
        var svc = _p.Open();
        Assert.Contains("no FaceGen pair — the load order could not be read", Row(svc, "000801:Missing.esp"));

        var file = Path.Combine(_p.Root, "paths.jsonl");
        var text = AssetTools.AssetStatus(svc, asset_paths: new[] { @"meshes\x.nif" }, to_file: file);
        Assert.Contains("epoch: NONE", text);
        Assert.Contains("could not be read for a fingerprint", text);
        Assert.Equal("", JsonDocument.Parse(File.ReadLines(file).First()).RootElement.GetProperty("epoch").GetString());
    }
}
