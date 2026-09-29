using Mutagen.Bethesda.Plugins;
using HousecarlCore;
using Xunit;
using static HousecarlMcpTests.NestedCreateRig;

namespace HousecarlMcpTests;

/// <summary>The voice-file check after a dialogue create: the .fuz/.lip path format, a line with no file reported silent
/// at that path, a planted file found, a line with no speaker named undetermined, one path per response number, a
/// speaker chain created in the same call, and a check that fails reported rather than thrown. Migrated from the
/// nested-create-guard probe (VOICE arms).</summary>
[Trait("tier", "integration")]
public sealed class CreatedLineVoiceCheckTests : IDisposable
{
    readonly NestedCreateRig _w = new();

    static WriteRequest Response(string number) => new()
    {
        RecordType = "DialogResponses", Path = new[] { "Responses" }, Verb = "Add",
        Struct = new StructSpec { Type = "DialogResponse", Fields = new() { ["ResponseNumber"] = number } },
    };

    WriteRequest Speaker(string value) => WritePathRig.Req("DialogResponses", "Speaker", "Set", value);

    /// <summary>A Data root under the rig with <paramref name="files"/> planted in it.</summary>
    string DataRoot(params string[] files)
    {
        var dir = Path.Combine(_w.Rig.Root, "data-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        foreach (var rel in files)
        {
            var full = Path.Combine(dir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, new byte[] { 0, 1, 2 });
        }
        return dir;
    }

    static VoiceReport Check(string patch, WritePatchBuilder.CreateOutcome o, LoadOrderResolver order, string dataDir)
    {
        using var assets = AssetResolver.Build("", "", dataDir, Array.Empty<string>(), Array.Empty<ActiveArchive>());
        return VoiceCheck.Run(patch, o.Created, order, assets);
    }

    string MasterVoicePath(FormKey info, VoiceFile kind) => VoicePath.For(info, "HcNcGdVoice", "HcNcGdQuest", "HcNcGdTopic", 1, kind);

    // VOICE-PATH: Quest[..10]_Topic[..15]_00+6hex_ResponseNumber, .fuz and .lip.
    [Fact]
    public void TheVoicePathFollowsTheQuestTopicIdNumberFormat()
    {
        var fk = new FormKey(new ModKey("MyPatch", ModType.Plugin), 0x000ABCu);
        Assert.Equal(@"Sound\Voice\MyPatch.esp\MaleNord\QuestEdito_TopicEditorIDLo_00000ABC_3.fuz",
            VoicePath.For(fk, "MaleNord", "QuestEditorIDLong", "TopicEditorIDLongerThan15", 3, VoiceFile.Fuz));
        Assert.Equal(@"Sound\Voice\MyPatch.esp\MaleNord\QuestEdito_TopicEditorIDLo_00000ABC_3.lip",
            VoicePath.For(fk, "MaleNord", "QuestEditorIDLong", "TopicEditorIDLongerThan15", 3, VoiceFile.Lip));
    }

    // VOICE-SILENT: a voiced line with no .fuz reports one line, not present, at the computed path.
    [Fact]
    public void AVoicedLineWithNoFileIsReportedSilentAtItsPath()
    {
        var (o, path) = _w.Create("HcNcVoiceSilent.esp", Under(_w.Topic.ToString(), "DialogResponses", "HcNcVsInfo", Speaker(_w.Npc.ToString()), Response("1")));
        Assert.True(o.Success, o.Error);
        var report = Check(path, o, _w.Order, DataRoot());
        var line = Assert.Single(report.Lines);
        Assert.False(line.FuzPresent);
        Assert.Equal(MasterVoicePath(o.Created[0].FormKey, VoiceFile.Fuz), line.FuzPath);
        Assert.Equal(1, line.ResponseNumber);
        Assert.Empty(report.Undetermined);
    }

    // VOICE-PRESENT: planting the .fuz and .lip at the computed paths reports both present, winner Data.
    [Fact]
    public void APlantedVoiceFileIsFoundAtThePathTheCheckBuilds()
    {
        var (o, path) = _w.Create("HcNcVoicePresent.esp", Under(_w.Topic.ToString(), "DialogResponses", "HcNcVpInfo", Speaker(_w.Npc.ToString()), Response("1")));
        Assert.True(o.Success, o.Error);
        var fuz = MasterVoicePath(o.Created[0].FormKey, VoiceFile.Fuz);
        var report = Check(path, o, _w.Order, DataRoot(fuz, MasterVoicePath(o.Created[0].FormKey, VoiceFile.Lip)));
        var line = Assert.Single(report.Lines);
        Assert.True(line.FuzPresent);
        Assert.True(line.LipPresent);
        Assert.Equal(fuz, line.FuzPath);
        Assert.Equal("Data", line.FuzWinner);
    }

    // VOICE-NOSPEAKER: a line with no Speaker is one undetermined entry naming Speaker, and no line.
    [Fact]
    public void ALineWithNoSpeakerIsUndeterminedNamingTheSpeaker()
    {
        var (o, path) = _w.Create("HcNcVoiceNoSpeaker.esp", Under(_w.Topic.ToString(), "DialogResponses", "HcNcVnsInfo", Response("1")));
        Assert.True(o.Success, o.Error);
        var report = Check(path, o, _w.Order, DataRoot());
        Assert.Empty(report.Lines);
        var u = Assert.Single(report.Undetermined);
        Assert.Equal(o.Created[0].FormKey, u.Info);
        Assert.Contains("Speaker", u.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // VOICE-MULTIRESP: responses numbered 5 and 2 give two lines whose paths end _5 and _2.
    [Fact]
    public void EachResponseGetsAPathKeyedByItsOwnNumber()
    {
        var (o, path) = _w.Create("HcNcVoiceMulti.esp", Under(_w.Topic.ToString(), "DialogResponses", "HcNcVmInfo", Speaker(_w.Npc.ToString()), Response("5"), Response("2")));
        Assert.True(o.Success, o.Error);
        var report = Check(path, o, _w.Order, DataRoot());
        Assert.Equal(new[] { 2, 5 }, report.Lines.Select(l => l.ResponseNumber).OrderBy(n => n));
        Assert.All(report.Lines, l => Assert.EndsWith($"_{l.ResponseNumber}.fuz", l.FuzPath, StringComparison.Ordinal));
        Assert.Empty(report.Undetermined);
    }

    // VOICE-SAMECALL: a VoiceType and speaker NPC created in the same call resolve the chain from the patch.
    [Fact]
    public void ASpeakerCreatedInTheSameCallResolvesItsVoice()
    {
        var (o, path) = _w.Create("HcNcVoiceSameCall.esp",
            Spec("VoiceType", "HcScVoice"),
            Spec("Npc", "HcScNpc", WritePathRig.Req("Npc", "Voice", "Set", "@HcScVoice")),
            Under(_w.Topic.ToString(), "DialogResponses", "HcScInfo", Speaker("@HcScNpc"), Response("1")));
        Assert.True(o.Success, o.Error);
        var info = o.Created.First(c => c.RecordType == "DialogResponses").FormKey;
        var fuz = VoicePath.For(info, "HcScVoice", "HcNcGdQuest", "HcNcGdTopic", 1, VoiceFile.Fuz);
        var report = Check(path, o, _w.Order, DataRoot(fuz));
        var line = Assert.Single(report.Lines);
        Assert.True(line.FuzPresent);
        Assert.Equal(fuz, line.FuzPath);
        Assert.Empty(report.Undetermined);
    }

    // VOICE-CHECKERROR: a check over a corrupt patch sets CheckError, does not throw, reports no lines.
    [Fact]
    public void AVoiceCheckThatCannotReadThePatchReportsTheError()
    {
        var (o, _) = _w.Create("HcNcVoiceCkErr.esp", Under(_w.Topic.ToString(), "DialogResponses", "HcNcCeInfo", Speaker(_w.Npc.ToString()), Response("1")));
        Assert.True(o.Success, o.Error);
        var corrupt = Path.Combine(_w.Rig.Root, "HcNcVoiceCorrupt.esp");
        File.WriteAllText(corrupt, "this is not a valid Skyrim plugin");
        var report = Check(corrupt, o, _w.Order, DataRoot());
        Assert.NotNull(report.CheckError);
        Assert.Empty(report.Lines);
    }

    public void Dispose() => _w.Dispose();
}
