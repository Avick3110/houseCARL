using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// EXPLORATORY probe for HCBR-2026-06-15-01 item 5.1 (ESL / FE-space FormID handling). houseCARL has ZERO
/// ESL-specific code — it delegates 100% to Mutagen by construction. This probe PINS the referenced Mutagen version's
/// semantics so EslOverrideFormIdTests (and any §4 surface-to-Aaron call) is built on measured reality, not the
/// scoping doc's claims (which it deliberately re-tests — e.g. "object id 0x123 ok / 0x800 throws").
///
/// FINDINGS (measured here + cross-checked by esl-real-scan over 1,399 real ESL plugins / 1.55M record headers),
/// which OVERTURN the scoping doc on two points and confirm correct-by-construction (§4-(a), no engine change):
///   • Legal ESL object-ID window is 0x800–0xFFF (the scoping's "use 0x123, not 0x800" is BACKWARDS): &lt;0x800 throws
///     LowerFormKeyRangeDisallowed (a general floor), &gt;0xFFF throws FormIDCompactionOutOfBounds (the ESL ceiling).
///   • There is NO on-disk FE-space. A reference to a light master is stored by the master's MASTER-LIST INDEX
///     (high byte 0x00 for the first master), identical to a full master; 0xFE never appears in the on-disk bytes
///     (0 of 1.55M real records). 0xFE is the RUNTIME address the engine computes from the master's header flag.
///     ⇒ the scoping's "assert the on-disk byte is FE-space" is unsound (it could never pass). The flag that tracks
///     light-ness is the master's OWN IsSmallMaster header bit; the FormKey is index-independent (no cross-instance
///     carry). Mutagen round-trips it all correctly. The guard below pins THESE invariants.
///
/// The arms below are kept as REPRODUCIBLE DOCUMENTATION of how the FINDINGS above were reached — they print the
/// measured reality (the questions they pose were answered as stated above); EslOverrideFormIdTests (src/housecarl-mcp-tests) pins it.
/// All self-contained (synthesized plugins in TEMP; NO Skyrim.esm, NO MO2 instance):
///
///   A  small-master object-ID range — synthesize a light master (IsSmallMaster=true) carrying ONE record at a
///      series of object IDs {0x000, 0x123, 0x7FF, 0x800, 0xFFF, 0x1000} and write it with the DEFAULT params our
///      WritePatchStaged uses; report which throw (pins the real FormIDCompaction range, resolving the 0x123-vs-0x800
///      question), plus CanBeSmallMaster and the post-reopen IsSmallMaster.
///   B  encode — override a light-master record from a NORMAL patch through the product write incantation
///      (WithLoadOrder + NoNextFormIDProcessing) and read the raw on-disk FormID of the override (hand-parsed):
///      is the top byte 0xFE (FE-space)?
///   C  decode/round-trip — reopen the patch (i) STANDALONE overlay and (ii) with the master beside it / via the
///      product LoadOrderResolver, and report the resolved FormKey: does it round-trip to (lightMaster, objId)?
///   D  negative control — same override but with the master's IsSmallMaster CLEARED: top byte is the plain master
///      index (NOT 0xFE) and the FormKey decodes to the full 24-bit ID. Proves placement TRACKS the flag.
///   E  cross-instance / second-ordering — write the patch with the light master at light-index 0, then force a
///      second light master ahead of it (light-index 1); the on-disk index portion changes but the resolved FormKey
///      is STABLE (the report's "cross-instance index carry" cannot happen — houseCARL stores no index byte).
///
/// Run: dotnet run --project src/housecarl-generator esl-formid-probe
/// </summary>
public static class EslFormIdHarness
{
    public static int RunProbe(string[] args)
    {
        Console.WriteLine("================================================================");
        Console.WriteLine(" houseCARL esl-formid-probe — ESL / FE-space semantics (HCBR-2026-06-15-01 item 5.1)");
        Console.WriteLine("================================================================");

        var tmpDir = Path.Combine(Path.GetTempPath(), "hc-esl-formid-probe");
        if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true);
        Directory.CreateDirectory(tmpDir);

        // ---- A: small-master object-ID range under the DEFAULT write params ----
        Console.WriteLine("A small-master object-ID range (write a light master carrying ONE weapon at each id):");
        foreach (uint objId in new uint[] { 0x000, 0x123, 0x7FF, 0x800, 0xFFF, 0x1000 })
        {
            var lKey = new ModKey($"HcEslA_{objId:X3}", ModType.Master);
            string lPath = Path.Combine(tmpDir, "A", lKey.FileName.String);
            Directory.CreateDirectory(Path.GetDirectoryName(lPath)!);
            var fk = new FormKey(lKey, objId);
            bool wrote = Try(() =>
            {
                var L = new SkyrimMod(lKey, SkyrimRelease.SkyrimSE) { IsSmallMaster = true };
                var w = new Weapon(fk, SkyrimRelease.SkyrimSE) { EditorID = $"HcEslAWeap_{objId:X3}" };
                L.Weapons.Add(w);
                L.BeginWrite.ToPath(lPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).NoNextFormIDProcessing().Write();
            }, out var err);
            string reopen = "";
            if (wrote)
            {
                ISkyrimModGetter? ov = null;
                try { ov = SkyrimMod.CreateFromBinaryOverlay(lPath, SkyrimRelease.SkyrimSE); reopen = $"reopened IsSmallMaster={ov.IsSmallMaster}, CanBeSmallMaster={ov.CanBeSmallMaster}, rec FormKey={ov.Weapons.First().FormKey}"; }
                catch (Exception ex) { reopen = $"reopen threw: {ex.GetType().Name}"; }
                finally { (ov as IDisposable)?.Dispose(); }
            }
            Console.WriteLine($"   objId 0x{objId:X3}: {(wrote ? $"WROTE — {reopen}" : $"THREW — {err}")}");
        }
        Console.WriteLine();

        // ---- Setup for B–E: a light master with a record at a known WORKING object id. ----
        // (Use 0x800 if A says it's legal, else fall back to 0x123 — pinned empirically above; we pick a value here
        //  and the printed A-table tells us if it was the right choice.)
        uint recId = 0x800;
        var mKey = new ModKey("HcEslLight", ModType.Master);
        string mPath = Path.Combine(tmpDir, mKey.FileName.String);
        var recFk = new FormKey(mKey, recId);
        bool setupWrote = Try(() =>
        {
            var L = new SkyrimMod(mKey, SkyrimRelease.SkyrimSE) { IsSmallMaster = true };
            var w = new Weapon(recFk, SkyrimRelease.SkyrimSE) { EditorID = "HcEslLightWeap", BasicStats = new WeaponBasicStats { Damage = 10 } };
            L.Weapons.Add(w);
            L.BeginWrite.ToPath(mPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).NoNextFormIDProcessing().Write();
        }, out var setupErr);
        Console.WriteLine($"-- setup light master {mKey.FileName} with weapon at 0x{recId:X3}: {(setupWrote ? "ok" : $"FAILED — {setupErr} (B–E may be unmeasurable; check the A-table for a legal id)")} --");
        Console.WriteLine();

        if (setupWrote)
        {
            // ---- B: encode — override from a NORMAL patch via the product incantation; read raw on-disk FormID. ----
            Console.WriteLine("B encode — override the light-master record from a normal patch:");
            var pKey = new ModKey("HcEslPatch", ModType.Plugin);
            string pPath = Path.Combine(tmpDir, "B", pKey.FileName.String);
            Directory.CreateDirectory(Path.GetDirectoryName(pPath)!);
            {
                using var lOvH = SkyrimMod.CreateFromBinaryOverlay(mPath, SkyrimRelease.SkyrimSE) as IDisposable;
                var lOv = (ISkyrimModGetter)lOvH!;
                var p = new SkyrimMod(pKey, SkyrimRelease.SkyrimSE);
                var ov = p.Weapons.GetOrAddAsOverride(lOv.Weapons.First(x => x.FormKey == recFk));
                ov.BasicStats!.Damage = 20;
                if (p.ModHeader.Stats.NextFormID < 0x800) p.ModHeader.Stats.NextFormID = 0x800;
                p.BeginWrite.ToPath(pPath).WithLoadOrder(new[] { lOv }).NoNextFormIDProcessing().Write();
                var (sig, raw) = FirstRecordFormId(pPath, "WEAP");
                Console.WriteLine($"   on-disk override FormID = 0x{raw:X8}  (top byte 0x{(raw >> 24) & 0xFF:X2}; FE-space = {((raw >> 24) & 0xFF) == 0xFE})  [sig {sig}]");

                // ---- C: decode/round-trip — standalone overlay vs master-beside / product resolver. ----
                Console.WriteLine("C decode/round-trip:");
                ISkyrimModGetter? pOvStandalone = null;
                try { pOvStandalone = SkyrimMod.CreateFromBinaryOverlay(pPath, SkyrimRelease.SkyrimSE); Console.WriteLine($"   STANDALONE overlay  → FormKey {pOvStandalone.Weapons.First().FormKey}  (expect {recFk})"); }
                catch (Exception ex) { Console.WriteLine($"   STANDALONE overlay  → threw {ex.GetType().Name}: {ex.Message}"); }
                finally { (pOvStandalone as IDisposable)?.Dispose(); }

                try
                {
                    using var r = LoadOrderResolver.Build(new[] { mPath, pPath });
                    var wi = r.ResolveWinner(recFk);
                    Console.WriteLine($"   product ResolveWinner([L,P], {recFk}) → winner={wi?.WinnerPlugin ?? "<null>"}, depth={wi?.OverrideDepth} (expect winner=HcEslPatch.esp, depth=1 — the FE override was decoded + recognized)");
                }
                catch (Exception ex) { Console.WriteLine($"   product resolver threw {ex.GetType().Name}: {ex.Message}"); }
            }
            Console.WriteLine();

            // ---- D: negative control — master NOT light. ----
            Console.WriteLine("D negative control — same override, master IsSmallMaster CLEARED:");
            var nKey = new ModKey("HcEslFull", ModType.Master);
            string nPath = Path.Combine(tmpDir, "D", nKey.FileName.String);
            Directory.CreateDirectory(Path.GetDirectoryName(nPath)!);
            var nFk = new FormKey(nKey, recId);
            bool nSetup = Try(() =>
            {
                var N = new SkyrimMod(nKey, SkyrimRelease.SkyrimSE) { IsSmallMaster = false };
                var w = new Weapon(nFk, SkyrimRelease.SkyrimSE) { EditorID = "HcEslFullWeap", BasicStats = new WeaponBasicStats { Damage = 10 } };
                N.Weapons.Add(w);
                N.BeginWrite.ToPath(nPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).NoNextFormIDProcessing().Write();
            }, out var nErr);
            if (!nSetup) Console.WriteLine($"   setup of full master threw — {nErr}");
            else
            {
                string pdPath = Path.Combine(tmpDir, "D", "HcEslPatchD.esp");
                using var nOvH = SkyrimMod.CreateFromBinaryOverlay(nPath, SkyrimRelease.SkyrimSE) as IDisposable;
                var nOv = (ISkyrimModGetter)nOvH!;
                var p = new SkyrimMod(new ModKey("HcEslPatchD", ModType.Plugin), SkyrimRelease.SkyrimSE);
                var ov = p.Weapons.GetOrAddAsOverride(nOv.Weapons.First(x => x.FormKey == nFk));
                ov.BasicStats!.Damage = 20;
                if (p.ModHeader.Stats.NextFormID < 0x800) p.ModHeader.Stats.NextFormID = 0x800;
                p.BeginWrite.ToPath(pdPath).WithLoadOrder(new[] { nOv }).NoNextFormIDProcessing().Write();
                var (_, raw) = FirstRecordFormId(pdPath, "WEAP");
                Console.WriteLine($"   on-disk override FormID = 0x{raw:X8}  (top byte 0x{(raw >> 24) & 0xFF:X2}; FE-space = {((raw >> 24) & 0xFF) == 0xFE} — expect NOT FE)");
                ISkyrimModGetter? pdOv = null;
                try { pdOv = SkyrimMod.CreateFromBinaryOverlay(pdPath, SkyrimRelease.SkyrimSE); Console.WriteLine($"   STANDALONE overlay → FormKey {pdOv.Weapons.First().FormKey} (expect {nFk})"); }
                finally { (pdOv as IDisposable)?.Dispose(); }
                using var rd = LoadOrderResolver.Build(new[] { nPath, pdPath });
                var wid = rd.ResolveWinner(nFk);
                Console.WriteLine($"   product ResolveWinner({nFk}) → winner={wid?.WinnerPlugin ?? "<null>"}, depth={wid?.OverrideDepth} (expect winner=HcEslPatchD.esp, depth=1)");
            }
            Console.WriteLine();

            // ---- E: cross-instance / second-ordering — light-index shifts, FormKey stable. ----
            Console.WriteLine("E cross-instance — write with the light master at light-index 0, then behind another light master:");
            {
                // second light master, also referenced by the patch (so it lands in the master list ahead/behind L)
                var l2Key = new ModKey("HcEslLight2", ModType.Master);
                string l2Path = Path.Combine(tmpDir, "E", l2Key.FileName.String);
                Directory.CreateDirectory(Path.GetDirectoryName(l2Path)!);
                var rec2Fk = new FormKey(l2Key, recId);
                bool l2ok = Try(() =>
                {
                    var L2 = new SkyrimMod(l2Key, SkyrimRelease.SkyrimSE) { IsSmallMaster = true };
                    var w = new Weapon(rec2Fk, SkyrimRelease.SkyrimSE) { EditorID = "HcEslLight2Weap", BasicStats = new WeaponBasicStats { Damage = 5 } };
                    L2.Weapons.Add(w);
                    L2.BeginWrite.ToPath(l2Path).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).NoNextFormIDProcessing().Write();
                }, out var l2err);
                if (!l2ok) Console.WriteLine($"   second light master setup threw — {l2err}");
                else
                {
                    foreach (var (label, order) in new[] { ("L first  (L light-idx 0)", new[] { mPath, l2Path }), ("L second (L light-idx 1)", new[] { l2Path, mPath }) })
                    {
                        string pePath = Path.Combine(tmpDir, "E", $"HcEslPatchE_{label[0]}_{order[0].GetHashCode():X4}.esp");
                        using var lAH = SkyrimMod.CreateFromBinaryOverlay(order[0], SkyrimRelease.SkyrimSE) as IDisposable;
                        using var lBH = SkyrimMod.CreateFromBinaryOverlay(order[1], SkyrimRelease.SkyrimSE) as IDisposable;
                        var lA = (ISkyrimModGetter)lAH!;
                        var lB = (ISkyrimModGetter)lBH!;
                        var p = new SkyrimMod(new ModKey(Path.GetFileNameWithoutExtension(pePath), ModType.Plugin), SkyrimRelease.SkyrimSE);
                        // reference BOTH light masters so both land in P's master list, in load order
                        p.Weapons.GetOrAddAsOverride(lA.Weapons.First()).BasicStats!.Damage = 21;
                        p.Weapons.GetOrAddAsOverride(lB.Weapons.First()).BasicStats!.Damage = 22;
                        if (p.ModHeader.Stats.NextFormID < 0x800) p.ModHeader.Stats.NextFormID = 0x800;
                        p.BeginWrite.ToPath(pePath).WithLoadOrder(new[] { lA, lB }).NoNextFormIDProcessing().Write();
                        var ids = AllRecordFormIds(pePath, "WEAP");
                        using var rr = LoadOrderResolver.Build(new[] { order[0], order[1], pePath });
                        var wi = rr.ResolveWinner(recFk);
                        Console.WriteLine($"   {label}: on-disk WEAP FormIDs [{string.Join(", ", ids.Select(x => $"0x{x:X8}"))}]; ResolveWinner(L-rec {recFk}) → winner={wi?.WinnerPlugin ?? "<null>"}, depth={wi?.OverrideDepth}");
                    }
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine("(A pins the legal range; B the FE encode; C the decode round-trip; D that it tracks the flag; E that the FormKey is index-independent)");
        try { Directory.Delete(tmpDir, recursive: true); } catch { }
        return 0;
    }

    /// <summary>GROUND-TRUTH scan over REAL CK/xEdit-authored plugins: for each plugin report whether it is itself
    /// light (TES4 header flag 0x200), and the high-byte distribution of its record-header FormIDs — counting how
    /// many records carry high byte 0xFE (the FE light space). Pure byte parsing (NO Mutagen master resolution), so
    /// it runs against a single plugin file with its masters absent. Settles whether SSE stores light-master
    /// references in FE-space ON DISK (then real overrides of ESL records show 0xFE) or by master-list index
    /// (then 0xFE never appears on disk and is purely a runtime/xEdit-display address).
    /// Run: dotnet run --project src/housecarl-generator esl-real-scan [dir] [maxPlugins]</summary>
    public static int RunRealScan(string[] args)
    {
        if (args.Length < 1) { Console.Error.WriteLine("usage: esl-real-scan <dir> [maxPlugins]"); return 2; }
        string dir = args[0];
        int max = args.Length > 1 && int.TryParse(args[1], out var m) ? m : 400;
        var files = Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories)
            .Where(f => { var e = Path.GetExtension(f).ToLowerInvariant(); return e == ".esp" || e == ".esm" || e == ".esl"; })
            .Take(max).ToList();
        Console.WriteLine($"esl-real-scan: {files.Count} plugins under {dir}");
        int lightPlugins = 0, scanned = 0;
        long totalRecords = 0;
        var globalHi = new Dictionary<int, long>();
        var feExamples = new List<string>();
        foreach (var f in files)
        {
            byte[] buf;
            try { buf = File.ReadAllBytes(f); } catch { continue; }
            if (buf.Length < 24) continue;
            bool selfLight = (BitConverter.ToUInt32(buf, 8) & 0x200u) != 0;
            if (selfLight) lightPlugins++;
            List<(string sig, uint formId)> recs;
            try { recs = WalkRecords(buf); } catch { continue; }
            scanned++;
            totalRecords += recs.Count;
            int feHere = 0;
            foreach (var (sig, fid) in recs)
            {
                int hi = (int)((fid >> 24) & 0xFF);
                globalHi[hi] = globalHi.GetValueOrDefault(hi) + 1;
                if (hi == 0xFE) feHere++;
            }
            if (feHere > 0 && feExamples.Count < 12)
                feExamples.Add($"   {Path.GetFileName(f)}{(selfLight ? " [self-light]" : "")}: {feHere} record(s) with high byte 0xFE");
        }
        Console.WriteLine($"scanned {scanned} plugins ({lightPlugins} self-light), {totalRecords} record headers");
        Console.WriteLine("record-header FormID high-byte distribution (top 12):");
        foreach (var kv in globalHi.OrderByDescending(k => k.Value).Take(12))
            Console.WriteLine($"   0x{kv.Key:X2} : {kv.Value}");
        long feCount = globalHi.GetValueOrDefault(0xFE);
        Console.WriteLine($"\n>>> records with high byte 0xFE (FE light space, ON DISK): {feCount}");
        if (feCount > 0) { Console.WriteLine("    ⇒ real plugins DO store FE-space references on disk:"); feExamples.ForEach(Console.WriteLine); }
        else Console.WriteLine("    ⇒ NO on-disk FE-space references found — light-master refs use the master-list index on disk; 0xFE is a runtime/display address only.");
        return 0;
    }

    /// <summary>Walk an .esp/.esm and return the (signature, raw on-disk FormID) of the FIRST major record whose
    /// signature matches. We only ever read the fixed 24-byte record header (sig, dataSize, formID) — never field
    /// data — so record compression is irrelevant. Recurses into GRUPs.</summary>
    static (string sig, uint formId) FirstRecordFormId(string path, string targetSig)
    {
        var all = WalkRecords(File.ReadAllBytes(path));
        foreach (var (sig, fid) in all) if (sig == targetSig) return (sig, fid);
        return ("<none>", 0);
    }

    static List<uint> AllRecordFormIds(string path, string targetSig)
        => WalkRecords(File.ReadAllBytes(path)).Where(x => x.sig == targetSig).Select(x => x.formId).ToList();

    static List<(string sig, uint formId)> WalkRecords(byte[] buf)
    {
        var outp = new List<(string, uint)>();
        // Skip the TES4 header record: sig(4) dataSize(4) at +4; record header is 24 bytes.
        if (buf.Length < 24) return outp;
        uint tes4Size = BitConverter.ToUInt32(buf, 4);
        int start = 24 + (int)tes4Size;
        Scan(buf, start, buf.Length, outp);
        return outp;
    }

    static void Scan(byte[] buf, int start, int end, List<(string, uint)> outp)
    {
        int p = start;
        while (p + 24 <= end)
        {
            string sig = System.Text.Encoding.ASCII.GetString(buf, p, 4);
            uint size = BitConverter.ToUInt32(buf, p + 4);
            // `next` in LONG arithmetic so a malformed huge size can't wrap a 32-bit add into a backwards jump.
            long next;
            if (sig == "GRUP")
            {
                // GRUP size INCLUDES the 24-byte header; contents are [p+24, p+size), sibling at p+size.
                next = (long)p + size;
                Scan(buf, p + 24, (int)Math.Min(next, end), outp);
            }
            else
            {
                outp.Add((sig, BitConverter.ToUInt32(buf, p + 12)));
                next = (long)p + 24 + size; // major record: 24-byte header + dataSize bytes of fields
            }
            // Forward-progress guard (Q3 — the manual esl-real-scan is pointed at real, possibly-malformed plugins):
            // a GRUP whose size is 0/< its own header would not advance p. Break instead of spinning.
            if (next <= p) break;
            p = (int)Math.Min(next, (long)end);
        }
    }

    static bool Try(Action op, out string err)
    {
        try { op(); err = "ok"; return true; }
        catch (Exception ex) { err = $"{ex.GetType().Name}: {ex.Message.Replace("\r", " ").Replace("\n", " ").Trim()}"; return false; }
    }
}
