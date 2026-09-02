// Converted-from: RecordsGuardProbe
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// SPEC §2.1.1 — to_file artifacts and @artifact re-entry, epoch-checked. (RecordsGuardProbe arm 5.)
///
/// <para>This class owns its OWN world: the staleness arm changes a plugin's mtime, which re-fingerprints
/// the build. In the linear probe that mutation sat in the middle of one procedure and every arm after it
/// silently inherited a different world; here the blast radius is one class.</para>
/// </summary>
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
