namespace HousecarlMcp;

/// <summary>The <c>asset_status</c> path bound as its own type, so a refusal call cannot take it and the path count in swapped order.</summary>
internal readonly record struct AssetPathBound(int Paths);
