using System.Text.RegularExpressions;
using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The order-wide epoch: a deterministic fingerprint of the world state that changes with content, order,
/// set, the winning file and the excluded set; every index-backed lane stamps the build it read; a pinned render
/// fills from the scanned build and the next query re-stamps.</summary>
[Trait("tier", "integration")]
public sealed class EpochFingerprintTests : IDisposable
{
    static readonly ModKey MasterKey = new("HcEpochMaster", ModType.Master);
    static readonly ModKey OvKey = new("HcEpochOverride", ModType.Plugin);
    static readonly string MasterName = MasterKey.FileName.String, OvName = OvKey.FileName.String;

    readonly string _root;
    readonly string _inst;
    readonly string _masterFile, _ovFile;
    readonly SkyrimMod _master;
    readonly List<FormKey> _weapons = new();
    readonly FormKey _mgef;

    public EpochFingerprintTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "hc-epoch-fingerprint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "game", "Data"));

        _master = new SkyrimMod(MasterKey, SkyrimRelease.SkyrimSE);
        for (int i = 0; i < 3; i++)
        {
            var w = _master.Weapons.AddNew(); w.EditorID = $"HcEpW{i}";
            w.BasicStats = new WeaponBasicStats { Damage = 10, Weight = 1 };
            _weapons.Add(w.FormKey);
        }
        var mgef = _master.MagicEffects.AddNew(); mgef.EditorID = "HcEpMgef"; _mgef = mgef.FormKey;
        var spell = _master.Spells.AddNew(); spell.EditorID = "HcEpSpell";
        var eff = new Effect(); eff.BaseEffect.SetTo(_mgef); eff.Data = new EffectData { Magnitude = 5 };
        spell.Effects.Add(eff);

        var ov = new SkyrimMod(OvKey, SkyrimRelease.SkyrimSE);
        ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(ov, _master.Weapons.First())).BasicStats = new WeaponBasicStats { Damage = 20, Weight = 1 };

        _inst = Path.Combine(_root, "inst");
        var mods = Path.Combine(_inst, "mods");
        foreach (var m in new[] { "MasterMod", "OverrideMod" }) Directory.CreateDirectory(Path.Combine(mods, m));
        _masterFile = Path.Combine(mods, "MasterMod", MasterName);
        _ovFile = Path.Combine(mods, "OverrideMod", OvName);
        _master.BeginWrite.ToPath(_masterFile).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        ov.BeginWrite.ToPath(_ovFile).WithLoadOrder(new ISkyrimModGetter[] { _master }).Write();

        File.WriteAllText(Path.Combine(_inst, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(_root, "game").Replace(@"\", @"\\") + ")\r\n");
        var prof = Path.Combine(_inst, "profiles", "Default");
        Directory.CreateDirectory(prof);
        File.WriteAllText(Path.Combine(prof, "loadorder.txt"), "# header\r\n" + MasterName + "\r\n" + OvName + "\r\n");
        File.WriteAllText(Path.Combine(prof, "plugins.txt"), "*" + MasterName + "\r\n*" + OvName + "\r\n");
        File.WriteAllText(Path.Combine(prof, "modlist.txt"), "# header\r\n+OverrideMod\r\n+MasterMod\r\n");
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* temp cleanup best-effort */ } }

    LoadOrderService Service() => LoadOrderService.WithInstance(_inst, 0, new UserConfigStore(Path.Combine(_root, "user.json")));

    string Fid(FormKey fk) => $"{fk.ID:X6}:{fk.ModKey.FileName}";

    static bool IsEpochToken(string? e) =>
        e is not null && LoadOrderResolver.IsCurrentEpochFormat(e)
        && e[(e.IndexOf('-') + 1)..] is { Length: 16 } hex && hex.All(ch => ch is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    // Probe arm 1: "epoch is an opaque format-tagged hex token", "a SECOND resolver over the same files fingerprints
    // IDENTICALLY", "view epoch == resolver epoch", "RefreshIfStale over an UNCHANGED order keeps the epoch".
    [Fact]
    public void TheEpoch_IsATaggedHexTokenStableAcrossResolversOverTheSameFiles()
    {
        var paths = new[] { _masterFile, _ovFile };
        using var r1 = LoadOrderResolver.Build(paths);
        using var r2 = LoadOrderResolver.Build(paths);
        Assert.True(IsEpochToken(r1.Epoch), r1.Epoch);
        Assert.Equal(r1.Epoch, r2.Epoch);
        Assert.Equal(r1.Epoch, r1.Capture().Epoch);
        Assert.False(r1.RefreshIfStale());
        Assert.Equal(r2.Epoch, r1.Capture().Epoch);
    }

    // Probe arm 1: "an epoch from an older format is distinguishable" and "a tag, a dash, then exactly 16 hex".
    [Fact]
    public void AnUntaggedEpoch_IsNotTheCurrentFormat()
    {
        using var r = LoadOrderResolver.Build(new[] { _masterFile });
        Assert.True(LoadOrderResolver.IsCurrentEpochFormat(r.Epoch));
        Assert.False(LoadOrderResolver.IsCurrentEpochFormat("0123456789abcdef"));
        Assert.True(r.Epoch.IndexOf('-') > 0);
        Assert.Equal(r.Epoch.IndexOf('-') + 17, r.Epoch.Length);
    }

    // Probe arm 2: "a BACKDATED content change is seen (value-compare, not newer-than)" and "the epoch changed with it".
    [Fact]
    public void ABackdatedContentChange_RefreshesAndChangesTheEpoch()
    {
        using var r = LoadOrderResolver.Build(new[] { _masterFile, _ovFile });
        var e0 = r.Epoch;
        File.SetLastWriteTimeUtc(_ovFile, DateTime.UtcNow.AddHours(-2));
        Assert.True(r.RefreshIfStale());
        Assert.NotEqual(e0, r.Capture().Epoch);
    }

    // Probe arm 2: "a REORDER fingerprints differently" and "a SET change fingerprints differently".
    [Fact]
    public void AReorderAndASetChange_EachFingerprintDifferently()
    {
        using var r = LoadOrderResolver.Build(new[] { _masterFile, _ovFile });
        using var swap = LoadOrderResolver.Build(new[] { _ovFile, _masterFile });
        using var one = LoadOrderResolver.Build(new[] { _masterFile });
        Assert.NotEqual(r.Epoch, swap.Epoch);
        Assert.NotEqual(r.Epoch, one.Epoch);
        Assert.NotEqual(swap.Epoch, one.Epoch);
    }

    // Probe arm 2: "a DIFFERENT FILE winning a same-named slot (same name, same mtime) fingerprints differently — the path term".
    // Probe arm 2: "a LOCKED plugin is excluded" and "the degraded and healthy builds fingerprint DIFFERENTLY".
    [Fact]
    public void TheWinningFilesPathAndTheExcludedSet_AreFingerprintTerms()
    {
        var copyA = Path.Combine(_root, "same-name-a", OvName);
        var copyB = Path.Combine(_root, "same-name-b", OvName);
        Directory.CreateDirectory(Path.GetDirectoryName(copyA)!); Directory.CreateDirectory(Path.GetDirectoryName(copyB)!);
        File.Copy(_ovFile, copyA); File.Copy(_ovFile, copyB);
        var tick = DateTime.UtcNow.AddHours(-1);
        File.SetLastWriteTimeUtc(copyA, tick); File.SetLastWriteTimeUtc(copyB, tick);
        using (var rA = LoadOrderResolver.Build(new[] { _masterFile, copyA }))
        using (var rB = LoadOrderResolver.Build(new[] { _masterFile, copyB }))
            Assert.NotEqual(rA.Epoch, rB.Epoch);

        string locked;
        using (new FileStream(copyA, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            using var rLocked = LoadOrderResolver.Build(new[] { _masterFile, copyA });
            Assert.Single(rLocked.ExcludedPlugins);
            locked = rLocked.Epoch;
        }
        using var healthy = LoadOrderResolver.Build(new[] { _masterFile, copyA });
        Assert.Empty(healthy.ExcludedPlugins);
        Assert.NotEqual(locked, healthy.Epoch);
    }

    // Probe arm 3: "Stats() names the current build epoch" and "StatusData carries the same epoch".
    [Fact]
    public void StatsAndStatus_NameTheCurrentBuild()
    {
        using var svc = Service();
        var current = svc.Stats().epoch;
        Assert.True(IsEpochToken(current), current);
        Assert.Equal(current, svc.StatusData().Epoch);
    }

    // Probe arm 3: "cross_plugin_query outcome stamps the scanned build", the text, json and dense renders carry it,
    // "group_by count table carries it", and "a REFUSED query (no filter) stays unstamped".
    [Fact]
    public void TheRecordsScan_StampsItsBuildInEveryRenderAndARefusalStaysUnstamped()
    {
        using var svc = Service();
        var current = svc.Stats().epoch;
        var q = svc.ReadArea.CrossQuery("WEAP", null, null, false, null, null, 500);
        Assert.Equal(current, q.Epoch);
        Assert.Contains($"epoch={current}", Wire.RenderCrossQuery(svc, q, null, 0));
        Assert.Contains($"\"epoch\": \"{current}\"", JsonWire.RenderCrossQuery(svc, q, null, 0, false, false));
        Assert.Contains($"\"epoch\": \"{current}\"", JsonWire.RenderCrossQueryDense(svc, q, null, 0, false, false));
        var g = svc.ReadArea.CrossQuery("WEAP", null, null, false, null, null, 500, groupBy: "winner");
        Assert.Equal(current, g.Epoch);
        Assert.Contains($"epoch={current}", Wire.RenderCrossQuery(svc, g, null, 0));
        var refused = svc.ReadArea.CrossQuery((string?)null, null, null, false, null, null, 500);
        Assert.NotNull(refused.Error);
        Assert.Null(refused.Epoch);
    }

    // Probe arm 3: "batch: every consulted row carries the batch's ONE epoch", "the malformed-formid row carries none",
    // "the absent-record REFUSAL is stamped", and the text and json headers carry it once.
    [Fact]
    public void ABatch_StampsOneEpochOnEveryConsultedRowIncludingAnAbsentRecord()
    {
        using var svc = Service();
        var current = svc.Stats().epoch;
        var batch = svc.ResolveBatch(new[] { Fid(_weapons[0]), Fid(_weapons[1]), "notaformid", "ABC123:Absent.esp" }, null, false);
        Assert.Equal(new[] { current }, batch.Where(o => o.Epoch is not null).Select(o => o.Epoch).Distinct());
        Assert.Null(batch[2].Epoch);
        Assert.NotNull(batch[3].Error);
        Assert.Equal(current, batch[3].Epoch);
        Assert.Single(Regex.Matches(Wire.RenderBatch(batch, 0), Regex.Escape($"epoch={current}")));
        Assert.Single(Regex.Matches(JsonWire.RenderBatch(batch, 0), Regex.Escape($"\"epoch\": \"{current}\"")));
    }

    // Probe arm 3: "read_record outcome stamps its capture", "resolve hands back the batch's epoch" (now the list
    // lane's summary rows, which replaced resolve).
    [Fact]
    public void ASingleReadAndResolve_StampTheirCapture()
    {
        using var svc = Service();
        var current = svc.Stats().epoch;
        Assert.Equal(current, svc.ReadArea.ResolveRead(_weapons[0], null, null, false).Epoch);
        var json = RecordsTools.Records(svc, formids: new[] { Fid(_weapons[0]), Fid(_mgef) }, format: "json");
        Assert.Single(Regex.Matches(json, Regex.Escape($"\"epoch\": \"{current}\"")));
        Assert.Contains("\"count\": 2", json);
    }

    // Probe arm 3: "effect_chain stamps + renders it", "its not-in-order refusal is stamped … and rendered", and
    // "its PRE-capture type-narrow refusal stays null".
    [Fact]
    public void EffectChain_StampsAnswersAndPostCaptureRefusalsButNotAPreCaptureRefusal()
    {
        using var svc = Service();
        var current = svc.Stats().epoch;
        var ec = svc.ResolveEffectChain(_mgef, null, 500);
        Assert.Null(ec.Error);
        Assert.Equal(current, ec.Epoch);
        Assert.Contains($"epoch={current}", Wire.RenderEffectChain(ec, 0, "limit="));
        var miss = svc.ResolveEffectChain(FormKey.Factory("0ABC12:" + MasterName), null, 500);
        Assert.NotNull(miss.Error);
        Assert.Equal(current, miss.Epoch);
        Assert.Contains($"epoch={current}", Wire.RenderEffectChain(miss, 0, "limit="));
        var narrow = svc.ResolveEffectChain(_mgef, new[] { "WEAP" }, 500);
        Assert.NotNull(narrow.Error);
        Assert.Null(narrow.Epoch);
    }

    // Probe arm 4: "scan, single read, and batch outcomes all carry the ViewPin", "the pinned render still stamps the
    // scanned build", "its fills read the SCANNED build's winners (1/1)", "the NEXT query re-stamps", "status agrees".
    [Fact]
    public void APinnedRender_FillsFromTheScannedBuildAndTheNextQueryReStamps()
    {
        using var svc = Service();
        var current = svc.Stats().epoch;
        var q = svc.ReadArea.CrossQuery("WEAP", null, null, false, null, null, 500);
        Assert.Equal(current, q.Epoch);
        Assert.NotNull(q.Pin);
        Assert.NotNull(svc.ReadArea.ResolveRead(_weapons[0], null, null, true).Pin);
        Assert.NotNull(svc.ResolveBatch(new[] { Fid(_weapons[0]) }, null, true)[0].Pin);

        // The override now also wins weapon 1; the pinned render must still show it winning one record.
        var ov2 = new SkyrimMod(OvKey, SkyrimRelease.SkyrimSE);
        ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(ov2, _master.Weapons.First())).BasicStats = new WeaponBasicStats { Damage = 20, Weight = 1 };
        ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(ov2, _master.Weapons.Skip(1).First())).BasicStats = new WeaponBasicStats { Damage = 99, Weight = 1 };
        ov2.BeginWrite.ToPath(_ovFile).WithLoadOrder(new ISkyrimModGetter[] { _master }).Write();

        var pinned = JsonWire.RenderCrossQuery(svc, q, new[] { "EditorID" }, 0, false, false);
        Assert.Contains($"\"epoch\": \"{current}\"", pinned);
        Assert.Single(Regex.Matches(pinned, "\"winner\": \"" + Regex.Escape(OvName) + "\""));

        var q2 = svc.ReadArea.CrossQuery("WEAP", null, null, false, null, null, 500);
        Assert.NotNull(q2.Epoch);
        Assert.NotEqual(current, q2.Epoch);
        Assert.Equal(q2.Epoch, svc.Stats().epoch);
        // The next query's own fills see the rewrite: the override now wins two records.
        Assert.Equal(2, Regex.Matches(JsonWire.RenderCrossQuery(svc, q2, new[] { "EditorID" }, 0, false, false),
                                      "\"winner\": \"" + Regex.Escape(OvName) + "\"").Count);
    }
}
