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
/// The bug: a parent's child records — placed references under a cell, INFOs under a topic — are declared per
/// plugin and assembled by the game from every plugin that declares them. An override that touches the parent for
/// an unrelated reason carries none and deletes none, so reading the winner reports an empty cell the game fills.
/// Reproduced on a real order at <c>008EB5:Skyrim.esm</c>: winner Temporary 0, <c>Skyrim.esm</c>'s own body 201.
///
/// The fixture reproduces that shape and its neighbours, so every branch of the trigger has an arm:
///   FALSE-EMPTY  — the winner touches cell A carrying no children; a lower plugin declares 3 Temporary,
///                  1 Persistent and a Landscape. The annotation fires on each, naming the count and the plugin.
///                  The pre-fix hazard is asserted first (the winner really does read 0) — a fixture that stopped
///                  exhibiting it would make every arm below vacuous.
///   SINGULAR     — Cell.Landscape is a SINGULAR owned child, not a list; present/absent is its count.
///   NOT-A-CELL   — the same annotation on DialogTopic.Responses, because the field set is
///                  <see cref="OwnedChildContent.FieldNames"/> over the write surface's pinned authority, never a
///                  hand list of cell fields.
///   WINNER-WINS  — cell B's winner declares MORE than the plugin below it: no annotation (the trigger is "another
///                  body declares more", not "more than one plugin touches this").
///   SOLE         — cell C is declared by one plugin: no annotation, no walk.
///   UNREQUESTED  — fields=[EditorID] on cell A: no annotation (the walk is gated on the read's own field lines).
///   NO-CHILDREN  — a weapon with three touchers: no annotation, and the field set for its type is EMPTY, which is
///                  what makes this free on every read that is not one of the three owning types.
///   OTHER-NOT-LOWER — a plugin=-scoped read of the BASE's cell B, where a HIGHER plugin declares more. The
///                  additive assembly is over the whole touching set, so the workaround this bug's reporter reached
///                  for is annotated too.
///   TOKEN        — the annotation is display-only: the field's round-trip token/note is byte-identical to what it
///                  was, on both transports.
///   SENTENCE     — the content net over <see cref="ReadSentences"/>: every const decides ([MustState] phrases or
///                  [NoClaims] with a reason) and still states the phrases it declares.
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
            var cellB = new FormKey(baseKey, 0xC02);      // the winner-declares-more cell
            var cellC = new FormKey(baseKey, 0xC03);      // declared by exactly one plugin
            var topic = new FormKey(baseKey, 0xD01);
            var weapon = new FormKey(baseKey, 0xE01);
            var baseDir = Path.Combine(mods, "BaseMod"); Directory.CreateDirectory(baseDir);
            {
                var m = new SkyrimMod(baseKey, SkyrimRelease.SkyrimSE);
                var a = new Cell(cellA, SkyrimRelease.SkyrimSE) { EditorID = "HcOcCellA", Flags = Cell.Flag.IsInteriorCell };
                for (int i = 0; i < 3; i++)
                    a.Temporary.Add(new PlacedObject(new FormKey(baseKey, (uint)(0xC10 + i)), SkyrimRelease.SkyrimSE) { EditorID = $"HcOcTemp{i}" });
                a.Persistent.Add(new PlacedObject(new FormKey(baseKey, 0xC1A), SkyrimRelease.SkyrimSE) { EditorID = "HcOcPers0" });
                a.Landscape = new Landscape(new FormKey(baseKey, 0xC1B), SkyrimRelease.SkyrimSE) { EditorID = "HcOcLand" };
                FileInterior(m, a);

                var b = new Cell(cellB, SkyrimRelease.SkyrimSE) { EditorID = "HcOcCellB", Flags = Cell.Flag.IsInteriorCell };
                b.Temporary.Add(new PlacedObject(new FormKey(baseKey, 0xC20), SkyrimRelease.SkyrimSE) { EditorID = "HcOcBTemp0" });
                FileInterior(m, b);

                var c = new Cell(cellC, SkyrimRelease.SkyrimSE) { EditorID = "HcOcCellC", Flags = Cell.Flag.IsInteriorCell };
                c.Temporary.Add(new PlacedObject(new FormKey(baseKey, 0xC30), SkyrimRelease.SkyrimSE) { EditorID = "HcOcCTemp0" });
                FileInterior(m, c);

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
                m.BeginWrite.ToPath(Path.Combine(baseDir, baseKey.FileName.String)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
            }

            // ---- MID: a second toucher of cell A that also declares no children (so "other plugins" is a count,
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

            // ---- TOP (the winner): the Occlusion.esp shape on cell A — touches the cell, carries no children.
            //      On cell B it declares MORE than the base. On the topic it re-lists one INFO of two. ----
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

                var t = new DialogTopic(topic, SkyrimRelease.SkyrimSE) { EditorID = "HcOcTopic" };
                var only = new DialogResponses(new FormKey(baseKey, 0xD10), SkyrimRelease.SkyrimSE);
                only.Responses.Add(new DialogResponse { Text = "patched line 0" });
                t.Responses.Add(only);
                m.DialogTopics.Add(t);

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

            string Read(FormKey fk, string? plugin = null, string[]? fields = null, int depth = 1, string? format = null)
                => ReadTools.ReadRecord(svc, fk.ToString(), plugin, fields, depth, false, false, format);

            // ---- the pre-fix hazard, asserted before anything is claimed about the annotation ----
            var aWinner = Read(cellA);
            Check("FIXTURE exhibits the bug: the winner's own Temporary reads EMPTY on a cell the base fills",
                aWinner.Contains("winner=HcOcTop.esp", StringComparison.Ordinal)
                && FieldLine(aWinner, "Temporary") is { } tw && tw.Contains("0 item(s)", StringComparison.Ordinal),
                FieldLine(aWinner, "Temporary"));
            var aBase = Read(cellA, plugin: baseKey.FileName.String);
            Check("…and the base's own body carries 3 — the count the winner does not show",
                FieldLine(aBase, "Temporary") is { } tb && tb.Contains("3 item(s)", StringComparison.Ordinal),
                FieldLine(aBase, "Temporary"));

            // ---- FALSE-EMPTY: the annotation on each of the three fields the base declares ----
            Check("Temporary is annotated: 1 other plugin declares content, most 3, named",
                FieldLine(aWinner, "Temporary") is { } t1
                && t1.Contains("1 other plugin(s) touching this record also declare Temporary content", StringComparison.Ordinal)
                && t1.Contains("(most: 3 in HcOcBase.esm)", StringComparison.Ordinal)
                && t1.Contains(ReadSentences.OwnedChildMerge, StringComparison.Ordinal),
                FieldLine(aWinner, "Temporary"));
            Check("Persistent is annotated too (most: 1) — the annotation is per FIELD, not per record",
                FieldLine(aWinner, "Persistent") is { } p1
                && p1.Contains("also declare Persistent content", StringComparison.Ordinal)
                && p1.Contains("(most: 1 in HcOcBase.esm)", StringComparison.Ordinal),
                FieldLine(aWinner, "Persistent"));
            // SINGULAR: Landscape holds ONE owned child record, not a list — present/absent is its count.
            Check("SINGULAR: Landscape is annotated (most: 1) — a singular owned child, not a list",
                FieldLine(aWinner, "Landscape") is { } l1
                && l1.Contains("also declare Landscape content", StringComparison.Ordinal)
                && l1.Contains("(most: 1 in HcOcBase.esm)", StringComparison.Ordinal),
                FieldLine(aWinner, "Landscape"));
            Check("NavigationMeshes — an owning field NOBODY declares — is NOT annotated",
                FieldLine(aWinner, "NavigationMeshes") is { } n1 && !n1.Contains("also declare", StringComparison.Ordinal),
                FieldLine(aWinner, "NavigationMeshes"));

            // ---- TOKEN: display-only. The value half of the line is exactly what it was before the annotation. ----
            Check("TOKEN: the annotated line still carries the leaf's own unchanged summary, annotation appended after it",
                FieldLine(aWinner, "Temporary") is { } t2
                && t2.StartsWith("Temporary = [list: 0 item(s)]" + ReadEngine.DepthExpandHint + "   (", StringComparison.Ordinal),
                FieldLine(aWinner, "Temporary"));

            // ---- NOT-A-CELL: the same annotation on a topic's INFOs (the field set is the pinned authority) ----
            var topicRead = Read(topic);
            Check("NOT-A-CELL: DialogTopic.Responses is annotated (winner re-lists 1 of the base's 2)",
                FieldLine(topicRead, "Responses") is { } r1
                && r1.Contains("also declare Responses content", StringComparison.Ordinal)
                && r1.Contains("(most: 2 in HcOcBase.esm)", StringComparison.Ordinal),
                FieldLine(topicRead, "Responses"));

            // ---- WINNER-WINS: cell B's winner declares MORE than the plugin below it ----
            var bWinner = Read(cellB);
            Check("WINNER-WINS: no annotation when the read body declares the most (4 vs the base's 1)",
                FieldLine(bWinner, "Temporary") is { } t3
                && t3.Contains("4 item(s)", StringComparison.Ordinal)
                && !t3.Contains("also declare", StringComparison.Ordinal),
                FieldLine(bWinner, "Temporary"));

            // ---- OTHER-NOT-LOWER: the plugin=-scoped read of the base sees the HIGHER plugin's declaration ----
            var bBase = Read(cellB, plugin: baseKey.FileName.String);
            Check("OTHER-NOT-LOWER: a plugin=-scoped read is annotated by a HIGHER plugin's declaration (most: 4 in HcOcTop.esp)",
                FieldLine(bBase, "Temporary") is { } t4
                && t4.Contains("also declare Temporary content", StringComparison.Ordinal)
                && t4.Contains("(most: 4 in HcOcTop.esp)", StringComparison.Ordinal),
                FieldLine(bBase, "Temporary"));

            // ---- SOLE: one declarer, nothing to compare against ----
            var cRead = Read(cellC);
            Check("SOLE: a record only one plugin touches is not annotated",
                FieldLine(cRead, "Temporary") is { } t5 && !t5.Contains("also declare", StringComparison.Ordinal),
                FieldLine(cRead, "Temporary"));

            // ---- UNREQUESTED: the walk is gated on the read's own field lines ----
            var aNarrow = Read(cellA, fields: new[] { "EditorID" });
            Check("UNREQUESTED: fields=[EditorID] on the same cell carries no annotation",
                !aNarrow.Contains("also declare", StringComparison.Ordinal), Trim(aNarrow));

            // ---- NO-CHILDREN: the type that owns nothing — the free path ----
            var weapRead = Read(weapon);
            Check("NO-CHILDREN: a 3-toucher weapon read carries no annotation",
                weapRead.Contains("winner=HcOcTop.esp", StringComparison.Ordinal)
                && !weapRead.Contains("also declare", StringComparison.Ordinal), Trim(weapRead));

            // ---- DEPTH: the summary line still carries it when the container is expanded ----
            var aDeep = Read(cellA, depth: 2);
            Check("DEPTH: at depth=2 the container's own summary line still carries the annotation",
                FieldLine(aDeep, "Temporary") is { } t6 && t6.Contains("also declare Temporary content", StringComparison.Ordinal),
                FieldLine(aDeep, "Temporary"));

            // ---- BOTH TRANSPORTS: one carrier (FieldValue.Display) — json states it as `display`, token untouched ----
            var aJson = Read(cellA, format: "json");
            string? jsonDisplay = null, jsonNote = null;
            using (var doc = JsonDocument.Parse(aJson))
                foreach (var f in doc.RootElement.GetProperty("fields").EnumerateArray())
                    if (f.GetProperty("path").GetString() == "Temporary")
                    {
                        jsonDisplay = f.TryGetProperty("display", out var d) ? d.GetString() : null;
                        jsonNote = f.TryGetProperty("note", out var n) ? n.GetString() : null;
                    }
            Check("JSON: the same sentence rides the json lane's `display`, from the same source",
                jsonDisplay is not null && jsonDisplay.Contains(ReadSentences.OwnedChildMerge, StringComparison.Ordinal)
                && jsonDisplay.Contains("(most: 3 in HcOcBase.esm)", StringComparison.Ordinal), jsonDisplay ?? "(no display)");
            Check("JSON: the leaf's own note is unchanged — the annotation never replaces the value half",
                jsonNote is not null && jsonNote.StartsWith("[list: 0 item(s)]", StringComparison.Ordinal), jsonNote ?? "(no note)");

            // ---- SENTENCE: the content net over ReadSentences ----
            var sentenceBad = SentenceViolations();
            Check("SENTENCE: every ReadSentences const decides ([MustState] phrases or [NoClaims] with a reason) and states them",
                sentenceBad.Count == 0, string.Join(" | ", sentenceBad));

            Console.WriteLine();
            Console.WriteLine($"=== owned-child-content-guard: {_pass} passed, {_fail} failed -> {(_fail == 0 ? "PASS" : "FAIL")} ===");
            return _fail == 0 ? 0 : 1;
        }
        catch (Exception ex) { Console.WriteLine($"   FAIL (unexpected): {ex.GetType().Name}: {ex.Message}"); return 1; }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
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

    /// <summary>The content half of the response-layer net, over <see cref="ReadSentences"/>: every const must
    /// DECIDE — declared phrases, or a stated reason there are none — and a sentence that declares a phrase must
    /// still contain it. The write surface's own arm is the model; this owner is the read surface's, and an
    /// undecorated const FAILS by name rather than passing in silence.</summary>
    static List<string> SentenceViolations()
    {
        var bad = new List<string>();
        foreach (var f in typeof(ReadSentences).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
        {
            if (!f.IsLiteral || f.FieldType != typeof(string)) { bad.Add($"{f.Name}: not a string const (unreadable to this net)"); continue; }
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
