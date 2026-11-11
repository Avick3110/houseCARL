using System.Text.Json;
using HousecarlGenerator;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary><c>housecarl_nif_inspect</c> on the shared selection (#1081's batch half): <c>paths=</c>, <c>under=</c> and
/// <c>provider=</c> pick meshes as asset_status picks files, each read loose or out of its archive; a full path is a
/// file on disk, read as itself; the uniform transport pages, counts, writes json and spills whole past the ceiling;
/// the retired names are refused in one sentence. The world: the mod Arch ships Dummy.bsa (bound to its Dummy.esp)
/// holding two meshes, a loose mesh and a loose texture; Fix's loose copy of the shared mesh wins over Arch's archived one.</summary>
[Trait("tier", "integration")]
public sealed class NifSelectionTests : IDisposable
{
    const string Arch1 = @"meshes\hcns\arch1.nif", LooseRel = @"meshes\hcns\loose.nif", Shared = @"meshes\hcns\shared.nif";

    readonly PlaceInstance _p = new();
    readonly LoadOrderService _svc;

    public NifSelectionTests()
    {
        var bsa = Path.Combine(_p.Mod("Arch"), "Dummy.bsa");
        File.WriteAllBytes(bsa, BsaBuilder.Build(105, BsaBuilder.HasFolderNames | BsaBuilder.HasFileNames, new[]
        {
            (@"meshes\hcns", new[] { ("arch1.nif", NifSetMeshes.Guard()), ("shared.nif", NifSetMeshes.Shader()) }),
        }));
        PlaceInstance.Loose(_p.Mod("Arch"), LooseRel, NifSetMeshes.Textured());
        PlaceInstance.Loose(_p.Mod("Arch"), @"textures\hcns\t.dds", BsaBuilder.Bytes("HCNS-DDS", 16));
        PlaceInstance.Loose(_p.Mod("Fix"), Shared, NifSetMeshes.Guard());
        _p.ProfileWithDummy("Arch", "+Fix", "+Arch");
        _svc = _p.Open();
    }

    public void Dispose() => _p.Dispose();

    static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    /// <summary>The block one mesh's path line opens, up to the next mesh or the notes.</summary>
    static string Block(string text, string path)
    {
        int at = text.IndexOf("\n" + path + "\n", StringComparison.OrdinalIgnoreCase);
        Assert.True(at >= 0, text);
        int end = text.IndexOf("\nmeshes\\", at + path.Length + 2, StringComparison.OrdinalIgnoreCase);
        int notes = text.IndexOf("\nnote: ", at, StringComparison.Ordinal);
        if (end < 0 || (notes >= 0 && notes < end)) end = notes;
        return end < 0 ? text[at..] : text[at..end];
    }

    [Fact]
    public void AProviderReadsItsLooseAndArchivedMeshesInOneCall()
    {
        var r = NifTools.NifInspect(_svc, provider: "Arch");

        Assert.Contains("(3 meshes, provider \"Arch\")", r);
        Assert.Contains("read from: \"Dummy.bsa\" (BSA)", Block(r, Arch1));             // out of the archive
        Assert.Contains("'GuardShape'", Block(r, Arch1));
        Assert.Contains("read from: \"Arch\" (loose)", Block(r, LooseRel));
        Assert.Contains("'FemaleHead'", Block(r, LooseRel));
        // provider= picks which; the winner is read, and the note names the knob that reads Arch's own copy.
        Assert.Contains("read from: \"Fix\" (loose)", Block(r, Shared));
        Assert.Contains("1 mesh(es) provider 'Arch' ships are won by another provider, and the winning copy was read; source_provider=\"Arch\" reads its own copies.", r);
        // Dummy.esp, Dummy.bsa and the texture are files Arch provides, and not meshes.
        Assert.Contains("3 file(s) the selectors matched are not .nif meshes and were left out", r);
        Assert.Contains("[accounting] total=3 rendered=3 skipped=0 capped=0 truncated=0", r);
    }

    /// <summary>An explicit path provider= did not pick is not counted as one of its meshes another provider wins.</summary>
    [Fact]
    public void AProviderBesideAnExplicitPathCountsNoMeshWonElsewhere()
    {
        var r = NifTools.NifInspect(_svc, provider: "Arch", paths: new[] { Shared });
        var census = NifTools.NifInspect(_svc, provider: "Arch", paths: new[] { Shared }, counts_only: true);

        Assert.Contains("read from: \"Fix\" (loose)", Block(r, Shared));
        Assert.DoesNotContain("won by another provider", r);
        Assert.DoesNotContain("won by another provider", census);
    }

    /// <summary>provider= reaches the same with a file on disk as with a Data path beside it: nothing more.</summary>
    [Fact]
    public void AProviderBesideAFileOnDiskSelectsNothingMoreAsBesideADataPath()
    {
        var disk = _p.Scratch("hcns-beside.nif", NifSetMeshes.Guard());

        var besideDisk = NifTools.NifInspect(_svc, provider: "Arch", paths: new[] { disk });
        var besideData = NifTools.NifInspect(_svc, provider: "Arch", paths: new[] { Arch1 });

        Assert.Contains("[accounting] total=1 rendered=1", besideDisk);
        Assert.Contains("[accounting] total=1 rendered=1", besideData);
    }

    [Fact]
    public void SourceProviderReadsTheCopyThatDoesNotWin()
    {
        var r = NifTools.NifInspect(_svc, paths: new[] { Shared }, source_provider: "Arch");

        Assert.Contains("read from: \"Dummy.bsa\" (BSA)", r);
        Assert.Contains("'LitShape'", r);                                                // Arch's archived copy, not Fix's
        Assert.DoesNotContain("'GuardShape'", r);
    }

    [Fact]
    public void ProviderAndSourceProviderTogetherReadThatModsOwnCopies()
    {
        var r = NifTools.NifInspect(_svc, provider: "Arch", source_provider: "Arch");

        Assert.Contains("'LitShape'", Block(r, Shared));
        Assert.DoesNotContain("won by another provider", r);
    }

    [Fact]
    public void UnderSelectsMeshesAcrossLooseAndArchivesAndCountsTheRest()
    {
        var r = NifTools.NifInspect(_svc, under: new[] { "**/hcns/**" });

        Assert.Contains("(3 meshes)", r);
        Assert.Contains(Arch1, r, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(LooseRel, r, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1 file(s) the selectors matched are not .nif meshes", r);
    }

    [Fact]
    public void AFileOnDiskIsReadAsThatFileBesideTheSelection()
    {
        var disk = _p.Scratch("hcns-disk.nif", NifSetMeshes.Shader());

        var r = NifTools.NifInspect(_svc, paths: new[] { disk, Arch1 });

        var block = r[r.IndexOf("\n" + disk + "\n", StringComparison.Ordinal)..r.IndexOf("\n" + Arch1, StringComparison.OrdinalIgnoreCase)];
        Assert.Contains("  read from: the file on disk\n", block);
        Assert.DoesNotContain("providers", block);                                       // no winner, no providers
        Assert.Contains("'LitShape'", block);
        Assert.Contains("[accounting] total=2 rendered=2", r);
    }

    [Fact]
    public void SourceProviderBesideAFileOnDiskSaysTheFileIsReadAsItself()
    {
        var disk = _p.Scratch("hcns-src.nif", NifSetMeshes.Guard());

        var r = NifTools.NifInspect(_svc, paths: new[] { disk, Shared }, source_provider: "Arch");

        Assert.Contains("note: source_provider= picks whose copy of a Data path is read; a file on disk is read as itself.", r);
        Assert.Contains("'LitShape'", Block(r, Shared));
    }

    [Fact]
    public void BlankEntriesAloneAreRefusedNamingTheList()
    {
        Assert.StartsWith("error: paths contains only empty/blank entries.", NifTools.NifInspect(_svc, paths: new[] { " ", "" }));
        Assert.StartsWith("error: formids contains only empty/blank entries.", NifTools.NifInspect(_svc, formids: new[] { " " }));
    }

    [Fact]
    public void ToFileRefusesCountsOnlyAndOffset()
    {
        var file = Path.Combine(_p.Root, "hcns-refused.jsonl");
        Assert.Equal(Artifacts.CountsOnlyWithToFile, NifTools.NifInspect(_svc, provider: "Arch", to_file: file, counts_only: true));
        Assert.StartsWith("error: to_file= captures the COMPLETE result", NifTools.NifInspect(_svc, provider: "Arch", to_file: file, offset: 1));
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void AFileOnDiskAloneReadsWithNoInstance()
    {
        var disk = _p.Scratch("hcns-alone.nif", NifSetMeshes.Guard());
        using var unconfigured = LoadOrderService.WithInstance(null, 0, new UserConfigStore(Path.Combine(_p.Root, "none.json")));

        var r = NifTools.NifInspect(unconfigured, paths: new[] { disk });

        Assert.Contains("read from: the file on disk", r);
        Assert.Contains("'GuardShape'", r);
    }

    /// <summary>A full path into the mods tree, alone and without sections=paths, still names its load-order address,
    /// on a mesh that reads and on one that does not, and the selection captures no view for it.</summary>
    [Fact]
    public void AModsTreePathAloneNamesItsLoadOrderAddress()
    {
        var good = Path.Combine(_p.Mod("Arch"), LooseRel);
        PlaceInstance.Loose(_p.Mod("Fix"), @"meshes\hcns\bad.nif", BsaBuilder.Bytes("HCNS-NOT-A-NIF", 32));
        var bad = Path.Combine(_p.Mod("Fix"), @"meshes\hcns\bad.nif");

        var ok = NifTools.NifInspect(_svc, paths: new[] { good });
        var failed = NifTools.NifInspect(_svc, paths: new[] { bad });

        Assert.Contains("'FemaleHead'", ok);
        Assert.Contains($"note: this file is in the mod folder 'Arch' and was read as the file on disk; paths=[\"{LooseRel}\"] source_provider=\"Arch\" reads that copy through the load order.", ok);
        Assert.Contains($"note: this file is in the mod folder 'Fix' and was read as the file on disk; paths=[\"meshes\\hcns\\bad.nif\"] source_provider=\"Fix\" reads that copy through the load order.", failed);
        Assert.Null(_svc.SelectMeshes(new[] { (good, true) }, Array.Empty<string>(), null, Array.Empty<FaceGenSeed>(), null, needView: false).Captured);
    }

    /// <summary>The json row and the to_file= row of a mods-tree file carry the address the text answer gives.</summary>
    [Fact]
    public void JsonAndToFileRowsOfAModsTreeFileCarryItsAddress()
    {
        var good = Path.Combine(_p.Mod("Arch"), LooseRel);
        var file = Path.Combine(_p.Root, "hcns-disknote.jsonl");
        var address = $"paths=[\"{LooseRel}\"] source_provider=\"Arch\" reads that copy through the load order.";

        var row = Json(NifTools.NifInspect(_svc, paths: new[] { good }, format: "json")).GetProperty("rows")[0];
        NifTools.NifInspect(_svc, paths: new[] { good }, to_file: file);

        Assert.EndsWith(address, row.GetProperty("disk_note").GetString());
        Assert.EndsWith(address, Json(File.ReadAllLines(file)[1]).GetProperty("disk_note").GetString());
    }

    /// <summary>Pins unchanged behaviour: a file on disk that fails is on_disk from its own data, beside a Data path that is not.</summary>
    [Fact]
    public void AFailedFileOnDiskRowIsOnDiskInJson()
    {
        var gone = Path.Combine(_p.Root, "hcns-gone-json.nif");

        var rows = Json(NifTools.NifInspect(_svc, paths: new[] { gone, Arch1 }, format: "json")).GetProperty("rows");

        Assert.Equal("error", rows[0].GetProperty("status").GetString());
        Assert.True(rows[0].GetProperty("on_disk").GetBoolean());
        Assert.False(rows[1].GetProperty("on_disk").GetBoolean());
    }

    [Fact]
    public void AMissingFileOnDiskIsItsOwnRow()
    {
        var gone = Path.Combine(_p.Root, "hcns-gone.nif");

        var r = NifTools.NifInspect(_svc, paths: new[] { gone, Arch1 });

        Assert.Contains($"no such file: '{gone}'.", r);
        Assert.Contains("'GuardShape'", Block(r, Arch1));
    }

    [Fact]
    public void ThePathsSectionOfAFileOnDiskResolvesItsLinksAgainstTheLoadOrder()
    {
        var disk = _p.Scratch("hcns-links.nif", NifSetMeshes.Guard());

        var r = NifTools.NifInspect(_svc, paths: new[] { disk }, sections: "paths");

        Assert.Contains("links read from the file on disk; each Data path's standing is the load order's", r);
        Assert.Contains("data path: ", r);
    }

    [Fact]
    public void TheRetiredMeshPathsParameterIsRefusedInOneSentence()
    {
        var r = NifTools.NifInspect(_svc, mesh_paths: Json("[\"meshes\\\\hcns\\\\arch1.nif\"]"));

        Assert.Equal("error: mesh_paths= is now paths= — pass the same list as paths=.", r);
    }

    [Fact]
    public void TheRetiredNpcParameterIsRefusedNamingFormIds()
    {
        var r = NifTools.NifInspect(_svc, npc: Json("[\"000801:Test.esp\"]"));

        Assert.Equal("error: npc= is now formids= — pass the same FormIDs as formids=.", r);
    }

    [Fact]
    public void AProviderNotInTheOrderIsRefused()
        => Assert.StartsWith("error: provider 'NoSuchMod' is not in the active load order", NifTools.NifInspect(_svc, provider: "NoSuchMod"));

    [Fact]
    public void ASelectionPastThePathBoundIsRefusedBeforeAnyMeshIsRead()
    {
        int prior = _svc.AssetArea.MaxAssetPaths;
        _svc.AssetArea.MaxAssetPaths = 2;
        try
        {
            var r = NifTools.NifInspect(_svc, under: new[] { "**/hcns/**" });
            Assert.StartsWith("error: the selection names at least 3 paths, past the 2-path bound one call is given, so nothing was read", r);
        }
        finally { _svc.AssetArea.MaxAssetPaths = prior; }
    }

    [Fact]
    public void LimitAndOffsetWindowTheSelection()
    {
        var r = NifTools.NifInspect(_svc, provider: "Arch", limit: 1, offset: 1);

        Assert.Contains("(1 mesh in this window of 3 selected, provider \"Arch\")", r);
        Assert.Contains(LooseRel, r, StringComparison.OrdinalIgnoreCase);              // sorted: arch1, loose, shared
        Assert.DoesNotContain(Arch1, r, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[accounting] total=3 rendered=1 skipped=1 capped=1", r);
        Assert.Contains("re-call with limit=1 offset=2 for the next page.", r);
    }

    [Fact]
    public void CountsOnlyCountsTheWholeSelectionAndListsNoMesh()
    {
        var r = NifTools.NifInspect(_svc, provider: "Arch", counts_only: true, limit: 1);

        Assert.Contains("(3 meshes selected, provider \"Arch\")", r);
        Assert.Contains("census: read=3 not read=0 (absent=0)", r);
        Assert.Contains("read from: loose=2 BSA=1 file on disk=0", r);
        Assert.Contains("streams: Skyrim SE=3 other=0; with unknown blocks=0", r);
        Assert.Contains("1 mesh(es) provider 'Arch' ships are won by another provider", r);
        Assert.DoesNotContain("GuardShape", r);
    }

    [Fact]
    public void CountsOnlyRefusesOffset()
        => Assert.StartsWith("error:", NifTools.NifInspect(_svc, provider: "Arch", counts_only: true, offset: 1));

    [Fact]
    public void JsonCarriesOneRowPerMeshAndTheAccounting()
    {
        var doc = Json(NifTools.NifInspect(_svc, provider: "Arch", format: "json", sections: "shapes"));

        var rows = doc.GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal(3, rows.Count);
        var arch = rows.Single(r => r.GetProperty("path").GetString()!.EndsWith("arch1.nif", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("BSA", arch.GetProperty("read_from").GetProperty("kind").GetString());
        Assert.Equal("GuardShape", arch.GetProperty("shapes")[0].GetString());
        Assert.Contains("--- shapes (", arch.GetProperty("sections").GetString());
        Assert.Equal(3, doc.GetProperty("accounting").GetProperty("total").GetInt32());
        Assert.False(doc.TryGetProperty("spilled", out _));
    }

    [Fact]
    public void ToFileWritesEveryMeshAndRendersTheManifest()
    {
        var file = Path.Combine(_p.Root, "hcns-out.jsonl");

        var r = NifTools.NifInspect(_svc, provider: "Arch", to_file: file, limit: 1);

        Assert.Contains("written at your request (to_file=)", r);
        var lines = File.ReadAllLines(file);
        Assert.Equal(4, lines.Length);                                                   // the manifest, then every mesh: limit= does not narrow it
        Assert.Equal("path", Json(lines[0]).GetProperty("identity").GetString());
        // It re-enters through paths=@file, as asset_status's artifact does.
        Assert.Contains("[accounting] total=3 rendered=3", NifTools.NifInspect(_svc, paths: new[] { "@" + file }));
    }

    [Fact]
    public void AnAnswerPastTheCeilingIsSpilledWholeAndTheFileIsNamed()
    {
        var r = NifTools.NifInspect(_svc, provider: "Arch", sections: "all", max_chars: 3000);

        Assert.True(r.Length <= 3000, $"{r.Length} chars:\n{r}");
        Assert.Contains("spilled: complete result (3 rows) -> ", r);
        var path = r[(r.IndexOf("spilled: complete result (3 rows) -> ", StringComparison.Ordinal) + 37)..].Split('\n')[0];
        var lines = File.ReadAllLines(path);
        Assert.Equal(4, lines.Length);
        // Every mesh's sections are whole in the file, the ones the inline answer left out too.
        Assert.All(lines.Skip(1), l => Assert.Contains("--- node tree (", Json(l).GetProperty("sections").GetString()));
        Assert.Contains("[accounting] total=3", r);
    }

    /// <summary>A spill of one window of the selection names that window in its query echo.</summary>
    [Fact]
    public void ASpilledWindowNamesItselfInTheManifest()
    {
        var r = NifTools.NifInspect(_svc, provider: "Arch", sections: "all", limit: 2, max_chars: 1500);

        const string spilled = "spilled: the returned WINDOW (2 rows of 3 total matches) -> ";
        Assert.Contains(spilled, r);
        var path = r[(r.IndexOf(spilled, StringComparison.Ordinal) + spilled.Length)..].Split('\n')[0];
        var query = Json(File.ReadAllLines(path)[0]).GetProperty("query");
        Assert.Equal("window: rows 1–2 of 3 (limit=2, offset=0)", query.GetProperty("window").GetString());
    }

    /// <summary>Files on disk and Data paths are rows in the order paths= gives them, interleaved.</summary>
    [Fact]
    public void FilesOnDiskAndDataPathsKeepThePathsOrder()
    {
        var disk = _p.Scratch("hcns-order.nif", NifSetMeshes.Guard());

        var rows = Json(NifTools.NifInspect(_svc, paths: new[] { Arch1, disk, LooseRel }, format: "json")).GetProperty("rows");

        Assert.Equal(new[] { Arch1, disk, LooseRel }, rows.EnumerateArray().Select(x => x.GetProperty("path").GetString()!).ToArray(),
                     StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void JsonPastTheCeilingIsSpilledWhole()
    {
        var doc = Json(NifTools.NifInspect(_svc, provider: "Arch", sections: "all", format: "json", max_chars: 5000));

        Assert.True(doc.GetProperty("rows").GetArrayLength() < 3);
        Assert.Equal(3, doc.GetProperty("spilled").GetProperty("row_count").GetInt32());
        Assert.True(File.Exists(doc.GetProperty("spilled").GetProperty("path").GetString()));
    }

    [Fact]
    public void ACallReadsEachArchiveOnce()
    {
        var assets = ((ILoadOrderHost)_svc).Assets;
        var before = assets.ArchiveTableReadCount;

        NifTools.NifInspect(_svc, provider: "Arch", source_provider: "Arch");

        Assert.Equal(1, assets.ArchiveTableReadCount - before);
    }

    [Fact]
    public void NifSetRefusesASecondPathInOneSentence()
    {
        var r = NifTools.NifSet(_svc, op: "set_scale", target: "GuardShape", paths: new[] { Arch1, LooseRel }, scale: "2");

        Assert.Equal($"error: paths names 2 meshes, and {ToolNames.NifSet} edits one per call — pass one, e.g. paths=[\"{Arch1}\"], and call again for each of the others.", r);
    }

    [Fact]
    public void NifSetRefusesTheRetiredMeshPathInOneSentence()
    {
        var r = NifTools.NifSet(_svc, op: "set_scale", target: "GuardShape", mesh_path: Json("\"meshes\\\\hcns\\\\arch1.nif\""), scale: "2");

        Assert.Equal("error: mesh_path= is now paths= — pass paths=[\"<the mesh path>\"].", r);
    }

    [Fact]
    public void NifSetWithNoPathIsRefusedNamingPaths()
        => Assert.StartsWith("error: paths is empty.", NifTools.NifSet(_svc, op: "set_scale", target: "GuardShape", scale: "2"));
}
