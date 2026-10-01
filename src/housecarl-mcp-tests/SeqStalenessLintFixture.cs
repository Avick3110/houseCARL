using HousecarlCore;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlMcpTests;

/// <summary>Synthetic masters with start-game-enabled quests and planted, omitted and aged <c>.seq</c> files for the
/// SEQ lint tests: Miss has no .seq; Stale has a .seq older than the plugin; Ok has a fresh .seq listing one of its two
/// SGE quests plus a RunOnce quest; OvrM's RunOnce quest is overridden by OvrP, which adds the SGE flag.</summary>
public sealed class SeqStalenessLintFixture : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "hc-seq-staleness-lint-tests-" + Guid.NewGuid().ToString("N"));

    public string OkPath { get; }
    public string OkSeq { get; }
    public FormKey Miss { get; private set; }
    public FormKey Stale { get; private set; }
    public FormKey Ok { get; private set; }
    public FormKey NotListed { get; private set; }
    public FormKey Plain { get; private set; }
    public FormKey Ovr { get; private set; }

    readonly LoadOrderResolver _resolver;
    readonly AssetResolver _assets;

    public SeqStalenessLintFixture()
    {
        var dataDir = Path.Combine(_root, "Data");
        Directory.CreateDirectory(Path.Combine(dataDir, "SEQ"));

        var missPath = Path.Combine(_root, "HcDvSeqMiss.esm");
        WriteMaster(missPath, "HcDvSeqMiss", m => Miss = AddQuest(m, "HcDvSeqMissQ", Quest.Flag.StartGameEnabled));
        var stalePath = Path.Combine(_root, "HcDvSeqStale.esm");
        WriteMaster(stalePath, "HcDvSeqStale", m => Stale = AddQuest(m, "HcDvSeqStaleQ", Quest.Flag.StartGameEnabled));
        OkPath = Path.Combine(_root, "HcDvSeqOk.esm");
        WriteMaster(OkPath, "HcDvSeqOk", m =>
        {
            Ok = AddQuest(m, "HcDvSeqOkQ", Quest.Flag.StartGameEnabled);
            NotListed = AddQuest(m, "HcDvSeqNotListedQ", Quest.Flag.StartGameEnabled);
            Plain = AddQuest(m, "HcDvSeqPlainQ", Quest.Flag.RunOnce);
        });

        var ovrMPath = Path.Combine(_root, "HcDvSeqOvrM.esm");
        WriteMaster(ovrMPath, "HcDvSeqOvrM", m => Ovr = AddQuest(m, "HcDvSeqOvrQ", Quest.Flag.RunOnce));
        var ovrPPath = Path.Combine(_root, "HcDvSeqOvrP.esp");
        using (var mOv = SkyrimMod.CreateFromBinaryOverlay(ovrMPath, SkyrimRelease.SkyrimSE))
        {
            var p = new SkyrimMod(new ModKey("HcDvSeqOvrP", ModType.Plugin), SkyrimRelease.SkyrimSE);
            if (p.ModHeader.Stats.NextFormID < 0x800) p.ModHeader.Stats.NextFormID = 0x800;
            p.Quests.GetOrAddAsOverride(mOv.Quests.First(x => x.FormKey == Ovr)).Flags = Quest.Flag.StartGameEnabled;
            p.BeginWrite.ToPath(ovrPPath).WithLoadOrder(new[] { (ISkyrimModGetter)mOv }).NoNextFormIDProcessing().Write();
        }

        var staleSeq = Path.Combine(dataDir, "SEQ", "HcDvSeqStale.seq");
        File.WriteAllBytes(staleSeq, SeqFile.Serialize(new[] { SeqFile.OnDiskFormIdFromPlugin(stalePath, Stale) }));
        OkSeq = Path.Combine(dataDir, "SEQ", "HcDvSeqOk.seq");
        File.WriteAllBytes(OkSeq, SeqFile.Serialize(new[] { SeqFile.OnDiskFormIdFromPlugin(OkPath, Ok) }));
        File.SetLastWriteTimeUtc(staleSeq, File.GetLastWriteTimeUtc(stalePath).AddHours(-1));
        File.SetLastWriteTimeUtc(OkSeq, File.GetLastWriteTimeUtc(OkPath).AddHours(1));

        _resolver = LoadOrderResolver.Build(new[] { missPath, stalePath, OkPath, ovrMPath, ovrPPath });
        _assets = AssetResolver.Build("", "", dataDir, Array.Empty<string>(), Array.Empty<ActiveArchive>());
    }

    public SeqLintFinding? Lint(FormKey fk) => DialogueValidate.Run(_resolver, _assets, fk).SeqLint;

    static FormKey AddQuest(SkyrimMod m, string edid, Quest.Flag flags)
    {
        var q = m.Quests.AddNew();
        q.EditorID = edid;
        q.Flags = flags;
        return q.FormKey;
    }

    static void WriteMaster(string path, string name, Action<SkyrimMod> build)
    {
        var m = new SkyrimMod(new ModKey(name, ModType.Master), SkyrimRelease.SkyrimSE);
        if (m.ModHeader.Stats.NextFormID < 0x800) m.ModHeader.Stats.NextFormID = 0x800;
        build(m);
        m.BeginWrite.ToPath(path).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).NoNextFormIDProcessing().Write();
    }

    public void Dispose()
    {
        _assets.Dispose();
        _resolver.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
