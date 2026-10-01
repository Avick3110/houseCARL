using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>Plugins in MO2's overwrite folder, where tool outputs land, resolve at the top of the order: an
/// overwrite-only plugin resolves without a stale-profile warning, an overwrite copy beats an enabled mod's copy, the
/// missing-plugin warning names overwrite only when overwrite was searched, and the service reads a record out of an
/// overwrite plugin once the profile lists it.</summary>
[Trait("tier", "integration")]
public sealed class OverwriteFolderResolveTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "hc-overwrite-resolve-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* temp cleanup best-effort */ } }

    /// <summary>A profile listing Skyrim.esm (in data), Dup.esp (in a mod AND overwrite), ToolOutput.esp (overwrite
    /// only) and Gone.esp (nowhere). Build maps paths and never opens a plugin, so the files are one byte each.</summary>
    (string Profile, string Mods, string Data, string Overwrite) StageCore()
    {
        var prof = Path.Combine(_root, "core", "profile");
        var mods = Path.Combine(_root, "core", "mods");
        var data = Path.Combine(_root, "core", "data");
        var ovw = Path.Combine(_root, "core", "overwrite");
        Directory.CreateDirectory(prof); Directory.CreateDirectory(Path.Combine(mods, "SomeMod"));
        Directory.CreateDirectory(data); Directory.CreateDirectory(ovw);
        File.WriteAllText(Path.Combine(data, "Skyrim.esm"), "x");
        File.WriteAllText(Path.Combine(mods, "SomeMod", "Dup.esp"), "x");
        File.WriteAllText(Path.Combine(ovw, "Dup.esp"), "x");
        File.WriteAllText(Path.Combine(ovw, "ToolOutput.esp"), "x");
        File.WriteAllText(Path.Combine(prof, "loadorder.txt"), "# header\r\nSkyrim.esm\r\nDup.esp\r\nToolOutput.esp\r\nGone.esp\r\n");
        File.WriteAllText(Path.Combine(prof, "plugins.txt"), "*Dup.esp\r\n*ToolOutput.esp\r\n*Gone.esp\r\n");
        File.WriteAllText(Path.Combine(prof, "modlist.txt"), "# header\r\n+SomeMod\r\n");
        return (prof, mods, data, ovw);
    }

    static string? PathOf(Mo2OrderResult r, string name) =>
        r.OrderedPaths.FirstOrDefault(p => Path.GetFileName(p).Equals(name, StringComparison.OrdinalIgnoreCase));

    static string? WarningFor(Mo2OrderResult r, string name) =>
        r.Warnings.FirstOrDefault(w => w.Contains(name, StringComparison.OrdinalIgnoreCase));

    // Probe: "an overwrite-only plugin resolves to its overwrite path", "…and raises no warning (it is not a
    // stale-profile problem)".
    [Fact]
    public void AnOverwriteOnlyPluginResolvesToItsOverwritePathWithoutAWarning()
    {
        var (prof, mods, data, ovw) = StageCore();
        var r = Mo2LoadOrder.Build(prof, mods, data, ovw);

        Assert.StartsWith(ovw, PathOf(r, "ToolOutput.esp"), StringComparison.OrdinalIgnoreCase);
        Assert.Null(WarningFor(r, "ToolOutput.esp"));
    }

    // Probe: "a name in overwrite AND an enabled mod resolves to the OVERWRITE copy (top of the VFS)".
    [Fact]
    public void ANameInOverwriteAndAnEnabledModResolvesToTheOverwriteCopy()
    {
        var (prof, mods, data, ovw) = StageCore();
        Assert.StartsWith(ovw, PathOf(Mo2LoadOrder.Build(prof, mods, data, ovw), "Dup.esp"), StringComparison.OrdinalIgnoreCase);
    }

    // Probe: "a genuinely-missing plugin still warns", "…and the warning names the overwrite folder among the places
    // searched"; explicit mode: "a missing plugin still warns", "…and the warning does NOT name the overwrite folder".
    [Fact]
    public void AMissingPluginsWarningNamesOverwriteOnlyWhenOverwriteWasSearched()
    {
        var (prof, mods, data, ovw) = StageCore();

        Assert.Contains("overwrite", WarningFor(Mo2LoadOrder.Build(prof, mods, data, ovw), "Gone.esp"), StringComparison.OrdinalIgnoreCase);
        var explicitMode = WarningFor(Mo2LoadOrder.Build(prof, mods, data, ""), "Gone.esp");
        Assert.NotNull(explicitMode);
        Assert.DoesNotContain("overwrite", explicitMode, StringComparison.OrdinalIgnoreCase);
    }

    // Probe: "baseline order resolved (overwrite plugin not yet in the profile)", "the overwrite-resident plugin resolves
    // once the profile lists it", "no warning raised for it", "a record inside the overwrite plugin reads end-to-end",
    // "the setup confirmation lists the overwrite root among the derived roots (hunt F9-4)".
    [Fact]
    public void TheServiceReadsARecordFromAnOverwritePluginOnceTheProfileListsIt()
    {
        var instance = Path.Combine(_root, "instance");
        var profiles = Path.Combine(instance, "profiles", "Default");
        var mods = Path.Combine(instance, "mods");
        var ovw = Path.Combine(instance, "overwrite");
        Directory.CreateDirectory(profiles); Directory.CreateDirectory(Path.Combine(mods, "MasterMod"));
        Directory.CreateDirectory(ovw); Directory.CreateDirectory(Path.Combine(_root, "game", "Data"));
        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(_root, "game").Replace(@"\", @"\\") + ")\r\n");

        var mKey = new ModKey("HcOvwMaster", ModType.Master);
        var m = new SkyrimMod(mKey, SkyrimRelease.SkyrimSE);
        m.Weapons.AddNew().EditorID = "HcOvwBase";
        m.BeginWrite.ToPath(Path.Combine(mods, "MasterMod", mKey.FileName.String)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        var tKey = new ModKey("HcOvwTool", ModType.Plugin);
        var t = new SkyrimMod(tKey, SkyrimRelease.SkyrimSE);
        var toolWeapon = t.Weapons.AddNew();
        toolWeapon.EditorID = "HcOvwToolW";
        t.BeginWrite.ToPath(Path.Combine(ovw, tKey.FileName.String)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        File.WriteAllText(Path.Combine(profiles, "loadorder.txt"), "# header\r\n" + mKey.FileName + "\r\n");
        File.WriteAllText(Path.Combine(profiles, "plugins.txt"), "*" + mKey.FileName + "\r\n");
        File.WriteAllText(Path.Combine(profiles, "modlist.txt"), "# header\r\n+MasterMod\r\n");

        using var svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(_root, "user.json")));
        Assert.Equal(1, svc.Stats().plugins);

        // MO2 refreshes the profile after the tool wrote to overwrite; the mtime is pushed so the change is seen by value.
        File.WriteAllText(Path.Combine(profiles, "loadorder.txt"), "# header\r\n" + mKey.FileName + "\r\n" + tKey.FileName + "\r\n");
        File.WriteAllText(Path.Combine(profiles, "plugins.txt"), "*" + mKey.FileName + "\r\n*" + tKey.FileName + "\r\n");
        File.SetLastWriteTimeUtc(Path.Combine(profiles, "loadorder.txt"), DateTime.UtcNow.AddHours(1));
        File.SetLastWriteTimeUtc(Path.Combine(profiles, "plugins.txt"), DateTime.UtcNow.AddHours(1));

        var status = svc.StatusData();
        Assert.Equal(2, status.ResolvedPluginCount);
        Assert.Empty(status.Warnings);

        var read = svc.ReadArea.ResolveRead(toolWeapon.FormKey, null, null, conflictTree: false);
        Assert.Null(read.Error);
        Assert.Equal(tKey.FileName.String, read.WinnerPlugin);

        Assert.Contains(ovw, SetupTools.Render(Mo2Instance.Resolve(instance), persisted: true, persistError: null, persistNote: null),
            StringComparison.OrdinalIgnoreCase);
    }
}
