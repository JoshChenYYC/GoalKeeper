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
        var acquired = await frameAcquirer.AcquireAsync(input, cameraOptions, cancellationToken);
        if (acquired.Status == PreflightAcquisitionStatus.Cancelled)
        {
            return new(PreflightStatus.Cancelled, null, null, PreflightRejection.None);
        }

        var frame = acquired.Frame ??
            throw new InvalidOperationException("A successful preflight acquisition requires a frame.");

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
                PreflightRejection.PerceptionFailure);
        }

        if (result is PerceptionInvalid)
        {
            return new(
                PreflightStatus.TechnicalFailure,
                frame,
                null,
                PreflightRejection.PerceptionInvalid);
        }

        if (result is PerceptionFailure)
        {
            return new(
                PreflightStatus.TechnicalFailure,
                frame,
                null,
                PreflightRejection.PerceptionFailure);
        }

        var observation = ((PerceptionSuccess)result).Proposal;
        if (observation.ImageQuality.Value != ImageQualityValue.Adequate)
        {
            return new(
                PreflightStatus.Rejected,
                frame,
                observation,
                PreflightRejection.ImageQuality);
        }

        if (observation.PeopleCount.Status != PeopleCountStatus.Counted ||
            observation.PeopleCount.Value != 1)
        {
            return new(
                PreflightStatus.Rejected,
                frame,
                observation,
                PreflightRejection.PeopleCount);
        }

        _candidate = new(
            PreflightStatus.AwaitingConfirmation,
            frame,
            observation,
            PreflightRejection.None);
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
