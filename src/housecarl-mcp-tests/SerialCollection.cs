using Xunit;

namespace HousecarlMcpTests;

/// <summary>Tests that read or set process-wide state (the FormID spelling table, the working directory, the corpus
/// path) run here, one at a time and after every parallel test.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SerialCollection
{
    public const string Name = "serial: process-global seams";
}
