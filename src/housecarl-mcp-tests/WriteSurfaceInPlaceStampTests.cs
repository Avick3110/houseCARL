using HousecarlCore;
using HousecarlMcp;
using Xunit;
using static HousecarlMcpTests.WriteSurfaceReads;

namespace HousecarlMcpTests;

/// <summary>A second in-place write into the same mod folder updates its one editedInPlace audit line, and that
/// [houseCARL] section does not make the folder houseCARL's own. The old write-surface-guard reached these lines only
/// because its arms wrote in place into the replacer more than once in one world; this states it directly.</summary>
[Trait("tier", "integration")]
public sealed class WriteSurfaceInPlaceStampTests : IDisposable
{
    readonly WriteSurfaceWorld _w = new();
    public void Dispose() => _w.Dispose();

    string Meta => Path.Combine(_w.ModsDir, "W2Repl", "meta.ini");

    void TwoInPlaceWrites()
    {
        foreach (var e in new[] { "W2StampA", "W2StampB" })
            Assert.Contains("IN PLACE", CreateTools.Create(_w.Svc,
                records: Json($$"""[{"record_type":"Keyword","editorid":"{{e}}"}]"""), in_place: _w.ReplacerName, acknowledge: true));
    }

    [Fact]
    public void ASecondInPlaceWriteUpdatesTheOneStampLine()
    {
        TwoInPlaceWrites();
        var lines = File.ReadAllLines(Meta);
        Assert.Single(lines, l => l.Trim() == HousecarlOwnerMeta.Section);
        Assert.Single(lines, l => l.Trim().StartsWith("editedInPlace=", StringComparison.Ordinal));
    }

    [Fact]
    public void AnInPlaceStampDoesNotMakeTheFolderHouseCarls()
    {
        TwoInPlaceWrites();
        Assert.False(HousecarlOwnerMeta.MarksOwned(Path.Combine(_w.ModsDir, "W2Repl")));
        var r = ApplyTools.Apply(_w.Svc, into: "W2Repl",
            ops: Json($$"""[{"formid":"{{_w.SubjectFid}}","field_path":"Name","value":"x"}]"""));
        Assert.StartsWith("error:", r);
    }
}
