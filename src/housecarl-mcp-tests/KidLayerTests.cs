using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>housecarl_layer reading KID: the winning files in KID's order, the lines expanded under filter= with what KID
/// does with each, formids= / plugins= selecting the lines naming a form, labelled target, filter or value, and KID's
/// part joined after SkyPatcher's and SPID's. The order is <see cref="SpidWorld"/>'s: HcArmorSrc.esp defines the armour
/// HcBoots, HcSpid.esp the keyword HcKeyword among others.</summary>
[Trait("tier", "integration")]
public sealed class KidLayerTests : IDisposable
{
    const string Boots = "000800:HcArmorSrc.esp", Keyword = "000814:HcSpid.esp";

    readonly SpidWorld _w = new(new[]
    {
        ("HcA_KID.ini",
            "; the GVLT shape: a created keyword onto an armour named by EditorID\r\n"
            + "Keyword = HcVisor|Armor|HcBoots\r\n"                              // :2
            + "Keyword = 0x814~HcSpid.esp|Armor|HcKeyword,-HcOther.esp\r\n"      // :3 a keyword by FormID, a keyword filter, a plugin term
            + "Keyword = HcSkull|Armor|HcAbsent.esp\r\n"                         // :4 dropped (BOOBIES_RotolMoreIcons_KID.ini:3)
            + "Keyword = HcTag|FormList|HcBoots\r\n"),                           // :5 dropped without a log
        ("HcB_KID.ini", "Keyword = HcVisor|Weapon\r\n"),
    }, shadowed: new[] { ("HcB_KID.ini", "Keyword = HcLoserLine|Armor\r\n") });

    public void Dispose() => _w.Dispose();

    static string Kid(SpidWorld w, string[]? formids = null, string[]? plugins = null, string? filter = null, int maxChars = 0)
        => SkyPatcherTools.Layer(w.Svc, distributor: new[] { "kid" }, filter: filter, formids: formids, max_chars: maxChars,
            plugins: plugins is null ? null : new SkyPatcherTools.LayerScope { names = plugins });

    // The proof shape (plan step 28; dev GVLT_LifeswornVestige_KID.ini:1): plugins= lists a line naming a plugin's record
    // by EditorID, labelled target, with the keyword it creates said.
    [Fact]
    public void PluginsListsALineNamingItsRecordByEditorId()
    {
        var text = Kid(_w, plugins: new[] { SpidWorld.ArmorSrc });
        Assert.Contains("  - HcA_KID.ini  (file 1 of 2, 3 line(s)) ← HcSpidIni\n      :2  Keyword = HcVisor|Armor|HcBoots\n"
                        + "          [!] keyword 'HcVisor' is no keyword's EditorID, so KID creates one keyword of that name for all the KID lines naming it, in any case, and this line hands it out\n"
                        + "          ↳ target filters: HcBoots → " + Boots, text);
        Assert.DoesNotContain(":5  ", text);
    }

    // Pick KID Q1 (a) and (12)(iii): the keyword is the value (dev Footprints_KID.ini:2), a keyword filter a filter, and a
    // plugin term touches its plugin under plugins= but names no form, so formids= never selects it.
    [Fact]
    public void TheKeywordIsTheValueAndAPluginTermTouchesOnlyUnderPlugins()
    {
        var text = Kid(_w, formids: new[] { Keyword });
        Assert.Contains(":3  Keyword = 0x814~HcSpid.esp|Armor|HcKeyword,-HcOther.esp\n          ↳ value keyword: 0x814~HcSpid.esp → " + Keyword
                        + "\n          ↳ filter filters: HcKeyword → " + Keyword, text);
        Assert.Contains(":3  Keyword = 0x814~HcSpid.esp|Armor|HcKeyword,-HcOther.esp\n          ↳ filter filters: HcOther.esp (the plugin, selecting the items it defines)",
            Kid(_w, plugins: new[] { SpidWorld.Other }));
        Assert.Contains("0 line(s) in 0 of 2 file(s)", Kid(_w, formids: new[] { "000800:HcOther.esp" }));
    }

    // r1 #18: with formids= and plugins= both given, a plugin term names no form, so it selects no line.
    [Fact]
    public void BothSelectorsGivenLeaveAPluginTermOut()
        => Assert.DoesNotContain("(the plugin, selecting the items it defines)", Kid(_w, formids: new[] { Keyword }, plugins: new[] { SpidWorld.Other }));

    // R41 and answer 5: under filter= a line's outcome by section is said: a filter naming a plugin the order lacks drops
    // the line, a type KID does not take drops it without a log.
    [Fact]
    public void FilterExpandsALineWithWhatKidDoes()
    {
        var text = Kid(_w, filter: "HcA_");
        Assert.Contains(":4  Keyword = HcSkull|Armor|HcAbsent.esp\n          [!] KID drops this line: filters 'HcAbsent.esp' names plugin 'HcAbsent.esp', which is not in the active order;", text);
        Assert.Contains(":5  Keyword = HcTag|FormList|HcBoots\n          [!] KID does not apply this line: it has type 'FormList', which is not one of KID's types", text);
        Assert.Contains("2 file(s) in Data (2 read), 4 line(s): Armor 3, Weapon 1; 1 more line(s) KID drops or ignores as it parses the file, before it reads any form (no '=', under a named section, a type or entry KID cannot parse, or a group with no members)", text);
    }

    // Pick Q3 (a): a selector naming a plugin the order does not carry answers for KID as for SPID: plugins= is refused
    // by the shared door, and formids= finds no line, though :4 names the plugin as a term (#1409 holds the sentence).
    [Fact]
    public void ASelectorNamingAPluginTheOrderLacksAnswersAsForSpid()
    {
        Assert.StartsWith("error: plugins= names nothing the load order carries", Kid(_w, plugins: new[] { "HcAbsent.esp" }));
        Assert.Contains("0 line(s) in 0 of 2 file(s).\n\nnothing matched: no KID line names such a form", Kid(_w, formids: new[] { "000800:HcAbsent.esp" }));
    }

    // R40: the shadowed copy is named and never read; an archive-only copy is listed, said not applied and not read.
    [Fact]
    public void AShadowedCopyIsNamedAndNeverReadAndAnArchiveOnlyOneIsNotApplied()
    {
        var text = Kid(_w, filter: "HcB_");
        Assert.Contains("[!] shadows same-path copies from HcSpidLoser, never read", text);
        Assert.DoesNotContain("HcLoserLine", text);
        using var w = new SpidWorld(new[] { ("HcA_KID.ini", "Keyword = HcVisor|Armor\r\n") }, archive:
            HousecarlGenerator.BsaBuilder.Build(105, HousecarlGenerator.BsaBuilder.HasFolderNames | HousecarlGenerator.BsaBuilder.HasFileNames,
                new[] { ("", new[] { ("hcpacked_KID.ini", System.Text.Encoding.ASCII.GetBytes("Keyword = HcVisor|Armor\r\n")) }) }));
        var packed = Kid(w, filter: "packed");
        Assert.Contains("2 file(s) in Data (1 read), 1 line(s)", packed);
        Assert.Contains("  - hcpacked_KID.ini  (file 2 of 2, 0 line(s)) ← HcSpid.bsa  [!] NOT applied: present only inside a BSA", packed);
    }

    // Pick "Drop it" (no parse cache) and R15: nothing is held between calls, so an edited file is read anew.
    [Fact]
    public void AnEditedFileIsReadAnewOnTheNextCall()
    {
        Assert.Contains("2 file(s) in Data (2 read), 4 line(s)", Kid(_w));
        File.AppendAllText(Path.Combine(_w.IniMod, "HcB_KID.ini"), "Keyword = HcVisor|Book\r\n");
        Assert.Contains("2 file(s) in Data (2 read), 5 line(s)", Kid(_w));
    }

    // Pick "(c), then measure ARR" (DECISIONS 2026-11-14 (20)) and R41: one EditorID table serves every format in one
    // call, so KID's lookups after SPID's walk add no type walked; KID alone pays the walk.
    [Fact]
    public void KidReadAfterSpidAddsNoEditorIdWalk()
    {
        using var w = new SpidWorld(new[] { ("HcA_DISTR.ini", "Perk = HcPerk\r\n"), ("HcA_KID.ini", "Keyword = HcVisor|Armor|HcBoots\r\n") });
        w.Layer();
        long Walked(Action call)
        {
            var before = w.Svc.Counters.EditorIdTypesWalked;
            call();
            return w.Svc.Counters.EditorIdTypesWalked - before;
        }
        var spid = Walked(() => SkyPatcherTools.Layer(w.Svc, distributor: new[] { "spid" }, formids: new[] { Boots }));
        Assert.True(spid > 0);
        Assert.Equal(spid, Walked(() => SkyPatcherTools.Layer(w.Svc, distributor: new[] { "spid", "kid" }, formids: new[] { Boots })));
        Assert.Equal(spid, Walked(() => Kid(w, formids: new[] { Boots })));
    }

    // r1 #25 for KID: a filter= matching no KID file resolves no KID line, so KID walks no EditorID table.
    [Fact]
    public void AFilterMatchingNoKidFileWalksNoEditorIdTable()
    {
        Kid(_w);
        var before = _w.Svc.Counters.EditorIdSweeps;
        Assert.Contains("nothing matched: no KID file name or providing mod contains 'npc'.", Kid(_w, filter: "npc"));
        Assert.Equal(0, _w.Svc.Counters.EditorIdSweeps - before);
    }

    // Pick Q2 "(a) Format order" (DECISIONS 2026-11-14 (37)): with distributor omitted and a filter= the earlier parts
    // fill, SPID takes what SkyPatcher leaves as on main and KID what SPID leaves; KID's part states its cut naming
    // distributor=["kid"], and the over-cap sentence names KID's call where KID is what did not fit.
    [Fact]
    public void KidTakesWhatSpidLeavesAndStatesItsCut()
    {
        using var w = new SpidWorld(new[]
        {
            ("HcShared_DISTR.ini", string.Concat(Enumerable.Range(1, 600).Select(i => $"Perk = HcPerk|HcAranea{i}\r\n"))),
            ("HcShared_KID.ini", "Keyword = HcVisor|Armor|HcBoots\r\n"),
        });
        // SPID's lines fill what SkyPatcher's part leaves at the default max_chars, as on main.
        var two = SkyPatcherTools.Layer(w.Svc, distributor: new[] { "skypatcher", "spid" }, filter: "HcShared");
        Assert.EndsWith("      ... [SPID lines cut at max_chars after SkyPatcher's part — distributor=[\"spid\"] reads SPID alone at the whole max_chars]", two);
        var three = SkyPatcherTools.Layer(w.Svc, distributor: new[] { "skypatcher", "spid", "kid" }, filter: "HcShared");
        const string Cut = "  ... [KID files cut at max_chars after SkyPatcher's and SPID's parts — distributor=[\"kid\"] reads KID alone at the whole max_chars]";
        Assert.StartsWith(two + "\n\nKID layer (", three);
        Assert.Contains("\n" + Cut + "\n[!] this response runs past the max_chars=40000 it was given: SkyPatcher's part comes first", three);
        Assert.EndsWith(", then SPID's part (" + (two.Length - two.IndexOf("SPID layer (", StringComparison.Ordinal)) + " chars), and KID's part does not fit in "
                        + "what is left; distributor=[\"kid\"] reads KID alone at the whole max_chars.", three);
        // With room, KID's lines follow SPID's whole part.
        var roomy = SkyPatcherTools.Layer(w.Svc, filter: "HcShared", max_chars: 400000);
        Assert.StartsWith(SkyPatcherTools.Layer(w.Svc, distributor: new[] { "skypatcher", "spid" }, filter: "HcShared", max_chars: 400000) + "\n\nKID layer (", roomy);
        Assert.Contains(":1  Keyword = HcVisor|Armor|HcBoots", roomy);
    }
}
