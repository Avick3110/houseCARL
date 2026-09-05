using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The LIGHT (ESL) header flag on a merge (#363): carried when every donor was light and the merged ids fit
/// the light window, dropped otherwise — and the report says which happened, and why, either way.</summary>
[Trait("tier", "integration")]
public sealed class MergeLightCarryTests : IClassFixture<MergeLightCarryWorld>
{
    readonly MergeLightCarryWorld _w;
    public MergeLightCarryTests(MergeLightCarryWorld w) => _w = w;

    static bool WrittenLight(WritePatchBuilder.MergeOutcome o)
    {
        using var m = SkyrimMod.CreateFromBinaryOverlay(o.OutputPath, SkyrimRelease.SkyrimSE);
        return m.IsSmallMaster;
    }

    /// <summary>Two light donors whose merged ids stay inside 0x800–0xFFF: the output is written LIGHT, so the merge
    /// does not cost a load-order slot the donors never cost.</summary>
    [Fact]
    public void EveryDonorLightAndTheIdsFittingCarriesTheLightFlagOntoTheOutput()
    {
        var o = _w.Svc.MergePlugins(new[] { MergeLightCarryWorld.LightA, MergeLightCarryWorld.LightB }, "HcLcBothLight.esp");

        Assert.True(o.Success, o.Error);
        Assert.True(o.LightCarried);
        Assert.True(WrittenLight(o));
        Assert.Contains("is written LIGHT too", WriteTools.RenderMerge(o));
    }

    /// <summary>One full donor in the set and the flag is dropped — merged content that was never light-legal as a
    /// whole cannot be carried as light — with the drop and its reason both stated.</summary>
    [Fact]
    public void OneFullDonorDropsTheLightFlagAndTheReportSaysThatIsWhy()
    {
        var o = _w.Svc.MergePlugins(new[] { MergeLightCarryWorld.LightA, MergeLightCarryWorld.Full }, "HcLcMixed.esp");

        Assert.True(o.Success, o.Error);
        Assert.False(o.LightCarried);
        Assert.False(WrittenLight(o));
        var rendered = WriteTools.RenderMerge(o);
        Assert.Contains("carried the LIGHT (ESL) status", rendered);
        Assert.Contains("Not every donor was light (1 of 2 were)", rendered);
    }

    /// <summary>A donor set with no light donor at all claims nothing about the light flag either way — there is no
    /// drop to report.</summary>
    [Fact]
    public void NoLightDonorRaisesNoLightNoteAtAll()
    {
        var o = _w.Svc.MergePlugins(new[] { MergeLightCarryWorld.Full }, "HcLcFullRenamed.esp");

        Assert.True(o.Success, o.Error);
        Assert.False(o.LightCarried);
        Assert.DoesNotContain("LIGHT (ESL) status", WriteTools.RenderMerge(o));
    }

    /// <summary>The MASTER (ESM) flag is never carried, and the drop is always said — the other half of the ruling.</summary>
    [Fact]
    public void TheMasterFlagIsNeverCarriedAndTheDropIsSaid()
    {
        var o = _w.Svc.MergePlugins(new[] { MergeLightCarryWorld.Master }, "HcLcMasterRenamed.esp");

        Assert.True(o.Success, o.Error);
        using (var m = SkyrimMod.CreateFromBinaryOverlay(o.OutputPath, SkyrimRelease.SkyrimSE))
            Assert.False(m.ModHeader.Flags.HasFlag(SkyrimModHeader.HeaderFlag.Master));
        Assert.Contains("carried MASTER status", WriteTools.RenderMerge(o));
    }

    /// <summary>The second reason the flag can be dropped, end to end: every donor is light, but one of them defines
    /// an id above the light ceiling, which the merge keeps where it is. Two records, so compact IS the remedy — the
    /// report must not blame a record count the merge does not have.</summary>
    [Fact]
    public void AllDonorsLightWithAnIdAboveTheCeilingDropsTheFlagAndStillOffersCompact()
    {
        var o = _w.Svc.MergePlugins(new[] { MergeLightCarryWorld.LightA, MergeLightCarryWorld.LightHighId }, "HcLcHighId.esp");

        Assert.True(o.Success, o.Error);
        Assert.False(o.LightCarried);
        Assert.False(WrittenLight(o));
        var rendered = WriteTools.RenderMerge(o);
        Assert.Contains("Every donor was light, but not every merged object id landed inside the light window", rendered);
        Assert.DoesNotContain("Not every donor was light", rendered);
        Assert.Contains("To make it light run", rendered);
        Assert.DoesNotContain("cannot make it light either", rendered);
    }

    /// <summary>The merged plugin defines more records than a light plugin holds, so compact would refuse — the report
    /// says that instead of sending the caller to a tool that cannot help.</summary>
    [Fact]
    public void TooManyRecordsForTheWindowSaysCompactCannotRescueIt()
    {
        var o = _w.Svc.MergePlugins(new[] { MergeLightCarryWorld.LightA, MergeLightCarryWorld.Crowd }, "HcLcCrowded.esp");

        Assert.True(o.Success, o.Error);
        Assert.False(o.LightCarried);
        Assert.Equal(MergeLightCarryWorld.CrowdRecords + 1, o.OriginatingRecords);
        var rendered = WriteTools.RenderMerge(o);
        Assert.Contains("Not every donor was light (1 of 2 were)", rendered);
        Assert.Contains("cannot make it light either", rendered);
        Assert.DoesNotContain("To make it light run", rendered);
    }
}

/// <summary>A synthetic MO2 instance for the merge light-carry arms: two light donors (one by header flag, one by the
/// .esl extension with the bit unset) whose object ids sit inside the light window, a third light donor whose single
/// id sits ABOVE the light ceiling, one full plugin, one full plugin with more records than the light window holds,
/// and one master. Every donor but the crowded one originates a single weapon, so a merge of any pair of those keeps
/// every id where it is.</summary>
public sealed class MergeLightCarryWorld : IDisposable
{
    public string Root { get; }
    public LoadOrderService Svc { get; }

    public const string LightA = "HcLcLightA.esp";     // header bit set
    public const string LightB = "HcLcLightB.esl";     // .esl extension, bit unset
    public const string LightHighId = "HcLcLightC.esl";// .esl extension, one record at 0x5000 — outside the light window
    public const string Full = "HcLcFull.esp";
    public const string Crowd = "HcLcCrowd.esp";       // more originating records than the light window's 2048 ids
    public const string Master = "HcLcMaster.esm";

    /// <summary>One more than the light window holds, so any merge including this donor overflows it.</summary>
    public const int CrowdRecords = 2049;

    public MergeLightCarryWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-merge-light-" + Guid.NewGuid().ToString("N"));
        var instance = Path.Combine(Root, "instance");
        var profile = Path.Combine(instance, "profiles", "Default");
        var mods = Path.Combine(instance, "mods");
        foreach (var d in new[] { profile, mods, Path.Combine(Root, "game", "Data") }) Directory.CreateDirectory(d);

        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");

        Write(mods, "LightAMod", new ModKey("HcLcLightA", ModType.Plugin), 0x801, light: true);
        Write(mods, "LightBMod", new ModKey("HcLcLightB", ModType.Light), 0x802, light: false);
        Write(mods, "FullMod", new ModKey("HcLcFull", ModType.Plugin), 0x803, light: false);
        Write(mods, "MasterMod", new ModKey("HcLcMaster", ModType.Master), 0x804, light: false);
        Write(mods, "CrowdMod", new ModKey("HcLcCrowd", ModType.Plugin), 0x801, light: false, count: CrowdRecords);
        // Written under the .esp ModKey and renamed, because Mutagen refuses to serialize an id above the ESL ceiling
        // under a light ModKey. A plugin file carries no copy of its own name, so the renamed file reads back as the
        // .esl the engine force-treats as light — which is the donor shape this arm needs.
        WriteRenamed(mods, "LightCMod", new ModKey("HcLcLightC", ModType.Plugin), 0x5000, LightHighId);

        var order = new[] { Master, LightA, LightB, LightHighId, Full, Crowd };
        File.WriteAllText(Path.Combine(profile, "loadorder.txt"), "# header\r\n" + string.Join("\r\n", order) + "\r\n");
        File.WriteAllText(Path.Combine(profile, "plugins.txt"), string.Join("\r\n", order.Select(p => "*" + p)) + "\r\n");
        File.WriteAllText(Path.Combine(profile, "modlist.txt"),
            "# header\r\n+MasterMod\r\n+CrowdMod\r\n+FullMod\r\n+LightCMod\r\n+LightBMod\r\n+LightAMod\r\n");

        Svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(Root, "houseCARL.user.json")));
    }

    static string Write(string mods, string folder, ModKey key, uint id, bool light, int count = 1)
    {
        var dir = Path.Combine(mods, folder);
        Directory.CreateDirectory(dir);
        var m = new SkyrimMod(key, SkyrimRelease.SkyrimSE) { IsSmallMaster = light };
        for (int i = 0; i < count; i++)
            m.Weapons.Add(new Weapon(new FormKey(key, id + (uint)i), SkyrimRelease.SkyrimSE) { EditorID = key.Name + "Weap" + i });
        var path = Path.Combine(dir, key.FileName.String);
        m.BeginWrite.ToPath(path).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        return path;
    }

    static void WriteRenamed(string mods, string folder, ModKey key, uint id, string fileName)
        => File.Move(Write(mods, folder, key, id, light: false), Path.Combine(mods, folder, fileName));

    public void Dispose()
    {
        Svc.Dispose();
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}
