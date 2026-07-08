using System.Text;
using HousecarlCore;
using NiflySharp;
using NiflySharp.Blocks;

namespace HousecarlGenerator;

/// <summary>
/// NifSet guard (NIF layer Wave 2). Proves <see cref="NifService.Set"/> — the byte-level mesh writer behind
/// housecarl_nif_set — applies each N2-whitelist op correctly, VERIFIES it with the two offset-immune gates, and
/// REFUSES loud on anything it can't safely do. Self-contained: every fixture is AUTHORED at probe time via NiflySharp
/// itself (the spike CreateAndSave_SE recipe), so no third-party mesh ships in-repo.
///
/// Write arms (each op applied end-to-end through Set(), then re-inspected — a silent no-op is caught by read-back):
///   • set_flags / set_scale — the value changes and every OTHER whitelist value is preserved.
///   • set_alpha — flags word + threshold change together, preserved elsewhere.
///   • set_partition — a BSDismember body-part id changes on the named shape.
///   • rename_shape (same length AND longer) — the header-string edit lands and the length-changing case does NOT
///     false-abort (the empirical reason gate 1 is a block-CONTENT diff, not a byte-position diff — 2026-07-08).
///   • rename_node — a node's header-string name changes.
///
/// Verification RED arms (prove the gates CATCH a bad write, not merely pass through — called directly):
///   • gate 1 (block-content) — an edit that also changed a NON-expected block is REFUSED; the same edit with that
///     block in the expected set passes. (Proves the collateral-change abort is real.)
///   • gate 2 (semantic read-back) — an op whose value did NOT take (a no-op) is REFUSED; the real edit passes.
///
/// Refusal arms (Q3 — a can't-do is a named refusal, nothing written):
///   • non-SE stream, target-not-found, ambiguous target, op-not-applicable (set_partition/set_alpha/set_path on a
///     shape lacking that property), out-of-range index/slot, empty ops/bytes.
///
/// Corpus smoke (existence-gated): set_path's success path on a REAL facegen mesh (slot 6 facetint swap) — the one op
/// whose synthetic fixture nifly's read-only TextureSetRef blocks authoring of; SKIPs cleanly without the corpus.
///
/// Run: dotnet run --project src/housecarl-generator nif-set-guard ["&lt;a-facegen.nif&gt;"]
/// </summary>
internal static class NifSetGuardProbe
{
    const string DefaultSmoke = @"E:\Skyrim Modding\ARR 2.0\mods\A makeover for Lucien\meshes\actors\character\FaceGenData\FaceGeom\lucien.esp\00005900.nif";

    public static int RunGuard(string[] args)
    {
        Console.WriteLine("================================================================");
        Console.WriteLine(" nif-set guard — whitelist writes + verification (housecarl_nif_set)");
        Console.WriteLine("================================================================");
        Console.WriteLine();
        int fail = 0;
        void Check(bool c, string label) { Console.WriteLine((c ? "  PASS  " : "  FAIL  ") + label); if (!c) fail++; }

        var seBytes = BuildSyntheticSe();

        // ---- write arms: each op end-to-end through Set(), then re-inspected ----
        Console.WriteLine("--- writes: each N2 op applies, verifies, and preserves everything else ---");

        // set_flags: shape flags change; scale / alpha / partitions untouched.
        {
            var o = NifService.Set(seBytes, new[] { new NifSetOp(NifSetOpKind.SetFlags, "GuardShape", Flags: 0x800000E) });
            Check(o.Error is null && o.WrittenBytes is not null, $"set_flags succeeds — {o.Error ?? "ok"}");
            var s = ShapeOf(o.WrittenBytes, "GuardShape");
            Check(s is { Flags: 0x800000E }, $"set_flags read-back is 0x800000E — 0x{s?.Flags:X}");
            Check(s is { Scale: 1.25f } && s.Alpha is { Flags: 0x12ED } && s.Partitions.Count == 2,
                  "set_flags preserved scale / alpha / partitions");
            Check(o.Report is { Ops.Count: 1 } && o.Report.HeaderChanged == false && o.Report.ChangedBlocks.Count == 1,
                  $"set_flags report: 1 op, header unchanged, 1 block — {(o.Report is null ? "no report" : $"hdr={o.Report.HeaderChanged} blocks={o.Report.ChangedBlocks.Count}")}");
        }

        // set_scale
        {
            var o = NifService.Set(seBytes, new[] { new NifSetOp(NifSetOpKind.SetScale, "GuardShape", Scale: 2.5f) });
            var s = ShapeOf(o.WrittenBytes, "GuardShape");
            Check(o.Error is null && s is not null && Math.Abs(s.Scale - 2.5f) < 1e-6f, $"set_scale read-back is 2.5 — {s?.Scale.ToString() ?? o.Error}");
            Check(s is { Flags: 0x400000E }, "set_scale preserved flags");
        }

        // set_alpha: flags word + threshold together
        {
            var o = NifService.Set(seBytes, new[] { new NifSetOp(NifSetOpKind.SetAlpha, "GuardShape", AlphaFlags: 0x00ED, AlphaThreshold: 200) });
            var s = ShapeOf(o.WrittenBytes, "GuardShape");
            Check(o.Error is null && s?.Alpha is { Flags: 0x00ED, Threshold: 200 },
                  $"set_alpha read-back 0x00ED/thr200 — {(s?.Alpha is null ? o.Error : $"0x{s.Alpha.Flags:X4}/thr{s.Alpha.Threshold}")}");
            Check(s is { Flags: 0x400000E, Scale: 1.25f }, "set_alpha preserved flags / scale");
        }

        // set_partition: body-part id 30 -> 32 on partition 0
        {
            var o = NifService.Set(seBytes, new[] { new NifSetOp(NifSetOpKind.SetPartition, "GuardShape", BodyPartId: 32, PartitionIndex: 0) });
            var s = ShapeOf(o.WrittenBytes, "GuardShape");
            Check(o.Error is null && s is not null && s.Partitions.Count == 2 && s.Partitions[0].BodyPartId == 32 && s.Partitions[1].BodyPartId == 31,
                  $"set_partition read-back [0]=32,[1]=31 — {(s is null ? o.Error : string.Join(",", s.Partitions.Select(p => p.BodyPartId)))}");
        }

        // rename_shape SAME length + LONGER (the length-changing case must NOT false-abort — the whole reason for the
        // block-content-diff refinement). Both preserve every other value.
        {
            var o1 = NifService.Set(seBytes, new[] { new NifSetOp(NifSetOpKind.RenameShape, "GuardShape", NewName: "GuardShapX") });
            Check(o1.Error is null && ShapeOf(o1.WrittenBytes, "GuardShapX") is not null, $"rename_shape (same length) — {o1.Error ?? "ok"}");
            Check(o1.Report is { HeaderChanged: true, ChangedBlocks.Count: 0 }, "rename touches the header string table, ZERO blocks");

            var o2 = NifService.Set(seBytes, new[] { new NifSetOp(NifSetOpKind.RenameShape, "GuardShape", NewName: "GuardShapeRenamedMuchLonger") });
            var s = ShapeOf(o2.WrittenBytes, "GuardShapeRenamedMuchLonger");
            Check(o2.Error is null && s is not null, $"rename_shape (LONGER) does not false-abort — {o2.Error ?? "ok"}");
            Check(s is { Flags: 0x400000E, Scale: 1.25f } && s.Alpha is { Flags: 0x12ED } && s.Partitions.Count == 2,
                  "a length-changing rename preserved flags / scale / alpha / partitions");
        }

        // rename_node
        {
            var o = NifService.Set(seBytes, new[] { new NifSetOp(NifSetOpKind.RenameNode, "GuardChildA", NewName: "RenamedChildA") });
            var back = NifService.Inspect(o.WrittenBytes ?? Array.Empty<byte>()).Inspect;
            Check(o.Error is null && back is not null && back.Nodes.Any(n => n.Name == "RenamedChildA") && back.Nodes.All(n => n.Name != "GuardChildA"),
                  $"rename_node GuardChildA -> RenamedChildA — {o.Error ?? "ok"}");
        }

        // multi-op in one call
        {
            var o = NifService.Set(seBytes, new[]
            {
                new NifSetOp(NifSetOpKind.SetFlags, "GuardShape", Flags: 0x800000E),
                new NifSetOp(NifSetOpKind.SetScale, "GuardShape", Scale: 3f),
            });
            var s = ShapeOf(o.WrittenBytes, "GuardShape");
            Check(o.Error is null && s is { Flags: 0x800000E } && Math.Abs(s!.Scale - 3f) < 1e-6f,
                  $"two ops in one call both land — {(s is null ? o.Error : $"0x{s.Flags:X}/{s.Scale}")}");
        }

        // ---- verification RED arms: the gates CATCH a bad write (called directly) ----
        Console.WriteLine();
        Console.WriteLine("--- verification: the two gates refuse a bad write, not merely pass ---");

        // gate 1 (block-content): an edit that also changed a NON-expected block is refused.
        {
            var (edited2, shapeIdx, childIdx) = TwoBlockEdit(seBytes);
            var refuse = NifService.VerifyBlockContent(seBytes, edited2, new HashSet<int> { shapeIdx }, expectHeader: false);
            Check(refuse is not null && refuse.Contains("should not have touched"),
                  $"gate 1 REFUSES a collateral (non-expected-block) change — {refuse ?? "(passed — BUG)"}");
            var pass = NifService.VerifyBlockContent(seBytes, edited2, new HashSet<int> { shapeIdx, childIdx }, expectHeader: false);
            Check(pass is null, $"gate 1 PASSES when both changed blocks are expected — {pass ?? "ok"}");
        }

        // gate 2 (semantic read-back): an op whose value did NOT take (a no-op) is refused.
        {
            var pre = NifService.Inspect(seBytes).Inspect!;
            // 'edited' == the original bytes (nothing applied), but we claim to have set flags to a NEW value.
            var noOp = NifService.VerifyReadBack(seBytes, pre, new[] { new NifSetOp(NifSetOpKind.SetFlags, "GuardShape", Flags: 0xABCDE) }, out _);
            Check(noOp is not null && noOp.Contains("did NOT take effect"),
                  $"gate 2 REFUSES a no-op write (read-back != requested) — {noOp ?? "(passed — BUG)"}");
            // and passes when the value really is present (flags already 0x400000E in the original)
            var real = NifService.VerifyReadBack(seBytes, pre, new[] { new NifSetOp(NifSetOpKind.SetFlags, "GuardShape", Flags: 0x400000E) }, out _);
            Check(real is null, $"gate 2 PASSES when read-back matches the request — {real ?? "ok"}");
        }

        // ---- refusal arms (Q3) ----
        Console.WriteLine();
        Console.WriteLine("--- refusals: a can't-do is named, nothing written ---");

        Check(NifService.Set(Array.Empty<byte>(), new[] { new NifSetOp(NifSetOpKind.SetFlags, "X", Flags: 1) }).Error is { } e0 && e0.Contains("empty"),
              "empty bytes → named refusal");
        Check(NifService.Set(seBytes, Array.Empty<NifSetOp>()).Error is not null, "no ops → named refusal");
        Check(NifService.Set(seBytes, new[] { new NifSetOp(NifSetOpKind.SetFlags, "NoSuchShape", Flags: 1) }).Error is { } e1 && e1.Contains("no shape or node named"),
              "target not found → named refusal");
        Check(NifService.Set(seBytes, new[] { new NifSetOp(NifSetOpKind.SetPartition, "BareShape", BodyPartId: 32) }).Error is { } e2 && e2.Contains("no BSDismember"),
              "set_partition on a shape with no skin → named refusal");
        Check(NifService.Set(seBytes, new[] { new NifSetOp(NifSetOpKind.SetAlpha, "BareShape", AlphaThreshold: 10) }).Error is { } e3 && e3.Contains("no alpha property"),
              "set_alpha on a shape with no alpha → named refusal");
        Check(NifService.Set(seBytes, new[] { new NifSetOp(NifSetOpKind.SetPath, "BareShape", TextureSlot: 0, Path: "x.dds") }).Error is { } e4 && e4.Contains("no shader texture set"),
              "set_path on a shape with no texture set → named refusal");
        Check(NifService.Set(seBytes, new[] { new NifSetOp(NifSetOpKind.SetPartition, "GuardShape", BodyPartId: 32, PartitionIndex: 9) }).Error is { } e5 && e5.Contains("out of range"),
              "partition_index out of range → named refusal");
        Check(NifService.Set(seBytes, new[] { new NifSetOp(NifSetOpKind.RenameShape, "GuardShape") }).Error is { } e6 && e6.Contains("new_name"),
              "rename with no new_name → named refusal");

        // ambiguous target — a mesh with two shapes both named 'Dup'
        {
            var dup = BuildDupNameSe();
            Check(NifService.Set(dup, new[] { new NifSetOp(NifSetOpKind.SetFlags, "Dup", Flags: 1) }).Error is { } ed && ed.Contains("ambiguous"),
                  "ambiguous shape name → named refusal (never a silent first-match write)");
        }

        // non-SE stream refusal
        {
            var le = TryBuildNonSe();
            if (le is null) Console.WriteLine("  SKIP  could not author a non-SE fixture on this NiflySharp build (SE gate still covered by the IsSkyrimSE read path).");
            else Check(NifService.Set(le, new[] { new NifSetOp(NifSetOpKind.SetFlags, "GuardShape", Flags: 1) }).Error is { } el && el.Contains("NOT a Skyrim SE"),
                       "non-SE stream → named refusal (no cross-game write)");
        }

        // ---- corpus smoke (existence-gated): set_path success on a REAL facegen mesh ----
        Console.WriteLine();
        Console.WriteLine("--- corpus smoke: set_path on a real facegen texture set (existence-gated) ---");
        var smoke = args.Length > 0 ? args[0] : (Environment.GetEnvironmentVariable("HOUSECARL_NIF_SMOKE") ?? DefaultSmoke);
        if (!File.Exists(smoke))
            Console.WriteLine($"  SKIP  no facegen mesh at '{smoke}' (pass one as arg 1 or set HOUSECARL_NIF_SMOKE). set_path shares the in-block machinery the CI arms above prove.");
        else
        {
            var fgBytes = File.ReadAllBytes(smoke);
            var fg = NifService.Inspect(fgBytes).Inspect;
            var head = fg?.Shapes.FirstOrDefault(x => x.Textures.Any(t => t.Slot == 6));
            if (head is null) Console.WriteLine("  SKIP  the smoke mesh has no slot-6 texture to swap.");
            else
            {
                const string newPath = @"textures\actors\character\facegendata\facetint\HOUSECARL_TEST\swapped.dds";
                var o = NifService.Set(fgBytes, new[] { new NifSetOp(NifSetOpKind.SetPath, head.Name, TextureSlot: 6, Path: newPath) });
                Check(o.Error is null && o.WrittenBytes is not null, $"set_path on a real facegen slot 6 succeeds — {o.Error ?? "ok"}");
                var back = ShapeOf(o.WrittenBytes, head.Name);
                Check(back is not null && back.Textures.Any(t => t.Slot == 6 && t.Path == newPath),
                      $"set_path read-back shows the new slot-6 path — {back?.Textures.FirstOrDefault(t => t.Slot == 6)?.Path ?? "(gone)"}");
                Check(back is not null && back.Flags == head.Flags && back.Partitions.Count == head.Partitions.Count,
                      "set_path on real data preserved the shape's flags / partitions");
            }
        }

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "================ ALL PASS ================" : $"================ {fail} CHECK(S) FAILED ================");
        return fail == 0 ? 0 : 1;
    }

    static NifShape? ShapeOf(byte[]? bytes, string name)
        => bytes is null ? null : NifService.Inspect(bytes).Inspect?.Shapes.FirstOrDefault(s => s.Name == name);

    /// <summary>Author a synthetic SE mesh: root → GuardChildA node; a full GuardShape (flags/scale/alpha/2 partitions);
    /// and a BareShape carrying nothing (for the not-applicable refusal arms). The spike CreateAndSave_SE recipe.</summary>
    static byte[] BuildSyntheticSe()
    {
        var ver = new NiVersion { FileVersion = NiVersion.ToFile("20.2.0.7"), UserVersion = 12, StreamVersion = 100 };
        var f = new NifFile();
        f.Create(ver, withRootNode: true);
        var root = f.GetRootNodes().First();
        root.Name = new NiStringRef("GuardRoot"); root.Flags_ui = 0xE;

        var childA = new NiNode { Name = new NiStringRef("GuardChildA"), Flags_ui = 0x40000E };
        root.Children.AddBlockRef(f.AddBlock(childA));

        var shape = new BSTriShape { Name = new NiStringRef("GuardShape"), Flags_ui = 0x400000E, Scale = 1.25f };
        root.Children.AddBlockRef(f.AddBlock(shape));
        var alpha = new NiAlphaProperty { Threshold = 128 }; alpha.Flags.Value = 0x12ED;
        shape.AlphaPropertyRef = new NiBlockRef<NiAlphaProperty>(f.AddBlock(alpha));
        var skin = new BSDismemberSkinInstance
        {
            Partitions = new List<NiflySharp.Structs.BodyPartList>
            {
                new() { BodyPart = (NiflySharp.Enums.BSDismemberBodyPartType)30, PartFlag = (NiflySharp.Enums.BSPartFlag)257 },
                new() { BodyPart = (NiflySharp.Enums.BSDismemberBodyPartType)31, PartFlag = (NiflySharp.Enums.BSPartFlag)257 },
            },
        };
        skin.NumPartitions = (uint)skin.Partitions.Count;
        shape.SkinInstanceRef = new NiBlockRef<NiflySharp.NiObject>(f.AddBlock(skin));

        var bare = new BSTriShape { Name = new NiStringRef("BareShape"), Flags_ui = 0x400000E, Scale = 1f };
        root.Children.AddBlockRef(f.AddBlock(bare));

        using var ms = new MemoryStream();
        if (f.Save(ms) != 0) throw new InvalidOperationException("nif-set-guard: authoring the synthetic SE mesh failed to save");
        return ms.ToArray();
    }

    /// <summary>A synthetic SE mesh with TWO shapes both named 'Dup' — for the ambiguous-target refusal arm.</summary>
    static byte[] BuildDupNameSe()
    {
        var ver = new NiVersion { FileVersion = NiVersion.ToFile("20.2.0.7"), UserVersion = 12, StreamVersion = 100 };
        var f = new NifFile();
        f.Create(ver, withRootNode: true);
        var root = f.GetRootNodes().First(); root.Name = new NiStringRef("Root"); root.Flags_ui = 0xE;
        root.Children.AddBlockRef(f.AddBlock(new BSTriShape { Name = new NiStringRef("Dup"), Flags_ui = 0x400000E, Scale = 1f }));
        root.Children.AddBlockRef(f.AddBlock(new BSTriShape { Name = new NiStringRef("Dup"), Flags_ui = 0x400000E, Scale = 1f }));
        using var ms = new MemoryStream();
        if (f.Save(ms) != 0) throw new InvalidOperationException("nif-set-guard: authoring the dup-name mesh failed");
        return ms.ToArray();
    }

    /// <summary>Try to author a NON-SE-stream fixture. Returns null if this NiflySharp build won't produce one that
    /// re-parses as non-SE (the arm SKIPs rather than fail — the SE gate is still exercised by the read path).</summary>
    static byte[]? TryBuildNonSe()
    {
        try
        {
            var ver = new NiVersion { FileVersion = NiVersion.ToFile("20.2.0.7"), UserVersion = 12, StreamVersion = 83 };
            var f = new NifFile();
            f.Create(ver, withRootNode: true);
            var root = f.GetRootNodes().First(); root.Name = new NiStringRef("GuardShape"); root.Flags_ui = 0xE;
            using var ms = new MemoryStream();
            if (f.Save(ms) != 0) return null;
            var bytes = ms.ToArray();
            var chk = NifService.Inspect(bytes).Inspect;
            return chk is { IsSkyrimSE: false } ? bytes : null;   // only usable if it truly reads back as non-SE
        }
        catch { return null; }
    }

    /// <summary>Author the synthetic mesh, load it, and change TWO blocks (the GuardShape's flags AND GuardChildA's
    /// flags), returning the edited bytes plus both block ids — the input the gate-1 RED arm feeds VerifyBlockContent.</summary>
    static (byte[] edited, int shapeIdx, int childIdx) TwoBlockEdit(byte[] seBytes)
    {
        var nif = new NifFile();
        using (var ms = new MemoryStream(seBytes)) nif.Load(ms);
        var shape = nif.GetShapes().First(s => s.Name?.String == "GuardShape");
        var child = nif.Blocks.OfType<NiNode>().First(n => n.Name?.String == "GuardChildA");
        ((NiAVObject)shape).Flags_ui = 0x800000E;
        child.Flags_ui = 0x123456;
        nif.GetBlockIndex(shape, out int shapeIdx);
        nif.GetBlockIndex(child, out int childIdx);
        using var outMs = new MemoryStream();
        nif.Save(outMs);
        return (outMs.ToArray(), shapeIdx, childIdx);
    }
}
