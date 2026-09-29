using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

using static HousecarlMcpTests.CheckMergeFixture;

namespace HousecarlMcpTests;

/// <summary>A synthetic MO2 instance for the merged check tool's own orchestration: two active plugins mastering an
/// absent master (dangling refs and a missing master) with unbound-VMAD weapons, and a third plugin on disk in an
/// enabled mod but out of the load order.</summary>
public sealed class CheckMergeOrchestrationWorld : IDisposable
{
    public const int OrchNpcs = 6;
    public const int OrchWeapons = 4;
    public const int OffOrderNpcs = 3;
    public const int OffOrderWeapons = 2;
    public const int SecondWeapons = 2;

    readonly string root = Path.Combine(Path.GetTempPath(), "hc-check-merge-orch-" + Guid.NewGuid().ToString("N"));

    public LoadOrderService Svc { get; }

    public CheckMergeOrchestrationWorld()
    {
        string instance = Path.Combine(root, "orch");
        string profiles = Path.Combine(instance, "profiles", "Default");
        string mods = Path.Combine(instance, "mods");
        string game = Path.Combine(root, "orchgame");
        Directory.CreateDirectory(profiles); Directory.CreateDirectory(mods); Directory.CreateDirectory(Path.Combine(game, "Data"));
        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + game.Replace(@"\", @"\\") + ")\r\n");

        string modDir = Path.Combine(mods, "OrchMod"), twoDir = Path.Combine(mods, "OrchTwo"), offDir = Path.Combine(mods, "OrchOff");
        string scripts = Path.Combine(modDir, "Scripts");
        foreach (var d in new[] { modDir, twoDir, offDir, scripts }) Directory.CreateDirectory(d);
        PexWriter.WritePex(Path.Combine(scripts, "HcOrchScript.pex"), "HcOrchScript", parent: null,
            PexWriter.AutoObj("HcOrchSpell", "Spell"), PexWriter.AutoScalar("HcOrchChance", "Int", null));

        string ghostPath = Path.Combine(root, "HcOrchGhost.esm");
        var ghost = new SkyrimMod(new ModKey("HcOrchGhost", ModType.Master), SkyrimRelease.SkyrimSE);
        var ghostRace = ghost.Races.AddNew(); ghostRace.EditorID = "HcOrchGhostRace";
        ghost.BeginWrite.ToPath(ghostPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        void WriteMod(string path, string name, int npcs, int weapons)
        {
            var m = new SkyrimMod(new ModKey(name, ModType.Plugin), SkyrimRelease.SkyrimSE);
            for (int i = 0; i < npcs; i++)
            { var n = m.Npcs.AddNew(); n.EditorID = $"{name}Npc{i:D2}"; n.Race.SetTo(ghostRace.FormKey); }
            for (int i = 0; i < weapons; i++)
            { var w = m.Weapons.AddNew(); w.EditorID = $"{name}Weap{i:D2}"; w.VirtualMachineAdapter = Vmad("HcOrchScript"); }
            using var g = SkyrimMod.CreateFromBinaryOverlay(ghostPath, SkyrimRelease.SkyrimSE);
            m.BeginWrite.ToPath(path).WithLoadOrder(new ISkyrimModGetter[] { g }).Write();
        }
        WriteMod(Path.Combine(modDir, "HcOrch.esp"), "HcOrch", OrchNpcs, OrchWeapons);
        WriteMod(Path.Combine(twoDir, "HcOrchTwo.esp"), "HcOrchTwo", 0, SecondWeapons);
        WriteMod(Path.Combine(offDir, "HcOrchOff.esp"), "HcOrchOff", OffOrderNpcs, OffOrderWeapons);

        File.WriteAllText(Path.Combine(profiles, "loadorder.txt"), "# header\r\nHcOrch.esp\r\nHcOrchTwo.esp\r\n");
        File.WriteAllText(Path.Combine(profiles, "plugins.txt"), "*HcOrch.esp\r\n*HcOrchTwo.esp\r\n");
        File.WriteAllText(Path.Combine(profiles, "modlist.txt"), "# header\r\n+OrchOff\r\n+OrchTwo\r\n+OrchMod\r\n");

        Svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(root, "houseCARL.orch.json")));
    }

    public void Dispose()
    {
        Svc.Dispose();
        try { Directory.Delete(root, true); } catch { /* best-effort */ }
    }
}

/// <summary>The merged check tool's orchestration, driven through <see cref="CheckTools.CheckTool"/>: which families
/// run, how classes, counts_only, exclude= and the off-order lane reach each, and which refusals stay family-local.
/// Each fact carries the <c>check-guard</c> probe arm it replaces.</summary>
[Trait("tier", "integration")]
public class CheckMergeOrchestrationTests : IClassFixture<CheckMergeOrchestrationWorld>
{
    const int OrchNpcs = CheckMergeOrchestrationWorld.OrchNpcs;
    const int OrchWeapons = CheckMergeOrchestrationWorld.OrchWeapons;
    const int OffOrderNpcs = CheckMergeOrchestrationWorld.OffOrderNpcs;
    const int OffOrderWeapons = CheckMergeOrchestrationWorld.OffOrderWeapons;
    const int SecondWeapons = CheckMergeOrchestrationWorld.SecondWeapons;

    readonly LoadOrderService svc;

    public CheckMergeOrchestrationTests(CheckMergeOrchestrationWorld world) => svc = world.Svc;

    static bool IsError(string r) => r.StartsWith("error", StringComparison.OrdinalIgnoreCase);

    // ORCH-CONTROL: the merged tool sweeps the instance and both families find what the fixture planted
    [Fact]
    public void TheMergedToolFindsWhatTheFixturePlanted()
    {
        var r = CheckTools.CheckTool(svc, findings: new[] { "errors", "scripts" });
        Assert.Contains($"{OrchNpcs} dangling ref(s)", r);
        Assert.Contains($"all {OrchWeapons + SecondWeapons} record section(s) found by this sweep appear above.", r);
        Assert.Contains("scanned 2 plugins", r);
    }

    // ORCH-FAMILY-SELECTION-ROUTES: omitted runs errors alone; ['scripts'] runs scripts alone; both runs both
    [Fact]
    public void FindingsSelectsWhichFamilySectionsTheResponseCarries()
    {
        var defaulted = CheckTools.CheckTool(svc);
        var scriptsOnly = CheckTools.CheckTool(svc, findings: new[] { "scripts" });
        var both = CheckTools.CheckTool(svc, findings: new[] { "errors", "scripts" });
        Assert.Equal((1, 0), (Count(defaulted, "\n[errors] "), Count(defaulted, "\n[scripts] ")));
        Assert.Equal((0, 1), (Count(scriptsOnly, "\n[errors] "), Count(scriptsOnly, "\n[scripts] ")));
        Assert.Equal((1, 1), (Count(both, "\n[errors] "), Count(both, "\n[scripts] ")));
    }

    // ORCH-CLASS-ROUTING-REACHES-THE-FAMILY: a class token runs its family narrowed; the other class reads NOT CHECKED
    [Fact]
    public void AClassTokenNarrowsTheFamilyItNames()
    {
        var masters = CheckTools.CheckTool(svc, findings: new[] { "missing_masters" });
        var scalar = CheckTools.CheckTool(svc, findings: new[] { "unbound_scalar" });
        Assert.DoesNotContain($"{OrchNpcs} dangling ref(s)", masters);
        Assert.Contains("missing master", masters, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, Count(scalar, "\n[scripts] "));
        Assert.Contains("NOT CHECKED", scalar);
    }

    // ORCH-COUNTS-ONLY-REACHES-EVERY-FAMILY: counts_only silences both families' listings and keeps the histograms
    [Fact]
    public void CountsOnlyReachesEveryFamily()
    {
        var r = CheckTools.CheckTool(svc, findings: new[] { "errors", "scripts" }, counts_only: true);
        Assert.DoesNotContain("[ERROR] ", r);
        Assert.DoesNotContain("[UNBOUND] ", r);
        Assert.DoesNotContain("[CHECK] ", r);
        Assert.Contains("dangling ref(s) by ", r);
    }

    // ORCH-OFF-ORDER-LANE-IN-BOTH-SWEPT-FAMILIES: an off-order plugin is swept by both families, each naming it
    [Fact]
    public void AnOffOrderPluginIsSweptByBothFamilies()
    {
        var r = CheckTools.CheckTool(svc, plugins: new[] { "HcOrch.esp", "HcOrchOff.esp" }, findings: new[] { "errors", "scripts" });
        Assert.Contains($"{OrchNpcs + OffOrderNpcs} dangling ref(s)", r);
        Assert.Contains($"all {OrchWeapons + OffOrderWeapons} record section(s) found by this sweep appear above.", r);
        Assert.Equal(2, Count(r, "swept OFF-ORDER (on disk, not in the active load order): HcOrchOff.esp"));
    }

    // ORCH-AN-ENTIRELY-OFF-ORDER-SCOPE-DOES-NOT-WIDEN: with every named plugin off-order both sweep that one file
    [Fact]
    public void AnEntirelyOffOrderScopeDoesNotWiden()
    {
        var r = CheckTools.CheckTool(svc, plugins: new[] { "HcOrchOff.esp" }, findings: new[] { "errors", "scripts" });
        Assert.Contains($"all {OffOrderWeapons} record section(s) found by this sweep appear above.", r);
        Assert.Equal(2, Count(r, "scanned 1 plugin"));
        Assert.DoesNotContain("HcOrchTwo.esp", r);
        Assert.Contains($"{OffOrderNpcs} dangling ref(s)", r);
    }

    // ORCH-EXCLUDE-REACHES-THE-SCRIPTS-FAMILY: exclude= removes the plugin from the scripts sweep as well
    [Fact]
    public void ExcludeReachesTheScriptsFamily()
    {
        var r = CheckTools.CheckTool(svc, findings: new[] { "errors", "scripts" }, exclude: new[] { "HcOrch.esp" });
        Assert.Contains("scanned 1 plugin ", r);
        Assert.Contains($"all {SecondWeapons} record section(s) found by this sweep appear above.", r);
        Assert.DoesNotContain("HcOrch.esp", r);
        Assert.DoesNotContain($"{OrchNpcs} dangling ref(s)", r);
    }

    // ORCH-EXCLUDE-GROUP-MEMBER-NOT-IN-SCOPE-IS-NOT-A-TYPO: an absent group member does not refuse the scripts sweep
    [Fact]
    public void AGroupWhoseMembersAreAbsentIsNotATypo()
    {
        var r = CheckTools.CheckTool(svc, findings: new[] { "scripts" }, exclude: new[] { "base_masters", "HcOrch.esp" });
        Assert.False(IsError(r), r);
        Assert.Contains("scanned 1 plugin ", r);
        Assert.Contains($"all {SecondWeapons} record section(s) found by this sweep appear above.", r);
        Assert.DoesNotContain("HcOrch.esp", r);
    }

    // ORCH-EXCLUDE-TYPED-NAME-NOT-IN-SCOPE-REFUSES: a typed exclude= name the scope does not contain refuses by name
    [Fact]
    public void ATypedExcludeNameNotInScopeRefuses()
    {
        var r = CheckTools.CheckTool(svc, findings: new[] { "scripts" }, exclude: new[] { "HcOrchNoSuch.esp" });
        Assert.True(IsError(r), r);
        Assert.Contains("HcOrchNoSuch.esp", r);
        Assert.Contains("not in the scope this sweep would cover", r);
    }

    // ORCH-EXCLUDE-FILTER-NOTE-IS-STATED: the scripts family's head names how many plugins exclude= left out
    [Fact]
    public void TheScriptsHeadStatesWhatExcludeLeftOut()
        => Assert.Contains("exclude= left out 1 plugin(s)",
                           CheckTools.CheckTool(svc, findings: new[] { "scripts" }, exclude: new[] { "HcOrch.esp" }));

    // ORCH-A-FAMILY-LOCAL-REFUSAL-DOES-NOT-REFUSE-THE-CALL: the seeded family's cost refusal leaves errors standing
    [Fact]
    public void AFamilyLocalRefusalDoesNotRefuseTheCall()
    {
        var plugins = new[] { "HcOrch.esp", "HcOrchOff.esp" };
        var r = CheckTools.CheckTool(svc, plugins: plugins, findings: new[] { "errors", "dialogue" });
        var json = CheckTools.CheckTool(svc, plugins: plugins, findings: new[] { "errors", "dialogue" }, format: "json");
        Assert.False(IsError(r), r);
        Assert.Contains($"{OrchNpcs + OffOrderNpcs} dangling ref(s)", r);
        Assert.Contains("seeds=", r);
        Assert.Equal(1, Count(r, "\n[errors] "));
        Assert.Equal(1, Count(r, "\n[dialogue] "));
        Assert.True(Has(Family(json, "dialogue"), "refused"));
        Assert.NotNull(Family(json, "errors"));
        Assert.False(Has(Family(json, "errors"), "refused"));
        Assert.Equal(false, Bool(Obj(Family(json, "dialogue"), "accounting"), "listing"));
    }

    // ORCH-A-SHARED-INPUT-REFUSAL-STILL-REFUSES-THE-CALL: an unknown types= refuses the whole call, naming the type
    [Fact]
    public void ASharedInputRefusalRefusesTheWholeCall()
    {
        var r = CheckTools.CheckTool(svc, findings: new[] { "errors", "scripts" }, types: new[] { "NOSUCHTYPE" });
        Assert.True(IsError(r), r);
        Assert.Contains("NOSUCHTYPE", r);
        Assert.Equal(0, Count(r, "\n[errors] "));
    }

    // ORCH-SHARED-INPUT-IS-CHECKED-BEFORE-FAMILY-DISPATCH: on findings=['dialogue'] bad shared inputs still refuse
    [Fact]
    public void SharedInputsAreCheckedOnADialogueOnlyCall()
    {
        var seeds = new[] { "0F1AC1:HcOrch.esp" };
        var dialogue = new[] { "dialogue" };
        var control = CheckTools.CheckTool(svc, findings: dialogue, seeds: seeds);
        var badType = CheckTools.CheckTool(svc, findings: dialogue, seeds: seeds, types: new[] { "NOSUCHTYPE" });
        var badFormid = CheckTools.CheckTool(svc, findings: dialogue, seeds: seeds, formids: new[] { "not-a-formid" });
        var badExclude = CheckTools.CheckTool(svc, findings: dialogue, seeds: seeds, exclude: new[] { "base_master" });
        var badTypeJson = CheckTools.CheckTool(svc, findings: dialogue, seeds: seeds, types: new[] { "NOSUCHTYPE" }, format: "json");
        Assert.True(IsError(badType), badType);
        Assert.Contains("NOSUCHTYPE", badType);
        Assert.True(IsError(badFormid), badFormid);
        Assert.Contains("not-a-formid", badFormid);
        Assert.True(IsError(badExclude), badExclude);
        Assert.Contains("base_master", badExclude);
        Assert.Contains("NOSUCHTYPE", Str(Root(badTypeJson), "error"));
        Assert.Contains("0F1AC1:HcOrch.esp", control);
        Assert.DoesNotContain("NOSUCHTYPE", control);
        Assert.DoesNotContain("not-a-formid", control);
        Assert.DoesNotContain("base_master", control);
    }

    // ORCH-A-BLANK-PLUGIN-NAME-REFUSES-RATHER-THAN-SWEEPING-THE-ORDER
    [Fact]
    public void ABlankPluginNameRefusesRatherThanSweepingTheOrder()
    {
        var blank = CheckTools.CheckTool(svc, findings: new[] { "scripts" }, plugins: new[] { "  " });
        var named = CheckTools.CheckTool(svc, findings: new[] { "scripts" }, plugins: new[] { "HcOrch.esp" });
        Assert.True(IsError(blank), blank);
        Assert.Contains("blank plugin name", blank);
        Assert.Equal(0, Count(blank, "\n[scripts] "));
        Assert.DoesNotContain("record section(s)", blank);
        Assert.Contains($"all {OrchWeapons} record section(s) found by this sweep appear above.", named);
    }

    // ORCH-EXCLUDE-JUDGES-AN-OFF-ORDER-SCOPE: exclude= over an entirely off-order scope empties, refuses, or rejects
    [Fact]
    public void ExcludeJudgesAnEntirelyOffOrderScope()
    {
        var off = new[] { "HcOrchOff.esp" };
        var scripts = new[] { "scripts" };
        var badToken = CheckTools.CheckTool(svc, findings: scripts, plugins: off, exclude: new[] { "NotAToken" });
        var otherName = CheckTools.CheckTool(svc, findings: scripts, plugins: off, exclude: new[] { "HcOrchTwo.esp" });
        var self = CheckTools.CheckTool(svc, findings: scripts, plugins: off, exclude: new[] { "HcOrchOff.esp" });
        Assert.True(IsError(badToken), badToken);
        Assert.Contains("NotAToken", badToken);
        Assert.Contains("exclude= names 'HcOrchTwo.esp'", otherName);
        Assert.Contains("exclude= removed every plugin this sweep would have covered (1 in scope, all excluded)", self);
    }

    // ORCH-DIALOGUE-REFUSAL-IS-FAMILY-LOCAL-THROUGH-THE-TOOL: no seeds= refuses dialogue in its section, errors answers
    [Fact]
    public void TheDialogueRefusalIsFamilyLocalThroughTheTool()
    {
        var r = CheckTools.CheckTool(svc, findings: new[] { "errors", "dialogue" });
        Assert.Equal(1, Count(r, "\n[dialogue] "));
        Assert.Contains("seeds=", r);
        Assert.Contains($"{OrchNpcs} dangling ref(s)", r);
        Assert.False(r.StartsWith("error:", StringComparison.Ordinal));
    }

    // ORCH-FORMAT-ROUTES-AND-A-TYPO-IS-REFUSED: format='json' returns the merged document; an unknown format refuses
    [Fact]
    public void FormatRoutesAndATypoIsRefused()
    {
        var json = CheckTools.CheckTool(svc, findings: new[] { "errors" }, format: "json");
        var bad = CheckTools.CheckTool(svc, findings: new[] { "errors" }, format: "jsonn");
        Assert.NotNull(Obj(Root(json), "families"));
        Assert.True(IsError(bad), bad);
        Assert.Contains("jsonn", bad);
    }
}

/// <summary>The retired-name table held against the families this surface absorbed.</summary>
[Trait("tier", "unit")]
public class CheckMergeRetiredNameTests
{
    // ORCH-EVERY-ABSORBED-ANCESTOR-HAS-A-RETIRED-NAME-ROW: each absorbed family has one reachable row; dialogue names info_order
    [Theory]
    [InlineData(SweepFamily.Errors)]
    [InlineData(SweepFamily.Scripts)]
    [InlineData(SweepFamily.Dialogue)]
    public void EachAbsorbedFamilyHasOneReachableRetiredNameRow(SweepFamily f)
    {
        var spelling = SweepFamilySelection.Spelling(f);
        var rows = AliasTable.AllRetiredTools
                             .Where(r => r.Successor.Contains(spelling, StringComparison.Ordinal)
                                      || r.Successor.Contains("findings=[\"" + SweepFamilySelection.Token(f) + "\"]", StringComparison.Ordinal))
                             .ToArray();
        var row = Assert.Single(rows);
        var hint = AliasTable.RetiredToolHint(row.Old);
        Assert.NotNull(hint);
        Assert.Contains(row.Old, hint);
    }

    // ORCH-EVERY-ABSORBED-ANCESTOR-HAS-A-RETIRED-NAME-ROW (dialogue's second destination)
    [Fact]
    public void TheValidateDialogueRowNamesInfoOrder()
        => Assert.Contains("info_order", AliasTable.RetiredToolHint("housecarl_validate_dialogue"));
}
