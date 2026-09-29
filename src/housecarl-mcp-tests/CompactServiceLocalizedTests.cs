using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Strings;
using HousecarlCore;
using HousecarlGenerator;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The compact service lane over localized sources: the new-file lane de-localizes and says so, the in-place
/// lane refuses before the consent prompt, and a source whose strings resolve nowhere (or cannot be read) is refused
/// with the remedy for its own shape. Each test builds its own instance.</summary>
[Trait("tier", "integration")]
public sealed class CompactServiceLocalizedTests
{
    static LocalizedStringsFixture.Spec Q2 => new("Q2Src", new ModKey("HcCsQ2", ModType.Plugin), "Q2 NAME", "Q2 DESC",
        StringsBeside: true, SecondLanguage: "French");

    static LocalizedStringsFixture.Spec GameData => new("LocSrc", new ModKey("HcCsLoc", ModType.Plugin), "LOC NAME", "LOC DESC");

    static string? ReadLang(string pluginPath, string edid, Language lang)
    {
        using var ov = SkyrimMod.CreateFromBinaryOverlay(pluginPath, SkyrimRelease.SkyrimSE,
            BinaryReadParameters.Default with { StringsParam = new StringsReadParameters { TargetLanguage = lang } });
        return ov.Weapons.FirstOrDefault(x => x.EditorID == edid)?.Name?.String;
    }

    // Q2 fixture: the source is a complete loose set beside the plugin, two languages.
    // Q2 an accepted-shape localized source compacts to a DE-LOCALIZED P' with no Strings folder beside it.
    [Fact]
    public void ALooseCompleteLocalizedSourceCompactsToANonLocalizedFileWithNoStringsFolder()
    {
        var q2 = Q2;
        using var w = new LocalizedCompactWorld(q2);
        var shape = LocalizedStrings.Assess(w.PluginPath(q2), w.Fx.Data);
        Assert.Equal(LocalizedShape.LooseComplete, shape.Shape);
        Assert.Equal(2, shape.Languages.Count);

        var o = w.Svc.CompactPlugin(q2.Key.FileName.String);

        Assert.True(o.Success, o.Error);
        using (var ov = SkyrimMod.CreateFromBinaryOverlay(o.OutputPath, SkyrimRelease.SkyrimSE))
            Assert.False(ov.UsingLocalization);
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(o.OutputPath)!, "Strings")));
    }

    // Q2 the text this read resolved is written into the plugin itself and reads back.
    [Fact]
    public void ALooseCompleteSourcesTextIsWrittenInlineAndReadsBack()
    {
        var q2 = Q2;
        using var w = new LocalizedCompactWorld(q2);
        var o = w.Svc.CompactPlugin(q2.Key.FileName.String);

        Assert.True(o.Success, o.Error);
        Assert.Equal(q2.Name, ReadLang(o.OutputPath, LocalizedStringsFixture.WeaponEdid(q2), Language.English));
    }

    // Q2 the report names both languages the source shipped and claims no count against that list.
    [Fact]
    public void TheDeLocalizedNoteNamesBothLanguagesAndClaimsNoCount()
    {
        var q2 = Q2;
        using var w = new LocalizedCompactWorld(q2);
        var o = w.Svc.CompactPlugin(q2.Key.FileName.String);

        Assert.True(o.Success, o.Error);
        Assert.Contains("is NOT localized", o.Note);
        Assert.Contains("(English, French)", o.Note);
        Assert.DoesNotContain("other language(s) it shipped", o.Note);
    }

    // Q2 the SAME source compacted IN PLACE is refused before the consent prompt, file untouched.
    // Q2 the in-place refusal tells this caller the new-file output is NOT localized.
    [Fact]
    public void ALooseCompleteSourceInPlaceIsRefusedBeforeConsentAndSaysTheNewFileIsNotLocalized()
    {
        var q2 = Q2;
        using var w = new LocalizedCompactWorld(q2);
        var src = w.PluginPath(q2);
        var before = File.ReadAllBytes(src);

        var o = w.Svc.CompactPlugin(q2.Key.FileName.String, inPlace: true, acknowledge: false);

        Assert.False(o.Success);
        Assert.False(o.NeedsAcknowledge);
        Assert.True(LocalizedCompactWorld.Same(src, before));
        Assert.True(LocalizedCompactWorld.NoStaging(src));
        Assert.Contains("That output is NOT localized", o.Error);
        Assert.DoesNotContain("keeps its .STRINGS files", o.Error);
    }

    // LOCALIZED fixture: the source read with the BARE overlay is blank.
    // LOCALIZED (game-Data shape) compact carries FULL+DESC into P'.
    [Fact]
    public void AGameDataStringsSourceKeepsItsNameAndDescriptionThroughTheCompact()
    {
        var ls = GameData;
        using var w = new LocalizedCompactWorld(ls);
        var bare = LocalizedStringsFixture.ReadBackBare(w.PluginPath(ls), LocalizedStringsFixture.WeaponEdid(ls));
        Assert.True(string.IsNullOrEmpty(bare.Name) && string.IsNullOrEmpty(bare.Desc), $"{bare.Name}|{bare.Desc}");

        var o = w.Svc.CompactPlugin(ls.Key.FileName.String);

        Assert.True(o.Success, o.Error);
        var rb = LocalizedStringsFixture.ReadBackBare(o.OutputPath, LocalizedStringsFixture.WeaponEdid(ls));
        Assert.Equal(ls.Name, rb.Name);
        Assert.Equal(ls.Desc, rb.Desc);
    }

    // LOCALIZED (game-Data shape) compact output is written NON-localized with strings inline.
    // NEWFILE-NOTE a localized SOURCE compacted to a new file still succeeds AND is reported as de-localized.
    [Fact]
    public void AGameDataStringsSourceCompactsNonLocalizedAndTheNoteSaysItHasNoTables()
    {
        var ls = GameData;
        using var w = new LocalizedCompactWorld(ls);
        var o = w.Svc.CompactPlugin(ls.Key.FileName.String);

        Assert.True(o.Success, o.Error);
        using (var ov = SkyrimMod.CreateFromBinaryOverlay(o.OutputPath, SkyrimRelease.SkyrimSE))
            Assert.False(ov.UsingLocalization);
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(o.OutputPath)!, "Strings")));
        Assert.Contains("is NOT localized", o.Note);
        Assert.Contains("with no .STRINGS files of its own", o.Note);
    }

    // REMEDY the default lane writes a NEW plugin against a localized target, original untouched.
    [Fact]
    public void TheDefaultEditLaneWritesANewPluginAgainstALocalizedTarget()
    {
        var ls = GameData;
        using var w = new LocalizedCompactWorld(ls);
        var src = w.PluginPath(ls);
        var before = File.ReadAllBytes(src);

        var r = w.Svc.ApplyEdits(new[] { new BulkOp { Formid = $"000A01:{ls.Key.FileName}", FieldPath = "BasicStats.Damage",
                                                      Verb = "Set", Value = "42" } }, "LocRemedyPatch", null);

        Assert.True(r.Success, r.Error);
        Assert.False(r.InPlace);
        Assert.True(File.Exists(r.OutputPath));
        Assert.True(LocalizedCompactWorld.Same(src, before));
    }

    // TARGET-LOC compacting a LOCALIZED plugin IN PLACE is refused before the consent prompt, file untouched.
    // TARGET-LOC the refusal tells THIS caller the new-file output is NOT localized, and where its text is.
    [Fact]
    public void AGameDataStringsSourceInPlaceIsRefusedBeforeConsentNamingWhereItsTextIs()
    {
        var ls = GameData;
        using var w = new LocalizedCompactWorld(ls);
        var src = w.PluginPath(ls);
        var before = File.ReadAllBytes(src);

        var o = w.Svc.CompactPlugin(ls.Key.FileName.String, inPlace: true, acknowledge: false);

        Assert.False(o.Success);
        Assert.False(o.NeedsAcknowledge);
        Assert.StartsWith("houseCARL did not compact", o.Error);
        Assert.True(LocalizedCompactWorld.Same(src, before));
        Assert.True(LocalizedCompactWorld.NoStaging(src));
        Assert.Contains("That output is NOT localized", o.Error);
        Assert.Contains(@"Data\Strings folder, not beside the plugin", o.Error);
        Assert.DoesNotContain("keeps its .STRINGS files", o.Error);
    }

    // NOWHERE-resolving strings REFUSE the new-file compact, named, nothing written.
    [Fact]
    public void StringsThatResolveNowhereRefuseTheNewFileCompact()
    {
        var gs = new LocalizedStringsFixture.Spec("GoneSrc", new ModKey("HcCsGone", ModType.Plugin), "GONE NAME", "GONE DESC", StringsNowhere: true);
        using var w = new LocalizedCompactWorld(gs);

        var o = w.Svc.CompactPlugin(gs.Key.FileName.String);

        Assert.False(o.Success);
        Assert.Contains("LOCALIZED", o.Error);
        Assert.Contains(".STRINGS", o.Error);
        Assert.True(string.IsNullOrEmpty(o.OutputPath) || !File.Exists(o.OutputPath), o.OutputPath);
    }

    // FOLDER-UNREADABLE refuses with the remedy for THIS shape — free the folder, not fill it.
    [Fact]
    public void AnUnlistableStringsFolderRefusesWithThePermissionsRemedy()
    {
        var ls = new LocalizedStringsFixture.Spec("LockedSrc", new ModKey("HcCsLocked", ModType.Plugin), "LOCKED NAME", "LOCKED DESC", StringsBeside: true);
        using var w = new LocalizedCompactWorld(ls);
        var strings = Path.Combine(w.Fx.Mods, ls.ModFolder, "Strings");
        Assert.True(w.DenyListing(strings), "the deny-listing ACE did not take on this host");

        var o = w.Svc.CompactPlugin(ls.Key.FileName.String);

        Assert.False(o.Success);
        Assert.Contains("fix its permissions", o.Error);
        Assert.DoesNotContain("place them in a Strings folder", o.Error);
    }

    // UNREADABLE-SRC: a plain plugin held FileShare.None. Fixture arms, the new-file lane failing (so it is no remedy,
    // and carries no note), the in-place refusal's wording, and the file byte-identical afterwards.
    [Fact]
    public void AHeldSourceIsRefusedInPlaceNamingTheReadFailureNotLocalization()
    {
        var ur = new LocalizedStringsFixture.Spec("UrSrc", new ModKey("HcCsUr", ModType.Plugin), "UR NAME", "UR DESC", Localized: false);
        using var w = new LocalizedCompactWorld(ur);
        var path = w.PluginPath(ur);
        var before = File.ReadAllBytes(path);
        // UNREADABLE-SRC fixture: unheld, the source is a plain NON-localized plugin.
        Assert.Equal(LocalizedShape.NotLocalized, LocalizedStrings.Assess(path, w.Fx.Data).Shape);

        WritePatchBuilder.CompactOutcome newFile, inPlace;
        LocalizedShape heldShape;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            heldShape = LocalizedStrings.Assess(path, w.Fx.Data).Shape;
            newFile = w.Svc.CompactPlugin(ur.Key.FileName.String);
            inPlace = w.Svc.CompactPlugin(ur.Key.FileName.String, inPlace: true, acknowledge: true);
        }

        // UNREADABLE-SRC fixture: held FileShare.None, the same source classifies Unreadable.
        Assert.Equal(LocalizedShape.Unreadable, heldShape);
        // UNREADABLE-SRC the NEW-FILE lane over the same held source ALSO fails, and carries no report.
        Assert.False(newFile.Success);
        Assert.Null(newFile.Note);
        // UNREADABLE-SRC the in-place refusal claims no .STRINGS files and points at no dead-end lane.
        Assert.False(inPlace.Success);
        Assert.DoesNotContain("translated plugin", inPlace.Error ?? "");
        Assert.DoesNotContain("Re-run without in_place", inPlace.Error ?? "");
        // UNREADABLE-SRC ...and says what actually failed, with the remedy for it.
        Assert.Contains("could not read it to see whether it is localized", inPlace.Error);
        Assert.Contains("has the file open", inPlace.Error);
        // UNREADABLE-SRC and the held source is byte-identical afterwards.
        Assert.True(LocalizedCompactWorld.Same(path, before));
    }

    // UNREADABLE-SRC unheld, the same source one lock apart: the service that just hit the held file compacts it
    // once the file is free, and still says nothing about localization.
    [Fact]
    public void TheSameServiceCompactsTheSourceOnceTheHoldIsReleased()
    {
        var ur = new LocalizedStringsFixture.Spec("UrSrc", new ModKey("HcCsUr", ModType.Plugin), "UR NAME", "UR DESC", Localized: false);
        using var w = new LocalizedCompactWorld(ur);
        var path = w.PluginPath(ur);
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.False(w.Svc.CompactPlugin(ur.Key.FileName.String).Success);

        var o = w.Svc.CompactPlugin(ur.Key.FileName.String);

        Assert.True(o.Success, o.Error);
        Assert.DoesNotContain("localized", o.Note ?? "", StringComparison.OrdinalIgnoreCase);
    }

    // UNREADABLE-SRC unheld, the same source compacts and still says nothing about localization.
    [Fact]
    public void AnUnheldPlainSourceCompactsWithNoLocalizationNote()
    {
        var ur = new LocalizedStringsFixture.Spec("UrSrc", new ModKey("HcCsUr", ModType.Plugin), "UR NAME", "UR DESC", Localized: false);
        using var w = new LocalizedCompactWorld(ur);

        var o = w.Svc.CompactPlugin(ur.Key.FileName.String);

        Assert.True(o.Success, o.Error);
        Assert.DoesNotContain("localized", o.Note ?? "", StringComparison.OrdinalIgnoreCase);
    }
}
