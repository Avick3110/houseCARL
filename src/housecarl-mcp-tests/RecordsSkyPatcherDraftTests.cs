using System.Text.Json;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The draft-INI value on the SkyPatcher overlay pole: a file not yet placed in a mod, folded into the
/// live layer so the record reads as the game would see it once the draft is placed.</summary>
[Collection("records")]
[Trait("tier", "integration")]
public sealed class RecordsSkyPatcherDraftTests : RecordsTestBase
{
    public RecordsSkyPatcherDraftTests(RecordsFixture f) : base(f) { }

    static RecordsTools.RecordsProject Damage => new() { form = "fields", fields = new[] { "BasicStats.Damage" } };
    static RecordsTools.RecordsProject DamageDelta => new() { form = "delta", fields = new[] { "BasicStats.Damage" } };
    static RecordsTools.RecordsProject DamageTree => new() { form = "tree", fields = new[] { "BasicStats.Damage" } };

    /// <summary>A draft INI on disk, outside any mod, and the pole that folds it in.</summary>
    string Draft(string dir, string file, string body)
    {
        var path = W.Scratch(dir, file);
        File.WriteAllText(path, body);
        return path;
    }

    static JsonElement DraftPole(string ini, string? subfolder = null, string state = "post") =>
        Je("{\"overlay\": \"skypatcher\", \"state\": \"" + state + "\", \"ini\": " + JsonSerializer.Serialize(ini)
           + (subfolder is null ? "" : ", \"subfolder\": \"" + subfolder + "\"") + "}");

    string ReadW0(JsonElement pole) =>
        RecordsTools.Records(Svc, formids: new[] { Fid(W.Weapons[0]) }, source: pole, project: Damage);

    [Fact]
    public void ADraftThatSetsALeafIsReadInThePostState()
    {
        var ini = Draft("draft-set", "MyWeapons.ini", "filterByWeapons=HcRecW0:attackDamage=123\r\n");
        Served(ReadW0(DraftPole(ini, "weapon")), "BasicStats.Damage = 123", ini);
    }

    [Fact]
    public void TheSubfolderIsTakenFromTheDraftsParentDirectoryAndTheArmSaysSo()
    {
        var ini = Draft("weapon", "Inferred.ini", "filterByWeapons=HcRecW0:attackDamage=77\r\n");
        Served(ReadW0(DraftPole(ini)), "BasicStats.Damage = 77", "parent directory");
    }

    /// <summary>The composition the skill's verify step wants: the draft's post state against the plain post state
    /// is the draft's own effect and nothing else.</summary>
    [Fact]
    public void TheDraftsPostVersusThePlainPostIsTheDraftsOwnEffect()
    {
        var ini = Draft("draft-delta", "Delta.ini", "filterByWeapons=HcRecW0:attackDamage=140\r\n");
        var r = RecordsTools.Records(Svc, formids: new[] { Fid(W.Weapons[0]) }, source: DraftPole(ini, "weapon"),
                                     versus: Overlay("post"), project: DamageDelta);
        Served(r, "BasicStats.Damage", "140", "1 difference");
    }

    [Fact]
    public void ADraftLineWithAnUnknownKeyWarnsAndNamesTheDraftsPath()
    {
        var ini = Draft("draft-typo", "Typo.ini", "filterByWeapons=HcRecW0:attakDamage=5\r\n");
        Served(ReadW0(DraftPole(ini, "weapon")), ini, "not in the SkyPatcher reference");
    }

    /// <summary>A draft named for a plugin that is not in the order would never be read once placed, so the post
    /// state is the plain winner and the response says why rather than reading as "the draft changes nothing".</summary>
    [Fact]
    public void ADraftGatedOnAnInactivePluginIsNotAppliedAndSaysSo()
    {
        var ini = Draft("draft-gate", "NotHere.esp.ini", "filterByWeapons=HcRecW0:attackDamage=131\r\n");
        var r = ReadW0(DraftPole(ini, "weapon"));
        Served(r, "not in the active load order");
        Assert.DoesNotContain("BasicStats.Damage = 131", r);
    }

    /// <summary>The tree form takes an overlay pole as versus= as much as delta does, and a draft's skipped line is
    /// the same fact there: without it every provider reads as identical to the reference with nothing said.</summary>
    [Fact]
    public void ADraftLineTheLayerSkipsIsNamedOnTheTreeFormToo()
    {
        var ini = Draft("draft-tree", "TreeTypo.ini", "filterByWeapons=HcRecW0:attakDamage=5\r\n");
        Served(RecordsTools.Records(Svc, formids: new[] { Fid(W.Weapons[0]) }, versus: DraftPole(ini, "weapon"),
                                    project: DamageTree), ini, "not in the SkyPatcher reference");
    }

    /// <summary>The gate warning comes from the fold rather than the replay, so the tree form has to render it too.</summary>
    [Fact]
    public void ADraftGatedOnAnInactivePluginSaysSoOnTheTreeFormToo()
    {
        var ini = Draft("draft-tree-gate", "AlsoNotHere.esp.ini", "filterByWeapons=HcRecW0:attackDamage=131\r\n");
        Served(RecordsTools.Records(Svc, formids: new[] { Fid(W.Weapons[0]) }, versus: DraftPole(ini, "weapon"),
                                    project: DamageTree), "not in the active load order");
    }

    /// <summary>A draft in a type the SkyPatcher.ini [Patcher] section switches off is folded into a folder the live
    /// scan never built — nothing is placed in that type — so the toggle has to be read off the layer rather than
    /// assumed on: once placed the DLL would skip the whole folder, and the post state stays the plain winner.</summary>
    [Fact]
    public void ADraftInATypeToggledOffInSkyPatcherIniAppliesNothingAndSaysSo()
    {
        var ini = Draft("draft-toggle", "Armors.ini", "filterByArmors=HcRecA0:damageResist=99\r\n");
        var r = RecordsTools.Records(Svc, formids: new[] { Fid(W.Armor) }, source: DraftPole(ini, "armor"),
                                     project: new() { form = "fields", fields = new[] { "ArmorRating" } });
        Served(r, "disables 'armor' patching");
        Assert.DoesNotContain("ArmorRating = 99", r);
    }

    [Fact]
    public void AnUndocumentedSubfolderIsRefusedNamingTheDocumentedFolders()
    {
        var ini = Draft("draft-sub", "Bad.ini", "filterByWeapons=HcRecW0:attackDamage=1\r\n");
        Refused(ReadW0(DraftPole(ini, "weapons")), "weapons", "documented folders are");
    }

    [Fact]
    public void ASubfolderThatCannotBeInferredIsRefusedAskingForOne()
    {
        var ini = Draft("draft-nofolder", "Loose.ini", "filterByWeapons=HcRecW0:attackDamage=1\r\n");
        Refused(ReadW0(DraftPole(ini)), "subfolder");
    }

    [Fact]
    public void TheDraftIsRefusedOnThePreState()
    {
        var ini = Draft("draft-pre", "Pre.ini", "filterByWeapons=HcRecW0:attackDamage=1\r\n");
        Refused(ReadW0(DraftPole(ini, "weapon", state: "pre")), "\"post\"");
    }

    [Fact]
    public void ADraftWhoseFilenameIsAlreadyPlacedIsRefused()
    {
        var ini = Draft("draft-clash", RecordsWorld.LiveSkyPatcherIni, "filterByWeapons=HcRecW0:attackDamage=1\r\n");
        Refused(ReadW0(DraftPole(ini, "weapon")), RecordsWorld.LiveSkyPatcherIni, "shadow");
    }

    /// <summary>The draft keys are a value on the overlay pole. On a {"file"} pole they would be dropped and that
    /// plugin's own record would read as the draft's post state.</summary>
    [Fact]
    public void TheDraftKeysOnAPluginPoleAreRefusedNamingTheOverlayPole()
    {
        var ini = Draft("draft-wrong-pole", "Mixed.ini", "filterByWeapons=HcRecW0:attackDamage=1\r\n");
        Refused(RecordsTools.Records(Svc, formids: new[] { Fid(W.Weapons[0]) },
                                     source: Je("{\"file\": \"" + W.OverrideName + "\", \"ini\": " + JsonSerializer.Serialize(ini) + "}"),
                                     project: Damage), "overlay");
    }

    [Fact]
    public void AMissingDraftFileIsRefused() =>
        Refused(ReadW0(DraftPole(W.Scratch("draft-missing", "Gone.ini"), "weapon")), "no file at");

    [Fact]
    public void ASubfolderWithNoDraftIsRefusedByName() =>
        Refused(RecordsTools.Records(Svc, formids: new[] { Fid(W.Weapons[0]) },
                                     source: Je("{\"overlay\": \"skypatcher\", \"state\": \"post\", \"subfolder\": \"weapon\"}"),
                                     project: Damage), "\"ini\"");
}
