using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>Six NPCs whose faces come from one leveled template of two members, all in one master (#1282): the
/// template chain is read a hop at a time through the door, not once per NPC.</summary>
[Trait("tier", "unit")]
public sealed class AssetWalkTemplateDoorTests : IDisposable
{
    readonly ScratchMo2 _mo2 = new("hc-walk-tmpl-");
    static readonly ModKey A = new("HcTmplA", ModType.Master);
    readonly List<FormKey> _npcs = new();
    readonly FormKey[] _members = new FormKey[2];
    readonly FormKey _list, _plain, _templated, _nested;
    readonly FormKey[] _chains = new FormKey[2];
    readonly LoadOrderService _svc;

    public AssetWalkTemplateDoorTests()
    {
        var a = new SkyrimMod(A, SkyrimRelease.SkyrimSE);
        var race = a.Races.AddNew("HcTmplRace");
        race.Flags = Race.Flag.Playable | Race.Flag.FaceGenHead;
        var lvln = a.LeveledNpcs.AddNew("HcTmplList");
        _list = lvln.FormKey;
        lvln.Entries = new();
        for (int i = 0; i < 2; i++)
        {
            var m = a.Npcs.AddNew("HcTmplMember" + i);
            m.Race.SetTo(race);
            _members[i] = m.FormKey;
            lvln.Entries.Add(new LeveledNpcEntry { Data = new LeveledNpcEntryData { Level = 1, Count = 1, Reference = m.ToLink<INpcSpawnGetter>() } });
        }
        for (int i = 0; i < 6; i++)
        {
            var n = a.Npcs.AddNew("HcTmplNpc" + i);
            n.Race.SetTo(race);
            n.Template.SetTo(lvln);
            n.Configuration.TemplateFlags = NpcConfiguration.TemplateFlag.Traits;
            _npcs.Add(n.FormKey);
        }
        // Two Traits chains, both past the template cap: 100 and 70 NPCs long.
        foreach (var (at, length) in new[] { (0, 100), (1, 70) })
        {
            Npc? below = null;
            for (int i = length - 1; i >= 0; i--)
            {
                var n = a.Npcs.AddNew($"HcTmplChain{at}_{i}");
                n.Race.SetTo(race);
                if (below is not null) { n.Template.SetTo(below); n.Configuration.TemplateFlags = NpcConfiguration.TemplateFlag.Traits; }
                below = n;
            }
            _chains[at] = below!.FormKey;
        }
        // An NPC with no template, and one whose template is reached only through it.
        var plain = a.Npcs.AddNew("HcTmplPlain");
        plain.Race.SetTo(race);
        _plain = plain.FormKey;
        var hidden = a.Npcs.AddNew("HcTmplHidden");
        hidden.Race.SetTo(race);
        var templated = a.Npcs.AddNew("HcTmplTemplated");
        templated.Race.SetTo(race);
        templated.Template.SetTo(hidden);
        templated.Configuration.TemplateFlags = NpcConfiguration.TemplateFlag.Traits;
        _templated = templated.FormKey;
        // An NPC templated on an NPC templated on the leveled list: the members are three hops down.
        var middle = a.Npcs.AddNew("HcTmplMiddle");
        middle.Race.SetTo(race);
        middle.Template.SetTo(lvln);
        middle.Configuration.TemplateFlags = NpcConfiguration.TemplateFlag.Traits;
        var nested = a.Npcs.AddNew("HcTmplNested");
        nested.Race.SetTo(race);
        nested.Template.SetTo(middle);
        nested.Configuration.TemplateFlags = NpcConfiguration.TemplateFlag.Traits;
        _nested = nested.FormKey;
        a.BeginWrite.ToPath(_mo2.InMod("TmplA", A)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        var name = A.FileName.String;
        _mo2.Profile(name + "\r\n", "*" + name + "\r\n", "+TmplA\r\n");
        _svc = _mo2.Open();
    }

    public void Dispose() { _svc.Dispose(); _mo2.Delete(); }

    AssetWalkData Walk() => Walk(_npcs);

    AssetWalkData Walk(IReadOnlyList<FormKey> seeds) => _svc.AssetWalk(Array.Empty<string>(), seeds, new[] { SourcePoles.Winner }, null);

    /// <summary>NPC group walks of a second walk over <paramref name="seeds"/>, the first paying the walk's one-time reads.</summary>
    int NpcReads(IReadOnlyList<FormKey> seeds)
    {
        Walk(seeds);
        var before = _svc.Counters.GroupReads.GetValueOrDefault((A.FileName.String, "Npcs"));
        Walk(seeds);
        return _svc.Counters.GroupReads.GetValueOrDefault((A.FileName.String, "Npcs")) - before;
    }

    /// <summary>The NPCs' template list and its members are read in one round per hop, with no single-key read; on
    /// the per-key read every NPC re-read the list and both members. The face still comes from both members.</summary>
    [Fact]
    public void ATemplateSharedByManyNpcsIsReadOncePerHop()
    {
        Walk();   // the reverse index and the voice cache's own reads, paid once
        var before = new Dictionary<(string Plugin, string Group), int>(_svc.Counters.GroupReads);
        var seeks = _svc.Counters.BodySeeks;
        var walk = Walk();
        var reads = _svc.Counters.GroupReads.ToDictionary(kv => kv.Key, kv => kv.Value - before.GetValueOrDefault(kv.Key));
        Assert.Equal(0, _svc.Counters.BodySeeks - seeks);
        var name = A.FileName.String;
        Assert.Equal(2, reads[(name, "LeveledNpcs")]);   // the faces' round, then the walk entering the list
        Assert.Equal(3, reads[(name, "Npcs")]);          // the seeds, the members' round, the walk entering them
        foreach (var member in _members)
            Assert.Contains(walk.Nodes, n => n.Edge == "facegen" && n.PulledBy.Contains(FormIdToken.Of(_npcs[0]))
                                             && n.Note == $"the face of template root {FormIdToken.Of(member)}, one of 2");
    }

    /// <summary>A template list the walk reads as a seed is still hopped from: its members are read in the faces'
    /// round, not one at a time when FaceRoots asks, so the NPC walks are those of the list not being a seed.</summary>
    [Fact]
    public void ATemplateListThatIsASeedHasItsMembersReadInOneRound()
        => Assert.Equal(NpcReads(_npcs), NpcReads(_npcs.Append(_list).ToList()));

    /// <summary>A Traits chain is read no further than FaceRoots follows it, so a chain of 100 reads as many NPC rounds
    /// as one of 70, both past the cap.</summary>
    [Fact]
    public void ATemplateChainIsReadNoFurtherThanTheCap()
        => Assert.Equal(NpcReads(new[] { _chains[1] }), NpcReads(new[] { _chains[0] }));

    /// <summary>A chain three hops deep is read a round per hop to its leveled list's members, not the members one at a
    /// time when FaceRoots asks: the seed, the middle NPC's round, the members' round, and the walk entering the middle
    /// NPC and then the members.</summary>
    [Fact]
    public void ANestedTemplateChainIsReadToItsLastHop() => Assert.Equal(5, NpcReads(new[] { _nested }));

    /// <summary>An NPC past the record cap is not entered, so its template is not read: the walk reads the seeds' NPC
    /// group once.</summary>
    [Fact]
    public void AnNpcPastTheRecordCapHasNoTemplateRead()
    {
        var area = _svc.AssetArea;
        var prior = area.MaxWalkRecords;
        area.MaxWalkRecords = 1;
        try
        {
            Assert.True(Walk(new[] { _plain, _templated }).Truncated);
            Assert.Equal(1, NpcReads(new[] { _plain, _templated }));
        }
        finally { area.MaxWalkRecords = prior; }
    }
}
