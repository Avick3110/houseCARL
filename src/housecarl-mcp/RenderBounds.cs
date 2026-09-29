namespace HousecarlMcp;

/// <summary>The records render bounds in force on one service; a null comparison bound is derived from the shape's price.</summary>
internal sealed record RenderBounds(int Rows, int WholeRecordRows, int IdentityRows, int? ComparisonRows = null,
                                    double? ComparisonMillis = null)
{
    internal static readonly RenderBounds Default = new(RenderBudget.DefaultMaxRenderRows, RenderBudget.DefaultMaxWholeRecordRows,
                                                         RenderBudget.DefaultMaxIdentityRows);
}
