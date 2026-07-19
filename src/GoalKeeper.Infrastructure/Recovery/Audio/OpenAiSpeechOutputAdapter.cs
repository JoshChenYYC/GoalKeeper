using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using GoalKeeper.Application.Recovery;
using Microsoft.Extensions.Options;

namespace GoalKeeper.Infrastructure.Recovery.Audio;

public sealed class OpenAiSpeechOutputAdapter : ISpeechOutputPort
{
    public const string HttpClientName =
        "GoalKeeper.OpenAI.Recovery.Speech";

    private static readonly RecoveryPcmFormat SpeechFormat =
        new(24_000, 16, 1);
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IRecoveryAudioPlaybackSink _playback;
    private readonly OpenAiRecoveryAudioOptions _options;
    private readonly Uri _endpoint;

    public OpenAiSpeechOutputAdapter(
        IHttpClientFactory httpClientFactory,
        IRecoveryAudioPlaybackSink playback,
        IOptions<OpenAiRecoveryAudioOptions> options)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(playback);
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _options.Validate();
        _httpClientFactory = httpClientFactory;
        _playback = playback;
        _endpoint = new(
            $"{_options.BaseUrl.AbsoluteUri.TrimEnd('/')}/audio/speech",
            UriKind.Absolute);
    }

    public async Task SpeakAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text) ||
            text.Length > RecoveryLimits.MaximumResponseLength ||
            text.Any(char.IsControl))
        {
            throw Failure(
                VoiceRecoveryStage.Playback,
                RecoveryFailureCategory.InvalidResponse,
                "Speech text is invalid.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(_options.RequestTimeout);
        using var httpClient = _httpClientFactory.CreateClient(HttpClientName);

        try
        {
            using var request = CreateRequest(text);
            using var response = await httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw Failure(
                    VoiceRecoveryStage.Playback,
                    Categorize(response.StatusCode),
                    "The speech provider rejected the request.");
            }

            if (response.Content.Headers.ContentLength >
                _options.MaximumSpeechResponseBytes)
            {
                throw Failure(
                    VoiceRecoveryStage.Playback,
                    RecoveryFailureCategory.InvalidResponse,
                    "The speech response exceeds its byte limit.");
            }

            var pcmBytes = await ReadPcmAsync(
                    response.Content,
                    timeout.Token)
                .ConfigureAwait(false);
            try
            {
                using var pcm = new MemoryStream(pcmBytes, writable: false);
                await _playback.PlayPcmAsync(
                        pcm,
                        SpeechFormat,
                        timeout.Token)
                    .ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pcmBytes);
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw Failure(
                VoiceRecoveryStage.Playback,
                RecoveryFailureCategory.Timeout,
                "Speech generation or playback timed out.",
                exception);
        }
        catch (RecoveryVoiceException)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            throw Failure(
                VoiceRecoveryStage.Playback,
                RecoveryFailureCategory.Network,
                "The speech request failed.",
                exception);
        }
        catch (IOException exception)
        {
            throw Failure(
                VoiceRecoveryStage.Playback,
                RecoveryFailureCategory.Network,
                "The speech response or playback failed.",
                exception);
        }
        catch (Exception exception)
        {
            throw Failure(
                VoiceRecoveryStage.Playback,
                RecoveryFailureCategory.ProviderUnavailable,
                "Speech playback failed.",
                exception);
        }
    }

    public async Task PlayListeningCueAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await _playback.PlayCueAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (RecoveryVoiceException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw Failure(
                VoiceRecoveryStage.Cue,
                RecoveryFailureCategory.ProviderUnavailable,
                "The listening cue could not be played.",
                exception);
        }
    }

    private HttpRequestMessage CreateRequest(string text)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = JsonContent.Create(new
            {
                model = _options.SpeechModel,
                voice = _options.Voice,
                input = text,
                response_format = "pcm"
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            _options.ApiKey);
        return request;
    }

    private async Task<byte[]> ReadPcmAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        var mediaType = content.Headers.ContentType?.MediaType;
        if (mediaType is not ("audio/pcm" or "application/octet-stream"))
        {
            throw Failure(
                VoiceRecoveryStage.Playback,
                RecoveryFailureCategory.InvalidResponse,
                "The speech response content type is invalid.");
        }

        await using var source = await content.ReadAsStreamAsync(
                cancellationToken)
            .ConfigureAwait(false);
        using var destination = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await source.ReadAsync(
                    buffer.AsMemory(),
                    cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (destination.Length + read >
                _options.MaximumSpeechResponseBytes)
            {
                throw Failure(
                    VoiceRecoveryStage.Playback,
                    RecoveryFailureCategory.InvalidResponse,
                    "The speech response exceeds its byte limit.");
            }

            destination.Write(buffer, 0, read);
        }

        var pcm = destination.ToArray();
        if (pcm.Length == 0 || pcm.Length % 2 != 0)
        {
            throw Failure(
                VoiceRecoveryStage.Playback,
                RecoveryFailureCategory.InvalidResponse,
                "The speech response is empty or has invalid PCM alignment.");
        }

        return pcm;
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
        VoiceRecoveryStage stage,
        RecoveryFailureCategory category,
        string message,
        Exception? innerException = null) =>
        new(category, stage, message, innerException);
}
