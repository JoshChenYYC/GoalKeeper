# GK-016 — Acceptance, soak, tuning, and closeout

**Status:** Blocked
**Depends on:** GK-001, GK-002, GK-006, GK-007, GK-009, GK-011–GK-015
**Suggested branch:** `task/GK-016-acceptance-soak`

## Outcome

The complete local .NET prototype has recorded offline acceptance, long-session soak, privacy/resource evidence, and explicitly consented camera/provider/voice smoke results, with final setup and limitations documented.

## Owned surface

- Cross-feature acceptance, browser, soak, and resource-leak tests
- Manual consented-smoke checklist and evidence
- Late-adapter registration and final shared-host integration
- Final README/roadmap status and known-limitations documentation
- Small integration fixes coordinated with the task that owns the affected feature

Do not add deferred product features while closing the prototype.

## Work

- Run the complete fake-driven setup, preflight, live, Recovery, completion, review, history, and deletion flow.
- Exercise camera, Perception, Reasoning, and Recovery failures plus stale results and ending from every nonterminal state.
- Run an accelerated long Focus Session and bound context, pending frames, database growth, tasks/threads, and memory/resource use.
- Verify no background worker, camera, or microphone survives terminal exit.
- Compose any provider or voice registration extension that merged after GK-011, without moving feature logic into `Program.cs`.
- With explicit consent and configured secrets, run the full .NET camera, hosted Perception, hosted Reasoning, and voice path; record exact provider/model versions without retaining sensitive payloads.
- Tune documented settings from evidence and rerun offline acceptance.
- Mark task statuses accurately, retain Python as reference-only, and update the core definition of done.

## Acceptance criteria

- All default Python and .NET quality gates pass offline.
- Browser acceptance covers the complete user journey and every terminal route.
- The soak stays within documented request/context and resource bounds with no surviving worker.
- Every simulated failure remains technical/indeterminate and never becomes Deviation evidence by itself.
- Secret/image/audio leak tests pass and invalid configuration fails early.
- Consented live evidence covers camera, Perception, Reasoning, and Recovery, or names the specific human/environment gate still open.
- README documents final setup, local data paths, secrets, tests, known limitations, and safe cleanup.

## Out of scope

- Crash/restart recovery
- Automatic retention or cross-session personalization
- Multi-user, remote, or production deployment
- Formal Intervention-quality research
