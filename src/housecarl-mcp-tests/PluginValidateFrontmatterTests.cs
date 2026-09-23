using System.Text.Json;
using Xunit;
using YamlDotNet.Serialization;

namespace HousecarlMcpTests;

/// <summary>Every shipped SKILL.md frontmatter, the Codex umbrella's included, parses as YAML with a name and a
/// description of at most the loader's 1024 characters, and plugin.json carries a name and a version. CI's
/// `claude plugin validate` reads only the bundled plugin tree, so the Codex umbrella is checked here or
/// nowhere. Migrated from the plugin-validate-guard probe.</summary>
[Trait("tier", "unit")]
public sealed class PluginValidateFrontmatterTests
{
    /// <summary>The loader's MAX_DESCRIPTION_LENGTH; past it the description is truncated silently.</summary>
    const int MaxDescription = 1024;

    public static IEnumerable<object[]> SkillFiles()
    {
        var roots = new[] { Path.Combine(HarnessPaths.RepoRoot, ".claude", "skills"), Path.Combine(HarnessPaths.RepoRoot, "plugin") };
        foreach (var r in roots)
            if (!Directory.Exists(r)) throw new InvalidOperationException($"skill root '{r}' does not exist");
        var files = roots.SelectMany(r => Directory.GetFiles(r, "SKILL.md", SearchOption.AllDirectories))
            .Select(f => Path.GetRelativePath(HarnessPaths.RepoRoot, f).Replace('\\', '/'))
            .Distinct().OrderBy(p => p, StringComparer.Ordinal).ToList();
        if (files.Count == 0) throw new InvalidOperationException("no SKILL.md under .claude/skills or plugin/");
        return files.Select(f => new object[] { f });
    }

    // INV1-GREEN: every shipped skill frontmatter parses and has name/description (both trees).
    [Theory]
    [MemberData(nameof(SkillFiles))]
    public void AShippedSkillFrontmatterParsesWithANameAndADescription(string relPath)
    {
        var v = ValidateSkillFrontmatter(File.ReadAllText(Path.Combine(HarnessPaths.RepoRoot, relPath)));
        Assert.True(v.Count == 0, $"{relPath}: " + string.Join("; ", v));
    }

    // INV1-GREEN: the Codex umbrella, which ships outside the tree the CLI validator reads, is in the set.
    [Fact]
    public void TheCodexUmbrellaIsAmongTheValidatedFiles() =>
        Assert.Contains(SkillFiles(), row => (string)row[0] == "plugin/codex/housecarl/SKILL.md");

    // INV1-RED: a colon-space description (the 1.3 pre-release bug) is caught.
    [Fact]
    public void AColonSpaceDescriptionFailsToParse() =>
        Assert.Contains(ValidateSkillFrontmatter("---\nname: x\ndescription: it edits the records themselves: distributing forms is SPID\n---\n"),
            s => s.Contains("parse", StringComparison.OrdinalIgnoreCase));

    // INV1-RED: a frontmatter missing 'description' is caught.
    [Fact]
    public void AFrontmatterMissingDescriptionIsReported() =>
        Assert.Contains(ValidateSkillFrontmatter("---\nname: x\n---\n"), s => s.Contains("description", StringComparison.OrdinalIgnoreCase));

    // INV1-RED: a file with no --- fence is caught.
    [Fact]
    public void AFileWithNoFenceIsReported() =>
        Assert.Contains(ValidateSkillFrontmatter("# Heading\nbody, no frontmatter\n"), s => s.Contains("frontmatter", StringComparison.OrdinalIgnoreCase));

    // INV1-RED: a description past the character ceiling is caught.
    [Fact]
    public void ADescriptionPastTheCeilingIsReported() =>
        Assert.Contains(ValidateSkillFrontmatter($"---\nname: x\ndescription: {new string('x', MaxDescription + 1)}\n---\n"),
            s => s.Contains("ceiling", StringComparison.OrdinalIgnoreCase));

    // INV2-GREEN: the plugin manifest parses and has name/version.
    [Fact]
    public void ThePluginManifestHasANameAndAVersion()
    {
        var path = Path.Combine(HarnessPaths.RepoRoot, "plugin", ".claude-plugin", "plugin.json");
        var v = ValidateManifest(File.Exists(path) ? File.ReadAllText(path) : null, path);
        Assert.True(v.Count == 0, string.Join("; ", v));
    }

    // INV2-RED: a manifest missing 'version' is caught.
    [Fact]
    public void AManifestMissingVersionIsReported() =>
        Assert.Contains(ValidateManifest("{ \"name\": \"x\" }", "synthetic"), s => s.Contains("version", StringComparison.OrdinalIgnoreCase));

    /// <summary>Take the leading --- ... --- block and parse it with a real YAML parser, as the harness does.</summary>
    static List<string> ValidateSkillFrontmatter(string content)
    {
        var v = new List<string>();
        var fm = ExtractFrontmatter(content);
        if (fm == null) { v.Add("no YAML frontmatter block (leading --- ... --- fence) found"); return v; }

        object? doc;
        try { doc = new DeserializerBuilder().Build().Deserialize<object>(fm); }
        catch (Exception ex)
        {
            v.Add($"frontmatter failed to parse as YAML ({ex.GetType().Name}: {ex.Message.Replace("\r", "").Split('\n')[0]})");
            return v;
        }
        if (doc is not IDictionary<object, object> map) { v.Add("frontmatter did not parse to a key/value mapping"); return v; }
        foreach (var key in new[] { "name", "description" })
        {
            map.TryGetValue(key, out var val);
            if (val is null || string.IsNullOrWhiteSpace(val.ToString())) v.Add($"frontmatter missing a non-empty '{key}'");
        }
        if (map.TryGetValue("description", out var d) && d?.ToString() is { } text && text.Length > MaxDescription)
            v.Add($"description is {text.Length} characters, past the {MaxDescription}-character ceiling");
        return v;
    }

    static List<string> ValidateManifest(string? json, string path)
    {
        var v = new List<string>();
        if (json == null) { v.Add($"manifest not found at {path}"); return v; }
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (Exception ex) { v.Add($"manifest is not valid JSON ({ex.Message})"); return v; }
        using (doc)
            foreach (var key in new[] { "name", "version" })
                if (!doc.RootElement.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(el.GetString()))
                    v.Add($"manifest missing a non-empty string '{key}'");
        return v;
    }

    /// <summary>The text between a leading '---' fence and the next line starting '---'; null when there is none.
    /// CRLF and a leading BOM are normalized, as the harness reads them.</summary>
    static string? ExtractFrontmatter(string content)
    {
        var text = content.Replace("\r\n", "\n").Replace('\r', '\n').TrimStart('\uFEFF');
        if (text != "---" && !text.StartsWith("---\n", StringComparison.Ordinal)) return null;
        var nl = text.IndexOf('\n');
        if (nl < 0) return null;
        var rest = text[(nl + 1)..];
        var end = rest.IndexOf("\n---", StringComparison.Ordinal);
        return end < 0 ? null : rest[..end];
    }
}
