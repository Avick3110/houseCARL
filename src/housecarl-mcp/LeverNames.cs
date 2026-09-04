namespace HousecarlMcp;

/// <summary>
/// The lever names a remedy sentence is allowed to name: the parameters the calling tool actually has.
/// The body renderers are shared by both tool generations, so the caller's own spelling has to be carried
/// in — otherwise a <c>housecarl_records</c> caller is told to narrow with <c>fields=</c> when its parameter
/// is <c>project.fields=</c>. <see cref="Legacy"/> is the default at every threaded seam, so a 1.x call site
/// that passes nothing renders exactly the bytes it rendered before.
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
    /// "winner_fields=true" or "fields_source=\"winner\"". The two generations renamed it, not just rescoped it,
    /// so the scoped-vs-winner note must carry the caller's own token.</summary>
    public string WinnerFields { get; }

    /// <summary>The 1.x read tools' spelling. The DEFAULT everywhere, so nothing renders differently
    /// until a caller asks for its own vocabulary.</summary>
    public static readonly LeverNames Legacy = new("fields=", "depth=", "winner_fields=true", "fields=/conflict_tree", "request fewer formids");

    /// <summary>housecarl_records: the selector and the expansion knob are form-scoped under project=,
    /// and there is no conflict_tree parameter at all. This is the formids lane's vocabulary — a
    /// scan-derived body lane says its selection differently (<see cref="OnScanSelection"/>).</summary>
    public static readonly LeverNames Records = new("project.fields=", "project.depth=", "fields_source=\"winner\"", "project= (summary rows)", "request fewer formids");

    /// <summary>What a scan's truncation notice tells the caller to DROP to slim each row. It must name
    /// something the caller can actually drop and re-run — on housecarl_records that is the whole project=
    /// construct, not <see cref="Fields"/>, because the 'fields' form refuses without its field paths.
    /// NULL when the call passed no such construct at all, and the clause is then omitted rather than stated
    /// over nothing. See <see cref="WithNothingToDrop"/>.</summary>
    public string? SlimScan { get; }

    /// <summary>This vocabulary as spoken by a call that passed NOTHING to slim rows with. The scan truncation
    /// notice drops its "drop …" clause entirely and keeps the two levers that are real on such a call — a lower
    /// limit= and a higher max_chars.</summary>
    public LeverNames WithNothingToDrop() => new(Fields, Depth, WinnerFields, null, BatchSelection);

    /// <summary>What a batch's truncation notice tells the caller to do to put fewer records in the response.
    /// The one lever whose name depends on the SELECTION LANE rather than on the tool: hand-named rows are
    /// narrowed by naming fewer, scan-selected rows by the scan's window. See <see cref="OnScanSelection"/>.</summary>
    public string BatchSelection { get; }

    /// <summary>This vocabulary as spoken on a SCAN-derived body lane: the rows came from a scan, so the lever
    /// that puts fewer of them in the response is limit=, not a formids list the caller never wrote. Every other
    /// lever is unchanged — the selection lane is a third axis, not a different tool.</summary>
    public LeverNames OnScanSelection() => new(Fields, Depth, WinnerFields, SlimScan, "lower limit=");

    /// <summary>The hint appended to a collapsed container cell ("[list: 3 item(s)]"), naming the knob that
    /// expands it. Threaded as ReadEngine's containerHint.</summary>
    public string ContainerHint => $" — pass {Depth}2 to expand";

    /// <summary>The dense render's container hint: dense refuses depth&gt;1 (its cells align 1:1 with the
    /// requested paths), so the plain hint would send the caller into that refusal blind. Names the format
    /// hop alongside the knob.</summary>
    public string DenseContainerHint => $" — pass {Depth}2 with format=text/json to expand (dense cells are positional)";
}
