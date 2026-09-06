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
