using System.Text;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// EXPLORATORY (F1) — the two BINDING kickoff measurements, taken on the branch BASE before any fix.
/// Fixture: an MO2 instance whose donor mod is DISABLED and holds the ONLY copy of a facegen path
/// (loose) plus a root BSA carrying a second path. Nothing enabled provides either.
/// Run: dotnet run --project src/housecarl-generator -- f1-measure
/// </summary>
internal static class F1MeasureProbe
{
    public static int Run(string[] args)
    {
        var root = Path.Combine(Path.GetTempPath(), "hc-f1-measure-" + Guid.NewGuid().ToString("N"));
        try { Measure(root); }
        catch (Exception ex) { Console.WriteLine("THREW: " + ex); return 1; }
        finally { try { Directory.Delete(root, true); } catch { } }
        return 0;
    }

    const string DonorFolder = "DonorMod";

    static void Say(string label, string body)
    {
        Console.WriteLine();
        Console.WriteLine("=== " + label + " ===");
        Console.WriteLine(body);
    }

    static void Measure(string root)
    {
        var fixA = Path.GetFullPath(Path.Combine("src", "housecarl-generator", "fixtures", "asset-resolver", "FixtureA.bsa"));
        if (!File.Exists(fixA)) { Console.WriteLine("MISSING fixture BSA at " + fixA + " (run from the repo root)"); return; }

        var inst = Path.Combine(root, "inst");
        var mods = Path.Combine(inst, "mods");
        var prof = Path.Combine(inst, "profiles", "Default");
        foreach (var d in new[] { mods, Path.Combine(inst, "game", "Data"), prof }) Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(inst, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(inst, "game").Replace(@"\", @"\\") + ")\r\n");

        // ---- base master (enabled) ----
        var baseKey = new ModKey("HcBase", ModType.Master);
        var raceA = new FormKey(baseKey, 0x800);
        var baseMod = new SkyrimMod(baseKey, SkyrimRelease.SkyrimSE);
        baseMod.Races.Add(new Race(raceA, SkyrimRelease.SkyrimSE) { EditorID = "RaceA" });
        WriteMod(mods, "BaseMod", baseMod);

        // ---- donor mod (DISABLED): the NPC + its head part, and the ONLY copy of its facegen ----
        var donorMk = new ModKey("Donor", ModType.Plugin);
        var hairFk = new FormKey(donorMk, 0x801);
        var donorFk = new FormKey(donorMk, 0x805);
        var donor = new SkyrimMod(donorMk, SkyrimRelease.SkyrimSE);
        var hp = new HeadPart(hairFk, SkyrimRelease.SkyrimSE) { EditorID = "DonorHair" };
        hp.Model = new Model { File = @"actors\character\character assets\hair\donorhair.nif" };
        donor.HeadParts.Add(hp);
        var dnpc = new Npc(donorFk, SkyrimRelease.SkyrimSE) { EditorID = "DonorNpc" };
        dnpc.Race.SetTo(raceA);
        dnpc.HeadParts.Add(hairFk);
        donor.Npcs.Add(dnpc);
        WriteMod(mods, DonorFolder, donor, baseMod);

        var donorDir = Path.Combine(mods, DonorFolder);
        var faceRel = FaceGenPath.For(donorFk, FaceGenSlot.Mesh);
        var loosePath = Path.Combine(donorDir, faceRel);
        Directory.CreateDirectory(Path.GetDirectoryName(loosePath)!);
        var donorBytes = Encoding.ASCII.GetBytes("DISABLED-DONOR-FACEGEN-LOOSE-BYTES");
        File.WriteAllBytes(loosePath, donorBytes);
        // a ROOT BSA in the donor's folder, carrying a path nothing else has
        File.Copy(fixA, Path.Combine(donorDir, "Donor.bsa"));
        const string bsaOnlyRel = @"meshes\actors\character\facegendata\facegeom\Dawnguard.esm\0001A51A.nif";

        // ---- profile: the donor is UNCHECKED, its plugin is not in the order ----
        File.WriteAllText(Path.Combine(prof, "modlist.txt"), $"+BaseMod\r\n-{DonorFolder}\r\n");
        File.WriteAllText(Path.Combine(prof, "loadorder.txt"), "HcBase.esm\r\n");
        File.WriteAllText(Path.Combine(prof, "plugins.txt"), "*HcBase.esm\r\n");
        File.WriteAllText(Path.Combine(prof, "Skyrim.ini"), "[General]\r\n");

        using var svc = LoadOrderService.WithInstance(inst, 0, new UserConfigStore(Path.Combine(root, "user.json")));

        Console.WriteLine("################  F1 KICKOFF MEASUREMENTS (branch base)  ################");
        Console.WriteLine($"disabled mod folder : {DonorFolder}  (modlist line '-{DonorFolder}')");
        Console.WriteLine($"facegen path        : {faceRel}");
        Console.WriteLine($"  loose in the donor folder : {File.Exists(loosePath)} ({donorBytes.Length}B)");
        Console.WriteLine($"bsa-only path       : {bsaOnlyRel}  (inside {DonorFolder}\\Donor.bsa)");

        // ---- pre-flight: is the path really invisible to the enabled universe? ----
        var st = svc.AssetStatus(new[] { faceRel, bsaOnlyRel });
        foreach (var r in st.Results)
            Console.WriteLine($"asset_status        : '{r.Hit?.RelPath}' exists={r.Hit?.Exists} providers={(r.Hit?.Providers.Count ?? 0)}");

        // ================= MEASUREMENT 1 — place_asset / bulk_place_asset =================
        Say("M1a  place_asset, source_provider=<disabled mod>, NO source=",
            PlaceAssetTools.PlaceAsset(svc, null, null, faceRel, null, DonorFolder, "M1a", null));

        Say("M1b  place_asset, source_provider=<disabled mod>, WITH source= (same rel path)",
            PlaceAssetTools.PlaceAsset(svc, null, null, faceRel, faceRel, DonorFolder, "M1b", null));

        Say("M1c  place_asset, source_provider=<disabled mod>, source= a path only its ROOT BSA has",
            PlaceAssetTools.PlaceAsset(svc, null, null, faceRel, bsaOnlyRel, DonorFolder, "M1c", null));

        Say("M1d  bulk_place_asset, source_provider=<disabled mod>, NO source=",
            PlaceAssetTools.BulkPlaceAsset(svc, new[]
            { new PlaceAssetSpec { AssetPath = faceRel, SourceProvider = DonorFolder } }, "M1d", null));

        Say("M1e  bulk_place_asset, source_provider=<disabled mod>, WITH source=",
            PlaceAssetTools.BulkPlaceAsset(svc, new[]
            { new PlaceAssetSpec { AssetPath = faceRel, Source = faceRel, SourceProvider = DonorFolder } }, "M1e", null));

        Say("M1f  place_asset, source_provider= a name NOTHING has anywhere (control)",
            PlaceAssetTools.PlaceAsset(svc, null, null, faceRel, faceRel, "NoSuchModAnywhere", "M1f", null));

        // ================= MEASUREMENT 2 — copy with from_source naming the disabled donor =================
        var seeds = new[] { "HeadParts", "HairColor", "HeadTexture", "WornArmor" };
        var newOut = CopyTools.Copy(svc, donorFk.ToString(), new[] { "Donor.esp" }, seeds,
                                    new[] { "Race:refuse" }, null, "TheClone", "M2New", null);
        Say("M2a  housecarl_copy, from_source=['Donor.esp'] (the DISABLED donor) — the RECORD side", newOut);

        var newPatchFolder = Directory.EnumerateDirectories(mods, "houseCARL - M2New*").OrderBy(d => d, StringComparer.Ordinal).FirstOrDefault();
        var newPatch = newPatchFolder is null ? null : Directory.EnumerateFiles(newPatchFolder, "*.esp").FirstOrDefault();
        Console.WriteLine($"  patch written       : {newPatch ?? "(none)"}");
        FormKey? newKey = null;
        if (newPatch is not null)
        {
            var m = SkyrimMod.CreateFromBinaryOverlay(newPatch, SkyrimRelease.SkyrimSE);
            try { newKey = m.Npcs.FirstOrDefault(n => string.Equals(n.EditorID, "TheClone", StringComparison.OrdinalIgnoreCase))?.FormKey; }
            finally { (m as IDisposable)?.Dispose(); }
        }
        Console.WriteLine($"  clone key           : {newKey?.ToString() ?? "(none)"}");

        if (newKey is { } nk)
        {
            Say("M2b  the successor's ASSET carry — bulk_place_asset renaming the donor's facegen onto the clone",
                PlaceAssetTools.BulkPlaceAsset(svc, new[]
                {
                    new PlaceAssetSpec { Formid = nk.ToString(), Kind = "mesh", Source = faceRel, SourceProvider = DonorFolder },
                }, null, Path.GetFileName(newPatch!)));
            var carried = Directory.EnumerateFiles(newPatchFolder!, "*.nif", SearchOption.AllDirectories)
                                   .FirstOrDefault(f => f.Contains("facegeom", StringComparison.OrdinalIgnoreCase));
            Console.WriteLine($"  successor carried   : {(carried is null ? "NOTHING" : $"{new FileInfo(carried).Length}B  == donor's? {File.ReadAllBytes(carried).SequenceEqual(donorBytes)}")}");
        }

        // ---- the ANCESTOR over the same disabled donor, for the before-state contrast ----
        var oldOut = NpcCopyTools.CopyNpcAppearance(svc, donorFk.ToString(), "Donor.esp", null, null, "TheClone", null, "M2Old", null);
        Say("M2c  the ANCESTOR copy_npc_appearance over the same disabled donor (DonorDisk carry)", oldOut);
        var oldFolder = Directory.EnumerateDirectories(mods, "houseCARL - M2Old*").OrderBy(d => d, StringComparer.Ordinal).FirstOrDefault();
        var oldFace = oldFolder is null ? null
            : Directory.EnumerateFiles(oldFolder, "*.nif", SearchOption.AllDirectories)
                       .FirstOrDefault(f => f.Contains("facegeom", StringComparison.OrdinalIgnoreCase));
        Console.WriteLine($"  ancestor carried    : {(oldFace is null ? "NOTHING" : $"{new FileInfo(oldFace).Length}B  == donor's? {File.ReadAllBytes(oldFace).SequenceEqual(donorBytes)}")}");
    }

    static void WriteMod(string mods, string folder, SkyrimMod m, params ISkyrimModGetter[] masters)
    {
        var dir = Path.Combine(mods, folder);
        Directory.CreateDirectory(dir);
        m.BeginWrite.ToPath(Path.Combine(dir, m.ModKey.FileName.String)).WithLoadOrder(masters).Write();
    }
}
