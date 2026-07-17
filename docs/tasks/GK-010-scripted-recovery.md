# GK-010 — Scripted Recovery boundary

**Status:** Blocked
**Depends on:** GK-003
**Suggested branch:** `task/GK-010-scripted-recovery`

## Outcome

A provider-neutral Recovery port accepts only the bounded Recovery Check-in context and returns a validated proposal. A deterministic text/script implementation can exercise every Recovery outcome without microphone, provider, or network access.

## Owned surface

- Recovery request, proposal, turn, and metadata contracts under `GoalKeeper.Application`
- Proposal validator and deterministic text/script fake
- Recovery contract and sequence tests

Do not add audio/provider SDKs, edit `Program.cs`, or mutate a Focus Session from an adapter.

## Work

- Shape requests from the immutable contract, admitted Intervention, evidence summary, disputed interval, active overrides, allowed outcomes, and bounded current Check-in turns.
- Represent recommitment, Behavior Clarification, end early, explicit continuation, bounded coaching, unclear response, and no response.
- Authoritatively enforce the configurable coaching cap from persisted current-Check-in turns, alongside proposal enums, required fields, session/version identity, and turn order. Adapters may mirror the check but cannot override it.
- Define transcript, structured outcome, turn timing, provider/model, schema/prompt, latency, and request-ID metadata for GK-003 persistence.
- Provide deterministic scripts for each single-turn outcome, repeated coaching, clarification, silence, cancellation, stale result, and failure.
- Keep room snapshots, raw audio, unrelated Goal history, and domain mutation outside the boundary.

## Acceptance criteria

- Every allowed proposal and every invalid/stale proposal has deterministic tests.
- Coaching sequences stop at the configured cap even across retries or adapter calls because persisted turn count, not adapter memory, is authoritative.
- Requests contain only the documented Check-in context and no snapshot/image data.
- Invalid, cancelled, stale, or failed responses cannot mutate domain state.
- GK-011 can persist the full turn/outcome metadata without adding a Recovery-specific EF migration.

## Out of scope

- Hosted conversational model, speech-to-text, text-to-speech, and microphone access
- Controller state transitions and timer reconciliation
- General chat or cross-session coaching memory
