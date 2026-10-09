using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The in-place verify read-back render (HCBR-2026-06-28-01). One <c>WritePatchBuilder.ApplyInPlace</c> over a
/// masterless plugin adds the same keyword to six weapons, which forces the touched-record verify on; that one outcome
/// is then rendered three ways through <c>WriteTools.Render</c>: compact (the default), full at a low cap, and full at
/// the default cap. The compact form covers every record in one line each instead of the deep dump that spilled past
/// the host limit.</summary>
[Trait("tier", "integration")]
public sealed class InPlaceReadbackRenderTests : IDisposable
{
    const int N = 6;
    const string PluginName = "hcCompact.esp";
    readonly string _dir = Path.Combine(Path.GetTempPath(), "hc-inplace-readback-" + Guid.NewGuid().ToString("N"));
    readonly string _path;
    readonly FormKey _kw;
    readonly List<FormKey> _weapons = new();
    readonly WritePatchBuilder.PatchOutcome _outcome;

    public InPlaceReadbackRenderTests()
    {
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, PluginName);
        var mod = new SkyrimMod(ModKey.FromNameAndExtension(PluginName), SkyrimRelease.SkyrimSE);
        var kw = mod.Keywords.AddNew(); kw.EditorID = "hcCompactKwAdded"; _kw = kw.FormKey;
        for (int i = 0; i < N; i++)
        {
            var w = mod.Weapons.AddNew();
            w.EditorID = $"hcCompactW{i}";
            w.Name = $"Compact Weapon {i}";
            w.BasicStats = new WeaponBasicStats { Damage = (ushort)(10 + i) };
            _weapons.Add(w.FormKey);
        }
        mod.BeginWrite.ToPath(_path).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        using var resolver = LoadOrderResolver.Build(new[] { _path });
        var edits = _weapons.Select(fk => new WritePatchBuilder.PatchEdit
        {
            Target = fk, Path = new[] { "Keywords" }, Verb = "Add", Value = _kw.ToString(),
        }).ToArray();
        _outcome = WritePatchBuilder.ApplyInPlace(resolver, TestCorpus.Rulebook, edits, _path, PluginName, fullReadback: true);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* temp cleanup best-effort */ } }

    static int Count(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    // SETUP: in-place apply succeeded; the verify is forced ON (all N touched records read back, none errored);
    // every op captured a 'what landed' descriptor naming the added keyword
    [Fact]
    public void TheInPlaceApplyVerifiesEveryTouchedRecordAndCapturesWhatLanded()
    {
        Assert.True(_outcome.Success, _outcome.Error);
        Assert.NotNull(_outcome.ReadBack);
        Assert.Equal(N, _outcome.ReadBack!.Count);
        Assert.All(_outcome.ReadBack, r => { Assert.Null(r.Error); Assert.NotNull(r.Record); });
        Assert.Equal(N, _outcome.Ops.Count);
        Assert.All(_outcome.Ops, o => Assert.Contains(_kw.ToString(), o.Landed));
    }

    // COMPACT: every one of the N edited records is verified (one 're-read clean' line each), none silently dropped
    [Fact]
    public void TheCompactRenderVerifiesEveryRecordOnItsOwnLine()
        => Assert.Equal(N, Count(WriteTools.Render(_outcome, maxChars: 0, fullDump: false), "re-read clean"));

    // COMPACT: it is NOT the deep field-by-field dump (no full-readback header)
    [Fact]
    public void TheCompactRenderIsNotTheDeepDump()
        => Assert.DoesNotContain("full read-back — the ENTIRE", WriteTools.Render(_outcome, maxChars: 0, fullDump: false));

    // COMPACT: it names what landed (the touched element + new count)
    [Fact]
    public void TheCompactRenderNamesWhatLanded()
    {
        var compact = WriteTools.Render(_outcome, maxChars: 0, fullDump: false);
        Assert.Contains("now ", compact);
        Assert.Contains(_kw.ToString(), compact);
    }

    // COMPACT: the whole response stays small even with N non-trivial records (no default-size spill)
    [Fact]
    public void TheCompactRenderStaysSmall()
        => Assert.True(WriteTools.Render(_outcome, maxChars: 0, fullDump: false).Length < 6_000);

    // CAP: the read-back default cap is well under the host token ceiling (the default-spill regression guard)
    [Fact]
    public void TheReadbackCapIsUnderTheHostCeiling()
    {
        Assert.True(Wire.ReadbackMaxChars < Wire.DefaultMaxChars);
        Assert.True(Wire.ReadbackMaxChars <= 32_000);
    }

    // FULL/low-cap: the deep dump is requested, bounded near the cap, and its truncation is EXPLICIT
    [Fact]
    public void TheFullRenderAtALowCapIsBoundedWithAnExplicitNote()
    {
        var full = WriteTools.Render(_outcome, maxChars: 1_500, fullDump: true);
        Assert.Contains("full read-back — the ENTIRE", full);
        Assert.True(full.Length < 4_000, $"{full.Length} chars");
        Assert.Contains("truncated", full);
    }

    // FULL/default: the deep dump is present, covers all N records, and shows real field content (a never-edited Name)
    [Fact]
    public void TheFullRenderAtTheDefaultCapCoversEveryRecordWithFieldContent()
    {
        var full = WriteTools.Render(_outcome, maxChars: 0, fullDump: true);
        Assert.Contains("full read-back — the ENTIRE", full);
        Assert.Equal(N, Count(full, "editorid="));
        Assert.Contains("Compact Weapon 0", full);
    }

    // GROUND TRUTH: re-opening the written file, all N weapons gained the keyword
    [Fact]
    public void EveryWeaponOnDiskGainedTheKeyword()
    {
        Assert.True(_outcome.Success, _outcome.Error);
        using var ov = SkyrimMod.CreateFromBinaryOverlay(_path, SkyrimRelease.SkyrimSE);
        Assert.All(_weapons, fk => Assert.Contains(ov.Weapons.Single(w => w.FormKey == fk).Keywords!, k => k.FormKey == _kw));
    }
}
