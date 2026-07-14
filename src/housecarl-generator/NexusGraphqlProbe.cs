using System.Text.Json;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// nexus-graphql-guard — locks the RAW GraphQL passthrough backstop's two pure, offline pieces:
///   • NexusClient.IsMutatingQuery — the READ-ONLY contract. A mutation/subscription OPERATION (the keyword at document
///     start, or after a prior operation's closing '}') is refused; a field that merely CONTAINS the word (a selection
///     like 'mutationCount') is NOT — a legitimate read is never falsely refused.
///   • Render.Graphql — pretty-printed + BOUNDED output (Q3: an oversize payload is truncated with an explicit marker,
///     never silently cut).
/// Both are network-free — the live query path is exercised by hand, not CI. Self-contained; no game data, no network.
/// </summary>
internal static class NexusGraphqlProbe
{
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("================================================================");
        Console.WriteLine(" nexus-graphql guard — read-only mutation refusal + bounded raw output");
        Console.WriteLine("================================================================");
        int fail = 0;
        void Check(bool c, string label) { Console.WriteLine((c ? "  PASS  " : "  FAIL  ") + label); if (!c) fail++; }

        // READ-ONLY contract — a mutation/subscription OPERATION is refused …
        Check(NexusClient.IsMutatingQuery("mutation { endorse(modId:1) { ok } }"), "mutation at doc start -> refused");
        Check(NexusClient.IsMutatingQuery("  \n  subscription { x }"), "subscription (leading whitespace) -> refused");
        Check(NexusClient.IsMutatingQuery("query{ mod{ name } } mutation{ x }"), "mutation as a SECOND operation (after '}') -> refused");
        // … but a legitimate READ is never falsely refused, even when a field/word contains 'mutation'/'subscription'.
        Check(!NexusClient.IsMutatingQuery("query{ mod(modId:\"1\"){ name tags{ name } } }"), "named read query -> allowed");
        Check(!NexusClient.IsMutatingQuery("{ mod{ name } }"), "anonymous read query -> allowed");
        Check(!NexusClient.IsMutatingQuery("query{ mod{ mutationCount subscriptionState } }"), "fields merely CONTAINING the words -> allowed (no false refusal)");

        // BOUNDED raw output (Q3) — a small payload renders whole + indented; an oversize one is truncated with a marker.
        using (var small = JsonDocument.Parse("{\"mod\":{\"name\":\"X\",\"tags\":[{\"name\":\"Gameplay\"}]}}"))
        {
            var outp = Render.Graphql(small.RootElement);
            Check(outp.Contains("\"Gameplay\"") && outp.Contains("\n  "), "small payload -> rendered whole + pretty-printed");
            Check(!outp.Contains("truncated"), "small payload -> no truncation marker");
        }
        using (var big = JsonDocument.Parse("{\"blob\":\"" + new string('x', 60000) + "\"}"))
        {
            var outp = Render.Graphql(big.RootElement);
            Check(outp.Contains("truncated"), "oversize payload -> explicit truncation marker (Q3, never silent)");
            Check(outp.Length < 41000, "oversize payload -> bounded near the cap");
        }

        Console.WriteLine(fail == 0
            ? "[nexus-graphql] PASS - read-only refusal + bounded output hold."
            : $"[nexus-graphql] FAIL ({fail})");
        return fail;
    }
}
