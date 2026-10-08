using System.Text.RegularExpressions;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A filtered config audit cuts a file that does not fit per reference line, non-OK references first (#1093).</summary>
[Trait("tier", "unit")]
public sealed class SkseConfigFilteredCutTests
{
    static SkseAuditedRef Ref(int n, SkseRefVerdict v) =>
        new(new HousecarlCore.SkseConfigRef($"0x{0x800 + n:X6}|Real.esp", HousecarlCore.SkseRefShape.FormToken,
                                            "Real.esp", 0x800u + (uint)n, $"0x{0x800 + n:X6}", n, null),
            v, v == SkseRefVerdict.Ok ? $"{0x800 + n:X6}:Real.esp" : $"{0x800 + n:X6}:Real.esp resolves to no record in 'Real.esp'");

    // A file whose references are OK except the last `dangling`, so file order puts every non-OK line at the end.
    static SkseConfigFileAudit File(string name, int refs, int dangling) =>
        new($"SKSE/Plugins/Cut/{name}.json", $"{name}.json", "Cut", "CutMod", 1, new[] { new SkseProvider("CutMod", "loose") },
            Enumerable.Range(1, refs).Select(n => Ref(n, n > refs - dangling ? SkseRefVerdict.Dangling : SkseRefVerdict.Ok)).ToList(),
            ReadError: null);

    static SkseConfigAuditData Data(SkseConfigFileAudit[] files) =>
        new(files, files.Length, Array.Empty<string>(), Array.Empty<string>(), false, Array.Empty<string>(), "Default");

    static string Render(int cap, params SkseConfigFileAudit[] files) => SkseConfigAuditWire.Render(Data(files), "Cut", cap);

    static int Lines(string text, string tag) => text.Split('\n').Count(l => l.StartsWith("  " + tag));

    [Fact]
    public void AFileTooBigForTheCapShowsItsNonOkReferencesFirstAndSaysHowManyItShowed()
    {
        var text = Render(4_000, File("big", 200, dangling: 3));

        var cut = Regex.Match(text, @"showing (\d+) of 200 references \(all 3 non-OK shown\)");
        Assert.True(cut.Success, text);
        Assert.Equal(3, Lines(text, "[DANGLING]"));
        Assert.True(text.IndexOf("[DANGLING]") < text.IndexOf("[OK]"), "a non-OK reference must come before the OKs");
        Assert.Equal(int.Parse(cut.Groups[1].Value), Lines(text, "[DANGLING]") + Lines(text, "[OK]"));
        Assert.True(text.Length <= 4_000, $"returned {text.Length} chars");
    }

    [Fact]
    public void WhenTheNonOkReferencesDoNotAllFitTheCutSaysHowManyDid()
    {
        var text = Render(4_000, File("big", 200, dangling: 200));

        var cut = Regex.Match(text, @"showing (\d+) of 200 references \((\d+) of 200 non-OK shown\)");
        Assert.True(cut.Success, text);
        Assert.Equal(int.Parse(cut.Groups[2].Value), Lines(text, "[DANGLING]"));
    }

    [Fact]
    public void APartlyShownFileCountsAsCutAndTheNextPageStartsOnItAgain()
    {
        var files = new[] { File("a-small", 3, dangling: 1), File("b-big", 200, dangling: 3), File("c-small", 3, dangling: 1) };
        var text = SkseConfigAuditWire.Render(Data(files), "Cut", 4_000, new RowWindow(0, 5));

        Assert.Contains("of 200 references (all 3 non-OK shown)", text);
        Assert.Contains("showing 1 of 3 files", text);
        Assert.Contains("total=3 rendered=1", text);
        Assert.Contains("truncated=2", text);
        Assert.Contains("max_chars cut 2 config(s)", text);
        Assert.Contains("re-call with limit=5 offset=1 for the next page", text);
        Assert.DoesNotContain("c-small", text);
    }

    [Fact]
    public void AnAllOkFileCutAloneSaysAllOkAndTheAccountingCountsItCut()
    {
        var text = Render(4_000, File("big", 200, dangling: 0));

        Assert.Matches(@"showing \d+ of 200 references \(all OK\)", text);
        Assert.Contains("total=1 rendered=0", text);
        Assert.Contains("truncated=1", text);
        Assert.Contains("max_chars cut 1 config(s)", text);
        Assert.Contains("raise max_chars for the one at offset=0", text);
    }

    [Fact]
    public void TheJsonTwinCutsARowInTheSameOrderAndCountsItCut()
    {
        var files = new[] { File("big", 200, dangling: 3) };
        var text = Render(4_000, files);
        using var doc = System.Text.Json.JsonDocument.Parse(SkseConfigAuditWire.RenderJson(Data(files), "Cut", 4_000));

        var refs = doc.RootElement.GetProperty("files").EnumerateArray().Single().GetProperty("references")
            .EnumerateArray().Select(r => (Raw: r.GetProperty("raw").GetString()!, Verdict: r.GetProperty("verdict").GetString()!)).ToList();
        Assert.True(refs.Count is > 3 and < 200, $"{refs.Count} references");
        Assert.All(refs.Take(3), r => Assert.Equal("dangling", r.Verdict));
        var textRaws = Regex.Matches(text, @"^  \[\w+\] '([^']+)'", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
        int both = Math.Min(textRaws.Count, refs.Count);
        Assert.Equal(textRaws.Take(both), refs.Take(both).Select(r => r.Raw));
        var acct = doc.RootElement.GetProperty("accounting");
        Assert.Equal(0, acct.GetProperty("rendered").GetInt32());
        Assert.Equal(1, acct.GetProperty("truncated").GetInt32());
    }

    [Fact]
    public void AFileThatFitsWholeKeepsItsFileOrderAndHasNoCutLine()
    {
        var text = Render(40_000, File("small", 5, dangling: 2));

        Assert.True(text.IndexOf("[OK]") < text.IndexOf("[DANGLING]"), "a whole file keeps the order the file declares");
        Assert.Equal(5, Lines(text, "[OK]") + Lines(text, "[DANGLING]"));
        Assert.DoesNotContain("references (", text);
        Assert.Contains("total=1 rendered=1", text);
    }
}
