using System.Text.Json;
using HousecarlCore;
using HousecarlMcp;
using Xunit;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Assets;

namespace HousecarlMcpTests;

/// <summary>asset_status links= (#1081's reader) and walk= (#1270): the files a mesh names, and every file a set of
/// records or paths reaches, each marked from-source, elsewhere, present or absent.</summary>
[Trait("tier", "unit")]
public sealed class AssetWalkTests : IClassFixture<AssetWalkWorld>
{
    readonly AssetWalkWorld _w;
    public AssetWalkTests(AssetWalkWorld w) => _w = w;

    AssetWalkData Walk(string[]? paths = null, string[]? records = null, string? source = null, string[]? fromSource = null)
        => _w.Svc.AssetWalk(paths ?? Array.Empty<string>(),
                            (records ?? Array.Empty<string>()).Select(r => _w.Keys[r]).ToList(), fromSource, source);

    static AssetWalkNode Node(AssetWalkData d, string path)
        => d.Nodes.Single(n => string.Equals(n.Path, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>The cape's root SMP pointer comes back as one labelled link, resolved to its winner, beside its slot,
    /// whose data\ form maps to the Data path.</summary>
    [Fact]
    public void LinksNameTheSmpXmlAndEachSlotWithItsWinner()
    {
        var row = _w.Svc.AssetStatus(new[] { AssetWalkWorld.CapeNif }, links: true).Results.Single();

        var smp = row.Links!.Single(l => l.Link.Kind == NifLinkKind.Smp);
        Assert.Equal(AssetWalkWorld.CapeXml, smp.Link.DataPath);
        Assert.Equal("Cape", smp.Hit!.Winner!.Source);
        var slot = row.Links!.Single(l => l.Link.Kind == NifLinkKind.Texture);
        Assert.Equal(@"textures\hcwalk\cape.dds", slot.Link.DataPath, ignoreCase: true);
        Assert.True(slot.Hit!.Exists);

        var text = AssetTools.AssetStatus(_w.Svc, asset_paths: new[] { AssetWalkWorld.CapeNif }, links: true);
        Assert.Contains("smp — root extra data 'HDT Skinned Mesh Physics Object'", text);
    }

    /// <summary>A row asked without links= carries no links, so the plain row is the row it always was.</summary>
    [Fact]
    public void WithoutLinksTheRowIsUnchanged()
    {
        var row = _w.Svc.AssetStatus(new[] { AssetWalkWorld.CapeNif }).Results.Single();
        Assert.Null(row.Links);
        Assert.DoesNotContain("links (", AssetTools.AssetStatus(_w.Svc, asset_paths: new[] { AssetWalkWorld.CapeNif }));
    }

    /// <summary>links= on the whole-selection arms: a to_file= file and a ceiling spill carry each mesh row's links, and
    /// counts_only= is refused in one sentence.</summary>
    [Fact]
    public void LinksReachTheFileOrAreRefused()
    {
        static JsonElement CapeRow(string file)
        {
            var lines = File.ReadAllLines(file);
            Assert.Contains("\"links\"", lines[0]);   // the manifest's row_schema names it
            return lines.Skip(1).Select(l => JsonDocument.Parse(l).RootElement)
                        .Single(r => string.Equals(r.GetProperty("path").GetString(), AssetWalkWorld.CapeNif, StringComparison.OrdinalIgnoreCase));
        }
        static void HasSmp(JsonElement row) => Assert.Contains(row.GetProperty("links").EnumerateArray(),
            l => l.GetProperty("kind").GetString() == "smp" && string.Equals(l.GetProperty("data_path").GetString(), AssetWalkWorld.CapeXml, StringComparison.OrdinalIgnoreCase));

        var file = Path.Combine(_w.Root, "links-" + Guid.NewGuid().ToString("N") + ".jsonl");
        AssetTools.AssetStatus(_w.Svc, under: new[] { "meshes/hcwalk" }, links: true, to_file: file);
        HasSmp(CapeRow(file));

        var spilled = AssetTools.AssetStatus(_w.Svc, under: new[] { "meshes/hcwalk", "textures/hcwalk" }, links: true, max_chars: 500);
        var at = spilled.IndexOf(" -> ", StringComparison.Ordinal);
        Assert.True(at > 0, spilled);
        HasSmp(CapeRow(spilled[(at + 4)..spilled.IndexOf('\n', at)].Trim()));

        Assert.Contains("counts_only= returns a census with no rows",
                        AssetTools.AssetStatus(_w.Svc, under: new[] { "meshes/hcwalk" }, links: true, counts_only: true));
    }

    /// <summary>A disabled donor: NPC → head part → hair mesh → SMP XML and three textures, each marked from the donor,
    /// elsewhere with its winner, or absent; and place takes the from-source set in one call.</summary>
    [Fact]
    public void ADisabledDonorWalkMarksEachNodeAndPlaceTakesTheSet()
    {
        var d = Walk(records: new[] { "HcWalkHero" }, source: AssetWalkWorld.DonorMod);

        Assert.Equal(WalkStanding.FromSource, Node(d, AssetWalkWorld.HairNif).Standing);
        Assert.Equal(WalkStanding.FromSource, Node(d, AssetWalkWorld.HairXml).Standing);
        Assert.Equal("smp", Node(d, AssetWalkWorld.HairXml).Edge);
        // The base mod ships this path too and wins it in the order; the named donor's copy still answers it.
        var donorTex = Node(d, AssetWalkWorld.DonorTex);
        Assert.Equal(WalkStanding.FromSource, donorTex.Standing);
        Assert.Equal(AssetWalkWorld.DonorMod, donorTex.Copy!.ProviderName);
        var elsewhere = Node(d, AssetWalkWorld.BaseTex);
        Assert.Equal(WalkStanding.Elsewhere, elsewhere.Standing);
        Assert.Equal(AssetWalkWorld.BaseMod, elsewhere.Copy!.ProviderName);
        Assert.Equal(WalkStanding.Absent, Node(d, AssetWalkWorld.MissingTex).Standing);

        var set = AssetWalkWire.PlaceSet(d);
        Assert.Equal(3, set.Count);
        using var doc = JsonDocument.Parse(AssetWalkWire.PlaceArray(set));
        var placed = PlaceTools.Place(_w.Svc, doc.RootElement, source_provider: AssetWalkWorld.DonorMod, patch: "HcWalkPlaced");
        Assert.Contains("placed 3", placed, StringComparison.OrdinalIgnoreCase);
        // The donor's bytes were placed, not the enabled base mod's older copy of the same path.
        var placedTex = Path.Combine(_w.ModsDir, "houseCARL - HcWalkPlaced", AssetWalkWorld.DonorTex);
        Assert.True(File.Exists(placedTex), placed);
        Assert.Equal(AssetWalkWorld.DonorBytes, File.ReadAllBytes(placedTex));
    }

    /// <summary>A FaceGen head mesh walks on like any mesh: its texture slot and its root SMP string become nodes,
    /// absent here, pulled by the head.</summary>
    [Fact]
    public void AFaceGenHeadWalksOnThroughItsLinks()
    {
        var d = Walk(records: new[] { "HcWalkHero" });
        var head = FaceGenPath.For(_w.Keys["HcWalkHero"], FaceGenSlot.Mesh);
        foreach (var (path, edge) in new[] { (AssetWalkWorld.FaceTex, "texture"), (AssetWalkWorld.FaceXml, "smp") })
        {
            var n = Node(d, path);
            Assert.Equal(edge, n.Edge);
            Assert.Equal(head, n.PulledBy, ignoreCase: true);
            Assert.Equal(WalkStanding.Absent, n.Standing);
        }
    }

    /// <summary>An effect-shader texture maps through the texture door and a BODYTRI string through the model door
    /// (meshes-relative), each a link of its own kind.</summary>
    [Fact]
    public void LinksMapEffectTexturesAndBodyTri()
    {
        var links = _w.Svc.AssetStatus(new[] { AssetWalkWorld.FxNif }, links: true).Results.Single().Links!;
        Assert.Equal(@"textures\hcwalk\fx.dds", links.Single(l => l.Link.Kind == NifLinkKind.Texture).Link.DataPath, ignoreCase: true);
        Assert.Contains("effect source texture", links.Single(l => l.Link.Kind == NifLinkKind.Texture).Link.Where);
        Assert.Equal(@"meshes\" + AssetWalkWorld.FxTri, links.Single(l => l.Link.Kind == NifLinkKind.BodyTri).Link.DataPath, ignoreCase: true);
    }

    /// <summary>The json lanes: a links= row carries each link's data_path, and a walk document carries its place_set.</summary>
    [Fact]
    public void TheJsonLanesCarryLinksAndThePlaceSet()
    {
        using var row = JsonDocument.Parse(AssetTools.AssetStatus(_w.Svc, asset_paths: new[] { AssetWalkWorld.CapeNif }, links: true, format: "json"));
        Assert.Contains(row.RootElement.GetProperty("results")[0].GetProperty("links").EnumerateArray(),
                        l => string.Equals(l.GetProperty("data_path").GetString(), AssetWalkWorld.CapeXml, StringComparison.OrdinalIgnoreCase));

        using var walk = JsonDocument.Parse(AssetTools.AssetStatus(_w.Svc, asset_paths: new[] { AssetWalkWorld.CapeNif }, walk: Walks.On, source_provider: "Cape", format: "json"));
        Assert.Equal(3, walk.RootElement.GetProperty("place_set").GetArrayLength());
    }

    /// <summary>An NPC with a FaceGenHead race reaches its pair at FaceGenPath.For's paths, keyed; one whose race has no
    /// FaceGenHead reaches none.</summary>
    [Fact]
    public void FaceGenIsTheRaceGatedPairAndKeyed()
    {
        var d = Walk(records: new[] { "HcWalkHero" });
        foreach (var slot in new[] { FaceGenSlot.Mesh, FaceGenSlot.Tint })
        {
            var n = Node(d, FaceGenPath.For(_w.Keys["HcWalkHero"], slot));
            Assert.Equal("facegen", n.Edge);
            Assert.True(n.Keyed);
            Assert.Equal(WalkStanding.Present, n.Standing);
        }

        var beast = Walk(records: new[] { "HcWalkBeast" });
        Assert.DoesNotContain(beast.Nodes, n => n.Edge == "facegen");
    }

    /// <summary>A Traits-templated NPC two deep draws its root's face, not its own id.</summary>
    [Fact]
    public void ATemplatedNpcReachesItsRootsFaceGen()
    {
        var d = Walk(records: new[] { "HcWalkTemplated" });
        var face = d.Nodes.Where(n => n.Edge == "facegen").Select(n => n.Path).ToList();
        // Pulled by the templated NPC itself, not reached later through its form links to the root.
        var root = Node(d, FaceGenPath.For(_w.Keys["HcWalkHero"], FaceGenSlot.Mesh));
        Assert.Contains("HcWalkTemplated", root.PulledBy);
        Assert.Contains("template root " + _w.Id("HcWalkHero"), root.Note);
        foreach (var slot in new[] { FaceGenSlot.Mesh, FaceGenSlot.Tint })
            Assert.DoesNotContain(FaceGenPath.For(_w.Keys["HcWalkTemplated"], slot), face, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A leveled-list template reaches every member's root, each one of 2; a looping chain is a note naming it.</summary>
    [Fact]
    public void ALeveledTemplateReachesEachMemberAndALoopIsNamed()
    {
        var d = Walk(records: new[] { "HcWalkLeveled" });
        var face = d.Nodes.Where(n => n.Edge == "facegen" && n.Path.EndsWith(".nif", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Equal(2, face.Count);
        Assert.All(face, n => Assert.Contains("one of 2", n.Note));

        // The looping pair is in memory only: Mutagen's voice resolution overflows the stack on one in any order it reads.
        var mod = new SkyrimMod(ModKey.FromFileName("HcWalkLoop.esp"), SkyrimRelease.SkyrimSE);
        var a = mod.Npcs.AddNew("HcWalkLoopA");
        var b = mod.Npcs.AddNew("HcWalkLoopB");
        foreach (var (n, to) in new[] { (a, b), (b, a) })
        {
            n.Template.SetTo(to);
            n.Configuration.TemplateFlags = NpcConfiguration.TemplateFlag.Traits;
        }
        var (roots, errors) = NpcFace.FaceRoots(a, fk => mod.Npcs.TryGetValue(fk));
        Assert.Empty(roots);
        Assert.Contains(errors, e => e.Contains("loops") && e.Contains(FormIdToken.Of(a.FormKey)) && e.Contains(FormIdToken.Of(b.FormKey)));
    }

    /// <summary>A leveled template nesting another reaches both members' roots, each one of 2; a template list with an
    /// entry that resolves to nothing keeps its good members' pairs and names the bad entry; a template that resolves
    /// to nothing is a note naming the chain.</summary>
    [Fact]
    public void ANestedOrPartlyBadLeveledTemplateKeepsEveryGoodMember()
    {
        foreach (var npc in new[] { "HcWalkNested", "HcWalkPartial" })
        {
            var d = Walk(records: new[] { npc });
            foreach (var member in new[] { "HcWalkHero", "HcWalkBeast2" })
            {
                var n = Node(d, FaceGenPath.For(_w.Keys[member], FaceGenSlot.Mesh));
                Assert.Contains("one of 2", n.Note);
                Assert.Contains(npc, n.PulledBy);
            }
            if (npc == "HcWalkPartial")
                Assert.Contains(d.Notes, n => n.Contains("does not resolve") && n.Contains(FormIdToken.Of(AssetWalkWorld.Missing)));
        }

        var dangling = Walk(records: new[] { "HcWalkDangling" });
        Assert.DoesNotContain(dangling.Nodes, n => n.Edge == "facegen");
        Assert.Contains(dangling.Notes, n => n.Contains("no FaceGen node") && n.Contains(FormIdToken.Of(AssetWalkWorld.Missing)));
    }

    /// <summary>A record's own script reaches its .pex and never the .psc source.</summary>
    [Fact]
    public void AScriptReachesItsPexOnly()
    {
        var d = Walk(records: new[] { "HcWalkHero" });
        Assert.Equal("script", Node(d, AssetWalkEdges.ScriptPex(AssetWalkWorld.OwnScript)).Edge);
        Assert.DoesNotContain(d.Nodes, n => n.Path.EndsWith(".psc", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A quest alias's script reaches its .pex.</summary>
    [Fact]
    public void AnAliasScriptReachesItsPex()
        => Assert.Equal("script", Node(Walk(records: new[] { "HcWalkAliased" }), AssetWalkEdges.ScriptPex(AssetWalkWorld.AliasScript)).Edge);

    /// <summary>A walk seeded by a formids= artifact from another build is refused, epoch-checked as the row lane is.</summary>
    [Fact]
    public void AWalkFormidsArtifactIsEpochChecked()
    {
        var file = Path.Combine(_w.Root, "ids-" + Guid.NewGuid().ToString("N") + ".jsonl");
        using (var writer = new ResultArtifact.Writer())
        {
            writer.WriteRow((w, _) => { w.WriteStartObject(); w.WriteString("formid", _w.Id("HcWalkHero")); w.WriteEndObject(); });
            Assert.Null(writer.Save(ArtifactTarget.Named(file), ToolNames.Records, Array.Empty<KeyValuePair<string, string>>(),
                                    "formid", new[] { "formid" }, "input order", total: 1, epoch: "e2-deadbeef").Error);
        }
        Assert.Contains("e2-deadbeef", AssetTools.AssetStatus(_w.Svc, formids: new[] { "@" + file }, walk: Walks.On));
    }

    /// <summary>A start-game-enabled quest reaches its plugin's .seq, keyed and absent here; one without the flag none.</summary>
    [Fact]
    public void AStartGameEnabledQuestReachesItsSeq()
    {
        var d = Walk(records: new[] { "HcWalkSge" });
        var seq = d.Nodes.Single(n => n.Edge == "seq");
        Assert.True(seq.Keyed);
        Assert.Equal(WalkStanding.Absent, seq.Standing);
        Assert.EndsWith("HcWalk.seq", seq.Path, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain(Walk(records: new[] { "HcWalkPlain" }).Nodes, n => n.Edge == "seq");
    }

    /// <summary>An override donor read through from_source= enters the head part its own plugin defines, by the
    /// master's FormKey, and walks on to its mesh's texture; a link into a third plugin stays a counted boundary.</summary>
    [Fact]
    public void AnOverrideDonorsOwnRecordsAreEntered()
    {
        var d = Walk(records: new[] { "HcWalkBeast2" }, source: AssetWalkWorld.OverrideMod,
                     fromSource: new[] { AssetWalkWorld.OverridePlugin });
        Assert.Equal(WalkStanding.FromSource, Node(d, AssetWalkWorld.OvHairNif).Standing);
        Assert.Contains("HcWalkOvHair", Node(d, AssetWalkWorld.OvHairNif).PulledBy);
        Assert.Equal(AssetWalkWorld.OvHairNif, Node(d, AssetWalkWorld.OvHairTex).PulledBy, ignoreCase: true);
        Assert.Contains(d.Notes, n => n.Contains("boundaries") && n.Contains(AssetWalkWorld.OtherPlugin + " ×1"));
    }

    /// <summary>A seed in a base master read through a from_source donor: the master's linked records are boundaries,
    /// the donor's head part is entered. The same seed read through the winner enters its own plugin.</summary>
    [Fact]
    public void ABaseMasterSeedReadThroughADonorKeepsItsPluginAsABoundary()
    {
        var d = Walk(records: new[] { "HcWalkBeast2" }, fromSource: new[] { AssetWalkWorld.OverridePlugin });
        Assert.Contains(d.Notes, n => n.Contains("boundaries") && n.Contains(AssetWalkWorld.Plugin + " ×"));
        Assert.Contains("HcWalkOvHair", Node(d, AssetWalkWorld.OvHairNif).PulledBy);

        var w = Walk(records: new[] { "HcWalkBeast2" });
        Assert.DoesNotContain(w.Notes, n => n.Contains(AssetWalkWorld.Plugin + " ×"));
    }

    /// <summary>An NPC read through a from_source plugin that does not hold its race still gets its FaceGen pair, the
    /// race read off the load order; a race nothing holds is a note naming it, never silence.</summary>
    [Fact]
    public void ARaceOutsideTheRecordSourcesStillGivesTheFace()
    {
        var d = Walk(records: new[] { "HcWalkBeast2" }, fromSource: new[] { AssetWalkWorld.OverridePlugin });
        foreach (var slot in new[] { FaceGenSlot.Mesh, FaceGenSlot.Tint })
            Assert.Equal("facegen", Node(d, FaceGenPath.For(_w.Keys["HcWalkBeast2"], slot)).Edge);

        var none = Walk(records: new[] { "HcWalkNoRace" });
        Assert.DoesNotContain(none.Nodes, n => n.Edge == "facegen");
        Assert.Contains(none.Notes, n => n.Contains("no FaceGen node") && n.Contains("race " + FormIdToken.Of(AssetWalkWorld.Missing)));
    }

    /// <summary>A from_source arm that has an NPC's race but cannot read it: a note naming the fault, and no FaceGen
    /// node from the winner's copy of the race. A race link to a record of another type is a note naming that type.</summary>
    [Fact]
    public void ARaceTheWalkCannotReadIsNamedNotMissed()
    {
        var mod = new SkyrimMod(ModKey.FromFileName("HcWalkFault.esp"), SkyrimRelease.SkyrimSE);
        var race = mod.Races.AddNew("HcWalkFaultRace");
        race.Flags = Race.Flag.FaceGenHead;
        var npc = mod.Npcs.AddNew("HcWalkFaultNpc");
        npc.Race.SetTo(race);
        var arm = new SourceArm("HcWalkFault.esp", SourceArmKind.File, "test",
                                _ => fk => fk == race.FormKey ? throw new IOException("the file is locked") : fk == npc.FormKey ? npc : null);
        var fetch = new AssetLayers.MemberReads(SourceChain.Single(arm), _ => fk => fk == race.FormKey ? race : null).Get;
        var added = new List<string>();
        var notes = new List<string>();
        AssetLayers.FaceGenOf(npc, "Npc HcWalkFaultNpc", new NpcFace(fetch, AssetLayers.RecordSourcesUnset), (raw, _, _, _, _) => added.Add(raw), notes);
        Assert.Empty(added);
        Assert.Contains(notes, n => n.Contains("'HcWalkFault.esp' has it but could not read it — the file is locked"));
        Assert.DoesNotContain(notes, n => n.Contains(AssetLayers.RecordSourcesUnset));

        var wrong = Walk(records: new[] { "HcWalkWrongRace" });
        Assert.DoesNotContain(wrong.Nodes, n => n.Edge == "facegen");
        Assert.Contains(wrong.Notes, n => n.Contains("names a VoiceType, not a race"));
    }

    /// <summary>An NPC with the Traits flag and no Template: Mutagen names no face for it, so the walk has no FaceGen
    /// node for it and a note with that reason, though its own pair ships.</summary>
    [Fact]
    public void TraitsWithNoTemplateIsANoteNotItsOwnFace()
    {
        var d = Walk(records: new[] { "HcWalkTraitsAlone" });
        Assert.DoesNotContain(d.Nodes, n => n.Edge == "facegen");
        Assert.Contains(d.Notes, n => n.Contains("HcWalkTraitsAlone") && n.Contains(NpcFace.TraitsNoTemplate));
    }

    /// <summary>A formids= walk seed with a source: both FaceGen nodes keyed, and the place set carries the keyed warning.</summary>
    [Fact]
    public void AFormidsSeedIsKeyed()
    {
        var text = AssetTools.AssetStatus(_w.Svc, formids: new[] { _w.Id("HcWalkHero") }, walk: Walks.On, source_provider: AssetWalkWorld.BaseMod);
        foreach (var slot in new[] { FaceGenSlot.Mesh, FaceGenSlot.Tint })
            Assert.Contains(FaceGenPath.For(_w.Keys["HcWalkHero"], slot) + "\n  seed of the selection   [keyed]", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("keyed nodes keep their FormID-named path", text);
    }

    /// <summary>A walk past max_chars, text and json, stays within the cap and names a results-directory file holding
    /// every node, even when the place set's array alone passes the cap.</summary>
    [Fact]
    public void AWalkPastTheCeilingSpillsEveryNode()
    {
        const int cap = 2500;
        static string[] Rows(string file) => File.ReadAllLines(file).Skip(1).ToArray();

        var text = AssetTools.AssetStatus(_w.Svc, asset_paths: AssetWalkWorld.ManyTex, walk: Walks.On,
                                          source_provider: AssetWalkWorld.DonorMod, max_chars: cap);
        Assert.True(text.Length <= cap, text);
        var at = text.IndexOf("spilled: complete result", StringComparison.Ordinal);
        Assert.True(at >= 0, text);
        var arrow = text.IndexOf(" -> ", at, StringComparison.Ordinal) + 4;
        var file = text[arrow..text.IndexOf('\n', arrow)].Trim();
        Assert.StartsWith(_w.Svc.ResultsDir, file, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(AssetWalkWorld.ManyTex.Length, Rows(file).Length);
        Assert.Contains("place set (60): the spilled file's rows", text);

        var json = AssetTools.AssetStatus(_w.Svc, asset_paths: AssetWalkWorld.ManyTex, walk: Walks.On,
                                          source_provider: AssetWalkWorld.DonorMod, format: "json", max_chars: cap);
        Assert.True(json.Length <= cap, json);
        using var doc = JsonDocument.Parse(json);
        var rows = Rows(doc.RootElement.GetProperty("spilled").GetProperty("path").GetString()!);
        Assert.Equal(AssetWalkWorld.ManyTex.Length, rows.Length);
        Assert.All(rows, r => Assert.Contains("\"standing\":\"from_source\"", r));
    }

    /// <summary>A walk past max_chars whose results file cannot be written, text and json, claims no file holds the
    /// nodes or the place set.</summary>
    [Fact]
    public void AFailedWalkSpillNamesNoFile()
    {
        const int cap = 2500;
        var dir = _w.Svc.ResultsDir;
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        File.WriteAllText(dir, "a file where the results folder goes");
        try
        {
            var text = AssetTools.AssetStatus(_w.Svc, asset_paths: AssetWalkWorld.ManyTex, walk: Walks.On,
                                              source_provider: AssetWalkWorld.DonorMod, max_chars: cap);
            Assert.Contains("could NOT be written", text);
            Assert.DoesNotContain("Every node is in the file", text);
            Assert.DoesNotContain("spilled file", text);
            Assert.Contains("place set (60): not shown", text);

            var json = AssetTools.AssetStatus(_w.Svc, asset_paths: AssetWalkWorld.ManyTex, walk: Walks.On,
                                              source_provider: AssetWalkWorld.DonorMod, format: "json", max_chars: cap);
            using var doc = JsonDocument.Parse(json);
            Assert.True(doc.RootElement.TryGetProperty("spill_error", out _), json);
            Assert.False(doc.RootElement.TryGetProperty("place_set_in_file", out _), json);
        }
        finally { File.Delete(dir); }
    }

    /// <summary>A dialogue line reaches the .fuz Mutagen resolves for its speaker, keyed.</summary>
    [Fact]
    public void ALineReachesItsVoiceFuz()
    {
        var d = Walk(records: new[] { "HcWalkLineSpeaker" });
        var fuz = Node(d, _w.Voice("HcWalkLineSpeaker", VoiceFile.Fuz));
        Assert.Equal("voice", fuz.Edge);
        Assert.True(fuz.Keyed);
    }

    /// <summary>No line is found off an NPC (#1278, V4): an NPC seed whose lines name it as Speaker and in a GetIsID
    /// lists no voice node and no line, with no through and with one leaving lines out, and at a record cap of one.</summary>
    [Fact]
    public void AnNpcSeedReachesNoLine()
    {
        foreach (var values in new WalkGrammar.Values?[] { null, Values("{\"through\": [\"VoiceType\"]}"), Values("{\"max_nodes\": 1}") })
        {
            var d = _w.Svc.AssetWalk(Array.Empty<string>(), new[] { _w.Keys["HcWalkSpeaker"] }, null, null, values: values);
            Assert.DoesNotContain(d.Nodes, n => n.Edge is "voice" or "lip");
            foreach (var line in new[] { "HcWalkLineSpeaker", "HcWalkLineCond" })
            {
                Assert.DoesNotContain(d.Nodes, n => n.PulledBy.Contains($"[{_w.Id(line)}]"));
                Assert.DoesNotContain(d.Notes, n => n.Contains($"[{_w.Id(line)}]"));
            }
        }
    }

    /// <summary>A line the walk reaches forward (shared response data) is a record like any other (#1278, closing the
    /// walk grammar's known limit): voiced when entered, refused by a refuse exclusion, counted by walk.max_nodes, and
    /// left out and counted by a through without DialogResponses. Pins behaviour main already had for such a line.</summary>
    [Fact]
    public void ALineReachedForwardIsJudgedAndCounted()
    {
        AssetWalkData Run(string? walk) => _w.Svc.AssetWalk(Array.Empty<string>(), new[] { _w.Keys["HcWalkLineShared"] }, null, null, values: walk is null ? null : Values(walk));
        var spoken = _w.Voice("HcWalkLineSpeaker", VoiceFile.Fuz);
        Assert.Contains($"DialogResponses [{_w.Id("HcWalkLineSpeaker")}]", Node(Run(null), spoken).PulledBy);
        Assert.StartsWith("error: the walk reached a DialogResponses (", Run("{\"exclusions\": [{\"match\": \"DialogResponses\", \"severity\": \"refuse\"}]}").Refusal);
        var capped = Run("{\"max_nodes\": 1}");
        Assert.True(capped.Truncated);
        Assert.DoesNotContain(capped.Nodes, n => n.Path.Equals(spoken, StringComparison.OrdinalIgnoreCase));
        var through = Run("{\"through\": [\"Npc\"]}");
        Assert.DoesNotContain(through.Nodes, n => n.Path.Equals(spoken, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(through.Notes, n => n.StartsWith("left out, not in walk.through: ") && n.Contains("1 DialogResponses"));
    }

    /// <summary>A line a disabled donor defines, seeded through from_source=, is voiced off the donor; the donor's NPC
    /// seeded alone lists its face and no line (#1278, V4).</summary>
    [Fact]
    public void AnOffOrderLineSeedIsVoiced()
    {
        var line = _w.Svc.AssetWalk(Array.Empty<string>(), new[] { _w.Keys["HcWalkOffLine"] }, new[] { AssetWalkWorld.OffPlugin }, null);
        var voice = Assert.Single(line.Nodes, n => n.Edge == "voice");
        Assert.Equal($"DialogResponses [{_w.Id("HcWalkOffLine")}]", voice.PulledBy);
        var npc = Walk(records: new[] { "HcWalkOffNpc" }, fromSource: new[] { AssetWalkWorld.OffPlugin });
        Assert.DoesNotContain(npc.Nodes, n => n.Edge == "voice");
        Assert.Contains(npc.Nodes, n => n.Edge == "facegen");
    }

    WalkGrammar.Values Values(string walk)
    {
        Assert.Null(WalkGrammar.ReadAssetWalk(_w.Svc.Types, Walks.Of(walk), out var v));
        return v!;
    }

    /// <summary>through's edge kinds choose the rows, the walk whole (#1278, plain E2): the donor walk with
    /// through=[texture] shows the hair mesh's three textures and the face mesh's one, in walk order, each naming its
    /// mesh in pulled_by while no mesh row is shown; every other row is counted by edge on the one left-out line.</summary>
    [Fact]
    public void AnEdgeKindShowsItsRowsAndCountsTheRest()
    {
        var whole = Walk(records: new[] { "HcWalkHero" }, source: AssetWalkWorld.DonorMod);
        var d = _w.Svc.AssetWalk(Array.Empty<string>(), new[] { _w.Keys["HcWalkHero"] }, null, AssetWalkWorld.DonorMod, values: Values("{\"through\": [\"texture\"]}"));
        Assert.Equal(whole.Nodes.Where(n => n.Edge == "texture").Select(n => n.Path), d.Nodes.Select(n => n.Path));
        Assert.Equal(AssetWalkWorld.HairNif, Node(d, AssetWalkWorld.DonorTex).PulledBy, ignoreCase: true);
        Assert.DoesNotContain(d.Nodes, n => n.Path.EndsWith(".nif", StringComparison.OrdinalIgnoreCase));
        var hidden = whole.Nodes.Where(n => n.Edge != "texture").GroupBy(n => n.Edge)
                          .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
                          .Select(g => $"{g.Count()} {g.Key} row{(g.Count() == 1 ? "" : "s")}");
        Assert.Contains("left out, not in walk.through: " + string.Join(", ", hidden), d.Notes);
        // The place set and the counts read the rows shown, so they agree with the answer.
        Assert.Equal(new[] { AssetWalkWorld.DonorTex }, AssetWalkWire.PlaceSet(d), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>An edge kind changes no read (#1278): with and without through=[texture], the walk reads the same
    /// groups and bodies, reaches the same files (the rows shown plus the rows counted), and writes the same notes but
    /// the left-out line.</summary>
    [Fact]
    public void AnEdgeKindLeavesTheWalkWhole()
    {
        (AssetWalkData Data, Dictionary<(string, string), int> Reads, long Seeks) Measured(WalkGrammar.Values? values)
        {
            var before = new Dictionary<(string, string), int>(_w.Svc.Counters.GroupReads);
            var seeks = _w.Svc.Counters.BodySeeks;
            var d = _w.Svc.AssetWalk(Array.Empty<string>(), new[] { _w.Keys["HcWalkHero"] }, null, AssetWalkWorld.DonorMod, values: values);
            var reads = _w.Svc.Counters.GroupReads.Where(kv => kv.Value != before.GetValueOrDefault(kv.Key))
                                                 .ToDictionary(kv => kv.Key, kv => kv.Value - before.GetValueOrDefault(kv.Key));
            return (d, reads, _w.Svc.Counters.BodySeeks - seeks);
        }
        Measured(null);
        var whole = Measured(null);
        var narrowed = Measured(Values("{\"through\": [\"texture\"]}"));
        Assert.Equal(whole.Reads.OrderBy(kv => kv.Key.ToString()), narrowed.Reads.OrderBy(kv => kv.Key.ToString()));
        Assert.Equal(whole.Seeks, narrowed.Seeks);
        var leftOut = Assert.Single(narrowed.Data.Notes, n => n.StartsWith("left out, not in walk.through: "));
        Assert.Equal(whole.Data.Notes, narrowed.Data.Notes.Where(n => n != leftOut));
        int counted = leftOut["left out, not in walk.through: ".Length..].Split(", ").Sum(p => int.Parse(p.Split(' ')[0]));
        Assert.Equal(whole.Data.Nodes.Count, narrowed.Data.Nodes.Count + counted);
    }

    /// <summary>On a path-only walk an edge kind chooses the rows too (#1278): the cape seed with
    /// through=[listed, texture] shows its seed row and its texture and counts its SMP row, with no values-unused note.</summary>
    [Fact]
    public void AnEdgeKindChoosesAPathOnlyWalksRows()
    {
        var text = AssetTools.AssetStatus(_w.Svc, asset_paths: new[] { AssetWalkWorld.CapeNif }, walk: Walks.Of("{\"through\": [\"listed\", \"texture\"]}"), format: "json");
        using var doc = JsonDocument.Parse(text);
        var edges = doc.RootElement.GetProperty("nodes").EnumerateArray().Select(n => n.GetProperty("edge").GetString()).ToList();
        Assert.Equal(new[] { "seed", "texture" }, edges);
        var notes = doc.RootElement.GetProperty("notes").EnumerateArray().Select(n => n.GetString()).ToList();
        Assert.Contains("left out, not in walk.through: 1 smp row", notes);
        Assert.DoesNotContain(ReadSentences.WalkValuesWithoutRecordSeed, notes);
    }

    /// <summary>The record cycles (#1278, C2): two lists naming each other are one loop, counted in one text note and
    /// listed in the json answer and the to_file= file, not in the text; a list naming 201 self-naming lists stops at
    /// the 200-loop cap and says so.</summary>
    [Fact]
    public void ALoopOfRecordsIsCountedAndListed()
    {
        var loop = $"{_w.Id("HcWalkLoopA")} (HcWalkLoopA) -> {_w.Id("HcWalkLoopB")} (HcWalkLoopB) -> {_w.Id("HcWalkLoopA")} (HcWalkLoopA)";
        const string Count = "1 record cycle(s) found (recorded, not an error): a record the walk reached again from itself, one per closing link; the loops are listed in format=json and in a to_file= file.";
        var seed = new[] { _w.Id("HcWalkLoopA") };
        var text = AssetTools.AssetStatus(_w.Svc, records: seed, walk: Walks.On);
        Assert.Contains(Count, text);
        Assert.DoesNotContain(loop, text);
        using (var doc = JsonDocument.Parse(AssetTools.AssetStatus(_w.Svc, records: seed, walk: Walks.On, format: "json")))
        {
            Assert.Equal(new[] { loop }, doc.RootElement.GetProperty("cycles").EnumerateArray().Select(c => c.GetString()));
            Assert.False(doc.RootElement.TryGetProperty("cycles_capped", out _));
        }
        var file = Path.Combine(_w.Root, "loop-" + Guid.NewGuid().ToString("N") + ".jsonl");
        AssetTools.AssetStatus(_w.Svc, records: seed, walk: Walks.On, to_file: file);
        using (var manifest = JsonDocument.Parse(File.ReadAllLines(file)[0]))
            Assert.Contains("walk cycle: " + loop, manifest.RootElement.GetProperty("notes").EnumerateArray().Select(n => n.GetString()));

        var many = _w.Svc.AssetWalk(Array.Empty<string>(), new[] { _w.Keys["HcWalkSelves"] }, null, null);
        Assert.Equal(RecordReads.WalkCycleCap, many.Cycles!.Count);
        Assert.True(many.CyclesCapped);
        Assert.Contains($"the cycle search stopped at {RecordReads.WalkCycleCap} loops — more loops are there than are counted.", many.Notes);
        Assert.Empty(Walk(records: new[] { "HcWalkHero" }).Cycles!);
    }
    /// <summary>A disabled donor shipping a line as .xwm + .lip with nothing active: both from the source and in the
    /// place set. An active .fuz beside a donor .xwm: both listed.</summary>
    [Fact]
    public void VoiceFormatIsReadAgainstTheSource()
    {
        var d = Walk(records: new[] { "HcWalkLineSpeaker", "HcWalkLineCond" }, source: AssetWalkWorld.DonorMod);
        var xwm = _w.Voice("HcWalkLineSpeaker", VoiceFile.Fuz)[..^4] + ".xwm";
        var lip = _w.Voice("HcWalkLineSpeaker", VoiceFile.Lip);
        Assert.Equal(WalkStanding.FromSource, Node(d, xwm).Standing);
        Assert.Equal(WalkStanding.FromSource, Node(d, lip).Standing);
        Assert.Contains(xwm, AssetWalkWire.PlaceSet(d), StringComparer.OrdinalIgnoreCase);
        Assert.Contains(lip, AssetWalkWire.PlaceSet(d), StringComparer.OrdinalIgnoreCase);

        Assert.Equal(WalkStanding.Elsewhere, Node(d, _w.Voice("HcWalkLineCond", VoiceFile.Fuz)).Standing);
        Assert.Equal(WalkStanding.FromSource, Node(d, _w.Voice("HcWalkLineCond", VoiceFile.Fuz)[..^4] + ".xwm").Standing);
    }

    /// <summary>A line with no speaker and a line whose speaker has no voice type each get a note naming its own reason.</summary>
    [Fact]
    public void ALineWithNoVoiceSaysWhy()
    {
        Assert.Contains(Walk(records: new[] { "HcWalkLineNoSpeaker" }).Notes, n => n.Contains("no voice node — no Speaker set"));
        Assert.Contains(Walk(records: new[] { "HcWalkLineMute" }).Notes,
                        n => n.Contains($"no voice node — its Speaker NPC {_w.Id("HcWalkMute")} has no Voice type set"));
    }

    /// <summary>A line with no responses of its own names the shared response data it draws on, with a Speaker and
    /// without; one with no shared response data either gets no Speaker or quest-alias reason.</summary>
    [Fact]
    public void ALineWithNoOwnResponsesSaysSo()
    {
        foreach (var line in new[] { "HcWalkLineShared", "HcWalkLineSharedNoSpeaker", "HcWalkLineBare" })
        {
            var notes = Walk(records: new[] { line }).Notes;
            var note = Assert.Single(notes, n => n.StartsWith($"DialogResponses [{_w.Id(line)}]: no voice node"));
            Assert.Contains("no responses of its own", note);
            if (line != "HcWalkLineBare") Assert.Contains("shared response data " + _w.Id("HcWalkLineSpeaker"), note);
            Assert.DoesNotContain("Speaker", note);
            Assert.DoesNotContain("speaker", note);
            Assert.DoesNotContain("alias", note);
        }
    }

    /// <summary>No seed is refused in one sentence; records= without walk= is refused.</summary>
    [Fact]
    public void AWalkWithNoSeedIsRefused()
    {
        Assert.NotNull(Walk().Refusal);
        Assert.Contains("pass walk=true", AssetTools.AssetStatus(_w.Svc, records: new[] { _w.Id("HcWalkHero") }));
    }

    /// <summary>A path seed walks the same file edges: the cape reaches its XML through the tool front.</summary>
    [Fact]
    public void APathSeedWalksThroughTheToolFront()
    {
        var text = AssetTools.AssetStatus(_w.Svc, asset_paths: new[] { AssetWalkWorld.CapeNif }, walk: Walks.On, source_provider: "Cape");
        Assert.Contains("smp of " + AssetWalkWorld.CapeNif, text);
        Assert.Contains("place set (3)", text);
        // A path-only walk read no record, so it names no record source, whatever from_source= says.
        Assert.DoesNotContain("records from", text);
        var json = AssetTools.AssetStatus(_w.Svc, asset_paths: new[] { AssetWalkWorld.CapeNif }, walk: Walks.On, from_source: new[] { "winner" }, format: "json");
        Assert.Equal(JsonValueKind.Null, JsonDocument.Parse(json).RootElement.GetProperty("record_sources").ValueKind);
        Assert.Contains("records from " + AssetWalkWorld.OverridePlugin,
            AssetTools.AssetStatus(_w.Svc, records: new[] { _w.Id("HcWalkBeast2") }, walk: Walks.On, from_source: new[] { AssetWalkWorld.OverridePlugin }));
    }
}
