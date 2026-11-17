using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The whole config audit's file-list sections, read errors and configs only inside a BSA: their headings,
/// rows, cut notice and window, on hand-built audits. These pin behaviour as built.</summary>
[Trait("tier", "unit")]
public sealed class SkseConfigFileSectionTests
{
    static SkseConfigFileAudit File(string rel, string? readError = null, bool archiveOnly = false)
        => new(rel, Path.GetFileName(rel), "Foo", archiveOnly ? "Arch.bsa" : "ModA", 1, Array.Empty<SkseProvider>(),
            readError is null
                ? new[] { new SkseAuditedRef(new SkseConfigRef("0x1|Skyrim.esm", SkseRefShape.FormToken, "Skyrim.esm", 0x1, "0x1", 1, null), SkseRefVerdict.Ok, null) }
                : Array.Empty<SkseAuditedRef>(),
            readError, ArchiveOnly: archiveOnly);

    static SkseConfigAuditData Data(params SkseConfigFileAudit[] files)
        => new(files, files.Length, Array.Empty<string>(), Array.Empty<string>(), false, Array.Empty<string>(), "TestProfile");

    [Fact]
    public void AReadErrorIsListedUnderItsOwnHeadingInTheWholeAudit()
    {
        var txt = SkseConfigAuditWire.Render(Data(File(@"SKSE\Plugins\Foo\ok.ini"), File(@"SKSE\Plugins\Foo\bad.ini", "boom")), null, 40_000);
        Assert.Contains("\nread errors — configs that could not be read/decoded (NOT counted as clean) (1):\n" +
                        "  - SKSE\\Plugins\\Foo\\bad.ini: boom  [← ModA]\n", txt);
    }

    static SkseConfigAuditData Packed(int n)
        => Data(Enumerable.Range(0, n).Select(i => File($@"SKSE\Plugins\Packed\cfg{i:00}.json", archiveOnly: true)).ToArray());

    [Fact]
    public void ACutBsaOnlyListSaysHowManyOfItsFilesItShowed()
    {
        var d = Packed(20);
        int cuts = 0;
        for (int cap = 800; cap <= 6_000; cap += 25)
        {
            var txt = SkseConfigAuditWire.Render(d, null, cap);
            var m = System.Text.RegularExpressions.Regex.Match(txt, @"\n  \.\.\. \[(\d+) of 20; raise max_chars\]\n");
            if (!m.Success) continue;
            cuts++;
            int rows = txt.Split('\n').Count(l => l.StartsWith(@"  - SKSE\Plugins\Packed\", StringComparison.Ordinal));
            Assert.Equal(rows, int.Parse(m.Groups[1].Value));
        }
        Assert.True(cuts > 0, "no cap in the sweep cut the list");
    }

    [Fact]
    public void ABsaOnlyHeadingThatDoesNotFitIsCountedAsAnOmittedSection()
    {
        var d = Packed(20);
        int omitted = 0;
        for (int cap = 200; cap <= 2_000; cap += 25)
        {
            var txt = SkseConfigAuditWire.Render(d, null, cap);
            if (txt.Contains("configs only inside a BSA (", StringComparison.Ordinal)) continue;
            omitted++;
            Assert.Contains("  ... [1 section(s) omitted at max_chars=" + cap + ";", txt);
        }
        Assert.True(omitted > 0, "no cap in the sweep left the heading out");
    }

    [Fact]
    public void AWindowedAuditListsOnlyTheBsaOnlyConfigsInsideItsWindow()
    {
        var d = Data(File(@"SKSE\Plugins\Foo\a.json", archiveOnly: true), File(@"SKSE\Plugins\Foo\b.ini"),
                     File(@"SKSE\Plugins\Foo\c.json", archiveOnly: true));
        var txt = SkseConfigAuditWire.Render(d, null, 40_000, new RowWindow(1, 2));
        Assert.Contains("configs only inside a BSA (1):", txt);
        Assert.Contains(@"  - SKSE\Plugins\Foo\c.json  [← Arch.bsa]", txt);
        Assert.DoesNotContain(@"  - SKSE\Plugins\Foo\a.json", txt);
    }
}
