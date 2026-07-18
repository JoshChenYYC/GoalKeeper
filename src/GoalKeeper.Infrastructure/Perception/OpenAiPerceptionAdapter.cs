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
    private const int MaximumOutputTokens = 16_384;
    private const string ProviderName = "openai";
    private static readonly HashSet<string> KnownRepairPaths =
    [
        "$",
        "$.schema_version",
        "$.image_quality",
        "$.image_quality.value",
        "$.image_quality.limitations",
        "$.people_count",
        "$.people_count.status",
        "$.people_count.value",
        "$.people_count.support",
        "$.people_count.limitations",
        "$.objects",
        "$.visible_cues"
    ];
    private static readonly HashSet<string> KnownCueRepairSuffixes =
    [
        ".subject",
        ".kind",
        ".state",
        ".support",
        ".description",
        ".visual_basis",
        ".limitations"
    ];

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly OpenAiPerceptionOptions _options;
    private readonly Uri _responsesEndpoint;

    public OpenAiPerceptionAdapter(
        IHttpClientFactory httpClientFactory,
        IOptions<OpenAiPerceptionOptions> options)
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

    public async Task<PerceptionResult> ObserveAsync(
        PerceptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var started = Stopwatch.GetTimestamp();
        var requestId = LocalRequestId();
        var model = _options.Model;
        ObservationValidationFailure? repairFailure = null;
        using var httpClient = _httpClientFactory.CreateClient(
            OpenAiPerceptionServiceCollectionExtensions.HttpClientName);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var call = await SendAsync(
                httpClient,
                request,
                repairFailure,
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

            repairFailure = ((InvalidObservation)validation).Failure;
        }

        return Failure(
            PerceptionFailureCategory.InvalidResponse,
            model,
            requestId,
            started);
    }

    private async Task<ProviderCall> SendAsync(
        HttpClient httpClient,
        PerceptionRequest request,
        ObservationValidationFailure? repairFailure,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(_options.RequestTimeout);

        try
        {
            using var message = CreateRequest(request, repairFailure);
            using var response = await httpClient.SendAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
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
                timeout.Token);
            if (responseBody is null)
            {
                return new FailedProviderCall(
                    PerceptionFailureCategory.InvalidResponse,
                    requestId);
            }

            var extraction = ExtractOutput(responseBody);
            if (extraction.Status != OutputExtractionStatus.Success)
            {
                return new FailedProviderCall(
                    PerceptionFailureCategory.InvalidResponse,
                    requestId);
            }

            return new CompletedProviderCall(
                extraction.Output,
                SafeProviderModel(extraction.Model, _options.Model),
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
        ObservationValidationFailure? repairFailure)
    {
        var imageData = Convert.ToBase64String(request.JpegBytes.Span);
        var payload = new JsonObject
        {
            ["model"] = _options.Model,
            ["store"] = false,
            ["max_output_tokens"] = MaximumOutputTokens,
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
                            ["text"] = repairFailure is not null
                                ? RepairInstruction(repairFailure)
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

    private static string RepairInstruction(
        ObservationValidationFailure failure)
    {
        var issues = failure.Issues
            .Take(8)
            .Select(issue => $"{SafeRepairPath(issue.Path)} ({issue.Code})");
        return "Re-examine the image and return one corrected schema instance. " +
               $"Correct these local validation issues: {string.Join(", ", issues)}.";
    }

    private static string SafeRepairPath(string path)
    {
        if (KnownRepairPaths.Contains(path))
        {
            return path;
        }

        foreach (var collectionPath in new[]
                 {
                     "$.image_quality.limitations",
                     "$.people_count.limitations",
                     "$.objects"
                 })
        {
            if (IsIndexedPath(path, collectionPath, out var suffix) &&
                suffix.Length == 0)
            {
                return $"{collectionPath}[]";
            }
        }

        if (IsIndexedPath(path, "$.visible_cues", out var cueSuffix))
        {
            if (cueSuffix.Length == 0)
            {
                return "$.visible_cues[]";
            }

            if (KnownCueRepairSuffixes.Contains(cueSuffix))
            {
                return $"$.visible_cues[]{cueSuffix}";
            }

            const string limitationsSuffix = ".limitations";
            if (cueSuffix.StartsWith(
                    $"{limitationsSuffix}[",
                    StringComparison.Ordinal) &&
                IsIndexedPath(
                    cueSuffix,
                    limitationsSuffix,
                    out var limitationItemSuffix) &&
                limitationItemSuffix.Length == 0)
            {
                return "$.visible_cues[].limitations[]";
            }
        }

        return "$";
    }

    private static bool IsIndexedPath(
        string path,
        string collectionPath,
        out string suffix)
    {
        suffix = string.Empty;
        var prefix = $"{collectionPath}[";
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var closingBracket = path.IndexOf(']', prefix.Length);
        if (closingBracket < 0 ||
            closingBracket == prefix.Length ||
            !path[prefix.Length..closingBracket].All(IsAsciiDigit))
        {
            return false;
        }

        suffix = path[(closingBracket + 1)..];
        return true;
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

    private static OutputExtraction ExtractOutput(
        ReadOnlyMemory<byte> responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return OutputExtraction.Missing;
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
                HasNonNullProperty(root, "incomplete_details"))
            {
                return OutputExtraction.TerminalInvalid(model);
            }

            if (!root.TryGetProperty("output", out var items) ||
                items.ValueKind != JsonValueKind.Array)
            {
                return OutputExtraction.MissingWithModel(model);
            }

            string? outputText = null;
            foreach (var item in items.EnumerateArray())
            {
                if (!HasStringValue(item, "type", "message"))
                {
                    continue;
                }

                if (!HasStringValue(item, "role", "assistant") ||
                    !HasStringValue(item, "status", "completed"))
                {
                    return OutputExtraction.TerminalInvalid(model);
                }

                if (!item.TryGetProperty("content", out var content) ||
                    content.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var part in content.EnumerateArray())
                {
                    if (HasStringValue(part, "type", "refusal"))
                    {
                        return OutputExtraction.TerminalInvalid(model);
                    }

                    if (HasStringValue(part, "type", "output_text") &&
                        part.TryGetProperty("text", out var text) &&
                        text.ValueKind == JsonValueKind.String)
                    {
                        if (outputText is not null)
                        {
                            return OutputExtraction.TerminalInvalid(model);
                        }

                        outputText = text.GetString();
                    }
                }
            }

            return outputText is null
                ? OutputExtraction.MissingWithModel(model)
                : OutputExtraction.Success(
                    Encoding.UTF8.GetBytes(outputText),
                    model);
        }
        catch (JsonException)
        {
            return OutputExtraction.Missing;
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

    private static bool IsAsciiHexDigit(char value) =>
        IsAsciiDigit(value) ||
        value is >= 'a' and <= 'f' or >= 'A' and <= 'F';

    private static bool IsSafeProviderIdentifier(
        string? value,
        int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > maximumLength)
        {
            return false;
        }

        return value.All(character =>
            character is >= 'a' and <= 'z' or
                >= 'A' and <= 'Z' or
                >= '0' and <= '9' or
                '-' or '_' or '.');
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

    private enum OutputExtractionStatus
    {
        Success,
        Missing,
        TerminalInvalid
    }

    private sealed record OutputExtraction(
        OutputExtractionStatus Status,
        ReadOnlyMemory<byte> Output,
        string? Model)
    {
        public static OutputExtraction Missing { get; } =
            new(OutputExtractionStatus.Missing, ReadOnlyMemory<byte>.Empty, null);

        public static OutputExtraction MissingWithModel(string? model) =>
            new(OutputExtractionStatus.Missing, ReadOnlyMemory<byte>.Empty, model);

        public static OutputExtraction TerminalInvalid(string? model) =>
            new(OutputExtractionStatus.TerminalInvalid, ReadOnlyMemory<byte>.Empty, model);

        public static OutputExtraction Success(
            ReadOnlyMemory<byte> output,
            string? model) =>
            new(OutputExtractionStatus.Success, output, model);
    }
}
