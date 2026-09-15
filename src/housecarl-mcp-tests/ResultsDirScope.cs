using HousecarlMcp;

namespace HousecarlMcpTests;

/// <summary>Point the auto-spill store at a directory for the scope, then put the prior value back. Without an
/// override <c>ResultsStore.Dir</c> resolves to the test binary's folder, so every truncating call piles an
/// artifact into the build output where nothing ever cleans it up and the store's prune has to stat it again.
/// A world takes one for its lifetime; a test that needs to see EXACTLY the file its own call wrote takes a
/// private one instead, so "the file this call spilled" is a Single(), not a guess.</summary>
public sealed class ResultsDirScope : IDisposable
{
    readonly string? _prior;
    public string Dir { get; }

    public ResultsDirScope(string dir, bool create = true)
    {
        Dir = dir;
        if (create) Directory.CreateDirectory(dir);
        _prior = ResultsStore.OverrideDirForTests;
        ResultsStore.OverrideDirForTests = dir;
    }

    public void Dispose() => ResultsStore.OverrideDirForTests = _prior;
}
