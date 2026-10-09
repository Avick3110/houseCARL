- **`housecarl_skse findings=config` now reads the `{"id": …, "plugin": "…"}` forms in JSON configs such as IED's,
  each at its JSON path, instead of calling those files clean; PlayerRef reads OK, not DANGLING.** Check with
  `findings=config filter=IED`.
