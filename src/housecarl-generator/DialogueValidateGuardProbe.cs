using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// SELF-CONTAINED CI REGRESSION GUARD for the on-demand dialogue-graph validator (nested-dialogue plan §3.6, Layer B
/// unit C part C2 — housecarl_validate_dialogue). Drives the REAL core path (DialogueValidate.Run) against a SYNTHESIZED
/// master in TEMP — NO Skyrim.esm, so it runs in CI. The master carries one topic per validation shape, a quest that
/// owns two topics (the fan-out), a weapon (the wrong-type reject) + a not-in-order FormKey (the absent reject).
/// Run: dotnet run --project src/housecarl-generator -- dialogue-validate-guard
///
/// Arms (ALL required — a GREEN must mean "the contract holds", never "the scenario doesn't arise here"):
///   CLEAN        — a well-formed topic (quest + branch resolve, PNAM chain correct) reports ZERO graph issues — the
///                  no-FALSE-POSITIVE keystone (a validator that cries wolf is as bad as one that misses, Q3).
///   PNAM-BROKEN  — a 2nd line with NO previous-link (PNAM) warns 'previous-link'/'order' — the chain teeth. RED if dropped: no warning.
///   NO-QUEST     — a topic with DialogTopic.Quest unset warns 'Quest' — the unowned-topic teeth.
///   BAD-BRANCH   — DialogTopic.Branch pointing at a non-DLBR is a PROBLEM naming 'Branch' — the broken-wiring teeth.
///   VOICE-WIRED  — a voiced line with no .fuz on disk surfaces as a SILENT VoiceLine IN THE VALIDATOR (the create-time
///                  VoiceCheck reused over an EXISTING info) — proves the reuse is wired, not just that VoiceCheck exists.
///   SCRIPT-WIRED — a bound result-script with no .pex surfaces as a ScriptNotCompiled finding IN THE VALIDATOR (the
///                  reused DialogueScriptCheck over an existing info).
///   CTDA-COUNT   — a line carrying a Condition is counted (ConditionedInfoCount >= 1) — the standing-CTDA-limit teeth.
///   QUEST-FANOUT — validating a QUEST fans out to EXACTLY the topics it owns (2 here), kind="quest". RED if the filter
///                  breaks: 0 or every topic.
///   REJ-NOTFOUND — a FormID not in the active order is a NAMED error ('not in the active load order'), never an empty pass.
///   REJ-WRONGTYPE— a FormID resolving to neither a DIAL nor a QUST (a Weapon) is a NAMED error ('not a dialogue topic').
/// </summary>
public static class DialogueValidateGuardProbe
{
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("################  REGRESSION GUARD — dialogue-graph validator (nested-dialogue plan §3.6, Layer B unit C2)  ################");
        Console.WriteLine();

        var tmpDir = Path.Combine(Path.GetTempPath(), "hc-dialogue-validate-guard");
        if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true);
        Directory.CreateDirectory(tmpDir);

        var mKey = new ModKey("HcDvGuardMaster", ModType.Master);
        string mPath = Path.Combine(tmpDir, mKey.FileName.String);
        FormKey cleanFk, pnamFk, noQuestFk, badBranchFk, voicedFk, scriptedFk, condFk, qFanFk, weapFk;
        try
        {
            var m = new SkyrimMod(mKey, SkyrimRelease.SkyrimSE);

            // Shared wiring the topics point at: two quests (main + the fan-out owner), a branch, a speaker chain.
            var qMain = m.Quests.AddNew(); qMain.EditorID = "HcDvQuestMain";
            var qFan = m.Quests.AddNew(); qFan.EditorID = "HcDvQuestFan"; qFanFk = qFan.FormKey;
            var branch = m.DialogBranches.AddNew(); branch.EditorID = "HcDvBranch";
            var voice = m.VoiceTypes.AddNew(); voice.EditorID = "HcDvVoice";
            var npc = m.Npcs.AddNew(); npc.EditorID = "HcDvNpc"; npc.Voice.SetTo(voice.FormKey);
            var weap = m.Weapons.AddNew(); weap.EditorID = "HcDvWeap"; weap.BasicStats = new WeaponBasicStats { Damage = 10 };
            weapFk = weap.FormKey;

            DialogResponses Info(string edid) => new(m.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = edid };

            // CLEAN: quest + branch wired, two lines with a correct PNAM chain (line0 has none; line1 -> line0).
            var tClean = m.DialogTopics.AddNew(); tClean.EditorID = "HcDvClean";
            tClean.Quest.SetTo(qMain.FormKey); tClean.Branch.SetTo(branch.FormKey);
            var c0 = Info("HcDvCleanI0"); var c1 = Info("HcDvCleanI1"); c1.PreviousDialog.SetTo(c0.FormKey);
            tClean.Responses.Add(c0); tClean.Responses.Add(c1); cleanFk = tClean.FormKey;

            // PNAM-BROKEN: a 2nd line whose PNAM is left unset (should chain to line0).
            var tPnam = m.DialogTopics.AddNew(); tPnam.EditorID = "HcDvBadPnam"; tPnam.Quest.SetTo(qMain.FormKey);
            tPnam.Responses.Add(Info("HcDvBpI0")); tPnam.Responses.Add(Info("HcDvBpI1")); pnamFk = tPnam.FormKey;

            // NO-QUEST: Quest deliberately left unset.
            var tNoQ = m.DialogTopics.AddNew(); tNoQ.EditorID = "HcDvNoQuest";
            tNoQ.Responses.Add(Info("HcDvNqI0")); noQuestFk = tNoQ.FormKey;

            // BAD-BRANCH: Branch points at a FormID that is no record (so it resolves to no DialogBranch).
            var tBadB = m.DialogTopics.AddNew(); tBadB.EditorID = "HcDvBadBranch"; tBadB.Quest.SetTo(qMain.FormKey);
            tBadB.Branch.SetTo(new FormKey(mKey, 0x00ABCDEF));
            tBadB.Responses.Add(Info("HcDvBbI0")); badBranchFk = tBadB.FormKey;

            // VOICE-WIRED: a voiced line (Speaker + one response) — no .fuz planted -> SILENT in the validator.
            var tVoice = m.DialogTopics.AddNew(); tVoice.EditorID = "HcDvVoiced"; tVoice.Quest.SetTo(qMain.FormKey);
            var vi = Info("HcDvVoicedI"); vi.Speaker.SetTo(npc.FormKey); vi.Responses.Add(new DialogResponse { ResponseNumber = 1 });
            tVoice.Responses.Add(vi); voicedFk = tVoice.FormKey;

            // SCRIPT-WIRED: a bound result-script fragment — no .pex planted -> ScriptNotCompiled in the validator.
            var tScript = m.DialogTopics.AddNew(); tScript.EditorID = "HcDvScripted"; tScript.Quest.SetTo(qMain.FormKey);
            var si = Info("HcDvScriptedI");
            si.VirtualMachineAdapter = new DialogResponsesAdapter { ScriptFragments = new ScriptFragments {
                FileName = "HcDvScriptClass", OnEnd = new ScriptFragment { ScriptName = "HcDvScriptClass", FragmentName = "Fragment_0" } } };
            tScript.Responses.Add(si); scriptedFk = tScript.FormKey;

            // CTDA-COUNT: a line carrying one Condition.
            var tCond = m.DialogTopics.AddNew(); tCond.EditorID = "HcDvCond"; tCond.Quest.SetTo(qMain.FormKey);
            var ci = Info("HcDvCondI");
            ci.Conditions.Add(new ConditionFloat { CompareOperator = CompareOperator.EqualTo, ComparisonValue = 1f,
                Data = new GetActorValueConditionData { ActorValue = ActorValue.Conjuration } });
            tCond.Responses.Add(ci); condFk = tCond.FormKey;

            // QUEST-FANOUT: two topics owned by qFan (and nothing else points at it).
            var tf1 = m.DialogTopics.AddNew(); tf1.EditorID = "HcDvFan1"; tf1.Quest.SetTo(qFan.FormKey); tf1.Responses.Add(Info("HcDvFan1I"));
            var tf2 = m.DialogTopics.AddNew(); tf2.EditorID = "HcDvFan2"; tf2.Quest.SetTo(qFan.FormKey); tf2.Responses.Add(Info("HcDvFan2I"));

            m.BeginWrite.ToPath(mPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: could not synthesize the fixture master: {ex.GetType().Name}: {(ex.InnerException ?? ex).Message}");
            try { Directory.Delete(tmpDir, recursive: true); } catch { }
            return 1;
        }

        var dataDir = Path.Combine(tmpDir, "data"); Directory.CreateDirectory(dataDir);   // EMPTY — nothing planted; voiced/scripted lines read as absent
        using var resolver = LoadOrderResolver.Build(new[] { mPath });
        using var assets = AssetResolver.Build("", "", dataDir, Array.Empty<string>(), Array.Empty<ActiveArchive>());
        Console.WriteLine($"-- setup: master {mKey.FileName}; topics clean/pnam/noquest/badbranch/voiced/scripted/cond + 2 fan-out; quest {qFanFk}; weapon {weapFk} --");
        Console.WriteLine();

        bool all = true;

        // ---------- CLEAN: a well-formed topic reports ZERO graph issues (no false positives) ----------
        {
            var t = One(DialogueValidate.Run(resolver, assets, cleanFk));
            bool ok = t is not null && t.Issues.Count == 0;
            all &= Pass("CLEAN no false issues", ok, t is null ? "no topic returned" : $"{t.Issues.Count} issue(s): {Issues(t)}");
        }

        // ---------- PNAM-BROKEN: a 2nd line missing its previous-link warns ----------
        {
            var t = One(DialogueValidate.Run(resolver, assets, pnamFk));
            bool ok = t is not null && t.Issues.Any(i => i.Severity == DialogueIssueSeverity.Warning
                && (i.Message.Contains("previous-link", StringComparison.OrdinalIgnoreCase) || i.Message.Contains("PNAM", StringComparison.Ordinal)));
            all &= Pass("PNAM-BROKEN warns chain", ok, t is null ? "no topic" : Issues(t));
        }

        // ---------- NO-QUEST: an unowned topic warns 'Quest' ----------
        {
            var t = One(DialogueValidate.Run(resolver, assets, noQuestFk));
            bool ok = t is not null && t.Issues.Any(i => i.Message.Contains("Quest", StringComparison.Ordinal));
            all &= Pass("NO-QUEST warns unowned", ok, t is null ? "no topic" : Issues(t));
        }

        // ---------- BAD-BRANCH: a Branch resolving to no DLBR is a PROBLEM ----------
        {
            var t = One(DialogueValidate.Run(resolver, assets, badBranchFk));
            bool ok = t is not null && t.Issues.Any(i => i.Severity == DialogueIssueSeverity.Problem && i.Message.Contains("Branch", StringComparison.Ordinal));
            all &= Pass("BAD-BRANCH problem", ok, t is null ? "no topic" : Issues(t));
        }

        // ---------- VOICE-WIRED: a voiced line with no .fuz surfaces as a SILENT VoiceLine in the validator ----------
        {
            var t = One(DialogueValidate.Run(resolver, assets, voicedFk));
            bool ok = t is not null && t.VoiceLines.Count == 1 && !t.VoiceLines[0].FuzPresent;
            all &= Pass("VOICE-WIRED silent line", ok, t is null ? "no topic" : $"lines={t.VoiceLines.Count} present={(t.VoiceLines.Count == 1 ? t.VoiceLines[0].FuzPresent.ToString() : "?")}");
        }

        // ---------- SCRIPT-WIRED: a bound script with no .pex surfaces as ScriptNotCompiled in the validator ----------
        {
            var t = One(DialogueValidate.Run(resolver, assets, scriptedFk));
            bool ok = t is not null && t.ScriptFindings.Count == 1 && t.ScriptFindings[0].Status == ScriptBindingStatus.ScriptNotCompiled;
            all &= Pass("SCRIPT-WIRED not-compiled", ok, t is null ? "no topic" : $"findings={t.ScriptFindings.Count} status={(t.ScriptFindings.Count == 1 ? t.ScriptFindings[0].Status.ToString() : "?")}");
        }

        // ---------- CTDA-COUNT: a conditioned line is counted (the standing-limit teeth) ----------
        {
            var t = One(DialogueValidate.Run(resolver, assets, condFk));
            bool ok = t is not null && t.ConditionedInfoCount >= 1;
            all &= Pass("CTDA-COUNT counts conditions", ok, t is null ? "no topic" : $"conditioned={t?.ConditionedInfoCount}");
        }

        // ---------- QUEST-FANOUT: validating a quest fans out to EXACTLY its 2 topics ----------
        {
            var r = DialogueValidate.Run(resolver, assets, qFanFk);
            bool ok = r.InputKind == "quest" && r.Error is null && r.CheckError is null && r.Topics.Count == 2;
            all &= Pass("QUEST-FANOUT 2 topics", ok, $"kind={r.InputKind} topics={r.Topics.Count} err=[{r.Error}] ckerr=[{r.CheckError}]");
        }

        // ---------- REJ-NOTFOUND: a FormID not in the order is a NAMED error ----------
        {
            var ghost = new FormKey(new ModKey("HcDvGhost", ModType.Plugin), 0x000800);
            var r = DialogueValidate.Run(resolver, assets, ghost);
            bool ok = r.InputKind == "error" && r.Error is not null && r.Error.Contains("not in the active load order", StringComparison.OrdinalIgnoreCase) && r.Topics.Count == 0;
            all &= Pass("REJ-NOTFOUND named error", ok, $"kind={r.InputKind} err=[{r.Error}]");
        }

        // ---------- REJ-WRONGTYPE: a Weapon FormID is a NAMED 'not a dialogue topic or quest' error ----------
        {
            var r = DialogueValidate.Run(resolver, assets, weapFk);
            bool ok = r.InputKind == "error" && r.Error is not null && r.Error.Contains("not a dialogue topic", StringComparison.OrdinalIgnoreCase) && r.Topics.Count == 0;
            all &= Pass("REJ-WRONGTYPE named error", ok, $"kind={r.InputKind} err=[{r.Error}]");
        }

        Console.WriteLine();
        Console.WriteLine(all
            ? "RESULT: PASS — the dialogue-graph validator holds (graph checks, voice/script reuse, fan-out, named rejects)."
            : "RESULT: FAIL — at least one arm regressed (see above).");
        try { Directory.Delete(tmpDir, recursive: true); } catch { }
        return all ? 0 : 1;
    }

    /// <summary>The single topic of a topic-kind report (null on an error / a 0-topic result), so the single-topic arms
    /// read its one TopicValidation directly.</summary>
    static TopicValidation? One(DialogueValidationReport r) => r.Topics.Count == 1 ? r.Topics[0] : null;

    static string Issues(TopicValidation t) => t.Issues.Count == 0 ? "<none>" : string.Join(" | ", t.Issues.Select(i => $"[{i.Severity}] {i.Message}"));

    static bool Pass(string label, bool ok, string detail)
    {
        Console.WriteLine($"   {label,-28}: {(ok ? "PASS" : "FAIL")} — {detail}");
        return ok;
    }
}
