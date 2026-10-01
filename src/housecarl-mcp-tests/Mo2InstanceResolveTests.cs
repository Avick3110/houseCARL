using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>Deriving the load-order roots and the active profile from one MO2 instance folder
/// (<see cref="Mo2Instance"/>, from the <c>mo2instance-probe</c> probe) over synthetic instances: the
/// <c>@ByteArray(...)</c> unwrap, the doubled-backslash unescape, a profile name with spaces, the
/// <c>base_directory</c> override, the MO2 2.5.x spaced form, and the named problems.</summary>
[Trait("tier", "integration")]
public sealed class Mo2InstanceResolveTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "hc-mo2instance-resolve-tests-" + Guid.NewGuid().ToString("N"));

    public Mo2InstanceResolveTests() => Directory.CreateDirectory(_root);

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    // E1a profile (with spaces) unwrapped from @ByteArray; E1b ProfileDir derived under the instance;
    // E1c gamePath \\-unescaped → DataDir = gamePath\Data; E1d ModsDir = instance\mods
    // E8a–E8d: the same from MO2 2.5.x's spaced 'key = value' form (regression: issue #128)
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheRootsAndProfileComeFromTheIni(bool spaced)
    {
        var inst = Path.Combine(_root, "e1");
        var game = Path.Combine(inst, "Stock Game");
        const string prof = "My Test Profile";
        MakeInstance(inst, game, prof, baseDirOverride: null, spaced);

        var p = Mo2Instance.Resolve(inst);

        Assert.Equal(prof, p.ProfileName);
        AssertSamePath(Path.Combine(inst, "profiles", prof), p.ProfileDir);
        AssertSamePath(Path.Combine(game, "Data"), p.DataDir);
        AssertSamePath(Path.Combine(inst, "mods"), p.ModsDir);
    }

    // E6 cheap profile detect == selected_profile (on a synthetic instance; the probe read the real one)
    [Fact]
    public void ReadSelectedProfileReturnsTheProfileAlone()
    {
        var inst = Path.Combine(_root, "e6");
        MakeInstance(inst, Path.Combine(inst, "Stock Game"), "Survival Run", baseDirOverride: null);

        Assert.Equal("Survival Run", Mo2Instance.ReadSelectedProfile(inst));
    }

    // E8e Validate → usable, no problems on the spaced form
    [Fact]
    public void ValidateAcceptsTheSpacedFormWithNoProblems()
    {
        var inst = Path.Combine(_root, "e8");
        MakeInstance(inst, Path.Combine(inst, "Stock Game"), "Default", baseDirOverride: null, spaced: true);

        var (ok, paths, problems) = Mo2Instance.Validate(inst);

        Assert.True(ok);
        Assert.NotNull(paths);
        Assert.Empty(problems);
    }

    // E2 unset profile → not ok, paths null, profile-problem surfaced
    [Fact]
    public void AnUnsetProfileIsNotUsableAndNamed()
    {
        var inst = Path.Combine(_root, "e2");
        MakeInstance(inst, Path.Combine(inst, "Stock Game"), profile: null, baseDirOverride: null);

        var (ok, paths, problems) = Mo2Instance.Validate(inst);

        Assert.False(ok);
        Assert.Null(paths);
        Assert.Contains(problems, s => s.Contains("selected_profile"));
    }

    // E3 no ini → not ok, problem names ModOrganizer.ini
    [Fact]
    public void AFolderWithNoIniIsNotUsableAndNamesTheIni()
    {
        var inst = Path.Combine(_root, "e3");
        Directory.CreateDirectory(inst);

        var (ok, _, problems) = Mo2Instance.Validate(inst);

        Assert.False(ok);
        Assert.Contains(problems, s => s.Contains("ModOrganizer.ini"));
    }

    // E4a ProfileDir under base_directory; E4b ModsDir under base_directory; E4c DataDir still under gamePath
    [Fact]
    public void BaseDirectoryMovesModsAndProfilesButNotData()
    {
        var inst = Path.Combine(_root, "e4");
        var baseDir = Path.Combine(_root, "e4base");
        var game = Path.Combine(inst, "Stock Game");
        MakeInstance(inst, game, "Default", baseDirOverride: baseDir);

        var p = Mo2Instance.Resolve(inst);

        AssertSamePath(Path.Combine(baseDir, "profiles", "Default"), p.ProfileDir);
        AssertSamePath(Path.Combine(baseDir, "mods"), p.ModsDir);
        AssertSamePath(Path.Combine(game, "Data"), p.DataDir);
    }

    // E7a still usable — silently falls back to the instance dir; E7b the discarded base_directory is NAMED (Q3)
    [Fact]
    public void AMissingBaseDirectoryFallsBackButIsNamed()
    {
        var inst = Path.Combine(_root, "e7");
        MakeInstance(inst, Path.Combine(inst, "Stock Game"), "Default", baseDirOverride: null);
        var ghost = Path.Combine(_root, "e7-ghost-base");
        File.AppendAllLines(Mo2Instance.IniPath(inst), new[] { "[Settings]", $"base_directory=@ByteArray({Escape(ghost)})" });

        var (ok, paths, problems) = Mo2Instance.Validate(inst);

        Assert.True(ok);
        Assert.NotNull(paths);
        AssertSamePath(Path.Combine(inst, "mods"), paths.ModsDir);
        Assert.Contains(problems, s => s.Contains("base_directory"));
    }

    /// <summary>A synthetic instance in the format MO2 writes: <c>@ByteArray(...)</c>-wrapped values with doubled
    /// backslashes; mods/ and profiles/&lt;profile&gt; under the base override if given, else the instance; a null
    /// profile writes <c>@Invalid()</c>.</summary>
    static void MakeInstance(string instanceDir, string gamePath, string? profile, string? baseDirOverride, bool spaced = false)
    {
        Directory.CreateDirectory(instanceDir);
        Directory.CreateDirectory(Path.Combine(gamePath, "Data"));
        var basePath = baseDirOverride ?? instanceDir;
        Directory.CreateDirectory(Path.Combine(basePath, "mods"));
        if (profile is not null)
        {
            var profDir = Path.Combine(basePath, "profiles", profile);
            Directory.CreateDirectory(profDir);
            File.WriteAllText(Path.Combine(profDir, "loadorder.txt"), "# header\nSkyrim.esm\n");
        }

        var lines = new List<string>
        {
            "[General]",
            Kv("gameName", "Skyrim Special Edition"),
            Kv("selected_profile", profile is null ? "@Invalid()" : $"@ByteArray({profile})"),
            Kv("gamePath", $"@ByteArray({Escape(gamePath)})"),
            Kv("game_edition", "Steam"),
        };
        if (baseDirOverride is not null)
        {
            lines.Add("[Settings]");
            lines.Add(Kv("base_directory", $"@ByteArray({Escape(baseDirOverride)})"));
        }
        File.WriteAllLines(Mo2Instance.IniPath(instanceDir), lines);

        string Kv(string k, string v) => spaced ? $"{k} = {v}" : $"{k}={v}";
    }

    static string Escape(string p) => p.Replace("\\", "\\\\");

    static void AssertSamePath(string expected, string actual) =>
        Assert.Equal(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(expected)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(actual)),
            ignoreCase: true);
}
