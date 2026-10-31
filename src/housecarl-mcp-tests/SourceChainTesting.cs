using HousecarlCore;
using Mutagen.Bethesda.Plugins;

namespace HousecarlMcpTests;

/// <summary>One key through <see cref="SourceChain.FetchMany"/>, a set of one, with the fault's PulledBy the caller names.</summary>
static class SourceChainTesting
{
    public static SourceFetch Fetch(this SourceChain chain, FormKey key, string pulledBy = "")
    {
        var got = chain.FetchMany(new[] { key })[key];
        return got.Fault is { } fault ? got with { Fault = fault with { PulledBy = pulledBy } } : got;
    }
}
