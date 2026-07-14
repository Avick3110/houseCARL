using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// COMPACT/MERGE Wave 2 — SERVICE-POLICY guard for housecarl_compact_plugin (PR #122 review #3). Where
/// remap-wave2-compact-guard drives the ENGINE (RenumberModInto) directly, this drives the SERVICE
/// (<see cref="LoadOrderService.CompactPlugin"/>) over a synthetic MO2 instance, exercising the policy branches the
/// engine guard bypasses — the paths most likely to regress silently:
///   CLEAN     — a self-contained nested mod compacts to a NEW file (default lane): success, !InPlace, P′ in a fresh
///               folder with every originating record in the ESL window and the light flag set.
///   ESL-OFF   — esl=false renumbers without the light flag.
///   OVERRIDE  — compacting a mod that OVERRIDES a master's interior cell and adds a NEW placed ref keeps the override
///               cell at its master FormID while the new child renumbers (the realistic patch case; copied 2 / renum 1).
///   REFUSE-EXT— a mod another plugin references is REFUSED (the external referencer named), nothing written.
///   GATE      — repoint_externals without in_place is REFUSED (the coherence gate; review #1).
///   NOT-ACTIVE— a plugin found nowhere on disk is refused (an inactive one FOUND on disk now compacts — see OFF-ORDER).
///   OFF-ORDER — a plugin in an UNLISTED mod folder (a fresh houseCARL patch pre-MO2-refresh; HCBR-2026-07-14-02 gap 3)
///               resolves by filename and compacts normally, with the OFF-ORDER note.
///   FLAG-ONLY — an override-only plugin: esl=true copies verbatim + sets the light flag (renum 0); esl=false refuses.
///   CONSENT   — in_place + repoint without acknowledge returns the CONFIRM prompt (no write); WITH acknowledge it
///               compacts the target IN PLACE and repoints the external referencer to the new key (the full opt-in path).
/// Run: dotnet run --project src/housecarl-generator compact-service-guard
/// </summary>
public static class CompactServiceGuardProbe
{
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("################  COMPACT Wave 2 — service-policy guard (housecarl_compact_plugin)  ################");
        Console.WriteLine();
        int fail = 0;
        void Check(bool c, string label) { Console.WriteLine((c ? "  PASS  " : "  FAIL  ") + label); if (!c) fail++; }

        var root = Path.Combine(Path.GetTempPath(), "hc-compact-service-guard-" + Guid.NewGuid().ToString("N"));
        try
        {
            string instance = Path.Combine(root, "instance");
            string profiles = Path.Combine(instance, "profiles", "Default");
            string mods = Path.Combine(instance, "mods");
            string data = Path.Combine(root, "game", "Data");
            Directory.CreateDirectory(profiles); Directory.CreateDirectory(mods); Directory.CreateDirectory(data);
            File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
                "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
                + Path.Combine(root, "game").Replace(@"\", @"\\") + ")\r\n");

            // ---- fixture mods (each in its own MO2 mod folder) ----
            var selfKey = new ModKey("HcCsSelf", ModType.Plugin);
            var swOld = new FormKey(selfKey, 0xA01); var scOld = new FormKey(selfKey, 0xA02); var spOld = new FormKey(selfKey, 0xA03);
            WriteMod(mods, "SelfMod", selfKey, Array.Empty<string>(), m =>
            {
                m.Weapons.Add(new Weapon(swOld, SkyrimRelease.SkyrimSE) { EditorID = "HcCsWeap", BasicStats = new WeaponBasicStats { Damage = 5 } });
                var c = new Cell(scOld, SkyrimRelease.SkyrimSE) { EditorID = "HcCsCell", Flags = Cell.Flag.IsInteriorCell };
                c.Temporary.Add(new PlacedObject(spOld, SkyrimRelease.SkyrimSE) { EditorID = "HcCsRef" });
                FileInterior(m, c);
            });

            var baseKey = new ModKey("HcCsBase", ModType.Master);
            var bcOld = new FormKey(baseKey, 0xA01);
            WriteMod(mods, "BaseMod", baseKey, Array.Empty<string>(), m =>
            {
                var c = new Cell(bcOld, SkyrimRelease.SkyrimSE) { EditorID = "HcCsBaseCell", Flags = Cell.Flag.IsInteriorCell };
                FileInterior(m, c);
            });

            var overKey = new ModKey("HcCsOver", ModType.Plugin);
            var opOld = new FormKey(overKey, 0xA01);
            {
                using var baseOv = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(mods, "BaseMod", baseKey.FileName.String), SkyrimRelease.SkyrimSE);
                var baseCache = baseOv.ToImmutableLinkCache();
                var baseCell = baseOv.EnumerateMajorRecords<ICellGetter>().First(c => c.EditorID == "HcCsBaseCell");
                var modDir = Path.Combine(mods, "OverMod"); Directory.CreateDirectory(modDir);
                var o = new SkyrimMod(overKey, SkyrimRelease.SkyrimSE);
                var ovCell = (ICell)WriteEngine.GenericGetOrAddAsOverride(o, baseCell, baseCache);
                ovCell.Temporary.Add(new PlacedObject(opOld, SkyrimRelease.SkyrimSE) { EditorID = "HcCsOverRef" });
                o.ModHeader.Stats.NextFormID = 0xA02;
                o.BeginWrite.ToPath(Path.Combine(modDir, overKey.FileName.String)).WithLoadOrder(new[] { baseOv }).NoNextFormIDProcessing().Write();
            }

            var libKey = new ModKey("HcCsLib", ModType.Plugin);
            var lwOld = new FormKey(libKey, 0xA01);
            WriteMod(mods, "LibMod", libKey, Array.Empty<string>(), m =>
                m.Weapons.Add(new Weapon(lwOld, SkyrimRelease.SkyrimSE) { EditorID = "HcCsLibWeap", BasicStats = new WeaponBasicStats { Damage = 8 } }));

            var depKey = new ModKey("HcCsDep", ModType.Plugin);
            var dlOld = new FormKey(depKey, 0xA01);
            {
                using var libOv = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(mods, "LibMod", libKey.FileName.String), SkyrimRelease.SkyrimSE);
                var modDir = Path.Combine(mods, "DepMod"); Directory.CreateDirectory(modDir);
                var d = new SkyrimMod(depKey, SkyrimRelease.SkyrimSE);
                var fl = new FormList(dlOld, SkyrimRelease.SkyrimSE) { EditorID = "HcCsDepList" };
                fl.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(lwOld));
                d.FormLists.Add(fl);
                d.ModHeader.Stats.NextFormID = 0xA02;
                d.BeginWrite.ToPath(Path.Combine(modDir, depKey.FileName.String)).WithLoadOrder(new[] { libOv }).NoNextFormIDProcessing().Write();
            }

            // ---- profile files (masters first, then plugins; one mod folder per plugin) ----
            File.WriteAllText(Path.Combine(profiles, "loadorder.txt"),
                "# header\r\n" + string.Join("\r\n", baseKey.FileName, selfKey.FileName, libKey.FileName, overKey.FileName, depKey.FileName) + "\r\n");
            File.WriteAllText(Path.Combine(profiles, "plugins.txt"),
                string.Join("\r\n", "*" + baseKey.FileName, "*" + selfKey.FileName, "*" + libKey.FileName, "*" + overKey.FileName, "*" + depKey.FileName) + "\r\n");
            File.WriteAllText(Path.Combine(profiles, "modlist.txt"),
                "# header\r\n" + string.Join("\r\n", "+DepMod", "+OverMod", "+LibMod", "+SelfMod", "+BaseMod") + "\r\n");

            var store = new UserConfigStore(Path.Combine(root, "houseCARL.user.json"));
            using var svc = LoadOrderService.WithInstance(instance, 0, store);
            svc.Stats();   // warm the lazy index once

            // ---- CLEAN: self-contained nested mod -> NEW-file compact, every record in the ESL window, light-flagged ----
            {
                var o = svc.CompactPlugin("HcCsSelf.esp");
                bool windowOk = false, lightOk = false;
                if (o.Success && File.Exists(o.OutputPath))
                {
                    using var pp = SkyrimMod.CreateFromBinaryOverlay(o.OutputPath, SkyrimRelease.SkyrimSE);
                    windowOk = pp.EnumerateMajorRecords().Where(r => r.FormKey.ModKey == selfKey)
                        .All(r => r.FormKey.ID >= RemapEngine.EslFloor && r.FormKey.ID <= RemapEngine.EslCeiling);
                    lightOk = pp.IsSmallMaster;
                }
                Check(o.Success && !o.InPlace && o.Esl && o.RecordsRenumbered == 3 && o.ExternalPlugins.Count == 0 && windowOk && lightOk,
                    $"CLEAN new-file compact (renum {o.RecordsRenumbered}, inWindow {windowOk}, light {lightOk}, ext {o.ExternalPlugins.Count}{(o.Success ? "" : "; ERR " + o.Error)})");
            }

            // ---- ESL-OFF: esl=false -> renumbered, NOT light-flagged ----
            {
                var o = svc.CompactPlugin("HcCsSelf.esp", esl: false);
                bool notLight = o.Success && File.Exists(o.OutputPath) && !SkyrimMod.CreateFromBinaryOverlay(o.OutputPath, SkyrimRelease.SkyrimSE).IsSmallMaster;
                Check(o.Success && !o.Esl && notLight, $"ESL-OFF contiguous renumber, no light flag (esl {o.Esl}, notLight {notLight})");
            }

            // ---- OVERRIDE: compacting OverMod keeps the override cell at its master key, renumbers the new placed ----
            {
                var o = svc.CompactPlugin("HcCsOver.esp");
                bool ok = false;
                if (o.Success && File.Exists(o.OutputPath))
                {
                    using var pp = SkyrimMod.CreateFromBinaryOverlay(o.OutputPath, SkyrimRelease.SkyrimSE);
                    var cell = pp.EnumerateMajorRecords<ICellGetter>().FirstOrDefault(c => c.EditorID == "HcCsBaseCell");
                    var placed = cell?.Temporary.FirstOrDefault(p => p.EditorID == "HcCsOverRef");
                    ok = cell?.FormKey == bcOld                                        // override cell stays at the master FormID
                         && placed is not null && placed.FormKey.ModKey == overKey      // the new placed renumbered into Over's own window
                         && placed.FormKey.ID >= RemapEngine.EslFloor && placed.FormKey.ID <= RemapEngine.EslCeiling;
                }
                Check(o.Success && o.RecordsCopied == 2 && o.RecordsRenumbered == 1 && ok,
                    $"OVERRIDE master-cell preserved + new child renumbered (copied {o.RecordsCopied}, renum {o.RecordsRenumbered}, struct {ok}{(o.Success ? "" : "; ERR " + o.Error)})");
            }

            // ---- REFUSE-EXT: LibMod is referenced by DepMod -> refused, DepMod named, nothing written ----
            {
                var o = svc.CompactPlugin("HcCsLib.esp");
                Check(!o.Success && !o.NeedsAcknowledge && (o.Error?.Contains("HcCsDep.esp", StringComparison.OrdinalIgnoreCase) ?? false),
                    $"REFUSE-EXT external referencer named ({o.Error?.Split('.')[0]})");
            }

            // ---- GATE: repoint_externals without in_place -> refused (review #1 coherence gate) ----
            {
                var o = svc.CompactPlugin("HcCsLib.esp", repointExternals: true, inPlace: false);
                Check(!o.Success && (o.Error?.Contains("requires in_place", StringComparison.OrdinalIgnoreCase) ?? false),
                    $"GATE repoint_externals requires in_place ({o.Error?.Split('.')[0]})");
            }

            // ---- NOT-ACTIVE: a plugin found NOWHERE on disk -> still refused (the gate now falls through to a locate) ----
            {
                var o = svc.CompactPlugin("HcCsNope.esp");
                Check(!o.Success && (o.Error?.Contains("not an active plugin", StringComparison.OrdinalIgnoreCase) ?? false)
                                 && (o.Error?.Contains("no on-disk copy", StringComparison.OrdinalIgnoreCase) ?? false),
                    $"NOT-ACTIVE nowhere-on-disk plugin refused ({o.Error?.Split('—')[0].Trim()})");
            }

            // ---- OFF-ORDER: a plugin in an UNLISTED mod folder (not in modlist/plugins/loadorder — the fresh houseCARL
            //      patch before the MO2 refresh, HCBR-2026-07-14-02 gap 3) resolves by filename and compacts normally ----
            {
                var offKey = new ModKey("HcCsOff", ModType.Plugin);
                var owOld = new FormKey(offKey, 0xA01);
                WriteMod(mods, "OffOrderMod", offKey, Array.Empty<string>(), m =>
                    m.Weapons.Add(new Weapon(owOld, SkyrimRelease.SkyrimSE) { EditorID = "HcCsOffWeap", BasicStats = new WeaponBasicStats { Damage = 3 } }));
                var o = svc.CompactPlugin("HcCsOff.esp");
                bool lightOk = false;
                if (o.Success && File.Exists(o.OutputPath))
                {
                    using var pp = SkyrimMod.CreateFromBinaryOverlay(o.OutputPath, SkyrimRelease.SkyrimSE);
                    lightOk = pp.IsSmallMaster && pp.EnumerateMajorRecords().All(r => r.FormKey.ID >= RemapEngine.EslFloor && r.FormKey.ID <= RemapEngine.EslCeiling);
                }
                Check(o.Success && o.RecordsRenumbered == 1 && lightOk && (o.Note?.Contains("OFF-ORDER") ?? false),
                    $"OFF-ORDER unlisted-folder plugin compacts, noted (renum {o.RecordsRenumbered}, light {lightOk}, note {(o.Note?.Contains("OFF-ORDER") ?? false)}{(o.Success ? "" : "; ERR " + o.Error)})");
            }

            // ---- FLAG-ONLY: an override-only plugin (nothing to renumber). esl=true sets the light flag on a verbatim
            //      copy (the all-override compatibility patch's ESL endgame); esl=false refuses (nothing to do) ----
            {
                var flagKey = new ModKey("HcCsFlag", ModType.Plugin);
                using (var libOv = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(mods, "LibMod", libKey.FileName.String), SkyrimRelease.SkyrimSE))
                {
                    var libCache = libOv.ToImmutableLinkCache();
                    var modDir = Path.Combine(mods, "FlagOnlyMod"); Directory.CreateDirectory(modDir);   // deliberately UNLISTED too
                    var f = new SkyrimMod(flagKey, SkyrimRelease.SkyrimSE);
                    var ow = (IWeapon)WriteEngine.GenericGetOrAddAsOverride(f, libOv.Weapons.First(), libCache);
                    ow.BasicStats!.Damage = 42;                                          // an actual override delta
                    f.BeginWrite.ToPath(Path.Combine(modDir, flagKey.FileName.String)).WithLoadOrder(new[] { libOv }).Write();
                }
                // esl=false FIRST — the esl=true success below writes a second on-disk copy of the basename (the
                // compacted output folder), after which a bare-filename locate is legitimately AMBIGUOUS.
                var oNo = svc.CompactPlugin("HcCsFlag.esp", esl: false);
                Check(!oNo.Success && (oNo.Error?.Contains("nothing to compact", StringComparison.OrdinalIgnoreCase) ?? false),
                    $"FLAG-ONLY esl=false: refused, nothing to do ({oNo.Error?.Split('.')[0]})");
                var o = svc.CompactPlugin("HcCsFlag.esp");
                bool lightOk = false, verbatim = false;
                if (o.Success && File.Exists(o.OutputPath))
                {
                    using var pp = SkyrimMod.CreateFromBinaryOverlay(o.OutputPath, SkyrimRelease.SkyrimSE);
                    lightOk = pp.IsSmallMaster;
                    var w = pp.EnumerateMajorRecords<IWeaponGetter>().FirstOrDefault();
                    verbatim = w is not null && w.FormKey == lwOld && w.BasicStats?.Damage == 42;   // override kept at the master key, delta intact
                }
                Check(o.Success && o.RecordsRenumbered == 0 && lightOk && verbatim && (o.Note?.Contains("no originating records") ?? false),
                    $"FLAG-ONLY esl=true: verbatim copy + light flag (renum {o.RecordsRenumbered}, light {lightOk}, verbatim {verbatim}{(o.Success ? "" : "; ERR " + o.Error)})");
                // …and NOW the bare name resolves to TWO on-disk copies (the unlisted fixture + the compacted output) —
                // the off-order locate must surface the ambiguity, never guess (Q3).
                var oAmb = svc.CompactPlugin("HcCsFlag.esp");
                Check(!oAmb.Success && (oAmb.Error?.Contains("ambiguous", StringComparison.OrdinalIgnoreCase) ?? false),
                    $"OFF-ORDER-AMBIGUOUS: a basename two folders provide is refused, named ({oAmb.Error?.Split('—')[0].Trim()})");
            }

            // ---- CONSENT: in_place + repoint WITHOUT acknowledge -> confirm prompt (no write); WITH it -> in-place compact + repoint ----
            {
                var pre = svc.CompactPlugin("HcCsLib.esp", repointExternals: true, inPlace: true, acknowledge: false);
                bool confirm = pre.NeedsAcknowledge && !pre.Success
                               && (pre.Error?.Contains("HcCsLib.esp", StringComparison.OrdinalIgnoreCase) ?? false)
                               && (pre.Error?.Contains("HcCsDep.esp", StringComparison.OrdinalIgnoreCase) ?? false);
                Check(confirm, $"CONSENT in_place+repoint without ack returns CONFIRM listing target+external (needsAck {pre.NeedsAcknowledge})");

                var o = svc.CompactPlugin("HcCsLib.esp", repointExternals: true, inPlace: true, acknowledge: true);
                FormKey? libWeapAfter = null, depRefAfter = null;
                var libPath = Path.Combine(mods, "LibMod", libKey.FileName.String);
                var depPath = Path.Combine(mods, "DepMod", depKey.FileName.String);
                if (o.Success)
                {
                    using (var lb = SkyrimMod.CreateFromBinaryOverlay(libPath, SkyrimRelease.SkyrimSE))
                        libWeapAfter = lb.Weapons.FirstOrDefault(w => w.EditorID == "HcCsLibWeap")?.FormKey;
                    using (var db = SkyrimMod.CreateFromBinaryOverlay(depPath, SkyrimRelease.SkyrimSE))
                        depRefAfter = db.FormLists.First().Items.FirstOrDefault()?.FormKey;
                }
                bool repointOk = o.Success && o.InPlace
                                 && libWeapAfter is { } lw && lw.ModKey == libKey && lw.ID >= RemapEngine.EslFloor && lw.ID <= RemapEngine.EslCeiling
                                 && depRefAfter == libWeapAfter
                                 && o.Repointed.Count == 1 && o.Repointed[0].Success;
                Check(repointOk, $"CONSENT in_place+repoint with ack: Lib weapon -> {libWeapAfter}, Dep ref -> {depRefAfter}, repointed {o.Repointed.Count(r => r.Success)}/{o.Repointed.Count}{(o.Success ? "" : "; ERR " + o.Error)}");
            }
        }
        finally { try { Directory.Delete(root, true); } catch { } }

        Console.WriteLine();
        Console.WriteLine($"=== compact-service-guard: {(fail == 0 ? "PASS" : $"FAIL ({fail})")} ===");
        return fail == 0 ? 0 : 1;
    }

    /// <summary>Build a plugin via <paramref name="build"/> and write it to its own MO2 mod folder under
    /// <paramref name="mods"/> (no masters unless the build adds referenced records).</summary>
    static void WriteMod(string mods, string folder, ModKey key, IReadOnlyList<string> masters, Action<SkyrimMod> build)
    {
        var dir = Path.Combine(mods, folder);
        Directory.CreateDirectory(dir);
        var m = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        build(m);
        m.BeginWrite.ToPath(Path.Combine(dir, key.FileName.String)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
    }

    /// <summary>File an interior cell into a mod's Cells block tree by its FormID digits (mirrors WriteEngine.AddInteriorCell).</summary>
    static void FileInterior(SkyrimMod mod, Cell cell)
    {
        uint id = cell.FormKey.ID;
        int blockN = (int)(id % 10), subN = (int)((id / 10) % 10);
        var records = mod.Cells.Records;
        var block = records.FirstOrDefault(b => b.BlockNumber == blockN);
        if (block is null) { block = new CellBlock { BlockNumber = blockN, GroupType = GroupTypeEnum.InteriorCellBlock }; records.Add(block); }
        var sub = block.SubBlocks.FirstOrDefault(s => s.BlockNumber == subN);
        if (sub is null) { sub = new CellSubBlock { BlockNumber = subN, GroupType = GroupTypeEnum.InteriorCellSubBlock }; block.SubBlocks.Add(sub); }
        sub.Cells.Add(cell);
    }
}
