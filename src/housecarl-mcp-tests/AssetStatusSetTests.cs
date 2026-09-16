using System.Text.Json;
using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The set and file-sink forms on asset_status (#584): a list re-entered from a file, NPC FormIDs whose
/// FaceGen pair is derived per NPC with the other half's winner beside each row, the to_file= artifact, and the
/// declared-cost bound. Before this, a whole-order pairing sweep was two calls per NPC and broke the character budget
/// past a few dozen paths.</summary>
[Trait("tier", "unit")]
public sealed class AssetStatusSetTests : IClassFixture<AssetSelectWorld>
{
    readonly AssetSelectWorld _w;
    public AssetStatusSetTests(AssetSelectWorld w) => _w = w;

    static string Temp(string leaf) =>
        Path.Combine(Path.GetTempPath(), "hc-asset-set-" + Guid.NewGuid().ToString("N") + "-" + leaf);

    static AssetPathResult Row(AssetStatusData d, string path) =>
        d.Results.Single(r => string.Equals(r.RelPath, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>A path list re-entered from a file answers one row per path, each with its winner and whether that
    /// winner is loose or a BSA — the same answer an inline list gives, with the list somewhere the caller's own
    /// tools wrote it.</summary>
    [Fact]
    public void APathListReadFromAFileAnswersOneRowPerPathWithItsWinnerAndKind()
    {
        var list = Temp("paths.txt");
        File.WriteAllLines(list, new[] { _w.Rel("0001.nif"), _w.Rel("0002.nif"), _w.Rel("0005.nif") });
        try
        {
            var text = AssetTools.AssetStatus(_w.Svc, new[] { "@" + list });

            Assert.Contains("(3 paths selected)", text);
            Assert.Contains("WINS: \"FaceBase\" (loose)", text);       // 0001 — one loose provider
            Assert.Contains("WINS: \"FaceHigher\" (loose)", text);     // 0002 — the higher-priority loose mod
            Assert.Contains("WINS: \"HcArch.bsa\" (BSA)", text);       // 0005 — reachable only through the archive
        }
        finally { File.Delete(list); }
    }

    /// <summary>An '@file' entry stands in place of the WHOLE list, so mixing it with inline entries is refused by
    /// name rather than read as a splice.</summary>
    [Fact]
    public void MixingAnAtFileEntryWithInlinePathsIsRefusedByName()
    {
        var text = AssetTools.AssetStatus(_w.Svc, new[] { "@C:\\work\\list.txt", _w.Rel("0001.nif") });

        Assert.Contains("stands IN PLACE OF the whole list", text);
    }

    /// <summary>The #584 measure: one FormID derives BOTH halves of that NPC's bake and each row names the OTHER
    /// half's winner beside its own — so the split (head from one mod, tint from another) is visible in one call
    /// instead of two per NPC plus a join.</summary>
    [Fact]
    public void AFormIdDerivesBothFaceGenPathsAndNamesThePairWinnerBesideEach()
    {
        var mesh = AssetSelectWorld.Face(AssetSelectWorld.SplitFormId, FaceGenSlot.Mesh);
        var tint = AssetSelectWorld.Face(AssetSelectWorld.SplitFormId, FaceGenSlot.Tint);

        var d = _w.Svc.AssetStatus(Array.Empty<string>(), null, 0, 0,
                                   new[] { new FaceGenSeed(AssetSelectWorld.SplitFormId, FormKey.Factory(AssetSelectWorld.SplitFormId), null) });

        Assert.Equal(2, d.Results.Count);
        var m = Row(d, mesh);
        var t = Row(d, tint);

        // The head: won loose by FaceHigher over the BSA copy, with the tint's winner stated beside it.
        Assert.Equal("FaceHigher", m.Hit!.Winner!.Source);
        Assert.Equal(AssetKind.Loose, m.Hit.Winner.Kind);
        Assert.Contains(m.Hit.Providers, p => p.Kind == AssetKind.Bsa);
        Assert.Equal(tint, m.PairPath);
        Assert.Equal("FaceBase", m.PairHit!.Winner!.Source);
        Assert.True(m.PairDiffers);

        // And the tint row is the same fact read from the other side.
        Assert.Equal("FaceBase", t.Hit!.Winner!.Source);
        Assert.Equal(mesh, t.PairPath);
        Assert.Equal("FaceHigher", t.PairHit!.Winner!.Source);
        Assert.True(t.PairDiffers);

        // A clean same-source pair is NOT flagged: the split is the finding, not the pairing.
        var clean = _w.Svc.AssetStatus(Array.Empty<string>(), null, 0, 0,
                                       new[] { new FaceGenSeed(AssetSelectWorld.MatchedFormId, FormKey.Factory(AssetSelectWorld.MatchedFormId), null) });
        Assert.All(clean.Results, r => Assert.False(r.PairDiffers));
    }

    /// <summary>Two ARCHIVES of ONE mod is not a split. It is how the vanilla game ships every bake — heads in
    /// "Skyrim - Meshes0.bsa", tints in "Skyrim - Textures0.bsa" — so a verdict taken off the provider NAME calls
    /// most of the order split. On the ARR order that was 2,719 NPCs against 375 decided on the owning mod.</summary>
    [Fact]
    public void TwoArchivesOfOneModAreNotASplit()
    {
        var d = _w.Svc.AssetStatus(Array.Empty<string>(), null, 0, 0,
                                   new[] { new FaceGenSeed(AssetSelectWorld.ArchivePairFormId, FormKey.Factory(AssetSelectWorld.ArchivePairFormId), null) });

        var mesh = Row(d, AssetSelectWorld.Face(AssetSelectWorld.ArchivePairFormId, FaceGenSlot.Mesh));
        var tint = Row(d, AssetSelectWorld.Face(AssetSelectWorld.ArchivePairFormId, FaceGenSlot.Tint));

        // Two different archives win the two halves...
        Assert.Equal(AssetKind.Bsa, mesh.Hit!.Winner!.Kind);
        Assert.Equal(AssetKind.Bsa, tint.Hit!.Winner!.Kind);
        Assert.NotEqual(mesh.Hit.Winner.Source, tint.Hit.Winner.Source);
        // ...and both archives are the same mod, so the pair is whole.
        Assert.Equal(AssetSelectWorld.ArchiveModName, mesh.Hit.Winner.OwningMod);
        Assert.Equal(AssetSelectWorld.ArchiveModName, tint.Hit.Winner.OwningMod);
        Assert.False(mesh.PairDiffers);
        Assert.False(tint.PairDiffers);
    }

    /// <summary>The whole-order sweep's own question: a winning head whose tint exists nowhere. The head row says the
    /// pair is ABSENT rather than leaving the caller to spot a missing row.</summary>
    [Fact]
    public void AWinningHeadWhoseTintHasNoProviderSaysSoOnTheHeadRow()
    {
        var text = AssetTools.AssetStatus(_w.Svc, formids: new[] { AssetSelectWorld.TintAbsentFormId });

        Assert.Contains("facegen mesh", text);
        Assert.Contains("pair (tint)", text);
        Assert.Contains("ABSENT — no active mod or BSA provides this path", text);
        // Absent is not "different": the split note is about two winners, and there is only one here.
        Assert.DoesNotContain("win from DIFFERENT", text);
    }

    /// <summary>A token that is not a FormID is ONE error row, not a failed call — the same posture a malformed asset
    /// path takes on this tool. Driven through the TOOL, so the parse and its catch are what answer: a RUNTIME FormID
    /// reaches for a load-order build, and this world has none, so a narrower catch hands the caller "an internal
    /// houseCARL failure (the arguments bound fine)" for input the tool can plainly name.</summary>
    [Fact]
    public void ATokenThatIsNotAFormIdIsOneErrorRowAndNotAFailedCall()
    {
        foreach (var bad in new[] { "not-a-formid", "0300B0B0" })
        {
            var text = AssetTools.AssetStatus(_w.Svc, formids: new[] { bad, AssetSelectWorld.MatchedFormId });

            Assert.DoesNotContain("failed unexpectedly", text);
            Assert.Contains("not a FormID", text);
            Assert.Contains("(3 paths selected)", text);        // the error row, then the good NPC's two halves
            Assert.Contains("WINS: \"FaceBase\" (loose)", text);
        }
    }

    /// <summary>to_file= writes the complete result as an artifact and renders only the manifest, and the file reads
    /// back: one row per resolved path, the pair columns filled, and row_count equal to total because the artifact is
    /// never a window.</summary>
    [Fact]
    public void ToFileWritesTheArtifactAndTheManifestReadsBack()
    {
        var file = Temp("sweep.jsonl");
        try
        {
            var text = AssetTools.AssetStatus(
                _w.Svc,
                formids: new[] { AssetSelectWorld.SplitFormId, AssetSelectWorld.MatchedFormId, AssetSelectWorld.TintAbsentFormId },
                to_file: file);

            Assert.Contains("spilled: complete result (6 rows)", text);
            Assert.Contains(file, text);
            Assert.Contains("identity=path", text);
            // The rows are the FILE: the manifest-only render does not print a per-path block.
            Assert.DoesNotContain("providers (", text);

            var lines = File.ReadAllLines(file);
            var manifest = JsonDocument.Parse(lines[0]).RootElement;
            Assert.Equal(ToolNames.AssetStatus, manifest.GetProperty("tool").GetString());
            Assert.Equal("path", manifest.GetProperty("identity").GetString());
            Assert.Equal(6, manifest.GetProperty("row_count").GetInt32());
            Assert.Equal(6, manifest.GetProperty("total").GetInt32());
            // This world resolves assets but no plugins, which is the tool's own decoupling: the rows still answer,
            // and the RESPONSE — not only the file — says why there is no fingerprint. A caller of a to_file= call
            // sees the manifest block and nothing else, so an empty epoch with the reason hidden in the file is the
            // unstamped state the artifact convention exists to make impossible.
            Assert.Equal("", manifest.GetProperty("epoch").GetString());
            Assert.Contains("epoch: NONE", text);
            Assert.Contains("could not be read for a fingerprint", text);
            // The §2.1 coverage stamp as a FIELD, not as prose: every row here is read off the VFS while the
            // fingerprint would describe the record build.
            Assert.False(manifest.GetProperty("epoch_covers_all_inputs").GetBoolean());
            Assert.Contains(manifest.GetProperty("epoch_uncovered").EnumerateArray(),
                            u => u.GetString()!.Contains("VFS", StringComparison.Ordinal));
            Assert.Contains("epoch_covers_all_inputs=false", text);

            var rows = lines.Skip(1).Where(l => l.Length > 0).Select(l => JsonDocument.Parse(l).RootElement).ToList();
            Assert.Equal(6, rows.Count);
            var split = rows.Single(r => r.GetProperty("path").GetString()!.EndsWith(".nif", StringComparison.OrdinalIgnoreCase)
                                      && r.GetProperty("formid").GetString() == AssetSelectWorld.SplitFormId);
            Assert.Equal("FaceHigher", split.GetProperty("winner").GetString());
            Assert.Equal("loose", split.GetProperty("winner_kind").GetString());
            Assert.Equal("mesh", split.GetProperty("slot").GetString());
            Assert.Equal("FaceBase", split.GetProperty("pair_winner").GetString());
            Assert.True(split.GetProperty("pair_differs").GetBoolean());
            // The two mod columns carry the OWNER the verdict was taken on, so a consumer can re-derive it. A loose
            // provider has no OwningMod at all, so writing the raw field would put null on both sides of the
            // commonest split there is — two loose overhauls — and read as null == null.
            Assert.Equal("FaceHigher", split.GetProperty("winner_mod").GetString());
            Assert.Equal("FaceBase", split.GetProperty("pair_winner_mod").GetString());
            Assert.NotEqual(split.GetProperty("winner_mod").GetString(), split.GetProperty("pair_winner_mod").GetString());

            // The file is re-enterable by construction: its identity column extracts to exactly the six paths.
            var (m2, tokens, rerr) = ResultArtifact.ReadIdentity(file, File.ReadAllText(file));
            Assert.Null(rerr);
            Assert.Equal("path", m2!.Identity);
            Assert.Equal(6, tokens!.Count);
            Assert.Equal(rows.Select(r => r.GetProperty("path").GetString()), tokens);

            // And it re-enters as a PATH list without a build: a path is a string, every answer about it is read live
            // off the VFS, and nothing in it can go stale against a record build. Gating it would refuse the loop the
            // feature exists for — sweep, fix a mod, re-ask the same paths — over a build the answer never used, and
            // an artifact written on an unbuildable order (this one) could never be re-entered at all.
            var again = AssetTools.AssetStatus(_w.Svc, new[] { "@" + file });
            Assert.Contains("(6 paths selected)", again);
            Assert.DoesNotContain("epoch", again, StringComparison.OrdinalIgnoreCase);
        }
        finally { File.Delete(file); }
    }

    /// <summary>The artifact is never a window, so offset= has nothing to page and is refused rather than silently
    /// writing a partial file under a completeness claim.</summary>
    [Fact]
    public void OffsetIsRefusedWithToFile()
    {
        var text = AssetTools.AssetStatus(_w.Svc, new[] { _w.Rel("0001.nif") },
                                          to_file: Temp("never-written.jsonl"), offset: 1);

        Assert.Contains("the artifact is never a window", text);
    }

    /// <summary>A to_file= path that is not absolute, or not .jsonl, is refused by the same validator the records
    /// surface runs — an unvalidated relative path writes under the SERVER's working directory and the response then
    /// names a file the caller cannot find.</summary>
    [Fact]
    public void AToFileTargetIsHeldToTheSameShapeTheRecordsSurfaceHoldsItTo()
    {
        Assert.Contains("must be an ABSOLUTE path",
                        AssetTools.AssetStatus(_w.Svc, new[] { _w.Rel("0001.nif") }, to_file: "sweep.jsonl"));
        Assert.Contains(".jsonl extension",
                        AssetTools.AssetStatus(_w.Svc, new[] { _w.Rel("0001.nif") }, to_file: Temp("sweep.txt")));
    }

    /// <summary>Past the per-call bound the call refuses UP FRONT, naming the count and what it would have spent,
    /// rather than resolving for an hour. A pair counts both halves, because both are resolved.</summary>
    [Fact]
    public void TheBoundRefusesUpFrontWithTheCountAndTheEstimate()
    {
        var prior = RenderBudget.MaxAssetPaths;
        RenderBudget.MaxAssetPaths = 3;
        try
        {
            var text = AssetTools.AssetStatus(
                _w.Svc,
                formids: new[] { AssetSelectWorld.SplitFormId, AssetSelectWorld.MatchedFormId, AssetSelectWorld.TintAbsentFormId });

            Assert.Contains("resolves 6 asset path(s)", text);
            Assert.Contains("3-path bound", text);
            Assert.Contains("seconds", text);                    // the estimate, not just the count

            // And the same selection under the bound still answers.
            RenderBudget.MaxAssetPaths = 6;
            Assert.Contains("(6 paths selected)",
                            AssetTools.AssetStatus(_w.Svc,
                                                   formids: new[] { AssetSelectWorld.SplitFormId, AssetSelectWorld.MatchedFormId, AssetSelectWorld.TintAbsentFormId }));
        }
        finally { RenderBudget.MaxAssetPaths = prior; }
    }

    /// <summary>On an `under=` sweep the bound stops the WALK, not just the resolve. The enumeration is the expensive
    /// half — it re-walks every loose root and re-scans every archive table under the prefix — so counting it in full
    /// and then refusing would charge the caller for exactly the work the refusal says was too much.</summary>
    [Fact]
    public void AnUnderSweepStopsWalkingAtTheBoundRatherThanCountingItAllFirst()
    {
        var prior = RenderBudget.MaxAssetPaths;
        RenderBudget.MaxAssetPaths = 2;
        try
        {
            var text = AssetTools.AssetStatus(_w.Svc, under: new[] { AssetSelectWorld.FaceGeomDir });

            Assert.Contains("at least 3 asset path(s)", text);        // a floor, not a total: it stopped counting
            Assert.Contains("stopped counting there", text);
            Assert.Contains("2-path bound", text);
        }
        finally { RenderBudget.MaxAssetPaths = prior; }
    }

    /// <summary>The bound is on what the call RESOLVES, so a windowed sweep over a folder bigger than the bound
    /// still answers — only the window is resolved. Bounding the SELECTION instead would refuse a paged `under=`
    /// sweep that worked before, and send the caller back round with the one lever the refusal names.</summary>
    [Fact]
    public void APagedUnderSweepAnswersOverAFolderBiggerThanTheBound()
    {
        var prior = RenderBudget.MaxAssetPaths;
        RenderBudget.MaxAssetPaths = 2;
        try
        {
            var text = AssetTools.AssetStatus(_w.Svc, under: new[] { AssetSelectWorld.FaceGeomDir }, limit: 2);

            Assert.DoesNotContain("bound", text);
            Assert.Contains($"({AssetSelectWorld.FaceGeomFiles} paths selected)", text);
            Assert.Contains("rendered=2", text);

            // And the second page too, so following the accounting's own advice keeps working.
            var page2 = AssetTools.AssetStatus(_w.Svc, under: new[] { AssetSelectWorld.FaceGeomDir }, limit: 2, offset: 2);
            Assert.DoesNotContain("bound", page2);
            Assert.Contains("rendered=2", page2);

            // A window OVER the bound is still refused — the window is what gets resolved.
            var wide = AssetTools.AssetStatus(_w.Svc, under: new[] { AssetSelectWorld.FaceGeomDir }, limit: 5);
            Assert.Contains("2-path bound", wide);
        }
        finally { RenderBudget.MaxAssetPaths = prior; }
    }

    /// <summary>A plain path list splits on line breaks ONLY. A comma is legal in a Windows file name and mod authors
    /// use them, so splitting on it turns one line into two tokens and answers ABSENT twice for a file that
    /// exists — a hole in the sweep that is not there, with no refusal and no note.</summary>
    [Fact]
    public void APlainPathListIsSplitOnLineBreaksAndNotOnCommas()
    {
        var commaPath = @"meshes\hccomma\journal, vol2.nif";
        var list = Temp("comma-paths.txt");
        File.WriteAllLines(list, new[] { commaPath, _w.Rel("0001.nif") });
        try
        {
            var d = _w.Svc.AssetStatus(
                Artifacts.ExpandListInput(new[] { "@" + list }, "asset_paths", identity: "path").Tokens!);

            Assert.Equal(2, d.Results.Count);
            Assert.Equal(commaPath, d.Results[0].RelPath);
        }
        finally { File.Delete(list); }
    }

    /// <summary>A pasted JSON array on ONE line is parsed as one, not joined into a single bogus token. Line
    /// splitting alone would hand back `meshes/a.nif", "meshes/b.nif` — still a legal relative path, so it passes
    /// validation and comes back as one ABSENT row with no refusal and no note, which is the comma bug's mirror
    /// image.</summary>
    [Fact]
    public void AOneLinePathArrayIsParsedAsJsonRatherThanJoinedIntoOneToken()
    {
        var list = Temp("array-paths.txt");
        File.WriteAllText(list, "[\"" + _w.Rel("0001.nif").Replace("\\", "\\\\") + "\", \""
                                      + _w.Rel("0002.nif").Replace("\\", "\\\\") + "\"]");
        try
        {
            var text = AssetTools.AssetStatus(_w.Svc, new[] { "@" + list });

            Assert.Contains("(2 paths selected)", text);
            Assert.Contains("WINS: \"FaceBase\" (loose)", text);
            Assert.Contains("WINS: \"FaceHigher\" (loose)", text);
            Assert.DoesNotContain("ABSENT", text);
        }
        finally { File.Delete(list); }
    }

    /// <summary>A glob's cap counts MATCHES, not candidates: the pattern filters inside the walk, so a narrow
    /// selector under a wide folder is never refused for the folder's size.</summary>
    [Fact]
    public void ANarrowGlobUnderAWideFolderIsNotRefusedForTheFoldersSize()
    {
        var prior = RenderBudget.MaxAssetPaths;
        RenderBudget.MaxAssetPaths = 2;
        try
        {
            var text = AssetTools.AssetStatus(_w.Svc, under: new[] { AssetSelectWorld.FaceGeomDir + @"\0004.*" });

            Assert.DoesNotContain("bound", text);
            Assert.Contains("(1 path selected)", text);
        }
        finally { RenderBudget.MaxAssetPaths = prior; }
    }

    /// <summary>The text lane shows the owning mod on a formids= row's own winner as well as its pair's, or the
    /// two-archives-of-one-mod verdict is unreadable there: two different BSA names, no split warning, and nothing on
    /// the page saying the two archives are one mod.</summary>
    [Fact]
    public void TheTextLaneNamesTheOwningModOnBothHalvesOfAnArchivePair()
    {
        var text = AssetTools.AssetStatus(_w.Svc, formids: new[] { AssetSelectWorld.ArchivePairFormId });

        Assert.Contains("WINS: \"HcArch.bsa\" (BSA)  [mod: " + AssetSelectWorld.ArchiveModName + "]", text);
        Assert.Contains("WINS: \"HcArch - Textures.bsa\" (BSA)  [mod: " + AssetSelectWorld.ArchiveModName + "]", text);
        Assert.DoesNotContain("win from DIFFERENT", text);

        // A plain path block is byte for byte the block it always was — no mod tag, no pair.
        var plain = AssetTools.AssetStatus(_w.Svc, new[] { _w.Rel("0005.nif") });
        Assert.Contains("WINS: \"HcArch.bsa\" (BSA)\n", plain);
        Assert.DoesNotContain("[mod:", plain);
    }

    /// <summary>The degraded-order roster survives the artifact round trip. It is written by the manifest and parsed
    /// back by it, and a build that lost plugins is what a re-read file has to be able to say about itself — which on
    /// a synthetic world needs no degraded order, only the write and the read.</summary>
    [Fact]
    public void TheDegradedOrderRosterSurvivesTheArtifactRoundTrip()
    {
        var file = Temp("degraded.jsonl");
        try
        {
            using (var writer = new ResultArtifact.Writer())
            {
                writer.WriteRow((w, _) => { w.WriteStartObject(); w.WriteString("path", @"meshes\a.nif"); w.WriteEndObject(); });
                var (m, err) = writer.Save(ArtifactTarget.Named(file), ToolNames.AssetStatus,
                                           Array.Empty<KeyValuePair<string, string>>(), "path", new[] { "path" },
                                           "input order", total: 1, epoch: "e2-deadbeef",
                                           excludedPlugins: new[] { "ksws03_quest.esp", "other.esp" });
                Assert.Null(err);
                Assert.True(m!.OrderDegraded);
            }

            var (read, tokens, rerr) = ResultArtifact.ReadIdentity(file, File.ReadAllText(file));
            Assert.Null(rerr);
            Assert.Single(tokens!);
            Assert.True(read!.OrderDegraded);
            Assert.Equal(new[] { "ksws03_quest.esp", "other.esp" }, read.ExcludedPlugins);
            // No coverage claim was made, so none is stamped — null is "this lane says nothing", not "it covers
            // everything".
            Assert.Null(read.EpochCoversAllInputs);
        }
        finally { File.Delete(file); }
    }

    /// <summary>A lane that DOES claim coverage and covers everything can say so. Normalizing an empty uncovered list
    /// to null would make the true stamp unwritable, so no artifact could ever state that its epoch describes its
    /// rows — only that it does not.</summary>
    [Fact]
    public void AnArtifactCanStampThatItsEpochCoversEverything()
    {
        var file = Temp("covered.jsonl");
        try
        {
            using (var writer = new ResultArtifact.Writer())
            {
                writer.WriteRow((w, _) => { w.WriteStartObject(); w.WriteString("path", @"meshes\a.nif"); w.WriteEndObject(); });
                writer.Save(ArtifactTarget.Named(file), ToolNames.AssetStatus,
                            Array.Empty<KeyValuePair<string, string>>(), "path", new[] { "path" }, "input order",
                            total: 1, epoch: "e2-deadbeef", epochUncovered: Array.Empty<string>());
            }

            var (read, _, rerr) = ResultArtifact.ReadIdentity(file, File.ReadAllText(file));
            Assert.Null(rerr);
            Assert.True(read!.EpochCoversAllInputs);
        }
        finally { File.Delete(file); }
    }

    /// <summary>A build that lost a plugin to a load failure says so in the artifact AND beside the spilled marker,
    /// naming it. Driven end to end by holding a plugin file open — the index build excludes a plugin it cannot open
    /// rather than failing — so what is asserted is the stamp the tool actually wrote, not a manifest built by
    /// hand.</summary>
    [Fact]
    public void ADegradedOrderIsNamedInTheArtifactAndBesideTheSpillMarker()
    {
        using var w = new DegradedOrderWorld();
        var file = Temp("degraded-live.jsonl");
        try
        {
            using (new FileStream(w.PluginFile, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                w.ForceRebuild();

                var text = AssetTools.AssetStatus(w.Svc, new[] { DegradedOrderWorld.AssetPath }, to_file: file);

                Assert.DoesNotContain("failed unexpectedly", text);
                Assert.Contains("order_degraded=true", text);
                Assert.Contains(DegradedOrderWorld.LockedPlugin, text);
                Assert.Contains("spilled: complete result (1 row)", text);

                var manifest = JsonDocument.Parse(File.ReadAllLines(file)[0]).RootElement;
                Assert.True(manifest.GetProperty("order_degraded").GetBoolean());
                Assert.Contains(manifest.GetProperty("excluded_plugins").EnumerateArray(),
                                p => p.GetString() == DegradedOrderWorld.LockedPlugin);
                // The epoch is real here: the order built, it just built short of one plugin.
                Assert.NotEqual("", manifest.GetProperty("epoch").GetString());
            }
        }
        finally { File.Delete(file); }
    }

    /// <summary>The no-epoch sentence is written from whatever the order read threw, with its remedy, on BOTH
    /// transports — the response, whose reader has the call in hand, and the file, whose reader months later has
    /// nothing else to go on.
    /// <para>Driven at the seam with an <see cref="IOException"/> message rather than end to end: the index build
    /// opens no plugin eagerly (a plugin it cannot open is excluded, which the test above uses), so the only thing
    /// that can throw one here is the profile read — and the asset capture reads the same files a frame earlier and
    /// dies first, which is #794. The <see cref="InvalidOperationException"/> arm of the same catch IS driven end to
    /// end, by <see cref="ToFileWritesTheArtifactAndTheManifestReadsBack"/>.</para></summary>
    [Fact]
    public void TheNoEpochSentenceCarriesTheReasonAndTheRemedyOnBothTransports()
    {
        var file = Temp("unreadable.jsonl");
        try
        {
            var why = Guard.Flatten(new IOException(
                "The process cannot access the file 'plugins.txt' because it is being used by another process.").Message);
            var data = _w.Svc.AssetStatus(new[] { _w.Rel("0001.nif") });

            var (spill, err) = AssetArtifact.Write(data, file, order: null,
                                                   Array.Empty<KeyValuePair<string, string>>(), why);
            Assert.Null(err);
            var text = AssetArtifact.RenderManifestOnly(data, spill!, json: false, cap: 80_000);

            Assert.Contains("epoch: NONE", text);
            Assert.Contains("could not be read for a fingerprint", text);
            Assert.Contains("being used by another process", text);
            Assert.Contains("re-run once the order reads", text);

            var manifest = JsonDocument.Parse(File.ReadAllLines(file)[0]).RootElement;
            Assert.Contains(manifest.GetProperty("notes").EnumerateArray(),
                            n => n.GetString()!.Contains("'epoch' is EMPTY", StringComparison.Ordinal)
                              && n.GetString()!.Contains("re-run the call once the order reads", StringComparison.Ordinal));
        }
        finally { File.Delete(file); }
    }

    /// <summary>The empty-selection refusal names every SELECT there is, formids= included — a caller who passed
    /// none has to be told all three.</summary>
    [Fact]
    public void TheEmptySelectionRefusalNamesFormidsToo()
    {
        var text = AssetTools.AssetStatus(_w.Svc);

        Assert.Contains("asset_paths, under and formids are all empty", text);
        Assert.Contains("01A51A:Dawnguard.esm", text);
    }

    /// <summary>The json lane carries the pair as data, not as prose a consumer has to parse out of a rendered
    /// line.</summary>
    [Fact]
    public void TheJsonRowCarriesThePairAsData()
    {
        var root = JsonDocument.Parse(
            AssetTools.AssetStatus(_w.Svc, formids: new[] { AssetSelectWorld.SplitFormId }, format: "json")).RootElement;

        var row = root.GetProperty("results")[0];
        Assert.Equal(AssetSelectWorld.SplitFormId, row.GetProperty("formid").GetString());
        Assert.Equal("mesh", row.GetProperty("slot").GetString());
        var pair = row.GetProperty("pair");
        Assert.Equal("tint", pair.GetProperty("slot").GetString());
        Assert.Equal("FaceBase", pair.GetProperty("winner").GetProperty("name").GetString());
        Assert.True(pair.GetProperty("differs").GetBoolean());
        // Both mod fields carry the OWNER the verdict is taken on. This fixture is loose-vs-loose, where a loose
        // provider has no OwningMod at all, so writing the raw field would put null on both sides of a true
        // `differs` and a consumer checking the verdict the way the document tells it to gets the opposite answer.
        Assert.Equal("FaceHigher", row.GetProperty("winner_mod").GetString());
        Assert.Equal("FaceBase", pair.GetProperty("winner_mod").GetString());

        // A plain path row is the document it always was: no formid, no slot, no pair.
        var plain = JsonDocument.Parse(
            AssetTools.AssetStatus(_w.Svc, new[] { _w.Rel("0001.nif") }, format: "json")).RootElement
            .GetProperty("results")[0];
        Assert.False(plain.TryGetProperty("formid", out _));
        Assert.False(plain.TryGetProperty("pair", out _));
    }
}

/// <summary>A two-plugin MO2 instance whose SECOND plugin file can be HELD by something else. The index build opens
/// each plugin, and one it cannot open is EXCLUDED from the build rather than failing it — so holding that file is
/// how a degraded order is produced on demand, and the stamp the artifact carries can be driven end to end instead
/// of asserted off a hand-built manifest.
/// <para>Its own instance, not a shared fixture: a test that locks a file and forces a rebuild must not poison a
/// world other tests read.</para></summary>
sealed class DegradedOrderWorld : IDisposable
{
    /// <summary>The one loose asset the sweep answers for, provided by a mod folder the index never opens.</summary>
    public const string AssetPath = @"meshes\hclocked\thing.nif";

    /// <summary>The plugin this world's tests hold open, and the name the degraded stamp must carry.</summary>
    public const string LockedPlugin = "HcLockedTwo.esm";

    public string Root { get; }
    public string PluginFile { get; }
    public LoadOrderService Svc { get; }

    public DegradedOrderWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-degraded-" + Guid.NewGuid().ToString("N"));
        var instance = Path.Combine(Root, "instance");
        var profile = Path.Combine(instance, "profiles", "Default");
        var mods = Path.Combine(instance, "mods");
        var pluginMod = Path.Combine(mods, "PluginMod");
        var assetMod = Path.Combine(mods, "AssetMod");
        foreach (var d in new[] { profile, Path.Combine(Root, "game", "Data"), pluginMod, assetMod })
            Directory.CreateDirectory(d);

        // Two masters, so the order still resolves when the second is excluded — one alone would leave no active
        // plugin and fail the build outright, which is a different answer.
        var first = new ModKey("HcLockedOne", ModType.Master);
        var second = new ModKey(Path.GetFileNameWithoutExtension(LockedPlugin), ModType.Master);
        foreach (var key in new[] { first, second })
        {
            var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
            mod.Weapons.AddNew().EditorID = key.Name + "Weapon";
            mod.BeginWrite.ToPath(Path.Combine(pluginMod, key.FileName.String))
               .WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        }
        PluginFile = Path.Combine(pluginMod, LockedPlugin);

        var asset = Path.Combine(assetMod, AssetPath);
        Directory.CreateDirectory(Path.GetDirectoryName(asset)!);
        File.WriteAllText(asset, "x");

        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");
        File.WriteAllText(Path.Combine(profile, "loadorder.txt"),
            "# header\r\n" + first.FileName.String + "\r\n" + LockedPlugin + "\r\n");
        File.WriteAllText(Path.Combine(profile, "plugins.txt"),
            "*" + first.FileName.String + "\r\n*" + LockedPlugin + "\r\n");
        File.WriteAllText(Path.Combine(profile, "modlist.txt"), "# header\r\n+AssetMod\r\n+PluginMod\r\n");
        File.WriteAllText(Path.Combine(profile, "Skyrim.ini"), "[Archive]\r\nsResourceArchiveList=\r\n");

        _profile = profile;
        Svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(Root, "houseCARL.user.json")));
    }

    readonly string _profile;

    /// <summary>Bump the profile's own stamp so the next call re-resolves and re-opens the plugins, rather than
    /// answering off the build the constructor already made before the file was locked.</summary>
    public void ForceRebuild()
    {
        var loadOrder = Path.Combine(_profile, "loadorder.txt");
        File.WriteAllText(loadOrder, File.ReadAllText(loadOrder) + "\r\n");
    }

    public void Dispose()
    {
        Svc.Dispose();
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}
