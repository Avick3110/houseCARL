using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>to_file artifacts and @artifact re-entry, epoch-checked. This class owns its OWN world because
/// the staleness test changes a plugin's mtime, which re-fingerprints the build for anything sharing
/// it.</summary>
[Trait("tier", "integration")]
public sealed class RecordsTransportTests : IDisposable
{
    readonly RecordsWorld _w = new();
    readonly string _art;

    public RecordsTransportTests() => _art = Path.Combine(_w.Root, "results", "weaps.jsonl");

    public void Dispose() => _w.Dispose();

    string[] Ids => _w.Weapons.Select(RecordsWorld.Fid).ToArray();

    string WriteArtifact()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_art)!);
        return RecordsTools.Records(_w.Svc, formids: Ids, to_file: _art,
                                    project: new RecordsTools.RecordsProject
                                    { form = "fields", fields = new[] { "BasicStats.Damage" } });
    }

    /// <summary>to_file creates the artifact's parent directory. Every other test here pre-creates it, so
    /// without this one a to_file that threw or refused on a missing parent would ship green.</summary>
    [Fact]
    public void ToFile_CreatesItsParentDirectory_TheCallerNeedNotMakeItFirst()
    {
        var nested = Path.Combine(_w.Root, "no-such-dir", "deeper", "artifact.jsonl");
        Assert.False(Directory.Exists(Path.GetDirectoryName(nested)));

        var r = RecordsTools.Records(_w.Svc, formids: Ids, to_file: nested,
                                     project: new RecordsTools.RecordsProject
                                     { form = "fields", fields = new[] { "BasicStats.Damage" } });

        Assert.True(File.Exists(nested), $"to_file did not create its parent directory. Response: {r}");
    }

    [Fact]
    public void ToFile_TheArtifactIsWrittenAndTheResponseIsManifestOnlyInline()
    {
        var r = WriteArtifact();
        Assert.True(File.Exists(_art));
        Assert.Contains(_art, r);
        Assert.DoesNotContain("Damage = 99", r);
    }

    [Fact]
    public void ArtifactReEntry_TheIdentityColumnBecomesTheListAgainstTheSameBuild()
    {
        WriteArtifact();
        var r = RecordsTools.Records(_w.Svc, formids: new[] { "@" + _art },
                                     project: new RecordsTools.RecordsProject { form = "identity" });
        Assert.False(r.StartsWith("error:", StringComparison.Ordinal));
        Assert.Contains("HcRecW0", r);
        Assert.Contains("HcRecW1", r);
    }

    [Fact]
    public void AfterTheOrderChanges_ReEntryRefusesNamingBothEpochs_NeverMixesTwoWorlds()
    {
        WriteArtifact();
        File.SetLastWriteTimeUtc(_w.OverrideFile, DateTime.UtcNow.AddMinutes(5));   // new epoch
        var r = RecordsTools.Records(_w.Svc, formids: new[] { "@" + _art },
                                     project: new RecordsTools.RecordsProject { form = "identity" });
        Assert.StartsWith("error:", r);
        Assert.Contains(_w.Epoch0!, r);
        Assert.Contains("epoch", r);
    }
}
