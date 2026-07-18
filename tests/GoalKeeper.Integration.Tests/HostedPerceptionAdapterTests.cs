using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GoalKeeper.Application.Perception;
using GoalKeeper.Infrastructure.Perception;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GoalKeeper.Integration.Tests;

public sealed class HostedPerceptionAdapterTests
{
    private const string TestApiKey = "test-key-never-log";
    private const string RequestId1 = "req_00000000000000000000000000000001";
    private const string RequestId2 = "req_00000000000000000000000000000002";

    [Fact]
    public async Task Recorded_valid_response_returns_a_validated_neutral_observation()
    {
        var handler = new ScriptedHandler(Success("valid-response.json", RequestId1));
        var adapter = CreateAdapter(handler);

        var result = Assert.IsType<PerceptionSuccess>(await adapter.ObserveAsync(Request()));

        Assert.Equal(ImageQualityValue.Adequate, result.Proposal.ImageQuality.Value);
        Assert.Equal(1, result.Proposal.PeopleCount.Value);
        Assert.Equal(["laptop"], result.Proposal.Objects);
        Assert.Equal("openai", result.Metadata.Provider);
        Assert.Equal("gpt-5.6-luna-2026-07-01", result.Metadata.Model);
        Assert.Equal("perception-v1", result.Metadata.PromptVersion);
        Assert.Equal(RequestId1, result.Metadata.RequestId);
        Assert.Single(handler.Requests);

        using var payload = JsonDocument.Parse(handler.Requests[0].Body);
        Assert.False(payload.RootElement.GetProperty("store").GetBoolean());
        Assert.Equal(16_384, payload.RootElement.GetProperty("max_output_tokens").GetInt32());
        Assert.Empty(payload.RootElement.GetProperty("tools").EnumerateArray());
        var format = payload.RootElement.GetProperty("text").GetProperty("format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.True(format.GetProperty("strict").GetBoolean());
        Assert.Equal(
            2,
            format.GetProperty("schema")
                .GetProperty("properties")
                .GetProperty("objects")
                .GetProperty("maxItems")
                .GetInt32());
        Assert.Equal(
            1,
            format.GetProperty("schema")
                .GetProperty("properties")
                .GetProperty("visible_cues")
                .GetProperty("maxItems")
                .GetInt32());
        Assert.Equal(
            "^.{1,80}$",
            format.GetProperty("schema")
                .GetProperty("properties")
                .GetProperty("objects")
                .GetProperty("items")
                .GetProperty("pattern")
                .GetString());
        var peopleCountBranches = format.GetProperty("schema")
            .GetProperty("properties")
            .GetProperty("people_count")
            .GetProperty("anyOf");
        Assert.Equal(3, peopleCountBranches.GetArrayLength());
        Assert.Equal(
            "counted",
            peopleCountBranches[0]
                .GetProperty("properties")
                .GetProperty("status")
                .GetProperty("enum")[0]
                .GetString());
        Assert.Equal(
            1,
            peopleCountBranches[0]
                .GetProperty("properties")
                .GetProperty("value")
                .GetProperty("minimum")
                .GetInt32());
        Assert.Equal(
            "not_visible",
            peopleCountBranches[1]
                .GetProperty("properties")
                .GetProperty("status")
                .GetProperty("enum")[0]
                .GetString());
        Assert.Equal(
            0,
            peopleCountBranches[1]
                .GetProperty("properties")
                .GetProperty("value")
                .GetProperty("enum")[0]
                .GetInt32());
        Assert.Equal(
            "null",
            peopleCountBranches[2]
                .GetProperty("properties")
                .GetProperty("value")
                .GetProperty("type")
                .GetString());

        var content = payload.RootElement.GetProperty("input")[0].GetProperty("content");
        var image = content[1];
        Assert.Equal("input_image", image.GetProperty("type").GetString());
        Assert.Equal("low", image.GetProperty("detail").GetString());
        Assert.Equal(
            "data:image/jpeg;base64,/9gB/9k=",
            image.GetProperty("image_url").GetString());
        Assert.Equal("Bearer", handler.Requests[0].Authorization?.Scheme);
        Assert.Equal(TestApiKey, handler.Requests[0].Authorization?.Parameter);
        Assert.DoesNotContain(TestApiKey, SafeMetadata(result.Metadata), StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_invalid_response_is_repaired_with_one_bounded_second_request()
    {
        var handler = new ScriptedHandler(
            Success("invalid-response.json", RequestId1),
            Success("valid-response.json", RequestId2));
        var factory = new RecordingHttpClientFactory(handler);
        var adapter = CreateAdapter(factory);

        var result = Assert.IsType<PerceptionSuccess>(await adapter.ObserveAsync(Request()));

        Assert.Equal(RequestId2, result.Metadata.RequestId);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(1, factory.CreateCount);
        Assert.Equal(1, factory.DisposeCount);
        Assert.DoesNotContain("Re-examine", handler.Requests[0].Body, StringComparison.Ordinal);
        Assert.Contains("Re-examine", handler.Requests[1].Body, StringComparison.Ordinal);
        Assert.Contains("$.people_count (MissingField)", handler.Requests[1].Body, StringComparison.Ordinal);
        Assert.Contains("$.objects (InvalidType)", handler.Requests[1].Body, StringComparison.Ordinal);
        Assert.DoesNotContain("objects\\\":\\\"laptop", handler.Requests[1].Body, StringComparison.Ordinal);
        Assert.DoesNotContain(TestApiKey, handler.Requests[1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Empty_scene_people_count_mismatch_is_explained_to_the_repair_request()
    {
        var handler = new ScriptedHandler(
            Success("inconsistent-empty-response.json", RequestId1),
            Success("valid-empty-response.json", RequestId2));
        var adapter = CreateAdapter(handler);

        var result = Assert.IsType<PerceptionSuccess>(await adapter.ObserveAsync(Request()));

        Assert.Equal(PeopleCountStatus.NotVisible, result.Proposal.PeopleCount.Status);
        Assert.Equal(0, result.Proposal.PeopleCount.Value);
        Assert.Equal(RequestId2, result.Metadata.RequestId);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains(
            "$.people_count.value (InconsistentValue)",
            handler.Requests[1].Body,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "\\\"value\\\":null",
            handler.Requests[1].Body,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_second_invalid_or_malformed_response_is_a_typed_technical_failure()
    {
        var handler = new ScriptedHandler(
            Success("invalid-response.json", RequestId1),
            Success("malformed-response.json", RequestId2));
        var adapter = CreateAdapter(handler);

        var result = Assert.IsType<PerceptionFailure>(await adapter.ObserveAsync(Request()));

        Assert.Equal(PerceptionFailureCategory.InvalidResponse, result.Category);
        Assert.Equal(RequestId2, result.Metadata.RequestId);
        Assert.Equal(2, handler.Requests.Count);
        Assert.IsNotType<PerceptionSuccess>(result);
    }

    [Fact]
    public async Task Request_timeout_returns_a_safe_timeout_failure_without_wall_clock_hanging()
    {
        var handler = new ScriptedHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var adapter = CreateAdapter(
            handler,
            options => options.RequestTimeout = TimeSpan.FromMilliseconds(20));

        var result = Assert.IsType<PerceptionFailure>(await adapter.ObserveAsync(Request()));

        Assert.Equal(PerceptionFailureCategory.Timeout, result.Category);
        Assert.StartsWith("local-", result.Metadata.RequestId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Caller_cancellation_is_propagated_instead_of_reclassified_as_timeout()
    {
        var handler = new ScriptedHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var adapter = CreateAdapter(handler);
        using var cancellation = new CancellationTokenSource();

        var operation = adapter.ObserveAsync(Request(), cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
    }

    [Fact]
    public async Task Recorded_rate_limit_response_returns_a_safe_category_without_its_body()
    {
        var handler = new ScriptedHandler((_, _) => Task.FromResult(
            Response(
                HttpStatusCode.TooManyRequests,
                Fixture("rate-limit-response.json"),
                RequestId1)));
        var adapter = CreateAdapter(handler);

        var result = Assert.IsType<PerceptionFailure>(await adapter.ObserveAsync(Request()));

        Assert.Equal(PerceptionFailureCategory.RateLimited, result.Category);
        Assert.Equal(RequestId1, result.Metadata.RequestId);
        Assert.DoesNotContain("rate_limit_error", SafeMetadata(result.Metadata), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Network_exception_returns_a_safe_network_failure()
    {
        var handler = new ScriptedHandler((_, _) => Task.FromException<HttpResponseMessage>(
            new HttpRequestException($"simulated network failure {TestApiKey}")));
        var adapter = CreateAdapter(handler);

        var result = Assert.IsType<PerceptionFailure>(await adapter.ObserveAsync(Request()));

        Assert.Equal(PerceptionFailureCategory.Network, result.Category);
        Assert.DoesNotContain(TestApiKey, SafeMetadata(result.Metadata), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, PerceptionFailureCategory.Authentication)]
    [InlineData(HttpStatusCode.Forbidden, PerceptionFailureCategory.Authentication)]
    [InlineData(HttpStatusCode.InternalServerError, PerceptionFailureCategory.ProviderUnavailable)]
    public async Task Provider_statuses_are_mapped_without_exposing_error_bodies(
        HttpStatusCode status,
        PerceptionFailureCategory expected)
    {
        var handler = new ScriptedHandler((_, _) => Task.FromResult(
            Response(status, $"secret provider body {TestApiKey}", RequestId1)));
        var adapter = CreateAdapter(handler);

        var result = Assert.IsType<PerceptionFailure>(await adapter.ObserveAsync(Request()));

        Assert.Equal(expected, result.Category);
        Assert.DoesNotContain(TestApiKey, SafeMetadata(result.Metadata), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("incomplete")]
    [InlineData("failed")]
    [InlineData("in_progress")]
    public async Task Non_completed_response_lifecycle_is_terminal_without_resending_the_image(
        string status)
    {
        var responseBody = MutateValidResponse(root => root["status"] = status);
        var handler = new ScriptedHandler((_, _) => Task.FromResult(
            Response(HttpStatusCode.OK, responseBody, RequestId1)));
        var adapter = CreateAdapter(handler);

        var result = Assert.IsType<PerceptionFailure>(await adapter.ObserveAsync(Request()));

        Assert.Equal(PerceptionFailureCategory.InvalidResponse, result.Category);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("error")]
    [InlineData("incomplete_details")]
    public async Task Completed_response_with_failure_details_is_terminal_without_repair(
        string propertyName)
    {
        var responseBody = MutateValidResponse(root =>
            root[propertyName] = new JsonObject { ["reason"] = "recorded failure" });
        var handler = new ScriptedHandler((_, _) => Task.FromResult(
            Response(HttpStatusCode.OK, responseBody, RequestId1)));
        var adapter = CreateAdapter(handler);

        var result = Assert.IsType<PerceptionFailure>(await adapter.ObserveAsync(Request()));

        Assert.Equal(PerceptionFailureCategory.InvalidResponse, result.Category);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Refusal_is_terminal_without_resending_the_image()
    {
        var responseBody = MutateValidResponse(root =>
        {
            var message = root["output"]![0]!.AsObject();
            message["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "refusal",
                    ["refusal"] = $"recorded refusal {TestApiKey}"
                }
            };
        });
        var handler = new ScriptedHandler((_, _) => Task.FromResult(
            Response(HttpStatusCode.OK, responseBody, RequestId1)));
        var adapter = CreateAdapter(handler);

        var result = Assert.IsType<PerceptionFailure>(await adapter.ObserveAsync(Request()));

        Assert.Equal(PerceptionFailureCategory.InvalidResponse, result.Category);
        Assert.Single(handler.Requests);
        Assert.DoesNotContain(TestApiKey, SafeMetadata(result.Metadata), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("role", "user")]
    [InlineData("status", "incomplete")]
    public async Task Non_completed_assistant_message_is_terminal_without_repair(
        string propertyName,
        string value)
    {
        var responseBody = MutateValidResponse(root =>
            root["output"]![0]![propertyName] = value);
        var handler = new ScriptedHandler((_, _) => Task.FromResult(
            Response(HttpStatusCode.OK, responseBody, RequestId1)));
        var adapter = CreateAdapter(handler);

        var result = Assert.IsType<PerceptionFailure>(await adapter.ObserveAsync(Request()));

        Assert.Equal(PerceptionFailureCategory.InvalidResponse, result.Category);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Multiple_output_text_parts_are_rejected_as_ambiguous_without_repair()
    {
        var responseBody = MutateValidResponse(root =>
        {
            var content = root["output"]![0]!["content"]!.AsArray();
            content.Add(content[0]!.DeepClone());
        });
        var handler = new ScriptedHandler((_, _) => Task.FromResult(
            Response(HttpStatusCode.OK, responseBody, RequestId1)));
        var adapter = CreateAdapter(handler);

        var result = Assert.IsType<PerceptionFailure>(await adapter.ObserveAsync(Request()));

        Assert.Equal(PerceptionFailureCategory.InvalidResponse, result.Category);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Wrong_response_resource_discriminator_is_terminal_without_repair()
    {
        var responseBody = MutateValidResponse(root => root["object"] = "list");
        var handler = new ScriptedHandler((_, _) => Task.FromResult(
            Response(HttpStatusCode.OK, responseBody, RequestId1)));
        var adapter = CreateAdapter(handler);

        var result = Assert.IsType<PerceptionFailure>(await adapter.ObserveAsync(Request()));

        Assert.Equal(PerceptionFailureCategory.InvalidResponse, result.Category);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("{not-json")]
    [InlineData("{\"object\":\"response\",\"status\":\"completed\"}")]
    public async Task Malformed_or_missing_output_envelope_is_terminal_without_repair(
        string responseBody)
    {
        var handler = new ScriptedHandler((_, _) => Task.FromResult(
            Response(HttpStatusCode.OK, responseBody, RequestId1)));
        var adapter = CreateAdapter(handler);

        var result = Assert.IsType<PerceptionFailure>(await adapter.ObserveAsync(Request()));

        Assert.Equal(PerceptionFailureCategory.InvalidResponse, result.Category);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Oversized_response_is_terminal_without_repair(
        bool declaredLength)
    {
        var bytes = new byte[(1024 * 1024) + 1];
        HttpContent content = declaredLength
            ? new ByteArrayContent(bytes)
            : new UnknownLengthContent(bytes);
        var handler = new ScriptedHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content
            };
            response.Headers.Add("x-request-id", RequestId1);
            return Task.FromResult(response);
        });
        var adapter = CreateAdapter(handler);

        var result = Assert.IsType<PerceptionFailure>(await adapter.ObserveAsync(Request()));

        Assert.Equal(PerceptionFailureCategory.InvalidResponse, result.Category);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Repair_instruction_does_not_echo_unknown_provider_property_names()
    {
        const string canary = "IGNORE_PREVIOUS_INSTRUCTIONS_CANARY";
        var hostilePropertyName = $"{canary}_{new string('X', 2048)}";
        var responseBody = MutateValidObservation(root =>
            root[hostilePropertyName] = true);
        var handler = new ScriptedHandler(
            (_, _) => Task.FromResult(
                Response(HttpStatusCode.OK, responseBody, RequestId1)),
            Success("valid-response.json", RequestId2));
        var adapter = CreateAdapter(handler);

        var result = Assert.IsType<PerceptionSuccess>(await adapter.ObserveAsync(Request()));

        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("$ (UnknownField)", handler.Requests[1].Body, StringComparison.Ordinal);
        Assert.DoesNotContain(canary, handler.Requests[1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hostile_provider_metadata_falls_back_without_persisting_provider_text()
    {
        var responseBody = MutateValidResponse(root =>
            root["model"] = "gpt-5.6-luna-data:image/jpeg;base64,/9gB/9k=");
        var handler = new ScriptedHandler((_, _) => Task.FromResult(
            Response(
                HttpStatusCode.OK,
                responseBody,
                $"req_{TestApiKey}")));
        var adapter = CreateAdapter(handler);

        var result = Assert.IsType<PerceptionSuccess>(await adapter.ObserveAsync(Request()));
        var metadata = SafeMetadata(result.Metadata);

        Assert.Equal("gpt-5.6-luna", result.Metadata.Model);
        Assert.StartsWith("local-", result.Metadata.RequestId, StringComparison.Ordinal);
        Assert.DoesNotContain(TestApiKey, metadata, StringComparison.Ordinal);
        Assert.DoesNotContain("/9gB/9k=", metadata, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Singleton_adapter_requests_one_named_client_per_observation()
    {
        var handler = new ScriptedHandler(
            Success("valid-response.json", RequestId1),
            Success("valid-response.json", RequestId2));
        var factory = new RecordingHttpClientFactory(handler);
        var adapter = CreateAdapter(factory);

        Assert.IsType<PerceptionSuccess>(await adapter.ObserveAsync(Request()));
        Assert.IsType<PerceptionSuccess>(await adapter.ObserveAsync(Request()));

        Assert.Equal(2, factory.CreateCount);
        Assert.Equal(2, factory.DisposeCount);
        Assert.All(
            factory.Names,
            name => Assert.Equal(
                OpenAiPerceptionServiceCollectionExtensions.HttpClientName,
                name));
    }

    [Fact]
    public async Task Isolated_registration_resolves_the_port_from_the_published_configuration_keys()
    {
        var settings = new Dictionary<string, string?>
        {
            [OpenAiPerceptionOptions.ApiKeyConfigurationKey] = TestApiKey,
            [OpenAiPerceptionOptions.BaseUrlConfigurationKey] = "https://recorded.test/v1",
            [OpenAiPerceptionOptions.ModelConfigurationKey] = "gpt-5.6-luna",
            [OpenAiPerceptionOptions.ImageDetailConfigurationKey] = "low"
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
        var handler = new ScriptedHandler(Success("valid-response.json", RequestId1));
        var services = new ServiceCollection();
        services.AddOpenAiPerception(configuration);
        services.AddHttpClient(OpenAiPerceptionServiceCollectionExtensions.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        await using var provider = services.BuildServiceProvider();

        var port = provider.GetRequiredService<IPerceptionPort>();
        Assert.Same(port, provider.GetRequiredService<IPerceptionPort>());
        Assert.Same(port, provider.GetRequiredService<OpenAiPerceptionAdapter>());
        var result = await port.ObserveAsync(Request());

        Assert.IsType<PerceptionSuccess>(result);
        Assert.Equal(new Uri("https://recorded.test/v1/responses"), handler.Requests[0].Uri);
    }

    [Fact]
    public void Hosted_options_fail_safely_without_echoing_a_missing_or_invalid_credential()
    {
        var handler = new ScriptedHandler(Success("valid-response.json", "unused"));
        var exception = Assert.Throws<InvalidOperationException>(() =>
            CreateAdapter(handler, options => options.ApiKey = string.Empty));

        Assert.Contains(
            OpenAiPerceptionOptions.ApiKeyConfigurationKey,
            exception.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain(TestApiKey, exception.Message, StringComparison.Ordinal);
    }

    private static OpenAiPerceptionAdapter CreateAdapter(
        HttpMessageHandler handler,
        Action<OpenAiPerceptionOptions>? configure = null)
        => CreateAdapter(new RecordingHttpClientFactory(handler), configure);

    private static OpenAiPerceptionAdapter CreateAdapter(
        IHttpClientFactory httpClientFactory,
        Action<OpenAiPerceptionOptions>? configure = null)
    {
        var options = new OpenAiPerceptionOptions
        {
            ApiKey = TestApiKey,
            BaseUrl = new Uri("https://recorded.test/v1"),
            Model = "gpt-5.6-luna",
            ImageDetail = "low"
        };
        configure?.Invoke(options);
        return new(httpClientFactory, Options.Create(options));
    }

    private static PerceptionRequest Request() =>
        new(
            new byte[] { 0xff, 0xd8, 0x01, 0xff, 0xd9 },
            new PerceptionRequestOptions(maximumObjects: 2, maximumVisibleCues: 1));

    private static Func<CapturedRequest, CancellationToken, Task<HttpResponseMessage>> Success(
        string fixture,
        string requestId) =>
        (_, _) => Task.FromResult(Response(HttpStatusCode.OK, Fixture(fixture), requestId));

    private static HttpResponseMessage Response(
        HttpStatusCode status,
        string content,
        string requestId)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json")
        };
        response.Headers.Add("x-request-id", requestId);
        return response;
    }

    private static string Fixture(string name)
    {
        var assembly = typeof(HostedPerceptionAdapterTests).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(candidate => candidate.EndsWith(
                $".Fixtures.Perception.{name}",
                StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string MutateValidResponse(Action<JsonObject> mutate)
    {
        var root = JsonNode.Parse(Fixture("valid-response.json"))!.AsObject();
        mutate(root);
        return root.ToJsonString();
    }

    private static string MutateValidObservation(Action<JsonObject> mutate)
    {
        var envelope = JsonNode.Parse(Fixture("valid-response.json"))!.AsObject();
        var outputText = envelope["output"]![0]!["content"]![0]!["text"]!
            .GetValue<string>();
        var observation = JsonNode.Parse(outputText)!.AsObject();
        mutate(observation);
        envelope["output"]![0]!["content"]![0]!["text"] =
            observation.ToJsonString();
        return envelope.ToJsonString();
    }

    private static string SafeMetadata(PerceptionMetadata metadata) =>
        string.Join(
            '|',
            metadata.Provider,
            metadata.Model,
            metadata.PromptVersion,
            metadata.SchemaVersion,
            metadata.Latency,
            metadata.RequestId);

    private sealed record CapturedRequest(
        Uri? Uri,
        string Body,
        AuthenticationHeaderValue? Authorization);

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<Func<CapturedRequest, CancellationToken, Task<HttpResponseMessage>>>
            _responses;

        public ScriptedHandler(
            params Func<CapturedRequest, CancellationToken, Task<HttpResponseMessage>>[] responses)
        {
            _responses = new(responses);
        }

        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var captured = new CapturedRequest(
                request.RequestUri,
                request.Content is null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync(cancellationToken),
                request.Headers.Authorization);
            Requests.Add(captured);
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("No recorded response remains.");
            }

            return await _responses.Dequeue()(captured, cancellationToken);
        }
    }

    private sealed class RecordingHttpClientFactory(
        HttpMessageHandler handler) : IHttpClientFactory
    {
        public int CreateCount { get; private set; }

        public int DisposeCount { get; private set; }

        public List<string> Names { get; } = [];

        public HttpClient CreateClient(string name)
        {
            CreateCount++;
            Names.Add(name);
            return new TrackingHttpClient(
                handler,
                () => DisposeCount++)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
        }
    }

    private sealed class TrackingHttpClient(
        HttpMessageHandler handler,
        Action onDisposed) : HttpClient(handler, disposeHandler: false)
    {
        private bool _disposed;

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                onDisposed();
            }

            base.Dispose(disposing);
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
}
