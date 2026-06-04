using System.Diagnostics;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlGenerator;

/// <summary>
/// Cleanup-gotcha / Option-B viability probe — the file-HANDLE lifecycle of a Mutagen overlay.
///
/// Context: the retrospective named the "cleanup-gotcha" — houseCARL mmap'ing plugin files keeps a
/// Windows handle open, so MO2/xEdit/Explorer can't delete/rename/overwrite them until release
/// (RETROSPECTIVE_PIVOT_2026-05-23 §"Cleanup-gotcha is unusable-class"; "Cannot ship v1.0 with this
/// limitation"). The legacy FILE_SHARE_DELETE fix lived in the custom mmap reader the rebuild REPLACED
/// with Mutagen's overlay; a Codex tester re-hit the lock on the shipped build. Aaron LOCKED Option B:
/// houseCARL holds ZERO plugin handles at rest — open a plugin only to read, dispose at the end of the
/// tool call. This probe PROVES B is viable (it does NOT decide — the decision is locked):
///
///   ARM 1 — bug + B's core mechanic, on a throwaway temp COPY (never live data):
///     (a) a plain SkyrimMod.CreateFromBinaryOverlay LOCKS the file (move/delete throw while open);
///     (b) Dispose() RELEASES it, and WHETHER release is deterministic (right after Dispose) or needs a
///         GC (which would weaken B — non-deterministic release).
///
///   ARM 2 — B's cost, on REAL plugins read-only IN PLACE (reuses ResolveProbe.GatherPlugins):
///     open -> read a record body -> dispose, per-fetch latency (single + steady-state) and the worst
///     case — ONE tool call fetching bodies across hundreds of plugins (a deep conflict tree, e.g.
///     depth-879 Tamriel), plus a RAM leak check over the churn (does Dispose actually free the mmap?).
///
/// Run: dotnet run --project src/housecarl-generator handle-probe [worstCaseN]   (default worstCaseN=300)
/// </summary>
public static class HandleProbe
{
    public static int RunProbe(string[] args)
    {
        int worstCaseN = args.Length > 0 && int.TryParse(args[0], out var n) ? n : 300;

        Console.WriteLine("================================================================");
        Console.WriteLine(" houseCARL handle-probe — cleanup-gotcha / Option-B viability");
        Console.WriteLine("================================================================");

        var paths = ResolveProbe.GatherPlugins();
        if (paths.Count == 0)
        {
            Console.Error.WriteLine("error: no plugins found (ResolveProbe.GatherPlugins returned empty — check game roots).");
            return 1;
        }
        Console.WriteLine($"Real plugin set: {paths.Count} plugins (read-only; ARM 1 COPIES the smallest, ARM 2 reads in place).");

        bool bugConfirmed = false;
        string releaseVerdict = "(not reached)";
        string tmpDir = Path.Combine(Path.GetTempPath(), "hc-handle-probe");

        // ================================================================
        //  ARM 1 — lock + release, on a throwaway temp COPY (safe).
        // ================================================================
        Console.WriteLine();
        Console.WriteLine("---- ARM 1: a plain overlay LOCKS the file; Dispose() RELEASES it (temp copy) ----");
        try
        {
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true);
            Directory.CreateDirectory(tmpDir);

            // Smallest plugin that is SUBSTANTIVE (>=20 KB) — skips empty placeholder esps so the
            // lock/release is proven on a genuinely record-bearing, faulted-in mmap, not a header stub.
            var smallest = paths.Select(p => new FileInfo(p)).Where(fi => fi.Length >= 20_000)
                                .OrderBy(fi => fi.Length).First();
            var current = Path.Combine(tmpDir, smallest.Name);
            File.Copy(smallest.FullName, current);
            Console.WriteLine($"   subject: copied {smallest.Name} ({smallest.Length / 1024.0:N0} KB) -> temp");

            // Open the overlay the EXACT way the live resolver does, and force the mmap + a real read.
            var ov = SkyrimMod.CreateFromBinaryOverlay(current, SkyrimRelease.SkyrimSE);
            var first = ov.EnumerateMajorRecords().FirstOrDefault();
            Console.WriteLine($"   opened overlay; first record: " +
                              (first is null ? "(none)" : $"{first.FormKey} EDID={first.EditorID ?? "(none)"}"));

            // (a) WHILE OPEN: move + delete should both be BLOCKED (the lock = the bug).
            var moved = current + ".moved";
            bool moveOk = TryFileOp(() => File.Move(current, moved), out var moveErr);
            if (moveOk) current = moved;                       // unexpected: track where it went
            bool delOk = TryFileOp(() => File.Delete(current), out var delErr);
            Console.WriteLine($"   while OPEN  move:   {(moveOk ? "SUCCEEDED (UNEXPECTED)" : "BLOCKED")}  [{moveErr}]");
            Console.WriteLine($"   while OPEN  delete: {(delOk ? "SUCCEEDED (UNEXPECTED)" : "BLOCKED")}  [{delErr}]");
            bugConfirmed = !moveOk && !delOk;

            // (b) Dispose -> the file must become deletable. Test deterministic (post-Dispose) vs GC-dependent.
            (ov as IDisposable)?.Dispose();
            bool fileStillThere = File.Exists(current);
            if (!fileStillThere)
            {
                releaseVerdict = "n/a — file was removed WHILE OPEN (Mutagen already permits it; the lock did NOT reproduce)";
            }
            else
            {
                bool freed = TryFileOp(() => File.Delete(current), out var relErr);
                if (freed)
                {
                    releaseVerdict = "RELEASED by Dispose() — deterministic. B's core mechanic CONFIRMED.";
                }
                else
                {
                    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                    bool freedAfterGc = TryFileOp(() => File.Delete(current), out relErr);
                    releaseVerdict = freedAfterGc
                        ? "released only AFTER GC — Dispose() is NOT sufficient on its own (B weaker: needs explicit dispose + the handle still lingers to a GC)."
                        : $"STILL LOCKED after Dispose()+GC — handle LEAK; B is broken as-is [{relErr}].";
                }
            }
            Console.WriteLine($"   after Dispose:      {releaseVerdict}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"   !! ARM 1 threw: {ex.GetType().Name}: {ex.Message}");
        }

        // ================================================================
        //  ARM 2 — open->read->dispose cost on REAL plugins, read-only in place.
        // ================================================================
        Console.WriteLine();
        int sample = Math.Min(worstCaseN, paths.Count);
        var subjects = paths.Take(sample).ToList();            // masters first (big) then mods — representative
        Console.WriteLine($"---- ARM 2: open -> read a record -> dispose, over {subjects.Count} REAL plugins in place ----");

        var memBefore = ResolveProbe.Mem();
        var perFetch = new List<double>(subjects.Count);
        int readOk = 0, noRec = 0, openFail = 0;
        var swAll = Stopwatch.StartNew();
        foreach (var p in subjects)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var o = SkyrimMod.CreateFromBinaryOverlay(p, SkyrimRelease.SkyrimSE);
                var r = o.EnumerateMajorRecords().FirstOrDefault();
                if (r is null) noRec++;
                else { _ = r.EditorID; _ = r.FormKey; readOk++; }   // force a real body touch (EDID parse)
                (o as IDisposable)?.Dispose();
            }
            catch { openFail++; }
            sw.Stop();
            perFetch.Add(sw.Elapsed.TotalMilliseconds);
        }
        swAll.Stop();
        var memAfter = ResolveProbe.Mem();

        perFetch.Sort();
        double Pct(double q) => perFetch.Count == 0 ? 0 : perFetch[Math.Min(perFetch.Count - 1, (int)(q * perFetch.Count))];
        double median = Pct(0.50), p95 = Pct(0.95), max = perFetch.Count > 0 ? perFetch[^1] : 0,
               min = perFetch.Count > 0 ? perFetch[0] : 0, avg = perFetch.Count > 0 ? perFetch.Average() : 0;

        Console.WriteLine($"   fetches: {subjects.Count}  (read {readOk} | no-record {noRec} | open-fail {openFail})");
        Console.WriteLine($"   per-fetch ms: min {min:N2} | median {median:N2} | avg {avg:N2} | p95 {p95:N2} | max {max:N2}");
        Console.WriteLine($"   one tool call touching all {subjects.Count} plugins: {swAll.Elapsed.TotalMilliseconds:N0} ms total");
        Console.WriteLine($"   extrapolated WORST tree (depth 879) = 879 x median = ~{879 * median:N0} ms");
        ResolveProbe.PrintDelta("RAM over the churn (leak check)", memBefore, memAfter);

        // ================================================================
        //  RESULT — the numbers the handoff quotes.
        // ================================================================
        Console.WriteLine();
        Console.WriteLine("================================================================");
        Console.WriteLine(" handle-probe RESULT (Option B is LOCKED; this proves it viable)");
        Console.WriteLine("================================================================");
        Console.WriteLine($"   bug reproduced (open LOCKS file) : {(bugConfirmed ? "YES — move + delete both BLOCKED while open" : "NO — did not lock (see ARM 1)")}");
        Console.WriteLine($"   B mechanic (Dispose releases)    : {releaseVerdict}");
        Console.WriteLine($"   B cost (median open/read/dispose): {median:N2} ms | worst whole-tree ~{879 * median:N0} ms (LLM round-trip is 200-2000 ms)");
        Console.WriteLine($"   leak over {subjects.Count} cycles          : managed {ResolveProbe.MB(memAfter.managed - memBefore.managed)} | ws {ResolveProbe.MB(memAfter.ws - memBefore.ws)} (≈flat = Dispose frees the mmap)");

        // cleanup (the temp file is usually gone via the release proof; remove the dir best-effort).
        try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true); }
        catch (Exception ex) { Console.WriteLine($"   (temp cleanup left {tmpDir}: {ex.GetType().Name} — likely still locked, telling in itself)"); }

        return 0;
    }

    static bool TryFileOp(Action op, out string err)
    {
        try { op(); err = "ok"; return true; }
        catch (Exception ex) { err = $"{ex.GetType().Name}: {ex.Message}"; return false; }
    }
}
