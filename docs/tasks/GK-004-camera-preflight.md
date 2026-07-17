# GK-004 — .NET camera acquisition and preflight input

**Status:** Done
**Owner:** Codex `/root/gk004`
**Branch:** `main` working tree
**Depends on:** None
**Suggested branch:** `task/GK-004-camera-preflight`

## Outcome

The Python camera behavior is ported behind a provider-neutral .NET camera port, including preflight frame acquisition and deterministic cleanup, without Perception, persistence, confirmation, or session-policy knowledge in the adapter.

## Owned surface

- Camera contracts under `GoalKeeper.Application`
- Camera/native-image adapters under `GoalKeeper.Infrastructure`
- Camera adapter and preflight unit/contract tests

Do not implement snapshot persistence, Perception calls, Reasoning, or the live Blazor session page.

## Work

- Define async open, warmup, frame capture, JPEG encoding, health, and release contracts.
- Port fixed camera lifecycle behavior from `camera_adapter.py` through a replaceable native wrapper.
- Implement preflight frame acquisition and retry/cancel inputs without embedding UI or acceptance policy in the adapter.
- Produce immutable JPEG frame results with monotonic and UTC capture metadata owned by the local process.
- Report camera/open/read/encode failures as technical events.
- Make cancellation and disposal idempotent.
- Keep package-specific types inside Infrastructure; document the selected .NET camera binding.

## Acceptance criteria

- Adapter contract tests use a fake native camera and require no webcam.
- Open failure, warmup failure, capture failure, cancellation, retry, normal exit, and exception paths release the camera exactly once.
- The preflight acquisition result contains enough immutable data for GK-007 to apply Perception validation and user confirmation.
- Camera contracts contain no Goal, Deviation, provider, or Reasoning data.
- A consented manual webcam check is documented separately under GK-002 or GK-016.

## Out of scope

- Person-count or image-quality judgment
- User-confirmation policy and the final mandatory-preflight decision
- Snapshot scheduling and retention
- Hosted provider calls
