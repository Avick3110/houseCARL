using HousecarlCore;
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

    /// <summary>0xFF over the first record header inside the plugin's WEAP group, so walking that group throws.</summary>
    void BreakWeapons(int i)
    {
        var path = Path.Combine(_mo2.ModsDir, _mods[i].Name, Name(i));
        var bytes = File.ReadAllBytes(path);
        int at = -1;
        for (int j = 0; j + 12 <= bytes.Length && at < 0; j++)
            if (bytes.AsSpan(j, 4).SequenceEqual("GRUP"u8) && bytes.AsSpan(j + 8, 4).SequenceEqual("WEAP"u8)) at = j;
        Assert.True(at >= 0, "the plugin has no WEAP group");
        bytes.AsSpan(at + 24, 24).Fill(0xFF);
        File.WriteAllBytes(path, bytes);
    }

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
}
