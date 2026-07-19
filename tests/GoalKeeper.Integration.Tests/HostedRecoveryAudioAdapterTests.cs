using System.Net;
using System.Text;
using GoalKeeper.Application.Recovery;
using GoalKeeper.Infrastructure.Recovery.Audio;
using Microsoft.Extensions.Options;

namespace GoalKeeper.Integration.Tests;

public sealed class HostedRecoveryAudioAdapterTests
{
    [Fact]
    public async Task Transient_wave_audio_zeroes_its_owned_buffer_on_dispose()
    {
        var audio = InMemoryTransientAudio.FromPcm16(
            [0x10, 0x20, 0x30, 0x40],
            16_000,
            1);
        await using var stream = await audio.OpenReadAsync();

        await audio.DisposeAsync();
        stream.Position = 0;
        var bytes = new byte[48];
        var read = await stream.ReadAsync(bytes);

        Assert.Equal(bytes.Length, read);
        Assert.All(bytes, value => Assert.Equal(0, value));
        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await audio.OpenReadAsync());
    }

    [Fact]
    public async Task Microphone_stops_and_releases_native_device_exactly_once()
    {
        var device = new FakeCaptureDevice([0xff, 0x7f, 0x00, 0x00]);
        var port = new NAudioMicrophonePort(
            new FakeCaptureDeviceFactory(device));

        await using var audio = Assert.IsAssignableFrom<ITransientAudio>(
            await port.CaptureAsync(
                new RecoveryAudioCaptureOptions(
                    maximumBytes: 48,
                    silenceAmplitudeThreshold: 0.01f)));

        Assert.Equal(48, audio.Length);
        Assert.Equal(1, device.StartCount);
        Assert.Equal(1, device.StopCount);
        Assert.Equal(1, device.DisposeCount);
    }

    [Fact]
    public async Task Microphone_returns_null_for_silence_and_releases_device()
    {
        var device = new FakeCaptureDevice([0x00, 0x00, 0x00, 0x00]);
        var port = new NAudioMicrophonePort(
            new FakeCaptureDeviceFactory(device));

        var audio = await port.CaptureAsync(
            new RecoveryAudioCaptureOptions(maximumBytes: 48));

        Assert.Null(audio);
        Assert.Equal(1, device.StopCount);
        Assert.Equal(1, device.DisposeCount);
    }

    [Fact]
    public async Task Microphone_cancellation_releases_device_once()
    {
        var device = new FakeCaptureDevice();
        var port = new NAudioMicrophonePort(
            new FakeCaptureDeviceFactory(device));
        using var cancellation = new CancellationTokenSource();

        var capture = port.CaptureAsync(
            new RecoveryAudioCaptureOptions(),
            cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => capture);
        Assert.Equal(1, device.StopCount);
        Assert.Equal(1, device.DisposeCount);
    }

    [Fact]
    public async Task Microphone_duration_boundary_releases_device_once()
    {
        var device = new FakeCaptureDevice();
        var port = new NAudioMicrophonePort(
            new FakeCaptureDeviceFactory(device));

        var audio = await port.CaptureAsync(
            new RecoveryAudioCaptureOptions(
                maximumDuration: TimeSpan.FromMilliseconds(10)));

        Assert.Null(audio);
        Assert.Equal(1, device.StopCount);
        Assert.Equal(1, device.DisposeCount);
    }

    [Fact]
    public async Task Microphone_start_failure_releases_device_once()
    {
        var device = new FakeCaptureDevice(
            startFailure: new InvalidOperationException("native failure"));
        var port = new NAudioMicrophonePort(
            new FakeCaptureDeviceFactory(device));

        var exception = await Assert.ThrowsAsync<RecoveryVoiceException>(
            () => port.CaptureAsync(new RecoveryAudioCaptureOptions()));

        Assert.Equal(VoiceRecoveryStage.Capture, exception.Stage);
        Assert.Equal(
            RecoveryFailureCategory.ProviderUnavailable,
            exception.Category);
        Assert.Equal(1, device.StopCount);
        Assert.Equal(1, device.DisposeCount);
    }

    [Fact]
    public async Task Transcription_uses_bounded_constant_filename_multipart()
    {
        var handler = new RecordingHandler(
            _ => Json(
                HttpStatusCode.OK,
                """{"text":"I will return to the task."}"""));
        var adapter = CreateSpeechInput(handler);
        await using var audio = InMemoryTransientAudio.FromPcm16(
            [0x01, 0x00],
            16_000,
            1);

        var transcript = await adapter.TranscribeAsync(audio);

        Assert.Equal("I will return to the task.", transcript);
        Assert.Single(handler.RequestBodies);
        var body = Encoding.Latin1.GetString(handler.RequestBodies[0]);
        Assert.Contains("name=file; filename=checkin.wav", body);
        Assert.Contains("gpt-4o-transcribe", body);
        Assert.DoesNotContain("session_id", body);
        Assert.DoesNotContain("sk-test-secret", body);
    }

    [Fact]
    public async Task Hostile_transcription_is_rejected_without_echoing_canary()
    {
        const string canary = "STT_PRIVATE_CANARY";
        var handler = new RecordingHandler(
            _ => Json(
                HttpStatusCode.OK,
                $$"""{"text":"{{canary}}\nsecond line"}"""));
        var adapter = CreateSpeechInput(handler);
        await using var audio = InMemoryTransientAudio.FromPcm16(
            [0x01, 0x00],
            16_000,
            1);

        var exception = await Assert.ThrowsAsync<RecoveryVoiceException>(
            () => adapter.TranscribeAsync(audio));

        Assert.Equal(VoiceRecoveryStage.Transcription, exception.Stage);
        Assert.Equal(
            RecoveryFailureCategory.InvalidResponse,
            exception.Category);
        Assert.DoesNotContain(canary, exception.ToString());
    }

    [Fact]
    public async Task Oversized_chunked_transcription_response_is_rejected()
    {
        var handler = new RecordingHandler(
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new UnknownLengthContent(
                    Encoding.UTF8.GetBytes(
                        """{"text":""" +
                        new string('X', 70_000) +
                        """}"""))
            });
        var adapter = CreateSpeechInput(handler);
        await using var audio = InMemoryTransientAudio.FromPcm16(
            [0x01, 0x00],
            16_000,
            1);

        var exception = await Assert.ThrowsAsync<RecoveryVoiceException>(
            () => adapter.TranscribeAsync(audio));

        Assert.Equal(
            RecoveryFailureCategory.InvalidResponse,
            exception.Category);
    }

    [Fact]
    public async Task Transcription_bounds_the_stream_not_only_declared_length()
    {
        var handler = new RecordingHandler(
            _ => Json(HttpStatusCode.OK, """{"text":"not reached"}"""));
        var adapter = CreateSpeechInput(
            handler,
            options => options.MaximumAudioRequestBytes = 4);
        await using var audio = new MisreportedAudio(
            declaredLength: 2,
            new byte[8]);

        var exception = await Assert.ThrowsAsync<RecoveryVoiceException>(
            () => adapter.TranscribeAsync(audio));

        Assert.Equal(
            RecoveryFailureCategory.InvalidResponse,
            exception.Category);
    }

    [Fact]
    public async Task Speech_uses_tts_1_coral_pcm_and_streams_to_sink()
    {
        var handler = new RecordingHandler(
            _ => PcmResponse([1, 2, 3, 4]));
        var playback = new RecordingPlaybackSink();
        var adapter = CreateSpeechOutput(handler, playback);

        await adapter.SpeakAsync("A short safe response.");

        Assert.Equal([1, 2, 3, 4], playback.PcmBytes);
        Assert.Equal(new RecoveryPcmFormat(24_000, 16, 1), playback.Format);
        var request = Encoding.UTF8.GetString(
            Assert.Single(handler.RequestBodies));
        Assert.Contains("\"model\":\"tts-1\"", request);
        Assert.Contains("\"voice\":\"coral\"", request);
        Assert.Contains("\"response_format\":\"pcm\"", request);
        Assert.DoesNotContain("sk-test-secret", request);
    }

    [Theory]
    [InlineData("", "audio/pcm")]
    [InlineData("x", "audio/pcm")]
    [InlineData("{}", "application/json")]
    public async Task Invalid_speech_bodies_never_reach_playback(
        string body,
        string contentType)
    {
        var handler = new RecordingHandler(
            _ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(
                        Encoding.UTF8.GetBytes(body))
                };
                response.Content.Headers.ContentType = new(contentType);
                return response;
            });
        var playback = new RecordingPlaybackSink();
        var adapter = CreateSpeechOutput(handler, playback);

        var exception = await Assert.ThrowsAsync<RecoveryVoiceException>(
            () => adapter.SpeakAsync("A short safe response."));

        Assert.Equal(
            RecoveryFailureCategory.InvalidResponse,
            exception.Category);
        Assert.Empty(playback.PcmBytes);
    }

    [Fact]
    public async Task Speech_stream_read_obeys_the_total_timeout()
    {
        var content = new StreamContent(new StallingStream());
        content.Headers.ContentType = new("audio/pcm");
        var handler = new RecordingHandler(
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content
            });
        var playback = new RecordingPlaybackSink();
        var adapter = CreateSpeechOutput(
            handler,
            playback,
            options => options.RequestTimeout =
                TimeSpan.FromMilliseconds(20));

        var exception = await Assert.ThrowsAsync<RecoveryVoiceException>(
            () => adapter.SpeakAsync("A short safe response."));

        Assert.Equal(RecoveryFailureCategory.Timeout, exception.Category);
        Assert.Empty(playback.PcmBytes);
    }

    [Fact]
    public async Task Listening_cue_never_creates_provider_request()
    {
        var handler = new RecordingHandler(
            _ => throw new InvalidOperationException("HTTP was not expected."));
        var playback = new RecordingPlaybackSink();
        var adapter = CreateSpeechOutput(handler, playback);

        await adapter.PlayListeningCueAsync();

        Assert.Equal(1, playback.CueCount);
        Assert.Empty(handler.RequestBodies);
    }

    [Theory]
    [InlineData("https://user:password@provider.invalid/v1")]
    [InlineData("https://provider.invalid/v1?tenant=unsafe")]
    [InlineData("https://provider.invalid/v1#unsafe")]
    public void Audio_adapter_rejects_unsafe_base_urls(string baseUrl)
    {
        var options = AudioOptions();
        options.BaseUrl = new Uri(baseUrl);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new OpenAiSpeechInputAdapter(
                new TestHttpClientFactory(new RecordingHandler(
                    _ => throw new InvalidOperationException())),
                Microsoft.Extensions.Options.Options.Create(options)));

        Assert.DoesNotContain(
            "sk-test-secret",
            exception.Message,
            StringComparison.Ordinal);
    }

    private static OpenAiSpeechInputAdapter CreateSpeechInput(
        HttpMessageHandler handler,
        Action<OpenAiRecoveryAudioOptions>? configure = null)
    {
        var options = AudioOptions();
        configure?.Invoke(options);
        return new(
            new TestHttpClientFactory(handler),
            Microsoft.Extensions.Options.Options.Create(options));
    }

    private static OpenAiSpeechOutputAdapter CreateSpeechOutput(
        HttpMessageHandler handler,
        IRecoveryAudioPlaybackSink playback,
        Action<OpenAiRecoveryAudioOptions>? configure = null)
    {
        var options = AudioOptions();
        configure?.Invoke(options);
        return new(
            new TestHttpClientFactory(handler),
            playback,
            Microsoft.Extensions.Options.Options.Create(options));
    }

    private static OpenAiRecoveryAudioOptions AudioOptions() =>
        new()
        {
            ApiKey = "sk-test-secret",
            BaseUrl = new("https://provider.invalid/v1")
        };

    private static HttpResponseMessage Json(
        HttpStatusCode statusCode,
        string json) =>
        new(statusCode)
        {
            Content = new StringContent(
                json,
                Encoding.UTF8,
                "application/json")
        };

    private static HttpResponseMessage PcmResponse(byte[] pcm)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(pcm)
        };
        response.Content.Headers.ContentType = new("audio/pcm");
        return response;
    }

    private sealed class FakeCaptureDeviceFactory(
        FakeCaptureDevice device) : IRecoveryAudioCaptureDeviceFactory
    {
        public IRecoveryAudioCaptureDevice Create(
            int deviceNumber,
            RecoveryPcmFormat format,
            int bufferMilliseconds) =>
            device;
    }

    private sealed class MisreportedAudio(
        long declaredLength,
        byte[] content) : ITransientAudio
    {
        public long Length => declaredLength;

        public string ContentType => InMemoryTransientAudio.WaveContentType;

        public ValueTask<Stream> OpenReadAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<Stream>(
                new MemoryStream(content, writable: false));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeCaptureDevice(
        byte[]? data = null,
        Exception? startFailure = null) :
        IRecoveryAudioCaptureDevice
    {
        private bool _stopped;

        public event EventHandler<RecoveryAudioDataAvailableEventArgs>?
            DataAvailable;

        public event EventHandler<RecoveryAudioCaptureStoppedEventArgs>?
            RecordingStopped;

        public int StartCount { get; private set; }

        public int StopCount { get; private set; }

        public int DisposeCount { get; private set; }

        public void Start()
        {
            StartCount++;
            if (startFailure is not null)
            {
                throw startFailure;
            }

            if (data is not null)
            {
                DataAvailable?.Invoke(
                    this,
                    new(data, data.Length));
            }
        }

        public void StopRecording()
        {
            if (_stopped)
            {
                return;
            }

            _stopped = true;
            StopCount++;
            RecordingStopped?.Invoke(this, new(null));
        }

        public void Dispose() => DisposeCount++;
    }

    private sealed class TestHttpClientFactory(
        HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false);
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) :
        HttpMessageHandler
    {
        public List<byte[]> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBodies.Add(
                request.Content is null
                    ? []
                    : await request.Content.ReadAsByteArrayAsync(
                        cancellationToken));
            return responseFactory(request);
        }
    }

    private sealed class UnknownLengthContent(byte[] content) : HttpContent
    {
        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context) =>
            stream.WriteAsync(content).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class StallingStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingPlaybackSink :
        IRecoveryAudioPlaybackSink
    {
        public byte[] PcmBytes { get; private set; } = [];

        public RecoveryPcmFormat? Format { get; private set; }

        public int CueCount { get; private set; }

        public async Task PlayPcmAsync(
            Stream pcm,
            RecoveryPcmFormat format,
            CancellationToken cancellationToken = default)
        {
            using var destination = new MemoryStream();
            await pcm.CopyToAsync(destination, cancellationToken);
            PcmBytes = destination.ToArray();
            Format = format;
        }

        public Task PlayCueAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CueCount++;
            return Task.CompletedTask;
        }
    }
}
