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
/// CHECK MODEL (review fold 2026-06-19, empirically confirmed against the live load order): vanilla topics leave PNAM
/// EMPTY and select intra-topic by Conditions, so PNAM absence is the NORM and is never flagged — the §3.6 "PNAM chain
/// in response order" check was DROPPED (it false-flagged ~every real topic). The real conversation chain is INFO.LinkTo
/// (topic → next topic). So the graph checks are: quest + branch wiring, LinkTo targets resolve, and a SET-but-dangling
/// PNAM. Deleted INFOs are skipped. Arms (ALL required — a GREEN must mean "the contract holds"):
///   CLEAN        — a well-formed topic (quest + branch resolve; INFOs with EMPTY PNAM, the vanilla norm; a valid
///                  LinkTo to a real topic) reports ZERO issues — the no-FALSE-POSITIVE keystone, and the direct proof
///                  that empty PNAM is NOT flagged (the regression the review caught).
///   LINKTO-DANGLE— an INFO.LinkTo to a missing topic is a PROBLEM naming 'LinkTo' — the real broken-chain teeth.
///   PNAM-DANGLE  — a SET PreviousDialog resolving to no INFO is a PROBLEM naming 'PNAM'/'previous-link'. (Absence is
///                  NOT flagged — proven by CLEAN.)
///   PNAM-RESOLVES— a SET previous-link to a REAL sibling INFO is NOT flagged — the positive lock that dangling-only is
///                  resolve-aware, not blanket (guards against an INFO index-scoping regression). PR #90 review finding 4.
///   DELETED-SKIP — a deleted INFO (a removed line) is skipped: not counted live (InfoCount excludes it), tallied as
///                  deleted, and never voice/script-checked or graph-flagged.
///   NO-QUEST     — a topic with DialogTopic.Quest unset warns 'Quest' — the unowned-topic teeth.
///   BAD-BRANCH   — DialogTopic.Branch pointing at a non-DLBR is a PROBLEM naming 'Branch'.
///   VOICE-WIRED  — a voiced line with no .fuz surfaces as a SILENT VoiceLine IN THE VALIDATOR (reused VoiceCheck).
///   SCRIPT-WIRED — a bound result-script with no .pex surfaces as a ScriptNotCompiled finding (reused DialogueScriptCheck).
///   CTDA-COUNT   — a line carrying a Condition is counted (ConditionedInfoCount >= 1) — the standing-CTDA-limit teeth.
///   QUEST-FANOUT — validating a QUEST fans out to EXACTLY the topics it owns (2 here), kind="quest".
///   REJ-NOTFOUND — a FormID not in the active order is a NAMED error ('not in the active load order').
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
        FormKey cleanFk, linkBadFk, pnamBadFk, pnamOkFk, deletedFk, noQuestFk, badBranchFk, voicedFk, scriptedFk, condFk, qFanFk, weapFk;
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

            // A plain target topic so CLEAN's LinkTo resolves to a real DIAL.
            var tTarget = m.DialogTopics.AddNew(); tTarget.EditorID = "HcDvLinkTarget"; tTarget.Quest.SetTo(qMain.FormKey);
            tTarget.Responses.Add(Info("HcDvLinkTargetI"));

            // CLEAN: quest + branch wired; two INFOs with EMPTY PNAM (the vanilla norm); the 2nd hands off to a real
            // topic via LinkTo. The no-false-positive keystone — empty PNAM and a valid LinkTo must NOT be flagged.
            var tClean = m.DialogTopics.AddNew(); tClean.EditorID = "HcDvClean";
            tClean.Quest.SetTo(qMain.FormKey); tClean.Branch.SetTo(branch.FormKey);
            var c1 = Info("HcDvCleanI1"); c1.LinkTo.Add(new FormLink<IDialogTopicGetter>(tTarget.FormKey));
            tClean.Responses.Add(Info("HcDvCleanI0")); tClean.Responses.Add(c1); cleanFk = tClean.FormKey;

            // LINKTO-DANGLE: an INFO links to a topic that is not in the order.
            var tLinkBad = m.DialogTopics.AddNew(); tLinkBad.EditorID = "HcDvLinkBad"; tLinkBad.Quest.SetTo(qMain.FormKey);
            var lb = Info("HcDvLinkBadI"); lb.LinkTo.Add(new FormLink<IDialogTopicGetter>(new FormKey(mKey, 0x00BBBBBB)));
            tLinkBad.Responses.Add(lb); linkBadFk = tLinkBad.FormKey;

            // PNAM-DANGLE: an INFO's previous-link points at an INFO that is not in the order.
            var tPnamBad = m.DialogTopics.AddNew(); tPnamBad.EditorID = "HcDvPnamBad"; tPnamBad.Quest.SetTo(qMain.FormKey);
            var pb = Info("HcDvPnamBadI"); pb.PreviousDialog.SetTo(new FormKey(mKey, 0x00CCCCCC));
            tPnamBad.Responses.Add(pb); pnamBadFk = tPnamBad.FormKey;

            // PNAM-RESOLVES: an INFO whose previous-link points at a REAL sibling INFO must NOT be flagged — the
            // positive lock that the dangling-ONLY PNAM check is resolve-aware, not blanket (PR #90 review finding 4).
            var tPnamOk = m.DialogTopics.AddNew(); tPnamOk.EditorID = "HcDvPnamOk"; tPnamOk.Quest.SetTo(qMain.FormKey);
            var pa = Info("HcDvPnamOkA");
            var pbk = Info("HcDvPnamOkB"); pbk.PreviousDialog.SetTo(pa.FormKey);
            tPnamOk.Responses.Add(pa); tPnamOk.Responses.Add(pbk); pnamOkFk = tPnamOk.FormKey;

            // DELETED-SKIP: a live INFO + a deleted INFO (a removed line) — the deleted one must be skipped.
            var tDeleted = m.DialogTopics.AddNew(); tDeleted.EditorID = "HcDvDeleted"; tDeleted.Quest.SetTo(qMain.FormKey);
            tDeleted.Responses.Add(Info("HcDvDeletedLive"));
            var del = Info("HcDvDeletedGone"); del.IsDeleted = true; tDeleted.Responses.Add(del);
            deletedFk = tDeleted.FormKey;

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
        Console.WriteLine($"-- setup: master {mKey.FileName}; topics clean/linkbad/pnambad/pnamok/deleted/noquest/badbranch/voiced/scripted/cond + 2 fan-out; quest {qFanFk}; weapon {weapFk} --");
        Console.WriteLine();

        bool all = true;

        // ---------- CLEAN: a well-formed topic (EMPTY PNAM + valid LinkTo) reports ZERO issues — empty PNAM is NOT flagged ----------
        {
            var t = One(DialogueValidate.Run(resolver, assets, cleanFk));
            bool ok = t is not null && t.Issues.Count == 0 && t.InfoCount == 2;
            all &= Pass("CLEAN no false issues", ok, t is null ? "no topic returned" : $"infos={t.InfoCount} issues={t.Issues.Count}: {Issues(t)}");
        }

        // ---------- LINKTO-DANGLE: a LinkTo to a missing topic is a PROBLEM ----------
        {
            var t = One(DialogueValidate.Run(resolver, assets, linkBadFk));
            bool ok = t is not null && t.Issues.Any(i => i.Severity == DialogueIssueSeverity.Problem && i.Message.Contains("LinkTo", StringComparison.Ordinal));
            all &= Pass("LINKTO-DANGLE problem", ok, t is null ? "no topic" : Issues(t));
        }

        // ---------- PNAM-DANGLE: a SET-but-unresolvable previous-link is a PROBLEM (absence is NOT flagged — see CLEAN) ----------
        {
            var t = One(DialogueValidate.Run(resolver, assets, pnamBadFk));
            bool ok = t is not null && t.Issues.Any(i => i.Severity == DialogueIssueSeverity.Problem
                && (i.Message.Contains("PNAM", StringComparison.Ordinal) || i.Message.Contains("previous-link", StringComparison.OrdinalIgnoreCase)));
            all &= Pass("PNAM-DANGLE problem", ok, t is null ? "no topic" : Issues(t));
        }

        // ---------- PNAM-RESOLVES: a SET previous-link to a REAL sibling INFO is NOT flagged (dangling-only is resolve-aware) ----------
        {
            var t = One(DialogueValidate.Run(resolver, assets, pnamOkFk));
            bool ok = t is not null && t.InfoCount == 2 && t.Issues.Count == 0;
            all &= Pass("PNAM-RESOLVES not flagged", ok, t is null ? "no topic" : $"infos={t.InfoCount} issues={t.Issues.Count}: {Issues(t)}");
        }

        // ---------- DELETED-SKIP: a deleted INFO is skipped (not counted live, tallied deleted, no findings) ----------
        {
            var t = One(DialogueValidate.Run(resolver, assets, deletedFk));
            bool ok = t is not null && t.InfoCount == 1 && t.DeletedInfoCount == 1
                && t.ScriptFindings.Count == 0 && t.VoiceLines.Count == 0 && t.Issues.Count == 0;
            all &= Pass("DELETED-SKIP not validated", ok, t is null ? "no topic" : $"live={t.InfoCount} deleted={t.DeletedInfoCount} issues={t.Issues.Count} script={t.ScriptFindings.Count}");
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
            ? "RESULT: PASS — the dialogue-graph validator holds (no false PNAM-absence flag, LinkTo + dangling-PNAM teeth, deleted-skip, voice/script reuse, fan-out, named rejects)."
            : "RESULT: FAIL — at least one arm regressed (see above).");
        try { Directory.Delete(tmpDir, recursive: true); } catch { }
        return all ? 0 : 1;
    }

    /// <summary>The single topic of a topic-kind report (null on an error / a non-single-topic result), so the
    /// single-topic arms read its one TopicValidation directly.</summary>
    static TopicValidation? One(DialogueValidationReport r) => r.Topics.Count == 1 ? r.Topics[0] : null;

    static string Issues(TopicValidation t) => t.Issues.Count == 0 ? "<none>" : string.Join(" | ", t.Issues.Select(i => $"[{i.Severity}] {i.Message}"));

    static bool Pass(string label, bool ok, string detail)
    {
        Console.WriteLine($"   {label,-28}: {(ok ? "PASS" : "FAIL")} — {detail}");
        return ok;
    }
}
