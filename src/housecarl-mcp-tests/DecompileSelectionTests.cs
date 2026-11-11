using HousecarlGenerator;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Pex;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary><c>housecarl_decompile_script</c> on the shared selection (#1287): Data paths, <c>under=</c> and
/// <c>provider=</c> pick scripts as asset_status picks files, each read loose or out of its archive with no extract
/// step; <c>source_provider=</c> reads a copy that does not win; a file on disk still decompiles beside them; a bare
/// name and the retired names are refused in one sentence. The world: the mod Arch ships Dummy.bsa (bound to its
/// Dummy.esp) holding two scripts, a loose script, and a copy of HcDsShared that the loose copy in Fix wins over;
/// the disabled mod Off ships one script.</summary>
[Trait("tier", "integration")]
public sealed class DecompileSelectionTests : IDisposable
{
    const string Arch1 = @"scripts\hcdsarch1.pex", ArchBase = @"scripts\hcdsarchbase.pex";
    const string LooseRel = @"scripts\hcdsloose.pex", Shared = @"scripts\hcdsshared.pex", OffRel = @"scripts\hcdsoff.pex";

    readonly PlaceInstance _p = new();
    readonly string _out;
    readonly LoadOrderService _svc;

    public DecompileSelectionTests()
    {
        _out = Path.Combine(_p.Root, "out");
        var bsa = Path.Combine(_p.Mod("Arch"), "Dummy.bsa");
        File.WriteAllBytes(bsa, BsaBuilder.Build(105, BsaBuilder.HasFolderNames | BsaBuilder.HasFileNames, new[]
        {
            ("scripts", new[]
            {
                ("hcdsarch1.pex", Pex("HcDsArch1", "HcDsArchBase", "ArchOne")),
                ("hcdsarchbase.pex", Pex("HcDsArchBase", null, "ArchBaseThing")),
                ("hcdsshared.pex", Pex("HcDsShared", null, "ArchCopyThing")),
            }),
        }));
        PlaceInstance.Loose(_p.Mod("Arch"), LooseRel, Pex("HcDsLoose", null, "LooseThing"));
        PlaceInstance.Loose(_p.Mod("Fix"), Shared, Pex("HcDsShared", null, "FixCopyThing"));
        PlaceInstance.Loose(_p.Mod("Off"), OffRel, Pex("HcDsOff", null, "OffThing"));
        _p.ProfileWithDummy("Arch", "+Fix", "+Arch", "-Off");
        _svc = _p.Open();
    }

    public void Dispose() => _p.Dispose();

    /// <summary>A one-object .pex named <paramref name="name"/> declaring one Spell property, as bytes.</summary>
    byte[] Pex(string name, string? parent, string property)
    {
        var dir = Path.Combine(_p.Root, "pex");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name + "-" + property + ".pex");
        PexWriter.WritePex(path, name, parent, PexWriter.AutoObj(property, "Spell"));
        return File.ReadAllBytes(path);
    }

    string Psc(string name) => File.ReadAllText(Path.Combine(_out, name + ".psc"));

    [Fact]
    public void AProviderDecompilesItsLooseAndArchivedScriptsInOneCall()
    {
        var r = DecompileTools.DecompileScript(_svc, provider: "Arch", out_path: _out);

        Assert.Contains("ArchOne", Psc("HcDsArch1"));                       // out of Dummy.bsa
        Assert.Contains("ArchBaseThing", Psc("HcDsArchBase"));
        Assert.Contains("LooseThing", Psc("HcDsLoose"));                    // loose in Arch
        Assert.Contains("FixCopyThing", Psc("HcDsShared"));                 // provider= picks which; the winner is read
        Assert.Contains("1 script(s) provider 'Arch' ships are won by another provider", r);
        Assert.Contains("4 OK", r);
        Assert.Contains(@"OK scripts\hcdsarch1.pex ← ""Dummy.bsa"" (BSA)", r);
        // Dummy.esp and Dummy.bsa are files Arch provides, and not scripts.
        Assert.Contains("2 file(s) the selectors matched are not .pex scripts", r);
        Assert.Contains("[accounting] total=4 rendered=4", r);
    }

    [Fact]
    public void UnderSelectsScriptsAcrossLooseAndArchives()
    {
        var r = DecompileTools.DecompileScript(_svc, under: new[] { "scripts/hcds*.pex" }, out_path: _out);

        Assert.Contains("4 OK", r);
        Assert.True(File.Exists(Path.Combine(_out, "HcDsArch1.psc")), r);
        Assert.True(File.Exists(Path.Combine(_out, "HcDsLoose.psc")), r);
        Assert.False(File.Exists(Path.Combine(_out, "HcDsOff.psc")), r);  // a disabled mod is not in the selection
    }

    [Fact]
    public void SourceProviderReadsTheCopyThatDoesNotWin()
    {
        var r = DecompileTools.DecompileScript(_svc, paths: new[] { "scripts/hcdsshared.pex" }, source_provider: "Arch", out_path: _out);

        Assert.Contains("ArchCopyThing", Psc("HcDsShared"));
        Assert.Contains(@"not the winning copy (""Fix"" (loose) wins)", r);
    }

    [Fact]
    public void SourceProviderReachesADisabledModsScript()
    {
        var r = DecompileTools.DecompileScript(_svc, paths: new[] { OffRel }, source_provider: "Off", out_path: _out);

        Assert.Contains("OffThing", Psc("HcDsOff"));
        Assert.Contains("(not loaded by the game)", r);
    }

    [Fact]
    public void AFileOnDiskDecompilesBesideTheSelection()
    {
        var disk = _p.Scratch("HcDsDisk.pex", Pex("HcDsDisk", null, "DiskThing"));

        var r = DecompileTools.DecompileScript(_svc, paths: new[] { disk, LooseRel }, out_path: _out);

        Assert.Contains("DiskThing", Psc("HcDsDisk"));
        Assert.Contains("LooseThing", Psc("HcDsLoose"));
        Assert.Contains($"OK {disk} ← the file on disk", r);
        Assert.True(r.IndexOf($"OK {disk}", StringComparison.Ordinal) < r.IndexOf("OK scripts", StringComparison.Ordinal), r);  // rows in the order given
    }

    [Fact]
    public void AMissingFileOnDiskIsItsOwnRow()
    {
        var missing = Path.Combine(_p.Root, "gone", "HcDsGone.pex");

        var r = DecompileTools.DecompileScript(_svc, paths: new[] { missing, LooseRel }, out_path: _out);

        Assert.Contains($"ERROR {missing} — no such file: '{missing}'.", r);
        Assert.Contains("1 OK, 0 PARTIAL, 1 not decompiled", r);
    }

    [Fact]
    public void ADataPathNoProviderShipsAndAnInvalidOneAreEachTheirOwnRow()
    {
        var r = DecompileTools.DecompileScript(_svc, paths: new[] { "scripts/hcdsnope.pex", "scripts/../hcdsup.pex", LooseRel },
                                               out_path: _out);

        Assert.Contains("ERROR scripts/hcdsnope.pex — ABSENT — ", r);
        Assert.Contains("ERROR scripts/../hcdsup.pex — invalid path — ", r);
        Assert.Contains("1 OK, 0 PARTIAL, 2 not decompiled", r);
    }

    [Fact]
    public void AScriptWhoseSecondObjectsTargetExistsSaysWhatWasAlreadyWritten()
    {
        var multi = Path.Combine(_p.Root, "multi", "HcDsMultiA.pex");
        Directory.CreateDirectory(Path.GetDirectoryName(multi)!);
        PexWriter.WriteMultiObjectPex(multi, "HcDsMultiA", "HcDsMultiB");
        Directory.CreateDirectory(_out);
        File.WriteAllText(Path.Combine(_out, "HcDsMultiB.psc"), "; mine");

        var r = DecompileTools.DecompileScript(_svc, paths: new[] { multi, LooseRel }, out_path: _out);

        Assert.Contains("Already written for this script: HcDsMultiA.psc.", r);
    }

    [Fact]
    public void SelectingWithOutPathStillNeedsTheInstance()
    {
        using var unconfigured = LoadOrderService.WithInstance(null, 0, new UserConfigStore(Path.Combine(_p.Root, "user.json")));

        var r = DecompileTools.DecompileScript(unconfigured, paths: new[] { LooseRel }, out_path: _out);

        Assert.Equal(unconfigured.ConfigPromptOrNull(), r);
    }

    [Fact]
    public void ABadOutPathBesidePatchSaysPatchWasIgnored()
    {
        var r = DecompileTools.DecompileScript(_svc, provider: "Arch", patch: "Mine", out_path: "relative/psc");

        Assert.StartsWith("error:", r);
        Assert.Contains("\nnote: patch=/into= were ignored because out_path= was given, and nothing was written.", r);
    }

    [Fact]
    public void ProviderWithASourceProviderCountsNoneWonElsewhere()
    {
        // source_provider= chose every copy read, so another provider's copy is no winner to report.
        var r = DecompileTools.DecompileScript(_svc, provider: "Arch", source_provider: "Fix", out_path: _out);

        Assert.Contains("FixCopyThing", Psc("HcDsShared"));
        Assert.DoesNotContain("won by another provider", r);
    }

    [Fact]
    public void PathsTakesAFileOfPaths()
    {
        var list = _p.Scratch("list.txt", System.Text.Encoding.ASCII.GetBytes(LooseRel + "\n" + Arch1 + "\n"));

        var r = DecompileTools.DecompileScript(_svc, paths: new[] { "@" + list }, out_path: _out);

        Assert.Contains("2 OK", r);
        Assert.Contains("LooseThing", Psc("HcDsLoose"));
    }

    [Fact]
    public void JsonListsRowsInOrderUpToTheFirstThatDoesNotFit()
    {
        // The first row is the wider (its path is longer); a ceiling between the two widths lists neither.
        var wide = Path.Combine(_p.Root, new string('w', 120), "HcDsDisk.pex");
        Directory.CreateDirectory(Path.GetDirectoryName(wide)!);
        File.WriteAllBytes(wide, Pex("HcDsDisk", null, "DiskThing"));
        var o1 = Path.Combine(_p.Root, "o1");
        var o2 = Path.Combine(_p.Root, "o2");
        using var all = System.Text.Json.JsonDocument.Parse(
            DecompileTools.DecompileScript(_svc, paths: new[] { wide, LooseRel }, format: "json", out_path: o1));
        var widths = all.RootElement.GetProperty("rows").EnumerateArray().Select(e => e.GetRawText().Length).ToArray();
        Assert.True(widths[0] > widths[1] + 1, string.Join(",", widths));

        var r = DecompileTools.DecompileScript(_svc, paths: new[] { wide, LooseRel }, format: "json", out_path: o2,
                                               max_chars: 4096 + widths[0] - 1);

        using var doc = System.Text.Json.JsonDocument.Parse(r);
        Assert.Equal(0, doc.RootElement.GetProperty("rows").GetArrayLength());
        Assert.Equal(2, doc.RootElement.GetProperty("accounting").GetProperty("truncated").GetInt32());
    }

    [Fact]
    public void ABareScriptNameIsRefusedNamingItsDataPathAndTheSearch()
    {
        var r = DecompileTools.DecompileScript(_svc, paths: new[] { "hcdsloose" }, out_path: _out);

        Assert.StartsWith("error:", r);
        Assert.Contains("paths=[\"scripts/hcdsloose.pex\"]", r);
        Assert.Contains("under=[\"**/hcdsloose*.pex\"]", r);
        Assert.False(Directory.Exists(_out));
    }

    [Fact]
    public void ABareNameWithItsExtensionIsRefusedNamingItsDataPathOnce()
    {
        var r = DecompileTools.DecompileScript(_svc, paths: new[] { "hcdsloose.pex" }, out_path: _out);

        Assert.Contains("paths=[\"scripts/hcdsloose.pex\"]", r);
        Assert.Contains("under=[\"**/hcdsloose*.pex\"]", r);
    }

    [Fact]
    public void TheRetiredPexParameterIsRefusedInOneSentence()
    {
        var r = DecompileTools.DecompileScript(_svc, pex: Path.Combine(_p.Root, "x.pex"), out_path: _out);

        Assert.StartsWith("error: pex= is now paths=", r);
        Assert.Single(r.Split('\n'));
    }

    [Fact]
    public void TheRetiredAssetPathsParameterIsRefusedInOneSentence()
    {
        var old = System.Text.Json.JsonDocument.Parse("[\"scripts/hcdsloose.pex\"]").RootElement;

        var r = AssetTools.AssetStatus(_svc, asset_paths: old);

        Assert.StartsWith("error: asset_paths= is now paths=", r);
        Assert.Single(r.Split('\n'));
    }

    [Fact]
    public void AssetStatusTakesPaths()
    {
        var r = AssetTools.AssetStatus(_svc, paths: new[] { LooseRel });

        Assert.Contains("hcdsloose.pex", r);
        Assert.DoesNotContain("error:", r);
    }

    [Fact]
    public void AnExistingSourceIsThatScriptsRowAndTheRestLand()
    {
        Directory.CreateDirectory(_out);
        File.WriteAllText(Path.Combine(_out, "HcDsLoose.psc"), "; mine");

        var r = DecompileTools.DecompileScript(_svc, paths: new[] { LooseRel, Arch1 }, out_path: _out);

        Assert.Equal("; mine", Psc("HcDsLoose"));
        Assert.Contains("ArchOne", Psc("HcDsArch1"));
        Assert.Contains(@"ERROR scripts\hcdsloose.pex — '", r);
        Assert.Contains("already exists — houseCARL never overwrites a source file", r);
        Assert.Contains("1 OK, 0 PARTIAL, 1 not decompiled", r);
    }

    [Fact]
    public void LimitAndOffsetChooseWhichScriptsAreDecompiled()
    {
        var r = DecompileTools.DecompileScript(_svc, paths: new[] { LooseRel, Arch1, ArchBase }, limit: 1, offset: 1, out_path: _out);

        Assert.True(File.Exists(Path.Combine(_out, "HcDsArch1.psc")), r);
        Assert.False(File.Exists(Path.Combine(_out, "HcDsLoose.psc")), r);
        Assert.False(File.Exists(Path.Combine(_out, "HcDsArchBase.psc")), r);
        Assert.Contains("in this window of 3 selected", r);
        Assert.Contains("offset=2 for the next page", r);
    }

    [Fact]
    public void AnArchivedScriptsParentIsReadOffTheLoadOrderAndAnUnreadableOneIsNamed()
    {
        // The parent ships, unreadable, in a mod no plugin of which is the one the child's archive binds.
        PlaceInstance.Loose(_p.Mod("Fix"), @"scripts\hcdsbroken.pex", BsaBuilder.Bytes("not a pex", 16));
        PlaceInstance.Loose(_p.Mod("Fix"), @"scripts\hcdsorphan.pex", Pex("HcDsOrphan", "HcDsBroken", "OrphanThing"));
        _p.TouchModlist();

        var r = DecompileTools.DecompileScript(_svc, paths: new[] { @"scripts\hcdsorphan.pex" }, out_path: _out);

        Assert.Contains("OrphanThing", Psc("HcDsOrphan"));
        Assert.Contains(@"1 parent script(s) could not be read off the load order: 'scripts\HcDsBroken.pex'", r);
        Assert.Contains("the .pex files this hierarchy reads were not all read (", r);   // the many-script wording
        Assert.Contains("plus what these scripts and the other .pex files that could be read declare", r);
    }

    [Fact]
    public void AModParentOnlyAPscHeaderNamesIsReadLooseAndArchivedToTheVanillaRoot()
    {
        // MCM Helper's shape: kid -> HcDsMcmBase (loose .pex, named as a parent only by another mod's .psc header)
        // -> HcDsSkiBase (in an archive) -> Quest -> Form (the baseline). The kid upcasts a parameter to Form.
        PlaceInstance.Loose(_p.Mod("Fix"), @"Source\Scripts\HcDsOtherKid.psc",
            System.Text.Encoding.ASCII.GetBytes("ScriptName HcDsOtherKid extends HcDsMcmBase\n"));
        PlaceInstance.Loose(_p.Mod("Fix"), @"scripts\hcdsmcmkid.pex", UpcastPex("HcDsMcmKid", "HcDsMcmBase", "Form"));
        PlaceInstance.Loose(_p.Mod("Fix"), @"scripts\hcdsmcmbase.pex", Pex("HcDsMcmBase", "HcDsSkiBase", "McmThing"));
        File.WriteAllBytes(Path.Combine(_p.Mod("Arch"), "Dummy - Textures.bsa"),
            BsaBuilder.Build(105, BsaBuilder.HasFolderNames | BsaBuilder.HasFileNames,
                new[] { ("scripts", new[] { ("hcdsskibase.pex", Pex("HcDsSkiBase", "Quest", "SkiThing")) }) }));
        _p.TouchModlist();

        var r = DecompileTools.DecompileScript(_p.Open(), paths: new[] { @"scripts\hcdsmcmkid.pex" }, out_path: _out);

        Assert.Contains("Take(k)", Psc("HcDsMcmKid"));
        Assert.DoesNotContain(" as Form", Psc("HcDsMcmKid"), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("could not be read", r);
    }

    /// <summary>A .pex named <paramref name="name"/> whose <c>Give(name k)</c> passes <c>k</c> to
    /// <c>Take(<paramref name="ancestor"/> f)</c>: the upcast is implicit only when the hierarchy reaches the ancestor.</summary>
    byte[] UpcastPex(string name, string parent, string ancestor)
    {
        static PexObjectVariableData Id(string n)
            => new() { VariableType = VariableType.Identifier, StringValue = n };
        var give = new PexObjectFunction { ReturnTypeName = "None", DocString = "" };
        give.Parameters.Add(new() { TypeName = name, Name = "k" });
        give.Locals.Add(new() { TypeName = ancestor, Name = "::temp0" });
        void Ins(InstructionOpcode op, params PexObjectVariableData[] args)
        {
            var ins = new PexObjectFunctionInstruction { OpCode = op };
            foreach (var a in args) ins.Arguments.Add(a);
            give.Instructions.Add(ins);
        }
        Ins(InstructionOpcode.CAST, Id("::temp0"), Id("k"));
        Ins(InstructionOpcode.CALLMETHOD, Id("Take"), Id("self"), Id("::NoneVar"),
            new() { VariableType = VariableType.Integer, IntValue = 1 }, Id("::temp0"));
        Ins(InstructionOpcode.RETURN, new PexObjectVariableData { VariableType = VariableType.Null });
        var take = new PexObjectFunction { ReturnTypeName = "None", DocString = "" };
        take.Parameters.Add(new() { TypeName = ancestor, Name = "f" });

        var state = new PexObjectState { Name = "" };
        state.Functions.Add(new() { FunctionName = "Give", Function = give });
        state.Functions.Add(new() { FunctionName = "Take", Function = take });
        var obj = new PexObject { Name = name, ParentClassName = parent, DocString = "", AutoStateName = "" };
        obj.States.Add(state);
        var pex = new PexFile(GameCategory.Skyrim)
            { MajorVersion = 3, MinorVersion = 2, GameId = 1, SourceFileName = name + ".psc", Username = "hc", MachineName = "ci" };
        pex.Objects.Add(obj);
        var path = Path.Combine(_p.Root, "pex", name + "-upcast.pex");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        pex.WritePexFile(path, GameCategory.Skyrim);
        return File.ReadAllBytes(path);
    }

    [Fact]
    public void AParentTheBaselineAlreadyNamesIsNotRead()
    {
        // Quest's parent is Form in the vanilla baseline; Form itself has none, and the order ships a form.pex in a
        // second archive, which reading the chain to its root would open.
        File.WriteAllBytes(Path.Combine(_p.Mod("Arch"), "Dummy - Textures.bsa"),
            BsaBuilder.Build(105, BsaBuilder.HasFolderNames | BsaBuilder.HasFileNames,
                new[] { ("scripts", new[] { ("form.pex", Pex("Form", null, "FormThing")) }) }));
        PlaceInstance.Loose(_p.Mod("Fix"), @"scripts\hcdsquestkid.pex", Pex("HcDsQuestKid", "Quest", "KidThing"));
        _p.TouchModlist();
        var svc = _p.Open();
        var assets = ((ILoadOrderHost)svc).Assets;
        svc.AssetStatus(new[] { LooseRel });                                 // builds the view first
        var before = assets.ArchiveTableReadCount;

        DecompileTools.DecompileScript(svc, paths: new[] { @"scripts\hcdsquestkid.pex", Arch1 }, out_path: _out);

        Assert.Equal(1, assets.ArchiveTableReadCount - before);              // Dummy.bsa, for Arch1; never the second
    }

    [Fact]
    public void AWindowScriptThatDeclaresNoParentIsNotReadAgainAsOne()
    {
        // The winning copy of a parentless script is an unreadable entry in a second archive; source_provider= reads
        // the disabled mod's good copy, which is the only read the call needs.
        File.WriteAllBytes(Path.Combine(_p.Mod("Arch"), "Dummy - Textures.bsa"),
            BsaBuilder.Build(105, BsaBuilder.HasFolderNames | BsaBuilder.HasFileNames,
                new[] { ("scripts", new[] { ("hcdsoff.pex", BsaBuilder.Bytes("not a pex", 16)) }) }));
        _p.TouchModlist();
        var svc = _p.Open();
        var assets = ((ILoadOrderHost)svc).Assets;
        svc.AssetStatus(new[] { LooseRel });                                 // builds the view first
        var before = assets.ArchiveTableReadCount;

        var r = DecompileTools.DecompileScript(svc, paths: new[] { OffRel }, source_provider: "Off", out_path: _out);

        Assert.Contains("OffThing", Psc("HcDsOff"));
        Assert.DoesNotContain("could not be read", r);
        Assert.Equal(0, assets.ArchiveTableReadCount - before);
    }

    [Fact]
    public void ACallReadsEachArchiveOnce()
    {
        var assets = ((ILoadOrderHost)_svc).Assets;
        var before = assets.ArchiveTableReadCount;

        DecompileTools.DecompileScript(_svc, provider: "Arch", out_path: _out);

        Assert.Equal(1, assets.ArchiveTableReadCount - before);
    }

    [Fact]
    public void AFileOnDiskAloneAnswersAsTheOneFileToolDid()
    {
        var disk = _p.Scratch("HcDsDisk.pex", Pex("HcDsDisk", null, "DiskThing"));

        var r = DecompileTools.DecompileScript(_svc, paths: new[] { disk }, out_path: _out);

        // The whole answer, as main's one-file tool gave it.
        Assert.Equal($"decompile OK: HcDsDisk.pex → {Path.Combine(_out, "HcDsDisk.psc")}\n" +
                     "0 function(s), all structured clean.\n" +
                     "known format losses (every decompiler): a parameter default is not in the .pex, so it survives only " +
                     "where a call in the same script omitted the argument — a parameter with no such call comes back with " +
                     "its default missing; comments/layout are gone (docstrings survive).\n" +
                     "the .psc is in the folder you named with out_path= (path above) — review it, edit it, and recompile " +
                     "with housecarl_compile_script.", r);
    }

    [Fact]
    public void SourceProviderBesideAFileOnDiskSaysTheFileIsReadAsItself()
    {
        var disk = _p.Scratch("HcDsDisk.pex", Pex("HcDsDisk", null, "DiskThing"));

        var r = DecompileTools.DecompileScript(_svc, paths: new[] { disk }, source_provider: "Arch", out_path: _out);

        Assert.Contains("DiskThing", Psc("HcDsDisk"));
        Assert.Contains("a file on disk is read as itself", r);
    }

    [Fact]
    public void CountsOnlyListsNoRowsAndStillWritesEveryScript()
    {
        var r = DecompileTools.DecompileScript(_svc, provider: "Arch", counts_only: true, out_path: _out);

        Assert.StartsWith("decompile: 4 script(s), provider \"Arch\" — 4 OK", r);
        Assert.DoesNotContain(" OK scripts", r);
        Assert.Equal(4, Directory.GetFiles(_out, "*.psc").Length);
    }

    [Fact]
    public void MaxCharsCutsTheListingAndEveryScriptIsStillWritten()
    {
        var r = DecompileTools.DecompileScript(_svc, provider: "Arch", max_chars: 1500, out_path: _out);

        Assert.Equal(4, Directory.GetFiles(_out, "*.psc").Length);
        Assert.Contains("whatever max_chars cuts from the listing", r);
        Assert.Matches(@"\[accounting\] total=4 rendered=[0-3] ", r);
    }

    [Fact]
    public void AMaxCharsCutAdvisesNoPageOverScriptsAlreadyWritten()
    {
        var text = DecompileTools.DecompileScript(_svc, provider: "Arch", max_chars: 1500, out_path: _out);
        var json = DecompileTools.DecompileScript(_svc, provider: "Arch", max_chars: 1500, format: "json",
                                                  out_path: Path.Combine(_p.Root, "out2"));

        Assert.Matches(@"\[accounting\] total=4 rendered=[0-3] .* remaining=0 ", text);
        Assert.DoesNotContain("re-call", text);
        Assert.DoesNotContain("offset=1", text);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("accounting").GetProperty("truncated").GetInt32() > 0, json);
        Assert.Equal(0, doc.RootElement.GetProperty("accounting").GetProperty("remaining").GetInt32());
        Assert.False(doc.RootElement.TryGetProperty("next_offset", out _), json);
    }

    [Fact]
    public void TheNextPageAdviceLandsInThePatchThisWindowWrote()
    {
        var mods = Directory.GetDirectories(_p.Mods).ToHashSet();

        var first = DecompileTools.DecompileScript(_svc, paths: new[] { LooseRel, Arch1, ArchBase }, limit: 1);
        var folder = Path.GetFileName(Assert.Single(Directory.GetDirectories(_p.Mods), d => !mods.Contains(d)));
        // into= names the patch by its stem, the form into= resolves on its canonical path.
        Assert.StartsWith("houseCARL - ", folder);
        var stem = folder["houseCARL - ".Length..];
        Assert.Contains($"re-call with into=\"{stem}\" limit=1 offset=1 for the next page.", first);

        var json = DecompileTools.DecompileScript(_svc, paths: new[] { LooseRel, Arch1, ArchBase }, limit: 1, offset: 1,
                                                  into: stem, format: "json");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(stem, doc.RootElement.GetProperty("next_into").GetString());
        Assert.Equal(2, doc.RootElement.GetProperty("next_offset").GetInt32());
        Assert.Single(Directory.GetDirectories(_p.Mods), d => !mods.Contains(d));
        Assert.True(File.Exists(Path.Combine(_p.Mods, folder, "Source", "Scripts", "HcDsArch1.psc")), json);
    }

    [Fact]
    public void TheJsonNextPageNamesTheFreshPatchByItsStem()
    {
        var mods = Directory.GetDirectories(_p.Mods).ToHashSet();

        var json = DecompileTools.DecompileScript(_svc, paths: new[] { LooseRel, Arch1 }, limit: 1, format: "json");

        var folder = Path.GetFileName(Assert.Single(Directory.GetDirectories(_p.Mods), d => !mods.Contains(d)));
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(folder["houseCARL - ".Length..], doc.RootElement.GetProperty("next_into").GetString());
    }

    [Fact]
    public void TheNextPageAdviceKeepsTheLaneAsGivenWhenNothingLanded()
    {
        PlaceInstance.Loose(_p.Mod("Fix"), @"scripts\hcdsbroken.pex", BsaBuilder.Bytes("not a pex", 16));
        _p.TouchModlist();
        var absent = new[] { "scripts/hcdsnope1.pex", "scripts/hcdsnope2.pex" };

        var removed = DecompileTools.DecompileScript(_svc, paths: new[] { @"scripts\hcdsbroken.pex", LooseRel }, limit: 1);
        var into = DecompileTools.DecompileScript(_svc, paths: absent, limit: 1, into: "houseCARL_Scripts");
        var patch = DecompileTools.DecompileScript(_svc, paths: absent, limit: 1, patch: "Mine");

        Assert.Contains("re-call with limit=1 offset=1 for the next page.", removed);   // its fresh folder was removed
        Assert.Contains("re-call with into=\"houseCARL_Scripts\" limit=1 offset=1 for the next page.", into);
        Assert.Contains("re-call with patch=\"Mine\" limit=1 offset=1 for the next page.", patch);
    }

    [Fact]
    public void TheNextPageAdviceUnderOutPathNamesTheSameFolder()
    {
        var text = DecompileTools.DecompileScript(_svc, paths: new[] { LooseRel, Arch1, ArchBase }, limit: 1, out_path: _out);
        var json = DecompileTools.DecompileScript(_svc, paths: new[] { LooseRel, Arch1, ArchBase }, limit: 1, offset: 1,
                                                  format: "json", out_path: _out);

        Assert.Contains($"re-call with out_path=\"{_out}\" limit=1 offset=1 for the next page.", text);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(_out, doc.RootElement.GetProperty("next_out_path").GetString());
    }

    [Fact]
    public void NoSelectorIsRefused()
    {
        var r = DecompileTools.DecompileScript(_svc, out_path: _out);

        Assert.StartsWith("error: no script given", r);
    }

    [Fact]
    public void AnExplicitPathThatIsNotAScriptIsItsOwnRow()
    {
        var r = DecompileTools.DecompileScript(_svc, paths: new[] { "scripts/hcdsloose.psc", LooseRel }, out_path: _out);

        Assert.Contains(@"ERROR scripts/hcdsloose.psc — 'scripts/hcdsloose.psc' is not a .pex compiled script.", r);
        Assert.Contains("1 OK, 0 PARTIAL, 1 not decompiled", r);
    }

    [Fact]
    public void AFreshPatchFolderNothingLandedInIsRemoved()
    {
        PlaceInstance.Loose(_p.Mod("Fix"), @"scripts\hcdsbroken.pex", BsaBuilder.Bytes("not a pex", 16));
        _p.TouchModlist();
        var before = Directory.GetDirectories(_p.Mods).Length;

        var r = DecompileTools.DecompileScript(_svc, paths: new[] { @"scripts\hcdsbroken.pex" });

        Assert.Contains("0 OK, 0 PARTIAL, 1 not decompiled", r);
        Assert.Equal(before, Directory.GetDirectories(_p.Mods).Length);
    }

    [Fact]
    public void ToFileIsRefusedNamingTheOutputFolder()
    {
        var r = DecompileTools.DecompileScript(_svc, provider: "Arch", to_file: Path.Combine(_p.Root, "x.jsonl"));

        Assert.StartsWith("error: to_file=", r);
        Assert.Contains("out_path=", r);
    }

    [Fact]
    public void JsonCarriesTheRowsAndTheAccounting()
    {
        var r = DecompileTools.DecompileScript(_svc, paths: new[] { LooseRel }, format: "json", out_path: _out);

        using var doc = System.Text.Json.JsonDocument.Parse(r);
        var row = doc.RootElement.GetProperty("rows")[0];
        Assert.Equal("ok", row.GetProperty("status").GetString());
        Assert.Equal("Arch", row.GetProperty("provider").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("accounting").GetProperty("total").GetInt32());
    }
}
