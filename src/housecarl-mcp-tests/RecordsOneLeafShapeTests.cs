using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>An MO2 instance whose one plugin holds an armor with two addons and a spell with two effects, written to
/// disk so the reads go through Mutagen's binary overlay.</summary>
public sealed class OneLeafWorld : IDisposable
{
    public string Root { get; }
    public LoadOrderService Svc { get; }
    public FormKey Addon0 { get; }
    public FormKey Addon1 { get; }
    public FormKey Armor { get; }
    public FormKey Mgef { get; }
    public FormKey Spell { get; }

    public OneLeafWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-one-leaf-tests-" + Guid.NewGuid().ToString("N"));
        var instance = Path.Combine(Root, "instance");
        var profiles = Path.Combine(instance, "profiles", "Default");
        var modDir = Path.Combine(instance, "mods", "OneLeafMod");
        foreach (var d in new[] { profiles, modDir, Path.Combine(Root, "game", "Data") }) Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");

        var key = new ModKey("HcOneLeaf", ModType.Master);
        var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        var a0 = mod.ArmorAddons.AddNew(); a0.EditorID = "HcOneLeafAddon0"; Addon0 = a0.FormKey;
        var a1 = mod.ArmorAddons.AddNew(); a1.EditorID = "HcOneLeafAddon1"; Addon1 = a1.FormKey;
        var armo = mod.Armors.AddNew(); armo.EditorID = "HcOneLeafArmor"; Armor = armo.FormKey;
        armo.Armature.Add(Addon0);
        armo.Armature.Add(Addon1);
        var mgef = mod.MagicEffects.AddNew(); mgef.EditorID = "HcOneLeafMgef"; Mgef = mgef.FormKey;
        var spel = mod.Spells.AddNew(); spel.EditorID = "HcOneLeafSpell"; Spell = spel.FormKey;
        { var e = new Effect(); e.BaseEffect.SetTo(Mgef); e.Data = new EffectData { Magnitude = 5 }; spel.Effects.Add(e); }
        { var e = new Effect(); e.BaseEffect.SetTo(Mgef); e.Data = new EffectData { Magnitude = 4 }; spel.Effects.Add(e); }
        mod.BeginWrite.ToPath(Path.Combine(modDir, key.FileName.String)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        File.WriteAllText(Path.Combine(profiles, "loadorder.txt"), "# header\r\n" + key.FileName + "\r\n");
        File.WriteAllText(Path.Combine(profiles, "plugins.txt"), "*" + key.FileName + "\r\n");
        File.WriteAllText(Path.Combine(profiles, "modlist.txt"), "# header\r\n+OneLeafMod\r\n");

        Svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(Root, "houseCARL.user.json")));
        Svc.Stats();
    }

    public void Dispose()
    {
        Svc.Dispose();
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}

public sealed class OneLeafFixture : IDisposable
{
    public OneLeafWorld W { get; } = new();
    public void Dispose() => W.Dispose();
}

/// <summary>A json or to_file row lists every leaf flat as {path, value} or {path, note}, never a nested 'cells'
/// object, so a consumer reads f[path] = value whatever was projected (#1070).</summary>
[Trait("tier", "integration")]
public sealed class RecordsOneLeafShapeTests : IClassFixture<OneLeafFixture>
{
    readonly OneLeafWorld _w;
    public RecordsOneLeafShapeTests(OneLeafFixture f) => _w = f.W;

    static RecordsTools.RecordsProject Fields(params string[] paths) => new() { form = "fields", fields = paths };
    static RecordsTools.RecordsProject Rows(params string[] paths) => new() { form = "rows", fields = paths };

    JsonElement JsonRow(FormKey fk, RecordsTools.RecordsProject p) =>
        JsonDocument.Parse(RecordsTools.Records(_w.Svc, formids: new[] { FormIdToken.Of(fk) }, format: "json", project: p))
                    .RootElement.GetProperty("records")[0].Clone();

    JsonElement FileRow(FormKey fk, RecordsTools.RecordsProject p)
    {
        var art = Path.Combine(_w.Root, "out", Guid.NewGuid().ToString("N") + ".jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(art)!);
        RecordsTools.Records(_w.Svc, formids: new[] { FormIdToken.Of(fk) }, project: p, to_file: art);
        return JsonDocument.Parse(File.ReadLines(art).Skip(1).First()).RootElement.Clone();
    }

    /// <summary>Each entry has a path and exactly one of value or note, and no entry nests others; returns path → value-or-note.</summary>
    static List<(string Path, string? Value)> Leaves(JsonElement row)
    {
        var outp = new List<(string, string?)>();
        foreach (var e in row.GetProperty("fields").EnumerateArray())
        {
            Assert.False(e.TryGetProperty("cells", out _), e.ToString());
            bool v = e.TryGetProperty("value", out var value), n = e.TryGetProperty("note", out _);
            Assert.True(v ^ n, "one of value|note: " + e);
            outp.Add((e.GetProperty("path").GetString()!, v ? value.GetString() : null));
        }
        Assert.Equal(outp.Count, outp.Select(l => l.Item1).Distinct().Count());
        return outp;
    }

    /// <summary>Every leaf under an element of <paramref name="list"/> follows that element's own entry and comes
    /// before the next element's.</summary>
    internal static void AssertGrouped(IReadOnlyList<string> paths, string list)
    {
        string? at = null;
        foreach (var p in paths)
        {
            if (!p.StartsWith(list + "[", StringComparison.Ordinal)) continue;
            var elem = p[..(p.IndexOf(']', list.Length) + 1)];
            if (p == elem) { at = elem; continue; }
            Assert.True(at == elem, $"'{p}' sits under '{at}', not its own element: " + string.Join(", ", paths));
        }
    }

    /// <summary>The text lane's field-line paths, in order.</summary>
    List<string> TextPaths(FormKey fk, RecordsTools.RecordsProject p) =>
        RecordsTools.Records(_w.Svc, formids: new[] { FormIdToken.Of(fk) }, project: p).Split('\n')
                    .Where(l => l.StartsWith("  ", StringComparison.Ordinal) && l.Contains(" = "))
                    .Select(l => l[2..l.IndexOf(" = ", StringComparison.Ordinal)]).ToList();

    JsonElement JsonRowCapped(FormKey fk, RecordsTools.RecordsProject p, int maxChars) =>
        JsonDocument.Parse(RecordsTools.Records(_w.Svc, formids: new[] { FormIdToken.Of(fk) }, format: "json", project: p, max_chars: maxChars))
                    .RootElement.GetProperty("records")[0].Clone();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AQuantifiedLinkListIsOneValueEntryPerElement(bool toFile)
    {
        var p = Fields("Armature[*]");
        var leaves = Leaves(toFile ? FileRow(_w.Armor, p) : JsonRow(_w.Armor, p));
        Assert.Equal(new[] { ("Armature[0]", FormIdToken.Of(_w.Addon0)), ("Armature[1]", FormIdToken.Of(_w.Addon1)) },
                     leaves.Select(l => (l.Path, l.Value)).ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AQuantifiedStructListLeadsEachElementWithItsOwnLineThenItsLeaves(bool toFile)
    {
        var p = Fields("Effects[*]");
        var leaves = Leaves(toFile ? FileRow(_w.Spell, p) : JsonRow(_w.Spell, p));
        var paths = leaves.Select(l => l.Path).ToList();
        Assert.Equal(0, paths.IndexOf("Effects[0]"));
        Assert.Contains("Effects[1].Data.Magnitude", paths);
        AssertGrouped(paths, "Effects");
        Assert.Equal("5", leaves.Single(l => l.Path == "Effects[0].Data.Magnitude").Value);
        Assert.Equal("4", leaves.Single(l => l.Path == "Effects[1].Data.Magnitude").Value);
        Assert.Equal(FormIdToken.Of(_w.Mgef), leaves.Single(l => l.Path == "Effects[1].BaseEffect").Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AQuantifiedSubPathIsOneValueEntryPerElement(bool toFile)
    {
        var p = Fields("Effects[*].Data.Magnitude");
        var leaves = Leaves(toFile ? FileRow(_w.Spell, p) : JsonRow(_w.Spell, p));
        Assert.Equal(new[] { ("Effects[0].Data.Magnitude", "5"), ("Effects[1].Data.Magnitude", "4") },
                     leaves.Select(l => (l.Path, l.Value)).ToArray());
    }

    [Theory]
    [InlineData(false, "Effects[*]", "Effects[*].Data.Magnitude")]
    [InlineData(true, "Effects[*]", "Effects[*].Data.Magnitude")]
    [InlineData(false, "Effects", "Effects[*]")]
    [InlineData(true, "Effects", "Effects[*]")]
    public void OverlappingRequestsListEachPathOnce(bool toFile, string a, string b)
    {
        var p = new RecordsTools.RecordsProject { form = "fields", fields = new[] { a, b }, depth = 4 };
        var leaves = Leaves(toFile ? FileRow(_w.Spell, p) : JsonRow(_w.Spell, p));   // Leaves asserts the paths are distinct
        Assert.Equal("5", leaves.Single(l => l.Path == "Effects[0].Data.Magnitude").Value);
        Assert.Equal("4", leaves.Single(l => l.Path == "Effects[1].Data.Magnitude").Value);
        AssertGrouped(leaves.Select(l => l.Path).ToList(), "Effects");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnElementsLeavesFollowItsOwnElementWhenAPlainColumnOverlaps(bool toFile)
    {
        // The plain column reaches absent optionals the row omits; they must not land after the last element.
        var p = new RecordsTools.RecordsProject { form = "fields", fields = new[] { "Effects[*]", "Effects" }, depth = 4 };
        var paths = Leaves(toFile ? FileRow(_w.Spell, p) : JsonRow(_w.Spell, p)).Select(l => l.Path).ToList();
        Assert.Contains("Effects[1].Data.Magnitude", paths);
        AssertGrouped(paths, "Effects");
    }

    [Theory]
    [InlineData("Effects[*]", "Effects")]
    [InlineData("Effects", "Effects[*]")]
    [InlineData("Effects[*]", "Effects[*].Data.Magnitude")]
    [InlineData("Effects[*].Data.Magnitude", "Effects[*]")]
    public void TheTextLaneListsEachPathOnceToo(string a, string b)
    {
        var paths = TextPaths(_w.Spell, new RecordsTools.RecordsProject { form = "fields", fields = new[] { a, b }, depth = 4 });
        Assert.Contains("Effects[1]", paths);
        Assert.Equal(paths.Count, paths.Distinct().Count());
        AssertGrouped(paths, "Effects");
    }

    [Fact]
    public void AMaxCharsCutWritesAnElementWholeOrNotAtAll()
    {
        var p = Fields("Effects[*]");
        var whole = Leaves(JsonRow(_w.Spell, p)).Select(l => l.Path).ToHashSet();
        var textCount = TextPaths(_w.Spell, p).Count;
        bool cutBetween = false;
        for (int cap = 200; cap <= 4000; cap += 25)
        {
            var row = JsonRowCapped(_w.Spell, p, cap);
            var leaves = Leaves(row);
            var shown = leaves.Select(l => l.Path).Where(x => x != "…").ToList();
            foreach (var elem in shown.Where(x => x is "Effects[0]" or "Effects[1]"))
            {
                bool Of(string x) => x == elem || x.StartsWith(elem + ".", StringComparison.Ordinal);
                Assert.Equal(whole.Where(Of).OrderBy(x => x), shown.Where(Of).OrderBy(x => x));
            }
            if (row.GetProperty("fields").EnumerateArray().LastOrDefault() is { } last && last.GetProperty("path").GetString() == "…")
            {
                // The count is in the text lane's unit: one field line per element row.
                Assert.Contains($"of {textCount} field lines shown", last.GetProperty("note").GetString());
                if (shown.Contains("Effects[0]") && !shown.Contains("Effects[1]")) cutBetween = true;
            }
        }
        Assert.True(cutBetween, "no max_chars in the sweep cut between the two elements");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnElementAskedTwiceIsListedOnce(bool toFile)
    {
        var leaves = Leaves(toFile ? FileRow(_w.Armor, Fields("Armature[*]", "Armature[0]")) : JsonRow(_w.Armor, Fields("Armature[*]", "Armature[0]")));
        Assert.Equal(new[] { "Armature[0]", "Armature[1]" }, leaves.Select(l => l.Path).ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheRowsFormListsItsLeavesFlatToo(bool toFile)
    {
        var p = Rows("Effects");
        var leaves = Leaves(toFile ? FileRow(_w.Spell, p) : JsonRow(_w.Spell, p));
        Assert.Contains(leaves, l => l.Path == "Effects[0]");
        Assert.Equal("5", leaves.Single(l => l.Path == "Effects[0].Data.Magnitude").Value);
        Assert.Equal("4", leaves.Single(l => l.Path == "Effects[1].Data.Magnitude").Value);
    }
}

/// <summary>The read's own expansion cut and a max_chars cut share the one '…' entry, so neither overwrites the other.</summary>
[Collection("records")]
[Trait("tier", "integration")]
public sealed class RecordsOneLeafCutTests : RecordsTestBase
{
    public RecordsOneLeafCutTests(RecordsFixture f) : base(f) { }

    [Fact]
    public void AnExpansionCutAndAMaxCharsCutAreOneEntry()
    {
        var doc = Je(RecordsTools.Records(Svc, formids: new[] { Fid(W.BigList) }, format: "json",
                                          project: Fields("Items[*]"), max_chars: 3000));
        var cuts = doc.GetProperty("records")[0].GetProperty("fields").EnumerateArray()
                      .Where(e => e.GetProperty("path").GetString() == "…").ToList();
        var note = Assert.Single(cuts).GetProperty("note").GetString();
        Assert.Contains("expansion truncated", note);
        Assert.Contains("truncated at max_chars", note);
    }
}
