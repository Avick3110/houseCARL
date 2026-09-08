# Player-choice topic semantics

What the player sees, what the NPC speaks, and how to branch a reply on one click. **Read this before
composing a player menu.** A player-choice topic can be byte-perfect — every subrecord present, every
check green — and still play absurdly, because these rules govern *which text goes where*, and no
data-layer check can evaluate that. They cost a shipped dialogue mod a full rework. Nothing here
applies to a generic greeting or a goodbye; those have no menu.

## The two text fields swap speakers

- **`INFO.Prompt` is the player's menu line** — the button they click.
- **`INFO.Responses` is the NPC's reply to it** — *not* player-spoken text.

Put the prompt text into `Responses` and the NPC parrots the player's own words back at them. This is
the single most common way a player topic goes wrong, and it is invisible at the data layer.

The topic's own `Name` is a third string again: it is the menu label the *topic* carries, which is
what a `LinkTo` into this topic will show.

## `LinkTo` sets the next player options, not what the NPC says

The linked topic's `Name` becomes the next menu button. Wiring an NPC reply as a `LinkTo` target
produces a clickable option literally labelled with that topic's name ("Wench reply") — a classic
symptom of this mistake.

The NPC's reply belongs in the current INFO's `Responses`. **One INFO can hold several
`DialogResponse` rows, and that is the multi-line-speech idiom**: the rows play sequentially and
automatically. Do not split a speech across sibling INFOs — only the first eligible INFO in a topic
plays, so everything after the first is dropped. Sibling INFOs in one topic are for stage or
condition *variants*, not for consecutive lines.

`LinkTo` is a list FormLink; it takes a `XXXXXX:Plugin.esp` FormID per target.

## The ender needs the `Goodbye` flag

**A conversation ender needs `Flags.Flags = Goodbye` on the INFO.** Without it the menu reopens after
every reply and the player can never leave the exchange. The create path materializes the `Flags`
struct for Creation-Kit parity but leaves `Goodbye` unset — it is an authoring semantic, not a
default. Set it on the INFO that ends the exchange, and on that one only.

## Keep the topic's conditions mutually exclusive

Only the first condition-passing INFO in a topic plays. When two INFOs can both pass, which one wins
falls back to list order — a fragile thing to lean on, and the thing that moves when another plugin
touches the topic. Mutually exclusive `Conditions` make the outcome order-independent, and that is
*why* houseCARL output needs no Creation-Kit ordering pass.

## Branching an NPC's reply on a single click

A single player click evaluates every candidate INFO's `Conditions` **once**, at the moment the
option is chosen. So an INFO's result-script fragment cannot roll a random or branching outcome and
*then* have sibling INFOs condition on it — their conditions were already tested. Nothing about the
records is wrong; the ordering of evaluation simply makes the pattern impossible.

The clean pattern, proven in a shipped mod: **pre-roll the outcome before the click.**

1. Register the deciding actor's script for the dialogue menu opening —
   `RegisterForMenu("Dialogue Menu")`.
2. When the menu opens, write the outcome into globals.
3. Let one topic hold every outcome INFO, each condition-disambiguated on those globals, each a
   complete exchange.

The click then just selects the INFO the pre-roll already satisfied. The outcome INFOs are ordinary
condition-disambiguated siblings, so the mutual-exclusion rule above is what keeps them honest.

## Reachability

A `Custom` topic is only entered through a `DialogBranch` whose `StartingTopic` names it, or through
a `LinkTo` from a topic that is itself reachable. A player menu with neither is byte-valid and never
appears. Only generic subtypes (Hello, Goodbye and the rest) are matched without an entry point —
see `references/dialogue-branch.md` for the DLBR's fields and its three flags.
