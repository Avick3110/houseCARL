using System.Text.RegularExpressions;
using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The closed SkyPatcher grammar catalog (<see cref="SkyPatcherCatalog"/>, from the
/// <c>skypatcher-catalog-guard</c> probe): it loads, covers the documented record types, holds its shape and
/// tractability invariants, classifies keys (an unknown one warns), refuses a malformed node at load, and matches the
/// skypatcher-authoring skill's router table it was transcribed from.</summary>
[Trait("tier", "unit")]
public sealed class SkyPatcherCatalogTests
{
    static readonly SkyPatcherCatalog Cat = SkyPatcherCatalog.Load();

    // catalog covers >= 27 record types
    [Fact]
    public void TheCatalogCoversTheDocumentedRecordTypes() =>
        Assert.True(Cat.Records.Count >= 27, $"{Cat.Records.Count} record types");

    // each record has its required dimension fields (OMOD may lack a primaryFilter); subfolder is unique
    [Fact]
    public void EveryRecordHasItsDimensionFieldsAndAUniqueSubfolder()
    {
        foreach (var r in Cat.Records)
        {
            Assert.False(string.IsNullOrWhiteSpace(r.RecordType), r.Sig);
            Assert.False(string.IsNullOrWhiteSpace(r.Subfolder), r.RecordType);
            Assert.False(string.IsNullOrWhiteSpace(r.Sig), r.RecordType);
            if (!IsOmod(r)) Assert.False(string.IsNullOrWhiteSpace(r.PrimaryFilter), r.RecordType);
        }
        var dup = Cat.Records.GroupBy(r => r.Subfolder, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key);
        Assert.Empty(dup);
    }

    // OMOD: no ops + a documented-gap note; every other type has >= 1 operation and a Primary filter
    [Fact]
    public void OmodIsTheEmptyGapAndEveryOtherTypeHasOpsAndAPrimaryFilter()
    {
        var omod = Assert.Single(Cat.Records, IsOmod);
        Assert.Empty(omod.Operations);
        Assert.NotNull(omod.Note);
        foreach (var r in Cat.Records.Where(r => !IsOmod(r)))
        {
            Assert.True(r.Operations.Count > 0, $"{r.RecordType} has no operation");
            Assert.True(r.Filters.Any(f => f.Kind == SkyPatcherFilterKind.Primary), $"{r.RecordType} has no Primary filter");
        }
    }

    // collection ⇒ COLLECTION; mirror ⇒ HARD; mult/add_numeric ⇒ stateful
    [Fact]
    public void EveryOperationShapeImpliesItsTractability()
    {
        var bad = Cat.Records.SelectMany(r => r.Operations.Select(op => (r.RecordType, op)))
            .Where(x => (x.op.Shape == SkyPatcherOpShape.Collection && x.op.Tractability != SkyPatcherTractability.Collection)
                     || (x.op.Shape == SkyPatcherOpShape.Mirror && x.op.Tractability != SkyPatcherTractability.Hard)
                     || ((x.op.Shape == SkyPatcherOpShape.Mult || x.op.Shape == SkyPatcherOpShape.AddNumeric) && !x.op.Stateful))
            .Select(x => $"{x.RecordType}.{x.op.Name}");
        Assert.Empty(bad);
    }

    // weapon.attackDamage ⇒ Operation / set / CLEAN
    [Fact]
    public void AttackDamageIsACleanSetOperation()
    {
        var k = Cat.Classify(Weapon, "attackDamage");
        Assert.Equal(SkyPatcherKeyRole.Operation, k.Role);
        Assert.Equal(SkyPatcherOpShape.Set, k.Operation?.Shape);
        Assert.Equal(SkyPatcherTractability.Clean, k.Operation?.Tractability);
    }

    // weapon.filterByWeaponsExcluded ⇒ Filter base=filterByWeapons connective=Excluded
    // …while its documented suffixed form ⇒ Filter base=filterByFirstPersonModel
    [Theory]
    [InlineData("filterByWeaponsExcluded", "filterByWeapons", "Excluded")]
    [InlineData("filterByFirstPersonModelOr", "filterByFirstPersonModel", "Or")]
    public void ASuffixedFilterSplitsIntoBaseAndConnective(string key, string baseKey, string connective)
    {
        var k = Cat.Classify(Weapon, key);
        Assert.Equal(SkyPatcherKeyRole.Filter, k.Role);
        Assert.Equal(baseKey, k.BaseKey);
        Assert.Equal(connective, k.Connective);
    }

    // weapon.keywordsToAdd ⇒ Operation / collection
    [Fact]
    public void KeywordsToAddIsACollectionOperation()
    {
        var k = Cat.Classify(Weapon, "keywordsToAdd");
        Assert.Equal(SkyPatcherKeyRole.Operation, k.Role);
        Assert.Equal(SkyPatcherOpShape.Collection, k.Operation?.Shape);
    }

    // an unknown key ⇒ Unknown (bundled-or-warn); bare form of a suffix-only filter ⇒ Unknown (undocumented token warns)
    [Theory]
    [InlineData("totallyBogusKeyXYZ")]
    [InlineData("filterByFirstPersonModel")]
    public void AnUndocumentedKeyIsUnknown(string key) =>
        Assert.Equal(SkyPatcherKeyRole.Unknown, Cat.Classify(Weapon, key).Role);

    // wrong-kind 'connectives' throws loudly at load
    [Fact]
    public void AWrongKindConnectivesNodeThrowsAtLoad() =>
        Assert.Throws<InvalidOperationException>(() => SkyPatcherCatalog.LoadFrom(
            """[{"recordType":"T","sig":"TTTT","subfolder":"t","primaryFilter":"f","filters":[{"name":"f","kind":"primary","connectives":"oops"}]}]"""));

    // HARD op present and HARD on ALL of the records that carry it
    [Theory]
    [InlineData("mirrorArmor")]
    [InlineData("changeStats")]
    [InlineData("setRandomVisualStyle")]
    [InlineData("mgefsToAdd")]
    public void AFlagshipHardOpIsHardOnEveryRecord(string opName)
    {
        var hits = Cat.Records.SelectMany(r => r.Operations.Where(o => o.Name == opName).Select(o => (r.RecordType, o.Tractability))).ToList();
        Assert.NotEmpty(hits);
        Assert.All(hits, h => Assert.Equal(SkyPatcherTractability.Hard, h.Tractability));
    }

    // catalog count == router row count; each catalog subfolder has a router row; recordType, sig and primaryFilter
    // match the skypatcher-authoring SKILL.md router table row for the subfolder
    [Fact]
    public void TheCatalogMatchesTheSkillRouterTable()
    {
        var rows = RouterRows();
        Assert.Equal(rows.Count, Cat.Records.Count);
        foreach (var r in Cat.Records)
            Assert.Contains(rows, e => e.Subfolder.Equals(r.Subfolder, StringComparison.OrdinalIgnoreCase));
        foreach (var e in rows)
        {
            var r = Cat.ForSubfolder(e.Subfolder);
            Assert.True(r is not null, $"router subfolder '{e.Subfolder}' is not in the catalog");
            Assert.Equal(e.Name, r.RecordType);
            Assert.Contains(r.Sig, e.Sigs);
            var parts = r.PrimaryFilter.Split(" / ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (e.PrimaryFilters.Length == 0) Assert.Equal("", r.PrimaryFilter);
            else Assert.All(e.PrimaryFilters, p => Assert.Contains(p, parts));
        }
    }

    static SkyPatcherRecordCatalog Weapon => Cat.ForSubfolder("weapon") ?? throw new InvalidOperationException("no weapon subfolder in the catalog");

    static bool IsOmod(SkyPatcherRecordCatalog r) => r.Sig.Equals("OMOD", StringComparison.OrdinalIgnoreCase);

    /// <summary>The router table rows of the skypatcher-authoring SKILL.md: record name, signatures, subfolder, primary filters.</summary>
    static List<(string Name, string[] Sigs, string Subfolder, string[] PrimaryFilters)> RouterRows()
    {
        var path = Path.Combine(HarnessPaths.RepoRoot, ".claude", "skills", "skypatcher-authoring", "SKILL.md");
        var rows = new List<(string, string[], string, string[])>();
        bool inTable = false;
        foreach (var line in File.ReadAllLines(path))
        {
            if (!inTable) { inTable = line.StartsWith("| Record type", StringComparison.Ordinal); continue; }
            if (!line.StartsWith("|", StringComparison.Ordinal)) break;
            if (line.StartsWith("|---", StringComparison.Ordinal)) continue;

            var cells = line.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();
            Assert.True(cells.Length == 4, $"router row has {cells.Length} columns: {line.Trim()}");
            int open = cells[0].IndexOf(" (", StringComparison.Ordinal);
            Assert.True(open >= 0 && cells[0].EndsWith(")", StringComparison.Ordinal), $"router row names no signature: {cells[0]}");
            var sigs = cells[0].Substring(open + 2, cells[0].Length - open - 3)
                .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            rows.Add((cells[0].Substring(0, open).Trim(), sigs, Backticked(cells[1]).FirstOrDefault() ?? "", Backticked(cells[2])));
        }
        Assert.True(rows.Count > 0, $"no router table in {path}");
        return rows;
    }

    static string[] Backticked(string cell) =>
        Regex.Matches(cell, "`([^`]+)`").Select(m => m.Groups[1].Value.Trim()).ToArray();
}
