# External-boundary failure safety

The deterministic integration suite injects failures without contacting a live
provider.

| Boundary | Covered failures | Safety invariant |
|---|---|---|
| Camera | open, warmup, capture, JPEG encoding, release, cancellation | No synthetic Observation; device released |
| Perception | timeout, rate limit, authentication, network, malformed/oversized schema, stale result, cancellation | Technical or indeterminate result; never Deviation evidence |
| Reasoning | timeout, rate limit, authentication, network, malformed/oversized schema, stale result, cancellation | No Intervention mutation on failure |
| Recovery conversation | timeout, rate limit, authentication, network, refusal, invalid schema, stale result, cancellation | No Recovery turn or session mutation |
| Recovery audio | silence, capture failure, transcription failure, TTS failure, timeout, cancellation | Microphone released once; raw buffers zeroed |
| Persistence/filesystem | optimistic conflict, missing row, I/O failure, deletion | Atomic state and safe retry/failure |

Provider fixtures contain only synthetic canaries. Log-capture and metadata
tests assert that API keys, JPEG/base64 bodies, raw audio markers, transcripts,
and raw provider responses do not cross operational logging boundaries.

