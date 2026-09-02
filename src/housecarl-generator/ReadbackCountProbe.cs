using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// SELF-CONTAINED CI REGRESSION GUARD for the bulk_apply composes= Add read-back count (#259). Appending N elements
/// to a list field in ONE op via <c>composes=</c> used to render a verify line that reported only the LAST element as
/// if a single element was added — <c>now 37 (+1), new [36] = …</c> for a 6-element compose — because
/// <c>ReadEngine.TouchedElement</c>'s Add arm hardcoded a <c>+1</c> delta and the single last index. It cost real
/// mid-session doubt ("did only 1 of 6 land?"). The renderer now carries the op's appended count (composes.Count,
/// wired from <c>WritePatchBuilder.DescribeApplied</c>) and reports the whole appended run: <c>now 6 (+6), new [0..5]</c>.
///
/// Drives the REAL in-place write path (<see cref="WritePatchBuilder.ApplyInPlace"/>, the bulk_apply in_place surface)
/// over a masterless plugin with a LeveledItem, in the CompactReadbackProbe pattern, and inspects the per-op
/// <c>Landed</c> descriptor. Arms:
///   * BATCH   — a composes= Add of 6 LeveledItemEntry onto an empty list reports <c>(+6), new [0..5]</c>, NOT (+1).
///     RED before the fix (reported (+1), new [5]), GREEN after.
///   * SINGLE  — a composes= Add of 1 still reports <c>(+1), new [0] = …</c> with the element named (the single-append
///     form is unchanged, and it proves the appended count is 1 for a 1-element compose, not a hardcoded constant).
///   * GROUND TRUTH — re-opening the file, the lists actually hold 6 and 1 entries (the writes were always correct;
///     only the summary text was wrong).
///
/// Run: dotnet run --project src/housecarl-generator -- readback-count-guard
/// </summary>
public static class ReadbackCountProbe
{
    static int _pass, _fail;

    [CiProbe("readback-count-guard")]
    public static int RunGuard(string[] args)
    {
        _pass = 0; _fail = 0;
        Console.WriteLine("################  REGRESSION GUARD — bulk_apply composes= Add read-back count (#259)  ################");
        Console.WriteLine();

        var dir = Path.Combine(Path.GetTempPath(), "hc_readback_count_guard");
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
        Directory.CreateDirectory(dir);

        const string pluginName = "hcReadbackCount.esp";
        var pluginPath = Path.Combine(dir, pluginName);
        const int Batch = 6;

        try
        {
            // ---- A masterless plugin: a weapon to reference, and two LeveledItems with EMPTY Entries lists — one for
            //      the batch compose, one for the single-element control. ----
            var mod = new SkyrimMod(ModKey.FromNameAndExtension(pluginName), SkyrimRelease.SkyrimSE);
            var w = mod.Weapons.AddNew(); w.EditorID = "hcRbWeap"; w.BasicStats = new WeaponBasicStats { Damage = 10 };
            FormKey weapFk = w.FormKey;
            var llBatch = mod.LeveledItems.AddNew(); llBatch.EditorID = "hcRbBatchLL"; FormKey batchFk = llBatch.FormKey;
            var llSingle = mod.LeveledItems.AddNew(); llSingle.EditorID = "hcRbSingleLL"; FormKey singleFk = llSingle.FormKey;
            var llNew = mod.LeveledItems.AddNew(); llNew.EditorID = "hcRbNewLaneLL"; FormKey newFk = llNew.FormKey;
            mod.BeginWrite.ToPath(pluginPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

            using var resolver = LoadOrderResolver.Build(new[] { pluginPath });
            var rulebook = CorpusRulebook.Load(GenerateCorpus(dir));
            string weapFid = $"{weapFk.ID:X6}:{weapFk.ModKey.FileName}";
            Console.WriteLine($"-- masterless '{pluginName}': composing {Batch} entries onto one empty LL, 1 onto another, IN PLACE --");
            Console.WriteLine();

            // A LeveledItemEntry compose spec (the plan's canonical composable element), built from parts.
            StructSpec Entry(int level) => new()
            {
                Type = "LeveledItemEntry",
                Sets = new List<WriteRequest>
                {
                    new() { RecordType = "LeveledItemEntry", Path = new[] { "Data", "Level" },     Verb = "Set", Value = level.ToString() },
                    new() { RecordType = "LeveledItemEntry", Path = new[] { "Data", "Count" },     Verb = "Set", Value = "1" },
                    new() { RecordType = "LeveledItemEntry", Path = new[] { "Data", "Reference" }, Verb = "Set", Value = weapFid },
                },
            };

            // NEW-PLUGIN LANE (the lane #259 was reported on) — the SAME composes= Add of Batch onto an empty LL, but
            // producing a NEW override patch instead of editing in place. Both lanes route the read-back through the
            // same DescribeApplied, so the count fix must hold here too. Run BEFORE the in-place edit (which mutates the
            // source file) so this reads llNew's Entries still empty.
            var patchPath = Path.Combine(dir, "hcReadbackPatch.esp");
            var newLane = WritePatchBuilder.Apply(resolver, rulebook, new[]
            {
                new WritePatchBuilder.PatchEdit
                {
                    Target = newFk, Path = new[] { "Entries" }, Verb = "Add",
                    Structs = Enumerable.Range(1, Batch).Select(Entry).ToArray(),
                },
            }, patchPath, extend: false);
            var newLaneLanded = newLane.Ops.FirstOrDefault(o => o.Target == newFk)?.Landed ?? "";
            Check($"NEW-PLUGIN LANE: composes= Add of {Batch} into a new patch also reports (+{Batch}), new [0..{Batch - 1}]  [{newLane.Error ?? "ok"}]",
                  newLane.Success
                  && newLaneLanded.Contains($"(+{Batch})", StringComparison.Ordinal)
                  && newLaneLanded.Contains($"new [0..{Batch - 1}]", StringComparison.Ordinal));
            Console.WriteLine($"   NEW-PLUGIN landed: {newLaneLanded}");
            Console.WriteLine();

            // ONE in-place call, two edits: a composes= Add of 6, and a composes= Add of 1.
            var edits = new[]
            {
                new WritePatchBuilder.PatchEdit
                {
                    Target = batchFk, Path = new[] { "Entries" }, Verb = "Add",
                    Structs = Enumerable.Range(1, Batch).Select(Entry).ToArray(),
                },
                new WritePatchBuilder.PatchEdit
                {
                    Target = singleFk, Path = new[] { "Entries" }, Verb = "Add",
                    Structs = new[] { Entry(1) },
                },
            };

            var outcome = WritePatchBuilder.ApplyInPlace(resolver, rulebook, edits, pluginPath, pluginName, fullReadback: false);
            Check($"SETUP: in-place ApplyInPlace succeeded  [{outcome.Error ?? "ok"}]", outcome.Success);

            var batchOp = outcome.Ops.FirstOrDefault(o => o.Target == batchFk);
            var singleOp = outcome.Ops.FirstOrDefault(o => o.Target == singleFk);
            var batchLanded = batchOp?.Landed ?? "";
            var singleLanded = singleOp?.Landed ?? "";

            Console.WriteLine($"   BATCH  landed: {batchLanded}");
            Console.WriteLine($"   SINGLE landed: {singleLanded}");
            Console.WriteLine();

            // BATCH: the whole appended run, never "(+1)" for six.
            Check($"BATCH: reports the full appended count (+{Batch}), not (+1)",
                  batchLanded.Contains($"(+{Batch})", StringComparison.Ordinal) && !batchLanded.Contains("(+1)", StringComparison.Ordinal));
            Check($"BATCH: reports the appended index RANGE [0..{Batch - 1}]",
                  batchLanded.Contains($"new [0..{Batch - 1}]", StringComparison.Ordinal));

            // SINGLE: the single-append form is unchanged — (+1), new [0] = <element>. (It's the BATCH arm that proves
            // the count is WIRED from Structs.Count rather than a constant; this arm pins that a 1-element compose keeps
            // the element-naming single form, not the range form.)
            Check("SINGLE: a 1-element compose still reports (+1), new [0] = <element>",
                  singleLanded.Contains("(+1)", StringComparison.Ordinal)
                  && singleLanded.Contains("new [0] = ", StringComparison.Ordinal)
                  && !singleLanded.Contains("..", StringComparison.Ordinal));

            // GROUND TRUTH: the writes themselves were always correct — only the summary text was at issue.
            Check($"GROUND TRUTH: on disk the batch LL holds {Batch} entries and the single LL holds 1",
                  EntryCount(pluginPath, batchFk) == Batch && EntryCount(pluginPath, singleFk) == 1);

            Console.WriteLine();
            Console.WriteLine($"=== readback-count-guard: {_pass} passed, {_fail} failed -> {(_fail == 0 ? "PASS" : "FAIL")} ===");
            return _fail == 0 ? 0 : 1;
        }
        catch (Exception ex) { Console.WriteLine($"   FAIL (unexpected): {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}"); return 1; }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    static int? EntryCount(string path, FormKey llFk)
    {
        ISkyrimModGetter? ov = null;
        try
        {
            ov = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
            return ov.LeveledItems.FirstOrDefault(x => x.FormKey == llFk)?.Entries?.Count ?? 0;
        }
        catch { return null; }
        finally { (ov as IDisposable)?.Dispose(); }
    }

    static string GenerateCorpus(string dir)
    {
        var genDir = Path.Combine(dir, "corpus-gen");
        CorpusGenerator.GenerateAll(genDir, Path.Combine(dir, "corpus-ref"));
        return Path.Combine(genDir, "corpus.json");
    }

    static void Check(string label, bool ok)
    {
        Console.WriteLine($"   [{(ok ? "PASS" : "FAIL")}] {label}");
        if (ok) _pass++; else _fail++;
    }
}
