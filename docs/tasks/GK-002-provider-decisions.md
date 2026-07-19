# GK-002 — Provider, model, privacy, and live-smoke decisions

**Status:** Done
**Depends on:** None
**Suggested branch:** `task/GK-002-provider-decisions`

## Outcome

The exact Perception, Reasoning, speech-to-text, text-to-speech, and Recovery conversation providers/models are selected and recorded, with consented smoke evidence and a safe configuration contract.

## Owned surface

- `docs/adr/`
- Provider/privacy sections of documentation
- Non-secret configuration examples

Do not commit credentials, provider responses containing room imagery, or adapter implementation.

## Work

- Select the exact model for image input plus strict structured output.
- Select the Reasoning model and the voice stack, including whether Recovery uses one or multiple providers.
- Record data-retention, training, regional-processing, and provider-side logging implications.
- Define environment-variable/Secret Manager names and fail-fast validation; keep `%LocalAppData%\GoalKeeper` as the application-data default.
- With explicit user consent, run the Python reference's webcam preflight and one image-only Perception smoke test.
- Record model IDs, date, schema behavior, result, and limitations without retaining secrets or unnecessary image bodies.

## Acceptance criteria

- Accepted ADRs name exact providers/models and explain the trade-offs.
- The selected image model demonstrably accepts the required input and structured schema.
- Voice and Reasoning choices identify authentication and privacy constraints.
- Secrets locations and configuration names are documented; no secret is committed or logged.
- Manual webcam/provider tests record consent and pass/fail evidence.

## Out of scope

- Production adapter code
- Automated live-provider tests
- Formal provider quality comparison

## Delivery record

- Provider/model decision: [ADR 0002](../adr/0002-openai-provider-and-model-stack.md)
- Safe configuration contract: [provider-configuration.md](../provider-configuration.md)
- Manual validation record: [GK-002-live-smoke-evidence.md](../validation/GK-002-live-smoke-evidence.md)
- The final consented running-host camera, Reasoning, and voice exercise is
  recorded in
  [GK-016 acceptance and soak evidence](../validation/GK-016-acceptance-soak-evidence.md).
