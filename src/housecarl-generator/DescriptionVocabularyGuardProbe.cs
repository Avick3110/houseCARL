using System.ComponentModel;
using System.Reflection;
using System.Reflection.Metadata;
using System.Text;
using System.Text.RegularExpressions;
using HousecarlCore;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// REGRESSION GUARD (standing CI instrument, self-contained) — CALLER-FACING PROSE VOCABULARY (#386).
///
/// <para><b>The gap this closes.</b> Nothing read the WORDS of two caller-facing prose surfaces: the
/// <c>[Description]</c> attributes a model reads to decide how to call a tool, and the consent prompts a modder
/// reads to decide whether to say yes. <c>write-surface-guard</c> and the <see cref="MustStateAttribute"/> /
/// <see cref="NoClaimsAttribute"/> walk both police the <see cref="WriteSentences"/> / <see cref="ReadSentences"/>
/// consts and reach neither; <c>wire-names-guard</c> does reach a <c>[Description]</c>, but only to parse the
/// brace shape declaration out of it and hold that against the reflected wire names — it has no opinion about the
/// sentence the declaration sits in, and a stale consent claim is invisible to it. So the in-place consent fix (#378) — which changed exactly one fact, that a REFUSED
/// call records nothing — left stale text behind that took FOUR sweeps to clear: three, run surface by surface,
/// found 11 on the <c>acknowledge=</c> parameters, 6 in the <c>WriteTools</c> lane parentheticals and 2 in the
/// handshake builders, and a fourth, run repo-wide by VOCABULARY instead, found fourteen more in homes the first
/// three had no reason to look at. (The per-pass counts are #386's own table; its headline totals those three
/// passes as thirteen, which is a discrepancy in the issue rather than something to average here — the counts
/// are given per pass so nothing restates a total its own itemisation contradicts.) Three of the four were triggered by a reviewer noticing a claim by eye rather than by
/// anything going red.</para>
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
/// <para><b>And the files those readers are handed come from the COMPILER'S RECEIPT, not from a directory
/// walk.</b> Two readers agreeing about a file set nobody certified is the same shape one floor up, and it was
/// measured: a <c>&lt;Compile Include&gt;</c>-linked source file shipped a false consent claim at 59/59 green.
/// So <c>MANIFEST-SET</c> holds the scanned file set against the portable-PDB document table of each shipped
/// assembly — the compiler's own record of what went into the thing that ships — in both directions. See
/// <see cref="ReadManifest"/> for the ruling that chose it (§4, Aaron-go 2026-08-26), the alternatives declined,
/// and the evaluation run before it was built. Above that receipt there is no further enumeration to certify:
/// what remains — that the guard runs, what is on the watchlist, and whether a sentence of known words is TRUE —
/// is declared residue, printed on every run in <see cref="NotInReach"/>.</para>
///
/// <para><b>The enumerations, and the question each answers.</b>
/// <list type="bullet">
///   <item><b>SOURCE</b> — every string-literal SENTENCE in the three shipped trees (<c>housecarl-mcp</c>,
///         <c>housecarl-core</c>, <c>housecarl-setup</c>), a sentence being a maximal run of adjacent literals
///         joined by <c>+</c> OR a run of consecutive <c>Append</c> / <c>Write</c> calls on one receiver,
///         optionally finished by the <c>Line</c> form of that verb: the unit an author writes. This answers
///         ABSENCE (INV1) — a phrase absent from every literal is absent from every string built out of them by
///         those two shapes. Assembly across control flow or through a helper is a DECLARED
///         boundary, printed on every run (<see cref="NotInReach"/>), not a claim this quietly covers.</item>
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
///         not have that shape (it moves one name and leaves the rest); the marked-default arm reaches such a run
///         only where it marks a default AND the annotated slot declares one in code, which is a narrower reach
///         than this paragraph used to claim; and the census prints every dropped run, and every marker whose
///         slot declares nothing, so what is asserted about by nothing is named on every run.</item>
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
    static int _pass, _fail, _class1, _class2, _harness;

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
            "reaches \"won't ask again\" / \"will not ask again\". This rule is BLUNT ON PURPOSE and the ground "
          + "says so rather than pretending otherwise: the span also sits inside honest wordings ('you may be "
          + "asked again'), and they are refused too. The settled way to say the true thing on this surface is "
          + "'may be needed again' / 'may see this again' — the correction clauses — so a sentence that reds here "
          + "is rephrased, not exempted."),
        new("asked again", NoCompanion,
            "the passive spelling — \"never asked again\", the wording sweep 1 actually found. A separate rule "
          + "because \"ask again\" does not reach it: these are substring tests, not stems. Blunt in the same way "
          + "and for the same reason as the rule above; the remedy is the settled phrasing, not an exemption row."),
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
        "prose assembled ACROSS CONTROL FLOW or through a helper — a run of Append calls broken by an 'if', or a "
            + "sentence one method starts and another finishes. A +-run and an unbroken Append or Write run on one "
            + "receiver ARE read as one sentence, written as separate statements or as a fluent chain, and a run "
            + "FINISHED with the Line form of its verb is one too, because that break lands after the last half; "
            + "a Line call does not CONTINUE a run, since then the break falls between the halves. Deciding which "
            + "conditional arms run together is dataflow analysis, not a merge rule, so this edge is declared "
            + "rather than guessed at",
        "third-party library messages surfaced to a caller (not ours to author, and not ours to fix)",
        "shipped JSON data files and the generated corpus (machine-shaped identifiers and paths; nothing in them is a sentence)",
        "whether a sentence built entirely of known words is TRUE (vocabulary, not truth — #308's boundary)",
        "a COMPANION clause that lives in a different sentence from its phrase — the companion test reads one "
            + "authored sentence, and a const reference breaks a run, so single-sourcing a claim without its "
            + "correction reds on caller text that did not change. Moving both together is the fix; an exemption "
            + "row is not, and MaxExemptions is deliberately too small to make it one",
        "the WATCHLIST itself — which wordings count as consent vocabulary is an authored judgement (the Phrases "
            + "table, each row with its ground). Nothing below it can derive it: the compiler's receipt says which "
            + "files ship, the two readers say what is in them, and neither has an opinion about which sentences "
            + "are over-claims. A claim invented in words nobody listed is outside every arm here",
        "that this guard RUNS AT ALL — CI invoking it is process, not construction, and no arm inside a guard can "
            + "assert its own scheduling. Above the compiler's receipt there is no further enumeration to certify; "
            + "what is left is this line, the line above, and truth",
        "prose inside a conditional-compilation region — reader A parses with no symbols defined and reader B has "
            + "no notion of directives, so a literal in a disabled arm is outside the net and one in an enabled arm "
            + "makes the two disagree. The count of regions in the scanned trees is printed on every run, and "
            + "INV6-DIRECTIVES names one whenever it could explain a disagreement the readers actually had",
    };

    // ================= entry =================

    public static int RunGuard(string[] args)
    {
        _pass = _fail = _class1 = _class2 = _harness = 0;
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
        Console.WriteLine($"    [c1] BY CONSTRUCTION: {_class1} arm(s) — the two-reader net and the arms over it, and the pins, which "
                        + "hold two independently written statements of one fact against each other. Unqualified claims.");
        Console.WriteLine($"    [c2] BEST-EFFORT: {_class2} arm(s) — every arm that reads MEANING out of prose or reflection BY PATTERN. A "
                        + "pattern's reach is not a by-construction fact, so each of these prints its own coverage above: how much of "
                        + "what is present it compared, and what it skipped, named with the reason.");
        Console.WriteLine($"         {_harness} further arm(s) drive those checkers with synthetic input; they claim nothing about the shipped surface.");
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
    /// and which talks to a modder in 47 <c>Console.Write*</c> calls, 37 of them opening on a literal. The setup
    /// tree was named in #386's second
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

    /// <summary>Shipped trees the run did not scan, AND trees it scanned that are not on the published list. SET
    /// EQUALITY, both directions, and the second direction is the point.
    /// <para>This tested SUBSET ONLY until 2026-08-25: <c>PublishedShippedTrees ⊆ scanned</c>. A tree ADDED to
    /// <see cref="ShippedAssemblies"/> and never enrolled here therefore passed green — its prose entered INV1's
    /// net silently and the pin that exists to make enrolment deliberate said nothing, because a pin that only
    /// notices subtraction is not holding a set, it is holding a floor. Both directions now, so enrolling a tree
    /// is one deliberate edit here that reds once, on purpose — the same shape as adding a verb to
    /// <see cref="PublishedVocabulary"/>.</para>
    /// <para>A function of its input so a RED arm can drive it with a set short a tree AND with one carrying an
    /// extra.</para></summary>
    static List<string> TreeSetMismatch(IReadOnlyCollection<string> scanned) =>
        PublishedShippedTrees
            .Where(t => !scanned.Contains(t, StringComparer.OrdinalIgnoreCase))
            .Select(t => $"'{t}' ships but is not among the trees this run scanned ({string.Join(", ", scanned)}) — INV1's net is "
                       + "short a tree, and INV5 cannot report it, because both come off the same assembly list")
            .Concat(scanned
                .Where(t => !PublishedShippedTrees.Contains(t, StringComparer.OrdinalIgnoreCase))
                .Select(t => $"'{t}' was scanned but is not on the published shipped-tree list ({string.Join(", ", PublishedShippedTrees)}) — a "
                           + "tree joined the net without being enrolled here. If it ships, add it to PublishedShippedTrees in the same "
                           + "commit that added it to ShippedAssemblies; if it does not, it should not be scanned"))
            .ToList();

    // ---- the packaging authority: what the build script actually publishes ----

    /// <summary>The script that assembles the shippable package — the ACTUAL authority on what ships, as opposed
    /// to this file's opinion about it. Read as text rather than run: the guard needs the answer at CI time on any
    /// machine, and running a packaging build to learn which trees it publishes would cost minutes to answer a
    /// question the script states in two lines.</summary>
    const string PackagingScript = "scripts/build-plugin.ps1";

    /// <summary>What <see cref="DeriveShippedTrees"/> found, WITH ITS OWN COVERAGE. The counts are the arm's
    /// denominator: a derivation that silently resolved fewer publish calls than the script contains is exactly
    /// the silent-shortfall shape this revision exists to end, so the numbers are printed and every unresolved
    /// call is named.</summary>
    readonly record struct ShipDerivation(List<string> Trees, int PublishCalls, int Resolved, List<string> Residue);

    /// <summary>Every <c>dotnet publish</c> call in the packaging script. The project argument is captured whether
    /// it is a variable (<c>$McpProj</c>, the form the script uses), a quoted path, or a bare one.
    /// <para>The variable alternative requires the <c>$</c>. It was optional until 2026-08-26, which made it
    /// match the leading IDENTIFIER of a bare relative path and stop there — <c>dotnet publish src/housecarl-mcp</c>
    /// captured <c>src</c>, and "or a path" was false for the commonest way to write one. An unquoted path now
    /// falls through to the last alternative and is captured whole.</para></summary>
    static readonly Regex PublishCall =
        new(@"dotnet\s+publish\s+(\$[A-Za-z_]\w*|'[^']+'|""[^""]+""|\S+)", RegexOptions.Compiled);

    /// <summary>How a publish-call argument names its project directory, when it is a PowerShell variable.</summary>
    const string JoinPathAssignment = @"\s*=\s*Join-Path\s+\$\w+\s+['""]([^'""]+)['""]";

    /// <summary>The trees whose SOURCE reaches a caller, derived from the packaging script: every project it
    /// publishes, plus the transitive closure of their <c>ProjectReference</c>s — a referenced project's code is
    /// compiled into or shipped beside the published output, so its prose ships too.
    /// <para><b>BEST-EFFORT — this reads a PowerShell script by pattern (Class 2).</b> It is not, and cannot be, a
    /// by-construction statement about what ships; MSBuild and PowerShell are the only things that know that for
    /// certain. What makes it worth having anyway is that it CANNOT hide a shortfall: the denominator is the
    /// number of <c>dotnet publish</c> calls the script text contains, the numerator is how many resolved to a
    /// tree under <c>src/</c>, and every call that did not resolve is named. A derivation that quietly stopped
    /// finding publish calls reports a smaller denominator, and the set equality against
    /// <see cref="PublishedShippedTrees"/> reds either way.</para>
    /// <para>A function of its inputs — the script text and a project-reference lookup — so the RED arms can drive
    /// it with a synthetic script and a synthetic graph, including the shapes it must refuse.</para></summary>
    static ShipDerivation DeriveShippedTrees(string scriptText, Func<string, List<string>?> projectReferences)
    {
        var residue = new List<string>();
        var roots = new List<string>();
        int calls = 0, resolved = 0;

        foreach (Match m in PublishCall.Matches(scriptText))
        {
            calls++;
            var arg = m.Groups[1].Value.Trim('\'', '"');
            string? tree = null;
            if (arg.StartsWith("$", StringComparison.Ordinal))
            {
                // Regex.Escape already escapes the leading '$'; escaping it again matched a literal backslash and
                // found no assignment at all — which the arm reported as a 0-of-2 denominator rather than as an
                // empty derived set that agreed with nothing. That is the Class-2 contract working: a derivation
                // that cannot read its input says so in a number instead of certifying the pin from thin air.
                var assign = Regex.Match(scriptText, "^\\s*" + Regex.Escape(arg) + JoinPathAssignment, RegexOptions.Multiline);
                if (assign.Success) tree = assign.Groups[1].Value;
                else residue.Add($"{PackagingScript}: 'dotnet publish {arg}' — no 'Join-Path' assignment of {arg} to a path was found, so this "
                               + "publish call resolved to no source tree. Whatever it publishes is outside the derived set");
            }
            else tree = arg;

            if (tree is null) continue;
            var slashed = tree.Replace('\\', '/').TrimEnd('/');
            var name = Path.GetFileName(slashed);
            if (name.Length == 0 || !slashed.Contains("src/", StringComparison.OrdinalIgnoreCase))
            {
                residue.Add($"{PackagingScript}: 'dotnet publish {arg}' resolves to '{tree}', which is not a tree under src/ — it is not in the "
                          + "derived set, and if it carries authored English it is outside INV1's net");
                continue;
            }
            resolved++;
            roots.Add(name);
        }

        if (calls == 0)
            residue.Add($"{PackagingScript}: no 'dotnet publish' call was found at all. Either the script stopped publishing, or it stopped "
                      + "spelling it this way and this derivation is reading nothing — which is why the CALL COUNT is printed, not the trees alone");

        // Transitive ProjectReference closure. A project whose file cannot be read is NAMED, never treated as a
        // leaf: a silently-empty reference list is how a tree drops out of the derived set with nothing red.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>(roots);
        while (queue.Count > 0)
        {
            var t = queue.Dequeue();
            if (!seen.Add(t)) continue;
            var refs = projectReferences(t);
            if (refs is null)
            {
                residue.Add($"the project file for '{t}' could not be read, so its ProjectReferences were not followed — any tree reachable "
                          + "ONLY through it is missing from the derived set");
                continue;
            }
            foreach (var r in refs) queue.Enqueue(r);
        }
        return new ShipDerivation(seen.OrderBy(s => s, StringComparer.Ordinal).ToList(), calls, resolved, residue);
    }

    /// <summary>The trees one project references, by name, or null when its project file cannot be read at all.
    /// Null rather than an empty list, because "references nothing" and "could not be asked" are different facts
    /// and the derivation reports the second as residue.</summary>
    static List<string>? ProjectReferencesOf(string tree)
    {
        var proj = Path.Combine("src", tree, tree + ".csproj");
        if (!File.Exists(proj)) return null;
        string xml;
        try { xml = File.ReadAllText(proj); } catch { return null; }
        return Regex.Matches(xml, "<ProjectReference\\s+Include\\s*=\\s*\"([^\"]+)\"")
            .Select(m => Path.GetFileNameWithoutExtension(m.Groups[1].Value.Replace('\\', '/')))
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Where the derived set and the independently written pin disagree, in both directions.</summary>
    static List<string> DerivedSetMismatch(IReadOnlyCollection<string> derived) =>
        PublishedShippedTrees.Where(t => !derived.Contains(t, StringComparer.OrdinalIgnoreCase))
            .Select(t => $"'{t}' is on the published shipped-tree list but {PackagingScript} does not publish it, directly or through a "
                       + "ProjectReference — either it stopped shipping (drop it here and from ShippedAssemblies) or the script stopped shipping it")
            .Concat(derived.Where(t => !PublishedShippedTrees.Contains(t, StringComparer.OrdinalIgnoreCase))
                .Select(t => $"'{t}' is published by {PackagingScript} (directly or through a ProjectReference) but is NOT on the published "
                           + "shipped-tree list — a tree started shipping and nothing enrolled it, so whatever prose it carries is outside "
                           + "INV1's net. Add it to ShippedAssemblies and to PublishedShippedTrees, or stop shipping it"))
            .ToList();

    static string Rel(string p) => Path.GetRelativePath(Directory.GetCurrentDirectory(), p).Replace('\\', '/');

    /// <summary>Whether a REPO-RELATIVE path lies under a build-output directory. ONE home, used by the file walk
    /// AND by the manifest classifier below, so the net and the compiler's receipt cannot drift about what "build
    /// output" means — a drift between two spellings of that rule would arrive as a membership mismatch about a
    /// file nobody edited, which is a false red pointing at the wrong thing.
    /// <para>Applied to the repo-relative path deliberately: a repository that itself lived under a directory
    /// called <c>obj</c> would otherwise classify every one of its files as build output.</para></summary>
    static bool IsBuildOutput(string relPath) =>
        relPath.Split('/').Any(seg => seg is "obj" or "bin");

    static IEnumerable<string> SourceFiles(string root) =>
        Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !IsBuildOutput(Rel(f)))
            .OrderBy(f => f, StringComparer.Ordinal);

    // ---- the compilation manifest: what the shipped artifact itself says went into it ----

    /// <summary>One shipped assembly's compilation manifest — the source documents its portable PDB names —
    /// split into the repo files the net must hold and the build-generated ones it must not, with the count of
    /// each so a manifest that suddenly named fewer documents could not do it quietly.</summary>
    readonly record struct CompilationManifest(string Assembly, List<string> Documents, int Generated, List<string> Problems);

    /// <summary>Every source document one shipped assembly's own compilation manifest names, read from the
    /// portable PDB beside it.
    ///
    /// <para><b>Why the receipt and not the directory.</b> Until 2026-08-26 the net's FILE membership was a
    /// directory walk: match a directory under <c>src/</c> to a shipped assembly's name, then take every
    /// <c>.cs</c> under it. Nothing asked the compilation which files it actually compiled, so a
    /// <c>&lt;Compile Include="..\somewhere-else\X.cs" /&gt;</c> put a shipped, compiled source file outside the
    /// net — measured on this branch at 59/59 green, with a false consent claim shipping and the file and literal
    /// counts unmoved. <c>GREEN-ROOTS</c> and <c>SHIP-DERIVED</c> could not catch it: both compare lists of
    /// DIRECTORIES, and <c>INV6-AGREE</c> certifies only that the two readers agree about the files they were
    /// HANDED. The two readers agreed perfectly about files nobody gave them — the same shape that killed designs
    /// one and two (a completeness claim certified by an oracle derived from the machinery it certifies),
    /// relocated one floor up. Ruled 2026-08-26 (§4, Aaron-go): the membership question ends at the system that
    /// owns the fact, and that system is the compiler. Hand-parsing the <c>.csproj</c> was rejected by name as
    /// the same disease one floor up; narrowing the claim to "every .cs under the shipped directories" was
    /// declined.</para>
    ///
    /// <para><b>What the receipt is.</b> The PDB's document table is the list of source documents the compiler
    /// emitted sequence points into — its own record of what went into the assembly. It is not derived from this
    /// guard, from either reader, or from any pattern over project files, which is what makes it an independent
    /// spelling of the net's membership: the pin rule is satisfied by construction rather than by a second list
    /// somebody has to remember to edit.</para>
    ///
    /// <para><b>The evaluation, run before this was written</b> (2026-08-26, Release build of the solution,
    /// measured — the ruling required the ground recorded either way):
    /// <list type="bullet">
    ///   <item>All three shipped assemblies emit PORTABLE PDBs (<c>BSJB</c>), and they sit beside the assemblies
    ///         in the generator's own output directory, because the generator project-references all three. The
    ///         CI job builds the solution and runs the generator from that directory, so the receipt is there at
    ///         the moment the guard needs it.</item>
    ///   <item><b>No new dependency.</b> <c>System.Reflection.Metadata</c> is in the shared framework — unlike
    ///         Roslyn, which is a pinned PackageReference. The evaluation harness read all four PDBs with an
    ///         empty <c>ItemGroup</c>.</item>
    ///   <item><b>Generated documents are exactly three per assembly and all under <c>obj/</c></b>:
    ///         <c>*.GlobalUsings.g.cs</c>, <c>*.AssemblyInfo.cs</c>, and the framework attributes file. No source
    ///         generator runs in the shipped trees; zero documents fell outside the repo root and zero named a
    ///         path that does not exist on disk. So the exclusion rule is one stated line — a document under
    ///         <c>obj/</c> or <c>bin/</c> is build output — and it is <see cref="IsBuildOutput"/>, the same rule
    ///         the file walk uses.</item>
    ///   <item><b>The receipt and the walk agree exactly today</b>: 73 + 49 + 1 = 123 repo documents against 123
    ///         scanned files. Both directions of the set equality are therefore assertable on measured ground,
    ///         not assumed.</item>
    /// </list></para>
    ///
    /// <para><b>The two ways this can go wrong, and both are LOUD.</b> A document reaches the table only where
    /// the compiler emitted sequence points, so a <c>.cs</c> file with no method body at all could in principle
    /// be compiled and absent from the receipt; that arrives as "scanned but no manifest names it" — a red
    /// naming the file, which an author resolves, never a silent narrowing.</para>
    ///
    /// <para><b>The path-shape condition, measured rather than assumed.</b> Documents carry the paths the
    /// compiler was given. SourceLink IS active here — the SDK includes it — but it only ANONYMISES source paths
    /// when <c>ContinuousIntegrationBuild</c> is set, which nothing in this repo or its CI workflow does, so the
    /// receipt names real local files and resolves under the guard's working directory. Building with
    /// <c>-p:ContinuousIntegrationBuild=true</c> was tried: every document comes back as <c>/_/src/…</c> and
    /// <c>MANIFEST-SET</c> goes RED, naming the condition, the cause and the remedy, with the count per manifest.
    /// <para>That red is deliberate and is not repaired by teaching this to strip the deterministic root. A build
    /// that has anonymised its source paths has removed the fact this arm needs; reconstructing it from a prefix
    /// convention would make the net's membership rest on a pattern again — one floor up from where the ruling
    /// rejected exactly that — and a reconstruction that was subtly wrong would match a file set silently rather
    /// than fail. So it stays loud, and whoever hardens the build resolves it deliberately.</para></para></summary>
    static CompilationManifest ReadManifest(Assembly asm)
    {
        var name = asm.GetName().Name!;
        var documents = new List<string>();
        var problems = new List<string>();
        int generated = 0;

        var location = asm.Location;
        if (string.IsNullOrEmpty(location))
        {
            problems.Add($"'{name}' has no file location, so its compilation manifest cannot be found — the net's file membership "
                       + "is uncertified for this assembly. A single-file or in-memory host would do this; the guard runs from a "
                       + "normal build output and must keep doing so");
            return new CompilationManifest(name, documents, generated, problems);
        }

        var pdb = Path.ChangeExtension(location, ".pdb");
        if (!File.Exists(pdb))
        {
            problems.Add($"'{name}' ships but its compilation manifest is not beside it ({Rel(pdb)} does not exist) — nothing certifies "
                       + "which files went into this assembly. Build with debug symbols (the SDK default emits a portable PDB for "
                       + "Release too); do not narrow the claim to the directory walk instead");
            return new CompilationManifest(name, documents, generated, problems);
        }

        try
        {
            using var stream = File.OpenRead(pdb);
            using var provider = MetadataReaderProvider.FromPortablePdbStream(stream);
            var md = provider.GetMetadataReader();
            // Unresolvable documents are ONE problem line per manifest with a count and an example, not one per
            // document: a mapped build makes every document unresolvable at once, and 123 identical lines bury
            // the single fact an author needs. Never a bare count — the count, the cause and a specimen.
            var unresolvable = new List<string>();
            foreach (var handle in md.Documents)
            {
                var raw = md.GetString(md.GetDocument(handle).Name);
                if (string.IsNullOrEmpty(raw))
                {
                    problems.Add($"{Rel(pdb)}: a document in the manifest has no name — it cannot be held against the net either way");
                    continue;
                }
                var rel = Rel(raw);
                if (rel.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(rel))
                {
                    unresolvable.Add(raw.Replace('\\', '/'));
                    continue;
                }
                if (IsBuildOutput(rel)) { generated++; continue; }
                documents.Add(rel);
            }
            if (unresolvable.Count > 0)
                problems.Add($"{Rel(pdb)}: {unresolvable.Count} of its document(s) are not under this run's working directory "
                           + $"('{Directory.GetCurrentDirectory().Replace('\\', '/')}') and cannot be matched against a scanned file — for example "
                           + $"'{unresolvable[0]}'. Either the guard is not running from the repo root, or the build ANONYMISED its source paths "
                           + "(ContinuousIntegrationBuild, DeterministicSourcePaths or PathMap — a '/_/' root is SourceLink's). The receipt then no "
                           + "longer names files this machine has, and the net's membership is uncertified: build the guard's inputs without path "
                           + "mapping, or bring the change here deliberately. This does not reconstruct the paths — see ReadManifest for why");
        }
        catch (Exception ex)
        {
            problems.Add($"{Rel(pdb)}: could not be read as a portable PDB — {ex.GetType().Name}: {ex.Message}. A Windows-format (native) PDB "
                       + "would do this; the manifest is the authority on the net's membership and a manifest that cannot be read "
                       + "certifies nothing");
        }
        return new CompilationManifest(name, documents.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), generated, problems);
    }

    /// <summary>Where the compiler's receipt and the net disagree about which files exist. SET EQUALITY, both
    /// directions, and each direction fails for a different reason so each says its own thing.
    /// <para>A function of its two inputs, so <c>RED-MANIFEST</c> can drive exactly this code with a synthetic
    /// receipt and a synthetic net — including the matching case, which must report nothing.</para></summary>
    static List<string> ManifestMismatch(IReadOnlyCollection<string> documents, IReadOnlyCollection<string> scanned) =>
        documents
            .Where(d => !scanned.Contains(d, StringComparer.OrdinalIgnoreCase))
            .Select(d => $"'{d}' was COMPILED INTO a shipped assembly — its own manifest names it — but no scanned root reaches it, so every "
                       + "literal in it is outside INV1's net. A file linked in by <Compile Include> from outside src/<assembly-name>/ "
                       + "does this. Move it under the tree it ships from, or stop compiling it into a shipped assembly")
            .Concat(scanned
                .Where(s => !documents.Contains(s, StringComparer.OrdinalIgnoreCase))
                .Select(s => $"'{s}' was scanned, but no shipped assembly's manifest names it — the guard is reading a file that did not go "
                           + "into anything that ships. Either it is excluded from compilation (dead source under a scanned tree), or it "
                           + "compiled with no method body for the compiler to record. Neither is a reason to widen the comparison: name "
                           + "the file's status here deliberately"))
            .ToList();

    /// <summary>Read every shipped source file with BOTH readers, hold them against each other, and hand back the
    /// sentences INV1 scans. Reader A feeds the net; reader B exists to falsify A's completeness.</summary>
    static List<Sentence> SourceArm()
    {
        Console.WriteLine("── SOURCE: two independent readers over the shipped literals, held against each other ──");
        var (roots, rootProblems) = ResolveRoots();
        var sentences = new List<Sentence>();
        var parseProblems = new List<string>();
        var agreeProblems = new List<string>();
        var directivesByFile = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var readersDisagreeIn = new HashSet<string>(StringComparer.Ordinal);
        // Every file the net ENUMERATED, recorded before it is read: membership is about which files the net
        // reaches, and a file that then fails to read is INV6-PARSE's red, not a hole in the file set.
        var scannedFiles = new List<string>();
        var lineProblems = new List<string>();
        int lineClosedAppend = 0, lineClosedWrite = 0;
        long chars = 0;
        int files = 0, literals = 0, inHoles = 0;

        foreach (var root in roots)
        {
            int rootFiles = 0, rootSentences = 0, rootHoles = 0;
            foreach (var file in SourceFiles(root))
            {
                rootFiles++;
                var label = Rel(file);
                scannedFiles.Add(label);
                string text;
                try { text = File.ReadAllText(file); }
                catch (Exception ex) { parseProblems.Add($"{label}: could not be read — {ex.GetType().Name}: {ex.Message}"); continue; }

                var directives = ConditionalDirectives(label, text);
                if (directives.Count > 0) directivesByFile[label] = directives;

                // Each reader is attempted separately and a throw NAMES ITS FILE. The second design wrapped the
                // whole guard in one catch, so a single malformed escape reported "the guard threw" and took all
                // 38 arms with it, naming nothing.
                List<SourceLiteral> a;
                Dictionary<int, AppendCall> calls;
                try
                {
                    a = RoslynLiteralReader.Read(text, out var errs, out calls);
                    foreach (var e in errs) parseProblems.Add($"{label}: {e}");
                }
                catch (Exception ex) { parseProblems.Add($"{label}: READER A threw — {ex.GetType().Name}: {ex.Message}"); continue; }

                List<SourceLiteral>? b = null;
                try { b = HandLiteralLexer.Read(text); }
                catch (Exception ex) { agreeProblems.Add($"{label}: READER B threw — {ex.GetType().Name}: {ex.Message}"); }
                if (b is not null)
                {
                    var disagreements = Disagreements(label, a, b);
                    if (disagreements.Count > 0) readersDisagreeIn.Add(label);
                    agreeProblems.AddRange(disagreements);
                }

                var merged = MergeSentences(text, a, calls);
                foreach (var s in merged)
                {
                    if (string.IsNullOrWhiteSpace(s.Text)) continue;
                    sentences.Add(new Sentence($"{label}:{s.Line}", s.Text));
                    rootSentences++;
                    chars += s.Text.Length;
                }
                // The Line rule, held against what the merge actually PRODUCED rather than against the predicate
                // that decides it. A sentence carries the START of its head, so a literal that opens a sentence
                // was not merged into what came before it; a Line-closed gap whose second literal is missing from
                // that set was joined across a line break the modder can see.
                var opens = merged.Select(m => m.Start).ToHashSet();
                foreach (var (lit, adds) in LineClosedGaps(a, calls))
                {
                    if (adds == "Append") lineClosedAppend++; else lineClosedWrite++;
                    if (!opens.Contains(lit.Start))
                        lineProblems.Add($"{label}:{lit.Line} — the call before this one already broke the line, and the two "
                                       + "were merged anyway: the phrase this reports would be split across a line break on "
                                       + "the caller's screen");
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

        // A run FINISHED by a Line call is one sentence (the break lands after it); a run that a Line call already
        // closed is two. The second half is the one with live instances in these trees, so it gets an arm over the
        // merge's own output. The count is printed rather than pinned: a new WriteLine pair is ordinary authoring,
        // and what must hold is that none of them merged — but if the count ever reaches zero this arm is asserting
        // nothing about nothing, and says so instead of passing quietly.
        int lineClosed = lineClosedAppend + lineClosedWrite;
        if (lineClosed == 0)
            lineProblems.Add("no run in the shipped trees is closed by a Line call any more — this arm has nothing left "
                           + "to hold, and a merge that started crossing line breaks would now pass it silently");
        Check($"GREEN-LINEBREAK  every run a Line call already CLOSED stayed two sentences ({lineClosedWrite} WriteLine "
            + $"and {lineClosedAppend} AppendLine gap(s) in the shipped trees, none merged)",
            lineProblems.Count == 0, lineProblems, tier: Tier.Construction);

        var scanned = roots.Select(r => Path.GetFileName(r)!).ToList();
        rootProblems.AddRange(TreeSetMismatch(scanned));
        Check($"GREEN-ROOTS   the trees SCANNED are exactly the set written down independently here — neither short one nor carrying one ({scanned.Count} of {PublishedShippedTrees.Length}: {string.Join(", ", scanned)})",
            rootProblems.Count == 0, rootProblems, tier: Tier.Construction);

        // The packaging authority, held against the same pin. Class 2 and labelled so: it reads a PowerShell
        // script and a set of .csproj files by pattern, which is not construction — so it prints its coverage,
        // and every publish call it could not resolve is named rather than absorbed.
        var ship = File.Exists(PackagingScript)
            ? DeriveShippedTrees(File.ReadAllText(PackagingScript), ProjectReferencesOf)
            : new ShipDerivation(new List<string>(), 0, 0,
                new List<string> { $"{PackagingScript} is not readable from '{Directory.GetCurrentDirectory()}' — nothing derived. The guard must "
                                 + "run from the repo root; a derivation with no input cannot certify the pin, and does not pretend to" });
        Console.WriteLine($"        packaging authority ({PackagingScript}): {ship.Resolved} of {ship.PublishCalls} 'dotnet publish' call(s) resolved to a tree "
                        + $"under src/; ProjectReference closure -> {ship.Trees.Count} tree(s): {(ship.Trees.Count == 0 ? "(none)" : string.Join(", ", ship.Trees))}");
        foreach (var r in ship.Residue) Console.WriteLine($"          · not derived: {r}");
        Check($"SHIP-DERIVED  the trees {PackagingScript} publishes are exactly the "
            + $"published list ({ship.Resolved}/{ship.PublishCalls} publish call(s) resolved, {ship.Residue.Count} not derived and named above)",
            DerivedSetMismatch(ship.Trees).Count == 0 && ship.Residue.Count == 0,
            DerivedSetMismatch(ship.Trees).Concat(ship.Residue).ToList(), tier: Tier.BestEffort);
        // The MEMBERSHIP arm. GREEN-ROOTS and SHIP-DERIVED settle which TREES are in; this settles which FILES,
        // against the only thing that knows for certain — the compiled artifact's own receipt. See ReadManifest
        // for the ruling, the evaluation and its measurements.
        var manifests = ShippedAssemblies.Select(ReadManifest).ToList();
        var manifestDocs = manifests.SelectMany(m => m.Documents).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var manifestProblems = manifests.SelectMany(m => m.Problems).ToList();
        Console.WriteLine("        compilation manifest (each shipped assembly's own portable-PDB document table):");
        foreach (var m in manifests)
            Console.WriteLine($"          · {m.Assembly}: {m.Documents.Count + m.Generated} document(s) -> {m.Documents.Count} repo source, "
                            + $"{m.Generated} build-generated (under obj/ or bin/ — the rule the file walk uses)"
                            + (m.Problems.Count == 0 ? "" : $", {m.Problems.Count} unreadable"));
        manifestProblems.AddRange(ManifestMismatch(manifestDocs, scannedFiles));
        Check($"MANIFEST-SET  the files the net scanned are exactly the repo files the shipped assemblies were COMPILED from, per their own "
            + $"manifests ({manifestDocs.Count} repo document(s) across {manifests.Count} manifest(s), {manifests.Sum(m => m.Generated)} build-generated excluded; {scannedFiles.Count} file(s) scanned)",
            manifestProblems.Count == 0, manifestProblems, tier: Tier.Construction);
        Check($"INV6-PARSE    every scanned file parses as C# ({files} file(s)) — a file the parser rejects is a file whose literals are not trustworthy",
            parseProblems.Count == 0, parseProblems, tier: Tier.Construction);
        Check($"INV6-AGREE    the two independently written readers agree about every literal in every file ({literals} literal(s), {inHoles} of them inside interpolation holes)",
            agreeProblems.Count == 0, agreeProblems, tier: Tier.Construction);
        // SCOPED to files the two readers actually disagree about. It reported every conditional region in every
        // scanned file until 2026-08-25, which reds on a #if that carries no literal at all — nothing for either
        // reader to read differently — and on a "#if" sitting inside a multi-line verbatim or raw literal, which
        // is not a directive. Both are green facts wearing a red remedy about prose. A directive can only BE the
        // cause of a disagreement in a file where a disagreement exists, so that is the file set it speaks about;
        // the rest are a census line, printed, because the boundary stays visible either way.
        var directiveProblems = directivesByFile
            .Where(kv => readersDisagreeIn.Contains(kv.Key))
            .SelectMany(kv => kv.Value)
            .ToList();
        Console.WriteLine($"        conditional-compilation regions: {directivesByFile.Values.Sum(v => v.Count)} in "
                        + $"{directivesByFile.Count} scanned file(s)"
                        + (directivesByFile.Count == 0
                            ? " — the construct the two readers are entitled to read differently is absent"
                            : $": {string.Join(", ", directivesByFile.Keys)}"));
        Check($"INV6-DIRECTIVES no file the two readers DISAGREE about carries conditional compilation — the one construct in "
            + "ordinary C# that would explain a disagreement rather than a reader having stopped "
            + $"({readersDisagreeIn.Count} file(s) in disagreement, {directivesByFile.Count} carrying a directive)",
            directiveProblems.Count == 0, directiveProblems, tier: Tier.Construction);
        Console.WriteLine();
        return sentences;
    }

    /// <summary>Every conditional-compilation directive in one file, named with its line.
    /// <para>This is the one construct in ordinary C# the two readers are ENTITLED to disagree about, and the
    /// disagreement would arrive as an INV6-AGREE red whose detail could not say why: reader A parses with no
    /// preprocessor symbols, so a <c>#if</c> arm is disabled-text trivia it never sees, while reader B has no
    /// notion of directives and reads both arms. So it is named here instead, at the cause, with the repair in the
    /// arm's own text — because the intuitive repair (teach reader B to skip disabled text) is the one that puts
    /// shipped prose outside INV1's net in silence when the symbol IS defined.</para>
    /// <para><c>#region</c>, <c>#nullable</c>, <c>#pragma</c> and the rest are not conditional and do not change
    /// what either reader sees, so they are not named.</para></summary>
    static List<string> ConditionalDirectives(string label, string src) =>
        Regex.Matches(src, @"^[ \t]*#[ \t]*(if|elif|else|endif)\b", RegexOptions.Multiline)
            .Select(m => $"{label}:{src.Take(m.Index).Count(c => c == '\n') + 1}: a conditional-compilation directive — or, "
                       + "inside a multi-line verbatim or raw literal, text shaped like one, which this reads by "
                       + "line and cannot tell apart. "
                       + "Reader A parses with no symbols defined and never sees the disabled arm; reader B reads every "
                       + "arm. Thread the build's symbols into BOTH readers, or do not ship prose from a #if region — "
                       + "teaching reader B to skip disabled text would hide the defined-symbol case from INV1 in silence.")
            .ToList();

    /// <summary>Where the two readers disagree about one file, as an author-readable difference.
    /// <para>The comparison is a MULTISET of (depth, text) rather than an ordered walk: the readers arrive at the
    /// same literals by different routes (a syntax tree in document order, a scanner in source order), and
    /// requiring them to agree about ORDER would be asserting a shared implementation detail instead of the fact
    /// that matters — that neither of them stopped reading.</para>
    /// <para><b>A disagreement is not automatically a reader that stopped.</b> It can also be a reader that
    /// DECODED differently (both raw-interpolated brace defects were of that kind), or a construct the two are
    /// entitled to read differently — conditional compilation being the one that exists in ordinary C#: reader A
    /// parses with no preprocessor symbols defined, so a <c>#if</c> arm is disabled-text trivia it never sees,
    /// while reader B has no notion of directives and reads every arm. So the detail says what it can observe —
    /// the counts differ — and not which reader is wrong, and the disclosure below carries the case.</para>
    /// <para>The repair for that one is NOT to teach reader B to skip disabled text. When the symbol IS defined
    /// the literal genuinely ships, and a reader B that skipped it would put shipped prose outside INV1's net with
    /// nothing red — the silent narrowing this whole design exists to make impossible. It is either symbols
    /// threaded into both readers, or the region does not ship.</para></summary>
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
                       + $"— the two are not reading the same thing here. Text: \"{Clip(text, 90)}\"");
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

    /// <summary>The text between two literals with its comments blanked out, so a run is not broken by one.
    /// <para><c>"a" + // why\n "b"</c> is one authored sentence and read as two until 2026-08-26, because the
    /// join test asks for nothing but whitespace and a <c>+</c>. A phrase spanning the join was then in no
    /// fragment, silently — the same shape as the Append gap, wearing a comment.</para>
    /// <para>Safe on this input specifically: the span lies BETWEEN two adjacent literals, so a <c>//</c> or
    /// <c>/*</c> in it cannot be inside a string. Applied to the gap only, never to the window in front of a
    /// literal, where an unterminated opener could be cut in half.</para></summary>
    static string StripComments(string gap)
    {
        if (gap.IndexOf('/') < 0) return gap;
        var sb = new StringBuilder(gap.Length);
        for (int i = 0; i < gap.Length; i++)
        {
            if (gap[i] == '/' && i + 1 < gap.Length && gap[i + 1] == '/')
            {
                while (i < gap.Length && gap[i] != '\n') i++;
                if (i < gap.Length) sb.Append('\n');
                continue;
            }
            if (gap[i] == '/' && i + 1 < gap.Length && gap[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < gap.Length && !(gap[i] == '*' && gap[i + 1] == '/')) i++;
                i++;                                    // onto the '/', which the loop's own i++ then steps past
                sb.Append(' ');
                continue;
            }
            sb.Append(gap[i]);
        }
        return sb.ToString();
    }

    /// <summary>Whether two literals are consecutive text-adding arguments on ONE receiver — the second half of
    /// what a sentence is here. Every question this asks is answered by READER A's syntax tree
    /// (<see cref="AppendCall"/>), never by the characters between or in front of the two literals.
    /// <para><b>Why not the text.</b> Until 2026-08-26 this read a receiver NAME out of a regex window in front
    /// of the literal, and two shipped shapes could not be spelled that way: a chain whose earlier link takes a
    /// VALUE (<c>sb.Append(count).Append("a"); sb.Append("b");</c>) presents a <c>)</c> where the pattern wanted
    /// a name, and an INDEXER receiver (<c>cells[i].Sb</c>) is not an identifier at all. Both refused a run this
    /// guard PRINTS that it reads, so a phrase split across one shipped with INV1 green — the same class of hole
    /// the fluent-form gap was, one layer down. Reading the receiver off the tree closes the class rather than
    /// the two instances: any spelling of a receiver is one node, so there is nothing left to enumerate.</para>
    /// <para><b>The receiver is compared, not just the method.</b> Two builders appended to in alternation are
    /// two sentences, and merging them would manufacture text no caller ever reads — the failure mode a merge
    /// rule has to avoid, since a phrase invented by the merge is a false RED on correct prose. The comparison is
    /// of the chain's HEAD receiver, so a run that switches from the fluent form to the statement form reads the
    /// same receiver on both sides.</para>
    /// <para><b>Adjacency is a tree relation too, and it means NOTHING between the two literals.</b> Either the
    /// second call is chained directly onto the first (<see cref="AppendCall.Inner"/> is the first's
    /// <see cref="AppendCall.Node"/>, which admits no room for anything else), or the two stand in consecutive
    /// statements of one body — and then the first must be the LAST thing its chain does
    /// (<c>Node == Outer</c>) and the second the FIRST thing its own chain does (<c>Inner &lt; 0</c>). Without
    /// that pair of conditions the statement form joins across values:
    /// <c>sb.Append("a").Append(name); sb.Append(pad).Append("b");</c> puts <c>name</c> and <c>pad</c> between
    /// the two halves, and a caller reads no "ab" anywhere. Anything else — an intervening statement, a second
    /// argument, a different method, a different receiver, a call in another block — breaks the run by
    /// construction.</para>
    /// <para><b>A single-statement <c>if</c> body does NOT continue into the next statement</b>, and that is the
    /// declared control-flow boundary rather than an omission: <see cref="AppendCall.Following"/> is -1 there,
    /// because the statement after an <c>if</c> is not a continuation of the arm. The regex this replaced merged
    /// those by accident whenever the <c>if</c> sat before the run's first literal, where a gap-only lookback
    /// could not see it — so it was reaching past a boundary <see cref="NotInReach"/> prints that it does not
    /// cross. Six shipped literals stopped being merged that way when the receiver moved onto the tree, each of
    /// them the statement after a one-line <c>if</c> body.</para></summary>
    static bool AppendRun(IReadOnlyDictionary<int, AppendCall> calls, SourceLiteral prev, SourceLiteral next)
        // A call that breaks the line can FINISH a run but never continue one: the break lands after its own
        // text, so Write("a"); WriteLine("b"); reads "ab" on one line, and WriteLine("a"); Write("b"); does not.
        => Contiguous(calls, prev, next, out var first) && !first.EndsLine;

    /// <summary>Everything a run needs EXCEPT the line rule: the same verb, on the same receiver, with nothing
    /// between. Split out from <see cref="AppendRun"/> so that <see cref="LineClosedGaps"/> can ask for the pairs
    /// that differ from a run in exactly one respect, without a second copy of the test to drift from this
    /// one. <paramref name="first"/> is the call the earlier literal belongs to.</summary>
    static bool Contiguous(IReadOnlyDictionary<int, AppendCall> calls, SourceLiteral prev, SourceLiteral next,
                           out AppendCall first)
    {
        // Keyed by the literal's END: a merged run carries its TAIL's end forward, so this looks up the call the
        // run last made rather than the one it started with.
        first = default;
        if (!calls.TryGetValue(prev.End, out var a) || !calls.TryGetValue(next.End, out var b)) return false;
        first = a;
        // The same VERB both sides. Append and Write each add text with nothing between, but a run that changes
        // from one to the other is two different calls, and joining them would manufacture text nobody reads.
        if (!string.Equals(a.Adds, b.Adds, StringComparison.Ordinal)) return false;
        if (!string.Equals(a.Receiver, b.Receiver, StringComparison.Ordinal)) return false;
        return b.Inner == a.Node
            || (a.Statement >= 0 && b.Statement >= 0 && a.Following == b.Statement
                && a.Node == a.Outer && b.Inner < 0);
    }

    /// <summary>The adjacent literal pairs that are a run in every respect except that the FIRST call already
    /// broke the line. These must stay two sentences, and there are enough of them in the shipped trees for that
    /// to be worth checking against the merge's actual output rather than against the predicate that decides it —
    /// which is what <c>GREEN-LINEBREAK</c> does.</summary>
    static List<(SourceLiteral Next, string Adds)> LineClosedGaps(
        List<SourceLiteral> lits, IReadOnlyDictionary<int, AppendCall> calls)
    {
        var outp = new List<(SourceLiteral, string)>();
        var top = lits.Where(l => l.Depth == 0).OrderBy(l => l.Start).ToList();
        for (int i = 1; i < top.Count; i++)
            if (Contiguous(calls, top[i - 1], top[i], out var a) && a.EndsLine)
                outp.Add((top[i], a.Adds));
        return outp;
    }

    /// <summary>Adjacent TOP-LEVEL literals are ONE sentence when the author wrote them as one. Two shapes count:
    /// <list type="bullet">
    ///   <item>a run joined by nothing but <c>+</c> — how every long description and every shared refusal here is
    ///         written;</item>
    ///   <item>a run of consecutive <c>Append</c> or <c>Write</c> calls on ONE receiver, each taking a literal
    ///         and nothing else — how the inline consent prompts are written. Written as separate statements or
    ///         as a fluent chain; both are one run, and the chained call is by far the commoner shape in these
    ///         trees. Which calls those are, and what receiver each is on, comes from READER A's syntax tree
    ///         (<see cref="AppendRun"/>), so a receiver spelled with an indexer or reached past an earlier
    ///         value argument is read like any other.</item>
    /// </list>
    /// A run breaks at anything else — a const reference, a method call, an argument separator, an intervening
    /// statement, a different receiver — which keeps the merge conservative: it never joins two things an author
    /// wrote apart, because a phrase the merge invented would be a false RED on correct prose.
    /// <para><b>The Append half was added 2026-08-25</b>, amending settled decision 10 on measured ground. That
    /// decision's reason was "how everything here is written", and it had measurably missed a shipped surface:
    /// <c>compact_plugin</c>'s in-place consent prompt is a run of <c>c.Append(…)</c> calls
    /// (<c>LoadOrderService.cs:6316-6325</c>), so <c>c.Append("This is shown "); c.Append("once.")</c> put the
    /// phrase in front of a modder with INV1 green and no fragment carrying it.</para>
    /// <para><b>The FLUENT form was added 2026-08-26</b>, and it was the majority shape: this file printed, and
    /// settled decision 10 said, that an unbroken run of <c>Append</c> calls on one receiver is one sentence,
    /// while the code read only the statement form — 2,479 chained <c>).Append(</c> gaps in the shipped trees
    /// against 257 statement-form ones. <c>Write</c> entered the set with it: <c>housecarl-setup</c> talks to a
    /// modder in 47 <c>Console.Write*</c> calls, and <c>Console.Write("…"); Console.Write("…");</c> concatenates
    /// on that modder's screen exactly as an Append run concatenates in a builder, so a run that CHANGES verb
    /// is two sentences and a run that keeps it is one. <b>A <c>Line</c> variant is the same verb</b>, admitted
    /// 2026-08-26 in the run's LAST position only: it adds its text and breaks AFTER, so
    /// <c>Write("a"); WriteLine("b");</c> is one line the modder reads as "ab" while
    /// <c>WriteLine("a"); Write("b");</c> is two. Ending a run with the <c>Line</c> form is the commoner way to
    /// write one, and reading it as two sentences left that whole shape outside INV1 with nothing on the printed
    /// boundary list saying so. It moved no shipped sentence: the trees hold ZERO plain-then-Line gaps today, and
    /// the 38 Line gaps they do hold (35 <c>WriteLine</c>, 3 <c>AppendLine</c>) are all Line-then-Line, which must
    /// stay two sentences and do — <c>GREEN-LINEBREAK</c> holds that against the merge's own output. The review
    /// that directed this counted 53 and 5 by the textual gap SHAPE alone; this count is of pairs that satisfy
    /// every other condition of a run, which is the set the rule can actually decide.
    /// <b>The receiver then moved onto the syntax tree the same
    /// day</b>: 12 further literals joined the run in front of them — every one a statement-form continuation
    /// the lookback could not read, because the call before it ended on a VALUE rather than on a name — and 6
    /// stopped being merged, each of them the statement after a one-line <c>if</c> body that the old pattern
    /// crossed by accident. Census 10,056 sentences to 10,050.</para>
    /// <para><b>The boundary that remains, and it is DECLARED rather than narrowed away</b> (see
    /// <see cref="NotInReach"/>, printed every run): text assembled ACROSS control flow, or through a helper. The
    /// live prompt interleaves its appends with <c>if</c> blocks, and joining across those would mean deciding
    /// which arms run together — dataflow analysis, not a merge rule. This is a statement about what the guard
    /// reaches, printed on every run, not a claim quietly softened.</para>
    /// <para>Merging happens ABOVE both readers, over reader A's literals only. <c>INV6-AGREE</c> compares what
    /// the two readers found, before any of this, so nothing here can make the readers agree by construction.</para>
    /// <para>A literal INSIDE an interpolation hole is its own sentence and never merges, because what surrounds
    /// it is an expression rather than prose. Its neighbours in the source are the ternary's other arm and the
    /// text around the hole, none of which the author wrote as one sentence with it.</para></summary>
    static List<SourceLiteral> MergeSentences(string src, List<SourceLiteral> lits,
                                              IReadOnlyDictionary<int, AppendCall> calls)
    {
        var outp = new List<SourceLiteral>();
        var top = lits.Where(l => l.Depth == 0).OrderBy(l => l.Start).ToList();
        foreach (var lit in top)
        {
            if (outp.Count > 0 && outp[^1].End <= lit.Start
                && (Join.IsMatch(StripComments(src[outp[^1].End..lit.Start])) || AppendRun(calls, outp[^1], lit)))
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
            Check($"INV1 \"{rule.Phrase}\" — {shape}  [{carriers} carrier(s), {exempted} exempt]", violations.Count == 0, violations, tier: Tier.Construction);
        }

        // The table is empty today, so an INV2 arm would assert nothing about nothing and pass on every possible
        // input — the shape CLAUDE.md's case law says to delete rather than strengthen. It materialises with the
        // first declared row; until then the claim lives in RED-DEADEXEMPT / GREEN-DEADEXEMPT, where the same
        // detector runs against a synthetic table and can actually fail.
        if (Exemptions.Length > 0)
        {
            var dead = DeadExemptions(Exemptions, used);
            Check($"INV2 every declared exemption still matches a real site ({Exemptions.Length} declared)", dead.Count == 0, dead, tier: Tier.Construction);
        }
        else
        {
            Console.WriteLine("        exemptions declared: 0 — INV2 has no arm to run (it materialises with the first row)");
        }

        // INV2-DEGEN always runs, empty table or not: it is a claim about the TABLE, not about its rows.
        var degen = Degenerate(Exemptions);
        Check($"INV2-DEGEN the exemption table cannot absorb an arbitrary miss ({Exemptions.Length} of at most {MaxExemptions} declared, each scoped to a named file with a ground)",
            degen.Count == 0, degen, tier: Tier.Construction);
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
                    : $" (nothing in THIS SENTENCE states {string.Join(" or ", rule.Companions.Select(c => $"\"{c}\""))})"
                      // The remedy, spelled out, because the wrong one is cheap and reachable: an exemption row.
                      // The companion is required in the same AUTHORED sentence, and a const reference breaks a
                      // run — so single-sourcing a shared claim into a const and leaving its correction behind reds
                      // on caller text that did not change. Move BOTH; do not declare the site exempt.
                    + " — keep the phrase and the clause that redeems it in the SAME literal run: if this claim is "
                    + "being single-sourced into a const, move the correction into that const with it")
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
    static IEnumerable<SurfaceSite> SurfaceSites() => SurfaceSitesOf(Surface.GetTypes());

    /// <summary>The same enumeration over any type set, so <c>RED-NESTEDTYPE</c> can drive BOTH branches of the
    /// nested-type skip with a synthetic type that has one — the live surface has no nested type carrying a
    /// type-level <c>[Description]</c> today, so nothing on it can exercise either branch.</summary>
    static IEnumerable<SurfaceSite> SurfaceSitesOf(IEnumerable<Type> types)
    {
        foreach (var t in types.OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            if (Text(t.GetCustomAttribute<DescriptionAttribute>(inherit: false)) is { } td)
                yield return new SurfaceSite($"[Description] on type {t.Name}", td, t, null);

            foreach (var m in t.GetMembers(AllMembers).OrderBy(m => m.Name, StringComparer.Ordinal))
            {
                // A NESTED TYPE is a member of its declaring type AND a type in GetTypes(), so counting it here
                // as well would yield the same [Description] twice — inflating three printed censuses (the
                // surface count, the default-parenthetical denominator, and the recital count) with duplicates
                // that make every ratio read better than it is. LATENT, not live, and measured as such: the
                // surface has nested types (RecordsTools' three) but none carries a type-level [Description], so
                // no census moved when this landed. RED-NESTEDTYPE drives both branches over a synthetic type,
                // which is the only way to reach them. The outer loop owns types; this loop owns the rest.
                if (m is Type) continue;
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
    /// message stating a false cause ("the SOURCE scan is not reading the file"). They are named in the census
    /// instead — by member, not as a bare count, so a green run says WHICH strings this arm did not compare; the
    /// completeness claim they were failing to serve is INV6-AGREE's, which covers every literal in every file
    /// regardless of what it is assigned to.</summary>
    static (List<(string Label, string Value)> Consts, List<string> RuntimeBuilt) CompiledConsts()
    {
        var consts = new List<(string, string)>();
        var runtimeBuilt = new List<string>();
        foreach (var asm in ShippedAssemblies)
            foreach (var t in asm.GetTypes().OrderBy(t => t.FullName, StringComparer.Ordinal))
                foreach (var f in t.GetFields(AllMembers).OrderBy(f => f.Name, StringComparer.Ordinal))
                {
                    if (f.FieldType != typeof(string) || !f.IsStatic) continue;
                    if (f.IsInitOnly && !f.IsLiteral) { runtimeBuilt.Add($"{t.Name}.{f.Name}"); continue; }
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
            uncoveredDesc.Count == 0, uncoveredDesc, tier: Tier.Construction);

        var (consts, runtimeBuilt) = CompiledConsts();
        var uncoveredConst = consts.Where(c => !Covered(c.Value, texts))
            .Select(c => $"{c.Label}: no scanned source literal accounts for its value — the SOURCE scan is not reading the file that declares it")
            .ToList();
        Console.WriteLine($"        static readonly strings not compared: {runtimeBuilt.Count} — their values can be built at runtime, so a "
                        + "source-text comparison would report a false cause. INV6-AGREE covers their literals."
                        + (runtimeBuilt.Count == 0 ? "" : $" They are: {string.Join("; ", runtimeBuilt)}."));
        Check($"INV5-CONSTS       every compile-time string const in the shipped assemblies is covered ({consts.Count} const(s))",
            uncoveredConst.Count == 0, uncoveredConst, tier: Tier.Construction);
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
        var unreadDefaults = new List<string>();
        var markerDisagree = new List<string>();
        int recitals = 0, dropped = 0, defaultParens = 0, unrecitedMentions = 0;
        var proseDefaults = new List<string>();

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
            // The independent denominator. A description that NAMES two or more verbs and yielded no recital is
            // a verb list joined by something Run does not read — a comma list, most likely. Both of INV3's
            // counts come off the same run pattern, so such a site was in neither of them; it is a printed line
            // now rather than a number that quietly did not move.
            var mentioned = VerbsNamed(s.Text);
            if (mentioned.Count >= 2 && !Recitals(s.Text, vocab).Any())
            {
                unrecitedMentions++;
                Console.WriteLine($"        (names {mentioned.Count} verb(s) but yields NO recital — a list joined by something the run "
                                + $"pattern does not read)  [{string.Join(", ", mentioned)}]  {s.Label}");
            }
            // Defaults declared in PROSE. Counted and named, never compared — see ProseDefault.
            foreach (Match pm in ProseDefault.Matches(s.Text))
                proseDefaults.Add($"{s.Label}: …{Clip(s.Text[Math.Max(0, pm.Index - 46)..Math.Min(s.Text.Length, pm.Index + 44)], 90)}…");
            // Marked defaults are read from the WHOLE description, not from inside the recital loop. The second
            // design collected them only for runs the admission test had already accepted, so a "(default)" on a
            // run that was dropped — or on a slot that recites nothing — entered neither the published-default
            // check nor the slot comparison, and the arm's "a general rule over whatever marks a default" was
            // false for exactly the sites nothing else was watching.
            var at = MarkedDefaultsAt(s.Text);
            foreach (var m in at) marks.Add((s, m.Token));

            // The COVERAGE census. The denominator is every '(default' parenthetical the description contains —
            // a fact about the text, counted by a pattern written independently of the one that reads markers —
            // and the numerator is what was read. Everything in between is named, so a marker the parser cannot
            // see is a visible line rather than a count that did not move.
            var parsed = new HashSet<int>(at.Select(m => m.At));
            defaultParens += DefaultParentheticals(s.Text).Count;
            unreadDefaults.AddRange(UnreadDefaults(s.Label, s.Text, parsed));
            foreach (var i in MarkerShaped(s.Text))
                if (!parsed.Contains(i))
                    markerDisagree.Add($"{s.Label}: …{Clip(s.Text[Math.Max(0, i - 46)..Math.Min(s.Text.Length, i + 44)], 90)}… — this parenthetical "
                                     + "has a token in front of it and reads as a marker to the character walk, but the marker pattern did not read "
                                     + "it. The two spellings of \"marker-shaped\" disagree, so one of them has stopped seeing a house style");
            foreach (var m in at)
                if (!MarkerShaped(s.Text).Contains(m.At))
                    markerDisagree.Add($"{s.Label}: the marker pattern read '{m.Token}' (default) at offset {m.At}, and the character walk does not "
                                     + "see a marker there — the pattern is reading something the second spelling calls a value-inside form");
        }

        Check($"INV3-TOKENS   every verb recited on the surface is a real verb ({recitals} recital(s) read, {dropped} run(s) not read as "
            + $"recitals, {unrecitedMentions} description(s) naming two or more verbs in no run shape at all)",
            unknown.Count == 0, unknown, tier: Tier.BestEffort);

        var unrecited = PublishedVocabulary.Where(v => !named.Contains(v)).ToList();
        Check($"INV3-UNION    the recitals it READ name {named.Count} of {PublishedVocabulary.Length} published verb(s) — a verb no "
            + $"description mentions is a verb no caller can find. Read from {recitals} recital(s); {dropped} separator-joined run(s) "
            + $"and {unrecitedMentions} verb-naming description(s) in no run shape were not read as recitals and are printed above, so a "
            + "verb named only in one of those is not counted here",
            unrecited.Count == 0,
            unrecited.Select(v => $"'{v}' is a write verb that no [Description] recital names").ToList(), tier: Tier.BestEffort);

        Check("INV4-HOMES    WriteVerbs.All and WriteVerbs.AllRecital agree with each other AND with the vocabulary written independently here",
            HomesAgree(WriteVerbs.All, WriteVerbs.AllRecital, PublishedVocabulary),
            new() { $"All=[{string.Join(",", WriteVerbs.All)}] AllRecital=[{string.Join(",", RecitalNames(WriteVerbs.AllRecital))}] "
                  + $"independent=[{string.Join(",", PublishedVocabulary)}]" }, tier: Tier.Construction);

        Check($"INV4-MARK     WriteVerbs.AllRecital marks exactly one verb (default), and it is '{PublishedDefault}'",
            MarkedDefaults(WriteVerbs.AllRecital) is [var only] && only == PublishedDefault,
            new() { $"AllRecital marks [{string.Join(",", MarkedDefaults(WriteVerbs.AllRecital))}] — the const feeds three shipped "
                  + "descriptions, so one edit here mis-states the default in all three at once" }, tier: Tier.Construction);

        // Printed BEFORE the arms that read markers, so the coverage a verdict rests on is on screen above it.
        Console.WriteLine($"        default parentheticals on the surface: {defaultParens} — {marks.Count} read as a \"token (default)\" marker, "
                        + $"{unreadDefaults.Count} not read as one, each named below with the reason it was rejected on");
        foreach (var u in unreadDefaults) Console.WriteLine($"          · not read as a marker: {u}");
        Console.WriteLine($"        defaults declared in PROSE rather than as a marker: {proseDefaults.Count} — counted and named here, "
                        + "compared against nothing. INV4-DEFAULT's denominator is the '(default' parenthetical, so these were in no census "
                        + "at all until 2026-08-26: changing \"verb defaults to Set\" to \"Merge\" moved no number and stayed green");
        foreach (var d in proseDefaults) Console.WriteLine($"          · prose default: {d}");
        Check($"INV4-MARKCOVER the marker pattern and an independently written character walk agree about which of "
            + $"the {defaultParens} default parenthetical(s) are marker-shaped",
            markerDisagree.Count == 0, markerDisagree, tier: Tier.Construction);

        DefaultSlotsArm(marks);
        TailGlossArm(surface);
        Console.WriteLine();
    }

    /// <summary>One marker read off a description: the token said to be the default, and the offset of the
    /// parenthesis that marks it — which is what lets the coverage census pair a parsed marker with the
    /// occurrence it came from, rather than comparing two counts and hoping.</summary>
    readonly record struct DefaultMark(string Token, int At);

    /// <summary>The characters that can END a default-marking parenthetical's first word. In the marker form the
    /// token sits OUTSIDE the parens (<c>'text' (default)</c>, <c>Set (default) | Add</c>,
    /// <c>'endorsements' (default, best-regarded first)</c>), so "default" is the whole of it or is followed by a
    /// separator. In the other form the value sits INSIDE (<c>(default 500)</c>, <c>(default 'Patch')</c>,
    /// <c>(default: the plugin's own folder)</c>) and there is no token in front to hold anything against.</summary>
    static readonly char[] MarkerTerminators = { ')', ',', ';', '–', '—' };

    /// <summary>The verbs a text marks <c>(default)</c>, WITH the offset of each marker.
    /// <para>The token may be QUOTED. It could not be until 2026-08-25 — the pattern wanted letters immediately
    /// before the whitespace and a parenthesis, so a closing quote broke it, and <c>'endorsements' (default,
    /// best-regarded first)</c> never matched. That is the ordinary house spelling on this surface: every
    /// <c>'text' (default)</c> transport marker was outside the census too, and a reviewer changing
    /// <c>NexusTools.cs:38</c>'s <c>sort = "endorsements"</c> to <c>"downloads"</c> measured 54 passed, 0 failed
    /// with the marker never even collected — so the census count did not move either, and nothing indicated a
    /// marker had been skipped.</para>
    /// <para>The parenthetical may also CONTINUE past the word (<c>(default, best-regarded first)</c>); it is the
    /// separator after "default" that says the token is outside, not the closing paren.</para>
    /// <para><b>The LEFT boundary is load-bearing, and was missing until 2026-08-26.</b> Without it the token
    /// group could start in the MIDDLE of a word, because all it requires is a letter: <c>3d (default)</c>
    /// matched from the <c>d</c>, <c>0x800 (default)</c> from the <c>x</c>, <c>_id (default)</c> from the
    /// <c>i</c>. Each put a token in the census that no author wrote — and because <see cref="MarkerShaped"/>
    /// walks the whole word backwards and refuses one that does not START with a letter, each was also a FALSE
    /// RED on <c>INV4-MARKCOVER</c>, a class-1 arm. The lookbehind sits before the optional quote, so the quoted
    /// form still matches from inside the quotes.</para></summary>
    static readonly Regex DefaultMarker = new(
        @"(?<![A-Za-z0-9_])['""‘’]?([A-Za-z][A-Za-z0-9_]*)['""‘’]?\s*\(\s*default\b\s*(?=[),;–—])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    static List<DefaultMark> MarkedDefaultsAt(string text) =>
        DefaultMarker.Matches(text)
            .Select(m => new DefaultMark(m.Groups[1].Value, m.Index + m.Value.IndexOf('(')))
            .ToList();

    /// <summary>The verbs a text marks <c>(default)</c>, in order. Deliberately a LIST rather than a single value,
    /// so "marks two" and "marks none" are both visible failures rather than one of them silently reading as the
    /// other.</summary>
    static List<string> MarkedDefaults(string text) => MarkedDefaultsAt(text).Select(m => m.Token).ToList();

    /// <summary>EVERY default-declaring parenthetical in a text, marker-shaped or not — the DENOMINATOR the
    /// marked-default census is read against.
    /// <para>Written deliberately WIDER and simpler than <see cref="DefaultMarker"/>, and independently of it: it
    /// asks only whether a parenthesis opens on the word "default", which is a fact about the text rather than an
    /// opinion about the house style. A denominator derived from the matcher it measures would move whenever the
    /// matcher's reach moved and could never show a shortfall — the pin rule, applied to a count.</para></summary>
    static readonly Regex DefaultParenthetical = new(@"\(\s*default\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    static List<int> DefaultParentheticals(string text) =>
        DefaultParenthetical.Matches(text).Select(m => m.Index).ToList();

    /// <summary>Which default-declaring parentheticals are MARKER-SHAPED — a second, independent spelling of the
    /// same question <see cref="DefaultMarker"/> answers, written by walking characters rather than as a pattern.
    /// <para>This is the falsifiable half of the coverage census. A census that only printed "read 9, present 30"
    /// could never fail, which is the unfalsifiable-arm shape; holding two independently written readings of
    /// "marker-shaped" against each other CAN fail, and does the moment one of them stops seeing a spelling the
    /// other sees. Same reasoning as <c>INV6-AGREE</c>, one layer up.</para>
    /// <para>What it deliberately does NOT flag: a parenthetical that carries its value inside itself. Those are
    /// real declared defaults this arm does not read, and they are named as residue rather than turned into a red
    /// on correct text.</para></summary>
    static List<int> MarkerShaped(string text)
    {
        var outp = new List<int>();
        for (int i = 0; i < text.Length; i++)
            if (text[i] == '(' && MarkerRejectReason(text, i) is null) outp.Add(i);
        return outp;
    }

    /// <summary>Why the parenthesis at <paramref name="at"/> is NOT a marker — or <c>null</c> when it is one.
    /// <para>ONE home for the backward walk, so <see cref="MarkerShaped"/> and <see cref="UnreadDefaults"/> can
    /// never disagree about a parenthetical, and so the residue line an author reads states the reason this code
    /// actually rejected on. It used to say "the value is INSIDE the parenthesis, so there is no token in front
    /// of it" about every unread parenthetical — false for <c>"*" (default)</c> on <c>RecordsWalk.follow</c>,
    /// where a token IS in front and is rejected for not starting with a letter. One residue line in thirty-eight
    /// pointing an author at a repair that would not have helped.</para></summary>
    static string? MarkerRejectReason(string text, int at)
    {
        const string word = "default";
        if (at >= text.Length || text[at] != '(') return "is not a parenthesis";
        int j = at + 1;
        while (j < text.Length && char.IsWhiteSpace(text[j])) j++;
        if (j + word.Length > text.Length || !text.Substring(j, word.Length).Equals(word, StringComparison.OrdinalIgnoreCase))
            return "does not open on the word \"default\"";
        int after = j + word.Length;
        // "defaulting", "defaults" — a longer word, not this one.
        if (after < text.Length && (char.IsLetterOrDigit(text[after]) || text[after] == '_'))
            return "opens on a LONGER word that starts with \"default\"";
        while (after < text.Length && char.IsWhiteSpace(text[after])) after++;
        if (after >= text.Length || Array.IndexOf(MarkerTerminators, text[after]) < 0)
            return "declares a default with the value INSIDE the parenthesis, so there is no token in front of it to hold "
                 + "against the slot. This arm reads the \"token (default)\" form only";

        int k = at - 1;
        while (k >= 0 && char.IsWhiteSpace(text[k])) k--;
        // NOTHING in front and a non-identifier token in front are different facts, and conflating them is what
        // made the residue line wrong: after stepping over a closing quote the identifier walk consumes nothing
        // for "*" exactly as it does for an empty run, so the two have to be separated BEFORE the walk.
        if (k < 0)
            return "has nothing in front of it — the parenthetical stands alone, so there is no token to hold against the slot";
        if (text[k] == '\'' || text[k] == '"' || text[k] == '‘' || text[k] == '’') k--;
        int end = k;
        while (k >= 0 && (char.IsLetterOrDigit(text[k]) || text[k] == '_')) k--;
        if (k == end || !char.IsLetter(text[k + 1]))
            return "has a token in front of it that does not start with a LETTER, so it names no slot value this arm can "
                 + "compare. Quote-wrapped punctuation (\"*\"), a number-led token (3d) and an underscore-led one all land here";
        return null;
    }

    /// <summary>The default-declaring parentheticals a run did NOT read as markers, each named with its site and
    /// the text around it. Residue, printed — never a bare count.</summary>
    static List<string> UnreadDefaults(string label, string text, IReadOnlyCollection<int> parsed) =>
        DefaultParentheticals(text)
            .Where(i => !parsed.Contains(i))
            .Select(i => $"{label}: …{Clip(text[Math.Max(0, i - 46)..Math.Min(text.Length, i + 44)], 90)}… — this "
                       + (MarkerRejectReason(text, i)
                          ?? "IS marker-shaped, but the pattern did not read it. The two spellings of \"marker-shaped\" "
                           + "disagree here, which is what INV4-MARKCOVER exists to report"))
            .ToList();

    /// <summary>The default marked on the SURFACE is the published verb wherever the marked token is a write verb,
    /// and wherever the slot it annotates declares a default value in code, the marked token and that value agree.
    /// A general rule over whatever marks a default — no site list — so a new write slot enrols itself.
    /// <para><b>The slot comparison runs for every marker, verb or not.</b> Whether a slot declares a default is a
    /// fact about the SLOT, and it does not stop being checkable because the token in front of the marker is
    /// unrecognised — an unrecognised token is precisely what a rename leaves behind. Gating this comparison on
    /// the vocabulary was measured GREEN over a description reading <c>Sett (default)</c> on a parameter declaring
    /// <c>"Set"</c>: the token was skipped for being stale, and the recital carrying it was dropped by
    /// <see cref="Recitals"/> for having no live verb left, so the two escape hatches covered each other.</para>
    /// <para>A marker whose token is NOT a write verb is a different kind of default (an output format, a mode).
    /// This arm has no opinion on what such a slot SHOULD default to, so it makes no published-default claim about
    /// it — but it still holds it against the slot, and names it in the census either way.</para>
    /// <para><b>CLASS 2 — best-effort, and it prints its denominator.</b> Which text marks a default is read out
    /// of prose by pattern, so this arm's reach is not a by-construction fact and does not claim to be. What it
    /// does instead is report its own coverage on every run: markers compared against a slot, markers SKIPPED, and
    /// the REASON for each skip. The reason is the load-bearing half — the census used to print "6 whose slot
    /// declares none", a bare count that read as a boundary when one of the six was <c>bool esl = true</c> and the
    /// arm simply could not spell it.</para>
    /// <para><b>What is still asserted about by nothing:</b> a marker whose slot genuinely declares no default (a
    /// nullable property coalesced downstream), and one whose default has no unambiguous prose spelling. Both are
    /// named individually with their reason, never counted and dropped (Q3).</para></summary>
    static void DefaultSlotsArm(List<(SurfaceSite Site, string Verb)> marks)
    {
        var vocab = new HashSet<string>(PublishedVocabulary, StringComparer.Ordinal);
        var problems = new List<string>();
        var nonVerb = new List<string>();
        var skipped = new List<string>();
        int compared = 0, verbMarks = 0;

        foreach (var (site, verb) in marks)
        {
            if (vocab.Contains(verb))
            {
                verbMarks++;
                if (verb != PublishedDefault)
                    problems.Add($"{site.Label}: marks '{verb}' (default), but the published default is '{PublishedDefault}'");
            }
            else nonVerb.Add($"{verb} @ {site.Label}");

            // Not gated on the vocabulary: see the summary. A stale token on a slot that declares a default is
            // the case this arm was measured green over.
            var declared = DeclaredDefault(site);
            if (declared.Rendered is null)
            {
                // Named WITH ITS REASON, which is the denominator's whole point: "6 whose slot declares none" hid
                // a bool that declares one, because a bare count cannot be read against anything.
                skipped.Add($"{verb} @ {site.Label} — {declared.SkippedBecause}");
                continue;
            }
            compared++;
            if (MarkDisagrees(verb, declared.Rendered))
                problems.Add($"{site.Label}: the description marks '{verb}' (default) but the slot actually defaults to '{declared.Rendered}'");
        }

        if (verbMarks == 0)
            problems.Add("no [Description] on the surface marks a write verb (default) at all — the marker that three shipped "
                       + "descriptions carry has gone, and nothing tells a caller which verb they get by omitting op=/verb=");

        Console.WriteLine($"        marked defaults: {marks.Count} in all — {verbMarks} on a write verb, {nonVerb.Count} on something that is not a verb"
                        + (nonVerb.Count == 0 ? "" : $": {string.Join("; ", nonVerb)}"));
        Console.WriteLine($"        slots compared: {compared} of {marks.Count} held against the slot's own declared default; {skipped.Count} skipped");
        foreach (var sk in skipped) Console.WriteLine($"          · not compared: {sk}");
        Check($"INV4-DEFAULT  {compared} of {marks.Count} marker(s) compared, {skipped.Count} skipped and named above: "
            + $"every marked (default) it could compare agrees with the slot's own declared default, and every marked VERB is '{PublishedDefault}'",
            problems.Count == 0, problems, tier: Tier.BestEffort);
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
    /// <para><b>A self-naming gloss changes WHICH verb is pinned, and nothing more.</b> When the parenthetical
    /// names exactly one verb, that verb becomes the subject instead of <see cref="TailGlossVerb"/> — but the arm
    /// still requires the recital to END with it. So following this docstring's own former advice (reword the
    /// gloss to name its subject) and then appending a ninth verb still reds the arm, on a description that reads
    /// correctly. That paragraph claimed the pin "steps aside"; it does not, and saying so was the same class of
    /// stale sentence this guard exists to catch. The conservatism is deliberate — the gloss is still glued to the
    /// tail, and nothing here can tell whether a reader binds it to the name or to the adjacency — but it is a
    /// limitation, recorded, not a courtesy the arm extends.</para></summary>
    static void TailGlossArm(IEnumerable<SurfaceSite> surface)
    {
        var problems = new List<string>();
        var tail = RecitalNames(WriteVerbs.AllRecital).LastOrDefault();
        // The DENOMINATOR: every description the recital reaches. The arm speaks only about those carrying a
        // parenthetical glued onto it, so the rest are counted and named rather than absent from the reckoning.
        var carrying = surface.Where(s => s.Text.Contains(WriteVerbs.AllRecital, StringComparison.Ordinal)).ToList();
        var glued = carrying
            .Select(s => (Site: s, Gloss: GluedGloss(s.Text, WriteVerbs.AllRecital)))
            .Where(x => x.Gloss is not null)
            .ToList();

        if (glued.Count == 0)
            problems.Add($"no [Description] appends a parenthetical directly onto WriteVerbs.AllRecital any more — this arm is "
                       + "asserting about nothing. Either the gloss moved (delete this arm and TailGlossVerb with it) or the "
                       + "recital stopped reaching that description (which is INV3's problem, and it did not see it).");

        foreach (var (site, gloss) in glued)
        {
            // Whole words, not substrings: "Adds a deep copy…" is ordinary English about CopyFrom, and a
            // substring match read the "Add" in it as the gloss naming a different verb — reddening this arm on a
            // rewording that moved nothing. It also made every compound verb ("SetAtIndex" contains "Set") match
            // twice, so the step-aside path below could never fire for one.
            var namesInGloss = VerbsNamedIn(gloss!);
            var subject = namesInGloss.Count == 1 ? namesInGloss[0] : TailGlossVerb;
            string how = namesInGloss.Count == 1 ? "the verb it names" : $"'{TailGlossVerb}', the verb it is written about";
            if (!string.Equals(tail, subject, StringComparison.Ordinal))
                problems.Add($"{site.Label}: the parenthetical glued to the recital glosses {how}, but the recital now ends with "
                           + $"'{tail}'. The gloss has moved onto '{tail}' and off '{subject}', and the tool schema is shipping "
                           + $"that claim. Gloss: \"{Clip(gloss!, 90)}\"");
        }

        Console.WriteLine($"        descriptions carrying WriteVerbs.AllRecital: {carrying.Count} — {glued.Count} with a parenthetical glued "
                        + $"directly onto it, {carrying.Count - glued.Count} without one, which this arm asserts nothing about"
                        + (carrying.Count == glued.Count ? "" : $": {string.Join("; ", carrying.Where(c => GluedGloss(c.Text, WriteVerbs.AllRecital) is null).Select(c => c.Label))}"));
        Check($"INV4-TAILGLOSS the verb AllRecital ends with is the one the glued gloss describes ('{TailGlossVerb}'), at "
            + $"{glued.Count} of the {carrying.Count} description(s) that carry the recital",
            problems.Count == 0, problems, tier: Tier.BestEffort);
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

    /// <summary>The write verbs a glued gloss NAMES, as whole words. A function of its input so a RED arm can
    /// drive it, and whole-word because a substring match read the "Add" in "Adds a deep copy…" as the gloss
    /// naming a different verb — reddening the tail pin on a rewording that moved nothing — and made every
    /// compound verb match twice ("SetAtIndex" contains "Set"), so the step-aside path could never fire for
    /// one.</summary>
    static List<string> VerbsNamedIn(string gloss) =>
        PublishedVocabulary.Where(v => Regex.IsMatch(gloss, $@"\b{Regex.Escape(v)}\b")).Distinct().ToList();

    /// <summary>Whether a marked default and the slot's own declared default disagree. A function of its inputs
    /// rather than of the live surface, so a RED arm can drive it — including the case the vocabulary gate used to
    /// skip, a marked token that is not a known verb at all.</summary>
    static bool MarkDisagrees(string marked, string? declared) =>
        declared is not null && !string.Equals(declared, marked, StringComparison.Ordinal);

    /// <summary>What a slot declares as its default, RENDERED the way prose spells it — or a stated REASON why it
    /// could not be read. Never a bare null: "declares nothing" and "declares something this arm cannot spell" are
    /// different facts, and collapsing them is how the arm went green over a slot that did declare one.
    /// <para>It read <c>p.DefaultValue as string</c> until 2026-08-25, so every non-string default came back null
    /// and the census printed "whose slot declares none" about a slot that declares one.
    /// <c>WriteTools.CompactPlugin(esl=)</c> is <c>bool esl = true</c> behind a description reading "When true
    /// (default)"; flipping it to <c>false</c> left the guard 54 passed, 0 failed. The cast was the whole defect
    /// for every default the renderer can spell — strings, bools, numbers, chars and named enum members, which is
    /// what the surface declares. It is not "all of them": <see cref="Render"/> still refuses a null, an enum
    /// value naming no member, and anything that is not one of those types, and each refusal comes back as a
    /// stated reason rather than as a null that reads like "declares none".</para></summary>
    readonly record struct SlotDefault(string? Rendered, string? SkippedBecause);

    /// <summary>A constant default as a description would write it: <c>true</c>, <c>10</c>, an enum member's name,
    /// a string as itself. The C# spelling, not <c>ToString()</c>'s, for the two where they differ — a bool prints
    /// <c>True</c> and prose says <c>true</c>, and holding a marked "true" against "True" would red on correct
    /// text. Anything with no unambiguous prose spelling is refused BY NAME rather than rendered into something
    /// that might accidentally match.</summary>
    static SlotDefault Render(object? value, Type declared)
    {
        var t = Nullable.GetUnderlyingType(declared) ?? declared;
        if (value is null)
            return new SlotDefault(null, t == typeof(string) || !t.IsValueType || Nullable.GetUnderlyingType(declared) is not null
                ? "declares null as its default, which marks no value a description could recite"
                : "declares no default value at all");
        if (value is string s) return new SlotDefault(s, null);
        if (value is bool b) return new SlotDefault(b ? "true" : "false", null);
        if (t.IsEnum)
        {
            var name = Enum.GetName(t, value);
            return name is null
                ? new SlotDefault(null, $"declares the enum value {value} of {t.Name}, which names no member — nothing to hold a marker against")
                : new SlotDefault(name, null);
        }
        if (value is char c) return new SlotDefault(c.ToString(), null);
        if (value is IFormattable f && t.IsPrimitive)
            return new SlotDefault(f.ToString(null, System.Globalization.CultureInfo.InvariantCulture), null);
        return new SlotDefault(null, $"declares a default of type {t.Name}, which this arm has no prose spelling for");
    }

    /// <summary>The default the annotated slot actually uses. An optional parameter carries it directly; a
    /// property carries it as an initializer, which means instantiating the declaring type to read it. Every
    /// refusal names its reason, so a green run says exactly what it did not compare and why.</summary>
    static SlotDefault DeclaredDefault(SurfaceSite site)
    {
        if (site.Param is { } p)
            return p.HasDefaultValue
                ? Render(p.DefaultValue, p.ParameterType)
                : new SlotDefault(null, "is a required parameter — it declares no default at all");
        if (site.Member is PropertyInfo { CanRead: true } prop)
        {
            object? instance;
            try { instance = Activator.CreateInstance(prop.DeclaringType!, nonPublic: true); }
            catch (Exception ex) { return new SlotDefault(null, $"is a property whose declaring type {prop.DeclaringType!.Name} could not be instantiated to read its initializer ({ex.GetType().Name})"); }
            if (instance is null) return new SlotDefault(null, $"is a property whose declaring type {prop.DeclaringType!.Name} instantiated to null");
            try { return Render(prop.GetValue(instance), prop.PropertyType); }
            catch (Exception ex) { return new SlotDefault(null, $"is a property whose getter threw ({ex.GetType().Name}), so its initializer could not be read"); }
        }
        return new SlotDefault(null, site.Member is null
            ? "is not a slot that can carry a default at all"
            : $"is a {site.Member.MemberType} rather than a parameter or a readable property, so it declares no default");
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

    /// <summary>The published verbs a text names AS WORDS, with no opinion about run shape — the independent
    /// denominator the recital census is read against.
    /// <para>Every count INV3 prints comes off <see cref="AllRuns"/>, so a recital that stops matching
    /// <see cref="Run"/> leaves the numerator AND the denominator together and the census does not move.
    /// Measured: comma-joining a seven-verb recital took "16 recitals read" to 15, green, with the recital named
    /// nowhere — it matched neither <see cref="Recitals"/> nor <see cref="DroppedRuns"/>, because both start from
    /// the same run pattern. Counting verb NAMES instead of runs cannot be walked past that way: the words are
    /// still there whatever punctuation joins them.</para>
    /// <para>This does NOT read a comma list as a recital — settled decision 16 stands, and <c>RED-NOTVERB</c>
    /// pins it. It makes such a site a printed line with its label instead of a silence.</para></summary>
    static List<string> VerbsNamed(string text) =>
        PublishedVocabulary.Where(v => Regex.IsMatch(text, $@"\b{Regex.Escape(v)}\b")).ToList();

    /// <summary>A default declared in PROSE rather than as a <c>(default)</c> marker — "verb defaults to Set",
    /// "the default is 500", "by default".
    /// <para><c>INV4-DEFAULT</c>'s denominator is its own <c>(default</c> pattern, so a default stated any other
    /// way is counted nowhere and named nowhere. Measured: changing <c>housecarl_set_field</c>'s "verb defaults
    /// to Set" to "Merge" left every census number identical and the run green. These are not compared against a
    /// slot — mapping a prose sentence to the member it describes is prose-parsing, not a fact anything here
    /// owns — but they are COUNTED and NAMED, so the reach of the marked-default arm reads honestly against how
    /// many defaults the surface actually declares.</para></summary>
    static readonly Regex ProseDefault =
        new(@"\bdefault(?:s|ing)?\s+(?:to|is|of)\b|\bby default\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    static IEnumerable<List<string>> AllRuns(string text)
    {
        foreach (Match m in Run.Matches(Parenthetical.Replace(text, " ")))
        {
            var tokens = m.Value.Split('|', '/').Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
            if (tokens.Count >= 2) yield return tokens;
        }
    }

    /// <summary>The verb names a const recital states. Read more loosely than a recital on a DESCRIPTION is: this
    /// splits on <c>|</c> alone and takes whatever is between the separators, while <see cref="Run"/> also admits
    /// <c>/</c> and requires each token to be Capitalised. The looseness is deliberate here — a const that stopped
    /// being a well-formed recital should reach INV4-HOMES as a disagreement about the NAMES, not vanish from the
    /// comparison for failing a shape test.</summary>
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

        // The slot comparison, driven in both directions over the case the vocabulary gate used to skip: a marked
        // token that is not a known verb at all. A slot that declares no default is asserted about by nothing, and
        // The nested-type skip, BOTH branches, over a synthetic type set — the live surface cannot drive either,
        // because no nested type on it carries a type-level [Description] yet. A nested type is a member of its
        // declaring type AND a type in GetTypes(), so without the skip its description enters every census twice
        // and each ratio reads better than it is. The nested type's MEMBERS must still be reached exactly once.
        var nestedSet = new[] { typeof(NestedFixture), typeof(NestedFixture.Inner) };
        var nestedSites = SurfaceSitesOf(nestedSet).ToList();
        Check("RED-NESTEDTYPE   a nested type's own [Description] is enumerated ONCE, and its members are still reached",
            nestedSites.Count(s => s.Text == "the nested type's own description") == 1
                && nestedSites.Count(s => s.Text == "the nested type's member description") == 1
                && nestedSites.Count(s => s.Text == "the outer type's own description") == 1
                // The PRECONDITION the skip rests on: Assembly.GetTypes() lists nested types in its own right, so
                // the outer loop reaches them and dropping them from the member loop loses nothing. If that ever
                // stopped being true the skip would silently REMOVE a shipped description from the surface, which
                // is the failure this arm has to be able to see.
                && Surface.GetTypes().Any(t => t.IsNested),
            new() { "a nested type's description is counted twice, or lost, or the assembly's type list has stopped "
                  + "including nested types. Twice inflates the surface count, the default-parenthetical denominator "
                  + "and the recital count together, so every printed ratio reads better than it is; lost or dropped "
                  + "puts a shipped description outside the reach arm entirely" }, redArm: true);

        // The marker's LEFT boundary and the rejection reasons, together: the two spellings of "marker-shaped"
        // have to agree about a token that does not START where a token starts, and the residue line has to say
        // which of the several reasons applied. Both halves shipped wrong — the pattern read '3d (default)' as a
        // marker on 'd' and the walk did not, a false RED on a class-1 arm; and every residue line claimed the
        // value was inside the parenthesis, which was false for the one site where a token IS in front.
        Check("RED-MARKBOUND    a marker cannot start mid-token, the two spellings agree on every such shape, and each rejection states ITS OWN reason",
            MarkedDefaults("3d (default)").Count == 0
                && MarkedDefaults("0x800 (default)").Count == 0
                && MarkedDefaults("_id (default)").Count == 0
                && MarkerShaped("3d (default)").Count == 0
                && MarkedDefaults("Set (default)") is ["Set"] && MarkerShaped("Set (default)").Count == 1
                && MarkedDefaults("'text' (default)") is ["text"] && MarkerShaped("'text' (default)").Count == 1
                && MarkerRejectReason("\"*\" (default)", 4) is { } starReason && starReason.Contains("does not start with a LETTER", StringComparison.Ordinal)
                && MarkerRejectReason("(default 'Patch')", 0) is { } valueReason && valueReason.Contains("VALUE INSIDE", StringComparison.OrdinalIgnoreCase)
                && MarkerRejectReason(" (default)", 1) is { } aloneReason && aloneReason.Contains("stands alone", StringComparison.Ordinal)
                && MarkerRejectReason("Set (default)", 4) is null,
            new() { "the marker pattern reads a token from the middle of a word — a token no author wrote, and a false RED on "
                  + "INV4-MARKCOVER, which is class 1 — or the two spellings disagree about one of those shapes, or a residue "
                  + "line gives the same reason for every rejection and sends an author at the wrong repair" }, redArm: true);

        // that half is driven too — otherwise "cannot compare" and "compared and agreed" would look alike.
        Check("RED-MARKSLOT     a marked default that disagrees with the slot's declared default is reported, verb or not",
            MarkDisagrees("Sett", "Set") && MarkDisagrees("Add", "Set") && MarkDisagrees("summary", "table")
                && !MarkDisagrees("Set", "Set") && !MarkDisagrees("Sett", null) && !MarkDisagrees("summary", null),
            new() { "the slot comparison skips a stale token because it is not a verb, reports one that agrees, or "
                  + "asserts about a slot that declares no default at all" }, redArm: true);

        // The RENDERER, over every constant type a slot can declare. The `as string` cast it replaces yielded null
        // for all but the first of these, so each non-string default read as "declares none" — a slot the arm then
        // asserted nothing about while printing a count that looked like a boundary.
        Check("RED-RENDER       a slot's declared default is rendered for every constant type, and an unspellable one is REFUSED by name",
            Render("Set", typeof(string)).Rendered == "Set"
                && Render(true, typeof(bool)).Rendered == "true"
                && Render(false, typeof(bool)).Rendered == "false"
                && Render(10, typeof(int)).Rendered == "10"
                && Render(2.5, typeof(double)).Rendered == "2.5"
                && Render('x', typeof(char)).Rendered == "x"
                && Render(StringComparison.Ordinal, typeof(StringComparison)).Rendered == "Ordinal"
                && Render(null, typeof(string)) is { Rendered: null, SkippedBecause: not null }
                && Render(null, typeof(int?)) is { Rendered: null, SkippedBecause: not null }
                && Render(new object(), typeof(object)) is { Rendered: null, SkippedBecause: not null },
            new() { "the renderer drops a constant default it should spell — a bool, an int, an enum member — or "
                  + "silently returns nothing for one it cannot spell instead of naming the reason. Either way a "
                  + "marked default on that slot goes into the skipped count with no way to read the count against "
                  + "anything, which is how 'When true (default)' over 'bool esl = false' stayed green" }, redArm: true);

        Check("RED-DIRECTIVES   a conditional-compilation directive is named with its line, and an ordinary one is not",
            ConditionalDirectives("F.cs", "class C {\n#if DEBUG\n    var a = \"x\";\n#else\n    var a = \"y\";\n#endif\n}").Count == 3
                && ConditionalDirectives("F.cs", "  #  if DEBUG\n#endif\n").Count == 2
                && ConditionalDirectives("F.cs", "#region R\n#nullable enable\n#pragma warning disable\n").Count == 0
                && ConditionalDirectives("F.cs", "var s = \"#if not a directive\";\n").Count == 0
                && ConditionalDirectives("F.cs", "class C { }\n").Count == 0,
            new() { "the directive reader misses a conditional directive, names a non-conditional one, or reads a "
                  + "'#if' inside a string as a directive — the first hides the one construct the two readers may "
                  + "legitimately disagree about, and the others red on code that is fine" }, redArm: true);

        Check("RED-GLOSSWORD    a gloss names a verb as a WORD, so ordinary English about one is not read as another",
            VerbsNamedIn("Adds a deep copy of the field").Count == 0
                && VerbsNamedIn("deep-copy the field at field_path from from_plugin's version").Count == 0
                && VerbsNamedIn("Set the field at the index").SequenceEqual(new[] { "Set" })
                && VerbsNamedIn("SetAtIndex overwrites the element").SequenceEqual(new[] { "SetAtIndex" }),
            new() { "the gloss reader matches a verb inside a longer word, or misses one the gloss does name — "
                  + "either way the tail pin is held against the wrong subject" }, redArm: true);

        // The tail-gloss pin, driven over the shape of the failing edit it exists to catch: a ninth verb appended
        // to the recital, which leaves every other verb arm green.
        const string ninth = "Set (default) | Add | Remove | SetAtIndex | InsertAtIndex | ReplaceAll | Merge | CopyFrom | Frobnicate";
        Check("RED-TAILGLOSS    appending a ninth verb moves the glued gloss onto it, and that is reported",
            RecitalNames(ninth).LastOrDefault() == "Frobnicate"
                && GluedGloss($"{ninth} (deep-copy the field — see from_plugin). More prose.", ninth) is { } g && g.StartsWith("deep-copy", StringComparison.Ordinal)
                && GluedGloss($"{ninth}. A parenthetical (later in the sentence) is not glued.", ninth) is null,
            new() { "the tail reader or the glued-gloss reader is wrong: a ninth verb does not read as the tail, the glued "
                  + "parenthetical is not found, or a parenthetical further along the sentence is read as glued" }, redArm: true);

        // The MERGE, over both shapes it joins and the four it must refuse. An Append run assembles the inline
        // consent prompts, so a phrase split across two calls has to reach a scannable sentence; a merge that
        // joined anything MORE than the author wrote would manufacture phrases and red on correct prose, which is
        // why every refusal below is driven too. Raw fixtures, so the C# these read is the C# written here.
        const string appendSrc = """
            var c = new StringBuilder();
            c.Append("this prompt is shown ");
            c.Append("once.");
            """;
        const string brokenSrc = """
            c.Append("this prompt is shown ");
            if (x) return;
            c.Append("once.");
            """;
        const string twoBuildersSrc = """
            a.Append("this prompt is shown ");
            b.Append("once.");
            """;
        const string secondArgSrc = """
            c.Append("this prompt is shown ", n);
            c.Append("once.");
            """;
        // Append finished with AppendLine: one sentence, for the same reason as Write/WriteLine.
        const string appendThenLineSrc = """
            c.Append("this prompt is shown ");
            c.AppendLine("once.");
            """;
        // The FLUENT chain — the commoner call shape in the shipped trees, and the one the merge did not read
        // until 2026-08-26. Two links and three, because a pattern demanding a NAMED receiver before each literal
        // reads a three-link chain as a two-link one and drops the tail silently.
        const string chainSrc = """
            c.Append("this prompt is shown ").Append("once.");
            """;
        const string longChainSrc = """
            c.Append("this ").Append("prompt is shown ").Append("once.");
            """;
        const string chainThenLineSrc = """
            c.Append("this prompt is shown ").AppendLine("once.");
            """;
        // A run that SWITCHES form mid-way. It merges, and correctly: MergeSentences carries the run's START
        // forward, so the receiver read in front of a merged run is the chain's HEAD — which is the name the
        // statement-form gap has to be compared against. Asserted in both directions, because getting the head
        // right is what separates this from merging two different builders.
        const string formSwitchSrc = """
            c.Append("this ").Append("prompt is shown ");
            c.Append("once.");
            """;
        const string formSwitchOtherReceiverSrc = """
            c.Append("this ").Append("prompt is shown ");
            d.Append("once.");
            """;
        // The two shapes a TEXT lookback in front of the literal could not read, both measured on this branch
        // before the receiver came off the syntax tree: a chain whose EARLIER link takes a value leaves a ")"
        // where an identifier pattern wanted a name, and an INDEXER receiver is not an identifier at all. Each
        // one refused a run this guard prints that it reads, so a phrase split across it shipped with INV1 green.
        // Driven with the negative alongside, because reading the receiver off a node is only worth anything if
        // it still tells two different indexed receivers apart.
        const string valueArgThenStatementSrc = """
            c.Append(count).Append("this prompt is shown ");
            c.Append("once.");
            """;
        const string indexerReceiverSrc = """
            cells[i].Sb.Append("this prompt is shown ");
            cells[i].Sb.Append("once.");
            """;
        const string indexerOtherReceiverSrc = """
            cells[i].Sb.Append("this prompt is shown ");
            other[i].Sb.Append("once.");
            """;
        // A VALUE between the two halves, in the statement form. The two literals are the tail of one statement
        // and the head of the next, but the chain keeps going after the first and starts before the second, so a
        // caller reads name and pad in between and the phrase exists nowhere. Both directions, because the two
        // are separate conditions and either one alone would let this through — DialogueTools.AppendTopic is the
        // live shape, and it is both at once.
        const string trailingValueSrc = """
            c.Append("this prompt is shown ").Append(name);
            c.Append("once.");
            """;
        const string leadingValueSrc = """
            c.Append("this prompt is shown ");
            c.Append(pad).Append("once.");
            """;
        // The argument must BE the literal, not merely END with one. An expression can close on a string and
        // append something else entirely — `name == "…"` appends True or False, and the literal is never read
        // by anyone — so a run link is the literal itself or an interpolated string, and nothing else. Driven
        // both ways: the compared literal must not merge, and an interpolated argument must.
        const string comparedLiteralSrc = """
            c.Append(name == "this prompt is shown ");
            c.Append("once.");
            """;
        const string interpolatedArgSrc = """
            c.Append("this prompt is shown ");
            c.Append($"once. {tail}");
            """;
        // Console.Write — the OTHER call that concatenates with nothing between, and the one housecarl-setup
        // talks to a modder through.
        const string consoleSrc = """
            Console.Write("this prompt is shown ");
            Console.Write("once.");
            """;
        // The Line variant, on the side of the break that matters. WriteLine adds its text and breaks AFTER, so
        // the modder reads "this prompt is shown once." on one line and it is one sentence. The claim this
        // fixture used to carry — that a line break lands BETWEEN the two halves — is true only the other way
        // round, and it left every run FINISHED with a Line call outside INV1 with nothing declaring it.
        const string consoleLineSrc = """
            Console.Write("this prompt is shown ");
            Console.WriteLine("once.");
            """;
        // The other way round, which really is two sentences: the break falls between the halves.
        const string lineThenPlainSrc = """
            Console.WriteLine("this prompt is shown ");
            Console.Write("once.");
            """;
        const string lineThenLineSrc = """
            Console.WriteLine("this prompt is shown ");
            Console.WriteLine("once.");
            """;
        const string appendLineThenAppendSrc = """
            c.AppendLine("this prompt is shown ");
            c.Append("once.");
            """;
        const string mixedMethodSrc = """
            c.Write("this prompt is shown ");
            c.Append("once.");
            """;
        // A comment does not end an authored sentence. Both comment forms, on both join shapes.
        const string commentJoinSrc = """
            var s = "this prompt is shown " + // the modder reads one line
                "once.";
            """;
        const string blockCommentJoinSrc = """
            var s = "this prompt is shown " + /* aside */ "once.";
            """;
        const string commentAppendSrc = """
            c.Append("this prompt is shown ");   // aside
            c.Append("once.");
            """;
        static List<string> Merged(string src) =>
            MergeSentences(src, RoslynLiteralReader.Read(src, out _, out var calls), calls)
                .Select(l => l.Text).ToList();
        static bool Carries(string src) =>
            Merged(src).Any(t => t.Contains("shown once", StringComparison.Ordinal));

        Check("RED-APPENDRUN    consecutive Append or Console.Write literals on ONE receiver merge into one sentence in BOTH the statement and the fluent form, across a switch between them, past an earlier value argument and on an indexer-spelled receiver and into an interpolated argument, and FINISHED with the Line variant of its verb, and fifteen shapes that are NOT one run do not",
            Carries(appendSrc)
                && Merged(appendSrc).Contains("this prompt is shown once.", StringComparer.Ordinal)
                && Carries(chainSrc)
                && Merged(chainSrc).Contains("this prompt is shown once.", StringComparer.Ordinal)
                && Carries(longChainSrc)
                && Merged(longChainSrc).Contains("this prompt is shown once.", StringComparer.Ordinal)
                && !Carries(brokenSrc)
                && !Carries(twoBuildersSrc)
                && !Carries(secondArgSrc)
                && Carries(appendThenLineSrc)
                && Carries(chainThenLineSrc)
                && Carries(formSwitchSrc)
                && Merged(formSwitchSrc).Contains("this prompt is shown once.", StringComparer.Ordinal)
                && !Carries(formSwitchOtherReceiverSrc)
                && Carries(valueArgThenStatementSrc)
                && Merged(valueArgThenStatementSrc).Contains("this prompt is shown once.", StringComparer.Ordinal)
                && Carries(indexerReceiverSrc)
                && Merged(indexerReceiverSrc).Contains("this prompt is shown once.", StringComparer.Ordinal)
                && !Carries(indexerOtherReceiverSrc)
                && !Carries(trailingValueSrc)
                && !Carries(leadingValueSrc)
                && !Carries(comparedLiteralSrc)
                && Carries(interpolatedArgSrc)
                && Carries(consoleSrc)
                && Merged(consoleSrc).Contains("this prompt is shown once.", StringComparer.Ordinal)
                && Carries(consoleLineSrc)
                && Merged(consoleLineSrc).Contains("this prompt is shown once.", StringComparer.Ordinal)
                && !Carries(lineThenPlainSrc)
                && !Carries(lineThenLineSrc)
                && !Carries(appendLineThenAppendSrc)
                && !Carries(mixedMethodSrc)
                && Carries(commentJoinSrc)
                && Carries(blockCommentJoinSrc)
                && Carries(commentAppendSrc),
            new() { "the merge does not read an unbroken Append run as one sentence in one of its two written forms, or "
                  + "loses one because an earlier link took a value or the receiver is spelled with an indexer — so a "
                  + "phrase split across two calls ships to a modder with INV1 green, which is what compact_plugin's prompt "
                  + "made possible and what the fluent chain, the commoner shape, went on doing — or it joins across an "
                  + "intervening statement, across two different builders, past a second argument, out of a run that a "
                  + "Line call already closed, into a different verb, off the head of a chain onto a DIFFERENT "
                  + "builder, between two different indexed receivers, or "
                  + "across a value that one statement ends with or the next begins with, or into an expression that "
                  + "merely ENDS on a literal while appending something else, any of which manufactures a phrase no "
                  + "caller reads and reds INV1 on correct prose" }, redArm: true);

        Check("RED-COVER        INV5's coverage predicate reports a compiled string no literal accounts for",
            !Covered("a compiled description no source literal accounts for", new[] { "something else entirely" })
                && Covered("a compiled description no source literal accounts for", new[] { "a compiled description no source literal accounts for" })
                && !Covered("a compiled description no source literal accounts for", new[] { "." }),
            new() { "the coverage predicate accepts an uncovered string, rejects a covered one, or lets a one-character "
                  + "literal cover anything — INV5 would pass with the SOURCE scan reading nothing" }, redArm: true);

        // BOTH DIRECTIONS. The subtraction half is the original arm; the ADDITION half is new, and it is the one
        // that was missing — the sabotage that "proved" this pin only ever removed a tree, so a tree added to the
        // net and never enrolled here passed green and its prose entered INV1's net in silence.
        Check("RED-ROOTS        a shipped tree missing from the scanned set is reported, an EXTRA scanned tree is reported, and a matching set is not",
            TreeSetMismatch(new[] { "housecarl-mcp", "housecarl-setup" }).Count == 1
                && TreeSetMismatch(PublishedShippedTrees.Append("housecarl-newthing").ToList()).Count == 1
                && TreeSetMismatch(new[] { "housecarl-mcp", "housecarl-newthing" }).Count == 3
                && TreeSetMismatch(PublishedShippedTrees).Count == 0,
            new() { "the root pin does not notice a tree dropped from the scanned set, does not notice one ADDED to it, or "
                  + "reports a set that matches — dropping a tree shrinks INV1's net and INV5's oracle together, and adding "
                  + "one puts a tree's prose in the net with nothing having enrolled it" }, redArm: true);

        // BOTH DIRECTIONS again, and here the first one is the load-bearing half: a file the compiler says it
        // compiled and the walk never reached is the shape that shipped a false consent claim at 59/59 green.
        // The build-output classifier is driven here too — it is what keeps the generated documents out, and a
        // classifier that stopped recognising obj/ would put three phantom files on the manifest side of every
        // comparison.
        Check("RED-MANIFEST     a compiled file the net never scanned is reported, a scanned file no manifest names is reported, a matching pair is not, and build output is classified as such",
            ManifestMismatch(new[] { "src/a/A.cs", "src/elsewhere/B.cs" }, new[] { "src/a/A.cs" }).Count == 1
                && ManifestMismatch(new[] { "src/a/A.cs" }, new[] { "src/a/A.cs", "src/a/Dead.cs" }).Count == 1
                && ManifestMismatch(new[] { "src/a/A.cs" }, new[] { "src/a/B.cs" }).Count == 2
                && ManifestMismatch(new[] { "src/a/A.cs" }, new[] { "src/a/a.cs" }).Count == 0
                && ManifestMismatch(new[] { "src/a/A.cs" }, new[] { "src/a/A.cs" }).Count == 0
                && IsBuildOutput("src/a/obj/Release/net9.0/a.AssemblyInfo.cs")
                && IsBuildOutput("src/a/bin/Release/net9.0/x.cs")
                && !IsBuildOutput("src/a/Objects.cs")
                && !IsBuildOutput("src/binder/Thing.cs"),
            new() { "the membership comparison does not notice a compiled file outside the net, does not notice a scanned file "
                  + "outside the compilation, disagrees about a matching pair, or the build-output classifier misreads a path — "
                  + "the first of those is a shipped source file whose prose is outside INV1's net with every arm green" }, redArm: true);

        // The packaging derivation, driven over a synthetic script and a synthetic project graph: the shape it
        // must read, and the three shapes it must REFUSE rather than absorb. The counts are the arm's denominator,
        // so they are asserted, not just printed.
        const string ps1 = "$McpProj   = Join-Path $RepoRoot 'src\\housecarl-mcp'\n"
                         + "$SetupProj = Join-Path $RepoRoot 'src\\housecarl-setup'\n"
                         + "dotnet publish $McpProj -c Release -o $ServerDir\n"
                         + "dotnet publish $SetupProj -c Release -o $PkgRoot\n";
        static List<string>? Graph(string t) => t switch
        {
            "housecarl-mcp" => new List<string> { "housecarl-core" },
            "housecarl-setup" => new List<string>(),
            "housecarl-core" => new List<string>(),
            _ => null,
        };
        var good = DeriveShippedTrees(ps1, Graph);
        var noAssign = DeriveShippedTrees("dotnet publish $Mystery -c Release\n", Graph);
        var notSrc = DeriveShippedTrees("dotnet publish $P\n$P = Join-Path $R 'tools\\thing'\n", Graph);
        var unreadable = DeriveShippedTrees(ps1.Replace("housecarl-setup", "housecarl-ghost"), Graph);
        var silent = DeriveShippedTrees("# the script stopped publishing anything\n", Graph);
        // A BARE relative path, the form the docstring always claimed to read. The variable alternative used to
        // allow the '$' to be absent, so it matched 'src' and stopped, and the call resolved to a tree that is
        // not under src/ — reported as residue, which reads as "the script does something odd" rather than as
        // "this pattern cannot read a path".
        var barePath = DeriveShippedTrees("dotnet publish src/housecarl-mcp -c Release\n", Graph);
        var quotedPath = DeriveShippedTrees("dotnet publish 'src/housecarl-setup' -c Release\n", Graph);

        Check("RED-SHIPDERIVE   the packaging derivation follows publish calls through the ProjectReference graph, and NAMES every call it could not resolve, whether the project is a variable, a quoted path, or a bare one",
            good.Trees.SequenceEqual(new[] { "housecarl-core", "housecarl-mcp", "housecarl-setup" }, StringComparer.Ordinal)
                && (good.PublishCalls, good.Resolved, good.Residue.Count) == (2, 2, 0)
                && DerivedSetMismatch(good.Trees).Count == 0
                && (noAssign.PublishCalls, noAssign.Resolved, noAssign.Residue.Count) == (1, 0, 1)
                && (notSrc.PublishCalls, notSrc.Resolved, notSrc.Residue.Count) == (1, 0, 1)
                && unreadable.Residue.Count == 1 && !unreadable.Trees.Contains("housecarl-setup")
                && (silent.PublishCalls, silent.Resolved, silent.Residue.Count) == (0, 0, 1)
                && barePath.Trees.SequenceEqual(new[] { "housecarl-core", "housecarl-mcp" }, StringComparer.Ordinal)
                && (barePath.PublishCalls, barePath.Resolved, barePath.Residue.Count) == (1, 1, 0)
                && quotedPath.Trees.SequenceEqual(new[] { "housecarl-setup" }, StringComparer.Ordinal)
                && (quotedPath.PublishCalls, quotedPath.Resolved, quotedPath.Residue.Count) == (1, 1, 0)
                && DerivedSetMismatch(new[] { "housecarl-mcp", "housecarl-core", "housecarl-setup", "housecarl-extra" }).Count == 1
                && DerivedSetMismatch(new[] { "housecarl-mcp", "housecarl-core" }).Count == 1,
            new() { "the derivation loses a tree reachable only through a ProjectReference, absorbs an unresolvable publish "
                  + "call instead of naming it, treats an unreadable .csproj as a leaf, reports a denominator that does not "
                  + "match the calls in the script, stays silent when the script publishes nothing at all, or cannot read a "
                  + "project named as a bare relative path — any of which lets a tree start shipping with its prose outside "
                  + "INV1's net and nothing red" }, redArm: true);

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
    /// the second design throw and take all its arms with it; a raw string literal; escaped braces; a RAW
    /// INTERPOLATED string, where a brace run shorter than the dollar count is content rather than an escape and a
    /// run longer than it is content followed by an opener — the two shapes each reader decoded its own way; a
    /// character literal holding a quote; and comment text, which must be read by neither.</summary>
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
            var n = $$$"""a {{ doubled brace pair }} kept and a {{{hole}}} opened""";
            var o = $$"""a {{{value}}} hole with one surplus brace, and a { single one""";
            var p = @"""quoted"" opens this verbatim string";
            var q = "an ANSI reset \e[0m sits in console prose";
            """";

        var a = RoslynLiteralReader.Read(fixture, out var parseErrors, out var fixtureCalls);
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
            // A raw interpolated string escapes nothing: the doubled braces below are CONTENT, and reader A
            // collapsing them (as the two regular flavours require) held a value the compiler never builds.
            "a {{ doubled brace pair }} kept and a {\u2026} opened",
            // A run longer than the opener count is a surplus brace of content plus the opener; reader B took the
            // whole run as the opener and dropped that character.
            "a {{\u2026}} hole with one surplus brace, and a { single one",
            // A verbatim literal that OPENS on an escaped quote. Reader B decided raw-vs-regular on the quote
            // run alone, so @"""… read as a raw string: the wrong text, or the rest of the file swallowed.
            "\"quoted\" opens this verbatim string",
            // C# 13's \e (U+001B). Reader A decodes it at LanguageVersion.Preview; reader B had no arm for it
            // and appended the letter instead, so the two disagreed about text neither had lost.
            "an ANSI reset \u001b[0m sits in console prose",
        };
        var sentences = MergeSentences(fixture, a, fixtureCalls).Select(l => l.Text).ToList();
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
        var labelled = MergeSentences(fixture, a, fixtureCalls).Select((l, n) => new Sentence($"fixture:{n}", l.Text)).ToList();
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

        // Both fixtures are DERIVED from the rule that drives them, like every other arm here. Pinning them to
        // Phrases[0] with hand-typed matching text meant reordering the phrase list broke this arm with a message
        // about the docstring boundary — which would not have moved.
        var banned = Phrases.First(r => r.Companions.Length == 0);
        string inComment = $"// a {banned.Phrase} thing\n";
        string inLiteral = $"var x = \"a {banned.Phrase} thing\";";
        Check("RED-COMMENTS     a phrase planted in a COMMENT is NOT reported — the declared docstring boundary, tested rather than asserted",
            Scan(banned, MergeSentences(inComment, RoslynLiteralReader.Read(inComment, out _, out var commentCalls), commentCalls)
                    .Select((l, n) => new Sentence($"fixture:{n}", l.Text)).ToList(), Array.Empty<Exemption>(), null).Violations.Count == 0
                && Scan(banned, MergeSentences(inLiteral, RoslynLiteralReader.Read(inLiteral, out _, out var literalCalls), literalCalls)
                    .Select((l, n) => new Sentence($"fixture:{n}", l.Text)).ToList(), Array.Empty<Exemption>(), null).Violations.Count == 1,
            new() { "the scanner does not see a phrase in a literal, or DOES see one in a comment — the declared "
                  + "docstring boundary would be false in one direction or the other" }, redArm: true);
    }

    // ================= reporting =================

    /// <summary>A type carrying a nested type, both described, so <c>RED-NESTEDTYPE</c> has a shape the live
    /// surface does not yet provide. Lives in the generator, which is never scanned, so its prose is not in
    /// INV1's net and cannot be mistaken for shipped text.</summary>
    [Description("the outer type's own description")]
    sealed class NestedFixture
    {
        [Description("the nested type's own description")]
        internal sealed class Inner
        {
            [Description("the nested type's member description")]
            internal string? Slot { get; init; }
        }
    }

    /// <summary>Which TIER an arm's claim belongs to. Declared per arm rather than inferred from its name,
    /// because the whole point of the split is that a reader can tell the two apart without knowing the code.
    /// <list type="bullet">
    ///   <item><b>Construction</b> — the claim holds by construction: the two-reader net and the arms over it,
    ///         and the pins, which compare two independently written statements of one fact. Unqualified.</item>
    ///   <item><b>BestEffort</b> — the arm reads MEANING out of prose or reflection by pattern. A pattern's reach
    ///         is never a by-construction fact, so the arm is labelled and MUST print its own coverage: how many
    ///         of the things present it actually compared, and what it skipped, named with the reason.</item>
    ///   <item><b>Harness</b> — a RED/GREEN arm driving a checker with synthetic input. It makes no claim about
    ///         the shipped surface at all; it says the checker above can still fail.</item>
    /// </list></summary>
    enum Tier { Construction, BestEffort, Harness }

    static void Check(string label, bool ok, List<string> detail, bool redArm = false, Tier tier = Tier.Harness)
    {
        if (tier == Tier.Construction) _class1++;
        else if (tier == Tier.BestEffort) _class2++;
        else _harness++;
        var tag = tier switch { Tier.Construction => "[c1] ", Tier.BestEffort => "[c2] ", _ => "     " };
        Console.WriteLine($"   [{(ok ? "PASS" : "FAIL")}] {tag}{label}");
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
