using System.Text.Json;
using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda.Plugins;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// <c>check to_file=</c> writes one row per finding the family counts, and its manifest's total is that count (#1145).
/// The dialogue report here carries one finding of every kind the count reads, so a kind the rows miss shows as
/// total above row_count instead of a file that says complete.
/// </summary>
[Trait("tier", "unit")]
public sealed class CheckArtifactCountTests : IDisposable
{
    static readonly ModKey Mk = new("HcDaTest", ModType.Plugin);
    static FormKey F(uint id) => new(Mk, id);
    const string Seed = "000800:HcDaTest.esp";
    const string BadSeed = "not-a-formid";

    readonly string _dir = Path.Combine(Path.GetTempPath(), "hc-check-artifact-" + Guid.NewGuid().ToString("N"));

    public CheckArtifactCountTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best-effort */ } }

    /// <summary>One quest seed carrying every dialogue finding kind once, beside non-findings that must not count.</summary>
    static DialogueValidationReport EveryKind()
    {
        var topic = new TopicValidation(F(0x801), "HcDaTopic", "HcDaTest.esp", 6, 0, 0, 2, "Topic", "Custom", "CUST",
            Issues: new[] { new DialogueIssue(DialogueIssueSeverity.Problem, "a topic-level problem") },
            VoiceLines: new[]
            {
                new VoiceLine(F(0x802), "HcDaTopic", 1, "sound/voice/hcdatest.esp/x/a.fuz", false, null, false, "a.lip", false, false),
                new VoiceLine(F(0x803), "HcDaTopic", 1, "sound/voice/hcdatest.esp/x/b.fuz", true, "SomeMod", false, "b.lip", true, false),
            },
            VoiceUndetermined: Array.Empty<VoiceUndetermined>(),
            ScriptFindings: new[]
            {
                new ScriptBindingFinding(F(0x804), "HcDaTopic", ScriptBindingStatus.ScriptNotCompiled,
                                         new[] { "HcDaScript" }, new[] { "Scripts/HcDaScript.pex" }, false, "no compiled .pex"),
                new ScriptBindingFinding(F(0x805), "HcDaTopic", ScriptBindingStatus.BindingIncomplete,
                                         Array.Empty<string>(), Array.Empty<string>(), false, "binds nothing"),
                new ScriptBindingFinding(F(0x806), "HcDaTopic", ScriptBindingStatus.BoundAndCompiled,
                                         new[] { "HcDaOk" }, Array.Empty<string>(), false, "fires"),
                new ScriptBindingFinding(F(0x807), "HcDaTopic", ScriptBindingStatus.Undetermined,
                                         Array.Empty<string>(), Array.Empty<string>(), false, "not located"),
            });
        return new DialogueValidationReport(F(0x800), "quest", "HcDaQuest", "HcDaTest.esp", new[] { topic })
        {
            InputIssues = new[] { new DialogueIssue(DialogueIssueSeverity.Warning, "a quest-level warning") },
            ScanGaps = new[] { "HcDaGone.esp could not be read." },
            SeqLint = new SeqLintFinding(true, "HcDaTest.esp", "HcDaTest.esp", 0x800, false, null, null, null),
        };
    }

    static readonly string[] Kinds =
        { "warning", "scan_error", "seq_unconfirmed", "problem", "silent_line", "script_not_compiled", "binding_incomplete" };

    static DialogueCheckResult Sweep(bool countsOnly = false) =>
        DialogueSweep.Run(() => new DialogueSweep.Binding(
                              _ => EveryKind(),
                              s => s == Seed ? F(0x800) : throw new FormatException("bad"),
                              CheckErrorsFixtures.Epoch),
                          new[] { Seed, BadSeed }, 1000, countsOnly);

    (JsonElement Manifest, JsonElement[] Rows) WriteFile(CheckSweep sweep)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".jsonl");
        var (spill, err) = CheckArtifact.Write(sweep, path, Array.Empty<KeyValuePair<string, string>>());
        Assert.Null(err);
        Assert.NotNull(spill);
        var lines = File.ReadAllLines(path);
        return (JsonDocument.Parse(lines[0]).RootElement.Clone(),
                lines.Skip(1).Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToArray());
    }

    [Fact]
    public void EveryDialogueFindingKindGetsOneRowAndTheTotalIsTheCount()
    {
        var (manifest, rows) = WriteFile(new CheckSweep(CheckErrorsFixtures.Sel("dialogue"), Dialogue: Sweep()));

        var classes = rows.Select(r => r.GetProperty("class").GetString()!).ToArray();
        Assert.Equal(Kinds.Append("seed_unreachable").OrderBy(c => c), classes.OrderBy(c => c));
        Assert.Equal(rows.Length, manifest.GetProperty("row_count").GetInt32());
        Assert.Equal(rows.Length, manifest.GetProperty("total").GetInt32());
    }

    [Fact]
    public void TheFileTotalIsTheCountsOnlyNumberAndTheInlineCount()
    {
        int countsOnly = Sweep(countsOnly: true).ProblemsFound;
        var result = Sweep();
        var (manifest, rows) = WriteFile(new CheckSweep(CheckErrorsFixtures.Sel("dialogue"), Dialogue: result));
        var inline = JsonDocument.Parse(JsonWire.RenderCheck(new CheckSweep(CheckErrorsFixtures.Sel("dialogue"), Dialogue: result), 40000))
                                 .RootElement.GetProperty("families").GetProperty(SweepFamilySelection.Token(SweepFamily.Dialogue));

        Assert.Equal(Kinds.Length, countsOnly);
        Assert.Equal(countsOnly, inline.GetProperty("findings_found").GetInt32());
        Assert.Equal(countsOnly, rows.Count(r => r.GetProperty("class").GetString() != "seed_unreachable"));
        Assert.Equal(countsOnly + result.Unresolved.Count, manifest.GetProperty("total").GetInt32());
    }

    [Fact]
    public void ADialogueRowCarriesTheRecordAndTheFileItIsAbout()
    {
        var (_, rows) = WriteFile(new CheckSweep(CheckErrorsFixtures.Sel("dialogue"), Dialogue: Sweep()));
        JsonElement Of(string cls) => rows.Single(r => r.GetProperty("class").GetString() == cls);

        Assert.Equal(F(0x802).ToString(), Of("silent_line").GetProperty("formid").GetString());
        Assert.Equal("sound/voice/hcdatest.esp/x/a.fuz", Of("silent_line").GetProperty("target").GetString());
        Assert.Equal("Scripts/HcDaScript.pex", Of("script_not_compiled").GetProperty("target").GetString());
        Assert.Equal("HcDaScript", Of("script_not_compiled").GetProperty("script").GetString());
        Assert.Equal("QUST", Of("warning").GetProperty("record_type").GetString());
        Assert.Contains("NO .seq", Of("seq_unconfirmed").GetProperty("detail").GetString());
        Assert.Equal("HcDaGone.esp could not be read.", Of("scan_error").GetProperty("detail").GetString());
    }

    /// <summary>An INFO row names the INFO, so it carries no EditorID rather than its topic's; the silent-line
    /// detail is the inline line's own sentence.</summary>
    [Fact]
    public void AnInfoRowCarriesNoTopicEditorIdAndTheInlineSilentSentence()
    {
        var result = Sweep();
        var (_, rows) = WriteFile(new CheckSweep(CheckErrorsFixtures.Sel("dialogue"), Dialogue: result));
        var text = CheckTextRender.RenderCheck(new CheckSweep(CheckErrorsFixtures.Sel("dialogue"), Dialogue: result), 40000);

        foreach (var r in rows.Where(r => r.GetProperty("record_type").GetString() == "INFO"))
            Assert.Equal(JsonValueKind.Null, r.GetProperty("editorid").ValueKind);
        var silent = rows.Single(r => r.GetProperty("class").GetString() == "silent_line").GetProperty("detail").GetString()!;
        Assert.StartsWith("[!] WILL BE SILENT", silent);
        Assert.Contains(silent, text);
    }

    /// <summary>Seeds limit= never tried count into total, and the manifest-only response says how many.</summary>
    [Fact]
    public void SeedsCutByLimitCountIntoTotalAndTheResponseSaysSo()
    {
        var result = DialogueSweep.Run(() => new DialogueSweep.Binding(_ => EveryKind(), _ => F(0x800), CheckErrorsFixtures.Epoch),
                                       new[] { Seed, "000801:HcDaTest.esp", "000802:HcDaTest.esp" }, 1);
        var sweep = new CheckSweep(CheckErrorsFixtures.Sel("dialogue"), Dialogue: result);
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".jsonl");
        var (spill, err) = CheckArtifact.Write(sweep, path, Array.Empty<KeyValuePair<string, string>>());
        Assert.Null(err);
        var manifest = JsonDocument.Parse(File.ReadLines(path).First()).RootElement;

        Assert.Equal(Kinds.Length, manifest.GetProperty("row_count").GetInt32());
        Assert.Equal(Kinds.Length + 2, manifest.GetProperty("total").GetInt32());
        var response = JsonDocument.Parse(CheckArtifact.RenderManifestOnly(sweep, spill!, json: true, 40000)).RootElement;
        Assert.False(response.GetProperty("spilled").GetProperty("complete").GetBoolean());
        Assert.Contains("2 were NOT reached", response.GetProperty("boundaries").GetProperty("dialogue").GetString());
        Assert.Contains("2 were NOT reached", CheckArtifact.RenderManifestOnly(sweep, spill!, json: false, 40000));
    }

    /// <summary>Errors writes a scan_error row per unscannable sample and counts every unscannable record.</summary>
    [Fact]
    public void UnscannableRecordsAreRowsAndCountIntoTotal()
    {
        var samples = new[] { "000901:HcDaTest.esp — Boom: one", "000902:HcDaTest.esp — Boom: two" };
        var r = CheckErrorsFixtures.Result(
            reports: new[] { new PluginErrors("HcDaTest.esp", Array.Empty<DanglingRef>(), Array.Empty<string>(), 2, samples, null) },
            totalUnscannable: 2);

        var (manifest, rows) = WriteFile(new CheckSweep(CheckErrorsFixtures.Sel("errors"), Errors: r));

        Assert.Equal(samples, rows.Where(x => x.GetProperty("class").GetString() == "scan_error")
                                  .Select(x => x.GetProperty("detail").GetString()));
        Assert.Equal(2, manifest.GetProperty("total").GetInt32());
        Assert.Equal(2, manifest.GetProperty("row_count").GetInt32());
    }

    /// <summary>Scripts counts the unverifiable notes the core collapsed into total, and the response says so.</summary>
    [Fact]
    public void CollapsedUnverifiableNotesCountIntoTotalAndTheResponseSaysSo()
    {
        var rec = new RecordScriptFindings(new FormKey(Mk, 0x920), "QUST", "HcDaQ", "HcDaTest.esp",
            Array.Empty<UnboundProperty>(), Array.Empty<NullObjectProperty>(),
            new[] { new ScriptUnverifiable("HcDaS", "no .pex") });
        var r = ScriptsFixtures.Result(reports: new[] { rec }, totalUnverifiable: 4) with { UnverifiableCollapsed = 3 };
        var sweep = new CheckSweep(CheckErrorsFixtures.Sel("scripts"), Scripts: r);
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".jsonl");
        var (spill, err) = CheckArtifact.Write(sweep, path, Array.Empty<KeyValuePair<string, string>>());
        Assert.Null(err);
        var manifest = JsonDocument.Parse(File.ReadLines(path).First()).RootElement;

        Assert.Equal(1, manifest.GetProperty("row_count").GetInt32());
        Assert.Equal(4, manifest.GetProperty("total").GetInt32());
        Assert.Contains("3 unverifiable note(s)", CheckArtifact.RenderManifestOnly(sweep, spill!, json: false, 40000));
    }

    /// <summary>The errors listing cut by limit= writes fewer rows than it counted, and the total says so.</summary>
    [Fact]
    public void AnErrorsListingCutByLimitShowsTotalAboveRowCount()
    {
        var src = new FormKey(Mk, 0x900);
        var dangling = new[] { new DanglingRef(src, "WEAP", "HcDaWeap", new FormKey(Mk, 0x901)) };
        var r = CheckErrorsFixtures.Result(
            reports: new[] { new PluginErrors("HcDaTest.esp", dangling, Array.Empty<string>(), 0, Array.Empty<string>(), null) },
            totalDangling: 5, limit: 1);

        var (manifest, rows) = WriteFile(new CheckSweep(CheckErrorsFixtures.Sel("errors"), Errors: r));

        Assert.Single(rows);
        Assert.Equal(5, manifest.GetProperty("total").GetInt32());
    }

    /// <summary>The scripts listing cut by limit= the same way.</summary>
    [Fact]
    public void AScriptsListingCutByLimitShowsTotalAboveRowCount()
    {
        var rec = new RecordScriptFindings(new FormKey(Mk, 0x910), "QUST", "HcDaQ", "HcDaTest.esp",
            new[] { new UnboundProperty("HcDaS", "HcDaS", "Prop", "Actor", true) },
            Array.Empty<NullObjectProperty>(), Array.Empty<ScriptUnverifiable>());
        var r = ScriptsFixtures.Result(reports: new[] { rec }, totalUnbound: 3, totalNullObject: 1, limit: 1);

        var (manifest, rows) = WriteFile(new CheckSweep(CheckErrorsFixtures.Sel("scripts"), Scripts: r));

        Assert.Single(rows);
        Assert.Equal(4, manifest.GetProperty("total").GetInt32());
    }
}
