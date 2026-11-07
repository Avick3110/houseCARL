using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>Two active plugins for the asset walk's edge and home pins and the copy-vs-asset suite (#1315). B, loaded
/// first, defines a head part X linking a head part Y, and a seed list of X; A, mastered on B, defines a second seed list linking X twice, one record per
/// edge kind (a head part, a weight-variant armor addon, a quest with a fragment file, a line voiced from a .wav, an
/// effect mesh naming a BODYTRI) and a tree of head parts under one list. An enabled files mod ships the .wav and the mesh.</summary>
public sealed class AssetWalkPinWorld : IDisposable
{
    readonly ScratchMo2 _mo2 = new("hc-walk-pin-");
    public static readonly ModKey A = ModKey.FromFileName("HcPinA.esp"), B = ModKey.FromFileName("HcPinB.esp");
    public const string ListedNif = @"meshes\hcpin\listed.nif";
    public const string ArmaNif = @"hcpin\body_1.nif";
    public const string FragmentFile = "HcPinFrag";
    public const string FxNif = @"meshes\hcpin\fx.nif";
    public const string FxTri = @"actors\character\hcpin\body.tri";
    public const string YNif = @"meshes\hcpin\y.nif";
    public LoadOrderService Svc { get; }
    public Dictionary<string, FormKey> Keys { get; } = new(StringComparer.Ordinal);
    public string Wav { get; }

    /// <summary>The order tree: HcPinOrd lists OrdA and OrdB; OrdA's extra parts are OrdA1 and OrdA2, OrdB's a null
    /// link, OrdB1 and OrdA2 again; OrdA1 links back to OrdB.</summary>
    public static readonly string[] OrdParts = { "HcPinOrdA", "HcPinOrdB", "HcPinOrdA1", "HcPinOrdA2", "HcPinOrdB1" };

    public AssetWalkPinWorld()
    {
        var b = new SkyrimMod(B, SkyrimRelease.SkyrimSE);
        var y = b.HeadParts.AddNew("HcPinY");
        y.Model = new Model { File = YNif };
        var x = b.HeadParts.AddNew("HcPinX");
        x.ExtraParts.Add(y);
        void SeedOfX(SkyrimMod m, string edid, int times)
        {
            var l = m.FormLists.AddNew(edid);
            for (int i = 0; i < times; i++) l.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(x.FormKey));
            Keys[edid] = l.FormKey;
        }
        SeedOfX(b, "HcPinSeedB", 1);
        b.BeginWrite.ToPath(_mo2.InMod("PinB", B)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        var a = new SkyrimMod(A, SkyrimRelease.SkyrimSE);
        SeedOfX(a, "HcPinSeedA", 2);

        var listed = a.HeadParts.AddNew("HcPinListed");
        listed.Model = new Model { File = ListedNif };
        Keys["HcPinListed"] = listed.FormKey;
        var arma = a.ArmorAddons.AddNew("HcPinArma");
        arma.WorldModel = new GenderedItem<Model?>(new Model { File = ArmaNif }, null);
        Keys["HcPinArma"] = arma.FormKey;
        var frag = a.Quests.AddNew("HcPinFragQ");
        frag.VirtualMachineAdapter = new QuestAdapter { FileName = FragmentFile };
        Keys["HcPinFragQ"] = frag.FormKey;
        var fx = a.Statics.AddNew("HcPinFx");
        fx.Model = new Model { File = FxNif };
        Keys["HcPinFx"] = fx.FormKey;

        var race = a.Races.AddNew("HcPinRace");
        var voice = a.VoiceTypes.AddNew("HcPinVoice");
        var speaker = a.Npcs.AddNew("HcPinSpk");
        speaker.Race.SetTo(race);
        speaker.Voice.SetTo(voice);
        var topic = a.DialogTopics.AddNew("HcPinTopic");
        topic.Quest.SetTo(a.Quests.AddNew("HcPinVQ"));
        var line = new DialogResponses(a.GetNextFormKey(), SkyrimRelease.SkyrimSE);
        line.Responses.Add(new DialogResponse { ResponseNumber = 1, Text = "HcPinLineWav" });
        line.Speaker.SetTo(speaker);
        topic.Responses.Add(line);
        Keys["HcPinLineWav"] = line.FormKey;
        Wav = VoicePath.For(line.FormKey, "HcPinVoice", "HcPinVQ", "HcPinTopic", 1, VoiceFile.Fuz)[..^4] + ".wav";

        HeadPart Part(string edid)
        {
            var p = a.HeadParts.AddNew(edid);
            p.Model = new Model { File = $@"meshes\hcpin\ord\{edid}.nif" };
            Keys[edid] = p.FormKey;
            return p;
        }
        var parts = OrdParts.ToDictionary(e => e, Part);
        void Links(string from, params string?[] to)
        {
            foreach (var t in to) parts[from].ExtraParts.Add(new FormLink<IHeadPartGetter>(t is null ? FormKey.Null : parts[t].FormKey));
        }
        Links("HcPinOrdA", "HcPinOrdA1", "HcPinOrdA2");
        Links("HcPinOrdB", null, "HcPinOrdB1", "HcPinOrdA2");
        Links("HcPinOrdA1", "HcPinOrdB");
        var ord = a.FormLists.AddNew("HcPinOrd");
        foreach (var e in new[] { "HcPinOrdA", "HcPinOrdB" }) ord.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(parts[e].FormKey));
        Keys["HcPinOrd"] = ord.FormKey;
        a.BeginWrite.ToPath(_mo2.InMod("PinA", A)).WithLoadOrder(new ISkyrimModGetter[] { b, a }).Write();

        var files = Path.Combine(_mo2.ModsDir, "PinFiles");
        NifSourceLaneInstance.Loose(files, Wav, new byte[] { 1 });
        NifSourceLaneInstance.Loose(files, FxNif, AssetWalkWorld.FxMesh(@"textures\hcpin\fx.dds", FxTri));

        var names = new[] { B, A }.Select(k => k.FileName.String).ToList();
        _mo2.Profile(string.Join("\r\n", names) + "\r\n", string.Join("", names.Select(n => "*" + n + "\r\n")), "+PinFiles\r\n+PinA\r\n+PinB\r\n");
        Svc = _mo2.Open();
    }

    public void Dispose() { Svc.Dispose(); _mo2.Delete(); }
}

/// <summary>The edge kinds no other test names, and the per-seed home of a walk seeded in two plugins (#1315): each
/// pins today's asset walk, so a later change to its loop shows here.</summary>
[Trait("tier", "unit")]
public sealed class AssetWalkPinTests : IClassFixture<AssetWalkPinWorld>
{
    readonly AssetWalkPinWorld _w;
    public AssetWalkPinTests(AssetWalkPinWorld w) => _w = w;

    AssetWalkData Walk(params string[] seeds) => _w.Svc.AssetWalk(Array.Empty<string>(), seeds.Select(s => _w.Keys[s]).ToList(), null, null);

    static AssetWalkNode Node(AssetWalkData d, string path)
        => d.Nodes.Single(n => string.Equals(n.Path, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>A path a record lists in its own fields is a 'listed' edge, pulled by the record.</summary>
    [Fact]
    public void ARecordsOwnPathIsListed()
    {
        var n = Node(Walk("HcPinListed"), AssetWalkPinWorld.ListedNif);
        Assert.Equal("listed", n.Edge);
        Assert.Contains("HcPinListed", n.PulledBy);
    }

    /// <summary>An armor addon's weight-variant model is an 'inferred' edge: Mutagen infers the _0 mesh from the _1 one.</summary>
    [Fact]
    public void AWeightVariantModelIsInferred()
    {
        var n = Node(Walk("HcPinArma"), @"meshes\hcpin\body_0.nif");
        Assert.Equal("inferred", n.Edge);
        Assert.Contains("HcPinArma", n.PulledBy);
    }

    /// <summary>A script adapter's fragment file name is a 'fragment' edge to its .pex, with the note saying why.</summary>
    [Fact]
    public void AnAdaptersFileNameIsAFragment()
    {
        var n = Node(Walk("HcPinFragQ"), @"Scripts\HcPinFrag.pex");
        Assert.Equal("fragment", n.Edge);
        Assert.Contains("Mutagen infers no link for it", n.Note);
    }

    /// <summary>A line whose only voice file ships as a .wav is a 'voice' edge to the .wav, which plays, and a 'lip' edge.</summary>
    [Fact]
    public void ALineVoicedFromAWavReachesIt()
    {
        var d = Walk("HcPinLineWav");
        var wav = Node(d, _w.Wav);
        Assert.Equal("voice", wav.Edge);
        Assert.True(wav.Keyed);
        Assert.Equal("no .fuz ships; this line plays from this file", wav.Note);
        Assert.Equal("lip", Node(d, _w.Wav[..^4] + ".lip").Edge);
        Assert.DoesNotContain(d.Nodes, n => n.Path.EndsWith(".fuz", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A walked record's mesh that names a BODYTRI reaches it, under meshes\, as a 'bodytri' edge pulled by the mesh.</summary>
    [Fact]
    public void AWalkedMeshReachesItsBodyTri()
    {
        var n = Node(Walk("HcPinFx"), @"meshes\" + AssetWalkPinWorld.FxTri);
        Assert.Equal("bodytri", n.Edge);
        Assert.Equal(AssetWalkPinWorld.FxNif, n.PulledBy, ignoreCase: true);
    }

    /// <summary>Two seeds read through the winner, one in each plugin, both linking B's X: A's two links are boundaries
    /// into B, counted per link, and X is entered through B's seed, whose home B is, so X's own link to Y is walked and
    /// is no third boundary.</summary>
    [Fact]
    public void EachSeedsHomeIsItsOwnPlugin()
    {
        var d = Walk("HcPinSeedA", "HcPinSeedB");
        Assert.Contains(d.Notes, n => n.Contains("boundaries") && n.Contains(AssetWalkPinWorld.B.FileName + " ×2."));
        Assert.Contains("HcPinY", Node(d, AssetWalkPinWorld.YNif).PulledBy);
    }
}
