using GoalKeeper.Application.Recovery;

namespace GoalKeeper.Application.Tests;

public sealed class VoiceRecoveryContractTests
{
    [Fact]
    public void Audio_capture_defaults_are_bounded_for_short_mono_speech()
    {
        var options = new RecoveryAudioCaptureOptions();

        Assert.Equal(0, options.DeviceIndex);
        Assert.Equal(16_000, options.SampleRate);
        Assert.Equal(16, options.BitsPerSample);
        Assert.Equal(1, options.Channels);
        Assert.Equal(TimeSpan.FromSeconds(30), options.MaximumDuration);
        Assert.Equal(25 * 1024 * 1024, options.MaximumBytes);
        Assert.InRange(options.SilenceAmplitudeThreshold, 0, 1);
    }

    [Fact]
    public void Voice_success_rejects_an_unbounded_transcript()
    {
        var proposal = RecoveryTestData.Proposal(
            RecoveryOutcome.Recommit,
            RecoveryTestData.Request());

        Assert.Throws<ArgumentException>(() =>
            new VoiceRecoveryProposalResponse(
                new string('x', RecoveryLimits.MaximumTranscriptLength + 1),
                proposal));
    }

    [Fact]
    public void Opening_prompt_discloses_ai_voice_then_asks_why()
    {
        var request = RecoveryTestData.Request();

        var prompt = RecoveryOpeningPrompt.Create(request);

        Assert.Equal(
            $"This is an AI-generated voice. {request.Intervention.AccountabilityMessage} " +
            RecoveryOpeningPrompt.CheckInQuestion,
            prompt);
        Assert.EndsWith("why aren't you focused right now?", prompt);
        Assert.DoesNotContain("limited camera observations", prompt);
        Assert.DoesNotContain("This interpretation may be wrong", prompt);
        Assert.DoesNotContain(request.Intervention.DeviationDescription, prompt);
        Assert.DoesNotContain(request.Intervention.EvidenceSummary, prompt);
        Assert.DoesNotContain(request.Intervention.Rationale, prompt);
        Assert.True(prompt.Length <= RecoveryLimits.MaximumResponseLength);
    }

    [Fact]
    public void Opening_prompt_falls_back_without_speaking_provider_context()
    {
        var request = RecoveryTestData.Request();
        var longValue = new string('x', RecoveryLimits.MaximumSummaryLength);
        request = new RecoveryRequest(
            request.SessionId,
            request.SessionVersion,
            request.Contract,
            new RecoveryInterventionContext(
                request.Intervention.InterventionId,
                request.Intervention.ListedDeviationId,
                new string('d', RecoveryLimits.MaximumDescriptionLength),
                longValue,
                longValue,
                request.Intervention.AdmittedAtUtc),
            request.DisputedInterval,
            request.ActiveOverrides,
            request.AllowedOutcomes,
            request.CurrentCheckInTurns,
            request.CurrentTranscript,
            request.RequestedAtUtc,
            request.Options);

        var prompt = RecoveryOpeningPrompt.Create(request);

        Assert.StartsWith("This is an AI-generated voice.", prompt);
        Assert.DoesNotContain(longValue, prompt);
        Assert.DoesNotContain(request.Intervention.DeviationDescription, prompt);
        Assert.True(prompt.Length <= RecoveryLimits.MaximumResponseLength);
    }

    [Fact]
    public void Opening_prompt_rejects_unsafe_arbitrary_text()
    {
        Assert.Throws<ArgumentException>(() =>
            RecoveryOpeningPrompt.Create("You are stupid."));
    }
}
