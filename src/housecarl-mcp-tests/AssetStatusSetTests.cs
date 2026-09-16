using System.Text.Json;
using HousecarlCore;
using HousecarlMcp;
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
                                   new[] { new FaceGenSeed(AssetSelectWorld.SplitFormId, Mutagen.Bethesda.Plugins.FormKey.Factory(AssetSelectWorld.SplitFormId), null) });

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
                                       new[] { new FaceGenSeed(AssetSelectWorld.MatchedFormId, Mutagen.Bethesda.Plugins.FormKey.Factory(AssetSelectWorld.MatchedFormId), null) });
        Assert.All(clean.Results, r => Assert.False(r.PairDiffers));
    }

    /// <summary>Two ARCHIVES of ONE mod is not a split. It is how the vanilla game ships every bake — heads in
    /// "Skyrim - Meshes0.bsa", tints in "Skyrim - Textures0.bsa" — so a verdict taken off the provider NAME calls
    /// most of the order split. On the ARR order that was 2,719 NPCs against 375 decided on the owning mod.</summary>
    [Fact]
    public void TwoArchivesOfOneModAreNotASplit()
    {
        var d = _w.Svc.AssetStatus(Array.Empty<string>(), null, 0, 0,
                                   new[] { new FaceGenSeed(AssetSelectWorld.ArchivePairFormId, Mutagen.Bethesda.Plugins.FormKey.Factory(AssetSelectWorld.ArchivePairFormId), null) });

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
        Assert.DoesNotContain("win from DIFFERENT sources", text);
    }

    /// <summary>A malformed FormID is ONE error row, not a failed call — the same posture a malformed asset path
    /// takes on this tool.</summary>
    [Fact]
    public void AMalformedFormIdIsOneErrorRowAndNotAFailedCall()
    {
        var d = _w.Svc.AssetStatus(Array.Empty<string>(), null, 0, 0,
                                   new[]
                                   {
                                       new FaceGenSeed("not-a-formid", null, "not a FormID: bad token. Expected 'XXXXXX:Plugin.esp'."),
                                       new FaceGenSeed(AssetSelectWorld.MatchedFormId, Mutagen.Bethesda.Plugins.FormKey.Factory(AssetSelectWorld.MatchedFormId), null),
                                   });

        Assert.Equal(3, d.Results.Count);                       // one error row, then the good NPC's two halves
        Assert.Single(d.Results.Where(r => r.Error is not null));
        Assert.Equal(2, d.Results.Count(r => r.Error is null && r.Hit!.Exists));
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
            // and the manifest says out loud that it carries no record fingerprint instead of failing the call.
            Assert.Equal("", manifest.GetProperty("epoch").GetString());
            Assert.Contains(manifest.GetProperty("notes").EnumerateArray(),
                            n => n.GetString()!.Contains("'epoch' is EMPTY", StringComparison.Ordinal));

            var rows = lines.Skip(1).Where(l => l.Length > 0).Select(l => JsonDocument.Parse(l).RootElement).ToList();
            Assert.Equal(6, rows.Count);
            var split = rows.Single(r => r.GetProperty("path").GetString()!.EndsWith(".nif", StringComparison.OrdinalIgnoreCase)
                                      && r.GetProperty("formid").GetString() == AssetSelectWorld.SplitFormId);
            Assert.Equal("FaceHigher", split.GetProperty("winner").GetString());
            Assert.Equal("loose", split.GetProperty("winner_kind").GetString());
            Assert.Equal("mesh", split.GetProperty("slot").GetString());
            Assert.Equal("FaceBase", split.GetProperty("pair_winner").GetString());
            Assert.True(split.GetProperty("pair_differs").GetBoolean());

            // The file is re-enterable by construction: its identity column extracts to exactly the six paths.
            var (m2, tokens, rerr) = ResultArtifact.ReadIdentity(file, File.ReadAllText(file));
            Assert.Null(rerr);
            Assert.Equal("path", m2!.Identity);
            Assert.Equal(6, tokens!.Count);
            Assert.Equal(rows.Select(r => r.GetProperty("path").GetString()), tokens);

            // And consuming it SERVER-side is epoch-checked: this world builds no load order, so re-entry refuses by
            // name rather than reading the snapshot as if it were current.
            var again = AssetTools.AssetStatus(_w.Svc, new[] { "@" + file });
            Assert.Contains("could not build a load order to check it against", again);
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

        // A plain path row is the document it always was: no formid, no slot, no pair.
        var plain = JsonDocument.Parse(
            AssetTools.AssetStatus(_w.Svc, new[] { _w.Rel("0001.nif") }, format: "json")).RootElement
            .GetProperty("results")[0];
        Assert.False(plain.TryGetProperty("formid", out _));
        Assert.False(plain.TryGetProperty("pair", out _));
    }
}
