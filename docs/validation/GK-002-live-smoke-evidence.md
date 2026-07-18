# GK-002 live smoke evidence

**Evidence date:** 2026-07-18
**Branch:** `task/GK-006-hosted-perception`
**Provider:** OpenAI API
**Perception model:** `gpt-5.6-luna`
**Prompt:** hosted `perception-v1` asset
**Schema:** hosted strict Observation v1 schema
**Image detail:** `low`

This record intentionally excludes credentials, authorization metadata, image
bodies, base64 content, raw provider responses, and room imagery.

## Consent and prerequisites

- Explicit consent to activate the webcam and send a captured still image to
  OpenAI: **pending**.
- Explicit consent to send three user-selected image-only test inputs to
  OpenAI: **yes**, recorded on 2026-07-18.
- `OPENAI_API_KEY` available to the process: **yes** (presence-only check; no
  value was read or printed).
- Python 3.11 available: **yes**, version 3.11.0.
- The standard OpenAI endpoint may retain customer content in abuse-monitoring
  logs for up to 30 days. A separate webcam consent remains required before
  activating the camera.

## Offline validation

The deterministic request-shape test passed before any provider call:

```text
py -3.11 -m pytest -q tests/test_capture.py::CaptureTests::test_observe_snapshot_sends_image_and_structured_schema
1 passed
```

The repository quality gate also passed on 2026-07-18:

```text
Python: 49 cases passed
.NET: 339 cases passed
Release build: 0 warnings, 0 errors
dotnet format --verify-no-changes: passed
NuGet restore and vulnerability audit: passed
```

This proves that the hosted adapter constructs an image-input request with the
strict schema. The live evidence below additionally proves that the selected
hosted model accepts the request.

## Webcam preflight

**Result:** Not run — explicit camera consent has not been supplied. The
process-scoped API credential is now available.

Planned command:

```powershell
python capture.py --model gpt-5.6-luna --detail low
```

Pass requires the camera to open, a usable frame with exactly one visible person
to satisfy the strict schema and preflight validation, explicit confirmation of
the view, and clean camera release. The resulting local session directory must
be deleted by the consenting operator or retained only under the documented
local data policy; it must never be committed.

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

## Known limitations

- This is a connectivity and schema smoke, not a provider quality comparison.
- One room view cannot establish robustness across lighting, occlusion, camera
  placement, skin tone, mobility aids, clothing, or different environments.
- `detail: low` trades fine visual detail for latency and cost. GK-016 owns the
  broader consented quality and soak evaluation.
- Provider aliases can change behavior. Persist the provider-returned model and
  rerun evidence after a model or prompt change.
