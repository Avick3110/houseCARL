using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Strings;
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
///   LOCALIZED — (#362) a source whose .STRINGS live in the game-Data folder rather than its own mod folder keeps its
///               FULL+DESC through the compact. Its own instance: the fixture turns on a game-Data Skyrim.esm, which
///               the order above has none of. A baseline arm reads the source with the BARE overlay first and requires
///               it to come back EMPTY — without that, the arm would pass on a fixture that was never localized. A
///               further arm pins that P′ is written non-localized with its strings inline, which is what makes the
///               read-back a read of the written bytes.
/// Run: dotnet run --project src/housecarl-generator compact-service-guard
/// </summary>
public static class CompactServiceGuardProbe
{
    [CiProbe("compact-service-guard")]
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("################  COMPACT Wave 2 — service-policy guard (housecarl_compact_plugin)  ################");
        Console.WriteLine();
        int fail = 0;
        void Check(bool c, string label) { Console.WriteLine((c ? "  PASS  " : "  FAIL  ") + label); if (!c) fail++; }

        var root = Path.Combine(Path.GetTempPath(), "hc-compact-service-guard-" + Guid.NewGuid().ToString("N"));

        // The REMEDY arm below drives the DEFAULT write lane, which pre-flights through the corpus rulebook. Generated
        // into a directory OUTSIDE `root` (which the finally deletes) and the previous CorpusPath restored afterwards:
        // CorpusPath is process-global, and leaving it pointing into a deleted temp dir would break whichever probe
        // ci-all runs next. CorpusGenerator memoizes its reflection, so under ci-all this costs nothing.
        var corpusDir = Path.Combine(Path.GetTempPath(), "hc-compact-guard-corpus");
        var priorCorpusPath = CorpusRulebook.CorpusPath;
        CorpusGenerator.GenerateAll(corpusDir, Path.Combine(corpusDir, "ref"));
        CorpusRulebook.CorpusPath = Path.Combine(corpusDir, "corpus.json");

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

                // The runtime distributor layer addresses records by FormID, which is exactly what a compaction moves,
                // and the identify pass reads plugins only — so the report says both, on every compaction rather than
                // only when the external-referencer list came back populated (this one is the clean case, and it is
                // still the case where a _DISTR.ini goes quietly dead).
                var rendered = WriteTools.RenderCompact(o);
                // PRESENCE, against the shared constant — see the twin arm in merge-service-guard for why the absence
                // assertions this replaces were worthless.
                Check(rendered.Contains(WriteSentences.CompactRuntimeConfigs),
                    "CLEAN the runtime-config loss reaches user output, verbatim from the shared sentence");
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

            // ---- LOCALIZED (#362): a source whose .STRINGS live in the game-Data folder, not its own mod folder ----
            //      Its own instance, because the fixture's load-bearing part is a game-Data Skyrim.esm (see
            //      LocalizedStringsFixture) and the order above deliberately has none.
            // ---- Q2-A, CUT (2026-08-26). This block used to pin the opposite outcome: a localized source in the
            //      arrangement a write can rewrite kept its output LOCALIZED, with a matching rewritten table set
            //      beside P′. It was measured working, and it was cut anyway — it generated defects faster than
            //      review cleared them, over a population the frequency sweep priced at one plugin in sixty-three on a
            //      real load order, and Aaron's ground is that multi-language mods do not exist in the wild (Nexus
            //      ships single-language translations), so the language the read resolves IS the mod's language.
            //
            //      The fixture stays — it is the strongest localized source a guard can build, a complete loose set
            //      beside the plugin with two languages — and its arms now pin the DE-LOCALIZED outcome and the note
            //      that announces it. Kept rather than deleted precisely because this is the arrangement the cut arm
            //      served: if a localized P′ ever comes back, it comes back here first.
            {
                var q2Root = Path.Combine(root, "q2");
                var q2 = new LocalizedStringsFixture.Spec(
                    "Q2Src", new ModKey("HcCsQ2", ModType.Plugin), "Q2 NAME", "Q2 DESC",
                    StringsBeside: true, SecondLanguage: "French");
                var q2fx = LocalizedStringsFixture.Build(q2Root, new[] { q2 });
                var q2Store = new UserConfigStore(Path.Combine(q2Root, "houseCARL.user.json"));
                using var q2Svc = LoadOrderService.WithInstance(q2fx.Instance, 0, q2Store);
                q2Svc.Stats();

                var q2Src = Path.Combine(q2fx.Mods, q2.ModFolder, q2.Key.FileName.String);
                var q2Shape = LocalizedStrings.Assess(q2Src, q2fx.Data);
                Check(q2Shape.Shape == LocalizedShape.LooseComplete && q2Shape.Languages.Count == 2,
                    $"Q2 fixture: the source is a complete loose set beside the plugin, two languages (shape={q2Shape.Shape} langs=[{string.Join(",", q2Shape.Languages)}])");

                var q2o = q2Svc.CompactPlugin(q2.Key.FileName.String);
                bool localizedOut = true, tablesBeside = true;
                string? en = null;
                if (q2o.Success && File.Exists(q2o.OutputPath))
                {
                    using (var ov = SkyrimMod.CreateFromBinaryOverlay(q2o.OutputPath, SkyrimRelease.SkyrimSE))
                        localizedOut = ov.UsingLocalization;
                    tablesBeside = Directory.Exists(Path.Combine(Path.GetDirectoryName(q2o.OutputPath)!, "Strings"));
                    en = ReadLang(q2o.OutputPath, LocalizedStringsFixture.WeaponEdid(q2), Language.English);
                }
                // READ BACK FROM THE OUTPUT. This is what makes the report note's claim about P′ a measured one
                // rather than something computed from the source folder's file list — the defect that let a note
                // announce tables that were never written.
                Check(q2o.Success && !localizedOut && !tablesBeside,
                    $"Q2 an accepted-shape localized source compacts to a DE-LOCALIZED P′ with no Strings folder beside it (success={q2o.Success} localized={localizedOut} tablesBeside={tablesBeside}{(q2o.Success ? "" : ", ERR " + q2o.Error)})");
                Check(en == q2.Name,
                    $"Q2 the text this read resolved is written into the plugin itself and reads back (English='{en}')");

                // THE NOTE. It names what the SOURCE shipped, states the output is not localized, and carries no
                // count: the sentence this replaces said "including the 1 other language(s) it shipped (English,
                // French)" — a count of N−1 against a list of N, with the surviving language listed as lost.
                var note = q2o.Note ?? "";
                Check(q2o.Success && note.Contains("is NOT localized", StringComparison.Ordinal)
                      && note.Contains("(English, French)", StringComparison.Ordinal)
                      && !note.Contains("other language(s) it shipped", StringComparison.Ordinal),
                    $"Q2 the report names both languages the source shipped and claims no count against that list [{note}]");

                // IN PLACE, the same source is REFUSED. This is the arm that went from "writes" to "refuses" when the
                // in-place localized arm was cut, so it is the one that would notice it coming back.
                var q2Before = File.ReadAllBytes(q2Src);
                var q2ip = q2Svc.CompactPlugin(q2.Key.FileName.String, inPlace: true, acknowledge: false);
                bool q2Untouched = File.ReadAllBytes(q2Src).AsSpan().SequenceEqual(q2Before);
                bool q2NoStaging = !Directory.Exists(Path.Combine(Path.GetDirectoryName(q2Src)!, ".housecarl-tmp"));
                Check(!q2ip.Success && !q2ip.NeedsAcknowledge && q2Untouched && q2NoStaging,
                    $"Q2 the SAME source compacted IN PLACE is refused before the consent prompt, file untouched " +
                    $"(refused={!q2ip.Success} preConsent={!q2ip.NeedsAcknowledge} untouched={q2Untouched} noStaging={q2NoStaging}) [{q2ip.Error}]");
                // …and the refusal promises the new-file lane only what it delivers. "keeps its .STRINGS files" was
                // the cut arm's promise; a refusal still making it would be sending callers to a lane that does not.
                Check(!q2ip.Success
                      && (q2ip.Error?.Contains("That output is NOT localized", StringComparison.Ordinal) ?? false)
                      && !(q2ip.Error?.Contains("keeps its .STRINGS files", StringComparison.Ordinal) ?? false),
                    $"Q2 the in-place refusal tells this caller the new-file output is NOT localized [{q2ip.Error}]");
            }

            {
                var locRoot = Path.Combine(root, "loc");
                var ls = new LocalizedStringsFixture.Spec("LocSrc", new ModKey("HcCsLoc", ModType.Plugin), "LOC NAME", "LOC DESC");
                var fx = LocalizedStringsFixture.Build(locRoot, new[] { ls });
                var locStore = new UserConfigStore(Path.Combine(locRoot, "houseCARL.user.json"));
                using var locSvc = LoadOrderService.WithInstance(fx.Instance, 0, locStore);
                locSvc.Stats();

                // The fixture really is the blanking shape — see the twin arm in merge-service-guard for why this is
                // not optional: without it the arms below pass on a fixture that was never localized.
                var srcPath = Path.Combine(fx.Mods, ls.ModFolder, ls.Key.FileName.String);
                var bare = LocalizedStringsFixture.ReadBackBare(srcPath, LocalizedStringsFixture.WeaponEdid(ls));
                Check(string.IsNullOrEmpty(bare.Name) && string.IsNullOrEmpty(bare.Desc),
                    $"LOCALIZED fixture: the source read with the BARE overlay is blank (Name='{bare.Name}' Desc='{bare.Desc}')");

                var o = locSvc.CompactPlugin(ls.Key.FileName.String);
                var rb = o.Success && File.Exists(o.OutputPath)
                    ? LocalizedStringsFixture.ReadBackBare(o.OutputPath, LocalizedStringsFixture.WeaponEdid(ls))
                    : (Name: null, Desc: null);
                Check(o.Success && rb.Name == ls.Name && rb.Desc == ls.Desc,
                    $"LOCALIZED (game-Data shape) compact carries FULL+DESC into P′ (Name='{rb.Name}' Desc='{rb.Desc}'{(o.Success ? "" : ", ERR " + o.Error)})");

                // The compacted output is a bare SkyrimMod too — non-localized, strings inline. Same reasoning as the
                // merge guard's twin: it is what makes the read-back above a read of the bytes rather than of a
                // folder-adjacent lookup, and it rules out compact shipping a localized plugin with no strings.
                bool flagOk = false, noStringsFolder = false;
                if (o.Success && File.Exists(o.OutputPath))
                {
                    using var ov = SkyrimMod.CreateFromBinaryOverlay(o.OutputPath, SkyrimRelease.SkyrimSE);
                    flagOk = !ov.UsingLocalization;
                    noStringsFolder = !Directory.Exists(Path.Combine(Path.GetDirectoryName(o.OutputPath)!, "Strings"));
                }
                // Still the pinned outcome for THIS fixture, and deliberately so: its strings were relocated to
                // game-Data, which is a shape the write refuses to rewrite, so the compaction cannot keep the output
                // localized and de-localizes as it always did. The ACCEPTED shape's opposite outcome is pinned by the
                // Q2 arms below — the two are different shapes, not a before and after.
                Check(flagOk && noStringsFolder,
                    $"LOCALIZED (game-Data shape) compact output is written NON-localized with strings inline (flagClear={flagOk} noStringsFolder={noStringsFolder})");

                // NEWFILE-NOTE: the new-file lane produces the same de-localized plugin the in-place lane is refused
                // for, so it says so at the point the caller takes it. Non-fatal — the compaction still succeeds.
                // The note's claim about the OUTPUT, checked against the output itself two arms above: NOT localized,
                // and carrying no tables of its own. It no longer names a per-shape reason the strings could not be
                // carried — there is one reason now, the same for every shape, because Q2-A was cut and no
                // arrangement keeps its tables.
                bool noteOn = o.Success && (o.Note?.Contains("is NOT localized", StringComparison.Ordinal) ?? false)
                              && (o.Note?.Contains("with no .STRINGS files of its own", StringComparison.Ordinal) ?? false);
                Check(noteOn, $"NEWFILE-NOTE a localized SOURCE compacted to a new file still succeeds AND is reported " +
                              $"as de-localized with no tables of its own (success={o.Success} noted={noteOn}) [{o.Note}]");

                // The arm above IS the measurement behind the in-place refusal's remedy: the same localized source,
                // compacted to a NEW file, keeps its FULL+DESC. The refusal names that lane, so it is named against a
                // measured result rather than an assumption — and this block asserts the lane still works, which is the
                // "new-file compact is untouched" direction of the in-place refusal below.
                //
                // TARGET-LOC: the same plugin compacted IN PLACE is refused, BEFORE the consent prompt and with the
                // file untouched. Isolation, per the lesson the per-lane in-place arms taught: there is no backstop
                // here to borrow a refusal from — the compaction builds a fresh non-localized plugin, so the write's
                // own localized check structurally cannot fire on this path. Deleting the service check makes this arm
                // RED by letting the compaction run, which is the whole point.
                // REMEDY: the clause three in-place lanes append names the default lane, so the default lane is
                // measured here against this same localized plugin before any refusal points at it. An edit with no
                // in_place lands in a NEW plugin and the localized original is byte-untouched — which is exactly and
                // only what the clause claims. It deliberately does NOT assert the text came through: whether the
                // strings resolve is the bound the changelog points at, and the clause never promised it.
                var remedyBefore = File.ReadAllBytes(srcPath);
                var remedyEdit = new[] { new BulkOp { Formid = $"000A01:{ls.Key.FileName}", FieldPath = "BasicStats.Damage",
                                                     Verb = "Set", Value = "42" } };
                var rem = locSvc.ApplyEdits(remedyEdit, "LocRemedyPatch", null);
                bool remedyUntouched = File.ReadAllBytes(srcPath).AsSpan().SequenceEqual(remedyBefore);
                bool remedyWrote = rem.Success && !rem.InPlace && File.Exists(rem.OutputPath);
                Check(remedyWrote && remedyUntouched,
                    $"REMEDY the default lane writes a NEW plugin against a localized target, original untouched " +
                    $"(success={rem.Success} inPlace={rem.InPlace} originalUntouched={remedyUntouched}) [{rem.Error ?? rem.OutputPath}]");

                var srcBefore = File.ReadAllBytes(srcPath);
                var oip = locSvc.CompactPlugin(ls.Key.FileName.String, inPlace: true, acknowledge: false);
                bool untouched = File.ReadAllBytes(srcPath).AsSpan().SequenceEqual(srcBefore);
                bool noStaging = !Directory.Exists(Path.Combine(Path.GetDirectoryName(srcPath)!, ".housecarl-tmp"));
                bool preConsent = !oip.NeedsAcknowledge;
                bool named = !oip.Success && (oip.Error?.StartsWith("houseCARL did not compact", StringComparison.Ordinal) ?? false);
                Check(named && preConsent && untouched && noStaging,
                    $"TARGET-LOC compacting a LOCALIZED plugin IN PLACE is refused before the consent prompt, file untouched " +
                    $"(refused={!oip.Success} named={named} preConsent={preConsent} untouched={untouched} noStaging={noStaging}) [{oip.Error}]");
                // The refusal names the new-file output as de-localized AND says where THIS source's text actually
                // is — game-Data, not beside the plugin, which is the half a caller can act on. The Q2 fixture is the
                // same pair of claims over a different arrangement, so between them the location clause is measured
                // varying while the output clause is measured constant.
                Check(!oip.Success
                      && (oip.Error?.Contains("That output is NOT localized", StringComparison.Ordinal) ?? false)
                      && (oip.Error?.Contains(@"Data\Strings folder, not beside the plugin", StringComparison.Ordinal) ?? false)
                      && !(oip.Error?.Contains("keeps its .STRINGS files", StringComparison.Ordinal) ?? false),
                    $"TARGET-LOC the refusal tells THIS caller the new-file output is NOT localized, and where its text is [{oip.Error}]");
            }

            // NEWFILE-NOTE, other direction: the SAME lane over a NON-localized source says nothing about localization.
            // Without this the note could be unconditional and the arm above would not notice.
            {
                var plainOut = svc.CompactPlugin(selfKey.FileName.String);
                bool quiet = plainOut.Success && !(plainOut.Note?.Contains("localized", StringComparison.OrdinalIgnoreCase) ?? false);
                Check(quiet, $"NEWFILE-NOTE a NON-localized source compacts to a new file with no localization note " +
                             $"(success={plainOut.Success} quiet={quiet}) [{plainOut.Note}]");
            }

            // ---- UNREADABLE-SRC (Aaron's review, findings 4): the compact lane's two consumers of the same shape ----
            //      read, and why only one of them may collapse.
            //
            //      `srcLocalized` is `Shape != NotLocalized` — deliberately fail-CLOSED, and right, for the in-place
            //      REFUSAL: a source houseCARL could not open must not be rewritten. It also gated the report NOTE,
            //      where fail-closed is the wrong answer: a plain non-localized plugin held for the instant of the
            //      Assess and readable again by the time the compaction ran was told its text lives in .STRINGS files
            //      that do not exist, and to distrust an output that is exactly what it asked for.
            //
            //      Fixtured by holding the source FileShare.None across the call — the same repro shape the write
            //      guard's fail-open arms use. The plugin is NOT localized here on purpose: that is what makes the
            //      note's claim provably false rather than merely unestablished.
            {
                var urRoot = Path.Combine(root, "unreadable");
                var ur = new LocalizedStringsFixture.Spec("UrSrc", new ModKey("HcCsUr", ModType.Plugin), "UR NAME", "UR DESC",
                                                          Localized: false);
                var fx = LocalizedStringsFixture.Build(urRoot, new[] { ur });
                var urStore = new UserConfigStore(Path.Combine(urRoot, "houseCARL.user.json"));
                using var urSvc = LoadOrderService.WithInstance(fx.Instance, 0, urStore);
                urSvc.Stats();

                var urPath = Path.Combine(fx.Mods, ur.ModFolder, ur.Key.FileName.String);
                var urBefore = File.ReadAllBytes(urPath);

                // The baseline, unheld: this plugin is not localized, so nothing about localization may be said of it
                // in either lane. Without this the arms below could pass on a fixture that really was localized.
                var baseline = LocalizedStrings.Assess(urPath, fx.Data);
                Check(baseline.Shape == LocalizedShape.NotLocalized,
                    $"UNREADABLE-SRC fixture: unheld, the source is a plain NON-localized plugin (shape={baseline.Shape})");

                WritePatchBuilder.CompactOutcome heldNewFile, heldInPlace;
                LocalizedShape heldShape;
                using (var hold = new FileStream(urPath, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    heldShape = LocalizedStrings.Assess(urPath, fx.Data).Shape;
                    heldNewFile = urSvc.CompactPlugin(ur.Key.FileName.String);
                    heldInPlace = urSvc.CompactPlugin(ur.Key.FileName.String, inPlace: true, acknowledge: true);
                }
                Check(heldShape == LocalizedShape.Unreadable,
                    $"UNREADABLE-SRC fixture: held FileShare.None, the same source classifies Unreadable (shape={heldShape})");

                // THE REMEDY THAT WOULD DEAD-END, measured before the sentence declines to name it. The in-place
                // refusal used to point at the new-file lane; this is the run that shows that lane fails for the
                // underlying reason nobody had been told about — which is why the Unreadable arm names the read
                // failure instead. #374's shape, on this arm.
                Check(!heldNewFile.Success,
                    $"UNREADABLE-SRC probe: the NEW-FILE lane over the same held source ALSO fails, so it is not a remedy " +
                    $"(success={heldNewFile.Success}) [{heldNewFile.Error}]");

                // THE REPORT NOTE IS NOT ARMED FROM HERE, AND THIS SAYS SO RATHER THAN LOOKING LIKE IT IS.
                //
                // The note's gate moved from `srcLocalized` (fail-closed, `Shape != NotLocalized`) to
                // `ConfirmedLocalized` (was the flag actually READ and set). Driving that difference end to end needs
                // a source that classifies Unreadable at the Assess and then compacts SUCCESSFULLY — and it cannot be
                // built: `PluginIsLocalized` and `TryReadOriginatingKeys` make the identical
                // `SkyrimMod.CreateFromBinaryOverlay` call on the same path a few lines apart, so a file the first
                // cannot open the second cannot either. The arm above measures exactly that. What is left between them
                // is a sub-millisecond race inside one `_writeGate` hold — real (it is the scenario the finding
                // names), and not something a fixture can reproduce honestly.
                //
                // So an arm asserting "the held source produced no localization note" would pass because the
                // compaction FAILED, not because the gate is right — vacuous in the way a guard is worst at showing.
                // It is armed at the predicate instead, in localized-write-guard's ConfirmedLocalized walk, and this
                // block asserts only what it actually drove.
                Check(heldNewFile.Note is null,
                    $"UNREADABLE-SRC (not a gate arm) the failed new-file run carries no report at all, which is why the " +
                    $"note's gate cannot be measured from here [{heldNewFile.Note ?? "<no note>"}]");

                // THE IN-PLACE REFUSAL. Its own words: no claim that the plugin is localized, no .STRINGS clauses, and
                // the remedy is what actually failed rather than a lane that fails the same way.
                var ipErr = heldInPlace.Error ?? "";
                Check(!heldInPlace.Success
                      && !ipErr.Contains("translated plugin", StringComparison.Ordinal)
                      && !ipErr.Contains("Re-run without in_place", StringComparison.Ordinal),
                    $"UNREADABLE-SRC the in-place refusal claims no .STRINGS files and points at no dead-end lane [{ipErr}]");
                Check(ipErr.Contains("could not read it to see whether it is localized", StringComparison.Ordinal)
                      && ipErr.Contains("has the file open", StringComparison.Ordinal),
                    $"UNREADABLE-SRC …and says what actually failed, with the remedy for it [{ipErr}]");
                Check(File.ReadAllBytes(urPath).AsSpan().SequenceEqual(urBefore),
                    "UNREADABLE-SRC and the held source is byte-identical afterwards");

                // OTHER DIRECTION, same fixture one lock apart: unheld, the plugin is readable and non-localized, so
                // the in-place lane proceeds and the note stays silent. Without this the arms above pass on a service
                // that refuses everything.
                var freeOut = urSvc.CompactPlugin(ur.Key.FileName.String);
                Check(freeOut.Success && !(freeOut.Note?.Contains("localized", StringComparison.OrdinalIgnoreCase) ?? false),
                    $"UNREADABLE-SRC unheld, the same source compacts and still says nothing about localization " +
                    $"(success={freeOut.Success}) [{freeOut.Note ?? freeOut.Error}]");
            }

            // ---- REPOINT-LOC: a repoint that would REWRITE a localized external referencer is refused UP FRONT ----
            //      houseCARL cannot re-serialize a localized plugin without corrupting its text, and the referencer
            //      rewrites happen only AFTER the target is already compacted on disk. So the refusal has to come
            //      before the compaction starts, and the arm's real assertion is that NOTHING was written — an arm
            //      that only checked for an error would pass just as well on a refusal issued halfway through.
            {
                var rlRoot = Path.Combine(root, "reploc");
                // The TARGET is deliberately non-localized: the in-place lane refuses a localized target outright, so a
                // localized one here would be refused by THAT check and this arm would never reach the referencer
                // pre-flight it is named for. Only the referencer is localized.
                var tgt = new LocalizedStringsFixture.Spec("RlTgt", new ModKey("HcCsRlTgt", ModType.Plugin), "TGT NAME", "TGT DESC",
                                                          Localized: false);
                var rf = new LocalizedStringsFixture.Spec("RlRef", new ModKey("HcCsRlRef", ModType.Plugin), "REF NAME", "REF DESC",
                                                          LinksTo: LocalizedStringsFixture.WeaponKey(tgt));
                var fx = LocalizedStringsFixture.Build(rlRoot, new[] { tgt, rf });
                var rlStore = new UserConfigStore(Path.Combine(rlRoot, "houseCARL.user.json"));
                using var rlSvc = LoadOrderService.WithInstance(fx.Instance, 0, rlStore);
                rlSvc.Stats();

                var tgtPath = Path.Combine(fx.Mods, tgt.ModFolder, tgt.Key.FileName.String);
                var refPath = Path.Combine(fx.Mods, rf.ModFolder, rf.Key.FileName.String);
                var tgtBefore = File.ReadAllBytes(tgtPath);
                var refBefore = File.ReadAllBytes(refPath);

                var o = rlSvc.CompactPlugin(tgt.Key.FileName.String, inPlace: true, repointExternals: true, acknowledge: true);
                bool named = !o.Success && (o.Error?.Contains("LOCALIZED", StringComparison.Ordinal) ?? false)
                                        && (o.Error?.Contains(rf.Key.FileName.String, StringComparison.OrdinalIgnoreCase) ?? false);
                bool tgtUntouched = File.ReadAllBytes(tgtPath).AsSpan().SequenceEqual(tgtBefore);
                bool refUntouched = File.ReadAllBytes(refPath).AsSpan().SequenceEqual(refBefore);
                bool noStaging = !Directory.Exists(Path.Combine(Path.GetDirectoryName(tgtPath)!, ".housecarl-tmp"))
                                 && !Directory.Exists(Path.Combine(Path.GetDirectoryName(refPath)!, ".housecarl-tmp"));
                Check(named && tgtUntouched && refUntouched && noStaging,
                    $"REPOINT-LOC refused up front, naming the localized referencer; target AND referencer untouched " +
                    $"(named={named} targetUntouched={tgtUntouched} refUntouched={refUntouched} noStaging={noStaging}) [{o.Error}]");

                // The identify pass really did see the referencer — otherwise the refusal above could be firing on an
                // empty referencer set and the arm would pass on a fixture with no external reference in it at all.
                var o2 = rlSvc.CompactPlugin(tgt.Key.FileName.String);
                Check(!o2.Success && (o2.Error?.Contains(rf.Key.FileName.String, StringComparison.OrdinalIgnoreCase) ?? false),
                    $"REPOINT-LOC fixture: the referencer IS an external referencer of the target (plain compact names it) [{o2.Error}]");

                // #374: that first refusal's remedy is "re-run with repoint_externals". With a referencer houseCARL
                // cannot rewrite, following it meets a second refusal — so the first one has to say so instead. The
                // OTHER direction of this conditional is REPOINT-PLAIN's arm below, where the ordinary remedy stands.
                bool warns = (o2.Error?.Contains("will NOT work here", StringComparison.Ordinal) ?? false)
                             && (o2.Error?.Contains(rf.Key.FileName.String, StringComparison.OrdinalIgnoreCase) ?? false)
                             && !(o2.Error?.Contains("Re-run with repoint_externals=true AND in_place=true", StringComparison.Ordinal) ?? false);
                Check(warns,
                    $"REPOINT-LOC the referencer refusal does NOT send the caller down a repoint that would refuse (#374) [{o2.Error}]");

                // The refusal must come BEFORE the consent gate. Without acknowledge the caller would otherwise get
                // "CONFIRM in-place rewrite (your ORIGINAL file(s) will be rewritten — no houseCARL backup or undo)"
                // and be asked to accept an irreversible trade-off for a run that was never going to write anything.
                var oNoAck = rlSvc.CompactPlugin(tgt.Key.FileName.String, inPlace: true, repointExternals: true);
                Check(!oNoAck.Success && !oNoAck.NeedsAcknowledge
                      && (oNoAck.Error?.Contains("LOCALIZED", StringComparison.Ordinal) ?? false),
                    $"REPOINT-LOC refuses BEFORE the consent gate (no CONFIRM prompt for a rewrite that cannot happen) " +
                    $"(refused={!oNoAck.Success} needsAck={oNoAck.NeedsAcknowledge}) [{oNoAck.Error}]");

                // The BACKSTOP, driven directly: the pre-flight above is what a caller normally meets, but the repoint
                // itself must refuse a localized target on its own — otherwise the pre-flight is the only thing standing
                // between a localized referencer and a corrupting rewrite, and any path that reaches the repoint another
                // way is unprotected. Straight at RemapEngine, with the service's refusal bypassed entirely.
                using (var rr = LoadOrderResolver.Build(new[]
                {
                    Path.Combine(fx.Data, "Skyrim.esm"),
                    tgtPath,
                    refPath,
                }))
                {
                    var refBefore2 = File.ReadAllBytes(refPath);
                    var rep = RemapEngine.RepointInPlace(rr, rf.Key.FileName.String,
                        new Dictionary<FormKey, FormKey> { [LocalizedStringsFixture.WeaponKey(tgt)] = new FormKey(tgt.Key, 0x900) });
                    bool untouched = File.ReadAllBytes(refPath).AsSpan().SequenceEqual(refBefore2);
                    bool verbatim = !rep.Success && (rep.Error?.StartsWith("houseCARL did not write", StringComparison.Ordinal) ?? false);
                    Check(verbatim && untouched,
                        $"REPOINT-LOC backstop: RepointInPlace itself refuses the localized referencer verbatim, file untouched " +
                        $"(refused={!rep.Success} verbatim={verbatim} untouched={untouched}) [{rep.Error}]");
                }
            }

            // ---- REPOINT-MIXED (Aaron's review, findings 5+6): LocalizedAmong's hits are NOT homogeneous ----
            //      Failing closed on a referencer houseCARL could not read is this branch's own change and it is
            //      right — but both refusals then rendered the whole hit list as "is localized" / "N of them are
            //      flagged LOCALIZED", and only the FIRST hit got an attributed reason. A referencer briefly held by
            //      xEdit was therefore reported as a localized plugin, and if it was not hit zero its actual condition
            //      appeared nowhere in the message: the modder goes looking for .STRINGS files instead of for the file
            //      they cannot open.
            //
            //      Two referencers of one target, one per class: RmRefLoc really is localized, RmRefLock is a plain
            //      plugin held FileShare.None across the call. Both must block; neither may be described as the other.
            {
                var rmRoot = Path.Combine(root, "repmixed");
                var tgt = new LocalizedStringsFixture.Spec("RmTgt", new ModKey("HcCsRmTgt", ModType.Plugin), "TGT NAME", "TGT DESC",
                                                          Localized: false);
                var refLoc = new LocalizedStringsFixture.Spec("RmRefLoc", new ModKey("HcCsRmRefLoc", ModType.Plugin), "L NAME", "L DESC",
                                                             LinksTo: LocalizedStringsFixture.WeaponKey(tgt));
                var refLock = new LocalizedStringsFixture.Spec("RmRefLock", new ModKey("HcCsRmRefLock", ModType.Plugin), "K NAME", "K DESC",
                                                              LinksTo: LocalizedStringsFixture.WeaponKey(tgt), Localized: false);
                var fx = LocalizedStringsFixture.Build(rmRoot, new[] { tgt, refLoc, refLock });
                var rmStore = new UserConfigStore(Path.Combine(rmRoot, "houseCARL.user.json"));
                using var rmSvc = LoadOrderService.WithInstance(fx.Instance, 0, rmStore);
                rmSvc.Stats();

                var tgtPath = Path.Combine(fx.Mods, tgt.ModFolder, tgt.Key.FileName.String);
                var locPath = Path.Combine(fx.Mods, refLoc.ModFolder, refLoc.Key.FileName.String);
                var lockPath = Path.Combine(fx.Mods, refLock.ModFolder, refLock.Key.FileName.String);

                Check(LocalizedStrings.Assess(locPath, fx.Data).Shape != LocalizedShape.NotLocalized
                      && LocalizedStrings.Assess(lockPath, fx.Data).Shape == LocalizedShape.NotLocalized,
                    "REPOINT-MIXED fixture: one referencer is localized, the other is a plain plugin (unheld)");

                // WHY THE MIXED LIST IS NOT DRIVEN END TO END, measured rather than assumed. Hold the plain referencer
                // FileShare.None and the identify pass cannot read it either — so it drops OUT of ExternalPlugins
                // entirely and LocalizedAmong is never asked about it. A referencer houseCARL cannot open therefore
                // reaches the Unreadable class only by becoming unreadable BETWEEN the identify pass and this
                // pre-flight, which is a race inside one _writeGate hold and not something a fixture can reproduce
                // honestly. That is exactly why LocalizedAmong fails closed on it — and why the RENDER is armed
                // directly below, on the real renderer, rather than through a service path that cannot produce the
                // input.
                WritePatchBuilder.CompactOutcome held;
                using (var hold = new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.None))
                    held = rmSvc.CompactPlugin(tgt.Key.FileName.String);
                Check(!held.Success && (held.Error?.Contains("Referencers: HcCsRmRefLoc.esp.", StringComparison.Ordinal) ?? false),
                    $"REPOINT-MIXED (not a render arm) a held referencer never reaches the pre-flight — the identify pass drops it first [{held.Error}]");

                // END TO END, one class: the localized referencer alone. The census counts it, names it, attributes
                // its reason — and carries NO second clause, which is the other direction of the split.
                var onlyLoc = rmSvc.CompactPlugin(tgt.Key.FileName.String).Error ?? "";
                Check(onlyLoc.Contains("1 flagged LOCALIZED (HcCsRmRefLoc.esp)", StringComparison.Ordinal)
                      && onlyLoc.Contains("Where HcCsRmRefLoc.esp's text is:", StringComparison.Ordinal)
                      && !onlyLoc.Contains("houseCARL could not read (", StringComparison.Ordinal)
                      && !onlyLoc.Contains("is blocked:", StringComparison.Ordinal),
                    $"REPOINT-MIXED end to end, a localized-only hit list gets one class and no unreadable clause [{onlyLoc}]");

                // THE RENDER ITSELF, on the REAL renderers (internal to housecarl-mcp, reachable via
                // InternalsVisibleTo) with the mixed hit list the service cannot be made to produce. This is the fold:
                // LocalizedAmong now returns the SHAPE with each hit, and both refusals split on it.
                var mixed = new (string Plugin, LocalizedShape Shape, string Why)[]
                {
                    ("A.esp", LocalizedShape.GameDataOnly, "A's text is in game-Data."),
                    ("B.esp", LocalizedShape.Unreadable, "houseCARL could not read the file at that path to see where its text lives."),
                    ("C.esp", LocalizedShape.LooseComplete, "C's text is beside it."),
                };
                var census = LoadOrderService.BlockedReferencerCensus(mixed);
                var reasons = LoadOrderService.BlockedReferencerReasons(mixed);

                Check(census.Contains("2 flagged LOCALIZED (A.esp, C.esp)", StringComparison.Ordinal)
                      && census.Contains("1 houseCARL could not read (B.esp)", StringComparison.Ordinal),
                    $"REPOINT-MIXED render: the census counts and names per class [{census}]");
                Check(!census.Contains("3 flagged LOCALIZED", StringComparison.Ordinal)
                      && !census.Contains("B.esp, C.esp", StringComparison.Ordinal),
                    $"REPOINT-MIXED render: the unreadable hit is NOT in the localized count or its name list [{census}]");

                // The defect finding 6 names precisely: only localized[0] was attributed, so a hit in the other class
                // — B.esp here, which is not even hit zero — had its actual condition appear nowhere in the message.
                Check(reasons.Contains("Where A.esp's text is:", StringComparison.Ordinal)
                      && reasons.Contains("Why B.esp is blocked:", StringComparison.Ordinal)
                      && reasons.Contains("could not read the file at that path", StringComparison.Ordinal),
                    $"REPOINT-MIXED render: the FIRST of EACH class is attributed, with its own lead-in [{reasons}]");
                Check(reasons.Contains("The other 1 localized referencer(s)", StringComparison.Ordinal)
                      && !reasons.Contains("unreadable referencer(s)", StringComparison.Ordinal),
                    $"REPOINT-MIXED render: the per-class tails count their own class, and a class of one gets none [{reasons}]");

                // Single-class inputs, both ways, so neither clause is unconditional.
                var locOnly = LoadOrderService.BlockedReferencerCensus(mixed.Where(m => m.Shape != LocalizedShape.Unreadable).ToArray());
                var unreadOnly = LoadOrderService.BlockedReferencerCensus(mixed.Where(m => m.Shape == LocalizedShape.Unreadable).ToArray());
                Check(!locOnly.Contains("could not read", StringComparison.Ordinal)
                      && !unreadOnly.Contains("flagged LOCALIZED", StringComparison.Ordinal)
                      && unreadOnly.Contains("1 houseCARL could not read (B.esp)", StringComparison.Ordinal),
                    $"REPOINT-MIXED render: a class with no hits contributes nothing [{locOnly}] [{unreadOnly}]");
            }

            // ---- REPOINT-BESIDE: a referencer in the arrangement houseCARL classifies most confidently — a complete
            //      loose set BESIDE it — blocks the repoint too. This is the write that was briefly permitted and then
            //      cut (2026-08-26): the repoint would have rewritten a localized plugin the caller never named,
            //      plugin and tables together, on their own file. The arm exists so that permission cannot return
            //      unnoticed — and it checks the referencer's TABLES byte-for-byte, not only the plugin, because what
            //      that write did was replace both.
            {
                var rbRoot = Path.Combine(root, "repbeside");
                var tgt = new LocalizedStringsFixture.Spec("RbTgt", new ModKey("HcCsRbTgt", ModType.Plugin), "TGT NAME", "TGT DESC",
                                                          Localized: false);
                var rf = new LocalizedStringsFixture.Spec("RbRef", new ModKey("HcCsRbRef", ModType.Plugin), "REF NAME", "REF DESC",
                                                          LinksTo: LocalizedStringsFixture.WeaponKey(tgt),
                                                          StringsBeside: true, SecondLanguage: "French");
                var fx = LocalizedStringsFixture.Build(rbRoot, new[] { tgt, rf });
                var rbStore = new UserConfigStore(Path.Combine(rbRoot, "houseCARL.user.json"));
                using var rbSvc = LoadOrderService.WithInstance(fx.Instance, 0, rbStore);
                rbSvc.Stats();

                var tgtPath = Path.Combine(fx.Mods, tgt.ModFolder, tgt.Key.FileName.String);
                var refPath = Path.Combine(fx.Mods, rf.ModFolder, rf.Key.FileName.String);

                var refShape = LocalizedStrings.Assess(refPath, fx.Data);
                Check(refShape.Shape == LocalizedShape.LooseComplete && refShape.Languages.Count == 2,
                    $"REPOINT-BESIDE fixture: the referencer really is the complete-loose-set arrangement (shape={refShape.Shape} langs={refShape.Languages.Count})");

                var tgtBefore = File.ReadAllBytes(tgtPath);
                var refBefore = File.ReadAllBytes(refPath);
                var refTablesBefore = LocalizedStrings.OwnTableFiles(refPath)
                    .ToDictionary(p => Path.GetFileName(p), File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);

                var o = rbSvc.CompactPlugin(tgt.Key.FileName.String, inPlace: true, repointExternals: true, acknowledge: true);
                bool named = !o.Success && (o.Error?.Contains(rf.Key.FileName.String, StringComparison.OrdinalIgnoreCase) ?? false);
                bool tgtUntouched = File.ReadAllBytes(tgtPath).AsSpan().SequenceEqual(tgtBefore);
                bool refUntouched = File.ReadAllBytes(refPath).AsSpan().SequenceEqual(refBefore);
                var refTablesAfter = LocalizedStrings.OwnTableFiles(refPath)
                    .ToDictionary(p => Path.GetFileName(p), File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);
                bool tablesUntouched = refTablesBefore.Count == refTablesAfter.Count
                    && refTablesBefore.All(kv => refTablesAfter.TryGetValue(kv.Key, out var b) && b.AsSpan().SequenceEqual(kv.Value));
                Check(named && tgtUntouched && refUntouched && tablesUntouched,
                    $"REPOINT-BESIDE a complete-loose-set referencer blocks the repoint; target, referencer AND its tables untouched " +
                    $"(named={named} tgt={tgtUntouched} ref={refUntouched} tables={tablesUntouched}/{refTablesBefore.Count}) [{o.Error}]");
            }

            // ---- REPOINT-PLAIN: a NON-localized target with a NON-localized referencer still compacts and repoints ----
            //      The other direction for the REFERENCER pre-flight: nothing localized anywhere, so neither the
            //      referencer check nor the target check may fire and the full opt-in path must work end to end.
            //      The target is non-localized here BECAUSE the in-place lane now refuses a localized one — this arm
            //      would otherwise be measuring that refusal instead of the repoint it is named for.
            {
                var rpRoot = Path.Combine(root, "repplain");
                var tgt = new LocalizedStringsFixture.Spec("RpTgt", new ModKey("HcCsRpTgt", ModType.Plugin), "TGT NAME", "TGT DESC",
                                                          Localized: false);
                var rf = new LocalizedStringsFixture.Spec("RpRef", new ModKey("HcCsRpRef", ModType.Plugin), "REF NAME", "REF DESC",
                                                          LinksTo: LocalizedStringsFixture.WeaponKey(tgt), Localized: false);
                var fx = LocalizedStringsFixture.Build(rpRoot, new[] { tgt, rf });
                var tgtPath = Path.Combine(fx.Mods, tgt.ModFolder, tgt.Key.FileName.String);
                var refPath = Path.Combine(fx.Mods, rf.ModFolder, rf.Key.FileName.String);

                var rpStore = new UserConfigStore(Path.Combine(rpRoot, "houseCARL.user.json"));
                using var rpSvc = LoadOrderService.WithInstance(fx.Instance, 0, rpStore);
                rpSvc.Stats();

                // The other direction of #374's conditional: with nothing blocking the repoint, the original remedy is
                // still what the caller is given. An arm that only checked the warning would let the warning fire on
                // every referencer and still pass.
                var oPlainFirst = rpSvc.CompactPlugin(tgt.Key.FileName.String);
                Check(!oPlainFirst.Success
                      && (oPlainFirst.Error?.Contains("Re-run with repoint_externals=true AND in_place=true", StringComparison.Ordinal) ?? false)
                      && !(oPlainFirst.Error?.Contains("will NOT work here", StringComparison.Ordinal) ?? false),
                    $"REPOINT-PLAIN a referencer houseCARL CAN rewrite still gets the ordinary repoint remedy (#374, other direction) [{oPlainFirst.Error}]");

                var o = rpSvc.CompactPlugin(tgt.Key.FileName.String, inPlace: true, repointExternals: true, acknowledge: true);
                FormKey? tgtWeapAfter = null, refLinkAfter = null;
                if (o.Success)
                {
                    using (var tb = SkyrimMod.CreateFromBinaryOverlay(tgtPath, SkyrimRelease.SkyrimSE))
                        tgtWeapAfter = tb.Weapons.FirstOrDefault(w => w.EditorID == LocalizedStringsFixture.WeaponEdid(tgt))?.FormKey;
                    using (var rb = SkyrimMod.CreateFromBinaryOverlay(refPath, SkyrimRelease.SkyrimSE))
                        refLinkAfter = rb.FormLists.First().Items.FirstOrDefault()?.FormKey;
                }
                Check(o.Success && o.InPlace && tgtWeapAfter is not null && refLinkAfter == tgtWeapAfter
                      && o.Repointed.Count == 1 && o.Repointed[0].Success,
                    $"REPOINT-PLAIN nothing localized: compacts in place + repoints (weapon -> {tgtWeapAfter}, ref -> {refLinkAfter}, " +
                    $"repointed {o.Repointed.Count(x => x.Success)}/{o.Repointed.Count}){(o.Success ? "" : "; ERR " + o.Error)}");
            }
        }
        finally { CorpusRulebook.CorpusPath = priorCorpusPath; try { Directory.Delete(root, true); } catch { } }

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

    /// <summary>Read one weapon's FULL from a written plugin in a specific language, resolving strictly from the
    /// tables beside THAT plugin — the read a localized output has to satisfy on its own.</summary>
    static string? ReadLang(string pluginPath, string edid, Language lang)
    {
        using var ov = SkyrimMod.CreateFromBinaryOverlay(pluginPath, SkyrimRelease.SkyrimSE,
            BinaryReadParameters.Default with { StringsParam = new StringsReadParameters { TargetLanguage = lang } });
        var w = ov.Weapons.FirstOrDefault(x => x.EditorID == edid);
        return w?.Name?.String;
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
