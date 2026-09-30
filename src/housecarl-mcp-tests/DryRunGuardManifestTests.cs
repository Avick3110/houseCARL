using HousecarlMcp;
using Xunit;
using static HousecarlMcpTests.DryRunGuardWorld;

namespace HousecarlMcpTests;

/// <summary>
/// The ops manifest on disk, <c>ops="@&lt;path&gt;"</c> (the former <c>dry-run-guard</c> probe, arm L): the same
/// pipeline as inline ops, a real manifest write that lands, and each file-contract refusal named with nothing written.
/// </summary>
[Trait("tier", "integration")]
public sealed class DryRunGuardManifestTests : IDisposable
{
    readonly DryRunGuardWorld _w = new();

    // The file lane's ops value, spelled by ApplyGuardWorld's helper.
    static System.Text.Json.JsonElement At(string path) => Json(ApplyGuardWorld.AtPath(path));

    string OpsJson(string value = "73") =>
        $"[{{\"formid\": \"{_w.Fid}\", \"field_path\": \"BasicStats.Damage\", \"op\": \"Set\", \"value\": \"{value}\"}}]";

    // L: manifest + dry_run renders IDENTICALLY to the same ops inline; the manifest dry run wrote nothing.
    [Fact]
    public void AManifestDryRunRendersIdenticallyToTheSameOpsInlineAndWritesNothing()
    {
        var manifest = _w.Manifest("ops-manifest.json", OpsJson());
        var before = _w.Snapshot();

        var dryFile = ApplyTools.Apply(_w.Svc, ops: At(manifest), patch: "DryL", dry_run: true);
        var dryInline = ApplyTools.Apply(_w.Svc, ops: Json(OpsJson()), patch: "DryL", dry_run: true);

        Assert.StartsWith(WriteSentences.DryRunHeader, dryFile);
        Assert.Equal(dryInline, dryFile);
        Assert.Equal(before, _w.Snapshot());
    }

    // L: a real manifest write lands (a new DryL mod folder + the wrote confirmation).
    [Fact]
    public void ARealManifestWriteLands()
    {
        var manifest = _w.Manifest("ops-manifest.json", OpsJson());
        var before = _w.ModFolders();

        var real = ApplyTools.Apply(_w.Svc, ops: At(manifest), patch: "DryL");

        Assert.Contains("wrote DryL.esp", real);
        Assert.Contains(_w.ModFolders().Except(before), f => f.Contains("DryL"));
    }

    // L: a call naming no ops at all refuses named.
    [Fact]
    public void ACallNamingNoOpsRefusesNamed() =>
        AssertRefusedNamedAndNothingWritten(() => ApplyTools.Apply(_w.Svc), "ops");

    // L: an explicit empty INLINE array keeps its existing refusal; it names the parameter, not a file.
    [Fact]
    public void AnInlineEmptyArrayNamesTheParameterNotAFile()
    {
        var r = AssertRefusedNamedAndNothingWritten(() => ApplyTools.Apply(_w.Svc, ops: Json("[]")), "ops is an empty array");
        Assert.DoesNotContain("the file named by", r);
    }

    // L: a blank @path refuses NAMED, never silently reinterpreted as absent.
    [Fact]
    public void ABlankAtPathRefusesNamed()
    {
        var r = AssertRefusedNamedAndNothingWritten(() => ApplyTools.Apply(_w.Svc, ops: At("   ")), "names no file");
        Assert.Contains("ops:", r);
    }

    // L: a relative path refuses.
    [Fact]
    public void ARelativeAtPathRefuses() =>
        AssertRefusedNamedAndNothingWritten(() => ApplyTools.Apply(_w.Svc, ops: Json("\"@ops.json\"")), "is not an absolute path");

    // L: an unreadable file refuses naming the path.
    [Fact]
    public void AnUnreadableManifestRefusesNamingThePath()
    {
        var r = AssertRefusedNamedAndNothingWritten(
            () => ApplyTools.Apply(_w.Svc, ops: At(Path.Combine(_w.ManifestDir, "no-such-manifest.json"))), "could not read");
        Assert.Contains("no-such-manifest.json", r);
    }

    // L: invalid JSON refuses naming the file lane + position + element.
    [Fact]
    public void InvalidManifestJsonRefusesNamingTheFileLanePositionAndElement()
    {
        var bad = _w.Manifest("bad.json", "[{\"formid\": }]");
        var r = AssertRefusedNamedAndNothingWritten(() => ApplyTools.Apply(_w.Svc, ops: At(bad)), "the file named by ops");
        Assert.Contains("line ", r);
        Assert.Contains("$[0].formid", r);
    }

    // L: a non-array root refuses naming the expected shape.
    [Fact]
    public void ANonArrayManifestRootRefusesNamingTheShape()
    {
        var obj = _w.Manifest("obj.json", "{\"operations\": []}");
        AssertRefusedNamedAndNothingWritten(() => ApplyTools.Apply(_w.Svc, ops: At(obj)), "JSON ARRAY");
    }

    // L: an empty manifest array refuses naming the file lane.
    [Fact]
    public void AnEmptyManifestArrayRefusesNamingTheFileLane()
    {
        var empty = _w.Manifest("empty.json", "[]");
        var r = AssertRefusedNamedAndNothingWritten(() => ApplyTools.Apply(_w.Svc, ops: At(empty)), "empty array");
        Assert.Contains("the file named by ops", r);
    }

    // L: a null element refuses naming its index and the file lane.
    [Fact]
    public void ANullManifestElementRefusesNamingItsIndexAndTheFileLane()
    {
        var nullEl = _w.Manifest("nullel.json",
            $"[null, {{\"formid\": \"{_w.Fid}\", \"field_path\": \"BasicStats.Damage\", \"value\": \"1\"}}]");
        var r = AssertRefusedNamedAndNothingWritten(() => ApplyTools.Apply(_w.Svc, ops: At(nullEl)), "[0]");
        Assert.Contains("the file named by ops", r);
    }

    // L: a misspelled op member refuses BY NAME (never the inline binder's silent drop).
    [Fact]
    public void AMisspelledManifestMemberRefusesByName()
    {
        var typo = _w.Manifest("typo.json",
            $"[{{\"formid\": \"{_w.Fid}\", \"feild_path\": \"BasicStats.Damage\", \"value\": \"5\"}}]");
        AssertRefusedNamedAndNothingWritten(() => ApplyTools.Apply(_w.Svc, ops: At(typo)), "feild_path");
    }

    /// <summary>The call refuses ("error:"), carries <paramref name="named"/>, and left the instance unchanged
    /// (L: none of the refusals left anything behind).</summary>
    string AssertRefusedNamedAndNothingWritten(Func<string> call, string named)
    {
        var before = _w.Snapshot();
        var r = call();
        Assert.StartsWith("error:", r);
        Assert.Contains(named, r);
        Assert.Equal(before, _w.Snapshot());
        return r;
    }

    public void Dispose() => _w.Dispose();
}
