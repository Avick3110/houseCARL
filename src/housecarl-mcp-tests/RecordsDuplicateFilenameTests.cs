// Converted-from: RecordsGuardProbe
using System.Text.Json;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// SPEC §4.2 — the one-pole source arms when a plugin FILENAME appears in more than one mod folder.
/// (RecordsGuardProbe arm 3.)
///
/// <para>These own a PRIVATE world, one per test, because the case only exists once a duplicate plugin file
/// is on disk — a mutation of the mods directory. Written first as a single test that created and deleted
/// the duplicate inside the SHARED world's mods dir: it was the only test in that collection writing what
/// its siblings read, so a process death inside the try, or a delete losing to a file lock, would have left
/// every later off-order test in the class getting "SEVERAL mod folders" and pointing nowhere near the
/// cause. A private world costs a build each and removes the hazard, and it lets the two assertions be two
/// tests, which is what the harness rule asks for.</para>
/// </summary>
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
