using System.Text.Json;
using HousecarlMcp;
using Mutagen.Bethesda.Plugins;
using Xunit;
using static HousecarlMcpTests.WriteSurfaceReads;

namespace HousecarlMcpTests;

/// <summary>forward source= from a plugin the active order does not contain: a disabled mod's version is reachable, the
/// render states which copy on disk was read, and the refusals stay named. Migrated from write-surface-guard's off-order
/// arm and its PR #313 review-fold arm; the in-place half is <see cref="WriteSurfaceOffOrderInPlaceTests"/>.</summary>
[Trait("tier", "integration")]
public sealed class WriteSurfaceOffOrderForwardTests : IClassFixture<WriteSurfaceWorld>
{
    readonly WriteSurfaceWorld _w;
    public WriteSurfaceOffOrderForwardTests(WriteSurfaceWorld w) => _w = w;

    static JsonElement Root(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    // probe: a source= on disk but NOT in the load order resolves: the DISABLED mod's version lands
    [Fact]
    public void DisabledModsVersionLands()
    {
        var r = ForwardTools.Forward(_w.Svc, formids: new[] { _w.SubjectFid }, source: _w.OffName, patch: "W2Off1");
        var path = _w.ArtifactPathFrom(r);
        Assert.NotNull(path);
        Assert.Equal((ushort)42, DamageIn(path!, _w.SubjectKey));
    }

    // probe: the render states the off-order read: the source is named NOT active, with the exact file and its layer
    // probe: the render says the epoch does NOT cover the off-order file (the stamp fingerprints the ACTIVE order)
    [Fact]
    public void RenderStatesTheOffOrderReadAndTheEpochGap()
    {
        var r = ForwardTools.Forward(_w.Svc, formids: new[] { _w.SubjectFid }, source: _w.OffName, patch: "W2OffR");
        Assert.Contains("read OFF-ORDER from", r);
        Assert.Contains(_w.OffPath, r, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(_w.OffFolder, r, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("outside of", r);
        Assert.Contains("epoch", r, StringComparison.OrdinalIgnoreCase);
    }

    // probe: the off-order overlay is RELEASED after the write — the source file is still movable (no handle at rest)
    [Fact]
    public void OffOrderOverlayIsReleased()
    {
        var r = ForwardTools.Forward(_w.Svc, formids: new[] { _w.SubjectFid }, source: _w.OffName, patch: "W2OffRel");
        Assert.NotNull(_w.ArtifactPathFrom(r));   // a movable file proves nothing unless the read happened
        var parked = _w.OffPath + ".parked";
        File.Move(_w.OffPath, parked);
        File.Move(parked, _w.OffPath);
    }

    // probe: format=json: source_in_order=false + source_read names the file, the layer, and epoch_covers_source=false
    [Fact]
    public void JsonStatesTheOffOrderRead()
    {
        var d = Root(ForwardTools.Forward(_w.Svc, formids: new[] { _w.SubjectFid }, source: _w.OffName, patch: "W2Off2", format: "json"));
        Assert.False(d.GetProperty("source_in_order").GetBoolean());
        var read = d.GetProperty("source_read");
        Assert.Equal(_w.OffPath, read.GetProperty("path").GetString(), ignoreCase: true);
        Assert.False(string.IsNullOrEmpty(read.GetProperty("where").GetString()));
        Assert.False(read.GetProperty("epoch_covers_source").GetBoolean());
    }

    // probe: format=json: an ACTIVE source says source_in_order=true and emits no source_read
    [Fact]
    public void JsonActiveSourceHasNoSourceRead()
    {
        var d = Root(ForwardTools.Forward(_w.Svc, formids: new[] { _w.SubjectFid }, source: _w.MasterName, patch: "W2Off3", format: "json"));
        Assert.True(d.GetProperty("source_in_order").GetBoolean());
        Assert.False(d.TryGetProperty("source_read", out _));
    }

    // probe: a record ORIGINATING in the off-order plugin is refused by name (the patch can't master an inactive plugin)
    // probe: the exemption is narrow: an origin that is NOT the artifact being written is still refused by name
    [Fact]
    public void RecordOriginatingOffOrderIsRefused()
    {
        var r = ForwardTools.Forward(_w.Svc, formids: new[] { _w.OffOwnFid }, source: _w.OffName, patch: "W2OffOrigin");
        Assert.StartsWith("error:", r);
        Assert.Contains(_w.OffName, r, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ORIGINATES", r);
        Assert.Contains("as a master", r);
    }

    // probe: an AMBIGUOUS off-order filename is refused naming every folder that provides it, never a guess
    // probe: the ambiguity refusal offers the PATH, never a mod= this tool does not have
    [Fact]
    public void AmbiguousFilenameIsRefusedNamingBothFolders()
    {
        var r = ForwardTools.Forward(_w.Svc, formids: new[] { _w.SubjectFid }, source: _w.AmbName, patch: "W2OffAmb");
        Assert.StartsWith("error:", r);
        Assert.Contains("W2AmbA", r, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("W2AmbB", r, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("mod=", r);
        Assert.Contains("path", r, StringComparison.OrdinalIgnoreCase);
    }

    // probe: the remedy works: a full PATH picks one copy and forwards it (the disambiguator this tool exposes)
    [Fact]
    public void FullPathPicksOneCopy()
    {
        var r = ForwardTools.Forward(_w.Svc, formids: new[] { _w.SubjectFid },
            source: Path.Combine(_w.ModsDir, "W2AmbA", _w.AmbName), patch: "W2OffByPath");
        var path = _w.ArtifactPathFrom(r);
        Assert.NotNull(path);
        Assert.Equal((ushort)1, DamageIn(path!, _w.SubjectKey));
    }

    // probe: an off-order file that does NOT define a named record is refused naming the file and the record
    [Fact]
    public void OffOrderFileNotDefiningTheRecordIsRefused()
    {
        var r = ForwardTools.Forward(_w.Svc, formids: new[] { _w.OffOwnFid },
            source: Path.Combine(_w.ModsDir, "W2AmbA", _w.AmbName), patch: "W2OffMiss");
        Assert.StartsWith("error:", r);
        Assert.Contains(_w.AmbName, r, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does NOT define or override", r);
        Assert.Contains(_w.OffOwnFid.Split(':')[0], r, StringComparison.OrdinalIgnoreCase);
    }

    // probe: dry_run over an off-order source: nothing written, and the off-order read is still disclosed
    [Fact]
    public void DryRunOffOrderWritesNothingAndDiscloses()
    {
        int before = Directory.GetDirectories(_w.ModsDir).Length;
        var r = ForwardTools.Forward(_w.Svc, formids: new[] { _w.SubjectFid }, source: _w.OffName, patch: "W2OffDry", dry_run: true);
        Assert.StartsWith("DRY RUN", r);
        Assert.Contains("read OFF-ORDER from", r);
        Assert.Equal(before, Directory.GetDirectories(_w.ModsDir).Length);
    }

    // probe: self-forward is caught by FILE IDENTITY when source= is a path to the artifact being written
    [Fact]
    public void SelfForwardByPathIsCaughtByFileIdentity()
    {
        var made = ForwardTools.Forward(_w.Svc, formids: new[] { _w.SubjectFid }, source: _w.MasterName, patch: "W2OffSelf");
        var madePath = _w.ArtifactPathFrom(made);
        Assert.NotNull(madePath);
        var r = ForwardTools.Forward(_w.Svc, formids: new[] { _w.SubjectFid }, source: madePath, into: Path.GetFileName(madePath));
        Assert.StartsWith("error:", r);
        Assert.Contains("is the output patch itself", r);
    }

    // probe: a PATH naming the ACTIVE copy is NOT reported off-order (no false 'not in the load order')
    // probe: …and it keeps the already-the-winner flag instead of claiming the record out-ranks ITSELF
    [Fact]
    public void PathToTheActiveCopyIsInOrder()
    {
        var r = ForwardTools.Forward(_w.Svc, formids: new[] { _w.SubjectFid }, source: _w.ReplacerPath, patch: "W2Live");
        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), r);
        Assert.DoesNotContain("read OFF-ORDER from", r);
        Assert.Contains("already the load-order winner", r, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("out-ranks the current winner", r);
    }

    // probe: format=json: the same path says source_in_order=true and emits no source_read (the epoch DOES cover it)
    [Fact]
    public void JsonPathToTheActiveCopyIsInOrder()
    {
        var d = Root(ForwardTools.Forward(_w.Svc, formids: new[] { _w.SubjectFid }, source: _w.ReplacerPath, patch: "W2Live2", format: "json"));
        Assert.True(d.GetProperty("source_in_order").GetBoolean());
        Assert.False(d.TryGetProperty("source_read", out _));
    }

    // probe: a path to a DIFFERENT file sharing the active plugin's NAME still reads OFF-ORDER (identity, not name)
    [Fact]
    public void SameNamedDifferentFileReadsOffOrder()
    {
        var shadow = Path.Combine(_w.Root, "shadow");
        Directory.CreateDirectory(shadow);
        var copy = Path.Combine(shadow, _w.ReplacerName);
        File.Copy(_w.ReplacerPath, copy, overwrite: true);
        var r = ForwardTools.Forward(_w.Svc, formids: new[] { _w.SubjectFid }, source: copy, patch: "W2Shadow");
        Assert.Contains("read OFF-ORDER from", r);
        Assert.Contains(copy, r, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A weapon created into a fresh patch at damage 5, a copy parked off-order under the same filename, and
    /// the live patch moved on to 77 — so forwarding the parked body back is a visible revert.</summary>
    (string ownPath, string ownFid, string parked) ParkedOwnRecord(string stem)
    {
        var created = CreateTools.Create(_w.Svc, patch: stem,
            records: Json("""[{"record_type":"Weapon","editorid":"W2OwnSubject","ops":[{"field_path":"BasicStats.Damage","value":"5"}]}]"""));
        var ownPath = _w.ArtifactPathFrom(created);
        var ownFid = FormIdsFrom(created).FirstOrDefault();
        Assert.NotNull(ownPath);
        Assert.NotNull(ownFid);
        var parkDir = Path.Combine(_w.Root, "own-old-" + stem);
        Directory.CreateDirectory(parkDir);
        var parked = Path.Combine(parkDir, Path.GetFileName(ownPath!));
        File.Copy(ownPath!, parked, overwrite: true);
        BumpDamage(ownPath!, 77);
        return (ownPath!, ownFid!, parked);
    }

    // probe: a RENAMED off-order copy is refused with the real cause (records are keyed by filename), not bare absence
    [Fact]
    public void RenamedOffOrderCopyIsRefusedWithTheCause()
    {
        var (ownPath, ownFid, parked) = ParkedOwnRecord("W2OwnRen");
        var misnamed = Path.Combine(Path.GetDirectoryName(parked)!, "old-" + Path.GetFileName(ownPath));
        File.Copy(parked, misnamed, overwrite: true);
        var r = ForwardTools.Forward(_w.Svc, formids: new[] { ownFid }, source: misnamed, into: Path.GetFileName(ownPath));
        Assert.StartsWith("error:", r);
        Assert.Contains("keyed by its FILENAME", r);
        Assert.Contains("DOES carry", r);
    }

    // probe: a record ORIGINATING in the artifact being written forwards fine (a plugin is never its own master)
    // probe: …and the written header does NOT list the artifact as its own master
    // probe: a record NO active plugin defines says so, instead of out-ranking a winner called '(none)'
    [Fact]
    public void SelfOriginatedRecordForwardsBackWithoutSelfMaster()
    {
        var (ownPath, ownFid, parked) = ParkedOwnRecord("W2OwnBack");
        var r = ForwardTools.Forward(_w.Svc, formids: new[] { ownFid }, source: parked, into: Path.GetFileName(ownPath));
        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), r);
        Assert.Equal((ushort)5, DamageIn(ownPath, FormKey.Factory(ownFid)));
        Assert.DoesNotContain(Path.GetFileName(ownPath), MastersLineOf(r), StringComparison.OrdinalIgnoreCase);
        var row = r.Split('\n').FirstOrDefault(l => l.Contains(ownFid, StringComparison.OrdinalIgnoreCase)) ?? "";
        Assert.Contains("no active plugin currently defines this record", row);
        Assert.DoesNotContain("out-ranks the current winner", row);
    }

    // probe: format=json: prior_winner is null there, not the string '(none)'
    [Fact]
    public void JsonPriorWinnerIsNullWhenNothingDefinesIt()
    {
        var (ownPath, ownFid, parked) = ParkedOwnRecord("W2OwnJson");
        var d = Root(ForwardTools.Forward(_w.Svc, formids: new[] { ownFid }, source: parked, into: Path.GetFileName(ownPath), format: "json"));
        Assert.Equal(JsonValueKind.Null, d.GetProperty("forwarded")[0].GetProperty("prior_winner").ValueKind);
    }
}

/// <summary>The in-place lane takes an off-order source too; its own world because it rewrites the replacer.</summary>
[Trait("tier", "integration")]
public sealed class WriteSurfaceOffOrderInPlaceTests : IDisposable
{
    readonly WriteSurfaceWorld _w = new();
    public void Dispose() => _w.Dispose();

    // probe: in_place: an OFF-ORDER source forwards into the ACTIVE target's own file (LANE is uniform)
    // probe: in_place: the off-order disclosure rides the in-place render too
    [Fact]
    public void OffOrderSourceForwardsInPlace()
    {
        var r = ForwardTools.Forward(_w.Svc, formids: new[] { _w.SubjectFid }, source: _w.OffName,
            in_place: _w.ReplacerName, acknowledge: true);
        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), r);
        Assert.Equal((ushort)42, DamageIn(_w.ReplacerPath, _w.SubjectKey));
        Assert.Contains("read OFF-ORDER from", r);
    }
}
