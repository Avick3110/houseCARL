using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// SELF-CONTAINED CI REGRESSION GUARD for the DialogTopic SNAM subtype-marker fix (issue #131). A topic with a
/// Subtype but a blank SNAM marker (0000) is a GUARANTEED load CTD — the engine buckets topics by that 4-char
/// marker. This guard pins BOTH sides of the fix so they can't drift:
///   • the authoritative index→marker table (DialogueSubtype) — structural shape + the reporter-confirmed anchors,
///   • the create-path AUTO-FILL — create a topic through the service and read the written SNAM back off disk,
///   • the create-path NON-OVERRIDE — an explicit SubtypeName is never clobbered,
///   • the validator ESCALATION — housecarl_validate_dialogue reports a blank marker as a PROBLEM, not a neutral fact.
/// Driven over a synthetic MO2 instance in temp (the bulk-create-guard synth pattern; no game files needed).
/// Run: dotnet run --project src/housecarl-generator -- dialogue-subtype-marker-guard
///
/// Arms (ALL required):
///   TABLE-SHAPE  — 103 contiguous entries, every marker a distinct 4-char signature.
///   TABLE-ANCHOR — the reporter-confirmed + common markers (Custom→CUST, Hello→HELO, Goodbye→GBYE, Idle→IDLE,
///                  ForceGreet→PFGT, Rumors→RUMO, Scene→SCEN).
///   AUTOFILL     — create a DialogTopic with Subtype=Hello and NO marker → the written SNAM is HELO, and the
///                  auto-fill is REPORTED as an op (not silent, Q3).
///   DEFAULT-CUST — create a bare DialogTopic (Subtype defaults to Custom) → the written SNAM is CUST (matches xEdit's
///                  own SNAM default; the dialogue-authoring skill's Custom example no longer ships a crash).
///   EXPLICIT-WINS— create a DialogTopic with an explicit SubtypeName=GBYE → it is NOT overridden to CUST.
///   VALIDATE-BLANK — a topic shipped with a blank marker (a raw pre-fix insert) → validate_dialogue raises a Problem
///                  naming the SNAM marker; a topic WITH a marker raises no such Problem.
/// </summary>
internal static class DialogueSubtypeMarkerGuardProbe
{
    [CiProbe("dialogue-subtype-marker-guard")]
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("################  REGRESSION GUARD — DialogTopic SNAM subtype marker (#131)  ################");
        Console.WriteLine();
        int fail = 0;
        void Check(bool c, string label) { Console.WriteLine((c ? "  PASS  " : "  FAIL  ") + label); if (!c) fail++; }

        // ---- TABLE-SHAPE: 103 contiguous entries, all distinct 4-char signatures ----
        {
            int n = DialogueSubtype.Count;
            var tags = new List<string>();
            bool allFour = true;
            for (int i = 0; i < n; i++)
            {
                var t = DialogueSubtype.MarkerFor(i);
                if (t is null || t.Length != 4) { allFour = false; break; }
                tags.Add(t);
            }
            bool distinct = tags.Count == tags.Distinct().Count();
            bool outOfRange = DialogueSubtype.MarkerFor(-1) is null && DialogueSubtype.MarkerFor(n) is null;
            Check(n == 103 && allFour && distinct && outOfRange,
                $"TABLE-SHAPE 103 contiguous 4-char distinct markers — count={n} allFour={allFour} distinct={distinct} rangeSafe={outOfRange}");
        }

        // ---- TABLE-ANCHOR: reporter-confirmed + common markers ----
        {
            var anchors = new (DialogTopic.SubtypeEnum sub, string tag)[]
            {
                (DialogTopic.SubtypeEnum.Custom, "CUST"),
                (DialogTopic.SubtypeEnum.Hello, "HELO"),
                (DialogTopic.SubtypeEnum.Goodbye, "GBYE"),
                (DialogTopic.SubtypeEnum.Idle, "IDLE"),
                (DialogTopic.SubtypeEnum.ForceGreet, "PFGT"),
                (DialogTopic.SubtypeEnum.Rumors, "RUMO"),
                (DialogTopic.SubtypeEnum.Scene, "SCEN"),
            };
            bool ok = anchors.All(a => DialogueSubtype.MarkerFor(a.sub) == a.tag);
            var bad = anchors.Where(a => DialogueSubtype.MarkerFor(a.sub) != a.tag)
                             .Select(a => $"{a.sub}!={DialogueSubtype.MarkerFor(a.sub)}(want {a.tag})");
            Check(ok, $"TABLE-ANCHOR common markers correct{(ok ? "" : " — MISMATCH: " + string.Join(", ", bad))}");
        }

        // ---- TABLE-NAMES: every Mutagen SubtypeEnum value maps to a non-null marker AND sits at its own row
        //      (NameAt((int)v) == v.ToString()). This machine-verifies the row ORDER, so a transposition of any of
        //      the ~100 non-anchor rows (the realistic merge-conflict slip) is CI-fatal, not silently wrong-bucketing.
        //      It also fails loud if a future Mutagen version adds a subtype the table doesn't model (cornerstone). ----
        {
            var vals = Enum.GetValues<DialogTopic.SubtypeEnum>();
            var mismatches = vals.Where(v => DialogueSubtype.MarkerFor(v) is null || DialogueSubtype.NameAt((int)v) != v.ToString())
                                 .Select(v => $"{v}(={(int)v}) name='{DialogueSubtype.NameAt((int)v)}' marker={DialogueSubtype.MarkerFor(v) ?? "<null>"}")
                                 .ToList();
            Check(mismatches.Count == 0,
                $"TABLE-NAMES all {vals.Length} Mutagen SubtypeEnum values map to their own row{(mismatches.Count == 0 ? "" : " — DRIFT: " + string.Join("; ", mismatches))}");
        }

        var root = Path.Combine(Path.GetTempPath(), "hc-dial-snam-guard-" + Guid.NewGuid().ToString("N"));
        try
        {
            // --- synthetic MO2 instance with a master mod carrying two pre-fix topics: one BLANK marker, one HELO. ---
            string instance = Path.Combine(root, "instance");
            string profiles = Path.Combine(instance, "profiles", "Default");
            string mods = Path.Combine(instance, "mods");
            Directory.CreateDirectory(profiles); Directory.CreateDirectory(mods);
            Directory.CreateDirectory(Path.Combine(root, "game", "Data"));
            File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
                "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
                + Path.Combine(root, "game").Replace(@"\", @"\\") + ")\r\n");

            var mKey = new ModKey("HcSnamMaster", ModType.Master);
            var modDir = Path.Combine(mods, "MasterMod");
            var masterPath = Path.Combine(modDir, mKey.FileName.String);
            Directory.CreateDirectory(modDir);
            FormKey blankTopicFk, okTopicFk, overridableFk;
            {
                var m = new SkyrimMod(mKey, SkyrimRelease.SkyrimSE);
                var blank = m.DialogTopics.AddNew(); blank.EditorID = "HcSnamBlank";
                blank.Subtype = DialogTopic.SubtypeEnum.Hello;              // Subtype set, marker LEFT BLANK (the #131 bug shape)
                blankTopicFk = blank.FormKey;
                var okTopic = m.DialogTopics.AddNew(); okTopic.EditorID = "HcSnamOk";
                okTopic.Subtype = DialogTopic.SubtypeEnum.Hello;
                okTopic.SubtypeName = new RecordType("HELO");               // a well-formed marker
                okTopicFk = okTopic.FormKey;
                var ovbl = m.DialogTopics.AddNew(); ovbl.EditorID = "HcSnamOverridable";  // a well-formed master topic an .esp will override + blank
                ovbl.Subtype = DialogTopic.SubtypeEnum.Hello;
                ovbl.SubtypeName = new RecordType("HELO");
                overridableFk = ovbl.FormKey;
                m.BeginWrite.ToPath(masterPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
            }

            // An OVERRIDE plugin that overrides the master's well-formed topic but BLANKS its SNAM marker — the F3
            // counterexample shape (a blank-SNAM override, observed shipping in a working mod). Its winner is the .esp,
            // so its FormKey's defining master (the .esm) != winner → the validator must treat it as an override (Warning).
            var oKey = new ModKey("HcSnamOverride", ModType.Plugin);
            var oDir = Path.Combine(mods, "OverrideMod");
            Directory.CreateDirectory(oDir);
            {
                using var masterGetter = SkyrimMod.CreateFromBinaryOverlay(masterPath, SkyrimRelease.SkyrimSE);
                var om = new SkyrimMod(oKey, SkyrimRelease.SkyrimSE);
                var ov = om.DialogTopics.GetOrAddAsOverride(masterGetter.DialogTopics.First(t => t.FormKey == overridableFk));
                ov.SubtypeName = RecordType.Null;                           // blank the marker on the override
                om.BeginWrite.ToPath(Path.Combine(oDir, oKey.FileName.String)).WithLoadOrder(new ISkyrimModGetter[] { masterGetter }).Write();
            }

            File.WriteAllText(Path.Combine(profiles, "loadorder.txt"), "# header\r\n" + mKey.FileName + "\r\n" + oKey.FileName + "\r\n");
            File.WriteAllText(Path.Combine(profiles, "plugins.txt"), "*" + mKey.FileName + "\r\n*" + oKey.FileName + "\r\n");
            File.WriteAllText(Path.Combine(profiles, "modlist.txt"), "# header\r\n+OverrideMod\r\n+MasterMod\r\n");

            var genDir = Path.Combine(root, "corpus-gen");
            CorpusGenerator.GenerateAll(genDir, Path.Combine(root, "corpus-ref"));
            CorpusRulebook.CorpusPath = Path.Combine(genDir, "corpus.json");

            var store = new UserConfigStore(Path.Combine(root, "houseCARL.user.json"));
            using var svc = LoadOrderService.WithInstance(instance, 0, store);
            svc.Stats();   // warm the lazy index once

            // ---- AUTOFILL: create a DialogTopic with Subtype=Hello and no marker → written SNAM = HELO, reported ----
            {
                var ops = new[] { new BulkOp { FieldPath = "Subtype", Verb = "Set", Value = "Hello" } };
                var o = svc.CreateOne("DialogTopic", "HcSnamAutofill", ops, "HcSnamAF", null);
                string? snam = o.Success ? TopicSnam(o.OutputPath, o.Created[0].FormKey) : null;
                bool reported = o.Success && o.Created[0].Ops.Any(op => op.Label.Contains("SubtypeName", StringComparison.OrdinalIgnoreCase));
                Check(o.Success && snam == "HELO" && reported,
                    $"AUTOFILL Subtype=Hello, no marker → SNAM auto-set to HELO + reported — {(o.Success ? $"snam={snam} reported={reported}" : "err=[" + o.Error + "]")}");
            }

            // ---- DEFAULT-CUST: create a bare DialogTopic (Subtype defaults Custom) → written SNAM = CUST ----
            {
                var o = svc.CreateOne("DialogTopic", "HcSnamBare", Array.Empty<BulkOp>(), "HcSnamBare", null);
                string? snam = o.Success ? TopicSnam(o.OutputPath, o.Created[0].FormKey) : null;
                Check(o.Success && snam == "CUST",
                    $"DEFAULT-CUST bare topic (Subtype defaults Custom) → SNAM auto-set to CUST — {(o.Success ? $"snam={snam}" : "err=[" + o.Error + "]")}");
            }

            // ---- EXPLICIT-WINS: an explicit SubtypeName is never overridden by the auto-fill ----
            {
                var ops = new[] { new BulkOp { FieldPath = "SubtypeName", Verb = "Set", Value = "GBYE" } };
                var o = svc.CreateOne("DialogTopic", "HcSnamExplicit", ops, "HcSnamEx", null);
                string? snam = o.Success ? TopicSnam(o.OutputPath, o.Created[0].FormKey) : null;
                Check(o.Success && snam == "GBYE",
                    $"EXPLICIT-WINS explicit SubtypeName=GBYE kept (not overridden to CUST) — {(o.Success ? $"snam={snam}" : "err=[" + o.Error + "]")}");
            }

            // ---- UNMODELED-REFUSE: a Subtype outside the modeled range with no explicit marker → FAIL LOUD, nothing
            //      written (never ship a silent blank SNAM; the pre-flight accepts undefined numeric enum values, so a
            //      Subtype=105 coerces — the create must catch it, not the enum parse). ----
            {
                var ops = new[] { new BulkOp { FieldPath = "Subtype", Verb = "Set", Value = "105" } };
                var o = svc.CreateOne("DialogTopic", "HcSnamOob", ops, "HcSnamOob", null);
                bool refused = !o.Success && o.Error is not null
                    && o.Error.Contains("marker", StringComparison.OrdinalIgnoreCase)
                    && o.Error.Contains("modeled", StringComparison.OrdinalIgnoreCase);
                bool noFolder = !Directory.EnumerateDirectories(mods, "houseCARL - HcSnamOob*").Any();
                Check(refused && noFolder,
                    $"UNMODELED-REFUSE out-of-range Subtype refused loud, nothing written — refused={refused} noFolder={noFolder} err=[{o.Error}]");
            }

            // ---- EDIT-SYNC (F1): editing an existing topic's Subtype (without setting SubtypeName) syncs the SNAM
            //      marker to match, so the change isn't a silent in-game no-op. HcSnamOk is Hello/HELO in the master;
            //      set Subtype=Goodbye → the written override's SNAM must become GBYE, and the sync is reported. ----
            {
                var ops = new[] { new BulkOp { Formid = okTopicFk.ToString(), FieldPath = "Subtype", Verb = "Set", Value = "Goodbye" } };
                var o = svc.ApplyEdits(ops, "HcSnamEdit", null);
                string? snam = o.Success ? TopicSnam(o.OutputPath, okTopicFk) : null;
                bool reported = o.Success && o.Ops.Any(op => op.Label.Contains("SubtypeName", StringComparison.OrdinalIgnoreCase));
                Check(o.Success && snam == "GBYE" && reported,
                    $"EDIT-SYNC set Subtype=Goodbye → SNAM synced to GBYE + reported — {(o.Success ? $"snam={snam} reported={reported}" : "err=[" + o.Error + "]")}");
            }

            // ---- EDIT-EXPLICIT-WINS (F1): setting Subtype AND SubtypeName in the SAME call keeps the explicit marker
            //      (the sync only fires when SubtypeName was NOT set) — set Subtype=Goodbye + SubtypeName=IDLE → IDLE. ----
            {
                var ops = new[]
                {
                    new BulkOp { Formid = okTopicFk.ToString(), FieldPath = "Subtype", Verb = "Set", Value = "Goodbye" },
                    new BulkOp { Formid = okTopicFk.ToString(), FieldPath = "SubtypeName", Verb = "Set", Value = "IDLE" },
                };
                var o = svc.ApplyEdits(ops, "HcSnamEditExplicit", null);
                string? snam = o.Success ? TopicSnam(o.OutputPath, okTopicFk) : null;
                Check(o.Success && snam == "IDLE",
                    $"EDIT-EXPLICIT-WINS Subtype+SubtypeName in one call keeps explicit IDLE (not synced to GBYE) — {(o.Success ? $"snam={snam}" : "err=[" + o.Error + "]")}");
            }

            // ---- EDIT-NO-TOUCH (F1): editing a NON-Subtype field must NOT touch the SNAM marker (no false sync on the
            //      countless topics whose subtype the call didn't change) — set Priority, SNAM stays HELO. ----
            {
                var ops = new[] { new BulkOp { Formid = okTopicFk.ToString(), FieldPath = "Priority", Verb = "Set", Value = "80" } };
                var o = svc.ApplyEdits(ops, "HcSnamEditPriority", null);
                string? snam = o.Success ? TopicSnam(o.OutputPath, okTopicFk) : null;
                Check(o.Success && snam == "HELO",
                    $"EDIT-NO-TOUCH non-Subtype edit leaves SNAM untouched (HELO) — {(o.Success ? $"snam={snam}" : "err=[" + o.Error + "]")}");
            }

            // ---- VALIDATE-BLANK: validate a topic shipped with a blank marker → a Problem naming the marker; the
            //      well-formed sibling raises no such Problem. ----
            {
                var rBlank = svc.ValidateDialogue(blankTopicFk);
                bool blankFlagged = rBlank.Topics.Count == 1 && rBlank.Topics[0].Issues.Any(i =>
                    i.Severity == DialogueIssueSeverity.Problem &&
                    i.Message.Contains("SubtypeName", StringComparison.OrdinalIgnoreCase) &&
                    i.Message.Contains("malformed", StringComparison.OrdinalIgnoreCase));
                var rOk = svc.ValidateDialogue(okTopicFk);
                bool okClean = rOk.Topics.Count == 1 && !rOk.Topics[0].Issues.Any(i =>
                    i.Message.Contains("SubtypeName", StringComparison.OrdinalIgnoreCase));
                Check(blankFlagged && okClean,
                    $"VALIDATE-BLANK blank marker → Problem, HELO marker → clean — blankFlagged={blankFlagged} okClean={okClean}");
            }

            // ---- OVERRIDE-WARN (F3): a blank-SNAM OVERRIDE of a master topic → a WARNING (not a Problem). The base
            //      record's marker may still apply (this exact shape ships in working, actively-played mods), so the
            //      validator must not cry "guaranteed CTD" over it — but it's still malformed, so it's surfaced. ----
            {
                var rOv = svc.ValidateDialogue(overridableFk);
                var markerIssues = rOv.Topics.Count == 1
                    ? rOv.Topics[0].Issues.Where(i => i.Message.Contains("SubtypeName", StringComparison.OrdinalIgnoreCase)).ToList()
                    : new List<DialogueIssue>();
                bool warnNotProblem = markerIssues.Count == 1
                    && markerIssues[0].Severity == DialogueIssueSeverity.Warning
                    && markerIssues[0].Message.Contains("override", StringComparison.OrdinalIgnoreCase);
                Check(warnNotProblem,
                    $"OVERRIDE-WARN blank-SNAM override → Warning (not Problem), names 'override' — {(markerIssues.Count == 1 ? $"sev={markerIssues[0].Severity}" : $"markerIssues={markerIssues.Count}")}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  guard infrastructure: {ex.GetType().Name}: {(ex.InnerException ?? ex).Message}");
            fail++;
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }

        Console.WriteLine();
        Console.WriteLine($"=== dialogue-subtype-marker-guard: {(fail == 0 ? "PASS" : "FAIL")} ===");
        return fail == 0 ? 0 : 1;
    }

    /// <summary>Read a created topic's SNAM marker back off the written patch on disk (the bytes MO2 would load).</summary>
    static string? TopicSnam(string patchPath, FormKey topicFk)
    {
        ISkyrimModGetter? ov = null;
        try
        {
            ov = SkyrimMod.CreateFromBinaryOverlay(patchPath, SkyrimRelease.SkyrimSE);
            var t = ov.DialogTopics.FirstOrDefault(x => x.FormKey == topicFk);
            return t is null ? null : t.SubtypeName.Type;
        }
        catch { return null; }
        finally { (ov as IDisposable)?.Dispose(); }
    }
}
