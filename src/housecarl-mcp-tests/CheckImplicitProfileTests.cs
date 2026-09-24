using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A sweep's <c>exclude=["implicit"]</c> group is read from the profile the sweep captured, even when MO2
/// switches profile between that capture and the exclude parse.</summary>
[Trait("tier", "integration")]
public sealed class CheckImplicitProfileTests : IDisposable
{
    const string OtherProfile = "Other";

    readonly CheckWorld _w = new();
    readonly string _ini;

    public CheckImplicitProfileTests()
    {
        _ini = Path.Combine(_w.Instance, "ModOrganizer.ini");

        // A second profile with the same order and mods, whose plugins.txt lists Skyrim.esm: nothing is implicit
        // there, and the order is unchanged, so the switch re-derives the roots without swapping the resolver.
        var other = Path.Combine(_w.Instance, "profiles", OtherProfile);
        Directory.CreateDirectory(other);
        File.Copy(Path.Combine(_w.ProfileDir, "loadorder.txt"), Path.Combine(other, "loadorder.txt"));
        File.Copy(Path.Combine(_w.ProfileDir, "modlist.txt"), Path.Combine(other, "modlist.txt"));
        File.WriteAllText(Path.Combine(other, "plugins.txt"), "*" + _w.BaseMasterName + "\r\n*HcCeWireMod.esp\r\n");
    }

    public void Dispose() => _w.Dispose();

    /// <summary>Selects the Other profile and makes the head re-derive now, on this thread and outside any hold.</summary>
    void SwitchProfile()
    {
        File.WriteAllText(_ini, File.ReadAllText(_ini).Replace("selected_profile=@ByteArray(Default)",
                                                                 "selected_profile=@ByteArray(" + OtherProfile + ")"));
        _w.Svc.CaptureView();
    }

    /// <summary><c>exclude=["implicit"]</c> whose first read switches the profile. Every sweep reads it once after its
    /// capture and before the implicit group is read, so the switch lands exactly between the two.</summary>
    IReadOnlyList<string> ImplicitThenSwitch() => new SwitchOnFirstRead(SweepExclusion.ImplicitToken, SwitchProfile);

    static readonly string[] Implicit = { SweepExclusion.ImplicitToken };

    [Fact]
    public void TheErrorsSweepTakesTheImplicitGroupFromTheProfileItCaptured()
    {
        var during = _w.Svc.CheckErrors(null, 1000, exclude: ImplicitThenSwitch());
        Assert.Null(during.Error);
        Assert.Equal(1, during.PluginsScanned);   // the Default profile's implicit Skyrim.esm is left out

        var after = _w.Svc.CheckErrors(null, 1000, exclude: Implicit);
        Assert.Equal(2, after.PluginsScanned);    // the switch landed: Other has nothing implicit
    }

    [Fact]
    public void TheScriptsSweepTakesTheImplicitGroupFromTheProfileItCaptured()
    {
        var during = _w.Svc.ValidateScripts(null, 1000, exclude: ImplicitThenSwitch());
        Assert.Null(during.Error);
        Assert.Equal(1, during.PluginsScanned);

        var after = _w.Svc.ValidateScripts(null, 1000, exclude: Implicit);
        Assert.Equal(2, after.PluginsScanned);
    }

    [Fact]
    public void TheFaceGenSweepTakesTheImplicitGroupFromTheProfileItCaptured()
    {
        var during = _w.Svc.CheckFaceGen(null, 1000, exclude: ImplicitThenSwitch());
        Assert.Null(during.Error);
        Assert.Equal(2, during.NpcsScanned);      // HcCeWireMod's two NPCs; Skyrim.esm's three are left out

        var after = _w.Svc.CheckFaceGen(null, 1000, exclude: Implicit);
        Assert.Equal(5, after.NpcsScanned);
    }

    /// <summary>A one-item list that runs <paramref name="onFirstRead"/> the first time it is enumerated.</summary>
    sealed class SwitchOnFirstRead(string item, Action onFirstRead) : IReadOnlyList<string>
    {
        bool _fired;
        public int Count => 1;
        public string this[int index] => index == 0 ? item : throw new ArgumentOutOfRangeException(nameof(index));

        public IEnumerator<string> GetEnumerator()
        {
            if (!_fired) { _fired = true; onFirstRead(); }
            yield return item;
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
