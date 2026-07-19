# GK-016 acceptance, soak, and closeout evidence

**Evidence date:** 2026-07-18
**Branch:** `task/GK-016-acceptance-soak`
**Runtime:** .NET SDK 10.0.202 on Windows

This record excludes credentials, authorization metadata, image bodies, raw
provider responses, room imagery, audio, and transcripts.

## Offline quality gate

The repository quality gate passed from the GK-016 worktree:

```text
Python: 49 cases
.NET: 475 cases
  Domain: 131
  Application: 112
  Integration: 232
Release build: 0 warnings, 0 errors
dotnet format --verify-no-changes: passed
NuGet restore and vulnerability audit: passed
```

The three fake-driven acceptance tests cover:

- Goal and Deviation Profile setup
- immutable Session Setup confirmation
- camera preflight
- monitored session and Recovery Check-in
- text and configured voice Recovery presentation paths
- Fulfilled and Ended Early terminal routes
- optional Session Review and history
- confirmed session deletion without Goal deletion
- confirmed Goal deletion
- camera cleanup on every exercised terminal route

Hosted and Disabled composition tests prove that the running host selects the
OpenAI Perception, Reasoning, Recovery conversation, and voice adapters only in
Hosted mode. Disabled mode retains technical-failure fallbacks and does not
require a credential.

## Accelerated soak and resource bounds

Deterministic accelerated tests recorded these bounds:

- 250 captures retained exactly one in-flight request and one newest pending
  frame; 248 superseded snapshots never reached Perception.
- The run persisted exactly 250 snapshot rows and 1,250 modeled snapshot bytes.
- A delayed result became stale, the newest eligible result was observed, and
  pending Perception work returned to zero.
- The camera was released exactly once.
- A real SQLite run persisted 250 snapshots and observations while Reasoning
  reads remained capped at `ReasoningLimits.RecentObservations`.
- 50 repeated worker start/cancel cycles ended with no active registry entry;
  worker construction, cancellation, and asynchronous scope disposal counts
  were all exactly 50.
- All 50 worker completion tasks reached a terminal state. After a forced full
  collection, retained managed-memory growth remained below the 8 MiB test
  bound and process-thread growth remained below the eight-thread test bound.
  The resource-metric collection runs without test parallelism so unrelated
  test workers cannot mask a leak.

Existing bounded-context and leak suites additionally verify:

- Reasoning request size and recent-observation limits
- no raw images in Reasoning or Recovery requests
- raw Recovery audio disposal
- secret, image, audio, base64, and raw-provider-response canary redaction
- early invalid-configuration failure

## Real browser and offline camera evidence

Microsoft Edge was driven through the real Blazor interactive-server UI against
an isolated `%LocalAppData%\GoalKeeper\gk016-browser-current` data root.

The browser completed Goal creation, the missing-profile prerequisite,
Deviation Profile creation, Session Setup confirmation, and navigation to
camera preflight. This run exposed and fixed:

- an unhandled Blazor circuit failure when Session Setup was opened before a
  Deviation Profile existed; the route now renders an actionable profile link
- malformed `1:g min` duration text on Ready and Preflight pages

With providers Disabled, one explicitly initiated camera capture completed
locally. The unconfigured Perception boundary produced the expected technical
preflight failure, the UI stated that no behavioral judgment was recorded, and
the microphone remained off.

The real Edge journey subsequently exercised the complete interactive path:
Goal creation, profile prerequisites, immutable setup, camera confirmation,
live monitoring, Recovery, natural and confirmed terminal commands, optional
review save, optional review skip, Goal history, confirmed Focus Session
deletion, and confirmed Goal deletion. The distinct terminal results were:

- natural timer fulfillment at exactly three minutes
- confirmed End Early at 14 seconds
- confirmed Complete Goal at 14 seconds

Every terminal page reported monitoring closed. The post-session review saved
once and rendered from history, the skip button returned directly to Goals,
session deletion removed its history entry while retaining the Goal, and Goal
deletion removed the remaining Goal and its owned data.

## Consented Hosted smoke

Status: **pass**.

The operator explicitly consented to the running-host camera, Perception,
Reasoning, microphone, transcription, Recovery conversation, and speech checks.
The OpenAI credential was supplied through .NET Secret Manager; only its
presence and configuration key name were checked. Its value was never read or
printed.

The .NET host ran in Hosted mode at loopback against the isolated
`%LocalAppData%\GoalKeeper\gk016-browser-current` data root. Microsoft Edge
drove the real interactive-server UI.

The first Hosted run established the non-intervention path:

- camera preflight captured a usable frame with exactly one visible person
- OpenAI `gpt-5.6-luna`, hosted `perception-v1`, Observation schema v1, and
  image detail `low` produced six accepted observations
- OpenAI `gpt-5.6-sol`, hosted `reasoning-v1`, schema v1 produced five accepted
  `ContinueObserving` decisions
- the one-minute session fulfilled normally with no Recovery turn

The second Hosted run used a three-minute contract whose listed deviation was
sustained visible phone use. It established the complete intervention and voice
path:

- preflight passed again before monitoring began
- 31 snapshots produced 26 observations, one superseded snapshot, and four
  typed agent errors
- nine accepted `gpt-5.6-sol` Reasoning evaluations included one
  `BeginRecoveryCheckIn` after three consecutive observations across
  approximately 20 seconds supported persistent phone use
- the admitted Intervention paused the focus timer and exposed voice only in
  Recovery
- selecting Respond by voice activated the microphone for one bounded capture;
  the configured `gpt-4o-transcribe` input, `gpt-5.6-terra`
  `recovery-conversation-v1` schema v1 decision, and `tts-1`/`coral` output
  path returned a structured recommit
- the Recovery decision completed in 1.068 seconds with request ID
  `req_007bfd0e00824de2997fecc1ca6124d2`; the Intervention persisted as
  resolved and exactly one Recovery turn persisted
- the UI entered the Recovery Window with `MIC off`, returned to Focusing, and
  fulfilled at exactly three minutes of authoritative focus time

The four Perception errors were never evidence. They caused one explicit
`MonitoringUnavailable` transition, followed by `monitoring.restored` and
normal completion. This exercised the fail-safe technical-failure route during
a real provider session rather than silently converting provider failure into a
behavioral judgment.

After completion, the terminal page reported monitoring closed. The camera and
microphone were released, no session worker remained registered, and the host
was subsequently stopped. The privacy-safe database inspection read only
states, counts, decisions, versions, latencies, and request IDs; it did not
read or reproduce the persisted transcript or observation descriptions.

The earlier image-only OpenAI evidence and provider-quality limitations remain
recorded in [GK-002 live smoke evidence](GK-002-live-smoke-evidence.md).
