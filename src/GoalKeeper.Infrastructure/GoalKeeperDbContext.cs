using Microsoft.EntityFrameworkCore;

namespace GoalKeeper.Infrastructure;

public sealed class GoalKeeperDbContext(DbContextOptions<GoalKeeperDbContext> options) : DbContext(options)
{
    public DbSet<GoalEntity> Goals => Set<GoalEntity>();
    public DbSet<DeviationProfileEntity> DeviationProfiles => Set<DeviationProfileEntity>();
    public DbSet<DeviationEntity> Deviations => Set<DeviationEntity>();
    public DbSet<ContractEntity> SessionContracts => Set<ContractEntity>();
    public DbSet<ContractBreakEntity> ContractBreaks => Set<ContractBreakEntity>();
    public DbSet<ContractDeviationEntity> ContractDeviations => Set<ContractDeviationEntity>();
    public DbSet<SessionSetupEntity> SessionSetups => Set<SessionSetupEntity>();
    public DbSet<FocusSessionEntity> FocusSessions => Set<FocusSessionEntity>();
    public DbSet<SnapshotEntity> Snapshots => Set<SnapshotEntity>();
    public DbSet<ObservationEntity> Observations => Set<ObservationEntity>();
    public DbSet<ReasoningEvaluationEntity> ReasoningEvaluations => Set<ReasoningEvaluationEntity>();
    public DbSet<EvidenceEpisodeEntity> EvidenceEpisodes => Set<EvidenceEpisodeEntity>();
    public DbSet<EvidenceObservationReferenceEntity> EvidenceObservationReferences =>
        Set<EvidenceObservationReferenceEntity>();
    public DbSet<InterventionEntity> Interventions => Set<InterventionEntity>();
    public DbSet<RecoveryTurnEntity> RecoveryTurns => Set<RecoveryTurnEntity>();
    public DbSet<DeviationOverrideEntity> DeviationOverrides => Set<DeviationOverrideEntity>();
    public DbSet<SessionReviewEntity> SessionReviews => Set<SessionReviewEntity>();
    public DbSet<AuditEventEntity> AuditEvents => Set<AuditEventEntity>();
    public DbSet<ApplicationSettingEntity> ApplicationSettings => Set<ApplicationSettingEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GoalEntity>().ToTable("Goals").Property(x => x.Version).IsConcurrencyToken();
        modelBuilder.Entity<DeviationProfileEntity>().ToTable("DeviationProfiles");
        modelBuilder.Entity<DeviationEntity>().ToTable("Deviations");
        modelBuilder.Entity<ContractEntity>().ToTable("SessionContracts");
        modelBuilder.Entity<ContractBreakEntity>().ToTable("ContractBreaks");
        modelBuilder.Entity<ContractDeviationEntity>().ToTable("ContractDeviations");
        modelBuilder.Entity<SessionSetupEntity>().ToTable("SessionSetups").Property(x => x.Version).IsConcurrencyToken();
        modelBuilder.Entity<SessionSetupEntity>().HasIndex(x => x.ContractId).IsUnique();
        modelBuilder.Entity<FocusSessionEntity>().ToTable("FocusSessions").Property(x => x.Version).IsConcurrencyToken();
        modelBuilder.Entity<FocusSessionEntity>().HasIndex(x => x.ContractId).IsUnique();
        modelBuilder.Entity<FocusSessionEntity>().HasIndex(x => x.ActiveSlot).IsUnique();
        modelBuilder.Entity<SnapshotEntity>().ToTable("Snapshots").HasIndex(x => new { x.SessionId, x.Sequence }).IsUnique();
        modelBuilder.Entity<ObservationEntity>().ToTable("Observations").HasIndex(x => x.SnapshotId).IsUnique();
        modelBuilder.Entity<ReasoningEvaluationEntity>().ToTable("ReasoningEvaluations");
        modelBuilder.Entity<EvidenceEpisodeEntity>().ToTable("EvidenceEpisodes");
        modelBuilder.Entity<EvidenceObservationReferenceEntity>().ToTable("EvidenceObservationReferences")
            .HasKey(x => new { x.EvidenceEpisodeId, x.Sequence });
        modelBuilder.Entity<EvidenceObservationReferenceEntity>()
            .HasIndex(x => new { x.EvidenceEpisodeId, x.ObservationId }).IsUnique();
        modelBuilder.Entity<InterventionEntity>().ToTable("Interventions");
        modelBuilder.Entity<RecoveryTurnEntity>().ToTable("RecoveryTurns");
        modelBuilder.Entity<RecoveryTurnEntity>()
            .HasIndex(x => new { x.InterventionId, x.TurnNumber }).IsUnique();
        modelBuilder.Entity<DeviationOverrideEntity>().ToTable("DeviationOverrides");
        modelBuilder.Entity<SessionReviewEntity>().ToTable("SessionReviews").HasIndex(x => x.SessionId).IsUnique();
        modelBuilder.Entity<AuditEventEntity>().ToTable("AuditEvents");
        modelBuilder.Entity<ApplicationSettingEntity>().ToTable("ApplicationSettings").HasKey(x => x.Key);

        modelBuilder.Entity<ContractEntity>()
            .HasOne(x => x.Goal).WithMany(x => x.Contracts).HasForeignKey(x => x.GoalId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<FocusSessionEntity>()
            .HasOne(x => x.Goal).WithMany(x => x.Sessions).HasForeignKey(x => x.GoalId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<FocusSessionEntity>()
            .HasOne(x => x.Contract).WithOne().HasForeignKey<FocusSessionEntity>(x => x.ContractId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<SessionSetupEntity>()
            .HasOne(x => x.Contract).WithOne(x => x.Setup)
            .HasForeignKey<SessionSetupEntity>(x => x.ContractId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<SnapshotEntity>()
            .HasOne(x => x.Session).WithMany(x => x.Snapshots)
            .HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ObservationEntity>()
            .HasOne(x => x.Session).WithMany(x => x.Observations)
            .HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ObservationEntity>()
            .HasOne(x => x.Snapshot).WithOne(x => x.Observation)
            .HasForeignKey<ObservationEntity>(x => x.SnapshotId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ReasoningEvaluationEntity>()
            .HasOne(x => x.Session).WithMany(x => x.ReasoningEvaluations)
            .HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ReasoningEvaluationEntity>()
            .HasOne(x => x.EvidenceEpisode).WithOne(x => x.Evaluation)
            .HasForeignKey<ReasoningEvaluationEntity>(x => x.EvidenceEpisodeId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<EvidenceEpisodeEntity>()
            .HasOne(x => x.Session).WithMany(x => x.EvidenceEpisodes)
            .HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<EvidenceObservationReferenceEntity>()
            .HasOne(x => x.EvidenceEpisode).WithMany(x => x.ObservationReferences)
            .HasForeignKey(x => x.EvidenceEpisodeId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<EvidenceObservationReferenceEntity>()
            .HasOne(x => x.Observation).WithMany(x => x.EvidenceReferences)
            .HasForeignKey(x => x.ObservationId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<InterventionEntity>()
            .HasOne(x => x.Session).WithMany(x => x.Interventions)
            .HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<InterventionEntity>()
            .HasOne(x => x.Evaluation).WithOne(x => x.Intervention)
            .HasForeignKey<InterventionEntity>(x => x.EvaluationId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<InterventionEntity>()
            .HasOne(x => x.EvidenceEpisode).WithOne(x => x.Intervention)
            .HasForeignKey<InterventionEntity>(x => x.EvidenceEpisodeId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<RecoveryTurnEntity>()
            .HasOne(x => x.Session).WithMany(x => x.RecoveryTurns)
            .HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<RecoveryTurnEntity>()
            .HasOne(x => x.Intervention).WithMany(x => x.RecoveryTurns)
            .HasForeignKey(x => x.InterventionId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<DeviationOverrideEntity>()
            .HasOne(x => x.Session).WithMany(x => x.DeviationOverrides)
            .HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<SessionReviewEntity>()
            .HasOne(x => x.Session).WithOne(x => x.Review)
            .HasForeignKey<SessionReviewEntity>(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<AuditEventEntity>()
            .HasOne(x => x.Session).WithMany(x => x.AuditEvents)
            .HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties().Where(x => x.ClrType == typeof(DateTimeOffset)))
            {
                property.SetColumnType("TEXT");
            }
            foreach (var property in entityType.GetProperties().Where(x => x.ClrType == typeof(DateTimeOffset?)))
            {
                property.SetColumnType("TEXT");
            }
        }
    }
}

public sealed class GoalKeeperDbContextFactory(DbContextOptions<GoalKeeperDbContext> options)
    : IDbContextFactory<GoalKeeperDbContext>
{
    public GoalKeeperDbContext CreateDbContext() => new(options);

    public Task<GoalKeeperDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(CreateDbContext());
}
