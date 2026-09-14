using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlGenerator;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A world holding the one record shape a forward walk used to die on: a perk whose entry-point effect
/// parses only when something reaches for it, with its EPFT parameter-type flag byte set to a value that is not
/// legal, so Mutagen throws the moment the walk enumerates its links. The plugin is written by Mutagen and then
/// patched on disk through <see cref="ProbeBytes.CorruptEpftBytes"/> — the suite's canonical unscannable-record
/// fixture, the same one the scan-lane guards use. An NPC seeds the walk and points at the corrupt perk, at a
/// sound perk, and at a race, so a walk that crosses the corrupt node must still record the other two.</summary>
public sealed class WalkUnscannableWorld : IDisposable
{
    public string Root { get; }
    public string MasterName { get; }
    public LoadOrderService Svc { get; }

    /// <summary>The NPC the walk is seeded from.</summary>
    public string SeedFid { get; }
    /// <summary>The perk Mutagen cannot parse — the boundary the walk must name.</summary>
    public string BadPerkFid { get; }

    /// <summary>An NPC whose ACBS subrecord is cut short, so the template flags this walk's template lane reads
    /// throw where the link read does not — the other lazy parse on the same node.</summary>
    public string BrokenNpcFid { get; }
    /// <summary>The NPC whose Template points at <see cref="BrokenNpcFid"/>.</summary>
    public string TemplateSeedFid { get; }

    public const string GoodPerkEditorId = "HcWalkUnscanGoodPerk";
    public const string RaceEditorId = "HcWalkUnscanRace";

    static int Find(byte[] b, string sig, int from)
    {
        for (int i = from; i + 4 <= b.Length; i++)
            if (b[i] == sig[0] && b[i + 1] == sig[1] && b[i + 2] == sig[2] && b[i + 3] == sig[3]) return i;
        return -1;
    }

    /// <summary>Cut one NPC's ACBS subrecord short, so the template flags at its end read past their own
    /// subrecord. Everything else in the file stays internally consistent: the record, its group and the file all
    /// shrink with it.</summary>
    static void CutAcbs(string path, uint rawFormId)
    {
        const int Keep = 8;
        var bytes = File.ReadAllBytes(path);
        int grup = -1, rec = -1;
        for (int i = 0; i + 24 <= bytes.Length && grup < 0; i++)
            if (bytes[i] == 'G' && bytes[i + 1] == 'R' && bytes[i + 2] == 'U' && bytes[i + 3] == 'P'
                && bytes[i + 8] == 'N' && bytes[i + 9] == 'P' && bytes[i + 10] == 'C' && bytes[i + 11] == '_') grup = i;
        Assert.True(grup >= 0, "NPC_ GRUP not found");
        for (int i = grup + 24; i + 24 <= bytes.Length && rec < 0; i++)
            if (bytes[i] == 'N' && bytes[i + 1] == 'P' && bytes[i + 2] == 'C' && bytes[i + 3] == '_'
                && BitConverter.ToUInt32(bytes, i + 12) == rawFormId) rec = i;
        Assert.True(rec >= 0, "the NPC_ record to cut was not found");
        int sub = Find(bytes, "ACBS", rec + 24);
        Assert.True(sub > rec, "ACBS not found in that record");
        int len = BitConverter.ToUInt16(bytes, sub + 4);
        Assert.True(len > Keep, $"ACBS is {len} bytes — nothing to cut");
        int cut = len - Keep;
        BitConverter.GetBytes((ushort)Keep).CopyTo(bytes, sub + 4);
        BitConverter.GetBytes(BitConverter.ToUInt32(bytes, rec + 4) - (uint)cut).CopyTo(bytes, rec + 4);
        BitConverter.GetBytes(BitConverter.ToUInt32(bytes, grup + 4) - (uint)cut).CopyTo(bytes, grup + 4);
        File.WriteAllBytes(path, bytes[..(sub + 6 + Keep)].Concat(bytes[(sub + 6 + len)..]).ToArray());
    }

    readonly string _priorCorpusPath;

    public WalkUnscannableWorld()
    {
        _priorCorpusPath = CorpusRulebook.CorpusPath;
        Root = Path.Combine(Path.GetTempPath(), "hc-walk-unscannable-tests-" + Guid.NewGuid().ToString("N"));
        var instance = Path.Combine(Root, "instance");
        var profiles = Path.Combine(instance, "profiles", "Default");
        var mods = Path.Combine(instance, "mods");
        foreach (var d in new[] { profiles, mods, Path.Combine(Root, "game", "Data") }) Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");

        var masterKey = new ModKey("HcWalkUnscan", ModType.Master);
        MasterName = masterKey.FileName.String;
        var master = new SkyrimMod(masterKey, SkyrimRelease.SkyrimSE);

        var race = master.Races.AddNew();
        race.EditorID = RaceEditorId;

        var goodPerk = master.Perks.AddNew();
        goodPerk.EditorID = GoodPerkEditorId;

        // The one entry-point effect in the plugin, so the byte patch below hits exactly it.
        var badPerk = master.Perks.AddNew();
        badPerk.EditorID = "HcWalkUnscanBadPerk";
        badPerk.Effects.Add(new PerkEntryPointModifyActorValue
        {
            EntryPoint = APerkEntryPointEffect.EntryType.CalculateWeaponDamage,
            ActorValue = ActorValue.OneHanded,
            Value = 1f,
            Modification = PerkEntryPointModifyActorValue.ModificationType.AddAVMult,
        });
        // A link on a field that still READS, so a seed path over it proves a chain the unreadable Effects cannot.
        badPerk.NextPerk.SetTo(goodPerk);

        var npc = master.Npcs.AddNew();
        npc.EditorID = "HcWalkUnscanSeed";
        npc.Race.SetTo(race);
        npc.Perks = new Noggog.ExtendedList<PerkPlacement>
        {
            new PerkPlacement { Perk = badPerk.ToLink(), Rank = 1 },
            new PerkPlacement { Perk = goodPerk.ToLink(), Rank = 1 },
        };

        // The template lane's own fixture: a broken NPC and the NPC that inherits from it.
        var broken = master.Npcs.AddNew();
        broken.EditorID = "HcWalkUnscanBrokenNpc";
        broken.Race.SetTo(race);
        var templateSeed = master.Npcs.AddNew();
        templateSeed.EditorID = "HcWalkUnscanTemplateSeed";
        templateSeed.Race.SetTo(race);
        templateSeed.Template.SetTo(broken);
        templateSeed.Configuration.TemplateFlags = NpcConfiguration.TemplateFlag.Stats;

        SeedFid = $"{npc.FormKey.ID:X6}:{masterKey.FileName}";
        BadPerkFid = $"{badPerk.FormKey.ID:X6}:{masterKey.FileName}";
        BrokenNpcFid = $"{broken.FormKey.ID:X6}:{masterKey.FileName}";
        TemplateSeedFid = $"{templateSeed.FormKey.ID:X6}:{masterKey.FileName}";

        var path = Path.Combine(mods, "WalkUnscanMod", MasterName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        master.BeginWrite.ToPath(path).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        Assert.Equal(1, ProbeBytes.CorruptEpftBytes(path));
        CutAcbs(path, broken.FormKey.ID);

        // The fixture must still exhibit the fault, or a green below proves nothing.
        using (var overlay = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE))
        {
            var bad = overlay.Perks.First(p => p.FormKey == badPerk.FormKey);
            Assert.ThrowsAny<Exception>(() =>
                ((Mutagen.Bethesda.Plugins.Records.IFormLinkContainerGetter)bad).EnumerateFormLinks().Count());
            var cut = overlay.Npcs.First(n => n.FormKey == broken.FormKey);
            Assert.ThrowsAny<Exception>(() => cut.Configuration.TemplateFlags);
        }

        var genDir = Path.Combine(Root, "corpus-gen");
        CorpusGenerator.GenerateAll(genDir, Path.Combine(Root, "corpus-ref"));
        CorpusRulebook.CorpusPath = Path.Combine(genDir, "corpus.json");

        File.WriteAllText(Path.Combine(profiles, "loadorder.txt"), "# header\r\n" + MasterName + "\r\n");
        File.WriteAllText(Path.Combine(profiles, "plugins.txt"), "*" + MasterName + "\r\n");
        File.WriteAllText(Path.Combine(profiles, "modlist.txt"), "# header\r\n+WalkUnscanMod\r\n");

        Svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(Root, "houseCARL.user.json")));
    }

    public void Dispose()
    {
        CorpusRulebook.CorpusPath = _priorCorpusPath;
        Svc.Dispose();
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}

public sealed class WalkUnscannableFixture : IDisposable
{
    public WalkUnscannableWorld W { get; } = new();
    public void Dispose() => W.Dispose();
}

/// <summary>A record Mutagen cannot parse is a BOUNDARY on a forward walk, the same answer the scan lanes give
/// (#725). Before, the walk entered the record, the parse threw, and the whole call came back as an internal
/// failure with nothing about which record or what to do.</summary>
[Trait("tier", "integration")]
public sealed class RecordsWalkUnscannableTests : IClassFixture<WalkUnscannableFixture>
{
    readonly WalkUnscannableWorld _w;
    public RecordsWalkUnscannableTests(WalkUnscannableFixture f) => _w = f.W;

    string Walk() => RecordsTools.Records(
        _w.Svc, formids: new[] { _w.SeedFid },
        walk: new RecordsTools.RecordsWalk { depth = 4 },
        project: new RecordsTools.RecordsProject { form = "chain" });

    [Fact]
    public void AnUnscannableNodeIsNamedAsABoundaryAndTheWalkCompletes()
    {
        var r = Walk();

        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), r);
        // The record, the exception type, one line — and the walk did not enter it.
        Assert.Contains(_w.BadPerkFid, r);
        Assert.Contains("could not be scanned (Mutagen could not parse its content) and was not entered", r);
        Assert.Contains("MalformedDataException", r);
        // …and everything else the seed points at is still reached.
        Assert.Contains(WalkUnscannableWorld.GoodPerkEditorId, r);
        Assert.Contains(WalkUnscannableWorld.RaceEditorId, r);
    }

    /// <summary>The reading forms consume the same reached set and render a body per row, so the whole call
    /// completes — the record that would not parse is reached like any other node.</summary>
    [Fact]
    public void AReadingFormOverTheSameWalkRendersItsRows()
    {
        var r = RecordsTools.Records(
            _w.Svc, formids: new[] { _w.SeedFid },
            walk: new RecordsTools.RecordsWalk { depth = 4 },
            project: new RecordsTools.RecordsProject { form = "summary" });

        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), r);
        Assert.Contains(WalkUnscannableWorld.GoodPerkEditorId, r);
        Assert.Contains(WalkUnscannableWorld.RaceEditorId, r);
        Assert.Contains(_w.BadPerkFid, r);
    }

    /// <summary>Seeded ON the record that will not parse: the seed names itself as a boundary instead of raising,
    /// and says it once for the record.</summary>
    [Fact]
    public void AnUnscannableSeedIsNamedAsItsOwnBoundary()
    {
        var r = RecordsTools.Records(
            _w.Svc, formids: new[] { _w.BadPerkFid },
            walk: new RecordsTools.RecordsWalk { depth = 4 },
            project: new RecordsTools.RecordsProject { form = "chain" });

        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), r);
        Assert.Contains(_w.BadPerkFid, r);
        Assert.True(1 == r.Split("could not be scanned").Length - 1, r);
        Assert.Contains("MalformedDataException", r);
    }

    /// <summary>The same seed under seed_paths: each path answers for itself and the call completes.</summary>
    [Fact]
    public void AnUnscannableSeedUnderSeedPathsStillAnswers()
    {
        var r = RecordsTools.Records(
            _w.Svc, formids: new[] { _w.BadPerkFid },
            walk: new RecordsTools.RecordsWalk { depth = 4, seed_paths = new[] { "Effects" } },
            project: new RecordsTools.RecordsProject { form = "chain" });

        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), r);
        Assert.Contains("PerkEntryPointModifyActorValue did not have expected parameter type flag", r);
    }

    /// <summary>A seed is not a node: a seed whose own content will not parse reaches nothing, and the count says
    /// so rather than counting the seed's own fault as a record it reached.</summary>
    [Fact]
    public void AnUnscannableSeedIsNotCountedAsAReachedNode()
    {
        var r = RecordsTools.Records(
            _w.Svc, formids: new[] { _w.BadPerkFid },
            walk: new RecordsTools.RecordsWalk { depth = 4 },
            project: new RecordsTools.RecordsProject { form = "chain" });

        Assert.Contains("0 node(s) reached", r);
        Assert.Contains("Nothing to walk from", r);
    }

    /// <summary>One path that will not parse does not throw away what another path already proved: the chain off
    /// the readable path is walked, and the unreadable one says why it gave nothing.</summary>
    [Fact]
    public void AFaultOnOneSeedPathKeepsWhatAnotherPathProved()
    {
        var r = RecordsTools.Records(
            _w.Svc, formids: new[] { _w.BadPerkFid },
            walk: new RecordsTools.RecordsWalk { depth = 4, seed_paths = new[] { "NextPerk", "Effects" } },
            project: new RecordsTools.RecordsProject { form = "chain" });

        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), r);
        Assert.Contains(WalkUnscannableWorld.GoodPerkEditorId, r);
        Assert.Contains("PerkEntryPointModifyActorValue did not have expected parameter type flag", r);
    }

    /// <summary>A path spelled wrong after one that will not parse still fails loudly on its own row — the record's
    /// fault does not silence the paths behind it.</summary>
    [Fact]
    public void AMistypedPathAfterAFaultingOneStillFailsLoudly()
    {
        var r = RecordsTools.Records(
            _w.Svc, formids: new[] { _w.BadPerkFid },
            walk: new RecordsTools.RecordsWalk { depth = 4, seed_paths = new[] { "Effects", "Efects" } },
            project: new RecordsTools.RecordsProject { form = "chain" });

        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), r);
        Assert.Contains("Efects", r);
    }

    /// <summary>The template lane reads the template flags, not the links, so it has its own way into the same
    /// fault: a chain node whose ACBS is cut short is a boundary too, and the walk still answers.</summary>
    [Fact]
    public void ATemplateWalkNamesAChainNodeThatWillNotParse()
    {
        var r = RecordsTools.Records(
            _w.Svc, formids: new[] { _w.TemplateSeedFid },
            walk: new RecordsTools.RecordsWalk { follow = "Template", seed_paths = new[] { "Template" }, depth = 4 },
            project: new RecordsTools.RecordsProject { form = "chain" });

        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), r);
        Assert.Contains(_w.BrokenNpcFid, r);
        Assert.Contains("could not be scanned", r);
    }
}
