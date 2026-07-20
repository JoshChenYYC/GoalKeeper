# GoalKeeper

GoalKeeper is a local, single-user accountability application for monitored focus sessions. You define a Goal and the visible behaviors that may indicate a deviation, then GoalKeeper takes periodic webcam snapshots and uses hosted AI to decide whether a supportive check-in would be useful.

The primary application is a .NET 10 interactive-server Blazor web app. The Python files in the repository are retained reference prototypes; they are not the application entry point.

## What the application does

- Stores Goals, accountability rules, session contracts, session history, and reviews locally.
- Requires a camera preflight before every monitored session.
- Takes still images at a fixed cadence instead of recording video.
- Separates image perception from temporal reasoning about whether to intervene.
- Pauses active-focus time for scheduled breaks, recovery check-ins, and sustained monitoring outages.
- Supports typed recovery responses and, in Hosted mode, bounded voice responses.
- Never treats a camera, network, or provider failure as evidence of user behavior.

## Requirements

- Windows 10 or later. The native camera and microphone adapters currently target Windows.
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). The repository's `global.json` selects SDK `10.0.202` with latest-patch roll-forward.
- A supported browser such as Microsoft Edge, Chrome, or Firefox.
- A webcam for camera preflight and monitored sessions.
- An OpenAI API key for a complete monitored session. Goal setup and other local-only features can be explored without one.
- A microphone for Hosted Recovery Check-ins. After GoalKeeper asks its question aloud, it automatically opens one bounded response capture and then releases the device.

Check the installed SDK from the repository root:

```powershell
dotnet --version
```

## Run locally

### 1. Start in safe local mode

The committed configuration uses `Disabled` provider mode. This mode requires no API key and makes no hosted AI requests.

From the repository root:

```powershell
dotnet run --project .\src\GoalKeeper.Web --launch-profile http
```

The launch profile normally opens a browser at `http://localhost:5191`. If it does not, open that address yourself. Use the exact URL printed after `Now listening on` if the configured port is unavailable or has been overridden.

Stop the application with `Ctrl+C` in the terminal.

Disabled mode lets you create and edit local Goals and accountability rules, prepare a contract, and initiate a camera capture. It cannot pass preflight because preflight requires AI perception to confirm image quality and that exactly one person is visible. Configure Hosted mode to run a complete Focus Session.

### 2. Enable hosted AI

Hosted mode sends selected snapshots and bounded Recovery content to the configured OpenAI API. API use may incur charges. Obtain consent from every person who may be captured before starting a hosted session.

Store the API key outside tracked configuration with .NET Secret Manager:

```powershell
$apiKey = Read-Host "OpenAI API key"
dotnet user-secrets set "GoalKeeper:Providers:OpenAI:ApiKey" $apiKey --project .\src\GoalKeeper.Web
Remove-Variable apiKey
```

The variable-based command keeps the key itself out of PowerShell history. For stricter local process-list protection, use Visual Studio's **Manage User Secrets** command for `GoalKeeper.Web` and add the same configuration key there.

Enable Hosted mode for the current PowerShell window and start the app:

```powershell
$env:GoalKeeper__Providers__Mode = "Hosted"
dotnet run --project .\src\GoalKeeper.Web --launch-profile http
```

The `http` launch profile sets the environment to Development, which makes the saved user secret available to the app. Hosted configuration is validated at startup. A missing key, invalid endpoint, or unsupported model setting stops the app with a configuration error instead of silently falling back.

To return to safe local mode in the current terminal:

```powershell
Remove-Item Env:GoalKeeper__Providers__Mode -ErrorAction SilentlyContinue
```

## Use GoalKeeper

### 1. Create or select a Goal

Open **Home**, enter a required title and an optional description, and select **Create Goal**. Home guides a first session from Goal, to accountability rules, to session setup. Use **Edit** on a Goal card to update it or to perform a confirmed deletion.

A Goal with an active Focus Session is locked against editing and deletion. Marking a Goal complete is permanent in the current prototype and replaces its start action with **View history**.

### 2. Define accountability rules

Follow the guided Home action or open **Accountability rules** in the top navigation.

1. Give the rules a name.
2. Under **Call me out when…**, enter one visible behavior per line, such as `Sustained attention to a phone` or `Leaving the camera view`.
3. Choose how visible those behaviors are to the camera.
4. Select **Save accountability rules**.

At least one behavior is required. Changes to reusable rules affect future session contracts only; a confirmed contract retains its original rules snapshot.

The observability choices are:

- `Observable`: the behavior should be directly visible to the camera.
- `PartiallyObservable`: the camera may provide useful but incomplete evidence.
- `NotObservable`: the behavior is unlikely to be reliably judged from room images.

### 3. Prepare the Session Contract

Select **Start session** on an active Goal card, then configure:

- **Focus time:** active-focus minutes, from 1 to 1,440. Planned breaks and recovery time do not count toward this target.
- **Planned breaks:** optional. Write when the break should start, then how long it should last, separated by a colon. For example, `25:5` means “after 25 minutes of focus, take a five-minute break.” Add each additional break on a new line. Break start times must be positive, unique, and earlier than the focus target.
- **What may GoalKeeper call out?:** limit interventions to the accountability-rules snapshot or permit a clearly identified unlisted behavior when evidence suggests it conflicts with the Goal.
- **How quickly should GoalKeeper step in?:** choose sooner, balanced, or only with stronger evidence.

Select **Lock session plan and continue**. After confirmation, the contract cannot be changed. A future session for the same Goal is prefilled from its most recent contract and creates a new immutable snapshot when confirmed.

### 4. Complete camera preflight

1. Select **Begin camera preflight**.
2. Sit where you plan to focus with your face and workspace comfortably visible.
3. Select **Capture camera view**. The camera is activated only for the capture.
4. If GoalKeeper reports poor image quality or the wrong number of people, adjust the lighting or framing and capture again.
5. When the preview is accepted, verify that the view is correct and only you are visible.
6. Select **Confirm and start focus**.

Preflight requires exactly one visible person and an adequate image. The microphone is never used during preflight. **Cancel setup** releases the camera and returns Home without starting a Focus Session.

### 5. Work through a live session

The live page shows authoritative active-focus time, projected completion, camera status, microphone status, and the current session state.

- GoalKeeper normally captures one still image every 10 seconds. It does not capture video, the screen, keyboard input, or operating-system activity.
- Scheduled breaks begin and end automatically. The focus timer and behavioral evidence are paused during a break.
- If monitoring briefly fails, GoalKeeper retries and does not count the failure as behavioral evidence. After the configured grace period, the focus timer pauses until monitoring recovers.
- If accumulated evidence justifies an intervention, the focus timer pauses for a private **Reality check**. The tough accountability line is generated with the Reasoning decision, persisted locally, and shown identically in text and voice; evidence and uncertainty remain available underneath.
- In Hosted mode, GoalKeeper speaks the check-in, plays a listening cue, and automatically opens the microphone for one bounded response. Raw audio is discarded after processing. If automatic voice capture fails, use **Try voice again** or the secondary written response.
- Use **Complete Goal** to end the session and mark its Goal complete, or **End early** to close monitoring without completing the Goal. Both actions require confirmation.

The target is active-focus time, not wall-clock time, so breaks, accepted deviation intervals, and recovery interactions can move the projected end later.

### 6. Review and manage history

After a fulfilled or early-ended session, choose **Optional session review**. You can record:

- whether you made meaningful progress;
- whether interventions were helpful;
- an optional note of up to 2,000 characters; and
- whether the Goal should be marked complete.

A review can be submitted only once. You may also skip it. Use **View Goal history** from the review screen to inspect terminal session states, immutable contracts, review status, snapshot counts, and local snapshot storage.

History provides confirmed controls to delete one Focus Session or an entire Goal. Deleting a session removes its database records and owned snapshot artifacts. Deleting a Goal removes all of its sessions and owned data.

## Local data and privacy

By default, GoalKeeper stores data under:

```text
%LocalAppData%\GoalKeeper
├── goalkeeper.db
└── sessions\<session-id>\snapshots\*.jpg
```

The SQLite database is authoritative. It includes Goals, immutable contracts, session state, observations, reasoning records, Recovery transcripts, and optional reviews. Captured JPEG snapshots are retained in session-owned directories.

In Hosted mode:

- selected JPEG snapshots are sent to OpenAI for perception;
- bounded structured observations and session context are sent for reasoning;
- a typed Recovery response is sent for Recovery conversation processing;
- voice Recovery sends bounded audio for transcription and may use generated speech; and
- raw microphone audio is held transiently and discarded after processing.

Credentials, JPEG/base64 bodies, raw audio, prompts, transcripts, and provider response bodies are excluded from normal application logs. For the selected provider stack and retention considerations, see [Hosted provider configuration](docs/provider-configuration.md) and [the provider ADR](docs/adr/0002-openai-provider-and-model-stack.md).

### Use a separate data directory

`GoalKeeper:DataRoot` must be an absolute path. Set it before starting the app:

```powershell
$env:GoalKeeper__DataRoot = "C:\GoalKeeperData"
dotnet run --project .\src\GoalKeeper.Web --launch-profile http
```

This is useful for isolated demos or tests. Remove the environment override when finished:

```powershell
Remove-Item Env:GoalKeeper__DataRoot -ErrorAction SilentlyContinue
```

### Reset all local application data

Prefer the confirmed in-app deletion controls. To perform a complete reset, first end any live session and stop GoalKeeper. Then delete only the configured GoalKeeper data-root directory. Do not delete the broader `%LocalAppData%` directory.

## Runtime configuration

GoalKeeper uses standard ASP.NET Core configuration precedence. Environment variables use double underscores in place of colons.

| Setting | Environment variable | Default |
|---|---|---|
| Provider mode | `GoalKeeper__Providers__Mode` | `Disabled` |
| Data directory | `GoalKeeper__DataRoot` | `%LocalAppData%\GoalKeeper` |
| Camera device | `GoalKeeper__SessionUi__CameraDeviceIndex` | `0` |
| Capture cadence | `GoalKeeper__SessionUi__CaptureCadence` | `00:00:10` |
| Camera JPEG quality | `GoalKeeper__SessionUi__CameraJpegQuality` | `85` |
| Monitoring outage grace | `GoalKeeper__SessionUi__TechnicalGracePeriod` | `00:00:30` |

The complete configuration contract is in [Operational configuration](docs/operational-configuration.md). Exact hosted provider and model settings are in [Hosted provider configuration](docs/provider-configuration.md).

## Troubleshooting

### The expected URL does not open

Use the URL printed after `Now listening on`. The `http` development launch profile uses `http://localhost:5191`; the app's no-profile loopback default is `http://127.0.0.1:5072`.

### The required .NET SDK is not found

Install the .NET 10 SDK selected by `global.json`, then run `dotnet --version` again from the repository root. A .NET runtime alone is not sufficient to build the app.

### Camera capture fails

- Confirm the camera is connected and enabled in Windows privacy settings.
- Close Teams, Zoom, Camera, or another program that may hold the device exclusively.
- If the desired camera is not device `0`, try another non-negative index:

  ```powershell
  $env:GoalKeeper__SessionUi__CameraDeviceIndex = "1"
  dotnet run --project .\src\GoalKeeper.Web --launch-profile http
  ```

- Improve lighting and ensure exactly one person is visible for preflight.

### Preflight says camera validation is unavailable

This is expected in Disabled mode. For a full session, configure the API key, set `GoalKeeper__Providers__Mode` to `Hosted`, restart the host, and retry. In Hosted mode, also check the terminal for a safe configuration or provider error.

### Hosted mode fails during startup

Hosted mode validates all provider settings before serving the UI. Confirm that the API key is stored under the `GoalKeeper.Web` user-secrets ID and that you launched with the Development `http` profile. Remove custom model or endpoint overrides unless they intentionally match the accepted configuration contract.

### A Goal cannot be edited or deleted

An active Focus Session locks its Goal. Return to the live session and complete it or end it early before editing or deleting the Goal.

## Build and test

A normal build requires only the .NET SDK:

```powershell
dotnet restore .\GoalKeeper.sln
dotnet build .\GoalKeeper.sln --configuration Release
dotnet test .\GoalKeeper.sln --configuration Release --no-build
```

The repository's complete deterministic quality gate also runs the retained Python reference tests, formatting verification, package audit, a warnings-as-errors Release build, and the .NET test suite. It does not open real hardware or call hosted providers.

The quality gate requires Python 3.11:

```powershell
py -3.11 -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r requirements-test.txt
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\quality-gate.ps1 -Python .\.venv\Scripts\python.exe
```

Results are written under `TestResults\`. Hardware and live-provider tests are intentionally opt-in and excluded from the default gate.

## Current limitations

- GoalKeeper is a Windows-native, loopback-only prototype for one local user.
- Only one Focus Session can be active at a time.
- Camera interpretation quality varies with framing, lighting, occlusion, and provider behavior.
- There is no identity or face recognition; preflight assumes a single-person scene.
- There are no ad hoc breaks or general pause control. End the session early if you cannot continue outside a Scheduled Break.
- Crash/restart session recovery, automatic retention, cross-session personalization, remote hosting, accounts, and production deployment are out of scope.

For the detailed behavior model, see [Application logic](docs/application-logic.md).
