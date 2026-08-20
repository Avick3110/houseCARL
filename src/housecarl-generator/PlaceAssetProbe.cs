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
///   I  the VFS source lane — source= a DATA-RELATIVE path resolved through the asset layer, with source_provider=
///      choosing the pole. Fixtures are named HOSTILELY ("JK's Skyrim", "winner") because friendly names cannot
///      exercise what the provider list promises. A NAMED provider is read even when another copy wins (I1, the arm
///      no other source policy passes); '*winner' reads the current winner (I2); a named provider that doesn't supply
///      the path is refused with nothing substituted (I3); source ≠ destination is a RENAME (I4 — the mechanism an
///      appearance copy is built from); an absent source and a pole against an on-disk source are named refusals
///      (I5, I6); odd path spellings resolve through the VFS (I7); the pole with no source= (I8); case-insensitivity
///      (I9); and the static pole-spelling correction on a miss (I10). I1c round-trips the refusal's own tokens back
///      through the tool — the arm a substring check cannot replace.
///   K  a BSA provider named as the pole, and a Data-relative source that merely ENDS in '.bsa'.
///   L  the wire field names — PlaceAssetSpec deserialized from the JSON an MCP client actually sends.
///   M  the OFF-ORDER source lane (F1) — source_provider= naming a mod MO2 does not tick, served off that mod
///      folder's disk (loose, then its root archives), and every pole that must NOT widen with it. Twenty-six
///      cells; see the block's own header for the grid.
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
                    Check(!r.Placed && r.Error!.Contains(WriteSentences.PlaceSourceWillNotGuess, StringComparison.Ordinal),
                          $"TWO providers + no source= is REFUSED (no guess) — {r.Error}");
                    Check(r.Error!.Contains("ModA", StringComparison.Ordinal) && r.Error.Contains("ModB", StringComparison.Ordinal),
                          "the refusal NAMES both providers — what source_provider= takes back");
                    Check(!r.Error!.Contains(mods, StringComparison.OrdinalIgnoreCase),
                          "the refusal leaks NO on-disk path — a provider name cannot go stale between the resolve and the read  [RED arm]");
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
                // place mesh+tint (per-asset outcomes), never the "must be a FULL '.bsa' path" spec refusal.
                // A RELATIVE '.bsa' is a Data-relative asset path now, not an archive to open — so it names ONE file
                // and cannot serve two slots. Accepting it here would hand the mesh and the tint the same bytes.
                Check(PlaceAssetTools.BulkPlaceAsset(svc, new[] { new PlaceAssetSpec { Formid = "01A51A:Dawnguard.esm", Source = @"meshes\some\thing.bsa" } })
                        .Contains("must be a FULL '.bsa' path", StringComparison.Ordinal),
                      "bulk tool: a both-expansion with a RELATIVE '.bsa' source is refused (one VFS path cannot serve two slots)  [RED arm]");
                Check(PlaceAssetTools.BulkPlaceAsset(svc, new[] { new PlaceAssetSpec { Formid = "01A51A:Dawnguard.esm", Source = @"C:\nope\x.nif", SourceProvider = "GMod" } })
                        .Contains(WriteSentences.PlaceBothSlotsPoleConstraint, StringComparison.Ordinal),
                      "bulk tool: the both-expansion refusal states when source_provider= actually applies there  [RED arm]");
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

            // ================= I: the VFS source lane — a Data-relative source + the three SOURCE poles =================
            Console.WriteLine();
            Console.WriteLine("--- I: source= a Data-relative path; source_provider= names WHOSE copy (named / winner / sole) ---");
            {
                var inst = Path.Combine(root, "svc-i");
                var (mods, _, prof) = MakeInstance(inst);
                // HOSTILE names on purpose. The first fixture used ModA/ModB, which cannot exercise the one thing the
                // provider list promises — that a name lifted out of a refusal is a name the selector accepts. An
                // apostrophe is the common real case ("JK's Skyrim" is one of the most-installed Skyrim mods) and it
                // dissolved the single-quote delimiter mid-name; the mod called "winner" is the one that used to
                // collide with the pole token. Both are now ordinary names, and the arms below prove it.
                const string ModA = "JK's Skyrim";
                const string ModB = "winner";
                var aBytes = new byte[] { 0xA1, 0xA1, 0xA1 };
                var bBytes = new byte[] { 0xB2, 0xB2 };
                foreach (var (m, b) in new[] { (ModA, aBytes), (ModB, bBytes) })
                {
                    var d = Path.Combine(mods, m);
                    Directory.CreateDirectory(d);
                    WriteLoose(d, FacegenRel, b);
                }
                File.WriteAllText(Path.Combine(mods, ModA, "Dummy.esp"), "x");
                WriteProfile(prof, new[] { "Dummy.esp" }, new[] { "*Dummy.esp" }, new[] { "+" + ModA, "+" + ModB });
                WriteSkyrimIni(prof, "");
                var store = new UserConfigStore(Path.Combine(root, "user-i.json"));
                using var svc = LoadOrderService.WithInstance(inst, 0, store);

                // Which of the two currently WINS is read from the resolver, never assumed: the arm's claim is that a
                // named provider is honoured whether or not it is the winner, and hard-coding the winner would make
                // the arm pass for the wrong reason the day MO2 priority is read differently.
                var winnerName = svc.AssetStatus(new[] { FacegenRel }).Results[0].Hit?.Winner?.Source;
                Check(winnerName == ModA || winnerName == ModB, $"the fixture is genuinely contended and one copy wins — winner={winnerName ?? "(none)"}");
                var loserName = winnerName == ModA ? ModB : ModA;
                var winnerBytes = winnerName == ModA ? aBytes : bBytes;
                var loserBytes = winnerName == ModA ? bBytes : aBytes;

                // The fixture's whole point: a mod called "winner" is an ORDINARY provider now. Under the bare-token
                // spelling this name was unreachable — the selector shadowed it — and the ambiguity refusal listed it
                // as a token its own remedy rejected. Naming it must place its bytes, and must not mean the pole.
                {
                    var o = svc.PlaceAssets(new[] { new PlaceRequest(FacegenRel, FacegenRel, "winner") }, null, null);
                    var r = o.Results[0];
                    var placed = o.ModFolder is null ? null : Path.Combine(o.ModFolder, FacegenRel);
                    var winnerModBytes = ModB == "winner" ? bBytes : aBytes;
                    Check(r.Placed && placed is not null && File.ReadAllBytes(placed).SequenceEqual(winnerModBytes),
                          $"a provider literally named 'winner' is reachable by its own name — the sigil un-reserved it  [RED arm] — {(r.Placed ? "ok" : r.Error)}");
                }

                // I1 — the DISCRIMINATING arm: a named provider that is NOT the winner is read anyway. Every other
                // source policy (winner-read, sole-provider) fails this one, which is why the fixture is contended.
                {
                    var o = svc.PlaceAssets(new[] { new PlaceRequest(FacegenRel, FacegenRel, loserName) }, null, null);
                    var r = o.Results[0];
                    var placed = o.ModFolder is null ? null : Path.Combine(o.ModFolder, FacegenRel);
                    Check(r.Placed && placed is not null && File.ReadAllBytes(placed).SequenceEqual(loserBytes),
                          $"source_provider='{loserName}' places THAT provider's bytes though '{winnerName}' wins the VFS  [RED arm] — {(r.Placed ? "ok" : r.Error)}");
                    Check(placed is not null && !File.ReadAllBytes(placed).SequenceEqual(winnerBytes),
                          "…and specifically NOT the winner's bytes (the two copies differ, so the assertion can fail)");
                }

                // I1b — the SAME claim through the WIRE. Everything else in arm I builds PlaceRequest by hand, which
                // pins the service policy and says nothing about whether the tool layer threads source_provider=
                // into it at all: with the mapping deleted, every hand-built arm here still passes while the
                // parameter silently does nothing on a real call. Both entrypoints, because they map specs
                // separately.  [RED arm]
                {
                    // The folder name is read OUT of the render rather than assembled from the naming convention —
                    // an arm that hard-codes the convention fails for a reason that has nothing to do with the claim.
                    var text = PlaceAssetTools.PlaceAsset(svc, asset_path: FacegenRel, source: FacegenRel,
                                                          source_provider: loserName, patch_name: "WireScalar");
                    var placedFile = PlacedFileFrom(text, mods, FacegenRel);
                    Check(placedFile is not null && File.ReadAllBytes(placedFile).SequenceEqual(loserBytes),
                          $"housecarl_place_asset carries source_provider= through to the read (the loser's bytes, not the winner's) — {Trim1(text)}");

                    var bulkText = PlaceAssetTools.BulkPlaceAsset(svc, new[]
                    {
                        new PlaceAssetSpec { AssetPath = FacegenRel, Source = FacegenRel, SourceProvider = loserName },
                    }, patch_name: "WireBulk");
                    var bulkFile = PlacedFileFrom(bulkText, mods, FacegenRel);
                    Check(bulkFile is not null && File.ReadAllBytes(bulkFile).SequenceEqual(loserBytes),
                          $"housecarl_bulk_place_asset carries per-asset source_provider through too — {Trim1(bulkText)}");
                }

                // I1c — THE ROUND TRIP. The refusal tells the caller to pass one of the names it lists; this takes
                // those names OUT of the rendered refusal and feeds each one back, asserting it places. The first
                // spelling of that list ran the name and its kind together ("ModA (loose)"), which no pole matches —
                // so the message's own remedy refused, and an arm asserting Contains("ModA") passed anyway. A
                // substring check cannot see this class; only the round trip can.  [RED arm]
                {
                    var refusal = svc.PlaceAssets(new[] { new PlaceRequest(FacegenRel, FacegenRel, null) }, null, null).Results[0].Error!;
                    // DOUBLE quotes: the delimiter has to be a character a provider name cannot contain, and one of
                    // these fixtures is named "JK's Skyrim" — a single-quote extractor returns [JK] and a fragment.
                    var tokens = System.Text.RegularExpressions.Regex.Matches(refusal, "\"([^\"]+)\"")
                        .Select(m => m.Groups[1].Value)
                        .Distinct().ToList();
                    Check(tokens.Count == 2 && tokens.Contains(ModA) && tokens.Contains(ModB),
                          $"the refusal offers exactly the two provider names as delimited tokens, apostrophe and all — [{string.Join(" | ", tokens)}]");
                    foreach (var token in tokens)
                    {
                        var o = svc.PlaceAssets(new[] { new PlaceRequest(FacegenRel, FacegenRel, token) }, null, null);
                        var rr = o.Results[0];
                        Check(rr.Placed, $"a token copied verbatim out of the refusal is accepted by source_provider= ('{token}') — {(rr.Placed ? "placed" : rr.Error)}");
                    }
                }

                // I2 — the winner pole stays reachable: "what the game shows right now".
                {
                    var o = svc.PlaceAssets(new[] { new PlaceRequest(FacegenRel, FacegenRel, AssetSourceChoice.WinnerToken) }, null, null);
                    var r = o.Results[0];
                    var placed = o.ModFolder is null ? null : Path.Combine(o.ModFolder, FacegenRel);
                    Check(r.Placed && placed is not null && File.ReadAllBytes(placed).SequenceEqual(winnerBytes),
                          $"source_provider={AssetSourceChoice.WinnerToken} places the CURRENT winner's bytes ('{winnerName}') — {(r.Placed ? "ok" : r.Error)}");
                }

                // I3 — a named provider that doesn't supply the path is refused, NOT quietly served by another.
                {
                    var r = svc.PlaceAssets(new[] { new PlaceRequest(FacegenRel, FacegenRel, "NoSuchMod") }, null, null).Results[0];
                    Check(!r.Placed && r.Error!.Contains(WriteSentences.PlaceSourceNoSubstitute, StringComparison.Ordinal),
                          $"a named provider that does not supply the path is REFUSED with nothing substituted  [RED arm] — {r.Error}");
                    Check(r.Error!.Contains(ModA, StringComparison.Ordinal) && r.Error.Contains(ModB, StringComparison.Ordinal),
                          "…and the providers that DO supply it are named (the typo's remedy)");
                }

                // I4 — THE RENAME. Source path ≠ destination path: one NPC's baked facegen lands under another's
                // FormID-derived name. This is the mechanism an appearance copy is built from.
                {
                    var destRel = FaceGenPath.For(FormKey.Factory("000FFF:Dummy.esp"), FaceGenSlot.Mesh);
                    Check(destRel != FacegenRel, "the rename fixture's destination really is a different path from its source");
                    var o = svc.PlaceAssets(new[] { new PlaceRequest(destRel, FacegenRel, loserName) }, null, null);
                    var r = o.Results[0];
                    var placed = o.ModFolder is null ? null : Path.Combine(o.ModFolder, destRel);
                    Check(r.Placed && placed is not null && File.Exists(placed) && File.ReadAllBytes(placed).SequenceEqual(loserBytes),
                          $"the source file's bytes land under the DESTINATION's name — a rename  [RED arm] — {(r.Placed ? "ok" : r.Error)}");
                    // STARTS with, not Contains: the loose description already ends in the provider's ABSOLUTE path,
                    // which has the Data-relative path as a substring — so Contains was satisfied by construction and
                    // stayed green with the rename prefix deleted. The prefix is the claim; its position is what
                    // distinguishes it from the path that was always there.  [RED arm]
                    Check(r.SourceDesc is not null && r.SourceDesc.StartsWith(FacegenRel, StringComparison.OrdinalIgnoreCase),
                          $"the render LEADS with the source file — a rename that reported only the provider would hide it — {r.SourceDesc}");
                    Check(o.ModFolder is null || !File.Exists(Path.Combine(o.ModFolder, FacegenRel)),
                          "the SOURCE path is not also written — a rename places one file, not two");
                }

                // I5 — a Data-relative source nothing provides is its own refusal, about the SOURCE (not the
                // destination): the destination of a rename is new by definition, so the two cannot share a message.
                {
                    var r = svc.PlaceAssets(new[] { new PlaceRequest(FacegenRel, @"meshes\nope\absent.nif", null) }, null, null).Results[0];
                    Check(!r.Placed && r.Error!.Contains("provides the source", StringComparison.Ordinal)
                          && r.Error.Contains(@"meshes\nope\absent.nif", StringComparison.OrdinalIgnoreCase),
                          $"an absent Data-relative SOURCE is refused naming that path — {r.Error}");
                }

                // I6 — a pole against an on-disk source cannot apply, and is SAID rather than dropped (Q3).
                {
                    var onDisk = Path.Combine(root, "i6-source.nif");
                    File.WriteAllBytes(onDisk, new byte[] { 7, 7 });
                    var r = svc.PlaceAssets(new[] { new PlaceRequest(FacegenRel, onDisk, ModA) }, null, null).Results[0];
                    Check(!r.Placed && r.Error!.Contains(WriteSentences.PlaceSourceProviderNeedsRelPath, StringComparison.Ordinal),
                          $"source_provider= with an ON-DISK source is refused, never silently ignored  [RED arm] — {r.Error}");
                }

                // I7 — the spellings that are NOT one exact file on disk. A leading separator is Path.IsPathRooted
                // on Windows but AssetResolver trims it, so '\meshes\…' is a legal Data-relative path as a
                // DESTINATION; classifying it as on-disk made one string mean two things in a single call and sent
                // it to the process CWD. Quoted is the other: it must resolve as the path inside the quotes.  [RED arm]
                {
                    foreach (var spelling in new[] { @"\" + FacegenRel, "/" + FacegenRel.Replace('\\', '/'), "\"" + FacegenRel + "\"" })
                    {
                        var o = svc.PlaceAssets(new[] { new PlaceRequest(FacegenRel, spelling, loserName) }, null, null);
                        var rr = o.Results[0];
                        var placed = o.ModFolder is null ? null : Path.Combine(o.ModFolder, FacegenRel);
                        Check(rr.Placed && placed is not null && File.ReadAllBytes(placed).SequenceEqual(loserBytes),
                              $"source= '{spelling}' resolves through the VFS under the named pole — {(rr.Placed ? "placed" : rr.Error)}");
                    }
                }

                // I8 — the pole with NO source= at all: the tool description offers this shape (read the destination
                // path from a named provider), and nothing exercised it.
                {
                    var o = svc.PlaceAssets(new[] { new PlaceRequest(FacegenRel, null, loserName) }, null, null);
                    var r = o.Results[0];
                    var placed = o.ModFolder is null ? null : Path.Combine(o.ModFolder, FacegenRel);
                    Check(r.Placed && placed is not null && File.ReadAllBytes(placed).SequenceEqual(loserBytes),
                          $"source_provider= with NO source= reads the destination path from the named provider — {(r.Placed ? "placed" : r.Error)}");
                    Check(r.SourceDesc is not null && !r.SourceDesc.StartsWith(FacegenRel, StringComparison.OrdinalIgnoreCase),
                          "…and it is NOT reported as a rename — source and destination are the same path");

                    // The same claim when the caller SPELLS the source, which is the natural way to say "place this
                    // path, from that mod". Keyed on the paths differing, not on a source being named — and compared
                    // on the VFS's own key, so a case or separator variant of the destination is still the same file.
                    // A raw string compare reports a rename between one file and itself.  [RED arm]
                    foreach (var spelling in new[] { FacegenRel, FacegenRel.ToUpperInvariant(), FacegenRel.Replace('\\', '/') })
                    {
                        var same = svc.PlaceAssets(new[] { new PlaceRequest(FacegenRel, spelling, loserName) }, null, null).Results[0];
                        Check(same.Placed && same.SourceDesc is not null
                              && !same.SourceDesc.StartsWith(spelling, StringComparison.OrdinalIgnoreCase)
                              && !same.SourceDesc.StartsWith(FacegenRel, StringComparison.OrdinalIgnoreCase),
                              $"source= '{spelling}' equals the destination, so no rename prefix — {(same.Placed ? same.SourceDesc : same.Error)}");
                    }
                }

                // I9 — the pole token is matched case-insensitively, like the provider names beside it.
                {
                    var o = svc.PlaceAssets(new[] { new PlaceRequest(FacegenRel, FacegenRel, "*WINNER") }, null, null);
                    var r = o.Results[0];
                    var placed = o.ModFolder is null ? null : Path.Combine(o.ModFolder, FacegenRel);
                    Check(r.Placed && placed is not null && File.ReadAllBytes(placed).SequenceEqual(winnerBytes),
                          $"the winner token is case-insensitive ('*WINNER') — {(r.Placed ? "placed" : r.Error)}");
                    var up = svc.PlaceAssets(new[] { new PlaceRequest(FacegenRel, FacegenRel, loserName.ToUpperInvariant()) }, null, null).Results[0];
                    Check(up.Placed, $"…and so is a provider name ('{loserName.ToUpperInvariant()}') — {(up.Placed ? "placed" : up.Error)}");
                }

                // I10 — the naming correction that rides on a named-provider miss. STATIC: it says how the pole is
                // spelled whatever the load order contains, which is the property the conditional winner-remedy did
                // not have. Same shape as the in_place=true correction.
                {
                    var r = svc.PlaceAssets(new[] { new PlaceRequest(FacegenRel, FacegenRel, "NoSuchModAtAll") }, null, null).Results[0];
                    Check(!r.Placed && r.Error!.Contains(WriteSentences.PlaceSourcePoleSpelling, StringComparison.Ordinal),
                          $"a named-provider miss states how the winner pole is spelled  [RED arm] — {r.Error}");
                }
            }

            // ================= K: a BSA provider named as the source pole =================
            // Every other instance in this probe writes an EMPTY sResourceArchiveList, so no fixture has ever had a
            // BSA in the VFS — the named-pole lane was proven for loose providers only, while the tool advertises a
            // BSA filename as a legal source_provider=. This one registers the committed fixture archive as a base
            // archive so the BSA branch of Describe and of the resolved read are both reached.
            Console.WriteLine();
            Console.WriteLine("--- K: source_provider= a BSA filename — the archive branch of the pole ---");
            {
                var inst = Path.Combine(root, "svc-k");
                var (mods, data, prof) = MakeInstance(inst);
                File.Copy(fixA, Path.Combine(data, "FixtureA.bsa"));          // a base archive, listed in Skyrim.ini
                var loose = Path.Combine(mods, "LooseFace");
                Directory.CreateDirectory(loose);
                var looseBytes = new byte[] { 0x4C, 0x4C };
                WriteLoose(loose, FacegenRel, looseBytes);
                File.WriteAllText(Path.Combine(loose, "Dummy.esp"), "x");
                WriteProfile(prof, new[] { "Dummy.esp" }, new[] { "*Dummy.esp" }, new[] { "+LooseFace" });
                WriteSkyrimIni(prof, "FixtureA.bsa");
                var store = new UserConfigStore(Path.Combine(root, "user-k.json"));
                using var svc = LoadOrderService.WithInstance(inst, 0, store);

                var hit = svc.AssetStatus(new[] { FacegenRel }).Results[0].Hit;
                Check(hit is not null && hit.Providers.Any(p => p.Kind == AssetKind.Bsa),
                      $"the fixture really does have a BSA provider in the VFS — providers: {string.Join(", ", hit?.Providers.Select(p => p.Source + " (" + p.Kind + ")") ?? Array.Empty<string>())}");
                Check(hit?.Winner?.Kind == AssetKind.Loose, "…and the LOOSE copy wins, so naming the BSA is naming the loser  [RED arm]");

                var bsaBytes = AssetResolver.TryReadArchiveEntry(fixA, FacegenRel)!;
                var o = svc.PlaceAssets(new[] { new PlaceRequest(FacegenRel, FacegenRel, "FixtureA.bsa") }, null, null);
                var r = o.Results[0];
                var placed = o.ModFolder is null ? null : Path.Combine(o.ModFolder, FacegenRel);
                Check(r.Placed && placed is not null && File.ReadAllBytes(placed).SequenceEqual(bsaBytes),
                      $"source_provider= a BSA filename extracts THAT archive's entry, not the winning loose copy  [RED arm] — {(r.Placed ? "placed" : r.Error)}");

                // …and the refusal's quoted token round-trips for a BSA provider too (its name carries a '.bsa'
                // extension, which is the shape most likely to be mangled by a list format).
                var refusal = svc.PlaceAssets(new[] { new PlaceRequest(FacegenRel, FacegenRel, "NopeMod") }, null, null).Results[0].Error!;
                Check(refusal.Contains("\"FixtureA.bsa\" (BSA)", StringComparison.Ordinal),
                      $"the BSA provider is listed as a delimited name with its kind outside the delimiters — {refusal}");

                // A Data-relative source that ENDS in '.bsa' is an asset path, not an archive to open. Testing the
                // extension before the qualified-path test sent exactly this to the process working directory.
                var relBsa = @"meshes\hcprobe\thing.bsa";
                WriteLoose(loose, relBsa, new byte[] { 0x7A, 0x7A });
                File.SetLastWriteTimeUtc(Path.Combine(prof, "modlist.txt"), DateTime.UtcNow.AddHours(1));
                var bo = svc.PlaceAssets(new[] { new PlaceRequest(@"meshes\hcprobe\copy.nif", relBsa, "LooseFace") }, null, null);
                var br = bo.Results[0];
                var bplaced = bo.ModFolder is null ? null : Path.Combine(bo.ModFolder, @"meshes\hcprobe\copy.nif");
                Check(br.Placed && bplaced is not null && File.ReadAllBytes(bplaced).SequenceEqual(new byte[] { 0x7A, 0x7A }),
                      $"a Data-relative source ending '.bsa' resolves through the VFS, not against the process CWD  [RED arm] — {(br.Placed ? "placed" : br.Error)}");
            }

            // ================= L: the wire field names =================
            // Every other arm builds PlaceAssetSpec with the C# initializer, so the [JsonPropertyName] a real MCP
            // caller actually sends is unexercised — a misspelled wire name would drop the parameter for every real
            // call with the suite green. This deserializes the JSON an MCP client sends and asserts the field lands.
            Console.WriteLine();
            Console.WriteLine("--- L: source_provider survives the JSON wire, under the name a client sends ---");
            {
                const string json = """
                [ { "asset_path": "meshes\\x.nif", "source": "meshes\\y.nif", "source_provider": "SomeMod" } ]
                """;
                var specs = System.Text.Json.JsonSerializer.Deserialize<PlaceAssetSpec[]>(json);
                Check(specs is { Length: 1 }, "the wire array deserializes");
                Check(specs?[0].SourceProvider == "SomeMod",
                      $"source_provider arrives under its wire name  [RED arm] — {specs?[0].SourceProvider ?? "(dropped)"}");
                Check(specs?[0].Source == @"meshes\y.nif" && specs?[0].AssetPath == @"meshes\x.nif",
                      "…and so do the sibling fields this spec pairs it with");
            }

            // ================= M: the OFF-ORDER source lane (F1, ruling O1) =================
            // Naming a mod reaches that mod's copy whether or not MO2 ticks it. TWENTY-SIX CELLS, enumerated
            // rather than inferred: the lane crosses two tools × two source shapes × two provider kinds, and its
            // whole risk is what it must NOT reach — so the not-widened poles get cells of their own, not an
            // argument. M17–M24 came from review round 1 measuring eight cells empty; M25–M26 from round 2 measuring two more.
            Console.WriteLine();
            Console.WriteLine("--- M: source_provider= names a mod MO2 does not tick — served off disk, and nothing else widens ---");
            {
                var inst = Path.Combine(root, "svc-m");
                var (mods, data, prof) = MakeInstance(inst);

                const string OnlyDisabled = @"meshes\hcprobe\only-disabled.nif";
                const string Shared       = @"meshes\hcprobe\shared.nif";
                const string Contended    = @"meshes\hcprobe\contended.nif";
                const string DataName     = "Data";       // a LOOSE-ROOT name, so a mods\Data folder can collide with it

                var eShared     = new byte[] { 0xE1, 0xE1, 0xE1, 0xE1 };
                var dShared     = new byte[] { 0xD1, 0xD1 };
                var dOnly       = new byte[] { 0xD0, 0xD0, 0xD0 };
                var dFaceLoose  = new byte[] { 0xDF, 0xDF, 0xDF, 0xDF, 0xDF };
                var e1Contended = new byte[] { 0xC1 };
                var e2Contended = new byte[] { 0xC2, 0xC2 };
                var dataGame    = new byte[] { 0x6A, 0x6A, 0x6A };
                var dataFolder  = new byte[] { 0x7B, 0x7B };
                const string DataCollision = @"meshes\hcprobe\data-collision.nif";

                // ENABLED
                var e1 = Path.Combine(mods, "Enabled1"); Directory.CreateDirectory(e1);
                WriteLoose(e1, Shared, eShared);
                WriteLoose(e1, Contended, e1Contended);
                File.WriteAllText(Path.Combine(e1, "Dummy.esp"), "x");
                var e2 = Path.Combine(mods, "Enabled2"); Directory.CreateDirectory(e2);
                WriteLoose(e2, Contended, e2Contended);

                // OFF-ORDER: unticked in modlist.txt, and holding copies nothing enabled has.
                var d1 = Path.Combine(mods, "Disabled1"); Directory.CreateDirectory(d1);
                WriteLoose(d1, OnlyDisabled, dOnly);
                WriteLoose(d1, Shared, dShared);
                WriteLoose(d1, Contended, new byte[] { 0xDC });
                WriteLoose(d1, FacegenRel, dFaceLoose);
                File.Copy(fixA, Path.Combine(d1, "Disabled1.bsa"));          // loose AND archive, for the lane's own order
                var d2 = Path.Combine(mods, "Disabled2"); Directory.CreateDirectory(d2);
                File.Copy(fixA, Path.Combine(d2, "Disabled2.bsa"));          // archive ONLY — the BSA branch of the lane

                // The reserved-name collision: a mods\Data folder beside the game Data folder, both supplying one path.
                var dFolder = Path.Combine(mods, DataName); Directory.CreateDirectory(dFolder);
                WriteLoose(dFolder, DataCollision, dataFolder);
                WriteLoose(data, DataCollision, dataGame);

                WriteProfile(prof, new[] { "Dummy.esp" }, new[] { "*Dummy.esp" },
                             new[] { "+Enabled1", "+Enabled2", "-Disabled1", "-Disabled2" });
                WriteSkyrimIni(prof, "");
                var store = new UserConfigStore(Path.Combine(root, "user-m.json"));
                using var svc = LoadOrderService.WithInstance(inst, 0, store);
                var archBytes = AssetResolver.TryReadArchiveEntry(fixA, FacegenRel)!;

                // The fixture's premise, MEASURED rather than assumed — if the "off-order" copies were visible to the
                // active universe, every cell below would pass for the wrong reason.
                var pre = svc.AssetStatus(new[] { OnlyDisabled, FacegenRel, Shared, Contended }).Results;
                Check(pre[0].Hit is { Exists: false } && pre[1].Hit is { Exists: false },
                      $"the off-order copies are INVISIBLE to the active universe (only-disabled={pre[0].Hit?.Exists}, facegen={pre[1].Hit?.Exists})");
                Check(pre[2].Hit?.Providers.Count == 1 && pre[3].Hit?.Providers.Count == 2,
                      $"…and the enabled universe supplies shared once and contended twice ({pre[2].Hit?.Providers.Count}, {pre[3].Hit?.Providers.Count})");
                Check(archBytes is not null && !archBytes.SequenceEqual(dFaceLoose),
                      "…and Disabled1's loose facegen differs from the copy in its own archive, so the lane's order is testable");

                string? PlacedAt(PlaceOutcome o, string rel) =>
                    o.ModFolder is null ? null : (File.Exists(Path.Combine(o.ModFolder, rel)) ? Path.Combine(o.ModFolder, rel) : null);

                // ---- M1: place_asset, NO source=, off-order LOOSE ----
                {
                    var o = svc.PlaceAssets(new[] { new PlaceRequest(OnlyDisabled, null, "Disabled1") }, null, null);
                    var p = PlacedAt(o, OnlyDisabled);
                    Check(o.Results[0].Placed && p is not null && File.ReadAllBytes(p).SequenceEqual(dOnly),
                          $"M1  no source=: naming an unticked mod places ITS copy of the destination path  [RED arm] — {(o.Results[0].Placed ? "placed" : o.Results[0].Error)}");
                }

                // ---- M2: place_asset, source= a DIFFERENT path — the rename the appearance carry is built from ----
                {
                    var dest = @"meshes\hcprobe\renamed.nif";
                    var o = svc.PlaceAssets(new[] { new PlaceRequest(dest, OnlyDisabled, "Disabled1") }, null, null);
                    var p = PlacedAt(o, dest);
                    Check(o.Results[0].Placed && p is not null && File.ReadAllBytes(p).SequenceEqual(dOnly),
                          $"M2  source= a different path: the unticked mod's bytes land under the DESTINATION name  [RED arm] — {(o.Results[0].Placed ? "placed" : o.Results[0].Error)}");
                }

                // ---- M3: the folder's ROOT ARCHIVE — the capability a path guess cannot reach ----
                {
                    var o = svc.PlaceAssets(new[] { new PlaceRequest(FacegenRel, FacegenRel, "Disabled2") }, null, null);
                    var p = PlacedAt(o, FacegenRel);
                    Check(o.Results[0].Placed && p is not null && File.ReadAllBytes(p).SequenceEqual(archBytes),
                          $"M3  a copy that exists ONLY inside the unticked mod's root .bsa is extracted  [RED arm] — {(o.Results[0].Placed ? "placed" : o.Results[0].Error)}");
                }

                // ---- M4/M5: the BULK tool's own door, both source shapes. The scalar cells above prove the service
                // policy and say nothing about whether bulk threads the per-asset pole into it. ----
                {
                    var t4 = PlaceAssetTools.BulkPlaceAsset(svc, new[]
                        { new PlaceAssetSpec { AssetPath = OnlyDisabled, SourceProvider = "Disabled1" } }, "MBulkNoSrc");
                    var f4 = PlacedFileFrom(t4, mods, OnlyDisabled);
                    Check(f4 is not null && File.ReadAllBytes(f4).SequenceEqual(dOnly),
                          $"M4  bulk_place_asset, no source=: the unticked mod's copy  [RED arm] — {Trim1(t4)}");

                    var t5 = PlaceAssetTools.BulkPlaceAsset(svc, new[]
                        { new PlaceAssetSpec { AssetPath = OnlyDisabled, Source = OnlyDisabled, SourceProvider = "Disabled1" } }, "MBulkSrc");
                    var f5 = PlacedFileFrom(t5, mods, OnlyDisabled);
                    Check(f5 is not null && File.ReadAllBytes(f5).SequenceEqual(dOnly),
                          $"M5  bulk_place_asset, with source=: same  [RED arm] — {Trim1(t5)}");
                }

                // ---- M6: PROVENANCE. Bytes out of a mod the game is not loading look identical on the OK line;
                // the response has to say which. Counted, not Contains-ed: the destination's own enable+sort
                // instruction is a DIFFERENT claim and duplicating either one is the #271 class. ----
                {
                    var text = PlaceAssetTools.PlaceAsset(svc, asset_path: OnlyDisabled, source_provider: "Disabled1", patch_name: "MProv");
                    int said = System.Text.RegularExpressions.Regex.Matches(text, "NOT enabled in MO2").Count;
                    int enableSort = System.Text.RegularExpressions.Regex.Matches(text, "\"wrote it\" is not \"it wins\"").Count;
                    Check(said == 1, $"M6  the render SAYS the source mod is not enabled — exactly once  [RED arm] — said={said}");
                    Check(text.Contains("Disabled1", StringComparison.Ordinal), "…naming the mod it read from");
                    Check(enableSort == 1, $"…and the destination's own enable+sort instruction is still said exactly once, not duplicated — {enableSort}");

                    var enabledText = PlaceAssetTools.PlaceAsset(svc, asset_path: Shared, source_provider: "Enabled1", patch_name: "MProvEnabled");
                    // PAIRED with the positive. A bare negative is satisfied by any refusal render, so on its own it
                    // would pass for a call that placed nothing at all (round 1).
                    var enabledFile = PlacedFileFrom(enabledText, mods, Shared);
                    Check(enabledFile is not null && File.ReadAllBytes(enabledFile).SequenceEqual(eShared)
                          && !enabledText.Contains("NOT enabled in MO2", StringComparison.Ordinal),
                          $"…and a read served by the ACTIVE universe PLACES and says no such thing  [RED arm] — {Trim1(enabledText)}");
                }

                // ---- M7: UNIVERSE FIRST. 'Data' is a loose-root name AND a folder under mods\ here. The universe
                // answers, so no enabled name changes behaviour and a disk folder cannot shadow one. ----
                {
                    var o = svc.PlaceAssets(new[] { new PlaceRequest(@"meshes\hcprobe\data-copy.nif", DataCollision, DataName) }, null, null);
                    var p = PlacedAt(o, @"meshes\hcprobe\data-copy.nif");
                    Check(o.Results[0].Placed && p is not null && File.ReadAllBytes(p).SequenceEqual(dataGame),
                          $"M7  a name the built universe knows is served by the UNIVERSE, not by a mods\\ folder of that name  [RED arm] — {(o.Results[0].Placed ? "placed" : o.Results[0].Error)}");
                    Check(p is not null && !File.ReadAllBytes(p).SequenceEqual(dataFolder),
                          "…and specifically not the folder's copy (the two differ, so the assertion can fail)");
                    Check(o.Results[0].SourceOffOrderProvider is null,
                          $"…and it is not reported as an off-order read — {o.Results[0].SourceOffOrderProvider ?? "(none)"}");
                }

                // ---- M8/M9: the two named-provider misses. ONE sentence, and the candidate list appears only when
                // there are candidates — an always-printed "pass one of these names" with nothing after it is #380's
                // empty remedy. ----
                {
                    var r8 = svc.PlaceAssets(new[] { new PlaceRequest(OnlyDisabled, OnlyDisabled, "NoSuchModAnywhere") }, null, null).Results[0];
                    Check(!r8.Placed && r8.Error!.Contains("NoSuchModAnywhere", StringComparison.Ordinal)
                          && r8.Error.Contains(WriteSentences.PlaceSourceNoSuchFolder, StringComparison.Ordinal),
                          $"M8  a name with no folder anywhere is refused NAMING it, and says there is no such folder  [RED arm] — {r8.Error}");
                    Check(!r8.Error!.Contains("pass one of these names", StringComparison.Ordinal),
                          $"…and offers NO candidate list, because there are no candidates to offer  [RED arm] — {r8.Error}");

                    var r9 = svc.PlaceAssets(new[] { new PlaceRequest(Contended, Contended, "NoSuchModAnywhere") }, null, null).Results[0];
                    Check(!r9.Placed && r9.Error!.Contains(WriteSentences.PlaceSourceNoSuchFolder, StringComparison.Ordinal)
                          && r9.Error.Contains("pass one of these names", StringComparison.Ordinal),
                          $"M9  the same miss on a path OTHERS supply lists them as the remedy  [RED arm] — {r9.Error}");
                    Check(r9.Error!.Contains("\"Enabled1\"", StringComparison.Ordinal) && r9.Error.Contains("\"Enabled2\"", StringComparison.Ordinal),
                          "…naming exactly the enabled providers");
                }

                // ---- M10–M12: NOT widened. Naming is the consent, so the omitted-provider lane, the winner pole and
                // the contention listing all stay inside the active universe.
                //
                // What these three are red to, stated honestly: no edit INSIDE the off-order lane can break them,
                // because that lane is reachable only through the Named pole and never contributes to res.Sources.
                // They are red to a RESOLVER-level widening — an unticked mod entering the built universe — which is
                // the regression class worth holding, and the sweep reddens them by ticking Disabled1 in the fixture.
                //
                // M10: a path only an unticked mod has still has no copy to auto-place. ----
                {
                    var r = svc.PlaceAssets(new[] { new PlaceRequest(OnlyDisabled, null, null) }, null, null).Results[0];
                    Check(!r.Placed && r.Error!.Contains("no copy to auto-place", StringComparison.Ordinal),
                          $"M10 with NO source_provider=, an unticked mod's copy is NOT auto-resolved  [RED arm] — {r.Error}");
                }

                // ---- M11: NOT widened — *winner is the ACTIVE winner. An unticked mod is not in the VFS and can
                // never be what "the game shows right now" means. ----
                {
                    var o = svc.PlaceAssets(new[] { new PlaceRequest(@"meshes\hcprobe\winner-copy.nif", Shared, AssetSourceChoice.WinnerToken) }, null, null);
                    var p = PlacedAt(o, @"meshes\hcprobe\winner-copy.nif");
                    Check(o.Results[0].Placed && p is not null && File.ReadAllBytes(p).SequenceEqual(eShared),
                          $"M11 *winner places the ACTIVE winner's bytes on a path an unticked mod also supplies  [RED arm] — {(o.Results[0].Placed ? "placed" : o.Results[0].Error)}");
                    Check(p is not null && !File.ReadAllBytes(p).SequenceEqual(dShared), "…and specifically not the unticked mod's");
                }

                // ---- M12: NOT widened — the contention listing is the ACTIVE universe's. An unticked mod that
                // happens to hold the path must never appear as a provider to choose between. ----
                {
                    var r = svc.PlaceAssets(new[] { new PlaceRequest(Contended, Contended, null) }, null, null).Results[0];
                    Check(!r.Placed && r.Error!.Contains("2 providers", StringComparison.Ordinal),
                          $"M12 the ambiguity refusal counts the ACTIVE providers only  [RED arm] — {r.Error}");
                    Check(!r.Error!.Contains("Disabled1", StringComparison.Ordinal),
                          "…and never names the unticked mod that also holds the path");
                }

                // ---- M13: the ESCAPE guard. A provider name is joined to mods\, so it is the other half of the
                // path validation — a name carrying a separator, a '..' or a drive must not address a file outside. ----
                {
                    // EVERY spelling here has to REACH something, or the cell passes on a resolver that escapes and
                    // merely finds nothing there. Round 2 caught '.' passing for exactly that reason, so all three
                    // targets are now written: one level ABOVE mods\ for the '..' forms, and the mods ROOT itself for
                    // '.' (mods\ is not a mod folder, so serving out of it would be just as wrong).
                    var outsider = Path.Combine(inst, "outsider");
                    Directory.CreateDirectory(outsider);
                    WriteLoose(outsider, OnlyDisabled, new byte[] { 0x66, 0x66, 0x66, 0x66, 0x66, 0x66 });
                    WriteLoose(inst, OnlyDisabled, new byte[] { 0x67, 0x67, 0x67, 0x67, 0x67 });     // the '..' target
                    WriteLoose(mods, OnlyDisabled, new byte[] { 0x68, 0x68, 0x68, 0x68 });           // the '.' target
                    File.SetLastWriteTimeUtc(Path.Combine(prof, "modlist.txt"), DateTime.UtcNow.AddHours(1));
                    // '..' and '.' are caught TWICE — by the explicit clause and by the trailing-dot rule, since both
                    // end in '.'. Defence in depth on a path-escape guard is worth keeping, and the consequence is
                    // that these two cells are red only to removing BOTH, the way M7 is red only to the compound.
                    foreach (var bad in new[] { @"..\outsider", "../outsider", @"Disabled1\..\Disabled1", outsider, ".", ".." })
                    {
                        var r = svc.PlaceAssets(new[] { new PlaceRequest(OnlyDisabled, OnlyDisabled, bad) }, null, null).Results[0];
                        Check(!r.Placed, $"M13 a provider name that is a PATH ('{bad}') places nothing  [RED arm] — {(r.Placed ? "PLACED" : "refused")}");
                        // …and the REFUSAL says the right thing. The arm stopped at !Placed, so it passed while the
                        // sentence beside it called a drive-rooted path a name the load order already provides files
                        // under (review round 2). A path-shaped name is its own outcome now.
                        Check(!r.Placed && r.Error!.Contains(WriteSentences.PlaceSourceNotAFolderName, StringComparison.Ordinal),
                              $"…and is refused AS a path-shaped name, not as a universe name  [RED arm] — {r.Error}");
                    }
                }

                // ---- M14: the lane's OWN order — loose first, then the folder's archives, which is the ancestor's
                // donor-disk shape. Disabled1 holds both copies of the facegen path. ----
                {
                    var o = svc.PlaceAssets(new[] { new PlaceRequest(FacegenRel, FacegenRel, "Disabled1") }, null, null);
                    var p = PlacedAt(o, FacegenRel);
                    Check(o.Results[0].Placed && p is not null && File.ReadAllBytes(p).SequenceEqual(dFaceLoose),
                          $"M14 the folder's LOOSE copy beats its own root archive  [RED arm] — {(o.Results[0].Placed ? "placed" : o.Results[0].Error)}");
                    Check(p is not null && !File.ReadAllBytes(p).SequenceEqual(archBytes), "…and specifically not the archive's");
                }

                // ---- M15: THE DISCRIMINATING CELL. A path an ENABLED mod supplies, with the UNTICKED one named:
                // the name binds, so the read is the named mod's. Every pre-F1 policy fails this one. ----
                {
                    var o = svc.PlaceAssets(new[] { new PlaceRequest(@"meshes\hcprobe\bound.nif", Shared, "Disabled1") }, null, null);
                    var p = PlacedAt(o, @"meshes\hcprobe\bound.nif");
                    Check(o.Results[0].Placed && p is not null && File.ReadAllBytes(p).SequenceEqual(dShared),
                          $"M15 naming the unticked mod reads ITS copy though an enabled mod supplies the path too  [RED arm] — {(o.Results[0].Placed ? "placed" : o.Results[0].Error)}");
                    Check(p is not null && !File.ReadAllBytes(p).SequenceEqual(eShared), "…and specifically not the enabled mod's");
                    Check(o.Results[0].SourceOffOrderProvider == "Disabled1",
                          $"…and the result carries the off-order provenance the render prints — {o.Results[0].SourceOffOrderProvider ?? "(none)"}");
                }

                // ---- M16: the cell M7 cannot reach. M7 passes on the universe's own match, so it says nothing
                // about the gate; this is the case where the universe name exists but does NOT supply the path, and
                // a mods\ folder of the same name does. Without the gate, 'Data' would quietly mean two different
                // providers depending on the path — which is the shadowing the ruled universe-first shape forbids. ----
                {
                    const string FolderOnly = @"meshes\hcprobe\data-folder-only.nif";
                    WriteLoose(Path.Combine(mods, DataName), FolderOnly, new byte[] { 0x5C, 0x5C });
                    File.SetLastWriteTimeUtc(Path.Combine(prof, "modlist.txt"), DateTime.UtcNow.AddHours(1));
                    // The folder copy is genuinely there — else "refused" proves nothing about the gate.
                    Check(File.Exists(Path.Combine(mods, DataName, FolderOnly)), "…the mods\\Data copy really is on disk");
                    var r = svc.PlaceAssets(new[] { new PlaceRequest(FolderOnly, FolderOnly, DataName) }, null, null).Results[0];
                    Check(!r.Placed,
                          $"M16 a name the universe knows NEVER falls through to a mods\\ folder of that name  [RED arm] — {(r.Placed ? "PLACED from the folder" : "refused")}");
                    // …and the REFUSAL must not claim a search the gate prevented. This arm stopped at !Placed, so it
                    // passed while the sentence beside it told the caller houseCARL had looked in that very folder.
                    Check(!r.Placed && r.Error!.Contains(WriteSentences.PlaceSourceUniverseName, StringComparison.Ordinal)
                          && !r.Error.Contains(WriteSentences.PlaceSourceDiskFolderSearched, StringComparison.Ordinal),
                          $"…and SAYS the name is one the active order already provides, claiming no folder search  [RED arm] — {r.Error}");
                }

                // ---- M17: the class three reviewers found in round 1. Naming an ENABLED mod that does not supply
                // the path must not claim its folder was searched — the gate answered first, so no folder was
                // opened. This is the commonest shape of all (the appearance skill tells callers to name the donor
                // MOD), and the sentence was unconditional. ----
                {
                    var r = svc.PlaceAssets(new[] { new PlaceRequest(OnlyDisabled, OnlyDisabled, "Enabled1") }, null, null).Results[0];
                    Check(!r.Placed && r.Error!.Contains(WriteSentences.PlaceSourceUniverseName, StringComparison.Ordinal),
                          $"M17 naming an ENABLED mod that lacks the path says only that the name is already provided  [RED arm] — {r.Error}");
                    Check(!r.Error!.Contains(WriteSentences.PlaceSourceDiskFolderSearched, StringComparison.Ordinal),
                          "…and claims NO mod-folder search, because the gate stopped one from happening  [RED arm]");
                }

                // ---- M18: PROVENANCE ON THE ARCHIVE BRANCH. M6 and M15 both assert it on a LOOSE read, so
                // dropping the flag from the archive branch alone shipped green through a whole sabotage sweep. ----
                {
                    var text = PlaceAssetTools.PlaceAsset(svc, asset_path: FacegenRel, source: FacegenRel,
                                                          source_provider: "Disabled2", patch_name: "MArchProv");
                    var f = PlacedFileFrom(text, mods, FacegenRel);
                    Check(f is not null && File.ReadAllBytes(f).SequenceEqual(archBytes),
                          $"M18 an archive-served off-order read places the archive's bytes — {Trim1(text)}");
                    Check(text.Contains("NOT enabled in MO2", StringComparison.Ordinal) && text.Contains("Disabled2", StringComparison.Ordinal),
                          $"…and SAYS so, naming the mod — the branch the loose cells cannot reach  [RED arm] — {Trim1(text)}");
                }

                // ---- M19: the archive branch's NEGATIVE. An off-order folder that EXISTS and whose root archive
                // does NOT hold the path must refuse. Nothing reached this: every other miss cell names a folder
                // that does not exist, so the archive lookup's false answer was never exercised. ----
                {
                    var r = svc.PlaceAssets(new[] { new PlaceRequest(OnlyDisabled, OnlyDisabled, "Disabled2") }, null, null).Results[0];
                    Check(!r.Placed && r.Error!.Contains(WriteSentences.PlaceSourceDiskFolderSearched, StringComparison.Ordinal),
                          $"M19 an existing off-order folder whose archive lacks the path is REFUSED, saying it was SEARCHED  [RED arm] — {r.Error}");
                    Check(!r.Error!.Contains(WriteSentences.PlaceSourceNoSuchFolder, StringComparison.Ordinal),
                          "…and NOT that the folder is missing — the two outcomes are different sentences  [RED arm]");
                    Check(Directory.Exists(Path.Combine(mods, "Disabled2")) && !r.Error!.Contains("unscanned", StringComparison.Ordinal),
                          "…and the folder really exists, with no unreadable-archive caveat — a clean miss, not an unknown");
                }

                // ---- M20: TOP-LEVEL ONLY. A .bsa nested in a subtree is not one the engine would load for that
                // mod, so reading it would serve bytes the game never would. The whole policy was satisfied by
                // construction — no fixture had a nested archive. ----
                {
                    var nested = Path.Combine(mods, "DisabledNested", "sub", "deep");
                    Directory.CreateDirectory(nested);
                    File.Copy(fixA, Path.Combine(nested, "Nested.bsa"));
                    File.SetLastWriteTimeUtc(Path.Combine(prof, "modlist.txt"), DateTime.UtcNow.AddHours(1));
                    var r = svc.PlaceAssets(new[] { new PlaceRequest(FacegenRel, FacegenRel, "DisabledNested") }, null, null).Results[0];
                    Check(!r.Placed,
                          $"M20 an archive NESTED in the mod folder is not read — root archives only  [RED arm] — {(r.Placed ? "PLACED from a nested archive" : "refused")}");
                    Check(AssetResolver.ArchiveHasEntry(Path.Combine(nested, "Nested.bsa"), FacegenRel),
                          "…and that nested archive really does hold the path, so the refusal is the policy and not an accident");
                }

                // ---- M21: an archive that will not READ is an UNKNOWN, not a miss. Reporting it as "this mod does
                // not have it" is the silent-wrong-answer class; the active lane has carried this caveat all along
                // and the off-order lane swallowed the throw. ----
                {
                    var brokenDir = Path.Combine(mods, "DisabledBroken");
                    Directory.CreateDirectory(brokenDir);
                    File.WriteAllBytes(Path.Combine(brokenDir, "Broken.bsa"), new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });
                    File.SetLastWriteTimeUtc(Path.Combine(prof, "modlist.txt"), DateTime.UtcNow.AddHours(1));
                    var r = svc.PlaceAssets(new[] { new PlaceRequest(FacegenRel, FacegenRel, "DisabledBroken") }, null, null).Results[0];
                    Check(!r.Placed && r.Error!.Contains("unscanned rather than absent", StringComparison.Ordinal)
                          && r.Error.Contains(WriteSentences.PlaceSourceFolderUnreadable, StringComparison.Ordinal),
                          $"M21 an unreadable archive in the named folder is reported as UNKNOWN, not as absent  [RED arm] — {r.Error}");
                    Check(r.Error!.Contains("Broken.bsa", StringComparison.Ordinal),
                          "…naming the archive that could not be read");
                }

                // ---- M22: Windows strips a trailing dot when it resolves a path, so 'Data.' opens mods\Data —
                // straight around a gate that matches the name as typed. Left in, an ENABLED mod named with a
                // trailing dot would be served off disk and reported as NOT enabled: a false provenance line, which
                // is the one sentence this lane exists to keep honest. ----
                {
                    // The PATH each spelling probes is load-bearing and was wrong. Both cells used to probe
                    // DataCollision, which 'Enabled1' does not supply — so no off-order provenance line was reachable
                    // from that cell whatever the guard did, and it passed under the very sabotage it names. Each
                    // spelling now probes a path its target folder REALLY holds (round 2).
                    foreach (var (spelling, rel, why) in new[]
                             { ("Data.",     DataCollision, "walks around the universe-first gate"),
                               ("Enabled1.", Shared,        "would print a false off-order provenance line for an ENABLED mod") })
                    {
                        var r = svc.PlaceAssets(new[] { new PlaceRequest(rel, rel, spelling) }, null, null).Results[0];
                        Check(!r.Placed || r.SourceOffOrderProvider is null,
                              $"M22 a trailing-dot spelling ('{spelling}') never reaches disk — it {why}  [RED arm] — " +
                              $"{(r.Placed ? "PLACED off-order=" + (r.SourceOffOrderProvider ?? "(none)") : "refused")}");
                        Check(!r.Placed && r.Error!.Contains(WriteSentences.PlaceSourceNotAFolderName, StringComparison.Ordinal),
                              $"…and SAYS the name is path-shaped rather than one the order already provides  [RED arm] — {r.Error}");
                    }
                }

                // ---- M23: '*winner' is a non-empty selector that is NOT a provider name. Routing the merged
                // refusal on "a provider string was passed" quoted the pole back as if it were a mod, claimed a
                // folder of that name had been searched — impossible, '*' cannot be in a folder name, which is why
                // the token is sigiled — and corrected the caller toward the spelling they had just used. ----
                {
                    foreach (var (src, label) in new[] { ((string?)null, "no source="), (OnlyDisabled, "with source=") })
                    {
                        var r = svc.PlaceAssets(new[] { new PlaceRequest(OnlyDisabled, src, AssetSourceChoice.WinnerToken) }, null, null).Results[0];
                        Check(!r.Placed && !r.Error!.Contains(WriteSentences.PlaceSourceDiskFolderSearched, StringComparison.Ordinal)
                              && !r.Error.Contains(WriteSentences.PlaceSourceUniverseName, StringComparison.Ordinal),
                              $"M23 {AssetSourceChoice.WinnerToken} ({label}) is never refused as a named PROVIDER  [RED arm] — {r.Error}");
                        Check(!r.Error!.Contains($"'{AssetSourceChoice.WinnerToken}' does not supply", StringComparison.Ordinal),
                              "…and the pole is not quoted back as though it were a mod name");
                    }
                }

                // ---- M24: the #283 root-prefix hint. A path taken off a record is stored relative to meshes\, and
                // naming a provider does not stop that being the caller's real mistake — but the named-provider
                // refusal used to swallow the hint that names the fix. ----
                {
                    var rootless = Shared.Substring(@"meshes\".Length);
                    var r = svc.PlaceAssets(new[] { new PlaceRequest(rootless, rootless, "NoSuchModAnywhere") }, null, null).Results[0];
                    Check(!r.Placed && r.Error!.Contains("Did you mean", StringComparison.Ordinal)
                          && r.Error.Contains(Shared, StringComparison.OrdinalIgnoreCase),
                          $"M24 a named-provider miss still offers the verified root-prefix hint  [RED arm] — {r.Error}");
                }

                // ---- M25: the render's own ADJACENCY. The off-order provenance line ends "enabling it is not
                // required"; the line directly under it used to open "once the mod is enabled", about a different
                // mod. Read in sequence they contradicted. Nothing asserted this string, so reverting the fix left
                // the whole suite green — a prose fold with no probe is a rewording (round 2). ----
                {
                    var text = PlaceAssetTools.PlaceAsset(svc, asset_path: OnlyDisabled, source_provider: "Disabled1",
                                                          patch_name: "MAdjacency");
                    var folder = text.Split('\n').FirstOrDefault(l => l.TrimStart().StartsWith("mod folder:", StringComparison.Ordinal))
                                     ?.Trim()["mod folder:".Length..].Trim();
                    Check(folder is { Length: > 0 } && text.Contains($"once '{folder}' is enabled", StringComparison.Ordinal),
                          $"M25 the winner line names the DESTINATION folder, not a bare \"the mod\"  [RED arm] — folder={folder ?? "(none)"}");
                    Check(!text.Contains("once the mod is enabled", StringComparison.Ordinal),
                          "…so it cannot be read as the off-order source mod the line above says need not be enabled  [RED arm]");
                }

                // ---- M27: the two SILENT reasons. The refusal switch is exhaustive by the compiler, so every
                // outcome has an arm — including the two that deliberately render no clause. `Found` never reaches a
                // refusal; `NotConsulted` does, whenever a caller selects under the Named pole without supplying the
                // lookup, and there the only honest thing to say about disk is nothing at all. Driven through the
                // REAL policy and the REAL render (the write-surface guard's shape for a branch a fixture cannot
                // otherwise reach). ----
                {
                    var res = new PlacementResolution(Contended, new[]
                    {
                        new PlacementSource("Enabled1", AssetKind.Loose, @"C:\x\a.nif", null, Contended),
                        new PlacementSource("Enabled2", AssetKind.Loose, @"C:\x\b.nif", null, Contended),
                    }, Ambiguous: true, ReadIncomplete: false);
                    var pick = AssetSourceSelection.Select(res, AssetSourceChoice.Named("NoSuchModAnywhere"));
                    Check(pick.OffOrderReason == OffOrderReason.NotConsulted,
                          $"M27 the Named pole with NO lookup supplied reports NotConsulted  [RED arm] — {pick.OffOrderReason}");
                    var sentence = WriteSentences.PlaceSourceNamedAbsent(
                        "NoSuchModAnywhere", Contended, pick.ProviderNames, pick.OffOrderReason);
                    foreach (var claim in new[] { WriteSentences.PlaceSourceUniverseName,
                                                  WriteSentences.PlaceSourceNotAFolderName,
                                                  WriteSentences.PlaceSourceNoSuchFolder,
                                                  WriteSentences.PlaceSourceDiskFolderSearched,
                                                  WriteSentences.PlaceSourceFolderUnreadable })
                        Check(!sentence.Contains(claim, StringComparison.Ordinal),
                              "…and its refusal makes NO claim about the disk, having looked at none  [RED arm]");
                    Check(sentence.Contains("does not supply", StringComparison.Ordinal),
                          $"…while still saying the one thing that is true — {sentence}");
                }
            }

            // ================= M26: the ARCHIVE half of the universe-first gate =================
            // IsUniverseProviderName tests loose-root names AND active archive filenames. Arm M's fixture has NO
            // active archives at all, so deleting the archive clause left every cell green — one whole half of the
            // gate with no coverage (round 2). It gets its own instance rather than an archive in arm M's: making
            // FixtureA active there would put its facegen path INSIDE the enabled universe and move what M3, M14,
            // M18, M20 and M21 measure (#333).
            Console.WriteLine();
            Console.WriteLine("--- M26: an ACTIVE archive's filename is a universe name too — a mods folder of that name never shadows it ---");
            {
                var inst = Path.Combine(root, "svc-m26");
                var (mods, _, prof) = MakeInstance(inst);

                // Dummy.esp is active and ships Dummy.bsa beside it, so "Dummy.bsa" is an ACTIVE archive name.
                var host = Path.Combine(mods, "ArchiveHost");
                Directory.CreateDirectory(host);
                File.WriteAllText(Path.Combine(host, "Dummy.esp"), "x");
                File.Copy(fixA, Path.Combine(host, "Dummy.bsa"));

                // …and a mod FOLDER named exactly like that archive, holding a path nothing else supplies. Windows
                // allows a folder name ending '.bsa', so this collision is constructible, and the branch's own tool
                // description teaches callers to pass an archive filename here.
                const string FolderOnly = @"meshes\hcprobe\m26-folder-only.nif";
                var collide = Path.Combine(mods, "Dummy.bsa");
                Directory.CreateDirectory(collide);
                WriteLoose(collide, FolderOnly, new byte[] { 0x26, 0x26, 0x26 });

                WriteProfile(prof, new[] { "Dummy.esp" }, new[] { "*Dummy.esp" }, new[] { "+ArchiveHost" });
                WriteSkyrimIni(prof, "");
                using var svc26 = LoadOrderService.WithInstance(inst, 0, new UserConfigStore(Path.Combine(root, "user-m26.json")));

                // The premise: "Dummy.bsa" really IS an active archive here, else the gate is not what refuses.
                var providers = svc26.AssetStatus(new[] { FacegenRel }).Results[0].Hit?.Providers
                                ?? (IReadOnlyList<AssetProvider>)Array.Empty<AssetProvider>();
                Check(providers.Any(p => p.Kind == AssetKind.Bsa && p.Source == "Dummy.bsa"),
                      $"the fixture has an ACTIVE archive named Dummy.bsa — providers: {string.Join(", ", providers.Select(p => p.Source + "/" + p.Kind))}");
                Check(File.Exists(Path.Combine(collide, FolderOnly)), "…and a mods\\Dummy.bsa FOLDER really holds a path nothing else supplies");

                var r = svc26.PlaceAssets(new[] { new PlaceRequest(FolderOnly, FolderOnly, "Dummy.bsa") }, null, null).Results[0];
                Check(!r.Placed && r.SourceOffOrderProvider is null,
                      $"M26 an ACTIVE ARCHIVE's name is answered by the universe and never falls through to a folder of that name  [RED arm] — " +
                      $"{(r.Placed ? "PLACED off-order=" + (r.SourceOffOrderProvider ?? "(none)") : "refused")}");
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

    /// <summary>First line of a tool render — enough to identify an outcome in a check label without pasting a block.</summary>
    static string Trim1(string s) => s.Split('\n')[0].Trim();

    /// <summary>The on-disk path of a file placed by a TOOL-level call, taken from the render's own "mod folder:"
    /// line. Reading it back rather than rebuilding it from the patch-name convention keeps a wire arm failing only
    /// for its own claim — the folder is auto-suffixed and prefixed, and an arm that assumed the spelling would go
    /// red on a rename of the convention while the parameter it tests still worked. Null = no folder was reported.</summary>
    static string? PlacedFileFrom(string render, string modsDir, string rel)
    {
        foreach (var line in render.Split('\n'))
        {
            var t = line.Trim();
            if (!t.StartsWith("mod folder:", StringComparison.Ordinal)) continue;
            var name = t["mod folder:".Length..].Trim();
            var cut = name.IndexOf("  —", StringComparison.Ordinal);       // the in-place / enable-and-sort suffixes
            if (cut >= 0) name = name[..cut].Trim();
            var p = Path.Combine(modsDir, name, rel);
            return File.Exists(p) ? p : null;
        }
        return null;
    }

    static void WriteLoose(string baseDir, string rel, byte[] bytes)
    {
        var p = Path.Combine(baseDir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, bytes);
    }
}
