using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A condition element's summary line is one compact line: function, the arm's own used parameters,
/// operator, comparand, run-on and flags. Read on the binary overlay the product reads plugins through.</summary>
[Trait("tier", "unit")]
public sealed class ConditionSummaryLineTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "hc-condition-line-" + Guid.NewGuid().ToString("N"));
    readonly SkyrimMod _mod = new(new ModKey("hc_condline", ModType.Plugin), SkyrimRelease.SkyrimSE);
    readonly string _perk, _npc, _global, _threshold, _ref;

    public ConditionSummaryLineTests()
    {
        Directory.CreateDirectory(_dir);
        var perk = _mod.Perks.AddNew(); perk.EditorID = "HC_CondLine_Perk"; _perk = perk.FormKey.ToString();
        var npc = _mod.Npcs.AddNew(); npc.EditorID = "HC_CondLine_Npc"; _npc = npc.FormKey.ToString();
        var glob = _mod.Globals.AddNewFloat(); glob.EditorID = "HC_CondLine_Value"; _global = glob.FormKey.ToString();
        var limit = _mod.Globals.AddNewFloat(); limit.EditorID = "HC_CondLine_Limit"; _threshold = limit.FormKey.ToString();
        var refKey = _mod.GetNextFormKey(); _ref = refKey.ToString();
        var holder = _mod.Perks.AddNew(); holder.EditorID = "HC_CondLine_Holder";

        var hasPerk = new HasPerkConditionData();
        hasPerk.Perk = new FormLinkOrIndex<IPerkGetter>(hasPerk, perk.FormKey);
        holder.Conditions.Add(new ConditionFloat
            { CompareOperator = CompareOperator.EqualTo, ComparisonValue = 1f, Flags = Condition.Flag.OR, Data = hasPerk });

        var isId = new GetIsIDConditionData { RunOnType = Condition.RunOnType.Target };
        isId.Object = new FormLinkOrIndex<IReferenceableObjectGetter>(isId, npc.FormKey);
        holder.Conditions.Add(new ConditionFloat { CompareOperator = CompareOperator.EqualTo, ComparisonValue = 1f, Data = isId });

        var global = new GetGlobalValueConditionData();
        global.Global = new FormLinkOrIndex<IGlobalGetter>(global, glob.FormKey);
        var byGlobal = new ConditionGlobal { CompareOperator = CompareOperator.GreaterThanOrEqualTo, Data = global };
        byGlobal.ComparisonValue.SetTo(limit.FormKey);
        holder.Conditions.Add(byGlobal);

        var onRef = new GetIsIDConditionData { RunOnType = Condition.RunOnType.Reference };
        onRef.Reference.SetTo(refKey);
        onRef.Object = new FormLinkOrIndex<IReferenceableObjectGetter>(onRef, npc.FormKey);
        holder.Conditions.Add(new ConditionFloat
            { CompareOperator = CompareOperator.NotEqualTo, ComparisonValue = 0f, Flags = Condition.Flag.SwapSubjectAndTarget | Condition.Flag.OR, Data = onRef });

        var byAlias = new GetIsIDConditionData { RunOnType = Condition.RunOnType.Reference, UseAliases = true };
        byAlias.Reference.SetTo(refKey);
        byAlias.Object = new FormLinkOrIndex<IReferenceableObjectGetter>(byAlias, 3u);
        holder.Conditions.Add(new ConditionFloat { CompareOperator = CompareOperator.EqualTo, ComparisonValue = 1f, Data = byAlias });

        var path = Path.Combine(_dir, _mod.ModKey.FileName);
        _mod.BeginWrite.ToPath(path).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* temp cleanup best-effort */ } }

    IReadOnlyList<FieldValue> Read(int depth, params string[] paths)
    {
        var overlayPath = Path.Combine(_dir, _mod.ModKey.FileName);
        using var overlay = SkyrimMod.CreateFromBinaryOverlay(overlayPath, SkyrimRelease.SkyrimSE);
        var holder = overlay.Perks.Single(p => p.EditorID == "HC_CondLine_Holder");
        return ReadEngine.ReadFields(holder, paths, depth).Fields.ToList();
    }

    static string? Line(IReadOnlyList<FieldValue> fields, string path) =>
        fields.FirstOrDefault(f => f.Path == path) is { } f ? (f.HasValue ? f.Token : f.Note) : null;

    [Fact]
    public void HasPerkReadsAsItsFunctionPerkComparisonRunOnAndOrLast() =>
        Assert.Equal($"[HasPerk({_perk}) == 1 on Subject OR]", Line(Read(2, "Conditions"), "Conditions[0]"));

    [Fact]
    public void GetIsIdNamesItsObjectAndTheTargetRunOn() =>
        Assert.Equal($"[GetIsID({_npc}) == 1 on Target]", Line(Read(2, "Conditions"), "Conditions[1]"));

    [Fact]
    public void AGlobalComparandIsTheGlobalsFormId() =>
        Assert.Equal($"[GetGlobalValue({_global}) >= {_threshold} on Subject]", Line(Read(2, "Conditions"), "Conditions[2]"));

    [Fact]
    public void AReferenceRunOnCarriesItsReferenceAndOtherFlagsComeBeforeOr() =>
        Assert.Equal($"[GetIsID({_npc}) != 0 on Reference {_ref} SwapSubjectAndTarget OR]", Line(Read(2, "Conditions"), "Conditions[3]"));

    [Fact]
    public void ADepthOneReadOfTheElementGivesTheSameLine() =>
        Assert.StartsWith($"[HasPerk({_perk}) == 1 on Subject OR]", Line(Read(1, "Conditions[0]"), "Conditions[0]"));

    [Fact]
    public void TheLineCarriesTheFirstFormIdItSpellsForResolveNames() =>
        Assert.Equal(_perk, Read(2, "Conditions").Single(f => f.Path == "Conditions[0]").NoteRef);

    [Fact]
    public void AnAliasModeParameterIsItsIndexAndTheReferenceIsTheFormIdForResolveNames()
    {
        var fields = Read(2, "Conditions");
        var element = fields.Single(f => f.Path == "Conditions[4]");
        Assert.Equal($"[GetIsID(alias 3) == 1 on Reference {_ref}]", Line(fields, "Conditions[4]"));
        Assert.Equal(_ref, element.NoteRef);
    }

    [Fact]
    public void DepthBelowTheElementStillPrintsEverySubField()
    {
        var fields = Read(3, "Conditions");
        Assert.StartsWith("[HasPerkConditionData]", Line(fields, "Conditions[0].Data"));
        Assert.Equal(_perk, Line(fields, "Conditions[0].Data.Perk"));
        Assert.NotNull(Line(fields, "Conditions[0].Data.SecondUnusedIntParameter"));
        Assert.Equal("EqualTo", Line(fields, "Conditions[0].CompareOperator"));
        Assert.NotNull(Line(fields, "Conditions[0].Unknown1"));
    }
}

/// <summary>The checks that key on a summary starting with '[' still read a condition element as a present container.</summary>
[Collection("records")]
[Trait("tier", "integration")]
public sealed class ConditionSummaryLinePresenceTests : RecordsTestBase
{
    public ConditionSummaryLinePresenceTests(RecordsFixture f) : base(f) { }

    [Theory]
    [InlineData("Conditions[0] exists", 1)]
    [InlineData("Conditions[0] missing", 0)]
    public void AConditionElementIsPresent(string clause, int matches)
    {
        var r = RecordsTools.Records(Svc, types: new[] { "MGEF" }, where: new[] { clause, "editorid = OtherMgef" });
        Assert.Contains($"scan: {matches} match", r);
    }
}
