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

    static string Render(int cap, params SkseConfigFileAudit[] files) =>
        SkseConfigAuditWire.Render(new SkseConfigAuditData(files, files.Length, Array.Empty<string>(), Array.Empty<string>(),
            false, Array.Empty<string>(), "Default"), "Cut", cap);

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
    public void ACutFileCountsAsRenderedAndTheFilesAfterItAreCountedCut()
    {
        var text = Render(4_000, File("a-small", 3, dangling: 1), File("b-big", 200, dangling: 3), File("c-small", 3, dangling: 1));

        Assert.Contains("showing 2 of 3 files", text);
        Assert.Contains("total=3 rendered=2", text);
        Assert.Contains("truncated=1", text);
        Assert.DoesNotContain("c-small", text);
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
