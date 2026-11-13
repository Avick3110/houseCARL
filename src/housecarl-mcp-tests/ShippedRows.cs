using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The runtime-format source with exact substrings replaced, so a test drives a rule off the row rather than
/// off the code's defaults; every substring it replaces must be in the shipped file.</summary>
static class ShippedRows
{
    static readonly string Shipped = RuntimeFormats.SourceText();

    /// <summary>The shipped source text with each edit applied in turn.</summary>
    public static string Edited(params (string From, string To)[] edits)
    {
        var json = Shipped;
        foreach (var (from, to) in edits)
        {
            Assert.Contains(from, json);
            json = json.Replace(from, to);
        }
        return json;
    }

    /// <summary>One distributor row off the edited source.</summary>
    public static DistributorFormat Distributor(string key, params (string From, string To)[] edits)
        => RuntimeFormats.DistributorsFrom(Edited(edits)).Single(d => d.Key == key);
}
