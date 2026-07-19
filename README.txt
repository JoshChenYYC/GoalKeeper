Planning note: the current agreed domain language is in CONTEXT.md and the
detailed pre-implementation behavior plan is in docs/application-logic.md.
Those documents supersede examples in this original vision when they differ.

## Automated quality gate

The default gate is deterministic and does not open a camera or microphone,
read a provider credential, or call a hosted model. It requires Python 3.11 and
the .NET SDK selected by `global.json` (currently 10.0.202).

From a fresh checkout, create an isolated Python test environment and run the
same gate used by CI:

```powershell
py -3.11 -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r requirements-test.txt
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\quality-gate.ps1 -Python .\.venv\Scripts\python.exe
```

The script runs `python -m pytest -q`, NuGet restore and audit, .NET format
verification, a warnings-as-errors Release build, and the complete
`GoalKeeper.sln` test suite. Results are written below `TestResults/`, and the
gate also enforces the retained baseline of at least 49 Python and 21 .NET test
cases.

Tests that need real hardware or a hosted provider must use the `hardware` or
`provider` pytest marker. Those markers are excluded from the default gate.
They are intentionally opt-in and require their own environment, credentials,
and runtime dependencies.

## .NET local prototype

The complete local prototype runs as a .NET 10 interactive-server Blazor app.
It supports Goal and Deviation Profile setup, immutable Session Contracts,
camera preflight, periodic snapshot monitoring, hosted Perception and Reasoning,
bounded Recovery Check-ins, session completion, optional review, history, and
confirmed local deletion.

The safe default is `Disabled` provider mode. It starts without a credential,
never calls a hosted model, and turns missing provider behavior into explicit
technical/indeterminate results. To use the selected hosted OpenAI stack, save
the credential with .NET Secret Manager; do not add it to a tracked file:

```powershell
dotnet user-secrets set "GoalKeeper:Providers:OpenAI:ApiKey" "YOUR_KEY" `
  --project .\src\GoalKeeper.Web
$env:GoalKeeper__Providers__Mode = "Hosted"
dotnet run --project .\src\GoalKeeper.Web
```

Open `http://127.0.0.1:5072`. Create a Focus profile before preparing the first
session. Camera access begins only when Capture is selected. In Hosted mode,
the microphone is available only during a Recovery Check-in and begins only
when Respond by voice is selected. Raw voice audio is discarded after the
bounded turn.

Application data defaults to `%LocalAppData%\GoalKeeper`; override it with
`GoalKeeper__DataRoot`. The SQLite database and owned session artifacts remain
local. Recovery transcripts are stored in that local database; raw voice audio
is discarded after transcription. Selected snapshots and bounded Recovery
audio/transcript content are sent to the configured provider in Hosted mode. See
`docs/provider-configuration.md`, `docs/operational-configuration.md`, and the
records under `docs/validation/` for the exact models and evidence.

Use the in-app confirmed deletion controls for individual sessions and Goals.
For a complete local reset, stop GoalKeeper and remove its configured data-root
directory. Never delete a broader `%LocalAppData%` directory.

Known prototype limitations:

* Native camera and microphone adapters currently target Windows.
* The app is single-user, loopback-only, and supports one active session.
* Snapshot/model quality varies with framing, lighting, occlusion, and provider
  behavior; technical failures never count as Deviation evidence.
* Crash/restart recovery, automatic retention, cross-session personalization,
  remote hosting, and production deployment are out of scope.
* Hosted provider abuse-monitoring retention may last up to 30 days.

The Python implementation remains reference-only and is not the application
entry point.

## Current recording prototype

The recording prototype is not part of the automated quality gate. To run it
explicitly, install the interactive runtime dependencies in a separate
environment and set an OpenAI API key:

```powershell
python -m pip install -r requirements.txt
$env:OPENAI_API_KEY="your-api-key"
```

Hosted provider, model, privacy, and configuration decisions are recorded in
`docs/adr/0002-openai-provider-and-model-stack.md` and
`docs/provider-configuration.md`. Selected room images and bounded Recovery
audio are processed by the OpenAI API. API data is not used for training unless
the organization opts in, but standard provider abuse-monitoring retention may
last up to 30 days. A session must not make hosted requests without explicit
consent from every person who may be captured.

Start a monitored capture session:

```powershell
python capture.py
```

Before monitoring begins, an OpenCV camera-preview window opens. Press Space
to capture a setup frame or Esc to cancel. The Perception Agent accepts the
setup only when image quality is adequate and exactly one person is visible;
the user must then confirm the view in the terminal. Canceling preflight does
not create a session folder.

After confirmation, each session is stored under
`captures/session-YYYYMMDD-HHMMSS/`:

* `preflight.jpg` and `preflight.json` contain the confirmed setup check.
* `observations.jsonl` contains neutral structured observations for reasoning.
* `capture_events.jsonl` records whether frames were observed, superseded, or
  failed during API processing.
* Timestamped JPEGs retain every monitoring snapshot locally.

Capture continues on its fixed cadence while one background API request runs.
If inference falls behind, only the newest unprocessed frame remains pending.
Stop with Ctrl+C; the camera is released before the active and newest pending
requests finish.

To test one existing JPEG against the Perception Agent without opening the
camera or running interactive preflight:

```powershell
python capture.py --image path\to\snapshot.jpg
```

## Session controller foundation

The controller layer is available as a programmatic integration point for the
future Goal/contract setup interface and Reasoning Agent:

* `domain.py` contains immutable Goal, contract, session, snapshot, observation,
  and reasoning-boundary types.
* `storage.py` provides the thread-safe SQLite authoritative store.
* `controller.py` owns Focus Session transitions, the active-focus timer,
  Scheduled Breaks, observation eligibility, and proposal validation.
* `ports.py` defines provider-neutral Camera, preview, and Perception boundaries;
  `camera_adapter.py` and `perception.py` contain the OpenCV and OpenAI details.
* Confirmed session and Goal deletion remove related SQLite records and only
  filesystem directories carrying the matching GoalKeeper ownership marker.

The current `python capture.py` command intentionally remains a recording-only
prototype until the Goal and Session Contract setup interface is implemented.
That future entry point creates and confirms a contract, then calls
`run_capture_session(..., controller=controller, contract=contract)`. When a
Reasoning Agent is supplied to the controller, perception and reasoning run as
one serialized cycle behind the same newest-pending-frame buffer.

SQLite retains authoritative Goals, immutable contract snapshots, Focus Session
state, snapshots, observations, and state-transition events. Session JPEG and
JSONL files remain inspectable artifacts. Observations captured during Scheduled
Breaks and technical failures are persisted for audit but are never included in
Reasoning Agent evidence.

I think we've refined the idea quite a bit. If I had to describe the project now, this is how I'd summarize it:

---

# AI Accountability Agent

## Vision

Build an AI accountability partner that helps users stay committed to a self-defined goal by reasoning about their behavior over time.

The project is **not** about recognizing what someone is doing.

The project is about deciding **whether their current behavior is still consistent with the goal they set.**

---

# User Flow

### 1. User starts a session

They tell the agent something like:

* "I'm studying LeetCode for an hour."
* "I'm writing my paper."
* "I'm doing homework."
* "I'm reading."

The user can also specify things like:

* session duration
* optional break rules
* allowed exceptions (optional)

---

### 2. Camera observes the room

A webcam is placed somewhere in the room.

Every **10 seconds**, one snapshot is taken.

No video processing.

No continuous streaming.

No OS integration.

No keyboard tracking.

No screen recording.

Just periodic room snapshots.

This keeps the system simple and lets you focus on the AI.

---

### 3. Perception Agent

Each image is converted into a compact structured observation.

For example:

```
User:
- sitting at desk

Objects:
- phone visible
- laptop open
- notebook present

Activity:
- looking at phone

Confidence:
- 0.86

Possible distractions:
- phone usage
```

The perception agent's job ends here.

It does **not** decide whether intervention is needed.

---

### 4. Reasoning Agent (the heart of the project)

This is where almost all of the intelligence lives.

The reasoning agent maintains the session state.

It remembers things like:

* how long the session has been running
* previous observations
* recent distractions
* reminder history
* whether the user recovered after reminders
* overall trend

It asks:

> "Given everything I've seen so far, should I intervene?"

NOT

> "What is happening in this image?"

This distinction is the core of the project.

---

### 5. Decision Making

The agent shouldn't interrupt immediately.

Example:

```
10:00
Focused.

↓

10:10
Phone appears.

↓

10:20
Still on phone.

↓

10:30
Still on phone.

↓

Decision:
Send reminder.
```

Another example:

```
User leaves room.

↓

Returns after 30 seconds.

↓

Decision:
Do nothing.
```

The assistant should recognize that not every distraction deserves intervention.

---

### 6. Coaching

If intervention is needed, generate a natural reminder.

Examples:

> "Looks like you've been distracted for a few minutes. Ready to get back to your goal?"

or

> "You've been away from your desk for a while. Let's finish one more problem."

Not robotic.

Not overly frequent.

Supportive.

---

### 7. User Interaction

The user can communicate with the assistant.

For example:

* "Taking a five minute break."
* "Resume."
* "Pause monitoring."

This helps reduce false positives.

---

# What Makes This Interesting

The webcam is **not** the interesting part.

Image classification is already a solved problem.

The interesting part is the reasoning.

The project demonstrates an AI agent that can:

* accumulate evidence over time
* distinguish between brief and persistent distractions
* decide when *not* to intervene
* adapt its behavior throughout the session
* act like an accountability partner instead of a detector

---

# Scope

Keep the initial version focused.

Support just a few goal types well (e.g., studying, reading, focused computer work).

Avoid trying to detect whether someone is specifically reading *Chapter 4* or solving a particular LeetCode problem. The agent only needs to determine whether the user's **observable behavior remains consistent with the goal**, not whether they are making perfect progress.

---

## One guiding principle

Every feature should answer this question:

> **"Does this make the agent better at deciding *when* to intervene?"**

If the answer is no, it's probably unnecessary for the hackathon.

I think this is a much stronger framing than where you started. Originally it felt like "an AI that looks at screenshots." Now it feels like **an AI agent that reasons about human behavior over time using visual evidence**, which is a much more compelling story for judges.
