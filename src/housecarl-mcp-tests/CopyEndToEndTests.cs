using System.Security.Cryptography;
using System.Text.RegularExpressions;
using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda.Plugins;
using Xunit;

namespace HousecarlMcpTests;

/// <summary><c>housecarl_copy</c>'s four walk refusals driven through <c>CopyTools.Copy</c> on a temp world, each
/// writing no mod folder and no plugin; the node and depth caps pinned at 128 and 32 from both sides; and the exact
/// reached order and FormID map read off the tool's answer. Tests only: they pin today's answers.</summary>
[Trait("tier", "integration")]
public sealed class CopyEndToEndTests
{
    static string Copy(CopyServiceWorld w, FormKey from, string[] sources, string stem)
        => CopyTools.Copy(w.Svc, FormIdToken.Of(from), sources, CopyServiceWorld.HeadParts, null, null, stem, stem);

    /// <summary>Every file and folder under <paramref name="root"/>: a file with its length, last write and content hash.</summary>
    static string[] Snapshot(string root) =>
        Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(p => File.Exists(p)
                ? $"{p} {new FileInfo(p).Length} {File.GetLastWriteTimeUtc(p):O} {Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))}"
                : p + Path.DirectorySeparatorChar)
            .ToArray();

    /// <summary>The world's whole root, the instance and the user config with it, is as it was: nothing added, removed
    /// or rewritten. The answer's closing "Nothing was written." is not a check: the render appends it to every failure.</summary>
    static void NothingWritten(string root, string[] before) => Assert.Equal(before, Snapshot(root));

    // ---- source missing -----------------------------------------------------------------------------

    // the from record no named source has: refuses as a miss pulled by from, writing nothing
    [Fact]
    public void AFromNoSourceHasRefusesAsAMissAndWritesNothing()
    {
        using var w = new CopyServiceWorld();
        var before = Snapshot(w.Root);

        var text = Copy(w, w.WideNpc, new[] { "Extra.esp" }, "MissFrom");

        Assert.Contains($"no source produced {FormIdToken.Of(w.WideNpc)} (reached via from)", text);
        NothingWritten(w.Root, before);
    }

    // a walked link no source has: refuses as a miss pulled by the seed, writing nothing
    [Fact]
    public void AWalkedLinkNoSourceHasRefusesAsAMissAndWritesNothing()
    {
        using var w = new CopyWalkWorld();
        var before = Snapshot(w.Root);

        var text = w.Clone(w.DanglingList, "MissWalk");

        Assert.Contains($"no source produced {FormIdToken.Of(w.Dangling)} (reached via FormList.Items)", text);
        NothingWritten(w.Root, before);
    }

    // ---- source fault -------------------------------------------------------------------------------

    static string ShadowPath(CopyServiceWorld w) => Path.Combine(w.ModsDir, "ShadowMod", "Shadow.esp");

    // the from record's plugin held open by another process: refuses as a fault naming the record, writing nothing
    [Fact]
    public void AFromInAnUnreadablePluginRefusesAsAFaultAndWritesNothing()
    {
        using var w = new CopyServiceWorld();
        Assert.Contains("CLONED", Copy(w, w.ShadowNpc, new[] { "Shadow.esp" }, "WarmFrom"));   // the index is built first
        var before = Snapshot(w.Root);

        string text;
        using (HeldOpen.Hold(ShadowPath(w))) text = Copy(w, w.ShadowNpc, new[] { "Shadow.esp" }, "FaultFrom");

        Assert.Contains($"carries {FormIdToken.Of(w.ShadowNpc)} but it could not be read", text);
        NothingWritten(w.Root, before);
    }

    // a walked record in a plugin held open by another process: refuses as a fault naming that record, writing nothing
    [Fact]
    public void AWalkedRecordInAnUnreadablePluginRefusesAsAFaultAndWritesNothing()
    {
        using var w = new CopyServiceWorld();
        Assert.Contains("CLONED", Copy(w, w.ShadowNpc, new[] { "Shadow.esp" }, "WarmWalk"));
        var before = Snapshot(w.Root);

        string text;
        using (HeldOpen.Hold(ShadowPath(w))) text = Copy(w, w.R5Npc, new[] { "Src.esp", "Shadow.esp" }, "FaultWalk");

        Assert.Contains("carries 000800:Shadow.esp but it could not be read", text);
        NothingWritten(w.Root, before);
    }

    // ---- the node cap: 128 -------------------------------------------------------------------------

    static int Rows(string text, string type) => Regex.Matches(text, $"^  - {type} ", RegexOptions.Multiline).Count;

    // 128 records reached copies all 128
    [Fact]
    public void AWalkOf128RecordsCopiesThemAll()
    {
        using var w = new CopyWalkWorld();

        var text = w.Clone(w.List128, "Nodes128");

        Assert.StartsWith("CLONED", text);
        Assert.Equal(128, Rows(text, "Keyword"));
    }

    // the 129th record refuses the copy at the cap, naming 128 and that record, writing nothing
    [Fact]
    public void TheRecord129RefusesAtTheNodeCapAndWritesNothing()
    {
        using var w = new CopyWalkWorld();
        var before = Snapshot(w.Root);

        var text = w.Clone(w.List129, "Nodes129");

        Assert.Contains("the walk passed 128 records", text);
        Assert.Contains($"Last pull: {FormIdToken.Of(w.Keyword129)}", text);
        NothingWritten(w.Root, before);
    }

    // ---- the depth cap: 32 -------------------------------------------------------------------------

    // a chain reaching depth 32 copies every link of it
    [Fact]
    public void AChainToDepth32CopiesThemAll()
    {
        using var w = new CopyWalkWorld();

        var text = w.Clone(w.Chain33, "Depth32");

        Assert.StartsWith("CLONED", text);
        Assert.Equal(33, Rows(text, "HeadPart"));
    }

    // the record at depth 33 refuses the copy at the cap, naming 32 and that record, writing nothing
    [Fact]
    public void TheRecordAtDepth33RefusesAtTheDepthCapAndWritesNothing()
    {
        using var w = new CopyWalkWorld();
        var before = Snapshot(w.Root);

        var text = w.Clone(w.Chain34, "Depth33");

        Assert.Contains("the walk went deeper than 32 hops", text);
        Assert.Contains($"Last pull: {FormIdToken.Of(w.Chain34Last)}", text);
        NothingWritten(w.Root, before);
    }

    // ---- the reached order -------------------------------------------------------------------------

    // the walk is breadth first, seeds and lists in order, a record entered at its first pull; new FormIDs follow that
    // order from 0x800, the clone last; the written plugin carries the same map
    [Fact]
    public void TheCopyReachesBreadthFirstAndNumbersInThatOrder()
    {
        using var w = new CopyWalkWorld();

        var text = w.Clone(w.OrderList, "OrderPatch");

        var rows = Regex.Matches(text, @"^  - \w+ '(?<eid>[^']*)'  \S+ -> (?<to>\S+)   \(from Walk\.esp, via (?<via>.+)\)$",
            RegexOptions.Multiline).Select(m => (Eid: m.Groups["eid"].Value, To: m.Groups["to"].Value, Via: m.Groups["via"].Value)).ToList();
        Assert.Equal(new[] { "OrdA", "OrdB", "OrdA1", "OrdA2", "OrdB1", "OrdTex" }, rows.Select(r => r.Eid));
        Assert.Equal(new[] { "000800", "000801", "000802", "000803", "000804", "000805" }.Select(id => id + ":OrderPatch.esp"),
            rows.Select(r => r.To));
        Assert.EndsWith("(OrdA)", rows[3].Via);   // OrdB links OrdA2 too, after OrdA did
        Assert.StartsWith($"CLONED {FormIdToken.Of(w.OrderList)} -> 000806:OrderPatch.esp", text);

        var written = CopyServiceWorld.ReadBack(
            Directory.EnumerateFiles(w.ModsDir, "OrderPatch.esp", SearchOption.AllDirectories).Single(),
            m => m.EnumerateMajorRecords().Select(r => (Eid: r.EditorID!, To: FormIdToken.Of(r.FormKey))).ToList());
        // exactly the answer's rows and the clone, under its new EditorID: no record the answer does not list
        Assert.Equal(rows.Select(r => (r.Eid, r.To)).Append(("OrderPatch", "000806:OrderPatch.esp")).Order(),
            written.Order());
    }
}
