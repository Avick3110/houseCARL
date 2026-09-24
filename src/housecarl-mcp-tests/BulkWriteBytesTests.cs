using System.Security.Cryptography;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// A bulk apply and a bulk forward must write the SAME BYTES they wrote before the per-plugin body gather
/// (<see cref="BodyGather"/>, #723) replaced the per-record fetch. The hashes below were taken from the build
/// before that change.
///
/// <para>WHAT A FAILURE MEANS. These digests are the whole serialized plugin, so they pin Mutagen's output format
/// and every later decision about how a patch's header is stamped, not only the gather. Read a difference as "the
/// bytes moved", then find out which: <see cref="BodyGatherEquivalenceTests"/> asserts the gather's own claim (a
/// gathered body is the body the single fetch returns) without depending on the format, so if IT passes and this
/// fails, the cause is downstream of the gather — a library bump, a header change — and the digest below is what
/// needs re-recording. The failure message prints the digest this build produced, which is the new value.</para>
/// </summary>
[Trait("tier", "integration")]
public sealed class BulkWriteBytesTests : IDisposable
{
    const string ApplySha = "BD730E59DEBF0009D30EE216D138283EC599B8ECA7CA2E2EBE487B72CD9B3A1A";
    const string ForwardSha = "0B4805A31399C3DFA2FC4DC328FD0398C7D850C5CBED6EA3BEFA072A7018AA57";
    // The create arm (#757) was recorded from a build with the parent gather's Gather() call removed, which is the
    // pre-change path exactly: Body falls back to the per-record fetch for everything it does not hold.
    const string CreateSha = "4BDCC433E3F11B8683ACE73AA19BC24C34CB8AE529F65E7DCF2D2C66FE0B79EE";

    const int Records = 300;
    const int Ops = 200;

    readonly string _root;
    readonly LoadOrderResolver _resolver;
    readonly CorpusRulebook _rulebook;
    readonly List<FormKey> _keys = new();
    readonly List<FormKey> _topicKeys = new();

    public BulkWriteBytesTests()
    {
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
        // MUST stay AFTER the weapon loop: FormKeys are handed out in creation order, so adding records before it
        // shifts all 300 weapon keys and moves the apply and forward bytes — two unrelated pins failing with a
        // message about the serializer.
        for (int i = 0; i < Ops; i++)
        {
            var t = master.DialogTopics.AddNew();
            t.EditorID = "HcBulkBytesT" + i;
            _topicKeys.Add(t.FormKey);
        }
        var masterFile = Path.Combine(_root, masterKey.FileName.String);
        master.BeginWrite.ToPath(masterFile).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        _resolver = LoadOrderResolver.Build(new[] { masterFile });
        _rulebook = TestCorpus.Rulebook;
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
        Assert.True(ApplySha == sha, $"a bulk apply's written bytes moved (see the class comment for how to tell the gather from the serializer); this build produced {sha}");
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
        Assert.True(ForwardSha == sha, $"a bulk forward's written bytes moved (see the class comment for how to tell the gather from the serializer); this build produced {sha}");
    }

    [Fact]
    public void BulkCreateWritesTheSameBytesAsBefore()
    {
        var specs = Enumerable.Range(0, Ops).Select(i => new WritePatchBuilder.CreateSpec
        {
            RecordType = "DialogResponses",
            EditorId = "HcBulkBytesInfo" + i,
            ParentRef = _topicKeys[i].ToString(),
            Edits = Array.Empty<WriteRequest>(),
        }).ToList();
        var outPath = Path.Combine(_root, "HcBulkBytesCreate.esp");
        var outcome = WritePatchBuilder.CreateRecords(_resolver, _rulebook, specs, outPath, extend: false);
        Assert.True(outcome.Success, outcome.Error);
        var sha = Sha(outPath);
        Assert.True(CreateSha == sha, $"a bulk create's written bytes moved (see the class comment for how to tell the gather from the serializer); this build produced {sha}");
    }

    public void Dispose()
    {
        _resolver.Dispose();
        try { Directory.Delete(_root, true); } catch { /* temp cleanup best-effort */ }
    }
}
