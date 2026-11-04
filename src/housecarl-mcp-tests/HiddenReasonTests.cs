using System.Text.Json;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A path MO2 hides is said hidden, with the rule and the mod holding it, by every surface that reads the
/// resolver's answer, and a hidden file is never read through a named folder (#1281).</summary>
[Trait("tier", "integration")]
public sealed class HiddenReasonTests : IDisposable
{
    const string OnlyHidden = @"meshes\only.nif";            // ModA ships only its .mohidden twin
    static readonly byte[] X = "x"u8.ToArray();
    readonly PlaceInstance _p = new();

    public HiddenReasonTests()
    {
        var a = _p.Mod("ModA");                                   // enabled
        PlaceInstance.Loose(a, "meta.ini", X);
        PlaceInstance.Loose(a, @"textures\x.dds.mohidden", X);
        PlaceInstance.Loose(a, @"textures\shown.dds", X);
        PlaceInstance.Loose(a, @".git\HEAD", X);
        PlaceInstance.Loose(a, @"fomod\info.xml", X);
        PlaceInstance.Loose(a, OnlyHidden + ".mohidden", X);
        PlaceInstance.Loose(a, PlaceInstance.FacegenRel + ".mohidden", X);
        var c = _p.Mod("ModC");                                   // unticked
        PlaceInstance.Loose(c, "meta.ini", X);
        PlaceInstance.Loose(c, @"meshes\c.nif.mohidden", X);
        PlaceInstance.Loose(_p.Mod("ModD"), @"textures\deny\t.dds", X);   // enabled, a sub-folder that may be list-denied
        PlaceInstance.Loose(_p.Mod("ModK"), "meta.ini", X);               // enabled, ships only a root meta.ini
        PlaceInstance.Loose(_p.Mod("ModS"), @"textures\s.dds", X);        // enabled, no root meta.ini
        PlaceInstance.Loose(_p.Data, @"textures\d.dds.mohidden", X);      // the game's own Data folder
        PlaceInstance.Loose(_p.Data, "meta.ini", X);
        // One active plugin, because the walk needs a load order.
        _p.ProfileWithDummy("ModS", "+ModA", "-ModC", "+ModD", "+ModK", "+ModS");
    }

    public void Dispose() => _p.Dispose();

    static AssetHit Hit(LoadOrderService svc, string rel) => svc.AssetStatus(new[] { rel }).Results[0].Hit!;

    static string Status(LoadOrderService svc, string? path = null, string[]? under = null, string? provider = null,
                         bool countsOnly = false, string? format = null)
        => AssetTools.AssetStatus(svc, asset_paths: path is null ? null : new[] { path }, under: under,
                                  provider: provider, counts_only: countsOnly, format: format);

    static JsonElement Assets(string json) => JsonDocument.Parse(json).RootElement;

    // ---- the resolver ------------------------------------------------------------------------------

    [Theory]
    [InlineData(@"textures\x.dds.mohidden", HiddenKind.SkipSuffix, ".mohidden")]
    [InlineData(@".git\HEAD", HiddenKind.SkipDirectory, ".git")]
    [InlineData("meta.ini", HiddenKind.RootName, "meta.ini")]
    [InlineData(@"fomod\info.xml", HiddenKind.RootName, "fomod folder")]
    public void AHiddenPathResolvesNotFoundWithItsRuleAndHolder(string rel, HiddenKind kind, string token)
    {
        var hit = Hit(_p.Open(), rel);

        Assert.False(hit.Exists);
        Assert.Equal(new HiddenRule(kind, token), hit.Hidden!.Rule);
        Assert.Equal("ModA", hit.Hidden.Holders[0]);
    }

    [Fact]
    public void ARootMetaIniNamesEveryRootHoldingItAndNotAnUntickedOne()
    {
        var holders = Hit(_p.Open(), "meta.ini").Hidden!.Holders;

        Assert.Equal(new[] { "ModA", "ModK", "Data" }, holders);
    }

    [Fact]
    public void AHiddenNameNobodyHoldsCarriesNoHiddenValue()
    {
        var svc = _p.Open();

        Assert.Null(Hit(svc, @"textures\nothing.dds.mohidden").Hidden);
        Assert.Contains("ABSENT — no active mod or BSA provides this path", Status(svc, @"textures\nothing.dds.mohidden"));
    }

    [Fact]
    public void TheUnhiddenNameNamesTheModsHiddenCopy()
    {
        var svc = _p.Open();

        var hidden = Hit(svc, OnlyHidden).Hidden!;
        Assert.Equal(OnlyHidden + ".mohidden", hidden.HiddenPath);
        Assert.Equal(new[] { "ModA" }, hidden.Holders);
        var text = Status(svc, OnlyHidden, provider: "ModA");
        Assert.Contains($"in 'ModA', as '{OnlyHidden}.mohidden'", text);
        Assert.Contains("provider \"ModA\": holds it, hidden by MO2", text);
        Assert.DoesNotContain("does not provide this path", text);
    }

    [Fact]
    public void TheGameDataFolderIsNeverSkippedButKeepsItsRootNamesHidden()
    {
        var svc = _p.Open();

        var hit = Hit(svc, @"textures\d.dds.mohidden");
        Assert.True(hit.Exists);
        Assert.Equal("Data", hit.Winner!.Source);
        Assert.Null(hit.Hidden);
        Assert.False(Hit(svc, "meta.ini").Exists);
        Assert.Contains(@"textures\d.dds.mohidden", svc.AssetStatus(Array.Empty<string>(), new[] { "textures" }).Results.Select(r => r.RelPath));
    }

    [Fact]
    public void TheRootNameRuleComesFirstWhereRulesOverlap()
        => Assert.Equal(HiddenKind.RootName, Mo2SkipRules.Default.Hides(@"fomod\x.mohidden", linked: true)!.Kind);

    // ---- the path row ------------------------------------------------------------------------------

    [Fact]
    public void EachRuleKindIsSaidInItsOwnWords()
    {
        var svc = _p.Open();

        Assert.Contains("ABSENT — hidden by MO2 (skipped by MO2's VFS, by the '.mohidden' suffix) in 'ModA'",
                        Status(svc, @"textures\x.dds.mohidden"));
        Assert.Contains("ABSENT — hidden by MO2 (skipped by MO2's VFS, under a '.git' folder) in 'ModA'",
                        Status(svc, @".git\HEAD"));
        Assert.Contains("ABSENT — hidden by MO2 (left out of MO2's Data view as a root meta.ini; the game still maps it) in 'ModA' and 2 more",
                        Status(svc, "meta.ini"));
    }

    [Fact]
    public void TheJsonRowCarriesTheHiddenRule()
    {
        var doc = JsonDocument.Parse(Status(_p.Open(), @"textures\x.dds.mohidden", format: "json")).RootElement;
        var row = doc.GetProperty("results")[0];

        Assert.False(row.GetProperty("exists").GetBoolean());
        var hidden = row.GetProperty("hidden");
        Assert.Equal("skip_suffix", hidden.GetProperty("rule").GetString());
        Assert.Equal(".mohidden", hidden.GetProperty("match").GetString());
        Assert.Equal("ModA", hidden.GetProperty("holders")[0].GetString());
    }

    [Fact]
    public void TheJsonStandingAndPairSayHidden()
    {
        var svc = _p.Open();

        var row = JsonDocument.Parse(Status(svc, OnlyHidden, provider: "ModA", format: "json")).RootElement.GetProperty("results")[0];
        Assert.Equal("hidden", row.GetProperty("provider_standing").GetString());
        var tint = JsonDocument.Parse(AssetTools.AssetStatus(svc, formids: new[] { PlaceInstance.FacegenFormId }, format: "json"))
                               .RootElement.GetProperty("results")[1];
        Assert.Equal("skip_suffix", tint.GetProperty("pair").GetProperty("hidden").GetProperty("rule").GetString());
    }

    [Fact]
    public void AToFileRowCarriesTheHiddenRule()
    {
        var target = Path.Combine(_p.Root, "hidden-rows.jsonl");
        AssetTools.AssetStatus(_p.Open(), asset_paths: new[] { ".git/HEAD", "textures/shown.dds" }, to_file: target);

        var lines = File.ReadAllLines(target);
        var rows = lines.Where(l => l.TrimStart().StartsWith("{\"path\"")).ToList();
        Assert.Contains(rows, r => r.Contains("\"hidden\":{\"rule\":\"skip_directory\""));
        Assert.Contains(rows, r => r.Contains("shown.dds") && !r.Contains("\"hidden\""));
        Assert.Contains(lines, l => l.Contains("\"provider_standing\",\"error\",\"hidden\""));   // the manifest's schema names it
    }

    [Fact]
    public void AMeshLinkToAHiddenFileSaysHidden()
    {
        PlaceInstance.Loose(_p.Mod("ModS"), @"meshes\linked.nif", AssetWalkWorld.Mesh(new[] { @"textures\x.dds.mohidden" }, null));
        var svc = _p.Open();

        var text = AssetTools.AssetStatus(svc, asset_paths: new[] { @"meshes\linked.nif" }, links: true);
        Assert.Contains("ABSENT — hidden by MO2 (skipped by MO2's VFS, by the '.mohidden' suffix) in 'ModA'", text);
        var json = AssetTools.AssetStatus(svc, asset_paths: new[] { @"meshes\linked.nif" }, links: true, format: "json");
        Assert.Equal("skip_suffix", JsonDocument.Parse(json).RootElement.GetProperty("results")[0].GetProperty("links")[0]
                                               .GetProperty("hidden").GetProperty("rule").GetString());
    }

    [Fact]
    public void AWalkToFileNamesTheHiddenColumnOnlyWhenANodeIsHidden()
    {
        // A walk spills whole past max_chars; the file is named in the render.
        var text = AssetTools.AssetStatus(_p.Open(), asset_paths: new[] { "meta.ini" }, walk: true, max_chars: 300);
        var file = System.Text.RegularExpressions.Regex.Match(text, @"[A-Za-z]:\\[^\s']+\.jsonl").Value;
        Assert.True(File.Exists(file), text);

        var lines = File.ReadAllLines(file);
        Assert.Contains(lines, l => l.Contains("\"note\",\"error\",\"hidden\""));
        Assert.Contains(lines, l => l.Contains("\"hidden\":{\"rule\":\"root_name\""));
    }

    [Fact]
    public void AnArchiveWinningOverAHiddenLooseCopyNotesTheHiddenCopy()
    {
        using var p = new PlaceInstance();
        File.WriteAllBytes(Path.Combine(p.Mod("Arch"), "Dummy.bsa"), PlaceInstance.FaceArchive());
        PlaceInstance.Loose(p.Mod("Hid"), PlaceInstance.FacegenRel + ".mohidden", X);
        p.ProfileWithDummy("Arch", "+Hid", "+Arch");

        var text = Status(p.Open(), PlaceInstance.FacegenRel);
        Assert.Contains("WINS: \"Dummy.bsa\"", text);
        Assert.Contains("note: another loose copy is hidden by MO2", text);
    }

    [Fact]
    public void AFaceGenPairHalfMo2HidesIsSaidHidden()
    {
        var text = AssetTools.AssetStatus(_p.Open(), formids: new[] { PlaceInstance.FacegenFormId });

        // The tint row's pair line, indented under its pair: header, names the mesh half's hidden copy.
        Assert.Contains("\n    ABSENT — hidden by MO2 (skipped by MO2's VFS, by the '.mohidden' suffix) in 'ModA', as", text);
    }

    // ---- under= and provider= --------------------------------------------------------------------

    [Theory]
    [InlineData("fom*/*")]
    [InlineData("*/info.xml")]
    [InlineData(".git")]
    [InlineData("meshes/*.mohidden")]
    [InlineData("meta.ini")]
    public void ASelectorMatchingOnlyHiddenFilesSaysHowManyMo2Hides(string sel)
    {
        var text = Status(_p.Open(), under: new[] { sel });

        Assert.Contains("matched no file", text);
        Assert.Contains("MO2 hides 1 matching path (", text);
        Assert.DoesNotContain("check the spelling", text);
    }

    [Fact]
    public void AFolderGlobNamedLikeARootFileDoesNotSayItIsHidden()
    {
        var text = Status(_p.Open(), under: new[] { "meta.ini/**" });

        Assert.Contains("check the spelling", text);
        Assert.DoesNotContain("MO2 hides", text);
    }

    [Fact]
    public void ARootGlobUnderAProviderWithNoRootMetaIniDoesNotSayOneIsHidden()
    {
        var text = Status(_p.Open(), under: new[] { "*.ini" }, provider: "ModS");

        Assert.Contains("check the spelling", text);
        Assert.DoesNotContain("MO2 hides", text);
    }

    [Fact]
    public void TheDataRootUnderAHiddenOnlyProviderSaysItsFilesAreHidden()
    {
        var text = Status(_p.Open(), under: new[] { "." }, provider: "ModK");

        Assert.Contains("MO2 hides 1 matching path (left out of MO2's Data view as a root meta.ini", text);
        Assert.DoesNotContain("empty selector", text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AHiddenOnlyProvidersCensusSaysWhatMo2Hides(bool countsOnly)
    {
        var text = Status(_p.Open(), provider: "ModK", countsOnly: countsOnly);

        Assert.Contains("provider 'ModK' provides no file in the active load order — MO2 hides 1 of its paths", text);
    }

    [Fact]
    public void AProvidersCensusCountsItsHiddenFiles()
        => Assert.Contains("provider 'ModA': MO2 hides 6 of its paths", Status(_p.Open(), provider: "ModA"));

    // ---- nif, walk, place ------------------------------------------------------------------------

    [Fact]
    public void NifInspectOnAHiddenPathSaysHidden()
    {
        var svc = _p.Open();

        Assert.Contains("ABSENT — hidden by MO2", NifTools.NifInspect(svc, new[] { "meta.ini" }));
        Assert.Contains("ABSENT — hidden by MO2 (left out of MO2's Data view as a root fomod folder",
                        NifTools.NifInspect(svc, new[] { "fomod/info.xml" }));
        var pole = NifTools.NifInspect(svc, new[] { "meta.ini" }, source_provider: AssetSourceChoice.WinnerToken);
        Assert.Contains("ABSENT — hidden by MO2", pole);
        Assert.Contains("so there is no winner to read", pole);
    }

    [Fact]
    public void NifSetOnAHiddenPathSaysHidden()
    {
        var t = NifTools.NifSet(_p.Open(), mesh_path: "meta.ini", op: "set_flags", target: "X", flags: "0x800000E");

        Assert.Contains("ABSENT — hidden by MO2", t);
        Assert.DoesNotContain("no active mod or BSA provides", t);
    }

    [Fact]
    public void AWalkOnAHiddenPathSaysHiddenInTextAndJson()
    {
        var svc = _p.Open();

        var text = AssetTools.AssetStatus(svc, asset_paths: new[] { "meta.ini" }, walk: true);
        Assert.Contains("ABSENT — hidden by MO2", text);
        var json = AssetTools.AssetStatus(svc, asset_paths: new[] { "meta.ini" }, walk: true, format: "json");
        Assert.Contains("\"hidden\":{\"rule\":\"root_name\"", json);
    }

    [Fact]
    public void PlaceWithNoSourceOnAHiddenPathSaysHiddenAndTeachesNoModName()
    {
        var t = PlaceTools.Place(_p.Open(), Assets("""[{"path":"meta.ini"}]"""));

        Assert.Contains("it is hidden by MO2", t);
        Assert.DoesNotContain(WriteSentences.PlaceSourceNameReachesUnticked, t);
    }

    // ---- R11: a named folder never reaches a hidden file ------------------------------------------

    [Theory]
    [InlineData("ModA", @"meshes\only.nif.mohidden")]     // enabled
    [InlineData("ModC", @"meshes\c.nif.mohidden")]        // unticked
    [InlineData("ModC", @"meshes\c.nif")]                  // unticked, the unhidden name
    [InlineData("ModA", "meta.ini")]
    public void NamingAFolderDoesNotReadItsHiddenFile(string mod, string rel)
    {
        var t = NifTools.NifInspect(_p.Open(), new[] { rel }, source_provider: mod);

        Assert.Contains(WriteSentences.PlaceSourceFolderHidden, t);
        Assert.Contains("hidden by MO2 (", t);
        Assert.DoesNotContain("NOT enabled", t);
    }

    /// <summary>Q2 (c), archives as today: a named folder's root archive still answers when its loose copy is hidden.</summary>
    [Fact]
    public void ANamedFoldersArchiveStillAnswersOverItsHiddenLooseCopy()
    {
        var arch = _p.Mod("ArchC");                                // unticked
        File.WriteAllBytes(Path.Combine(arch, "ArchC.bsa"), PlaceInstance.FaceArchive());
        PlaceInstance.Loose(arch, PlaceInstance.FacegenRel + ".mohidden", X);

        var t = PlaceTools.Place(_p.Open(), Assets($$"""[{"path":"{{PlaceInstance.FacegenRel.Replace("\\", "/")}}"}]"""),
                                 source_provider: "ArchC");

        Assert.True(t.Contains("read from 'ArchC'"), t);
        Assert.DoesNotContain(WriteSentences.PlaceSourceFolderHidden, t);
    }

    /// <summary>Forward slashes and a trailing dot name the same file, so the sentence does not name it again as another
    /// one; a twin asked with forward slashes is named as Windows opens it.</summary>
    [Fact]
    public void ANamedHiddenFileAskedWithForwardSlashesIsNotNamedTwice()
    {
        var svc = _p.Open();

        var t = NifTools.NifInspect(svc, new[] { "meshes/c.nif.mohidden." }, source_provider: "ModC");
        Assert.Contains(@"in 'ModC'. ", t);
        Assert.DoesNotContain(", as '", t);
        Assert.Contains(@"in 'ModC', as 'meshes\c.nif.mohidden'", NifTools.NifInspect(svc, new[] { "meshes/c.nif" }, source_provider: "ModC"));
    }

    [Fact]
    public void ADirectlyHiddenPathIsNotNamedAsATwin()
        => Assert.DoesNotContain(", as '", Status(_p.Open(), @"textures\x.dds.mohidden"));

    [Fact]
    public void AHiddenNameUnderAProviderThatHoldsNoneIsNotSaidHidden()
    {
        var text = Status(_p.Open(), under: new[] { @"textures\x.dds.mohidden" }, provider: "ModS");

        Assert.Contains("check the spelling", text);
        Assert.DoesNotContain("MO2 hides", text);
    }

    [Fact]
    public void PlaceNamingAFolderDoesNotReadItsHiddenFile()
    {
        var t = PlaceTools.Place(_p.Open(), Assets("""[{"path":"textures/x.dds.mohidden"}]"""), source_provider: "ModA");

        Assert.Contains(WriteSentences.PlaceSourceFolderHidden, t);
        Assert.DoesNotContain("NOT enabled", t);
    }

    [Fact]
    public void AWalkFromAFolderPutsNoHiddenNodeInThePlaceSet()
    {
        var t = AssetTools.AssetStatus(_p.Open(), asset_paths: new[] { ".git/HEAD" }, walk: true, source_provider: "ModA");

        Assert.DoesNotContain("FROM SOURCE", t);
        Assert.Contains("ABSENT — hidden by MO2 (skipped by MO2's VFS, under a '.git' folder) in 'ModA'", t);
        Assert.DoesNotContain("does not ship it", t);
    }

    [Fact]
    public void AWalkFromAnUntickedFolderSaysItsCopyIsHidden()
    {
        var t = AssetTools.AssetStatus(_p.Open(), asset_paths: new[] { @"meshes\c.nif.mohidden" }, walk: true, source_provider: "ModC");

        Assert.Contains("ABSENT — hidden by MO2 (skipped by MO2's VFS, by the '.mohidden' suffix) in 'ModC'", t);
    }

    // ---- R12: an enabled mod is never called unticked -----------------------------------------------

    [Fact]
    public void AnEnabledModsFileItsFolderDidNotListIsNotCalledUnticked()
    {
        var deny = Path.Combine(_p.Mods, "ModD", "textures", "deny");
        Assert.True(PlaceInstance.TryDenyList(deny), "the list deny did not bite on this machine");
        try
        {
            var t = PlaceTools.Place(_p.Open(), Assets("""[{"path":"textures/deny/t.dds"}]"""), source_provider: "ModD");

            Assert.Contains("a mod folder that IS enabled in MO2, but whose folder did not read this build", t);
            Assert.DoesNotContain("NOT enabled", t);
        }
        finally { PlaceInstance.UndenyList(deny); }
    }
}
