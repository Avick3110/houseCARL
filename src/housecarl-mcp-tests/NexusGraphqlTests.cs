using System.Text.Json;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The raw GraphQL passthrough's offline pieces (from the <c>nexus-graphql-guard</c> probe): the read-only
/// refusal of a mutation or subscription operation, with no false refusal of a read, and the literal, bounded
/// rendering of the returned payload. Nothing here reaches the network.</summary>
[Trait("tier", "unit")]
public sealed class NexusGraphqlTests
{
    // mutation at doc start -> refused; subscription (leading whitespace) -> refused; mutation as a SECOND operation (after '}') -> refused
    [Theory]
    [InlineData("mutation { endorse(modId:1) { ok } }")]
    [InlineData("  \n  subscription { x }")]
    [InlineData("query{ mod{ name } } mutation{ x }")]
    public void AMutationOrSubscriptionOperationIsRefused(string query) =>
        Assert.True(NexusClient.IsMutatingQuery(query));

    // named read query -> allowed; anonymous read query -> allowed; fields merely CONTAINING the words -> allowed (no false refusal)
    [Theory]
    [InlineData("query{ mod(modId:\"1\"){ name tags{ name } } }")]
    [InlineData("{ mod{ name } }")]
    [InlineData("query{ mod{ mutationCount subscriptionState } }")]
    public void AReadQueryIsNeverRefused(string query) =>
        Assert.False(NexusClient.IsMutatingQuery(query));

    // Not a probe assert: the refusal is returned before any request is sent, so a mutation never reaches the endpoint.
    [Fact]
    public async Task AMutationIsRefusedWithoutSendingARequest()
    {
        var handler = new CountingHandler();
        var (ok, error, _) = await new NexusClient(new HttpClient(handler))
            .RawQueryAsync("mutation { endorse(modId:1) { ok } }", null, default);

        Assert.False(ok);
        Assert.Contains("READ-ONLY", error);
        Assert.Equal(0, handler.Sent);
    }

    // small payload -> rendered whole + pretty-printed; small payload -> no truncation marker
    [Fact]
    public void ASmallPayloadRendersWholeAndIndented()
    {
        using var doc = JsonDocument.Parse("{\"mod\":{\"name\":\"X\",\"tags\":[{\"name\":\"Gameplay\"}]}}");
        var outp = Render.Graphql(doc.RootElement);

        Assert.Contains("\"Gameplay\"", outp);
        Assert.Contains("\n  ", outp);
        Assert.DoesNotContain("truncated", outp);
    }

    // oversize payload -> explicit truncation marker (Q3, never silent); oversize payload -> bounded near the cap
    [Fact]
    public void AnOversizePayloadIsCutWithAMarker()
    {
        using var doc = JsonDocument.Parse("{\"blob\":\"" + new string('x', 60000) + "\"}");
        var outp = Render.Graphql(doc.RootElement);

        Assert.Contains("truncated", outp);
        Assert.True(outp.Length < 41000, $"length {outp.Length}");
    }

    // '+' rendered literal, not escaped; non-ASCII and '&' rendered literal, not escaped (exact-output fidelity)
    [Fact]
    public void PlusAmpersandAndNonAsciiRenderLiterally()
    {
        using var doc = JsonDocument.Parse("{\"version\":\"1.0+SE\",\"name\":\"Brivé & Co\"}");
        var outp = Render.Graphql(doc.RootElement);

        Assert.Contains("1.0+SE", outp);
        Assert.Contains("Brivé & Co", outp);
    }

    sealed class CountingHandler : HttpMessageHandler
    {
        public int Sent;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Sent++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                { Content = new StringContent("{\"data\":{}}") });
        }
    }
}
