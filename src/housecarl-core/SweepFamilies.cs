namespace HousecarlCore;

/// <summary>A FAMILY of derived findings on the merged <c>check</c> surface: one sweep, one taxonomy, one section of
/// the response. Families are what <c>findings=</c> selects among; classes are what it selects within one.</summary>
public enum SweepFamily
{
    /// <summary>Load-order integrity: dangling references, missing masters, parse failures.</summary>
    Errors,

    /// <summary>VMAD script-property binding: unbound properties, bound-but-null objects, unverifiable
    /// attachments.</summary>
    Scripts,

    /// <summary>Dialogue graph integrity over SEEDED topics and quests. Seeded, not swept: it takes its own
    /// <c>seeds=</c>, and a call naming it without seeds is a declared cost-refusal.</summary>
    Dialogue,

    /// <summary>FACEGEN: which mod wins each NPC's head .nif, which wins its face .dds, and which plugin wins the
    /// NPC_ record behind them, joined one row per NPC. Swept like the errors family, and not in the default set.</summary>
    Facegen,
}

/// <summary>The merged <c>findings=</c> vocabulary: which families a call runs, and which classes within each. Family
/// tokens and class tokens are one vocabulary; contracts in docs/architecture/check-family-tests.md.</summary>
public sealed class SweepFamilySelection
{
    /// <summary>Every family the merged surface knows, in the order a response renders them — the one place
    /// membership is declared, read by <see cref="TryParse"/>, <see cref="NotRun"/> and <see cref="Vocabulary"/>
    /// alike. Pinned by REGISTERED-IS-THE-MEMBERSHIP in CheckMergeProbe.</summary>
    public static readonly IReadOnlyList<SweepFamily> Registered =
        new[] { SweepFamily.Errors, SweepFamily.Scripts, SweepFamily.Dialogue, SweepFamily.Facegen };

    SweepFamilySelection(IReadOnlyList<SweepFamily> ran, ErrorFindingClass errors, ScriptFindingClass scripts,
                         FaceGenFindingClass facegen, bool defaulted)
    {
        Ran = ran;
        ErrorClasses = errors;
        ScriptClasses = scripts;
        FaceGenClasses = facegen;
        Defaulted = defaulted;
        NotRun = Registered.Where(f => !ran.Contains(f)).ToArray();
    }

    /// <summary>The families this call runs, in <see cref="Registered"/> order — never the order the caller named
    /// them.</summary>
    public IReadOnlyList<SweepFamily> Ran { get; }

    /// <summary>The registered families this call does NOT run; the response states these by name.</summary>
    public IReadOnlyList<SweepFamily> NotRun { get; }

    /// <summary>Which error classes the errors family looks for; <see cref="ErrorFindingClass.All"/> when the family
    /// was named without narrowing.</summary>
    public ErrorFindingClass ErrorClasses { get; }

    /// <summary>Which script classes the scripts family reports.</summary>
    public ScriptFindingClass ScriptClasses { get; }

    /// <summary>Which facegen classes the facegen family reports; under <see cref="FaceGenFindingClass.All"/> the
    /// benign <c>family_split</c> class is counted in the header but not listed.</summary>
    public FaceGenFindingClass FaceGenClasses { get; }

    /// <summary><c>findings=</c> was omitted, so <see cref="Ran"/> is the default rather than a caller's choice.</summary>
    public bool Defaulted { get; }

    /// <summary>The family's token as a caller spells it in <c>findings=</c>.</summary>
    public static string Token(SweepFamily f) => f switch
    {
        SweepFamily.Errors => "errors",
        SweepFamily.Scripts => "scripts",
        SweepFamily.Dialogue => "dialogue",
        SweepFamily.Facegen => "facegen",
        _ => f.ToString().ToLowerInvariant(),
    };

    /// <summary>The family's own section title in a merged response.</summary>
    public static string Title(SweepFamily f) => f switch
    {
        SweepFamily.Errors => "load-order integrity sweep",
        SweepFamily.Scripts => "VMAD script-property binding sweep",
        SweepFamily.Dialogue => "dialogue graph validation (seeded)",
        SweepFamily.Facegen => "facegen mesh/tint/record join",
        _ => Token(f),
    };

    /// <summary>What this family is, in the fewest words that still distinguish it — for the sentence naming a family
    /// that did not run.</summary>
    public static string Describe(SweepFamily f) => f switch
    {
        SweepFamily.Errors => "dangling references, missing masters and parse failures",
        SweepFamily.Scripts => "unbound script properties",
        SweepFamily.Dialogue => "broken dialogue wiring, silent lines and result scripts that will not fire",
        SweepFamily.Facegen => "dark-face desyncs between an NPC's baked files and its winning record",
        _ => Token(f),
    };

    /// <summary>The exact spelling that adds one family to a call; the dialogue family's carries the <c>seeds=</c>
    /// that makes the call run rather than refuse.</summary>
    public static string Spelling(SweepFamily f) => f == SweepFamily.Dialogue
        ? "findings=[\"dialogue\"] seeds=[\"XXXXXX:Plugin.esp\"]"
        : "findings=[\"" + Token(f) + "\"]";

    /// <summary>The whole legal vocabulary, for the refusal — every family token (read off <see cref="Registered"/>)
    /// and every class token.</summary>
    public static string Vocabulary =>
        string.Join(", ", Registered.Select(f => "'" + Token(f) + "'"))
        + " (whole families), or the classes inside them: 'dangling', 'missing_masters' (errors); "
        + "'unbound_object', 'unbound_scalar', 'unbound' (both), 'bound_null' (scripts); "
        + FaceGenCheck.Vocabulary + " (facegen). The dialogue family takes "
        + "no class token — it narrows by seeds=, not by class";

    /// <summary>Parse the merged <c>findings=</c>. An empty or omitted list is the errors-family default; an
    /// unrecognized token is a named refusal listing the whole vocabulary.</summary>
    public static bool TryParse(IReadOnlyList<string>? names, out SweepFamilySelection selection, out string? error)
    {
        selection = null!;
        error = null;

        if (names is not { Count: > 0 })
        {
            selection = new SweepFamilySelection(new[] { SweepFamily.Errors }, ErrorFindingClass.All,
                                                 ScriptFindingClass.All, FaceGenFindingClass.All, defaulted: true);
            return true;
        }

        var ran = new List<SweepFamily>();
        var errorClasses = ErrorFindingClass.None;
        var scriptClasses = ScriptFindingClass.None;
        var facegenClasses = FaceGenFindingClass.None;
        bool errorsWholeFamily = false, scriptsWholeFamily = false, facegenWholeFamily = false;

        foreach (var raw in names)
        {
            // Family tokens resolve against Registered, the same list Vocabulary offers from.
            string token = Normalize(raw);
            var named = FamilyFor(token);
            if (named is { } fam)
            {
                Add(fam);
                if (fam == SweepFamily.Errors) errorsWholeFamily = true;
                if (fam == SweepFamily.Scripts) scriptsWholeFamily = true;
                if (fam == SweepFamily.Facegen) facegenWholeFamily = true;
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
                    // The facegen family's class tokens resolve through the one lookup that spells them.
                    if (FaceGenCheck.ClassFor(token) is { } fc)
                    {
                        Add(SweepFamily.Facegen); facegenClasses |= fc; break;
                    }
                    error = $"findings='{raw}' is not a check finding family or class — use {Vocabulary}. "
                          + "Unscannable records, scan errors and unverifiable script attachments are ALWAYS "
                          + "reported and cannot be filtered out (a suppressed 'could not read' would read as a "
                          + "clean result).";
                    return false;
            }
        }

        // A family named as a WHOLE gets every class; a family reached only through class tokens gets exactly those.
        selection = new SweepFamilySelection(
            Registered.Where(ran.Contains).ToArray(),
            errorsWholeFamily || errorClasses == ErrorFindingClass.None ? ErrorFindingClass.All : errorClasses,
            scriptsWholeFamily || scriptClasses == ScriptFindingClass.None ? ScriptFindingClass.All : scriptClasses,
            facegenWholeFamily || facegenClasses == FaceGenFindingClass.None ? FaceGenFindingClass.All : facegenClasses,
            defaulted: false);
        return true;

        void Add(SweepFamily f) { if (!ran.Contains(f)) ran.Add(f); }
    }

    /// <summary>The registered family a whole-family token names, or null where the token is not one — the single
    /// resolution of a family token.</summary>
    static SweepFamily? FamilyFor(string token)
    {
        foreach (var f in Registered) if (Token(f) == token) return f;
        return null;
    }

    static string Normalize(string? s) => (s ?? "").Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
}
