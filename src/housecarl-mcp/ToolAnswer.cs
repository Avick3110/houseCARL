using HousecarlCore;
namespace HousecarlMcp;

/// <summary>What a tool body returns: an answer served or refused, which <see cref="Guard"/> renders once — the
/// document as the call's text, a refusal as <c>isError</c>; contract in docs/architecture/typed-answer.md.</summary>
/// <remarks>No conversion from <see cref="string"/>: a body says which one it is, so a refusal cannot go out as a served answer.</remarks>
internal sealed class ToolAnswer
{
    readonly string? _document;
    readonly string? _message;
    readonly bool _json;
    readonly OrderStamp? _epoch;
    readonly bool _epochLine;

    /// <summary>True when the call was refused; the result then carries <c>isError</c>.</summary>
    public bool Refused { get; }

    ToolAnswer(bool refused, string? document, string? message, bool json, OrderStamp? epoch, bool epochLine = false)
        => (Refused, _document, _message, _json, _epoch, _epochLine) = (refused, document, message, json, epoch, epochLine);

    /// <summary>The call's answer.</summary>
    public static ToolAnswer Served(string document) => new(false, document, null, false, null);

    /// <summary>A refusal whose whole document the lane rendered itself: a refused outcome, which keeps its own members.</summary>
    public static ToolAnswer RefusedDocument(string document) => new(true, document, null, false, null);

    /// <summary>An outcome's document, refused when it did not succeed.</summary>
    public static ToolAnswer Outcome(bool succeeded, string document) => new(!succeeded, document, null, false, null);

    /// <summary>A whole-call refusal rendered per transport: text keeps the message as written, json is
    /// <c>{ok:false, error, epoch}</c> with the <see cref="Wire.RefusalPrefix"/> stripped; epoch null writes <c>epoch:null</c>.
    /// <paramref name="epochLine"/> also stamps the text, on its own line, as the lanes that say so do.</summary>
    public static ToolAnswer Refusal(bool json, string message, OrderStamp? epoch = null, bool epochLine = false)
        => new(true, null, message, json, epoch, epochLine);

    /// <summary>A refusal that is text on every transport: a tool with no format=, the format= error itself (ruling #7), the Guard's own.</summary>
    public static ToolAnswer Refusal(string message) => new(true, null, message, false, null);

    /// <summary>The text the call returns; only <see cref="Guard"/> renders.</summary>
    internal string Render()
    {
        if (_document is not null) return _document;
        if (!_json) return _epochLine ? _message + Wire.EpochLine(_epoch) : _message!;
        var bare = _message!.StartsWith(Wire.RefusalPrefix, StringComparison.Ordinal)
            ? _message[Wire.RefusalPrefix.Length..]
            : _message;
        return JsonWire.RenderError(bare, _epoch);
    }
}
