using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GoalKeeper.Application.Perception;
using GoalKeeper.Application.Reasoning;
using GoalKeeper.Domain;
using GoalKeeper.Infrastructure.Reasoning;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GoalKeeper.Integration.Tests;

public sealed class HostedReasoningAdapterTests
{
    private const string TestApiKey = "test-key-never-log";
    private const string RequestId1 = "req_00000000000000000000000000000001";
    private const string RequestId2 = "req_00000000000000000000000000000002";
    private static readonly Guid SessionId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ContractId =
        Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid DeviationId =
        Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid ObservationId =
        Guid.Parse("44444444-4444-4444-4444-444444444444");

    [Fact]
    public async Task Recorded_continue_response_returns_proposal_and_exact_safe_request()
    {
        const string untrustedMarker = "UNTRUSTED_GOAL_MARKER";
        var handler = new ScriptedHandler(
            Success("valid-continue-response.json", RequestId1));
        var adapter = CreateAdapter(handler);

        var result = Assert.IsType<ReasoningSuccess>(
            await adapter.EvaluateAsync(Request(untrustedMarker)));

        Assert.Equal(ReasoningDecision.ContinueObserving, result.Proposal.Decision);
        Assert.Null(result.Proposal.Intervention);
        Assert.Equal("openai", result.Metadata.Provider);
        Assert.Equal("gpt-5.6-luna", result.Metadata.Model);
        Assert.Equal("reasoning-v1", result.Metadata.PromptVersion);
        Assert.Equal(RequestId1, result.Metadata.RequestId);
        Assert.Single(handler.Requests);

        using var payload = JsonDocument.Parse(handler.Requests[0].Body);
        var root = payload.RootElement;
        Assert.False(root.GetProperty("store").GetBoolean());
        Assert.Equal(32_768, root.GetProperty("max_output_tokens").GetInt32());
        Assert.Equal(
            "medium",
            root.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Empty(root.GetProperty("tools").EnumerateArray());
        Assert.Equal(
            "json_schema",
            root.GetProperty("text")
                .GetProperty("format")
                .GetProperty("type")
                .GetString());
        Assert.True(
            root.GetProperty("text")
                .GetProperty("format")
                .GetProperty("strict")
                .GetBoolean());
        Assert.Equal(
            SessionId.ToString("D"),
            root.GetProperty("text")
                .GetProperty("format")
                .GetProperty("schema")
                .GetProperty("properties")
                .GetProperty("session_id")
                .GetProperty("const")
                .GetString());

        var instructions = root.GetProperty("instructions").GetString()!;
        var inputText = root.GetProperty("input")[0]
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString()!;
        Assert.DoesNotContain(untrustedMarker, instructions, StringComparison.Ordinal);
        Assert.Contains(untrustedMarker, inputText, StringComparison.Ordinal);
        Assert.DoesNotContain("jpeg_bytes", inputText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("snapshot_path", inputText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("raw_body", inputText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Bearer", handler.Requests[0].Authorization?.Scheme);
        Assert.Equal(TestApiKey, handler.Requests[0].Authorization?.Parameter);
        Assert.DoesNotContain(TestApiKey, SafeMetadata(result.Metadata), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recorded_intervention_hydrates_episode_reference_times_from_request()
    {
        var handler = new ScriptedHandler(
            Success("valid-intervention-response.json", RequestId1));
        var adapter = CreateAdapter(handler);

        var result = Assert.IsType<ReasoningSuccess>(
            await adapter.EvaluateAsync(Request()));

        Assert.Equal(
            ReasoningDecision.BeginRecoveryCheckIn,
            result.Proposal.Decision);
        Assert.Equal(DeviationId, result.Proposal.Intervention!.ListedDeviationId);
        var episode = Assert.Single(result.Proposal.EpisodeUpdates);
        Assert.Equal(TimeSpan.FromSeconds(2), episode.FirstObservation.CapturedAtMonotonic);
        Assert.Equal(TimeSpan.FromSeconds(2), episode.LatestObservation.CapturedAtMonotonic);
        Assert.Equal(
            TimeSpan.FromSeconds(2),
            Assert.Single(episode.KeyObservations).CapturedAtMonotonic);
    }

    [Fact]
    public async Task One_invalid_output_is_repaired_once_without_echoing_provider_content()
    {
        var handler = new ScriptedHandler(
            Success("invalid-response.json", RequestId1),
            Success("valid-continue-response.json", RequestId2));
        var factory = new RecordingHttpClientFactory(handler);
        var adapter = CreateAdapter(factory);

        var result = Assert.IsType<ReasoningSuccess>(
            await adapter.EvaluateAsync(Request()));

        Assert.Equal(RequestId2, result.Metadata.RequestId);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(1, factory.CreateCount);
        Assert.Equal(1, factory.DisposeCount);
        Assert.Contains(
            "invalid_output_shape",
            handler.Requests[1].Body,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "IGNORE_PREVIOUS_INSTRUCTIONS_CANARY",
            handler.Requests[1].Body,
            StringComparison.Ordinal);
        Assert.Equal(
            InputRequestText(handler.Requests[0].Body),
            InputRequestText(handler.Requests[1].Body));
    }

    [Fact]
    public async Task Two_invalid_outputs_return_bounded_reasoning_invalid()
    {
        var handler = new ScriptedHandler(
            Success("invalid-response.json", RequestId1),
            Success("invalid-response.json", RequestId2));
        var adapter = CreateAdapter(handler);

        var result = Assert.IsType<ReasoningInvalid>(
            await adapter.EvaluateAsync(Request()));

        Assert.Equal(["invalid_output_shape"], result.ValidationReasons);
        Assert.Equal(2, handler.Requests.Count);
        Assert.DoesNotContain(
            "IGNORE_PREVIOUS_INSTRUCTIONS_CANARY",
            string.Join('|', result.ValidationReasons),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("incomplete")]
    [InlineData("failed")]
    [InlineData("in_progress")]
    public async Task Non_completed_lifecycle_is_terminal_without_repair(
        string status)
    {
        var body = MutateValidResponse(root => root["status"] = status);
        var handler = new ScriptedHandler((_, _) => Task.FromResult(
            Response(HttpStatusCode.OK, body, RequestId1)));
        var adapter = CreateAdapter(handler);

        var result = Assert.IsType<ReasoningFailure>(
            await adapter.EvaluateAsync(Request()));

        Assert.Equal(ReasoningFailureCategory.InvalidResponse, result.Category);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Refusal_is_terminal_without_repair_or_metadata_leak()
    {
        var body = MutateValidResponse(root =>
        {
            root["output"]![1]!["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "refusal",
                    ["refusal"] = $"recorded refusal {TestApiKey}"
                }
            };
        });
        var handler = new ScriptedHandler((_, _) => Task.FromResult(
            Response(HttpStatusCode.OK, body, RequestId1)));
        var adapter = CreateAdapter(handler);

        var result = Assert.IsType<ReasoningFailure>(
            await adapter.EvaluateAsync(Request()));

        Assert.Equal(ReasoningFailureCategory.InvalidResponse, result.Category);
        Assert.Single(handler.Requests);
        Assert.DoesNotContain(TestApiKey, SafeMetadata(result.Metadata), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ambiguous_output_is_terminal_without_repair()
    {
        var body = MutateValidResponse(root =>
        {
            var content = root["output"]![1]!["content"]!.AsArray();
            content.Add(content[0]!.DeepClone());
        });
        var handler = new ScriptedHandler((_, _) => Task.FromResult(
            Response(HttpStatusCode.OK, body, RequestId1)));
        var adapter = CreateAdapter(handler);

        var result = Assert.IsType<ReasoningFailure>(
            await adapter.EvaluateAsync(Request()));

        Assert.Equal(ReasoningFailureCategory.InvalidResponse, result.Category);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("{not-json")]
    [InlineData("{\"object\":\"response\",\"status\":\"completed\"}")]
    public async Task Malformed_or_missing_output_envelope_is_terminal_without_repair(
        string body)
    {
        var handler = new ScriptedHandler((_, _) => Task.FromResult(
            Response(HttpStatusCode.OK, body, RequestId1)));
        var adapter = CreateAdapter(handler);

        var result = Assert.IsType<ReasoningFailure>(
            await adapter.EvaluateAsync(Request()));

        Assert.Equal(ReasoningFailureCategory.InvalidResponse, result.Category);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Wrong_response_resource_is_terminal_without_repair()
    {
        var body = MutateValidResponse(root => root["object"] = "list");
        var handler = new ScriptedHandler((_, _) => Task.FromResult(
            Response(HttpStatusCode.OK, body, RequestId1)));
        var adapter = CreateAdapter(handler);

        var result = Assert.IsType<ReasoningFailure>(
            await adapter.EvaluateAsync(Request()));

        Assert.Equal(ReasoningFailureCategory.InvalidResponse, result.Category);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Oversized_provider_response_is_terminal_without_repair(
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

        var result = Assert.IsType<ReasoningFailure>(
            await adapter.EvaluateAsync(Request()));

        Assert.Equal(ReasoningFailureCategory.InvalidResponse, result.Category);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Structurally_valid_semantic_proposal_is_left_for_gk008_validation()
    {
        var body = MutateValidOutput(output =>
        {
            output["decision"] = "begin_recovery_check_in";
            output["intervention"] = null;
        });
        var handler = new ScriptedHandler((_, _) => Task.FromResult(
            Response(HttpStatusCode.OK, body, RequestId1)));
        var adapter = CreateAdapter(handler);

        var result = Assert.IsType<ReasoningSuccess>(
            await adapter.EvaluateAsync(Request()));

        Assert.Equal(
            ReasoningDecision.BeginRecoveryCheckIn,
            result.Proposal.Decision);
        Assert.Null(result.Proposal.Intervention);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, ReasoningFailureCategory.Authentication)]
    [InlineData(HttpStatusCode.Forbidden, ReasoningFailureCategory.Authentication)]
    [InlineData(HttpStatusCode.RequestTimeout, ReasoningFailureCategory.Timeout)]
    [InlineData(HttpStatusCode.TooManyRequests, ReasoningFailureCategory.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, ReasoningFailureCategory.ProviderUnavailable)]
    public async Task Provider_statuses_are_typed_without_exposing_error_body(
        HttpStatusCode status,
        ReasoningFailureCategory expected)
    {
        var handler = new ScriptedHandler((_, _) => Task.FromResult(
            Response(status, $"secret provider body {TestApiKey}", RequestId1)));
        var adapter = CreateAdapter(handler);

        var result = Assert.IsType<ReasoningFailure>(
            await adapter.EvaluateAsync(Request()));

        Assert.Equal(expected, result.Category);
        Assert.Single(handler.Requests);
        Assert.DoesNotContain(TestApiKey, SafeMetadata(result.Metadata), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Network_exception_returns_safe_network_failure()
    {
        var handler = new ScriptedHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(
                new HttpRequestException($"simulated {TestApiKey}")));
        var adapter = CreateAdapter(handler);

        var result = Assert.IsType<ReasoningFailure>(
            await adapter.EvaluateAsync(Request()));

        Assert.Equal(ReasoningFailureCategory.Network, result.Category);
        Assert.DoesNotContain(TestApiKey, SafeMetadata(result.Metadata), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Total_request_timeout_returns_typed_timeout()
    {
        var handler = new ScriptedHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var adapter = CreateAdapter(
            handler,
            options => options.RequestTimeout = TimeSpan.FromMilliseconds(20));

        var result = Assert.IsType<ReasoningFailure>(
            await adapter.EvaluateAsync(Request()));

        Assert.Equal(ReasoningFailureCategory.Timeout, result.Category);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Caller_cancellation_is_propagated()
    {
        var handler = new ScriptedHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var adapter = CreateAdapter(handler);
        using var cancellation = new CancellationTokenSource();

        var operation = adapter.EvaluateAsync(Request(), cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Oversized_direct_request_fails_before_creating_http_client()
    {
        var handler = new ScriptedHandler();
        var factory = new RecordingHttpClientFactory(handler);
        var adapter = CreateAdapter(factory);

        var result = Assert.IsType<ReasoningFailure>(
            await adapter.EvaluateAsync(Request(new string('X', 600_000))));

        Assert.Equal(ReasoningFailureCategory.InvalidResponse, result.Category);
        Assert.Equal(0, factory.CreateCount);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Malformed_nested_request_fails_before_creating_http_client()
    {
        var handler = new ScriptedHandler();
        var factory = new RecordingHttpClientFactory(handler);
        var adapter = CreateAdapter(factory);
        var valid = Request();
        var malformed = new ReasoningRequest(
            valid.SessionId,
            valid.SessionVersion,
            valid.CurrentState,
            valid.Contract,
            valid.DeviationOverrides,
            valid.ActiveEpisodes,
            valid.HistoricalEpisodes,
            valid.RecoverySummaries,
            valid.PriorDecisions,
            valid.NewObservation,
            new ReasoningObservation[] { null! });

        var result = Assert.IsType<ReasoningFailure>(
            await adapter.EvaluateAsync(malformed));

        Assert.Equal(ReasoningFailureCategory.InvalidResponse, result.Category);
        Assert.Equal(0, factory.CreateCount);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Hostile_provider_metadata_uses_safe_local_fallbacks()
    {
        var body = MutateValidResponse(root =>
            root["model"] = "gpt-5.6-luna-data:image/jpeg;base64,SECRET");
        var handler = new ScriptedHandler((_, _) => Task.FromResult(
            Response(HttpStatusCode.OK, body, $"req_{TestApiKey}")));
        var adapter = CreateAdapter(handler);

        var result = Assert.IsType<ReasoningSuccess>(
            await adapter.EvaluateAsync(Request()));
        var metadata = SafeMetadata(result.Metadata);

        Assert.Equal("gpt-5.6-luna", result.Metadata.Model);
        Assert.StartsWith("local-", result.Metadata.RequestId, StringComparison.Ordinal);
        Assert.DoesNotContain(TestApiKey, metadata, StringComparison.Ordinal);
        Assert.DoesNotContain("base64", metadata, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Isolated_registration_resolves_singleton_port_and_named_client()
    {
        var settings = new Dictionary<string, string?>
        {
            [OpenAiReasoningOptions.ApiKeyConfigurationKey] = TestApiKey,
            [OpenAiReasoningOptions.BaseUrlConfigurationKey] = "https://recorded.test/v1",
            [OpenAiReasoningOptions.ModelConfigurationKey] = "gpt-5.6-luna",
            [OpenAiReasoningOptions.EffortConfigurationKey] = "medium"
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
        var handler = new ScriptedHandler(
            Success("valid-continue-response.json", RequestId1));
        var services = new ServiceCollection();
        services.AddOpenAiReasoning(configuration);
        services.AddHttpClient(OpenAiReasoningServiceCollectionExtensions.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        await using var provider = services.BuildServiceProvider();

        var port = provider.GetRequiredService<IReasoningPort>();
        Assert.Same(port, provider.GetRequiredService<IReasoningPort>());
        Assert.Same(port, provider.GetRequiredService<OpenAiReasoningAdapter>());
        var result = await port.EvaluateAsync(Request());

        Assert.IsType<ReasoningSuccess>(result);
        Assert.Equal(new Uri("https://recorded.test/v1/responses"), handler.Requests[0].Uri);
    }

    [Fact]
    public void Invalid_options_name_only_the_safe_configuration_key()
    {
        var handler = new ScriptedHandler();
        var exception = Assert.Throws<InvalidOperationException>(() =>
            CreateAdapter(handler, options => options.ApiKey = string.Empty));

        Assert.Contains(
            OpenAiReasoningOptions.ApiKeyConfigurationKey,
            exception.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain(TestApiKey, exception.Message, StringComparison.Ordinal);
    }

    private static ReasoningRequest Request(string goalTitle = "Write")
    {
        var observation = new ReasoningObservation(
            ObservationId,
            3,
            new DateTimeOffset(2026, 7, 18, 22, 0, 0, TimeSpan.Zero),
            TimeSpan.FromSeconds(2),
            new Observation(
                ObservationSchemaVersions.V1,
                new(ImageQualityValue.Adequate, []),
                new(PeopleCountStatus.Counted, 1, VisualSupport.Direct, []),
                ["laptop"],
                []));
        return new(
            SessionId,
            3,
            FocusSessionState.Focusing,
            new(
                ContractId,
                goalTitle,
                null,
                TimeSpan.FromMinutes(25),
                [new(DeviationId, "Phone use", VisualObservability.Observable)],
                ReasoningMode.Exploratory,
                Sensitivity.Balanced),
            [],
            [],
            [],
            [],
            [],
            observation,
            [observation]);
    }

    private static OpenAiReasoningAdapter CreateAdapter(
        HttpMessageHandler handler,
        Action<OpenAiReasoningOptions>? configure = null) =>
        CreateAdapter(new RecordingHttpClientFactory(handler), configure);

    private static OpenAiReasoningAdapter CreateAdapter(
        IHttpClientFactory factory,
        Action<OpenAiReasoningOptions>? configure = null)
    {
        var options = new OpenAiReasoningOptions
        {
            ApiKey = TestApiKey,
            BaseUrl = new Uri("https://recorded.test/v1"),
            Model = "gpt-5.6-luna",
            Effort = "medium"
        };
        configure?.Invoke(options);
        return new(factory, Options.Create(options));
    }

    private static Func<CapturedRequest, CancellationToken, Task<HttpResponseMessage>> Success(
        string fixture,
        string requestId) =>
        (_, _) => Task.FromResult(
            Response(HttpStatusCode.OK, Fixture(fixture), requestId));

    private static HttpResponseMessage Response(
        HttpStatusCode status,
        string content,
        string requestId)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json")
        };
        response.Headers.TryAddWithoutValidation("x-request-id", requestId);
        return response;
    }

    private static string Fixture(string name)
    {
        var assembly = typeof(HostedReasoningAdapterTests).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(candidate => candidate.EndsWith(
                $".Fixtures.Reasoning.{name}",
                StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string MutateValidResponse(Action<JsonObject> mutate)
    {
        var root = JsonNode.Parse(Fixture("valid-continue-response.json"))!.AsObject();
        mutate(root);
        return root.ToJsonString();
    }

    private static string MutateValidOutput(Action<JsonObject> mutate)
    {
        var envelope = JsonNode.Parse(
            Fixture("valid-continue-response.json"))!.AsObject();
        var outputText = envelope["output"]![1]!["content"]![0]!["text"]!
            .GetValue<string>();
        var output = JsonNode.Parse(outputText)!.AsObject();
        mutate(output);
        envelope["output"]![1]!["content"]![0]!["text"] =
            output.ToJsonString();
        return envelope.ToJsonString();
    }

    private static string InputRequestText(string requestBody)
    {
        using var payload = JsonDocument.Parse(requestBody);
        return payload.RootElement.GetProperty("input")[0]
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString()!;
    }

    private static string SafeMetadata(ReasoningMetadata metadata) =>
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

        public HttpClient CreateClient(string name)
        {
            Assert.Equal(OpenAiReasoningServiceCollectionExtensions.HttpClientName, name);
            CreateCount++;
            return new TrackingHttpClient(handler, () => DisposeCount++)
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
