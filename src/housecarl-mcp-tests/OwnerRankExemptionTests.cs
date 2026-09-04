using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// A container item owned by a FACTION carries a COED block of two words: the owner FormID, and a second word
/// that is a <c>RequiredRank</c> int. When the owner faction lives in a MASTER — every override this sweep
/// reads — Mutagen cannot type the owner arm and falls back to <c>UntypedOwner</c>, which exposes BOTH words as
/// FormLinks; a rank of <c>-1</c> (<c>0xFFFFFFFF</c>, id <c>FFFFFF</c>) then reads as a dangling reference.
/// <c>ErrorCheck.UntypedOwnerVariableData</c> drops only that SECOND word.
///
/// <para>The exemption trades off both ways and both are covered here: exempt too little and the rank word is
/// reported as dangling again; exempt too much and the owner FORM stops being swept, so a genuinely broken
/// owner goes silent.</para>
///
/// <para>Driven at DTO level on <c>ErrorCheck.Run</c> over a fixture of this file's own — never a shared world,
/// because a faction-owned container with an absent owner master is a shape no other test wants. Asserted
/// structurally, keyed by record: the FormKeys in <c>Dangling</c> and the names in <c>MissingMasters</c>, never
/// a rendered sentence.</para>
/// </summary>
[Trait("tier", "integration")]
public sealed class OwnerRankExemptionTests : IDisposable
{
    const string MasterName = "HcOwMaster.esm";
    const string GhostName = "HcOwGhost.esm";
    const string OwnerName = "HcOwOwner.esp";

    readonly string _root;
    readonly ErrorCheckResult _result;

    /// <summary>The faction in the PRESENT master: its owned chest must produce no finding at all.</summary>
    readonly FormKey _presentFaction;

    /// <summary>The faction in the ABSENT master: its owned chest's OWNER FORM must still dangle.</summary>
    readonly FormKey _ghostFaction;

    public OwnerRankExemptionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "hc-owner-rank-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        var master = new SkyrimMod(ModKey.FromNameAndExtension(MasterName), SkyrimRelease.SkyrimSE);
        var presentFaction = master.Factions.AddNew(); presentFaction.EditorID = "HcOwPresentFaction";
        _presentFaction = presentFaction.FormKey;
        var masterPath = Path.Combine(_root, MasterName);
        master.BeginWrite.ToPath(masterPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        // Written to disk so the owner plugin can declare it a master and reference into it — then NEVER handed
        // to the resolver, which is what makes it a missing master and its faction an unresolvable owner.
        var ghost = new SkyrimMod(ModKey.FromNameAndExtension(GhostName), SkyrimRelease.SkyrimSE);
        var ghostFaction = ghost.Factions.AddNew(); ghostFaction.EditorID = "HcOwGhostFaction";
        _ghostFaction = ghostFaction.FormKey;
        var ghostPath = Path.Combine(_root, GhostName);
        ghost.BeginWrite.ToPath(ghostPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        var owner = new SkyrimMod(ModKey.FromNameAndExtension(OwnerName), SkyrimRelease.SkyrimSE);
        Owned(owner, "HcOwGoodChest", _presentFaction);
        Owned(owner, "HcOwGhostChest", _ghostFaction);
        var ownerPath = Path.Combine(_root, OwnerName);
        using (var masterOv = SkyrimMod.CreateFromBinaryOverlay(masterPath, SkyrimRelease.SkyrimSE))
        using (var ghostOv = SkyrimMod.CreateFromBinaryOverlay(ghostPath, SkyrimRelease.SkyrimSE))
            owner.BeginWrite.ToPath(ownerPath).WithLoadOrder(new ISkyrimModGetter[] { masterOv, ghostOv }).Write();

        // The ghost is on disk but OUT of the order — the whole point.
        using var resolver = LoadOrderResolver.Build(new[] { masterPath, ownerPath });
        _result = ErrorCheck.Run(resolver, null, 1000);
        Assert.True(_result.Success, _result.Error);
    }

    /// <summary>One container holding one item owned by <paramref name="faction"/> at RequiredRank -1 — the
    /// 0xFFFFFFFF word the untyped-owner fallback exposes as a FormLink.</summary>
    static void Owned(SkyrimMod mod, string edid, FormKey faction)
    {
        var chest = mod.Containers.AddNew();
        chest.EditorID = edid;
        chest.Items = new()
        {
            new ContainerEntry
            {
                Item = new ContainerItem { Count = 1 },
                Data = new ExtraData
                {
                    ItemCondition = 1f,
                    Owner = new FactionOwner { Faction = new FormLink<IFactionGetter>(faction), RequiredRank = -1 },
                },
            },
        };
    }

    PluginErrors OwnerReport =>
        Assert.Single(_result.Reports, p => string.Equals(p.Plugin, OwnerName, StringComparison.OrdinalIgnoreCase));

    // ---- OWNER-RANK -------------------------------------------------------------------------------------
    // The rank WORD is never a reference. Under-exempting (dropping the UntypedOwnerVariableData call) reddens
    // this: the 0xFFFFFFFF rank of BOTH chests comes back as a dangling target.

    [Fact]
    public void Fact207_ARequiredRankOfMinusOneIsNotADanglingReference()
    {
        Assert.DoesNotContain(OwnerReport.Dangling, d => d.Target.ID == 0xFFFFFF);
    }

    // ---- OWNER-DATA-CHECKED -----------------------------------------------------------------------------
    // ...and the owner FORM is still swept. Over-exempting (dropping the owner word too, or exempting the whole
    // ExtraData) reddens this: the ghost faction stops being reported and a genuinely broken owner goes silent.

    [Fact]
    public void Fact207_TheOwnerFormItselfIsStillSwept_AndItsAbsentMasterIsReported()
    {
        Assert.Contains(OwnerReport.Dangling, d => d.Target == _ghostFaction);
        Assert.Contains(GhostName, OwnerReport.MissingMasters, StringComparer.OrdinalIgnoreCase);

        // The other chest's owner resolves, so it contributes nothing — the exemption is not what is hiding it.
        Assert.DoesNotContain(OwnerReport.Dangling, d => d.Target == _presentFaction);
    }

    // ---- OWNER-RANK-TOTAL -------------------------------------------------------------------------------
    // The sweep-wide number both directions move. Without the fix this order totals 3 (two rank artifacts plus
    // the ghost owner); with it, exactly the ghost owner.

    [Fact]
    public void Fact207_TheOrderTotalsExactlyTheOneBrokenOwner()
    {
        Assert.Equal(1, _result.TotalDangling);
        var only = Assert.Single(OwnerReport.Dangling);
        Assert.Equal(_ghostFaction, only.Target);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* temp cleanup best-effort */ }
    }
}
