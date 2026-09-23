using Xunit;
using SetupProgram = HousecarlSetup.Program;

namespace HousecarlMcpTests;

/// <summary>Migrated from the setup-update-lock-guard probe: re-running setup over a live install refuses before
/// any copy, and a held file the pre-flight cannot see is caught mid-copy.</summary>
[Collection(SetupInstallerCollection.Name)]
[Trait("tier", "unit")]
public sealed class SetupUpdateLockTests
{
    static readonly byte[] ExeV1 = { 1, 1, 1, 1 };
    static readonly byte[] ExeV2 = { 2, 2, 2, 2 };

    /// <summary>Open a file the way a running image holds it: share-read, write denied.</summary>
    static FileStream HoldLikeRunning(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read);

    static void WriteLockPackage(SetupInstallerSandbox box)
    {
        SetupInstallerSandbox.WriteFile(Path.Combine(box.Src, ".claude-plugin", "plugin.json"), "{}");
        SetupInstallerSandbox.WriteFile(Path.Combine(box.Src, "server", "housecarl-mcp.exe"), ExeV1);
        SetupInstallerSandbox.WriteFile(Path.Combine(box.Src, "server", "Mutagen.Bethesda.dll"), new byte[] { 9 });
        SetupInstallerSandbox.WriteFile(Path.Combine(box.Src, "skills", "demo-skill", "SKILL.md"), "demo");
        SetupInstallerSandbox.WriteFile(Path.Combine(box.UmbrellaPackageDir, "SKILL.md"), "umbrella");
    }

    // T1: a clean first install succeeds, and the umbrella packed where the packager puts it lands at ~/.agents/skills/housecarl.
    [Fact]
    public void ACleanInstallLandsTheUmbrellaWhereThePackagerPutsIt()
    {
        var (packaged, why) = SetupInstallerSandbox.CodexPackagePath();
        Assert.True(packaged is not null, why);

        using var box = new SetupInstallerSandbox();
        WriteLockPackage(box);

        var clean = box.Install(SetupProgram.Target.Both);

        Assert.Equal(SetupProgram.InstallOutcome.Installed, clean.Outcome);
        Assert.True(File.Exists(SetupProgram.ClaudeDestExe(box.Home)));
        Assert.True(File.Exists(SetupProgram.CodexDestExe(box.Home, box.Home)));
        Assert.True(File.Exists(Path.Combine(SetupProgram.CodexSkillsRoot(box.Home), "housecarl", "SKILL.md")));
    }

    // T2: with the Claude server exe held, the re-install refuses at pre-flight: no copy ran and the exe is byte-intact.
    [Fact]
    public void AHeldClaudeExeIsRefusedBeforeAnyCopy()
    {
        using var box = new SetupInstallerSandbox();
        WriteLockPackage(box);
        box.Install(SetupProgram.Target.Both);
        string claudeExe = SetupProgram.ClaudeDestExe(box.Home);
        string sentinel = Path.Combine(SetupProgram.ClaudeSkillsDest(box.Home), "skills", "demo-skill", "SKILL.md");
        SetupInstallerSandbox.WriteFile(Path.Combine(box.Src, "server", "housecarl-mcp.exe"), ExeV2);
        File.Delete(sentinel);

        SetupProgram.InstallResult locked;
        using (HoldLikeRunning(claudeExe))
            locked = box.Install(SetupProgram.Target.Claude);

        Assert.Equal(SetupProgram.InstallOutcome.ServerInUse, locked.Outcome);
        Assert.True(locked.RefusedBeforeAnyCopy);
        Assert.False(File.Exists(sentinel));
        Assert.Equal(ExeV1, File.ReadAllBytes(claudeExe));
    }

    // T3: the same pre-flight refusal at the Codex destination.
    [Fact]
    public void AHeldCodexExeIsRefusedBeforeAnyCopy()
    {
        using var box = new SetupInstallerSandbox();
        WriteLockPackage(box);
        box.Install(SetupProgram.Target.Both);

        SetupProgram.InstallResult locked;
        using (HoldLikeRunning(SetupProgram.CodexDestExe(box.Home, box.Home)))
            locked = box.Install(SetupProgram.Target.Codex);

        Assert.Equal(SetupProgram.InstallOutcome.ServerInUse, locked.Outcome);
        Assert.True(locked.RefusedBeforeAnyCopy);
    }

    // T4: a held sibling DLL (exe free) is caught mid-copy as ServerInUse, not thrown, and not reported as pre-flight.
    [Fact]
    public void AHeldDllIsReportedMidCopyAsServerInUse()
    {
        using var box = new SetupInstallerSandbox();
        WriteLockPackage(box);
        box.Install(SetupProgram.Target.Both);
        string dll = Path.Combine(Path.GetDirectoryName(SetupProgram.ClaudeDestExe(box.Home))!, "Mutagen.Bethesda.dll");

        SetupProgram.InstallResult locked;
        using (HoldLikeRunning(dll))
            locked = box.Install(SetupProgram.Target.Claude);

        Assert.Equal(SetupProgram.InstallOutcome.ServerInUse, locked.Outcome);
        Assert.False(locked.RefusedBeforeAnyCopy);
    }
}
