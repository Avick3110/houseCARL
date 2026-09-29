using System.Text.Json;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>#314: one active plugin that Mutagen cannot OPEN must not break every write in the order, and the
/// unopenable-reference threshold holds both ways on both lanes (a one-master header writes, a two-master header
/// refuses naming the cause), with the dry run agreeing. Each test's
/// comment is the assertion of the ci-all probe it replaced.</summary>
[Trait("tier", "integration")]
public sealed class ExcludedMasterWriteTests :
    IClassFixture<ExcludedMasterMainOrder>, IClassFixture<ExcludedMasterRealOrder>, IClassFixture<ExcludedMasterBaselineOrder>,
    IClassFixture<ExcludedMasterCompactOutcome>, IClassFixture<ExcludedMasterRepointOutcome>, IClassFixture<ExcludedMasterInPlaceOrder>
{
    const string Unopenable = "cannot be opened by houseCARL";

    readonly ExcludedMasterMainOrder _main;
    readonly ExcludedMasterRealOrder _real;
    readonly ExcludedMasterBaselineOrder _base;
    readonly ExcludedMasterCompactOutcome _compact;
    readonly ExcludedMasterRepointOutcome _repoint;
    readonly ExcludedMasterInPlaceOrder _ip;

    public ExcludedMasterWriteTests(ExcludedMasterMainOrder main, ExcludedMasterRealOrder real, ExcludedMasterBaselineOrder baseline,
        ExcludedMasterCompactOutcome compact, ExcludedMasterRepointOutcome repoint, ExcludedMasterInPlaceOrder ip)
    { _main = main; _real = real; _base = baseline; _compact = compact; _repoint = repoint; _ip = ip; }

    static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement.Clone();
    static JsonElement NameOp(string fid, string value) => Json($$"""[{"formid":"{{fid}}","field_path":"Name","value":"{{value}}"}]""");
    static JsonElement DamageOp(string fid, string value) => Json($$"""[{"formid":"{{fid}}","field_path":"BasicStats.Damage","value":"{{value}}"}]""");

    /// <summary>The render's "masters:" line alone, so a plugin name in a per-record row cannot satisfy the check.</summary>
    static string MastersLineOf(string render)
        => render.Split('\n').FirstOrDefault(l => l.StartsWith("masters:", StringComparison.Ordinal)) ?? "";

    static int Count(string text, string token) => text.Split(token).Length - 1;

    // ---- the patch lane, baseline-less order ----

    // probe: "forward into a NEW patch succeeds — the broken plugin is not this write's business"
    [Fact]
    public void AForwardIntoANewPatchThatDoesNotReferenceTheBrokenPluginSucceeds()
    {
        var r = ForwardTools.Forward(_main.Svc, formids: new[] { _main.SubjectFid }, source: ExcludedMasterMainOrder.MasterName, patch: "X314Fwd");
        Assert.False(r.StartsWith("error:"), r);
    }

    // probe: "apply into a NEW patch succeeds"
    [Fact]
    public void AnApplyIntoANewPatchSucceedsBesideAnUnopenablePlugin()
    {
        var r = ApplyTools.Apply(_main.Svc, ops: NameOp(_main.SubjectFid, "X314"), patch: "X314Apply");
        Assert.False(r.StartsWith("error:"), r);
    }

    // probe: "create into a NEW patch succeeds"
    [Fact]
    public void ACreateIntoANewPatchSucceedsBesideAnUnopenablePlugin()
    {
        var r = CreateTools.Create(_main.Svc, patch: "X314Create", records: Json("""[{"record_type":"Keyword","editorid":"X314Kw"}]"""));
        Assert.False(r.StartsWith("error:"), r);
    }

    // probe: "naming the EXCLUDED plugin as a source is still refused (the skip must not become an escape hatch)"
    [Fact]
    public void NamingTheUnopenablePluginAsTheSourceIsStillRefused()
    {
        var r = ForwardTools.Forward(_main.Svc, formids: new[] { _main.SubjectFid }, source: ExcludedMasterMainOrder.BrokenName, patch: "X314Bad");
        Assert.StartsWith("error:", r);
        Assert.Contains("was excluded", r);
    }

    // probe: "a baseline-less order: a patch whose record ORIGINATES in the unopenable plugin still WRITES, mastering on it"
    [Fact]
    public void AOneMasterPatchHeaderOnTheUnopenablePluginWritesAndMastersOnIt()
    {
        var r = ForwardTools.Forward(_main.Svc, formids: new[] { _main.BrokenOwnFid }, source: ExcludedMasterMainOrder.CleanName, patch: "X314Need");
        Assert.False(r.StartsWith("error:"), r);
        Assert.Contains(ExcludedMasterMainOrder.BrokenName, MastersLineOf(r), StringComparison.OrdinalIgnoreCase);
    }

    // probe: "a MULTI-master header REFUSES (the sorted-header residual the skip leaves) — pinned, not assumed"
    // probe: "…and that refusal NAMES the unopenable plugin as the cause, with the remedy"
    [Fact]
    public void ATwoMasterPatchHeaderRefusesNamingTheUnopenablePluginAndTheRemedy()
    {
        var r = ForwardTools.Forward(_main.Svc, formids: new[] { _main.SubjectFid, _main.BrokenOwnFid },
            source: ExcludedMasterMainOrder.CleanName, patch: "X314Both");
        Assert.StartsWith("error:", r);
        Assert.Contains(ExcludedMasterMainOrder.BrokenName, r, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Unopenable, r);
        Assert.Contains("writes that do NOT reference their records are unaffected", r);
    }

    // probe: "dry_run predicts the SAME refusal for the same call, naming the same cause (#225 parity)"
    [Fact]
    public void APatchLaneDryRunPredictsTheTwoMasterRefusal()
    {
        var r = ForwardTools.Forward(_main.Svc, formids: new[] { _main.SubjectFid, _main.BrokenOwnFid },
            source: ExcludedMasterMainOrder.CleanName, patch: "X314DryBoth", dry_run: true);
        Assert.StartsWith("error:", r);
        Assert.Contains(ExcludedMasterMainOrder.BrokenName, r, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Unopenable, r);
    }

    // probe: "…and a dry run of a write that does NOT reference it still predicts success (no over-refusal)"
    [Fact]
    public void APatchLaneDryRunOfAnUnrelatedWriteStillPredictsSuccess()
    {
        var r = ForwardTools.Forward(_main.Svc, formids: new[] { _main.SubjectFid }, source: ExcludedMasterMainOrder.MasterName,
            patch: "X314DryOk", dry_run: true);
        Assert.StartsWith("DRY RUN", r);
    }

    // probe: "in_place: the residual is named by CAUSE, not as 'NOT active in the load order'"
    [Fact]
    public void AnInPlaceForwardNamesTheUnopenableCauseNotAnInactivePlugin()
    {
        var r = ForwardTools.Forward(_main.Svc, formids: new[] { _main.SubjectFid }, source: ExcludedMasterMainOrder.MasterName,
            in_place: ExcludedMasterMainOrder.CleanName, acknowledge: true);
        Assert.StartsWith("error:", r);
        Assert.Contains(Unopenable, r);
        Assert.DoesNotContain("is NOT active in", r);
    }

    // probe: "remove in_place: an unopenable DECLARED master is a named refusal, not an escaping exception"
    [Fact]
    public void AnInPlaceRemoveOverAnUnopenableDeclaredMasterIsANamedRefusal()
    {
        var r = RemoveTools.Remove(_main.Svc, formids: new[] { _main.SubjectFid }, in_place: ExcludedMasterMainOrder.CleanName, acknowledge: true);
        Assert.StartsWith("error:", r);
        Assert.Contains(ExcludedMasterMainOrder.BrokenName, r, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Unopenable, r);
        Assert.Contains("UNTOUCHED", r);
    }

    // ---- the patch lane, a real order with Skyrim.esm and Update.esm ----

    // probe: "REAL order: a write that doesn't reference the broken plugin lands, with the baselines in its header"
    [Fact]
    public void InARealOrderAnUnrelatedWriteLandsWithTheBaselinesInItsHeader()
    {
        var r = ForwardTools.Forward(_real.Svc, formids: new[] { _real.SubjectFid }, source: "Skyrim.esm", patch: "SxOk");
        Assert.False(r.StartsWith("error:"), r);
        Assert.Contains("Skyrim.esm", MastersLineOf(r), StringComparison.OrdinalIgnoreCase);
    }

    // probe: "REAL order: a write REFERENCING the broken plugin's record refuses, naming the cause (never a single-master success)"
    [Fact]
    public void InARealOrderAWriteReferencingTheUnopenablePluginRefusesNamingIt()
    {
        var r = ForwardTools.Forward(_real.Svc, formids: new[] { _real.BrokenOwnFid }, source: ExcludedMasterRealOrder.CleanName, patch: "SxNeed");
        Assert.StartsWith("error:", r);
        Assert.Contains(ExcludedMasterRealOrder.BrokenName, r, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Unopenable, r);
    }

    // ---- an unopenable baseline master ----

    string CreateAgainstBrokenBaseline()
        => CreateTools.Create(_base.Svc, patch: "BlCreate", records: Json("""[{"record_type":"Keyword","editorid":"BlKw"}]"""));

    // probe: "a self-contained create REFUSES when a baseline master is unopenable (never a silently master-less plugin)"
    [Fact]
    public void ASelfContainedCreateRefusesWhenABaselineMasterIsUnopenable()
    {
        var r = CreateAgainstBrokenBaseline();
        Assert.StartsWith("error:", r);
        Assert.Contains("BASELINE master", r);
        Assert.Contains("Skyrim.esm", r, StringComparison.OrdinalIgnoreCase);
    }

    // probe: "…and renders that refusal exactly ONCE, without the serialize lead-in it never reached"
    [Fact]
    public void TheBaselineRefusalRendersOnceWithoutTheSerializeLeadIn()
    {
        var r = CreateAgainstBrokenBaseline();
        Assert.Equal(1, Count(r, "BASELINE master"));
        Assert.DoesNotContain("serialize or commit", r);
        Assert.DoesNotContain("the existing file is untouched", r);
    }

    // probe: "…and dry_run predicts that same refusal VERBATIM, not a paraphrase that can drift (#225 parity)"
    [Fact]
    public void ADryRunPredictsTheBaselineRefusalVerbatim()
    {
        var real = CreateAgainstBrokenBaseline();
        var realBody = real["error: ".Length..].Trim();
        var dry = ApplyTools.Apply(_base.Svc, patch: "BlDry", dry_run: true, ops: NameOp(_base.SubjectFid, "Bl"));
        Assert.StartsWith("error:", dry);
        Assert.Contains(realBody, dry);
    }

    // probe: "the closure copy renders the baseline refusal through the SAME substituting renderer as its sibling lanes"
    [Fact]
    public void TheClosureCopyRendersTheBaselineRefusalOnceWithoutItsLaneTrailer()
    {
        var r = CopyTools.Copy(_base.Svc, _base.DonorFid, null, new[] { "HeadParts" }, null, null, "BlDonorClone", "BlNpc", null);
        Assert.StartsWith("error:", r);
        Assert.Equal(1, Count(r, "BASELINE master"));
        Assert.DoesNotContain("Nothing usable was written", r);
        Assert.DoesNotContain("..", r);
        Assert.DoesNotContain("serialize failed", r);
    }

    // ---- compact and repoint ----

    // probe: "compact: an unopenable DECLARED master is a named Fail (not an escaping engine throw), naming the plugin and the remedy"
    [Fact]
    public void ACompactOverAnUnopenableDeclaredMasterIsANamedFail()
    {
        Assert.Null(_compact.Escaped);
        Assert.False(_compact.Outcome!.Success);
        var e = _compact.Outcome.Error ?? "";
        Assert.Contains(ExcludedMasterCompactOutcome.BrokenName, e, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("could not be opened for the serialize", e);
        Assert.Contains("Nothing was written", e);
    }

    // probe: "…and the fresh mod folder it cut is REMOVED — the cleanup an escaping throw would have skipped"
    [Fact]
    public void ACompactThatFailsOnAnUnopenableMasterLeavesNoModFolderBehind()
    {
        Assert.Equal(_compact.FoldersBefore, _compact.FoldersAfter);
    }

    // probe: "repoint: the per-plugin failure is NAMED — the unopenable master, the remedy, and the file left UNTOUCHED"
    [Fact]
    public void ARepointOverAnUnopenableMasterNamesThePluginAndLeavesItUntouched()
    {
        var r = _repoint.Render;
        Assert.Contains(ExcludedMasterRepointOutcome.ExternalName, r, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(ExcludedMasterRepointOutcome.BrokenName, r, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Unopenable, r);
        Assert.Contains("UNTOUCHED", r);
    }

    // probe: "…and it is a REPORTED result, not Guard.Tool's internal-failure wrapper (the throw would land after P′ is on disk)"
    [Fact]
    public void ARepointFailureIsAReportedResultNotAnInternalFailure()
    {
        Assert.Contains("repointed in place", _repoint.Render, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("internal houseCARL failure", _repoint.Render, StringComparison.OrdinalIgnoreCase);
    }

    // probe: "…and the compaction itself is still REPORTED — the failing repoint is one plugin's result, not the call's"
    [Fact]
    public void TheCompactionIsStillReportedWhenOneRepointFails()
    {
        Assert.True(_repoint.TargetRewritten);
        Assert.Contains("compacted", _repoint.Render, StringComparison.OrdinalIgnoreCase);
    }

    // ---- the in-place lane's single-master threshold ----

    // probe: "in_place, ONE-master header: the write LANDS even though that one master is unopenable (value read back off the file)"
    [Fact]
    public void AnInPlaceOneMasterHeaderOnTheUnopenablePluginWrites()
    {
        var r = ApplyTools.Apply(_ip.Svc, ops: DamageOp(_ip.BrokenOwnFid, "44"), in_place: ExcludedMasterInPlaceOrder.OneName, acknowledge: true);
        Assert.False(r.StartsWith("error:"), r);
        Assert.Equal((ushort)44, ExcludedMasterInPlaceOrder.DamageOnDisk(_ip.OnePath, _ip.BrokenOwn));
    }

    // probe: "…and the dry run AGREES (no over-refusal — the predictor's threshold, on the lane that predicts it)"
    [Fact]
    public void AnInPlaceOneMasterDryRunPredictsSuccess()
    {
        var r = ApplyTools.Apply(_ip.Svc, ops: DamageOp(_ip.BrokenOwnFid, "45"), in_place: ExcludedMasterInPlaceOrder.OneName,
            acknowledge: true, dry_run: true);
        Assert.StartsWith("DRY RUN", r);
    }

    // probe: "in_place, TWO-master header: the write REFUSES, naming the unopenable plugin as the cause"
    [Fact]
    public void AnInPlaceTwoMasterHeaderRefusesNamingTheUnopenablePlugin()
    {
        var r = ApplyTools.Apply(_ip.Svc, ops: DamageOp(_ip.BrokenOwnFid, "46"), in_place: ExcludedMasterInPlaceOrder.TwoName, acknowledge: true);
        Assert.StartsWith("error:", r);
        Assert.Contains(ExcludedMasterInPlaceOrder.BrokenName, r, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Unopenable, r);
        Assert.DoesNotContain("is NOT active in", r);
    }

    // probe: "…and the dry run predicts THAT refusal too (#225 parity on the in-place lane, both directions now pinned)"
    [Fact]
    public void AnInPlaceTwoMasterDryRunPredictsTheRefusal()
    {
        var r = ApplyTools.Apply(_ip.Svc, ops: DamageOp(_ip.BrokenOwnFid, "47"), in_place: ExcludedMasterInPlaceOrder.TwoName,
            acknowledge: true, dry_run: true);
        Assert.StartsWith("error:", r);
        Assert.Contains(ExcludedMasterInPlaceOrder.BrokenName, r, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Unopenable, r);
    }
}
