using System.Diagnostics;
using GoalKeeper.Application.Perception;

namespace GoalKeeper.Application.Monitoring;

public sealed class PreflightOrchestrator(
    PreflightFrameAcquirer frameAcquirer,
    IPerceptionPort perception)
{
    private PreflightResult? _candidate;

    public async Task<PreflightResult> AcquireAsync(
        PreflightAcquisitionInput input,
        CameraAcquisitionOptions cameraOptions,
        PerceptionRequestOptions? perceptionOptions = null,
        CancellationToken cancellationToken = default)
    {
        _candidate = null;
        var totalStarted = Stopwatch.GetTimestamp();
        var cameraStarted = Stopwatch.GetTimestamp();
        var acquired = await frameAcquirer.AcquireAsync(input, cameraOptions, cancellationToken);
        var cameraElapsed = Stopwatch.GetElapsedTime(cameraStarted);
        if (acquired.Status == PreflightAcquisitionStatus.Cancelled)
        {
            return new(
                PreflightStatus.Cancelled,
                null,
                null,
                PreflightRejection.None,
                new(cameraElapsed, TimeSpan.Zero, Stopwatch.GetElapsedTime(totalStarted)));
        }

        var frame = acquired.Frame ??
            throw new InvalidOperationException("A successful preflight acquisition requires a frame.");

        var providerStarted = Stopwatch.GetTimestamp();
        PerceptionResult result;
        try
        {
            result = await perception.ObserveAsync(
                new PerceptionRequest(frame.Jpeg.AsMemory(), perceptionOptions),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new(
                PreflightStatus.TechnicalFailure,
                frame,
                null,
                PreflightRejection.PerceptionFailure,
                new(
                    cameraElapsed,
                    Stopwatch.GetElapsedTime(providerStarted),
                    Stopwatch.GetElapsedTime(totalStarted)));
        }

        var timing = new PreflightTiming(
            cameraElapsed,
            result.Metadata.Latency,
            Stopwatch.GetElapsedTime(totalStarted));

        if (result is PerceptionInvalid)
        {
            return new(
                PreflightStatus.TechnicalFailure,
                frame,
                null,
                PreflightRejection.PerceptionInvalid,
                timing);
        }

        if (result is PerceptionFailure)
        {
            return new(
                PreflightStatus.TechnicalFailure,
                frame,
                null,
                PreflightRejection.PerceptionFailure,
                timing);
        }

        var observation = ((PerceptionSuccess)result).Proposal;
        if (observation.ImageQuality.Value != ImageQualityValue.Adequate)
        {
            return new(
                PreflightStatus.Rejected,
                frame,
                observation,
                PreflightRejection.ImageQuality,
                timing);
        }

        if (observation.PeopleCount.Status != PeopleCountStatus.Counted ||
            observation.PeopleCount.Value != 1)
        {
            return new(
                PreflightStatus.Rejected,
                frame,
                observation,
                PreflightRejection.PeopleCount,
                timing);
        }

        _candidate = new(
            PreflightStatus.AwaitingConfirmation,
            frame,
            observation,
            PreflightRejection.None,
            timing);
        return _candidate;
    }

    public PreflightResult Confirm(bool cameraViewIsCorrect)
    {
        if (_candidate is null ||
            _candidate.Status != PreflightStatus.AwaitingConfirmation)
        {
            throw new InvalidOperationException(
                "A successful camera and Perception candidate is required before confirmation.");
        }

        var candidate = _candidate;
        _candidate = null;
        return cameraViewIsCorrect
            ? candidate with { Status = PreflightStatus.Passed }
            : candidate with
            {
                Status = PreflightStatus.Rejected,
                Rejection = PreflightRejection.UserRejected
            };
    }

    public PreflightResult Cancel()
    {
        _candidate = null;
        return new(PreflightStatus.Cancelled, null, null, PreflightRejection.None);
    }
}
