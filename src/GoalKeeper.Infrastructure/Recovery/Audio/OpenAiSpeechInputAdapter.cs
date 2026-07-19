using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using GoalKeeper.Application.Recovery;
using Microsoft.Extensions.Options;

namespace GoalKeeper.Infrastructure.Recovery.Audio;

public sealed class OpenAiSpeechInputAdapter : ISpeechInputPort
{
    public const string HttpClientName =
        "GoalKeeper.OpenAI.Recovery.Transcription";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly OpenAiRecoveryAudioOptions _options;
    private readonly Uri _endpoint;

    public OpenAiSpeechInputAdapter(
        IHttpClientFactory httpClientFactory,
        IOptions<OpenAiRecoveryAudioOptions> options)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _options.Validate();
        _httpClientFactory = httpClientFactory;
        _endpoint = new(
            $"{_options.BaseUrl.AbsoluteUri.TrimEnd('/')}/audio/transcriptions",
            UriKind.Absolute);
    }

    public async Task<string> TranscribeAsync(
        ITransientAudio audio,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audio);
        cancellationToken.ThrowIfCancellationRequested();
        if (audio.Length is <= 0 ||
            audio.Length > _options.MaximumAudioRequestBytes ||
            !string.Equals(
                audio.ContentType,
                InMemoryTransientAudio.WaveContentType,
                StringComparison.Ordinal))
        {
            throw Failure(
                RecoveryFailureCategory.InvalidResponse,
                "The captured audio is invalid or exceeds its bound.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(_options.RequestTimeout);
        using var httpClient = _httpClientFactory.CreateClient(HttpClientName);

        try
        {
            await using var audioStream = await audio.OpenReadAsync(timeout.Token)
                .ConfigureAwait(false);
            await using var boundedAudio = new BoundedReadStream(
                audioStream,
                _options.MaximumAudioRequestBytes,
                () => Failure(
                    RecoveryFailureCategory.InvalidResponse,
                    "The captured audio stream exceeds its byte limit."));
            using var request = CreateRequest(boundedAudio);
            using var response = await httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw Failure(
                    Categorize(response.StatusCode),
                    "The transcription provider rejected the request.");
            }

            var payload = await ReadBoundedAsync(
                    response.Content,
                    _options.MaximumTranscriptionResponseBytes,
                    timeout.Token)
                .ConfigureAwait(false);
            return ParseTranscript(payload);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw Failure(
                RecoveryFailureCategory.Timeout,
                "The transcription request timed out.",
                exception);
        }
        catch (RecoveryVoiceException)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            throw Failure(
                RecoveryFailureCategory.Network,
                "The transcription request failed.",
                exception);
        }
        catch (IOException exception)
        {
            throw Failure(
                RecoveryFailureCategory.Network,
                "The transcription response could not be read.",
                exception);
        }
    }

    private HttpRequestMessage CreateRequest(Stream audio)
    {
        var multipart = new MultipartFormDataContent();
        var audioContent = new StreamContent(audio);
        audioContent.Headers.ContentType = new MediaTypeHeaderValue(
            InMemoryTransientAudio.WaveContentType);
        multipart.Add(audioContent, "file", "checkin.wav");
        multipart.Add(new StringContent(_options.TranscriptionModel), "model");
        multipart.Add(new StringContent("json"), "response_format");

        var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = multipart
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            _options.ApiKey);
        return request;
    }

    private static string ParseTranscript(ReadOnlyMemory<byte> payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("text", out var textElement) ||
                textElement.ValueKind != JsonValueKind.String)
            {
                throw Failure(
                    RecoveryFailureCategory.InvalidResponse,
                    "The transcription response shape is invalid.");
            }

            var transcript = textElement.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(transcript) ||
                transcript.Length > RecoveryLimits.MaximumTranscriptLength ||
                transcript.Any(char.IsControl))
            {
                throw Failure(
                    RecoveryFailureCategory.InvalidResponse,
                    "The transcription text is invalid.");
            }

            return transcript;
        }
        catch (JsonException exception)
        {
            throw Failure(
                RecoveryFailureCategory.InvalidResponse,
                "The transcription response is malformed.",
                exception);
        }
    }

    private static async Task<ReadOnlyMemory<byte>> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > maximumBytes)
        {
            throw Failure(
                RecoveryFailureCategory.InvalidResponse,
                "The transcription response exceeds its byte limit.");
        }

        await using var source = await content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var destination = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                return destination.ToArray();
            }

            if (destination.Length + read > maximumBytes)
            {
                throw Failure(
                    RecoveryFailureCategory.InvalidResponse,
                    "The transcription response exceeds its byte limit.");
            }

            destination.Write(buffer, 0, read);
        }
    }

    private static RecoveryFailureCategory Categorize(HttpStatusCode status) =>
        status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                RecoveryFailureCategory.Authentication,
            HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout =>
                RecoveryFailureCategory.Timeout,
            HttpStatusCode.TooManyRequests =>
                RecoveryFailureCategory.RateLimited,
            >= HttpStatusCode.InternalServerError =>
                RecoveryFailureCategory.ProviderUnavailable,
            _ => RecoveryFailureCategory.InvalidResponse
        };

    private static RecoveryVoiceException Failure(
        RecoveryFailureCategory category,
        string message,
        Exception? innerException = null) =>
        new(
            category,
            VoiceRecoveryStage.Transcription,
            message,
            innerException);
}
