using System.Reflection;
using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The read door is the only public way to read a body (#1282): no public single-key read is left on the
/// index view, the resolver or the record sources, so a lane cannot read per key without visibly working around it.</summary>
[Trait("tier", "unit")]
public sealed class DoorSurfaceTests
{
    static IEnumerable<string> Public(Type t)
        => t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static).Select(m => m.Name);

    [Theory]
    [InlineData(typeof(LoadOrderResolver.IndexView), "GetRecord")]
    [InlineData(typeof(LoadOrderResolver.IndexView), "FetchRecord")]
    [InlineData(typeof(LoadOrderResolver.IndexView), "ResolveTree")]
    [InlineData(typeof(LoadOrderResolver), "GetRecord")]
    [InlineData(typeof(LoadOrderResolver), "ResolveTree")]
    [InlineData(typeof(SourceChain), "Fetch")]
    [InlineData(typeof(SourceArm), "Fetch")]
    public void NoPublicSingleKeyRead(Type type, string member) => Assert.DoesNotContain(member, Public(type));

    /// <summary>The door's own walk primitives are no public read around it.</summary>
    [Theory]
    [InlineData(typeof(BodyGather), "WalkOnce")]
    [InlineData(typeof(LoadOrderResolver.IndexView), "CollectRecords")]
    [InlineData(typeof(LoadOrderResolver), "CollectRecords")]
    public void NoPublicWalkAroundTheDoor(Type type, string member) => Assert.DoesNotContain(member, Public(type));
}
