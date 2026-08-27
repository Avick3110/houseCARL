using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace HousecarlGenerator;

/// <summary>
/// REFUSAL-COMPLETENESS GUARD (#403) — the read surface's refusal grammar is complete, and STAYS complete.
///
/// <para><b>Why it exists.</b> The refusal grammar shipped as 81 explicit <c>Wire.Refuse</c> call sites, and the
/// set of sites needing one was found by running a regex over two hand-named files. Round 1 of the pre-PR review
/// found that population short by fourteen — twelve refusals produced inside a helper and returned verbatim, a
/// ternary whose two arms were both bare (the regex wants <c>return "error:</c> on ONE line; that line reads
/// <c>return comparisonForm</c>), and a whole json-capable read tool that was never in the file set. Reviewer 3
/// then proved by mutation that reverting ALL fourteen <c>ReadTools</c> call sites to prose left the entire
/// 131-probe suite green. So the grammar had neither completeness by construction nor a guard that noticed its
/// absence — CLAUDE.md §3's hand-wired-coverage failure mode, restated on the response layer.</para>
///
/// <para><b>Two binding properties, both load-bearing.</b></para>
/// <list type="number">
///   <item><b>It PARSES.</b> Roslyn walks return statements; there is no regex anywhere in the enumeration. The
///   thing that defeated the sweep was a construct spanning lines, which is precisely what a line-oriented
///   pattern cannot see and what a syntax tree sees for free. <see cref="PreFixTernaryFixture"/> preserves that
///   exact pre-fix ternary as a permanent known-RED fixture: <c>INV-FIXTURE-RED</c> asserts the enumerator flags
///   it. <b>If that fixture ever passes, the guard is broken, not the tree</b> — a checker whose known-red case
///   comes back green is a broken checker, never a clean surface (§11).</item>
///   <item><b>The population is DERIVED, never listed.</b> <see cref="DerivePopulation"/> finds every
///   <c>Guard.Tool</c> body that consults the format machinery (<c>Wire.WantsJson</c> /
///   <c>Wire.CrossQueryFormat</c>) and takes THAT as the surface under guard. A new json-capable read tool
///   enrols itself; a hand-named file set is what let <c>housecarl_check</c> sit outside the trace with a bare
///   refusal and its own comment stating the rule it was breaking.</item>
/// </list>
///
/// <para><b>The allowlist is small BECAUSE the population is derived.</b> Of the 26 refusal returns that stay
/// bare on the tree, only the ones actually inside a format-consuming tool body are the guard's business; the
/// text-lane renderers and <c>ParsePole</c>'s own returns are not in a tool body at all, and their call sites —
/// which ARE in one — are covered like any other site. Every entry below cites the settled decision that makes
/// it correct, so an exception cannot be added without naming the ruling that permits it.</para>
///
/// <para>Self-contained: reads source files, no corpus and no MO2 instance, so it must run from the repo root.
/// Run: <c>dotnet run --project src/housecarl-generator -- refusal-completeness-guard</c></para>
/// </summary>
public static class RefusalCompletenessGuardProbe
{
    static int _pass, _fail;
    static readonly CSharpParseOptions Options = new(LanguageVersion.Preview);

    /// <summary>Renderers that give a returned refusal its shape. A return wrapped in one of these has answered
    /// the transport question; anything else carrying a refusal sentence has not.
    ///
    /// <para><c>Refuse</c> is listed unqualified as well as <c>Wire.Refuse</c>: the WRITE tools reach the same
    /// verb through a local helper of that name, and the shape a refusal gets does not depend on how the call
    /// spells its receiver. Matching the verb rather than the receiver is what lets the population stay derived
    /// — a surface that renders refusals correctly through its own helper is not a hole.</para></summary>
    static readonly string[] ApprovedRenderers = { "Wire.Refuse", "JsonWire.Render", "Wire.Render", "Refuse" };

    /// <summary>The refusals that stay bare inside a format-consuming tool body, each with the settled decision
    /// that rules it correct. Keyed <c>file:sentence-fragment</c> rather than by line, so ordinary edits above a
    /// site do not rot the list while a CHANGE to the site still trips it.</summary>
    static readonly (string File, string Fragment, string Decision, string Why)[] Allow =
    {
        // The format= parse refusal itself. Surface-wide ("*") on purpose: the population derives past the read
        // lane into the write tools, and the rule does not change there — a call whose format VALUE did not parse
        // has not told anyone which shape it wanted, so there is no known render to answer in. ApplyTools states
        // exactly that in its own comment at the site.
        ("*", "ferr",   "#7", "the format= parse refusal cannot know the shape the caller wanted"),
        ("*", "fmtErr", "#7", "the format= parse refusal cannot know the shape the caller wanted"),
        // dense is a textual transport, so its two refusals are text by definition rather than by omission.
        ("RecordsTools.cs", "format='dense' is the scan lane's columnar form",     "#7", "dense is a textual transport"),
        ("RecordsTools.cs", "format='dense' is the in-order scan's columnar form", "#7", "dense is a textual transport"),
    };

    /// <summary>THE KNOWN-RED FIXTURE — <c>RecordsTools.cs:223</c> as it stood BEFORE the fold: a return whose
    /// two ternary arms are both bare refusal sentences, with a wrapped statement on either side. The sweep that
    /// missed it needed the literal on the same line as the <c>return</c>. The enumerator must flag this; the
    /// arm that says so is the guard's own proof that it can still see what a regex could not.</summary>
    const string PreFixTernaryFixture = """
        static class Fixture
        {
            static string Body()
            {
                bool json = Wire.WantsJson(format, out var ferr);
                if (a) return Wire.Refuse(json, $"error: wrapped above.");
                if (form is not ("fields" or "everything"))
                    return comparisonForm
                        ? $"error: project.depth belongs to the 'fields'/'everything' forms."
                        : $"error: project.depth expands field contents.";
                if (b) return Wire.Refuse(json, $"error: wrapped below.");
                return Ok();
            }
        }
        """;

    /// <summary>The counter-fixture: the SAME shape, correctly wrapped. An enumerator that flags everything would
    /// also flag this, and would be useless while looking vigilant — so its silence here is an arm too.</summary>
    const string PostFixTernaryFixture = """
        static class Fixture
        {
            static string Body()
            {
                bool json = Wire.WantsJson(format, out var ferr);
                if (form is not ("fields" or "everything"))
                    return Wire.Refuse(json, comparisonForm
                        ? $"error: project.depth belongs to the 'fields'/'everything' forms."
                        : $"error: project.depth expands field contents.");
                return Ok();
            }
        }
        """;

    public static int RunGuard(string[] args)
    {
        _pass = _fail = 0;
        Console.WriteLine("=== refusal-completeness-guard: the read surface's refusal grammar, by construction ===");
        Console.WriteLine();

        // ---- 1. the enumerator itself, against fixtures whose verdict is known by hand ------------------
        Console.WriteLine("--- 1: the enumerator can see what the sweep could not ---");
        var redHits = Enumerate("<fixture>", PreFixTernaryFixture, out var fixErrors);
        Check(fixErrors.Count == 0, $"the known-RED fixture parses ({string.Join("; ", fixErrors)})");
        Check(redHits.Count == 1,
              $"KNOWN-RED FIXTURE: the pre-fix ternary is FLAGGED (found {redHits.Count}, expected 1) — if this "
              + "passes, the enumerator is broken, not the tree");
        var greenHits = Enumerate("<fixture>", PostFixTernaryFixture, out _);
        Check(greenHits.Count == 0,
              $"…and the wrapped form of the same shape is NOT flagged (found {greenHits.Count}, expected 0) — "
              + "an enumerator that flags everything proves nothing");

        // ---- 2. the population, derived from the artifact ----------------------------------------------
        Console.WriteLine();
        Console.WriteLine("--- 2: the population derives itself ---");
        var srcDir = Path.Combine(Directory.GetCurrentDirectory(), "src", "housecarl-mcp");
        if (!Directory.Exists(srcDir))
        {
            Check(false, $"there is no '{srcDir}' to scan — this guard reads source, so the CWD must be the repo root");
            return Done();
        }

        var population = DerivePopulation(srcDir, out var parseErrors, out var bodies);
        Check(parseErrors.Count == 0,
              $"every scanned file PARSES — a file that does not parse has untrustworthy returns ({string.Join("; ", parseErrors)})");
        Check(population.Count > 0, "the derived population is non-empty (Guard.Tool bodies consulting the format machinery)");
        Console.WriteLine($"    population: {bodies} tool body/bodies across {population.Count} file(s) — "
                        + string.Join(", ", population.OrderBy(f => f)));

        // ---- 3. the residue: every flagged site must cite a ruling -------------------------------------
        Console.WriteLine();
        Console.WriteLine("--- 3: every bare refusal in the population cites a settled decision ---");
        var flagged = new List<Hit>();
        foreach (var file in Directory.EnumerateFiles(srcDir, "*.cs"))
        {
            var text = File.ReadAllText(file);
            foreach (var h in Enumerate(Path.GetFileName(file), text, out _, populationOnly: true))
                flagged.Add(h);
        }

        var unexplained = flagged.Where(h => Match(h) is null).ToList();
        foreach (var h in unexplained)
            Console.WriteLine($"    UNEXPLAINED  {h.File}:{h.Line}  {Trim(h.Sentence)}");
        Check(unexplained.Count == 0,
              $"no bare whole-call refusal in the population is without a ruling (found {unexplained.Count})");

        // A stale allowlist is a silent hole: it says a site is fine when the site is gone or has changed shape.
        var unused = Allow.Where(a => !flagged.Any(h => Matches(h, a))).ToList();
        foreach (var a in unused)
            Console.WriteLine($"    STALE ALLOWLIST ENTRY  {a.File} :: '{a.Fragment}' ({a.Decision}) matches nothing");
        Check(unused.Count == 0,
              $"every allowlist entry still names a live site (found {unused.Count} stale)");

        Console.WriteLine();
        Console.WriteLine($"    {flagged.Count} bare refusal return(s) in the population, all ruled: "
                        + string.Join(", ", Allow.Select(a => a.Decision).Distinct()));
        return Done();
    }

    static int Done()
    {
        Console.WriteLine();
        Console.WriteLine(_fail == 0
            ? "[refusal-completeness-guard] PASS — every reachable refusal in the derived population is shaped or ruled."
            : "[refusal-completeness-guard] FAIL — see the lines above.");
        Console.WriteLine($"=== refusal-completeness-guard: {_pass} passed, {_fail} failed -> {(_fail == 0 ? "PASS" : "FAIL")} ===");
        return _fail == 0 ? 0 : 1;
    }

    // ================= the enumeration =================

    internal readonly record struct Hit(string File, int Line, string Sentence);

    /// <summary>Every <c>Guard.Tool</c> body that consults the format machinery — the surface this guard polices,
    /// derived rather than named. Returns the FILES those bodies live in; <paramref name="bodies"/> counts them.</summary>
    static HashSet<string> DerivePopulation(string srcDir, out List<string> parseErrors, out int bodies)
    {
        parseErrors = new List<string>();
        bodies = 0;
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(srcDir, "*.cs"))
        {
            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(path), Options);
            foreach (var d in tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error))
                parseErrors.Add($"{Path.GetFileName(path)} {d.Id} line {d.Location.GetLineSpan().StartLinePosition.Line + 1}");
            foreach (var body in ToolBodies(tree.GetRoot()))
            {
                bodies++;
                files.Add(Path.GetFileName(path));
                _ = body;
            }
        }
        return files;
    }

    /// <summary>A <c>Guard.Tool(...)</c> invocation's body lambda, when that body consults the format machinery.
    /// This is the definition of "on the json-capable read surface" — a tool that never asks about the transport
    /// has no transport to honour, which is why <c>housecarl_effect_chain</c> is correctly absent without an
    /// allowlist entry, and why a NEW json-capable tool enrols itself the day it is written.</summary>
    static IEnumerable<SyntaxNode> ToolBodies(SyntaxNode root)
    {
        foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (inv.Expression is not MemberAccessExpressionSyntax ma) continue;
            if (ma.Name.Identifier.Text != "Tool" || ma.Expression.ToString() != "Guard") continue;
            foreach (var arg in inv.ArgumentList.Arguments)
            {
                var e = arg.Expression;
                if (e is not (ParenthesizedLambdaExpressionSyntax or SimpleLambdaExpressionSyntax
                              or AnonymousMethodExpressionSyntax)) continue;
                if (ConsultsFormat(e)) yield return e;
            }
        }
    }

    static bool ConsultsFormat(SyntaxNode body)
        => body.DescendantNodes().OfType<InvocationExpressionSyntax>()
               .Any(i => i.Expression is MemberAccessExpressionSyntax m
                      && m.Expression.ToString() == "Wire"
                      && m.Name.Identifier.Text is "WantsJson" or "CrossQueryFormat");

    /// <summary>Every return in <paramref name="src"/> that hands a caller an UNSHAPED refusal.
    ///
    /// <para>Two shapes count, and both are structural — no pattern is matched against source text.
    /// (a) the returned expression carries a string literal whose value opens with the refusal prefix, at ANY
    /// depth, so a ternary arm, a concatenation and an interpolation are all seen; (b) the returned expression is
    /// a bare identifier introduced by an <c>out var</c> or a deconstruction in the same body — the shape of a
    /// refusal produced inside a helper and handed straight back, which is how twelve of the fourteen escaped.
    /// Either way, a return already wrapped in an approved renderer is not a hit.</para>
    ///
    /// <para><paramref name="populationOnly"/> restricts the walk to derived tool bodies; the fixtures run with it
    /// off, since a fixture is a body in its own right.</para></summary>
    internal static List<Hit> Enumerate(string file, string src, out List<string> parseErrors,
                                        bool populationOnly = false)
    {
        var tree = CSharpSyntaxTree.ParseText(src, Options);
        parseErrors = tree.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => $"{d.Id} line {d.Location.GetLineSpan().StartLinePosition.Line + 1}")
            .ToList();

        var root = tree.GetRoot();
        var scopes = populationOnly
            ? ToolBodies(root).ToList()
            : new List<SyntaxNode> { root };

        var hits = new List<Hit>();
        var seen = new HashSet<int>();
        foreach (var scope in scopes)
        {
            // Names bound by an `out var X` ARGUMENT — the shape of a refusal produced inside a helper and handed
            // straight back. Deliberately NOT every SingleVariableDesignation: `is { } prompt` binds the same way
            // and the MO2-not-configured prompt it binds is trained guidance addressed to the model, not a
            // refusal sentence — it is returned bare by the whole tool surface, write tools included, and is a
            // separate surface-wide question rather than part of this grammar.
            var outNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var arg in scope.DescendantNodes().OfType<ArgumentSyntax>())
            {
                if (!arg.RefKindKeyword.IsKind(SyntaxKind.OutKeyword)) continue;
                if (arg.Expression is DeclarationExpressionSyntax { Designation: SingleVariableDesignationSyntax sv })
                    outNames.Add(sv.Identifier.Text);
            }

            foreach (var ret in scope.DescendantNodes().OfType<ReturnStatementSyntax>())
            {
                var expr = ret.Expression;
                if (expr is null) continue;
                // `return (xerr);` is `return xerr;` with noise. Unwrap before asking what shape it is, so a
                // refusal cannot escape the net behind a pair of brackets.
                while (expr is ParenthesizedExpressionSyntax paren) expr = paren.Expression;
                if (IsWrapped(expr)) continue;

                string? sentence = RefusalLiteral(expr);
                if (sentence is null && expr is IdentifierNameSyntax id && outNames.Contains(id.Identifier.Text))
                    sentence = id.Identifier.Text;
                if (sentence is null) continue;
                if (TransportAlreadyLeft(ret)) continue;

                int line = ret.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                if (seen.Add(line)) hits.Add(new Hit(file, line, sentence));
            }
        }
        return hits;
    }

    /// <summary>Has the json transport already returned before this statement?
    ///
    /// <para>The text-lane twin shape: <c>if (json) return JsonWire.RenderX(o); … if (!o.Success) return
    /// "error: " + o.Error;</c>. The second return is prose on purpose and is UNREACHABLE on a json call, because
    /// the json arm left several lines above it. Recognising that structurally is what keeps the allowlist honest
    /// — every one of these would otherwise need a hand-written exception saying "the transport already left",
    /// and a guard whose exceptions restate a structural fact is a guard that has stopped deriving.</para>
    ///
    /// <para>Deliberately narrow: only an <c>if (json) return …;</c> with no else, appearing EARLIER in an
    /// enclosing block. A conditional that merely mentions <c>json</c> does not count.</para></summary>
    static bool TransportAlreadyLeft(ReturnStatementSyntax ret)
    {
        SyntaxNode? node = ret;
        while (node is not null)
        {
            if (node.Parent is BlockSyntax block)
            {
                foreach (var st in block.Statements)
                {
                    if (st == node) break;               // only statements BEFORE this one count
                    if (st is not IfStatementSyntax ifs || ifs.Else is not null) continue;
                    if (ifs.Condition is not IdentifierNameSyntax cond || cond.Identifier.Text != "json") continue;
                    var inner = ifs.Statement is BlockSyntax b
                        ? b.Statements.LastOrDefault()
                        : ifs.Statement;
                    if (inner is ReturnStatementSyntax) return true;
                }
            }
            node = node.Parent;
        }
        return false;
    }

    /// <summary>Is this return already shaped? True when an approved renderer is the OUTERMOST call, or wraps the
    /// literal-bearing part of a conditional (<c>json ? JsonWire.X : Wire.X</c> is shaped on both arms).</summary>
    static bool IsWrapped(ExpressionSyntax expr)
    {
        foreach (var inv in expr.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
        {
            var name = inv.Expression.ToString();
            if (ApprovedRenderers.Any(r => name.StartsWith(r, StringComparison.Ordinal))) return true;
        }
        return false;
    }

    /// <summary>The first refusal sentence carried anywhere inside the expression, or null. Uses the compiler's
    /// own decoding, so an escape or an interpolation hole cannot hide the prefix.</summary>
    static string? RefusalLiteral(ExpressionSyntax expr)
    {
        foreach (var node in expr.DescendantNodesAndSelf())
        {
            string? v = node switch
            {
                LiteralExpressionSyntax lit when lit.IsKind(SyntaxKind.StringLiteralExpression)
                    => lit.Token.ValueText,
                InterpolatedStringTextSyntax txt => txt.TextToken.ValueText,
                _ => null,
            };
            if (v is not null && v.StartsWith("error: ", StringComparison.Ordinal)) return v;
        }
        return null;
    }

    // ================= allowlist matching =================

    static (string File, string Fragment, string Decision, string Why)? Match(Hit h)
    {
        foreach (var a in Allow)
            if (Matches(h, a)) return a;
        return null;
    }

    static bool Matches(Hit h, (string File, string Fragment, string Decision, string Why) a)
        => (a.File == "*" || string.Equals(h.File, a.File, StringComparison.OrdinalIgnoreCase))
           && h.Sentence.Contains(a.Fragment, StringComparison.Ordinal);

    static string Trim(string s) => s.Length <= 110 ? s : s[..110] + "…";

    static void Check(bool ok, string label)
    {
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {label}");
        if (ok) _pass++; else _fail++;
    }
}
