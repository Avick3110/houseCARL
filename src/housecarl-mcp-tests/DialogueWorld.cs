using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The synthetic MO2 world the dialogue family's info-order and CK-parity facts are driven against. Ported
/// from <c>DialogueInfoOrderProbe</c> / <c>DialogueValidateGuardProbe</c> (#486 PR 2) as a real MO2 instance.
///
/// <para>Three plugins: <see cref="MasterName"/> (the topic + the CK-parity-complete view/branch/quest seeds),
/// <see cref="MidName"/> (re-lists 6 of the topic's 8 INFOs, PNAM-chained, in reverse — moves nothing),
/// <see cref="LastName"/> — the WINNER — (re-lists ONLY INFO 0 with no PNAM, which evicts it to the tail: the
/// reported #275 shape). <see cref="Order"/>'s expected sequence proves the merge.</para>
///
/// <para><b>Never the shared instance for a test that locks a file.</b> Each of the three dialogue lock facts
/// (<c>UNREAD-WIRED</c>, <c>DEFINER-LOCK-LOUD</c>, <c>WINNER-LOCK-LOUD</c>) constructs its OWN
/// <see cref="DialogueWorld"/> via <c>new()</c> rather than the shared collection fixture — a held file is
/// unreadable to anything else in the process, so sharing it would make every other test's readability depend
/// on scheduling (the same rule <c>ScriptsWorld</c>'s own doc states, and why <c>HeldOpenTests</c> builds its
/// own one-plugin world instead of locking the shared one).</para>
///
/// <para><b>A lock test must force the index build FIRST</b> — call <c>Svc.Stats()</c> on a fresh world before
/// taking the hold. Measured on this fixture: locking a plugin before the first real query makes the index build
/// SILENTLY EXCLUDE it, which changes the topic's winner instead of surfacing a read failure, so the lock facts
/// would quietly assert something else. The silent exclusion itself is the product-level concern in issue #353,
/// not a property of this fixture; the <c>Stats()</c> call is how these tests stay out of its way, and deleting
/// it changes what the three lock tests measure without failing anything.</para>
/// </summary>
public sealed class DialogueWorld : IDisposable
{
    public const string MasterName = "HcDvMaster.esp";
    public const string MidName = "HcDvMid.esp";
    public const string LastName = "HcDvLast.esp";

    public string Root { get; }
    public string Instance { get; }
    public string MasterPath { get; }
    public string MidPath { get; }
    public string LastPath { get; }

    public LoadOrderService Svc { get; }

    /// <summary>The topic: 8 plain INFOs in master, no PNAM, so file order is the order — until mid/last override.</summary>
    public FormKey Topic { get; }

    /// <summary>The 8 INFO FormKeys in their ORIGINAL (master) order.</summary>
    public IReadOnlyList<FormKey> Info { get; }

    /// <summary>The line evicted from #1 to #8 by <see cref="LastName"/>'s no-PNAM re-list — the #275 shape.</summary>
    public FormKey MovedLine => Info[0];

    public FormKey ViewOk { get; }
    public FormKey BranchOk { get; }
    public FormKey QuestOk { get; }

    public DialogueWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-dialogue-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(Root, "game", "Data"));

        var masterKey = ModKey.FromNameAndExtension(MasterName);
        var midKey = ModKey.FromNameAndExtension(MidName);
        var lastKey = ModKey.FromNameAndExtension(LastName);

        var master = new SkyrimMod(masterKey, SkyrimRelease.SkyrimSE);

        DialogResponses NewInfo(string edid) => new(master.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = edid };

        var topic = master.DialogTopics.AddNew(); topic.EditorID = "HcDvOrder";
        Topic = topic.FormKey;
        var info = new FormKey[8];
        for (int i = 0; i < 8; i++) { var r = NewInfo($"HcDvLine{i}"); info[i] = r.FormKey; topic.Responses.Add(r); }
        Info = info;

        // CK-parity-complete seeds — the no-false-positive lock for V1 (a real authored view/branch/quest never
        // renders as a gap).
        var view = master.DialogViews.AddNew(); view.EditorID = "HcDvViewOk";
        DialogueCkParity.ApplyViewDefaults(view); ViewOk = view.FormKey;
        var branch = master.DialogBranches.AddNew(); branch.EditorID = "HcDvBranchOk";
        DialogueCkParity.ApplyBranchDefaults(branch); BranchOk = branch.FormKey;
        var quest = master.Quests.AddNew(); quest.EditorID = "HcDvQuestOk";
        quest.Objectives.Add(new QuestObjective { Index = 1 });
        DialogueCkParity.ApplyQuestDefaults(quest); QuestOk = quest.FormKey;

        Instance = Path.Combine(Root, "inst");
        var mods = Path.Combine(Instance, "mods");
        Directory.CreateDirectory(Path.Combine(mods, "MasterMod"));
        Directory.CreateDirectory(Path.Combine(mods, "MidMod"));
        Directory.CreateDirectory(Path.Combine(mods, "LastMod"));
        MasterPath = Path.Combine(mods, "MasterMod", MasterName);
        MidPath = Path.Combine(mods, "MidMod", MidName);
        LastPath = Path.Combine(mods, "LastMod", LastName);
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
        last.BeginWrite.ToPath(LastPath).WithLoadOrder(new ISkyrimModGetter[] { master, mid }).Write();

        File.WriteAllText(Path.Combine(Instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");
        var prof = Path.Combine(Instance, "profiles", "Default");
        Directory.CreateDirectory(prof);
        File.WriteAllText(Path.Combine(prof, "loadorder.txt"), "# header\r\n" + MasterName + "\r\n" + MidName + "\r\n" + LastName + "\r\n");
        File.WriteAllText(Path.Combine(prof, "plugins.txt"), "*" + MasterName + "\r\n*" + MidName + "\r\n*" + LastName + "\r\n");
        File.WriteAllText(Path.Combine(prof, "modlist.txt"), "# header\r\n+LastMod\r\n+MidMod\r\n+MasterMod\r\n");

        var store = new UserConfigStore(Path.Combine(Root, "user.json"));
        Svc = LoadOrderService.WithInstance(Instance, 0, store);
    }

    public void Dispose()
    {
        Svc.Dispose();
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
