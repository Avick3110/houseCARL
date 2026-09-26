namespace HousecarlMcp;

/// <summary>The four records render bounds in force on one service; production keeps <see cref="Default"/>, a test lowers its own world's.</summary>
internal sealed record RenderBounds(int Rows, int WholeRecordRows, int IdentityRows, int ComparisonRows)
{
    internal static readonly RenderBounds Default = new(RenderBudget.DefaultMaxRenderRows, RenderBudget.DefaultMaxWholeRecordRows,
                                                         RenderBudget.DefaultMaxIdentityRows, RenderBudget.DefaultMaxComparisonRows);
}
