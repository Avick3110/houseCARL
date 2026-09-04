using System.Text.Json;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>Shared shorthand for the tool-driven records tests: the world, the pole builders, the JSON helper.</summary>
public abstract class RecordsTestBase
{
    protected readonly RecordsWorld W;
    protected RecordsTestBase(RecordsFixture f) => W = f.W;

    protected LoadOrderService Svc => W.Svc;

    protected static JsonElement Je(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>A bare plugin-name source/versus pole.</summary>
    protected static JsonElement Plugin(string name) => Je("\"" + name + "\"");

    /// <summary>The SkyPatcher overlay pole at a named state.</summary>
    protected static JsonElement Overlay(string state) => Je("{\"overlay\": \"skypatcher\", \"state\": \"" + state + "\"}");

    protected static string Fid(Mutagen.Bethesda.Plugins.FormKey fk) => RecordsWorld.Fid(fk);

    protected string[] AllWeaponIds => W.Weapons.Select(Fid).ToArray();

    protected static RecordsTools.RecordsProject Form(string form) => new() { form = form };

    protected static RecordsTools.RecordsProject Fields(params string[] paths) =>
        new() { form = "fields", fields = paths };

    protected static RecordsTools.RecordsScope Scope(params string[] names) => new() { names = names };

    protected static int CountOf(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    /// <summary>A refusal: the text lane's own discriminant.</summary>
    protected static void Refused(string response, params string[] mustName)
    {
        Assert.StartsWith("error:", response);
        foreach (var s in mustName) Assert.Contains(s, response);
    }

    /// <summary>Served, not refused, and naming what the test claims.</summary>
    protected static void Served(string response, params string[] mustName)
    {
        Assert.False(response.StartsWith("error:", StringComparison.Ordinal), "refused: " + First(response));
        foreach (var s in mustName) Assert.Contains(s, response);
    }

    protected static string First(string response) =>
        response.Split('\n').FirstOrDefault()?.Trim() ?? "";
}
