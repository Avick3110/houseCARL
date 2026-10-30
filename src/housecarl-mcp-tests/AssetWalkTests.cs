using System.Text.Json;
using HousecarlCore;
using HousecarlMcp;
using Xunit;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Skyrim;
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
        Assert.Equal(WalkStanding.FromSource, Node(d, AssetWalkWorld.DonorTex).Standing);
        var elsewhere = Node(d, AssetWalkWorld.BaseTex);
        Assert.Equal(WalkStanding.Elsewhere, elsewhere.Standing);
        Assert.Equal(AssetWalkWorld.BaseMod, elsewhere.Copy!.ProviderName);
        Assert.Equal(WalkStanding.Absent, Node(d, AssetWalkWorld.MissingTex).Standing);

        var set = AssetWalkWire.PlaceSet(d);
        Assert.Equal(3, set.Count);
        using var doc = JsonDocument.Parse(AssetWalkWire.PlaceArray(set));
        var placed = PlaceTools.Place(_w.Svc, doc.RootElement, source_provider: AssetWalkWorld.DonorMod, patch: "HcWalkPlaced");
        Assert.Contains("placed 3", placed, StringComparison.OrdinalIgnoreCase);
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
        Assert.DoesNotContain(face, p => p.Contains(_w.Keys["HcWalkTemplated"].ID.ToString("X6"), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A leveled-list template reaches every member's root, each one of 2; a looping chain is a note naming it.</summary>
    [Fact]
    public void ALeveledTemplateReachesEachMemberAndALoopIsNamed()
    {
        var d = Walk(records: new[] { "HcWalkLeveled" });
        var face = d.Nodes.Where(n => n.Edge == "facegen" && n.Path.EndsWith(".nif", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Equal(2, face.Count);
        Assert.All(face, n => Assert.Contains("one of 2", n.Note));

        var loop = Walk(records: new[] { "HcWalkLoopA" }, fromSource: new[] { AssetWalkWorld.OverridePlugin });
        Assert.DoesNotContain(loop.Nodes, n => n.Edge == "facegen");
        Assert.Contains(loop.Notes, n => n.Contains("loops"));
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

    /// <summary>An NPC with the Traits flag and no Template draws its own face: both FaceGen nodes, with a note saying why.</summary>
    [Fact]
    public void TraitsWithNoTemplateGivesItsOwnFace()
    {
        var d = Walk(records: new[] { "HcWalkTraitsAlone" });
        foreach (var slot in new[] { FaceGenSlot.Mesh, FaceGenSlot.Tint })
        {
            var n = Node(d, FaceGenPath.For(_w.Keys["HcWalkTraitsAlone"], slot));
            Assert.Equal(WalkStanding.Present, n.Standing);
            Assert.Contains("no Template", n.Note);
        }
    }

    /// <summary>A formids= walk seed with a source: both FaceGen nodes keyed, and the place set carries the keyed warning.</summary>
    [Fact]
    public void AFormidsSeedIsKeyed()
    {
        var text = AssetTools.AssetStatus(_w.Svc, formids: new[] { _w.Id("HcWalkHero") }, walk: true, source_provider: AssetWalkWorld.BaseMod);
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

        var text = AssetTools.AssetStatus(_w.Svc, asset_paths: AssetWalkWorld.ManyTex, walk: true,
                                          source_provider: AssetWalkWorld.DonorMod, max_chars: cap);
        Assert.True(text.Length <= cap, text);
        var at = text.IndexOf("spilled: complete result", StringComparison.Ordinal);
        Assert.True(at >= 0, text);
        var arrow = text.IndexOf(" -> ", at, StringComparison.Ordinal) + 4;
        var file = text[arrow..text.IndexOf('\n', arrow)].Trim();
        Assert.StartsWith(_w.Svc.ResultsDir, file, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(AssetWalkWorld.ManyTex.Length, Rows(file).Length);
        Assert.Contains("place set (60): the spilled file's rows", text);

        var json = AssetTools.AssetStatus(_w.Svc, asset_paths: AssetWalkWorld.ManyTex, walk: true,
                                          source_provider: AssetWalkWorld.DonorMod, format: "json", max_chars: cap);
        Assert.True(json.Length <= cap, json);
        using var doc = JsonDocument.Parse(json);
        var rows = Rows(doc.RootElement.GetProperty("spilled").GetProperty("path").GetString()!);
        Assert.Equal(AssetWalkWorld.ManyTex.Length, rows.Length);
        Assert.All(rows, r => Assert.Contains("\"standing\":\"from_source\"", r));
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

    /// <summary>An NPC seed reaches the voice files of the line that names it as Speaker and the line that names it
    /// in a GetIsID condition.</summary>
    [Fact]
    public void AnNpcReachesItsLinesVoice()
    {
        var d = Walk(records: new[] { "HcWalkSpeaker" });
        foreach (var line in new[] { "HcWalkLineSpeaker", "HcWalkLineCond" })
        {
            var fuz = Node(d, _w.Voice(line, VoiceFile.Fuz));
            Assert.Equal("voice", fuz.Edge);
            Assert.Contains("a line naming Npc HcWalkSpeaker", fuz.PulledBy);
        }
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
        var text = AssetTools.AssetStatus(_w.Svc, asset_paths: new[] { AssetWalkWorld.CapeNif }, walk: true, source_provider: "Cape");
        Assert.Contains("smp of " + AssetWalkWorld.CapeNif, text);
        Assert.Contains("place set (3)", text);
        // A path-only walk read no record, so it names no record source, whatever from_source= says.
        Assert.DoesNotContain("records from", text);
        var json = AssetTools.AssetStatus(_w.Svc, asset_paths: new[] { AssetWalkWorld.CapeNif }, walk: true, from_source: new[] { "winner" }, format: "json");
        Assert.Equal(JsonValueKind.Null, JsonDocument.Parse(json).RootElement.GetProperty("record_sources").ValueKind);
        Assert.Contains("records from " + AssetWalkWorld.OverridePlugin,
            AssetTools.AssetStatus(_w.Svc, records: new[] { _w.Id("HcWalkBeast2") }, walk: true, from_source: new[] { AssetWalkWorld.OverridePlugin }));
    }
}
