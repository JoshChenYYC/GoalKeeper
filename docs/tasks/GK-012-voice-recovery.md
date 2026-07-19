# GK-012 — Natural voice Recovery Check-in

**Status:** In progress
**Owner:** Codex `/root`
**Branch:** `task/GK-012-voice-recovery`
**Depends on:** GK-002, GK-010, GK-011
**Suggested branch:** `task/GK-012-voice-recovery`

## Outcome

A Recovery Check-in activates audio only when needed, explains the Intervention with uncertainty, accepts natural speech, and returns one bounded proposal to the controller while always releasing the microphone and discarding raw audio.

## Owned surface

- Microphone, speech-input, and speech-output ports under `GoalKeeper.Application`
- Voice Recovery and audio/provider adapters under `GoalKeeper.Infrastructure`
- Feature-local service-registration extension and adapter tests

Consume the GK-010 Recovery contract. Do not edit `Program.cs`, mutate Focus Session state in an adapter, or add always-on voice control.

## Work

- Play an audible cue before microphone activation and activate only in Recovery Check-in.
- Build the opening explanation from Deviation, approximate duration, evidence summary, conflict rationale, and uncertainty.
- Provide only the GK-010 bounded context; never room snapshots.
- Map speech to the bounded GK-010 proposals and validate them before returning.
- Mirror the configured coaching limit, default three exchanges, to avoid unnecessary provider turns; the persisted-turn validator in GK-010 remains authoritative.
- Keep the timer paused until GK-011 resolves the proposal.
- Persist transcript and structured outcome through controller contracts; discard raw audio after processing.
- Retain the scripted/text adapter for tests and development fallback.

## Acceptance criteria

- Deterministic tests cover every proposal, unclear response, coaching cap, silence, cancellation, provider failure, and timeout.
- Microphone release occurs exactly once on every normal, exceptional, cancellation, and timeout path.
- Raw audio is absent from persistence and logs; transcript retention is explicit.
- Out-of-schema output is rejected without state mutation.
- Registration is isolated for GK-016 composition and does not alter shared host files.

## Out of scope

- Continuous listening or wake words
- Raw-image access by Recovery
- Cross-session coaching memory
