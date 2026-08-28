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
/// have at all: see <see cref="SlimScan"/>, where the 1.x phrase names conflict_tree and the records
/// phrase cannot, because that tool has no such parameter in any spelling.
///
/// <see cref="Legacy"/> is the default at every threaded seam, so a 1.x call site that passes nothing
/// renders exactly the bytes it rendered before.
/// </summary>
public sealed class LeverNames
{
    private LeverNames(string fields, string depth, string winnerFields, string? slimScan, string batchSelection)
    {
        Fields = fields;
        Depth = depth;
        WinnerFields = winnerFields;
        SlimScan = slimScan;
        BatchSelection = batchSelection;
    }

    /// <summary>How this caller spells the field selector, including the '=' — "fields=" or "project.fields=".</summary>
    public string Fields { get; }

    /// <summary>How this caller spells the content-expansion knob — "depth=" or "project.depth=".</summary>
    public string Depth { get; }


    /// <summary>How this caller asks for the WINNER's field values on a scoped scan, as a complete token —
    /// "winner_fields=true" or "fields_source=\"winner\"". The two generations do not merely scope this one
    /// differently, they renamed it: housecarl_records refuses "winner_fields" by alias and points at
    /// fields_source="winner", so the P5 scoped-vs-winner note has to carry the caller's own token.</summary>
    public string WinnerFields { get; }

    /// <summary>The 1.x read tools' spelling. The DEFAULT everywhere, so nothing renders differently
    /// until a caller asks for its own vocabulary.</summary>
    public static readonly LeverNames Legacy = new("fields=", "depth=", "winner_fields=true", "fields=/conflict_tree", "request fewer formids");

    /// <summary>housecarl_records: the selector and the expansion knob are form-scoped under project=,
    /// and there is no conflict_tree parameter at all. This is the FORMIDS lane's vocabulary — a
    /// scan-derived body lane says its selection differently (<see cref="OnScanSelection"/>).</summary>
    public static readonly LeverNames Records = new("project.fields=", "project.depth=", "fields_source=\"winner\"", "project= (summary rows)", "request fewer formids");

    /// <summary>What a scan's truncation notice tells the caller to DROP to slim each row.
    ///
    /// Not simply the selector: this remedy has to name something the caller can actually drop and re-run.
    /// On the 1.x tools that is the two slimming levers together. On housecarl_records it is NOT
    /// <see cref="Fields"/> — the 'fields' form REQUIRES its field paths and refuses without them, so "drop
    /// project.fields=" names a next call the tool rejects (round 2 drove it). Dropping the whole project=
    /// construct is the actionable move: form defaults to 'summary', which is the slim render this remedy is
    /// pointing at anyway.
    ///
    /// NULL when the call passed no such construct at all, and the clause is then omitted rather than rendered
    /// over nothing: housecarl_records defaults to form='summary' and its off-order scan never passes a project
    /// block, so "drop project= (summary rows)" told those callers to drop what they had not written and promised
    /// them the rows they were already reading. See <see cref="WithNothingToDrop"/>.</summary>
    public string? SlimScan { get; }

    /// <summary>This vocabulary as spoken by a call that passed NOTHING to slim rows with. The scan truncation
    /// notice drops its "drop …" clause entirely and keeps the two levers that are real on such a call — a lower
    /// limit= and a higher max_chars.</summary>
    public LeverNames WithNothingToDrop() => new(Fields, Depth, WinnerFields, null, BatchSelection);

    /// <summary>What a BATCH's truncation notice tells the caller to do to put fewer records in the response.
    ///
    /// This is the one lever whose name depends on the SELECTION LANE rather than on the tool: a batch whose
    /// rows the caller named by hand is narrowed by naming fewer of them, and a batch whose rows a SCAN
    /// selected is narrowed by the scan's window. housecarl_records has both lanes — formids= reaches
    /// Wire.RenderBatch, and so do the scan-derived body lanes (form='everything', and the off-order scan) —
    /// so the caller's lane, not the caller's tool, decides the sentence. See <see cref="OnScanSelection"/>.</summary>
    public string BatchSelection { get; }

    /// <summary>This vocabulary as spoken on a SCAN-derived body lane: the rows came from a scan, so the lever
    /// that puts fewer of them in the response is limit=, not a formids list the caller never wrote. Every other
    /// lever is unchanged — the selection lane is a third axis, not a different tool.</summary>
    public LeverNames OnScanSelection() => new(Fields, Depth, WinnerFields, SlimScan, "lower limit=");

    /// <summary>The hint appended to a COLLAPSED container cell ("[list: 3 item(s)]"), naming the knob that
    /// expands it. Threaded as ReadEngine's containerHint, which already carries a per-call string —
    /// the core default names the 1.x spelling.</summary>
    public string ContainerHint => $" — pass {Depth}2 to expand";

    /// <summary>The dense render's container hint: dense refuses depth&gt;1 (positional cells align 1:1 with the
    /// requested paths — #231), so the plain hint would send the caller into that refusal blind. Names the
    /// format hop with the knob.</summary>
    public string DenseContainerHint => $" — pass {Depth}2 with format=text/json to expand (dense cells are positional)";
}
