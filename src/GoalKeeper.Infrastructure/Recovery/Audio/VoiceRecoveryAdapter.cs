using GoalKeeper.Application.Recovery;
using GoalKeeper.Domain;

namespace GoalKeeper.Infrastructure.Recovery.Audio;

public sealed class VoiceRecoveryAdapter(
    IMicrophonePort microphone,
    ISpeechInputPort speechInput,
    ISpeechOutputPort speechOutput,
    IRecoveryPort conversation,
    IClock clock,
    RecoveryAudioCaptureOptions? captureOptions = null) :
    IVoiceRecoveryPort
{
    private readonly RecoveryAudioCaptureOptions _captureOptions =
        captureOptions ?? new();

    public async Task<VoiceRecoveryPortResult> ProposeAsync(
        RecoveryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var stage = VoiceRecoveryStage.Opening;
        var startedAtUtc = Later(clock.UtcNow, request.RequestedAtUtc);
        var startedAtMonotonic = clock.MonotonicNow;

        try
        {
            await speechOutput.SpeakAsync(
                    RecoveryOpeningPrompt.Create(request),
                    cancellationToken)
                .ConfigureAwait(false);

            stage = VoiceRecoveryStage.Cue;
            await speechOutput.PlayListeningCueAsync(cancellationToken)
                .ConfigureAwait(false);

            stage = VoiceRecoveryStage.Capture;
            var audio = await microphone.CaptureAsync(
                    _captureOptions,
                    cancellationToken)
                .ConfigureAwait(false);
            if (audio is null)
            {
                return LocalNoResponse(
                    request,
                    startedAtUtc,
                    startedAtMonotonic);
            }

            string transcript;
            stage = VoiceRecoveryStage.Transcription;
            await using (audio.ConfigureAwait(false))
            {
                transcript = await speechInput.TranscribeAsync(
                        audio,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            transcript = CanonicalTranscript(transcript);
            var frozenRequest = WithTranscript(request, transcript);

            stage = VoiceRecoveryStage.Conversation;
            var conversationResult = await conversation.ProposeAsync(
                    frozenRequest,
                    cancellationToken)
                .ConfigureAwait(false);
            if (conversationResult is RecoveryFailureResponse failure)
            {
                return new VoiceRecoveryFailureResponse(
                    stage,
                    failure.Category,
                    failure.Metadata);
            }

            if (conversationResult is not RecoveryProposalResponse response ||
                RecoveryProposalValidator.Validate(
                    frozenRequest,
                    response.Proposal) is not ValidRecoveryProposal)
            {
                return new VoiceRecoveryFailureResponse(
                    stage,
                    RecoveryFailureCategory.InvalidResponse);
            }

            if (response.Proposal.AssistantMessage is { } assistantMessage)
            {
                stage = VoiceRecoveryStage.Playback;
                await speechOutput.SpeakAsync(
                        assistantMessage,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return new VoiceRecoveryProposalResponse(
                transcript,
                response.Proposal);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (RecoveryVoiceException exception)
        {
            return new VoiceRecoveryFailureResponse(
                stage == VoiceRecoveryStage.Opening
                    ? VoiceRecoveryStage.Opening
                    : exception.Stage,
                exception.Category);
        }
        catch
        {
            return new VoiceRecoveryFailureResponse(
                stage,
                RecoveryFailureCategory.Unknown);
        }
    }

    private VoiceRecoveryPortResult LocalNoResponse(
        RecoveryRequest request,
        DateTimeOffset startedAtUtc,
        TimeSpan startedAtMonotonic)
    {
        var completedAtUtc = Later(clock.UtcNow, startedAtUtc);
        var latency = clock.MonotonicNow - startedAtMonotonic;
        if (latency < TimeSpan.Zero)
        {
            latency = TimeSpan.Zero;
        }

        var frozenRequest = WithTranscript(request, null);
        var proposal = new RecoveryProposal(
            request.SessionId,
            request.SessionVersion,
            request.Intervention.InterventionId,
            request.NextTurnNumber,
            RecoveryOutcome.NoResponse,
            null,
            null,
            null,
            false,
            new(startedAtUtc, completedAtUtc),
            new(
                "local",
                "silence-detector-v1",
                "voice-recovery-v1",
                request.Options.SchemaVersion,
                latency,
                $"local_{Guid.NewGuid():N}"));
        if (RecoveryProposalValidator.Validate(
                frozenRequest,
                proposal) is not ValidRecoveryProposal)
        {
            return new VoiceRecoveryFailureResponse(
                VoiceRecoveryStage.Conversation,
                RecoveryFailureCategory.InvalidResponse);
        }

        return new VoiceRecoveryProposalResponse(null, proposal);
    }

    private static RecoveryRequest WithTranscript(
        RecoveryRequest request,
        string? transcript) =>
        new(
            request.SessionId,
            request.SessionVersion,
            request.Contract,
            request.Intervention,
            request.DisputedInterval,
            request.ActiveOverrides,
            request.AllowedOutcomes,
            request.CurrentCheckInTurns,
            transcript,
            request.RequestedAtUtc,
            request.Options);

    private static string CanonicalTranscript(string transcript)
    {
        var canonical = transcript?.Trim();
        if (string.IsNullOrWhiteSpace(canonical) ||
            canonical.Length > RecoveryLimits.MaximumTranscriptLength ||
            canonical.Any(char.IsControl))
        {
            throw new RecoveryVoiceException(
                RecoveryFailureCategory.InvalidResponse,
                VoiceRecoveryStage.Transcription,
                "The transcript is empty, unsafe, or exceeds its bound.");
        }

        return canonical;
    }

    private static DateTimeOffset Later(
        DateTimeOffset first,
        DateTimeOffset second) =>
        first >= second ? first : second;
}
