using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A plugin ticked in <c>plugins.txt</c> whose only copy sits in a mod folder <c>modlist.txt</c> has
/// switched off (#957): MO2's VFS does not serve the file, so the game does not load it whatever the tick says.</summary>
public sealed class TickedPluginSwitchedOffModWorld : IDisposable
{
    public string Root { get; }
    public string OffName => "HcTsOff.esp";
    public string OffMod => "HcTsOffMod";
    /// <summary>Ticked, and no copy anywhere in the install.</summary>
    public string GoneName => "HcTsGone.esp";
    /// <summary>An implicit master (in loadorder.txt, absent from plugins.txt) whose only copy is in the switched-off folder.</summary>
    public string OffMasterName => "HcTsOffMaster.esm";
    /// <summary>An implicit master whose only copy is in a mods folder modlist.txt does not list.</summary>
    public string UnlistedMasterName => "HcTsUnlistedMaster.esm";
    /// <summary>An implicit master with no copy anywhere.</summary>
    public string GoneMasterName => "HcTsGoneMaster.esm";
    public string Instance { get; }
    public LoadOrderService Svc { get; }
    public ToolPathResolver Tools { get; }

    public TickedPluginSwitchedOffModWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-ticked-switched-off-" + Guid.NewGuid().ToString("N"));
        var instance = Instance = Path.Combine(Root, "instance");
        var profileDir = Path.Combine(instance, "profiles", "Default");
        var otherProfileDir = Path.Combine(instance, "profiles", "Other");
        Directory.CreateDirectory(otherProfileDir);
        var mods = Path.Combine(instance, "mods");
        Directory.CreateDirectory(profileDir);
        Directory.CreateDirectory(Path.Combine(Root, "game", "Data"));
        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");

        var vanillaDir = Path.Combine(mods, "VanillaStub");
        var offDir = Path.Combine(mods, OffMod);
        foreach (var d in new[] { vanillaDir, offDir }) Directory.CreateDirectory(d);

        var sky = new SkyrimMod(new ModKey("Skyrim", ModType.Master), SkyrimRelease.SkyrimSE);
        var race = sky.Races.AddNew(); race.EditorID = "HcTsVanillaRace";
        sky.BeginWrite.ToPath(Path.Combine(vanillaDir, "Skyrim.esm")).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        var off = new SkyrimMod(new ModKey("HcTsOff", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var kw = off.Keywords.AddNew(); kw.EditorID = "HcTsOffKeyword";
        off.BeginWrite.ToPath(Path.Combine(offDir, OffName)).WithLoadOrder(new ISkyrimModGetter[] { sky }).Write();

        var offMaster = new SkyrimMod(new ModKey("HcTsOffMaster", ModType.Master), SkyrimRelease.SkyrimSE);
        var mkw = offMaster.Keywords.AddNew(); mkw.EditorID = "HcTsOffMasterKeyword";
        offMaster.BeginWrite.ToPath(Path.Combine(offDir, OffMasterName)).WithLoadOrder(new ISkyrimModGetter[] { sky }).Write();

        var unlistedDir = Path.Combine(mods, "HcTsUnlisted");
        Directory.CreateDirectory(unlistedDir);
        new SkyrimMod(new ModKey("HcTsUnlistedMaster", ModType.Master), SkyrimRelease.SkyrimSE)
            .BeginWrite.ToPath(Path.Combine(unlistedDir, UnlistedMasterName)).WithLoadOrder(new ISkyrimModGetter[] { sky }).Write();

        // An enabled mod's SKSE DLL whose image names the unserved plugin and a served one.
        var skseDir = Path.Combine(mods, "HcTsSkseMod", "SKSE", "Plugins");
        Directory.CreateDirectory(skseDir);
        File.WriteAllBytes(Path.Combine(skseDir, "HcTsPeek.dll"),
            System.Text.Encoding.ASCII.GetBytes("\0\0" + OffName + "\0\0Skyrim.esm\0\0"));

        foreach (var dir in new[] { profileDir, otherProfileDir })
        {
            File.WriteAllText(Path.Combine(dir, "loadorder.txt"),
                "# header\r\nSkyrim.esm\r\n" + OffMasterName + "\r\n" + UnlistedMasterName + "\r\n" + GoneMasterName + "\r\n" +
                OffName + "\r\n" + GoneName + "\r\n");
            // The tick with nothing served behind it: the plugin is checked, its only folder is switched off.
            File.WriteAllText(Path.Combine(dir, "plugins.txt"), "*" + OffName + "\r\n*" + GoneName + "\r\n");
        }
        File.WriteAllText(Path.Combine(profileDir, "modlist.txt"),
            "# header\r\n+HcTsSkseMod\r\n-" + OffMod + "\r\n+VanillaStub\r\n");
        // Other switches the folder ON, so its served set differs from Default's.
        File.WriteAllText(Path.Combine(otherProfileDir, "modlist.txt"),
            "# header\r\n+HcTsSkseMod\r\n+" + OffMod + "\r\n+VanillaStub\r\n");

        var store = new UserConfigStore(Path.Combine(Root, "houseCARL.user.json"));
        Svc = LoadOrderService.WithInstance(instance, 0, store);
        Tools = new ToolPathResolver(store);
    }

    public void Dispose()
    {
        Svc.Dispose();
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}

public sealed class TickedPluginSwitchedOffModFixture : IDisposable
{
    public TickedPluginSwitchedOffModWorld W { get; } = new();
    public void Dispose() => W.Dispose();
}


/// <summary>A plugin the VFS does not serve is not active, whatever plugins.txt says: every lane that answers
/// "is it active" says not, and names the switched-off folder holding the copy.</summary>
[Trait("tier", "integration")]
public sealed class TickedPluginSwitchedOffModTests : IClassFixture<TickedPluginSwitchedOffModFixture>
{
    readonly TickedPluginSwitchedOffModWorld W;
    public TickedPluginSwitchedOffModTests(TickedPluginSwitchedOffModFixture f) => W = f.W;

    [Fact]
    public void TheStatusFilterCallsTheTickedPluginNotActiveAndNamesTheSwitchedOffFolder()
    {
        var r = StatusTools.LoadOrderStatus(W.Svc, W.Tools, filter: W.OffName);
        var line = Assert.Single(r.Split('\n'), l => l.Contains("as a plugin:", StringComparison.Ordinal));
        Assert.Contains("NOT ACTIVE", line);
        Assert.Contains($"mod '{W.OffMod}', which is switched OFF", line);
    }

    [Fact]
    public void TheStatusSummaryDoesNotCountTheTickedPluginActive()
    {
        var r = StatusTools.LoadOrderStatus(W.Svc, W.Tools);
        // Skyrim.esm is the one implicit master; the ticked plugin is not served, so no checked plugin is active.
        Assert.Contains("active:   1  (0 checked + 1 implicit", r);
        Assert.Contains("not served: 5", r);
        // The list comes from the same served set as the count.
        Assert.Contains("implicit masters / CC (1):", r);
        Assert.Contains($"load order lists '{W.OffName}', but it is provided by mod '{W.OffMod}', which is switched OFF", r);
    }

    [Fact]
    public void AWinnerReadRefusalNamesTheSwitchedOffFolderAndTheOffOrderSpelling()
    {
        var r = RecordsTools.Records(W.Svc, formids: new[] { "000800:" + W.OffName });
        Assert.Contains($"mod '{W.OffMod}', which is switched OFF", r);
        Assert.Contains("source={\"file\": \"" + W.OffName + "\", \"mod\": \"" + W.OffMod + "\"}", r);
        Assert.DoesNotContain("stale", r);
    }

    [Fact]
    public void TheStatusFilterCallsATickedPluginWithNoCopyNotActive()
    {
        var r = StatusTools.LoadOrderStatus(W.Svc, W.Tools, filter: W.GoneName);
        var line = Assert.Single(r.Split('\n'), l => l.Contains("as a plugin:", StringComparison.Ordinal));
        Assert.Contains("NOT ACTIVE — ticked in plugins.txt, but no enabled mod", line);
    }

    [Fact]
    public void TheStatusFilterCallsAnImplicitMasterInASwitchedOffModNotActive()
    {
        var r = StatusTools.LoadOrderStatus(W.Svc, W.Tools, filter: W.OffMasterName);
        var line = Assert.Single(r.Split('\n'), l => l.Contains("as a plugin:", StringComparison.Ordinal));
        Assert.Contains($"NOT ACTIVE — an implicit master/CC, but it is provided by mod '{W.OffMod}'", line);
    }

    [Fact]
    public void TheProfileInspectionDoesNotCountUnservedPluginsActive()
    {
        var r = StatusTools.LoadOrderStatus(W.Svc, W.Tools, profile: "Other");
        // Other has the folder on: six in the order, the three with no copy in an enabled layer unserved. Default would say 1.
        Assert.Contains("plugins: 6 in order · 3 active · 0 inactive · 3 not served", r);
        Assert.Contains("  not served (3):", r);
    }

    [Fact]
    public void TheSetupSummaryDoesNotCountUnservedPluginsActive()
    {
        var r = SetupTools.Render(Mo2Instance.Resolve(W.Instance), persisted: true, persistError: null, persistNote: null);
        Assert.Contains("plugins in the load order (1 active)", r);
    }

    [Fact]
    public void TheSksePeekDoesNotAdjudicateAnUnservedPluginAsLoaded()
    {
        var r = SkseTools.Skse(W.Svc, filter: "HcTsPeek", peek: true);
        Assert.Contains("NOT in your load order", LineOf(r, W.OffName));
        Assert.Contains("(in your load order)", LineOf(r, "Skyrim.esm"));
    }

    [Fact]
    public void AWinnerReadRefusalForAnUnservedImplicitMasterNamesTheSwitchedOffFolder()
    {
        var r = RecordsTools.Records(W.Svc, formids: new[] { "000800:" + W.OffMasterName });
        Assert.Contains($"'{W.OffMasterName}' is an implicit master listed in loadorder.txt, but it is not active", r);
        Assert.Contains($"mod '{W.OffMod}', which is switched OFF", r);
        Assert.DoesNotContain("tick the plugin", r);
    }

    [Fact]
    public void AnImplicitMasterInAnUnlistedFolderIsStaleToTheExplainerAndNotActiveToStatus()
    {
        var r = RecordsTools.Records(W.Svc, formids: new[] { "000800:" + W.UnlistedMasterName });
        Assert.Contains($"'{W.UnlistedMasterName}' is an implicit master listed in loadorder.txt, but it is not active: no enabled mod", r);
        Assert.Contains("the profile is stale", r);
        Assert.DoesNotContain("does not list it", r);
        Assert.Contains("NOT ACTIVE — an implicit master/CC, but no enabled mod", StatusTools.LoadOrderStatus(W.Svc, W.Tools, filter: W.UnlistedMasterName));
    }

    [Fact]
    public void AnImplicitMasterWithNoCopyIsStaleToTheExplainerAndNotActiveToStatus()
    {
        var r = RecordsTools.Records(W.Svc, formids: new[] { "000800:" + W.GoneMasterName });
        Assert.Contains($"'{W.GoneMasterName}' is an implicit master listed in loadorder.txt, but it is not active: no enabled mod", r);
        Assert.Contains("the profile is stale", r);
        Assert.Contains("NOT ACTIVE — an implicit master/CC, but no enabled mod", StatusTools.LoadOrderStatus(W.Svc, W.Tools, filter: W.GoneMasterName));
    }

    [Fact]
    public void ATickedPluginMissingFromLoadOrderTxtWithNoCopyIsStaleAndNotActive()
    {
        var root = Path.Combine(Path.GetTempPath(), "hc-ticked-unordered-" + Guid.NewGuid().ToString("N"));
        try
        {
            var data = Path.Combine(root, "Data"); var mods = Path.Combine(root, "mods"); var profile = Path.Combine(root, "profile");
            foreach (var d in new[] { data, mods, profile }) Directory.CreateDirectory(d);
            new SkyrimMod(new ModKey("Skyrim", ModType.Master), SkyrimRelease.SkyrimSE)
                .BeginWrite.ToPath(Path.Combine(data, "Skyrim.esm")).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
            // Ticked in plugins.txt, absent from loadorder.txt, and no copy anywhere.
            File.WriteAllText(Path.Combine(profile, "loadorder.txt"), "Skyrim.esm\r\n");
            File.WriteAllText(Path.Combine(profile, "plugins.txt"), "*" + W.GoneName + "\r\n");
            File.WriteAllText(Path.Combine(profile, "modlist.txt"), "# header\r\n");
            var store = new UserConfigStore(Path.Combine(root, "houseCARL.user.json"));
            using var svc = LoadOrderService.WithExplicitPaths(data, mods, profile, 0, store);
            var status = StatusTools.LoadOrderStatus(svc, new ToolPathResolver(store));
            Assert.Contains("active:   1  (0 checked + 1 implicit", status);
            var refusal = RecordsTools.Records(svc, formids: new[] { "000800:" + W.GoneName });
            Assert.Contains($"'{W.GoneName}' is ticked in plugins.txt, but it is not active: no enabled mod or the game Data folder provides it — the profile is stale", refusal);
        }
        finally { try { Directory.Delete(root, true); } catch { /* temp cleanup best-effort */ } }
    }

    [Fact]
    public void ARefusalOffAPinnedViewIsExplainedFromItsOwnBuildAfterALaterBuildIsPublished()
    {
        using var w = new TickedPluginSwitchedOffModWorld();   // mutated below, so its own instance
        var mods = Path.Combine(w.Instance, "mods");
        var modlist = Path.Combine(w.Instance, "profiles", "Default", "modlist.txt");
        LoadOrderResolver.IndexView later = default;
        // Inside the pin's hold, a second switched-off copy above the first is published as the next build; the order itself is unchanged.
        void PublishTheNextBuild()
        {
            Directory.CreateDirectory(Path.Combine(mods, "HcTsNewOff"));
            File.Copy(Path.Combine(mods, w.OffMod, w.OffName), Path.Combine(mods, "HcTsNewOff", w.OffName));
            File.WriteAllText(modlist, "# header\r\n+HcTsSkseMod\r\n-HcTsNewOff\r\n-" + w.OffMod + "\r\n+VanillaStub\r\n");
            later = w.Svc.CaptureView();
        }
        var (pin, _) = ((ILoadOrderHost)w.Svc).CapturePinAndRoots(PublishTheNextBuild);
        Assert.Contains("mod 'HcTsNewOff', which is switched OFF", later.AbsenceClause(w.OffName));
        Assert.Contains($"mod '{w.OffMod}', which is switched OFF", pin.View.AbsenceClause(w.OffName));
    }

    [Fact]
    public void ASwitchToAProfileThatResolvesNothingPublishesNoServedAnswerBesideTheOldRoots()
    {
        using var w = new TickedPluginSwitchedOffModWorld();   // mutated below, so its own instance
        var empty = Path.Combine(w.Instance, "profiles", "Empty");
        Directory.CreateDirectory(empty);
        File.WriteAllText(Path.Combine(empty, "loadorder.txt"), "Skyrim.esm\r\n" + w.OffName + "\r\n");
        File.WriteAllText(Path.Combine(empty, "plugins.txt"), "*" + w.OffName + "\r\n");
        File.WriteAllText(Path.Combine(empty, "modlist.txt"), "-VanillaStub\r\n+HcTsSkseMod\r\n-" + w.OffMod + "\r\n");
        w.Svc.NamedProfileComposition(null);                   // derives the roots; nothing is built yet
        var ini = Path.Combine(w.Instance, "ModOrganizer.ini");
        File.WriteAllText(ini, File.ReadAllText(ini).Replace("@ByteArray(Default)", "@ByteArray(Empty)"));
        // The switch cannot land (Empty resolves nothing), so the answer stays Default's, where VanillaStub serves Skyrim.esm.
        for (int i = 0; i < 2; i++)
            Assert.Contains("(in your load order)", LineOf(SkseTools.Skse(w.Svc, filter: "HcTsPeek", peek: true), "Skyrim.esm"));
    }

    [Fact]
    public void ThePeekAndStatusReadOneServedAnswerAfterACopyLandsInAnEnabledMod()
    {
        using var w = CopyLandsInAnEnabledMod();
        Assert.Contains("NOT in your load order", LineOf(SkseTools.Skse(w.Svc, filter: "HcTsPeek", peek: true), w.OffName));
        Assert.Contains("NOT ACTIVE", StatusTools.LoadOrderStatus(w.Svc, w.Tools, filter: w.OffName));
    }

    [Fact]
    public void AfterACopyLandsInAnEnabledModTheExplainerStillNamesTheSwitchedOffFolder()
    {
        using var w = CopyLandsInAnEnabledMod();
        Assert.Contains("NOT ACTIVE", StatusTools.LoadOrderStatus(w.Svc, w.Tools, filter: w.OffName));
        // The build's answer, not the flat not-in-order sentence a fresh disk check led to.
        Assert.Contains($"mod '{w.OffMod}', which is switched OFF", RecordsTools.Records(w.Svc, formids: new[] { "000800:" + w.OffName }));
    }

    /// <summary>A world of its own, status read once, then a served copy of the ticked plugin lands in an enabled mod with no profile write, so the order build is not re-run.</summary>
    static TickedPluginSwitchedOffModWorld CopyLandsInAnEnabledMod()
    {
        var w = new TickedPluginSwitchedOffModWorld();
        StatusTools.LoadOrderStatus(w.Svc, w.Tools);
        File.Copy(Path.Combine(w.Instance, "mods", w.OffMod, w.OffName), Path.Combine(w.Instance, "mods", "HcTsSkseMod", w.OffName));
        return w;
    }

    [Fact]
    public void TheActiveProfilesInspectionTakesTheBuildsAnswerWhenTheInstanceIsSpelledWithForwardSlashes()
    {
        using var w = new TickedPluginSwitchedOffModWorld();   // mutated below, so its own instance
        var store = new UserConfigStore(Path.Combine(w.Root, "slashes.user.json"));
        using var svc = LoadOrderService.WithInstance(w.Instance.Replace('\\', '/'), 0, store);
        StatusTools.LoadOrderStatus(svc, new ToolPathResolver(store));
        // A copy lands in an enabled mod with no profile write: the build still says 5 not served, a fresh listing would say 4.
        File.Copy(Path.Combine(w.Instance, "mods", w.OffMod, w.OffName), Path.Combine(w.Instance, "mods", "HcTsSkseMod", w.OffName));
        var r = StatusTools.LoadOrderStatus(svc, new ToolPathResolver(store), profile: "Default");
        Assert.Contains("plugins: 6 in order · 1 active · 0 inactive · 5 not served", r);
    }

    [Fact]
    public void InExplicitPathsModeTheStatusFilterNamesOnlyThePlacesSearched()
    {
        var root = Path.Combine(Path.GetTempPath(), "hc-ticked-explicit-" + Guid.NewGuid().ToString("N"));
        try
        {
            var data = Path.Combine(root, "Data"); var mods = Path.Combine(root, "mods"); var profile = Path.Combine(root, "profile");
            foreach (var d in new[] { data, mods, profile }) Directory.CreateDirectory(d);
            new SkyrimMod(new ModKey("Skyrim", ModType.Master), SkyrimRelease.SkyrimSE)
                .BeginWrite.ToPath(Path.Combine(data, "Skyrim.esm")).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
            File.WriteAllText(Path.Combine(profile, "loadorder.txt"), "Skyrim.esm\r\n" + W.GoneName + "\r\n");
            File.WriteAllText(Path.Combine(profile, "plugins.txt"), "*" + W.GoneName + "\r\n");
            File.WriteAllText(Path.Combine(profile, "modlist.txt"), "# header\r\n");
            var store = new UserConfigStore(Path.Combine(root, "houseCARL.user.json"));
            using var svc = LoadOrderService.WithExplicitPaths(data, mods, profile, 0, store);
            var r = StatusTools.LoadOrderStatus(svc, new ToolPathResolver(store), filter: W.GoneName);
            var line = Assert.Single(r.Split('\n'), l => l.Contains("as a plugin:", StringComparison.Ordinal));
            Assert.Contains("no enabled mod or the game Data folder provides it", line);
        }
        finally { try { Directory.Delete(root, true); } catch { /* temp cleanup best-effort */ } }
    }

    [Fact]
    public void ThePeekJudgesEveryReferenceNotLoadedWhenNothingListedIsServed()
    {
        var root = Path.Combine(Path.GetTempPath(), "hc-ticked-none-served-" + Guid.NewGuid().ToString("N"));
        try
        {
            var instance = Path.Combine(root, "instance");
            var profile = Path.Combine(instance, "profiles", "Default");
            var mods = Path.Combine(instance, "mods");
            var skse = Path.Combine(mods, "HcTsSkseMod", "SKSE", "Plugins");
            foreach (var d in new[] { profile, skse, Path.Combine(mods, W.OffMod), Path.Combine(root, "game", "Data") }) Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
                "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
                + Path.Combine(root, "game").Replace(@"\", @"\\") + ")\r\n");
            File.WriteAllText(Path.Combine(mods, W.OffMod, W.OffName), "");
            File.WriteAllBytes(Path.Combine(skse, "HcTsPeek.dll"), System.Text.Encoding.ASCII.GetBytes("\0\0" + W.OffName + "\0\0"));
            // The one listed plugin is ticked, and its only folder is switched off.
            File.WriteAllText(Path.Combine(profile, "loadorder.txt"), W.OffName + "\r\n");
            File.WriteAllText(Path.Combine(profile, "plugins.txt"), "*" + W.OffName + "\r\n");
            File.WriteAllText(Path.Combine(profile, "modlist.txt"), "+HcTsSkseMod\r\n-" + W.OffMod + "\r\n");
            using var svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(root, "houseCARL.user.json")));
            Assert.Contains("NOT in your load order", LineOf(SkseTools.Skse(svc, filter: "HcTsPeek", peek: true), W.OffName));
        }
        finally { try { Directory.Delete(root, true); } catch { /* temp cleanup best-effort */ } }
    }

    static string LineOf(string text, string needle) =>
        text.Split('\n').FirstOrDefault(l => l.Contains(needle) && l.Contains("load order"))
        ?? throw new Xunit.Sdk.XunitException($"no load-order line naming '{needle}' in:\n{text}");
}
