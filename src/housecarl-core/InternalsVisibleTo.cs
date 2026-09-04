using System.Runtime.CompilerServices;

// The proof harness (housecarl-generator) reaches into the engines' INTERNAL helpers —
// WriteEngine.ResolveType/ConcreteOf/NavigateToConditionArm/EnumerateFlatGroups/TryCoerce/...,
// ReadEngine.ReadLeaf, and the WalkContext-driven byte proofs. Declaring it a friend keeps those
// checks working without widening this core's PUBLIC surface to ~15 test-only helpers.
[assembly: InternalsVisibleTo("housecarl-generator")]

// The xUnit test project drives the same service/tool seams.
[assembly: InternalsVisibleTo("housecarl-mcp-tests")]
