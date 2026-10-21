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

    /// <summary>Rewrite the first record of that signature (and EDID, if named), its body and header, with its size and every enclosing group's grown to fit.</summary>
    internal static void EditRecord(string path, string recordSig,
        Func<List<(string Sig, byte[] Raw)>, List<(string Sig, byte[] Raw)>>? edit = null, Action<byte[]>? header = null,
        string? edid = null)
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
            if (edid is not null && !subs.Any(s => s.Sig == "EDID" && Encoding.ASCII.GetString(s.Raw, 6, s.Raw.Length - 6).TrimEnd('\0') == edid))
                { p += 24 + size; continue; }
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

    /// <summary>A plugin with an LTEX carrying those flags, its header set to form version 35, below Mutagen's gate at 43.</summary>
    (string Path, FormKey Arma) StageOldLtex(LandscapeTexture.Flag flags)
    {
        var (path, arma) = StagePluginWith(m => m.LandscapeTextures.AddNew("HcRT_Ltex").Flags = flags);
        EditRecord(path, "LTEX", header: h => BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(20), 35));
        Assert.Contains(RecordSubrecords(File.ReadAllBytes(path), "LTEX", out _, out _, out _), s => s.Sig == "INAM");
        return (path, arma);
    }

    // Allowed: an all-zero LTEX INAM below form version 43, which Mutagen neither reads nor writes.
    [Fact]
    public void AZeroLtexInamBelowFormVersion43IsAnAllowedLoss()
    {
        var (path, arma) = StageOldLtex(0);
        var o = SetWeaponAdjust(path, arma);
        Assert.True(o.Success, o.Error);
    }

    // Refused: the same INAM with IsSnow set carries a flag the write would drop.
    [Fact]
    public void AnLtexInamWithIsSnowSetBelowFormVersion43Refuses()
    {
        var (path, arma) = StageOldLtex(LandscapeTexture.Flag.IsSnow);
        var o = SetWeaponAdjust(path, arma);
        Assert.False(o.Success);
        Assert.Contains("INAM", o.Error);
    }

    // A compressed record is walked through its decompressed body on both sides, and writes.
    [Fact]
    public void ACompressedRecordIsWalkedThroughItsBody()
    {
        var (path, arma) = StagePluginWith(m => m.ArmorAddons.First().IsCompressed = true);
        var file = SubrecordInventory.Walk(File.ReadAllBytes(path), ModKey.FromFileName(PluginName));
        Assert.Contains(file[arma].Subs.Keys, k => k.Sig == "MOD2");
        using (var ov = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE))
            Assert.True(ov.ArmorAddons.Single().IsCompressed);
        var o = SetWeaponAdjust(path, arma);
        Assert.True(o.Success, o.Error);
    }

    /// <summary>The partial forms the fixture stages, by signature and EDID: interior CELL, exterior CELL under a plain WRLD, WRLD, DIAL.</summary>
    static readonly (string Sig, string Edid)[] Partials =
        { ("CELL", "HcPF_Partial"), ("CELL", "HcPF_Exterior"), ("WRLD", "HcPF_PartialWorld"), ("DIAL", "HcPF_Topic") };

    /// <summary>The ARMA, a plain interior CELL and each partial form (record flag 0x4000), every one cut to its EDID (a DIAL keeps
    /// its TIFC) except, with <paramref name="cellData"/>, the interior one, which keeps what Mutagen wrote.</summary>
    (string Path, FormKey Arma) StagePartialForms(bool cellData = false)
    {
        var (path, arma) = StagePluginWith(m =>
        {
            Cell New(string edid, uint flags) =>
                new(m.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = edid, Flags = Cell.Flag.HasWater, MajorRecordFlagsRaw = (int)flags };
            var sub = new CellSubBlock { GroupType = GroupTypeEnum.InteriorCellSubBlock };
            sub.Cells.Add(New("HcPF_Partial", 0x4000));
            sub.Cells.Add(New("HcPF_Plain", 0));
            m.Cells.Records.Add(new CellBlock { GroupType = GroupTypeEnum.InteriorCellBlock, SubBlocks = { sub } });
            var world = new Worldspace(m.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcPF_World" };
            world.SubCells.Add(new WorldspaceBlock
            {
                GroupType = GroupTypeEnum.ExteriorCellBlock,
                Items = { new WorldspaceSubBlock { GroupType = GroupTypeEnum.ExteriorCellSubBlock, Items = { New("HcPF_Exterior", 0x4000) } } },
            });
            m.Worldspaces.Add(world);
            m.Worldspaces.Add(new Worldspace(m.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcPF_PartialWorld", MajorRecordFlagsRaw = 0x4000 });
            m.DialogTopics.Add(new DialogTopic(m.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcPF_Topic", MajorRecordFlagsRaw = 0x4000 });
        });
        foreach (var (sig, edid) in Partials)
            if (!(cellData && edid == "HcPF_Partial"))
                EditRecord(path, sig, subs => subs.Where(s => s.Sig is "EDID" or "TIFC").ToList(), edid: edid);
        return (path, arma);
    }

    /// <summary>The whole record, header and body, of the one with that signature and EDID.</summary>
    static byte[] RecordBytes(string path, string recordSig, string edid)
    {
        var b = File.ReadAllBytes(path);
        var (_, at, rec) = SubrecordInventory.Records(b, ModKey.FromFileName(Path.GetFileName(path))).Single(r =>
            r.Rec.RecordType.Type == recordSig &&
            SubrecordInventory.Contents(r.Rec).Any(s => s.Sig == "EDID" && Encoding.ASCII.GetString(s.Data.Span).TrimEnd('\0') == edid));
        return b[at..(at + (int)rec.TotalLength)];
    }

    static FormKey KeyOf(string path, string edid)
    {
        using var ov = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
        return ov.EnumerateMajorRecords().Single(r => r.EditorID == edid).FormKey;
    }

    static void AssertRefusedUntouched(WritePatchBuilder.PatchOutcome o, string path, byte[] before)
    {
        Assert.False(o.Success);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(path)!, ".housecarl-tmp")));
    }

    // #1209: Mutagen 0.54.4 adds its defaults to a partial form; the in-place write puts each back as the file held it.
    [Fact]
    public void AnInPlaceWriteLeavesEveryEdidOnlyPartialFormByteIdentical()
    {
        var (path, arma) = StagePartialForms();
        var before = Partials.Select(p => RecordBytes(path, p.Sig, p.Edid)).ToList();
        Assert.Contains("TIFC", Encoding.ASCII.GetString(before[3]));
        var plain = RecordBytes(path, "CELL", "HcPF_Plain");
        var o = SetWeaponAdjust(path, arma);
        Assert.True(o.Success, o.Error);
        Assert.Equal(before, Partials.Select(p => RecordBytes(path, p.Sig, p.Edid)).ToList());
        Assert.Equal(plain, RecordBytes(path, "CELL", "HcPF_Plain"));
        // Every group size still holds: the written file opens with all of them.
        using var back = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
        Assert.Equal(new[] { "HcPF_Exterior", "HcPF_Partial", "HcPF_PartialWorld", "HcPF_Plain", "HcPF_Topic", "HcPF_World" },
            back.EnumerateMajorRecords().Select(r => r.EditorID).Where(e => e?.StartsWith("HcPF_") == true).Order());
    }

    // #1209: a partial CELL an earlier Mutagen save padded with DATA and LTMP goes back as read, padding included.
    [Fact]
    public void AnInPlaceWriteLeavesAPaddedPartialCellByteIdentical()
    {
        var (path, arma) = StagePartialForms(cellData: true);
        var before = RecordBytes(path, "CELL", "HcPF_Partial");
        Assert.Contains("DATA", Encoding.ASCII.GetString(before));
        var o = SetWeaponAdjust(path, arma);
        Assert.True(o.Success, o.Error);
        Assert.Equal(before, RecordBytes(path, "CELL", "HcPF_Partial"));
    }

    // #1209: an edit to a partial form cannot be written as one, so the write refuses.
    [Fact]
    public void AnInPlaceEditOfAPartialFormIsRefusedWithTheFileUntouched()
    {
        var (path, _) = StagePartialForms();
        var cell = KeyOf(path, "HcPF_Exterior");
        var before = File.ReadAllBytes(path);
        WritePatchBuilder.PatchOutcome o;
        using (var r = LoadOrderResolver.Build(new[] { _w.MasterPath, path }))
            o = WritePatchBuilder.ApplyInPlace(r, TestCorpus.Rulebook,
                new[] { new WritePatchBuilder.PatchEdit { Target = cell, Path = new[] { "EditorID" }, Verb = "Set", Value = "HcPF_Renamed" } },
                path, PluginName);
        AssertRefusedUntouched(o, path, before);
        Assert.StartsWith($"refused: CELL 'HcPF_Exterior' {FormIdToken.Of(cell)} in '{PluginName}' is a partial form", o.Error);
        Assert.Contains("and this call edits it", o.Error);
    }

    WritePatchBuilder.PatchOutcome SetFlags(string path, FormKey target, int flags)
    {
        using var r = LoadOrderResolver.Build(new[] { _w.MasterPath, path });
        return WritePatchBuilder.ApplyInPlace(r, TestCorpus.Rulebook,
            new[] { new WritePatchBuilder.PatchEdit { Target = target, Path = new[] { "MajorRecordFlagsRaw" }, Verb = "Set", Value = flags.ToString() } },
            path, PluginName);
    }

    // #1209 r1 #11: setting the flag on a record is refused as an edit, not as a record houseCARL cannot find.
    [Fact]
    public void AnInPlaceApplySettingThePartialFlagIsRefusedAsAnEdit()
    {
        var (path, _) = StagePartialForms();
        var plain = KeyOf(path, "HcPF_Plain");
        var before = File.ReadAllBytes(path);
        var o = SetFlags(path, plain, 0x4000);
        AssertRefusedUntouched(o, path, before);
        Assert.StartsWith($"refused: CELL {FormIdToken.Of(plain)} in '{PluginName}' is a partial form", o.Error);
        Assert.Contains("and this call edits it", o.Error);
        Assert.Contains("clear 0x4000 in its MajorRecordFlagsRaw in this same call", o.Error);
    }

    // #1209 r1 #40: the edit remedy's same-call route works: clearing the flag writes the record as a plain one.
    [Fact]
    public void AnInPlaceApplyClearingThePartialFlagWritesTheRecord()
    {
        var (path, _) = StagePartialForms();
        var cell = KeyOf(path, "HcPF_Exterior");
        var o = SetFlags(path, cell, 0);
        Assert.True(o.Success, o.Error);
        using var back = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
        Assert.Equal(0, back.EnumerateMajorRecords().Single(r => r.FormKey == cell).MajorRecordFlagsRaw & 0x4000);
    }

    // #1209: an into= write goes through WritePatch (#1246), which this fix leaves as it was: no new refusal.
    [Fact]
    public void AnApplyIntoAPatchHoldingAPartialFormIsWrittenAsBefore()
    {
        var (path, arma) = StagePartialForms(cellData: true);
        using var r = LoadOrderResolver.Build(new[] { _w.MasterPath, path });
        var o = WritePatchBuilder.Apply(r, TestCorpus.Rulebook,
            new[] { new WritePatchBuilder.PatchEdit { Target = arma, Path = new[] { "WeaponAdjust" }, Verb = "Set", Value = "1.5" } },
            path, extend: true);
        Assert.True(o.Success, o.Error);
        Assert.Contains("DATA", Encoding.ASCII.GetString(RecordBytes(path, "CELL", "HcPF_Partial")));
    }

    /// <summary>A plugin in <paramref name="dir"/> holding one partial override of <paramref name="owner"/>'s interior
    /// <paramref name="cell"/>, its body cut to what <paramref name="keep"/> leaves (default: the EDID), with whatever <paramref name="fill"/> adds.</summary>
    static string PartialOverride(string dir, string name, SkyrimMod owner, Cell cell,
        Func<List<(string Sig, byte[] Raw)>, List<(string Sig, byte[] Raw)>>? keep = null, Action<SkyrimMod, Cell>? fill = null)
    {
        var donor = new SkyrimMod(ModKey.FromFileName(name), SkyrimRelease.SkyrimSE);
        var over = cell.DeepCopy();
        over.MajorRecordFlagsRaw = 0x4000;
        fill?.Invoke(donor, over);
        donor.Cells.Records.Add(new CellBlock
        {
            GroupType = GroupTypeEnum.InteriorCellBlock, BlockNumber = owner.Cells.Records[0].BlockNumber,
            SubBlocks = { new CellSubBlock { GroupType = GroupTypeEnum.InteriorCellSubBlock, BlockNumber = owner.Cells.Records[0].SubBlocks[0].BlockNumber, Cells = { over } } },
        });
        var path = Path.Combine(dir, name);
        donor.BeginWrite.ToPath(path).WithLoadOrder(new ISkyrimModGetter[] { owner }).Write();
        EditRecord(path, "CELL", keep ?? (subs => subs.Where(s => s.Sig == "EDID").ToList()));
        return path;
    }

    // #1209: a merge writes a donor's partial override back as the donor held it.
    [Fact]
    public void AMergeWritesADonorsPartialFormBackByteIdentical()
    {
        var dir = _w.NewDir();
        var master = new SkyrimMod(ModKey.FromFileName("HcPF_Master.esm"), SkyrimRelease.SkyrimSE);
        var cell = WriteEngine.AddInteriorCell(master, "HcPF_MasterCell");
        var masterPath = Path.Combine(dir, "HcPF_Master.esm");
        master.BeginWrite.ToPath(masterPath).WithNoLoadOrder().Write();
        var donorKey = ModKey.FromFileName("HcPF_Donor.esp");
        var donorPath = PartialOverride(dir, "HcPF_Donor.esp", master, cell);
        var held = RecordBytes(donorPath, "CELL", "HcPF_MasterCell");
        var outKey = ModKey.FromFileName("HcPF_Merged.esp");
        var outPath = Path.Combine(dir, outKey.FileName.String);
        var m = WritePatchBuilder.MergeBuild(new[] { ("HcPF_Donor.esp", donorPath, donorKey) }, outKey, new Dictionary<FormKey, FormKey>(),
            new[] { "HcPF_Master.esm" }, n => n == "HcPF_Master.esm" ? masterPath : null, outPath, dataDir: null, isFaction: _ => false);
        Assert.True(m.Success, m.Error);
        Assert.Equal(held, RecordBytes(outPath, "CELL", "HcPF_MasterCell"));
    }

    // #1209 r1 #4: a forward in place drags in the source plugin's partial parent CELL, which is written back from that plugin.
    [Fact]
    public void AnInPlaceForwardOfARefWhosePartialParentItDragsInWritesThatParentBackFromItsSource()
    {
        var dir = _w.NewDir();
        var master = new SkyrimMod(ModKey.FromFileName("HcPF_Master.esm"), SkyrimRelease.SkyrimSE);
        var cell = WriteEngine.AddInteriorCell(master, "HcPF_MasterCell");
        var stat = master.Statics.AddNew("HcPF_Static");
        var masterPath = Path.Combine(dir, "HcPF_Master.esm");
        master.BeginWrite.ToPath(masterPath).WithNoLoadOrder().Write();
        var refKey = default(FormKey);
        var srcPath = PartialOverride(dir, "HcPF_Src.esp", master, cell, fill: (m, c) =>
        {
            var placed = new PlacedObject(m.GetNextFormKey(), SkyrimRelease.SkyrimSE) { Base = stat.ToNullableLink(), Placement = new Placement() };
            c.Temporary.Add(placed);
            refKey = placed.FormKey;
        });
        var targetPath = Path.Combine(dir, PluginName);
        var target = new SkyrimMod(ModKey.FromFileName(PluginName), SkyrimRelease.SkyrimSE);
        target.Keywords.AddNew().EditorID = "HcRT_Kw";
        target.BeginWrite.ToPath(targetPath).WithNoLoadOrder().Write();
        var held = RecordBytes(srcPath, "CELL", "HcPF_MasterCell");
        using var r = LoadOrderResolver.Build(new[] { masterPath, srcPath, targetPath });
        var o = WritePatchBuilder.ForwardRecordsInPlace(r,
            new[] { new WritePatchBuilder.ForwardSpec { Target = refKey, FromPlugin = "HcPF_Src.esp" } }, targetPath, PluginName, "source=");
        Assert.True(o.Success, o.Error);
        Assert.Equal(held, RecordBytes(targetPath, "CELL", "HcPF_MasterCell"));
    }

    // #1209 r1 #1: an addon's partial override of the base's own CELL is keyed through the merge's key map, so it is found.
    [Fact]
    public void AMergeOfABaseAndAnAddonWritesTheAddonsPartialOverrideOfTheBaseBack()
    {
        var dir = _w.NewDir();
        var baseKey = ModKey.FromFileName("HcPF_Base.esp");
        var baseMod = new SkyrimMod(baseKey, SkyrimRelease.SkyrimSE);
        var cell = WriteEngine.AddInteriorCell(baseMod, "HcPF_BaseCell");
        var basePath = Path.Combine(dir, baseKey.FileName.String);
        baseMod.BeginWrite.ToPath(basePath).WithNoLoadOrder().Write();
        var addonPath = PartialOverride(dir, "HcPF_Addon.esp", baseMod, cell);
        var held = RecordBytes(addonPath, "CELL", "HcPF_BaseCell");
        var outKey = ModKey.FromFileName("HcPF_Merged.esp");
        var outPath = Path.Combine(dir, outKey.FileName.String);
        var m = WritePatchBuilder.MergeBuild(
            new[] { ("HcPF_Base.esp", basePath, baseKey), ("HcPF_Addon.esp", addonPath, ModKey.FromFileName("HcPF_Addon.esp")) },
            outKey, new Dictionary<FormKey, FormKey> { [cell.FormKey] = new FormKey(outKey, cell.FormKey.ID) },
            Array.Empty<string>(), _ => null, outPath, dataDir: null, isFaction: _ => false);
        Assert.True(m.Success, m.Error);
        Assert.Equal(held, RecordBytes(outPath, "CELL", "HcPF_BaseCell"));
    }

    // A subrecord over 64 KB, carried by XXXX, counts at its real length and XXXX itself is not counted.
    [Fact]
    public void AnXxxxCarriedSubrecordCountsAtItsRealLength()
    {
        // Mutagen 0.54.4 will not write a 70,000-byte DESC, so the body is built by hand: XXXX, then DESC with a zero length.
        var xxxx = Subrecord("XXXX", BitConverter.GetBytes(70000));
        var desc = Subrecord("DESC", Array.Empty<byte>()).Concat(Enumerable.Repeat((byte)'a', 70000)).ToArray();
        var e = new SubrecordInventory.RecordEntry { Signature = "BOOK" };
        e.AddBody(new Noggog.ReadOnlyMemorySlice<byte>(Subrecord("EDID", Encoding.ASCII.GetBytes("HcRT\0")).Concat(xxxx).Concat(desc).ToArray()));
        Assert.Contains(e.Subs.Keys, k => k.Sig == "DESC" && k.Length == 70000);
        Assert.Contains(e.Subs.Keys, k => k.Sig == "EDID" && k.Length == 5);
        Assert.DoesNotContain(e.Subs.Keys, k => k.Sig == "XXXX");
    }

    // A trailing piece shorter than a subrecord header, or a payload cut short, faults the walk rather than being dropped.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ATruncatedTrailingSubrecordIsNotSilentlyDropped(bool shortHeader)
    {
        var e = new SubrecordInventory.RecordEntry { Signature = "KYWD" };
        var edid = Subrecord("EDID", Encoding.ASCII.GetBytes("HcRT\0"));
        var body = shortHeader ? edid.Concat(edid[..3]).ToArray() : edid[..8];
        Assert.ThrowsAny<Exception>(() => e.AddBody(new Noggog.ReadOnlyMemorySlice<byte>(body)));
    }

    /// <summary>One record's walk from raw PHWT payloads, as the file or the written side would hold them.</summary>
    static Dictionary<FormKey, SubrecordInventory.RecordEntry> RaceWith(FormKey key, params byte[][] phwt)
    {
        var e = new SubrecordInventory.RecordEntry { Signature = "RACE", FormVersion = 44 };
        e.AddBody(new Noggog.ReadOnlyMemorySlice<byte>(phwt.SelectMany(p => Subrecord("PHWT", p)).ToArray()));
        return new() { [key] = e };
    }

    static byte[] Floats(params float[] v) => v.SelectMany(BitConverter.GetBytes).ToArray();

    // Allowed: a PHWT written back as its own leading bytes, the dropped tail all zero (Requiem pads 8 floats to 16).
    [Fact]
    public void APhwtWhoseDroppedTailIsAllZeroIsAnAllowedLoss()
    {
        var key = new FormKey(ModKey.FromFileName(PluginName), 0x800);
        var kept = Floats(1, 0.5f, 0, 0, 0.25f, 0, 0, 1);
        var diffs = SubrecordInventory.Diff(RaceWith(key, kept.Concat(new byte[32]).ToArray(), kept.Concat(new byte[32]).ToArray()),
                                            RaceWith(key, kept, kept));
        Assert.Single(diffs);
        Assert.Empty(SubrecordInventory.Losses(diffs, SubrecordInventory.Renames, SubrecordInventory.InformationFree));
    }

    // Refused: the same cut when the tail holds a value, or when the kept bytes differ from the file's.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void APhwtCutThatLosesAValueStillRefuses(bool nonZeroTail)
    {
        var key = new FormKey(ModKey.FromFileName(PluginName), 0x800);
        var kept = Floats(1, 0.5f, 0, 0, 0.25f, 0, 0, 1);
        var file = kept.Concat(nonZeroTail ? Floats(0, 0, 0, 0.75f, 0, 0, 0, 0) : new byte[32]).ToArray();
        var written = nonZeroTail ? kept : Floats(1, 0.5f, 0, 0, 0.25f, 0, 0, 0);
        var diffs = SubrecordInventory.Diff(RaceWith(key, file), RaceWith(key, written));
        Assert.Single(SubrecordInventory.Losses(diffs, SubrecordInventory.Renames, SubrecordInventory.InformationFree));
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
    [InlineData("LTEX", "INAM", 35, false, false)]
    [InlineData("RACE", "PHWT", 44, false, true)]
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
