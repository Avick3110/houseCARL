---
updated: 2026-11-17
covers: [src/housecarl-core/ReachTerm.cs, src/housecarl-core/ReachFields.cs, src/housecarl-core/reach-fields.json, src/housecarl-core/SpidReach.cs, src/housecarl-core/KidReach.cs, src/housecarl-mcp/RecordReach.cs]
---
# The reach term

## What it is
`records where=["spid reaches <line>"]` and `["kid reaches <line>"]` select the records a SPID or KID line reaches on
the live order, judged by the format's own rules (SPID 7.3.0/7.3.3, KID 4.0.6). Each record is yes, no, not decidable
from the files, or decided only in the running game; the rows are the yes records, the other two counted apart.

## Contracts
- **The term.** A where= entry starting `spid` or `kid` (any case), whitespace, `reaches`, whitespace, is a reach term;
  the rest of the entry is the line, taken whole. Any other entry is a field predicate as before. Reach terms AND with
  each other and with field terms, formids= and plugins= like every where= term.
- **Bounds.** A reach term bounds the scan itself: SPID's universe is every NPC_ winner but the player and deleted
  records, KID's every winner of the line's type section. A types= naming none of those types is refused; an off-order
  source= is refused. The yes set becomes the scan's identity set, so an empty one answers 0 matches.
- **One read per format.** The line is parsed by the layer's own split (`SpidLines.Parse`, `KidLines.Parse`) and read with
  every line of the format's files in one `ReadAll`, in one replay context over the scan's own view, so the EditorID
  table walks every type once. A line the format skips, a Linked line and an ExclusiveGroup line are refused naming why;
  a line the format drops answers 0 with the drop's reason. Nothing is kept between calls.
- **SPID** (LookupFilters.cpp, LookupNPC.cpp): strings ALL (`+`, flattened), NOT, MATCH, ANY (`*`) over the NPC's and
  its race's keyword EditorIDs, its name, its own and its template's EditorIDs (NPC EditorIDs only with po3 Tweaks);
  forms ALL, NOT, MATCH by `has_form` per form type, a form list by its members, a plugin term by the NPC's or its
  template's plugin; actor level (a PC-level-mult NPC by its CalcMin/CalcMax interval: inside yes, outside no, else not
  decidable), skill levels (an autocalc NPC not decidable), skill weights off its class; traits F/M, U, S, C off the
  record, L and D the running game, T yes when in PotentialFollowerFaction else the running game; location and
  placed-actor filters the running game; chance below 100 puts every selected NPC in the running-game bucket, the
  chance stated as written; a Death line likewise. An ExclusiveGroup sibling the NPC holds keeps the line off it.
- **Template chain (Q2 b).** Each field an NPC filter reads is taken from the first NPC up its template chain whose
  flag for that field's category is clear; the field-to-category table is data (`reach-fields.json`, `npcTemplate`). A
  chain reaching a leveled actor, a template that does not resolve, or a loop makes that field not decidable.
- **SPID's Keyword pass (Q3 b).** Every kept regular Keyword line of the layer but those giving the asked line's own
  keyword is run over the NPC before the asked line, repeated until nothing is added (the dependency order where one
  exists); a keyword it gives counts for later lines. A give the files cannot settle stays open with its reason, so a
  line reading it is not decidable or the running game's, the worse of the two.
- **KID** (BaseFilters.cpp, ItemData.cpp, Traits.h): chance first, as KID's fixed roll (`KidReach.Roll`: Xoshiro256**
  seeded with szudzik_pair(fnv1a_32(keyword EditorID), runtime FormID)), then traits per type, then filters: one `+`
  group whole, the plain list's exact and partial terms, `-` excluding. A filter form is the item itself, a keyword
  (held, given earlier, or the costliest effect's for a magic item, tome, enchanted armour or weapon), a location and its
  parents, a form list's members, or the item field `reach-fields.json` (`kidForms`) names for the form's type. A
  costliest-effect test is decided when every effect answers alike, else not decidable. An item already holding the
  keyword is skipped and counted. Trait H on a spell, scroll or enchantment is not decidable (Q4 a); a furniture `T()`
  trait, and an exact actor-value or archetype name that is not the item's EditorID or name, are not decidable.
- **KID's pass order.** The type's other keywords are run over the item first, each keyword's lines in file order, the
  first passing one giving it, repeated until nothing is added; Magic Effect lines run over every magic effect first
  when a line reads keywords, so a costliest-effect test sees what they give. An ExclusiveGroup sibling held or given
  keeps the line off the item.
- **The note.** One `reach:` note per term leads the scan's notes: the universe, the yes, not-decidable and
  running-game counts, the top reasons with counts and the first three records of each open bucket, that the rows are
  the yes records only, and the owed sentences (replay, chance, base records not actors, a drop).

## Known limitations
- Only SPID's Keyword lines are replayed; a faction or perk an earlier SPID pass hands out does not count.
- Outfits, leveled lists, BOS and FLM are not followed (Q1 a).
- An ExclusiveGroup member is taken by its resolved form; SPID's and KID's tie rules where a cycle or partial string
  breaks the dependency order are not modelled (the repeat-until-stable pass gives every give).
- KID partial keyword strings match keyword EditorIDs in any case.
- The template-category table and the CK category of each field are read off UESP, not the engine.

## Pinned by
- `RecordReachTests` — one test per pick (chain, leveled end, Keyword replay, chance upstream, KID H, no outfit
  following), each bucket, PC-level-mult, strings/traits/forms, KID pass order, KID trait, KID chance, both
  ExclusiveGroup rules, the held keyword, the refusals, the drop, and composition with a field term.

## Where
`ReachTerm.Split` (the where= split, verdicts, the note), `SpidReach.Evaluate`, `KidReach.Evaluate`, `ReachFields`
(the data); `RecordReads.Reach` (`RecordReach.cs`) opens the replay and reads the layers; `CrossQuery` intersects the
yes set; the tool text is `records`' where= parameter.

## Related
[`spid-layer.md`](spid-layer.md), [`kid-layer.md`](kid-layer.md) (the line splits and reads it reuses),
[`select-and-walk.md`](select-and-walk.md) (the where= grammar).
