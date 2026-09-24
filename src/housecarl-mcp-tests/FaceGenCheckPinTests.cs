using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The facegen sweep takes its index view, asset build and roots from one profile, even when MO2 switches profile mid-call.</summary>
[Trait("tier", "integration")]
public sealed class FaceGenCheckPinTests : IDisposable
{
    const string OtherProfile = "Other";

    readonly FaceGenWorld _w = new();
    readonly string _ini;

    public FaceGenCheckPinTests()
    {
        var instance = Path.Combine(_w.Root, "instance");
        _ini = Path.Combine(instance, "ModOrganizer.ini");

        // A second profile: the master alone, with only FgBase enabled. HcFgSplit's tint and HcFgStale's overhaul are gone there.
        var other = Path.Combine(instance, "profiles", OtherProfile);
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "loadorder.txt"), "# header\r\n" + FaceGenWorld.MasterName + "\r\n");
        File.WriteAllText(Path.Combine(other, "plugins.txt"), "*" + FaceGenWorld.MasterName + "\r\n");
        File.WriteAllText(Path.Combine(other, "modlist.txt"),
            "# header\r\n-" + FaceGenWorld.OverhaulMod + "\r\n-" + FaceGenWorld.OtherMod + "\r\n-" + FaceGenWorld.UpdateMod
            + "\r\n+" + FaceGenWorld.BaseMod + "\r\n");
        File.WriteAllText(Path.Combine(other, "Skyrim.ini"), "[Archive]\r\nsResourceArchiveList=\r\n");
    }

    public void Dispose()
    {
        _w.Svc.Dispose();
        _w.Dispose();
    }

    void SelectOtherProfile()
        => File.WriteAllText(_ini, File.ReadAllText(_ini).Replace("selected_profile=@ByteArray(Default)",
                                                                   "selected_profile=@ByteArray(" + OtherProfile + ")"));

    FaceGenCheckResult Sweep() => _w.Svc.CheckFaceGen(null, 1000);

    static string? ClassOf(FaceGenCheckResult r, string editorId)
        => r.Findings.FirstOrDefault(f => f.EditorId == editorId)?.Class;

    /// <summary>On a warm service, a profile switch right after the pin leaves the whole sweep on the first profile.</summary>
    [Fact]
    public void AProfileSwitchAfterThePinDoesNotSplitTheSweep()
    {
        var before = Sweep();                                                   // warms the index and the asset build
        Assert.Equal("split_bake", ClassOf(before, "HcFgSplit"));
        Assert.Equal("stale_bake", ClassOf(before, "HcFgStale"));
        _w.Svc.AfterCheckPinForGuard = SelectOtherProfile;

        var during = Sweep();
        _w.Svc.AfterCheckPinForGuard = null;

        // The Default profile's records and files: HcFgSplit's tint still comes from FgOther, not a missing half.
        Assert.Equal("split_bake", ClassOf(during, "HcFgSplit"));
        Assert.Equal("stale_bake", ClassOf(during, "HcFgStale"));
        Assert.Equal(before.Epoch, during.Epoch);

        // The switch landed for the next call: the Other profile's files lack the tint and its order the overhaul.
        var after = Sweep();
        Assert.Equal("tint_absent", ClassOf(after, "HcFgSplit"));
        Assert.Null(ClassOf(after, "HcFgStale"));
    }
}
