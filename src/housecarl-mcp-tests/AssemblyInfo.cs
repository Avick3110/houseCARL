using Xunit;

// Parallelisation is off: CorpusRulebook.CorpusPath is a process-global every world repoints, so two live
// worlds read each other's corpus and the first Dispose deletes it under the survivor.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
