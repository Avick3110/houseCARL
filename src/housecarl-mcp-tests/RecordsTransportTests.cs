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

    /// <summary>
    /// to_file creates the artifact's parent directory. In the linear probe this was covered by accident —
    /// three arms wrote into results/ before anything created it — and the conversion pre-creates the
    /// directory everywhere, so the coverage was lost silently. Asserted deliberately here: a change making
    /// to_file throw or refuse on a missing parent would otherwise ship with the whole suite green.
    /// </summary>
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
