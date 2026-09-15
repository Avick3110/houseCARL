using System.Text;
using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Streams;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Strings;
using Mutagen.Bethesda.Strings.DI;
using HousecarlCore;
using HousecarlGenerator;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// #740: translated names carrying UTF-8 read as Windows-1252 mojibake and were written back as <c>?</c>. Mutagen
/// picks the encoding from the target language, and Skyrim SE + English is the one pairing whose answer carries no
/// UTF-8 lane — which is exactly the Japanese community's setup (<c>sLanguage=ENGLISH</c> with the
/// <c>*_English.STRINGS</c> tables replaced by UTF-8 translations, and UTF-8 in inline <c>FULL</c> fields too).
///
/// <para>Four plugins, one per shape: a UTF-8 inline <c>FULL</c> (carrying a Japanese AND an accented Latin name, so
/// a per-string write decision would leave the file mixed), a UTF-8 <c>_English.STRINGS</c> table, a UTF-8 plugin
/// holding only accented Latin text — the French/German translation ESP, whose round trip was byte-exact even before
/// any of this — and a real Windows-1252 plugin that has to keep both reading as it did and staying 1252 bytes through
/// a write. The world is built per test because the write arms rewrite a plugin in it.</para>
/// </summary>
[Trait("tier", "integration")]
public sealed class Utf8NameTests : IDisposable
{
    /// <summary>The name from the report — <c>10FC28:Skyrim.esm</c>'s Japanese translation.</summary>
    const string JapaneseName = "エルフの防御術の盾(優)";
    const string LocalizedName = "炎の剣";
    /// <summary>An accented Latin name. In Windows-1252 its bytes are not valid UTF-8, so the fallback is what has to
    /// read them; in UTF-8 they are — the same characters, two encodings, which is the whole per-file question.</summary>
    const string LatinName = "Épée d'acier";

    const string InlineName = "HcUtf8Inline.esp";
    const string TableName = "HcUtf8Table.esp";
    const string LatinPluginName = "HcUtf8Latin.esp";
    const string Utf8LatinName = "HcUtf8Accent.esp";

    readonly string _root, _instance;
    readonly string _priorCorpusPath;
    readonly LoadOrderService _svc;
    readonly FormKey _inlineWeapon, _inlineLatinWeapon, _tableWeapon, _latinWeapon, _utf8LatinWeapon;

    public Utf8NameTests()
    {
        _priorCorpusPath = CorpusRulebook.CorpusPath;
        _root = Path.Combine(Path.GetTempPath(), "hc-utf8-name-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "game", "Data"));
        _instance = Path.Combine(_root, "inst");
        var mods = Path.Combine(_instance, "mods");

        (_inlineWeapon, _inlineLatinWeapon) = WriteInline(NewDir(mods, "InlineMod"));
        _tableWeapon = WriteLocalized(NewDir(mods, "TableMod"));
        _latinWeapon = WriteLatin(NewDir(mods, "LatinMod"));
        _utf8LatinWeapon = WriteUtf8Latin(NewDir(mods, "AccentMod"));

        var genDir = Path.Combine(_root, "corpus-gen");
        CorpusGenerator.GenerateAll(genDir, Path.Combine(_root, "corpus-ref"));
        CorpusRulebook.CorpusPath = Path.Combine(genDir, "corpus.json");

        File.WriteAllText(Path.Combine(_instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(_root, "game").Replace(@"\", @"\\") + ")\r\n");
        var prof = Path.Combine(_instance, "profiles", "Default");
        Directory.CreateDirectory(prof);
        var all = new[] { InlineName, TableName, LatinPluginName, Utf8LatinName };
        File.WriteAllText(Path.Combine(prof, "loadorder.txt"), "# header\r\n" + string.Concat(all.Select(n => n + "\r\n")));
        File.WriteAllText(Path.Combine(prof, "plugins.txt"), string.Concat(all.Select(n => "*" + n + "\r\n")));
        File.WriteAllText(Path.Combine(prof, "modlist.txt"), "# header\r\n+AccentMod\r\n+LatinMod\r\n+TableMod\r\n+InlineMod\r\n");

        _svc = LoadOrderService.WithInstance(_instance, 0, new UserConfigStore(Path.Combine(_root, "user.json")));
    }

    public void Dispose()
    {
        _svc.Dispose();
        CorpusRulebook.CorpusPath = _priorCorpusPath;
        try { Directory.Delete(_root, true); } catch { /* temp cleanup best-effort */ }
    }

    static string NewDir(string mods, string name)
    {
        var d = Path.Combine(mods, name);
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>The write-side encodings, spelled here rather than borrowed from the code under test: a fixture that
    /// shares the production constant cannot fail when that constant is wrong.</summary>
    static readonly EncodingBundle Utf8 = new(MutagenEncoding._utf8, MutagenEncoding._utf8);

    /// <summary>The reported shape, plus the mixed-encoding trap: one Japanese name and one accented LATIN name in
    /// the SAME UTF-8 file. Deciding the write encoding per string would send the first out as UTF-8 and the second
    /// as Windows-1252, and no reader gets both.</summary>
    (FormKey Japanese, FormKey Latin) WriteInline(string dir)
    {
        var mod = new SkyrimMod(new ModKey("HcUtf8Inline", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var w = mod.Weapons.AddNew();
        w.EditorID = "HcUtf8InlineWeap";
        w.Name = JapaneseName;
        w.BasicStats = new WeaponBasicStats { Damage = 10, Weight = 1 };
        var latin = mod.Weapons.AddNew();
        latin.EditorID = "HcUtf8InlineLatinWeap";
        latin.Name = LatinName;
        latin.BasicStats = new WeaponBasicStats { Damage = 11, Weight = 1 };
        mod.BeginWrite.ToPath(Path.Combine(dir, InlineName))
           .WithLoadOrder(Array.Empty<ISkyrimModGetter>()).WithEmbeddedEncodings(Utf8).Write();
        return (w.FormKey, latin.FormKey);
    }

    /// <summary>A UTF-8 plugin carrying ONLY accented Latin text — a French or German translation ESP. Before any of
    /// this its round trip was byte-exact by accident (1252 in, mojibake, the same 1252 bytes out); it has to stay
    /// byte-exact now that the read decodes it correctly.</summary>
    FormKey WriteUtf8Latin(string dir)
    {
        var mod = new SkyrimMod(new ModKey("HcUtf8Accent", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var w = mod.Weapons.AddNew();
        w.EditorID = "HcUtf8AccentWeap";
        w.Name = LatinName;
        w.BasicStats = new WeaponBasicStats { Damage = 9, Weight = 2 };
        mod.BeginWrite.ToPath(Path.Combine(dir, Utf8LatinName))
           .WithLoadOrder(Array.Empty<ISkyrimModGetter>()).WithEmbeddedEncodings(Utf8).Write();
        return w.FormKey;
    }

    /// <summary>A LOCALIZED plugin whose <c>_English.STRINGS</c> table is genuinely UTF-8 — the reported shape. The
    /// table encoding comes from the language, not from the embedded bundle, so the fixture hands Mutagen a strings
    /// writer with a UTF-8 provider to produce one.</summary>
    FormKey WriteLocalized(string dir)
    {
        var key = new ModKey("HcUtf8Table", ModType.Plugin);
        var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        mod.ModHeader.Flags |= SkyrimModHeader.HeaderFlag.Localized;
        var w = mod.Weapons.AddNew();
        w.EditorID = "HcUtf8TableWeap";
        w.Name = LocalizedName;
        w.BasicStats = new WeaponBasicStats { Damage = 12, Weight = 2 };
        using var sw = new StringsWriter(GameRelease.SkyrimSE, key, Path.Combine(dir, "Strings"),
                                         new AlwaysUtf8(), new System.IO.Abstractions.FileSystem());
        mod.BeginWrite.ToPath(Path.Combine(dir, TableName))
           .WithLoadOrder(Array.Empty<ISkyrimModGetter>()).WithEmbeddedEncodings(Utf8).WithStringsWriter(sw).Write();
        return w.FormKey;
    }

    /// <summary>A plugin written by Mutagen's own default — Windows-1252 for Skyrim SE English — so its accented
    /// name really is 1252 bytes on disk.</summary>
    FormKey WriteLatin(string dir)
    {
        var mod = new SkyrimMod(new ModKey("HcUtf8Latin", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var w = mod.Weapons.AddNew();
        w.EditorID = "HcUtf8LatinWeap";
        w.Name = LatinName;
        w.BasicStats = new WeaponBasicStats { Damage = 8, Weight = 3 };
        mod.BeginWrite.ToPath(Path.Combine(dir, LatinPluginName)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        return w.FormKey;
    }

    sealed class AlwaysUtf8 : IMutagenEncodingProvider
    {
        public IMutagenEncoding GetEncoding(GameRelease release, Language language) => MutagenEncoding._utf8;
    }

    static string Fid(FormKey fk) => $"{fk.ID:X6}:{fk.ModKey.FileName}";
    static JsonElement Je(string json) => JsonDocument.Parse(json).RootElement.Clone();
    static RecordsTools.RecordsProject NameField => new() { form = "fields", fields = new[] { "Name" } };

    string ReadName(FormKey fk, string? format = null) =>
        RecordsTools.Records(_svc, formids: new[] { Fid(fk) }, project: NameField, format: format);

    static readonly Encoding Cp1252 = System.Text.CodePagesEncodingProvider.Instance.GetEncoding(1252)!;

    static bool FileHolds(string path, string text) => FileHoldsBytes(path, Encoding.UTF8.GetBytes(text));

    static bool FileHoldsBytes(string path, byte[] needle) => HoldsBytes(File.ReadAllBytes(path), needle);

    /// <summary>Set the weapon's damage — a change that touches no text, so what the file's strings come back as is
    /// the write encoding and nothing else.</summary>
    string Edit(FormKey weapon, string? in_place = null, string? patch = null) => ApplyTools.Apply(_svc,
        ops: Je($@"[{{""formid"":""{Fid(weapon)}"",""field_path"":""BasicStats.Damage"",""op"":""Set"",""value"":""20""}}]"),
        in_place: in_place, acknowledge: in_place is not null, patch: patch);

    static bool HoldsBytes(byte[] hay, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= hay.Length; i++)
        {
            bool ok = true;
            for (int j = 0; j < needle.Length; j++) if (hay[i + j] != needle[j]) { ok = false; break; }
            if (ok) return true;
        }
        return false;
    }

    string PluginPath(string modFolder, string plugin) => Path.Combine(_instance, "mods", modFolder, plugin);

    // ---- read -------------------------------------------------------------------------------------

    [Fact]
    public void AUtf8InlineNameReadsBackAsItself()
    {
        var r = ReadName(_inlineWeapon);
        Assert.DoesNotContain("error:", r);
        Assert.Contains(JapaneseName, r);
    }

    [Fact]
    public void AUtf8EnglishStringsEntryReadsBackAsItself()
    {
        var r = ReadName(_tableWeapon);
        Assert.DoesNotContain("error:", r);
        Assert.Contains(LocalizedName, r);
    }

    /// <summary>The no-regression arm on the read side: a real Windows-1252 name still reads as itself. A lenient
    /// UTF-8 decoder would hand back replacement characters rather than fall back, which is why the decision is a
    /// validity check and not a swallowed exception. What it cannot say is whether a WRITE left those bytes alone —
    /// that is <see cref="AnInPlaceEditLeavesAWindows1252NameAsWindows1252Bytes"/>.</summary>
    [Fact]
    public void AWindows1252NameStillReadsAsItself()
    {
        var r = ReadName(_latinWeapon);
        Assert.DoesNotContain("error:", r);
        Assert.Contains(LatinName, r);
    }

    /// <summary>The json projection carries the same characters the text one does. They ride as <c>\uXXXX</c>
    /// escapes — json's own spelling for non-ASCII, which parses back to the identical string — so the assertion is
    /// on the parsed value, not on the bytes.</summary>
    [Fact]
    public void TheJsonProjectionCarriesTheSameCharacters()
    {
        using var doc = JsonDocument.Parse(ReadName(_inlineWeapon, format: "json"));
        Assert.True(HoldsString(doc.RootElement, JapaneseName), "no value in the json render is the name");
    }

    /// <summary>True if any string anywhere in the document IS that string — the projection's own nesting is not
    /// what this test is about.</summary>
    static bool HoldsString(JsonElement e, string want) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString() == want,
        JsonValueKind.Object => e.EnumerateObject().Any(p => HoldsString(p.Value, want)),
        JsonValueKind.Array => e.EnumerateArray().Any(x => HoldsString(x, want)),
        _ => false,
    };

    // ---- write ------------------------------------------------------------------------------------

    /// <summary>An in-place edit re-serializes the WHOLE plugin, so every name it does not touch is re-encoded. The
    /// untouched Japanese name has to survive that, in the file's bytes and on the next read.</summary>
    [Fact]
    public void AnInPlaceEditLeavesAUtf8NameIntact()
    {
        var r = Edit(_inlineWeapon, in_place: InlineName);
        Assert.DoesNotContain("error:", r);

        var path = PluginPath("InlineMod", InlineName);
        Assert.True(FileHolds(path, JapaneseName), "the rewritten plugin lost the UTF-8 bytes");
        Assert.False(FileHoldsBytes(path, Encoding.UTF8.GetBytes("?????????(?)")), "the name was written as question marks");
        Assert.Contains(JapaneseName, ReadName(_inlineWeapon));
    }

    /// <summary>The mixed-encoding arm. The same UTF-8 file holds a Japanese name and an accented LATIN one; after an
    /// in-place edit BOTH have to still be UTF-8. A per-string decision writes the Latin one as 1252 because 1252 can
    /// spell it, and the file then has two encodings in it.</summary>
    [Fact]
    public void AUtf8FileKeepsOneEncodingForBothItsJapaneseAndItsLatinNames()
    {
        var r = Edit(_inlineWeapon, in_place: InlineName);
        Assert.DoesNotContain("error:", r);

        var path = PluginPath("InlineMod", InlineName);
        Assert.True(FileHolds(path, JapaneseName), "the Japanese name is no longer UTF-8");
        Assert.True(FileHolds(path, LatinName), "the accented Latin name is no longer UTF-8");
        Assert.False(FileHoldsBytes(path, Cp1252.GetBytes(LatinName)), "the accented name was written in 1252 beside UTF-8");
        Assert.Contains(LatinName, ReadName(_inlineLatinWeapon));
    }

    /// <summary>A UTF-8 translation ESP carrying only accented Latin text. Its round trip was byte-exact before any
    /// of this — read as 1252 mojibake, written back as the same bytes — so a correct read paired with a 1252-first
    /// write is the one combination that BREAKS a file that used to survive.</summary>
    [Fact]
    public void AUtf8LatinPluginRoundTripsByteIdenticalThroughAnInPlaceEdit()
    {
        var before = File.ReadAllBytes(PluginPath("AccentMod", Utf8LatinName));
        var r = Edit(_utf8LatinWeapon, in_place: Utf8LatinName);
        Assert.DoesNotContain("error:", r);

        var path = PluginPath("AccentMod", Utf8LatinName);
        Assert.True(FileHolds(path, LatinName), "the accented name is no longer UTF-8");
        Assert.False(FileHoldsBytes(path, Cp1252.GetBytes(LatinName)), "the accented name was flipped to 1252");
        Assert.True(HoldsBytes(before, Encoding.UTF8.GetBytes(LatinName)), "the fixture was not UTF-8 to begin with");
        Assert.Contains(LatinName, ReadName(_utf8LatinWeapon));
    }

    /// <summary>The mirror, and the arm the read-side one cannot cover: the reader accepts both encodings, so only
    /// the BYTES say whether a Windows-1252 plugin was left alone. An in-place edit re-serializes the whole file, so
    /// a UTF-8-first write would silently convert every Western name — and an accented asset path — to bytes the
    /// game does not read at <c>sLanguage=ENGLISH</c>.</summary>
    [Fact]
    public void AnInPlaceEditLeavesAWindows1252NameAsWindows1252Bytes()
    {
        var r = Edit(_latinWeapon, in_place: LatinPluginName);
        Assert.DoesNotContain("error:", r);

        var path = PluginPath("LatinMod", LatinPluginName);
        Assert.True(FileHoldsBytes(path, Cp1252.GetBytes(LatinName)), "the accented name is no longer 1252 bytes");
        Assert.False(FileHolds(path, LatinName), "the accented name was re-encoded to UTF-8");
        Assert.Contains(LatinName, ReadName(_latinWeapon));
    }

    /// <summary>The patch lane copies the winning record into a NEW plugin, so the name makes a full read-then-write
    /// trip through both encodings. This is the arm that catches a HALF fix: with the read corrected and the write
    /// left on Windows-1252, the real Japanese characters have no 1252 spelling and the encoder writes <c>?</c>.</summary>
    [Fact]
    public void ACopyIntoANewPatchCarriesTheUtf8NameVerbatim()
    {
        var r = Edit(_inlineWeapon, patch: "HcUtf8Patch");
        Assert.DoesNotContain("error:", r);

        var written = Directory.GetFiles(_instance, "HcUtf8Patch*.esp", SearchOption.AllDirectories);
        Assert.Single(written);
        Assert.True(FileHolds(written[0], JapaneseName), "the patch lost the UTF-8 bytes");
    }
}
