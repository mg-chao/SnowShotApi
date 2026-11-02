\set ON_ERROR_STOP on

SELECT concat_ws('|',
    (SELECT count(*) FROM snowshot.principals),
    (SELECT count(*) FROM snowshot.principal_fingerprints),
    (SELECT count(*) FROM snowshot.usage_operations),
    (SELECT count(*) FROM snowshot.provider_attempts),
    (SELECT count(*) FROM snowshot.usage_events),
    (SELECT sum("Requests") FROM snowshot.daily_aggregates),
    (SELECT sum("CommittedNanoYuan") FROM snowshot.allowance_periods),
    (SELECT sum("ReservedNanoYuan") FROM snowshot.allowance_periods),
    (SELECT sum("CommittedNanoYuan") FROM snowshot.operator_budget_periods),
    (SELECT sum("ReservedNanoYuan") FROM snowshot.operator_budget_periods),
    (SELECT count(*) FROM snowshot.policy_revisions),
    (SELECT "ActiveRevision" FROM snowshot.policy_state WHERE "Id" = 1),
    (SELECT count(*) FROM snowshot."__EFMigrationsHistory"));
