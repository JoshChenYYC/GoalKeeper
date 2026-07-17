# GK-007 — Capture/Perception pipeline and monitoring health

**Status:** Ready
**Depends on:** GK-003, GK-004, GK-005
**Suggested branch:** `task/GK-007-monitoring-pipeline`

## Outcome

Provider-neutral orchestration completes mandatory preflight, retains fixed-cadence snapshots, processes only one Perception request at a time, persists fresh validated Observations, and emits monitoring-health events for sustained camera or Perception failures.

## Owned surface

- Monitoring and preflight orchestration under `GoalKeeper.Application`
- Snapshot artifact coordination under `GoalKeeper.Infrastructure`
- Pipeline integration tests using fake camera, Perception, clock, and repositories

Consume GK-003 through GK-005 interfaces. Do not redesign their contracts, add a hosted provider adapter, or mutate Focus Session state directly.

## Work

- Combine camera acquisition, Perception image-quality/person-count validation, and explicit user confirmation into the mandatory preflight decision.
- Implement fixed-grid capture scheduling without burst catch-up.
- Keep one active Perception cycle and one newest pending frame; mark replaced frames `superseded`.
- Retain every captured JPEG locally and persist controller-owned sequence, ID, UTC, monotonic, and session-version metadata.
- Finalize each snapshot as `observed`, `stale`, or `agent_error` through atomic repository operations.
- Enforce a configurable Observation freshness limit before any result is exposed to Reasoning.
- Exclude Scheduled Break captures and all technical failures from behavioral evidence.
- Aggregate consecutive camera/Perception failures from the first failed operation and emit technical-grace expiry/recovery events.
- Make cancellation and terminal cleanup drain work and release camera resources.

## Acceptance criteria

- Preflight cannot pass without an acceptable Perception result and explicit confirmation; failures support retry or cancel.
- Fake-clock tests prove fixed-grid cadence and no burst catch-up.
- Slow Perception keeps only the in-flight and newest pending frame while every capture remains auditable.
- Stale, invalid, break-time, superseded, and failed results cannot be queried as Reasoning-eligible.
- Technical grace starts at the first sustained failure and clears after successful health recovery.
- Camera release is proven for every pipeline exit; no test needs hardware, credentials, or network.

## Out of scope

- Semantic Reasoning or Evidence Episode updates
- Focus Session state mutation for Monitoring Unavailable; GK-011 consumes health events
- Voice and UI
