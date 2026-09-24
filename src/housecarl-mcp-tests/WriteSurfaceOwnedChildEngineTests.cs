using System.Reflection;
using HousecarlCore;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Xunit;
using static HousecarlMcpTests.WriteSurfaceReads;

namespace HousecarlMcpTests;

/// <summary>The engine half of #324 and #335, driven directly: the child-group capture/restore gate and its three
/// refusals, the reflected child-bearing property set, and how every write verb answers at an owned child record
/// (Cell.Landscape, Worldspace.TopCell). Migrated from write-surface-guard's child-group arm.</summary>
[Trait("tier", "integration")]
public sealed class WriteSurfaceOwnedChildEngineTests
{
    static CorpusRulebook Rules => CorpusRulebook.Load();

    static string? Throws(Action act)
    {
        try { act(); return null; }
        catch (Exception ex) { return ex.Message; }
    }

    static FormKey Fk(uint id) => new(new ModKey("HcW2Master", ModType.Master), id);

    static string Names(Type t) =>
        string.Join(",", WriteEngine.ChildBearingProperties(t).Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));

    /// <summary>Does the text name the tool as a whole identifier, not as part of a longer 1.x name?</summary>
    static bool NamesTool(string text, string name)
    {
        static bool Part(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '-';
        for (int i = text.IndexOf(name, StringComparison.Ordinal); i >= 0; i = text.IndexOf(name, i + 1, StringComparison.Ordinal))
        {
            if (i > 0 && Part(text[i - 1])) continue;
            int after = i + name.Length;
            if (after >= text.Length || !Part(text[after])) return true;
        }
        return false;
    }

    // ---- the capture gate --------------------------------------------------------------------------

    // probe: the gate is the capture FACT: an uncaptured carry does not engage the guard at all
    // probe: …and a captured one does, on the identical record and children
    [Fact]
    public void RestoreActsOnlyOnACapturedCarry()
    {
        var topic = new DialogTopic(Fk(0x123460), SkyrimRelease.SkyrimSE);
        topic.Responses.Add(new DialogResponses(Fk(0x123461), SkyrimRelease.SkyrimSE) { EditorID = "W324Gate" });
        Assert.Null(WriteEngine.RestoreChildGroup(topic, default, "…"));
        var captured = WriteEngine.CaptureChildGroup(topic) with { Held = Array.Empty<(PropertyInfo, object?)>(), Count = 9 };
        Assert.NotNull(WriteEngine.RestoreChildGroup(topic, captured, "…"));
    }

    // probe: Worldspace: the capture sees BOTH the top cell and the exterior cell two containers down
    // probe: …and the re-attach restores both onto a fresh copy, with the count balancing
    [Fact]
    public void WorldspaceCaptureAndRestoreCrossTwoContainers()
    {
        var wrld = new Worldspace(Fk(0x123470), SkyrimRelease.SkyrimSE);
        wrld.TopCell = new Cell(Fk(0x123471), SkyrimRelease.SkyrimSE) { EditorID = "W324Top" };
        var sub = new WorldspaceSubBlock { BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellSubBlock };
        sub.Items.Add(new Cell(Fk(0x123472), SkyrimRelease.SkyrimSE) { EditorID = "W324Ext" });
        var block = new WorldspaceBlock { BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellBlock };
        block.Items.Add(sub);
        wrld.SubCells.Add(block);

        var carry = WriteEngine.CaptureChildGroup(wrld);
        Assert.Equal(2, carry.Count);
        var fresh = new Worldspace(wrld.FormKey, SkyrimRelease.SkyrimSE);
        Assert.Null(WriteEngine.RestoreChildGroup(fresh, carry, "…"));
        Assert.Equal("W324Top", fresh.TopCell?.EditorID);
        Assert.Contains(fresh.SubCells.SelectMany(b => b.Items).SelectMany(sb => sb.Items), c => c.EditorID == "W324Ext");
    }

    // probe: the child-bearing property set is exactly what Mutagen models: Cell(4) · DialogTopic(1) · Worldspace(2)
    [Fact]
    public void ChildBearingPropertySetIsPinned()
    {
        Assert.Equal("Landscape,NavigationMeshes,Persistent,Temporary", Names(typeof(Cell)));
        Assert.Equal("Responses", Names(typeof(DialogTopic)));
        Assert.Equal("SubCells,TopCell", Names(typeof(Worldspace)));
    }

    // probe: …and NOTHING else does, across every concrete record type Mutagen models (a link is not a child)
    [Fact]
    public void NoOtherRecordTypeBearsChildren()
    {
        var owners = typeof(Weapon).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && !t.Name.EndsWith("BinaryOverlay", StringComparison.Ordinal)
                        && typeof(IMajorRecord).IsAssignableFrom(t))
            .Where(t => WriteEngine.ChildBearingProperties(t).Count > 0)
            .Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal);
        Assert.Equal("Cell,DialogTopic,Worldspace", string.Join(",", owners));
    }

    // ---- the three RestoreChildGroup refusals ------------------------------------------------------

    static DialogTopic Carrier()
    {
        var t = new DialogTopic(Fk(0x123456), SkyrimRelease.SkyrimSE);
        t.Responses.Add(new DialogResponses(Fk(0x123457), SkyrimRelease.SkyrimSE) { EditorID = "W324Arrived" });
        return t;
    }

    const string Untouched = "Nothing was serialized; UNTOUCHED.";

    // probe: refusal 1: a copy arriving with children of its own is refused as an IMPORT when nothing was held
    [Fact]
    public void ArrivingChildrenWithNothingHeldIsRefusedAsAnImport()
    {
        var emptyCaptured = WriteEngine.CaptureChildGroup(new DialogTopic(Fk(0x123459), SkyrimRelease.SkyrimSE));
        var r = WriteEngine.RestoreChildGroup(Carrier(), emptyCaptured, Untouched);
        Assert.NotNull(r);
        Assert.Contains("arrived carrying 1 child record(s)", r);
        Assert.Contains("W324Arrived", r);
        Assert.Contains("refuses rather than silently import", r);
        // probe: …and each refusal states what was left alone exactly ONCE
        Assert.Equal(1, CountOf(r!, "Nothing was serialized"));
    }

    // probe: refusal 2: …and as a two-sets clash when the destination held some too, naming both counts
    [Fact]
    public void ArrivingChildrenWithSomeHeldIsAClash()
    {
        var carrier = Carrier();
        var r = WriteEngine.RestoreChildGroup(carrier, WriteEngine.CaptureChildGroup(carrier), Untouched);
        Assert.NotNull(r);
        Assert.Contains("while the destination held 1", r);
        Assert.Contains("discard one of the two sets", r);
        Assert.Equal(1, CountOf(r!, "Nothing was serialized"));
    }

    // probe: refusal 3: a child count that does not survive the replace refuses, naming what it cannot account for
    [Fact]
    public void UnbalancedChildCountIsRefused()
    {
        var held = WriteEngine.CaptureChildGroup(Carrier());
        var empty = new DialogTopic(Fk(0x123458), SkyrimRelease.SkyrimSE);
        var r = WriteEngine.RestoreChildGroup(empty, held with { Count = 3, Names = new[] { "W324Ghost" } }, Untouched);
        Assert.NotNull(r);
        Assert.Contains("it carries 3 child record(s)", r);
        Assert.Contains("W324Ghost", r);
        Assert.Contains("cannot account for", r);
        Assert.True(r!.IndexOf("Nothing was serialized", StringComparison.Ordinal) < r.IndexOf("Please report", StringComparison.Ordinal), r);
        Assert.Equal(1, CountOf(r, "Nothing was serialized"));
    }

    // ---- writing at a singular owned child (#335) --------------------------------------------------

    // probe: an absent owned child record refuses in its OWN words, not as a composition deferral
    [Fact]
    public void AbsentOwnedChildRefusesInItsOwnWords()
    {
        var landless = new Cell(Fk(0x123480), SkyrimRelease.SkyrimSE);
        var msg = Throws(() => WriteEngine.ApplyVerb(landless,
            new WriteRequest { RecordType = "Cell", Path = new[] { "Landscape", "EditorID" }, Verb = "Set", Value = "W335" }));
        Assert.NotNull(msg);
        Assert.Contains("owned child RECORD", msg);
        Assert.Contains("its own FormKey", msg);
        Assert.DoesNotContain("COMPOSITION type", msg);
    }

    // probe: …and a PRESENT owned child is navigable: a sub-field write lands on a cell that carries one
    [Fact]
    public void PresentOwnedChildIsNavigable()
    {
        var landed = new Cell(Fk(0x123481), SkyrimRelease.SkyrimSE) { Landscape = new Landscape(Fk(0x123482), SkyrimRelease.SkyrimSE) };
        var msg = Throws(() => WriteEngine.ApplyVerb(landed,
            new WriteRequest { RecordType = "Cell", Path = new[] { "Landscape", "EditorID" }, Verb = "Set", Value = "W335" }));
        Assert.Null(msg);
        Assert.Equal("W335", landed.Landscape?.EditorID);
    }

    // probe: …while an absent COMPOSITION substruct still gets the composition deferral, unchanged
    [Fact]
    public void AbsentCompositionSubstructKeepsTheDeferral()
    {
        var land = new Landscape(Fk(0x123483), SkyrimRelease.SkyrimSE);
        var msg = Throws(() => WriteEngine.ApplyVerb(land,
            new WriteRequest { RecordType = "Landscape", Path = new[] { "VertexColors", "X" }, Verb = "Set", Value = "1" }));
        Assert.NotNull(msg);
        Assert.Contains("COMPOSITION type", msg);
        Assert.DoesNotContain("owned child RECORD", msg);
    }

    // probe: a patch override of a parent arrives WITHOUT the parent's child records — so the absent arm is what the default lane meets
    // probe: …and the remedy it DOES name works: the child record, addressed on its own axis, takes the write
    [Fact]
    public void OverrideArrivesWithoutChildrenAndTheChildTakesItsOwnWrite()
    {
        var src = new Cell(Fk(0x123484), SkyrimRelease.SkyrimSE) { EditorID = "W335Src" };
        src.Landscape = new Landscape(Fk(0x123485), SkyrimRelease.SkyrimSE) { EditorID = "W335Land" };
        src.Persistent.Add(new PlacedObject(Fk(0x123486), SkyrimRelease.SkyrimSE));
        var srcMod = new SkyrimMod(new ModKey("HcW335Src", ModType.Master), SkyrimRelease.SkyrimSE);
        var sub = new CellSubBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellSubBlock };
        sub.Cells.Add(src);
        var blk = new CellBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellBlock };
        blk.SubBlocks.Add(sub);
        srcMod.Cells.Records.Add(blk);
        var cache = srcMod.ToImmutableLinkCache();
        var patch = new SkyrimMod(new ModKey("HcW335Patch", ModType.Plugin), SkyrimRelease.SkyrimSE);

        var cellOverride = (ICell)WriteEngine.GenericGetOrAddAsOverride(patch, src, cache);
        Assert.Null(cellOverride.Landscape);
        Assert.Empty(cellOverride.Persistent);

        var landOverride = (ILandscape)WriteEngine.GenericGetOrAddAsOverride(patch, src.Landscape!, cache);
        var msg = Throws(() => WriteEngine.ApplyVerb(landOverride,
            new WriteRequest { RecordType = "Landscape", Path = new[] { "EditorID" }, Verb = "Set", Value = "W335Direct" }));
        Assert.Null(msg);
        Assert.Equal("W335Direct", landOverride.EditorID);
    }

    // ---- the disposition table: every write verb against the owned-child shape --------------------

    static StructSpec Land() => new() { Type = "Landscape", Fields = new Dictionary<string, string> { ["EditorID"] = "X" } };
    static StructSpec Model() => new() { Type = "Model", Fields = new Dictionary<string, string> { ["File"] = @"probe\b.nif" } };

    /// <summary>(what a caller does) -> (the request, and null for accepted or the words the refusal must carry).</summary>
    static readonly Dictionary<string, (WriteRequest Req, string? MustSay)> Dispositions = new()
    {
        ["Set value= (a FormID, the shape the old formlink classification invited)"] =
            (new() { RecordType = "Cell", Path = new[] { "Landscape" }, Verb = "Set", Value = "000800:Skyrim.esm" }, "owned child RECORD"),
        ["Set compose= (build the child from parts)"] =
            (new() { RecordType = "Cell", Path = new[] { "Landscape" }, Verb = "Set", Struct = Land() }, "owned child RECORD"),
        ["Add composes= (the third door: a LIST of built elements)"] =
            (new() { RecordType = "Cell", Path = new[] { "Landscape" }, Verb = "Add", Structs = new[] { Land() } }, "owned child RECORD"),
        ["Remove, keyless (clear the field — deletes the record and its subtree)"] =
            (new() { RecordType = "Cell", Path = new[] { "Landscape" }, Verb = "Remove" }, "owned child RECORD"),
        ["Remove with a key (there is no element to name on a singular child)"] =
            (new() { RecordType = "Cell", Path = new[] { "Landscape" }, Verb = "Remove", Key = "0" }, "owned child RECORD"),
        ["CopyFrom (transplant the field's value from another plugin's version)"] =
            (new() { RecordType = "Cell", Path = new[] { "Landscape" }, Verb = "CopyFrom" }, "owned child RECORD"),
        ["CopyFrom on the other owned-child field, so the answer is the shape's, not one field's"] =
            (new() { RecordType = "Worldspace", Path = new[] { "TopCell" }, Verb = "CopyFrom" }, "owned child RECORD"),
        ["Add a plain value (a collection verb on a singular leaf)"] =
            (new() { RecordType = "Cell", Path = new[] { "Landscape" }, Verb = "Add", Value = "x" }, "Add is only valid"),
        ["ReplaceAll"] =
            (new() { RecordType = "Cell", Path = new[] { "Landscape" }, Verb = "ReplaceAll", Values = new[] { "x" } }, "ReplaceAll is only valid"),
        ["SetAtIndex"] =
            (new() { RecordType = "Cell", Path = new[] { "Landscape" }, Verb = "SetAtIndex", Key = "0", Value = "x" }, "SetAtIndex is only valid"),
        ["InsertAtIndex (the leaf: a singular child is not a list, so the collection verb refuses by cardinality)"] =
            (new() { RecordType = "Cell", Path = new[] { "Landscape" }, Verb = "InsertAtIndex", Key = "0", Value = "x" }, "InsertAtIndex is only valid"),
        ["Merge"] =
            (new() { RecordType = "Cell", Path = new[] { "Landscape" }, Verb = "Merge", Entries = new Dictionary<string, string> { ["k"] = "v" } }, "Merge is only valid"),
        ["descend to a sub-field of the child (the gate cannot know if one is there — live state)"] =
            (new() { RecordType = "Cell", Path = new[] { "Landscape", "EditorID" }, Verb = "Set", Value = "W335" }, null),
        ["CONTROL an ordinary nullable substruct still clears"] =
            (new() { RecordType = "Book", Path = new[] { "Model" }, Verb = "Remove" }, null),
        ["CONTROL an ordinary substruct still composes"] =
            (new() { RecordType = "Book", Path = new[] { "Model" }, Verb = "Set", Struct = Model() }, null),
        ["CONTROL the LIST form of the family still deletes one child BY INDEX"] =
            (new() { RecordType = "Cell", Path = new[] { "Persistent" }, Verb = "Remove", Key = "0" }, null),
        ["CONTROL the LIST form still refuses CopyFrom in its own words"] =
            (new() { RecordType = "Cell", Path = new[] { "Persistent" }, Verb = "CopyFrom" }, "holds owned child records"),
        ["CONTROL the LIST form redirects an InsertAtIndex of a child record to the record axis"] =
            (new() { RecordType = "Cell", Path = new[] { "Persistent" }, Verb = "InsertAtIndex", Key = "0", Struct = new StructSpec { Type = "PlacedObject" } }, "holds owned child records"),
        ["CONTROL …and its sibling SetAtIndex gives the SAME answer there, so insert has not forked"] =
            (new() { RecordType = "Cell", Path = new[] { "Persistent" }, Verb = "SetAtIndex", Key = "0", Struct = new StructSpec { Type = "PlacedObject" } }, "holds owned child records"),
        ["composes= at the LIST form — the batch input surface, which short-circuits above the collection verbs"] =
            (new() { RecordType = "Cell", Path = new[] { "Persistent" }, Verb = "Add", Structs = new[] { new StructSpec { Type = "PlacedObject" } } }, "holds owned child records"),
        ["composes= at the LIST form, ReplaceAll — the other verb that reaches it"] =
            (new() { RecordType = "Cell", Path = new[] { "Persistent" }, Verb = "ReplaceAll", Structs = new[] { new StructSpec { Type = "PlacedObject" } } }, "holds owned child records"),
        ["composes= at the LIST form of the OTHER record-element family"] =
            (new() { RecordType = "DialogTopic", Path = new[] { "Responses" }, Verb = "Add", Structs = new[] { new StructSpec { Type = "DialogResponses" } } }, "holds owned child records"),
        ["CONTROL composes= on an ordinary modeled list still reaches the composes= surface"] =
            (new() { RecordType = "Faction", Path = new[] { "Conditions" }, Verb = "Set", Structs = new[] { new StructSpec { Type = "ConditionFloat" } } }, "composes= appends/replaces a LIST"),
        ["CONTROL composes= on an ordinary substruct keeps the generic LIST sentence"] =
            (new() { RecordType = "Book", Path = new[] { "Model" }, Verb = "Add", Structs = new[] { Model() } }, "composes= builds a LIST"),
        ["ReplaceAll composes=[…] (the other verb that reaches the composes clause)"] =
            (new() { RecordType = "Cell", Path = new[] { "Landscape" }, Verb = "ReplaceAll", Structs = new[] { Land() } }, "owned child RECORD"),
        ["ReplaceAll composes=[] (the modeled-list CLEAR — the other door to deleting the child)"] =
            (new() { RecordType = "Cell", Path = new[] { "Landscape" }, Verb = "ReplaceAll", Structs = Array.Empty<StructSpec>() }, "owned child RECORD"),
        ["CopyFrom THROUGH the child (a leaf under it) — transplant refuses at any depth"] =
            (new() { RecordType = "Cell", Path = new[] { "Landscape", "EditorID" }, Verb = "CopyFrom" }, "runs through 'Landscape'"),
        ["CopyFrom through the other owned-child field, so the answer is the shape's"] =
            (new() { RecordType = "Worldspace", Path = new[] { "TopCell", "EditorID" }, Verb = "CopyFrom" }, "runs through 'TopCell'"),
        ["mid-path Set under the child stays accepted (in-place descent, not transplant)"] =
            (new() { RecordType = "Cell", Path = new[] { "Landscape", "EditorID" }, Verb = "Set", Value = "x" }, null),
        ["mid-path Remove under the child stays accepted (clears a field OF the carried child, not the child)"] =
            (new() { RecordType = "Cell", Path = new[] { "Landscape", "VertexHeightMap" }, Verb = "Remove" }, null),
        ["CONTROL CopyFrom through an ORDINARY substruct path is still accepted"] =
            (new() { RecordType = "Book", Path = new[] { "Model", "File" }, Verb = "CopyFrom" }, null),
        ["mid-path InsertAtIndex at a LIST leaf under the child stays accepted (in-place descent, not transplant)"] =
            (new() { RecordType = "Cell", Path = new[] { "Landscape", "Textures" }, Verb = "InsertAtIndex", Key = "0", Value = "000800:Skyrim.esm" }, null),
        ["CONTROL the same mid-path leaf takes SetAtIndex too — insert's disposition does not fork from its sibling's"] =
            (new() { RecordType = "Cell", Path = new[] { "Landscape", "Textures" }, Verb = "SetAtIndex", Key = "0", Value = "000800:Skyrim.esm" }, null),
    };

    public static IEnumerable<object[]> DispositionRows() => Dispositions.Keys.Select(k => new object[] { k });

    // probe: disposition · {what} — one row per verb against the owned-child shape, with its controls
    [Theory]
    [MemberData(nameof(DispositionRows))]
    public void Disposition(string what)
    {
        var (req, mustSay) = Dispositions[what];
        var got = Rules.Validate(req);
        if (mustSay is null) Assert.Null(got);
        else { Assert.NotNull(got); Assert.Contains(mustSay, got); }
    }

    static string? SetValue() => Rules.Validate(new WriteRequest { RecordType = "Cell", Path = new[] { "Landscape" }, Verb = "Set", Value = "000800:Skyrim.esm" });
    static string? Remove() => Rules.Validate(new WriteRequest { RecordType = "Cell", Path = new[] { "Landscape" }, Verb = "Remove" });
    static string? CopyFrom() => Rules.Validate(new WriteRequest { RecordType = "Cell", Path = new[] { "Landscape" }, Verb = "CopyFrom" });

    // probe: …and the three Set-shaped doors give ONE sentence, not three that point at each other
    [Fact]
    public void TheThreeSetShapedDoorsGiveOneSentence()
    {
        var doors = new[]
        {
            SetValue(),
            Rules.Validate(new WriteRequest { RecordType = "Cell", Path = new[] { "Landscape" }, Verb = "Set", Struct = Land() }),
            Rules.Validate(new WriteRequest { RecordType = "Cell", Path = new[] { "Landscape" }, Verb = "Add", Structs = new[] { Land() } }),
        };
        Assert.All(doors, d => Assert.NotNull(d));
        Assert.Single(doors.Distinct(StringComparer.Ordinal));
    }

    // probe: the three field-level refusals name a working record-axis call, not an open gap
    // probe: …and each names where the child's FormID comes from, with the depth that actually shows it
    [Fact]
    public void FieldLevelRefusalsNameARecordAxisCallAndTheDepth()
    {
        foreach (var s in new[] { Remove(), CopyFrom(), SetValue() })
        {
            Assert.NotNull(s);
            Assert.DoesNotContain("#350", s);
            Assert.True(NamesTool(s!, "housecarl_create") || NamesTool(s!, "housecarl_remove"), s);
            Assert.Contains("depth=2", s);
        }
    }

    // probe: …and CopyFrom's refusal names housecarl_forward (carry an existing one) AND housecarl_create (author a new one)
    [Fact]
    public void CopyFromRefusalNamesForwardAndCreate()
    {
        var s = CopyFrom()!;
        Assert.True(NamesTool(s, "housecarl_forward"), s);
        Assert.True(NamesTool(s, "housecarl_create"), s);
    }
}
