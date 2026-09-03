using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;
using Xunit.Abstractions;

namespace HousecarlMcpTests;

/// <summary>
/// THE PROSE GUARD — a fact test may not assert a fragment of prose the product composed (#492).
///
/// <para><b>The class it closes.</b> An assertion over rendered text stays green when the thing it names is
/// broken: the span cannot occur by fixture construction, so a negative is vacuous; only a prefix is pinned,
/// so a tail sabotage survives; a second render branch or a second RECORD emits the same line, so the arm is
/// satisfied by something it is not about. The interim discipline — whole composed lines anchored to the
/// record, sabotage per branch, the fold-population sweep — is enforced by eye and by review rounds, and the
/// rounds kept finding the class. Under CLAUDE.md §3's third cornerstone that is the signal to make the
/// shape unwritable rather than to strengthen the check.</para>
///
/// <para><b>Every population here is DERIVED.</b> The assertion sites come from a Roslyn parse of the test
/// sources; the wire vocabulary is every JSON property name the product writes; the fixture values are read
/// off the fixture files' own initialisers; the fixture symbols are the types those files declare plus the
/// fields holding them. Nothing is a list in this file — a hand-listed population is short by exactly what
/// nobody thought of, and the guard stays green over the gap.</para>
///
/// <para><b>Parse only.</b> <c>CSharpSyntaxTree.ParseText</c>, no workspace, no compilation, no semantic
/// model, so this needs no MSBuild and runs in well under a second. The rule is a syntactic question about
/// literals and argument positions, and a syntax tree answers it exactly.</para>
///
/// <para><b>The exemption is not a list and not a bare marker.</b> A class marked
/// <see cref="SentenceCatalogueAttribute"/> is exempt from the prose rule and subject to a STRICTER one:
/// every assertion's subject must be a catalogue expression, never a tool response. A fact test's subject is
/// a tool response by definition, so the marker cannot be sprayed onto one — the marker's own rule fails it.
/// </para>
/// </summary>
[Trait("tier", "unit")]
public sealed class TestProseGuardTests
{
    readonly ITestOutputHelper _out;
    public TestProseGuardTests(ITestOutputHelper output) => _out = output;

    /// <summary>The one file a reshaping PR edits, and the file every failure below names.</summary>
    public static string BaselinePath =>
        Path.Combine(HarnessPaths.RepoRoot, "src", "housecarl-mcp-tests", "test-prose-baseline.json");

    // ---- the rule, stated once -------------------------------------------------------------------------
    //
    // AXIS 1 — the EXPECTED expression (argument 0 of the eight string-assertion forms):
    //   CAT    references a sentence-catalogue symbol (ReadSentences. / WriteSentences.)
    //   LIT    carries at least one string literal, or an interpolated string's literal segments
    //   NOLIT  carries none (a number, an enum, a variable, a typeof, a FormKey, a collection)
    // AXIS 2 — is every literal a DECLARED WIRE TOKEN? (derived: every JSON property name the product writes)
    // AXIS 3 — the SUBJECT (argument 1): a document/collection accessor, or rendered text.
    //
    // THE BUCKETS, first match wins:
    //   ii  CATALOGUE   AXIS1 = CAT — an identity check, whole-line by construction
    //   iii STRUCTURAL  AXIS1 = NOLIT, or the expected value is a collection, or AXIS3 = document accessor
    //                   (and not read back with .GetString()), or AXIS2 = every literal is a wire token
    //   iv  FIXTURE     the literals are whitespace-free AND the expression names a fixture symbol, or each
    //                   literal is declared as a value in a fixture file
    //   i   PROSE       everything else — #492's class, and the only bucket the baseline counts

    static readonly string[] Forms =
        { "Contains", "DoesNotContain", "StartsWith", "EndsWith", "Equal", "NotEqual", "Matches", "DoesNotMatch" };

    static readonly Regex CatalogueRe = new(@"\b(ReadSentences|WriteSentences)\s*\.", RegexOptions.Compiled);

    static readonly Regex DocRe = new(
        @"GetProperty\s*\(|RootElement|\.GetInt32\s*\(|\.GetBoolean\s*\(|\.GetDouble\s*\(|" +
        @"\.ValueKind|\.EnumerateArray|\.EnumerateObject|\.Count\b|\.Length\b|new\[\]\s*\{|" +
        @"\.ToArray\s*\(|\.Select\s*\(|\.Keys\b|\.Values\b|\.ToList\s*\(", RegexOptions.Compiled);

    static readonly Regex CollectionRe = new(
        @"new\[\]\s*\{|new\s+\w+\[\]|\.ToHashSet|\.ToArray\s*\(", RegexOptions.Compiled);

    /// <summary>Files whose string literals are fixture VALUES rather than product prose.</summary>
    static readonly Regex FixtureFileRe = new(
        @"(World|Fixtures|TestBase)\.cs$|^(PexWriter|HarnessPaths|RepoProjects|HeldOpen)\.cs$", RegexOptions.Compiled);

    // ---- one assertion site ----------------------------------------------------------------------------

    public sealed record Site(string File, int Line, string Form, string Bucket,
                              string Expected, string Actual, IReadOnlyList<string> Literals);

    // Every memo below is guarded: the three arms in this class run sequentially, but nothing stops a future
    // caller in another class from asking, and a torn Dictionary read fails toward a wrong population.
    static readonly object Memo = new();
    static IReadOnlyList<Site>? _sites;
    static int _skipped, _hoisted;

    public static IReadOnlyList<Site> Sites()
    {
        lock (Memo)
        {
            return _sites ??= DeriveSites();
        }
    }

    static IReadOnlyList<Site> DeriveSites()
    {
        var wire = WireTokens();
        var fixtureValues = FixtureValues();
        var fixtureSymbols = FixtureSymbols();
        var symbolRe = NameAlternation(fixtureSymbols);

        var sites = new List<Site>();
        int skipped = 0, hoisted = 0;

        foreach (var path in TestFiles())
        {
            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(path));
            var root = tree.GetRoot();
            var name = Path.GetFileName(path);
            var consts = ConstStrings(root);

            foreach (var call in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (call.Expression is not MemberAccessExpressionSyntax ma) continue;
                if (!IsAssert(ma.Expression)) continue;

                var form = ma.Name.Identifier.ValueText;
                if (!Forms.Contains(form)) continue;

                var args = call.ArgumentList.Arguments;
                if (args.Count == 0) continue;

                // xUnit's collection forms — Assert.Contains(collection, predicate) — ask a different
                // question, so they are outside this rule rather than misclassified by it.
                if (args.Any(a => a.DescendantNodesAndSelf().OfType<LambdaExpressionSyntax>().Any()))
                {
                    skipped++;
                    continue;
                }

                var expectedNode = args[0].Expression;
                var expected = Flatten(expectedNode.ToString());
                var actual = args.Count > 1 ? Flatten(args[1].Expression.ToString()) : "";

                var literals = LiteralsIn(expectedNode).ToList();

                // A sentence hoisted into a `const string` and asserted through the identifier is the same
                // arm wearing a name, so the constant is resolved and classified by its VALUE. A
                // whitespace-free constant is left alone: that is a wire member, a field path or an
                // EditorID named once, which is the shape this guard is trying to encourage.
                if (literals.Count == 0 && expectedNode is IdentifierNameSyntax id
                 && consts.TryGetValue(id.Identifier.ValueText, out var hoistedValue)
                 && hoistedValue.Any(char.IsWhiteSpace))
                {
                    literals.Add(hoistedValue);
                    hoisted++;
                }

                var bucket = Classify(expected, actual, literals, wire, fixtureValues, symbolRe);
                sites.Add(new Site(name, tree.GetLineSpan(call.Span).StartLinePosition.Line + 1,
                                   form, bucket, Clip(expected, 200), Clip(actual, 120), literals));
            }
        }

        _skipped = skipped;
        _hoisted = hoisted;
        return sites;
    }

    /// <summary>The receiver of an assertion call: <c>Assert</c>, or any qualification of it
    /// (<c>Xunit.Assert</c>), so writing the namespace out does not take a site off the population.</summary>
    static bool IsAssert(ExpressionSyntax receiver) => receiver switch
    {
        IdentifierNameSyntax { Identifier.ValueText: "Assert" } => true,
        MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Assert" } => true,
        _ => false,
    };

    static string Classify(string expected, string actual, IReadOnlyList<string> literals,
                           IReadOnlySet<string> wire, IReadOnlySet<string> fixtureValues, Regex? symbolRe)
    {
        if (CatalogueRe.IsMatch(expected)) return "ii-catalogue";
        if (literals.Count == 0) return "iii-structural";
        if (literals.All(wire.Contains)) return "iii-structural";
        if (CollectionRe.IsMatch(expected)) return "iii-structural";
        if (DocRe.IsMatch(actual) && !actual.TrimEnd().EndsWith(".GetString()", StringComparison.Ordinal))
            return "iii-structural";

        var spaceFree = literals.All(l => !l.Contains(' '));
        if (spaceFree && symbolRe is not null && symbolRe.IsMatch(expected)) return "iv-fixture";
        if (spaceFree && literals.All(fixtureValues.Contains)) return "iv-fixture";

        return "i-prose";
    }

    // ---- the derived populations -----------------------------------------------------------------------

    static IEnumerable<string> TestFiles() => CsFiles(Path.Combine(HarnessPaths.RepoRoot, "src", "housecarl-mcp-tests"));

    static IEnumerable<string> CsFiles(string root) =>
        Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                 .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                          && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                 .OrderBy(p => p, StringComparer.Ordinal);

    static readonly string[] WireWriters =
    {
        "WritePropertyName", "WriteString", "WriteNumber", "WriteBoolean",
        "WriteStartObject", "WriteStartArray", "WriteNull",
    };

    static IReadOnlySet<string>? _wire;

    /// <summary>Every JSON property name the product writes, plus every <c>[JsonPropertyName]</c> — the same
    /// surface <c>WireNamesProbe</c> already reflects over, read here off the syntax.</summary>
    public static IReadOnlySet<string> WireTokens()
    {
        lock (Memo) { if (_wire is not null) return _wire; }

        var toks = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in new[] { "housecarl-mcp", "housecarl-core" })
            foreach (var path in CsFiles(Path.Combine(HarnessPaths.RepoRoot, "src", root)))
            {
                var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot();

                foreach (var call in tree.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    var name = call.Expression switch
                    {
                        MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText,
                        IdentifierNameSyntax i => i.Identifier.ValueText,
                        _ => null,
                    };
                    if (name is null || !WireWriters.Contains(name)) continue;
                    if (call.ArgumentList.Arguments.Count == 0) continue;
                    if (call.ArgumentList.Arguments[0].Expression is LiteralExpressionSyntax
                        { RawKind: (int)SyntaxKind.StringLiteralExpression } lit)
                        toks.Add(lit.Token.ValueText);
                }

                foreach (var attr in tree.DescendantNodes().OfType<AttributeSyntax>())
                    if (attr.Name.ToString().EndsWith("JsonPropertyName", StringComparison.Ordinal)
                     && attr.ArgumentList?.Arguments.Count > 0
                     && attr.ArgumentList.Arguments[0].Expression is LiteralExpressionSyntax
                        { RawKind: (int)SyntaxKind.StringLiteralExpression } a)
                        toks.Add(a.Token.ValueText);
            }

        lock (Memo) { return _wire ??= toks; }
    }

    static IReadOnlySet<string>? _fixtureValues;

    /// <summary>Values the fixture files DECLARE — an initialiser, an expression body, or a first argument.
    /// A literal a fixture wrote is an input the test handed the product, not prose the product composed.</summary>
    public static IReadOnlySet<string> FixtureValues()
    {
        lock (Memo) { if (_fixtureValues is not null) return _fixtureValues; }

        var vals = new HashSet<string>(StringComparer.Ordinal);

        void Take(ExpressionSyntax? e)
        {
            if (e is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } lit
             && lit.Token.ValueText.Length is >= 2 and <= 64
             && !lit.Token.ValueText.Contains('\n'))
                vals.Add(Flatten(lit.Token.ValueText));
        }

        foreach (var path in TestFiles().Where(p => FixtureFileRe.IsMatch(Path.GetFileName(p))))
        {
            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot();
            foreach (var eq in tree.DescendantNodes().OfType<EqualsValueClauseSyntax>()) Take(eq.Value);
            foreach (var arrow in tree.DescendantNodes().OfType<ArrowExpressionClauseSyntax>()) Take(arrow.Expression);
            foreach (var list in tree.DescendantNodes().OfType<ArgumentListSyntax>())
                if (list.Arguments.Count > 0) Take(list.Arguments[0].Expression);
            foreach (var assign in tree.DescendantNodes().OfType<AssignmentExpressionSyntax>()) Take(assign.Right);
        }

        lock (Memo) { return _fixtureValues ??= vals; }
    }

    static IReadOnlySet<string>? _fixtureSymbols;

    /// <summary>
    /// The symbols that mark an expression as reading a FIXTURE rather than a response — derived, where the
    /// plan's script hand-listed them. Three sources, each a rule: the types the fixture files declare; the
    /// fields and properties in the test project whose declared type is one of those; and the product's NAME
    /// REGISTRIES, being types whose every non-private member is a <c>const string</c> (a tool name or a
    /// parameter name asserted from its own registry is a symbol, not a spelled sentence).
    /// </summary>
    public static IReadOnlySet<string> FixtureSymbols()
    {
        lock (Memo) { if (_fixtureSymbols is not null) return _fixtureSymbols; }

        var symbols = new HashSet<string>(StringComparer.Ordinal);
        var fixtureTypes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var path in TestFiles().Where(p => FixtureFileRe.IsMatch(Path.GetFileName(p))))
            foreach (var t in CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot()
                                              .DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                fixtureTypes.Add(t.Identifier.ValueText);
                foreach (var m in t.Members.OfType<MethodDeclarationSyntax>())
                    symbols.Add(m.Identifier.ValueText);
            }

        foreach (var t in fixtureTypes) symbols.Add(t);

        // Fields and properties anywhere in the test project whose declared type is a fixture type. This is
        // what recovers `W`, `_w` and their kin without naming them.
        foreach (var path in TestFiles())
        {
            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot();

            foreach (var f in tree.DescendantNodes().OfType<FieldDeclarationSyntax>())
                if (fixtureTypes.Contains(BaseTypeName(f.Declaration.Type)))
                    foreach (var v in f.Declaration.Variables) symbols.Add(v.Identifier.ValueText);

            foreach (var p in tree.DescendantNodes().OfType<PropertyDeclarationSyntax>())
                if (fixtureTypes.Contains(BaseTypeName(p.Type))) symbols.Add(p.Identifier.ValueText);
        }

        foreach (var reg in NameRegistries()) symbols.Add(reg);

        lock (Memo) { return _fixtureSymbols ??= symbols; }
    }

    /// <summary>Product types whose every non-private declared member is a <c>const string</c> — a registry of
    /// names (tool names, lever names), never a sentence catalogue, which carries methods and caps too.</summary>
    static IEnumerable<string> NameRegistries()
    {
        foreach (var root in new[] { "housecarl-mcp", "housecarl-core" })
            foreach (var path in CsFiles(Path.Combine(HarnessPaths.RepoRoot, "src", root)))
                foreach (var t in CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot()
                                                  .DescendantNodes().OfType<ClassDeclarationSyntax>())
                {
                    var members = t.Members.Where(m => !m.Modifiers.Any(SyntaxKind.PrivateKeyword)).ToList();
                    if (members.Count == 0) continue;
                    if (members.All(m => m is FieldDeclarationSyntax f
                                      && f.Modifiers.Any(SyntaxKind.ConstKeyword)
                                      && f.Declaration.Type.ToString() == "string"))
                        yield return t.Identifier.ValueText;
                }
    }

    static string BaseTypeName(TypeSyntax t) => t switch
    {
        NullableTypeSyntax n => BaseTypeName(n.ElementType),
        QualifiedNameSyntax q => q.Right.Identifier.ValueText,
        GenericNameSyntax g => g.Identifier.ValueText,
        SimpleNameSyntax s => s.Identifier.ValueText,
        _ => t.ToString(),
    };

    static Regex? NameAlternation(IReadOnlySet<string> names)
    {
        var usable = names.Where(n => n.Length > 0 && n.All(c => char.IsLetterOrDigit(c) || c == '_'))
                          .OrderByDescending(n => n.Length).ToArray();
        return usable.Length == 0
            ? null
            : new Regex(@"(?<![A-Za-z0-9_])(?:" + string.Join("|", usable.Select(Regex.Escape)) + @")\s*[.(]",
                        RegexOptions.Compiled);
    }

    // ---- literal extraction ----------------------------------------------------------------------------

    static IEnumerable<string> LiteralsIn(SyntaxNode node)
    {
        foreach (var n in node.DescendantNodesAndSelf())
        {
            switch (n)
            {
                case LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } lit:
                {
                    var t = Flatten(lit.Token.ValueText);
                    if (t.Length > 0) yield return t;
                    break;
                }
                case InterpolatedStringTextSyntax text:
                {
                    var t = Flatten(text.TextToken.ValueText);
                    if (t.Length > 0) yield return t;
                    break;
                }
            }
        }
    }

    /// <summary>Every <c>const string</c> declared in one file, by name — a field or a local.</summary>
    static Dictionary<string, string> ConstStrings(SyntaxNode root)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        void Take(VariableDeclarationSyntax decl, bool isConst)
        {
            if (!isConst || decl.Type.ToString() != "string") return;
            foreach (var v in decl.Variables)
                if (v.Initializer?.Value is LiteralExpressionSyntax
                    { RawKind: (int)SyntaxKind.StringLiteralExpression } lit)
                    map[v.Identifier.ValueText] = Flatten(lit.Token.ValueText);
        }

        foreach (var f in root.DescendantNodes().OfType<FieldDeclarationSyntax>())
            Take(f.Declaration, f.Modifiers.Any(SyntaxKind.ConstKeyword));
        foreach (var l in root.DescendantNodes().OfType<LocalDeclarationStatementSyntax>())
            Take(l.Declaration, l.IsConst);

        return map;
    }

    static string Flatten(string s) =>
        string.Join(" ", s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    static string Clip(string s, int n) => s.Length <= n ? s : s[..n];

    // ---- the baseline ----------------------------------------------------------------------------------

    static IReadOnlyDictionary<string, int> Committed()
    {
        var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            File.ReadAllText(BaselinePath),
            new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip });

        Assert.True(raw is not null, $"'{BaselinePath}' did not parse into a baseline.");

        var map = raw!.Where(kv => !kv.Key.StartsWith('_') && kv.Value.ValueKind == JsonValueKind.Number)
                      .ToDictionary(kv => kv.Key, kv => kv.Value.GetInt32(), StringComparer.Ordinal);

        Assert.True(map.Count > 0,
            $"'{BaselinePath}' carries no per-file entries, so every comparison below is vacuous. Either the " +
            "file's shape changed or the countdown was emptied without reaching zero the honest way.");
        return map;
    }

    // ---- the gates -------------------------------------------------------------------------------------

    [Fact]
    public void EveryFilesProseCountMatchesTheBaseline_AndTheOnlyLegalDirectionIsDown()
    {
        var committed = Committed();
        var actual = Sites().Where(s => s.Bucket == "i-prose")
                            .GroupBy(s => s.File, StringComparer.Ordinal)
                            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var grew = new List<string>();
        var shrank = new List<string>();

        foreach (var file in committed.Keys.Union(actual.Keys, StringComparer.Ordinal)
                                      .OrderBy(f => f, StringComparer.Ordinal))
        {
            var allowed = committed.TryGetValue(file, out var n) ? n : 0;
            var found = actual.TryGetValue(file, out var list) ? list : new List<Site>();

            if (found.Count > allowed)
                grew.Add($"{file}: {found.Count} prose assertion(s), baseline {allowed}\n" +
                         "    (the COUNT is the gate, so these are the sites past the allowance in line order, " +
                         "not necessarily the one just written — any of them is a legal one to reshape)\n" +
                         string.Join("\n", found.OrderBy(s => s.Line)
                                                .Skip(allowed)
                                                .Select(s => $"      line {s.Line}  Assert.{s.Form}  " +
                                                             $"\"{Clip(s.Literals.FirstOrDefault() ?? "", 90)}\"")));
            else if (found.Count < allowed)
                shrank.Add($"{file}: {found.Count} prose assertion(s), baseline {allowed}");
        }

        _out.WriteLine($"prose sites: {actual.Values.Sum(v => v.Count)} (baseline {committed.Values.Sum()}) " +
                       $"across {actual.Count} file(s) · total assertion sites {Sites().Count} · " +
                       $"lambda forms skipped {_skipped} · hoisted constants resolved {_hoisted}");
        foreach (var kv in actual.OrderByDescending(k => k.Value.Count).ThenBy(k => k.Key, StringComparer.Ordinal))
            _out.WriteLine($"  {kv.Key}: {kv.Value.Count}");

        Assert.False(grew.Count > 0,
            "A fact test asserts a fragment of prose the product composed:\n  " + string.Join("\n  ", grew) +
            "\n\nTwo dispositions, and both are shorter to write than the arm you replaced:\n" +
            "  1. If the claim is an ENGINE FACT, assert it on the json document keyed by the record it is " +
            "about — Facts.Record(doc, key) / Facts.Field(rec, path) / Facts.Number|Text|Flag. There is no span " +
            "to prefix-pin, a second record cannot satisfy it, and an absent subject throws instead of passing.\n" +
            "  2. If the claim is that a SENTENCE ARRIVED, move the sentence into ReadSentences (byte-identical " +
            "— a reword is a caller-facing change and is ruled separately) and assert its identity with " +
            "Facts.States(text, ReadSentences.X). Its wording is then pinned once, in ReadSentenceWordingTests.\n" +
            $"A file's allowance lives in '{BaselinePath}'; raising one is legal, deliberate, and argued in the " +
            "same commit.");

        Assert.False(shrank.Count > 0,
            "You reshaped prose assertions and the countdown does not know:\n  " + string.Join("\n  ", shrank) +
            $"\nLower or delete each key in '{BaselinePath}' in this PR. A baseline left above the real figure " +
            "stops being a countdown and becomes headroom.");
    }

    /// <summary>
    /// The vacuity canary. A rename, a reformat, a package that fails to load, a walk that stops matching —
    /// each would leave the gate above green over an empty tree, which is the guard failing toward green.
    /// </summary>
    [Fact]
    public void TheDerivationIsMeasuringSomething_NotAnEmptyTree()
    {
        var sites = Sites();
        var committed = Committed();

        Assert.True(sites.Count > committed.Values.Sum() && sites.Count > 0,
            $"The parse found {sites.Count} assertion site(s) across the test project, and the baseline alone " +
            $"records {committed.Values.Sum()} prose assertions. Fewer sites than that means the walk has " +
            "stopped finding things rather than the tree having been cleaned.");

        Assert.True(WireTokens().Count > 0 && FixtureValues().Count > 0 && FixtureSymbols().Count > 0,
            $"A derived population came back empty: {WireTokens().Count} wire token(s), " +
            $"{FixtureValues().Count} fixture value(s), {FixtureSymbols().Count} fixture symbol(s). Each one " +
            "empty moves sites INTO the prose bucket or out of it silently, so an empty one is this guard's " +
            "subject rather than a detail.");

        _out.WriteLine($"wire tokens {WireTokens().Count} · fixture values {FixtureValues().Count} · " +
                       $"fixture symbols {FixtureSymbols().Count} · sites {sites.Count} · " +
                       string.Join(" · ", sites.GroupBy(s => s.Bucket).OrderBy(g => g.Key, StringComparer.Ordinal)
                                                .Select(g => $"{g.Key} {g.Count()}")));
    }

    // ---- the marker's own rule -------------------------------------------------------------------------

    /// <summary>
    /// What makes <see cref="SentenceCatalogueAttribute"/> unsprayable. Inside a marked class every
    /// assertion's SUBJECT must be a catalogue expression — a sentence catalogue member, or something the
    /// test project itself computed. A subject rooted at any other product type is a tool response, and a
    /// fact test's subject is a tool response by definition, so putting the marker on a fact test fails HERE
    /// instead of buying the class silence.
    /// </summary>
    [Fact]
    public void EveryAssertionInAMarkedClassHasACatalogueSubject_TheMarkerCannotBeSprayedOnAFactTest()
    {
        var product = ProductTypes();
        var offenders = new List<string>();
        var marked = 0;

        foreach (var path in TestFiles())
        {
            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(path));
            var root = tree.GetRoot();
            var name = Path.GetFileName(path);

            foreach (var cls in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                if (!cls.AttributeLists.SelectMany(a => a.Attributes)
                        .Any(a => a.Name.ToString() is "SentenceCatalogue" or "SentenceCatalogueAttribute"))
                    continue;

                marked++;
                foreach (var call in cls.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (call.Expression is not MemberAccessExpressionSyntax ma) continue;
                    if (!IsAssert(ma.Expression)) continue;
                    if (!Forms.Contains(ma.Name.Identifier.ValueText)) continue;
                    if (call.ArgumentList.Arguments.Count < 2) continue;

                    var subject = call.ArgumentList.Arguments[1].Expression;
                    var rootName = RootName(Resolve(subject, call), call);
                    if (rootName is null || !product.Contains(rootName)
                     || rootName.EndsWith("Sentences", StringComparison.Ordinal)) continue;

                    offenders.Add($"{name}:{tree.GetLineSpan(call.Span).StartLinePosition.Line + 1} — " +
                                  $"Assert.{ma.Name.Identifier.ValueText} over a subject rooted at the product " +
                                  $"type {rootName}: {Clip(Flatten(subject.ToString()), 100)}");
                }
            }
        }

        Assert.True(marked > 0,
            "No class carries [SentenceCatalogue], so this rule is vacuous. The marker exists to swap the prose " +
            "rule for a stricter one; a project with no marked class has either lost the catalogue tests or " +
            "renamed the attribute, and both are this arm's subject.");

        Assert.True(offenders.Count == 0,
            "[SentenceCatalogue] classes assert over a TOOL RESPONSE:\n  " + string.Join("\n  ", offenders) +
            "\nThe marker exempts a class from the prose rule because its subject is the catalogue itself. A " +
            "test whose subject is a response is a FACT test wherever the attribute is written, and it belongs " +
            "in a fact-test class asserting structure keyed by the record it is about.");
    }

    /// <summary>One hop of local resolution: a subject that is a local holding a tool response is that
    /// response. Deeper chains are outside a parse-only walk and are stated as its limit.</summary>
    static ExpressionSyntax Resolve(ExpressionSyntax subject, SyntaxNode context)
    {
        if (subject is not IdentifierNameSyntax id) return subject;

        var method = context.Ancestors().OfType<BaseMethodDeclarationSyntax>().FirstOrDefault();
        var decl = method?.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                         .FirstOrDefault(v => v.Identifier.ValueText == id.Identifier.ValueText);

        return decl?.Initializer?.Value ?? subject;
    }

    /// <summary>The leftmost identifier of an expression — the type or local it is rooted at.</summary>
    static string? RootName(ExpressionSyntax e, SyntaxNode _)
    {
        var node = (SyntaxNode)e;
        while (true)
            switch (node)
            {
                case MemberAccessExpressionSyntax m: node = m.Expression; break;
                case InvocationExpressionSyntax i: node = i.Expression; break;
                case ElementAccessExpressionSyntax el: node = el.Expression; break;
                case ConditionalAccessExpressionSyntax c: node = c.Expression; break;
                case ParenthesizedExpressionSyntax p: node = p.Expression; break;
                case CastExpressionSyntax cast: node = cast.Expression; break;
                case IdentifierNameSyntax id: return id.Identifier.ValueText;
                default: return null;
            }
    }

    static IReadOnlySet<string>? _productTypes;

    /// <summary>Every type the product declares — derived, so a new tool surface is inside this rule the day
    /// it lands.</summary>
    public static IReadOnlySet<string> ProductTypes()
    {
        lock (Memo) { if (_productTypes is not null) return _productTypes; }

        var types = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in new[] { "housecarl-mcp", "housecarl-core" })
            foreach (var path in CsFiles(Path.Combine(HarnessPaths.RepoRoot, "src", root)))
                foreach (var t in CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot()
                                                  .DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
                    types.Add(t.Identifier.ValueText);

        lock (Memo) { return _productTypes ??= types; }
    }
}
