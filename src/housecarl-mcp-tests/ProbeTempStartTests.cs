using System.Reflection;
using HousecarlGenerator;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The sentence a run prints when it cannot make the fixture directory it works in — a full or
/// read-only temp volume, or a plain file already at that path. The redirect itself cannot be driven from a
/// test without repointing this process's own temp directory, so the sentence is tested where it is built.
/// Reflected: the builder is private to the class that owns the redirect.</summary>
[Trait("tier", "unit")]
public sealed class ProbeTempStartTests
{
    static string StartFailure(string root, Exception ex) => (string)typeof(ProbeTemp)
        .GetMethod("StartFailure", BindingFlags.NonPublic | BindingFlags.Static)!
        .Invoke(null, new object[] { root, ex })!;

    [Fact]
    public void NamesThePathAndTheUnderlyingReason()
    {
        var sentence = StartFailure(@"C:\Temp\hc-1234", new IOException("The disk is full."));
        Assert.Contains(@"C:\Temp\hc-1234", sentence);
        Assert.Contains("The disk is full.", sentence);
    }

    [Fact]
    public void SaysWhatToTry()
    {
        var sentence = StartFailure(@"C:\Temp\hc-1234", new IOException("A file with the same name exists."));
        Assert.Contains("free space", sentence);
        Assert.Contains("remove", sentence);
    }
}
