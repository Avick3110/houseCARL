using System.Text.Json;
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

    [Fact]
    public void AnAtFileBesideAnInlineNameOnExcludeIsRefusedNamingTheSpellingToPass()
    {
        var text = CheckTools.CheckTool(W.Svc, exclude: new[] { W.BaseMasterName, "@" + ListFile(W.BaseMasterName) });

        Assert.StartsWith("error:", text);
        Assert.Contains("exclude=[\"@<path>\"] alone", text);
    }

    /// <summary>The file's names go through check's own resolution: a case mismatch resolves, as it does inline.</summary>
    [Fact]
    public void AListFileNameInAnotherCaseResolvesAsItDoesInline()
    {
        var text = CheckTools.CheckTool(W.Svc, plugins: new[] { "@" + ListFile("hccewiremod.ESP") });

        Assert.Equal((1, 2), Totals(text));
    }

    /// <summary>A name in the file that is nowhere is refused by check's own sentence, as it is inline.</summary>
    [Fact]
    public void AListFileNameFoundNowhereIsRefusedAsItIsInline()
    {
        var fromFile = CheckTools.CheckTool(W.Svc, plugins: new[] { "@" + ListFile("NoSuchPlugin931.esp") });
        var inline = CheckTools.CheckTool(W.Svc, plugins: new[] { "NoSuchPlugin931.esp" });

        Assert.Contains("NoSuchPlugin931.esp", fromFile);
        Assert.Equal(inline, fromFile);
    }

    [Fact]
    public void AListFileThatDoesNotExistIsRefusedNamingPlugins()
    {
        var text = CheckTools.CheckTool(W.Svc, plugins: new[] { "@" + Path.Combine(W.Root, "missing-" + Guid.NewGuid().ToString("N") + ".txt") });

        Assert.StartsWith("error:", text);
        Assert.Contains("could not read plugins= list file", text);
    }

    [Fact]
    public void AFormIdArtifactIsRefusedByItsIdentity()
    {
        var artifact = Path.Combine(W.Root, "npcs-" + Guid.NewGuid().ToString("N") + ".jsonl");
        RecordsTools.Records(W.Svc, types: new[] { "NPC_" }, to_file: artifact);

        var text = CheckTools.CheckTool(W.Svc, plugins: new[] { "@" + artifact });

        Assert.StartsWith("error:", text);
        Assert.Contains("there is no plugin list in it for plugins=", text);
    }

    /// <summary>'@@' names a plugin whose filename starts with '@', on both lists: the name reaches check's own
    /// resolution, which refuses it as a name rather than reading a file.</summary>
    [Fact]
    public void ADoubledAtIsAPluginNameOnPluginsAndOnExclude()
    {
        var plugins = CheckTools.CheckTool(W.Svc, plugins: new[] { "@@HcCeWireMod.esp" });
        var exclude = CheckTools.CheckTool(W.Svc, exclude: new[] { "@@HcCeWireMod.esp" });

        foreach (var text in new[] { plugins, exclude })
        {
            Assert.Contains("'@HcCeWireMod.esp'", text);
            Assert.DoesNotContain("list file", text);
        }
    }

    /// <summary>A to_file manifest echoes the list file, not the names it held.</summary>
    [Fact]
    public void AToFileManifestEchoesTheListFile()
    {
        var list = ListFile("HcCeWireMod.esp");
        var artifact = Path.Combine(W.Root, "check-" + Guid.NewGuid().ToString("N") + ".jsonl");

        CheckTools.CheckTool(W.Svc, plugins: new[] { "@" + list }, to_file: artifact);

        var manifest = JsonDocument.Parse(File.ReadLines(artifact).First()).RootElement;
        Assert.Equal("@" + list, manifest.GetProperty("query").GetProperty("plugins").GetString());
    }
}
