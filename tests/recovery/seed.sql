\set ON_ERROR_STOP on

INSERT INTO snowshot.policy_revisions
    ("Revision", "Fingerprint", "CanonicalDocument", "PrincipalDailyAllowanceNanoYuan",
     "DailyOperatorBudgetNanoYuan", "MonthlyOperatorBudgetNanoYuan", "ActivatedAt")
VALUES (1, decode(repeat('04', 32), 'hex'), 'recovery-policy', 1000, 10000, 100000, clock_timestamp());

INSERT INTO snowshot.policy_state ("Id", "ActiveRevision", "UpdatedAt")
VALUES (1, 1, clock_timestamp());

INSERT INTO snowshot.principals ("Id", "CreatedAt")
VALUES ('0198a56e-8f5f-7b3f-8cf1-e80d0547ca01', clock_timestamp());

INSERT INTO snowshot.principal_fingerprints ("Fingerprint", "PrincipalId", "CreatedAt", "LastSeenAt")
VALUES (decode(repeat('01', 32), 'hex'), '0198a56e-8f5f-7b3f-8cf1-e80d0547ca01', clock_timestamp(), clock_timestamp());

INSERT INTO snowshot.allowance_periods
    ("PrincipalId", "PeriodDate", "LimitNanoYuan", "CommittedNanoYuan", "ReservedNanoYuan", "AppliedPolicyRevision", "UpdatedAt")
VALUES ('0198a56e-8f5f-7b3f-8cf1-e80d0547ca01', CURRENT_DATE, 1000, 10, 0, 1, clock_timestamp());

INSERT INTO snowshot.operator_budget_periods
    ("Kind", "PeriodKey", "LimitNanoYuan", "CommittedNanoYuan", "ReservedNanoYuan", "AppliedPolicyRevision", "UpdatedAt")
VALUES
    (0, to_char(CURRENT_DATE, 'YYYYMMDD'), 10000, 10, 0, 1, clock_timestamp()),
    (1, to_char(CURRENT_DATE, 'YYYYMM'), 100000, 10, 0, 1, clock_timestamp());

INSERT INTO snowshot.usage_operations
    ("Id", "PrincipalId", "AllowanceDate", "Kind", "Resource", "IdempotencyHash", "OwnerToken", "Fence",
     "PolicyFingerprint", "PolicyRevision", "InputRateNanoYuan", "OutputRateNanoYuan", "AllowanceLimitNanoYuan",
     "ReservedPublicNanoYuan", "ReservedOperatorNanoYuan", "ActualPublicNanoYuan", "ActualOperatorNanoYuan",
     "OperatorOverageNanoYuan", "State", "CreatedAt", "AbsoluteDeadline", "LeaseExpiresAt", "DispatchedAt",
     "SettledAt", "SettlementFingerprint")
VALUES
    ('0198a56e-8f5f-7b3f-8cf1-e80d0547ca02', '0198a56e-8f5f-7b3f-8cf1-e80d0547ca01', CURRENT_DATE,
     1, 'qwen-flash', decode(repeat('02', 32), 'hex'), decode(repeat('03', 32), 'hex'), 1,
     decode(repeat('04', 32), 'hex'), 1, 1, 1, 1000, 100, 100, 10, 10, 0, 2,
     clock_timestamp() - interval '2 minutes', clock_timestamp() + interval '3 minutes',
     clock_timestamp() + interval '1 minute', clock_timestamp() - interval '1 minute', clock_timestamp(),
     decode(repeat('05', 32), 'hex'));

INSERT INTO snowshot.provider_attempts
    ("Id", "OperationId", "AttemptNumber", "Provider", "Resource", "State", "DispatchState", "Outcome",
     "HttpStatus", "InputUnits", "OutputUnits", "CostNanoYuan", "CostKnown", "StartedAt", "CompletedAt")
VALUES
    ('0198a56e-8f5f-7b3f-8cf1-e80d0547ca03', '0198a56e-8f5f-7b3f-8cf1-e80d0547ca02', 1,
     'recovery-provider', 'qwen-flash', 1, 2, 'success', 200, 5, 5, 10, true,
     clock_timestamp() - interval '1 minute', clock_timestamp());

INSERT INTO snowshot.usage_events
    ("OperationId", "PrincipalId", "Kind", "Resource", "Outcome", "InputUnits", "OutputUnits",
     "PublicCostNanoYuan", "OperatorCostNanoYuan", "OperatorOverageNanoYuan", "CostKnown", "OccurredAt")
VALUES
    ('0198a56e-8f5f-7b3f-8cf1-e80d0547ca02', '0198a56e-8f5f-7b3f-8cf1-e80d0547ca01', 1,
     'qwen-flash', 'success', 5, 5, 10, 10, 0, true, clock_timestamp());

INSERT INTO snowshot.daily_aggregates
    ("UsageDate", "Kind", "Resource", "Requests", "UnknownCostRequests", "InputUnits", "OutputUnits",
     "PublicCostNanoYuan", "OperatorCostNanoYuan", "OperatorOverageNanoYuan", "UpdatedAt")
VALUES (CURRENT_DATE, 1, 'qwen-flash', 1, 0, 5, 5, 10, 10, 0, clock_timestamp());
