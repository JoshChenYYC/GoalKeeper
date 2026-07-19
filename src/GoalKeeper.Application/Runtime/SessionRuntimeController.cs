using System.Collections.Concurrent;
using GoalKeeper.Application.Monitoring;
using GoalKeeper.Application.Perception;
using GoalKeeper.Application.Reasoning;
using GoalKeeper.Application.Recovery;
using GoalKeeper.Domain;

namespace GoalKeeper.Application.Runtime;

public sealed class SessionRuntimeController(
    IGoalKeeperRepository repository,
    PreflightOrchestrator preflight,
    MonitoringPipeline monitoring,
    DurableReasoningOrchestrator reasoning,
    IRecoveryPort recovery,
    IClock clock,
    ISessionRuntimeWorkerCoordinator? workerCoordinator = null,
    IVoiceRecoveryPort? voiceRecovery = null)
    : IMonitoringSessionState,
      IMonitoringObservationSink,
      IMonitoringHealthEventSink,
      ICameraTechnicalEventSink,
      IAsyncDisposable
{
    private readonly SemaphoreSlim _commands = new(1, 1);
    private readonly SemaphoreSlim _voiceRecoveryGate = new(1, 1);
    private readonly object _stateSync = new();
    private readonly ConcurrentQueue<MonitoringHealthEvent> _healthEvents = new();
    private readonly HashSet<MonitoringTechnicalSource> _unavailableSources = [];
    private CancellationTokenSource? _monitoringCancellation;
    private Task? _monitoringTask;
    private CancellationTokenSource? _activeVoiceRecoveryCancellation;
    private Guid? _setupId;
    private Guid? _sessionId;
    private long _sessionVersion;
    private FocusSessionState? _sessionState;
    private string? _technicalFailure;
    private MonitoringHealthTracker? _reasoningHealth;

    public Guid SessionId
    {
        get
        {
            lock (_stateSync)
            {
                return _sessionId ?? Guid.Empty;
            }
        }
    }

    public long SessionVersion
    {
        get
        {
            lock (_stateSync)
            {
                return _sessionVersion;
            }
        }
    }

    public bool IsScheduledBreak
    {
        get
        {
            lock (_stateSync)
            {
                return _sessionState == FocusSessionState.ScheduledBreak;
            }
        }
    }

    public async Task<SessionPreflightAttempt> AcquirePreflightAsync(
        Guid setupId,
        PreflightAcquisitionInput input,
        CameraAcquisitionOptions cameraOptions,
        PerceptionRequestOptions? perceptionOptions = null,
        CancellationToken cancellationToken = default)
    {
        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureNoLiveWorker();
            var setup = await repository.GetSetupAsync(setupId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new KeyNotFoundException("Session Setup not found.");
            if (setup.Status != SessionSetupStatus.Ready)
            {
                throw new DomainRuleViolationException("Only a ready Session Setup can enter preflight.");
            }

            var result = await preflight.AcquireAsync(
                    input,
                    cameraOptions,
                    perceptionOptions,
                    cancellationToken)
                .ConfigureAwait(false);
            if (result.Status == PreflightStatus.Cancelled)
            {
                await repository.TransitionSetupAsync(
                        setup.Id,
                        setup.Version,
                        SessionSetupStatus.Cancelled,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            lock (_stateSync)
            {
                _setupId = result.Status == PreflightStatus.Cancelled ? null : setupId;
                if (result.Status != PreflightStatus.Cancelled &&
                    _sessionState is FocusSessionState.Fulfilled or FocusSessionState.EndedEarly)
                {
                    _sessionId = null;
                    _sessionVersion = 0;
                    _sessionState = null;
                }

                _technicalFailure = result.Status == PreflightStatus.TechnicalFailure
                    ? result.Rejection.ToString()
                    : null;
            }

            var preview = result.Frame is null
                ? null
                : new PreflightPreview(
                    result.Frame.Jpeg.ToArray(),
                    result.Frame.PixelWidth,
                    result.Frame.PixelHeight);
            return new(
                setupId,
                result.Status,
                result.Rejection,
                result.CanRetry,
                preview,
                result.Timing);
        }
        finally
        {
            _commands.Release();
        }
    }

    public async Task<SessionPreflightAttempt> CancelPreflightAsync(
        CancellationToken cancellationToken = default)
    {
        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var setupId = _setupId ??
                throw new InvalidOperationException("There is no active preflight.");
            var result = preflight.Cancel();
            var setup = await repository.GetSetupAsync(setupId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new KeyNotFoundException("Session Setup not found.");
            await repository.TransitionSetupAsync(
                    setup.Id,
                    setup.Version,
                    SessionSetupStatus.Cancelled,
                    cancellationToken)
                .ConfigureAwait(false);
            lock (_stateSync)
            {
                _setupId = null;
                _technicalFailure = null;
            }

            return new(
                setupId,
                result.Status,
                result.Rejection,
                result.CanRetry,
                null,
                result.Timing);
        }
        finally
        {
            _commands.Release();
        }
    }

    public async Task<SessionStartResult> ConfirmAndStartAsync(
        bool cameraViewIsCorrect,
        MonitoringOptions monitoringOptions,
        string? artifactDirectory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(monitoringOptions);
        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureNoLiveWorker();
            var setupId = _setupId ??
                throw new InvalidOperationException("A successful preflight attempt is required.");
            var result = preflight.Confirm(cameraViewIsCorrect);
            if (result.Status != PreflightStatus.Passed)
            {
                lock (_stateSync)
                {
                    _setupId = null;
                }

                return new(result.Status, result.Rejection, null);
            }

            var setup = await repository.GetSetupAsync(setupId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new KeyNotFoundException("Session Setup not found.");
            var goal = await repository.GetGoalAsync(setup.Contract.GoalId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new KeyNotFoundException("Goal not found.");
            var settings = await repository.GetSettingsAsync(cancellationToken)
                .ConfigureAwait(false);
            if (workerCoordinator is not null)
            {
                await workerCoordinator.WaitUntilAvailableAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            var session = FocusSession.Start(
                RuntimeAggregateFactory.RehydrateGoal(goal),
                RuntimeAggregateFactory.RehydrateContract(setup.Contract),
                preflightSuccessful: true,
                clock,
                RuntimeAggregateFactory.CreatePolicy(settings));
            var persisted = await repository.StartSessionAsync(
                    setup.Id,
                    setup.Version,
                    session.CreateSnapshot(),
                    artifactDirectory,
                    cancellationToken)
                .ConfigureAwait(false);

            var workerCancellation = new CancellationTokenSource();
            lock (_stateSync)
            {
                _setupId = setupId;
                _sessionId = persisted.Id;
                _sessionVersion = persisted.Version;
                _sessionState = persisted.State;
                _technicalFailure = null;
                _unavailableSources.Clear();
                _reasoningHealth = new(
                    persisted.Id,
                    monitoringOptions.TechnicalGracePeriod,
                    clock,
                    this);
                _monitoringCancellation = workerCancellation;
            }

            var worker = monitoring.RunAsync(this, monitoringOptions, workerCancellation.Token);
            lock (_stateSync)
            {
                _monitoringTask = worker;
            }

            workerCoordinator?.Start(persisted.Id);
            return new(result.Status, result.Rejection, persisted);
        }
        finally
        {
            _commands.Release();
        }
    }

    public Task<FocusSessionRuntimeView?> AdvanceAsync(
        CancellationToken cancellationToken = default) =>
        MutateAsync("runtime.advance", static session => session.Advance(), false, cancellationToken);

    public Task<FocusSessionRuntimeView?> CompleteGoalAsync(
        CancellationToken cancellationToken = default)
    {
        CancelActiveVoiceRecovery();
        return MutateAsync(
            "runtime.complete_goal",
            static session => session.CompleteGoal(),
            true,
            cancellationToken);
    }

    public Task<FocusSessionRuntimeView?> EndEarlyAsync(
        CancellationToken cancellationToken = default)
    {
        CancelActiveVoiceRecovery();
        return MutateAsync(
            "runtime.end_early",
            static session => session.EndEarlyByUser(),
            false,
            cancellationToken);
    }

    public Task<FocusSessionRuntimeView?> ReturnToRecoveryCheckInAsync(
        CancellationToken cancellationToken = default) =>
        MutateAsync(
            "runtime.return_to_recovery",
            static session => session.ReturnToRecoveryCheckIn(),
            false,
            cancellationToken);

    public async Task<FocusSessionRuntimeView?> SubmitRecoveryAsync(
        string? transcript,
        CancellationToken cancellationToken = default)
    {
        CancelActiveVoiceRecovery();
        RecoveryRequest request;
        FocusSessionRuntimeView requestedView;
        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = await LoadCurrentAsync(cancellationToken).ConfigureAwait(false);
            if (loaded is null)
            {
                return null;
            }

            var (view, _, contract, session) = loaded.Value;
            if (view.State != FocusSessionState.RecoveryCheckIn ||
                view.Runtime.ActiveIntervention is not { } active)
            {
                throw new DomainRuleViolationException(
                    "A Recovery response requires an active Recovery Check-in.");
            }

            var turns = await repository.GetRecoveryTurnsAsync(
                    view.Id,
                    active.Id,
                    cancellationToken)
                .ConfigureAwait(false);
            request = CreateRecoveryRequest(
                view,
                contract,
                turns,
                transcript,
                clock.UtcNow);
            requestedView = view;
        }
        finally
        {
            _commands.Release();
        }

        RecoveryPortResult portResult;
        try
        {
            portResult = await recovery.ProposeAsync(request, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            lock (_stateSync)
            {
                _technicalFailure = $"recovery_{exception.GetType().Name}";
            }

            return await repository.GetSessionAsync(requestedView.Id, cancellationToken)
                .ConfigureAwait(false);
        }

        if (portResult is RecoveryFailureResponse failure)
        {
            RecordRecoveryFailure(failure.Category);

            return await repository.GetSessionAsync(requestedView.Id, cancellationToken)
                .ConfigureAwait(false);
        }

        return await CommitRecoveryProposalAsync(
                request,
                requestedView,
                transcript,
                ((RecoveryProposalResponse)portResult).Proposal,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<FocusSessionRuntimeView?> SubmitVoiceRecoveryAsync(
        CancellationToken cancellationToken = default)
    {
        if (voiceRecovery is null)
        {
            throw new InvalidOperationException("Voice Recovery is not configured.");
        }

        await _voiceRecoveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var voiceCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_stateSync)
        {
            _activeVoiceRecoveryCancellation = voiceCancellation;
        }

        try
        {
            return await SubmitVoiceRecoveryCoreAsync(
                    voiceCancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            lock (_stateSync)
            {
                if (ReferenceEquals(
                        _activeVoiceRecoveryCancellation,
                        voiceCancellation))
                {
                    _activeVoiceRecoveryCancellation = null;
                }
            }

            _voiceRecoveryGate.Release();
        }
    }

    private async Task<FocusSessionRuntimeView?> SubmitVoiceRecoveryCoreAsync(
        CancellationToken cancellationToken)
    {
        if (voiceRecovery is null)
        {
            throw new InvalidOperationException("Voice Recovery is not configured.");
        }

        RecoveryRequest request;
        FocusSessionRuntimeView requestedView;
        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = await LoadCurrentAsync(cancellationToken).ConfigureAwait(false);
            if (loaded is null)
            {
                return null;
            }

            var (view, _, contract, session) = loaded.Value;
            if (view.State != FocusSessionState.RecoveryCheckIn ||
                view.Runtime.ActiveIntervention is not { } active)
            {
                throw new DomainRuleViolationException(
                    "A Voice Recovery response requires an active Recovery Check-in.");
            }

            var turns = await repository.GetRecoveryTurnsAsync(
                    view.Id,
                    active.Id,
                    cancellationToken)
                .ConfigureAwait(false);
            request = CreateRecoveryRequest(
                view,
                contract,
                turns,
                transcript: null,
                clock.UtcNow);
            requestedView = view;
        }
        finally
        {
            _commands.Release();
        }

        VoiceRecoveryPortResult portResult;
        try
        {
            portResult = await voiceRecovery.ProposeAsync(request, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (RecoveryVoiceException exception)
        {
            RecordRecoveryFailure(exception.Category);
            return await repository.GetSessionAsync(requestedView.Id, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            lock (_stateSync)
            {
                _technicalFailure = $"recovery_{exception.GetType().Name}";
            }

            return await repository.GetSessionAsync(requestedView.Id, cancellationToken)
                .ConfigureAwait(false);
        }

        if (portResult is VoiceRecoveryFailureResponse failure)
        {
            RecordRecoveryFailure(failure.Category);
            return await repository.GetSessionAsync(requestedView.Id, cancellationToken)
                .ConfigureAwait(false);
        }

        if (portResult is not VoiceRecoveryProposalResponse response)
        {
            lock (_stateSync)
            {
                _technicalFailure = "recovery_invalid_response";
            }

            return await repository.GetSessionAsync(requestedView.Id, cancellationToken)
                .ConfigureAwait(false);
        }

        return await CommitRecoveryProposalAsync(
                request,
                requestedView,
                response.CapturedTranscript,
                response.Proposal,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<FocusSessionRuntimeView?> CommitRecoveryProposalAsync(
        RecoveryRequest request,
        FocusSessionRuntimeView requestedView,
        string? capturedTranscript,
        RecoveryProposal proposal,
        CancellationToken cancellationToken)
    {
        FocusSessionRuntimeView? persisted = null;
        var terminal = false;
        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var fresh = await LoadCurrentAsync(cancellationToken).ConfigureAwait(false);
            if (fresh is null ||
                fresh.Value.View.Id != request.SessionId ||
                fresh.Value.View.Version != request.SessionVersion ||
                fresh.Value.View.State != FocusSessionState.RecoveryCheckIn ||
                fresh.Value.View.Runtime.ActiveIntervention?.Id != request.Intervention.InterventionId)
            {
                return fresh?.View;
            }

            var freshTurns = await repository.GetRecoveryTurnsAsync(
                    request.SessionId,
                    request.Intervention.InterventionId,
                    cancellationToken)
                .ConfigureAwait(false);
            var revalidatedRequest = CreateRecoveryRequest(
                fresh.Value.View,
                fresh.Value.Contract,
                freshTurns,
                capturedTranscript,
                request.RequestedAtUtc);
            var validation = RecoveryProposalValidator.Validate(revalidatedRequest, proposal);
            if (validation is not ValidRecoveryProposal valid)
            {
                lock (_stateSync)
                {
                    _technicalFailure = "recovery_invalid_response";
                }

                return fresh.Value.View;
            }

            var turn = RecoveryTurnFactory.Create(Guid.NewGuid(), valid);
            var before = fresh.Value.Session.CreateSnapshot();
            ApplyRecoveryOutcome(fresh.Value.Session, turn);
            var after = fresh.Value.Session.CreateSnapshot();
            if (after.Version == before.Version)
            {
                after = after with { Version = before.Version + 1 };
            }

            var commit = await repository.CommitRecoveryTurnAsync(
                    new(
                        before.Version,
                        after,
                        RecoveryTurnPersistence.ToWrite(turn),
                        [Audit(
                            $"recovery.{turn.Outcome.ToString().ToLowerInvariant()}",
                            before.State,
                            after.State)]),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!commit.Applied)
            {
                return await repository.GetSessionAsync(requestedView.Id, cancellationToken)
                    .ConfigureAwait(false);
            }

            persisted = await repository.GetSessionAsync(requestedView.Id, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new KeyNotFoundException("Focus Session not found after Recovery commit.");
            UpdateState(persisted);
            lock (_stateSync)
            {
                _technicalFailure = null;
            }

            terminal = persisted.State is FocusSessionState.Fulfilled or FocusSessionState.EndedEarly;
        }
        finally
        {
            _commands.Release();
        }

        if (terminal)
        {
            if (persisted is not null)
            {
                workerCoordinator?.Cancel(persisted.Id);
            }

            await StopMonitoringAsync().ConfigureAwait(false);
        }

        return persisted;
    }

    private void RecordRecoveryFailure(RecoveryFailureCategory category)
    {
        lock (_stateSync)
        {
            _technicalFailure =
                $"recovery_{category.ToString().ToLowerInvariant()}";
        }
    }

    private void CancelActiveVoiceRecovery()
    {
        CancellationTokenSource? active;
        lock (_stateSync)
        {
            active = _activeVoiceRecoveryCancellation;
        }

        try
        {
            active?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Completion raced the state-changing command.
        }
    }

    public async Task<SessionRuntimeStatus> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        Guid? setupId;
        Guid? sessionId;
        Task? worker;
        string? technicalFailure;
        lock (_stateSync)
        {
            setupId = _setupId;
            sessionId = _sessionId;
            worker = _monitoringTask;
            technicalFailure = _technicalFailure;
        }

        var current = sessionId is null
            ? null
            : await repository.GetSessionAsync(sessionId.Value, cancellationToken).ConfigureAwait(false);
        var controllerState = current switch
        {
            { Runtime.State: FocusSessionState.Fulfilled or FocusSessionState.EndedEarly } =>
                SessionRuntimeControllerState.Idle,
            not null => SessionRuntimeControllerState.Running,
            null when setupId is not null => SessionRuntimeControllerState.Preflight,
            _ => SessionRuntimeControllerState.Idle
        };
        return new(
            controllerState,
            setupId,
            current?.Id,
            current?.State,
            current?.Version,
            current?.Runtime.ProjectedEndUtc,
            worker is { IsCompleted: false },
            technicalFailure);
    }

    public async Task<SessionLiveStatus?> GetLiveStatusAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (status.SessionId != sessionId || status.SetupId is not { } setupId)
        {
            return null;
        }

        var session = await repository.GetSessionAsync(sessionId, cancellationToken)
            .ConfigureAwait(false);
        var setup = await repository.GetSetupAsync(setupId, cancellationToken)
            .ConfigureAwait(false);
        if (session is null || setup is null)
        {
            return null;
        }

        var contract = setup.Contract;
        if (contract.Id != session.ContractId)
        {
            throw new PersistenceConflictException(
                "The active Session Contract identity changed.");
        }

        var now = clock.MonotonicNow;
        var elapsed = session.Runtime.Timer.Accumulated;
        if (session.Runtime.Timer.RunningSince is { } runningSince)
        {
            elapsed += now - runningSince;
        }

        elapsed = Positive(elapsed);
        var remaining = Positive(contract.TargetFocusDuration - elapsed);
        TimeSpan? countdown = session.State switch
        {
            FocusSessionState.ScheduledBreak when session.Runtime.BreakEndsAt is { } deadline =>
                Positive(deadline - now),
            FocusSessionState.AwaitingResponse when session.Runtime.ResponseDeadline is { } deadline =>
                Positive(deadline - now),
            FocusSessionState.MonitoringUnavailable when session.Runtime.MonitoringDeadline is { } deadline =>
                Positive(deadline - now),
            FocusSessionState.RecoveryWindow when session.Runtime.CurrentRecoveryWindow is { } window =>
                Positive(window.EndsAt - now),
            _ => null
        };
        var recoveryPrompt = session.Runtime.ActiveIntervention?.Evaluation.Rationale ??
            (session.State == FocusSessionState.RecoveryCheckIn
                ? "GoalKeeper noticed a pattern that may not match this session. What happened?"
                : null);
        var terminal = session.State is
            FocusSessionState.Fulfilled or FocusSessionState.EndedEarly;
        return new(
            session.Id,
            contract.GoalTitle,
            session.State,
            elapsed,
            contract.TargetFocusDuration,
            remaining,
            countdown,
            session.Runtime.ProjectedEndUtc,
            status.HasActiveWorker && !terminal,
            status.TechnicalFailure,
            recoveryPrompt,
            session.State is FocusSessionState.Focusing or FocusSessionState.RecoveryWindow,
            !terminal,
            session.State == FocusSessionState.RecoveryCheckIn,
            session.State == FocusSessionState.AwaitingResponse,
            terminal,
            session.Runtime.EndedEarlyReason);
    }

    public async Task RunSchedulerAsync(
        ISessionRuntimeScheduler scheduler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        try
        {
            while (true)
            {
                await scheduler.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);
                await DrainHealthEventsAsync(cancellationToken).ConfigureAwait(false);
                await AdvanceAsync(cancellationToken).ConfigureAwait(false);
                await ObserveWorkerFailureAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await StopMonitoringAsync().ConfigureAwait(false);
        }
    }

    public async Task PublishAsync(
        ReasoningEligibleObservation observation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var loaded = await LoadCurrentAsync(cancellationToken).ConfigureAwait(false);
        if (loaded is null ||
            loaded.Value.View.State is not (
                FocusSessionState.Focusing or
                FocusSessionState.RecoveryWindow or
                FocusSessionState.MonitoringUnavailable))
        {
            return;
        }

        var (view, _, contract, _) = loaded.Value;
        var result = await reasoning.EvaluateAndAdmitAsync(
                new(contract, view.Runtime, observation.Persisted, []),
                async (context, token) =>
                {
                    var fresh = await LoadCurrentAsync(token).ConfigureAwait(false);
                    if (fresh is null ||
                        fresh.Value.View.Id != context.Request.SessionId ||
                        fresh.Value.View.Version != context.Request.SessionVersion ||
                        fresh.Value.View.State != context.Request.CurrentState)
                    {
                        throw new PersistenceConflictException(
                            "The Focus Session changed while Reasoning was running.");
                    }

                    var domain = fresh.Value.Session;
                    var fromState = domain.State;
                    domain.AdmitIntervention(context.Evaluation);
                    return new(
                        domain.CreateSnapshot(),
                        [Audit("reasoning.intervention_admitted", fromState, domain.State)]);
                },
                cancellationToken)
            .ConfigureAwait(false);
        MonitoringHealthTracker? reasoningHealth;
        lock (_stateSync)
        {
            reasoningHealth = _reasoningHealth;
        }

        if (result.FailureCategory is { } failureCategory)
        {
            reasoningHealth?.ReportFailure(MonitoringTechnicalSource.Reasoning);
            lock (_stateSync)
            {
                _technicalFailure =
                    $"reasoning_{failureCategory.ToString().ToLowerInvariant()}";
            }
        }
        else if (result.Request is not null)
        {
            reasoningHealth?.ReportRecovery(MonitoringTechnicalSource.Reasoning);
            lock (_stateSync)
            {
                if (!_unavailableSources.Contains(MonitoringTechnicalSource.Reasoning) &&
                    _technicalFailure?.StartsWith("reasoning_", StringComparison.Ordinal) == true)
                {
                    _technicalFailure = null;
                }
            }
        }

        var current = await repository.GetSessionAsync(view.Id, cancellationToken)
            .ConfigureAwait(false);
        if (current is not null)
        {
            UpdateState(current);
        }
    }

    public void Report(MonitoringHealthEvent healthEvent)
    {
        ArgumentNullException.ThrowIfNull(healthEvent);
        _healthEvents.Enqueue(healthEvent);
    }

    public void Report(CameraTechnicalEvent technicalEvent)
    {
        ArgumentNullException.ThrowIfNull(technicalEvent);
        lock (_stateSync)
        {
            _technicalFailure = $"camera_{technicalEvent.Kind.ToString().ToLowerInvariant()}";
        }
    }

    public async ValueTask DisposeAsync()
    {
        CancelActiveVoiceRecovery();
        await StopMonitoringAsync().ConfigureAwait(false);
        await _voiceRecoveryGate.WaitAsync().ConfigureAwait(false);
        _voiceRecoveryGate.Release();
        _voiceRecoveryGate.Dispose();
        _commands.Dispose();
    }

    private async Task<FocusSessionRuntimeView?> MutateAsync(
        string eventName,
        Action<FocusSession> command,
        bool completesGoal,
        CancellationToken cancellationToken)
    {
        FocusSessionRuntimeView? persisted = null;
        var terminal = false;
        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            const int maximumAttempts = 3;
            for (var attempt = 1; attempt <= maximumAttempts; attempt++)
            {
                var loaded = await LoadCurrentAsync(cancellationToken).ConfigureAwait(false);
                if (loaded is null)
                {
                    return null;
                }

                var (view, _, _, session) = loaded.Value;
                var before = session.CreateSnapshot();
                command(session);
                var after = session.CreateSnapshot();
                if (after.Version == before.Version)
                {
                    return view;
                }

                var mutation = new RuntimeMutation(
                    before.Version,
                    after,
                    [Audit(eventName, before.State, after.State)])
                {
                    GoalCompletedAtUtc = completesGoal ? clock.UtcNow : null
                };
                try
                {
                    persisted = await repository.UpdateSessionAsync(
                            view.Id,
                            mutation,
                            cancellationToken)
                        .ConfigureAwait(false);
                    UpdateState(persisted);
                    terminal = persisted.Runtime.State is
                        FocusSessionState.Fulfilled or FocusSessionState.EndedEarly;
                    break;
                }
                catch (PersistenceConflictException) when (attempt < maximumAttempts)
                {
                    // Provider admission commits outside the command gate so a slow
                    // result never blocks user controls. Reload and reapply the local
                    // command when that commit wins the optimistic-concurrency race.
                }
            }
        }
        finally
        {
            _commands.Release();
        }

        if (terminal)
        {
            if (persisted is not null)
            {
                workerCoordinator?.Cancel(persisted.Id);
            }

            await StopMonitoringAsync().ConfigureAwait(false);
        }

        return persisted;
    }

    private async Task<(
        FocusSessionRuntimeView View,
        GoalView Goal,
        SessionContractView Contract,
        FocusSession Session)?> LoadCurrentAsync(CancellationToken cancellationToken)
    {
        var sessionId = SessionId;
        if (sessionId == Guid.Empty)
        {
            return null;
        }

        var view = await repository.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (view is null)
        {
            return null;
        }

        var goal = await repository.GetGoalAsync(view.GoalId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Goal not found.");
        var contract = await repository.GetLatestContractAsync(view.GoalId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Session Contract not found.");
        if (contract.Id != view.ContractId)
        {
            throw new PersistenceConflictException("The active Session Contract identity changed.");
        }

        return (
            view,
            goal,
            contract,
            RuntimeAggregateFactory.RehydrateSession(goal, contract, view.Runtime, clock));
    }

    private static RecoveryRequest CreateRecoveryRequest(
        FocusSessionRuntimeView view,
        SessionContractView contract,
        IReadOnlyList<RecoveryTurnView> persistedTurns,
        string? transcript,
        DateTimeOffset requestedAtUtc)
    {
        var active = view.Runtime.ActiveIntervention ??
            throw new DomainRuleViolationException(
                "A Recovery request requires an active Intervention.");
        var evidence = active.Evaluation.EvidenceEpisode ??
            throw new DomainRuleViolationException(
                "A Recovery request requires Intervention evidence.");
        var deviationDescription = evidence.Deviation.ListedDeviationId is { } listed
            ? contract.Deviations.Single(value => value.Id == listed).Description
            : evidence.Deviation.UnlistedDescription!;
        var rationale = active.Evaluation.Rationale ??
            throw new DomainRuleViolationException(
                "A Recovery request requires a Reasoning rationale.");
        var evidenceSummary = CreateEvidenceSummary(evidence.Observations.Count);
        var turns = persistedTurns.Select(RecoveryTurnPersistence.FromView).ToArray();
        var options = new RecoveryRequestOptions(
            view.Runtime.Policy.MaximumCoachingTurns);
        var coachingTurns = persistedTurns
            .Select(RecoveryTurnPersistence.FromView)
            .Count(value => value.Outcome == RecoveryOutcome.AdditionalCoaching);
        IReadOnlyList<RecoveryOutcome> allowed;
        if (view.Runtime.RequiresFinalEscalation)
        {
            allowed = [RecoveryOutcome.ContinueSession, RecoveryOutcome.EndEarly];
        }
        else
        {
            var ordinary = new List<RecoveryOutcome>
            {
                RecoveryOutcome.Recommit,
                RecoveryOutcome.BehaviorClarification,
                RecoveryOutcome.EndEarly
            };
            if (coachingTurns < options.MaximumCoachingTurns)
            {
                ordinary.Add(RecoveryOutcome.AdditionalCoaching);
            }

            ordinary.Add(RecoveryOutcome.UnclearResponse);
            ordinary.Add(RecoveryOutcome.NoResponse);
            allowed = ordinary;
        }
        return new(
            view.Id,
            view.Version,
            new(
                contract.Id,
                contract.GoalId,
                contract.GoalTitle,
                contract.GoalDescription,
                contract.TargetFocusDuration,
                contract.ReasoningMode,
                contract.Sensitivity),
            new(
                active.Id,
                evidence.Deviation.ListedDeviationId,
                deviationDescription,
                evidenceSummary,
                rationale,
                active.AdmittedAtUtc),
            new(
                active.AdmittedAt - active.DisputedDuration,
                active.AdmittedAt),
            view.Runtime.DeviationOverrides.Select(value =>
                new RecoveryOverrideContext(
                    value.Id,
                    value.Deviation.ListedDeviationId,
                    value.Deviation.ListedDeviationId is { } overrideId
                        ? contract.Deviations.Single(item => item.Id == overrideId).Description
                        : value.Deviation.UnlistedDescription!,
                    value.Reason,
                    value.AppliedAtUtc)),
            allowed,
            turns,
            transcript,
            requestedAtUtc,
            options);
    }

    private static string CreateEvidenceSummary(int observationCount) =>
        observationCount == 1
            ? "The evidence episode contains 1 ordered observation associated with the suspected pattern."
            : $"The evidence episode contains {observationCount} ordered observations associated with the suspected pattern.";

    private static void ApplyRecoveryOutcome(FocusSession session, RecoveryTurn turn)
    {
        switch (turn.Outcome)
        {
            case RecoveryOutcome.Recommit:
                session.Recommit();
                break;
            case RecoveryOutcome.BehaviorClarification:
                session.ClarifyBehavior(
                    turn.Clarification!,
                    turn.RemainderOverrideConfirmed);
                break;
            case RecoveryOutcome.EndEarly:
                session.EndEarlyByUser();
                break;
            case RecoveryOutcome.ContinueSession:
                session.ConfirmContinuationAfterEscalation();
                break;
            case RecoveryOutcome.NoResponse:
                session.ReportNoResponse();
                break;
            case RecoveryOutcome.AdditionalCoaching:
            case RecoveryOutcome.UnclearResponse:
                break;
            default:
                throw new DomainRuleViolationException(
                    "The Recovery outcome is not supported.");
        }
    }

    private async Task DrainHealthEventsAsync(CancellationToken cancellationToken)
    {
        while (_healthEvents.TryDequeue(out var healthEvent))
        {
            var current = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
            if (current.SessionId != healthEvent.SessionId)
            {
                continue;
            }

            var shouldTransition = false;
            var shouldRestore = false;
            lock (_stateSync)
            {
                if (healthEvent.Kind == MonitoringHealthEventKind.TechnicalGraceExpired)
                {
                    _unavailableSources.Add(healthEvent.Source);
                    _technicalFailure = string.Join(",", _unavailableSources.Order());
                    shouldTransition =
                        current.State is FocusSessionState.Focusing or FocusSessionState.RecoveryWindow;
                }
                else
                {
                    _unavailableSources.Remove(healthEvent.Source);
                    shouldRestore =
                        current.State == FocusSessionState.MonitoringUnavailable &&
                        _unavailableSources.Count == 0;
                    _technicalFailure = _unavailableSources.Count == 0
                        ? null
                        : string.Join(",", _unavailableSources.Order());
                }
            }

            if (shouldTransition)
            {
                await MutateAsync(
                        "monitoring.unavailable",
                        static session => session.ReportMonitoringUnavailable(),
                        false,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (shouldRestore)
            {
                await MutateAsync(
                        "monitoring.restored",
                        static session => session.RestoreMonitoring(),
                        false,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task ObserveWorkerFailureAsync(CancellationToken cancellationToken)
    {
        Task? worker;
        lock (_stateSync)
        {
            worker = _monitoringTask;
        }

        if (worker is not { IsFaulted: true })
        {
            return;
        }

        lock (_stateSync)
        {
            _technicalFailure = worker.Exception?.GetBaseException().GetType().Name ??
                "monitoring_failure";
            _unavailableSources.Add(MonitoringTechnicalSource.Camera);
        }

        var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (status.State is FocusSessionState.Focusing or FocusSessionState.RecoveryWindow)
        {
            await MutateAsync(
                    "monitoring.worker_failed",
                    static session => session.ReportMonitoringUnavailable(),
                    false,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task StopMonitoringAsync()
    {
        CancellationTokenSource? cancellation;
        Task? worker;
        lock (_stateSync)
        {
            cancellation = _monitoringCancellation;
            worker = _monitoringTask;
            _monitoringCancellation = null;
            _monitoringTask = null;
            _reasoningHealth = null;
            _unavailableSources.Clear();
        }

        if (cancellation is null)
        {
            return;
        }

        await cancellation.CancelAsync().ConfigureAwait(false);
        try
        {
            if (worker is not null)
            {
                await worker.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private void EnsureNoLiveWorker()
    {
        lock (_stateSync)
        {
            if (_monitoringTask is { IsCompleted: false })
            {
                throw new DomainRuleViolationException("Another Focus Session worker is already active.");
            }
        }
    }

    private void UpdateState(FocusSessionRuntimeView view)
    {
        lock (_stateSync)
        {
            _sessionId = view.Id;
            _sessionVersion = view.Version;
            _sessionState = view.State;
        }
    }

    private static TimeSpan Positive(TimeSpan value) =>
        value < TimeSpan.Zero ? TimeSpan.Zero : value;

    private RuntimeAuditWrite Audit(
        string eventName,
        FocusSessionState? fromState,
        FocusSessionState? toState) =>
        new(clock.UtcNow, eventName, fromState, toState, "{}");
}
