# Write-side recipes for existing dialogue

Three repeatedly-needed edits to dialogue that already exists. **Read this when the job is an edit
rather than an authoring pass** — cloning a verified condition gate onto many lines, writing an INFO
subtype the Creation Kit will not offer, or un-binding a result script. Each has one sharp edge, and
each is stated here once.

All three are ordinary `housecarl_apply` calls. Every edit lands in a reviewable patch by default;
the in-place lane is `in_place="<plugin>.esp"` with `acknowledge=true` on the first write to that
plugin.

## Recipe A — clone a verified condition gate onto N lines

**Never hand-synthesize the operator bytes.** A `Condition` (CTDA) is a polymorphic struct: a
`ConditionFloat` carrying a `CompareOperator`, a `ComparisonValue`, and a polymorphic `Data` holding
the function and its parameters. Computing that encoded operator and comparison by hand is exactly
what once wrote 26 broken conditions onto one gate. So read a known-good gate and copy it.

1. **Build the gate once** — in the Creation Kit, or on one INFO you have validated — and read it
   back: `housecarl_records(formids=["<source>"], project={"form":"fields","fields":["Conditions"],"depth":4})`.
   Note that `depth` is a sub-parameter *inside* the form; passing it beside the form is refused, and
   the `delta` form does not take it at all.
2. **Read each target first, so you know what you are about to overwrite.** Do not skip the targets
   that already carry `Conditions` — a broken hand-synthesized gate is exactly what this recipe
   exists to replace, and skipping those repairs none of them.
3. **Copy the field with the zip**, many pairs in one call. `bundle=` names the paths copied for
   every pair; `assignments=` pairs each target with its own source. Only what `bundle` names is
   copied — identity and every other field are untouched by construction:

   ```json
   housecarl_apply(
     into="MyPatch.esp", readback=true,
     bundle=["Conditions"],
     assignments=[
       { "target": "0A12C4:MyMod.esp", "from": "0B77E0:Skyrim.esm" },
       { "target": "0A12C5:MyMod.esp", "from": "0B77E0:Skyrim.esm" } ])
   ```

   The copy **replaces** the target's `Conditions` with the source's — `CopyFrom` is the only verb the
   zip issues, and there is no merge variant. That is why step 2 says to read each target first.
   `target` and `from` must be the same record type — INFO to INFO.

Nothing is computed anywhere in this recipe, which is the whole point. Confirm the written rows
against the source with the read-back before enabling the patch. A conditions-only edit does not
disturb the `.seq`.

## Recipe B — write an INFO subtype the CK's dropdown refuses to offer

The Creation Kit's player-dialogue subtype dropdown only lists subtypes already present in the
branch, so you cannot pick `ForceGreet` there.

**The subtype lives on the topic, not the line.** It is `DialogTopic.Subtype` on the DIAL;
`DialogResponses` — the INFO — has no `Subtype` field at all. Copy the exact value from a known-good
topic of that kind and write it:

```json
housecarl_apply(ops=[{ "formid": "0B77E0:MyMod.esp",
                       "field_path": "Subtype", "value": "ForceGreet" }])
```

`ForceGreet` is Mutagen's spelling of xEdit's `PFGT` subtype; the enum's legal values are in
`housecarl:mutagen-reference`. Setting `Subtype` on the create path derives the SNAM marker with it —
a topic carrying a `Subtype` and a blank marker is a load CTD, so if you set the subtype on an
existing topic outside the create path, check the marker went with it.

## Recipe C — un-bind a result-script fragment from an INFO

Clearing a fragment binding is a supported `Remove`; it needs no drop-and-recreate. To null the whole
result-script adapter — every script and fragment on the INFO:

```json
housecarl_apply(ops=[{ "formid": "0A12C4:MyMod.esp",
                       "field_path": "VirtualMachineAdapter", "op": "Remove" }])
```

To drop only the fragment binding while keeping any attached scripts, `Remove` the fragment field
itself: `field_path="VirtualMachineAdapter.ScriptFragments"`. Which of the two you want is the whole
decision here — the first takes the scripts with it. Read back to confirm the binding is gone before
enabling the patch.
