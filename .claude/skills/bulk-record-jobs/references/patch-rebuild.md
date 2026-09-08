# Recipe: the patch rebuild

"Re-derive an old compatibility patch against a new version of the mod it patches." A different job
from a catalogue: the trigger is a version bump, the input is an existing patch, and the success
criterion is that the still-valid deltas land on the new baseline and the dead ones do not.

The old patch is usually disabled, which is fine — `source=` and `versus=` read a plugin's version
wherever it lives, active in the order or sitting on disk unticked, and the response says which arm
resolved.

## 1. Survey the contest

```
housecarl_records(plugins={…the target mod…}, project={"form":"aggregate","group_by":"winner"},
                  counts_only=true)
```

One call says how much of the mod is contested and by whom, which is what decides whether this is a
twenty-record job or a five-hundred-record one.

## 2. Extract the old delta, per record

```
housecarl_records(formids=[…the contested set…], format="json",
                  project={"form":"delta", "fields":[…]},
                  source="OldPatch.esp", versus="TargetMod.esp")
```

`source=` is the subject of the comparison and `versus=` the reference pole; `versus=` is required
on the delta form. Naming `fields` scopes the comparison. Spill the result with `to_file=` when the
set is large — the delta form consumes every scan match, so the scan terms are the cost bound.

## 3. Forward the baseline, then edit the forwarded copy

`housecarl_forward` copies a named plugin's whole record verbatim; `housecarl_apply into=` the same
patch then edits that forwarded body rather than re-resolving the load-order winner. That pair is
the pinned stale-winner bypass, and it is the whole reason the rebuild works while an old winner
still sits on top. Where the contested winner is a plugin another tool regenerates, the never-copy
rule in the server's standing instructions decides `source=`: name the authored plugin.

## 4. Batch the re-application

One `housecarl_apply` call carries the whole op list:

- **Whole-field transplants**: an `ops[]` entry with `from_source="OldPatch.esp"` takes that field's
  value from the plugin you name instead of from the winner — off-order sources included.
- **List rebuilds**: `composes` on one op appends N elements or replaces the list wholesale;
  `composes=[]` clears it.
- A large job goes through a manifest: `ops="@<absolute path>"` reads the same array from a JSON
  file, so the ops are written once, dry-run from the file, then applied — and re-running the same
  manifest recovers an interrupted write, because overrides are idempotent.

Every call is all-or-nothing: one malformed op refuses the whole call with per-op reasons and
nothing is written.

## 5. Dry-run, then verify

`dry_run=true` runs the full real pipeline — winner resolve, schema pre-flight, every op applied in
memory, the reference-resolution check — and stops before anything touches disk. Run it on the
manifest before the real write; it returns exactly the refusal the real call would give.

Then verify the written file, before the patch is enabled:

```
housecarl_check(plugins=["MyRebuiltPatch.esp"])
```

`plugins=` resolves a name that is not in the active order on disk and sweeps it off-order, which is
the pre-enable check for a patch houseCARL just wrote. `readback=true` on the write returns the
written record bodies for the same reason. Both describe the written file, not load-order truth.
