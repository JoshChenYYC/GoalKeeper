using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GoalKeeper.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RuntimePersistenceFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "CapturedAtMonotonicTicks",
                table: "Snapshots",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<bool>(
                name: "Accepted",
                table: "ReasoningEvaluations",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "EvidenceEpisodeId",
                table: "ReasoningEvaluations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RejectionReason",
                table: "ReasoningEvaluations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SessionVersion",
                table: "Observations",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "ActiveSlot",
                table: "FocusSessions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ProjectedEndUtc",
                table: "FocusSessions",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<string>(
                name: "RuntimeSnapshotJson",
                table: "FocusSessions",
                type: "TEXT",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.Sql(
                """
                UPDATE "Snapshots"
                SET "ProcessingStatus" =
                    CASE lower("ProcessingStatus")
                        WHEN 'captured' THEN 'captured'
                        WHEN 'superseded' THEN 'superseded'
                        WHEN 'observed' THEN 'observed'
                        WHEN 'stale' THEN 'stale'
                        WHEN 'agenterror' THEN 'agent_error'
                        WHEN 'agent_error' THEN 'agent_error'
                        ELSE 'agent_error'
                    END;
                """);
            migrationBuilder.Sql(
                """
                UPDATE "Observations"
                SET "SessionVersion" = COALESCE(
                    (SELECT "SessionVersion"
                     FROM "Snapshots"
                     WHERE "Snapshots"."Id" = "Observations"."SnapshotId"),
                    1);
                """);
            migrationBuilder.Sql(
                """
                UPDATE "ReasoningEvaluations"
                SET "Accepted" = 1;
                """);
            migrationBuilder.Sql(
                """
                UPDATE "FocusSessions"
                SET "ProjectedEndUtc" = "StartedAtUtc",
                    "RuntimeSnapshotJson" = '{}',
                    "ActiveSlot" =
                        CASE
                            WHEN "State" IN ('Fulfilled', 'EndedEarly') THEN NULL
                            ELSE 1
                        END;
                """);

            migrationBuilder.CreateTable(
                name: "EvidenceObservationReferences",
                columns: table => new
                {
                    EvidenceEpisodeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ObservationId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvidenceObservationReferences", x => new { x.EvidenceEpisodeId, x.Sequence });
                    table.ForeignKey(
                        name: "FK_EvidenceObservationReferences_EvidenceEpisodes_EvidenceEpisodeId",
                        column: x => x.EvidenceEpisodeId,
                        principalTable: "EvidenceEpisodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_EvidenceObservationReferences_Observations_ObservationId",
                        column: x => x.ObservationId,
                        principalTable: "Observations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RecoveryTurns_InterventionId_TurnNumber",
                table: "RecoveryTurns",
                columns: new[] { "InterventionId", "TurnNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RecoveryTurns_SessionId",
                table: "RecoveryTurns",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_ReasoningEvaluations_EvidenceEpisodeId",
                table: "ReasoningEvaluations",
                column: "EvidenceEpisodeId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReasoningEvaluations_SessionId",
                table: "ReasoningEvaluations",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_Observations_SessionId",
                table: "Observations",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_Interventions_EvaluationId",
                table: "Interventions",
                column: "EvaluationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Interventions_EvidenceEpisodeId",
                table: "Interventions",
                column: "EvidenceEpisodeId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Interventions_SessionId",
                table: "Interventions",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_FocusSessions_ActiveSlot",
                table: "FocusSessions",
                column: "ActiveSlot",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EvidenceEpisodes_SessionId",
                table: "EvidenceEpisodes",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_DeviationOverrides_SessionId",
                table: "DeviationOverrides",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_SessionId",
                table: "AuditEvents",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_EvidenceObservationReferences_EvidenceEpisodeId_ObservationId",
                table: "EvidenceObservationReferences",
                columns: new[] { "EvidenceEpisodeId", "ObservationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EvidenceObservationReferences_ObservationId",
                table: "EvidenceObservationReferences",
                column: "ObservationId");

            migrationBuilder.AddForeignKey(
                name: "FK_AuditEvents_FocusSessions_SessionId",
                table: "AuditEvents",
                column: "SessionId",
                principalTable: "FocusSessions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_DeviationOverrides_FocusSessions_SessionId",
                table: "DeviationOverrides",
                column: "SessionId",
                principalTable: "FocusSessions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_EvidenceEpisodes_FocusSessions_SessionId",
                table: "EvidenceEpisodes",
                column: "SessionId",
                principalTable: "FocusSessions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Interventions_EvidenceEpisodes_EvidenceEpisodeId",
                table: "Interventions",
                column: "EvidenceEpisodeId",
                principalTable: "EvidenceEpisodes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Interventions_FocusSessions_SessionId",
                table: "Interventions",
                column: "SessionId",
                principalTable: "FocusSessions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Interventions_ReasoningEvaluations_EvaluationId",
                table: "Interventions",
                column: "EvaluationId",
                principalTable: "ReasoningEvaluations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Observations_FocusSessions_SessionId",
                table: "Observations",
                column: "SessionId",
                principalTable: "FocusSessions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Observations_Snapshots_SnapshotId",
                table: "Observations",
                column: "SnapshotId",
                principalTable: "Snapshots",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ReasoningEvaluations_EvidenceEpisodes_EvidenceEpisodeId",
                table: "ReasoningEvaluations",
                column: "EvidenceEpisodeId",
                principalTable: "EvidenceEpisodes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ReasoningEvaluations_FocusSessions_SessionId",
                table: "ReasoningEvaluations",
                column: "SessionId",
                principalTable: "FocusSessions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_RecoveryTurns_FocusSessions_SessionId",
                table: "RecoveryTurns",
                column: "SessionId",
                principalTable: "FocusSessions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_RecoveryTurns_Interventions_InterventionId",
                table: "RecoveryTurns",
                column: "InterventionId",
                principalTable: "Interventions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_SessionReviews_FocusSessions_SessionId",
                table: "SessionReviews",
                column: "SessionId",
                principalTable: "FocusSessions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AuditEvents_FocusSessions_SessionId",
                table: "AuditEvents");

            migrationBuilder.DropForeignKey(
                name: "FK_DeviationOverrides_FocusSessions_SessionId",
                table: "DeviationOverrides");

            migrationBuilder.DropForeignKey(
                name: "FK_EvidenceEpisodes_FocusSessions_SessionId",
                table: "EvidenceEpisodes");

            migrationBuilder.DropForeignKey(
                name: "FK_Interventions_EvidenceEpisodes_EvidenceEpisodeId",
                table: "Interventions");

            migrationBuilder.DropForeignKey(
                name: "FK_Interventions_FocusSessions_SessionId",
                table: "Interventions");

            migrationBuilder.DropForeignKey(
                name: "FK_Interventions_ReasoningEvaluations_EvaluationId",
                table: "Interventions");

            migrationBuilder.DropForeignKey(
                name: "FK_Observations_FocusSessions_SessionId",
                table: "Observations");

            migrationBuilder.DropForeignKey(
                name: "FK_Observations_Snapshots_SnapshotId",
                table: "Observations");

            migrationBuilder.DropForeignKey(
                name: "FK_ReasoningEvaluations_EvidenceEpisodes_EvidenceEpisodeId",
                table: "ReasoningEvaluations");

            migrationBuilder.DropForeignKey(
                name: "FK_ReasoningEvaluations_FocusSessions_SessionId",
                table: "ReasoningEvaluations");

            migrationBuilder.DropForeignKey(
                name: "FK_RecoveryTurns_FocusSessions_SessionId",
                table: "RecoveryTurns");

            migrationBuilder.DropForeignKey(
                name: "FK_RecoveryTurns_Interventions_InterventionId",
                table: "RecoveryTurns");

            migrationBuilder.DropForeignKey(
                name: "FK_SessionReviews_FocusSessions_SessionId",
                table: "SessionReviews");

            migrationBuilder.DropTable(
                name: "EvidenceObservationReferences");

            migrationBuilder.DropIndex(
                name: "IX_RecoveryTurns_InterventionId_TurnNumber",
                table: "RecoveryTurns");

            migrationBuilder.DropIndex(
                name: "IX_RecoveryTurns_SessionId",
                table: "RecoveryTurns");

            migrationBuilder.DropIndex(
                name: "IX_ReasoningEvaluations_EvidenceEpisodeId",
                table: "ReasoningEvaluations");

            migrationBuilder.DropIndex(
                name: "IX_ReasoningEvaluations_SessionId",
                table: "ReasoningEvaluations");

            migrationBuilder.DropIndex(
                name: "IX_Observations_SessionId",
                table: "Observations");

            migrationBuilder.DropIndex(
                name: "IX_Interventions_EvaluationId",
                table: "Interventions");

            migrationBuilder.DropIndex(
                name: "IX_Interventions_EvidenceEpisodeId",
                table: "Interventions");

            migrationBuilder.DropIndex(
                name: "IX_Interventions_SessionId",
                table: "Interventions");

            migrationBuilder.DropIndex(
                name: "IX_FocusSessions_ActiveSlot",
                table: "FocusSessions");

            migrationBuilder.DropIndex(
                name: "IX_EvidenceEpisodes_SessionId",
                table: "EvidenceEpisodes");

            migrationBuilder.DropIndex(
                name: "IX_DeviationOverrides_SessionId",
                table: "DeviationOverrides");

            migrationBuilder.DropIndex(
                name: "IX_AuditEvents_SessionId",
                table: "AuditEvents");

            migrationBuilder.DropColumn(
                name: "CapturedAtMonotonicTicks",
                table: "Snapshots");

            migrationBuilder.DropColumn(
                name: "Accepted",
                table: "ReasoningEvaluations");

            migrationBuilder.DropColumn(
                name: "EvidenceEpisodeId",
                table: "ReasoningEvaluations");

            migrationBuilder.DropColumn(
                name: "RejectionReason",
                table: "ReasoningEvaluations");

            migrationBuilder.DropColumn(
                name: "SessionVersion",
                table: "Observations");

            migrationBuilder.DropColumn(
                name: "ActiveSlot",
                table: "FocusSessions");

            migrationBuilder.DropColumn(
                name: "ProjectedEndUtc",
                table: "FocusSessions");

            migrationBuilder.DropColumn(
                name: "RuntimeSnapshotJson",
                table: "FocusSessions");
        }

    }
}
