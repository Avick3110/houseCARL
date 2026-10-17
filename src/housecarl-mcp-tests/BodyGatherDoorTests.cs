using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A write gather reads its (plugin, key) pairs through the door: each (plugin, group) once, faults in
/// declaration order, and what a faulted plugin yielded kept.</summary>
[Trait("tier", "integration")]
public sealed class BodyGatherDoorTests : IDisposable
{
    readonly ScratchMo2 _mo2 = new("hc-gather-door-");
    readonly ModKey[] _mods = { new("HcGdA", ModType.Master), new("HcGdB", ModType.Plugin), new("HcGdC", ModType.Plugin) };
    readonly Dictionary<ModKey, (FormKey[] Npcs, FormKey[] Weapons)> _keys = new();

    public BodyGatherDoorTests()
    {
        var loadorder = ""; var plugins = ""; var modlist = "";
        foreach (var key in _mods)
        {
            var m = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
            var npcs = Enumerable.Range(0, 2).Select(i => m.Npcs.AddNew("HcGdN" + key.Name + i).FormKey).ToArray();
            var weapons = Enumerable.Range(0, 2).Select(i => m.Weapons.AddNew("HcGdW" + key.Name + i).FormKey).ToArray();
            _keys[key] = (npcs, weapons);
            m.BeginWrite.ToPath(_mo2.InMod(key.Name, key)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
            loadorder += key.FileName + "\r\n"; plugins += "*" + key.FileName + "\r\n"; modlist = "+" + key.Name + "\r\n" + modlist;
        }
        _mo2.Profile(loadorder, plugins, modlist);
    }

    public void Dispose() => _mo2.Delete();

    string Name(int i) => _mods[i].FileName.String;

    /// <summary>0xFF over the first record header inside the plugin's <paramref name="sig"/> group, size and write time kept, so walking that group throws.</summary>
    void Break(int i, string sig) => Break(Path.Combine(_mo2.ModsDir, _mods[i].Name, Name(i)), sig);

    /// <summary>0xFF over the first record header inside the <paramref name="sig"/> group of the plugin at <paramref name="path"/>.</summary>
    static void Break(string path, string sig)
    {
        var mtime = File.GetLastWriteTimeUtc(path);
        var bytes = File.ReadAllBytes(path);
        var tag = System.Text.Encoding.ASCII.GetBytes(sig);
        int at = -1;
        for (int j = 0; j + 12 <= bytes.Length && at < 0; j++)
            if (bytes.AsSpan(j, 4).SequenceEqual("GRUP"u8) && bytes.AsSpan(j + 8, 4).SequenceEqual(tag)) at = j;
        Assert.True(at >= 0, $"the plugin has no {sig} group");
        bytes.AsSpan(at + 24, 24).Fill(0xFF);
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, mtime);
    }

    void BreakWeapons(int i) => Break(i, "WEAP");

    static string Answer(Func<IMajorRecordGetter?> read)
    {
        try { return read() is { } r ? $"{r.FormKey}|{r.EditorID}" : "(absent)"; }
        catch (Exception ex) { return ex.GetType().Name + ": " + ex.Message; }
    }

    [Fact]
    public void FaultsComeInDeclarationOrderAndWhatAFaultedPluginYieldedIsKept()
    {
        using var svc = _mo2.Open();
        var pin = svc.CapturePin();
        BreakWeapons(0);
        BreakWeapons(2);
        using var session = pin.Resolver.OpenSession();
        var gather = new BodyGather(pin.View, session);
        foreach (var i in new[] { 2, 1, 0 })                 // declared against load order
            foreach (var fk in _keys[_mods[i]].Npcs.Concat(_keys[_mods[i]].Weapons)) gather.Want(Name(i), fk);
        gather.Gather();

        Assert.Equal(new[] { Name(2), Name(0) }, gather.Faults.Keys);

        // Held bodies answer with no seek: every body of the clean plugin, and the NPCs the faulted plugins yielded.
        var seeks = svc.Counters.BodySeeks;
        foreach (var i in new[] { 0, 1, 2 })
            foreach (var fk in _keys[_mods[i]].Npcs) Assert.Equal(Name(i), gather.Body(Name(i), fk)!.FormKey.ModKey.FileName.String);
        foreach (var fk in _keys[_mods[1]].Weapons) Assert.NotNull(gather.Body(Name(1), fk));
        Assert.Equal(seeks, svc.Counters.BodySeeks);

        // Every pair answers what the one-at-a-time fetch answers, the broken weapons' faults included.
        using var plain = pin.Resolver.OpenSession();
        foreach (var i in new[] { 0, 1, 2 })
            foreach (var fk in _keys[_mods[i]].Npcs.Concat(_keys[_mods[i]].Weapons))
                Assert.Equal(Answer(() => pin.View.GetRecord(plain, Name(i), fk)), Answer(() => gather.Body(Name(i), fk)));
        var broken = _keys[_mods[0]].Weapons[0];
        Assert.False(Answer(() => gather.Body(Name(0), broken)).StartsWith(broken.ToString()), "the broken weapon still read");
    }

    [Fact]
    public void AWriteGatherReadsEachPluginGroupOnce()
    {
        using var svc = _mo2.Open();
        var pin = svc.CapturePin();
        var reads = GroupReadDelta.Of(svc, () =>
        {
            using var session = pin.Resolver.OpenSession();
            var gather = new BodyGather(pin.View, session);
            foreach (var i in new[] { 1, 0 })
                foreach (var fk in _keys[_mods[i]].Npcs.Concat(_keys[_mods[i]].Weapons)) gather.Want(Name(i), fk);
            gather.Gather();
            return gather.Body(Name(1), _keys[_mods[1]].Weapons[1])?.EditorID ?? "absent";
        }, "HcGdWHcGdB1");
        Assert.Equal(new Dictionary<(string, string), int>
        {
            [(Name(0), "Npcs")] = 1, [(Name(0), "Weapons")] = 1, [(Name(1), "Npcs")] = 1, [(Name(1), "Weapons")] = 1,
        }, reads);
    }

    /// <summary>A broken NPC_ group sorts before WEAP by group name; the plugin's weapons still come back and the plugin is named.</summary>
    [Fact]
    public void WinnerBodiesKeepTheGroupsAfterABrokenOne()
    {
        using var svc = _mo2.Open();
        var pin = svc.CapturePin();
        Break(1, "NPC_");
        using var session = pin.Resolver.OpenSession();
        var (npcs, weapons) = _keys[_mods[1]];
        var bodies = WinnerBodies.For(pin.View, session, weapons.Concat(npcs).ToList(), out var faults);
        Assert.Equal(new[] { Name(1) }, faults.Keys);
        Assert.Equal(weapons.Order(), bodies.Keys.Order());
    }

    /// <summary>A where= scan over a plugin with a broken NPC_ group still matches its weapons and names the plugin.</summary>
    [Fact]
    public void AWhereScanMatchesTheHealthyGroupsOfAPluginWithABrokenOne()
    {
        using var svc = _mo2.Open();
        var (npcs, weapons) = _keys[_mods[1]];
        var ids = npcs.Concat(weapons).Select(ScratchMo2.Fid).ToArray();
        Assert.Contains("scan: 2 matches", RecordsTools.Records(svc, formids: ids, where: new[] { "editorid startswith HcGdW" }));
        Break(1, "NPC_");
        var r = RecordsTools.Records(svc, formids: ids, where: new[] { "editorid startswith HcGdW" });
        Assert.True(r.Contains("scan: 2 matches") && r.Contains("HcGdWHcGdB0") && r.Contains("HcGdWHcGdB1")
                    && r.Contains($"coverage gap: 1 plugin(s) could not be read, so any record they win is missing from this answer: could not finish reading '{Name(1)}'"), r);
    }

    /// <summary>formids= with conflicts_only and no where=, over an overriding plugin with a broken NPC_ group: its
    /// weapons answer and only the broken group's keys are named unscannable.</summary>
    [Fact]
    public void AFormidUniverseJudgesTheHealthyGroupsOfAPluginWithABrokenOne()
    {
        var a = _mods[0];
        var o = new ModKey("HcGdO", ModType.Plugin);
        var om = new SkyrimMod(o, SkyrimRelease.SkyrimSE);
        var (npcs, weapons) = _keys[a];
        for (int i = 0; i < 2; i++)
        {
            om.Npcs.Add(new Npc(npcs[i], SkyrimRelease.SkyrimSE) { EditorID = "HcGdN" + a.Name + i });
            om.Weapons.Add(new Weapon(weapons[i], SkyrimRelease.SkyrimSE) { EditorID = "HcGdW" + a.Name + i });
        }
        var aPath = Path.Combine(_mo2.ModsDir, a.Name, Name(0));
        using (var aMod = SkyrimMod.CreateFromBinaryOverlay(aPath, SkyrimRelease.SkyrimSE))
            om.BeginWrite.ToPath(_mo2.InMod(o.Name, o)).WithLoadOrder(new ISkyrimModGetter[] { aMod }).Write();
        var all = _mods.Append(o).ToArray();
        _mo2.Profile(string.Concat(all.Select(k => k.FileName + "\r\n")), string.Concat(all.Select(k => "*" + k.FileName + "\r\n")),
                     string.Concat(Enumerable.Reverse(all).Select(k => "+" + k.Name + "\r\n")));
        using var svc = _mo2.Open();
        var ids = npcs.Concat(weapons).Select(ScratchMo2.Fid).ToArray();
        Assert.Contains("scan: 4 matches", RecordsTools.Records(svc, formids: ids, conflicts_only: true));
        Break(Path.Combine(_mo2.ModsDir, o.Name, o.FileName.String), "NPC_");
        var r = RecordsTools.Records(svc, formids: ids, conflicts_only: true);
        Assert.True(r.Contains("scan: 2 matches") && r.Contains("HcGdWHcGdA0") && r.Contains("HcGdWHcGdA1")
                    && r.Contains("2 record instance(s) could not be scanned"), r);
    }

    /// <summary>With a per-group hook, each group's bodies are dropped once it is handled: inside every group's hook the
    /// gather holds exactly that group's asked bodies, never an earlier group's.</summary>
    [Fact]
    public void APerGroupGatherHoldsOnlyTheGroupBeingHandled()
    {
        using var svc = _mo2.Open();
        var pin = svc.CapturePin();
        using var session = pin.Resolver.OpenSession();
        var gather = new BodyGather(pin.View, session);
        foreach (var i in new[] { 0, 1, 2 })
            foreach (var fk in _keys[_mods[i]].Npcs.Concat(_keys[_mods[i]].Weapons)) gather.Want(Name(i), fk);
        int groups = 0;
        gather.Gather(asked =>
        {
            groups++;
            var held = new Dictionary<FormKey, IMajorRecordGetter>();
            gather.CopyInto(held);
            Assert.Equal(asked.OrderBy(k => k.ToString()), held.Keys.OrderBy(k => k.ToString()));
        });
        Assert.Equal(6, groups);
        var after = new Dictionary<FormKey, IMajorRecordGetter>();
        gather.CopyInto(after);
        Assert.Empty(after);
    }

    /// <summary>A per-group gather dropped its bodies, so a later Body for a declared key reads the record, never null.</summary>
    [Fact]
    public void BodyAfterAPerGroupGatherStillReadsTheRecord()
    {
        using var svc = _mo2.Open();
        var pin = svc.CapturePin();
        using var session = pin.Resolver.OpenSession();
        var gather = new BodyGather(pin.View, session);
        var fk = _keys[_mods[1]].Weapons[0];
        gather.Want(Name(1), fk);
        gather.Gather(_ => { });
        Assert.Equal(pin.View.GetRecord(session, Name(1), fk)!.EditorID, gather.Body(Name(1), fk)?.EditorID);
    }
}
