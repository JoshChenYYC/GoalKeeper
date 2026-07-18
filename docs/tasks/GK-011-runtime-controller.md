# GK-011 — End-to-end session runtime controller

**Status:** Done
**Owner:** Codex `/root`
**Branch:** `task/GK-011-runtime-controller`
**Depends on:** GK-007, GK-008, GK-010
**Suggested branch:** `task/GK-011-runtime-controller`

## Outcome

One long-lived .NET runtime controller composes domain, persistence, monitoring, Reasoning, and scripted Recovery so the complete Focus Session lifecycle passes with deterministic fakes and no hardware, credentials, or network.

## Owned surface

- Session runtime controller, commands, queries, and serialization under `GoalKeeper.Application`
- Hosted scheduling service and runtime composition under `GoalKeeper.Web`
- `Program.cs` and runtime DI registration
- End-to-end fake-driven lifecycle tests

This task is the sole integration owner for `Program.cs`. Adapters contribute feature-local registration extensions and never take a direct dependency on the controller.

## Work

- Implement setup-to-preflight-to-start orchestration and enforce one live session and one active pipeline/newest pending item.
- Run scheduling in `BackgroundService`/`IHostedService` lifetime, respect cancellation, and create scopes or use `IDbContextFactory` instead of capturing scoped `DbContext` services.
- Serialize authoritative commands and check session identity/version/freshness after every asynchronous boundary.
- Admit validated Interventions and persist the evaluation/state/timer change atomically.
- Revalidate every Recovery proposal through GK-010 after the asynchronous boundary, then resolve it through domain commands and persist turns, overrides, and outcomes.
- Aggregate camera, Perception, and typed Reasoning failures as technical health; transition to Monitoring Unavailable after grace and restore/end deterministically without Deviation evidence.
- Release camera and background work on cancellation, navigation command, startup failure, and every terminal path.
- Expose application query DTOs for UI without EF entities or provider types.

## Acceptance criteria

The fake-camera/fake-agent/fake-clock suite covers:

1. Preflight retry/cancel/confirm and guarded start.
2. Uninterrupted target fulfillment and early Goal completion.
3. Exact Scheduled Break boundaries.
4. Listed and Exploratory proposal admission.
5. Behavior Clarification restoring disputed time.
6. Recommitment excluding time and explicitly shifting projected end.
7. Recovery Window suppression and repeated-Recovery escalation.
8. Awaiting Response return and no-response timeout.
9. Stale/invalid proposals retained as rejected evaluations but otherwise ignored.
10. Camera, Perception, and Reasoning outage recovery/timeout without behavioral evidence.
11. User ending from every nonterminal state.
12. Optimistic concurrency with no partial state change.
13. Resource release with no surviving session worker.

No scenario uses webcam, provider credentials, microphone, or network.

## Out of scope

- Hosted provider implementation and natural voice
- Blazor presentation
- Crash/restart recovery after process termination
