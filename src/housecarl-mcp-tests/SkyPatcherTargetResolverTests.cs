using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>One target resolver (#1321): a direct-list EditorID names the one record the game's EditorID map holds, the
/// first loaded under the name; a target that names nothing says why on its line; EditorIDs are walked once per call.</summary>
[Trait("tier", "integration")]
public sealed class SkyPatcherTargetResolverTests
{
    const string Ini = "HcTargets.ini";
    static readonly ModKey Other = ModKey.FromFileName("HcSpRelOther.esp");

    string Post(SkyPatcherRelevanceWorld w, string fid, string field) => RecordsTools.Records(w.Svc, formids: new[] { fid },
        source: SkyPatcherRelevanceWorld.Post(), project: new RecordsTools.RecordsProject { form = "fields", fields = new[] { field } });

    // HcSpRelOther.esp, loading after HcSpRel.esp, defines a second armor named HcPlainCuirass.
    static SkyPatcherRelevanceWorld DuplicateWorld(string ini) => new(new[] { (Ini, ini) }, otherPlugin: true,
        otherRecords: o => o.Armors.Add(new Armor(new FormKey(o.ModKey, 0x810), SkyrimRelease.SkyrimSE) { EditorID = "HcPlainCuirass" }));

    [Fact]
    public void ADuplicatedEditorIdPatchesOnlyTheFirstLoadedRecord()
    {
        using var w = DuplicateWorld("filterByArmors=HcPlainCuirass:value=42\r\n");
        Assert.Contains("Value = 42", Post(w, w.Plain, "Value"));
        Assert.Contains("Value = 0", Post(w, "000810:HcSpRelOther.esp", "Value"));
    }

    [Fact]
    public void ADuplicatedEditorIdSaysOnItsLineWhichRecordTheGameUses()
    {
        using var w = DuplicateWorld("filterByArmors=HcPlainCuirass:value=42\r\n");
        Assert.Contains("target 'HcPlainCuirass' (in a filterByArmors) names 2 Armor records; the replay takes the first loaded, 000811:HcSpRel.esp from HcSpRel.esp.",
            SkyPatcherTools.SkyPatcherLayer(w.Svc, filter: Ini));
    }

    // HcSpRelOther.esp overrides HcHeelBoots and renames it: the game's map keeps the name the master loaded first.
    [Fact]
    public void AnEditorIdARenamingOverrideDropsStillNamesTheRecord()
    {
        using var w = new SkyPatcherRelevanceWorld(new[] { (Ini, "filterByArmors=HcHeelBoots:value=43\r\n") }, otherPlugin: true,
            otherRecords: o => o.Armors.Add(new Armor(new FormKey(ModKey.FromFileName("HcSpRel.esp"), 0x810), SkyrimRelease.SkyrimSE) { EditorID = "HcRenamedBoots" }));
        Assert.Contains("Value = 43", Post(w, w.Heel, "Value"));
    }

    [Fact]
    public void ATargetThatNamesNothingIsReportedOnItsLine()
    {
        using var w = new SkyPatcherRelevanceWorld(new[] { (Ini,
            "filterByArmors=HcNoSuchArmor:value=5\r\n"
            + "filterByArmors=HcSpRel.esp|zz:value=6\r\n"
            + "filterByArmors=HcGone.esp|800:value=7\r\n"
            + "filterByArmors=HcSpRel.esp|899:value=8\r\n") });
        var layer = SkyPatcherTools.SkyPatcherLayer(w.Svc, filter: Ini);
        Assert.Contains("target 'HcNoSuchArmor' (in a filterByArmors) finds no record by that EditorID among Armor records in the active order.", layer);
        Assert.Contains("target 'HcSpRel.esp|zz' (in a filterByArmors) is not a FormID SkyPatcher can read", layer);
        Assert.Contains("target 'HcGone.esp|800' (in a filterByArmors) names plugin 'HcGone.esp', which is not in the active order, so it names nothing.", layer);
        Assert.Contains("target 'HcSpRel.esp|899' (in a filterByArmors) names 000899:HcSpRel.esp, which no plugin in the active order defines, so it names nothing.", layer);
        Assert.Contains("no-op scan: 1 explicit EditorID target(s) did not resolve", layer);   // the malformed address is no EditorID
    }

    // HcSpRel.esp's keyword HcKw2 loads the name before HcSpRelOther.esp's armor of the same name, and the keyword line
    // puts keywords in the call's walk: the armor lookup still finds the armor, whatever other lines the layer holds.
    [Fact]
    public void ANameAnotherTypeAlsoHoldsStillNamesTheScopedRecord()
    {
        using var w = new SkyPatcherRelevanceWorld(new[] { (Ini, "filterByArmors=HcKw2:value=44\r\nfilterByKeywords=HcKw2:weight=3\r\n") }, otherPlugin: true,
            otherRecords: o => o.Armors.Add(new Armor(new FormKey(o.ModKey, 0x810), SkyrimRelease.SkyrimSE) { EditorID = "HcKw2" }));
        Assert.Contains("Value = 44", Post(w, "000810:HcSpRelOther.esp", "Value"));
    }

    [Fact]
    public void A0xFormIdTargetIsNotReportedAsAMiss()
    {
        using var w = new SkyPatcherRelevanceWorld(new[] { (Ini, "filterByArmors=HcSpRel.esp|0x000810:value=9\r\n") });
        Assert.DoesNotContain("target '", SkyPatcherTools.SkyPatcherLayer(w.Svc, filter: Ini));
        Assert.Contains("Value = 9", Post(w, w.Heel, "Value"));
    }

    // Names in a direct list, a keyword filter and a keyword op: one walk covers all three scopes, and the next call walks again.
    [Fact]
    public void EditorIdsAreWalkedOncePerCall()
    {
        using var w = new SkyPatcherRelevanceWorld(new[] { (Ini,
            "filterByArmors=HcPlainCuirass:value=3\r\n"
            + "filterByKeywords=HcHeelKw:keywordsToAdd=HcAddKw\r\n") });
        var before = w.Svc.Counters.EditorIdSweeps;
        SkyPatcherTools.SkyPatcherLayer(w.Svc, filter: Ini);
        Assert.Equal(1, w.Svc.Counters.EditorIdSweeps - before);
        before = w.Svc.Counters.EditorIdSweeps;
        RecordsTools.Records(w.Svc, formids: new[] { w.Plain, w.Heel, w.Both }, source: SkyPatcherRelevanceWorld.Post(),
            project: new RecordsTools.RecordsProject { form = "fields", fields = new[] { "Value", "Keywords" } });
        Assert.Equal(1, w.Svc.Counters.EditorIdSweeps - before);
        // a comparison of two replayed poles opens two replay contexts; they share the call's one table
        before = w.Svc.Counters.EditorIdSweeps;
        RecordsTools.Records(w.Svc, formids: new[] { w.Plain }, source: SkyPatcherRelevanceWorld.Post(), versus: SkyPatcherRelevanceWorld.Post(),
            project: new RecordsTools.RecordsProject { form = "delta", fields = new[] { "Value" } });
        Assert.Equal(1, w.Svc.Counters.EditorIdSweeps - before);
    }

    // A later walk of another type that finds an earlier-loaded holder of a name puts it first: HcDupName is an armor in
    // HcSpRel.esp and a keyword in HcSpRelOther.esp, and the keyword is walked first.
    [Fact]
    public void ALaterWalkGivesANameToTheRecordLoadedFirst()
    {
        using var w = new SkyPatcherRelevanceWorld(Array.Empty<(string, string)>(), otherPlugin: true,
            records: m => m.Armors.Add(new Armor(new FormKey(m.ModKey, 0x830), SkyrimRelease.SkyrimSE) { EditorID = "HcDupName" }),
            otherRecords: o => o.Keywords.Add(new Keyword(new FormKey(o.ModKey, 0x830), SkyrimRelease.SkyrimSE) { EditorID = "HcDupName" }));
        var table = SkyPatcherEditorIdTable.For(w.Svc.CaptureView(), null);
        table.Cover(new[] { typeof(IKeywordGetter) });
        table.Cover(new[] { typeof(IArmorGetter) });
        var holders = table.Find("HcDupName", null, out _);
        Assert.Equal(new FormKey(ModKey.FromFileName("HcSpRel.esp"), 0x830), holders[0].Record);
        Assert.Equal(2, holders.Count);
    }

    // Holders sort by load position alone: the keyword HcKw2 in HcSpRel.esp comes before HcSpRelOther.esp's armor, whatever the type names.
    [Fact]
    public void HoldersOfOneNameSortByLoadPositionNotType()
    {
        using var w = new SkyPatcherRelevanceWorld(Array.Empty<(string, string)>(), otherPlugin: true,
            otherRecords: o => o.Armors.Add(new Armor(new FormKey(o.ModKey, 0x830), SkyrimRelease.SkyrimSE) { EditorID = "HcKw2" }));
        var table = SkyPatcherEditorIdTable.For(w.Svc.CaptureView(), null);
        table.Cover(new[] { typeof(IKeywordGetter), typeof(IArmorGetter) });
        Assert.Equal(new FormKey(ModKey.FromFileName("HcSpRel.esp"), 0x802), table.Find("HcKw2", null, out _)[0].Record);
    }

    // One call holds one table per view: a second order's view inside the same call gets a table of its own.
    [Fact]
    public void ACallGivesEachViewItsOwnTable()
    {
        using var a = new SkyPatcherRelevanceWorld(Array.Empty<(string, string)>());
        using var b = new SkyPatcherRelevanceWorld(Array.Empty<(string, string)>());
        var (va, vb) = (a.Svc.CaptureView(), b.Svc.CaptureView());   // taken outside the call, which pins one view
        using var call = CallContext.Begin();
        var first = SkyPatcherEditorIdTable.For(va, null);
        Assert.Same(first, SkyPatcherEditorIdTable.For(va, null));
        Assert.NotSame(first, SkyPatcherEditorIdTable.For(vb, null));
    }

    // A name starting with a digit is not in the call's first walk; its own lookup walks its scope.
    [Fact]
    public void ADigitFirstEditorIdTargetResolves()
    {
        using var w = new SkyPatcherRelevanceWorld(new[] { (Ini, "filterByArmors=1HcDigit:value=5\r\n") },
            records: m => m.Armors.Add(new Armor(new FormKey(m.ModKey, 0x830), SkyrimRelease.SkyrimSE) { EditorID = "1HcDigit" }));
        Assert.Contains("Value = 5", Post(w, "000830:HcSpRel.esp", "Value"));
    }

    // The Excluded spelling resolves its EditorID like the direct list: only the first loaded HcPlainCuirass is left out.
    [Fact]
    public void AnExcludedEditorIdLeavesOutOnlyTheFirstLoadedRecord()
    {
        using var w = DuplicateWorld("filterByArmorsExcluded=HcPlainCuirass:value=7\r\n");
        Assert.Contains("Value = 0", Post(w, w.Plain, "Value"));
        Assert.Contains("Value = 7", Post(w, "000810:HcSpRelOther.esp", "Value"));
    }

    // The conflict detector resolves an EditorID target, so a record named by EditorID in one INI and by FormID in another is one target.
    [Fact]
    public void ARecordNamedByEditorIdAndByFormIdConflictsWithItself()
    {
        using var w = new SkyPatcherRelevanceWorld(new[] { ("HcA.ini", "filterByArmors=HcPlainCuirass:value=3\r\n"),
            ("HcB.ini", "filterByArmors=HcSpRel.esp|811:value=4\r\n") });
        Assert.Contains("1 set-conflict(s)", SkyPatcherTools.SkyPatcherLayer(w.Svc));
    }

    // addToLLs carries no form type: its item is looked up among every item type, as SkyPatcher's untyped lookup finds it.
    [Fact]
    public void AnUntypedOpFindsItsEditorId()
    {
        using var w = new SkyPatcherRelevanceWorld(new[] { (Ini, "filterByLLs=HcSpRel.esp|820:addToLLs=HcPlainCuirass~1~1\r\n") },
            subfolder: "leveledList",
            records: m => m.LeveledItems.Add(new LeveledItem(new FormKey(m.ModKey, 0x820), SkyrimRelease.SkyrimSE) { EditorID = "HcLvl" }));
        Assert.Contains("Entries = [list: 1 item(s)]", Post(w, "000820:HcSpRel.esp", "Entries"));
    }

    // HcList holds HcPlainCuirass.
    static void AddList(SkyrimMod m) => m.FormLists.Add(new FormList(new FormKey(m.ModKey, 0x840), SkyrimRelease.SkyrimSE)
    {
        EditorID = "HcList",
        Items = new() { new FormLink<ISkyrimMajorRecordGetter>(new FormKey(m.ModKey, 0x811)) },
    });

    // formsToReplace has no lookup scope, so its EditorIDs miss whatever other lines the layer walks, as on main.
    [Fact]
    public void AnUnscopedOpEditorIdMissesBesideAScopedOne()
    {
        using var w = new SkyPatcherRelevanceWorld(new[] { (Ini,
            "filterByFormLists=HcSpRel.esp|840:formsToAdd=HcHeelBoots:formsToReplace=HcPlainCuirass=HcBothBoots\r\n") },
            subfolder: "formList", records: AddList);
        Assert.Contains("formList\\HcTargets.ini:1: 'formsToReplace=HcPlainCuirass=HcBothBoots' — form(s) not resolvable; skipped.",
            Post(w, "000840:HcSpRel.esp", "Items"));
    }

    // A form-list item EditorID is looked up among every type: a call that looks none up walks only its own scopes.
    [Fact]
    public void ACallThatLooksUpNoFormListItemWalksOnlyItsScopes()
    {
        using var w = new SkyPatcherRelevanceWorld(new[] { (Ini, "filterByArmors=HcPlainCuirass:value=3\r\n"),
            ("../formList/HcList.ini", "filterByFormLists=HcSpRel.esp|840:formsToAdd=HcHeelBoots\r\n") }, records: AddList);
        var before = w.Svc.Counters.EditorIdTypesWalked;
        Assert.Contains("Value = 3", Post(w, w.Plain, "Value"));
        Assert.Equal(1, w.Svc.Counters.EditorIdTypesWalked - before);
        Assert.Contains("Items = [list: 2 item(s)]", Post(w, "000840:HcSpRel.esp", "Items"));
    }

    // Without Tweaks a form-list item EditorID resolves for a type the game keeps names of, and a miss names those types.
    [Fact]
    public void WithoutTweaksAFormListItemEditorIdOfAKeptTypeResolves()
    {
        using var w = new SkyPatcherRelevanceWorld(new[] { (Ini,
            "filterByFormLists=HcSpRel.esp|840:formsToAdd=HcHeelKw\r\nfilterByFormLists=HcSpRel.esp|840:formsToAdd=HcHeelBoots\r\n") },
            subfolder: "formList", tweaks: false, records: AddList);
        var read = Post(w, "000840:HcSpRel.esp", "Items");
        Assert.Contains("Items = [list: 2 item(s)]", read);
        Assert.Contains("formList\\HcTargets.ini:2: 'formsToAdd=HcHeelBoots' — EditorID 'HcHeelBoots' finds nothing: powerofthree's Tweaks (SKSE\\Plugins\\po3_Tweaks.dll) is not in this order, so of SkyrimMajorRecord records the game keeps EditorIDs only of ", read);
        Assert.DoesNotContain("HcHeelKw' finds nothing", read);
    }
}
