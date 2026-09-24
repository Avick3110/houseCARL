using Xunit;

namespace HousecarlMcpTests;

/// <summary>Tests that read or set a process-global the product keeps as a test seam (the cost counters, the render
/// budget, the auto-spill directory, the FormID spelling table) run here, one at a time and after every parallel
/// test, until those seams stop being process-wide (#903). It carries the records world its members share.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SerialCollection : ICollectionFixture<RecordsFixture>
{
    public const string Name = "serial: process-global seams";
}
