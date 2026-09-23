using Xunit;
using System.Text.RegularExpressions;
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
    public string Package { get; }
    public string Src { get; }
    public string Home { get; }
    public string UmbrellaPackageDir { get; }

    readonly string? savedCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");

    public SetupInstallerSandbox()
    {
        Environment.SetEnvironmentVariable("CODEX_HOME", null);
        Root = Path.Combine(Path.GetTempPath(), "hc-setup-test-" + Guid.NewGuid().ToString("N"));
        Package = Path.Combine(Root, "package");
        Src = Path.Combine(Package, "housecarl");
        Home = Path.Combine(Root, "home");
        var (packaged, why) = CodexPackagePath();
        if (packaged is null) throw new InvalidOperationException("Codex umbrella package path: " + why);
        UmbrellaPackageDir = Path.Combine(new[] { Package }.Concat(packaged.Split('/')).Append("housecarl").ToArray());
    }

    /// <summary>A package with a manifest, a server exe, these skills, and the Codex umbrella.</summary>
    public void WritePackage(params string[] skills)
    {
        WriteFile(Path.Combine(Src, ".claude-plugin", "plugin.json"), "{}");
        WriteFile(Path.Combine(Src, "server", "housecarl-mcp.exe"), "exe");
        foreach (string s in skills)
            WriteFile(Path.Combine(Src, "skills", s, "SKILL.md"), s);
        WriteFile(Path.Combine(UmbrellaPackageDir, "SKILL.md"), "umbrella");
    }

    public SetupProgram.InstallResult Install(SetupProgram.Target target)
        => SetupProgram.TryInstall(target, Src, Home, Home);

    /// <summary>Where the packager puts the Codex skills root, relative to the package root, read out of
    /// scripts/build-plugin.ps1 so the fixture ships the umbrella where the real package does.</summary>
    public static (string? Path, string Why) CodexPackagePath()
    {
        string script = Path.Combine(HarnessPaths.RepoRoot, "scripts", "build-plugin.ps1");
        if (!File.Exists(script)) return (null, "no " + script);
        string text = File.ReadAllText(script);
        var root = Regex.Match(text, @"^\s*\$CodexRoot\s*=\s*Join-Path\s+\$PkgRoot\s+'([^']+)'", RegexOptions.Multiline);
        var skills = Regex.Match(text, @"^\s*\$CodexSkills\s*=\s*Join-Path\s+\$CodexRoot\s+'([^']+)'", RegexOptions.Multiline);
        if (!root.Success) return (null, "no '$CodexRoot = Join-Path $PkgRoot ...' assignment found");
        if (!skills.Success) return (null, "no '$CodexSkills = Join-Path $CodexRoot ...' assignment found");
        string path = $"{root.Groups[1].Value}/{skills.Groups[1].Value}";
        return (path, path);
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
