using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GoalKeeper.Application.Perception;
using Microsoft.Extensions.Options;

namespace GoalKeeper.Infrastructure.Perception;

public sealed class OpenAiPerceptionAdapter : IPerceptionPort
{
    private const int MaximumProviderResponseBytes = 1024 * 1024;
    private const string ProviderName = "openai";

    private readonly HttpClient _httpClient;
    private readonly OpenAiPerceptionOptions _options;
    private readonly Uri _responsesEndpoint;

    public OpenAiPerceptionAdapter(
        HttpClient httpClient,
        IOptions<OpenAiPerceptionOptions> options)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value;
        _options.Validate();
        _httpClient = httpClient;
        _responsesEndpoint = new Uri(
            $"{_options.BaseUrl.AbsoluteUri.TrimEnd('/')}/responses",
            UriKind.Absolute);
    }

    public async Task<PerceptionResult> ObserveAsync(
        PerceptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var started = Stopwatch.GetTimestamp();
        var requestId = LocalRequestId();
        var model = _options.Model;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var call = await SendAsync(
                request,
                isRepair: attempt == 1,
                cancellationToken);
            requestId = call.RequestId;

            if (call is FailedProviderCall failed)
            {
                return Failure(failed.Category, model, requestId, started);
            }

            var completed = (CompletedProviderCall)call;
            model = completed.Model;
            var validation = ObservationValidator.Validate(
                completed.OutputJson,
                request.Options);
            if (validation is ValidatedObservation valid)
            {
                return new PerceptionSuccess(
                    valid.Value,
                    Metadata(model, requestId, started));
            }
        }

        return Failure(
            PerceptionFailureCategory.InvalidResponse,
            model,
            requestId,
            started);
    }

    private async Task<ProviderCall> SendAsync(
        PerceptionRequest request,
        bool isRepair,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(_options.RequestTimeout);

        try
        {
            using var message = CreateRequest(request, isRepair);
            using var response = await _httpClient.SendAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            var requestId = SafeProviderValue(
                GetRequestId(response),
                160,
                LocalRequestId());

            if (!response.IsSuccessStatusCode)
            {
                return new FailedProviderCall(
                    Categorize(response.StatusCode),
                    requestId);
            }

            var responseBody = await ReadBoundedAsync(
                response.Content,
                timeout.Token);
            if (responseBody is null ||
                !TryExtractOutput(responseBody, out var output, out var returnedModel))
            {
                return new CompletedProviderCall(
                    ReadOnlyMemory<byte>.Empty,
                    _options.Model,
                    requestId);
            }

            return new CompletedProviderCall(
                output,
                SafeProviderValue(returnedModel, 120, _options.Model),
                requestId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new FailedProviderCall(
                PerceptionFailureCategory.Timeout,
                LocalRequestId());
        }
        catch (HttpRequestException)
        {
            return new FailedProviderCall(
                PerceptionFailureCategory.Network,
                LocalRequestId());
        }
        catch (IOException)
        {
            return new FailedProviderCall(
                PerceptionFailureCategory.Network,
                LocalRequestId());
        }
    }

    private HttpRequestMessage CreateRequest(
        PerceptionRequest request,
        bool isRepair)
    {
        var imageData = Convert.ToBase64String(request.JpegBytes.Span);
        var payload = new JsonObject
        {
            ["model"] = _options.Model,
            ["store"] = false,
            ["instructions"] = PerceptionPromptAssets.Prompt,
            ["input"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "input_text",
                            ["text"] = isRepair
                                ? "Re-examine the image and return one corrected schema instance."
                                : "Examine the image and return one schema instance."
                        },
                        new JsonObject
                        {
                            ["type"] = "input_image",
                            ["image_url"] = $"data:image/jpeg;base64,{imageData}",
                            ["detail"] = _options.ImageDetail
                        }
                    }
                }
            },
            ["text"] = new JsonObject
            {
                ["format"] = new JsonObject
                {
                    ["type"] = "json_schema",
                    ["name"] = "neutral_observation_v1",
                    ["strict"] = true,
                    ["schema"] = PerceptionPromptAssets.CreateSchema(request.Options)
                }
            },
            ["tools"] = new JsonArray()
        };

        var message = new HttpRequestMessage(HttpMethod.Post, _responsesEndpoint)
        {
            Content = new ByteArrayContent(
                JsonSerializer.SerializeToUtf8Bytes(payload))
        };
        message.Content.Headers.ContentType = new MediaTypeHeaderValue(
            "application/json");
        message.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            _options.ApiKey);
        return message;
    }

    private static async Task<byte[]?> ReadBoundedAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaximumProviderResponseBytes)
        {
            return null;
        }

        await using var source = await content.ReadAsStreamAsync(cancellationToken);
        using var destination = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
            {
                return destination.ToArray();
            }

            if (destination.Length + read > MaximumProviderResponseBytes)
            {
                return null;
            }

            destination.Write(buffer, 0, read);
        }
    }

    private static bool TryExtractOutput(
        ReadOnlyMemory<byte> responseBody,
        out ReadOnlyMemory<byte> output,
        out string? model)
    {
        output = ReadOnlyMemory<byte>.Empty;
        model = null;

        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (root.TryGetProperty("model", out var modelElement) &&
                modelElement.ValueKind == JsonValueKind.String)
            {
                model = modelElement.GetString();
            }

            if (!root.TryGetProperty("output", out var items) ||
                items.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("content", out var content) ||
                    content.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var part in content.EnumerateArray())
                {
                    if (part.TryGetProperty("type", out var type) &&
                        type.ValueKind == JsonValueKind.String &&
                        string.Equals(
                            type.GetString(),
                            "output_text",
                            StringComparison.Ordinal) &&
                        part.TryGetProperty("text", out var text) &&
                        text.ValueKind == JsonValueKind.String)
                    {
                        output = Encoding.UTF8.GetBytes(text.GetString()!);
                        return true;
                    }
                }
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    private static PerceptionFailureCategory Categorize(HttpStatusCode statusCode) =>
        statusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                PerceptionFailureCategory.Authentication,
            HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout =>
                PerceptionFailureCategory.Timeout,
            HttpStatusCode.TooManyRequests =>
                PerceptionFailureCategory.RateLimited,
            >= HttpStatusCode.InternalServerError =>
                PerceptionFailureCategory.ProviderUnavailable,
            _ => PerceptionFailureCategory.InvalidResponse
        };

    private static PerceptionFailure Failure(
        PerceptionFailureCategory category,
        string model,
        string requestId,
        long started) =>
        new(category, Metadata(model, requestId, started));

    private static PerceptionMetadata Metadata(
        string model,
        string requestId,
        long started) =>
        new(
            ProviderName,
            model,
            PerceptionPromptAssets.PromptVersion,
            ObservationSchemaVersions.V1,
            Stopwatch.GetElapsedTime(started),
            requestId);

    private static string? GetRequestId(HttpResponseMessage response) =>
        response.Headers.TryGetValues("x-request-id", out var values)
            ? values.FirstOrDefault()
            : null;

    private static string SafeProviderValue(
        string? value,
        int maximumLength,
        string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var safe = new string(value
            .Where(character => !char.IsControl(character))
            .Take(maximumLength)
            .ToArray());
        return string.IsNullOrWhiteSpace(safe) ? fallback : safe;
    }

    private static string LocalRequestId() => $"local-{Guid.NewGuid():N}";

    private abstract record ProviderCall(string RequestId);

    private sealed record CompletedProviderCall(
        ReadOnlyMemory<byte> OutputJson,
        string Model,
        string RequestId) : ProviderCall(RequestId);

    private sealed record FailedProviderCall(
        PerceptionFailureCategory Category,
        string RequestId) : ProviderCall(RequestId);
}
