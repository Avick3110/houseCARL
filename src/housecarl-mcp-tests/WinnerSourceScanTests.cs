using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlGenerator;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>An order whose winners are spread across THREE plugins, which is what a winner-source scan has to get
/// right: a master defining 30 weapons, a first plugin overriding the first ten and a second overriding a middle
/// ten that overlaps it. So five records win in the first plugin, ten in the second and fifteen in the master, and
/// a scan that fetched the wrong plugin's body — or the first declarer's — matches a different set.</summary>
public sealed class WinnerSourceWorld : IDisposable
{
    public const int Weapons = 30, Armors = 3;
    public const ushort MasterDamage = 5, LowDamage = 20, MidDamage = 30;

    public string Root { get; }
    public LoadOrderService Svc { get; }
    public string MasterName { get; }
    public string LowName { get; }
    public string MidName { get; }
    public string MasterPath { get; }
    public string LowPath { get; }
    public string MidPath { get; }
    public IReadOnlyList<FormKey> Keys { get; }
    /// <summary>Records of a SECOND type in the same master, so a typed gather can be handed a type that does not
    /// cover every wanted key.</summary>
    public IReadOnlyList<FormKey> ArmorKeys { get; }

    readonly string _priorCorpusPath;

    public WinnerSourceWorld()
    {
        _priorCorpusPath = CorpusRulebook.CorpusPath;
        Root = Path.Combine(Path.GetTempPath(), "hc-winner-source-" + Guid.NewGuid().ToString("N"));
        var instance = Path.Combine(Root, "instance");
        var profiles = Path.Combine(instance, "profiles", "Default");
        var mods = Path.Combine(instance, "mods");
        foreach (var d in new[] { profiles, mods, Path.Combine(Root, "game", "Data") }) Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");

        var masterKey = new ModKey("HcWsrcBase", ModType.Master);
        var lowKey = new ModKey("HcWsrcLow", ModType.Plugin);
        var midKey = new ModKey("HcWsrcMid", ModType.Plugin);
        MasterName = masterKey.FileName.String; LowName = lowKey.FileName.String; MidName = midKey.FileName.String;

        var keys = new List<FormKey>();
        var masterDir = Path.Combine(mods, "BaseMod"); Directory.CreateDirectory(masterDir);
        var master = new SkyrimMod(masterKey, SkyrimRelease.SkyrimSE);
        for (int i = 0; i < Weapons; i++)
        {
            var fk = new FormKey(masterKey, (uint)(0xA00 + i));
            keys.Add(fk);
            master.Weapons.Add(new Weapon(fk, SkyrimRelease.SkyrimSE)
            {
                EditorID = "HcWsrcWeap" + i,
                BasicStats = new WeaponBasicStats { Damage = MasterDamage, Value = 10, Weight = 1 },
            });
        }
        Keys = keys;
        var armorKeys = new List<FormKey>();
        for (int i = 0; i < Armors; i++)
        {
            var fk = new FormKey(masterKey, (uint)(0xB00 + i));
            armorKeys.Add(fk);
            master.Armors.Add(new Armor(fk, SkyrimRelease.SkyrimSE) { EditorID = "HcWsrcArmo" + i });
        }
        ArmorKeys = armorKeys;
        MasterPath = Path.Combine(masterDir, MasterName);
        master.BeginWrite.ToPath(MasterPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        // Disposed before the fixture leaves the constructor: it is a memory-mapped overlay, and a live handle on the
        // master would make the temp tree undeletable on Windows, leaking a whole instance per run.
        var masterOv = SkyrimMod.CreateFromBinaryOverlay(MasterPath, SkyrimRelease.SkyrimSE);
        try
        {
            LowPath = Write(mods, "LowMod", lowKey, masterOv, 0, 10, LowDamage);
            MidPath = Write(mods, "MidMod", midKey, masterOv, 5, 15, MidDamage);
        }
        finally { (masterOv as IDisposable)?.Dispose(); }

        var order = new[] { MasterName, LowName, MidName };
        File.WriteAllText(Path.Combine(profiles, "loadorder.txt"), "# header\r\n" + string.Join("\r\n", order) + "\r\n");
        File.WriteAllText(Path.Combine(profiles, "plugins.txt"), string.Concat(order.Select(n => "*" + n + "\r\n")));
        File.WriteAllText(Path.Combine(profiles, "modlist.txt"), "# header\r\n+MidMod\r\n+LowMod\r\n+BaseMod\r\n");

        var genDir = Path.Combine(Root, "corpus-gen");
        CorpusGenerator.GenerateAll(genDir, Path.Combine(Root, "corpus-ref"));
        CorpusRulebook.CorpusPath = Path.Combine(genDir, "corpus.json");

        Svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(Root, "houseCARL.user.json")));
        Svc.Stats();
    }

    string Write(string mods, string folder, ModKey key, ISkyrimModGetter masterOv, int from, int to, ushort damage)
    {
        var dir = Path.Combine(mods, folder); Directory.CreateDirectory(dir);
        var m = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        for (int i = from; i < to; i++)
        {
            var w = m.Weapons.GetOrAddAsOverride(masterOv.Weapons.First(x => x.FormKey == Keys[i]));
            w.BasicStats!.Damage = damage;
        }
        var path = Path.Combine(dir, key.FileName.String);
        m.BeginWrite.ToPath(path).WithLoadOrder(new[] { masterOv }).Write();
        return path;
    }

    public void Dispose()
    {
        Svc.Dispose();
        CorpusRulebook.CorpusPath = _priorCorpusPath;
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}

public sealed class WinnerSourceFixture : IDisposable
{
    public WinnerSourceWorld W { get; } = new();
    public void Dispose() => W.Dispose();
}

/// <summary>
/// <c>where_source=winner</c> over a scope whose records win in three different plugins (#251). The winner bodies
/// are gathered a chunk of candidates at a time, by PLUGIN — one enumeration per distinct winner plugin in the
/// chunk — instead of one whole-overlay walk per candidate, so these pin that the batched gather answers with the
/// same set the per-record fetch did: the true winner's body for every candidate, including the ones the scoped
/// plugin itself wins, which it takes from the streamed body rather than re-reading.
/// </summary>
[Trait("tier", "integration")]
public sealed class WinnerSourceScanTests : IClassFixture<WinnerSourceFixture>
{
    readonly WinnerSourceWorld _w;
    public WinnerSourceScanTests(WinnerSourceFixture f) => _w = f.W;

    const string DamagePath = "BasicStats.Damage";

    string[] Matched(string comparison, string[]? scope = null, string[]? types = null)
    {
        var json = Scan(comparison, scope, types);
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.TryGetProperty("error", out _), json);
        return doc.RootElement.GetProperty("matches").EnumerateArray()
                  .Select(m => m.GetProperty("formid").GetString()!).OrderBy(x => x, StringComparer.Ordinal).ToArray();
    }

    string[] Matched(ushort damage, string[]? scope = null, string[]? types = null) => Matched($"= {damage}", scope, types);

    string Scan(string comparison, string[]? scope = null, string[]? types = null) =>
        RecordsTools.Records(_w.Svc, types: types,
            plugins: new RecordsTools.RecordsScope { names = scope ?? new[] { _w.MasterName } },
            where: new[] { $"{DamagePath} {comparison}" }, where_source: "winner",
            project: new RecordsTools.RecordsProject { form = "fields", fields = new[] { "EditorID" } },
            format: "json", max_chars: 200000, limit: 1000);

    string[] Expected(int from, int to) =>
        Enumerable.Range(from, to - from).Select(i => _w.Keys[i])
                  .Select(fk => fk.ToString()).OrderBy(x => x, StringComparer.Ordinal).ToArray();

    /// <summary>The middle ten win in the LAST plugin, and only those match its damage — a gather that took the
    /// first declarer, or the scoped body, would answer a different set.</summary>
    [Fact]
    public void TheRecordsWonByTheHighestPluginMatchOnThatPluginsBody() =>
        Assert.Equal(Expected(5, 15), Matched(WinnerSourceWorld.MidDamage));

    /// <summary>The first five are overridden by the low plugin and NOT by the high one, so they win there — the
    /// gather has to fetch from a second winner plugin in the same scan.</summary>
    [Fact]
    public void TheRecordsWonByTheMiddlePluginMatchOnThatOne() =>
        Assert.Equal(Expected(0, 5), Matched(WinnerSourceWorld.LowDamage));

    /// <summary>And the fifteen nobody overrides win in the MASTER, which is also the scoped plugin: a gather that
    /// only looked at plugins above the scope would lose half the order.</summary>
    [Fact]
    public void TheRecordsNobodyOverridesWinInTheScopedPluginItself() =>
        Assert.Equal(Expected(15, WinnerSourceWorld.Weapons), Matched(WinnerSourceWorld.MasterDamage));

    /// <summary>Every candidate is accounted for exactly once across the three winner plugins — no record is
    /// dropped by the gather and none is counted twice.</summary>
    [Fact]
    public void TheThreeWinnerPluginsPartitionTheScopeWithNothingLostOrDoubled()
    {
        var all = Matched(WinnerSourceWorld.MidDamage).Concat(Matched(WinnerSourceWorld.LowDamage))
                  .Concat(Matched(WinnerSourceWorld.MasterDamage)).ToArray();
        Assert.Equal(WinnerSourceWorld.Weapons, all.Length);
        Assert.Equal(WinnerSourceWorld.Weapons, all.Distinct().Count());
    }

    /// <summary>A scope spanning several plugins still de-dups to one verdict per record: the winner answer is
    /// FormKey-intrinsic, so a key touched by two scoped plugins must not match twice.</summary>
    [Fact]
    public void AMultiPluginScopeStillAnswersOncePerRecord() =>
        Assert.Equal(Expected(5, 15),
                     Matched(WinnerSourceWorld.MidDamage, new[] { _w.MasterName, _w.LowName, _w.MidName }));

    /// <summary>The same answer with a type scope, which narrows each winner plugin's walk to that type's GRUP.</summary>
    [Fact]
    public void ATypeScopedWinnerSourceScanAnswersTheSameSet() =>
        Assert.Equal(Expected(5, 15), Matched(WinnerSourceWorld.MidDamage, types: new[] { "WEAP" }));

    /// <summary>The gather under the tool: every candidate is judged on its WINNER's body. The scoped master's own
    /// copy declares the same damage on all thirty, so a filter above it can only match through the gather — the
    /// fifteen some plugin raises — and no candidate may go unscannable on the way.</summary>
    [Fact]
    public void EveryCandidateIsJudgedOnItsWinnersBody_NotTheScopedPluginsCopy()
    {
        Assert.Equal(Expected(0, 15), Matched($"> {WinnerSourceWorld.MasterDamage}"));
        Assert.DoesNotContain("did not yield the record", Scan($"> {WinnerSourceWorld.MasterDamage}"));
    }

    /// <summary>A winner plugin another program is holding open says THAT, with the underlying cause, rather than
    /// reporting the index as stale — the two are different problems and only one of them the user can act on.</summary>
    [Fact]
    public void AWinnerPluginHeldOpenNamesTheHeldFile_NotAStaleIndex()
    {
        using var hold = new FileStream(_w.MidPath, FileMode.Open, FileAccess.Read, FileShare.None);
        var json = Scan($"= {WinnerSourceWorld.MidDamage}");
        using var doc = JsonDocument.Parse(json);
        var notes = string.Join(" ", doc.RootElement.GetProperty("notes").EnumerateArray().Select(n => n.GetString()));
        Assert.Contains($"could not read '{_w.MidName}'", notes);
        Assert.Contains("being used by another process", notes);
        Assert.DoesNotContain("did not yield the record on winner-source re-fetch", notes);
    }

    /// <summary>The typed gather decides its flat fallback per KEY, not per plugin. Handed a type that covers only
    /// some of the wanted keys, it must still answer with the rest: Mutagen's typed enumeration routes nothing at
    /// all for a type it does not model, so "the typed walk found something, therefore what it missed is not here"
    /// turns a record the plugin holds into a silent absence.</summary>
    [Fact]
    public void ATypedGatherFallsThroughForAWantedKeyThatTypeDoesNotCover()
    {
        using var resolver = LoadOrderResolver.Build(new[] { _w.MasterPath, _w.LowPath, _w.MidPath });
        var view = resolver.Capture();
        using var session = resolver.OpenSession();
        var sink = new Dictionary<FormKey, IMajorRecordGetter>();
        view.CollectRecords(session, _w.MasterName,
                            new[] { _w.Keys[20], _w.ArmorKeys[0] }, new[] { typeof(IWeaponGetter) }, sink);
        Assert.Contains(_w.Keys[20], sink.Keys);
        Assert.Contains(_w.ArmorKeys[0], sink.Keys);
    }
}
