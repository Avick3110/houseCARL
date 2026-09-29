namespace HousecarlMcp;

/// <summary>The four records render bounds in force on one service; production keeps <see cref="Default"/>, a test lowers its own world's.
/// A null <see cref="ComparisonRows"/> derives the comparison bound from the shape's measured row cost.</summary>
internal sealed record RenderBounds(int Rows, int WholeRecordRows, int IdentityRows, int? ComparisonRows = null)
{
    internal static readonly RenderBounds Default = new(RenderBudget.DefaultMaxRenderRows, RenderBudget.DefaultMaxWholeRecordRows,
                                                         RenderBudget.DefaultMaxIdentityRows);
}
