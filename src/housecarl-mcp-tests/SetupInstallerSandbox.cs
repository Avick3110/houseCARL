using Xunit;
using SetupProgram = HousecarlSetup.Program;

namespace HousecarlMcpTests;

/// <summary>The installer tests capture the console and clear CODEX_HOME, both process-wide, so they run alone.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SetupInstallerCollection
{
    public const string Name = "setup-installer";
}

/// <summary>A temp package and a temp home for driving the real installer. CODEX_HOME is cleared so config.toml
/// lands under the temp home, and restored on dispose.</summary>
internal sealed class SetupInstallerSandbox : IDisposable
{
    public string Root { get; }
    public string Src { get; }
    public string Home { get; }

    readonly string? savedCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");

    public SetupInstallerSandbox()
    {
        Environment.SetEnvironmentVariable("CODEX_HOME", null);
        Root = Path.Combine(Path.GetTempPath(), "hc-setup-test-" + Guid.NewGuid().ToString("N"));
        Src = Path.Combine(Root, "package", "housecarl");
        Home = Path.Combine(Root, "home");
    }

    /// <summary>A package with a manifest, a server exe, and these skills.</summary>
    public void WritePackage(params string[] skills)
    {
        WriteFile(Path.Combine(Src, ".claude-plugin", "plugin.json"), "{}");
        WriteFile(Path.Combine(Src, "server", "housecarl-mcp.exe"), "exe");
        foreach (string s in skills)
            WriteFile(Path.Combine(Src, "skills", s, "SKILL.md"), s);
    }

    public SetupProgram.InstallResult Install(SetupProgram.Target target)
        => SetupProgram.TryInstall(target, Src, Home, Home);

    /// <summary>The state a v2.0.0-v2.1.2 Codex install left: its umbrella skill under the shared root, and its
    /// name on the record. Call after an install has written the record.</summary>
    public void SeedRecordedUmbrella()
    {
        WriteFile(Path.Combine(SetupProgram.CodexSkillsRoot(Home), "housecarl", "SKILL.md"), "umbrella");
        string record = SetupProgram.CodexSkillRecord(Home, Home);
        File.WriteAllLines(record, File.ReadAllLines(record).Append("housecarl"));
    }

    public static void WriteFile(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    public static void WriteFile(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>Run something with the console captured, and return what it said.</summary>
    public static string Capture(Action run)
    {
        var saved = Console.Out;
        using var buf = new StringWriter();
        Console.SetOut(buf);
        try { run(); }
        finally { Console.SetOut(saved); }
        return buf.ToString();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CODEX_HOME", savedCodexHome);
        try
        {
            if (!Directory.Exists(Root)) return;
            foreach (string f in Directory.GetFiles(Root, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
