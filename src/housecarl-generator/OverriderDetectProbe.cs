using System.Linq;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// COMPACT gap #2 — EXTERNAL-OVERRIDER detection guard for housecarl_compact_plugin. The shipped identify-pass walked
/// only OUTGOING FormLinks, so a plugin that OVERRIDES a record being renumbered (its override shares the FormKey but
/// need carry no link into the set — e.g. a face-only NPC override) was MISSED and orphaned silently after the renumber.
/// This proves the fix: the identify-pass now also tests each external record's OWN FormKey against the remap set, and
/// surfaces overriders as a WARN — warn-and-proceed (xEdit parity), NOT the referencer refuse/repoint (an override can't
/// be auto-repointed: that's an identity change, not a link rewrite, so routing it through repoint would be a Q3 false
/// success). Drives the REAL LoadOrderService over synthetic MO2 instances.
///   OVERRIDER  — plugin Q overrides target P's record (no outgoing ref into P): compacting P SUCCEEDS (warn-and-proceed)
///                and the outcome NAMES Q as an external overrider, while listing ZERO referencers (the two are distinct).
///   REFERENCER — plugin R FormLinks into P's record (does not override it): compacting P is still REFUSED + R named
///                (existing behavior, unchanged) — the contrast that proves overrider≠referencer handling.
/// Run: dotnet run --project src/housecarl-generator overrider-detect-guard
/// </summary>
public static class OverriderDetectProbe
{
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("################  COMPACT gap #2 — external-overrider detection guard  ################");
        Console.WriteLine();
        int fail = 0;
        void Check(bool c, string label) { Console.WriteLine((c ? "  PASS  " : "  FAIL  ") + label); if (!c) fail++; }

        var root = Path.Combine(Path.GetTempPath(), "hc-overrider-detect-guard-" + Guid.NewGuid().ToString("N"));
        try
        {
            // ============ OVERRIDER: Q overrides P's weapon (pure override) → compact P warns + proceeds, names Q ============
            {
                var (mods, prof) = MakeInstance(Path.Combine(root, "ovr"));
                var pKey = new ModKey("OvrTarget", ModType.Plugin);
                var pWeap = new FormKey(pKey, 0xA01);
                WriteMod(mods, "OvrTarget", pKey, m =>
                    m.Weapons.Add(new Weapon(pWeap, SkyrimRelease.SkyrimSE) { EditorID = "OvrWeap", BasicStats = new WeaponBasicStats { Damage = 7 } }));
                // Q OVERRIDES P's weapon — a deep-copy-as-override, NO added FormLink into P (the link-only walk missed this).
                var qKey = new ModKey("OvrDependent", ModType.Plugin);
                {
                    using var pOv = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(mods, "OvrTarget", pKey.FileName.String), SkyrimRelease.SkyrimSE);
                    var pCache = pOv.ToImmutableLinkCache();
                    var pw = pOv.Weapons.First(w => w.EditorID == "OvrWeap");
                    var dir = Path.Combine(mods, "OvrDependent"); Directory.CreateDirectory(dir);
                    var q = new SkyrimMod(qKey, SkyrimRelease.SkyrimSE);
                    WriteEngine.GenericGetOrAddAsOverride(q, pw, pCache);     // q now overrides P:0xA01 (declares P a master)
                    q.BeginWrite.ToPath(Path.Combine(dir, qKey.FileName.String)).WithLoadOrder(new[] { pOv }).NoNextFormIDProcessing().Write();
                }
                WriteProfile(prof, new[] { pKey.FileName.String, qKey.FileName.String },
                    new[] { "*" + pKey.FileName, "*" + qKey.FileName }, new[] { "+OvrDependent", "+OvrTarget" });
                WriteSkyrimIni(prof);

                using var svc = LoadOrderService.WithInstance(Path.Combine(root, "ovr"), 0, new UserConfigStore(Path.Combine(root, "user-ovr.json")));
                svc.Stats();

                var o = svc.CompactPlugin("OvrTarget.esp");
                bool detected = o.ExternalOverriders is { } ovr && ovr.Contains("OvrDependent.esp", StringComparer.OrdinalIgnoreCase);
                bool notReferencer = o.ExternalPlugins.Count == 0;            // it's an OVERRIDER, not a referencer
                Check(o.Success && detected && notReferencer,
                    $"OVERRIDER detected + warn-and-proceed (success {o.Success}, overriders [{(o.ExternalOverriders is { } x ? string.Join(",", x) : "")}], referencers {o.ExternalPlugins.Count}{(o.Success ? "" : "; ERR " + o.Error)})");
            }

            // ============ REFERENCER (contrast): R FormLinks into P → still REFUSED + named (distinct path) ============
            {
                var (mods, prof) = MakeInstance(Path.Combine(root, "ref"));
                var pKey = new ModKey("RefTarget", ModType.Plugin);
                var pWeap = new FormKey(pKey, 0xA01);
                WriteMod(mods, "RefTarget", pKey, m =>
                    m.Weapons.Add(new Weapon(pWeap, SkyrimRelease.SkyrimSE) { EditorID = "RefWeap", BasicStats = new WeaponBasicStats { Damage = 7 } }));
                // R REFERENCES P's weapon via a FormList (outgoing link) — does NOT override it.
                var rKey = new ModKey("RefDependent", ModType.Plugin);
                {
                    using var pOv = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(mods, "RefTarget", pKey.FileName.String), SkyrimRelease.SkyrimSE);
                    var dir = Path.Combine(mods, "RefDependent"); Directory.CreateDirectory(dir);
                    var r = new SkyrimMod(rKey, SkyrimRelease.SkyrimSE);
                    var fl = new FormList(new FormKey(rKey, 0xA01), SkyrimRelease.SkyrimSE) { EditorID = "RefList" };
                    fl.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(pWeap));
                    r.FormLists.Add(fl);
                    r.ModHeader.Stats.NextFormID = 0xA02;
                    r.BeginWrite.ToPath(Path.Combine(dir, rKey.FileName.String)).WithLoadOrder(new[] { pOv }).NoNextFormIDProcessing().Write();
                }
                WriteProfile(prof, new[] { pKey.FileName.String, rKey.FileName.String },
                    new[] { "*" + pKey.FileName, "*" + rKey.FileName }, new[] { "+RefDependent", "+RefTarget" });
                WriteSkyrimIni(prof);

                using var svc = LoadOrderService.WithInstance(Path.Combine(root, "ref"), 0, new UserConfigStore(Path.Combine(root, "user-ref.json")));
                svc.Stats();

                var o = svc.CompactPlugin("RefTarget.esp");
                Check(!o.Success && !o.NeedsAcknowledge && (o.Error?.Contains("RefDependent.esp", StringComparison.OrdinalIgnoreCase) ?? false),
                    $"REFERENCER still REFUSED + named — distinct from the overrider warn ({o.Error?.Split('.')[0]})");
            }
        }
        finally { try { Directory.Delete(root, true); } catch { } }

        Console.WriteLine();
        Console.WriteLine($"=== overrider-detect-guard: {(fail == 0 ? "PASS" : $"FAIL ({fail})")} ===");
        return fail == 0 ? 0 : 1;
    }

    // ---- synthetic MO2 layout helpers (the CompactServiceGuard / FacegenCarry probe pattern) ----

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

    static void WriteMod(string mods, string folder, ModKey key, Action<SkyrimMod> build)
    {
        var dir = Path.Combine(mods, folder);
        Directory.CreateDirectory(dir);
        var m = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        build(m);
        m.BeginWrite.ToPath(Path.Combine(dir, key.FileName.String)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
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
}
