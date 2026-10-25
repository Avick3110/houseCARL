using System.Text;
using System.Text.Json;
using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Streams;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Strings;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// PR #1254's report: a merge or rename of a UTF-8 plugin took the encoding recorded under the OUTPUT's never-read name,
/// so under English a Japanese donor was refused and an accented one silently went Windows-1252, and under Japanese a
/// Japanese-only donor went Shift-JIS. The same name-for-provenance stand-in reached a copy clone and a patch over a UTF-8
/// override. A new file now goes UTF-8 when a copy decoded UTF-8 text into it; a file rewritten in place keeps its own.
/// </summary>
[Trait("tier", "integration")]
[Collection(MyGamesIsolation.Serial)]
public sealed class NewFileTextEncodingTests : IDisposable
{
    const string JapaneseName = "エルフの防御術の盾(優)";
    const string LatinName = "Épée d'acier";
    const string OverName = "Hache élevée";

    const string Legacy = "HcMuLegacy.esp", Jp = "HcMuJp.esp", Accent = "HcMuAccent.esp", JpOnly = "HcMuJpOnly.esp",
                 Over = "HcMuOver.esp", Back = "HcMuBack.esp", Owned = "HcMuOwned.esp", Ascii = "HcMuAscii.esp";

    static readonly Encoding Cp1252 = CodePagesEncodingProvider.Instance.GetEncoding(1252)!;
    static readonly Encoding Cp932 = CodePagesEncodingProvider.Instance.GetEncoding(932)!;
    static readonly EncodingBundle Utf8Bundle = new(new MutagenEncodingWrapper(new UTF8Encoding(false)), new MutagenEncodingWrapper(new UTF8Encoding(false)));
    static readonly EncodingBundle Cp1252Bundle = new(new MutagenEncodingWrapper(Cp1252), new MutagenEncodingWrapper(Cp1252));

    readonly string _root = Path.Combine(Path.GetTempPath(), "hc-newfile-text-" + Guid.NewGuid().ToString("N"));
    readonly List<LoadOrderService> _services = new();
    FormKey _legacyWeap, _legacyPlain, _jpWeap, _accentWeap, _jpOnlyWeap, _ownedDrop, _asciiWeap;

    public void Dispose()
    {
        foreach (var s in _services) s.Dispose();
        try { Directory.Delete(_root, true); } catch { /* temp cleanup best-effort */ }
    }

    string Instance => Path.Combine(_root, "inst");
    string Mods => Path.Combine(Instance, "mods");

    string ModDir(string name)
    {
        var d = Path.Combine(Mods, name);
        Directory.CreateDirectory(d);
        return d;
    }

    static SkyrimMod NewMod(string file) => new(ModKey.FromFileName(file), SkyrimRelease.SkyrimSE);

    static Weapon AddWeapon(SkyrimMod mod, string editorId, string name)
    {
        var w = mod.Weapons.AddNew();
        w.EditorID = editorId;
        w.Name = name;
        w.BasicStats = new WeaponBasicStats { Damage = 7, Weight = 1 };
        return w;
    }

    static void Write(SkyrimMod mod, string path, EncodingBundle encodings, params ISkyrimModGetter[] masters)
        => mod.BeginWrite.ToPath(path).WithLoadOrder(masters).WithEmbeddedEncodings(encodings).Write();

    /// <summary>The world, under <paramref name="sLanguage"/>: a Windows-1252 plugin, UTF-8 plugins holding Japanese and
    /// Latin, Latin only and Japanese only, a UTF-8 override of the 1252 plugin's weapon, a Windows-1252 override of the
    /// UTF-8 Latin weapon, and a houseCARL patch written UTF-8, and an all-ASCII plugin.</summary>
    LoadOrderService Open(string sLanguage)
    {
        Directory.CreateDirectory(Path.Combine(_root, "game", "Data"));
        var legacy = NewMod(Legacy);
        _legacyWeap = AddWeapon(legacy, "HcMuLegacyWeap", LatinName).FormKey;
        _legacyPlain = AddWeapon(legacy, "HcMuLegacyPlain", "Iron Axe").FormKey;
        Write(legacy, Path.Combine(ModDir("LegacyMod"), Legacy), Cp1252Bundle);

        var jp = NewMod(Jp);
        _jpWeap = AddWeapon(jp, "HcMuJpWeap", JapaneseName).FormKey;
        AddWeapon(jp, "HcMuJpLatin", LatinName);
        Write(jp, Path.Combine(ModDir("JpMod"), Jp), Utf8Bundle);

        var accent = NewMod(Accent);
        var accentWeap = AddWeapon(accent, "HcMuAccentWeap", LatinName);
        var kw = accent.Keywords.AddNew("HcMuAccentKw");
        accentWeap.Keywords = new() { kw };
        _accentWeap = accentWeap.FormKey;
        Write(accent, Path.Combine(ModDir("AccentMod"), Accent), Utf8Bundle);

        var jpOnly = NewMod(JpOnly);
        _jpOnlyWeap = AddWeapon(jpOnly, "HcMuJpOnlyWeap", JapaneseName).FormKey;
        Write(jpOnly, Path.Combine(ModDir("JpOnlyMod"), JpOnly), Utf8Bundle);

        var over = NewMod(Over);
        var ov = (Weapon)legacy.Weapons[_legacyWeap].DeepCopy();
        ov.Name = OverName;
        over.Weapons.Add(ov);
        Write(over, Path.Combine(ModDir("OverMod"), Over), Utf8Bundle, legacy);

        var back = NewMod(Back);
        var bv = (Weapon)accent.Weapons[_accentWeap].DeepCopy();
        bv.BasicStats!.Damage = 9;
        back.Weapons.Add(bv);
        Write(back, Path.Combine(ModDir("BackMod"), Back), Cp1252Bundle, accent);

        var owned = NewMod(Owned);
        AddWeapon(owned, "HcMuOwnedKeep", OverName);
        _ownedDrop = AddWeapon(owned, "HcMuOwnedDrop", "Drop Me").FormKey;
        var ownedDir = ModDir("OwnedMod");
        Write(owned, Path.Combine(ownedDir, Owned), Utf8Bundle);
        File.WriteAllText(Path.Combine(ownedDir, "meta.ini"), HousecarlOwnerMeta.Section + "\r\ngenerated=true\r\n");

        var ascii = NewMod(Ascii);
        _asciiWeap = AddWeapon(ascii, "HcMuAsciiWeap", "Iron Sword").FormKey;
        Write(ascii, Path.Combine(ModDir("AsciiMod"), Ascii), Utf8Bundle);

        var plugins = new[] { Legacy, Jp, Accent, JpOnly, Over, Back, Owned, Ascii };
        File.WriteAllText(Path.Combine(Instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(_root, "game").Replace(@"\", @"\\") + ")\r\n");
        var prof = Path.Combine(Instance, "profiles", "Default");
        Directory.CreateDirectory(prof);
        File.WriteAllText(Path.Combine(prof, "loadorder.txt"), "# header\r\n" + string.Concat(plugins.Select(n => n + "\r\n")));
        File.WriteAllText(Path.Combine(prof, "plugins.txt"), string.Concat(plugins.Select(n => "*" + n + "\r\n")));
        File.WriteAllText(Path.Combine(prof, "modlist.txt"),
            "# header\r\n+AsciiMod\r\n+OwnedMod\r\n+BackMod\r\n+OverMod\r\n+JpOnlyMod\r\n+AccentMod\r\n+JpMod\r\n+LegacyMod\r\n");
        File.WriteAllText(Path.Combine(prof, "Skyrim.ini"), $"[General]\r\nsLanguage={sLanguage}\r\n");
        var svc = LoadOrderService.WithInstance(Instance, 0, new UserConfigStore(Path.Combine(_root, "user.json")));
        _services.Add(svc);
        return svc;
    }

    static string Fid(FormKey fk) => $"{fk.ID:X6}:{fk.ModKey.FileName}";
    static JsonElement Je(string json) => JsonDocument.Parse(json).RootElement.Clone();

    string Output(string plugin) => Directory.GetFiles(Mods, plugin, SearchOption.AllDirectories).Single();

    static bool Holds(string path, byte[] needle) => File.ReadAllBytes(path).AsSpan().IndexOf(needle) >= 0;

    static bool HoldsUtf8(string path, string text) => Holds(path, Encoding.UTF8.GetBytes(text));
    static bool Holds1252(string path, string text) => Holds(path, Cp1252.GetBytes(text));

    static string Merge(LoadOrderService svc, string patch, params string[] donors)
    {
        var r = WriteTools.MergePlugins(svc, donors, patch);
        Assert.DoesNotContain("error:", r);
        return r;
    }

    static string Damage(LoadOrderService svc, FormKey fk, string patch) => ApplyTools.Apply(svc,
        ops: Je($@"[{{""formid"":""{Fid(fk)}"",""field_path"":""BasicStats.Damage"",""op"":""Set"",""value"":""20""}}]"),
        patch: patch);

    // ---- merge and rename ---------------------------------------------------------------------------

    [Fact]
    public void AnEnglishRenameOfAUtf8JapanesePluginKeepsItsBytes()
    {
        var svc = Open("ENGLISH");
        Merge(svc, "HcMuR1", Jp);
        Assert.True(HoldsUtf8(Output("HcMuR1.esp"), JapaneseName), "the Japanese name is not the donor's UTF-8");
        Assert.True(HoldsUtf8(Output("HcMuR1.esp"), LatinName), "the Latin name left UTF-8");
    }

    [Fact]
    public void AnEnglishRenameOfAUtf8AccentedPluginStaysUtf8()
    {
        var svc = Open("ENGLISH");
        Merge(svc, "HcMuR2", Accent);
        Assert.True(HoldsUtf8(Output("HcMuR2.esp"), LatinName), "the accented name is not UTF-8");
        Assert.False(Holds1252(Output("HcMuR2.esp"), LatinName), "the accented name went Windows-1252");
    }

    [Fact]
    public void AnEnglishMergeOfAUtf8AndAWindows1252DonorIsUtf8Throughout()
    {
        var svc = Open("ENGLISH");
        Merge(svc, "HcMuR4", Accent, Legacy);
        Assert.True(HoldsUtf8(Output("HcMuR4.esp"), LatinName), "the merge is not UTF-8");
        Assert.False(Holds1252(Output("HcMuR4.esp"), LatinName), "a name stayed Windows-1252 in a UTF-8 merge");

        Merge(svc, "HcMuR5", Jp, Legacy);
        Assert.True(HoldsUtf8(Output("HcMuR5.esp"), JapaneseName), "the Japanese name did not land");
    }

    /// <summary>DECISIONS 2026-10-25 (6): a merge of UTF-8 donors under Japanese goes UTF-8, not Shift-JIS.</summary>
    [Fact]
    public void AJapaneseRenameOfAUtf8JapaneseOnlyPluginStaysUtf8()
    {
        var svc = Open("JAPANESE");
        Merge(svc, "HcMuR11", JpOnly);
        Assert.True(HoldsUtf8(Output("HcMuR11.esp"), JapaneseName), "the Japanese name is not UTF-8");
        Assert.False(Holds(Output("HcMuR11.esp"), Cp932.GetBytes(JapaneseName)), "the Japanese name went Shift-JIS");
    }

    [Fact]
    public void AJapaneseRenameOfAUtf8AccentedPluginIsWrittenUtf8()
    {
        var svc = Open("JAPANESE");
        Merge(svc, "HcMuJaAccent", Accent);
        Assert.True(HoldsUtf8(Output("HcMuJaAccent.esp"), LatinName), "the accented name is not UTF-8");
    }

    /// <summary>Unchanged: a Windows-1252 plugin renamed keeps its bytes, in English and in Japanese.</summary>
    [Theory]
    [InlineData("ENGLISH")]
    [InlineData("JAPANESE")]
    public void ARenameOfAWindows1252PluginKeepsItsBytes(string language)
    {
        var svc = Open(language);
        Merge(svc, "HcMuR3", Legacy);
        Assert.True(Holds1252(Output("HcMuR3.esp"), LatinName), "the 1252 name changed bytes");
    }

    /// <summary>Unchanged (#1241): in a language with no code page a merge is UTF-8, as xEdit's is, legacy donors included.</summary>
    [Fact]
    public void AKoreanRenameOfAWindows1252PluginIsUtf8()
    {
        var svc = Open("KOREAN");
        Merge(svc, "HcMuKo", Legacy);
        Assert.True(HoldsUtf8(Output("HcMuKo.esp"), LatinName), "the Korean merge is not UTF-8");
    }

    /// <summary>A merge that cannot write says so naming the merge, in its own words: no exception type, no phase guess,
    /// and no in_place= remedy for a call that has none.</summary>
    [Fact]
    public void AMergeThatCannotWriteSaysSoNamingTheMerge()
    {
        Open("ENGLISH");
        var donor = Output(Accent);
        var outKey = ModKey.FromFileName("HcMuFail.esp");
        var dict = new Dictionary<FormKey, FormKey>();
        using (var ov = SkyrimMod.CreateFromBinaryOverlay(donor, SkyrimRelease.SkyrimSE))
            foreach (var rec in ov.EnumerateMajorRecords()) dict[rec.FormKey] = new FormKey(outKey, rec.FormKey.ID);
        var blocker = Path.Combine(_root, "blocker");
        File.WriteAllText(blocker, "a file where the output folder would go");

        var r = WritePatchBuilder.MergeBuild(new[] { (Accent, donor, _accentWeap.ModKey) }, outKey, dict, Array.Empty<string>(),
            _ => null, Path.Combine(blocker, "HcMuFail.esp"), dataDir: null, isFaction: _ => false);

        Assert.False(r.Success);
        Assert.StartsWith("houseCARL did not write the merged plugin 'HcMuFail.esp'", r.Error);
        Assert.DoesNotContain("Exception", r.Error);
        Assert.DoesNotContain("serialize or commit", r.Error);
        Assert.DoesNotContain("in_place", r.Error);
        Assert.EndsWith("then run the merge again.", r.Error);
    }

    /// <summary>The same sentence reaches the merge tool's reply: what failed and what to try, ending in a period.</summary>
    [Fact]
    public void AMergeThatCannotWriteSaysWhatToTryThroughTheTool()
    {
        var svc = Open("ENGLISH");
        using var _ = new DenyNewSubfolders(Mods);   // the merge's own folder is made, its staging folder is not

        var r = WriteTools.MergePlugins(svc, new[] { Accent }, "HcMuFail");

        Assert.StartsWith("error: houseCARL did not write the merged plugin 'HcMuFail.esp' and left nothing behind (", r);
        Assert.EndsWith("check that the mods folder is writable and has free space, then run the merge again.", r);
        Assert.DoesNotContain("Exception", r);
        // "left nothing behind": no folder or plugin of the merge's name remains under the mods folder.
        Assert.Empty(Directory.GetFileSystemEntries(Mods, "*HcMuFail*", SearchOption.AllDirectories));
    }

    /// <summary>An aggregate throw names each cause once, not the wrapper's copy of it and then the cause again.</summary>
    [Fact]
    public void AMergeWriteFailureNamesAnAggregatesCauseOnce()
    {
        var s = WritePatchBuilder.MergeWriteFailure("HcMuFail.esp", new AggregateException(new IOException("The disk is full.")));
        Assert.Equal("houseCARL did not write the merged plugin 'HcMuFail.esp' and left nothing behind (The disk is full); "
                     + "check that the mods folder is writable and has free space, then run the merge again.", s);
    }

    /// <summary>Deny the current user making folders inside any NEW subfolder of <paramref name="dir"/> until disposed.</summary>
    sealed class DenyNewSubfolders : IDisposable
    {
        readonly DirectoryInfo _dir;
        readonly System.Security.AccessControl.FileSystemAccessRule _rule = new(
            System.Security.Principal.WindowsIdentity.GetCurrent().Name,
            System.Security.AccessControl.FileSystemRights.CreateDirectories,
            System.Security.AccessControl.InheritanceFlags.ContainerInherit,
            System.Security.AccessControl.PropagationFlags.InheritOnly,
            System.Security.AccessControl.AccessControlType.Deny);

        public DenyNewSubfolders(string dir)
        {
            _dir = new DirectoryInfo(dir);
            var sec = _dir.GetAccessControl();
            sec.AddAccessRule(_rule);
            _dir.SetAccessControl(sec);
        }

        public void Dispose()
        {
            var sec = _dir.GetAccessControl();
            sec.RemoveAccessRuleSpecific(_rule);
            _dir.SetAccessControl(sec);
        }
    }

    // ---- compaction ---------------------------------------------------------------------------------

    /// <summary>Unchanged: a compacted copy keeps its source's encoding.</summary>
    [Theory]
    [InlineData("ENGLISH")]
    [InlineData("JAPANESE")]
    public void ACompactedCopyKeepsItsSourcesEncoding(string language)
    {
        var svc = Open(language);
        Assert.DoesNotContain("error:", WriteTools.CompactPlugin(svc, Jp));
        Assert.DoesNotContain("error:", WriteTools.CompactPlugin(svc, Legacy));
        var jp = Directory.GetFiles(Mods, Jp, SearchOption.AllDirectories).Single(p => !p.Contains("JpMod"));
        var legacy = Directory.GetFiles(Mods, Legacy, SearchOption.AllDirectories).Single(p => !p.Contains("LegacyMod"));
        Assert.True(HoldsUtf8(jp, JapaneseName) && HoldsUtf8(jp, LatinName), "the UTF-8 source's copy left UTF-8");
        Assert.True(Holds1252(legacy, LatinName), "the 1252 source's copy left Windows-1252");
    }

    // ---- copy clone and patches ---------------------------------------------------------------------

    [Fact]
    public void AnEnglishCloneOfAUtf8RecordStaysUtf8()
    {
        var svc = Open("ENGLISH");
        var r = CopyTools.Copy(svc, Fid(_accentWeap), new[] { Accent }, new[] { "Keywords" }, null, null, "HcMuClone", "HcMuR8", null);
        Assert.DoesNotContain("error:", r);
        Assert.True(HoldsUtf8(Output("HcMuR8.esp"), LatinName), "the clone's name is not UTF-8");
        Assert.False(Holds1252(Output("HcMuR8.esp"), LatinName), "the clone's name went Windows-1252");
    }

    [Fact]
    public void AnEnglishPatchOverAUtf8OverrideKeepsItsUntouchedName()
    {
        var svc = Open("ENGLISH");
        Assert.DoesNotContain("error:", Damage(svc, _legacyWeap, "HcMuR12"));
        Assert.True(HoldsUtf8(Output("HcMuR12.esp"), OverName), "the override's name is not UTF-8");
        Assert.False(Holds1252(Output("HcMuR12.esp"), OverName), "the override's name went Windows-1252");
    }

    [Fact]
    public void AnEnglishForwardOfAUtf8OverrideKeepsItsName()
    {
        var svc = Open("ENGLISH");
        Assert.DoesNotContain("error:", ForwardTools.Forward(svc, formids: new[] { Fid(_legacyWeap) }, source: Over, patch: "HcMuFwd"));
        Assert.True(HoldsUtf8(Output("HcMuFwd.esp"), OverName), "the forwarded name is not UTF-8");
    }

    [Fact]
    public void AnEnglishCopiedUtf8FieldMakesThePatchUtf8()
    {
        var svc = Open("ENGLISH");
        var r = ApplyTools.Apply(svc, ops: Je(
            $@"[{{""formid"":""{Fid(_legacyPlain)}"",""field_path"":""Name"",""op"":""CopyFrom"",""from"":""{Fid(_accentWeap)}"",""from_source"":""{Accent}""}}]"),
            patch: "HcMuCopyFrom");
        Assert.DoesNotContain("error:", r);
        Assert.True(HoldsUtf8(Output("HcMuCopyFrom.esp"), LatinName), "the copied name is not UTF-8");
    }

    /// <summary>Unchanged: removing a record from a UTF-8 patch keeps the rest of it UTF-8.</summary>
    [Fact]
    public void RemovingFromAUtf8PatchKeepsItUtf8()
    {
        var svc = Open("ENGLISH");
        Assert.DoesNotContain("error:", RemoveTools.Remove(svc, formids: new[] { Fid(_ownedDrop) }, into: Owned));
        Assert.True(HoldsUtf8(Output(Owned), OverName), "the kept name left UTF-8");
    }

    /// <summary>The copied version's text decides, not its record's origin: a Windows-1252 override of a UTF-8 plugin's
    /// record, patched, keeps its own Windows-1252 name.</summary>
    [Fact]
    public void APatchOverAWindows1252OverrideOfAUtf8RecordStaysWindows1252()
    {
        var svc = Open("ENGLISH");
        // The origin's own read resolves it UTF-8, the stand-in the name rule took for the copied version's text.
        Assert.Contains(LatinName, RecordsTools.Records(svc, formids: new[] { Fid(_accentWeap) },
            project: new RecordsTools.RecordsProject { form = "fields", fields = new[] { "Name" } }, source: JsonSerializer.SerializeToElement(Accent)));
        Assert.DoesNotContain("error:", Damage(svc, _accentWeap, "HcMuBackPatch"));
        Assert.True(Holds1252(Output("HcMuBackPatch.esp"), LatinName), "the 1252 override's name was re-encoded");

        // Extended, it masters the UTF-8 plugin; a master's name is not the patch's text either.
        Assert.DoesNotContain("error:", ApplyTools.Apply(svc, ops: Je(
            $@"[{{""formid"":""{Fid(_accentWeap)}"",""field_path"":""BasicStats.Weight"",""op"":""Set"",""value"":""4""}}]"),
            into: "HcMuBackPatch.esp"));
        Assert.True(Holds1252(Output("HcMuBackPatch.esp"), LatinName), "extending the patch re-encoded its 1252 name");
    }

    // ---- the copy scope -----------------------------------------------------------------------------

    /// <summary>A copy scope ends with its copy: text decoded after an inner scope returns marks the outer mod, and
    /// text decoded after the outer scope returns marks no mod.</summary>
    [Fact]
    public void ACopyScopeEndsWithItsCopy()
    {
        Open("ENGLISH");
        var path = Output(Accent);
        var outer = NewMod("HcMuScopeOuter.esp");
        var inner = NewMod("HcMuScopeInner.esp");
        var after = NewMod("HcMuScopeAfter.esp");
        using var ov = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE, PluginTextEncoding.ReadFor(path));
        string? Decode() => ov.Weapons.Single().Name?.String;

        PluginTextEncoding.CopyInto(outer, () => { PluginTextEncoding.CopyInto(inner, () => 0); return Decode(); });
        Assert.True(PluginTextEncoding.NewFileIsUtf8(outer), "the outer copy's UTF-8 text did not mark it");
        Assert.False(PluginTextEncoding.NewFileIsUtf8(inner), "text decoded after the inner copy marked it");

        PluginTextEncoding.CopyInto(after, () => 0);
        Assert.Equal(LatinName, Decode());
        Assert.False(PluginTextEncoding.NewFileIsUtf8(after), "text decoded after the copy marked it");
    }

    /// <summary>Unchanged: Japanese typed into a new patch with no UTF-8 contributor stays Shift-JIS (DECISIONS 2026-10-21 (7)).</summary>
    [Fact]
    public void AJapaneseValueIntoANewPatchWithNoUtf8ContributorIsShiftJis()
    {
        var svc = Open("JAPANESE");
        var r = ApplyTools.Apply(svc, ops: Je(
            $@"[{{""formid"":""{Fid(_legacyPlain)}"",""field_path"":""Name"",""op"":""Set"",""value"":""{JapaneseName}""}}]"),
            patch: "HcMuR9");
        Assert.DoesNotContain("error:", r);
        Assert.True(Holds(Output("HcMuR9.esp"), Cp932.GetBytes(JapaneseName)), "the typed name is not Shift-JIS");
    }

    // ---- in place -----------------------------------------------------------------------------------

    /// <summary>DECISIONS 2026-10-25 (11): an in-place edit of an all-ASCII plugin under a code-page language keeps that
    /// page, so a value it cannot spell refuses and the file is untouched.</summary>
    [Fact]
    public void AJapaneseValueInPlaceIntoAnAsciiPluginRefuses()
    {
        var svc = Open("ENGLISH");
        var path = Output(Ascii);
        var before = File.ReadAllBytes(path);

        var ops = Je($@"[{{""formid"":""{Fid(_asciiWeap)}"",""field_path"":""Name"",""op"":""Set"",""value"":""{JapaneseName}""}}]");
        ApplyTools.Apply(svc, ops: ops, in_place: Ascii);   // the first touch asks for consent
        var r = ApplyTools.Apply(svc, ops: ops, in_place: Ascii, acknowledge: true);
        Assert.Contains("Windows-1252 has no spelling for", r);
        Assert.Equal(before, File.ReadAllBytes(path));
    }
}
