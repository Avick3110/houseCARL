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
///   • set_path with NO texture_slot — the header-string form (#413): an asset reference (a .tri / material /
///     physics-xml string) swaps, every other string is untouched, a length-changing swap does not false-abort, and
///     a shape/node NAME, an absent string, a wrong-case near-miss and a swap-for-itself all refuse loud.
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
    /// <summary>The synthetic mesh's asset-reference header string — a BODYTRI value, the material/.tri/xml class
    /// set_path's header-string form addresses, and not any shape or node's name.</summary>
    const string GuardTriPath = @"meshes\actors\character\character assets\guard.tri";

    const string DefaultSmoke =@"E:\Skyrim Modding\ARR 2.0\mods\A makeover for Lucien\meshes\actors\character\FaceGenData\FaceGeom\lucien.esp\00005900.nif";

    [CiProbe("nif-set-guard")]
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

        // ---- set_path, HEADER-STRING form (#413): a material / .tri / physics-xml ref swaps like a rename ----
        Console.WriteLine();
        Console.WriteLine("--- set_path without texture_slot: the header-string form ---");
        {
            const string swapped = @"meshes\actors\character\character assets\guardswapped.tri";
            var o = NifService.Set(seBytes, new[] { new NifSetOp(NifSetOpKind.SetPath, GuardTriPath, Path: swapped) });
            Check(o.Error is null && o.WrittenBytes is not null, $"a header string swaps and verifies — {o.Error ?? "ok"}");
            var strings = o.WrittenBytes is null ? new List<string>() : NifService.Inspect(o.WrittenBytes).Inspect!.HeaderStrings.ToList();
            Check(strings.Contains(swapped) && !strings.Contains(GuardTriPath),
                  "the new string is in the table and the old one is gone");
            // The write is a swap, not an addition: nothing else in the table may move.
            var before = NifService.Inspect(seBytes).Inspect!.HeaderStrings;
            Check(strings.Count == before.Count && before.Where(x => x != GuardTriPath).All(strings.Contains),
                  "every OTHER header string is untouched (a swap, not an insert)");
            // A LONGER replacement grows the table, which is the case a byte-position diff would false-abort on.
            var longer = NifService.Set(seBytes, new[] { new NifSetOp(NifSetOpKind.SetPath, GuardTriPath, Path: swapped + "_and_then_some_more") });
            Check(longer.Error is null, $"a LENGTH-CHANGING header-string swap does not false-abort gate 1 — {longer.Error ?? "ok"}");

            // Refusals. A shape/node name has its own op, with guards this form does not repeat.
            var onName = NifService.Set(seBytes, new[] { new NifSetOp(NifSetOpKind.SetPath, "GuardShape", Path: "Renamed") });
            Check(onName.Error is not null && onName.Error.Contains("rename_shape") && onName.WrittenBytes is null,
                  $"a shape NAME is refused and sent to rename_shape — {onName.Error ?? "(wrote it — BUG)"}");
            var missing = NifService.Set(seBytes, new[] { new NifSetOp(NifSetOpKind.SetPath, @"meshes\nothing\here.tri", Path: swapped) });
            Check(missing.Error is not null && missing.Error.Contains("no header string") && missing.WrittenBytes is null,
                  $"a string no block carries is refused by name — {missing.Error ?? "(wrote it — BUG)"}");
            // Case matters: the table is exact, so a near-miss must refuse rather than swap something else.
            var wrongCase = NifService.Set(seBytes, new[] { new NifSetOp(NifSetOpKind.SetPath, GuardTriPath.ToUpperInvariant(), Path: swapped) });
            Check(wrongCase.Error is not null && wrongCase.WrittenBytes is null,
                  $"matching is case-SENSITIVE — a wrong-case target refuses — {wrongCase.Error ?? "(wrote it — BUG)"}");
            var same = NifService.Set(seBytes, new[] { new NifSetOp(NifSetOpKind.SetPath, GuardTriPath, Path: GuardTriPath) });
            Check(same.Error is not null && same.WrittenBytes is null,
                  $"swapping a string for itself is refused rather than written as a no-op — {same.Error ?? "(wrote it — BUG)"}");
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

        // ---- set_shader_value (#291): the six lighting values, gated on the BLOCK's own setter ----
        Console.WriteLine();
        Console.WriteLine("--- set_shader_value: writes where the block really carries the value, refuses loud where it doesn't ---");
        {
            var shBytes = BuildShaderSe();

            // The DETECTION FACT this whole op rests on, pinned first (#291). The read gate (ReallyReads) walks the
            // INiShader interface map; that CANNOT answer writability, because the interface declares all six
            // GET-ONLY — there is no set_ accessor to find, so an interface-map write gate would refuse everything.
            // If upstream ever adds interface setters, this arm fires and the write gate should be revisited.
            var noIfaceSetters = new[] { "EmissiveColor", "EmissiveMultiple", "Glossiness", "SpecularStrength", "SpecularColor", "Alpha" }
                .All(n => typeof(INiShader).GetProperty(n) is { CanWrite: false });
            Check(noIfaceSetters, "INiShader still declares all six lighting values GET-ONLY — the reason the write gate reflects the CONCRETE block, not the interface map");

            // The gate itself, on both branches. These are the two facts the refusal messages assert.
            Check(NifService.ReallyWrites(typeof(BSLightingShaderProperty), "Glossiness") is { Writable: true, Components: 1 }
                  && NifService.ReallyWrites(typeof(BSLightingShaderProperty), "SpecularColor") is { Writable: true, Components: 3 },
                  "ReallyWrites says YES on BSLightingShaderProperty, with the component count read off the property TYPE (scalar 1 / colour 3)");
            // NO-SETTER vs UNKNOWN-TYPE are separate states, and the effect shader is specifically the FORMER. Checking
            // only 'not writable' would let the two collapse again — and the refusal messages assert different facts.
            Check(NifService.ReallyWrites(typeof(BSEffectShaderProperty), "Glossiness") is { Writable: false, UnknownTypeName: null }
                  && NifService.ReallyWrites(typeof(BSEffectShaderProperty), "Alpha") is { Writable: false, UnknownTypeName: null },
                  "ReallyWrites says NO-SETTER (not unknown-type) on BSEffectShaderProperty — the block whose accessor is the interface stub (a write there would silently no-op)");
            // The unknown-type arm is unreachable through any real shader block on 1.1.0, so it is pinned on a stand-in
            // with a settable property of a type houseCARL does not marshal. Without this, the third state ships
            // unexercised and the branch that keeps its message honest is never proven to fire.
            Check(NifService.ReallyWrites(typeof(UnmarshalableShaderStandIn), nameof(UnmarshalableShaderStandIn.Glossiness)) is { Writable: false, UnknownTypeName: "String" },
                  "a SETTABLE property of an unmarshalable type reports unknown-type — a distinct state from no-setter, so the refusal cannot claim 'not settable'");

            // YES branch — every one of the six writes, survives the save/reload, and reads back as the NEW value.
            // Each expected value is distinct from BOTH the fixture's authored value and the block's constructor
            // default, so neither a dropped write nor a stub constant can pass.
            foreach (var (name, nums, read) in new (string, float[], Func<NifShader, string>)[]
            {
                ("glossiness",        new[] { 55f },              s => Fmt2(s.Glossiness)),
                ("specular_strength", new[] { 3.25f },            s => Fmt2(s.SpecularStrength)),
                ("emissive_multiple", new[] { 7.5f },             s => Fmt2(s.EmissiveMultiple)),
                ("alpha",             new[] { 0.125f },           s => Fmt2(s.Alpha)),
                ("emissive_color",    new[] { 0.1f, 0.2f, 0.3f }, s => Rgb2(s.EmissiveColor)),
                ("specular_color",    new[] { 0.4f, 0.6f, 0.8f }, s => Rgb2(s.SpecularColor)),
            })
            {
                var o = NifService.Set(shBytes, new[] { new NifSetOp(NifSetOpKind.SetShaderValue, "LitShape", ShaderValue: name, ShaderNumbers: nums) });
                var sh = ShapeOf(o.WrittenBytes, "LitShape")?.Shader;
                string want = nums.Length == 1 ? Fmt2(nums[0]) : $"rgb({Fmt2(nums[0])},{Fmt2(nums[1])},{Fmt2(nums[2])})";
                Check(o.Error is null && sh is not null && read(sh) == want,
                      $"set_shader_value {name} -> {want} lands and reads back — {(o.Error ?? (sh is null ? "(no shader)" : read(sh)))}");
            }

            // A write changes ONLY the value it names — the other five survive untouched.
            {
                var o = NifService.Set(shBytes, new[] { new NifSetOp(NifSetOpKind.SetShaderValue, "LitShape", ShaderValue: "glossiness", ShaderNumbers: new[] { 55f }) });
                var sh = ShapeOf(o.WrittenBytes, "LitShape")?.Shader;
                Check(sh is not null && Fmt2(sh.SpecularStrength) == "1.5" && Fmt2(sh.EmissiveMultiple) == "2.5" && Fmt2(sh.Alpha) == "0.5"
                      && Rgb2(sh.EmissiveColor) == "rgb(0.25,0.5,0.75)" && Rgb2(sh.SpecularColor) == "rgb(1,0.5,0.25)",
                      "a glossiness write preserved the other five lighting values");
                Check(o.Report is { Ops.Count: 1, HeaderChanged: false, ChangedBlocks.Count: 1 },
                      "the report says: 1 op, header untouched, exactly ONE block changed (the shader)");
            }

            // NO/TYPE branch — the Q3 case this op exists to not commit. All six refuse on the effect shader, and the
            // refusal NAMES the block type rather than failing vaguely.
            foreach (var name in new[] { "glossiness", "specular_strength", "emissive_multiple", "alpha", "emissive_color", "specular_color" })
            {
                var nums = name.EndsWith("_color") ? new[] { 0.1f, 0.2f, 0.3f } : new[] { 1f };
                var o = NifService.Set(shBytes, new[] { new NifSetOp(NifSetOpKind.SetShaderValue, "EffShape", ShaderValue: name, ShaderNumbers: nums) });
                // Pin the CLAIM, not merely the wording. Naming the block type alone is too weak to be falsifiable:
                // the arity refusal names it too (a non-writable value reports 0 components), so an arm checking only
                // for "BSEffectShaderProperty" still passes with this gate deleted — it would ratify the right answer
                // arriving for the wrong reason. Requiring the settability sentence makes deleting the gate fail here.
                Check(o.Error is { } e && e.Contains("BSEffectShaderProperty") && e.Contains("not settable on that block type") && o.WrittenBytes is null,
                      $"set_shader_value {name} on a BSEffectShaderProperty → refused AS UNSETTABLE, block type NAMED, nothing written — {o.Error ?? "WROTE ANYWAY"}");
            }

            // ARITY comes from the library's property type, so a wrong count is a named refusal — never a truncated
            // or zero-padded write.
            Check(NifService.Set(shBytes, new[] { new NifSetOp(NifSetOpKind.SetShaderValue, "LitShape", ShaderValue: "glossiness", ShaderNumbers: new[] { 1f, 2f, 3f }) }).Error is { } ea1 && ea1.Contains("takes 1 number"),
                  "a scalar given 3 numbers → named refusal");
            Check(NifService.Set(shBytes, new[] { new NifSetOp(NifSetOpKind.SetShaderValue, "LitShape", ShaderValue: "specular_color", ShaderNumbers: new[] { 1f }) }).Error is { } ea2 && ea2.Contains("takes 3 numbers"),
                  "a colour given 1 number → named refusal");

            // Vocabulary: unknown names refuse WITH the list; the British spelling resolves (the renderer says
            // "colour", the library says "Color" — a caller reading one and typing it at the other is not an error).
            Check(NifService.Set(shBytes, new[] { new NifSetOp(NifSetOpKind.SetShaderValue, "LitShape", ShaderValue: "shininess", ShaderNumbers: new[] { 1f }) }).Error is { } ev && ev.Contains("glossiness"),
                  "an unknown shader_value → named refusal listing the accepted names");
            // BOTH AXES AT ONCE, and that is the point. The first version of this arm tested British-lowercase and
            // American-mixed-case separately, so it passed while 'Specular_Colour' — the combination, and the obvious
            // thing a caller actually types — resolved to nothing: the rewrite ran before the case fold and could not
            // match. An arm that never exercises the combination its wording claims is the "pins the wording, not the
            // claim" shape (review of PR #292).
            Check(NifService.ShaderValueProperty("Specular_Colour") == "SpecularColor"
                  && NifService.ShaderValueProperty("EMISSIVE_COLOUR") == "EmissiveColor"
                  && NifService.ShaderValueProperty("specular_colour") == "SpecularColor"
                  && NifService.ShaderValueProperty("Glossiness") == "Glossiness"
                  && NifService.ShaderValueProperty("specular-strength") == "SpecularStrength",
                  "the British spelling resolves AT ANY CASE (not just lower), alongside mixed case and the hyphen form");
            Check(NifService.Set(shBytes, new[] { new NifSetOp(NifSetOpKind.SetShaderValue, "NoShaderShape", ShaderValue: "glossiness", ShaderNumbers: new[] { 1f }) }).Error is { } ens && ens.Contains("no shader property"),
                  "set_shader_value on a shape with NO shader → named refusal");

            // THE 0-1 CONVENTION — warned, never enforced, and the choice is empirical rather than tidy. A scan of
            // 40,000 workspace meshes (190,298 SK lighting shaders) found 261 shapes with alpha above 1 (max 100), 155
            // emissive-colour components above 1, and 4 specular-colour components above 1 — so refusing out-of-range
            // would refuse edits to meshes that exist. The write lands; the report says the number looks wrong. Before
            // this, the 0-1 bound was asserted in three places and enforced in none (review of PR #292).
            {
                var o = NifService.Set(shBytes, new[] { new NifSetOp(NifSetOpKind.SetShaderValue, "LitShape", ShaderValue: "specular_color", ShaderNumbers: new[] { 255f, 255f, 255f }) });
                var sh = ShapeOf(o.WrittenBytes, "LitShape")?.Shader;
                Check(o.Error is null && Rgb2(sh?.SpecularColor) == "rgb(255,255,255)",
                      $"an out-of-convention colour is WRITTEN, not refused (real meshes carry them) — {o.Error ?? Rgb2(sh?.SpecularColor)}");
                Check(o.Report is { } rep && rep.Warnings.Any(x => x.Contains("outside the 0-1 range") && x.Contains("255")),
                      $"…and the report WARNS, naming the NifSkope 0-255 confusion — {(o.Report is null ? "(no report)" : string.Join(" | ", o.Report.Warnings))}");
                // The warning is scoped to the values the convention applies to: glossiness 900000 is real (the same
                // scan saw exactly that), so warning about it would be crying wolf on a legitimate write.
                var g = NifService.Set(shBytes, new[] { new NifSetOp(NifSetOpKind.SetShaderValue, "LitShape", ShaderValue: "glossiness", ShaderNumbers: new[] { 900f }) });
                Check(g.Error is null && g.Report is { } grep2 && !grep2.Warnings.Any(x => x.Contains("outside the 0-1 range")),
                      "an unbounded value (glossiness 900) is NOT warned about — the convention is per-value, not blanket");
                // And a normalized value INSIDE the range stays quiet, so the warning means something when it appears.
                var a = NifService.Set(shBytes, new[] { new NifSetOp(NifSetOpKind.SetShaderValue, "LitShape", ShaderValue: "alpha", ShaderNumbers: new[] { 0.25f }) });
                Check(a.Error is null && a.Report is { } arep && !arep.Warnings.Any(x => x.Contains("outside the 0-1 range")),
                      "an in-range alpha is not warned about");
            }

            // THE LAYOUT GATE's premise. ApplyOp declines any non-Skyrim shader layout, the same scope claim the read
            // path makes — but Set() refuses a non-SE stream before ApplyOp is reached, and an SE stream always parses
            // as the SK layout, so that gate is a SECOND one rather than one catching a live case. It is guarded by
            // pinning exactly that premise: if an SE-stream mesh ever parses as some other layout, this fires and the
            // gate stops being redundant. (Type is a settable property — the coupling is nifly's, not a guarantee.)
            Check(ShapeOf(shBytes, "LitShape")?.Shader is { GameType: "SK" } && NifService.Inspect(shBytes).Inspect is { IsSkyrimSE: true },
                  "an SE-stream mesh still parses its shader as the SK layout — the premise that makes the layout gate redundant rather than dead");
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
        Check(NifService.Set(seBytes, new[] { new NifSetOp(NifSetOpKind.RenameShape, "GuardShape", NewName: "BareShape") }).Error is { } e7 && e7.Contains("already named"),
              "rename ONTO an existing shape name → named refusal (no manufactured duplicate; keeps gate-2 read-back sound)");

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

        // ---- service lanes end-to-end (REAL LoadOrderService over a synthetic MO2 instance — PlaceAssetProbe pattern) ----
        Console.WriteLine();
        Console.WriteLine("--- service: new-folder + in-place lanes + persistent consent (real LoadOrderService) ---");
        const string MeshRel = @"meshes\actors\character\facegendata\facegeom\Test.esp\00000001.nif";
        var svcRoot = Path.Combine(Path.GetTempPath(), "hc-nif-set-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(svcRoot);
        try
        {
            // DEFAULT (new-folder) lane: the edited mesh lands in a fresh houseCARL folder; original untouched; winner reported.
            {
                var inst = Path.Combine(svcRoot, "new");
                var (mods, _, prof) = MakeInstance(inst);
                var mod = Path.Combine(mods, "FaceMod"); Directory.CreateDirectory(mod);
                WriteLoose(mod, MeshRel, seBytes);
                File.WriteAllText(Path.Combine(mod, "Dummy.esp"), "x");
                WriteProfile(prof, new[] { "Dummy.esp" }, new[] { "*Dummy.esp" }, new[] { "+FaceMod" });
                WriteSkyrimIni(prof);
                using var svc = HousecarlMcp.LoadOrderService.WithInstance(inst, 0, new UserConfigStore(Path.Combine(svcRoot, "u-new.json")));

                var res = svc.NifSet(MeshRel, new[] { new NifSetOp(NifSetOpKind.SetFlags, "GuardShape", Flags: 0x800000E) }, null, "FaceFix", null, inPlace: false, acknowledge: false);
                Check(res.Error is null && res.OutputModFolder is not null, $"new-folder lane writes a verified mesh into a fresh houseCARL folder — {res.Error ?? "ok"}");
                var placed = res.OutputModFolder is null ? null : Path.Combine(res.OutputModFolder, MeshRel);
                Check(placed is not null && File.Exists(placed), "the edited mesh lands at the SAME rel path in the new folder");
                Check(ShapeOf(placed is null ? null : File.ReadAllBytes(placed), "GuardShape") is { Flags: 0x800000E }, "the placed mesh reads the new flags");
                Check(File.ReadAllBytes(Path.Combine(mod, MeshRel)).SequenceEqual(seBytes), "the ORIGINAL loose mesh is untouched (non-destructive default)");
                Check(res.CurrentWinner is { } w && w.Contains("FaceMod"), $"reports the current winner to sort above — {res.CurrentWinner}");
            }

            // IN-PLACE lane: first call prompts for consent (nothing written); ack writes in place; a later edit needs no re-ack.
            {
                var inst = Path.Combine(svcRoot, "ip");
                var (mods, _, prof) = MakeInstance(inst);
                var mod = Path.Combine(mods, "FaceMod"); Directory.CreateDirectory(mod);
                WriteLoose(mod, MeshRel, seBytes);
                File.WriteAllText(Path.Combine(mod, "Dummy.esp"), "x");
                WriteProfile(prof, new[] { "Dummy.esp" }, new[] { "*Dummy.esp" }, new[] { "+FaceMod" });
                WriteSkyrimIni(prof);
                var loosePath = Path.Combine(mod, MeshRel);
                using var svc = HousecarlMcp.LoadOrderService.WithInstance(inst, 0, new UserConfigStore(Path.Combine(svcRoot, "u-ip.json")));

                var r1 = svc.NifSet(MeshRel, new[] { new NifSetOp(NifSetOpKind.SetFlags, "GuardShape", Flags: 0x800000E) }, null, null, null, inPlace: true, acknowledge: false);
                Check(r1.NeedsAcknowledge && r1.Report is null, $"in-place FIRST call without acknowledge → consent prompt, nothing written — {(r1.NeedsAcknowledge ? "prompt" : "NO PROMPT")}");
                // The mesh prompt's half of the shared lead, by PRESENCE of the corrected wording. Keyed to "mesh", so
                // this also pins that the mesh lane passes its own subject rather than inheriting the plugin's.
                var meshPrompt = r1.AckPrompt ?? "";
                Check(meshPrompt.Contains("shown until an in-place write to this mesh LANDS", StringComparison.Ordinal)
                      && meshPrompt.Contains("a call that is refused records nothing", StringComparison.Ordinal),
                      "the mesh prompt states WHEN it stops — a landed write, not \"once\"");
                Check(meshPrompt.Contains("not a copy", StringComparison.Ordinal)
                      && meshPrompt.Contains("cannot restore what it overwrites", StringComparison.Ordinal),
                      "the mesh prompt's file claim is direction-neutral (true whether or not a prior call already mutated it)");
                Check(File.ReadAllBytes(loosePath).SequenceEqual(seBytes), "the loose file is untouched by the un-acknowledged in-place call");

                var r2 = svc.NifSet(MeshRel, new[] { new NifSetOp(NifSetOpKind.SetFlags, "GuardShape", Flags: 0x800000E) }, null, null, null, inPlace: true, acknowledge: true);
                Check(r2.Error is null && r2.InPlace && r2.InPlacePath == loosePath, $"in-place WITH acknowledge overwrites the loose file where it sits — {r2.Error ?? "ok"}");
                Check(ShapeOf(File.ReadAllBytes(loosePath), "GuardShape") is { Flags: 0x800000E }, "the in-place file now reads the new flags");

                var r3 = svc.NifSet(MeshRel, new[] { new NifSetOp(NifSetOpKind.SetScale, "GuardShape", Scale: 2f) }, null, null, null, inPlace: true, acknowledge: false);
                Check(r3.Error is null && !r3.NeedsAcknowledge && r3.InPlace, $"a LATER in-place edit of the same file needs NO re-acknowledge (consent persisted) — {(r3.NeedsAcknowledge ? "RE-PROMPTED" : "ok")}");

                // mutual exclusion + absent path refusals through the service
                Check(svc.NifSet(MeshRel, new[] { new NifSetOp(NifSetOpKind.SetScale, "GuardShape", Scale: 1f) }, null, null, "SomeFolder", inPlace: true, acknowledge: false).Error is { } em && em.Contains("mutually exclusive"),
                      "in_place + into= → named refusal (mutually exclusive)");
                Check(svc.NifSet(@"meshes\nope\absent.nif", new[] { new NifSetOp(NifSetOpKind.SetScale, "GuardShape", Scale: 1f) }, null, null, null, false, false).Error is { } ea && ea.Contains("ABSENT"),
                      "an absent mesh path → ABSENT refusal");
            }

            // IN-PLACE lane, the REFUSAL direction of the consent record (#378): a call refused AFTER the consent
            // check but BEFORE the file changes must record nothing, or the next call rewrites the user's original
            // unprompted. The nif lane's refusals past the gate are all I/O, so the fixture holds the target open —
            // shared for reading, so the resolve and read still succeed and the ATOMIC SWAP is what fails, leaving
            // the mesh byte-intact. Own instance and own store, so the arm starts un-acknowledged.
            {
                var inst = Path.Combine(svcRoot, "iprefuse");
                var (mods, _, prof) = MakeInstance(inst);
                var mod = Path.Combine(mods, "FaceMod"); Directory.CreateDirectory(mod);
                WriteLoose(mod, MeshRel, seBytes);
                File.WriteAllText(Path.Combine(mod, "Dummy.esp"), "x");
                WriteProfile(prof, new[] { "Dummy.esp" }, new[] { "*Dummy.esp" }, new[] { "+FaceMod" });
                WriteSkyrimIni(prof);
                var loosePath = Path.Combine(mod, MeshRel);
                var storePath = Path.Combine(svcRoot, "u-ip-refused.json");
                using var svc = HousecarlMcp.LoadOrderService.WithInstance(inst, 0, new UserConfigStore(storePath));

                var op = new[] { new NifSetOp(NifSetOpKind.SetFlags, "GuardShape", Flags: 0x800000E) };
                bool refused, spent, untouched, gateFirst;
                using (new FileStream(loosePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    // The SAME call without acknowledge must still meet the handshake. Without this the arm accepts any
                    // failure, so hoisting the I/O refusal ahead of the consent gate — a plausible "pre-flight the
                    // lock" refactor — would leave it green while the scenario it names stopped happening at all.
                    gateFirst = svc.NifSet(MeshRel, op, null, null, null, inPlace: true, acknowledge: false).NeedsAcknowledge;
                    var blocked = svc.NifSet(MeshRel, op, null, null, null, inPlace: true, acknowledge: true);
                    refused = blocked.Error is not null && !blocked.InPlace;
                    spent = new UserConfigStore(storePath).IsInPlaceAcknowledged(loosePath);
                    untouched = File.ReadAllBytes(loosePath).SequenceEqual(seBytes);
                }
                Check(gateFirst, $"the consent gate is reached BEFORE the overwrite refuses — {(gateFirst ? "handshake" : "refused ahead of the gate; this arm would be vacuous")}");
                Check(refused && untouched, $"a held target makes the in-place overwrite refuse with the mesh byte-intact — {(refused ? "refused" : "WROTE ANYWAY")}, untouched={untouched}");
                Check(!spent, $"the refused in-place edit records NO consent — consentRecorded={spent} (want False)");
                var after = svc.NifSet(MeshRel, op, null, null, null, inPlace: true, acknowledge: false);
                Check(after.NeedsAcknowledge && after.Report is null,
                      $"…so the NEXT call still meets the first-touch prompt rather than overwriting unprompted — {(after.NeedsAcknowledge ? "prompt" : "NO PROMPT — the refusal banked the confirmation")}");
            }
        }
        finally { try { Directory.Delete(svcRoot, recursive: true); } catch { /* temp scratch */ } }

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

                // rename on REAL data — the flagship facegen case, and the op most exposed to nifly's string-table
                // rebuild (a rebuilt table that reindexed shared strings would change OTHER blocks' name refs → gate-1
                // would refuse). This arm proves a real-mesh rename does NOT false-refuse — and (with a length change)
                // exercises the F1 block-slicing recovery on a real footer, where a +4 misalign would surface.
                var reName = head.Name + "_HC_RENAMED_LONGER";
                var ro = NifService.Set(fgBytes, new[] { new NifSetOp(NifSetOpKind.RenameShape, head.Name, NewName: reName) });
                Check(ro.Error is null && ro.WrittenBytes is not null, $"rename_shape on a real facegen mesh does NOT false-refuse — {ro.Error ?? "ok"}");
                var rb = ShapeOf(ro.WrittenBytes, reName);
                Check(rb is not null && rb.Flags == head.Flags && rb.Partitions.Count == head.Partitions.Count,
                      $"the real-mesh rename landed + preserved flags/partitions — {(rb is null ? "shape GONE" : "ok")}");
                Check(ro.Report is { HeaderChanged: true }, "the real-mesh rename touched the header string table (as expected)");
            }
        }

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "================ ALL PASS ================" : $"================ {fail} CHECK(S) FAILED ================");
        return fail == 0 ? 0 : 1;
    }

    static NifShape? ShapeOf(byte[]? bytes, string name)
        => bytes is null ? null : NifService.Inspect(bytes).Inspect?.Shapes.FirstOrDefault(s => s.Name == name);

    /// <summary>A stand-in for the one <see cref="NifService.ReallyWrites"/> state no real NiflySharp 1.1.0 shader block
    /// can produce: a lighting value that IS settable, of a type houseCARL has no marshalling for. It exists so that
    /// state's refusal — which must NOT claim the value is unsettable — is exercised rather than shipped on trust.</summary>
    sealed class UnmarshalableShaderStandIn { public string Glossiness { get; set; } = ""; }

    /// <summary>A nullable shader scalar as invariant text — "(unread)" when the reader withheld it, which is a
    /// DIFFERENT outcome from any number and must never compare equal to one.</summary>
    static string Fmt2(float? v) => v is { } f ? f.ToString(System.Globalization.CultureInfo.InvariantCulture) : "(unread)";

    static string Rgb2(NifColor? c) => c is { } k ? $"rgb({Fmt2(k.R)},{Fmt2(k.G)},{Fmt2(k.B)})" : "(unread)";

    /// <summary>Author a synthetic SE mesh: root → GuardChildA node; a full GuardShape (flags/scale/alpha/2 partitions);
    /// and a BareShape carrying nothing (for the not-applicable refusal arms). The spike CreateAndSave_SE recipe.</summary>
    internal static byte[] BuildSyntheticSe()
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

        // A header string that is an ASSET REFERENCE rather than a name — the material / .tri / physics-xml class
        // set_path's header-string form addresses.
        var tri = new NiStringExtraData { Name = new NiStringRef("BODYTRI"), StringData = new NiStringRef(GuardTriPath) };
        root.ExtraDataList ??= new NiBlockRefArray<NiExtraData>();
        root.ExtraDataList.AddBlockRef(f.AddBlock(tri));
        root.NumExtraDataList = (uint)root.ExtraDataList.Count;

        using var ms = new MemoryStream();
        if (f.Save(ms) != 0) throw new InvalidOperationException("nif-set-guard: authoring the synthetic SE mesh failed to save");
        return ms.ToArray();
    }

    /// <summary>A synthetic SE mesh carrying BOTH shader block types (#291): 'LitShape' with a
    /// BSLightingShaderProperty — the one block NiflySharp 1.1.0 makes all six lighting values settable on — and
    /// 'EffShape' with a BSEffectShaderProperty, which answers all six from <c>INiShader</c>'s get-only stub and can
    /// write none of them. Separate from <see cref="BuildSyntheticSe"/> so the shader arms cannot perturb the block
    /// indices the gate-1 arms above depend on.
    ///
    /// Every value is authored AWAY from its constructor default, so a write arm that read the default back would
    /// fail rather than coincidentally match (the stub-constant trap, one axis over).</summary>
    static byte[] BuildShaderSe()
    {
        var ver = new NiVersion { FileVersion = NiVersion.ToFile("20.2.0.7"), UserVersion = 12, StreamVersion = 100 };
        var f = new NifFile();
        f.Create(ver, withRootNode: true);
        var root = f.GetRootNodes().First(); root.Name = new NiStringRef("ShaderRoot"); root.Flags_ui = 0xE;

        var lit = new BSTriShape { Name = new NiStringRef("LitShape"), Flags_ui = 0x400000E, Scale = 1f };
        root.Children.AddBlockRef(f.AddBlock(lit));
        var lsp = new BSLightingShaderProperty
        {
            Glossiness = 30f,
            SpecularStrength = 1.5f,
            EmissiveMultiple = 2.5f,
            Alpha = 0.5f,
            EmissiveColor = new NiflySharp.Structs.Color4(0.25f, 0.5f, 0.75f, 0f),
            SpecularColor = new NiflySharp.Structs.Color3(1f, 0.5f, 0.25f),
        };
        lit.ShaderPropertyRef = new NiBlockRef<BSShaderProperty>(f.AddBlock(lsp));

        var eff = new BSTriShape { Name = new NiStringRef("EffShape"), Flags_ui = 0x400000E, Scale = 1f };
        root.Children.AddBlockRef(f.AddBlock(eff));
        eff.ShaderPropertyRef = new NiBlockRef<BSShaderProperty>(f.AddBlock(new BSEffectShaderProperty()));

        var noShader = new BSTriShape { Name = new NiStringRef("NoShaderShape"), Flags_ui = 0x400000E, Scale = 1f };
        root.Children.AddBlockRef(f.AddBlock(noShader));

        using var ms = new MemoryStream();
        if (f.Save(ms) != 0) throw new InvalidOperationException("nif-set-guard: authoring the shader mesh failed to save");
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

    // ---- synthetic MO2 layout helpers (the PlaceAssetProbe / AssetStatusProbe pattern) ----

    internal static (string mods, string data, string prof) MakeInstance(string inst)
    {
        var mods = Path.Combine(inst, "mods");
        var data = Path.Combine(inst, "game", "Data");
        var prof = Path.Combine(inst, "profiles", "Default");
        foreach (var d in new[] { mods, data, prof }) Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(inst, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(inst, "game").Replace(@"\", @"\\") + ")\r\n");
        return (mods, data, prof);
    }

    internal static void WriteProfile(string profDir, string[] loadorder, string[] plugins, string[] modlist)
    {
        Directory.CreateDirectory(profDir);
        File.WriteAllText(Path.Combine(profDir, "loadorder.txt"), "# header\r\n" + string.Join("\r\n", loadorder) + "\r\n");
        File.WriteAllText(Path.Combine(profDir, "plugins.txt"), string.Join("\r\n", plugins) + "\r\n");
        File.WriteAllText(Path.Combine(profDir, "modlist.txt"), "# header\r\n" + string.Join("\r\n", modlist) + "\r\n");
    }

    internal static void WriteSkyrimIni(string profDir)
    {
        Directory.CreateDirectory(profDir);
        File.WriteAllText(Path.Combine(profDir, "Skyrim.ini"), "[Archive]\r\nsResourceArchiveList=\r\n");
    }

    internal static void WriteLoose(string baseDir, string rel, byte[] bytes)
    {
        var p = Path.Combine(baseDir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, bytes);
    }
}
