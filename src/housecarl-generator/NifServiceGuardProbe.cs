using System.Text;
using HousecarlCore;
using HousecarlMcp;
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
///   • shader (#272) — the shader block, its game layout, the BSLightingShaderType enum, and the SLSF1/SLSF2 words
///     decoded to the LIBRARY's own bit names (in bit order); the emissive colour round-trips as RGB.
///   • slot names (#272) — texture slots carry the SEMANTIC name the shader's type+flags determine (Soft_Lighting
///     ⇒ slot 2 is SoftLighting; model-space normals with no backlight ⇒ slot 7 is Specular) — AND the paired Q3
///     arm: a slot nothing determines (slot 4, nothing env-maps) stays UNNAMED rather than getting a plausible
///     wrong label. An index-only implementation passes the first and fails the second.
///   • unnamed bits (#272/#255) — pinned on a synthetic gapped enum, since every flag word nifly names today covers
///     all 32 bits: the residual is surfaced as a hex mask, and a multi-bit combo member peels before its parts.
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
            Check(nif.BlockCount == 8, $"block count 8 — {nif.BlockCount}");
            Check(CensusHas(nif, "NiNode", 3) && CensusHas(nif, "BSTriShape", 1)
                  && CensusHas(nif, "BSDismemberSkinInstance", 1) && CensusHas(nif, "NiAlphaProperty", 1)
                  && CensusHas(nif, "BSLightingShaderProperty", 1) && CensusHas(nif, "BSShaderTextureSet", 1),
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
                Check(shape.BlockType == "BSTriShape" && shape.FlagsDefault == 0x8000E && shape.FlagsDefaultType == "BSTriShape",
                      $"shape flag default resolved from nif.xml (BSTriShape → 0x8000E) — {shape.BlockType}/{(shape.FlagsDefault is { } fd ? "0x" + fd.ToString("X") : "none")}");

                // ---- shader property (#272) ----
                var sh = shape.Shader;
                Check(sh is { BlockType: "BSLightingShaderProperty", GameType: "SK", ShaderType: "SkinTint" },
                      $"shader block + game layout + TYPE enum read — {(sh is null ? "NO SHADER" : $"{sh.BlockType}/{sh.GameType}/{sh.ShaderType ?? "(none)"}")}");
                if (sh is not null)
                {
                    // The flag names come from nifly's own enum, so this pins the DECODE, not a transcription:
                    // 0x1|0x2|0x1000 = Specular|Skinned|Model_Space_Normals, reported in bit order.
                    Check(sh.Flags1 is { Label: "SLSF1", Raw: 0x1003, UnknownBits: 0 }
                          && sh.Flags1.Names.SequenceEqual(new[] { "Specular", "Skinned", "Model_Space_Normals" }),
                          $"SLSF1 decodes to its named bits, in bit order — {(sh.Flags1 is null ? "NONE" : $"0x{sh.Flags1.Raw:X} [{string.Join(", ", sh.Flags1.Names)}]")}");
                    Check(sh.Flags2 is { Label: "SLSF2", Raw: 0x2000011, UnknownBits: 0 }
                          && sh.Flags2.Names.SequenceEqual(new[] { "ZBuffer_Write", "Double_Sided", "Soft_Lighting" }),
                          $"SLSF2 decodes to its named bits — {(sh.Flags2 is null ? "NONE" : $"0x{sh.Flags2.Raw:X} [{string.Join(", ", sh.Flags2.Names)}]")}");
                    // RGB, not RGBA — a lighting shader's emissive is a Color3 on disk, so there is no alpha to
                    // round-trip and none is reported (the interface's synthetic 4th component is deliberately dropped).
                    // Matched through the NULLABLE, not dereferenced: if a library bump ever drops the EmissiveColor
                    // override, ReallyReads correctly returns false and this must print a FAIL — not throw an NRE and
                    // take the whole probe with it. Goes quiet, doesn't go wrong (review of PR #286).
                    Check(sh.EmissiveColor is { } e
                          && Math.Abs(e.R - 0.25f) < 1e-6f && Math.Abs(e.G - 0.5f) < 1e-6f && Math.Abs(e.B - 0.75f) < 1e-6f,
                          "emissive colour round-trips exactly — "
                          + (sh.EmissiveColor is { } ec ? $"rgb({ec.R},{ec.G},{ec.B})" : "UNREAD (the override is gone?)"));
                    // THE FORMER UPSTREAM-GAP ARM, FLIPPED (#287). Through NiflySharp 1.0.0 these five fell through to
                    // INiShader's default-interface STUB, which returns a constant, so the arm asserted houseCARL
                    // reported NOTHING for them. 1.1.0 implements all five on BSLightingShaderProperty, so the arm now
                    // asserts the REAL round-tripped values — and it is the same tripwire read the other way: each was
                    // authored to a value DISTINCT from the stub constant it replaced (0, or 1 for Alpha), so a
                    // regression to the stub fails here loudly instead of printing a confident wrong number.
                    Check(sh.Glossiness is { } gl && Math.Abs(gl - 30f) < 1e-6f
                          && sh.SpecularStrength is { } sst && Math.Abs(sst - 1.5f) < 1e-6f
                          && sh.EmissiveMultiple is { } em && Math.Abs(em - 2.5f) < 1e-6f
                          && sh.Alpha is { } al && Math.Abs(al - 0.5f) < 1e-6f
                          && sh.SpecularColor is { } spc
                          && Math.Abs(spc.R - 1f) < 1e-6f && Math.Abs(spc.G - 0.5f) < 1e-6f && Math.Abs(spc.B - 0.25f) < 1e-6f,
                          $"every lighting value round-trips its authored value, none of them the old stub constant — "
                          + $"glossiness={Fmt(sh.Glossiness)} specularStrength={Fmt(sh.SpecularStrength)} "
                          + $"specularColor={(sh.SpecularColor is { } c ? $"rgb({c.R},{c.G},{c.B})" : "UNREAD (the override is gone?)")} "
                          + $"emissiveMultiple={Fmt(sh.EmissiveMultiple)} alpha={Fmt(sh.Alpha)}");
                }

                // Slot names are DERIVED from that type+flags, not from the index. Soft_Lighting (and NOT Glow_Map)
                // makes slot 2 SoftLighting — the exact finding the reporter needed pynifly for; Model_Space_Normals
                // with no backlight makes slot 7 Specular.
                string? Slot(int n) => shape.Textures.FirstOrDefault(t => t.Slot == n)?.SlotName;
                Check(Slot(0) == "Diffuse" && Slot(1) == "Normal" && Slot(2) == "SoftLighting" && Slot(7) == "Specular",
                      $"texture slots carry their SEMANTIC names, derived from shader type + flags — [{string.Join(", ", shape.Textures.Select(t => $"tex[{t.Slot}]{(t.SlotName is null ? "" : " (" + t.SlotName + ")")}"))}]");
                // The Q3 arm: nothing env-maps this shader, so slot 4 is genuinely undetermined. An implementation that
                // named it from the index alone ("Environment") would pass every arm above and fail only this one.
                Check(shape.Textures.Any(t => t.Slot == 4) && Slot(4) is null,
                      $"an UNDETERMINED slot stays unnamed rather than getting a confident wrong label — tex[4] name = {Slot(4) ?? "(none)"}");
            }
            var childA = nif.Nodes.FirstOrDefault(n => n.Name == "GuardChildA");
            Check(childA is { BlockType: "NiNode", FlagsDefault: 0xE, FlagsDefaultType: "NiNode" },
                  $"node flag default resolved from nif.xml (NiNode → 0xE) — {(childA is null ? "MISSING" : childA.BlockType + "/" + (childA.FlagsDefault is { } nd ? "0x" + nd.ToString("X") : "none"))}");
        }

        // ---- transcription lock: pin distinctive nif.xml SSE flag-defaults so a future edit can't quietly corrupt one ----
        // (the table is a hand-transcription from nif.xml; the resolver arms above only exercise BSTriShape/NiNode, so
        // the other entries ride on this direct check — review hardening note.)
        var defs = NifService.AvFlagsSseDefaults;
        Check(defs["BSTriShape"] == 0x8000E && defs["NiNode"] == 0xE && defs["BSLeafAnimNode"] == 0x808000E
              && defs["BSMeshLODTriShape"] == 0x100E && defs["BSOrderedNode"] == 0x8200E && defs["BSLODTriShape"] == 0x800000E
              && defs["BSTreeNode"] == 0x8080E && defs["BSBlastNode"] == 0x8000F && defs["BSMultiBoundNode"] == 0xE,
              "nif.xml SSE flag-default transcription intact (distinctive entries pinned against drift)");

        // ---- refusal arms (Q3): a bad file is a named error, never a throw or a half-model ----
        Console.WriteLine();
        Console.WriteLine("--- refusal: a bad file is surfaced, never a silent/partial answer ---");
        var empty = NifService.Inspect(Array.Empty<byte>());
        Check(empty.Inspect is null && empty.Error is not null, $"empty bytes → named error — {empty.Error ?? "(none!)"}");
        var garbage = NifService.Inspect(Encoding.ASCII.GetBytes("this is plainly not a NIF file, just ASCII text padding padding padding."));
        Check(garbage.Inspect is null && garbage.Error is not null, $"non-NIF garbage → named error, not a throw — {garbage.Error ?? "(none!)"}");

        // ---- render layer (NifWire.Render via synthetic NifInspectData — the AssetStatusProbe G-layer pattern) ----
        Console.WriteLine();
        Console.WriteLine("--- render: alarms-first, error/provider chain, filtered omitted-count, unknown-empty edge ---");
        var summaryOnly = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var ok = NifWire.Render(FakeData(FakeInspect(3, 1, false, Array.Empty<string>()), null), summaryOnly, Array.Empty<string>(), 80_000);
        Check(ok.Contains("read from: ModA") && ok.Contains("20.2.0.7") && ok.Contains("[Skyrim SE]") && ok.Contains("Shape0"),
              "success render carries winner + version + SE flag + shape names");

        var abs = NifWire.Render(FakeData(null, "ABSENT — no active mod or BSA provides this mesh path.",
                                          bsaFailures: new[] { "Bad.bsa (loaded by P.esp): truncated" }), summaryOnly, Array.Empty<string>(), 80_000);
        Check(abs.Contains("could NOT be read")
              && abs.IndexOf("could NOT be read", StringComparison.Ordinal) < abs.IndexOf("ABSENT", StringComparison.Ordinal),
              "the read-failure alarm renders BEFORE the ABSENT error (a Q3 alarm can't be buried)");
        Check(abs.Contains("ModA (loose) > Base.bsa (BSA)"), "the error path still shows the winner→loser provider chain");

        var amb = NifWire.Render(FakeData(FakeInspect(1, 0, false, Array.Empty<string>()), null, ambiguous: true), summaryOnly, Array.Empty<string>(), 80_000);
        Check(amb.Contains("more than one source"), "ambiguity is surfaced as a note");

        // finding #2: a cut INSIDE a filtered section counts the filtered remainder (≤2 of 4 shapes), never the total.
        // 4 shapes, 2 with (30) partitions each; a small cap cuts after the first partition line → "1 more omitted",
        // never "3"/"4 more omitted" (the pre-fix total-based count).
        var wantParts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "partitions" };
        var cut = NifWire.Render(FakeData(FakeInspect(4, 2, false, Array.Empty<string>(), partsPerShape: 30), null), wantParts, Array.Empty<string>(), 400);
        Check(cut.Contains("more omitted") && !cut.Contains("3 more omitted") && !cut.Contains("4 more omitted"),
              $"a filtered-section cut counts the FILTERED remainder, not the total shape count — {(cut.Contains("more omitted") ? "cut fired, remainder bounded" : "NO CUT (retune)")}");

        // PR #243 review: RenderShapesDetail obeys the same rule — a mid-list cut counts the REMAINING shapes, never
        // the total. 6 shapes at cap 600: the cut fires after ≥1 shape rendered, so "6 more omitted" is the bug.
        var wantShapesCut = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "shapes" };
        var cutShapes = NifWire.Render(FakeData(FakeInspect(6, 0, false, Array.Empty<string>()), null), wantShapesCut, Array.Empty<string>(), 600);
        Check(cutShapes.Contains("more omitted") && !cutShapes.Contains("6 more omitted"),
              $"a shapes-detail cut counts the REMAINDER, not the total shape count — {(cutShapes.Contains("more omitted") ? "cut fired, remainder bounded" : "NO CUT (retune)")}");

        // finding #3: HasUnknownBlocks true but no named types → 'present', never a bare '0 type(s) —'.
        var ue = NifWire.Render(FakeData(FakeInspect(1, 0, true, Array.Empty<string>()), null), summaryOnly, Array.Empty<string>(), 80_000);
        Check(ue.Contains("unknown blocks: present") && !ue.Contains("0 type(s)"), "HasUnknownBlocks with no named types renders 'present', not '0 type(s)'");

        var unkSec = NifWire.Render(FakeData(FakeInspect(1, 0, false, Array.Empty<string>()), null), summaryOnly, new[] { "bogus" }, 80_000);
        Check(unkSec.Contains("unrecognized section"), "an unrecognized sections= token is surfaced, never silently ignored");

        // ---- NON-SKYRIM LAYOUT: the slot interpreter DECLINES rather than fabricating (review of PR #286) ----
        // The same fixture at stream 130 parses as the FO4 layout. Its shader carries an all-zero F4SPF word, yet
        // nifly's HasSoftlight / HasBacklight / Parallax helpers all answer TRUE there — they return true for a layout
        // where the concept isn't modelled, not false. So an ungated interpreter labels slots 2/3/7 from nothing the
        // mesh carries, and it rides sections=paths and sections=shapes too. This is the arm that catches that; every
        // other slot arm in this probe is SK and stays green through the bug.
        Console.WriteLine();
        Console.WriteLine("--- non-Skyrim layout: slot naming declines (it models a SKYRIM convention) ---");
        var fo4 = NifService.Inspect(BuildSyntheticSe(streamVersion: 130));
        Check(fo4.Error is null && fo4.Inspect is not null, $"the FO4-layout mesh parses clean — {fo4.Error ?? "ok"}");
        var fo4Shape = fo4.Inspect?.Shapes.FirstOrDefault(s => s.Name == "GuardShape");
        Check(fo4Shape?.Shader is { GameType: "FO4" },
              $"the fixture really is read as the FO4 layout — {fo4Shape?.Shader?.GameType ?? "(no shader)"}");
        Check(fo4Shape is not null && fo4Shape.Textures.Count > 0 && fo4Shape.Textures.All(t => t.SlotName is null),
              "NOT-SKYRIM — every slot is UNNAMED on a non-SK layout, including the ones nifly's helpers answer "
              + $"'true' for — [{string.Join(", ", fo4Shape?.Textures.Select(t => $"tex[{t.Slot}]{(t.SlotName is null ? "" : " (" + t.SlotName + ")")}") ?? Array.Empty<string>())}]");

        // The decline must reach the WIRE, not just the data model: a bare tex[2] on an SK mesh means "this shader
        // doesn't determine slot 2", and on an FO4 mesh it means "we don't model this layout" — byte-identical output,
        // opposite meanings. The header's [NOT an SE stream] marker can't disambiguate them either, since an LE mesh
        // trips it yet still parses as SK and DOES get named slots (re-review of PR #286).
        var fo4Sections = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "shader", "paths", "shapes" };
        var fo4Render = NifWire.Render(FakeData(fo4.Inspect, null), fo4Sections, Array.Empty<string>(), 80_000);
        Check(fo4Render.Contains("NOT DERIVED for this block") && fo4Render.Contains("unmodelled, not undetermined")
              && System.Text.RegularExpressions.Regex.Matches(fo4Render, "NOT DERIVED").Count >= 3,
              "NOT-SKYRIM-RENDER — the decline is STATED in the shader section and caveated on both slot-listing sections");
        var skRender = NifWire.Render(FakeData(outcome.Inspect, null), fo4Sections, Array.Empty<string>(), 80_000);
        Check(!skRender.Contains("NOT DERIVED"),
              "NOT-SKYRIM-RENDER — an ordinary Skyrim mesh carries none of that noise");

        // ---- shader section + named slots reach the RENDER, not just the data model (#272) ----
        var wantShader = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "shader", "paths" };
        var shRender = NifWire.Render(FakeData(outcome.Inspect, null), wantShader, Array.Empty<string>(), 80_000);
        Check(shRender.Contains("--- shader") && shRender.Contains("BSLightingShaderProperty")
              && shRender.Contains("type SkinTint") && shRender.Contains("[SK layout]")
              && shRender.Contains("SLSF2 0x02000011") && shRender.Contains("Soft_Lighting")
              && shRender.Contains("emissive rgb(0.25,0.5,0.75)"),
              "the shader section renders: block, TYPE, layout, decoded flag names, emissive values");
        // The index is KEPT alongside the name (nif_set's texture_slot= takes the index), and an undetermined slot
        // renders bare — the render half of the Q3 arm above.
        // The read half of #287: on 1.1.0 every lighting value reaches the WIRE with its authored number, and the
        // unread-caveat line is ABSENT because there is nothing left unread on this block. Both halves are asserted —
        // a render that printed the values AND kept claiming they were unread would be a Q3 failure of its own.
        Check(shRender.Contains("glossiness 30") && shRender.Contains("specular 1.5 rgb(1,0.5,0.25)")
              && shRender.Contains("alpha 0.5") && shRender.Contains("emissive rgb(0.25,0.5,0.75) x2.5")
              && !shRender.Contains("NOT READ by this NiflySharp version"),
              "every lighting value reaches the render with its real number, and no unread caveat is left over");
        Check(shRender.Contains("tex[2] (SoftLighting): ") && shRender.Contains("tex[7] (Specular): ")
              && shRender.Contains("tex[4]: ") && !shRender.Contains("tex[4] ("),
              "named slots render as 'tex[N] (Name)' and an undetermined slot stays bare 'tex[N]'");

        // ---- THE DECLINE ARM (#287): a block this library STILL stubs reports nothing, and says so ----
        // The arm above proves ReallyReads says YES where upstream implements. This proves it says NO where upstream
        // does not — on 1.1.0 a BSEffectShaderProperty answers all six lighting values from the interface stub. Delete
        // the check, or make it answer true unconditionally, and THIS is the arm that fails; every other one stays
        // green while houseCARL starts printing "glossiness 0, alpha 1" on every effect-shader mesh in the load order.
        Console.WriteLine();
        Console.WriteLine("--- a value this library STILL stubs is reported as unread, never as its constant ---");
        var eff = NifService.Inspect(BuildSyntheticEffectShader());
        Check(eff.Error is null && eff.Inspect is not null, $"the effect-shader mesh parses clean — {eff.Error ?? "ok"}");
        var effSh = eff.Inspect?.Shapes.FirstOrDefault(s => s.Name == "EffectShape")?.Shader;
        Check(effSh is { BlockType: "BSEffectShaderProperty", GameType: "SK", ShaderType: null },
              $"the fixture really is an SK-layout effect shader, and it reports NO shader type — "
              + $"{(effSh is null ? "(no shader)" : $"{effSh.BlockType}/{effSh.GameType}/{effSh.ShaderType ?? "(none)"}")}");
        if (effSh is not null)
        {
            Check(effSh.EmissiveColor is null && effSh.EmissiveMultiple is null && effSh.Glossiness is null
                  && effSh.SpecularStrength is null && effSh.SpecularColor is null && effSh.Alpha is null,
                  "every lighting value this block only STUBS is reported as unread — "
                  + $"emissive={(effSh.EmissiveColor is null ? "unread" : "REPORTED")} "
                  + $"emissiveMultiple={Fmt(effSh.EmissiveMultiple)} glossiness={Fmt(effSh.Glossiness)} "
                  + $"specularStrength={Fmt(effSh.SpecularStrength)} "
                  + $"specularColor={(effSh.SpecularColor is null ? "unread" : "REPORTED")} alpha={Fmt(effSh.Alpha)}");
            // And the gap is STATED on the wire, not merely absent from it — the caller has to be able to tell
            // "we can't see it" from "the mesh says 0". The negative half pins that no stub constant leaked through.
            var effRender = NifWire.Render(FakeData(eff.Inspect, null), wantShader, Array.Empty<string>(), 80_000);
            Check(effRender.Contains("NOT READ by this NiflySharp version")
                  && effRender.Contains("glossiness") && effRender.Contains("alpha")
                  && !effRender.Contains("glossiness 0") && !effRender.Contains("alpha 1"),
                  "the unread values are NAMED on the wire, and none of them printed as its constant");
        }

        // ---- the unnamed-bit path (#255's posture, carried to the shader words) ----
        // Every flag word nifly names today (SLSF1/2, F4SPF1/2, BSShaderFlags1/2) covers all 32 bits, so a real mesh
        // can never produce a residual — the decode is pinned here on a synthetic enum WITH gaps instead, so the
        // behaviour is proven rather than assumed if an upstream enum ever loses a member. It also pins the
        // combo-before-constituent peel: Combo (0x9) must win over Alpha (0x1), which is then left uncounted.
        var gap = NifService.DecodeFlagWord("TEST", (GappedFlags)0x1D);
        Check(gap.Raw == 0x1D && gap.UnknownBits == 0x10 && gap.Names.SequenceEqual(new[] { "Beta", "Combo" }),
              $"an unnamed bit is surfaced as a residual mask, and a combo member peels before its constituent bits — 0x{gap.Raw:X} [{string.Join(", ", gap.Names)}] +0x{gap.UnknownBits:X}");
        var gapRender = NifWire.Render(FakeData(FakeShaderInspect(gap), null), wantShader, Array.Empty<string>(), 80_000);
        Check(gapRender.Contains("(+unknown bits 0x10)"),
              "the residual reaches the rendered flag line — an unnamed bit is stated, never silently dropped");

        // flag decode: a shape at 0x400000E vs the BSTriShape default 0x8000E → 0x80000 clear (missing), +0x4000000 extra.
        var shapesSec = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "shapes" };
        var decoded = NifWire.Render(FakeData(FakeInspect(1, 0, false, Array.Empty<string>()), null), shapesSec, Array.Empty<string>(), 80_000);
        Check(decoded.Contains("0x80000 clear") && decoded.Contains("vs BSTriShape default 0x8000E") && decoded.Contains("+0x4000000") && decoded.Contains("-0x80000"),
              "NiAVObject flags decode by deviation from the nif.xml default + the 0x80000 bit (no invented bit-names)");

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
                    // #272 end-to-end on REAL data: the synthetic fixture proves the decode, this proves it survives a
                    // mesh nobody authored for the test. Asserts only what is true of ANY SE facegen head (a lighting
                    // shader read as the SK layout, with both flag words present) and PRINTS the derived type + slot
                    // names, so a wrong derivation is visible to a human even where it isn't assertable.
                    Check(head.Shader is { BlockType: "BSLightingShaderProperty", GameType: "SK" }
                          && head.Shader.Flags1 is not null && head.Shader.Flags2 is not null,
                          "LucienHead's shader reads on real data — " + (head.Shader is null ? "NO SHADER"
                              : $"{head.Shader.BlockType}/{head.Shader.GameType} type {head.Shader.ShaderType ?? "(none)"} "
                                + $"SLSF1[{string.Join("|", head.Shader.Flags1?.Names ?? Array.Empty<string>())}] "
                                + $"SLSF2[{string.Join("|", head.Shader.Flags2?.Names ?? Array.Empty<string>())}] "
                                + $"slots: {string.Join(", ", head.Textures.Select(t => $"{t.Slot}{(t.SlotName is null ? "" : "=" + t.SlotName)}"))}"));
                }
                if (hair is not null) Check(hair.Alpha is { Flags: 0x12ED, Blend: true }, $"LucienHair alpha 0x12ED blend=true — {(hair.Alpha is null ? "NONE" : $"0x{hair.Alpha.Flags:X4} blend={hair.Alpha.Blend}")}");
                if (hairline is not null) Check(hairline.Alpha is { Flags: 0x12EE, Blend: false, Test: true, Threshold: 180 },
                      $"LucienHairLine alpha 0x12EE blend=false test=true thr180 — {(hairline.Alpha is null ? "NONE" : $"0x{hairline.Alpha.Flags:X4} blend={hairline.Alpha.Blend} test={hairline.Alpha.Test} thr={hairline.Alpha.Threshold}")}");
                // flag-default inheritance on LIVE data: facegen shapes are BSDynamicTriShape (no own nif.xml default) →
                // the resolver must walk to the BSTriShape parent's 0x8000E. Proves the inheritance walk on a real mesh.
                var dyn = fg.Shapes.FirstOrDefault(x => x.BlockType == "BSDynamicTriShape");
                if (dyn is not null)
                    Check(dyn.FlagsDefault == 0x8000E && dyn.FlagsDefaultType == "BSTriShape",
                          $"a real BSDynamicTriShape inherits the BSTriShape flag default 0x8000E — 0x{(dyn.FlagsDefault is { } d ? d.ToString("X") : "none")} from {dyn.FlagsDefaultType ?? "(none)"}");
            }
        }

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "================ ALL PASS ================" : $"================ {fail} CHECK(S) FAILED ================");
        return fail == 0 ? 0 : 1;
    }

    /// <summary>A nullable float for a probe DETAIL string — "unread" is the answer being asserted, so it has to be
    /// visible in the output rather than rendering as an empty string that reads like a zero.</summary>
    static string Fmt(float? f) => f is { } v ? v.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unread";

    static bool CensusHas(NifInspect nif, string type, int count) => nif.BlockTypes.Any(t => t.Type == type && t.Count == count);

    /// <summary>A flags enum with DELIBERATE GAPS (bits 0x2, 0x10 … are unnamed) and one multi-bit COMBO member — the
    /// shape no real nifly shader word has, since every one of those names all 32 bits. Exists solely so the residual
    /// and combo-peel behaviour of <see cref="NifService.DecodeFlagWord"/> is proven rather than assumed.</summary>
    enum GappedFlags : uint { Alpha = 0x1, Beta = 0x4, Combo = 0x9 }

    /// <summary>A one-shape inspect model carrying <paramref name="word"/> as its shader's first flag word — the
    /// render-layer input for the unnamed-bit arm.</summary>
    static NifInspect FakeShaderInspect(NifShaderFlagWord word)
    {
        var shader = new NifShader("BSLightingShaderProperty", "SK", "Default", word, null,
                                   new NifColor(0f, 0f, 0f), 1f, 30f, 1f, new NifColor(1f, 1f, 1f), 1f);
        var shape = new NifShape("GapShape", 0xE, 1f, "BSTriShape", 0x8000E, "BSTriShape",
                                 new List<NifPartition>(), null, new List<NifTexture>(), new List<string>(), shader);
        return new NifInspect("20.2.0.7", 12, 100, true, 2,
            new List<NifBlockTypeCount> { new("BSTriShape", 1), new("NiNode", 1) },
            false, Array.Empty<string>(),
            new List<NifShape> { shape }, new List<NifNode> { new(0, "Root", 0xE, "NiNode", 0xE, "NiNode") },
            new List<string> { "Root", "GapShape" });
    }

    /// <summary>A synthetic inspect model for the render-layer arms (no file needed): <paramref name="totalShapes"/>
    /// shapes named <paramref name="namePrefix"/>0.., the first <paramref name="withPartitions"/> of them carrying
    /// <paramref name="partsPerShape"/> partitions. Internal: NifInspectBatchGuardProbe reuses this builder (one
    /// synthetic-model shape for both render guards, PR #243 review) rather than growing a divergent copy.</summary>
    internal static NifInspect FakeInspect(int totalShapes, int withPartitions, bool hasUnknown, IReadOnlyList<string> unknownTypes, int partsPerShape = 2, string namePrefix = "Shape")
    {
        var shapes = new List<NifShape>();
        for (int i = 0; i < totalShapes; i++)
        {
            var parts = new List<NifPartition>();
            if (i < withPartitions)
                for (int p = 0; p < partsPerShape; p++)
                    parts.Add(new NifPartition(30 + p, "SBP_" + (30 + p) + "_PART", 257));
            shapes.Add(new NifShape(namePrefix + i, 0x400000E, 1f, "BSTriShape", 0x8000E, "BSTriShape", parts, null, new List<NifTexture>(), new List<string>()));
        }
        return new NifInspect("20.2.0.7", 12, 100, true, totalShapes + 1,
            new List<NifBlockTypeCount> { new("BSTriShape", totalShapes), new("NiNode", 1) },
            hasUnknown, unknownTypes,
            shapes, new List<NifNode> { new(0, "Root", 0xE, "NiNode", 0xE, "NiNode") }, new List<string> { "Root", "Shape0" });
    }

    /// <summary>Wrap an inspect model (or an error) in the service-layer batch (a batch of ONE — #229 made the wire
    /// batch-shaped) with a two-provider winner→loser chain — the input to <see cref="NifWire.Render"/>. The batch
    /// contracts themselves (order, per-path isolation, one-shot alarms, omitted-mesh cut) are NifInspectBatchGuardProbe's.</summary>
    static NifInspectBatchData FakeData(NifInspect? inspect, string? error, bool ambiguous = false, IReadOnlyList<string>? bsaFailures = null)
    {
        var provs = new List<NifProvider> { new("ModA", "loose"), new("Base.bsa", "BSA") };
        var one = new NifInspectData("meshes\\test.nif", inspect is null ? null : provs[0], provs, ambiguous, false, inspect, error);
        return new NifInspectBatchData(new[] { one }, bsaFailures ?? Array.Empty<string>(), Array.Empty<string>(), "Default");
    }

    /// <summary>Author a minimal SE mesh whose one shape carries a <c>BSEffectShaderProperty</c> — the block NiflySharp
    /// 1.1.0 still answers EVERY lighting value on from <see cref="INiShader"/>'s default-interface STUB. It is the
    /// subject of the DECLINE arm, and it exists as its own fixture rather than as a second shape on the main one so
    /// the census / block-count arms there stay pinned to what they were written for.
    ///
    /// Why it must exist at all: #287 flipped the main fixture's arm from "these five are reported as unread" to "these
    /// five carry their real values", because 1.1.0 implements them on BSLightingShaderProperty. That left every arm in
    /// this probe exercising only <c>ReallyReads</c>'s YES branch — so a change that deleted the check, or made it
    /// answer true unconditionally, would go green here while making an effect shader report "glossiness 0, alpha 1" on
    /// every such mesh in the world. That is the exact silent-wrong-answer #272 was built to prevent, so the NO branch
    /// gets its own subject rather than riding on whatever upstream happens not to have implemented this month.</summary>
    static byte[] BuildSyntheticEffectShader()
    {
        var ver = new NiVersion { FileVersion = NiVersion.ToFile("20.2.0.7"), UserVersion = 12, StreamVersion = 100 };
        var f = new NifFile();
        f.Create(ver, withRootNode: true);
        var root = f.GetRootNodes().First();
        root.Name = new NiStringRef("EffectRoot");
        root.Flags_ui = 0xE;

        var shape = new BSTriShape { Name = new NiStringRef("EffectShape"), Flags_ui = 0xE, Scale = 1f };
        root.Children.AddBlockRef(f.AddBlock(shape));

        // Authored on the SK layout so the block is read the same way the main fixture's is — the difference under
        // test is the BLOCK TYPE, not the layout (that decline is the NOT-SKYRIM arm's job, and conflating the two
        // would let either one pass for the wrong reason).
        var shader = new BSEffectShaderProperty
        {
            Type = NiflySharp.Helpers.ShaderHelper.ShaderGameType.SK,
            ShaderFlags_SSPF1 = NiflySharp.Enums.SkyrimShaderPropertyFlags1.Specular,
            ShaderFlags_SSPF2 = NiflySharp.Enums.SkyrimShaderPropertyFlags2.ZBuffer_Write,
            SourceTexture = new NiString4(@"textures\guard\effect.dds", false),
        };
        shape.ShaderPropertyRef = new NiBlockRef<BSShaderProperty>(f.AddBlock(shader));

        using var ms = new MemoryStream();
        if (f.Save(ms) != 0) throw new InvalidOperationException("nif-service-guard: authoring the synthetic effect-shader mesh failed to save");
        return ms.ToArray();
    }

    /// <summary>Author a minimal but genuine Skyrim SE mesh in-memory (the spike's CreateAndSave_SE recipe): an SE
    /// header, a 3-level NiNode tree with names + flags, and one BSTriShape carrying a name/flags/scale, two BSDismember
    /// partitions, and an alpha property. Every block is REFERENCED (parented / ref'd) so NiflySharp's save-time
    /// unreferenced-block prune keeps them. Returns the saved bytes — the exact input shape housecarl_nif_inspect reads.</summary>
    static byte[] BuildSyntheticSe(uint streamVersion = 100)
    {
        var ver = new NiVersion { FileVersion = NiVersion.ToFile("20.2.0.7"), UserVersion = 12, StreamVersion = streamVersion };
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

        // A real shader property + texture set (#272). The values are chosen to exercise every SLOT-NAME arm at once:
        // Soft_Lighting (not Glow_Map) makes slot 2 SoftLighting — the reporter's wolf finding; Model_Space_Normals
        // (with no backlight) makes slot 7 Specular; nothing env-maps, so slot 4 must stay UNNAMED.
        var shader = new BSLightingShaderProperty
        {
            // Mark the block as the SKYRIM layout up front: nifly's shader accessors DISPATCH on Type, and a
            // block constructed in memory (rather than parsed) starts at None, where the SK-only scalars neither
            // read nor serialize. Without this the fixture would author a shader full of zeros.
            Type = streamVersion == 100 ? NiflySharp.Helpers.ShaderHelper.ShaderGameType.SK
                                        : NiflySharp.Helpers.ShaderHelper.ShaderGameType.FO4,
            ShaderType_SK_FO4 = NiflySharp.Enums.BSLightingShaderType.SkinTint,
            ShaderFlags_SSPF1 = NiflySharp.Enums.SkyrimShaderPropertyFlags1.Specular
                              | NiflySharp.Enums.SkyrimShaderPropertyFlags1.Skinned
                              | NiflySharp.Enums.SkyrimShaderPropertyFlags1.Model_Space_Normals,
            ShaderFlags_SSPF2 = NiflySharp.Enums.SkyrimShaderPropertyFlags2.ZBuffer_Write
                              | NiflySharp.Enums.SkyrimShaderPropertyFlags2.Double_Sided
                              | NiflySharp.Enums.SkyrimShaderPropertyFlags2.Soft_Lighting,
            EmissiveColor = new NiflySharp.Structs.Color4 { R = 0.25f, G = 0.5f, B = 0.75f, A = 1f },
            // Each lighting scalar gets a DISTINCT non-zero value, so no arm below can pass on a coincidence with the
            // constant (0, or 1 for Alpha) the interface stub used to hand back. Settable as plain properties since
            // NiflySharp 1.1.0 (#287); before that the fixture had to reach the private backing fields.
            EmissiveMultiple = 2.5f,
            Glossiness = 30f,
            SpecularStrength = 1.5f,
            Alpha = 0.5f,
            SpecularColor = new NiflySharp.Structs.Color3 { R = 1f, G = 0.5f, B = 0.25f },
        };
        var texSet = new BSShaderTextureSet
        {
            Textures = new List<NiString4>
            {
                new(@"textures\guard\diffuse.dds", false), new(@"textures\guard\normal.dds", false),
                new(@"textures\guard\soft.dds", false),    new("", false),
                new(@"textures\guard\slot4.dds", false),   new("", false),
                new("", false),                            new(@"textures\guard\slot7.dds", false),
                new("", false),
            },
        };
        texSet.NumTextures = (uint)texSet.Textures.Count;
        shader.TextureSetRef = new NiBlockRef<BSShaderTextureSet>(f.AddBlock(texSet));
        shape.ShaderPropertyRef = new NiBlockRef<BSShaderProperty>(f.AddBlock(shader));

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
