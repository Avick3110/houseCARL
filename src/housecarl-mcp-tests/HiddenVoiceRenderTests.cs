using System.Text.Json;
using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda.Plugins;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A voice line whose .fuz a mod holds only hidden says so in the create and the dialogue sweep, text and
/// json, rather than asking for the audio to be placed (#1281).</summary>
[Trait("tier", "unit")]
public sealed class HiddenVoiceRenderTests
{
    static readonly ModKey Mk = new("HcHvTest", ModType.Plugin);
    static FormKey F(uint id) => new(Mk, id);
    const string Seed = "000800:HcHvTest.esp";
    const string Fuz = @"sound\voice\hchvtest.esp\x\a.fuz";

    static readonly HiddenCopies Hidden =
        new(new HiddenRule(HiddenKind.SkipSuffix, ".mohidden"), Fuz + ".mohidden", new[] { "HidMod" }, Twin: true);

    static readonly VoiceLine Line = new(F(0x802), "HcHvTopic", 1, Fuz, false, null, false, "a.lip", true, false, Hidden);

    static WritePatchBuilder.CreateOutcome Created() =>
        new WritePatchBuilder.CreateOutcome(
            true, null, @"C:\mods\HcHvTest\HcHvTest.esp", false,
            new[] { new WritePatchBuilder.CreatedRecord(F(0x802), "DialogResponses", "HcHvInfo", Array.Empty<WritePatchBuilder.OpResult>()) },
            Array.Empty<string>(), 512)
        { Voice = new VoiceReport(new[] { Line }, Array.Empty<VoiceUndetermined>()), ScriptBinding = ScriptBindingReport.Empty };

    static CheckSweep Sweep()
    {
        var topic = new TopicValidation(F(0x801), "HcHvTopic", "HcHvTest.esp", 1, 0, 0, 1, "Topic", "Custom", "CUST",
            Issues: Array.Empty<DialogueIssue>(), VoiceLines: new[] { Line },
            VoiceUndetermined: Array.Empty<VoiceUndetermined>(), ScriptFindings: Array.Empty<ScriptBindingFinding>());
        var report = new DialogueValidationReport(F(0x800), "quest", "HcHvQuest", "HcHvTest.esp", new[] { topic });
        var result = DialogueSweep.Run(() => new DialogueSweep.Binding(_ => report, _ => F(0x800), CheckErrorsFixtures.Epoch),
                                       new[] { Seed }, 1000, false);
        return new CheckSweep(CheckErrorsFixtures.Sel("dialogue"), Dialogue: result);
    }

    const string Said = "(hidden by MO2 (skipped by MO2's VFS, by the '.mohidden' suffix) in 'HidMod'";

    static void HasFuzHidden(string json)
    {
        static IEnumerable<JsonElement> All(JsonElement e) => e.ValueKind switch
        {
            JsonValueKind.Object => e.EnumerateObject().SelectMany(p => All(p.Value).Prepend(p.Name == "fuz_hidden" ? p.Value : default)),
            JsonValueKind.Array => e.EnumerateArray().SelectMany(All),
            _ => Enumerable.Empty<JsonElement>(),
        };
        var found = All(JsonDocument.Parse(json).RootElement).Where(e => e.ValueKind == JsonValueKind.Object).ToList();
        Assert.True(found.Count > 0, json);
        Assert.All(found, h => Assert.Equal("skip_suffix", h.GetProperty("rule").GetString()));
    }

    [Fact]
    public void TheCreatesTextSaysTheFuzIsHidden()
    {
        var text = WriteTools.RenderCreate(Created(), maxChars: 60000);

        Assert.Contains(Said, text);
        Assert.DoesNotContain("place the audio here", text);
    }

    [Fact]
    public void TheCreatesJsonCarriesFuzHidden() => HasFuzHidden(JsonWire.RenderCreateOutcome(Created(), 60000, false, "patch"));

    [Fact]
    public void TheSweepsTextSaysTheFuzIsHidden()
    {
        var text = CheckTextRender.RenderCheck(Sweep(), 40000);

        Assert.Contains(Said, text);
        Assert.DoesNotContain("place the audio here", text);
    }

    [Fact]
    public void TheSweepsJsonCarriesFuzHidden() => HasFuzHidden(JsonWire.RenderCheck(Sweep(), 40000));
}
