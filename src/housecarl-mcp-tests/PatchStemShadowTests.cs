using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// A fresh write against a plugin the active order is NOT loading (#561). The mod-folder and active-plugin tests
/// behind the stem allocator never saw such a plugin, so houseCARL used to write "&lt;name&gt;.esp" beside a foreign
/// "&lt;name&gt;.esp" — two plugins that cannot both be active — and said nothing. The check is on the FILENAME the
/// call will write, so a lane whose folder name and plugin name differ is judged by the plugin it really emits.
/// Driven through <c>housecarl_create</c>'s engine entry, the shortest real <c>patch=</c> write, and through
/// <c>housecarl_merge_plugins</c>, whose output filename is not its folder stem.
///
/// <para>Each test builds its OWN world: they add mod folders and rewrite the profile's modlist.</para>
/// </summary>
[Trait("tier", "integration")]
[Collection("records")]
public sealed class PatchStemShadowTests
{
    /// <summary>A mod folder the user owns: no <c>meta.ini</c> marker, so houseCARL reads it as foreign, listed in
    /// modlist.txt with a leading "-" so MO2 has it switched OFF. The plugin file is a stub — nothing opens it,
    /// because the order never loads it, which is the whole point.</summary>
    static void AddDisabledForeignMod(RecordsWorld w, string folder, string plugin)
    {
        var dir = Path.Combine(w.ModsDir, folder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, plugin), "a user's file houseCARL must never shadow");
        File.AppendAllText(Path.Combine(w.Instance, "profiles", "Default", "modlist.txt"), "-" + folder + "\r\n");
    }

    static WritePatchBuilder.CreateOutcome FreshPatch(RecordsWorld w, string? patchName, string editorId) =>
        w.Svc.CreateRecordsBatch(new[] { new CreateOp { RecordType = "Keyword", Editorid = editorId } },
                                 patchName, null);

    /// <summary>The bug: a disabled mod of someone else's holds "My Cool Patch.esp", and patch="My Cool Patch" used
    /// to mint a second plugin of that name beside it. It is refused, and the sentence names where it found it and
    /// the file.</summary>
    [Fact]
    public void AStemThatWouldShadowAnInactivePluginInAForeignModFolderIsRefused()
    {
        using var w = new RecordsWorld();
        AddDisabledForeignMod(w, "Foreign Patch Mod", "My Cool Patch.esp");

        var o = FreshPatch(w, "My Cool Patch", "HcShadowKwA");

        Assert.False(o.Success);
        Assert.Contains("Foreign Patch Mod", o.Error);
        Assert.Contains("My Cool Patch.esp", o.Error);
        Assert.Contains("patch=", o.Error);
        // Refused, never renamed: no patch folder of either spelling was left behind.
        Assert.Empty(Directory.EnumerateDirectories(w.ModsDir, "houseCARL - My Cool Patch*"));
    }

    /// <summary>The record lane writes "&lt;stem&gt;.esp" and nothing else, so a foreign "&lt;stem&gt;.esm" is a
    /// DIFFERENT filename that coexists with it perfectly well. The write proceeds; refusing it would refuse a legal
    /// write and explain it with a collision that does not exist.</summary>
    [Fact]
    public void AForeignEsmOfTheSameStemIsNoShadowForTheEspTheLaneWrites()
    {
        using var w = new RecordsWorld();
        AddDisabledForeignMod(w, "Foreign Master Mod", "My Cool Patch.esm");

        var o = FreshPatch(w, "My Cool Patch", "HcShadowKwB");

        Assert.True(o.Success, "refused: " + o.Error);
        Assert.Equal("My Cool Patch.esp", Path.GetFileName(o.OutputPath));
    }

    /// <summary>A shadow on a suffix houseCARL INVENTED steps to the next suffix instead of hard-stopping: with
    /// patch= omitted the caller chose no name, so there is no name to tell them to avoid. Here the default stem's
    /// own "Patch.esp" is shadowed, and the allocator lands on "Patch_001.esp".</summary>
    [Fact]
    public void AShadowOnAStemTheCallerDidNotChooseStepsToTheNextSuffix()
    {
        using var w = new RecordsWorld();
        AddDisabledForeignMod(w, "Foreign Default Mod", "Patch.esp");

        var o = FreshPatch(w, null, "HcShadowKwC");

        Assert.True(o.Success, "refused: " + o.Error);
        Assert.Equal("Patch_001.esp", Path.GetFileName(o.OutputPath));
        Assert.Equal("houseCARL - Patch_001", Path.GetFileName(Path.GetDirectoryName(o.OutputPath)));
    }

    /// <summary>The copy lane falls back to the new EditorID when patch= is omitted, and that stem is NOT the
    /// caller's own name: standalone-izing a follower out of a mod switched OFF in MO2 with new_editorid="Donor"
    /// shadows that mod's own "Donor.esp", and an unspecified patch= steps to the next suffix like everywhere else
    /// rather than refusing on a parameter the call never passed.</summary>
    [Fact]
    public void AShadowOnAnEditoridDerivedStemStepsToTheNextSuffix()
    {
        using var w = new TwoDisabledDonorsWorld();

        var r = CopyTools.Copy(w.Svc, w.Fid(w.DonorNpc), new[] { "Donor.esp" },
                               new[] { "HeadParts", "HairColor", "HeadTexture", "WornArmor" },
                               new[] { "Race:refuse" }, null, "Donor", null, null);

        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), "refused: " + r.Split('\n')[0]);
        Assert.Single(Directory.EnumerateDirectories(w.ModsDir, "houseCARL - Donor_001"));
    }

    /// <summary>A header-only trigger is bound by its BASENAME, so the extension the shadowing file carries does not
    /// matter: a disabled mod holding "MyTrigger.esm" refuses create_plugin("MyTrigger"), which would otherwise write
    /// a second file of that basename — the state the tool's own active-order check exists to prevent. The refusal
    /// names the file it found, not the one that was going to be written.</summary>
    [Fact]
    public void AHeaderOnlyCreateIsRefusedByAnyExtensionSharingItsBasename()
    {
        using var w = new RecordsWorld();
        AddDisabledForeignMod(w, "Foreign Trigger Mod", "HcShadowTrigger.esm");

        var o = w.Svc.CreatePlugin("HcShadowTrigger");

        Assert.False(o.Success);
        Assert.Contains("Foreign Trigger Mod", o.Error);
        Assert.Contains("HcShadowTrigger.esm", o.Error);
        Assert.Contains("plugin_name=", o.Error);
        Assert.Empty(Directory.EnumerateDirectories(w.ModsDir, "houseCARL - HcShadowTrigger*"));
    }

    /// <summary>A profile that names no mod at all cannot tell a shadow from a loaded plugin — with modlist.txt gone
    /// every folder under ModsDir reads as UNLISTED — so the sweep is skipped and the write proceeds, rather than
    /// refusing a name on a folder it has misread. The order still loads a plugin (the game Data folder provides it),
    /// so the "no active plugin" guard is not the one doing the work here.</summary>
    [Fact]
    public void AProfileNamingNoModAtAllSkipsTheShadowSweep()
    {
        using var w = new RecordsWorld();
        AddDisabledForeignMod(w, "Foreign Unreadable Mod", "My Cool Patch.esp");
        File.Copy(Path.Combine(w.ModsDir, "MasterMod", w.MasterName),
                  Path.Combine(w.Root, "game", "Data", w.MasterName));
        File.Delete(Path.Combine(w.Instance, "profiles", "Default", "modlist.txt"));

        var o = FreshPatch(w, "My Cool Patch", "HcShadowKwD");

        Assert.True(o.Success, "refused: " + o.Error);
        Assert.Equal("My Cool Patch.esp", Path.GetFileName(o.OutputPath));
    }

    /// <summary>A merge's folder stem and its plugin filename are different names: the folder defaults to
    /// "&lt;output&gt; renamed" while the file written is output= itself. The shadow is on the FILE, so a foreign
    /// disabled mod holding that filename refuses the merge, and the remedy names output=, the parameter that
    /// actually moves it.</summary>
    [Fact]
    public void AMergeIsRefusedOnTheOutputFilenameItWritesNotItsFolderStem()
    {
        using var w = new RecordsWorld();
        AddDisabledForeignMod(w, "Foreign Merge Mod", "HcShadowMerge.esp");

        var o = w.Svc.MergePlugins(new[] { w.MidName }, "HcShadowMerge.esp");

        Assert.False(o.Success);
        Assert.Contains("Foreign Merge Mod", o.Error);
        Assert.Contains("HcShadowMerge.esp", o.Error);
        Assert.Contains("output=", o.Error);
    }
}
