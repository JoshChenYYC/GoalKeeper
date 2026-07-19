# GK-006 — Hosted Perception adapter

**Status:** Done
**Owner:** Codex `/root`
**Branch:** `task/GK-006-hosted-perception`
**Depends on:** GK-002, GK-005
**Suggested branch:** `task/GK-006-hosted-perception`

## Outcome

The provider selected in GK-002 implements the GK-005 Perception port through a versioned neutral prompt and strict output schema, with bounded repair, cancellation, privacy, and safe diagnostics.

## Owned surface

- Hosted Perception adapter under `GoalKeeper.Infrastructure`
- Versioned Perception prompt/schema assets
- Feature-local service-registration extension
- Adapter contract tests with recorded responses

Do not edit `Program.cs`, schedule camera captures, create behavioral judgments, or mutate a Focus Session.

## Work

- Send only JPEG bytes and provider-safe request options to the selected image model.
- Request the GK-005 schema using a neutral, versioned prompt and strict structured output.
- Validate decoded output through the GK-005 validator; allow one application-level repair, then return a typed agent error.
- Implement cancellation, request timeout, rate-limit, network, and malformed-response handling.
- Return provider/model, prompt/schema versions, latency, request ID, and safe failure category.
- Redact credentials and exclude image bodies and raw provider responses from logs by default.

## Acceptance criteria

- Recorded valid, invalid, repaired, timeout, rate-limit, and network responses pass without network access.
- A second invalid response is a technical failure, not an Observation or Deviation.
- Requests satisfy the GK-005 leak-prevention tests and contain no Goal, Deviation, sensitivity, or history data.
- Logs and metadata contain no credentials, base64 image, or raw response body.
- The feature registers through an isolated extension; GK-011 may compose it if already merged, otherwise GK-016 owns the late composition change.

## Out of scope

- Provider selection and consent
- Camera cadence, freshness, and snapshot retention
- Evidence Episodes or Intervention proposals
