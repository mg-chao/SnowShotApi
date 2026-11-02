using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SnowShot.Infrastructure.Production.Persistence.Migrations
{
    /// <inheritdoc />
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1861:Avoid constant arrays as arguments", Justification = "EF-generated one-time schema migration uses declarative index column arrays.")]
    public partial class AddAccountsAndVirtualPayments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AccountId",
                schema: "snowshot",
                table: "usage_operations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ReservedFreeNanoYuan",
                schema: "snowshot",
                table: "usage_operations",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "ReservedWalletNanoYuan",
                schema: "snowshot",
                table: "usage_operations",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "account_captchas",
                schema: "snowshot",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AnswerHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    ClientKey = table.Column<string>(type: "text", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Consumed = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_account_captchas", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "account_platform_events",
                schema: "snowshot",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    EventName = table.Column<string>(type: "text", nullable: false),
                    PayloadEncrypted = table.Column<string>(type: "text", nullable: false),
                    State = table.Column<string>(type: "text", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_account_platform_events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "account_throttles",
                schema: "snowshot",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    PeriodStart = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_account_throttles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "accounts",
                schema: "snowshot",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Username = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    NormalizedUsername = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ActivatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_accounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "account_financial_entries",
                schema: "snowshot",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    AmountNanoYuan = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    OrderId = table.Column<string>(type: "text", nullable: true),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_account_financial_entries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_account_financial_entries_accounts_AccountId",
                        column: x => x.AccountId,
                        principalSchema: "snowshot",
                        principalTable: "accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "account_payer_claims",
                schema: "snowshot",
                columns: table => new
                {
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    OpenId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_account_payer_claims", x => x.AccountId);
                    table.ForeignKey(
                        name: "FK_account_payer_claims_accounts_AccountId",
                        column: x => x.AccountId,
                        principalSchema: "snowshot",
                        principalTable: "accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "account_purchase_requests",
                schema: "snowshot",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Purpose = table.Column<string>(type: "text", nullable: false),
                    State = table.Column<string>(type: "text", nullable: false),
                    ProofHash = table.Column<byte[]>(type: "bytea", nullable: true),
                    CodeImage = table.Column<byte[]>(type: "bytea", nullable: true),
                    GeneratedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OrderId = table.Column<string>(type: "text", nullable: true),
                    FulfilledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    BootstrapSessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    BootstrapTokensEncrypted = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_account_purchase_requests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_account_purchase_requests_accounts_AccountId",
                        column: x => x.AccountId,
                        principalSchema: "snowshot",
                        principalTable: "accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "account_sessions",
                schema: "snowshot",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    OpenId = table.Column<string>(type: "text", nullable: true),
                    WechatSessionKeyEncrypted = table.Column<string>(type: "text", nullable: true),
                    AccessHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    RefreshHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    AccessExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RefreshExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_account_sessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_account_sessions_accounts_AccountId",
                        column: x => x.AccountId,
                        principalSchema: "snowshot",
                        principalTable: "accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "account_usage_deduplications",
                schema: "snowshot",
                columns: table => new
                {
                    Hash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_account_usage_deduplications", x => x.Hash);
                    table.ForeignKey(
                        name: "FK_account_usage_deduplications_accounts_AccountId",
                        column: x => x.AccountId,
                        principalSchema: "snowshot",
                        principalTable: "accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "account_username_claims",
                schema: "snowshot",
                columns: table => new
                {
                    NormalizedUsername = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_account_username_claims", x => x.NormalizedUsername);
                    table.ForeignKey(
                        name: "FK_account_username_claims_accounts_AccountId",
                        column: x => x.AccountId,
                        principalSchema: "snowshot",
                        principalTable: "accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "account_wallets",
                schema: "snowshot",
                columns: table => new
                {
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    AvailableNanoYuan = table.Column<long>(type: "bigint", nullable: false),
                    HeldNanoYuan = table.Column<long>(type: "bigint", nullable: false),
                    OutstandingNanoYuan = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_account_wallets", x => x.AccountId);
                    table.CheckConstraint("ck_account_wallet_nonnegative", "\"AvailableNanoYuan\" >= 0 AND \"HeldNanoYuan\" >= 0 AND \"OutstandingNanoYuan\" >= 0");
                    table.ForeignKey(
                        name: "FK_account_wallets_accounts_AccountId",
                        column: x => x.AccountId,
                        principalSchema: "snowshot",
                        principalTable: "accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "account_wechat_links",
                schema: "snowshot",
                columns: table => new
                {
                    OpenId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_account_wechat_links", x => x.OpenId);
                    table.ForeignKey(
                        name: "FK_account_wechat_links_accounts_AccountId",
                        column: x => x.AccountId,
                        principalSchema: "snowshot",
                        principalTable: "accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "account_payment_orders",
                schema: "snowshot",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    OpenId = table.Column<string>(type: "text", nullable: false),
                    PackageId = table.Column<string>(type: "text", nullable: false),
                    PriceFen = table.Column<int>(type: "integer", nullable: false),
                    PaidFen = table.Column<int>(type: "integer", nullable: true),
                    State = table.Column<string>(type: "text", nullable: false),
                    PlatformTransactionId = table.Column<string>(type: "text", nullable: true),
                    PaymentParametersEncrypted = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    VerifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastQueriedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Fulfilled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_account_payment_orders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_account_payment_orders_account_purchase_requests_RequestId",
                        column: x => x.RequestId,
                        principalSchema: "snowshot",
                        principalTable: "account_purchase_requests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "account_refunds",
                schema: "snowshot",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    OrderId = table.Column<string>(type: "text", nullable: false),
                    RefundFen = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<string>(type: "text", nullable: false),
                    Applied = table.Column<bool>(type: "boolean", nullable: false),
                    SuccessNotified = table.Column<bool>(type: "boolean", nullable: false),
                    PlatformRefundId = table.Column<string>(type: "text", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_account_refunds", x => x.Id);
                    table.ForeignKey(
                        name: "FK_account_refunds_account_payment_orders_OrderId",
                        column: x => x.OrderId,
                        principalSchema: "snowshot",
                        principalTable: "account_payment_orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_usage_operations_AccountId",
                schema: "snowshot",
                table: "usage_operations",
                column: "AccountId");

            migrationBuilder.AddCheckConstraint(
                name: "ck_account_usage_funding",
                schema: "snowshot",
                table: "usage_operations",
                sql: "\"ReservedFreeNanoYuan\" >= 0 AND \"ReservedWalletNanoYuan\" >= 0 AND (\"AccountId\" IS NULL OR \"ReservedFreeNanoYuan\" + \"ReservedWalletNanoYuan\" = \"ReservedPublicNanoYuan\")");

            migrationBuilder.CreateIndex(
                name: "IX_account_captchas_ExpiresAt",
                schema: "snowshot",
                table: "account_captchas",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_account_financial_entries_AccountId_OccurredAt_Id",
                schema: "snowshot",
                table: "account_financial_entries",
                columns: new[] { "AccountId", "OccurredAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_account_payer_claims_OpenId",
                schema: "snowshot",
                table: "account_payer_claims",
                column: "OpenId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_account_payment_orders_PlatformTransactionId",
                schema: "snowshot",
                table: "account_payment_orders",
                column: "PlatformTransactionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_account_payment_orders_RequestId",
                schema: "snowshot",
                table: "account_payment_orders",
                column: "RequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_account_payment_orders_State_CreatedAt",
                schema: "snowshot",
                table: "account_payment_orders",
                columns: new[] { "State", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_account_platform_events_State_ReceivedAt",
                schema: "snowshot",
                table: "account_platform_events",
                columns: new[] { "State", "ReceivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_account_purchase_requests_AccountId",
                schema: "snowshot",
                table: "account_purchase_requests",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_account_purchase_requests_State_ExpiresAt",
                schema: "snowshot",
                table: "account_purchase_requests",
                columns: new[] { "State", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_account_refunds_OrderId",
                schema: "snowshot",
                table: "account_refunds",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_account_sessions_AccessHash",
                schema: "snowshot",
                table: "account_sessions",
                column: "AccessHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_account_sessions_AccountId",
                schema: "snowshot",
                table: "account_sessions",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_account_sessions_RefreshExpiresAt",
                schema: "snowshot",
                table: "account_sessions",
                column: "RefreshExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_account_sessions_RefreshHash",
                schema: "snowshot",
                table: "account_sessions",
                column: "RefreshHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_account_usage_deduplications_AccountId_OperationId",
                schema: "snowshot",
                table: "account_usage_deduplications",
                columns: new[] { "AccountId", "OperationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_account_username_claims_AccountId",
                schema: "snowshot",
                table: "account_username_claims",
                column: "AccountId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_account_wechat_links_AccountId",
                schema: "snowshot",
                table: "account_wechat_links",
                column: "AccountId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_usage_operations_accounts_AccountId",
                schema: "snowshot",
                table: "usage_operations",
                column: "AccountId",
                principalSchema: "snowshot",
                principalTable: "accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_usage_operations_accounts_AccountId",
                schema: "snowshot",
                table: "usage_operations");

            migrationBuilder.DropTable(
                name: "account_captchas",
                schema: "snowshot");

            migrationBuilder.DropTable(
                name: "account_financial_entries",
                schema: "snowshot");

            migrationBuilder.DropTable(
                name: "account_payer_claims",
                schema: "snowshot");

            migrationBuilder.DropTable(
                name: "account_platform_events",
                schema: "snowshot");

            migrationBuilder.DropTable(
                name: "account_refunds",
                schema: "snowshot");

            migrationBuilder.DropTable(
                name: "account_sessions",
                schema: "snowshot");

            migrationBuilder.DropTable(
                name: "account_throttles",
                schema: "snowshot");

            migrationBuilder.DropTable(
                name: "account_usage_deduplications",
                schema: "snowshot");

            migrationBuilder.DropTable(
                name: "account_username_claims",
                schema: "snowshot");

            migrationBuilder.DropTable(
                name: "account_wallets",
                schema: "snowshot");

            migrationBuilder.DropTable(
                name: "account_wechat_links",
                schema: "snowshot");

            migrationBuilder.DropTable(
                name: "account_payment_orders",
                schema: "snowshot");

            migrationBuilder.DropTable(
                name: "account_purchase_requests",
                schema: "snowshot");

            migrationBuilder.DropTable(
                name: "accounts",
                schema: "snowshot");

            migrationBuilder.DropIndex(
                name: "IX_usage_operations_AccountId",
                schema: "snowshot",
                table: "usage_operations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_account_usage_funding",
                schema: "snowshot",
                table: "usage_operations");

            migrationBuilder.DropColumn(
                name: "AccountId",
                schema: "snowshot",
                table: "usage_operations");

            migrationBuilder.DropColumn(
                name: "ReservedFreeNanoYuan",
                schema: "snowshot",
                table: "usage_operations");

            migrationBuilder.DropColumn(
                name: "ReservedWalletNanoYuan",
                schema: "snowshot",
                table: "usage_operations");
        }
    }
}
