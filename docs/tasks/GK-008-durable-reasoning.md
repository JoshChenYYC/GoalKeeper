# GK-008 — Durable Reasoning core and deterministic fake

**Status:** Done
**Owner:** Codex `/root/gk008_reasoning`
**Branch:** `task/GK-008-durable-reasoning`
**Merged via:** PR #8
**Depends on:** GK-003, GK-005
**Suggested branch:** `task/GK-008-durable-reasoning`

## Outcome

Provider-neutral Reasoning builds bounded requests from durable state, validates evidence-linked proposals, persists every evaluation, and supports long sessions without relying on a model context window as memory.

## Owned surface

- Reasoning request/proposal contracts and orchestration under `GoalKeeper.Application`
- Deterministic Evidence Episode policy under `GoalKeeper.Domain`
- Deterministic Reasoning fake and Reasoning unit/integration tests

Do not add hosted transport, camera scheduling, Recovery dialogue, controller state mutation, or UI.

## Work

- Build requests from the immutable contract, overrides, active/historical episode summaries, Recovery summaries, the new Observation, and a bounded recent window.
- Maintain compact durable Evidence Episodes and compact older Observations into summaries.
- Support only `continue_observing` and `begin_recovery_check_in` proposals.
- Validate listed/unlisted mode, same-session persisted references, ordering, freshness, session version, current state, and bounded key/contradictory references.
- Derive authoritative evidence times from persisted Observation references.
- Persist every evaluation, including continue, rejected, stale, and technical results, with validation reasons. Rejected results must not mutate session, episode, Intervention, or timer state.
- Provide scripts for continue, listed Intervention, exploratory Intervention, stale result, invalid references, and thrown failure.

## Acceptance criteria

- Every accepted Intervention proposal is reconstructable from persisted Observations.
- Cross-session, nonexistent, reordered, stale, superseded, failed, and Profile Only unlisted references are rejected and recorded.
- Long-session tests prove Observation count and serialized request size remain bounded.
- The deterministic fake can drive every controller scenario without network access.
- Reasoning receives no room image and never mutates a Focus Session directly.

## Out of scope

- Hosted model prompt/transport
- Controller admission and timer reconciliation
- Intervention-quality evaluation across sessions
