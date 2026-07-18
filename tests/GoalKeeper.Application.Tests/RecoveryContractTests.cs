using System.Reflection;
using System.Text.Json;
using GoalKeeper.Application.Recovery;
using GoalKeeper.Domain;

namespace GoalKeeper.Application.Tests;

public sealed class RecoveryContractTests
{
    [Fact]
    public void Request_contains_only_bounded_check_in_context_and_no_snapshot_or_audio_payload()
    {
        var request = RecoveryTestData.Request();

        Assert.Equal(RecoveryTestData.ContractId, request.Contract.ContractId);
        Assert.Equal(RecoveryTestData.GoalId, request.Contract.GoalId);
        Assert.Equal(RecoveryTestData.InterventionId, request.Intervention.InterventionId);
        Assert.Equal(TimeSpan.FromMinutes(2), request.DisputedInterval.Duration);
        Assert.Single(request.ActiveOverrides);
        Assert.Equal(7, request.AllowedOutcomes.Count);

        var requestTypes = new[]
        {
            typeof(RecoveryRequest),
            typeof(RecoveryContractContext),
            typeof(RecoveryInterventionContext),
            typeof(RecoveryDisputedInterval),
            typeof(RecoveryOverrideContext),
            typeof(RecoveryRequestOptions),
            typeof(RecoveryTurn)
        };
        var forbidden = new[]
        {
            "snapshot", "image", "jpeg", "bitmap", "frame", "audio", "microphone",
            "room", "goalhistory", "crosssession"
        };

        Assert.DoesNotContain(
            requestTypes.SelectMany(type => type.GetProperties(BindingFlags.Instance | BindingFlags.Public)),
            property => forbidden.Any(name =>
                property.Name.Contains(name, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Request_defensively_copies_bounded_collections_and_rejects_invalid_context()
    {
        var outcomes = Enum.GetValues<RecoveryOutcome>().ToList();
        var turns = new List<RecoveryTurn>();
        var request = RecoveryTestData.Request(turns: turns, allowedOutcomes: outcomes);
        outcomes.Clear();
        turns.Add(RecoveryTestData.Turn(1, RecoveryOutcome.Recommit));

        Assert.Equal(7, request.AllowedOutcomes.Count);
        Assert.Empty(request.CurrentCheckInTurns);
        Assert.Throws<ArgumentException>(() =>
            RecoveryTestData.Request(allowedOutcomes: []));
        Assert.Throws<ArgumentException>(() =>
            RecoveryTestData.Request(
                allowedOutcomes: [RecoveryOutcome.Recommit, RecoveryOutcome.Recommit]));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RecoveryTestData.Request(maximumCoachingTurns: RecoveryLimits.MaximumCoachingTurns + 1));
    }

    [Fact]
    public void Persisted_current_turns_must_be_same_check_in_ordered_and_within_the_coaching_cap()
    {
        var ordered = new[]
        {
            RecoveryTestData.Turn(1, RecoveryOutcome.AdditionalCoaching),
            RecoveryTestData.Turn(2, RecoveryOutcome.UnclearResponse)
        };
        var request = RecoveryTestData.Request(turns: ordered);

        Assert.Equal(3, request.NextTurnNumber);
        Assert.Equal(1, request.PersistedCoachingTurnCount);

        Assert.Throws<ArgumentException>(() =>
            RecoveryTestData.Request(turns: [ordered[1], ordered[0]]));
        Assert.Throws<ArgumentException>(() =>
            RecoveryTestData.Request(
                turns:
                [
                    RecoveryTestData.Turn(1, RecoveryOutcome.AdditionalCoaching),
                    RecoveryTestData.Turn(2, RecoveryOutcome.AdditionalCoaching)
                ],
                maximumCoachingTurns: 1));
    }

    [Fact]
    public void Result_contract_represents_every_failure_category_and_safe_audit_metadata()
    {
        foreach (var category in Enum.GetValues<RecoveryFailureCategory>())
        {
            var failure = new RecoveryFailureResponse(category, RecoveryTestData.Metadata());
            Assert.Equal(category, failure.Category);
            Assert.Equal("scripted-text", failure.Metadata.Provider);
        }
    }

    [Fact]
    public void Recovery_boundary_has_no_domain_mutation_surface()
    {
        var portMethod = typeof(IRecoveryPort).GetMethod(nameof(IRecoveryPort.ProposeAsync));
        Assert.NotNull(portMethod);
        Assert.Equal(
            [typeof(RecoveryRequest), typeof(CancellationToken)],
            portMethod.GetParameters().Select(parameter => parameter.ParameterType));

        var boundaryTypes = new[]
        {
            typeof(IRecoveryPort),
            typeof(RecoveryRequest),
            typeof(RecoveryPortResult),
            typeof(RecoveryProposalValidation),
            typeof(RecoveryTurnFactory)
        };

        Assert.DoesNotContain(
            boundaryTypes.SelectMany(type =>
                type.GetProperties(BindingFlags.Instance | BindingFlags.Public)),
            property => property.PropertyType == typeof(FocusSession));
        Assert.DoesNotContain(
            typeof(RecoveryTurnFactory).GetMethods(BindingFlags.Public | BindingFlags.Static),
            method => method.GetParameters().Any(parameter =>
                parameter.ParameterType == typeof(FocusSession)));
    }

    [Fact]
    public void Validated_turn_maps_full_audit_metadata_into_existing_persistence_columns()
    {
        var request = RecoveryTestData.Request(
            RecoveryTestData.DefaultTranscript(RecoveryOutcome.BehaviorClarification));
        var validation = Assert.IsType<ValidRecoveryProposal>(
            RecoveryProposalValidator.Validate(
                request,
                RecoveryTestData.Proposal(
                    RecoveryOutcome.BehaviorClarification,
                    request,
                    remainderOverrideConfirmed: true)));
        var turn = RecoveryTurnFactory.Create(Guid.NewGuid(), validation);

        var write = RecoveryTurnPersistence.ToWrite(turn);
        using var document = JsonDocument.Parse(write.Outcome);
        var root = document.RootElement;

        Assert.Equal(turn.Transcript, write.Transcript);
        Assert.Equal("behavior_clarification", root.GetProperty("structured_outcome").GetString());
        Assert.Equal("scripted-text", root.GetProperty("metadata").GetProperty("provider").GetString());
        Assert.Equal("deterministic-v1", root.GetProperty("metadata").GetProperty("model").GetString());
        Assert.Equal("recovery-v1", root.GetProperty("metadata").GetProperty("prompt_version").GetString());
        Assert.Equal(RecoverySchemaVersions.V1,
            root.GetProperty("metadata").GetProperty("schema_version").GetInt32());
        Assert.Equal("recovery-request-123",
            root.GetProperty("metadata").GetProperty("request_id").GetString());
        Assert.Equal("00:00:00.0400000",
            root.GetProperty("metadata").GetProperty("latency").GetString());
        Assert.Equal(
            turn.Timing.StartedAtUtc,
            root.GetProperty("timing").GetProperty("started_at_utc").GetDateTimeOffset());
        Assert.Equal(
            turn.Timing.CompletedAtUtc,
            root.GetProperty("timing").GetProperty("completed_at_utc").GetDateTimeOffset());
        Assert.Equal(
            turn.Clarification,
            root.GetProperty("clarification").GetString());
        Assert.True(root.GetProperty("remainder_override_confirmed").GetBoolean());
        Assert.Equal(turn.Timing.CompletedAtUtc, write.OccurredAtUtc);
    }
}
