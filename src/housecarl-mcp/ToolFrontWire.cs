namespace HousecarlMcp;

// The transport helpers every tool front shares: the epoch stamp, the char budgets, the format switch and the one refusal render.

static partial class Wire
{
    /// <summary>The text lane's stamp on its own line: <c>epoch=</c> plus the degraded-order clause, or empty when the outcome consulted no build.</summary>
    internal static string EpochLine(OrderStamp? stamp) => stamp is null ? "" : $"\nepoch={stamp.Epoch}{stamp.Clause}";

    /// <summary>The same stamp inline in a head line, after the counts.</summary>
    internal static string EpochInline(OrderStamp? stamp) => stamp is null ? "" : $"  epoch={stamp.Epoch}{stamp.Clause}";

    /// <summary>Server default char budget for one tool response (~20k tokens). A caller raises it per-call via max_chars.</summary>
    public const int DefaultMaxChars = 80_000;

    /// <summary>Default char budget for any write-tool read-back dump, held below <see cref="DefaultMaxChars"/> by the host's per-result ceiling; pinned by <c>CompactReadbackProbe</c>'s 80k-spill guard.</summary>
    public const int ReadbackMaxChars = 24_000;

    /// <summary>How many distinct contested parent hosts a create render names before it says "and N further"; shared with the json twin, which publishes the full count beside the capped list.</summary>
    public const int ContestedHostsShown = 10;

    /// <summary>Parse the shared format= param: null or "text" is text, "json" is json, and anything else is a named error rather than a fall-through to text.</summary>
    public static bool WantsJson(string? format, out string? error)
    {
        error = null;
        var f = format?.Trim();
        if (string.IsNullOrEmpty(f) || f.Equals("text", StringComparison.OrdinalIgnoreCase)) return false;
        if (f.Equals("json", StringComparison.OrdinalIgnoreCase)) return true;
        error = $"error: format='{format}' is not recognized — use 'text' (the default) or 'json'.";
        return false;
    }

    /// <summary>The read surface's refusal prefix, defined once so the text and json lanes agree where the sentence starts.</summary>
    internal const string RefusalPrefix = "error: ";

    /// <summary>The one whole-call refusal render for the read surface: text unchanged, json stripped of the prefix; contract in docs/architecture/records-tool-front.md.</summary>
    internal static string Refuse(bool json, string message, OrderStamp? epoch = null)
    {
        if (!json) return message;
        var bare = message.StartsWith(RefusalPrefix, StringComparison.Ordinal)
            ? message[RefusalPrefix.Length..]
            : message;
        return JsonWire.RenderError(bare, epoch);
    }
}
