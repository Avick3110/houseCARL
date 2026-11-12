using System.Text.Json;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>exclude= on the shared file selection (#1319): a walk's to_file= output re-enters as a path list, and
/// "what a mod ships that the walk does not reach" is two calls, the walk to a file and the provider minus that file, on
/// asset_status, nif_inspect and decompile_script alike.</summary>
[Trait("tier", "unit")]
public sealed class FileExclusionTests : IClassFixture<AssetWalkWorld>
{
    readonly AssetWalkWorld _w;
    public FileExclusionTests(AssetWalkWorld w) => _w = w;

    const string Mod = "Cape";
    const int Big = 400_000;

    string File(string tag) => Path.Combine(_w.Root, tag + "-" + Guid.NewGuid().ToString("N") + ".jsonl");

    /// <summary>The walk from the cape mesh to a file: the mesh, its texture and its SMP XML, all the cape mod's.</summary>
    string CapeWalk()
    {
        var f = File("reached");
        var r = AssetTools.AssetStatus(_w.Svc, paths: new[] { AssetWalkWorld.CapeNif }, walk: Walks.On, source_provider: Mod, to_file: f);
        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), r);
        return f;
    }

    static string[] Rows(string file) => System.IO.File.ReadAllLines(file).Skip(1)
        .Select(l => JsonDocument.Parse(l).RootElement.GetProperty("path").GetString()!).ToArray();

    static string Fold(string p) => p.Replace('/', '\\').ToLowerInvariant();

    /// <summary>The json rows' paths of an asset_status call, and its selector notes.</summary>
    static (string[] Paths, string[] Notes) Json(string response)
    {
        using var doc = JsonDocument.Parse(response);
        var r = doc.RootElement;
        return (r.GetProperty("results").EnumerateArray().Select(x => x.GetProperty("path").GetString()!).ToArray(),
                r.GetProperty("selector_notes").EnumerateArray().Select(n => n.GetString()!).ToArray());
    }

    string Status(string[]? exclude, string? provider = Mod, string[]? paths = null, bool counts = false)
        => AssetTools.AssetStatus(_w.Svc, paths: paths, provider: provider, exclude: exclude, counts_only: counts,
                                  format: counts ? null : "json", max_chars: Big);

    /// <summary>The re-entry bug: a walk's file, whose rows carry "error": null, enters paths= as the walk's nodes.</summary>
    [Fact]
    public void AWalkFileReEntersAsAPathsList()
    {
        var f = CapeWalk();
        var nodes = Rows(f);
        Assert.Equal(3, nodes.Length);
        var r = AssetTools.AssetStatus(_w.Svc, paths: new[] { "@" + f }, counts_only: true);
        Assert.DoesNotContain("ERROR rows", r);
        Assert.Contains($"census: counted={nodes.Length} ", r);
    }

    /// <summary>Q1 (a), two calls: the provider minus the walk's file is exactly what it ships that the walk does not
    /// reach, compared ignoring case and slash direction, and the note counts what it removed.</summary>
    [Fact]
    public void AProviderMinusItsWalkFileIsWhatItShipsThatTheWalkDoesNotReach()
    {
        var f = CapeWalk();
        var shipped = Json(Status(null)).Paths;
        var reached = Rows(f).Select(Fold).ToHashSet();
        var expected = shipped.Where(p => !reached.Contains(Fold(p))).ToArray();
        Assert.NotEmpty(expected);
        Assert.True(expected.Length < shipped.Length);

        var (paths, notes) = Json(Status(new[] { "@" + f }));
        Assert.Equal(expected, paths);
        Assert.Equal($"exclude= removed {shipped.Length - expected.Length} of the {shipped.Length} selected path(s).", Assert.Single(notes));
        Assert.Contains($"census: counted={expected.Length} ", Status(new[] { "@" + f }, counts: true));
    }

    /// <summary>Matching ignores case and slash direction, and a plain entry naming a folder takes everything beneath it.</summary>
    [Fact]
    public void MatchingIgnoresCaseAndSlashesAndAFolderTakesWhatIsBeneath()
    {
        var one = Json(Status(new[] { AssetWalkWorld.CapeNif.ToUpperInvariant().Replace('\\', '/') }, null,
                              new[] { AssetWalkWorld.CapeNif, AssetWalkWorld.FxNif })).Paths;
        Assert.Equal(new[] { AssetWalkWorld.FxNif }, one);

        var all = Json(Status(null)).Paths;
        var noMeshes = Json(Status(new[] { "MESHES" })).Paths;
        Assert.Equal(all.Where(p => !p.StartsWith(@"meshes\", StringComparison.OrdinalIgnoreCase)), noMeshes);
        var noPex = Json(Status(new[] { "scripts/*.PEX" })).Paths;
        Assert.Equal(all.Where(p => !p.EndsWith(".pex", StringComparison.OrdinalIgnoreCase)), noPex);
    }

    /// <summary>Q2 (a): an entry the selection does not hold is counted in a note, not refused.</summary>
    [Fact]
    public void AnEntryTheSelectionDoesNotHoldIsCountedInANote()
    {
        var all = Json(Status(null)).Paths;
        var (paths, notes) = Json(Status(new[] { "textures/hcwalk/nowhere.dds", AssetWalkWorld.CapeNif }));
        Assert.Equal(all.Length - 1, paths.Length);
        Assert.Equal($"exclude= removed 1 of the {all.Length} selected path(s); 1 of its 2 entries matched no selected path, so removed nothing.",
                     Assert.Single(notes));
    }

    /// <summary>Q2 (a): an exclusion that empties the selection is the answer, zero rows, not a refusal.</summary>
    [Fact]
    public void AnExclusionThatEmptiesTheSelectionAnswersZeroRows()
    {
        var r = Status(new[] { "**" });
        Assert.DoesNotContain("error", r[..Math.Min(r.Length, 20)]);
        Assert.Empty(Json(r).Paths);
        Assert.Contains("census: counted=0 ", Status(new[] { "**" }, counts: true));
    }

    /// <summary>nif_inspect takes exclude=: the provider's meshes less the walk's, and an explicit path it removes has no row.</summary>
    [Fact]
    public void NifInspectTakesExclude()
    {
        var f = CapeWalk();
        var r = NifTools.NifInspect(_w.Svc, provider: Mod, exclude: new[] { "@" + f }, counts_only: true);
        var all = NifTools.NifInspect(_w.Svc, provider: Mod, counts_only: true);
        // The walk's three files leave the cape mod's other files, of which two are meshes.
        Assert.True(r.Contains("exclude= removed 3 of the"), r);
        Assert.True(r.Contains("2 mesh"), r);
        Assert.True(all.Contains("3 mesh"), all);
        Assert.DoesNotContain("exclude=", all);

        var two = NifTools.NifInspect(_w.Svc, paths: new[] { AssetWalkWorld.CapeNif, AssetWalkWorld.FxNif },
                                      exclude: new[] { AssetWalkWorld.CapeNif });
        Assert.True(two.Contains("exclude= removed 1 of the 2 selected path(s)."), two);
        Assert.Contains(AssetWalkWorld.FxNif, two, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(AssetWalkWorld.CapeNif, two, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>decompile_script takes exclude=: an explicit Data path it removes has no row, and the rest still read.</summary>
    [Fact]
    public void DecompileScriptTakesExclude()
    {
        var outDir = Path.Combine(_w.Root, "psc-" + Guid.NewGuid().ToString("N"));
        var r = DecompileTools.DecompileScript(_w.Svc, paths: new[] { AssetWalkWorld.BadPex, AssetWalkWorld.ChildPex },
                                               exclude: new[] { AssetWalkWorld.BadPex.Replace('\\', '/').ToUpperInvariant() }, out_path: outDir);
        Assert.Contains("exclude= removed 1 of the 2 selected path(s).", r);
        Assert.DoesNotContain(AssetWalkWorld.BadPex, r, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(AssetWalkWorld.ChildPex, r, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[accounting] total=1", r);
    }

    /// <summary>With no selector of the load order's, exclude= is said, never silently dropped: on a file on disk,
    /// and on a walk seeded by records alone.</summary>
    [Fact]
    public void AnExclusionWithNothingSelectedSaysSo()
    {
        const string none = "exclude= removed 0 of the 0 selected path(s); 1 of its 1 entries matched no selected path, so removed nothing.";
        var disk = Path.Combine(_w.ModsDir, Mod, AssetWalkWorld.ChildPex);
        var pex = DecompileTools.DecompileScript(_w.Svc, paths: new[] { disk }, exclude: new[] { "scripts" },
                                                 out_path: Path.Combine(_w.Root, "psc-" + Guid.NewGuid().ToString("N")));
        Assert.Contains(none, pex);
        var nif = NifTools.NifInspect(_w.Svc, paths: new[] { Path.Combine(_w.ModsDir, Mod, AssetWalkWorld.CapeNif) }, exclude: new[] { "meshes" });
        Assert.Contains(none, nif);
        var walk = AssetTools.AssetStatus(_w.Svc, records: new[] { _w.Id("HcWalkHero") }, walk: Walks.On, exclude: new[] { "meshes" },
                                          counts_only: true);
        Assert.Contains(none, walk);
    }

    /// <summary>Q3 (a): a walk cut at walk.max_nodes leads the answer with a note naming the fix; the rows still come.</summary>
    [Fact]
    public void ACutWalksFileLeadsWithANoteAndTheRowsStillCome()
    {
        var f = File("cut");
        var w = AssetTools.AssetStatus(_w.Svc, records: new[] { _w.Id("HcWalkHero") }, walk: Walks.Of("{\"max_nodes\": 1}"),
                                       from_source: new[] { AssetWalkWorld.Plugin }, to_file: f);
        Assert.Contains("walk.max_nodes=1", w);
        AssertLeadsCut(f, "walk.max_nodes=1");
        // It leads a selector's own note too.
        var (_, notes) = Json(AssetTools.AssetStatus(_w.Svc, provider: Mod, under: new[] { "meshes/nowhere" }, exclude: new[] { "@" + f },
                                                     format: "json", max_chars: Big));
        Assert.StartsWith($"exclude= file '{f}' is a lower bound", notes[0]);
        Assert.StartsWith("under 'meshes/nowhere' matched no file", notes[1]);
    }

    /// <summary>Q3 (a): a walk stopped at the path bound is a lower bound too.</summary>
    [Fact]
    public void AWalkStoppedAtThePathBoundLeadsWithANote()
    {
        var f = File("bound");
        var prior = _w.Svc.AssetArea.MaxAssetPaths;
        _w.Svc.AssetArea.MaxAssetPaths = 1;
        try { Assert.Contains("the walk stopped at 1 files", AssetTools.AssetStatus(_w.Svc, paths: new[] { AssetWalkWorld.CapeNif }, walk: Walks.On, to_file: f)); }
        finally { _w.Svc.AssetArea.MaxAssetPaths = prior; }
        AssertLeadsCut(f, "the walk stopped at 1 files");
    }

    void AssertLeadsCut(string file, string said)
    {
        var plain = Json(Status(null)).Paths;
        var (paths, notes) = Json(Status(new[] { "@" + file }));
        Assert.StartsWith($"exclude= file '{file}' is a lower bound of what its walk reaches — ", notes[0]);
        Assert.Contains(said, notes[0]);
        Assert.EndsWith("re-walk with to_file= and pass the new file.", notes[0]);
        Assert.Equal(plain.Length - Rows(file).Count(p => plain.Contains(p, StringComparer.OrdinalIgnoreCase)), paths.Length);
    }

    /// <summary>Q3 (a): a file written on another build leads with a note naming both epochs; the rows still come.</summary>
    [Fact]
    public void AFileFromAnotherBuildLeadsWithANoteAndTheRowsStillCome()
    {
        var f = CapeWalk();
        var lines = System.IO.File.ReadAllLines(f);
        var epoch = JsonDocument.Parse(lines[0]).RootElement.GetProperty("epoch").GetString()!;
        Assert.NotEmpty(epoch);
        var old = File("old");
        lines[0] = lines[0].Replace($"\"epoch\":\"{epoch}\"", "\"epoch\":\"e2-0000000000000000\"");
        System.IO.File.WriteAllLines(old, lines);

        var (now, _) = Json(Status(new[] { "@" + f }));
        var (paths, notes) = Json(Status(new[] { "@" + old }));
        Assert.Equal(now, paths);
        Assert.Equal($"exclude= file '{old}' was written on another build (epoch=e2-0000000000000000; this build is epoch={epoch}), " +
                     "so it may not hold what that call answers now — re-run the call that wrote it with to_file= and pass the new file.", notes[0]);
        // An epoch an older houseCARL wrote is not called another build: it cannot be compared.
        var older = File("older");
        System.IO.File.WriteAllLines(older, lines.Select((l, i) => i == 0 ? l.Replace("\"epoch\":\"e2-0000000000000000\"", "\"epoch\":\"0000000000000000\"") : l));
        Assert.StartsWith($"exclude= file '{older}' carries an epoch an older houseCARL wrote (epoch=0000000000000000)",
                          Json(Status(new[] { "@" + older })).Notes[0]);
        // A file written with no epoch says it cannot tell.
        var bare = File("bare");
        lines[0] = lines[0].Replace("\"epoch\":\"e2-0000000000000000\"", "\"epoch\":\"\"");
        System.IO.File.WriteAllLines(bare, lines);
        Assert.Equal($"exclude= file '{bare}' carries no epoch, so whether it was written on this build is unknown — " +
                     "re-run the call that wrote it with to_file= and pass the new file.", Json(Status(new[] { "@" + bare })).Notes[0]);
        // The same build says nothing of it.
        Assert.DoesNotContain(Json(Status(new[] { "@" + f })).Notes, n => n.Contains("another build"));
    }

    /// <summary>A file's manifest query echo, by key.</summary>
    static Dictionary<string, string> Query(string file)
        => JsonDocument.Parse(System.IO.File.ReadLines(file).First()).RootElement.GetProperty("query").EnumerateObject()
               .ToDictionary(p => p.Name, p => p.Value.ToString());

    /// <summary>F1-a: a provider-minus file records its exclusion, the '@file' as given or the inline count; a file of a
    /// call without exclude= has no exclude key.</summary>
    [Fact]
    public void AnExcludeCallsFileRecordsTheExclusion()
    {
        var f = CapeWalk();
        var minus = File("minus");
        Assert.DoesNotContain("error:", AssetTools.AssetStatus(_w.Svc, provider: Mod, exclude: new[] { "@" + f }, to_file: minus));
        Assert.Equal("@" + f, Query(minus)["exclude"]);
        var inline = File("inline");
        AssetTools.AssetStatus(_w.Svc, provider: Mod, exclude: new[] { AssetWalkWorld.CapeNif, "scripts/*.pex" }, to_file: inline);
        Assert.Equal("2 inline path(s)", Query(inline)["exclude"]);
        var plain = File("plain");
        AssetTools.AssetStatus(_w.Svc, provider: Mod, to_file: plain);
        Assert.DoesNotContain("exclude", Query(plain).Keys);
    }

    /// <summary>Row 4: a walk seeded by a provider minus an exclusion records it among its seeds; one without does not.</summary>
    [Fact]
    public void AWalksFileRecordsItsSeedsExclusion()
    {
        var f = File("walk-minus");
        Assert.DoesNotContain("error:", AssetTools.AssetStatus(_w.Svc, provider: Mod, exclude: new[] { "scripts" }, walk: Walks.On, to_file: f));
        Assert.Equal("1 inline path(s)", Query(f)["exclude"]);
        var plain = File("walk-plain");
        AssetTools.AssetStatus(_w.Svc, provider: Mod, walk: Walks.On, to_file: plain);
        Assert.DoesNotContain("exclude", Query(plain).Keys);
    }

    /// <summary>Row 5: nif_inspect's file records the exclusion; one without does not.</summary>
    [Fact]
    public void NifInspectsFileRecordsTheExclusion()
    {
        var walked = CapeWalk();
        var f = File("nif-minus");
        Assert.DoesNotContain("error:", NifTools.NifInspect(_w.Svc, provider: Mod, exclude: new[] { "@" + walked }, to_file: f));
        Assert.Equal("@" + walked, Query(f)["exclude"]);
        var plain = File("nif-plain");
        NifTools.NifInspect(_w.Svc, provider: Mod, to_file: plain);
        Assert.DoesNotContain("exclude", Query(plain).Keys);
    }

    /// <summary>F1-b: a call refused for a bad window, no selector, records= without a walk, a bad to_file= or counts_only=
    /// with to_file= answers that refusal before an '@file' with an epoch captures the view to compare it.</summary>
    [Fact]
    public void ACheapRefusalComesBeforeTheExclusionFileIsRead()
    {
        var f = CapeWalk();
        var ex = new[] { "@" + f };
        var calls = new Func<string[]?, string>[]
        {
            e => AssetTools.AssetStatus(_w.Svc, provider: Mod, exclude: e, offset: -1),
            e => AssetTools.AssetStatus(_w.Svc, exclude: e),
            e => AssetTools.AssetStatus(_w.Svc, provider: Mod, records: new[] { _w.Id("HcWalkHero") }, exclude: e),
            e => AssetTools.AssetStatus(_w.Svc, provider: Mod, exclude: e, to_file: "relative.jsonl"),
            e => AssetTools.AssetStatus(_w.Svc, provider: Mod, exclude: e, counts_only: true, to_file: File("counts")),
        };
        foreach (var call in calls)
        {
            var without = call(null);
            Assert.StartsWith("error:", without);
            var checks = _w.Svc.Counters.ProfileFreshnessChecks;
            Assert.Equal(without, call(ex));
            Assert.Equal(checks, _w.Svc.Counters.ProfileFreshnessChecks);
        }
    }

    /// <summary>The list door's own refusal for an '@file' beside inline entries, and a blank entry, refused.</summary>
    [Fact]
    public void MixedFileAndInlineAndABlankEntryAreRefused()
    {
        var f = CapeWalk();
        var mixed = AssetTools.AssetStatus(_w.Svc, provider: Mod, exclude: new[] { "@" + f, AssetWalkWorld.FxNif });
        Assert.Contains("exclude= mixes an '@file' entry with inline entries", mixed);
        var blank = AssetTools.AssetStatus(_w.Svc, provider: Mod, exclude: new[] { " " });
        Assert.StartsWith("error: exclude= has a blank entry", blank);
    }
}

/// <summary>Q3 (a) on a walk cut at walk.depth: a chain of lists walked one hop out.</summary>
[Trait("tier", "unit")]
public sealed class FileExclusionDepthTests : IClassFixture<AssetWalkDoorWorld>
{
    readonly AssetWalkDoorWorld _w;
    public FileExclusionDepthTests(AssetWalkDoorWorld w) => _w = w;

    [Fact]
    public void AWalkCutAtItsDepthLeadsWithANoteAndTheRowsStillCome()
    {
        var f = Path.Combine(Path.GetTempPath(), "hc-exclude-depth-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            var w = AssetTools.AssetStatus(_w.Svc, records: new[] { FormIdToken.Of(_w.Keys["HcDoorChain0"]) },
                                           walk: Walks.Of("{\"follow\": \"*\", \"depth\": 1}"), to_file: f);
            Assert.True(w.Contains("hops from a seed and were not entered"), w);
            var r = AssetTools.AssetStatus(_w.Svc, paths: new[] { "textures/nowhere.dds" }, exclude: new[] { "@" + f });
            Assert.True(r.Contains($"exclude= file '{f}' is a lower bound of what its walk reaches — 1 record(s) sit more than walk.depth=1 hops"), r);
            // The walk reached no file, so its file has no rows: the empty set, which removes nothing.
            Assert.Contains("exclude= removed 0 of the 1 selected path(s).", r);
            Assert.Contains("(1 path selected", r);
        }
        finally { File.Delete(f); }
    }
}
