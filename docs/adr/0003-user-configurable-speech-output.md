# Allow user-configurable speech output

**Status:** Accepted<br>
**Decision date:** 2026-07-19<br>
**Decision owner:** GoalKeeper application

## Context

ADR 0002 selected OpenAI `tts-1` with the `coral` voice as the exact Recovery
text-to-speech stack. That fixed selection prevented a user from choosing the
latency, quality, and voice characteristics they prefer for spoken Recovery
check-ins.

The OpenAI text-to-speech guide checked on 2026-07-19 documents three Audio
Speech models and model-dependent built-in voice sets. It also requires a clear
disclosure that generated speech is AI-generated rather than a human voice:

- [Text to speech](https://developers.openai.com/api/docs/guides/text-to-speech)
- [Create speech API](https://developers.openai.com/api/docs/api-reference/audio/createSpeech)

## Decision

Keep `tts-1` with `coral` as the behavior-preserving default, and expose a
local **Settings** page where the user can select:

| Speech model | Accepted built-in voices |
|---|---|
| `gpt-4o-mini-tts` | `alloy`, `ash`, `ballad`, `coral`, `echo`, `fable`, `nova`, `onyx`, `sage`, `shimmer`, `verse`, `marin`, `cedar` |
| `tts-1` | `alloy`, `ash`, `coral`, `echo`, `fable`, `nova`, `onyx`, `sage`, `shimmer` |
| `tts-1-hd` | `alloy`, `ash`, `coral`, `echo`, `fable`, `nova`, `onyx`, `sage`, `shimmer` |

Store the selected pair in the existing local `ApplicationSettings` table.
Validate the model and model/voice compatibility before saving and immediately
before each Audio Speech request. Read the saved pair for each request so a new
selection takes effect without restarting GoalKeeper.

The Settings page must disclose that GoalKeeper's spoken voice is AI-generated
and not a human voice. It must update the voice choices when the model changes
and keep the visible selection aligned with the value that will be saved.

Do not accept arbitrary model names, custom voice IDs, or incompatible pairs.
Do not move the API key or provider request into the browser. The server-side
trust boundary, PCM response format, bounded response handling, and local audio
playback from ADR 0002 remain unchanged.

## Consequences

- Users can trade latency and quality and choose a preferred built-in voice.
- Existing installations retain `tts-1` and `coral` until the user saves a new
  selection.
- A saved selection applies to the next generated speech request, including an
  already-running application.
- Adding another model or voice requires updating the centralized allow-list,
  compatibility tests, provider contract, and this decision trail.
- Custom voices remain out of scope because eligibility and voice-ID lifecycle
  require a separate product and consent decision.
