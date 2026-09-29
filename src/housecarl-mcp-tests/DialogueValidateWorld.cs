using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;

namespace HousecarlMcpTests;

/// <summary>The dialogue validator's fixture, moved from the retired <c>dialogue-validate-guard</c> probe: one
/// synthetic master with a topic per validation shape, a quest owning two topics, CK-parity gap and complete
/// DLVW/DLBR/QUST inputs, and a weapon. No .fuz or .pex is planted. Every seed runs through the real
/// <c>DialogueValidate.Run</c> once; the tests read the reports.</summary>
internal static class DialogueValidateWorld
{
    static readonly string Dir = Path.Combine(Path.GetTempPath(), "hc-dialogue-validate-" + Guid.NewGuid().ToString("N"));
    static readonly Lazy<Dictionary<string, DialogueValidationReport>> Reports = new(Build);

    internal static DialogueValidationReport Report(string seed) => Reports.Value[seed];

    /// <summary>The single topic of a topic seed's report; the test fails if the report is not exactly one topic.</summary>
    internal static TopicValidation Topic(string seed)
    {
        var r = Report(seed);
        if (r.Topics.Count != 1)
            throw new InvalidOperationException($"seed '{seed}' returned {r.Topics.Count} topics (kind {r.InputKind}, error {r.Error})");
        return r.Topics[0];
    }

    static Dictionary<string, DialogueValidationReport> Build()
    {
        Directory.CreateDirectory(Dir);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { Directory.Delete(Dir, true); } catch { /* best-effort */ } };

        var mKey = new ModKey("HcDvMaster", ModType.Master);
        string mPath = Path.Combine(Dir, mKey.FileName.String);
        var seeds = new Dictionary<string, FormKey>();
        var m = new SkyrimMod(mKey, SkyrimRelease.SkyrimSE);

        var qMain = m.Quests.AddNew(); qMain.EditorID = "HcDvQuestMain";
        qMain.Aliases.Add(new QuestAlias { ID = 0, Name = "HcDvAlias0" });
        var qFan = m.Quests.AddNew(); qFan.EditorID = "HcDvQuestFan"; seeds["fanout"] = qFan.FormKey;
        var branch = m.DialogBranches.AddNew(); branch.EditorID = "HcDvBranch";
        var voice = m.VoiceTypes.AddNew(); voice.EditorID = "HcDvVoice";
        var npc = m.Npcs.AddNew(); npc.EditorID = "HcDvNpc"; npc.Voice.SetTo(voice.FormKey);
        var weap = m.Weapons.AddNew(); weap.EditorID = "HcDvWeap"; weap.BasicStats = new WeaponBasicStats { Damage = 10 };
        seeds["weapon"] = weap.FormKey;

        // A CK-parity-complete INFO, filled by the same ApplyInfoDefaults the create path runs.
        DialogResponses Info(string edid)
        {
            var info = new DialogResponses(m.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = edid };
            DialogueCkParity.ApplyInfoDefaults(info);
            return info;
        }

        DialogTopic Topic(string seed, string edid)
        {
            var t = m.DialogTopics.AddNew(); t.EditorID = edid;
            t.Quest.SetTo(qMain.FormKey);
            seeds[seed] = t.FormKey;
            return t;
        }

        DialogTopic Conditioned(string seed, string edid, Condition c)
        {
            var t = Topic(seed, edid);
            var i = Info(edid + "I"); i.Conditions.Add(c); t.Responses.Add(i);
            return t;
        }

        var tTarget = m.DialogTopics.AddNew(); tTarget.EditorID = "HcDvLinkTarget"; tTarget.Quest.SetTo(qMain.FormKey);
        tTarget.Responses.Add(Info("HcDvLinkTargetI"));

        var tClean = Topic("clean", "HcDvClean"); tClean.Branch.SetTo(branch.FormKey);
        var c1 = Info("HcDvCleanI1"); c1.LinkTo.Add(new FormLink<IDialogTopicGetter>(tTarget.FormKey));
        tClean.Responses.Add(Info("HcDvCleanI0")); tClean.Responses.Add(c1);

        var lb = Info("HcDvLinkBadI"); lb.LinkTo.Add(new FormLink<IDialogTopicGetter>(new FormKey(mKey, 0x00BBBBBB)));
        Topic("linkto-dangle", "HcDvLinkBad").Responses.Add(lb);

        var pb = Info("HcDvPnamBadI"); pb.PreviousDialog.SetTo(new FormKey(mKey, 0x00CCCCCC));
        Topic("pnam-dangle", "HcDvPnamBad").Responses.Add(pb);

        var tPnamOk = Topic("pnam-resolves", "HcDvPnamOk");
        var pa = Info("HcDvPnamOkA"); var pbk = Info("HcDvPnamOkB"); pbk.PreviousDialog.SetTo(pa.FormKey);
        tPnamOk.Responses.Add(pa); tPnamOk.Responses.Add(pbk);

        var tDeleted = Topic("deleted", "HcDvDeleted");
        tDeleted.Responses.Add(Info("HcDvDeletedLive"));
        var del = Info("HcDvDeletedGone"); del.IsDeleted = true; tDeleted.Responses.Add(del);

        var tNoQ = m.DialogTopics.AddNew(); tNoQ.EditorID = "HcDvNoQuest"; seeds["no-quest"] = tNoQ.FormKey;
        tNoQ.Responses.Add(Info("HcDvNqI0"));

        var tBadB = Topic("bad-branch", "HcDvBadBranch"); tBadB.Branch.SetTo(new FormKey(mKey, 0x00ABCDEF));
        tBadB.Responses.Add(Info("HcDvBbI0"));

        var vi = Info("HcDvVoicedI"); vi.Speaker.SetTo(npc.FormKey); vi.Responses.Add(new DialogResponse { ResponseNumber = 1 });
        Topic("voiced", "HcDvVoiced").Responses.Add(vi);

        var si = Info("HcDvScriptedI");
        si.VirtualMachineAdapter = new DialogResponsesAdapter { ScriptFragments = new ScriptFragments {
            FileName = "HcDvScriptClass", OnEnd = new ScriptFragment { ScriptName = "HcDvScriptClass", FragmentName = "Fragment_0" } } };
        Topic("scripted", "HcDvScripted").Responses.Add(si);

        Conditioned("ctda-count", "HcDvCond", new ConditionFloat { CompareOperator = CompareOperator.EqualTo, ComparisonValue = 1f,
            Data = new GetActorValueConditionData { ActorValue = ActorValue.Conjuration } });

        var ebi = Info("HcDvEncBadI"); ebi.Prompt = "Wait…";
        ebi.Responses.Add(new DialogResponse { ResponseNumber = 1, Text = "Take it — or leave it" });
        Topic("text-mojibake", "HcDvEncBad").Responses.Add(ebi);
        var eoi = Info("HcDvEncOkI"); eoi.Prompt = "Wait...";
        eoi.Responses.Add(new DialogResponse { ResponseNumber = 1, Text = "Take it - or leave it" });
        Topic("text-clean", "HcDvEncOk").Responses.Add(eoi);

        {
            var t = Topic("cond-clean", "HcDvCondClean"); var i = Info("HcDvCondCleanI");
            var d1 = new GetStageConditionData { RunOnType = Condition.RunOnType.Subject }; SetFloiForm(d1, "Quest", qMain.FormKey);
            i.Conditions.Add(new ConditionFloat { CompareOperator = CompareOperator.GreaterThanOrEqualTo, ComparisonValue = 10f, Data = d1 });
            var d2 = new GetActorValueConditionData { ActorValue = ActorValue.Health, RunOnType = Condition.RunOnType.Subject };
            i.Conditions.Add(new ConditionFloat { CompareOperator = CompareOperator.GreaterThan, ComparisonValue = 0f, Data = d2 });
            t.Responses.Add(i);
        }

        Conditioned("cond-ref-unset", "HcDvCondRefUnset", new ConditionFloat { CompareOperator = CompareOperator.GreaterThan, ComparisonValue = 0f,
            Data = new GetActorValueConditionData { ActorValue = ActorValue.Health, RunOnType = Condition.RunOnType.Reference } });
        Conditioned("cond-dead-alias", "HcDvCondDeadAlias", new ConditionFloat { CompareOperator = CompareOperator.EqualTo, ComparisonValue = 1f,
            Data = new GetIsAliasRefConditionData { ReferenceAliasIndex = 99, RunOnType = Condition.RunOnType.Subject } });
        Conditioned("cond-dead-localias", "HcDvCondDeadLocAlias", new ConditionFloat { CompareOperator = CompareOperator.EqualTo, ComparisonValue = 1f,
            Data = new GetInCurrentLocAliasConditionData { LocationAliasIndex = 99, RunOnType = Condition.RunOnType.Subject } });
        Conditioned("cond-dead-questalias", "HcDvCondDeadQAlias", new ConditionFloat { CompareOperator = CompareOperator.GreaterThan, ComparisonValue = 0f,
            Data = new GetActorValueConditionData { ActorValue = ActorValue.Health, RunOnType = Condition.RunOnType.QuestAlias, RunOnTypeIndex = 99 } });
        Conditioned("cond-alias-ok", "HcDvCondAliasOk", new ConditionFloat { CompareOperator = CompareOperator.EqualTo, ComparisonValue = 1f,
            Data = new GetIsAliasRefConditionData { ReferenceAliasIndex = 0, RunOnType = Condition.RunOnType.Subject } });
        {
            var d = new HasPerkConditionData { RunOnType = Condition.RunOnType.Subject, UseAliases = true };
            d.Perk = new FormLinkOrIndex<IPerkGetter>(d, 7u);   // alias index 7: index mode, a bogus low link on the overlay
            Conditioned("cond-alias-floi", "HcDvCondAliasFloi", new ConditionFloat { CompareOperator = CompareOperator.EqualTo, ComparisonValue = 1f, Data = d });
        }
        {
            var d = new GetStageConditionData { RunOnType = Condition.RunOnType.Subject }; SetFloiForm(d, "Quest", new FormKey(mKey, 0x00DDDDDD));
            Conditioned("cond-dangling-param", "HcDvCondDangling", new ConditionFloat { CompareOperator = CompareOperator.GreaterThanOrEqualTo, ComparisonValue = 10f, Data = d });
        }
        {
            var cg = new ConditionGlobal { CompareOperator = CompareOperator.EqualTo,
                Data = new GetActorValueConditionData { ActorValue = ActorValue.Health, RunOnType = Condition.RunOnType.Subject } };
            cg.ComparisonValue.SetTo(new FormKey(mKey, 0x00EEEEEE));
            Conditioned("cond-dangling-global", "HcDvCondGlobal", cg);
        }

        var condCell = new Cell(m.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcDvCondCell", Flags = Cell.Flag.IsInteriorCell };
        var condPlaced = new PlacedObject(m.GetNextFormKey(), SkyrimRelease.SkyrimSE); condPlaced.Base.SetTo(weap.FormKey);
        condCell.Persistent.Add(condPlaced);
        var condSub = new CellSubBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellSubBlock }; condSub.Cells.Add(condCell);
        var condBlock = new CellBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellBlock }; condBlock.SubBlocks.Add(condSub);
        m.Cells.Records.Add(condBlock);
        {
            var d = new GetIsIDConditionData { RunOnType = Condition.RunOnType.Subject }; SetFloiForm(d, "Object", npc.FormKey);
            Conditioned("cond-getisid-ok", "HcDvCondGetIsIdOk", new ConditionFloat { CompareOperator = CompareOperator.EqualTo, ComparisonValue = 1f, Data = d });
        }
        {
            var d = new GetIsIDConditionData { RunOnType = Condition.RunOnType.Subject }; SetFloiForm(d, "Object", condPlaced.FormKey);
            Conditioned("cond-getisid-placed", "HcDvCondGetIsId", new ConditionFloat { CompareOperator = CompareOperator.EqualTo, ComparisonValue = 1f, Data = d });
        }

        var tf1 = m.DialogTopics.AddNew(); tf1.EditorID = "HcDvFan1"; tf1.Quest.SetTo(qFan.FormKey); tf1.Responses.Add(Info("HcDvFan1I"));
        var tf2 = m.DialogTopics.AddNew(); tf2.EditorID = "HcDvFan2"; tf2.Quest.SetTo(qFan.FormKey); tf2.Responses.Add(Info("HcDvFan2I"));

        var knownGlob = new GlobalShort(m.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcDvKnownGlobal" };
        m.Globals.Add(knownGlob);
        qMain.TextDisplayGlobals.Add(new FormLink<IGlobalGetter>(knownGlob.FormKey));
        { var i = Info("HcDvGlobOkI"); i.Prompt = "It will cost <Global=HcDvKnownGlobal> gold."; Topic("global-tag-ok", "HcDvGlobOk").Responses.Add(i); }
        { var i = Info("HcDvGlobBadI"); i.Prompt = "It will cost <Global=HcDvUnknownGlobal> gold."; Topic("global-tag-missing", "HcDvGlobBad").Responses.Add(i); }
        { var i = Info("HcDvGlobSubI"); i.Prompt = "Opens in <Global.Time=HcDvUnknownGlobal> hours."; Topic("global-tag-subtag", "HcDvGlobSub").Responses.Add(i); }

        {
            var d = new GetActorValueConditionData { ActorValue = ActorValue.Health, RunOnType = Condition.RunOnType.Reference };
            d.Reference.SetTo(new FormKey(new ModKey("Skyrim", ModType.Master), 0x14));   // PlayerRef, engine-implicit
            Conditioned("playerref", "HcDvPlayerRef", new ConditionFloat { CompareOperator = CompareOperator.GreaterThan, ComparisonValue = 0f, Data = d });
        }
        {
            var d = new GetActorValueConditionData { ActorValue = ActorValue.Health, RunOnType = Condition.RunOnType.Reference };
            d.Reference.SetTo(new FormKey(mKey, 0x00BBBB01));   // no record and not whitelisted
            Conditioned("playerref-control", "HcDvPlayerRefCtl", new ConditionFloat { CompareOperator = CompareOperator.GreaterThan, ComparisonValue = 0f, Data = d });
        }

        // A bare INFO: no ApplyInfoDefaults, so no CNAM or ENAM.
        Topic("info-ckparity-gap", "HcDvCkGap").Responses.Add(new DialogResponses(m.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcDvCkGapI" });
        Topic("info-ckparity-ok", "HcDvCkOk").Responses.Add(Info("HcDvCkOkI"));

        var vGap = m.DialogViews.AddNew(); vGap.EditorID = "HcDvViewGap"; seeds["view-gap"] = vGap.FormKey;
        var vOk = m.DialogViews.AddNew(); vOk.EditorID = "HcDvViewOk";
        DialogueCkParity.ApplyViewDefaults(vOk); seeds["view-ok"] = vOk.FormKey;

        var brGap = m.DialogBranches.AddNew(); brGap.EditorID = "HcDvBrGap"; seeds["branch-gap"] = brGap.FormKey;
        var brOk = m.DialogBranches.AddNew(); brOk.EditorID = "HcDvBrOk";
        DialogueCkParity.ApplyBranchDefaults(brOk); brOk.Flags = DialogBranch.Flag.TopLevel;
        seeds["branch-ok"] = brOk.FormKey;

        var qGap = m.Quests.AddNew(); qGap.EditorID = "HcDvQGap";
        qGap.Objectives.Add(new QuestObjective { Index = 1 }); seeds["quest-gap"] = qGap.FormKey;
        var qOk = m.Quests.AddNew(); qOk.EditorID = "HcDvQOk";
        qOk.Objectives.Add(new QuestObjective { Index = 1 });
        DialogueCkParity.ApplyQuestDefaults(qOk); seeds["quest-ok"] = qOk.FormKey;

        // Every topic gets a well-formed branch and SNAM marker, so the no-issue seeds do not trip those lints.
        foreach (var t in m.DialogTopics) if (t.Branch.IsNull) t.Branch.SetTo(branch.FormKey);
        foreach (var t in m.DialogTopics) if (DialogueSubtype.IsBlankMarker(t.SubtypeName)) t.SubtypeName = new RecordType("CUST");

        m.BeginWrite.ToPath(mPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        seeds["not-found"] = new FormKey(new ModKey("HcDvGhost", ModType.Plugin), 0x000800);

        var dataDir = Path.Combine(Dir, "data"); Directory.CreateDirectory(dataDir);   // empty: no .fuz, no .pex
        using var resolver = LoadOrderResolver.Build(new[] { mPath });
        using var assets = AssetResolver.Build("", "", dataDir, Array.Empty<string>(), Array.Empty<ActiveArchive>());
        return seeds.ToDictionary(s => s.Key, s => DialogueValidate.Run(resolver, assets, s.Value));
    }

    /// <summary>Set a condition FormLinkOrIndex to a form-mode target through its <c>.Link</c>, by reflection.</summary>
    static void SetFloiForm(object data, string prop, FormKey fk)
    {
        var floi = data.GetType().GetProperty(prop)!.GetValue(data)!;
        var link = floi.GetType().GetProperty("Link")!.GetValue(floi)!;
        link.GetType().GetMethod("SetTo", new[] { typeof(FormKey) })!.Invoke(link, new object[] { fk });
    }
}
