using System.Text.Json;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The artifact family's fixture: a <see cref="RecordsWorld"/> plus a PRIVATE auto-spill results
/// directory. Without the override <c>ResultsStore.Dir</c> resolves to the server binary's folder, so
/// truncating calls would write artifacts into the build output and read each other's spills.</summary>
public sealed class ArtifactFixture : IDisposable
{
    public RecordsWorld W { get; }

    /// <summary>The class-wide auto-spill directory. A test that needs to see EXACTLY the file its own call
    /// wrote takes a private one via <see cref="ArtifactTestBase.OwnResults"/> instead.</summary>
    public string ResultsDir { get; }

    readonly string? _priorResultsDir;

    public ArtifactFixture()
    {
        W = new RecordsWorld();
        ResultsDir = Path.Combine(W.Root, "server-results");
        Directory.CreateDirectory(ResultsDir);
        _priorResultsDir = ResultsStore.OverrideDirForTests;
        ResultsStore.OverrideDirForTests = ResultsDir;
    }

    public void Dispose()
    {
        // Before the world's delete: the static must not be left naming a directory the next line removes.
        ResultsStore.OverrideDirForTests = _priorResultsDir;
        W.Dispose();
    }
}

/// <summary>Shared shorthand for the artifact tests: the world, the artifact readers, the transport helpers.</summary>
public abstract class ArtifactTestBase
{
    protected readonly RecordsWorld W;
    protected readonly string ResultsDir;

    protected ArtifactTestBase(ArtifactFixture f) { W = f.W; ResultsDir = f.ResultsDir; }

    protected LoadOrderService Svc => W.Svc;

    protected static string Fid(Mutagen.Bethesda.Plugins.FormKey fk) => RecordsWorld.Fid(fk);

    /// <summary>The three weapons the world's master defines, as FormID tokens (index 2 is deleted by the
    /// active override — so a batch over this set carries one error row, which is deliberate).</summary>
    protected string[] Ids => W.Weapons.Select(Fid).ToArray();

    /// <summary>A caller-named artifact path under the world's scratch tree.</summary>
    protected string Art(string name) => W.Scratch("artifacts", name);

    protected static JsonElement Je(string json) => JsonDocument.Parse(json).RootElement.Clone();

    protected static RecordsTools.RecordsProject Form(string form) => new() { form = form };

    protected static readonly RecordsTools.RecordsProject Identity = new() { form = "identity" };
    protected static readonly RecordsTools.RecordsProject Everything = new() { form = "everything" };

    /// <summary>A refusal: the text lane's own discriminant plus what the caller claims it names.</summary>
    protected static void Refused(string response, params string[] mustName)
    {
        Assert.StartsWith("error:", response);
        foreach (var s in mustName) Assert.Contains(s, response);
    }

    protected static ResultArtifact.Manifest ManifestOf(string path)
    {
        var (m, _, err) = ResultArtifact.ReadIdentity(path, File.ReadAllText(path));
        Assert.Null(err);
        return m!;
    }

    protected static List<string> TokensOf(string path)
    {
        var (_, tokens, err) = ResultArtifact.ReadIdentity(path, File.ReadAllText(path));
        Assert.Null(err);
        return tokens!;
    }

    /// <summary>Point the auto-spill store at a directory of this test's own for the scope, then put the prior
    /// value back. One test, one directory — so "the file this call spilled" is a Single(), not a guess.</summary>
    protected sealed class ResultsDirScope : IDisposable
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

    protected ResultsDirScope OwnResults(string name) => new(W.Scratch("spills", name, "dir"));

    /// <summary>The one artifact a spilling call left in its own results directory.</summary>
    protected static string TheSpill(ResultsDirScope d) => Assert.Single(Directory.GetFiles(d.Dir, "*.jsonl"));

    /// <summary>The wire spellings of <c>format=</c>, derived from the product's own transport enum — a
    /// hand-typed list would be short by exactly whatever a later transport adds.</summary>
    public static TheoryData<string> Transports()
    {
        var d = new TheoryData<string>();
        foreach (var n in Enum.GetNames<Wire.QueryFormat>()) d.Add(n.ToLowerInvariant());
        return d;
    }
}
