# Use OpenAI for hosted perception, reasoning, and voice recovery

**Status:** Accepted  
**Decision date:** 2026-07-18  
**Decision owner:** GK-002

## Context

GoalKeeper sends selected room snapshots and bounded Recovery Check-in audio to
hosted models. Perception and every controller-facing language-model response
must conform to a strict schema. The provider choice must also keep credentials
out of the browser and repository, make remote processing explicit to the user,
and preserve the controller's rule that raw audio is discarded after processing.

The decision was checked against the OpenAI API documentation on 2026-07-18:

- [model catalog](https://developers.openai.com/api/docs/models)
- [GPT-5.6 Luna](https://developers.openai.com/api/docs/models/gpt-5.6-luna)
- [GPT-5.6 Sol](https://developers.openai.com/api/docs/models/gpt-5.6-sol)
- [GPT-5.6 Terra](https://developers.openai.com/api/docs/models/gpt-5.6-terra)
- [GPT-4o Transcribe](https://developers.openai.com/api/docs/models/gpt-4o-transcribe)
- [TTS-1](https://developers.openai.com/api/docs/models/tts-1)
- [structured outputs](https://developers.openai.com/api/docs/guides/structured-outputs)
- [image detail levels](https://developers.openai.com/api/docs/guides/images-vision#choose-an-image-detail-level)
- [data controls](https://developers.openai.com/api/docs/guides/your-data)

## Decision

Use the OpenAI API as the sole hosted provider for the core prototype. Use
server-side API-key authentication and these exact model IDs and API shapes:

| Responsibility | Provider and exact model | API and options | Rationale |
|---|---|---|---|
| Perception | OpenAI `gpt-5.6-luna` | Responses API, image input, `detail: low`, strict JSON Schema, `store: false`, no tools | Room snapshots are frequent and need bounded neutral extraction rather than frontier reasoning. Luna supports image input and structured outputs at the lowest cost of the GPT-5.6 family. Low detail supplies a 512-by-512 representation, which is appropriate for coarse scene, person-count, object, and behavior cues; GK-016 must revisit it if quality evidence is weak. |
| Reasoning | OpenAI `gpt-5.6-sol` | Responses API, text input, strict JSON Schema, `reasoning.effort: medium`, `store: false`, no tools | Temporal evidence interpretation and deciding when not to intervene are the central quality risk. The current flagship model is worth the higher cost at the lower Reasoning cadence. |
| Recovery speech-to-text | OpenAI `gpt-4o-transcribe` | Audio Transcriptions API | The check-in needs accurate short-form transcripts. The non-streaming request is compatible with bounded microphone capture and deterministic disposal of raw audio. |
| Recovery conversation and outcome mapping | OpenAI `gpt-5.6-terra` | Responses API, strict JSON Schema, `reasoning.effort: low`, `store: false`, no tools | Recovery is bounded and latency-sensitive but still needs reliable natural-language interpretation. Terra balances quality, latency, and cost between Sol and Luna. |
| Recovery text-to-speech | OpenAI `tts-1`, voice `coral` | Audio Speech API, streamed playback | TTS-1 remains a supported lower-latency speech model and preserves the accepted bounded prototype stack. The current guide recommends the newer `gpt-4o-mini-tts`; changing models requires a superseding decision and new live evidence. The application must tell the user that the voice is AI-generated. |

The model IDs above are aliases because OpenAI currently publishes no dated
snapshots for the GPT-5.6 variants. Persist the provider-returned model ID with
each result and rerun contract and quality evidence before accepting a changed
alias behavior or switching to a later model.

Recovery deliberately uses one provider through three separate model calls.
The current `gpt-realtime-2.1` family supports natural audio interaction but not
strict structured outputs. A direct speech-to-speech session would therefore
weaken the bounded GK-010 proposal contract and make raw-audio disposal harder
to audit. The staged design also permits deterministic validation between the
transcript, structured proposal, and spoken response.

## Authentication and trust boundary

- Use one project-scoped OpenAI API key. The key is supplied only to the local
  server process through environment configuration or .NET Secret Manager.
- Never send the API key to a Blazor client, command line, URL, committed file,
  log field, exception message, telemetry event, or persisted evaluation.
- All provider calls use HTTPS. The default API base URL is
  `https://api.openai.com/v1`; an approved regional project may explicitly use
  its documented regional base URL.
- The adapters do not enable web search, MCP, code execution, conversation
  objects, background mode, provider files, or provider tracing.
- A missing or invalid hosted configuration is a startup error when hosted mode
  is enabled. There is no silent fallback to a different model or provider.

The exact configuration and fail-fast contract is documented in
[provider-configuration.md](../provider-configuration.md).

## Privacy and provider-side handling

The following statements reflect OpenAI's published data controls on the
decision date and must be rechecked before a production release:

- API inputs and outputs are not used to train OpenAI models unless the API
  organization explicitly opts in to data sharing.
- Under standard retention, abuse-monitoring logs may contain prompts,
  responses, and derived metadata and are retained for up to 30 days, with
  possible longer retention when legally required or needed to protect the
  service or third parties.
- The Responses API stores application state for at least 30 days when storage
  is enabled. GoalKeeper therefore sends `store: false`, does not use provider
  conversation state, and treats the local database as authoritative.
- OpenAI's endpoint table lists Audio Transcriptions with no default abuse-log
  or application-state retention, while Audio Speech has up to 30 days of
  abuse-monitoring retention and no application-state retention. This does not
  remove the requirement to disclose all remote audio processing.
- Image inputs are scanned for CSAM. A flagged image may be retained for manual
  review even when an account has Zero Data Retention or Modified Abuse
  Monitoring enabled.
- Zero Data Retention and Modified Abuse Monitoring require OpenAI approval and
  are not assumed for the prototype. If enabled later, eligibility and endpoint
  limitations must be verified rather than inferred from the account setting.
- The default global endpoint makes no region guarantee. Regional storage and
  regional processing are separate capabilities. For example, the current
  documentation lists Canada as regional storage without regional processing,
  while the United States and Europe list both for supported services. Regional
  projects and non-US controls may require approved data-retention controls.

Before the first hosted request of a session, the UI must disclose that selected
room images and Recovery audio/transcripts are sent to OpenAI, that standard
provider retention may apply, and that local deletion cannot delete provider
abuse-monitoring records. Consent is session-specific and must be obtained from
every person who may be captured. Do not start monitoring if consent is absent.

GoalKeeper retains snapshots, observations, reasoning records, transcripts, and
reviews locally until manual deletion as defined in `application-logic.md`. Raw
Recovery audio exists only in memory or a short-lived local buffer, is never
logged, and is disposed immediately after transcription succeeds or fails.

## Consequences and follow-up

- GK-006 implements only the Perception selection above.
- GK-009 implements only the Reasoning selection above.
- GK-012 implements the staged Recovery stack above and always releases the
  microphone and disposes raw audio.
- GK-015 enforces configuration validation and log redaction.
- GK-016 evaluates whether low-detail Luna observations are good enough and may
  propose a new ADR if quality, latency, cost, residency, or model lifecycle
  evidence justifies changing the decision.
- Live smoke evidence is recorded in
  [GK-002-live-smoke-evidence.md](../validation/GK-002-live-smoke-evidence.md).

## Alternatives considered

- **GPT-5.6 Sol for every stage:** highest expected quality, but unnecessary
  cost for frequent neutral image extraction and bounded Recovery mapping.
- **GPT-5.6 Luna for every stage:** lowest cost, but it spends too little of the
  prototype budget on the core temporal-reasoning risk.
- **GPT-Realtime-2.1 end to end:** attractive conversational latency, but it
  lacks strict structured outputs and blurs the transcript/disposal boundary.
- **Multiple hosted providers:** may improve one specialized stage, but creates
  more credential, retention, regional-processing, failure, and consent
  surfaces before provider diversity is a demonstrated requirement.

