namespace HousecarlMcp;

/// <summary>The lever names a remedy sentence may name: the parameters the calling tool actually has, carried in
/// because the body renderers are shared by both tool generations. <see cref="Legacy"/> is the default.</summary>
public sealed class LeverNames
{
    private LeverNames(string fields, string depth, string winnerFields, string? slimScan, string batchSelection,
                       bool hasFieldSelector = true)
    {
        Fields = fields;
        Depth = depth;
        WinnerFields = winnerFields;
        SlimScan = slimScan;
        BatchSelection = batchSelection;
        HasFieldSelector = hasFieldSelector;
    }

    /// <summary>How this caller spells the field selector, including the '=' — "fields=" or "project.fields=".</summary>
    public string Fields { get; }

    public string Depth { get; }

    /// <summary>How this caller asks for the winner's field values on a scoped scan, as a complete token.</summary>
    public string WinnerFields { get; }

    /// <summary>The 1.x read tools' spelling, and the default everywhere.</summary>
    public static readonly LeverNames Legacy = new("fields=", "depth=", "winner_fields=true", "fields=/conflict_tree", "request fewer formids");

    /// <summary>housecarl_records' formids lane: the selector and the expansion knob are form-scoped under project=.</summary>
    public static readonly LeverNames Records = new("project.fields=", "project.depth=", "fields_source=\"winner\"", "project= (summary rows)", "request fewer formids");

    /// <summary>What a scan's truncation notice tells the caller to drop to slim each row; null when the call passed
    /// nothing to drop, and the clause is then omitted.</summary>
    public string? SlimScan { get; }

    public LeverNames WithNothingToDrop() => new(Fields, Depth, WinnerFields, null, BatchSelection, HasFieldSelector);

    /// <summary>False when the form being rendered has no field selector, so a truncation notice does not name one.</summary>
    public bool HasFieldSelector { get; }

    public LeverNames WithoutFieldSelector() => new(Fields, Depth, WinnerFields, SlimScan, BatchSelection, false);

    /// <summary>What a batch's truncation notice tells the caller to do to put fewer records in the response; the one
    /// lever whose name depends on the selection lane rather than on the tool.</summary>
    public string BatchSelection { get; }

    /// <summary>This vocabulary on a scan-derived body lane, where the lever is limit=.</summary>
    public LeverNames OnScanSelection() => new(Fields, Depth, WinnerFields, SlimScan, "lower limit=", HasFieldSelector);

    /// <summary>The hint appended to a collapsed container cell, naming the knob that expands it.</summary>
    public string ContainerHint => $" — pass {Depth}2 to expand";

    /// <summary>The dense render's container hint, naming the format hop alongside the knob because dense refuses depth&gt;1.</summary>
    public string DenseContainerHint => $" — pass {Depth}2 with format=text/json to expand (dense cells are positional)";
}
