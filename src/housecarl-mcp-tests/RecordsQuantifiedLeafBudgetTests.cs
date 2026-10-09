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
        var huge = master.Spells.AddNew(); huge.EditorID = "HcLeafHuge"; HugeSpell = huge.FormKey;
        for (int i = 0; i <= ReadEngine.MaxExpandNodes; i++)
        {
            var e = new Effect(); e.BaseEffect.SetTo(mgef.FormKey); e.Data = new EffectData { Magnitude = i };
            huge.Effects.Add(e);
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

    [Fact]
    public void TheReadNamesTheLeafOnlyWhenNothingReadsTheListWhole()
    {
        var (alone, _) = FieldFolds.Parse(new[] { "Effects[*].BaseEffect", "Effects[*count]" });
        Assert.Equal(new[] { "Effects[*].BaseEffect", "Effects" }, alone!.Read().Paths);
        var (whole, _) = FieldFolds.Parse(new[] { "Effects[*].BaseEffect", "Effects" });
        Assert.Equal(new[] { "Effects" }, whole!.Read().Paths);
    }
}
