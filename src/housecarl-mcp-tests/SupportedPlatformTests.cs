using System.Text.Json;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The off-Windows gate (#1256): the record reads are served, every other tool and every pole that can reach a file
/// outside the active order is refused per call. CI runs on Windows, so the OS fact is passed in rather than read.
/// </summary>
[Trait("tier", "unit")]
public sealed class SupportedPlatformTests
{
    static IDictionary<string, JsonElement> Args(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    /// <summary>Off Windows a tool outside the served set is refused in one sentence naming the tool and the issue.</summary>
    [Fact]
    public void ANonWindowsCallToAnAssetToolIsRefusedInOneSentence()
    {
        var said = SupportedPlatform.Refusal(ToolNames.AssetStatus, null, isWindows: false);

        Assert.NotNull(said);
        Assert.Contains(ToolNames.AssetStatus, said, StringComparison.Ordinal);
        Assert.Contains("issues/1256", said, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', said);
        Assert.EndsWith(".", said, StringComparison.Ordinal);
    }

    /// <summary>On Windows nothing is refused, whatever the tool or its poles.</summary>
    [Theory]
    [InlineData(ToolNames.AssetStatus, "{}")]
    [InlineData(ToolNames.Records, """{"source": {"overlay": "skypatcher", "state": "post"}}""")]
    public void AWindowsCallIsNotRefused(string tool, string args) =>
        Assert.Null(SupportedPlatform.Refusal(tool, Args(args), isWindows: true));

    /// <summary>Off Windows a records read on the active order is served: no pole, the winner in any case, the provider below.</summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("""{"source": "WINNER"}""")]
    [InlineData("""{"versus": "previous_provider"}""")]
    [InlineData("""{"source": null, "versus": "winner"}""")]
    public void ANonWindowsRecordsReadOnTheActiveOrderIsServed(string args) =>
        Assert.Null(SupportedPlatform.Refusal(ToolNames.Records, Args(args), isWindows: false));

    /// <summary>Off Windows a pole that names a plugin, a file pair or the overlay is refused naming that pole, in any spelling of its name.</summary>
    [Theory]
    [InlineData("""{"source": "Requiem.esp"}""", "source=")]
    [InlineData("""{"Source": "Requiem.esp"}""", "source=")]
    [InlineData("""{"versus": {"overlay": "skypatcher", "state": "post"}}""", "versus=")]
    [InlineData("""{"versus": {"file": "X.esp", "mod": "Some Mod"}}""", "versus=")]
    public void ANonWindowsRecordsPoleOutsideTheActiveOrderIsRefused(string args, string pole)
    {
        var said = SupportedPlatform.Refusal(ToolNames.Records, Args(args), isWindows: false);

        Assert.NotNull(said);
        Assert.StartsWith("error: " + pole, said, StringComparison.Ordinal);
        Assert.Contains("issues/1256", said, StringComparison.Ordinal);
    }
}

/// <summary>
/// The built server, started as its own process with the OS fact swapped by a .NET startup hook (<see cref="StartupHook"/>,
/// in this test assembly), so the gate is measured where a client meets it: on the wire.
/// </summary>
[Trait("tier", "stdio")]
public sealed class SupportedPlatformProcessTests
{
    static ServerFixture StartAsNonWindows() => new(new Dictionary<string, string>
    {
        ["DOTNET_STARTUP_HOOKS"] = typeof(StartupHook).Assembly.Location,
    });

    /// <summary>Off Windows the server starts and serves the record reads, and an asset tool comes back refused, not run.</summary>
    [Fact]
    public void ANonWindowsServerServesTheRecordReadsAndRefusesTheRest()
    {
        using var server = StartAsNonWindows();

        var status = server.Call(ToolNames.LoadOrderStatus, "{}");
        Assert.True(status.BodyRan, status.Describe());

        var winner = server.Call(ToolNames.Records, """{"formids": ["000800:Skyrim.esm"]}""");
        Assert.True(winner.BodyRan, winner.Describe());

        var asset = server.Call(ToolNames.AssetStatus, """{"asset_paths": ["meshes/a.nif"]}""");
        Assert.True(asset.IsError, asset.Describe());
        Assert.False(asset.BodyRan, asset.Describe());
        Assert.Contains("issues/1256", asset.Text, StringComparison.Ordinal);

        var named = server.Call(ToolNames.Records, """{"formids": ["000800:Skyrim.esm"], "source": "Requiem.esp"}""");
        Assert.True(named.IsError, named.Describe());
        Assert.StartsWith("error: source=", named.Text, StringComparison.Ordinal);
    }
}
