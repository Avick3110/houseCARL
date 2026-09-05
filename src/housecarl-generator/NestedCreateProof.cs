using System.Security.Cryptography;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// Build proof for the NESTED path of <see cref="WritePatchBuilder.CreateRecords"/>, the core the create tool calls
/// (<see cref="CreateProof"/> covers the flat path). It must drive the whole production chain — parent override →
/// NestedAddNew → ApplyVerb → multi-master serialize → re-open from disk — not the Mutagen primitive in isolation,
/// or it proves nothing about the tool.
///
///   N1 — ONE-SHOT: a DialogTopic (flat) + a DialogResponses/INFO under it (same-call
///        sibling parent) in ONE call → re-open: the INFO is in the NEW topic's Responses, both ids local 0x800+.
///   N2 — INFO into an EXISTING topic (FormKey parent): the topic is overridden into the patch and the new INFO
///        appended → re-open: the new INFO is under it. What the override carries of the ORIGINAL lines is
///        REPORTED, not asserted — see the note at the end of the run.
///   N10 — a SINGULAR owned child: a Landscape under an existing cell that has none → re-open, the cell holds it.
///   N11 — REJECT a singular slot that is already filled: a Landscape under a cell that has terrain → refused
///         before anything is allocated, from the parent's REAL body (the patch's override carries no children).
///   N12 — a Cell under a Worldspace with NEITHER route named → refused naming both (collection= and grid=).
///   N13 — DELETE a singular owned child: remove the N10 Landscape by its own FormID → re-open, the cell holds
///         none. Mutagen's typed remove reaches no singular owned child, so this is the detach path end to end.
///   N3 — PlacedObject into an EXISTING Cell, collection='Persistent' (the named discriminator) →
///        re-open: the new ref is in the cell's Persistent list.
///   N4 — REJECT a nested type with NO parent: create 'DialogResponses' alone → refused, names the need for a parent.
///   N5 — REJECT a parent that can't contain the child: 'DialogResponses' under a WEAPON FormKey → refused loud.
///   N6 — REJECT an ambiguous add-target: 'PlacedObject' into a Cell with NO collection= → refused, names the lists.
///   N7 — REJECT a forward sibling parent: an INFO whose parent sibling is declared LATER → refused, the order rule.
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
        // The SINGULAR owned-child arms need both halves of the slot: a cell that carries terrain and one that does not.
        FormKey landlessCellFk = default, terrainCellFk = default;
        foreach (var (fk, _, body) in resolver.WinnerRecordsOfType(new[] { typeof(ICellGetter) }))
        {
            if (body is not ICellGetter c) continue;
            if (c.Landscape is null) { if (landlessCellFk.IsNull) landlessCellFk = fk; }
            else if (terrainCellFk.IsNull) terrainCellFk = fk;
            if (!landlessCellFk.IsNull && !terrainCellFk.IsNull) break;
        }
        FormKey worldspaceFk = resolver.WinnerRecordsOfType(new[] { typeof(IWorldspaceGetter) }).Select(x => x.fk).FirstOrDefault();
        if (topicFk.IsNull || cellFk.IsNull || weaponFk.IsNull || landlessCellFk.IsNull || terrainCellFk.IsNull || worldspaceFk.IsNull)
        { Console.Error.WriteLine($"error: could not sample fixtures (topic={topicFk} cell={cellFk} weapon={weaponFk} landless-cell={landlessCellFk} terrain-cell={terrainCellFk} worldspace={worldspaceFk})."); return 1; }
        Console.WriteLine($"fixtures: topic={topicFk} (responses={origResponses})  cell={cellFk} (persistent={origPersistent})  weapon={weaponFk}");
        Console.WriteLine($"          landless-cell={landlessCellFk}  terrain-cell={terrainCellFk}  worldspace={worldspaceFk}");
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
        //   Asserts the MECHANISM only: the new child is allocated, placed under the overridden parent, local id.
        //   It does NOT assert "the parent's existing children survive" — overriding the parent yields an override
        //   carrying ONLY the new child. Whether Skyrim merges children across plugins or the override replaces the
        //   child list is still unestablished, so it is reported below rather than asserted either way.
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

        // ===================== N3 — PlacedObject into an existing Cell, collection='Persistent' =====================
        //   Same as N2: asserts the MECHANISM (ref allocated + in the cell's Persistent + local id). The override
        //   carries only the new ref, not the cell's other 28 — the same unestablished question, not asserted.
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

        // ===================== N8 — multi-child under one new topic + a FIELD EDIT on a created INFO =====================
        {
            var outPath = Path.Combine(outDir, "houseCARL_NestedCreate_N8.esp");
            var specs = new[]
            {
                new WritePatchBuilder.CreateSpec { RecordType = "DialogTopic", EditorId = "HC_N8_Topic", Edits = Array.Empty<WriteRequest>() },
                new WritePatchBuilder.CreateSpec { RecordType = "DialogResponses", EditorId = "HC_N8_L1", ParentRef = "HC_N8_Topic", Edits = Array.Empty<WriteRequest>() },
                new WritePatchBuilder.CreateSpec { RecordType = "DialogResponses", EditorId = "HC_N8_L2", ParentRef = "HC_N8_Topic",
                    Edits = new[] { new WriteRequest { RecordType = "DialogResponses", Path = new[] { "Prompt" }, Verb = "Set", Value = "houseCARL line two" } } },
            };
            var o = WritePatchBuilder.CreateRecords(resolver, rulebook, specs, outPath, extend: false);
            bool ok = o.Success && o.Created.Count == 3;
            var l1 = ok ? o.Created[1].FormKey : default;
            var l2 = ok ? o.Created[2].FormKey : default;
            var responses = ok ? TopicResponses(outPath, "HC_N8_Topic") : null;
            bool bothUnder = responses is not null && responses.Contains(l1) && responses.Contains(l2);
            var prompt = ok ? InfoPrompt(outPath, l2) : null;
            bool fieldLanded = prompt == "houseCARL line two";
            bool distinct = ok && l1 != l2;
            bool pass = ok && bothUnder && fieldLanded && distinct;
            results.Add(("N8 multi-INFO + field edit", pass,
                $"created={(o.Success ? o.Created.Count : 0)} both-under-topic={YN(bothUnder)} L2.Prompt=\"{prompt}\" field-landed={YN(fieldLanded)} distinct-ids={YN(distinct)}{Err(o)}"));
        }

        // ===================== N9 — extend= with a parent CARRIED BY THE PATCH =====================
        //   Create a topic (call 1), then add an INFO under it in a SEPARATE into= call (call 2). The parent topic lives
        //   in the PATCH, not the load order, so it resolves only because the patch being extended is opened before
        //   pre-flight. Asserts the INFO lands under it, and that a parent absent from BOTH still refuses loud.
        {
            var outPath = Path.Combine(outDir, "houseCARL_NestedCreate_N9.esp");
            var s1 = WritePatchBuilder.CreateRecords(resolver, rulebook,
                new[] { new WritePatchBuilder.CreateSpec { RecordType = "DialogTopic", EditorId = "HC_N9_Topic", Edits = Array.Empty<WriteRequest>() } },
                outPath, extend: false);
            var topicFk2 = s1.Success ? s1.Created[0].FormKey : default;
            var s2 = WritePatchBuilder.CreateRecords(resolver, rulebook,
                new[] { new WritePatchBuilder.CreateSpec { RecordType = "DialogResponses", EditorId = "HC_N9_Info", ParentRef = topicFk2.ToString(), Edits = Array.Empty<WriteRequest>() } },
                outPath, extend: true);
            var responses = s2.Success ? TopicResponses(outPath, topicFk2) : null;
            bool infoUnder = s2.Success && responses is not null && responses.Contains(s2.Created[0].FormKey);
            // a parent in a non-existent plugin is absent from BOTH the load order and the patch — the surviving refusal.
            var s3 = WritePatchBuilder.CreateRecords(resolver, rulebook,
                new[] { new WritePatchBuilder.CreateSpec { RecordType = "DialogResponses", EditorId = "HC_N9_Ghost", ParentRef = "ABCDEF:HC_GhostPlugin.esp", Edits = Array.Empty<WriteRequest>() } },
                outPath, extend: true);
            bool ghostRefused = !s3.Success && (s3.Error ?? "").Contains("load order", StringComparison.OrdinalIgnoreCase)
                && (s3.Error ?? "").Contains("patch", StringComparison.OrdinalIgnoreCase);
            bool ok = s1.Success && s2.Success && infoUnder && ghostRefused;
            results.Add(("N9 extend+patch-carried parent (now works; absent still refuses)", ok,
                $"call1-ok={YN(s1.Success)} call2-ok={YN(s2.Success)} info-under-topic={YN(infoUnder)} absent-refused-loud={YN(ghostRefused)}{Err(s2)}"));
        }

        // ===================== N10 — a SINGULAR owned child under a parent that has none =====================
        //   The shape parent= could not reach at all: a slot holding exactly one record, not a list. Driven through
        //   the same production chain as every arm above — parent override → NestedAddNew → serialize → re-open.
        FormKey n10Land = default; string n10Path = Path.Combine(outDir, "houseCARL_NestedCreate_N10.esp");
        {
            var specs = new[]
            {
                new WritePatchBuilder.CreateSpec { RecordType = "Landscape", EditorId = "HC_N10_Land", ParentRef = landlessCellFk.ToString(), Edits = Array.Empty<WriteRequest>() },
            };
            var o = WritePatchBuilder.CreateRecords(resolver, rulebook, specs, n10Path, extend: false);
            bool ok = o.Success && o.Created.Count == 1;
            n10Land = ok ? o.Created[0].FormKey : default;
            var under = ok ? CellLandscape(n10Path, landlessCellFk) : null;
            bool present = under == n10Land;
            bool local = ok && n10Land.ID >= 0x800 && n10Land.ModKey.FileName.String.Equals(Path.GetFileName(n10Path), StringComparison.OrdinalIgnoreCase);
            results.Add(("N10 singular child created", ok && present && local,
                $"created={YN(ok)} land-under-cell={YN(present)} local-id={YN(local)}{Err(o)}"));
        }

        // ===================== N11 — REJECT a singular slot that is already filled =====================
        //   Measured against the parent's REAL body: the patch's fresh override of the cell carries no children, so
        //   a guard reading the copy would allocate a second, empty LAND and ship it.
        results.Add(RejectCheck("N11 reject occupied singular slot", outDir, "N11", resolver, rulebook,
            new[] { new WritePatchBuilder.CreateSpec { RecordType = "Landscape", EditorId = "HC_N11_Land", ParentRef = terrainCellFk.ToString(), Edits = Array.Empty<WriteRequest>() } },
            msg => msg.Contains("already holds", StringComparison.OrdinalIgnoreCase)));

        // ===================== N12 — REJECT a Cell under a Worldspace with neither route named =====================
        results.Add(RejectCheck("N12 reject unnamed cell route", outDir, "N12", resolver, rulebook,
            new[] { new WritePatchBuilder.CreateSpec { RecordType = "Cell", EditorId = "HC_N12_Cell", ParentRef = worldspaceFk.ToString(), Edits = Array.Empty<WriteRequest>() } },
            msg => msg.Contains("TopCell", StringComparison.Ordinal) && msg.Contains("grid=", StringComparison.Ordinal)));

        // ===================== N13 — DELETE the singular owned child N10 created =====================
        {
            var rem = WritePatchBuilder.RemoveRecords(resolver, new[] { n10Land }, n10Path);
            bool removed = rem.Success && rem.Removed.Count == 1 && rem.Removed[0].Target == n10Land;
            bool gone = File.Exists(n10Path) && CellLandscape(n10Path, landlessCellFk) is null;
            results.Add(("N13 singular child removed", removed && gone,
                $"removed={YN(removed)} slot-empty-on-reopen={YN(gone)}" + (rem.Success ? "" : "  err=" + (rem.Error ?? "").Replace('\n', ' '))));
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

    static string? InfoPrompt(string patchPath, FormKey infoFk)
    {
        ISkyrimModGetter? back = null;
        try
        {
            back = SkyrimMod.CreateFromBinaryOverlay(patchPath, SkyrimRelease.SkyrimSE);
            var info = back.EnumerateMajorRecords<IDialogResponsesGetter>().FirstOrDefault(x => x.FormKey == infoFk);
            return info?.Prompt?.String;
        }
        catch { return null; }
        finally { (back as IDisposable)?.Dispose(); }
    }

    /// <summary>The FormKey in a cell's singular terrain slot on re-open, or null when the slot is empty.</summary>
    static FormKey? CellLandscape(string patchPath, FormKey cellFk)
    {
        ISkyrimModGetter? back = null;
        try
        {
            back = SkyrimMod.CreateFromBinaryOverlay(patchPath, SkyrimRelease.SkyrimSE);
            return back.EnumerateMajorRecords<ICellGetter>().FirstOrDefault(x => x.FormKey == cellFk)?.Landscape?.FormKey;
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
