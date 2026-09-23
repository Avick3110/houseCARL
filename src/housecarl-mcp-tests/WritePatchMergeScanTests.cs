using System.Text;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The merge donor scan reads no links off a deleted record: its references are not live. Stryker row T45
/// (dev/plans/STRYKER_WRITE_PATH_2026-09-23.md).
/// </summary>
[Trait("tier", "integration")]
public sealed class WritePatchMergeScanTests : IDisposable
{
    const uint DeletedFlag = 0x20;

    readonly WritePathRig _rig = new();

    [Fact]
    public void ADonorsDeletedRecordContributesNoDonorLinks()
    {
        var space = new SkyrimMod(ModKey.FromFileName("HcWpScanSpace.esp"), SkyrimRelease.SkyrimSE);
        var target = space.Weapons.AddNew();
        target.EditorID = "HcWpScanTarget";
        _rig.Write(space);

        // A donor whose only record links into the other donor. Mutagen writes a deleted record without its body, so
        // the record is written live and its header flagged deleted after: a deleted record that still carries a body.
        var donor = new SkyrimMod(ModKey.FromFileName("HcWpScanDeleted.esp"), SkyrimRelease.SkyrimSE);
        var gone = donor.Weapons.AddNew();
        gone.EditorID = "HcWpScanGone";
        gone.Template.SetTo(target.FormKey);
        var donorPath = _rig.Write(donor, "donor", space);
        FlagFirstRecordDeleted(donorPath, "WEAP");
        Assert.True(_rig.Open(donorPath).Weapons.Single().IsDeleted);

        var ok = WritePatchBuilder.TryScanMergeDonor(donorPath, donor.ModKey,
            new HashSet<ModKey> { space.ModKey, donor.ModKey }, out var scan, out var error);

        Assert.True(ok, error);
        Assert.Contains(gone.FormKey, scan.Records);   // the record itself is still seen
        Assert.Empty(scan.DonorLinks);
    }

    /// <summary>Set the deleted bit in the header of the first record of type <paramref name="sig"/> (not its group).</summary>
    static void FlagFirstRecordDeleted(string path, string sig)
    {
        var bytes = File.ReadAllBytes(path);
        var want = Encoding.ASCII.GetBytes(sig);
        for (int i = 8; i + 12 <= bytes.Length; i++)
        {
            if (!bytes.AsSpan(i, 4).SequenceEqual(want)) continue;
            if (Encoding.ASCII.GetString(bytes, i - 8, 4) == "GRUP") continue;   // a group's label, not a record
            var flags = BitConverter.ToUInt32(bytes, i + 8) | DeletedFlag;
            BitConverter.GetBytes(flags).CopyTo(bytes, i + 8);
            File.WriteAllBytes(path, bytes);
            return;
        }
        Assert.Fail($"no {sig} record header in {path}");
    }

    public void Dispose() => _rig.Dispose();
}
