using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>housecarl_layer's selectors (#1286): distributor= names the format, formids= and plugins= select the lines
/// naming a form they select, anywhere on the line and each labelled where; a selection matching nothing says so without
/// the whole-layer no-op notes; the old tool name is refused naming the new one.</summary>
[Trait("tier", "integration")]
public sealed class LayerSelectTests : IDisposable
{
    const string Ini = "HcSelect.ini";
    const string Other = "HcSpRelOther.esp";
    const string HeelKw = "000800:HcSpRel.esp";
    const string AddKw = "000801:HcSpRel.esp";
    const string OtherKw = "000800:HcSpRelOther.esp";
    readonly SkyPatcherRelevanceWorld _w;

    // :1 a FormID target, :2 an EditorID target (HcSpRelOther.esp overrides it), :3 a keyword filter by EditorID and an
    // op value by FormID, :4 an op value by EditorID, :5 a name literal spelling a FormID, :6 an unknown key with a
    // FormID in its second sub-arg, :7 an EditorID target that names nothing (the layer's one no-op note), :8 an op value
    // HcSpRelOther.esp does not define, :9 an EditorID in an op value's second sub-arg, :10 a target named twice.
    // HcOther.ini: a line with no selected form. ../bogusFolder: an undocumented folder's line.
    public LayerSelectTests() => _w = new SkyPatcherRelevanceWorld(new[]
    {
        (Ini, "filterByArmors=HcSpRel.esp|810:value=5\r\n"
              + "filterByArmors=HcPlainCuirass:value=6\r\n"
              + "filterByKeywords=HcHeelKw:keywordsToAdd=HcSpRelOther.esp|800\r\n"
              + "filterByArmors=HcSpRel.esp|812:keywordsToAdd=HcAddKw\r\n"
              + "filterByArmors=HcSpRel.esp|812:fullName=~HcSpRel.esp|810~\r\n"
              + "filterByArmors=HcSpRel.esp|812:bogusKey=x~HcSpRel.esp|802\r\n"
              + "filterByArmors=HcNoSuchArmor:value=1\r\n"
              + "filterByArmors=HcSpRel.esp|812:keywordsToAdd=HcSpRelOther.esp|8FF\r\n"
              + "filterByArmors=HcSpRel.esp|812:keywordsToAdd=HcSpRel.esp|802~HcAddKw\r\n"
              + "filterByArmors=HcSpRel.esp|810,HcSpRel.esp|810:keywordsToAdd=HcSpRel.esp|802\r\n"),
        ("HcOther.ini", "filterByArmors=HcSpRel.esp|812:value=9\r\n"),
        ("../bogusFolder/HcBogus.ini", "anything=HcSpRel.esp|811\r\n"),
    }, otherPlugin: true,
        otherRecords: o => o.Armors.Add(new Armor(new FormKey(ModKey.FromFileName("HcSpRel.esp"), 0x811), SkyrimRelease.SkyrimSE) { EditorID = "HcPlainCuirass" }));

    public void Dispose() => _w.Dispose();

    string Layer(string[]? formids = null, string[]? plugins = null, bool definedIn = false, string? filter = null, string[]? distributor = null)
        => SkyPatcherTools.Layer(_w.Svc, distributor: distributor, filter: filter, formids: formids,
            plugins: plugins is null ? null : new SkyPatcherTools.LayerScope { names = plugins, defined_in = definedIn });

    [Fact]
    public void FormidsSelectAnEditorIdTargetedLine()
    {
        var text = Layer(formids: new[] { _w.Plain });
        Assert.Contains(":2  filterByArmors=HcPlainCuirass:value=6", text);
        Assert.Contains("↳ target filterByArmors: HcPlainCuirass → 000811:HcSpRel.esp", text);
        Assert.DoesNotContain(":1  filterByArmors=HcSpRel.esp|810", text);   // only the selected lines expand
        Assert.Contains("lines naming a form of formids=[" + _w.Plain + "] — 2 line(s) in 2 of 3 INI(s)", text);   // the bogus folder's '|' form too
    }

    [Fact]
    public void FormidsSelectAFilterFormByEditorId()
    {
        var text = Layer(formids: new[] { HeelKw });
        Assert.Contains("↳ filter filterByKeywords: HcHeelKw → " + HeelKw, text);
    }

    // Q2 (a): a form named only as an op value still makes the line touch its plugin.
    [Fact]
    public void AFormNamedOnlyAsAnOpValueSelectsItsLine()
    {
        var text = Layer(plugins: new[] { Other }, definedIn: true);
        Assert.Contains(":3  filterByKeywords=HcHeelKw:keywordsToAdd=HcSpRelOther.esp|800", text);
        Assert.Contains("↳ value keywordsToAdd: " + OtherKw, text);
    }

    [Fact]
    public void AnOpValueEditorIdIsLookedUpInTheOpsScope()
    {
        var text = Layer(formids: new[] { AddKw });
        Assert.Contains("↳ value keywordsToAdd: HcAddKw → " + AddKw, text);
    }

    // The records meaning: a plugin touches what it overrides, so HcSpRelOther.esp's override of HcPlainCuirass selects :2.
    [Fact]
    public void PluginsSelectsLinesNamingARecordThePluginOverrides()
    {
        var text = Layer(plugins: new[] { Other });
        Assert.Contains(":2  filterByArmors=HcPlainCuirass:value=6", text);
        Assert.Contains(":3  ", text);
    }

    [Fact]
    public void DefinedInDropsRecordsThePluginOnlyOverrides()
    {
        var text = Layer(plugins: new[] { Other }, definedIn: true);
        Assert.DoesNotContain(":2  filterByArmors=HcPlainCuirass", text);
        Assert.Contains("(defined_in)", text);
    }

    // :8 names HcSpRelOther.esp|8FF, a form HcSpRelOther.esp does not hold: neither plugins= nor defined_in selects it.
    [Fact]
    public void AFormItsOwnPluginDoesNotHoldIsSelectedByNeitherPluginsReading()
    {
        Assert.DoesNotContain(":8  ", Layer(plugins: new[] { Other }));
        Assert.DoesNotContain(":8  ", Layer(plugins: new[] { Other }, definedIn: true));
    }

    // As on records, a narrowing like AI Overhaul's: a plugin that only overrides the forms lines name selects none of them.
    [Fact]
    public void DefinedInOnAPluginThatOnlyOverridesSelectsNothing()
    {
        var text = Layer(formids: new[] { _w.Plain }, plugins: new[] { Other }, definedIn: true);
        Assert.Contains("0 of 3 INI(s) hold one", text);
    }

    [Fact]
    public void FormidsAndPluginsBothGivenMustBothPass()
    {
        Assert.Contains(":2  ", Layer(formids: new[] { _w.Plain }, plugins: new[] { Other }));
        Assert.Contains("0 of 3 INI(s) hold one", Layer(formids: new[] { _w.Heel }, plugins: new[] { Other }));
    }

    /// <summary>Pins what the tokenizer already gives: a <c>~…~</c> literal spelling a FormID keeps its '~' on the plugin name, so it names no loaded record.</summary>
    [Fact]
    public void ANameLiteralNamesNoRecord()
    {
        var text = Layer(formids: new[] { _w.Heel });
        Assert.Contains(":1  ", text);
        Assert.DoesNotContain(":5  ", text);
    }

    [Fact]
    public void AnUnknownKeyStillGivesItsFormsLabelledSo()
    {
        var text = Layer(formids: new[] { "000802:HcSpRel.esp" });
        Assert.Contains("↳ unknown key bogusKey: 000802:HcSpRel.esp", text);
    }

    [Fact]
    public void AnUndocumentedFoldersLineGivesItsFormIdForms()
    {
        var text = Layer(formids: new[] { _w.Plain });
        Assert.Contains("HcBogus.ini", text);
        Assert.Contains("↳ unknown key anything: 000811:HcSpRel.esp", text);
    }

    [Fact]
    public void FilterAndSelectorMustBothMatchAFile()
    {
        Assert.Contains("0 of 3 INI(s) hold one", Layer(formids: new[] { _w.Plain }, filter: "HcOther"));
        Assert.Contains(":1  ", Layer(formids: new[] { _w.Heel }, filter: Ini));
    }

    [Fact]
    public void ASelectionMatchingNothingSaysSoWithoutTheNoOpNotes()
    {
        Assert.Contains("no-op scan: 1 explicit EditorID target(s) did not resolve", Layer());   // the overview carries it
        var text = Layer(formids: new[] { "000899:HcSpRel.esp" });
        Assert.Contains("0 of 3 INI(s) hold one", text);
        Assert.Contains("as a target, a filter form, an op value or under a key SkyPatcher does not document.", text);
        Assert.DoesNotContain("no-op scan", text);
        Assert.DoesNotContain("patch line(s)", text);   // never the overview
    }

    // Plain is named in both type folders, so some max_chars cuts the second; the notice names what narrows the listing.
    [Fact]
    public void ASelectorCutNamesTheSelectorsAndAFilterCutReadsAsBefore()
    {
        string? Cut(Func<int, string> call) => Enumerable.Range(10, 300).Select(i => call(i * 10))
            .FirstOrDefault(t => t.Contains("remaining matching folders omitted at max_chars"));
        var selected = Cut(m => SkyPatcherTools.Layer(_w.Svc, formids: new[] { _w.Plain }, max_chars: m));
        Assert.NotNull(selected);
        Assert.Contains("raise it or narrow formids=/plugins=]", selected);
        var filtered = Cut(m => SkyPatcherTools.Layer(_w.Svc, filter: "Hc", max_chars: m));
        Assert.NotNull(filtered);
        Assert.Contains("raise it or narrow filter=]", filtered);
    }

    [Fact]
    public void AFilterMatchingNothingCarriesNoNoOpNote()
    {
        var text = Layer(filter: "NoSuchIni");
        Assert.Contains("0 of 3 INI(s) match", text);
        Assert.DoesNotContain("no-op scan", text);
    }

    // Main's ZeroMatch kept the unreadable-plugin note: a plugin the sweep cannot open may be why nothing matched.
    [Fact]
    public void AZeroAnswerKeepsTheUnreadablePluginNote()
    {
        // HcSpRelOther.esp here overrides nothing a line names, so only the unresolved EditorID's sweep opens it.
        using var w = new SkyPatcherRelevanceWorld(new[] { (Ini, "filterByArmors=HcNoSuchArmor:value=1\r\n") }, otherPlugin: true);
        SkyPatcherTools.Layer(w.Svc);
        // same size and write time, so the index keeps the plugin and only the EditorID sweep finds it unreadable
        var stamp = File.GetLastWriteTimeUtc(w.OtherPath);
        File.WriteAllBytes(w.OtherPath, new byte[new FileInfo(w.OtherPath).Length]);
        File.SetLastWriteTimeUtc(w.OtherPath, stamp);

        foreach (var text in new[] { SkyPatcherTools.Layer(w.Svc, filter: "NoSuchIni"), SkyPatcherTools.Layer(w.Svc, formids: new[] { "000899:HcSpRel.esp" }) })
        {
            Assert.Contains("Lines naming a record it defines could not be resolved.", text);
            Assert.DoesNotContain("explicit EditorID target(s) did not resolve", text);
        }
    }

    [Fact]
    public void FormidsReadFromAFile()
    {
        var file = Path.Combine(_w.Root, "ids.txt");
        File.WriteAllText(file, _w.Plain + "\n");
        var text = Layer(formids: new[] { "@" + file });
        Assert.Contains(":2  filterByArmors=HcPlainCuirass", text);
        Assert.Contains("formids=@" + file, text);
    }

    [Fact]
    public void AStaleFormidsArtifactIsRefused()
    {
        var file = Path.Combine(_w.Root, "ids-" + Guid.NewGuid().ToString("N") + ".jsonl");
        using (var writer = new ResultArtifact.Writer())
        {
            writer.WriteRow((w, _) => { w.WriteStartObject(); w.WriteString("formid", _w.Plain); w.WriteEndObject(); });
            Assert.Null(writer.Save(ArtifactTarget.Named(file), ToolNames.Records, Array.Empty<KeyValuePair<string, string>>(),
                                    "formid", new[] { "formid" }, "input order", total: 1, epoch: "e2-deadbeef").Error);
        }
        Assert.Contains("was captured at epoch=e2-deadbeef", Layer(formids: new[] { "@" + file }));
    }

    [Fact]
    public void ABadFormidIsRefusedNamingIt()
        => Assert.StartsWith("error: formids= entry 'nonsense' is not a FormID", Layer(formids: new[] { "nonsense" }));

    [Fact]
    public void PluginsWithNoNamesIsRefused()
        => Assert.StartsWith("error: plugins= selects by plugins.names", Layer(plugins: Array.Empty<string>()));

    [Fact]
    public void ABlankPluginNameIsRefused()
        => Assert.StartsWith("error: plugins.names[0] is empty", Layer(plugins: new[] { " " }));

    [Fact]
    public void PluginsNamingNothingLoadedIsRefused()
        => Assert.StartsWith("error: plugins= names nothing the load order carries", Layer(plugins: new[] { "HcNotLoaded.esp" }));

    [Fact]
    public void ANamedPluginTheOrderLacksIsNotedNotDropped()
    {
        var text = Layer(plugins: new[] { Other, "HcNotLoaded.esp" });
        Assert.Contains(":2  ", text);
        Assert.Contains("[!] this answer covers the 1 named plugin(s) the load order carries", text);
        Assert.DoesNotContain("[!] note:", text);
    }

    [Fact]
    public void DistributorSkypatcherReadsAsOmitted()
        => Assert.Equal(Layer(), Layer(distributor: new[] { "SkyPatcher" }));

    [Fact]
    public void DistributorOarIsNotRead() => Assert.StartsWith(
        "error: distributor='oar' is not read: houseCARL does not read Open Animation Replacer files", Layer(distributor: new[] { "oar" }));

    [Fact]
    public void DistributorIedIsNotRead() => Assert.StartsWith(
        "error: distributor='ied' is not read: houseCARL does not read Immersive Equipment Displays files", Layer(distributor: new[] { "ied" }));

    [Fact]
    public void DistributorDescIsNotRead() => Assert.StartsWith(
        "error: distributor='desc' is not read: houseCARL does not read Description Framework files", Layer(distributor: new[] { "desc" }));

    [Fact]
    public void DistributorDsdIsNotRead() => Assert.StartsWith(
        "error: distributor='dsd' is not read: houseCARL does not read Dynamic String Distributor files", Layer(distributor: new[] { "dsd" }));

    [Theory]
    [InlineData("spid", "Spell Perk Item Distributor")]
    [InlineData("kid", "Keyword Item Distributor")]
    [InlineData("bos", "Base Object Swapper")]
    [InlineData("flm", "FormList Manipulator")]
    public void ADistributorStillToComeIsNotReadYet(string name, string full)
        => Assert.StartsWith($"error: distributor='{name}' is not read yet: this build reads SkyPatcher lines only, and {full} lines",
            Layer(distributor: new[] { name }));

    [Fact]
    public void AnUnknownDistributorIsNotAFormat()
        => Assert.StartsWith("error: distributor='xyz' is not a format", Layer(distributor: new[] { "xyz" }));

    // Q1 (a): the tool is housecarl_layer, and the old name is a retired row naming it.
    [Fact]
    public void TheOldNameIsRefusedNamingTheNewOne()
    {
        Assert.Equal("housecarl_layer", ToolNames.Layer);
        var hint = AliasTable.RetiredToolHint("housecarl_skypatcher_layer");
        Assert.NotNull(hint);
        Assert.StartsWith("error: housecarl_skypatcher_layer is not on this surface — absorbed into housecarl_layer:", hint);
    }
}
