using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A live MO2 order with SPID files in Data: HcSpid.esp defines an NPC, a race, a perk, a spell, a keyword and
/// an outfit whose one item is an armour of HcArmorSrc.esp; HcOther.esp defines an NPC whose display name is Skald.</summary>
sealed class SpidWorld : IDisposable
{
    public const string Spid = "HcSpid.esp", ArmorSrc = "HcArmorSrc.esp", Other = "HcOther.esp";
    public readonly string Root, IniMod, Loser;
    public readonly LoadOrderService Svc;

    public SpidWorld(IEnumerable<(string Name, string Text)> files, IEnumerable<(string Name, string Text)>? shadowed = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-spid-layer-" + Guid.NewGuid().ToString("N"));
        var instance = Path.Combine(Root, "instance");
        var profileDir = Path.Combine(instance, "profiles", "Default");
        var mods = Path.Combine(instance, "mods");
        IniMod = Path.Combine(mods, "HcSpidIni");
        Loser = Path.Combine(mods, "HcSpidLoser");
        foreach (var d in new[] { profileDir, Path.Combine(Root, "game", "Data"), Path.Combine(mods, "HcSpidPlugins"), IniMod, Loser })
            Directory.CreateDirectory(d);
        SkyPatcherSwitchesFile.WriteTweaks(IniMod);
        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace('\\', '/') + ")\r\n");

        var armorSrc = new SkyrimMod(ModKey.FromFileName(ArmorSrc), SkyrimRelease.SkyrimSE);
        var boots = new Armor(new FormKey(armorSrc.ModKey, 0x800), SkyrimRelease.SkyrimSE) { EditorID = "HcBoots" };
        armorSrc.Armors.Add(boots);
        Write(armorSrc, Path.Combine(mods, "HcSpidPlugins", ArmorSrc));

        var mod = new SkyrimMod(ModKey.FromFileName(Spid), SkyrimRelease.SkyrimSE);
        mod.Npcs.Add(new Npc(new FormKey(mod.ModKey, 0x810), SkyrimRelease.SkyrimSE) { EditorID = "HcAranea", Name = "Aranea" });
        mod.Races.Add(new Race(new FormKey(mod.ModKey, 0x811), SkyrimRelease.SkyrimSE) { EditorID = "HcDeerRace" });
        mod.Perks.Add(new Perk(new FormKey(mod.ModKey, 0x812), SkyrimRelease.SkyrimSE) { EditorID = "HcPerk" });
        mod.Spells.Add(new Spell(new FormKey(mod.ModKey, 0x813), SkyrimRelease.SkyrimSE) { EditorID = "HcSpell" });
        mod.Keywords.Add(new Keyword(new FormKey(mod.ModKey, 0x814), SkyrimRelease.SkyrimSE) { EditorID = "HcKeyword" });
        mod.Outfits.Add(new Outfit(new FormKey(mod.ModKey, 0x815), SkyrimRelease.SkyrimSE)
        {
            EditorID = "HcOutfit",
            Items = new ExtendedList<IFormLinkGetter<IOutfitTargetGetter>> { boots.ToLink<IOutfitTargetGetter>() },
        });
        Write(mod, Path.Combine(mods, "HcSpidPlugins", Spid));

        var other = new SkyrimMod(ModKey.FromFileName(Other), SkyrimRelease.SkyrimSE);
        other.Npcs.Add(new Npc(new FormKey(other.ModKey, 0x800), SkyrimRelease.SkyrimSE) { EditorID = "HcOtherNpc", Name = "Skald" });
        Write(other, Path.Combine(mods, "HcSpidPlugins", Other));

        foreach (var (name, text) in files) File.WriteAllText(Path.Combine(IniMod, name), text);
        foreach (var (name, text) in shadowed ?? []) File.WriteAllText(Path.Combine(Loser, name), text);

        var order = new[] { ArmorSrc, Spid, Other };
        File.WriteAllText(Path.Combine(profileDir, "Skyrim.ini"), "[Archive]\r\nsResourceArchiveList=\r\n");
        File.WriteAllText(Path.Combine(profileDir, "loadorder.txt"), "# header\r\n" + string.Concat(order.Select(o => o + "\r\n")));
        File.WriteAllText(Path.Combine(profileDir, "plugins.txt"), string.Concat(order.Select(o => "*" + o + "\r\n")));
        // highest priority first: the INI mod wins over the loser
        File.WriteAllText(Path.Combine(profileDir, "modlist.txt"), "# header\r\n+HcSpidIni\r\n+HcSpidLoser\r\n+HcSpidPlugins\r\n");
        Svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(Root, "houseCARL.user.json")));
    }

    static void Write(SkyrimMod m, string path) => m.BeginWrite.ToPath(path).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

    public string Layer(string[]? formids = null, string[]? plugins = null, string? filter = null, bool definedIn = false, int maxChars = 0)
        => SkyPatcherTools.Layer(Svc, distributor: new[] { "spid" }, filter: filter, formids: formids, max_chars: maxChars,
            plugins: plugins is null ? null : new SkyPatcherTools.LayerScope { names = plugins, defined_in = definedIn });

    public void Dispose()
    {
        Svc.Dispose();
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}

/// <summary>housecarl_layer reading SPID: the winning files in SPID's order, the lines expanded under filter= with what
/// SPID does with each, and formids= / plugins= selecting the lines naming a form, labelled target, filter or value.</summary>
[Trait("tier", "integration")]
public sealed class SpidLayerTests : IDisposable
{
    const string Perk = "000812:HcSpid.esp", Aranea = "000810:HcSpid.esp", Race = "000811:HcSpid.esp", Outfit = "000815:HcSpid.esp";

    readonly SpidWorld _w = new(new[]
    {
        ("HcA_DISTR.ini",
            "; the Adamant shape: a perk by EditorID\r\n"
            + "Perk = HcPerk\r\n"                                            // :2
            + "Perk = HcNoSuchPerk\r\n"                                      // :3 dropped (Adamant_DISTR.ini:13)
            + "Spell = HcSpell|NONE|0x1000810,HcDeerRace\r\n"                // :4 a runtime NPC target and a race filter
            + "Outfit = HcOutfit|Skald\r\n"                                  // :5 a display-name string; the outfit holds HcBoots
            + "Spell = HcSpell||HcOther.esp\r\n"                             // :6 a plugin term
            + "LinkedSpell = HcSpell|HcPerk\r\n"                             // :7
            + "ExclusiveGroup = Mine|HcKeyword,HcPerk\r\n"                   // :8
            + "DeathSpell = HcSpell||HcDeerRace\r\n"                         // :9
            + "Keyword = HcCreated\r\n"),                                    // :10
        ("HcB_DISTR.ini", "Outfit = 0x815~HcSpid.esp\r\n"),
    }, shadowed: new[] { ("HcB_DISTR.ini", "Outfit = HcLoserLine\r\n") });

    public void Dispose() => _w.Dispose();

    // The proof shape (plan step 28, dev Adamant_DISTR.ini:3): plugins= lists a line naming a record by EditorID, labelled value.
    [Fact]
    public void PluginsListsALineNamingItsRecordByEditorId()
    {
        var text = _w.Layer(plugins: new[] { SpidWorld.Spid });
        Assert.Contains(":2  Perk = HcPerk\n          ↳ value Perk: HcPerk → " + Perk, text);
        Assert.DoesNotContain(":3  Perk = HcNoSuchPerk", text);   // names no record, so no plugin
    }

    // Pick SPID Q1 (a): the NPC a filter names is the target, a race a filter.
    [Fact]
    public void ANamedNpcIsLabelledTarget()
    {
        var text = _w.Layer(formids: new[] { Aranea, Race });
        Assert.Contains("↳ target form filter: 0x1000810 → " + Aranea, text);
        Assert.Contains("↳ filter form filter: HcDeerRace → " + Race, text);
    }

    // Pick (4) "Named forms only": a line handing out an outfit does not touch the plugin of the armour inside it.
    [Fact]
    public void AnOutfitLineDoesNotTouchThePluginsOfItsItems()
    {
        Assert.Contains("0 line(s) in 0 of 2 file(s)", _w.Layer(plugins: new[] { SpidWorld.ArmorSrc }));
        Assert.Contains(":5  Outfit = HcOutfit|Skald", _w.Layer(plugins: new[] { SpidWorld.Spid }));
    }

    // Pick SPID Q2 (a): a display-name token names no form, so the plugin of the NPC holding that name is not touched; the line says so.
    [Fact]
    public void ADisplayNameTokenTouchesNoPluginAndIsMarked()
    {
        Assert.Contains("1 line(s) in 1 of 2 file(s)", _w.Layer(plugins: new[] { SpidWorld.Other }));   // only :6's plugin term
        Assert.Contains("string filter 'Skald' is no NPC or keyword EditorID, so it matches by name (an actor's display name) in the running game",
            _w.Layer(filter: "HcA_"));
    }

    // Pick (12)(iii): a plugin term touches its plugin under plugins=; it names no form, so formids= never selects it.
    [Fact]
    public void APluginTermTouchesItsPluginButNamesNoForm()
    {
        var text = _w.Layer(plugins: new[] { SpidWorld.Other });
        Assert.Contains(":6  Spell = HcSpell||HcOther.esp\n          ↳ filter form filter: HcOther.esp (the plugin, selecting the NPCs it defines)", text);
        Assert.DoesNotContain(":6  ", _w.Layer(formids: new[] { "000800:HcOther.esp" }));
        Assert.DoesNotContain(":6  ", _w.Layer(formids: new[] { "000800:HcOther.esp" }, plugins: new[] { SpidWorld.Other }));
    }

    // Pick SPID Q3 (a): Linked, ExclusiveGroup and Death lines are read and selected like any other.
    [Fact]
    public void EveryLineKindIsSelected()
    {
        var text = _w.Layer(formids: new[] { Perk });
        Assert.Contains(":7  LinkedSpell = HcSpell|HcPerk\n          ↳ filter linked parents: HcPerk → " + Perk, text);
        Assert.Contains(":8  ExclusiveGroup = Mine|HcKeyword,HcPerk\n          ↳ filter group members: HcPerk → " + Perk, text);
        Assert.Contains(":9  DeathSpell", _w.Layer(formids: new[] { Race }));
    }

    // R41 and answer 3: a line's outcome where a name finds nothing is said on the line under filter=.
    [Fact]
    public void FilterExpandsALineWithWhatSpidDoes()
    {
        var text = _w.Layer(filter: "HcA_");
        Assert.Contains(":3  Perk = HcNoSuchPerk\n          [!] SPID drops this line: value 'HcNoSuchPerk' finds no record by that EditorID in the active order, so SPID drops the line", text);
        Assert.Contains(":10  Keyword = HcCreated\n          [!] value 'HcCreated' finds no record by that EditorID in the active order, so SPID creates a keyword of that name and hands it out", text);
    }

    // Pick SPID Q4 (a): each first-wins line states its place, across files in SPID's order; no conflict report.
    [Fact]
    public void AFirstWinsLineStatesItsPlace()
    {
        var text = _w.Layer(formids: new[] { Outfit });
        Assert.Contains(":5  Outfit = HcOutfit|Skald\n          [!] string filter 'Skald'", text);
        Assert.Contains("first wins: Outfit line 1 of 2 in SPID's order", text);
        Assert.Contains("first wins: Outfit line 2 of 2 in SPID's order", text);
        Assert.DoesNotContain("conflict", text);
    }

    // R40: the shadowed copy is named and never read.
    [Fact]
    public void AShadowedCopyIsNamedAndNeverRead()
    {
        var text = _w.Layer(filter: "HcB_");
        Assert.Contains("[!] shadows same-path copies from HcSpidLoser, never read", text);
        Assert.DoesNotContain("HcLoserLine", text);
    }

    // Pick "Yes, measure first" (no parse cache) and R15: nothing is held between calls, so an edited file is read anew.
    [Fact]
    public void AnEditedFileIsReadAnewOnTheNextCall()
    {
        Assert.Contains("2 file(s) in Data (2 read), 10 line(s)", _w.Layer());
        File.AppendAllText(Path.Combine(_w.IniMod, "HcB_DISTR.ini"), "Perk = HcPerk\r\n");
        Assert.Contains("2 file(s) in Data (2 read), 11 line(s)", _w.Layer());
    }

    // R41: one EditorID table serves both formats in one call.
    [Fact]
    public void BothFormatsReadInOneCallShareOneEditorIdWalk()
    {
        _w.Layer();   // warms the index
        var before = _w.Svc.Counters.EditorIdSweeps;
        SkyPatcherTools.Layer(_w.Svc, formids: new[] { Perk });
        Assert.Equal(1, _w.Svc.Counters.EditorIdSweeps - before);
    }
}

/// <summary>The SPID render's file rows against data no instance holds.</summary>
[Trait("tier", "unit")]
public sealed class SpidWireTests
{
    // R40: a copy only inside an archive is listed, said not applied, and no line of it is read.
    [Fact]
    public void AnArchiveOnlyCopyIsSaidNotApplied()
    {
        var file = new DistributorDiscovery.ConfigFile("Packed_DISTR.ini", "HcMod", null, [], "only inside an archive (HcMod.bsa), which SPID never lists", null);
        var spid = new SpidLayerData(RuntimeFormats.Spid, new[] { new SpidFileData(file, [], null) }, "Default");
        var scan = new SkyPatcherDiscovery.LayerScan([], [], false, new SkyPatcherDiscovery.Switches(new Dictionary<string, bool>(), null));
        var d = new SkyPatcherLayerData(scan, [], [], [], [], [], [], false, [], "Default") { SkyPatcherRead = false, Spid = spid };
        var text = SpidWire.RenderLayers(d, null, 40000, null);
        Assert.Contains("1 file(s) in Data (0 read), 0 line(s)", text);
        Assert.Contains("Packed_DISTR.ini  (file 1 of 1, 0 line(s)) ← HcMod  [!] NOT applied: only inside an archive", text);
    }
}
