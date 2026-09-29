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

/// <summary>The round-trip check (#961) on the in-place and into= lanes, each test on its own staged plugin.</summary>
[Trait("tier", "integration")]
[Collection(InPlaceGuardCollection.Name)]
public sealed class InPlaceRoundTripTests
{
    const string PluginName = "HcRoundTrip.esp";
    readonly W _w;
    public InPlaceRoundTripTests(W w) { _w = w; }

    /// <summary>One ARMA with both genders' world and first-person models, which Mutagen writes in the CK order.</summary>
    internal static FormKey AddArma(SkyrimMod mod)
    {
        var arma = mod.ArmorAddons.AddNew();
        arma.EditorID = "HcRT_Arma";
        arma.BodyTemplate = new BodyTemplate { FirstPersonFlags = BipedObjectFlag.Body, ArmorType = ArmorType.Clothing };
        arma.WorldModel = new GenderedItem<Model?>(
            new Model { File = "hc\\male.nif", Data = new byte[12] }, new Model { File = "hc\\female.nif" });
        arma.FirstPersonModel = new GenderedItem<Model?>(
            new Model { File = "hc\\male1st.nif", Data = new byte[12] }, new Model { File = "hc\\female1st.nif" });
        return arma.FormKey;
    }

    /// <summary>A plugin defining that ARMA and one keyword, written by Mutagen in the CK order.</summary>
    (string Path, FormKey Arma) StagePlugin()
    {
        var path = Path.Combine(_w.NewDir(), PluginName);
        var mod = new SkyrimMod(ModKey.FromFileName(PluginName), SkyrimRelease.SkyrimSE);
        var arma = AddArma(mod);
        mod.Keywords.AddNew().EditorID = "HcRT_Kw";
        mod.BeginWrite.ToPath(path).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        return (path, arma);
    }

    /// <summary>The #961 order: MOD2, MO2T, MOD4, MO4T, MOD3, MOD5 — the CK file with MOD3 moved after MO4T.</summary>
    (string Path, FormKey Arma) StageReproduction()
    {
        var (path, arma) = StagePlugin();
        ToIssueOrder(path);
        return (path, arma);
    }

    /// <summary>Rewrite the file's one ARMA into the #961 order in place; sizes are unchanged.</summary>
    internal static void ToIssueOrder(string path)
    {
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
    }

    /// <summary>The CK file with its 8-byte BOD2 replaced by the older 12-byte BODT, record and group sizes grown to fit.</summary>
    (string Path, FormKey Arma) StageBodt()
    {
        var (path, arma) = StagePlugin();
        EditRecord(path, "ARMA", edit: subs =>
        {
            int i = subs.FindIndex(s => s.Sig == "BOD2");
            var bod2 = subs[i].Raw;
            var bodt = new byte[12];
            bod2.AsSpan(6, 4).CopyTo(bodt);                 // first-person flags
            bod2.AsSpan(10, 4).CopyTo(bodt.AsSpan(8));      // armor type, after the general flags byte and 3 unused
            subs[i] = ("BODT", Subrecord("BODT", bodt));
            return subs;
        });
        return (path, arma);
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
        Assert.Contains(WriteSentences.RoundTripChecked, WriteTools.Render(o));
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

    /// <summary>Rewrite the first record of that signature, its body and header, with its size and every enclosing group's grown to fit.</summary>
    internal static void EditRecord(string path, string recordSig,
        Func<List<(string Sig, byte[] Raw)>, List<(string Sig, byte[] Raw)>>? edit = null, Action<byte[]>? header = null)
    {
        var b = File.ReadAllBytes(path);
        var groups = new List<(int Start, int End)>();
        int p = 24 + (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(4));
        while (p < b.Length)
        {
            groups.RemoveAll(g => g.End <= p);
            var sig = Encoding.ASCII.GetString(b, p, 4);
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p + 4));
            if (sig == "GRUP") { groups.Add((p, p + size)); p += 24; continue; }
            if (sig != recordSig) { p += 24 + size; continue; }
            var subs = new List<(string Sig, byte[] Raw)>();
            for (int q = p + 24; q < p + 24 + size;)
            {
                int len = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(q + 4));
                subs.Add((Encoding.ASCII.GetString(b, q, 4), b[q..(q + 6 + len)]));
                q += 6 + len;
            }
            var body = (edit is null ? subs : edit(subs)).SelectMany(s => s.Raw).ToArray();
            int grow = body.Length - size;
            var head = b[p..(p + 24)];
            header?.Invoke(head);
            BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(4), (uint)body.Length);
            var outBytes = b[..p].Concat(head).Concat(body).Concat(b[(p + 24 + size)..]).ToArray();
            foreach (var g in groups)
                BinaryPrimitives.WriteUInt32LittleEndian(outBytes.AsSpan(g.Start + 4),
                    BinaryPrimitives.ReadUInt32LittleEndian(outBytes.AsSpan(g.Start + 4)) + (uint)grow);
            File.WriteAllBytes(path, outBytes);
            return;
        }
        throw new InvalidOperationException($"no {recordSig} record in the file");
    }

    /// <summary>The CK-order plugin plus whatever <paramref name="add"/> puts in it, written by Mutagen.</summary>
    (string Path, FormKey Arma) StagePluginWith(Action<SkyrimMod> add)
    {
        var path = Path.Combine(_w.NewDir(), PluginName);
        var mod = new SkyrimMod(ModKey.FromFileName(PluginName), SkyrimRelease.SkyrimSE);
        var arma = AddArma(mod);
        add(mod);
        mod.BeginWrite.ToPath(path).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        return (path, arma);
    }

    static void AddPlacedStatic(SkyrimMod mod)
    {
        var stat = mod.Statics.AddNew("HcRT_Static");
        var cell = WriteEngine.AddInteriorCell(mod, "HcRT_Cell");
        cell.Temporary.Add(new PlacedObject(mod.GetNextFormKey(), SkyrimRelease.SkyrimSE) { Base = stat.ToNullableLink(), Placement = new Placement() });
    }

    static byte[] Subrecord(string sig, byte[] payload)
    {
        var raw = new byte[6 + payload.Length];
        Encoding.ASCII.GetBytes(sig).CopyTo(raw, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(4), (ushort)payload.Length);
        payload.CopyTo(raw, 6);
        return raw;
    }

    // Allowed: an LTEX INAM on a record older than form version 43, which Mutagen neither reads nor writes.
    [Fact]
    public void AnLtexInamBelowFormVersion43IsAnAllowedLoss()
    {
        var (path, arma) = StagePluginWith(m =>
        {
            var ltex = m.LandscapeTextures.AddNew("HcRT_Ltex");
            ltex.Flags = LandscapeTexture.Flag.IsSnow;
        });
        EditRecord(path, "LTEX", header: h => BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(20), 35));
        Assert.Contains(RecordSubrecords(File.ReadAllBytes(path), "LTEX", out _, out _, out _), s => s.Sig == "INAM");
        var o = SetWeaponAdjust(path, arma);
        Assert.True(o.Success, o.Error);
    }

    // Allowed: a deleted REFR whose body is only its NAME, the measured shape; Mutagen writes it empty.
    [Fact]
    public void ADeletedRefrsBodyIsAnAllowedLoss()
    {
        var (path, arma) = StagePluginWith(AddPlacedStatic);
        EditRecord(path, "REFR", edit: subs => subs.Where(s => s.Sig == "NAME").ToList(), header: h => h[8] |= 0x20);
        Assert.Contains(RecordSubrecords(File.ReadAllBytes(path), "REFR", out _, out _, out _), s => s.Sig == "NAME");
        var o = SetWeaponAdjust(path, arma);
        Assert.True(o.Success, o.Error);
    }

    // Allowed: a REFR XRMR of four zero bytes, a zero room count Mutagen does not write back.
    [Fact]
    public void AnAllZeroRefrXrmrIsAnAllowedLoss()
    {
        var (path, arma) = StagePluginWith(AddPlacedStatic);
        EditRecord(path, "REFR", edit: subs =>
        {
            subs.Insert(subs.FindIndex(s => s.Sig == "DATA"), ("XRMR", Subrecord("XRMR", new byte[4])));
            return subs;
        });
        Assert.Contains(RecordSubrecords(File.ReadAllBytes(path), "REFR", out _, out _, out _), s => s.Sig == "XRMR");
        var o = SetWeaponAdjust(path, arma);
        Assert.True(o.Success, o.Error);
    }

    // The allowance is exactly the measured class: the same subrecord outside its condition, or on another type, still refuses.
    [Theory]
    [InlineData("LTEX", "INAM", 44, false, true)]
    [InlineData("WEAP", "INAM", 35, false, true)]
    [InlineData("REFR", "NAME", 44, false, true)]
    [InlineData("ACHR", "NAME", 44, true, true)]
    [InlineData("REFR", "DATA", 44, true, false)]
    [InlineData("REFR", "XRMR", 44, false, false)]
    public void ALossOutsideTheMeasuredClassStillRefuses(string record, string sub, int formVersion, bool deleted, bool zero)
    {
        var d = new SubrecordInventory.RecordDiff(new FormKey(ModKey.FromFileName(PluginName), 0x800), record, formVersion, deleted,
            new[] { new SubrecordInventory.Sub(sub, 4, 1, zero) }, Array.Empty<SubrecordInventory.Sub>());
        Assert.Single(SubrecordInventory.Losses(new() { d }, SubrecordInventory.Renames, SubrecordInventory.InformationFree));
    }

    // Lengths are compared: a subrecord kept at another length is a loss, which a signature count alone would pass.
    [Fact]
    public void ASubrecordWrittenBackAtAnotherLengthIsRefused()
    {
        var (path, arma) = StagePlugin();
        EditRecord(path, "ARMA", edit: subs =>
        {
            int i = subs.FindIndex(s => s.Sig == "DNAM");
            subs[i] = ("DNAM", Subrecord("DNAM", subs[i].Raw[6..].Concat(new byte[] { 1, 2, 3, 4 }).ToArray()));
            return subs;
        });
        var before = File.ReadAllBytes(path);
        var o = SetWeaponAdjust(path, arma);
        Assert.False(o.Success);
        Assert.Contains("DNAM at ", o.Error);
        Assert.Contains("(written at ", o.Error);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    static void AssertRefusedUntouched(bool success, string? error, string path, byte[] before)
    {
        Assert.False(success);
        Assert.Contains("MOD2, MO2T, MOD4, MO4T", error);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(path)!, ".housecarl-tmp")));
    }

    [Fact]
    public void AnInPlaceCreateIntoThatPluginIsRefusedWithTheFileUntouched()
    {
        var (path, _) = StageReproduction();
        var before = File.ReadAllBytes(path);
        using var r = LoadOrderResolver.Build(new[] { _w.MasterPath, path });
        var o = WritePatchBuilder.CreateRecordsInPlace(r, TestCorpus.Rulebook,
            new[] { new WritePatchBuilder.CreateSpec { RecordType = "Keyword", EditorId = "HcRT_New", Edits = Array.Empty<WriteRequest>() } },
            path, PluginName);
        AssertRefusedUntouched(o.Success, o.Error, path, before);
    }

    // A spec error comes before the round trip, as on apply.
    [Fact]
    public void AnInPlaceCreateWithABadSpecGetsTheSpecErrorFirst()
    {
        var (path, _) = StageReproduction();
        using var r = LoadOrderResolver.Build(new[] { _w.MasterPath, path });
        var o = WritePatchBuilder.CreateRecordsInPlace(r, TestCorpus.Rulebook,
            new[] { new WritePatchBuilder.CreateSpec { RecordType = "Keyword", EditorId = "", Edits = Array.Empty<WriteRequest>() } },
            path, PluginName);
        Assert.False(o.Success);
        Assert.Contains("an editorid is required", o.Error);
        Assert.DoesNotContain("MOD2", o.Error);
    }

    // into= re-serializes the whole existing patch, so it is checked like an in-place write.
    [Fact]
    public void ACreateIntoAPatchThatWouldLoseSubrecordsIsRefusedWithTheFileUntouched()
    {
        var (path, _) = StageReproduction();
        var before = File.ReadAllBytes(path);
        using var r = LoadOrderResolver.Build(new[] { _w.MasterPath, path });
        var o = WritePatchBuilder.CreateRecords(r, TestCorpus.Rulebook,
            new[] { new WritePatchBuilder.CreateSpec { RecordType = "Keyword", EditorId = "HcRT_New", Edits = Array.Empty<WriteRequest>() } },
            path, extend: true);
        AssertRefusedUntouched(o.Success, o.Error, path, before);
        Assert.Contains("drop into=", o.Error);
    }

    [Fact]
    public void AnApplyIntoAPatchThatWouldLoseSubrecordsIsRefusedWithTheFileUntouched()
    {
        var (path, arma) = StageReproduction();
        var before = File.ReadAllBytes(path);
        using var r = LoadOrderResolver.Build(new[] { _w.MasterPath, path });
        var o = WritePatchBuilder.Apply(r, TestCorpus.Rulebook,
            new[] { new WritePatchBuilder.PatchEdit { Target = arma, Path = new[] { "WeaponAdjust" }, Verb = "Set", Value = "1.5" } },
            path, extend: true);
        AssertRefusedUntouched(o.Success, o.Error, path, before);
    }

    [Fact]
    public void AForwardIntoAPatchThatWouldLoseSubrecordsIsRefusedWithTheFileUntouched()
    {
        var (path, _) = StageReproduction();
        var before = File.ReadAllBytes(path);
        using var r = LoadOrderResolver.Build(new[] { _w.MasterPath, path });
        var o = WritePatchBuilder.ForwardRecords(r,
            new[] { new WritePatchBuilder.ForwardSpec { Target = _w.Weapon, FromPlugin = W.MasterName } }, path, extend: true, "source=");
        AssertRefusedUntouched(o.Success, o.Error, path, before);
    }

    [Fact]
    public void ARemoveFromAPatchChecksTheRestAndLeavesTheRemovedRecordOut()
    {
        var (path, arma) = StageReproduction();
        FormKey keyword;
        using (var ov = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE)) keyword = ov.Keywords.Single().FormKey;
        var before = File.ReadAllBytes(path);
        using var r = LoadOrderResolver.Build(new[] { _w.MasterPath, path });
        var refused = WritePatchBuilder.RemoveRecords(r, new[] { keyword }, path);
        AssertRefusedUntouched(refused.Success, refused.Error, path, before);
        var o = WritePatchBuilder.RemoveRecords(r, new[] { arma }, path);
        Assert.True(o.Success, o.Error);
    }

    [Fact]
    public void AnInPlaceRemoveFromThatPluginIsRefusedWithTheFileUntouched()
    {
        var (path, _) = StageReproduction();
        FormKey keyword;
        using (var ov = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE)) keyword = ov.Keywords.Single().FormKey;
        var before = File.ReadAllBytes(path);
        using var r = LoadOrderResolver.Build(new[] { _w.MasterPath, path });
        var o = WritePatchBuilder.RemoveRecordsInPlace(r, new[] { keyword }, path, PluginName);
        AssertRefusedUntouched(o.Success, o.Error, path, before);
        Assert.Contains("fix that record in xEdit first, or remove it in the same call", o.Error);
        Assert.DoesNotContain("in_place=", o.Error);
    }

    // The removed record goes whole, so its own losses are not counted.
    [Fact]
    public void AnInPlaceRemoveOfTheLossyRecordItselfWrites()
    {
        var (path, arma) = StageReproduction();
        using var r = LoadOrderResolver.Build(new[] { _w.MasterPath, path });
        var o = WritePatchBuilder.RemoveRecordsInPlace(r, new[] { arma }, path, PluginName);
        Assert.True(o.Success, o.Error);
        using var ov = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
        Assert.Empty(ov.ArmorAddons);
        Assert.Single(ov.Keywords);
    }

    // The forwarded body replaces the old one whole, so the old one's losses are not counted.
    [Fact]
    public void AnInPlaceForwardOverTheLossyRecordWritesTheSourcesVersion()
    {
        var dir = _w.NewDir();
        var srcPath = Path.Combine(dir, "HcRtSource.esp");
        var src = new SkyrimMod(ModKey.FromFileName("HcRtSource.esp"), SkyrimRelease.SkyrimSE);
        var arma = AddArma(src);
        src.BeginWrite.ToPath(srcPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        var path = Path.Combine(dir, PluginName);
        using (var srcOv = SkyrimMod.CreateFromBinaryOverlay(srcPath, SkyrimRelease.SkyrimSE))
        {
            var mod = new SkyrimMod(ModKey.FromFileName(PluginName), SkyrimRelease.SkyrimSE);
            mod.ArmorAddons.GetOrAddAsOverride(srcOv.ArmorAddons.Single());
            mod.Keywords.AddNew().EditorID = "HcRT_Kw";
            mod.BeginWrite.ToPath(path).WithLoadOrder(new ISkyrimModGetter[] { srcOv }).Write();
        }
        ToIssueOrder(path);
        using var r = LoadOrderResolver.Build(new[] { _w.MasterPath, srcPath, path });
        var o = WritePatchBuilder.ForwardRecordsInPlace(r,
            new[] { new WritePatchBuilder.ForwardSpec { Target = arma, FromPlugin = "HcRtSource.esp" } },
            path, PluginName, "source=");
        Assert.True(o.Success, o.Error);
        Assert.True(HasSubrecord(path, "MOD2"));
        Assert.True(HasSubrecord(path, "MOD4"));
    }

    [Fact]
    public void AnInPlaceForwardIntoThatPluginIsRefusedWithTheFileUntouched()
    {
        var (path, _) = StageReproduction();
        var before = File.ReadAllBytes(path);
        using var r = LoadOrderResolver.Build(new[] { _w.MasterPath, path });
        var o = WritePatchBuilder.ForwardRecordsInPlace(r,
            new[] { new WritePatchBuilder.ForwardSpec { Target = _w.Weapon, FromPlugin = W.MasterName } },
            path, PluginName, "source=");
        AssertRefusedUntouched(o.Success, o.Error, path, before);
    }

    // A fault of the check itself is refused in the check's own words, never passed as clean or blamed on a serialize.
    [Fact]
    public void AFileTheCheckCannotReadIsRefusedInTheChecksOwnWords()
    {
        var (path, _) = StagePlugin();
        var parsed = SkyrimMod.CreateFromBinary(path, SkyrimRelease.SkyrimSE);
        File.Delete(path);
        var refusal = SubrecordInventory.RoundTripRefusal(parsed, path, Array.Empty<ISkyrimModGetter>(), SubrecordInventory.Remedy.RecordLane);
        Assert.NotNull(refusal);
        Assert.Contains("round-trip check could not run", refusal);
        Assert.DoesNotContain("serialize or commit", refusal);
    }

    [Fact]
    public void AFileThatDoesNotParseIsRefusedRatherThanPassed()
    {
        var path = Path.Combine(_w.NewDir(), PluginName);
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("TES4 not a plugin"));
        using var r = LoadOrderResolver.Build(new[] { _w.MasterPath });
        var refusal = WritePatchBuilder.RoundTripRefusalAt(r.Capture(), path, SubrecordInventory.Remedy.CompactTarget);
        Assert.NotNull(refusal);
        Assert.Contains("round-trip check could not run", refusal);
        Assert.Contains("does not parse", refusal);
    }

    // A target whose own record links a plugin the order lacks still gets the lane's own sentence, not a serialize one.
    [Fact]
    public void ATargetLinkingAnInactivePluginStillGetsTheNotActiveSentence()
    {
        var path = Path.Combine(_w.NewDir(), W.UserName);
        using (var mOv = SkyrimMod.CreateFromBinaryOverlay(_w.MasterPath, SkyrimRelease.SkyrimSE))
        using (var hOv = SkyrimMod.CreateFromBinaryOverlay(_w.HighPath, SkyrimRelease.SkyrimSE))
        {
            var u = new SkyrimMod(ModKey.FromFileName(W.UserName), SkyrimRelease.SkyrimSE);
            var uw = u.Weapons.GetOrAddAsOverride(mOv.Weapons.First(x => x.FormKey == _w.Weapon));
            uw.Keywords = new() { _w.HighKeyword };
            u.BeginWrite.ToPath(path).WithLoadOrder(new ISkyrimModGetter[] { mOv, hOv }).Write();
        }
        var before = File.ReadAllBytes(path);
        using var r = LoadOrderResolver.Build(new[] { _w.MasterPath, path });
        var o = WritePatchBuilder.ApplyInPlace(r, TestCorpus.Rulebook,
            new[] { new WritePatchBuilder.PatchEdit { Target = _w.Weapon, Path = new[] { "BasicStats", "Damage" }, Verb = "Set", Value = "7" } },
            path, W.UserName);
        Assert.False(o.Success);
        Assert.Contains("NOT active", o.Error, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(path));
    }
}

/// <summary>The same check on the compact and copy lanes, each test on its own MO2 instance.</summary>
[Trait("tier", "integration")]
public sealed class CompactRoundTripTests
{
    sealed class World : IDisposable
    {
        public string Root { get; }
        public string TargetPath { get; }
        public string ReferencerPath { get; }
        public const string TargetName = "HcRtCompactTarget.esp";
        public const string ReferencerName = "HcRtCompactRef.esp";
        public LoadOrderService Svc { get; }

        public World(bool lossyTarget)
        {
            Root = Path.Combine(Path.GetTempPath(), "hc-compact-roundtrip-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(Root, "game", "Data"));
            var inst = Path.Combine(Root, "inst");
            var modsDir = Path.Combine(inst, "mods");
            Directory.CreateDirectory(Path.Combine(modsDir, "TargetMod"));
            Directory.CreateDirectory(Path.Combine(modsDir, "RefMod"));

            var targetKey = ModKey.FromFileName(TargetName);
            var weaponKey = new FormKey(targetKey, 0x900);
            var target = new SkyrimMod(targetKey, SkyrimRelease.SkyrimSE);
            var keyword = new Keyword(new FormKey(targetKey, 0x901), SkyrimRelease.SkyrimSE) { EditorID = "HcRtKeyword" };
            target.Keywords.Add(keyword);
            target.Weapons.Add(new Weapon(weaponKey, SkyrimRelease.SkyrimSE)
                { EditorID = "HcRtWeapon", Keywords = new() { keyword.ToLink() } });
            if (lossyTarget) InPlaceRoundTripTests.AddArma(target);
            target.ModHeader.Stats.NextFormID = 0x902;
            TargetPath = Path.Combine(modsDir, "TargetMod", TargetName);
            target.BeginWrite.ToPath(TargetPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).NoNextFormIDProcessing().Write();
            if (lossyTarget) InPlaceRoundTripTests.ToIssueOrder(TargetPath);

            using (var targetOverlay = SkyrimMod.CreateFromBinaryOverlay(TargetPath, SkyrimRelease.SkyrimSE))
            {
                var refKey = ModKey.FromFileName(ReferencerName);
                var referencer = new SkyrimMod(refKey, SkyrimRelease.SkyrimSE);
                var list = new FormList(new FormKey(refKey, 0x900), SkyrimRelease.SkyrimSE) { EditorID = "HcRtList" };
                list.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(weaponKey));
                referencer.FormLists.Add(list);
                if (!lossyTarget) InPlaceRoundTripTests.AddArma(referencer);
                ReferencerPath = Path.Combine(modsDir, "RefMod", ReferencerName);
                referencer.BeginWrite.ToPath(ReferencerPath).WithLoadOrder(new[] { targetOverlay }).Write();
            }
            if (!lossyTarget) InPlaceRoundTripTests.ToIssueOrder(ReferencerPath);

            File.WriteAllText(Path.Combine(inst, "ModOrganizer.ini"),
                "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
                + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");
            var prof = Path.Combine(inst, "profiles", "Default");
            Directory.CreateDirectory(prof);
            File.WriteAllText(Path.Combine(prof, "loadorder.txt"), "# header\r\n" + TargetName + "\r\n" + ReferencerName + "\r\n");
            File.WriteAllText(Path.Combine(prof, "plugins.txt"), "*" + TargetName + "\r\n*" + ReferencerName + "\r\n");
            File.WriteAllText(Path.Combine(prof, "modlist.txt"), "# header\r\n+RefMod\r\n+TargetMod\r\n");

            Svc = LoadOrderService.WithInstance(inst, 0, new UserConfigStore(Path.Combine(Root, "user.json")));
            Svc.Stats();
        }

        public void Dispose()
        {
            Svc.Dispose();
            try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
        }
    }

    [Fact]
    public void AnInPlaceCompactOfAPluginThatWouldLoseSubrecordsIsRefusedBeforeAnythingIsWritten()
    {
        using var w = new World(lossyTarget: true);
        var target = File.ReadAllBytes(w.TargetPath);
        var o = w.Svc.CompactPlugin(World.TargetName, esl: true, inPlace: true, repointExternals: true, acknowledge: true);
        Assert.False(o.Success);
        Assert.Contains("MOD2, MO2T, MOD4, MO4T", o.Error);
        Assert.Contains(World.TargetName, o.Error);
        Assert.Equal(target, File.ReadAllBytes(w.TargetPath));
    }

    // The check runs once, on the acknowledged call; the consent prompt comes first and costs no round trip.
    [Fact]
    public void TheUnacknowledgedCompactAsksForConsentWithoutRunningTheCheck()
    {
        using var w = new World(lossyTarget: true);
        var o = w.Svc.CompactPlugin(World.TargetName, esl: true, inPlace: true, repointExternals: true, acknowledge: false);
        Assert.True(o.NeedsAcknowledge, o.Error);
        Assert.DoesNotContain("MOD2", o.Error);
    }

    // copy into= re-serializes the whole existing patch, so it is checked like the other into= lanes.
    [Fact]
    public void ACopyIntoAPatchThatWouldLoseSubrecordsIsRefusedWithTheFileUntouched()
    {
        using var w = new World(lossyTarget: false);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(w.ReferencerPath)!, "meta.ini"),
            HousecarlOwnerMeta.Section + "\r\ngenerated=true\r\nplugin=" + World.ReferencerName + "\r\n");
        var referencer = File.ReadAllBytes(w.ReferencerPath);
        var r = CopyTools.Copy(w.Svc, "000900:" + World.TargetName, null, new[] { "Keywords" }, null, null,
            "HcRtClone", null, World.ReferencerName);
        Assert.Contains("MOD2, MO2T, MOD4, MO4T", r);
        Assert.Equal(referencer, File.ReadAllBytes(w.ReferencerPath));
    }

    [Fact]
    public void ARepointOfAReferencerThatWouldLoseSubrecordsIsRefusedBeforeTheCompactIsWritten()
    {
        using var w = new World(lossyTarget: false);
        var target = File.ReadAllBytes(w.TargetPath);
        var referencer = File.ReadAllBytes(w.ReferencerPath);
        var o = w.Svc.CompactPlugin(World.TargetName, esl: true, inPlace: true, repointExternals: true, acknowledge: true);
        Assert.False(o.Success);
        Assert.Contains("MOD2, MO2T, MOD4, MO4T", o.Error);
        Assert.Contains(World.ReferencerName, o.Error);
        Assert.Equal(target, File.ReadAllBytes(w.TargetPath));
        Assert.Equal(referencer, File.ReadAllBytes(w.ReferencerPath));
    }
}
