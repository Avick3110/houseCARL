using System.Reflection;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The status header names the running server's build. Verifying that an installed houseCARL is the build
/// you think it is used to mean reading ProductVersion off housecarl-mcp.exe out of band; the line puts it in band.</summary>
[Trait("tier", "unit")]
public class ServerBuildLineTests
{
    static LoadOrderStatusData Empty() => new(
        new Mo2Composition(
            Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase), Array.Empty<string>(), Array.Empty<string>()),
        Array.Empty<string>(), 0, 0, false, "profiles/Default", "Default", null,
        new Dictionary<string, string>(), null);

    static string Render(string? lookup = null) => StatusWire.Render(
        Empty(), Array.Empty<LogFolderView>(),
        new NamedProfileResult(false, Array.Empty<string>(), null, null, null, Array.Empty<string>()),
        lookup, cap: 80_000);

    [Fact]
    public void TheStatusHeaderNamesTheBuildTheServerAssemblyEmbeds()
    {
        var stamped = ToolSurface.Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        Assert.False(string.IsNullOrWhiteSpace(stamped),
            "the built housecarl-mcp assembly carries no informational version, so there is nothing for the status line to report");

        Assert.Contains("server:   " + stamped, Render());
        // The lookup answer returns early from its own branch, so it needs its own arm: both paths name the build.
        Assert.Contains("server:   " + stamped, Render("Requiem.esp"));
    }

    /// <summary>An unconfigured server — a staged install nobody pointed at an MO2 instance yet — answers with the
    /// setup prompt and nothing else. That is the case the build line exists for, so it renders ahead of it.</summary>
    [Fact]
    public void TheUnconfiguredAnswerStillNamesTheBuild()
    {
        var dir = Path.Combine(Path.GetTempPath(), "housecarl-serverline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new UserConfigStore(Path.Combine(dir, "user.json"));
            var text = StatusTools.LoadOrderStatus(
                LoadOrderService.WithInstance(null, 0, store), new ToolPathResolver(store));

            Assert.StartsWith("server:   " + ServerBuild.Line, text);
            Assert.Contains("no Mod Organizer 2 instance configured", text);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
