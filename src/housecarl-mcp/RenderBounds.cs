namespace HousecarlMcp;

/// <summary>The four records render bounds in force on one service; production keeps <see cref="Default"/>, a test lowers its own world's.
/// A null <see cref="ComparisonRows"/> derives the comparison bound from the shape's price and <see cref="ComparisonMillis"/>,
/// which is <see cref="RenderBudget.ComparisonBudgetMillis"/> when null.</summary>
internal sealed record RenderBounds(int Rows, int WholeRecordRows, int IdentityRows, int? ComparisonRows = null,
                                    double? ComparisonMillis = null)
{
    internal static readonly RenderBounds Default = new(RenderBudget.DefaultMaxRenderRows, RenderBudget.DefaultMaxWholeRecordRows,
                                                         RenderBudget.DefaultMaxIdentityRows);
}
