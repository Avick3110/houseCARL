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

    /// <summary>The second reason the flag can be dropped, at the render seam: every donor was light but the ids did
    /// not fit, which is the one case compact cannot rescue — so the report must not send the caller there.</summary>
    [Fact]
    public void AllDonorsLightButTheIdsOverflowingReportsTheWindowAndOffersNoCompact()
    {
        var o = _w.Svc.MergePlugins(new[] { MergeLightCarryWorld.LightA, MergeLightCarryWorld.LightB }, "HcLcOverflowRender.esp");
        Assert.True(o.Success, o.Error);
        // The same outcome, re-stated as the overflow shape: both donors light, the flag not carried.
        var overflowed = o with { LightCarried = false };

        var rendered = WriteTools.RenderMerge(overflowed);

        Assert.Contains("Every donor was light, but the merged object ids do not all fit the light window", rendered);
        Assert.DoesNotContain("Want it light?", rendered);
        Assert.Contains("cannot make it light either", rendered);
    }
}

/// <summary>A synthetic MO2 instance for the merge light-carry arms: two light donors (one by header flag, one by the
/// .esl extension with the bit unset) whose object ids sit inside the light window, one full plugin, and one master.
/// Every donor originates a single weapon, so a merge of any pair keeps every id where it is.</summary>
public sealed class MergeLightCarryWorld : IDisposable
{
    public string Root { get; }
    public LoadOrderService Svc { get; }

    public const string LightA = "HcLcLightA.esp";     // header bit set
    public const string LightB = "HcLcLightB.esl";     // .esl extension, bit unset
    public const string Full = "HcLcFull.esp";
    public const string Master = "HcLcMaster.esm";

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

        var order = new[] { Master, LightA, LightB, Full };
        File.WriteAllText(Path.Combine(profile, "loadorder.txt"), "# header\r\n" + string.Join("\r\n", order) + "\r\n");
        File.WriteAllText(Path.Combine(profile, "plugins.txt"), string.Join("\r\n", order.Select(p => "*" + p)) + "\r\n");
        File.WriteAllText(Path.Combine(profile, "modlist.txt"),
            "# header\r\n+MasterMod\r\n+FullMod\r\n+LightBMod\r\n+LightAMod\r\n");

        Svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(Root, "houseCARL.user.json")));
    }

    static void Write(string mods, string folder, ModKey key, uint id, bool light)
    {
        var dir = Path.Combine(mods, folder);
        Directory.CreateDirectory(dir);
        var m = new SkyrimMod(key, SkyrimRelease.SkyrimSE) { IsSmallMaster = light };
        m.Weapons.Add(new Weapon(new FormKey(key, id), SkyrimRelease.SkyrimSE) { EditorID = key.Name + "Weap" });
        m.BeginWrite.ToPath(Path.Combine(dir, key.FileName.String)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
    }

    public void Dispose()
    {
        Svc.Dispose();
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}
