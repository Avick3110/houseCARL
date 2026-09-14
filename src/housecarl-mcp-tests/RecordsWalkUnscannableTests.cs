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

    public const string GoodPerkEditorId = "HcWalkUnscanGoodPerk";
    public const string RaceEditorId = "HcWalkUnscanRace";

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

        var npc = master.Npcs.AddNew();
        npc.EditorID = "HcWalkUnscanSeed";
        npc.Race.SetTo(race);
        npc.Perks = new Noggog.ExtendedList<PerkPlacement>
        {
            new PerkPlacement { Perk = badPerk.ToLink(), Rank = 1 },
            new PerkPlacement { Perk = goodPerk.ToLink(), Rank = 1 },
        };

        SeedFid = $"{npc.FormKey.ID:X6}:{masterKey.FileName}";
        BadPerkFid = $"{badPerk.FormKey.ID:X6}:{masterKey.FileName}";

        var path = Path.Combine(mods, "WalkUnscanMod", MasterName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        master.BeginWrite.ToPath(path).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        Assert.Equal(1, ProbeBytes.CorruptEpftBytes(path));

        // The fixture must still exhibit the fault, or a green below proves nothing.
        using (var overlay = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE))
        {
            var bad = overlay.Perks.First(p => p.FormKey == badPerk.FormKey);
            Assert.ThrowsAny<Exception>(() =>
                ((Mutagen.Bethesda.Plugins.Records.IFormLinkContainerGetter)bad).EnumerateFormLinks().Count());
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

    /// <summary>The reading forms consume the same reached set, so they complete too — the crash was in the walk,
    /// not in the render.</summary>
    [Fact]
    public void AReadingFormOverTheSameWalkCompletesToo()
    {
        var r = RecordsTools.Records(
            _w.Svc, formids: new[] { _w.SeedFid },
            walk: new RecordsTools.RecordsWalk { depth = 4 },
            project: new RecordsTools.RecordsProject { form = "summary" }, counts_only: true);

        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), r);
    }
}
