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

---

# Recording and Perception Prototype

The current implementation covers only the input boundary:

1. capture one webcam frame every 10 seconds
2. encode and save that frame as a JPEG
3. send the same JPEG to the OpenAI Responses API
4. append the structured observation returned by the API to
   `observations.jsonl`

It does not decide whether the user is on task and does not generate a
reminder. The reasoning agent can consume the JSONL stream independently.

## Setup

```powershell
python -m pip install -r requirements.txt
$env:OPENAI_API_KEY="your-api-key"
```

## Run a session

```powershell
python capture.py
```

Useful options:

```powershell
# Capture every 5 seconds for a 30-minute session.
python capture.py --interval 5 --duration 30

# Use a different camera or model.
python capture.py --camera 1 --model gpt-5.6-luna

# Verify the API path with an existing JPEG without opening the webcam.
python capture.py --image captures/example.jpg
```

Each session is written under `captures/session-YYYYMMDD-HHMMSS/`. Its
`observations.jsonl` contains one record per successful API response:

```json
{"captured_at":"2026-07-15T20:30:00.000-07:00","image":"20260715-203000-000000.jpg","observation":{"user_present":true,"user_state":"sitting at desk","objects":["laptop","notebook"],"activity":"typing on laptop","possible_distractions":[],"confidence":0.91}}
```
