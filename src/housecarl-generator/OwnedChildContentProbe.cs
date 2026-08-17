using System.Reflection;
using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// #342 stage 1 — GUARD for the owned-child content annotation, over a synthetic MO2 instance driven through the
/// REAL read tool (<c>housecarl_read_record</c>, both transports).
///
/// The bug: a parent's child records — placed references under a cell, INFOs under a topic, cells under a
/// worldspace — are declared per plugin and assembled by the game from every plugin that declares them. An
/// override that touches the parent for an unrelated reason carries none and deletes none, so reading the winner
/// reports an empty cell the game fills. Reproduced on a real order at <c>008EB5:Skyrim.esm</c>: winner
/// Temporary 0, <c>Skyrim.esm</c>'s own body 201.
///
/// The trigger is a BOOLEAN — does any OTHER plugin touching this record declare at least one child record for
/// this field — and two arms exist because the cheaper questions both produce wrong answers:
///   REACH-NOT-ELEMENT — "has a top-level element" is true of a worldspace holding empty block scaffolding, whose
///                  cells sit two container levels down. WRLD-SCAFFOLD holds that an empty-scaffold plugin is NOT
///                  named a declarer while a real one is.
///   DISJOINT     — "declares MORE than this body" says nothing when this body holds the biggest single list and
///                  another declares a disjoint set the game also loads. DISJOINT + EQUAL hold that both annotate.
/// The rest:
///   FALSE-EMPTY  — the winner touches cell A carrying no children; a lower plugin declares Temporary, Persistent
///                  and a Landscape. Each is annotated, naming the declarer. The pre-fix hazard is asserted first
///                  — a fixture that stopped exhibiting it would make every arm below vacuous.
///   SINGULAR     — Cell.Landscape is a SINGULAR owned child, not a list.
///   NOT-A-CELL   — DialogTopic.Responses, because the field set is <see cref="OwnedChildContent.Fields"/>
///                  over the write surface's pinned authority, never a hand list of cell fields.
///   SOLE / SELF  — one declarer: no annotation, and the read body is never named among the "others".
///   UNREQUESTED  — fields=[EditorID]: no annotation (the walk is gated on the read's own field lines).
///   NO-CHILDREN  — a weapon with three touchers: no annotation, and the field set for its type is EMPTY.
///   RESPONSE-ONCE— the invariant clause is stated ONCE per response, not per annotated field, on both lanes.
///   UNREADABLE   — a toucher whose body cannot be read is NAMED, never silently dropped (a missing declarer
///                  reads as "nobody else declares", the same wrong answer one level down).
///   TOKEN        — display-only: the field's round-trip token/note is what it was, on both transports.
///   SENTENCE     — the content net over <see cref="ReadSentences"/>, consts AND the composed per-field note.
///
/// Run: <c>dotnet run --project src/housecarl-generator -- owned-child-content-guard</c>
/// </summary>
public static class OwnedChildContentProbe
{
    static int _pass, _fail;

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

            // The DEFAULT lane — the cheap tier. conflict_tree stays false.
            string Read(FormKey fk, string? plugin = null, string[]? fields = null, int depth = 1, string? format = null)
                => ReadTools.ReadRecord(svc, fk.ToString(), plugin, fields, depth, false, false, format);
            // The lane that has already fetched every touching body — the precise tier rides it for free.
            string ReadCt(FormKey fk, string? plugin = null)
                => ReadTools.ReadRecord(svc, fk.ToString(), plugin, null, 1, true, false, null);
            string By(ModKey k) => $"{ReadSentences.DeclaredBy} {k.FileName}";
            // ================= TIER 1 — the CHEAP annotation every read states from the index alone =========
            var aWinner = Read(cellA);
            Check("FIXTURE exhibits the bug: the winner's own Temporary reads EMPTY on a cell the base fills",
                aWinner.Contains("winner=HcOcTop.esp", StringComparison.Ordinal)
                && FieldLine(aWinner, "Temporary") is { } tw && tw.Contains("0 item(s)", StringComparison.Ordinal),
                FieldLine(aWinner, "Temporary"));
            var aBase = Read(cellA, plugin: baseKey.FileName.String);
            Check("…and the base's own body carries 3 — the content the winner does not show",
                FieldLine(aBase, "Temporary") is { } tb && tb.Contains("3 item(s)", StringComparison.Ordinal),
                FieldLine(aBase, "Temporary"));

            Check("CHEAP: the child-bearing field says other plugins touch this record and were not read",
                FieldLine(aWinner, "Temporary") is { } c1
                && c1.Contains($"2 {ReadSentences.NotRead}", StringComparison.Ordinal),
                FieldLine(aWinner, "Temporary"));
            Check("CHEAP: it claims nothing about WHO declares — no declarer naming on the default lane",
                !aWinner.Contains(ReadSentences.DeclaredBy, StringComparison.Ordinal)
                && !aWinner.Contains(ReadSentences.CarriedBy, StringComparison.Ordinal), Trim(aWinner));
            Check("CHEAP: every child-bearing field carries it, including one nobody declares — 'not read' is true of all",
                FieldLine(aWinner, "NavigationMeshes") is { } c2 && c2.Contains(ReadSentences.NotRead, StringComparison.Ordinal),
                FieldLine(aWinner, "NavigationMeshes"));
            Check("CHEAP: the response states the not-read clause ONCE, and names the lane that answers precisely",
                Occurrences(aWinner, ClauseHead(ReadSentences.NotReadFraming)) == 1
                && ClauseLine(aWinner, ReadSentences.NotReadFraming) is { } cc
                && cc.Contains("conflict_tree=true", StringComparison.Ordinal),
                $"clause={Occurrences(aWinner, ClauseHead(ReadSentences.NotReadFraming))}");
            // The clause NAMES its fields rather than pointing at them — the fix for a positional claim three
            // transports independently falsified (manifest line 1, json's second key, a truncated field loop).
            Check("CLAUSE-NAMES: the clause names the annotated fields, and points at no position at all",
                ClauseLine(aWinner, ReadSentences.NotReadFraming) is { } cn
                && cn.Contains("Temporary", StringComparison.Ordinal)
                && cn.Contains("Persistent", StringComparison.Ordinal)
                && !cn.Contains("above", StringComparison.OrdinalIgnoreCase)
                && !cn.Contains("below", StringComparison.OrdinalIgnoreCase),
                ClauseLine(aWinner, ReadSentences.NotReadFraming) ?? "(no clause)");
            Check("CLAUSE-NAMES: the names are DERIVED — every field named is one the response annotated",
                ClauseLine(aWinner, ReadSentences.NotReadFraming) is { } cd
                && NamedFields(cd, ReadSentences.NotReadFraming).All(f => FieldLine(aWinner, f) is { } fl
                                                                          && fl.Contains(ReadSentences.NotRead, StringComparison.Ordinal)),
                ClauseLine(aWinner, ReadSentences.NotReadFraming) ?? "(no clause)");
            Check("CHEAP: the cheap tier CANNOT read a body — its signature takes no overlay session",
                typeof(LoadOrderService)
                    .GetMethod("AnnotateOwnedChildContent", BindingFlags.NonPublic | BindingFlags.Static)!
                    .GetParameters().All(x => x.ParameterType != typeof(LoadOrderResolver.OverlaySession)),
                "AnnotateOwnedChildContent takes an OverlaySession — it can fetch bodies");

            var cRead = Read(cellC);
            Check("SOLE: a record only one plugin touches is not annotated at all",
                FieldLine(cRead, "Temporary") is { } t5 && !t5.Contains(ReadSentences.NotRead, StringComparison.Ordinal),
                FieldLine(cRead, "Temporary"));
            var aNarrow = Read(cellA, fields: new[] { "EditorID" });
            Check("UNREQUESTED: fields=[EditorID] carries no annotation and no clause",
                !aNarrow.Contains(ReadSentences.NotRead, StringComparison.Ordinal)
                && ClauseLine(aNarrow, ReadSentences.NotReadFraming) is null, Trim(aNarrow));
            var weapRead = Read(weapon);
            Check("NO-CHILDREN: a 3-toucher weapon read carries no annotation, and its field set is EMPTY",
                weapRead.Contains("winner=HcOcTop.esp", StringComparison.Ordinal)
                && !weapRead.Contains(ReadSentences.NotRead, StringComparison.Ordinal)
                && FieldsOn(topDir, topKey, weapon).Count == 0, Trim(weapRead));

            // ================= TIER 2 — the PRECISE answer, on the lane that already fetched the bodies =======
            var aTree = ReadCt(cellA);
            Check("PRECISE: conflict_tree names the declaring plugin instead of the cheap 'not read' note",
                FieldLine(aTree, "Temporary") is { } p1
                && p1.Contains(By(baseKey), StringComparison.Ordinal)
                && !p1.Contains(ReadSentences.NotRead, StringComparison.Ordinal),
                FieldLine(aTree, "Temporary"));
            Check("PRECISE: a field NOBODY declares loses its note entirely — the cheap tier could not know that",
                FieldLine(aTree, "NavigationMeshes") is { } p2
                && !p2.Contains(ReadSentences.NotRead, StringComparison.Ordinal)
                && !p2.Contains(ReadSentences.DeclaredBy, StringComparison.Ordinal),
                FieldLine(aTree, "NavigationMeshes"));
            Check("PRECISE: the plugin that touches the cell declaring NOTHING is not named a declarer",
                FieldLine(aTree, "Temporary") is { } p3 && !p3.Contains("HcOcMid.esp", StringComparison.Ordinal),
                FieldLine(aTree, "Temporary"));
            Check("PRECISE: Persistent too — the annotation is per FIELD, not per record",
                FieldLine(aTree, "Persistent") is { } p4 && p4.Contains(By(baseKey), StringComparison.Ordinal),
                FieldLine(aTree, "Persistent"));

            // ---- the two SHAPES say different things, because different things are true of them ----
            Check("SINGULAR: Landscape is annotated with a COUNT, never a declarer name list",
                FieldLine(aTree, "Landscape") is { } s1
                && s1.Contains($"{ReadSentences.CarriedBy} 1 other plugin(s)", StringComparison.Ordinal)
                && !s1.Contains(ReadSentences.DeclaredBy, StringComparison.Ordinal),
                FieldLine(aTree, "Landscape"));
            Check("SINGULAR: the response carries the singly-resolved clause for that shape, naming its field",
                Occurrences(aTree, ClauseHead(ReadSentences.SingleResolvedFraming)) == 1
                && ClauseLine(aTree, ReadSentences.SingleResolvedFraming) is { } sc
                && NamedFields(sc, ReadSentences.SingleResolvedFraming).SequenceEqual(new[] { "Landscape" }),
                ClauseLine(aTree, ReadSentences.SingleResolvedFraming) ?? "(no singular clause)");
            Check("COLLECTION: the response also carries the additive clause, once, naming the list-shaped fields",
                Occurrences(aTree, ClauseHead(ReadSentences.MergeCollectionFraming)) == 1
                && ClauseLine(aTree, ReadSentences.MergeCollectionFraming) is { } mc
                && NamedFields(mc, ReadSentences.MergeCollectionFraming).SequenceEqual(new[] { "Persistent", "Temporary" }),
                ClauseLine(aTree, ReadSentences.MergeCollectionFraming) ?? "(no collection clause)");
            // FINDING 5 — the flagship: a winner carrying NOTHING, beside plugins that declare. "also declared by"
            // asserted that this body declares too, which is false in exactly the case the feature exists for.
            Check("LABEL: on a winner carrying 0 items the declarer label does NOT assert this body declares too",
                FieldLine(aTree, "Temporary") is { } fs
                && fs.Contains("0 item(s)", StringComparison.Ordinal)
                && fs.Contains(By(baseKey), StringComparison.Ordinal)
                && !fs.Contains("also ", StringComparison.OrdinalIgnoreCase),
                FieldLine(aTree, "Temporary"));
            Check("SHAPE: the classifier answers the two shapes off the TYPE, before any body is read",
                ShapeOn(topDir, topKey, cellA, "Landscape") == OwnedChildShape.Singular
                && ShapeOn(topDir, topKey, cellA, "Temporary") == OwnedChildShape.Collection
                && ShapeOn(baseDir, baseKey, wrld, "TopCell") == OwnedChildShape.Singular
                && ShapeOn(baseDir, baseKey, wrld, "SubCells") == OwnedChildShape.Collection,
                $"Landscape={ShapeOn(topDir, topKey, cellA, "Landscape")} SubCells={ShapeOn(baseDir, baseKey, wrld, "SubCells")}");
            var dOnly = ReadCt(cellD);
            Check("SHAPE: a response with only COLLECTION fields annotated states only the additive clause",
                Occurrences(dOnly, ClauseHead(ReadSentences.MergeCollectionFraming)) == 1
                && Occurrences(dOnly, ClauseHead(ReadSentences.SingleResolvedFraming)) == 0,
                $"collection={Occurrences(dOnly, ClauseHead(ReadSentences.MergeCollectionFraming))} "
                + $"singular={Occurrences(dOnly, ClauseHead(ReadSentences.SingleResolvedFraming))}");

            // ---- REACH-NOT-ELEMENT: empty block scaffolding is not a declaration of cells ----
            var wsBase = ReadCt(wrld, plugin: baseKey.FileName.String);
            Check("WRLD-SCAFFOLD: a worldspace declaring 2 EMPTY blocks is NOT named as declaring SubCells content",
                FieldLine(wsBase, "SubCells") is { } w1 && !w1.Contains(ReadSentences.DeclaredBy, StringComparison.Ordinal),
                FieldLine(wsBase, "SubCells"));
            var wsWinner = ReadCt(wrld);
            Check("…while the winner (2 empty blocks) IS annotated by the base's 1 block holding 3 real cells",
                FieldLine(wsWinner, "SubCells") is { } w2 && w2.Contains(By(baseKey), StringComparison.Ordinal),
                FieldLine(wsWinner, "SubCells"));
            Check("REACH: DeclaresChild answers the CHILD question, not the element question, on both bodies",
                DeclaresOn(baseDir, baseKey, wrld, "SubCells") == true
                && DeclaresOn(topDir, topKey, wrld, "SubCells") == false,
                $"base={DeclaresOn(baseDir, baseKey, wrld, "SubCells")} top={DeclaresOn(topDir, topKey, wrld, "SubCells")}");

            // ---- DISJOINT / EQUAL / SELF: the shapes a count comparison stayed silent on ----
            var bTree = ReadCt(cellB);
            Check("DISJOINT: the winner declaring 4 of its OWN references is still annotated by the base's 1",
                FieldLine(bTree, "Temporary") is { } t3
                && t3.Contains("4 item(s)", StringComparison.Ordinal) && t3.Contains(By(baseKey), StringComparison.Ordinal),
                FieldLine(bTree, "Temporary"));
            Check("EQUAL: one reference each side — both bodies declare, so the read is annotated",
                FieldLine(dOnly, "Temporary") is { } t7 && t7.Contains(By(baseKey), StringComparison.Ordinal),
                FieldLine(dOnly, "Temporary"));
            var eTree = ReadCt(cellE);
            Check("SELF: a body that is the ONLY declarer is not annotated, and never names itself",
                FieldLine(eTree, "Temporary") is { } t8
                && t8.Contains("1 item(s)", StringComparison.Ordinal)
                && !t8.Contains(ReadSentences.DeclaredBy, StringComparison.Ordinal),
                FieldLine(eTree, "Temporary"));

            var topicTree = ReadCt(topic);
            Check("NOT-A-CELL: DialogTopic.Responses is annotated (winner re-lists 1 of the base's 2)",
                FieldLine(topicTree, "Responses") is { } r1 && r1.Contains(By(baseKey), StringComparison.Ordinal),
                FieldLine(topicTree, "Responses"));

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

            // ---- DEPTH / TRANSPORTS ----
            var aDeep = Read(cellA, depth: 2);
            Check("DEPTH: at depth=2 the container's own summary line still carries the annotation",
                FieldLine(aDeep, "Temporary") is { } t6 && t6.Contains(ReadSentences.NotRead, StringComparison.Ordinal),
                FieldLine(aDeep, "Temporary"));
            var batch = ReadTools.BatchRecordDetail(svc, new[] { cellA.ToString(), cellB.ToString() });
            Check("RESPONSE-ONCE (batch): two annotated records, still ONE clause",
                Occurrences(batch, ClauseHead(ReadSentences.NotReadFraming)) == 1,
                $"clause={Occurrences(batch, ClauseHead(ReadSentences.NotReadFraming))}");
            var cleanBatch = ReadTools.BatchRecordDetail(svc, new[] { weapon.ToString() });
            Check("RESPONSE-ONCE: a response with nothing annotated carries NO clause",
                Occurrences(cleanBatch, ClauseHead(ReadSentences.NotReadFraming)) == 0, Trim(cleanBatch));

            // ---- EMISSION (text): the clause is earned by a field LINE, not by the decision to annotate ----
            // A cap that stops the field loop before the annotated field leaves a clause describing something the
            // caller cannot see. Both directions, on the same record, moved only by max_chars.
            var tightText = ReadTools.ReadRecord(svc, cellA.ToString(), null, null, 1, false, false, null, max_chars: 300);
            Check("EMISSION (text): a cap that truncates the annotated field away states NO clause over it",
                tightText.Contains("truncated: showing", StringComparison.Ordinal)
                && !tightText.Contains(ReadSentences.NotRead, StringComparison.Ordinal)
                && ClauseLine(tightText, ReadSentences.NotReadFraming) is null, Trim(tightText));
            Check("EMISSION (text): …and the SAME read with room for the field states it, over that field",
                aWinner.Contains(ReadSentences.NotRead, StringComparison.Ordinal)
                && ClauseLine(aWinner, ReadSentences.NotReadFraming) is not null, Trim(aWinner));

            // ---- RESERVE: the clauses come OUT of max_chars, not on top of it (finding 6) ----
            // Measured on the SINGLE read, because the bulk lanes auto-spill when they truncate and that manifest
            // block is appended outside any cap — a total-length claim there would be measuring the spill. Here the
            // ceiling is tight: every cut is checked before a line is appended, so the response can exceed
            // max_chars by its own longest line and by nothing else. fields= puts the annotated fields FIRST so
            // they survive the cut (otherwise the emission gate correctly states nothing and there is no clause to
            // budget for), with filler behind them to make the body outgrow the cap either way.
            var padded = new[] { "Temporary", "Persistent", "Landscape", "NavigationMeshes" }
                .Concat(Enumerable.Repeat(new[] { "Name", "Flags", "Grid", "Lighting", "OcclusionData", "MaxHeightData",
                                                  "LightingTemplate", "WaterHeight", "Regions", "Location", "Water",
                                                  "Owner", "FactionRank", "LockList" }, 4).SelectMany(x => x)).ToArray();
            var cappedCheap = ReadTools.ReadRecord(svc, cellA.ToString(), null, padded, 1, false, false, null, 1400);
            int longestLine = cappedCheap.Split('\n').Max(l => l.Length + 1);
            Check("RESERVE: an annotated response answers INSIDE its max_chars — the clause is reserved, not appended",
                cappedCheap.Contains("truncated: showing", StringComparison.Ordinal)
                && ClauseLine(cappedCheap, ReadSentences.NotReadFraming) is not null
                && cappedCheap.Length <= 1400 + longestLine,
                $"len={cappedCheap.Length} cap=1400 longest-line={longestLine}");
            Check("RESERVE: the cut still quotes the caller's OWN max_chars, not the reduced budget",
                cappedCheap.Contains("max_chars=1400", StringComparison.Ordinal)
                && !cappedCheap.Contains("max_chars=" + (1400 - ReadSentences.ClauseReserve(true, false, false)), StringComparison.Ordinal),
                Trim(cappedCheap));
            // The conflict_tree lane is Aaron's own repro. Its diff block cuts per NODE rather than per line, so its
            // pre-existing slack is wider than one line and a total-length claim there would be measuring the diff,
            // not this fix. What IS this fix on that lane: what the clauses actually cost fits what was held back.
            var cappedTree = ReadTools.BatchRecordDetail(svc, new[] { cellA.ToString(), cellB.ToString() },
                                                         conflict_tree: true, max_chars: 2000);
            int clauseChars = new[] { ReadSentences.MergeCollectionFraming, ReadSentences.SingleResolvedFraming }
                .Select(f => ClauseLine(cappedTree, f))
                .Where(l => l is not null).Sum(l => l!.Length + 2);   // each clause is written as "\n" + clause + "\n"
            Check("RESERVE: on the conflict_tree lane the stated clauses fit inside the chars reserved for them",
                clauseChars > 0 && clauseChars <= ReadSentences.ClauseReserve(false, true, true),
                $"clauses={clauseChars} reserve={ReadSentences.ClauseReserve(false, true, true)}");

            var aJson = Read(cellA, format: "json");
            string? jsonDisplay = null, jsonNote = null;
            using (var doc = JsonDocument.Parse(aJson))
            {
                foreach (var f in doc.RootElement.GetProperty("fields").EnumerateArray())
                    if (f.GetProperty("path").GetString() == "Temporary")
                    {
                        jsonDisplay = f.TryGetProperty("display", out var d) ? d.GetString() : null;
                        jsonNote = f.TryGetProperty("note", out var n) ? n.GetString() : null;
                    }
                Check("JSON: the clause is a response-level member, from the same source as text",
                    doc.RootElement.TryGetProperty("owned_child_note", out var ocn)
                    && ocn.GetString() is { } s
                    && s.StartsWith(ClauseHead(ReadSentences.NotReadFraming), StringComparison.Ordinal)
                    && NamedFields(s, ReadSentences.NotReadFraming).Contains("Temporary"),
                    "owned_child_note missing or drifted");
            }
            // The single-read json object writes the clause AFTER the array it describes, and only over what that
            // array carried — it used to be the object's SECOND key, ahead of `fields` (Aaron's finding 3).
            Check("JSON: the clause is written after the fields array it is about, never ahead of it",
                aJson.IndexOf("\"owned_child_note\"", StringComparison.Ordinal)
                    > aJson.IndexOf("\"fields\"", StringComparison.Ordinal),
                $"note-at={aJson.IndexOf("\"owned_child_note\"", StringComparison.Ordinal)} fields-at={aJson.IndexOf("\"fields\"", StringComparison.Ordinal)}");
            var aJsonTight = ReadTools.ReadRecord(svc, cellA.ToString(), null, null, 1, false, false, "json", 300);
            using (var doc = JsonDocument.Parse(aJsonTight))
                Check("EMISSION (json single): a fields array truncated before the annotated field states NO clause",
                    doc.RootElement.GetProperty("fields").EnumerateArray()
                       .Any(f => f.TryGetProperty("note", out var n) && n.GetString()!.StartsWith("[truncated at max_chars", StringComparison.Ordinal))
                    && !doc.RootElement.TryGetProperty("owned_child_note", out _), Trim(aJsonTight));
            Check("JSON: the per-field half rides `display`",
                jsonDisplay is not null && jsonDisplay.Contains(ReadSentences.NotRead, StringComparison.Ordinal),
                jsonDisplay ?? "(no display)");
            Check("TOKEN: the leaf's own note is unchanged on both lanes — the annotation never replaces the value",
                jsonNote is not null && jsonNote.StartsWith("[list: 0 item(s)]", StringComparison.Ordinal)
                && FieldLine(aWinner, "Temporary")!.StartsWith(
                       "Temporary = [list: 0 item(s)]" + ReadEngine.DepthExpandHint + "   (", StringComparison.Ordinal),
                jsonNote ?? "(no note)");

            // ---- ARTIFACTS: a to_file job carries the same tier its lane ran, clause IN the file ----
            var artifact = Path.Combine(root, "cells.jsonl");
            var inline = ReadTools.BatchRecordDetail(svc, new[] { cellA.ToString() }, to_file: artifact);
            var fileText = File.ReadAllText(artifact);
            // The manifest is JSON, and the writer escapes non-ASCII (an em-dash ships as —), so the clause is
            // compared against the DECODED string values rather than the raw file text.
            var fileDecoded = JsonStrings(File.ReadAllLines(artifact)[0]);
            Check("ARTIFACT: the annotation reaches the FILE's rows, and its clause reaches the manifest",
                fileText.Contains(ReadSentences.NotRead, StringComparison.Ordinal)
                && fileDecoded.Contains(ClauseHead(ReadSentences.NotReadFraming), StringComparison.Ordinal),
                $"note={fileText.Contains(ReadSentences.NotRead, StringComparison.Ordinal)} "
                + $"clause-in-manifest={fileDecoded.Contains(ClauseHead(ReadSentences.NotReadFraming), StringComparison.Ordinal)}");
            // The manifest is line 1 and the rows are lines 2..N, so a clause that pointed "above" pointed away from
            // its own subject for the one reader it was written for — an artifact re-opened with no conversation
            // attached (Aaron's finding 2). It names the fields instead, which is true from line 1.
            Check("ARTIFACT: the manifest clause names the annotated fields and claims no position",
                ClauseLine(fileDecoded, ReadSentences.NotReadFraming) is { } ml
                && NamedFields(ml, ReadSentences.NotReadFraming).Contains("Temporary")
                && !ml.Contains("above", StringComparison.OrdinalIgnoreCase)
                && !ml.Contains("below", StringComparison.OrdinalIgnoreCase),
                ClauseLine(fileDecoded, ReadSentences.NotReadFraming) ?? "(no manifest clause)");
            Check("ARTIFACT: the manifest-only response does NOT state a clause over rows it did not render",
                ClauseLine(inline, ReadSentences.NotReadFraming) is null, Trim(inline));

            // ---- THE PRECISE TIER INHERITS THE TREE RENDER'S SKIPS ----
            // A tree fetch is one whole-overlay enumeration per touching plugin. The annotation must never buy
            // those bodies where the diff itself would not: on a record nothing else touches (nothing to diff), or
            // on a response already over budget (the diff is skipped and the bodies thrown away).
            var soleCt = ReadCt(cellC);
            Check("SKIP: conflict_tree on a SOLE-toucher record does not fetch a tree — nothing to diff, nothing to name",
                !soleCt.Contains("diff (field deltas", StringComparison.Ordinal)
                && FieldLine(soleCt, "Temporary") is { } sk1
                && !sk1.Contains(ReadSentences.DeclaredBy, StringComparison.Ordinal)
                && !sk1.Contains(ReadSentences.NotRead, StringComparison.Ordinal), FieldLine(soleCt, "Temporary"));
            // The over-budget half of that skip is NOT pinned, and the code says why: the prefetch runs before the
            // record's own fields are rendered, so it cannot foresee a cap hit during them. What IS pinned is that
            // a bulk lane whose buffer is already full renders no further rows at all, so those rows pay nothing.
            var capped = ReadTools.BatchRecordDetail(svc, new[] { cellA.ToString(), cellB.ToString() },
                                                     conflict_tree: true, max_chars: 400);
            Check("SKIP: a bulk response past its budget stops rendering rows, so the rows past it buy no bodies",
                capped.Contains("truncated: rendered", StringComparison.Ordinal), Trim(capped));

            // ---- JSON gates the clause on rows RENDERED, exactly as the text lane does ----
            var jsonTrunc = ReadTools.BatchRecordDetail(svc, new[] { weapon.ToString(), cellA.ToString() },
                                                        format: "json", max_chars: 400);
            using (var doc = JsonDocument.Parse(jsonTrunc))
                Check("JSON: a batch truncated before its only annotated row states NO clause",
                    doc.RootElement.GetProperty("truncated").GetBoolean()
                    && !doc.RootElement.TryGetProperty("owned_child_note", out _),
                    $"rendered={doc.RootElement.GetProperty("rendered").GetInt32()} clause={doc.RootElement.TryGetProperty("owned_child_note", out _)}");
            var jsonSpill = ReadTools.BatchRecordDetail(svc, new[] { cellA.ToString() }, format: "json",
                                                        to_file: Path.Combine(root, "spill.jsonl"));
            using (var doc = JsonDocument.Parse(jsonSpill))
                Check("JSON: a manifest-only response states NO clause over rows it did not render",
                    !doc.RootElement.TryGetProperty("owned_child_note", out _), Trim(jsonSpill));

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
        var before = ReadTools.ReadRecord(svc, cellA.ToString(), null, null, 1, true);   // precise lane — declarer naming lives there
        bool intact = FieldLine(before, "Temporary") is { } l
                      && l.Contains($"{ReadSentences.DeclaredBy} {baseKey.FileName}", StringComparison.Ordinal);
        Check("UNREADABLE: the fixture's intact read names the declarer (so the corrupted read has something to lose)",
              intact, FieldLine(before, "Temporary"));

        // Corrupt the declaring plugin AFTER the index knows it touches the record, then read again.
        File.WriteAllBytes(Path.Combine(mods2, "BaseMod", baseKey.FileName.String), new byte[] { 0x00, 0x01, 0x02, 0x03 });
        string after;
        try { after = ReadTools.ReadRecord(svc, cellA.ToString(), null, null, 1, true); }
        catch (Exception ex) { after = "THREW " + ex.GetType().Name + ": " + ex.Message; }
        var line = FieldLine(after, "Temporary");
        var stats = svc.Stats();

        // What actually happens, measured: the plugin leaves the ORDER. The annotation must then stop naming it —
        // it no longer declares anything the game loads from this order — but the disappearance must be ACCOUNTED
        // for where a caller can see it, which is the load-failure list, not swallowed in silence.
        Check("UNREADABLE: an unopenable declarer leaves the order, and the load-order layer NAMES the failure",
            stats.loadFailures.Any(f => f.Contains(baseKey.FileName.String, StringComparison.OrdinalIgnoreCase)),
            $"loadFailures=[{string.Join(" | ", stats.loadFailures)}]");
        Check("…and the annotation follows the order rather than naming a plugin that is no longer in it",
            line is not null && !line.Contains(baseKey.FileName.String, StringComparison.OrdinalIgnoreCase),
            line ?? Trim(after));

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

    /// <summary>The rendered line for one field path, trimmed — the text lane's "  Path = value   (annotation)".</summary>
    static string? FieldLine(string render, string path)
    {
        foreach (var line in render.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith(path + " = ", StringComparison.Ordinal)) return t;
        }
        return null;
    }

    /// <summary>Every string VALUE in a json document, concatenated — the write guard's own idiom, because the
    /// writer escapes non-ASCII and a raw Contains would fail for the encoder's reasons, not the render's.</summary>
    static string JsonStrings(string json)
    {
        var sb = new System.Text.StringBuilder();
        void Walk(JsonElement e)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.String: sb.Append(e.GetString()).Append('\n'); break;
                case JsonValueKind.Object: foreach (var p in e.EnumerateObject()) Walk(p.Value); break;
                case JsonValueKind.Array: foreach (var v in e.EnumerateArray()) Walk(v); break;
            }
        }
        using var doc = JsonDocument.Parse(json);
        Walk(doc.RootElement);
        return sb.ToString();
    }

    /// <summary>A response-level clause's field-INDEPENDENT head, up to the "{0}" its derived field list fills.
    /// "Is this clause stated, and how often" is a question about the clause; WHICH fields it names is a separate
    /// question with its own arms, so counting on the head keeps the two from masking each other.</summary>
    static string ClauseHead(string framing) => framing[..framing.IndexOf("{0}", StringComparison.Ordinal)];

    /// <summary>The rendered clause line for one framing, or null when the response does not state it.</summary>
    static string? ClauseLine(string render, string framing)
    {
        var head = ClauseHead(framing);
        foreach (var line in render.Split('\n'))
            if (line.StartsWith(head, StringComparison.Ordinal)) return line;
        return null;
    }

    /// <summary>The field names a rendered clause actually derived — the "({0})" list, parsed back out.</summary>
    static IReadOnlyList<string> NamedFields(string clauseLine, string framing)
    {
        var rest = clauseLine[ClauseHead(framing).Length..];
        int close = rest.IndexOf(')');
        return close < 0
            ? Array.Empty<string>()
            : rest[..close].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                           .Where(s => s != "…").ToList();
    }

    static int Occurrences(string haystack, string needle)
    {
        int n = 0;
        for (int i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
        return n;
    }

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

    static string Trim(string s) => s.Length <= 300 ? s.Replace('\n', '|') : s[..300].Replace('\n', '|') + "…";

    static void Check(string label, bool ok, string? detail = null)
    {
        Console.WriteLine($"   [{(ok ? "PASS" : "FAIL")}] {label}");
        if (ok) _pass++;
        else { _fail++; if (detail is not null) Console.WriteLine($"          got: {detail}"); }
    }
}
