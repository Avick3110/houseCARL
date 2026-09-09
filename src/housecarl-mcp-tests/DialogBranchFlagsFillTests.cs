using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The DialogBranch Flags (DNAM) auto-fill on <c>housecarl_create</c>. A branch created without Flags used to be
/// filled to 0, and 0 is the one value that kills a branch: Flags carries TopLevel, and a branch that is not top
/// level never reaches the player's dialogue menu, so the branch and every topic under it are dead (#693). The fill
/// is now TopLevel for BOTH categories — what the CK's own branch dialog ticks for a player branch, and what both
/// vanilla Command branches carry — and stays non-override either way. The Command test is what pins that the fill
/// does not depend on the branch's Category.
///
/// <para>Each test builds its OWN world: the create writes a patch plugin into the instance.</para>
/// </summary>
[Trait("tier", "integration")]
[Collection("records")]
public sealed class DialogBranchFlagsFillTests
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

    /// <summary>The bug: a player branch with no Flags was filled to 0 and never reached the player's menu.</summary>
    [Fact]
    public void APlayerBranchWithNoFlagsIsFilledToTopLevelAndTheFillIsReported()
    {
        using var w = new RecordsWorld();

        var o = CreateBranch(w, "HcBrPlayerFill");

        Assert.True(o.Success, "refused: " + o.Error);
        var (cat, flags) = ReadBranch(o.OutputPath, o.Created[0].FormKey);
        Assert.Equal(DialogBranch.CategoryType.Player, cat);
        Assert.Equal(DialogBranch.Flag.TopLevel, flags);
        Assert.Contains(o.Created[0].Ops, op =>
            op.Label.Contains("Flags (DNAM", StringComparison.OrdinalIgnoreCase)
            && op.Label.Contains("TopLevel", StringComparison.Ordinal));
    }

    /// <summary>Non-override: a player branch the author deliberately keeps out of the menu passes Flags=0 and keeps
    /// it — present on disk, which is not the same record as an absent DNAM.</summary>
    [Fact]
    public void AnExplicitFlagsOnAPlayerBranchIsKept()
    {
        using var w = new RecordsWorld();

        var o = CreateBranch(w, "HcBrPlayerKeep", Set("Flags", "0"));

        Assert.True(o.Success, "refused: " + o.Error);
        var (cat, flags) = ReadBranch(o.OutputPath, o.Created[0].FormKey);
        Assert.Equal(DialogBranch.CategoryType.Player, cat);
        Assert.NotNull(flags);
        Assert.Equal(default(DialogBranch.Flag), flags);
        Assert.DoesNotContain(o.Created[0].Ops, op => op.Label.Contains("Flags (DNAM", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A Command branch (a bribe/intimidate speech challenge) is picked from the same player menu, and both
    /// vanilla Command branches — DA14Bribe and DA14Intimidate — are TopLevel, so it gets the same fill. Pinned on the
    /// Category VALUE, so a fill that started keying off Category again would fail here.</summary>
    [Fact]
    public void ACommandBranchWithNoFlagsIsFilledToTopLevel()
    {
        using var w = new RecordsWorld();

        var o = CreateBranch(w, "HcBrCommandFill", Set("Category", "Command"));

        Assert.True(o.Success, "refused: " + o.Error);
        var (cat, flags) = ReadBranch(o.OutputPath, o.Created[0].FormKey);
        Assert.Equal(DialogBranch.CategoryType.Command, cat);
        Assert.Equal(DialogBranch.Flag.TopLevel, flags);
        Assert.Contains(o.Created[0].Ops, op =>
            op.Label.Contains("Flags (DNAM", StringComparison.OrdinalIgnoreCase)
            && op.Label.Contains("TopLevel", StringComparison.Ordinal));
    }
}
