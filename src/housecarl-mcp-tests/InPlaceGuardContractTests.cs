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
        var r1 = svc.ApplyEdits(SetDamage, null, null, inPlace: true);
        Assert.False(r1.Success);
        Assert.Contains("requires target=", r1.Error);
        var r2 = svc.ApplyEdits(SetDamage, null, "somepatch", fullReadback: false, target: W.UserName, inPlace: true);
        Assert.False(r2.Success);
        Assert.Contains("mutually exclusive", r2.Error);
        var r3 = svc.ApplyEdits(SetDamage, null, null, fullReadback: false, target: W.UserName, inPlace: false);
        Assert.False(r3.Success);
        Assert.Contains("only meaningful with in_place", r3.Error);
    }

    // L contract (in_place<->target, _|_ into=) — create
    [Fact]
    public void TheCreateLaneRefusesInPlaceWithoutTargetWithIntoAndTargetWithoutInPlace()
    {
        using var svc = Service();
        var r4 = svc.InPlaceGuardCreate("Keyword", "HcIP_K", Array.Empty<BulkOp>(), null, null, false, null, null, null, target: null, inPlace: true, acknowledge: true);
        Assert.False(r4.Success);
        Assert.Contains("requires target=", r4.Error);
        var r5 = svc.InPlaceGuardCreate("Keyword", "HcIP_K", Array.Empty<BulkOp>(), null, "somepatch", false, null, null, null, target: W.UserName, inPlace: true, acknowledge: true);
        Assert.False(r5.Success);
        Assert.Contains("mutually exclusive", r5.Error);
        var r6 = svc.InPlaceGuardCreate("Keyword", "HcIP_K", Array.Empty<BulkOp>(), null, null, false, null, null, null, target: W.UserName, inPlace: false, acknowledge: false);
        Assert.False(r6.Success);
        Assert.Contains("only meaningful with in_place", r6.Error);
    }

    // U contract (in_place<->target, _|_ patch=) — remove
    [Fact]
    public void TheRemoveLaneRefusesInPlaceWithoutTargetWithPatchAndTargetWithoutInPlace()
    {
        using var svc = Service();
        var r7 = svc.RemoveRecords(new[] { _w.WeaponId }, null, target: null, inPlace: true, acknowledge: true);
        Assert.False(r7.Success);
        Assert.Contains("requires target=", r7.Error);
        var r8 = svc.RemoveRecords(new[] { _w.WeaponId }, "somepatch", target: W.UserName, inPlace: true, acknowledge: true);
        Assert.False(r8.Success);
        Assert.Contains("mutually exclusive", r8.Error);
        var r9 = svc.RemoveRecords(new[] { _w.WeaponId }, null, target: W.UserName, inPlace: false);
        Assert.False(r9.Success);
        Assert.Contains("only meaningful with in_place", r9.Error);
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
