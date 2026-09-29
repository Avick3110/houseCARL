using System.Text.RegularExpressions;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>housecarl_check's plugins= and exclude= take the '@file' spelling housecarl_records' plugins.names takes,
/// through the one plugin list expander (#931). The world holds five dangling refs, three in the base master.</summary>
[Trait("tier", "integration")]
public sealed class CheckPluginListAtFileTests : IClassFixture<CheckWorldFixture>
{
    readonly CheckWorld W;
    public CheckPluginListAtFileTests(CheckWorldFixture f) => W = f.W;

    static readonly Regex HeadRx = new(@"^scanned (\d+) plugins? .*?(\d+) dangling ref\(s\)", RegexOptions.Compiled);

    static (int Scanned, int Dangling) Totals(string response)
    {
        var m = response.Split('\n').Select(l => HeadRx.Match(l.Trim())).FirstOrDefault(x => x.Success)
                ?? throw new InvalidOperationException("no errors-family head line: " + response.Split('\n')[0]);
        return (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
    }

    string ListFile(params string[] lines)
    {
        var path = Path.Combine(W.Root, "list-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
        return path;
    }

    [Fact]
    public void PluginsFromAListFileSweepOnlyThatPlugin()
    {
        var text = CheckTools.CheckTool(W.Svc, plugins: new[] { "@" + ListFile("HcCeWireMod.esp") });

        Assert.Equal((1, 2), Totals(text));
    }

    [Fact]
    public void ExcludeFromAListFileLeavesThatPluginOut()
    {
        var text = CheckTools.CheckTool(W.Svc, exclude: new[] { "@" + ListFile(W.BaseMasterName) });

        Assert.Equal((1, 2), Totals(text));
    }

    [Fact]
    public void AnAtFileBesideAnInlineNameIsRefusedNamingTheSpellingToPass()
    {
        var text = CheckTools.CheckTool(W.Svc, plugins: new[] { "HcCeWireMod.esp", "@" + ListFile("HcCeWireMod.esp") });

        Assert.StartsWith("error:", text);
        Assert.Contains("plugins=[\"@<path>\"] alone", text);
    }
}
