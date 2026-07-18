using System.Text.Json;
using System.Text.Json.Serialization;
using GoalKeeper.Application.Perception;
using GoalKeeper.Domain;

namespace GoalKeeper.Application.Monitoring;

public sealed class MonitoringPipeline(
    ICameraFactory cameraFactory,
    IPerceptionPort perception,
    IGoalKeeperRepository repository,
    ISnapshotArtifactStore artifactStore,
    IMonitoringDelay delay,
    IClock clock,
    IMonitoringObservationSink observationSink,
    IMonitoringHealthEventSink healthEventSink)
{
    private static readonly JsonSerializerOptions ObservationJsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly object _sync = new();
    private PendingSnapshot? _pending;
    private Task _processor = Task.CompletedTask;
    private bool _processing;
    private bool _accepting;
    private MonitoringOptions? _options;
    private IMonitoringSessionState? _session;
    private MonitoringHealthTracker? _health;

    public async Task RunAsync(
        IMonitoringSessionState session,
        MonitoringOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (session.SessionId == Guid.Empty || session.SessionVersion <= 0)
        {
            throw new ArgumentException("Monitoring requires an active versioned Focus Session.", nameof(session));
        }

        lock (_sync)
        {
            if (_accepting || _processing)
            {
                throw new InvalidOperationException("This monitoring pipeline is already running.");
            }

            _session = session;
            _options = options;
            _health = new(
                session.SessionId,
                options.TechnicalGracePeriod,
                clock,
                healthEventSink);
            _accepting = true;
        }

        var camera = cameraFactory.Create();
        var sequence = 0;
        var nextCapture = clock.MonotonicNow;
        try
        {
            await camera.OpenAsync(options.Camera.DeviceIndex, cancellationToken);
            await camera.WarmUpAsync(options.Camera.WarmupFrameCount, cancellationToken);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await WaitForGridAsync(nextCapture, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    using var rawFrame = await camera.CaptureFrameAsync(cancellationToken);
                    var frame = await camera.EncodeJpegAsync(
                        rawFrame,
                        options.Camera.JpegQuality,
                        cancellationToken);
                    var snapshot = await RetainSnapshotAsync(
                        session,
                        sequence++,
                        frame,
                        cancellationToken);
                    if (session.IsScheduledBreak)
                    {
                        await repository.UpdateSnapshotStatusAsync(
                            session.SessionId,
                            snapshot.Persisted.Id,
                            SnapshotProcessingStatus.Superseded,
                            cancellationToken);
                    }
                    else
                    {
                        await EnqueueAsync(snapshot, cancellationToken);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    _health.ReportFailure(MonitoringTechnicalSource.Camera);
                }

                nextCapture += options.CaptureCadence;
                var now = clock.MonotonicNow;
                if (now >= nextCapture)
                {
                    var missed = ((now - nextCapture).Ticks / options.CaptureCadence.Ticks) + 1;
                    nextCapture += TimeSpan.FromTicks(options.CaptureCadence.Ticks * missed);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            _health.ReportFailure(MonitoringTechnicalSource.Camera);
            throw;
        }
        finally
        {
            Task processor;
            PendingSnapshot? abandoned;
            lock (_sync)
            {
                _accepting = false;
                abandoned = _pending;
                _pending = null;
                processor = _processor;
            }

            try
            {
                if (abandoned is not null)
                {
                    await FinalizeStatusAsync(
                        abandoned.Persisted.Id,
                        SnapshotProcessingStatus.Superseded);
                }

                await processor.ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    await camera.ReleaseAsync(CancellationToken.None);
                }
                finally
                {
                    try
                    {
                        await camera.DisposeAsync();
                    }
                    finally
                    {
                        lock (_sync)
                        {
                            _session = null;
                            _options = null;
                            _health = null;
                            _processor = Task.CompletedTask;
                        }
                    }
                }
            }
        }
    }

    private async Task WaitForGridAsync(
        TimeSpan dueAt,
        CancellationToken cancellationToken)
    {
        var remaining = dueAt - clock.MonotonicNow;
        if (remaining > TimeSpan.Zero)
        {
            await delay.DelayAsync(remaining, cancellationToken);
        }
    }

    private async Task<PendingSnapshot> RetainSnapshotAsync(
        IMonitoringSessionState session,
        int sequence,
        CapturedJpegFrame frame,
        CancellationToken cancellationToken)
    {
        var artifact = await artifactStore.RetainAsync(
            session.SessionId,
            sequence,
            frame,
            cancellationToken);
        try
        {
            var persisted = await repository.AddSnapshotAsync(
                new(
                    frame.Id,
                    session.SessionId,
                    sequence,
                    frame.CapturedAtUtc,
                    frame.CapturedAtMonotonic,
                    artifact.Path,
                    artifact.StoredBytes,
                    SnapshotProcessingStatus.Captured,
                    session.SessionVersion),
                cancellationToken);
            return new(frame, persisted);
        }
        catch
        {
            await artifactStore.DeleteAsync(
                session.SessionId,
                artifact.Path,
                CancellationToken.None);
            throw;
        }
    }

    private async Task EnqueueAsync(
        PendingSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        PendingSnapshot? replaced = null;
        lock (_sync)
        {
            if (!_accepting)
            {
                replaced = snapshot;
            }
            else if (_processing)
            {
                replaced = _pending;
                _pending = snapshot;
            }
            else
            {
                _processing = true;
                _processor = ProcessQueueAsync(snapshot, cancellationToken);
            }
        }

        if (replaced is not null)
        {
            await repository.UpdateSnapshotStatusAsync(
                replaced.Persisted.SessionId,
                replaced.Persisted.Id,
                SnapshotProcessingStatus.Superseded,
                cancellationToken);
        }
    }

    private async Task ProcessQueueAsync(
        PendingSnapshot current,
        CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await ProcessOneAsync(current, cancellationToken);

                lock (_sync)
                {
                    if (_pending is null)
                    {
                        _processing = false;
                        return;
                    }

                    current = _pending;
                    _pending = null;
                }
            }
        }
        finally
        {
            lock (_sync)
            {
                _processing = false;
            }
        }
    }

    private async Task ProcessOneAsync(
        PendingSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var session = _session ??
            throw new InvalidOperationException("The monitoring session is unavailable.");
        var options = _options ??
            throw new InvalidOperationException("Monitoring options are unavailable.");
        var health = _health ??
            throw new InvalidOperationException("Monitoring health is unavailable.");

        try
        {
            var request = new PerceptionRequest(
                snapshot.Frame.Jpeg.AsMemory(),
                options.PerceptionOptions);
            var result = await perception.ObserveAsync(
                request,
                cancellationToken);
            if (result is PerceptionInvalid)
            {
                result = await perception.ObserveAsync(
                    request,
                    cancellationToken);
            }

            if (result is not PerceptionSuccess success)
            {
                await FinalizeStatusAsync(
                    snapshot.Persisted.Id,
                    SnapshotProcessingStatus.AgentError);
                health.ReportFailure(MonitoringTechnicalSource.Perception);
                return;
            }

            if (clock.UtcNow - snapshot.Frame.CapturedAtUtc >
                options.ObservationFreshnessLimit)
            {
                await FinalizeStatusAsync(
                    snapshot.Persisted.Id,
                    SnapshotProcessingStatus.Stale);
                health.ReportFailure(MonitoringTechnicalSource.Perception);
                return;
            }

            var observation = await repository.AddObservationAsync(
                new(
                    Guid.NewGuid(),
                    session.SessionId,
                    snapshot.Persisted.Id,
                    snapshot.Persisted.SessionVersion,
                    clock.UtcNow,
                    success.Proposal.SchemaVersion,
                    JsonSerializer.Serialize(success.Proposal, ObservationJsonOptions)),
                cancellationToken);
            await observationSink.PublishAsync(
                new(observation, success.Proposal),
                cancellationToken);
            health.ReportRecovery(MonitoringTechnicalSource.Perception);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await TryFinalizeAgentErrorAsync(snapshot.Persisted.Id);
            throw;
        }
        catch (Exception)
        {
            await TryFinalizeAgentErrorAsync(snapshot.Persisted.Id);
            health.ReportFailure(MonitoringTechnicalSource.Perception);
        }
    }

    private Task<SnapshotView> FinalizeStatusAsync(
        Guid snapshotId,
        SnapshotProcessingStatus status)
    {
        var session = _session ??
            throw new InvalidOperationException("The monitoring session is unavailable.");
        return repository.UpdateSnapshotStatusAsync(
            session.SessionId,
            snapshotId,
            status,
            CancellationToken.None);
    }

    private async Task TryFinalizeAgentErrorAsync(Guid snapshotId)
    {
        try
        {
            await FinalizeStatusAsync(snapshotId, SnapshotProcessingStatus.AgentError);
        }
        catch (Exception)
        {
            // The primary processing failure remains authoritative.
        }
    }

    private sealed record PendingSnapshot(
        CapturedJpegFrame Frame,
        SnapshotView Persisted);
}
