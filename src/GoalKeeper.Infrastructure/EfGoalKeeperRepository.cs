using GoalKeeper.Application;
using GoalKeeper.Domain;
using Microsoft.EntityFrameworkCore;

namespace GoalKeeper.Infrastructure;

public sealed class EfGoalKeeperRepository(
    IDbContextFactory<GoalKeeperDbContext> factory,
    SessionArtifactStore? artifactStore = null) : IGoalKeeperRepository
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.Database.MigrateAsync(cancellationToken);
        if (!await db.ApplicationSettings.AnyAsync(cancellationToken))
        {
            var defaults = ApplicationSettingsView.Default;
            db.ApplicationSettings.AddRange(
                Setting("RecoveryWindowSeconds", defaults.RecoveryWindow.TotalSeconds),
                Setting("ResponseTimeoutSeconds", defaults.ResponseTimeout.TotalSeconds),
                Setting("TechnicalOutageGraceSeconds", defaults.TechnicalOutageGrace.TotalSeconds),
                Setting("MaximumUnsuccessfulRecoveries", defaults.MaximumUnsuccessfulRecoveries),
                Setting("MaximumCoachingTurns", defaults.MaximumCoachingTurns));
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<IReadOnlyList<GoalView>> ListGoalsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var entities = await db.Goals.AsNoTracking().ToListAsync(cancellationToken);
        return entities.OrderBy(x => x.CreatedAtUtc).Select(ToView).ToArray();
    }

    public async Task<GoalView?> GetGoalAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var entity = await db.Goals.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        return entity is null ? null : ToView(entity);
    }

    public async Task<GoalView> CreateGoalAsync(string title, string? description, DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var entity = new GoalEntity
        {
            Id = Guid.NewGuid(),
            Title = title,
            Description = description,
            Status = GoalStatus.Active.ToString(),
            CreatedAtUtc = createdAtUtc,
            Version = 1
        };
        db.Goals.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return ToView(entity);
    }

    public async Task<GoalView> UpdateGoalAsync(Guid id, long expectedVersion, string title, string? description,
        DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default)
    {
        _ = updatedAtUtc;
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var entity = await db.Goals.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("Goal not found.");
        if (entity.Version != expectedVersion)
        {
            throw new PersistenceConflictException("The Goal was changed by another operation.");
        }
        if (entity.Status != GoalStatus.Active.ToString() ||
            await db.FocusSessions.AnyAsync(x => x.GoalId == id &&
                x.State != "Fulfilled" && x.State != "EndedEarly", cancellationToken))
        {
            throw new DomainRuleViolationException("The Goal cannot be edited while completed or in an active session.");
        }

        entity.Title = title;
        entity.Description = description;
        entity.Version++;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new PersistenceConflictException("The Goal was changed by another operation.") { Source = exception.Source };
        }
        return ToView(entity);
    }

    public async Task DeleteGoalAsync(Guid id, long expectedVersion, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var entity = await db.Goals.Include(x => x.Sessions).SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("Goal not found.");
        if (entity.Version != expectedVersion)
        {
            throw new PersistenceConflictException("The Goal was changed by another operation.");
        }
        if (entity.Sessions.Any(x => x.State is not ("Fulfilled" or "EndedEarly")))
        {
            throw new DomainRuleViolationException("An active Focus Session locks Goal deletion.");
        }
        foreach (var session in entity.Sessions)
        {
            if (session.ArtifactDirectory is not null && artifactStore is null)
                throw new InvalidOperationException("No artifact store is configured for safe deletion.");
            artifactStore?.ValidateOwned(session.Id, session.ArtifactDirectory);
        }
        db.Goals.Remove(entity);
        await db.SaveChangesAsync(cancellationToken);
        foreach (var session in entity.Sessions)
            artifactStore?.DeleteOwned(session.Id, session.ArtifactDirectory);
    }

    public async Task<DeviationProfileView?> GetProfileAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var profiles = await db.DeviationProfiles.AsNoTracking().Include(x => x.Deviations).ToListAsync(cancellationToken);
        var entity = profiles.OrderByDescending(x => x.UpdatedAtUtc).FirstOrDefault();
        return entity is null ? null : ToView(entity);
    }

    public async Task<DeviationProfileView> SaveProfileAsync(string name, IReadOnlyList<DeviationInput> deviations,
        DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var profiles = await db.DeviationProfiles.ToListAsync(cancellationToken);
        var entity = profiles.OrderByDescending(x => x.UpdatedAtUtc).FirstOrDefault();
        if (entity is null)
        {
            entity = new() { Id = Guid.NewGuid(), CreatedAtUtc = nowUtc, Version = 1 };
            db.DeviationProfiles.Add(entity);
        }
        else
        {
            await db.Deviations.Where(x => x.ProfileId == entity.Id).ExecuteDeleteAsync(cancellationToken);
            entity.Version++;
        }
        entity.Name = name;
        entity.UpdatedAtUtc = nowUtc;
        var replacements = deviations.Select((x, index) => new DeviationEntity
        {
            Id = Guid.NewGuid(),
            ProfileId = entity.Id,
            Description = x.Description,
            Observability = x.Observability.ToString(),
            SortOrder = index
        }).ToList();
        db.Deviations.AddRange(replacements);
        await db.SaveChangesAsync(cancellationToken);
        entity.Deviations = replacements;
        return ToView(entity);
    }

    public async Task<SessionContractView?> GetLatestContractAsync(Guid goalId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var contracts = await db.SessionContracts.AsNoTracking().Where(x => x.GoalId == goalId)
            .Include(x => x.Breaks).Include(x => x.Deviations).ToListAsync(cancellationToken);
        var entity = contracts.OrderByDescending(x => x.ConfirmedAtUtc).FirstOrDefault();
        return entity is null ? null : ToView(entity);
    }

    public async Task<SessionSetupView> CreateReadySetupAsync(SessionContractDraft draft, DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await db.Goals.AnyAsync(x => x.Id == draft.GoalId && x.Status == "Active", cancellationToken))
        {
            throw new DomainRuleViolationException("A ready Session Setup requires an active Goal.");
        }
        var contract = new ContractEntity
        {
            Id = Guid.NewGuid(),
            GoalId = draft.GoalId,
            GoalTitle = draft.GoalTitle,
            GoalDescription = draft.GoalDescription,
            TargetFocusTicks = draft.TargetFocusDuration.Ticks,
            DeviationProfileId = draft.DeviationProfileId,
            DeviationProfileName = draft.DeviationProfileName,
            ReasoningMode = draft.ReasoningMode.ToString(),
            Sensitivity = draft.Sensitivity.ToString(),
            ConfirmedAtUtc = nowUtc,
            Breaks = draft.ScheduledBreaks.OrderBy(x => x.ActiveFocusOffset).Select((x, index) =>
                new ContractBreakEntity
                {
                    ActiveFocusOffsetTicks = x.ActiveFocusOffset.Ticks,
                    DurationTicks = x.Duration.Ticks,
                    SortOrder = index
                }).ToList(),
            Deviations = draft.Deviations.Select((x, index) => new ContractDeviationEntity
            {
                DeviationId = x.Id,
                Description = x.Description,
                Observability = x.Observability.ToString(),
                SortOrder = index
            }).ToList()
        };
        var setup = new SessionSetupEntity
        {
            Id = Guid.NewGuid(),
            Contract = contract,
            Status = SessionSetupStatus.Ready.ToString(),
            CreatedAtUtc = nowUtc,
            Version = 1
        };
        db.SessionSetups.Add(setup);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ToView(setup);
    }

    public async Task<SessionSetupView?> GetSetupAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var entity = await db.SessionSetups.AsNoTracking().Include(x => x.Contract).ThenInclude(x => x.Breaks)
            .Include(x => x.Contract).ThenInclude(x => x.Deviations)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        return entity is null ? null : ToView(entity);
    }

    public async Task<StorageUsageView> GetStorageUsageAsync(Guid? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var query = db.Snapshots.AsNoTracking();
        if (sessionId is { } id) query = query.Where(x => x.SessionId == id);
        return new(await query.SumAsync(x => x.StoredBytes, cancellationToken),
            await query.CountAsync(cancellationToken));
    }

    public async Task<ApplicationSettingsView> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var values = await db.ApplicationSettings.AsNoTracking().ToDictionaryAsync(x => x.Key, x => x.Value, cancellationToken);
        return new(
            TimeSpan.FromSeconds(ReadDouble(values, "RecoveryWindowSeconds")),
            TimeSpan.FromSeconds(ReadDouble(values, "ResponseTimeoutSeconds")),
            TimeSpan.FromSeconds(ReadDouble(values, "TechnicalOutageGraceSeconds")),
            ReadInt(values, "MaximumUnsuccessfulRecoveries"),
            ReadInt(values, "MaximumCoachingTurns"));
    }

    private static ApplicationSettingEntity Setting(string key, double value) =>
        new() { Key = key, Value = value.ToString(System.Globalization.CultureInfo.InvariantCulture) };
    private static double ReadDouble(Dictionary<string, string> values, string key) =>
        double.Parse(values[key], System.Globalization.CultureInfo.InvariantCulture);
    private static int ReadInt(Dictionary<string, string> values, string key) =>
        int.Parse(values[key], System.Globalization.CultureInfo.InvariantCulture);
    private static GoalView ToView(GoalEntity x) => new(x.Id, x.Title, x.Description,
        Enum.Parse<GoalStatus>(x.Status), x.CreatedAtUtc, x.CompletedAtUtc, x.Version);
    private static DeviationProfileView ToView(DeviationProfileEntity x) => new(x.Id, x.Name,
        x.Deviations.OrderBy(d => d.SortOrder).Select(d => new DeviationView(d.Id, d.Description,
            Enum.Parse<VisualObservability>(d.Observability))).ToArray(), x.CreatedAtUtc, x.UpdatedAtUtc, x.Version);
    private static SessionContractView ToView(ContractEntity x) => new(x.Id, x.GoalId, x.GoalTitle,
        x.GoalDescription, TimeSpan.FromTicks(x.TargetFocusTicks),
        x.Breaks.OrderBy(b => b.SortOrder).Select(b => new ScheduledBreakInput(
            TimeSpan.FromTicks(b.ActiveFocusOffsetTicks), TimeSpan.FromTicks(b.DurationTicks))).ToArray(),
        x.DeviationProfileId, x.DeviationProfileName,
        x.Deviations.OrderBy(d => d.SortOrder).Select(d => new DeviationView(d.DeviationId, d.Description,
            Enum.Parse<VisualObservability>(d.Observability))).ToArray(),
        Enum.Parse<ReasoningMode>(x.ReasoningMode), Enum.Parse<Sensitivity>(x.Sensitivity), x.ConfirmedAtUtc);
    private static SessionSetupView ToView(SessionSetupEntity x) => new(x.Id,
        Enum.Parse<SessionSetupStatus>(x.Status), ToView(x.Contract), x.CreatedAtUtc, x.Version);
}
