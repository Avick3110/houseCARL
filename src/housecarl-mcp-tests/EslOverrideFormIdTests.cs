using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// ESL FormID handling, delegated to Mutagen and pinned here: a light master holds object IDs 0x800-0xFFF only; an
/// override of a light-master record written through <c>WritePatchBuilder.Apply</c> is stored on disk by master-list
/// index (never 0xFE), decodes back to the master's key, and is recognised as the winner; the light flag lives on the
/// master's own header; and the decoded keys do not depend on the master ordering at write time.
/// </summary>
[Trait("tier", "integration")]
public sealed class EslOverrideFormIdTests : IDisposable
{
    const uint RecId = 0x800;

    readonly string _dir = Path.Combine(Path.GetTempPath(), "hc-esl-formid-" + Guid.NewGuid().ToString("N"));
    readonly string _light;
    readonly FormKey _lightRec;

    public EslOverrideFormIdTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "range"));
        var key = new ModKey("HcEslGuardLight", ModType.Master);
        _light = Path.Combine(_dir, key.FileName.String);
        _lightRec = new FormKey(key, RecId);
        WriteMaster(key, _light, _lightRec, light: true);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* temp cleanup best-effort */ } }

    // Probe arm RANGE: a light master writes records at 0x800 and 0xFFF.
    [Theory]
    [InlineData(0x800u)]
    [InlineData(0xFFFu)]
    public void ALightMasterWritesARecordInsideTheWindow(uint objId) =>
        Assert.Equal("", TryWriteAt($"in{objId:X}", objId, light: true));

    // Probe arm RANGE: below 0x800 is the general lower-range floor.
    [Fact]
    public void ALightMasterRefusesARecordBelow0x800() =>
        Assert.Equal("LowerFormKeyRangeDisallowedException", TryWriteAt("r123", 0x123, light: true));

    // Probe arm RANGE: above 0xFFF is the light-master compaction ceiling.
    [Fact]
    public void ALightMasterRefusesARecordAbove0xFFF() =>
        Assert.Equal("FormIDCompactionOutOfBoundsException", TryWriteAt("r1000", 0x1000, light: true));

    // Probe arm CONTROL: a full master writes 0x1000, so the ceiling is contingent on the light flag.
    [Fact]
    public void AFullMasterWritesARecordAbove0xFFF() =>
        Assert.Equal("", TryWriteAt("ctl1000", 0x1000, light: false));

    // Probe arm LIGHT: the override decodes to the master's own stored key and wins at depth 1 or more.
    [Fact]
    public void AnOverrideOfALightMasterRecordRoundTripsAndWins()
    {
        var patch = ApplyDamage(new[] { _light }, "HcEslGuardLightPatch.esp", (_lightRec, "20"));

        var masterKey = WeapKeys(_light).Single();
        Assert.Equal(_lightRec, masterKey);
        Assert.Equal(masterKey, WeapKeys(patch).Single());
        var (winner, depth) = Winner(new[] { _light, patch }, masterKey);
        Assert.Equal("HcEslGuardLightPatch.esp", winner, ignoreCase: true);
        Assert.True(depth >= 1, $"depth {depth}");
    }

    // Probe arm LIGHT: the override is stored by master-list index (high byte 0x00), not 0xFE; the master stays light.
    [Fact]
    public void AnOverrideOfALightMasterRecordIsStoredByMasterIndex()
    {
        var patch = ApplyDamage(new[] { _light }, "HcEslGuardLightPatch.esp", (_lightRec, "20"));

        Assert.Equal(0x00u, OnDiskWeapIds(patch).Single() >> 24);
        Assert.True(IsSmallMaster(_light));
    }

    // Probe arm FLAG: a full master's override round-trips and encodes the same high byte; only the header flag differs.
    [Fact]
    public void TheLightFlagIsTheMastersHeaderNotTheOnDiskByte()
    {
        var key = new ModKey("HcEslGuardFull", ModType.Master);
        var full = Path.Combine(_dir, key.FileName.String);
        var fullRec = new FormKey(key, RecId);
        WriteMaster(key, full, fullRec, light: false);

        var patch = ApplyDamage(new[] { full }, "HcEslGuardFullPatch.esp", (fullRec, "20"));

        Assert.Equal(fullRec, WeapKeys(patch).Single());
        var (winner, depth) = Winner(new[] { full, patch }, fullRec);
        Assert.Equal("HcEslGuardFullPatch.esp", winner, ignoreCase: true);
        Assert.True(depth >= 1, $"depth {depth}");
        Assert.Equal(0x00u, OnDiskWeapIds(patch).Single() >> 24);
        Assert.False(IsSmallMaster(full));
        Assert.True(IsSmallMaster(_light));
    }

    // Probe arm INDEX: two master orderings decode to the same keys, the light master's on-disk index moves 0 to 1, and
    // no record carries 0xFE.
    [Fact]
    public void TheDecodedKeysDoNotDependOnTheMasterOrdering()
    {
        var key2 = new ModKey("HcEslGuardLight2", ModType.Master);
        var light2 = Path.Combine(_dir, key2.FileName.String);
        var rec2 = new FormKey(key2, RecId);
        WriteMaster(key2, light2, rec2, light: true);
        var expected = new[] { _lightRec, rec2 }.OrderBy(k => k.ToString()).ToList();

        var lightIndex = new List<uint>();
        foreach (var (label, order) in new[] { ("first", new[] { _light, light2 }), ("second", new[] { light2, _light }) })
        {
            var patch = ApplyDamage(order, $"HcEslGuardIdx_{label}.esp", (_lightRec, "21"), (rec2, "22"));

            Assert.Equal(expected, WeapKeys(patch).OrderBy(k => k.ToString()).ToList());
            var (winner, depth) = Winner(order.Append(patch).ToArray(), _lightRec);
            Assert.Equal(Path.GetFileName(patch), winner, ignoreCase: true);
            Assert.True(depth >= 1, $"depth {depth}");
            var ids = OnDiskWeapIds(patch);
            Assert.DoesNotContain(ids, x => (x >> 24) == 0xFE);
            lightIndex.Add(ids[WeapKeys(patch).IndexOf(_lightRec)] >> 24);
        }
        Assert.Equal(new uint[] { 0, 1 }, lightIndex);
    }

    string ApplyDamage(string[] order, string patchName, params (FormKey fk, string dmg)[] edits)
    {
        var patch = Path.Combine(_dir, patchName);
        using var r = LoadOrderResolver.Build(order);
        var o = WritePatchBuilder.Apply(r, TestCorpus.Rulebook,
            edits.Select(e => new WritePatchBuilder.PatchEdit { Target = e.fk, Path = new[] { "BasicStats", "Damage" }, Verb = "Set", Value = e.dmg }).ToArray(),
            patch, extend: false);
        Assert.True(o.Success, o.Error);
        return patch;
    }

    static void WriteMaster(ModKey key, string path, FormKey rec, bool light)
    {
        var m = new SkyrimMod(key, SkyrimRelease.SkyrimSE) { IsSmallMaster = light };
        m.Weapons.Add(new Weapon(rec, SkyrimRelease.SkyrimSE) { EditorID = key.Name + "Weap", BasicStats = new WeaponBasicStats { Damage = 10 } });
        m.BeginWrite.ToPath(path).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).NoNextFormIDProcessing().Write();
    }

    /// <summary>Write a master with one record at <paramref name="objId"/>; "" on success, else the exception's type name.</summary>
    string TryWriteAt(string tag, uint objId, bool light)
    {
        var key = new ModKey($"HcEslGuardRange_{tag}", ModType.Master);
        try { WriteMaster(key, Path.Combine(_dir, "range", key.FileName.String), new FormKey(key, objId), light); return ""; }
        catch (Exception ex) { return ex.GetType().Name; }
    }

    static bool IsSmallMaster(string path)
    {
        using var ov = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
        return ov.IsSmallMaster;
    }

    static List<FormKey> WeapKeys(string path)
    {
        using var ov = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
        return ov.Weapons.Select(w => w.FormKey).ToList();
    }

    static (string? winner, int depth) Winner(IReadOnlyList<string> order, FormKey fk)
    {
        using var r = LoadOrderResolver.Build(order);
        return r.ResolveWinner(fk) is { } w ? (w.WinnerPlugin, w.OverrideDepth) : (null, -1);
    }

    /// <summary>The raw record-header FormIDs of every WEAP in the file, read from the bytes, not through Mutagen.</summary>
    static List<uint> OnDiskWeapIds(string path)
    {
        var buf = File.ReadAllBytes(path);
        var outp = new List<uint>();
        Scan(buf, 24 + (int)BitConverter.ToUInt32(buf, 4), buf.Length, outp);
        return outp;
    }

    static void Scan(byte[] buf, int start, int end, List<uint> outp)
    {
        int p = start;
        while (p + 24 <= end)
        {
            var sig = System.Text.Encoding.ASCII.GetString(buf, p, 4);
            uint size = BitConverter.ToUInt32(buf, p + 4);
            long next;
            if (sig == "GRUP") { next = (long)p + size; Scan(buf, p + 24, (int)Math.Min(next, end), outp); }
            else { if (sig == "WEAP") outp.Add(BitConverter.ToUInt32(buf, p + 12)); next = (long)p + 24 + size; }
            if (next <= p) break;
            p = (int)Math.Min(next, end);
        }
    }
}
