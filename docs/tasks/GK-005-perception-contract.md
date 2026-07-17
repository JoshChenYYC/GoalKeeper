# GK-005 — Perception schema, validator, port, and fake

**Status:** Ready
**Depends on:** None
**Suggested branch:** `task/GK-005-perception-contract`

## Outcome

The application has a provider-neutral, versioned Perception boundary that converts image requests into locally validated neutral Observations and can be exercised deterministically without credentials, hardware, or network access.

## Owned surface

- Perception request/result contracts and port under `GoalKeeper.Application`
- Observation schema, enums, and local validators
- Deterministic Perception fake and validator/contract tests

Do not add HTTP/model SDK code, camera scheduling, Reasoning judgments, controller transitions, or UI.

## Work

- Define Observation schema v1 with image quality, people count, objects, and bounded neutral visible cues.
- Preserve `direct`, `partial`, `inferred`, and `unavailable`; distinguish unknown, not visible, and not occurring.
- Define a port that receives only JPEG bytes plus provider-safe request options and returns a proposal plus safe metadata.
- Reject unknown fields, invalid enums, missing required data, malformed payloads, identity claims, and behavioral judgments.
- Preserve the single-person/no-identity boundary.
- Provide deterministic valid, invalid, delayed, cancelled, and failed fake responses.

## Acceptance criteria

- Validator tests cover every enum and required-field boundary without network access.
- Invalid output is a typed technical result, never an Observation or Deviation.
- A leak-prevention test proves Goal title/description, Deviations, sensitivity, and history cannot enter a Perception request.
- The contract can represent provider/model, prompt/schema versions, latency, request ID, and safe failure category without credentials, image bodies, or raw response bodies.
- GK-007 and GK-008 can compile against this task using only the port and application DTOs.

## Out of scope

- Hosted Perception transport and prompt assets
- Evidence Episodes or Intervention proposals
- Frame freshness and newest-frame scheduling
