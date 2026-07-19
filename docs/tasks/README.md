# Assignable Work

This directory contains the remaining GoalKeeper roadmap as independently assignable tasks. Each task fixes its own outcome, boundaries, dependencies, and acceptance criteria so another person or subagent can begin without re-planning the product.

## Status vocabulary

- **Ready**: all prerequisites are present; implementation may start.
- **Human gate**: requires consent, credentials, hardware access, or a product/provider decision.
- **Blocked**: do not integrate until the listed task IDs are complete.
- **In progress**: claimed on a named branch or PR.
- **Done**: merged and all acceptance criteria verified.

## Task index

| ID | Task | Current status | Depends on | Primary output |
|---|---|---|---|---|
| [GK-001](./GK-001-quality-gate.md) | Reproducible quality gate and CI | Done | — | Offline-safe Python/.NET CI |
| [GK-002](./GK-002-provider-decisions.md) | Provider, model, privacy, and live-smoke decisions | In progress | — | Accepted ADRs and smoke evidence |
| [GK-003](./GK-003-runtime-persistence.md) | Runtime state and persistence foundation | Done | — | Recoverable atomic runtime contract |
| [GK-004](./GK-004-camera-preflight.md) | .NET camera acquisition and preflight input | Done | — | Provider-neutral camera adapter |
| [GK-005](./GK-005-perception-contract.md) | Perception schema, validator, port, and fake | Done | — | Provider-neutral Observation contract |
| [GK-006](./GK-006-hosted-perception.md) | Hosted Perception adapter | Done | GK-002, GK-005 | Versioned image-provider integration |
| [GK-007](./GK-007-monitoring-pipeline.md) | Capture/Perception pipeline and monitoring health | Done | GK-003, GK-004, GK-005 | Fresh persisted observation stream |
| [GK-008](./GK-008-durable-reasoning.md) | Durable Reasoning core and deterministic fake | Done | GK-003, GK-005 | Bounded evidence-linked decisions |
| [GK-009](./GK-009-hosted-reasoning.md) | Hosted Reasoning adapter | Done | GK-002, GK-008 | Versioned provider integration |
| [GK-010](./GK-010-scripted-recovery.md) | Scripted Recovery boundary | Done | GK-003 | Deterministic Recovery proposals |
| [GK-011](./GK-011-runtime-controller.md) | End-to-end session runtime controller | Done | GK-007, GK-008, GK-010 | Complete fake-driven lifecycle |
| [GK-012](./GK-012-voice-recovery.md) | Natural voice Recovery Check-in | Done | GK-002, GK-010, GK-011 | Bounded voice interaction |
| [GK-013](./GK-013-live-session-ui.md) | Live Focus Session UI | Done | GK-011 | Preflight and live controls |
| [GK-014](./GK-014-review-history-ui.md) | Review, history, storage, and deletion UI | Done | GK-003, GK-011 | Complete post-session workflow |
| [GK-015](./GK-015-configuration-log-safety.md) | Configuration, logging, and failure safety | In progress | GK-003, GK-006, GK-009, GK-011, GK-012 | Validated safe operations |
| [GK-016](./GK-016-acceptance-soak.md) | Acceptance, soak, tuning, and closeout | Blocked | GK-001, GK-002, GK-006, GK-007, GK-009, GK-011–GK-015 | Prototype-readiness evidence |

## Parallel work lanes

- **Foundation lane:** GK-001 and GK-003.
- **Hardware lane:** GK-004, followed by the camera side of GK-007.
- **Human decision lane:** GK-002.
- **Perception lane:** GK-005 is immediately available; GK-006 waits only for GK-002 and GK-005.
- **Reasoning lane:** GK-008 then GK-009.
- **Recovery lane:** GK-010 can begin after GK-003.
- **Integration lane:** GK-007 then GK-011.
- **Experience lane:** GK-012, GK-013, and GK-014 can run in parallel after GK-011.
- **Release lane:** GK-015 then GK-016.

## Claiming a task

1. Confirm every dependency is merged.
2. Change `Status` to `In progress` and add the branch/owner in the task document.
3. Use a branch such as `task/GK-007-monitoring-pipeline` and include the task ID in the PR title.
4. Stay inside the task's owned surface unless the PR explicitly identifies a coordinated cross-task change.
5. Report commands and results for every acceptance test.
6. Set the task to `Done` only after merge.

## Shared integration rules

- `GoalKeeper.Domain` owns deterministic rules, not orchestration or provider SDKs.
- `GoalKeeper.Application` owns commands, queries, ports, and orchestration.
- `GoalKeeper.Infrastructure` owns EF Core, filesystem, camera, HTTP/provider, and audio adapters.
- `GoalKeeper.Web` owns presentation only; components do not access EF entities or provider SDKs.
- `FocusSession`, runtime snapshots, `IGoalKeeperRepository`, `GoalKeeperDbContext`, and the next EF migration are owned by GK-003 until its contracts are merged.
- Initial runtime composition and `Program.cs` are owned by GK-011; adapters register through feature-local service extensions. An adapter already merged at that point may be composed by GK-011. Afterward, GK-015 may add only its configuration/logging hook, and GK-016 owns late-adapter composition and final integration fixes.
- The shared Blazor shell (`Routes`, navigation, and global styles) is owned by GK-013 until GK-016; GK-014 adds routable feature components and feature-local styles without editing that shell.
- EF migrations are serialized through GK-003 or a clearly declared follow-up task; parallel tasks must not create competing migrations.
- Hosted adapters return proposals. They never mutate a Focus Session directly.

## Coverage map

| Former phase | Assignable tasks |
|---|---|
| Baseline and architecture | GK-001, GK-002 |
| Capture and Perception | GK-003–GK-007 |
| Reasoning and durable memory | GK-003, GK-008, GK-009 |
| Scripted Recovery and controller | GK-010, GK-011 |
| Voice Recovery | GK-012 |
| Completion and UI | GK-013, GK-014 |
| Hardening and readiness | GK-015, GK-016 |
