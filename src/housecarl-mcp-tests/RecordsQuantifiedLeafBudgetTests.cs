using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>Two spells whose effect lists outgrow the shared expansion budget: one only when each element is read
/// whole, one even when only a leaf is read off each.</summary>
public sealed class QuantifiedLeafWorld : IDisposable
{
    public string Root { get; }
    public LoadOrderService Svc { get; }

    /// <summary>247 effects, the issue's count, each heavy enough that whole elements overrun the budget.</summary>
    public const int LongCount = 247;
    public FormKey LongSpell { get; }

    /// <summary>More effects than the budget has lines, so even one leaf each is cut.</summary>
    public FormKey HugeSpell { get; }

    /// <summary>A topic with <see cref="LongCount"/> responses, each heavy enough that whole bodies overrun the budget.</summary>
    public FormKey LongTopic { get; }

    /// <summary>The long spell as built, for a read that does not go through the tool.</summary>
    public ISpellGetter LongSpellRecord { get; }

    public QuantifiedLeafWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-quantleaf-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(Root, "game", "Data"));
        var key = new ModKey("HcLeafMaster", ModType.Master);
        var master = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        var mgef = master.MagicEffects.AddNew(); mgef.EditorID = "HcLeafMgef";

        var spell = master.Spells.AddNew(); spell.EditorID = "HcLeafLong"; LongSpell = spell.FormKey;
        for (int i = 0; i < LongCount; i++)
        {
            var e = new Effect(); e.BaseEffect.SetTo(mgef.FormKey); e.Data = new EffectData { Magnitude = i };
            for (int c = 0; c < 3; c++) e.Conditions.Add(new ConditionFloat { ComparisonValue = c, Data = new GetLevelConditionData() });
            spell.Effects.Add(e);
        }
        LongSpellRecord = spell;
        var huge = master.Spells.AddNew(); huge.EditorID = "HcLeafHuge"; HugeSpell = huge.FormKey;
        for (int i = 0; i <= ReadEngine.MaxExpandNodes; i++)
        {
            var e = new Effect(); e.BaseEffect.SetTo(mgef.FormKey); e.Data = new EffectData { Magnitude = i };
            huge.Effects.Add(e);
        }

        var topic = master.DialogTopics.AddNew(); topic.EditorID = "HcLeafTopic"; LongTopic = topic.FormKey;
        for (int i = 0; i < LongCount; i++)
        {
            var info = new DialogResponses(master.GetNextFormKey(), SkyrimRelease.SkyrimSE);
            info.Responses.Add(new DialogResponse { Text = "line " + i, ResponseNumber = 1 });
            for (int c = 0; c < 3; c++) info.Conditions.Add(new ConditionFloat { ComparisonValue = c, Data = new GetLevelConditionData() });
            topic.Responses.Add(info);
        }

        var instance = Path.Combine(Root, "inst");
        var modDir = Path.Combine(instance, "mods", "LeafMod");
        Directory.CreateDirectory(modDir);
        master.BeginWrite.ToPath(Path.Combine(modDir, key.FileName.String)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");
        var prof = Path.Combine(instance, "profiles", "Default");
        Directory.CreateDirectory(prof);
        File.WriteAllText(Path.Combine(prof, "loadorder.txt"), "# header\r\n" + key.FileName + "\r\n");
        File.WriteAllText(Path.Combine(prof, "plugins.txt"), "*" + key.FileName + "\r\n");
        File.WriteAllText(Path.Combine(prof, "modlist.txt"), "# header\r\n+LeafMod\r\n");
        Svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(Root, "user.json")));
    }

    public void Dispose()
    {
        Svc.Dispose();
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}

/// <summary>#1146: a path ending in one field under <c>[*]</c> spends the expansion budget on that field alone.</summary>
[Trait("tier", "integration")]
public sealed class RecordsQuantifiedLeafBudgetTests : IClassFixture<QuantifiedLeafWorld>
{
    readonly QuantifiedLeafWorld W;
    public RecordsQuantifiedLeafBudgetTests(QuantifiedLeafWorld w) => W = w;

    string Read(FormKey fk, string path, string format = "text") =>
        RecordsTools.Records(W.Svc, formids: new[] { RecordsWorld.Fid(fk) }, format: format, max_chars: 1_000_000,
                             project: new RecordsTools.RecordsProject { form = "fields", fields = new[] { path } });

    static int CountOf(string s, string needle)
    {
        int n = 0, i = 0;
        while ((i = s.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    [Fact]
    public void EveryElementsLeafComesBackWhenWholeElementsWouldOverrunTheBudget()
    {
        var r = Read(W.LongSpell, "Effects[*].BaseEffect");
        Assert.Equal(QuantifiedLeafWorld.LongCount, CountOf(r, "].BaseEffect = "));
        Assert.Contains($"Effects[{QuantifiedLeafWorld.LongCount - 1}].BaseEffect = ", r);
        Assert.DoesNotContain("expansion truncated", r);
    }

    [Fact]
    public void AStructLeafIsStillOneLinePerElement()
    {
        var r = Read(W.LongSpell, "Effects[*].Data");
        Assert.Equal(QuantifiedLeafWorld.LongCount, CountOf(r, "].Data = "));
        Assert.DoesNotContain("].Data.Magnitude", r);
    }

    [Fact]
    public void ALeafPastTheBudgetStillSaysTheReadWasCut()
    {
        var r = Read(W.HugeSpell, "Effects[*].BaseEffect");
        Assert.Contains("expansion truncated", r);
        Assert.DoesNotContain($"Effects[{ReadEngine.MaxExpandNodes}].BaseEffect", r);
    }

    [Fact]
    public void JsonAndDenseCarryTheSameLeaves()
    {
        var json = JsonDocument.Parse(Read(W.LongSpell, "Effects[*].BaseEffect", "json")).RootElement;
        var paths = json.GetProperty("records")[0].GetProperty("fields").EnumerateArray()
                        .Select(f => f.GetProperty("path").GetString()!).Where(p => p.EndsWith(".BaseEffect")).ToList();
        Assert.Equal(QuantifiedLeafWorld.LongCount, paths.Count);

        var dense = JsonDocument.Parse(Read(W.LongSpell, "Effects[*].BaseEffect", "dense")).RootElement;
        Assert.Equal(QuantifiedLeafWorld.LongCount, dense.GetProperty("rows").GetArrayLength());
    }

    /// <summary>A list leaf is read as the one line its cell renders; opening its conditions, here at depth 6,
    /// would overrun the budget.</summary>
    [Fact]
    public void ALeafIsReadAsTheOneLineItsCellRenders()
    {
        var r = RecordsTools.Records(W.Svc, formids: new[] { RecordsWorld.Fid(W.LongSpell) }, max_chars: 1_000_000,
            project: new RecordsTools.RecordsProject { form = "fields", fields = new[] { "Effects[*].Conditions" }, depth = 6 });
        Assert.Equal(QuantifiedLeafWorld.LongCount, CountOf(r, "].Conditions = "));
        Assert.DoesNotContain("expansion truncated", r);
    }

    /// <summary>The sub-path column reads its own leaves whatever else the call reads off the same list.</summary>
    [Theory]
    [InlineData("Effects", false)]
    [InlineData("Effects[*]", false)]
    [InlineData("Effects[0]", false)]
    [InlineData("Effects", true)]
    [InlineData("Effects[*]", true)]
    [InlineData("Effects[0]", true)]
    public void ASubPathColumnIsTheSameBesideTheWholeList(string sibling, bool siblingFirst)
    {
        const string tail = "Effects[*].BaseEffect.FormKey";
        var alone = Column(Dense(W.LongSpell, tail), tail);
        var beside = Column(siblingFirst ? Dense(W.LongSpell, sibling, tail) : Dense(W.LongSpell, tail, sibling), tail);
        Assert.Equal(QuantifiedLeafWorld.LongCount, alone.Count(c => c.Contains("HcLeafMaster")));
        Assert.Equal(alone, beside);
    }

    /// <summary>The issue's own shape, a topic's responses read whole beside their FormKeys, in either order.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryResponseFormKeyComesBackBesideTheWholeResponses(bool wholeFirst)
    {
        const string tail = "Responses[*].FormKey";
        var dense = wholeFirst ? Dense(W.LongTopic, "Responses[*]", tail) : Dense(W.LongTopic, tail, "Responses[*]");
        Assert.Equal(QuantifiedLeafWorld.LongCount, Column(dense, tail).Count(c => c.Contains("HcLeafMaster")));
    }

    /// <summary>One column's dense cells, by the column's own spelling.</summary>
    static List<string> Column(string dense, string column)
    {
        var root = JsonDocument.Parse(dense).RootElement;
        int at = root.GetProperty("columns").EnumerateArray().Select(c => c.GetString()).ToList().IndexOf(column);
        Assert.True(at >= 0, dense[..Math.Min(dense.Length, 400)]);
        return root.GetProperty("rows").EnumerateArray()
            .Select(r => r[at].ValueKind == JsonValueKind.Null ? "" : r[at].ToString()).Where(c => c.Length > 0).ToList();
    }

    /// <summary>Beside a sub-path column, a whole-element row still leads with the element's own line.</summary>
    [Fact]
    public void AnElementRowLeadsWithTheElementBesideASubPathColumn()
    {
        var r = RecordsTools.Records(W.Svc, formids: new[] { RecordsWorld.Fid(W.LongSpell) }, max_chars: 1_000_000,
            project: new RecordsTools.RecordsProject { form = "fields", fields = new[] { "Effects[*].BaseEffect.FormKey", "Effects[*]" } });
        var row = r.Split('\n').First(l => l.TrimStart().StartsWith("Effects[0] = ", StringComparison.Ordinal));
        Assert.StartsWith("Effects[0] = [Effect]", row.Trim());
    }

    string Dense(FormKey fk, params string[] paths) =>
        RecordsTools.Records(W.Svc, formids: new[] { RecordsWorld.Fid(fk) }, format: "dense", max_chars: 1_000_000,
                             project: new RecordsTools.RecordsProject { form = "fields", fields = paths });

    /// <summary>The engine's own path reader is unchanged: only the fold reads a sub-path off each element.</summary>
    [Fact]
    public void TheEngineStillRefusesAQuantifierInAReadPath()
    {
        var read = ReadEngine.ReadFields(W.LongSpellRecord, new[] { "Effects[*].BaseEffect" }, depth: 3);
        Assert.DoesNotContain(read.Fields, f => f.Path.StartsWith("Effects[0]", StringComparison.Ordinal));
    }
}
