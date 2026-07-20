using System.Diagnostics;
using GoalKeeper.Application;
using GoalKeeper.Application.Monitoring;
using GoalKeeper.Application.Recovery;
using GoalKeeper.Application.Runtime;
using GoalKeeper.Domain;
using GoalKeeper.Web.Operations;
using Microsoft.Extensions.Options;

namespace GoalKeeper.Web.Presentation;

public sealed class SessionRuntimeUiOptions
{
    public int CameraDeviceIndex { get; set; }
    public int CameraWarmupFrameCount { get; set; } = 8;
    public int CameraJpegQuality { get; set; } = 85;
    public TimeSpan CaptureCadence { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan ObservationFreshnessLimit { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan TechnicalGracePeriod { get; set; } = TimeSpan.FromSeconds(30);

    internal CameraAcquisitionOptions CameraOptions() =>
        new(CameraDeviceIndex, CameraWarmupFrameCount, CameraJpegQuality);

    internal MonitoringOptions MonitoringOptions() =>
        new(CaptureCadence, ObservationFreshnessLimit, TechnicalGracePeriod, CameraOptions());
}

public sealed record PreflightPageView(
    Guid SetupId,
    string GoalTitle,
    TimeSpan TargetFocus,
    int ScheduledBreakCount,
    string ProfileName,
    string ReasoningSummary,
    PreflightStatus? Status,
    string? StatusMessage,
    string? PreviewDataUrl,
    int? PreviewWidth,
    int? PreviewHeight,
    PreflightTiming? Timing,
    bool CanCapture,
    bool CanRetry,
    bool CanConfirm);

public sealed record LiveSessionPageView(
    Guid SessionId,
    string GoalTitle,
    FocusSessionState State,
    string StateLabel,
    string StateMessage,
    TimeSpan FocusElapsed,
    TimeSpan FocusTarget,
    TimeSpan FocusRemaining,
    TimeSpan? StateCountdown,
    DateTimeOffset ProjectedEndUtc,
    bool MonitoringActive,
    string? TechnicalFailure,
    string? RecoveryAccountabilityMessage,
    string? RecoveryEvidenceContext,
    bool CanReplayRecoveryOpening,
    string? RecoveryAudioNotice,
    bool AutomaticRecoveryVoiceInProgress,
    bool AutomaticRecoveryMicrophoneActive,
    bool CanCompleteGoal,
    bool CanEndEarly,
    bool CanSubmitRecovery,
    bool CanSubmitVoiceRecovery,
    bool CanReturnToRecovery,
    bool IsTerminal,
    EndedEarlyReason? EndedEarlyReason,
    TimeSpan? StartupDuration);

public sealed record SessionStartPresentation(
    bool Started,
    Guid? SessionId,
    string? Error,
    TimeSpan Duration);

public interface ISessionRuntimePresentation
{
    Task<PreflightPageView> GetPreflightAsync(Guid setupId, CancellationToken cancellationToken = default);
    Task<PreflightPageView> CaptureAsync(Guid setupId, bool retry, CancellationToken cancellationToken = default);
    Task CancelPreflightAsync(Guid setupId, CancellationToken cancellationToken = default);
    Task<SessionStartPresentation> ConfirmAndStartAsync(Guid setupId, CancellationToken cancellationToken = default);
    Task<LiveSessionPageView?> GetLiveAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task<LiveSessionPageView?> CompleteGoalAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task<LiveSessionPageView?> EndEarlyAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task<LiveSessionPageView?> SubmitRecoveryAsync(Guid sessionId, string response, CancellationToken cancellationToken = default);
    Task<LiveSessionPageView?> SubmitVoiceRecoveryAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task<LiveSessionPageView?> ReplayRecoveryOpeningAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task<LiveSessionPageView?> ReturnToRecoveryAsync(Guid sessionId, CancellationToken cancellationToken = default);
}

public sealed class SessionRuntimePresentation(
    SessionRuntimeController controller,
    IGoalKeeperRepository repository,
    IOptions<SessionRuntimeUiOptions> options,
    IOptions<GoalKeeperOperationalOptions> operationalOptions,
    IVoiceRecoveryPort? voiceRecovery = null,
    ISpeechOutputPort? speechOutput = null) : ISessionRuntimePresentation, IDisposable
{
    private readonly SemaphoreSlim _commands = new(1, 1);
    private readonly SemaphoreSlim _preflightCommands = new(1, 1);
    private readonly SemaphoreSlim _recoveryAudio = new(1, 1);
    private readonly object _recoveryAudioSync = new();
    private readonly SessionRuntimeUiOptions _options = options.Value;
    private readonly bool _hostedProvidersEnabled =
        operationalOptions.Value.Providers.Mode == GoalKeeperProviderMode.Hosted;
    private PreflightPageView? _activePreflightView;
    private SessionStartDiagnostic? _lastSessionStart;
    private Guid? _automaticRecoveryInterventionId;
    private Guid? _automaticRecoveryInProgressId;
    private Guid? _automaticMicrophoneInterventionId;
    private Guid? _recoveryAutomationNoticeId;
    private string? _recoveryAutomationNotice;

    public async Task<PreflightPageView> GetPreflightAsync(
        Guid setupId,
        CancellationToken cancellationToken = default)
    {
        await _preflightCommands.WaitAsync(cancellationToken);
        try
        {
            var status = await controller.GetStatusAsync(cancellationToken);
            ClearStalePreflight(status);
            var setup = await RequireReadySetupAsync(setupId, cancellationToken);
            if (_activePreflightView is { } existing &&
                existing.SetupId == setupId &&
                status.ControllerState == SessionRuntimeControllerState.Preflight &&
                status.SetupId == setupId)
            {
                return existing;
            }

            return MapPreflight(setup, null);
        }
        finally
        {
            _preflightCommands.Release();
        }
    }

    public async Task<PreflightPageView> CaptureAsync(
        Guid setupId,
        bool retry,
        CancellationToken cancellationToken = default)
    {
        await _preflightCommands.WaitAsync(cancellationToken);
        try
        {
            var statusBeforeCapture = await controller.GetStatusAsync(cancellationToken);
            ClearStalePreflight(statusBeforeCapture);
            var setup = await RequireReadySetupAsync(setupId, cancellationToken);
            _activePreflightView = null;
            var attempt = await controller.AcquirePreflightAsync(
                setupId,
                retry ? PreflightAcquisitionInput.Retry : PreflightAcquisitionInput.Capture,
                _options.CameraOptions(),
                cancellationToken: cancellationToken);
            var status = await controller.GetStatusAsync(cancellationToken);
            if (status.ControllerState != SessionRuntimeControllerState.Preflight ||
                status.SetupId != setupId)
            {
                throw new InvalidOperationException(
                    "The active camera preflight changed before the preview was ready.");
            }

            _activePreflightView = MapPreflight(setup, attempt);
            return _activePreflightView;
        }
        finally
        {
            _preflightCommands.Release();
        }
    }

    public async Task CancelPreflightAsync(Guid setupId, CancellationToken cancellationToken = default)
    {
        await _preflightCommands.WaitAsync(cancellationToken);
        try
        {
            var status = await controller.GetStatusAsync(cancellationToken);
            if (status.ControllerState == SessionRuntimeControllerState.Preflight)
            {
                if (status.SetupId != setupId)
                {
                    ClearStalePreflight(status);
                    throw new InvalidOperationException(
                        "Another Session Setup owns the active camera preflight.");
                }

                await controller.CancelPreflightAsync(cancellationToken);
                _activePreflightView = null;
                return;
            }

            if (status.ControllerState == SessionRuntimeControllerState.Running)
            {
                _activePreflightView = null;
                throw new InvalidOperationException(
                    "Camera preflight cannot be cancelled after monitoring starts.");
            }

            ClearStalePreflight(status);
            await controller.AcquirePreflightAsync(
                setupId,
                PreflightAcquisitionInput.Cancel,
                _options.CameraOptions(),
                cancellationToken: cancellationToken);
            _activePreflightView = null;
        }
        finally
        {
            _preflightCommands.Release();
        }
    }

    public async Task<SessionStartPresentation> ConfirmAndStartAsync(
        Guid setupId,
        CancellationToken cancellationToken = default)
    {
        var startedAt = Stopwatch.GetTimestamp();
        await _preflightCommands.WaitAsync(cancellationToken);
        try
        {
            var status = await controller.GetStatusAsync(cancellationToken);
            var canConfirm = _activePreflightView is
            {
                CanConfirm: true
            } view &&
                view.SetupId == setupId &&
                status.ControllerState == SessionRuntimeControllerState.Preflight &&
                status.SetupId == setupId;

            if (!canConfirm)
            {
                ClearStalePreflight(status);
                return new(
                    false,
                    null,
                    "Capture and validate a camera view before starting.",
                    Stopwatch.GetElapsedTime(startedAt));
            }

            SessionStartResult result;
            try
            {
                result = await controller.ConfirmAndStartAsync(
                    cameraViewIsCorrect: true,
                    _options.MonitoringOptions(),
                    cancellationToken: cancellationToken);
            }
            finally
            {
                _activePreflightView = null;
            }

            var duration = Stopwatch.GetElapsedTime(startedAt);
            if (result.Session is null)
            {
                return new(
                    false,
                    null,
                    PreflightMessage(result.PreflightStatus, result.Rejection),
                    duration);
            }

            _lastSessionStart = new(result.Session.Id, duration);
            return new(true, result.Session.Id, null, duration);
        }
        finally
        {
            _preflightCommands.Release();
        }
    }

    public Task<LiveSessionPageView?> GetLiveAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default) =>
        LoadLiveAsync(sessionId, cancellationToken);

    public Task<LiveSessionPageView?> CompleteGoalAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default) =>
        RunIdempotentAsync(sessionId, controller.CompleteGoalAsync, cancellationToken);

    public Task<LiveSessionPageView?> EndEarlyAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default) =>
        RunIdempotentAsync(sessionId, controller.EndEarlyAsync, cancellationToken);

    public async Task<LiveSessionPageView?> SubmitRecoveryAsync(
        Guid sessionId,
        string response,
        CancellationToken cancellationToken = default)
    {
        ThrowIfAutomaticRecoveryIsRunning();
        if (string.IsNullOrWhiteSpace(response))
        {
            throw new ArgumentException("Tell GoalKeeper what happened before continuing.", nameof(response));
        }

        await EnsureCurrentSessionAsync(sessionId, cancellationToken);
        await controller.SubmitRecoveryAsync(response.Trim(), cancellationToken);
        return await LoadLiveAsync(sessionId, cancellationToken);
    }

    public async Task<LiveSessionPageView?> SubmitVoiceRecoveryAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfAutomaticRecoveryIsRunning();
        if (voiceRecovery is null)
        {
            throw new InvalidOperationException("Voice Recovery is not configured.");
        }

        await EnsureCurrentSessionAsync(sessionId, cancellationToken);
        await controller.SubmitVoiceRecoveryAsync(cancellationToken);
        return await LoadLiveAsync(sessionId, cancellationToken);
    }

    public async Task<LiveSessionPageView?> ReplayRecoveryOpeningAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfAutomaticRecoveryIsRunning();
        if (speechOutput is null)
        {
            throw new InvalidOperationException("Recovery audio is not configured.");
        }

        await EnsureCurrentSessionAsync(sessionId, cancellationToken);
        var live = await controller.GetLiveStatusAsync(sessionId, cancellationToken);
        if (live is not
            {
                CanSubmitRecovery: true,
                RecoveryInterventionId: { } interventionId,
                RecoveryAccountabilityMessage: { } accountabilityMessage
            })
        {
            throw new InvalidOperationException(
                "There is no active Recovery opening to replay.");
        }

        await SpeakRecoveryOpeningAsync(accountabilityMessage, cancellationToken);
        lock (_recoveryAudioSync)
        {
            if (_automaticRecoveryInterventionId == interventionId)
            {
                _recoveryAutomationNoticeId = null;
                _recoveryAutomationNotice = null;
            }
        }

        return await LoadLiveAsync(sessionId, cancellationToken);
    }

    public async Task<LiveSessionPageView?> ReturnToRecoveryAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        await EnsureCurrentSessionAsync(sessionId, cancellationToken);
        await controller.ReturnToRecoveryCheckInAsync(cancellationToken);
        return await LoadLiveAsync(sessionId, cancellationToken);
    }

    private async Task<LiveSessionPageView?> RunIdempotentAsync(
        Guid sessionId,
        Func<CancellationToken, Task<FocusSessionRuntimeView?>> action,
        CancellationToken cancellationToken)
    {
        await _commands.WaitAsync(cancellationToken);
        try
        {
            var current = await LoadLiveAsync(sessionId, cancellationToken);
            if (current?.IsTerminal == true)
            {
                return current;
            }

            if (current is null)
            {
                await EnsureCurrentSessionAsync(sessionId, cancellationToken);
            }

            await action(cancellationToken);
            return await LoadLiveAsync(sessionId, cancellationToken);
        }
        finally
        {
            _commands.Release();
        }
    }

    private async Task EnsureCurrentSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var status = await controller.GetStatusAsync(cancellationToken);
        if (status.SessionId != sessionId)
        {
            throw new InvalidOperationException("This page is not connected to the active Focus Session.");
        }
    }

    private async Task<LiveSessionPageView?> LoadLiveAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        var live = await controller.GetLiveStatusAsync(sessionId, cancellationToken);
        if (live is null)
        {
            return null;
        }

        var automation = StartAutomaticRecovery(live);
        return new(
            live.SessionId,
            live.GoalTitle,
            live.State,
            StateLabel(live.State),
            StateMessage(live.State),
            live.FocusElapsed,
            live.FocusTarget,
            live.FocusRemaining,
            live.StateCountdown,
            live.ProjectedEndUtc,
            live.MonitoringActive,
            live.TechnicalFailure,
            live.RecoveryAccountabilityMessage,
            live.RecoveryEvidenceContext,
            live.CanSubmitRecovery &&
            live.RecoveryAccountabilityMessage is not null &&
            speechOutput is not null,
            automation.Notice,
            automation.InProgress,
            automation.MicrophoneActive,
            live.CanCompleteGoal,
            live.CanEndEarly,
            live.CanSubmitRecovery,
            live.CanSubmitRecovery &&
            voiceRecovery is not null &&
            !automation.InProgress,
            live.CanReturnToRecovery,
            live.IsTerminal,
            live.EndedEarlyReason,
            _lastSessionStart is { } diagnostic && diagnostic.SessionId == live.SessionId
                ? diagnostic.Duration
                : null);
    }

    private RecoveryAutomationView StartAutomaticRecovery(
        SessionLiveStatus live)
    {
        if (speechOutput is null ||
            !live.CanSubmitRecovery ||
            live.RecoveryInterventionId is not { } interventionId ||
            live.RecoveryAccountabilityMessage is not { } accountabilityMessage)
        {
            lock (_recoveryAudioSync)
            {
                return new(
                    _automaticRecoveryInProgressId is not null,
                    _automaticMicrophoneInterventionId is not null,
                    null);
            }
        }

        lock (_recoveryAudioSync)
        {
            if (_automaticRecoveryInterventionId != interventionId)
            {
                _automaticRecoveryInterventionId = interventionId;
                _automaticRecoveryInProgressId = interventionId;
                _automaticMicrophoneInterventionId = null;
                _recoveryAutomationNoticeId = null;
                _recoveryAutomationNotice = null;
                _ = RunAutomaticRecoveryAsync(
                    interventionId,
                    accountabilityMessage);
            }

            return AutomationView(interventionId);
        }
    }

    private async Task RunAutomaticRecoveryAsync(
        Guid interventionId,
        string accountabilityMessage)
    {
        await Task.Yield();
        try
        {
            try
            {
                await SpeakRecoveryOpeningAsync(
                    accountabilityMessage,
                    CancellationToken.None);
            }
            catch
            {
                SetAutomationNotice(
                    interventionId,
                    "Audio playback did not start. Read the check-in below or try Replay audio.");
                return;
            }

            if (voiceRecovery is null)
            {
                return;
            }

            lock (_recoveryAudioSync)
            {
                if (_automaticRecoveryInterventionId == interventionId)
                {
                    _automaticMicrophoneInterventionId = interventionId;
                }
            }

            var result = await controller.SubmitVoiceRecoveryAsync(
                CancellationToken.None);
            if (result?.State == FocusSessionState.RecoveryCheckIn)
            {
                SetAutomationNotice(
                    interventionId,
                    "Automatic voice response did not complete. Try voice again or type your answer.");
            }
        }
        catch
        {
            SetAutomationNotice(
                interventionId,
                "Automatic voice response did not complete. Try voice again or type your answer.");
        }
        finally
        {
            lock (_recoveryAudioSync)
            {
                if (_automaticRecoveryInterventionId == interventionId)
                {
                    _automaticRecoveryInProgressId = null;
                    _automaticMicrophoneInterventionId = null;
                }
            }
        }
    }

    private void SetAutomationNotice(Guid interventionId, string notice)
    {
        lock (_recoveryAudioSync)
        {
            if (_automaticRecoveryInterventionId == interventionId)
            {
                _recoveryAutomationNoticeId = interventionId;
                _recoveryAutomationNotice = notice;
            }
        }
    }

    private RecoveryAutomationView AutomationView(Guid interventionId) =>
        new(
            _automaticRecoveryInProgressId == interventionId,
            _automaticMicrophoneInterventionId == interventionId,
            _recoveryAutomationNoticeId == interventionId
                ? _recoveryAutomationNotice
                : null);

    private void ThrowIfAutomaticRecoveryIsRunning()
    {
        lock (_recoveryAudioSync)
        {
            if (_automaticRecoveryInProgressId is not null)
            {
                throw new InvalidOperationException(
                    "GoalKeeper is already listening for the automatic voice response.");
            }
        }
    }

    private async Task SpeakRecoveryOpeningAsync(
        string accountabilityMessage,
        CancellationToken cancellationToken)
    {
        await _recoveryAudio.WaitAsync(cancellationToken);
        try
        {
            await speechOutput!.SpeakAsync(
                RecoveryOpeningPrompt.Create(accountabilityMessage),
                cancellationToken);
        }
        finally
        {
            _recoveryAudio.Release();
        }
    }

    private async Task<SessionSetupView> RequireReadySetupAsync(
        Guid setupId,
        CancellationToken cancellationToken)
    {
        var setup = await repository.GetSetupAsync(setupId, cancellationToken)
            ?? throw new KeyNotFoundException("Session Setup not found.");
        if (setup.Status != SessionSetupStatus.Ready)
        {
            throw new InvalidOperationException("This Session Setup is no longer ready for preflight.");
        }

        return setup;
    }

    private PreflightPageView MapPreflight(
        SessionSetupView setup,
        SessionPreflightAttempt? attempt)
    {
        var preview = attempt?.Preview;
        var dataUrl = preview is null
            ? null
            : $"data:image/jpeg;base64,{Convert.ToBase64String(preview.Jpeg)}";
        return new(
            setup.Id,
            setup.Contract.GoalTitle,
            setup.Contract.TargetFocusDuration,
            setup.Contract.ScheduledBreaks.Count,
            setup.Contract.DeviationProfileName,
            $"{setup.Contract.ReasoningMode} · {setup.Contract.Sensitivity}",
            attempt?.Status,
            attempt is null
                ? _hostedProvidersEnabled
                    ? null
                    : "Hosted AI validation is disabled. You can capture a test image, but GoalKeeper cannot approve preflight or start a Focus Session until Hosted mode is configured."
                : PreflightMessage(attempt.Status, attempt.Rejection),
            dataUrl,
            preview?.PixelWidth,
            preview?.PixelHeight,
            attempt?.Timing,
            attempt is null,
            attempt?.CanRetry == true,
            attempt?.Status == PreflightStatus.AwaitingConfirmation);
    }

    private void ClearStalePreflight(SessionRuntimeStatus status)
    {
        if (_activePreflightView is not { } cached)
        {
            return;
        }

        if (status.ControllerState != SessionRuntimeControllerState.Preflight ||
            status.SetupId != cached.SetupId)
        {
            _activePreflightView = null;
        }
    }

    private string PreflightMessage(PreflightStatus status, PreflightRejection rejection) =>
        (status, rejection) switch
        {
            (PreflightStatus.AwaitingConfirmation, _) =>
                "One person is visible and the image is usable. Confirm that this view is correct.",
            (PreflightStatus.Rejected, PreflightRejection.ImageQuality) =>
                "The image is not clear enough. Adjust the light or camera and try again.",
            (PreflightStatus.Rejected, PreflightRejection.PeopleCount) =>
                "Preflight needs exactly one person in view. Adjust the camera and try again.",
            (PreflightStatus.TechnicalFailure, _) when !_hostedProvidersEnabled =>
                "Image captured, but hosted AI validation is disabled. Configure an OpenAI API key, enable Hosted mode, restart GoalKeeper, and try again. No behavioral judgment was recorded.",
            (PreflightStatus.TechnicalFailure, _) =>
                "Image captured, but hosted camera validation failed. Check the GoalKeeper host output and provider configuration, then try again. No behavioral judgment was recorded.",
            (PreflightStatus.Cancelled, _) => "Preflight was cancelled.",
            _ => "The camera view could not be confirmed. Try another capture."
        };

    private static string StateLabel(FocusSessionState state) =>
        state switch
        {
            FocusSessionState.Focusing => "Focus in progress",
            FocusSessionState.ScheduledBreak => "Scheduled break",
            FocusSessionState.RecoveryCheckIn => "Recovery check-in",
            FocusSessionState.RecoveryWindow => "Back in focus",
            FocusSessionState.AwaitingResponse => "Waiting for you",
            FocusSessionState.MonitoringUnavailable => "Monitoring paused",
            FocusSessionState.Fulfilled => "Session fulfilled",
            FocusSessionState.EndedEarly => "Session ended",
            _ => state.ToString()
        };

    private static string StateMessage(FocusSessionState state) =>
        state switch
        {
            FocusSessionState.Focusing => "Keep the next action small. GoalKeeper is quietly monitoring.",
            FocusSessionState.ScheduledBreak => "This break is part of your contract. Monitoring cannot create evidence now.",
            FocusSessionState.RecoveryCheckIn => "The focus timer is paused. Respond in your own words.",
            FocusSessionState.RecoveryWindow => "You recommitted. This recovery window gives you space to settle back in.",
            FocusSessionState.AwaitingResponse => "The timer remains paused. Return before the response window closes.",
            FocusSessionState.MonitoringUnavailable => "A technical issue paused the timer. It is not behavioral evidence.",
            FocusSessionState.Fulfilled => "You reached the session outcome. Take a moment before moving on.",
            FocusSessionState.EndedEarly => "The session and its monitoring resources have been closed.",
            _ => ""
        };

    public void Dispose()
    {
        _commands.Dispose();
        _preflightCommands.Dispose();
        _recoveryAudio.Dispose();
    }

    private sealed record SessionStartDiagnostic(Guid SessionId, TimeSpan Duration);

    private readonly record struct RecoveryAutomationView(
        bool InProgress,
        bool MicrophoneActive,
        string? Notice);
}
