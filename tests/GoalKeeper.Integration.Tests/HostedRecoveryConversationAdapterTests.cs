using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GoalKeeper.Application.Recovery;
using GoalKeeper.Domain;
using GoalKeeper.Infrastructure.Recovery.Conversation;

namespace GoalKeeper.Integration.Tests;

public sealed class HostedRecoveryConversationAdapterTests
{
    private const string TestApiKey = "test-key-never-log";
    private const string RequestId1 =
        "req_00000000000000000000000000000001";
    private const string RequestId2 =
        "req_00000000000000000000000000000002";

    private static readonly Guid SessionId =
        Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid ContractId =
        Guid.Parse("20000000-0000-0000-0000-000000000002");
    private static readonly Guid GoalId =
        Guid.Parse("30000000-0000-0000-0000-000000000003");
    private static readonly Guid InterventionId =
        Guid.Parse("40000000-0000-0000-0000-000000000004");
    private static readonly Guid DeviationId =
        Guid.Parse("50000000-0000-0000-0000-000000000005");
    private static readonly DateTimeOffset RequestedAtUtc =
        new(2020, 7, 18, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("recommit-response.json", RecoveryOutcome.Recommit)]
    [InlineData(
        "behavior-clarification-response.json",
        RecoveryOutcome.BehaviorClarification)]
    [InlineData("end-early-response.json", RecoveryOutcome.EndEarly)]
    [InlineData(
        "continue-session-response.json",
        RecoveryOutcome.ContinueSession)]
    [InlineData(
        "additional-coaching-response.json",
        RecoveryOutcome.AdditionalCoaching)]
    [InlineData("unclear-response.json", RecoveryOutcome.UnclearResponse)]
    [InlineData("no-response.json", RecoveryOutcome.NoResponse)]
    public async Task Recorded_outcomes_are_locally_stamped_and_validated(
        string fixture,
        RecoveryOutcome outcome)
    {
        var request = Request(DefaultTranscript(outcome));
        var handler = new ScriptedHandler(
            Success(fixture, RequestId1));
        var adapter = CreateAdapter(handler);

        var result = Assert.IsType<RecoveryProposalResponse>(
            await adapter.ProposeAsync(request));
        var proposal = result.Proposal;

        Assert.Equal(outcome, proposal.Outcome);
        Assert.Equal(request.SessionId, proposal.SessionId);
        Assert.Equal(request.SessionVersion, proposal.SessionVersion);
        Assert.Equal(
            request.Intervention.InterventionId,
            proposal.InterventionId);
        Assert.Equal(request.NextTurnNumber, proposal.TurnNumber);
        Assert.Equal(request.CurrentTranscript, proposal.Transcript);
        Assert.NotNull(proposal.Timing);
        Assert.True(
            proposal.Timing!.StartedAtUtc >= request.RequestedAtUtc);
        Assert.True(
            proposal.Timing.CompletedAtUtc >=
            proposal.Timing.StartedAtUtc);
        Assert.Equal("openai", proposal.Metadata!.Provider);
        Assert.Equal("gpt-5.6-terra", proposal.Metadata.Model);
        Assert.Equal(
            "recovery-conversation-v2",
            proposal.Metadata.PromptVersion);
        Assert.Equal(
            RecoverySchemaVersions.V1,
            proposal.Metadata.SchemaVersion);
        Assert.Equal(RequestId1, proposal.Metadata.RequestId);
        Assert.IsType<ValidRecoveryProposal>(
            RecoveryProposalValidator.Validate(request, proposal));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Request_uses_exact_terra_contract_and_only_bounded_context()
    {
        const string untrustedMarker = "UNTRUSTED_GOAL_MARKER";
        var handler = new ScriptedHandler(
            Success("recommit-response.json", RequestId1));
        var adapter = CreateAdapter(handler);

        await adapter.ProposeAsync(
            Request("I will return to the report.", untrustedMarker));

        var captured = Assert.Single(handler.Requests);
        Assert.Equal(
            new Uri("https://recorded.test/v1/responses"),
            captured.Uri);
        Assert.Equal("Bearer", captured.Authorization!.Scheme);
        Assert.Equal(TestApiKey, captured.Authorization.Parameter);
        Assert.StartsWith(
            "local-",
            captured.ClientRequestId,
            StringComparison.Ordinal);

        using var document = JsonDocument.Parse(captured.Body);
        var root = document.RootElement;
        Assert.Equal("gpt-5.6-terra", root.GetProperty("model").GetString());
        Assert.False(root.GetProperty("store").GetBoolean());
        Assert.Equal(
            "low",
            root.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Equal(4096, root.GetProperty("max_output_tokens").GetInt32());
        Assert.Empty(root.GetProperty("tools").EnumerateArray());

        var format = root.GetProperty("text").GetProperty("format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.Equal("recovery_proposal_v1", format.GetProperty("name").GetString());
        Assert.True(format.GetProperty("strict").GetBoolean());
        var schema = format.GetProperty("schema");
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(
            4,
            schema.GetProperty("required").GetArrayLength());
        Assert.Equal(
            280,
            schema.GetProperty("properties")
                .GetProperty("assistant_message")
                .GetProperty("maxLength")
                .GetInt32());

        var inputText = InputText(captured.Body);
        Assert.Contains(untrustedMarker, inputText, StringComparison.Ordinal);
        Assert.DoesNotContain("\"session_id\"", inputText, StringComparison.Ordinal);
        Assert.DoesNotContain("\"intervention_id\"", inputText, StringComparison.Ordinal);
        Assert.DoesNotContain("\"metadata\"", inputText, StringComparison.Ordinal);
        Assert.DoesNotContain("\"timing\"", inputText, StringComparison.Ordinal);
        Assert.DoesNotContain("\"raw_audio\"", inputText, StringComparison.Ordinal);
        Assert.DoesNotContain("\"image\"", inputText, StringComparison.Ordinal);
        Assert.Contains("\"current_transcript\"", inputText, StringComparison.Ordinal);
        Assert.Contains("\"allowed_outcomes\"", inputText, StringComparison.Ordinal);
        Assert.Contains(
            "\"accountability_message\":\"The phone has had its turn. Put it down and finish the report.\"",
            inputText,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Semantic_failure_is_repaired_once_with_frozen_input_and_safe_codes()
    {
        var handler = new ScriptedHandler(
            Success("invalid-coaching-response.json", RequestId1),
            Success("additional-coaching-response.json", RequestId2));
        var adapter = CreateAdapter(handler);
        var request = Request(
            $"Help me choose the next small step. {TestApiKey}");

        var result = Assert.IsType<RecoveryProposalResponse>(
            await adapter.ProposeAsync(request));

        Assert.Equal(RecoveryOutcome.AdditionalCoaching, result.Proposal.Outcome);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(
            InputText(handler.Requests[0].Body),
            InputText(handler.Requests[1].Body));
        var repair = RepairText(handler.Requests[1].Body);
        Assert.Contains("missing_field", repair, StringComparison.Ordinal);
        Assert.DoesNotContain(TestApiKey, repair, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "assistant_message",
            repair,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Coaching_cap_is_enforced_and_repaired_with_safe_codes()
    {
        var request = Request("I still need help.");
        var turns = Enumerable.Range(1, 3)
            .Select(turn => new RecoveryTurn(
                Guid.NewGuid(),
                request.SessionId,
                request.SessionVersion,
                request.Intervention.InterventionId,
                turn,
                RecoveryOutcome.AdditionalCoaching,
                $"Coaching response {turn}.",
                null,
                "Choose one smaller next action.",
                false,
                new(
                    request.RequestedAtUtc.AddMinutes(-1),
                    request.RequestedAtUtc
                        .AddMinutes(-1)
                        .AddMilliseconds(turn)),
                new(
                    "deterministic-fake",
                    "recovery-v1",
                    "recovery-v1",
                    RecoverySchemaVersions.V1,
                    TimeSpan.Zero,
                    $"local_{turn}")))
            .ToArray();
        request = new RecoveryRequest(
            request.SessionId,
            request.SessionVersion,
            request.Contract,
            request.Intervention,
            request.DisputedInterval,
            request.ActiveOverrides,
            request.AllowedOutcomes
                .Where(outcome =>
                    outcome != RecoveryOutcome.AdditionalCoaching)
                .ToArray(),
            turns,
            request.CurrentTranscript,
            request.RequestedAtUtc,
            new RecoveryRequestOptions(maximumCoachingTurns: 3));
        var handler = new ScriptedHandler(
            Success("additional-coaching-response.json", RequestId1),
            Success("recommit-response.json", RequestId2));
        var adapter = CreateAdapter(handler);

        var result = Assert.IsType<RecoveryProposalResponse>(
            await adapter.ProposeAsync(request));

        Assert.Equal(RecoveryOutcome.Recommit, result.Proposal.Outcome);
        var repair = RepairText(handler.Requests[1].Body);
        Assert.Contains(
            "outcome_not_allowed",
            repair,
            StringComparison.Ordinal);
        Assert.Contains(
            "coaching_cap_reached",
            repair,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_output_shape_is_repaired_once()
    {
        var malformed = MutateOutput(
            "recommit-response.json",
            output => output["unexpected"] = true);
        var handler = new ScriptedHandler(
            (_, _) => Task.FromResult(
                Response(HttpStatusCode.OK, malformed, RequestId1)),
            Success("recommit-response.json", RequestId2));
        var adapter = CreateAdapter(handler);

        var result = Assert.IsType<RecoveryProposalResponse>(
            await adapter.ProposeAsync(
                Request("I will return to the report.")));

        Assert.Equal(RecoveryOutcome.Recommit, result.Proposal.Outcome);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains(
            "invalid_output_shape",
            RepairText(handler.Requests[1].Body),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Provider_bound_violation_is_repaired_instead_of_escaping()
    {
        var oversized = MutateOutput(
            "unclear-response.json",
            output => output["assistant_message"] =
                new string(
                    'x',
                    RecoveryLimits.MaximumResponseLength + 1));
        var handler = new ScriptedHandler(
            (_, _) => Task.FromResult(
                Response(HttpStatusCode.OK, oversized, RequestId1)),
            Success("unclear-response.json", RequestId2));
        var adapter = CreateAdapter(handler);

        var result = Assert.IsType<RecoveryProposalResponse>(
            await adapter.ProposeAsync(Request("I am not sure.")));

        Assert.Equal(RecoveryOutcome.UnclearResponse, result.Proposal.Outcome);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains(
            "invalid_value",
            RepairText(handler.Requests[1].Body),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Second_invalid_proposal_is_terminal()
    {
        var handler = new ScriptedHandler(
            Success("invalid-coaching-response.json", RequestId1),
            Success("invalid-coaching-response.json", RequestId2));
        var adapter = CreateAdapter(handler);

        var result = Assert.IsType<RecoveryFailureResponse>(
            await adapter.ProposeAsync(
                Request("Help me choose the next small step.")));

        Assert.Equal(RecoveryFailureCategory.InvalidResponse, result.Category);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, RecoveryFailureCategory.Authentication)]
    [InlineData(HttpStatusCode.Forbidden, RecoveryFailureCategory.Authentication)]
    [InlineData(HttpStatusCode.RequestTimeout, RecoveryFailureCategory.Timeout)]
    [InlineData(HttpStatusCode.GatewayTimeout, RecoveryFailureCategory.Timeout)]
    [InlineData(HttpStatusCode.TooManyRequests, RecoveryFailureCategory.RateLimited)]
    [InlineData(HttpStatusCode.BadRequest, RecoveryFailureCategory.InvalidResponse)]
    [InlineData(
        HttpStatusCode.InternalServerError,
        RecoveryFailureCategory.ProviderUnavailable)]
    public async Task Provider_statuses_are_typed_without_exposing_body(
        HttpStatusCode status,
        RecoveryFailureCategory category)
    {
        var handler = new ScriptedHandler((_, _) => Task.FromResult(
            Response(
                status,
                $"secret provider body {TestApiKey}",
                RequestId1)));
        var adapter = CreateAdapter(handler);

        var result = Assert.IsType<RecoveryFailureResponse>(
            await adapter.ProposeAsync(Request()));

        Assert.Equal(category, result.Category);
        Assert.DoesNotContain(
            TestApiKey,
            SafeMetadata(result.Metadata),
            StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Network_exception_returns_safe_network_failure()
    {
        var handler = new ScriptedHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(
                new HttpRequestException($"simulated {TestApiKey}")));
        var adapter = CreateAdapter(handler);

        var result = Assert.IsType<RecoveryFailureResponse>(
            await adapter.ProposeAsync(Request()));

        Assert.Equal(RecoveryFailureCategory.Network, result.Category);
        Assert.DoesNotContain(
            TestApiKey,
            SafeMetadata(result.Metadata),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Total_timeout_covers_provider_call()
    {
        var handler = new ScriptedHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var adapter = CreateAdapter(
            handler,
            options =>
                options.RequestTimeout = TimeSpan.FromMilliseconds(20));

        var result = Assert.IsType<RecoveryFailureResponse>(
            await adapter.ProposeAsync(Request()));

        Assert.Equal(RecoveryFailureCategory.Timeout, result.Category);
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

        var operation = adapter.ProposeAsync(
            Request(),
            cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => operation);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Hostile_provider_metadata_uses_local_safe_fallbacks()
    {
        var body = MutateEnvelope(
            "recommit-response.json",
            root =>
                root["model"] =
                    "gpt-5.6-terra-data:image/jpeg;base64,SECRET");
        var handler = new ScriptedHandler((_, _) => Task.FromResult(
            Response(HttpStatusCode.OK, body, $"req_{TestApiKey}:secret")));
        var adapter = CreateAdapter(handler);

        var result = Assert.IsType<RecoveryProposalResponse>(
            await adapter.ProposeAsync(
                Request("I will return to the report.")));
        var metadata = result.Proposal.Metadata!;

        Assert.Equal("gpt-5.6-terra", metadata.Model);
        Assert.StartsWith(
            "local-",
            metadata.RequestId,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            TestApiKey,
            SafeMetadata(metadata),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "base64",
            SafeMetadata(metadata),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Oversized_provider_response_is_rejected(
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
            response.Headers.TryAddWithoutValidation(
                "x-request-id",
                RequestId1);
            return Task.FromResult(response);
        });
        var adapter = CreateAdapter(handler);

        var result = Assert.IsType<RecoveryFailureResponse>(
            await adapter.ProposeAsync(Request()));

        Assert.Equal(RecoveryFailureCategory.InvalidResponse, result.Category);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Maximum_transcript_is_sent_without_truncation()
    {
        var transcript = new string('T', RecoveryLimits.MaximumTranscriptLength);
        var handler = new ScriptedHandler(
            Success("recommit-response.json", RequestId1));
        var adapter = CreateAdapter(handler);

        var result = Assert.IsType<RecoveryProposalResponse>(
            await adapter.ProposeAsync(Request(transcript)));

        Assert.Equal(transcript, result.Proposal.Transcript);
        Assert.Contains(
            transcript,
            InputText(Assert.Single(handler.Requests).Body),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("refusal")]
    [InlineData("incomplete")]
    [InlineData("unexpected_item")]
    public async Task Invalid_response_envelopes_are_terminal_without_repair(
        string mutation)
    {
        var body = MutateEnvelope(
            "recommit-response.json",
            root =>
            {
                if (mutation == "refusal")
                {
                    root["output"]![1]!["content"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "refusal",
                            ["refusal"] = $"secret {TestApiKey}"
                        }
                    };
                }
                else if (mutation == "incomplete")
                {
                    root["status"] = "incomplete";
                    root["incomplete_details"] = new JsonObject
                    {
                        ["reason"] = "max_output_tokens"
                    };
                }
                else
                {
                    root["output"]!.AsArray().Insert(
                        0,
                        new JsonObject
                        {
                            ["type"] = "function_call"
                        });
                }
            });
        var handler = new ScriptedHandler((_, _) => Task.FromResult(
            Response(HttpStatusCode.OK, body, RequestId1)));
        var adapter = CreateAdapter(handler);

        var result = Assert.IsType<RecoveryFailureResponse>(
            await adapter.ProposeAsync(Request()));

        Assert.Equal(RecoveryFailureCategory.InvalidResponse, result.Category);
        Assert.Single(handler.Requests);
        Assert.DoesNotContain(
            TestApiKey,
            SafeMetadata(result.Metadata),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_options_fail_without_exposing_secret()
    {
        var handler = new ScriptedHandler();
        var exception = Assert.Throws<InvalidOperationException>(() =>
            CreateAdapter(handler, options => options.Effort = "medium"));

        Assert.Contains(
            OpenAiRecoveryConversationOptions.EffortConfigurationKey,
            exception.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            TestApiKey,
            exception.Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://user:password@recorded.test/v1")]
    [InlineData("https://recorded.test/v1?tenant=unsafe")]
    [InlineData("https://recorded.test/v1#unsafe")]
    public void Unsafe_base_urls_are_rejected(string baseUrl)
    {
        var handler = new ScriptedHandler();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            CreateAdapter(
                handler,
                options => options.BaseUrl = new Uri(baseUrl)));

        Assert.Contains(
            OpenAiRecoveryConversationOptions.BaseUrlConfigurationKey,
            exception.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            TestApiKey,
            exception.Message,
            StringComparison.Ordinal);
    }

    private static RecoveryRequest Request(
        string? transcript = "I am ready to continue.",
        string goalTitle = "Finish the quarterly report") =>
        new(
            SessionId,
            7,
            new RecoveryContractContext(
                ContractId,
                GoalId,
                goalTitle,
                "Draft and verify the final report.",
                TimeSpan.FromMinutes(50),
                ReasoningMode.ProfileOnly,
                Sensitivity.Balanced),
            new RecoveryInterventionContext(
                InterventionId,
                DeviationId,
                "Sustained attention to a phone",
                "Phone-directed posture remained visible across three observations.",
                "The cited pattern may conflict with the confirmed Goal.",
                RequestedAtUtc.AddMinutes(-1),
                "The phone has had its turn. Put it down and finish the report."),
            new RecoveryDisputedInterval(
                TimeSpan.FromMinutes(10),
                TimeSpan.FromMinutes(12)),
            [
                new RecoveryOverrideContext(
                    Guid.Parse(
                        "60000000-0000-0000-0000-000000000006"),
                    DeviationId,
                    "Sustained attention to a phone",
                    "Authenticator use is allowed.",
                    RequestedAtUtc.AddMinutes(-5))
            ],
            Enum.GetValues<RecoveryOutcome>(),
            [],
            transcript,
            RequestedAtUtc,
            new RecoveryRequestOptions());

    private static string? DefaultTranscript(RecoveryOutcome outcome) =>
        outcome switch
        {
            RecoveryOutcome.Recommit =>
                "I will return to the report.",
            RecoveryOutcome.BehaviorClarification =>
                "I used the phone to approve the report upload.",
            RecoveryOutcome.EndEarly =>
                "I need to end this Focus Session.",
            RecoveryOutcome.ContinueSession =>
                "I explicitly want to continue.",
            RecoveryOutcome.AdditionalCoaching =>
                "Help me choose the next small step.",
            RecoveryOutcome.UnclearResponse =>
                "I am not sure.",
            RecoveryOutcome.NoResponse => null,
            _ => throw new ArgumentOutOfRangeException(nameof(outcome))
        };

    private static OpenAiRecoveryConversationAdapter CreateAdapter(
        HttpMessageHandler handler,
        Action<OpenAiRecoveryConversationOptions>? configure = null) =>
        CreateAdapter(
            new RecordingHttpClientFactory(handler),
            configure);

    private static OpenAiRecoveryConversationAdapter CreateAdapter(
        IHttpClientFactory factory,
        Action<OpenAiRecoveryConversationOptions>? configure = null)
    {
        var options = new OpenAiRecoveryConversationOptions
        {
            ApiKey = TestApiKey,
            BaseUrl = new Uri("https://recorded.test/v1"),
            Model = "gpt-5.6-terra",
            Effort = "low"
        };
        configure?.Invoke(options);
        return new(factory, options);
    }

    private static Func<
        CapturedRequest,
        CancellationToken,
        Task<HttpResponseMessage>> Success(
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
            Content = new StringContent(
                content,
                Encoding.UTF8,
                "application/json")
        };
        response.Headers.TryAddWithoutValidation(
            "x-request-id",
            requestId);
        return response;
    }

    private static string Fixture(string name) =>
        File.ReadAllText(
            Path.Combine(
                RepositoryRoot(),
                "tests",
                "GoalKeeper.Integration.Tests",
                "Fixtures",
                "Recovery",
                "Conversation",
                name));

    private static string RepositoryRoot()
    {
        DirectoryInfo? candidate = new(AppContext.BaseDirectory);
        while (candidate is not null)
        {
            if (File.Exists(Path.Combine(candidate.FullName, "GoalKeeper.sln")))
            {
                return candidate.FullName;
            }

            candidate = candidate.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the GoalKeeper repository root.");
    }

    private static string MutateEnvelope(
        string fixture,
        Action<JsonObject> mutate)
    {
        var root = JsonNode.Parse(Fixture(fixture))!.AsObject();
        mutate(root);
        return root.ToJsonString();
    }

    private static string MutateOutput(
        string fixture,
        Action<JsonObject> mutate) =>
        MutateEnvelope(
            fixture,
            envelope =>
            {
                var message = envelope["output"]!
                    .AsArray()
                    .Single(item =>
                        item!["type"]!.GetValue<string>() == "message")!;
                var text = message["content"]![0]!["text"]!.GetValue<string>();
                var output = JsonNode.Parse(text)!.AsObject();
                mutate(output);
                message["content"]![0]!["text"] =
                    output.ToJsonString();
            });

    private static string InputText(string requestBody)
    {
        using var payload = JsonDocument.Parse(requestBody);
        return payload.RootElement.GetProperty("input")[0]
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString()!;
    }

    private static string RepairText(string requestBody)
    {
        using var payload = JsonDocument.Parse(requestBody);
        return payload.RootElement.GetProperty("input")[0]
            .GetProperty("content")[1]
            .GetProperty("text")
            .GetString()!;
    }

    private static string SafeMetadata(RecoveryMetadata metadata) =>
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
        AuthenticationHeaderValue? Authorization,
        string? ClientRequestId);

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<Func<
            CapturedRequest,
            CancellationToken,
            Task<HttpResponseMessage>>> _responses;

        public ScriptedHandler(
            params Func<
                CapturedRequest,
                CancellationToken,
                Task<HttpResponseMessage>>[] responses)
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
                    : await request.Content.ReadAsStringAsync(
                        cancellationToken),
                request.Headers.Authorization,
                request.Headers.TryGetValues(
                    "X-Client-Request-Id",
                    out var values)
                    ? values.Single()
                    : null);
            Requests.Add(captured);
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException(
                    "No recorded response remains.");
            }

            return await _responses.Dequeue()(
                captured,
                cancellationToken);
        }
    }

    private sealed class RecordingHttpClientFactory(
        HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal(
                OpenAiRecoveryConversationAdapter.HttpClientName,
                name);
            return new HttpClient(handler, disposeHandler: false)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
        }
    }

    private sealed class UnknownLengthContent(
        byte[] content) : HttpContent
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
