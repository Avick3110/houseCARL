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
    /// <summary>Every family the merged surface knows, in the order a response renders them. Phase 2's dialogue
    /// family joins this list and nothing else has to change: what runs, what is reported as not run, and what the
    /// refusal offers all read from here.</summary>
    public static readonly IReadOnlyList<SweepFamily> Registered = new[] { SweepFamily.Errors, SweepFamily.Scripts };

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
        _ => f.ToString().ToLowerInvariant(),
    };

    /// <summary>What this family is, in the fewest words that still distinguish it — for the sentence naming a
    /// family that did not run, where the token alone ("scripts") does not say what asking for it would buy.</summary>
    public static string Describe(SweepFamily f) => f switch
    {
        SweepFamily.Errors => "dangling references, missing masters and parse failures",
        SweepFamily.Scripts => "unbound script properties",
        _ => Token(f),
    };

    /// <summary>The exact <c>findings=</c> spelling that adds one family to a call. A remedy that names a knob
    /// without spelling it is a remedy the caller has to guess at.</summary>
    public static string Spelling(SweepFamily f) => "findings=[\"" + Token(f) + "\"]";

    /// <summary>The whole legal vocabulary, for the refusal — every family token and every class token, so a
    /// caller who misspelled one sees all of them rather than the half their tool used to have.</summary>
    public static string Vocabulary =>
        string.Join(", ", Registered.Select(f => "'" + Token(f) + "'"))
        + " (whole families), or the classes inside them: 'dangling', 'missing_masters' (errors); "
        + "'unbound_object', 'unbound_scalar', 'unbound' (both), 'bound_null' (scripts)";

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
            switch (Normalize(raw))
            {
                case "errors":
                    Add(SweepFamily.Errors); errorsWholeFamily = true; break;
                case "scripts":
                    Add(SweepFamily.Scripts); scriptsWholeFamily = true; break;

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

    static string Normalize(string? s) => (s ?? "").Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
}
