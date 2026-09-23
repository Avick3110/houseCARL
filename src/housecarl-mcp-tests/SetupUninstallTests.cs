using Xunit;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SetupProgram = HousecarlSetup.Program;
using SetupUninstall = HousecarlSetup.Uninstall;

namespace HousecarlMcpTests;

/// <summary>Migrated from the setup-uninstall probe: the removal takes back exactly what the install wrote, and
/// every other byte of both host configs is where it was.</summary>
[Collection(SetupInstallerCollection.Name)]
[Trait("tier", "unit")]
public sealed class SetupUninstallTests
{
    /// <summary>A ~/.claude.json holding an unrelated root key and somebody else's server, in the indentation
    /// System.Text.Json writes, which is the indentation both splices reproduce.</summary>
    static string ExistingClaudeJson() => new JsonObject
    {
        ["numStartups"] = 7,
        ["mcpServers"] = new JsonObject
        {
            ["other"] = new JsonObject { ["type"] = "stdio", ["command"] = @"C:\tools\other.exe" },
        },
    }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    const string ExistingToml = "# my own config\n[other]\nkey = 1\n";

    /// <summary>T1: two pre-existing host configs, a foreign shared skill, an install for Both with a saved MO2
    /// instance beside each server, then an uninstall of Both. Returns the uninstall's result.</summary>
    static SetupUninstall.Result InstallThenUninstallBoth(SetupInstallerSandbox box)
    {
        box.WritePackage("skill-one", "skill-two");
        SetupInstallerSandbox.WriteFile(SetupProgram.ClaudeJson(box.Home), ExistingClaudeJson());
        SetupInstallerSandbox.WriteFile(SetupProgram.CodexConfigToml(box.Home), ExistingToml);
        SetupInstallerSandbox.WriteFile(Path.Combine(SetupProgram.CodexSkillsRoot(box.Home), "someone-elses-skill", "SKILL.md"), "not ours");

        Assert.Equal(SetupProgram.InstallOutcome.Installed, box.Install(SetupProgram.Target.Both).Outcome);
        Assert.True(File.Exists(SetupProgram.ClaudeSkillRecord(box.Home)));
        Assert.Contains("housecarl", File.ReadAllText(SetupProgram.ClaudeJson(box.Home)));
        Assert.Contains("[mcp_servers.housecarl]", File.ReadAllText(SetupProgram.CodexConfigToml(box.Home)));
        SetupInstallerSandbox.WriteFile(Path.Combine(SetupProgram.CodexServerDir(box.Home, box.Home), "houseCARL.user.json"), "{}");
        SetupInstallerSandbox.WriteFile(Path.Combine(SetupProgram.ClaudeSkillsDest(box.Home), "server", "houseCARL.user.json"), "{}");

        SetupUninstall.Result removed = null!;
        SetupInstallerSandbox.Capture(() => removed = SetupUninstall.TryUninstall(SetupProgram.Target.Both, box.Home, box.Home));
        Assert.Equal(SetupUninstall.Outcome.Removed, removed.What);
        return removed;
    }

    // T2: both host configs are byte-identical to before the install.
    [Fact]
    public void UninstallRestoresBothHostConfigsByteForByte()
    {
        using var box = new SetupInstallerSandbox();
        InstallThenUninstallBoth(box);

        Assert.Equal(ExistingClaudeJson(), File.ReadAllText(SetupProgram.ClaudeJson(box.Home)));
        Assert.Equal(ExistingToml, File.ReadAllText(SetupProgram.CodexConfigToml(box.Home)));
    }

    // T3: no houseCARL file is left: the Claude tree, the Codex server dir, the record, the emptied data dir.
    [Fact]
    public void UninstallLeavesNoHouseCarlFile()
    {
        using var box = new SetupInstallerSandbox();
        InstallThenUninstallBoth(box);
        string codexServer = SetupProgram.CodexServerDir(box.Home, box.Home);

        Assert.False(Directory.Exists(SetupProgram.ClaudeSkillsDest(box.Home)));
        Assert.False(Directory.Exists(codexServer));
        Assert.False(File.Exists(SetupProgram.CodexSkillRecord(box.Home, box.Home)));
        Assert.False(Directory.Exists(Path.GetDirectoryName(codexServer)));
    }

    // T4: under the shared skills root only the recorded folders go; a foreign skill is untouched.
    [Fact]
    public void UninstallRemovesOnlyRecordedSkillsFromTheSharedRoot()
    {
        using var box = new SetupInstallerSandbox();
        InstallThenUninstallBoth(box);
        string shared = SetupProgram.CodexSkillsRoot(box.Home);

        Assert.False(Directory.Exists(Path.Combine(shared, "skill-one")));
        Assert.False(Directory.Exists(Path.Combine(shared, "skill-two")));
        Assert.False(Directory.Exists(Path.Combine(shared, "housecarl")));
        Assert.True(Directory.Exists(Path.Combine(shared, "someone-elses-skill")));
    }

    // T5: both configs keep a .bak of the file as it was, and the install's own .houseCARL.bak is untouched.
    [Fact]
    public void UninstallKeepsABackupOfEachConfig()
    {
        using var box = new SetupInstallerSandbox();
        InstallThenUninstallBoth(box);
        string claudeJson = SetupProgram.ClaudeJson(box.Home);

        Assert.True(File.Exists(SetupProgram.CodexConfigToml(box.Home) + SetupUninstall.BackupSuffix));
        Assert.Contains("housecarl", File.ReadAllText(claudeJson + SetupUninstall.BackupSuffix));
        Assert.Equal(ExistingClaudeJson(), File.ReadAllText(claudeJson + ".houseCARL.bak"));
    }

    // T6: an install too old to have left a Claude record still has its tree removed, and the run says so.
    [Fact]
    public void AnInstallWithNoClaudeRecordStillHasItsTreeRemoved()
    {
        using var box = new SetupInstallerSandbox();
        box.WritePackage("skill-one");
        box.Install(SetupProgram.Target.Claude);
        File.Delete(SetupProgram.ClaudeSkillRecord(box.Home));

        string said = SetupInstallerSandbox.Capture(() => SetupUninstall.TryUninstall(SetupProgram.Target.Claude, box.Home, box.Home));

        Assert.False(Directory.Exists(SetupProgram.ClaudeSkillsDest(box.Home)));
        Assert.Contains("before setup began recording", said);
    }

    // T7: with no Codex record nothing under the shared skills root is removed, the run says why, and the
    // server dir it owns still goes.
    [Fact]
    public void WithNoCodexRecordNoSharedSkillIsRemoved()
    {
        using var box = new SetupInstallerSandbox();
        box.WritePackage("skill-one");
        box.Install(SetupProgram.Target.Codex);
        SetupInstallerSandbox.WriteFile(Path.Combine(SetupProgram.CodexSkillsRoot(box.Home), "someone-elses-skill", "SKILL.md"), "not ours");
        File.Delete(SetupProgram.CodexSkillRecord(box.Home, box.Home));

        string said = SetupInstallerSandbox.Capture(() => SetupUninstall.TryUninstall(SetupProgram.Target.Codex, box.Home, box.Home));

        Assert.True(Directory.Exists(Path.Combine(SetupProgram.CodexSkillsRoot(box.Home), "skill-one")));
        Assert.True(Directory.Exists(Path.Combine(SetupProgram.CodexSkillsRoot(box.Home), "someone-elses-skill")));
        Assert.Contains("no record of which skills", said);
        Assert.False(Directory.Exists(SetupProgram.CodexServerDir(box.Home, box.Home)));
    }

    // T8: a held server exe refuses the removal at pre-flight, before anything is deleted.
    [Fact]
    public void AHeldServerExeRefusesTheUninstallBeforeAnyDelete()
    {
        using var box = new SetupInstallerSandbox();
        box.WritePackage("skill-one");
        box.Install(SetupProgram.Target.Claude);

        SetupUninstall.Result held;
        using (new FileStream(SetupProgram.ClaudeDestExe(box.Home), FileMode.Open, FileAccess.Read, FileShare.Read))
            held = SetupUninstall.TryUninstall(SetupProgram.Target.Claude, box.Home, box.Home);

        Assert.Equal(SetupUninstall.Outcome.ServerInUse, held.What);
        Assert.True(held.RefusedBeforeAnyDelete);
        Assert.True(Directory.Exists(SetupProgram.ClaudeSkillsDest(box.Home)));
        Assert.Contains("housecarl", File.ReadAllText(SetupProgram.ClaudeJson(box.Home)));
    }

    /// <summary>The config.toml shapes the splice must leave alone around our one table: label, before, after
    /// (null after for a file with no table of ours).</summary>
    public static TheoryData<string, string, string?> TomlCases => new()
    {
        { "our table in the middle: the comment above the next table stays",
          "# my own config\n[other]\nkey = 1\n\n[mcp_servers.housecarl]\ncommand = 'x'\n\n# my foo server\n[mcp_servers.foo]\ncommand = 'f'\n",
          "# my own config\n[other]\nkey = 1\n\n\n# my foo server\n[mcp_servers.foo]\ncommand = 'f'\n" },
        { "a CRLF file stays CRLF",
          "[other]\r\nkey = 1\r\n\r\n[mcp_servers.housecarl]\r\ncommand = 'x'\r\n",
          "[other]\r\nkey = 1\r\n" },
        { "mixed newlines keep each line's own",
          "[a]\r\nx = 1\n[mcp_servers.housecarl]\ncommand = 'x'\n[b]\r\nz = 1\n",
          "[a]\r\nx = 1\n[b]\r\nz = 1\n" },
        { "no trailing newline stays without one",
          "[mcp_servers.housecarl]\ncommand = 'x'\n\n[other]\nkey = 1",
          "\n[other]\nkey = 1" },
        { "our own subtables go with the table",
          "[mcp_servers.housecarl]\ncommand = 'x'\n[mcp_servers.housecarl.env]\nA = 'b'\n\n[other]\nk = 1\n",
          "\n[other]\nk = 1\n" },
        { "the quoted spelling of the key is ours",
          "[other]\nkey = 1\n\n[mcp_servers.\"housecarl\"]\ncommand = 'x'\n",
          "[other]\nkey = 1\n" },
        { "the spaced dotted spelling is ours",
          "[other]\nkey = 1\n\n[mcp_servers . housecarl]\ncommand = 'x'\n",
          "[other]\nkey = 1\n" },
        { "a same-prefix sibling table is not ours",
          "[mcp_servers.housecarl2]\ncommand = 'x'\n", null },
        { "an array of tables under our name is not ours",
          "[[mcp_servers.housecarl]]\ncommand = 'x'\n", null },
    };

    // T9: the TOML splice keeps every byte that is not ours, whatever shape the file is.
    [Theory]
    [MemberData(nameof(TomlCases))]
    public void TheTomlSpliceKeepsEveryByteThatIsNotOurs(string label, string before, string? after)
    {
        Assert.True(after == SetupUninstall.RemoveTomlTable(before, "housecarl"), label);
    }

    // T9: a config.toml that starts with a BOM has its entry removed and still starts with that BOM.
    [Fact]
    public void ABomConfigTomlKeepsItsBom()
    {
        using var box = new SetupInstallerSandbox();
        byte[] bom = { 0xEF, 0xBB, 0xBF };
        string path = Path.Combine(box.Root, "bom-config.toml");
        SetupInstallerSandbox.WriteFile(path, bom.Concat(Encoding.UTF8.GetBytes("[other]\nkey = 1\n\n[mcp_servers.housecarl]\ncommand = 'x'\n")).ToArray());

        Assert.Equal(SetupUninstall.ConfigEdit.Removed, SetupUninstall.UnregisterCodexMcpServer(path, "housecarl"));
        Assert.Equal(bom.Concat(Encoding.UTF8.GetBytes("[other]\nkey = 1\n")).ToArray(), File.ReadAllBytes(path));
    }

    // T10: a config.toml with only a same-prefix sibling table is NotFound, untouched, and not backed up.
    [Fact]
    public void ACodexConfigWithNoEntryOfOursIsNotFoundAndUntouched()
    {
        using var box = new SetupInstallerSandbox();
        const string sibling = "[mcp_servers.housecarl2]\ncommand = 'x'\n";
        string path = Path.Combine(box.Root, "stranger-config.toml");
        SetupInstallerSandbox.WriteFile(path, sibling);

        Assert.Equal(SetupUninstall.ConfigEdit.NotFound, SetupUninstall.UnregisterCodexMcpServer(path, "housecarl"));
        Assert.Equal(sibling, File.ReadAllText(path));
        Assert.False(File.Exists(path + SetupUninstall.BackupSuffix));
    }

    // T10: a ~/.claude.json with somebody else's server and none of ours is NotFound and untouched.
    [Fact]
    public void AClaudeJsonWithNoEntryOfOursIsNotFoundAndUntouched()
    {
        using var box = new SetupInstallerSandbox();
        string path = Path.Combine(box.Root, "stranger.claude.json");
        SetupInstallerSandbox.WriteFile(path, ExistingClaudeJson());

        Assert.Equal(SetupUninstall.ConfigEdit.NotFound, SetupUninstall.UnregisterClaudeMcpServer(path, "housecarl"));
        Assert.Equal(ExistingClaudeJson(), File.ReadAllText(path));
    }

    // T11: a ~/.claude.json with no mcpServers key round-trips; the key the install created goes with the entry.
    [Fact]
    public void TheMcpServersKeyTheInstallCreatedIsTakenBackOut()
    {
        using var box = new SetupInstallerSandbox();
        box.WritePackage("skill-one");
        const string noServers = "{\n  \"numStartups\": 7\n}";
        string claudeJson = SetupProgram.ClaudeJson(box.Home);
        SetupInstallerSandbox.WriteFile(claudeJson, noServers);

        SetupInstallerSandbox.Capture(() => box.Install(SetupProgram.Target.Claude));
        Assert.Contains("mcpServers", File.ReadAllText(claudeJson));
        SetupInstallerSandbox.Capture(() => SetupUninstall.TryUninstall(SetupProgram.Target.Claude, box.Home, box.Home));

        Assert.Equal(noServers, File.ReadAllText(claudeJson));
    }

    // T12: with a record, a skill folder houseCARL never installed survives under its own skills root, and the
    // run names what it left.
    [Fact]
    public void WithARecordAForeignSkillUnderTheClaudeTreeSurvives()
    {
        using var box = new SetupInstallerSandbox();
        box.WritePackage("skill-one");
        string dest = SetupProgram.ClaudeSkillsDest(box.Home);
        SetupInstallerSandbox.Capture(() => box.Install(SetupProgram.Target.Claude));
        SetupInstallerSandbox.WriteFile(Path.Combine(dest, "skills", "not-ours", "SKILL.md"), "somebody else's");

        string said = SetupInstallerSandbox.Capture(() => SetupUninstall.TryUninstall(SetupProgram.Target.Claude, box.Home, box.Home));

        Assert.True(Directory.Exists(Path.Combine(dest, "skills", "not-ours")));
        Assert.False(Directory.Exists(Path.Combine(dest, "skills", "skill-one")));
        Assert.False(Directory.Exists(Path.Combine(dest, "server")));
        Assert.Contains("still holds these", said);
    }

    // T12: with no record, the package layout is the route: a file houseCARL never wrote survives, the shipped
    // skills and manifest go, and the run says which route it took.
    [Fact]
    public void WithNoRecordAForeignFileUnderTheClaudeTreeSurvives()
    {
        using var box = new SetupInstallerSandbox();
        box.WritePackage("skill-one");
        string dest = SetupProgram.ClaudeSkillsDest(box.Home);
        SetupInstallerSandbox.Capture(() => box.Install(SetupProgram.Target.Claude));
        File.Delete(SetupProgram.ClaudeSkillRecord(box.Home));
        SetupInstallerSandbox.WriteFile(Path.Combine(dest, "my-notes.txt"), "mine");

        string said = SetupInstallerSandbox.Capture(() => SetupUninstall.TryUninstall(SetupProgram.Target.Claude, box.Home, box.Home));

        Assert.True(File.Exists(Path.Combine(dest, "my-notes.txt")));
        Assert.False(Directory.Exists(Path.Combine(dest, "skills")));
        Assert.False(File.Exists(Path.Combine(dest, ".claude-plugin", "plugin.json")));
        Assert.Contains("went by the files a houseCARL package ships", said);
    }
}
