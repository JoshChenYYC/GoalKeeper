using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GoalKeeper.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RuntimeSameSessionGuards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            CreateSameSessionTrigger(
                migrationBuilder,
                "Observations",
                """
                (SELECT "SessionId" FROM "Snapshots" WHERE "Id" = NEW."SnapshotId")
                    <> NEW."SessionId"
                """);
            CreateSameSessionTrigger(
                migrationBuilder,
                "ReasoningEvaluations",
                """
                NEW."EvidenceEpisodeId" IS NOT NULL AND
                (SELECT "SessionId" FROM "EvidenceEpisodes" WHERE "Id" = NEW."EvidenceEpisodeId")
                    <> NEW."SessionId"
                """);
            CreateSameSessionTrigger(
                migrationBuilder,
                "EvidenceObservationReferences",
                """
                (SELECT "SessionId" FROM "EvidenceEpisodes" WHERE "Id" = NEW."EvidenceEpisodeId")
                    <> NEW."SessionId"
                OR
                (SELECT "SessionId" FROM "Observations" WHERE "Id" = NEW."ObservationId")
                    <> NEW."SessionId"
                """);
            CreateSameSessionTrigger(
                migrationBuilder,
                "Interventions",
                """
                (SELECT "SessionId" FROM "ReasoningEvaluations" WHERE "Id" = NEW."EvaluationId")
                    <> NEW."SessionId"
                OR
                (SELECT "SessionId" FROM "EvidenceEpisodes" WHERE "Id" = NEW."EvidenceEpisodeId")
                    <> NEW."SessionId"
                """);
            CreateSameSessionTrigger(
                migrationBuilder,
                "RecoveryTurns",
                """
                (SELECT "SessionId" FROM "Interventions" WHERE "Id" = NEW."InterventionId")
                    <> NEW."SessionId"
                """);
            CreateSameSessionTrigger(
                migrationBuilder,
                "FocusSessions",
                """
                (SELECT "GoalId" FROM "SessionContracts" WHERE "Id" = NEW."ContractId")
                    <> NEW."GoalId"
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in new[]
                     {
                         "Observations",
                         "ReasoningEvaluations",
                         "EvidenceObservationReferences",
                         "Interventions",
                         "RecoveryTurns",
                         "FocusSessions"
                     })
            {
                foreach (var operation in new[] { "INSERT", "UPDATE" })
                {
                    migrationBuilder.Sql(
                        $"""DROP TRIGGER IF EXISTS "TR_{table}_SameSession_{operation}";""");
                }
            }
        }

        private static void CreateSameSessionTrigger(
            MigrationBuilder migrationBuilder,
            string table,
            string condition)
        {
            foreach (var operation in new[] { "INSERT", "UPDATE" })
            {
                var trigger = $"TR_{table}_SameSession_{operation}";
                migrationBuilder.Sql(
                    $"""
                     CREATE TRIGGER "{trigger}"
                     BEFORE {operation} ON "{table}"
                     FOR EACH ROW
                     WHEN {condition}
                     BEGIN
                         SELECT RAISE(ABORT, 'Cross-session reference rejected.');
                     END;
                     """);
            }
        }
    }
}
