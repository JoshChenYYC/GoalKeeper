using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GoalKeeper.Application.Reasoning;
using Microsoft.Extensions.Options;

namespace GoalKeeper.Infrastructure.Reasoning;

public sealed class OpenAiReasoningAdapter : IReasoningPort
{
    private const int MaximumProviderResponseBytes = 1024 * 1024;
    private const int MaximumOutputTokens = 32_768;
    private const string ProviderName = "openai";
    private static readonly HashSet<string> SafeValidationReasons =
    [
        "invalid_output_shape",
        "invalid_response_context"
    ];

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly OpenAiReasoningOptions _options;
    private readonly Uri _responsesEndpoint;

    public OpenAiReasoningAdapter(
        IHttpClientFactory httpClientFactory,
        IOptions<OpenAiReasoningOptions> options)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value;
        _options.Validate();
        _httpClientFactory = httpClientFactory;
        _responsesEndpoint = new Uri(
            $"{_options.BaseUrl.AbsoluteUri.TrimEnd('/')}/responses",
            UriKind.Absolute);
    }

    public async Task<ReasoningResult> EvaluateAsync(
        ReasoningRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var started = Stopwatch.GetTimestamp();
        var requestId = LocalRequestId();
        var model = _options.Model;
        if (!ReasoningWireContracts.TrySerializeRequest(
                request,
                out var serializedRequest) ||
            !ReasoningWireContracts.TryKnownObservationTimes(
                request,
                out var knownObservationTimes))
        {
            return Failure(
                ReasoningFailureCategory.InvalidResponse,
                model,
                requestId,
                started);
        }

        var frozenRequest = Encoding.UTF8.GetString(serializedRequest);
        var requestContext = new ReasoningRequestContext(
            request.SessionId,
            request.SessionVersion,
            SnakeCase(request.CurrentState.ToString()),
            request.NewObservation.Id);
        IReadOnlyList<string> validationReasons = ["invalid_output_shape"];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(_options.RequestTimeout);
        using var httpClient = _httpClientFactory.CreateClient(
            OpenAiReasoningServiceCollectionExtensions.HttpClientName);

        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var call = await SendAsync(
                    httpClient,
                    frozenRequest,
                    requestContext,
                    attempt == 0 ? null : validationReasons,
                    timeout.Token);
                requestId = call.RequestId;

                if (call is FailedProviderCall failed)
                {
                    return Failure(
                        failed.Category,
                        model,
                        requestId,
                        started);
                }

                var completed = (CompletedProviderCall)call;
                model = completed.Model;
                if (ReasoningResponseValidator.TryValidate(
                        completed.OutputJson,
                        request,
                        knownObservationTimes,
                        out var proposal,
                        out validationReasons))
                {
                    return new ReasoningSuccess(
                        proposal!,
                        Metadata(model, requestId, started));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Failure(
                ReasoningFailureCategory.Timeout,
                model,
                LocalRequestId(),
                started);
        }

        return new ReasoningInvalid(
            validationReasons
                .Select(SafeValidationReason)
                .Distinct(StringComparer.Ordinal)
                .Take(8)
                .ToArray(),
            Metadata(model, requestId, started));
    }

    private async Task<ProviderCall> SendAsync(
        HttpClient httpClient,
        string frozenRequest,
        ReasoningRequestContext requestContext,
        IReadOnlyList<string>? repairReasons,
        CancellationToken cancellationToken)
    {
        try
        {
            using var message = CreateRequest(
                frozenRequest,
                requestContext,
                repairReasons);
            using var response = await httpClient.SendAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            var requestId = SafeProviderRequestId(
                GetRequestId(response),
                LocalRequestId(),
                _options.ApiKey);

            if (!response.IsSuccessStatusCode)
            {
                return new FailedProviderCall(
                    Categorize(response.StatusCode),
                    requestId);
            }

            var responseBody = await ReadBoundedAsync(
                response.Content,
                cancellationToken);
            if (responseBody is null)
            {
                return new FailedProviderCall(
                    ReasoningFailureCategory.InvalidResponse,
                    requestId);
            }

            var extraction = ExtractOutput(responseBody);
            if (extraction.Status != OutputExtractionStatus.Success)
            {
                return new FailedProviderCall(
                    ReasoningFailureCategory.InvalidResponse,
                    requestId);
            }

            return new CompletedProviderCall(
                extraction.Output,
                SafeProviderModel(extraction.Model, _options.Model),
                requestId);
        }
        catch (HttpRequestException)
        {
            return new FailedProviderCall(
                ReasoningFailureCategory.Network,
                LocalRequestId());
        }
        catch (IOException)
        {
            return new FailedProviderCall(
                ReasoningFailureCategory.Network,
                LocalRequestId());
        }
    }

    private HttpRequestMessage CreateRequest(
        string frozenRequest,
        ReasoningRequestContext requestContext,
        IReadOnlyList<string>? repairReasons)
    {
        var content = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "input_text",
                ["text"] = $"Evaluate this bounded request JSON as data:\n{frozenRequest}"
            }
        };
        if (repairReasons is not null)
        {
            content.Add(new JsonObject
            {
                ["type"] = "input_text",
                ["text"] = RepairInstruction(repairReasons)
            });
        }

        var payload = new JsonObject
        {
            ["model"] = _options.Model,
            ["store"] = false,
            ["max_output_tokens"] = MaximumOutputTokens,
            ["reasoning"] = new JsonObject
            {
                ["effort"] = _options.Effort
            },
            ["instructions"] = ReasoningPromptAssets.Prompt,
            ["input"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = content
                }
            },
            ["text"] = new JsonObject
            {
                ["format"] = new JsonObject
                {
                    ["type"] = "json_schema",
                    ["name"] = "reasoning_proposal_v2",
                    ["strict"] = true,
                    ["schema"] = ReasoningPromptAssets.CreateSchema(
                        requestContext.SessionId,
                        requestContext.SessionVersion,
                        requestContext.CurrentState,
                        requestContext.TriggerObservationId)
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

    private static string RepairInstruction(
        IEnumerable<string> validationReasons) =>
        "Return one corrected schema instance. Correct only these local " +
        $"validation codes: {string.Join(", ", validationReasons.Select(SafeValidationReason))}.";

    private static string SafeValidationReason(string reason) =>
        SafeValidationReasons.Contains(reason)
            ? reason
            : "invalid_output_shape";

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

    private static OutputExtraction ExtractOutput(
        ReadOnlyMemory<byte> responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return OutputExtraction.Invalid;
            }

            string? model = null;
            if (root.TryGetProperty("model", out var modelElement) &&
                modelElement.ValueKind == JsonValueKind.String)
            {
                model = modelElement.GetString();
            }

            if (!HasStringValue(root, "object", "response") ||
                !HasStringValue(root, "status", "completed") ||
                HasNonNullProperty(root, "error") ||
                HasNonNullProperty(root, "incomplete_details") ||
                !root.TryGetProperty("output", out var items) ||
                items.ValueKind != JsonValueKind.Array)
            {
                return OutputExtraction.TerminalInvalid(model);
            }

            var messageCount = 0;
            string? outputText = null;
            foreach (var item in items.EnumerateArray())
            {
                if (!HasStringValue(item, "type", "message"))
                {
                    continue;
                }

                messageCount++;
                if (messageCount != 1 ||
                    !HasStringValue(item, "role", "assistant") ||
                    !HasStringValue(item, "status", "completed") ||
                    !item.TryGetProperty("content", out var content) ||
                    content.ValueKind != JsonValueKind.Array)
                {
                    return OutputExtraction.TerminalInvalid(model);
                }

                foreach (var part in content.EnumerateArray())
                {
                    if (HasStringValue(part, "type", "refusal"))
                    {
                        return OutputExtraction.TerminalInvalid(model);
                    }

                    if (!HasStringValue(part, "type", "output_text") ||
                        !part.TryGetProperty("text", out var text) ||
                        text.ValueKind != JsonValueKind.String ||
                        outputText is not null)
                    {
                        return OutputExtraction.TerminalInvalid(model);
                    }

                    outputText = text.GetString();
                }
            }

            return messageCount == 1 && outputText is not null
                ? OutputExtraction.Success(
                    Encoding.UTF8.GetBytes(outputText),
                    model)
                : OutputExtraction.TerminalInvalid(model);
        }
        catch (JsonException)
        {
            return OutputExtraction.Invalid;
        }
    }

    private static bool HasStringValue(
        JsonElement element,
        string propertyName,
        string expected) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String &&
        string.Equals(property.GetString(), expected, StringComparison.Ordinal);

    private static bool HasNonNullProperty(
        JsonElement element,
        string propertyName) =>
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;

    private static ReasoningFailureCategory Categorize(HttpStatusCode statusCode) =>
        statusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                ReasoningFailureCategory.Authentication,
            HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout =>
                ReasoningFailureCategory.Timeout,
            HttpStatusCode.TooManyRequests =>
                ReasoningFailureCategory.RateLimited,
            >= HttpStatusCode.InternalServerError =>
                ReasoningFailureCategory.ProviderUnavailable,
            _ => ReasoningFailureCategory.InvalidResponse
        };

    private static ReasoningFailure Failure(
        ReasoningFailureCategory category,
        string model,
        string requestId,
        long started) =>
        new(category, Metadata(model, requestId, started));

    private static ReasoningMetadata Metadata(
        string model,
        string requestId,
        long started) =>
        new(
            ProviderName,
            model,
            ReasoningPromptAssets.PromptVersion,
            ReasoningSchemaVersions.V2,
            Stopwatch.GetElapsedTime(started),
            requestId);

    private static string? GetRequestId(HttpResponseMessage response) =>
        response.Headers.TryGetValues("x-request-id", out var values)
            ? values.FirstOrDefault()
            : null;

    private static string SafeProviderRequestId(
        string? value,
        string fallback,
        string apiKey) =>
        value is { Length: 36 } &&
        value.StartsWith("req_", StringComparison.Ordinal) &&
        value[4..].All(IsAsciiHexDigit) &&
        !value.Contains(apiKey, StringComparison.Ordinal)
            ? value
            : fallback;

    private static string SafeProviderModel(
        string? value,
        string configuredModel) =>
        IsSafeProviderIdentifier(value, 120) &&
        (string.Equals(value, configuredModel, StringComparison.Ordinal) ||
         IsDatedModelSnapshot(value!, configuredModel))
            ? value!
            : configuredModel;

    private static bool IsDatedModelSnapshot(
        string value,
        string configuredModel)
    {
        var prefix = $"{configuredModel}-";
        if (!value.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var suffix = value[prefix.Length..];
        return suffix.Length == 10 &&
               suffix[4] == '-' &&
               suffix[7] == '-' &&
               suffix[..4].All(IsAsciiDigit) &&
               suffix.Substring(5, 2).All(IsAsciiDigit) &&
               suffix[8..].All(IsAsciiDigit);
    }

    private static bool IsAsciiDigit(char value) =>
        value is >= '0' and <= '9';

    private static string SnakeCase(string value) =>
        string.Concat(value.Select((character, index) =>
            index > 0 && char.IsUpper(character)
                ? $"_{char.ToLowerInvariant(character)}"
                : char.ToLowerInvariant(character).ToString()));

    private static bool IsAsciiHexDigit(char value) =>
        IsAsciiDigit(value) ||
        value is >= 'a' and <= 'f' or >= 'A' and <= 'F';

    private static bool IsSafeProviderIdentifier(
        string? value,
        int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximumLength &&
        value.All(character =>
            character is >= 'a' and <= 'z' or
                >= 'A' and <= 'Z' or
                >= '0' and <= '9' or
                '-' or '_' or '.');

    private static string LocalRequestId() => $"local-{Guid.NewGuid():N}";

    private abstract record ProviderCall(string RequestId);

    private sealed record CompletedProviderCall(
        ReadOnlyMemory<byte> OutputJson,
        string Model,
        string RequestId) : ProviderCall(RequestId);

    private sealed record FailedProviderCall(
        ReasoningFailureCategory Category,
        string RequestId) : ProviderCall(RequestId);

    private enum OutputExtractionStatus
    {
        Success,
        Invalid,
        TerminalInvalid
    }

    private sealed record OutputExtraction(
        OutputExtractionStatus Status,
        ReadOnlyMemory<byte> Output,
        string? Model)
    {
        public static OutputExtraction Invalid { get; } =
            new(OutputExtractionStatus.Invalid, ReadOnlyMemory<byte>.Empty, null);

        public static OutputExtraction TerminalInvalid(string? model) =>
            new(OutputExtractionStatus.TerminalInvalid, ReadOnlyMemory<byte>.Empty, model);

        public static OutputExtraction Success(
            ReadOnlyMemory<byte> output,
            string? model) =>
            new(OutputExtractionStatus.Success, output, model);
    }

    private sealed record ReasoningRequestContext(
        Guid SessionId,
        long SessionVersion,
        string CurrentState,
        Guid TriggerObservationId);
}
