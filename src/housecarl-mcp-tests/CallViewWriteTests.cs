using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;
using Xunit.Abstractions;

namespace HousecarlMcpTests;

/// <summary>A write finishes on the view its in-gate pin took: a mod switched off, or a profile switch, after the pin and
/// before the post-write file reads changes neither the carries nor the checks (#992). Each case runs cold (no file
/// view built before the call) and warm (an earlier call built one).</summary>
[Trait("tier", "integration")]
public sealed class CallViewWriteTests : IDisposable
{
    readonly ITestOutputHelper _out;
    readonly ScratchMo2 _mo2 = new("hc-call-view-");
    readonly ModKey _a = new("HcCvA", ModType.Plugin), _b = new("HcCvB", ModType.Plugin);
    readonly FormKey _npcA, _npcB, _topic;
    LoadOrderService? _svc;

    public CallViewWriteTests(ITestOutputHelper output)
    {
        _out = output;
        var a = new SkyrimMod(_a, SkyrimRelease.SkyrimSE);
        var npcA = a.Npcs.AddNew("HcCvNpcA");
        _npcA = npcA.FormKey;
        var questA = new Quest(a.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcCvQuestA", Flags = Quest.Flag.StartGameEnabled };
        a.Quests.Add(questA);
        var voice = a.VoiceTypes.AddNew("HcCvVoice");
        npcA.Voice.SetTo(voice.FormKey);
        var topic = a.DialogTopics.AddNew("HcCvTopic");
        topic.Quest.SetTo(questA.FormKey);
        _topic = topic.FormKey;
        a.BeginWrite.ToPath(_mo2.InMod("AMod", _a)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        var b = new SkyrimMod(_b, SkyrimRelease.SkyrimSE);
        _npcB = b.Npcs.AddNew("HcCvNpcB").FormKey;
        b.BeginWrite.ToPath(_mo2.InMod("BMod", _b)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        // A's face files and .seq ship in a separate loose mod, as "X - Faces" mods do; B's sit beside its plugin.
        Loose("AFace", _npcA);
        Loose("BMod", _npcB);
        Directory.CreateDirectory(Path.Combine(_mo2.ModsDir, "AFace", "SEQ"));
        File.WriteAllBytes(Path.Combine(_mo2.ModsDir, "AFace", "SEQ", "HcCvA.seq"), new byte[] { 0x00, 0x08, 0x00, 0x00 });
        // The voice file the line the create test makes would speak, shipped in a loose mod of its own.
        var fuz = Path.Combine(_mo2.ModsDir, "VFace",
            VoicePath.For(new FormKey(new ModKey("CvCreate", ModType.Plugin), 0x800), "HcCvVoice", "HcCvQuestA", "HcCvTopic", 1, VoiceFile.Fuz));
        Directory.CreateDirectory(Path.GetDirectoryName(fuz)!);
        File.WriteAllBytes(fuz, new byte[] { 0, 1, 2 });
        // The result script the created line binds, compiled into the same loose mod.
        Directory.CreateDirectory(Path.Combine(_mo2.ModsDir, "VFace", "Scripts"));
        File.WriteAllBytes(Path.Combine(_mo2.ModsDir, "VFace", "Scripts", "HcCvScript.pex"), new byte[] { 0, 1, 2 });
        _mo2.Profile(Order(), Plugins(), Modlist(), "Default");
        // Other switches off the loose face and voice mods, so a write that took Other's files would carry or find less.
        _mo2.Profile(Order(), Plugins(), Modlist(aFace: false, vFace: false), "Other");
    }

    void Loose(string mod, FormKey npc)
    {
        foreach (var (_, rel) in FaceGenPath.Both(npc))
        {
            var p = Path.Combine(_mo2.ModsDir, mod, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllBytes(p, new byte[] { 0xFA, 0xCE });
        }
    }

    string Order() => _a.FileName + "\r\n" + _b.FileName + "\r\n";
    string Plugins() => "*" + _a.FileName + "\r\n*" + _b.FileName + "\r\n";
    static string Modlist(bool aFace = true, bool bMod = true, bool aMod = true, bool vFace = true) =>
        (vFace ? "+" : "-") + "VFace\r\n" + (aFace ? "+" : "-") + "AFace\r\n" + (bMod ? "+" : "-") + "BMod\r\n" + (aMod ? "+" : "-") + "AMod\r\n";

    public void Dispose() { _svc?.Dispose(); _mo2.Delete(); }

    int CarriedFaceFiles(string patch)
    {
        var dir = Path.Combine(_mo2.ModsDir, "houseCARL - " + patch);
        return Directory.Exists(dir) ? Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
            .Count(f => f.Contains("FaceGen", StringComparison.OrdinalIgnoreCase)) : -1;
    }

    /// <summary>The change each case lands after the in-gate pin and before the post-write file reads.</summary>
    Action? Change(string kind) => kind switch
    {
        "none" => null,
        "loose-mod-off" => () => _mo2.Profile(Order(), Plugins(), Modlist(aFace: false)),
        "donor-folder-off" => () => _mo2.Profile(Order(), Plugins(), Modlist(bMod: false)),
        "source-folder-off" => () => _mo2.Profile(Order(), Plugins(), Modlist(aMod: false)),
        "voice-mod-off" => () => _mo2.Profile(Order(), Plugins(), Modlist(vFace: false)),
        _ => () => _mo2.SelectProfile("Other"),
    };

    LoadOrderService Open(bool warm)
    {
        _svc = _mo2.Open();
        if (warm) _svc.AssetStatus(new[] { "meshes/x.nif" });   // a previous call built the file view
        return _svc;
    }

    string Merge(string kind, bool warm)
    {
        var svc = Open(warm);
        var patch = "CvMerge";
        svc.WriteArea.AfterCommitForGuard = Change(kind);
        var answer = WriteTools.MergePlugins(svc, new[] { _a.FileName.String, _b.FileName.String }, patch);
        svc.WriteArea.AfterCommitForGuard = null;
        _out.WriteLine($"--- merge {kind} (warm={warm}) carried face files on disk: {CarriedFaceFiles(patch)}");
        _out.WriteLine(answer);
        Assert.Equal(4, CarriedFaceFiles(patch));
        return answer;
    }

    // The merge answers in full off its in-gate pin whatever lands after it: the loose-only case would carry 2 files
    // with no part 2 (cold) or no part 1 (warm), and the plugin-set and profile cases refuse after the commit with Q1 flipped.
    [Theory]
    [InlineData("none", false)]
    [InlineData("none", true)]
    [InlineData("loose-mod-off", false)]
    [InlineData("loose-mod-off", true)]
    [InlineData("donor-folder-off", false)]
    [InlineData("donor-folder-off", true)]
    [InlineData("switch", false)]
    [InlineData("switch", true)]
    public void AMergeCarriesTheControlsFilesWhateverMovesAfterItsPin(string kind, bool warm)
    {
        var answer = Merge(kind, warm);
        Assert.Contains("facegen: copied 4 files for 2 NPCs", answer);
        Assert.Contains("SEQ: regenerated", answer);
        Assert.DoesNotContain("SEQ WARN", answer);
        Assert.DoesNotContain("load order changed", answer);
    }

    string Compact(string kind, bool warm)
    {
        var svc = Open(warm);
        var patch = "CvCompact";
        svc.WriteArea.AfterCommitForGuard = Change(kind);
        var answer = WriteTools.CompactPlugin(svc, _a.FileName.String, patch: patch);
        svc.WriteArea.AfterCommitForGuard = null;
        _out.WriteLine($"--- compact {kind} (warm={warm}) carried face files on disk: {CarriedFaceFiles(patch)}");
        _out.WriteLine(answer);
        Assert.Equal(2, CarriedFaceFiles(patch));
        return answer;
    }

    [Theory]
    [InlineData("none", false)]
    [InlineData("none", true)]
    [InlineData("loose-mod-off", false)]
    [InlineData("loose-mod-off", true)]
    [InlineData("source-folder-off", false)]
    [InlineData("source-folder-off", true)]
    [InlineData("switch", false)]
    [InlineData("switch", true)]
    public void ACompactCarriesTheControlsFilesWhateverMovesAfterItsPin(string kind, bool warm)
    {
        var answer = Compact(kind, warm);
        Assert.Contains("facegen: copied 2 files for 1 NPC", answer);
        Assert.Contains("SEQ: regenerated", answer);
        Assert.DoesNotContain("SEQ WARN", answer);
        Assert.DoesNotContain("load order changed", answer);
    }

    /// <summary>Run <paramref name="body"/> as one tool call, which must be served, and return what it produced.</summary>
    static T InCall<T>(Func<T> body)
    {
        T result = default!;
        Assert.Equal("ok", Guard.Tool("housecarl_probe", _ => { result = body(); return ToolAnswer.Served("ok"); }));
        return result;
    }

    // An in-place apply still judges the .seq its pin's build serves: A's ships in AFace and lists no quest at its FormID.
    [Theory]
    [InlineData("none", false)]
    [InlineData("none", true)]
    [InlineData("loose-mod-off", false)]
    [InlineData("loose-mod-off", true)]
    [InlineData("switch", false)]
    [InlineData("switch", true)]
    public void AnInPlaceApplyJudgesTheSeqItsPinServes(string kind, bool warm)
    {
        var svc = Open(warm);
        svc.WriteArea.AfterCommitForGuard = Change(kind);
        var outcome = InCall(() => svc.ApplyEdits(new[] { new BulkOp { Formid = ScratchMo2.Fid(_npcA), FieldPath = "EditorID", Value = "HcCvNpcEdited" } },
                                                  null, null, target: _a.FileName.String, inPlace: true, acknowledge: true));
        svc.WriteArea.AfterCommitForGuard = null;
        Assert.True(outcome.Success, outcome.Error);
        Assert.Contains("the .seq for 'HcCvA.esp' no longer lists its start-game-enabled quest", outcome.Note);
    }

    // A created line's voice and script checks read the files its pin's build serves: its .fuz and .pex ship in VFace.
    [Theory]
    [InlineData("none", false)]
    [InlineData("none", true)]
    [InlineData("voice-mod-off", false)]
    [InlineData("voice-mod-off", true)]
    [InlineData("switch", false)]
    [InlineData("switch", true)]
    public void ACreatedLinesChecksReadTheFilesItsPinServes(string kind, bool warm)
    {
        var svc = Open(warm);
        svc.WriteArea.AfterCommitForGuard = Change(kind);
        var outcome = InCall(() => svc.CreateRecordsBatch(new[]
        {
            new CreateOp
            {
                RecordType = "DialogResponses", Editorid = "HcCvLine", Parent = ScratchMo2.Fid(_topic),
                Operations = new[]
                {
                    new BulkOp { FieldPath = "Speaker", Value = ScratchMo2.Fid(_npcA) },
                    new BulkOp { FieldPath = "Responses", Verb = "Add", Compose = new StructInput { Type = "DialogResponse", Fields = new() { ["ResponseNumber"] = "1" } } },
                    new BulkOp { FieldPath = "VirtualMachineAdapter.Scripts", Verb = "Add", Compose = new StructInput { Type = "ScriptEntry", Fields = new() { ["Name"] = "HcCvScript" } } },
                },
            },
        }, "CvCreate", null));
        svc.WriteArea.AfterCommitForGuard = null;
        Assert.True(outcome.Success, outcome.Error);
        Assert.Null(outcome.ScriptBinding!.CheckError);
        Assert.Equal(ScriptBindingStatus.BoundAndCompiled, Assert.Single(outcome.ScriptBinding.Findings).Status);
        var line = Assert.Single(outcome.Voice!.Lines);
        Assert.True(line.FuzPresent, line.FuzPath);
        Assert.Null(outcome.Voice.CheckError);
    }
}
