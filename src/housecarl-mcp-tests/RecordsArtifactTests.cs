using System.Text.RegularExpressions;
using HousecarlCore;
using System.Text.Json;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

// The artifact disposition on housecarl_records: to_file, auto-spill at the render ceiling, @artifact
// re-entry, the epoch check, the results store's own hygiene, and error-row identity. The lanes exercised are
// project.form='identity', 'everything', and the types=/where= scan.

/// <summary>to_file, auto-spill, re-entry, the store's refusals, and error-row identity — everything that
/// reads one stable build.</summary>
[Trait("tier", "integration")]
public sealed class RecordsArtifactTests : ArtifactTestBase, IClassFixture<ArtifactFixture>
{
    public RecordsArtifactTests(ArtifactFixture f) : base(f) { }

    /// <summary>The scan population these tests measure against, read off the product's own accounting rather
    /// than counted by hand here.</summary>
    int SpellTotal => Je(RecordsTools.Records(Svc, types: new[] { "SPEL" }, format: "json")).GetProperty("total").GetInt32();
    int WeaponTotal => Je(RecordsTools.Records(Svc, types: new[] { "WEAP" }, format: "json")).GetProperty("total").GetInt32();

    // ---- to_file — the forced disposition ----------------------------------------------------------

    [Fact]
    public void ToFile_TheArtifactIsCompleteAndStampedWithTheScannedBuild()
    {
        var art = Art("complete.jsonl");
        RecordsTools.Records(Svc, types: new[] { "SPEL" }, to_file: art);
        var m = ManifestOf(art);
        Assert.Equal(SpellTotal, m.RowCount);
        Assert.Equal(m.RowCount, m.Total);
        Assert.Equal(W.Epoch0, m.Epoch);
    }

    /// <summary>The manifest's provenance stamp records WHICH TOOL wrote the artifact, and it is read back into
    /// a live re-entry refusal ("artifact '…' (from X) …"), so a wrong stamp is a wrong name in a sentence a
    /// caller reads.</summary>
    [Fact]
    public void ToFile_TheManifestStampsTheToolThatActuallyWroteIt_Scan()
    {
        var art = Art("provenance-scan.jsonl");
        RecordsTools.Records(Svc, types: new[] { "SPEL" }, to_file: art);

        Assert.Equal(ToolNames.Records, ManifestOf(art).Tool);
    }

    /// <summary>The identity writer is a second stamp site on a different code path, so it gets its own
    /// test.</summary>
    [Fact]
    public void ToFile_TheManifestStampsTheToolThatActuallyWroteIt_Identity()
    {
        var art = Art("provenance-identity.jsonl");
        RecordsTools.Records(Svc, formids: W.SpellBodies.Select(b => RecordsWorld.Fid(b.FormKey)).ToArray(),
                             project: new RecordsTools.RecordsProject { form = "identity" }, to_file: art);

        Assert.Equal(ToolNames.Records, ManifestOf(art).Tool);
    }

    /// <summary>The stamp population read out of the writer's own source rather than listed here: every
    /// <c>writer.Save(path, …)</c> call site in <c>Artifacts.cs</c> must pass the <c>ToolNames</c> constant, not
    /// a literal, so a new writer is covered without an edit here.</summary>
    [Fact]
    public void EveryArtifactWritersProvenanceStampInterpolatesTheToolNameConstant()
    {
        var src = File.ReadAllText(Path.Combine(HarnessPaths.RepoRoot, "src", "housecarl-mcp", "Artifacts.cs"));
        var sites = Regex.Matches(src, @"writer\.Save\(path,\s*([^,]+),");

        Assert.True(sites.Count >= 8, $"only {sites.Count} writer.Save sites found — the scan went vacuous");
        foreach (Match m in sites)
            Assert.Equal("ToolNames.Records", m.Groups[1].Value.Trim());
    }

    /// <summary>For every form the tool declares — the list read out of the tool's own refusal, so it cannot
    /// drift from the surface — a served <c>to_file=</c> call must stamp the constant. Forms that refuse
    /// <c>to_file=</c> are skipped and counted, never silently passed.</summary>
    [Fact]
    public void EveryFormThatSpillsToAFile_StampsTheToolThatWroteIt()
    {
        var refusal = RecordsTools.Records(Svc, types: new[] { "SPEL" },
                                           project: new RecordsTools.RecordsProject { form = "not-a-form" });
        var forms = Regex.Match(refusal, @"use ([a-z_ |]+)\.").Groups[1].Value
                         .Split('|').Select(f => f.Trim()).Where(f => f.Length > 0).ToList();
        Assert.True(forms.Count >= 8, "the tool's form vocabulary did not parse: " + refusal);

        int stamped = 0;
        foreach (var form in forms)
        {
            var art = Art("provenance-" + form + ".jsonl");
            var project = new RecordsTools.RecordsProject { form = form };
            if (form == "fields") project.fields = new[] { "EditorID" };
            if (form == "aggregate") project.group_by = "type";
            var r = form is "delta" or "tree" or "chain" or "info_order"
                ? "skip"
                : RecordsTools.Records(Svc, types: new[] { "SPEL" }, project: project, to_file: art);
            if (r == "skip" || r.StartsWith("error:", StringComparison.Ordinal) || !File.Exists(art)) continue;
            Assert.Equal(ToolNames.Records, ManifestOf(art).Tool);
            stamped++;
        }
        Assert.True(stamped >= 3, $"only {stamped} forms produced an artifact — the arm went vacuous");
    }

    [Fact]
    public void ToFile_TheManifestTypeCountsCountTheRows()
    {
        var art = Art("typecounts.jsonl");
        RecordsTools.Records(Svc, types: new[] { "SPEL" }, to_file: art);
        var m = ManifestOf(art);
        var only = Assert.Single(m.TypeCounts!);   // one scanned type, so the count IS the row count
        Assert.Equal(m.RowCount, only.Value);
    }

    [Fact]
    public void ToFile_TheTextResponsesSpilledMarkerNamesTheFile()
    {
        // Prose-only: the marker is a rendered sentence, and the datum it must carry is the path itself.
        var art = Art("named.jsonl");
        var r = RecordsTools.Records(Svc, types: new[] { "SPEL" }, to_file: art);
        Assert.Contains("spilled:", r);
        Assert.Contains(art, r);
    }

    [Fact]
    public void ToFileJson_TheSpilledMarkerRidesInTheDocumentWithThePathAndReason()
    {
        var art = Art("json-marker.jsonl");
        var sp = Je(RecordsTools.Records(Svc, types: new[] { "SPEL" }, format: "json", to_file: art)).GetProperty("spilled");
        Assert.Equal(art, sp.GetProperty("path").GetString());
        Assert.Equal("to_file", sp.GetProperty("reason").GetString());
    }

    [Fact]
    public void ToFileJson_TheRowsAreOmittedWhileTheTrueTotalStaysIntact()
    {
        var art = Art("json-total.jsonl");
        var d = Je(RecordsTools.Records(Svc, types: new[] { "SPEL" }, format: "json", to_file: art));
        Assert.Equal(0, d.GetProperty("matches").GetArrayLength());
        Assert.Equal(SpellTotal, d.GetProperty("total").GetInt32());
    }

    [Fact]
    public void AnArtifactDeclaringNoIdentityColumnRefusesReEntryByName()
    {
        // records refuses aggregate+to_file up front, so the count-table file is built here through the same
        // writer that lane used.
        var p = CountTableArtifact("counts.jsonl");
        var (_, _, err) = ResultArtifact.ReadIdentity(p, File.ReadAllText(p));
        Assert.Contains("NO identity column", err);
    }

    [Fact]
    public void ToFile_ARelativePathIsRefusedNamingTheAbsoluteRequirement() =>
        Refused(RecordsTools.Records(Svc, types: new[] { "SPEL" }, to_file: "relative.jsonl"), "ABSOLUTE");

    [Fact]
    public void ToFile_ANonJsonlNameIsRefusedSoTheFileSaysWhatItIs() =>
        Refused(RecordsTools.Records(Svc, types: new[] { "SPEL" }, to_file: Art("x.csv")), ".jsonl");

    [Fact]
    public void ToFilePlusOffset_IsRefusedBecauseTheArtifactIsNeverAWindow() =>
        Refused(RecordsTools.Records(Svc, types: new[] { "SPEL" }, offset: 2, to_file: Art("y.jsonl")), "offset");

    // ---- auto-spill at the inline ceiling ----------------------------------------------------------

    const int TinyScan = 200;    // below the scan render's floor for this world
    const int TinyList = 120;    // below the identity render's floor for three rows
    const int TinyBody = 300;    // below the full-body render's floor for three records

    [Fact]
    public void Control_MaxCharsTruncatesTheInlineTextRender()
    {
        using var d = OwnResults("control-trunc");
        Assert.Contains("[truncated:", RecordsTools.Records(Svc, types: new[] { "WEAP" }, max_chars: TinyScan));
    }

    [Fact]
    public void AnAutoSpillAnnouncesTheCompleteResultWithItsRowCountNotTheRenderedPrefix()
    {
        using var d = OwnResults("complete-marker");
        var r = RecordsTools.Records(Svc, types: new[] { "WEAP" }, max_chars: TinyScan);
        Assert.Contains($"spilled: complete result ({WeaponTotal} rows)", r);
    }

    [Theory]
    [MemberData(nameof(Transports), MemberType = typeof(ArtifactTestBase))]
    public void AnAutoSpillNamesTheResultsDirFileThatActuallyExists(string format)
    {
        using var d = OwnResults("names-" + format);
        var r = RecordsTools.Records(Svc, types: new[] { "WEAP" }, format: format, max_chars: TinyScan);
        var path = TheSpill(d);
        // The file NAME, not the full path: a json render escapes the path's separators, and the name is
        // unique inside this test's own results directory.
        Assert.Contains(Path.GetFileName(path), r);
    }

    [Fact]
    public void AnAutoSpilledArtifactHoldsEveryRowStampedWithTheScannedBuild()
    {
        using var d = OwnResults("spill-rows");
        RecordsTools.Records(Svc, types: new[] { "WEAP" }, max_chars: TinyScan);
        var m = ManifestOf(TheSpill(d));
        Assert.Equal(WeaponTotal, m.RowCount);
        Assert.Equal(W.Epoch0, m.Epoch);
    }

    [Fact]
    public void AutoSpillJson_TruncatedIsTrueAlongsideASpilledPathThatResolves()
    {
        using var d = OwnResults("json-truncated");
        var doc = Je(RecordsTools.Records(Svc, types: new[] { "WEAP" }, format: "json", max_chars: TinyScan));
        Assert.True(doc.GetProperty("truncated").GetBoolean());
        Assert.True(File.Exists(doc.GetProperty("spilled").GetProperty("path").GetString()!));
    }

    [Fact]
    public void TheBodyLaneAutoSpillsItsCompleteRowsWhenTheRenderIsTruncated()
    {
        using var d = OwnResults("body-spill");
        var r = RecordsTools.Records(Svc, formids: Ids, project: Everything, max_chars: TinyBody);
        Assert.Contains("spilled:", r);
        Assert.Equal(Ids.Length, ManifestOf(TheSpill(d)).RowCount);
    }

    [Fact]
    public void TheIdentityLaneAutoSpillsUnderTheSameContract()
    {
        using var d = OwnResults("identity-spill");
        var r = RecordsTools.Records(Svc, formids: Ids, project: Identity, max_chars: TinyList);
        Assert.Contains("spilled:", r);
        Assert.Equal(Ids.Length, ManifestOf(TheSpill(d)).RowCount);
    }

    [Fact]
    public void AFailedAutoSpillIsNamedLoudInTheTextResponse()
    {
        // Prose-only: the failure has no value to report — the datum is that no file was produced.
        using var d = UncreatableResults("failed-text");
        var r = RecordsTools.Records(Svc, types: new[] { "WEAP" }, max_chars: TinyScan);
        Assert.Contains("[truncated:", r);
        Assert.Contains("could NOT be written", r);
        Assert.Contains("exists NOWHERE", r);
    }

    [Fact]
    public void AFailedAutoSpillRidesTheJsonDocumentAsSpillError()
    {
        using var d = UncreatableResults("failed-json");
        var doc = Je(RecordsTools.Records(Svc, types: new[] { "WEAP" }, format: "json", max_chars: TinyScan));
        Assert.True(doc.TryGetProperty("spill_error", out _));
    }

    // ---- @file re-entry against the same build -----------------------------------------------------

    [Fact]
    public void ArtifactReEntry_TheBodyLaneReadsEveryIdentityInTheFile()
    {
        var art = IdentityArtifact("reenter-body.jsonl");
        var d = Je(RecordsTools.Records(Svc, formids: new[] { "@" + art }, project: Everything,
                                        format: "json", counts_only: true));
        Assert.Equal(TokensOf(art).Count, d.GetProperty("count").GetInt32());
    }

    [Fact]
    public void ArtifactReEntry_TheWhereGrammarMembershipTestTakesTheSameFile()
    {
        var art = IdentityArtifact("reenter-where.jsonl");
        var d = Je(RecordsTools.Records(Svc, types: new[] { "WEAP" }, where: new[] { $"formid in @{art}" }, format: "json"));
        Assert.Equal(TokensOf(art).Count, d.GetProperty("total").GetInt32());
    }

    [Fact]
    public void APlainAtFileListStillEntersWithNoManifestAndNoEpochClaim()
    {
        var plain = PlainList("plain.txt", 2);
        var d = Je(RecordsTools.Records(Svc, formids: new[] { "@" + plain }, project: Identity,
                                        format: "json", counts_only: true));
        Assert.Equal(2, d.GetProperty("count").GetInt32());
    }

    [Fact]
    public void AnAtFileEntryMixedWithInlineEntriesIsRefusedNamed() =>
        Refused(RecordsTools.Records(Svc, formids: new[] { "@" + PlainList("mixed-plain.txt", 2), Ids[0] }),
                "IN PLACE OF the whole list");

    [Fact]
    public void ARelativeAtPathIsRefusedNamed() =>
        Refused(RecordsTools.Records(Svc, formids: new[] { "@relative.txt" }), "ABSOLUTE");

    [Fact]
    public void ANoIdentityArtifactIsRefusedAtTheFormidsDoorToo() =>
        Refused(RecordsTools.Records(Svc, formids: new[] { "@" + CountTableArtifact("counts-door.jsonl") }),
                "NO identity column");

    // ---- the spill marker tells the truth about what it holds --------------------------------------

    [Fact]
    public void AWindowedAutoSpillSaysWindowAndNeverClaimsTheCompleteResult()
    {
        using var d = OwnResults("windowed");
        var r = RecordsTools.Records(Svc, types: new[] { "WEAP" }, limit: 2, max_chars: TinyScan);
        Assert.Contains($"spilled: the returned WINDOW (2 rows of {WeaponTotal} total matches)", r);
        Assert.DoesNotContain("complete result", r);
    }

    [Fact]
    public void AWindowedAutoSpillNamesWhereTheMissingMatchesAre()
    {
        // Prose-only: "nowhere" is not a value the response can carry as a number.
        using var d = OwnResults("windowed-missing");
        Assert.Contains("beyond limit= are in NO file",
                        RecordsTools.Records(Svc, types: new[] { "WEAP" }, limit: 2, max_chars: TinyScan));
    }

    [Fact]
    public void WindowedSpillJson_CarriesCompleteFalseWithRowCountAndTotalAsData()
    {
        using var d = OwnResults("windowed-json");
        var sp = Je(RecordsTools.Records(Svc, types: new[] { "WEAP" }, limit: 2, format: "json", max_chars: TinyScan))
                 .GetProperty("spilled");
        Assert.False(sp.GetProperty("complete").GetBoolean());
        Assert.Equal(2, sp.GetProperty("row_count").GetInt32());
        Assert.Equal(WeaponTotal, sp.GetProperty("total").GetInt32());
    }

    [Fact]
    public void Control_AnUnwindowedSpillIsCompleteTrue()
    {
        using var d = OwnResults("unwindowed-json");
        var sp = Je(RecordsTools.Records(Svc, types: new[] { "WEAP" }, format: "json", max_chars: TinyScan))
                 .GetProperty("spilled");
        Assert.True(sp.GetProperty("complete").GetBoolean());
    }

    [Fact]
    public void AFailedToFileUnderJsonReturnsAParseableErrorAndEpochDocument()
    {
        var doc = Je(RecordsTools.Records(Svc, types: new[] { "SPEL" }, format: "json",
                                          to_file: UnwritableTarget("blocked-scan", "x.jsonl")));
        Assert.Contains("could not write", doc.GetProperty("error").GetString());
        Assert.Equal(W.Epoch0, doc.GetProperty("epoch").GetString());
    }

    [Fact]
    public void AFailedToFileOnTheBodyLaneParsesToo()
    {
        var doc = Je(RecordsTools.Records(Svc, formids: new[] { Ids[0] }, project: Everything, format: "json",
                                          to_file: UnwritableTarget("blocked-body", "y.jsonl")));
        Assert.True(doc.TryGetProperty("error", out _));
    }

    [Fact]
    public void AWhitespaceOnlyFormidsElementStaysAPerItemErrorAndTheBatchSurvives()
    {
        var ids = new[] { Ids[0], "  " };
        var d = Je(RecordsTools.Records(Svc, formids: ids, project: Identity, format: "json", counts_only: true));
        Assert.Equal(2, d.GetProperty("count").GetInt32());
        Assert.Equal(1, d.GetProperty("errors").GetInt32());
        Assert.DoesNotContain("failed unexpectedly", RecordsTools.Records(Svc, formids: ids, project: Identity));
    }

    [Fact]
    public void ToFileIntoTheServersResultsDirectoryIsRefusedNamingThePruneHazard() =>
        Refused(RecordsTools.Records(Svc, types: new[] { "SPEL" }, to_file: Path.Combine(ResultsDir, "mine.jsonl")),
                "pruned by age");

    // ---- error rows are not identity-bearing -------------------------------------------------------

    [Fact]
    public void AnArtifactKeepsItsErrorRowWhileIdentityExtractionYieldsOnlyTheResolvedFormids()
    {
        var art = Art("mixed.jsonl");
        RecordsTools.Records(Svc, formids: new[] { "garbage", Ids[0], Ids[1] }, project: Identity, to_file: art);
        Assert.Equal(3, ManifestOf(art).RowCount);
        Assert.Equal(2, TokensOf(art).Count);
    }

    [Fact]
    public void AMixedArtifactReEntersOnItsResolvedRowsWithNoWasItEditedMisdiagnosis()
    {
        var art = Art("mixed-reenter.jsonl");
        RecordsTools.Records(Svc, formids: new[] { "garbage", Ids[0], Ids[1] }, project: Identity, to_file: art);
        var d = Je(RecordsTools.Records(Svc, formids: new[] { "@" + art }, project: Identity,
                                        format: "json", counts_only: true));
        Assert.Equal(2, d.GetProperty("count").GetInt32());
        Assert.DoesNotContain("was it edited", RecordsTools.Records(Svc, formids: new[] { "@" + art }, project: Identity));
    }

    [Fact]
    public void TheWhereGrammarMembershipTestAgreesWithAMixedArtifactsResolvedRows()
    {
        var art = Art("mixed-where.jsonl");
        RecordsTools.Records(Svc, formids: new[] { "garbage", Ids[0], Ids[1] }, project: Identity, to_file: art);
        var d = Je(RecordsTools.Records(Svc, types: new[] { "WEAP" }, where: new[] { $"formid in @{art}" }, format: "json"));
        Assert.Equal(2, d.GetProperty("total").GetInt32());
    }

    [Fact]
    public void ABodyArtifactsParseFailureRowIsSkippedTheSameWay()
    {
        var art = Art("bmixed.jsonl");
        RecordsTools.Records(Svc, formids: new[] { "notaformid", Ids[0] }, project: Everything, to_file: art);
        Assert.Equal(2, ManifestOf(art).RowCount);
        Assert.Equal(Ids[0], Assert.Single(TokensOf(art)));
    }

    [Fact]
    public void AnAllErrorArtifactIsRefusedByItsRealCauseNeverByAccusingTheFile()
    {
        var art = Art("allerr.jsonl");
        RecordsTools.Records(Svc, formids: new[] { "garbage1", "garbage2" }, project: Identity, to_file: art);
        var r = RecordsTools.Records(Svc, formids: new[] { "@" + art }, project: Identity);
        Refused(r, "ERROR rows");
        Assert.DoesNotContain("was it edited", r);
    }

    // ---- fixture builders --------------------------------------------------------------------------

    /// <summary>An identity artifact over the world's weapons — the file the re-entry tests read back.</summary>
    string IdentityArtifact(string name)
    {
        var art = Art(name);
        var r = RecordsTools.Records(Svc, formids: Ids, project: Identity, to_file: art);
        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), r);
        return art;
    }

    /// <summary>A plain (non-artifact) @file list of the first <paramref name="n"/> weapons.</summary>
    string PlainList(string name, int n)
    {
        var p = Art(name);
        File.WriteAllText(p, string.Join("\r\n", Ids.Take(n)));
        return p;
    }

    /// <summary>A count-table artifact: rows with no per-record identity column.</summary>
    string CountTableArtifact(string name)
    {
        var p = Art(name);
        using var w = new ResultArtifact.Writer();
        w.WriteRow((jw, _) =>
        {
            jw.WriteStartObject();
            jw.WriteString("key", W.OverrideName);
            jw.WriteNumber("count", 1);
            jw.WriteEndObject();
        });
        var (_, err) = w.Save(p, ToolNames.Records, Array.Empty<KeyValuePair<string, string>>(),
                              identity: null, new[] { "key", "count" }, "count desc, then key asc", 1, W.Epoch0!);
        Assert.Null(err);
        return p;
    }

    /// <summary>An auto-spill directory that CANNOT be created: its parent is a file.</summary>
    ResultsDirScope UncreatableResults(string name)
    {
        var blocker = W.Scratch("blockers", name);
        File.WriteAllText(blocker, "a file where the results directory should be");
        return new ResultsDirScope(Path.Combine(blocker, "sub"), create: false);
    }

    /// <summary>A to_file target that passes validation and then cannot be written: its parent is a file.</summary>
    string UnwritableTarget(string name, string leaf)
    {
        var blocker = W.Scratch("blockers", name);
        File.WriteAllText(blocker, "a file where a directory should be");
        return Path.Combine(blocker, "sub", leaf);
    }
}

/// <summary>
/// The epoch check. This class owns its own world per test: every test changes a plugin's mtime, which
/// re-fingerprints the build — a shared world would hand the change to whatever ran next.
/// </summary>
[Trait("tier", "integration")]
public sealed class RecordsArtifactEpochTests : IDisposable
{
    readonly RecordsWorld _w = new();
    readonly string? _priorResultsDir;

    public RecordsArtifactEpochTests()
    {
        _priorResultsDir = ResultsStore.OverrideDirForTests;
        var dir = Path.Combine(_w.Root, "server-results");
        Directory.CreateDirectory(dir);
        ResultsStore.OverrideDirForTests = dir;
    }

    public void Dispose()
    {
        ResultsStore.OverrideDirForTests = _priorResultsDir;
        _w.Dispose();
    }

    string[] Ids => _w.Weapons.Select(RecordsWorld.Fid).ToArray();
    static RecordsTools.RecordsProject Identity => new() { form = "identity" };
    static RecordsTools.RecordsProject Everything => new() { form = "everything" };

    string ArtifactPath(string name)
    {
        var p = System.IO.Path.Combine(_w.Root, "artifacts", name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
        return p;
    }

    string WriteArtifact(string name)
    {
        var p = ArtifactPath(name);
        var r = RecordsTools.Records(_w.Svc, formids: Ids, project: Identity, to_file: p);
        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), r);
        return p;
    }

    /// <summary>Write the artifact, then change the load order so the artifact's epoch is stale.</summary>
    string Stale(string name)
    {
        var p = WriteArtifact(name);
        File.SetLastWriteTimeUtc(_w.OverrideFile, DateTime.UtcNow.AddHours(1));
        return p;
    }

    /// <summary>An epoch in the shape a houseCARL from before the fingerprint formula changed wrote: 16 bare hex
    /// chars, no format tag.</summary>
    const string PreUpgradeEpoch = "0123456789abcdef";

    /// <summary>Write the artifact, then rewrite its manifest epoch into the pre-upgrade shape — the state every
    /// artifact on disk is in the moment a user upgrades across a formula change, with NOTHING about the load
    /// order having moved.</summary>
    string PreUpgrade(string name)
    {
        var p = WriteArtifact(name);
        var text = File.ReadAllText(p);
        Assert.Contains(_w.Epoch0!, text);
        File.WriteAllText(p, text.Replace(_w.Epoch0!, PreUpgradeEpoch));
        return p;
    }

    string Now => _w.Svc.Stats().epoch!;

    [Fact]
    public void Control_TheBuildReFingerprintsWhenAPluginsContentSignalChanges()
    {
        var before = _w.Epoch0;
        File.SetLastWriteTimeUtc(_w.OverrideFile, DateTime.UtcNow.AddHours(1));
        Assert.NotEqual(before, Now);
    }

    [Fact]
    public void StaleReEntry_TheBodyLaneRefusalNamesBothEpochsAndTheNoOverridePosture()
    {
        var art = Stale("stale-body.jsonl");
        var now = Now;
        var r = RecordsTools.Records(_w.Svc, formids: new[] { "@" + art }, project: Everything);
        Assert.StartsWith("error:", r);
        Assert.Contains(_w.Epoch0!, r);
        Assert.Contains(now, r);
        Assert.Contains("the load order changed", r);
        Assert.Contains("no stale-override", r);
    }

    /// <summary>An artifact written before the fingerprint formula changed mismatches over a load order nothing
    /// touched, so the refusal has to name the upgrade, not blame the order it cannot speak for.</summary>
    [Fact]
    public void PreUpgradeReEntry_TheRefusalBlamesTheFormulaChangeAndNotTheLoadOrder()
    {
        var art = PreUpgrade("pre-upgrade.jsonl");
        var now = Now;
        var r = RecordsTools.Records(_w.Svc, formids: new[] { "@" + art }, project: Everything);
        Assert.StartsWith("error:", r);
        Assert.Contains($"epoch={PreUpgradeEpoch}", r);
        Assert.Contains($"epoch={now}", r);
        Assert.Contains("written by an OLDER houseCARL", r);
        // The load order was never touched, so the refusal may not claim it was.
        Assert.DoesNotContain("the load order changed", r);
        Assert.Contains("Re-run the producing query", r);
        Assert.Contains("no stale-override", r);
    }

    [Fact]
    public void StaleReEntry_TheRefusalIsStampedWithTheBuildItConsulted()
    {
        var art = Stale("stale-stamp.jsonl");
        var now = Now;
        var r = RecordsTools.Records(_w.Svc, formids: new[] { "@" + art }, project: Everything);
        // A served response stamps the same epoch, so without the refusal assertion this test would pass with
        // the epoch check gone entirely.
        Assert.StartsWith("error:", r);
        Assert.Contains($"epoch={now}", r);
    }

    [Fact]
    public void StaleReEntryJson_IsAnErrorAndEpochDocument()
    {
        var art = Stale("stale-json.jsonl");
        var now = Now;
        var d = JsonDocument.Parse(RecordsTools.Records(_w.Svc, formids: new[] { "@" + art }, project: Everything,
                                                        format: "json")).RootElement;
        Assert.True(d.TryGetProperty("error", out _));
        Assert.Equal(now, d.GetProperty("epoch").GetString());
    }

    [Fact]
    public void StaleReEntry_TheWhereGrammarPredicateRefusesTheSameWayStamped()
    {
        var art = Stale("stale-where.jsonl");
        var now = Now;
        var r = RecordsTools.Records(_w.Svc, types: new[] { "WEAP" }, where: new[] { $"formid in @{art}" });
        // The scan lane emits its envelope header before the refusal line, so the refusal is the SECOND line
        // here — unlike the list lanes, whose refusal is the whole response.
        Assert.Contains("\nerror:", r);
        Assert.Contains(_w.Epoch0!, r);
        Assert.Contains($"epoch={now}", r);
    }

    [Fact]
    public void Control_APlainListStillEntersAfterTheOrderChanges_ItNeverClaimedABuild()
    {
        var plain = ArtifactPath("plain.txt");
        File.WriteAllText(plain, string.Join("\r\n", Ids.Take(2)));
        File.SetLastWriteTimeUtc(_w.OverrideFile, DateTime.UtcNow.AddHours(1));
        var d = JsonDocument.Parse(RecordsTools.Records(_w.Svc, formids: new[] { "@" + plain }, project: Identity,
                                                        format: "json", counts_only: true)).RootElement;
        Assert.Equal(2, d.GetProperty("count").GetInt32());
    }

    [Fact]
    public void ToFile_OverwritesTheStaleArtifactWholesaleWithTheNewBuildsResult()
    {
        var art = Stale("rematerialize.jsonl");
        var stale = ReadEpoch(art);
        var now = Now;
        RecordsTools.Records(_w.Svc, formids: Ids, project: Identity, to_file: art);
        Assert.Equal(now, ReadEpoch(art));
        Assert.NotEqual(stale, ReadEpoch(art));
    }

    [Fact]
    public void ReEntryIsCleanAgainstTheReMaterializedArtifact()
    {
        var art = Stale("clean-again.jsonl");
        RecordsTools.Records(_w.Svc, formids: Ids, project: Identity, to_file: art);
        var d = JsonDocument.Parse(RecordsTools.Records(_w.Svc, formids: new[] { "@" + art }, project: Identity,
                                                        format: "json", counts_only: true)).RootElement;
        Assert.Equal(Ids.Length, d.GetProperty("count").GetInt32());
    }

    static string ReadEpoch(string path)
    {
        var (m, _, err) = ResultArtifact.ReadIdentity(path, File.ReadAllText(path));
        Assert.Null(err);
        return m!.Epoch;
    }
}

/// <summary>The core writer/reader pair the whole disposition rests on — no load order needed.</summary>
[Trait("tier", "unit")]
public sealed class RecordsArtifactRoundTripTests : IDisposable
{
    const string Epoch = "abcdef0123456789";

    readonly string _dir = Path.Combine(Path.GetTempPath(), "hc-artifact-roundtrip-" + Guid.NewGuid().ToString("N"));

    public RecordsArtifactRoundTripTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (Exception) { /* temp cleanup */ } }

    (string Path, string? Error, ResultArtifact.Manifest? Manifest) Write()
    {
        var p = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".jsonl");
        using var w = new ResultArtifact.Writer();
        w.WriteRow((jw, _) =>
        {
            jw.WriteStartObject(); jw.WriteString("formid", "000001:A.esp"); jw.WriteString("type", "Weapon"); jw.WriteEndObject();
        }, "Weapon");
        w.WriteRow((jw, _) =>
        {
            jw.WriteStartObject(); jw.WriteString("formid", "000002:A.esp"); jw.WriteString("type", "Armor"); jw.WriteEndObject();
        }, "Armor");
        var (m, err) = w.Save(p, ToolNames.Records, new List<KeyValuePair<string, string>> { new("type", "WEAP") },
                              "formid", new[] { "formid", "type" }, "input order", 2, Epoch);
        return (p, err, m);
    }

    [Fact]
    public void TheWriterSavesItsManifestAndItsRows()
    {
        var (_, err, m) = Write();
        Assert.Null(err);
        Assert.Equal(2, m!.RowCount);
    }

    [Fact]
    public void TheSniffRecognizesAManifestOnLine1() =>
        Assert.True(ResultArtifact.LooksLikeArtifact(File.ReadAllText(Write().Path)));

    [Fact]
    public void TheSniffSaysNoToAPlainFormidList() =>
        Assert.False(ResultArtifact.LooksLikeArtifact("000001:A.esp\n000002:A.esp\n"));

    [Fact]
    public void TheManifestFieldsSurviveTheRoundTrip()
    {
        var (path, _, _) = Write();
        var (m, _, err) = ResultArtifact.ReadIdentity(path, File.ReadAllText(path));
        Assert.Null(err);
        Assert.Equal(ToolNames.Records, m!.Tool);
        Assert.Equal(Epoch, m.Epoch);
        Assert.Equal(2, m.RowCount);
        Assert.Equal("formid", m.Identity);
        Assert.Equal(2, m.TypeCounts!.Count);
        Assert.Equal(1, m.TypeCounts["Weapon"]);
        Assert.Equal(1, m.TypeCounts["Armor"]);
    }

    [Fact]
    public void TheIdentityTokensComeBackInRowOrder()
    {
        var (path, _, _) = Write();
        var (_, tokens, err) = ResultArtifact.ReadIdentity(path, File.ReadAllText(path));
        Assert.Null(err);
        Assert.Equal(new[] { "000001:A.esp", "000002:A.esp" }, tokens);
    }
}

/// <summary>The results store's own contracts: reservation, release, and the write-time age prune.</summary>
[Trait("tier", "unit")]
public sealed class RecordsArtifactResultsStoreTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "hc-artifact-store-" + Guid.NewGuid().ToString("N"));
    readonly string? _prior;

    public RecordsArtifactResultsStoreTests()
    {
        Directory.CreateDirectory(_dir);
        _prior = ResultsStore.OverrideDirForTests;
        ResultsStore.OverrideDirForTests = _dir;
    }

    public void Dispose()
    {
        ResultsStore.OverrideDirForTests = _prior;
        try { Directory.Delete(_dir, true); } catch (Exception) { /* temp cleanup */ }
    }

    string Aged(string name, int daysOld)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, "{}");
        File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddDays(-daysOld));
        return p;
    }

    [Fact]
    public void ASpillOlderThanThePruneWindowIsDeletedAtTheNextWrite()
    {
        var old = Aged("stale-spill.jsonl", ResultsStore.PruneAfterDays + 1);
        ResultsStore.NextPath(ToolNames.Records, "0123456789abcdef");
        Assert.False(File.Exists(old));
    }

    [Fact]
    public void AFreshSpillSurvivesTheSameWriteTimePrune()
    {
        var fresh = Aged("fresh-spill.jsonl", 0);
        ResultsStore.NextPath(ToolNames.Records, "0123456789abcdef");
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public void SameSecondReservationsGetDistinctNamesBecauseReservingCreatesTheFile()
    {
        var p1 = ResultsStore.NextPath(ToolNames.Records, "0123456789abcdef");
        var p2 = ResultsStore.NextPath(ToolNames.Records, "0123456789abcdef");
        Assert.NotEqual(p1, p2);
        Assert.True(File.Exists(p1));
        Assert.True(File.Exists(p2));
    }

    [Fact]
    public void ReleaseCleansAFailedSpillsReservation()
    {
        var p = ResultsStore.NextPath(ToolNames.Records, "0123456789abcdef");
        ResultsStore.Release(p);
        Assert.False(File.Exists(p));
    }

    [Fact]
    public void AnOldOrphanedWriterTempIsPrunedLikeAnyStaleSpill()
    {
        var orphan = Aged("half-written.jsonl.tmp-deadbeef", ResultsStore.PruneAfterDays + 1);
        ResultsStore.Release(ResultsStore.NextPath(ToolNames.Records, "0123456789abcdef"));
        Assert.False(File.Exists(orphan));
    }
}
