using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The DialogBranch Flags (DNAM) pre-flight on <c>housecarl_create</c>. Flags has no honest default: TopLevel
/// publishes a branch to the player's dialogue menu, so a menu branch without it — and every topic under it — is
/// dead (#693), while a scripted Say() branch left at TopLevel shows as a selectable "..." (#212). Skyrim.esm carries
/// both shapes deliberately (2117 TopLevel, 203 Player branches at exactly 0), so a create with no Flags is REFUSED
/// naming both values, and nothing is written. A passed value always wins, an explicit 0 included.
///
/// <para>Each test builds its OWN world: the create writes a patch plugin into the instance.</para>
/// </summary>
[Trait("tier", "integration")]
[Collection("records")]
public sealed class DialogBranchFlagsRefusalTests
{
    static WritePatchBuilder.CreateOutcome CreateBranch(RecordsWorld w, string editorId, params BulkOp[] ops) =>
        w.Svc.CreateRecordsBatch(
            new[] { new CreateOp { RecordType = "DialogBranch", Editorid = editorId, Operations = ops } },
            editorId, null);

    static BulkOp Set(string path, string value) => new() { FieldPath = path, Verb = "Set", Value = value };

    /// <summary>The written branch's (Category, Flags) — each null when its subrecord is ABSENT, which is a different
    /// record from a present zero: the engine reads an absent DNAM as TopLevel.</summary>
    static (DialogBranch.CategoryType? category, DialogBranch.Flag? flags) ReadBranch(string patchPath, FormKey fk)
    {
        var ov = SkyrimMod.CreateFromBinaryOverlay(patchPath, SkyrimRelease.SkyrimSE);
        try
        {
            var br = ov.DialogBranches.FirstOrDefault(x => x.FormKey == fk);
            return (br?.Category, br?.Flags);
        }
        finally { (ov as IDisposable)?.Dispose(); }
    }

    /// <summary>No Flags on a player branch is refused in one sentence naming BOTH values and what each does — and
    /// nothing is written, like every create refusal.</summary>
    [Fact]
    public void APlayerBranchWithNoFlagsIsRefusedNamingBothValues()
    {
        using var w = new RecordsWorld();

        var o = CreateBranch(w, "HcBrPlayerNoFlags");

        Assert.False(o.Success);
        Assert.Contains(
            "DialogBranch 'HcBrPlayerNoFlags' needs Flags: pass TopLevel for a menu entry the player can pick, or 0 "
            + "for a scripted Say() topic that must stay hidden.",
            o.Error);
        Assert.Empty(o.Created);
    }

    /// <summary>The refusal names the record it is about, so a batch creating several says which one.</summary>
    [Fact]
    public void TheRefusalNamesTheEditorId()
    {
        using var w = new RecordsWorld();

        var o = CreateBranch(w, "HcBrNamedInRefusal");

        Assert.False(o.Success);
        Assert.Contains("HcBrNamedInRefusal", o.Error);
    }

    /// <summary>An explicit TopLevel — a menu entry the player picks — lands on disk.</summary>
    [Fact]
    public void AnExplicitTopLevelLands()
    {
        using var w = new RecordsWorld();

        var o = CreateBranch(w, "HcBrTopLevel", Set("Flags", "TopLevel"));

        Assert.True(o.Success, "refused: " + o.Error);
        var (cat, flags) = ReadBranch(o.OutputPath, o.Created[0].FormKey);
        Assert.Equal(DialogBranch.CategoryType.Player, cat);
        Assert.Equal(DialogBranch.Flag.TopLevel, flags);
    }

    /// <summary>An explicit 0 — the #212 case, a scripted Say() topic that must stay hidden — lands as a PRESENT
    /// zero, which is not the same record as an absent DNAM.</summary>
    [Fact]
    public void AnExplicitZeroLands()
    {
        using var w = new RecordsWorld();

        var o = CreateBranch(w, "HcBrHidden", Set("Flags", "0"));

        Assert.True(o.Success, "refused: " + o.Error);
        var (cat, flags) = ReadBranch(o.OutputPath, o.Created[0].FormKey);
        Assert.Equal(DialogBranch.CategoryType.Player, cat);
        Assert.NotNull(flags);
        Assert.Equal(default(DialogBranch.Flag), flags);
        Assert.DoesNotContain(o.Created[0].Ops, op => op.Label.Contains("Flags (DNAM", StringComparison.OrdinalIgnoreCase));
    }
}
