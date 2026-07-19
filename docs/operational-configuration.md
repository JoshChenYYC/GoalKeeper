# Operational configuration

GoalKeeper uses the standard ASP.NET Core configuration precedence:
`appsettings.json`, environment-specific JSON, Secret Manager in Development,
environment variables, and command-line arguments. Secrets never belong in a
JSON file or command transcript.

## Runtime and capture defaults

| Configuration key | Default | Validation |
|---|---:|---|
| `Urls` | `http://127.0.0.1:5072` | Local loopback binding |
| `GoalKeeper:DataRoot` | `%LocalAppData%\GoalKeeper` | Absolute path |
| `GoalKeeper:Runtime:TickInterval` | `00:00:00.250` | Positive |
| `GoalKeeper:Runtime:ReasoningFreshnessLimit` | `00:01:00` | Positive |
| `GoalKeeper:SessionUi:CameraDeviceIndex` | `0` | Non-negative |
| `GoalKeeper:SessionUi:CameraWarmupFrameCount` | `8` | Non-negative |
| `GoalKeeper:SessionUi:CameraJpegQuality` | `85` | 1–100 |
| `GoalKeeper:SessionUi:CaptureCadence` | `00:00:10` | Positive |
| `GoalKeeper:SessionUi:ObservationFreshnessLimit` | `00:00:30` | Positive |
| `GoalKeeper:SessionUi:TechnicalGracePeriod` | `00:00:30` | Non-negative |

Focus Session policy values are typed, persisted application settings rather
than host configuration. Fresh databases use a one-minute Recovery Window,
one-minute response timeout, 30-second technical-outage grace, three maximum
unsuccessful recoveries, and three maximum coaching turns. Domain construction
validates these values before a session starts.

Provider keys and exact accepted models are documented in
[provider-configuration.md](./provider-configuration.md). Disabled mode is the
offline-safe default and requires no credential. Hosted mode validates the
complete stack during host startup.

## Safe diagnostics

Operational warning events accept only a known boundary, a known technical
category, the local session identifier, and a provider request identifier with
the expected `req_` shape. Unknown or payload-shaped values are replaced with
`redacted`.

Credentials, authorization headers, full prompts, transcripts, provider
response bodies, JPEG/base64 data, and raw microphone or speech buffers are
never operational-log fields. Adapter results remain typed as technical,
indeterminate, or behavioral data; technical failures never create Deviation
evidence by themselves.

