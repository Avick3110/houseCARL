using HousecarlCore;
using HousecarlGenerator;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A deleted record carries no live body, so a references= scan leaves it out before the reference walk: a
/// deleted perk whose leftover body throws on parse is a clean non-match, not a record named in the scan note as
/// unscannable.</summary>
[Trait("tier", "integration")]
public sealed class DeletedRecordScanTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "hc-deleted-record-scan-" + Guid.NewGuid().ToString("N"));

    public DeletedRecordScanTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* temp cleanup best-effort */ } }

    // Probe: CONTROL "bad perk reads as Deleted", CONTROL "bad perk STILL throws from EnumerateFormLinks"; "scan call
    // completed (no escape, no error)", "good referencing perk still matched", "deleted record EXCLUDED, not in ScanNote".
    [Fact]
    public void ADeletedPerkWithAThrowingBodyIsLeftOutOfTheScanNotNamedInTheScanNote()
    {
        var modKey = new ModKey("HcDeletedScanGuard", ModType.Plugin);
        var espPath = Path.Combine(_dir, modKey.FileName.String);
        var mod = new SkyrimMod(modKey, SkyrimRelease.SkyrimSE);
        var target = mod.Perks.AddNew(); target.EditorID = "HcDeletedScanGuard_Target";
        // The bad perk takes the lower FormID, so it enumerates before the good match.
        var bad = mod.Perks.AddNew(); bad.EditorID = "HcDeletedScanGuard_Bad";
        bad.Effects.Add(new PerkEntryPointModifyActorValue
        {
            EntryPoint = APerkEntryPointEffect.EntryType.CalculateWeaponDamage,
            ActorValue = ActorValue.OneHanded,
            Value = 1f,
            Modification = PerkEntryPointModifyActorValue.ModificationType.AddAVMult,
        });
        var good = mod.Perks.AddNew(); good.EditorID = "HcDeletedScanGuard_Good";
        good.NextPerk.SetTo(target.FormKey);
        mod.BeginWrite.ToPath(espPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        // Mutagen writes a deleted record with an empty body, so the flag is patched onto a written, still-bodied record.
        Assert.Equal(1, ProbeBytes.CorruptEpftBytes(espPath));
        Assert.Equal(1, ProbeBytes.SetDeletedFlag(espPath, "PERK", bad.FormKey.ID));

        using (var ov = SkyrimMod.CreateFromBinaryOverlay(espPath, SkyrimRelease.SkyrimSE))
        {
            var badOv = ov.Perks.First(p => p.FormKey == bad.FormKey);
            Assert.True(badOv.IsDeleted);
            Assert.ThrowsAny<Exception>(() => ((IFormLinkContainerGetter)badOv).EnumerateFormLinks().Count());
        }

        using var resolver = LoadOrderResolver.Build(new[] { espPath });
        var svc = LoadOrderService.ForGuard(resolver, new UserConfigStore(Path.Combine(_dir, "houseCARL.user.json")));
        var q = svc.ReadArea.CrossQuery(type: null, references: new[] { target.FormKey }, editoridContains: null, conflictsOnly: false,
                                        plugins: new[] { modKey.FileName.String }, where: null, limit: 500);

        Assert.Null(q.Error);
        Assert.Equal(new[] { good.FormKey }, q.Keys);
        Assert.DoesNotContain(bad.FormKey.ToString(), q.ScanNote ?? "", StringComparison.OrdinalIgnoreCase);
    }
}
