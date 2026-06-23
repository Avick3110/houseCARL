using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// EXPLORATORY / REAL-DATA probe for the IN-PLACE WRITE LANE, Wave 0 (the design-gating measurement —
/// dev/plans/IN_PLACE_WRITE_LANE_PLAN_2026-06-13.md §5.3 / §9 STEP-2). It answers the one fork the planning
/// chair CANNOT decide (fork #1, the round-trip baseline): when houseCARL re-serializes a WHOLE existing
/// plugin (the in-place lane re-emits every record, not a thin override), does it come back BYTE-IDENTICAL,
/// and if not, WHAT diverges?
///
/// Why this is the first build action. The entire houseCARL write path to date emits thin OVERRIDE patches,
/// and the standing byte-faithfulness proof (WriteOracle) is "engine override == hand-typed Mutagen override"
/// — NOT "Mutagen round-trips a whole plugin". So houseCARL has ZERO empirical evidence of Mutagen's
/// whole-plugin divergence surface beyond the named residuals (Worldspace OFST regen, Array2d/terrain grids,
/// the ~8 unparseable records). The round-trip floor (§5) is the SUBSTITUTE for the ownership marker the
/// patch lane relies on; calibrating its accept/refuse threshold (byte-strict vs semantic-tolerant) needs
/// the real divergence surface measured, not guessed.
///
/// THE MEASUREMENT (per sampled plugin):
///   1. EAGER load — SkyrimMod.CreateFromBinary(path) (the SAME call the extend write path uses;
///      WritePatchBuilder.cs:247). The SINGLE target plugin only — NEVER the load order (the legacy 12–14 GB
///      RAM trap; CLAUDE.md §1). Masters are opened LAZILY as overlays.
///   2. NO-OP RE-SERIALIZE — write the loaded mod straight back out with the product incantation MINUS the
///      FormID floor: .WithLoadOrder(<the plugin's own masters, as overlays>).NoNextFormIDProcessing().Write().
///      NoNextFormIDProcessing persists the IN-MEMORY counter verbatim, and we deliberately DO NOT call
///      EnsureFormIdFloor — so the written HEDR.NextObjectID equals the ORIGINAL. That is EXACTLY the
///      §5.1-correct in-place serialize (preserve the author's counter), so any divergence the probe sees is
///      the TRUE residual surface the floor must handle, not floor-induced noise.
///   3. BYTE-COMPARE the re-serialized temp against the original on disk; categorize any divergence.
///   4. NextObjectID, measured SEPARATELY (§5.1): report how many plugins carry a stored counter the CURRENT
///      product floor (EnsureFormIdFloor) WOULD have raised — quantifying why in-place must preserve, not floor.
///
/// CATEGORIES reported for a divergent plugin:
///   • UNLOADABLE       — CreateFromBinary threw (the read-parse residual class, incl. the 2 known unparseable
///                        PERKs). In-place would REFUSE this plugin rather than silently re-emit it minus the
///                        record Mutagen couldn't parse. Counted, never silently skipped.
///   • RECORDS-CHANGED  — the on-disk record-header SEQUENCE (sig+FormID, GRUP-recursive) differs between
///                        original and re-serialized: a dropped / added / reordered record. The dangerous class.
///   • HEADER-ONLY      — record sequence identical; first byte divergence falls inside the TES4 header region
///                        (master list / ONAM / flags / counter churn) — body records intact.
///   • BODY-DIVERGENT   — record sequence identical; divergence is in record bodies (compression re-emit,
///                        subrecord ordering, OFST regen, terrain/Array2d) — the §5.2 blind-spot territory.
///
/// This is a MANUAL/real-data probe (like esl-real-scan / conflict-diff-proof / perk-refs-proof): it needs real
/// CK-/xEdit-/Wrye-Bash-authored plugins, which a self-contained CI fixture cannot stand in for (a synthetic
/// plugin round-trips clean by construction and would reveal nothing). It writes ONLY to the system temp dir
/// and never mutates the real load order (read-only). SKIPs cleanly without --mo2.
///
/// Run: dotnet run --project src/housecarl-generator roundtrip-probe -- --mo2 &lt;instanceDir&gt; [--max N] [--plugins A.esp,B.esp]
/// </summary>
public static class RoundTripProbe
{
    public static int RunProbe(string[] args)
    {
        var f = WriteEngine.ParseFlags(args);
        var instanceDir = f.GetValueOrDefault("mo2");
        if (instanceDir is null || !Directory.Exists(instanceDir))
        {
            Console.WriteLine("SKIP: needs --mo2 <instanceDir>. The whole-plugin round-trip divergence surface can only be");
            Console.WriteLine("      measured against REAL CK-/xEdit-/Wrye-Bash-authored plugins — a synthetic fixture round-trips");
            Console.WriteLine("      clean by construction and would reveal nothing. (This is why Wave 0 is a manual probe, not a CI guard.)");
            return 0;
        }
        int max = f.TryGetValue("max", out var ms) && int.TryParse(ms, out var mm) ? mm : 60;
        var only = f.GetValueOrDefault("plugins")?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Console.WriteLine("################  IN-PLACE WAVE 0 — whole-plugin round-trip divergence surface  ################");
        Console.WriteLine($"   instance: {instanceDir}");
        Console.WriteLine();

        var p = Mo2Instance.Resolve(instanceDir);
        var order = Mo2LoadOrder.Build(p.ProfileDir, p.ModsDir, p.DataDir, p.OverwriteDir);
        var orderedPaths = order.OrderedPaths.ToList();
        // filename -> winning on-disk path (a plugin filename is unique in a load order; OrderedPaths is the
        // resolved winner per plugin). The master-resolution map for step 2's .WithLoadOrder.
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var op in orderedPaths) byName[Path.GetFileName(op)] = op;

        // Sample: explicit --plugins if given; else an even spread across the whole order (a mix of vanilla
        // masters, big overhauls, small patches — varied provenance), with the vanilla masters force-included.
        List<string> targets;
        if (only is not null)
            targets = orderedPaths.Where(op => only.Contains(Path.GetFileName(op))).ToList();
        else
        {
            var forced = new[] { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm" };
            var picked = new List<string>();
            foreach (var fn in forced) if (byName.TryGetValue(fn, out var pp)) picked.Add(pp);
            int remaining = Math.Max(0, max - picked.Count);
            if (remaining > 0 && orderedPaths.Count > 0)
            {
                var pool = orderedPaths.Where(op => !picked.Contains(op)).ToList();
                int step = Math.Max(1, pool.Count / remaining);
                for (int i = 0; i < pool.Count && picked.Count < max; i += step) picked.Add(pool[i]);
            }
            targets = picked.Distinct().ToList();
        }

        Console.WriteLine($"   load order: {orderedPaths.Count} plugins; sampling {targets.Count}{(only is null ? $" (cap --max {max})" : " (explicit --plugins)")}.");
        Console.WriteLine();

        var tmpRoot = Path.Combine(Path.GetTempPath(), "hc-roundtrip-probe");
        if (Directory.Exists(tmpRoot)) { try { Directory.Delete(tmpRoot, recursive: true); } catch { } }
        Directory.CreateDirectory(tmpRoot);

        int identical = 0, headerOnly = 0, bodyDiverge = 0, recordsChanged = 0, unloadable = 0, mastersMissing = 0, writeFailed = 0;
        int counterWouldFloor = 0;
        var notable = new List<string>();   // divergent plugins, for the per-plugin detail table

        foreach (var path in targets)
        {
            string name = Path.GetFileName(path);
            byte[] origBytes;
            try { origBytes = File.ReadAllBytes(path); }
            catch (Exception ex) { unloadable++; notable.Add($"   {name,-48} READ-FAILED  {ex.GetType().Name}"); continue; }

            SkyrimMod mod;
            try { mod = SkyrimMod.CreateFromBinary(path, SkyrimRelease.SkyrimSE); }
            catch (Exception ex)
            {
                unloadable++;
                notable.Add($"   {name,-48} UNLOADABLE   {WriteEngine.Describe(ex)}  → in-place would REFUSE (read-parse residual)");
                continue;
            }

            // §5.1 — the header counter, measured analytically (no write needed): would the CURRENT product
            // floor have raised this plugin's stored NextObjectID? (If yes, a floor-applying re-serialize would
            // diverge here — which is why in-place PRESERVES the counter instead.)
            uint storedNext = mod.ModHeader.Stats.NextFormID;
            uint floor = 0x800;
            foreach (var r in mod.EnumerateMajorRecords())
                if (r.FormKey.ModKey == mod.ModKey && r.FormKey.ID >= floor) floor = r.FormKey.ID + 1;
            bool floorWouldRaise = storedNext < floor;
            if (floorWouldRaise) counterWouldFloor++;

            // Resolve this plugin's own declared masters to on-disk overlays for a faithful re-serialize.
            var masterOverlays = new List<IDisposable>();
            ISkyrimModGetter[] orderedMasters;
            try
            {
                var resolved = new List<ISkyrimModGetter>();
                bool missing = false;
                foreach (var mr in mod.ModHeader.MasterReferences)
                {
                    var mfn = mr.Master.FileName.String;
                    if (!byName.TryGetValue(mfn, out var mpath)) { missing = true; break; }
                    var ov = SkyrimMod.CreateFromBinaryOverlay(mpath, SkyrimRelease.SkyrimSE);
                    masterOverlays.Add((IDisposable)ov);
                    resolved.Add(ov);
                }
                if (missing)
                {
                    mastersMissing++;
                    notable.Add($"   {name,-48} MASTERS-MISSING  (a declared master is absent from the order — unmeasurable here)");
                    foreach (var d in masterOverlays) d.Dispose();
                    continue;
                }
                orderedMasters = resolved.ToArray();
            }
            catch (Exception ex)
            {
                mastersMissing++;
                notable.Add($"   {name,-48} MASTER-OPEN-FAILED  {ex.GetType().Name}");
                foreach (var d in masterOverlays) d.Dispose();
                continue;
            }

            // Step 2 — the faithful, counter-preserving no-op re-serialize (the §5.1-correct in-place shape).
            string tmpPath = Path.Combine(tmpRoot, name);
            try
            {
                mod.BeginWrite
                    .ToPath(tmpPath)
                    .WithLoadOrder(orderedMasters)
                    .NoNextFormIDProcessing()
                    .Write();
            }
            catch (Exception ex)
            {
                writeFailed++;
                notable.Add($"   {name,-48} WRITE-FAILED  {WriteEngine.Describe(ex)}");
                foreach (var d in masterOverlays) d.Dispose();
                continue;
            }
            finally { foreach (var d in masterOverlays) d.Dispose(); }

            byte[] newBytes;
            try { newBytes = File.ReadAllBytes(tmpPath); }
            catch (Exception ex) { writeFailed++; notable.Add($"   {name,-48} REREAD-FAILED  {ex.GetType().Name}"); continue; }
            finally { try { File.Delete(tmpPath); } catch { } }

            if (origBytes.AsSpan().SequenceEqual(newBytes))
            {
                identical++;
                continue;
            }

            // Divergent — categorize. Walk record headers on both sides (GRUP-recursive).
            var origRecs = WalkRecords(origBytes);
            var newRecs = WalkRecords(newBytes);
            bool seqSame = origRecs.Count == newRecs.Count && origRecs.SequenceEqual(newRecs);
            int firstDiff = FirstDivergence(origBytes, newBytes);
            int tes4End = 24 + (origBytes.Length >= 8 ? (int)BitConverter.ToUInt32(origBytes, 4) : 0);
            int sizeDelta = newBytes.Length - origBytes.Length;
            string counterTag = floorWouldRaise ? $", counter 0x{storedNext:X}→floor 0x{floor:X}" : "";

            if (!seqSame)
            {
                recordsChanged++;
                int dropped = origRecs.Count - newRecs.Count;
                notable.Add($"   {name,-48} RECORDS-CHANGED  recs {origRecs.Count}→{newRecs.Count} ({(dropped > 0 ? $"-{dropped}" : dropped < 0 ? $"+{-dropped}" : "reordered")}), Δsize {sizeDelta:+#;-#;0}b{counterTag}");
            }
            else if (firstDiff >= 0 && firstDiff < tes4End)
            {
                headerOnly++;
                notable.Add($"   {name,-48} HEADER-ONLY      first Δ @0x{firstDiff:X} (TES4 hdr, ends 0x{tes4End:X}), Δsize {sizeDelta:+#;-#;0}b{counterTag}");
            }
            else
            {
                bodyDiverge++;
                notable.Add($"   {name,-48} BODY-DIVERGENT   first Δ @0x{firstDiff:X} (past TES4 hdr 0x{tes4End:X}), Δsize {sizeDelta:+#;-#;0}b{counterTag}");
            }
        }

        Console.WriteLine("── PER-PLUGIN (divergent / unmeasurable only; byte-identical omitted) ──");
        if (notable.Count == 0) Console.WriteLine("   (every sampled plugin round-tripped byte-identical)");
        else foreach (var line in notable) Console.WriteLine(line);
        Console.WriteLine();

        int measured = identical + headerOnly + bodyDiverge + recordsChanged;
        Console.WriteLine("── AGGREGATE ──");
        Console.WriteLine($"   sampled            : {targets.Count}");
        Console.WriteLine($"   BYTE-IDENTICAL     : {identical}");
        Console.WriteLine($"   HEADER-ONLY diverge: {headerOnly}   (master list / ONAM / flags — body records intact)");
        Console.WriteLine($"   BODY-DIVERGENT     : {bodyDiverge}   (compression / subrecord order / OFST / terrain — §5.2 territory)");
        Console.WriteLine($"   RECORDS-CHANGED    : {recordsChanged}   (dropped/added/reordered record — the dangerous class)");
        Console.WriteLine($"   UNLOADABLE         : {unloadable}   (CreateFromBinary threw — in-place REFUSES, never silently re-emits minus a record)");
        Console.WriteLine($"   masters-missing    : {mastersMissing}   (a declared master absent from the order — unmeasurable, not a divergence)");
        Console.WriteLine($"   write-failed       : {writeFailed}");
        Console.WriteLine();
        Console.WriteLine($"   §5.1 NextObjectID  : {counterWouldFloor}/{measured} loadable plugins carry a stored counter the CURRENT product");
        Console.WriteLine($"                        floor (EnsureFormIdFloor) WOULD raise — i.e. a floor-applying re-serialize would");
        Console.WriteLine($"                        diverge on the header counter for these. In-place PRESERVES the counter, so this");
        Console.WriteLine($"                        probe's no-op writes did NOT floor it (the §5.1-correct behavior, measured clean above).");
        Console.WriteLine();
        Console.WriteLine("── READING THIS (fork #1, the round-trip baseline) ──");
        Console.WriteLine("   • BYTE-IDENTICAL high  → a byte-strict floor is viable for those plugins; refuse on any miss (strict-first).");
        Console.WriteLine("   • BODY-DIVERGENT / HEADER-ONLY present → byte-strict would refuse safe plugins en masse; the surface here is");
        Console.WriteLine("     the exact set the two-tier semantic threshold (or a targeted byte-check on terrain/OFST families) must tolerate.");
        Console.WriteLine("   • RECORDS-CHANGED / UNLOADABLE are NON-NEGOTIABLE refusals regardless of threshold (a record vanished or wouldn't parse).");

        try { Directory.Delete(tmpRoot, recursive: true); } catch { }
        return 0;
    }

    /// <summary>Walk an .esp/.esm/.esl and return the (signature, raw on-disk FormID) of every major record
    /// header in document order (GRUP-recursive). Reads only the fixed 24-byte record header, never field data,
    /// so record compression is irrelevant. The structural fingerprint a re-serialize must preserve exactly.</summary>
    static List<(string sig, uint formId)> WalkRecords(byte[] buf)
    {
        var outp = new List<(string, uint)>();
        if (buf.Length < 24) return outp;
        uint tes4Size = BitConverter.ToUInt32(buf, 4);
        Scan(buf, 24 + (int)tes4Size, buf.Length, outp);
        return outp;
    }

    static void Scan(byte[] buf, int start, int end, List<(string, uint)> outp)
    {
        int pos = start;
        while (pos + 24 <= end)
        {
            string sig = System.Text.Encoding.ASCII.GetString(buf, pos, 4);
            uint size = BitConverter.ToUInt32(buf, pos + 4);
            long next;
            if (sig == "GRUP")
            {
                next = (long)pos + size;                       // GRUP size INCLUDES its 24-byte header
                Scan(buf, pos + 24, (int)Math.Min(next, end), outp);
            }
            else
            {
                outp.Add((sig, BitConverter.ToUInt32(buf, pos + 12)));
                next = (long)pos + 24 + size;                  // major record: 24-byte header + dataSize
            }
            if (next <= pos) break;                            // forward-progress guard (malformed size)
            pos = (int)Math.Min(next, (long)end);
        }
    }

    /// <summary>Index of the first differing byte between two buffers, or -1 if one is a strict prefix of the
    /// other (length-only divergence). Length differences alone still register via the size-delta report.</summary>
    static int FirstDivergence(byte[] a, byte[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++) if (a[i] != b[i]) return i;
        return a.Length == b.Length ? -1 : n;
    }
}
