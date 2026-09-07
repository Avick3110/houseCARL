using SetupProgram = HousecarlSetup.Program; // alias: the generator's own top-level Program shadows it otherwise

namespace HousecarlGenerator;

/// <summary>
/// Setup stale-skill prune guard (PR #620 review). houseCARL-Setup upgrades by merge-copy
/// (CopyDirectory = CreateDirectory + File.Copy overwrite:true, no delete), so a skill dropped from the
/// package used to survive an upgrade at both install locations and keep loading. The fix removes, after the
/// copy, the skill folders the previous install left behind — by directory diff at the Claude location
/// (houseCARL owns that skills root outright) and by the recorded install list at the Codex location
/// (~/.agents/skills is shared with every other agent's skills). This drives the REAL
/// <see cref="HousecarlSetup.Program.TryInstall"/> over a synthetic package + a temp home:
///
///   T1  install a package shipping three skills for Both, then re-install a package shipping only two.
///   T2  Claude location: the dropped skill's folder is gone, the two shipped ones are still there.
///   T3  Codex location: the dropped skill's folder is gone, the two shipped ones and the umbrella remain,
///       and a foreign skill folder houseCARL never installed is UNTOUCHED (the shared dir is not diffed).
///   T4  the run says which folders it removed, at both locations.
///
/// Self-contained: synthetic files in temp, no game data / no MO2 / no real host config. RED before the fix:
/// T2/T3's "gone" checks fail (the folder survives the merge-copy) and T4 finds nothing said.
///
/// Run: dotnet run --project src/housecarl-generator setup-skill-prune
/// </summary>
internal static class SetupSkillPruneProbe
{
    [CiProbe("setup-skill-prune")]
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("================================================================");
        Console.WriteLine(" setup skill-prune guard — an upgrade takes back the skills it dropped");
        Console.WriteLine("================================================================");
        Console.WriteLine();
        int fail = 0;
        void Check(bool c, string label) { Console.WriteLine((c ? "  PASS  " : "  FAIL  ") + label); if (!c) fail++; }

        // Keep the Codex destination hermetic (TryInstall honours CODEX_HOME for config.toml).
        Environment.SetEnvironmentVariable("CODEX_HOME", null);

        string root = Path.Combine(Path.GetTempPath(), "hc-setup-prune-" + Guid.NewGuid().ToString("N"));
        string pkg  = Path.Combine(root, "package");
        string src  = Path.Combine(pkg, "housecarl");
        string home = Path.Combine(root, "home");

        string claudeSkills = Path.Combine(home, ".claude", "skills", "housecarl", "skills");
        string codexSkills  = Path.Combine(home, ".agents", "skills");

        try
        {
            // ---- a synthetic package shipping three skills ----
            WriteFile(Path.Combine(src, ".claude-plugin", "plugin.json"), "{}");
            WriteFile(Path.Combine(src, "server", "housecarl-mcp.exe"), "exe");
            foreach (string s in new[] { "kept-one", "kept-two", "dropped-skill" })
                WriteFile(Path.Combine(src, "skills", s, "SKILL.md"), s);
            WriteFile(Path.Combine(pkg, "codex", "housecarl", "SKILL.md"), "umbrella");

            Console.WriteLine("--- T1: install the three-skill package, then re-install one that ships only two ---");
            var first = SetupProgram.TryInstall(SetupProgram.Target.Both, src, home, home);
            Check(first.Outcome == SetupProgram.InstallOutcome.Installed, "first install => Installed");
            Check(Directory.Exists(Path.Combine(claudeSkills, "dropped-skill")), "the dropped-to-be skill is installed for Claude");
            Check(Directory.Exists(Path.Combine(codexSkills, "dropped-skill")),  "the dropped-to-be skill is installed for Codex");

            // A skill some other tool put in the shared ~/.agents/skills — houseCARL never installed it.
            WriteFile(Path.Combine(codexSkills, "someone-elses-skill", "SKILL.md"), "not ours");

            Directory.Delete(Path.Combine(src, "skills", "dropped-skill"), recursive: true); // the next version drops it

            string said;
            var saved = Console.Out;
            using (var buf = new StringWriter())
            {
                Console.SetOut(buf);
                try { SetupProgram.TryInstall(SetupProgram.Target.Both, src, home, home); }
                finally { Console.SetOut(saved); }
                said = buf.ToString();
            }

            Console.WriteLine();
            Console.WriteLine("--- T2: the Claude skills root (houseCARL's outright) loses only the dropped skill ---");
            Check(!Directory.Exists(Path.Combine(claudeSkills, "dropped-skill")), "dropped-skill is gone from ~/.claude/skills/housecarl/skills");
            Check(Directory.Exists(Path.Combine(claudeSkills, "kept-one")), "kept-one survives");
            Check(Directory.Exists(Path.Combine(claudeSkills, "kept-two")), "kept-two survives");

            Console.WriteLine();
            Console.WriteLine("--- T3: the shared Codex skills dir loses the dropped skill and NOTHING else ---");
            Check(!Directory.Exists(Path.Combine(codexSkills, "dropped-skill")), "dropped-skill is gone from ~/.agents/skills");
            Check(Directory.Exists(Path.Combine(codexSkills, "kept-one")), "kept-one survives");
            Check(Directory.Exists(Path.Combine(codexSkills, "kept-two")), "kept-two survives");
            Check(Directory.Exists(Path.Combine(codexSkills, "housecarl")), "the umbrella skill survives");
            Check(Directory.Exists(Path.Combine(codexSkills, "someone-elses-skill")),
                  "a skill houseCARL never installed is untouched (the shared dir is not directory-diffed)");

            Console.WriteLine();
            Console.WriteLine("--- T4: the run says what it removed, at both locations ---");
            Check(said.Contains("dropped-skill"), "the removed skill is named in the output");
            Check(said.Contains("[Claude Code] removed skills this version no longer ships"), "said so for the Claude location");
            Check(said.Contains("[Codex] removed skills this version no longer ships"), "said so for the Codex location");
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* non-fatal */ } }

        Console.WriteLine();
        Console.WriteLine(fail == 0
            ? "================ ALL PASS ================"
            : $"================ {fail} CHECK(S) FAILED ================");
        return fail == 0 ? 0 : 1;
    }

    private static void WriteFile(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
}
