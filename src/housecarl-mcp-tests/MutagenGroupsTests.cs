using System.Reflection;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A mod getter that records which group properties Mutagen's own typed walk reads, following its
/// interface fallback (a trap subtype walks as the trap base) back through this recorder.</summary>
public class GroupReadRecorder : DispatchProxy
{
    public ISkyrimModGetter Real = null!;
    public List<string> Read { get; } = new();
    int _depth;

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method!.Name.StartsWith("get_", StringComparison.Ordinal)
            && typeof(IMajorRecordGetterEnumerable).IsAssignableFrom(method.ReturnType)) Read.Add(method.Name[4..]);
        if (method.Name == "EnumerateMajorRecords" && args is [Type t, bool unknown] && _depth < 4)
        {
            _depth++;
            try { return SkyrimModMixIn.EnumerateMajorRecords((ISkyrimModGetter)(object)this, t, unknown).ToList(); }
            finally { _depth--; }
        }
        return method.Invoke(Real, args);
    }

    /// <summary>The groups, in first-read order, Mutagen's typed walk of an empty mod reads for <paramref name="t"/>.</summary>
    public static List<string> GroupsWalked(Type t)
    {
        var proxy = DispatchProxy.Create<ISkyrimModGetter, GroupReadRecorder>();
        var rec = (GroupReadRecorder)(object)proxy;
        rec.Real = new SkyrimMod(ModKey.FromFileName("HcGroupRecorder.esp"), SkyrimRelease.SkyrimSE);
        _ = SkyrimModMixIn.EnumerateMajorRecords(proxy, t, throwIfUnknown: true).ToList();
        return rec.Read.Distinct().ToList();
    }
}

/// <summary>The group map is derived from Mutagen's getter interfaces and walks exactly the groups Mutagen's own
/// generated switch walks for every type a gather can seek.</summary>
[Trait("tier", "unit")]
public sealed class MutagenGroupsTests
{
    static string[]? Names(Type t) => MutagenGroups.PropsOf(t)?.Select(p => p.Name).ToArray();

    [Fact]
    public void CellsReferencesAndNavmeshesShareOneGroupSetAndAnNpcHasItsOwn()
    {
        var cellGroups = new[] { "Cells", "Worldspaces" };
        Assert.Equal(cellGroups, Names(typeof(ICellGetter)));
        Assert.Equal(cellGroups, Names(typeof(IPlacedObjectGetter)));
        Assert.Equal(cellGroups, Names(typeof(IPlacedNpcGetter)));
        Assert.Equal(cellGroups, Names(typeof(INavigationMeshGetter)));
        Assert.Equal(new[] { "Npcs" }, Names(typeof(INpcGetter)));
        Assert.Equal(new[] { "DialogTopics" }, Names(typeof(IDialogResponsesGetter)));
        Assert.Null(Names(typeof(IMajorRecordGetter)));
        Assert.Null(Names(typeof(ISkyrimMajorRecordGetter)));
        var shared = Assert.Single(MutagenGroups.SharedGroups(new[] { typeof(ICellGetter), typeof(INpcGetter), typeof(IPlacedObjectGetter) }));
        Assert.Equal(new[] { typeof(ICellGetter), typeof(IPlacedObjectGetter) }, shared);
    }

    /// <summary>Every seek type the order index carries, and the base interface, against the groups Mutagen's switch
    /// reads; null means the flat walk, which reads every group.</summary>
    [Fact]
    public void EverySeekTypeWalksTheGroupsMutagensSwitchWalks()
    {
        var all = typeof(ISkyrimModGetter).GetProperties()
            .Where(p => typeof(IMajorRecordGetterEnumerable).IsAssignableFrom(p.PropertyType) && p.PropertyType.IsGenericType)
            .Select(p => p.Name).ToHashSet();
        var seeks = typeof(ISkyrimModGetter).Assembly.GetTypes()
            .Where(c => c.IsClass && !c.IsAbstract && typeof(ISkyrimMajorRecordGetter).IsAssignableFrom(c))
            .Select(WriteEngine.SeekTypeFor)
            .Append(typeof(IMajorRecordGetter)).Append(typeof(ISkyrimMajorRecordGetter)).Distinct().ToList();
        Assert.True(seeks.Count > 100, $"only {seeks.Count} seek types found");
        var wrong = new List<string>();
        foreach (var t in seeks)
        {
            var walked = GroupReadRecorder.GroupsWalked(t);
            bool same = Names(t) is { } derived ? derived.SequenceEqual(walked) : walked.ToHashSet().SetEquals(all);
            if (!same) wrong.Add($"{t.Name}: derived {string.Join(",", Names(t) ?? new[] { "(flat)" })}, Mutagen {string.Join(",", walked)}");
        }
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }
}
