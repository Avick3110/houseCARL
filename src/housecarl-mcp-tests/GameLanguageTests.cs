using System.Text;
using System.Text.Json;
using HousecarlCore;
using HousecarlGenerator;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Streams;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Strings;
using Mutagen.Bethesda.Strings.DI;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// #1212: plugins were opened in English and Windows-1252 whatever the game's language, so a non-English install read
/// blank names and wrote English or blanks into patches. #1075: a plugin whose strings archive sits in another MO2 mod
/// folder (ARR's cleaned Creation Club masters) read every name as unresolved. Each test builds its own MO2 instance.
/// </summary>
[Trait("tier", "integration")]
public sealed class GameLanguageTests : IDisposable
{
    const string GermanName = "Großes Eisenschwert";
    const string RussianName = "Железный меч";
    const string RussianNewName = "Стальной меч";
    const string CcName = "Fishing Rod";

    static readonly Encoding Cp1251 = CodePagesEncodingProvider.Instance.GetEncoding(1251)!;
    static readonly Encoding Cp1252 = CodePagesEncodingProvider.Instance.GetEncoding(1252)!;

    readonly string _root;
    readonly List<LoadOrderService> _services = new();

    public GameLanguageTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "hc-game-language-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        foreach (var s in _services) s.Dispose();
        try { Directory.Delete(_root, true); } catch { /* temp cleanup best-effort */ }
    }

    // ---- the world --------------------------------------------------------------------------------

    string Instance => Path.Combine(_root, "inst");
    string Mods => Path.Combine(Instance, "mods");
    string DataDir => Path.Combine(_root, "game", "Data");

    string ModDir(string name)
    {
        var d = Path.Combine(Mods, name);
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>The MO2 files: <paramref name="mods"/> highest priority first, <paramref name="plugins"/> in load order, and the profile's Skyrim.ini when a language is given.</summary>
    LoadOrderService Open(string? sLanguage, string[] mods, string[] plugins, string[]? implicitMasters = null)
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(Mods);
        File.WriteAllText(Path.Combine(Instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(_root, "game").Replace(@"\", @"\\") + ")\r\n");
        var prof = Path.Combine(Instance, "profiles", "Default");
        Directory.CreateDirectory(prof);
        var masters = implicitMasters ?? Array.Empty<string>();
        File.WriteAllText(Path.Combine(prof, "loadorder.txt"),
            "# header\r\n" + string.Concat(masters.Concat(plugins).Select(n => n + "\r\n")));
        File.WriteAllText(Path.Combine(prof, "plugins.txt"), string.Concat(plugins.Select(n => "*" + n + "\r\n")));
        File.WriteAllText(Path.Combine(prof, "modlist.txt"), "# header\r\n" + string.Concat(mods.Select(m => "+" + m + "\r\n")));
        if (sLanguage is not null)
            File.WriteAllText(Path.Combine(prof, "Skyrim.ini"), $"[General]\r\nsLanguage={sLanguage}\r\n\r\n[Display]\r\n");
        var svc = LoadOrderService.WithInstance(Instance, 0, new UserConfigStore(Path.Combine(_root, "user.json")));
        _services.Add(svc);
        return svc;
    }

    /// <summary>A LOCALIZED plugin whose one weapon is named only in <paramref name="language"/>; its tables go into <c>Strings\</c> beside it, in Windows-1252 as the game's own Western tables are.</summary>
    static FormKey WriteLocalized(string path, Language language, string name, ModType type = ModType.Plugin)
    {
        var key = new ModKey(Path.GetFileNameWithoutExtension(path), type);
        var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE) { UsingLocalization = true };
        var w = mod.Weapons.AddNew();
        w.EditorID = key.Name + "Weap";
        w.Name = new TranslatedString(language, name);
        w.BasicStats = new WeaponBasicStats { Damage = 7, Weight = 1 };
        using (var sw = new StringsWriter(GameRelease.SkyrimSE, key, Path.Combine(Path.GetDirectoryName(path)!, "Strings"),
                                          new Always(new MutagenEncodingWrapper(Cp1252)), new System.IO.Abstractions.FileSystem()))
            mod.BeginWrite.ToPath(path).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).WithStringsWriter(sw).Write();
        return w.FormKey;
    }

    static string Fid(FormKey fk) => $"{fk.ID:X6}:{fk.ModKey.FileName}";
    static JsonElement Je(string json) => JsonDocument.Parse(json).RootElement.Clone();

    static string ReadName(LoadOrderService svc, FormKey fk, object? source = null) =>
        RecordsTools.Records(svc, formids: new[] { Fid(fk) }, project: new RecordsTools.RecordsProject { form = "fields", fields = new[] { "Name" } },
                             source: source is null ? null : JsonSerializer.SerializeToElement(source));

    static string Set(LoadOrderService svc, FormKey fk, string field, string value, string patch) => ApplyTools.Apply(svc,
        ops: Je($@"[{{""formid"":""{Fid(fk)}"",""field_path"":""{field}"",""op"":""Set"",""value"":{JsonSerializer.Serialize(value)}}}]"),
        patch: patch);

    string PatchPath(string patch) => Directory.GetFiles(Mods, patch, SearchOption.AllDirectories).Single();

    static bool Holds(string path, byte[] needle)
    {
        var hay = File.ReadAllBytes(path);
        for (int i = 0; i + needle.Length <= hay.Length; i++)
            if (hay.AsSpan(i, needle.Length).SequenceEqual(needle)) return true;
        return false;
    }

    // ---- #1212 ------------------------------------------------------------------------------------

    /// <summary>A plugin that ships German tables only, on a German install: the name reads in German, and a patch that
    /// overrides the record carries the German name rather than a blank one.</summary>
    [Fact]
    public void AGermanOnlyPluginReadsItsGermanNameAndAPatchCarriesIt()
    {
        var weapon = WriteLocalized(Path.Combine(ModDir("GermanMod"), "HcDe.esp"), Language.German, GermanName);
        Assert.False(File.Exists(Path.Combine(Mods, "GermanMod", "Strings", "HcDe_English.STRINGS")), "the fixture shipped English tables");
        var svc = Open("GERMAN", new[] { "GermanMod" }, new[] { "HcDe.esp" });

        var read = ReadName(svc, weapon);
        Assert.DoesNotContain("error:", read);
        Assert.Contains(GermanName, read);

        var r = Set(svc, weapon, "BasicStats.Damage", "20", "HcDePatch.esp");
        Assert.DoesNotContain("error:", r);
        Assert.True(Holds(PatchPath("HcDePatch.esp"), Cp1252.GetBytes(GermanName)), "the patch does not carry the German name");
        Assert.Contains(GermanName, ReadName(svc, weapon, source: "HcDePatch.esp"));
    }

    /// <summary>A Russian plugin is in Windows-1251. It reads as Cyrillic, and a new name written through the legacy
    /// lane goes out in 1251, not 1252 (which cannot spell it) and not UTF-8 (which the game does not read there).</summary>
    [Fact]
    public void ARussianNameWrittenThroughTheLegacyLaneComesBackInWindows1251()
    {
        var path = Path.Combine(ModDir("RussianMod"), "HcRu.esp");
        var mod = new SkyrimMod(new ModKey("HcRu", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var w = mod.Weapons.AddNew();
        w.EditorID = "HcRuWeap";
        w.Name = RussianName;
        w.BasicStats = new WeaponBasicStats { Damage = 7, Weight = 1 };
        var cp1251 = new MutagenEncodingWrapper(Cp1251);
        mod.BeginWrite.ToPath(path).WithLoadOrder(Array.Empty<ISkyrimModGetter>())
           .WithEmbeddedEncodings(new EncodingBundle(cp1251, cp1251)).Write();
        Assert.True(Holds(path, Cp1251.GetBytes(RussianName)), "the fixture is not 1251");
        var svc = Open("RUSSIAN", new[] { "RussianMod" }, new[] { "HcRu.esp" });

        Assert.Contains(RussianName, ReadName(svc, w.FormKey));

        var r = Set(svc, w.FormKey, "Name", RussianNewName, "HcRuPatch.esp");
        Assert.DoesNotContain("error:", r);
        var patch = PatchPath("HcRuPatch.esp");
        Assert.True(Holds(patch, Cp1251.GetBytes(RussianNewName)), "the new name is not in Windows-1251");
        Assert.False(Holds(patch, Encoding.UTF8.GetBytes(RussianNewName)), "the new name went out as UTF-8");
        Assert.Contains(RussianNewName, ReadName(svc, w.FormKey, source: "HcRuPatch.esp"));
    }

    /// <summary>The #740 setup: <c>sLanguage=ENGLISH</c> with UTF-8 <c>_English</c> tables. Naming the language must
    /// not take the UTF-8-first read away from it.</summary>
    [Fact]
    public void AJapaneseSetupOnEnglishTablesReadsAsBefore()
    {
        const string japanese = "炎の剣";
        var dir = ModDir("JapaneseMod");
        var key = new ModKey("HcJa", ModType.Plugin);
        var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE) { UsingLocalization = true };
        var w = mod.Weapons.AddNew();
        w.EditorID = "HcJaWeap";
        w.Name = japanese;
        w.BasicStats = new WeaponBasicStats { Damage = 7, Weight = 1 };
        using (var sw = new StringsWriter(GameRelease.SkyrimSE, key, Path.Combine(dir, "Strings"),
                                          new Always(MutagenEncoding._utf8), new System.IO.Abstractions.FileSystem()))
            mod.BeginWrite.ToPath(Path.Combine(dir, "HcJa.esp")).WithLoadOrder(Array.Empty<ISkyrimModGetter>())
               .WithStringsWriter(sw).Write();
        var svc = Open("ENGLISH", new[] { "JapaneseMod" }, new[] { "HcJa.esp" });

        var read = ReadName(svc, w.FormKey);
        Assert.DoesNotContain("error:", read);
        Assert.Contains(japanese, read);
    }

    /// <summary>A profile whose language the game has no tables for is refused in one sentence naming the language
    /// and where houseCARL looked, rather than every name reading blank.</summary>
    [Fact]
    public void AProfileWhoseLanguageHasNoTablesIsRefused()
    {
        Directory.CreateDirectory(DataDir);
        var skyrim = WriteLocalized(Path.Combine(DataDir, "Skyrim.esm"), Language.English, "Iron Sword", ModType.Master);
        var svc = Open("GERMAN", Array.Empty<string>(), Array.Empty<string>(), new[] { "Skyrim.esm" });

        var r = ReadName(svc, skyrim);
        Assert.Contains("sLanguage=GERMAN", r);
        Assert.Contains("no German string tables for Skyrim.esm", r);
        Assert.Contains(Path.Combine(DataDir, "Strings"), r);
    }

    /// <summary>One encoding for every table, spelled here rather than borrowed from the code under test.</summary>
    sealed class Always(IMutagenEncoding encoding) : IMutagenEncodingProvider
    {
        public IMutagenEncoding GetEncoding(GameRelease release, Language language) => encoding;
    }

    // ---- #1075 ------------------------------------------------------------------------------------

    /// <summary>ARR's shape: the winning copy of a Creation Club master sits in one mod folder with no strings, and the
    /// archive named for it, carrying its tables, sits in another. The read finds the tables through the archive, and
    /// the in-place shape census names the same archive.</summary>
    [Fact]
    public void APluginWhoseStringsArchiveIsInAnotherModFolderReadsItsName()
    {
        // Skyrim.esm in the game Data, so the order has a game Data folder to redirect from.
        Directory.CreateDirectory(DataDir);
        new SkyrimMod(new ModKey("Skyrim", ModType.Master), SkyrimRelease.SkyrimSE)
            .BeginWrite.ToPath(Path.Combine(DataDir, "Skyrim.esm")).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        // Build the plugin with its tables, then move the tables into an archive in the other mod folder.
        var staging = Path.Combine(_root, "staging");
        Directory.CreateDirectory(staging);
        var weapon = WriteLocalized(Path.Combine(staging, "HcCc.esm"), Language.English, CcName, ModType.Master);
        var tables = Directory.GetFiles(Path.Combine(staging, "Strings"))
            .Select(f => (Path.GetFileName(f).ToLowerInvariant(), File.ReadAllBytes(f))).ToArray();
        File.Copy(Path.Combine(staging, "HcCc.esm"), Path.Combine(ModDir("Cleaned"), "HcCc.esm"));
        File.WriteAllBytes(Path.Combine(ModDir("Fishing"), "HcCc.bsa"),
            BsaBuilder.Build(105, BsaBuilder.HasFolderNames | BsaBuilder.HasFileNames, new[] { ("strings", tables) }));
        var svc = Open(null, new[] { "Cleaned", "Fishing" }, new[] { "HcCc.esm" }, new[] { "Skyrim.esm" });

        var read = ReadName(svc, weapon);
        Assert.DoesNotContain("error:", read);
        Assert.Contains(CcName, read);
        Assert.DoesNotContain("unresolved", read);

        var shape = LocalizedStrings.Assess(Path.Combine(Mods, "Cleaned", "HcCc.esm"), DataDir);
        Assert.Equal(LocalizedShape.BsaEmbedded, shape.Shape);
        Assert.Equal("Fishing", shape.BsaModFolder);
    }
}
