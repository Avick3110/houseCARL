using HousecarlCore;
using HousecarlGenerator;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A references= scan over a perk whose entry-point effect Mutagen cannot parse used to throw out of the
/// whole call. The scan now carries on past the record, still finds the good match after it, and names the record
/// it could not scan in the scan note.</summary>
[Trait("tier", "integration")]
public sealed class ReferencesScanFaultTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "hc-references-scan-fault-" + Guid.NewGuid().ToString("N"));

    public ReferencesScanFaultTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* temp cleanup best-effort */ } }

    // Probe: "scan call completed (no escape, no error)", "good referencing perk matched", "unscannable perk ACCOUNTED
    // by FormKey (ScanNote)"; the control "bad perk throws from EnumerateFormLinks".
    [Fact]
    public void AnUnparseablePerkBeforeTheMatch_IsNamedInTheScanNoteAndTheScanStillFindsTheMatch()
    {
        var modKey = new ModKey("HcPerkRefsGuard", ModType.Plugin);
        var espPath = Path.Combine(_dir, modKey.FileName.String);
        var mod = new SkyrimMod(modKey, SkyrimRelease.SkyrimSE);
        var target = mod.Perks.AddNew(); target.EditorID = "HcPerkRefsGuard_Target";
        // The bad perk takes the lower FormID, so it enumerates before the good match.
        var bad = mod.Perks.AddNew(); bad.EditorID = "HcPerkRefsGuard_Bad";
        bad.Effects.Add(new PerkEntryPointModifyActorValue
        {
            EntryPoint = APerkEntryPointEffect.EntryType.CalculateWeaponDamage,
            ActorValue = ActorValue.OneHanded,
            Value = 1f,
            Modification = PerkEntryPointModifyActorValue.ModificationType.AddAVMult,
        });
        var good = mod.Perks.AddNew(); good.EditorID = "HcPerkRefsGuard_Good";
        good.NextPerk.SetTo(target.FormKey);
        mod.BeginWrite.ToPath(espPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        Assert.Equal(1, ProbeBytes.CorruptEpftBytes(espPath));

        using (var ov = SkyrimMod.CreateFromBinaryOverlay(espPath, SkyrimRelease.SkyrimSE))
        {
            var badOv = ov.Perks.First(p => p.FormKey == bad.FormKey);
            Assert.ThrowsAny<Exception>(() => ((IFormLinkContainerGetter)badOv).EnumerateFormLinks().Count());
        }

        using var resolver = LoadOrderResolver.Build(new[] { espPath });
        var svc = LoadOrderService.ForGuard(resolver, new UserConfigStore(Path.Combine(_dir, "houseCARL.user.json")));
        var q = svc.ReadArea.CrossQuery(type: null, references: new[] { target.FormKey }, editoridContains: null, conflictsOnly: false,
                                        plugins: new[] { modKey.FileName.String }, where: null, limit: 500);

        Assert.Null(q.Error);
        Assert.Equal(1, q.Total);
        Assert.Equal(new[] { good.FormKey }, q.Keys);
        Assert.Contains(bad.FormKey.ToString(), q.ScanNote, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1 record instance(s)", q.ScanNote);
    }
}
