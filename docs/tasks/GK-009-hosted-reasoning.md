# GK-009 — Hosted Reasoning adapter

**Status:** Done
**Owner:** Codex `/root`
**Branch:** `task/GK-009-hosted-reasoning`
**Depends on:** GK-002, GK-008
**Suggested branch:** `task/GK-009-hosted-reasoning`

## Outcome

The selected hosted Reasoning model implements the GK-008 port through a versioned prompt and strict output schema while preserving validation, privacy, cancellation, and bounded retry rules.

## Owned surface

- Hosted Reasoning adapter under `GoalKeeper.Infrastructure`
- Versioned Reasoning prompt/schema assets
- Feature-local service-registration extension
- Adapter contract tests with recorded responses

Do not edit `Program.cs`, alter evidence rules, or mutate controller/domain state.

## Work

- Serialize the bounded GK-008 request without raw images or unbounded history.
- Request the exact proposal schema from the provider selected in GK-002.
- Capture provider/model, prompt/schema versions, latency, request ID, and safe failure category.
- Implement cancellation, timeout, rate-limit, network, and one schema-repair retry.
- Return proposals to GK-008 validation; never execute them inside the adapter.
- Redact credentials and avoid logging full sensitive prompts/responses by default.

## Acceptance criteria

- Recorded valid, invalid, repaired, timeout, rate-limit, and network responses pass offline tests.
- Invalid schema is retried at most once.
- Provider failures return typed technical outcomes, are auditable, and create no behavioral evidence.
- Metadata is complete and contains no secret, image body, or raw microphone data.
- The feature registers through an isolated extension; GK-011 may compose it if already merged, otherwise GK-016 owns the late composition change.

## Out of scope

- Evidence Episode policy
- Controller transitions
- Voice Recovery
