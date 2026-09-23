using Xunit;
using SetupProgram = HousecarlSetup.Program;

namespace HousecarlMcpTests;

/// <summary>Migrated from the setup-skill-prune probe: an upgrade takes back the skills the new package dropped,
/// by directory diff at the Claude location and by the install record at the shared Codex location.</summary>
[Collection(SetupInstallerCollection.Name)]
[Trait("tier", "unit")]
public sealed class SetupSkillPruneTests
{
    /// <summary>T1: install three skills for Both, add a foreign skill to the shared root, drop one skill from
    /// the package, and re-install. Returns what the re-install said.</summary>
    static string UpgradeDroppingOne(SetupInstallerSandbox box)
    {
        box.WritePackage("kept-one", "kept-two", "dropped-skill");
        var first = box.Install(SetupProgram.Target.Both);
        Assert.Equal(SetupProgram.InstallOutcome.Installed, first.Outcome);
        Assert.True(Directory.Exists(Path.Combine(ClaudeSkills(box), "dropped-skill")));
        Assert.True(Directory.Exists(Path.Combine(CodexSkills(box), "dropped-skill")));

        SetupInstallerSandbox.WriteFile(Path.Combine(CodexSkills(box), "someone-elses-skill", "SKILL.md"), "not ours");
        Directory.Delete(Path.Combine(box.Src, "skills", "dropped-skill"), recursive: true);
        return SetupInstallerSandbox.Capture(() => box.Install(SetupProgram.Target.Both));
    }

    static string ClaudeSkills(SetupInstallerSandbox box) => Path.Combine(SetupProgram.ClaudeSkillsDest(box.Home), "skills");
    static string CodexSkills(SetupInstallerSandbox box) => SetupProgram.CodexSkillsRoot(box.Home);
    static string Record(SetupInstallerSandbox box) => SetupProgram.CodexSkillRecord(box.Home, box.Home);

    // T2: the Claude skills root loses only the dropped skill.
    [Fact]
    public void AnUpgradePrunesTheDroppedSkillFromTheClaudeRoot()
    {
        using var box = new SetupInstallerSandbox();
        UpgradeDroppingOne(box);

        Assert.False(Directory.Exists(Path.Combine(ClaudeSkills(box), "dropped-skill")));
        Assert.True(Directory.Exists(Path.Combine(ClaudeSkills(box), "kept-one")));
        Assert.True(Directory.Exists(Path.Combine(ClaudeSkills(box), "kept-two")));
    }

    // T3: the shared Codex root loses the dropped skill and nothing else: the umbrella and a foreign skill stay.
    [Fact]
    public void AnUpgradePrunesOnlyTheRecordedDroppedSkillFromTheSharedCodexRoot()
    {
        using var box = new SetupInstallerSandbox();
        UpgradeDroppingOne(box);

        Assert.False(Directory.Exists(Path.Combine(CodexSkills(box), "dropped-skill")));
        Assert.True(Directory.Exists(Path.Combine(CodexSkills(box), "kept-one")));
        Assert.True(Directory.Exists(Path.Combine(CodexSkills(box), "kept-two")));
        Assert.True(Directory.Exists(Path.Combine(CodexSkills(box), "housecarl")));
        Assert.True(Directory.Exists(Path.Combine(CodexSkills(box), "someone-elses-skill")));
    }

    // T4: the run names the removed skill, at both locations.
    [Fact]
    public void AnUpgradeNamesTheSkillsItRemoved()
    {
        using var box = new SetupInstallerSandbox();
        string said = UpgradeDroppingOne(box);

        Assert.Contains("dropped-skill", said);
        Assert.Contains("[Claude Code] removed skills", said);
        Assert.Contains("[Codex] removed skills", said);
    }

    // T5: a read-only leftover cannot be deleted; the install still finishes and registers both hosts, the run
    // names the folder, and the Codex record keeps it so the next upgrade tries again.
    [Fact]
    public void AReadOnlyLeftoverDoesNotStopMcpRegistration()
    {
        using var box = new SetupInstallerSandbox();
        UpgradeDroppingOne(box);
        string record = Record(box);
        Assert.Contains("housecarl", File.ReadAllLines(record));

        foreach (string skillRoot in new[] { ClaudeSkills(box), CodexSkills(box) })
        {
            string file = Path.Combine(skillRoot, "stuck-skill", "SKILL.md");
            SetupInstallerSandbox.WriteFile(file, "left behind");
            File.SetAttributes(file, FileAttributes.ReadOnly);
        }
        File.WriteAllLines(record, File.ReadAllLines(record).Append("stuck-skill"));
        string claudeJson = SetupProgram.ClaudeJson(box.Home);
        string codexToml = SetupProgram.CodexConfigToml(box.Home);
        File.Delete(claudeJson);
        File.Delete(codexToml);

        SetupProgram.InstallResult stuck = null!;
        string said = SetupInstallerSandbox.Capture(() => stuck = box.Install(SetupProgram.Target.Both));

        Assert.Equal(SetupProgram.InstallOutcome.Installed, stuck.Outcome);
        Assert.Contains("housecarl", File.ReadAllText(claudeJson));
        Assert.Contains("housecarl", File.ReadAllText(codexToml));
        Assert.True(Directory.Exists(Path.Combine(ClaudeSkills(box), "stuck-skill")));
        Assert.Contains("could not be deleted", said);
        Assert.Contains(Path.Combine(ClaudeSkills(box), "stuck-skill"), said);
        Assert.Contains("stuck-skill", File.ReadAllLines(record));
        Assert.True(Directory.Exists(Path.Combine(ClaudeSkills(box), "kept-one")));
    }

    // T6: a package with no skills folder is a broken package, so it prunes nothing and keeps the record.
    [Fact]
    public void APackageWithNoSkillsFolderPrunesNothing()
    {
        using var box = new SetupInstallerSandbox();
        UpgradeDroppingOne(box);
        Directory.Delete(Path.Combine(box.Src, "skills"), recursive: true);

        string said = SetupInstallerSandbox.Capture(() => box.Install(SetupProgram.Target.Both));

        Assert.True(Directory.Exists(Path.Combine(ClaudeSkills(box), "kept-one")));
        Assert.True(Directory.Exists(Path.Combine(ClaudeSkills(box), "kept-two")));
        Assert.True(Directory.Exists(Path.Combine(CodexSkills(box), "kept-one")));
        Assert.True(Directory.Exists(Path.Combine(CodexSkills(box), "kept-two")));
        Assert.True(Directory.Exists(Path.Combine(CodexSkills(box), "housecarl")));
        Assert.Contains("kept-one", File.ReadAllLines(Record(box)));
        Assert.Contains("no skills folder", said);
    }

    // T7: the umbrella is on the record, and goes when a later package stops shipping it.
    [Fact]
    public void TheUmbrellaGoesWhenThePackageStopsShippingIt()
    {
        using var box = new SetupInstallerSandbox();
        UpgradeDroppingOne(box);
        Directory.Delete(box.UmbrellaPackageDir, recursive: true);

        string said = SetupInstallerSandbox.Capture(() => box.Install(SetupProgram.Target.Both));

        Assert.False(Directory.Exists(Path.Combine(CodexSkills(box), "housecarl")));
        Assert.Contains(said.Split('\n'), l => l.Trim() == "- housecarl");
        Assert.True(Directory.Exists(Path.Combine(CodexSkills(box), "kept-one")));
        Assert.True(Directory.Exists(Path.Combine(CodexSkills(box), "someone-elses-skill")));
        Assert.DoesNotContain("housecarl", File.ReadAllLines(Record(box)));
    }
}
