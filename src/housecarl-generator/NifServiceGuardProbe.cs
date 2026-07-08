using System.Text;
using HousecarlCore;
using NiflySharp;
using NiflySharp.Blocks;

namespace HousecarlGenerator;

/// <summary>
/// NifService guard (NIF layer Wave 1). Proves <see cref="NifService.Inspect"/> — the byte-level mesh reader behind
/// housecarl_nif_inspect — decodes every N2-whitelist value correctly and fails LOUD on a bad file.
///
/// Self-contained arms (always run — a synthetic SE mesh AUTHORED at probe time via NiflySharp itself, so NO
/// third-party mesh ships in-repo; the spike's CreateAndSave_SE recipe, SPIKE_NIF_LAYER_2026-07-08 §7):
///   • parse — the authored mesh loads clean (no error, no unknown blocks).
///   • header — version 20.2.0.7, user 12 / stream 100, recognized as a Skyrim SE stream.
///   • census — the on-disk block type histogram matches what was authored (3 NiNode, 1 BSTriShape, 1 dismember, 1 alpha).
///   • node tree — pre-order depth + names + NiAVObject flags (root → child A → child B, depths 0/1/2).
///   • strings — the header string table carries the authored node/shape names.
///   • shape — name, NiAVObject flags (0x400000E), and scale (1.25) read exactly.
///   • partitions — BSDismember body parts decode to their SBP_* enum names with the right part flags (30 HEAD, 31 HAIR).
///   • alpha — the alpha property decodes: raw flags 0x12ED, blend + test on, threshold 128.
///   • refusal — empty bytes and non-NIF garbage each return a NAMED error (Q3), never a throw or a half-model.
///
/// Corpus smoke (existence-gated — a REAL facegen mesh, the spike §5 regression truths for the values the synthetic
/// fixture can't wire: texture-set paths + bone lists). Runs only when the file is present (arg 1, or env
/// HOUSECARL_NIF_SMOKE, or the workspace default); SKIPs cleanly otherwise, so CI stays green without the corpus.
///
/// Run: dotnet run --project src/housecarl-generator nif-service-guard ["&lt;a-facegen.nif&gt;"]
/// </summary>
internal static class NifServiceGuardProbe
{
    // The spike §5 ground-truth mesh ('A makeover for Lucien'). Overridable by arg/env so nothing machine-specific is baked in.
    const string DefaultSmoke = @"E:\Skyrim Modding\ARR 2.0\mods\A makeover for Lucien\meshes\actors\character\FaceGenData\FaceGeom\lucien.esp\00005900.nif";

    public static int RunGuard(string[] args)
    {
        Console.WriteLine("================================================================");
        Console.WriteLine(" nif-service guard — mesh value decode (housecarl_nif_inspect)");
        Console.WriteLine("================================================================");
        Console.WriteLine();
        int fail = 0;
        void Check(bool c, string label) { Console.WriteLine((c ? "  PASS  " : "  FAIL  ") + label); if (!c) fail++; }

        // ---- self-contained: author a synthetic SE mesh in-memory, inspect it, assert every value ----
        Console.WriteLine("--- synthetic SE mesh (authored at probe time — no third-party mesh in repo) ---");
        var bytes = BuildSyntheticSe();
        var outcome = NifService.Inspect(bytes);
        Check(outcome.Error is null && outcome.Inspect is not null, $"the authored mesh parses clean — {outcome.Error ?? "ok"}");

        if (outcome.Inspect is { } nif)
        {
            Check(nif.IsSkyrimSE && nif.UserVersion == 12 && nif.StreamVersion == 100,
                  $"header identity: SE stream, user 12 / stream 100 — got user {nif.UserVersion} / stream {nif.StreamVersion}, SE={nif.IsSkyrimSE}");
            Check(nif.VersionString.Contains("20.2.0.7"), $"version string is 20.2.0.7 — '{nif.VersionString}'");
            Check(!nif.HasUnknownBlocks && nif.UnknownBlockTypes.Count == 0, "no unknown blocks in an authored SE mesh");
            Check(nif.BlockCount == 6, $"block count 6 — {nif.BlockCount}");
            Check(CensusHas(nif, "NiNode", 3) && CensusHas(nif, "BSTriShape", 1)
                  && CensusHas(nif, "BSDismemberSkinInstance", 1) && CensusHas(nif, "NiAlphaProperty", 1),
                  $"block census matches what was authored — {string.Join(", ", nif.BlockTypes.Select(t => t.Type + " x" + t.Count))}");

            // node tree — pre-order depth + names + flags
            Check(nif.Nodes.Count == 3
                  && nif.Nodes[0] is { Name: "GuardRoot", Depth: 0 }
                  && nif.Nodes.Any(n => n is { Name: "GuardChildA", Depth: 1 })
                  && nif.Nodes.Any(n => n is { Name: "GuardChildB", Depth: 2 }),
                  $"node tree walks root→A→B at depths 0/1/2 — [{string.Join(", ", nif.Nodes.Select(n => n.Name + "@" + n.Depth))}]");
            Check(nif.Nodes.First(n => n.Name == "GuardChildA").Flags == 0x40000E, "a node's NiAVObject flags read exactly (0x40000E)");
            Check(nif.HeaderStrings.Contains("GuardRoot") && nif.HeaderStrings.Contains("GuardShape"),
                  $"the header string table carries the authored names — [{string.Join(", ", nif.HeaderStrings)}]");

            // shape — name / flags / scale / partitions / alpha
            var shape = nif.Shapes.FirstOrDefault(s => s.Name == "GuardShape");
            Check(shape is not null, $"the authored shape is found — {(shape is null ? "MISSING" : "'GuardShape'")}");
            if (shape is not null)
            {
                Check(shape.Flags == 0x400000E, $"shape NiAVObject flags 0x400000E — 0x{shape.Flags:X}");
                Check(Math.Abs(shape.Scale - 1.25f) < 1e-6f, $"shape scale 1.25 — {shape.Scale}");
                Check(shape.Partitions.Count == 2
                      && shape.Partitions[0] is { BodyPartId: 30, BodyPartName: "SBP_30_HEAD", PartFlags: 257 }
                      && shape.Partitions[1] is { BodyPartId: 31, BodyPartName: "SBP_31_HAIR", PartFlags: 257 },
                      $"BSDismember partitions decode to SBP_* names + flags — [{string.Join(", ", shape.Partitions.Select(p => p.BodyPartId + " " + p.BodyPartName + " f" + p.PartFlags))}]");
                Check(shape.Alpha is { Flags: 0x12ED, Blend: true, Test: true, Threshold: 128 },
                      $"alpha property decodes (0x12ED, blend+test, thr 128) — {(shape.Alpha is null ? "NONE" : $"0x{shape.Alpha.Flags:X4} blend={shape.Alpha.Blend} test={shape.Alpha.Test} thr={shape.Alpha.Threshold}")}");
            }
        }

        // ---- refusal arms (Q3): a bad file is a named error, never a throw or a half-model ----
        Console.WriteLine();
        Console.WriteLine("--- refusal: a bad file is surfaced, never a silent/partial answer ---");
        var empty = NifService.Inspect(Array.Empty<byte>());
        Check(empty.Inspect is null && empty.Error is not null, $"empty bytes → named error — {empty.Error ?? "(none!)"}");
        var garbage = NifService.Inspect(Encoding.ASCII.GetBytes("this is plainly not a NIF file, just ASCII text padding padding padding."));
        Check(garbage.Inspect is null && garbage.Error is not null, $"non-NIF garbage → named error, not a throw — {garbage.Error ?? "(none!)"}");

        // ---- corpus smoke (existence-gated): the spike §5 facegen truths — texture paths + bones on REAL data ----
        Console.WriteLine();
        Console.WriteLine("--- corpus smoke: spike §5 facegen regression truths (existence-gated) ---");
        var smoke = args.Length > 0 ? args[0] : (Environment.GetEnvironmentVariable("HOUSECARL_NIF_SMOKE") ?? DefaultSmoke);
        if (!File.Exists(smoke))
        {
            Console.WriteLine($"  SKIP  no facegen mesh at '{smoke}' (pass one as arg 1 or set HOUSECARL_NIF_SMOKE). The synthetic arms above are self-contained.");
        }
        else
        {
            var s = NifService.Inspect(File.ReadAllBytes(smoke));
            Check(s.Error is null && s.Inspect is not null, $"the facegen mesh parses clean — {s.Error ?? "ok"}");
            if (s.Inspect is { } fg)
            {
                Check(fg.IsSkyrimSE && !fg.HasUnknownBlocks, "facegen is an SE mesh with zero unknown blocks");
                var head = fg.Shapes.FirstOrDefault(x => x.Name == "LucienHead");
                var hair = fg.Shapes.FirstOrDefault(x => x.Name == "LucienHair");
                var hairline = fg.Shapes.FirstOrDefault(x => x.Name == "LucienHairLine");
                Check(head is not null && hair is not null && hairline is not null,
                      $"the §5 shapes are present — {string.Join(", ", fg.Shapes.Select(x => x.Name))}");
                if (head is not null)
                {
                    Check(head.Flags == 0x400000E, $"LucienHead flags 0x400000E — 0x{head.Flags:X}");
                    Check(head.Partitions.Any(p => p is { BodyPartId: 30, BodyPartName: "SBP_30_HEAD" }), "LucienHead has the HEAD partition (30)");
                    Check(head.Textures.Any(t => t.Slot == 6 && t.Path.Contains(@"facetint\lucien.esp\00005900.dds", StringComparison.OrdinalIgnoreCase)),
                          "LucienHead texture slot 6 is the facetint path (the RaceMenu tint case)");
                    Check(head.Bones.Contains("NPC Head [Head]") && head.Bones.Contains("NPC Spine2 [Spn2]"), "LucienHead bone list reads (NPC Head / NPC Spine2)");
                }
                if (hair is not null) Check(hair.Alpha is { Flags: 0x12ED, Blend: true }, $"LucienHair alpha 0x12ED blend=true — {(hair.Alpha is null ? "NONE" : $"0x{hair.Alpha.Flags:X4} blend={hair.Alpha.Blend}")}");
                if (hairline is not null) Check(hairline.Alpha is { Flags: 0x12EE, Blend: false, Test: true, Threshold: 180 },
                      $"LucienHairLine alpha 0x12EE blend=false test=true thr180 — {(hairline.Alpha is null ? "NONE" : $"0x{hairline.Alpha.Flags:X4} blend={hairline.Alpha.Blend} test={hairline.Alpha.Test} thr={hairline.Alpha.Threshold}")}");
            }
        }

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "================ ALL PASS ================" : $"================ {fail} CHECK(S) FAILED ================");
        return fail == 0 ? 0 : 1;
    }

    static bool CensusHas(NifInspect nif, string type, int count) => nif.BlockTypes.Any(t => t.Type == type && t.Count == count);

    /// <summary>Author a minimal but genuine Skyrim SE mesh in-memory (the spike's CreateAndSave_SE recipe): an SE
    /// header, a 3-level NiNode tree with names + flags, and one BSTriShape carrying a name/flags/scale, two BSDismember
    /// partitions, and an alpha property. Every block is REFERENCED (parented / ref'd) so NiflySharp's save-time
    /// unreferenced-block prune keeps them. Returns the saved bytes — the exact input shape housecarl_nif_inspect reads.</summary>
    static byte[] BuildSyntheticSe()
    {
        var ver = new NiVersion { FileVersion = NiVersion.ToFile("20.2.0.7"), UserVersion = 12, StreamVersion = 100 };
        var f = new NifFile();
        f.Create(ver, withRootNode: true);
        var root = f.GetRootNodes().First();
        root.Name = new NiStringRef("GuardRoot");
        root.Flags_ui = 0xE;

        var childA = new NiNode { Name = new NiStringRef("GuardChildA"), Flags_ui = 0x40000E };
        root.Children.AddBlockRef(f.AddBlock(childA));
        var childB = new NiNode { Name = new NiStringRef("GuardChildB"), Flags_ui = 0x408000E };
        childA.Children.AddBlockRef(f.AddBlock(childB));

        var shape = new BSTriShape { Name = new NiStringRef("GuardShape"), Flags_ui = 0x400000E, Scale = 1.25f };
        root.Children.AddBlockRef(f.AddBlock(shape));

        var alpha = new NiAlphaProperty { Threshold = 128 };
        alpha.Flags.Value = 0x12ED;
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
        shape.SkinInstanceRef = new NiBlockRef<NiObject>(f.AddBlock(skin));

        using var ms = new MemoryStream();
        if (f.Save(ms) != 0) throw new InvalidOperationException("nif-service-guard: authoring the synthetic SE mesh failed to save");
        return ms.ToArray();
    }
}
