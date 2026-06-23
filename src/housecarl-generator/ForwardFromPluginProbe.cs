using System.Security.Cryptography;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// SELF-CONTAINED CI REGRESSION GUARD for FORWARD-FROM-PLUGIN (HCBR-2026-06-21 — "no convenient 'forward an earlier
/// plugin's version as my override'"). Drives the REAL product path (WritePatchBuilder.ForwardRecords — what
/// housecarl_forward_record calls) against a SYNTHESIZED 4-plugin order in TEMP — NO Skyrim.esm, so it runs in CI.
///
/// THE GAP (reproduced by construction): the write tools (set_field / bulk_apply) override the load-order WINNER. To
/// re-assert an EARLIER plugin's version over a later override (the report case: restore ATweaks' Searing-Sun spell over
/// Sacrilege's), there was no primitive — you had to hand-reconstruct the earlier record field-by-field. forward_record
/// copies the earlier plugin's WHOLE record verbatim as the override.
///
/// THE FIX (generic by construction): ForwardRecords resolves the source body from a NAMED plugin (not the winner) via
/// the resolver's GetRecord, then DEEP-COPIES it into the patch through WriteEngine.GenericGetOrAddAsOverride — the SAME
/// override primitive Apply uses, so it covers every record type (nested families included) with no per-type wiring and
/// no field pre-flight (a whole valid source record is legal by definition).
///
/// FIXTURE (priority master -> ModA -> ModB -> Other): master defines weapon X(Dmg 10), Y(Dmg 100); ModA overrides
/// X->20, Y->200 (the EARLIER override to re-assert); ModB overrides X->30, Y->300 (the WINNER); Other defines its OWN
/// weapon W and touches NEITHER X nor Y (the "source doesn't define it" Q3 fixture). Distinct Damage per version is the
/// by-construction discriminator: the read-back Damage proves WHICH plugin's version landed.
///
/// Arms (ALL required — a GREEN must mean "the contract holds"):
///   SETUP             — the order resolves X's winner to ModB (else the forward beats nothing — Q3).
///   FORWARD-NONWINNER — forward ModA's X: the patch carries Dmg 20 (ModA's), NOT 30 (winner ModB) NOR 10 (master); the
///                       header carries ONLY the origin master (NOT ModA — content is copied, not mastered); the prior
///                       winner (ModB) is reported and WasAlreadyWinner is false. THE core proof.
///   FORWARD-MASTER    — forward the ORIGIN master's X: Dmg 10 lands (revert-to-vanilla by naming a master as source).
///   NESTED            — forward a PlacedObject (lives in a cell -> the link-cache NESTED override branch, NOT the flat
///                       WEAP path): ModA's Scale 2.0 copied, NOT winner ModB's 3.0. Demonstrates the "every record
///                       type, nested families included" generality for the forward path itself, not just by reuse.
///   ALREADY-WINNER    — forward ModB's X (already winning): succeeds, Dmg 30, flagged WasAlreadyWinner (redundant — surfaced, not silent Q3).
///   MULTI             — forward [X,Y] from ModA in ONE call: both land (20, 200).
///   EXTEND            — forward X (fresh) then Y into the SAME patch (into=): both present (20, 200).
///   ORIGINALS         — the 4 source plugins are SHA-identical across the whole run (only the patch is ever written).
///   REJ-NOTINORDER    — a source plugin not in the order refuses loud ('not in the load order'), NO file.
///   REJ-DOESNTDEFINE  — forwarding X from Other (which defines only W) refuses loud ('does NOT define'), NO file.
///   REJ-INTOSELF      — from_plugin == the output patch itself refuses loud ('output patch itself'), NO file.
///   REJ-DUP           — the SAME target twice in one call refuses loud ('more than once'), NO file.
///
/// COVERAGE NOTE (Q3 — surface the gap, don't imply completeness): four of GetRecord's five null/refusal shapes are
/// armed above (not-in-order, doesn't-define, the-patch-itself, dup). The FIFTH — a source plugin EXCLUDED from the
/// index because Mutagen can't parse it — is the deliberately-uncovered branch: synthesizing an unparseable plugin here
/// would duplicate pkcu-regression's malformed-record machinery for one reject path. The branch is a plain
/// ExcludedPlugins lookup sharing the same OrdinalIgnoreCase table as the armed checks; if it ever earns a test, model
/// it on pkcu-regression's synthetic malformed plugin.
///
/// Run: dotnet run --project src/housecarl-generator -- forward-from-plugin-guard
/// </summary>
public static class ForwardFromPluginProbe
{
    const string MasterName = "HcFwdMaster.esm";
    const string ModAName = "HcFwdModA.esp";
    const string ModBName = "HcFwdModB.esp";
    const string OtherName = "HcFwdOther.esp";

    public static int RunGuard(string[] args)
    {
        Console.WriteLine("forward-from-plugin-guard — copy a NAMED plugin's version of a record as an override (HCBR-2026-06-21)");
        Console.WriteLine();

        var tmpDir = Path.Combine(Path.GetTempPath(), "hc-forward-from-plugin-guard");
        if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true);
        Directory.CreateDirectory(tmpDir);
        try { return RunChecks(tmpDir); }
        finally { try { Directory.Delete(tmpDir, recursive: true); } catch { /* best-effort */ } }
    }

    static int RunChecks(string tmpDir)
    {
        int failures = 0;
        void Check(string label, bool ok, string? detail = null)
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {label}{(ok || detail is null ? "" : $"\n        -> {detail}")}");
            if (!ok) failures++;
        }

        // ---- synthesize the 4-plugin order (see class doc). ----
        string mPath = Path.Combine(tmpDir, MasterName);
        string aPath = Path.Combine(tmpDir, ModAName);
        string bPath = Path.Combine(tmpDir, ModBName);
        string oPath = Path.Combine(tmpDir, OtherName);
        FormKey xFk, yFk, pFk;
        try
        {
            var m = new SkyrimMod(new ModKey("HcFwdMaster", ModType.Master), SkyrimRelease.SkyrimSE);
            var x = m.Weapons.AddNew(); x.EditorID = "HcFwdWeapX"; x.BasicStats = new WeaponBasicStats { Damage = 10 };
            var y = m.Weapons.AddNew(); y.EditorID = "HcFwdWeapY"; y.BasicStats = new WeaponBasicStats { Damage = 100 };
            xFk = x.FormKey; yFk = y.FormKey;
            // A NESTED fixture (the by-construction generality, SHOWN not just asserted): a PlacedObject lives in a cell,
            // so RecordNeedsSourceCache(it)==true and forwarding it exercises the link-cache NESTED override branch — a
            // DIFFERENT code path than the flat WEAP arms. Distinct Scale per version (master 1, ModA 2, ModB 3) is the
            // discriminator, exactly as Damage is for X/Y (a placed ref's parent chain reconstructs from the source overlay).
            var cell = new Cell(m.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcFwdCell" };
            var placed = new PlacedObject(m.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcFwdRef", Scale = 1.0f };
            cell.Persistent.Add(placed);
            var subBlock = new CellSubBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellSubBlock };
            subBlock.Cells.Add(cell);
            var cblock = new CellBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellBlock };
            cblock.SubBlocks.Add(subBlock);
            m.Cells.Records.Add(cblock);
            pFk = placed.FormKey;
            m.BeginWrite.ToPath(mPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
            var mCache = m.ToImmutableLinkCache();   // the nested PlacedObject override (below) reconstructs its parent chain from it

            var a = new SkyrimMod(new ModKey("HcFwdModA", ModType.Plugin), SkyrimRelease.SkyrimSE);
            ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(a, x)).BasicStats!.Damage = 20;
            ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(a, y)).BasicStats!.Damage = 200;
            ((IPlacedObject)WriteEngine.GenericGetOrAddAsOverride(a, placed, mCache)).Scale = 2.0f;
            a.BeginWrite.ToPath(aPath).WithLoadOrder(new ISkyrimModGetter[] { m }).Write();

            var b = new SkyrimMod(new ModKey("HcFwdModB", ModType.Plugin), SkyrimRelease.SkyrimSE);
            ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(b, x)).BasicStats!.Damage = 30;
            ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(b, y)).BasicStats!.Damage = 300;
            ((IPlacedObject)WriteEngine.GenericGetOrAddAsOverride(b, placed, mCache)).Scale = 3.0f;
            b.BeginWrite.ToPath(bPath).WithLoadOrder(new ISkyrimModGetter[] { m }).Write();

            var o = new SkyrimMod(new ModKey("HcFwdOther", ModType.Plugin), SkyrimRelease.SkyrimSE);
            var w = o.Weapons.AddNew(); w.EditorID = "HcFwdWeapW"; w.BasicStats = new WeaponBasicStats { Damage = 5 };
            o.BeginWrite.ToPath(oPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: could not synthesize fixtures: {ex.GetType().Name}: {(ex.InnerException ?? ex).Message}");
            return 1;
        }

        var orderPaths = new[] { mPath, aPath, bPath, oPath };   // priority: master -> ModA -> ModB -> Other
        // SHA the sources up front; assert untouched at the end (only the patch is ever written).
        var shaBefore = orderPaths.ToDictionary(p => p, ShaOf, StringComparer.Ordinal);

        // ---- SETUP: the order resolves X's winner to ModB (else the forward below beats nothing). ----
        using (var r = LoadOrderResolver.Build(orderPaths))
        {
            var v = r.Capture();
            Check("SETUP: winner of X resolves to ModB (the override the forward must beat)",
                v.ResolveWinner(xFk)?.WinnerPlugin == ModBName, $"winner={v.ResolveWinner(xFk)?.WinnerPlugin}");
        }

        // ---- FORWARD-NONWINNER (the core proof): forward ModA's X -> Dmg 20, masters=[origin], winner reported. ----
        {
            string pPath = Path.Combine(tmpDir, "HcFwdNonWinner.esp");
            using var r = LoadOrderResolver.Build(orderPaths);
            var o = WritePatchBuilder.ForwardRecords(r,
                new[] { new WritePatchBuilder.ForwardSpec { Target = xFk, FromPlugin = ModAName } }, pPath, extend: false);
            var dmg = o.Success ? ReadWeaponDamage(pPath, xFk) : null;
            bool mastersOk = o.Success && o.Masters.Count == 1 && o.Masters[0].Equals(MasterName, StringComparison.OrdinalIgnoreCase);
            bool winnerRep = o.Success && o.Forwarded.Count == 1 && o.Forwarded[0].PriorWinner == ModBName && !o.Forwarded[0].WasAlreadyWinner;
            Check("FORWARD-NONWINNER: ModA's X (Dmg 20) copied, NOT winner ModB's (30); header=[origin master only]; winner reported",
                o.Success && dmg == 20 && mastersOk && winnerRep,
                $"success={o.Success} dmg={dmg} (want 20) masters=[{(o.Success ? string.Join(",", o.Masters) : "")}] winnerRep={winnerRep} err=[{Trim(o.Error)}]");
        }

        // ---- FORWARD-MASTER (revert to vanilla): forward the ORIGIN master's X -> Dmg 10. ----
        {
            string pPath = Path.Combine(tmpDir, "HcFwdMasterVer.esp");
            using var r = LoadOrderResolver.Build(orderPaths);
            var o = WritePatchBuilder.ForwardRecords(r,
                new[] { new WritePatchBuilder.ForwardSpec { Target = xFk, FromPlugin = MasterName } }, pPath, extend: false);
            var dmg = o.Success ? ReadWeaponDamage(pPath, xFk) : null;
            Check("FORWARD-MASTER: forwarding the origin master's version reverts X to vanilla (Dmg 10)",
                o.Success && dmg == 10, $"success={o.Success} dmg={dmg} (want 10) err=[{Trim(o.Error)}]");
        }

        // ---- NESTED: forward a PlacedObject (the link-cache NESTED override path, a different branch than the flat WEAP
        //      arms) -> ModA's Scale 2.0 lands, NOT winner ModB's 3.0. Converts "every record type, nested families
        //      included" from by-construction to SHOWN for the forward path itself. ----
        {
            string pPath = Path.Combine(tmpDir, "HcFwdNested.esp");
            using var r = LoadOrderResolver.Build(orderPaths);
            var o = WritePatchBuilder.ForwardRecords(r,
                new[] { new WritePatchBuilder.ForwardSpec { Target = pFk, FromPlugin = ModAName } }, pPath, extend: false);
            var scale = o.Success ? ReadPlacedScale(pPath, pFk) : null;
            Check("NESTED: forwarding a PlacedObject (nested link-cache path) copies ModA's version (Scale 2.0), NOT the winner's (3.0)",
                o.Success && scale == 2.0f, $"success={o.Success} scale={scale} (want 2) err=[{Trim(o.Error)}]");
        }

        // ---- ALREADY-WINNER: forward ModB's X (already the winner) -> succeeds, Dmg 30, flagged redundant. ----
        {
            string pPath = Path.Combine(tmpDir, "HcFwdAlreadyWin.esp");
            using var r = LoadOrderResolver.Build(orderPaths);
            var o = WritePatchBuilder.ForwardRecords(r,
                new[] { new WritePatchBuilder.ForwardSpec { Target = xFk, FromPlugin = ModBName } }, pPath, extend: false);
            var dmg = o.Success ? ReadWeaponDamage(pPath, xFk) : null;
            bool flagged = o.Success && o.Forwarded.Count == 1 && o.Forwarded[0].WasAlreadyWinner;
            Check("ALREADY-WINNER: forwarding the current winner succeeds (Dmg 30) and is flagged redundant (Q3, not silent)",
                o.Success && dmg == 30 && flagged, $"success={o.Success} dmg={dmg} (want 30) flagged={flagged} err=[{Trim(o.Error)}]");
        }

        // ---- MULTI: forward [X,Y] from ModA in ONE call -> both land (20, 200). ----
        {
            string pPath = Path.Combine(tmpDir, "HcFwdMulti.esp");
            using var r = LoadOrderResolver.Build(orderPaths);
            var o = WritePatchBuilder.ForwardRecords(r, new[]
            {
                new WritePatchBuilder.ForwardSpec { Target = xFk, FromPlugin = ModAName },
                new WritePatchBuilder.ForwardSpec { Target = yFk, FromPlugin = ModAName },
            }, pPath, extend: false);
            var dx = o.Success ? ReadWeaponDamage(pPath, xFk) : null;
            var dy = o.Success ? ReadWeaponDamage(pPath, yFk) : null;
            Check("MULTI: two records forwarded from ModA in one call both land (X=20, Y=200)",
                o.Success && o.Forwarded.Count == 2 && dx == 20 && dy == 200,
                $"success={o.Success} count={(o.Success ? o.Forwarded.Count : 0)} dx={dx} dy={dy} err=[{Trim(o.Error)}]");
        }

        // ---- EXTEND (into=): forward X (fresh) then Y into the SAME patch -> both present (20, 200). ----
        {
            string pPath = Path.Combine(tmpDir, "HcFwdExtend.esp");
            bool firstOk, secondOk; ushort? dx = null, dy = null;
            using (var r = LoadOrderResolver.Build(orderPaths))
                firstOk = WritePatchBuilder.ForwardRecords(r,
                    new[] { new WritePatchBuilder.ForwardSpec { Target = xFk, FromPlugin = ModAName } }, pPath, extend: false).Success;
            using (var r = LoadOrderResolver.Build(orderPaths))
            {
                var o2 = WritePatchBuilder.ForwardRecords(r,
                    new[] { new WritePatchBuilder.ForwardSpec { Target = yFk, FromPlugin = ModAName } }, pPath, extend: true);
                secondOk = o2.Success && o2.Extended;
            }
            if (firstOk && secondOk) { dx = ReadWeaponDamage(pPath, xFk); dy = ReadWeaponDamage(pPath, yFk); }
            Check("EXTEND: forward X fresh, then Y into the same patch (into=) — both survive (X=20, Y=200)",
                firstOk && secondOk && dx == 20 && dy == 200, $"first={firstOk} second={secondOk} dx={dx} dy={dy}");
        }

        // ---- Q3 rejects (whole call refused, NO file written, named reason). ----
        void RejectArm(string label, string stem, WritePatchBuilder.ForwardSpec[] specs, Func<string, bool> msgOk)
        {
            string pPath = Path.Combine(tmpDir, stem + ".esp");
            using var r = LoadOrderResolver.Build(orderPaths);
            var o = WritePatchBuilder.ForwardRecords(r, specs, pPath, extend: false);
            bool refused = !o.Success, noFile = !File.Exists(pPath), named = o.Error is not null && msgOk(o.Error);
            Check(label, refused && noFile && named, $"refused={refused} noFile={noFile} named={named} err=[{Trim(o.Error)}]");
        }

        RejectArm("REJ-NOTINORDER: source plugin not in the order refuses loud, no file", "HcFwdNotInOrder",
            new[] { new WritePatchBuilder.ForwardSpec { Target = xFk, FromPlugin = "NotReal.esp" } },
            msg => msg.Contains("not in the load order", StringComparison.OrdinalIgnoreCase));

        RejectArm("REJ-DOESNTDEFINE: source in the order but doesn't define the record refuses loud, no file", "HcFwdNoDef",
            new[] { new WritePatchBuilder.ForwardSpec { Target = xFk, FromPlugin = OtherName } },
            msg => msg.Contains("does NOT define", StringComparison.OrdinalIgnoreCase) || msg.Contains("does not define", StringComparison.OrdinalIgnoreCase));

        // INTOSELF: from_plugin == the output patch's own filename (HcFwdSelf.esp). The self-check fires before the
        // in-order check, so it's reported as the self-forward no-op, not a misleading "not in the load order".
        RejectArm("REJ-INTOSELF: from_plugin == the output patch itself refuses loud, no file", "HcFwdSelf",
            new[] { new WritePatchBuilder.ForwardSpec { Target = xFk, FromPlugin = "HcFwdSelf.esp" } },
            msg => msg.Contains("output patch itself", StringComparison.OrdinalIgnoreCase));

        RejectArm("REJ-DUP: the same target twice in one call refuses loud, no file", "HcFwdDup",
            new[]
            {
                new WritePatchBuilder.ForwardSpec { Target = xFk, FromPlugin = ModAName },
                new WritePatchBuilder.ForwardSpec { Target = xFk, FromPlugin = ModBName },
            },
            msg => msg.Contains("more than once", StringComparison.OrdinalIgnoreCase));

        // ---- ORIGINALS: every source plugin SHA-identical across the whole run (only patches were ever written). ----
        bool untouched = shaBefore.All(kv => string.Equals(kv.Value, ShaOf(kv.Key), StringComparison.Ordinal));
        Check("ORIGINALS: the 4 source plugins are byte-identical after the run (only the patch is written)", untouched,
            untouched ? null : "a source plugin's bytes changed — forwarding must never write a source");

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "forward-from-plugin-guard: ALL PASS" : $"forward-from-plugin-guard: {failures} FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }

    // ---- helpers ----

    static ushort? ReadWeaponDamage(string patchPath, FormKey fk)
    {
        ISkyrimModGetter? back = null;
        try
        {
            back = SkyrimMod.CreateFromBinaryOverlay(patchPath, SkyrimRelease.SkyrimSE);
            return back.EnumerateMajorRecords<IWeaponGetter>().FirstOrDefault(r => r.FormKey == fk)?.BasicStats?.Damage;
        }
        catch { return null; }
        finally { (back as IDisposable)?.Dispose(); }
    }

    static float? ReadPlacedScale(string patchPath, FormKey fk)
    {
        ISkyrimModGetter? back = null;
        try
        {
            back = SkyrimMod.CreateFromBinaryOverlay(patchPath, SkyrimRelease.SkyrimSE);
            return back.EnumerateMajorRecords<IPlacedObjectGetter>().FirstOrDefault(r => r.FormKey == fk)?.Scale;
        }
        catch { return null; }
        finally { (back as IDisposable)?.Dispose(); }
    }

    static string ShaOf(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs));
    }

    static string Trim(string? s) => s is null ? "" : (s.Length <= 160 ? s.Replace("\n", " ") : s[..160].Replace("\n", " ") + "…");
}
