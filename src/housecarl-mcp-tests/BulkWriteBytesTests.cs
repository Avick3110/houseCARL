using System.Security.Cryptography;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlGenerator;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// A bulk apply and a bulk forward must write the SAME BYTES they wrote before the per-plugin body gather
/// (<see cref="BodyGather"/>, #723) replaced the per-record fetch. The hashes below were taken from the build
/// before that change, so a difference here means the gather changed the product and not just its cost.
/// </summary>
[Trait("tier", "integration")]
public sealed class BulkWriteBytesTests : IDisposable
{
    const string ApplySha = "BD730E59DEBF0009D30EE216D138283EC599B8ECA7CA2E2EBE487B72CD9B3A1A";
    const string ForwardSha = "0B4805A31399C3DFA2FC4DC328FD0398C7D850C5CBED6EA3BEFA072A7018AA57";

    const int Records = 300;
    const int Ops = 200;

    readonly string _root;
    readonly string _priorCorpus;
    readonly LoadOrderResolver _resolver;
    readonly CorpusRulebook _rulebook;
    readonly List<FormKey> _keys = new();

    public BulkWriteBytesTests()
    {
        _priorCorpus = CorpusRulebook.CorpusPath;
        _root = Path.Combine(Path.GetTempPath(), "hc-bulkbytes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        var masterKey = new ModKey("HcBulkBytesMaster", ModType.Master);
        var master = new SkyrimMod(masterKey, SkyrimRelease.SkyrimSE);
        for (int i = 0; i < Records; i++)
        {
            var w = master.Weapons.AddNew();
            w.EditorID = "HcBulkBytesW" + i;
            w.BasicStats = new WeaponBasicStats { Damage = (ushort)(10 + i), Weight = 1 };
            _keys.Add(w.FormKey);
        }
        var masterFile = Path.Combine(_root, masterKey.FileName.String);
        master.BeginWrite.ToPath(masterFile).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        var genDir = Path.Combine(_root, "corpus-gen");
        CorpusGenerator.GenerateAll(genDir, Path.Combine(_root, "corpus-ref"));
        CorpusRulebook.CorpusPath = Path.Combine(genDir, "corpus.json");

        _resolver = LoadOrderResolver.Build(new[] { masterFile });
        _rulebook = CorpusRulebook.Load();
    }

    static string Sha(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s));
    }

    [Fact]
    public void BulkApplyWritesTheSameBytesAsBefore()
    {
        var edits = _keys.Take(Ops).Select(k => new WritePatchBuilder.PatchEdit
        {
            Target = k, Path = new[] { "BasicStats", "Damage" }, Verb = "Set", Value = "42",
        }).ToList();
        var outPath = Path.Combine(_root, "HcBulkBytesApply.esp");
        var outcome = WritePatchBuilder.Apply(_resolver, _rulebook, edits, outPath, extend: false);
        Assert.True(outcome.Success, outcome.Error);
        var sha = Sha(outPath);
        Assert.True(ApplySha == sha, $"bulk apply wrote different bytes than before the body gather: {sha}");
    }

    [Fact]
    public void BulkForwardWritesTheSameBytesAsBefore()
    {
        var specs = _keys.Take(Ops)
            .Select(k => new WritePatchBuilder.ForwardSpec { Target = k, FromPlugin = "HcBulkBytesMaster.esm" })
            .ToList();
        var outPath = Path.Combine(_root, "HcBulkBytesForward.esp");
        var outcome = WritePatchBuilder.ForwardRecords(_resolver, specs, outPath, extend: false, sourceParam: "source");
        Assert.True(outcome.Success, outcome.Error);
        var sha = Sha(outPath);
        Assert.True(ForwardSha == sha, $"bulk forward wrote different bytes than before the body gather: {sha}");
    }

    public void Dispose()
    {
        CorpusRulebook.CorpusPath = _priorCorpus;
        _resolver.Dispose();
        try { Directory.Delete(_root, true); } catch { /* temp cleanup best-effort */ }
    }
}
