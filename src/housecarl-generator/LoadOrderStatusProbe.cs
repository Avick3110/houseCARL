using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// Instance-describe + named-profile read guard (HCBR-2026-06-15-01 item 9.2 / PR-I). housecarl_load_order_status used to
/// report the profile NAME but never the resolved INSTANCE PATH (which MO2 instance houseCARL is pointed at — easy to lose
/// track of), and it could not inspect an INACTIVE profile. The fix surfaces the instance path and adds a profile= read
/// that reports any sibling profile's composition WITHOUT switching to it. The Q3 hazards this guards:
///   • the instance path must come from the SAME gated snapshot as the rest of the status line (never re-derived);
///   • inspecting an inactive profile must NOT switch the active profile and must NOT build the record index (cheap text
///     parse only);
///   • an unknown profile name must NAME the available options, never render a silently-empty composition;
///   • explicit-paths mode has no profiles root, so a named read must refuse LOUD there, never enumerate an arbitrary dir.
///
/// Arms (each asserts the service DATA and the RENDERED text the user actually sees):
///   A  instance path surfaced — instance mode: StatusData().InstanceDir == the configured folder; header renders "instance: &lt;path&gt;".
///   B  explicit-paths mode — a REAL WithExplicitPaths service (not ForGuard): InstanceDir == null; header renders "explicit-paths mode".
///   C  named/inactive read — a two-profile instance (Default active, Second inactive, DISTINCT comps): profile='Second'
///      returns Second's composition (≠ Default's), the active profile stays 'Default' (no switch), match is case-insensitive.
///   D  not found — an unknown name yields no composition but NAMES the available profiles (Q3); the render says so.
///   E  explicit refusal — WithExplicitPaths + a named read refuses loud (InstanceMode false); the render explains why.
///   F  discovery — no name in instance mode lists the available profiles; the default status renders the discovery line.
///   G  base_directory redirect — profiles under a redirected base are still found (root = parent of the derived ProfileDir,
///      so the redirect is honored BY CONSTRUCTION — guards against a future re-derivation from the instance dir).
///   H  stray-folder filter — a folder under profiles/ with no loadorder.txt (never-opened profile / backup dir) is NOT
///      listed or matched, so it can't be offered or read back as an all-zero phantom profile (Q3 — pre-push review fold).
///
/// Self-contained: synthetic MO2 instances + one synthesized master plugin in temp; no game data, no corpus (reads only).
/// </summary>
internal static class LoadOrderStatusProbe
{
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("================================================================");
        Console.WriteLine(" load-order-status guard — instance path + named/inactive-profile read (9.2)");
        Console.WriteLine("================================================================");
        Console.WriteLine();
        int fail = 0;
        void Check(bool c, string label) { Console.WriteLine((c ? "  PASS  " : "  FAIL  ") + label); if (!c) fail++; }

        var root = Path.Combine(Path.GetTempPath(), "hc-losstatus-guard-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "game", "Data"));   // the ini's gamePath target (shared)

            // ---- one synthesized master plugin (so the active order resolves to real bytes for StatusData) ----
            var masterKey = new ModKey("HcLosMaster", ModType.Master);
            var extraKey = new ModKey("HcLosExtra", ModType.Plugin);
            string masterName = masterKey.FileName.String, extraName = extraKey.FileName.String;
            var masterFile = Path.Combine(root, masterName);
            var master = new SkyrimMod(masterKey, SkyrimRelease.SkyrimSE);
            var w = master.Weapons.AddNew(); w.EditorID = "HcLosW0"; w.BasicStats = new WeaponBasicStats { Damage = 10, Weight = 1 };
            master.BeginWrite.ToPath(masterFile).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

            var logs = Array.Empty<LogFolderView>();   // render needs a log list; the log surface is out of scope here
            string Render(LoadOrderService svc, NamedProfileResult profiles, string? profileReq) =>
                StatusWire.Render(svc.StatusData(), logs, profiles, lookup: null, cap: 80_000);

            void WriteIni(string inst, string profile, string? baseDir = null)
            {
                var b = baseDir is null ? "" : "base_directory=@ByteArray(" + baseDir.Replace(@"\", @"\\") + ")\r\n";
                File.WriteAllText(Path.Combine(inst, Mo2Instance.IniFileName),
                    "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(" + profile + ")\r\ngamePath=@ByteArray("
                    + Path.Combine(root, "game").Replace(@"\", @"\\") + ")\r\n" + (baseDir is null ? "" : "[Settings]\r\n" + b));
            }
            void WriteProfile(string profDir, string[] loadorder, string[] plugins, string[] modlist)
            {
                Directory.CreateDirectory(profDir);
                File.WriteAllText(Path.Combine(profDir, "loadorder.txt"), "# header\r\n" + string.Join("\r\n", loadorder) + "\r\n");
                File.WriteAllText(Path.Combine(profDir, "plugins.txt"), string.Join("\r\n", plugins) + "\r\n");
                File.WriteAllText(Path.Combine(profDir, "modlist.txt"), "# header\r\n" + string.Join("\r\n", modlist) + "\r\n");
            }
            // A usable instance under baseRoot (mods/ + profiles/ live there): mods/MasterMod has the master bytes.
            string MakeInstance(string name, string? baseDir = null)
            {
                var inst = Path.Combine(root, name);
                var b = baseDir ?? inst;
                Directory.CreateDirectory(Path.Combine(b, "mods", "MasterMod"));
                File.Copy(masterFile, Path.Combine(b, "mods", "MasterMod", masterName));
                Directory.CreateDirectory(inst);
                WriteIni(inst, "Default", baseDir);
                return inst;
            }

            // ---- A: instance mode surfaces the resolved instance path ----
            Console.WriteLine("--- A: instance path surfaced (instance mode) ---");
            string instA = MakeInstance("inst-a");
            WriteProfile(Path.Combine(instA, "profiles", "Default"), new[] { masterName }, new[] { "*" + masterName }, new[] { "+MasterMod" });
            // One store, shared by every arm: nothing here calls SetInstance / set_tool_path, so houseCARL.user.json is
            // never written — each arm points a fresh service at its own synthetic instance.
            var store = new UserConfigStore(Path.Combine(root, "user.json"));
            using (var svc = LoadOrderService.WithInstance(instA, 0, store))
            {
                var data = svc.StatusData();
                Check(data.InstanceDir == instA, $"InstanceDir == the configured instance folder (got '{data.InstanceDir}')");
                Check(data.ProfileName == "Default", $"ProfileName == 'Default' (got '{data.ProfileName}')");
                var text = Render(svc, svc.NamedProfileComposition(null), null);
                Check(text.Contains("instance: " + instA), "rendered header carries the 'instance: <path>' line");
            }

            // ---- B: explicit-paths mode renders "explicit-paths mode" (REAL WithExplicitPaths, not ForGuard) ----
            Console.WriteLine();
            Console.WriteLine("--- B: explicit-paths mode (InstanceDir null) — real WithExplicitPaths over synthesized plugins ---");
            string instB = MakeInstance("inst-b");
            string profB = Path.Combine(instB, "profiles", "Default");
            WriteProfile(profB, new[] { masterName }, new[] { "*" + masterName }, new[] { "+MasterMod" });
            using (var svc = LoadOrderService.WithExplicitPaths(Path.Combine(root, "game", "Data"), Path.Combine(instB, "mods"), profB, 0, store))
            {
                var data = svc.StatusData();
                Check(data.InstanceDir is null, "explicit-paths mode → InstanceDir is null");
                var text = Render(svc, svc.NamedProfileComposition(null), null);
                Check(text.Contains("explicit-paths mode"), "rendered header shows 'explicit-paths mode' (not a bogus path)");
            }

            // ---- C: named/inactive read — Second's composition, active profile unchanged ----
            Console.WriteLine();
            Console.WriteLine("--- C: inspect an INACTIVE profile without switching to it ---");
            string instC = MakeInstance("inst-c");
            WriteProfile(Path.Combine(instC, "profiles", "Default"), new[] { masterName }, new[] { "*" + masterName }, new[] { "+MasterMod" });
            WriteProfile(Path.Combine(instC, "profiles", "Second"), new[] { masterName, extraName },
                         new[] { "*" + masterName, "*" + extraName }, new[] { "+MasterMod", "+ExtraMod" });
            using (var svc = LoadOrderService.WithInstance(instC, 0, store))
            {
                var second = svc.NamedProfileComposition("Second");
                Check(second.InstanceMode && second.Composition is not null, "Second resolved (instance mode, composition present)");
                Check(second.Composition!.EnabledMods.Count == 2, $"Second sees 2 enabled mods (got {second.Composition.EnabledMods.Count})");
                Check(second.Composition.ActivePluginNames.Contains(extraName), "Second's active plugins include the extra plugin");
                Check(svc.ProfileName == "Default", $"the active profile is STILL 'Default' — the named read did not switch (got '{svc.ProfileName}')");

                var def = svc.NamedProfileComposition("Default");
                Check(def.Composition!.EnabledMods.Count == 1, $"Default sees 1 enabled mod — the two profiles are genuinely DISTINCT (got {def.Composition.EnabledMods.Count})");
                Check(svc.NamedProfileComposition("second").Composition is not null, "name match is case-insensitive ('second' finds 'Second')");

                var text = Render(svc, second, "Second");
                Check(text.Contains("inspecting profile 'Second'") && text.Contains("active profile is unchanged"),
                      "render shows the named inspection and states the active profile is unchanged");

                // ---- D: not-found names the available profiles (Q3), never a silent empty composition ----
                Console.WriteLine();
                Console.WriteLine("--- D: an unknown profile name lists the available ones (Q3) ---");
                var nf = svc.NamedProfileComposition("Nope");
                Check(nf.InstanceMode && nf.Composition is null, "unknown name → no composition (not a silent empty)");
                Check(nf.AvailableProfiles.Contains("Default") && nf.AvailableProfiles.Contains("Second"), "not-found result NAMES the available profiles");
                var nfText = Render(svc, nf, "Nope");
                Check(nfText.Contains("not found") && nfText.Contains("Default") && nfText.Contains("Second"),
                      "render says 'not found' and lists the real options");

                // ---- F: discovery line in the default status ----
                Console.WriteLine();
                Console.WriteLine("--- F: the default status lists the available profiles (discovery) ---");
                var disc = svc.NamedProfileComposition(null);
                Check(disc.InstanceMode && disc.RequestedName is null && disc.AvailableProfiles.Count == 2, "no name → discovery list of both profiles");
                var discText = Render(svc, disc, null);
                Check(discText.Contains("profiles available"), "default status renders the 'profiles available' discovery line");
            }

            // ---- E: explicit-paths mode refuses a named read LOUD (no profiles root) ----
            Console.WriteLine();
            Console.WriteLine("--- E: explicit-paths mode refuses a named-profile read (no profiles root) ---");
            using (var svc = LoadOrderService.WithExplicitPaths(Path.Combine(root, "game", "Data"), Path.Combine(instB, "mods"), profB, 0, store))
            {
                var r = svc.NamedProfileComposition("whatever");
                Check(!r.InstanceMode && r.Composition is null, "explicit mode → named read refused (InstanceMode false, no composition)");
                var text = Render(svc, r, "whatever");
                Check(text.Contains("MO2-instance mode"), "render explains the named read needs instance mode");
            }

            // ---- G: base_directory redirect — profiles under the redirected base are still found (by construction) ----
            Console.WriteLine();
            Console.WriteLine("--- G: base_directory redirect — root = parent of the derived ProfileDir (honored by construction) ---");
            string baseG = Path.Combine(root, "base-g");
            string instG = MakeInstance("inst-g", baseDir: baseG);   // mods/ + profiles/ live under baseG, ini points there
            WriteProfile(Path.Combine(baseG, "profiles", "Default"), new[] { masterName }, new[] { "*" + masterName }, new[] { "+MasterMod" });
            WriteProfile(Path.Combine(baseG, "profiles", "Second"), new[] { masterName, extraName },
                         new[] { "*" + masterName, "*" + extraName }, new[] { "+MasterMod", "+ExtraMod" });
            using (var svc = LoadOrderService.WithInstance(instG, 0, store))
            {
                var second = svc.NamedProfileComposition("Second");
                Check(second.InstanceMode && second.Composition is not null && second.Composition.EnabledMods.Count == 2,
                      "a profile under the redirected base_directory is found and read correctly");
                Check(second.AvailableProfiles.Contains("Default") && second.AvailableProfiles.Contains("Second"),
                      "the profiles root resolved through base_directory (both siblings enumerated)");
            }

            // ---- H: a stray / never-opened folder under profiles/ (no loadorder.txt) is NOT offered or matched ----
            Console.WriteLine();
            Console.WriteLine("--- H: a folder under profiles/ with no loadorder.txt is skipped (Q3 — never an all-zero phantom profile) ---");
            string instH = MakeInstance("inst-h");
            WriteProfile(Path.Combine(instH, "profiles", "Default"), new[] { masterName }, new[] { "*" + masterName }, new[] { "+MasterMod" });
            Directory.CreateDirectory(Path.Combine(instH, "profiles", "_NotAProfile"));   // a stray dir — no loadorder.txt (e.g. a backup or never-opened profile)
            using (var svc = LoadOrderService.WithInstance(instH, 0, store))
            {
                var disc = svc.NamedProfileComposition(null);
                Check(disc.AvailableProfiles.Contains("Default") && !disc.AvailableProfiles.Contains("_NotAProfile"),
                      "the stray folder is excluded from the available profiles (only loadorder.txt-bearing dirs)");
                var miss = svc.NamedProfileComposition("_NotAProfile");
                Check(miss.InstanceMode && miss.Composition is null,
                      "requesting the stray folder is a clean not-found, not an all-zero composition");
            }
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* temp scratch */ } }

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "================ ALL PASS ================" : $"================ {fail} CHECK(S) FAILED ================");
        return fail == 0 ? 0 : 1;
    }
}
