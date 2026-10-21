using HousecarlCore;
using HousecarlGenerator;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>Ruling F: a plugin that changes on disk after the call's index was built refuses the whole call, naming
/// the plugin; the retry answers the new state. A master defines 40 weapons at Damage 10; HcMcA.esp overrides the
/// first 20 at 20, HcMcB.esp the last 20 at 30; their rewrites carry 99 and 77.</summary>
[Trait("tier", "integration")]
public sealed class MidCallChangeTests : IDisposable
{
    const string MasterName = "HcMcMaster.esm", AName = "HcMcA.esp", BName = "HcMcB.esp", PatchName = "HcMcPatch.esp";

    readonly string _dir = Path.Combine(Path.GetTempPath(), "hc-midcall-" + Guid.NewGuid().ToString("N"));
    readonly string _master, _a, _b, _aNew, _bNew;
    readonly List<FormKey> _weapons = new();
    readonly SkyrimMod _masterMod;

    public MidCallChangeTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "new"));
        _master = Path.Combine(_dir, MasterName); _a = Path.Combine(_dir, AName); _b = Path.Combine(_dir, BName);
        _aNew = Path.Combine(_dir, "new", AName); _bNew = Path.Combine(_dir, "new", BName);
        _masterMod = new SkyrimMod(ModKey.FromFileName(MasterName), SkyrimRelease.SkyrimSE);
        for (int i = 0; i < 40; i++)
        {
            var w = _masterMod.Weapons.AddNew(); w.EditorID = $"HcMcW{i:D2}";
            w.BasicStats = new WeaponBasicStats { Damage = 10, Weight = 1 };
            _weapons.Add(w.FormKey);
        }
        _masterMod.BeginWrite.ToPath(_master).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        Override(_a, AName, 0, 20); Override(_aNew, AName, 0, 99);
        Override(_b, BName, 20, 30); Override(_bNew, BName, 20, 77);
    }

    void Override(string path, string name, int from, ushort damage)
    {
        var mod = new SkyrimMod(ModKey.FromFileName(name), SkyrimRelease.SkyrimSE);
        foreach (var w in _masterMod.Weapons.Skip(from).Take(20))
            ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(mod, w)).BasicStats = new WeaponBasicStats { Damage = damage, Weight = 1 };
        mod.BeginWrite.ToPath(path).WithLoadOrder(new ISkyrimModGetter[] { _masterMod }).Write();
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* temp cleanup best-effort */ } }

    /// <summary>Another program saves <paramref name="src"/> over <paramref name="dst"/>: new bytes, a new time.</summary>
    static void Save(string src, string dst)
    {
        File.Copy(src, dst, overwrite: true);
        File.SetLastWriteTimeUtc(dst, DateTime.UtcNow.AddSeconds(5));
    }

    LoadOrderService Service(out LoadOrderResolver resolver)
    {
        resolver = LoadOrderResolver.Build(new[] { _master, _a, _b });
        return LoadOrderService.ForGuard(resolver, new UserConfigStore(Path.Combine(_dir, "user.json")));
    }

    string[] Fids => _weapons.Select(fk => $"{fk.ID:X6}:{fk.ModKey.FileName}").ToArray();

    static readonly RecordsTools.RecordsProject Damage = new() { form = "fields", fields = new[] { "BasicStats.Damage" } };

    /// <summary>Run <paramref name="call"/> with <paramref name="onCheck"/> seeing each file's stamp check, in order.</summary>
    static T During<T>(Action<string> onCheck, Func<T> call)
    {
        MidCallChange.BeforeCheck.Value = onCheck;
        try { return call(); }
        finally { MidCallChange.BeforeCheck.Value = null; }
    }

    [Fact]
    public void AChangeBeforeTheFirstOpenRefusesTheCall()
    {
        var svc = Service(out var resolver);
        using var _ = resolver;
        bool saved = false;
        var answer = During(name => { if (!saved && name == AName) { Save(_aNew, _a); saved = true; } },
                            () => RecordsTools.Records(svc, formids: Fids, project: Damage));
        Assert.True(saved);
        Assert.StartsWith("error:", answer);
        Assert.Contains($"'{AName}' changed on disk during this call", answer);
        Assert.DoesNotContain("99", answer);

        var retry = RecordsTools.Records(svc, formids: Fids, project: Damage);   // the next call's door rebuilds
        Assert.DoesNotContain("error:", retry);
        Assert.Contains("99", retry);
    }

    [Fact]
    public void AChangeBetweenTwoGroupsRefusesTheCall()
    {
        var svc = Service(out var resolver);
        using var _ = resolver;
        var seen = new List<string>();
        var answer = During(name =>
            {
                seen.Add(name);
                if (name == BName && seen.Count(n => n == BName) == 1) Save(_bNew, _b);
            },
            () => RecordsTools.Records(svc, types: new[] { "WEAP" }, where: new[] { "BasicStats.Damage >= 0" }, project: Damage));
        Assert.True(seen.IndexOf(BName) > seen.IndexOf(AName) && seen.IndexOf(AName) >= 0, string.Join(",", seen));
        Assert.StartsWith("error:", answer);
        Assert.Contains($"'{BName}' changed on disk during this call", answer);
        Assert.DoesNotContain("77", answer);
    }

    [Fact]
    public void AChangeDuringAWriteRefusesItAndLeavesTheOtherSaveInPlace()
    {
        using var resolver = LoadOrderResolver.Build(new[] { _master, _a, _b });
        var patch = Path.Combine(_dir, "out", PatchName);
        Directory.CreateDirectory(Path.GetDirectoryName(patch)!);
        WritePatchBuilder.PatchEdit Edit(int i, string value) => new()
            { Target = _weapons[i], Path = new[] { "BasicStats", "Weight" }, Verb = "Set", Value = value };
        Assert.True(WritePatchBuilder.Apply(resolver, TestCorpus.Rulebook, new[] { Edit(0, "3") }, patch, extend: false).Success);

        // The other program's save: the same patch with a different record set.
        var other = Path.Combine(_dir, "new", PatchName);
        Assert.True(WritePatchBuilder.Apply(resolver, TestCorpus.Rulebook, new[] { Edit(5, "4") }, other, extend: false).Success);
        var otherBytes = File.ReadAllBytes(other);

        int sightings = 0;
        WritePatchBuilder.PatchOutcome o;
        using (MidCallChange.Begin())                   // the tool guard's call mark
            o = During(name => { if (name == PatchName && ++sightings == 2) Save(other, patch); },   // 1 the parse, 2 the commit
                       () => WritePatchBuilder.Apply(resolver, TestCorpus.Rulebook, new[] { Edit(1, "5") }, patch, extend: true));
        Assert.Equal(2, sightings);
        Assert.False(o.Success, "the write committed over a file saved during it");
        Assert.Contains($"'{PatchName}' changed on disk during this call", o.Error);
        Assert.Equal(otherBytes, File.ReadAllBytes(patch));
    }

    /// <summary>A rewrite that keeps the last-write time but not the length rebuilds that plugin's reverse-index
    /// partition, so references= stops answering from the old file's edges.</summary>
    [Fact]
    public void ASameTimeNewLengthRewriteRebuildsTheReverseIndexPartition()
    {
        SkyrimMod World(bool linked, out FormKey mgef)
        {
            var m = new SkyrimMod(ModKey.FromFileName("HcMcGap.esm"), SkyrimRelease.SkyrimSE);
            var e = m.MagicEffects.AddNew(); e.EditorID = "HcMcGapMgef";
            var spel = m.Spells.AddNew(); spel.EditorID = "HcMcGapSpell";
            if (linked) spel.Effects.Add(new Effect { BaseEffect = e.ToNullableLink(), Data = new EffectData { Magnitude = 1 } });
            mgef = e.FormKey;
            return m;
        }
        var path = Path.Combine(_dir, "HcMcGap.esm");
        World(true, out var mgef).BeginWrite.ToPath(path).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        using var resolver = LoadOrderResolver.Build(new[] { path });
        var v1 = resolver.Capture(); v1.EnsureReverseIndex();
        Assert.True(v1.ReverseIndex!.HasAnyReferencer(mgef));

        var t = File.GetLastWriteTimeUtc(path); long len0 = new FileInfo(path).Length;
        World(false, out _).BeginWrite.ToPath(path).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        File.SetLastWriteTimeUtc(path, t);
        Assert.NotEqual(len0, new FileInfo(path).Length);

        Assert.True(resolver.RefreshIfStale());
        var v2 = resolver.Capture(); v2.EnsureReverseIndex();
        Assert.False(v2.ReverseIndex!.HasAnyReferencer(mgef), "the reverse index kept the old file's edge");
    }

    /// <summary>Two calls overlap: the later one's door rebuilt a saved plugin's reverse-index partition, so the earlier
    /// call, still on the old build, must not answer from the new edges; its refresh refuses instead.</summary>
    [Fact]
    public void AnOlderCallDoesNotHoldAPartitionANewerCallRebuilt()
    {
        var path = Path.Combine(_dir, "HcMcRev.esm");
        void World(bool linked)
        {
            var m = new SkyrimMod(ModKey.FromFileName("HcMcRev.esm"), SkyrimRelease.SkyrimSE);
            var e = m.MagicEffects.AddNew(); e.EditorID = "HcMcRevMgef";
            var spel = m.Spells.AddNew(); spel.EditorID = "HcMcRevSpell";
            if (linked) spel.Effects.Add(new Effect { BaseEffect = e.ToNullableLink(), Data = new EffectData { Magnitude = 1 } });
            m.BeginWrite.ToPath(path).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        }
        World(false);
        using var resolver = LoadOrderResolver.Build(new[] { path });
        var older = resolver.Capture();                 // call A's door, before the save
        World(true); File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
        Assert.True(resolver.RefreshIfStale());         // call B's door
        resolver.Capture().EnsureReverseIndex();
        var ex = Assert.Throws<LoadOrderChangedMidCallException>(() => older.EnsureReverseIndex());
        Assert.Equal("HcMcRev.esm", ex.Plugin);
    }

    /// <summary>The reverse index is warm; a referencing plugin is saved after the call's build and before its refresh,
    /// so its held partition still matches the build stamp but not the file: the refresh refuses rather than answer
    /// the old edges.</summary>
    [Fact]
    public void AHeldPartitionWhosePluginIsSavedMidCallRefuses()
    {
        var path = Path.Combine(_dir, "HcMcHeld.esm");
        void World(bool linked)
        {
            var m = new SkyrimMod(ModKey.FromFileName("HcMcHeld.esm"), SkyrimRelease.SkyrimSE);
            var e = m.MagicEffects.AddNew(); e.EditorID = "HcMcHeldMgef";
            var spel = m.Spells.AddNew(); spel.EditorID = "HcMcHeldSpell";
            if (linked) spel.Effects.Add(new Effect { BaseEffect = e.ToNullableLink(), Data = new EffectData { Magnitude = 1 } });
            m.BeginWrite.ToPath(path).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        }
        World(true);
        using var resolver = LoadOrderResolver.Build(new[] { path });
        resolver.Capture().EnsureReverseIndex();         // an earlier call warmed the index
        var call = resolver.Capture();                   // this call's build
        World(false); File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
        var ex = Assert.Throws<LoadOrderChangedMidCallException>(() => call.EnsureReverseIndex());
        Assert.Equal("HcMcHeld.esm", ex.Plugin);
    }

    /// <summary>An active plugin a write opens by path (a compact source, a merge donor) is checked against the call's
    /// build, not pinned at first sight: a save between the call's door and that open refuses, naming it.</summary>
    [Fact]
    public void AnInOrderCompactSourceSavedBeforeItsOpenRefuses()
    {
        using var resolver = LoadOrderResolver.Build(new[] { _master, _a, _b });
        var outPath = Path.Combine(_dir, "out", AName);
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        bool saved = false;
        using var mark = MidCallChange.Begin();         // the tool guard's call mark
        resolver.Capture();                             // the call's door
        var r = During(name => { if (!saved && name == AName) { Save(_aNew, _a); saved = true; } },
                       () => WritePatchBuilder.CompactBuild(_a, ModKey.FromFileName(AName), new Dictionary<FormKey, FormKey>(),
                                                            n => n == MasterName ? _master : null, outPath, esl: false, floor: 0x800, dataDir: null, isFaction: _ => false));
        Assert.True(saved);
        Assert.False(r.Success);
        Assert.Contains($"'{AName}' changed on disk during this call", mark.Settle());
    }

    /// <summary>A compact that repoints its referencers commits the target in place first; a referencer saved after that
    /// commit refuses the rest, and the refusal names the file already written instead of saying nothing was.</summary>
    [Fact]
    public void ARefusalAfterACommitNamesTheFileAlreadyWritten()
    {
        var tgt = new LocalizedStringsFixture.Spec("McTgt", new ModKey("HcMcCmpTgt", ModType.Plugin), "TGT NAME", "TGT DESC", Localized: false);
        var rf = new LocalizedStringsFixture.Spec("McRef", new ModKey("HcMcCmpRef", ModType.Plugin), "REF NAME", "REF DESC",
                                                  LinksTo: LocalizedStringsFixture.WeaponKey(tgt), Localized: false);
        using var w = new LocalizedCompactWorld(tgt, rf);
        string tgtPath = w.PluginPath(tgt), refPath = w.PluginPath(rf);
        var tgtBefore = File.ReadAllBytes(tgtPath);
        bool saved = false;
        using var mark = MidCallChange.Begin();
        try
        {
            During(name =>
                {
                    if (saved || name != rf.Key.FileName.String || LocalizedCompactWorld.Same(tgtPath, tgtBefore)) return;
                    File.SetLastWriteTimeUtc(refPath, DateTime.UtcNow.AddSeconds(5)); saved = true;
                },
                () => w.Svc.CompactPlugin(tgt.Key.FileName.String, inPlace: true, repointExternals: true, acknowledge: true));
        }
        catch (LoadOrderChangedMidCallException) { /* the guard returns Settle's sentence */ }
        Assert.True(saved);
        var refusal = mark.Settle();
        Assert.Contains($"'{rf.Key.FileName}' changed on disk during this call, after houseCARL had written {tgt.Key.FileName}", refusal);
        Assert.DoesNotContain("nothing was answered or written", refusal);
    }

    /// <summary>A call opens the master's losing copy under one build; the master is saved and a refresh swaps the
    /// build, so a later open under the new build passes. The call's last check re-stats what it opened under the
    /// first build and refuses, naming the master.</summary>
    [Fact]
    public void AnOpenUnderAnEarlierBuildIsReCheckedWhenTheCallSpansTwoBuilds()
    {
        using var resolver = LoadOrderResolver.Build(new[] { _master, _a, _b });
        using var mark = MidCallChange.Begin();
        using (var first = resolver.OpenSession())
            Assert.NotNull(resolver.Capture().GetRecord(first, MasterName, _weapons[0]));
        File.SetLastWriteTimeUtc(_master, DateTime.UtcNow.AddSeconds(5));
        Assert.True(resolver.RefreshIfStale());
        using (var second = resolver.OpenSession())
            Assert.NotNull(resolver.Capture().GetRecord(second, MasterName, _weapons[0]));
        Assert.Contains($"'{MasterName}' changed on disk during this call", mark.Settle());
    }

    /// <summary>A session opened after a refresh checks its open against the new build; a view still on the old build
    /// reading the same plugin through that session checks the cached open against its own build and refuses.</summary>
    [Fact]
    public void ASessionsCachedOpenIsCheckedAgainstAViewOnAnotherBuild()
    {
        using var resolver = LoadOrderResolver.Build(new[] { _master, _a, _b });
        var older = resolver.Capture();
        Save(_aNew, _a);
        Assert.True(resolver.RefreshIfStale());
        using var session = resolver.OpenSession();
        Assert.NotNull(session.LinkCacheFor(AName));
        var ex = Assert.Throws<LoadOrderChangedMidCallException>(() => older.GetRecord(session, AName, _weapons[0]));
        Assert.Equal(AName, ex.Plugin);
    }

    /// <summary>An answer read off the index alone opens no plugin, so the open check never sees it: the dialogue
    /// check judges a right-typed LinkTo target by the index (#1173). The target's plugin is saved mid-call, and the
    /// call's last check refuses, naming it.</summary>
    [Fact]
    public void AnIndexOnlyAnswerRefusesWhenItsPluginChangesMidCall()
    {
        var t = new SkyrimMod(new ModKey("HcMcTargets", ModType.Master), SkyrimRelease.SkyrimSE);
        var target = t.DialogTopics.AddNew(); target.EditorID = "HcMcTarget";
        var tPath = Path.Combine(_dir, t.ModKey.FileName.String);
        t.BeginWrite.ToPath(tPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        var s = new SkyrimMod(new ModKey("HcMcSeed", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var quest = s.Quests.AddNew(); quest.EditorID = "HcMcQuest";
        var topic = s.DialogTopics.AddNew(); topic.EditorID = "HcMcSeedTopic";
        topic.Quest.SetTo(quest.FormKey); topic.SubtypeName = new RecordType("CUST");
        var info = new DialogResponses(s.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcMcSeedI" };
        DialogueCkParity.ApplyInfoDefaults(info);
        info.LinkTo.Add(new FormLink<IDialogTopicGetter>(target.FormKey));
        topic.Responses.Add(info);
        var sPath = Path.Combine(_dir, s.ModKey.FileName.String);
        s.BeginWrite.ToPath(sPath).WithLoadOrder(new ISkyrimModGetter[] { t }).Write();

        // The other program's save of the targets plugin: the topic is now a weapon.
        var tNew = new SkyrimMod(t.ModKey, SkyrimRelease.SkyrimSE);
        tNew.Weapons.Add(new Weapon(target.FormKey, SkyrimRelease.SkyrimSE) { EditorID = "HcMcNowAWeapon" });
        var tNewPath = Path.Combine(_dir, "new", t.ModKey.FileName.String);
        tNew.BeginWrite.ToPath(tNewPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        using var resolver = LoadOrderResolver.Build(new[] { tPath, sPath });
        using var assets = AssetResolver.Build("", "", _dir, Array.Empty<string>(), Array.Empty<ActiveArchive>());
        bool saved = false;
        using var mark = MidCallChange.Begin();         // the tool guard's call mark
        var report = During(name => { if (!saved && name == s.ModKey.FileName.String) { Save(tNewPath, tPath); saved = true; } },
                            () => DialogueValidate.Run(resolver, assets, topic.FormKey));
        Assert.True(saved);
        Assert.DoesNotContain(report.Topics.Single().Issues, i => i.Message.Contains("LinkTo"));   // the index said DIAL
        Assert.Contains($"'{t.ModKey.FileName}' changed on disk during this call", mark.Settle());
    }
}
