using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// A fresh <c>patch=</c> stem against a plugin the active order is NOT loading (#561). The mod-folder and
/// active-plugin tests behind the stem allocator never saw such a plugin, so houseCARL used to mint
/// "&lt;stem&gt;.esp" beside a foreign "&lt;stem&gt;.esp" — two plugins that cannot both be active — and said
/// nothing. Driven through <c>housecarl_create</c>'s engine entry, the shortest real <c>patch=</c> write; the check
/// itself is shared by every fresh-write lane.
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
    static void AddDisabledForeignMod(RecordsWorld w, string folder, string? plugin)
    {
        var dir = Path.Combine(w.ModsDir, folder);
        Directory.CreateDirectory(dir);
        if (plugin is not null)
            File.WriteAllText(Path.Combine(dir, plugin), "a user's file houseCARL must never shadow");
        File.AppendAllText(Path.Combine(w.Instance, "profiles", "Default", "modlist.txt"), "-" + folder + "\r\n");
    }

    static WritePatchBuilder.CreateOutcome FreshPatch(RecordsWorld w, string patchName, string editorId) =>
        w.Svc.CreateRecordsBatch(new[] { new CreateOp { RecordType = "Keyword", Editorid = editorId } },
                                 patchName, null);

    /// <summary>The bug: a disabled mod of someone else's holds "My Cool Patch.esp", and patch="My Cool Patch" used
    /// to mint a second plugin of that name beside it. It is refused, and the sentence names the folder it found and
    /// the file in it.</summary>
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

    /// <summary>The same name with no foreign plugin behind it is an ordinary write — the check must not refuse a
    /// stem merely because a folder of some similar name exists.</summary>
    [Fact]
    public void TheSameStemProceedsWhenNoForeignFolderHoldsThePlugin()
    {
        using var w = new RecordsWorld();
        AddDisabledForeignMod(w, "Foreign Patch Mod", null);

        var o = FreshPatch(w, "My Cool Patch", "HcShadowKwB");

        Assert.True(o.Success, "refused: " + o.Error);
        Assert.Equal("My Cool Patch.esp", Path.GetFileName(o.OutputPath));
        Assert.Equal("houseCARL - My Cool Patch", Path.GetFileName(Path.GetDirectoryName(o.OutputPath)));
    }

    /// <summary>houseCARL's OWN patch of that name is not a shadow — the stem allocator suffixes past it as it always
    /// did, and a second patch is created. The suffixed stem is checked in its turn: a foreign disabled mod holding
    /// "My Cool Patch_001.esp" refuses the name the suffix landed on, so the collision is never renamed around.</summary>
    [Fact]
    public void AnOwnedPatchIsNoShadow_ButTheStemTheSuffixLandsOnIsStillChecked()
    {
        using var w = new RecordsWorld();
        var first = FreshPatch(w, "My Cool Patch", "HcShadowKwC");
        Assert.True(first.Success, "refused: " + first.Error);

        // The owned folder now holds an INACTIVE "My Cool Patch.esp" — MO2 has not been told about it yet — so the
        // base stem reaches the shadow check on exactly the terms the foreign case does.
        var second = FreshPatch(w, "My Cool Patch", "HcShadowKwD");
        Assert.True(second.Success, "refused: " + second.Error);
        Assert.Equal("houseCARL - My Cool Patch_001", Path.GetFileName(Path.GetDirectoryName(second.OutputPath)));

        AddDisabledForeignMod(w, "Foreign Suffix Mod", "My Cool Patch_002.esp");
        var third = FreshPatch(w, "My Cool Patch", "HcShadowKwE");

        Assert.False(third.Success);
        Assert.Contains("Foreign Suffix Mod", third.Error);
        Assert.Contains("My Cool Patch_002.esp", third.Error);
    }
}
