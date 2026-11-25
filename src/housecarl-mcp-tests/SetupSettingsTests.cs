using System.Text;
using System.Text.Json.Nodes;
using Xunit;
using SetupProgram = HousecarlSetup.Program;
using SetupUninstall = HousecarlSetup.Uninstall;

namespace HousecarlMcpTests;

/// <summary>A run of setup changes only its own keys on houseCARL's entry, writes nothing when that changes
/// nothing, and never writes over a copy it took before.</summary>
[Collection(SetupInstallerCollection.Name)]
[Trait("tier", "unit")]
public sealed class SetupSettingsTests
{
    const string OldExe = @"C:\old\housecarl-mcp.exe";

    static SetupInstallerSandbox Box()
    {
        var box = new SetupInstallerSandbox();
        box.WritePackage("skill-one");
        return box;
    }

    static string ClaudeJson(SetupInstallerSandbox box) => SetupProgram.ClaudeJson(box.Home);
    static string Toml(SetupInstallerSandbox box) => SetupProgram.CodexConfigToml(box.Home);
    static string ClaudeExe(SetupInstallerSandbox box) => SetupProgram.ClaudeDestExe(box.Home);
    static string CodexExe(SetupInstallerSandbox box) => SetupProgram.CodexDestExe(box.Home, box.Home);
    static string Literal(string exe) => "'" + exe + "'";
    static string Escaped(string exe) => "\"" + exe.Replace(@"\", @"\\") + "\"";

    static void Install(SetupInstallerSandbox box, SetupProgram.Target target)
        => SetupInstallerSandbox.Capture(() => Assert.Equal(SetupProgram.InstallOutcome.Installed, box.Install(target).Outcome));

    static void Uninstall(SetupInstallerSandbox box, SetupProgram.Target target)
        => SetupInstallerSandbox.Capture(() => SetupUninstall.TryUninstall(target, box.Home, box.Home));

    static string[] Copies(string path)
        => Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".houseCARL*")
            .Select(p => Path.GetFileName(p)[Path.GetFileName(path).Length..]).Order(StringComparer.Ordinal).ToArray();

    static string ClaudeWith(JsonNode? entry) => new JsonObject
    {
        ["numStartups"] = 7,
        ["mcpServers"] = new JsonObject
        {
            ["other"] = new JsonObject { ["command"] = "x", ["env"] = new JsonObject { ["K"] = "v" }, ["timeout"] = 5 },
            ["housecarl"] = entry,
        },
        ["projects"] = new JsonObject
        {
            ["C:/p"] = new JsonObject { ["mcpServers"] = new JsonObject { ["housecarl"] = new JsonObject { ["command"] = "y" } } },
        },
    }.ToJsonString();

    static void AssertClaude(SetupInstallerSandbox box, JsonNode expectedEntry)
        => Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse(ClaudeWith(expectedEntry)), JsonNode.Parse(File.ReadAllText(ClaudeJson(box)))),
            File.ReadAllText(ClaudeJson(box)));

    // ---- Claude Code -------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData(OldExe)]
    [InlineData("C:/old/HouseCARL-MCP.EXE")]
    public void AClaudeEntryKeepsItsSettings(string? was)
    {
        using var box = Box();
        JsonObject Entry(string exe) => new()
        {
            ["env"] = new JsonObject { ["HOUSECARL_MAX_SCHEMA_DEPTH"] = "10" },
            ["type"] = "stdio",
            ["command"] = exe,
            ["args"] = new JsonArray("--HouseCarl:MaxPlugins=5"),
            ["timeout"] = 600000,
            ["alwaysLoad"] = true,
        };
        SetupInstallerSandbox.WriteFile(ClaudeJson(box), ClaudeWith(Entry(was ?? ClaudeExe(box))));

        Install(box, SetupProgram.Target.Claude);

        AssertClaude(box, Entry(ClaudeExe(box)));
    }

    [Fact]
    public void AClaudeEntryThatRunsHouseCarlGetsItsTypeAndNoArgs()
    {
        using var box = Box();
        SetupInstallerSandbox.WriteFile(ClaudeJson(box), ClaudeWith(new JsonObject { ["command"] = ClaudeExe(box), ["timeout"] = 5 }));

        Install(box, SetupProgram.Target.Claude);

        AssertClaude(box, new JsonObject { ["command"] = ClaudeExe(box), ["timeout"] = 5, ["type"] = "stdio" });
    }

    public static TheoryData<string, string> ClaudeEntriesNotSetups => new()
    {
        { "wrapper", """{"type":"stdio","command":"cmd","args":["/c","C:/x/housecarl-mcp.exe"],"env":{"A":"1"}}""" },
        { "url", """{"type":"http","url":"http://127.0.0.1:1","timeout":5}""" },
        { "command not a string", """{"command":["housecarl-mcp.exe"],"timeout":5}""" },
        { "null", "null" },
        { "string", "\"x\"" },
    };

    // Pins unchanged behaviour: an entry that is not setup's is replaced.
    [Theory]
    [MemberData(nameof(ClaudeEntriesNotSetups))]
    public void AClaudeEntryThatDoesNotRunHouseCarlIsReplacedWhole(string label, string entry)
    {
        using var box = Box();
        string before = ClaudeWith(JsonNode.Parse(entry));
        SetupInstallerSandbox.WriteFile(ClaudeJson(box), before);

        Install(box, SetupProgram.Target.Claude);

        AssertClaude(box, new JsonObject { ["type"] = "stdio", ["command"] = ClaudeExe(box), ["args"] = new JsonArray() });
        Assert.True(before == File.ReadAllText(ClaudeJson(box) + ".houseCARL.bak"), label);
    }

    // Claude Code reads such an entry; setup must not stop at it half done.
    [Theory]
    [InlineData(null, "\"env\":{\"A\":\"1\"},\"env\":{\"B\":\"2\"}")]
    [InlineData(OldExe, "\"env\":{\"A\":\"1\"},\"env\":{\"B\":\"2\"}")]
    [InlineData(null, "\"args\":[],\"args\":[]")]
    [InlineData(OldExe, "\"args\":[],\"args\":[]")]
    [InlineData("cmd", "\"env\":{},\"env\":{}")]
    public void AClaudeEntryThatHoldsAKeyTwiceIsReplaced(string? was, string twice)
    {
        using var box = Box();
        string before = ClaudeWith("[E]").Replace("\"[E]\"",
            "{\"type\":\"stdio\",\"command\":" + Escaped(was ?? ClaudeExe(box)) + "," + twice + ",\"timeout\":5}");
        SetupInstallerSandbox.WriteFile(ClaudeJson(box), before);

        Install(box, SetupProgram.Target.Claude);

        AssertClaude(box, new JsonObject { ["type"] = "stdio", ["command"] = ClaudeExe(box), ["args"] = new JsonArray() });
        Assert.Equal(before, File.ReadAllText(ClaudeJson(box) + ".houseCARL.bak"));
    }

    // Pins unchanged behaviour: a repeated key inside a setting, or on another server's entry, is not setup's to read.
    [Fact]
    public void AKeyHeldTwiceElsewhereInMcpServersDoesNotReplaceTheEntry()
    {
        using var box = Box();
        string Text(string exe) => "{\"mcpServers\":{\"other\":{\"a\":1,\"a\":2},\"housecarl\":{\"type\":\"stdio\",\"command\":"
            + Escaped(exe) + ",\"env\":{\"A\":\"1\",\"A\":\"2\"},\"timeout\":5}}}";
        SetupInstallerSandbox.WriteFile(ClaudeJson(box), Text(OldExe));

        Install(box, SetupProgram.Target.Claude);

        Assert.Equal(Text(ClaudeExe(box)), string.Concat(File.ReadAllText(ClaudeJson(box)).Where(c => !char.IsWhiteSpace(c))));
    }

    [Theory]
    [InlineData("{\"mcpServers\":{\"housecarl\":{\"timeout\":5,\"command\":\"<exe>\",\"type\":\"stdio\"}}}")]
    [InlineData("{\r\n\t\"mcpServers\": {\r\n\t\t\"housecarl\": { \"type\": \"stdio\", \"command\": \"<exe>\", \"args\": [] }\r\n\t}\r\n}\r\n")]
    public void AClaudeReRunThatChangesNothingWritesNothing(string text)
    {
        using var box = Box();
        text = text.Replace("<exe>", ClaudeExe(box).Replace(@"\", @"\\"));
        SetupInstallerSandbox.WriteFile(ClaudeJson(box), text);
        DateTime then = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(ClaudeJson(box), then);

        Install(box, SetupProgram.Target.Claude);

        Assert.Equal(text, File.ReadAllText(ClaudeJson(box)));
        Assert.Equal(then, File.GetLastWriteTimeUtc(ClaudeJson(box)));
        Assert.Empty(Copies(ClaudeJson(box)));
    }

    // ---- Codex -------------------------------------------------------------

    /// <summary>Where settings sit around a table whose command is houseCARL's exe. [C] is the command value.</summary>
    public static TheoryData<string, string> CodexLayouts
    {
        get
        {
            const string head = "[mcp_servers.housecarl]\n";
            const string table = head + "command = [C]\n";
            const string apply = "[mcp_servers.housecarl.tools.housecarl_apply]\napproval_mode = \"approve\"\n";
            const string env = "[mcp_servers.housecarl.env]\nHOUSECARL_MAX_SCHEMA_DEPTH = \"10\"\n";
            // Read as statements, these lines are a table of ours that comes first.
            const string decoy = "z = '''\n[mcp_servers.housecarl]\ncommand = 'y'\n'''\n";
            const string other = "[mcp_servers.other]\ncommand = \"x\"\ntool_timeout_sec = 5\n";
            return new()
            {
                { "scalar keys", table + "tool_timeout_sec = 600\nenabled = false\ncwd = 'C:\\x'\n\n" + other },
                { "subtables right after the table", table + "\n" + env + "\n" + apply + "\n" + other },
                { "subtable after another server", table + "\n" + other + "\n" + apply },
                { "subtable before the table", env + "\n" + table + "\n" + other },
                { "inline env", table + "env = { HOUSECARL_MAX_SCHEMA_DEPTH = \"10\" }\n\n" + other },
                { "dotted env", table + "env.HOUSECARL_MAX_SCHEMA_DEPTH = \"10\"\n\n" + other },
                { "key before command", head + "tool_timeout_sec = 600\ncommand = [C]\n\n" + other },
                { "multi-line array", table + "enabled_tools = [\n  \"housecarl_records\",\n]\n\n" + other },
                { "a line like a header in an array before command", head + "x = [\n[\"a\"]\n]\ncommand = [C]\n\n" + other },
                { "a line like a command in a triple-quoted string", head + "x = \"\"\"\ncommand = 'y' \\\"\"\"\"\ncommand = [C]\n\n" + other },
                { "a line like our header in a triple-quoted string", decoy + table },
                { "a bracket in a comment", "# [\n" + decoy + table + "\n" + other },
                { "a bracket in a string", "x = \"[\"\ny = '{'\n" + decoy + table + "\n" + other },
                { "an inline table", "x = { y = 1 }\n" + decoy + table + "\n" + other },
                { "a quote escaped in a string", "x = \"a\\\"[\"\n" + decoy + table + "\n" + other },
                { "a backslash ending a literal string", "x = ['a\\', 'b']\n" + decoy + table + "\n" + other },
                { "an unclosed string above", "x = 'it\n" + decoy + table + "\n" + other },
                { "an array left open", head + "enabled_tools = [\ncommand = [C]\n\n" + other },
                { "a triple quote left open", head + "x = '''\ncommand = [C]\n\n" + other },
                { "a multi-line value in a file left open", "x = [\n" + table + "cert = '''\nc2VjcmV0dG9rZW4=\n'''\n" },
                { "args of the user's", table + "args = [\"--HouseCarl:MaxPlugins=5\"]\n\n" + other },
                { "comments in and after the table", head + "# why the timeout\ntool_timeout_sec = 600\ncommand = [C] # mine\n# last\n\n# next\n" + other },
                { "array of tables", table + "\n[[mcp_servers.housecarl.env_vars]]\nname = \"A\"\n\n" + other },
                { "CRLF", (table + "tool_timeout_sec = 600\n\n" + env + "\n" + other).Replace("\n", "\r\n") },
                { "mixed newlines", "[a]\r\nx = 1\n" + table + "enabled = false\r\n" },
                { "no newline at the end", other + "\n" + head + "enabled = false\ncommand = [C]" },
                { "quoted command key", head + "\"command\"=[C]\n" },
                { "quoted header", "[mcp_servers.\"housecarl\"]\ncommand = [C]\nenabled = false\n\n" + other },
                { "spaced header", "[ mcp_servers . housecarl ]\n  command  =\t[C]\nenabled = false\n\n" + other },
                { "header with a comment", "[mcp_servers.housecarl] # mine\ncommand = [C]\nenabled = false\n\n" + other },
                { "BOM", "\uFEFF" + table + "enabled = false\n" },
            };
        }
    }

    [Theory]
    [MemberData(nameof(CodexLayouts))]
    public void OnlyTheCodexCommandValueChanges(string label, string layout)
    {
        foreach (Func<string, string> spell in new[] { Literal, Escaped })
        {
            using var box = Box();
            SetupInstallerSandbox.WriteFile(Toml(box), Encoding.UTF8.GetBytes(layout.Replace("[C]", spell(OldExe))));

            Install(box, SetupProgram.Target.Codex);

            string after = Encoding.UTF8.GetString(File.ReadAllBytes(Toml(box)));
            Assert.True(layout.Replace("[C]", Literal(CodexExe(box))) == after, label + ":\n" + after);
        }
    }

    [Theory]
    [MemberData(nameof(CodexLayouts))]
    public void ACodexReRunThatChangesNothingWritesNothing(string label, string layout)
    {
        foreach (Func<string, string> spell in new[] { Literal, Escaped })
        {
            using var box = Box();
            byte[] before = Encoding.UTF8.GetBytes(layout.Replace("[C]", spell(CodexExe(box))));
            SetupInstallerSandbox.WriteFile(Toml(box), before);
            DateTime then = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(Toml(box), then);

            Install(box, SetupProgram.Target.Codex);

            Assert.True(before.SequenceEqual(File.ReadAllBytes(Toml(box))), label);
            Assert.Equal(then, File.GetLastWriteTimeUtc(Toml(box)));
            Assert.Empty(Copies(Toml(box)));
        }
    }

    /// <summary>The keys of a table that is not setup's, as they are written under its header.</summary>
    public static TheoryData<string> CodexTablesNotSetups => new()
    {
        "url = \"http://127.0.0.1:1/mcp\"\ntool_timeout_sec = 600\n",
        "command = \"cmd\"\nargs = [\"/c\", \"housecarl-mcp.exe\"]\n",
        "command = '''C:\\x\\housecarl-mcp.exe'''\n",
        "command = \"\"\"\nC:\\\\x\\\\housecarl-mcp.exe\"\"\"\n",
        "command = \"C:\\x\\housecarl-mcp.exe\"\n",
        "command = 'C:\\o'neil\\housecarl-mcp.exe'\n",
        "command = 'C:\\x\\housecarl-mcp.exe\n",
        "command =\n",
        "command = 'C:\\x\\housecarl-mcp.exe' junk\n",
        "command = QC:/x/housecarl-mcp.exeQ\nenabled = true\n",
        "command.x = 'C:\\x\\housecarl-mcp.exe'\n",
        "\n[mcp_servers.housecarl.env]\ncommand = 'C:\\x\\housecarl-mcp.exe'\n",
    };

    [Theory]
    [MemberData(nameof(CodexTablesNotSetups))]
    public void ACodexTableThatDoesNotRunHouseCarlIsReplacedWhole(string keys)
    {
        using var box = Box();
        const string above = "[other]\nkey = 1\n\n";
        const string below = "\n# next\n[mcp_servers.other]\ncommand = \"x\"\n";
        string before = above + "[mcp_servers.housecarl]\n" + keys + below;
        SetupInstallerSandbox.WriteFile(Toml(box), before);

        Install(box, SetupProgram.Target.Codex);

        Assert.Equal(above + "[mcp_servers.housecarl]\ncommand = " + Literal(CodexExe(box)) + "\n" + below, File.ReadAllText(Toml(box)));
        Assert.Equal(before, File.ReadAllText(Toml(box) + ".houseCARL.bak"));
    }

    [Fact]
    public void ACodexFileThatEndsAtAnEmptyCommandIsReplacedWhole()
    {
        using var box = Box();
        SetupInstallerSandbox.WriteFile(Toml(box), "[mcp_servers.housecarl]\ncommand =");

        Install(box, SetupProgram.Target.Codex);

        Assert.Equal("[mcp_servers.housecarl]\ncommand = " + Literal(CodexExe(box)) + "\n", File.ReadAllText(Toml(box)));
    }

    // Pins unchanged behaviour: subtables with no table of ours stay, and the table is added.
    [Fact]
    public void CodexSubtablesWithNoTableStayAndTheTableIsAdded()
    {
        using var box = Box();
        const string env = "[mcp_servers.housecarl.env]\nA = \"1\"\n";
        SetupInstallerSandbox.WriteFile(Toml(box), env);

        Install(box, SetupProgram.Target.Codex);

        Assert.Equal(env + "\n[mcp_servers.housecarl]\ncommand = " + Literal(CodexExe(box)) + "\n", File.ReadAllText(Toml(box)));
    }

    // ---- first install: pins unchanged behaviour ---------------------------

    [Fact]
    public void AFirstInstallWritesTheSameTextAsBefore()
    {
        using var box = Box();

        Install(box, SetupProgram.Target.Both);

        string exe = ClaudeExe(box).Replace(@"\", @"\\");
        Assert.Equal(
            "{\n  \"mcpServers\": {\n    \"housecarl\": {\n      \"type\": \"stdio\",\n      \"command\": \"" + exe
            + "\",\n      \"args\": []\n    }\n  }\n}", File.ReadAllText(ClaudeJson(box)).Replace("\r\n", "\n"));
        Assert.Equal(
            "# houseCARL MCP server (added by houseCARL-Setup)\n[mcp_servers.housecarl]\ncommand = " + Literal(CodexExe(box)) + "\n",
            File.ReadAllText(Toml(box)));
        Assert.Empty(Copies(ClaudeJson(box)));
        Assert.Empty(Copies(Toml(box)));
    }

    [Theory]
    [InlineData("", "# houseCARL MCP server (added by houseCARL-Setup)\n")]
    [InlineData("\n\n", "# houseCARL MCP server (added by houseCARL-Setup)\n")]
    [InlineData("[other]\nkey = 1\n\n\n", "[other]\nkey = 1\n\n")]
    public void AFirstCodexInstallAddsTheTableAtTheEnd(string before, string above)
    {
        using var box = Box();
        SetupInstallerSandbox.WriteFile(Toml(box), before);

        Install(box, SetupProgram.Target.Codex);

        Assert.Equal(above + "[mcp_servers.housecarl]\ncommand = " + Literal(CodexExe(box)) + "\n", File.ReadAllText(Toml(box)));
    }

    [Fact]
    public void AFirstCodexInstallIntoACrlfFileIsCrlf()
    {
        using var box = Box();
        SetupInstallerSandbox.WriteFile(Toml(box), "[other]\r\nkey = 1\r\n");

        Install(box, SetupProgram.Target.Codex);

        Assert.Equal("[other]\r\nkey = 1\r\n\r\n[mcp_servers.housecarl]\r\ncommand = " + Literal(CodexExe(box)) + "\r\n",
            File.ReadAllText(Toml(box)));
    }

    // ---- the copy of the file ----------------------------------------------

    [Fact]
    public void TheCopyOfTheFirstFileOutlastsReRuns()
    {
        using var box = Box();
        const string json = "{\n  \"numStartups\": 1\n}\n", toml = "model = \"m\"\n";
        SetupInstallerSandbox.WriteFile(ClaudeJson(box), json);
        SetupInstallerSandbox.WriteFile(Toml(box), toml);

        for (int run = 0; run < 3; run++) Install(box, SetupProgram.Target.Both);

        Assert.Equal(json, File.ReadAllText(ClaudeJson(box) + ".houseCARL.bak"));
        Assert.Equal(toml, File.ReadAllText(Toml(box) + ".houseCARL.bak"));
        Assert.Equal([".houseCARL.bak"], Copies(ClaudeJson(box)));
        Assert.Equal([".houseCARL.bak"], Copies(Toml(box)));
    }

    [Fact]
    public void EachChangedFileIsKeptUnderItsOwnName()
    {
        using var box = Box();
        string[] files = ["1", "2", "3"];
        foreach (string n in files)
        {
            SetupInstallerSandbox.WriteFile(Toml(box), "[mcp_servers.housecarl]\ncommand = " + Literal(OldExe) + "\ntool_timeout_sec = " + n + "\n");
            SetupInstallerSandbox.WriteFile(ClaudeJson(box), ClaudeWith(new JsonObject { ["command"] = OldExe, ["timeout"] = n }));
            Install(box, SetupProgram.Target.Both);
            Install(box, SetupProgram.Target.Both);
        }

        string[] names = [".houseCARL.bak", ".houseCARL.2.bak", ".houseCARL.3.bak"];
        Assert.Equal(names.Order(StringComparer.Ordinal), Copies(Toml(box)));
        Assert.Equal(names.Order(StringComparer.Ordinal), Copies(ClaudeJson(box)));
        for (int i = 0; i < 3; i++)
        {
            Assert.Contains("tool_timeout_sec = " + files[i], File.ReadAllText(Toml(box) + names[i]));
            Assert.Equal(ClaudeWith(new JsonObject { ["command"] = OldExe, ["timeout"] = files[i] }), File.ReadAllText(ClaudeJson(box) + names[i]));
        }
    }

    [Fact]
    public void ACopyWithTheSameBytesIsNotTakenAgain()
    {
        using var box = Box();
        const string toml = "[other]\nkey = 1\n";
        SetupInstallerSandbox.WriteFile(Toml(box), toml);
        Install(box, SetupProgram.Target.Codex);
        File.WriteAllText(Toml(box), toml);

        Install(box, SetupProgram.Target.Codex);

        Assert.Equal([".houseCARL.bak"], Copies(Toml(box)));
    }

    [Theory]
    [InlineData(SetupProgram.Target.Claude, "{}")]
    [InlineData(SetupProgram.Target.Codex, "[mcp_servers.\"housecarl\"]\ncommand = \"x\"\n")]
    public void ABrokenStartIsStillInTheCopyAfterTwoRuns(SetupProgram.Target target, string start)
    {
        using var box = Box();
        string path = target == SetupProgram.Target.Claude ? ClaudeJson(box) : Toml(box);
        SetupInstallerSandbox.WriteFile(path, start);

        Install(box, target);
        Install(box, target);

        Assert.Equal(start, File.ReadAllText(path + ".houseCARL.bak"));
        Assert.Single(Copies(path));
    }

    [Fact]
    public void ARetryAfterTheCodexStepFailedKeepsTheClaudeCopy()
    {
        using var box = Box();
        const string json = "{\n  \"numStartups\": 1\n}\n";
        SetupInstallerSandbox.WriteFile(ClaudeJson(box), json);
        Directory.CreateDirectory(Toml(box));
        SetupInstallerSandbox.Capture(() => Assert.ThrowsAny<Exception>(() => box.Install(SetupProgram.Target.Both)));
        Directory.Delete(Toml(box));

        Install(box, SetupProgram.Target.Both);

        Assert.Equal(json, File.ReadAllText(ClaudeJson(box) + ".houseCARL.bak"));
        Assert.Equal([".houseCARL.bak"], Copies(ClaudeJson(box)));
    }

    [Fact]
    public void ASecondUninstallKeepsTheFirstOnesCopy()
    {
        using var box = Box();
        string json = "", toml = "";
        for (int run = 1; run <= 2; run++)
        {
            SetupInstallerSandbox.WriteFile(ClaudeJson(box), ClaudeWith(new JsonObject { ["command"] = OldExe, ["timeout"] = run }));
            SetupInstallerSandbox.WriteFile(Toml(box), "[mcp_servers.housecarl]\ncommand = 'x'\nrun = " + run + "\n");
            if (run == 1) (json, toml) = (File.ReadAllText(ClaudeJson(box)), File.ReadAllText(Toml(box)));
            Uninstall(box, SetupProgram.Target.Both);
        }

        Assert.Equal(json, File.ReadAllText(ClaudeJson(box) + ".houseCARL.uninstall.bak"));
        Assert.Equal(toml, File.ReadAllText(Toml(box) + ".houseCARL.uninstall.bak"));
        Assert.Equal([".houseCARL.uninstall.2.bak", ".houseCARL.uninstall.bak"], Copies(ClaudeJson(box)));
        Assert.Equal([".houseCARL.uninstall.2.bak", ".houseCARL.uninstall.bak"], Copies(Toml(box)));
    }

    // Pins unchanged behaviour: the removal takes the whole entry, the settings a re-run kept with it.
    [Fact]
    public void AnUninstallTakesTheSettingsWithTheEntry()
    {
        using var box = Box();
        const string other = "[mcp_servers.other]\ncommand = \"x\"\n";
        SetupInstallerSandbox.WriteFile(ClaudeJson(box), ClaudeWith(new JsonObject { ["command"] = OldExe, ["timeout"] = 5 }));
        SetupInstallerSandbox.WriteFile(Toml(box), other
            + "\n[mcp_servers.housecarl]\ncommand = " + Literal(OldExe) + "\nenabled = false\n\n[mcp_servers.housecarl.env]\nA = \"1\"\n");
        Install(box, SetupProgram.Target.Both);

        Uninstall(box, SetupProgram.Target.Both);

        Assert.Equal(other, File.ReadAllText(Toml(box)));
        var servers = JsonNode.Parse(File.ReadAllText(ClaudeJson(box)))!["mcpServers"]!.AsObject();
        Assert.Equal(["other"], servers.Select(s => s.Key));
    }

    [Fact]
    public void ABomClaudeJsonKeepsItsBom()
    {
        using var box = Box();
        SetupInstallerSandbox.WriteFile(ClaudeJson(box), Encoding.UTF8.GetBytes("\uFEFF{\n  \"numStartups\": 1\n}\n"));

        Install(box, SetupProgram.Target.Claude);

        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'{' }, File.ReadAllBytes(ClaudeJson(box))[..4]);
    }
}
