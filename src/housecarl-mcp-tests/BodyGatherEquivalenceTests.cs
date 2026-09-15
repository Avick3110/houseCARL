using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The gather's whole claim, asserted without depending on what a plugin serializes to: for every record a write
/// lane asks for, <see cref="BodyGather.Body"/> answers what the one-at-a-time
/// <see cref="LoadOrderResolver.IndexView.GetRecord"/> answers — the same record, the same fields, and the same
/// null for one the plugin does not hold.
/// </summary>
[Trait("tier", "integration")]
public sealed class BodyGatherEquivalenceTests : IDisposable
{
    const int Records = 200;

    readonly string _root;
    readonly LoadOrderResolver _resolver;
    readonly List<FormKey> _keys = new();
    readonly string _masterName;
    readonly string _replName;

    public BodyGatherEquivalenceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "hc-gathereq-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        var masterKey = new ModKey("HcGatherEqMaster", ModType.Master);
        _masterName = masterKey.FileName.String;
        var master = new SkyrimMod(masterKey, SkyrimRelease.SkyrimSE);
        for (int i = 0; i < Records; i++)
        {
            var w = master.Weapons.AddNew();
            w.EditorID = "HcGatherEqW" + i;
            w.BasicStats = new WeaponBasicStats { Damage = (ushort)(10 + i), Weight = 1 };
            _keys.Add(w.FormKey);
        }
        var masterFile = Path.Combine(_root, _masterName);
        master.BeginWrite.ToPath(masterFile).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        // A second plugin overriding only every tenth record, so the two lookups are also compared on a plugin that
        // holds SOME of the wanted keys and not the rest — the case where a miss has to stay a miss.
        var replKey = new ModKey("HcGatherEqRepl", ModType.Plugin);
        _replName = replKey.FileName.String;
        var repl = new SkyrimMod(replKey, SkyrimRelease.SkyrimSE);
        for (int i = 0; i < Records; i += 10)
        {
            var ov = (IWeapon)WriteEngine.GenericGetOrAddAsOverride(repl, master.Weapons.First(w => w.FormKey == _keys[i]));
            ov.BasicStats = new WeaponBasicStats { Damage = 999, Weight = 2 };
        }
        var replFile = Path.Combine(_root, _replName);
        repl.BeginWrite.ToPath(replFile).WithLoadOrder(new ISkyrimModGetter[] { master }).Write();

        _resolver = LoadOrderResolver.Build(new[] { masterFile, replFile });
    }

    static string Describe(IMajorRecordGetter? r) =>
        r is null ? "(absent)" : $"{r.FormKey}|{r.GetType().Name}|{r.EditorID}|{(r as IWeaponGetter)?.BasicStats?.Damage}";

    [Fact]
    public void AGatheredBodyIsTheBodyTheSingleFetchReturns()
    {
        using var session = _resolver.OpenSession();
        var view = _resolver.Capture();

        var gather = new BodyGather(view, session);
        foreach (var k in _keys) { gather.Want(_masterName, k); gather.Want(_replName, k); }
        gather.Gather();

        using var plain = _resolver.OpenSession();
        var plainView = _resolver.Capture();
        foreach (var k in _keys)
            foreach (var plugin in new[] { _masterName, _replName })
                Assert.Equal(Describe(plainView.GetRecord(plain, plugin, k)), Describe(gather.Body(plugin, k)));
    }

    [Fact]
    public void AnUndeclaredPairStillAnswers()
    {
        using var session = _resolver.OpenSession();
        var view = _resolver.Capture();

        var gather = new BodyGather(view, session);
        gather.Want(_masterName, _keys[0]);
        gather.Gather();

        // Never declared: the fallback fetch answers it rather than reporting the record absent.
        Assert.Equal("(absent)", Describe(gather.Body(_replName, _keys[1])));
        Assert.NotEqual("(absent)", Describe(gather.Body(_masterName, _keys[5])));
        Assert.NotEqual("(absent)", Describe(gather.Body(_replName, _keys[10])));
    }

    /// <summary>Run <paramref name="body"/> with the override plugin moved out from under the index — the file that
    /// opened when the load order was built and cannot be opened now, which is the fault every option here is a
    /// different answer to. The view is captured BEFORE the move, so the plugin is still a member of the build the
    /// gather walks rather than one the index dropped.</summary>
    void WithUnreadableRepl(Action<LoadOrderResolver.IndexView, LoadOrderResolver.OverlaySession> body)
    {
        var view = _resolver.Capture();
        var repl = Path.Combine(_root, _replName);
        var away = repl + ".away";
        File.Move(repl, away);
        try
        {
            using var session = _resolver.OpenSession();
            body(view, session);
        }
        finally { File.Move(away, repl); }
    }

    [Fact]
    public void TheNullOptionAnswersNullInsteadOfSeeking()
    {
        using var session = _resolver.OpenSession();
        var view = _resolver.Capture();

        var gather = new BodyGather(view, session, absent: BodyGather.Absent.Null);
        gather.Want(_masterName, _keys[0]);
        gather.Gather();

        Assert.NotEqual("(absent)", Describe(gather.Body(_masterName, _keys[0])));   // declared and held: the body

        // Never declared. The Seek option fetches it one at a time; this one answers null and pays NO seek, because
        // the caller's own read raises whatever it raises.
        var before = LoadOrderResolver.BodySeeks;
        Assert.Null(gather.Body(_masterName, _keys[1]));
        Assert.Null(gather.Body(_replName, _keys[10]));
        Assert.Equal(before, LoadOrderResolver.BodySeeks);
    }

    [Fact]
    public void AFaultedPluginIsNamedAndFallsBackToTheSingleFetch()
    {
        WithUnreadableRepl((view, session) =>
        {
            var gather = new BodyGather(view, session);
            gather.Want(_replName, _keys[0]);
            gather.Gather();

            Assert.Contains(_replName, gather.Faults.Keys);
            Assert.Contains(_replName, gather.Faulted);
            Assert.IsType<PluginUnreadableException>(gather.Faults[_replName]);

            // The fallback IS the one-at-a-time path, so the caller sees the fault its own loop always saw — the
            // same type, in the same words — rather than an up-front throw naming no record. Not the same
            // exception the gather RECORDS: CollectRecords names the open failure itself, while the per-record
            // fetch lets the underlying fault out raw, and that asymmetry is the pre-existing one this fold left
            // alone.
            var direct = Assert.ThrowsAny<Exception>(() => view.GetRecord(session, _replName, _keys[0]));
            var through = Assert.ThrowsAny<Exception>(() => gather.Body(_replName, _keys[0]));
            Assert.Equal(direct.GetType(), through.GetType());
            Assert.Equal(direct.Message, through.Message);

            // A plugin that IS readable in the same gather is unaffected.
            Assert.NotEqual("(absent)", Describe(gather.Body(_masterName, _keys[0])));
        });
    }

    [Fact]
    public void AFaultedPluginAnswersNullUnderTheNullOption()
    {
        WithUnreadableRepl((view, session) =>
        {
            var gather = new BodyGather(view, session, absent: BodyGather.Absent.Null);
            gather.Want(_replName, _keys[0]);
            gather.Gather();

            Assert.Contains(_replName, gather.Faults.Keys);
            Assert.Null(gather.Body(_replName, _keys[0]));      // no throw: the row's own read raises the fault
        });
    }

    [Fact]
    public void AFaultedPluginIsNotWalkedAgainBySecondGather()
    {
        var view = _resolver.Capture();
        var repl = Path.Combine(_root, _replName);
        var away = repl + ".away";
        File.Move(repl, away);
        try
        {
            using var session = _resolver.OpenSession();
            var gather = new BodyGather(view, session, absent: BodyGather.Absent.Null);
            gather.Want(_replName, _keys[0]);
            gather.Gather();
            Assert.Contains(_replName, gather.Faults.Keys);

            // The plugin is readable again, and a second Gather() still does not walk it: a plugin is attempted
            // ONCE per gather. Every caller declares, gathers, and reads once, so nothing observes this — it is
            // asserted because it is a contract point on a public class, not because a lane depends on it.
            File.Move(away, repl);
            gather.Gather();
            Assert.Null(gather.Body(_replName, _keys[0]));
        }
        finally { if (File.Exists(away)) File.Move(away, repl); }
    }

    public void Dispose()
    {
        _resolver.Dispose();
        try { Directory.Delete(_root, true); } catch { /* temp cleanup best-effort */ }
    }
}
