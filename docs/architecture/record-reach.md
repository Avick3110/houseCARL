---
updated: 2026-11-18
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
  source= is refused. The records no reach term says no to become the scan's identity set, so an empty one answers 0
  matches; the scan's other terms run over them, the yes ones are the rows and the open ones are only counted. A
  group_by='type' count table takes a reach term as its bound. Field terms are parsed, and a types= naming none of the
  lines' types (read off the lines unjudged), a stale artifact, an unknown type and a wrong field path are refused,
  before the reach pass, so none waits on a line being judged.
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
  chance stated as written; a Death line likewise. An ExclusiveGroup sibling the NPC holds keeps the line off it; a
  keyword member is matched by its EditorID, so a keyword SPID creates counts. An NPC whose template is a leveled
  list, directly or past NPC templates, is not decidable for a string or NPC form filter it does not otherwise meet, since SPID also matches the NPC
  the list picks (a string only when it is, or for a partial one is part of, some NPC's EditorID, the only string a
  pick adds, and only where SPID sees NPC EditorIDs). An NPC already holding the line's form on its record (a spell, shout or
  leveled spell in its spell list, a perk, a faction, a keyword its own or its race's; `has_form`, `InsertKeyword`) is
  counted as already holding it, which SPID skips, and is open when that field is not decidable. The layer's earlier lines of the same kind, type and
  form run first: an NPC they reach is counted as already holding it, which SPID skips. Every line of the layer when
  the asked line is not in it; the asked line is found in the layer by its parse, in any case (KID's too). A keyword a KID Race line may give the NPC's race (judged by the KID reach over the
  races) is not decidable, since KID tags races before SPID reads them and the SPID reach does not replay KID. The
  races are judged, and the note names them, only when the line, an earlier line of its form, a feeder it runs or a
  group sibling reads one of the Race lines' keywords by an exact or partial string.
- **Template chain (Q2 b).** Each field an NPC filter reads is taken from the first NPC up its template chain whose
  flag for that field's category is clear; the field-to-category table is data (`reach-fields.json`, `npcTemplate`). A
  chain reaching a leveled actor, a template that does not resolve, or a loop makes that field not decidable.
- **SPID's Keyword pass (Q3 b).** Every kept regular Keyword line of the layer but those giving the asked line's own
  keyword is run over the NPC before the asked line, in layer order, repeated until nothing is added, at most 16
  passes, never taking a give back; a keyword it gives counts for later lines. A give the files cannot settle stays open with its reason, so a
  line reading it is not decidable or the running game's, the worse of the two. Only the lines whose keyword the asked
  line or an earlier line of its form can read (a string it names or holds part of, a group sibling, through each
  other) are run; the rest change
  none of its verdicts, and the note counts both.
- **KID** (BaseFilters.cpp, ItemData.cpp, Traits.h): chance first, as KID's fixed roll (`KidReach.Roll`: Xoshiro256**
  seeded with szudzik_pair(fnv1a_32(keyword EditorID), runtime FormID)), then traits per type, then filters: one `+`
  group whole, the plain list's exact and partial terms, `-` excluding. A filter form is the item itself, a keyword
  (held, given earlier, or the costliest effect's for a magic item, tome, enchanted armour or weapon), a location and its
  parents, a form list's members, or the item field `reach-fields.json` (`kidForms`) names for the form's type. A
  costliest-effect test is decided when every effect answers alike, else not decidable. An item already holding the
  keyword is skipped and counted. Trait H on a spell, scroll or enchantment is not decidable (Q4 a); a furniture `T()`
  trait, and an exact actor-value or archetype name that is not the item's EditorID or name, are not decidable.
- **KID's pass order.** The type's other keywords are run over the item first, each keyword's lines in file order, the
  first passing one giving it, in layer order, repeated until nothing is added, at most 16 passes, never taking a give
  back; the type's earlier lines of the asked keyword run too, and an item they tag is counted as already holding
  it. Magic Effect lines run over every magic effect first
  when a line reads keywords, so a costliest-effect test sees what they give. An ExclusiveGroup sibling held or given
  keeps the line off the item. As for SPID, only the lines whose keyword the asked line can read are run (all of them
  when it reads a form list).
- **The note.** One `reach:` note per term leads the scan's notes: the universe, the yes, not-decidable and
  running-game counts, the top reasons with counts and the first three records of each open bucket, that the rows are
  the yes records only, and the owed sentences (replay, earlier lines, chance, base records not actors, deleted
  records left out, a drop). When the call has any other term (types=, plugins=, formids=, a field term, another
  reach term, conflicts_only=, references=), the counts and examples are those within it, and the whole universe's
  counts follow; the already-held count too, the scan running its other terms over the held records without making
  them rows. A `formid in` or `editorid in` member whose record a reach term leaves open or held is no row, so it is
  named among the members that matched no record.

## Known limitations
- Only SPID's Keyword lines are replayed; a faction or perk an earlier SPID pass hands out does not count.
- Outfits, leveled lists, BOS and FLM are not followed (Q1 a).
- The Keyword-pass replay runs feeders in layer order, repeated until nothing is added (at most 16 passes), and never
  retracts a give, where SPID and KID sort lines by dependency first; a `-` keyword filter on a feeder placed before
  the keyword's giver can give where the game would not.
- KID partial keyword strings match keyword EditorIDs in any case.
- The template-category table and the CK category of each field are read off UESP, not the engine.
- A spid term ANDed with a kid term answers 0 with no sentence why (the two universes never overlap).
- A plugin that turns unreadable after the index build fails the reach call; a fresh call answers.
- A half-open level such as `5/` reads 0 for every NPC, but the note does not say SPID drops the line.
- A keyword group sibling is tested on every string channel, so an NPC whose name or EditorID equals a sibling
  keyword reads as holding it.
- A RACE or CLAS record that will not read is skipped, so the NPCs using it are judged as if it held nothing, a
  confident yes or no (KID: an MGEF, LCTN, ENCH or SPEL that will not read, likewise).
- A feeder is replayed without its own ExclusiveGroup: it gives even where the record holds one of its siblings.
- A SPID Package line's NPC that already runs the package is not counted already held (SPID skips it); NPC packages
  are not read.
- A SPID chance of 0 puts every selected NPC in the running-game bucket instead of no.
- A KID `R()` actor value is compared by Mutagen's enum index, so a game value of 53 or more, or -1, never matches.
- The note does not carry the replay's coverage caveats (an unread asset root, a BSA-only file, an EditorID-table
  gap), which the layer answer states.
- A plugin with a mid-file parse fault fails the whole reach call instead of being skipped and named.
- An earlier SPID line of the same kind and form counts as giving it first for every kind, though SPID adds Item
  counts per line, so a second Item line of the same item reaches NPCs this calls already held.
- KID's Magic Effect pass runs only when the asked line or a feeder reads keywords, not when only an earlier line does.
- An NPC whose name or EditorID equals a KID Race line's keyword reads as holding it, so its race tag is not left open.
- KID's Race lines are judged with only the Race lines as their layer, so a keyword another KID type creates is not seen.
- A SPID ExclusiveGroup member EditorID that no record holds is taken as a keyword SPID creates, whether or not a
  Keyword line gives it.
- A KID line and a layer copy of it differing by an unset (`NONE`, empty) or default traits or chance section are
  different lines, so the copy runs as an earlier line.
- A SPID line and a `Final` copy of it are one line to the earlier-line walk.
- A trait letter's case is ignored when finding the asked line in the layer.
- Whitespace KID keeps in a keyword, traits or chance section is ignored when finding the asked line in the layer.
- An NPC listed in a SPID Faction line's faction at rank -1 counts as already holding it.
- A keyword a KID Race line gives the NPC's race is not counted held for a SPID Keyword line handing it out.
- An NPC template chain through a deleted NPC ends there, so a leveled list past it is not seen.
- SkyPatcher's NPC and item keyword or faction adds are not replayed before the reach, and a record they touch is not
  marked open: SkyPatcher's replay works one record at a time, by copying it, so knowing which records its lines touch
  would mean replaying every NPC or item.

## Pinned by
- `RecordReachTests` — one test per pick (chain, leveled end, Keyword replay, chance upstream, KID H, no outfit
  following), each bucket, PC-level-mult, strings/traits/forms, KID pass order, KID trait, KID chance, both
  ExclusiveGroup rules, the held keyword, the refusals, the drop, composition with a field term, the scoped note,
  group_by='type', the parse-first refusal, created group keywords, the leveled pick, earlier lines, KID Race tags.
- `RecordReachArmTests` — one test per arm the first review found untested (the roll's known values, uninherited
  categories, the PC-level-mult range, feeder pruning, the costliest effect, the off-order refusal, the runtime
  FormID, multi-pass chains, sibling tests, two ANDed terms, the line refusals, the term split, the data load).

## Where
`ReachTerm.Split` (the where= split, verdicts, the note), `SpidReach.Evaluate`, `KidReach.Evaluate`, `ReachFields`
(the data); `RecordReads.Reach` (`RecordReach.cs`) opens the replay and reads the layers; `CrossQuery` intersects the
yes set; the tool text is `records`' where= parameter.

## Related
[`spid-layer.md`](spid-layer.md), [`kid-layer.md`](kid-layer.md) (the line splits and reads it reuses),
[`select-and-walk.md`](select-and-walk.md) (the where= grammar).
