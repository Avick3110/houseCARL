using System.Text.Json.Nodes;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The built server needs the base .NET runtime and nothing else. Setup does not check for a runtime of its own:
/// it shares the server's target framework, so setup starting proves the runtime (unless roll-forward
/// (DOTNET_ROLL_FORWARD) moves setup to a later major), and that holds only while the server asks for no second
/// shared framework (the ASP.NET Core one it dropped with .NET 10).
/// </summary>
[Trait("tier", "unit")]
public sealed class ServerRuntimeConfigTests
{
    [Fact]
    public void Server_needs_only_the_base_runtime()
    {
        var path = Path.Combine(HarnessPaths.RepoRoot, "src", "housecarl-mcp", "bin",
                                HarnessPaths.Configuration, HarnessPaths.TargetFramework,
                                "housecarl-mcp.runtimeconfig.json");
        var options = JsonNode.Parse(File.ReadAllText(path))!["runtimeOptions"]!;

        // One framework is written as "framework"; more than one as a "frameworks" array.
        var names = options["frameworks"] is JsonArray many
            ? many.Select(f => (string?)f!["name"]).ToList()
            : [(string?)options["framework"]?["name"]];

        Assert.Equal(["Microsoft.NETCore.App"], names);
    }
}
