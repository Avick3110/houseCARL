using Mutagen.Bethesda.Plugins;
using Xunit;
using Xunit.Abstractions;

namespace HousecarlMcpTests;

/// <summary>The pieces place is built from, with no MO2 instance: the FormKey to FaceGen path transform, reading one
/// entry out of an archive without holding it open, and the crash-atomic write.</summary>
[Trait("tier", "unit")]
public sealed class PlaceCoreTests : IDisposable
{
    readonly ITestOutputHelper _out;
    readonly string _root = Path.Combine(Path.GetTempPath(), "hc-place-core-" + Guid.NewGuid().ToString("N"));

    public PlaceCoreTests(ITestOutputHelper output) { _out = output; Directory.CreateDirectory(_root); }
    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* temp cleanup */ } }

    // Probe A: "mesh path is folder=defining-master + '00'+6hex .nif", "tint path is facetint + .dds",
    // "the computed mesh path matches the committed fixture's facegen entry name exactly".
    [Fact]
    public void TheFaceGenPathIsTheDefiningMasterFolderAndTheIdMaskedToEightHex()
    {
        var fk = FormKey.Factory(PlaceInstance.FacegenFormId);

        Assert.Equal(@"meshes\actors\character\facegendata\facegeom\Dawnguard.esm\0001A51A.nif", FaceGenPath.For(fk, FaceGenSlot.Mesh));
        Assert.Equal(@"textures\actors\character\facegendata\facetint\Dawnguard.esm\0001A51A.dds", FaceGenPath.For(fk, FaceGenSlot.Tint));
    }

    // Probe A: "the folder is the FormKey's defining master and the id is masked to 8 hex ('00'+6) — Skyrim.esm/00000ABC.nif".
    [Fact]
    public void ADifferentMasterIsADifferentFolder()
    {
        Assert.Equal(@"meshes\actors\character\facegendata\facegeom\Skyrim.esm\00000ABC.nif",
                     FaceGenPath.For(FormKey.Factory("000ABC:Skyrim.esm"), FaceGenSlot.Mesh));
    }

    // Probe A: "Both() returns mesh first, then tint".
    [Fact]
    public void BothReturnsTheMeshThenTheTint()
    {
        var both = FaceGenPath.Both(FormKey.Factory(PlaceInstance.FacegenFormId));

        Assert.Equal(new[] { FaceGenSlot.Mesh, FaceGenSlot.Tint }, both.Select(b => b.Slot));
    }

    // Probe B: "the facegen entry's bytes are extracted from the BSA" and "an entry not in the archive returns null
    // (not a throw, not empty bytes)". Strengthened: the bytes are compared exactly, not only for being non-empty.
    [Fact]
    public void OneArchiveEntryIsReadExactlyAndAnAbsentEntryIsNull()
    {
        var bsa = Path.Combine(_root, "read.bsa");
        File.WriteAllBytes(bsa, PlaceInstance.FaceArchive());

        Assert.Equal(PlaceInstance.ArchiveFaceBytes, AssetResolver.TryReadArchiveEntry(bsa, PlaceInstance.FacegenRel));
        Assert.Null(AssetResolver.TryReadArchiveEntry(bsa, @"meshes\nope\not-in-archive.nif"));
    }

    // Probe B: "the .bsa stays renamable AND deletable after extraction — zero handles held at rest".
    [Fact]
    public void TheArchiveIsNotHeldOpenAfterTheRead()
    {
        var bsa = Path.Combine(_root, "held.bsa");
        File.WriteAllBytes(bsa, PlaceInstance.FaceArchive());
        Assert.NotNull(AssetResolver.TryReadArchiveEntry(bsa, PlaceInstance.FacegenRel));

        File.Move(bsa, bsa + ".moved");
        File.Move(bsa + ".moved", bsa);
        File.Delete(bsa);

        Assert.False(File.Exists(bsa));
    }

    // Probe C: "a fresh place writes byte-exact" and "no staging temp is left after a fresh place".
    [Fact]
    public void AFreshWriteIsByteExactAndLeavesNoTemp()
    {
        var fresh = Path.Combine(_root, "sub", "fresh.nif");
        Directory.CreateDirectory(Path.GetDirectoryName(fresh)!);
        var bytes = new byte[] { 1, 2, 3, 4 };

        AtomicFile.WriteAllBytes(fresh, bytes);

        Assert.Equal(bytes, File.ReadAllBytes(fresh));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(fresh)!).Where(f => f != fresh));
    }

    // Probe C: "an overwrite place writes the NEW bytes byte-exact", "no staging temp is left after an overwrite place",
    // and "overwrite preserves the destination's creation time — File.Replace, not File.Move". On a host whose file
    // system tunneling keeps the creation time through File.Move too, the last one cannot be told apart and stops, as
    // the probe did.
    [Fact]
    public void AnOverwriteWritesTheNewBytesAndKeepsTheCreationTime()
    {
        var oldCreate = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var over = Path.Combine(_root, "over", "over.dds");
        Directory.CreateDirectory(Path.GetDirectoryName(over)!);
        File.WriteAllBytes(over, new byte[] { 9, 9, 9 });
        File.SetCreationTimeUtc(over, oldCreate);
        var nb = new byte[] { 7, 7, 7, 7, 7 };

        AtomicFile.WriteAllBytes(over, nb);

        Assert.Equal(nb, File.ReadAllBytes(over));
        Assert.Equal(new[] { over }, Directory.GetFiles(Path.GetDirectoryName(over)!));
        if (TunnelingMasksCreationTime(_root, oldCreate))
        {
            _out.WriteLine("Skipped the creation-time half: file system tunneling keeps it through File.Move on this host.");
            return;
        }
        Assert.Equal(oldCreate, File.GetCreationTimeUtc(over));
    }

    /// <summary>Does a File.Move over an existing file keep that file's creation time on this host? Then the
    /// creation-time signal cannot tell File.Replace from File.Move.</summary>
    internal static bool TunnelingMasksCreationTime(string dir, DateTime oldCreate)
    {
        var f = Path.Combine(dir, "ctl-" + Guid.NewGuid().ToString("N") + ".dat");
        File.WriteAllBytes(f, new byte[] { 0 });
        File.SetCreationTimeUtc(f, oldCreate);
        var s = f + ".s";
        File.WriteAllBytes(s, new byte[] { 1 });
        File.Move(s, f, overwrite: true);
        var masks = File.GetCreationTimeUtc(f) == oldCreate;
        File.Delete(f);
        return masks;
    }
}
