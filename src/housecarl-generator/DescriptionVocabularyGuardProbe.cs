using System.ComponentModel;
using System.Reflection;
using System.Text.RegularExpressions;
using HousecarlCore;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// REGRESSION GUARD (standing CI instrument, self-contained) — CALLER-FACING PROSE VOCABULARY (#386).
///
/// <para><b>The gap this closes.</b> Two caller-facing prose surfaces had nothing behind them: the
/// <c>[Description]</c> attributes a model reads to decide how to call a tool, and the consent prompts a modder
/// reads to decide whether to say yes. <c>write-surface-guard</c> and the <see cref="MustStateAttribute"/> /
/// <see cref="NoClaimsAttribute"/> walk both police the <see cref="WriteSentences"/> / <see cref="ReadSentences"/>
/// consts and reach neither. So the in-place consent fix (#378) — which changed exactly one fact, that a REFUSED
/// call records nothing — left stale text behind that four separate hand sweeps found in four separate homes, each
/// sweep triggered by a reviewer noticing a claim by eye rather than by anything going red.</para>
///
/// <para><b>Two readers, because a completeness claim cannot certify itself.</b> This is the third design. The
/// first enumerated the surface by REFLECTION and was class-stopped: five measured routes carried a consent
/// sentence to a caller that reflection could not reach, including <c>compact_plugin</c>'s inline consent prompt.
/// The second scanned SOURCE LITERALS with a hand lexer and was class-stopped on the same class one design later:
/// the lexer could not see a literal inside an interpolation hole, so 201 shipped lines were outside the net, and
/// the arm chartered to make that falsifiable (compiled values covered by the scan) structurally could not see it
/// either — C# requires a compiled constant, and runtime-interpolated prose has none. Both deaths were the same
/// shape: <b>a completeness claim certified by an oracle derived from the machinery it certifies.</b></para>
///
/// <para>So the net is still the shipped source literals — that premise was never falsified, and every one of the
/// five routes IS a source literal — but the reading of them is done TWICE, by two independently written readers,
/// and the two are held against each other:
/// <list type="bullet">
///   <item><see cref="RoslynLiteralReader"/> (READER A) — the C# compiler's own parser. What counts as a literal
///         is its decision, not an opinion, so this reader cannot disagree with the build.</item>
///   <item><see cref="HandLiteralLexer"/> (READER B) — a second spelling written from C#'s lexical grammar,
///         sharing no code with A beyond the <see cref="SourceLiteral"/> record.</item>
/// </list>
/// <c>INV6-AGREE</c> asserts the two produce the same literals, file by file. A reader that stops early,
/// mis-decodes an escape, or cannot see into a hole makes them disagree and turns that arm red with the file
/// named. Neither reader is its own oracle, which is the property both prior designs lacked.</para>
///
/// <para><b>The enumerations, and the question each answers.</b>
/// <list type="bullet">
///   <item><b>SOURCE</b> — every string-literal SENTENCE in the three shipped trees (<c>housecarl-mcp</c>,
///         <c>housecarl-core</c>, <c>housecarl-setup</c>), a sentence being a maximal run of adjacent literals
///         joined by <c>+</c>: the unit an author writes. This answers ABSENCE (INV1) — a phrase absent from
///         every literal is absent from every string built out of them, however assembled.</item>
///   <item><b>SURFACE</b> — the compiled <c>[Description]</c> attributes of the tool assembly. This answers
///         PRESENCE and COMPLETENESS (INV3/INV4): "which verbs does a caller read" needs the text assembled, and
///         only the attribute knows which literals reached a description.</item>
/// </list></para>
///
///   INV1 — every consent-vocabulary phrase declared here is ABSENT from every shipped source literal, or the
///          sentence carrying it also states the clause that makes it true (the phrase's COMPANION).
///   INV2 — every declared exemption still matches a real site (materialises with the first declared row), and
///          the exemption table cannot degenerate into an allowlist of the surface (INV2-DEGEN, always runs).
///   INV3 — every write-verb RECITAL on the <c>[Description]</c> surface names only real verbs, and between them
///          the recitals name the whole published vocabulary.
///   INV4 — the two homes for the verb vocabulary still say the same thing and still say what this guard writes
///          down independently; the verb marked <c>(default)</c> is one verb, the same one at every site that
///          marks it, and the one the annotated slots actually default to; and the TAIL of the recital is the verb
///          the gloss glued to that tail describes.
///   INV5 — every compile-time constant on the surface (the <c>[Description]</c> arguments and the <c>const</c>
///          strings) is covered by the SOURCE scan. A third kind of evidence, through the compiler and the
///          runtime rather than through either reader.
///   INV6 — every scanned file PARSES, and the two readers AGREE about what is in it. This is the arm the
///          by-construction claim rests on.
///
/// <para><b>The pin.</b> <see cref="Phrases"/>, <see cref="PublishedVocabulary"/>, <see cref="PublishedDefault"/>
/// and <see cref="TailGlossVerb"/> are INDEPENDENTLY WRITTEN literals, never derived from the consts they check. A
/// const-concat conversion verified against the same const that produced it is the check-A tautology — it stays
/// green when the const is emptied. <c>remedy-verbs-guard</c>'s SITE-UNKNOWN-VERB arm documents the same pattern
/// for the same reason. A deliberate change to the published vocabulary turns INV4 red once, on purpose. A silent
/// one turns it red too.</para>
///
/// <para><b>Declared boundary — what this does NOT reach, and why.</b> The by-construction claim is exactly as
/// honest as this list, so the run PRINTS it (see <see cref="NotInReach"/>) rather than leaving it in a docstring
/// that no CI log carries.
/// <list type="bullet">
///   <item><b>Comments, including XML docstrings.</b> Not literals; skipped by both readers, deliberately.
///         Authored narrative prose is the non-mechanizable residue #337/#330 ruled on, and the docstrings on
///         these very builders are part of it. So are the READMEs, the shipped skills, the CHANGELOG, and the
///         plugin / marketplace metadata — none of which is in a scanned tree.</item>
///   <item><b>Text assembled around a value</b> — <c>"shown " + name + "once"</c>, or a phrase split across an
///         interpolation hole. The <c>+</c>-run merge is conservative (it joins literals separated by nothing but
///         <c>+</c>) and a hole is rendered as <see cref="SourceLiteral.HoleMarker"/>, which carries no letters.
///         So a phrase split around a value carries no phrase in any fragment. This cannot be closed short of
///         dataflow analysis and is not claimed to be: what the guard makes structural is that putting a consent
///         claim in front of a caller AS ORDINARY TEXT takes a deliberate act. Splitting a sentence around a value
///         to get past a vocabulary guard IS that act, and it leaves the evidence of intent in the diff — which is
///         the standard #386 asks for, not a proof of impossibility.</item>
///   <item><b>Non-source text.</b> Third-party library messages surfaced to a caller, and the shipped JSON data
///         files plus the generated corpus: machine-shaped identifiers, paths and edges rather than authored
///         English. A consent claim cannot originate in them because nothing in them is a sentence.</item>
///   <item><b>Truth, as opposed to vocabulary.</b> The check cannot tell a true sentence from a false one built
///         entirely of known words, and teaching it to grade prose would be #308's verdict-layer mistake wearing
///         new clothes.</item>
///   <item><b>A verb recital whose tokens are ALL stale at once.</b> <see cref="Recitals"/> admits a run only if
///         at least one token is a real verb — otherwise "Text | Json" would be read as a verb list — so a run in
///         which every name went stale simultaneously is dropped and INV3-TOKENS stays green. Rename drift does
///         not have that shape (it moves one name and leaves the rest), the marked-default arm reaches any such
///         run that marks a default, and the census prints every dropped run so the blind spot is visible.</item>
///   <item><b>Completeness per site.</b> INV3's union arm cannot see a verb missing from ONE description while
///         another names it. Whether a verb is legal at a given carrier is a semantic fact no attribute carries
///         (<c>create_record</c>'s <c>op</c> refuses <c>CopyFrom</c>) — the same boundary <c>wire-names-guard</c>
///         records for its INV5.</item>
/// </list></para>
///
/// Run: <c>dotnet run --project src/housecarl-generator -- description-vocab-guard</c>
/// </summary>
public static class DescriptionVocabularyGuardProbe
{
    static int _pass, _fail;

    // ================= the independently-written literals (the PIN — never derived) =================

    /// <summary>The clause that makes a "one-time" modifier honest: the caller is told, in the same breath, that a
    /// refused call records nothing and so may bring them back here. Written out here deliberately, NOT read off
    /// the sentences it checks — a companion taken from the text it validates would validate anything.</summary>
    static readonly string[] CorrectionClauses =
        { "a call that is refused records nothing", "may be needed again", "may see this again" };

    /// <summary>Spelled out rather than an empty array at each call site, so "no companion" reads as the
    /// deliberate choice it is: this phrase is refused outright, with no wording that redeems it.</summary>
    static readonly string[] NoCompanion = Array.Empty<string>();

    /// <summary>One consent-vocabulary rule. <see cref="VocabRule.Companions"/> EMPTY means the phrase is refused
    /// outright; otherwise it is allowed only in a sentence that ALSO states one of these. Companions plural
    /// rather than one required wording because the same correction is phrased for its surface ("so it may be
    /// needed again" on a parameter, "so you may see this again" in the prompt), and pinning wording instead of
    /// the claim is what <see cref="MustStateAttribute"/>'s norm warns against.</summary>
    readonly record struct VocabRule(string Phrase, string[] Companions, string Ground);

    /// <summary>The consent vocabulary, written out here and nowhere else. Sourced from the four sweeps recorded
    /// in #386 — the eleven <c>acknowledge=</c> parameter claims, the six lane parentheticals, the two handshake
    /// prompts, and the fourteen <c>one-time</c> modifiers — plus the siblings those wordings have. Adding a
    /// phrase here is how the class stays closed as new ways to over-claim get invented; each carries its ground,
    /// because a phrase list without grounds is a list nobody can safely prune. Each ground names only spans the
    /// rule actually REACHES: these are substring tests, so a ground citing a wording the span does not match
    /// would be the guard making the same kind of over-claim it exists to catch.</summary>
    static readonly VocabRule[] Phrases =
    {
        new("one-time", CorrectionClauses,
            "the TRADE-OFF is one-time; the PROMPT is not. A refused call records nothing (#378), so a caller can "
          + "meet it again — the modifier is only honest next to the clause that says so."),
        new("one time", CorrectionClauses, "the spaced spelling of the same claim."),
        new("shown once", NoCompanion,
            "false as written: the prompt is shown until an in-place write LANDS, which is not the same as once. "
          + "This is the exact wording #378 made stale."),
        new("only once", NoCompanion, "the same claim as 'shown once', in the form sweep 2 found."),
        new("just once", NoCompanion, "the same claim, colloquial form."),
        new("never again", NoCompanion,
            "the eleven-instance wording from sweep 1 ('needed once, never again for it') — the claim a refused "
          + "call falsifies."),
        new("first and only", NoCompanion, "asserts a uniqueness of the prompt that nothing enforces."),
        new("first time only", NoCompanion, "the same claim, adjectival form."),
        new("single time", NoCompanion, "the same claim, spelled around 'once'."),
        new("ask again", NoCompanion,
            "reaches \"won't ask again\" / \"will not ask again\". The true statement is phrased the other way "
          + "round ('may be needed again'), so nothing honest needs this span."),
        new("asked again", NoCompanion,
            "the passive spelling — \"never asked again\", the wording sweep 1 actually found. A separate rule "
          + "because \"ask again\" does not reach it: these are substring tests, not stems."),
        new("not see this again", NoCompanion,
            "the negation of the prompt's own correct sentence ('so you may see this again')."),
        new("never see this again", NoCompanion, "the same claim, emphatic form."),
        new("once per plugin", NoCompanion, "asserts a per-plugin cap on the prompt that the consent store does not provide."),
        new("once per file", NoCompanion, "the same claim, keyed to the file."),
        new("once per mesh", NoCompanion, "the same claim, keyed to the mesh."),
    };

    /// <summary>houseCARL's write-verb vocabulary, written out here independently of <see cref="WriteVerbs.All"/>
    /// and of <see cref="WriteVerbs.AllRecital"/>. Holding the recital against the collection that produced it
    /// would prove only that the copy was faithful; this is the second, independent statement that makes INV4 able
    /// to fail at all. In the published order.</summary>
    static readonly string[] PublishedVocabulary =
        { "Set", "Add", "Remove", "SetAtIndex", "InsertAtIndex", "ReplaceAll", "Merge", "CopyFrom" };

    /// <summary>The verb a write slot uses when the caller names none — written independently for the same reason
    /// as the vocabulary above. <see cref="WriteVerbs.AllRecital"/> feeds three shipped descriptions, so ONE edit
    /// to its <c>(default)</c> marker mis-states the default in all three at once; the const-concat concentrated
    /// the fact, and a concentrated fact needs a pin.</summary>
    const string PublishedDefault = "Set";

    /// <summary>The verb that the parenthetical GLUED to <see cref="WriteVerbs.AllRecital"/>'s tail describes.
    /// <para>Written independently, and it is the whole point of INV4-TAILGLOSS. <c>BulkOp.verb</c>'s description
    /// is <c>AllRecital + " (deep-copy the field at field_path from from_plugin's version — see from_plugin). …"</c>,
    /// so that gloss lands on whichever verb the recital ends with. It reads correctly today by POSITION and
    /// nothing else. Appending a ninth verb — the very edit the const exists to make sufficient — silently moves
    /// the gloss onto the new verb and strips it off this one, shipping a false claim in the tool schema;
    /// reordering does the same. Deposited on #386 (2026-08-25) as an acceptance item for this guard, so the
    /// positional coincidence becomes a checked fact and the gloss can stay where it is.</para></summary>
    const string TailGlossVerb = "CopyFrom";

    /// <summary>One declared exemption: <c>Phrase</c> is allowed at any site whose label CONTAINS
    /// <c>SiteContains</c>, for the stated <c>Ground</c>.</summary>
    readonly record struct Exemption(string Phrase, string SiteContains, string Ground);

    /// <summary>Sites where a phrase is accurate and stays. EMPTY, and empty as a MEASURED result rather than an
    /// aspiration: the source-literal net is ~10k sentences across three shipped trees and every phrase above is
    /// either absent from it or carries its companion. The mechanism stays because #386 asks for a deliberate-act
    /// escape hatch — but it is fenced by <see cref="MaxExemptions"/>, because an exemption list that can absorb
    /// any miss is not a guard, it is an allowlist of the surface wearing a guard's name.</summary>
    static readonly Exemption[] Exemptions =
    {
        // (none — see the summary above; this being empty is a measurement, not an omission)
    };

    /// <summary>How many exemptions this guard may carry before the table itself is the finding.
    /// <para>The EXEMPTION-DEGENERATION TRIPWIRE, carried in from #386's first escalation: an exemption list that
    /// grows to fit the surface stops being a guard, because every future miss has somewhere to go. Three is not a
    /// capacity estimate — it is the point at which "this phrase is accurate here" stops being a handful of
    /// recorded decisions and starts being a policy. Hitting it is a CLAUDE.md §4 escalation about the phrase
    /// list or the surface, never a number to raise in the same commit that needed it raised.</para></summary>
    const int MaxExemptions = 3;

    /// <summary>Where shipped prose can come from that this guard structurally cannot see. Printed on every run,
    /// not just written in the summary above: the by-construction claim is exactly as honest as this list, and a
    /// disclosure nobody reads is the same shape as no disclosure. Carried in from #386's first escalation.</summary>
    static readonly string[] NotInReach =
    {
        "comments and XML docstrings (not literals — the #337/#330 authored-prose residue, deliberately out)",
        "the READMEs, the shipped skills, plugin/CHANGELOG.md, and the plugin / marketplace JSON metadata (not in a scanned tree)",
        "a phrase split around a VALUE — \"shown \" + n + \"once\", or across an interpolation hole (no fragment carries it)",
        "third-party library messages surfaced to a caller (not ours to author, and not ours to fix)",
        "shipped JSON data files and the generated corpus (machine-shaped identifiers and paths; nothing in them is a sentence)",
        "whether a sentence built entirely of known words is TRUE (vocabulary, not truth — #308's boundary)",
    };

    // ================= entry =================

    public static int RunGuard(string[] args)
    {
        _pass = _fail = 0;
        Console.WriteLine("################  REGRESSION GUARD — caller-facing prose vocabulary (two readers over the shipped source literals + the [Description] surface)  ################");
        Console.WriteLine();
        try
        {
            var source = SourceArm();
            VocabularyArm(source);
            var surface = SurfaceSites().ToList();
            ReachArm(surface, source);
            VerbArm(surface);
            RedArms();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   [FAIL] the guard threw: {ex.GetType().Name}: {(ex.InnerException ?? ex).Message}");
            _fail++;
        }

        Console.WriteLine();
        Console.WriteLine($"=== description-vocab-guard: {_pass} passed, {_fail} failed -> {(_fail == 0 ? "PASS" : "FAIL")} ===");
        return _fail == 0 ? 0 : 1;
    }

    // ================= enumeration SOURCE: every string literal the shipped trees declare =================

    /// <summary>One authored sentence and the <c>path:line</c> that tells an author where to go and fix it.</summary>
    readonly record struct Sentence(string Label, string Text);

    static readonly Assembly Surface = typeof(ApplyOp).Assembly;
    static readonly Assembly Core = typeof(WriteVerbs).Assembly;
    static readonly Assembly Setup = typeof(HousecarlSetup.Program).Assembly;

    const BindingFlags AllMembers =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;

    /// <summary>Every assembly that SHIPS and carries authored English. The tool surface (<c>housecarl-mcp</c>),
    /// the write engine with its sentence consts (<c>housecarl-core</c>), and the setup utility
    /// (<c>housecarl-setup</c>), which <c>build-plugin.ps1</c> publishes into the package root beside the plugin
    /// and which talks to a modder in ~45 lines of console prose. The setup tree was named in #386's second
    /// escalation as an unscanned caller-facing surface; it is scanned, not excluded.
    /// <para>The generator is absent because it is the INSPECTOR, not the inspected: its probes quote the
    /// vocabulary in order to assert on it, and a scanner that treated its own statement of a rule as an instance
    /// of the rule would report itself. The exclusion is the derivation — this list is assemblies that ship —
    /// not a filename filter.</para></summary>
    static readonly Assembly[] ShippedAssemblies = { Surface, Core, Setup };

    /// <summary>The trees that ship, written out here INDEPENDENTLY of <see cref="ShippedAssemblies"/> — the same
    /// pin discipline as <see cref="PublishedVocabulary"/>, and for a sharper reason.
    /// <para><see cref="ShippedAssemblies"/> is one home feeding two things: the source roots INV1 scans, and the
    /// compiled descriptions and consts INV5 checks that scan against. Drop an assembly from it and BOTH shrink
    /// together — the net loses a tree and the oracle stops asking about it, silently and green. That is the exact
    /// shape #386's two previous designs died of, reappearing inside the fix, and a sabotage cell measured it:
    /// removing <c>housecarl-core</c> from that list left every arm passing. Holding the derived roots against a
    /// list written down separately is what makes shortening the net a deliberate act that turns this arm red
    /// once, on purpose.</para></summary>
    static readonly string[] PublishedShippedTrees = { "housecarl-mcp", "housecarl-core", "housecarl-setup" };

    /// <summary>The source tree for each shipped assembly, found under <c>src/</c> by the assembly's own name
    /// rather than typed as a path, so a project rename cannot leave this scanning a directory that no longer
    /// holds the code. The match is case-insensitive because an assembly name need not match its folder's casing
    /// (<c>houseCARL-Setup</c> lives in <c>src/housecarl-setup</c>), and a tree that cannot be found is a reported
    /// problem rather than a root silently skipped.</summary>
    static (List<string> Roots, List<string> Problems) ResolveRoots()
    {
        var roots = new List<string>();
        var problems = new List<string>();
        var present = Directory.Exists("src")
            ? Directory.EnumerateDirectories("src").ToList()
            : new List<string>();
        if (present.Count == 0)
            problems.Add($"there is no 'src' directory to scan — the CWD must be the repo root (it is '{Directory.GetCurrentDirectory()}')");

        foreach (var asm in ShippedAssemblies)
        {
            var name = asm.GetName().Name!;
            var match = present.FirstOrDefault(d => string.Equals(Path.GetFileName(d), name, StringComparison.OrdinalIgnoreCase));
            if (match is null) problems.Add($"no source tree under src/ matches shipped assembly '{name}' — INV1's net is missing that tree entirely");
            else roots.Add(match.Replace('\\', '/'));
        }
        return (roots.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), problems);
    }

    /// <summary>Shipped trees the run did not scan. A function of its input so a RED arm can drive it with a set
    /// that is short a tree — the sabotage this pin exists to catch.</summary>
    static List<string> MissingTrees(IReadOnlyCollection<string> scanned) =>
        PublishedShippedTrees
            .Where(t => !scanned.Contains(t, StringComparer.OrdinalIgnoreCase))
            .Select(t => $"'{t}' ships but is not among the trees this run scanned ({string.Join(", ", scanned)}) — INV1's net is "
                       + "short a tree, and INV5 cannot report it, because both come off the same assembly list")
            .ToList();

    static string Rel(string p) => Path.GetRelativePath(Directory.GetCurrentDirectory(), p).Replace('\\', '/');

    static IEnumerable<string> SourceFiles(string root) =>
        Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal);

    /// <summary>Read every shipped source file with BOTH readers, hold them against each other, and hand back the
    /// sentences INV1 scans. Reader A feeds the net; reader B exists to falsify A's completeness.</summary>
    static List<Sentence> SourceArm()
    {
        Console.WriteLine("── SOURCE: two independent readers over the shipped literals, held against each other ──");
        var (roots, rootProblems) = ResolveRoots();
        var sentences = new List<Sentence>();
        var parseProblems = new List<string>();
        var agreeProblems = new List<string>();
        long chars = 0;
        int files = 0, literals = 0, inHoles = 0;

        foreach (var root in roots)
        {
            int rootFiles = 0, rootSentences = 0, rootHoles = 0;
            foreach (var file in SourceFiles(root))
            {
                rootFiles++;
                var label = Rel(file);
                string text;
                try { text = File.ReadAllText(file); }
                catch (Exception ex) { parseProblems.Add($"{label}: could not be read — {ex.GetType().Name}: {ex.Message}"); continue; }

                // Each reader is attempted separately and a throw NAMES ITS FILE. The second design wrapped the
                // whole guard in one catch, so a single malformed escape reported "the guard threw" and took all
                // 38 arms with it, naming nothing.
                List<SourceLiteral> a;
                try
                {
                    a = RoslynLiteralReader.Read(text, out var errs);
                    foreach (var e in errs) parseProblems.Add($"{label}: {e}");
                }
                catch (Exception ex) { parseProblems.Add($"{label}: READER A threw — {ex.GetType().Name}: {ex.Message}"); continue; }

                List<SourceLiteral>? b = null;
                try { b = HandLiteralLexer.Read(text); }
                catch (Exception ex) { agreeProblems.Add($"{label}: READER B threw — {ex.GetType().Name}: {ex.Message}"); }
                if (b is not null) agreeProblems.AddRange(Disagreements(label, a, b));

                foreach (var s in MergeSentences(text, a))
                {
                    if (string.IsNullOrWhiteSpace(s.Text)) continue;
                    sentences.Add(new Sentence($"{label}:{s.Line}", s.Text));
                    rootSentences++;
                    chars += s.Text.Length;
                }
                literals += a.Count;
                rootHoles += a.Count(l => l.Depth > 0);
            }
            files += rootFiles;
            inHoles += rootHoles;
            Console.WriteLine($"        {root}: {rootFiles} file(s), {rootSentences} literal sentence(s), {rootHoles} literal(s) inside interpolation holes");
            if (rootFiles == 0) rootProblems.Add($"source root '{root}' holds no .cs file — the tree moved, and this scan is reading nothing");
            if (rootSentences == 0) rootProblems.Add($"source root '{root}' yielded no string literal at all — the readers are not reading this tree");
        }

        Console.WriteLine($"        total: {files} file(s), {literals} literal(s) ({inHoles} inside interpolation holes), {sentences.Count} sentence(s), {chars / 1000}k char(s)");
        Console.WriteLine("        NOT in reach (the by-construction claim is exactly as honest as this list):");
        foreach (var n in NotInReach) Console.WriteLine($"          · {n}");

        var scanned = roots.Select(r => Path.GetFileName(r)!).ToList();
        rootProblems.AddRange(MissingTrees(scanned));
        Check($"GREEN-ROOTS   every tree that SHIPS was found and scanned, and it is the set written down independently here ({scanned.Count} of {PublishedShippedTrees.Length}: {string.Join(", ", scanned)})",
            rootProblems.Count == 0, rootProblems);
        Check($"INV6-PARSE    every scanned file parses as C# ({files} file(s)) — a file the parser rejects is a file whose literals are not trustworthy",
            parseProblems.Count == 0, parseProblems);
        Check($"INV6-AGREE    the two independently written readers agree about every literal in every file ({literals} literal(s), {inHoles} of them inside interpolation holes)",
            agreeProblems.Count == 0, agreeProblems);
        Console.WriteLine();
        return sentences;
    }

    /// <summary>Where the two readers disagree about one file, as an author-readable difference.
    /// <para>The comparison is a MULTISET of (depth, text) rather than an ordered walk: the readers arrive at the
    /// same literals by different routes (a syntax tree in document order, a scanner in source order), and
    /// requiring them to agree about ORDER would be asserting a shared implementation detail instead of the fact
    /// that matters — that neither of them stopped reading.</para></summary>
    static List<string> Disagreements(string label, List<SourceLiteral> a, List<SourceLiteral> b)
    {
        var left = Bag(a);
        var right = Bag(b);
        var problems = new List<string>();
        foreach (var key in left.Keys.Union(right.Keys))
        {
            left.TryGetValue(key, out int la);
            right.TryGetValue(key, out int lb);
            if (la == lb) continue;
            var (depth, text) = key;
            problems.Add($"{label}: reader A found {la} and reader B found {lb} of the literal at hole-depth {depth} "
                       + $"— one of them stopped reading. Text: \"{Clip(text, 90)}\"");
        }
        if (problems.Count > 8)
            problems = problems.Take(8).Append($"{label}: … and {problems.Count - 8} further disagreements in this file — "
                                             + "a divergence this wide is a reader that lost its place, not a handful of literals").ToList();
        return problems;
    }

    static Dictionary<(int Depth, string Text), int> Bag(List<SourceLiteral> lits)
    {
        var bag = new Dictionary<(int, string), int>();
        foreach (var l in lits)
        {
            var key = (l.Depth, l.Text);
            bag[key] = bag.TryGetValue(key, out int n) ? n + 1 : 1;
        }
        return bag;
    }

    /// <summary>An excerpt with its line breaks made visible. The carriage return is RENDERED rather than
    /// stripped: a disagreement about line endings is a real disagreement, and hiding the character would print
    /// both sides of it identically.</summary>
    static string Clip(string s, int max) =>
        (s.Length <= max ? s : s[..max] + "…").Replace("\r", "\\r").Replace("\n", "\\n");

    /// <summary>Only whitespace and a single <c>+</c> between two literals — the shape of one authored sentence
    /// wrapped across lines.</summary>
    static readonly Regex Join = new(@"^\s*\+\s*$", RegexOptions.Compiled);

    /// <summary>Adjacent TOP-LEVEL literals joined by nothing but <c>+</c> are ONE sentence: that is how every
    /// long description and every shared refusal in this codebase is written, and reading them apart would split a
    /// phrase from the clause that redeems it. A run breaks at anything else — a const reference, a method call,
    /// an argument separator — which keeps the merge conservative: it never joins two things an author wrote
    /// apart.
    /// <para>A literal INSIDE an interpolation hole is its own sentence and never merges, because what surrounds
    /// it is an expression rather than prose. Its neighbours in the source are the ternary's other arm and the
    /// text around the hole, none of which the author wrote as one sentence with it.</para></summary>
    static List<SourceLiteral> MergeSentences(string src, List<SourceLiteral> lits)
    {
        var outp = new List<SourceLiteral>();
        var top = lits.Where(l => l.Depth == 0).OrderBy(l => l.Start).ToList();
        foreach (var lit in top)
        {
            if (outp.Count > 0 && outp[^1].End <= lit.Start && Join.IsMatch(src[outp[^1].End..lit.Start]))
                outp[^1] = outp[^1] with { Text = outp[^1].Text + lit.Text, End = lit.End };
            else
                outp.Add(lit);
        }
        outp.AddRange(lits.Where(l => l.Depth > 0));
        return outp;
    }

    // ================= INV1 / INV2: the consent vocabulary =================

    static void VocabularyArm(List<Sentence> sentences)
    {
        Console.WriteLine("── VOCABULARY: each phrase is absent from the shipped literals, or carries the clause that makes it true ──");
        var used = new HashSet<Exemption>();
        foreach (var rule in Phrases)
        {
            var (violations, carriers, exempted) = Scan(rule, sentences, Exemptions, used);
            var shape = rule.Companions.Length == 0
                ? "absent from every shipped literal"
                : $"never stated without one of: {string.Join(" / ", rule.Companions.Select(c => $"\"{c}\""))}";
            Check($"INV1 \"{rule.Phrase}\" — {shape}  [{carriers} carrier(s), {exempted} exempt]", violations.Count == 0, violations);
        }

        // The table is empty today, so an INV2 arm would assert nothing about nothing and pass on every possible
        // input — the shape CLAUDE.md's case law says to delete rather than strengthen. It materialises with the
        // first declared row; until then the claim lives in RED-DEADEXEMPT / GREEN-DEADEXEMPT, where the same
        // detector runs against a synthetic table and can actually fail.
        if (Exemptions.Length > 0)
        {
            var dead = DeadExemptions(Exemptions, used);
            Check($"INV2 every declared exemption still matches a real site ({Exemptions.Length} declared)", dead.Count == 0, dead);
        }
        else
        {
            Console.WriteLine("        exemptions declared: 0 — INV2 has no arm to run (it materialises with the first row)");
        }

        // INV2-DEGEN always runs, empty table or not: it is a claim about the TABLE, not about its rows.
        var degen = Degenerate(Exemptions);
        Check($"INV2-DEGEN the exemption table cannot absorb an arbitrary miss ({Exemptions.Length} of at most {MaxExemptions} declared, each scoped to a named file with a ground)",
            degen.Count == 0, degen);
        Console.WriteLine();
    }

    /// <summary>The whole checker, over any (label, text) set and any exemption table — so the RED arms can drive
    /// exactly this code, both branches of the exemption conditional included, with synthetic input rather than a
    /// re-implementation of it that could agree with a broken original.</summary>
    static (List<string> Violations, int Carriers, int Exempted) Scan(
        VocabRule rule, IEnumerable<Sentence> sentences, Exemption[] exemptions, HashSet<Exemption>? used)
    {
        var violations = new List<string>();
        int carriers = 0, exempted = 0;
        foreach (var s in sentences)
        {
            if (s.Text.IndexOf(rule.Phrase, StringComparison.OrdinalIgnoreCase) < 0) continue;
            carriers++;
            var hit = exemptions.Where(e =>
                string.Equals(e.Phrase, rule.Phrase, StringComparison.Ordinal)
                && s.Label.Contains(e.SiteContains, StringComparison.OrdinalIgnoreCase)).ToList();
            // Exemptions are recorded BEFORE the companion test short-circuits, so a row that covers a sentence
            // which later grew its own correction clause still reads as live rather than as dead. INV2 reports an
            // exemption that matches nothing; it should not be made to report one whose site simply got better.
            foreach (var e in hit) used?.Add(e);
            if (rule.Companions.Any(c => s.Text.Contains(c, StringComparison.OrdinalIgnoreCase))) continue;
            if (hit.Count > 0) { exempted++; continue; }
            violations.Add($"{s.Label}: says \"{rule.Phrase}\" — {rule.Ground}"
                + (rule.Companions.Length == 0
                    ? ""
                    : $" (nothing in it states {string.Join(" or ", rule.Companions.Select(c => $"\"{c}\""))})")
                + $"\n            … {Excerpt(s.Text, rule.Phrase)}");
        }
        return (violations, carriers, exempted);
    }

    /// <summary>Exemptions that fired on nothing. An exemption nobody can see firing is cover for the next stale
    /// claim, so it is reported rather than left to rot.</summary>
    static List<string> DeadExemptions(Exemption[] exemptions, HashSet<Exemption> used) =>
        exemptions.Where(e => !used.Contains(e))
            .Select(e => $"the exemption for \"{e.Phrase}\" at sites containing '{e.SiteContains}' matched nothing — "
                       + "the claim it covered is gone; delete the row rather than leave it as cover for the next one")
            .ToList();

    /// <summary>Whether the exemption table has stopped being a handful of recorded decisions. Three separate
    /// ways it can: too many rows, a row scoped so broadly it exempts a whole tree rather than a site, and a row
    /// with no ground, which is a hole with a comment where its reason should be.</summary>
    static List<string> Degenerate(Exemption[] exemptions)
    {
        var problems = new List<string>();
        if (exemptions.Length > MaxExemptions)
            problems.Add($"{exemptions.Length} exemptions declared, more than the {MaxExemptions} this guard may carry — a list that "
                       + "can absorb any miss is an allowlist of the surface, not a guard. This is a CLAUDE.md §4 escalation about "
                       + "the phrase list or the surface, not a number to raise in the commit that needed it raised.");
        foreach (var e in exemptions)
        {
            if (!e.SiteContains.Contains(".cs", StringComparison.OrdinalIgnoreCase))
                problems.Add($"the exemption for \"{e.Phrase}\" is scoped to '{e.SiteContains}', which names no .cs file — an exemption "
                           + "must name the site it covers, or it covers whatever drifts into matching it");
            if (e.Ground.Trim().Length < 40)
                problems.Add($"the exemption for \"{e.Phrase}\" at '{e.SiteContains}' carries no usable ground — an exemption without a "
                           + "reason cannot be pruned by anyone who did not write it");
            if (!Phrases.Any(p => string.Equals(p.Phrase, e.Phrase, StringComparison.Ordinal)))
                problems.Add($"the exemption for \"{e.Phrase}\" names no declared phrase — it can never fire, and reads as cover that exists");
        }
        return problems;
    }

    static string Excerpt(string text, string phrase)
    {
        int i = Math.Max(0, text.IndexOf(phrase, StringComparison.OrdinalIgnoreCase));
        int from = Math.Max(0, i - 60), to = Math.Min(text.Length, i + phrase.Length + 60);
        return (from > 0 ? "…" : "") + text[from..to].Replace("\n", " ") + (to < text.Length ? "…" : "");
    }

    // ================= enumeration SURFACE: the compiled [Description]s =================

    /// <summary>One compiled caller-facing string, with the reflection handle that knows what it annotates — which
    /// is what lets the marked-default arm ask the slot what it actually defaults to.</summary>
    readonly record struct SurfaceSite(string Label, string Text, MemberInfo? Member, ParameterInfo? Param);

    /// <summary>Every <c>[Description]</c> the tool assembly declares — on a type, on a member, on a method
    /// parameter. The whole tool surface a client reads, discovered without naming a single tool.
    /// <c>inherit: false</c> throughout, parameters included: an inherited description is declared by the base and
    /// would be counted there, and counting it twice would inflate a census this guard reports as a
    /// measurement.</summary>
    static IEnumerable<SurfaceSite> SurfaceSites()
    {
        foreach (var t in Surface.GetTypes().OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            if (Text(t.GetCustomAttribute<DescriptionAttribute>(inherit: false)) is { } td)
                yield return new SurfaceSite($"[Description] on type {t.Name}", td, t, null);

            foreach (var m in t.GetMembers(AllMembers).OrderBy(m => m.Name, StringComparer.Ordinal))
            {
                if (Text(m.GetCustomAttribute<DescriptionAttribute>(inherit: false)) is { } md)
                    yield return new SurfaceSite($"[Description] on {t.Name}.{m.Name}", md, m, null);
                if (m is not MethodBase method) continue;
                foreach (var p in method.GetParameters())
                    if (Text(p.GetCustomAttribute<DescriptionAttribute>(inherit: false)) is { } pd)
                        yield return new SurfaceSite($"[Description] on {t.Name}.{m.Name}({p.Name}=)", pd, m, p);
            }
        }
    }

    static string? Text(DescriptionAttribute? a) => string.IsNullOrWhiteSpace(a?.Description) ? null : a!.Description;

    /// <summary>Every string <c>const</c> the shipped assemblies declare. COMPILE-TIME CONSTANTS ONLY, and that
    /// restriction is the point: INV5 compares a runtime value against scanned SOURCE TEXT, and C# guarantees a
    /// const's value is built from literals, so the comparison is sound. A <c>static readonly</c> string can be
    /// built at runtime — interpolated, formatted, read from somewhere — and its value is then not source text at
    /// all. The second design compared them anyway and false-RED'd on an ordinary interpolated field with a
    /// message stating a false cause ("the SOURCE scan is not reading the file"). They are counted and named in
    /// the census instead; the completeness claim they were failing to serve is INV6-AGREE's, which covers every
    /// literal in every file regardless of what it is assigned to.</summary>
    static (List<(string Label, string Value)> Consts, int RuntimeBuilt) CompiledConsts()
    {
        var consts = new List<(string, string)>();
        int runtimeBuilt = 0;
        foreach (var asm in ShippedAssemblies)
            foreach (var t in asm.GetTypes().OrderBy(t => t.FullName, StringComparer.Ordinal))
                foreach (var f in t.GetFields(AllMembers).OrderBy(f => f.Name, StringComparer.Ordinal))
                {
                    if (f.FieldType != typeof(string) || !f.IsStatic) continue;
                    if (f.IsInitOnly && !f.IsLiteral) { runtimeBuilt++; continue; }
                    if (!f.IsLiteral) continue;
                    string? v;
                    try { v = f.GetValue(null) as string; } catch { continue; }
                    if (!string.IsNullOrWhiteSpace(v)) consts.Add(($"const {t.Name}.{f.Name}", v!));
                }
        return (consts, runtimeBuilt);
    }

    // ================= INV5: the source scan actually covers the compiled surface =================

    /// <summary>How much of a compiled string a single scanned sentence has to account for before it counts as
    /// covering it. A compiled constant is built from literals, so SOME literal chunk of it must be in the scan;
    /// requiring a substantial one is what stops a stray "." from covering everything and leaving the arm
    /// toothless. Short strings must be found whole.</summary>
    const int CoverChars = 24;

    static bool Covered(string value, IEnumerable<string> sentenceTexts)
    {
        int need = Math.Min(value.Length, CoverChars);
        return sentenceTexts.Any(t => t.Length >= need && value.Contains(t, StringComparison.Ordinal));
    }

    static void ReachArm(List<SurfaceSite> surface, List<Sentence> sentences)
    {
        Console.WriteLine("── REACH: the source scan covers every compile-time constant on the surface ──");
        var texts = sentences.Select(s => s.Text).ToList();

        var uncoveredDesc = surface.Where(s => !Covered(s.Text, texts))
            .Select(s => $"{s.Label}: no scanned source literal accounts for it — the SOURCE scan is not reading the file that declares it, "
                       + $"so INV1 is blind there. First {Math.Min(70, s.Text.Length)} chars: \"{s.Text[..Math.Min(70, s.Text.Length)]}\"")
            .ToList();
        Check($"INV5-DESCRIPTIONS every compiled [Description] is covered by a scanned source literal ({surface.Count} description(s))",
            uncoveredDesc.Count == 0, uncoveredDesc);

        var (consts, runtimeBuilt) = CompiledConsts();
        var uncoveredConst = consts.Where(c => !Covered(c.Value, texts))
            .Select(c => $"{c.Label}: no scanned source literal accounts for its value — the SOURCE scan is not reading the file that declares it")
            .ToList();
        Console.WriteLine($"        static readonly strings not compared: {runtimeBuilt} — their values can be built at runtime, so a "
                        + "source-text comparison would report a false cause. INV6-AGREE covers their literals.");
        Check($"INV5-CONSTS       every compile-time string const in the shipped assemblies is covered ({consts.Count} const(s))",
            uncoveredConst.Count == 0, uncoveredConst);
        Console.WriteLine();
    }

    // ================= INV3 / INV4: the verb recitals =================

    /// <summary>Parenthetical asides are lifted out before a recital is read, so "Set (default) | Add" reads as
    /// the two-token run it is rather than stopping at the aside. What the aside SAID is not lost — the
    /// marked-default arm reads it off the raw text.</summary>
    static readonly Regex Parenthetical = new(@"\([^()]*\)", RegexOptions.Compiled);

    /// <summary>A recital: two or more Capitalised tokens joined by <c>|</c> or <c>/</c>. Comma-joined lists are
    /// deliberately NOT read as recitals — every prose sentence listing capitalised things would become one, and a
    /// checker that guesses is worse than one with a stated edge.</summary>
    static readonly Regex Run = new(@"\b[A-Z][A-Za-z]*(?:\s*[|/]\s*[A-Z][A-Za-z]*)+", RegexOptions.Compiled);

    static void VerbArm(List<SurfaceSite> surface)
    {
        Console.WriteLine("── VERBS: every recital on the [Description] surface, and the homes the vocabulary comes from ──");
        var vocab = new HashSet<string>(PublishedVocabulary, StringComparer.Ordinal);
        var unknown = new List<string>();
        var named = new HashSet<string>(StringComparer.Ordinal);
        var marks = new List<(SurfaceSite Site, string Verb)>();
        int recitals = 0, dropped = 0;

        foreach (var s in surface)
        {
            foreach (var tokens in Recitals(s.Text, vocab))
            {
                recitals++;
                int real = 0;
                foreach (var tok in tokens)
                    if (vocab.Contains(tok)) { named.Add(tok); real++; }
                    else unknown.Add($"{s.Label}: recites \"{string.Join(" | ", tokens)}\" — '{tok}' is not a write verb "
                                   + $"(the vocabulary is {string.Join(", ", PublishedVocabulary)})");
                // The count of REAL verbs, not a FULL/subset verdict. Several of these runs are prose ("omit
                // value= for Remove / ReplaceAll / Merge"), not vocabulary declarations at all, and labelling them
                // as partial lists would assert an intent the text does not have.
                Console.WriteLine($"        names {real}/{PublishedVocabulary.Length}  [{string.Join(" | ", tokens)}]  {s.Label}");
            }
            foreach (var d in DroppedRuns(s.Text, vocab))
            {
                dropped++;
                Console.WriteLine($"        (not read as a verb recital)  [{d}]  {s.Label}");
            }
            // Marked defaults are read from the WHOLE description, not from inside the recital loop. The second
            // design collected them only for runs the admission test had already accepted, so a "(default)" on a
            // run that was dropped — or on a slot that recites nothing — entered neither the published-default
            // check nor the slot comparison, and the arm's "a general rule over whatever marks a default" was
            // false for exactly the sites nothing else was watching.
            foreach (var verb in MarkedDefaults(s.Text)) marks.Add((s, verb));
        }

        Check($"INV3-TOKENS   every verb recited on the surface is a real verb ({recitals} recital(s) read, {dropped} run(s) not read as recitals)",
            unknown.Count == 0, unknown);

        var unrecited = PublishedVocabulary.Where(v => !named.Contains(v)).ToList();
        Check("INV3-UNION    between them the recitals name the whole published vocabulary — a verb no description mentions is a verb no caller can find",
            unrecited.Count == 0,
            unrecited.Select(v => $"'{v}' is a write verb that no [Description] recital names").ToList());

        Check("INV4-HOMES    WriteVerbs.All and WriteVerbs.AllRecital agree with each other AND with the vocabulary written independently here",
            HomesAgree(WriteVerbs.All, WriteVerbs.AllRecital, PublishedVocabulary),
            new() { $"All=[{string.Join(",", WriteVerbs.All)}] AllRecital=[{string.Join(",", RecitalNames(WriteVerbs.AllRecital))}] "
                  + $"independent=[{string.Join(",", PublishedVocabulary)}]" });

        Check($"INV4-MARK     WriteVerbs.AllRecital marks exactly one verb (default), and it is '{PublishedDefault}'",
            MarkedDefaults(WriteVerbs.AllRecital) is [var only] && only == PublishedDefault,
            new() { $"AllRecital marks [{string.Join(",", MarkedDefaults(WriteVerbs.AllRecital))}] — the const feeds three shipped "
                  + "descriptions, so one edit here mis-states the default in all three at once" });

        DefaultSlotsArm(marks);
        TailGlossArm(surface);
        Console.WriteLine();
    }

    /// <summary>The verbs a text marks <c>(default)</c>, in order. Deliberately a LIST rather than a single value,
    /// so "marks two" and "marks none" are both visible failures rather than one of them silently reading as the
    /// other.</summary>
    static List<string> MarkedDefaults(string text) =>
        Regex.Matches(text, @"\b([A-Za-z][A-Za-z]*)\s*\(\s*default\s*\)").Select(m => m.Groups[1].Value).ToList();

    /// <summary>The default marked on the SURFACE is the published verb wherever the marked token is a write verb,
    /// and where the slot it annotates declares a default value in code, the two agree. A general rule over
    /// whatever marks a default — no site list — so a new write slot enrols itself.
    /// <para>A marker whose token is NOT a write verb is a different kind of default (an output format, a mode);
    /// those are counted and named in the census rather than asserted about, because this arm has no opinion on
    /// what a non-verb slot should default to. A stale VERB name marked default is still caught: it is either a
    /// real verb that is not the published default, or a token INV3-TOKENS reports from the recital carrying
    /// it.</para>
    /// <para>Sites whose slot declares no default in code (a nullable property coalesced downstream) are counted
    /// and named rather than dropped: what cannot be compared is reported, never passed over in silence (Q3).</para></summary>
    static void DefaultSlotsArm(List<(SurfaceSite Site, string Verb)> marks)
    {
        var vocab = new HashSet<string>(PublishedVocabulary, StringComparer.Ordinal);
        var problems = new List<string>();
        var nonVerb = new List<string>();
        int compared = 0, unreadable = 0, verbMarks = 0;

        foreach (var (site, verb) in marks)
        {
            if (!vocab.Contains(verb)) { nonVerb.Add($"{verb} @ {site.Label}"); continue; }
            verbMarks++;
            if (verb != PublishedDefault)
                problems.Add($"{site.Label}: marks '{verb}' (default), but the published default is '{PublishedDefault}'");

            var declared = DeclaredDefault(site);
            if (declared is null) { unreadable++; continue; }
            compared++;
            if (!string.Equals(declared, verb, StringComparison.Ordinal))
                problems.Add($"{site.Label}: the description marks '{verb}' (default) but the slot actually defaults to '{declared}'");
        }

        if (verbMarks == 0)
            problems.Add("no [Description] on the surface marks a write verb (default) at all — the marker that three shipped "
                       + "descriptions carry has gone, and nothing tells a caller which verb they get by omitting op=/verb=");

        Console.WriteLine($"        marked defaults: {verbMarks} on a write verb ({compared} whose slot declares a default in code, "
                        + $"{unreadable} that declare none), {nonVerb.Count} on something that is not a verb"
                        + (nonVerb.Count == 0 ? "" : $": {string.Join("; ", nonVerb)}"));
        Check($"INV4-DEFAULT  every verb marked (default) on the surface is '{PublishedDefault}', and agrees with the slot's own declared default where there is one",
            problems.Count == 0, problems);
    }

    /// <summary>The recital's TAIL is the verb the gloss glued to that tail describes.
    /// <para><b>The hazard this pins.</b> <c>BulkOp.verb</c>'s description is <c>AllRecital</c> with a
    /// parenthetical appended directly onto it, and that parenthetical glosses ONE verb — the one the recital
    /// happens to end with. Appending a ninth verb to the const, which is exactly the edit the const exists to
    /// make sufficient at one site instead of three, moves the gloss onto the new verb and strips it off the old
    /// one; the tool schema then ships a false claim and no other arm sees it, because the recital is still
    /// complete and every token in it is still a real verb. Reordering does the same. The other two conversion
    /// sites are position-independent — one appends after a full stop, one reads <c>"op is " + AllRecital + ". "</c>.</para>
    /// <para><b>What it does NOT establish.</b> It does not check that the gloss is a TRUE statement about the
    /// verb it lands on — that is authored prose, the residue #337/#330 ruled non-mechanizable, and the guard
    /// catches unknown vocabulary rather than false sentences. What it makes structural is the COUPLING: the
    /// gloss's subject is identified by position, so the position is now a checked fact.</para>
    /// <para>If a future edit makes the gloss name its own subject, the pin steps aside rather than punishing the
    /// improvement: a glued parenthetical that names exactly one verb is held against THAT verb, since it no
    /// longer depends on position at all.</para></summary>
    static void TailGlossArm(IEnumerable<SurfaceSite> surface)
    {
        var problems = new List<string>();
        var tail = RecitalNames(WriteVerbs.AllRecital).LastOrDefault();
        var glued = surface
            .Select(s => (Site: s, Gloss: GluedGloss(s.Text, WriteVerbs.AllRecital)))
            .Where(x => x.Gloss is not null)
            .ToList();

        if (glued.Count == 0)
            problems.Add($"no [Description] appends a parenthetical directly onto WriteVerbs.AllRecital any more — this arm is "
                       + "asserting about nothing. Either the gloss moved (delete this arm and TailGlossVerb with it) or the "
                       + "recital stopped reaching that description (which is INV3's problem, and it did not see it).");

        foreach (var (site, gloss) in glued)
        {
            var namesInGloss = PublishedVocabulary.Where(v => gloss!.Contains(v, StringComparison.Ordinal)).Distinct().ToList();
            var subject = namesInGloss.Count == 1 ? namesInGloss[0] : TailGlossVerb;
            string how = namesInGloss.Count == 1 ? "the verb it names" : $"'{TailGlossVerb}', the verb it is written about";
            if (!string.Equals(tail, subject, StringComparison.Ordinal))
                problems.Add($"{site.Label}: the parenthetical glued to the recital glosses {how}, but the recital now ends with "
                           + $"'{tail}'. The gloss has moved onto '{tail}' and off '{subject}', and the tool schema is shipping "
                           + $"that claim. Gloss: \"{Clip(gloss!, 90)}\"");
        }

        Check($"INV4-TAILGLOSS the verb AllRecital ends with is the one the glued gloss describes ('{TailGlossVerb}', {glued.Count} glued site(s))",
            problems.Count == 0, problems);
    }

    /// <summary>The parenthetical that sits IMMEDIATELY after <paramref name="recital"/> inside
    /// <paramref name="text"/>, or null when there is none. "Immediately" means whitespace only in between: a
    /// parenthetical further along the sentence belongs to that sentence, not to the recital's last token.</summary>
    static string? GluedGloss(string text, string recital)
    {
        int at = text.IndexOf(recital, StringComparison.Ordinal);
        if (at < 0) return null;
        int i = at + recital.Length;
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        if (i >= text.Length || text[i] != '(') return null;
        int depth = 0;
        for (int j = i; j < text.Length; j++)
        {
            if (text[j] == '(') depth++;
            else if (text[j] == ')' && --depth == 0) return text[(i + 1)..j];
        }
        return null;
    }

    /// <summary>The default value the annotated slot actually uses, or null when it declares none. An optional
    /// parameter carries it directly; a property carries it as an initializer, which means instantiating the
    /// declaring type to read it.</summary>
    static string? DeclaredDefault(SurfaceSite site)
    {
        if (site.Param is { HasDefaultValue: true } p) return p.DefaultValue as string;
        if (site.Member is PropertyInfo { CanRead: true } prop && prop.PropertyType == typeof(string))
        {
            try
            {
                var instance = Activator.CreateInstance(prop.DeclaringType!, nonPublic: true);
                return instance is null ? null : prop.GetValue(instance) as string;
            }
            catch { return null; }
        }
        return null;
    }

    /// <summary>Whether the two verb homes and the independently-written vocabulary all say the same thing. A
    /// function of its inputs rather than of the live consts, so a RED arm can drive it with a disagreement.</summary>
    static bool HomesAgree(IEnumerable<string> all, string recital, IEnumerable<string> independent)
    {
        var pin = independent.ToList();
        return all.SequenceEqual(pin, StringComparer.Ordinal) && RecitalNames(recital).SequenceEqual(pin, StringComparer.Ordinal);
    }

    /// <summary>Every verb recital in one string: a separator-joined run of Capitalised tokens, at least one of
    /// which is a known verb — so "MO2 / xEdit" and "Text | Json" are not read as verb lists.</summary>
    static IEnumerable<List<string>> Recitals(string text, HashSet<string> vocab)
    {
        foreach (var tokens in AllRuns(text))
            if (tokens.Any(vocab.Contains)) yield return tokens;
    }

    /// <summary>The runs the admission test above THROWS AWAY, rendered for the census. A run whose every token
    /// went stale at once looks exactly like an ordinary non-verb run, so the guard cannot separate them — what it
    /// can do is print them, which is the difference between a stated blind spot and a hidden one.</summary>
    static IEnumerable<string> DroppedRuns(string text, HashSet<string> vocab)
    {
        foreach (var tokens in AllRuns(text))
            if (!tokens.Any(vocab.Contains)) yield return string.Join(" | ", tokens);
    }

    static IEnumerable<List<string>> AllRuns(string text)
    {
        foreach (Match m in Run.Matches(Parenthetical.Replace(text, " ")))
        {
            var tokens = m.Value.Split('|', '/').Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
            if (tokens.Count >= 2) yield return tokens;
        }
    }

    /// <summary>The verb names a const recital states, read the same way a description's is.</summary>
    static List<string> RecitalNames(string recital) =>
        Parenthetical.Replace(recital, " ").Split('|').Select(t => t.Trim()).Where(t => t.Length > 0).ToList();

    // ================= RED arms =================

    /// <summary>Every checker this guard relies on, driven with synthetic input so a green run means each of them
    /// can still fail: the phrase scanner in both of its outcomes, both branches of the exemption conditional, the
    /// dead-exemption detector and the degeneration tripwire in both directions, the recital reader in both
    /// directions, the marked-default reader, the vocabulary-homes comparison, the glued-gloss reader, the
    /// sentence merge, INV5's coverage predicate, the reader-agreement comparison, and BOTH READERS over a
    /// fixture carrying every literal shape that has previously gone unread. The arms name what they drive;
    /// nothing here claims reach for a checker it does not run.</summary>
    static void RedArms()
    {
        Console.WriteLine("── RED: every checker driven with synthetic input, so a green run means it can still fail ──");
        var banned = Phrases.First(p => p.Companions.Length == 0);
        var companioned = Phrases.First(p => p.Companions.Length > 0);
        var none = Array.Empty<Exemption>();

        var (v1, _, _) = Scan(banned, new[] { new Sentence("RED synthetic site", $"…this prompt is {banned.Phrase} for the plugin.") }, none, null);
        Check($"RED-BANNED       a synthetic sentence saying \"{banned.Phrase}\" is reported", v1.Count == 1, v1, redArm: true);

        var (v2, _, _) = Scan(companioned, new[] { new Sentence("RED synthetic site", $"Confirms the {companioned.Phrase} trade-off.") }, none, null);
        Check($"RED-COMPANION    a synthetic \"{companioned.Phrase}\" with NO correction clause is reported", v2.Count == 1, v2, redArm: true);

        var (v3, _, _) = Scan(companioned, new[] { new Sentence("RED synthetic site",
            $"Confirms the {companioned.Phrase} trade-off — {companioned.Companions[0]}.") }, none, null);
        Check("GREEN-COMPANION  the same synthetic sentence WITH the correction clause is not reported", v3.Count == 0,
            v3.Concat(new[] { "the companion rule refuses a sentence that states its own correction — it would red the honest wording too" }).ToList());

        // Both branches of the exemption conditional. It ships with an empty table, so these are the only place it
        // runs at all — which is exactly why it needs an arm in each direction rather than none.
        var exemptSite = new Sentence("SomeFile.cs:12", $"…this prompt is {banned.Phrase} for the plugin.");
        var matching = new[] { new Exemption(banned.Phrase, "SomeFile.cs", "RED-arm fixture standing in for a real recorded decision, long enough to pass the ground test") };
        var wrongSite = new[] { new Exemption(banned.Phrase, "OtherFile.cs", "RED-arm fixture standing in for a real recorded decision, long enough to pass the ground test") };
        var usedHit = new HashSet<Exemption>();
        var (v4, _, ex4) = Scan(banned, new[] { exemptSite }, matching, usedHit);
        Check("GREEN-EXEMPT     a declared exemption whose site matches SUPPRESSES the violation", v4.Count == 0 && ex4 == 1,
            new() { $"{v4.Count} violation(s), {ex4} exempted — the exemption branch did not fire on a matching site" });

        var (v5, _, ex5) = Scan(banned, new[] { exemptSite }, wrongSite, null);
        Check("RED-EXEMPT       an exemption declared for a DIFFERENT site does not suppress it", v5.Count == 1 && ex5 == 0, v5, redArm: true);

        Check("GREEN-DEADEXEMPT an exemption that fired is not reported dead", DeadExemptions(matching, usedHit).Count == 0,
            new() { "the detector reported an exemption that had just matched a site — every live exemption would read as dead" });

        var deadReport = DeadExemptions(wrongSite, new HashSet<Exemption>());
        Check("RED-DEADEXEMPT   an exemption that matched nothing is reported", deadReport.Count == 1, deadReport, redArm: true);

        var overCap = Enumerable.Range(0, MaxExemptions + 1)
            .Select(i => new Exemption(banned.Phrase, $"File{i}.cs", "RED-arm fixture standing in for a real recorded decision, long enough to pass the ground test"))
            .ToArray();
        Check($"RED-DEGEN        an exemption table over the cap, one scoped to no file, and one with no ground are each reported",
            Degenerate(overCap).Count == 1
                && Degenerate(new[] { new Exemption(banned.Phrase, "everything", "a ground long enough to pass the length test but scoped to no file at all") }).Count == 1
                && Degenerate(new[] { new Exemption(banned.Phrase, "File.cs", "because") }).Count == 1
                && Degenerate(matching).Count == 0,
            new() { "the degeneration tripwire misses an over-cap table, an unscoped row, or a row with no ground — or reports a "
                  + "well-formed one, which would make every legitimate exemption impossible to declare" }, redArm: true);

        var vocab = new HashSet<string>(PublishedVocabulary, StringComparer.Ordinal);
        var red = Recitals("op is Set (default) | Add | Frobnicate.", vocab).ToList();
        Check("RED-VERB         a synthetic recital carrying a token that is not a verb is read, and the token is visible",
            red.Count == 1 && red[0].Count == 3 && red[0].Contains("Frobnicate"),
            new() { $"read {red.Count} recital(s): {string.Join(" ;; ", red.Select(r => string.Join("|", r)))}" }, redArm: true);

        Check("RED-NOTVERB      a separator-joined run with no verb in it is NOT read as a recital",
            !Recitals("Output format is Text | Json.", vocab).Any()
                && DroppedRuns("Output format is Text | Json.", vocab).Count() == 1,
            new() { "a non-verb run was read as a verb recital (or vanished from the census) — INV3 would fill with "
                  + "false reds, or the dropped-run census would stop showing what the admission test throws away" }, redArm: true);

        Check("RED-MARK         a recital marking the wrong verb (default) is read as marking that verb",
            MarkedDefaults("Set | Add (default) | Remove") is [var mis] && mis == "Add"
                && MarkedDefaults("Set | Add | Remove").Count == 0
                && MarkedDefaults("Set (default) | Add (default)").Count == 2
                && MarkedDefaults("nothing recited here, but Json (default) is marked") is ["Json"],
            new() { "the marked-default reader does not see a moved, missing, or duplicated marker — or does not see one "
                  + "OUTSIDE a recital, which is the site class the second design collected nothing from" }, redArm: true);

        Check("RED-HOMES        the vocabulary-homes comparison reports a disagreement",
            !HomesAgree(PublishedVocabulary, string.Join(" | ", PublishedVocabulary.Take(PublishedVocabulary.Length - 1)), PublishedVocabulary)
                && HomesAgree(PublishedVocabulary, string.Join(" | ", PublishedVocabulary), PublishedVocabulary),
            new() { "the comparison passes a recital that is missing a verb, or fails one that is complete" }, redArm: true);

        // The tail-gloss pin, driven over the shape of the failing edit it exists to catch: a ninth verb appended
        // to the recital, which leaves every other verb arm green.
        const string ninth = "Set (default) | Add | Remove | SetAtIndex | InsertAtIndex | ReplaceAll | Merge | CopyFrom | Frobnicate";
        Check("RED-TAILGLOSS    appending a ninth verb moves the glued gloss onto it, and that is reported",
            RecitalNames(ninth).LastOrDefault() == "Frobnicate"
                && RecitalNames(WriteVerbs.AllRecital).LastOrDefault() == TailGlossVerb
                && GluedGloss($"{ninth} (deep-copy the field — see from_plugin). More prose.", ninth) is { } g && g.StartsWith("deep-copy", StringComparison.Ordinal)
                && GluedGloss($"{ninth}. A parenthetical (later in the sentence) is not glued.", ninth) is null,
            new() { "the tail reader or the glued-gloss reader is wrong: a ninth verb does not read as the tail, the glued "
                  + "parenthetical is not found, or a parenthetical further along the sentence is read as glued" }, redArm: true);

        Check("RED-COVER        INV5's coverage predicate reports a compiled string no literal accounts for",
            !Covered("a compiled description no source literal accounts for", new[] { "something else entirely" })
                && Covered("a compiled description no source literal accounts for", new[] { "a compiled description no source literal accounts for" })
                && !Covered("a compiled description no source literal accounts for", new[] { "." }),
            new() { "the coverage predicate accepts an uncovered string, rejects a covered one, or lets a one-character "
                  + "literal cover anything — INV5 would pass with the SOURCE scan reading nothing" }, redArm: true);

        Check("RED-ROOTS       a shipped tree missing from the scanned set is reported, and a complete set is not",
            MissingTrees(new[] { "housecarl-mcp", "housecarl-setup" }).Count == 1
                && MissingTrees(PublishedShippedTrees).Count == 0,
            new() { "the root pin does not notice a tree dropped from the scanned set, or reports one that is present — "
                  + "shortening the assembly list would shrink INV1's net and INV5's oracle together, in silence" }, redArm: true);

        AgreementArms();
        ReaderArms();
    }

    /// <summary>The reader-agreement COMPARISON, driven with synthetic literal sets. INV6-AGREE is the arm the
    /// whole by-construction claim rests on, and an agreement check that cannot report a disagreement would
    /// certify two readers that had both stopped reading.</summary>
    static void AgreementArms()
    {
        var same = new List<SourceLiteral> { new(1, 0, "alpha", 0, 5), new(2, 1, "beta", 6, 10) };
        var missing = new List<SourceLiteral> { new(1, 0, "alpha", 0, 5) };
        var wrongDepth = new List<SourceLiteral> { new(1, 0, "alpha", 0, 5), new(2, 0, "beta", 6, 10) };
        var doubled = new List<SourceLiteral> { new(1, 0, "alpha", 0, 5), new(2, 1, "beta", 6, 10), new(3, 1, "beta", 11, 15) };

        Check("GREEN-AGREE      two readers that found the same literals are not reported as disagreeing",
            Disagreements("F.cs", same, new List<SourceLiteral>(same)).Count == 0,
            new() { "identical reader output was reported as a disagreement — INV6-AGREE would be red on every file" });

        Check("RED-AGREE        a literal one reader missed, one it placed at the wrong hole depth, and one it counted twice are each reported",
            Disagreements("F.cs", same, missing).Count == 1
                && Disagreements("F.cs", same, wrongDepth).Count == 2
                && Disagreements("F.cs", same, doubled).Count == 1,
            new() { "the agreement comparison misses a dropped literal, a depth difference, or a duplicate — a reader that "
                  + "stopped reading inside an interpolation hole would look identical to one that did not" }, redArm: true);
    }

    /// <summary>BOTH readers over one fixture carrying every literal shape that has previously gone unread, plus
    /// the shapes an author uses every day. This is where the two designs before this one failed silently, so
    /// each shape is named: a literal inside an interpolation hole and inside a TERNARY in one; an APOSTROPHE
    /// inside such a nested literal (which flipped the second design's lexer into character-literal mode and lost
    /// the rest of the file); a URL, whose double slash read as a comment; a LONE SURROGATE escape, which made
    /// the second design throw and take all its arms with it; a raw string literal; escaped braces; a character
    /// literal holding a quote; and comment text, which must be read by neither.</summary>
    static void ReaderArms()
    {
        // Four quote characters open this fixture because it CONTAINS a three-quote raw string literal, which is
        // one of the shapes both readers have to get right.
        const string fixture = """"
            // a comment saying "not a literal at all"
            /* a block comment with "another non-literal" */
            var a = "plain";
            var b = @"verbatim ""quoted"" and \not\an\escape";
            var c = "escaped \"quote\" and an em dash \u2014 here";
            var d = $"interpolated {value} hole";
            var e = "one authored " +
                    "sentence across lines";
            var f = "left" + Something + "right";
            var g = '"';
            var h = $"a note: {(n == 1 ? "you will not be asked again" : "shown once")} — mind that.";
            var i = $"{(bad ? "it's gone" : "kept")} then \"shown once\" and \"never again\" and \"just once\".";
            var j = $"see {(x ? "https://example.invalid/a//b" : "none")} then \"only once\" survives.";
            var k = "a lone surrogate \uD83D stands alone";
            var l = $"escaped {{braces}} and a {nested} hole";
            var m = """
                a raw string
                over two lines
                """;
            """";

        var a = RoslynLiteralReader.Read(fixture, out var parseErrors);
        var b = HandLiteralLexer.Read(fixture);
        var divergence = Disagreements("fixture", a, b);

        Check($"GREEN-FIXTURE-PARSES the reader fixture is valid C# ({a.Count} literal(s) read by reader A)",
            parseErrors.Count == 0, parseErrors);

        Check($"GREEN-READERS-AGREE  both readers agree over every shape in the fixture ({b.Count} literal(s) read by reader B)",
            divergence.Count == 0, divergence);

        var want = new[]
        {
            "plain",
            "verbatim \"quoted\" and \\not\\an\\escape",
            "escaped \"quote\" and an em dash \u2014 here",
            "one authored sentence across lines",
            "left",
            "right",
            "you will not be asked again",
            "shown once",
            "it's gone",
            "https://example.invalid/a//b",
            "a lone surrogate \uD83D stands alone",
            "a raw string\nover two lines",
        };
        var sentences = MergeSentences(fixture, a).Select(l => l.Text).ToList();
        // Compared with line terminators normalized on BOTH sides. This fixture is a raw string in this file, so
        // its own line endings are whatever the checkout used — LF here, CRLF after git's Windows conversion — and
        // a raw literal keeps them. This arm is about which SHAPES reach a scannable sentence; whether the two
        // readers agree about a terminator is INV6-AGREE's question, and it asks it over the whole shipped tree in
        // whichever form that tree was checked out.
        static string Nl(string s) => s.Replace("\r\n", "\n");
        var flat = sentences.Select(Nl).ToList();
        var missing = want.Where(w => !flat.Contains(Nl(w), StringComparer.Ordinal)).Select(w => $"no sentence equals: \"{Clip(w, 60)}\"").ToList();
        var leaked = sentences.Where(s => s.Contains("non-literal", StringComparison.Ordinal) || s.Contains("not a literal", StringComparison.Ordinal))
            .Select(s => $"COMMENT text was read as a literal: \"{Clip(s, 60)}\" — every docstring would enter INV1's net").ToList();
        var joined = sentences.Contains("leftright", StringComparer.Ordinal)
            ? new List<string> { "two literals separated by an expression were merged — the merge would join things an author wrote apart" }
            : new List<string>();
        Check($"GREEN-SHAPES     every shape in the fixture reaches a scannable sentence, and comment text reaches none ({sentences.Count} sentence(s))",
            missing.Count == 0 && leaked.Count == 0 && joined.Count == 0,
            missing.Concat(leaked).Concat(joined).ToList());

        // The whole point of the fixture: the phrases planted inside interpolation holes are REACHABLE. Three of
        // them sit behind the exact shapes that were measured green on live shipped prose one design ago.
        var labelled = MergeSentences(fixture, a).Select((l, n) => new Sentence($"fixture:{n}", l.Text)).ToList();
        const int PlantedInFixture = 6;
        var holeHits = Phrases
            .Where(p => p.Companions.Length == 0)
            .Sum(p => Scan(p, labelled, Array.Empty<Exemption>(), null).Violations.Count);
        Check($"RED-HOLES        every phrase planted behind an interpolation hole is reported ({holeHits} of {PlantedInFixture})",
            holeHits == PlantedInFixture,
            new() { $"{holeHits} found, {PlantedInFixture} planted. They are: \"asked again\" and \"shown once\" INSIDE the arms of a "
                  + "ternary in a hole (invisible to the second design entirely); \"shown once\", \"never again\" and \"just once\" "
                  + "in the text AFTER a hole holding an apostrophe (which used to flip the lexer into character-literal mode and "
                  + "swallow the rest of the file); and \"only once\" after a hole holding a URL (whose double slash used to read "
                  + "as a comment). Fewer means a shape went invisible again; more means the fixture grew a phrase and this count "
                  + "was not moved with it." }, redArm: true);

        Check("RED-COMMENTS     a phrase planted in a COMMENT is NOT reported — the declared docstring boundary, tested rather than asserted",
            Scan(Phrases[0], MergeSentences("// a one-time thing\n", RoslynLiteralReader.Read("// a one-time thing\n", out _))
                    .Select((l, n) => new Sentence($"fixture:{n}", l.Text)).ToList(), Array.Empty<Exemption>(), null).Violations.Count == 0
                && Scan(Phrases[0], MergeSentences("var x = \"a one-time thing\";", RoslynLiteralReader.Read("var x = \"a one-time thing\";", out _))
                    .Select((l, n) => new Sentence($"fixture:{n}", l.Text)).ToList(), Array.Empty<Exemption>(), null).Violations.Count == 1,
            new() { "the scanner does not see a phrase in a literal, or DOES see one in a comment — the declared "
                  + "docstring boundary would be false in one direction or the other" }, redArm: true);
    }

    // ================= reporting =================

    static void Check(string label, bool ok, List<string> detail, bool redArm = false)
    {
        Console.WriteLine($"   [{(ok ? "PASS" : "FAIL")}] {label}");
        if (!ok)
        {
            if (detail.Count == 0)
                Console.WriteLine(redArm ? "        - (the checker reported NO violation — it is toothless)" : "        - (no detail)");
            foreach (var d in detail.Take(20)) Console.WriteLine($"        - {d}");
            // Never a silent cut (Q3): a 58-violation failure showing 20 rows reads like a 20-violation one.
            if (detail.Count > 20) Console.WriteLine($"        - … and {detail.Count - 20} more");
        }
        if (ok) _pass++; else _fail++;
    }
}
