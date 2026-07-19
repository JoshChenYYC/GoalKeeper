# Use Luna first for hosted reasoning

**Status:** Accepted

**Decision date:** 2026-07-18

**Decision owner:** GoalKeeper

## Context

The hosted Reasoning adapter evaluates every reasoning-eligible Observation.
The initial provider decision in ADR 0002 selected `gpt-5.6-sol` because
temporal interpretation is a central quality risk. Early cost evidence showed
that Sol cache writes and output were the dominant spend, while the current
bounded request, strict schema, controller validation, and fail-safe behavior
already constrain the task substantially.

OpenAI documents `gpt-5.6-luna` as the cost-sensitive, high-volume tier in the
GPT-5.6 family. The application needs evidence that a stronger tier materially
improves accepted decisions before paying that premium on every Observation.

## Decision

Supersede only the Reasoning-model row of ADR 0002. Use OpenAI
`gpt-5.6-luna` for hosted Reasoning through the Responses API with the existing
strict JSON Schema, `reasoning.effort: medium`, `store: false`, no tools, prompt,
request context, repair attempt, and controller-side validation.

Keeping medium effort and every other request variable unchanged establishes a
model-only baseline. The application does not automatically fall back to a
stronger model after a Luna failure; existing technical-failure and
continue-observing behavior remains authoritative.

Consider promotion in this order:

1. Keep Luna when representative live evaluations meet the quality gate.
2. Evaluate Terra with the same prompt and effort when Luna has repeatable
   semantic failures that are not caused by input quality, prompt ambiguity,
   schema validation, or controller policy.
3. Evaluate Sol only when Terra still fails the same quality-critical cases.

Promote only when the stronger model produces a material, repeatable improvement
in accepted decision quality that justifies its additional cost and latency.

## Quality gate

Automated contract tests must establish that Luna preserves the exact request,
strict response schema, repair limit, safe metadata, fail-closed behavior, and
host configuration. A live evaluation must then cover representative examples
of:

- ambiguous or insufficient evidence that should continue observing;
- a sustained listed deviation that should begin a Recovery Check-in;
- contradictory observations that should not trigger prematurely;
- stale, invalid, or technically failed evidence that must not become
  behavioral evidence; and
- correct observation citations and episode-memory updates.

Record structured-output validity, accepted/rejected decisions, false-positive
and false-negative outcomes, latency, input/output/reasoning tokens, cache reads
and writes, and cost per accepted evaluation. Contract tests alone do not prove
that Luna's model judgment is sufficient.

## Consequences

- Luna becomes the exact hosted Reasoning model accepted by startup validation.
- Perception remains on Luna, Recovery conversation remains on Terra, and all
  audio models remain unchanged.
- ADR 0002 and GK-016 evidence remain historical records of the Sol baseline.
- A future move to Terra or Sol requires recorded live comparison evidence and
  a superseding decision.
