using System.Text.Json;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The one-pole source when a plugin FILENAME appears in more than one mod folder. These own a
/// PRIVATE world, one per test, because the case only exists once a duplicate plugin file is on disk — a
/// mutation of the mods directory that a shared world's other tests would read.</summary>
[Trait("tier", "integration")]
public sealed class RecordsDuplicateFilenameTests : IDisposable
{
    readonly RecordsWorld _w = new();

    /// <summary>The same plugin filename present in a second mod folder.</summary>
    public RecordsDuplicateFilenameTests()
    {
        var dupDir = Path.Combine(_w.ModsDir, "OldModCopy");
        Directory.CreateDirectory(dupDir);
        File.Copy(_w.OldFile, Path.Combine(dupDir, _w.OldName));
    }

    public void Dispose() => _w.Dispose();

    string SecondWeapon => RecordsWorld.Fid(_w.Weapons[1]);

    [Fact]
    public void ABareFilenameFoundInSeveralModFoldersIsRefusedNamingThemAndTheDisambiguatingPole()
    {
        var r = RecordsTools.Records(_w.Svc, formids: new[] { SecondWeapon },
                                     source: JsonDocument.Parse("\"" + _w.OldName + "\"").RootElement.Clone());

        Assert.Contains("SEVERAL mod folders", r);
        Assert.Contains("\"mod\"", r);
    }

    [Fact]
    public void TheStructuredFileModPoleDisambiguatesIt_AndTheRecordIsServedOutOfLoadOrder()
    {
        var pole = JsonDocument.Parse($"{{\"file\": \"{_w.OldName}\", \"mod\": \"OldMod\"}}").RootElement.Clone();

        var r = RecordsTools.Records(_w.Svc, formids: new[] { SecondWeapon }, source: pole);

        Assert.Contains("OUT-OF-LOAD-ORDER", r);
        Assert.DoesNotContain("error: ", r);
    }
}
