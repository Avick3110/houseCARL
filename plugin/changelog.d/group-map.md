- **Record reads that gather cells together with their placed references now read each shared group once, so they
  are faster and lighter.** To check, read a few hundred Skyrim.esm cells and their references with `formids=` and
  compare the time against the previous version.
