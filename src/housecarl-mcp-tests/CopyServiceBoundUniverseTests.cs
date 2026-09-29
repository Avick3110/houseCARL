using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>Which plugins the closure copy treats as the source it copies away from — the BOUND universe — driven
/// through <c>LoadOrderService.CopyClosure</c>: every named arm binds whatever its kind, a record active nowhere is
/// internalized, a base-game master never binds, and the render's standalone line follows what was bound.</summary>
[Trait("tier", "integration")]
public sealed class CopyServiceBoundUniverseTests : IDisposable
{
    readonly CopyServiceWorld _w = new();
    public void Dispose() => _w.Dispose();

    ClosureCopyOutcome TwoArm() => _w.Copy(_w.WideNpc, new[] { "Src.esp", "Extra.esp" }, new[] { "HeadParts", "HeadTexture" },
        CopyServiceWorld.NoExclusions, null, "TwoArmClone", "TwoArmPatch", null);

    // probe arm 4: a TWO-ARM chain with distinct ModKeys copies; the SECOND arm's record is internalized and
    // attributed to it — an ACTIVE plugin also provides that key, so only being BOUND puts it in the closure
    [Fact]
    public void ASecondFileArmsRecordIsInternalizedAndAttributedToThatArm()
    {
        var o = TwoArm();

        CopyServiceWorld.Succeeded(o);
        Assert.Contains(o.Copied, c => c.EditorId == "ExtraBrow" && c.ArmSpelling == "Extra.esp");
    }

    // probe arm 4: the SECOND arm's plugin is NOT a master — the bound universe covers every file arm, not just
    // from's — so the standalone claim is computed over BOTH source plugins
    [Fact]
    public void TheSecondFileArmsPluginIsNotAMasterAndTheStandaloneClaimCoversIt()
    {
        var o = TwoArm();

        CopyServiceWorld.Succeeded(o);
        Assert.DoesNotContain(o.Masters, m => m.Equals("Extra.esp", StringComparison.OrdinalIgnoreCase));
        Assert.False(o.SourceAmongMasters);
    }

    // probe arm 5: a record whose defining plugin is active NOWHERE is internalized (the missing-master disjunct),
    // so the artifact does not declare a master that exists nowhere
    [Fact]
    public void ARecordWhoseDefiningPluginIsActiveNowhereIsInternalizedNotMastered()
    {
        var o = TwoArm();

        CopyServiceWorld.Succeeded(o);
        Assert.Contains(o.Copied, c => c.EditorId == "GhostTex");
        Assert.DoesNotContain(o.Masters, m => m.Equals("Ghost.esp", StringComparison.OrdinalIgnoreCase));
    }

    // probe arm 7c: RULING 5 — an ENABLED plugin named in from_source= copies; a record DEFINED by that plugin is
    // INTERNALIZED, not kept as a mastered link; that plugin is NOT among the masters, and the standalone claim is
    // computed over the same corrected set
    [Fact]
    public void AnEnabledPluginNamedAsASourceIsBoundAndItsRecordInternalized()
    {
        var o = _w.Copy(_w.R5Npc, new[] { "Src.esp", "Shadow.esp" }, CopyServiceWorld.HeadParts,
            CopyServiceWorld.NoExclusions, null, "ActiveNamedClone", "ActiveNamedPatch", null);

        CopyServiceWorld.Succeeded(o);
        Assert.Contains(o.Copied, c => c.EditorId == "ShadowBrow");
        Assert.DoesNotContain(o.Masters, m => m.Equals("Shadow.esp", StringComparison.OrdinalIgnoreCase));
        Assert.False(o.SourceAmongMasters);
    }

    // probe arm 7g: INVENTORY F24 — a donor defined in a BASE-GAME master copies and NOTHING is bound; the render
    // says what the copy IS — an appearance transplant — and NOT the standalone claim
    [Fact]
    public void ABaseGameDonorBindsNothingAndIsRenderedAsAnAppearanceTransplant()
    {
        var o = _w.Copy(_w.DawnguardNpc, new[] { "Dawnguard.esm" }, CopyServiceWorld.HeadParts,
            CopyServiceWorld.NoExclusions, null, "BaseGameClone", "BaseGamePatch", null);

        CopyServiceWorld.Succeeded(o);
        Assert.True(o.NothingBound);
        var text = CopyTools.Render(o);
        Assert.Contains("appearance transplant", text);
        Assert.DoesNotContain("the source is NOT a master", text);
    }

    // probe arm 7h: the MIXED case — a BASE-GAME FormID read through a named mod override copies; the bound set is
    // NOT empty, the override's own head part is INTERNALIZED, the response does NOT call it a transplant, and it
    // takes the standalone/alarm path instead
    [Fact]
    public void ABaseGameFormIdReadThroughANamedModOverrideIsNotRenderedAsATransplant()
    {
        var o = _w.Copy(_w.DawnguardNpc, new[] { "Shadow.esp", "Dawnguard.esm" }, CopyServiceWorld.HeadParts,
            CopyServiceWorld.NoExclusions, null, "MixedClone", "MixedPatch", null);

        CopyServiceWorld.Succeeded(o);
        Assert.False(o.NothingBound);
        Assert.Contains(o.Copied, c => c.EditorId == "ShadowBrow");
        var text = CopyTools.Render(o);
        Assert.DoesNotContain("appearance transplant", text);
        Assert.True(text.Contains("the source is NOT a master", StringComparison.Ordinal)
                    || text.Contains("IS among the masters", StringComparison.Ordinal), text);
    }
}
