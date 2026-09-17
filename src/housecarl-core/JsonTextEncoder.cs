using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace HousecarlCore;

/// <summary>The ONE encoder behind every json the server writes; contract in docs/architecture/render-budget.md.</summary>
public static class JsonTextEncoder
{
    /// <summary>The encoder itself, for a writer that needs its own options (a different indentation, say).</summary>
    public static readonly JavaScriptEncoder Encoder = JavaScriptEncoder.Create(UnicodeRanges.All);

    public static readonly JsonWriterOptions OneLine = new() { Encoder = Encoder };
}
