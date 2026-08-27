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
///   <item><b>The population is DERIVED, never listed.</b> <see cref="DerivePopulation"/> takes every scope in
///   which the transport is DECIDED — a <c>Guard.Tool</c> body consulting <c>Wire.WantsJson</c> /
///   <c>Wire.CrossQueryFormat</c> — or HANDED over — a method or local function declaring a <c>bool json</c> or
///   a <c>QueryFormat</c> parameter. A new json-capable read tool enrols itself, and so does a new helper given
///   the transport; a hand-named file set is what let <c>housecarl_check</c> sit outside the trace with a bare
///   refusal and its own comment stating the rule it was breaking, and the tool-body-only draft of this net is
///   what let <c>RenderListAggregate</c> revert to prose while the guard stayed green.</item>
/// </list>
///
/// <para><b>The allowlist is small BECAUSE the population is derived.</b> Of the refusal returns that stay bare
/// on the tree, only the ones in a policed scope are the guard's business, and a text-lane return inside one is
/// excluded STRUCTURALLY rather than by hand when the json arm returned above it (<see cref="TransportAlreadyLeft"/>).
/// <c>ParsePole</c>'s own returns are in no policed scope at all — no transport is decided in it or handed to it —
/// and its call sites, which ARE policed, are covered like any other site. Every entry below cites the settled
/// decision that makes it correct, so an exception cannot be added without naming the ruling that permits it.</para>
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

    /// <summary>THE SECOND KNOWN-RED FIXTURE — <c>ReadTools.cs:101</c> under Aaron's gate-review mutation: the
    /// refusal <c>Artifacts.ExpandListInput</c> hands back through a tuple, returned bare. This shape defeated the
    /// net until the deconstruction designation joined it, and the miss was invisible to every other cell in the
    /// sweep because <c>is { } verr</c> — the shape one line below it in the same file — WAS in the net. The
    /// enumerator must flag this; eight live sites carry it.</summary>
    const string HelperTupleFixture = """
        static class Fixture
        {
            static string Body()
            {
                bool json = Wire.WantsJson(format, out var ferr);
                var (toks, demand, echoSrc, xerr) = Artifacts.ExpandListInput(formids, "formids");
                if (xerr is not null) return xerr;
                return Ok();
            }
        }
        """;

    /// <summary>Its counter-fixture: the same tuple, wrapped. Silence here is an arm too.</summary>
    const string WrappedTupleFixture = """
        static class Fixture
        {
            static string Body()
            {
                bool json = Wire.WantsJson(format, out var ferr);
                var (toks, demand, echoSrc, xerr) = Artifacts.ExpandListInput(formids, "formids");
                if (xerr is not null) return Wire.Refuse(json, xerr);
                return Ok();
            }
        }
        """;

    /// <summary>THE POPULATION FIXTURE — three helpers, none of them a <c>Guard.Tool</c> body, enumerated with
    /// the population filter ON. Two were HANDED the transport (a <c>bool json</c>, a <c>QueryFormat</c>) and
    /// must be policed; the third has none in scope and must not be, because its shape is decided at its call
    /// sites (<c>ParsePole</c>'s posture, settled #8). This is the arm that proves the derivation itself rather
    /// than asserting it — the <c>QueryFormat</c> half has no live site today, so nothing else would.</summary>
    const string TransportHelperFixture = """
        static class Fixture
        {
            static string Render(string groupBy, bool json)
            {
                return $"error: project.group_by='{groupBy}' is not a count key.";
            }

            static string Scan(QueryFormat fmt)
            {
                return $"error: the scan lane refused.";
            }

            static string NoTransport(string pole)
            {
                return $"error: a helper with no transport in scope decides nothing here.";
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

        var tupleRed = Enumerate("<fixture>", HelperTupleFixture, out var tupleErrors);
        Check(tupleErrors.Count == 0, $"the tuple-deconstruction fixture parses ({string.Join("; ", tupleErrors)})");
        Check(tupleRed.Count == 1,
              $"KNOWN-RED FIXTURE: a refusal handed back through a deconstructed tuple is FLAGGED "
              + $"(found {tupleRed.Count}, expected 1) — the binding is a designation, not an out argument");
        var tupleGreen = Enumerate("<fixture>", WrappedTupleFixture, out _);
        Check(tupleGreen.Count == 0,
              $"…and the wrapped form of the same tuple is NOT flagged (found {tupleGreen.Count}, expected 0)");

        // ---- 2. the population, derived from the artifact ----------------------------------------------
        Console.WriteLine();
        Console.WriteLine("--- 2: the population derives itself ---");
        var srcDir = Path.Combine(Directory.GetCurrentDirectory(), "src", "housecarl-mcp");
        if (!Directory.Exists(srcDir))
        {
            Check(false, $"there is no '{srcDir}' to scan — this guard reads source, so the CWD must be the repo root");
            return Done();
        }

        var handed = Enumerate("<fixture>", TransportHelperFixture, out var handedErrors, populationOnly: true);
        Check(handedErrors.Count == 0, $"the transport-helper fixture parses ({string.Join("; ", handedErrors)})");
        Check(handed.Count == 2,
              $"a helper HANDED the transport is in the population — both spellings (found {handed.Count}, "
              + "expected 2: the `bool json` helper and the `QueryFormat` one)");
        Check(handed.All(h => !h.Sentence.Contains("decides nothing here", StringComparison.Ordinal)),
              "…and a helper with NO transport in scope is not (settled #8 — its shape is decided at its call sites)");

        var population = DerivePopulation(srcDir, out var parseErrors, out var bodies);
        Check(parseErrors.Count == 0,
              $"every scanned file PARSES — a file that does not parse has untrustworthy returns ({string.Join("; ", parseErrors)})");
        Check(population.Count > 0, "the derived population is non-empty (bodies that decide the transport, and helpers handed it)");
        Console.WriteLine($"    population: {bodies} policed scope(s) across {population.Count} file(s) — "
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

        // ---- 4. the discriminant means ONE thing, structurally -----------------------------------------
        Console.WriteLine();
        Console.WriteLine("--- 4: `ok` is the discriminant and nothing else ---");
        var okWrites = OkKeyWrites(Path.Combine(srcDir, "JsonWire.cs"));
        var strays = okWrites.Where(w => !w.Legal).ToList();
        foreach (var w in strays)
            Console.WriteLine($"    STRAY `ok`  JsonWire.cs:{w.Line}  writes ok as `{Trim(w.Value)}` in {w.Method}()");
        Check(strays.Count == 0,
              $"every `ok` key is either the refusal discriminant (literal false) or a write outcome (o.Success) "
              + $"— a SERVED read document that writes `ok` is the #403 round-1 collision (found {strays.Count})");
        Console.WriteLine($"    {okWrites.Count} `ok` write(s): "
                        + string.Join(", ", okWrites.GroupBy(w => w.Value).Select(g => $"{g.Count()}x {g.Key}")));

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

    /// <summary>Every scope on the json-capable surface, derived from the artifact in two ways — a scope is
    /// policed when the transport is DECIDED in it or HANDED to it.
    ///
    /// <para>(a) A <c>Guard.Tool(...)</c> body lambda that consults the format machinery. A tool that never asks
    /// about the transport has no transport to honour, which is why <c>housecarl_effect_chain</c> is correctly
    /// absent without an allowlist entry, and why a NEW json-capable tool enrols itself the day it is written.</para>
    ///
    /// <para>(b) Any method or local function DECLARING a transport parameter — a <c>bool json</c>, or a
    /// <c>QueryFormat</c>. Aaron's gate review found (a) alone insufficient: a helper handed the transport
    /// produces its refusal AND returns it inside itself, so no tool-body return ever carries the sentence and
    /// the site sat outside the net entirely (<c>RecordsTools.RenderListAggregate</c>, reverted to prose, left
    /// the guard green). Deriving on the parameter closes it without naming a file — a helper that was given the
    /// transport was given the obligation with it. <c>ParsePole</c> stays correctly outside both: it has no
    /// transport in scope, so its shape is decided at its call sites, which ARE in a tool body.</para></summary>
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
        foreach (var m in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            if (CarriesTransport(m.ParameterList)) yield return m;
        foreach (var f in root.DescendantNodes().OfType<LocalFunctionStatementSyntax>())
            if (CarriesTransport(f.ParameterList)) yield return f;
    }

    /// <summary>Does this signature take the transport as an argument? <c>bool json</c> by name and type — the
    /// spelling the whole read surface uses — or a <c>QueryFormat</c> of any name, which carries the three-way
    /// text/json/dense answer and so is unambiguous on its type alone.</summary>
    static bool CarriesTransport(ParameterListSyntax? ps)
        => ps is not null && ps.Parameters.Any(p =>
               (p.Type?.ToString() is "bool" or "bool?" && p.Identifier.Text == "json")
               || p.Type?.ToString() is "QueryFormat" or "Wire.QueryFormat");

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
            // Names bound to a string a HELPER produced — the shape of a refusal handed straight back, which is
            // how twelve of the fourteen escaped. THREE bindings carry it on this surface, and each is its own
            // syntax: an `out var X` argument (Wire.WantsJson), a tuple deconstruction `var (…, X) = Helper(…)`
            // (Artifacts.ExpandListInput), and an `is { } X` recursive pattern (Artifacts.ValidateToFile). Each
            // one was missing from a draft of this net and each miss was found the same way — by a mutation cell
            // that reverted the site and came back with nothing flagged. ExpandListInput's tuple is NOT an out
            // argument: its left side is a DeclarationExpressionSyntax carrying a ParenthesizedVariableDesignation,
            // so the `out var` walk below cannot see it and an earlier comment here said otherwise.
            //
            // One binding is excluded, structurally and by name rather than by file: a designation bound from
            // ConfigPromptOrNull(). What that returns is trained guidance addressed to the model when no MO2
            // instance is configured — not a refusal sentence, returned bare by the whole tool surface including
            // the write tools, and a separate surface-wide question rather than part of this grammar.
            var outNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var arg in scope.DescendantNodes().OfType<ArgumentSyntax>())
            {
                if (!arg.RefKindKeyword.IsKind(SyntaxKind.OutKeyword)) continue;
                if (arg.Expression is DeclarationExpressionSyntax { Designation: SingleVariableDesignationSyntax sv })
                    outNames.Add(sv.Identifier.Text);
            }
            foreach (var decl in scope.DescendantNodes().OfType<DeclarationExpressionSyntax>())
            {
                if (decl.Designation is not ParenthesizedVariableDesignationSyntax pd) continue;
                if (decl.Parent is AssignmentExpressionSyntax asg
                    && asg.Right.ToString().Contains("ConfigPromptOrNull", StringComparison.Ordinal)) continue;
                foreach (var v in pd.DescendantNodesAndSelf().OfType<SingleVariableDesignationSyntax>())
                    outNames.Add(v.Identifier.Text);
            }
            foreach (var pat in scope.DescendantNodes().OfType<IsPatternExpressionSyntax>())
            {
                if (pat.Pattern is not RecursivePatternSyntax { Designation: SingleVariableDesignationSyntax pv })
                    continue;
                if (pat.Expression.ToString().Contains("ConfigPromptOrNull", StringComparison.Ordinal)) continue;
                outNames.Add(pv.Identifier.Text);
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

    // ================= the discriminant =================

    internal readonly record struct OkWrite(int Line, string Method, string Value, bool Legal);

    /// <summary>Every write of an <c>ok</c> PROPERTY in the json renderer, with what it writes and whether that
    /// is a legal meaning.
    ///
    /// <para>Exactly two meanings are legal, and they are told apart by the ARGUMENT, structurally:</para>
    /// <list type="bullet">
    ///   <item>a literal <c>false</c> — the refusal discriminant, written in one place
    ///   (<c>WriteRefusal</c>);</item>
    ///   <item><c>o.Success</c> — the WRITE surface's outcome flag, which carries both verdicts by long-standing
    ///   contract and is not this lane's business.</item>
    /// </list>
    ///
    /// <para>Anything else writing that key is the #403 round-1 collision: <c>RenderCounts</c> wrote
    /// <c>WriteNumber("ok", ok)</c> — a resolved-row COUNT — onto a served census, so a consumer branching on the
    /// discriminant read an answered call as a refused one, and a typed consumer could not parse it. Held here as
    /// a structural fact rather than a runtime sample, because the collision was invisible to every runtime arm on
    /// the surface: the document it appeared on was never one the refusal probes rendered.</para></summary>
    static List<OkWrite> OkKeyWrites(string jsonWirePath)
    {
        var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(jsonWirePath), Options);
        var outp = new List<OkWrite>();
        foreach (var inv in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (inv.Expression is not MemberAccessExpressionSyntax ma) continue;
            if (!ma.Name.Identifier.Text.StartsWith("Write", StringComparison.Ordinal)) continue;
            var args = inv.ArgumentList.Arguments;
            if (args.Count < 2) continue;
            if (args[0].Expression is not LiteralExpressionSyntax key
                || !key.IsKind(SyntaxKind.StringLiteralExpression)
                || key.Token.ValueText != "ok") continue;

            var value = args[1].Expression.ToString();
            bool legal = value == "false"                                  // the discriminant
                      || value.EndsWith(".Success", StringComparison.Ordinal);  // a write outcome
            var method = inv.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault()?.Identifier.Text
                         ?? "<file scope>";
            outp.Add(new OkWrite(inv.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                                 method, value, legal));
        }
        return outp;
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
