using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The built housecarl-mcp.exe, spun up ONCE and driven over stdio — the wire, not a C# method call.
///
/// This is the oracle #470 asks for: what a caller can actually reach is `tools/list`, never the
/// <c>[McpServerTool]</c> attributes in source. Those two diverged silently for twelve days and no guard
/// noticed, because every check-family guard called <c>CheckTools.CheckTool</c> directly.
///
/// The server boots against a FRESH EMPTY data dir, so there is no houseCARL.user.json and it is
/// deterministically unconfigured — on a dev box too, where a real user config may sit beside the exe.
/// "The tool body ran" is then observable as the trained config prompt: binding failures answer with the
/// SDK's generic error instead, which is exactly the difference these tests are about.
/// </summary>
public sealed class ServerFixture : IDisposable
{
    readonly Process _proc;
    readonly StreamWriter _in;
    readonly StreamReader _out;
    readonly LinePump _lines;
    readonly string _dataDir;
    int _id = 1;
    string? _poison;

    /// <summary>
    /// The per-call deadline. A test that deliberately drives the timeout path shortens it on its OWN
    /// private fixture — never on the shared one, which is what the poison below exists to protect.
    /// </summary>
    internal TimeSpan RpcTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>The trained prompt an unconfigured server gives once a tool BODY runs.</summary>
    public const string ConfigPrompt = "no Mod Organizer 2 instance configured";

    /// <summary>The SDK's generic bind failure — the answer a caller gets when the body never ran.</summary>
    public const string GenericError = "An error occurred invoking";

    /// <summary>Every tool name `tools/list` published, in publication order.</summary>
    public IReadOnlyList<string> PublishedNames { get; }

    /// <summary>The published tool objects by name — schema included.</summary>
    public IReadOnlyDictionary<string, JsonElement> PublishedTools { get; }

    public ServerFixture()
    {
        var exe = Path.Combine(HarnessPaths.RepoRoot, "src", "housecarl-mcp", "bin",
                               HarnessPaths.Configuration, "net9.0", "housecarl-mcp.exe");
        if (!File.Exists(exe))
            throw new InvalidOperationException(
                $"housecarl-mcp.exe is not built at '{exe}'. Build the solution in " +
                $"{HarnessPaths.Configuration} first — these tests drive the real server and cannot " +
                "substitute anything else for it.");

        _dataDir = Path.Combine(Path.GetTempPath(), "housecarl-mcp-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDir);

        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };
        psi.Environment["HOUSECARL_DATA_DIR"] = _dataDir;

        _proc = Process.Start(psi)!;
        _proc.ErrorDataReceived += (_, _) => { };     // server logs ride stderr — drain, ignore
        _proc.BeginErrorReadLine();
        _in = _proc.StandardInput;
        _out = _proc.StandardOutput;
        _lines = new LinePump(_out);   // ONE reader on this stream for the fixture's whole life

        Rpc("initialize", new
        {
            protocolVersion = "2025-06-18",
            capabilities = new { },
            clientInfo = new { name = "housecarl-mcp-tests", version = "0" },
        });
        Notify("notifications/initialized");

        var tools = Rpc("tools/list", new { });
        var names = new List<string>();
        var byName = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var t in tools.GetProperty("tools").EnumerateArray())
        {
            var n = t.GetProperty("name").GetString()!;
            names.Add(n);
            byName[n] = t.Clone();
        }
        PublishedNames = names;
        PublishedTools = byName;
    }

    /// <summary>One tools/call, with its arguments spelled as the JSON a client would actually send.</summary>
    public CallResult Call(string tool, string argumentsJson)
    {
        using var argsDoc = JsonDocument.Parse(argumentsJson);
        var result = Rpc("tools/call", new { name = tool, arguments = argsDoc.RootElement });

        bool isError = result.TryGetProperty("isError", out var ie) && ie.ValueKind == JsonValueKind.True;
        string? blockType = null, text = null;
        int blocks = 0;
        if (result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            blocks = content.GetArrayLength();
            foreach (var block in content.EnumerateArray())
            {
                blockType ??= block.TryGetProperty("type", out var ty) ? ty.GetString() : null;
                if (text is null && block.TryGetProperty("text", out var tx)) text = tx.GetString();
            }
        }
        return new CallResult(isError, blocks, blockType, text ?? "", result);
    }

    /// <summary>A tools/call response, decomposed into the values a test can assert on.</summary>
    public readonly record struct CallResult(bool IsError, int ContentBlocks, string? FirstBlockType,
                                             string Text, JsonElement Raw)
    {
        /// <summary>True when the tool BODY ran: the unconfigured server's own prompt came back.</summary>
        public bool BodyRan => !Text.Contains(GenericError, StringComparison.Ordinal)
                            && Text.Contains(ConfigPrompt, StringComparison.Ordinal);

        public string Describe() => $"isError={IsError} blocks={ContentBlocks} type={FirstBlockType} " +
                                    $"text=\"{(Text.Length > 200 ? Text[..200] + "…" : Text)}\"";
    }

    JsonElement Rpc(string method, object @params)
    {
        // One timeout poisons the fixture. This server is shared by every stdio test in the run, so a
        // genuinely hung call is a property of the server, not of the one test that happened to find it —
        // letting each later test spend its own full deadline turns one cause into seventy identical
        // timeouts and buries it. Fail fast, and name the call that actually broke.
        if (_poison is { } first)
            throw new InvalidOperationException(
                $"The shared server fixture is poisoned by an earlier timeout — {first} This call was not " +
                "attempted. Fix the first timeout; every failure after it is downstream of that one.");

        int id = Interlocked.Increment(ref _id);
        Send(new { jsonrpc = "2.0", id, method, @params });

        var deadline = DateTime.UtcNow + RpcTimeout;
        while (true)
        {
            var remaining = deadline - DateTime.UtcNow;      // ONE shared budget; a per-line wait never extends it
            if (remaining <= TimeSpan.Zero || !_lines.TryTake(remaining, out var line)) break;
            if (line.Length == 0) continue;

            using var doc = JsonDocument.Parse(line);
            if (!doc.RootElement.TryGetProperty("id", out var rid)
                || rid.ValueKind != JsonValueKind.Number || rid.GetInt32() != id) continue;   // notification or other id

            if (doc.RootElement.TryGetProperty("error", out var err))
                throw new InvalidOperationException($"JSON-RPC error for {method}: {err.GetRawText()}");
            return doc.RootElement.GetProperty("result").Clone();
        }

        _poison ??= $"no response to {method} (id {id}) within {RpcTimeout.TotalSeconds:0.###}s.";
        throw new TimeoutException(_poison);
    }

    void Notify(string method) => Send(new { jsonrpc = "2.0", method });

    void Send(object msg)
    {
        _in.WriteLine(JsonSerializer.Serialize(msg));
        _in.Flush();
    }

    public void Dispose()
    {
        try { if (!_proc.HasExited) _proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
        _lines.Dispose();   // after the kill: the pump's ReadLine returns once the stream is torn down
        _proc.Dispose();
        try { Directory.Delete(_dataDir, recursive: true); } catch { /* best effort */ }
    }
}

/// <summary>One server process for every stdio test in the run — spinning one per class costs a boot each.</summary>
[CollectionDefinition("server")]
public sealed class ServerCollection : ICollectionFixture<ServerFixture> { }
