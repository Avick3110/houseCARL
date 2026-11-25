using System.Diagnostics;
using System.Text.Json.Nodes;
using Xunit;
using SetupProgram = HousecarlSetup.Program;

namespace HousecarlMcpTests;

/// <summary>What a run of the built setup exe says about houseCARL's entry, in the plan before it writes and in the
/// summary after.</summary>
[Collection(SetupInstallerCollection.Name)]
[Trait("tier", "unit")]
public sealed class SetupRunTextTests
{
    const string OldExe = @"C:\old\housecarl-mcp.exe";
    // Codex settings that turn houseCARL off, with a value that must not be printed.
    const string KeptOff = "enabled = false\ntool_timeout_sec = 600\n\n[mcp_servers.housecarl.env]\nTOKEN = \"tok-secret\"\n";

    static SetupInstallerSandbox Box()
    {
        var box = new SetupInstallerSandbox();
        box.WritePackage("skill-one");
        Directory.CreateDirectory(box.Home);
        foreach (string file in Directory.GetFiles(AppContext.BaseDirectory, "houseCARL-Setup.*"))
            File.Copy(file, Path.Combine(Path.GetDirectoryName(box.Src)!, Path.GetFileName(file)));
        return box;
    }

    sealed record Ran(int ExitCode, string Out, string Err)
    {
        static readonly string[] Closings = [" is installed for ", "houseCARL is removed from "];
        int Cut => Closings.Select(c => Out.IndexOf(c, StringComparison.Ordinal)).Max();
        public string Plan => Out[..Cut];
        public string Summary => Out[Cut..];
    }

    static Ran Setup(SetupInstallerSandbox box, params string[] args)
    {
        ProcessStartInfo psi = new(Path.Combine(Path.GetDirectoryName(box.Src)!, "houseCARL-Setup.exe"))
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        foreach (string a in args.Append("--yes")) psi.ArgumentList.Add(a);
        psi.Environment["HOUSECARL_SETUP_HOME"] = box.Home;
        using Process p = Process.Start(psi)!;
        p.StandardInput.Close();
        Task<string> said = p.StandardOutput.ReadToEndAsync();
        Task<string> err = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(60_000)) { p.Kill(entireProcessTree: true); Assert.Fail("setup did not finish"); }
        return new Ran(p.ExitCode, said.Result, err.Result);
    }

    static string Toml(SetupInstallerSandbox box) => SetupProgram.CodexConfigToml(box.Home);
    static string ClaudeJson(SetupInstallerSandbox box) => SetupProgram.ClaudeJson(box.Home);

    static void WriteToml(SetupInstallerSandbox box, string exe, string rest)
        => SetupInstallerSandbox.WriteFile(Toml(box), "[mcp_servers.housecarl]\ncommand = '" + exe + "'\n" + rest);

    static void WriteClaude(SetupInstallerSandbox box, JsonObject entry)
        => SetupInstallerSandbox.WriteFile(ClaudeJson(box),
            new JsonObject { ["mcpServers"] = new JsonObject { ["housecarl"] = entry } }.ToJsonString());

    /// <summary>What the plan or the summary says beside a config's path.</summary>
    static string ConfigLine(string part, string path)
        => part.Split(Environment.NewLine).Select(l => l.Trim())
            .Single(l => l.StartsWith(path + " ", StringComparison.Ordinal))[path.Length..].Trim();

    [Fact]
    public void AFirstInstallSaysRegisteredAndNamesTheCopy()
    {
        using var box = Box();
        SetupInstallerSandbox.WriteFile(Toml(box), "model = \"m\"\n");

        Ran ran = Setup(box, "--both");

        Assert.Equal(0, ran.ExitCode);
        Assert.Contains("registers the server", ran.Plan);
        Assert.Contains("registered the server", ran.Summary);
        Assert.Contains("is kept in config.toml.houseCARL.bak", ran.Plan);
        Assert.Contains("is kept in config.toml.houseCARL.bak", ran.Summary);
        Assert.DoesNotContain(".claude.json.houseCARL", ran.Out);
        Assert.DoesNotContain("your settings", ran.Out);
    }

    [Fact]
    public void KeptClaudeSettingsAreNamedWithoutTheirValues()
    {
        using var box = Box();
        WriteClaude(box, new JsonObject
        {
            ["type"] = "stdio", ["command"] = OldExe, ["args"] = new JsonArray("--x"),
            ["env"] = new JsonObject { ["TOKEN"] = "tok-secret" }, ["timeout"] = 600000,
        });

        Ran ran = Setup(box, "--claude");

        Assert.Contains("keeps your settings on it: args, env, timeout" + Environment.NewLine, ran.Plan);
        Assert.Contains("kept your settings on it: args, env, timeout" + Environment.NewLine, ran.Summary);
        Assert.Contains("registered the server", ran.Summary);
        Assert.DoesNotContain("tok-secret", ran.Out);
        Assert.DoesNotContain("600000", ran.Out);
    }

    [Fact]
    public void SetupsOwnClaudeKeysAreNotNamedAsTheUsers()
    {
        using var box = Box();
        WriteClaude(box, new JsonObject { ["type"] = "stdio", ["command"] = OldExe, ["args"] = new JsonArray() });

        Assert.DoesNotContain("your settings", Setup(box, "--claude").Out);
    }

    [Fact]
    public void KeptCodexSettingsAreNamedWithoutTheirValues()
    {
        using var box = Box();
        WriteToml(box, OldExe, "# a = 1\nx.a = 1\nx.b = '''\n[mcp_servers.housecarl.fake]\n'''\n" + KeptOff + "\n[mcp_servers.other]\ncommand = 'x'\n\n[mcp_servers.other.env]\nB = '1'\n"
            + "\n[mcp_servers.housecarl.tools.a]\napproval_mode = \"approve\"\n\n[mcp_servers.housecarl.tools.b]\n"
            + "\n[[mcp_servers.housecarl.env_vars]]\nname = \"A\"\n");

        Ran ran = Setup(box, "--codex");

        Assert.Contains("keeps your settings on it: x, enabled, tool_timeout_sec, env, tools, env_vars" + Environment.NewLine, ran.Plan);
        Assert.Contains("kept your settings on it: x, enabled, tool_timeout_sec, env, tools, env_vars" + Environment.NewLine, ran.Summary);
        Assert.DoesNotContain("tok-secret", ran.Out);
        Assert.DoesNotContain("600", ran.Out.Replace(box.Root, ""));
    }

    [Theory]
    [InlineData("x = [\n[mcp_servers.housecarl]\ncommand = '" + OldExe + "'\ncert = '''\nc2VjcmV0dG9rZW4=\n'''\n")]
    [InlineData("x = 1]\n[mcp_servers.housecarl]\ncommand = '" + OldExe + "'\ncert = '''\nc2VjcmV0dG9rZW4=\n'''\n")]
    [InlineData("x = '''\n[mcp_servers.housecarl]\ncommand = '" + OldExe + "'\ncert = '''\nc2VjcmV0dG9rZW4=\n'''\n")]
    [InlineData("x = [\n[mcp_servers.housecarl]\nurl = 'u'\n[mcp_servers.other]\ncommand = 'x'\n[mcp_servers.housecarl.c2VjcmV0dG9rZW4]\n")]
    [InlineData("x = [\n[mcp_servers.housecarl.c2VjcmV0dG9rZW4]\n")]
    public void NoSettingIsNamedInAConfigLeftOpen(string toml)
    {
        using var box = Box();
        SetupInstallerSandbox.WriteFile(Toml(box), toml);

        Ran ran = Setup(box, "--codex");

        Assert.Equal(0, ran.ExitCode);
        Assert.DoesNotContain("c2VjcmV0dG9rZW4", ran.Out);
        Assert.DoesNotContain("your settings", ran.Out);
    }

    // Pins unchanged behaviour: no name is printed, but that houseCARL is off still is.
    [Fact]
    public void AConfigLeftOpenStillSaysHouseCarlIsOff()
    {
        using var box = Box();
        WriteToml(box, OldExe, "enabled = false\nx = [\n");

        Assert.Contains("still turned off in Codex", Setup(box, "--codex").Summary);
    }

    [Theory]
    [InlineData("enabled = false # for now\n", true)]
    [InlineData("enabled = true\n", false)]
    [InlineData("\n[mcp_servers.housecarl.tools]\nenabled = false\n", false)]
    public void TheSummarySaysWhenCodexStillHasHouseCarlOff(string settings, bool off)
    {
        using var box = Box();
        WriteToml(box, OldExe, settings);

        Assert.Equal(off, Setup(box, "--codex").Summary.Contains("still turned off in Codex"));
        // The second run changes nothing, and says so still.
        Assert.Equal(off, Setup(box, "--codex").Summary.Contains("still turned off in Codex"));
    }

    [Fact]
    public void AReRunThatChangesNothingSaysAlreadyRegistered()
    {
        using var box = Box();
        Setup(box, "--both");
        File.AppendAllText(Toml(box), KeptOff);

        Ran ran = Setup(box, "--both");

        foreach (string part in new[] { ran.Plan, ran.Summary })
        {
            Assert.Equal(2, part.Split("already registered, so this file is not written to").Length - 1);
            Assert.DoesNotContain("registers the server", part);
            Assert.DoesNotContain("registered the server", part);
            Assert.DoesNotContain(".bak", part);
        }
        Assert.Contains("kept your settings on it: enabled, tool_timeout_sec, env", ran.Summary);
    }

    [Fact]
    public void ThePlanOfARunThatChangesNothingNamesTheSettingsAndThatHouseCarlIsOff()
    {
        using var box = Box();
        WriteToml(box, SetupProgram.CodexDestExe(box.Home, box.Home), KeptOff);

        Ran ran = Setup(box, "--codex");

        Assert.Contains("enabled, tool_timeout_sec, env" + Environment.NewLine, ran.Plan);
        Assert.Contains("still turned off in Codex", ran.Plan);
    }

    [Fact]
    public void ThePlanNamesTheCopyOfAReplacedEntry()
    {
        using var box = Box();
        WriteToml(box, @"C:\x\wrapper.exe", "");

        Assert.Contains("config.toml.houseCARL.bak", Setup(box, "--codex").Plan);
    }

    [Fact]
    public void AReRunThatChangesNothingDoesNotSayItWritesOrRegisters()
    {
        using var box = Box();
        Setup(box, "--both");

        Ran ran = Setup(box, "--both");

        Assert.DoesNotContain("This will replace", ran.Plan);
        Assert.DoesNotContain("This will write", ran.Plan);
        Assert.DoesNotContain("registering", ran.Plan);
    }

    [Fact]
    public void AFirstInstallOverAnEntryThatIsThereDoesNotSayItWrites()
    {
        using var box = Box();
        WriteToml(box, SetupProgram.CodexDestExe(box.Home, box.Home), "");

        Assert.DoesNotContain("This will write", Setup(box, "--codex").Plan);
    }

    [Fact]
    public void ARemovalPlanDoesNotSayAnEntryIsThere()
    {
        using var box = Box();
        Setup(box, "--both");
        File.Delete(ClaudeJson(box));
        SetupInstallerSandbox.WriteFile(Toml(box), "model = \"m\"\n");

        Ran ran = Setup(box, "--uninstall", "--both");

        foreach (string path in new[] { ClaudeJson(box), Toml(box) })
            Assert.StartsWith("if ", ConfigLine(ran.Plan, path));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(Toml(box))!, "*.bak"));
    }

    [Fact]
    public void AReplacedEntryIsSaidWithItsCopy()
    {
        using var box = Box();
        WriteClaude(box, new JsonObject { ["command"] = "cmd", ["timeout"] = 5 });
        SetupInstallerSandbox.WriteFile(Toml(box), "[mcp_servers.housecarl]\nurl = 'http://127.0.0.1:1'\nenabled = false\n");

        Ran ran = Setup(box, "--both");

        foreach (string path in new[] { ClaudeJson(box), Toml(box) })
        {
            Assert.Contains("replaces", ConfigLine(ran.Plan, path));
            Assert.Contains("replaced", ConfigLine(ran.Summary, path));
        }
        Assert.Contains("is kept in .claude.json.houseCARL.bak", ran.Summary);
        Assert.Contains("is kept in config.toml.houseCARL.bak", ran.Summary);
        Assert.DoesNotContain("your settings", ran.Out);
        Assert.DoesNotContain("still turned off", ran.Out);
    }

    [Theory]
    [InlineData("\"env\":{\"A\":\"1\"},\"env\":{\"B\":\"2\"}")]
    [InlineData("\"args\":[],\"args\":[]")]
    public void AClaudeEntryThatHoldsAKeyTwiceIsSaidReplacedWithItsCopy(string twice)
    {
        using var box = Box();
        SetupInstallerSandbox.WriteFile(ClaudeJson(box),
            "{\"mcpServers\":{\"housecarl\":{\"type\":\"stdio\",\"command\":\"C:/old/housecarl-mcp.exe\"," + twice + "}}}");

        Ran ran = Setup(box, "--claude");

        Assert.Equal(0, ran.ExitCode);
        Assert.Contains("replaces", ConfigLine(ran.Plan, ClaudeJson(box)));
        Assert.Contains("replaced", ConfigLine(ran.Summary, ClaudeJson(box)));
        foreach (string part in new[] { ran.Plan, ran.Summary })
        {
            Assert.Contains(".claude.json.houseCARL.bak", part);
            Assert.DoesNotContain("your settings", part);
        }
    }

    [Fact]
    public void ASubtableThatWasThereBeforeTheTableIsNamed()
    {
        using var box = Box();
        SetupInstallerSandbox.WriteFile(Toml(box), "[mcp_servers.housecarl.tools.a]\nx = 1\n[mcp_servers.housecarl.tools.b]\nx = 1\n");

        Assert.Contains("kept your settings on it: tools" + Environment.NewLine, Setup(box, "--codex").Summary);
    }

    // An entry whose command is there but unread, one with none, and one a subtable of stays: no false reason, and
    // not the whole of it.
    [Theory]
    [InlineData("[mcp_servers.housecarl]\ncommand = '''C:\\x\\housecarl-mcp.exe'''\n")]
    [InlineData("[mcp_servers.housecarl]\nenabled = false\n")]
    [InlineData("[mcp_servers.housecarl]\nurl = 'u'\n\n[mcp_servers.other]\ncommand = 'x'\n\n[mcp_servers.housecarl.env]\nA = '1'\n")]
    public void AReplacedEntryIsNotGivenAFalseReason(string toml)
    {
        using var box = Box();
        SetupInstallerSandbox.WriteFile(Toml(box), toml);

        Ran ran = Setup(box, "--codex");

        foreach (string said in new[] { ConfigLine(ran.Plan, Toml(box)), ConfigLine(ran.Summary, Toml(box)) })
        {
            Assert.Contains("replace", said);
            Assert.DoesNotContain("its command", said);
            Assert.DoesNotContain("whole", said);
        }
    }

    [Fact]
    public void ASubtableLeftOutsideAReplacedTableIsNamed()
    {
        using var box = Box();
        SetupInstallerSandbox.WriteFile(Toml(box), "[mcp_servers.housecarl]\nurl = 'u'\n\n[mcp_servers.housecarl.http_headers]\nA = '1'\n"
            + "\n[[mcp_servers.housecarl.env_vars]]\nname = 'A'\n\n[mcp_servers.other]\ncommand = 'x'\n\n[mcp_servers.housecarl.env]\nA = '1'\n");

        Assert.Contains("kept your settings on it: env_vars, env" + Environment.NewLine, Setup(box, "--codex").Summary);
    }

    [Fact]
    public void ANumberedCopyIsNamedInThePlanAndTheSummary()
    {
        using var box = Box();
        SetupInstallerSandbox.WriteFile(Toml(box) + ".houseCARL.bak", "an older file");
        WriteToml(box, OldExe, "");

        Ran ran = Setup(box, "--codex");

        Assert.Contains("is kept in config.toml.houseCARL.2.bak", ran.Plan);
        Assert.Contains("is kept in config.toml.houseCARL.2.bak", ran.Summary);
    }

    [Fact]
    public void ARemovalSummaryNamesItsNumberedCopy()
    {
        using var box = Box();
        SetupInstallerSandbox.WriteFile(Toml(box) + ".houseCARL.uninstall.bak", "an older file");
        SetupInstallerSandbox.WriteFile(ClaudeJson(box) + ".houseCARL.uninstall.bak", "an older file");
        WriteToml(box, OldExe, "");
        WriteClaude(box, new JsonObject { ["command"] = OldExe });

        Ran ran = Setup(box, "--uninstall", "--both");

        Assert.Contains("is kept in config.toml.houseCARL.uninstall.2.bak", ran.Summary);
        Assert.Contains("is kept in .claude.json.houseCARL.uninstall.2.bak", ran.Summary);
        Assert.DoesNotContain(".houseCARL.uninstall.bak", ran.Plan);
    }

    // Like the held config below it is not a stop at the plan; and no copy is taken, so none is promised.
    [Fact]
    public void AConfigThatDoesNotParseIsReportedByTheRunNotThePlan()
    {
        using var box = Box();
        SetupInstallerSandbox.WriteFile(ClaudeJson(box), "{\"mcpServers\":{\"a\":{},}}");

        Ran ran = Setup(box, "--claude");

        Assert.Equal(1, ran.ExitCode);
        Assert.Contains("registers", ConfigLine(ran.Out, ClaudeJson(box)));
        Assert.DoesNotContain("copy", ConfigLine(ran.Out, ClaudeJson(box)));
        Assert.Empty(Directory.GetFiles(box.Home, "*.bak"));
    }

    // Pins unchanged behaviour: a config another program holds open is the "fully quit" refusal, not a stop at the plan.
    [Fact]
    public void AHeldConfigIsReportedByTheRunNotThePlan()
    {
        using var box = Box();
        WriteToml(box, OldExe, "");

        Ran ran;
        using (new FileStream(Toml(box), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            ran = Setup(box, "--codex");

        Assert.Equal(1, ran.ExitCode);
        Assert.Contains("fully quit", ran.Err);
        Assert.Contains("registers", ConfigLine(ran.Out, Toml(box)));
        Assert.DoesNotContain("copy", ConfigLine(ran.Out, Toml(box)));
    }
}
