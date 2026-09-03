using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Forge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "colonies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    demand_profile = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_colonies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "colony_orders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Colony = table.Column<Guid>(type: "uuid", nullable: false),
                    lines = table.Column<string>(type: "jsonb", nullable: false),
                    DeliveryWindowStart = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeliveryWindowEnd = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_colony_orders", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "dock_bays",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IsOpen = table.Column<bool>(type: "boolean", nullable: false),
                    schedule = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dock_bays", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "gel_lots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GelTypeId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProducedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    FefoPriority = table.Column<int>(type: "integer", nullable: false),
                    IsExpired = table.Column<bool>(type: "boolean", nullable: false),
                    AssignedZoneId = table.Column<Guid>(type: "uuid", nullable: true),
                    AtRisk = table.Column<bool>(type: "boolean", nullable: false),
                    temperature_history = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gel_lots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "gel_types",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Formulation = table.Column<string>(type: "jsonb", nullable: false),
                    Velocity = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gel_types", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "pick_faces",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Zone = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pick_faces", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "starships",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CargoCapacity = table.Column<int>(type: "integer", nullable: false),
                    Destination = table.Column<Guid>(type: "uuid", nullable: false),
                    LoadedQuantity = table.Column<int>(type: "integer", nullable: false),
                    loading_windows = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_starships", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "temperature_zones",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AllowableRange = table.Column<string>(type: "jsonb", nullable: false),
                    Capacity = table.Column<int>(type: "integer", nullable: false),
                    StoredQuantity = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_temperature_zones", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "warehouse_tasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "text", nullable: false),
                    origin = table.Column<string>(type: "jsonb", nullable: false),
                    destination = table.Column<string>(type: "jsonb", nullable: false),
                    EstimatedDuration = table.Column<TimeSpan>(type: "interval", nullable: false),
                    TravelTime = table.Column<TimeSpan>(type: "interval", nullable: false),
                    AssignedWorker = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_warehouse_tasks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "workers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    HourlyRate = table.Column<decimal>(type: "numeric", nullable: false),
                    shifts = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workers", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_gel_lots_GelTypeId_ExpiresAt",
                table: "gel_lots",
                columns: new[] { "GelTypeId", "ExpiresAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "colonies");

            migrationBuilder.DropTable(
                name: "colony_orders");

            migrationBuilder.DropTable(
                name: "dock_bays");

            migrationBuilder.DropTable(
                name: "gel_lots");

            migrationBuilder.DropTable(
                name: "gel_types");

            migrationBuilder.DropTable(
                name: "pick_faces");

            migrationBuilder.DropTable(
                name: "starships");

            migrationBuilder.DropTable(
                name: "temperature_zones");

            migrationBuilder.DropTable(
                name: "warehouse_tasks");

            migrationBuilder.DropTable(
                name: "workers");
        }
    }
}
