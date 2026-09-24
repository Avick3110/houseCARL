using Xunit;

namespace HousecarlMcpTests;

/// <summary>Tests that read or set process-wide state (the cost counters, the render budget, the auto-spill
/// directory, the FormID spelling table, the working directory) run here, one at a time and after every parallel
/// test, until that state stops being process-wide (#903). It carries the records world its members share.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SerialCollection : ICollectionFixture<RecordsFixture>
{
    public const string Name = "serial: process-global seams";
}
