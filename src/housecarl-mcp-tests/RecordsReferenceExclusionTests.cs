using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The negated <c>references=</c> entry (a leading '!') and the quantified <c>where=</c> step, driven
/// end to end through the tool over a real scan.</summary>
[Collection("records")]
[Trait("tier", "integration")]
public sealed class RecordsReferenceExclusionTests : RecordsTestBase
{
    public RecordsReferenceExclusionTests(RecordsFixture f) : base(f) { }

    [Fact]
    public void ANegatedReferenceKeepsOnlyTheRecordsThatDoNotLinkThatTarget()
    {
        var r = RecordsTools.Records(Svc, types: new[] { "SPEL" }, references: new[] { "!" + Fid(W.MgefA) });
        Served(r, "HcRecSpellB");
        Assert.DoesNotContain("HcRecSpellA", r);
        Assert.DoesNotContain("HcRecSpellC", r);
    }

    [Fact]
    public void APlainAndANegatedTargetInOneCallComposeByAnd()
    {
        var r = RecordsTools.Records(Svc, types: new[] { "SPEL" },
                                     references: new[] { Fid(W.MgefA), "!" + Fid(W.MgefB) });
        Served(r, "HcRecSpellA", "HcRecSpellC");
        Assert.DoesNotContain("HcRecSpellB", r);
    }

    [Fact]
    public void ANegatedReferenceKeepsADeletedRecord_WhichReferencesNothingAtAll()
    {
        // The positive term skips a record with no live body (it can never link the target); the negated term must
        // not, because that record is the strongest match the term has.
        var r = RecordsTools.Records(Svc, types: new[] { "WEAP" }, references: new[] { "!" + Fid(W.MgefA) });
        Assert.Contains(Fid(W.Weapons[2]), r);
    }

    [Fact]
    public void ANegatedReferenceTakesTheAtFileSpellingToo()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hc-refs-neg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var listFile = Path.Combine(dir, "targets.txt");
            File.WriteAllText(listFile, Fid(W.MgefA) + Environment.NewLine);
            var r = RecordsTools.Records(Svc, types: new[] { "SPEL" }, references: new[] { "!@" + listFile });
            Served(r, "HcRecSpellB");
            Assert.DoesNotContain("HcRecSpellA", r);
            Assert.DoesNotContain("bad references FormID", r);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void ANegatedReferenceStillNeedsABoundingScope() =>
        Refused(RecordsTools.Records(Svc, references: new[] { "!" + Fid(W.MgefA) }), "types=");

    [Fact]
    public void ABareBangNamesNoTargetAndIsRefused() =>
        Refused(RecordsTools.Records(Svc, types: new[] { "SPEL" }, references: new[] { "!" }), "names no target");

    [Fact]
    public void TheQuantifiedStepRidesWhereEndToEnd()
    {
        var r = RecordsTools.Records(Svc, types: new[] { "SPEL" },
                                     where: new[] { "Effects[*any].Data.Magnitude > 8" });
        Served(r, "HcRecSpellC");
        Assert.DoesNotContain("HcRecSpellA", r);
    }

    [Fact]
    public void AQuantifierOnANonListStepIsRefusedFromTheSchemaWhenTheTypeIsNamed()
    {
        // The scan's refusal rides under the header line, so this is not the bare "error:" shape.
        var r = RecordsTools.Records(Svc, types: new[] { "ARMO" },
                                     where: new[] { "BodyTemplate[*any].FirstPersonFlags has Head" });
        Assert.Contains("error:", r);
        Assert.Contains("not a list", r);
        Assert.Contains("substruct on Armor", r);
    }

    [Fact]
    public void ALinkPredicatesRightSideIsNotJudgedAgainstTheScannedType()
    {
        // 'Name' is a substruct on Spell, but this step lands on the link TARGET (a MagicEffect), so the scanned
        // type's schema has no say — refusing it here would name a field the step was never rooted at.
        var r = RecordsTools.Records(Svc, types: new[] { "SPEL" },
                                     where: new[] { "Effects[*any].BaseEffect->Name[*count] > 0" });
        Assert.DoesNotContain("on Spell", r);
    }

    [Fact]
    public void AnUncheckableRightSideStepDoesNotSilenceACheckableOne()
    {
        // 'Conditions' is not a field on Spell at all, so that step has no schema answer; the second predicate's
        // step is plainly checkable and must still be refused.
        var r = RecordsTools.Records(Svc, types: new[] { "SPEL" },
                                     where: new[] { "Effects[*any].BaseEffect->Conditions[*count] > 0",
                                                    "Description[*any] = x" });
        Assert.Contains("not a list", r);
        Assert.Contains("on Spell", r);
    }

    [Fact]
    public void AQuantifierOnAListStepPassesTheSchemaCheck()
    {
        var r = RecordsTools.Records(Svc, types: new[] { "SPEL" }, where: new[] { "Effects[*count] > 0" });
        Served(r, "HcRecSpellA");
    }

    [Fact]
    public void AFoldTokenInProjectFieldsIsRefused_ABooleanIsNotARow() =>
        Refused(RecordsTools.Records(Svc, types: new[] { "SPEL" },
                                     project: new RecordsTools.RecordsProject { form = "fields", fields = new[] { "Effects[*any].Data.Magnitude" } }),
                "not a row");

    [Fact]
    public void TheProjectionHalfOfTheQuantifiedStepIsRefusedAsUnbuilt_NeverSilentlyMisread() =>
        Refused(RecordsTools.Records(Svc, types: new[] { "SPEL" },
                                     project: new RecordsTools.RecordsProject { form = "fields", fields = new[] { "Effects[*count]" } }),
                "not built yet");

    [Fact]
    public void ANonQuantifierTokenInProjectFieldsIsNamedATypo_NotAnUnbuiltCapability()
    {
        var r = RecordsTools.Records(Svc, types: new[] { "SPEL" },
                                     project: new RecordsTools.RecordsProject { form = "fields", fields = new[] { "Effects[*sum].Data.Magnitude" } });
        Assert.Contains("is not a quantifier", r);
        Assert.DoesNotContain("not built yet", r);
    }

    [Fact]
    public void TheBareStarIsRefusedThroughTheToolNamingTheFoldTokens()
    {
        // The scan's parse refusal rides under the header line, so this is not the bare "error:" shape.
        var r = RecordsTools.Records(Svc, types: new[] { "SPEL" }, where: new[] { "Effects[*].Data.Magnitude > 8" });
        Assert.Contains("error:", r);
        Assert.Contains("[*any]", r);
    }
}
