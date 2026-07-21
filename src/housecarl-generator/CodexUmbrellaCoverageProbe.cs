using System.Reflection;
using ModelContextProtocol.Server;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// REGRESSION GUARD (standing CI instrument, self-contained) — CODEX UMBRELLA COVERAGE.
///
/// The Codex packaging ships ONE umbrella routing skill (plugin/codex/housecarl/SKILL.md) that hand-lists
/// houseCARL's MCP tools and helper skills. Unlike the 13 Claude Code skills — each its own trigger — the
/// umbrella is Codex's single hand-maintained router, so nothing forced it to track the tool/skill surface: it
/// silently drifted from full coverage to 9 of ~45 tools over ~2 months because adding a tool never touched it.
///
/// This guard makes that drift impossible by construction. It reflects the REAL [McpServerTool] names off the
/// housecarl-mcp assembly (immune to the source line-wrapping that hides a tool from a grep) and reads the REAL
/// .claude/skills/* folders, then asserts EVERY one is referenced in the umbrella — or explicitly allow-listed as
/// a deliberate omission. A session that adds housecarl_foo or a new skill and forgets the Codex router now gets
/// a RED CI arm naming exactly what to add. Same "green only if the checker has teeth" shape as the other guards:
/// RED arms feed a synthetic omission and assert it fires; the allow-list is proven to actually suppress.
///
///   INV1 — every current MCP tool name is referenced in the umbrella (or allow-listed).
///   INV2 — every bundled skill slug is referenced in the umbrella (or allow-listed).
///
/// Run: dotnet run --project src/housecarl-generator -- codex-umbrella-coverage-guard
/// </summary>
public static class CodexUmbrellaCoverageProbe
{
    static int _pass, _fail;

    // Deliberate omissions from the Codex umbrella router. EMPTY by design — the umbrella covers the whole
    // surface today. Add a name here ONLY with a one-line reason when a tool/skill is intentionally not routed by
    // the umbrella; that keeps "not in the router" a conscious choice recorded here, never silent drift.
    static readonly HashSet<string> Allow = new(StringComparer.Ordinal)
    {
        // (none)
    };

    static readonly string UmbrellaPath = Path.Combine("plugin", "codex", "housecarl", "SKILL.md");

    public static int RunGuard(string[] args)
    {
        Console.WriteLine("################  REGRESSION GUARD — Codex umbrella coverage (tools + skills referenced)  ################");
        Console.WriteLine();
        try
        {
            // A missing/empty router must never read as "all covered" (Q3).
            Check($"GREEN umbrella router resolves ('{UmbrellaPath.Replace('\\', '/')}', run from repo root)", File.Exists(UmbrellaPath),
                new() { $"'{Path.GetFullPath(UmbrellaPath)}' not found — CWD must be the repo root" });
            var umbrella = File.Exists(UmbrellaPath) ? File.ReadAllText(UmbrellaPath) : "";

            // Authoritative tool set — reflected off the shipped [McpServerTool] attributes.
            var tools = McpToolNames();
            Check($"GREEN reflected a non-empty MCP tool set ({tools.Count})", tools.Count > 0,
                new() { "no [McpServerTool] names reflected off the housecarl-mcp assembly — wrong assembly, or the attribute type moved" });

            // Authoritative skill set — the real .claude/skills/* folders.
            var skills = SkillSlugs();
            Check($"GREEN found bundled skill folders ({skills.Count})", skills.Count > 0,
                new() { "no folders under .claude/skills — wrong CWD or empty tree" });

            // INV1 — every tool referenced.
            var missTools = MissingRefs(umbrella, tools, Allow);
            Check("INV1-GREEN every MCP tool is referenced in the umbrella router", missTools.Count == 0,
                missTools.Select(t => $"tool not routed by the Codex umbrella: {t} — add it to {UmbrellaPath.Replace('\\', '/')} (or Allow with a reason)").ToList());

            // INV2 — every skill referenced.
            var missSkills = MissingRefs(umbrella, skills, Allow);
            Check("INV2-GREEN every bundled skill is referenced in the umbrella router", missSkills.Count == 0,
                missSkills.Select(s => $"skill not routed by the Codex umbrella: {s} — add it to {UmbrellaPath.Replace('\\', '/')} (or Allow with a reason)").ToList());

            // RED arms — the checker must catch an omission, or it is toothless.
            var redTool = MissingRefs("router text that mentions no tools", new[] { "housecarl_read_record" }, Empty());
            Check("INV1-RED  a missing tool reference is caught", redTool.Contains("housecarl_read_record"), redTool, redArm: true);

            var redSkill = MissingRefs("router text that mentions no skills", new[] { "facegen-diagnostics" }, Empty());
            Check("INV2-RED  a missing skill reference is caught", redSkill.Contains("facegen-diagnostics"), redSkill, redArm: true);

            // The allow-list must actually suppress — else an Allow entry would be a lie.
            var allowed = MissingRefs("router text that mentions no tools", new[] { "housecarl_read_record" },
                new HashSet<string>(StringComparer.Ordinal) { "housecarl_read_record" });
            Check("ALLOW     an allow-listed name is NOT reported missing", allowed.Count == 0, allowed, redArm: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   FAIL (unexpected): {ex.GetType().Name}: {ex.Message}");
            _fail++;
        }

        Console.WriteLine();
        Console.WriteLine($"=== codex-umbrella-coverage-guard: {_pass} passed, {_fail} failed -> {(_fail == 0 ? "PASS" : "FAIL")} ===");
        return _fail == 0 ? 0 : 1;
    }

    static HashSet<string> Empty() => new(StringComparer.Ordinal);

    /// <summary>Reflect every [McpServerTool] Name off the housecarl-mcp assembly (anchored via a known tool type).</summary>
    static HashSet<string> McpToolNames()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in typeof(ReadTools).Assembly.GetTypes())
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var a = m.GetCustomAttribute<McpServerToolAttribute>(inherit: false);
                if (a?.Name is { Length: > 0 } n) names.Add(n);
            }
        return names;
    }

    static List<string> SkillSlugs()
    {
        var dir = Path.Combine(".claude", "skills");
        return Directory.Exists(dir)
            ? Directory.GetDirectories(dir).Select(d => Path.GetFileName(d)!).Where(s => !string.IsNullOrEmpty(s))
                       .OrderBy(s => s, StringComparer.Ordinal).ToList()
            : new();
    }

    /// <summary>Required names not present in the umbrella text and not allow-listed. Ordinal substring match — no
    /// tool or skill name is a substring of another, so a plain Contains is unambiguous.</summary>
    static List<string> MissingRefs(string umbrella, IEnumerable<string> required, ISet<string> allow)
        => required.Where(r => !allow.Contains(r) && !umbrella.Contains(r, StringComparison.Ordinal))
                   .OrderBy(r => r, StringComparer.Ordinal).ToList();

    static void Check(string label, bool ok, List<string> detail, bool redArm = false)
    {
        Console.WriteLine($"   {label,-72}: {(ok ? "PASS" : "FAIL")}");
        if (!ok)
        {
            if (detail.Count == 0)
                Console.WriteLine(redArm ? "        - (the checker reported NO violation — it is toothless)" : "        - (no detail)");
            foreach (var d in detail.Take(20)) Console.WriteLine($"        - {d}");
        }
        if (ok) _pass++; else _fail++;
    }
}
