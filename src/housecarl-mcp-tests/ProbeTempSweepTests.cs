using System.Diagnostics;
using System.Reflection;
using HousecarlGenerator;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The liveness call behind <see cref="ProbeTemp"/>'s sweep of old fixture roots. It decides whether
/// an <c>hc-&lt;pid&gt;</c> root is residue from a killed run or the scratch of a run still working in it, so a
/// wrong answer either leaves a directory behind or deletes a live run's fixtures mid-run. Reflected: the check
/// is private to the class that owns the sweep.</summary>
[Trait("tier", "unit")]
public sealed class ProbeTempSweepTests
{
    static bool IsLive(int pid) => (bool)typeof(ProbeTemp)
        .GetMethod("IsLive", BindingFlags.NonPublic | BindingFlags.Static)!
        .Invoke(null, new object[] { pid })!;

    static string SweepUnlistable(string temp, Exception ex) => (string)typeof(ProbeTemp)
        .GetMethod("SweepUnlistable", BindingFlags.NonPublic | BindingFlags.Static)!
        .Invoke(null, new object[] { temp, ex })!;

    [Fact]
    public void OwnProcessIsLive() => Assert.True(IsLive(Environment.ProcessId));

    [Fact]
    public void PidNoProcessHoldsIsDead() => Assert.False(IsLive(FreePid()));

    /// <summary>The Windows System process is running, but a run without the rights to open it cannot read
    /// its exit state. Only "no such process" is evidence of death; everything else leaves the root alone.</summary>
    [Fact]
    public void ProcessWeCannotInspectIsLive()
    {
        var pid = SystemPid();
        Assert.True(IsLive(pid), $"pid {pid} is running, so its root must not be swept");
    }

    /// <summary>A temp directory this process may traverse and write but not read denies the sweep's
    /// enumeration while the fixture root is still created, so the run goes green with no start sentence: the
    /// line about the sweep is then the only thing that says the roots of killed runs are piling up.
    /// Reflected: the builder is private to the class that owns the sweep.</summary>
    [Fact]
    public void UnlistableTempNamesTheDirectoryAndTheReason()
    {
        var line = SweepUnlistable(@"C:\Temp", new UnauthorizedAccessException(@"Access to the path 'C:\Temp' is denied."));
        Assert.Contains(@"C:\Temp", line);
        Assert.Contains(@"Access to the path 'C:\Temp' is denied.", line);
        Assert.Contains("could not look", line);
        Assert.Contains("TMP/TEMP", line);
    }

    /// <summary>A sweep that could not look is not an entry left behind: the two states have different
    /// remedies, and counting the first as the second names residue nobody saw.</summary>
    [Fact]
    public void UnlistableTempIsNotReportedAsResidue()
    {
        var line = SweepUnlistable(@"C:\Temp", new UnauthorizedAccessException("denied"));
        Assert.DoesNotContain("left 1 entry", line);
        Assert.DoesNotContain("entries", line);
    }

    /// <summary>A pid no process holds, confirmed rather than assumed.</summary>
    static int FreePid()
    {
        for (int pid = 999_996; pid > 900_000; pid -= 4)
        {
            try { Process.GetProcessById(pid).Dispose(); }
            catch (ArgumentException) { return pid; }
        }
        throw new InvalidOperationException("every candidate pid is in use");
    }

    /// <summary>The System (4) or Idle (0) process: running, and normally not openable.</summary>
    static int SystemPid()
    {
        foreach (var pid in new[] { 4, 0 })
        {
            try { Process.GetProcessById(pid).Dispose(); return pid; }
            catch (ArgumentException) { }
        }
        throw new InvalidOperationException("neither pid 4 nor pid 0 is running");
    }
}
