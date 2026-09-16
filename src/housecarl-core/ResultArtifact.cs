using System.Text.Json;

namespace HousecarlCore;

/// <summary>The result artifact: ONE self-contained file that decouples result size from render size. Line 1 is the
/// MANIFEST (what query produced it, what shape its rows are, and — load-bearing — the EPOCH fingerprint of the
/// build it was read from); lines 2+ are one JSON row per line (JSONL). Immutable once written: the server never
/// appends to or mutates an artifact — a re-run writes a NEW file (or overwrites a caller-named to_file= target
/// wholesale). Line-addressable and greppable by design: the traversal ergonomics are the client's own file tools,
/// inherited, not built.
///
/// <para>Re-entry contract: an <c>@&lt;path&gt;</c> list input whose target is an artifact yields its
/// IDENTITY column as the list — scan once, project forever — and server-side consumption is EPOCH-CHECKED against
/// the current build: mismatch is a loud refusal naming both epochs, with deliberately NO stale-override parameter
/// (fresh re-projection goes through the server; honest-snapshot traversal of the file is the client's own lane,
/// which the server cannot and should not police).</para></summary>
public static class ResultArtifact
{
    /// <summary>The manifest-format version stamped as the <c>housecarl_artifact</c> value — bumped only if line 1's
    /// schema ever changes incompatibly. Its PRESENCE is what marks a file as an artifact (vs a plain formid list).</summary>
    public const int ManifestVersion = 1;

    /// <summary>Leading characters stripped before sniffing/parsing line 1: a UTF-8 BOM (a file round-tripped
    /// through a BOM-writing editor is still the same artifact) and ordinary indentation.</summary>
    static readonly char[] LineNoise = { '\uFEFF', ' ', '\t' };

    // ---- writing ------------------------------------------------------------------------------------

    /// <summary>Accumulates JSONL rows for one artifact, then <see cref="Save"/> writes manifest + rows into the
    /// target's file. Rows buffer in memory: an artifact is written in one call's scope and even a very large
    /// result (100k+ rows) is tens of MB transiently — no server-side state survives the call; the STATE is the
    /// file.</summary>
    public sealed class Writer : IDisposable
    {
        // Counts as it writes, so a row writer sharing the inline renders' (stream, cap) pair measures
        // characters without rescanning a buffer that runs to megabytes.
        readonly CharCountedStream _rows = new();
        readonly Dictionary<string, int> _typeCounts = new(StringComparer.Ordinal);
        int _rowCount;

        /// <summary>Append one row: <paramref name="write"/> emits exactly one JSON value (an object) into the
        /// writer; a newline is appended after it. <paramref name="type"/> — when the row has a record type —
        /// feeds the manifest's per-type counts. The row stream is handed in too so budget-aware row writers
        /// shared with the inline renders can take their (stream, cap) pair — the artifact passes an unreachable
        /// cap, because an artifact row is NEVER truncated: the file must be complete.</summary>
        public void WriteRow(Action<Utf8JsonWriter, CharCountedStream> write, string? type = null)
        {
            using (var w = new Utf8JsonWriter(_rows, JsonTextEncoder.OneLine))   // deliberately NOT indented — one row, one line
            {
                write(w, _rows);
                w.Flush();
            }
            _rows.WriteByte((byte)'\n');
            _rowCount++;
            if (type is not null) _typeCounts[type] = _typeCounts.GetValueOrDefault(type) + 1;
        }

        public int RowCount => _rowCount;

        /// <summary>Write the finished artifact: line 1 = manifest, lines 2+ = the accumulated rows. Returns the
        /// manifest it stamped (echoed into the response's spilled marker) or a named error — never throws for an
        /// IO failure; the caller renders it. <paramref name="total"/> is the TRUE result total; it equals
        /// <see cref="RowCount"/> unless the producing lane deliberately windowed (an auto-spill of an explicit
        /// limit= window writes the window and says so via total &gt; row_count).</summary>
        /// <param name="notes">Response-level statements the ROWS depend on for their meaning — a note a row's own
        /// annotation would otherwise leave unexplained. An artifact is re-entered later with no conversation
        /// attached, so a label that ships without its meaning ships as noise — rows carrying "also declared by X"
        /// need the sentence that says what a child record is. Stated once here rather than per row, which
        /// on a 100k-row artifact would be megabytes of one repeated sentence.</param>
        /// <param name="epochUncovered">The verdict classes the <paramref name="epoch"/> fingerprint does NOT
        /// describe (SPEC §2.1, amended 2026-09-05). Non-empty stamps <c>epoch_covers_all_inputs: false</c> and
        /// names them, rather than leaving the caveat to prose a consumer cannot grep.</param>
        /// <param name="excludedPlugins">Plugins the build lost to a load failure, from the same
        /// <c>OrderStamp</c> the response carries: non-empty stamps <c>order_degraded: true</c> and names them.</param>
        public (Manifest? Manifest, string? Error) Save(
            ArtifactTarget target, string tool, IReadOnlyList<KeyValuePair<string, string>> query, string? identity,
            IReadOnlyList<string> rowSchema, string sort, int total, string epoch,
            IReadOnlyList<string>? notes = null, IReadOnlyList<string>? epochUncovered = null,
            IReadOnlyList<string>? excludedPlugins = null)
        {
            var manifest = new Manifest(tool, query, identity, rowSchema, sort, _rowCount, total,
                                        _typeCounts.Count > 0 ? _typeCounts : null, epoch,
                                        DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
                                        notes is { Count: > 0 } ? notes : null,
                                        // Passed through as given, empty included: null is "this lane makes no
                                        // coverage claim", an EMPTY list is "it makes one and the stamp covers
                                        // everything". Normalizing empty to null would make the true stamp
                                        // unwritable, so no artifact could ever say its epoch covers its rows.
                                        epochUncovered,
                                        excludedPlugins is { Count: > 0 } ? excludedPlugins : null);
            target.EnsureUnwritten();   // a target is single-use; writing one twice is a bug, not an IO failure
            try
            {
                // The target decides where the bytes land — its own reserved handle, or a temp it then moves into
                // place — and cleans up after itself if this throws.
                target.Write(fs =>
                {
                    using (var w = new Utf8JsonWriter(fs, JsonTextEncoder.OneLine)) { manifest.WriteTo(w); w.Flush(); }
                    fs.WriteByte((byte)'\n');
                    _rows.Position = 0;
                    _rows.CopyTo(fs);
                });
                return (manifest, null);
            }
            catch (Exception ex)
            {
                return (null, $"could not write the result artifact to '{target.Path}' — {ex.GetType().Name}: {ex.Message}");
            }
        }

        public void Dispose() => _rows.Dispose();
    }

    /// <summary>Line 1 of an artifact, parsed. <see cref="Identity"/> names the row column an <c>@file</c> re-entry
    /// extracts (the identity column — <c>formid</c> on every record lane); null means the rows carry no
    /// per-record identity (a group_by count table) and re-entry refuses by name. <see cref="Total"/> vs
    /// <see cref="RowCount"/>: equal unless the producing lane windowed (see <see cref="Writer.Save"/>).</summary>
    public sealed record Manifest(
        string Tool,
        IReadOnlyList<KeyValuePair<string, string>> Query,
        string? Identity,
        IReadOnlyList<string> RowSchema,
        string Sort,
        int RowCount,
        int Total,
        IReadOnlyDictionary<string, int>? TypeCounts,
        string Epoch,
        string Created,
        IReadOnlyList<string>? Notes = null,
        IReadOnlyList<string>? EpochUncovered = null,
        IReadOnlyList<string>? ExcludedPlugins = null)
    {
        /// <summary>Does <see cref="Epoch"/> describe everything these rows were read off? False when
        /// <see cref="EpochUncovered"/> names a substrate the record fingerprint says nothing about — the asset
        /// lane's whole row shape, for one. Null when the producing lane made no coverage claim.</summary>
        public bool? EpochCoversAllInputs => EpochUncovered is null ? null : EpochUncovered.Count == 0;

        /// <summary>Did the build these rows came from lose plugins to a load failure (SPEC §2.1)?</summary>
        public bool OrderDegraded => ExcludedPlugins is { Count: > 0 };

        internal void WriteTo(Utf8JsonWriter w)
        {
            w.WriteStartObject();
            w.WriteNumber("housecarl_artifact", ManifestVersion);   // the marker: presence = "this file is an artifact"
            w.WriteString("tool", Tool);
            w.WriteStartObject("query");
            foreach (var kv in Query) w.WriteString(kv.Key, kv.Value);
            w.WriteEndObject();
            if (Identity is null) w.WriteNull("identity"); else w.WriteString("identity", Identity);
            w.WriteStartArray("row_schema");
            foreach (var c in RowSchema) w.WriteStringValue(c);
            w.WriteEndArray();
            w.WriteString("sort", Sort);
            w.WriteNumber("row_count", RowCount);
            w.WriteNumber("total", Total);
            if (TypeCounts is not null)
            {
                w.WriteStartObject("type_counts");
                foreach (var kv in TypeCounts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal))
                    w.WriteNumber(kv.Key, kv.Value);
                w.WriteEndObject();
            }
            w.WriteString("epoch", Epoch);
            // The §2.1 coverage stamp and the degraded-order roster, in the vocabulary the read surface already uses
            // (JsonWire.WriteSweepEpoch). Written only by a lane that passes them: asset_status stamps both today,
            // the record lanes stamp neither yet, so a consumer greps one key where it is written rather than
            // everywhere.
            if (EpochCoversAllInputs is { } covers)
            {
                w.WriteBoolean("epoch_covers_all_inputs", covers);
                if (EpochUncovered is { Count: > 0 })
                {
                    w.WriteStartArray("epoch_uncovered");
                    foreach (var u in EpochUncovered) w.WriteStringValue(u);
                    w.WriteEndArray();
                }
            }
            if (ExcludedPlugins is { Count: > 0 })
            {
                w.WriteBoolean("order_degraded", true);
                w.WriteStartArray("excluded_plugins");
                foreach (var p in ExcludedPlugins) w.WriteStringValue(p);
                w.WriteEndArray();
            }
            w.WriteString("created", Created);
            if (Notes is { Count: > 0 })   // response-level statements the rows' own annotations rely on
            {
                w.WriteStartArray("notes");
                foreach (var n in Notes) w.WriteStringValue(n);
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }
    }

    // ---- reading (the @file re-entry) ---------------------------------------------------------------

    /// <summary>Cheap artifact sniff on a file's already-read content: is the FIRST LINE a manifest? Used by the
    /// list-input readers to route between "plain formid list" and "artifact → identity column". Robust to a
    /// leading BOM; anything that fails to parse as a manifest is NOT an artifact (a plain list whose first entry
    /// happens to start with '{' would fail the marker check, not crash).</summary>
    public static bool LooksLikeArtifact(string content)
    {
        var firstLine = FirstLine(content).TrimStart(LineNoise);
        if (!firstLine.StartsWith("{", StringComparison.Ordinal)) return false;
        try
        {
            using var doc = JsonDocument.Parse(firstLine);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("housecarl_artifact", out _);
        }
        catch (JsonException) { return false; }
    }

    /// <summary>Parse an artifact's manifest + extract its identity-column tokens, in row order. A named error
    /// — never a throw, never a silent partial list — on: a malformed manifest, a manifest declaring NO identity
    /// column (a count-table artifact has no per-record identity to re-enter with), a non-error row missing the
    /// column, or a row that isn't valid JSON. <paramref name="content"/> is the file's full text (the callers
    /// already hold it — one read, two uses).
    /// <para><b>Error rows are not identity-bearing.</b> A row carrying an <c>error</c> member
    /// documents a failure — a malformed input token, an absent record — it does not name a record: resolve/batch
    /// write the caller's RAW token (or the null FormKey) into such rows, so their identity values are legitimately
    /// not FormIDs. Extraction SKIPS them by contract: re-entering an artifact means "the records this file
    /// resolved", and treating a failure row's raw token as a record identity is how the reconciliation subtraction
    /// would go wrong, not right. An all-error artifact refuses by name (nothing resolvable to re-enter). The
    /// was-it-edited refusal below is reserved for genuine mismatches — a SUCCESS row without the identity column —
    /// which a server-written artifact never contains.</para></summary>
    public static (Manifest? Manifest, List<string>? Tokens, string? Error) ReadIdentity(string path, string content)
    {
        Manifest? manifest;
        {
            var (m, err) = ParseManifest(path, FirstLine(content));
            if (err is not null) return (null, null, err);
            manifest = m;
        }
        if (manifest!.Identity is null)
            return (null, null, $"artifact '{path}' (from {manifest.Tool}) declares NO identity column — its rows are " +
                                $"aggregate/count rows, not per-record rows, so there is no formid list to re-enter with. " +
                                $"Re-run the producing query without group_by= (or with to_file=) to get a per-record artifact.");

        var tokens = new List<string>(manifest.RowCount);
        int lineNo = 1, errorRows = 0;
        foreach (var line in EnumerateLines(content))
        {
            lineNo++;
            if (line.Length == 0) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("error", out _)) { errorRows++; continue; }   // not identity-bearing — see the doc
                if (!doc.RootElement.TryGetProperty(manifest.Identity, out var idProp) || idProp.ValueKind != JsonValueKind.String)
                    return (null, null, $"artifact '{path}': row on line {lineNo} carries no '{manifest.Identity}' identity value — " +
                                        $"the file does not match its own manifest (was it edited?). Regenerate it from the producing query.");
                tokens.Add(idProp.GetString()!);
            }
            catch (JsonException ex)
            {
                return (null, null, $"artifact '{path}': line {lineNo} is not a valid JSON row ({ex.Message}) — " +
                                    $"the file does not match its own manifest (was it edited?). Regenerate it from the producing query.");
            }
        }
        if (tokens.Count == 0)
            return (null, null, errorRows > 0
                ? $"artifact '{path}': all {errorRows} row(s) are ERROR rows (failed inputs of the producing call) — nothing resolved, so there is no formid list to re-enter with."
                : $"artifact '{path}' has a manifest but no rows — the producing query matched nothing; there is no list to re-enter with.");
        return (manifest, tokens, null);
    }

    static (Manifest? M, string? Error) ParseManifest(string path, string firstLine)
    {
        try
        {
            using var doc = JsonDocument.Parse(firstLine.TrimStart(LineNoise));
            var r = doc.RootElement;
            var query = new List<KeyValuePair<string, string>>();
            if (r.TryGetProperty("query", out var q) && q.ValueKind == JsonValueKind.Object)
                foreach (var p in q.EnumerateObject()) query.Add(new(p.Name, p.Value.ToString()));
            var schema = new List<string>();
            if (r.TryGetProperty("row_schema", out var rs) && rs.ValueKind == JsonValueKind.Array)
                foreach (var c in rs.EnumerateArray()) schema.Add(c.ToString());
            Dictionary<string, int>? typeCounts = null;
            if (r.TryGetProperty("type_counts", out var tc) && tc.ValueKind == JsonValueKind.Object)
            {
                typeCounts = new(StringComparer.Ordinal);
                foreach (var p in tc.EnumerateObject()) typeCounts[p.Name] = p.Value.GetInt32();
            }
            // Parsed back for the same reason it is written: a note explains what the ROWS mean. A parse that
            // dropped it would let a round-trip quietly strip the sentence the rows depend on.
            List<string>? notes = null;
            if (r.TryGetProperty("notes", out var nt) && nt.ValueKind == JsonValueKind.Array)
            {
                notes = new List<string>();
                foreach (var n in nt.EnumerateArray()) if (n.ValueKind == JsonValueKind.String) notes.Add(n.GetString()!);
            }
            // Parsed back for the same reason the notes are: a coverage caveat and a degraded-order roster that a
            // round-trip dropped would leave the re-read file claiming more than the write did.
            List<string>? uncovered = null;
            if (r.TryGetProperty("epoch_covers_all_inputs", out var cov) && cov.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                uncovered = new List<string>();
                if (r.TryGetProperty("epoch_uncovered", out var un) && un.ValueKind == JsonValueKind.Array)
                    foreach (var u in un.EnumerateArray()) if (u.ValueKind == JsonValueKind.String) uncovered.Add(u.GetString()!);
            }
            List<string>? excluded = null;
            if (r.TryGetProperty("excluded_plugins", out var ex) && ex.ValueKind == JsonValueKind.Array)
            {
                excluded = new List<string>();
                foreach (var p in ex.EnumerateArray()) if (p.ValueKind == JsonValueKind.String) excluded.Add(p.GetString()!);
            }
            return (new Manifest(
                        r.TryGetProperty("tool", out var t) ? t.GetString() ?? "?" : "?",
                        query,
                        r.TryGetProperty("identity", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null,
                        schema,
                        r.TryGetProperty("sort", out var s) ? s.GetString() ?? "?" : "?",
                        r.TryGetProperty("row_count", out var rc) ? rc.GetInt32() : 0,
                        r.TryGetProperty("total", out var tot) ? tot.GetInt32() : 0,
                        typeCounts,
                        r.TryGetProperty("epoch", out var e) ? e.GetString() ?? "?" : "?",
                        r.TryGetProperty("created", out var cr) ? cr.GetString() ?? "?" : "?",
                        notes, uncovered, excluded),
                    null);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            return (null, $"artifact '{path}': line 1 looked like a manifest but did not parse as one ({ex.Message}) — " +
                          $"was the file edited? Regenerate it from the producing query.");
        }
    }

    static string FirstLine(string content)
    {
        int nl = content.IndexOfAny(new[] { '\r', '\n' });
        return nl < 0 ? content : content[..nl];
    }

    static IEnumerable<string> EnumerateLines(string content)
    {
        bool first = true;
        using var reader = new StringReader(content);
        for (string? line = reader.ReadLine(); line is not null; line = reader.ReadLine())
        {
            if (first) { first = false; continue; }   // line 1 = the manifest
            yield return line.Trim();
        }
    }
}

/// <summary>Where one artifact is written: a caller-named <c>to_file=</c> path, or a RESERVED auto-spill path whose
/// exclusive handle this owns. The reservation IS the file — the handle that claimed the name is the handle
/// <see cref="ResultArtifact.Writer.Save"/> writes through, so nothing can take or hold the file in between.
/// Disposing a reservation that was never written closes the handle and deletes the file it owns, best-effort, so a
/// cancelled call strands nothing; a named target owns no handle and needs no disposal, and a failed write there
/// leaves the caller's file exactly as it was. <see cref="Write"/> says why the two kinds land differently.</summary>
public sealed class ArtifactTarget : IDisposable
{
    readonly FileStream? _reserved;
    bool _wrote;

    ArtifactTarget(string path, FileStream? reserved) { Path = path; _reserved = reserved; }

    /// <summary>The file this artifact is written to — what the response names.</summary>
    public string Path { get; }

    /// <summary>A caller-named target: no reservation, written through a temp moved into place.</summary>
    public static ArtifactTarget Named(string path) => new(path, null);

    /// <summary>A reserved target: the open, exclusive handle that holds the name.</summary>
    public static ArtifactTarget Reserved(string path, FileStream held) => new(path, held);

    /// <summary>Put the artifact on disk: <paramref name="writeInto"/> emits the whole file into the stream it is
    /// given. One path per kind of target, because they have opposite hazards.
    /// <para>A RESERVATION is written through the handle that claimed the name: the file is the server's own, it was
    /// empty a moment ago, and a temp-then-move onto it is what #766 was — a replace-move fails while anything holds
    /// the destination without share-delete. A crash mid-write cannot pass a half artifact for a whole one, because
    /// the manifest is line 1 and carries row_count, so a short file fails its own manifest.</para>
    /// <para>A NAMED target is the CALLER's file and may already hold an artifact they still want, so it is written
    /// through a same-directory temp moved into place — atomic on NTFS — and a failure anywhere, or a process kill,
    /// leaves the destination untouched. There is no empty placeholder at the destination for a scanner to hold, so
    /// the move here is not the #766 hazard.</para></summary>
    internal void Write(Action<Stream> writeInto)
    {
        if (_reserved is not null)
        {
            using (_reserved) writeInto(_reserved);
            _wrote = true;
            return;
        }

        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = Path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None)) writeInto(fs);
            File.Move(tmp, Path, overwrite: true);
            _wrote = true;
        }
        catch (Exception)
        {
            // The temp is full artifact size and a failed write is likeliest exactly when the volume is tight, so
            // never leave it behind. Best-effort: a second failure deleting it must not mask the first.
            try { File.Delete(tmp); } catch (Exception) { }
            throw;
        }
    }

    /// <summary>A target is written once: a reservation's handle is closed by the write, so a second one would fail
    /// against a dead stream and take the landed artifact with it.</summary>
    internal void EnsureUnwritten()
    {
        if (_wrote) throw new InvalidOperationException($"the artifact target '{Path}' has already been written");
    }

    public void Dispose()
    {
        if (_reserved is null) return;   // a named target owns no handle and no file of its own
        _reserved.Dispose();
        if (!_wrote) try { File.Delete(Path); } catch (Exception) { }
    }
}

/// <summary>An epoch obligation carried by an artifact-backed list input: the artifact at <see cref="Path"/> was
/// captured at <see cref="Epoch"/>, and the consuming call must compare that against the build it actually captures
/// — AFTER its own Capture(), inside the service, so the check and the answer read the same build (one view per
/// call; a tool-layer pre-check would race a freshness rebuild). Mismatch = loud refusal naming both epochs.</summary>
public sealed record ArtifactDemand(string Path, string Epoch);
