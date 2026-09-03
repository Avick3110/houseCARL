using System.Text.Json;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The three off-order-by-path facts <c>BulkPrimitivesWave3Probe.cs</c> asserted through the deleted
/// <c>LoadOrderService.DiffRecord</c> (#486), re-asserted here against <c>housecarl_records project=delta</c> —
/// the surface a surviving tool calls. Numbered B4/B5/B7 per
/// <c>dev/session-handoffs/render-halves-scratch/PHASE-1-record.md</c> §5's phase-4 fact list. B1/B2/B3/B6/B8/B9/B10
/// are already covered elsewhere (named in the phase-4 record); this file carries only what was not.
///
/// <para>Driven on the shared, frozen <see cref="RecordsWorld"/> — <c>OldFile</c> is already the disabled-mod
/// pole B4 needs, and B5 copies it once into <see cref="RecordsWorld.Scratch"/> (a scratch path, never touching
/// the frozen fixture's own files) for the outside-the-install shape.</para>
/// </summary>
[Collection("records")]
[Trait("tier", "integration")]
public sealed class RecordsOffOrderPathTests : RecordsTestBase
{
    public RecordsOffOrderPathTests(RecordsFixture f) : base(f) { }

    static RecordsTools.RecordsProject Delta => new() { form = "delta" };
    static JsonElement PathPole(string p) => Je(JsonSerializer.Serialize(p));

    // ---- fact B4 --------------------------------------------------------------------------------------
    // A path form source that IS inside a DISABLED mod stays off-order and the label NAMES the cause — the
    // switched-off MOD folder, never the plugin's own "unticked" wording (that is a different cause on a
    // different address form).

    [Fact]
    public void FactB4_ADisabledModsPluginAddressedByPathStaysOffOrderAndNamesTheCause()
    {
        var r = RecordsTools.Records(Svc, formids: new[] { Fid(W.Weapons[1]) },
            source: PathPole(W.OldFile), versus: Plugin(W.MasterName), project: Delta);

        Served(r, "OUT-OF-LOAD-ORDER", "direct path", "NOT active");
        Assert.Contains("it is provided by mod 'OldMod', which is switched OFF in MO2", r);
        Assert.Contains("switch it on", r);
    }

    // ---- fact B5 --------------------------------------------------------------------------------------
    // A same-named copy OUTSIDE every install root stays off-order — the filename never decides provenance,
    // and the cause names the absence of a providing layer rather than a switched-off mod (there is no mod
    // to blame).

    [Fact]
    public void FactB5_ASameNamedCopyOutsideEveryInstallRootStaysOffOrder()
    {
        var outside = W.Scratch("Outside", W.OldName);
        File.Copy(W.OldFile, outside, overwrite: true);

        var r = RecordsTools.Records(Svc, formids: new[] { Fid(W.Weapons[1]) },
            source: PathPole(outside), versus: Plugin(W.MasterName), project: Delta);

        Served(r, "OUT-OF-LOAD-ORDER", "direct path", "NOT active");
        Assert.Contains("no MO2 layer was found providing this exact path", r);
        Assert.DoesNotContain("switched OFF", r);
        // The record's own content still reads correctly off the copy — the filename never decided provenance.
        Assert.Contains("BasicStats.Damage=55", r);
    }

    // ---- fact B7 --------------------------------------------------------------------------------------
    // fields= narrows a delta to exactly the named path.

    [Fact]
    public void FactB7_FieldsNarrowsADeltaToExactlyTheNamedPath()
    {
        var r = RecordsTools.Records(Svc, formids: new[] { Fid(W.Weapons[0]) }, source: Plugin(W.MasterName),
            versus: Plugin(W.OverrideName), project: new RecordsTools.RecordsProject { form = "delta", fields = new[] { "BasicStats.Damage" } });

        Served(r, "1 difference", "BasicStats.Damage=10");
        Assert.Equal(1, CountOf(r, "BasicStats."));
    }
}
