using System.Text.Json;
using YamlDotNet.Serialization;

namespace HousecarlGenerator;

/// <summary>
/// REGRESSION GUARD (standing CI instrument) — PLUGIN PACKAGING VALIDATION.
///
/// A shipped skill is only ever reached through its SKILL.md YAML frontmatter: the harness parses that
/// frontmatter to learn the skill's `name` + `description`, and the description is the ENTIRE surface the
/// relevance matcher triggers on. When the frontmatter fails to parse, the harness drops ALL of it silently —
/// the skill loads with empty metadata, present on disk but invisible to triggering. That is a Q3
/// silently-wrong outcome in a SHIPPED artifact, and it is exactly what bit `dialogue-authoring` at the 1.3
/// pre-release gate: a single colon-space inside the unquoted description ("...the records themselves:
/// distributing...") made YAML read a nested mapping, threw, and dropped name+description. It passed PR review
/// and CI green because CI never validated the plugin PACKAGE — only `claude plugin validate --strict` (a
/// manual, rig-time step) caught it. This guard turns that manual catch into a by-construction CI gate, so this
/// class is caught at PR time, not at release (the manual --strict stays as belt-and-suspenders — see INV1).
///
/// Self-contained, in the corpus-hygiene-guard pattern: it reads the REAL shipped artifacts from the repo
/// (every .claude/skills/*/SKILL.md, the Codex umbrella plugin/codex/housecarl/SKILL.md, and the manifest
/// plugin/.claude-plugin/plugin.json — CWD is the repo root, as the generator's corpus default already assumes)
/// and asserts each invariant GREEN over them, paired with a RED
/// arm that feeds the SAME checker a synthetic violation and asserts it FIRES — so a GREEN can never mean "no
/// skill happened to be broken", only "the checker has teeth and every real skill passes it".
///
///   INV1 — EVERY SHIPPED SKILL FRONTMATTER PARSES + CARRIES name/description, across BOTH shipped trees (the
///          Claude Code skills under .claude/skills AND the Codex umbrella plugin/codex/housecarl). The leading
///          --- ... --- fenced block parses as a YAML mapping with a non-empty `name` and `description`. The parse
///          uses a real YAML parser (YamlDotNet) — a FAITHFUL PROXY for, not byte-identical to, the harness's own
///          (JS) YAML parser: it reliably catches the colon-space class (RED-proven below), but the residual risk
///          is a false-NEGATIVE (YamlDotNet accepts what the real loader would drop), which is why the manual
///          --strict stays the belt-and-suspenders. (RED: the literal colon-space description that dropped
///          dialogue-authoring; a frontmatter missing `description`; a file with no fence at all.)
///   INV2 — THE PLUGIN MANIFEST PARSES + CARRIES name/version. plugin.json is valid JSON with a non-empty
///          string name + version (the single source of truth the build stamps). (RED: a manifest missing
///          version.)
///
/// Run: dotnet run --project src/housecarl-generator -- plugin-validate-guard
/// </summary>
public static class PluginValidateProbe
{
    static int _pass, _fail;

    public static int RunGuard(string[] args)
    {
        Console.WriteLine("################  REGRESSION GUARD — plugin packaging validation (SKILL.md frontmatter + manifest)  ################");
        Console.WriteLine();
        try
        {
            // ---------- INV1 — every shipped skill's frontmatter parses + has name/description ----------
            // BOTH shipped frontmatter trees are in scope: the Claude Code skills (.claude/skills, bundled by
            // build-plugin.ps1) AND the Codex umbrella skill (plugin/codex/housecarl, shipped separately by the
            // same build). Both carry a YAML frontmatter the respective loader parses, so both are exposed to the
            // parse-failure class — walking only .claude/skills would leave the Codex skill's same-shape
            // description unguarded (PR #95 review).
            var skillRoots = new[] { Path.Combine(".claude", "skills"), "plugin" };
            foreach (var root in skillRoots)
                Check($"INV1-GREEN skill root '{root}' resolves (run from repo root)", Directory.Exists(root),
                    new() { $"'{Path.GetFullPath(root)}' not found — CWD must be the repo root" });

            var skillFiles = skillRoots.Where(Directory.Exists)
                .SelectMany(r => Directory.GetFiles(r, "SKILL.md", SearchOption.AllDirectories))
                .Distinct().OrderBy(p => p, StringComparer.Ordinal).ToList();
            // loud-fail on zero: a wrong CWD must never read as "all skills valid" (Q3)
            Check($"INV1-GREEN found shipped skill frontmatters to validate ({skillFiles.Count})", skillFiles.Count > 0,
                new() { "no SKILL.md under .claude/skills or plugin/ — wrong CWD or empty tree" });
            foreach (var f in skillFiles)
            {
                var v = ValidateSkillFrontmatter(File.ReadAllText(f));
                Check($"INV1-GREEN '{RelLabel(f)}' frontmatter parses + has name/description", v.Count == 0, v);
            }

            // RED arms — the SAME checker must catch each class, or it is toothless
            RedSkill("INV1-RED  colon-space description (the dialogue-authoring bug) is caught",
                "---\nname: x\ndescription: it edits the records themselves: distributing forms is SPID\n---\n",
                s => s.Contains("parse", StringComparison.OrdinalIgnoreCase));
            RedSkill("INV1-RED  a frontmatter missing 'description' is caught",
                "---\nname: x\n---\n",
                s => s.Contains("description", StringComparison.OrdinalIgnoreCase));
            RedSkill("INV1-RED  a file with no --- fence is caught",
                "# Heading\nbody, no frontmatter\n",
                s => s.Contains("frontmatter", StringComparison.OrdinalIgnoreCase));

            // ---------- INV2 — the plugin manifest parses + has name/version ----------
            var manifestPath = Path.Combine("plugin", ".claude-plugin", "plugin.json");
            var mv = ValidateManifest(File.Exists(manifestPath) ? File.ReadAllText(manifestPath) : null, manifestPath);
            Check("INV2-GREEN plugin manifest parses + has name/version", mv.Count == 0, mv);

            var redManifest = ValidateManifest("{ \"name\": \"x\" }", "synthetic");
            Check("INV2-RED  a manifest missing 'version' is caught",
                redManifest.Any(s => s.Contains("version", StringComparison.OrdinalIgnoreCase)), redManifest, redArm: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   FAIL (unexpected): {ex.GetType().Name}: {ex.Message}");
            _fail++;
        }

        Console.WriteLine();
        Console.WriteLine($"=== plugin-validate-guard: {_pass} passed, {_fail} failed -> {(_fail == 0 ? "PASS" : "FAIL")} ===");
        return _fail == 0 ? 0 : 1;
    }

    static void RedSkill(string label, string synthetic, Func<string, bool> caught)
    {
        var hit = ValidateSkillFrontmatter(synthetic);
        Check(label, hit.Any(caught), hit, redArm: true);
    }

    /// <summary>One arm: print PASS/FAIL, tally, and on FAIL dump the (un)expected violations so a red CI log is
    /// self-diagnosing (Q3). For a RED arm an EMPTY detail means the checker caught nothing — i.e. it is toothless.</summary>
    static void Check(string label, bool ok, List<string> detail, bool redArm = false)
    {
        Console.WriteLine($"   {label,-72}: {(ok ? "PASS" : "FAIL")}");
        if (!ok)
        {
            if (detail.Count == 0)
                Console.WriteLine(redArm ? "        - (the checker reported NO violation — it is toothless)" : "        - (no detail)");
            foreach (var d in detail.Take(12)) Console.WriteLine($"        - {d}");
        }
        if (ok) _pass++; else _fail++;
    }

    // ===================================================================== checkers

    /// <summary>Faithful to how the harness loads a SKILL.md: take the leading --- ... --- fenced block and parse
    /// it with a real YAML parser. A parse throw is THE failure class (the harness then drops every field). On a
    /// clean parse, require a non-empty name + description (an empty description triggers on nothing).</summary>
    static List<string> ValidateSkillFrontmatter(string content)
    {
        var v = new List<string>();
        var fm = ExtractFrontmatter(content);
        if (fm == null) { v.Add("no YAML frontmatter block (leading --- ... --- fence) found"); return v; }

        object? doc;
        try { doc = new DeserializerBuilder().Build().Deserialize<object>(fm); }
        catch (Exception ex)
        {
            v.Add($"frontmatter failed to parse as YAML ({ex.GetType().Name}: {FirstLine(ex.Message)}) — the harness drops ALL metadata when this happens");
            return v;
        }
        if (doc is not IDictionary<object, object> map) { v.Add("frontmatter did not parse to a key/value mapping"); return v; }
        foreach (var key in new[] { "name", "description" })
        {
            map.TryGetValue(key, out var val);
            if (val is null || string.IsNullOrWhiteSpace(val.ToString()))
                v.Add($"frontmatter missing a non-empty '{key}'");
        }
        return v;
    }

    static List<string> ValidateManifest(string? json, string path)
    {
        var v = new List<string>();
        if (json == null) { v.Add($"manifest not found at {path}"); return v; }
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (Exception ex) { v.Add($"manifest is not valid JSON ({FirstLine(ex.Message)})"); return v; }
        using (doc)
            foreach (var key in new[] { "name", "version" })
                if (!doc.RootElement.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(el.GetString()))
                    v.Add($"manifest missing a non-empty string '{key}'");
        return v;
    }

    /// <summary>The frontmatter is the text between the first '---' fence at the very top of the file and the next
    /// line that begins '---'. Returns null when there is no leading fenced block. Normalizes CRLF and a leading
    /// BOM so a Windows-authored file is read the same way the harness reads it.</summary>
    static string? ExtractFrontmatter(string content)
    {
        var text = content.Replace("\r\n", "\n").Replace('\r', '\n').TrimStart('\uFEFF');
        if (text != "---" && !text.StartsWith("---\n", StringComparison.Ordinal)) return null;
        var nl = text.IndexOf('\n');
        if (nl < 0) return null;
        var rest = text[(nl + 1)..];
        var end = rest.IndexOf("\n---", StringComparison.Ordinal);
        if (end < 0) return null;
        return rest[..end];
    }

    static string FirstLine(string s) => s.Replace("\r", "").Split('\n')[0];

    /// <summary>A repo-root-relative, forward-slashed label for a SKILL.md's directory — so
    /// '.claude/skills/dialogue-authoring' and 'plugin/codex/housecarl' are both unambiguous in the CI log.</summary>
    static string RelLabel(string file)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(file))!;
        return Path.GetRelativePath(Directory.GetCurrentDirectory(), dir).Replace('\\', '/');
    }
}
