# Skyrim voice file naming (.fuz / .lip)

Skyrim resolves a spoken dialogue line's audio by **filesystem convention**, never from a plugin field —
there is no `Voice` / `Wav` / `Lip` / `FileName` field on INFO / `DialogResponse` anywhere in the Mutagen
corpus. So a byte-valid INFO with no `.fuz` on disk plays **silent**, and nothing in the record points at
the cause. This is why voice is a presence check, not a field write.

houseCARL computes the expected path and checks it for you: `housecarl_create_record` reports a
**WILL BE SILENT** note per voiced response line at create time, and `housecarl_validate_dialogue` re-runs
that check over an existing topic. This reference exists so you can explain the note, audit a path by hand,
and avoid the override trap.

## The path template (authoritative — xEdit `InfoFileName`)

```
Data\Sound\Voice\<Plugin.esp>\<VoiceType>\<QuestEDID[0..9]>_<TopicEDID[0..14]>_<8-hex FormID, high byte 00, UPPERCASE>_<responseNum>.fuz
```

…plus a matching `.lip` at the same name. The generator is xEdit's `Export dialogues.pas`:
`IntToHex(InfoFormID and $FFFFFF, 8)`.

## Load-order-independent — no runtime-FormID bridge

The FormID segment is `InfoFormID and $FFFFFF` — the `and $FFFFFF` **masks off the load-order index** to a
literal `00` high byte, leaving the 6 plugin-local digits. That is exactly the plugin-local FormID
houseCARL already holds, so the expected path is computable at author time with **no runtime FormID
resolution**. (The SEQ format shares this property — see `seq-file-format.md`.) An early worry that the
voice check needed a runtime-FormID bridge was wrong; it does not.

## Four inputs the path needs (not just the FormID)

1. **Quest EDID** + **Topic / DIAL EDID** — combined cap ~25 chars (`QuestEDID[0..9]` + `TopicEDID[0..14]`).
2. The **8-hex `00` + plugin-local FormID**, uppercase.
3. **One presence check per response line** — `_1`, `_2`, … — because one INFO can hold several spoken rows.
   A single `_1` check is wrong for a multi-row INFO.
4. **`<VoiceType>`** — comes from the **speaker** (the NPC's or quest-alias's voice type), not the INFO.
   It is an author-time-knowable input, but it is a real input to resolve. houseCARL derives the folder name
   from the VoiceType's **EditorID**; that the on-disk folder equals the VoiceType EditorID is the one
   unverified assumption in the path — if a mod ships its audio under a folder that differs from the VoiceType
   EditorID, the computed path is wrong, so check that first when a path doesn't match.

## Two traps

1. **The folder is the plugin that DEFINES the INFO.** For create-into-a-new-plugin that's your new plugin
   (clean). For an **override / in-place edit** of an existing INFO it's the *winner's* plugin and the audio
   lives with the original — the one place voice brushes the override question. Do not compute the folder
   from the conflict winner when the line is defined elsewhere.
2. **Voice breaks on a FormID-VALUE change, not load-order reordering.** ESL compaction / merges renumber
   the low digits → the path moves → orphaned audio (whole tools like VoiceEslify exist for this). The
   create path is immune (it assigns stable `0x800+` locals), but flag it if a user is compacting/merging a
   plugin whose lines already have audio.

## Scope boundary

houseCARL checks **presence** — whether the `.fuz` (and `.lip`) exists at the computed path. It does **not**
verify lip-sync accuracy or the audio content, and **voice acting / audio generation is out of scope**
entirely. When a line will be silent, the honest handoff is: "this line has no audio at `<path>` — record
or provide the `.fuz`/`.lip` yourself (voice acting is out of scope)." A clean presence check is necessary
but never sufficient for "this line will be heard correctly."
