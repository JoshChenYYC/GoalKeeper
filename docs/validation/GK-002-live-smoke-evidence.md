# GK-002 live smoke evidence

**Evidence date:** 2026-07-18
**Branches:** `task/GK-006-hosted-perception`,
`task/GK-016-acceptance-soak`
**Provider:** OpenAI API
**Perception model:** `gpt-5.6-luna`
**Prompt:** hosted `perception-v1` asset
**Schema:** hosted strict Observation v1 schema
**Image detail:** `low`

This record intentionally excludes credentials, authorization metadata, image
bodies, base64 content, raw provider responses, and room imagery.

## Consent and prerequisites

- Explicit consent to activate the webcam and send captured still images to
  OpenAI through the running .NET host: **yes**, recorded on 2026-07-18.
- Explicit consent to send three user-selected image-only test inputs to
  OpenAI: **yes**, recorded on 2026-07-18.
- `OPENAI_API_KEY` available to the process: **yes** (presence-only check; no
  value was read or printed).
- Python 3.11 available: **yes**, version 3.11.0.
- The standard OpenAI endpoint may retain customer content in abuse-monitoring
  logs for up to 30 days.

## Offline validation

The deterministic request-shape test passed before any provider call:

```text
py -3.11 -m pytest -q tests/test_capture.py::CaptureTests::test_observe_snapshot_sends_image_and_structured_schema
1 passed
```

The repository quality gate also passed on 2026-07-18:

```text
Python: 49 cases passed
.NET: 356 cases passed
Release build: 0 warnings, 0 errors
dotnet format --verify-no-changes: passed
NuGet restore and vulnerability audit: passed
```

This proves that the hosted adapter constructs an image-input request with the
strict schema. The live evidence below additionally proves that the selected
hosted model accepts the request.

## Webcam preflight

**Result:** Pass.

The consented camera check ran twice through the real .NET interactive-server
UI in Hosted mode. Each explicitly initiated preflight capture opened the
Windows camera, sent one selected still to `gpt-5.6-luna`, returned a
schema-valid usable observation with exactly one visible person, and enabled
session start. The camera continued at the configured snapshot cadence only
during each live session and released on terminal completion.

The full Hosted evidence, including Reasoning, voice Recovery, transient
technical failure, and resource cleanup, is recorded in
[GK-016 acceptance and soak evidence](GK-016-acceptance-soak-evidence.md).

## Image-only Perception smoke

**Result:** Pass.

At `2026-07-18T22:34:34Z`, a Git-ignored local harness invoked the exact hosted
adapter against a consented empty-room JPEG stored outside the repository. The
Responses API returned HTTP 200 with `status: completed` and one `output_text`.
The output passed the GK-005 validator on the first request:

```text
model: gpt-5.6-luna
prompt: perception-v1
schema: Observation v1
detail: low
latency: 4097 ms
request ID: req_d9819d49280447b58e3b9a70edc48c86
people count: not_visible, value 0, direct support
```

An initial exploratory request exposed that the schema allowed contradictory
people-count status/value combinations. The hosted adapter returned a typed
technical failure after the model repeated the invalid combination during
repair. The schema now encodes the counted, not-visible, and unknown branches
with `anyOf`, the neutral prompt states the same invariants, and repair receives
only bounded local issue paths/codes. The post-fix request above demonstrates
provider acceptance and local validation.

Two additional consented scenario images returned completed, schema-valid
observations during the exploratory run. They also exposed quality limitations:
a partially visible right-edge form was counted as a second person in one image,
and a phone was described only as a generic handheld device in another. No
credential, image body, base64 content, or raw provider response was retained.

At `2026-07-18T22:56:54Z`, all three consented inputs were rerun after response
lifecycle validation, metadata sanitization, output-token bounding, and
`IHttpClientFactory` lifetime hardening. Each completed on its first request
with HTTP 200, `status: completed`, exactly one `output_text`, and a locally
valid Observation:

```text
not_in_frame: 6180 ms, request ID req_eba3e8478be24614a3d1a40a2e622751
working:      4585 ms, request ID req_dbb3d3dbcc2a41ff9988a562ce10f5af
on_phone:     5485 ms, request ID req_56107d47bd5e4ccfaef4b97dc0220429
```

The input named `not_in_frame.jpg` contains a person partially visible at the
lower-left edge, so its valid result counted one person with partial support.
The filename should not be treated as ground-truth evidence that nobody is
visible.

## Known limitations

- This is a connectivity and schema smoke, not a provider quality comparison.
- One room view cannot establish robustness across lighting, occlusion, camera
  placement, skin tone, mobility aids, clothing, or different environments.
- `detail: low` trades fine visual detail for latency and cost. The GK-016 live
  exercise demonstrated the intended intervention journey but is not a formal
  quality evaluation.
- Provider aliases can change behavior. Persist the provider-returned model and
  rerun evidence after a model or prompt change.
