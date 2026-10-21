using System.Text.Json;
using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>#1206: a plugin excluded for a record Mutagen cannot parse still touches records. Its overrides are read
/// marked, apply refuses them, and a reference into its own records is classified as into it, not as undefined.</summary>
[Trait("tier", "integration")]
public sealed class ExcludedPluginTouchTests : IDisposable
{
    const string BadName = "HcExBad.esp";
    const string CleanName = "HcExClean.esp";
    const string LateName = "HcExLate.esp";
    const string BrokenName = "HcExBroken.esp";   // the bad plugin cut short, so its record headers cannot be walked either
    const string GarbageName = "HcExGarbage.esp"; // not a plugin at all, so it cannot be opened

    readonly string _root = Path.Combine(Path.GetTempPath(), "hc-excluded-touch-" + Guid.NewGuid().ToString("N"));
    readonly LoadOrderService _svc;
    readonly FormKey _overridden;   // the master's weapon the bad plugin overrides
    readonly FormKey _overriddenLater;   // a master weapon the bad plugin overrides and a later plugin overrides again
    readonly FormKey _untouched;    // the master's keyword nothing else touches
    readonly FormKey _badOwn;       // a keyword the bad plugin defines
    readonly FormKey _cell;         // the master's interior cell the bad plugin overrides
    readonly FormKey _badRef;       // a placed object in that cell the bad plugin overrides
    readonly FormKey _cleanRef;     // a placed object in that cell the bad plugin does not touch
    readonly FormKey _cleanWeapon;  // the clean plugin's weapon, keyworded with the bad plugin's own keyword
    readonly FormKey _injected;     // a keyword the bad plugin injects under the master's FormID space
    readonly FormKey _missing;      // a master FormID no plugin defines
    readonly FormKey _lateWeapon;   // the late plugin's weapon, keyworded with the injected and the missing keyword

    public ExcludedPluginTouchTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "game", "Data"));
        var master = new SkyrimMod(new ModKey("HcExMaster", ModType.Master), SkyrimRelease.SkyrimSE);
        var weapon = master.Weapons.AddNew(); weapon.EditorID = "HcExWeapon";
        weapon.BasicStats = new WeaponBasicStats { Damage = 10, Weight = 1 };
        var weapon2 = master.Weapons.AddNew(); weapon2.EditorID = "HcExWeaponLate";
        weapon2.BasicStats = new WeaponBasicStats { Damage = 10, Weight = 1 };
        var kw = master.Keywords.AddNew(); kw.EditorID = "HcExKeyword";
        _overridden = weapon.FormKey; _overriddenLater = weapon2.FormKey; _untouched = kw.FormKey;
        var cell = new Cell(master.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcExCell" };
        var refA = new PlacedObject(master.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcExRefBad" };
        var refB = new PlacedObject(master.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcExRefClean" };
        refA.Base.SetTo(weapon.FormKey); refB.Base.SetTo(weapon.FormKey);
        cell.Temporary.Add(refA); cell.Temporary.Add(refB);
        var block = new CellBlock { BlockNumber = 1, GroupType = GroupTypeEnum.InteriorCellBlock };
        var sub = new CellSubBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellSubBlock };
        sub.Cells.Add(cell); block.SubBlocks.Add(sub); master.Cells.Records.Add(block);
        _cell = cell.FormKey; _badRef = refA.FormKey; _cleanRef = refB.FormKey;
        master.Keywords.Add(new Keyword(new FormKey(master.ModKey, 0x1A00), SkyrimRelease.SkyrimSE) { EditorID = "HcExMasterFarKeyword" });   // so a compaction of the master renumbers

        var bad = new SkyrimMod(ModKey.FromNameAndExtension(BadName), SkyrimRelease.SkyrimSE);
        ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(bad, weapon)).BasicStats = new WeaponBasicStats { Damage = 99, Weight = 1 };
        ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(bad, weapon2)).BasicStats = new WeaponBasicStats { Damage = 99, Weight = 1 };
        WriteEngine.GenericGetOrAddAsOverride(bad, refA, master.ToImmutableLinkCache());   // carries the cell override with it
        var badKw = bad.Keywords.AddNew(); badKw.EditorID = "HcExBadKeyword";
        _badOwn = badKw.FormKey;
        bad.Packages.AddNew().EditorID = "HcExBadPackage";
        _injected = new FormKey(master.ModKey, 0xA00);
        bad.Keywords.Add(new Keyword(_injected, SkyrimRelease.SkyrimSE) { EditorID = "HcExInjectedKeyword" });
        _missing = new FormKey(master.ModKey, 0xA01);

        var clean = new SkyrimMod(ModKey.FromNameAndExtension(CleanName), SkyrimRelease.SkyrimSE);
        var cw = clean.Weapons.AddNew(); cw.EditorID = "HcExCleanWeapon";
        _cleanWeapon = cw.FormKey;
        cw.Keywords = new Noggog.ExtendedList<IFormLinkGetter<IKeywordGetter>> { badKw.ToLink() };
        clean.Keywords.Add(new Keyword(new FormKey(clean.ModKey, 0x1A00), SkyrimRelease.SkyrimSE) { EditorID = "HcExFarKeyword" });   // past the light range, so a compaction renumbers

        var late = new SkyrimMod(ModKey.FromNameAndExtension(LateName), SkyrimRelease.SkyrimSE);
        ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(late, weapon2)).BasicStats = new WeaponBasicStats { Damage = 50, Weight = 1 };
        var lw = late.Weapons.AddNew(); lw.EditorID = "HcExLateWeapon";
        _lateWeapon = lw.FormKey;
        lw.Keywords = new Noggog.ExtendedList<IFormLinkGetter<IKeywordGetter>> { _injected.ToLink<IKeywordGetter>(), _missing.ToLink<IKeywordGetter>() };

        var mods = Path.Combine(_root, "inst", "mods");
        string Place(string mod, SkyrimMod m, params ISkyrimModGetter[] order)
        {
            Directory.CreateDirectory(Path.Combine(mods, mod));
            var path = Path.Combine(mods, mod, m.ModKey.FileName);
            m.BeginWrite.ToPath(path).WithLoadOrder(order).Write();
            return path;
        }
        Place("MasterMod", master);
        var badPath = Place("BadMod", bad, master);
        OverstatePkcuLength(badPath);
        Directory.CreateDirectory(Path.Combine(mods, "BrokenMod"));
        File.WriteAllBytes(Path.Combine(mods, "BrokenMod", BrokenName), OverstateNestedGroup(File.ReadAllBytes(badPath)));
        Place("CleanMod", clean, master, bad);
        Place("LateMod", late, master);
        Directory.CreateDirectory(Path.Combine(mods, "GarbageMod"));
        File.WriteAllBytes(Path.Combine(mods, "GarbageMod", GarbageName), new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

        var inst = Path.Combine(_root, "inst");
        File.WriteAllText(Path.Combine(inst, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(_root, "game").Replace(@"\", @"\\") + ")\r\n");
        var prof = Path.Combine(inst, "profiles", "Default");
        Directory.CreateDirectory(prof);
        var names = new[] { master.ModKey.FileName.String, BadName, BrokenName, CleanName, LateName, GarbageName };
        File.WriteAllText(Path.Combine(prof, "loadorder.txt"), "# header\r\n" + string.Join("\r\n", names) + "\r\n");
        File.WriteAllText(Path.Combine(prof, "plugins.txt"), string.Join("", names.Select(n => "*" + n + "\r\n")));
        File.WriteAllText(Path.Combine(prof, "modlist.txt"), "# header\r\n+GarbageMod\r\n+LateMod\r\n+CleanMod\r\n+BrokenMod\r\n+BadMod\r\n+MasterMod\r\n");
        _svc = LoadOrderService.WithInstance(inst, 0, new UserConfigStore(Path.Combine(_root, "user.json")));
    }

    public void Dispose()
    {
        _svc.Dispose();
        try { Directory.Delete(_root, true); } catch { /* temp cleanup best-effort */ }
    }

    /// <summary>Overstate the first PKCU subrecord's length so Mutagen throws mid-enumeration; the record headers stay intact.</summary>
    static void OverstatePkcuLength(string path)
    {
        var b = File.ReadAllBytes(path);
        for (int i = 0; i < b.Length - 6; i++)
        {
            if (b[i] != (byte)'P' || b[i + 1] != (byte)'K' || b[i + 2] != (byte)'C' || b[i + 3] != (byte)'U') continue;
            BitConverter.GetBytes((ushort)99).CopyTo(b, i + 4);
            File.WriteAllBytes(path, b);
            return;
        }
        Assert.Fail("Mutagen wrote no PKCU subrecord for the package");
    }

    /// <summary>Overstate the cell's temporary-children GRUP (type 9) so a header walk runs off the end; the top-level groups stay intact, so the file still opens.</summary>
    static byte[] OverstateNestedGroup(byte[] b)
    {
        for (int i = 0; i < b.Length - 16; i++)
        {
            if (b[i] != (byte)'G' || b[i + 1] != (byte)'R' || b[i + 2] != (byte)'U' || b[i + 3] != (byte)'P' || BitConverter.ToInt32(b, i + 12) != 9) continue;
            BitConverter.GetBytes(0x7FFFFFFF).CopyTo(b, i + 4);
            return b;
        }
        Assert.Fail("Mutagen wrote no temporary-children group for the cell");
        return b;
    }

    static string Fid(FormKey fk) => FormIdToken.Of(fk);

    const string Note = "also touched by " + BadName + ", excluded this session as unreadable, which loads after this record's winner and wins it in game — its version is not in this answer.";

    string Status(string? filter) =>
        StatusTools.LoadOrderStatus(_svc, new ToolPathResolver(new UserConfigStore(Path.Combine(_root, "user.json"))), filter: filter);

    [Fact]
    public void TheBadPluginIsExcludedForAParseFailure()
    {
        var status = Status(BadName);
        Assert.Contains("EXCLUDED this session", status);
        Assert.Contains("a record it overrides after that record's winner is read without its version, marked on summary, fields, tree, delta, dense and to_file reads, " +
                        "and default-lane apply and forward on either lane refuse to write it", status);
        Assert.Contains("houseCARL does NOT read this plugin, so any record it overrides is answered without its version.", status);
        Assert.DoesNotContain("every other plugin is unaffected", status);

        var all = Status(null);
        Assert.Contains("houseCARL does NOT read these, so any record they override is answered without their version:", all);
        Assert.DoesNotContain("every OTHER plugin works", all);
    }

    [Fact]
    public void APluginWhoseHeadersCannotBeWalkedSaysItsTouchesAreNotMarked()
    {
        var status = Status(BrokenName);
        Assert.Contains("EXCLUDED this session", status);
        Assert.Contains("the records it overrides could not be listed either (", status);
        Assert.Contains("so reads of them are not marked and writes to them are not refused", status);
    }

    [Fact]
    public void APluginThatCannotBeOpenedSaysItsTouchesAreNeitherMarkedNorRefused()
    {
        var status = Status(GarbageName);
        Assert.Contains("EXCLUDED this session", status);
        Assert.Contains("the records it overrides cannot be listed, so reads of them are not marked and writes to them are not refused", status);
    }

    [Fact]
    public void AScanRowOfATouchedRecordCarriesTheNote()
    {
        var scan = RecordsTools.Records(_svc, types: new[] { "WEAP" });
        Assert.Contains("    note: " + Note, scan);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(scan, "note: also touched by"));   // only the weapon the bad plugin wins
    }

    public static TheoryData<string, string?> MarkedForms => new()
    {
        { "tree", null }, { "fields", null }, { "summary", null }, { "delta", null },
        { "tree", "json" }, { "fields", "json" }, { "summary", "json" }, { "delta", "json" },
    };

    [Theory, MemberData(nameof(MarkedForms))]
    public void AReadOfARecordTheExcludedPluginOverridesIsMarked(string form, string? format)
    {
        RecordsTools.RecordsProject Project() => new() { form = form, fields = form is "fields" or "delta" ? new[] { "BasicStats.Damage" } : null };
        JsonElement? versus = form == "delta"
            ? JsonDocument.Parse(JsonSerializer.Serialize(new { file = Path.Combine(_root, "inst", "mods", "BadMod", BadName) })).RootElement.Clone() : null;
        var read = RecordsTools.Records(_svc, formids: new[] { Fid(_overridden) }, project: Project(), versus: versus, format: format);
        if (format == "json") Assert.Contains("\"excluded_note\": \"" + Note.Replace("'", "\\u0027") + "\"", read);
        else Assert.Contains("note: " + Note, read);

        var control = RecordsTools.Records(_svc, formids: new[] { Fid(_untouched) }, project: new() { form = "tree" });
        Assert.Contains("HcExKeyword", control);
        Assert.DoesNotContain("also touched by", control);
    }

    [Fact]
    public void ARecordInACellTheExcludedPluginOverridesIsMarked()
    {
        var tree = RecordsTools.Records(_svc, formids: new[] { Fid(_badRef), Fid(_cell) }, project: new() { form = "tree" });
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(tree, "note: " + System.Text.RegularExpressions.Regex.Escape(Note)).Count);
    }

    [Fact]
    public void ADefaultLaneWriteToARecordTheExcludedPluginOverridesIsRefused()
    {
        var refused = Apply(_overridden, "BasicStats.Damage", "12");
        Assert.Contains(Fid(_overridden) + ": also touched by " + BadName + ", excluded this session as unreadable, which loads after this record's winner " +
                        "and wins it in game, so this write could undo that plugin's change.", refused);

        var control = Apply(_untouched, "EditorID", "HcExKeywordRenamed");
        Assert.False(control.StartsWith("error:", StringComparison.Ordinal), control);
        Assert.Contains("HcExKeywordRenamed", control);
        Assert.DoesNotContain("also touched by", control);
    }

    [Fact]
    public void ACopyFromSourceThatIsTheWinnerOfATouchedRecordIsRefused()
    {
        var r = ApplyTools.Apply(_svc, dry_run: true, ops: JsonDocument.Parse(
            "[{\"formid\":\"" + Fid(_cleanWeapon) + "\",\"field_path\":\"BasicStats\",\"op\":\"CopyFrom\",\"from\":\"" + Fid(_overridden) + "\"}]").RootElement);
        Assert.Contains(Fid(_overridden) + ": also touched by " + BadName, r);
    }

    [Fact]
    public void ARecordALaterPluginOverridesAgainIsNeitherMarkedNorRefused()
    {
        var tree = RecordsTools.Records(_svc, formids: new[] { Fid(_overriddenLater) }, project: new() { form = "tree" });
        Assert.Contains(LateName, tree);
        Assert.DoesNotContain("also touched by", tree);
        var applied = Apply(_overriddenLater, "BasicStats.Damage", "12");
        Assert.DoesNotContain("also touched by", applied);
        Assert.Contains("12", applied);

        // The excluded plugin loads last for the other weapon, so that one stays marked.
        Assert.Contains("also touched by " + BadName, RecordsTools.Records(_svc, formids: new[] { Fid(_overridden) }, project: new() { form = "tree" }));
    }

    [Fact]
    public void AnInPlaceApplyToATouchedRecordIsNotRefused()
    {
        var r = ApplyTools.Apply(_svc, dry_run: true, in_place: "HcExMaster.esm", acknowledge: true,
            ops: JsonDocument.Parse("[{\"formid\":\"" + Fid(_overridden) + "\",\"field_path\":\"BasicStats.Damage\",\"op\":\"Set\",\"value\":\"12\"}]").RootElement);
        Assert.DoesNotContain("also touched by", r);
        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), r);
    }

    [Fact]
    public void AForwardOfATouchedRecordIsRefusedOnBothLanes()
    {
        var fromMaster = ForwardTools.Forward(_svc, formids: new[] { Fid(_overridden) }, source: "HcExMaster.esm", dry_run: true);
        Assert.Contains("also touched by " + BadName + ", excluded this session as unreadable", fromMaster);

        var inPlace = ForwardTools.Forward(_svc, formids: new[] { Fid(_overridden) }, source: "HcExMaster.esm", in_place: CleanName, acknowledge: true, dry_run: true);
        Assert.Contains("also touched by " + BadName + ", excluded this session as unreadable", inPlace);
    }

    [Fact]
    public void AnInPlaceCompactionOfAPluginTheExcludedPluginMastersNamesItAsUnreadAndRefuses()
    {
        var r = WriteTools.CompactPlugin(_svc, source: "HcExMaster.esm", esl: true, in_place: true, acknowledge: true);
        Assert.Contains("could not fully read " + BadName, r);
        Assert.Contains("refused — this is an IN-PLACE rewrite", r);
    }

    [Fact]
    public void AnInPlaceCompactionOfAPluginTheExcludedPluginDoesNotMasterIsNotBlockedByIt()
    {
        var r = WriteTools.CompactPlugin(_svc, source: CleanName, esl: true, in_place: true, acknowledge: true);
        Assert.DoesNotContain("could not fully read", r);
        Assert.DoesNotContain("refused", r);
        Assert.Contains("compacted " + CleanName + " IN PLACE", r);
    }

    string Excluded => $"FormID {Fid(_badOwn)} is not resolvable: its plugin '{BadName}' was excluded from this session";

    [Fact]
    public void ResolveNamesSaysALinkIsIntoTheExcludedPlugin()
    {
        var text = RecordsTools.Records(_svc, formids: new[] { Fid(_cleanWeapon) },
                                        project: new() { form = "fields", fields = new[] { "Keywords" }, depth = 2, resolve_names = true });
        Assert.Contains("unresolved: " + Excluded, text);
        var json = RecordsTools.Records(_svc, formids: new[] { Fid(_cleanWeapon) },
                                        project: new() { form = "fields", fields = new[] { "Keywords" }, depth = 2, resolve_names = true }, format: "json");
        Assert.Contains(Excluded, json.Replace("\\u0027", "'"));
    }

    [Fact]
    public void ResolveNamesGivesTheUnresolvedSentenceForEveryCause()
    {
        var project = new RecordsTools.RecordsProject { form = "fields", fields = new[] { "Keywords" }, depth = 2, resolve_names = true };
        var text = RecordsTools.Records(_svc, formids: new[] { Fid(_lateWeapon) }, project: project);
        Assert.Contains("unresolved: Plugin 'HcExMaster.esm' IS in the load order, but defines no record 000A01", text);
        Assert.DoesNotContain("unresolved: no active plugin defines this target", text);
        var json = RecordsTools.Records(_svc, formids: new[] { Fid(_lateWeapon) }, project: project, format: "json");
        Assert.Contains("\"error\": \"Plugin 'HcExMaster.esm' IS in the load order, but defines no record 000A01", json.Replace("\\u0027", "'"));
    }

    [Fact]
    public void AWalkAsksTheFactForAKeyTheExcludedPluginInjects()
    {
        var text = RecordsTools.Records(_svc, formids: new[] { Fid(_lateWeapon) }, walk: new() { seed_paths = new[] { "Keywords" } },
                                        project: new() { form = "chain" });
        Assert.Contains("unresolved — Plugin 'HcExMaster.esm' IS in the load order, but defines no record 000A00", text);
        Assert.Contains("a missing endpoint", text);   // the FormID nothing defines keeps the generic sentence
    }

    [Fact]
    public void AWalkSaysAnEndpointIsInTheExcludedPlugin()
    {
        var text = RecordsTools.Records(_svc, formids: new[] { Fid(_cleanWeapon) }, walk: new() { seed_paths = new[] { "Keywords" } },
                                        project: new() { form = "chain" });
        Assert.Contains("unresolved — " + Excluded, text);
        Assert.DoesNotContain("a missing endpoint", text);
    }

    [Fact]
    public void ADenseRowCarriesTheExcludedNote()
    {
        var dense = RecordsTools.Records(_svc, formids: new[] { Fid(_overridden), Fid(_untouched) }, format: "dense",
                                         project: new() { form = "fields", fields = new[] { "EditorID" } });
        Assert.Contains("\"excluded_note\":\"also touched by " + BadName + ", excluded this session as unreadable", dense.Replace(": ", ":"));
        Assert.DoesNotContain("\"editorid_note\"", dense);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(dense, "also touched by"));   // the untouched keyword's row owes none
    }

    [Fact]
    public void TheErrorsHeadlineAndCountsOnlyTallyRefsIntoTheExcludedPluginApart()
    {
        var text = CheckTools.CheckTool(_svc, plugins: new[] { CleanName }, findings: new[] { "errors" });
        Assert.Contains("1 dangling ref(s) (1 of them into an excluded plugin houseCARL could not read)", text);
        var counts = CheckTools.CheckTool(_svc, plugins: new[] { CleanName }, findings: new[] { "errors" }, counts_only: true);
        Assert.Contains("  " + BadName + " (1 of them into an excluded plugin houseCARL could not read)", counts);
        var json = CheckTools.CheckTool(_svc, plugins: new[] { CleanName }, findings: new[] { "errors" }, counts_only: true, format: "json");
        var row = JsonDocument.Parse(json).RootElement.GetProperty("families").GetProperty("errors").GetProperty("dangling_by_target_plugin").GetProperty("rows")[0];
        Assert.Equal(BadName, row.GetProperty("key").GetString());
        Assert.Equal(1, row.GetProperty("into_excluded").GetInt32());
    }

    [Fact]
    public void ADeltaWhoseSubjectIsTheExcludedPluginsOwnFileOwesNoNote()
    {
        var badPath = Path.Combine(_root, "inst", "mods", "BadMod", BadName);
        var own = RecordsTools.Records(_svc, formids: new[] { Fid(_overridden) },
            source: JsonDocument.Parse(JsonSerializer.Serialize(new { file = badPath })).RootElement.Clone(),
            versus: JsonDocument.Parse("\"winner\"").RootElement.Clone(),
            project: new() { form = "delta", fields = new[] { "BasicStats.Damage" } });
        Assert.Contains("99", own);
        Assert.DoesNotContain("also touched by", own);
    }

    string Apply(FormKey fk, string path, string value) => ApplyTools.Apply(_svc, dry_run: true,
        ops: JsonDocument.Parse("[{\"formid\":\"" + Fid(fk) + "\",\"field_path\":\"" + path + "\",\"op\":\"Set\",\"value\":\"" + value + "\"}]").RootElement);

    [Fact]
    public void AReferenceIntoTheExcludedPluginsOwnRecordIsClassifiedAsIntoIt()
    {
        var text = CheckTools.CheckTool(_svc, plugins: new[] { CleanName }, findings: new[] { "errors" });
        Assert.Contains($"-> {_badOwn}   [target in excluded plugin {BadName}, which houseCARL could not read]", text);
        Assert.DoesNotContain("[target not defined by any active plugin]", text);

        var json = JsonDocument.Parse(CheckTools.CheckTool(_svc, plugins: new[] { CleanName }, findings: new[] { "errors" }, format: "json"));
        Assert.Contains("\"target_in_excluded\":[\"" + BadName + "\"]", json.RootElement.GetRawText().Replace(" ", "").Replace("\r", "").Replace("\n", ""));

        // A copy outside the order is swept by the off-order branch, which classifies the same way.
        var loose = Path.Combine(_root, "loose", "HcExCleanCopy.esp");
        Directory.CreateDirectory(Path.GetDirectoryName(loose)!);
        File.Copy(Path.Combine(_root, "inst", "mods", "CleanMod", CleanName), loose);
        var offOrder = CheckTools.CheckTool(_svc, plugins: new[] { loose }, findings: new[] { "errors" });
        Assert.Contains($"-> {_badOwn}   [target in excluded plugin {BadName}, which houseCARL could not read]", offOrder);
    }
}
