using System.Text.RegularExpressions;
using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// NIF source-lane guard — how <c>nif_inspect</c> / <c>nif_set</c> address a PROVIDER, driven through the real tool
/// over a synthetic MO2 instance whose mod names are deliberately hostile (a space, parentheses, an apostrophe).
///
/// <para>#340 — ROUND TRIP. Every provider name the tool prints is taken back out of the rendered chain and fed
/// straight into <c>mod=</c>, and each one must select that provider. A substring check would not catch the bug this
/// guards: the old render printed <c>SomeMod (loose)</c>, which contains the accepted <c>SomeMod</c> and refuses when
/// passed back. The chain is parsed by the DELIMITER, so the arm fails the moment the printed token stops being the
/// accepted one.</para>
///
/// <para>#388 — REACH. Naming a mod reaches that mod's loose files AND its own root archives: for a mod MO2 loads
/// whose only copy is inside its .bsa (the old dead end — the caller had to know to name the archive instead), and
/// for one MO2 is not loading at all, where the refusal used to report the mesh ABSENT and read as "the donor has no
/// mesh". A name with no folder behind it still refuses, and says which places were searched.</para>
///
/// <para>#412 — <c>npc=</c>. An NPC FormID is resolved to its FaceGen head mesh and read as one more member of the
/// batch, in the same call as an explicit path.</para>
///
/// Run: <c>dotnet run --project src/housecarl-generator -- nif-source-lane-guard</c>
/// </summary>
internal static class NifSourceLaneProbe
{
    // Deliberately hostile: a space and parentheses (an MO2 name legitimately carries them, so "strip the
    // parenthetical" can never be the round-trip rule) and an apostrophe (JK's Skyrim — what killed single quotes).
    const string BsaOnlyMod = "Donor Mod (SE)";
    const string LooseMod = "JK's Skyrim";
    const string OffMod = "Unticked Donor";

    // The facegeom path for 000001:Test.esp — so the same file is reachable as a path AND as npc=.
    const string FaceRel = @"meshes\actors\character\facegendata\facegeom\Test.esp\00000001.nif";
    const string NpcFormId = "000001:Test.esp";
    // A second mesh, present ONLY inside the unticked mod's own archive.
    const string OffRel = @"meshes\actors\character\facegendata\facegeom\Test.esp\00000002.nif";

    [CiProbe("nif-source-lane-guard")]
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("================================================================");
        Console.WriteLine(" nif source-lane guard — mod= round trip, reach, and npc=");
        Console.WriteLine("================================================================");
        Console.WriteLine();
        int fail = 0;
        void Check(bool c, string label) { Console.WriteLine((c ? "  PASS  " : "  FAIL  ") + label); if (!c) fail++; }

        var seBytes = NifSetGuardProbe.BuildSyntheticSe();
        var root = Path.Combine(Path.GetTempPath(), "hc-nif-source-lane-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var inst = Path.Combine(root, "inst");
            var (mods, _, prof) = NifSetGuardProbe.MakeInstance(inst);

            // The mod whose ONLY copy of the mesh is inside its own root archive, bound to an active plugin.
            var bsaMod = Path.Combine(mods, BsaOnlyMod);
            Directory.CreateDirectory(bsaMod);
            File.WriteAllText(Path.Combine(bsaMod, "Test.esp"), "x");
            File.WriteAllBytes(Path.Combine(bsaMod, "Test.bsa"), Archive(FaceRel, seBytes));

            // A loose provider for the same path, so the chain has two entries to round-trip.
            NifSetGuardProbe.WriteLoose(Path.Combine(mods, LooseMod), FaceRel, seBytes);

            // The mod MO2 is NOT loading, carrying the second mesh inside its own root archive.
            var offDir = Path.Combine(mods, OffMod);
            Directory.CreateDirectory(offDir);
            File.WriteAllBytes(Path.Combine(offDir, "Off.bsa"), Archive(OffRel, seBytes));

            NifSetGuardProbe.WriteProfile(prof, new[] { "Test.esp" }, new[] { "*Test.esp" },
                                          new[] { "+" + LooseMod, "+" + BsaOnlyMod, "-" + OffMod });
            NifSetGuardProbe.WriteSkyrimIni(prof);

            using var svc = HousecarlMcp.LoadOrderService.WithInstance(inst, 0, new UserConfigStore(Path.Combine(root, "u.json")));

            // ---- #340: every printed provider name selects that provider when fed straight back ----
            Console.WriteLine("--- #340: the printed token is the accepted token ---");
            var chainOut = HousecarlMcp.NifTools.NifInspect(svc, new[] { FaceRel });
            Check(chainOut.Contains("providers (2)"), $"both providers are listed — {Line(chainOut, "providers")}");
            var printed = Regex.Matches(Line(chainOut, "providers"), "\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();
            Check(printed.Count == 2 && printed.Contains(LooseMod) && printed.Contains("Test.bsa"),
                  $"the names come out of the chain by DELIMITER, hostile characters intact — [{string.Join(" | ", printed)}]");
            foreach (var name in printed)
            {
                var back = HousecarlMcp.NifTools.NifInspect(svc, new[] { FaceRel }, mod: name);
                Check(back.Contains("read from: \"" + name + "\""),
                      $"round trip: the printed '{name}' selects that provider — {Line(back, "read from") ?? Line(back, "does not supply")}");
            }
            // The old render's spelling must NOT be accepted silently as something else: it is not a provider name.
            var annotated = HousecarlMcp.NifTools.NifInspect(svc, new[] { FaceRel }, mod: LooseMod + " (loose)");
            Check(annotated.Contains("does not supply") && !annotated.Contains("read from:"),
                  "the kind annotation is NOT part of the name — passing it refuses rather than resolving anyway");

            // ---- #388: naming a mod reaches its own archives, ticked or not ----
            Console.WriteLine();
            Console.WriteLine("--- #388: a mod's name reaches its loose files AND its own root archives ---");
            var byMod = HousecarlMcp.NifTools.NifInspect(svc, new[] { FaceRel }, mod: BsaOnlyMod);
            Check(byMod.Contains("read from:") && !byMod.Contains("does not supply"),
                  $"an ENABLED mod whose only copy is in its own .bsa is reached by naming the MOD — {Line(byMod, "read from") ?? Line(byMod, "does not supply")}");

            var absent = HousecarlMcp.NifTools.NifInspect(svc, new[] { OffRel });
            Check(absent.Contains("ABSENT"), "the second mesh is ABSENT with no mod= (nothing active provides it)");
            var offRead = HousecarlMcp.NifTools.NifInspect(svc, new[] { OffRel }, mod: OffMod);
            Check(offRead.Contains("read from:") && !offRead.Contains("ABSENT"),
                  $"naming an UNTICKED mod reads out of its own root archive, and never reports the mesh ABSENT — {Line(offRead, "read from") ?? Line(offRead, "ABSENT")}");

            var noSuch = HousecarlMcp.NifTools.NifInspect(svc, new[] { FaceRel }, mod: "NoSuchMod");
            Check(noSuch.Contains("'NoSuchMod' does not supply") && noSuch.Contains("no MO2 mod folder of that name")
                  && !noSuch.Contains("ABSENT"),
                  $"a name with no folder behind it refuses by NAME and says where it looked — {Line(noSuch, "does not supply")}");

            // nif_set answers mod= through the same lane — the pair has drifted before.
            var setByMod = svc.NifSet(FaceRel, new[] { new NifSetOp(NifSetOpKind.SetFlags, "GuardShape", Flags: 0x800000E) },
                                      BsaOnlyMod, "NifLane", null, inPlace: false, acknowledge: false);
            // The provider is still the ARCHIVE — that is what supplies the bytes; naming the mod is how it was
            // addressed, and the response says which copy was read rather than echoing the name that was typed.
            Check(setByMod.Error is null && setByMod.Edited is { Kind: "BSA" },
                  $"nif_set reaches the same copy by the same name — {setByMod.Error ?? setByMod.Edited?.Text}");
            var setNoSuch = svc.NifSet(FaceRel, new[] { new NifSetOp(NifSetOpKind.SetFlags, "GuardShape", Flags: 0x800000E) },
                                       "NoSuchMod", null, null, inPlace: false, acknowledge: false);
            Check(setNoSuch.Error is { } se && se.Contains("'NoSuchMod' does not supply") && !se.Contains("ABSENT"),
                  $"nif_set's refusal is the same sentence — {setNoSuch.Error}");

            // ---- #412: npc= derives the FaceGen mesh and joins the batch ----
            Console.WriteLine();
            Console.WriteLine("--- #412: npc= resolves to the FaceGen head mesh ---");
            var byNpc = HousecarlMcp.NifTools.NifInspect(svc, null, npc: new[] { NpcFormId });
            Check(byNpc.Contains(FaceRel) && byNpc.Contains("read from:"),
                  $"an NPC FormID alone derives its facegeom .nif and reads it — {Line(byNpc, "read from") ?? byNpc.Split('\n').FirstOrDefault(l => l.Contains("nif"))}");
            var both = HousecarlMcp.NifTools.NifInspect(svc, new[] { FaceRel }, npc: new[] { NpcFormId });
            Check(both.Contains("(2 meshes)"), $"mesh_paths and npc compose in ONE call — {Line(both, "nif inspect")}");
            var neither = HousecarlMcp.NifTools.NifInspect(svc, null, null);
            Check(neither.StartsWith("error:") && neither.Contains("npc"),
                  $"neither selector is a named refusal that names both — {neither}");
            var badNpc = HousecarlMcp.NifTools.NifInspect(svc, null, npc: new[] { "not-a-formid" });
            Check(badNpc.StartsWith("error:") && badNpc.Contains("not-a-formid"),
                  $"a malformed npc FormID is refused by name — {badNpc}");

            Console.WriteLine();
            Console.WriteLine(fail == 0 ? "================ ALL PASS ================" : $"================ {fail} FAILED ================");
            return fail == 0 ? 0 : 1;
        }
        catch (Exception ex) { Console.WriteLine($"  FAIL  (unexpected) {ex.GetType().Name}: {ex.Message}"); return 1; }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* temp scratch */ } }
    }

    /// <summary>A one-entry uncompressed BSA carrying <paramref name="rel"/>, authored in memory.</summary>
    static byte[] Archive(string rel, byte[] bytes) => BsaBuilder.Build(105,
        BsaBuilder.HasFolderNames | BsaBuilder.HasFileNames,
        new[] { (Path.GetDirectoryName(rel)!, new[] { (Path.GetFileName(rel), bytes) }) });

    /// <summary>The first rendered line containing <paramref name="needle"/>, trimmed — the arm labels quote the
    /// tool's own output rather than restating what it should have said.</summary>
    static string? Line(string output, string needle)
        => output.Split('\n').FirstOrDefault(l => l.Contains(needle))?.Trim();
}
