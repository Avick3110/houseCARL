using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;

namespace HousecarlMcpTests;

/// <summary>
/// The synthetic MO2 instance the errors family's own arms are driven over — the world #486 PR 2 owed, built
/// because <see cref="CheckWorld"/> is frozen (its arms take fixture-known totals from it, so a later need gets
/// its own world rather than an edit to that one).
///
/// <para>What it carries, and why each piece is here:</para>
/// <list type="bullet">
/// <item><c>Skyrim.esm</c> — a base master BY FILENAME, which is what <see cref="ErrorCheck.BaseMasters"/> matches
///   on, holding three dangling refs. It is in <c>loadorder.txt</c> and absent from <c>plugins.txt</c>, so it is
///   force-loaded, and its findings are the BASELINE half of the split.</item>
/// <item><c>HcErrMod.esp</c> — two more dangling refs, mastering only <c>Skyrim.esm</c>. The non-baseline half.</item>
/// <item><c>HcErrPatch.esp</c> — declares <c>HcErrGone.esm</c>, written OUTSIDE the instance entirely, so the
///   master is missing and not installed anywhere. Its one NPC points into that master, so it also carries one
///   dangling ref: a missing master and the broken refs into it are the same event seen twice, and a world that
///   separated them would be modelling something MO2 cannot produce.</item>
/// <item><c>HcErrBad.esp</c> — sixteen bytes of rubbish, ticked in <c>plugins.txt</c>. The index cannot parse it,
///   so the excluded-plugins roster is a REAL one rather than a hand-shaped dictionary.</item>
/// </list>
///
/// <para><b>Fixture-known totals</b> (the constants below, asserted by
/// <see cref="CheckErrorsWorldTests.TheWorldSweepsThreePluginsAndFindsSixDanglingRefsOneMissingMasterAndOneUnparseablePlugin"/>
/// so a fixture drift fails there rather than everywhere): six dangling refs over three plugins, three of them
/// baseline; one missing master; one unparseable plugin.</para>
///
/// <para><b>The world is frozen</b>, in the sense <see cref="BulkRecordsWorld"/> states: arms take those totals
/// from it, so a later need builds its own world instead of editing this one. It does not repoint
/// <c>CorpusRulebook.CorpusPath</c> — the errors sweep reads master tables and FormLinks, never the record
/// rulebook — so it generates no corpus and touches no process-global.</para>
/// </summary>
public sealed class CheckErrorsWorld : IDisposable
{
    public string Root { get; }
    public string Instance { get; }

    /// <summary>The base master, by filename. Three of the six dangling refs are its.</summary>
    public string BaseName => "Skyrim.esm";
    /// <summary>The ordinary mod plugin: two dangling refs, every master satisfied.</summary>
    public string ModName => "HcErrMod.esp";
    /// <summary>The plugin whose declared master is missing: one dangling ref into it.</summary>
    public string PatchName => "HcErrPatch.esp";
    /// <summary>The master <see cref="PatchName"/> declares, written outside the instance — installed nowhere.</summary>
    public string GoneName => "HcErrGone.esm";
    /// <summary>The plugin the index cannot parse.</summary>
    public string BadName => "HcErrBad.esp";

    /// <summary>Dangling refs in the whole world.</summary>
    public const int TotalDangling = 6;
    /// <summary>How many of them come from the base master.</summary>
    public const int BaselineDangling = 3;
    /// <summary>Plugins the sweep scans — the three parseable ones; the unparseable one is excluded, not scanned.</summary>
    public const int ScannedPlugins = 3;

    public LoadOrderService Svc { get; }

    public CheckErrorsWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-check-errors-world-" + Guid.NewGuid().ToString("N"));
        Instance = Path.Combine(Root, "instance");
        var profileDir = Path.Combine(Instance, "profiles", "Default");
        var mods = Path.Combine(Instance, "mods");
        var outside = Path.Combine(Root, "not-installed");
        Directory.CreateDirectory(profileDir);
        Directory.CreateDirectory(mods);
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(Path.Combine(Root, "game", "Data"));
        File.WriteAllText(Path.Combine(Instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");

        var vanillaDir = Path.Combine(mods, "VanillaStub");
        var modDir = Path.Combine(mods, "ErrMod");
        var patchDir = Path.Combine(mods, "ErrPatch");
        var badDir = Path.Combine(mods, "ErrBad");
        foreach (var d in new[] { vanillaDir, modDir, patchDir, badDir }) Directory.CreateDirectory(d);

        // Defined by nothing in the order — every ref to it dangles.
        var deadFk = FormKey.Factory("0E0E0E:Skyrim.esm");

        var sky = new SkyrimMod(new ModKey("Skyrim", ModType.Master), SkyrimRelease.SkyrimSE);
        for (int i = 0; i < BaselineDangling; i++)
        { var n = sky.Npcs.AddNew(); n.EditorID = $"HcErrVanilla{i}"; n.Race.SetTo(deadFk); }
        var skyPath = Path.Combine(vanillaDir, BaseName);
        sky.BeginWrite.ToPath(skyPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        var gone = new SkyrimMod(new ModKey("HcErrGone", ModType.Master), SkyrimRelease.SkyrimSE);
        var goneRace = gone.Races.AddNew(); goneRace.EditorID = "HcErrGoneRace";
        gone.BeginWrite.ToPath(Path.Combine(outside, GoneName)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        var mod = new SkyrimMod(new ModKey("HcErrMod", ModType.Plugin), SkyrimRelease.SkyrimSE);
        for (int i = 0; i < 2; i++)
        { var n = mod.Npcs.AddNew(); n.EditorID = $"HcErrMod{i}"; n.Race.SetTo(deadFk); }
        mod.BeginWrite.ToPath(Path.Combine(modDir, ModName)).WithLoadOrder(new ISkyrimModGetter[] { sky }).Write();

        var patch = new SkyrimMod(new ModKey("HcErrPatch", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var pn = patch.Npcs.AddNew(); pn.EditorID = "HcErrPatchNpc"; pn.Race.SetTo(goneRace.FormKey);
        patch.BeginWrite.ToPath(Path.Combine(patchDir, PatchName))
             .WithLoadOrder(new ISkyrimModGetter[] { sky, gone }).Write();

        File.WriteAllBytes(Path.Combine(badDir, BadName), new byte[16]);

        File.WriteAllText(Path.Combine(profileDir, "loadorder.txt"),
            "# header\r\n" + BaseName + "\r\n" + ModName + "\r\n" + PatchName + "\r\n" + BadName + "\r\n");
        File.WriteAllText(Path.Combine(profileDir, "plugins.txt"),
            "*" + ModName + "\r\n*" + PatchName + "\r\n*" + BadName + "\r\n");
        File.WriteAllText(Path.Combine(profileDir, "modlist.txt"),
            "# header\r\n+ErrBad\r\n+ErrPatch\r\n+ErrMod\r\n+VanillaStub\r\n");

        var store = new UserConfigStore(Path.Combine(Root, "houseCARL.user.json"));
        Svc = LoadOrderService.WithInstance(Instance, 0, store);
    }

    public void Dispose()
    {
        Svc.Dispose();
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}

/// <summary>The world, built once for the class. Every arm over it is read-only.</summary>
public sealed class CheckErrorsWorldFixture : IDisposable
{
    public CheckErrorsWorld W { get; } = new();
    public void Dispose() => W.Dispose();
}
