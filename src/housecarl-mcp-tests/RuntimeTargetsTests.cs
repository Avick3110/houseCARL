using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The one target resolver under each format's own spelling: a FormID written before the plugin, a load index
/// written into it, a bare runtime FormID, a plugin name as a term, an EditorID looked up among every type, and a name
/// no record holds. SkyPatcher's spelling reads none of them, which is why its answers do not move.</summary>
[Trait("tier", "unit")]
public sealed class RuntimeTargetsTests
{
    static DistributorFormat Format(string key) => RuntimeFormats.Distributor(key)!;

    static TargetSpelling Spelling(string key) => Format(key).Targets;

    static TargetSpelling Sky => RuntimeFormats.SkyPatcher.Targets;

    static readonly FormKey Lantern = FormKey.Factory("000800:Skyrim.esm");
    static readonly FormKey Elsewhere = FormKey.Factory("000800:Dawnguard.esm");
    static readonly FormKey InLight = FormKey.Factory("000800:MyLight.esl");
    static readonly FormKey InNextLight = FormKey.Factory("000801:OtherLight.esl");
    static readonly FormKey Trimmed = FormKey.Factory("01234A:Skyrim.esm");

    static SkyPatcherOverlayHarness.StubResolver Order()
    {
        var r = new SkyPatcherOverlayHarness.StubResolver();
        r.Plugins.Add("Skyrim.esm");
        r.Plugins.Add("Dawnguard.esm");
        r.RuntimeBases["Skyrim.esm"] = 0x00000000;
        r.RuntimeBases["Dawnguard.esm"] = 0x02000000;
        r.RuntimeForms[0x00000800] = Lantern;
        r.RuntimeForms[0x02000800] = Elsewhere;
        r.Winners[Lantern] = "Skyrim.esm";
        r.Winners[Elsewhere] = "Dawnguard.esm";
        // Two light plugins, the first at light index 1: its records are numbered from FE001000.
        r.Plugins.Add("MyLight.esl");
        r.RuntimeBases["MyLight.esl"] = 0xFE001000;
        r.RuntimeForms[0xFE001800] = InLight;
        r.RuntimeForms[0xFE002801] = InNextLight;
        r.Winners[InLight] = "MyLight.esl";
        r.Winners[InNextLight] = "OtherLight.esl";
        r.RuntimeForms[0x0001234A] = Trimmed;
        r.Winners[Trimmed] = "Skyrim.esm";
        return r;
    }

    // (a) Probe: the FormID is written first, which SkyPatcher's own spelling reads as a name.
    [Fact]
    public void AFormIdWrittenBeforeThePluginIsAnAddressForTheFourAndANameForSkyPatcher()
    {
        var token = "0x800~Skyrim.esm";

        var read = RuntimeTargets.Read(token, Spelling("spid"));
        Assert.Equal(RuntimeTargets.TargetKind.Address, read.Kind);
        Assert.Equal("Skyrim.esm", read.Address!.Plugin);
        Assert.Equal(0x800u, read.Address.FormId);
        Assert.Equal(Lantern, RuntimeTargets.Resolve(token, null, Order(), Spelling("spid")));

        Assert.Equal(RuntimeTargets.TargetKind.EditorId, RuntimeTargets.Read(token, Sky).Kind);
    }

    // (b) Probe: a load index written in is added to the plugin's base unmasked, so it lands on another plugin's record, and the line says so.
    [Fact]
    public void AFormIdWrittenWithALoadIndexLandsWhereTheFrameworkAddsIt()
    {
        var token = "0x2000800~Skyrim.esm";
        var read = RuntimeTargets.Read(token, Spelling("spid"));

        Assert.True(read.LoadIndexWritten(Order().RuntimeBaseOf("Skyrim.esm")!.Value));
        Assert.Equal(Elsewhere, RuntimeTargets.Resolve(token, null, Order(), Spelling("spid")));
        Assert.Contains("is written with a load index", RuntimeTargets.Report(token, null, Order(), Format("spid")));

        // SkyPatcher's own arithmetic keeps the low 24 bits, so the same number stays inside the plugin it names.
        Assert.Equal(Lantern, RuntimeTargets.Resolve("Skyrim.esm|0x2000800", null, Order(), Sky));
    }

    // (b) Probe: the index window is the plugin's own — 12 bits under a light plugin, so 0x1801 carries an index
    // there and 0x800 does not, where the same 0x1801 is an object id under a full plugin.
    [Fact]
    public void ALightPluginsIndexWindowIsTwelveBitsWide()
    {
        Assert.Equal(InNextLight, RuntimeTargets.Resolve("0x1801~MyLight.esl", null, Order(), Spelling("spid")));
        Assert.Contains("is written with a load index", RuntimeTargets.Report("0x1801~MyLight.esl", null, Order(), Format("spid")));

        Assert.Equal(InLight, RuntimeTargets.Resolve("0x800~MyLight.esl", null, Order(), Spelling("spid")));
        Assert.Null(RuntimeTargets.Report("0x800~MyLight.esl", null, Order(), Format("spid")));
        // Under a full plugin the same number is an object id, as it has always been.
        Assert.DoesNotContain("load index", RuntimeTargets.Report("0x1801~Skyrim.esm", null, Order(), Format("spid")) ?? "");
    }

    // (b) Probe: the arithmetic is the row's — put SkyPatcher's on SPID's row and the index is masked off instead.
    [Fact]
    public void TheFormIdArithmeticIsTheRows()
        => Assert.Equal(Lantern, RuntimeTargets.Resolve("0x2000800~Skyrim.esm", null, Order(),
            ShippedRows.Distributor("spid", ("\"formId\": \"runtimeAdd\"", "\"formId\": \"localObjectId\"")).Targets));

    // (a) Probe: a token packing more fields after the plugin names the plugin of its own field, not the rest of
    // the line; SkyPatcher's row, whose plugin is written first, answers as it always has.
    [Fact]
    public void AFieldPackedAfterThePluginIsNotPartOfItsName()
    {
        var read = RuntimeTargets.Read("0x800~Skyrim.esm~trailing", Spelling("spid"));
        Assert.Equal("Skyrim.esm", read.Address!.Plugin);
        Assert.Equal(Lantern, RuntimeTargets.Resolve("0x800~Skyrim.esm~trailing", null, Order(), Spelling("spid")));

        var sky = RuntimeTargets.Read("Skyrim.esm|0x800|x", Sky);
        Assert.Equal("Skyrim.esm", sky.Address!.Plugin);
        Assert.Equal(0x800u, sky.Address.FormId);
    }

    // (b) Probe: FLM's row erases the written load index of a ten-character side before the add; a shorter side
    // keeps it, and the same tokens under the rows that add raw are unchanged.
    [Fact]
    public void ATenCharacterSideLosesItsLoadIndexWhereTheRowSaysSo()
    {
        Assert.Equal(0x01234Au, RuntimeTargets.Read("0x0201234A~Skyrim.esm", Spelling("flm")).Address!.FormId);
        Assert.Equal(Trimmed, RuntimeTargets.Resolve("0x0201234A~Skyrim.esm", null, Order(), Spelling("flm")));

        Assert.Equal(0x201234Au, RuntimeTargets.Read("0x201234A~Skyrim.esm", Spelling("flm")).Address!.FormId);
        Assert.Equal(0x0201234Au, RuntimeTargets.Read("0x0201234A~Skyrim.esm", Spelling("spid")).Address!.FormId);
        Assert.Equal(0x0201234Au, RuntimeTargets.Read("0x0201234A~Skyrim.esm", Spelling("kid")).Address!.FormId);
    }

    // (c) Probe: hex with no plugin is the runtime FormID, the '0x' required; SkyPatcher reads the same token as a name.
    [Fact]
    public void BareHexIsARuntimeFormIdForTheFourAndANameForSkyPatcher()
    {
        Assert.Equal(RuntimeTargets.TargetKind.RuntimeFormId, RuntimeTargets.Read("0x800", Spelling("spid")).Kind);
        Assert.Equal(Lantern, RuntimeTargets.Resolve("0x800", null, Order(), Spelling("spid")));
        Assert.Equal(RuntimeTargets.TargetKind.EditorId, RuntimeTargets.Read("800", Spelling("spid")).Kind);
        Assert.Equal(RuntimeTargets.TargetKind.EditorId, RuntimeTargets.Read("0x800", Sky).Kind);
    }

    // (c) Probe: whitespace the frameworks' own digit test rejects is not a bare FormID, so the token is a name.
    [Theory]
    [InlineData("0x1A ")]
    [InlineData("0x 1A")]
    [InlineData("0x1A\n")]
    public void BareHexWithWhitespaceIsANameNotAFormId(string token)
    {
        Assert.False(RuntimeTargets.TryParseBareHex(token, out _));
        Assert.Equal(RuntimeTargets.TargetKind.EditorId, RuntimeTargets.Read(token, Spelling("spid")).Kind);
        Assert.True(RuntimeTargets.TryParseBareHex("0x1A", out var tight) && tight == 0x1A);
    }

    // (d) Probe: two plugin-name rules — SPID takes the extension text anywhere, KID only at the end.
    [Theory]
    [InlineData("spid", "Mod.esp", RuntimeTargets.TargetKind.PluginName)]
    [InlineData("kid", "Mod.esp", RuntimeTargets.TargetKind.PluginName)]
    [InlineData("spid", "Mod.esp-extra", RuntimeTargets.TargetKind.PluginName)]
    [InlineData("kid", "Mod.esp-extra", RuntimeTargets.TargetKind.EditorId)]
    [InlineData("spid", "Mod.ESP", RuntimeTargets.TargetKind.EditorId)]
    [InlineData("kid", "Mod.ESP", RuntimeTargets.TargetKind.EditorId)]
    public void APluginTermIsReadByItsOwnRule(string key, string token, RuntimeTargets.TargetKind kind)
        => Assert.Equal(kind, RuntimeTargets.Read(token, Spelling(key)).Kind);

    // (d) Probe: a plugin name names a file and never a record, and the line says which.
    [Fact]
    public void APluginNameNamesNoRecordAndIsReportedAsThePluginItIs()
    {
        Assert.Null(RuntimeTargets.Resolve("Skyrim.esm", null, Order(), Spelling("kid")));
        Assert.Contains("names plugin 'Skyrim.esm', not a record", RuntimeTargets.Report("Skyrim.esm", null, Order(), Format("kid")));
        Assert.Contains("is not in the active order", RuntimeTargets.Report("Absent.esp", null, Order(), Format("kid")));
    }

    // (e) Probe: the four look a name up among every record type, as the game's one map keeps it; SkyPatcher stays scoped.
    [Fact]
    public void ANameIsLookedUpAmongEveryTypeForTheFourAndWithinItsScopeForSkyPatcher()
    {
        var r = Order();
        r.EditorIds["0DVDarkElfLantern01Floral"] = Lantern;
        r.EditorIdTypes["0DVDarkElfLantern01Floral"] = "Static";

        Assert.Equal(Lantern, RuntimeTargets.Resolve("0DVDarkElfLantern01Floral", "Weapon", r, Spelling("spid")));
        Assert.Null(RuntimeTargets.Resolve("0DVDarkElfLantern01Floral", "Weapon", r, Sky));
    }

    // (f) Probe: a name no record holds is a keyword the framework creates, where SkyPatcher's row says it names nothing.
    [Fact]
    public void ANameNoRecordHoldsIsCreatedAsAKeywordWhereTheRowSaysSo()
    {
        Assert.Contains("creates a keyword of that name", RuntimeTargets.Report("ArmorHeavyFancy", null, Order(), Format("kid")));
        Assert.Equal("finds no record by that EditorID in the active order",
            RuntimeTargets.Report("ArmorHeavyFancy", null, Order(),
                ShippedRows.Distributor("kid", ("\"missingName\": \"createsKeyword\"", "\"missingName\": \"namesNothing\""))));
    }

    // (e) Probe: the gap is said of a lookup that found nothing, after it — a name a kept type holds reports as it
    // resolves, where before the lookup every name of the scope was told the game keeps none of its own.
    [Fact]
    public void AnEditorIdGapIsSaidAfterTheLookupItDescribes()
    {
        Assert.Null(RuntimeTargets.Report("KeptName", null, new GappedResolver("KeptName"), Format("spid")));
        Assert.Equal($"finds nothing: {GappedResolver.GapText}",
            RuntimeTargets.Report("OtherName", null, new GappedResolver("KeptName"), Format("spid")));
    }

    // (e) Probe: a lookup the caller never ran is said to be skipped, never reported as a miss of the active order.
    [Fact]
    public void ALookupThatNeverRanIsNotReportedAsAMissOfTheOrder()
    {
        var r = new SkippingResolver();

        Assert.Equal(SkippingResolver.SkipText, RuntimeTargets.Report("AnyName", null, r, Format("spid")));
        // A scoped row's lookup on the same resolver runs, so its miss is still the order's.
        Assert.Contains("finds no record by that EditorID", RuntimeTargets.Report("AnyName", "Weapon", r, RuntimeTargets.SkyPatcherName, Sky));
    }

    /// <summary>A resolver that runs no lookup among every record type, as a caller reading no field one lands on
    /// does not; a scoped lookup on it runs and misses. Nothing else is answered.</summary>
    sealed class SkippingResolver : SkyPatcherOverlay.IFormResolver
    {
        public const string SkipText = "is looked up among every record type, which this listing does not walk, so whether the active order holds a record of that name is not read here";

        public string? EditorIdNotLookedUp(string? mutagenType) => mutagenType == RuntimeTargets.EveryTypeScope ? SkipText : null;
        public FormKey? ResolveEditorId(string editorId, string? mutagenType) => null;
        public string? ReadWinnerLeaf(FormKey donor, string path) => null;
        public IReadOnlyList<FormKey>? KeywordsOf(FormKey record) => null;
        public bool PluginPresent(string pluginName) => false;
        public (FormKey? Record, string? Why) FromRuntimeFormId(uint value) => (null, "names nothing");
        public uint? RuntimeBaseOf(string pluginName) => null;
        public string? WinnerPluginOf(FormKey record) => null;
        public string? EditorIdOf(FormKey record) => null;
    }

    /// <summary>A resolver whose scope has a gap AND holds some of the names in it — po3 Tweaks missing where the
    /// game still keeps the EditorIDs of some types of an every-type scope. Nothing else is answered.</summary>
    sealed class GappedResolver(string holds) : SkyPatcherOverlay.IFormResolver
    {
        public const string GapText = "po3 Tweaks is not installed, so of the record types of the SkyrimMajorRecord group the game keeps EditorIDs only of Spell records";

        public string? EditorIdGap(string? mutagenType) => GapText;
        public FormKey? ResolveEditorId(string editorId, string? mutagenType) => editorId == holds ? Lantern : null;
        public string? ReadWinnerLeaf(FormKey donor, string path) => null;
        public IReadOnlyList<FormKey>? KeywordsOf(FormKey record) => null;
        public bool PluginPresent(string pluginName) => false;
        public (FormKey? Record, string? Why) FromRuntimeFormId(uint value) => (null, "names nothing");
        public uint? RuntimeBaseOf(string pluginName) => null;
        public string? WinnerPluginOf(FormKey record) => null;
        public string? EditorIdOf(FormKey record) => null;
    }

    // Probe: a rule a row has not read is said to be unread, never guessed as a name.
    [Theory]
    [InlineData("bos")]
    [InlineData("flm")]
    public void ATokenUnderARowWithAnUnreadRuleIsUnread(string key)
    {
        Assert.Equal(RuntimeTargets.TargetKind.Unread, RuntimeTargets.Read("0x800", Spelling(key)).Kind);
        Assert.Null(RuntimeTargets.Resolve("0x800", null, Order(), Spelling(key)));
        Assert.Contains("is not read yet", RuntimeTargets.Report("0x800", null, Order(), Format(key)));
    }

    // Probe: each unread rule alone makes a token unread — one arm per field.
    [Theory]
    [InlineData("\"bareFormId\": \"runtimeFormId\"", "\"bareFormId\": \"unread\"")]
    [InlineData("\"pluginTerm\": \"containsExtensionText\"", "\"pluginTerm\": \"unread\"")]
    [InlineData("\"missingName\": \"createsKeyword\"", "\"missingName\": \"unread\"")]
    [InlineData("\"editorIds\": \"everyType\"", "\"editorIds\": \"unread\"")]
    public void OneUnreadRuleAloneMakesATokenUnread(string from, string to)
    {
        var row = ShippedRows.Distributor("spid", (from, to));
        Assert.Equal(RuntimeTargets.TargetKind.Unread, RuntimeTargets.Read("AnyName", row.Targets).Kind);
        // The address half is read either way: it is sourced for all four.
        Assert.Equal(RuntimeTargets.TargetKind.Address, RuntimeTargets.Read("0x800~Skyrim.esm", row.Targets).Kind);
    }

    // Probe: SkyPatcher's own resolve door reads the row its report reads — put another row's arithmetic and
    // EditorID scope on a copy and the record it names follows, where it used to be written into the door.
    [Fact]
    public void SkyPatchersOwnResolveFollowsTheRowItsReportFollows()
    {
        var edited = Sky with { FormId = TargetFormIdMath.RuntimeAdd, EditorIds = TargetEditorIdScope.EveryType };
        var address = Value("Skyrim.esm|0x2000800");
        var r = Order();
        r.EditorIds["0DVDarkElfLantern01Floral"] = Lantern;
        r.EditorIdTypes["0DVDarkElfLantern01Floral"] = "Static";
        var name = Value("0DVDarkElfLantern01Floral");

        // Today's row masks the index off and keeps the call's scope. (A bare null would read as the scopes overload.)
        Assert.Equal(Lantern, RuntimeTargets.Resolve(address, (string?)null, r));
        Assert.Null(RuntimeTargets.Resolve(name, "Weapon", r));
        // The copy adds the index in and looks the name up among every type, as the four distributor rows do.
        Assert.Equal(Elsewhere, RuntimeTargets.Resolve(address, (string?)null, r, edited));
        Assert.Equal(Lantern, RuntimeTargets.Resolve(name, "Weapon", r, edited));
    }

    static SkyPatcherValue Value(string raw)
        => new(raw, IsNameLiteral: false, NameText: null, SubArgs: new[] { raw }, Address: SkyPatcherParse.TryParseAddress(raw));

    // Probe: SkyPatcher's own spelling comes off its row — change the address character and its parse follows.
    [Fact]
    public void SkyPatchersAddressCharacterIsItsRows()
    {
        Assert.Equal("Skyrim.esm", SkyPatcherParse.TryParseAddress("Skyrim.esm|0x800")!.Plugin);
        Assert.Null(SkyPatcherParse.TryParseAddress("Skyrim.esm/0x800"));
        Assert.Equal("Skyrim.esm", SkyPatcherParse.TryParseAddress("Skyrim.esm/0x800",
            RuntimeFormats.SkyPatcher.Grammar with { Address = '/' })!.Plugin);
    }

    // Probe: a plugin the order does not have names nothing, whatever the arithmetic.
    [Fact]
    public void AnAddressNamingAnAbsentPluginNamesNothing()
    {
        Assert.Null(RuntimeTargets.Resolve("0x800~Absent.esp", null, Order(), Spelling("spid")));
        Assert.Contains("which is not in the active order", RuntimeTargets.Report("0x800~Absent.esp", null, Order(), Format("spid")));
    }

    // Probe: the side the FormID digits were looked for on is the row's address order, not one format's.
    [Fact]
    public void AMalformedAddressNamesTheSideItsOwnRowWritesTheFormIdOn()
    {
        Assert.Contains("no hex digit before '~'", RuntimeTargets.Report("junk~Skyrim.esm", null, Order(), Format("spid")));
        // SkyPatcher writes the plugin first, so its own sentence is unchanged.
        Assert.Contains("no hex digit after '|'",
            RuntimeTargets.Report("Skyrim.esm|junk", null, Order(), RuntimeTargets.SkyPatcherName, Sky));
    }

    // Probe: an address the order has a plugin for but no record at names nothing, with the runtime FormID in the sentence.
    [Fact]
    public void AnAddressNoPluginDefinesNamesNothing()
    {
        Assert.Null(RuntimeTargets.Resolve("0x900~Skyrim.esm", null, Order(), Spelling("spid")));
        Assert.Contains("which no plugin in the active order defines", RuntimeTargets.Report("0x900~Skyrim.esm", null, Order(), Format("spid")));
    }
}
