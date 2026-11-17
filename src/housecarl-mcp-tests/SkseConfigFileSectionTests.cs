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
}
