using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The synthetic MO2 world the dialogue family's info-order and CK-parity facts are driven against.
///
/// <para>Four plugins: <see cref="VanillaName"/> (a base master by filename, carrying the stale-subtype topics the
/// SNAM ownership gate reads as "not the modder's"), <see cref="MasterName"/> (the topic + the CK-parity-complete view/branch/quest seeds),
/// <see cref="MidName"/> (re-lists 6 of the topic's 8 INFOs, PNAM-chained, in reverse — moves nothing),
/// <see cref="LastName"/> — the WINNER — (re-lists ONLY INFO 0 with no PNAM, which evicts it to the
/// tail).</para>
///
/// <para>And one plugin OUTSIDE the order: <see cref="PatchName"/>, in a disabled mod folder and in neither
/// loadorder.txt nor plugins.txt — the freshly authored patch an off-order fold names.</para>
///
/// <para><b>Never the shared instance for a test that locks a file.</b> Each of the three dialogue lock facts
/// (<c>UNREAD-WIRED</c>, <c>DEFINER-LOCK-LOUD</c>, <c>WINNER-LOCK-LOUD</c>) constructs its OWN
/// <see cref="DialogueWorld"/> via <c>new()</c> rather than the shared collection fixture — a held file is
/// unreadable to anything else in the process, so sharing it would make every other test's readability depend
/// on scheduling.</para>
///
/// <para><b>A lock test must force the index build FIRST</b> — call <c>Svc.Stats()</c> on a fresh world before
/// taking the hold. Locking a plugin before the first real query makes the index build SILENTLY EXCLUDE it,
/// which changes the topic's winner instead of surfacing a read failure, so the lock facts would quietly
/// assert something else. Deleting the <c>Stats()</c> call changes what those tests measure without failing
/// anything.</para>
/// </summary>
public sealed class DialogueWorld : IDisposable
{
    /// <summary>A base master BY FILENAME — what <see cref="ErrorCheck.BaseMasters"/> matches on, so its own records
    /// are the "vanilla, not the modder's" side of the SNAM ownership gate.</summary>
    public const string VanillaName = "Skyrim.esm";
    public const string MasterName = "HcDvMaster.esp";
    public const string MidName = "HcDvMid.esp";
    public const string LastName = "HcDvLast.esp";

    /// <summary>Creation Club content: in loadorder.txt, absent from plugins.txt, so it is FORCE-LOADED — the
    /// implicit group the load-order status shows, and content the modder no more authored than a base master's.</summary>
    public const string CcName = "ccHcTest.esl";

    /// <summary>The freshly authored patch: on disk in a DISABLED mod folder and in NEITHER loadorder.txt nor
    /// plugins.txt, so the active order does not have it. What an off-order fold names.</summary>
    public const string PatchName = "HcDvPatch.esp";

    public string Root { get; }
    public string Instance { get; }
    public string MasterPath { get; }
    public string MidPath { get; }
    public string LastPath { get; }

    /// <summary>The off-order patch's path on disk — inside a mod folder MO2 has disabled.</summary>
    public string PatchPath { get; private set; } = "";

    /// <summary>The off-order MASTER patch's path on disk.</summary>
    public string PatchEsmPath { get; private set; } = "";

    public LoadOrderService Svc { get; }

    /// <summary>The topic: 8 plain INFOs in master, no PNAM, so file order is the order — until mid/last override.</summary>
    public FormKey Topic { get; }

    /// <summary>The 8 INFO FormKeys in their ORIGINAL (master) order.</summary>
    public IReadOnlyList<FormKey> Info { get; }

    /// <summary>The line evicted from #1 to #8 by <see cref="LastName"/>'s no-PNAM re-list.</summary>
    public FormKey MovedLine => Info[0];

    public FormKey ViewOk { get; }
    public FormKey BranchOk { get; }
    public FormKey QuestOk { get; }

    /// <summary>The #660 shape: numeric Subtype = RechargeExit (73) beside SNAM = HELO (Hello, 79) — a topic
    /// carrying the pre-Dragonborn numbering, six low. The marker is authoritative.</summary>
    public FormKey StaleSubtypeTopic { get; }

    /// <summary>The control: Subtype and SNAM naming the same subtype, so a topic that agrees is never labelled.</summary>
    public FormKey AgreeingSubtypeTopic { get; }

    /// <summary>The same stale shape in the base master <see cref="VanillaName"/>, touched by nothing — Bethesda's own
    /// number, which the modder cannot act on, so the check stays quiet about it.</summary>
    public FormKey VanillaStaleTopic { get; }

    /// <summary>A stale base-master topic that <see cref="LastName"/> OVERRIDES, carrying the base record's pair
    /// forward UNCHANGED — Bethesda's number still, not the override's statement, so the check stays quiet.</summary>
    public FormKey VanillaStaleOverriddenTopic { get; }

    /// <summary>A base-master topic whose pair AGREES, overridden by the force-loaded <see cref="CcName"/> with a
    /// contradicting Subtype: a pair that plugin really did author, kept quiet only by who force-loads it.</summary>
    public FormKey CcOverriddenTopic { get; }

    /// <summary>Two fields edited apart, not an old file: Subtype=Custom (0) beside SNAM=HELO (79), a gap the
    /// Dragonborn renumbering cannot explain. The Subtype edit is an in-game no-op until SNAM is synced.</summary>
    public FormKey EditedApartSubtypeTopic { get; }

    /// <summary>SNAM=FVDL — index 3, the one modeled row Mutagen's enum leaves unnamed — beside Subtype=Custom, so a
    /// render has to name the subtype from the marker itself rather than hand back nothing.</summary>
    public FormKey FvdlMarkerTopic { get; }

    /// <summary>A topic whose SNAM is a non-blank marker the table does not model (<c>ZZZZ</c>) — neither blank nor a
    /// disagreement, and silently unbucketed in game if nothing says so.</summary>
    public FormKey UnmodeledMarkerTopic { get; }

    /// <summary>The off-order patch's own new topic, defined in <see cref="PatchName"/> and therefore in no active
    /// plugin at all — the topic a fold is the whole merge of.</summary>
    public FormKey PatchOwnTopic { get; }

    /// <summary>The two INFOs of <see cref="PatchOwnTopic"/>, in the patch's own list order.</summary>
    public IReadOnlyList<FormKey> PatchOwnInfo { get; }

    /// <summary>A topic the BASE MASTER defines and the regular winner overrides — where an off-order .esm has to
    /// land between the two, not at the end.</summary>
    public FormKey MasterBlockTopic { get; }

    /// <summary>Its three INFOs, in the base master's own list order.</summary>
    public IReadOnlyList<FormKey> MasterBlockInfo { get; }

    /// <summary>The off-order patch that is a MASTER by extension: MO2 sorts it into the master block, not onto
    /// the end of the order.</summary>
    public const string PatchEsmName = "HcDvPatch.esm";

    /// <summary>A second copy of <see cref="MidName"/>, in a DISABLED mod folder — the shadowed-copy fold, whose
    /// filename is active while this file is not the one the order loads.</summary>
    public const string ShadowModFolder = "MidShadowMod";

    readonly ResultsDirScope _results;

    /// <summary>The default world: <see cref="PatchName"/> sits on disk OUTSIDE the order.</summary>
    public DialogueWorld() : this(patchActive: false) { }

    /// <param name="patchActive">write the same patch into the ACTIVE order instead — the comparison arm for a
    /// folded read, so what the fold projects can be measured against what the order really says once the plugin
    /// is enabled. Its own instance, never the shared fixture.</param>
    public DialogueWorld(bool patchActive)
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-dialogue-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(Root, "game", "Data"));
        _results = new ResultsDirScope(Path.Combine(Root, "server-results"));

        var masterKey = ModKey.FromNameAndExtension(MasterName);
        var midKey = ModKey.FromNameAndExtension(MidName);
        var lastKey = ModKey.FromNameAndExtension(LastName);

        // The base master, by filename: two topics carrying the pre-Dragonborn numbering, one of which LastMod
        // overrides. Which side of the SNAM ownership gate a record falls on is decided by this filename.
        var sky = new SkyrimMod(new ModKey("Skyrim", ModType.Master), SkyrimRelease.SkyrimSE);
        var vanillaStale = sky.DialogTopics.AddNew(); vanillaStale.EditorID = "HcDvVanillaStale";
        vanillaStale.Subtype = DialogTopic.SubtypeEnum.RechargeExit;
        vanillaStale.SubtypeName = new RecordType("HELO");
        VanillaStaleTopic = vanillaStale.FormKey;
        var vanillaOver = sky.DialogTopics.AddNew(); vanillaOver.EditorID = "HcDvVanillaStaleOverridden";
        vanillaOver.Subtype = DialogTopic.SubtypeEnum.RechargeExit;
        vanillaOver.SubtypeName = new RecordType("HELO");
        VanillaStaleOverriddenTopic = vanillaOver.FormKey;
        // A topic the BASE MASTER defines with three lines, for the master-block placement: an off-order .esm
        // folded into it lands after this plugin and BEFORE the regular plugin that also touches it.
        var masterOrder = sky.DialogTopics.AddNew(); masterOrder.EditorID = "HcDvMasterOrder";
        MasterBlockTopic = masterOrder.FormKey;
        var masterInfo = new FormKey[3];
        for (int i = 0; i < 3; i++)
        {
            var r = new DialogResponses(sky.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = $"HcDvMasterLine{i}" };
            masterInfo[i] = r.FormKey;
            masterOrder.Responses.Add(r);
        }
        MasterBlockInfo = masterInfo;
        // …and one whose pair AGREES, for the Creation Club override to contradict on its own account.
        var ccBase = sky.DialogTopics.AddNew(); ccBase.EditorID = "HcDvCcOverridden";
        ccBase.Subtype = DialogTopic.SubtypeEnum.Hello;
        ccBase.SubtypeName = new RecordType("HELO");
        CcOverriddenTopic = ccBase.FormKey;

        var master = new SkyrimMod(masterKey, SkyrimRelease.SkyrimSE);

        DialogResponses NewInfo(string edid) => new(master.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = edid };

        var topic = master.DialogTopics.AddNew(); topic.EditorID = "HcDvOrder";
        Topic = topic.FormKey;
        var info = new FormKey[8];
        for (int i = 0; i < 8; i++) { var r = NewInfo($"HcDvLine{i}"); info[i] = r.FormKey; topic.Responses.Add(r); }
        Info = info;

        // Subtype-vs-SNAM pair (#660): one topic carrying the pre-Dragonborn numbering (RechargeExit=73 under
        // SNAM HELO, six low) and one where the two agree.
        var stale = master.DialogTopics.AddNew(); stale.EditorID = "HcDvStaleSubtype";
        stale.Subtype = DialogTopic.SubtypeEnum.RechargeExit;
        stale.SubtypeName = new RecordType("HELO");
        StaleSubtypeTopic = stale.FormKey;
        var agree = master.DialogTopics.AddNew(); agree.EditorID = "HcDvAgreeingSubtype";
        agree.Subtype = DialogTopic.SubtypeEnum.Hello;
        agree.SubtypeName = new RecordType("HELO");
        AgreeingSubtypeTopic = agree.FormKey;
        // Two fields edited apart: Custom (0) beside HELO (79) is a gap no renumbering explains.
        var apart = master.DialogTopics.AddNew(); apart.EditorID = "HcDvEditedApart";
        apart.Subtype = DialogTopic.SubtypeEnum.Custom;
        apart.SubtypeName = new RecordType("HELO");
        EditedApartSubtypeTopic = apart.FormKey;
        // The row Mutagen's enum omits (index 3) — the marker is the only name there is.
        var fvdl = master.DialogTopics.AddNew(); fvdl.EditorID = "HcDvFvdlMarker";
        fvdl.Subtype = DialogTopic.SubtypeEnum.Custom;
        fvdl.SubtypeName = new RecordType("FVDL");
        FvdlMarkerTopic = fvdl.FormKey;
        // A non-blank marker the table does not model — the silent-fallthrough case.
        var unmodeled = master.DialogTopics.AddNew(); unmodeled.EditorID = "HcDvUnmodeledMarker";
        unmodeled.Subtype = DialogTopic.SubtypeEnum.Hello;
        unmodeled.SubtypeName = new RecordType("ZZZZ");
        UnmodeledMarkerTopic = unmodeled.FormKey;

        // CK-parity-complete seeds — the no-false-positive lock for V1 (a real authored view/branch/quest never
        // renders as a gap).
        var view = master.DialogViews.AddNew(); view.EditorID = "HcDvViewOk";
        DialogueCkParity.ApplyViewDefaults(view); ViewOk = view.FormKey;
        var branch = master.DialogBranches.AddNew(); branch.EditorID = "HcDvBranchOk";
        // Flags (DNAM) is never filled — the create path refuses instead — so the OK fixture sets it itself.
        DialogueCkParity.ApplyBranchDefaults(branch); branch.Flags = DialogBranch.Flag.TopLevel;
        BranchOk = branch.FormKey;
        var quest = master.Quests.AddNew(); quest.EditorID = "HcDvQuestOk";
        quest.Objectives.Add(new QuestObjective { Index = 1 });
        DialogueCkParity.ApplyQuestDefaults(quest); QuestOk = quest.FormKey;

        Instance = Path.Combine(Root, "inst");
        var mods = Path.Combine(Instance, "mods");
        Directory.CreateDirectory(Path.Combine(mods, "VanillaStub"));
        Directory.CreateDirectory(Path.Combine(mods, "MasterMod"));
        Directory.CreateDirectory(Path.Combine(mods, "MidMod"));
        Directory.CreateDirectory(Path.Combine(mods, "LastMod"));
        MasterPath = Path.Combine(mods, "MasterMod", MasterName);
        MidPath = Path.Combine(mods, "MidMod", MidName);
        LastPath = Path.Combine(mods, "LastMod", LastName);
        sky.BeginWrite.ToPath(Path.Combine(mods, "VanillaStub", VanillaName)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        master.BeginWrite.ToPath(MasterPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        // MID: re-lists INFOs 2..7, PNAM-chained, in REVERSE — moves nothing (a well-behaved patch).
        var mid = new SkyrimMod(midKey, SkyrimRelease.SkyrimSE);
        var midTopic = (IDialogTopic)WriteEngine.GenericGetOrAddAsOverride(mid, topic);
        midTopic.Responses.Clear();
        for (int i = 7; i >= 2; i--)
        {
            var r = new DialogResponses(info[i], SkyrimRelease.SkyrimSE) { EditorID = $"HcDvLine{i}" };
            r.PreviousDialog.SetTo(info[i - 1]);
            midTopic.Responses.Add(r);
        }
        mid.BeginWrite.ToPath(MidPath).WithLoadOrder(new ISkyrimModGetter[] { master }).Write();

        // LAST (the winner): re-lists ONLY INFO 0, no PNAM — evicted from the top, appended to the bottom.
        var last = new SkyrimMod(lastKey, SkyrimRelease.SkyrimSE);
        var lastTopic = (IDialogTopic)WriteEngine.GenericGetOrAddAsOverride(last, topic);
        lastTopic.Responses.Clear();
        lastTopic.Responses.Add(new DialogResponses(info[0], SkyrimRelease.SkyrimSE) { EditorID = "HcDvLine0" });
        // …and overrides one stale base-master topic, carrying the stale number forward: now a mod's own record.
        WriteEngine.GenericGetOrAddAsOverride(last, vanillaOver);
        // …and re-lists the base master's FIRST line with no PNAM, which sends it to the bottom: the regular
        // plugin an off-order .esm has to be folded in AHEAD of.
        var lastMasterOrder = (IDialogTopic)WriteEngine.GenericGetOrAddAsOverride(last, masterOrder);
        lastMasterOrder.Responses.Clear();
        lastMasterOrder.Responses.Add(new DialogResponses(masterInfo[0], SkyrimRelease.SkyrimSE) { EditorID = "HcDvMasterLine0" });
        last.BeginWrite.ToPath(LastPath).WithLoadOrder(new ISkyrimModGetter[] { sky, master, mid }).Write();

        // CC (force-loaded, and the winner of its topic): overrides an AGREEING base topic with a contradicting
        // Subtype, so the pair is this plugin's own — only who force-loads it keeps the check quiet.
        var cc = new SkyrimMod(ModKey.FromNameAndExtension(CcName), SkyrimRelease.SkyrimSE);
        var ccTopic = (IDialogTopic)WriteEngine.GenericGetOrAddAsOverride(cc, ccBase);
        ccTopic.Subtype = DialogTopic.SubtypeEnum.Custom;
        Directory.CreateDirectory(Path.Combine(mods, "CcMod"));
        cc.BeginWrite.ToPath(Path.Combine(mods, "CcMod", CcName))
          .WithLoadOrder(new ISkyrimModGetter[] { sky, master, mid, last }).Write();

        // The freshly authored patch: written into a DISABLED mod folder and listed in neither loadorder.txt nor
        // plugins.txt, so nothing in the active order sees it. It re-lists INFO 3 with NO PNAM (the tail arm) and
        // INFO 5 with a PNAM naming INFO 1 (the after-target arm), and defines a topic of its own.
        var patchKey = ModKey.FromNameAndExtension(PatchName);
        var patch = new SkyrimMod(patchKey, SkyrimRelease.SkyrimSE);
        var patchTopic = (IDialogTopic)WriteEngine.GenericGetOrAddAsOverride(patch, topic);
        patchTopic.Responses.Clear();
        patchTopic.Responses.Add(new DialogResponses(info[3], SkyrimRelease.SkyrimSE) { EditorID = "HcDvLine3" });
        var reLinked = new DialogResponses(info[5], SkyrimRelease.SkyrimSE) { EditorID = "HcDvLine5" };
        reLinked.PreviousDialog.SetTo(info[1]);
        patchTopic.Responses.Add(reLinked);
        var ownTopic = patch.DialogTopics.AddNew(); ownTopic.EditorID = "HcDvPatchOwn";
        PatchOwnTopic = ownTopic.FormKey;
        var ownInfo = new FormKey[2];
        for (int i = 0; i < 2; i++)
        {
            var r = new DialogResponses(patch.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = $"HcDvPatchOwnLine{i}" };
            ownInfo[i] = r.FormKey;
            ownTopic.Responses.Add(r);
        }
        PatchOwnInfo = ownInfo;
        Directory.CreateDirectory(Path.Combine(mods, "PatchMod"));
        PatchPath = Path.Combine(mods, "PatchMod", PatchName);
        patch.BeginWrite.ToPath(PatchPath)
             .WithLoadOrder(new ISkyrimModGetter[] { sky, master, mid, last }).Write();

        // The off-order MASTER: a .esm, so MO2 sorts it into the master block rather than onto the end. It
        // re-lists the base master's MIDDLE line with no PNAM, which moves that line within the block — visible
        // only if the fold really is placed ahead of the regular plugin that re-lists the first line.
        var patchEsm = new SkyrimMod(ModKey.FromNameAndExtension(PatchEsmName), SkyrimRelease.SkyrimSE);
        var esmTopic = (IDialogTopic)WriteEngine.GenericGetOrAddAsOverride(patchEsm, masterOrder);
        esmTopic.Responses.Clear();
        esmTopic.Responses.Add(new DialogResponses(masterInfo[1], SkyrimRelease.SkyrimSE) { EditorID = "HcDvMasterLine1" });
        Directory.CreateDirectory(Path.Combine(mods, "PatchEsmMod"));
        PatchEsmPath = Path.Combine(mods, "PatchEsmMod", PatchEsmName);
        patchEsm.BeginWrite.ToPath(PatchEsmPath).WithLoadOrder(new ISkyrimModGetter[] { sky }).Write();

        // The SHADOWED copy: the same filename as an ACTIVE plugin, in a disabled folder. It re-lists INFO 2 with
        // no PNAM, so folding it moves that line to the bottom — and its rows must not render under the active
        // copy's name.
        var midShadow = new SkyrimMod(midKey, SkyrimRelease.SkyrimSE);
        var shadowTopic = (IDialogTopic)WriteEngine.GenericGetOrAddAsOverride(midShadow, topic);
        shadowTopic.Responses.Clear();
        shadowTopic.Responses.Add(new DialogResponses(info[2], SkyrimRelease.SkyrimSE) { EditorID = "HcDvLine2" });
        Directory.CreateDirectory(Path.Combine(mods, ShadowModFolder));
        midShadow.BeginWrite.ToPath(Path.Combine(mods, ShadowModFolder, MidName))
                 .WithLoadOrder(new ISkyrimModGetter[] { master }).Write();

        File.WriteAllText(Path.Combine(Instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");
        var prof = Path.Combine(Instance, "profiles", "Default");
        Directory.CreateDirectory(prof);
        // The patch is in the order only on the comparison arm — the same file, the same records, read as the
        // game would read it once MO2 enables it.
        var patchLine = patchActive ? PatchName + "\r\n" : "";
        File.WriteAllText(Path.Combine(prof, "loadorder.txt"),
            "# header\r\n" + VanillaName + "\r\n" + MasterName + "\r\n" + MidName + "\r\n" + LastName + "\r\n"
            + CcName + "\r\n" + patchLine);
        // Neither the base master nor the CC plugin is listed here — that absence is what makes them force-loaded.
        File.WriteAllText(Path.Combine(prof, "plugins.txt"),
            "*" + MasterName + "\r\n*" + MidName + "\r\n*" + LastName + "\r\n" + (patchActive ? "*" + PatchName + "\r\n" : ""));
        // PatchMod, PatchEsmMod and the shadow folder are switched OFF: their files are on disk and out of the
        // order, which is what a fold names. MidMod sits above the shadow folder, so the copy the order loads —
        // and the copy a {file, mod} fold of the shadow is measured against — is MidMod's.
        File.WriteAllText(Path.Combine(prof, "modlist.txt"),
            "# header\r\n" + (patchActive ? "+" : "-") + "PatchMod\r\n-PatchEsmMod\r\n+CcMod\r\n+LastMod\r\n+MidMod\r\n-"
            + ShadowModFolder + "\r\n+MasterMod\r\n+VanillaStub\r\n");

        var store = new UserConfigStore(Path.Combine(Root, "user.json"));
        Svc = LoadOrderService.WithInstance(Instance, 0, store);
    }

    public void Dispose()
    {
        Svc.Dispose();
        _results.Dispose();   // before the delete below: the static must not name a removed directory
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}

/// <summary>The shared, read-only dialogue world. One build per test class collection — never for a test that
/// holds a file open.</summary>
public sealed class DialogueFixture : IDisposable
{
    public DialogueWorld W { get; } = new();
    public LoadOrderService Svc => W.Svc;
    public void Dispose() => W.Dispose();
}

[CollectionDefinition("dialogue")]
public sealed class DialogueCollection : ICollectionFixture<DialogueFixture> { }
