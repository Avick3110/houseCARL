using System.Reflection;
using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// #342 stage 1 — GUARD for the owned-child content annotation, at the UNIT level, over a synthetic MO2 instance.
///
/// The bug: a parent's child records — placed references under a cell, INFOs under a topic, cells under a
/// worldspace — are declared per plugin and assembled by the game from every plugin that declares them. An
/// override that touches the parent for an unrelated reason carries none and deletes none, so reading the winner
/// reports an empty cell the game fills. Reproduced on a real order at <c>008EB5:Skyrim.esm</c>: winner
/// Temporary 0, <c>Skyrim.esm</c>'s own body 201.
///
/// <para>This guard used to drive <c>housecarl_read_record</c> end to end, and most of its arms were about a
/// RENDERED response. The 1.x cut deleted that tool. The rendered arms for the CHEAP tier are tests against
/// <c>housecarl_records</c> in <c>src/housecarl-mcp-tests</c>; the rendered arms for the PRECISE tier
/// (<c>conflict_tree=true</c>) have no 2.0 lever and are gone. What is left here is the layer underneath, which
/// no tool change touches:</para>
///   REACH-NOT-ELEMENT — <c>DeclaresChild</c> answers "does this body declare a child RECORD", not "does it hold a
///                  top-level element": a worldspace holding empty block scaffolding declares no cells, and its
///                  real cells sit two container levels down.
///   SHAPE        — the singular-vs-collection classifier answers off the TYPE, before any body is read.
///   NO-CHILDREN  — a weapon's child-bearing field set is EMPTY, so no read of one can annotate anything.
///   BY CONSTRUCTION — the getter → concrete hop the field set rides resolves for EVERY child-bearing type.
///   UNREADABLE   — a declarer that stops being openable leaves the ORDER, and the load-order layer NAMES the
///                  failure rather than swallowing it; and <c>DeclaresChild</c> keeps "I could not look" (null)
///                  distinct from "nothing there" (false).
///   SENTENCE     — the content net over <see cref="ReadSentences"/>, consts AND the composed per-field note.
///
/// Run: <c>dotnet run --project src/housecarl-generator -- owned-child-content-guard</c>
/// </summary>
public static class OwnedChildContentProbe
{
    static int _pass, _fail;

    [CiProbe("owned-child-content-guard")]
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("################  #342 stage 1 — owned-child content annotation  ################");
        Console.WriteLine();
        _pass = _fail = 0;

        var root = Path.Combine(Path.GetTempPath(), "hc-owned-child-" + Guid.NewGuid().ToString("N"));
        try
        {
            string instance = Path.Combine(root, "instance");
            string profiles = Path.Combine(instance, "profiles", "Default");
            string mods = Path.Combine(instance, "mods");
            foreach (var d in new[] { profiles, mods, Path.Combine(root, "game", "Data") }) Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
                "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
                + Path.Combine(root, "game").Replace(@"\", @"\\") + ")\r\n");

            // ---- BASE: the plugin that declares the children (Skyrim.esm's role in the report) ----
            var baseKey = new ModKey("HcOcBase", ModType.Master);
            var cellA = new FormKey(baseKey, 0xC01);      // the false-empty cell
            var cellB = new FormKey(baseKey, 0xC02);      // DISJOINT: both bodies declare, no overlap
            var cellC = new FormKey(baseKey, 0xC03);      // declared by exactly one plugin
            var cellD = new FormKey(baseKey, 0xC04);      // EQUAL: both declare one reference
            var cellE = new FormKey(baseKey, 0xC05);      // SELF: only the winner declares
            var topic = new FormKey(baseKey, 0xD01);
            var weapon = new FormKey(baseKey, 0xE01);
            var wrld = new FormKey(baseKey, 0xF01);       // WRLD-SCAFFOLD: real cells vs empty blocks
            var baseDir = Path.Combine(mods, "BaseMod"); Directory.CreateDirectory(baseDir);
            {
                var m = new SkyrimMod(baseKey, SkyrimRelease.SkyrimSE);
                var a = new Cell(cellA, SkyrimRelease.SkyrimSE) { EditorID = "HcOcCellA", Flags = Cell.Flag.IsInteriorCell };
                for (int i = 0; i < 3; i++)
                    a.Temporary.Add(new PlacedObject(new FormKey(baseKey, (uint)(0xC10 + i)), SkyrimRelease.SkyrimSE) { EditorID = $"HcOcTemp{i}" });
                a.Persistent.Add(new PlacedObject(new FormKey(baseKey, 0xC1A), SkyrimRelease.SkyrimSE) { EditorID = "HcOcPers0" });
                a.Landscape = new Landscape(new FormKey(baseKey, 0xC1B), SkyrimRelease.SkyrimSE) { EditorID = "HcOcLand" };
                FileInterior(m, a);

                // DISJOINT: base declares 1, the winner will declare 4 OTHER references — the live set is 5, and a
                // count comparison ("does anyone declare MORE than this body") said nothing at all here.
                var b = new Cell(cellB, SkyrimRelease.SkyrimSE) { EditorID = "HcOcCellB", Flags = Cell.Flag.IsInteriorCell };
                b.Temporary.Add(new PlacedObject(new FormKey(baseKey, 0xC20), SkyrimRelease.SkyrimSE) { EditorID = "HcOcBTemp0" });
                FileInterior(m, b);

                var c = new Cell(cellC, SkyrimRelease.SkyrimSE) { EditorID = "HcOcCellC", Flags = Cell.Flag.IsInteriorCell };
                c.Temporary.Add(new PlacedObject(new FormKey(baseKey, 0xC30), SkyrimRelease.SkyrimSE) { EditorID = "HcOcCTemp0" });
                FileInterior(m, c);

                // EQUAL: one reference each side — the count comparison's blind spot at parity.
                var dCell = new Cell(cellD, SkyrimRelease.SkyrimSE) { EditorID = "HcOcCellD", Flags = Cell.Flag.IsInteriorCell };
                dCell.Temporary.Add(new PlacedObject(new FormKey(baseKey, 0xC40), SkyrimRelease.SkyrimSE) { EditorID = "HcOcDTemp0" });
                FileInterior(m, dCell);

                // SELF: the base touches the cell declaring NOTHING; only the winner declares.
                FileInterior(m, new Cell(cellE, SkyrimRelease.SkyrimSE) { EditorID = "HcOcCellE", Flags = Cell.Flag.IsInteriorCell });

                var t = new DialogTopic(topic, SkyrimRelease.SkyrimSE) { EditorID = "HcOcTopic" };
                for (int i = 0; i < 2; i++)
                {
                    var info = new DialogResponses(new FormKey(baseKey, (uint)(0xD10 + i)), SkyrimRelease.SkyrimSE);
                    info.Responses.Add(new DialogResponse { Text = $"base line {i}" });
                    t.Responses.Add(info);
                }
                m.DialogTopics.Add(t);

                m.Weapons.Add(new Weapon(weapon, SkyrimRelease.SkyrimSE)
                    { EditorID = "HcOcWeap", BasicStats = new WeaponBasicStats { Damage = 5 } });

                // WRLD: ONE block holding THREE cells — real children, two container levels down.
                var ws = new Worldspace(wrld, SkyrimRelease.SkyrimSE) { EditorID = "HcOcWrld" };
                var blk = new WorldspaceBlock { BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellBlock };
                var sub = new WorldspaceSubBlock { BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellSubBlock };
                for (int i = 0; i < 3; i++)
                    sub.Items.Add(new Cell(new FormKey(baseKey, (uint)(0xF10 + i)), SkyrimRelease.SkyrimSE) { EditorID = $"HcOcWsCell{i}" });
                blk.Items.Add(sub); ws.SubCells.Add(blk);
                m.Worldspaces.Add(ws);

                m.BeginWrite.ToPath(Path.Combine(baseDir, baseKey.FileName.String)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
            }

            // ---- MID: a second toucher of cell A that also declares no children (so "other plugins" is a list,
            //      not a synonym for "the master") ----
            var midKey = new ModKey("HcOcMid", ModType.Plugin);
            var midDir = Path.Combine(mods, "MidMod"); Directory.CreateDirectory(midDir);
            {
                using var baseOv = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(baseDir, baseKey.FileName.String), SkyrimRelease.SkyrimSE);
                var m = new SkyrimMod(midKey, SkyrimRelease.SkyrimSE);
                FileInterior(m, new Cell(cellA, SkyrimRelease.SkyrimSE) { EditorID = "HcOcCellA", Flags = Cell.Flag.IsInteriorCell });
                m.Weapons.GetOrAddAsOverride(baseOv.Weapons.First(w => w.FormKey == weapon)).BasicStats!.Damage = 7;
                m.BeginWrite.ToPath(Path.Combine(midDir, midKey.FileName.String)).WithLoadOrder(new ISkyrimModGetter[] { baseOv }).Write();
            }

            // ---- TOP (the winner): the Occlusion.esp shape on cell A. On the worldspace it declares TWO blocks
            //      holding ZERO cells — the scaffolding an element-level answer would call a declarer. ----
            var topKey = new ModKey("HcOcTop", ModType.Plugin);
            var topDir = Path.Combine(mods, "TopMod"); Directory.CreateDirectory(topDir);
            {
                using var baseOv = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(baseDir, baseKey.FileName.String), SkyrimRelease.SkyrimSE);
                var m = new SkyrimMod(topKey, SkyrimRelease.SkyrimSE);
                FileInterior(m, new Cell(cellA, SkyrimRelease.SkyrimSE) { EditorID = "HcOcCellA", Flags = Cell.Flag.IsInteriorCell });

                var b = new Cell(cellB, SkyrimRelease.SkyrimSE) { EditorID = "HcOcCellB", Flags = Cell.Flag.IsInteriorCell };
                for (int i = 0; i < 4; i++)
                    b.Temporary.Add(new PlacedObject(new FormKey(topKey, (uint)(0xB10 + i)), SkyrimRelease.SkyrimSE) { EditorID = $"HcOcTopTemp{i}" });
                FileInterior(m, b);

                var dCell = new Cell(cellD, SkyrimRelease.SkyrimSE) { EditorID = "HcOcCellD", Flags = Cell.Flag.IsInteriorCell };
                dCell.Temporary.Add(new PlacedObject(new FormKey(topKey, 0xB40), SkyrimRelease.SkyrimSE) { EditorID = "HcOcTopDTemp0" });
                FileInterior(m, dCell);

                var e = new Cell(cellE, SkyrimRelease.SkyrimSE) { EditorID = "HcOcCellE", Flags = Cell.Flag.IsInteriorCell };
                e.Temporary.Add(new PlacedObject(new FormKey(topKey, 0xB50), SkyrimRelease.SkyrimSE) { EditorID = "HcOcTopETemp0" });
                FileInterior(m, e);

                var t = new DialogTopic(topic, SkyrimRelease.SkyrimSE) { EditorID = "HcOcTopic" };
                var only = new DialogResponses(new FormKey(baseKey, 0xD10), SkyrimRelease.SkyrimSE);
                only.Responses.Add(new DialogResponse { Text = "patched line 0" });
                t.Responses.Add(only);
                m.DialogTopics.Add(t);

                var ws = new Worldspace(wrld, SkyrimRelease.SkyrimSE) { EditorID = "HcOcWrld" };
                for (int bx = 0; bx < 2; bx++)   // TWO blocks, each with a sub-block, holding NO cells at all
                {
                    var eb = new WorldspaceBlock { BlockNumberX = (short)bx, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellBlock };
                    eb.Items.Add(new WorldspaceSubBlock { BlockNumberX = (short)bx, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellSubBlock });
                    ws.SubCells.Add(eb);
                }
                m.Worldspaces.Add(ws);

                m.Weapons.GetOrAddAsOverride(baseOv.Weapons.First(w => w.FormKey == weapon)).BasicStats!.Damage = 9;
                m.BeginWrite.ToPath(Path.Combine(topDir, topKey.FileName.String)).WithLoadOrder(new ISkyrimModGetter[] { baseOv }).Write();
            }

            File.WriteAllText(Path.Combine(profiles, "loadorder.txt"),
                "# header\r\n" + string.Join("\r\n", baseKey.FileName, midKey.FileName, topKey.FileName) + "\r\n");
            File.WriteAllText(Path.Combine(profiles, "plugins.txt"),
                string.Join("\r\n", "*" + baseKey.FileName, "*" + midKey.FileName, "*" + topKey.FileName) + "\r\n");
            File.WriteAllText(Path.Combine(profiles, "modlist.txt"),
                "# header\r\n" + string.Join("\r\n", "+TopMod", "+MidMod", "+BaseMod") + "\r\n");

            using var svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(root, "houseCARL.user.json")));
            svc.Stats();

            // TIER 1 stood here: the DEFAULT lane's Read()/ReadCt() helpers and the eight arms over a cheap read
            // of cell A - that the winner's Temporary reads empty while the base carries 3, that each
            // child-bearing field says other plugins were not read, that no declarer is named, that the clause is
            // stated once and names its fields with no positional claim, and that those names are derived. They
            // drove housecarl_read_record and are tests against housecarl_records in src/housecarl-mcp-tests now.
            Check("CHEAP: the cheap tier CANNOT read a body — its signature takes no overlay session",
                typeof(LoadOrderService)
                    .GetMethod("AnnotateOwnedChildContent", BindingFlags.NonPublic | BindingFlags.Static)!
                    .GetParameters().All(x => x.ParameterType != typeof(LoadOrderResolver.OverlaySession)),
                "AnnotateOwnedChildContent takes an OverlaySession — it can fetch bodies");

            // The SOLE, UNREQUESTED and NO-CHILDREN reads stood here, driving housecarl_read_record; they are
            // tests in src/housecarl-mcp-tests now. The field-set half of NO-CHILDREN is a fact about the TYPE,
            // asked of a body and answered without any tool, so it stays here as its own arm.
            Check("NO-CHILDREN: a weapon's child-bearing field set is EMPTY, so no read of one can annotate anything",
                FieldsOn(topDir, topKey, weapon).Count == 0,
                string.Join(", ", FieldsOn(topDir, topKey, weapon).Keys));
            // ================= TIER 2 - the PRECISE answer =====================================================
            // The precise tier's reads stood here and through the DISJOINT/EQUAL/SELF and NOT-A-CELL arms below:
            // conflict_tree=true naming the declaring plugin per field, the singular-vs-collection clauses, the
            // no-"also" label on a winner carrying nothing, and the worldspace scaffolding pair. They drove
            // housecarl_read_record with conflict_tree=true, which the 1.x cut deleted, and the records surface has
            // no lever for that tier - so unlike the arms above they have NO test replacing them. What survives is
            // the unit level: the shape classifier and DeclaresChild, which answer the same questions off a body
            // with no render in the way, and are the arms kept below.
            Check("SHAPE: the classifier answers the two shapes off the TYPE, before any body is read",
                ShapeOn(topDir, topKey, cellA, "Landscape") == OwnedChildShape.Singular
                && ShapeOn(topDir, topKey, cellA, "Temporary") == OwnedChildShape.Collection
                && ShapeOn(baseDir, baseKey, wrld, "TopCell") == OwnedChildShape.Singular
                && ShapeOn(baseDir, baseKey, wrld, "SubCells") == OwnedChildShape.Collection,
                $"Landscape={ShapeOn(topDir, topKey, cellA, "Landscape")} SubCells={ShapeOn(baseDir, baseKey, wrld, "SubCells")}");
            // ---- REACH-NOT-ELEMENT: empty block scaffolding is not a declaration of cells ----
            // The two rendered WRLD-SCAFFOLD arms that stood here drove the precise tier and die with it. The
            // question they were about is asked directly below.
            Check("REACH: DeclaresChild answers the CHILD question, not the element question, on both bodies",
                DeclaresOn(baseDir, baseKey, wrld, "SubCells") == true
                && DeclaresOn(topDir, topKey, wrld, "SubCells") == false,
                $"base={DeclaresOn(baseDir, baseKey, wrld, "SubCells")} top={DeclaresOn(topDir, topKey, wrld, "SubCells")}");

            // The DISJOINT / EQUAL / SELF / NOT-A-CELL arms stood here - all precise-tier reads, all gone with it.
            // ---- BY CONSTRUCTION: the getter -> concrete hop resolves for EVERY child-bearing type ----
            var hopBad = new List<string>();
            foreach (var t in typeof(Weapon).Assembly.GetTypes())
            {
                if (!t.IsClass || t.IsAbstract || t.Name.EndsWith("BinaryOverlay", StringComparison.Ordinal)) continue;
                if (!typeof(Mutagen.Bethesda.Plugins.Records.IMajorRecord).IsAssignableFrom(t)) continue;
                if (WriteEngine.ChildBearingProperties(t).Count == 0) continue;
                var overlay = typeof(Weapon).Assembly.GetType(t.FullName + "BinaryOverlay");
                if (overlay is null) { hopBad.Add($"{t.Name}: no overlay type to map from"); continue; }
                var getter = WriteEngine.PrimaryGetter(overlay);
                var back = getter is null ? null : WriteEngine.ConcreteOf(getter);
                if (back != t) hopBad.Add($"{t.Name}: overlay maps to {back?.Name ?? "(null)"}");
            }
            Check("BY CONSTRUCTION: every concrete child-bearing type's overlay maps back to it (the hop the field set rides)",
                hopBad.Count == 0, string.Join(" | ", hopBad));

            // ---- DEPTH / TRANSPORTS / EMISSION / RESERVE / JSON / ARTIFACTS ----
            // A long block of arms stood here over the depth=2 render, the batch lane, the emission gate at a tight
            // max_chars, the clause reserve, both json transports and the to_file artifact. All drove
            // housecarl_read_record or housecarl_batch_record_detail; all are tests against housecarl_records in
            // src/housecarl-mcp-tests now, apart from the two conflict_tree arms in the block (the tree lane's
            // clause reserve, and the sole-toucher tree skip), which die with the precise tier.

            // ---- UNREADABLE: a toucher whose body cannot be read is NAMED, not dropped ----
            CheckUnreadable(root, mods, baseDir, baseKey, topKey, cellA);

            // ---- SENTENCE: the content net over ReadSentences, consts AND the composed note ----
            var sentenceBad = SentenceViolations();
            Check("SENTENCE: every ReadSentences const decides ([MustState] phrases or [NoClaims] with a reason) and states them",
                sentenceBad.Count == 0, string.Join(" | ", sentenceBad));
            var composed = ReadSentences.DeclarersNote(OwnedChildShape.Collection,
                new[] { "A.esp", "B.esp", "C.esp", "D.esp" }, new[] { "E.esp" });
            Check("SENTENCE: the COLLECTION note is built from the consts the net covers, and caps its names",
                composed is not null
                && composed.Contains(ReadSentences.DeclaredBy, StringComparison.Ordinal)
                && composed.Contains(ReadSentences.CouldNotRead, StringComparison.Ordinal)
                && composed.Contains("(+1 more)", StringComparison.Ordinal)
                && !composed.Contains("D.esp", StringComparison.Ordinal), composed ?? "(null)");
            var singular = ReadSentences.DeclarersNote(OwnedChildShape.Singular,
                new[] { "A.esp", "B.esp", "C.esp", "D.esp" }, Array.Empty<string>());
            Check("SENTENCE: the SINGULAR note counts and never floods names — 484 declarers of a TopCell is noise",
                singular is not null
                && singular.Contains($"{ReadSentences.CarriedBy} 4 other plugin(s)", StringComparison.Ordinal)
                && !singular.Contains("A.esp", StringComparison.Ordinal), singular ?? "(null)");
            Check("SENTENCE: nothing to say → null, so the caller has ONE place to decide",
                ReadSentences.DeclarersNote(OwnedChildShape.Collection, Array.Empty<string>(), Array.Empty<string>()) is null);

            Console.WriteLine();
            Console.WriteLine($"=== owned-child-content-guard: {_pass} passed, {_fail} failed -> {(_fail == 0 ? "PASS" : "FAIL")} ===");
            return _fail == 0 ? 0 : 1;
        }
        catch (Exception ex) { Console.WriteLine($"   FAIL (unexpected): {ex.GetType().Name}: {ex.Message}"); return 1; }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    /// <summary>UNREADABLE — the "could not look is not nothing there" rule, at the two ends where it is honestly
    /// observable, plus the measured account of what happens when a declaring plugin stops being readable at all.
    ///
    /// <para>The end-to-end shape this started as does NOT exist, and finding that out is what the fixture is for.
    /// Corrupting a declaring plugin after the index is built does not leave it a named-but-unfetchable toucher:
    /// the next read's freshness re-check rebuilds, the plugin is EXCLUDED from the order (named in
    /// <c>Stats().loadFailures</c>), the record's override depth drops and it is no longer a toucher at all — so
    /// the annotation stops naming it because the ORDER changed, not because a read failed silently. That is the
    /// load-order layer's existing, named behaviour and the arms below pin it rather than pretending otherwise.
    /// The residual hazard the unknown-arm exists for — a plugin that opens at header level but faults while its
    /// child group is walked — is not reachable from outside the process, so it is pinned at its two ends: the
    /// unit answer (null, never false) and the sentence that names it.</para></summary>
    static void CheckUnreadable(string root, string mods, string baseDir, ModKey baseKey, ModKey topKey, FormKey cellA)
    {
        var inst2 = Path.Combine(root, "unreadable");
        var prof2 = Path.Combine(inst2, "profiles", "Default");
        var mods2 = Path.Combine(inst2, "mods");
        foreach (var d in new[] { prof2, mods2, Path.Combine(root, "game2", "Data") }) Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(inst2, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(root, "game2").Replace(@"\", @"\\") + ")\r\n");
        foreach (var (src, name) in new[] { (baseDir, "BaseMod"), (Path.Combine(mods, "TopMod"), "TopMod") })
        {
            var dst = Path.Combine(mods2, name); Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)));
        }
        File.WriteAllText(Path.Combine(prof2, "loadorder.txt"), "# header\r\n" + baseKey.FileName + "\r\n" + topKey.FileName + "\r\n");
        File.WriteAllText(Path.Combine(prof2, "plugins.txt"), "*" + baseKey.FileName + "\r\n*" + topKey.FileName + "\r\n");
        File.WriteAllText(Path.Combine(prof2, "modlist.txt"), "# header\r\n+TopMod\r\n+BaseMod\r\n");

        using var svc = LoadOrderService.WithInstance(inst2, 0, new UserConfigStore(Path.Combine(root, "unreadable.user.json")));
        svc.Stats();                                                   // index built off the INTACT files
        // The intact-read arm and the corrupted-read arm stood here, both precise-tier conflict_tree reads through
        // housecarl_read_record. They die with that tier: the cheap annotation names no plugin at all, so there is
        // nothing left for "the annotation follows the order" to be about. What the pair was really guarding —
        // that the disappearance is ACCOUNTED for rather than swallowed — is the load-failure arm below, which
        // asks the load-order layer directly.

        // Corrupt the declaring plugin AFTER the index knows it touches the record. Stats() captures a fresh
        // build (the same freshness re-check any read would have triggered), so the exclusion surfaces here.
        File.WriteAllBytes(Path.Combine(mods2, "BaseMod", baseKey.FileName.String), new byte[] { 0x00, 0x01, 0x02, 0x03 });
        var stats = svc.Stats();

        // What actually happens, measured: the plugin leaves the ORDER — and the disappearance must be ACCOUNTED
        // for where a caller can see it, which is the load-failure list, not swallowed in silence.
        Check("UNREADABLE: an unopenable declarer leaves the order, and the load-order layer NAMES the failure",
            stats.loadFailures.Any(f => f.Contains(baseKey.FileName.String, StringComparison.OrdinalIgnoreCase)),
            $"loadFailures=[{string.Join(" | ", stats.loadFailures)}]");

        // The null rule, at the unit end: a field the body does not have is "I could not look", never "nothing
        // there". This is the answer the service turns into a NAMED could-not-read plugin.
        using var ov = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(mods2, "TopMod", topKey.FileName.String), SkyrimRelease.SkyrimSE);
        var topBody = ov.EnumerateMajorRecords().FirstOrDefault(r => r.FormKey == cellA);
        Check("UNREADABLE: DeclaresChild on a field the body does not have answers NULL, never false",
            topBody is not null && OwnedChildContent.DeclaresChild(topBody, "NoSuchFieldHere") is null,
            $"{OwnedChildContent.DeclaresChild(topBody!, "NoSuchFieldHere")}");
        Check("…and a body that HAS the field but declares nothing answers false, so the two stay distinguishable",
            topBody is not null && OwnedChildContent.DeclaresChild(topBody, "Temporary") == false,
            $"{OwnedChildContent.DeclaresChild(topBody!, "Temporary")}");
    }

    /// <summary>Ask <see cref="OwnedChildContent.DeclaresChild"/> directly of ONE plugin's own body — the unit-level
    /// answer behind the WRLD arms, so a render change cannot make them pass for the wrong reason.</summary>
    static bool? DeclaresOn(string dir, ModKey key, FormKey fk, string field)
    {
        using var ov = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(dir, key.FileName.String), SkyrimRelease.SkyrimSE);
        var body = ov.EnumerateMajorRecords().FirstOrDefault(r => r.FormKey == fk);
        return body is null ? null : OwnedChildContent.DeclaresChild(body, field);
    }

    static IReadOnlyDictionary<string, OwnedChildShape> FieldsOn(string dir, ModKey key, FormKey fk)
    {
        using var ov = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(dir, key.FileName.String), SkyrimRelease.SkyrimSE);
        var body = ov.EnumerateMajorRecords().FirstOrDefault(r => r.FormKey == fk);
        return body is null ? new Dictionary<string, OwnedChildShape>() : OwnedChildContent.Fields(body);
    }

    /// <summary>The SHAPE the classifier gives one field — asked off a body, answered off its TYPE, so the two
    /// sentence arms are chosen by structure rather than by a name list.</summary>
    static OwnedChildShape ShapeOn(string dir, ModKey key, FormKey fk, string field)
    {
        using var ov = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(dir, key.FileName.String), SkyrimRelease.SkyrimSE);
        var body = ov.EnumerateMajorRecords().FirstOrDefault(r => r.FormKey == fk);
        return body is null ? OwnedChildShape.None : OwnedChildContent.ShapeOf(body, field);
    }

    // FieldLine / JsonStrings / ClauseHead / ClauseLine / NamedFields / Occurrences stood here: helpers that
    // parsed a RENDERED response. Every arm that read one drove a deleted tool, so they went with those arms.

    /// <summary>The content half of the response-layer net, over <see cref="ReadSentences"/>: every const must
    /// DECIDE — declared phrases, or a stated reason there are none — and a sentence that declares a phrase must
    /// still contain it. The write surface's own arm is the model; this owner is the read surface's, and an
    /// undecorated const FAILS by name rather than passing in silence.</summary>
    static List<string> SentenceViolations()
    {
        var bad = new List<string>();
        foreach (var f in typeof(ReadSentences).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
        {
            if (!f.IsLiteral) { bad.Add($"{f.Name}: not a const (unreadable to this net)"); continue; }
            if (f.FieldType != typeof(string)) continue;   // a non-prose const (a cap) carries no sentence to check
            var text = (string?)f.GetRawConstantValue() ?? "";
            var must = f.GetCustomAttribute<MustStateAttribute>();
            var none = f.GetCustomAttribute<NoClaimsAttribute>();
            if (must is not null && none is not null) { bad.Add($"{f.Name}: declares BOTH [MustState] and [NoClaims] — pick one"); continue; }
            if (must is null && none is null) { bad.Add($"{f.Name}: declares neither [MustState] phrases nor [NoClaims] with a reason"); continue; }
            if (none is not null && none.Reason.Trim().Length == 0) bad.Add($"{f.Name}: [NoClaims] with no stated reason");
            foreach (var phrase in must?.Phrases ?? Array.Empty<string>())
                if (!text.Contains(phrase, StringComparison.Ordinal)) bad.Add($"{f.Name}: no longer states \"{phrase}\"");
        }
        return bad;
    }

    /// <summary>File an interior cell into its block/sub-block (Mutagen writes cells through the group tree, not a
    /// flat list) — the merge guard's helper, same arithmetic.</summary>
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


    static void Check(string label, bool ok, string? detail = null)
    {
        Console.WriteLine($"   [{(ok ? "PASS" : "FAIL")}] {label}");
        if (ok) _pass++;
        else { _fail++; if (detail is not null) Console.WriteLine($"          got: {detail}"); }
    }
}
