using System.Text.Json;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// Off-order-by-path facts on <c>housecarl_records project=delta</c>: a plugin addressed by path from a
/// disabled mod, a same-named copy outside every install root, and <c>fields=</c> narrowing a delta.
///
/// <para>Driven on the shared, frozen <see cref="RecordsWorld"/> — <c>OldFile</c> is already the disabled-mod
/// pole, and the outside-the-install case copies it into <see cref="RecordsWorld.Scratch"/> so the frozen
/// fixture's own files are never touched.</para>
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

        // The WHOLE composed label in one span — the address form, the not-active state, the cause and the remedy
        // are one sentence, and separate fragments of it leave any of the four free to be reworded around them.
        const string label = "OUT-OF-LOAD-ORDER (direct path; NOT active — it is provided by mod 'OldMod', " +
                             "which is switched OFF in MO2 — switch it on, then re-sort)";
        Served(r, label);

        // The providing mod and the remedy must each be stated once, not twice in one breath. Counted within ONE
        // label — the record's own subject line — because the same label is emitted in the header and per record
        // by design; and counted in the QUOTED form, because the echoed path also carries the mod's folder name.
        var subject = Assert.Single(r.Split('\n'), l => l.TrimStart().StartsWith("subject:", StringComparison.Ordinal));
        Assert.Equal(1, CountOf(subject, "'OldMod'"));
        Assert.Equal(1, CountOf(subject, "switch it on"));
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

        // The whole composed label again, for the same reason as B4 — and it is the WHOLE label that carries this
        // fact: the cause names the absence of a providing layer where B4's names a switched-off mod.
        Served(r, "OUT-OF-LOAD-ORDER (direct path; NOT active — no MO2 layer was found providing this exact path)");
        Assert.DoesNotContain("switched OFF", r);
        // The record's own content still reads correctly off the copy — the filename never decided provenance.
        Assert.Contains("BasicStats.Damage=55", r);
    }

    // ---- fact B7 --------------------------------------------------------------------------------------
    // fields= narrows a delta to exactly the named path.

    [Fact]
    public void FactB7_FieldsNarrowsADeltaToExactlyTheNamedPath()
    {
        string Delta(params string[] fields) => RecordsTools.Records(Svc, formids: new[] { Fid(W.Weapons[0]) },
            source: Plugin(W.MasterName), versus: Plugin(W.OverrideName),
            project: new RecordsTools.RecordsProject { form = "delta", fields = fields.Length == 0 ? null : fields });

        // The whole composed count line, not the "1 difference" fragment — that fragment is a substring of
        // "11 differences" and would read a wider delta as this one.
        var named = Delta("BasicStats.Damage");
        Served(named, $"1 difference — each value line: {W.MasterName}'s value (reference = {W.OverrideName}):",
                      "BasicStats.Damage=10");
        Assert.Equal(1, CountOf(named, "BasicStats."));

        // The control that makes the narrowing mean something. This pair differs in exactly ONE field, so a
        // fields= naming the Damage path alone is identical to the un-narrowed delta and the assertions above pass
        // with fields= deleted. Naming a DIFFERENT path discriminates: it must report no difference, and it reports
        // the Damage delta the moment fields= stops narrowing.
        var elsewhere = Delta("EditorID");
        Assert.Contains("identical across the fields read", elsewhere);
        Assert.DoesNotContain("BasicStats.Damage", elsewhere);
    }
}
