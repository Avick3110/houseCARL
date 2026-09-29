using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>plugins.names takes the '@file' spelling formids= and references= take: one '@&lt;absolute path&gt;'
/// entry stands in place of the list, read by the same expander, with the same refusals (#931).</summary>
[Collection("records")]
[Trait("tier", "integration")]
public sealed class RecordsScopeAtFileTests : RecordsTestBase
{
    static string TempPath(string ext) => Path.Combine(Path.GetTempPath(), "hc-scope-atfile-" + Guid.NewGuid().ToString("N") + ext);

    /// <summary>The scan's scope is the file's two plugins and not the third active one; the artifact's manifest is
    /// where the response states the scope it scanned.</summary>
    [Fact]
    public void AnAtFileScopeScansExactlyThePluginsTheFileLists()
    {
        var file = TempPath(".txt");
        var artifact = TempPath(".jsonl");
        File.WriteAllText(file, W.MasterName + "\n" + W.OverrideName + "\n");
        try
        {
            var text = RecordsTools.Records(Svc, plugins: Scope("@" + file), types: new[] { "WEAP" }, to_file: artifact);

            Assert.DoesNotContain("error:", text);
            var manifest = File.ReadLines(artifact).First();
            Assert.Contains(W.MasterName, manifest);
            Assert.Contains(W.OverrideName, manifest);
            Assert.DoesNotContain(W.MidName, manifest);
        }
        finally { File.Delete(file); File.Delete(artifact); }
    }

    [Fact]
    public void AnAtFileThatDoesNotExistIsRefused()
    {
        var text = RecordsTools.Records(Svc, plugins: Scope("@" + TempPath(".txt")), types: new[] { "WEAP" });

        Refused(text, "could not read");
    }

    /// <summary>'@file' stands in place of the whole list on plugins.names as on formids=, so a name beside it is
    /// refused the same way rather than spliced.</summary>
    [Fact]
    public void AnAtFileBesideAnInlineNameIsRefused()
    {
        var file = TempPath(".txt");
        File.WriteAllText(file, W.OverrideName + "\n");
        try
        {
            var text = RecordsTools.Records(Svc, plugins: Scope(W.MasterName, "@" + file), types: new[] { "WEAP" });

            Refused(text, "mixes");
        }
        finally { File.Delete(file); }
    }

    public RecordsScopeAtFileTests(RecordsFixture f) : base(f) { }
}
