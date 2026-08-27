namespace HousecarlMcp;

/// <summary>
/// The lever names a REMEDY is allowed to name: the parameters the CALLING tool actually has.
///
/// A remedy sentence predicts what a LATER call produces ("narrow with fields=, lower depth="), so it is
/// only true for a caller that HAS those parameters. The body renderers are shared by both tool
/// generations — <c>housecarl_records</c> drives the same <see cref="Wire"/> / <see cref="JsonWire"/>
/// methods the 1.x read tools do — and a remedy composed inside a shared renderer can therefore only
/// speak one vocabulary. It spoke the 1.x one, so a <c>housecarl_records</c> caller was told to narrow
/// with <c>fields=</c> and lower <c>depth=</c>, which on that tool are <c>project.fields=</c> and
/// <c>project.depth=</c> (#439, the #343 class).
///
/// This carries the caller's spelling INTO the renderer. It changes WHO is named a lever, never which
/// levers a family offers: the divergences between remedy families are correct and stay (housecarl_resolve
/// has no fields=, housecarl_effect_chain has no offset=, and their leaner sentences are right — audited
/// on #439, settled decision #10). The one place the SET changes is a lever the calling tool does not
/// have at all: see <see cref="ConflictTree"/>.
///
/// <see cref="Legacy"/> is the default at every threaded seam, so a 1.x call site that passes nothing
/// renders exactly the bytes it rendered before.
/// </summary>
public sealed class LeverNames
{
    private LeverNames(string fields, string depth, string? conflictTree)
    {
        Fields = fields;
        Depth = depth;
        ConflictTree = conflictTree;
    }

    /// <summary>How this caller spells the field selector, including the '=' — "fields=" or "project.fields=".</summary>
    public string Fields { get; }

    /// <summary>How this caller spells the content-expansion knob — "depth=" or "project.depth=".</summary>
    public string Depth { get; }

    /// <summary>How this caller spells the conflict-tree toggle, or NULL when it has no such lever.
    /// housecarl_records does not: the tree is a PROJECT FORM there (project.form='tree'), a different
    /// call shape, not a way to slim a scan — so a remedy offering it as one would be inventing a
    /// parameter, which is the defect this type exists to stop. Null means "leave it unnamed".</summary>
    public string? ConflictTree { get; }

    /// <summary>The 1.x read tools' spelling. The DEFAULT everywhere, so nothing renders differently
    /// until a caller asks for its own vocabulary.</summary>
    public static readonly LeverNames Legacy = new("fields=", "depth=", "conflict_tree");

    /// <summary>housecarl_records: the selector and the expansion knob are form-scoped under project=,
    /// and there is no conflict_tree parameter at all.</summary>
    public static readonly LeverNames Records = new("project.fields=", "project.depth=", null);

    /// <summary>The two slimming levers named together, as the cross-query truncation notice names them.
    /// A caller with no conflict-tree lever gets the selector alone rather than a lever it cannot pass.</summary>
    public string FieldsOrTree => ConflictTree is null ? Fields : Fields + "/" + ConflictTree;

    /// <summary>The hint appended to a COLLAPSED container cell ("[list: 3 item(s)]"), naming the knob that
    /// expands it. Threaded as ReadEngine's containerHint, which already carries a per-call string —
    /// the core default names the 1.x spelling.</summary>
    public string ContainerHint => $" — pass {Depth}2 to expand";

    /// <summary>The dense render's container hint: dense refuses depth&gt;1 (positional cells align 1:1 with the
    /// requested paths — #231), so the plain hint would send the caller into that refusal blind. Names the
    /// format hop with the knob.</summary>
    public string DenseContainerHint => $" — pass {Depth}2 with format=text/json to expand (dense cells are positional)";
}
