using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The four lanes #827 did not reach, each of which hedged that "a BSA or a loose mod folder failed to read"
/// and named no folder: the facegen sweep's head, the scripts sweep's head, the per-property ".pex not on disk" reason,
/// and the write lane's carry and coverage notes. Each now names the mod folder it could not read (#850), so the hedge
/// leaves the modder something to act on.</summary>
[Trait("tier", "integration")]
public sealed class UnreadableRootNamedLanesTests : IDisposable
{
    readonly BlockedSweepWorld _w = new();

    public void Dispose() => _w.Dispose();

    /// <summary>The named-root line, as the lane writes it — asserting on the shared lead rather than the mod name
    /// alone, so a name that reached the response some other way cannot pass this test.</summary>
    static string Named(string mod) => BatchRender.RootFailureLead + mod;

    string Sweep(params string[] findings)
        => CheckTools.CheckTool(_w.Svc, findings: findings, max_chars: 60000);

    [Fact]
    public void TheFacegenSweepHeadNamesTheRootItCouldNotRead()
    {
        Assert.True(_w.Blocked, BlockedSweepWorld.NotStaged);

        var text = Sweep("facegen");

        Assert.Contains("failed to read this build", text, StringComparison.Ordinal);
        Assert.Contains(Named(BlockedSweepWorld.BlockedMod), text, StringComparison.Ordinal);
    }

    /// <summary>The json twin of the head's caveat: the same roots, under a named array, so a caller branching on
    /// read_incomplete can say WHICH folder rather than only that one failed.</summary>
    [Fact]
    public void TheFacegenSweepsJsonHeadNamesItToo()
    {
        Assert.True(_w.Blocked, BlockedSweepWorld.NotStaged);

        var json = CheckTools.CheckTool(_w.Svc, findings: new[] { "facegen" }, format: "json", max_chars: 60000);

        Assert.Contains("root_read_failures", json, StringComparison.Ordinal);
        Assert.Contains(BlockedSweepWorld.BlockedMod, json, StringComparison.Ordinal);
    }

    [Fact]
    public void TheScriptsSweepHeadNamesTheRootItCouldNotRead()
    {
        Assert.True(_w.Blocked, BlockedSweepWorld.NotStaged);

        var text = Sweep("scripts");

        Assert.Contains(".pex not on disk", text, StringComparison.Ordinal);
        Assert.Contains(Named(BlockedSweepWorld.BlockedMod), text, StringComparison.Ordinal);
    }

    /// <summary>The per-property reason inside the scripts listing has no caveat block above it to point at, so it
    /// carries the root in its own sentence.</summary>
    [Fact]
    public void ThePerPropertyReasonNamesTheRootItCouldNotRead()
    {
        Assert.True(_w.Blocked, BlockedSweepWorld.NotStaged);

        var text = Sweep("scripts");

        Assert.Contains("is not on disk", text, StringComparison.Ordinal);
        Assert.Contains("the loose root that would not read: " + BlockedSweepWorld.BlockedMod, text,
                        StringComparison.Ordinal);
    }

    /// <summary>The write lane's facegen and voice carry notes, off a real compact over a blocked tree.</summary>
    [Fact]
    public void TheCarryNotesNameTheRootTheyCouldNotRead()
    {
        using var w = new BlockedCarryWorld();
        Assert.True(w.Blocked, BlockedSweepWorld.NotStaged);

        var text = WriteTools.RenderCompact(w.Svc.CompactPlugin(BlockedCarryWorld.PluginName));

        // Both notes hedge on the same scan, so each names the root under its own hedge, not once for the pair.
        int facegen = text.IndexOf("'no facegen' result may be incomplete", StringComparison.Ordinal);
        int voice = text.IndexOf("'no voice' result may be incomplete", StringComparison.Ordinal);
        Assert.True(facegen >= 0 && voice > facegen, text);
        Assert.Contains(Named(BlockedCarryWorld.BlockedMod), text[facegen..voice], StringComparison.Ordinal);
        Assert.Contains(Named(BlockedCarryWorld.BlockedMod), text[voice..], StringComparison.Ordinal);
    }

    /// <summary>The create lane's two coverage reports. The checks run over a mods tree with one denied folder, so the
    /// root each report names is one it really could not read, not a hand-built list.</summary>
    [Fact]
    public void TheCreateCoverageNotesNameTheRootTheyCouldNotRead()
    {
        using var f = new BlockedReportFixture();
        Assert.True(f.Blocked, BlockedSweepWorld.NotStaged);

        var outcome = new WritePatchBuilder.CreateOutcome(
            true, null, f.PatchPath, false,
            new[] { new WritePatchBuilder.CreatedRecord(f.InfoKey, "DialogResponses", "HcRootInfo",
                                                        Array.Empty<WritePatchBuilder.OpResult>()) },
            Array.Empty<string>(), 512)
            { Voice = f.Voice, ScriptBinding = f.ScriptBinding };

        Assert.NotEmpty(f.Voice.RootFailures);
        Assert.NotEmpty(f.ScriptBinding.RootFailures);
        var text = WriteTools.RenderCreate(outcome, maxChars: 60000);

        int result = text.IndexOf("result-script coverage", StringComparison.Ordinal);
        int voice = text.IndexOf("voice coverage", StringComparison.Ordinal);
        Assert.True(voice >= 0 && result > voice, text);
        Assert.Contains(Named(BlockedReportFixture.BlockedMod), text[voice..result], StringComparison.Ordinal);
        Assert.Contains(Named(BlockedReportFixture.BlockedMod), text[result..], StringComparison.Ordinal);
    }

}

/// <summary>Its own instance, never a shared fixture: it denies the current account one whole mod folder. The order
/// carries an NPC, so the facegen sweep scans, and a weapon bound to a script class no mod compiles, so the scripts
/// sweep reaches the per-property ".pex not on disk" reason.</summary>
sealed class BlockedSweepWorld : IDisposable
{
    public const string BlockedMod = "RootBlockedSweepMod";
    public const string NotStaged = "the deny ACE did not bite on this host, so the unreadable root was never staged";

    public string Root { get; }
    public LoadOrderService Svc { get; }
    public bool Blocked { get; }

    readonly string _blockedDir;

    public BlockedSweepWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-rootsweep-" + Guid.NewGuid().ToString("N"));
        var instance = Path.Combine(Root, "instance");
        var profile = Path.Combine(instance, "profiles", "Default");
        var mods = Path.Combine(instance, "mods");
        var pluginMod = Path.Combine(mods, "SweepPluginMod");
        var assetMod = Path.Combine(mods, "SweepAssetMod");
        _blockedDir = Path.Combine(mods, BlockedMod);
        foreach (var d in new[] { profile, Path.Combine(Root, "game", "Data"), pluginMod, assetMod })
            Directory.CreateDirectory(d);

        // One master with the two records the sweeps need: an NPC for the facegen join, and a weapon whose attached
        // script class is compiled nowhere, which is what makes the .pex read fail and carry its reason.
        var key = new ModKey("HcRootSweep", ModType.Master);
        var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        var race = mod.Races.AddNew();
        race.EditorID = "HcRootSweepRace";
        race.Flags |= Race.Flag.FaceGenHead;
        var npc = mod.Npcs.AddNew();
        npc.EditorID = "HcRootSweepNpc";
        npc.Race.SetTo(race);
        var weapon = mod.Weapons.AddNew();
        weapon.EditorID = "HcRootSweepWeapon";
        var vmad = new VirtualMachineAdapter();
        vmad.Scripts.Add(new ScriptEntry { Name = "HcRootSweepUncompiled" });
        weapon.VirtualMachineAdapter = vmad;
        mod.BeginWrite.ToPath(Path.Combine(pluginMod, key.FileName.String))
           .WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        // A readable mod provides each layer too, so no lane answers "nothing here" instead of scanning.
        Write(assetMod, @"Scripts\readable.pex", "not a real pex");
        Write(assetMod, FaceGenPath.For(npc.FormKey, FaceGenSlot.Mesh), "mesh");

        // The blocked mod's content is written FIRST: from the deny on, the folder cannot be touched.
        Write(_blockedDir, @"Scripts\blocked.pex", "x");
        Write(_blockedDir, FaceGenPath.For(npc.FormKey, FaceGenSlot.Tint), "tint");

        // From the deny on, anything that throws would leave the ACE behind and block the temp tree's own cleanup.
        try
        {
            Blocked = DenyAce.TryDeny(_blockedDir);
            Svc = Stage(instance, profile, key.FileName.String);
        }
        catch
        {
            DenyAce.Undeny(_blockedDir);
            throw;
        }
    }

    static void Write(string modDir, string rel, string text)
    {
        var path = Path.Combine(modDir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    LoadOrderService Stage(string instance, string profile, string pluginFile)
    {
        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");
        File.WriteAllText(Path.Combine(profile, "loadorder.txt"), "# header\r\n" + pluginFile + "\r\n");
        File.WriteAllText(Path.Combine(profile, "plugins.txt"), "*" + pluginFile + "\r\n");
        File.WriteAllText(Path.Combine(profile, "modlist.txt"),
            "# header\r\n+" + BlockedMod + "\r\n+SweepAssetMod\r\n+SweepPluginMod\r\n");
        File.WriteAllText(Path.Combine(profile, "Skyrim.ini"), "[Archive]\r\nsResourceArchiveList=\r\n");

        return LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(Root, "houseCARL.user.json")));
    }

    public void Dispose()
    {
        Svc.Dispose();
        DenyAce.Undeny(_blockedDir);
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}

/// <summary>The compact lane's world: one ESP with an NPC to renumber, so the facegen and voice carry passes both run
/// and both hedge, plus one denied mod folder for them to name.</summary>
sealed class BlockedCarryWorld : IDisposable
{
    public const string BlockedMod = "RootBlockedCarryMod";
    public const string PluginName = "HcRootCarry.esp";

    public string Root { get; }
    public LoadOrderService Svc { get; }
    public bool Blocked { get; }

    readonly string _blockedDir;

    public BlockedCarryWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-rootcarry-" + Guid.NewGuid().ToString("N"));
        var instance = Path.Combine(Root, "instance");
        var profile = Path.Combine(instance, "profiles", "Default");
        var mods = Path.Combine(instance, "mods");
        var faceMod = Path.Combine(mods, "CarryFaceMod");
        _blockedDir = Path.Combine(mods, BlockedMod);
        foreach (var d in new[] { profile, Path.Combine(Root, "game", "Data"), faceMod })
            Directory.CreateDirectory(d);

        // An id above the ESL ceiling, so the compact really renumbers it and the carry passes have work to consider.
        var key = ModKey.FromFileName(PluginName);
        var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        mod.Npcs.Add(new Npc(new FormKey(key, 0xA10), SkyrimRelease.SkyrimSE) { EditorID = "HcRootCarryNpc" });
        mod.BeginWrite.ToPath(Path.Combine(faceMod, PluginName))
           .WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        var blockedFile = Path.Combine(_blockedDir, "Scripts", "blocked.pex");
        Directory.CreateDirectory(Path.GetDirectoryName(blockedFile)!);
        File.WriteAllText(blockedFile, "x");

        try
        {
            Blocked = DenyAce.TryDeny(_blockedDir);
            File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
                "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
                + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");
            File.WriteAllText(Path.Combine(profile, "loadorder.txt"), "# header\r\n" + PluginName + "\r\n");
            File.WriteAllText(Path.Combine(profile, "plugins.txt"), "*" + PluginName + "\r\n");
            File.WriteAllText(Path.Combine(profile, "modlist.txt"),
                "# header\r\n+" + BlockedMod + "\r\n+CarryFaceMod\r\n");
            File.WriteAllText(Path.Combine(profile, "Skyrim.ini"), "[Archive]\r\nsResourceArchiveList=\r\n");
            Svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(Root, "houseCARL.user.json")));
            Svc.Stats();
        }
        catch
        {
            DenyAce.Undeny(_blockedDir);
            throw;
        }
    }

    public void Dispose()
    {
        Svc.Dispose();
        DenyAce.Undeny(_blockedDir);
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}

/// <summary>The create lane's two coverage checks, run for real over a mods tree with one denied folder. No MO2
/// instance: both checks take a resolver and an asset resolver, which is all a created dialogue line needs checking.</summary>
sealed class BlockedReportFixture : IDisposable
{
    public const string BlockedMod = "RootBlockedReportMod";

    public string Root { get; }
    public string PatchPath { get; }
    public FormKey InfoKey { get; }
    public bool Blocked { get; }
    public VoiceReport Voice { get; }
    public ScriptBindingReport ScriptBinding { get; }

    readonly string _blockedDir;

    public BlockedReportFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-rootreport-" + Guid.NewGuid().ToString("N"));
        var mods = Path.Combine(Root, "mods");
        var dataDir = Path.Combine(Root, "Data");
        _blockedDir = Path.Combine(mods, BlockedMod);
        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(Path.Combine(_blockedDir, "Scripts"));
        File.WriteAllText(Path.Combine(_blockedDir, "Scripts", "blocked.pex"), "x");

        // A patch holding one topic and one scripted response, written and re-opened the way each check sees a real one.
        var key = new ModKey("HcRootReport", ModType.Plugin);
        PatchPath = Path.Combine(Root, key.FileName.String);
        var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        var topic = mod.DialogTopics.AddNew();
        topic.EditorID = "HcRootTopic";
        var info = new DialogResponses(mod.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcRootInfo" };
        var vmad = new DialogResponsesAdapter();
        vmad.Scripts.Add(new ScriptEntry { Name = "HcRootUncompiled" });
        info.VirtualMachineAdapter = vmad;
        // One spoken response, or the voice check has no line to verdict and writes no block at all.
        info.Responses.Add(new DialogResponse { ResponseNumber = 1 });
        topic.Responses.Add(info);
        mod.BeginWrite.ToPath(PatchPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        InfoKey = info.FormKey;

        try
        {
            Blocked = DenyAce.TryDeny(_blockedDir);
            var created = new[] { new WritePatchBuilder.CreatedRecord(InfoKey, "DialogResponses", "HcRootInfo",
                                                                     Array.Empty<WritePatchBuilder.OpResult>()) };
            using var resolver = LoadOrderResolver.Build(new[] { PatchPath });
            using var assets = AssetResolver.Build("", mods, dataDir, new[] { BlockedMod },
                                                   Array.Empty<ActiveArchive>());
            // The binding check resolves a .pex, which is the read that finds the blocked root; the voice check
            // reads the SAME asset build, whose failures are kept for its life, so it runs after it.
            ScriptBinding = DialogueScriptCheck.Run(PatchPath, created, assets);
            Voice = VoiceCheck.Run(PatchPath, created, resolver, assets);
        }
        catch
        {
            DenyAce.Undeny(_blockedDir);
            throw;
        }
    }

    public void Dispose()
    {
        DenyAce.Undeny(_blockedDir);
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}
