using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// SELF-CONTAINED CI REGRESSION GUARD for the CREATE-TOOL WIRE (housecarl_create_record's parent/collection +
/// the new housecarl_bulk_create batch tool, nested/dialogue plan Layer A). Where nested-create-guard pins the
/// CORE (WritePatchBuilder.CreateRecords) against a bare resolver, this pins the SERVICE LAYER —
/// LoadOrderService.CreateRecords (single, with parent/collection) + CreateRecordsBatch (the array) — driven over a
/// synthetic MO2 instance in temp (the write-mutex-guard synth pattern: real ModOrganizer.ini + profile + a master
/// mod), so the wire's NEW logic (type resolution from a string, parent/collection passthrough, per-record batch
/// aggregation, MO2 folder-per-patch output) runs end-to-end. (The MCP argument binding above the service is generic
/// SDK + already covered by binding-shim-guard; this drives the service methods directly, as write-mutex-guard does.)
/// Run: dotnet run --project src/housecarl-generator -- bulk-create-guard
///
/// Arms (ALL required):
///   FLAT       — a single flat Keyword create still works (the wire didn't break the existing flat path).
///   SINGLE-PARENT — a single create with parent=<an existing master topic's FormID> nests an INFO under it
///                (proves parent/collection passthrough through the service to the core).
///   ONESHOT    — bulk_create of [DialogTopic, DialogResponses parent=<the topic's editorid>] in one call: both
///                created, the INFO under the NEW topic (the same-call sibling one-shot, through the batch wire).
///   BATCH-AON  — a batch whose 2nd spec is un-createable (a nested type with no parent) refuses the WHOLE call,
///                naming the problem, with the valid 1st spec NOT written and no orphan folder (all-or-nothing, Q3).
///   GUIDANCE   — a single nested create with no parent refuses loud and the message guides to parent= / bulk_create
///                (the refreshed CanCreateType copy reaches the user).
///   EXTERIOR-WIRE — create_record 'Cell' with parent=<worldspace> + grid= creates an exterior cell AND the CellShell
///                report rides back (the §4-(b) coordinate-keyed wire: grid= threads service→core; the "you must still
///                provide lighting/terrain/navmesh" teeth fire).
///   INTERIOR-WIRE — create_record 'Cell' with NO parent + NO grid creates an interior cell with its INTERIOR shell report.
///   MULTI        — bulk_create of an exterior + an interior cell lists BOTH in one shell report (the >1-cell path).
/// </summary>
internal static class BulkCreateGuardProbe
{
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("################  REGRESSION GUARD — create-tool wire (create_record parent/collection + bulk_create)  ################");
        Console.WriteLine();
        int fail = 0;
        void Check(bool c, string label) { Console.WriteLine((c ? "  PASS  " : "  FAIL  ") + label); if (!c) fail++; }

        var root = Path.Combine(Path.GetTempPath(), "hc-bulk-create-guard-" + Guid.NewGuid().ToString("N"));
        try
        {
            // --- synthetic MO2 instance with ONE master mod carrying a dialogue topic (the existing-parent fixture). ---
            string instance = Path.Combine(root, "instance");
            string profiles = Path.Combine(instance, "profiles", "Default");
            string mods = Path.Combine(instance, "mods");
            string data = Path.Combine(root, "game", "Data");
            Directory.CreateDirectory(profiles); Directory.CreateDirectory(mods); Directory.CreateDirectory(data);
            File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
                "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
                + Path.Combine(root, "game").Replace(@"\", @"\\") + ")\r\n");

            var mKey = new ModKey("HcBcGdMaster", ModType.Master);
            var modDir = Path.Combine(mods, "MasterMod");
            Directory.CreateDirectory(modDir);
            FormKey topicFk, worldFk;
            {
                var m = new SkyrimMod(mKey, SkyrimRelease.SkyrimSE);
                var topic = m.DialogTopics.AddNew(); topic.EditorID = "HcBcGdTopic";
                topicFk = topic.FormKey;
                var world = m.Worldspaces.AddNew(); world.EditorID = "HcBcGdWorld";   // the exterior-cell parent fixture (coord-keyed wire)
                worldFk = world.FormKey;
                m.BeginWrite.ToPath(Path.Combine(modDir, mKey.FileName.String)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
            }
            File.WriteAllText(Path.Combine(profiles, "loadorder.txt"), "# header\r\n" + mKey.FileName + "\r\n");
            File.WriteAllText(Path.Combine(profiles, "plugins.txt"), "*" + mKey.FileName + "\r\n");
            File.WriteAllText(Path.Combine(profiles, "modlist.txt"), "# header\r\n+MasterMod\r\n");

            var genDir = Path.Combine(root, "corpus-gen");
            CorpusGenerator.GenerateAll(genDir, Path.Combine(root, "corpus-ref"));
            CorpusRulebook.CorpusPath = Path.Combine(genDir, "corpus.json");

            var store = new UserConfigStore(Path.Combine(root, "houseCARL.user.json"));
            using var svc = LoadOrderService.WithInstance(instance, 0, store);
            svc.Stats();   // warm the lazy index once

            // ---- FLAT: single flat Keyword create, no parent ----
            {
                var o = svc.CreateRecords("Keyword", "HcBcGdKw", Array.Empty<BulkOp>(), "HcBcFlat", null);
                Check(o.Success && o.Created.Count == 1 && o.Created[0].FormKey.ID >= 0x800,
                    $"FLAT single flat create still works — {(o.Success ? o.Created[0].FormKey.ToString() : "err=[" + o.Error + "]")}");
            }

            // ---- SINGLE-PARENT: single create_record with parent= an existing master topic ----
            {
                var o = svc.CreateRecords("DialogResponses", "HcBcN2Info", Array.Empty<BulkOp>(), "HcBcSingleParent", null, parent: topicFk.ToString());
                var responses = o.Success ? TopicResponses(o.OutputPath, topicFk) : null;
                bool under = responses is not null && o.Success && responses.Contains(o.Created[0].FormKey);
                Check(o.Success && o.Created.Count == 1 && o.Created[0].FormKey.ID >= 0x800 && under,
                    $"SINGLE-PARENT INFO into existing topic via parent= — {(o.Success ? (under ? "under the topic" : "NOT under topic") : "err=[" + o.Error + "]")}");
            }

            // ---- ONESHOT: bulk_create topic + its first line, parent = same-call sibling editorid ----
            {
                var records = new[]
                {
                    new CreateOp { RecordType = "DialogTopic", Editorid = "HcBcOsTopic" },
                    new CreateOp { RecordType = "DialogResponses", Editorid = "HcBcOsL1", Parent = "HcBcOsTopic",
                        Operations = new[] { new BulkOp { FieldPath = "Prompt", Verb = "Set", Value = "houseCARL one-shot" } } },
                };
                var o = svc.CreateRecordsBatch(records, "HcBcOneShot", null);
                var responses = o.Success ? TopicResponses(o.OutputPath, "HcBcOsTopic") : null;
                bool under = responses is not null && o.Success && o.Created.Count == 2 && responses.Contains(o.Created[1].FormKey);
                Check(o.Success && o.Created.Count == 2 && under,
                    $"ONESHOT bulk_create topic + line (sibling parent) — {(o.Success ? (under ? "line under the new topic" : "NOT under topic") : "err=[" + o.Error + "]")}");
            }

            // ---- BATCH-AON: a batch with one un-createable spec refuses the whole call, writes nothing ----
            {
                var records = new[]
                {
                    new CreateOp { RecordType = "Keyword", Editorid = "HcBcAonKw" },                 // valid
                    new CreateOp { RecordType = "DialogResponses", Editorid = "HcBcAonBad" },        // nested, NO parent → un-createable
                };
                var o = svc.CreateRecordsBatch(records, "HcBcAon", null);
                bool refused = !o.Success && o.Error is not null;
                bool noFolder = !Directory.EnumerateDirectories(mods, "houseCARL - HcBcAon*").Any();
                Check(refused && noFolder,
                    $"BATCH-AON one bad spec refuses the whole batch, nothing written — refused={refused} noFolder={noFolder} err=[{o.Error}]");
            }

            // ---- GUIDANCE: a nested create with no parent guides to parent= / bulk_create ----
            {
                var o = svc.CreateRecords("DialogResponses", "HcBcNoParent", Array.Empty<BulkOp>(), "HcBcGuidance", null);
                bool guided = !o.Success && o.Error is not null
                    && o.Error.Contains("parent", StringComparison.OrdinalIgnoreCase)
                    && o.Error.Contains("bulk_create", StringComparison.OrdinalIgnoreCase);
                Check(guided, $"GUIDANCE nested-with-no-parent refused + guides to parent=/bulk_create — guided={guided} err=[{o.Error}]");
            }

            // ---- EXTERIOR-WIRE: create_record Cell with parent=<worldspace> + grid= → exterior cell + shell report ----
            //      Proves the grid= param threads service→core AND the CellShell teeth fire (Aaron's "you must fill" report).
            {
                var o = svc.CreateRecords("Cell", "HcBcExtCell", Array.Empty<BulkOp>(), "HcBcExt", null, parent: worldFk.ToString(), grid: "1000,-1000");
                var shell = o.CellShell;
                bool ext = shell is not null && shell.Cells.Count == 1 && !shell.Cells[0].Interior && shell.Cells[0].MustProvide.Count > 0;
                Check(o.Success && o.Created.Count == 1 && o.Created[0].FormKey.ID >= 0x800 && ext,
                    $"EXTERIOR-WIRE Cell via parent=worldspace + grid → created + EXTERIOR shell report — {(o.Success ? (ext ? "exterior shell w/ must-provide" : "shell missing/wrong") : "err=[" + o.Error + "]")}");
            }

            // ---- INTERIOR-WIRE: create_record Cell with NO parent + NO grid → interior cell + shell report ----
            {
                var o = svc.CreateRecords("Cell", "HcBcIntCell", Array.Empty<BulkOp>(), "HcBcInt", null);
                var shell = o.CellShell;
                bool inter = shell is not null && shell.Cells.Count == 1 && shell.Cells[0].Interior && shell.Cells[0].MustProvide.Count > 0;
                Check(o.Success && o.Created.Count == 1 && o.Created[0].FormKey.ID >= 0x800 && inter,
                    $"INTERIOR-WIRE Cell with no parent → created + INTERIOR shell report — {(o.Success ? (inter ? "interior shell w/ must-provide" : "shell missing/wrong") : "err=[" + o.Error + "]")}");
            }

            // ---- MULTI: bulk_create an exterior + an interior cell in ONE call → the shell report lists BOTH (the >1-cell
            //      report path, untested by the single-cell arms; PR #94 review nit). ----
            {
                var records = new[]
                {
                    new CreateOp { RecordType = "Cell", Editorid = "HcBcMExt", Parent = worldFk.ToString(), Grid = "200,200" },
                    new CreateOp { RecordType = "Cell", Editorid = "HcBcMInt" },
                };
                var o = svc.CreateRecordsBatch(records, "HcBcMulti", null);
                var shell = o.CellShell;
                bool two = shell is not null && shell.Cells.Count == 2 && shell.Cells.Any(c => c.Interior) && shell.Cells.Any(c => !c.Interior);
                Check(o.Success && o.Created.Count == 2 && two,
                    $"MULTI bulk_create exterior + interior → shell lists BOTH — {(o.Success ? (two ? "2 cells (1 ext, 1 int)" : "shell wrong: count=" + (shell?.Cells.Count.ToString() ?? "null")) : "err=[" + o.Error + "]")}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  guard infrastructure: {ex.GetType().Name}: {(ex.InnerException ?? ex).Message}");
            fail++;
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }

        Console.WriteLine();
        Console.WriteLine($"=== bulk-create-guard: {(fail == 0 ? "PASS" : "FAIL")} ===");
        return fail == 0 ? 0 : 1;
    }

    static List<FormKey>? TopicResponses(string patchPath, FormKey topicFk)
    {
        ISkyrimModGetter? ov = null;
        try
        {
            ov = SkyrimMod.CreateFromBinaryOverlay(patchPath, SkyrimRelease.SkyrimSE);
            var t = ov.DialogTopics.FirstOrDefault(x => x.FormKey == topicFk);
            return t?.Responses.Select(x => x.FormKey).ToList();
        }
        catch { return null; }
        finally { (ov as IDisposable)?.Dispose(); }
    }

    static List<FormKey>? TopicResponses(string patchPath, string topicEditorId)
    {
        ISkyrimModGetter? ov = null;
        try
        {
            ov = SkyrimMod.CreateFromBinaryOverlay(patchPath, SkyrimRelease.SkyrimSE);
            var t = ov.DialogTopics.FirstOrDefault(x => x.EditorID == topicEditorId);
            return t?.Responses.Select(x => x.FormKey).ToList();
        }
        catch { return null; }
        finally { (ov as IDisposable)?.Dispose(); }
    }
}
