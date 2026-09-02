using Xunit;

// Test parallelisation is OFF, and the reason is a product property, not a test one:
// `CorpusRulebook.CorpusPath` is a process-global mutable static that every world must point at its own
// generated corpus. Two worlds alive at once — which is exactly what xUnit's default cross-collection
// parallelism produces — have one of them reading the other's corpus, and the first Dispose deletes it
// out from under the survivor. Measured, not assumed: it turned 126 green tests into 30 failures the
// moment a second world appeared.
//
// This is harness-plan debt 7 ("fixed temp dirs make concurrent probe runs mutually destructive") in its
// real form: the probes were never parallel, so the static was never a problem. A standard runner wants
// parallelism by default, and it will keep wanting it. Retiring the static is W7 work the conversion
// surfaces rather than solves.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
