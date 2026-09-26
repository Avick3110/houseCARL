using System.Reflection;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

[assembly: TestFramework("HousecarlMcpTests.TestRunSetup", "housecarl-mcp-tests")]

namespace HousecarlMcpTests;

/// <summary>Sets up what every test in the run shares, once, before the first test runs and never on discovery: the
/// test corpus. A failure here stops the run with its own message.</summary>
public sealed class TestRunSetup : XunitTestFramework
{
    public TestRunSetup(IMessageSink messageSink) : base(messageSink) { }

    protected override ITestFrameworkExecutor CreateExecutor(AssemblyName assemblyName) =>
        new Executor(assemblyName, SourceInformationProvider, DiagnosticMessageSink);

    sealed class Executor : XunitTestFrameworkExecutor
    {
        public Executor(AssemblyName assemblyName, ISourceInformationProvider sourceInformationProvider, IMessageSink diagnosticMessageSink)
            : base(assemblyName, sourceInformationProvider, diagnosticMessageSink) { }

        protected override void RunTestCases(IEnumerable<IXunitTestCase> testCases, IMessageSink executionMessageSink,
                                             ITestFrameworkExecutionOptions executionOptions)
        {
            _ = TestCorpus.Path;
            base.RunTestCases(testCases, executionMessageSink, executionOptions);
        }
    }
}
