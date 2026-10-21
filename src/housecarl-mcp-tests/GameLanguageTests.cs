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
[Collection(MyGamesIsolation.Serial)]
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

    string Profile => Path.Combine(Instance, "profiles", "Default");

    /// <summary>The MO2 files: <paramref name="mods"/> highest priority first, <paramref name="plugins"/> in load order, and the profile's Skyrim.ini when a language is given.</summary>
    LoadOrderService Open(string? sLanguage, string[] mods, string[] plugins, string[]? implicitMasters = null, string? archives = null,
                          string? instance = null)
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
            File.WriteAllText(Path.Combine(prof, "Skyrim.ini"), $"[General]\r\nsLanguage={sLanguage}\r\n\r\n[Display]\r\n"
                + (archives is null ? "" : $"\r\n[Archive]\r\nsResourceArchiveList={archives}\r\n"));
        var svc = LoadOrderService.WithInstance(instance ?? Instance, 0, new UserConfigStore(Path.Combine(_root, "user.json")));
        _services.Add(svc);
        return svc;
    }

    /// <summary>A LOCALIZED plugin whose one weapon is named only in <paramref name="language"/>; its tables go into <c>Strings\</c> beside it, in Windows-1252 as the game's own Western tables are.</summary>
    static FormKey WriteLocalized(string path, Language language, string name, ModType type = ModType.Plugin, string? english = null)
    {
        var key = new ModKey(Path.GetFileNameWithoutExtension(path), type);
        var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE) { UsingLocalization = true };
        var w = mod.Weapons.AddNew();
        w.EditorID = key.Name + "Weap";
        w.Name = new TranslatedString(language, name);
        if (english is not null) w.Name.Set(Language.English, english);
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

        // A new patch that touches another field keeps the untouched Russian name in 1251.
        Assert.DoesNotContain("error:", Set(svc, w.FormKey, "BasicStats.Damage", "20", "HcRuDamage.esp"));
        Assert.True(Holds(PatchPath("HcRuDamage.esp"), Cp1251.GetBytes(RussianName)), "the untouched name left Windows-1251");
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
        Assert.Contains("Skyrim.esm's German .STRINGS/.DLSTRINGS/.ILSTRINGS string tables were not found loose in a Strings folder or in an archive the game loads", r);
        // The archive list that would have named the vanilla archives could not be read, and the refusal says where it looked.
        Assert.Contains("Also, could not read the [Archive] sResourceArchiveList from SkyrimCustom.ini or Skyrim.ini (looked in the profile folder)", r);
    }

    /// <summary>Skyrim.esm needs all three of its tables in the language: one kind missing refuses, naming it.</summary>
    [Fact]
    public void ASkyrimWithOneTableKindMissingIsRefused()
    {
        Directory.CreateDirectory(DataDir);
        var skyrim = WriteLocalized(Path.Combine(DataDir, "Skyrim.esm"), Language.German, GermanName, ModType.Master);
        File.Delete(Path.Combine(DataDir, "Strings", "Skyrim_German.DLSTRINGS"));
        var svc = Open("GERMAN", Array.Empty<string>(), Array.Empty<string>(), new[] { "Skyrim.esm" });

        var r = ReadName(svc, skyrim);
        Assert.StartsWith("error: houseCARL cannot read this load order in German", r);
        Assert.Contains("Skyrim.esm's German .DLSTRINGS string tables were not found", r);
    }

    /// <summary>A German order whose plugin's tables come only from a translation mod's loose Strings: the census
    /// finds them where the read does, so a compact goes ahead instead of refusing tables the read uses.</summary>
    [Fact]
    public void AGermanOrderWithTablesOnlyInATranslationModPassesTheCensus()
    {
        var staging = Path.Combine(_root, "staging");
        Directory.CreateDirectory(staging);
        WriteLocalized(Path.Combine(staging, "HcTm.esp"), Language.German, GermanName);
        File.Copy(Path.Combine(staging, "HcTm.esp"), Path.Combine(ModDir("SwordMod"), "HcTm.esp"));
        var strings = Path.Combine(ModDir("SwordMod German"), "Strings");
        Directory.CreateDirectory(strings);
        foreach (var f in Directory.GetFiles(Path.Combine(staging, "Strings"))) File.Copy(f, Path.Combine(strings, Path.GetFileName(f)));
        var svc = Open("GERMAN", new[] { "SwordMod German", "SwordMod" }, new[] { "HcTm.esp" });

        var r = WriteTools.CompactPlugin(svc, "HcTm.esp");
        Assert.DoesNotContain("error:", r);
        Assert.DoesNotContain("refused", r);
    }

    /// <summary>The same refusal reached by a profile switch that keeps the plugin order: the next call answers with
    /// the one sentence, not a plugin "held open by another program".</summary>
    [Fact]
    public void ALanguageChangeWithTheSameOrderRefusesInOneSentence()
    {
        Directory.CreateDirectory(DataDir);
        var skyrim = WriteLocalized(Path.Combine(DataDir, "Skyrim.esm"), Language.English, "Iron Sword", ModType.Master);
        var svc = Open("ENGLISH", Array.Empty<string>(), Array.Empty<string>(), new[] { "Skyrim.esm" });
        Assert.Contains("Iron Sword", ReadName(svc, skyrim));

        File.WriteAllText(Path.Combine(Profile, "Skyrim.ini"), "[General]\r\nsLanguage=GERMAN\r\n");
        File.AppendAllText(Path.Combine(Profile, "modlist.txt"), "\r\n");   // a profile touch that keeps the order

        var r = ReadName(svc, skyrim);
        Assert.StartsWith("error: houseCARL cannot read this load order in German", r);
        Assert.DoesNotContain("held open", r);
        // The baseline did not advance past the refusal, so the next call refuses too rather than reading English.
        Assert.StartsWith("error: houseCARL cannot read this load order in German", ReadName(svc, skyrim));
    }

    /// <summary>The same refusal reached by a profile change that also changes the plugin order: every call refuses,
    /// and the old build is not served in English.</summary>
    [Fact]
    public void ALanguageChangeWithANewOrderRefusesOnEveryCall()
    {
        Directory.CreateDirectory(DataDir);
        var skyrim = WriteLocalized(Path.Combine(DataDir, "Skyrim.esm"), Language.English, "Iron Sword", ModType.Master);
        new SkyrimMod(new ModKey("HcAdd", ModType.Plugin), SkyrimRelease.SkyrimSE)
            .BeginWrite.ToPath(Path.Combine(ModDir("AddMod"), "HcAdd.esp")).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        var svc = Open("ENGLISH", Array.Empty<string>(), Array.Empty<string>(), new[] { "Skyrim.esm" });
        Assert.Contains("Iron Sword", ReadName(svc, skyrim));

        File.WriteAllText(Path.Combine(Profile, "Skyrim.ini"), "[General]\r\nsLanguage=GERMAN\r\n");
        File.WriteAllText(Path.Combine(Profile, "modlist.txt"), "# header\r\n+AddMod\r\n");
        File.WriteAllText(Path.Combine(Profile, "loadorder.txt"), "# header\r\nSkyrim.esm\r\nHcAdd.esp\r\n");
        File.WriteAllText(Path.Combine(Profile, "plugins.txt"), "*HcAdd.esp\r\n");

        Assert.StartsWith("error: houseCARL cannot read this load order in German", ReadName(svc, skyrim));
        Assert.StartsWith("error: houseCARL cannot read this load order in German", ReadName(svc, skyrim));
    }

    /// <summary>A localized Skyrim.esm whose German tables ship in a Data archive the INI loads, as a real German
    /// install's do: the order reads, in German, with no refusal.</summary>
    [Fact]
    public void AGermanInstallWithSkyrimTablesInAnArchiveReads()
    {
        var staging = Path.Combine(_root, "staging");
        Directory.CreateDirectory(staging);
        var weapon = WriteLocalized(Path.Combine(staging, "Skyrim.esm"), Language.German, GermanName, ModType.Master);
        Directory.CreateDirectory(DataDir);
        File.Copy(Path.Combine(staging, "Skyrim.esm"), Path.Combine(DataDir, "Skyrim.esm"));
        File.WriteAllBytes(Path.Combine(DataDir, "Skyrim - Interface.bsa"), Archive(staging));
        var svc = Open("GERMAN", Array.Empty<string>(), Array.Empty<string>(), new[] { "Skyrim.esm" }, archives: "Skyrim - Interface.bsa");

        var read = ReadName(svc, weapon);
        Assert.DoesNotContain("error:", read);
        Assert.Contains(GermanName, read);
    }

    /// <summary>English and German tables both ship: a German install reads and carries the German name, not the English one.</summary>
    [Fact]
    public void WithEnglishTablesAlsoPresentAGermanInstallReadsGerman()
    {
        var weapon = WriteLocalized(Path.Combine(ModDir("BothMod"), "HcBoth.esp"), Language.German, GermanName, english: "Big Iron Sword");
        Assert.True(File.Exists(Path.Combine(Mods, "BothMod", "Strings", "HcBoth_English.STRINGS")), "the fixture has no English tables");
        var svc = Open("GERMAN", new[] { "BothMod" }, new[] { "HcBoth.esp" });

        var read = ReadName(svc, weapon);
        Assert.Contains(GermanName, read);
        Assert.DoesNotContain("Big Iron Sword", read);

        Assert.DoesNotContain("error:", Set(svc, weapon, "BasicStats.Damage", "20", "HcBothPatch.esp"));
        var patch = PatchPath("HcBothPatch.esp");
        Assert.True(Holds(patch, Cp1252.GetBytes(GermanName)), "the patch does not carry the German name");
        Assert.False(Holds(patch, Cp1252.GetBytes("Big Iron Sword")), "the patch carries the English name");
    }

    /// <summary>A mod shipping only English tables, on a German install: the game shows no German name, and neither does houseCARL.</summary>
    [Fact]
    public void AnEnglishOnlyModOnAGermanInstallDoesNotReadEnglish()
    {
        var weapon = WriteLocalized(Path.Combine(ModDir("EnglishMod"), "HcEn.esp"), Language.English, "Big Iron Sword");
        var svc = Open("GERMAN", new[] { "EnglishMod" }, new[] { "HcEn.esp" });

        var read = ReadName(svc, weapon);
        Assert.DoesNotContain("error:", read);
        Assert.Contains("HcEnWeap", read);
        Assert.DoesNotContain("Big Iron Sword", read);

        // The census agrees: no German text found, so a compact refuses, naming the language it looked for.
        var r = WriteTools.CompactPlugin(svc, "HcEn.esp");
        Assert.Contains(".STRINGS files beside it in English only, none in German, the game's language", r);
        Assert.Contains("Put this plugin's German .STRINGS where houseCARL can see them", r);
    }

    /// <summary>sLanguage that names no game language, or a number, refuses in one sentence naming the INI and the value.</summary>
    [Theory]
    [InlineData("KLINGON")]
    [InlineData("1")]
    public void AnSLanguageThatNamesNoLanguageIsRefused(string value)
    {
        var weapon = WriteLocalized(Path.Combine(ModDir("GermanMod"), "HcDe.esp"), Language.German, GermanName);
        var svc = Open(value, new[] { "GermanMod" }, new[] { "HcDe.esp" });

        var r = ReadName(svc, weapon);
        Assert.Contains($"sets sLanguage={value}, which is not a game language it knows", r);
        Assert.Contains(Path.Combine(Profile, "Skyrim.ini"), r);
    }

    /// <summary>JAPANESE with UTF-8 <c>_Japanese</c> tables: the #740 UTF-8-first read holds under a non-English language too.</summary>
    [Fact]
    public void AJapaneseInstallReadsUtf8JapaneseTables()
    {
        const string japanese = "炎の剣";
        var dir = ModDir("JapaneseMod");
        var key = new ModKey("HcJp", ModType.Plugin);
        var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE) { UsingLocalization = true };
        var w = mod.Weapons.AddNew();
        w.EditorID = "HcJpWeap";
        w.Name = new TranslatedString(Language.Japanese, japanese);
        w.BasicStats = new WeaponBasicStats { Damage = 7, Weight = 1 };
        using (var sw = new StringsWriter(GameRelease.SkyrimSE, key, Path.Combine(dir, "Strings"),
                                          new Always(new MutagenEncodingWrapper(Encoding.UTF8)), new System.IO.Abstractions.FileSystem()))
            mod.BeginWrite.ToPath(Path.Combine(dir, "HcJp.esp")).WithLoadOrder(Array.Empty<ISkyrimModGetter>())
               .WithStringsWriter(sw).Write();
        var svc = Open("JAPANESE", new[] { "JapaneseMod" }, new[] { "HcJp.esp" });

        Assert.Contains(japanese, ReadName(svc, w.FormKey));
    }

    /// <summary>A Korean install's legacy lane is Windows-1252, which decodes every byte: a plugin whose name is not
    /// UTF-8 (a Western mod's "é") reads as written, and a new patch that touches another field writes the untouched
    /// name back byte for byte rather than as a replacement character.</summary>
    [Fact]
    public void AKoreanInstallRoundTripsAPluginWithANonUtf8Name()
    {
        var path = Path.Combine(ModDir("WesternMod"), "HcWe.esp");
        var mod = new SkyrimMod(new ModKey("HcWe", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var w = mod.Weapons.AddNew();
        w.EditorID = "HcWeWeap";
        w.Name = "Epée longue";
        w.BasicStats = new WeaponBasicStats { Damage = 7, Weight = 1 };
        var cp1252 = new MutagenEncodingWrapper(Cp1252);
        mod.BeginWrite.ToPath(path).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).WithEmbeddedEncodings(new EncodingBundle(cp1252, cp1252)).Write();
        var svc = Open("KOREAN", new[] { "WesternMod" }, new[] { "HcWe.esp" });

        var read = ReadName(svc, w.FormKey);
        Assert.DoesNotContain("error:", read);
        Assert.Contains("Epée longue", read);

        Assert.DoesNotContain("error:", Set(svc, w.FormKey, "BasicStats.Damage", "20", "HcWePatch.esp"));
        var patch = PatchPath("HcWePatch.esp");
        Assert.True(Holds(patch, Cp1252.GetBytes("Epée longue")), "the untouched name did not round-trip");
        Assert.False(Holds(patch, new byte[] { 0xEF, 0xBF, 0xBD }), "the untouched name was written as a replacement character");
    }

    /// <summary>A plugin's lane is its own bytes': a Russian master whose Russian tables are UTF-8 but whose own text is
    /// not leaves a new patch over it in Windows-1251, the code page the game reads that patch's text in.</summary>
    [Fact]
    public void ARussianPatchOverAMasterWithUtf8TablesTakesTheMastersOwnLane()
    {
        var key = new ModKey("HcRm", ModType.Plugin);
        var dir = ModDir("RussianMaster");
        var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE) { UsingLocalization = true };
        var w = mod.Weapons.AddNew();
        w.EditorID = "HcRmWeap";
        w.Name = new TranslatedString(Language.Russian, RussianName);
        w.BasicStats = new WeaponBasicStats { Damage = 7, Weight = 1 };
        using (var sw = new StringsWriter(GameRelease.SkyrimSE, key, Path.Combine(dir, "Strings"),
                                          new Always(new MutagenEncodingWrapper(Encoding.UTF8)), new System.IO.Abstractions.FileSystem()))
            mod.BeginWrite.ToPath(Path.Combine(dir, "HcRm.esp")).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).WithStringsWriter(sw).Write();
        var svc = Open("RUSSIAN", new[] { "RussianMaster" }, new[] { "HcRm.esp" });
        Assert.Contains(RussianName, ReadName(svc, w.FormKey));

        Assert.DoesNotContain("error:", Set(svc, w.FormKey, "Name", RussianNewName, "HcRmPatch.esp"));
        var patch = PatchPath("HcRmPatch.esp");
        Assert.True(Holds(patch, Cp1251.GetBytes(RussianNewName)), "the new name is not in Windows-1251");
        Assert.False(Holds(patch, Encoding.UTF8.GetBytes(RussianNewName)), "the master's tables put the patch in UTF-8");
    }

    /// <summary>A lane is recorded per language: a master whose English tables are UTF-8, read under English first, still
    /// leaves a Russian patch over it in Windows-1251 once the language changes.</summary>
    [Fact]
    public void ALaneReadUnderEnglishDoesNotCarryIntoRussian()
    {
        var key = new ModKey("HcRe", ModType.Plugin);
        var dir = ModDir("BilingualMaster");
        var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE) { UsingLocalization = true };
        var w = mod.Weapons.AddNew();
        w.EditorID = "HcReWeap";
        w.Name = new TranslatedString(Language.Russian, RussianName);
        w.Name.Set(Language.English, "Épée de fer");
        w.BasicStats = new WeaponBasicStats { Damage = 7, Weight = 1 };
        using (var sw = new StringsWriter(GameRelease.SkyrimSE, key, Path.Combine(dir, "Strings"),
                                          new Always(new MutagenEncodingWrapper(Encoding.UTF8)), new System.IO.Abstractions.FileSystem()))
            mod.BeginWrite.ToPath(Path.Combine(dir, "HcRe.esp")).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).WithStringsWriter(sw).Write();
        var svc = Open("ENGLISH", new[] { "BilingualMaster" }, new[] { "HcRe.esp" });
        Assert.Contains("Épée de fer", ReadName(svc, w.FormKey));

        File.WriteAllText(Path.Combine(Profile, "Skyrim.ini"), "[General]\r\nsLanguage=RUSSIAN\r\n");
        File.AppendAllText(Path.Combine(Profile, "modlist.txt"), "\r\n");
        Assert.Contains(RussianName, ReadName(svc, w.FormKey));

        Assert.DoesNotContain("error:", Set(svc, w.FormKey, "Name", RussianNewName, "HcRePatch.esp"));
        Assert.True(Holds(PatchPath("HcRePatch.esp"), Cp1251.GetBytes(RussianNewName)), "the English lane put the Russian patch in UTF-8");
    }

    /// <summary>English keeps the rule it always had: a master whose English tables hold UTF-8 text puts a new patch
    /// over it in UTF-8, so an English write is the bytes it was before the language was read.</summary>
    [Fact]
    public void AnEnglishPatchOverAMasterWithUtf8TablesKeepsTodaysBytes()
    {
        var key = new ModKey("HcEm", ModType.Plugin);
        var dir = ModDir("EnglishMaster");
        var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE) { UsingLocalization = true };
        var w = mod.Weapons.AddNew();
        w.EditorID = "HcEmWeap";
        w.Name = "Épée de fer";
        w.BasicStats = new WeaponBasicStats { Damage = 7, Weight = 1 };
        using (var sw = new StringsWriter(GameRelease.SkyrimSE, key, Path.Combine(dir, "Strings"),
                                          new Always(new MutagenEncodingWrapper(Encoding.UTF8)), new System.IO.Abstractions.FileSystem()))
            mod.BeginWrite.ToPath(Path.Combine(dir, "HcEm.esp")).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).WithStringsWriter(sw).Write();
        var svc = Open(null, new[] { "EnglishMaster" }, new[] { "HcEm.esp" });
        Assert.Contains("Épée de fer", ReadName(svc, w.FormKey));

        Assert.DoesNotContain("error:", Set(svc, w.FormKey, "Name", "Épée neuve", "HcEmPatch.esp"));
        Assert.True(Holds(PatchPath("HcEmPatch.esp"), Encoding.UTF8.GetBytes("Épée neuve")), "the English patch left UTF-8");
    }

    // ---- which INI ----------------------------------------------------------------------------------

    /// <summary>A profile MO2 runs on the My Games INIs (LocalSettings=false) reads the language from there, not from a
    /// stale Skyrim.ini left in the profile; SkyrimCustom.ini's value wins over Skyrim.ini's.</summary>
    [Fact]
    public void AProfileWithoutLocalInisReadsTheLanguageFromMyGames()
    {
        var weapon = WriteLocalized(Path.Combine(ModDir("GermanMod"), "HcDe.esp"), Language.German, GermanName);
        var svc = Open("ENGLISH", new[] { "GermanMod" }, new[] { "HcDe.esp" });
        File.WriteAllText(Path.Combine(Profile, "settings.ini"), "[General]\r\nLocalSettings=false\r\n");
        var myGames = Path.Combine(_root, "MyGames");
        Directory.CreateDirectory(myGames);
        File.WriteAllText(Path.Combine(myGames, "Skyrim.ini"), "[General]\r\nsLanguage=FRENCH\r\n");
        File.WriteAllText(Path.Combine(myGames, "SkyrimCustom.ini"), "[General]\r\nsLanguage=GERMAN\r\n");

        Assert.Contains(GermanName, MyGamesIsolation.Pointed(myGames, () => ReadName(svc, weapon)));
    }

    // ---- where a plugin's tables come from -----------------------------------------------------------

    /// <summary>A translation mod above a plugin's own mod ships loose tables for it: the game shows the translation,
    /// and so does houseCARL.</summary>
    [Fact]
    public void ATranslationModsLooseTablesWinOverThePluginsOwn()
    {
        var weapon = WriteLocalized(Path.Combine(ModDir("SwordMod"), "HcTr.esp"), Language.English, "Big Iron Sword");
        var staging = Path.Combine(_root, "staging");
        Directory.CreateDirectory(staging);
        WriteLocalized(Path.Combine(staging, "HcTr.esp"), Language.English, "Translated Sword");
        var strings = Path.Combine(ModDir("SwordMod Translation"), "Strings");
        Directory.CreateDirectory(strings);
        foreach (var f in Directory.GetFiles(Path.Combine(staging, "Strings"))) File.Copy(f, Path.Combine(strings, Path.GetFileName(f)));
        var svc = Open(null, new[] { "SwordMod Translation", "SwordMod" }, new[] { "HcTr.esp" });

        var read = ReadName(svc, weapon);
        Assert.Contains("Translated Sword", read);
        Assert.DoesNotContain("Big Iron Sword", read);

        // The census takes the same winner: the in-place refusal names the translation's folder, not the plugin's own.
        var r = InPlace(svc, weapon, "BasicStats.Damage", "20", "HcTr.esp");
        Assert.Contains("it resolves from the Strings folder in the mod folder 'SwordMod Translation'", r);
        Assert.Contains("a plugin write does not reach the Strings folder in the mod folder 'SwordMod Translation'", r);
        Assert.DoesNotContain("beside it (", r);
    }

    /// <summary>A plugin's English tables, built in a staging folder: the plugin is copied to <paramref name="pluginDir"/>
    /// with no tables beside it, and the staging folder holding them is returned.</summary>
    string StageEnglish(string pluginDir, string file, string name, out FormKey weapon)
    {
        var staging = Path.Combine(_root, "staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        weapon = WriteLocalized(Path.Combine(staging, file), Language.English, name);
        File.Copy(Path.Combine(staging, file), Path.Combine(pluginDir, file));
        return staging;
    }

    static void CopyTables(string staging, string strings, Func<string, bool>? which = null)
    {
        Directory.CreateDirectory(strings);
        foreach (var f in Directory.GetFiles(Path.Combine(staging, "Strings")).Where(f => which?.Invoke(f) ?? true))
            File.Copy(f, Path.Combine(strings, Path.GetFileName(f)));
    }

    /// <summary>The census counts the plugin's own tables only when the read takes them: an English order reads its
    /// Data\Strings winner, or its archive in another mod folder, not the German set beside the plugin.</summary>
    [Theory]
    [InlineData(false, "it resolves from your game's Data\\Strings folder")]
    [InlineData(true, "inside the archive HcOw.bsa in the mod folder 'Arc'")]
    public void TheOwnFolderCountsOnlyWhenTheReadTakesIt(bool archive, string where)
    {
        var sword = ModDir("SwordMod");
        WriteLocalized(Path.Combine(sword, "HcOw.esp"), Language.German, GermanName);
        File.Delete(Path.Combine(sword, "HcOw.esp"));
        var staging = StageEnglish(sword, "HcOw.esp", "Big Iron Sword", out var weapon);
        if (archive) File.WriteAllBytes(Path.Combine(ModDir("Arc"), "HcOw.bsa"), Archive(staging));
        else CopyTables(staging, Path.Combine(DataDir, "Strings"));
        Directory.CreateDirectory(DataDir);
        new SkyrimMod(new ModKey("Skyrim", ModType.Master), SkyrimRelease.SkyrimSE)
            .BeginWrite.ToPath(Path.Combine(DataDir, "Skyrim.esm")).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        var svc = Open(null, new[] { "SwordMod", "Arc" }, new[] { "HcOw.esp" }, new[] { "Skyrim.esm" });

        var r = InPlace(svc, weapon, "BasicStats.Damage", "20", "HcOw.esp");
        Assert.True(r.Contains(where), r);
        Assert.DoesNotContain("beside it (", r);
    }

    /// <summary>A translation mod's loose tables win over the plugin's own archive in the read, and so in the census.</summary>
    [Fact]
    public void ATranslationsLooseTablesComeBeforeTheArchiveBesideThePlugin()
    {
        var sword = ModDir("SwordMod");
        var staging = StageEnglish(sword, "HcBs.esp", "Big Iron Sword", out var weapon);
        File.WriteAllBytes(Path.Combine(sword, "HcBs.bsa"), Archive(staging));
        CopyTables(staging, Path.Combine(ModDir("SwordMod Translation"), "Strings"));
        var svc = Open(null, new[] { "SwordMod Translation", "SwordMod" }, new[] { "HcBs.esp" });

        var r = InPlace(svc, weapon, "BasicStats.Damage", "20", "HcBs.esp");
        Assert.Contains("it resolves from the Strings folder in the mod folder 'SwordMod Translation'", r);
        Assert.DoesNotContain("HcBs.bsa", r);
    }

    /// <summary>An instance folder spelled with '/' still finds a plugin's own tables to be the ones it reads.</summary>
    [Fact]
    public void AnInstanceSpelledWithForwardSlashesKeepsThePluginsOwnTables()
    {
        var weapon = WriteLocalized(Path.Combine(ModDir("SwordMod"), "HcSl.esp"), Language.English, "Big Iron Sword");
        var svc = Open(null, new[] { "SwordMod" }, new[] { "HcSl.esp" }, instance: Instance.Replace('\\', '/'));

        var r = InPlace(svc, weapon, "BasicStats.Damage", "20", "HcSl.esp");
        Assert.Contains("in a Strings folder beside it (English)", r);
        Assert.DoesNotContain("resolves from", r);
    }

    /// <summary>A translation mod that wins with two of the three kinds is reported partial, naming the missing one.</summary>
    [Fact]
    public void APartialLooseWinnerIsReportedPartial()
    {
        var staging = StageEnglish(ModDir("SwordMod"), "HcPa.esp", "Big Iron Sword", out var weapon);
        CopyTables(staging, Path.Combine(ModDir("SwordMod Translation"), "Strings"),
                   f => !f.EndsWith(".ILSTRINGS", StringComparison.OrdinalIgnoreCase));
        var svc = Open(null, new[] { "SwordMod Translation", "SwordMod" }, new[] { "HcPa.esp" });

        var r = InPlace(svc, weapon, "BasicStats.Damage", "20", "HcPa.esp");
        Assert.Contains("in the mod folder 'SwordMod Translation' (English), of which English has no .ILSTRINGS file", r);
    }

    /// <summary>The winner rule is Mutagen's: a translation's copy of an INI-listed archive wins from a mod folder
    /// whatever its name, and an archive named for another plugin by the last " - " ("HcUp - Extra - Textures" is
    /// HcUp - Extra's) does not, though it outranks it.</summary>
    [Fact]
    public void ATranslationOfAnIniListedArchiveInAModFolderWins()
    {
        var staging = Path.Combine(_root, "staging");
        Directory.CreateDirectory(staging);
        WriteLocalized(Path.Combine(staging, "HcUp.esm"), Language.German, GermanName, ModType.Master);
        Directory.CreateDirectory(DataDir);
        var plugin = Path.Combine(DataDir, "HcUp.esm");
        File.Copy(Path.Combine(staging, "HcUp.esm"), plugin);
        var listed = Path.Combine(ModDir("German Translation"), "Skyrim - Interface.bsa");
        var other = Path.Combine(ModDir("Other"), "HcUp - Extra - Textures.bsa");
        File.WriteAllBytes(listed, Archive(staging));
        File.WriteAllBytes(other, Archive(staging));
        var prof = Path.Combine(_root, "profile");
        Directory.CreateDirectory(prof);
        File.WriteAllText(Path.Combine(prof, "Skyrim.ini"), "[General]\r\nsLanguage=GERMAN\r\n");
        var archives = new[]
        {
            new ActiveArchive(listed, ArchiveDiscovery.IniArchiveOwner, 0, "German Translation"),
            new ActiveArchive(other, "HcUp - Extra.esp", 5, "Other"),
        };

        var text = GameLanguage.Read(GameIni.For(prof), Mo2LoadOrder.ReadComposition(prof), new ArchiveDiscoveryResult(archives, Array.Empty<string>()), Mods, DataDir, "", new[] { plugin });

        Assert.Equal(Path.GetDirectoryName(listed), text.FoldersFor(plugin, DataDir)?.Archives);
    }

    /// <summary>The refusal names only archives carrying a missing kind: one passed over for a kind that did find a
    /// winner is not said to carry the missing tables.</summary>
    [Fact]
    public void ASkyrimRefusalNamesOnlyTheMissingKindsArchives()
    {
        var staging = Path.Combine(_root, "staging");
        Directory.CreateDirectory(staging);
        WriteLocalized(Path.Combine(staging, "Skyrim.esm"), Language.German, GermanName, ModType.Master);
        Directory.CreateDirectory(DataDir);
        var skyrim = Path.Combine(DataDir, "Skyrim.esm");
        File.Copy(Path.Combine(staging, "Skyrim.esm"), skyrim);
        byte[] Only(params string[] kinds) => BsaBuilder.Build(105, BsaBuilder.HasFolderNames | BsaBuilder.HasFileNames, new[] { ("strings",
            kinds.Select(k => ($"skyrim_german.{k}", File.ReadAllBytes(Path.Combine(staging, "Strings", $"Skyrim_German.{k}")))).ToArray()) });
        var listed = Path.Combine(DataDir, "Skyrim - Interface.bsa");
        var other = Path.Combine(ModDir("Other"), "Other.bsa");
        var extra = Path.Combine(ModDir("Extra"), "Extra.bsa");
        File.WriteAllBytes(listed, Only("strings", "ilstrings"));
        File.WriteAllBytes(other, Only("strings"));
        File.WriteAllBytes(extra, Only("dlstrings"));
        var prof = Path.Combine(_root, "profile");
        Directory.CreateDirectory(prof);
        File.WriteAllText(Path.Combine(prof, "Skyrim.ini"), "[General]\r\nsLanguage=GERMAN\r\n");
        var archives = new[]
        {
            new ActiveArchive(listed, ArchiveDiscovery.IniArchiveOwner, 0, null),
            new ActiveArchive(other, "Other.esp", 5, "Other"),
            new ActiveArchive(extra, "Extra.esp", 3, "Extra"),
        };

        var ex = Assert.Throws<GameLanguageRefusedException>(() => GameLanguage.Read(GameIni.For(prof), Mo2LoadOrder.ReadComposition(prof),
            new ArchiveDiscoveryResult(archives, Array.Empty<string>()), Mods, DataDir, "", new[] { skyrim }));
        Assert.Contains("Skyrim.esm's German .DLSTRINGS string tables were not found", ex.Message);
        Assert.Contains("Extra.bsa in the mod folder 'Extra' carries them, but it is named for another plugin and the INI does not list it", ex.Message);
        Assert.DoesNotContain("Other.bsa", ex.Message);
    }

    /// <summary>Mutagen reads a plugin's tables from one folder: Skyrim.esm's kinds winning from two folders is refused,
    /// naming the split, and another plugin's split keeps the read beside it rather than reading one folder for all.</summary>
    [Fact]
    public void TableKindsThatWinFromTwoFoldersAreNotReadFromOne()
    {
        var staging = Path.Combine(_root, "staging");
        Directory.CreateDirectory(staging);
        var weapon = WriteLocalized(Path.Combine(staging, "Skyrim.esm"), Language.German, GermanName, ModType.Master);
        Directory.CreateDirectory(Path.Combine(DataDir, "Strings"));
        var skyrim = Path.Combine(DataDir, "Skyrim.esm");
        File.Copy(Path.Combine(staging, "Skyrim.esm"), skyrim);
        foreach (var k in new[] { "DLSTRINGS", "ILSTRINGS" })
            File.Copy(Path.Combine(staging, "Strings", $"Skyrim_German.{k}"), Path.Combine(DataDir, "Strings", $"Skyrim_German.{k}"));
        var fix = Path.Combine(ModDir("Fix"), "Strings");
        Directory.CreateDirectory(fix);
        File.Copy(Path.Combine(staging, "Strings", "Skyrim_German.STRINGS"), Path.Combine(fix, "Skyrim_German.STRINGS"));
        var svc = Open("GERMAN", new[] { "Fix" }, Array.Empty<string>(), new[] { "Skyrim.esm" });

        var r = ReadName(svc, weapon);
        Assert.Contains("Skyrim.esm's German string tables win from more than one folder (.STRINGS loose in the mod folder 'Fix', .DLSTRINGS loose in your game's Data folder", r);

        var plugin = Path.Combine(ModDir("SwordMod"), "HcSp.esp");
        WriteLocalized(plugin, Language.English, "Big Iron Sword");
        var part = Path.Combine(ModDir("SwordMod Fix"), "Strings");
        Directory.CreateDirectory(part);
        File.Copy(Path.Combine(ModDir("SwordMod"), "Strings", "HcSp_English.STRINGS"), Path.Combine(part, "HcSp_English.STRINGS"));
        File.WriteAllText(Path.Combine(Profile, "Skyrim.ini"), "[General]\r\nsLanguage=ENGLISH\r\n");
        File.WriteAllText(Path.Combine(Profile, "modlist.txt"), "# header\r\n+SwordMod Fix\r\n+SwordMod\r\n");
        var text = GameLanguage.Read(GameIni.For(Profile), Mo2LoadOrder.ReadComposition(Profile), new ArchiveDiscoveryResult(Array.Empty<ActiveArchive>(), Array.Empty<string>()),
                                     Mods, DataDir, "", new[] { plugin });
        Assert.Null(text.FoldersFor(plugin, DataDir));
    }

    /// <summary>The order's own copy keeps its winners however its path is spelled: forward slashes find the
    /// translation's tables as the order's spelling does.</summary>
    [Fact]
    public void AServedPluginSpelledAnotherWayKeepsItsWinners()
    {
        var plugin = Path.Combine(ModDir("SwordMod"), "HcSw.esp");
        WriteLocalized(plugin, Language.English, "Big Iron Sword");
        var strings = Path.Combine(ModDir("SwordMod Translation"), "Strings");
        Directory.CreateDirectory(strings);
        foreach (var f in Directory.GetFiles(Path.Combine(ModDir("SwordMod"), "Strings"))) File.Copy(f, Path.Combine(strings, Path.GetFileName(f)));
        Open(null, new[] { "SwordMod Translation", "SwordMod" }, new[] { "HcSw.esp" });

        var text = GameLanguage.Read(GameIni.For(Profile), Mo2LoadOrder.ReadComposition(Profile), new ArchiveDiscoveryResult(Array.Empty<ActiveArchive>(), Array.Empty<string>()),
                                     Mods, DataDir, "", new[] { plugin });
        Assert.Equal(strings, text.FoldersFor(plugin.Replace('\\', '/'), DataDir)?.Strings);
    }

    /// <summary>A copy of a plugin the order does not serve (an older version in a disabled mod) reads the tables
    /// beside it, not the enabled copy's.</summary>
    [Fact]
    public void AnOffOrderCopyReadsTheTablesBesideIt()
    {
        var weapon = WriteLocalized(Path.Combine(ModDir("SwordMod"), "HcOff.esp"), Language.English, "Enabled Sword");
        WriteLocalized(Path.Combine(ModDir("OldSword"), "HcOff.esp"), Language.English, "Old Sword");
        var svc = Open(null, new[] { "SwordMod" }, new[] { "HcOff.esp" });
        File.AppendAllText(Path.Combine(Profile, "modlist.txt"), "-OldSword\r\n");

        var read = ReadName(svc, weapon, source: new { file = "HcOff.esp", mod = "OldSword" });
        Assert.DoesNotContain("error:", read);
        Assert.Contains("Old Sword", read);
        Assert.DoesNotContain("Enabled Sword", read);
    }

    // ---- where the language lives ---------------------------------------------------------------------

    /// <summary>The write's code page is the call's language and nothing else: a new plugin no contributor puts in a
    /// lane, created through the service under a Russian order, goes out in Windows-1251.</summary>
    [Fact]
    public void ANewPluginWithNoContributorUnderARussianOrderIsWindows1251()
    {
        var plain = new SkyrimMod(new ModKey("HcPlain", ModType.Plugin), SkyrimRelease.SkyrimSE);
        plain.BeginWrite.ToPath(Path.Combine(ModDir("PlainMod"), "HcPlain.esp")).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        var svc = Open("RUSSIAN", new[] { "PlainMod" }, new[] { "HcPlain.esp" });

        var r = CreateTools.Create(svc, patch: "HcOut", records: Je(
            $@"[{{""record_type"":""Weapon"",""editorid"":""HcOutWeap"",""ops"":[{{""field_path"":""Name"",""value"":{JsonSerializer.Serialize(RussianName)}}}]}}]"));
        Assert.DoesNotContain("error:", r);
        Assert.True(Holds(PatchPath("HcOut.esp"), Cp1251.GetBytes(RussianName)), "the name is not in Windows-1251");
    }

    /// <summary>An in-place edit of a Russian 1251 plugin keeps its untouched name in 1251, and a value 1251 cannot
    /// spell is refused naming Windows-1251.</summary>
    [Fact]
    public void AnInPlaceEditOfARussianPluginStaysWindows1251()
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
        var svc = Open("RUSSIAN", new[] { "RussianMod" }, new[] { "HcRu.esp" });
        Assert.Contains(RussianName, ReadName(svc, w.FormKey));

        var r = InPlace(svc, w.FormKey, "BasicStats.Damage", "20", "HcRu.esp");
        Assert.DoesNotContain("error:", r);
        Assert.True(Holds(path, Cp1251.GetBytes(RussianName)), "the untouched name left Windows-1251");

        var bad = InPlace(svc, w.FormKey, "Name", "Ελληνικά", "HcRu.esp");
        Assert.Contains("Windows-1251", bad);
        Assert.Contains("has no spelling for", bad);
    }

    /// <summary>A plain plugin's own names set its lane in every language: under a Russian order, an in-place edit of a
    /// UTF-8 plugin keeps its untouched name in UTF-8, not Windows-1251.</summary>
    [Fact]
    public void AnInPlaceEditOfAUtf8PluginUnderARussianOrderStaysUtf8()
    {
        var path = Path.Combine(ModDir("Utf8Mod"), "HcU8.esp");
        var mod = new SkyrimMod(new ModKey("HcU8", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var w = mod.Weapons.AddNew();
        w.EditorID = "HcU8Weap";
        w.Name = RussianName;
        w.BasicStats = new WeaponBasicStats { Damage = 7, Weight = 1 };
        var utf8 = new MutagenEncodingWrapper(new UTF8Encoding(false));
        mod.BeginWrite.ToPath(path).WithLoadOrder(Array.Empty<ISkyrimModGetter>())
           .WithEmbeddedEncodings(new EncodingBundle(utf8, utf8)).Write();
        var svc = Open("RUSSIAN", new[] { "Utf8Mod" }, new[] { "HcU8.esp" });
        Assert.Contains(RussianName, ReadName(svc, w.FormKey));

        Assert.DoesNotContain("error:", InPlace(svc, w.FormKey, "BasicStats.Damage", "20", "HcU8.esp"));
        Assert.True(Holds(path, Encoding.UTF8.GetBytes(RussianName)), "the untouched name left UTF-8");
        Assert.False(Holds(path, Cp1251.GetBytes(RussianName)), "the untouched name went out Windows-1251");
    }

    const string KoreanName = "강철 검";

    /// <summary>Korean has no code page in Mutagen, so its text is UTF-8: an in-place Korean name into a plugin whose
    /// own text is all ASCII goes out UTF-8.</summary>
    [Fact]
    public void AKoreanInPlaceNameIntoAnAsciiPluginIsUtf8()
    {
        var path = Path.Combine(ModDir("PlainMod"), "HcKo.esp");
        var mod = new SkyrimMod(new ModKey("HcKo", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var w = mod.Weapons.AddNew();
        w.EditorID = "HcKoWeap";
        w.Name = "Iron Sword";
        w.BasicStats = new WeaponBasicStats { Damage = 7, Weight = 1 };
        mod.BeginWrite.ToPath(path).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        var svc = Open("KOREAN", new[] { "PlainMod" }, new[] { "HcKo.esp" });
        Assert.Contains("Iron Sword", ReadName(svc, w.FormKey));

        Assert.DoesNotContain("error:", InPlace(svc, w.FormKey, "Name", KoreanName, "HcKo.esp"));
        Assert.True(Holds(path, Encoding.UTF8.GetBytes(KoreanName)), "the Korean name is not UTF-8");
    }

    /// <summary>A Korean install keeps a legacy file's own bytes, so a Korean name in place there is refused, and the
    /// refusal says the file's text is not UTF-8 rather than calling it Windows-1252.</summary>
    [Fact]
    public void AKoreanInPlaceNameIntoALegacyPluginSaysItIsNotUtf8()
    {
        var path = Path.Combine(ModDir("WesternMod"), "HcWe.esp");
        var mod = new SkyrimMod(new ModKey("HcWe", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var w = mod.Weapons.AddNew();
        w.EditorID = "HcWeWeap";
        w.Name = "Epée longue";
        w.BasicStats = new WeaponBasicStats { Damage = 7, Weight = 1 };
        var cp1252 = new MutagenEncodingWrapper(Cp1252);
        mod.BeginWrite.ToPath(path).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).WithEmbeddedEncodings(new EncodingBundle(cp1252, cp1252)).Write();
        var svc = Open("KOREAN", new[] { "WesternMod" }, new[] { "HcWe.esp" });
        Assert.Contains("Epée longue", ReadName(svc, w.FormKey));

        var bad = InPlace(svc, w.FormKey, "Name", KoreanName, "HcWe.esp");
        Assert.Contains("Its text is not UTF-8", bad);
        Assert.Contains("which only UTF-8 can spell, because your game's language has no code page of its own", bad);
        Assert.DoesNotContain("Windows-1252", bad);
        Assert.True(Holds(path, Cp1252.GetBytes("Epée longue")), "the refused write changed the file");
    }

    string InPlace(LoadOrderService svc, FormKey fk, string field, string value, string plugin)
    {
        var ops = Je($@"[{{""formid"":""{Fid(fk)}"",""field_path"":""{field}"",""op"":""Set"",""value"":{JsonSerializer.Serialize(value)}}}]");
        ApplyTools.Apply(svc, ops: ops, in_place: plugin);   // the first touch asks for consent
        return ApplyTools.Apply(svc, ops: ops, in_place: plugin, acknowledge: true);
    }

    /// <summary>A BSA holding every table file in <paramref name="staging"/>'s Strings folder.</summary>
    static byte[] Archive(string staging)
        => BsaBuilder.Build(105, BsaBuilder.HasFolderNames | BsaBuilder.HasFileNames, new[] { ("strings",
            Directory.GetFiles(Path.Combine(staging, "Strings")).Select(f => (Path.GetFileName(f).ToLowerInvariant(), File.ReadAllBytes(f))).ToArray()) });

    /// <summary>One encoding for every table, spelled here rather than borrowed from the code under test.</summary>
    sealed class Always(IMutagenEncoding encoding) : IMutagenEncodingProvider
    {
        public IMutagenEncoding GetEncoding(GameRelease release, Language language) => encoding;
    }

    // ---- #1075 ------------------------------------------------------------------------------------

    /// <summary>ARR's shape: the winning copy of a Creation Club master sits in one mod folder with no strings, and the
    /// archive named for it, carrying its tables, sits in another. The read finds the tables through the archive, and
    /// the in-place shape census names the same archive, in a mod folder or in MO2's overwrite folder.</summary>
    [Theory]
    [InlineData(false, "in the mod folder 'Fishing'")]
    [InlineData(true, "in MO2's overwrite folder")]
    public void APluginWhoseStringsArchiveIsInAnotherModFolderReadsItsName(bool inOverwrite, string where)
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
        var archiveDir = inOverwrite ? Path.Combine(Instance, "overwrite") : ModDir("Fishing");
        Directory.CreateDirectory(archiveDir);
        File.WriteAllBytes(Path.Combine(archiveDir, "HcCc.bsa"),
            BsaBuilder.Build(105, BsaBuilder.HasFolderNames | BsaBuilder.HasFileNames, new[] { ("strings", tables) }));
        var svc = Open(null, new[] { "Cleaned", "Fishing" }, new[] { "HcCc.esm" }, new[] { "Skyrim.esm" });

        var read = ReadName(svc, weapon);
        Assert.DoesNotContain("error:", read);
        Assert.Contains(CcName, read);
        Assert.DoesNotContain("unresolved", read);

        // The in-place census names the archive the read used.
        Assert.Contains(where, InPlace(svc, weapon, "BasicStats.Damage", "20", "HcCc.esm"));
    }
}
