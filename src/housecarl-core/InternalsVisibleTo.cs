using System.Runtime.CompilerServices;

// The proof harness (housecarl-generator) reaches into the extracted engines' INTERNAL helpers
// — WriteEngine.ResolveType/ConcreteOf/NavigateToConditionArm/EnumerateFlatGroups/TryCoerce/...,
// ReadEngine.ReadLeaf, and the WalkContext-driven byte proofs — exactly as it did when engine and
// harness shared one assembly. Declaring the harness a friend keeps every write-proof/read-proof
// oracle green across the §8.2 split WITHOUT widening this product core's PUBLIC surface to ~15
// test-only helpers. Standard product-lib + proof-assembly idiom. (MCP-server step §8.2, 2026-06-01.)
[assembly: InternalsVisibleTo("housecarl-generator")]
