using System.Text.Json;
using HousecarlCore;
using HousecarlMcp;
using Xunit;
using static HousecarlMcpTests.CheckErrorsFixtures;

namespace HousecarlMcpTests;

/// <summary>
/// <c>EpochGuardProbe</c>'s epoch-stamp facts that survive #486 PR 2, re-asserted against the merged
/// <c>Wire.RenderCheck</c> / <c>JsonWire.RenderCheck</c> and <c>housecarl_records</c> — the surfaces that emit
/// them today (the probe drove the deleted 1.x single-family renderers). Numbered E1-E8 per
/// <c>dev/session-handoffs/render-halves-scratch/PHASE-1-record.md</c> §5's phase-3 fact list.
///
/// <para>E1 (the three response-shape stamp arms) and E3 (a post-capture refusal's own DTO stamp) are NOT
/// re-tested here: E1 was repaired in place in phase 1 (an arity fix on a surviving renderer, not a fact this PR
/// owes) and E3 is already covered — <c>RecordsArtifactTests.StaleReEntry_TheRefusalIsStampedWithTheBuildItConsulted</c>
/// / <c>.StaleReEntryJson_IsAnErrorAndEpochDocument</c> and <c>RecordsRefusalGrammarTests.TheOffOrderRefusalCarriesTheBuildItConsultedNotEpochNull</c>.
/// E2's TEXT half is covered by <c>RecordsListLaneTests.IdentityForm_LabelsTheListStatesTheFormAndStampsTheEpoch</c>;
/// only its JSON half is fresh here.</para>
///
/// <para>Driven on <see cref="EpochWorld"/> — a dedicated fixture (never the shared errors/scripts worlds) because
/// E7/E8 need an OFF-ORDER plugin and E5 needs an ENABLED-but-unparseable one, neither of which
/// <see cref="ScriptsWorld"/> or the errors family's world carries. Every hand-shaped value below was captured
/// from the live render, not composed from memory of the source.</para>
/// </summary>
[Collection("epoch")]
[Trait("tier", "integration")]
public sealed class EpochCheckSweepTests
{
    readonly EpochWorld W;
    public EpochCheckSweepTests(EpochFixture f) => W = f.W;

    LoadOrderService Svc => W.Svc;

    // ---- fact E2 (json half only — the text half is covered elsewhere) ------------------------------------
    // A single successful record read's JSON render carries the capture's epoch at the document's top level.

    [Fact]
    public void FactE2_ASingleRecordReadsJsonRenderCarriesTheCapturesEpoch()
    {
        var current = Svc.Stats().epoch;
        var fid = $"{W.Weapon.ID:X6}:{W.Weapon.ModKey.FileName}";

        var json = RecordsTools.Records(Svc, formids: new[] { fid },
            project: new RecordsTools.RecordsProject { form = "fields", fields = new[] { "EditorID" } },
            format: "json");

        Assert.Equal(current, JsonDocument.Parse(json).RootElement.GetProperty("epoch").GetString());
    }

    // ---- fact E4 --------------------------------------------------------------------------------------
    // A successful sweep stamps the swept build and both renders carry it, for the errors family and the
    // scripts family.

    [Fact]
    public void FactE4_ASuccessfulSweepStampsTheSweptBuild_ForErrorsAndScripts()
    {
        var current = Svc.Stats().epoch;

        var errors = Svc.CheckErrors(null, 1000);
        Assert.Equal(current, errors.Epoch);
        var errText = Wire.RenderCheck(new CheckSweep(Sel("errors"), Errors: errors), 20000);
        var errJson = JsonWire.RenderCheck(new CheckSweep(Sel("errors"), Errors: errors), 20000);
        Assert.Contains($"epoch={current}", errText);
        Assert.Equal(current, ErrorsFamily(errJson).GetProperty("epoch").GetString());

        var scripts = Svc.ValidateScripts(null, 1000);
        Assert.Equal(current, scripts.Epoch);
        var scrText = Wire.RenderCheck(new CheckSweep(Sel("scripts"), Scripts: scripts), 20000);
        var scrJson = JsonWire.RenderCheck(new CheckSweep(Sel("scripts"), Scripts: scripts), 20000);
        Assert.Contains($"epoch={current}", scrText);
        Assert.Equal(current,
            JsonDocument.Parse(scrJson).RootElement.GetProperty("families").GetProperty("scripts")
                        .GetProperty("epoch").GetString());
    }

    // ---- facts E5 / E6 ----------------------------------------------------------------------------------
    // Every sweep REFUSAL is a real document carrying the stamp — the errors family's locate refusal, its
    // CORE-frame excluded-plugin refusal, and the scripts family's not-in-order refusal — in text and json, and
    // NONE carries an epoch_covers_all_inputs claim: coverage is a success-path assertion only.

    [Fact]
    public void FactE5_6_EverySweepRefusalIsAStampedRealDocument_CarryingNoCoverageClaim()
    {
        var current = Svc.Stats().epoch;

        var locate = Svc.CheckErrors(new[] { "Nope.esp" }, 1000);
        Assert.NotNull(locate.Error);
        Assert.Equal(current, locate.Epoch);
        var locateJson = JsonWire.RenderCheck(new CheckSweep(Sel("errors"), Errors: locate), 20000);
        Assert.True(locateJson.Length > 2, "the refusal rendered as an empty string");
        var locateDoc = JsonDocument.Parse(locateJson).RootElement;
        Assert.False(locateDoc.GetProperty("ok").GetBoolean());
        Assert.Equal(current, locateDoc.GetProperty("epoch").GetString());
        // Read over the WHOLE document, not its root: the key's one writer is inside the family object, so a root
        // TryGetProperty is false whatever the refusal path does and could never have failed. A refusal that grew
        // a coverage claim anywhere reddens this (pre-green review 1b, finding 4). FactE8 is the proven-positive
        // counterpart — it reads the key back off the success path.
        Assert.DoesNotContain("epoch_covers_all_inputs", locateJson);

        var excluded = Svc.CheckErrors(new[] { EpochWorld.BadName }, 1000);
        // The whole composed refusal, anchored to the plugin it names, rather than the bare word "excluded" — the
        // word alone survives any rewriting of the sentence around it and is emitted by other refusals too.
        Assert.Contains($"plugin '{EpochWorld.BadName}' was excluded from this session because it could not be " +
                         "parsed", excluded.Error);
        Assert.Contains("fix or remove it upstream; it cannot be checked.", excluded.Error);
        Assert.Equal(current, excluded.Epoch);
        var excludedText = Wire.RenderCheck(new CheckSweep(Sel("errors"), Errors: excluded), 20000);
        Assert.Contains($"epoch={current}", excludedText);

        var scriptsRefusal = Svc.ValidateScripts(new[] { "Nope.esp" }, 1000);
        Assert.NotNull(scriptsRefusal.Error);
        Assert.Equal(current, scriptsRefusal.Epoch);
        var scriptsText = Wire.RenderCheck(new CheckSweep(Sel("scripts"), Scripts: scriptsRefusal), 20000);
        var scriptsJson = JsonWire.RenderCheck(new CheckSweep(Sel("scripts"), Scripts: scriptsRefusal), 20000);
        Assert.Contains($"epoch={current}", scriptsText);
        Assert.Equal(current, JsonDocument.Parse(scriptsJson).RootElement.GetProperty("epoch").GetString());
    }

    // ---- fact E7 ---------------------------------------------------------------------------------------
    // An off-order sweep qualifies its text stamp with "(indexed plugins only …"; an all-indexed sweep carries
    // no qualifier.

    [Fact]
    public void FactE7_AnOffOrderSweepQualifiesItsTextStamp_AnAllIndexedSweepDoesNot()
    {
        var offOrder = Svc.CheckErrors(new[] { EpochWorld.OldName }, 1000);
        Assert.True(offOrder.Error is null && offOrder.OffOrderScanned is { Count: > 0 });
        var offOrderText = Wire.RenderCheck(new CheckSweep(Sel("errors"), Errors: offOrder), 20000);
        // The WHOLE qualifier. A 22-character prefix of a 72-character sentence leaves the half that says what the
        // qualifier MEANS unpinned — the tail could be replaced wholesale and this stayed green (pre-green review
        // 1a, finding 2).
        Assert.Contains("(indexed plugins only — off-order file content is outside the fingerprint)", offOrderText);

        var allIndexed = Svc.CheckErrors(null, 1000);
        var allIndexedText = Wire.RenderCheck(new CheckSweep(Sel("errors"), Errors: allIndexed), 20000);
        // The negative stays on the SHORT needle deliberately — a negative is strongest with the least text, and a
        // long one would pass over a qualifier that came back reworded.
        Assert.DoesNotContain("(indexed plugins only", allIndexedText);
    }

    // ---- fact E8 ---------------------------------------------------------------------------------------
    // The sweep json carries epoch_covers_all_inputs as data: false when off-order files were swept, true
    // otherwise.

    [Fact]
    public void FactE8_TheSweepJsonCarriesEpochCoverageAsData()
    {
        var offOrder = Svc.CheckErrors(new[] { EpochWorld.OldName }, 1000);
        var offOrderJson = JsonWire.RenderCheck(new CheckSweep(Sel("errors"), Errors: offOrder), 20000);
        Assert.False(ErrorsFamily(offOrderJson).GetProperty("epoch_covers_all_inputs").GetBoolean());

        var allIndexed = Svc.CheckErrors(null, 1000);
        var allIndexedJson = JsonWire.RenderCheck(new CheckSweep(Sel("errors"), Errors: allIndexed), 20000);
        Assert.True(ErrorsFamily(allIndexedJson).GetProperty("epoch_covers_all_inputs").GetBoolean());
    }
}
