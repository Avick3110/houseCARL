using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// Patch-stem load-order-collision guard (PR #192 review). Defaulting the new-patch stem to the generic "Patch"
/// (so the MO2 folder reads "houseCARL - Patch", not the doubled "houseCARL - houseCARL_Patch") means the default
/// plugin BASENAME is "Patch.esp" — a genuinely common name. The engine forbids two ACTIVE plugins sharing a
/// basename, and mod-FOLDER uniqueness (UniqueStem's original arm) never sees a same-named plugin living in another
/// mod. So UniqueStem now ALSO uniquifies against the active load order: if "Patch.esp" is already live, the write
/// becomes "Patch_001.esp", never a duplicate.
///
/// Drives the REAL service write path against a synthetic MO2 instance whose load order already contains an active
/// "Patch.esp". Arms:
///   COLLISION — a DEFAULT-stem write (patch_name omitted → "Patch") uniquifies to "houseCARL - Patch_001\Patch_001.esp"
///               because "Patch.esp" is already active. RED if the load-order arm is reverted (old code emits "Patch.esp").
///   CONTROL   — a write with a stem NOT in the load order stays bare ("houseCARL - <stem>\<stem>.esp"): the guard fires
///               only on a real collision, never spuriously.
///   ORIGINAL  — the active "Patch.esp" is byte-untouched (the write went to a fresh uniquified patch, not in place).
/// </summary>
internal static class PatchStemCollisionProbe
{
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("================================================================");
        Console.WriteLine(" patch-stem collision guard — default stem uniquifies vs the load order");
        Console.WriteLine("================================================================");
        Console.WriteLine();
        int fail = 0;
        void Check(bool c, string label) { Console.WriteLine((c ? "  PASS  " : "  FAIL  ") + label); if (!c) fail++; }
        static bool Ends(string path, params string[] parts) =>
            path.EndsWith(Path.Combine(parts), StringComparison.OrdinalIgnoreCase);

        var root = Path.Combine(Path.GetTempPath(), "hc-patch-stem-collision-guard-" + Guid.NewGuid().ToString("N"));
        try
        {
            // ---- synthetic MO2 instance whose active load order ALREADY holds a "Patch.esp" (the collision surface) ----
            string instance = Path.Combine(root, "instance");
            string profiles = Path.Combine(instance, "profiles", "Default");
            string mods = Path.Combine(instance, "mods");
            Directory.CreateDirectory(profiles); Directory.CreateDirectory(mods);
            Directory.CreateDirectory(Path.Combine(root, "game", "Data"));
            File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
                "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
                + Path.Combine(root, "game").Replace(@"\", @"\\") + ")\r\n");

            // A base mod providing an ACTIVE "Patch.esp" that carries a weapon we can edit (so the write has a real target
            // AND the default stem "Patch" collides with this plugin's basename).
            var baseKey = new ModKey("Patch", ModType.Plugin);
            var basePath = Path.Combine(mods, "BaseMod", baseKey.FileName.String);
            Directory.CreateDirectory(Path.GetDirectoryName(basePath)!);
            FormKey weapFk;
            {
                var m = new SkyrimMod(baseKey, SkyrimRelease.SkyrimSE);
                var w = m.Weapons.AddNew(); w.EditorID = "HcStemWeap"; w.BasicStats = new WeaponBasicStats { Damage = 10, Weight = 1 };
                weapFk = w.FormKey;
                m.BeginWrite.ToPath(basePath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
            }
            File.WriteAllText(Path.Combine(profiles, "loadorder.txt"), "# header\r\n" + baseKey.FileName + "\r\n");
            File.WriteAllText(Path.Combine(profiles, "plugins.txt"), "*" + baseKey.FileName + "\r\n");
            File.WriteAllText(Path.Combine(profiles, "modlist.txt"), "# header\r\n+BaseMod\r\n");
            byte[] baseBytesAtStart = File.ReadAllBytes(basePath);

            var genDir = Path.Combine(root, "corpus-gen");
            CorpusGenerator.GenerateAll(genDir, Path.Combine(root, "corpus-ref"));
            CorpusRulebook.CorpusPath = Path.Combine(genDir, "corpus.json");

            string fid = $"{weapFk.ID:X6}:{weapFk.ModKey.FileName}";
            var store = new UserConfigStore(Path.Combine(root, "houseCARL.user.json"));
            using var svc = LoadOrderService.WithInstance(instance, 0, store);
            svc.Stats();                                                       // warm the lazy index once, off the clock

            BulkOp Dmg(int v) => new() { Formid = fid, FieldPath = "BasicStats.Damage", Verb = "Set", Value = v.ToString() };

            // ---- COLLISION: default stem "Patch" must dodge the active "Patch.esp" → "houseCARL - Patch_001\Patch_001.esp" ----
            Console.WriteLine("--- 1: default stem collides with active Patch.esp ---");
            var r1 = svc.ApplyEdits(new[] { Dmg(20) }, null, null);
            Check(r1.Success, "default-stem write succeeded");
            Check(r1.Success && Ends(r1.OutputPath, "houseCARL - Patch_001", "Patch_001.esp"),
                  "default stem uniquified to Patch_001 (dodged the active Patch.esp) — RED if the load-order arm is gone");
            Check(!Ends(r1.OutputPath ?? "", "Patch.esp"),
                  "the write did NOT emit a bare Patch.esp that would duplicate the active plugin");

            // ---- CONTROL: a stem NOT in the load order stays bare (the guard fires only on a real collision) ----
            Console.WriteLine("--- 2: a non-colliding stem stays bare (no spurious uniquify) ---");
            var r2 = svc.ApplyEdits(new[] { Dmg(21) }, "HcNoSuchStemZZ", null);
            Check(r2.Success && Ends(r2.OutputPath, "houseCARL - HcNoSuchStemZZ", "HcNoSuchStemZZ.esp"),
                  "a stem with no load-order clash is used as-is (no _001)");

            // ---- ORIGINAL: the active base plugin is byte-untouched (fresh patch, not in place) ----
            Console.WriteLine("--- 3: the active Patch.esp is byte-untouched ---");
            Check(File.ReadAllBytes(basePath).AsSpan().SequenceEqual(baseBytesAtStart),
                  "base Patch.esp unchanged (writes went to fresh uniquified patches)");
        }
        catch (Exception ex)
        {
            Console.WriteLine("  FAIL  probe threw: " + ex);
            fail++;
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort temp cleanup */ }
        }

        Console.WriteLine();
        Console.WriteLine(fail == 0
            ? "[patch-stem-collision-guard] PASS — default stem uniquifies against the active load order."
            : $"[patch-stem-collision-guard] FAIL — {fail} check(s) failed.");
        return fail == 0 ? 0 : 1;
    }
}
