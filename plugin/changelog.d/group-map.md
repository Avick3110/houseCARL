- **Record reads that gather cells together with their placed references now read each shared group once, so they
  are faster and lighter.** A `records` call over 800 Skyrim.esm cells and their references went from about 9 s to
  6 s on a busy machine.
