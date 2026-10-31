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
    readonly LoadOrderService _svc;

    public AssetWalkTemplateDoorTests()
    {
        var a = new SkyrimMod(A, SkyrimRelease.SkyrimSE);
        var race = a.Races.AddNew("HcTmplRace");
        race.Flags = Race.Flag.Playable | Race.Flag.FaceGenHead;
        var lvln = a.LeveledNpcs.AddNew("HcTmplList");
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
        a.BeginWrite.ToPath(_mo2.InMod("TmplA", A)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        var name = A.FileName.String;
        _mo2.Profile(name + "\r\n", "*" + name + "\r\n", "+TmplA\r\n");
        _svc = _mo2.Open();
    }

    public void Dispose() { _svc.Dispose(); _mo2.Delete(); }

    AssetWalkData Walk() => _svc.AssetWalk(Array.Empty<string>(), _npcs, new[] { SourcePoles.Winner }, null);

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
}
