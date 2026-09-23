using System.Reflection;
using ModelContextProtocol.Server;
using HousecarlCore;
using HousecarlMcp;
using Xunit;
using W = HousecarlMcpTests.InPlaceGuardWorld;

namespace HousecarlMcpTests;

/// <summary>The in-place contract at the service: <c>in_place</c> needs <c>target=</c>, excludes <c>into=</c> /
/// <c>patch=</c>, a <c>target=</c> alone is refused, and a target outside the load order is refused rather than
/// retargeted. Plus the published opt-in: every tool declaring <c>in_place</c> has it off by default and a consent
/// parameter off by default. Moved from the <c>inplace-guard</c> probe.</summary>
[Trait("tier", "integration")]
[Collection(InPlaceGuardCollection.Name)]
public sealed class InPlaceGuardContractTests
{
    readonly W _w;
    public InPlaceGuardContractTests(W w) { _w = w; _w.UseCorpus(); }

    /// <summary>A service over [master, a fresh user copy, high]; disposing it disposes the resolver.</summary>
    LoadOrderService Service()
    {
        var user = _w.FreshUser();
        return LoadOrderService.ForGuard(LoadOrderResolver.Build(new[] { _w.MasterPath, user, _w.HighPath }),
            new UserConfigStore(_w.NewStorePath()));
    }

    BulkOp[] SetDamage => new[] { new BulkOp { Formid = _w.WeaponId, FieldPath = "BasicStats.Damage", Verb = "Set", Value = "1" } };

    // E contract (in_place<->target, _|_ into=)
    [Fact]
    public void TheEditLaneRefusesInPlaceWithoutTargetWithIntoAndTargetWithoutInPlace()
    {
        using var svc = Service();
        Assert.Contains("requires target=", svc.ApplyEdits(SetDamage, null, null, inPlace: true).Error);
        Assert.Contains("mutually exclusive", svc.ApplyEdits(SetDamage, null, "somepatch", fullReadback: false, target: W.UserName, inPlace: true).Error);
        Assert.Contains("only meaningful with in_place", svc.ApplyEdits(SetDamage, null, null, fullReadback: false, target: W.UserName, inPlace: false).Error);
    }

    // L contract (in_place<->target, _|_ into=) — create
    [Fact]
    public void TheCreateLaneRefusesInPlaceWithoutTargetWithIntoAndTargetWithoutInPlace()
    {
        using var svc = Service();
        Assert.Contains("requires target=", svc.InPlaceGuardCreate("Keyword", "HcIP_K", Array.Empty<BulkOp>(), null, null, false, null, null, null, target: null, inPlace: true, acknowledge: true).Error);
        Assert.Contains("mutually exclusive", svc.InPlaceGuardCreate("Keyword", "HcIP_K", Array.Empty<BulkOp>(), null, "somepatch", false, null, null, null, target: W.UserName, inPlace: true, acknowledge: true).Error);
        Assert.Contains("only meaningful with in_place", svc.InPlaceGuardCreate("Keyword", "HcIP_K", Array.Empty<BulkOp>(), null, null, false, null, null, null, target: W.UserName, inPlace: false, acknowledge: false).Error);
    }

    // U contract (in_place<->target, _|_ patch=) — remove
    [Fact]
    public void TheRemoveLaneRefusesInPlaceWithoutTargetWithPatchAndTargetWithoutInPlace()
    {
        using var svc = Service();
        Assert.Contains("requires target=", svc.RemoveRecords(new[] { _w.WeaponId }, null, target: null, inPlace: true, acknowledge: true).Error);
        Assert.Contains("mutually exclusive", svc.RemoveRecords(new[] { _w.WeaponId }, "somepatch", target: W.UserName, inPlace: true, acknowledge: true).Error);
        Assert.Contains("only meaningful with in_place", svc.RemoveRecords(new[] { _w.WeaponId }, null, target: W.UserName, inPlace: false).Error);
    }

    // F resolver refuses a non-load-order target
    [Fact]
    public void AnInPlaceEditOnATargetOutsideTheOrderIsRefused()
    {
        using var svc = Service();
        var o = svc.ApplyEdits(SetDamage, null, null, fullReadback: false, target: "NotAReal.esp", inPlace: true, acknowledge: true);
        Assert.False(o.Success);
        Assert.Contains("not an active plugin", o.Error);
    }

    // V resolver refuses a non-load-order target — remove
    [Fact]
    public void AnInPlaceRemoveOnATargetOutsideTheOrderIsRefused()
    {
        using var svc = Service();
        var o = svc.RemoveRecords(new[] { _w.WeaponId }, null, target: "NotAReal.esp", inPlace: true, acknowledge: true);
        Assert.False(o.Success);
        Assert.Contains("not an active plugin", o.Error);
    }

    // H opt-in by construction, every in_place-declaring tool (derived subject set)
    [Fact]
    public void EveryToolDeclaringInPlaceHasItOffByDefaultWithAConsentParameterOffByDefault()
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        var tools = ToolSurface.Assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>(inherit: false) is not null)
            .SelectMany(t => t.GetMethods(flags))
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>(inherit: false)?.Name is { Length: > 0 })
            .Where(m => m.GetParameters().Any(p => p.Name == "in_place"))
            .ToList();
        Assert.NotEmpty(tools);

        var offenders = new List<string>();
        foreach (var m in tools)
        {
            var ip = m.GetParameters().First(p => p.Name == "in_place");
            bool stringLane = ip.ParameterType == typeof(string);
            bool offByDefault = stringLane ? ip.DefaultValue is null : ip.DefaultValue is false;
            // On the string lane in_place names the file, so a target= beside it is the old pair left behind.
            bool noTargetPair = !stringLane || m.GetParameters().All(p => p.Name != "target");
            var ack = m.GetParameters().FirstOrDefault(p => p.Name == "acknowledge");
            bool ackOff = ack is not null && ack.DefaultValue is false;
            if (!offByDefault || !noTargetPair || !ackOff)
                offenders.Add($"{m.GetCustomAttribute<McpServerToolAttribute>()!.Name}(off={offByDefault},noTarget={noTargetPair},ackOff={ackOff})");
        }
        Assert.Empty(offenders);
    }
}
