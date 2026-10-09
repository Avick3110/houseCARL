namespace HousecarlMcp;

/// <summary>The records render bounds in force on one service; a test lowers its own world's, and a null <see cref="ComparisonRows"/> takes the floor.</summary>
internal sealed record RenderBounds(int Rows, int WholeRecordRows, int? ComparisonRows = null)
{
    internal static readonly RenderBounds Default = new(RenderBudget.DefaultMaxRenderRows, RenderBudget.DefaultMaxWholeRecordRows);
}
