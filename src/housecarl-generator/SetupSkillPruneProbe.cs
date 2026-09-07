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
///   T5  a leftover folder holding a READ-ONLY file cannot be deleted: the install still finishes and still
///       registers the MCP server for both hosts, the run says which folder is still there, and the Codex
///       record keeps it so the next upgrade tries again (PR #620 review, Program.cs:388).
///   T6  a package with NO skills folder prunes NOTHING: absent is a broken package, not "this version ships
///       no skills", so the installed skills and the record survive it (Program.cs:488).
///   T7  the Codex umbrella is on the record, and goes when a later package stops shipping it (Program.cs:437).
///
/// Self-contained: synthetic files in temp, no game data / no MO2 / no real host config. RED before the fix:
/// T2/T3's "gone" checks fail (the folder survives the merge-copy) and T4 finds nothing said; before the
/// review fold, T5's install throws instead of registering, T6 deletes every installed skill, and T7's
/// umbrella survives unrecorded.
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

            string said = Capture(() => SetupProgram.TryInstall(SetupProgram.Target.Both, src, home, home));

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

            // ---- T5: a leftover the prune cannot delete ----
            Console.WriteLine();
            Console.WriteLine("--- T5: a read-only leftover cannot be deleted, and the install finishes anyway ---");
            string recordPath = Path.Combine(home, "houseCARL", "installed-skills.txt");
            Check(File.Exists(recordPath), "the Codex install recorded what it put in the shared dir");
            Check(File.ReadAllLines(recordPath).Contains("housecarl"), "the umbrella folder is on the record");

            // A stale folder at each location whose file is read-only: Directory.Delete throws
            // UnauthorizedAccessException on it, which is not the sharing violation TryInstall catches.
            foreach (string skillRoot in new[] { claudeSkills, codexSkills })
                MakeReadOnlyFolder(Path.Combine(skillRoot, "stuck-skill"));
            File.WriteAllLines(recordPath, File.ReadAllLines(recordPath).Append("stuck-skill"));

            // Delete both host configs first, so their reappearance proves the registration ran AFTER the
            // failed prune rather than surviving from the earlier install.
            string claudeJson = Path.Combine(home, ".claude.json");
            string codexToml  = Path.Combine(home, ".codex", "config.toml");
            File.Delete(claudeJson);
            File.Delete(codexToml);

            SetupProgram.InstallResult stuck = null!;
            string stuckSaid = Capture(() => stuck = SetupProgram.TryInstall(SetupProgram.Target.Both, src, home, home));

            Check(stuck.Outcome == SetupProgram.InstallOutcome.Installed, "the install finishes, not a failure");
            Check(File.Exists(claudeJson) && File.ReadAllText(claudeJson).Contains("housecarl"),
                  "the Claude MCP registration still ran");
            Check(File.Exists(codexToml) && File.ReadAllText(codexToml).Contains("housecarl"),
                  "the Codex MCP registration still ran");
            Check(Directory.Exists(Path.Combine(claudeSkills, "stuck-skill")), "the folder it could not delete is still there");
            Check(stuckSaid.Contains("could not be deleted"), "the run says a folder could not be deleted");
            Check(stuckSaid.Contains(Path.Combine(claudeSkills, "stuck-skill")), "and names its full path");
            Check(File.ReadAllLines(recordPath).Contains("stuck-skill"),
                  "the Codex record keeps the folder it could not take back, so the next upgrade tries again");
            Check(Directory.Exists(Path.Combine(claudeSkills, "kept-one")), "the shipped skills are still installed");

            foreach (string skillRoot in new[] { claudeSkills, codexSkills })
                DeleteReadOnlyFolder(Path.Combine(skillRoot, "stuck-skill"));
            File.WriteAllLines(recordPath, File.ReadAllLines(recordPath).Where(l => l != "stuck-skill"));

            // ---- T6: a package with no skills folder ----
            Console.WriteLine();
            Console.WriteLine("--- T6: a package that ships no skills folder prunes nothing ---");
            string skillsSrc = Path.Combine(src, "skills");
            string skillsAway = Path.Combine(src, "skills-away");
            Directory.Move(skillsSrc, skillsAway);

            string noSkillsSaid = Capture(() => SetupProgram.TryInstall(SetupProgram.Target.Both, src, home, home));

            Check(Directory.Exists(Path.Combine(claudeSkills, "kept-one")) && Directory.Exists(Path.Combine(claudeSkills, "kept-two")),
                  "the Claude skills survive a package with no skills folder");
            Check(Directory.Exists(Path.Combine(codexSkills, "kept-one")) && Directory.Exists(Path.Combine(codexSkills, "kept-two")),
                  "the Codex skills survive it too");
            Check(Directory.Exists(Path.Combine(codexSkills, "housecarl")), "so does the umbrella");
            Check(File.ReadAllLines(recordPath).Contains("kept-one"), "the record is not overwritten empty");
            Check(noSkillsSaid.Contains("no skills folder"), "the run says why it pruned nothing");
            Directory.Move(skillsAway, skillsSrc);

            // ---- T7: the umbrella is taken back when the package stops shipping it ----
            Console.WriteLine();
            Console.WriteLine("--- T7: a package that stops shipping the umbrella takes the installed one back ---");
            Directory.Delete(Path.Combine(pkg, "codex", "housecarl"), recursive: true);

            string noUmbrellaSaid = Capture(() => SetupProgram.TryInstall(SetupProgram.Target.Both, src, home, home));

            Check(!Directory.Exists(Path.Combine(codexSkills, "housecarl")), "the umbrella folder is gone from ~/.agents/skills");
            Check(noUmbrellaSaid.Split('\n').Any(l => l.Trim() == "- housecarl"), "the removed umbrella is named in the output");
            Check(Directory.Exists(Path.Combine(codexSkills, "kept-one")), "the shipped skills are untouched");
            Check(Directory.Exists(Path.Combine(codexSkills, "someone-elses-skill")), "so is the foreign skill");
            Check(!File.ReadAllLines(recordPath).Contains("housecarl"), "and it is off the record");
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

    /// <summary>Run an install with the console captured, and return what it said.</summary>
    private static string Capture(Action run)
    {
        var saved = Console.Out;
        using var buf = new StringWriter();
        Console.SetOut(buf);
        try { run(); }
        finally { Console.SetOut(saved); }
        return buf.ToString();
    }

    /// <summary>A skill folder whose file is read-only — Directory.Delete throws UnauthorizedAccessException.</summary>
    private static void MakeReadOnlyFolder(string dir)
    {
        string file = Path.Combine(dir, "SKILL.md");
        WriteFile(file, "left behind");
        File.SetAttributes(file, FileAttributes.ReadOnly);
    }

    private static void DeleteReadOnlyFolder(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(dir, recursive: true);
    }
}
