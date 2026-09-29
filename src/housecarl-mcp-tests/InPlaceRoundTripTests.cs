using System.Buffers.Binary;
using System.Text;
using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Xunit;
using W = HousecarlMcpTests.InPlaceGuardWorld;

namespace HousecarlMcpTests;

/// <summary>The in-place round-trip check (#961): before any op runs, the unedited target is serialized in memory and
/// every record must keep the subrecord signatures its file holds, less the measured renames. Each test stages its own
/// one-ARMA plugin in a fresh folder of the in-place world.</summary>
[Trait("tier", "integration")]
[Collection(InPlaceGuardCollection.Name)]
public sealed class InPlaceRoundTripTests
{
    const string PluginName = "HcRoundTrip.esp";
    readonly W _w;
    public InPlaceRoundTripTests(W w) { _w = w; }

    /// <summary>A plugin defining one ARMA with both genders' world and first-person models, written by Mutagen in the CK order.</summary>
    (string Path, FormKey Arma) StagePlugin()
    {
        var path = Path.Combine(_w.NewDir(), PluginName);
        var mod = new SkyrimMod(ModKey.FromFileName(PluginName), SkyrimRelease.SkyrimSE);
        var arma = mod.ArmorAddons.AddNew();
        arma.EditorID = "HcRT_Arma";
        arma.BodyTemplate = new BodyTemplate { FirstPersonFlags = BipedObjectFlag.Body, ArmorType = ArmorType.Clothing };
        arma.WorldModel = new GenderedItem<Model?>(
            new Model { File = "hc\\male.nif", Data = new byte[12] }, new Model { File = "hc\\female.nif" });
        arma.FirstPersonModel = new GenderedItem<Model?>(
            new Model { File = "hc\\male1st.nif", Data = new byte[12] }, new Model { File = "hc\\female1st.nif" });
        mod.BeginWrite.ToPath(path).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        return (path, arma.FormKey);
    }

    /// <summary>The #961 order: MOD2, MO2T, MOD4, MO4T, MOD3, MOD5 — the CK file with MOD3 moved after MO4T.</summary>
    (string Path, FormKey Arma) StageReproduction()
    {
        var (path, arma) = StagePlugin();
        var subs = RecordSubrecords(File.ReadAllBytes(path), "ARMA", out var bytes, out var start, out var end);
        Assert.Equal(new[] { "MOD2", "MO2T", "MOD3", "MOD4", "MO4T", "MOD5" },
            subs.Select(s => s.Sig).Where(s => s.StartsWith("MO")).ToArray());
        var mod3 = subs.Single(s => s.Sig == "MOD3");
        subs.Remove(mod3);
        subs.Insert(subs.FindIndex(s => s.Sig == "MO4T") + 1, mod3);
        var body = subs.SelectMany(s => s.Raw).ToArray();
        Assert.Equal(end - start, body.Length);
        body.CopyTo(bytes, start);
        File.WriteAllBytes(path, bytes);
        return (path, arma);
    }

    /// <summary>The CK file with its 8-byte BOD2 replaced by the older 12-byte BODT, record and group sizes grown to fit.</summary>
    (string Path, FormKey Arma) StageBodt()
    {
        var (path, arma) = StagePlugin();
        var subs = RecordSubrecords(File.ReadAllBytes(path), "ARMA", out var bytes, out var start, out var end);
        int i = subs.FindIndex(s => s.Sig == "BOD2");
        var bod2 = subs[i].Raw;
        var bodt = new byte[6 + 12];
        Encoding.ASCII.GetBytes("BODT").CopyTo(bodt, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(bodt.AsSpan(4), 12);
        bod2.AsSpan(6, 4).CopyTo(bodt.AsSpan(6));          // first-person flags
        bod2.AsSpan(10, 4).CopyTo(bodt.AsSpan(14));        // armor type, after the general flags byte and 3 unused
        subs[i] = ("BODT", bodt);
        var body = subs.SelectMany(s => s.Raw).ToArray();
        int grow = body.Length - (end - start);
        var outBytes = bytes[..start].Concat(body).Concat(bytes[end..]).ToArray();
        // The one ARMA record's size and its top-level group's size, both grown by the same delta.
        int rec = start - 24;
        BinaryPrimitives.WriteUInt32LittleEndian(outBytes.AsSpan(rec + 4), (uint)body.Length);
        int grup = TopGroupOf(outBytes, rec);
        BinaryPrimitives.WriteUInt32LittleEndian(outBytes.AsSpan(grup + 4),
            BinaryPrimitives.ReadUInt32LittleEndian(outBytes.AsSpan(grup + 4)) + (uint)grow);
        File.WriteAllBytes(path, outBytes);
        return (path, arma);
    }

    static int TopGroupOf(byte[] b, int recordAt)
    {
        int p = 24 + (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(4));
        while (p < b.Length)
        {
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p + 4));
            if (recordAt > p && recordAt < p + size) return p;
            p += size;
        }
        throw new InvalidOperationException("the record is in no top-level group");
    }

    /// <summary>The first record of that signature: its subrecords as raw slices, and where its body sits in the file.</summary>
    static List<(string Sig, byte[] Raw)> RecordSubrecords(byte[] file, string recordSig, out byte[] bytes, out int start, out int end)
    {
        bytes = file;
        int p = 24 + (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(4));
        while (p < file.Length)
        {
            var sig = Encoding.ASCII.GetString(file, p, 4);
            if (sig == "GRUP") { p += 24; continue; }
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(p + 4));
            if (sig != recordSig) { p += 24 + size; continue; }
            start = p + 24; end = start + size;
            var subs = new List<(string, byte[])>();
            for (int q = start; q < end;)
            {
                int len = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(q + 4));
                subs.Add((Encoding.ASCII.GetString(file, q, 4), file[q..(q + 6 + len)]));
                q += 6 + len;
            }
            return subs;
        }
        throw new InvalidOperationException($"no {recordSig} record in the file");
    }

    static bool HasSubrecord(string path, string sig) =>
        RecordSubrecords(File.ReadAllBytes(path), "ARMA", out _, out _, out _).Any(s => s.Sig == sig);

    WritePatchBuilder.PatchOutcome SetWeaponAdjust(string path, FormKey arma, bool dryRun = false)
    {
        using var r = LoadOrderResolver.Build(new[] { _w.MasterPath, path });
        return WritePatchBuilder.ApplyInPlace(r, TestCorpus.Rulebook,
            new[] { new WritePatchBuilder.PatchEdit { Target = arma, Path = new[] { "WeaponAdjust" }, Verb = "Set", Value = "1.5" } },
            path, PluginName, dryRun: dryRun);
    }

    // Pins Mutagen 0.54.4's read of the #961 order, so the bump that fixes it flips this test.
    [Fact]
    public void TheParserStillReadsTheReproductionWithoutTheMaleModels()
    {
        var (path, arma) = StageReproduction();
        using (var ov = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE))
        {
            var rec = ov.ArmorAddons.Single(x => x.FormKey == arma);
            Assert.Null(rec.WorldModel?.Male);
            Assert.Null(rec.FirstPersonModel?.Male);
            Assert.Equal("hc\\female.nif", rec.WorldModel?.Female?.File.GivenPath);
        }
        var full = SkyrimMod.CreateFromBinary(path, SkyrimRelease.SkyrimSE).ArmorAddons.Single(x => x.FormKey == arma);
        Assert.Null(full.WorldModel?.Male);
        Assert.Null(full.FirstPersonModel?.Male);
    }

    [Fact]
    public void AnInPlaceApplyToAPluginThatWouldLoseSubrecordsIsRefusedWithTheFileUntouched()
    {
        var (path, arma) = StageReproduction();
        var before = File.ReadAllBytes(path);
        var o = SetWeaponAdjust(path, arma);
        Assert.False(o.Success);
        Assert.Contains($"ArmorAddon {FormIdToken.Of(arma)}", o.Error);
        Assert.Contains("MOD2, MO2T, MOD4, MO4T", o.Error);
        Assert.Contains("UNTOUCHED", o.Error);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(path)!, ".housecarl-tmp")));
    }

    [Fact]
    public void AnInPlaceDryRunOfThatPluginIsRefusedTheSameWay()
    {
        var (path, arma) = StageReproduction();
        var o = SetWeaponAdjust(path, arma, dryRun: true);
        Assert.False(o.Success);
        Assert.Contains("MOD2, MO2T, MOD4, MO4T", o.Error);
    }

    [Fact]
    public void TheSamePluginInTheStandardOrderWritesAndKeepsTheMaleModels()
    {
        var (path, arma) = StagePlugin();
        var o = SetWeaponAdjust(path, arma);
        Assert.True(o.Success, o.Error);
        Assert.True(HasSubrecord(path, "MOD2"));
        Assert.True(HasSubrecord(path, "MOD4"));
        Assert.Contains("pre-write round trip matched", WriteTools.Render(o));
    }

    // The allow-list: BODT is rewritten as BOD2, one for one, which is not a loss.
    [Fact]
    public void ARecordWhoseBodtIsRewrittenAsBod2WritesWithoutARefusal()
    {
        var (path, arma) = StageBodt();
        Assert.True(HasSubrecord(path, "BODT"));
        var o = SetWeaponAdjust(path, arma);
        Assert.True(o.Success, o.Error);
        Assert.True(HasSubrecord(path, "BOD2"));
        Assert.False(HasSubrecord(path, "BODT"));
    }
}
