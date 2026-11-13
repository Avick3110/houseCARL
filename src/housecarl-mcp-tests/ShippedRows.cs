using System.Text.Json;
using System.Text.Json.Nodes;
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

    /// <summary>One distributor row off the source with the edits applied INSIDE that row only, so a test reads the
    /// value off the row it names and not off whichever row carries the same text first.</summary>
    public static DistributorFormat Distributor(string key, params (string From, string To)[] edits)
        => RuntimeFormats.DistributorsFrom(EditedRow(key, edits)).Single(d => d.Key == key);

    /// <summary>The shipped source with one distributor row's text edited; every substring it replaces must be in that row.</summary>
    public static string EditedRow(string key, params (string From, string To)[] edits)
    {
        var doc = JsonNode.Parse(Shipped)!;
        var rows = doc["distributors"]!.AsArray();
        int at = Enumerable.Range(0, rows.Count).Single(i => (string?)rows[i]!["key"] == key);
        var row = rows[at]!.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        foreach (var (from, to) in edits)
        {
            Assert.Contains(from, row);
            row = row.Replace(from, to);
        }
        rows[at] = JsonNode.Parse(row);
        return doc.ToJsonString();
    }
}
