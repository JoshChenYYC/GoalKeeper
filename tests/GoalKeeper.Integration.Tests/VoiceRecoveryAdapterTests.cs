using GoalKeeper.Application.Recovery;
using GoalKeeper.Domain;
using GoalKeeper.Infrastructure.Recovery.Audio;

namespace GoalKeeper.Integration.Tests;

public sealed class VoiceRecoveryAdapterTests
{
    [Fact]
    public async Task One_turn_runs_in_order_and_disposes_audio_before_conversation()
    {
        var order = new List<string>();
        var audio = new TrackingAudio(order);
        var microphone = new StubMicrophone(order, audio);
        var speechInput = new StubSpeechInput(
            order,
            "I need a smaller next step.");
        var speechOutput = new StubSpeechOutput(order);
        var conversation = new CallbackRecoveryPort(request =>
        {
            order.Add("conversation");
            Assert.True(audio.IsDisposed);
            return new RecoveryProposalResponse(
                Proposal(
                    request,
                    RecoveryOutcome.UnclearResponse,
                    "Could you choose one small next action?"));
        });
        var adapter = Adapter(
            microphone,
            speechInput,
            speechOutput,
            conversation);

        var result = Assert.IsType<VoiceRecoveryProposalResponse>(
            await adapter.ProposeAsync(Request()));

        Assert.Equal("I need a smaller next step.", result.CapturedTranscript);
        Assert.Equal(
            [
                "speak:opening",
                "cue",
                "capture",
                "transcribe",
                "audio.dispose",
                "conversation",
                "speak:assistant"
            ],
            order);
        Assert.Equal(1, audio.DisposeCount);
        Assert.Equal(1, conversation.CallCount);
    }

    [Fact]
    public async Task Silence_returns_local_no_response_without_provider_calls()
    {
        var order = new List<string>();
        var microphone = new StubMicrophone(order, null);
        var speechInput = new StubSpeechInput(order, "not used");
        var speechOutput = new StubSpeechOutput(order);
        var conversation = new CallbackRecoveryPort(
            _ => throw new InvalidOperationException("Conversation was not expected."));
        var adapter = Adapter(
            microphone,
            speechInput,
            speechOutput,
            conversation);

        var result = Assert.IsType<VoiceRecoveryProposalResponse>(
            await adapter.ProposeAsync(Request()));

        Assert.Null(result.CapturedTranscript);
        Assert.Equal(RecoveryOutcome.NoResponse, result.Proposal.Outcome);
        Assert.Null(result.Proposal.Transcript);
        Assert.Equal(["speak:opening", "cue", "capture"], order);
        Assert.Equal(0, speechInput.CallCount);
        Assert.Equal(0, conversation.CallCount);
    }

    [Fact]
    public async Task Invalid_inner_proposal_is_rejected_without_assistant_playback()
    {
        var order = new List<string>();
        var audio = new TrackingAudio(order);
        var speechOutput = new StubSpeechOutput(order);
        var conversation = new CallbackRecoveryPort(request =>
            new RecoveryProposalResponse(
                Proposal(
                    request,
                    RecoveryOutcome.UnclearResponse,
                    "Please clarify.") with
                {
                    SessionVersion = request.SessionVersion + 1
                }));
        var adapter = Adapter(
            new StubMicrophone(order, audio),
            new StubSpeechInput(order, "I am unsure."),
            speechOutput,
            conversation);

        var result = Assert.IsType<VoiceRecoveryFailureResponse>(
            await adapter.ProposeAsync(Request()));

        Assert.Equal(VoiceRecoveryStage.Conversation, result.Stage);
        Assert.Equal(
            RecoveryFailureCategory.InvalidResponse,
            result.Category);
        Assert.DoesNotContain("speak:assistant", order);
        Assert.Equal(1, audio.DisposeCount);
    }

    [Fact]
    public async Task Transcription_failure_disposes_audio_and_returns_typed_stage()
    {
        const string canary = "PRIVATE_TRANSCRIPT_CANARY";
        var order = new List<string>();
        var audio = new TrackingAudio(order);
        var adapter = Adapter(
            new StubMicrophone(order, audio),
            new ThrowingSpeechInput(
                order,
                new RecoveryVoiceException(
                    RecoveryFailureCategory.InvalidResponse,
                    VoiceRecoveryStage.Transcription,
                    canary)),
            new StubSpeechOutput(order),
            new CallbackRecoveryPort(
                _ => throw new InvalidOperationException("Not expected.")));

        var result = Assert.IsType<VoiceRecoveryFailureResponse>(
            await adapter.ProposeAsync(Request()));

        Assert.Equal(VoiceRecoveryStage.Transcription, result.Stage);
        Assert.Equal(
            RecoveryFailureCategory.InvalidResponse,
            result.Category);
        Assert.Equal(1, audio.DisposeCount);
        Assert.DoesNotContain(canary, result.ToString());
    }

    [Fact]
    public async Task Caller_cancellation_during_transcription_is_rethrown_after_disposal()
    {
        var order = new List<string>();
        var audio = new TrackingAudio(order);
        using var cancellation = new CancellationTokenSource();
        var adapter = Adapter(
            new StubMicrophone(order, audio),
            new CancellingSpeechInput(order, cancellation),
            new StubSpeechOutput(order),
            new CallbackRecoveryPort(
                _ => throw new InvalidOperationException("Not expected.")));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => adapter.ProposeAsync(Request(), cancellation.Token));

        Assert.Equal(1, audio.DisposeCount);
    }

    [Fact]
    public async Task Cue_failure_prevents_microphone_activation()
    {
        var order = new List<string>();
        var microphone = new StubMicrophone(order, null);
        var speechOutput = new StubSpeechOutput(
            order,
            cueFailure: new RecoveryVoiceException(
                RecoveryFailureCategory.ProviderUnavailable,
                VoiceRecoveryStage.Cue));
        var adapter = Adapter(
            microphone,
            new StubSpeechInput(order, "not used"),
            speechOutput,
            new CallbackRecoveryPort(
                _ => throw new InvalidOperationException("Not expected.")));

        var result = Assert.IsType<VoiceRecoveryFailureResponse>(
            await adapter.ProposeAsync(Request()));

        Assert.Equal(VoiceRecoveryStage.Cue, result.Stage);
        Assert.Equal(0, microphone.CallCount);
        Assert.Equal(["speak:opening", "cue"], order);
    }

    [Fact]
    public async Task Opening_failure_is_reported_as_opening_and_never_activates_microphone()
    {
        var order = new List<string>();
        var microphone = new StubMicrophone(order, null);
        var speechOutput = new StubSpeechOutput(
            order,
            speakFailure: new RecoveryVoiceException(
                RecoveryFailureCategory.ProviderUnavailable,
                VoiceRecoveryStage.Playback));
        var adapter = Adapter(
            microphone,
            new StubSpeechInput(order, "not used"),
            speechOutput,
            new CallbackRecoveryPort(
                _ => throw new InvalidOperationException("Not expected.")));

        var result = Assert.IsType<VoiceRecoveryFailureResponse>(
            await adapter.ProposeAsync(Request()));

        Assert.Equal(VoiceRecoveryStage.Opening, result.Stage);
        Assert.Equal(0, microphone.CallCount);
        Assert.Equal(["speak:opening"], order);
    }

    private static VoiceRecoveryAdapter Adapter(
        IMicrophonePort microphone,
        ISpeechInputPort speechInput,
        ISpeechOutputPort speechOutput,
        IRecoveryPort conversation) =>
        new(
            microphone,
            speechInput,
            speechOutput,
            conversation,
            new FakeClock());

    private static RecoveryRequest Request()
    {
        var requestedAt = new DateTimeOffset(
            2026,
            7,
            18,
            18,
            0,
            0,
            TimeSpan.Zero);
        return new(
            Guid.Parse("10000000-0000-0000-0000-000000000001"),
            4,
            new(
                Guid.Parse("20000000-0000-0000-0000-000000000001"),
                Guid.Parse("30000000-0000-0000-0000-000000000001"),
                "Write the report",
                "Finish the next section.",
                TimeSpan.FromMinutes(45),
                ReasoningMode.ProfileOnly,
                Sensitivity.Balanced),
            new(
                Guid.Parse("40000000-0000-0000-0000-000000000001"),
                null,
                "unrelated browsing",
                "The observed pattern changed away from the report.",
                "The activity may not support the stated writing goal.",
                requestedAt.AddMinutes(-2)),
            new(TimeSpan.FromMinutes(8), TimeSpan.FromMinutes(10)),
            [],
            Enum.GetValues<RecoveryOutcome>(),
            [],
            null,
            requestedAt);
    }

    private static RecoveryProposal Proposal(
        RecoveryRequest request,
        RecoveryOutcome outcome,
        string? assistantMessage) =>
        new(
            request.SessionId,
            request.SessionVersion,
            request.Intervention.InterventionId,
            request.NextTurnNumber,
            outcome,
            request.CurrentTranscript,
            null,
            assistantMessage,
            false,
            new(
                request.RequestedAtUtc,
                request.RequestedAtUtc.AddMilliseconds(50)),
            new(
                "openai",
                "gpt-5.6-terra",
                "recovery-v1",
                request.Options.SchemaVersion,
                TimeSpan.FromMilliseconds(50),
                "req_0123456789abcdef0123456789abcdef"));

    private sealed class TrackingAudio(List<string> order) : ITransientAudio
    {
        public long Length => 48;

        public string ContentType => InMemoryTransientAudio.WaveContentType;

        public int DisposeCount { get; private set; }

        public bool IsDisposed => DisposeCount != 0;

        public ValueTask<Stream> OpenReadAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<Stream>(
                new MemoryStream([1, 2, 3, 4], writable: false));

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            order.Add("audio.dispose");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StubMicrophone(
        List<string> order,
        ITransientAudio? audio) : IMicrophonePort
    {
        public int CallCount { get; private set; }

        public Task<ITransientAudio?> CaptureAsync(
            RecoveryAudioCaptureOptions options,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            order.Add("capture");
            return Task.FromResult(audio);
        }
    }

    private sealed class StubSpeechInput(
        List<string> order,
        string transcript) : ISpeechInputPort
    {
        public int CallCount { get; private set; }

        public Task<string> TranscribeAsync(
            ITransientAudio audio,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            order.Add("transcribe");
            return Task.FromResult(transcript);
        }
    }

    private sealed class ThrowingSpeechInput(
        List<string> order,
        Exception exception) : ISpeechInputPort
    {
        public Task<string> TranscribeAsync(
            ITransientAudio audio,
            CancellationToken cancellationToken = default)
        {
            order.Add("transcribe");
            return Task.FromException<string>(exception);
        }
    }

    private sealed class CancellingSpeechInput(
        List<string> order,
        CancellationTokenSource cancellation) : ISpeechInputPort
    {
        public Task<string> TranscribeAsync(
            ITransientAudio audio,
            CancellationToken cancellationToken = default)
        {
            order.Add("transcribe");
            cancellation.Cancel();
            return Task.FromCanceled<string>(cancellationToken);
        }
    }

    private sealed class StubSpeechOutput(
        List<string> order,
        Exception? cueFailure = null,
        Exception? speakFailure = null) : ISpeechOutputPort
    {
        private int _speakCount;

        public Task SpeakAsync(
            string text,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            order.Add(_speakCount++ == 0
                ? "speak:opening"
                : "speak:assistant");
            return speakFailure is null
                ? Task.CompletedTask
                : Task.FromException(speakFailure);
        }

        public Task PlayListeningCueAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            order.Add("cue");
            return cueFailure is null
                ? Task.CompletedTask
                : Task.FromException(cueFailure);
        }
    }

    private sealed class CallbackRecoveryPort(
        Func<RecoveryRequest, RecoveryPortResult> callback) : IRecoveryPort
    {
        public int CallCount { get; private set; }

        public Task<RecoveryPortResult> ProposeAsync(
            RecoveryRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(callback(request));
        }
    }

    private sealed class FakeClock : IClock
    {
        public TimeSpan MonotonicNow => TimeSpan.FromMinutes(10);

        public DateTimeOffset UtcNow =>
            new(2026, 7, 18, 18, 0, 1, TimeSpan.Zero);
    }
}
