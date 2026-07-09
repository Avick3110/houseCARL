using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// MO2 overwrite-folder resolution guard (2026-06-12 hunt F9, Aaron-picked full fix): plugins living in MO2's
/// OVERWRITE folder — the top of MO2's VFS, where tool outputs land (Synthesis patches, xEdit "new file",
/// Wrye Bash) — are listed in the profile files by MO2 but were unresolvable to houseCARL, and the warning
/// misdiagnosed them as a stale-profile problem ("trigger a re-sort") that a re-sort cannot fix. The fix
/// includes overwrite in the filename map at HIGHEST priority (a copy there beats every mod, MO2's own rule).
///
/// Arms (all deterministic):
///   1  core/resolve — an overwrite-only plugin listed in the profile resolves to its overwrite path, no warning.
///   2  core/priority — a filename present in overwrite AND an enabled mod resolves to the OVERWRITE copy.
///   3  core/warning — a genuinely-missing plugin still warns, and the warning names the overwrite folder among
///      the places searched (the message is honest about what was checked) — but in explicit-paths mode
///      (overwriteDir="") the SAME warning omits overwrite, since it was never searched (hunt F9-3).
///   4  service/end-to-end — a real (synthesized) plugin only in overwrite: the service resolves the full order,
///      reads a record out of the overwrite plugin, and reports no warnings.
///   5  service/freshness — the overwrite plugin enters the profile files mid-session (the MO2-refresh flow after
///      a tool writes there); the next call picks it up.
///   6  service/setup-confirm — the housecarl_set_mo2_instance confirmation lists the overwrite root among the
///      derived roots, not silently omitted (hunt F9-4).
///
/// Arms 1–3 drive Mo2LoadOrder.Build directly with dummy plugin FILES (Build maps paths; it never opens plugin
/// content). Arms 4–5 drive the REAL service against a synthetic MO2 instance with real plugin bytes. Self-contained.
/// </summary>
internal static class OverwriteResolveProbe
{
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("================================================================");
        Console.WriteLine(" overwrite-resolve guard — MO2's overwrite layer resolves, on top");
        Console.WriteLine("================================================================");
        Console.WriteLine();
        int fail = 0;
        void Check(bool c, string label) { Console.WriteLine((c ? "  PASS  " : "  FAIL  ") + label); if (!c) fail++; }

        var root = Path.Combine(Path.GetTempPath(), "hc-overwrite-guard-" + Guid.NewGuid().ToString("N"));
        try
        {
            // ---- arms 1-3: the core path map, with dummy plugin files ----
            Console.WriteLine("--- 1-3: core filename map (Mo2LoadOrder.Build) ---");
            {
                var prof = Path.Combine(root, "core", "profile");
                var mods = Path.Combine(root, "core", "mods");
                var data = Path.Combine(root, "core", "data");
                var ovw  = Path.Combine(root, "core", "overwrite");
                Directory.CreateDirectory(prof); Directory.CreateDirectory(Path.Combine(mods, "SomeMod"));
                Directory.CreateDirectory(data); Directory.CreateDirectory(ovw);

                File.WriteAllText(Path.Combine(data, "Skyrim.esm"), "x");                       // base game
                File.WriteAllText(Path.Combine(mods, "SomeMod", "Dup.esp"), "x");               // a mod's copy…
                File.WriteAllText(Path.Combine(ovw, "Dup.esp"), "x");                           // …and overwrite's copy of the SAME name
                File.WriteAllText(Path.Combine(ovw, "ToolOutput.esp"), "x");                    // only in overwrite

                File.WriteAllText(Path.Combine(prof, "loadorder.txt"),
                    "# header\r\nSkyrim.esm\r\nDup.esp\r\nToolOutput.esp\r\nGone.esp\r\n");
                File.WriteAllText(Path.Combine(prof, "plugins.txt"), "*Dup.esp\r\n*ToolOutput.esp\r\n*Gone.esp\r\n");
                File.WriteAllText(Path.Combine(prof, "modlist.txt"), "# header\r\n+SomeMod\r\n");

                var r = Mo2LoadOrder.Build(prof, mods, data, ovw);

                var toolPath = r.OrderedPaths.FirstOrDefault(p => Path.GetFileName(p).Equals("ToolOutput.esp", StringComparison.OrdinalIgnoreCase));
                Check(toolPath is not null && toolPath.StartsWith(ovw, StringComparison.OrdinalIgnoreCase),
                      $"an overwrite-only plugin resolves to its overwrite path — {toolPath ?? "(unresolved)"}");
                Check(!r.Warnings.Any(w => w.Contains("ToolOutput.esp", StringComparison.OrdinalIgnoreCase)),
                      "…and raises no warning (it is not a stale-profile problem)");

                var dupPath = r.OrderedPaths.FirstOrDefault(p => Path.GetFileName(p).Equals("Dup.esp", StringComparison.OrdinalIgnoreCase));
                Check(dupPath is not null && dupPath.StartsWith(ovw, StringComparison.OrdinalIgnoreCase),
                      $"a name in overwrite AND an enabled mod resolves to the OVERWRITE copy (top of the VFS) — {dupPath ?? "(unresolved)"}");

                var goneWarn = r.Warnings.FirstOrDefault(w => w.Contains("Gone.esp", StringComparison.OrdinalIgnoreCase));
                Check(goneWarn is not null, "a genuinely-missing plugin still warns");
                Check(goneWarn is not null && goneWarn.Contains("overwrite", StringComparison.OrdinalIgnoreCase),
                      "…and the warning names the overwrite folder among the places searched");

                // F9-3: explicit-paths mode passes overwriteDir="" (there IS no overwrite layer), so the same warning
                // must NOT claim the overwrite folder was searched — that would overstate what was checked (Q3).
                var rExplicit = Mo2LoadOrder.Build(prof, mods, data, "");
                var goneWarnExplicit = rExplicit.Warnings.FirstOrDefault(w => w.Contains("Gone.esp", StringComparison.OrdinalIgnoreCase));
                Check(goneWarnExplicit is not null, "explicit mode (overwriteDir=\"\"): a missing plugin still warns");
                Check(goneWarnExplicit is not null && !goneWarnExplicit.Contains("overwrite", StringComparison.OrdinalIgnoreCase),
                      "…and the warning does NOT name the overwrite folder (it was never searched in explicit mode)");
            }

            // ---- arms 4-5: the real service against a synthetic instance with real plugin bytes ----
            Console.WriteLine();
            Console.WriteLine("--- 4-5: service end-to-end (a real plugin living only in overwrite) ---");
            {
                string instance = Path.Combine(root, "instance");
                string profiles = Path.Combine(instance, "profiles", "Default");
                string mods = Path.Combine(instance, "mods");
                string ovw = Path.Combine(instance, "overwrite");
                string data = Path.Combine(root, "game", "Data");
                Directory.CreateDirectory(profiles); Directory.CreateDirectory(Path.Combine(mods, "MasterMod"));
                Directory.CreateDirectory(ovw); Directory.CreateDirectory(data);
                File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
                    "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
                    + Path.Combine(root, "game").Replace(@"\", @"\\") + ")\r\n");

                var mKey = new ModKey("HcOvwMaster", ModType.Master);
                {
                    var m = new SkyrimMod(mKey, SkyrimRelease.SkyrimSE);
                    var w = m.Weapons.AddNew(); w.EditorID = "HcOvwBase"; w.BasicStats = new WeaponBasicStats { Damage = 10 };
                    m.BeginWrite.ToPath(Path.Combine(mods, "MasterMod", mKey.FileName.String)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
                }
                var tKey = new ModKey("HcOvwTool", ModType.Plugin);
                FormKey toolFk;
                {
                    var t = new SkyrimMod(tKey, SkyrimRelease.SkyrimSE);
                    var w = t.Weapons.AddNew(); w.EditorID = "HcOvwToolW"; w.BasicStats = new WeaponBasicStats { Damage = 42 };
                    toolFk = w.FormKey;
                    t.BeginWrite.ToPath(Path.Combine(ovw, tKey.FileName.String)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
                }

                // arm 5 starts BEFORE the tool plugin is in the profile (the moment a tool just wrote it to overwrite)
                File.WriteAllText(Path.Combine(profiles, "loadorder.txt"), "# header\r\n" + mKey.FileName + "\r\n");
                File.WriteAllText(Path.Combine(profiles, "plugins.txt"), "*" + mKey.FileName + "\r\n");
                File.WriteAllText(Path.Combine(profiles, "modlist.txt"), "# header\r\n+MasterMod\r\n");

                var store = new UserConfigStore(Path.Combine(root, "user.json"));
                using var svc = LoadOrderService.WithInstance(instance, 0, store);
                Check(svc.Stats().plugins == 1, "baseline order resolved (overwrite plugin not yet in the profile)");

                // MO2 refresh after the tool ran: the profile files now list the overwrite-resident plugin
                File.WriteAllText(Path.Combine(profiles, "loadorder.txt"), "# header\r\n" + mKey.FileName + "\r\n" + tKey.FileName + "\r\n");
                File.WriteAllText(Path.Combine(profiles, "plugins.txt"), "*" + mKey.FileName + "\r\n*" + tKey.FileName + "\r\n");
                // Guarantee the change is detected by value: a warm run can rewrite within one OS timer tick (~15ms)
                // of the baseline stat, leaving the mtime identical — real MO2 refreshes are always much later.
                File.SetLastWriteTimeUtc(Path.Combine(profiles, "loadorder.txt"), DateTime.UtcNow.AddHours(1));
                File.SetLastWriteTimeUtc(Path.Combine(profiles, "plugins.txt"), DateTime.UtcNow.AddHours(1));

                var status = svc.StatusData();
                Check(status.ResolvedPluginCount == 2,
                      $"the overwrite-resident plugin resolves once the profile lists it — {status.ResolvedPluginCount}/2 plugins");
                Check(status.Warnings.Count == 0,
                      $"no warning raised for it ({status.Warnings.Count} warning(s): {string.Join(" | ", status.Warnings)})");

                var read = svc.ResolveRead(toolFk, null, null, conflictTree: false);
                Check(read.Error is null && read.Record is not null && read.WinnerPlugin == tKey.FileName,
                      $"a record inside the overwrite plugin reads end-to-end — winner={read.WinnerPlugin ?? "?"}, err={read.Error ?? "none"}");

                // F9-4: the setup confirmation (housecarl_set_mo2_instance) lists the overwrite root among the derived
                // roots, not silently omitted — it's a real load-order root the user should see.
                var confirm = SetupTools.Render(Mo2Instance.Resolve(instance), persisted: true, persistError: null, persistNote: null);
                Check(confirm.Contains(ovw, StringComparison.OrdinalIgnoreCase),
                      "the setup confirmation lists the overwrite root among the derived roots (hunt F9-4)");
            }
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* temp scratch */ } }

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "================ ALL PASS ================" : $"================ {fail} CHECK(S) FAILED ================");
        return fail == 0 ? 0 : 1;
    }
}
