using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// #565: three patches each carried their own override of one shared leveled list, only the last-loaded fork was
/// live, and every write reported success. A write into a patch now says when the record it overrides is already
/// overridden somewhere else.
///
/// <para>The world: a master defining a leveled list and two weapons, and one houseCARL-owned patch (HcForkA.esp)
/// that already overrides the list — so a write into a DIFFERENT patch forks it, and a write into HcForkA itself
/// does not.</para>
/// </summary>
[Trait("tier", "integration")]
public sealed class ForkWarningTests : IDisposable
{
    const string MasterName = "HcForkMaster.esm";
    const string PatchAName = "HcForkA.esp";
    const string ForeignName = "HcForkForeign.esp";

    readonly string _root;
    readonly LoadOrderService _svc;
    readonly FormKey _list, _foreignList, _bothList, _topic, _info, _swordA, _swordB;

    public ForkWarningTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "hc-fork-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "game", "Data"));

        var master = new SkyrimMod(new ModKey("HcForkMaster", ModType.Master), SkyrimRelease.SkyrimSE);
        var a = master.Weapons.AddNew(); a.EditorID = "HcForkSwordA"; _swordA = a.FormKey;
        var b = master.Weapons.AddNew(); b.EditorID = "HcForkSwordB"; _swordB = b.FormKey;
        var ll = master.LeveledItems.AddNew();
        ll.EditorID = "HcForkList";
        ll.Entries = new Noggog.ExtendedList<LeveledItemEntry>
        {
            new LeveledItemEntry { Data = new LeveledItemEntryData { Level = 1, Count = 1, Reference = new FormLink<IItemGetter>(_swordA) } },
        };
        _list = ll.FormKey;
        // A second list, overridden only by the third-party plugin below — the control for the fresh-patch arm.
        var fl = master.LeveledItems.AddNew();
        fl.EditorID = "HcForkForeignList";
        fl.Entries = new Noggog.ExtendedList<LeveledItemEntry>
        {
            new LeveledItemEntry { Data = new LeveledItemEntryData { Level = 1, Count = 1, Reference = new FormLink<IItemGetter>(_swordA) } },
        };
        _foreignList = fl.FormKey;
        // A third list both of them override — one record with two forkers.
        var bl = master.LeveledItems.AddNew();
        bl.EditorID = "HcForkBothList";
        bl.Entries = new Noggog.ExtendedList<LeveledItemEntry>
        {
            new LeveledItemEntry { Data = new LeveledItemEntryData { Level = 1, Count = 1, Reference = new FormLink<IItemGetter>(_swordA) } },
        };
        _bothList = bl.FormKey;
        // A topic with one line in it: the nested record whose CONTAINER is what a patch overrides.
        var topic = master.DialogTopics.AddNew();
        topic.EditorID = "HcForkTopic";
        var info = new DialogResponses(master.GetNextFormKey(), SkyrimRelease.SkyrimSE);
        topic.Responses.Add(info);
        _topic = topic.FormKey;
        _info = info.FormKey;

        // Patch A: a real override of that same list, the state the bug starts from — and of the TOPIC, but not of
        // the line inside it, which is what makes the container the only thing a nested write collides with.
        var patchA = new SkyrimMod(new ModKey("HcForkA", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var ovr = patchA.LeveledItems.GetOrAddAsOverride(ll);
        ovr.Entries!.Add(new LeveledItemEntry { Data = new LeveledItemEntryData { Level = 1, Count = 1, Reference = new FormLink<IItemGetter>(_swordB) } });
        patchA.LeveledItems.GetOrAddAsOverride(bl).ChanceNone = Noggog.Percent.FactoryPutInRange(0.10);
        patchA.DialogTopics.GetOrAddAsOverride(topic);

        // A plugin houseCARL did NOT make, overriding the other list — a third-party mod, not a sibling patch.
        var foreign = new SkyrimMod(new ModKey("HcForkForeign", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var fovr = foreign.LeveledItems.GetOrAddAsOverride(fl);
        fovr.Entries!.Add(new LeveledItemEntry { Data = new LeveledItemEntryData { Level = 1, Count = 1, Reference = new FormLink<IItemGetter>(_swordB) } });
        foreign.LeveledItems.GetOrAddAsOverride(bl).ChanceNone = Noggog.Percent.FactoryPutInRange(0.20);

        var instance = Path.Combine(_root, "inst");
        var mods = Path.Combine(instance, "mods");
        var masterFolder = Path.Combine(mods, "ForkMasterMod");
        var patchFolder = Path.Combine(mods, "houseCARL - HcForkA");
        var foreignFolder = Path.Combine(mods, "ForkForeignMod");
        Directory.CreateDirectory(masterFolder);
        Directory.CreateDirectory(patchFolder);
        Directory.CreateDirectory(foreignFolder);
        master.BeginWrite.ToPath(Path.Combine(masterFolder, MasterName))
            .WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        patchA.BeginWrite.ToPath(Path.Combine(patchFolder, PatchAName))
            .WithLoadOrder(new ISkyrimModGetter[] { master }).Write();
        foreign.BeginWrite.ToPath(Path.Combine(foreignFolder, ForeignName))
            .WithLoadOrder(new ISkyrimModGetter[] { master }).Write();
        // The ownership marker, so into="HcForkA.esp" reaches this folder the way it reaches a patch houseCARL wrote.
        // The foreign folder gets none, which is what makes it foreign.
        File.WriteAllText(Path.Combine(patchFolder, "meta.ini"), HousecarlOwnerMeta.Section + "\r\ngenerated=true\r\n");

        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(_root, "game").Replace(@"\", @"\\") + ")\r\n");
        var prof = Path.Combine(instance, "profiles", "Default");
        Directory.CreateDirectory(prof);
        File.WriteAllText(Path.Combine(prof, "loadorder.txt"), "# header\r\n" + MasterName + "\r\n" + PatchAName + "\r\n" + ForeignName + "\r\n");
        File.WriteAllText(Path.Combine(prof, "plugins.txt"), "*" + MasterName + "\r\n*" + PatchAName + "\r\n*" + ForeignName + "\r\n");
        File.WriteAllText(Path.Combine(prof, "modlist.txt"), "# header\r\n+ForkForeignMod\r\n+houseCARL - HcForkA\r\n+ForkMasterMod\r\n");

        _svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(_root, "user.json")));
    }

    static string Fid(FormKey fk) => $"{fk.ID:X6}:{fk.ModKey.FileName}";
    static JsonElement Je(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>One Add of a weapon into a leveled list, landing wherever the lane says.</summary>
    string AddEntry(FormKey list, FormKey weapon, string? patch = null, string? into = null, string? format = null) =>
        ApplyTools.Apply(_svc,
            ops: Je($@"[{{""formid"":""{Fid(list)}"",""field_path"":""Entries"",""op"":""Add"",""compose"":{{""type"":""LeveledItemEntry"",""sets"":[{{""path"":""Data.Level"",""value"":""3""}},{{""path"":""Data.Count"",""value"":""1""}},{{""path"":""Data.Reference"",""value"":""{Fid(weapon)}""}}]}}}}]"),
            patch: patch, into: into, format: format);

    [Fact]
    public void WritingIntoASecondPatchNamesThePatchThatAlreadyOverridesTheRecord()
    {
        var r = AddEntry(_list, _swordB, patch: "HcForkB");
        Assert.DoesNotContain("error:", r);
        Assert.Contains("warning:", r);
        Assert.Contains(PatchAName, r);
        Assert.Contains("only the last-loaded copy", r);
        Assert.Contains($"into=\"{PatchAName}\"", r);
    }

    [Fact]
    public void TheWarningIsItsOwnKeyInTheJsonRender()
    {
        var doc = JsonDocument.Parse(AddEntry(_list, _swordB, patch: "HcForkBJson", format: "json"));
        Assert.Contains(PatchAName, doc.RootElement.GetProperty("warning").GetString());
    }

    [Fact]
    public void WritingIntoThePatchThatAlreadyOverridesTheRecordWarnsAboutNothing()
    {
        var r = AddEntry(_list, _swordB, into: PatchAName);
        Assert.DoesNotContain("error:", r);
        Assert.DoesNotContain("warning:", r);
    }

    [Fact]
    public void AFreshPatchOverARecordNobodyElseOverridesWarnsAboutNothing()
    {
        // The weapon is defined by the master and overridden by no one, so overriding it forks nothing.
        var r = ApplyTools.Apply(_svc,
            ops: Je($@"[{{""formid"":""{Fid(_swordA)}"",""field_path"":""BasicStats.Damage"",""op"":""Set"",""value"":""12""}}]"),
            patch: "HcForkLone");
        Assert.DoesNotContain("error:", r);
        Assert.DoesNotContain("warning:", r);
    }

    /// <summary>A fresh patch sorts to the top and has just copied the winner's body in, so a third-party mod
    /// overriding the record is not a fork hazard — its content came along. Only a sibling houseCARL patch, which may
    /// not be enabled yet and so may not be the winner, is.</summary>
    [Fact]
    public void AFreshPatchOverARecordOnlyAForeignPluginOverridesWarnsAboutNothing()
    {
        var r = AddEntry(_foreignList, _swordA, patch: "HcForkForeignFresh");
        Assert.DoesNotContain("error:", r);
        Assert.DoesNotContain("warning:", r);
    }

    /// <summary>The other arm: a patch that HAS a position is out-loaded by anything below it, whoever wrote it, so
    /// the foreign plugin is named there — and the remedy names the lane that can edit a plugin houseCARL did not
    /// make.</summary>
    [Fact]
    public void ExtendingAPatchNamesAnyPluginBelowItWhoeverWroteIt()
    {
        var r = AddEntry(_foreignList, _swordA, into: PatchAName);
        Assert.DoesNotContain("error:", r);
        Assert.Contains("warning:", r);
        Assert.Contains(ForeignName, r);
        Assert.Contains($"in_place=\"{ForeignName}\"", r);
    }

    /// <summary>in_place= is the lane the positioned arm's remedy was written for, and the one the caller cannot
    /// simply re-target, so it owes the warning too.</summary>
    [Fact]
    public void AnInPlaceEditIsWarnedAboutThePluginsBelowIt()
    {
        var r = ApplyTools.Apply(_svc,
            ops: Je($@"[{{""formid"":""{Fid(_foreignList)}"",""field_path"":""ChanceNone"",""op"":""Set"",""value"":""0.05""}}]"),
            in_place: MasterName, acknowledge: true);
        Assert.DoesNotContain("error:", r);
        Assert.Contains("warning:", r);
        Assert.Contains(ForeignName, r);
        Assert.Contains($"in_place=\"{ForeignName}\"", r);
    }

    /// <summary>Two records forked by two DIFFERENT plugins have no single into= that fixes both — following one
    /// would move both edits into a patch that still forks the other — so the remedy says to route them per record
    /// instead of naming the globally last-loaded forker.</summary>
    [Fact]
    public void RecordsForkedByDifferentPluginsGetNoSingleIntoRemedy()
    {
        var r = ApplyTools.Apply(_svc,
            ops: Je($@"[{{""formid"":""{Fid(_list)}"",""field_path"":""ChanceNone"",""op"":""Set"",""value"":""0.05""}},{{""formid"":""{Fid(_foreignList)}"",""field_path"":""ChanceNone"",""op"":""Set"",""value"":""0.05""}}]"),
            in_place: MasterName, acknowledge: true);
        Assert.DoesNotContain("error:", r);
        Assert.Contains("warning:", r);
        Assert.Contains(PatchAName, r);
        Assert.Contains(ForeignName, r);
        Assert.Contains("do not all answer to the same plugin", r);
        Assert.DoesNotContain("pass into=", r);
        // The per-record branch must keep the lane split the single-plugin branch has, or it sends the caller to
        // into= a plugin houseCARL did not write, which the extend gate refuses.
        Assert.Contains("into= if it is a houseCARL patch, else in_place=", r);
    }

    /// <summary>ONE record forked by TWO plugins has a single answer — the last-loaded of them, whose copy applies —
    /// so it keeps the singular subject and the single-plugin remedy rather than falling to the per-record wording,
    /// which would be false on its face about one record.</summary>
    [Fact]
    public void OneRecordForkedByTwoPluginsIsPointedAtTheLastLoadedOne()
    {
        var r = ApplyTools.Apply(_svc,
            ops: Je($@"[{{""formid"":""{Fid(_bothList)}"",""field_path"":""ChanceNone"",""op"":""Set"",""value"":""0.05""}}]"),
            in_place: MasterName, acknowledge: true);
        Assert.DoesNotContain("error:", r);
        Assert.Contains("this record is", r);
        Assert.DoesNotContain("do not all answer", r);
        Assert.Contains($"in_place=\"{ForeignName}\"", r);      // the LAST-loaded of the two, not HcForkA
    }

    /// <summary>An in-place edit of a record the target already carries makes no second copy, so the sentence may not
    /// call it a fork — the hazard there is that the copy is out-loaded.</summary>
    [Fact]
    public void AnInPlaceEditIsNotDescribedAsForkingTheRecord()
    {
        var r = ApplyTools.Apply(_svc,
            ops: Je($@"[{{""formid"":""{Fid(_foreignList)}"",""field_path"":""ChanceNone"",""op"":""Set"",""value"":""0.05""}}]"),
            in_place: MasterName, acknowledge: true);
        Assert.Contains("warning:", r);
        Assert.Contains("out-loaded", r);
        Assert.DoesNotContain("this write forks", r);
    }

    /// <summary>Overriding a nested record drags its container in as an override too, so a container another patch
    /// already overrides is forked by this write and must be named — the case that bites hardest, because nothing in
    /// the caller's own op list mentions the container at all.</summary>
    [Fact]
    public void ForwardingANestedRecordWarnsAboutItsContainer()
    {
        var r = ForwardTools.Forward(_svc, formids: new[] { Fid(_info) }, source: MasterName, patch: "HcForkNested");
        Assert.DoesNotContain("error:", r);
        Assert.Contains("warning:", r);
        Assert.Contains(PatchAName, r);      // A overrides the INFO's TOPIC, not the INFO
    }

    /// <summary>A dry run predicts the same fork, or the check only fires once the caller is already committed.</summary>
    [Fact]
    public void ADryRunSaysTheWriteWouldFork()
    {
        var r = ApplyTools.Apply(_svc,
            ops: Je($@"[{{""formid"":""{Fid(_list)}"",""field_path"":""Entries"",""op"":""Add"",""compose"":{{""type"":""LeveledItemEntry"",""sets"":[{{""path"":""Data.Level"",""value"":""4""}},{{""path"":""Data.Count"",""value"":""1""}},{{""path"":""Data.Reference"",""value"":""{Fid(_swordB)}""}}]}}}}]"),
            patch: "HcForkDry", dry_run: true);
        Assert.Contains("DRY RUN", r);
        Assert.Contains("warning:", r);
        Assert.Contains(PatchAName, r);
    }

    /// <summary>Forwarding lands an override in a patch exactly as an edit does, so it asks the same question.</summary>
    [Fact]
    public void AForwardIntoAFreshPatchWarnsAboutTheOtherOverride()
    {
        var r = ForwardTools.Forward(_svc, formids: new[] { Fid(_list) }, source: MasterName, patch: "HcForkFwd");
        Assert.DoesNotContain("error:", r);
        Assert.Contains("warning:", r);
        Assert.Contains(PatchAName, r);
    }

    public void Dispose()
    {
        _svc.Dispose();
        try { Directory.Delete(_root, true); } catch { /* temp cleanup best-effort */ }
    }
}
