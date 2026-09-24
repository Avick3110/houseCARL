using Xunit;

namespace HousecarlMcpTests;

/// <summary>Tests that read or set process-wide state (the cost counters, the render budget, the auto-spill
/// directory, the FormID spelling table, the working directory) run here, one at a time and after every parallel
/// test, until that state stops being process-wide (#903).</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SerialCollection
{
    public const string Name = "serial: process-global seams";
}

/// <summary>The serial tests that read the shared records world, in their own collection so the other serial
/// classes do not build one (#903).</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SerialRecordsCollection : ICollectionFixture<RecordsFixture>
{
    public const string Name = "serial: process-global seams, records world";
}
