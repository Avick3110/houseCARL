using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// SELF-CONTAINED CI REGRESSION GUARD for nested-record CREATE (nested/dialogue plan, Layer A), in the pattern of
/// upsert-guard / formid-floor-guard. Drives the REAL product path (WritePatchBuilder.CreateRecords) against a
/// SYNTHESIZED master in TEMP — NO Skyrim.esm, so it runs in CI (unlike the manual nested-create-proof, which samples
/// fixtures from vanilla Skyrim.esm). The synthesized master carries the three fixture shapes the mechanism nests
/// under: a Weapon (the can't-contain-a-child reject target), a DialogTopic (the unique-collection happy path), and an
/// interior Cell (the named-collection happy path + the ambiguous-collection reject).
/// Run: dotnet run --project src/housecarl-generator -- nested-create-guard
///
/// Arms (ALL required — a GREEN must mean "the contract holds", never "the scenario doesn't arise here"). They mirror
/// the manual proof's N1-N9:
///   ONESHOT   (N1) — topic + its first INFO created in ONE call (same-call sibling parent): both records allocated at
///                    the local 0x800+ floor, the INFO present in the topic's Responses on disk, distinct FormKeys.
///   MULTICHILD(N8) — topic + TWO INFOs in one call, the second carrying a field edit: all three created, both INFOs
///                    under the topic, the edited Prompt landed on the second.
///   INTOTOPIC (N2) — an INFO added to an EXISTING (master) topic by FormKey: the new INFO at the floor, present in
///                    the topic override's Responses (outcome (i), the unique collection derived by type).
///   INTOCELL  (N3) — a PlacedObject added to an EXISTING (master) cell, the collection named 'Persistent' (outcome
///                    (ii)): the new ref at the floor, present in the cell override's Persistent list.
///   REJ-NOPARENT(N4) — a nested type with NO parent refuses loud (the type isn't flat-createable), no file written.
///   REJ-BADPARENT(N5)— an INFO under a Weapon refuses loud ('cannot be created under' — the containment boundary),
///                    no file written.
///   REJ-AMBIG (N6) — a PlacedObject into a Cell with NO collection named refuses loud naming the candidate lists
///                    ('more than one' + 'Persistent') — the outcome-(ii) discriminator is required, never guessed.
///   REJ-FWDSIB(N7) — a child whose same-call sibling parent is declared LATER refuses loud ('earlier in this call').
///   EXTEND (was N9) — a parent created in a PRIOR into= call IS now resolvable (the N9 extend-gap fix): create a topic
///                    (call 1), then in a SECOND into= call add an INFO under it by FormKey — the INFO lands under the
///                    patch-carried topic. A GENUINELY-absent parent (in neither the load order nor the patch) still
///                    refuses loud, naming both.
///
/// Layer B unit A — same-call sibling reference (@editorid) as a FormLink VALUE (the PNAM order-chain + Topic back-link
/// in ONE bulk_create):
///   SIBREF        — topic + 2 lines; line 1's Topic=@topic and line 2's PreviousDialog=@line1 BOTH resolve to the
///                   right same-call 0x800+ FormKeys on disk (the keystone).
///   REJ-SIBFWD    — a field @ref to a sibling declared LATER refuses loud ('EARLIER in this call' — the declared-earlier rule).
///   REJ-SIBNONFL  — an @ref on a NON-FormLink field (FavorLevel) refuses loud ('only valid on a FormLink field' — the
///                   string-collision guard that keeps Phase-3 substitution scoped to formlinks).
///   REJ-SIBLIST   — an @ref inside a COLLECTION value (a list ReplaceAll's Values) refuses loud ('only supported as a
///                   single Set value') — the create path resolves only the singular Value, so a list/dict token must
///                   fail at the gate, not accept-then-throw at apply (Q3).
///   REJ-SIBDICT   — the dict half of that gate: an @ref inside a dict Merge's Entries values refuses loud the same way.
///   REJ-SIBAPPLY  — an @ref validated with a NULL sibling set (the Apply/set_field context) refuses at the rulebook —
///                   create-only scoping, so @editorid never becomes an accept-then-substitute-nothing hole (Q3).
///
/// GENERAL FormLink-ELEMENT collection value-shape — the BROADER pre-existing gap REJ-SIBLIST/REJ-SIBDICT named: ANY
/// malformed FormLink ELEMENT (not just an @editorid sibling token) in a collection value was accepted at pre-flight
/// then threw "Malformed FormKey string" at apply (a Q3 accept-then-throw). The gate now validates each element with
/// the SAME recognizer the singular formlink Set uses (DialogResponses.LinkTo = List&lt;FormLink&lt;DialogTopic&gt;&gt;):
///   FLELEM-REJ-GATE    — a malformed element in a ReplaceAll (req.Values), PAST a valid one, refuses at PRE-FLIGHT and
///                        NAMES the bad element ('Illegal FormLink element …' — per-element, the rulebook driven directly).
///   FLELEM-REJ-ADD     — the req.Value slot too: a malformed Add value refuses (Add/SetAtIndex carry the element there).
///   FLELEM-NULLCLEAR-OK— a null-clear synonym ('00000000') is a LEGAL element (shares IsValidFormLinkValue with the
///                        singular path) — the gate doesn't over-reject the clear shape.
///   FLELEM-OK-E2E      — a VALID FormID element round-trips through the REAL create+apply (accepted AND written to disk).
///   FLELEM-REJ-E2E     — a malformed element refuses end-to-end with NO file written; the PRE-FLIGHT message (not the
///                        apply throw) proves it was the gate. (The dict half — Merge/ReplaceAll on a formlink-VALUED
///                        dict — is dormant-by-construction: no such field is modeled in the corpus today, see ValueLegality.)
///
/// ELEMENT-VALUE PRESENCE — the null-PRESENCE twin of the value-SHAPE gap above (PR #76 follow-up). Add/SetAtIndex on a
/// coercible-element collection set the new element by coercing the singular req.Value; a MISSING value (req.Value null,
/// no compose) used to slip pre-flight — the formlink step-4a check uses `is { } ev`, which SKIPS a null slot — then
/// Coerce(null) yielded a null element that threw a NullReferenceException at SERIALIZE (the misleading "compose the Data
/// arm" message). The value-presence gate refuses it loud, mirroring the singular Set "requires a value":
///   FLELEM-REJ-NULLADD       — a null req.Value on a formlink-list Add refuses at PRE-FLIGHT (RED before: accepted, null).
///   FLELEM-REJ-NULLADD-PLAIN — the SAME gate fires for a NON-formlink coercible list (Race.MovementTypeNames =
///                              List&lt;String&gt;) — proves it's gated UNIFORMLY by element KIND, not formlink-ness (by construction).
///   FLELEM-REJ-NULLSETIDX    — a compose supplied with NO value on a coercible SetAtIndex still refuses (PR #77 review
///                              finding 1): SetAtIndex ignores req.Struct, so the gate carries NO req.Struct guard.
///   FLELEM-REJ-NULLADD-E2E   — a null-value Add refuses end-to-end with NO file written (the gate, not the serialize NRE).
///
/// KEY / INDEX PRESENCE — the missing-addressing-key twin of the value-presence gap above (PR #77 follow-up). A dict
/// Add/Remove coerces req.Key into / against the entry; a list SetAtIndex parses req.Key as the index. A MISSING
/// key/index slipped pre-flight (VerbLegality required a key only for Set-on-dict) and threw UNNAMED at apply
/// (Coerce(null) / int.Parse(null)). VerbLegality now requires it up front, by construction:
///   KEYIDX-REJ-DICTADD    — a dict Add with no key refuses at PRE-FLIGHT (Class.SkillWeights=Dictionary&lt;Skill,Byte&gt;;
///                           a valid value is supplied so ONLY the missing key differs). RED before: accepted (null).
///   KEYIDX-REJ-DICTREMOVE — a dict Remove with no key refuses (it identifies the entry BY key). RED before: accepted.
///   KEYIDX-REJ-SETIDX     — a list SetAtIndex with no index refuses (Race.MovementTypeNames=List&lt;String&gt;). RED before: accepted.
///   KEYIDX-OK-LISTREMOVE  — a keyless list Remove + value is STILL accepted: list Remove is by-index-OR-by-value, so the
///                           DICT-only scope does not over-reach to lists (no-over-reject, like FLELEM-NULLCLEAR-OK).
///   KEYIDX-REJ-SETIDX-E2E — a keyless SetAtIndex refuses end-to-end with NO file written; the PRE-FLIGHT message (not the
///                           apply int.Parse(null) throw) proves the gate.
///
/// KEY / INDEX VALUE-SHAPE — the malformed-addressing-key twin of the PRESENCE gap above (this PR). PRESENCE (above)
/// catches a MISSING key/index; this catches a PRESENT-but-MALFORMED one. A dict Set/Add/Remove coerces req.Key into the
/// entry and Merge/ReplaceAll coerce each Entries key (ApplyDictVerb -> Coerce(key, KeyType)); a list SetAtIndex/Remove
/// parses req.Key as the index (ApplyListVerb -> int.Parse(req.Key!)). A malformed key/index slipped pre-flight (only dict
/// SET key-shape was gated, and only ENUM keys by catalog-NAME) and threw UNNAMED at apply. ValueLegality now gates the
/// key/index SHAPE by construction: dict keys via the SAME coercibility the apply path uses (the key's real CLR type
/// resolved from the field's dict AQ — EVERY key kind, not just enums); list indices via IsValidListIndexValue (parseable
/// non-negative int, the in-range check left to apply, Q3):
///   KEYSHAPE-REJ-DICTADD       — a dict Add with a non-coercible enum key refuses at PRE-FLIGHT (Class.SkillWeights; a
///                                valid value supplied so ONLY the key differs). RED before: accepted (no Add key-shape gate).
///   KEYSHAPE-REJ-DICTREMOVE    — a dict Remove with a non-coercible key refuses (it identifies the entry BY key). RED: accepted.
///   KEYSHAPE-REJ-MERGEKEY      — a Merge with a non-coercible Entries KEY refuses (Merge/ReplaceAll keys coerce too). RED: accepted.
///   KEYSHAPE-REJ-SBYTE         — a Remove on the ONE non-enum-keyed dict (Package.Data = Dictionary&lt;sbyte,APackageData&gt;)
///                                with a non-numeric key refuses 'does not coerce to sbyte' — proves the gate is by the key's
///                                real CLR type (every kind), not enum-catalog-name only. RED before: accepted (the sbyte hole).
///   KEYSHAPE-REJ-SETIDX        — a list SetAtIndex with a non-integer index refuses (Race.MovementTypeNames). RED: accepted.
///   KEYSHAPE-REJ-NEGIDX        — a list SetAtIndex with a NEGATIVE index refuses too: int.Parse accepts '-1' but the indexer
///                                throws, so the gate pre-checks &gt;= 0 (the &gt;=0 decision). RED before: accepted.
///   KEYSHAPE-REJ-LISTREMOVE-IDX— a list Remove with a present non-integer index refuses (the RemoveAt path int.Parse too). RED: accepted.
///   KEYSHAPE-OK-DICTADD        — a dict Add with a VALID enum key + value is STILL accepted (no over-reject). Accepted before AND after.
///   KEYSHAPE-OK-SETIDX         — a list SetAtIndex with a VALID index is STILL accepted (no over-reject). Accepted before AND after.
///   KEYSHAPE-OK-NUMENUM-SET    — a dict Set with a NUMERIC enum key ('3', which apply accepts via Enum.Parse) is accepted: the
///                                gate matches apply, no longer over-rejecting numeric enum keys (the reconciled Set path).
///                                RED before: REJECTED by the old enum-catalog-NAME-only check (the gate/apply drift this fixes).
///   KEYSHAPE-REJ-E2E           — a malformed list index refuses end-to-end with NO file written; the PRE-FLIGHT message
///                                ('Illegal list index'), not the apply int.Parse throw, proves the gate.
///
/// GAP 1 — mid-path dict-key VALUE-SHAPE (the one-segment-up twin of the leaf KEYSHAPE-REJ-SBYTE arm). The leaf step-4-key
/// block (PR #79) gates a dict key at the LEAF; ValidateFromType's bracketed MID-PATH hop ('Data[key].field') checked the
/// key via CheckValue WITHOUT the key's AQ, so it fell to the enum-catalog-by-name fallback and missed the lone non-enum
/// key (Package.Data = Dictionary&lt;sbyte,APackageData&gt;, the only mid-path-navigable dict). A malformed mid-path key
/// was accepted then threw FormatException at apply (StepIntoElement -&gt; Coerce(key, sbyte)). The fix passes
/// DictKeyType(field)?.AQ — the SAME recognizer pair the leaf block uses — so mid-path and leaf can't drift:
///   GAP1-REJ-MIDKEY-SBYTE      — a malformed sbyte key in a mid-path hop ('Data[notasbyte].Name') refuses 'does not coerce
///                                to sbyte' at PRE-FLIGHT (RED before: accepted — no AQ, 'sbyte' not a catalog enum).
///   GAP1-OK-MIDKEY             — a VALID sbyte mid-path key ('Data[0].Name') + valid leaf Set stays accepted (no over-reject).
///   GAP1-REJ-E2E               — a malformed mid-path key refuses end-to-end with NO file written (review hardening); the
///                                PRE-FLIGHT 'does not coerce to sbyte', not the apply sbyte FormatException, proves the gate.
///
/// GAP 1 (cont.) — mid-path LIST-index VALUE-SHAPE (the list twin of the dict-key mid-path gate above; the one-segment-up
/// twin of the leaf KEYSHAPE-REJ-NEGIDX arm). ValidateFromType's bracketed MID-PATH list branch checked the index with a
/// bare int.TryParse, which ACCEPTS a negative ('-1' parses) — but apply's StepIntoElement list branch requires idx &gt;= 0
/// and throws a PLAIN InvalidOperationException (a SHAPE error, deliberately not an ExpectedApplyRejection), so a negative
/// mid-path index ('Conditions[-1].field') was accepted then threw under the "real inconsistency" wrapper. The LEAF index
/// was already on IsValidListIndexValue; the mid-path hop drifted. The fix points it onto the SAME recognizer, so leaf and
/// mid-path can't drift (after it, the apply non-negative throw is unreachable on the gated path; it stays as defense-in-depth):
///   GAP1-REJ-MIDLISTIDX-NEG    — a NEGATIVE mid-path list index ('Conditions[-1].CompareOperator') refuses at PRE-FLIGHT
///                                (Faction.Conditions = List&lt;Condition&gt;, a struct-element list; a valid enum leaf so ONLY
///                                the index is at fault). RED before: accepted (bare int.TryParse passed '-1').
///   GAP1-OK-MIDLISTIDX         — a VALID non-negative mid-path list index ('Conditions[0].CompareOperator') stays accepted (no over-reject).
///   GAP1-REJ-NEGIDX-E2E        — a negative mid-path list index refuses end-to-end with NO file written; op1 composes a
///                                ConditionFloat to materialize the list (non-null) so apply truly reaches the negative-index
///                                throw, then op2 navigates [-1]. RED before: the apply throw under the inconsistency wrapper;
///                                with the fix the PRE-FLIGHT 'non-negative integer' (anti-wrapper asserts) proves the gate.
///
/// GAP 2 — NON-FORMLINK coercible collection ELEMENT VALUE-SHAPE (the value twin of step-4a + the dict-Set value block).
/// A non-null, non-formlink, MALFORMED coercible element value on list Add/SetAtIndex/ReplaceAll/Remove-by-value and dict
/// Add/Merge/ReplaceAll passed pre-flight then threw UNNAMED at apply (Coerce -> float.Parse/byte.Parse). dict-Set value
/// was already gated; the new ValueLegality step-4b block mirrors that CheckValue across those verbs/slots, scoped to
/// IsValueCoercibleElement && FormLinkTarget is null (formlink elements keep step-4a) and verb/key-faithful to which slot
/// apply coerces (so a Remove-BY-INDEX with a stray value is not over-rejected):
///   GAP2-REJ-DICTADD           — a malformed dict Add value ('notabyte' into Dictionary&lt;Skill,Byte&gt;) refuses 'does
///                                not coerce to Byte' (RED before: accepted — only dict Set value was gated).
///   GAP2-REJ-LISTADD           — a malformed list Add value ('notafloat' into List&lt;Single&gt;) refuses (RED: accepted).
///   GAP2-REJ-LISTREPLACEALL    — a bad ReplaceAll value PAST a valid one is caught and NAMED (per-element scan). RED: accepted.
///   GAP2-REJ-LISTREMOVE        — a malformed Remove-BY-VALUE (Key null) refuses (RED: accepted).
///   GAP2-REJ-DICTMERGE         — a malformed Merge entries value refuses ('notabyte'). RED: accepted.
///   GAP2-OK-VALID              — valid list+dict element values stay accepted (no over-reject).
///   GAP2-OK-REMOVE-BYINDEX     — a list Remove BY INDEX (Key present) carrying a stray value is NOT over-rejected (apply
///                                ignores the value on a by-index Remove) — proves the verb/key-faithful scoping.
///   GAP2-FORMLINK-ROUTE        — a formlink list (Weapon.Keywords) stays on step-4a: a valid FormID accepted, a malformed
///                                one refuses 'Illegal FormLink element' (NOT 'does not coerce') — the two blocks partition
///                                coercible elements with no overlap.
///   GAP2-OK-OFFCARD-SLOT       — a stray off-cardinality slot apply ignores (req.Entries on a list ReplaceAll) is NOT
///                                over-rejected — step-4b's loops are slot-faithful (Values->list, Entries->dict), review polish.
///   GAP2-REJ-E2E               — a malformed list value refuses end-to-end with NO file written; the PRE-FLIGHT message
///                                ('does not coerce to Single'), not the apply float.Parse throw, proves the gate.
///
/// G6 — RECORD-ELEMENT collection VERBS. A list/dict whose ELEMENT is an owned child RECORD (DialogTopic.Responses ->
/// DialogResponses) is neither coercible, composable, nor formlink, so a collection verb fell through ValueLegality to
/// ACCEPT then threw at apply (compose -> CompositionRequiredException, named-but-MISLEADING; or plain value -> Coerce
/// 'No coercion rule'). The new step-4-rec branch redirects Add/SetAtIndex/ReplaceAll to the record axis
/// (create_record/bulk_create parent=) by one ClassifyElement==Record predicate; a record Remove BY INDEX stays accepted:
///   G6-REJ-RECORD-ADD          — a record-element Add (compose) refuses, naming create_record (RED before: accepted then
///                                CompositionRequiredException at apply).
///   G6-REJ-RECORD-REPLACEALL   — a record-element ReplaceAll refuses too (verb coverage). RED before: accepted.
///   G6-OK-REMOVE-BYINDEX       — a record-element Remove BY INDEX (RemoveAt) stays accepted (no over-reject; verb-scoped).
///   G6-OK-STRUCT-UNCHANGED     — a struct-element Add (Faction.Ranks) still composes — the Record branch is mutually
///                                exclusive with Struct/Arm, so it doesn't bleed onto the real composition surface.
///   G6-REJ-E2E                 — a record-element Add refuses end-to-end with NO file written; the PRE-FLIGHT message
///                                (not the apply CompositionRequiredException) proves the gate. (A FLAT create whose own op
///                                does the bad Add — NOT a parent= nested create, which is a disjoint, legitimate path.)
///
/// G4 — StructSpec CtorArgs VALUE-SHAPE + ARITY. A compose (polymorphic Set arm OR struct-element Add) can carry
/// positional ctor_args (StructInput.CtorArgs -> StructSpec.CtorArgs); StructSpecContents validated Fields + Sets but
/// NEVER CtorArgs. At apply, Instantiate selects GetConstructors().FirstOrDefault(len==N) then Coerce(arg, paramType) — a
/// wrong ARITY threw 'no constructor taking N arg(s)' (named-but-at-apply) and a malformed arg threw UNNAMED. The new
/// WriteEngine.TryRecognizeCtorArgs mirrors Instantiate EXACTLY (ResolveStructType + same ctor selector + TryCoerce per
/// arg), called from StructSpecContents — the ONE gap whose recognizer is new, but composed from existing engine
/// primitives, by construction:
///   G4-REJ-CTORARG-SHAPE       — a malformed ctor arg ('notatypeenum' for MagicEffectArchetype's TypeEnum) of the right
///                                arity refuses at PRE-FLIGHT, naming the bad arg (RED before: accepted then Enum.Parse
///                                threw unnamed at apply).
///   G4-REJ-CTORARG-ARITY       — a wrong ctor-arg COUNT refuses (mirrors Instantiate's 'no constructor taking 3 arg(s)').
///                                RED before: accepted.
///   G4-OK-CTORARG              — valid ctor args ('ValueModifier') of the right arity stay accepted (no over-reject).
///   G4-OK-NOCTORARGS          — a compose WITHOUT ctor_args (the common case) is untouched (the spec.CtorArgs guard skips).
///   G4-REJ-CTORARG-E2E         — a malformed ctor arg refuses end-to-end with NO file written (review hardening); the
///                                PRE-FLIGHT 'ctor arg #0', not the apply Enum.Parse throw, proves the gate ran before Instantiate.
///
/// G7 — composable-element MERGE + non-plain-value Remove-BY-VALUE (the deferred-reject completion; matrix-critic finding
/// + a record twin found while implementing G6). The IsComposableElement deferred-reject covered Add and
/// ReplaceAll/SetAtIndex but OMITTED Merge (a Package.Data Merge fell through to ACCEPT then threw 'No coercion rule' at
/// apply), and a Remove BY VALUE (Key null) on ANY non-plain-value element (composable OR record — neither has a
/// plain-value form) likewise fell through then threw. The fix folds Merge into the composable deferred-reject and adds
/// ONE unified Remove-by-value branch (predicate: list Remove, Key null, NOT formlink, NOT coercible) covering
/// composable + record + the dormant uncoercible case by construction:
///   G7-REJ-DICTMERGE                 — a Package.Data Merge refuses ('Merge of modeled elements is a later surface')
///                                      (RED before: accepted then 'No coercion rule' at apply).
///   G7-REJ-COMPOSABLE-REMOVE-BYVALUE — a struct-element (Faction.Ranks) Remove-by-value refuses, redirected to remove-by-index.
///   G7-REJ-RECORD-REMOVE-BYVALUE     — a record-element (DialogTopic.Responses) Remove-by-value refuses too — the unified
///                                      non-plain-value branch covers records (the twin found while implementing G6).
///   G7-OK-COMPOSABLE-REMOVE-BYINDEX  — a struct-element Remove BY INDEX (RemoveAt) stays accepted (no over-reject).
///   G7-OK-DICTREMOVE-BYKEY           — a composable-dict (Package.Data) Remove BY KEY stays accepted (the branch is list-only).
///
/// GAP 3 — dict-element COMPOSITION (PR-B; AI-package Data-input authoring). Package.Data (Dictionary&lt;sbyte,APackageData&gt;)
/// is the ONLY struct/arm-VALUED dict Mutagen models, so this is the last un-authorable PACK piece — a package's typed Data
/// inputs (target/location/bool/int/float/objectlist/topic). Before Gap 3 the gate refused a dict Add carrying a compose
/// ('dict-element composition is a later surface') and ApplyDictVerb ignored req.Struct — a correct-but-incomplete case-(c)
/// deferral, NOT an accept-then-throw. Gap 3 BUILDS it by construction, mirroring the LIST compose path: ApplyDictVerb
/// Add/Set now BuildStruct(req.Struct) for a composable element, and the gate ACCEPTS the spec via the SAME
/// StructElementLegality the list Add uses (poly-base arm resolution + recursive contents — no per-type wiring). Add stays
/// throw-on-duplicate (Aaron 2026-06-18); overwrite is Set-with-compose. G6/G7 keep record-element verbs +
/// ReplaceAll/SetAtIndex/Merge deferred, so only Add/Set compose:
///   GAP3-OK-DICTADD-COMPOSE  — a Package.Data Add carrying a PackageDataBool compose is ACCEPTED (RED before: 'later surface').
///   GAP3-OK-DICTSET-COMPOSE  — a Package.Data Set carrying a compose is ACCEPTED (the overwrite path; RED before: 'requires a value').
///   GAP3-REJ-BADARM          — a compose type that is not an APackageData arm refuses, naming the legal arms (RED before: 'later surface').
///   GAP3-OK-LIST-UNCHANGED   — an ARM-element LIST compose (Faction.Conditions) still composes (the dict-Add change didn't disturb the list path).
///   GAP3-E2E                 — a composed PackageDataBool(Data=true) round-trips through the real create+apply path onto Package.Data[0] on disk.
///   GAP3-E2E-SET             — the Set-OVERWRITE apply branch round-trips: Add Data[0]=false then Set Data[0]=true reads Data==true on disk (the dup escape hatch).
///   GAP3-REJ-DUP             — an Add of an already-present key refuses (apply-time) end-to-end, NO file written, naming Set as the overwrite path,
///                              and renders that EXPECTED rejection cleanly — NO "real inconsistency" wrapper (gap-audit Finding 3).
///
/// G8 — the polymorphic BASE composed by its OWN name (PR-B review finding; PRE-EXISTING, shared list+dict). A compose
/// naming the base itself ({Type:"APackageData"} on Package.Data, {Type:"Condition"} on a *.Conditions list) hit
/// StructElementLegality's `if (spec.Type == er) specSchema = elemSchema` short-circuit, validated against the base's OWN
/// fields, and ACCEPTED — then apply DIVERGED by base kind (verified by reflection, correcting the PR-B note's
/// 'CompositionRequiredException' guess): a CONCRETE base (APackageData, IsAbstract=false, public parameterless ctor)
/// Instantiate()s a degenerate empty base and SILENTLY WRITES IT (a Q3 silent-wrong-write, WORSE than a throw); an
/// ABSTRACT base (Condition) throws MemberAccessException at Invoke. A CONCRETE poly-base also lists ITSELF among its arms
/// (FindUnionArms keeps it; only an ABSTRACT base like Condition is filtered by !IsAbstract), so the arms.Contains branch
/// would admit it too and GAP3-REJ-BADARM's message advertised it. The fix rejects composing the base by its own name and
/// filters the base out of the legal-arms set everywhere (Contains + every message) at BOTH composition entry points —
/// StructElementLegality (collection elements: the 3 self-listing bases APackageData/BaseLayer/ScriptProperty)
/// AND its sibling ArmLegality (standalone polymorphic FIELDS: DialogResponsesAdapter.ScriptFragments,
/// GenderedItem&lt;SimpleModel&gt; — the twin found in a completeness sweep, folded in Aaron 2026-06-18). Recognizer = corpus
/// poly-base KIND, NOT Type.IsAbstract (APackageData is concrete, so IsAbstract would miss the silent-write case). By
/// construction over every poly-base family, no per-type wiring, no generator/corpus change; a concrete non-poly-base
/// struct composed by its own name still accepts:
///   GAP3-REJ-BASEARM         — a Package.Data Add composing the base 'APackageData' itself refuses, offering the concrete
///                              arms (RED before: accepted via the spec.Type==er short-circuit).
///   GAP3-REJ-BASEARM-LIST    — the LIST twin (Faction.Conditions Add {Type:"Condition"}) refuses too — proves the fix is
///                              in the SHARED validator and catches an ABSTRACT base by the same check. RED before: accepted.
///   GAP3-OK-BASE-NOOVERREJECT— a CONCRETE struct element composed by its own name (Faction.Ranks element 'Rank', Kind=struct)
///                              STILL accepts — only poly-bases are rejected, not every spec.Type==er. Accepted before AND after.
///   GAP3-REJ-BASEARM-E2E     — composing the base refuses end-to-end with NO file written; RED here was the WORST case (the
///                              concrete base is instantiable, so it SILENTLY wrote a degenerate entry) — GREEN: gate rejects, no file.
///   GAP3-REJ-BASEARM-FIELD   — the ArmLegality twin: a standalone poly-FIELD Set composing the base
///                              (DialogResponses.VirtualMachineAdapter.ScriptFragments {Type:"ScriptFragments"}) refuses. RED before: accepted.
///   GAP3-OK-ARMFIELD-UNCHANGED— a REAL arm ('SceneScriptFragments') on that poly field still accepts (no over-reject). Before AND after.
///   (GAP3-REJ-BADARM strengthened: its 'Legal element types' list no longer names the un-composable base 'APackageData'.)
///
/// EXPECTED apply rejections — the LIVE-STATE collection-addressing class (gap-audit Finding 3, "close the whole class").
/// Apply-time refusals whose cause is live state the schema-only gate CANNOT see (occupancy / length / presence) must
/// render CLEANLY via WritePatchBuilder, NOT under the "pre-flight ACCEPTED … a real inconsistency" wrapper (an
/// ExpectedApplyRejectionException, a subclass of IOE the catch keys off). GAP3-REJ-DUP (above) is the dict-occupancy
/// member; the rest:
///   EXPECTED-REJ-SETIDX-OOB    — a list SetAtIndex past the end refuses cleanly E2E, NO file (Race.MovementTypeNames[5]). RED: wrapped.
///   EXPECTED-REJ-REMOVEIDX-OOB — a list Remove-by-index past the end refuses cleanly E2E, NO file (Add one, then Remove[5]). RED: wrapped.
///   EXPECTED-OK-SETIDX-INRANGE — an in-range SetAtIndex[0] still APPLIES (value lands; the new pre-check doesn't over-reject).
///   EXPECTED-NAV-TYPE          — the shared StepIntoElement throws the EXPECTED kind for an absent dict entry AND an out-of-bounds list index. RED: plain IOE.
///   EXPECTED-REJ-NAV-E2E       — a mid-path nav reject (Package.Data[5].Name, absent key) renders cleanly THROUGH WritePatchBuilder, NO file. RED: wrapped.
///   (Left LOUD by design: bad-SHAPE index, structural/reflection failures — not clean live-state addressing. Present-but-null
///    is now its own MalformedTargetDataException third category — see below.)
///
/// Gap 2 (PR #83 follow-up) — a present-but-null element/entry is its own MalformedTargetDataException THIRD category.
/// Such a throw was a plain InvalidOperationException → the "pre-flight ACCEPTED … a real inconsistency" wrapper, which
/// mislabels pre-existing malformed SOURCE data (not user input, not an engine bug) as an internal inconsistency. Now its
/// own exception kind, caught distinctly by WritePatchBuilder and rendered cleanly (Aaron 2026-06-18: dedicated third
/// category). The state isn't producible via houseCARL's write path (the null gates forbid it) and Mutagen won't
/// serialize a null element to make a malformed fixture, so this is engine-direct (throw kind + message); the type
/// deterministically routes to the dedicated catch (a verbatim passthrough mirroring the proven ExpectedApplyRejection catch):
///   MALFORMED-NAV-TYPE         — a present-but-null dict entry AND list element throw MalformedTargetDataException ('malformed'
///                                message), not a plain IOE. RED before: plain InvalidOperationException → not caught as the third kind.
///
/// Gap 3 (PR #83 follow-up) — surface a Remove that removes NOTHING (close the silent-no-op). list Remove-by-value and
/// dict Remove-by-key ignored the runtime Remove's bool, so "remove X" when X isn't present SILENTLY succeeded (Q3
/// degradation). Now all three forms surface as the EXPECTED kind ("nothing to remove") — the symmetric twin of Add's
/// duplicate-key refusal; consistent with Remove-by-INDEX already surfacing out-of-range (Aaron 2026-06-18: surface, not
/// idempotent). REMOVE-* disambiguates from the GAP3-* dict-element-COMPOSITION arms:
///   REMOVE-REJ-DICTKEY-ABSENT  — a dict Remove of a key not present refuses cleanly E2E, NO file. RED before: silent success → file written.
///   REMOVE-REJ-NULLCOLL        — a Remove on an absent (null) collection refuses cleanly E2E, NO file. RED before: silent no-op → file written.
///   REMOVE-REJ-LISTVAL-ABSENT  — a list Remove-by-value of a value not present throws the EXPECTED kind (in-memory). RED before: no throw.
///   REMOVE-OK-PRESENT-DICT     — a dict Remove of a PRESENT key still succeeds (no over-reject of a real removal).
///   REMOVE-OK-PRESENT-LIST     — a list Remove-by-value of a PRESENT value still succeeds (no over-reject).
///
/// VOICE (Layer B unit B) — the on-disk voice (.fuz/.lip) PRESENCE check for created dialogue lines (nested-dialogue
/// plan §3.5). A byte-valid INFO with no .fuz on disk plays NOTHING — the silent-failure class houseCARL refuses (Q3).
/// VoiceCheck runs as a post-create step: it walks the written patch to map each created INFO to its parent topic,
/// resolves the speaker chain (INFO.Speaker -> Npc.Voice -> VoiceType.EditorID) + the topic's quest, computes each
/// response line's expected path (VoicePath, the xEdit InfoFileName transform), and checks the VFS (AssetResolver).
/// The master carries a VoiceType + an NPC (Voice -> it) + a Quest the topic points at; an AssetResolver over a TEMP
/// Data root (planted / absent .fuz) makes the present/silent verdict CI-testable with no real load order:
///   VOICE-PATH        — the PURE transform locks the keystone format: Quest EDID[..10] _ Topic EDID[..15] _ "00"+6hex
///                       local id _ ResponseNumber . fuz/.lip (verified empirically against real loose .fuz files).
///   VOICE-SILENT      — a created voiced line (Speaker set, one response) with NO .fuz planted reports 1 line, NOT
///                       present, at exactly the computed path (the "WILL BE SILENT" path the create surfaces).
///   VOICE-PRESENT     — planting BOTH the .fuz and the .lip at the computed paths flips the SAME line to present
///                       (FuzPresent + LipPresent, winner = the Data root) — proves the path the check builds is the
///                       path the engine looks under (no silent-wrong path), and exercises both the .fuz and .lip legs.
///   VOICE-NOSPEAKER   — a created line with NO Speaker can't resolve a voice folder (the runtime quest-alias case): a
///                       NAMED undetermined reason naming Speaker, and NO line — never a false "fine" (Q3).
///   VOICE-MULTIRESP   — an INFO with two response lines numbered NON-sequentially (5, 2) yields two lines whose paths
///                       carry _5 / _2 — one presence check PER spoken response, keyed by the line's own ResponseNumber
///                       (a positional i+1 impl would emit {1,2} and fail).
///   VOICE-SAMECALL    — the speaker NPC and its VoiceType are created in the SAME bulk_create as the voiced INFO, so
///                       the speaker chain resolves through patchByKey (same-call records), not the load order.
///   VOICE-CHECKERROR  — the check run against a CORRUPT patch path surfaces on VoiceReport.CheckError and does NOT
///                       throw / does NOT lose the created records — the Q3 "never demote a successful create" safety net.
/// </summary>
public static class NestedCreateGuardProbe
{
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("################  REGRESSION GUARD — nested-record CREATE (nested/dialogue plan, Layer A)  ################");
        Console.WriteLine();

        var tmpDir = Path.Combine(Path.GetTempPath(), "hc-nested-create-guard");
        if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true);
        Directory.CreateDirectory(tmpDir);

        // --- Setup: a master carrying the three nest-under fixtures + the validator corpus.
        //     Weapon  → the can't-contain reject (N5).  DialogTopic → the unique-collection happy path (N2).
        //     interior Cell → the named-collection happy path (N3) + the ambiguous-collection reject (N6). ---
        var mKey = new ModKey("HcNcGdMaster", ModType.Master);
        string mPath = Path.Combine(tmpDir, mKey.FileName.String);
        FormKey masterWeapFk, masterTopicFk, masterCellFk, masterVoiceFk, masterNpcFk, masterQuestFk;
        try
        {
            var m = new SkyrimMod(mKey, SkyrimRelease.SkyrimSE);

            var w = m.Weapons.AddNew(); w.EditorID = "HcNcGdWeap"; w.BasicStats = new WeaponBasicStats { Damage = 10 };
            masterWeapFk = w.FormKey;

            var topic = m.DialogTopics.AddNew(); topic.EditorID = "HcNcGdTopic";
            masterTopicFk = topic.FormKey;

            // Voice-check fixtures (Layer B unit B): the speaker chain INFO.Speaker -> Npc.Voice -> VoiceType.EditorID,
            // and a Quest the topic points at (the path's quest segment). A line created under this topic with this
            // speaker has a fully-resolvable voice path.
            var voice = m.VoiceTypes.AddNew(); voice.EditorID = "HcNcGdVoice";
            masterVoiceFk = voice.FormKey;
            var quest = m.Quests.AddNew(); quest.EditorID = "HcNcGdQuest";
            masterQuestFk = quest.FormKey;
            var npc = m.Npcs.AddNew(); npc.EditorID = "HcNcGdNpc"; npc.Voice.SetTo(voice.FormKey);
            masterNpcFk = npc.FormKey;
            topic.Quest.SetTo(quest.FormKey);

            // An interior cell lives under a CellBlock/CellSubBlock structure (FormKey-LESS group structs); build it by
            // hand — there's no flat AddNew for a cell. The cell itself is a normal (FormKey, release) record.
            var cell = new Cell(m.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcNcGdCell" };
            masterCellFk = cell.FormKey;
            var subBlock = new CellSubBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellSubBlock };
            subBlock.Cells.Add(cell);
            var block = new CellBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellBlock };
            block.SubBlocks.Add(subBlock);
            m.Cells.Records.Add(block);

            m.BeginWrite.ToPath(mPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: could not synthesize the fixture master: {ex.GetType().Name}: {(ex.InnerException ?? ex).Message}");
            try { Directory.Delete(tmpDir, recursive: true); } catch { }
            return 1;
        }

        // Confirm the synthesized fixtures actually round-trip + resolve (a master that doesn't carry them would make
        // the fixture-dependent arms silently test nothing — Q3).
        bool fixturesOk;
        using (var r = LoadOrderResolver.Build(new[] { mPath }))
        {
            var view = r.Capture();
            fixturesOk = view.ResolveWinner(masterWeapFk) is not null
                      && view.ResolveWinner(masterTopicFk) is not null
                      && view.ResolveWinner(masterCellFk) is not null
                      && view.ResolveWinner(masterVoiceFk) is not null
                      && view.ResolveWinner(masterNpcFk) is not null
                      && view.ResolveWinner(masterQuestFk) is not null;
        }
        var genDir = Path.Combine(tmpDir, "corpus-gen");
        CorpusGenerator.GenerateAll(genDir, Path.Combine(tmpDir, "corpus-ref"));
        var rulebook = CorpusRulebook.Load(Path.Combine(genDir, "corpus.json"));
        Console.WriteLine($"-- setup: master {mKey.FileName} with weapon {masterWeapFk}, topic {masterTopicFk}, cell {masterCellFk}, voice {masterVoiceFk}, npc {masterNpcFk}, quest {masterQuestFk}; fixtures-resolve={fixturesOk}; corpus generated --");
        Console.WriteLine();

        // ---------- ONESHOT (N1): topic + its first INFO in ONE call ----------
        bool oneshotOk = false;
        {
            string pPath = Path.Combine(tmpDir, "HcNcOneShot.esp");
            var specs = new[]
            {
                new WritePatchBuilder.CreateSpec { RecordType = "DialogTopic", EditorId = "HcNcOsTopic", Edits = Array.Empty<WriteRequest>() },
                new WritePatchBuilder.CreateSpec { RecordType = "DialogResponses", EditorId = "HcNcOsInfo", ParentRef = "HcNcOsTopic", Edits = Array.Empty<WriteRequest>() },
            };
            using var r = LoadOrderResolver.Build(new[] { mPath });
            var o = WritePatchBuilder.CreateRecords(r, rulebook, specs, pPath, extend: false);
            bool floored = o.Success && o.Created.Count == 2 && o.Created.All(c => c.FormKey.ID >= 0x800);
            var responses = o.Success ? TopicResponses(pPath, "HcNcOsTopic") : null;
            bool infoUnder = responses is not null && o.Success && responses.Contains(o.Created[1].FormKey);
            bool distinct = o.Success && o.Created.Count == 2 && o.Created[0].FormKey != o.Created[1].FormKey;
            oneshotOk = o.Success && floored && infoUnder && distinct;
            Console.WriteLine($"   ONESHOT  topic+first INFO, one call : {(oneshotOk ? $"PASS — both >=0x800, INFO under topic ({(responses?.Count ?? 0)} response)" : $"FAIL — success={o.Success} floored={floored} infoUnder={infoUnder} distinct={distinct} err=[{o.Error}]")}");
        }

        // ---------- MULTICHILD (N8): topic + two INFOs (+ a field edit) in one call ----------
        bool multiOk = false;
        {
            string pPath = Path.Combine(tmpDir, "HcNcMulti.esp");
            var specs = new[]
            {
                new WritePatchBuilder.CreateSpec { RecordType = "DialogTopic", EditorId = "HcNcMTopic", Edits = Array.Empty<WriteRequest>() },
                new WritePatchBuilder.CreateSpec { RecordType = "DialogResponses", EditorId = "HcNcML1", ParentRef = "HcNcMTopic", Edits = Array.Empty<WriteRequest>() },
                new WritePatchBuilder.CreateSpec { RecordType = "DialogResponses", EditorId = "HcNcML2", ParentRef = "HcNcMTopic",
                    Edits = new[] { new WriteRequest { RecordType = "DialogResponses", Path = new[] { "Prompt" }, Verb = "Set", Value = "houseCARL line two" } } },
            };
            using var r = LoadOrderResolver.Build(new[] { mPath });
            var o = WritePatchBuilder.CreateRecords(r, rulebook, specs, pPath, extend: false);
            var responses = o.Success ? TopicResponses(pPath, "HcNcMTopic") : null;
            bool bothUnder = responses is not null && o.Success && responses.Contains(o.Created[1].FormKey) && responses.Contains(o.Created[2].FormKey);
            string? l2Prompt = o.Success ? InfoPrompt(pPath, o.Created[2].FormKey) : null;
            string? l1Prompt = o.Success ? InfoPrompt(pPath, o.Created[1].FormKey) : null;
            bool editLanded = l2Prompt == "houseCARL line two";
            bool editIsolated = l1Prompt != "houseCARL line two";   // the edit landed on L2 ONLY — it did NOT leak to its sibling L1
            multiOk = o.Success && o.Created.Count == 3 && bothUnder && editLanded && editIsolated;
            Console.WriteLine($"   MULTICHILD topic+2 INFO + field edit: {(multiOk ? $"PASS — 3 created, both under topic, L2.Prompt landed (only on L2)" : $"FAIL — success={o.Success} count={(o.Success ? o.Created.Count : 0)} bothUnder={bothUnder} editLanded={editLanded} editIsolated={editIsolated} l2=[{l2Prompt}] l1=[{l1Prompt}] err=[{o.Error}]")}");
        }

        // ---------- INTOTOPIC (N2): INFO into an EXISTING (master) topic by FormKey ----------
        bool intoTopicOk = false;
        {
            string pPath = Path.Combine(tmpDir, "HcNcIntoTopic.esp");
            var spec = new[] { new WritePatchBuilder.CreateSpec { RecordType = "DialogResponses", EditorId = "HcNcN2Info", ParentRef = masterTopicFk.ToString(), Edits = Array.Empty<WriteRequest>() } };
            using var r = LoadOrderResolver.Build(new[] { mPath });
            var o = WritePatchBuilder.CreateRecords(r, rulebook, spec, pPath, extend: false);
            bool floored = o.Success && o.Created.Count == 1 && o.Created[0].FormKey.ID >= 0x800 && o.Created[0].FormKey.ModKey.FileName.String == "HcNcIntoTopic.esp";
            var responses = o.Success ? TopicResponses(pPath, masterTopicFk) : null;
            bool present = responses is not null && o.Success && responses.Contains(o.Created[0].FormKey);
            intoTopicOk = o.Success && floored && present;
            Console.WriteLine($"   INTOTOPIC INFO into existing topic  : {(intoTopicOk ? "PASS — new INFO >=0x800/local, under the topic override" : $"FAIL — success={o.Success} floored={floored} present={present} err=[{o.Error}]")}");
        }

        // ---------- INTOCELL (N3): PlacedObject into an EXISTING cell, collection named 'Persistent' ----------
        bool intoCellOk = false;
        {
            string pPath = Path.Combine(tmpDir, "HcNcIntoCell.esp");
            var spec = new[] { new WritePatchBuilder.CreateSpec { RecordType = "PlacedObject", EditorId = "HcNcN3Ref", ParentRef = masterCellFk.ToString(), IntoCollection = "Persistent", Edits = Array.Empty<WriteRequest>() } };
            using var r = LoadOrderResolver.Build(new[] { mPath });
            var o = WritePatchBuilder.CreateRecords(r, rulebook, spec, pPath, extend: false);
            bool floored = o.Success && o.Created.Count == 1 && o.Created[0].FormKey.ID >= 0x800 && o.Created[0].FormKey.ModKey.FileName.String == "HcNcIntoCell.esp";
            var persistent = o.Success ? CellPersistent(pPath, masterCellFk) : null;
            bool present = persistent is not null && o.Success && persistent.Contains(o.Created[0].FormKey);
            intoCellOk = o.Success && floored && present;
            Console.WriteLine($"   INTOCELL Placed into cell.Persistent: {(intoCellOk ? "PASS — new ref >=0x800/local, in the cell override's Persistent" : $"FAIL — success={o.Success} floored={floored} present={present} err=[{o.Error}]")}");
        }

        // ---------- REJ-NOPARENT (N4) ----------
        bool rejNoParentOk = RejectArm("REJ-NOPARENT nested no parent     ", tmpDir, "N4", mPath, rulebook,
            new[] { new WritePatchBuilder.CreateSpec { RecordType = "DialogResponses", EditorId = "HcNcN4", Edits = Array.Empty<WriteRequest>() } },
            msg => msg.Contains("parent", StringComparison.OrdinalIgnoreCase) || msg.Contains("nested", StringComparison.OrdinalIgnoreCase));

        // ---------- REJ-BADPARENT (N5): INFO under a Weapon ----------
        bool rejBadParentOk = RejectArm("REJ-BADPARENT INFO under Weapon   ", tmpDir, "N5", mPath, rulebook,
            new[] { new WritePatchBuilder.CreateSpec { RecordType = "DialogResponses", EditorId = "HcNcN5", ParentRef = masterWeapFk.ToString(), Edits = Array.Empty<WriteRequest>() } },
            msg => msg.Contains("cannot be created under", StringComparison.OrdinalIgnoreCase));

        // ---------- REJ-AMBIG (N6): Placed into a Cell with no collection named ----------
        bool rejAmbigOk = RejectArm("REJ-AMBIG Placed, no collection    ", tmpDir, "N6", mPath, rulebook,
            new[] { new WritePatchBuilder.CreateSpec { RecordType = "PlacedObject", EditorId = "HcNcN6", ParentRef = masterCellFk.ToString(), Edits = Array.Empty<WriteRequest>() } },
            msg => msg.Contains("more than one", StringComparison.OrdinalIgnoreCase) && msg.Contains("Persistent", StringComparison.OrdinalIgnoreCase));

        // ---------- REJ-FWDSIB (N7): sibling parent declared LATER ----------
        bool rejFwdSibOk = RejectArm("REJ-FWDSIB forward sibling parent  ", tmpDir, "N7", mPath, rulebook,
            new[]
            {
                new WritePatchBuilder.CreateSpec { RecordType = "DialogResponses", EditorId = "HcNcN7Info", ParentRef = "HcNcN7Topic", Edits = Array.Empty<WriteRequest>() },
                new WritePatchBuilder.CreateSpec { RecordType = "DialogTopic", EditorId = "HcNcN7Topic", Edits = Array.Empty<WriteRequest>() },
            },
            msg => msg.Contains("earlier in this call", StringComparison.OrdinalIgnoreCase));

        // ---------- EXTEND (was N9): a parent created in a PRIOR into= call IS now resolvable (the N9 fix) ----------
        bool extendOk = false;
        {
            string pPath = Path.Combine(tmpDir, "HcNcExtend.esp");
            // call 1: a one-shot topic + its first line (so the topic ALREADY carries a child when call 2 extends it).
            FormKey topicFk = default, l1Fk = default; bool call1Ok;
            using (var r = LoadOrderResolver.Build(new[] { mPath }))
            {
                var o1 = WritePatchBuilder.CreateRecords(r, rulebook,
                    new[]
                    {
                        new WritePatchBuilder.CreateSpec { RecordType = "DialogTopic", EditorId = "HcNcExTopic", Edits = Array.Empty<WriteRequest>() },
                        new WritePatchBuilder.CreateSpec { RecordType = "DialogResponses", EditorId = "HcNcExL1", ParentRef = "HcNcExTopic", Edits = Array.Empty<WriteRequest>() },
                    },
                    pPath, extend: false);
                call1Ok = o1.Success && o1.Created.Count == 2;
                if (call1Ok) { topicFk = o1.Created[0].FormKey; l1Fk = o1.Created[1].FormKey; }
            }
            // call 2: add a SECOND line under that topic — the topic lives ONLY in the patch (the N9 case).
            bool call2Ok = false; FormKey l2Fk = default;
            if (call1Ok)
                using (var r = LoadOrderResolver.Build(new[] { mPath }))
                {
                    var o2 = WritePatchBuilder.CreateRecords(r, rulebook,
                        new[] { new WritePatchBuilder.CreateSpec { RecordType = "DialogResponses", EditorId = "HcNcExL2", ParentRef = topicFk.ToString(), Edits = Array.Empty<WriteRequest>() } },
                        pPath, extend: true);
                    call2Ok = o2.Success; l2Fk = o2.Success ? o2.Created[0].FormKey : default;
                }
            // BOTH the prior line (L1) and the new line (L2) must be under the topic — the patch-carried parent is used
            // in full, never an override carrying only the new child (which would silently drop L1).
            var responses = call2Ok ? TopicResponses(pPath, topicFk) : null;
            bool under = responses is not null && responses.Contains(l1Fk) && responses.Contains(l2Fk);
            // and a GENUINELY-absent parent (in neither the load order nor the patch) still refuses loud, naming both.
            bool absentRefused = false; string? absentErr = null;
            if (call1Ok)
                using (var r = LoadOrderResolver.Build(new[] { mPath }))
                {
                    var o3 = WritePatchBuilder.CreateRecords(r, rulebook,
                        new[] { new WritePatchBuilder.CreateSpec { RecordType = "DialogResponses", EditorId = "HcNcExGhost", ParentRef = "0F0F0F:HcNcGdMaster.esm", Edits = Array.Empty<WriteRequest>() } },
                        pPath, extend: true);
                    absentRefused = !o3.Success; absentErr = o3.Error;
                }
            bool absentNamed = absentErr is not null && absentErr.Contains("load order", StringComparison.OrdinalIgnoreCase) && absentErr.Contains("patch", StringComparison.OrdinalIgnoreCase);
            extendOk = call1Ok && call2Ok && under && absentRefused && absentNamed;
            Console.WriteLine($"   EXTEND patch-carried parent works    : {(extendOk ? "PASS — prior-call topic resolvable, BOTH lines under it; a truly-absent parent still refuses loud" : $"FAIL — call1={call1Ok} call2={call2Ok} under={under} absentRefused={absentRefused} absentNamed={absentNamed} absentErr=[{absentErr}]")}");
        }

        // ---------- SIBREF (Layer B unit A): @editorid same-call FormLink forward-ref ----------
        // The keystone: a one-shot topic + two lines where line 1 back-links to the same-call topic (Topic=@topic) and
        // line 2 chains off line 1 (PreviousDialog=@line1) — BOTH targets are sibling local 0x800+ FormKeys not known
        // until allocation. Proves the @editorid token resolves to the right allocated FormKey in a FormLink VALUE.
        bool sibrefOk = false;
        {
            string pPath = Path.Combine(tmpDir, "HcNcSibRef.esp");
            var specs = new[]
            {
                new WritePatchBuilder.CreateSpec { RecordType = "DialogTopic", EditorId = "HcNcSrTopic", Edits = Array.Empty<WriteRequest>() },
                new WritePatchBuilder.CreateSpec { RecordType = "DialogResponses", EditorId = "HcNcSrL1", ParentRef = "HcNcSrTopic",
                    Edits = new[] { new WriteRequest { RecordType = "DialogResponses", Path = new[] { "Topic" }, Verb = "Set", Value = "@HcNcSrTopic" } } },
                new WritePatchBuilder.CreateSpec { RecordType = "DialogResponses", EditorId = "HcNcSrL2", ParentRef = "HcNcSrTopic",
                    Edits = new[]
                    {
                        new WriteRequest { RecordType = "DialogResponses", Path = new[] { "Topic" }, Verb = "Set", Value = "@HcNcSrTopic" },
                        new WriteRequest { RecordType = "DialogResponses", Path = new[] { "PreviousDialog" }, Verb = "Set", Value = "@HcNcSrL1" },
                    } },
            };
            using var r = LoadOrderResolver.Build(new[] { mPath });
            var o = WritePatchBuilder.CreateRecords(r, rulebook, specs, pPath, extend: false);
            FormKey topicFk = o.Success ? o.Created[0].FormKey : default;
            FormKey l1Fk = o.Success && o.Created.Count > 1 ? o.Created[1].FormKey : default;
            FormKey l2Fk = o.Success && o.Created.Count > 2 ? o.Created[2].FormKey : default;
            var l1Topic = o.Success ? InfoTopic(pPath, l1Fk) : null;            // back-link → same-call topic
            var l2Topic = o.Success ? InfoTopic(pPath, l2Fk) : null;
            var l2Prev  = o.Success ? InfoPreviousDialog(pPath, l2Fk) : null;   // PNAM chain → prior same-call line
            bool backLink = l1Topic == topicFk && l2Topic == topicFk;
            bool pnam = l2Prev == l1Fk;
            sibrefOk = o.Success && o.Created.Count == 3 && backLink && pnam && topicFk != l1Fk && l1Fk != l2Fk;
            Console.WriteLine($"   SIBREF @editorid FormLink fwd-ref    : {(sibrefOk ? "PASS — Topic back-link + PreviousDialog chain resolved to same-call FormKeys" : $"FAIL — success={o.Success} backLink={backLink} pnam={pnam} l1Topic=[{l1Topic}] l2Prev=[{l2Prev}] topic=[{topicFk}] l1=[{l1Fk}] err=[{o.Error}]")}");
        }

        // ---------- REJ-SIBFWD: a field @ref to a sibling declared LATER refuses loud (declared-earlier rule) ----------
        bool sibRejFwdOk = RejectArm("REJ-SIBFWD @ref to later sibling   ", tmpDir, "SibFwd", mPath, rulebook,
            new[]
            {
                new WritePatchBuilder.CreateSpec { RecordType = "DialogTopic", EditorId = "HcNcSfTopic", Edits = Array.Empty<WriteRequest>() },
                new WritePatchBuilder.CreateSpec { RecordType = "DialogResponses", EditorId = "HcNcSfL1", ParentRef = "HcNcSfTopic",
                    Edits = new[] { new WriteRequest { RecordType = "DialogResponses", Path = new[] { "PreviousDialog" }, Verb = "Set", Value = "@HcNcSfL2" } } },
                new WritePatchBuilder.CreateSpec { RecordType = "DialogResponses", EditorId = "HcNcSfL2", ParentRef = "HcNcSfTopic", Edits = Array.Empty<WriteRequest>() },
            },
            msg => msg.Contains("EARLIER in this call", StringComparison.OrdinalIgnoreCase));

        // ---------- REJ-SIBNONFL: an @ref on a NON-FormLink field refuses loud (the string-collision guard) ----------
        bool sibRejNonflOk = RejectArm("REJ-SIBNONFL @ref on non-formlink  ", tmpDir, "SibNonFl", mPath, rulebook,
            new[]
            {
                new WritePatchBuilder.CreateSpec { RecordType = "DialogTopic", EditorId = "HcNcSnTopic", Edits = Array.Empty<WriteRequest>() },
                new WritePatchBuilder.CreateSpec { RecordType = "DialogResponses", EditorId = "HcNcSnL1", ParentRef = "HcNcSnTopic",
                    Edits = new[] { new WriteRequest { RecordType = "DialogResponses", Path = new[] { "FavorLevel" }, Verb = "Set", Value = "@HcNcSnTopic" } } },
            },
            msg => msg.Contains("only valid on a FormLink field", StringComparison.OrdinalIgnoreCase));

        // ---------- REJ-SIBLIST: an @ref inside a COLLECTION value (list ReplaceAll) refuses loud, not accept-then-throw ----------
        // The create path substitutes only the singular Set Value; a sibling token in req.Values would otherwise slip
        // past pre-flight and throw FormKey.Factory at apply (a Q3 accept-then-throw). Caught loud at the gate instead.
        bool sibRejListOk = RejectArm("REJ-SIBLIST @ref in list value     ", tmpDir, "SibList", mPath, rulebook,
            new[]
            {
                new WritePatchBuilder.CreateSpec { RecordType = "DialogTopic", EditorId = "HcNcSlTopic", Edits = Array.Empty<WriteRequest>() },
                new WritePatchBuilder.CreateSpec { RecordType = "DialogResponses", EditorId = "HcNcSlL1", ParentRef = "HcNcSlTopic",
                    Edits = new[] { new WriteRequest { RecordType = "DialogResponses", Path = new[] { "LinkTo" }, Verb = "ReplaceAll", Values = new[] { "@HcNcSlTopic" } } } },
            },
            msg => msg.Contains("only supported as a single Set value", StringComparison.OrdinalIgnoreCase));

        // ---------- REJ-SIBDICT: an @ref inside a DICT value (Merge Entries) refuses loud — the dict half of the gate ----------
        // Sibling tokens in a dict Entries' VALUES are caught by the same collection gate as the list case (the |
        // req.Entries branch). Class.SkillWeights (Dictionary<Skill,Byte>) is a flat dict leaf; the gate fires on the
        // '@' in the Entries value before any key/value coercion, so the dict key need not be a valid Skill.
        bool sibRejDictOk = RejectArm("REJ-SIBDICT @ref in dict value     ", tmpDir, "SibDict", mPath, rulebook,
            new[]
            {
                new WritePatchBuilder.CreateSpec { RecordType = "Class", EditorId = "HcNcSdClass",
                    Edits = new[] { new WriteRequest { RecordType = "Class", Path = new[] { "SkillWeights" }, Verb = "Merge",
                        Entries = new Dictionary<string, string> { ["OneHanded"] = "@HcNcSdClass" } } } },
            },
            msg => msg.Contains("only supported as a single Set value", StringComparison.OrdinalIgnoreCase));

        // ---------- REJ-SIBAPPLY: an @ref on the Apply/set_field path (no siblings) refuses at the rulebook ----------
        // Drives the rulebook DIRECTLY with a null sibling set (the override/set_field context) — proves the create-only
        // scoping that keeps @editorid from becoming an accept-then-substitute-nothing hole on the edit-existing path (Q3).
        bool sibRejApplyOk;
        {
            var req = new WriteRequest { RecordType = "DialogResponses", Path = new[] { "PreviousDialog" }, Verb = "Set", Value = "@AnySibling" };
            var reject = rulebook.Validate(req);   // null sibling set == the override/set_field context
            sibRejApplyOk = reject is not null && reject.Contains("only valid when creating records in ONE call", StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"   REJ-SIBAPPLY @ref on set_field path  : {(sibRejApplyOk ? "PASS — rejected at the gate (no same-call siblings when editing an existing record)" : $"FAIL — reject=[{reject}]")}");
        }

        // ====== GENERAL FormLink-ELEMENT collection value-shape (the broader gap the sibling-ref collection gate named) ======
        // The sibling-ref arms above gate a '@editorid' token in a collection value; this is the GENERAL case — ANY
        // malformed FormLink ELEMENT in a collection (a list/dict whose element is a FormLink). DialogResponses.LinkTo
        // is List<FormLink<IDialogTopicGetter>> (corpus FormLinkTarget set), the same fixture field REJ-SIBLIST used.

        // ---------- FLELEM-REJ-GATE: a malformed element in a ReplaceAll (req.Values) refuses at PRE-FLIGHT ----------
        // Drive the rulebook DIRECTLY (parent-free) — a non-null reject IS a gate refusal (apply would instead throw the
        // misleading "Malformed FormKey string"). A VALID FormID sits first in the list, so the per-element scan must
        // catch the bad one even past a good one, and the message must NAME the offending element (per-element, Q3).
        bool flElemRejGateOk;
        {
            var req = new WriteRequest { RecordType = "DialogResponses", Path = new[] { "LinkTo" }, Verb = "ReplaceAll",
                Values = new[] { masterTopicFk.ToString(), "notaformkey" } };
            var reject = rulebook.Validate(req);
            flElemRejGateOk = reject is not null
                && reject.Contains("Illegal FormLink element", StringComparison.OrdinalIgnoreCase)
                && reject.Contains("notaformkey", StringComparison.Ordinal);
            Console.WriteLine($"   FLELEM-REJ-GATE malformed list elem  : {(flElemRejGateOk ? "PASS — refused at pre-flight, names the bad element past a valid one" : $"FAIL — reject=[{reject}]")}");
        }

        // ---------- FLELEM-REJ-ADD: a malformed element in an Add (req.Value slot) refuses too ----------
        // Guards the req.Value arm of the gate specifically (Add/SetAtIndex carry the element in req.Value, not req.Values).
        bool flElemRejAddOk;
        {
            var req = new WriteRequest { RecordType = "DialogResponses", Path = new[] { "LinkTo" }, Verb = "Add", Value = "notaformkey" };
            var reject = rulebook.Validate(req);
            flElemRejAddOk = reject is not null && reject.Contains("Illegal FormLink element", StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"   FLELEM-REJ-ADD malformed Add value   : {(flElemRejAddOk ? "PASS — the req.Value slot is gated too" : $"FAIL — reject=[{reject}]")}");
        }

        // ---------- FLELEM-NULLCLEAR-OK: a null-clear synonym element is LEGAL (mirrors the singular formlink check) ----------
        // The gate shares IsValidFormLinkValue with the singular path, so "00000000" (a null-clear) must pass as an
        // element exactly as it does as a singular Set value — proves the gate doesn't over-reject the legal clear shape.
        bool flElemNullClearOk;
        {
            var req = new WriteRequest { RecordType = "DialogResponses", Path = new[] { "LinkTo" }, Verb = "ReplaceAll",
                Values = new[] { masterTopicFk.ToString(), "00000000" } };
            var reject = rulebook.Validate(req);
            flElemNullClearOk = reject is null;
            Console.WriteLine($"   FLELEM-NULLCLEAR-OK null-clear elem  : {(flElemNullClearOk ? "PASS — a real FormID and a null-clear synonym both accepted" : $"FAIL — reject=[{reject}]")}");
        }

        // ---------- FLELEM-OK-E2E: a VALID element round-trips through the REAL create+apply path ----------
        // The no-over-reject proof in full: pre-flight accepts AND the engine writes it (the gate guards the apply path,
        // it does not block it). Create a topic + INFO whose LinkTo ReplaceAll = [a real FormID]; read it back off disk.
        bool flElemOkE2eOk = false;
        {
            string pPath = Path.Combine(tmpDir, "HcNcFlElemOk.esp");
            var specs = new[]
            {
                new WritePatchBuilder.CreateSpec { RecordType = "DialogTopic", EditorId = "HcNcFoTopic", Edits = Array.Empty<WriteRequest>() },
                new WritePatchBuilder.CreateSpec { RecordType = "DialogResponses", EditorId = "HcNcFoL1", ParentRef = "HcNcFoTopic",
                    Edits = new[] { new WriteRequest { RecordType = "DialogResponses", Path = new[] { "LinkTo" }, Verb = "ReplaceAll",
                        Values = new[] { masterTopicFk.ToString() } } } },
            };
            using var r = LoadOrderResolver.Build(new[] { mPath });
            var o = WritePatchBuilder.CreateRecords(r, rulebook, specs, pPath, extend: false);
            var linkTo = o.Success && o.Created.Count > 1 ? InfoLinkTo(pPath, o.Created[1].FormKey) : null;
            bool present = linkTo is not null && linkTo.Contains(masterTopicFk);
            flElemOkE2eOk = o.Success && present;
            Console.WriteLine($"   FLELEM-OK-E2E valid elem round-trips : {(flElemOkE2eOk ? "PASS — accepted at the gate AND written to LinkTo on disk" : $"FAIL — success={o.Success} present={present} linkTo=[{(linkTo is null ? "null" : string.Join(",", linkTo))}] err=[{o.Error}]")}");
        }

        // ---------- FLELEM-REJ-E2E: a malformed element refuses end-to-end with NO file written (gate, not apply throw) ----------
        // The "no file written" half: drive the REAL create path; the message being the PRE-FLIGHT one (not the apply
        // "Malformed FormKey string") proves the gate caught it, and RejectArm proves the all-or-nothing leaves no file.
        bool flElemRejE2eOk = RejectArm("FLELEM-REJ-E2E malformed list elem ", tmpDir, "FlElem", mPath, rulebook,
            new[]
            {
                new WritePatchBuilder.CreateSpec { RecordType = "DialogTopic", EditorId = "HcNcFeTopic", Edits = Array.Empty<WriteRequest>() },
                new WritePatchBuilder.CreateSpec { RecordType = "DialogResponses", EditorId = "HcNcFeL1", ParentRef = "HcNcFeTopic",
                    Edits = new[] { new WriteRequest { RecordType = "DialogResponses", Path = new[] { "LinkTo" }, Verb = "ReplaceAll", Values = new[] { "notaformkey" } } } },
            },
            msg => msg.Contains("Illegal FormLink element", StringComparison.OrdinalIgnoreCase));

        // ====== ELEMENT-VALUE PRESENCE — the null-PRESENCE twin of the FLELEM value-SHAPE gap above ======
        // FLELEM-REJ-ADD/GATE catch a MALFORMED (non-null) element; this catches a MISSING one. The step-4a formlink
        // check uses `is { } ev`, which SKIPS a null req.Value — so a formlink-list Add with NO value used to pass
        // pre-flight (the RED state) and then null-deref/odd-result at apply (the same accept-then-throw shape PR #76
        // closed, but for the absent-value case). The value-presence gate refuses it loud, mirroring the singular
        // Set "requires a value". Coercible-element-only + verb-scoped (Add/SetAtIndex consume the singular req.Value).

        // ---------- FLELEM-REJ-NULLADD: a MISSING element value (req.Value null) on an Add refuses at PRE-FLIGHT ----------
        // Driven parent-free against the rulebook — a non-null reject IS the gate refusal. RED before the gate:
        // Validate returned null (accepted), because the formlink step-4a `is { } ev` skips the null slot.
        bool flElemRejNullAddOk;
        {
            var req = new WriteRequest { RecordType = "DialogResponses", Path = new[] { "LinkTo" }, Verb = "Add", Value = null };
            var reject = rulebook.Validate(req);
            flElemRejNullAddOk = reject is not null && reject.Contains("requires an element value", StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"   FLELEM-REJ-NULLADD missing Add value : {(flElemRejNullAddOk ? "PASS — a null element value is refused at pre-flight (not accepted-then-null/thrown at apply)" : $"FAIL — reject=[{reject}]")}");
        }

        // ---------- FLELEM-REJ-NULLADD-PLAIN: the gate fires for a NON-formlink coercible element too (uniform scope) ----------
        // The gate keys off the element KIND (ScalarCoercible/WholeCoercible via SchemaClassifier), not formlink-ness, so a
        // plain coercible list shares the same null-presence hazard and the same fix BY CONSTRUCTION. Race.MovementTypeNames
        // is List<String> (no FormLinkTarget, no ElementTypeRef → ScalarCoercible). RED before the gate: accepted (null).
        bool flElemRejNullAddPlainOk;
        {
            var req = new WriteRequest { RecordType = "Race", Path = new[] { "MovementTypeNames" }, Verb = "Add", Value = null };
            var reject = rulebook.Validate(req);
            flElemRejNullAddPlainOk = reject is not null && reject.Contains("requires an element value", StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"   FLELEM-REJ-NULLADD-PLAIN non-formlink: {(flElemRejNullAddPlainOk ? "PASS — a null value on a plain coercible (List<String>) Add is refused too — gated uniformly" : $"FAIL — reject=[{reject}]")}");
        }

        // ---------- FLELEM-REJ-NULLSETIDX: a compose supplied with NO value on a coercible SetAtIndex still refuses ----------
        // (PR #77 review finding 1.) SetAtIndex NEVER consumes req.Struct — ApplyListVerb's SetAtIndex is unconditionally
        // Coerce(req.Value!, elem) — so a compose+no-value must NOT suppress the presence gate, else Coerce(null) hits the
        // same serialize NRE the gate exists to kill. The gate therefore has NO req.Struct guard (a coercible element is
        // never built from a struct, so a struct here is itself malformed). RED before the finding-1 fold: the gate's old
        // `&& req.Struct is null` clause let a non-null Struct skip the gate → accepted. Race.MovementTypeNames = List<String>.
        bool flElemRejNullSetIdxOk;
        {
            var req = new WriteRequest { RecordType = "Race", Path = new[] { "MovementTypeNames" }, Verb = "SetAtIndex",
                Key = "0", Value = null, Struct = new StructSpec { Type = "Keyword" } };
            var reject = rulebook.Validate(req);
            flElemRejNullSetIdxOk = reject is not null && reject.Contains("requires an element value", StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"   FLELEM-REJ-NULLSETIDX struct+no value: {(flElemRejNullSetIdxOk ? "PASS — a compose can't suppress the gate on SetAtIndex (which ignores req.Struct)" : $"FAIL — reject=[{reject}]")}");
        }

        // ---------- FLELEM-REJ-NULLADD-E2E: a missing element value refuses end-to-end with NO file written ----------
        // The "no file written" half (RejectArm): the REAL create+apply path refuses a null-value LinkTo Add and leaves
        // no patch — the pre-flight message (not an apply null/throw) proving the gate, all-or-nothing leaving nothing.
        bool flElemRejNullAddE2eOk = RejectArm("FLELEM-REJ-NULLADD-E2E missing value", tmpDir, "FlElemNull", mPath, rulebook,
            new[]
            {
                new WritePatchBuilder.CreateSpec { RecordType = "DialogTopic", EditorId = "HcNcFnTopic", Edits = Array.Empty<WriteRequest>() },
                new WritePatchBuilder.CreateSpec { RecordType = "DialogResponses", EditorId = "HcNcFnL1", ParentRef = "HcNcFnTopic",
                    Edits = new[] { new WriteRequest { RecordType = "DialogResponses", Path = new[] { "LinkTo" }, Verb = "Add", Value = null } } },
            },
            msg => msg.Contains("requires an element value", StringComparison.OrdinalIgnoreCase));

        // ====== KEY / INDEX PRESENCE — the missing-addressing-key twin of the element-VALUE-presence gate above ======
        // The value-presence gate (FLELEM-REJ-NULL*) catches a missing element VALUE; this catches a missing addressing
        // KEY/INDEX. A dict Add/Remove coerces req.Key into / against the entry (ApplyDictVerb -> Coerce(req.Key!, kType));
        // a list SetAtIndex parses req.Key as the index (ApplyListVerb -> int.Parse(req.Key!)). A MISSING key/index used
        // to slip pre-flight — VerbLegality required a key only for Set-on-dict, NOT for Add/SetAtIndex/Remove — and threw
        // UNNAMED at apply (Coerce(null) / int.Parse(null) -> the generic "internal failure" misdirection, a Q3 accept-
        // then-throw). VerbLegality now requires the key/index up front, by construction (verb x cardinality, no per-type
        // list). It is PRESENCE only — the key VALUE-shape (coercible-to-KeyType / parseable-as-int) stays the deferred
        // surface ValueLegality step-4a names. The reachability is the same the value-presence twin proved: `key` is an
        // optional string? param (WriteTools set_field / BulkOp), so ToolCallShim's required-param gate never blocks it.

        // ---------- KEYIDX-REJ-DICTADD: a dict Add with NO key refuses at PRE-FLIGHT (Class.SkillWeights=Dictionary<Skill,Byte>) ----------
        // A VALID value (Byte "5") is supplied so ONLY the missing key differs — isolates key-presence from value-presence.
        // RED before the gate: VerbLegality's Add arm returned null for any list/dict -> accepted, then Coerce(null,Skill) threw at apply.
        bool keyIdxRejDictAddOk;
        {
            var req = new WriteRequest { RecordType = "Class", Path = new[] { "SkillWeights" }, Verb = "Add", Key = null, Value = "5" };
            var reject = rulebook.Validate(req);
            keyIdxRejDictAddOk = reject is not null && reject.Contains("requires a key", StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"   KEYIDX-REJ-DICTADD missing dict key  : {(keyIdxRejDictAddOk ? "PASS — a dict Add with no key is refused at pre-flight (not accepted-then-thrown at apply)" : $"FAIL — reject=[{reject}]")}");
        }

        // ---------- KEYIDX-REJ-DICTREMOVE: a dict Remove with NO key refuses at PRE-FLIGHT (it identifies the entry BY key) ----------
        // RED before the gate: VerbLegality's Remove arm returned null for list/dict -> accepted, then Coerce(null,Skill) threw at apply.
        bool keyIdxRejDictRemoveOk;
        {
            var req = new WriteRequest { RecordType = "Class", Path = new[] { "SkillWeights" }, Verb = "Remove", Key = null };
            var reject = rulebook.Validate(req);
            keyIdxRejDictRemoveOk = reject is not null && reject.Contains("requires a key", StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"   KEYIDX-REJ-DICTREMOVE missing dict key: {(keyIdxRejDictRemoveOk ? "PASS — a dict Remove with no key is refused at pre-flight" : $"FAIL — reject=[{reject}]")}");
        }

        // ---------- KEYIDX-REJ-SETIDX: a list SetAtIndex with NO index refuses at PRE-FLIGHT (Race.MovementTypeNames=List<String>) ----------
        // A VALID value is supplied so ONLY the missing index differs. RED before the gate: VerbLegality's SetAtIndex arm
        // returned null for any list -> accepted, then int.Parse(null) threw ArgumentNullException at apply.
        bool keyIdxRejSetIdxOk;
        {
            var req = new WriteRequest { RecordType = "Race", Path = new[] { "MovementTypeNames" }, Verb = "SetAtIndex", Key = null, Value = "MT_Walk" };
            var reject = rulebook.Validate(req);
            keyIdxRejSetIdxOk = reject is not null && reject.Contains("requires an index", StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"   KEYIDX-REJ-SETIDX missing list index : {(keyIdxRejSetIdxOk ? "PASS — a list SetAtIndex with no index is refused at pre-flight" : $"FAIL — reject=[{reject}]")}");
        }

        // ---------- KEYIDX-OK-LISTREMOVE: a keyless list Remove + a value is STILL accepted (no over-reject) ----------
        // The gate is DICT-only for Remove: a list Remove is by-index-OR-by-value (ApplyListVerb), so a null key legally
        // falls back to remove-by-value. Proves the dict-scoping doesn't over-reach to lists. Accepted before AND after.
        bool keyIdxOkListRemoveOk;
        {
            var req = new WriteRequest { RecordType = "Race", Path = new[] { "MovementTypeNames" }, Verb = "Remove", Key = null, Value = "MT_Walk" };
            var reject = rulebook.Validate(req);
            keyIdxOkListRemoveOk = reject is null;
            Console.WriteLine($"   KEYIDX-OK-LISTREMOVE keyless list rm : {(keyIdxOkListRemoveOk ? "PASS — a keyless list Remove (by value) is NOT over-rejected" : $"FAIL — reject=[{reject}]")}");
        }

        // ---------- KEYIDX-REJ-SETIDX-E2E: a keyless list SetAtIndex refuses end-to-end with NO file written (gate, not apply throw) ----------
        // The "no file written" half (RejectArm): the REAL create+apply path refuses a no-index LinkTo SetAtIndex and
        // leaves no patch — the PRE-FLIGHT message ('requires an index'), not the apply int.Parse(null) throw, proving the gate.
        bool keyIdxRejSetIdxE2eOk = RejectArm("KEYIDX-REJ-SETIDX-E2E no index    ", tmpDir, "KeyIdxNoIdx", mPath, rulebook,
            new[]
            {
                new WritePatchBuilder.CreateSpec { RecordType = "DialogTopic", EditorId = "HcNcKiTopic", Edits = Array.Empty<WriteRequest>() },
                new WritePatchBuilder.CreateSpec { RecordType = "DialogResponses", EditorId = "HcNcKiL1", ParentRef = "HcNcKiTopic",
                    Edits = new[] { new WriteRequest { RecordType = "DialogResponses", Path = new[] { "LinkTo" }, Verb = "SetAtIndex", Key = null, Value = "012345:Skyrim.esm" } } },
            },
            msg => msg.Contains("requires an index", StringComparison.OrdinalIgnoreCase));

        // ====== KEY / INDEX VALUE-SHAPE — the malformed-key/index twin of the PRESENCE gate above ======
        // The presence gate (KEYIDX-*) catches a MISSING key/index; this catches a PRESENT-but-MALFORMED one. A dict
        // Set/Add/Remove coerces req.Key into the entry (ApplyDictVerb -> Coerce(req.Key!, KeyType)) and Merge/ReplaceAll
        // coerce each Entries key; a list SetAtIndex/Remove parses req.Key as the index (ApplyListVerb -> int.Parse).
        // ValueLegality now gates the SHAPE: dict keys via the SAME coercibility apply uses (the key's real CLR type from
        // the field's dict AQ — EVERY kind, not just enum-by-name), list indices via IsValidListIndexValue (non-negative
        // int; in-range left to apply). Most arms drive the rulebook DIRECTLY (a non-null reject IS the gate refusal).

        // ---------- KEYSHAPE-REJ-DICTADD: a dict Add with a non-coercible ENUM key refuses at PRE-FLIGHT ----------
        // A VALID value (Byte "5") so ONLY the key differs. RED before: no Add key-shape gate existed -> accepted, then
        // Coerce("notaskill", Skill) threw at apply.
        bool keyShapeRejDictAddOk;
        {
            var req = new WriteRequest { RecordType = "Class", Path = new[] { "SkillWeights" }, Verb = "Add", Key = "notaskill", Value = "5" };
            var reject = rulebook.Validate(req);
            keyShapeRejDictAddOk = reject is not null && reject.Contains("dict key", StringComparison.OrdinalIgnoreCase)
                && reject.Contains("not a legal Skill", StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"   KEYSHAPE-REJ-DICTADD bad enum key   : {(keyShapeRejDictAddOk ? "PASS — a non-coercible dict Add key is refused at pre-flight (not accepted-then-thrown at apply)" : $"FAIL — reject=[{reject}]")}");
        }

        // ---------- KEYSHAPE-REJ-DICTREMOVE: a dict Remove with a non-coercible key refuses (entry identified BY key) ----------
        bool keyShapeRejDictRemoveOk;
        {
            var req = new WriteRequest { RecordType = "Class", Path = new[] { "SkillWeights" }, Verb = "Remove", Key = "notaskill" };
            var reject = rulebook.Validate(req);
            keyShapeRejDictRemoveOk = reject is not null && reject.Contains("not a legal Skill", StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"   KEYSHAPE-REJ-DICTREMOVE bad key     : {(keyShapeRejDictRemoveOk ? "PASS — a non-coercible dict Remove key is refused at pre-flight" : $"FAIL — reject=[{reject}]")}");
        }

        // ---------- KEYSHAPE-REJ-MERGEKEY: a Merge with a non-coercible Entries KEY refuses (Merge/ReplaceAll keys coerce too) ----------
        // Values are valid + carry no @ (so the sibling-collection gate passes through to the entries-key scan). RED before: accepted.
        bool keyShapeRejMergeKeyOk;
        {
            var req = new WriteRequest { RecordType = "Class", Path = new[] { "SkillWeights" }, Verb = "Merge",
                Entries = new Dictionary<string, string> { ["notaskill"] = "5" } };
            var reject = rulebook.Validate(req);
            keyShapeRejMergeKeyOk = reject is not null && reject.Contains("not a legal Skill", StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"   KEYSHAPE-REJ-MERGEKEY bad entry key : {(keyShapeRejMergeKeyOk ? "PASS — a non-coercible Merge entries key is refused too (Merge/ReplaceAll keys coerce)" : $"FAIL — reject=[{reject}]")}");
        }

        // ---------- KEYSHAPE-REJ-SBYTE: the ONE non-enum-keyed dict — by-construction, not enum-only ----------
        // Package.Data = Dictionary<sbyte,APackageData>. A Remove (no value, sidesteps the composable-element value path)
        // with a non-numeric key refuses 'does not coerce to sbyte' — the gate resolves the key's REAL CLR type (sbyte) from
        // the dict AQ, so it catches a kind the old enum-catalog-name check NEVER could. RED before: accepted (the sbyte hole).
        bool keyShapeRejSbyteOk;
        {
            var req = new WriteRequest { RecordType = "Package", Path = new[] { "Data" }, Verb = "Remove", Key = "notanumber" };
            var reject = rulebook.Validate(req);
            keyShapeRejSbyteOk = reject is not null && reject.Contains("does not coerce to sbyte", StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"   KEYSHAPE-REJ-SBYTE non-enum key     : {(keyShapeRejSbyteOk ? "PASS — a non-coercible sbyte key is refused by construction (real CLR type, not enum-name only)" : $"FAIL — reject=[{reject}]")}");
        }

        // ---------- KEYSHAPE-REJ-SETIDX: a list SetAtIndex with a non-integer index refuses ----------
        // A VALID value so ONLY the index differs. RED before: accepted, then int.Parse("abc") threw FormatException at apply.
        bool keyShapeRejSetIdxOk;
        {
            var req = new WriteRequest { RecordType = "Race", Path = new[] { "MovementTypeNames" }, Verb = "SetAtIndex", Key = "abc", Value = "MT_Walk" };
            var reject = rulebook.Validate(req);
            keyShapeRejSetIdxOk = reject is not null && reject.Contains("Illegal list index", StringComparison.OrdinalIgnoreCase)
                && reject.Contains("non-negative integer", StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"   KEYSHAPE-REJ-SETIDX non-int index   : {(keyShapeRejSetIdxOk ? "PASS — a non-integer list index is refused at pre-flight" : $"FAIL — reject=[{reject}]")}");
        }

        // ---------- KEYSHAPE-REJ-NEGIDX: a list SetAtIndex with a NEGATIVE index refuses (the >=0 decision) ----------
        // int.Parse accepts "-1" but the indexer throws ArgumentOutOfRangeException — so the gate pre-checks >= 0. RED before: accepted.
        bool keyShapeRejNegIdxOk;
        {
            var req = new WriteRequest { RecordType = "Race", Path = new[] { "MovementTypeNames" }, Verb = "SetAtIndex", Key = "-1", Value = "MT_Walk" };
            var reject = rulebook.Validate(req);
            keyShapeRejNegIdxOk = reject is not null && reject.Contains("non-negative integer", StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"   KEYSHAPE-REJ-NEGIDX negative index  : {(keyShapeRejNegIdxOk ? "PASS — a negative index is refused too (parses but the indexer would throw)" : $"FAIL — reject=[{reject}]")}");
        }

        // ---------- KEYSHAPE-REJ-LISTREMOVE-IDX: a list Remove with a present non-integer index refuses (the RemoveAt path) ----------
        // A present key on list Remove is interpreted as the index (ApplyListVerb -> RemoveAt(int.Parse(req.Key))). RED before: accepted.
        bool keyShapeRejListRemoveIdxOk;
        {
            var req = new WriteRequest { RecordType = "Race", Path = new[] { "MovementTypeNames" }, Verb = "Remove", Key = "abc" };
            var reject = rulebook.Validate(req);
            keyShapeRejListRemoveIdxOk = reject is not null && reject.Contains("Illegal list index", StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"   KEYSHAPE-REJ-LISTREMOVE-IDX non-int : {(keyShapeRejListRemoveIdxOk ? "PASS — a non-integer index on list Remove (by-index) is refused too" : $"FAIL — reject=[{reject}]")}");
        }

        // ---------- KEYSHAPE-OK-DICTADD: a dict Add with a VALID enum key + value is STILL accepted (no over-reject) ----------
        bool keyShapeOkDictAddOk;
        {
            var req = new WriteRequest { RecordType = "Class", Path = new[] { "SkillWeights" }, Verb = "Add", Key = "OneHanded", Value = "5" };
            var reject = rulebook.Validate(req);
            keyShapeOkDictAddOk = reject is null;
            Console.WriteLine($"   KEYSHAPE-OK-DICTADD valid key+value : {(keyShapeOkDictAddOk ? "PASS — a valid dict Add (key+value) is NOT over-rejected" : $"FAIL — reject=[{reject}]")}");
        }

        // ---------- KEYSHAPE-OK-SETIDX: a list SetAtIndex with a VALID index is STILL accepted (no over-reject) ----------
        bool keyShapeOkSetIdxOk;
        {
            var req = new WriteRequest { RecordType = "Race", Path = new[] { "MovementTypeNames" }, Verb = "SetAtIndex", Key = "0", Value = "MT_Walk" };
            var reject = rulebook.Validate(req);
            keyShapeOkSetIdxOk = reject is null;
            Console.WriteLine($"   KEYSHAPE-OK-SETIDX valid index      : {(keyShapeOkSetIdxOk ? "PASS — a valid list index (0) is NOT over-rejected" : $"FAIL — reject=[{reject}]")}");
        }

        // ---------- KEYSHAPE-OK-NUMENUM-SET: a dict Set with a NUMERIC enum key ('3') is accepted — gate matches apply ----------
        // apply's Coerce("3", Skill) = Enum.Parse accepts the underlying-numeric form, so the gate must too. The reconciled
        // Set path now resolves the key's real enum type (TryCoerce), not the old NAME-only catalog check. RED before:
        // REJECTED ('3' is not a NAMED Skill) — the gate/apply drift this fix closes by reusing the engine recognizer.
        bool keyShapeOkNumEnumSetOk;
        {
            var req = new WriteRequest { RecordType = "Class", Path = new[] { "SkillWeights" }, Verb = "Set", Key = "3", Value = "5" };
            var reject = rulebook.Validate(req);
            keyShapeOkNumEnumSetOk = reject is null;
            Console.WriteLine($"   KEYSHAPE-OK-NUMENUM-SET numeric key : {(keyShapeOkNumEnumSetOk ? "PASS — a numeric enum key '3' (which apply accepts) is no longer over-rejected" : $"FAIL — reject=[{reject}]")}");
        }

        // ---------- KEYSHAPE-REJ-E2E: a malformed list index refuses end-to-end with NO file written (gate, not apply throw) ----------
        // The "no file written" half (RejectArm): a real create+apply with a non-integer LinkTo SetAtIndex index leaves no
        // patch — the PRE-FLIGHT message ('Illegal list index'), not the apply int.Parse throw, proving the gate. A valid
        // value is supplied so ONLY the index is at fault (the index gate runs before the value-presence gate).
        bool keyShapeRejE2eOk = RejectArm("KEYSHAPE-REJ-E2E bad list index   ", tmpDir, "KeyShapeIdx", mPath, rulebook,
            new[]
            {
                new WritePatchBuilder.CreateSpec { RecordType = "DialogTopic", EditorId = "HcNcKsTopic", Edits = Array.Empty<WriteRequest>() },
                new WritePatchBuilder.CreateSpec { RecordType = "DialogResponses", EditorId = "HcNcKsL1", ParentRef = "HcNcKsTopic",
                    Edits = new[] { new WriteRequest { RecordType = "DialogResponses", Path = new[] { "LinkTo" }, Verb = "SetAtIndex", Key = "abc", Value = "012345:Skyrim.esm" } } },
            },
            msg => msg.Contains("Illegal list index", StringComparison.OrdinalIgnoreCase));

        // ====== GAP 1 — mid-path dict-key VALUE-SHAPE (the one-segment-up twin of the leaf KEYSHAPE gate above) ======
        // KEYSHAPE-REJ-SBYTE gates a dict key at the LEAF; this gates a dict key in a MID-PATH hop ('Data[key].field').
        // ValidateFromType's bracketed mid-path branch checked the key via CheckValue WITHOUT the key's AQ, so it fell to
        // the enum-catalog-by-name fallback and missed the lone non-enum key type (Package.Data = Dictionary<sbyte,...>).
        // A malformed mid-path key ('Data[notasbyte].Name') was accepted then threw FormatException at apply
        // (StepIntoElement -> Coerce(key, sbyte)). The fix passes DictKeyType(field)?.AQ — the SAME recognizer pair the
        // leaf step-4-key block uses — so mid-path and leaf can't drift.

        // ---------- GAP1-REJ-MIDKEY-SBYTE: a malformed sbyte key in a MID-PATH hop refuses at PRE-FLIGHT ----------
        // Package.Data = Dictionary<sbyte,APackageData> is the ONLY mid-path-navigable (struct/poly-valued) dict. A valid
        // APackageData base field ('Name', writable string) as the leaf so ONLY the mid-path key is at fault. RED before:
        // accepted (the mid-path CheckValue had no AQ -> 'sbyte' is not a catalog enum -> returns null).
        bool gap1RejMidKeySbyteOk;
        {
            var req = new WriteRequest { RecordType = "Package", Path = new[] { "Data[notasbyte]", "Name" }, Verb = "Set", Value = "houseCARL" };
            var reject = rulebook.Validate(req);
            gap1RejMidKeySbyteOk = reject is not null && reject.Contains("does not coerce to sbyte", StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"   GAP1-REJ-MIDKEY-SBYTE bad mid key  : {(gap1RejMidKeySbyteOk ? "PASS — a malformed sbyte mid-path key is refused at pre-flight (real CLR type, not enum-name only)" : $"FAIL — reject=[{reject}]")}");
        }

        // ---------- GAP1-OK-MIDKEY: a VALID sbyte mid-path key + valid leaf Set stays accepted (no over-reject) ----------
        bool gap1OkMidKeyOk;
        {
            var req = new WriteRequest { RecordType = "Package", Path = new[] { "Data[0]", "Name" }, Verb = "Set", Value = "houseCARL" };
            var reject = rulebook.Validate(req);
            gap1OkMidKeyOk = reject is null;
            Console.WriteLine($"   GAP1-OK-MIDKEY valid sbyte mid key : {(gap1OkMidKeyOk ? "PASS — a valid sbyte mid-path key (Data[0]) is NOT over-rejected" : $"FAIL — reject=[{reject}]")}");
        }

        // ---------- GAP1-REJ-E2E: a malformed mid-path dict key refuses end-to-end with NO file written (review hardening) ----------
        // Package is a flat (Kind=record) createable record; the create's own op edits Data[notasbyte].Name. A fresh
        // Package's Data is an empty NON-null dict (Mutagen initializes it), so apply DOES reach the key coerce — WITHOUT
        // the fix, pre-flight accepts and apply throws the genuine sbyte FormatException at StepIntoElement ->
        // Coerce('notasbyte', sbyte) (RED-proven verbatim). With the fix, the PRE-FLIGHT message ('does not coerce to
        // sbyte'), not the apply throw, refuses it with NO file written — the faithful mid-path-key accept-then-throw, end to end.
        bool gap1RejE2eOk = RejectArm("GAP1-REJ-E2E bad mid-path key      ", tmpDir, "Gap1E2E", mPath, rulebook,
            new[]
            {
                new WritePatchBuilder.CreateSpec { RecordType = "Package", EditorId = "HcNcG1Pack",
                    Edits = new[] { new WriteRequest { RecordType = "Package", Path = new[] { "Data[notasbyte]", "Name" }, Verb = "Set", Value = "houseCARL" } } },
            },
            msg => msg.Contains("does not coerce to sbyte", StringComparison.OrdinalIgnoreCase));

        // ====== GAP 1 (cont.) — mid-path LIST-index VALUE-SHAPE (the list twin of the dict-key mid-path gate above) ======
        // ValidateFromType's bracketed MID-PATH list branch checked the index with a bare int.TryParse, which ACCEPTS a
        // negative ('-1' parses fine) — but apply's StepIntoElement list branch requires idx >= 0 and throws a PLAIN
        // InvalidOperationException (NOT an ExpectedApplyRejectionException, by design — it's a SHAPE error, not live
        // state). So a negative mid-path index ('Conditions[-1].field') was accepted at pre-flight then threw at apply
        // and got the misleading "real inconsistency" wrapper. The LEAF list index was already reconciled onto
        // WriteEngine.IsValidListIndexValue (KEYSHAPE-REJ-NEGIDX, parseable non-negative int32); the mid-path hop drifted.
        // The fix points the mid-path check onto that SAME recognizer, so the leaf and the mid-path hop can't drift —
        // after it, the apply-side non-negative throw is unreachable on the gated path (both non-integer AND negative are
        // caught at pre-flight; the apply throw stays as defense-in-depth, shared with the READ path).

        // ---------- GAP1-REJ-MIDLISTIDX-NEG: a NEGATIVE mid-path list index refuses at PRE-FLIGHT ----------
        // Faction.Conditions = List<Condition> is a struct-element (Condition Kind=polymorphic-base, NOT record) list,
        // hence mid-path-navigable; 'CompareOperator' (a valid enum 'EqualTo') is the writable leaf so ONLY the index is
        // at fault. RED before: accepted (the bare int.TryParse passed '-1'), then apply threw the plain non-negative IOE
        // under the inconsistency wrapper.
        bool gap1RejMidListIdxNegOk;
        {
            var req = new WriteRequest { RecordType = "Faction", Path = new[] { "Conditions[-1]", "CompareOperator" }, Verb = "Set", Value = "EqualTo" };
            var reject = rulebook.Validate(req);
            gap1RejMidListIdxNegOk = reject is not null && reject.Contains("non-negative integer", StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"   GAP1-REJ-MIDLISTIDX-NEG neg index  : {(gap1RejMidListIdxNegOk ? "PASS — a negative mid-path list index is refused at pre-flight (reconciled onto IsValidListIndexValue; no leaf/mid-path drift)" : $"FAIL — reject=[{reject}]")}");
        }

        // ---------- GAP1-OK-MIDLISTIDX: a VALID (non-negative) mid-path list index + valid leaf Set stays accepted ----------
        bool gap1OkMidListIdxOk;
        {
            var req = new WriteRequest { RecordType = "Faction", Path = new[] { "Conditions[0]", "CompareOperator" }, Verb = "Set", Value = "EqualTo" };
            var reject = rulebook.Validate(req);
            gap1OkMidListIdxOk = reject is null;
            Console.WriteLine($"   GAP1-OK-MIDLISTIDX valid index     : {(gap1OkMidListIdxOk ? "PASS — a valid non-negative mid-path list index (Conditions[0]) is NOT over-rejected" : $"FAIL — reject=[{reject}]")}");
        }

        // ---------- GAP1-REJ-NEGIDX-E2E: a negative mid-path list index refuses end-to-end with NO file written ----------
        // The apply-time negative-index throw only bites a NON-NULL list — on a list that is null (a fresh record's
        // default), StepIntoElement's absent-collection ExpectedApplyRejection fires FIRST (already clean), masking the
        // bug. So op1 composes a ConditionFloat to MATERIALIZE Conditions (non-null, 1 element; the proven-good list
        // compose of GAP3-OK-LIST-UNCHANGED), then op2 navigates Conditions[-1] — faithfully reproducing the bug:
        // WITHOUT the fix, pre-flight accepts both, apply lands op1 then op2 hits StepIntoElement's list branch and throws
        // the PLAIN non-negative IOE → the misleading "real inconsistency" wrapper (RED — the anti-wrapper asserts catch
        // it). WITH the fix, Phase-1 pre-flight rejects op2 (the whole all-or-nothing create is refused, NOTHING created,
        // op1 never lands) with the clean gate message ('non-negative integer'), NO file written — model: GAP1-REJ-E2E.
        bool gap1RejNegIdxE2eOk = RejectArm("GAP1-REJ-NEGIDX-E2E neg mid index ", tmpDir, "Gap1NegIdx", mPath, rulebook,
            new[]
            {
                new WritePatchBuilder.CreateSpec { RecordType = "Faction", EditorId = "HcNcG1NegIdx",
                    Edits = new[]
                    {
                        new WriteRequest { RecordType = "Faction", Path = new[] { "Conditions" }, Verb = "Add", Struct = new StructSpec { Type = "ConditionFloat" } },
                        new WriteRequest { RecordType = "Faction", Path = new[] { "Conditions[-1]", "CompareOperator" }, Verb = "Set", Value = "EqualTo" },
                    } },
            },
            msg => msg.Contains("non-negative integer", StringComparison.OrdinalIgnoreCase)
                && !msg.Contains("real inconsistency", StringComparison.OrdinalIgnoreCase)
                && !msg.Contains("pre-flight ACCEPTED", StringComparison.OrdinalIgnoreCase));

        // ====== GAP 2 — NON-FORMLINK coercible collection ELEMENT VALUE-SHAPE ======
        // The value twin of step-4a (formlink elements) and of the dict-Set value block. A non-null, non-formlink,
        // MALFORMED coercible element value on list Add/SetAtIndex/ReplaceAll/Remove-by-value and dict Add/Merge/ReplaceAll
        // passed pre-flight then threw UNNAMED at apply (Coerce -> float.Parse/byte.Parse). dict-Set value was already
        // gated. The new step-4b block mirrors the dict-Set CheckValue across those verbs/slots, scoped to
        // IsValueCoercibleElement && FormLinkTarget is null (formlink elements keep step-4a), and verb/key-faithful to
        // which slot apply actually coerces (so a Remove-BY-INDEX carrying a stray value is NOT over-rejected).

        // ---------- GAP2-REJ-DICTADD: dict Add, valid key, malformed value (Class.SkillWeights=Dictionary<Skill,Byte>) ----------
        bool gap2RejDictAddOk;
        {
            var req = new WriteRequest { RecordType = "Class", Path = new[] { "SkillWeights" }, Verb = "Add", Key = "OneHanded", Value = "notabyte" };
            var reject = rulebook.Validate(req);
            gap2RejDictAddOk = reject is not null && reject.Contains("does not coerce to Byte", StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"   GAP2-REJ-DICTADD bad dict value     : {(gap2RejDictAddOk ? "PASS — a malformed dict Add value is refused at pre-flight (only dict Set value was gated before)" : $"FAIL — reject=[{reject}]")}");
        }

        // ---------- GAP2-REJ-LISTADD: list Add malformed value (MusicTrack.CuePoints=List<Single>) ----------
        bool gap2RejListAddOk;
        {
            var req = new WriteRequest { RecordType = "MusicTrack", Path = new[] { "CuePoints" }, Verb = "Add", Value = "notafloat" };
            var reject = rulebook.Validate(req);
            gap2RejListAddOk = reject is not null && reject.Contains("does not coerce to Single", StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"   GAP2-REJ-LISTADD bad list value     : {(gap2RejListAddOk ? "PASS — a malformed list Add value is refused at pre-flight" : $"FAIL — reject=[{reject}]")}");
        }

        // ---------- GAP2-REJ-LISTREPLACEALL: bad value PAST a good one (per-element scan names the bad one) ----------
        bool gap2RejListReplaceAllOk;
        {
            var req = new WriteRequest { RecordType = "MusicTrack", Path = new[] { "CuePoints" }, Verb = "ReplaceAll", Values = new[] { "1.5", "notafloat" } };
            var reject = rulebook.Validate(req);
            gap2RejListReplaceAllOk = reject is not null && reject.Contains("does not coerce to Single", StringComparison.OrdinalIgnoreCase)
                && reject.Contains("notafloat", StringComparison.Ordinal);
            Console.WriteLine($"   GAP2-REJ-LISTREPLACEALL bad in list : {(gap2RejListReplaceAllOk ? "PASS — a bad ReplaceAll value is caught past a valid one, named" : $"FAIL — reject=[{reject}]")}");
        }

        // ---------- GAP2-REJ-LISTREMOVE: Remove-by-value (Key null) malformed value ----------
        bool gap2RejListRemoveOk;
        {
            var req = new WriteRequest { RecordType = "MusicTrack", Path = new[] { "CuePoints" }, Verb = "Remove", Value = "notafloat" };
            var reject = rulebook.Validate(req);
            gap2RejListRemoveOk = reject is not null && reject.Contains("does not coerce to Single", StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"   GAP2-REJ-LISTREMOVE bad remove value: {(gap2RejListRemoveOk ? "PASS — a malformed Remove-by-value is refused at pre-flight" : $"FAIL — reject=[{reject}]")}");
        }

        // ---------- GAP2-REJ-DICTMERGE: Merge entries bad value (valid key, non-@ value) ----------
        bool gap2RejDictMergeOk;
        {
            var req = new WriteRequest { RecordType = "Class", Path = new[] { "SkillWeights" }, Verb = "Merge",
                Entries = new Dictionary<string, string> { ["OneHanded"] = "notabyte" } };
            var reject = rulebook.Validate(req);
            gap2RejDictMergeOk = reject is not null && reject.Contains("does not coerce to Byte", StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"   GAP2-REJ-DICTMERGE bad merge value  : {(gap2RejDictMergeOk ? "PASS — a malformed Merge entries value is refused at pre-flight" : $"FAIL — reject=[{reject}]")}");
        }

        // ---------- GAP2-OK-VALID: valid coercible values stay accepted (no over-reject) ----------
        bool gap2OkValidOk;
        {
            var r1 = rulebook.Validate(new WriteRequest { RecordType = "MusicTrack", Path = new[] { "CuePoints" }, Verb = "Add", Value = "1.5" });
            var r2 = rulebook.Validate(new WriteRequest { RecordType = "Class", Path = new[] { "SkillWeights" }, Verb = "Add", Key = "OneHanded", Value = "5" });
            gap2OkValidOk = r1 is null && r2 is null;
            Console.WriteLine($"   GAP2-OK-VALID valid values accepted : {(gap2OkValidOk ? "PASS — valid list+dict element values are NOT over-rejected" : $"FAIL — r1=[{r1}] r2=[{r2}]")}");
        }

        // ---------- GAP2-OK-REMOVE-BYINDEX: Remove BY INDEX (Key present) ignores its value at apply -> not over-rejected ----------
        // ApplyListVerb Remove with a Key is RemoveAt(int.Parse(key)) — the value is apply-irrelevant. The step-4b value
        // check is verb/key-faithful (Remove value only when Key is null), so a stray value here must NOT reject.
        bool gap2OkRemoveByIndexOk;
        {
            var req = new WriteRequest { RecordType = "MusicTrack", Path = new[] { "CuePoints" }, Verb = "Remove", Key = "0", Value = "notafloat" };
            var reject = rulebook.Validate(req);
            gap2OkRemoveByIndexOk = reject is null;
            Console.WriteLine($"   GAP2-OK-REMOVE-BYINDEX stray value  : {(gap2OkRemoveByIndexOk ? "PASS — a by-index Remove with a stray value is NOT over-rejected (apply ignores it)" : $"FAIL — reject=[{reject}]")}");
        }

        // ---------- GAP2-FORMLINK-ROUTE: formlink elements still take step-4a, not step-4b (the partition holds) ----------
        // A formlink list (Weapon.Keywords, FormLinkTarget set) is EXCLUDED from step-4b's scope, so a valid FormID stays
        // accepted and a malformed one rejects with step-4a's "Illegal FormLink element" (NOT "does not coerce") — proving
        // the two value-shape blocks partition coercible elements with no overlap and no gap.
        bool gap2FormlinkRouteOk;
        {
            var ok = rulebook.Validate(new WriteRequest { RecordType = "Weapon", Path = new[] { "Keywords" }, Verb = "Add", Value = "012345:Skyrim.esm" });
            var bad = rulebook.Validate(new WriteRequest { RecordType = "Weapon", Path = new[] { "Keywords" }, Verb = "Add", Value = "notaformkey" });
            gap2FormlinkRouteOk = ok is null && bad is not null
                && bad.Contains("Illegal FormLink element", StringComparison.OrdinalIgnoreCase)
                && !bad.Contains("does not coerce", StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"   GAP2-FORMLINK-ROUTE step-4a intact  : {(gap2FormlinkRouteOk ? "PASS — formlink elements still route through step-4a, not step-4b" : $"FAIL — ok=[{ok}] bad=[{bad}]")}");
        }

        // ---------- GAP2-OK-OFFCARD-SLOT: a stray off-cardinality slot apply IGNORES is NOT over-rejected (review polish) ----------
        // ApplyListVerb ReplaceAll consumes req.Values only and ignores req.Entries; step-4b is slot-faithful (Values only
        // for a list, Entries only for a dict), so a malformed stray Entries on a LIST ReplaceAll does not over-reject.
        // RED before the slot-scoping fix: the cardinality-blind Entries loop scanned it -> rejected (gate stricter than apply).
        bool gap2OkOffcardSlotOk;
        {
            var req = new WriteRequest { RecordType = "MusicTrack", Path = new[] { "CuePoints" }, Verb = "ReplaceAll",
                Values = new[] { "1.5" }, Entries = new Dictionary<string, string> { ["x"] = "notafloat" } };
            var reject = rulebook.Validate(req);
            gap2OkOffcardSlotOk = reject is null;
            Console.WriteLine($"   GAP2-OK-OFFCARD-SLOT stray entries  : {(gap2OkOffcardSlotOk ? "PASS — a stray off-cardinality slot (Entries on a list ReplaceAll) apply ignores is NOT over-rejected" : $"FAIL — reject=[{reject}]")}");
        }

        // ---------- GAP2-REJ-E2E: a malformed list value refuses end-to-end with NO file written (gate, not apply throw) ----------
        // MusicTrack is a flat (Kind=record) createable record — no parent needed. The PRE-FLIGHT message ('does not
        // coerce to Single'), not the apply float.Parse throw, proves the gate; RejectArm proves no file written.
        bool gap2RejE2eOk = RejectArm("GAP2-REJ-E2E bad list value       ", tmpDir, "Gap2", mPath, rulebook,
            new[]
            {
                new WritePatchBuilder.CreateSpec { RecordType = "MusicTrack", EditorId = "HcNcG2Mtrk",
                    Edits = new[] { new WriteRequest { RecordType = "MusicTrack", Path = new[] { "CuePoints" }, Verb = "Add", Value = "notafloat" } } },
            },
            msg => msg.Contains("does not coerce to Single", StringComparison.OrdinalIgnoreCase));

        // ====== G6 — RECORD-ELEMENT collection VERBS (Add/SetAtIndex/ReplaceAll redirect to create_record) ======
        // A list/dict whose element is an owned child RECORD (DialogTopic.Responses -> DialogResponses) is neither
        // coercible, composable, nor formlink, so a collection verb fell through ValueLegality to ACCEPT then threw at
        // apply (BuildStruct -> CompositionRequiredException, or Coerce -> "No coercion rule"). The new step-4-rec branch
        // redirects to the record axis (create_record/bulk_create parent=). Verb-scoped to Add/SetAtIndex/ReplaceAll; a
        // record Remove BY INDEX stays throw-free/accepted, and a record Remove BY VALUE is the non-plain-value Remove
        // surface closed with G7's unified Remove-by-value reject.

        // ---------- G6-REJ-RECORD-ADD: an Add (compose) on a record-element list refuses, naming create_record ----------
        bool g6RejRecordAddOk;
        {
            var req = new WriteRequest { RecordType = "DialogTopic", Path = new[] { "Responses" }, Verb = "Add", Struct = new StructSpec { Type = "DialogResponses" } };
            var reject = rulebook.Validate(req);
            g6RejRecordAddOk = reject is not null
                && reject.Contains("created on its own (the record axis)", StringComparison.OrdinalIgnoreCase)
                && reject.Contains("housecarl_create_record", StringComparison.OrdinalIgnoreCase)
                && reject.Contains("Responses", StringComparison.Ordinal);
            Console.WriteLine($"   G6-REJ-RECORD-ADD record-elem Add   : {(g6RejRecordAddOk ? "PASS — a record-element Add is refused, redirected to create_record (RED before: accepted then CompositionRequiredException at apply)" : $"FAIL — reject=[{reject}]")}");
        }

        // ---------- G6-REJ-RECORD-REPLACEALL: ReplaceAll on a record-element list refuses too (verb coverage) ----------
        bool g6RejRecordReplaceAllOk;
        {
            var req = new WriteRequest { RecordType = "DialogTopic", Path = new[] { "Responses" }, Verb = "ReplaceAll", Values = new[] { "012345:Skyrim.esm" } };
            var reject = rulebook.Validate(req);
            g6RejRecordReplaceAllOk = reject is not null && reject.Contains("created on its own (the record axis)", StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"   G6-REJ-RECORD-REPLACEALL           : {(g6RejRecordReplaceAllOk ? "PASS — a record-element ReplaceAll is refused too (RED before: accepted)" : $"FAIL — reject=[{reject}]")}");
        }

        // ---------- G6-OK-REMOVE-BYINDEX: a record-element Remove BY INDEX (RemoveAt) stays accepted (no over-reject) ----------
        bool g6OkRemoveByIndexOk;
        {
            var req = new WriteRequest { RecordType = "DialogTopic", Path = new[] { "Responses" }, Verb = "Remove", Key = "0" };
            var reject = rulebook.Validate(req);
            g6OkRemoveByIndexOk = reject is null;
            Console.WriteLine($"   G6-OK-REMOVE-BYINDEX record rm idx  : {(g6OkRemoveByIndexOk ? "PASS — a record-element Remove by index (RemoveAt) is throw-free and NOT over-rejected" : $"FAIL — reject=[{reject}]")}");
        }

        // ---------- G6-OK-STRUCT-UNCHANGED: a struct-element Add still composes (Record branch doesn't bleed onto Struct) ----------
        bool g6OkStructUnchangedOk;
        {
            var req = new WriteRequest { RecordType = "Faction", Path = new[] { "Ranks" }, Verb = "Add", Struct = new StructSpec { Type = "Rank" } };
            var reject = rulebook.Validate(req);
            g6OkStructUnchangedOk = reject is null;
            Console.WriteLine($"   G6-OK-STRUCT-UNCHANGED struct Add   : {(g6OkStructUnchangedOk ? "PASS — a struct-element Add (Faction.Ranks) still composes (Record branch is mutually exclusive)" : $"FAIL — reject=[{reject}]")}");
        }

        // ---------- G6-REJ-E2E: a record-element Add refuses end-to-end with NO file written (gate, not apply throw) ----------
        // A FLAT DialogTopic create whose OWN op does the bad record-element Add (NOT a parent= nested create — that path
        // is disjoint and legitimately works). The PRE-FLIGHT message, not the apply CompositionRequiredException, proves the gate.
        bool g6RejE2eOk = RejectArm("G6-REJ-E2E record-element Add      ", tmpDir, "G6", mPath, rulebook,
            new[]
            {
                new WritePatchBuilder.CreateSpec { RecordType = "DialogTopic", EditorId = "HcNcG6Topic",
                    Edits = new[] { new WriteRequest { RecordType = "DialogTopic", Path = new[] { "Responses" }, Verb = "Add", Struct = new StructSpec { Type = "DialogResponses" } } } },
            },
            msg => msg.Contains("created on its own (the record axis)", StringComparison.OrdinalIgnoreCase));

        // ====== G4 — StructSpec CtorArgs VALUE-SHAPE + ARITY (compose ctor_args were never pre-flight-validated) ======
        // A compose (polymorphic Set arm OR struct-element Add) can carry positional ctor_args (StructInput.CtorArgs ->
        // StructSpec.CtorArgs). StructSpecContents validated Fields + Sets but NEVER CtorArgs. At apply, Instantiate picks
        // GetConstructors().FirstOrDefault(len==N) then Coerce(arg, paramType): a wrong ARITY threw "no constructor taking
        // N arg(s)" (named-but-at-apply) and a malformed arg threw UNNAMED (Enum.Parse/int.Parse). The new
        // WriteEngine.TryRecognizeCtorArgs mirrors Instantiate EXACTLY (ResolveStructType + same ctor selector + TryCoerce
        // per arg), called from StructSpecContents — gate and apply can't drift. Fixture: MagicEffect.Archetype
        // (polymorphic, writable) arm MagicEffectArchetype, whose concrete ctor takes a (MagicEffectArchetype.TypeEnum).

        // ---------- G4-REJ-CTORARG-SHAPE: a malformed ctor arg of the right arity refuses, naming the bad arg ----------
        bool g4RejCtorArgShapeOk;
        {
            var req = new WriteRequest { RecordType = "MagicEffect", Path = new[] { "Archetype" }, Verb = "Set",
                Struct = new StructSpec { Type = "MagicEffectArchetype", CtorArgs = new[] { "notatypeenum" } } };
            var reject = rulebook.Validate(req);
            g4RejCtorArgShapeOk = reject is not null && reject.Contains("ctor arg #0", StringComparison.OrdinalIgnoreCase)
                && reject.Contains("notatypeenum", StringComparison.Ordinal);
            Console.WriteLine($"   G4-REJ-CTORARG-SHAPE bad ctor arg   : {(g4RejCtorArgShapeOk ? "PASS — a malformed ctor arg is refused at pre-flight, named (RED before: accepted then Enum.Parse threw unnamed at apply)" : $"FAIL — reject=[{reject}]")}");
        }

        // ---------- G4-REJ-CTORARG-ARITY: a wrong ctor-arg COUNT refuses (mirrors Instantiate's arity throw) ----------
        bool g4RejCtorArgArityOk;
        {
            var req = new WriteRequest { RecordType = "MagicEffect", Path = new[] { "Archetype" }, Verb = "Set",
                Struct = new StructSpec { Type = "MagicEffectArchetype", CtorArgs = new[] { "a", "b", "c" } } };
            var reject = rulebook.Validate(req);
            g4RejCtorArgArityOk = reject is not null && reject.Contains("no constructor taking 3 arg(s)", StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"   G4-REJ-CTORARG-ARITY wrong count    : {(g4RejCtorArgArityOk ? "PASS — a wrong ctor-arg count is refused at pre-flight (RED before: accepted then 'no constructor taking 3 arg(s)' at apply)" : $"FAIL — reject=[{reject}]")}");
        }

        // ---------- G4-OK-CTORARG: valid ctor args of the right arity stay accepted (no over-reject) ----------
        bool g4OkCtorArgOk;
        {
            var req = new WriteRequest { RecordType = "MagicEffect", Path = new[] { "Archetype" }, Verb = "Set",
                Struct = new StructSpec { Type = "MagicEffectArchetype", CtorArgs = new[] { "ValueModifier" } } };
            var reject = rulebook.Validate(req);
            g4OkCtorArgOk = reject is null;
            Console.WriteLine($"   G4-OK-CTORARG valid ctor arg        : {(g4OkCtorArgOk ? "PASS — a valid ctor arg (MagicEffectArchetype TypeEnum 'ValueModifier') is NOT over-rejected" : $"FAIL — reject=[{reject}]")}");
        }

        // ---------- G4-OK-NOCTORARGS: a compose WITHOUT ctor_args (the common case) is untouched ----------
        // The check is guarded by `spec.CtorArgs is { }` — a null CtorArgs skips it entirely, so the parameterless/field
        // compose path the engine already proves is unchanged. (No Fields, to avoid an unrelated enum dependency.)
        bool g4OkNoCtorArgsOk;
        {
            var req = new WriteRequest { RecordType = "MagicEffect", Path = new[] { "Archetype" }, Verb = "Set",
                Struct = new StructSpec { Type = "MagicEffectLightArchetype" } };
            var reject = rulebook.Validate(req);
            g4OkNoCtorArgsOk = reject is null;
            Console.WriteLine($"   G4-OK-NOCTORARGS no ctor_args       : {(g4OkNoCtorArgsOk ? "PASS — a compose without ctor_args (the common case) is untouched" : $"FAIL — reject=[{reject}]")}");
        }

        // ---------- G4-REJ-CTORARG-E2E: a malformed ctor arg refuses end-to-end with NO file written (review hardening) ----------
        // MagicEffect is a flat (Kind=record) createable record; the create's own op does the polymorphic Set with a bad
        // ctor arg. The PRE-FLIGHT message ('ctor arg #0'), not the apply Enum.Parse throw, proves the gate caught it
        // BEFORE Instantiate ran — self-enforcing against future drift in Instantiate (G4's recognizer is the one
        // genuinely new one, so this faithfully reproduces the accept-then-throw end to end). RejectArm proves no file.
        bool g4RejCtorArgE2eOk = RejectArm("G4-REJ-CTORARG-E2E bad ctor arg   ", tmpDir, "G4E2E", mPath, rulebook,
            new[]
            {
                new WritePatchBuilder.CreateSpec { RecordType = "MagicEffect", EditorId = "HcNcG4Mgef",
                    Edits = new[] { new WriteRequest { RecordType = "MagicEffect", Path = new[] { "Archetype" }, Verb = "Set",
                        Struct = new StructSpec { Type = "MagicEffectArchetype", CtorArgs = new[] { "notatypeenum" } } } } },
            },
            msg => msg.Contains("ctor arg #0", StringComparison.OrdinalIgnoreCase));

        // ====== G7 — composable-element MERGE + non-plain-value Remove-BY-VALUE (the deferred-reject completion) ======
        // The IsComposableElement deferred-reject block covered Add (compose/deferred) and ReplaceAll/SetAtIndex, but
        // OMITTED Merge — a Package.Data Merge fell through to ACCEPT then threw 'No coercion rule' at apply. And a
        // Remove BY VALUE (Key null) on ANY non-plain-value element (composable OR record — both have no plain-value
        // form) likewise fell through then threw. The fix adds Merge to the composable deferred-reject and adds ONE
        // unified Remove-by-value branch (predicate: list Remove, Key null, NOT formlink, NOT coercible) covering
        // composable + record + the dormant uncoercible case by construction. A Remove BY INDEX (Key present) and a dict
        // Remove BY KEY stay accepted (throw-free RemoveAt / key-gated). Closes the 2 matrix-critic cells + the record
        // Remove-by-value twin found while implementing G6.

        // ---------- G7-REJ-DICTMERGE: a Package.Data Merge (composable-valued dict) refuses (Merge now in the block) ----------
        bool g7RejDictMergeOk;
        {
            var req = new WriteRequest { RecordType = "Package", Path = new[] { "Data" }, Verb = "Merge",
                Entries = new Dictionary<string, string> { ["0"] = "x" } };
            var reject = rulebook.Validate(req);
            g7RejDictMergeOk = reject is not null && reject.Contains("Merge of modeled elements", StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"   G7-REJ-DICTMERGE composable merge   : {(g7RejDictMergeOk ? "PASS — a Package.Data Merge is refused (Merge folded into the composable deferred-reject; RED before: accepted then 'No coercion rule' at apply)" : $"FAIL — reject=[{reject}]")}");
        }

        // ---------- G7-REJ-COMPOSABLE-REMOVE-BYVALUE: a struct-element list Remove-by-value refuses (remove by index) ----------
        bool g7RejComposableRemoveOk;
        {
            var req = new WriteRequest { RecordType = "Faction", Path = new[] { "Ranks" }, Verb = "Remove", Value = "x" };
            var reject = rulebook.Validate(req);
            g7RejComposableRemoveOk = reject is not null && reject.Contains("BY INDEX", StringComparison.OrdinalIgnoreCase)
                && reject.Contains("not by value", StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"   G7-REJ-COMPOSABLE-REMOVE-BYVALUE   : {(g7RejComposableRemoveOk ? "PASS — a struct-element Remove-by-value is refused, redirected to remove-by-index (RED before: accepted then 'No coercion rule' at apply)" : $"FAIL — reject=[{reject}]")}");
        }

        // ---------- G7-REJ-RECORD-REMOVE-BYVALUE: a record-element list Remove-by-value refuses too (one unified branch) ----------
        // The twin found while implementing G6: a record element ALSO has no plain-value form, so the SAME predicate
        // (not coercible, not formlink) catches it. RED before: accepted then Coerce(value, IDialogResponsesGetter) threw.
        bool g7RejRecordRemoveOk;
        {
            var req = new WriteRequest { RecordType = "DialogTopic", Path = new[] { "Responses" }, Verb = "Remove", Value = "x" };
            var reject = rulebook.Validate(req);
            g7RejRecordRemoveOk = reject is not null && reject.Contains("BY INDEX", StringComparison.OrdinalIgnoreCase)
                && reject.Contains("not by value", StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"   G7-REJ-RECORD-REMOVE-BYVALUE       : {(g7RejRecordRemoveOk ? "PASS — a record-element Remove-by-value is refused too (the unified non-plain-value branch covers records)" : $"FAIL — reject=[{reject}]")}");
        }

        // ---------- G7-OK-COMPOSABLE-REMOVE-BYINDEX: a struct-element Remove BY INDEX stays accepted (no over-reject) ----------
        bool g7OkComposableRemoveIdxOk;
        {
            var req = new WriteRequest { RecordType = "Faction", Path = new[] { "Ranks" }, Verb = "Remove", Key = "0" };
            var reject = rulebook.Validate(req);
            g7OkComposableRemoveIdxOk = reject is null;
            Console.WriteLine($"   G7-OK-COMPOSABLE-REMOVE-BYINDEX    : {(g7OkComposableRemoveIdxOk ? "PASS — a struct-element Remove by index (RemoveAt) is throw-free and NOT over-rejected" : $"FAIL — reject=[{reject}]")}");
        }

        // ---------- G7-OK-DICTREMOVE-BYKEY: a Package.Data Remove BY KEY stays accepted (the Remove-by-value branch is list-only) ----------
        bool g7OkDictRemoveKeyOk;
        {
            var req = new WriteRequest { RecordType = "Package", Path = new[] { "Data" }, Verb = "Remove", Key = "0" };
            var reject = rulebook.Validate(req);
            g7OkDictRemoveKeyOk = reject is null;
            Console.WriteLine($"   G7-OK-DICTREMOVE-BYKEY composable  : {(g7OkDictRemoveKeyOk ? "PASS — a composable-dict Remove by key (key-gated, throw-free) is NOT over-rejected" : $"FAIL — reject=[{reject}]")}");
        }

        // ====== GAP 3 — dict-element COMPOSITION (PR-B; AI-package Data-input authoring) ======
        // Package.Data (Dictionary<sbyte,APackageData>) is the ONLY struct/arm-VALUED dict Mutagen models — the last
        // un-authorable PACK piece (a package's typed Data inputs: target/location/bool/int/float/objectlist/topic).
        // Before Gap 3 the gate refused a dict Add compose ('dict-element composition is a later surface') and
        // ApplyDictVerb ignored req.Struct (case-c deferral). Gap 3 builds it BY CONSTRUCTION, mirroring the LIST compose
        // path: ApplyDictVerb Add/Set BuildStruct(req.Struct) for a composable element + the gate ACCEPTS the spec via the
        // SAME StructElementLegality the list Add uses. Add stays throw-on-duplicate; overwrite is Set-with-compose.

        // ---------- GAP3-OK-DICTADD-COMPOSE: a Package.Data Add carrying a PackageDataBool compose is ACCEPTED ----------
        bool gap3OkDictAddComposeOk;
        {
            var req = new WriteRequest { RecordType = "Package", Path = new[] { "Data" }, Verb = "Add", Key = "0",
                Struct = new StructSpec { Type = "PackageDataBool", Fields = new Dictionary<string, string> { ["Data"] = "true" } } };
            var reject = rulebook.Validate(req);
            gap3OkDictAddComposeOk = reject is null;
            Console.WriteLine($"   GAP3-OK-DICTADD-COMPOSE            : {(gap3OkDictAddComposeOk ? "PASS — a Package.Data Add carrying a PackageDataBool compose is ACCEPTED (RED before: rejected 'dict-element composition is a later surface')" : $"FAIL — reject=[{reject}]")}");
        }

        // ---------- GAP3-OK-DICTSET-COMPOSE: a Package.Data Set (overwrite) carrying a compose is ACCEPTED ----------
        bool gap3OkDictSetComposeOk;
        {
            var req = new WriteRequest { RecordType = "Package", Path = new[] { "Data" }, Verb = "Set", Key = "0",
                Struct = new StructSpec { Type = "PackageDataBool", Fields = new Dictionary<string, string> { ["Data"] = "true" } } };
            var reject = rulebook.Validate(req);
            gap3OkDictSetComposeOk = reject is null;
            Console.WriteLine($"   GAP3-OK-DICTSET-COMPOSE            : {(gap3OkDictSetComposeOk ? "PASS — a Package.Data Set carrying a compose is ACCEPTED (the overwrite path; RED before: 'Set on dict requires a value' — req.Value null)" : $"FAIL — reject=[{reject}]")}");
        }

        // ---------- GAP3-REJ-BADARM: a Package.Data compose whose type is not an APackageData arm refuses ----------
        bool gap3RejBadArmOk;
        {
            var req = new WriteRequest { RecordType = "Package", Path = new[] { "Data" }, Verb = "Add", Key = "0",
                Struct = new StructSpec { Type = "Weapon" } };
            var reject = rulebook.Validate(req);
            // (G8) the un-composable base must NOT appear in the 'Legal element types:' list it names — scoped to the
            // list portion, since 'APackageData' legitimately appears earlier as the element-type context of the mismatch.
            int g3LegalAt = reject?.IndexOf("Legal element types:", StringComparison.Ordinal) ?? -1;
            string g3LegalList = g3LegalAt >= 0 ? reject![g3LegalAt..] : "";
            gap3RejBadArmOk = reject is not null && reject.Contains("does not match", StringComparison.OrdinalIgnoreCase)
                && g3LegalAt >= 0 && !g3LegalList.Contains("APackageData", StringComparison.Ordinal);
            Console.WriteLine($"   GAP3-REJ-BADARM bad compose arm    : {(gap3RejBadArmOk ? "PASS — a non-APackageData compose type is refused, naming the legal arms but NOT the base 'APackageData' in the legal list (G8 filter; RED before G8: the base was advertised)" : $"FAIL — reject=[{reject}]")}");
        }

        // ---------- GAP3-OK-LIST-UNCHANGED: an ARM-element LIST compose (Faction.Conditions) still composes (no regression) ----------
        // The composable-block Add branch now serves list AND dict; this guards that the LIST arm-element path (the closest
        // analog to Package.Data's arm-valued dict) still accepts after the edit. Accepted before AND after (regression guard).
        bool gap3OkListUnchangedOk;
        {
            var req = new WriteRequest { RecordType = "Faction", Path = new[] { "Conditions" }, Verb = "Add",
                Struct = new StructSpec { Type = "ConditionFloat" } };
            var reject = rulebook.Validate(req);
            gap3OkListUnchangedOk = reject is null;
            Console.WriteLine($"   GAP3-OK-LIST-UNCHANGED arm-list Add: {(gap3OkListUnchangedOk ? "PASS — an arm-element list compose still composes (the dict-Add change didn't disturb the list path)" : $"FAIL — reject=[{reject}]")}");
        }

        // ---------- GAP3-E2E: a composed PackageDataBool round-trips through the REAL create+apply path onto disk ----------
        bool gap3OkE2eOk = false;
        {
            string pPath = Path.Combine(tmpDir, "HcNcGap3Ok.esp");
            var specs = new[]
            {
                new WritePatchBuilder.CreateSpec { RecordType = "Package", EditorId = "HcNcGap3Pack",
                    Edits = new[] { new WriteRequest { RecordType = "Package", Path = new[] { "Data" }, Verb = "Add", Key = "0",
                        Struct = new StructSpec { Type = "PackageDataBool", Fields = new Dictionary<string, string> { ["Data"] = "true", ["Name"] = "HcGap3" } } } } },
            };
            using var r = LoadOrderResolver.Build(new[] { mPath });
            var o = WritePatchBuilder.CreateRecords(r, rulebook, specs, pPath, extend: false);
            bool present = o.Success && o.Created.Count > 0 && PackageDataComposedBool(pPath, o.Created[0].FormKey);
            gap3OkE2eOk = o.Success && present;
            Console.WriteLine($"   GAP3-E2E composed bool round-trips : {(gap3OkE2eOk ? "PASS — accepted at the gate AND a PackageDataBool(Data=true) written to Package.Data[0] on disk" : $"FAIL — success={o.Success} present={present} err=[{o.Error}]")}");
        }

        // ---------- GAP3-E2E-SET: the Set-OVERWRITE apply path round-trips (the documented duplicate-key escape hatch) ----------
        // GAP3-E2E proves the Add apply branch; this proves the new Set branch (setItem.Invoke(dict, …, BuildValue())) — the
        // path a user takes after "Key already present — use Set to overwrite". Add Data[0]={Data:false}, then Set Data[0]=
        // {Data:true} in ONE create: the Set REPLACES, so the on-disk Data[0] reads Data==true (PackageDataComposedBool).
        bool gap3OkE2eSetOk = false;
        {
            string pPath = Path.Combine(tmpDir, "HcNcGap3Set.esp");
            var specs = new[]
            {
                new WritePatchBuilder.CreateSpec { RecordType = "Package", EditorId = "HcNcGap3SetPack",
                    Edits = new[]
                    {
                        new WriteRequest { RecordType = "Package", Path = new[] { "Data" }, Verb = "Add", Key = "0",
                            Struct = new StructSpec { Type = "PackageDataBool", Fields = new Dictionary<string, string> { ["Data"] = "false" } } },
                        new WriteRequest { RecordType = "Package", Path = new[] { "Data" }, Verb = "Set", Key = "0",
                            Struct = new StructSpec { Type = "PackageDataBool", Fields = new Dictionary<string, string> { ["Data"] = "true" } } },
                    } },
            };
            using var r = LoadOrderResolver.Build(new[] { mPath });
            var o = WritePatchBuilder.CreateRecords(r, rulebook, specs, pPath, extend: false);
            bool overwritten = o.Success && o.Created.Count > 0 && PackageDataComposedBool(pPath, o.Created[0].FormKey);
            gap3OkE2eSetOk = o.Success && overwritten;
            Console.WriteLine($"   GAP3-E2E-SET overwrite round-trips : {(gap3OkE2eSetOk ? "PASS — Add Data[0]=false then Set Data[0]=true OVERWRITES; PackageDataBool(Data=true) on disk (the 'use Set to overwrite' escape hatch)" : $"FAIL — success={o.Success} overwritten={overwritten} err=[{o.Error}]")}");
        }

        // ---------- GAP3-REJ-DUP: Add of an already-present key refuses (apply-time) with the improved 'use Set' guidance ----------
        // The duplicate check is apply-time (pre-flight is schema-only, can't see live occupancy): two Adds of Data[0] in one
        // create — the second refuses the WHOLE call, no file written, with the Gap-3 message naming Set as the overwrite path.
        // ALSO guards gap-audit Finding 3: this EXPECTED apply rejection (an ExpectedApplyRejectionException) must render its
        // clean guidance WITHOUT the "pre-flight ACCEPTED … a real inconsistency" wrapper reserved for genuine gate/apply
        // drift. RED-proof: before the fix the envelope embedded that wrapper, so the absence-asserts below would FAIL.
        bool gap3RejDupOk = RejectArm("GAP3-REJ-DUP duplicate key add     ", tmpDir, "Gap3Dup", mPath, rulebook,
            new[]
            {
                new WritePatchBuilder.CreateSpec { RecordType = "Package", EditorId = "HcNcGap3Dup",
                    Edits = new[]
                    {
                        new WriteRequest { RecordType = "Package", Path = new[] { "Data" }, Verb = "Add", Key = "0",
                            Struct = new StructSpec { Type = "PackageDataBool", Fields = new Dictionary<string, string> { ["Data"] = "true" } } },
                        new WriteRequest { RecordType = "Package", Path = new[] { "Data" }, Verb = "Add", Key = "0",
                            Struct = new StructSpec { Type = "PackageDataBool", Fields = new Dictionary<string, string> { ["Data"] = "false" } } },
                    } },
            },
            msg => msg.Contains("already present", StringComparison.OrdinalIgnoreCase)
                && msg.Contains("use Set", StringComparison.OrdinalIgnoreCase)
                && !msg.Contains("real inconsistency", StringComparison.OrdinalIgnoreCase)   // Finding 3: no inconsistency wrapper …
                && !msg.Contains("pre-flight ACCEPTED", StringComparison.OrdinalIgnoreCase)); //            … on an expected rejection

        // ====== G8 — the polymorphic BASE composed by its OWN name (the gate/apply drift this batch exists to kill) ======
        // StructElementLegality short-circuits `if (spec.Type == er) specSchema = elemSchema` — so composing the poly-BASE
        // itself ({Type:"APackageData"} on the Package.Data dict, {Type:"Condition"} on a *.Conditions list) validated
        // against the base's OWN fields and ACCEPTED, then apply DIVERGED by base kind (verified by reflection): a CONCRETE
        // base (APackageData, IsAbstract=false, has a public parameterless ctor) Instantiate()s a degenerate empty base and
        // SILENTLY WRITES IT — a Q3 silent-wrong-write, WORSE than a throw; an ABSTRACT base (Condition) throws
        // MemberAccessException ("cannot create an abstract class") at Invoke. A CONCRETE poly-base ALSO lists itself among
        // its arms (FindUnionArms keeps a non-abstract base — only an abstract one is filtered by !IsAbstract), so the
        // arms.Contains branch would admit it too and the bad-arm message advertised it. The recognizer is the corpus
        // poly-base KIND, NOT Type.IsAbstract — APackageData is concrete, so IsAbstract would miss the silent-write (worse)
        // case. The base is rejected when named, and filtered out of the legal-arms set everywhere (Contains + message).

        // ---------- GAP3-REJ-BASEARM: a Package.Data Add composing the BASE 'APackageData' itself refuses ----------
        bool gap3RejBaseArmOk;
        {
            var req = new WriteRequest { RecordType = "Package", Path = new[] { "Data" }, Verb = "Add", Key = "0",
                Struct = new StructSpec { Type = "APackageData" } };
            var reject = rulebook.Validate(req);
            gap3RejBaseArmOk = reject is not null
                && reject.Contains("polymorphic base", StringComparison.OrdinalIgnoreCase)
                && reject.Contains("PackageDataBool", StringComparison.Ordinal);   // a real arm is offered instead
            Console.WriteLine($"   GAP3-REJ-BASEARM compose the base  : {(gap3RejBaseArmOk ? "PASS — composing the poly-base 'APackageData' itself refuses, offering the concrete arms (RED before: accepted via the spec.Type==er short-circuit)" : $"FAIL — reject=[{reject}]")}");
        }

        // ---------- GAP3-REJ-BASEARM-LIST: the LIST twin — Faction.Conditions Add {Type:"Condition"} refuses ----------
        // Proves the fix lives in the SHARED StructElementLegality (list + dict), and catches an ABSTRACT base (Condition,
        // NOT in its own arms) by the same spec.Type==er + poly-base check the concrete APackageData hits.
        bool gap3RejBaseArmListOk;
        {
            var req = new WriteRequest { RecordType = "Faction", Path = new[] { "Conditions" }, Verb = "Add",
                Struct = new StructSpec { Type = "Condition" } };
            var reject = rulebook.Validate(req);
            gap3RejBaseArmListOk = reject is not null
                && reject.Contains("polymorphic base", StringComparison.OrdinalIgnoreCase)
                && reject.Contains("ConditionFloat", StringComparison.Ordinal);
            Console.WriteLine($"   GAP3-REJ-BASEARM-LIST list base    : {(gap3RejBaseArmListOk ? "PASS — composing the poly-base 'Condition' on a list refuses too (shared validator; RED before: accepted)" : $"FAIL — reject=[{reject}]")}");
        }

        // ---------- GAP3-OK-BASE-NOOVERREJECT: a CONCRETE (non-poly-base) struct composed by its own name STILL accepts ----
        // The no-over-reject guard: the fix rejects spec.Type==er ONLY when er is a poly-base. A plain struct element
        // (Faction.Ranks element 'Rank', Kind=struct) composed by its own name is the NORMAL case and must stay accepted.
        bool gap3OkBaseNoOverRejectOk;
        {
            var req = new WriteRequest { RecordType = "Faction", Path = new[] { "Ranks" }, Verb = "Add",
                Struct = new StructSpec { Type = "Rank" } };
            var reject = rulebook.Validate(req);
            gap3OkBaseNoOverRejectOk = reject is null;
            Console.WriteLine($"   GAP3-OK-BASE-NOOVERREJECT struct   : {(gap3OkBaseNoOverRejectOk ? "PASS — a concrete struct element composed by its own name ('Rank') still accepts (only poly-bases rejected)" : $"FAIL — reject=[{reject}]")}");
        }

        // ---------- GAP3-REJ-BASEARM-E2E: composing the base end-to-end refuses with NO file written (gate, not apply) ----
        // The silent-wrong-write closed end-to-end: APackageData is concrete + instantiable, so RED here was the WORST case
        // — the create SUCCEEDED and a degenerate empty-base entry was WRITTEN with no error (refused=False, noFile=False).
        // GREEN: the gate rejects pre-flight ('polymorphic base'), so NO file is written and BuildStruct never runs.
        bool gap3RejBaseArmE2eOk = RejectArm("GAP3-REJ-BASEARM-E2E compose base  ", tmpDir, "Gap3Base", mPath, rulebook,
            new[]
            {
                new WritePatchBuilder.CreateSpec { RecordType = "Package", EditorId = "HcNcGap3Base",
                    Edits = new[] { new WriteRequest { RecordType = "Package", Path = new[] { "Data" }, Verb = "Add", Key = "0",
                        Struct = new StructSpec { Type = "APackageData" } } } },
            },
            msg => msg.Contains("polymorphic base", StringComparison.OrdinalIgnoreCase));

        // ---------- GAP3-REJ-BASEARM-FIELD: the STANDALONE poly-FIELD twin (ArmLegality, NOT StructElementLegality) ----
        // A Set on a polymorphic FIELD (not a collection element) composing the base by name hits the SIBLING validator
        // ArmLegality, which had the IDENTICAL hole: a concrete poly-base (ScriptFragments on
        // DialogResponsesAdapter.ScriptFragments) self-lists, so legal.Contains(base) admitted it then apply silently
        // wrote a degenerate base. Found in a completeness sweep, folded in (Aaron 2026-06-18) so the poly-base-by-own-name
        // class is closed at BOTH composition entry points by construction. Path: DialogResponses -> VirtualMachineAdapter
        // (the VMAD substruct) -> ScriptFragments (polymorphic leaf).
        bool gap3RejBaseArmFieldOk;
        {
            var req = new WriteRequest { RecordType = "DialogResponses", Path = new[] { "VirtualMachineAdapter", "ScriptFragments" },
                Verb = "Set", Struct = new StructSpec { Type = "ScriptFragments" } };
            var reject = rulebook.Validate(req);
            gap3RejBaseArmFieldOk = reject is not null
                && reject.Contains("polymorphic base", StringComparison.OrdinalIgnoreCase)
                && reject.Contains("SceneScriptFragments", StringComparison.Ordinal);   // a real arm is offered instead
            Console.WriteLine($"   GAP3-REJ-BASEARM-FIELD poly field  : {(gap3RejBaseArmFieldOk ? "PASS — a standalone poly-FIELD Set composing the base 'ScriptFragments' refuses via ArmLegality (RED before: accepted, then silent-write)" : $"FAIL — reject=[{reject}]")}");
        }

        // ---------- GAP3-OK-ARMFIELD-UNCHANGED: a REAL arm on the same poly field still accepts (no over-reject) ----------
        bool gap3OkArmFieldUnchangedOk;
        {
            var req = new WriteRequest { RecordType = "DialogResponses", Path = new[] { "VirtualMachineAdapter", "ScriptFragments" },
                Verb = "Set", Struct = new StructSpec { Type = "SceneScriptFragments" } };
            var reject = rulebook.Validate(req);
            gap3OkArmFieldUnchangedOk = reject is null;
            Console.WriteLine($"   GAP3-OK-ARMFIELD-UNCHANGED real arm: {(gap3OkArmFieldUnchangedOk ? "PASS — a real arm ('SceneScriptFragments') on the poly field still accepts (filtering the base didn't break real arms)" : $"FAIL — reject=[{reject}]")}");
        }

        // ====== EXPECTED apply rejections — the LIVE-STATE collection-addressing class (gap-audit Finding 3, whole class) ======
        // Finding 3's dup-key (GAP3-REJ-DUP) is ONE member of a class: apply-time refusals whose cause is live collection
        // state the schema-only pre-flight CANNOT see. They must render CLEANLY (the actionable message), NOT under the
        // "pre-flight ACCEPTED … a real inconsistency" wrapper reserved for genuine gate/apply drift. The leaf out-of-range
        // index (SetAtIndex / Remove-by-index) is the sibling Finding 3 named; the mid-path nav twins route through the SAME
        // WritePatchBuilder catch via the shared StepIntoElement (now throwing the EXPECTED kind too).

        // ---------- EXPECTED-REJ-SETIDX-OOB: a list SetAtIndex past the end refuses CLEANLY end-to-end, NO file ----------
        // Index 5 is a VALID shape (pre-flight accepts — KEYSHAPE leaves the in-range bound to apply), so the gate passes
        // and apply's new pre-check fires (Race.MovementTypeNames=List<String> — a String list, so no master to serialize).
        // RED before: the indexer Invoke threw ArgumentOutOfRangeException → wrapped as a "real inconsistency"; the
        // absence-asserts below would FAIL.
        bool expectedRejSetIdxOobOk = RejectArm("EXPECTED-REJ-SETIDX-OOB out-of-range", tmpDir, "ExpSetIdxOob", mPath, rulebook,
            new[]
            {
                new WritePatchBuilder.CreateSpec { RecordType = "Race", EditorId = "HcNcExpSetIdx",
                    Edits = new[] { new WriteRequest { RecordType = "Race", Path = new[] { "MovementTypeNames" }, Verb = "SetAtIndex", Key = "5", Value = "MT_Walk" } } },
            },
            msg => msg.Contains("out of range", StringComparison.OrdinalIgnoreCase)
                && !msg.Contains("real inconsistency", StringComparison.OrdinalIgnoreCase)
                && !msg.Contains("pre-flight ACCEPTED", StringComparison.OrdinalIgnoreCase));

        // ---------- EXPECTED-REJ-REMOVEIDX-OOB: a list Remove-by-index past the end refuses CLEANLY, NO file ----------
        // Add one element first so the list is non-null (Remove on an absent list is a deliberate no-op), then Remove[5]
        // (count 1 → out of range). RED before: RemoveAt Invoke threw ArgumentOutOfRangeException → wrapped.
        bool expectedRejRemoveIdxOobOk = RejectArm("EXPECTED-REJ-REMOVEIDX-OOB out-range", tmpDir, "ExpRmIdxOob", mPath, rulebook,
            new[]
            {
                new WritePatchBuilder.CreateSpec { RecordType = "Race", EditorId = "HcNcExpRmIdx",
                    Edits = new[]
                    {
                        new WriteRequest { RecordType = "Race", Path = new[] { "MovementTypeNames" }, Verb = "Add", Value = "MT_Walk" },
                        new WriteRequest { RecordType = "Race", Path = new[] { "MovementTypeNames" }, Verb = "Remove", Key = "5" },
                    } },
            },
            msg => msg.Contains("out of range", StringComparison.OrdinalIgnoreCase)
                && !msg.Contains("real inconsistency", StringComparison.OrdinalIgnoreCase)
                && !msg.Contains("pre-flight ACCEPTED", StringComparison.OrdinalIgnoreCase));

        // ---------- EXPECTED-OK-SETIDX-INRANGE: an in-range SetAtIndex still applies (the pre-check does NOT over-reject) ----
        // Direct engine (no serialize/master concern — a String list never references a master): Add then SetAtIndex[0],
        // assert the value LANDED and nothing threw. Guards against the new pre-check rejecting a legitimate in-range index.
        bool expectedOkSetIdxInRangeOk;
        {
            var race = new Race(new FormKey(mKey, 0x901u), SkyrimRelease.SkyrimSE);
            bool threw = false;
            try
            {
                WriteEngine.ApplyVerb(race, new WriteRequest { RecordType = "Race", Path = new[] { "MovementTypeNames" }, Verb = "Add", Value = "MT_Walk" });
                WriteEngine.ApplyVerb(race, new WriteRequest { RecordType = "Race", Path = new[] { "MovementTypeNames" }, Verb = "SetAtIndex", Key = "0", Value = "MT_Run" });
            }
            catch { threw = true; }
            bool landed = race.MovementTypeNames is { Count: 1 } mtn && mtn[0] == "MT_Run";
            expectedOkSetIdxInRangeOk = !threw && landed;
            Console.WriteLine($"   EXPECTED-OK-SETIDX-INRANGE valid  : {(expectedOkSetIdxInRangeOk ? "PASS — Add then in-range SetAtIndex[0] applies (value lands; the pre-check does NOT over-reject)" : $"FAIL — threw={threw} landed={landed}")}");
        }

        // ---------- EXPECTED-NAV-TYPE: the shared StepIntoElement throws the EXPECTED kind for live-state mid-path nav ----
        // So a mid-path write into an absent dict entry / out-of-bounds list index routes into the clean-render channel,
        // not the wrapper. Direct engine (deterministic; independent of whether pre-flight admits a mid-path index). RED
        // before: StepIntoElement threw a PLAIN InvalidOperationException → not caught as EXPECTED → flags stay false → FAIL.
        bool expectedNavTypeOk;
        {
            bool dictEntryAbsentExpected = false, listOobExpected = false;
            var pkg = new Package(new FormKey(mKey, 0x902u), SkyrimRelease.SkyrimSE);   // Data dict carries no entry at key 0
            try { WriteEngine.ApplyVerb(pkg, new WriteRequest { RecordType = "Package", Path = new[] { "Data[0]", "Data" }, Verb = "Set", Value = "true" }); }
            catch (ExpectedApplyRejectionException) { dictEntryAbsentExpected = true; }
            catch { }
            var fac = new Faction(new FormKey(mKey, 0x903u), SkyrimRelease.SkyrimSE);    // Conditions list carries no index 5
            try { WriteEngine.ApplyVerb(fac, new WriteRequest { RecordType = "Faction", Path = new[] { "Conditions[5]", "Flags" }, Verb = "Set", Value = "0" }); }
            catch (ExpectedApplyRejectionException) { listOobExpected = true; }
            catch { }
            expectedNavTypeOk = dictEntryAbsentExpected && listOobExpected;
            Console.WriteLine($"   EXPECTED-NAV-TYPE live-state nav   : {(expectedNavTypeOk ? "PASS — StepIntoElement throws the EXPECTED kind for an absent dict entry AND an out-of-bounds list index (routes to clean render)" : $"FAIL — dictEntryAbsent={dictEntryAbsentExpected} listOob={listOobExpected}")}");
        }

        // ---------- EXPECTED-REJ-NAV-E2E: a mid-path nav reject renders CLEANLY through WritePatchBuilder end-to-end ----------
        // EXPECTED-NAV-TYPE proves the engine throws the right KIND; this closes the seam by proving the FULL render (the
        // shared WritePatchBuilder catch → clean message, no wrapper, NO file). Package.Data[5].Name is a VALID mid-path
        // path (GAP1-OK-MIDKEY: valid sbyte key + the writable APackageData 'Name' leaf) but key 5 is ABSENT in a fresh
        // Package's (empty, non-null) Data dict — so the gate passes and apply hits StepIntoElement's key-absent throw.
        // RED before the nav reclassification: that throw was a PLAIN InvalidOperationException → wrapped as a "real
        // inconsistency"; the absence-asserts below would FAIL.
        bool expectedRejNavE2eOk = RejectArm("EXPECTED-REJ-NAV-E2E absent key   ", tmpDir, "ExpNavKey", mPath, rulebook,
            new[]
            {
                new WritePatchBuilder.CreateSpec { RecordType = "Package", EditorId = "HcNcExpNav",
                    Edits = new[] { new WriteRequest { RecordType = "Package", Path = new[] { "Data[5]", "Name" }, Verb = "Set", Value = "houseCARL" } } },
            },
            msg => msg.Contains("No entry with key", StringComparison.OrdinalIgnoreCase)
                && !msg.Contains("real inconsistency", StringComparison.OrdinalIgnoreCase)
                && !msg.Contains("pre-flight ACCEPTED", StringComparison.OrdinalIgnoreCase));

        // ====== Gap 2 (PR #83 follow-up) — present-but-null element/entry = its own MalformedTargetData third category ======
        // A present-but-null collection element / dict entry threw a PLAIN InvalidOperationException → the generic
        // "pre-flight ACCEPTED it but the apply threw — a real inconsistency" wrapper, which mislabels pre-existing
        // malformed SOURCE data (not the user's input, not an engine bug) as an internal inconsistency. It is now its own
        // MalformedTargetDataException — a distinct THIRD category (neither ExpectedApplyRejection nor the inconsistency
        // wrapper), rendered cleanly by WritePatchBuilder. Decision: Aaron 2026-06-18 (dedicated third category).
        // NOTE on coverage: the present-but-null state is NOT producible through houseCARL's own write path (the null gates
        // forbid writing one) and Mutagen will not serialize a null element to synthesize a malformed fixture — so there is
        // nothing to drive through the create/Apply pipeline for a full E2E render. This engine-direct arm proves the THROW
        // KIND + the third-category message; the throw type deterministically routes to WritePatchBuilder's dedicated
        // MalformedTargetDataException catch (a verbatim-message passthrough mirroring the proven ExpectedApplyRejection catch).

        // ---------- MALFORMED-NAV-TYPE: a present-but-null dict entry AND list element throw MalformedTargetData ----------
        // Inject a present-but-null entry/element in-memory (the only way to reach the state — see NOTE), then navigate in.
        // RED before: the throws were plain InvalidOperationException → not caught as MalformedTargetData → flags stay false.
        bool malformedNavTypeOk;
        {
            bool dictOk = false, listOk = false;
            var pkg = new Package(new FormKey(mKey, 0x913u), SkyrimRelease.SkyrimSE);
            pkg.Data[(sbyte)0] = null!;   // present-but-null dict entry (Data is non-null/empty on a fresh Package)
            try { WriteEngine.ApplyVerb(pkg, new WriteRequest { RecordType = "Package", Path = new[] { "Data[0]", "Data" }, Verb = "Set", Value = "true" }); }
            catch (MalformedTargetDataException ex) { dictOk = ex.Message.Contains("malformed", StringComparison.OrdinalIgnoreCase); }
            catch { }
            var fac = new Faction(new FormKey(mKey, 0x914u), SkyrimRelease.SkyrimSE);
            WriteEngine.ApplyVerb(fac, new WriteRequest { RecordType = "Faction", Path = new[] { "Conditions" }, Verb = "Add", Struct = new StructSpec { Type = "ConditionFloat" } });
            fac.Conditions![0] = null!;   // present-but-null list element (Conditions materialized with one element by the Add)
            try { WriteEngine.ApplyVerb(fac, new WriteRequest { RecordType = "Faction", Path = new[] { "Conditions[0]", "CompareOperator" }, Verb = "Set", Value = "EqualTo" }); }
            catch (MalformedTargetDataException ex) { listOk = ex.Message.Contains("malformed", StringComparison.OrdinalIgnoreCase); }
            catch { }
            malformedNavTypeOk = dictOk && listOk;
            Console.WriteLine($"   MALFORMED-NAV-TYPE present-but-null : {(malformedNavTypeOk ? "PASS — StepIntoElement throws MalformedTargetDataException (the distinct third category, 'malformed' message) for a present-but-null dict entry AND list element" : $"FAIL — dictOk={dictOk} listOk={listOk}")}");
        }

        // ====== Gap 3 (PR #83 follow-up) — surface a Remove that removes NOTHING (close the silent-no-op) ======
        // list Remove-by-value and dict Remove-by-key IGNORED the runtime Remove's bool, so "remove X" when X isn't
        // present SILENTLY succeeded (a Q3 silent degradation — reports success having changed nothing). Now all three
        // forms — dict key absent, list value absent, and a Remove on an absent (null) collection — SURFACE as the
        // EXPECTED kind ("nothing to remove"): the symmetric twin of Add's duplicate-key refusal, and consistent with
        // Remove-by-INDEX already surfacing out-of-range. Decision: Aaron 2026-06-18 (surface, not idempotent). (REMOVE-*
        // prefix disambiguates from the existing GAP3-* dict-element-COMPOSITION arms.)

        // ---------- REMOVE-REJ-DICTKEY-ABSENT: a dict Remove of a key NOT present refuses cleanly E2E, NO file ----------
        // A fresh Package's Data is an empty NON-null dict, so apply reaches the runtime Remove (returns false). RED before:
        // the false was ignored → silent success → a no-op patch was WRITTEN (refused=False, noFile=False).
        bool removeRejDictKeyAbsentOk = RejectArm("REMOVE-REJ-DICTKEY-ABSENT no key  ", tmpDir, "RmDictKey", mPath, rulebook,
            new[]
            {
                new WritePatchBuilder.CreateSpec { RecordType = "Package", EditorId = "HcNcRmDictKey",
                    Edits = new[] { new WriteRequest { RecordType = "Package", Path = new[] { "Data" }, Verb = "Remove", Key = "0" } } },
            },
            msg => msg.Contains("nothing to remove", StringComparison.OrdinalIgnoreCase)
                && !msg.Contains("real inconsistency", StringComparison.OrdinalIgnoreCase)
                && !msg.Contains("pre-flight ACCEPTED", StringComparison.OrdinalIgnoreCase));

        // ---------- REMOVE-REJ-NULLCOLL: a Remove on an ABSENT (null) collection refuses cleanly E2E, NO file ----------
        // A fresh Faction's Conditions is null (the absent-collection case, (a)). RED before: the null-collection
        // early-return silently no-op'd → a no-op patch was WRITTEN.
        bool removeRejNullCollOk = RejectArm("REMOVE-REJ-NULLCOLL absent coll   ", tmpDir, "RmNullColl", mPath, rulebook,
            new[]
            {
                new WritePatchBuilder.CreateSpec { RecordType = "Faction", EditorId = "HcNcRmNull",
                    Edits = new[] { new WriteRequest { RecordType = "Faction", Path = new[] { "Conditions" }, Verb = "Remove", Key = "5" } } },
            },
            msg => msg.Contains("nothing to remove", StringComparison.OrdinalIgnoreCase)
                && !msg.Contains("real inconsistency", StringComparison.OrdinalIgnoreCase)
                && !msg.Contains("pre-flight ACCEPTED", StringComparison.OrdinalIgnoreCase));

        // ---------- REMOVE-REJ-LISTVAL-ABSENT: a list Remove-by-value of a value NOT present throws the EXPECTED kind ----
        // Direct engine (a String list never references a master): Add "MT_Run" (list non-null, ["MT_Run"]) then Remove
        // "MT_Walk" (absent). RED before: List<T>.Remove returned false, ignored → no throw → flag stays false.
        bool removeRejListValAbsentOk;
        {
            var race = new Race(new FormKey(mKey, 0x910u), SkyrimRelease.SkyrimSE);
            WriteEngine.ApplyVerb(race, new WriteRequest { RecordType = "Race", Path = new[] { "MovementTypeNames" }, Verb = "Add", Value = "MT_Run" });
            bool surfaced = false;
            try { WriteEngine.ApplyVerb(race, new WriteRequest { RecordType = "Race", Path = new[] { "MovementTypeNames" }, Verb = "Remove", Value = "MT_Walk" }); }
            catch (ExpectedApplyRejectionException) { surfaced = true; }
            catch { }
            removeRejListValAbsentOk = surfaced && race.MovementTypeNames is { Count: 1 };
            Console.WriteLine($"   REMOVE-REJ-LISTVAL-ABSENT no value : {(removeRejListValAbsentOk ? "PASS — a list Remove-by-value of an absent value surfaces (EXPECTED kind); the present element is untouched" : $"FAIL — surfaced={surfaced}")}");
        }

        // ---------- REMOVE-OK-PRESENT-DICT: a dict Remove of a PRESENT key still succeeds (no over-reject) ----------
        // Add a composed PackageDataBool at key 0, then Remove it: the runtime Remove returns true → no throw, dict emptied.
        bool removeOkPresentDictOk;
        {
            var pkg = new Package(new FormKey(mKey, 0x911u), SkyrimRelease.SkyrimSE);
            bool threw = false;
            try
            {
                WriteEngine.ApplyVerb(pkg, new WriteRequest { RecordType = "Package", Path = new[] { "Data" }, Verb = "Add", Key = "0",
                    Struct = new StructSpec { Type = "PackageDataBool", Fields = new Dictionary<string, string> { ["Data"] = "true" } } });
                WriteEngine.ApplyVerb(pkg, new WriteRequest { RecordType = "Package", Path = new[] { "Data" }, Verb = "Remove", Key = "0" });
            }
            catch { threw = true; }
            removeOkPresentDictOk = !threw && pkg.Data is { Count: 0 };
            Console.WriteLine($"   REMOVE-OK-PRESENT-DICT present key  : {(removeOkPresentDictOk ? "PASS — a dict Remove of a PRESENT key still succeeds (the no-op surface does NOT over-reject a real removal)" : $"FAIL — threw={threw} count={pkg.Data?.Count}")}");
        }

        // ---------- REMOVE-OK-PRESENT-LIST: a list Remove-by-value of a PRESENT value still succeeds (no over-reject) ----------
        bool removeOkPresentListOk;
        {
            var race = new Race(new FormKey(mKey, 0x912u), SkyrimRelease.SkyrimSE);
            bool threw = false;
            try
            {
                WriteEngine.ApplyVerb(race, new WriteRequest { RecordType = "Race", Path = new[] { "MovementTypeNames" }, Verb = "Add", Value = "MT_Run" });
                WriteEngine.ApplyVerb(race, new WriteRequest { RecordType = "Race", Path = new[] { "MovementTypeNames" }, Verb = "Remove", Value = "MT_Run" });
            }
            catch { threw = true; }
            removeOkPresentListOk = !threw && race.MovementTypeNames is { Count: 0 };
            Console.WriteLine($"   REMOVE-OK-PRESENT-LIST present val  : {(removeOkPresentListOk ? "PASS — a list Remove-by-value of a PRESENT value still succeeds (no over-reject)" : $"FAIL — threw={threw} count={race.MovementTypeNames?.Count}")}");
        }

        // ==================  VOICE (Layer B unit B) — on-disk .fuz/.lip presence check  ==================
        // The keystone path transform + the present / silent / undeterminable verdict over the REAL create path. The .fuz
        // path is computed from {defining plugin, speaker voice type, parent quest+topic EDIDs, response number}; a created
        // INFO with no .fuz on disk plays SILENT in game (the Q3 class this closes). An AssetResolver over a TEMP Data root
        // (planted / absent .fuz) makes the present/silent verdict CI-testable with no real load order.

        // ---------- VOICE-PATH: the pure transform locks the xEdit InfoFileName format (Quest[..10]_Topic[..15]_00+6hex_num) ----------
        bool voicePathOk;
        {
            var fk = new FormKey(new ModKey("MyPatch", ModType.Plugin), 0x000ABCu);
            var fuz = VoicePath.For(fk, "MaleNord", "QuestEditorIDLong", "TopicEditorIDLongerThan15", 3, VoiceFile.Fuz);
            var lip = VoicePath.For(fk, "MaleNord", "QuestEditorIDLong", "TopicEditorIDLongerThan15", 3, VoiceFile.Lip);
            const string expFuz = @"Sound\Voice\MyPatch.esp\MaleNord\QuestEdito_TopicEditorIDLo_00000ABC_3.fuz";
            const string expLip = @"Sound\Voice\MyPatch.esp\MaleNord\QuestEdito_TopicEditorIDLo_00000ABC_3.lip";
            voicePathOk = fuz == expFuz && lip == expLip;
            Console.WriteLine($"   VOICE-PATH transform format        : {(voicePathOk ? "PASS — quest[..10]_topic[..15]_00+6hex_num, .fuz/.lip" : $"FAIL — fuz=[{fuz}] lip=[{lip}]")}");
        }

        // ---------- VOICE-SILENT: a created voiced line with NO .fuz on disk reports WILL-BE-SILENT at the right path ----------
        bool voiceSilentOk = false;
        {
            string pPath = Path.Combine(tmpDir, "HcNcVoiceSilent.esp");
            var specs = new[]
            {
                new WritePatchBuilder.CreateSpec { RecordType = "DialogResponses", EditorId = "HcNcVsInfo", ParentRef = masterTopicFk.ToString(),
                    Edits = new[]
                    {
                        new WriteRequest { RecordType = "DialogResponses", Path = new[] { "Speaker" }, Verb = "Set", Value = masterNpcFk.ToString() },
                        new WriteRequest { RecordType = "DialogResponses", Path = new[] { "Responses" }, Verb = "Add",
                            Struct = new StructSpec { Type = "DialogResponse", Fields = new Dictionary<string, string> { ["ResponseNumber"] = "1" } } },
                    } },
            };
            using var r = LoadOrderResolver.Build(new[] { mPath });
            var o = WritePatchBuilder.CreateRecords(r, rulebook, specs, pPath, extend: false);
            var dataDir = Path.Combine(tmpDir, "voice-silent-data"); Directory.CreateDirectory(dataDir);   // EMPTY — no .fuz planted
            using var assets = AssetResolver.Build("", "", dataDir, Array.Empty<string>(), Array.Empty<ActiveArchive>());
            var report = o.Success ? VoiceCheck.Run(pPath, o.Created, r, assets) : VoiceReport.Empty;
            var exp = o.Success ? VoicePath.For(o.Created[0].FormKey, "HcNcGdVoice", "HcNcGdQuest", "HcNcGdTopic", 1, VoiceFile.Fuz) : "";
            var line = report.Lines.Count == 1 ? report.Lines[0] : null;
            voiceSilentOk = o.Success && line is not null && !line.FuzPresent && line.FuzPath == exp && line.ResponseNumber == 1 && report.Undetermined.Count == 0;
            Console.WriteLine($"   VOICE-SILENT no .fuz -> silent     : {(voiceSilentOk ? $"PASS — 1 line, absent, path={exp}" : $"FAIL — success={o.Success} lines={report.Lines.Count} undet={report.Undetermined.Count} present={line?.FuzPresent} path=[{line?.FuzPath}] exp=[{exp}] err=[{o.Error}]")}");
        }

        // ---------- VOICE-PRESENT: planting the .fuz at the computed path reports voice present (winner = Data) ----------
        bool voicePresentOk = false;
        {
            string pPath = Path.Combine(tmpDir, "HcNcVoicePresent.esp");
            var specs = new[]
            {
                new WritePatchBuilder.CreateSpec { RecordType = "DialogResponses", EditorId = "HcNcVpInfo", ParentRef = masterTopicFk.ToString(),
                    Edits = new[]
                    {
                        new WriteRequest { RecordType = "DialogResponses", Path = new[] { "Speaker" }, Verb = "Set", Value = masterNpcFk.ToString() },
                        new WriteRequest { RecordType = "DialogResponses", Path = new[] { "Responses" }, Verb = "Add",
                            Struct = new StructSpec { Type = "DialogResponse", Fields = new Dictionary<string, string> { ["ResponseNumber"] = "1" } } },
                    } },
            };
            using var r = LoadOrderResolver.Build(new[] { mPath });
            var o = WritePatchBuilder.CreateRecords(r, rulebook, specs, pPath, extend: false);
            var dataDir = Path.Combine(tmpDir, "voice-present-data");
            string? exp = null; bool planted = false;
            if (o.Success)
            {
                exp = VoicePath.For(o.Created[0].FormKey, "HcNcGdVoice", "HcNcGdQuest", "HcNcGdTopic", 1, VoiceFile.Fuz);
                var expLip = VoicePath.For(o.Created[0].FormKey, "HcNcGdVoice", "HcNcGdQuest", "HcNcGdTopic", 1, VoiceFile.Lip);
                foreach (var rel in new[] { exp, expLip })   // plant BOTH the .fuz and the .lip — exercise both legs of CheckInfo
                {
                    var full = Path.Combine(dataDir, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    File.WriteAllBytes(full, new byte[] { 0, 1, 2 });
                }
                planted = true;
            }
            else Directory.CreateDirectory(dataDir);
            using var assets = AssetResolver.Build("", "", dataDir, Array.Empty<string>(), Array.Empty<ActiveArchive>());
            var report = o.Success ? VoiceCheck.Run(pPath, o.Created, r, assets) : VoiceReport.Empty;
            var line = report.Lines.Count == 1 ? report.Lines[0] : null;
            voicePresentOk = o.Success && planted && line is not null && line.FuzPresent && line.LipPresent && line.FuzPath == exp && line.FuzWinner == "Data";
            Console.WriteLine($"   VOICE-PRESENT planted .fuz+.lip     : {(voicePresentOk ? $"PASS — 1 line, .fuz+.lip present (Data), path={exp}" : $"FAIL — success={o.Success} lines={report.Lines.Count} fuz={line?.FuzPresent} lip={line?.LipPresent} winner=[{line?.FuzWinner}] path=[{line?.FuzPath}] err=[{o.Error}]")}");
        }

        // ---------- VOICE-NOSPEAKER: a created line with no Speaker can't compute a path -> a NAMED undetermined (Q3) ----------
        bool voiceNoSpeakerOk = false;
        {
            string pPath = Path.Combine(tmpDir, "HcNcVoiceNoSpeaker.esp");
            var specs = new[]
            {
                new WritePatchBuilder.CreateSpec { RecordType = "DialogResponses", EditorId = "HcNcVnsInfo", ParentRef = masterTopicFk.ToString(),
                    Edits = new[]
                    {
                        new WriteRequest { RecordType = "DialogResponses", Path = new[] { "Responses" }, Verb = "Add",
                            Struct = new StructSpec { Type = "DialogResponse", Fields = new Dictionary<string, string> { ["ResponseNumber"] = "1" } } },
                    } },
            };
            using var r = LoadOrderResolver.Build(new[] { mPath });
            var o = WritePatchBuilder.CreateRecords(r, rulebook, specs, pPath, extend: false);
            var dataDir = Path.Combine(tmpDir, "voice-nospk-data"); Directory.CreateDirectory(dataDir);
            using var assets = AssetResolver.Build("", "", dataDir, Array.Empty<string>(), Array.Empty<ActiveArchive>());
            var report = o.Success ? VoiceCheck.Run(pPath, o.Created, r, assets) : VoiceReport.Empty;
            var u = report.Undetermined.Count == 1 ? report.Undetermined[0] : null;
            voiceNoSpeakerOk = o.Success && report.Lines.Count == 0 && u is not null && u.Info == o.Created[0].FormKey
                && u.Reason.Contains("Speaker", StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"   VOICE-NOSPEAKER undeterminable     : {(voiceNoSpeakerOk ? "PASS — no Speaker -> 1 NAMED undetermined, no lines" : $"FAIL — success={o.Success} lines={report.Lines.Count} undet={report.Undetermined.Count} reason=[{u?.Reason}] err=[{o.Error}]")}");
        }

        // ---------- VOICE-MULTIRESP: two response lines -> two .fuz paths, _1 and _2 (one per ResponseNumber) ----------
        bool voiceMultiOk = false;
        {
            string pPath = Path.Combine(tmpDir, "HcNcVoiceMulti.esp");
            var specs = new[]
            {
                new WritePatchBuilder.CreateSpec { RecordType = "DialogResponses", EditorId = "HcNcVmInfo", ParentRef = masterTopicFk.ToString(),
                    Edits = new[]
                    {
                        new WriteRequest { RecordType = "DialogResponses", Path = new[] { "Speaker" }, Verb = "Set", Value = masterNpcFk.ToString() },
                        // NON-SEQUENTIAL ResponseNumbers (5, 2) — so a positional i+1 impl (which would emit 1,2) is
                        // distinguishable from the real read of resp.ResponseNumber: the path must carry _5 / _2.
                        new WriteRequest { RecordType = "DialogResponses", Path = new[] { "Responses" }, Verb = "Add",
                            Struct = new StructSpec { Type = "DialogResponse", Fields = new Dictionary<string, string> { ["ResponseNumber"] = "5" } } },
                        new WriteRequest { RecordType = "DialogResponses", Path = new[] { "Responses" }, Verb = "Add",
                            Struct = new StructSpec { Type = "DialogResponse", Fields = new Dictionary<string, string> { ["ResponseNumber"] = "2" } } },
                    } },
            };
            using var r = LoadOrderResolver.Build(new[] { mPath });
            var o = WritePatchBuilder.CreateRecords(r, rulebook, specs, pPath, extend: false);
            var dataDir = Path.Combine(tmpDir, "voice-multi-data"); Directory.CreateDirectory(dataDir);
            using var assets = AssetResolver.Build("", "", dataDir, Array.Empty<string>(), Array.Empty<ActiveArchive>());
            var report = o.Success ? VoiceCheck.Run(pPath, o.Created, r, assets) : VoiceReport.Empty;
            var nums = report.Lines.Select(l => l.ResponseNumber).OrderBy(n => n).ToList();
            bool pathsKeyed = report.Lines.All(l => l.FuzPath.EndsWith($"_{l.ResponseNumber}.fuz", StringComparison.Ordinal));
            voiceMultiOk = o.Success && report.Lines.Count == 2 && nums.SequenceEqual(new[] { 2, 5 }) && pathsKeyed && report.Undetermined.Count == 0;
            Console.WriteLine($"   VOICE-MULTIRESP keyed by RespNum    : {(voiceMultiOk ? "PASS — 2 lines, ResponseNumbers {2,5}, each path carries its own _N (not positional)" : $"FAIL — success={o.Success} lines={report.Lines.Count} nums=[{string.Join(",", nums)}] pathsKeyed={pathsKeyed} err=[{o.Error}]")}");
        }

        // ---------- VOICE-SAMECALL: speaker NPC + its VoiceType created in the SAME call -> patch-first resolution ----------
        // The other voice arms resolve speaker/voice/quest from the MASTER (the load-order arm of Resolve). This one
        // creates the VoiceType + the speaker NPC (Voice=@it) alongside the voiced INFO in ONE bulk_create, so the
        // speaker chain resolves through patchByKey (same-call records) — a regression dropping that arm would otherwise
        // pass every other voice arm GREEN.
        bool voiceSameCallOk = false;
        {
            string pPath = Path.Combine(tmpDir, "HcNcVoiceSameCall.esp");
            var specs = new[]
            {
                new WritePatchBuilder.CreateSpec { RecordType = "VoiceType", EditorId = "HcScVoice", Edits = Array.Empty<WriteRequest>() },
                new WritePatchBuilder.CreateSpec { RecordType = "Npc", EditorId = "HcScNpc",
                    Edits = new[] { new WriteRequest { RecordType = "Npc", Path = new[] { "Voice" }, Verb = "Set", Value = "@HcScVoice" } } },
                new WritePatchBuilder.CreateSpec { RecordType = "DialogResponses", EditorId = "HcScInfo", ParentRef = masterTopicFk.ToString(),
                    Edits = new[]
                    {
                        new WriteRequest { RecordType = "DialogResponses", Path = new[] { "Speaker" }, Verb = "Set", Value = "@HcScNpc" },
                        new WriteRequest { RecordType = "DialogResponses", Path = new[] { "Responses" }, Verb = "Add",
                            Struct = new StructSpec { Type = "DialogResponse", Fields = new Dictionary<string, string> { ["ResponseNumber"] = "1" } } },
                    } },
            };
            using var r = LoadOrderResolver.Build(new[] { mPath });
            var o = WritePatchBuilder.CreateRecords(r, rulebook, specs, pPath, extend: false);
            var infoFk = o.Success ? o.Created.First(c => c.RecordType == "DialogResponses").FormKey : default;
            var dataDir = Path.Combine(tmpDir, "voice-samecall-data");
            string? exp = null;
            if (o.Success)
            {
                exp = VoicePath.For(infoFk, "HcScVoice", "HcNcGdQuest", "HcNcGdTopic", 1, VoiceFile.Fuz);
                var full = Path.Combine(dataDir, exp);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllBytes(full, new byte[] { 0, 1, 2 });
            }
            else Directory.CreateDirectory(dataDir);
            using var assets = AssetResolver.Build("", "", dataDir, Array.Empty<string>(), Array.Empty<ActiveArchive>());
            var report = o.Success ? VoiceCheck.Run(pPath, o.Created, r, assets) : VoiceReport.Empty;
            var line = report.Lines.Count == 1 ? report.Lines[0] : null;
            voiceSameCallOk = o.Success && line is not null && line.FuzPresent && line.FuzPath == exp && report.Undetermined.Count == 0;
            Console.WriteLine($"   VOICE-SAMECALL patch-resolved chain : {(voiceSameCallOk ? $"PASS — speaker+voice from the SAME call (patchByKey), present at {exp}" : $"FAIL — success={o.Success} lines={report.Lines.Count} undet={report.Undetermined.Count} present={line?.FuzPresent} path=[{line?.FuzPath}] exp=[{exp}] err=[{o.Error}]")}");
        }

        // ---------- VOICE-CHECKERROR: a check failure SURFACES on CheckError, never throws / never demotes the create ----
        // The create succeeds; the voice check is then run against a CORRUPT patch path so the overlay-open throws.
        // VoiceCheck must catch it and return CheckError (not rethrow, not lose the created records) — the Q3 safety net.
        bool voiceCheckErrorOk = false;
        {
            string pPath = Path.Combine(tmpDir, "HcNcVoiceCkErr.esp");
            var specs = new[]
            {
                new WritePatchBuilder.CreateSpec { RecordType = "DialogResponses", EditorId = "HcNcCeInfo", ParentRef = masterTopicFk.ToString(),
                    Edits = new[]
                    {
                        new WriteRequest { RecordType = "DialogResponses", Path = new[] { "Speaker" }, Verb = "Set", Value = masterNpcFk.ToString() },
                        new WriteRequest { RecordType = "DialogResponses", Path = new[] { "Responses" }, Verb = "Add",
                            Struct = new StructSpec { Type = "DialogResponse", Fields = new Dictionary<string, string> { ["ResponseNumber"] = "1" } } },
                    } },
            };
            using var r = LoadOrderResolver.Build(new[] { mPath });
            var o = WritePatchBuilder.CreateRecords(r, rulebook, specs, pPath, extend: false);
            var dataDir = Path.Combine(tmpDir, "voice-ckerr-data"); Directory.CreateDirectory(dataDir);
            using var assets = AssetResolver.Build("", "", dataDir, Array.Empty<string>(), Array.Empty<ActiveArchive>());
            var corruptPath = Path.Combine(tmpDir, "HcNcVoiceCorrupt.esp");
            File.WriteAllText(corruptPath, "this is not a valid Skyrim plugin");
            VoiceReport report = VoiceReport.Empty; bool threw = false;
            try { report = o.Success ? VoiceCheck.Run(corruptPath, o.Created, r, assets) : VoiceReport.Empty; }
            catch { threw = true; }
            voiceCheckErrorOk = o.Success && !threw && report.CheckError is not null && report.Lines.Count == 0;
            Console.WriteLine($"   VOICE-CHECKERROR surfaced not thrown: {(voiceCheckErrorOk ? "PASS — corrupt patch -> CheckError set, no throw, no lines" : $"FAIL — success={o.Success} threw={threw} checkError=[{report.CheckError}] lines={report.Lines.Count} err=[{o.Error}]")}");
        }

        // ==================  RESULT-SCRIPT (Layer B unit C) — per-create VMAD binding + .pex presence check  ==================
        // A dialogue line can carry a RESULT SCRIPT (a Papyrus fragment run when the line plays) on its VMAD: a
        // ScriptFragments fragment (FileName + a Begin/End fragment) and/or attached Scripts. A byte-valid INFO whose
        // binding is half-built, or names a script with no compiled Scripts\<class>.pex on disk, runs NOTHING in game
        // (the Q3 class this closes; plan §3 job 3). DialogueScriptCheck verdicts each CREATED scripted INFO. A temp
        // Data root (planted / absent .pex) makes the bound / incomplete / not-compiled verdict CI-testable. Fixtures
        // are built directly (a topic + an INFO with a configured VMAD), then serialized + re-opened the way the check
        // sees a real written patch.
        (bool ok, string path, FormKey infoFk) BuildScriptFixture(string name, Action<DialogResponses> configure)
        {
            try
            {
                var key = new ModKey(name, ModType.Plugin);
                var path = Path.Combine(tmpDir, key.FileName.String);
                var fm = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
                var topic = fm.DialogTopics.AddNew(); topic.EditorID = name + "Topic";
                var info = new DialogResponses(fm.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = name + "Info" };
                configure(info);
                topic.Responses.Add(info);
                fm.BeginWrite.ToPath(path).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
                return (true, path, info.FormKey);
            }
            catch (Exception ex) { Console.WriteLine($"   (script fixture '{name}' failed: {ex.GetType().Name}: {(ex.InnerException ?? ex).Message})"); return (false, "", default); }
        }
        WritePatchBuilder.CreatedRecord[] AsCreated(string name, FormKey fk)
            => new[] { new WritePatchBuilder.CreatedRecord(fk, "DialogResponses", name + "Info", Array.Empty<WritePatchBuilder.OpResult>()) };

        // ---------- SCRIPT-INCOMPLETE: a VMAD present but binding nothing usable -> BindingIncomplete (won't fire) ----------
        bool scriptIncompleteOk = false;
        {
            var f = BuildScriptFixture("HcScIncomplete", info => info.VirtualMachineAdapter = new DialogResponsesAdapter());
            var dataDir = Path.Combine(tmpDir, "script-incomplete-data"); Directory.CreateDirectory(dataDir);
            using var assets = AssetResolver.Build("", "", dataDir, Array.Empty<string>(), Array.Empty<ActiveArchive>());
            var report = f.ok ? DialogueScriptCheck.Run(f.path, AsCreated("HcScIncomplete", f.infoFk), assets) : ScriptBindingReport.Empty;
            var find = report.Findings.Count == 1 ? report.Findings[0] : null;
            scriptIncompleteOk = f.ok && find is not null && find.Status == ScriptBindingStatus.BindingIncomplete && find.Info == f.infoFk;
            Console.WriteLine($"   SCRIPT-INCOMPLETE empty VMAD       : {(scriptIncompleteOk ? "PASS — VMAD present, binds nothing -> BindingIncomplete" : $"FAIL — ok={f.ok} findings={report.Findings.Count} status={find?.Status} err=[{report.CheckError}]")}");
        }

        // ---------- SCRIPT-NOFRAG: a ScriptFragments FileName with NO Begin/End fragment is hollow -> BindingIncomplete ----------
        // The .pex IS planted, so a "FileName alone counts as bound" regression would mis-read this as BoundAndCompiled
        // (not NotCompiled) — the planted .pex is what makes that regression visible here.
        bool scriptNoFragOk = false;
        {
            var f = BuildScriptFixture("HcScNoFrag", info =>
                info.VirtualMachineAdapter = new DialogResponsesAdapter { ScriptFragments = new ScriptFragments { FileName = "HcScNoFragClass" } });
            var dataDir = Path.Combine(tmpDir, "script-nofrag-data"); Directory.CreateDirectory(dataDir);
            var planted = Path.Combine(dataDir, @"Scripts\HcScNoFragClass.pex");
            Directory.CreateDirectory(Path.GetDirectoryName(planted)!); File.WriteAllBytes(planted, new byte[] { 0, 1, 2 });
            using var assets = AssetResolver.Build("", "", dataDir, Array.Empty<string>(), Array.Empty<ActiveArchive>());
            var report = f.ok ? DialogueScriptCheck.Run(f.path, AsCreated("HcScNoFrag", f.infoFk), assets) : ScriptBindingReport.Empty;
            var find = report.Findings.Count == 1 ? report.Findings[0] : null;
            scriptNoFragOk = f.ok && find is not null && find.Status == ScriptBindingStatus.BindingIncomplete;
            Console.WriteLine($"   SCRIPT-NOFRAG FileName, no fragment: {(scriptNoFragOk ? "PASS — FileName alone (no Begin/End) -> BindingIncomplete even with a .pex on disk" : $"FAIL — ok={f.ok} findings={report.Findings.Count} status={find?.Status} err=[{report.CheckError}]")}");
        }

        // ---------- SCRIPT-NOTCOMPILED: a bound fragment whose Scripts\<class>.pex is absent -> ScriptNotCompiled ----------
        bool scriptNotCompiledOk = false;
        {
            var f = BuildScriptFixture("HcScNotComp", info =>
                info.VirtualMachineAdapter = new DialogResponsesAdapter { ScriptFragments = new ScriptFragments {
                    FileName = "HcScNotCompClass", OnEnd = new ScriptFragment { ScriptName = "HcScNotCompClass", FragmentName = "Fragment_0" } } });
            var dataDir = Path.Combine(tmpDir, "script-notcomp-data"); Directory.CreateDirectory(dataDir);   // EMPTY — no .pex planted
            using var assets = AssetResolver.Build("", "", dataDir, Array.Empty<string>(), Array.Empty<ActiveArchive>());
            var report = f.ok ? DialogueScriptCheck.Run(f.path, AsCreated("HcScNotComp", f.infoFk), assets) : ScriptBindingReport.Empty;
            var find = report.Findings.Count == 1 ? report.Findings[0] : null;
            scriptNotCompiledOk = f.ok && find is not null && find.Status == ScriptBindingStatus.ScriptNotCompiled
                && find.MissingPex.Count == 1 && find.MissingPex[0] == @"Scripts\HcScNotCompClass.pex";
            Console.WriteLine($"   SCRIPT-NOTCOMPILED no .pex         : {(scriptNotCompiledOk ? @"PASS — bound fragment, no Scripts\<class>.pex -> ScriptNotCompiled" : $"FAIL — ok={f.ok} findings={report.Findings.Count} status={find?.Status} missing=[{(find is null ? "" : string.Join(",", find.MissingPex))}] err=[{report.CheckError}]")}");
        }

        // ---------- SCRIPT-BOUND: a bound fragment WITH its compiled .pex on disk -> BoundAndCompiled ----------
        bool scriptBoundOk = false;
        {
            var f = BuildScriptFixture("HcScBound", info =>
                info.VirtualMachineAdapter = new DialogResponsesAdapter { ScriptFragments = new ScriptFragments {
                    FileName = "HcScBoundClass", OnEnd = new ScriptFragment { ScriptName = "HcScBoundClass", FragmentName = "Fragment_0" } } });
            var dataDir = Path.Combine(tmpDir, "script-bound-data");
            if (f.ok)
            {
                var planted = Path.Combine(dataDir, @"Scripts\HcScBoundClass.pex");
                Directory.CreateDirectory(Path.GetDirectoryName(planted)!); File.WriteAllBytes(planted, new byte[] { 0, 1, 2 });
            }
            else Directory.CreateDirectory(dataDir);
            using var assets = AssetResolver.Build("", "", dataDir, Array.Empty<string>(), Array.Empty<ActiveArchive>());
            var report = f.ok ? DialogueScriptCheck.Run(f.path, AsCreated("HcScBound", f.infoFk), assets) : ScriptBindingReport.Empty;
            var find = report.Findings.Count == 1 ? report.Findings[0] : null;
            scriptBoundOk = f.ok && find is not null && find.Status == ScriptBindingStatus.BoundAndCompiled && find.MissingPex.Count == 0;
            Console.WriteLine($"   SCRIPT-BOUND planted .pex          : {(scriptBoundOk ? "PASS — bound fragment + compiled .pex -> BoundAndCompiled" : $"FAIL — ok={f.ok} findings={report.Findings.Count} status={find?.Status} err=[{report.CheckError}]")}");
        }

        // ---------- SCRIPT-ATTACHED: an attached Scripts[] entry (not a fragment) is a bound class too -> checked for .pex ----------
        bool scriptAttachedOk = false;
        {
            var f = BuildScriptFixture("HcScAttached", info =>
            {
                var a = new DialogResponsesAdapter();
                a.Scripts.Add(new ScriptEntry { Name = "HcScAttachedClass" });
                info.VirtualMachineAdapter = a;
            });
            var dataDir = Path.Combine(tmpDir, "script-attached-data");
            if (f.ok)
            {
                var planted = Path.Combine(dataDir, @"Scripts\HcScAttachedClass.pex");
                Directory.CreateDirectory(Path.GetDirectoryName(planted)!); File.WriteAllBytes(planted, new byte[] { 0, 1, 2 });
            }
            else Directory.CreateDirectory(dataDir);
            using var assets = AssetResolver.Build("", "", dataDir, Array.Empty<string>(), Array.Empty<ActiveArchive>());
            var report = f.ok ? DialogueScriptCheck.Run(f.path, AsCreated("HcScAttached", f.infoFk), assets) : ScriptBindingReport.Empty;
            var find = report.Findings.Count == 1 ? report.Findings[0] : null;
            scriptAttachedOk = f.ok && find is not null && find.Status == ScriptBindingStatus.BoundAndCompiled && find.Scripts.Contains("HcScAttachedClass");
            Console.WriteLine($"   SCRIPT-ATTACHED Scripts[] entry    : {(scriptAttachedOk ? "PASS — attached Scripts[] class + .pex -> BoundAndCompiled" : $"FAIL — ok={f.ok} findings={report.Findings.Count} status={find?.Status} scripts=[{(find is null ? "" : string.Join(",", find.Scripts))}] err=[{report.CheckError}]")}");
        }

        // ---------- SCRIPT-NOVMAD: a created line with NO result script is NOT checked (no false-positive nag) ----------
        bool scriptNoVmadOk = false;
        {
            var f = BuildScriptFixture("HcScNoVmad", info => { });   // no VMAD configured
            var dataDir = Path.Combine(tmpDir, "script-novmad-data"); Directory.CreateDirectory(dataDir);
            using var assets = AssetResolver.Build("", "", dataDir, Array.Empty<string>(), Array.Empty<ActiveArchive>());
            var report = f.ok ? DialogueScriptCheck.Run(f.path, AsCreated("HcScNoVmad", f.infoFk), assets) : new ScriptBindingReport(Array.Empty<ScriptBindingFinding>()) { CheckError = "fixture failed" };
            scriptNoVmadOk = f.ok && report.IsEmpty;
            Console.WriteLine($"   SCRIPT-NOVMAD no script -> no nag   : {(scriptNoVmadOk ? "PASS — a line with no VMAD yields no finding (not nagged)" : $"FAIL — ok={f.ok} findings={report.Findings.Count} err=[{report.CheckError}]")}");
        }

        // ---------- SCRIPT-CHECKERROR: a check failure SURFACES on CheckError, never throws / never demotes the create ----------
        bool scriptCheckErrorOk = false;
        {
            var corruptPath = Path.Combine(tmpDir, "HcScCorrupt.esp");
            File.WriteAllText(corruptPath, "this is not a valid Skyrim plugin");
            var dataDir = Path.Combine(tmpDir, "script-ckerr-data"); Directory.CreateDirectory(dataDir);
            using var assets = AssetResolver.Build("", "", dataDir, Array.Empty<string>(), Array.Empty<ActiveArchive>());
            var created = new[] { new WritePatchBuilder.CreatedRecord(new FormKey(new ModKey("HcScCk", ModType.Plugin), 0x800), "DialogResponses", "HcScCkInfo", Array.Empty<WritePatchBuilder.OpResult>()) };
            ScriptBindingReport report = ScriptBindingReport.Empty; bool threw = false;
            try { report = DialogueScriptCheck.Run(corruptPath, created, assets); }
            catch { threw = true; }
            scriptCheckErrorOk = !threw && report.CheckError is not null && report.Findings.Count == 0;
            Console.WriteLine($"   SCRIPT-CHECKERROR surfaced not thrown:{(scriptCheckErrorOk ? "PASS — corrupt patch -> CheckError set, no throw, no findings" : $"FAIL — threw={threw} checkError=[{report.CheckError}] findings={report.Findings.Count}")}");
        }

        Console.WriteLine();
        bool pass = fixturesOk && oneshotOk && multiOk && intoTopicOk && intoCellOk
                    && rejNoParentOk && rejBadParentOk && rejAmbigOk && rejFwdSibOk && extendOk
                    && sibrefOk && sibRejFwdOk && sibRejNonflOk && sibRejListOk && sibRejDictOk && sibRejApplyOk
                    && flElemRejGateOk && flElemRejAddOk && flElemNullClearOk && flElemOkE2eOk && flElemRejE2eOk
                    && flElemRejNullAddOk && flElemRejNullAddPlainOk && flElemRejNullSetIdxOk && flElemRejNullAddE2eOk
                    && keyIdxRejDictAddOk && keyIdxRejDictRemoveOk && keyIdxRejSetIdxOk && keyIdxOkListRemoveOk && keyIdxRejSetIdxE2eOk
                    && keyShapeRejDictAddOk && keyShapeRejDictRemoveOk && keyShapeRejMergeKeyOk && keyShapeRejSbyteOk
                    && keyShapeRejSetIdxOk && keyShapeRejNegIdxOk && keyShapeRejListRemoveIdxOk
                    && keyShapeOkDictAddOk && keyShapeOkSetIdxOk && keyShapeOkNumEnumSetOk && keyShapeRejE2eOk
                    && gap1RejMidKeySbyteOk && gap1OkMidKeyOk && gap1RejE2eOk
                    && gap1RejMidListIdxNegOk && gap1OkMidListIdxOk && gap1RejNegIdxE2eOk
                    && gap2RejDictAddOk && gap2RejListAddOk && gap2RejListReplaceAllOk && gap2RejListRemoveOk && gap2RejDictMergeOk
                    && gap2OkValidOk && gap2OkRemoveByIndexOk && gap2FormlinkRouteOk && gap2OkOffcardSlotOk && gap2RejE2eOk
                    && g6RejRecordAddOk && g6RejRecordReplaceAllOk && g6OkRemoveByIndexOk && g6OkStructUnchangedOk && g6RejE2eOk
                    && g4RejCtorArgShapeOk && g4RejCtorArgArityOk && g4OkCtorArgOk && g4OkNoCtorArgsOk && g4RejCtorArgE2eOk
                    && g7RejDictMergeOk && g7RejComposableRemoveOk && g7RejRecordRemoveOk && g7OkComposableRemoveIdxOk && g7OkDictRemoveKeyOk
                    && gap3OkDictAddComposeOk && gap3OkDictSetComposeOk && gap3RejBadArmOk && gap3OkListUnchangedOk
                    && gap3OkE2eOk && gap3OkE2eSetOk && gap3RejDupOk
                    && gap3RejBaseArmOk && gap3RejBaseArmListOk && gap3OkBaseNoOverRejectOk && gap3RejBaseArmE2eOk
                    && gap3RejBaseArmFieldOk && gap3OkArmFieldUnchangedOk
                    && expectedRejSetIdxOobOk && expectedRejRemoveIdxOobOk && expectedOkSetIdxInRangeOk && expectedNavTypeOk
                    && expectedRejNavE2eOk
                    && malformedNavTypeOk
                    && removeRejDictKeyAbsentOk && removeRejNullCollOk && removeRejListValAbsentOk
                    && removeOkPresentDictOk && removeOkPresentListOk
                    && voicePathOk && voiceSilentOk && voicePresentOk && voiceNoSpeakerOk && voiceMultiOk
                    && voiceSameCallOk && voiceCheckErrorOk
                    && scriptIncompleteOk && scriptNoFragOk && scriptNotCompiledOk && scriptBoundOk
                    && scriptAttachedOk && scriptNoVmadOk && scriptCheckErrorOk;
        Console.WriteLine($"=== nested-create-guard: {(pass ? "PASS" : "FAIL")} ===");
        try { Directory.Delete(tmpDir, recursive: true); } catch { }
        return pass ? 0 : 1;
    }

    /// <summary>Drive a create expected to REFUSE (extend=false, fresh path): assert Success=false, NO file written,
    /// and the error matches <paramref name="msgOk"/> (the same shape as the manual proof's RejectCheck).</summary>
    static bool RejectArm(string banner, string tmpDir, string tag, string mPath, CorpusRulebook rulebook,
        WritePatchBuilder.CreateSpec[] specs, Func<string, bool> msgOk)
    {
        string pPath = Path.Combine(tmpDir, $"HcNcRej{tag}.esp");
        bool refused; string? error;
        using (var r = LoadOrderResolver.Build(new[] { mPath }))
        {
            var o = WritePatchBuilder.CreateRecords(r, rulebook, specs, pPath, extend: false);
            refused = !o.Success; error = o.Error;
        }
        bool noFile = !File.Exists(pPath);
        bool named = error is not null && msgOk(error);
        bool ok = refused && noFile && named;
        Console.WriteLine($"   {banner}: {(ok ? "PASS — refused by name, no file written" : $"FAIL — refused={refused} noFile={noFile} named={named} err=[{error}]")}");
        return ok;
    }

    /// <summary>Re-open the written patch and confirm a Package's Data dict carries a composed PackageDataBool(Data=true)
    /// at key 0 — the Gap-3 dict-element composition round-trip (the value was built FROM PARTS, not coerced).</summary>
    static bool PackageDataComposedBool(string patchPath, FormKey packFk)
    {
        ISkyrimModGetter? ov = null;
        try
        {
            ov = SkyrimMod.CreateFromBinaryOverlay(patchPath, SkyrimRelease.SkyrimSE);
            var p = ov.Packages.FirstOrDefault(x => x.FormKey == packFk);
            if (p?.Data is null) return false;
            return p.Data.TryGetValue((sbyte)0, out var d) && d is IPackageDataBoolGetter b && b.Data;
        }
        catch { return false; }
        finally { (ov as IDisposable)?.Dispose(); }
    }

    /// <summary>Re-open the written patch and list a new topic's (by EditorID) child INFO FormKeys.</summary>
    static List<FormKey>? TopicResponses(string patchPath, string topicEditorId)
    {
        ISkyrimModGetter? ov = null;
        try
        {
            ov = SkyrimMod.CreateFromBinaryOverlay(patchPath, SkyrimRelease.SkyrimSE);
            var t = ov.DialogTopics.FirstOrDefault(x => x.EditorID == topicEditorId);
            return t?.Responses.Select(x => x.FormKey).ToList();
        }
        catch { return null; }
        finally { (ov as IDisposable)?.Dispose(); }
    }

    /// <summary>Re-open the written patch and list a topic's (by FormKey) child INFO FormKeys.</summary>
    static List<FormKey>? TopicResponses(string patchPath, FormKey topicFk)
    {
        ISkyrimModGetter? ov = null;
        try
        {
            ov = SkyrimMod.CreateFromBinaryOverlay(patchPath, SkyrimRelease.SkyrimSE);
            var t = ov.DialogTopics.FirstOrDefault(x => x.FormKey == topicFk);
            return t?.Responses.Select(x => x.FormKey).ToList();
        }
        catch { return null; }
        finally { (ov as IDisposable)?.Dispose(); }
    }

    /// <summary>Re-open the written patch and read a created INFO's Prompt (the field-edit check).</summary>
    static string? InfoPrompt(string patchPath, FormKey infoFk)
    {
        ISkyrimModGetter? ov = null;
        try
        {
            ov = SkyrimMod.CreateFromBinaryOverlay(patchPath, SkyrimRelease.SkyrimSE);
            foreach (var t in ov.DialogTopics)
                foreach (var info in t.Responses)
                    if (info.FormKey == infoFk) return info.Prompt?.String;
            return null;
        }
        catch { return null; }
        finally { (ov as IDisposable)?.Dispose(); }
    }

    /// <summary>Re-open the written patch and list a cell's (by FormKey) Persistent placed-ref FormKeys.</summary>
    static List<FormKey>? CellPersistent(string patchPath, FormKey cellFk)
    {
        ISkyrimModGetter? ov = null;
        try
        {
            ov = SkyrimMod.CreateFromBinaryOverlay(patchPath, SkyrimRelease.SkyrimSE);
            foreach (var block in ov.Cells)
                foreach (var sub in block.SubBlocks)
                    foreach (var c in sub.Cells)
                        if (c.FormKey == cellFk) return c.Persistent.Select(x => x.FormKey).ToList();
            return null;
        }
        catch { return null; }
        finally { (ov as IDisposable)?.Dispose(); }
    }

    /// <summary>Re-open the written patch and read a created INFO's Topic back-link FormKey (the @editorid sibling-ref
    /// arm). FormKey.Null if unset.</summary>
    static FormKey? InfoTopic(string patchPath, FormKey infoFk) => InfoFormLink(patchPath, infoFk, i => i.Topic.FormKey);

    /// <summary>Re-open the written patch and read a created INFO's PreviousDialog (PNAM) FormKey. FormKey.Null if unset.</summary>
    static FormKey? InfoPreviousDialog(string patchPath, FormKey infoFk) => InfoFormLink(patchPath, infoFk, i => i.PreviousDialog.FormKey);

    /// <summary>Re-open the written patch, find a created INFO by FormKey (under any topic's Responses), and project a
    /// FormLink field off it — shared by the Topic / PreviousDialog readers.</summary>
    static FormKey? InfoFormLink(string patchPath, FormKey infoFk, Func<IDialogResponsesGetter, FormKey> select)
    {
        ISkyrimModGetter? ov = null;
        try
        {
            ov = SkyrimMod.CreateFromBinaryOverlay(patchPath, SkyrimRelease.SkyrimSE);
            foreach (var t in ov.DialogTopics)
                foreach (var info in t.Responses)
                    if (info.FormKey == infoFk) return select(info);
            return null;
        }
        catch { return null; }
        finally { (ov as IDisposable)?.Dispose(); }
    }

    /// <summary>Re-open the written patch and list a created INFO's LinkTo (TCLT) element FormKeys — the valid
    /// formlink-ELEMENT round-trip check (FLELEM-OK-E2E). Null if the INFO isn't found.</summary>
    static List<FormKey>? InfoLinkTo(string patchPath, FormKey infoFk)
    {
        ISkyrimModGetter? ov = null;
        try
        {
            ov = SkyrimMod.CreateFromBinaryOverlay(patchPath, SkyrimRelease.SkyrimSE);
            foreach (var t in ov.DialogTopics)
                foreach (var info in t.Responses)
                    if (info.FormKey == infoFk) return info.LinkTo.Select(x => x.FormKey).ToList();
            return null;
        }
        catch { return null; }
        finally { (ov as IDisposable)?.Dispose(); }
    }
}
