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

    static string Render() => StatusWire.Render(
        Empty(), Array.Empty<LogFolderView>(),
        new NamedProfileResult(false, Array.Empty<string>(), null, null, null, Array.Empty<string>()),
        lookup: null, cap: 80_000);

    [Fact]
    public void TheStatusHeaderNamesTheBuildTheServerAssemblyEmbeds()
    {
        var stamped = ToolSurface.Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        Assert.False(string.IsNullOrWhiteSpace(stamped),
            "the built housecarl-mcp assembly carries no informational version, so there is nothing for the status line to report");

        Assert.Equal(stamped, ServerBuild.Version);
        Assert.Contains("server:   " + stamped, Render());
    }
}
