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
///   INFO-CKPARITY-GAP— a live INFO missing CNAM (FavorLevel) / ENAM (Flags) WARNs twice, naming each — the CK-editor-crash
///                  catch, via the SHARED DialogueCkParity.MissingInfoDefaults probe the create path fills through.
///   INFO-CKPARITY-OK — a CK-parity-complete INFO (Info() runs ApplyInfoDefaults) is NOT flagged — the no-false-positive lock.
///   NO-QUEST     — a topic with DialogTopic.Quest unset warns 'Quest' — the unowned-topic teeth.
///   BAD-BRANCH   — DialogTopic.Branch pointing at a non-DLBR is a PROBLEM naming 'Branch'.
///   VOICE-WIRED  — a voiced line with no .fuz surfaces as a SILENT VoiceLine IN THE VALIDATOR (reused VoiceCheck).
///   SCRIPT-WIRED — a bound result-script with no .pex surfaces as a ScriptNotCompiled finding (reused DialogueScriptCheck).
///   FRAGMENT-PRESENCE— a line carrying a real result-script FRAGMENT is reported HasFragment + FragmentInfoCount>=1 (item 8).
///   FRAGMENT-FREE— a plain voiced line is fragment-free (FragmentInfoCount 0), COUNTED not omitted (item 8).
///   CTDA-COUNT   — a line carrying a Condition is counted (ConditionedInfoCount >= 1) — the standing-CTDA-limit teeth.
///   COND-CLEAN   — two WELL-FORMED conditions (GetStage on the owning quest + GetIsID on a base npc) report ZERO
///                  condition issues — the no-false-positive keystone (incl. the GetIsID-on-a-base-form lock) (item 4).
///   COND-REF-UNSET— Run On a specific Reference with no reference set WARNs ('Run On a specific reference') (item 4).
///   COND-DEAD-ALIAS— GetIsAliasRef at an alias index the owning quest doesn't define WARNs ('no alias with that ID') (item 4).
///   COND-DEAD-LOCALIAS— GetInCurrentLocAlias at an undefined location-alias WARNs ('location-alias', 'no alias with that ID') (item 4).
///   COND-DEAD-QUESTALIAS— Run On:QuestAlias at an undefined alias WARNs ('reference-alias', 'no alias with that ID') (item 4).
///   COND-ALIAS-OK — GetIsAliasRef at a REAL alias index is NOT flagged — the resolve-aware positive lock (item 4).
///   COND-ALIAS-FLOI— an ALIAS-mode form-param FLOI (HasPerk.Perk=alias 7) is NOT flagged — the binary-overlay
///                  false-positive lock (an index-mode FLOI's .Link is a bogus low key on the overlay; lint 3/5 gate on form mode) (item 4).
///   COND-DANGLING-PARAM— a condition form param (GetStage's Quest) not in the order WARNs ('not in the active load order') (item 4).
///   COND-DANGLING-GLOBAL— a ConditionGlobal vs a GLOB not in the order WARNs ('compares against global') (item 4).
///   COND-GETISID-OK— GetIsID pointed at a BASE object is NOT flagged — the lint-5 no-false-positive lock (item 4).
///   COND-GETISID-PLACED— GetIsID pointed at a PLACED reference WARNs ('placed reference'), NOT the dangling-param lint (item 4).
///   TEXT-MOJIBAKE— a line whose player-facing text carries non-ASCII chars (ellipsis/em-dash) WARNs, naming them (item 1).
///   TEXT-CLEAN   — pure-ASCII player-facing text is NOT flagged — the lint keys on >0x7F, not "has text" (item 1).
///   GLOBAL-TAG-OK — a <Global=X> whose X IS in the owning quest's TextDisplayGlobals is NOT flagged (the no-false-positive lock).
///   GLOBAL-TAG-MISSING— a <Global=X> whose X is NOT in TextDisplayGlobals WARNs (naming it) — the [...]-in-game teeth (Track B).
///   GLOBAL-TAG-SUBTAG — a CK formatting-subtag form <Global.Time=X> (modifier before the =) with an uncovered global ALSO WARNs (regex covers the pre-= subtag; PR #139 review).
///   PLAYERREF-WHITELIST— Run On the engine-implicit PlayerRef (000014:Skyrim.esm), absent from the order, is NOT flagged (the sub-0x800 whitelist, Junti false-positive fix).
///   PLAYERREF-CONTROL— Run On a NON-whitelisted missing reference still WARNs — the whitelist is a precise 2-form set, not the whole reserved range.
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
        FormKey cleanFk, linkBadFk, pnamBadFk, pnamOkFk, deletedFk, noQuestFk, badBranchFk, voicedFk, scriptedFk, condFk, qFanFk, weapFk, encBadFk, encOkFk;
        FormKey condCleanFk, condRefUnsetFk, condDeadAliasFk, condDeadLocAliasFk, condDeadQAliasFk, condAliasOkFk, condAliasFloiFk, condDanglingFk, condGlobalFk, condGetIsIdOkFk, condGetIsIdFk;
        FormKey globOkFk, globBadFk, globSubFk, playerRefFk, playerRefCtlFk, ckGapFk, ckOkFk;
        try
        {
            var m = new SkyrimMod(mKey, SkyrimRelease.SkyrimSE);

            // Shared wiring the topics point at: two quests (main + the fan-out owner), a branch, a speaker chain.
            var qMain = m.Quests.AddNew(); qMain.EditorID = "HcDvQuestMain";
            qMain.Aliases.Add(new QuestAlias { ID = 0, Name = "HcDvAlias0" });   // ONE reference-alias (ID 0) — the alias-index lints key on this set
            var qFan = m.Quests.AddNew(); qFan.EditorID = "HcDvQuestFan"; qFanFk = qFan.FormKey;
            var branch = m.DialogBranches.AddNew(); branch.EditorID = "HcDvBranch";
            var voice = m.VoiceTypes.AddNew(); voice.EditorID = "HcDvVoice";
            var npc = m.Npcs.AddNew(); npc.EditorID = "HcDvNpc"; npc.Voice.SetTo(voice.FormKey);
            var weap = m.Weapons.AddNew(); weap.EditorID = "HcDvWeap"; weap.BasicStats = new WeaponBasicStats { Damage = 10 };
            weapFk = weap.FormKey;

            // Info(): a CK-parity-COMPLETE INFO — run through the same ApplyInfoDefaults the create path uses, so it
            // carries FavorLevel (CNAM) + response Flags (ENAM) like a real CK-/xEdit-authored line. Without this the
            // new INFO CK-parity lint would (correctly) flag every bare fixture INFO and trip every "no issues" arm; so
            // this helper ALSO doubles as the no-false-positive lock for that lint (CLEAN et al. stay green only because
            // these INFOs are complete). The INFO-CKPARITY-GAP fixture below deliberately does NOT use this.
            DialogResponses Info(string edid)
            {
                var info = new DialogResponses(m.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = edid };
                DialogueCkParity.ApplyInfoDefaults(info);
                return info;
            }

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

            // TEXT-ENCODING (item 1): one topic with non-ASCII player-facing strings (an ellipsis in the Prompt,
            // an em-dash in a response Text) -> WARN, naming them; one with pure-ASCII text -> NOT flagged (the
            // no-false-positive lock — the lint keys on >0x7F, not on "has text").
            var tEncBad = m.DialogTopics.AddNew(); tEncBad.EditorID = "HcDvEncBad"; tEncBad.Quest.SetTo(qMain.FormKey);
            var ebi = Info("HcDvEncBadI"); ebi.Prompt = "Wait…";
            ebi.Responses.Add(new DialogResponse { ResponseNumber = 1, Text = "Take it — or leave it" });
            tEncBad.Responses.Add(ebi); encBadFk = tEncBad.FormKey;

            var tEncOk = m.DialogTopics.AddNew(); tEncOk.EditorID = "HcDvEncOk"; tEncOk.Quest.SetTo(qMain.FormKey);
            var eoi = Info("HcDvEncOkI"); eoi.Prompt = "Wait...";
            eoi.Responses.Add(new DialogResponse { ResponseNumber = 1, Text = "Take it - or leave it" });
            tEncOk.Responses.Add(eoi); encOkFk = tEncOk.FormKey;

            // CONDITION LINTS (item 4) — topics owned by qMain (which carries ONE reference-alias, ID 0). Each
            // condition below is STRUCTURALLY malformed in exactly one decidable way; a well-formed control proves
            // no false positive. Conditions are built from real Mutagen ConditionData arms (the same shapes
            // read_record surfaces), never hand-encoded bytes.

            // COND-CLEAN: two WELL-FORMED conditions (GetStage on the owning quest + GetActorValue, both Run On
            // Subject) -> ZERO condition issues. The no-false-positive keystone (a present quest param + a
            // param-free function both pass clean).
            var tCondClean = m.DialogTopics.AddNew(); tCondClean.EditorID = "HcDvCondClean"; tCondClean.Quest.SetTo(qMain.FormKey);
            {
                var i = Info("HcDvCondCleanI");
                var d1 = new GetStageConditionData { RunOnType = Condition.RunOnType.Subject }; SetFloiForm(d1, "Quest", qMain.FormKey);
                i.Conditions.Add(new ConditionFloat { CompareOperator = CompareOperator.GreaterThanOrEqualTo, ComparisonValue = 10f, Data = d1 });
                var d2 = new GetActorValueConditionData { ActorValue = ActorValue.Health, RunOnType = Condition.RunOnType.Subject };
                i.Conditions.Add(new ConditionFloat { CompareOperator = CompareOperator.GreaterThan, ComparisonValue = 0f, Data = d2 });
                tCondClean.Responses.Add(i); condCleanFk = tCondClean.FormKey;
            }

            // COND-REF-UNSET: Run On a specific Reference but NO reference set -> WARN (the gate runs on nothing).
            var tCondRef = m.DialogTopics.AddNew(); tCondRef.EditorID = "HcDvCondRefUnset"; tCondRef.Quest.SetTo(qMain.FormKey);
            {
                var i = Info("HcDvCondRefUnsetI");
                var d = new GetActorValueConditionData { ActorValue = ActorValue.Health, RunOnType = Condition.RunOnType.Reference };
                i.Conditions.Add(new ConditionFloat { CompareOperator = CompareOperator.GreaterThan, ComparisonValue = 0f, Data = d });
                tCondRef.Responses.Add(i); condRefUnsetFk = tCondRef.FormKey;
            }

            // COND-DEAD-ALIAS: GetIsAliasRef naming alias #99 (qMain defines only #0) -> WARN dead alias.
            var tCondAliasBad = m.DialogTopics.AddNew(); tCondAliasBad.EditorID = "HcDvCondDeadAlias"; tCondAliasBad.Quest.SetTo(qMain.FormKey);
            {
                var i = Info("HcDvCondDeadAliasI");
                var d = new GetIsAliasRefConditionData { ReferenceAliasIndex = 99, RunOnType = Condition.RunOnType.Subject };
                i.Conditions.Add(new ConditionFloat { CompareOperator = CompareOperator.EqualTo, ComparisonValue = 1f, Data = d });
                tCondAliasBad.Responses.Add(i); condDeadAliasFk = tCondAliasBad.FormKey;
            }

            // COND-DEAD-LOCALIAS: GetInCurrentLocAlias naming a location-alias #99 the owning quest doesn't define
            // -> WARN. Locks lint 2's GetInCurrentLocAlias sub-check (structurally identical to GetIsAliasRef, distinct path).
            var tCondLocAlias = m.DialogTopics.AddNew(); tCondLocAlias.EditorID = "HcDvCondDeadLocAlias"; tCondLocAlias.Quest.SetTo(qMain.FormKey);
            {
                var i = Info("HcDvCondDeadLocAliasI");
                var d = new GetInCurrentLocAliasConditionData { LocationAliasIndex = 99, RunOnType = Condition.RunOnType.Subject };
                i.Conditions.Add(new ConditionFloat { CompareOperator = CompareOperator.EqualTo, ComparisonValue = 1f, Data = d });
                tCondLocAlias.Responses.Add(i); condDeadLocAliasFk = tCondLocAlias.FormKey;
            }

            // COND-DEAD-QUESTALIAS: Run On:QuestAlias at an alias #99 the owning quest doesn't define -> WARN.
            // Locks lint 2's RunOnType==QuestAlias sub-check (the run-on path, distinct from the alias-param paths).
            var tCondQAlias = m.DialogTopics.AddNew(); tCondQAlias.EditorID = "HcDvCondDeadQAlias"; tCondQAlias.Quest.SetTo(qMain.FormKey);
            {
                var i = Info("HcDvCondDeadQAliasI");
                var d = new GetActorValueConditionData { ActorValue = ActorValue.Health, RunOnType = Condition.RunOnType.QuestAlias, RunOnTypeIndex = 99 };
                i.Conditions.Add(new ConditionFloat { CompareOperator = CompareOperator.GreaterThan, ComparisonValue = 0f, Data = d });
                tCondQAlias.Responses.Add(i); condDeadQAliasFk = tCondQAlias.FormKey;
            }

            // COND-ALIAS-OK: GetIsAliasRef naming alias #0 (a REAL alias of qMain) -> NOT flagged (resolve-aware lock).
            var tCondAliasOk = m.DialogTopics.AddNew(); tCondAliasOk.EditorID = "HcDvCondAliasOk"; tCondAliasOk.Quest.SetTo(qMain.FormKey);
            {
                var i = Info("HcDvCondAliasOkI");
                var d = new GetIsAliasRefConditionData { ReferenceAliasIndex = 0, RunOnType = Condition.RunOnType.Subject };
                i.Conditions.Add(new ConditionFloat { CompareOperator = CompareOperator.EqualTo, ComparisonValue = 1f, Data = d });
                tCondAliasOk.Responses.Add(i); condAliasOkFk = tCondAliasOk.FormKey;
            }

            // COND-ALIAS-FLOI: a form-PARAMETER FormLinkOrIndex in ALIAS mode (HasPerk.Perk = alias index 7,
            // UseAliases=true) -> NOT flagged. The regression lock for the binary-overlay FLOI false positive: on the
            // overlay the validator reads, an index-mode FLOI's .Link is a bogus low key (000007), so WITHOUT the
            // form-mode gate lint 3 would WARN "references 000007:... not in the active load order" on a well-formed
            // alias-mode gate. Round-trips through the on-disk master (the overlay path) like every arm. Index 7 need
            // not be a real alias — alias-mode FLOI *parameter* indices are deliberately out of the dangling-form scope.
            var tCondAliasFloi = m.DialogTopics.AddNew(); tCondAliasFloi.EditorID = "HcDvCondAliasFloi"; tCondAliasFloi.Quest.SetTo(qMain.FormKey);
            {
                var i = Info("HcDvCondAliasFloiI");
                var d = new HasPerkConditionData { RunOnType = Condition.RunOnType.Subject, UseAliases = true };
                d.Perk = new FormLinkOrIndex<IPerkGetter>(d, 7u);   // alias index 7 — index mode, the bogus-overlay-link shape
                i.Conditions.Add(new ConditionFloat { CompareOperator = CompareOperator.EqualTo, ComparisonValue = 1f, Data = d });
                tCondAliasFloi.Responses.Add(i); condAliasFloiFk = tCondAliasFloi.FormKey;
            }

            // COND-DANGLING-PARAM: GetStage whose Quest param is NOT in the load order -> WARN dangling param.
            var tCondDangling = m.DialogTopics.AddNew(); tCondDangling.EditorID = "HcDvCondDangling"; tCondDangling.Quest.SetTo(qMain.FormKey);
            {
                var i = Info("HcDvCondDanglingI");
                var d = new GetStageConditionData { RunOnType = Condition.RunOnType.Subject }; SetFloiForm(d, "Quest", new FormKey(mKey, 0x00DDDDDD));
                i.Conditions.Add(new ConditionFloat { CompareOperator = CompareOperator.GreaterThanOrEqualTo, ComparisonValue = 10f, Data = d });
                tCondDangling.Responses.Add(i); condDanglingFk = tCondDangling.FormKey;
            }

            // COND-DANGLING-GLOBAL: a ConditionGlobal compared against a GLOB not in the load order -> WARN.
            var tCondGlobal = m.DialogTopics.AddNew(); tCondGlobal.EditorID = "HcDvCondGlobal"; tCondGlobal.Quest.SetTo(qMain.FormKey);
            {
                var i = Info("HcDvCondGlobalI");
                var d = new GetActorValueConditionData { ActorValue = ActorValue.Health, RunOnType = Condition.RunOnType.Subject };
                var cg = new ConditionGlobal { CompareOperator = CompareOperator.EqualTo, Data = d };
                cg.ComparisonValue.SetTo(new FormKey(mKey, 0x00EEEEEE));
                i.Conditions.Add(cg);
                tCondGlobal.Responses.Add(i); condGlobalFk = tCondGlobal.FormKey;
            }

            // COND-GETISID-PLACED: GetIsID pointed at a PLACED reference (an interior-cell PlacedObject) -> WARN
            // wrong-kind. The placed ref must EXIST + resolve (so lint 3 doesn't fire instead) and resolve to an
            // IPlacedGetter (so lint 5 does) — proving the validator resolves a placed ref's record type.
            var condCell = new Cell(m.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcDvCondCell", Flags = Cell.Flag.IsInteriorCell };
            var condPlaced = new PlacedObject(m.GetNextFormKey(), SkyrimRelease.SkyrimSE); condPlaced.Base.SetTo(weap.FormKey);
            condCell.Persistent.Add(condPlaced);
            var condSub = new CellSubBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellSubBlock }; condSub.Cells.Add(condCell);
            var condBlock = new CellBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellBlock }; condBlock.SubBlocks.Add(condSub);
            m.Cells.Records.Add(condBlock);
            // COND-GETISID-OK: GetIsID pointed at a BASE npc -> NOT flagged. The lint-5 no-false-positive lock
            // (a base object is exactly what GetIsID wants), proving lint 5 fires ONLY for a placed reference.
            var tCondGetIsIdOk = m.DialogTopics.AddNew(); tCondGetIsIdOk.EditorID = "HcDvCondGetIsIdOk"; tCondGetIsIdOk.Quest.SetTo(qMain.FormKey);
            {
                var i = Info("HcDvCondGetIsIdOkI");
                var d = new GetIsIDConditionData { RunOnType = Condition.RunOnType.Subject }; SetFloiForm(d, "Object", npc.FormKey);
                i.Conditions.Add(new ConditionFloat { CompareOperator = CompareOperator.EqualTo, ComparisonValue = 1f, Data = d });
                tCondGetIsIdOk.Responses.Add(i); condGetIsIdOkFk = tCondGetIsIdOk.FormKey;
            }

            var tCondGetIsId = m.DialogTopics.AddNew(); tCondGetIsId.EditorID = "HcDvCondGetIsId"; tCondGetIsId.Quest.SetTo(qMain.FormKey);
            {
                var i = Info("HcDvCondGetIsIdI");
                var d = new GetIsIDConditionData { RunOnType = Condition.RunOnType.Subject }; SetFloiForm(d, "Object", condPlaced.FormKey);
                i.Conditions.Add(new ConditionFloat { CompareOperator = CompareOperator.EqualTo, ComparisonValue = 1f, Data = d });
                tCondGetIsId.Responses.Add(i); condGetIsIdFk = tCondGetIsId.FormKey;
            }

            // QUEST-FANOUT: two topics owned by qFan (and nothing else points at it).
            var tf1 = m.DialogTopics.AddNew(); tf1.EditorID = "HcDvFan1"; tf1.Quest.SetTo(qFan.FormKey); tf1.Responses.Add(Info("HcDvFan1I"));
            var tf2 = m.DialogTopics.AddNew(); tf2.EditorID = "HcDvFan2"; tf2.Quest.SetTo(qFan.FormKey); tf2.Responses.Add(Info("HcDvFan2I"));

            // GLOBAL-TAG lint (Track B): qMain gains one GLOB in its TextDisplayGlobals. One topic tags <Global=HcDvKnownGlobal>
            // (COVERED — not flagged), another tags <Global=HcDvUnknownGlobal> (UNCOVERED — WARN; renders as [...] in game).
            // Both owned by qMain so the lint has a resolvable TextDisplayGlobals set. ASCII text only (no encoding false flag).
            var knownGlob = new GlobalShort(m.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcDvKnownGlobal" };
            m.Globals.Add(knownGlob);
            qMain.TextDisplayGlobals.Add(new FormLink<IGlobalGetter>(knownGlob.FormKey));

            var tGlobOk = m.DialogTopics.AddNew(); tGlobOk.EditorID = "HcDvGlobOk"; tGlobOk.Quest.SetTo(qMain.FormKey);
            { var i = Info("HcDvGlobOkI"); i.Prompt = "It will cost <Global=HcDvKnownGlobal> gold."; tGlobOk.Responses.Add(i); globOkFk = tGlobOk.FormKey; }

            var tGlobBad = m.DialogTopics.AddNew(); tGlobBad.EditorID = "HcDvGlobBad"; tGlobBad.Quest.SetTo(qMain.FormKey);
            { var i = Info("HcDvGlobBadI"); i.Prompt = "It will cost <Global=HcDvUnknownGlobal> gold."; tGlobBad.Responses.Add(i); globBadFk = tGlobBad.FormKey; }

            // The CK formatting-subtag form <Global.Time=X> (modifier BEFORE the =) naming an uncovered global must ALSO
            // WARN — those produce the same silent [...] failure, so the plain <Global= anchor would miss them (PR #139 review).
            var tGlobSub = m.DialogTopics.AddNew(); tGlobSub.EditorID = "HcDvGlobSub"; tGlobSub.Quest.SetTo(qMain.FormKey);
            { var i = Info("HcDvGlobSubI"); i.Prompt = "Opens in <Global.Time=HcDvUnknownGlobal> hours."; tGlobSub.Responses.Add(i); globSubFk = tGlobSub.FormKey; }

            // PLAYERREF whitelist (Track B — engine-implicit sub-0x800 forms): a condition Run On PlayerRef (000014:Skyrim.esm),
            // which is NOT in this synthetic order, must NOT warn (the whitelist — the false-positive fix). The CONTROL runs on
            // a NON-whitelisted missing ref and MUST still warn, proving the whitelist is a precise 2-form set, not blanket.
            var tPlayerRef = m.DialogTopics.AddNew(); tPlayerRef.EditorID = "HcDvPlayerRef"; tPlayerRef.Quest.SetTo(qMain.FormKey);
            {
                var i = Info("HcDvPlayerRefI");
                var d = new GetActorValueConditionData { ActorValue = ActorValue.Health, RunOnType = Condition.RunOnType.Reference };
                d.Reference.SetTo(new FormKey(new ModKey("Skyrim", ModType.Master), 0x14));   // PlayerRef 000014:Skyrim.esm (engine-implicit)
                i.Conditions.Add(new ConditionFloat { CompareOperator = CompareOperator.GreaterThan, ComparisonValue = 0f, Data = d });
                tPlayerRef.Responses.Add(i); playerRefFk = tPlayerRef.FormKey;
            }
            var tPlayerRefCtl = m.DialogTopics.AddNew(); tPlayerRefCtl.EditorID = "HcDvPlayerRefCtl"; tPlayerRefCtl.Quest.SetTo(qMain.FormKey);
            {
                var i = Info("HcDvPlayerRefCtlI");
                var d = new GetActorValueConditionData { ActorValue = ActorValue.Health, RunOnType = Condition.RunOnType.Reference };
                d.Reference.SetTo(new FormKey(mKey, 0x00BBBB01));   // a synthetic-master ref that is NOT a record → dangling AND not whitelisted
                i.Conditions.Add(new ConditionFloat { CompareOperator = CompareOperator.GreaterThan, ComparisonValue = 0f, Data = d });
                tPlayerRefCtl.Responses.Add(i); playerRefCtlFk = tPlayerRefCtl.FormKey;
            }

            // INFO-CKPARITY-GAP (the new INFO CNAM/ENAM lint): a BARE INFO — NOT run through Info()/ApplyInfoDefaults —
            // so it lacks the FavorLevel (CNAM) + response-Flags (ENAM) subrecords the CK writes unconditionally. Two
            // Warnings expected, naming each subrecord (the CK-editor-crash catch). Quest set + the loops below give it a
            // branch + marker, so those are its ONLY issues. Subtype left non-Custom so the BNAM lint stays silent.
            var tCkGap = m.DialogTopics.AddNew(); tCkGap.EditorID = "HcDvCkGap"; tCkGap.Quest.SetTo(qMain.FormKey);
            tCkGap.Responses.Add(new DialogResponses(m.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcDvCkGapI" });
            ckGapFk = tCkGap.FormKey;

            // INFO-CKPARITY-OK: a CK-parity-COMPLETE INFO (via Info(), which fills both subrecords) is NOT flagged — the
            // explicit no-false-positive lock for the new lint (a real authored INFO carries CNAM+ENAM).
            var tCkOk = m.DialogTopics.AddNew(); tCkOk.EditorID = "HcDvCkOk"; tCkOk.Quest.SetTo(qMain.FormKey);
            tCkOk.Responses.Add(Info("HcDvCkOkI"));
            ckOkFk = tCkOk.FormKey;

            // Give every branch-LESS fixture topic the shared well-formed branch — SAME reason as the SNAM loop
            // below: this guard tests the GRAPH/CONDITION/VOICE lints, not the BNAM-absent lint (that's
            // dialogue-ckparity-guard's job), so its Custom topics must be branch-clean or a "no issues" arm would
            // trip on that unrelated Warning. tBadBranch keeps its deliberately-bad branch (its link IsNull is false),
            // so it's skipped and BAD-BRANCH still fires.
            foreach (var t in m.DialogTopics) if (t.Branch.IsNull) t.Branch.SetTo(branch.FormKey);

            // Give every fixture topic a well-formed SNAM marker (#131) so the new blank-marker Problem never fires
            // here — this guard tests the GRAPH/CONDITION/VOICE lints, not the marker (that's dialogue-subtype-marker-guard),
            // so its topics must be marker-clean or a "no issues" arm would trip on an unrelated defect.
            foreach (var t in m.DialogTopics) if (DialogueSubtype.IsBlankMarker(t.SubtypeName)) t.SubtypeName = new RecordType("CUST");

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

        // ---------- INFO-CKPARITY-GAP: a bare INFO missing CNAM/ENAM WARNs twice, naming each subrecord (CK-editor-crash catch) ----------
        {
            var t = One(DialogueValidate.Run(resolver, assets, ckGapFk));
            bool cnam = t is not null && t.Issues.Any(i => i.Severity == DialogueIssueSeverity.Warning
                && i.Message.Contains("CNAM", StringComparison.Ordinal) && i.Message.Contains("Creation Kit", StringComparison.Ordinal));
            bool enam = t is not null && t.Issues.Any(i => i.Severity == DialogueIssueSeverity.Warning
                && i.Message.Contains("ENAM", StringComparison.Ordinal) && i.Message.Contains("Creation Kit", StringComparison.Ordinal));
            all &= Pass("INFO-CKPARITY-GAP warns CNAM+ENAM", cnam && enam, t is null ? "no topic" : Issues(t));
        }

        // ---------- INFO-CKPARITY-OK: a CK-parity-complete INFO is NOT flagged — the no-false-positive lock ----------
        {
            var t = One(DialogueValidate.Run(resolver, assets, ckOkFk));
            bool ok = t is not null && t.InfoCount == 1
                && !t.Issues.Any(i => i.Message.Contains("CNAM", StringComparison.Ordinal) || i.Message.Contains("ENAM", StringComparison.Ordinal));
            all &= Pass("INFO-CKPARITY-OK not flagged", ok, t is null ? "no topic" : $"infos={t.InfoCount} issues={t.Issues.Count}: {Issues(t)}");
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

        // ---------- FRAGMENT-PRESENCE (item 8): a line carrying a real result-script FRAGMENT is reported HasFragment + tallied ----------
        {
            var t = One(DialogueValidate.Run(resolver, assets, scriptedFk));
            bool ok = t is not null && t.FragmentInfoCount == 1 && t.ScriptFindings.Count == 1 && t.ScriptFindings[0].HasFragment;
            all &= Pass("FRAGMENT-PRESENCE has fragment", ok, t is null ? "no topic" : $"fragInfos={t.FragmentInfoCount} hasFrag={(t.ScriptFindings.Count == 1 ? t.ScriptFindings[0].HasFragment.ToString() : "?")}");
        }

        // ---------- FRAGMENT-FREE (item 8): a plain voiced line is fragment-free, COUNTED (FragmentInfoCount 0), not omitted ----------
        {
            var t = One(DialogueValidate.Run(resolver, assets, voicedFk));
            bool ok = t is not null && t.FragmentInfoCount == 0 && t.InfoCount == 1 && t.VoiceLines.Count == 1 && t.ScriptFindings.Count == 0;
            all &= Pass("FRAGMENT-FREE plain voiced", ok, t is null ? "no topic" : $"fragInfos={t.FragmentInfoCount} infos={t.InfoCount} voice={t.VoiceLines.Count} script={t.ScriptFindings.Count}");
        }

        // ---------- CTDA-COUNT: a conditioned line is counted (the standing-limit teeth) ----------
        {
            var t = One(DialogueValidate.Run(resolver, assets, condFk));
            bool ok = t is not null && t.ConditionedInfoCount >= 1;
            all &= Pass("CTDA-COUNT counts conditions", ok, t is null ? "no topic" : $"conditioned={t?.ConditionedInfoCount}");
        }

        // ---------- COND-CLEAN (item 4): two well-formed conditions report ZERO condition issues — the no-false-positive keystone ----------
        {
            var t = One(DialogueValidate.Run(resolver, assets, condCleanFk));
            bool ok = t is not null && t.ConditionedInfoCount == 1 && t.Issues.Count == 0;
            all &= Pass("COND-CLEAN no false issues", ok, t is null ? "no topic" : $"cond={t.ConditionedInfoCount} issues={t.Issues.Count}: {Issues(t)}");
        }

        // ---------- COND-REF-UNSET (item 4): Run On a specific Reference with none set is a WARN ----------
        {
            var t = One(DialogueValidate.Run(resolver, assets, condRefUnsetFk));
            bool ok = t is not null && t.Issues.Any(i => i.Severity == DialogueIssueSeverity.Warning && i.Message.Contains("Run On a specific reference", StringComparison.Ordinal));
            all &= Pass("COND-REF-UNSET warns", ok, t is null ? "no topic" : Issues(t));
        }

        // ---------- COND-DEAD-ALIAS (item 4): an alias index the owning quest doesn't define is a WARN ----------
        {
            var t = One(DialogueValidate.Run(resolver, assets, condDeadAliasFk));
            bool ok = t is not null && t.Issues.Any(i => i.Severity == DialogueIssueSeverity.Warning && i.Message.Contains("no alias with that ID", StringComparison.Ordinal));
            all &= Pass("COND-DEAD-ALIAS warns", ok, t is null ? "no topic" : Issues(t));
        }

        // ---------- COND-DEAD-LOCALIAS (item 4): GetInCurrentLocAlias at an undefined location-alias is a WARN ----------
        {
            var t = One(DialogueValidate.Run(resolver, assets, condDeadLocAliasFk));
            bool ok = t is not null && t.Issues.Any(i => i.Severity == DialogueIssueSeverity.Warning
                && i.Message.Contains("location-alias", StringComparison.Ordinal) && i.Message.Contains("no alias with that ID", StringComparison.Ordinal));
            all &= Pass("COND-DEAD-LOCALIAS warns", ok, t is null ? "no topic" : Issues(t));
        }

        // ---------- COND-DEAD-QUESTALIAS (item 4): Run On:QuestAlias at an undefined alias is a WARN ----------
        {
            var t = One(DialogueValidate.Run(resolver, assets, condDeadQAliasFk));
            bool ok = t is not null && t.Issues.Any(i => i.Severity == DialogueIssueSeverity.Warning
                && i.Message.Contains("reference-alias", StringComparison.Ordinal) && i.Message.Contains("no alias with that ID", StringComparison.Ordinal));
            all &= Pass("COND-DEAD-QUESTALIAS warns", ok, t is null ? "no topic" : Issues(t));
        }

        // ---------- COND-ALIAS-OK (item 4): a REAL alias index is NOT flagged — the resolve-aware positive lock ----------
        {
            var t = One(DialogueValidate.Run(resolver, assets, condAliasOkFk));
            bool ok = t is not null && t.ConditionedInfoCount == 1 && t.Issues.Count == 0;
            all &= Pass("COND-ALIAS-OK not flagged", ok, t is null ? "no topic" : $"issues={t.Issues.Count}: {Issues(t)}");
        }

        // ---------- COND-ALIAS-FLOI (item 4): an ALIAS-mode form-param FLOI is NOT flagged — the binary-overlay false-positive lock ----------
        {
            var t = One(DialogueValidate.Run(resolver, assets, condAliasFloiFk));
            bool ok = t is not null && t.ConditionedInfoCount == 1 && t.Issues.Count == 0;
            all &= Pass("COND-ALIAS-FLOI not flagged", ok, t is null ? "no topic" : $"issues={t.Issues.Count}: {Issues(t)}");
        }

        // ---------- COND-DANGLING-PARAM (item 4): a condition form param not in the order is a WARN ----------
        {
            var t = One(DialogueValidate.Run(resolver, assets, condDanglingFk));
            bool ok = t is not null && t.Issues.Any(i => i.Severity == DialogueIssueSeverity.Warning
                && i.Message.Contains("condition", StringComparison.Ordinal) && i.Message.Contains("not in the active load order", StringComparison.Ordinal));
            all &= Pass("COND-DANGLING-PARAM warns", ok, t is null ? "no topic" : Issues(t));
        }

        // ---------- COND-DANGLING-GLOBAL (item 4): a ConditionGlobal vs a missing GLOB is a WARN ----------
        {
            var t = One(DialogueValidate.Run(resolver, assets, condGlobalFk));
            bool ok = t is not null && t.Issues.Any(i => i.Severity == DialogueIssueSeverity.Warning && i.Message.Contains("compares against global", StringComparison.Ordinal));
            all &= Pass("COND-DANGLING-GLOBAL warns", ok, t is null ? "no topic" : Issues(t));
        }

        // ---------- COND-GETISID-OK (item 4): GetIsID at a BASE object is NOT flagged — lint-5 no-false-positive lock ----------
        {
            var t = One(DialogueValidate.Run(resolver, assets, condGetIsIdOkFk));
            bool ok = t is not null && t.ConditionedInfoCount == 1 && t.Issues.Count == 0;
            all &= Pass("COND-GETISID-OK not flagged", ok, t is null ? "no topic" : $"issues={t.Issues.Count}: {Issues(t)}");
        }

        // ---------- COND-GETISID-PLACED (item 4): GetIsID pointed at a placed reference is a WARN (not the dangling-param one) ----------
        {
            var t = One(DialogueValidate.Run(resolver, assets, condGetIsIdFk));
            bool ok = t is not null && t.Issues.Any(i => i.Severity == DialogueIssueSeverity.Warning && i.Message.Contains("placed reference", StringComparison.Ordinal));
            all &= Pass("COND-GETISID-PLACED warns", ok, t is null ? "no topic" : Issues(t));
        }

        // ---------- TEXT-MOJIBAKE (item 1): non-ASCII player-facing text WARNs, naming the offending chars ----------
        {
            var t = One(DialogueValidate.Run(resolver, assets, encBadFk));
            bool ok = t is not null && t.Issues.Count(i => i.Severity == DialogueIssueSeverity.Warning
                && i.Message.Contains("non-ASCII", StringComparison.OrdinalIgnoreCase)) >= 2;   // the Prompt + the response Text
            all &= Pass("TEXT-MOJIBAKE warns non-ASCII", ok, t is null ? "no topic" : Issues(t));
        }

        // ---------- TEXT-CLEAN (item 1): pure-ASCII player-facing text is NOT flagged (no false positive) ----------
        {
            var t = One(DialogueValidate.Run(resolver, assets, encOkFk));
            bool ok = t is not null && !t.Issues.Any(i => i.Message.Contains("non-ASCII", StringComparison.OrdinalIgnoreCase));
            all &= Pass("TEXT-CLEAN no false flag", ok, t is null ? "no topic" : Issues(t));
        }

        // ---------- GLOBAL-TAG-OK (Track B): a <Global=X> covered by the quest's TextDisplayGlobals is NOT flagged ----------
        {
            var t = One(DialogueValidate.Run(resolver, assets, globOkFk));
            bool ok = t is not null && !t.Issues.Any(i => i.Message.Contains("TextDisplayGlobals", StringComparison.Ordinal));
            all &= Pass("GLOBAL-TAG-OK not flagged", ok, t is null ? "no topic" : $"issues={t.Issues.Count}: {Issues(t)}");
        }

        // ---------- GLOBAL-TAG-MISSING (Track B): a <Global=X> NOT in TextDisplayGlobals WARNs, naming X ----------
        {
            var t = One(DialogueValidate.Run(resolver, assets, globBadFk));
            bool ok = t is not null && t.Issues.Any(i => i.Severity == DialogueIssueSeverity.Warning
                && i.Message.Contains("TextDisplayGlobals", StringComparison.Ordinal) && i.Message.Contains("HcDvUnknownGlobal", StringComparison.Ordinal));
            all &= Pass("GLOBAL-TAG-MISSING warns", ok, t is null ? "no topic" : Issues(t));
        }

        // ---------- GLOBAL-TAG-SUBTAG (Track B): a <Global.Time=X> subtag form (modifier before =) with an uncovered global WARNs ----------
        {
            var t = One(DialogueValidate.Run(resolver, assets, globSubFk));
            bool ok = t is not null && t.Issues.Any(i => i.Severity == DialogueIssueSeverity.Warning
                && i.Message.Contains("TextDisplayGlobals", StringComparison.Ordinal) && i.Message.Contains("HcDvUnknownGlobal", StringComparison.Ordinal));
            all &= Pass("GLOBAL-TAG-SUBTAG warns", ok, t is null ? "no topic" : Issues(t));
        }

        // ---------- PLAYERREF-WHITELIST (Track B): Run On engine-implicit PlayerRef (000014:Skyrim.esm) is NOT flagged ----------
        {
            var t = One(DialogueValidate.Run(resolver, assets, playerRefFk));
            bool ok = t is not null && t.Issues.Count == 0;
            all &= Pass("PLAYERREF-WHITELIST not flagged", ok, t is null ? "no topic" : $"issues={t.Issues.Count}: {Issues(t)}");
        }

        // ---------- PLAYERREF-CONTROL (Track B): Run On a non-whitelisted missing ref still WARNs (precise, not blanket) ----------
        {
            var t = One(DialogueValidate.Run(resolver, assets, playerRefCtlFk));
            bool ok = t is not null && t.Issues.Any(i => i.Severity == DialogueIssueSeverity.Warning
                && i.Message.Contains("Run On reference", StringComparison.Ordinal) && i.Message.Contains("not in the active load order", StringComparison.Ordinal));
            all &= Pass("PLAYERREF-CONTROL warns missing", ok, t is null ? "no topic" : Issues(t));
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
            ? "RESULT: PASS — the dialogue-graph validator holds (no false PNAM-absence flag, LinkTo + dangling-PNAM teeth, deleted-skip, voice/script reuse, encoding lint, condition lints (item 4), <Global=X>/TextDisplayGlobals + PlayerRef-whitelist lints (Track B), fan-out, named rejects)."
            : "RESULT: FAIL — at least one arm regressed (see above).");
        try { Directory.Delete(tmpDir, recursive: true); } catch { }
        return all ? 0 : 1;
    }

    /// <summary>Set a condition FormLinkOrIndex (e.g. GetIsID.Object) to a FORM-mode target via its <c>.Link</c> —
    /// the inverse of WriteEngine.ReadFloiFormKey, by reflection (the union's typed setter isn't on the public getter
    /// interface). UseAliases/UsePackageData stay false (form mode) so ReadFloiFormKey reads the link back.</summary>
    static void SetFloiForm(object data, string prop, FormKey fk)
    {
        var floi = data.GetType().GetProperty(prop)!.GetValue(data)!;
        var link = floi.GetType().GetProperty("Link")!.GetValue(floi)!;
        link.GetType().GetMethod("SetTo", new[] { typeof(FormKey) })!.Invoke(link, new object[] { fk });
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
