# GK-003 — Runtime state and persistence foundation

**Status:** Done
**Owner:** Codex `/root`
**Branch:** `main` working tree
**Depends on:** None
**Suggested branch:** `task/GK-003-runtime-persistence`

## Outcome

The deterministic Focus Session can be persisted, rehydrated, and updated atomically, and application-facing repositories support every Phase 3–7 record without exposing EF entities or forcing parallel tasks to edit shared persistence files.

## Owned surface

- `FocusSession`, Focus Timer snapshotting, Evidence value validation, and exhaustive FSM tests in `GoalKeeper.Domain`
- Runtime/persistence contracts and DTOs in `GoalKeeper.Application`
- EF entities, mappings, repositories, and the next migration in `GoalKeeper.Infrastructure`
- Runtime and persistence integration tests

This task is the sole owner of the next EF migration and shared runtime contract. Do not edit `Program.cs`; GK-011 owns runtime composition.

## Work

- Define a complete Focus Session runtime snapshot containing timer accumulation/running point, break index/deadline, projected end, active Intervention/dispute, Recovery Window/counter, response deadline, monitoring outage, terminal fields, and version.
- Rehydrate the aggregate from a snapshot without bypassing invariants or taking a second Goal lock.
- Make every command validate before mutation; a rejected command must leave all fields and version unchanged.
- Increment session version for every durable mutation, including remainder overrides and Session Review submission.
- Prevent public record constructors from bypassing factory validation, or validate on every aggregate boundary.
- Replace partial invalid-transition examples with an exhaustive command/state transition matrix.
- Map complete runtime state in EF and publish one authoritative load/update contract that returns application DTOs rather than EF entities.
- Define two explicit transaction paths. The accepted path appends the evaluation, compares the expected session version, and saves authoritative mutations atomically. If version comparison identifies a stale/rejected result, roll back that path and use a separate append-only rejection path that records the evaluation/reason without changing session, episode, Intervention, or timer state.
- Enforce one active Focus Session at both database and hosted-runtime boundaries; the database constraint must cover sessions across Goals.
- Add standalone Focus Session deletion and Session Setup Ready/Started/Cancelled transitions.
- Complete foreign keys/cascades for Observations, Evaluations, Episodes and references, Interventions, Recovery turns, Overrides, Reviews, audits, and snapshots.
- Add repository operations for snapshot lifecycle, versioned Observations, durable Reasoning/Recovery records, recent windows, history, reviews, storage use, and the complete Focus Session update path.
- Represent `captured`, `superseded`, `observed`, `stale`, and `agent_error` explicitly.

## Acceptance criteria

- A runtime snapshot round-trips every nonterminal and terminal state through SQLite and resumes deterministic fake-clock behavior.
- A rejected domain command changes no aggregate field or version and creates no success/business-state record. If the command contract calls for an attempt audit, only that append-only record is retained.
- A stale or rejected agent evaluation is retained with its rejection reason while session, episode, Intervention, and timer state remain unchanged.
- A storage or optimistic conflict rolls back every proposed success mutation. After the current version is reread and the result is classified as stale/rejected, only the separate rejection path may append its evaluation/audit record.
- The database rejects a second active Focus Session even when it belongs to another Goal or controller instance.
- Cross-session references and duplicate sequence/Observation identifiers are rejected.
- Session deletion preserves its Goal and removes every dependent row plus marker-owned artifacts; unsafe paths block metadata deletion.
- A database created by the existing migration upgrades forward without reset or data loss.
- Recent-window queries are ordered, bounded, session-scoped, and expose no EF entity.

## Out of scope

- Camera, HTTP/model, audio, UI, and background-service composition
- Evidence compaction policy and hosted prompts
- Crash/restart recovery beyond deterministic rehydration support
