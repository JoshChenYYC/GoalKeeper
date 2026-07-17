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
        modelBuilder.Entity<SnapshotEntity>().ToTable("Snapshots").HasIndex(x => new { x.SessionId, x.Sequence }).IsUnique();
        modelBuilder.Entity<ObservationEntity>().ToTable("Observations").HasIndex(x => x.SnapshotId).IsUnique();
        modelBuilder.Entity<ReasoningEvaluationEntity>().ToTable("ReasoningEvaluations");
        modelBuilder.Entity<EvidenceEpisodeEntity>().ToTable("EvidenceEpisodes");
        modelBuilder.Entity<InterventionEntity>().ToTable("Interventions");
        modelBuilder.Entity<RecoveryTurnEntity>().ToTable("RecoveryTurns");
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
