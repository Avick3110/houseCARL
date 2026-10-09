- **`housecarl_records` json and `to_file` rows no longer carry `cells`: every `fields` entry is `{path, value}`
  or `{path, note}`, so a consumer reads `f[path] = value`.** Grouping by element: `docs/architecture/json-wire.md`,
  "One leaf shape".
