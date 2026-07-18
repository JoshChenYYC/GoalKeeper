# GK-002 live smoke evidence

**Evidence date:** 2026-07-18  
**Branch:** `task/GK-002-provider-decisions`  
**Provider:** OpenAI API  
**Perception model:** `gpt-5.6-luna`  
**Prompt:** retained Python `PERCEPTION_PROMPT`  
**Schema:** retained Python strict `room_observation` schema  
**Image detail:** `low`

This record intentionally excludes credentials, authorization metadata, image
bodies, base64 content, raw provider responses, and room imagery.

## Consent and prerequisites

- Explicit consent to activate the webcam and send a captured still image to
  OpenAI: **pending**.
- Explicit consent to send the selected image-only test input to OpenAI:
  **pending**.
- `OPENAI_API_KEY` available to the process: **no** (presence-only check; no
  value was read or printed).
- Python 3.11 available: **yes**, version 3.11.0.
- The standard OpenAI endpoint may retain customer content in abuse-monitoring
  logs for up to 30 days. The consenting operator must acknowledge this before
  either live request.

## Offline validation

The deterministic request-shape test passed before any provider call:

```text
py -3.11 -m pytest -q tests/test_capture.py::CaptureTests::test_observe_snapshot_sends_image_and_structured_schema
1 passed
```

The repository quality gate also passed on 2026-07-18:

```text
Python: 49 cases passed
.NET: 320 cases passed
Release build: 0 warnings, 0 errors
dotnet format --verify-no-changes: passed
NuGet restore and vulnerability audit: passed
```

This proves that the retained adapter constructs an image-input request with
the strict schema. It does not prove that the selected hosted model accepts the
request; that evidence remains the image-only live smoke below.

## Webcam preflight

**Result:** Not run — explicit camera/provider consent and a process-scoped API
credential have not been supplied.

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

**Result:** Not run — explicit provider consent, a non-sensitive JPEG selected
by the consenting operator, and a process-scoped API credential have not been
supplied.

Planned command:

```powershell
python capture.py --image <consented-jpeg> --model gpt-5.6-luna --detail low
```

Pass requires one successful Responses API request, nonempty JSON output that
conforms to the strict `room_observation` schema, and no secret or image body in
the retained evidence. Record only pass/fail, UTC execution time, provider
request ID when safely available, returned model ID, schema acceptance, latency,
and limitations.

## Known limitations

- This is a connectivity and schema smoke, not a provider quality comparison.
- One room view cannot establish robustness across lighting, occlusion, camera
  placement, skin tone, mobility aids, clothing, or different environments.
- `detail: low` trades fine visual detail for latency and cost. GK-016 owns the
  broader consented quality and soak evaluation.
- Provider aliases can change behavior. Persist the provider-returned model and
  rerun evidence after a model or prompt change.
