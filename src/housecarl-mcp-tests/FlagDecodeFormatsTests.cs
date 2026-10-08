using System.Text.Json;
using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A flags value decodes the same way in text, json, dense, to_file rows and the conflict tree: named bits by
/// name, each unnamed bit as slotNN (biped) or bitN, and a biped field's slot numbers unless every bit is a slotNN token.
/// Values: every bit named, the issue's 1073741828 (Body plus the unnamed slot 60), slot 60 alone, and an Armor
/// MajorFlags carrying an unnamed bit.</summary>
[Trait("tier", "integration")]
public sealed class FlagDecodeFormatsTests : IClassFixture<FlagDecodeFormatsTests.World>
{
    const BipedObjectFlag AllNamed = BipedObjectFlag.Body | BipedObjectFlag.Forearms;   // slots 32 34
    const BipedObjectFlag WithSlot60 = (BipedObjectFlag)1073741828;                      // Body + slot 60
    const BipedObjectFlag LoneSlot60 = (BipedObjectFlag)1073741824;                      // slot 60 alone
    const Armor.MajorFlag WithBit8 = Armor.MajorFlag.NonPlayable | (Armor.MajorFlag)0x100;

    const string AllNamedDecode = "Body, Forearms [slots 32 34]";
    const string Slot60Decode = "1073741828 [Body | slot60 | slots 32 60]";
    const string Bit8Decode = "260 [NonPlayable | bit8]";

    public sealed class World : IDisposable
    {
        readonly string _dir;
        public LoadOrderResolver Resolver { get; }
        public LoadOrderService Svc { get; }
        public string Dir => _dir;

        public World()
        {
            Assert.False(Enum.IsDefined(typeof(BipedObjectFlag), (BipedObjectFlag)0x40000000));
            Assert.False(Enum.IsDefined(typeof(Armor.MajorFlag), (Armor.MajorFlag)0x100));
            _dir = Path.Combine(Path.GetTempPath(), "hc-flag-decode-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            var masterPath = Path.Combine(_dir, "hcFlagMaster.esp");
            var overPath = Path.Combine(_dir, "hcFlagOver.esp");

            var master = new SkyrimMod(ModKey.FromNameAndExtension("hcFlagMaster.esp"), SkyrimRelease.SkyrimSE);
            Arma(master, "hcFlagNamed", AllNamed);
            Arma(master, "hcFlagSlot60", WithSlot60);
            Arma(master, "hcFlagLoneSlot", LoneSlot60);
            Armo(master, "hcFlagMajor", WithBit8);
            var treeArma = Arma(master, "hcFlagTreeArma", WithSlot60);
            var treeArmo = Armo(master, "hcFlagTreeArmo", WithBit8);
            master.BeginWrite.ToPath(masterPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

            var over = new SkyrimMod(ModKey.FromNameAndExtension("hcFlagOver.esp"), SkyrimRelease.SkyrimSE);
            ((IArmorAddon)WriteEngine.GenericGetOrAddAsOverride(over, treeArma)).BodyTemplate!.FirstPersonFlags = AllNamed;
            ((IArmor)WriteEngine.GenericGetOrAddAsOverride(over, treeArmo)).MajorFlags = Armor.MajorFlag.NonPlayable;
            over.BeginWrite.ToPath(overPath).WithLoadOrder(new ISkyrimModGetter[] { master }).Write();

            Resolver = LoadOrderResolver.Build(new[] { masterPath, overPath });
            Svc = LoadOrderService.ForGuard(Resolver, new UserConfigStore(Path.Combine(_dir, "houseCARL.user.json")));
        }

        static ArmorAddon Arma(SkyrimMod mod, string eid, BipedObjectFlag flags)
        {
            var a = mod.ArmorAddons.AddNew();
            a.EditorID = eid;
            a.BodyTemplate = new BodyTemplate { FirstPersonFlags = flags };
            return a;
        }

        static Armor Armo(SkyrimMod mod, string eid, Armor.MajorFlag flags)
        {
            var a = mod.Armors.AddNew();
            a.EditorID = eid;
            a.MajorFlags = flags;
            return a;
        }

        public void Dispose()
        {
            Resolver.Dispose();
            try { Directory.Delete(_dir, true); } catch { /* temp cleanup best-effort */ }
        }
    }

    readonly World _w;
    public FlagDecodeFormatsTests(World w) => _w = w;

    static readonly RecordsTools.RecordsProject Slots = new() { form = "fields", fields = new[] { "BodyTemplate.FirstPersonFlags" } };
    static readonly RecordsTools.RecordsProject Major = new() { form = "fields", fields = new[] { "MajorFlags" } };

    string Read(string type, string eid, RecordsTools.RecordsProject project, string? format = null, string? toFile = null) =>
        RecordsTools.Records(_w.Svc, types: new[] { type }, where: new[] { $"editorid = {eid}" }, project: project,
                             format: format, to_file: toFile);

    static JsonElement Field(string json) =>
        JsonDocument.Parse(json).RootElement.GetProperty("matches")[0].GetProperty("fields")[0];

    static int[] SlotArray(JsonElement field) => field.GetProperty("slots").EnumerateArray().Select(e => e.GetInt32()).ToArray();

    // ---- text ----

    [Fact]
    public void Text_AllNamedBipedShowsNamesAndSlots() =>
        Assert.Contains("BodyTemplate.FirstPersonFlags = Body, Forearms   (slots 32 34)", Read("ARMA", "hcFlagNamed", Slots));

    [Fact]
    public void Text_AnUnnamedSlotKeepsTheNamedOnesAndNamesItAsASlotToken() =>
        Assert.Contains("BodyTemplate.FirstPersonFlags = 1073741828   (Body | slot60 | slots 32 60)", Read("ARMA", "hcFlagSlot60", Slots));

    [Fact]
    public void Text_ALoneUnnamedSlotIsNotDecodedTwice() =>
        Assert.Contains("BodyTemplate.FirstPersonFlags = 1073741824   (slot60)\n", Read("ARMA", "hcFlagLoneSlot", Slots).ReplaceLineEndings("\n"));

    [Fact]
    public void Text_MajorFlagsWithAnUnnamedBitNamesItAsABitToken() =>
        Assert.Contains("MajorFlags = 260   (NonPlayable | bit8)", Read("ARMO", "hcFlagMajor", Major));

    // ---- json ----

    [Fact]
    public void Json_AllNamedBipedCarriesTheSlotArray()
    {
        var f = Field(Read("ARMA", "hcFlagNamed", Slots, "json"));
        Assert.Equal("Body, Forearms", f.GetProperty("value").GetString());
        Assert.Equal(new[] { 32, 34 }, SlotArray(f));
    }

    [Fact]
    public void Json_AnUnnamedSlotKeepsTheRawValueAndDecodesBesideIt()
    {
        var f = Field(Read("ARMA", "hcFlagSlot60", Slots, "json"));
        Assert.Equal("1073741828", f.GetProperty("value").GetString());
        Assert.Equal("Body | slot60 | slots 32 60", f.GetProperty("display").GetString());
        Assert.Equal(new[] { 32, 60 }, SlotArray(f));
    }

    [Fact]
    public void Json_ALoneUnnamedSlotDecodesOnceAndKeepsTheSlotArray()
    {
        var f = Field(Read("ARMA", "hcFlagLoneSlot", Slots, "json"));
        Assert.Equal("slot60", f.GetProperty("display").GetString());
        Assert.Equal(new[] { 60 }, SlotArray(f));
    }

    [Fact]
    public void Json_MajorFlagsDecodesAndCarriesNoSlotArray()
    {
        var f = Field(Read("ARMO", "hcFlagMajor", Major, "json"));
        Assert.Equal("260", f.GetProperty("value").GetString());
        Assert.Equal("NonPlayable | bit8", f.GetProperty("display").GetString());
        Assert.False(f.TryGetProperty("slots", out _));
    }

    // ---- dense ----

    static string DenseCell(string json) =>
        JsonDocument.Parse(json).RootElement.GetProperty("rows")[0].EnumerateArray().Last().GetString()!;

    [Fact]
    public void Dense_AllNamedBipedShowsSlots() =>
        Assert.Equal("Body, Forearms   (slots 32 34)", DenseCell(Read("ARMA", "hcFlagNamed", Slots, "dense")));

    [Fact]
    public void Dense_AnUnnamedSlotDecodes() =>
        Assert.Equal("1073741828   (Body | slot60 | slots 32 60)", DenseCell(Read("ARMA", "hcFlagSlot60", Slots, "dense")));

    [Fact]
    public void Dense_ALoneUnnamedSlotDecodesOnce() =>
        Assert.Equal("1073741824   (slot60)", DenseCell(Read("ARMA", "hcFlagLoneSlot", Slots, "dense")));

    [Fact]
    public void Dense_MajorFlagsDecodes() =>
        Assert.Equal("260   (NonPlayable | bit8)", DenseCell(Read("ARMO", "hcFlagMajor", Major, "dense")));

    // ---- to_file rows ----

    JsonElement FileField(string type, string eid, RecordsTools.RecordsProject project)
    {
        var path = Path.Combine(_w.Dir, $"{eid}-{Guid.NewGuid():N}.jsonl");
        Read(type, eid, project, "json", path);
        var row = File.ReadAllLines(path)[1];   // line 1 is the manifest
        return JsonDocument.Parse(row).RootElement.GetProperty("fields")[0].Clone();
    }

    [Fact]
    public void ToFile_AllNamedBipedCarriesTheSlotArray() =>
        Assert.Equal(new[] { 32, 34 }, SlotArray(FileField("ARMA", "hcFlagNamed", Slots)));

    [Fact]
    public void ToFile_AnUnnamedSlotDecodes()
    {
        var f = FileField("ARMA", "hcFlagSlot60", Slots);
        Assert.Equal("Body | slot60 | slots 32 60", f.GetProperty("display").GetString());
        Assert.Equal(new[] { 32, 60 }, SlotArray(f));
    }

    [Fact]
    public void ToFile_ALoneUnnamedSlotDecodesOnce()
    {
        var f = FileField("ARMA", "hcFlagLoneSlot", Slots);
        Assert.Equal("slot60", f.GetProperty("display").GetString());
        Assert.Equal(new[] { 60 }, SlotArray(f));
    }

    [Fact]
    public void ToFile_MajorFlagsDecodes() =>
        Assert.Equal("NonPlayable | bit8", FileField("ARMO", "hcFlagMajor", Major).GetProperty("display").GetString());

    // ---- tree diff ----

    static readonly RecordsTools.RecordsProject TreeSlots = new() { form = "tree", fields = new[] { "BodyTemplate.FirstPersonFlags" } };
    static readonly RecordsTools.RecordsProject TreeMajor = new() { form = "tree", fields = new[] { "MajorFlags" } };

    [Fact]
    public void Tree_BothSidesOfASlotDeltaDecode()
    {
        var tree = Read("ARMA", "hcFlagTreeArma", TreeSlots);
        Assert.Contains($"BodyTemplate.FirstPersonFlags={Slot60Decode}", tree);
        Assert.Contains(AllNamedDecode, tree);
    }

    [Fact]
    public void Tree_AMajorFlagsDeltaDecodesItsUnnamedBit() =>
        Assert.Contains($"MajorFlags={Bit8Decode}", Read("ARMO", "hcFlagTreeArmo", TreeMajor));
}
