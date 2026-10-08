using System.Text.Json;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A test instance holding three armors and the given armor-folder INIs: HcHeelBoots carries HcHeelKw,
/// HcBothBoots carries HcHeelKw and HcKw2, HcPlainCuirass carries none; with <c>otherPlugin</c>, a second plugin
/// at <see cref="OtherPath"/> defines the keyword HcOtherKw.</summary>
sealed class SkyPatcherRelevanceWorld : IDisposable
{
    const string PluginName = "HcSpRel.esp";
    const string OtherName = "HcSpRelOther.esp";
    public readonly string Root, OtherPath;
    public readonly LoadOrderService Svc;
    public readonly string Heel, Both, Plain;

    public SkyPatcherRelevanceWorld(IEnumerable<(string Name, string Text)> inis, bool otherPlugin = false)
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-skypatcher-relevance-" + Guid.NewGuid().ToString("N"));
        var instance = Path.Combine(Root, "instance");
        var profileDir = Path.Combine(instance, "profiles", "Default");
        var mods = Path.Combine(instance, "mods");
        var iniDir = Path.Combine(mods, "HcSpRelIni", "SKSE", "Plugins", "SkyPatcher", "armor");
        foreach (var d in new[] { profileDir, Path.Combine(Root, "game", "Data"), Path.Combine(mods, "HcSpRelPlugins"), iniDir })
            Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace('\\', '/') + ")\r\n");

        var mod = new SkyrimMod(ModKey.FromFileName(PluginName), SkyrimRelease.SkyrimSE);
        var heelKw = new Keyword(new FormKey(mod.ModKey, 0x800), SkyrimRelease.SkyrimSE) { EditorID = "HcHeelKw" };
        var addKw = new Keyword(new FormKey(mod.ModKey, 0x801), SkyrimRelease.SkyrimSE) { EditorID = "HcAddKw" };
        var kw2 = new Keyword(new FormKey(mod.ModKey, 0x802), SkyrimRelease.SkyrimSE) { EditorID = "HcKw2" };
        mod.Keywords.Add(heelKw);
        mod.Keywords.Add(addKw);
        mod.Keywords.Add(kw2);
        var heel = new Armor(new FormKey(mod.ModKey, 0x810), SkyrimRelease.SkyrimSE)
        {
            EditorID = "HcHeelBoots",
            Keywords = new ExtendedList<IFormLinkGetter<IKeywordGetter>> { heelKw.ToLink() },
        };
        var plain = new Armor(new FormKey(mod.ModKey, 0x811), SkyrimRelease.SkyrimSE) { EditorID = "HcPlainCuirass" };
        var both = new Armor(new FormKey(mod.ModKey, 0x812), SkyrimRelease.SkyrimSE)
        {
            EditorID = "HcBothBoots",
            Keywords = new ExtendedList<IFormLinkGetter<IKeywordGetter>> { heelKw.ToLink(), kw2.ToLink() },
        };
        mod.Armors.Add(heel);
        mod.Armors.Add(plain);
        mod.Armors.Add(both);
        mod.BeginWrite.ToPath(Path.Combine(mods, "HcSpRelPlugins", PluginName)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        Heel = RecordsWorld.Fid(heel.FormKey);
        Plain = RecordsWorld.Fid(plain.FormKey);
        Both = RecordsWorld.Fid(both.FormKey);

        foreach (var (name, text) in inis)
            File.WriteAllText(Path.Combine(iniDir, name), text);

        OtherPath = Path.Combine(mods, "HcSpRelOther", OtherName);
        if (otherPlugin)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(OtherPath)!);
            var other = new SkyrimMod(ModKey.FromFileName(OtherName), SkyrimRelease.SkyrimSE);
            other.Keywords.Add(new Keyword(new FormKey(other.ModKey, 0x800), SkyrimRelease.SkyrimSE) { EditorID = "HcOtherKw" });
            other.BeginWrite.ToPath(OtherPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        }
        var order = otherPlugin ? new[] { PluginName, OtherName } : new[] { PluginName };

        File.WriteAllText(Path.Combine(profileDir, "Skyrim.ini"), "[Archive]\r\nsResourceArchiveList=\r\n");
        File.WriteAllText(Path.Combine(profileDir, "loadorder.txt"), "# header\r\n" + string.Concat(order.Select(o => o + "\r\n")));
        File.WriteAllText(Path.Combine(profileDir, "plugins.txt"), string.Concat(order.Select(o => "*" + o + "\r\n")));
        File.WriteAllText(Path.Combine(profileDir, "modlist.txt"),
            "# header\r\n+HcSpRelIni\r\n+HcSpRelPlugins\r\n" + (otherPlugin ? "+HcSpRelOther\r\n" : ""));

        Svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(Root, "houseCARL.user.json")));
    }

    public void Dispose()
    {
        Svc.Dispose();
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }

    /// <summary>The post-state overlay source, with a draft INI folded in when given.</summary>
    public static JsonElement Post(string? draft = null) => JsonDocument.Parse(
        "{\"overlay\": \"skypatcher\", \"state\": \"post\"" + (draft is null ? "" : $", \"ini\": \"{draft.Replace('\\', '/')}\"") + "}").RootElement.Clone();

    public string Read(string[] fids, string? format = null, string? draft = null)
        => RecordsTools.Records(Svc, formids: fids, source: Post(draft), format: format);
}

/// <summary>An overlay read carries the SkyPatcher warnings that bear on its records; a note from a line that cannot
/// reach them is one pointer to housecarl_skypatcher_layer filter=, which lists it under its line (#1093).</summary>
[Trait("tier", "integration")]
public sealed class SkyPatcherWarningRelevanceTests : IDisposable
{
    const string IniName = "HcRelevance.ini";
    const string RidingIni = "HcRiding.ini";
    readonly SkyPatcherRelevanceWorld _w;

    // HcRelevance :1 an Or list (a missing keyword cannot flip it), :2 a bare list (a missing keyword forces NoMatch), :3 an unknown key.
    // HcRiding: two Or lines sharing one missing keyword; HcBothBoots meets both, HcHeelBoots only the first.
    public SkyPatcherWarningRelevanceTests() => _w = new SkyPatcherRelevanceWorld(new[]
    {
        (IniName, "filterByKeywordsOr=HcNoSuchA,HcHeelKw,HcNoSuchB:keywordsToAdd=HcAddKw\r\n"
                  + "filterByKeywords=HcHeelKw,HcNoSuchC:keywordsToAdd=HcAddKw\r\n"
                  + "filterByBogusThing=1:keywordsToAdd=HcAddKw\r\n"),
        (RidingIni, "filterByKeywordsOr=HcNoSuchD,HcHeelKw:keywordsToAdd=HcAddKw\r\n"
                    + "filterByKeywordsOr=HcNoSuchD,HcKw2:keywordsToAdd=HcAddKw\r\n"),
    });

    public void Dispose() => _w.Dispose();

    string Read(string fid, string? format = null) => _w.Read(new[] { fid }, format);

    [Fact]
    public void ARecordNoLineReachesGetsOnePointerAndNoWarningText()
    {
        var text = Read(_w.Plain);

        Assert.Contains($"[!] skypatcher: note(s) on lines that do not reach these records — {ToolNames.SkypatcherLayer} filter={IniName} lists them", text);
        Assert.DoesNotContain("HcNoSuchA", text);
        Assert.DoesNotContain("HcNoSuchC", text);
    }

    [Fact]
    public void ARecordTheOrLineMatchesGetsItsWarnings()
        => Assert.Contains("keyword 'HcNoSuchA' (in a filterByKeywordsOr) resolves to nothing", Read(_w.Heel));

    // the bare list's resolved keyword is on the record, so only the missing one stops the line
    [Fact]
    public void AMissingKeywordThatStopsABareLineRidesTheRead()
        => Assert.Contains("keyword 'HcNoSuchC' (in a filterByKeywords) resolves to nothing", Read(_w.Heel));

    [Fact]
    public void AnUnknownKeyRidesEveryRead()
        => Assert.Contains("'filterByBogusThing' are not in the SkyPatcher reference", Read(_w.Plain));

    // HcRiding:2 reaches HcBothBoots (its warning deduped behind :1's), so it is no note for HcHeelBoots, which it misses
    [Fact]
    public void ALineThatReachesOneRecordReadIsNoNoteForAnother()
    {
        var text = _w.Read(new[] { _w.Both, _w.Heel });

        Assert.Contains("keyword 'HcNoSuchD' (in a filterByKeywordsOr) resolves to nothing", text);
        Assert.DoesNotContain($"filter={RidingIni}", text);
    }

    // a draft is in no layer listing, so a pointer would name a list that does not hold its notes
    [Fact]
    public void ADraftLineThatReachesNoRecordStillRidesTheRead()
    {
        var dir = Path.Combine(_w.Root, "draft", "armor");
        Directory.CreateDirectory(dir);
        var draft = Path.Combine(dir, "HcDraft.ini");
        File.WriteAllText(draft, "filterByKeywordsOr=HcNoSuchE:keywordsToAdd=HcAddKw\r\n");

        var text = _w.Read(new[] { _w.Plain }, draft: draft);

        Assert.Contains("keyword 'HcNoSuchE' (in a filterByKeywordsOr) resolves to nothing", text);
        Assert.DoesNotContain("filter=HcDraft.ini", text);
    }

    [Fact]
    public void TheJsonWarningsMemberCarriesThePointer()
    {
        using var doc = JsonDocument.Parse(Read(_w.Plain, "json"));
        Assert.Contains($"filter={IniName} lists them", doc.RootElement.GetProperty("skypatcher_warnings").GetString());
    }

    [Fact]
    public void TheLayerFilterShowsTheLintUnderItsLine()
    {
        var text = SkyPatcherTools.SkyPatcherLayer(_w.Svc, filter: IniName);

        int line1 = text.IndexOf(":1  filterByKeywordsOr", StringComparison.Ordinal);
        int lint = text.IndexOf("[!] keyword 'HcNoSuchA' (in a filterByKeywordsOr) resolves to nothing", StringComparison.Ordinal);
        int line2 = text.IndexOf(":2  filterByKeywords=", StringComparison.Ordinal);
        Assert.True(line1 >= 0 && line1 < lint && lint < line2, text);
    }
}

/// <summary>Past the warning cap and the per-INI pointer count: the overflow count stays with the warnings, and the
/// INIs past the fifth roll into one line.</summary>
[Trait("tier", "integration")]
public sealed class SkyPatcherNotePointerRollUpTests : IDisposable
{
    readonly SkyPatcherRelevanceWorld _w;

    // HcOver: one unknown-key line more than the cap, each riding; HcNote1..6: one Or line each that reaches no record.
    public SkyPatcherNotePointerRollUpTests() => _w = new SkyPatcherRelevanceWorld(
        new[] { ("HcOver.ini", string.Concat(Enumerable.Range(1, HousecarlCore.SkyPatcherOverlay.WarningSink.Cap + 1)
                    .Select(i => $"filterByBogus{i}=1:keywordsToAdd=HcAddKw\r\n"))) }
        .Concat(Enumerable.Range(1, 6).Select(i => ($"HcNote{i}.ini", $"filterByKeywordsOr=HcNoSuchN{i}:keywordsToAdd=HcAddKw\r\n"))));

    public void Dispose() => _w.Dispose();

    [Fact]
    public void TheSixthInisNotesRollIntoOneLine()
    {
        var text = _w.Read(new[] { _w.Plain });

        Assert.Contains("filter=HcNote5.ini lists them", text);
        Assert.Contains($"note(s) in 1 other INI(s) — {ToolNames.SkypatcherLayer} filter=<INI filename> lists each one's", text);
        Assert.DoesNotContain("filter=HcNote6.ini", text);
    }

    [Fact]
    public void TheJsonOverflowCountSitsWithTheWarningsBeforeThePointers()
    {
        using var doc = JsonDocument.Parse(_w.Read(new[] { _w.Plain }, "json"));
        var member = doc.RootElement.GetProperty("skypatcher_warnings").GetString()!;

        int over = member.IndexOf("| 1 further warning(s) not listed |", StringComparison.Ordinal);
        int pointer = member.IndexOf("note(s) on lines that do not reach", StringComparison.Ordinal);
        Assert.True(over >= 0 && over < pointer, member);
    }
}

/// <summary>A deciding warning is judged on the whole line, a repeat warning past the cap counts once, and the lint's
/// incomplete-table sentence is said once (#1093).</summary>
[Trait("tier", "integration")]
public sealed class SkyPatcherLineLevelWarningTests : IDisposable
{
    const string IniName = "HcLineLevel.ini";
    readonly SkyPatcherRelevanceWorld _w;

    // :1 the bare list's missing keyword would decide, but the EditorID filter misses HcHeelBoots anyway; :2 the EditorID filter passes.
    public SkyPatcherLineLevelWarningTests() => _w = new SkyPatcherRelevanceWorld(new[]
    {
        (IniName, "filterByKeywords=HcHeelKw,HcNoSuchF:filterByEditorIdContains=Cuirass:keywordsToAdd=HcAddKw\r\n"
                  + "filterByKeywords=HcHeelKw,HcNoSuchG:filterByEditorIdContains=Heel:keywordsToAdd=HcAddKw\r\n"),
    }, otherPlugin: true);

    public void Dispose() => _w.Dispose();

    [Fact]
    public void AMissingKeywordDoesNotRideWhenAnotherFilterOnItsLineMisses()
    {
        var text = _w.Read(new[] { _w.Heel });

        Assert.DoesNotContain("HcNoSuchF", text);
        Assert.Contains($"filter={IniName} lists them", text);
    }

    [Fact]
    public void AMissingKeywordRidesWhenEveryOtherFilterOnItsLinePasses()
        => Assert.Contains("keyword 'HcNoSuchG' (in a filterByKeywords) resolves to nothing", _w.Read(new[] { _w.Heel }));

    [Fact]
    public void ARepeatedWarningPastTheCapCountsOnce()
    {
        var sink = new HousecarlCore.SkyPatcherOverlay.WarningSink();
        for (int i = 0; i <= HousecarlCore.SkyPatcherOverlay.WarningSink.Cap; i++) sink.Add("w" + i);
        for (int i = 0; i < 5; i++) sink.Add("w" + HousecarlCore.SkyPatcherOverlay.WarningSink.Cap);

        Assert.Equal(1, sink.Overflow);
    }

    // the second plugin turns unreadable after the index is built, so every keyword lookup reads an incomplete table
    [Fact]
    public void TheIncompleteTableSentenceIsSaidOnce()
    {
        _w.Read(new[] { _w.Heel });
        // same size and write time, so the index keeps the plugin and only the record sweep finds it unreadable
        var stamp = File.GetLastWriteTimeUtc(_w.OtherPath);
        File.WriteAllBytes(_w.OtherPath, new byte[new FileInfo(_w.OtherPath).Length]);
        File.SetLastWriteTimeUtc(_w.OtherPath, stamp);

        var text = SkyPatcherTools.SkyPatcherLayer(_w.Svc, filter: IniName);

        int first = text.IndexOf("EditorID table missing a plugin", StringComparison.Ordinal);
        Assert.True(first >= 0, text);
        Assert.Equal(-1, text.IndexOf("EditorID table missing a plugin", first + 1, StringComparison.Ordinal));
    }
}
