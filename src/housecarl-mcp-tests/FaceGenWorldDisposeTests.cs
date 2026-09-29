using Xunit;

namespace HousecarlMcpTests;

/// <summary>A facegen world that cannot delete its root says so, naming the folder, instead of leaving it in temp.</summary>
[Trait("tier", "integration")]
public sealed class FaceGenWorldDisposeTests
{
    [Fact]
    public void DisposeWithAFileHeldOpenThrowsNamingTheFolderAndDeletesItOnceReleased()
    {
        if (!OperatingSystem.IsWindows()) return;   // only Windows refuses to delete a file held with FileShare.None
        var w = new FaceGenWorld();
        var held = HeldOpen.Hold(Path.Combine(w.Root, "instance", "mods", FaceGenWorld.BaseMod, FaceGenWorld.MasterName));
        try
        {
            var e = Assert.Throws<IOException>(w.Dispose);
            Assert.Contains(w.Root, e.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { held.Dispose(); }

        w.Dispose();
        Assert.False(Directory.Exists(w.Root));
    }
}
