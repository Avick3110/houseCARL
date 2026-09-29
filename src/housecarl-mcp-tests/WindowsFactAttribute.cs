using Xunit;

namespace HousecarlMcpTests;

/// <summary>A fact that reports as skipped, not passed, off Windows.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows only: it needs a file lock that blocks a delete.";
    }
}
