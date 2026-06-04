using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HousecarlGenerator;

/// <summary>
/// First-wave step 3 — emits the slim reference tree the <c>mutagen-reference</c> skill ships:
/// per-type JSON schema blocks (one compact line each — JSONL) sharded by kind, plus
/// <c>index.jsonl</c> mapping every type name + xEdit signature to its {file, line}. This is the
/// SAME catalog <c>corpus.json</c> holds, minus the assembly-qualified type names (the write tool
/// keeps those; Claude never needs them) — so the skill's read-content and the write tool's rulebook
/// physically can't disagree about field names or types: one generator walk, two renderings.
///
/// Lookup pattern the skill follows: grep <c>index.jsonl</c> for the name/signature, take {file, line},
/// then read exactly that one line from the shard. The block is one line, so the read is one line —
/// never whole-load a shard, never answer a schema question from memory (decision #4).
///
/// Terse field keys (n/t/c/w + sparse refs) keep the JSON within ~15% of markdown; the legend lives
/// once in the skill's SKILL.md. Aaron's human-readable view stays <c>corpus.summary.md</c>.
/// </summary>
public static class ReferenceEmitter
{
    public static void Emit(Corpus corpus, string refDir)
    {
        Directory.CreateDirectory(refDir);
        var index = new List<SlimIndex>();

        // Shard by kind; within a shard entries stay name-sorted (corpus.Types is a SortedDictionary and
        // GroupBy preserves source order within each group). One compact JSON line per type — the line
        // number IS the block address, so the index needs only {file, line}, not a range.
        foreach (var grp in corpus.Types.Values.GroupBy(t => ShardFor(t.Kind)).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var sb = new StringBuilder();
            int line = 0;
            foreach (var t in grp)
            {
                line++;
                sb.Append(JsonSerializer.Serialize(ToSlim(t), SlimOpts)).Append('\n');
                index.Add(new SlimIndex { Name = t.Name, Sig = t.Signature, Kind = t.Kind, File = $"references/{grp.Key}", Line = line });
            }
            File.WriteAllText(Path.Combine(refDir, grp.Key), sb.ToString());
        }

        // Index sorted by name: deterministic output + grep-friendly. The signature is one-to-many onto
        // names (GMST -> 4 GameSetting* variants), so a sig grep can legitimately return several rows.
        var isb = new StringBuilder();
        foreach (var ix in index.OrderBy(i => i.Name, StringComparer.Ordinal))
            isb.Append(JsonSerializer.Serialize(ix, SlimOpts)).Append('\n');
        File.WriteAllText(Path.Combine(refDir, "index.jsonl"), isb.ToString());
    }

    static string ShardFor(string kind) => kind switch
    {
        "record" or "header" => "records.jsonl",
        "struct" => "structs.jsonl",
        "arm" => "arms.jsonl",
        "polymorphic-base" => "polymorphic.jsonl",
        "enum" => "enums.jsonl",
        _ => "other.jsonl",
    };

    static SlimType ToSlim(TypeSchema t) => new()
    {
        Name = t.Name,
        Kind = t.Kind,
        Sig = t.Signature,
        Getter = t.GetterInterface,
        Mutable = t.MutableInterface,
        AbstractBase = t.AbstractBase,
        Arms = t.Arms,
        Writable = t.Kind == "enum" ? null : $"{t.WritableCount}/{t.FieldCount}",
        Values = t.EnumValues,
        Fields = (t.Kind == "enum" || t.Fields.Count == 0) ? null : t.Fields.Select(ToSlimField).ToList(),
    };

    static SlimField ToSlimField(FieldSchema f) => new()
    {
        Name = f.Name,
        Type = f.Type,
        Card = f.Cardinality,
        Writable = f.Writable,
        Ref = f.TypeRef,
        Arms = f.Arms,
        Elem = f.ElementType,
        ElemRef = f.ElementTypeRef,
        ElemArms = f.ElementArms,
        Key = f.KeyType,
        Target = f.FormLinkTarget,
        Nullable = f.Nullable ? true : null,
        Identity = f.IsIdentity ? true : null,
    };

    static readonly JsonSerializerOptions SlimOpts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Emit literal angle-brackets and backticks instead of their \uXXXX escapes — these files are read by
        // Claude, not embedded in HTML, so the relaxed encoder is both cleaner and a real token saving
        // (FormLink<X> / List<X> are everywhere). corpus.json keeps default escaping; it's machine-only.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // Slim DTOs: terse keys, null-omitted. Distinct from Schema.cs's TypeSchema/FieldSchema (which carry
    // the assembly-qualified names corpus.json needs) — this is the deliberately-trimmed read view.

    sealed class SlimType
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("kind")] public string Kind { get; set; } = "";
        [JsonPropertyName("sig")] public string? Sig { get; set; }
        [JsonPropertyName("getter")] public string? Getter { get; set; }
        [JsonPropertyName("mutable")] public string? Mutable { get; set; }
        [JsonPropertyName("base")] public string? AbstractBase { get; set; }
        [JsonPropertyName("arms")] public List<string>? Arms { get; set; }
        [JsonPropertyName("writable")] public string? Writable { get; set; }
        [JsonPropertyName("values")] public List<string>? Values { get; set; }
        [JsonPropertyName("fields")] public List<SlimField>? Fields { get; set; }
    }

    sealed class SlimField
    {
        [JsonPropertyName("n")] public string Name { get; set; } = "";
        [JsonPropertyName("t")] public string Type { get; set; } = "";
        [JsonPropertyName("c")] public string Card { get; set; } = "";
        [JsonPropertyName("w")] public bool Writable { get; set; }
        [JsonPropertyName("ref")] public string? Ref { get; set; }
        [JsonPropertyName("arms")] public List<string>? Arms { get; set; }
        [JsonPropertyName("elem")] public string? Elem { get; set; }
        [JsonPropertyName("elemRef")] public string? ElemRef { get; set; }
        [JsonPropertyName("elemArms")] public List<string>? ElemArms { get; set; }
        [JsonPropertyName("key")] public string? Key { get; set; }
        [JsonPropertyName("target")] public string? Target { get; set; }
        [JsonPropertyName("null")] public bool? Nullable { get; set; }
        [JsonPropertyName("id")] public bool? Identity { get; set; }
    }

    sealed class SlimIndex
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("sig")] public string? Sig { get; set; }
        [JsonPropertyName("kind")] public string Kind { get; set; } = "";
        [JsonPropertyName("file")] public string File { get; set; } = "";
        [JsonPropertyName("line")] public int Line { get; set; }
    }
}
