# bulk-record-jobs — trigger fan-out results (2026-07-15)

Method: HOUSECARL_SKILL_AUTHORING §6.5 — one fresh-context agent per query, anonymized
capability statement (no skill name, no tool names), pure relevance question, adjudicated from
each agent's REASONING (not the bare verdict token). Eval set: `eval_set.json` (9 should-trigger,
10 should-not-trigger near-misses).

## Scores (description as shipped in this PR)

- **Recall: 9/9 = 100%** (threshold ≥ 80%) — all catalogue / conflict-survey / patch-rebuild /
  fan-out / JSON-extraction phrasings judged in-scope with correct reasoning, including the
  casual/typo variant and the subagent-orchestration phrasing.
- **Specificity: 9/10 = 90%** (threshold ≥ 50%).

## The one miss

"Which armors sit on biped slot 52 in my load order?" fired (expected quiet — `biped-slot-reference`'s
lane). The judging agent's reasoning was honest: the request IS many-records → one-list, so it
pattern-matches the capability. Accepted per the asymmetric-cost rule (§3.3: over-trigger wastes
~100 words; under-trigger loses the session) — and the skill body's Sub-topic routing table hands
the slot question to `biped-slot-reference` on load, so the cost of the false fire is one hop.

Re-measure on any description change (§6.5 step 6).
