- **`has`, `has_any` and `has_none` now test a negative integer field by its bits, and refuse a digit string, a
  whole-number float or an unnamed non-`[Flags]` enum value, which they used to parse.** The refusal reads
  `needs a flags/bitmask or integer field, but 'X' is not one: it read '…'`; `MajorRecordFlagsRaw has 0x80000000` checks
  the fix.
