using Xunit;

namespace HousecarlMcpTests;

/// <summary>A facegen world that cannot delete its root says so from its assert, naming the folder, and its Dispose does not throw.</summary>
[Trait("tier", "integration")]
public sealed class FaceGenWorldDisposeTests
{
    [Fact]
    public void AFileHeldOpenFailsTheAssertNamingTheFolderAndTheWorldIsDeletedOnceReleased()
    {
        if (!OperatingSystem.IsWindows()) return;   // only Windows refuses to delete a file held with FileShare.None
        var w = new FaceGenWorld();
        HeldOpen? held = null;
        try
        {
            held = HeldOpen.Hold(Path.Combine(w.Root, "instance", "mods", FaceGenWorld.BaseMod, FaceGenWorld.MasterName));
            w.Dispose();
            Assert.True(Directory.Exists(w.Root));
            var e = Assert.Throws<IOException>(w.AssertNoHandlesLeft);
            Assert.Contains(w.Root, e.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            held?.Dispose();
            w.Dispose();
        }

        w.AssertNoHandlesLeft();
        Assert.False(Directory.Exists(w.Root));
    }
}
