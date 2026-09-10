using System.Diagnostics;
using System.Text.Json;

namespace HousecarlSetup;

/// <summary>
/// What is already on this machine, read before setup writes anything: which hosts are here, whether houseCARL
/// is installed for each, and at what version. It is its own file because it is its own job - <see cref="Program"/>
/// stays the install, and the paths it reads come from <see cref="Program"/>'s path helpers so a destination
/// cannot drift between what is reported and what is written.
///
/// Reading only. Nothing here creates a directory or a file, so a run that is cancelled at the plan leaves the
/// machine exactly as it found it.
/// </summary>
public static class Detect
{
    /// <summary>One host as it stands before the install.</summary>
    /// <param name="Name">The host's name, as the menu and the plan say it.</param>
    /// <param name="Present">True when the host itself is on this machine.</param>
    /// <param name="Installed">True when houseCARL is already installed for it.</param>
    /// <param name="InstalledVersion">The installed version, or null when it is installed but unreadable.</param>
    public sealed record HostState(string Name, bool Present, bool Installed, string? InstalledVersion)
    {
        /// <summary>The right-hand side of this host's row in the detection block and the menu.</summary>
        public string Summary =>
            !Present    ? "not found"
            : !Installed ? "found  ·  houseCARL not installed"
            : InstalledVersion is null ? "found  ·  houseCARL installed"
            :                            "found  ·  houseCARL " + InstalledVersion + " installed";
    }

    /// <summary>Claude Code: the desktop app keeps ~/.claude.json and ~/.claude, and either one is it being here.
    /// The installed version is the copied plugin's own manifest, which is the file that shipped it.</summary>
    public static HostState Claude(string home)
    {
        bool present = File.Exists(Program.ClaudeJson(home)) || Directory.Exists(Path.Combine(home, ".claude"));
        string dest  = Program.ClaudeSkillsDest(home);
        bool installed = File.Exists(Program.ClaudeDestExe(home)) || Directory.Exists(dest);
        string? version = installed
            ? PluginVersion(Path.Combine(dest, ".claude-plugin", "plugin.json"))
            : null;
        return new HostState("Claude Code", present, installed, version);
    }

    /// <summary>Codex: ~/.codex (or CODEX_HOME) is it being here. A Codex install copies the server but not the
    /// plugin manifest, so the version comes off the installed server exe, which build-plugin.ps1 stamps.</summary>
    public static HostState Codex(string home, string? homeOverride)
    {
        bool present = Directory.Exists(Program.CodexConfigHome(home));
        string destExe = Program.CodexDestExe(home, homeOverride);
        bool installed = File.Exists(destExe);
        string? version = installed ? ExeVersion(destExe) : null;
        return new HostState("Codex", present, installed, version);
    }

    /// <summary>The "version" of a plugin.json, or null when the file is absent, unreadable or has no version.</summary>
    public static string? PluginVersion(string pluginJsonPath)
    {
        if (!File.Exists(pluginJsonPath)) return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(pluginJsonPath));
            return doc.RootElement.TryGetProperty("version", out JsonElement v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null; // an unreadable manifest is "installed, version unknown", never a stopped run
        }
    }

    /// <summary>The product version stamped into an exe, with any "+sha" metadata trimmed; null when unreadable.</summary>
    private static string? ExeVersion(string exePath)
    {
        try
        {
            string? v = FileVersionInfo.GetVersionInfo(exePath).ProductVersion;
            if (string.IsNullOrWhiteSpace(v)) return null;
            int plus = v.IndexOf('+');
            return plus > 0 ? v[..plus] : v;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
