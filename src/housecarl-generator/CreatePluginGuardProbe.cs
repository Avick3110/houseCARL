using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// SELF-CONTAINED CI REGRESSION GUARD for CREATE-PLUGIN (HCBR-2026-06-19-02 — "no way to author a placeholder /
/// header-only trigger plugin"). houseCARL's write model is record-centric: a plugin existed only as a side effect of
/// containing a record, so a basename-bound SKSE config trigger (CraftingCategories-style) had to be materialised WITH
/// a junk filler record. housecarl_create_plugin authors a valid TES4 header with ZERO records — the clean primitive.
///
/// Two layers, both required for a GREEN to mean "the contract holds":
///   CORE arms (WritePatchBuilder.CreatePlugin straight to a temp path — no MO2, no Skyrim.esm):
///     HEADER-ONLY-ESP — esl=false → 0 records, 0 masters (Aaron 2026-06-23: an empty plugin carries none), NOT
///                       ESL-flagged, first 4 bytes "TES4", author/description survive the write+reopen.
///     HEADER-ONLY-ESL — esl=true → the light-master (IsSmallMaster) flag survives reopen; still 0 records / 0 masters.
///   SERVICE arms (the REAL LoadOrderService.CreatePlugin over a synthetic MO2 instance — the bulk-create-guard synth):
///     WIRE-CREATE    — writes the EXACT-named plugin ('HcCpTrigger.esp', NOT auto-suffixed) in a 'houseCARL - …' folder.
///     REJ-FOLDER     — re-creating the SAME name refuses loud ('already exists') with no second folder (no auto-suffix:
///                      the basename is load-bearing for the trigger, so houseCARL refuses rather than rename).
///     REJ-NAME-ACTIVE— naming a plugin already ACTIVE in the order refuses loud ('already active'), no folder (a second
///                      plugin of that basename would shadow it — MO2 picks one by mod order).
///     ESL-WIRE       — esl=true through the service lands a light-flagged plugin on disk.
///
/// Run: dotnet run --project src/housecarl-generator -- create-plugin-guard
/// </summary>
public static class CreatePluginGuardProbe
{
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("create-plugin-guard — author an empty, header-only (trigger) plugin (HCBR-2026-06-19-02)");
        Console.WriteLine();

        int failures = 0;
        void Check(string label, bool ok, string? detail = null)
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {label}{(ok || detail is null ? "" : $"\n        -> {detail}")}");
            if (!ok) failures++;
        }

        var root = Path.Combine(Path.GetTempPath(), "hc-create-plugin-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // ===== CORE arms (self-contained: WritePatchBuilder.CreatePlugin to a temp path, no MO2) =====

            // HEADER-ONLY-ESP: a full (non-ESL) header-only plugin round-trips clean.
            {
                string p = Path.Combine(root, "HcCpEsp.esp");
                var o = WritePatchBuilder.CreatePlugin(p, esl: false, author: "houseCARL", description: "trigger plugin");
                bool tes4 = File.Exists(p) && IsTes4(p);
                var (recs, masters, eslBack, author, desc) = o.Success ? Reopen(p) : (-1, new List<string>(), (bool?)null, null, null);
                Check("HEADER-ONLY-ESP: 0 records, 0 masters, NOT ESL, sig TES4, author/description round-trip",
                    o.Success && o.RecordCount == 0 && o.Masters.Count == 0 && !o.Esl && tes4
                        && recs == 0 && masters.Count == 0 && eslBack == false && author == "houseCARL" && desc == "trigger plugin",
                    $"success={o.Success} recs={recs} masters=[{string.Join(",", masters)}] esl={eslBack} tes4={tes4} author='{author}' desc='{desc}' err=[{Trim(o.Error)}]");
            }

            // HEADER-ONLY-ESL: esl=true sets the light-master flag (survives reopen); still empty.
            {
                string p = Path.Combine(root, "HcCpEslCore.esp");
                var o = WritePatchBuilder.CreatePlugin(p, esl: true, author: null, description: null);
                var (recs, masters, eslBack, _, _) = o.Success ? Reopen(p) : (-1, new List<string>(), (bool?)null, null, null);
                Check("HEADER-ONLY-ESL: esl=true sets the light-master flag (survives reopen); still 0 records / 0 masters",
                    o.Success && o.Esl && o.RecordCount == 0 && eslBack == true && recs == 0 && masters.Count == 0,
                    $"success={o.Success} esl={eslBack} recs={recs} masters=[{string.Join(",", masters)}] err=[{Trim(o.Error)}]");
            }

            // ===== SERVICE arms (synthetic MO2 instance → the REAL LoadOrderService.CreatePlugin) =====
            string instance = Path.Combine(root, "instance");
            string profiles = Path.Combine(instance, "profiles", "Default");
            string mods = Path.Combine(instance, "mods");
            Directory.CreateDirectory(profiles); Directory.CreateDirectory(mods);
            Directory.CreateDirectory(Path.Combine(root, "game", "Data"));
            File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
                "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
                + Path.Combine(root, "game").Replace(@"\", @"\\") + ")\r\n");

            var mKey = new ModKey("HcCpMaster", ModType.Master);
            var modDir = Path.Combine(mods, "MasterMod");
            Directory.CreateDirectory(modDir);
            {
                var m = new SkyrimMod(mKey, SkyrimRelease.SkyrimSE);
                var kw = m.Keywords.AddNew(); kw.EditorID = "HcCpKw";   // one record so the master is a real plugin in the order
                m.BeginWrite.ToPath(Path.Combine(modDir, mKey.FileName.String)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
            }
            File.WriteAllText(Path.Combine(profiles, "loadorder.txt"), "# header\r\n" + mKey.FileName + "\r\n");
            File.WriteAllText(Path.Combine(profiles, "plugins.txt"), "*" + mKey.FileName + "\r\n");
            File.WriteAllText(Path.Combine(profiles, "modlist.txt"), "# header\r\n+MasterMod\r\n");

            var store = new UserConfigStore(Path.Combine(root, "houseCARL.user.json"));
            using var svc = LoadOrderService.WithInstance(instance, 0, store);
            svc.Stats();   // warm the lazy index once

            // WIRE-CREATE + EXACT-NAME: the service writes the EXACT-named header-only plugin in a houseCARL folder.
            {
                var o = svc.CreatePlugin("HcCpTrigger");
                bool exactName = o.Success && Path.GetFileName(o.OutputPath) == "HcCpTrigger.esp";
                bool folderOk = o.Success && Path.GetFileName(Path.GetDirectoryName(o.OutputPath)!) == "houseCARL - HcCpTrigger";
                bool onDisk = o.Success && File.Exists(o.OutputPath) && IsTes4(o.OutputPath);
                Check("WIRE-CREATE: service writes the EXACT-named header-only plugin in a houseCARL folder (no auto-suffix)",
                    o.Success && exactName && folderOk && onDisk && o.RecordCount == 0,
                    $"success={o.Success} file={(o.Success ? Path.GetFileName(o.OutputPath) : "")} exactName={exactName} folderOk={folderOk} onDisk={onDisk} err=[{Trim(o.Error)}]");
            }

            // REJ-FOLDER: a second create of the SAME name refuses loud (no auto-suffix), no second folder.
            {
                var o = svc.CreatePlugin("HcCpTrigger");
                bool refused = !o.Success && o.Error is not null && o.Error.Contains("already exists", StringComparison.OrdinalIgnoreCase);
                int folderCount = Directory.EnumerateDirectories(mods, "houseCARL - HcCpTrigger*").Count();
                Check("REJ-FOLDER: re-creating the same name refuses loud (no auto-suffix); exactly one folder remains",
                    refused && folderCount == 1, $"refused={refused} folderCount={folderCount} err=[{Trim(o.Error)}]");
            }

            // REJ-NAME-ACTIVE: naming a plugin already ACTIVE in the order refuses loud (would shadow it), no folder.
            {
                var o = svc.CreatePlugin("HcCpMaster");   // the synthesized master, active in the order (as HcCpMaster.esm)
                bool refused = !o.Success && o.Error is not null && o.Error.Contains("already active", StringComparison.OrdinalIgnoreCase);
                bool noFolder = !Directory.EnumerateDirectories(mods, "houseCARL - HcCpMaster*").Any();
                Check("REJ-NAME-ACTIVE: naming an already-active plugin refuses loud (no shadow), no folder",
                    refused && noFolder, $"refused={refused} noFolder={noFolder} err=[{Trim(o.Error)}]");
            }

            // ESL-WIRE: esl=true through the service → the on-disk plugin is light-flagged.
            {
                var o = svc.CreatePlugin("HcCpEslWire", esl: true);
                var (_, _, eslBack, _, _) = o.Success ? Reopen(o.OutputPath) : (-1, new List<string>(), (bool?)null, null, null);
                Check("ESL-WIRE: esl=true through the service lands a light-flagged plugin on disk",
                    o.Success && o.Esl && eslBack == true, $"success={o.Success} esl={eslBack} err=[{Trim(o.Error)}]");
            }

            // COLD-START (PR #105 review #1): create_plugin as the FIRST op on a fresh service (no prior Stats()/read
            // to warm the lazy index) must derive ModsDir itself and SUCCEED — not misreport "ModsDir '' does not
            // exist". A separate, un-warmed service over the same instance exercises the cold path the warmed `svc`
            // above masks. RED before the fix (derive paths before the _modsDir check), GREEN after.
            {
                using var cold = LoadOrderService.WithInstance(instance, 0, store);
                var o = cold.CreatePlugin("HcCpCold");
                Check("COLD-START: create_plugin as the first op on a fresh service succeeds (derives ModsDir; no false config error)",
                    o.Success && Path.GetFileName(o.OutputPath) == "HcCpCold.esp",
                    $"success={o.Success} file={(o.Success ? Path.GetFileName(o.OutputPath) : "")} err=[{Trim(o.Error)}]");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: guard infrastructure: {ex.GetType().Name}: {(ex.InnerException ?? ex).Message}");
            failures++;
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ } }

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "create-plugin-guard: ALL PASS" : $"create-plugin-guard: {failures} FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }

    // ---- helpers ----

    /// <summary>First 4 bytes == ASCII "TES4" (the mod header signature — a valid plugin always opens with it).</summary>
    static bool IsTes4(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> b = stackalloc byte[4];
            return fs.Read(b) == 4 && b[0] == (byte)'T' && b[1] == (byte)'E' && b[2] == (byte)'S' && b[3] == (byte)'4';
        }
        catch { return false; }
    }

    /// <summary>Re-open the written plugin as a binary overlay and read back what the contract promises:
    /// (record count, master filenames, IsSmallMaster, Author, Description).</summary>
    static (int recs, List<string> masters, bool? esl, string? author, string? desc) Reopen(string path)
    {
        ISkyrimModGetter? back = null;
        try
        {
            back = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
            return (back.EnumerateMajorRecords().Count(),
                back.ModHeader.MasterReferences.Select(m => m.Master.FileName.ToString()).ToList(),
                back.IsSmallMaster, back.ModHeader.Author, back.ModHeader.Description);
        }
        catch { return (-1, new List<string>(), null, null, null); }
        finally { (back as IDisposable)?.Dispose(); }
    }

    static string Trim(string? s) => s is null ? "" : (s.Length <= 160 ? s.Replace("\n", " ") : s[..160].Replace("\n", " ") + "…");
}
