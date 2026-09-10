using System.Text.Json.Nodes;
using SetupProgram = HousecarlSetup.Program;
using SetupUninstall = HousecarlSetup.Uninstall;

namespace HousecarlGenerator;

/// <summary>
/// Setup uninstall guard (installer PR C). Until this there was no way to remove houseCARL other than deleting
/// folders by hand and editing ~/.claude.json and ~/.codex/config.toml, and a hand edit of either is where a
/// user loses the rest of the file. The removal is two splices beside the install's insert splices, so the
/// property that matters is not "the entry is gone" but "the entry is gone AND every other byte is where it
/// was". This drives the REAL <see cref="HousecarlSetup.Uninstall.TryUninstall"/> over a synthetic package, a
/// temp home, and two host configs that already carry somebody else's MCP server:
///
///   T1  install for Both over the two pre-existing configs, then uninstall Both.
///   T2  ~/.claude.json is byte-identical to before the install, and ~/.codex/config.toml likewise: the
///       other server, the unrelated root key, the comment and the newline style all survive the round trip.
///   T3  no houseCARL file is left: the Claude tree, the Codex server dir (the saved MO2 instance with it),
///       the skill record and the houseCARL data dir are all gone.
///   T4  under the SHARED ~/.agents/skills only the folders the record named went — a skill houseCARL never
///       installed is untouched.
///   T5  both configs were copied to a .houseCARL.bak before being edited.
///   T6  an install too old to have left a Claude skill record still has its tree removed, and the run says
///       that is the route it took.
///   T7  with no Codex record, nothing under the shared skills root is removed, and the run says so.
///   T8  a running session's server exe is pre-flighted: the uninstall refuses before deleting anything, and
///       the installed tree is still there.
///
/// Self-contained: synthetic files in temp, no game data / no MO2 / no real host config. RED without the
/// removal splices: T2 fails on both files (a whole-file rewrite reformats them, a naive line delete leaves the
/// blank line the insert added), T3 leaves the tree, T4 takes the foreign skill with a directory diff, and T8
/// deletes into a live install.
///
/// Run: dotnet run --project src/housecarl-generator setup-uninstall
/// </summary>
internal static class SetupUninstallProbe
{
    [CiProbe("setup-uninstall")]
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("================================================================");
        Console.WriteLine(" setup uninstall guard — the removal takes back exactly what the install wrote");
        Console.WriteLine("================================================================");
        Console.WriteLine();
        int fail = 0;
        void Check(bool c, string label) { Console.WriteLine((c ? "  PASS  " : "  FAIL  ") + label); if (!c) fail++; }

        // Keep the Codex destination hermetic (the installer honours CODEX_HOME for config.toml).
        Environment.SetEnvironmentVariable("CODEX_HOME", null);

        string root = Path.Combine(Path.GetTempPath(), "hc-setup-uninstall-" + Guid.NewGuid().ToString("N"));
        string pkg  = Path.Combine(root, "package");
        string src  = Path.Combine(pkg, "housecarl");
        string home = Path.Combine(root, "home");

        // Asked of the installer's own path helpers, never rebuilt here.
        string claudeDest   = SetupProgram.ClaudeSkillsDest(home);
        string claudeJson   = SetupProgram.ClaudeJson(home);
        string claudeRecord = SetupProgram.ClaudeSkillRecord(home);
        string codexServer  = SetupProgram.CodexServerDir(home, home);
        string codexSkills  = SetupProgram.CodexSkillsRoot(home);
        string codexRecord  = SetupProgram.CodexSkillRecord(home, home);
        string codexToml    = SetupProgram.CodexConfigToml(home);
        string codexDataDir = Path.GetDirectoryName(codexServer)!;

        var (codexPkgPath, _) = SetupUpdateLockProbe.CodexPackagePath();
        string umbrellaPkgDir = Path.Combine(new[] { pkg }
            .Concat((codexPkgPath ?? "codex/skills").Split('/'))
            .Append("housecarl").ToArray());

        try
        {
            // ---- a synthetic package ----
            WriteFile(Path.Combine(src, ".claude-plugin", "plugin.json"), "{}");
            WriteFile(Path.Combine(src, "server", "housecarl-mcp.exe"), "exe");
            foreach (string s in new[] { "skill-one", "skill-two" })
                WriteFile(Path.Combine(src, "skills", s, "SKILL.md"), s);
            WriteFile(Path.Combine(umbrellaPkgDir, "SKILL.md"), "umbrella");

            // ---- two host configs that already carry somebody else's server ----
            WriteFile(claudeJson, ExistingClaudeJson());
            WriteFile(codexToml, "# my own config\n[other]\nkey = 1\n");
            string claudeBefore = File.ReadAllText(claudeJson);
            string tomlBefore   = File.ReadAllText(codexToml);

            // A skill some other tool put in the shared ~/.agents/skills.
            WriteFile(Path.Combine(codexSkills, "someone-elses-skill", "SKILL.md"), "not ours");

            Console.WriteLine("--- T1: install for Both, then uninstall Both ---");
            var installed = SetupProgram.TryInstall(SetupProgram.Target.Both, src, home, home);
            Check(installed.Outcome == SetupProgram.InstallOutcome.Installed, "the install ran");
            Check(File.Exists(claudeRecord), "the Claude install recorded which skills it wrote");
            Check(File.ReadAllText(claudeJson).Contains("housecarl"), "the Claude config carries the entry");
            Check(File.ReadAllText(codexToml).Contains("[mcp_servers.housecarl]"), "the Codex config carries the table");
            // The saved MO2 instance the server writes beside itself — the removal takes it with the server dir.
            WriteFile(Path.Combine(codexServer, "houseCARL.user.json"), "{\"Mo2InstanceDir\":\"D:\\\\MO2\"}");
            WriteFile(Path.Combine(claudeDest, "server", "houseCARL.user.json"), "{\"Mo2InstanceDir\":\"D:\\\\MO2\"}");

            SetupUninstall.Result removed = null!;
            string said = Capture(() => removed = SetupUninstall.TryUninstall(SetupProgram.Target.Both, home, home));
            Check(removed.What == SetupUninstall.Outcome.Removed, "the uninstall ran");

            Console.WriteLine();
            Console.WriteLine("--- T2: both host configs are byte-identical to before the install ---");
            Check(File.ReadAllText(claudeJson) == claudeBefore,
                  "~/.claude.json is back to its original bytes, entry and all else");
            Check(File.ReadAllText(codexToml) == tomlBefore,
                  "~/.codex/config.toml is back to its original bytes");
            Check(File.ReadAllText(claudeJson).Contains("\"other\""), "the other server is still registered");
            Check(File.ReadAllText(codexToml).Contains("[other]"), "the other Codex table is still there");

            Console.WriteLine();
            Console.WriteLine("--- T3: no houseCARL file is left on the machine ---");
            Check(!Directory.Exists(claudeDest), "the Claude tree is gone (skills, server and the saved MO2 instance)");
            Check(!Directory.Exists(codexServer), "the Codex server dir is gone (the saved MO2 instance with it)");
            Check(!File.Exists(codexRecord), "the Codex skill record is gone");
            Check(!Directory.Exists(codexDataDir), "the emptied houseCARL data dir is gone");

            Console.WriteLine();
            Console.WriteLine("--- T4: under the shared skills root, only the recorded folders went ---");
            Check(!Directory.Exists(Path.Combine(codexSkills, "skill-one")), "skill-one is gone");
            Check(!Directory.Exists(Path.Combine(codexSkills, "skill-two")), "skill-two is gone");
            Check(!Directory.Exists(Path.Combine(codexSkills, "housecarl")), "the umbrella skill is gone");
            Check(Directory.Exists(Path.Combine(codexSkills, "someone-elses-skill")),
                  "a skill houseCARL never installed is untouched");

            Console.WriteLine();
            Console.WriteLine("--- T5: each config was backed up before it was edited ---");
            Check(File.Exists(claudeJson + ".houseCARL.bak"), "~/.claude.json.houseCARL.bak exists");
            Check(File.Exists(codexToml + ".houseCARL.bak"), "config.toml.houseCARL.bak exists");
            Check(File.ReadAllText(claudeJson + ".houseCARL.bak").Contains("housecarl"),
                  "the backup holds the file as it was, entry included");

            // ---- T6: an install with no Claude record ----
            Console.WriteLine();
            Console.WriteLine("--- T6: an install too old to have left a record still has its tree removed ---");
            SetupProgram.TryInstall(SetupProgram.Target.Claude, src, home, home);
            File.Delete(claudeRecord); // as an install from before the record existed leaves it
            string oldSaid = Capture(() => SetupUninstall.TryUninstall(SetupProgram.Target.Claude, home, home));
            Check(!Directory.Exists(claudeDest), "the tree it owns is removed anyway");
            Check(oldSaid.Contains("before setup began recording"), "and the run says that is the route it took");

            // ---- T7: no Codex record ----
            Console.WriteLine();
            Console.WriteLine("--- T7: with no Codex record, nothing under the shared skills root is removed ---");
            SetupProgram.TryInstall(SetupProgram.Target.Codex, src, home, home);
            File.Delete(codexRecord);
            string noRecordSaid = Capture(() => SetupUninstall.TryUninstall(SetupProgram.Target.Codex, home, home));
            Check(Directory.Exists(Path.Combine(codexSkills, "skill-one")),
                  "the skills stay where they are rather than being guessed at by a directory diff");
            Check(Directory.Exists(Path.Combine(codexSkills, "someone-elses-skill")), "so does the foreign skill");
            Check(noRecordSaid.Contains("no record of which skills"), "and the run says why it removed none");
            Check(!Directory.Exists(codexServer), "the server dir, which it does own, is still removed");
            foreach (string d in new[] { "skill-one", "skill-two", "housecarl" })
                if (Directory.Exists(Path.Combine(codexSkills, d))) Directory.Delete(Path.Combine(codexSkills, d), true);

            // ---- T8: a live session's server exe ----
            Console.WriteLine();
            Console.WriteLine("--- T8: a server file in use refuses the removal before anything is deleted ---");
            SetupProgram.TryInstall(SetupProgram.Target.Claude, src, home, home);
            string liveExe = SetupProgram.ClaudeDestExe(home);
            SetupUninstall.Result held;
            using (new FileStream(liveExe, FileMode.Open, FileAccess.Read, FileShare.Read))
                held = SetupUninstall.TryUninstall(SetupProgram.Target.Claude, home, home);
            Check(held.What == SetupUninstall.Outcome.ServerInUse, "the removal refuses");
            Check(held.RefusedBeforeAnyDelete, "at the pre-flight, before any delete");
            Check(Directory.Exists(claudeDest), "the installed tree is still there");
            Check(File.ReadAllText(claudeJson).Contains("housecarl"), "and so is the registration");
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* non-fatal */ } }

        Console.WriteLine();
        Console.WriteLine(fail == 0
            ? "================ ALL PASS ================"
            : $"================ {fail} CHECK(S) FAILED ================");
        return fail == 0 ? 0 : 1;
    }

    /// <summary>A ~/.claude.json that already holds an unrelated root key and somebody else's MCP server, in the
    /// indentation System.Text.Json writes — which is the indentation both splices reproduce, so a difference
    /// after the round trip is the splice's, not the fixture's.</summary>
    private static string ExistingClaudeJson()
    {
        JsonObject root = new()
        {
            ["numStartups"] = 7,
            ["mcpServers"] = new JsonObject
            {
                ["other"] = new JsonObject
                {
                    ["type"]    = "stdio",
                    ["command"] = @"C:\tools\other.exe",
                },
            },
        };
        return root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    private static void WriteFile(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    /// <summary>Run something with the console captured, and return what it said.</summary>
    private static string Capture(Action run)
    {
        var saved = Console.Out;
        using var buf = new StringWriter();
        Console.SetOut(buf);
        try { run(); }
        finally { Console.SetOut(saved); }
        return buf.ToString();
    }
}
