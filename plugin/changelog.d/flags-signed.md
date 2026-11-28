- **`has`, `has_any` and `has_none` now test a negative integer field by its bits instead of refusing it, so
  `MajorRecordFlagsRaw has 0x80000000` works on a record with bit 31 set.** On Skyrim.esm REFRs it gives 162, the same
  as `SkyrimMajorRecordFlags has 0x80000000`.
