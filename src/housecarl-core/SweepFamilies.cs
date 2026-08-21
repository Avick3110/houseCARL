namespace HousecarlCore;

/// <summary>
/// A FAMILY of derived findings on the merged <c>check</c> surface (SPEC §6.1): one sweep, one taxonomy, one
/// section of the response. The families are what <c>findings=</c> selects among; the CLASSES inside a family
/// (<see cref="ErrorFindingClass"/>, <see cref="ScriptFindingClass"/>) are what it selects WITHIN one.
/// </summary>
public enum SweepFamily
{
    /// <summary>Load-order integrity: dangling references, missing masters, parse failures. Was
    /// <c>housecarl_check_errors</c>.</summary>
    Errors,

    /// <summary>VMAD script-property binding: unbound properties, bound-but-null objects, unverifiable
    /// attachments. Was <c>housecarl_validate_scripts</c>.</summary>
    Scripts,

    /// <summary>Dialogue graph integrity over SEEDED topics and quests: broken quest/branch/LinkTo wiring, dangling
    /// previous-links, silent voiced lines, result scripts that will not fire, malformed conditions, missing
    /// CK-parity subrecords, a stale or missing <c>.seq</c>. Was <c>housecarl_validate_dialogue</c>'s findings —
    /// its classes 1-7. Class 8, the effective merged INFO order, is NOT here: an ordered sequence over the
    /// touching-plugin stack is a different result shape, and SPEC §6.1 sends it to
    /// <c>records project=info_order</c> instead.
    ///
    /// <para><b>It is SEEDED, not swept</b> (SPEC §6.1 F1.1). Its selection is seed-expansion — a quest expands to
    /// every topic it owns — so it takes its own <c>seeds=</c> rather than the other families' plugin scope, and a
    /// call that names it without seeds is a declared cost-refusal (F1.2), never a whole-order dialogue
    /// sweep.</para></summary>
    Dialogue,
}

/// <summary>
/// The merged <c>findings=</c> vocabulary: which FAMILIES a call runs, and which CLASSES within each.
///
/// <para><b>Family tokens and class tokens are one vocabulary, not two parameters.</b> A family token means every
/// class in that family; a class token means that family runs, narrowed to that class. This is the device
/// <c>unbound</c> already was — a group name standing for two classes inside one vocabulary — applied one level
/// up, so naming several families needs no second parameter and no guard clause (SPEC §6.1 F1.3: a call naming
/// several families runs each over its own declared selection, and the render is sectioned per family).</para>
///
/// <para><b>The default is ONE family, and the response says so.</b> Omitting <c>findings=</c> runs the errors
/// family alone. It cannot be every family: an unscoped scripts sweep is 468 seconds on a 3800-plugin order
/// (measured), and SPEC §6.1 F1.2 declares an unscoped dialogue sweep a cost-refusal outright. A default that
/// narrows silently would be the sweep answering a question the caller did not ask and not saying which — so the
/// selection carries <see cref="NotRun"/>, and the render states every registered family it did not run together
/// with the spelling that gets it (<see cref="Spelling"/>). The default narrows only because the response
/// discloses it (Q3).</para>
/// </summary>
public sealed class SweepFamilySelection
{
    /// <summary>Every family the merged surface knows, in the order a response renders them. Membership is declared
    /// HERE and nowhere else: what a call can ask for (<see cref="TryParse"/>), what the response reports as not run
    /// (<see cref="NotRun"/>) and what the refusal offers (<see cref="Vocabulary"/>) all read this list.
    ///
    /// <para><b>The parse used to be a hand-written case per family, and that was a hole rather than a chore.</b> A
    /// family added here without its own <c>case</c> was one the refusal OFFERED — <see cref="Vocabulary"/> is built
    /// off this list — and the parser then rejected, so the response would name a spelling that does not work. The
    /// family tokens are matched against this list instead, so a registered family is askable by construction and
    /// the two can no longer disagree. What still needs writing per family is its own DATA (<see cref="Token"/>,
    /// <see cref="Title"/>, <see cref="Describe"/>) and its class tokens, which are facts about that family rather
    /// than a second membership list.</para></summary>
    public static readonly IReadOnlyList<SweepFamily> Registered =
        new[] { SweepFamily.Errors, SweepFamily.Scripts, SweepFamily.Dialogue };

    SweepFamilySelection(IReadOnlyList<SweepFamily> ran, ErrorFindingClass errors, ScriptFindingClass scripts, bool defaulted)
    {
        Ran = ran;
        ErrorClasses = errors;
        ScriptClasses = scripts;
        Defaulted = defaulted;
        NotRun = Registered.Where(f => !ran.Contains(f)).ToArray();
    }

    /// <summary>The families this call runs, in <see cref="Registered"/> order — never the order the caller named
    /// them, so two calls selecting the same families render alike.</summary>
    public IReadOnlyList<SweepFamily> Ran { get; }

    /// <summary>The registered families this call does NOT run. The response states these by name; a caller
    /// cannot otherwise tell a family that found nothing from one that never ran.</summary>
    public IReadOnlyList<SweepFamily> NotRun { get; }

    /// <summary>Which error classes the errors family looks for. <see cref="ErrorFindingClass.All"/> when the
    /// family was named without narrowing.</summary>
    public ErrorFindingClass ErrorClasses { get; }

    /// <summary>Which script classes the scripts family reports.</summary>
    public ScriptFindingClass ScriptClasses { get; }

    /// <summary><c>findings=</c> was omitted, so <see cref="Ran"/> is the default rather than a caller's choice.
    /// The render says which of the two it is: "you did not ask for these" and "you asked for these and not those"
    /// are different sentences.</summary>
    public bool Defaulted { get; }

    /// <summary>The family's token as a caller spells it in <c>findings=</c>.</summary>
    public static string Token(SweepFamily f) => f switch
    {
        SweepFamily.Errors => "errors",
        SweepFamily.Scripts => "scripts",
        SweepFamily.Dialogue => "dialogue",
        _ => f.ToString().ToLowerInvariant(),
    };

    /// <summary>The family's own section title in a merged response — the title its ancestor tool put at the top
    /// of a whole response, now naming a section of one.</summary>
    public static string Title(SweepFamily f) => f switch
    {
        SweepFamily.Errors => "load-order integrity sweep",
        SweepFamily.Scripts => "VMAD script-property binding sweep",
        SweepFamily.Dialogue => "dialogue graph validation (seeded)",
        _ => Token(f),
    };

    /// <summary>What this family is, in the fewest words that still distinguish it — for the sentence naming a
    /// family that did not run, where the token alone ("scripts") does not say what asking for it would buy.</summary>
    public static string Describe(SweepFamily f) => f switch
    {
        SweepFamily.Errors => "dangling references, missing masters and parse failures",
        SweepFamily.Scripts => "unbound script properties",
        SweepFamily.Dialogue => "broken dialogue wiring, silent lines and result scripts that will not fire",
        _ => Token(f),
    };

    /// <summary>The exact spelling that adds one family to a call. A remedy that names a knob without spelling it is
    /// a remedy the caller has to guess at — and one that spells a call the tool would REFUSE is worse than none.
    /// The dialogue family is seeded rather than swept (F1.1), so <c>findings=["dialogue"]</c> on its own is the
    /// cost-refusal (F1.2); its remedy carries the <c>seeds=</c> that makes the call run.</summary>
    public static string Spelling(SweepFamily f) => f == SweepFamily.Dialogue
        ? "findings=[\"dialogue\"] seeds=[\"XXXXXX:Plugin.esp\"]"
        : "findings=[\"" + Token(f) + "\"]";

    /// <summary>The whole legal vocabulary, for the refusal — every family token and every class token, so a
    /// caller who misspelled one sees all of them rather than the half their tool used to have.
    ///
    /// <para>The FAMILY half reads <see cref="Registered"/>, so it cannot offer a token
    /// <see cref="TryParse"/> refuses. The CLASS half is written per family, because a family's classes are a fact
    /// about that family and there is nowhere else to state them. The dialogue family has none: its ancestor
    /// filtered nothing, and inventing a class vocabulary it never had would be capability growth wearing a merge's
    /// clothes — it narrows by <c>seeds=</c>, and the sentence says so rather than leaving a caller to wonder why
    /// their family is the one with no classes listed.</para></summary>
    public static string Vocabulary =>
        string.Join(", ", Registered.Select(f => "'" + Token(f) + "'"))
        + " (whole families), or the classes inside them: 'dangling', 'missing_masters' (errors); "
        + "'unbound_object', 'unbound_scalar', 'unbound' (both), 'bound_null' (scripts). The dialogue family takes "
        + "no class token — it narrows by seeds=, not by class";

    /// <summary>Parse the merged <c>findings=</c>. An empty or omitted list is the errors-family default; an
    /// unrecognized token is a NAMED refusal listing the whole vocabulary, never a silent drop to "everything"
    /// (Q3 — that would answer a different question than the one asked).</summary>
    public static bool TryParse(IReadOnlyList<string>? names, out SweepFamilySelection selection, out string? error)
    {
        selection = null!;
        error = null;

        if (names is not { Count: > 0 })
        {
            selection = new SweepFamilySelection(new[] { SweepFamily.Errors }, ErrorFindingClass.All,
                                                 ScriptFindingClass.All, defaulted: true);
            return true;
        }

        var ran = new List<SweepFamily>();
        var errorClasses = ErrorFindingClass.None;
        var scriptClasses = ScriptFindingClass.None;
        bool errorsWholeFamily = false, scriptsWholeFamily = false;

        foreach (var raw in names)
        {
            // FAMILY tokens are matched against Registered, never against a hand-written case per family: a family
            // this list carries is one the refusal already offers (Vocabulary), so resolving the token here is what
            // stops the response from naming a spelling the parser rejects.
            string token = Normalize(raw);
            var named = FamilyFor(token);
            if (named is { } fam)
            {
                Add(fam);
                if (fam == SweepFamily.Errors) errorsWholeFamily = true;
                if (fam == SweepFamily.Scripts) scriptsWholeFamily = true;
                continue;
            }

            switch (token)
            {
                case "dangling":
                    Add(SweepFamily.Errors); errorClasses |= ErrorFindingClass.Dangling; break;
                case "missing_masters":
                    Add(SweepFamily.Errors); errorClasses |= ErrorFindingClass.MissingMasters; break;

                case "unbound_object":
                    Add(SweepFamily.Scripts); scriptClasses |= ScriptFindingClass.UnboundObject; break;
                case "unbound_scalar":
                    Add(SweepFamily.Scripts); scriptClasses |= ScriptFindingClass.UnboundScalar; break;
                case "unbound":
                    Add(SweepFamily.Scripts); scriptClasses |= ScriptFindingClass.UnboundObject | ScriptFindingClass.UnboundScalar; break;
                case "bound_null":
                    Add(SweepFamily.Scripts); scriptClasses |= ScriptFindingClass.BoundNull; break;

                default:
                    error = $"findings='{raw}' is not a check finding family or class — use {Vocabulary}. "
                          + "Unscannable records, scan errors and unverifiable script attachments are ALWAYS "
                          + "reported and cannot be filtered out (a suppressed 'could not read' would read as a "
                          + "clean result).";
                    return false;
            }
        }

        // A family named as a WHOLE gets every class; a family reached only through class tokens gets exactly
        // those. Naming both ('scripts' and 'bound_null') is the whole family — the wider of the two, which is
        // what the caller asked for by naming the family at all.
        selection = new SweepFamilySelection(
            Registered.Where(ran.Contains).ToArray(),
            errorsWholeFamily || errorClasses == ErrorFindingClass.None ? ErrorFindingClass.All : errorClasses,
            scriptsWholeFamily || scriptClasses == ScriptFindingClass.None ? ScriptFindingClass.All : scriptClasses,
            defaulted: false);
        return true;

        void Add(SweepFamily f) { if (!ran.Contains(f)) ran.Add(f); }
    }

    /// <summary>The registered family a whole-family token names, or null where the token is not one. The ONE
    /// resolution of a family token, shared by the parser and by anything else that has to know — so
    /// <see cref="Vocabulary"/> offering a token and <see cref="TryParse"/> accepting it are the same fact.</summary>
    static SweepFamily? FamilyFor(string token)
    {
        foreach (var f in Registered) if (Token(f) == token) return f;
        return null;
    }

    static string Normalize(string? s) => (s ?? "").Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
}
