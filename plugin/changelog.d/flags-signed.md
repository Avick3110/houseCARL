- **`has`, `has_any` and `has_none` now bit-test a negative integer field, and refuse any field that is not a
  `[Flags]` enum or an integer: `needs a flags/bitmask or integer field, but 'X' is not one: it read '…'`.** On
  Skyrim.esm REFRs, `MajorRecordFlagsRaw has 0x80000000` matches 162, as `SkyrimMajorRecordFlags has 0x80000000` does.
