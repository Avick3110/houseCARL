using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// EXPLORATORY probe for HCBR-2026-06-15-01 item 5.1 (ESL / FE-space FormID handling). houseCARL has ZERO
/// ESL-specific code — it delegates 100% to Mutagen by construction. This probe PINS the referenced Mutagen version's
/// semantics so the regression guard (and any §4 surface-to-Aaron call) is built on measured reality, not the
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
/// measured reality (the questions they pose were answered as stated above); the regression guard (RunGuard) pins it.
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
public static class EslFormIdProbe
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

    /// <summary>
    /// SELF-CONTAINED CI REGRESSION GUARD for ESL / FE-space FormID handling (HCBR-2026-06-15-01 item 5.1), in the
    /// pattern of formid-floor-guard. houseCARL has ZERO ESL-specific code — it delegates 100% to Mutagen by
    /// construction (cornerstone §3) — so this is a REGRESSION GUARD that pins the REAL, correct behavior, NOT a
    /// mutation-RED fix probe (there is no houseCARL fix to revert; the finding is correct-by-construction).
    ///
    /// The behavior it pins was settled empirically (esl-formid-probe + esl-real-scan over 1,399 real ESL plugins /
    /// 1.55M record headers): SSE stores a reference to a light master's record ON DISK by the master's MASTER-LIST
    /// INDEX (e.g. high byte 0x00 for the first master), NOT in FE-space — `0xFE` is the RUNTIME address the engine
    /// computes from the master's own header flag, and never appears in the on-disk bytes (0 of 1.55M real records).
    /// The scoping doc's "assert the on-disk byte is FE-space" was based on a wrong premise; this guard asserts the
    /// real invariant (master-index encode, never 0xFE) so a future Mutagen bump or write-path change that breaks ESL
    /// patching goes RED.
    ///
    /// Drives the REAL product write path (WritePatchBuilder.Apply → the single WriteEngine.WritePatch chokepoint),
    /// then the REAL product read path (LoadOrderResolver). Self-contained (synthesizes its masters in TEMP; generates
    /// the validator corpus in-process; NO Skyrim.esm, NO MO2). Run: dotnet run --project src/housecarl-generator esl-formid-guard
    ///
    /// Arms (ALL required — a GREEN must mean "ESL patching round-trips correctly", never "the scenario didn't arise"):
    ///   RANGE     — a light master writes a record at 0x800 and 0xFFF, and REFUSES (named throw) at 0x123 (the GENERAL
    ///               lower-range floor, LowerFormKeyRangeDisallowed) and 0x1000 (the ESL-SPECIFIC compaction ceiling,
    ///               FormIDCompactionOutOfBounds — only fires when IsSmallMaster=true). Pins the legal ESL object-ID
    ///               window so houseCARL never silently writes an out-of-window ESL record.
    ///   CONTROL   — in-CI RED-sensitivity: a FULL master writes a record at 0x1000 fine (no compaction ceiling),
    ///               where the light master REFUSED it — proving RANGE's 0x1000 refusal is CONTINGENT on the light
    ///               flag, not unconditional. (The manual sabotage — forcing the flag off flips RANGE+LIGHT+FLAG red —
    ///               is documented in the PR; this bakes the contingency into every run, like formid-floor-guard's CONTROL.)
    ///   LIGHT     — override a light-master record via Apply; the patch round-trips, anchored to the MASTER's OWN
    ///               on-disk record key (read independently, not a constructed key): raw overlay decode == that key,
    ///               the product resolver names the patch the winner at depth>=1 (the override was decoded + recognized),
    ///               the on-disk override FormID is master-index-encoded (high byte 0x00, NOT 0xFE), master reopens IsSmallMaster=true.
    ///   FLAG      — NEGATIVE CONTROL: the same override of a FULL (IsSmallMaster=false) master ALSO round-trips and
    ///               ALSO encodes on-disk high byte 0x00 — proving the on-disk byte does NOT discriminate light from
    ///               full; the discriminator is the master's own header flag (light reopens true, full reopens false).
    ///   INDEX     — the report's "cross-instance index carry": write a patch against the light master in TWO master
    ///               orderings; the decoded FormKey VALUES are identical across orderings (not just the winner name)
    ///               while only the on-disk master index moves, and NO on-disk record carries high byte 0xFE — houseCARL
    ///               stores no absolute index, so it can't mangle.
    /// </summary>
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("################  REGRESSION GUARD — ESL / FE-space FormID handling (HCBR-2026-06-15-01 item 5.1)  ################");
        Console.WriteLine();

        var tmpDir = Path.Combine(Path.GetTempPath(), "hc-esl-formid-guard");
        if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true);
        Directory.CreateDirectory(tmpDir);

        var genDir = Path.Combine(tmpDir, "corpus-gen");
        CorpusGenerator.GenerateAll(genDir, Path.Combine(tmpDir, "corpus-ref"));
        var rulebook = CorpusRulebook.Load(Path.Combine(genDir, "corpus.json"));
        Console.WriteLine("-- setup: corpus generated --");

        // --- RANGE: pin the legal ESL object-ID window 0x800–0xFFF. Two distinct mechanisms bound it (the RED-proof
        //     teased them apart): the 0x800 FLOOR is the GENERAL lower-range guard (LowerFormKeyRangeDisallowed fires on
        //     any originating sub-0x800 record, ESL or not); the 0xFFF CEILING is the ESL-SPECIFIC FormIDCompaction limit
        //     (FormIDCompactionOutOfBounds, only when IsSmallMaster=true). Both must hold for houseCARL to never silently
        //     write an out-of-window ESL record.
        //     NOTE (scope): RANGE + CONTROL pin an UPSTREAM capability — writing ORIGINATING records into a light master —
        //     that houseCARL does not exercise today (it writes patch plugins and does not ESL-flag its own output). The
        //     LIVE ESL path is the LIGHT + INDEX arms (overriding a light-master record via Apply). RANGE/CONTROL are kept
        //     anyway so the window is regression-pinned the day houseCARL (or a future tool path) does originate ESL records. ---
        bool rangeOk;
        {
            // Match on the exception TYPE name (not a message substring) — robust to an upstream message rewording.
            bool w800 = TryWriteLightAt(tmpDir, "r800", 0x800, light: true, out _);
            bool wFFF = TryWriteLightAt(tmpDir, "rFFF", 0xFFF, light: true, out _);
            bool t123 = !TryWriteLightAt(tmpDir, "r123", 0x123, light: true, out var e123) && e123 == "LowerFormKeyRangeDisallowedException";
            bool t1000 = !TryWriteLightAt(tmpDir, "r1000", 0x1000, light: true, out var e1000) && e1000 == "FormIDCompactionOutOfBoundsException";
            rangeOk = w800 && wFFF && t123 && t1000;
            Console.WriteLine($"   RANGE 0x800–0xFFF write / <0x800 + >0xFFF refuse : {(rangeOk ? "PASS" : $"FAIL — 0x800 wrote={w800}, 0xFFF wrote={wFFF}, 0x123 refused-typed={t123}, 0x1000 refused-typed={t1000}")}");
        }

        // --- CONTROL (in-CI RED-sensitivity): the ESL ceiling is CONTINGENT on the light flag — a FULL master writes
        //     a record at 0x1000 fine (no compaction ceiling), where a light master REFUSED it above. This bakes the
        //     contingency into every CI run: a write path that stopped engaging IsSmallMaster would no longer refuse
        //     0x1000 as a light master, and the manual sabotage proved exactly that (forcing the flag off flips
        //     RANGE+LIGHT+FLAG red — see the PR/doc). Mirrors formid-floor-guard's CONTROL arm (proving the constraint
        //     is real, not vacuous). ---
        bool controlOk;
        {
            bool fullWritesHigh = TryWriteLightAt(tmpDir, "ctl1000", 0x1000, light: false, out var cerr);
            controlOk = fullWritesHigh;
            Console.WriteLine($"   CONTROL ESL ceiling is flag-contingent           : {(controlOk ? "PASS — full master writes 0x1000 (no ESL ceiling); RANGE's refusal is light-specific" : $"FAIL — full master refused 0x1000: {cerr}")}");
        }

        // Shared: a light master with a weapon at 0x800 (a legal compacted id), the record we override below.
        uint recId = 0x800;
        var lKey = new ModKey("HcEslGuardLight", ModType.Master);
        string lPath = Path.Combine(tmpDir, lKey.FileName.String);
        var recFk = new FormKey(lKey, recId);
        WriteLightMaster(lKey, lPath, recFk, light: true);

        // --- LIGHT: override the light-master record via the REAL Apply path; assert the full round-trip. ---
        bool lightOk;
        {
            string pPath = Path.Combine(tmpDir, "HcEslGuardLightPatch.esp");
            bool applyOk; using (var r = LoadOrderResolver.Build(new[] { lPath }))
            {
                var edit = new WritePatchBuilder.PatchEdit { Target = recFk, Path = new[] { "BasicStats", "Damage" }, Verb = "Set", Value = "20" };
                applyOk = WritePatchBuilder.Apply(r, rulebook, new[] { edit }, pPath, extend: false).Success;
            }
            bool flagSurvives = ReopenIsSmallMaster(lPath) == true;
            // Anchor the round-trip to the MASTER's OWN on-disk record key (read independently from lPath), not to a
            // key we constructed — so a systematic FE-decode corruption can't slip through a self-referential compare.
            FormKey srcFk = RawFirstWeapFormKey(lPath);
            FormKey directDecode = RawFirstWeapFormKey(pPath);
            (string? winner, int depth) = ResolveWinner(new[] { lPath, pPath }, srcFk);
            uint raw = FirstRecordFormId(pPath, "WEAP").formId;
            int hi = (int)((raw >> 24) & 0xFF);
            bool overrideRecognized = applyOk && winner != null && winner.Equals("HcEslGuardLightPatch.esp", StringComparison.OrdinalIgnoreCase) && depth >= 1;
            bool decodeOk = directDecode == srcFk && srcFk == recFk; // patch decode == master's real stored record == expected
            bool masterIndexEncode = hi == 0x00; // L is the only/first master → index 0 (NOT 0xFE)
            lightOk = applyOk && flagSurvives && decodeOk && overrideRecognized && masterIndexEncode;
            Console.WriteLine($"   LIGHT override round-trips                       : {(lightOk ? $"PASS — overlay decode {directDecode} == master record {srcFk}, winner={winner}@depth{depth}, on-disk 0x{raw:X8} (high 0x{hi:X2}), master IsSmallMaster={flagSurvives}" : $"FAIL — apply={applyOk}, decode={decodeOk}(patch {directDecode} vs master {srcFk}), winner={winner}@depth{depth}, on-disk 0x{raw:X8} high 0x{hi:X2}, flagSurvives={flagSurvives}")}");
        }

        // --- FLAG (negative control): the SAME override of a FULL master round-trips + encodes identically on disk;
        //     the discriminator is the master's own flag (light→true, full→false), NOT the on-disk byte. ---
        bool flagOk;
        {
            var nKey = new ModKey("HcEslGuardFull", ModType.Master);
            string nPath = Path.Combine(tmpDir, nKey.FileName.String);
            var nFk = new FormKey(nKey, recId);
            WriteLightMaster(nKey, nPath, nFk, light: false);
            string pPath = Path.Combine(tmpDir, "HcEslGuardFullPatch.esp");
            bool applyOk; using (var r = LoadOrderResolver.Build(new[] { nPath }))
            {
                var edit = new WritePatchBuilder.PatchEdit { Target = nFk, Path = new[] { "BasicStats", "Damage" }, Verb = "Set", Value = "20" };
                applyOk = WritePatchBuilder.Apply(r, rulebook, new[] { edit }, pPath, extend: false).Success;
            }
            bool? fullRaw = ReopenIsSmallMaster(nPath);   // the negative control's own header flag (expect False)
            bool? lightRaw = ReopenIsSmallMaster(lPath);  // the light master's, side-by-side (expect True)
            bool discriminates = lightRaw == true && fullRaw == false; // the master header is what distinguishes them
            (string? winner, int depth) = ResolveWinner(new[] { nPath, pPath }, nFk);
            uint raw = FirstRecordFormId(pPath, "WEAP").formId;
            int hi = (int)((raw >> 24) & 0xFF);
            bool roundTrips = applyOk && RawFirstWeapFormKey(pPath) == nFk && winner != null && winner.Equals("HcEslGuardFullPatch.esp", StringComparison.OrdinalIgnoreCase) && depth >= 1;
            bool sameEncode = hi == 0x00; // full master override ALSO high byte 0x00 — on-disk byte does not discriminate
            flagOk = roundTrips && sameEncode && discriminates;
            Console.WriteLine($"   FLAG tracks the master header, not the byte      : {(flagOk ? $"PASS — full round-trips, on-disk high 0x{hi:X2} (== light's), raw IsSmallMaster light={lightRaw}/full={fullRaw}" : $"FAIL — roundTrips={roundTrips}, on-disk high 0x{hi:X2} sameEncode={sameEncode}, raw IsSmallMaster light={lightRaw}/full={fullRaw}")}");
        }

        // --- INDEX (cross-instance): two orderings of the same light master → on-disk index moves, FormKey stable,
        //     never 0xFE. Proves no "cross-instance index carry" (houseCARL stores no absolute index). ---
        bool indexOk;
        {
            var l2Key = new ModKey("HcEslGuardLight2", ModType.Master);
            string l2Path = Path.Combine(tmpDir, l2Key.FileName.String);
            var rec2Fk = new FormKey(l2Key, recId);
            WriteLightMaster(l2Key, l2Path, rec2Fk, light: true);

            // The two master records, keyed by filename+objId (the index-INDEPENDENT identity we expect on both reads).
            var expectedKeys = new[] { recFk, rec2Fk }.OrderBy(k => k.ToString()).ToList();
            var results = new List<(string label, bool stable, bool noFe, bool valueStable)>();
            foreach (var (label, order) in new[] { ("L-first", new[] { lPath, l2Path }), ("L-second", new[] { l2Path, lPath }) })
            {
                string pePath = Path.Combine(tmpDir, $"HcEslGuardIdx_{label}.esp");
                bool applyOk; using (var r = LoadOrderResolver.Build(order))
                {
                    var edits = new[]
                    {
                        new WritePatchBuilder.PatchEdit { Target = recFk,  Path = new[] { "BasicStats", "Damage" }, Verb = "Set", Value = "21" },
                        new WritePatchBuilder.PatchEdit { Target = rec2Fk, Path = new[] { "BasicStats", "Damage" }, Verb = "Set", Value = "22" },
                    };
                    applyOk = WritePatchBuilder.Apply(r, rulebook, edits, pePath, extend: false).Success;
                }
                (string? winner, int depth) = ResolveWinner(order.Append(pePath).ToArray(), recFk);
                var ids = AllRecordFormIds(pePath, "WEAP");
                // VALUE stability (not just winner-name): the on-disk references DECODE to the same two FormKeys
                // regardless of write-time master ordering — the proof there is no cross-instance index carry.
                var decoded = RawAllWeapFormKeys(pePath).OrderBy(k => k.ToString()).ToList();
                bool valueStable = decoded.SequenceEqual(expectedKeys);
                bool stable = applyOk && winner != null && winner.Equals(Path.GetFileName(pePath), StringComparison.OrdinalIgnoreCase) && depth >= 1;
                bool noFe = ids.All(x => ((x >> 24) & 0xFF) != 0xFE);
                results.Add((label, stable, noFe, valueStable));
                Console.WriteLine($"      {label}: winner={winner}@depth{depth}, on-disk WEAP=[{string.Join(",", ids.Select(x => $"0x{x:X8}"))}], decoded keys=[{string.Join(",", decoded)}]");
            }
            indexOk = results.All(x => x.stable && x.noFe && x.valueStable);
            Console.WriteLine($"   INDEX FormKey stable across orderings, no 0xFE  : {(indexOk ? "PASS — decoded FormKeys identical both orderings, on-disk indices master-list-based" : $"FAIL — stable/noFe/valueStable per arm: {string.Join(" ", results.Select(x => $"{x.label}({x.stable},{x.noFe},{x.valueStable})"))}")}");
        }

        Console.WriteLine();
        bool pass = rangeOk && controlOk && lightOk && flagOk && indexOk;
        Console.WriteLine($"=== esl-formid-guard: {(pass ? "PASS" : "FAIL")} ===");
        try { Directory.Delete(tmpDir, recursive: true); } catch { }
        return pass ? 0 : 1;
    }

    /// <summary>Synthesize a master carrying ONE weapon at the given FormKey, light or full, and write it (default
    /// params + NoNextFormIDProcessing, the product's incantation). Throws on a write refusal (callers that probe the
    /// range wrap it in Try).</summary>
    static void WriteLightMaster(ModKey key, string path, FormKey recFk, bool light)
    {
        var L = new SkyrimMod(key, SkyrimRelease.SkyrimSE) { IsSmallMaster = light };
        var w = new Weapon(recFk, SkyrimRelease.SkyrimSE) { EditorID = key.Name + "Weap", BasicStats = new WeaponBasicStats { Damage = 10 } };
        L.Weapons.Add(w);
        L.BeginWrite.ToPath(path).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).NoNextFormIDProcessing().Write();
    }

    /// <summary>RANGE/CONTROL-arm helper: try to write a (light or full) master with its record at
    /// <paramref name="objId"/>; on refusal returns false and outs the caught exception's TYPE NAME (not its message —
    /// so the assertion is robust to an upstream message rewording).</summary>
    static bool TryWriteLightAt(string tmpDir, string tag, uint objId, bool light, out string excType)
    {
        var key = new ModKey($"HcEslGuardRange_{tag}", ModType.Master);
        string path = Path.Combine(tmpDir, "range", key.FileName.String);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try { WriteLightMaster(key, path, new FormKey(key, objId), light); excType = ""; return true; }
        catch (Exception ex) { excType = ex.GetType().Name; return false; }
    }

    /// <summary>Reopen a plugin as an overlay and read its IsSmallMaster header flag (null if it can't open).</summary>
    static bool? ReopenIsSmallMaster(string path)
    {
        ISkyrimModGetter? ov = null;
        try { ov = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE); return ov.IsSmallMaster; }
        catch { return null; }
        finally { (ov as IDisposable)?.Dispose(); }
    }

    /// <summary>Reopen a patch STANDALONE and return the FormKey its first WEAP decodes to (the direct decode of the
    /// on-disk reference back to its defining master + object id).</summary>
    static FormKey RawFirstWeapFormKey(string path)
    {
        ISkyrimModGetter? ov = null;
        try { ov = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE); return ov.Weapons.First().FormKey; }
        finally { (ov as IDisposable)?.Dispose(); }
    }

    /// <summary>Reopen a patch STANDALONE and return the FormKeys ALL its WEAPs decode to — the index-independent
    /// identities the on-disk references resolve to (for the cross-ordering value-stability check).</summary>
    static List<FormKey> RawAllWeapFormKeys(string path)
    {
        ISkyrimModGetter? ov = null;
        try { ov = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE); return ov.Weapons.Select(w => w.FormKey).ToList(); }
        finally { (ov as IDisposable)?.Dispose(); }
    }

    /// <summary>Resolve a FormKey through the product LoadOrderResolver and return (winnerPluginFileName, overrideDepth);
    /// (null, -1) if unresolved. depth>=1 means an override of the master record was decoded + recognized.</summary>
    static (string? winner, int depth) ResolveWinner(IReadOnlyList<string> order, FormKey fk)
    {
        using var r = LoadOrderResolver.Build(order);
        var wi = r.ResolveWinner(fk);
        return wi is { } w ? (w.WinnerPlugin, w.OverrideDepth) : (null, -1);
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
