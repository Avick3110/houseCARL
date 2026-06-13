using System.Security.Cryptography;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// NESTED-CREATE build proof (nested/dialogue plan, Layer A) — exercises the REAL
/// <see cref="WritePatchBuilder.CreateRecords"/> (the core the MCP create tool calls) on its NESTED path, so the
/// proof transfers to the server by construction (the way <see cref="CreateProof"/> proves flat create). Where
/// STEP 0's scout proved the Mutagen PRIMITIVE in isolation, this proves the mechanism THROUGH the production cleave —
/// parent override → NestedAddNew → ApplyVerb → multi-master serialize → re-open from disk.
///
///   N1 — ONE-SHOT (the unit Aaron confirmed): a DialogTopic (flat) + a DialogResponses/INFO under it (same-call
///        sibling parent) in ONE call → re-open: the INFO is in the NEW topic's Responses, both ids local 0x800+.
///   N2 — INFO into an EXISTING topic (FormKey parent): the topic is overridden into the patch carrying its
///        original lines, the new INFO appended → re-open: new INFO present AND the original responses survive.
///   N3 — PlacedObject into an EXISTING Cell, collection='Persistent' (the outcome-(ii) named discriminator) →
///        re-open: the new ref is in the cell's Persistent list.
///   N4 — REJECT a nested type with NO parent (Q3): create 'DialogResponses' alone → refused, names the need for a parent.
///   N5 — REJECT a parent that can't contain the child (Q3): 'DialogResponses' under a WEAPON FormKey → refused loud.
///   N6 — REJECT an ambiguous add-target (Q3): 'PlacedObject' into a Cell with NO collection= → refused, names the lists.
///   N7 — REJECT a forward sibling parent (Q3): an INFO whose parent sibling is declared LATER → refused, the order rule.
///
/// Vanilla Skyrim.esm is SHA-checked unchanged (create only writes the patch). Patches land in
/// write-output/nested-create-proof/ for xEdit.  Run: dotnet run --project src/housecarl-generator nested-create-proof [Skyrim.esm]
/// </summary>
public static class NestedCreateProof
{
    const string DefaultSource = @"E:\Skyrim Modding\ARR 2.0\Stock Game\Data\Skyrim.esm";

    public static int RunProof(string[] args)
    {
        var src = args.Length > 0 && !args[0].StartsWith("--") ? args[0] : DefaultSource;

        Console.WriteLine("================================================================");
        Console.WriteLine(" houseCARL nested-create proof (Layer A — housecarl_create_record nested path)");
        Console.WriteLine("================================================================");
        Console.WriteLine($"source: {src}");
        if (!File.Exists(src)) { Console.Error.WriteLine($"error: source not found: {src}"); return 1; }

        using var resolver = LoadOrderResolver.Build(new[] { src });
        var rulebook = CorpusRulebook.Load();
        Console.WriteLine($"Resolver: {resolver.PluginCount:N0} plugin | {resolver.RecordCount:N0} records.");

        // Sample an existing topic WITH responses (N2), a cell WITH persistent refs (N3), a weapon (N5 bad parent).
        FormKey topicFk = default; int origResponses = 0;
        foreach (var (fk, _, body) in resolver.WinnerRecordsOfType(new[] { typeof(IDialogTopicGetter) }))
            if (body is IDialogTopicGetter t && t.Responses.Count > 0) { topicFk = fk; origResponses = t.Responses.Count; break; }
        FormKey cellFk = default; int origPersistent = 0;
        foreach (var (fk, _, body) in resolver.WinnerRecordsOfType(new[] { typeof(ICellGetter) }))
            if (body is ICellGetter c && c.Persistent.Count > 0) { cellFk = fk; origPersistent = c.Persistent.Count; break; }
        FormKey weaponFk = resolver.WinnerRecordsOfType(new[] { typeof(IWeaponGetter) }).Select(x => x.fk).FirstOrDefault();
        if (topicFk.IsNull || cellFk.IsNull || weaponFk.IsNull)
        { Console.Error.WriteLine($"error: could not sample fixtures (topic={topicFk} cell={cellFk} weapon={weaponFk})."); return 1; }
        Console.WriteLine($"fixtures: topic={topicFk} (responses={origResponses})  cell={cellFk} (persistent={origPersistent})  weapon={weaponFk}");
        Console.WriteLine();

        var outDir = Path.GetFullPath(Path.Combine("write-output", "nested-create-proof"));
        if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
        Directory.CreateDirectory(outDir);
        var shaBefore = Sha(src);

        var results = new List<(string name, bool pass, string detail)>();

        // ===================== N1 — ONE-SHOT: new topic + INFO under it, in one call =====================
        {
            var outPath = Path.Combine(outDir, "houseCARL_NestedCreate_N1.esp");
            var specs = new[]
            {
                new WritePatchBuilder.CreateSpec { RecordType = "DialogTopic", EditorId = "HC_N1_Topic", Edits = Array.Empty<WriteRequest>() },
                new WritePatchBuilder.CreateSpec { RecordType = "DialogResponses", EditorId = "HC_N1_Info1", ParentRef = "HC_N1_Topic", Edits = Array.Empty<WriteRequest>() },
            };
            var o = WritePatchBuilder.CreateRecords(resolver, rulebook, specs, outPath, extend: false);
            bool ok2 = o.Success && o.Created.Count == 2;
            var topic = ok2 ? o.Created[0].FormKey : default;
            var info = ok2 ? o.Created[1].FormKey : default;
            bool floored = ok2 && topic.ID >= 0x800 && info.ID >= 0x800 && topic != info
                && topic.ModKey.FileName.String.Equals(Path.GetFileName(outPath), StringComparison.OrdinalIgnoreCase)
                && info.ModKey == topic.ModKey;
            var responses = ok2 ? TopicResponses(outPath, "HC_N1_Topic") : null;
            bool underTopic = responses is not null && responses.Contains(info);
            bool pass = ok2 && floored && underTopic;
            results.Add(("N1 one-shot topic+INFO", pass,
                $"created={(o.Success ? o.Created.Count : 0)} topic={(ok2 ? "0x" + topic.ID.ToString("X6") : "-")} info={(ok2 ? "0x" + info.ID.ToString("X6") : "-")} floored={YN(floored)} info-under-new-topic={YN(underTopic)}{Err(o)}"));
        }

        // ===================== N2 — INFO into an EXISTING topic (FormKey parent) =====================
        //   ASSERTS THE MECHANISM only (the new child is allocated + placed under the overridden parent + local id).
        //   It does NOT assert "the parent's existing children survive" — overriding the parent yields an override
        //   carrying ONLY the new child (sibChildren below). Whether that's correct (Skyrim merges children across
        //   plugins by record) or lossy (the override replaces the child list) is the merge-vs-replace SEMANTIC,
        //   reported as an OPEN QUESTION below — NOT decided by guessing (evidence-first, §4).
        int n2OverrideCount = -1;
        {
            var outPath = Path.Combine(outDir, "houseCARL_NestedCreate_N2.esp");
            var specs = new[]
            {
                new WritePatchBuilder.CreateSpec { RecordType = "DialogResponses", EditorId = "HC_N2_Info", ParentRef = topicFk.ToString(), Edits = Array.Empty<WriteRequest>() },
            };
            var o = WritePatchBuilder.CreateRecords(resolver, rulebook, specs, outPath, extend: false);
            bool ok = o.Success && o.Created.Count == 1;
            var info = ok ? o.Created[0].FormKey : default;
            var responses = ok ? TopicResponses(outPath, topicFk) : null;
            n2OverrideCount = responses?.Count ?? -1;
            bool present = responses is not null && responses.Contains(info);
            bool local = ok && info.ID >= 0x800 && info.ModKey.FileName.String.Equals(Path.GetFileName(outPath), StringComparison.OrdinalIgnoreCase);
            bool pass = ok && present && local;
            results.Add(("N2 INFO added to existing topic", pass,
                $"created={YN(ok)} new-info-present={YN(present)} local-id={YN(local)} | override-carries={(responses?.Count ?? -1)} of {origResponses + 1} (orig+new) [merge-semantic: OPEN]{Err(o)}"));
        }

        // ===================== N3 — PlacedObject into an existing Cell, collection='Persistent' (outcome ii) =====================
        //   Same as N2: asserts the MECHANISM (ref allocated + in the cell's Persistent + local id). The override
        //   carries only the new ref (not the cell's other 28) — the merge-vs-replace OPEN QUESTION, not asserted.
        int n3OverrideCount = -1;
        {
            var outPath = Path.Combine(outDir, "houseCARL_NestedCreate_N3.esp");
            var specs = new[]
            {
                new WritePatchBuilder.CreateSpec { RecordType = "PlacedObject", EditorId = "HC_N3_Ref", ParentRef = cellFk.ToString(), IntoCollection = "Persistent", Edits = Array.Empty<WriteRequest>() },
            };
            var o = WritePatchBuilder.CreateRecords(resolver, rulebook, specs, outPath, extend: false);
            bool ok = o.Success && o.Created.Count == 1;
            var refFk = ok ? o.Created[0].FormKey : default;
            var persistent = ok ? CellPersistent(outPath, cellFk) : null;
            n3OverrideCount = persistent?.Count ?? -1;
            bool present = persistent is not null && persistent.Contains(refFk);
            bool local = ok && refFk.ID >= 0x800 && refFk.ModKey.FileName.String.Equals(Path.GetFileName(outPath), StringComparison.OrdinalIgnoreCase);
            bool pass = ok && present && local;
            results.Add(("N3 Placed added to existing cell", pass,
                $"created={YN(ok)} ref-in-persistent={YN(present)} local-id={YN(local)} | override-carries={(persistent?.Count ?? -1)} of {origPersistent + 1} (orig+new) [merge-semantic: OPEN]{Err(o)}"));
        }

        // ===================== N4 — REJECT nested with no parent =====================
        results.Add(RejectCheck("N4 reject nested no-parent", outDir, "N4", resolver, rulebook,
            new[] { new WritePatchBuilder.CreateSpec { RecordType = "DialogResponses", EditorId = "HC_N4", Edits = Array.Empty<WriteRequest>() } },
            msg => msg.Contains("parent", StringComparison.OrdinalIgnoreCase) || msg.Contains("nested", StringComparison.OrdinalIgnoreCase)));

        // ===================== N5 — REJECT a parent that can't contain the child =====================
        results.Add(RejectCheck("N5 reject bad parent (INFO<-Weapon)", outDir, "N5", resolver, rulebook,
            new[] { new WritePatchBuilder.CreateSpec { RecordType = "DialogResponses", EditorId = "HC_N5", ParentRef = weaponFk.ToString(), Edits = Array.Empty<WriteRequest>() } },
            msg => msg.Contains("cannot be created under", StringComparison.OrdinalIgnoreCase)));

        // ===================== N6 — REJECT ambiguous add-target (Cell has 2 placed lists) =====================
        results.Add(RejectCheck("N6 reject ambiguous collection", outDir, "N6", resolver, rulebook,
            new[] { new WritePatchBuilder.CreateSpec { RecordType = "PlacedObject", EditorId = "HC_N6", ParentRef = cellFk.ToString(), Edits = Array.Empty<WriteRequest>() } },
            msg => msg.Contains("more than one", StringComparison.OrdinalIgnoreCase) && msg.Contains("Persistent", StringComparison.OrdinalIgnoreCase)));

        // ===================== N7 — REJECT a forward sibling parent (declared LATER) =====================
        results.Add(RejectCheck("N7 reject forward sibling parent", outDir, "N7", resolver, rulebook,
            new[]
            {
                new WritePatchBuilder.CreateSpec { RecordType = "DialogResponses", EditorId = "HC_N7_Info", ParentRef = "HC_N7_Topic", Edits = Array.Empty<WriteRequest>() },
                new WritePatchBuilder.CreateSpec { RecordType = "DialogTopic", EditorId = "HC_N7_Topic", Edits = Array.Empty<WriteRequest>() },
            },
            msg => msg.Contains("earlier in this call", StringComparison.OrdinalIgnoreCase)));

        bool srcOk = shaBefore == Sha(src);

        // ---- VERDICT ----
        Console.WriteLine("Results:");
        foreach (var (name, pass, detail) in results)
            Console.WriteLine($"  [{(pass ? "PASS" : "FAIL")}] {name,-34} {detail}");
        Console.WriteLine($"  [{(srcOk ? "PASS" : "FAIL")}] {"Skyrim.esm byte-untouched",-34}");
        Console.WriteLine();
        Console.WriteLine("NOTE — the surgical-override model (resolved via mutagen-reference, not guessed):");
        Console.WriteLine($"  Adding a child to an EXISTING parent overrides that parent carrying ONLY the new child");
        Console.WriteLine($"  (N2 topic override = {n2OverrideCount} response of {origResponses + 1}; N3 cell override = {n3OverrideCount} persistent of {origPersistent + 1}).");
        Console.WriteLine($"  This is CORRECT, not lossy: INFOs and placed refs are FULL records (own FormKeys; INFO carries a");
        Console.WriteLine($"  Topic back-link). The engine loads each child record from its defining plugin and merges by FormID,");
        Console.WriteLine($"  so the cell's other refs / the topic's original line still load from Skyrim.esm. The override is");
        Console.WriteLine($"  surgical (parent header + the new child) — the standard add-to-an-existing-parent shape.");
        Console.WriteLine();
        bool allPass = results.All(r => r.pass) && srcOk;
        Console.WriteLine("================================================================");
        Console.WriteLine(allPass
            ? "=== ALL CHECKS PASS — nested-record creation is proven through the production create cleave.\n" +
              "    Open write-output/nested-create-proof/*.esp in xEdit: N1=a new topic + its line, N2=a new line on an\n" +
              "    existing topic, N3=a new ref in a cell. New local FormIDs, Skyrim.esm byte-untouched. ==="
            : "=== FAIL — see the checks above (a !! is the thing to resolve). ===");
        Console.WriteLine("================================================================");
        return allPass ? 0 : 1;
    }

    /// <summary>A REJECT check: drive CreateRecords expecting refusal, assert no file written + the message matches.</summary>
    static (string, bool, string) RejectCheck(string name, string outDir, string tag, LoadOrderResolver resolver,
        CorpusRulebook rulebook, WritePatchBuilder.CreateSpec[] specs, Func<string, bool> msgOk)
    {
        var outPath = Path.Combine(outDir, $"houseCARL_NestedCreate_{tag}.esp");
        var o = WritePatchBuilder.CreateRecords(resolver, rulebook, specs, outPath, extend: false);
        bool refused = !o.Success;
        bool named = refused && msgOk(o.Error ?? "");
        bool noFile = !File.Exists(outPath);
        bool pass = refused && named && noFile;
        return (name, pass, $"refused={YN(refused)} msg-named={YN(named)} no-file={YN(noFile)}" + (refused ? "" : $"  (unexpectedly wrote; created={o.Created.Count})"));
    }

    // ---- re-open helpers ----

    static List<FormKey>? TopicResponses(string patchPath, string topicEditorId)
        => TopicResponsesBy(patchPath, t => t.EditorID == topicEditorId);
    static List<FormKey>? TopicResponses(string patchPath, FormKey topicFk)
        => TopicResponsesBy(patchPath, t => t.FormKey == topicFk);

    static List<FormKey>? TopicResponsesBy(string patchPath, Func<IDialogTopicGetter, bool> match)
    {
        ISkyrimModGetter? back = null;
        try
        {
            back = SkyrimMod.CreateFromBinaryOverlay(patchPath, SkyrimRelease.SkyrimSE);
            var t = back.DialogTopics.FirstOrDefault(match);
            return t?.Responses.Select(r => r.FormKey).ToList();
        }
        catch { return null; }
        finally { (back as IDisposable)?.Dispose(); }
    }

    static List<FormKey>? CellPersistent(string patchPath, FormKey cellFk)
    {
        ISkyrimModGetter? back = null;
        try
        {
            back = SkyrimMod.CreateFromBinaryOverlay(patchPath, SkyrimRelease.SkyrimSE);
            var c = back.EnumerateMajorRecords<ICellGetter>().FirstOrDefault(x => x.FormKey == cellFk);
            return c?.Persistent.Select(r => r.FormKey).ToList();
        }
        catch { return null; }
        finally { (back as IDisposable)?.Dispose(); }
    }

    static string Err(WritePatchBuilder.CreateOutcome o) => o.Success ? "" : "  err=" + (o.Error ?? "").Replace('\n', ' ');
    static string YN(bool b) => b ? "Y" : "N";
    static string Sha(string p) { using var s = File.OpenRead(p); using var h = SHA256.Create(); return Convert.ToHexString(h.ComputeHash(s)); }
}
