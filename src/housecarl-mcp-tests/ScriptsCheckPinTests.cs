using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The script sweep takes its index view, asset build and roots from one profile, even when MO2 switches profile mid-call.</summary>
[Trait("tier", "integration")]
public sealed class ScriptsCheckPinTests : IDisposable
{
    const string OtherProfile = "Other";
    const string OverrideMod = "ScriptsBrokenOverride";

    readonly ScriptsWorld _w = new();
    readonly string _ini;

    public ScriptsCheckPinTests()
    {
        _ini = Path.Combine(_w.Instance, "ModOrganizer.ini");

        // A second profile: the same plugin, with a higher-priority mod whose HcSpChild.pex does not parse.
        var scripts = Path.Combine(_w.ModsDir, OverrideMod, "Scripts");
        Directory.CreateDirectory(scripts);
        File.WriteAllText(Path.Combine(scripts, ScriptsWorld.ChildScript + ".pex"), "not a pex");
        var other = Path.Combine(_w.Instance, "profiles", OtherProfile);
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "loadorder.txt"), "# header\r\n" + _w.PluginName + "\r\n");
        File.WriteAllText(Path.Combine(other, "plugins.txt"), "*" + _w.PluginName + "\r\n");
        File.WriteAllText(Path.Combine(other, "modlist.txt"), "# header\r\n+" + OverrideMod + "\r\n+ScriptsMod\r\n");
    }

    public void Dispose() => _w.Dispose();

    void SelectOtherProfile()
        => File.WriteAllText(_ini, File.ReadAllText(_ini).Replace("selected_profile=@ByteArray(Default)",
                                                                   "selected_profile=@ByteArray(" + OtherProfile + ")"));

    RecordScriptFindings Footgun(ScriptCheckResult r)
    {
        Assert.True(r.Success, r.Error);
        return Assert.Single(r.Reports, x => x.Record == _w.Footgun);
    }

    /// <summary>On a warm service, a profile switch right after the pin leaves the whole sweep on the first profile.</summary>
    [Fact]
    public void AProfileSwitchAfterThePinDoesNotSplitTheSweep()
    {
        var before = Footgun(_w.Svc.ValidateScripts(null, 1000));             // warms the index and the asset build
        Assert.Empty(before.Unverifiable);
        Assert.NotEmpty(before.Unbound);
        _w.Svc.CheckArea.AfterCheckPinForGuard = SelectOtherProfile;

        var during = Footgun(_w.Svc.ValidateScripts(null, 1000));
        _w.Svc.CheckArea.AfterCheckPinForGuard = null;

        // The Default profile's files: the footgun's script still reads from ScriptsMod's good .pex.
        Assert.Empty(during.Unverifiable);
        Assert.Equal(before.Unbound.Count, during.Unbound.Count);

        // The switch landed for the next call: the Other profile's broken .pex wins, so the script cannot be read.
        var after = Footgun(_w.Svc.ValidateScripts(null, 1000));
        Assert.NotEmpty(after.Unverifiable);
    }
}
