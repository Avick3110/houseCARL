using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda.Plugins;

namespace HousecarlGenerator;

/// <summary>
/// place-asset guard (facegen-diagnostics Phase 3 — housecarl_place_asset / housecarl_bulk_place_asset). Proves the
/// WRITE side of dark-face repair: the FormKey→FaceGen-path keystone, the precise placer (explicit + auto-resolved
/// source), in-process BSA single-entry extraction with ZERO handles at rest (the cornerstone), the crash-atomic
/// non-destructive write, the wins-VFS end-to-end story through the REAL service, and the Q3 refusals.
///
/// PURE / CORE arms (no MO2 instance):
///   A  FaceGen-path transform — folder = the DEFINING master (NOT a winner), file = "00" + the 6-hex local id
///      (index masked), mesh under facegeom/.nif, tint under facetint/.dds; matches the committed fixture name. [RED:
///      the keystone — a wrong mask/folder places a DIFFERENT NPC's asset.]
///   B  native BSA single-entry extraction — TryReadArchiveEntry pulls the right bytes out of the committed FixtureA.bsa,
///      returns null for an absent entry, AND holds ZERO handle at rest (the .bsa stays renamable/deletable after). [RED:
///      the cornerstone — a held handle would block MO2/xEdit.]
///   C  crash-atomic routing — AtomicFile.WriteAllBytes overwrites byte-exact AND preserves the destination's creation
///      time (File.Replace, not File.Move), self-calibrating off a tunneling control; a fresh write lands byte-exact and
///      leaves no temp. [RED: a non-atomic File.Move regression flips the creation-time arm.]
///
/// SERVICE arms (the REAL LoadOrderService over a synthetic MO2 instance, AssetStatusProbe style):
///   D  explicit-source place + wins-VFS end-to-end — a loose source placed over a different current winner writes the
///      right bytes into a fresh houseCARL mod; after ENABLING that mod on top, the REAL svc.AssetStatus reports IT as the
///      VFS winner (the placed copy actually wins once sorted). Originals untouched; the placed file == the source bytes.
///   E  BSA-source place end-to-end — source = a .bsa path (entry derived from the destination) places the extracted bytes.
///   F  auto-resolve — sole provider used with no source=; >1 provider REFUSED ambiguous (per-asset, no guess); 0 providers
///      REFUSED with guidance. [RED: an auto-guess on ambiguity is the Q3 hazard this arm forbids.]
///   G  non-destructive / provenance / Q3 — an all-failed FRESH batch leaves NO orphan folder; a partial batch KEEPS the
///      folder (the good file present); a drive-rooted / '..' destination is a per-asset named error; the tool layer
///      refuses malformed specs (no kind, both/neither of formid+asset_path, bad formid/kind, both-expansion + loose source).
///
/// Self-contained: synthetic folders/instances in temp + the committed fixtures/asset-resolver/FixtureA.bsa, NO BSArch.
/// Run: dotnet run --project src/housecarl-generator place-asset-guard
/// </summary>
internal static class PlaceAssetProbe
{
    // A facegen path that EXISTS inside the committed FixtureA.bsa (the dark-face shape) — the extraction + e2e source.
    const string FacegenRel = @"meshes\actors\character\facegendata\facegeom\Dawnguard.esm\0001A51A.nif";

    public static int RunGuard(string[] args)
    {
        Console.WriteLine("================================================================");
        Console.WriteLine(" place-asset guard — FaceGen-path keystone + precise placer + BSA extract + wins-VFS");
        Console.WriteLine("================================================================");
        Console.WriteLine();
        int fail = 0;
        void Check(bool c, string label) { Console.WriteLine((c ? "  PASS  " : "  FAIL  ") + label); if (!c) fail++; }

        var fixDir = Path.GetFullPath(@"src/housecarl-generator/fixtures/asset-resolver");
        var fixA = Path.Combine(fixDir, "FixtureA.bsa");
        if (!File.Exists(fixA))
        {
            Console.WriteLine($"  FAIL  committed BSA fixture present at {fixA} (run from the repo root)");
            Console.WriteLine("================ 1 CHECK(S) FAILED ================");
            return 1;
        }

        var root = Path.Combine(Path.GetTempPath(), "hc-place-asset-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // ================= A: the FaceGen-path transform (the keystone) =================
            Console.WriteLine("--- A: FaceGen path = pure transform of the FormKey (defining master + masked id) ---");
            {
                var fk = FormKey.Factory("01A51A:Dawnguard.esm");        // houseCARL 6-hex form of the fixture NPC
                var mesh = FaceGenPath.For(fk, FaceGenSlot.Mesh);
                var tint = FaceGenPath.For(fk, FaceGenSlot.Tint);
                Check(mesh == @"meshes\actors\character\facegendata\facegeom\Dawnguard.esm\0001A51A.nif",
                      $"mesh path is folder=defining-master + '00'+6hex .nif — got {mesh}");
                Check(tint == @"textures\actors\character\facegendata\facetint\Dawnguard.esm\0001A51A.dds",
                      $"tint path is facetint + .dds — got {tint}");
                Check(mesh == FacegenRel, "the computed mesh path matches the committed fixture's facegen entry name exactly");

                // the FOLDER is the DEFINING master in the FormKey, NEVER substituted — a different master ⇒ a different folder.
                var fk2 = FormKey.Factory("000ABC:Skyrim.esm");
                Check(FaceGenPath.For(fk2, FaceGenSlot.Mesh) == @"meshes\actors\character\facegendata\facegeom\Skyrim.esm\00000ABC.nif",
                      "the folder is the FormKey's defining master and the id is masked to 8 hex ('00'+6) — Skyrim.esm/00000ABC.nif");
                var both = FaceGenPath.Both(fk);
                Check(both.Count == 2 && both[0].Slot == FaceGenSlot.Mesh && both[1].Slot == FaceGenSlot.Tint,
                      "Both() returns mesh first, then tint");
            }

            // ================= B: native BSA single-entry extraction + zero handles at rest =================
            Console.WriteLine();
            Console.WriteLine("--- B: TryReadArchiveEntry — right bytes, null for absent, ZERO handle at rest (cornerstone) ---");
            {
                var bsaCopy = Path.Combine(root, "extract-probe.bsa");
                File.Copy(fixA, bsaCopy);
                var bytes = AssetResolver.TryReadArchiveEntry(bsaCopy, FacegenRel);
                Check(bytes is { Length: > 0 }, $"the facegen entry's bytes are extracted from the BSA — {(bytes?.Length ?? 0)} bytes");
                Check(AssetResolver.TryReadArchiveEntry(bsaCopy, @"meshes\nope\not-in-archive.nif") is null,
                      "an entry not in the archive returns null (not a throw, not empty bytes)");

                // The cornerstone: after extraction returns, NOTHING keeps the .bsa mapped — rename + delete must succeed.
                bool renamable = true;
                var renamed = bsaCopy + ".moved";
                try { File.Move(bsaCopy, renamed); File.Move(renamed, bsaCopy); File.Delete(bsaCopy); }
                catch { renamable = false; }
                Check(renamable, "the .bsa stays renamable AND deletable after extraction — zero handles held at rest");
            }

            // ================= C: crash-atomic routing (AtomicFile.WriteAllBytes → File.Replace) =================
            Console.WriteLine();
            Console.WriteLine("--- C: place write is crash-atomic (File.Replace path), fresh + overwrite ---");
            {
                var oldCreate = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

                // self-calibrating control: does file-system tunneling mask the creation-time signal on THIS host?
                bool tunnelingMasks;
                {
                    var f = Path.Combine(root, "ctl.dat");
                    File.WriteAllBytes(f, new byte[] { 0 });
                    File.SetCreationTimeUtc(f, oldCreate);
                    var s = f + ".s"; File.WriteAllBytes(s, new byte[] { 1 });
                    File.Move(s, f, overwrite: true);
                    tunnelingMasks = File.GetCreationTimeUtc(f) == oldCreate;
                }

                var fresh = Path.Combine(root, "sub", "fresh.nif");
                Directory.CreateDirectory(Path.GetDirectoryName(fresh)!);
                var fb = new byte[] { 1, 2, 3, 4 };
                AtomicFile.WriteAllBytes(fresh, fb);
                Check(File.Exists(fresh) && File.ReadAllBytes(fresh).SequenceEqual(fb), "a fresh place writes byte-exact");
                Check(!File.Exists(fresh + ".houseCARL-tmp"), "no staging temp is left after a fresh place");

                var over = Path.Combine(root, "over.dds");
                File.WriteAllBytes(over, new byte[] { 9, 9, 9 });
                File.SetCreationTimeUtc(over, oldCreate);
                var nb = new byte[] { 7, 7, 7, 7, 7 };
                AtomicFile.WriteAllBytes(over, nb);
                Check(File.ReadAllBytes(over).SequenceEqual(nb), "an overwrite place writes the NEW bytes byte-exact");
                Check(!File.Exists(over + ".houseCARL-tmp"), "no staging temp is left after an overwrite place");
                if (tunnelingMasks)
                    Console.WriteLine("  SKIP  creation-time preserved — UNPROVABLE on a tunneling host (Q3, not a pass)");
                else
                    Check(File.GetCreationTimeUtc(over) == oldCreate,
                          "overwrite preserves the destination's creation time — File.Replace, not File.Move  [RED arm]");
            }

            // ================= D: explicit-source place + wins-VFS end-to-end (REAL service) =================
            Console.WriteLine();
            Console.WriteLine("--- D: place a loose source over a wrong winner, then ENABLE → it wins the VFS (REAL svc.AssetStatus) ---");
            {
                var inst = Path.Combine(root, "svc-d");
                var (mods, _, prof) = MakeInstance(inst);
                var wrong = Path.Combine(mods, "WrongFace");
                Directory.CreateDirectory(wrong);
                WriteLoose(wrong, FacegenRel, new byte[] { 0xBA, 0xD0 });    // the current (wrong) winner
                var correctSrc = Path.Combine(root, "correct-face.nif");
                var correctBytes = new byte[] { 0x60, 0x0D, 0x60, 0x0D };
                File.WriteAllBytes(correctSrc, correctBytes);
                WriteProfile(prof, new[] { "Dummy.esp" }, new[] { "*Dummy.esp" }, new[] { "+WrongFace" });
                WriteSkyrimIni(prof, "");
                File.WriteAllText(Path.Combine(wrong, "Dummy.esp"), "x");    // a resolvable plugin path; never parsed

                var store = new UserConfigStore(Path.Combine(root, "user-d.json"));
                using var svc = LoadOrderService.WithInstance(inst, 0, store);

                // before: the wrong copy wins
                Check(svc.AssetStatus(new[] { FacegenRel }).Results[0].Hit?.Winner?.Source == "WrongFace",
                      "before placing, the wrong loose copy wins the VFS");

                var outcome = svc.PlaceAssets(new[] { new PlaceRequest(FacegenRel, correctSrc) }, patchName: null, into: null);
                var r0 = outcome.Results[0];
                Check(r0.Placed && outcome.ModFolder is not null, $"the asset placed into a fresh houseCARL mod folder — {(r0.Placed ? Path.GetFileName(outcome.ModFolder!) : r0.Error)}");
                Check(r0.CurrentWinner == "WrongFace (loose)", $"the placement reports the CURRENT winner to sort above — {r0.CurrentWinner}");
                var placedFile = outcome.ModFolder is null ? null : Path.Combine(outcome.ModFolder, FacegenRel);
                Check(placedFile is not null && File.Exists(placedFile) && File.ReadAllBytes(placedFile).SequenceEqual(correctBytes),
                      "the placed file holds the SOURCE bytes byte-exact");
                Check(File.ReadAllBytes(correctSrc).SequenceEqual(correctBytes) && File.ReadAllBytes(Path.Combine(wrong, FacegenRel)).SequenceEqual(new byte[] { 0xBA, 0xD0 }),
                      "originals untouched — the source AND the prior winner are unchanged");

                // enable the placed mod ON TOP, then re-resolve: it must WIN (the end-to-end fix)
                if (outcome.ModFolder is { } mf)
                {
                    var placedMod = Path.GetFileName(mf);
                    WriteProfile(prof, new[] { "Dummy.esp" }, new[] { "*Dummy.esp" }, new[] { "+" + placedMod, "+WrongFace" });
                    File.SetLastWriteTimeUtc(Path.Combine(prof, "modlist.txt"), DateTime.UtcNow.AddHours(1));
                    var after = svc.AssetStatus(new[] { FacegenRel }).Results[0];
                    Check(after.Hit?.Winner?.Source == placedMod && after.Hit.Winner.Kind == AssetKind.Loose,
                          $"after enabling the placed mod on top, IT wins the VFS — winner={after.Hit?.Winner?.Source}");
                }
                else Check(false, "wins-VFS end-to-end skipped — nothing was placed");
            }

            // ================= E: BSA-source place end-to-end =================
            Console.WriteLine();
            Console.WriteLine("--- E: source = a .bsa path → the extracted entry is placed (CC-NPC case) ---");
            {
                var inst = Path.Combine(root, "svc-e");
                var (mods, _, prof) = MakeInstance(inst);
                WriteProfile(prof, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
                WriteSkyrimIni(prof, "");
                var store = new UserConfigStore(Path.Combine(root, "user-e.json"));
                using var svc = LoadOrderService.WithInstance(inst, 0, store);

                var expect = AssetResolver.TryReadArchiveEntry(fixA, FacegenRel)!;
                var outcome = svc.PlaceAssets(new[] { new PlaceRequest(FacegenRel, fixA) }, patchName: null, into: null);
                var r0 = outcome.Results[0];
                Check(r0.Placed, $"a .bsa source places (entry derived from the destination) — {(r0.Placed ? "ok" : r0.Error)}");
                var placedFile = outcome.ModFolder is null ? null : Path.Combine(outcome.ModFolder, FacegenRel);
                Check(placedFile is not null && File.Exists(placedFile) && File.ReadAllBytes(placedFile).SequenceEqual(expect),
                      "the placed bytes equal the natively-extracted BSA entry, byte-exact");

                // a QUOTED .bsa source must still route to BSA EXTRACTION, not be read WHOLE as a loose file (the Q3
                // silent-wrong mis-route the independent pre-merge review caught). RED if routing decides .bsa-vs-loose
                // on the un-trimmed string: the loose branch would place File.ReadAllBytes(wholeArchive) != the entry.
                var outcomeQ = svc.PlaceAssets(new[] { new PlaceRequest(FacegenRel, "\"" + fixA + "\"") }, patchName: null, into: null);
                var rQ = outcomeQ.Results[0];
                var placedQ = outcomeQ.ModFolder is null ? null : Path.Combine(outcomeQ.ModFolder, FacegenRel);
                Check(rQ.Placed && placedQ is not null && File.ReadAllBytes(placedQ).SequenceEqual(expect),
                      "a QUOTED .bsa source extracts the ENTRY (placed bytes == the entry, NOT the whole archive read as loose)  [RED arm]");
            }

            // ================= F: auto-resolve (sole / ambiguous / absent) =================
            Console.WriteLine();
            Console.WriteLine("--- F: auto-resolve — sole provider used; ambiguous refused (no guess); absent refused ---");
            {
                // sole provider
                {
                    var inst = Path.Combine(root, "svc-f1");
                    var (mods, _, prof) = MakeInstance(inst);
                    var only = Path.Combine(mods, "OnlyMod");
                    Directory.CreateDirectory(only);
                    var b = new byte[] { 1, 1, 1 };
                    WriteLoose(only, FacegenRel, b);
                    File.WriteAllText(Path.Combine(only, "Dummy.esp"), "x");
                    WriteProfile(prof, new[] { "Dummy.esp" }, new[] { "*Dummy.esp" }, new[] { "+OnlyMod" });
                    WriteSkyrimIni(prof, "");
                    var store = new UserConfigStore(Path.Combine(root, "user-f1.json"));
                    using var svc = LoadOrderService.WithInstance(inst, 0, store);
                    var r = svc.PlaceAssets(new[] { new PlaceRequest(FacegenRel, null) }, null, null).Results[0];
                    Check(r.Placed, $"a SOLE provider auto-resolves with no source= — {(r.Placed ? "ok" : r.Error)}");
                }
                // ambiguous → refuse, no guess
                {
                    var inst = Path.Combine(root, "svc-f2");
                    var (mods, _, prof) = MakeInstance(inst);
                    foreach (var m in new[] { "ModA", "ModB" }) { var d = Path.Combine(mods, m); Directory.CreateDirectory(d); WriteLoose(d, FacegenRel, new byte[] { 2 }); }
                    File.WriteAllText(Path.Combine(mods, "ModA", "Dummy.esp"), "x");
                    WriteProfile(prof, new[] { "Dummy.esp" }, new[] { "*Dummy.esp" }, new[] { "+ModA", "+ModB" });
                    WriteSkyrimIni(prof, "");
                    var store = new UserConfigStore(Path.Combine(root, "user-f2.json"));
                    using var svc = LoadOrderService.WithInstance(inst, 0, store);
                    var r = svc.PlaceAssets(new[] { new PlaceRequest(FacegenRel, null) }, null, null).Results[0];
                    Check(!r.Placed && r.Error!.Contains("ambiguous"), $"TWO providers + no source= is REFUSED (no guess) — {r.Error}");
                }
                // absent → refuse
                {
                    var inst = Path.Combine(root, "svc-f3");
                    var (mods, _, prof) = MakeInstance(inst);
                    WriteProfile(prof, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
                    WriteSkyrimIni(prof, "");
                    var store = new UserConfigStore(Path.Combine(root, "user-f3.json"));
                    using var svc = LoadOrderService.WithInstance(inst, 0, store);
                    var r = svc.PlaceAssets(new[] { new PlaceRequest(FacegenRel, null) }, null, null).Results[0];
                    Check(!r.Placed && r.Error!.Contains("no copy to auto-place"), $"NO provider + no source= is REFUSED with guidance — {r.Error}");
                }
            }

            // ================= G: non-destructive / provenance / Q3 refusals =================
            Console.WriteLine();
            Console.WriteLine("--- G: no-orphan on all-fail, keep-on-partial, drive-rooted reject, tool-layer spec refusals ---");
            {
                var inst = Path.Combine(root, "svc-g");
                var (mods, _, prof) = MakeInstance(inst);
                var only = Path.Combine(mods, "GMod");
                Directory.CreateDirectory(only);
                WriteLoose(only, FacegenRel, new byte[] { 5, 5 });
                File.WriteAllText(Path.Combine(only, "Dummy.esp"), "x");
                WriteProfile(prof, new[] { "Dummy.esp" }, new[] { "*Dummy.esp" }, new[] { "+GMod" });
                WriteSkyrimIni(prof, "");
                var store = new UserConfigStore(Path.Combine(root, "user-g.json"));
                using var svc = LoadOrderService.WithInstance(inst, 0, store);

                // all-failed FRESH batch → NO orphan folder left
                var allFail = svc.PlaceAssets(new[] { new PlaceRequest(@"meshes\absent\x.nif", null) }, "GHostFolder", null);
                Check(allFail.ModFolder is null, "an all-failed batch reports no mod folder");
                Check(!Directory.Exists(Path.Combine(mods, "houseCARL - GHostFolder")), "a fresh folder with NOTHING placed is removed — no orphan (F4/H2)");

                // partial FRESH batch → folder KEPT, good file present
                var partial = svc.PlaceAssets(new[]
                {
                    new PlaceRequest(FacegenRel, null),                  // ok (sole provider)
                    new PlaceRequest(@"meshes\absent\y.nif", null),      // fails (absent)
                }, "GKeepFolder", null);
                Check(partial.ModFolder is not null && File.Exists(Path.Combine(partial.ModFolder!, FacegenRel)),
                      "a PARTIAL batch keeps the folder with the good file present");

                // drive-rooted / '..' destination → per-asset named error (Q3)
                var bad = svc.PlaceAssets(new[] { new PlaceRequest(@"C:\Windows\evil.nif", @"C:\x") }, null, null).Results[0];
                Check(!bad.Placed && bad.Error!.Contains("drive-rooted"), $"a drive-rooted destination is a per-asset named error — {bad.Error}");
                var esc = svc.PlaceAssets(new[] { new PlaceRequest(@"meshes\..\..\evil.nif", FacegenRel) }, null, null).Results[0];
                Check(!esc.Placed && esc.Error!.Contains("parent-escaping"), $"a '..'-escaping destination is rejected — {esc.Error}");

                // tool-layer spec refusals (the REAL tool entrypoints, config-gated svc)
                Check(PlaceAssetTools.PlaceAsset(svc, formid: "01A51A:Dawnguard.esm", kind: null).Contains("kind is required"),
                      "single tool: formid with no kind is refused (it places ONE file)");
                Check(PlaceAssetTools.PlaceAsset(svc, formid: "01A51A:Dawnguard.esm", asset_path: "meshes/x.nif", kind: "mesh").Contains("exactly one"),
                      "single tool: BOTH formid and asset_path is refused");
                Check(PlaceAssetTools.PlaceAsset(svc).Contains("exactly one"),
                      "single tool: NEITHER formid nor asset_path is refused");
                Check(PlaceAssetTools.PlaceAsset(svc, formid: "not-a-formid", kind: "mesh").Contains("bad formid"),
                      "single tool: a malformed formid is refused named");
                Check(PlaceAssetTools.PlaceAsset(svc, formid: "01A51A:Dawnguard.esm", kind: "bogus").Contains("not valid"),
                      "single tool: a bad kind token is refused named");
                Check(PlaceAssetTools.BulkPlaceAsset(svc, new[] { new PlaceAssetSpec { Formid = "01A51A:Dawnguard.esm", Source = @"C:\loose.nif" } })
                        .Contains(".bsa"),
                      "bulk tool: a both-expansion (formid, no kind) with a non-.bsa source is refused");
                // a QUOTED .bsa source (the natural form for a spaced filename) must NOT be wrongly refused at the spec
                // level — quotes are trimmed for the test, as ReadExplicitSource does (review fix). It then attempts to
                // place mesh+tint (per-asset outcomes), never the "must be a bare '.bsa' path" spec refusal.
                Check(!PlaceAssetTools.BulkPlaceAsset(svc, new[] { new PlaceAssetSpec { Formid = "01A51A:Dawnguard.esm", Source = "\"" + fixA + "\"" } })
                        .Contains("must be a bare"),
                      "bulk tool: a QUOTED .bsa source in a both-expansion is ACCEPTED (not refused for the trailing quote)");
                Check(PlaceAssetTools.BulkPlaceAsset(svc, Array.Empty<PlaceAssetSpec>()).Contains("empty"),
                      "bulk tool: an empty assets array is rejected");
            }

            // ================= H: provenance + crash-atomic ROUTING through the SERVICE (overwrite via into=) =================
            // The fresh-folder arms (D/E) never overwrite a pre-existing dest, so this arm places TWICE into the same folder
            // (into=) to prove: (1) the overwrite yields the NEW bytes, not the stale prior — no false-success on a
            // pre-existing file (the 2026-06-12 BSArch lesson, on the SERVICE path); (2) the service place routes through
            // the crash-atomic primitive — the destination's creation time is PRESERVED (File.Replace), RED to a
            // File.Move(overwrite) regression in PlaceOne (which resets it). HONEST residual: a regression to a plain
            // File.WriteAllBytes is NOT distinguishable in-process (it also preserves creation time and is also atomic for
            // non-crash writes) — only the crash window differs, which no in-process probe can observe (the same limit
            // atomic-commit-guard states). This arm catches the File.Move regression + the stale-bytes false-success.
            Console.WriteLine();
            Console.WriteLine("--- H: service overwrite (into=) — NEW bytes, not stale; creation-time preserved (routes through AtomicFile) ---");
            {
                var inst = Path.Combine(root, "svc-h");
                var (_, _, prof) = MakeInstance(inst);
                WriteProfile(prof, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
                WriteSkyrimIni(prof, "");
                var store = new UserConfigStore(Path.Combine(root, "user-h.json"));
                using var svc = LoadOrderService.WithInstance(inst, 0, store);

                var srcV1 = Path.Combine(root, "v1.nif"); File.WriteAllBytes(srcV1, new byte[] { 1, 1, 1 });
                var srcV2 = Path.Combine(root, "v2.nif"); var v2 = new byte[] { 2, 2, 2, 2, 2, 2 }; File.WriteAllBytes(srcV2, v2);

                var first = svc.PlaceAssets(new[] { new PlaceRequest(FacegenRel, srcV1) }, "RouteProv", null);
                Check(first.Results[0].Placed && first.ModFolder is not null, "first place into a fresh folder succeeds");
                if (first.ModFolder is { } mf)
                {
                    var dest = Path.Combine(mf, FacegenRel);
                    var oldCreate = new DateTime(2019, 6, 6, 0, 0, 0, DateTimeKind.Utc);

                    // tunneling control (same dir, a File.Move on a throwaway): is the creation-time signal valid here?
                    var ctl = Path.Combine(mf, "ctl.bin"); File.WriteAllBytes(ctl, new byte[] { 0 }); File.SetCreationTimeUtc(ctl, oldCreate);
                    var cs = ctl + ".s"; File.WriteAllBytes(cs, new byte[] { 1 }); File.Move(cs, ctl, overwrite: true);
                    bool tunnelingMasks = File.GetCreationTimeUtc(ctl) == oldCreate;
                    try { File.Delete(ctl); } catch { /* throwaway */ }

                    File.SetCreationTimeUtc(dest, oldCreate);
                    var second = svc.PlaceAssets(new[] { new PlaceRequest(FacegenRel, srcV2) }, null, "RouteProv");   // into= the SAME folder
                    Check(second.Results[0].Placed, $"second place into= the existing folder succeeds — {(second.Results[0].Placed ? "ok" : second.Results[0].Error)}");
                    Check(File.Exists(dest) && File.ReadAllBytes(dest).SequenceEqual(v2),
                          "overwrite via the SERVICE yields the NEW bytes byte-exact, not the stale prior (provenance — no false success)");
                    if (tunnelingMasks)
                        Console.WriteLine("  SKIP  service place creation-time preserved — UNPROVABLE on a tunneling host (Q3, not a pass)");
                    else
                        Check(File.GetCreationTimeUtc(dest) == oldCreate,
                              "the service place preserves the dest creation time — routes through AtomicFile (File.Replace), not File.Move  [RED arm]");
                }
                else Check(false, "provenance/routing skipped — first place produced no folder");
            }
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* temp scratch */ } }

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "================ ALL PASS ================" : $"================ {fail} CHECK(S) FAILED ================");
        return fail == 0 ? 0 : 1;
    }

    // ---- synthetic MO2 layout helpers (the AssetStatusProbe / FreshnessCaptureProbe pattern) ----

    /// <summary>Create a synthetic MO2 instance skeleton (mods/, game/Data/, profiles/Default/, ModOrganizer.ini) and
    /// return (modsDir, dataDir, profileDir). The caller writes the profile + any mods.</summary>
    static (string mods, string data, string prof) MakeInstance(string inst)
    {
        var mods = Path.Combine(inst, "mods");
        var data = Path.Combine(inst, "game", "Data");
        var prof = Path.Combine(inst, "profiles", "Default");
        foreach (var d in new[] { mods, data, prof }) Directory.CreateDirectory(d);
        WriteIni(inst, "Default", Path.Combine(inst, "game"));
        return (mods, data, prof);
    }

    static void WriteProfile(string profDir, string[] loadorder, string[] plugins, string[] modlist)
    {
        Directory.CreateDirectory(profDir);
        File.WriteAllText(Path.Combine(profDir, "loadorder.txt"), "# header\r\n" + string.Join("\r\n", loadorder) + "\r\n");
        File.WriteAllText(Path.Combine(profDir, "plugins.txt"), string.Join("\r\n", plugins) + "\r\n");
        File.WriteAllText(Path.Combine(profDir, "modlist.txt"), "# header\r\n" + string.Join("\r\n", modlist) + "\r\n");
    }

    static void WriteSkyrimIni(string profDir, string resourceArchiveList)
    {
        Directory.CreateDirectory(profDir);
        File.WriteAllText(Path.Combine(profDir, "Skyrim.ini"),
            "[Archive]\r\nsResourceArchiveList=" + resourceArchiveList + "\r\n");
    }

    static void WriteIni(string inst, string profile, string gameDir) =>
        File.WriteAllText(Path.Combine(inst, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(" + profile + ")\r\ngamePath=@ByteArray("
            + gameDir.Replace(@"\", @"\\") + ")\r\n");

    static void WriteLoose(string baseDir, string rel, byte[] bytes)
    {
        var p = Path.Combine(baseDir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, bytes);
    }
}
