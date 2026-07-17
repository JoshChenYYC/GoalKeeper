using System.Text.Json;
using System.Text.Json.Serialization;
using GoalKeeper.Application;
using GoalKeeper.Domain;
using Microsoft.EntityFrameworkCore;

namespace GoalKeeper.Infrastructure;

public sealed partial class EfGoalKeeperRepository
{
    private static readonly JsonSerializerOptions RuntimeJsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<SessionSetupView> TransitionSetupAsync(
        Guid id,
        long expectedVersion,
        SessionSetupStatus targetStatus,
        CancellationToken cancellationToken = default)
    {
        if (targetStatus == SessionSetupStatus.Ready)
        {
            throw new DomainRuleViolationException("A Session Setup cannot transition back to Ready.");
        }

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var setup = await LoadSetupAsync(db, id, cancellationToken);
        EnsureSetupVersion(setup, expectedVersion);
        if (setup.Status != SessionSetupStatus.Ready.ToString())
        {
            throw new DomainRuleViolationException("Only a ready Session Setup can transition.");
        }

        if (targetStatus == SessionSetupStatus.Started)
        {
            throw new DomainRuleViolationException("Use StartSessionAsync to start a Session Setup.");
        }

        setup.Status = targetStatus.ToString();
        setup.Version++;
        await SaveConcurrencyAsync(db, "The Session Setup was changed by another operation.", cancellationToken);
        return ToView(setup);
    }

    public async Task<FocusSessionRuntimeView> StartSessionAsync(
        Guid setupId,
        long expectedSetupVersion,
        FocusSessionRuntimeSnapshot initialRuntime,
        string? artifactDirectory = null,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialRuntime(initialRuntime);
        if (artifactDirectory is not null)
        {
            if (artifactStore is null)
            {
                throw new InvalidOperationException("No artifact store is configured for session artifacts.");
            }

            artifactStore.ValidateOwned(initialRuntime.Id, artifactDirectory);
        }

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var setup = await LoadSetupAsync(db, setupId, cancellationToken);
        EnsureSetupVersion(setup, expectedSetupVersion);
        if (setup.Status != SessionSetupStatus.Ready.ToString())
        {
            throw new DomainRuleViolationException("Only a ready Session Setup can start monitoring.");
        }

        if (setup.ContractId != initialRuntime.ContractId ||
            setup.Contract.GoalId != initialRuntime.GoalId)
        {
            throw new DomainRuleViolationException("The runtime snapshot does not match the Session Setup.");
        }

        if (await db.FocusSessions.AnyAsync(
                x => x.State != "Fulfilled" && x.State != "EndedEarly",
                cancellationToken))
        {
            throw new DomainRuleViolationException("Another Focus Session is already active.");
        }

        var session = new FocusSessionEntity
        {
            Id = initialRuntime.Id,
            GoalId = initialRuntime.GoalId,
            ContractId = initialRuntime.ContractId,
            ArtifactDirectory = artifactDirectory
        };
        ApplyRuntime(session, initialRuntime);
        setup.Status = SessionSetupStatus.Started.ToString();
        setup.Version++;
        db.FocusSessions.Add(session);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new PersistenceConflictException(
                "The Focus Session could not start because active runtime state changed.")
            {
                Source = exception.Source
            };
        }

        return ToRuntimeView(session);
    }

    public async Task<FocusSessionRuntimeView?> GetSessionAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var entity = await db.FocusSessions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        return entity is null ? null : ToRuntimeView(entity);
    }

    public async Task<FocusSessionRuntimeView> UpdateSessionAsync(
        Guid id,
        RuntimeMutation mutation,
        CancellationToken cancellationToken = default)
    {
        ValidateMutation(id, mutation.ExpectedVersion, mutation.Runtime);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var entity = await db.FocusSessions.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("Focus Session not found.");
        if (entity.Version != mutation.ExpectedVersion)
        {
            throw new PersistenceConflictException("The Focus Session was changed by another operation.");
        }

        db.Entry(entity).Property(x => x.Version).OriginalValue = mutation.ExpectedVersion;
        ApplyRuntime(entity, mutation.Runtime);
        AddAudits(db, id, mutation.Runtime.Version, mutation.AuditEvents);
        await SynchronizeRuntimeRecordsAsync(
            db,
            mutation.Runtime,
            mutation.GoalCompletedAtUtc,
            cancellationToken);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new PersistenceConflictException("The Focus Session was changed by another operation.")
            {
                Source = exception.Source
            };
        }

        return ToRuntimeView(entity);
    }

    public async Task DeleteSessionAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var entity = await db.FocusSessions.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("Focus Session not found.");
        if (entity.ActiveSlot is not null)
        {
            throw new DomainRuleViolationException("An active Focus Session cannot be deleted.");
        }

        if (entity.ArtifactDirectory is not null && artifactStore is null)
        {
            throw new InvalidOperationException("No artifact store is configured for safe deletion.");
        }

        artifactStore?.ValidateOwned(entity.Id, entity.ArtifactDirectory);
        db.FocusSessions.Remove(entity);
        await db.SaveChangesAsync(cancellationToken);
        artifactStore?.DeleteOwned(entity.Id, entity.ArtifactDirectory);
    }

    public async Task<IReadOnlyList<SessionHistoryItem>> ListSessionHistoryAsync(
        Guid? goalId = null,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        ValidateLimit(limit);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var query = db.FocusSessions.AsNoTracking().Include(x => x.Contract).AsQueryable();
        if (goalId is { } id)
        {
            query = query.Where(x => x.GoalId == id);
        }

        var entities = await query.ToListAsync(cancellationToken);
        return entities.OrderByDescending(x => x.StartedAtUtc)
            .Take(limit)
            .Select(x => new SessionHistoryItem(
            x.Id,
            x.GoalId,
            x.Contract.GoalTitle,
            ParseEnum<FocusSessionState>(x.State),
            x.StartedAtUtc,
            x.EndedAtUtc,
            ParseNullableEnum<EndedEarlyReason>(x.EndReason),
            x.Version)).ToArray();
    }

    public async Task<SnapshotView> AddSnapshotAsync(
        SnapshotWrite snapshot,
        CancellationToken cancellationToken = default)
    {
        ValidateSnapshot(snapshot);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var sessionVersion = await db.FocusSessions.Where(x => x.Id == snapshot.SessionId)
            .Select(x => (long?)x.Version)
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new KeyNotFoundException("Focus Session not found.");
        if (snapshot.SessionVersion > sessionVersion)
        {
            throw new DomainRuleViolationException("A Snapshot cannot reference a future session version.");
        }

        var entity = new SnapshotEntity
        {
            Id = snapshot.Id,
            SessionId = snapshot.SessionId,
            Sequence = snapshot.Sequence,
            CapturedAtUtc = snapshot.CapturedAtUtc,
            CapturedAtMonotonicTicks = snapshot.CapturedAtMonotonic.Ticks,
            ImagePath = snapshot.ImagePath,
            StoredBytes = snapshot.StoredBytes,
            ProcessingStatus = ToStorage(snapshot.Status),
            SessionVersion = snapshot.SessionVersion
        };
        db.Snapshots.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return ToView(entity);
    }

    public async Task<SnapshotView> UpdateSnapshotStatusAsync(
        Guid sessionId,
        Guid snapshotId,
        SnapshotProcessingStatus status,
        CancellationToken cancellationToken = default)
    {
        EnsureDefined(status);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var entity = await db.Snapshots.SingleOrDefaultAsync(
            x => x.SessionId == sessionId && x.Id == snapshotId,
            cancellationToken) ?? throw new KeyNotFoundException("Snapshot not found.");
        var current = ParseSnapshotStatus(entity.ProcessingStatus);
        if (current != SnapshotProcessingStatus.Captured || status == SnapshotProcessingStatus.Captured)
        {
            throw new DomainRuleViolationException("The Snapshot processing status is final.");
        }

        entity.ProcessingStatus = ToStorage(status);
        await db.SaveChangesAsync(cancellationToken);
        return ToView(entity);
    }

    public async Task<ObservationView> AddObservationAsync(
        ObservationWrite observation,
        CancellationToken cancellationToken = default)
    {
        ValidateObservation(observation);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var snapshot = await db.Snapshots.SingleOrDefaultAsync(
            x => x.SessionId == observation.SessionId && x.Id == observation.SnapshotId,
            cancellationToken) ?? throw new KeyNotFoundException("Snapshot not found.");
        if (ParseSnapshotStatus(snapshot.ProcessingStatus) != SnapshotProcessingStatus.Captured)
        {
            throw new DomainRuleViolationException("Only a captured Snapshot can produce an Observation.");
        }

        if (observation.SessionVersion != snapshot.SessionVersion ||
            observation.ProcessedAtUtc < snapshot.CapturedAtUtc)
        {
            throw new DomainRuleViolationException("The Observation capture metadata is inconsistent.");
        }

        var entity = new ObservationEntity
        {
            Id = observation.Id,
            SessionId = observation.SessionId,
            SnapshotId = observation.SnapshotId,
            SessionVersion = observation.SessionVersion,
            ProcessedAtUtc = observation.ProcessedAtUtc,
            SchemaVersion = observation.SchemaVersion,
            DocumentJson = observation.DocumentJson
        };
        snapshot.ProcessingStatus = ToStorage(SnapshotProcessingStatus.Observed);
        db.Observations.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ToView(entity, snapshot);
    }

    public async Task<IReadOnlyList<ObservationView>> GetRecentObservationsAsync(
        Guid sessionId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ValidateLimit(limit);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var recent = await db.Observations.AsNoTracking()
            .Include(x => x.Snapshot)
            .Where(x => x.SessionId == sessionId)
            .OrderByDescending(x => x.Snapshot.Sequence)
            .Take(limit)
            .ToListAsync(cancellationToken);
        return recent.OrderBy(x => x.Snapshot.CapturedAtUtc)
            .ThenBy(x => x.Snapshot.Sequence)
            .Select(x => ToView(x, x.Snapshot))
            .ToArray();
    }

    public async Task<ReasoningCommitResult> CommitReasoningEvaluationAsync(
        ReasoningCommitRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateReasoningCommit(request);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var session = await db.FocusSessions.SingleOrDefaultAsync(
            x => x.Id == request.ProposedRuntime.Id,
            cancellationToken) ?? throw new KeyNotFoundException("Focus Session not found.");
        if (session.Version != request.ExpectedSessionVersion)
        {
            await transaction.RollbackAsync(cancellationToken);
            return await AppendRejectedEvaluationAsync(
                request.Evaluation,
                "stale_session_version",
                cancellationToken);
        }

        db.Entry(session).Property(x => x.Version).OriginalValue = request.ExpectedSessionVersion;
        ApplyRuntime(session, request.ProposedRuntime);
        var evaluation = ToEntity(request.Evaluation, accepted: true, rejectionReason: null);
        if (request.EvidenceEpisode is { } episodeWrite)
        {
            var episode = ToEntity(episodeWrite);
            evaluation.EvidenceEpisode = episode;
            if (request.Intervention is { } interventionWrite)
            {
                var intervention = ToEntity(interventionWrite);
                intervention.Evaluation = evaluation;
                intervention.EvidenceEpisode = episode;
                db.Interventions.Add(intervention);
            }

            db.EvidenceEpisodes.Add(episode);
        }
        else if (request.Intervention is not null)
        {
            throw new DomainRuleViolationException("An Intervention requires an Evidence Episode.");
        }

        db.ReasoningEvaluations.Add(evaluation);
        AddAudits(db, session.Id, request.ProposedRuntime.Version, request.AuditEvents);
        await SynchronizeRuntimeRecordsAsync(
            db,
            request.ProposedRuntime,
            null,
            cancellationToken);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(true, null, request.ProposedRuntime.Version);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(cancellationToken);
            return await AppendRejectedEvaluationAsync(
                request.Evaluation,
                "stale_session_version",
                cancellationToken);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            return await AppendRejectedEvaluationAsync(
                request.Evaluation,
                "storage_conflict",
                cancellationToken);
        }
    }

    public async Task<IReadOnlyList<ReasoningEvaluationView>> GetRecentReasoningEvaluationsAsync(
        Guid sessionId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ValidateLimit(limit);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var evaluations = await db.ReasoningEvaluations.AsNoTracking()
            .Where(x => x.SessionId == sessionId)
            .ToListAsync(cancellationToken);
        return evaluations.OrderByDescending(x => x.EvaluatedAtUtc)
            .Take(limit)
            .OrderBy(x => x.EvaluatedAtUtc)
            .Select(ToView)
            .ToArray();
    }

    public async Task AddRecoveryTurnAsync(
        RecoveryTurnWrite turn,
        CancellationToken cancellationToken = default)
    {
        if (turn.Id == Guid.Empty || turn.SessionId == Guid.Empty ||
            turn.InterventionId == Guid.Empty || turn.TurnNumber <= 0 ||
            string.IsNullOrWhiteSpace(turn.Outcome))
        {
            throw new DomainRuleViolationException("The Recovery turn is invalid.");
        }

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        db.RecoveryTurns.Add(new RecoveryTurnEntity
        {
            Id = turn.Id,
            SessionId = turn.SessionId,
            InterventionId = turn.InterventionId,
            TurnNumber = turn.TurnNumber,
            Outcome = turn.Outcome.Trim(),
            Transcript = string.IsNullOrWhiteSpace(turn.Transcript) ? null : turn.Transcript.Trim(),
            OccurredAtUtc = turn.OccurredAtUtc
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<ReasoningCommitResult> AppendRejectedEvaluationAsync(
        ReasoningEvaluationWrite evaluation,
        string reason,
        CancellationToken cancellationToken)
    {
        await using var rejectionDb = await factory.CreateDbContextAsync(cancellationToken);
        var currentVersion = await rejectionDb.FocusSessions.Where(x => x.Id == evaluation.SessionId)
            .Select(x => (long?)x.Version)
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new KeyNotFoundException("Focus Session not found.");
        if (!await rejectionDb.ReasoningEvaluations.AnyAsync(x => x.Id == evaluation.Id, cancellationToken))
        {
            rejectionDb.ReasoningEvaluations.Add(ToEntity(evaluation, accepted: false, reason));
            await rejectionDb.SaveChangesAsync(cancellationToken);
        }

        return new(false, reason, currentVersion);
    }

    private static async Task<SessionSetupEntity> LoadSetupAsync(
        GoalKeeperDbContext db,
        Guid id,
        CancellationToken cancellationToken) =>
        await db.SessionSetups.Include(x => x.Contract).ThenInclude(x => x.Breaks)
            .Include(x => x.Contract).ThenInclude(x => x.Deviations)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
        ?? throw new KeyNotFoundException("Session Setup not found.");

    private static void EnsureSetupVersion(SessionSetupEntity setup, long expectedVersion)
    {
        if (setup.Version != expectedVersion)
        {
            throw new PersistenceConflictException("The Session Setup was changed by another operation.");
        }
    }

    private static async Task SaveConcurrencyAsync(
        GoalKeeperDbContext db,
        string message,
        CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new PersistenceConflictException(message) { Source = exception.Source };
        }
    }

    private static void EnsureInitialRuntime(FocusSessionRuntimeSnapshot runtime)
    {
        if (runtime.Id == Guid.Empty || runtime.GoalId == Guid.Empty ||
            runtime.ContractId == Guid.Empty || runtime.Version <= 0 ||
            runtime.State is FocusSessionState.Fulfilled or FocusSessionState.EndedEarly)
        {
            throw new DomainRuleViolationException("The initial Focus Session runtime is invalid.");
        }
    }

    private static void ValidateMutation(
        Guid sessionId,
        long expectedVersion,
        FocusSessionRuntimeSnapshot runtime)
    {
        if (runtime.Id != sessionId || expectedVersion <= 0 ||
            runtime.Version <= expectedVersion)
        {
            throw new DomainRuleViolationException("The runtime mutation version is invalid.");
        }
    }

    private static void ApplyRuntime(
        FocusSessionEntity entity,
        FocusSessionRuntimeSnapshot runtime)
    {
        entity.State = runtime.State.ToString();
        entity.ActiveSlot = runtime.State is FocusSessionState.Fulfilled or FocusSessionState.EndedEarly
            ? null
            : 1;
        entity.Version = runtime.Version;
        entity.StartedAtUtc = runtime.StartedAtUtc;
        entity.ProjectedEndUtc = runtime.ProjectedEndUtc;
        entity.EndedAtUtc = runtime.EndedAtUtc;
        entity.AccumulatedFocusTicks = runtime.Timer.Accumulated.Ticks;
        entity.EndReason = runtime.EndedEarlyReason?.ToString();
        entity.RuntimeSnapshotJson = JsonSerializer.Serialize(runtime, RuntimeJsonOptions);
    }

    private static FocusSessionRuntimeView ToRuntimeView(FocusSessionEntity entity)
    {
        var runtime = entity.RuntimeSnapshotJson is "{}" or ""
            ? LegacyRuntime(entity)
            : JsonSerializer.Deserialize<FocusSessionRuntimeSnapshot>(
                entity.RuntimeSnapshotJson,
                RuntimeJsonOptions)
              ?? throw new InvalidOperationException("The Focus Session runtime snapshot is missing.");
        return new(
            entity.Id,
            entity.GoalId,
            entity.ContractId,
            ParseEnum<FocusSessionState>(entity.State),
            entity.Version,
            entity.StartedAtUtc,
            entity.EndedAtUtc,
            entity.ArtifactDirectory,
            runtime);
    }

    private static FocusSessionRuntimeSnapshot LegacyRuntime(FocusSessionEntity entity)
    {
        var state = ParseEnum<FocusSessionState>(entity.State);
        TimeSpan? running = state is FocusSessionState.Focusing or FocusSessionState.RecoveryWindow
            ? TimeSpan.Zero
            : null;
        return new(
            entity.Id,
            entity.GoalId,
            entity.ContractId,
            state,
            entity.Version,
            entity.StartedAtUtc,
            entity.ProjectedEndUtc == default ? entity.StartedAtUtc : entity.ProjectedEndUtc,
            entity.EndedAtUtc,
            ParseNullableEnum<EndedEarlyReason>(entity.EndReason),
            new FocusTimerSnapshot(
                TimeSpan.FromTicks(entity.AccumulatedFocusTicks),
                running,
                null),
            0,
            null,
            null,
            null,
            0,
            false,
            null,
            null,
            null,
            [],
            [],
            null,
            new FocusSessionPolicySnapshot(
                ApplicationSettingsView.Default.RecoveryWindow,
                ApplicationSettingsView.Default.ResponseTimeout,
                ApplicationSettingsView.Default.TechnicalOutageGrace,
                ApplicationSettingsView.Default.MaximumUnsuccessfulRecoveries,
                ApplicationSettingsView.Default.MaximumCoachingTurns));
    }

    private static void AddAudits(
        GoalKeeperDbContext db,
        Guid sessionId,
        long sessionVersion,
        IEnumerable<RuntimeAuditWrite> audits)
    {
        db.AuditEvents.AddRange(audits.Select(x => new AuditEventEntity
        {
            SessionId = sessionId,
            SessionVersion = sessionVersion,
            OccurredAtUtc = x.OccurredAtUtc,
            Event = Required(x.Event, "Audit event"),
            FromState = x.FromState?.ToString(),
            ToState = x.ToState?.ToString(),
            PayloadJson = RequiredJson(x.PayloadJson)
        }));
    }

    private static async Task SynchronizeRuntimeRecordsAsync(
        GoalKeeperDbContext db,
        FocusSessionRuntimeSnapshot runtime,
        DateTimeOffset? goalCompletedAtUtc,
        CancellationToken cancellationToken)
    {
        var existingOverrideIds = await db.DeviationOverrides
            .Where(x => x.SessionId == runtime.Id)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
        db.DeviationOverrides.AddRange(runtime.DeviationOverrides
            .Where(x => !existingOverrideIds.Contains(x.Id))
            .Select(x => new DeviationOverrideEntity
            {
                Id = x.Id,
                SessionId = x.SessionId,
                ListedDeviationId = x.Deviation.ListedDeviationId,
                UnlistedDescription = x.Deviation.UnlistedDescription,
                Reason = x.Reason,
                AppliedAtUtc = x.AppliedAtUtc
            }));

        if (runtime.Review is { } review &&
            !await db.SessionReviews.AnyAsync(x => x.SessionId == runtime.Id, cancellationToken))
        {
            db.SessionReviews.Add(new SessionReviewEntity
            {
                Id = review.Id,
                SessionId = review.SessionId,
                MeaningfulProgress = review.MeaningfulProgress,
                Helpfulness = review.Helpfulness.ToString(),
                Note = review.Note,
                MarkGoalComplete = review.MarkGoalComplete,
                SubmittedAtUtc = review.SubmittedAtUtc
            });
        }

        goalCompletedAtUtc ??= runtime.Review is { MarkGoalComplete: true } completedReview
            ? completedReview.SubmittedAtUtc
            : null;
        if (goalCompletedAtUtc is { } completedAt)
        {
            var goal = await db.Goals.SingleAsync(x => x.Id == runtime.GoalId, cancellationToken);
            if (goal.Status == GoalStatus.Active.ToString())
            {
                goal.Status = GoalStatus.Completed.ToString();
                goal.CompletedAtUtc = completedAt;
                goal.Version++;
            }
        }

        if (runtime.ActiveIntervention is null)
        {
            var active = await db.Interventions
                .Where(x => x.SessionId == runtime.Id && x.Status == "Active")
                .ToListAsync(cancellationToken);
            foreach (var intervention in active)
            {
                intervention.Status = "Resolved";
            }
        }
    }

    private static EvidenceEpisodeEntity ToEntity(EvidenceEpisodeWrite value)
    {
        if (value.Id == Guid.Empty || value.SessionId == Guid.Empty ||
            value.Observations.Count == 0)
        {
            throw new DomainRuleViolationException("The Evidence Episode is invalid.");
        }

        if ((value.ListedDeviationId is null) == string.IsNullOrWhiteSpace(value.UnlistedDescription))
        {
            throw new DomainRuleViolationException("The Evidence Episode Deviation reference is invalid.");
        }

        return new EvidenceEpisodeEntity
        {
            Id = value.Id,
            SessionId = value.SessionId,
            ListedDeviationId = value.ListedDeviationId,
            UnlistedDescription = value.UnlistedDescription,
            CreatedAtUtc = value.CreatedAtUtc,
            DocumentJson = RequiredJson(value.DocumentJson),
            ObservationReferences = value.Observations.Select(x =>
                new EvidenceObservationReferenceEntity
                {
                    EvidenceEpisodeId = value.Id,
                    SessionId = value.SessionId,
                    ObservationId = x.ObservationId,
                    Sequence = x.Sequence
                }).ToList()
        };
    }

    private static ReasoningEvaluationEntity ToEntity(
        ReasoningEvaluationWrite value,
        bool accepted,
        string? rejectionReason)
    {
        if (value.Id == Guid.Empty || value.SessionId == Guid.Empty ||
            value.SessionVersion <= 0 || value.SchemaVersion <= 0)
        {
            throw new DomainRuleViolationException("The Reasoning Evaluation is invalid.");
        }

        EnsureDefined(value.Decision);
        return new ReasoningEvaluationEntity
        {
            Id = value.Id,
            SessionId = value.SessionId,
            SessionVersion = value.SessionVersion,
            Decision = value.Decision.ToString(),
            EvaluatedAtUtc = value.EvaluatedAtUtc,
            SchemaVersion = value.SchemaVersion,
            DocumentJson = RequiredJson(value.DocumentJson),
            Accepted = accepted,
            RejectionReason = rejectionReason
        };
    }

    private static InterventionEntity ToEntity(InterventionWrite value)
    {
        if (value.Id == Guid.Empty || value.SessionId == Guid.Empty ||
            value.EvaluationId == Guid.Empty || value.EvidenceEpisodeId == Guid.Empty ||
            value.DisputedDuration < TimeSpan.Zero)
        {
            throw new DomainRuleViolationException("The Intervention is invalid.");
        }

        return new InterventionEntity
        {
            Id = value.Id,
            SessionId = value.SessionId,
            EvaluationId = value.EvaluationId,
            EvidenceEpisodeId = value.EvidenceEpisodeId,
            AdmittedAtUtc = value.AdmittedAtUtc,
            DisputedTicks = value.DisputedDuration.Ticks,
            Status = Required(value.Status, "Intervention status")
        };
    }

    private static void ValidateReasoningCommit(ReasoningCommitRequest request)
    {
        ValidateMutation(
            request.ProposedRuntime.Id,
            request.ExpectedSessionVersion,
            request.ProposedRuntime);
        if (request.Evaluation.SessionId != request.ProposedRuntime.Id ||
            request.Evaluation.SessionVersion != request.ExpectedSessionVersion ||
            request.Evaluation.Decision == ReasoningDecision.BeginRecoveryCheckIn &&
            request.EvidenceEpisode is null ||
            request.Evaluation.Decision == ReasoningDecision.ContinueObserving &&
            request.EvidenceEpisode is not null ||
            request.EvidenceEpisode is { } episode && episode.SessionId != request.ProposedRuntime.Id ||
            request.Intervention is { } intervention &&
            (intervention.SessionId != request.ProposedRuntime.Id ||
             intervention.EvaluationId != request.Evaluation.Id ||
             request.EvidenceEpisode is null ||
             intervention.EvidenceEpisodeId != request.EvidenceEpisode.Id))
        {
            throw new DomainRuleViolationException("Reasoning commit references are inconsistent.");
        }
    }

    private static void ValidateSnapshot(SnapshotWrite snapshot)
    {
        if (snapshot.Id == Guid.Empty || snapshot.SessionId == Guid.Empty ||
            snapshot.Sequence < 0 || snapshot.CapturedAtMonotonic < TimeSpan.Zero ||
            snapshot.StoredBytes < 0 || snapshot.SessionVersion <= 0 ||
            string.IsNullOrWhiteSpace(snapshot.ImagePath))
        {
            throw new DomainRuleViolationException("The Snapshot is invalid.");
        }

        EnsureDefined(snapshot.Status);
        if (snapshot.Status != SnapshotProcessingStatus.Captured)
        {
            throw new DomainRuleViolationException("A new Snapshot must begin as captured.");
        }
    }

    private static void ValidateObservation(ObservationWrite observation)
    {
        if (observation.Id == Guid.Empty || observation.SessionId == Guid.Empty ||
            observation.SnapshotId == Guid.Empty || observation.SessionVersion <= 0 ||
            observation.SchemaVersion <= 0)
        {
            throw new DomainRuleViolationException("The Observation is invalid.");
        }

        _ = RequiredJson(observation.DocumentJson);
    }

    private static SnapshotView ToView(SnapshotEntity value) =>
        new(
            value.Id,
            value.SessionId,
            value.Sequence,
            value.CapturedAtUtc,
            TimeSpan.FromTicks(value.CapturedAtMonotonicTicks),
            value.ImagePath,
            value.StoredBytes,
            ParseSnapshotStatus(value.ProcessingStatus),
            value.SessionVersion);

    private static ObservationView ToView(ObservationEntity value, SnapshotEntity snapshot) =>
        new(
            value.Id,
            value.SessionId,
            value.SnapshotId,
            value.SessionVersion,
            snapshot.CapturedAtUtc,
            TimeSpan.FromTicks(snapshot.CapturedAtMonotonicTicks),
            value.ProcessedAtUtc,
            value.SchemaVersion,
            value.DocumentJson);

    private static ReasoningEvaluationView ToView(ReasoningEvaluationEntity value) =>
        new(
            value.Id,
            value.SessionId,
            value.SessionVersion,
            ParseEnum<ReasoningDecision>(value.Decision),
            value.EvaluatedAtUtc,
            value.SchemaVersion,
            value.DocumentJson,
            value.Accepted,
            value.RejectionReason);

    private static string ToStorage(SnapshotProcessingStatus status) => status switch
    {
        SnapshotProcessingStatus.Captured => "captured",
        SnapshotProcessingStatus.Superseded => "superseded",
        SnapshotProcessingStatus.Observed => "observed",
        SnapshotProcessingStatus.Stale => "stale",
        SnapshotProcessingStatus.AgentError => "agent_error",
        _ => throw new DomainRuleViolationException("The Snapshot processing status is invalid.")
    };

    private static SnapshotProcessingStatus ParseSnapshotStatus(string status) =>
        status.ToLowerInvariant() switch
        {
            "captured" => SnapshotProcessingStatus.Captured,
            "superseded" => SnapshotProcessingStatus.Superseded,
            "observed" => SnapshotProcessingStatus.Observed,
            "stale" => SnapshotProcessingStatus.Stale,
            "agent_error" or "agenterror" => SnapshotProcessingStatus.AgentError,
            _ => throw new InvalidOperationException("The stored Snapshot processing status is invalid.")
        };

    private static T ParseEnum<T>(string value) where T : struct, Enum =>
        Enum.TryParse<T>(value, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw new InvalidOperationException($"The stored {typeof(T).Name} is invalid.");

    private static T? ParseNullableEnum<T>(string? value) where T : struct, Enum =>
        value is null ? null : ParseEnum<T>(value);

    private static void EnsureDefined<T>(T value) where T : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new DomainRuleViolationException($"{typeof(T).Name} is invalid.");
        }
    }

    private static void ValidateLimit(int limit)
    {
        if (limit is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "The query limit must be between 1 and 1000.");
        }
    }

    private static string Required(string? value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new DomainRuleViolationException($"{name} is required.")
            : value.Trim();

    private static string RequiredJson(string? value)
    {
        var json = Required(value, "JSON document");
        try
        {
            using var _ = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new DomainRuleViolationException($"The JSON document is invalid: {exception.Message}");
        }

        return json;
    }
}
