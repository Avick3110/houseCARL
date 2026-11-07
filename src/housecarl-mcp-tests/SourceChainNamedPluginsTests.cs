using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The plugins a chain's arms name, the one parse copy's bound set and the asset walk's home both start from.</summary>
[Trait("tier", "unit")]
public sealed class SourceChainNamedPluginsTests
{
    static SourceArm Arm(string spelling, SourceArmKind kind = SourceArmKind.ActiveOrder)
        => new(spelling, kind, spelling, _ => _ => null);

    /// <summary>'winner' in any case and a spelling that names no plugin name nothing; a base master and a file arm
    /// off the order are returned, in arm order, by their file name.</summary>
    [Fact]
    public void EveryNonWinnerArmThatNamesAPluginIsReturned()
    {
        var chain = new SourceChain(new[]
        {
            Arm("winner"), Arm("WINNER"), Arm("Skyrim.esm"), Arm("not a plugin"),
            Arm(@"C:\mods\Off Mod\HcOff.esp", SourceArmKind.File), Arm("Winner"),
        });
        Assert.Equal(new[] { ModKey.FromFileName("Skyrim.esm"), ModKey.FromFileName("HcOff.esp") }, chain.NamedPlugins());
    }
}
