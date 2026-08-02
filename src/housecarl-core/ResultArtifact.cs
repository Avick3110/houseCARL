using System.Text.Json;

namespace HousecarlCore;

/// <summary>The §2.1.1 result artifact (tool-surface 2.0, W1): ONE self-contained file that decouples result size
/// from render size. Line 1 is the MANIFEST (what query produced it, what shape its rows are, and — load-bearing —
/// the EPOCH fingerprint of the build it was read from); lines 2+ are one JSON row per line (JSONL). Immutable once
/// written: the server never appends to or mutates an artifact — a re-run writes a NEW file (or overwrites a
/// caller-named to_file= target wholesale). Line-addressable and greppable by design: the traversal ergonomics are
/// the client's own file tools, inherited, not built.
///
/// <para>Re-entry contract (SPEC §2.1.1): an <c>@&lt;path&gt;</c> list input whose target is an artifact yields its
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

    /// <summary>Accumulates JSONL rows for one artifact, then <see cref="Save"/> writes manifest + rows atomically
    /// (temp file + move). Rows buffer in memory: an artifact is written in one call's scope and even a very large
    /// result (100k+ rows) is tens of MB transiently — no server-side state survives the call (the retired-daemon
    /// posture stays retired; the STATE is the file).</summary>
    public sealed class Writer : IDisposable
    {
        readonly MemoryStream _rows = new();
        readonly Dictionary<string, int> _typeCounts = new(StringComparer.Ordinal);
        int _rowCount;

        /// <summary>Append one row: <paramref name="write"/> emits exactly one JSON value (an object) into the
        /// writer; a newline is appended after it. <paramref name="type"/> — when the row has a record type —
        /// feeds the manifest's per-type counts. The row stream is handed in too so budget-aware row writers
        /// shared with the inline renders can take their (stream, cap) pair — the artifact passes an unreachable
        /// cap, because an artifact row is NEVER truncated (the file being complete is the §2.1.1 contract).</summary>
        public void WriteRow(Action<Utf8JsonWriter, MemoryStream> write, string? type = null)
        {
            using (var w = new Utf8JsonWriter(_rows))   // deliberately NOT indented — one row, one line
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
        /// IO failure (the caller renders it; Q3). <paramref name="total"/> is the TRUE result total; it equals
        /// <see cref="RowCount"/> unless the producing lane deliberately windowed (an auto-spill of an explicit
        /// limit= window writes the window and says so via total &gt; row_count).</summary>
        public (Manifest? Manifest, string? Error) Save(
            string path, string tool, IReadOnlyList<KeyValuePair<string, string>> query, string? identity,
            IReadOnlyList<string> rowSchema, string sort, int total, string epoch)
        {
            var manifest = new Manifest(tool, query, identity, rowSchema, sort, _rowCount, total,
                                        _typeCounts.Count > 0 ? _typeCounts : null, epoch,
                                        DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"));
            string? tmp = null;
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                // Temp-then-move so a crash mid-write never leaves a half artifact that could pass for a whole one
                // (line 1's row_count would not match, but why leave the hazard). Same-directory temp keeps the
                // move atomic on NTFS.
                tmp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    using (var w = new Utf8JsonWriter(fs)) { manifest.WriteTo(w); w.Flush(); }
                    fs.WriteByte((byte)'\n');
                    _rows.Position = 0;
                    _rows.CopyTo(fs);
                }
                File.Move(tmp, path, overwrite: true);
                return (manifest, null);
            }
            catch (Exception ex)
            {
                // The temp is full artifact size and a failed write is likeliest exactly when the volume is tight
                // (PR #306 review) — never leave it behind. Best-effort: the named error below is the contract; a
                // second failure deleting the temp must not mask it.
                if (tmp is not null) try { File.Delete(tmp); } catch (Exception) { }
                return (null, $"could not write the result artifact to '{path}' — {ex.GetType().Name}: {ex.Message}");
            }
        }

        public void Dispose() => _rows.Dispose();
    }

    /// <summary>Line 1 of an artifact, parsed. <see cref="Identity"/> names the row column an <c>@file</c> re-entry
    /// extracts (the §2.1.1 "identity column" — <c>formid</c> on every record lane); null means the rows carry no
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
        string Created)
    {
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
            w.WriteString("created", Created);
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
    /// (never a throw, never a silent partial list — Q3) on: a malformed manifest, a manifest declaring NO identity
    /// column (a count-table artifact has no per-record identity to re-enter with), a row missing the column, or a
    /// row that isn't valid JSON. <paramref name="content"/> is the file's full text (the callers already hold it —
    /// one read, two uses).</summary>
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
        int lineNo = 1;
        foreach (var line in EnumerateLines(content))
        {
            lineNo++;
            if (line.Length == 0) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
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
            return (null, null, $"artifact '{path}' has a manifest but no rows — the producing query matched nothing; there is no list to re-enter with.");
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
                        r.TryGetProperty("created", out var cr) ? cr.GetString() ?? "?" : "?"),
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

/// <summary>An epoch obligation carried by an artifact-backed list input: the artifact at <see cref="Path"/> was
/// captured at <see cref="Epoch"/>, and the consuming call must compare that against the build it actually captures
/// — AFTER its own Capture(), inside the service, so the check and the answer read the same build (one view per
/// call; a tool-layer pre-check would race a freshness rebuild). Mismatch = loud refusal naming both epochs.</summary>
public sealed record ArtifactDemand(string Path, string Epoch);
