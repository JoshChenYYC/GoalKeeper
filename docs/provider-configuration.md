# Hosted provider configuration contract

This document defines the non-secret configuration names selected by GK-002.
It is an implementation contract for GK-006, GK-009, GK-012, and GK-015; it
does not add a production provider adapter.

## Selected values

| .NET configuration key | Environment variable | Default or required value | Secret |
|---|---|---|---|
| `GoalKeeper:Providers:Mode` | `GoalKeeper__Providers__Mode` | `Disabled` or `Hosted`; default `Disabled` | No |
| `GoalKeeper:Providers:OpenAI:ApiKey` | `GoalKeeper__Providers__OpenAI__ApiKey` | Required and nonblank in `Hosted` mode | Yes |
| `GoalKeeper:Providers:OpenAI:BaseUrl` | `GoalKeeper__Providers__OpenAI__BaseUrl` | `https://api.openai.com/v1` | No |
| `GoalKeeper:Providers:Perception:Model` | `GoalKeeper__Providers__Perception__Model` | `gpt-5.6-luna` | No |
| `GoalKeeper:Providers:Perception:ImageDetail` | `GoalKeeper__Providers__Perception__ImageDetail` | `low` | No |
| `GoalKeeper:Providers:Reasoning:Model` | `GoalKeeper__Providers__Reasoning__Model` | `gpt-5.6-luna` | No |
| `GoalKeeper:Providers:Reasoning:Effort` | `GoalKeeper__Providers__Reasoning__Effort` | `medium` | No |
| `GoalKeeper:Providers:Recovery:ConversationModel` | `GoalKeeper__Providers__Recovery__ConversationModel` | `gpt-5.6-terra` | No |
| `GoalKeeper:Providers:Recovery:ReasoningEffort` | `GoalKeeper__Providers__Recovery__ReasoningEffort` | `low` | No |
| `GoalKeeper:Providers:Recovery:TranscriptionModel` | `GoalKeeper__Providers__Recovery__TranscriptionModel` | `gpt-4o-transcribe` | No |
| `GoalKeeper:Providers:Recovery:SpeechModel` | `GoalKeeper__Providers__Recovery__SpeechModel` | `tts-1` | No |
| `GoalKeeper:Providers:Recovery:Voice` | `GoalKeeper__Providers__Recovery__Voice` | `coral` | No |
| `GoalKeeper:DataRoot` | `GoalKeeper__DataRoot` | `%LocalAppData%\GoalKeeper` | No |

`store: false` is a request invariant for every Responses API call. It is not a
configuration switch because the core version has no feature that requires
provider-side response storage.

The retained Python reference continues to use `OPENAI_API_KEY` and optionally
`OPENAI_MODEL`. Its selected default is `gpt-5.6-luna`; `OPENAI_MODEL` exists
only for an explicit smoke experiment and does not change the accepted .NET
selection.

## Safe examples

PowerShell environment setup, with the secret deliberately omitted:

```powershell
$env:GoalKeeper__Providers__Mode = "Hosted"
$env:GoalKeeper__Providers__OpenAI__BaseUrl = "https://api.openai.com/v1"
$env:GoalKeeper__Providers__Perception__Model = "gpt-5.6-luna"
$env:GoalKeeper__Providers__Perception__ImageDetail = "low"
$env:GoalKeeper__Providers__Reasoning__Model = "gpt-5.6-luna"
$env:GoalKeeper__Providers__Reasoning__Effort = "medium"
$env:GoalKeeper__Providers__Recovery__ConversationModel = "gpt-5.6-terra"
$env:GoalKeeper__Providers__Recovery__ReasoningEffort = "low"
$env:GoalKeeper__Providers__Recovery__TranscriptionModel = "gpt-4o-transcribe"
$env:GoalKeeper__Providers__Recovery__SpeechModel = "tts-1"
$env:GoalKeeper__Providers__Recovery__Voice = "coral"
```

For local .NET development, the Secret Manager name is
`GoalKeeper:Providers:OpenAI:ApiKey`. `GoalKeeper.Web` has a committed
`UserSecretsId`, so IDE secret-management commands store the value outside the
repository.
Set the value through an IDE secret-management UI or another input method that
does not expose it in shell history or a process list. Never paste a real key
into this document, `appsettings*.json`, `.env`, a test fixture, or a command
transcript.

The committed default is `GoalKeeper:Providers:Mode=Disabled`. It validates and
starts without reading provider credentials and resolves only deterministic
unavailable/fake boundaries. `Hosted` validates the complete exact model stack
and API key during host startup, but final live-adapter composition remains
owned by GK-016.

The default local binding is `http://127.0.0.1:5072`. Override `Urls` explicitly
only for a deliberate local environment; remote hosting is outside the
prototype scope. `GoalKeeper:DataRoot` defaults to
`%LocalAppData%\GoalKeeper` and must be an absolute path when overridden.

## Fail-fast validation

The host validates the complete provider options during startup:

1. `Mode` is exactly `Disabled` or `Hosted`.
2. `Hosted` requires a nonblank API key and an absolute HTTPS base URL.
3. Every model, voice, image-detail, and reasoning-effort value is nonblank and
   belongs to the accepted set in the table above. A deliberate model change
   first requires a superseding ADR and new contract evidence.
4. `Disabled` does not read or validate credentials and registers only
   deterministic unavailable/fake boundaries; it never makes a network call.
5. A 401/403 response is an authentication/configuration failure, never
   behavioral evidence. Do not retry it as a transient error.
6. The application never falls back to another model, global endpoint, or
   provider after validation or a runtime provider failure.

Startup error messages may name the missing configuration key, but must not
include credential values, authorization headers, base64 image data, raw audio,
full prompts, transcripts, or raw provider responses.

## Safe operational metadata

Persist and log only the bounded metadata already required by the application
contracts: provider name (`openai`), requested model, provider-returned model,
prompt version, schema version, latency, request ID, result category, and safe
token/usage counts. Request IDs are diagnostic metadata, not authentication.

Credentials, image URLs/data bodies, JPEG bytes, raw microphone buffers, full
provider payloads, and provider SDK exception bodies are excluded from logs.
Recovery transcripts are persisted locally according to product policy but are
not emitted to normal application logs.
