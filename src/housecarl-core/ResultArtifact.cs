using System.Text.Json;

namespace HousecarlCore;

/// <summary>The result artifact: one JSONL file whose line 1 is the manifest and lines 2+ are the rows, immutable once written; contract in docs/architecture/output-and-artifacts.md.</summary>
public static class ResultArtifact
{
    /// <summary>The manifest-format version stamped as the <c>housecarl_artifact</c> value; its PRESENCE is what marks a file as an artifact rather than a plain formid list.</summary>
    public const int ManifestVersion = 1;

    /// <summary>Leading characters stripped before sniffing or parsing line 1: a UTF-8 BOM and ordinary indentation.</summary>
    static readonly char[] LineNoise = { '\uFEFF', ' ', '\t' };

    // ---- writing ------------------------------------------------------------------------------------

    /// <summary>Accumulates JSONL rows for one artifact in memory, then <see cref="Save"/> writes manifest and rows into the target's file; no server-side state survives the call.</summary>
    public sealed class Writer : IDisposable
    {
        // Counts as it writes, so a shared row writer measures characters without rescanning the buffer.
        readonly CharCountedStream _rows = new();
        readonly Dictionary<string, int> _typeCounts = new(StringComparer.Ordinal);
        int _rowCount;

        /// <summary>Append one row, newline-terminated, counting <paramref name="type"/> into the manifest; the artifact passes an unreachable cap, because an artifact row is NEVER truncated.</summary>
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

        /// <summary>Write the finished artifact, returning the manifest it stamped or a named error; never throws for an IO failure. <paramref name="total"/> exceeds <see cref="RowCount"/> only when the producing lane windowed.</summary>
        /// <param name="notes">Response-level statements the ROWS depend on for their meaning, stated once here rather than per row.</param>
        /// <param name="epochUncovered">The verdict classes <paramref name="epoch"/> does NOT describe (SPEC §2.1); non-empty stamps <c>epoch_covers_all_inputs: false</c>.</param>
        /// <param name="excludedPlugins">Plugins the build lost to a load failure, from the response's own <c>OrderStamp</c>; non-empty stamps <c>order_degraded: true</c>.</param>
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
                                        // Empty included: null is "no coverage claim", empty is "the stamp covers everything".
                                        epochUncovered,
                                        excludedPlugins is { Count: > 0 } ? excludedPlugins : null);
            target.EnsureUnwritten();   // a target is single-use; writing one twice is a bug, not an IO failure
            try
            {
                // The target decides where the bytes land, and cleans up after itself if this throws.
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

    /// <summary>Line 1 of an artifact, parsed. <see cref="Identity"/> names the column an <c>@file</c> re-entry extracts, or is null for a count table, which re-entry refuses by name.</summary>
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
        /// <summary>Does <see cref="Epoch"/> describe everything these rows were read off? Null when the producing lane made no coverage claim.</summary>
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
            // The §2.1 coverage stamp and degraded-order roster, in JsonWire.WriteSweepEpoch's vocabulary.
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

    /// <summary>Cheap sniff on already-read content: is line 1 a manifest? Anything that fails to parse as one is NOT an artifact, never a crash.</summary>
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

    /// <summary>Parse an artifact's manifest and extract its identity-column tokens in row order, or return a named error; error rows are skipped, not identity-bearing — re-entry contract in docs/architecture/output-and-artifacts.md.</summary>
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
            // Parsed back so a round-trip cannot strip the sentence the rows depend on.
            List<string>? notes = null;
            if (r.TryGetProperty("notes", out var nt) && nt.ValueKind == JsonValueKind.Array)
            {
                notes = new List<string>();
                foreach (var n in nt.EnumerateArray()) if (n.ValueKind == JsonValueKind.String) notes.Add(n.GetString()!);
            }
            // Parsed back too, so a re-read file never claims more coverage than the write did.
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

/// <summary>Where one artifact is written: a caller-named <c>to_file=</c> path, or a RESERVED auto-spill path whose exclusive handle this owns; contract in docs/architecture/output-and-artifacts.md.</summary>
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

    /// <summary>Put the artifact on disk: a reservation writes through the handle that claimed the name, a named target through a same-directory temp moved into place — one path per kind, because the hazards are opposite.</summary>
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
            // Best-effort: a full-size temp is never left behind, and a second failure must not mask the first.
            try { File.Delete(tmp); } catch (Exception) { }
            throw;
        }
    }

    /// <summary>A target is written once; a second write would fail against the reservation's closed stream and take the landed artifact with it.</summary>
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

/// <summary>An epoch obligation carried by an artifact-backed list input: the consuming call must compare <see cref="Epoch"/> against the build it captures, AFTER its own Capture() and inside the service.</summary>
public sealed record ArtifactDemand(string Path, string Epoch);
