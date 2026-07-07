using System.Linq;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// STANDALONE-NPC-COPY guard (housecarl_copy_npc_appearance — capability chain Stage 3). Drives the REAL
/// <see cref="LoadOrderService.CopyNpcAppearance"/> over a synthetic MO2 instance with a DISABLED donor (the realistic
/// case the chain exists for), pinning the empirical MUSTs the 2026-07-01 build test paid for:
///   APPLY      — donor appearance onto an existing active NPC: the closure internalizes the donor's HDPT/TXST/CLFM
///                under NEW keys with EditorIDs PRESERVED (facegeom block-name identity), HDPT.Parts (.tri morph refs —
///                the lip-sync layer) arrive on the copy, ExtraParts/TextureSet links are REMAPPED to the copies, a link
///                into an ACTIVE master is KEPT (not internalized), TintLayers/TextureLighting carry the donor's exact
///                values (the dark-skin default trap), and the DONOR IS NOT A MASTER of the patch (the standalone claim).
///   ASSETS     — the facegen pair is RENAMED to the TARGET's FormKey path (folder = the target's defining plugin), the
///                geom's embedded texture (byte-scraped) and the record-referenced model are carried from the disabled
///                donor's folder, and an unresolvable referenced path is a NAMED miss, never silent (Q3).
///   CLONE      — a full standalone clone: new EditorID/Name, appearance remapped, and the donor-internal NON-appearance
///                links (a faction membership, a default outfit) STRIPPED with each strip NAMED in the outcome.
///   REFUSALS   — both/neither target modes; a donor formid the active order can't see (the message routes to
///                source_plugin=); a non-NPC donor; a donor-internal custom RACE (out of scope, named).
/// Run: dotnet run --project src/housecarl-generator -- copy-npc-appearance-guard
/// </summary>
public static class NpcCopyProbe
{
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("################  STANDALONE-NPC-COPY guard (housecarl_copy_npc_appearance)  ################");
        Console.WriteLine();
        int fail = 0;
        void Check(bool c, string label) { Console.WriteLine((c ? "  PASS  " : "  FAIL  ") + label); if (!c) fail++; }

        var root = Path.Combine(Path.GetTempPath(), "hc-npc-copy-guard-" + Guid.NewGuid().ToString("N"));
        try
        {
            // ================= shared fixture: active master + active follower scaffold + DISABLED donor =================
            var inst = Path.Combine(root, "inst");
            var (mods, prof) = MakeInstance(inst);

            var keepKey = new ModKey("HcKeepMe", ModType.Master);
            var raceFk = new FormKey(keepKey, 0x800);
            var keptTxstFk = new FormKey(keepKey, 0x801);
            var keepMod = new SkyrimMod(keepKey, SkyrimRelease.SkyrimSE);
            keepMod.Races.Add(new Race(raceFk, SkyrimRelease.SkyrimSE) { EditorID = "HcRace" });
            keepMod.TextureSets.Add(new TextureSet(keptTxstFk, SkyrimRelease.SkyrimSE) { EditorID = "HcKeptTex" });
            WriteModDirect(mods, "KeepMod", keepMod);

            var folKey = new ModKey("MyFollower", ModType.Plugin);
            var targetFk = new FormKey(folKey, 0x800);
            var target2Fk = new FormKey(folKey, 0x801);
            var folMod = new SkyrimMod(folKey, SkyrimRelease.SkyrimSE);
            var saela = new Npc(targetFk, SkyrimRelease.SkyrimSE) { EditorID = "Saela" };
            saela.Race.SetTo(raceFk);
            folMod.Npcs.Add(saela);
            var saela2 = new Npc(target2Fk, SkyrimRelease.SkyrimSE) { EditorID = "Saela2" };
            saela2.Race.SetTo(raceFk);
            folMod.Npcs.Add(saela2);
            WriteModDirect(mods, "FollowerMod", folMod, keepMod);

            // a fixture "Skyrim.esm" — the BASE-GAME-donor lane (Implicits BaseMasters match by name); its NPC's
            // race + headpart live in itself, and none of that may classify as donor-internal.
            var vanKey = new ModKey("Skyrim", ModType.Master);
            var vanNpcFk = new FormKey(vanKey, 0x900);
            var vanMod = new SkyrimMod(vanKey, SkyrimRelease.SkyrimSE);
            vanMod.Races.Add(new Race(new FormKey(vanKey, 0x901), SkyrimRelease.SkyrimSE) { EditorID = "VanRace" });
            var vanHp = new HeadPart(new FormKey(vanKey, 0x902), SkyrimRelease.SkyrimSE) { EditorID = "VanHairHP" };
            vanMod.HeadParts.Add(vanHp);
            var vanNpc = new Npc(vanNpcFk, SkyrimRelease.SkyrimSE) { EditorID = "VanDonor" };
            vanNpc.Race.SetTo(new FormKey(vanKey, 0x901));
            vanNpc.HeadParts.Add(vanHp.FormKey);
            vanMod.Npcs.Add(vanNpc);
            WriteModDirect(mods, "FakeVanilla", vanMod);

            // an active mod already providing Saela2's facegen DESTINATION path — the precedence-warning arm.
            WriteFixture(Path.Combine(mods, "ContestMod"),
                $@"meshes\actors\character\facegendata\facegeom\MyFollower.esp\00000801.nif", new byte[] { 0xAA });

            var donorKey = new ModKey("Donor", ModType.Plugin);
            var donorNpcFk = new FormKey(donorKey, 0xD62);
            var texFk = new FormKey(donorKey, 0xD00);
            var hairFk = new FormKey(donorKey, 0xD01);
            var hairlineFk = new FormKey(donorKey, 0xD02);
            var colorFk = new FormKey(donorKey, 0xD03);
            var factionFk = new FormKey(donorKey, 0xD04);
            var outfitFk = new FormKey(donorKey, 0xD05);
            var badRaceFk = new FormKey(donorKey, 0xD06);
            var badNpcFk = new FormKey(donorKey, 0xD07);

            var donorMod = new SkyrimMod(donorKey, SkyrimRelease.SkyrimSE);
            donorMod.TextureSets.Add(new TextureSet(texFk, SkyrimRelease.SkyrimSE) { EditorID = "VivTex" });
            var hairline = new HeadPart(hairlineFk, SkyrimRelease.SkyrimSE) { EditorID = "VivHairlineHP" };
            hairline.Parts.Add(new Part { PartType = Part.PartTypeEnum.Tri, FileName = @"Actors\Character\Character Assets\FemaleHead.tri" });
            donorMod.HeadParts.Add(hairline);
            var hair = new HeadPart(hairFk, SkyrimRelease.SkyrimSE) { EditorID = "VivHairHP" };
            hair.TextureSet.SetTo(texFk);
            hair.ExtraParts.Add(hairlineFk);
            hair.Parts.Add(new Part { PartType = Part.PartTypeEnum.Tri, FileName = @"Actors\Character\Character Assets\FemaleHead.tri" });
            hair.Parts.Add(new Part { PartType = Part.PartTypeEnum.RaceMorph, FileName = @"Actors\Character\Character Assets\FemaleHeadRaces.tri" });
            hair.Model = new Model { File = @"actors\character\viv\hair.nif" };
            donorMod.HeadParts.Add(hair);
            donorMod.Colors.Add(new ColorRecord(colorFk, SkyrimRelease.SkyrimSE) { EditorID = "VivColor" });
            donorMod.Factions.Add(new Faction(factionFk, SkyrimRelease.SkyrimSE) { EditorID = "VivFaction" });
            donorMod.Outfits.Add(new Outfit(outfitFk, SkyrimRelease.SkyrimSE) { EditorID = "VivOutfit" });
            donorMod.Races.Add(new Race(badRaceFk, SkyrimRelease.SkyrimSE) { EditorID = "VivRace" });

            var classFk = new FormKey(donorKey, 0xD08);
            var classedNpcFk = new FormKey(donorKey, 0xD09);
            var templatedNpcFk = new FormKey(donorKey, 0xD0A);
            donorMod.Classes.Add(new Class(classFk, SkyrimRelease.SkyrimSE) { EditorID = "VivClass" });

            var viv = new Npc(donorNpcFk, SkyrimRelease.SkyrimSE) { EditorID = "Viv", Name = "Vivace" };
            viv.Configuration.Flags |= NpcConfiguration.Flag.Female;      // gender-fitted appearance — the match-to-donor arm
            viv.Race.SetTo(raceFk);
            viv.HeadParts.Add(hairFk);
            viv.HairColor.SetTo(colorFk);
            viv.HeadTexture.SetTo(keptTxstFk);                             // kept link — resolves via the ACTIVE master
            viv.TextureLighting = System.Drawing.Color.FromArgb(255, 246, 237, 235);
            viv.TintLayers.Add(new TintLayer { Index = 3, InterpolationValue = 1.0f, Color = System.Drawing.Color.FromArgb(255, 90, 60, 50) });
            viv.TintLayers.Add(new TintLayer { Index = 7, InterpolationValue = 0.5f });
            viv.Weight = 42f;
            viv.Factions.Add(new RankPlacement { Faction = factionFk.ToLink<IFactionGetter>(), Rank = 0 });   // clone-strip candidate
            viv.DefaultOutfit.SetTo(outfitFk);                                                                 // clone-strip candidate
            donorMod.Npcs.Add(viv);

            var badNpc = new Npc(badNpcFk, SkyrimRelease.SkyrimSE) { EditorID = "VivBadRace" };
            badNpc.Race.SetTo(badRaceFk);                                  // donor-internal custom race → the named refusal
            donorMod.Npcs.Add(badNpc);
            var classedNpc = new Npc(classedNpcFk, SkyrimRelease.SkyrimSE) { EditorID = "VivClassed" };
            classedNpc.Race.SetTo(raceFk);
            classedNpc.Class.SetTo(classFk);                               // donor-internal REQUIRED link → clone must refuse, never null it
            donorMod.Npcs.Add(classedNpc);
            var templatedNpc = new Npc(templatedNpcFk, SkyrimRelease.SkyrimSE) { EditorID = "VivTemplated" };
            templatedNpc.Race.SetTo(raceFk);
            templatedNpc.Template.SetTo(donorNpcFk);                       // appearance lives on the template → refusal
            templatedNpc.Configuration.TemplateFlags |= NpcConfiguration.TemplateFlag.Traits;
            donorMod.Npcs.Add(templatedNpc);
            WriteModDirect(mods, "DonorMod", donorMod, keepMod);

            // donor-only loose assets: the facegen pair (geom embeds a texture path for the scrape), the scraped
            // texture, and the record-referenced hair model.
            var donorDir = Path.Combine(mods, "DonorMod");
            var geomRel = $@"meshes\actors\character\facegendata\facegeom\Donor.esp\00000D62.nif";
            var tintRel = $@"textures\actors\character\facegendata\facetint\Donor.esp\00000D62.dds";
            var skinRel = $@"textures\actors\viv\skin.dds";
            var hairRel = $@"meshes\actors\character\viv\hair.nif";
            var geomBytes = new byte[] { 1, 2, 3, 0 }
                .Concat(System.Text.Encoding.ASCII.GetBytes(skinRel)).Concat(new byte[] { 0, 9, 8 }).ToArray();
            WriteFixture(donorDir, geomRel, geomBytes);
            WriteFixture(donorDir, tintRel, new byte[] { 0xDD, 0x51 });
            WriteFixture(donorDir, skinRel, new byte[] { 0x51, 0x4B });
            WriteFixture(donorDir, hairRel, new byte[] { 0x4E, 0x1F });

            WriteProfile(prof,
                loadorder: new[] { vanKey.FileName.String, keepKey.FileName.String, folKey.FileName.String },
                plugins: new[] { "*" + vanKey.FileName, "*" + keepKey.FileName, "*" + folKey.FileName },
                modlist: new[] { "+ContestMod", "+FollowerMod", "+KeepMod", "+FakeVanilla", "-DonorMod" });
            WriteSkyrimIni(prof);

            using var svc = LoadOrderService.WithInstance(inst, 0, new UserConfigStore(Path.Combine(root, "user.json")));
            svc.Stats();

            // ================= 1. APPLY — disabled donor's appearance onto the active follower =================
            {
                var o = svc.CopyNpcAppearance(donorNpcFk.ToString(), "Donor.esp", null,
                                              targetFk.ToString(), null, null, "SaelaFace", null);
                Check(o.Success, $"apply: succeeds ({o.Error})");
                Check(o.Success && o.Mode == "apply" && o.DonorOutOfLoadOrder, "apply: mode=apply, donor stamped OUT-OF-LOAD-ORDER");
                Check(o.Success && o.Internalized.Count == 4, $"apply: internalizes exactly the donor subtree — 2 HDPT + TXST + CLFM (got {o.Internalized.Count})");
                Check(o.Success && o.Internalized.All(r => r.NewKey.ModKey.FileName.String == "SaelaFace.esp"), "apply: every copy lands under the patch's own new FormIDs");
                Check(o.Success && !o.DonorAmongMasters && !o.Masters.Contains("Donor.esp", StringComparer.OrdinalIgnoreCase),
                    "apply: THE STANDALONE CLAIM — the donor is NOT among the patch masters");
                Check(o.Success && o.Masters.Contains("HcKeepMe.esm", StringComparer.OrdinalIgnoreCase),
                    "apply: the kept active-master link IS mastered (HcKeepMe.esm)");

                if (o.Success)
                {
                    using var pd = OpenDisposable(o.OutPath!, out var patch);
                    var npc = patch.Npcs.FirstOrDefault(n => n.FormKey == targetFk);
                    Check(npc is not null, "apply: the patch overrides the target NPC");
                    if (npc is not null)
                    {
                        var patchKey = ModKey.FromFileName("SaelaFace.esp");
                        Check(npc.HeadParts.Count == 1 && npc.HeadParts[0].FormKey.ModKey == patchKey,
                            "apply: HeadParts remapped to the internalized copy");
                        Check(npc.HeadTexture is { IsNull: false } htl && htl.FormKey == keptTxstFk,
                            "apply: the ACTIVE-master link is KEPT, not internalized");
                        Check(npc.TintLayers.Count == 2 && npc.TintLayers[0].Index == 3,
                            "apply: TintLayers carry the donor's layers");
                        Check(npc.TextureLighting is { } tl && tl.R == 246 && tl.G == 237 && tl.B == 235,
                            "apply: TextureLighting carries the donor's value (the dark-skin default trap)");
                        Check(Math.Abs(npc.Weight - 42f) < 0.001, "apply: Weight carries");
                        Check(npc.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female),
                            "apply: the GENDER flag matches the donor (headparts/facegen are gender-fitted)");
                        Check(npc.HairColor is { IsNull: false } hcl && hcl.FormKey.ModKey == patchKey,
                            "apply: HairColor remapped to the internalized CLFM");

                        var hairCopy = patch.HeadParts.FirstOrDefault(h => h.EditorID == "VivHairHP");
                        Check(hairCopy is not null, "apply: HDPT EditorID PRESERVED (facegeom block-name identity)");
                        if (hairCopy is not null)
                        {
                            Check(hairCopy.Parts.Count == 2, "apply: HDPT.Parts (.tri morph refs — the LIP-SYNC layer) arrive on the copy");
                            Check(hairCopy.ExtraParts.Count == 1 && hairCopy.ExtraParts[0].FormKey.ModKey == patchKey,
                                "apply: ExtraParts remapped to the copied hairline");
                            Check(hairCopy.TextureSet is { IsNull: false } ts && ts.FormKey.ModKey == patchKey,
                                "apply: HDPT.TextureSet remapped to the copied TXST");
                        }
                    }

                    var outDir = Path.GetDirectoryName(o.OutPath!)!;
                    Check(File.Exists(Path.Combine(outDir, $@"meshes\actors\character\facegendata\facegeom\MyFollower.esp\00000800.nif")),
                        "assets: facegeom RENAMED to the TARGET's FormKey path (folder = the target's defining plugin)");
                    Check(File.Exists(Path.Combine(outDir, $@"textures\actors\character\facegendata\facetint\MyFollower.esp\00000800.dds")),
                        "assets: facetint renamed alongside");
                    Check(File.Exists(Path.Combine(outDir, skinRel)),
                        "assets: the geom's EMBEDDED texture (byte-scraped) carried from the disabled donor's folder");
                    Check(File.Exists(Path.Combine(outDir, hairRel)),
                        "assets: the record-referenced model carried from the disabled donor's folder");
                    Check(o.Assets is { } aa && aa.Missing.Any(m => m.Contains("FemaleHead.tri", StringComparison.OrdinalIgnoreCase)),
                        "assets: an unresolvable referenced path is a NAMED miss (Q3), never silent");
                }
            }

            // ================= 2. CLONE — a full standalone clone with the strips NAMED =================
            {
                var o = svc.CopyNpcAppearance(donorNpcFk.ToString(), "Donor.esp", null,
                                              null, "VivClone", "Vivi", "VivCloneP", null);
                Check(o.Success, $"clone: succeeds ({o.Error})");
                Check(o.Success && !o.DonorAmongMasters, "clone: the donor is NOT among the patch masters");
                Check(o.Success && o.Stripped.Any(s => s.Field.StartsWith("Factions[", StringComparison.Ordinal)),
                    "clone: the donor-faction membership is STRIPPED and NAMED");
                Check(o.Success && o.Stripped.Any(s => s.Field == "DefaultOutfit"),
                    "clone: the donor DefaultOutfit is STRIPPED and NAMED");
                if (o.Success)
                {
                    using var pd = OpenDisposable(o.OutPath!, out var patch);
                    var cl = patch.Npcs.FirstOrDefault(n => n.EditorID == "VivClone");
                    Check(cl is not null && cl.FormKey == o.NewNpcKey, "clone: the new NPC exists under its reported new key");
                    if (cl is not null)
                    {
                        Check(cl.Name?.String == "Vivi", "clone: new Name applied");
                        Check(cl.HeadParts.Count == 1 && cl.HeadParts[0].FormKey.ModKey == cl.FormKey.ModKey,
                            "clone: HeadParts remapped to the internalized copies");
                        Check(cl.Factions.Count == 0 && (cl.DefaultOutfit is null || cl.DefaultOutfit.IsNull),
                            "clone: the stripped references are really gone from the record");
                        Check(cl.TextureLighting is { } tl2 && tl2.R == 246, "clone: TextureLighting carried by the whole-record copy");
                    }
                    var outDir = Path.GetDirectoryName(o.OutPath!)!;
                    var newId = "00" + o.NewNpcKey.ID.ToString("X6");
                    Check(File.Exists(Path.Combine(outDir, $@"meshes\actors\character\facegendata\facegeom\VivCloneP.esp\{newId}.nif")),
                        "clone: facegeom renamed to the CLONE's FormKey path (folder = the patch)");
                }
            }

            // ================= 2b. EXTEND RE-RUN — dedupe the already-internalized subtree + precedence warning =================
            {
                var o = svc.CopyNpcAppearance(donorNpcFk.ToString(), "Donor.esp", null,
                                              target2Fk.ToString(), null, null, null, "SaelaFace");
                Check(o.Success, $"re-run: apply onto a SECOND target into the SAME patch succeeds ({o.Error})");
                Check(o.Success && o.Reused.Count == 4 && o.Internalized.Count == 0,
                    $"re-run: the donor subtree is REUSED, not re-copied (reused {o.Reused.Count}, internalized {o.Internalized.Count})");
                if (o.Success)
                {
                    using var pd = OpenDisposable(o.OutPath!, out var patch);
                    Check(patch.HeadParts.Count(h => h.EditorID == "VivHairHP") == 1,
                        "re-run: exactly ONE 'VivHairHP' in the patch — no duplicate EditorIDs to break block-name mapping");
                    var npc2 = patch.Npcs.FirstOrDefault(n => n.FormKey == target2Fk);
                    Check(npc2 is not null && npc2.HeadParts.Count == 1 && npc2.HeadParts[0].FormKey == patch.HeadParts.First(h => h.EditorID == "VivHairHP").FormKey,
                        "re-run: the second target is wired to the REUSED copy");
                }
                Check(o.Success && o.Assets is { } ca && ca.Warnings.Any(w => w.Contains("ALREADY provided")),
                    "re-run: a contested facegen DESTINATION gets a named precedence warning (file precedence is its own system)");
            }

            // ================= 2c. BASE-GAME donor — never donor-bound (the vanilla-look transplant lane) =================
            {
                var o = svc.CopyNpcAppearance(vanNpcFk.ToString(), null, null,
                                              targetFk.ToString(), null, null, "VanFace", null);
                Check(o.Success, $"base-game donor: succeeds — no nonsense custom-race refusal ({o.Error})");
                Check(o.Success && o.DonorIsBaseGame && o.Internalized.Count == 0,
                    "base-game donor: nothing internalized (its records resolve everywhere), said plainly");
                Check(o.Success && o.Masters.Contains("Skyrim.esm", StringComparer.OrdinalIgnoreCase),
                    "base-game donor: the links stay links — Skyrim.esm mastered normally");
            }

            // ================= 2d. clone refusals the fold added =================
            {
                var req = svc.CopyNpcAppearance(classedNpcFk.ToString(), "Donor.esp", null,
                                                null, "VivClassedClone", null, "VivClassedP", null);
                Check(!req.Success && req.Error!.Contains("REQUIRED") && req.Error!.Contains("apply lane"),
                    "clone: a donor-internal REQUIRED link (Class) REFUSES loud — never silently nulled");
                var tpl = svc.CopyNpcAppearance(templatedNpcFk.ToString(), "Donor.esp", null,
                                                targetFk.ToString(), null, null, null, null);
                Check(!tpl.Success && tpl.Error!.Contains("template"),
                    "refusal: a Traits-TEMPLATED donor is named (its own record carries no appearance)");
                var dup = svc.CopyNpcAppearance(donorNpcFk.ToString(), "Donor.esp", null,
                                                null, "VivClone", null, null, "VivCloneP");
                Check(!dup.Success && dup.Error!.Contains("already contains an NPC"),
                    "clone re-run: minting the same EditorID into the same patch is refused");
            }

            // ================= 2e. the NIF texture scanner — boundary + multi-match (review findings) =================
            {
                static byte[] Ascii(string s) => System.Text.Encoding.ASCII.GetBytes(s);
                var two = Ascii("junk\0textures\\a\\one.dds\0mid\0textures\\b\\two.dds\0");
                var got = NpcAppearanceAssets.ScrapeNifTexturePaths(two);
                Check(got.Count == 2 && got.Contains(@"textures\a\one.dds") && got.Contains(@"textures\b\two.dds"),
                    $"scanner: two paths in one buffer both found (got {string.Join(" | ", got)})");
                var glued = Ascii("textures\\a\\one.dds_textures\\b\\two.dds\0");     // one printable run, two paths
                var got2 = NpcAppearanceAssets.ScrapeNifTexturePaths(glued);
                Check(got2.Count == 2 && got2[0] == @"textures\a\one.dds" && got2[1] == @"textures\b\two.dds",
                    "scanner: FIRST-.dds match — adjacent paths in one run never glue into garbage");
                var longRun = Ascii(new string('x', 300) + "textures\\c\\three.dds\0"); // path right after a capped printable run
                var got3 = NpcAppearanceAssets.ScrapeNifTexturePaths(longRun);
                Check(got3.Count == 1 && got3[0] == @"textures\c\three.dds",
                    "scanner: a path starting at a capped-run boundary is not skipped");
            }

            // ================= 3. Q3 refusals =================
            {
                var both = svc.CopyNpcAppearance(donorNpcFk.ToString(), "Donor.esp", null, targetFk.ToString(), "X", null, null, null);
                Check(!both.Success && both.Error!.Contains("EXACTLY ONE"), "refusal: both target modes named");
                var neither = svc.CopyNpcAppearance(donorNpcFk.ToString(), "Donor.esp", null, null, null, null, null, null);
                Check(!neither.Success && neither.Error!.Contains("EXACTLY ONE"), "refusal: neither target mode named");
                var inactive = svc.CopyNpcAppearance(donorNpcFk.ToString(), null, null, targetFk.ToString(), null, null, null, null);
                Check(!inactive.Success && inactive.Error!.Contains("source_plugin"),
                    "refusal: a donor the active order can't see routes to source_plugin=");
                var notNpc = svc.CopyNpcAppearance(texFk.ToString(), "Donor.esp", null, targetFk.ToString(), null, null, null, null);
                Check(!notNpc.Success && notNpc.Error!.Contains("not an NPC"), "refusal: a non-NPC donor is named");
                var badRace = svc.CopyNpcAppearance(badNpcFk.ToString(), "Donor.esp", null, targetFk.ToString(), null, null, null, null);
                Check(!badRace.Success && badRace.Error!.Contains("RACE"), "refusal: a donor-internal custom race is out of scope, NAMED");
                Check(!Directory.Exists(Path.Combine(mods, "houseCARL - houseCARL_NpcCopy")),
                    "refusal: no orphan patch folder is left behind (hunt F4)");
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort temp cleanup */ }
        }

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "ALL CHECKS PASSED" : $"{fail} CHECK(S) FAILED");
        return fail == 0 ? 0 : 1;
    }

    // ---- synthetic MO2 layout helpers (the SeqRegen / ReadPluginFile probe pattern) ----

    static (string mods, string prof) MakeInstance(string inst)
    {
        var mods = Path.Combine(inst, "mods");
        var data = Path.Combine(inst, "game", "Data");
        var prof = Path.Combine(inst, "profiles", "Default");
        foreach (var d in new[] { mods, data, prof }) Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(inst, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(inst, "game").Replace(@"\", @"\\") + ")\r\n");
        return (mods, prof);
    }

    static void WriteModDirect(string mods, string folder, SkyrimMod m, params ISkyrimModGetter[] masters)
    {
        var dir = Path.Combine(mods, folder);
        Directory.CreateDirectory(dir);
        m.BeginWrite.ToPath(Path.Combine(dir, m.ModKey.FileName.String)).WithLoadOrder(masters).Write();
    }

    static void WriteProfile(string prof, string[] loadorder, string[] plugins, string[] modlist)
    {
        Directory.CreateDirectory(prof);
        File.WriteAllText(Path.Combine(prof, "loadorder.txt"), "# header\r\n" + string.Join("\r\n", loadorder) + "\r\n");
        File.WriteAllText(Path.Combine(prof, "plugins.txt"), string.Join("\r\n", plugins) + "\r\n");
        File.WriteAllText(Path.Combine(prof, "modlist.txt"), "# header\r\n" + string.Join("\r\n", modlist) + "\r\n");
    }

    static void WriteSkyrimIni(string prof) =>
        File.WriteAllText(Path.Combine(prof, "Skyrim.ini"), "[Archive]\r\nsResourceArchiveList=\r\n");

    static void WriteFixture(string modDir, string relPath, byte[] bytes)
    {
        var p = Path.Combine(modDir, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, bytes);
    }

    /// <summary>Open a written patch as an overlay, returning the disposable so `using` releases the handle.</summary>
    static IDisposable OpenDisposable(string path, out ISkyrimModGetter mod)
    {
        var ov = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
        mod = ov;
        return (IDisposable)ov;
    }
}
