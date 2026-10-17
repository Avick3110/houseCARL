using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A delta window's row bookkeeping: every row it reads comes back answered, a repeated FormID and a subject
/// that errors included.</summary>
[Trait("tier", "integration")]
public sealed class DeltaWindowRowTests : IDisposable
{
    readonly ScratchMo2 _mo2 = new("hc-delta-rows-");
    readonly ModKey _a = new("HcDwA", ModType.Master);
    readonly ModKey _b = new("HcDwB", ModType.Plugin);
    readonly FormKey[] _weapons;

    public DeltaWindowRowTests()
    {
        var a = new SkyrimMod(_a, SkyrimRelease.SkyrimSE);
        _weapons = Enumerable.Range(0, 2).Select(i => a.Weapons.AddNew("HcDwW" + i).FormKey).ToArray();
        a.BeginWrite.ToPath(_mo2.InMod(_a.Name, _a)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        WriteB(_weapons[0]);
        _mo2.Profile($"{_a.FileName}\r\n{_b.FileName}\r\n", $"*{_a.FileName}\r\n*{_b.FileName}\r\n", $"+{_b.Name}\r\n+{_a.Name}\r\n");
    }

    public void Dispose() => _mo2.Delete();

    /// <summary>HcDwB.esp overriding only <paramref name="fk"/>, its EditorID changed.</summary>
    void WriteB(FormKey fk)
    {
        var b = new SkyrimMod(_b, SkyrimRelease.SkyrimSE);
        var w = new Weapon(fk, SkyrimRelease.SkyrimSE) { EditorID = "HcDwOverride" };
        b.Weapons.Add(w);
        b.BeginWrite.ToPath(_mo2.InMod(_b.Name, _b)).WithNoLoadOrder().Write();
    }

    static IReadOnlyList<RecordReads.DeltaRow> Delta(LoadOrderService svc, IReadOnlyList<string> ids, RecordReads.PoleSpec subject)
        => svc.DeltaBatch(ids, subject, new RecordReads.PoleSpec(RecordReads.PoleKind.Named, "HcDwA.esm"), null, null,
                          out _, out _, out _, out var refusal, out _, ComparisonMeter.Unmetered())
              is var rows && refusal is null ? rows : throw new InvalidOperationException(refusal);

    /// <summary>A FormID named twice is answered on both rows, and the two rows are the same diff.</summary>
    [Fact]
    public void AFormIdNamedTwiceAnswersBothRowsAlike()
    {
        using var svc = _mo2.Open();
        var id = ScratchMo2.Fid(_weapons[0]);
        var rows = Delta(svc, new[] { id, id }, RecordReads.PoleSpec.Winner);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => { Assert.NotNull(r); Assert.Null(r.Error); Assert.NotNull(r.Diff); });
        Assert.Equal(rows[0].Diff!.Deltas, rows[1].Diff!.Deltas);
        Assert.Contains(rows[0].Diff!.Deltas, d => d.Contains("HcDwOverride"));
    }

    /// <summary>A subject the index says holds the record but whose read errors still answers its row, with the
    /// subject's error. Rewritten under the pin, the file refuses the call (ruling F); rewritten with its size and
    /// last-write time kept, the one change ruling F cannot see, the row carries the error.</summary>
    [Fact]
    public void ASubjectThatHoldsTheRecordButErrorsCarriesItsError()
    {
        using var svc = _mo2.Open();
        var subject = new RecordReads.PoleSpec(RecordReads.PoleKind.Named, _b.FileName.String);
        var ids = new[] { ScratchMo2.Fid(_weapons[0]) };
        var path = _mo2.InMod(_b.Name, _b);
        var time = File.GetLastWriteTimeUtc(path);
        svc.CaptureView();   // the index is built while HcDwB.esp still overrides the first weapon
        void RewriteB(bool keepStamp) => svc.ReadArea.AfterReadPinForGuard = () =>
        {
            svc.ReadArea.AfterReadPinForGuard = null;
            WriteB(_weapons[1]);   // inside the hold: the file no longer holds what the pinned index says it does
            if (keepStamp) File.SetLastWriteTimeUtc(path, time);
        };

        RewriteB(keepStamp: true);
        var row = Assert.Single(Delta(svc, ids, subject));
        Assert.NotNull(row);
        Assert.StartsWith("subject: ", row.Error);

        WriteB(_weapons[0]);
        File.SetLastWriteTimeUtc(path, time);
        RewriteB(keepStamp: false);
        Assert.Equal(_b.FileName.String, Assert.Throws<LoadOrderChangedMidCallException>(() => Delta(svc, ids, subject)).Plugin);
    }
}
